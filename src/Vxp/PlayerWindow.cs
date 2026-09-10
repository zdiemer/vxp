using Silk.NET.Maths;
using Silk.NET.SDL;
using Vxp.Config;
using Vxp.Emulation;
using Vxp.Format;
using Vxp.Input;
using Vxp.Ui;
using Vxp.Video;

namespace Vxp;

/// <summary>
/// SDL front end: window, texture upload, queued audio, input dispatch and the menus.
/// </summary>
/// <remarks>
/// <para>
/// Audio is queued rather than pulled from a callback, and each decoded frame is tagged
/// with the audio sample index it starts at. The picture shown is the newest frame whose
/// audio has actually reached the device, so video follows sound exactly instead of
/// running ahead by the depth of the audio buffer.
/// </para>
/// </remarks>
public sealed unsafe class PlayerWindow : IDisposable
{
    private const ushort AudioS16Lsb = 0x8010;
    private const int AxisThreshold = 16000;

    private readonly VideoNowPlayer _player;
    private readonly VxpSettings _settings;
    private readonly InputMap _input;
    private readonly DiscMap _discMap;

    private readonly Sdl _sdl;
    private readonly Silk.NET.SDL.Window* _window;
    private readonly Renderer* _renderer;
    private readonly uint _audioDevice;

    private readonly Canvas _canvas = new();
    private readonly MenuController _menu = new();
    private readonly StatusOverlay _overlay = new();
    private readonly PictureAdjustment _adjust;

    private readonly Queue<PendingFrame> _pending = new();
    private readonly object _pendingGate = new();
    private readonly short[] _mixBuffer = new short[2048];
    private readonly byte[] _displayRgba = new byte[VideoDecoder.RgbaFrameBytes];
    private readonly HashSet<int> _heldAxes = new();

    private Texture* _videoTexture;
    private Texture* _overlayTexture;
    private Texture* _effectTexture;
    private GameController* _controller;

    private byte[] _displayFrame;
    private long _samplesQueued;
    private double _measuredFps;
    private int _effectWidth;
    private int _effectHeight;
    private bool _fastForward;
    private bool _focused = true;
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("VXP_TRACE_INPUT") == "1";
    private bool _running = true;
    private bool _disposed;

    private readonly record struct PendingFrame(long StartSample, byte[] Rgba);

    /// <summary>Creates the window and opens the audio device.</summary>
    public PlayerWindow(VideoNowPlayer player, VxpSettings settings, InputMap input)
    {
        _player = player;
        _settings = settings;
        _input = input;
        _discMap = DiscMap.Build(player.Disc);
        _adjust = PictureAdjustment.FromSettings(settings.Video);
        _displayFrame = (byte[])player.Framebuffer.Clone();

        _sdl = Sdl.GetApi();
        if (_sdl.Init(Sdl.InitVideo | Sdl.InitAudio | Sdl.InitGamecontroller) != 0)
            throw new InvalidOperationException($"SDL_Init failed: {_sdl.GetErrorS()}");

        var scale = Math.Clamp(settings.Video.WindowScale, 1, 16);

        _window = _sdl.CreateWindow(
            "vxp",
            Sdl.WindowposCentered, Sdl.WindowposCentered,
            FrameLayout.Width * scale, FrameLayout.Height * scale,
            (uint)(WindowFlags.Shown | WindowFlags.Resizable));

        if (_window is null)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {_sdl.GetErrorS()}");

        var rendererFlags = (uint)RendererFlags.Accelerated;
        if (settings.Video.VSync) rendererFlags |= (uint)RendererFlags.Presentvsync;

        _renderer = _sdl.CreateRenderer(_window, -1, rendererFlags);
        if (_renderer is null) _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
        if (_renderer is null) throw new InvalidOperationException($"SDL_CreateRenderer failed: {_sdl.GetErrorS()}");

        CreateVideoTexture();

        var want = new AudioSpec
        {
            Freq = FrameLayout.AudioSampleRate,
            Format = AudioS16Lsb,
            Channels = 1,
            Samples = 1024,
        };

        AudioSpec have;
        _audioDevice = OpenAudio(&want, &have);
        if (_audioDevice == 0)
            throw new InvalidOperationException($"SDL_OpenAudioDevice failed: {_sdl.GetErrorS()}");

        _sdl.PauseAudioDevice(_audioDevice, 0);
        OpenController();

        _player.FrameDecoded += OnFrameDecoded;
        _menu.Changed += ApplySettings;

        ApplySettings();
        if (settings.Video.Fullscreen) SetFullscreen(true);
        if (settings.Emulation.AutoPlay) _player.Play();
    }

    private uint OpenAudio(AudioSpec* want, AudioSpec* have)
    {
        var device = _settings.Audio.Device;
        if (string.IsNullOrWhiteSpace(device)) return _sdl.OpenAudioDevice((byte*)null, 0, want, have, 0);

        var bytes = System.Text.Encoding.UTF8.GetBytes(device + '\0');
        fixed (byte* name = bytes) return _sdl.OpenAudioDevice(name, 0, want, have, 0);
    }

    /// <summary>Runs until the viewer quits.</summary>
    public void Run()
    {
        var lastFrameTime = _sdl.GetPerformanceCounter();
        var frequency = (double)_sdl.GetPerformanceFrequency();

        while (_running)
        {
            PumpEvents();
            TopUpAudio();
            PresentNewestReadyFrame();
            Render();

            var now = _sdl.GetPerformanceCounter();
            var elapsed = (now - lastFrameTime) / frequency;
            lastFrameTime = now;
            if (elapsed > 0) _measuredFps = _measuredFps * 0.9 + 1.0 / elapsed * 0.1;
        }
    }

    /// <summary>Pushes the current settings into the player and the renderer.</summary>
    private void ApplySettings()
    {
        _player.Navigation = _settings.Emulation.Navigation;
        _player.Timeout = _settings.Emulation.ChoiceTimeout;
        _player.Loop = _settings.Emulation.Loop;

        if (!_fastForward) _player.Speed = _settings.Emulation.SpeedPercent / 100.0;

        _adjust.Brightness = _settings.Video.Brightness;
        _adjust.Contrast = _settings.Video.Contrast;
        _adjust.Saturation = _settings.Video.Saturation;
        _adjust.Gamma = _settings.Video.Gamma;
        _adjust.SetOrder(_settings.Video.ChannelOrder);

        CreateVideoTexture();
        _effectWidth = 0; // force the grid overlay to be rebuilt
    }

    private void CreateVideoTexture()
    {
        var hint = _settings.Video.Filter == ScaleFilter.Linear ? "linear" : "nearest";
        var bytes = System.Text.Encoding.UTF8.GetBytes("SDL_RENDER_SCALE_QUALITY\0");
        var value = System.Text.Encoding.UTF8.GetBytes(hint + '\0');
        fixed (byte* name = bytes)
        fixed (byte* quality = value)
            _sdl.SetHint(name, quality);

        if (_videoTexture is not null) _sdl.DestroyTexture(_videoTexture);

        _videoTexture = _sdl.CreateTexture(
            _renderer, (uint)PixelFormatEnum.Abgr8888, (int)TextureAccess.Streaming,
            FrameLayout.Width, FrameLayout.Height);
    }

    private void OnFrameDecoded(VideoNowPlayer player)
    {
        // Raised from inside RenderAudio, so SamplesRendered is the sample index at
        // which this frame's audio begins.
        var copy = (byte[])player.Framebuffer.Clone();
        lock (_pendingGate) _pending.Enqueue(new PendingFrame(player.SamplesRendered, copy));
    }

    private void TopUpAudio()
    {
        if (!_focused && !_settings.Audio.PlayInBackground) return;

        var target = (long)(_settings.Audio.BufferMilliseconds / 1000.0 * FrameLayout.AudioSampleRate) * sizeof(short);
        var guard = 0;

        while (_sdl.GetQueuedAudioSize(_audioDevice) < target && guard++ < 64)
        {
            var span = _mixBuffer.AsSpan();
            _player.RenderAudio(span);

            var volume = _settings.Audio.Muted ? 0f : _settings.Audio.Volume / 100f;
            if (volume < 0.999f)
            {
                for (var i = 0; i < span.Length; i++) span[i] = (short)(span[i] * volume);
            }

            fixed (short* data = _mixBuffer)
            {
                if (_sdl.QueueAudio(_audioDevice, data, (uint)(_mixBuffer.Length * sizeof(short))) != 0) break;
            }

            _samplesQueued += _mixBuffer.Length;
        }
    }

    /// <summary>Promotes the newest decoded frame whose audio has already been played.</summary>
    private void PresentNewestReadyFrame()
    {
        var queuedSamples = _sdl.GetQueuedAudioSize(_audioDevice) / sizeof(short);
        var playPosition = _samplesQueued - queuedSamples;

        lock (_pendingGate)
        {
            while (_pending.Count > 0 && _pending.Peek().StartSample <= playPosition)
                _displayFrame = _pending.Dequeue().Rgba;
        }
    }

    private void PumpEvents()
    {
        Event e = default;
        while (_sdl.PollEvent(ref e) != 0)
        {
            if (_traceInput && e.Type != 0x400)
            {
                Console.Error.WriteLine(
                    $"[input] type=0x{e.Type:X} sym={e.Key.Keysym.Sym} mod={e.Key.Keysym.Mod} " +
                    $"name={Input.KeyNames.Name(e.Key.Keysym.Sym)}");

                Console.Error.Flush();
            }

            switch ((EventType)e.Type)
            {
                case EventType.Quit:
                    RequestQuit();
                    break;

                case EventType.Keydown:
                    OnKeyDown(e.Key.Keysym.Sym, (KeyModifiers)0 | Translate((Keymod)e.Key.Keysym.Mod), e.Key.Repeat != 0);
                    break;

                case EventType.Keyup:
                    OnKeyUp(e.Key.Keysym.Sym);
                    break;

                case EventType.Controllerbuttondown:
                    OnControllerButton(e.Cbutton.Button, pressed: true);
                    break;

                case EventType.Controllerbuttonup:
                    OnControllerButton(e.Cbutton.Button, pressed: false);
                    break;

                case EventType.Controlleraxismotion:
                    OnControllerAxis(e.Caxis.Axis, e.Caxis.Value);
                    break;

                case EventType.Controllerdeviceadded:
                    OpenController();
                    break;

                case EventType.Windowevent:
                    if ((WindowEventID)e.Window.Event == WindowEventID.FocusGained) _focused = true;
                    else if ((WindowEventID)e.Window.Event == WindowEventID.FocusLost) _focused = false;
                    break;
            }
        }
    }

    private static KeyModifiers Translate(Keymod mod)
    {
        var result = KeyModifiers.None;
        if ((mod & (Keymod.Ctrl | Keymod.Lctrl | Keymod.Rctrl)) != 0) result |= KeyModifiers.Control;
        if ((mod & (Keymod.Shift | Keymod.Lshift | Keymod.Rshift)) != 0) result |= KeyModifiers.Shift;
        if ((mod & (Keymod.Alt | Keymod.Lalt | Keymod.Ralt)) != 0) result |= KeyModifiers.Alt;
        return result;
    }

    private void OnKeyDown(int keycode, KeyModifiers modifiers, bool repeat)
    {
        if (_menu.IsCapturing && !repeat)
        {
            if (_menu.Capture(Binding.Key(keycode, modifiers), _input)) return;
        }

        Dispatch(_input.MatchKey(keycode, modifiers), pressed: true, repeat);
    }

    private void OnKeyUp(int keycode)
    {
        // Release only matters for held actions, which never carry modifiers.
        Dispatch(_input.MatchKey(keycode, KeyModifiers.None), pressed: false, repeat: false);
    }

    private void OnControllerButton(byte button, bool pressed)
    {
        if (pressed && _menu.IsCapturing && _menu.Capture(Binding.Button((ControllerButton)button), _input)) return;

        Dispatch(_input.MatchButton(button), pressed, repeat: false);
    }

    private void OnControllerAxis(byte axis, short value)
    {
        var positive = value > AxisThreshold;
        var negative = value < -AxisThreshold;
        var key = axis * 2;

        Handle(key, positive, true);
        Handle(key + 1, negative, false);

        void Handle(int slot, bool active, bool direction)
        {
            if (active == _heldAxes.Contains(slot)) return;

            if (active) _heldAxes.Add(slot);
            else _heldAxes.Remove(slot);

            if (active && _menu.IsCapturing
                && _menu.Capture(Binding.Axis((ControllerAxis)axis, direction), _input))
            {
                return;
            }

            Dispatch(_input.MatchAxis(axis, direction), active, repeat: false);
        }
    }

    /// <summary>
    /// Runs the one action a control means right now.
    /// </summary>
    /// <remarks>
    /// A control can be bound to more than one action on purpose: the arrow keys work the
    /// transport while playing and move the highlight while a menu is open, and Escape
    /// opens the menu and then backs out of it. Which one applies depends on whether a
    /// menu is up, so exactly one action fires per press rather than all of them.
    /// </remarks>
    private void Dispatch(IReadOnlyList<InputAction> actions, bool pressed, bool repeat)
    {
        // Held actions need both edges, wherever the menu is.
        foreach (var action in actions)
        {
            if (action != InputAction.FastForward) continue;
            SetFastForward(pressed);
            return;
        }

        if (!pressed) return;

        if (InputRouter.Resolve(actions, _menu.IsOpen) is not { } resolved) return;

        if (_menu.IsOpen)
        {
            _menu.Handle(resolved);
            if (!_menu.IsOpen) SaveSettings();
            return;
        }

        Perform(resolved, repeat);
    }

    private void Perform(InputAction action, bool repeat)
    {
        switch (action)
        {
            case InputAction.ToggleMenu:
                OpenMenu(Menus.Root(BuildContext()));
                break;

            case InputAction.TrackBrowser:
                OpenMenu(Menus.TrackBrowser(BuildContext()));
                break;

            case InputAction.TogglePause:
                _player.TogglePause();
                break;

            case InputAction.Stop:
                _player.Stop();
                ResetAudio();
                break;

            case InputAction.NextTrack:
                _player.NextTrack();
                ResetAudio();
                break;

            case InputAction.PreviousTrack:
                _player.PreviousTrack();
                ResetAudio();
                break;

            case InputAction.GoBack:
                if (_player.GoBack()) ResetAudio();
                else _menu.Toast("Nothing to go back to");
                break;

            case InputAction.SeekForward:
                _player.SeekBy(_settings.Emulation.SeekSeconds);
                ResetAudio();
                break;

            case InputAction.SeekBackward:
                _player.SeekBy(-_settings.Emulation.SeekSeconds);
                ResetAudio();
                break;

            case InputAction.FrameForward:
                _player.StepFrame(1);
                ResetAudio();
                break;

            case InputAction.FrameBackward:
                _player.StepFrame(-1);
                ResetAudio();
                break;

            case InputAction.SpeedUp:
                ChangeSpeed(25);
                break;

            case InputAction.SpeedDown:
                ChangeSpeed(-25);
                break;

            case InputAction.SpeedReset:
                _settings.Emulation.SpeedPercent = 100;
                ApplySettings();
                _menu.Toast("Normal speed");
                break;

            case InputAction.VolumeUp:
                ChangeVolume(5);
                break;

            case InputAction.VolumeDown:
                ChangeVolume(-5);
                break;

            case InputAction.ToggleMute:
                _settings.Audio.Muted = !_settings.Audio.Muted;
                _menu.Toast(_settings.Audio.Muted ? "Muted" : "Sound on");
                SaveSettings();
                break;

            case InputAction.ToggleLoop:
                _settings.Emulation.Loop = _settings.Emulation.Loop switch
                {
                    LoopMode.None => LoopMode.Track,
                    LoopMode.Track => LoopMode.Disc,
                    _ => LoopMode.None,
                };

                ApplySettings();
                _menu.Toast($"Loop: {_settings.Emulation.Loop}");
                SaveSettings();
                break;

            case InputAction.ToggleOverlay:
                _settings.Interface.Overlay = _settings.Interface.Overlay switch
                {
                    OverlayMode.Hidden => OverlayMode.Auto,
                    OverlayMode.Auto => OverlayMode.Always,
                    _ => OverlayMode.Hidden,
                };

                _overlay.Flash(_settings.Interface);
                SaveSettings();
                break;

            case InputAction.ToggleFullscreen:
                SetFullscreen(!_settings.Video.Fullscreen);
                break;

            case InputAction.Screenshot:
                TakeScreenshot();
                break;

            case InputAction.Quit:
                RequestQuit();
                break;

            case >= InputAction.Choice1 and <= InputAction.Choice6:
                TakeChoice(action - InputAction.Choice1);
                break;
        }

        if (repeat) return;
        _overlay.Flash(_settings.Interface);
    }

    private void TakeChoice(int slot)
    {
        if (_settings.Emulation.InstantChoices)
        {
            if (_player.TakeChoiceNow(slot))
            {
                ResetAudio();
                _menu.Toast($"Branch {slot + 1}");
            }
            else
            {
                _menu.Toast("No such branch here");
            }

            return;
        }

        _menu.Toast(_player.PressChoice(slot) ? $"Branch {slot + 1} chosen" : "No such branch here");
    }

    private void ChangeSpeed(int delta)
    {
        _settings.Emulation.SpeedPercent = Math.Clamp(_settings.Emulation.SpeedPercent + delta, 25, 800);
        ApplySettings();
        _menu.Toast($"{_settings.Emulation.SpeedPercent / 100.0:0.##}x");
        SaveSettings();
    }

    private void ChangeVolume(int delta)
    {
        _settings.Audio.Volume = Math.Clamp(_settings.Audio.Volume + delta, 0, 100);
        _settings.Audio.Muted = false;
        _menu.Toast($"Volume {_settings.Audio.Volume}");
        SaveSettings();
    }

    private void SetFastForward(bool on)
    {
        if (_fastForward == on) return;

        _fastForward = on;
        _player.Speed = on
            ? _settings.Emulation.FastForwardPercent / 100.0
            : _settings.Emulation.SpeedPercent / 100.0;
    }

    private MenuContext BuildContext() => new()
    {
        Settings = _settings,
        Input = _input,
        Player = _player,
        Disc = _discMap,
        CloseMenu = () =>
        {
            _menu.Close();
            SaveSettings();
        },
        Screenshot = TakeScreenshot,
        ToggleFullscreen = () => SetFullscreen(!_settings.Video.Fullscreen),
        Quit = RequestQuit,
        Toast = message => _menu.Toast(message),
    };

    private void OpenMenu(MenuPage page)
    {
        _menu.Push(page);
        if (!_settings.Audio.PlayInBackground) _player.Pause();
    }

    private void RequestQuit() => _running = false;

    /// <summary>Drops queued audio and buffered frames so a transport jump takes effect at once.</summary>
    private void ResetAudio()
    {
        _sdl.ClearQueuedAudio(_audioDevice);
        lock (_pendingGate) _pending.Clear();
        _displayFrame = (byte[])_player.Framebuffer.Clone();
        _samplesQueued = 0;
    }

    private void SetFullscreen(bool on)
    {
        _settings.Video.Fullscreen = on;
        _sdl.SetWindowFullscreen(_window, on ? (uint)WindowFlags.FullscreenDesktop : 0);
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.StoreInputMap(_input);
            SettingsStore.Save(_settings);
        }
        catch (IOException)
        {
            // A settings file that cannot be written should not interrupt playback.
        }
    }

    private void TakeScreenshot()
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(_settings.Interface.ScreenshotDirectory)
                ? Path.Combine(SettingsStore.Directory, "screenshots")
                : _settings.Interface.ScreenshotDirectory;

            Directory.CreateDirectory(directory);

            var name = $"{_discMap.Name}_t{_player.CurrentTrack:D2}_f{_player.CurrentFrame:D5}.png";
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

            var path = Path.Combine(directory, name);
            PngWriter.Write(path, _displayRgba, FrameLayout.Width, FrameLayout.Height);
            _menu.Toast($"Saved {Path.GetFileName(path)}", 3);
        }
        catch (IOException ex)
        {
            _menu.Toast($"Screenshot failed: {ex.Message}", 4);
        }
    }

    private void Render()
    {
        int windowWidth, windowHeight;
        _sdl.GetRendererOutputSize(_renderer, &windowWidth, &windowHeight);

        _displayFrame.CopyTo(_displayRgba, 0);
        _adjust.Apply(_displayRgba);

        fixed (byte* pixels = _displayRgba)
            _sdl.UpdateTexture(_videoTexture, (Rectangle<int>*)null, pixels, FrameLayout.Width * 4);

        var background = Rgba.Parse(_settings.Video.BackgroundColor);
        _sdl.SetRenderDrawColor(_renderer, background.R, background.G, background.B, 255);
        _sdl.RenderClear(_renderer);

        var destination = ComputeDestination(windowWidth, windowHeight);
        _sdl.RenderCopy(_renderer, _videoTexture, (Rectangle<int>*)null, ref destination);

        DrawPanelEffect(ref destination);
        DrawOverlay(windowWidth, windowHeight);

        _sdl.RenderPresent(_renderer);
    }

    private Rectangle<int> ComputeDestination(int windowWidth, int windowHeight)
    {
        var sourceWidth = FrameLayout.Width * _settings.Video.PixelAspect;
        var sourceHeight = (double)FrameLayout.Height;

        switch (_settings.Video.ScaleMode)
        {
            case Config.ScaleMode.Stretch:
                return new Rectangle<int>(0, 0, windowWidth, windowHeight);

            case Config.ScaleMode.OneToOne:
            {
                var w = (int)Math.Round(sourceWidth);
                return Centred(w, FrameLayout.Height);
            }

            case Config.ScaleMode.IntegerScale:
            {
                var factor = Math.Max(1, Math.Min(
                    (int)(windowWidth / sourceWidth),
                    windowHeight / FrameLayout.Height));

                return Centred((int)Math.Round(sourceWidth * factor), FrameLayout.Height * factor);
            }

            default:
            {
                var factor = Math.Min(windowWidth / sourceWidth, windowHeight / sourceHeight);
                return Centred((int)Math.Round(sourceWidth * factor), (int)Math.Round(sourceHeight * factor));
            }
        }

        Rectangle<int> Centred(int width, int height)
            => new((windowWidth - width) / 2, (windowHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Draws the simulated pixel grid and scanlines over the picture, at the size the
    /// picture is actually being shown so the lines land on real pixel boundaries.
    /// </summary>
    private void DrawPanelEffect(ref Rectangle<int> destination)
    {
        var grid = _settings.Video.LcdGrid;
        var scanlines = _settings.Video.Scanlines;
        if (grid == 0 && scanlines == 0) return;

        var width = destination.Size.X;
        var height = destination.Size.Y;
        if (width < FrameLayout.Width || height < FrameLayout.Height) return;

        if (_effectTexture is null || _effectWidth != width || _effectHeight != height)
        {
            BuildEffectTexture(width, height, grid, scanlines);
            _effectWidth = width;
            _effectHeight = height;
        }

        if (_effectTexture is not null) _sdl.RenderCopy(_renderer, _effectTexture, (Rectangle<int>*)null, ref destination);
    }

    private void BuildEffectTexture(int width, int height, int grid, int scanlines)
    {
        if (_effectTexture is not null) _sdl.DestroyTexture(_effectTexture);

        var pixels = new byte[width * height * 4];
        var cellWidth = width / (double)FrameLayout.Width;
        var cellHeight = height / (double)FrameLayout.Height;

        for (var y = 0; y < height; y++)
        {
            var alpha = 0;

            if (scanlines > 0 && (int)(y / cellHeight * 2) % 2 == 1) alpha = scanlines * 255 / 200;
            if (grid > 0 && (int)(y % cellHeight) == 0) alpha = Math.Max(alpha, grid * 255 / 150);

            for (var x = 0; x < width; x++)
            {
                var value = alpha;
                if (grid > 0 && (int)(x % cellWidth) == 0) value = Math.Max(value, grid * 255 / 150);
                if (value == 0) continue;

                var index = (y * width + x) * 4;
                pixels[index + 3] = (byte)Math.Clamp(value, 0, 255);
            }
        }

        _effectTexture = _sdl.CreateTexture(
            _renderer, (uint)PixelFormatEnum.Abgr8888, (int)TextureAccess.Static, width, height);

        if (_effectTexture is null) return;

        fixed (byte* data = pixels) _sdl.UpdateTexture(_effectTexture, (Rectangle<int>*)null, data, width * 4);
        _sdl.SetTextureBlendMode(_effectTexture, BlendMode.Blend);
    }

    private void DrawOverlay(int windowWidth, int windowHeight)
    {
        var scale = _settings.Interface.FontScale > 0
            ? _settings.Interface.FontScale
            : Math.Clamp(windowWidth / 480, 1, 4);

        _overlay.Track(_player, _settings.Interface);

        if (_canvas.Resize(windowWidth, windowHeight)) RecreateOverlayTexture();

        // Nothing to draw: skip the clear and the texture upload entirely.
        var showStatus = _overlay.ShouldDraw(_settings.Interface, _player);
        if (!showStatus && !_menu.IsOpen && !_menu.HasToast) return;

        _canvas.Clear();

        if (showStatus)
        {
            var queuedMs = (int)(_sdl.GetQueuedAudioSize(_audioDevice) / sizeof(short)
                                 * 1000.0 / FrameLayout.AudioSampleRate);

            _overlay.Draw(_canvas, scale, _player, _settings,
                new HostStatus(_settings.Audio.Volume, _settings.Audio.Muted, _measuredFps, queuedMs));
        }

        _menu.Draw(_canvas, scale, _settings.Interface.DimBehindMenu);

        if (_overlayTexture is null) RecreateOverlayTexture();
        if (_overlayTexture is null) return;

        fixed (byte* pixels = _canvas.Pixels)
            _sdl.UpdateTexture(_overlayTexture, (Rectangle<int>*)null, pixels, _canvas.Width * 4);

        var destination = new Rectangle<int>(0, 0, _canvas.Width, _canvas.Height);
        _sdl.RenderCopy(_renderer, _overlayTexture, (Rectangle<int>*)null, ref destination);
    }

    private void RecreateOverlayTexture()
    {
        if (_overlayTexture is not null) _sdl.DestroyTexture(_overlayTexture);

        _overlayTexture = _sdl.CreateTexture(
            _renderer, (uint)PixelFormatEnum.Abgr8888, (int)TextureAccess.Streaming,
            _canvas.Width, _canvas.Height);

        if (_overlayTexture is not null) _sdl.SetTextureBlendMode(_overlayTexture, BlendMode.Blend);
    }

    private void OpenController()
    {
        if (_controller is not null) return;

        for (var i = 0; i < _sdl.NumJoysticks(); i++)
        {
            if (_sdl.IsGameController(i) == SdlBool.False) continue;

            _controller = _sdl.GameControllerOpen(i);
            if (_controller is not null) return;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _player.FrameDecoded -= OnFrameDecoded;
        _menu.Changed -= ApplySettings;

        if (_controller is not null) _sdl.GameControllerClose(_controller);
        if (_audioDevice != 0) _sdl.CloseAudioDevice(_audioDevice);
        if (_effectTexture is not null) _sdl.DestroyTexture(_effectTexture);
        if (_overlayTexture is not null) _sdl.DestroyTexture(_overlayTexture);
        if (_videoTexture is not null) _sdl.DestroyTexture(_videoTexture);
        if (_renderer is not null) _sdl.DestroyRenderer(_renderer);
        if (_window is not null) _sdl.DestroyWindow(_window);

        _sdl.Quit();
        _sdl.Dispose();
    }
}
