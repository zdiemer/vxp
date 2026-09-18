using Silk.NET.Maths;
using Silk.NET.SDL;
using Vxp.Config;
using Vxp.Emulation;
using Vxp.Format;
using Vxp.Input;
using Vxp.Ui;
using Vxp.Ui.Native;
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

    /// <summary>Timer that keeps playback alive while a native menu holds the thread.</summary>
    private const nuint MenuLoopTimerId = 1;

    // The disc can be swapped while the window is up, and there may be none at all, so
    // these three change together in Mount and are null in the empty player.
    private LoadedDisc? _disc;
    private VideoNowPlayer? _player;
    private DiscMap? _discMap;

    private readonly VxpSettings _settings;
    private readonly InputMap _input;
    private readonly SettingsSession _session;

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

    private nint _nativeWindow;
    private Win32MenuBar? _menuBar;
    private Win32WindowHook? _messageHook;
    private bool _menuBarStale;
    private bool _inMenuLoop;
    private bool _pumping;
    private bool _altUsed;

    private readonly bool _traceInput = Environment.GetEnvironmentVariable("VXP_TRACE_INPUT") == "1";
    private readonly bool _traceMenu = Environment.GetEnvironmentVariable("VXP_TRACE_MENU") == "1";
    private bool _running = true;
    private bool _disposed;

    /// <summary>Why the last disc failed to open, shown on the empty player's screen.</summary>
    private string? _loadError;

    /// <summary>
    /// Work that must not run inside the event pump: a modal dialog, or a disc swap
    /// requested from a menu that is still being dispatched.
    /// </summary>
    private readonly Queue<Action> _deferred = new();

    private readonly record struct PendingFrame(long StartSample, byte[] Rgba);

    /// <summary>Creates the window and opens the audio device.</summary>
    /// <param name="disc">
    /// The disc to play, or null to open the empty player. The window owns it from here
    /// and disposes it when another disc replaces it or the window closes.
    /// </param>
    /// <param name="settings">Live settings; the menus change them in place.</param>
    /// <param name="input">The binding table.</param>
    /// <param name="session">What saving writes back, and whether it writes at all.</param>
    public PlayerWindow(LoadedDisc? disc, VxpSettings settings, InputMap input, SettingsSession session)
    {
        _disc = disc;
        _player = disc?.Player;
        _discMap = disc?.Map;
        _settings = settings;
        _input = input;
        _session = session;
        _adjust = PictureAdjustment.FromSettings(settings.Video);
        _displayFrame = CurrentFramebuffer();

        _sdl = Sdl.GetApi();
        if (_sdl.Init(Sdl.InitVideo | Sdl.InitAudio | Sdl.InitGamecontroller) != 0)
            throw new SdlException($"SDL_Init failed: {_sdl.GetErrorS()}");

        // SDL swallows Alt and F10 so games can use them, which would leave a native menu
        // bar reachable only with the mouse. The menu matters more here than Alt does.
        if (settings.Interface.NativeMenuBar) SetHint("SDL_WINDOWS_ENABLE_MENU_MNEMONICS", "1");

        var scale = Math.Clamp(settings.Video.WindowScale, 1, 16);

        _window = _sdl.CreateWindow(
            "vxp",
            Sdl.WindowposCentered, Sdl.WindowposCentered,
            DisplayWidth(settings.Video) * scale, FrameLayout.Height * scale,
            (uint)(WindowFlags.Shown | WindowFlags.Resizable));

        if (_window is null)
            throw new SdlException($"SDL_CreateWindow failed: {_sdl.GetErrorS()}");

        var rendererFlags = (uint)RendererFlags.Accelerated;
        if (settings.Video.VSync) rendererFlags |= (uint)RendererFlags.Presentvsync;

        _renderer = _sdl.CreateRenderer(_window, -1, rendererFlags);
        if (_renderer is null) _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
        if (_renderer is null) throw new SdlException($"SDL_CreateRenderer failed: {_sdl.GetErrorS()}");

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
            throw new SdlException($"SDL_OpenAudioDevice failed: {_sdl.GetErrorS()}");

        _sdl.PauseAudioDevice(_audioDevice, 0);
        OpenController();

        if (_player is not null) _player.FrameDecoded += OnFrameDecoded;
        _menu.Changed += ApplySettings;

        ApplySettings();
        CreateNativeMenu(FrameLayout.Height * scale);
        _menuBarStale = false;
        UpdateTitle();

        if (settings.Video.Fullscreen) SetFullscreen(true);
        if (settings.Emulation.AutoPlay) _player?.Play();
    }

    // ------------------------------------------------------------ disc swapping

    /// <summary>The frame to show right now: the player's, or black with no disc.</summary>
    private byte[] CurrentFramebuffer()
        => _player is null ? new byte[VideoDecoder.RgbaFrameBytes] : (byte[])_player.Framebuffer.Clone();

    /// <summary>
    /// Opens the disc at <paramref name="path"/> in place of the one playing. If it cannot
    /// be opened the message is shown in the window and whatever was playing carries on.
    /// </summary>
    private void LoadDisc(string path)
    {
        LoadedDisc next;
        try
        {
            next = LoadedDisc.Open(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A bad file is the viewer's to fix, not a reason to take the player down.
            _loadError = $"Could not open {Path.GetFileName(path)}: {ex.Message}";

            // The empty player's screen shows it already, unless a menu is covering it;
            // over a disc it has to be said.
            if (_player is not null || _menu.IsOpen) _menu.Toast(_loadError, 6);

            // A recent disc that has since gone is dropped, so the list stays worth reading.
            if (!File.Exists(path) && _settings.RecentDiscs.Remove(path))
            {
                _menuBarStale = true;
                SaveSettings();
            }

            return;
        }

        Mount(next);

        // Only a zip needs this: see PlayCommand.
        next.Player.Disc.PrecacheInBackground();

        _settings.RecordRecentDisc(next.Path);
        SaveSettings();
        _menu.Toast(_discMap?.Name ?? "Disc loaded", 3);
    }

    /// <summary>
    /// Puts <paramref name="next"/> in the player, or empties it when null, and disposes
    /// whatever was there before.
    /// </summary>
    /// <remarks>
    /// Frames and sound from the old disc are dropped rather than played out, and the
    /// player is only ever driven from this thread, so nothing can reach the old one once
    /// it has been unhooked here.
    /// </remarks>
    private void Mount(LoadedDisc? next)
    {
        var previous = _disc;
        if (_player is not null) _player.FrameDecoded -= OnFrameDecoded;

        _disc = next;
        _player = next?.Player;
        _discMap = next?.Map;
        _fastForward = false;
        _loadError = null;
        _menu.Close();

        if (_player is not null) _player.FrameDecoded += OnFrameDecoded;

        ApplySettings();
        ResetAudio();
        previous?.Dispose();
        UpdateTitle();

        if (_player is not null && _settings.Emulation.AutoPlay) _player.Play();
        _overlay.Flash(_settings.Interface);
    }

    private void CloseDisc()
    {
        if (_disc is null) return;

        Mount(null);
        _menu.Toast("Disc ejected");
    }

    /// <summary>Asks for a disc with the system Open dialog, or the nearest thing to one.</summary>
    private void ChooseDisc()
    {
        var owner = OperatingSystem.IsWindows() ? WindowHandle() : 0;

        if (owner == 0)
        {
            // Nowhere to put a native dialog: the recent list and a drop are what's left.
            OpenMenu(Menus.RecentDiscs(BuildContext()));
            _menu.Toast("Drop a .cue or .zip onto the window to open it", 4);
            return;
        }

        var recent = _settings.RecentDiscs.FirstOrDefault();
        var folder = recent is null ? null : Path.GetDirectoryName(recent);
        if (folder is not null && !Directory.Exists(folder)) folder = null;

        // Full screen hides the pointer, which the dialog needs.
        _sdl.ShowCursor(1);
        var path = Win32.ShowOpenFileDialog(owner, "Open a VideoNow disc", DiscFiles.DialogFilter, folder);
        _sdl.ShowCursor(_settings.Video.Fullscreen ? 0 : 1);

        if (path is not null) LoadDisc(path);
    }

    /// <summary>A file dropped on the window: opened if it looks like a disc.</summary>
    private void OnDropFile(byte* file)
    {
        if (file is null) return;

        var path = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)file);
        _sdl.Free(file);
        if (string.IsNullOrEmpty(path)) return;

        if (!DiscFiles.IsDiscFile(path))
        {
            _menu.Toast($"Not a disc: {Path.GetFileName(path)}. Drop a .cue, .zip or .bin.", 4);
            return;
        }

        _deferred.Enqueue(() => LoadDisc(path));
    }

    /// <summary>The Win32 handle behind the SDL window, whether or not it has a menu bar.</summary>
    private nint WindowHandle()
    {
        if (_nativeWindow != 0) return _nativeWindow;

        var version = new Silk.NET.SDL.Version();
        _sdl.GetVersion(ref version);

        var info = new SysWMInfo { Version = version };
        if (!_sdl.GetWindowWMInfo(_window, &info) || info.Subsystem != SysWMType.Windows) return 0;

        return info.Info.Win.Hwnd;
    }

    private void UpdateTitle()
        => _sdl.SetWindowTitle(_window, _discMap is null ? "vxp" : $"{_discMap.Name} - vxp");

    private void RunDeferred()
    {
        // Anything queued while this runs waits for the next turn of the loop.
        for (var count = _deferred.Count; count > 0 && _deferred.TryDequeue(out var work); count--) work();
    }

    /// <summary>
    /// Puts a native menu bar on the window, where the platform has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bar is built from the same pages the in-window menu draws. Both stay
    /// available: the bar is the everyday surface, and the in-window menu is what full
    /// screen, game controllers and the other platforms use — and it is still the only
    /// place a control can be rebound, because a menu bar has nowhere to catch a
    /// keypress.
    /// </para>
    /// <para>
    /// <paramref name="desiredClientHeight"/> is the picture height the window was asked
    /// for. SDL sizes windows without knowing about a menu, so the bar would otherwise
    /// take its strip out of the picture.
    /// </para>
    /// </remarks>
    private void CreateNativeMenu(int desiredClientHeight)
    {
        if (!OperatingSystem.IsWindows() || !_settings.Interface.NativeMenuBar) return;

        var version = new Silk.NET.SDL.Version();
        _sdl.GetVersion(ref version);

        var info = new SysWMInfo { Version = version };
        if (!_sdl.GetWindowWMInfo(_window, &info) || info.Subsystem != SysWMType.Windows) return;

        _nativeWindow = info.Info.Win.Hwnd;
        if (_nativeWindow == 0) return;

        try
        {
            _menuBar = new Win32MenuBar(_nativeWindow, Menus.Bar(BuildContext()), OnNativeMenuChanged);
        }
        catch (InvalidOperationException)
        {
            // No menu bar is a cosmetic loss; the in-window menu still works.
            _nativeWindow = 0;
            return;
        }

        // Menu messages never reach SDL_PollEvent in time to be useful: Windows runs its
        // own modal loop while a menu is open, so they have to be taken as they arrive.
        // Without the hook nothing on the bar would do anything, so a bar is only worth
        // showing if the hook went on.
        _messageHook = new Win32WindowHook(_nativeWindow, OnWindowsMessage);
        if (!_messageHook.Installed)
        {
            _messageHook.Dispose();
            _messageHook = null;
            _menuBar.Dispose();
            _menuBar = null;
            _nativeWindow = 0;
            return;
        }

        _menuBar.Attach();
        _menuBar.PreserveClientHeight(desiredClientHeight);
    }

    private void OnWindowsMessage(uint message, nuint wParam, nint lParam)
    {
        if (_traceMenu && message is Win32.WmCommand or Win32.WmInitMenuPopup or Win32.WmEnterMenuLoop or Win32.WmExitMenuLoop)
        {
            Console.Error.WriteLine($"[menu] msg=0x{message:X} wParam=0x{wParam:X} lParam=0x{lParam:X} bar={_menuBar is not null}");
            Console.Error.Flush();
        }

        if (_menuBar is not { } bar) return;

        switch (message)
        {
            // A menu choice has a zero high word and no control handle behind it.
            case Win32.WmCommand when (wParam >> 16) == 0 && lParam == 0:
                bar.Invoke((int)(wParam & 0xFFFF));
                break;

            case Win32.WmInitMenuPopup:
                bar.RefreshPopup((nint)wParam);
                break;

            case Win32.WmEnterMenuLoop:
                _inMenuLoop = true;
                Win32.SetTimer(_nativeWindow, MenuLoopTimerId, 15, 0);
                break;

            case Win32.WmExitMenuLoop:
                _inMenuLoop = false;
                Win32.KillTimer(_nativeWindow, MenuLoopTimerId);
                break;

            case Win32.WmTimer when _inMenuLoop && wParam == MenuLoopTimerId:
                PumpWhileMenuIsOpen();
                break;

            case Win32.WmSysKeyDown:
                OnSystemKeyDown(wParam, lParam);
                break;

            // A bare Alt, pressed and released with nothing in between, opens the bar.
            case Win32.WmSysKeyUp when wParam == Win32.VkMenu:
                if (!_altUsed) ActivateMenuBar('\0');
                _altUsed = false;
                break;
        }
    }

    /// <summary>
    /// Gives the menu bar the keyboard, which it does not otherwise get.
    /// </summary>
    /// <remarks>
    /// Windows normally turns Alt and F10 into a menu activation inside
    /// <c>DefWindowProc</c>, but SDL handles those keys itself and never passes them on,
    /// so a menu bar on an SDL window can only be opened with the mouse. Reading the
    /// keys here and asking for the activation directly puts that back. Alt with a
    /// letter is only forwarded when the letter really is one of the bar's mnemonics, so
    /// a control bound to Alt and something else still behaves as the viewer bound it.
    /// </remarks>
    private void OnSystemKeyDown(nuint wParam, nint lParam)
    {
        if (wParam == Win32.VkMenu)
        {
            if ((lParam & Win32.KeyWasDown) == 0) _altUsed = false;
            return;
        }

        _altUsed = true;

        if (wParam == Win32.VkF10)
        {
            ActivateMenuBar('\0');
            return;
        }

        var key = (char)wParam;
        if (_menuBar?.HasMnemonic(key) == true) ActivateMenuBar(key);
    }

    private void ActivateMenuBar(char mnemonic)
    {
        if (_nativeWindow == 0 || _settings.Video.Fullscreen) return;

        Win32.PostMessage(_nativeWindow, Win32.WmSysCommand, Win32.ScKeyMenu, mnemonic);
    }

    /// <summary>
    /// Keeps sound and picture running while an open menu holds the thread.
    /// </summary>
    /// <remarks>
    /// An open Windows menu runs its own message loop, so <see cref="Run"/> stops turning
    /// for as long as the menu is up. Left alone the queued audio would drain dry and the
    /// picture would freeze, so a timer inside that loop drives the same work the main
    /// loop does, minus the event pump that Windows is already doing.
    /// </remarks>
    private void PumpWhileMenuIsOpen()
    {
        if (_pumping) return;

        _pumping = true;
        try
        {
            TopUpAudio();
            PresentNewestReadyFrame();
            Render();
        }
        finally
        {
            _pumping = false;
        }
    }

    private void OnNativeMenuChanged()
    {
        ApplySettings();
        SaveSettings();
    }

    /// <summary>
    /// Rebuilds the bar so rows that come and go — the track list, mainly — follow the
    /// settings. Labels and marks refresh themselves as each popup opens, so this only
    /// runs between frames, never while a menu is up.
    /// </summary>
    private void RebuildNativeMenu()
    {
        _menuBarStale = false;
        if (_menuBar is not { } previous || _nativeWindow == 0 || _inMenuLoop) return;

        Win32MenuBar replacement;
        try
        {
            replacement = new Win32MenuBar(_nativeWindow, Menus.Bar(BuildContext()), OnNativeMenuChanged);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        // Attach first so the bar is replaced rather than removed and put back, which
        // would resize the client area twice and make the picture jump.
        if (!_settings.Video.Fullscreen) replacement.Attach();

        _menuBar = replacement;
        previous.Dispose();
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
            if (_menuBarStale) RebuildNativeMenu();

            PumpEvents();
            RunDeferred();
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
        if (_player is not null)
        {
            _player.Navigation = _settings.Emulation.Navigation;
            _player.Timeout = _settings.Emulation.ChoiceTimeout;
            _player.Loop = _settings.Emulation.Loop;

            if (!_fastForward) _player.Speed = PlaybackRate(_settings.Emulation.SpeedPercent);
        }

        _adjust.Brightness = _settings.Video.Brightness;
        _adjust.Contrast = _settings.Video.Contrast;
        _adjust.Saturation = _settings.Video.Saturation;
        _adjust.Gamma = _settings.Video.Gamma;
        _adjust.SetOrder(_settings.Video.ChannelOrder);

        CreateVideoTexture();
        _effectWidth = 0; // force the grid overlay to be rebuilt
        _menuBarStale = true;
    }

    /// <summary>
    /// Width one frame occupies on screen, in source pixels, once the panel's non-square
    /// pixels are allowed for.
    /// </summary>
    /// <remarks>
    /// The picture is stored 144 wide but is not 144 wide to look at, so this is what the
    /// window is sized from. Sizing the window to the stored width instead would leave
    /// the picture pillarboxed in a window that never fits it.
    /// </remarks>
    public static int DisplayWidth(VideoSettings video)
        => (int)Math.Round(FrameLayout.Width * Math.Clamp(video.PixelAspect, 0.5, 2.0));

    private void SetHint(string name, string value)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name + '\0');
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value + '\0');

        fixed (byte* hint = nameBytes)
        fixed (byte* setting = valueBytes)
            _sdl.SetHint(hint, setting);
    }

    private void CreateVideoTexture()
    {
        SetHint("SDL_RENDER_SCALE_QUALITY", _settings.Video.Filter == ScaleFilter.Linear ? "linear" : "nearest");

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
        if (_player is null) return;
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

                case EventType.Dropfile:
                    OnDropFile(e.Drop.File);
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
        if (_player is null && InputActions.NeedsDisc(action))
        {
            if (!repeat) _menu.Toast(NoDiscHint(), 3);
            return;
        }

        switch (action)
        {
            case InputAction.OpenDisc:
                // The dialog is modal and pumps messages itself, so it waits until the
                // event that asked for it has been dealt with.
                _deferred.Enqueue(ChooseDisc);
                break;

            case InputAction.CloseDisc:
                _deferred.Enqueue(CloseDisc);
                break;

            case InputAction.ToggleMenu:
                OpenMenu(Menus.Root(BuildContext()));
                break;

            case InputAction.TrackBrowser:
                OpenMenu(Menus.TrackBrowser(BuildContext()));
                break;

            case InputAction.TogglePause:
                _player?.TogglePause();
                break;

            case InputAction.Stop:
                _player?.Stop();
                ResetAudio();
                break;

            case InputAction.NextTrack:
                _player?.NextTrack();
                ResetAudio();
                break;

            case InputAction.PreviousTrack:
                _player?.PreviousTrack();
                ResetAudio();
                break;

            case InputAction.GoBack:
                if (_player?.GoBack() == true) ResetAudio();
                else _menu.Toast("Nothing to go back to");
                break;

            case InputAction.SeekForward:
                _player?.SeekBy(_settings.Emulation.SeekSeconds);
                ResetAudio();
                break;

            case InputAction.SeekBackward:
                _player?.SeekBy(-_settings.Emulation.SeekSeconds);
                ResetAudio();
                break;

            case InputAction.FrameForward:
                _player?.StepFrame(1);
                ResetAudio();
                break;

            case InputAction.FrameBackward:
                _player?.StepFrame(-1);
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
        if (_player is null) return;
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
        if (_player is null) return;
        if (_fastForward == on) return;

        _fastForward = on;
        _player.Speed = PlaybackRate(on ? _settings.Emulation.FastForwardPercent : _settings.Emulation.SpeedPercent);
    }

    /// <summary>
    /// The player speed for a percentage of normal. The device runs at the byte-derived
    /// rate, so the configured disc rate is folded in as a resampling ratio.
    /// </summary>
    private double PlaybackRate(int percent)
        => percent / 100.0 * _settings.Emulation.DiscSampleRate / FrameLayout.AudioSampleRate;

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
        Perform = action => Perform(action, repeat: false),
        SelectTrack = number =>
        {
            if (_player is null) return;
            _player.SelectTrack(number);
            _player.Play();
            ResetAudio();
            _menu.Close();
            SaveSettings();
        },
        OpenPage = OpenMenu,
        ShowInfo = ShowInfo,
        OpenScreenshots = OpenScreenshotFolder,
        OpenRecent = path => _deferred.Enqueue(() => LoadDisc(path)),
    };

    /// <summary>What to tell someone who pressed a disc control with no disc in.</summary>
    private string NoDiscHint()
    {
        var open = _input.BindingsFor(InputAction.OpenDisc);
        return open.Count == 0 ? "No disc loaded" : $"No disc loaded - {open[0]} opens one";
    }

    /// <summary>
    /// Shows a block of text in a native dialog, falling back to an in-window page where
    /// there is no dialog to put it in.
    /// </summary>
    private void ShowInfo(string title, string body)
    {
        if (OperatingSystem.IsWindows() && _nativeWindow != 0)
        {
            Win32.MessageBox(_nativeWindow, body, title, Win32.MbOk | Win32.MbIconInformation);
            return;
        }

        OpenMenu(new MenuPage
        {
            Title = title,
            Items = body.ReplaceLineEndings("\n").Split('\n')
                .Select(line => (MenuItem)new MenuHeading { Label = line })
                .ToArray(),
        });
    }

    private void OpenScreenshotFolder()
    {
        try
        {
            var directory = ScreenshotDirectory();
            Directory.CreateDirectory(directory);

            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _menu.Toast($"Could not open the folder: {ex.Message}", 4);
        }
    }

    private string ScreenshotDirectory()
        => string.IsNullOrWhiteSpace(_settings.Interface.ScreenshotDirectory)
            ? Path.Combine(SettingsStore.Directory, "screenshots")
            : _settings.Interface.ScreenshotDirectory;

    private void OpenMenu(MenuPage page)
    {
        _menu.Push(page);
        if (!_settings.Audio.PlayInBackground) _player?.Pause();
    }

    private void RequestQuit() => _running = false;

    /// <summary>Drops queued audio and buffered frames so a transport jump takes effect at once.</summary>
    private void ResetAudio()
    {
        _sdl.ClearQueuedAudio(_audioDevice);
        lock (_pendingGate) _pending.Clear();
        _displayFrame = CurrentFramebuffer();

        // Frames are tagged with the player's running sample count, which a jump does not
        // reset. Zeroing this instead would hold the picture back by everything played so
        // far: frozen for that long after the jump, then trailing the sound by it.
        _samplesQueued = _player?.SamplesRendered ?? 0;
    }

    private void SetFullscreen(bool on)
    {
        _settings.Video.Fullscreen = on;

        // A menu bar across the top of a full screen picture is neither use nor ornament.
        if (on) _menuBar?.Detach();
        _sdl.SetWindowFullscreen(_window, on ? (uint)WindowFlags.FullscreenDesktop : 0);
        if (!on) _menuBar?.Attach();

        // Nothing in the player is driven by the mouse, so full screen hides the pointer
        // rather than leave it parked over the picture.
        _sdl.ShowCursor(on ? 0 : 1);

        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.StoreInputMap(_input);
            _session.Save(_settings);
        }
        catch (IOException)
        {
            // A settings file that cannot be written should not interrupt playback.
        }
    }

    private void TakeScreenshot()
    {
        if (_player is null || _discMap is null) return;

        try
        {
            var directory = ScreenshotDirectory();
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

        if (_player is null)
        {
            DrawEmptyPlayer(windowWidth, windowHeight, scale);
            return;
        }

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

    /// <summary>
    /// The screen with no disc in: how to open one, and why the last one would not open.
    /// </summary>
    private void DrawEmptyPlayer(int windowWidth, int windowHeight, int menuScale)
    {
        if (_canvas.Resize(windowWidth, windowHeight)) RecreateOverlayTexture();
        _canvas.Clear();

        // The status overlay's size reads as small print; this is the whole screen.
        var scale = _settings.Interface.FontScale > 0
            ? _settings.Interface.FontScale + 1
            : Math.Clamp(windowWidth / 260, 1, 4);

        var lines = new List<(string Text, int Scale, Rgba Color)> { ("No disc", scale * 2, Rgba.Accent) };

        var open = _input.BindingsFor(InputAction.OpenDisc);
        var menu = _input.BindingsFor(InputAction.ToggleMenu);
        lines.Add((open.Count > 0 ? $"{open[0]} to open a disc" : "Open a disc from the menu", scale, Rgba.White));
        lines.Add(("or drop a .cue or .zip on this window", scale, Rgba.Grey));
        if (menu.Count > 0) lines.Add(($"{menu[0]} for the menu", scale, Rgba.Grey));

        var margin = 8 * scale;
        var gap = BitmapFont.LineAdvance;
        var errorFrom = lines.Count;

        if (_loadError is not null)
        {
            foreach (var line in Wrap(_loadError, windowWidth - margin * 2, scale).Take(3))
                lines.Add((line, scale, Rgba.Warn));
        }

        var height = lines.Sum(l => l.Scale * gap) + gap * scale;
        var y = (windowHeight - height) / 2;

        for (var i = 0; i < lines.Count; i++)
        {
            // A little air under the heading, and before an error.
            if (i == 1 || (i == errorFrom && i < lines.Count)) y += gap * scale / 2;

            var (text, size, color) = lines[i];
            _canvas.TextCentred(windowWidth / 2, y, BitmapFont.Fit(text, windowWidth - margin * 2, size), size, color);
            y += size * gap;
        }

        _menu.Draw(_canvas, menuScale, _settings.Interface.DimBehindMenu);

        if (_overlayTexture is null) RecreateOverlayTexture();
        if (_overlayTexture is null) return;

        fixed (byte* pixels = _canvas.Pixels)
            _sdl.UpdateTexture(_overlayTexture, (Rectangle<int>*)null, pixels, _canvas.Width * 4);

        var destination = new Rectangle<int>(0, 0, _canvas.Width, _canvas.Height);
        _sdl.RenderCopy(_renderer, _overlayTexture, (Rectangle<int>*)null, ref destination);
    }

    /// <summary>Breaks <paramref name="text"/> into lines that fit <paramref name="pixels"/>, at word boundaries.</summary>
    private static IEnumerable<string> Wrap(string text, int pixels, int scale)
    {
        var line = string.Empty;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (line.Length > 0 && BitmapFont.Measure(candidate, scale) > pixels)
            {
                yield return line;
                candidate = word;
            }

            line = candidate;
        }

        if (line.Length > 0) yield return line;
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

        if (_player is not null) _player.FrameDecoded -= OnFrameDecoded;
        _menu.Changed -= ApplySettings;

        // Drop the bar before the window goes: the message hook reads this field and must
        // find nothing once the handles behind it are gone.
        var menuBar = _menuBar;
        _menuBar = null;
        if (_nativeWindow != 0) Win32.KillTimer(_nativeWindow, MenuLoopTimerId);
        menuBar?.Dispose();
        _messageHook?.Dispose();
        _messageHook = null;
        _nativeWindow = 0;

        if (_controller is not null) _sdl.GameControllerClose(_controller);
        if (_audioDevice != 0) _sdl.CloseAudioDevice(_audioDevice);
        if (_effectTexture is not null) _sdl.DestroyTexture(_effectTexture);
        if (_overlayTexture is not null) _sdl.DestroyTexture(_overlayTexture);
        if (_videoTexture is not null) _sdl.DestroyTexture(_videoTexture);
        if (_renderer is not null) _sdl.DestroyRenderer(_renderer);
        if (_window is not null) _sdl.DestroyWindow(_window);

        _sdl.Quit();
        _sdl.Dispose();

        _disc?.Dispose();
        _disc = null;
        _player = null;
        _discMap = null;
    }
}
