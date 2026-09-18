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
    /// Advance in disc order unless the viewer took a branch, ignoring where the disc
    /// says to go. Useful for watching every segment of a title in turn.
    /// </summary>
    DiscOrder,

    /// <summary>
    /// Go where the disc says: the track named by register 0x4F, then any tracks queued
    /// by the branch that was taken, then the score branch of a tagged segment, and only
    /// then disc order. This is how the titles are meant to play, and the default.
    /// </summary>
    FollowHeader,
}

/// <summary>What the player does when a choice segment ends and nothing was pressed.</summary>
public enum ChoiceTimeout
{
    /// <summary>
    /// Take the first destination the segment offers. This answers every quiz question
    /// with key 1, so it is no longer the default.
    /// </summary>
    FirstBranch,

    /// <summary>Carry on in disc order and ignore the branch table.</summary>
    DiscOrder,

    /// <summary>
    /// Hold on the segment until a key is pressed, playing it again from the start as a
    /// segment naming itself in register 0x4F does. The default.
    /// </summary>
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
/// <see cref="FrameLayout.PlaybackSampleRate"/> Hz and the player decodes exactly as many
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
    private readonly List<int> _followOn = new();
    private BranchEntry? _pendingChoice;
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
    public NavigationPolicy Navigation { get; set; } = NavigationPolicy.FollowHeader;

    /// <summary>What happens at a choice point when the viewer does nothing.</summary>
    public ChoiceTimeout Timeout { get; set; } = ChoiceTimeout.Wait;

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

    /// <summary>
    /// Branch table of the frame on screen. It can change within a segment: timed
    /// prompts are runs of frames with a table of their own.
    /// </summary>
    public IReadOnlyList<BranchEntry> Branches { get; private set; } = [];

    /// <summary>
    /// True while the frame on screen puts a choice to the viewer: a choice segment, or a
    /// prompt within any other. A lone entry in the last slot is the standing "back to
    /// the menu" button that most segments carry, and does not count.
    /// </summary>
    public bool IsChoicePoint =>
        CurrentHeader is { OffersChoice: true }
            ? Branches.Count > 0
            : Branches.Any(b => b.Slot != FrameHeader.BranchEntryCount - 1);

    /// <summary>Branch slot the viewer has selected for this segment, or -1.</summary>
    public int SelectedChoice => _pendingChoice?.Slot ?? -1;

    /// <summary>Score before any segment has changed it.</summary>
    public const int InitialScore = 0x64;

    /// <summary>
    /// The score that <see cref="SegmentKind.ScoreUp"/>, <see cref="SegmentKind.ScoreDown"/>
    /// and <see cref="SegmentKind.ScoreReset"/> segments change and
    /// <see cref="SegmentKind.TaggedChoice"/> segments branch on.
    /// </summary>
    public int Score { get; private set; } = InitialScore;

    /// <summary>Tracks queued to play once the current one ends, from the branch that led here.</summary>
    public IReadOnlyList<int> FollowOn => _followOn;

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
            Score = InitialScore;
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
            if (CurrentFrame > Layout.PlaybackFrameRate)
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
            var frames = (int)Math.Round(seconds * Layout.PlaybackFrameRate);
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
    /// Records the viewer choice for the current segment, to be taken when the segment
    /// ends. The discs read as though the hardware jumps on the press instead, which is
    /// <see cref="TakeChoiceNow"/>; this is the older, deferred behaviour, kept for
    /// scripts and for viewers who would rather see each scene out.
    /// </summary>
    /// <remarks>
    /// The entry is looked up in the frame on screen when the button is pressed, because
    /// tables change within a segment: a timed prompt answers one way inside its window
    /// and another way once the window has passed.
    /// </remarks>
    /// <param name="slot">Branch slot, 0 through 5.</param>
    /// <returns>True if the frame on screen offers that slot.</returns>
    public bool PressChoice(int slot)
    {
        lock (_gate)
        {
            foreach (var branch in Branches)
            {
                if (branch.Slot != slot) continue;
                _pendingChoice = branch;
                return true;
            }

            return false;
        }
    }

    /// <summary>Clears any pending branch selection.</summary>
    public void ClearChoice()
    {
        lock (_gate) _pendingChoice = null;
    }

    /// <summary>
    /// Takes a branch at once rather than waiting for the segment to end. This is how the
    /// discs are cut to be played: a timed prompt's success clip picks up the picture from
    /// the prompt window, not from the end of the segment, and the menu key on an episode
    /// several minutes long is meant to answer when it is pressed. See <c>docs/format.md</c>.
    /// </summary>
    public bool TakeChoiceNow(int slot)
    {
        lock (_gate)
        {
            foreach (var branch in Branches)
            {
                if (branch.Slot != slot) continue;
                if (!TakeBranch(branch)) return false;
                DecodeCurrentFrameForDisplay();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with mono 16-bit PCM at
    /// <see cref="FrameLayout.PlaybackSampleRate"/> Hz, advancing playback by exactly that
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
                // The frame just finished comes off the position before the next is read,
                // because moving to another segment starts the position again from zero;
                // taking it off afterwards left the new segment a frame in arrears, reading
                // before its first sample.
                while (_audioPosition >= _frameAudio.Length)
                {
                    _audioPosition -= _frameAudio.Length;
                    if (!AdvanceFrame()) return Silence(destination, i);
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
        if (frame.FrameIndex == 0) ApplyScore(CurrentHeader!.Kind);
        _frameAudio = frame.Audio;
        return true;
    }

    private void ApplyFrame(VideoNowFrame frame)
    {
        var hadChoice = IsChoicePoint;

        CurrentHeader = frame.ReadHeader();
        Branches = CurrentHeader.Branches;

        VideoDecoder.DecodeRgba(frame.PixelData, Framebuffer);

        FrameDecoded?.Invoke(this);
        if (!hadChoice && IsChoicePoint) ChoicePresented?.Invoke(this);
    }

    /// <summary>Applies a segment's effect on the score as it starts playing.</summary>
    private void ApplyScore(SegmentKind kind)
    {
        Score = kind switch
        {
            SegmentKind.ScoreUp => Math.Min(Score + 1, byte.MaxValue),
            SegmentKind.ScoreDown => Math.Max(Score - 1, 0),
            SegmentKind.ScoreReset => InitialScore,
            _ => Score,
        };
    }

    /// <summary>Jumps to a branch's destination and queues the rest of its play list.</summary>
    private bool TakeBranch(BranchEntry branch)
    {
        if (!LoadTrack(branch.Track, remember: true)) return false;

        _followOn.Clear();
        _followOn.AddRange(branch.FollowOn);
        return true;
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
        if (_pendingChoice is { } chosen && TakeBranch(chosen)) return true;

        if (Navigation == NavigationPolicy.FollowHeader && header is not null)
        {
            // Register 0x4F names the next track outright; naming itself makes the segment
            // repeat until the viewer acts. It is never set on choice segments.
            if (header.ContinueTrack > 0 && LoadTrack(header.ContinueTrack, remember: true)) return true;

            // Then whatever the branch that led here queued behind it.
            while (_followOn.Count > 0)
            {
                var queued = _followOn[0];
                _followOn.RemoveAt(0);
                if (LoadTrack(queued, remember: true)) return true;
            }

            // A tagged segment is not a question for the viewer but a test of the score:
            // the first entry whose threshold the score meets is taken.
            if (header.Kind == SegmentKind.TaggedChoice)
            {
                foreach (var branch in header.Branches)
                    if (Score >= branch.Tag && TakeBranch(branch)) return true;
            }
        }

        if (header is { OffersChoice: true })
        {
            // A question left unanswered is asked again, the way a segment naming itself
            // in 0x4F repeats: picture and sound play on from the top rather than freezing.
            // Only a Choice asks the viewer anything; a tagged segment whose score bands
            // all missed has nothing to wait for and carries on in disc order.
            if (Timeout == ChoiceTimeout.Wait
                && header.Kind == SegmentKind.Choice
                && LoadTrack(CurrentTrack, remember: false))
            {
                return true;
            }

            if (Timeout == ChoiceTimeout.FirstBranch
                && header.Branches.Count > 0
                && TakeBranch(header.Branches[0]))
            {
                return true;
            }
        }

        var next = NextPlayableTrack(CurrentTrack);
        if (next is not null && LoadTrack(next.Value, remember: true)) return true;

        return EndOfPlayback();
    }

    private bool EndOfPlayback()
    {
        if (Loop == LoopMode.Disc && LoadTrack(FirstPlayableTrack(), remember: false))
        {
            _followOn.Clear();
            Score = InitialScore;
            return true;
        }

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
        _pendingChoice = null;
        _frameAudio = [];
        _audioPosition = 0;

        if (changed) TrackChanged?.Invoke(this);
        return true;
    }

    /// <summary>Moves to a track at the viewer's request, which abandons any queued play list.</summary>
    private void SelectTrackCore(int trackNumber)
    {
        if (!LoadTrack(trackNumber, remember: true)) return;
        _followOn.Clear();
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
    /// See <see cref="TrackReader.IsBlank"/>. Disc order and track skipping pass over these
    /// as they pass over fill; selecting one directly still plays it.
    /// </remarks>
    public bool IsBlank(int trackNumber)
    {
        if (_blank.TryGetValue(trackNumber, out var blank)) return blank;

        blank = GetReader(trackNumber)?.IsBlank() == true;
        _blank[trackNumber] = blank;
        return blank;
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
