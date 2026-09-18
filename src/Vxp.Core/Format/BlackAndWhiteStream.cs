using Vxp.Discs;

namespace Vxp.Format;

/// <summary>
/// The stream of an original black and white VideoNow disc.
/// </summary>
/// <remarks>
/// <para>
/// The disc is read as 16-bit stereo CD audio, and each four-byte sample frame is one
/// group: the left channel carries two video bytes, and the right channel a marker byte
/// and then an audio byte.
/// </para>
/// <code>
/// | video | video | marker | audio | video | video | marker | audio | ...
/// </code>
/// <para>
/// The marker says what the group's video bytes are part of: <c>0x5A</c> for picture,
/// one of <c>0xE1</c>, <c>0xC3</c> or <c>0xA5</c> for a frame's header, and the matching
/// <c>0xD2</c>, <c>0xB4</c> or <c>0x96</c> for its footer. A frame is 2940 groups: 670 of
/// header, 1600 of picture and 670 of footer. The header and footer video bytes mostly
/// repeat the marker and carry no picture. There is no sync word and no register file,
/// so no branching either: black and white discs play straight through.
/// </para>
/// <para>
/// The picture is 80 x 80 pixels of 16 greys, 40 bytes to a row, high nibble first, with
/// 0 black and 15 white.
/// </para>
/// <para>
/// Frames do not respect track boundaries. A long programme is mastered as one stream
/// and cut into tracks wherever the index falls, so a track can open partway through a
/// frame and end partway through another. <see cref="TrackReader"/> counts only the frames
/// whose picture lies wholly inside the track, and gives the partial frames' sound to the
/// first and last whole frames so none of it is lost. See <c>docs/format.md</c>.
/// </para>
/// </remarks>
public static class BlackAndWhiteStream
{
    /// <summary>Bytes in one group: two video bytes, a marker and an audio sample.</summary>
    public const int GroupBytes = 4;

    /// <summary>Video bytes at the start of each group.</summary>
    public const int VideoBytesPerGroup = 2;

    /// <summary>Offset of the marker byte within a group.</summary>
    public const int MarkerOffset = 2;

    /// <summary>Offset of the audio sample within a group.</summary>
    public const int AudioOffset = 3;

    /// <summary>Groups of header at the start of a frame.</summary>
    public const int HeaderGroups = 670;

    /// <summary>Groups of picture in a frame: 3200 video bytes.</summary>
    public const int PictureGroups = 1600;

    /// <summary>Groups of footer at the end of a frame.</summary>
    public const int FooterGroups = 670;

    /// <summary>Groups in a whole frame.</summary>
    public const int FrameGroups = HeaderGroups + PictureGroups + FooterGroups;

    /// <summary>Picture width in pixels.</summary>
    public const int Width = 80;

    /// <summary>Picture height in pixels.</summary>
    public const int Height = 80;

    /// <summary>Packed bytes in one picture row: two pixels a byte.</summary>
    public const int RowBytes = Width / 2;

    /// <summary>Marker on every group of picture.</summary>
    public const byte PictureMarker = 0x5A;

    /// <summary>
    /// Longest header run accepted when finding a frame. The discs write 670 groups, and
    /// now and then fewer; a much longer run is not a header.
    /// </summary>
    private const int MaxHeaderGroups = HeaderGroups + 130;

    /// <summary>
    /// True for a header marker: <c>0xE1</c> on the first frames of a programme, whose
    /// header carries a 16-step ramp, <c>0xC3</c> on ordinary frames, and <c>0xA5</c> on
    /// the last.
    /// </summary>
    public static bool IsHeaderMarker(byte marker) => marker is 0xE1 or 0xC3 or 0xA5;

    /// <summary>True for a footer marker, <c>0xD2</c>, <c>0xB4</c> or <c>0x96</c>.</summary>
    public static bool IsFooterMarker(byte marker) => marker is 0xD2 or 0xB4 or 0x96;

    /// <summary>Where a track's whole frames are.</summary>
    /// <param name="Starts">Byte offset of each frame's first header group, in order.</param>
    /// <param name="PictureOffsets">Bytes from each frame's start to its picture.</param>
    /// <param name="Alignment">Offset of the first group boundary in the track, 0 to 3.</param>
    public sealed record FrameIndex(long[] Starts, int[] PictureOffsets, int Alignment)
    {
        /// <summary>Number of whole frames.</summary>
        public int Count => Starts.Length;
    }

    /// <summary>
    /// Finds every frame in <paramref name="track"/> whose header is followed by a whole
    /// picture, by reading the track through once.
    /// </summary>
    /// <remarks>
    /// Frames follow one another every 2940 groups within a track, but a track can open
    /// partway through a frame, a programme ends in padding, and the first header after a
    /// track boundary is often short (213 groups at the start of <i>Rugrats: All Growed
    /// Up</i> part 1, track 7). So frames are found by their markers rather than counted
    /// from the track's length. Reading a track through is quick next to playing it.
    /// </remarks>
    public static FrameIndex IndexFrames(DiscTrack track)
    {
        var probe = new byte[(int)Math.Min(64 * 1024, track.ByteLength)];
        var probed = track.Read(0, probe);
        var first = FindFrame(probe.AsSpan(0, probed), 0);
        if (first < 0) return new FrameIndex([], [], 0);

        var alignment = first % GroupBytes;
        var starts = new List<long>();
        var pictures = new List<int>();

        long headerStart = -1, pictureStart = -1;
        int headerGroups = 0, pictureGroups = 0;
        byte previous = 0;

        var chunk = new byte[1024 * 1024];
        for (long position = alignment; position + GroupBytes <= track.ByteLength;)
        {
            var read = track.Read(position, chunk) / GroupBytes * GroupBytes;
            if (read == 0) break;

            for (var o = 0; o < read; o += GroupBytes, position += GroupBytes)
            {
                var marker = chunk[o + MarkerOffset];

                if (IsHeaderMarker(marker))
                {
                    if (!IsHeaderMarker(previous))
                    {
                        headerStart = position;
                        headerGroups = 0;
                    }

                    headerGroups++;
                    pictureStart = -1;
                }
                else if (marker == PictureMarker)
                {
                    if (IsHeaderMarker(previous) && headerStart >= 0 && headerGroups <= MaxHeaderGroups)
                    {
                        pictureStart = position;
                        pictureGroups = 0;
                    }

                    if (pictureStart >= 0 && ++pictureGroups == PictureGroups)
                    {
                        starts.Add(headerStart);
                        pictures.Add((int)(pictureStart - headerStart));
                        headerStart = pictureStart = -1;
                    }
                }
                else
                {
                    pictureStart = -1;
                }

                previous = marker;
            }
        }

        return new FrameIndex(starts.ToArray(), pictures.ToArray(), alignment);
    }

    /// <summary>True if <paramref name="stream"/> holds at least one whole black and white frame picture.</summary>
    public static bool LooksLike(ReadOnlySpan<byte> stream) => FindFrame(stream, 0) >= 0;

    /// <summary>
    /// Finds the first frame at or after byte <paramref name="from"/> whose header is
    /// followed by a whole picture inside <paramref name="stream"/>.
    /// </summary>
    /// <returns>
    /// The byte offset of the group that opens the frame's header, or -1. The offset also
    /// fixes the group alignment, which retail tracks start on but a stray rip may not.
    /// </returns>
    public static int FindFrame(ReadOnlySpan<byte> stream, int from)
        => FindFrame(stream, from, out _);

    /// <summary>
    /// As <see cref="FindFrame(ReadOnlySpan{byte}, int)"/>, also giving the byte offset at
    /// which the frame's picture starts.
    /// </summary>
    public static int FindFrame(ReadOnlySpan<byte> stream, int from, out int picture)
    {
        picture = -1;
        var last = stream.Length - (PictureGroups + 1) * GroupBytes;

        for (var start = Math.Max(0, from); start <= last; start++)
        {
            if (!IsHeaderMarker(stream[start + MarkerOffset])) continue;
            if (start >= GroupBytes && IsHeaderMarker(stream[start - GroupBytes + MarkerOffset])) continue;

            // Walk the header run; a wrong alignment lands in video bytes that repeat the
            // marker, and is rejected below because they are not followed by a picture.
            var offset = start;
            var groups = 0;
            while (offset + MarkerOffset < stream.Length && IsHeaderMarker(stream[offset + MarkerOffset]))
            {
                offset += GroupBytes;
                if (++groups > MaxHeaderGroups) break;
            }

            if (groups > MaxHeaderGroups) continue;
            if (IsPictureRun(stream, offset))
            {
                picture = offset;
                return start;
            }
        }

        return -1;
    }

    private static bool IsPictureRun(ReadOnlySpan<byte> stream, int offset)
    {
        if (offset + PictureGroups * GroupBytes > stream.Length) return false;

        for (var g = 0; g < PictureGroups; g++)
            if (stream[offset + g * GroupBytes + MarkerOffset] != PictureMarker) return false;

        return true;
    }

    /// <summary>
    /// Splits groups into video bytes and audio samples. Audio under a zero marker, the
    /// padding before and after a programme, is written as silence (<c>0x80</c>) rather
    /// than the zeros the disc holds, which would click.
    /// </summary>
    public static void Deinterleave(ReadOnlySpan<byte> stream, Span<byte> video, Span<byte> audio)
    {
        var groups = stream.Length / GroupBytes;
        for (var g = 0; g < groups; g++)
        {
            var source = g * GroupBytes;
            if (!video.IsEmpty)
            {
                video[g * VideoBytesPerGroup] = stream[source];
                video[g * VideoBytesPerGroup + 1] = stream[source + 1];
            }

            if (!audio.IsEmpty)
                audio[g] = stream[source + MarkerOffset] == 0 ? (byte)0x80 : stream[source + AudioOffset];
        }
    }

    /// <summary>
    /// Decodes an 80 x 80 picture (3200 packed bytes) into grey RGBA, rows top to bottom.
    /// Each byte is two pixels, the high nibble on the left; a 4-bit level expands to 8 bits
    /// by nibble replication, so 0 is black and 15 is <c>0xFF</c>.
    /// </summary>
    public static void DecodeRgba(ReadOnlySpan<byte> picture, Span<byte> rgba)
    {
        const int bytes = Width * Height / 2;
        if (picture.Length < bytes)
            throw new ArgumentException($"Expected at least {bytes} packed bytes.", nameof(picture));
        if (rgba.Length < Width * Height * 4)
            throw new ArgumentException($"Expected at least {Width * Height * 4} output bytes.", nameof(rgba));

        var o = 0;
        for (var i = 0; i < bytes; i++)
        {
            Grey(rgba, ref o, (byte)((picture[i] >> 4) * 0x11));
            Grey(rgba, ref o, (byte)((picture[i] & 0x0F) * 0x11));
        }
    }

    private static void Grey(Span<byte> rgba, ref int index, byte level)
    {
        rgba[index++] = level;
        rgba[index++] = level;
        rgba[index++] = level;
        rgba[index++] = 0xFF;
    }
}
