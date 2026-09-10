using Silk.NET.Maths;
using Silk.NET.SDL;
using Vxp.Emulation;
using Vxp.Format;

namespace Vxp;

/// <summary>
/// SDL front end: window, texture upload, queued audio and input handling.
/// </summary>
/// <remarks>
/// <para>
/// Audio is queued rather than pulled from a callback, and each decoded frame is tagged
/// with the audio sample index it starts at. The picture shown is the newest frame whose
/// audio has actually reached the device, so video follows sound exactly instead of
/// running ahead by the depth of the audio buffer.
/// </para>
/// </remarks>
internal sealed unsafe class PlayerWindow : IDisposable
{
    // SDL keycodes. Printable keys use their ASCII value; the rest are scancodes
    // with bit 30 set. Spelling them out avoids depending on binding enum names.
    private const int KeyEscape = 27;
    private const int KeyBackspace = 8;
    private const int KeyTab = 9;
    private const int KeySpace = 32;
    private const int KeyF = 'f';
    private const int ScancodeMask = 1 << 30;
    private const int KeyRight = ScancodeMask | 79;
    private const int KeyLeft = ScancodeMask | 80;
    private const int KeyDown = ScancodeMask | 81;
    private const int KeyUp = ScancodeMask | 82;

    private const ushort AudioS16Lsb = 0x8010;

    /// <summary>How much audio to keep queued. Sets the latency of transport response.</summary>
    private static readonly TimeSpan AudioQueueTarget = TimeSpan.FromMilliseconds(200);

    private readonly VideoNowPlayer _player;
    private readonly Sdl _sdl;
    private readonly Silk.NET.SDL.Window* _window;
    private readonly Renderer* _renderer;
    private readonly Texture* _videoTexture;
    private readonly uint _audioDevice;

    private readonly Queue<PendingFrame> _pending = new();
    private readonly object _pendingGate = new();
    private readonly short[] _mixBuffer = new short[4096];

    private Texture* _overlayTexture;
    private int _overlayWidth;
    private int _overlayHeight;
    private byte[] _overlayPixels = [];
    private string _overlayText = string.Empty;

    private byte[] _displayFrame;
    private long _samplesQueued;
    private float _volume = 0.8f;
    private bool _showOverlay = true;
    private bool _fullscreen;
    private bool _running = true;
    private bool _disposed;

    private readonly record struct PendingFrame(long StartSample, byte[] Rgba);

    public PlayerWindow(VideoNowPlayer player, int scale, bool fullscreen)
    {
        _player = player;
        _displayFrame = (byte[])player.Framebuffer.Clone();

        if (scale < 1) scale = 1;

        _sdl = Sdl.GetApi();
        if (_sdl.Init(Sdl.InitVideo | Sdl.InitAudio) != 0)
            throw new InvalidOperationException($"SDL_Init failed: {_sdl.GetErrorS()}");

        var width = FrameLayout.Width * scale;
        var height = FrameLayout.Height * scale;

        _window = _sdl.CreateWindow(
            "VideoNow XP",
            Sdl.WindowposCentered, Sdl.WindowposCentered,
            width, height,
            (uint)(WindowFlags.Shown | WindowFlags.Resizable));

        if (_window is null)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {_sdl.GetErrorS()}");

        _renderer = _sdl.CreateRenderer(_window, -1, (uint)(RendererFlags.Accelerated | RendererFlags.Presentvsync));
        if (_renderer is null)
            _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);

        if (_renderer is null)
            throw new InvalidOperationException($"SDL_CreateRenderer failed: {_sdl.GetErrorS()}");

        _videoTexture = _sdl.CreateTexture(
            _renderer,
            (uint)PixelFormatEnum.Abgr8888,
            (int)TextureAccess.Streaming,
            FrameLayout.Width, FrameLayout.Height);

        if (_videoTexture is null)
            throw new InvalidOperationException($"SDL_CreateTexture failed: {_sdl.GetErrorS()}");

        var want = new AudioSpec
        {
            Freq = FrameLayout.AudioSampleRate,
            Format = AudioS16Lsb,
            Channels = 1,
            Samples = 1024,
        };

        AudioSpec have;
        _audioDevice = _sdl.OpenAudioDevice((byte*)null, 0, &want, &have, 0);
        if (_audioDevice == 0)
            throw new InvalidOperationException($"SDL_OpenAudioDevice failed: {_sdl.GetErrorS()}");

        _sdl.PauseAudioDevice(_audioDevice, 0);

        _player.FrameDecoded += OnFrameDecoded;

        if (fullscreen) SetFullscreen(true);
        _player.Play();
    }

    public void Run()
    {
        while (_running)
        {
            PumpEvents();
            TopUpAudio();
            PresentNewestReadyFrame();
            Render();
        }
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
        var targetBytes = (long)(AudioQueueTarget.TotalSeconds * FrameLayout.AudioSampleRate) * sizeof(short);

        while (_sdl.GetQueuedAudioSize(_audioDevice) < targetBytes)
        {
            var span = _mixBuffer.AsSpan();
            _player.RenderAudio(span);

            if (_volume < 0.999f)
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = (short)(span[i] * _volume);
            }

            fixed (short* data = _mixBuffer)
            {
                if (_sdl.QueueAudio(_audioDevice, data, (uint)(_mixBuffer.Length * sizeof(short))) != 0)
                    break;
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
            switch ((EventType)e.Type)
            {
                case EventType.Quit:
                    _running = false;
                    break;

                case EventType.Keydown:
                    HandleKey(e.Key.Keysym.Sym);
                    break;
            }
        }
    }

    private void HandleKey(int key)
    {
        switch (key)
        {
            case KeyEscape:
                _running = false;
                break;

            case KeySpace:
                _player.TogglePause();
                break;

            case KeyRight:
                _player.NextTrack();
                ResetAudio();
                break;

            case KeyLeft:
                _player.PreviousTrack();
                ResetAudio();
                break;

            case KeyUp:
                _volume = Math.Min(1f, _volume + 0.1f);
                break;

            case KeyDown:
                _volume = Math.Max(0f, _volume - 0.1f);
                break;

            case KeyBackspace:
                _player.Stop();
                ResetAudio();
                break;

            case KeyTab:
                _showOverlay = !_showOverlay;
                break;

            case KeyF:
                SetFullscreen(!_fullscreen);
                break;

            default:
                if (key >= '1' && key <= '6') _player.PressChoice(key - '1');
                break;
        }
    }

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
        _fullscreen = on;
        _sdl.SetWindowFullscreen(_window, on ? (uint)WindowFlags.FullscreenDesktop : 0);
    }

    private void Render()
    {
        int windowWidth, windowHeight;
        _sdl.GetRendererOutputSize(_renderer, &windowWidth, &windowHeight);

        fixed (byte* pixels = _displayFrame)
            _sdl.UpdateTexture(_videoTexture, (Rectangle<int>*)null, pixels, FrameLayout.Width * 4);

        _sdl.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        _sdl.RenderClear(_renderer);

        var destination = FitPreservingAspect(windowWidth, windowHeight);
        _sdl.RenderCopy(_renderer, _videoTexture, (Rectangle<int>*)null, ref destination);

        if (_showOverlay) RenderOverlay(windowWidth, windowHeight);

        _sdl.RenderPresent(_renderer);
    }

    private static Rectangle<int> FitPreservingAspect(int windowWidth, int windowHeight)
    {
        var scale = Math.Min(
            windowWidth / (double)FrameLayout.Width,
            windowHeight / (double)FrameLayout.Height);

        var width = (int)(FrameLayout.Width * scale);
        var height = (int)(FrameLayout.Height * scale);

        return new Rectangle<int>((windowWidth - width) / 2, (windowHeight - height) / 2, width, height);
    }

    private void RenderOverlay(int windowWidth, int windowHeight)
    {
        var text = BuildStatusText();
        EnsureOverlayTexture(windowWidth, windowHeight);

        if (text != _overlayText)
        {
            _overlayText = text;
            Array.Clear(_overlayPixels);

            var scale = Math.Max(1, windowWidth / 320);
            var lines = text.Split('\n');
            var y = 6;

            foreach (var line in lines)
            {
                BitmapFont.Draw(_overlayPixels, _overlayWidth, _overlayHeight, 6, y, line, scale, 0xF0, 0xF0, 0xF0);
                y += (BitmapFont.GlyphHeight + 3) * scale;
            }

            fixed (byte* pixels = _overlayPixels)
                _sdl.UpdateTexture(_overlayTexture, (Rectangle<int>*)null, pixels, _overlayWidth * 4);
        }

        var destination = new Rectangle<int>(0, 0, _overlayWidth, _overlayHeight);
        _sdl.RenderCopy(_renderer, _overlayTexture, (Rectangle<int>*)null, ref destination);
    }

    private void EnsureOverlayTexture(int windowWidth, int windowHeight)
    {
        if (_overlayTexture is not null && _overlayWidth == windowWidth && _overlayHeight == windowHeight)
            return;

        if (_overlayTexture is not null) _sdl.DestroyTexture(_overlayTexture);

        _overlayWidth = windowWidth;
        _overlayHeight = windowHeight;
        _overlayPixels = new byte[windowWidth * windowHeight * 4];
        _overlayText = string.Empty;

        _overlayTexture = _sdl.CreateTexture(
            _renderer,
            (uint)PixelFormatEnum.Abgr8888,
            (int)TextureAccess.Streaming,
            windowWidth, windowHeight);

        _sdl.SetTextureBlendMode(_overlayTexture, BlendMode.Blend);
    }

    private string BuildStatusText()
    {
        var state = _player.State switch
        {
            TransportState.Playing => "PLAY",
            TransportState.Paused => "PAUSE",
            _ => "STOP",
        };

        var line1 =
            $"{state}  TRACK {_player.CurrentTrack:00}/{_player.Disc.Tracks.Count:00}  " +
            $"{_player.Position:mm\\:ss}/{_player.TrackDuration:mm\\:ss}  VOL {(int)(_volume * 100)}";

        var branches = _player.Branches;
        if (branches.Count == 0) return line1;

        var choices = string.Join("  ", branches.Select(b => $"[{b.Slot + 1}] TRACK {b.Track}"));
        var marker = _player.SelectedChoice >= 0 ? $"  CHOSE {_player.SelectedChoice + 1}" : string.Empty;
        return $"{line1}\nCHOOSE: {choices}{marker}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _player.FrameDecoded -= OnFrameDecoded;

        if (_audioDevice != 0) _sdl.CloseAudioDevice(_audioDevice);
        if (_overlayTexture is not null) _sdl.DestroyTexture(_overlayTexture);
        if (_videoTexture is not null) _sdl.DestroyTexture(_videoTexture);
        if (_renderer is not null) _sdl.DestroyRenderer(_renderer);
        if (_window is not null) _sdl.DestroyWindow(_window);

        _sdl.Quit();
        _sdl.Dispose();
    }
}
