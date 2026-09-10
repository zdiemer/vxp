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
    private readonly DiscImage _disc;
    private readonly Dictionary<int, TrackReader> _readers = new();
    private readonly object _gate = new();

    private TrackReader? _reader;
    private byte[] _audioTail = [];
    private int _audioTailPosition;
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
        SelectTrack(disc.Tracks.Count > 0 ? disc.Tracks[0].Number : 1);
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

    /// <summary>
    /// Total samples produced by <see cref="RenderAudio"/> since the player was created.
    /// </summary>
    /// <remarks>
    /// Read inside <see cref="FrameDecoded"/> this gives the sample index at which the
    /// newly decoded frame begins, which is what a host needs to show each frame at the
    /// moment its audio actually reaches the speakers rather than when it was decoded.
    /// </remarks>
    public long SamplesRendered { get; private set; }

    /// <summary>Raised on the audio thread whenever a new frame has been decoded into <see cref="Framebuffer"/>.</summary>
    public event Action<VideoNowPlayer>? FrameDecoded;

    /// <summary>Raised when playback moves to a different track.</summary>
    public event Action<VideoNowPlayer>? TrackChanged;

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
            SelectTrackCore(_disc.Tracks.Count > 0 ? _disc.Tracks[0].Number : 1);
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
            var next = NextTrackInDiscOrder(CurrentTrack);
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

            var index = IndexOfTrack(CurrentTrack);
            if (index > 0) SelectTrackCore(_disc.Tracks[index - 1].Number);
            else SelectTrackCore(CurrentTrack);
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
            var offered = false;
            foreach (var branch in Branches)
            {
                if (branch.Slot == slot) { offered = true; break; }
            }

            if (!offered) return false;
            _pendingChoiceSlot = slot;
            return true;
        }
    }

    /// <summary>Clears any pending branch selection.</summary>
    public void ClearChoice()
    {
        lock (_gate) _pendingChoiceSlot = -1;
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
            var written = 0;

            while (written < destination.Length)
            {
                if (State != TransportState.Playing)
                {
                    destination[written..].Clear();
                    SamplesRendered += destination.Length - written;
                    return destination.Length;
                }

                if (_audioTailPosition >= _audioTail.Length && !AdvanceFrame())
                {
                    destination[written..].Clear();
                    SamplesRendered += destination.Length - written;
                    return destination.Length;
                }

                var available = _audioTail.Length - _audioTailPosition;
                var take = Math.Min(available, destination.Length - written);
                AudioDecoder.DecodePcm16(
                    _audioTail.AsSpan(_audioTailPosition, take),
                    destination.Slice(written, take));

                _audioTailPosition += take;
                written += take;
                SamplesRendered += take;
            }

            return written;
        }
    }

    /// <summary>Position within the current track.</summary>
    public TimeSpan Position => Layout.FrameDuration * CurrentFrame;

    /// <summary>Duration of the current track.</summary>
    public TimeSpan TrackDuration => Layout.FrameDuration * TrackFrameCount;

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

        CurrentHeader = frame.ReadHeader();
        Branches = CurrentHeader.OffersChoice ? CurrentHeader.Branches : [];
        if (Branches.Count == 0) _pendingChoiceSlot = -1;

        VideoDecoder.DecodeRgba(frame.PixelData, Framebuffer);
        _audioTail = frame.Audio;
        _audioTailPosition = 0;

        FrameDecoded?.Invoke(this);
        return true;
    }

    /// <summary>Applies the branch logic at the end of a segment. Returns false when the disc ends.</summary>
    private bool GoToNextSegment()
    {
        var header = CurrentHeader;
        var kind = header?.Kind ?? SegmentKind.None;

        // A viewer choice always wins.
        if (_pendingChoiceSlot >= 0 && header is not null)
        {
            foreach (var branch in header.Branches)
            {
                if (branch.Slot == _pendingChoiceSlot && branch.Track != 0 && LoadTrack(branch.Track))
                    return true;
            }
        }

        switch (kind)
        {
            case SegmentKind.Terminal:
                State = TransportState.Stopped;
                return false;

            case SegmentKind.Hub:
            case SegmentKind.Restart:
                if (header is { ContinueTrack: > 0 } && LoadTrack(header.ContinueTrack)) return true;
                break;

            case SegmentKind.Choice:
            case SegmentKind.TaggedChoice:
                // No choice was made: fall through to the first offered destination so
                // the story still progresses, as the hardware does on a timeout.
                if (header is not null && header.Branches.Count > 0 && LoadTrack(header.Branches[0].Track))
                    return true;
                break;
        }

        if (Navigation == NavigationPolicy.FollowHeader
            && header is { ContinueTrack: > 0 }
            && LoadTrack(header.ContinueTrack))
        {
            return true;
        }

        var next = NextTrackInDiscOrder(CurrentTrack);
        if (next is not null && LoadTrack(next.Value)) return true;

        State = TransportState.Stopped;
        return false;
    }

    private bool LoadTrack(int trackNumber)
    {
        var reader = GetReader(trackNumber);
        if (reader is null || reader.FrameCount == 0) return false;

        var changed = reader.TrackNumber != _reader?.TrackNumber;
        _reader = reader;
        CurrentFrame = 0;
        _pendingChoiceSlot = -1;
        _audioTail = [];
        _audioTailPosition = 0;

        if (changed) TrackChanged?.Invoke(this);
        return true;
    }

    private void SelectTrackCore(int trackNumber)
    {
        if (!LoadTrack(trackNumber)) return;

        // Decode the first frame straight away so the picture is correct while paused.
        var frame = _reader!.ReadFrame(0);
        if (frame is null) return;

        CurrentHeader = frame.ReadHeader();
        Branches = CurrentHeader.OffersChoice ? CurrentHeader.Branches : [];
        VideoDecoder.DecodeRgba(frame.PixelData, Framebuffer);
        FrameDecoded?.Invoke(this);
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

    private int IndexOfTrack(int trackNumber)
    {
        for (var i = 0; i < _disc.Tracks.Count; i++)
            if (_disc.Tracks[i].Number == trackNumber) return i;
        return -1;
    }

    private int? NextTrackInDiscOrder(int trackNumber)
    {
        var index = IndexOfTrack(trackNumber);
        if (index < 0 || index + 1 >= _disc.Tracks.Count) return null;
        return _disc.Tracks[index + 1].Number;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disc.Dispose();
    }
}
