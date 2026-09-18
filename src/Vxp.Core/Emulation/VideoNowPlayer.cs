using Vxp.Discs;
using Vxp.Format;

namespace Vxp.Emulation;

/// <summary>Transport state of a <see cref="VideoNowPlayer"/>.</summary>
public enum TransportState
{
    /// <summary>Nothing is loaded or the disc has ended.</summary>
    Stopped,

    /// <summary>Playing at disc rate.</summary>
    Playing,

    /// <summary>Paused on the current frame.</summary>
    Paused,
}

/// <summary>
/// How the player decides which track follows the one that just ended.
/// </summary>
public enum NavigationPolicy
{
    /// <summary>
    /// Advance in disc order unless the viewer took a branch. Segment kinds that
    /// unambiguously redirect (<see cref="SegmentKind.Hub"/>,
    /// <see cref="SegmentKind.Restart"/>) are still honoured. This is the safe default.
    /// </summary>
    DiscOrder,

    /// <summary>
    /// Also follow register 0x4F as an unconditional "continue with this track" pointer.
    /// The meaning of that register is not fully established, so this is for experimentation.
    /// </summary>
    FollowHeader,
}

/// <summary>What the player does when a choice segment ends and nothing was pressed.</summary>
public enum ChoiceTimeout
{
    /// <summary>Take the first destination the segment offers, as the hardware does.</summary>
    FirstBranch,

    /// <summary>Carry on in disc order and ignore the branch table.</summary>
    DiscOrder,

    /// <summary>Pause on the last frame and wait for the viewer.</summary>
    Wait,
}

/// <summary>What happens when the end of a track or of the disc is reached.</summary>
public enum LoopMode
{
    /// <summary>Play on into the next segment, and stop at the end of the disc.</summary>
    None,

    /// <summary>Repeat the current track for ever.</summary>
    Track,

    /// <summary>Return to the first track when the disc ends.</summary>
    Disc,
}

/// <summary>
/// A VideoNow player: disc transport, the interactive branch logic read out of each
/// segment header, and synchronised picture and sound.
/// </summary>
/// <remarks>
/// <para>
/// Sound is the master clock. The host pulls PCM through <see cref="RenderAudio"/> at
/// <see cref="FrameLayout.AudioSampleRate"/> Hz and the player decodes exactly as many
/// frames as that consumes, so picture and sound cannot drift and playback runs at the
/// true disc rate rather than an approximation of it.
/// </para>
/// </remarks>
public sealed class VideoNowPlayer : IDisposable
{
    /// <summary>Slowest playback rate the player accepts.</summary>
    public const double MinSpeed = 0.25;

    /// <summary>Fastest playback rate the player accepts.</summary>
    public const double MaxSpeed = 8.0;

    private readonly DiscImage _disc;
    private readonly Dictionary<int, TrackReader> _readers = new();
    private readonly Dictionary<int, bool> _blank = new();
    private readonly List<int> _history = new();
    private readonly object _gate = new();

    private TrackReader? _reader;
    private byte[] _frameAudio = [];
    private double _audioPosition;
    private double _speed = 1.0;
    private int _pendingChoiceSlot = -1;
    private bool _disposed;

    /// <summary>Opens a player over an already mounted disc.</summary>
    /// <param name="disc">The disc to play.</param>
    /// <param name="layout">Frame layout; detected from the disc when omitted.</param>
    public VideoNowPlayer(DiscImage disc, FrameLayout? layout = null)
    {
        _disc = disc;
        Layout = layout
                 ?? FormatDetector.Detect(disc)
                 ?? throw new InvalidDataException("No VideoNow video stream was found on this disc.");

        Framebuffer = new byte[VideoDecoder.RgbaFrameBytes];
        SelectTrack(FirstPlayableTrack());
    }

    /// <summary>Opens a player over the disc described by a cue sheet.</summary>
    public static VideoNowPlayer Open(string cuePath) => new(DiscImage.Open(cuePath));

    /// <summary>Frame layout in use.</summary>
    public FrameLayout Layout { get; }

    /// <summary>The mounted disc.</summary>
    public DiscImage Disc => _disc;

    /// <summary>Decoded picture of the current frame, as 144x80 RGBA.</summary>
    public byte[] Framebuffer { get; }

    /// <summary>Current transport state.</summary>
    public TransportState State { get; private set; } = TransportState.Stopped;

    /// <summary>How the player chooses the next track.</summary>
    public NavigationPolicy Navigation { get; set; } = NavigationPolicy.DiscOrder;

    /// <summary>What happens at a choice point when the viewer does nothing.</summary>
    public ChoiceTimeout Timeout { get; set; } = ChoiceTimeout.FirstBranch;

    /// <summary>What happens at the end of a track or of the disc.</summary>
    public LoopMode Loop { get; set; } = LoopMode.None;

    /// <summary>
    /// Playback rate, where 1.0 is the disc's own rate. Audio is resampled to match, so
    /// pitch rises and falls with speed as it would on tape.
    /// </summary>
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, MinSpeed, MaxSpeed);
    }

    /// <summary>Track currently loaded, or 0 if none.</summary>
    public int CurrentTrack => _reader?.TrackNumber ?? 0;

    /// <summary>Frame position within the current track.</summary>
    public int CurrentFrame { get; private set; }

    /// <summary>Number of frames in the current track.</summary>
    public int TrackFrameCount => _reader?.FrameCount ?? 0;

    /// <summary>Header of the frame on screen, or <see langword="null"/> if nothing is loaded.</summary>
    public FrameHeader? CurrentHeader { get; private set; }

    /// <summary>Branch destinations offered by the current segment.</summary>
    public IReadOnlyList<BranchEntry> Branches { get; private set; } = [];

    /// <summary>True while the current segment offers the viewer a choice.</summary>
    public bool IsChoicePoint => Branches.Count > 0;

    /// <summary>Branch slot the viewer has selected for this segment, or -1.</summary>
    public int SelectedChoice => _pendingChoiceSlot;

    /// <summary>Tracks visited so far, most recent last. Drives <see cref="GoBack"/>.</summary>
    public IReadOnlyList<int> History => _history;

    /// <summary>
    /// Total samples produced by <see cref="RenderAudio"/> since the player was created.
    /// </summary>
    /// <remarks>
    /// Read inside <see cref="FrameDecoded"/> this gives the sample index at which the
    /// newly decoded frame begins, which is what a host needs to show each frame at the
    /// moment its audio actually reaches the speakers rather than when it was decoded.
    /// </remarks>
    public long SamplesRendered { get; private set; }

    /// <summary>Position within the current track.</summary>
    public TimeSpan Position => Layout.FrameDuration * CurrentFrame;

    /// <summary>Duration of the current track.</summary>
    public TimeSpan TrackDuration => Layout.FrameDuration * TrackFrameCount;

    /// <summary>Raised on the audio thread whenever a new frame has been decoded into <see cref="Framebuffer"/>.</summary>
    public event Action<VideoNowPlayer>? FrameDecoded;

    /// <summary>Raised when playback moves to a different track.</summary>
    public event Action<VideoNowPlayer>? TrackChanged;

    /// <summary>Raised when a segment starts offering the viewer a choice.</summary>
    public event Action<VideoNowPlayer>? ChoicePresented;

    /// <summary>Begins or resumes playback.</summary>
    public void Play()
    {
        lock (_gate)
        {
            if (_reader is null) return;
            State = TransportState.Playing;
        }
    }

    /// <summary>Pauses on the current frame.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (State == TransportState.Playing) State = TransportState.Paused;
        }
    }

    /// <summary>Toggles between playing and paused.</summary>
    public void TogglePause()
    {
        lock (_gate)
        {
            State = State switch
            {
                TransportState.Playing => TransportState.Paused,
                TransportState.Paused => TransportState.Playing,
                _ => _reader is null ? TransportState.Stopped : TransportState.Playing,
            };
        }
    }

    /// <summary>Stops playback and rewinds to the start of the disc.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            State = TransportState.Stopped;
            _history.Clear();
            SelectTrackCore(FirstPlayableTrack());
        }
    }

    /// <summary>Jumps to the start of <paramref name="trackNumber"/>.</summary>
    public void SelectTrack(int trackNumber)
    {
        lock (_gate) SelectTrackCore(trackNumber);
    }

    /// <summary>Skips to the next track in disc order.</summary>
    public void NextTrack()
    {
        lock (_gate)
        {
            var next = NextPlayableTrack(CurrentTrack);
            if (next is not null) SelectTrackCore(next.Value);
        }
    }

    /// <summary>
    /// Skips to the previous track, or restarts the current one if playback is already
    /// past its start, matching how the skip-back button behaves on the hardware.
    /// </summary>
    public void PreviousTrack()
    {
        lock (_gate)
        {
            if (CurrentFrame > Layout.FrameRate)
            {
                SelectTrackCore(CurrentTrack);
                return;
            }

            var previous = PreviousPlayableTrack(CurrentTrack);
            SelectTrackCore(previous ?? CurrentTrack);
        }
    }

    /// <summary>
    /// Returns to the segment played before this one, which is how you undo a wrong turn
    /// in an interactive title.
    /// </summary>
    /// <returns>True if there was somewhere to go back to.</returns>
    public bool GoBack()
    {
        lock (_gate)
        {
            if (_history.Count == 0) return false;

            var target = _history[^1];
            _history.RemoveAt(_history.Count - 1);

            // SelectTrackCore would push the track we are leaving, undoing the pop.
            var restore = _history.Count;
            SelectTrackCore(target);
            if (_history.Count > restore) _history.RemoveRange(restore, _history.Count - restore);
            return true;
        }
    }

    /// <summary>Moves to <paramref name="frameIndex"/> within the current track.</summary>
    public void SeekToFrame(int frameIndex)
    {
        lock (_gate)
        {
            if (_reader is null) return;

            CurrentFrame = Math.Clamp(frameIndex, 0, Math.Max(0, _reader.FrameCount - 1));
            _frameAudio = [];
            _audioPosition = 0;
            DecodeCurrentFrameForDisplay();
        }
    }

    /// <summary>Moves by <paramref name="seconds"/> within the current track.</summary>
    public void SeekBy(double seconds)
    {
        lock (_gate)
        {
            var frames = (int)Math.Round(seconds * Layout.FrameRate);
            SeekToFrameCore(CurrentFrame + frames);
        }
    }

    /// <summary>
    /// Steps <paramref name="delta"/> frames and pauses, for inspecting a title frame by frame.
    /// </summary>
    public void StepFrame(int delta)
    {
        lock (_gate)
        {
            State = TransportState.Paused;
            SeekToFrameCore(CurrentFrame + delta);
        }
    }

    /// <summary>
    /// Records the viewer choice for the current segment. The jump happens when the
    /// segment ends, which is how these discs are cut: the choice window is the tail of
    /// the segment and each destination is a whole separate track.
    /// </summary>
    /// <param name="slot">Branch slot, 0 through 5.</param>
    /// <returns>True if the current segment offers that slot.</returns>
    public bool PressChoice(int slot)
    {
        lock (_gate)
        {
            foreach (var branch in Branches)
            {
                if (branch.Slot != slot) continue;
                _pendingChoiceSlot = slot;
                return true;
            }

            return false;
        }
    }

    /// <summary>Clears any pending branch selection.</summary>
    public void ClearChoice()
    {
        lock (_gate) _pendingChoiceSlot = -1;
    }

    /// <summary>
    /// Takes a branch at once rather than waiting for the segment to end, for viewers who
    /// would rather not sit through the rest of the scene.
    /// </summary>
    public bool TakeChoiceNow(int slot)
    {
        lock (_gate)
        {
            foreach (var branch in Branches)
            {
                if (branch.Slot != slot) continue;
                if (!LoadTrack(branch.Track, remember: true)) return false;
                DecodeCurrentFrameForDisplay();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with mono 16-bit PCM at
    /// <see cref="FrameLayout.AudioSampleRate"/> Hz, advancing playback by exactly that
    /// much time. Writes silence while paused or stopped.
    /// </summary>
    /// <returns>The number of samples written, always the length of the destination.</returns>
    public int RenderAudio(Span<short> destination)
    {
        lock (_gate)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                if (State != TransportState.Playing) return Silence(destination, i);

                // At speeds above 1.0 a single output sample can step past a whole frame.
                while (_audioPosition >= _frameAudio.Length)
                {
                    var consumed = _frameAudio.Length;
                    if (!AdvanceFrame()) return Silence(destination, i);
                    _audioPosition -= consumed;
                }

                destination[i] = SampleAt(_audioPosition);
                _audioPosition += _speed;
                SamplesRendered++;
            }

            return destination.Length;
        }
    }

    private int Silence(Span<short> destination, int from)
    {
        destination[from..].Clear();
        SamplesRendered += destination.Length - from;
        return destination.Length;
    }

    /// <summary>Reads the audio stream at a fractional position, interpolating between samples.</summary>
    private short SampleAt(double position)
    {
        if (_frameAudio.Length == 0) return 0;

        var index = Math.Clamp((int)position, 0, _frameAudio.Length - 1);
        var current = (_frameAudio[index] - 128) << 8;

        // The next sample lives in the following frame at a frame boundary; holding the
        // current value there costs at most one sample of flatness per frame.
        var next = index + 1 < _frameAudio.Length ? (_frameAudio[index + 1] - 128) << 8 : current;

        return (short)(current + (next - current) * (position - index));
    }

    private void SeekToFrameCore(int frameIndex)
    {
        if (_reader is null) return;

        CurrentFrame = Math.Clamp(frameIndex, 0, Math.Max(0, _reader.FrameCount - 1));
        _frameAudio = [];
        _audioPosition = 0;
        DecodeCurrentFrameForDisplay();
    }

    private bool AdvanceFrame()
    {
        if (_reader is null) return false;

        if (CurrentFrame >= _reader.FrameCount && !GoToNextSegment()) return false;

        var frame = _reader!.ReadFrame(CurrentFrame);
        if (frame is null)
        {
            if (!GoToNextSegment()) return false;
            frame = _reader!.ReadFrame(CurrentFrame);
            if (frame is null)
            {
                State = TransportState.Stopped;
                return false;
            }
        }

        CurrentFrame++;
        ApplyFrame(frame);
        _frameAudio = frame.Audio;
        return true;
    }

    private void ApplyFrame(VideoNowFrame frame)
    {
        var hadChoice = Branches.Count > 0;

        CurrentHeader = frame.ReadHeader();
        Branches = CurrentHeader.OffersChoice ? CurrentHeader.Branches : [];
        if (Branches.Count == 0) _pendingChoiceSlot = -1;

        VideoDecoder.DecodeRgba(frame.PixelData, Framebuffer);

        FrameDecoded?.Invoke(this);
        if (!hadChoice && Branches.Count > 0) ChoicePresented?.Invoke(this);
    }

    /// <summary>Applies the branch logic at the end of a segment. Returns false when playback ends.</summary>
    private bool GoToNextSegment()
    {
        if (Loop == LoopMode.Track)
        {
            CurrentFrame = 0;
            return true;
        }

        var header = CurrentHeader;

        // A viewer choice always wins.
        if (_pendingChoiceSlot >= 0 && header is not null)
        {
            foreach (var branch in header.Branches)
            {
                if (branch.Slot == _pendingChoiceSlot && branch.Track != 0 && LoadTrack(branch.Track, remember: true))
                    return true;
            }
        }

        switch (header?.Kind ?? SegmentKind.None)
        {
            case SegmentKind.Terminal:
                return EndOfPlayback();

            case SegmentKind.Hub:
            case SegmentKind.Restart:
                if (header is { ContinueTrack: > 0 } && LoadTrack(header.ContinueTrack, remember: true)) return true;
                break;

            case SegmentKind.Choice:
            case SegmentKind.TaggedChoice:
                if (Timeout == ChoiceTimeout.Wait)
                {
                    CurrentFrame = Math.Max(0, TrackFrameCount - 1);
                    State = TransportState.Paused;
                    return false;
                }

                if (Timeout == ChoiceTimeout.FirstBranch
                    && header is not null
                    && header.Branches.Count > 0
                    && LoadTrack(header.Branches[0].Track, remember: true))
                {
                    return true;
                }

                break;
        }

        if (Navigation == NavigationPolicy.FollowHeader
            && header is { ContinueTrack: > 0 }
            && LoadTrack(header.ContinueTrack, remember: true))
        {
            return true;
        }

        var next = NextPlayableTrack(CurrentTrack);
        if (next is not null && LoadTrack(next.Value, remember: true)) return true;

        return EndOfPlayback();
    }

    private bool EndOfPlayback()
    {
        if (Loop == LoopMode.Disc && LoadTrack(FirstPlayableTrack(), remember: false)) return true;

        State = TransportState.Stopped;
        return false;
    }

    private bool LoadTrack(int trackNumber, bool remember)
    {
        var reader = GetReader(trackNumber);
        if (reader is null || reader.FrameCount == 0) return false;

        var previous = _reader?.TrackNumber ?? 0;
        var changed = reader.TrackNumber != previous;

        if (remember && changed && previous != 0)
        {
            _history.Add(previous);
            if (_history.Count > 256) _history.RemoveAt(0);
        }

        _reader = reader;
        CurrentFrame = 0;
        _pendingChoiceSlot = -1;
        _frameAudio = [];
        _audioPosition = 0;

        if (changed) TrackChanged?.Invoke(this);
        return true;
    }

    private void SelectTrackCore(int trackNumber)
    {
        if (!LoadTrack(trackNumber, remember: true)) return;
        DecodeCurrentFrameForDisplay();
    }

    /// <summary>Decodes the current frame into the framebuffer without consuming its audio.</summary>
    private void DecodeCurrentFrameForDisplay()
    {
        var frame = _reader?.ReadFrame(CurrentFrame);
        if (frame is null) return;

        ApplyFrame(frame);
    }

    private TrackReader? GetReader(int trackNumber)
    {
        if (_readers.TryGetValue(trackNumber, out var cached)) return cached;

        var track = _disc.FindTrack(trackNumber);
        if (track is null) return null;

        // Tracks holding no VideoNow stream (padding at the end of a disc) are skipped.
        var layout = FormatDetector.Detect(track);
        if (layout is null) return null;

        var reader = new TrackReader(track, layout);
        _readers[trackNumber] = reader;
        return reader;
    }

    /// <summary>True if the track carries a VideoNow stream rather than padding.</summary>
    public bool IsPlayable(int trackNumber) => GetReader(trackNumber)?.FrameCount > 0;

    /// <summary>
    /// True for a plain linear segment in which no frame carries any picture or sound.
    /// </summary>
    /// <remarks>
    /// Mastering leaves these between the title sequence and the first real segment: on
    /// <i>Batman vs The Joker</i> tracks 3 and 4 are 24 seconds of black silence, and the
    /// title's own register 0x4F steps straight over them to track 5. Disc order and track
    /// skipping pass over them as they pass over fill; selecting one directly still plays it.
    /// </remarks>
    public bool IsBlank(int trackNumber)
    {
        if (_blank.TryGetValue(trackNumber, out var blank)) return blank;

        blank = ScanForBlank(GetReader(trackNumber));
        _blank[trackNumber] = blank;
        return blank;
    }

    private static bool ScanForBlank(TrackReader? reader)
    {
        if (reader is null || reader.FrameCount == 0) return false;

        for (var i = 0; i < reader.FrameCount; i++)
        {
            var frame = reader.ReadFrame(i);
            if (frame is null) return false;

            // Anything that redirects or offers a choice matters even with nothing to show.
            if (i == 0 && frame.ReadHeader().Kind != SegmentKind.Linear) return false;

            if (frame.PixelData.ContainsAnyExcept((byte)0x00)) return false;
            if (frame.Audio.AsSpan().ContainsAnyExcept((byte)0x80)) return false;
        }

        return true;
    }

    /// <summary>True if disc order and track skipping should stop at this track.</summary>
    private bool IsWorthVisiting(int trackNumber) => IsPlayable(trackNumber) && !IsBlank(trackNumber);

    private int FirstPlayableTrack()
    {
        foreach (var track in _disc.Tracks)
            if (IsWorthVisiting(track.Number)) return track.Number;

        foreach (var track in _disc.Tracks)
            if (IsPlayable(track.Number)) return track.Number;

        return _disc.Tracks.Count > 0 ? _disc.Tracks[0].Number : 1;
    }

    private int IndexOfTrack(int trackNumber)
    {
        for (var i = 0; i < _disc.Tracks.Count; i++)
            if (_disc.Tracks[i].Number == trackNumber) return i;

        return -1;
    }

    private int? NextPlayableTrack(int trackNumber)
    {
        var index = IndexOfTrack(trackNumber);
        if (index < 0) return null;

        for (var i = index + 1; i < _disc.Tracks.Count; i++)
            if (IsWorthVisiting(_disc.Tracks[i].Number)) return _disc.Tracks[i].Number;

        return null;
    }

    private int? PreviousPlayableTrack(int trackNumber)
    {
        var index = IndexOfTrack(trackNumber);
        if (index < 0) return null;

        for (var i = index - 1; i >= 0; i--)
            if (IsWorthVisiting(_disc.Tracks[i].Number)) return _disc.Tracks[i].Number;

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disc.Dispose();
    }
}
