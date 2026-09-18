using Vxp.Discs;

namespace Vxp.Format;

/// <summary>A single de-interleaved VideoNow frame.</summary>
public sealed class VideoNowFrame
{
    internal VideoNowFrame(int trackNumber, int frameIndex, byte[] video, byte[] audio, FrameLayout layout)
        : this(trackNumber, frameIndex, video, audio, layout, layout.HeaderBytes)
    {
    }

    internal VideoNowFrame(int trackNumber, int frameIndex, byte[] video, byte[] audio, FrameLayout layout, int pictureOffset)
    {
        TrackNumber = trackNumber;
        FrameIndex = frameIndex;
        Video = video;
        Audio = audio;
        Layout = layout;
        PictureOffset = pictureOffset;
    }

    /// <summary>Track this frame came from.</summary>
    public int TrackNumber { get; }

    /// <summary>Zero-based position of this frame within the track, as counted by the reader.</summary>
    public int FrameIndex { get; }

    /// <summary>Layout used to decode the frame.</summary>
    public FrameLayout Layout { get; }

    /// <summary>De-interleaved video bytes: header followed by packed pixel data.</summary>
    public byte[] Video { get; }

    /// <summary>
    /// De-interleaved audio bytes: unsigned 8-bit mono samples. Colour and XP frames always
    /// carry <see cref="FrameLayout.AudioBytes"/>; a black and white frame carries the sound
    /// up to the next frame, which at a track's edges includes that of any partial frame.
    /// </summary>
    public byte[] Audio { get; }

    /// <summary>Where the packed picture starts within <see cref="Video"/>.</summary>
    public int PictureOffset { get; }

    /// <summary>Packed pixel data with the header removed.</summary>
    public ReadOnlySpan<byte> PixelData => Video.AsSpan(PictureOffset, Layout.PictureBytes);

    /// <summary>Parses this frame's display-controller header.</summary>
    public FrameHeader ReadHeader() => FrameHeader.Parse(Video, Layout);

    /// <summary>Decodes this frame's picture to RGBA, <see cref="FrameLayout.RgbaBytes"/> long.</summary>
    public byte[] DecodeRgba()
    {
        var rgba = new byte[Layout.RgbaBytes];
        VideoDecoder.Decode(this, rgba);
        return rgba;
    }
}

/// <summary>
/// Reads frames sequentially from one track of a mounted disc, de-interleaving the
/// stream as it goes.
/// </summary>
public sealed class TrackReader
{
    private readonly DiscTrack _track;
    private readonly byte[] _stream;
    private readonly byte[] _video;
    private readonly byte[] _audio;
    private readonly BlackAndWhiteStream.FrameIndex? _index;

    /// <summary>Opens <paramref name="track"/> for reading using <paramref name="layout"/>.</summary>
    /// <remarks>
    /// A black and white track is scanned end to end to find its frames, since they do not
    /// sit on a fixed grid (see <see cref="BlackAndWhiteStream.IndexFrames"/>).
    /// </remarks>
    public TrackReader(DiscTrack track, FrameLayout layout)
    {
        _track = track;
        Layout = layout;

        if (layout.Monochrome)
        {
            _index = BlackAndWhiteStream.IndexFrames(track);
            FrameCount = _index.Count;
            StartOffset = FrameCount > 0 ? _index.Starts[0] : 0;

            _stream = [];
            _video = [];
            _audio = [];
            return;
        }

        StartOffset = FindFirstFrameOffset(track, layout);
        FrameCount = (int)Math.Max(0, (track.ByteLength - StartOffset) / layout.StreamBytes);

        _stream = new byte[layout.StreamBytes];
        _video = new byte[layout.VideoBytes];
        _audio = new byte[layout.AudioBytes];
    }

    /// <summary>Layout in use for this track.</summary>
    public FrameLayout Layout { get; }

    /// <summary>Byte offset within the track at which the first complete frame begins.</summary>
    public long StartOffset { get; }

    /// <summary>Number of whole frames available in the track.</summary>
    public int FrameCount { get; }

    /// <summary>Track number being read.</summary>
    public int TrackNumber => _track.Number;

    /// <summary>Duration of the track at the layout's playback rate.</summary>
    public TimeSpan Duration => Layout.FrameDuration * FrameCount;

    /// <summary>
    /// True for a plain linear segment in which no frame carries any picture or sound:
    /// all-zero pixels and audio pinned at 0x80, under an ordinary header.
    /// </summary>
    /// <remarks>
    /// Mastering leaves these between the title sequence and the first real segment: on
    /// <i>Batman vs The Joker</i> tracks 3 and 4 are 217 frames of black silence, and the
    /// title's own register 0x4F steps straight over them to track 5. A segment that
    /// offers a choice or redirects matters even with nothing to show, so only
    /// <see cref="SegmentKind.Linear"/> counts. The scan stops at the first frame with
    /// anything in it, so on a track with content it costs a frame or two. Black and white
    /// frames have no header, so no black and white track counts.
    /// </remarks>
    public bool IsBlank()
    {
        if (FrameCount == 0) return false;

        for (var i = 0; i < FrameCount; i++)
        {
            var frame = ReadFrame(i);
            if (frame is null) return false;

            if (i == 0 && frame.ReadHeader().Kind != SegmentKind.Linear) return false;

            if (frame.PixelData.ContainsAnyExcept((byte)0x00)) return false;
            if (frame.Audio.AsSpan().ContainsAnyExcept((byte)0x80)) return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the frame at <paramref name="frameIndex"/>, or <see langword="null"/> past the end
    /// of the track. The returned buffers are freshly allocated and safe to retain.
    /// </summary>
    public VideoNowFrame? ReadFrame(int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= FrameCount) return null;
        if (Layout.Monochrome) return ReadBlackAndWhiteFrame(frameIndex);

        var offset = StartOffset + (long)frameIndex * Layout.StreamBytes;
        var read = _track.Read(offset, _stream);
        if (read < Layout.StreamBytes) return null;

        Deinterleave(_stream, _video, _audio);

        return new VideoNowFrame(
            _track.Number,
            frameIndex,
            _video.AsSpan(0, Layout.VideoBytes).ToArray(),
            _audio.AsSpan(0, Layout.AudioBytes).ToArray(),
            Layout);
    }

    /// <summary>
    /// Reads only the header of the frame at <paramref name="frameIndex"/>, which is a
    /// small fraction of the frame and all that surveying a track's branch tables needs.
    /// </summary>
    public FrameHeader? ReadHeader(int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= FrameCount) return null;
        if (Layout.Monochrome) return FrameHeader.Parse([], Layout);

        var groups = (Layout.HeaderBytes + FrameLayout.VideoBytesPerGroup - 1) / FrameLayout.VideoBytesPerGroup;
        var stream = _stream.AsSpan(0, groups * FrameLayout.GroupBytes);

        var offset = StartOffset + (long)frameIndex * Layout.StreamBytes;
        if (_track.Read(offset, stream) < stream.Length) return null;

        Deinterleave(stream, _video, _audio);
        return FrameHeader.Parse(_video, Layout);
    }

    /// <summary>Splits an interleaved Color or XP frame into its video and audio halves.</summary>
    public static void Deinterleave(ReadOnlySpan<byte> stream, Span<byte> video, Span<byte> audio)
    {
        var groups = stream.Length / FrameLayout.GroupBytes;
        for (var g = 0; g < groups; g++)
        {
            var source = g * FrameLayout.GroupBytes;
            stream.Slice(source, FrameLayout.VideoBytesPerGroup)
                  .CopyTo(video[(g * FrameLayout.VideoBytesPerGroup)..]);
            audio[g] = stream[source + FrameLayout.VideoBytesPerGroup];
        }
    }

    /// <summary>
    /// Reads a black and white frame from the index. Its sound runs to the start of the
    /// next frame, and the first and last frames of the track also take the sound beyond
    /// them, of padding or of a frame cut at the track boundary, so a programme cut across
    /// tracks loses none.
    /// </summary>
    private VideoNowFrame? ReadBlackAndWhiteFrame(int frameIndex)
    {
        const int group = BlackAndWhiteStream.GroupBytes;
        var index = _index!;

        var start = index.Starts[frameIndex];
        var picture = start + index.PictureOffsets[frameIndex];
        var pictureEnd = picture + BlackAndWhiteStream.PictureGroups * group;

        var from = frameIndex == 0 ? index.Alignment : start;
        var to = frameIndex + 1 < index.Count
            ? index.Starts[frameIndex + 1]
            : index.Alignment + (_track.ByteLength - index.Alignment) / group * group;

        var bytes = new byte[(int)(to - from)];
        if (_track.Read(from, bytes) < bytes.Length) return null;

        var audio = new byte[bytes.Length / group];
        BlackAndWhiteStream.Deinterleave(bytes, [], audio);

        var video = new byte[(pictureEnd - start) / group * BlackAndWhiteStream.VideoBytesPerGroup];
        BlackAndWhiteStream.Deinterleave(bytes.AsSpan((int)(start - from), (int)(pictureEnd - start)), video, []);

        var pictureOffset = (int)(picture - start) / group * BlackAndWhiteStream.VideoBytesPerGroup;
        return new VideoNowFrame(_track.Number, frameIndex, video, audio, Layout, pictureOffset);
    }

    /// <summary>
    /// Locates the first frame boundary in a track by searching for the sync word.
    /// Ripped tracks normally start exactly on a frame, but a leading pregap would shift it.
    /// </summary>
    private static long FindFirstFrameOffset(DiscTrack track, FrameLayout layout)
    {
        var window = new byte[Math.Min(layout.StreamBytes * 2, (int)Math.Min(track.ByteLength, int.MaxValue))];
        var read = track.Read(0, window);
        var index = window.AsSpan(0, read).IndexOf(FrameLayout.SyncWord);
        return index < 0 ? 0 : index;
    }
}
