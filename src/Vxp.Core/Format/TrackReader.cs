using Vxp.Discs;

namespace Vxp.Format;

/// <summary>A single de-interleaved VideoNow frame.</summary>
public sealed class VideoNowFrame
{
    internal VideoNowFrame(int trackNumber, int frameIndex, byte[] video, byte[] audio, FrameLayout layout)
    {
        TrackNumber = trackNumber;
        FrameIndex = frameIndex;
        Video = video;
        Audio = audio;
        Layout = layout;
    }

    /// <summary>Track this frame came from.</summary>
    public int TrackNumber { get; }

    /// <summary>Zero-based position of this frame within the track, as counted by the reader.</summary>
    public int FrameIndex { get; }

    /// <summary>Layout used to decode the frame.</summary>
    public FrameLayout Layout { get; }

    /// <summary>De-interleaved video bytes: header followed by packed pixel data.</summary>
    public byte[] Video { get; }

    /// <summary>De-interleaved audio bytes: unsigned 8-bit mono samples.</summary>
    public byte[] Audio { get; }

    /// <summary>Packed pixel data with the header removed.</summary>
    public ReadOnlySpan<byte> PixelData => Video.AsSpan(Layout.HeaderBytes, FrameLayout.PixelBytes);

    /// <summary>Parses this frame's display-controller header.</summary>
    public FrameHeader ReadHeader() => FrameHeader.Parse(Video, Layout);

    /// <summary>Decodes this frame's picture to RGBA.</summary>
    public byte[] DecodeRgba() => VideoDecoder.DecodeRgba(PixelData);
}

/// <summary>
/// Reads frames sequentially from one track of a mounted disc, de-interleaving the
/// nine-video-bytes-to-one-audio-byte stream as it goes.
/// </summary>
public sealed class TrackReader
{
    private readonly DiscTrack _track;
    private readonly byte[] _stream;
    private readonly byte[] _video;
    private readonly byte[] _audio;

    /// <summary>Opens <paramref name="track"/> for reading using <paramref name="layout"/>.</summary>
    public TrackReader(DiscTrack track, FrameLayout layout)
    {
        _track = track;
        Layout = layout;
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

    /// <summary>Duration of the track at the disc's exact frame rate.</summary>
    public TimeSpan Duration => Layout.FrameDuration * FrameCount;

    /// <summary>
    /// Reads the frame at <paramref name="frameIndex"/>, or <see langword="null"/> past the end
    /// of the track. The returned buffers are freshly allocated and safe to retain.
    /// </summary>
    public VideoNowFrame? ReadFrame(int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= FrameCount) return null;

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

        var groups = (Layout.HeaderBytes + FrameLayout.VideoBytesPerGroup - 1) / FrameLayout.VideoBytesPerGroup;
        var stream = _stream.AsSpan(0, groups * FrameLayout.GroupBytes);

        var offset = StartOffset + (long)frameIndex * Layout.StreamBytes;
        if (_track.Read(offset, stream) < stream.Length) return null;

        Deinterleave(stream, _video, _audio);
        return FrameHeader.Parse(_video, Layout);
    }

    /// <summary>Splits an interleaved frame into its video and audio halves.</summary>
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
