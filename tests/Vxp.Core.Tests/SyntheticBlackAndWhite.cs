using Vxp.Format;

namespace Vxp.Tests;

/// <summary>
/// Builds black and white VideoNow streams in memory: four-byte groups of two video bytes,
/// a marker and an audio sample, laid out as header, picture and footer.
/// </summary>
internal static class SyntheticBlackAndWhite
{
    /// <summary>
    /// One frame of disc bytes: <paramref name="headerGroups"/> groups of header under
    /// <paramref name="headerMarker"/>, the 3200-byte <paramref name="picture"/>, and a footer
    /// under the matching footer marker, with <paramref name="audio"/> in every fourth byte.
    /// </summary>
    public static byte[] BuildFrame(
        ReadOnlySpan<byte> picture,
        ReadOnlySpan<byte> audio,
        byte headerMarker = 0xC3,
        int headerGroups = BlackAndWhiteStream.HeaderGroups)
    {
        var footerMarker = headerMarker switch { 0xE1 => (byte)0xD2, 0xA5 => (byte)0x96, _ => (byte)0xB4 };
        var groups = headerGroups + BlackAndWhiteStream.PictureGroups + BlackAndWhiteStream.FooterGroups;
        var stream = new byte[groups * BlackAndWhiteStream.GroupBytes];

        for (var g = 0; g < groups; g++)
        {
            var o = g * BlackAndWhiteStream.GroupBytes;
            var p = g - headerGroups;

            if (g < headerGroups)
            {
                stream[o] = stream[o + 1] = stream[o + 2] = headerMarker;
            }
            else if (p < BlackAndWhiteStream.PictureGroups)
            {
                stream[o] = picture[p * 2];
                stream[o + 1] = picture[p * 2 + 1];
                stream[o + 2] = BlackAndWhiteStream.PictureMarker;
            }
            else
            {
                stream[o] = stream[o + 1] = stream[o + 2] = footerMarker;
            }

            stream[o + 3] = g < audio.Length ? audio[g] : (byte)0x80;
        }

        return stream;
    }

    /// <summary>
    /// A frame whose picture and sound say which track and frame it is: the first pixel's
    /// grey is the track, the second the frame, and every sample is 0x80 plus the frame.
    /// </summary>
    public static byte[] StampedFrame(int track, int frame)
    {
        var picture = new byte[BlackAndWhiteStream.Width * BlackAndWhiteStream.Height / 2];
        picture[0] = (byte)(((track & 0x0F) << 4) | (frame & 0x0F));

        var audio = new byte[FrameLayout.BlackAndWhite.AudioBytes];
        Array.Fill(audio, (byte)(0x80 + (frame & 0x0F)));

        return BuildFrame(picture, audio);
    }

    /// <summary>Groups of padding: zero video, zero marker and zero audio, as the discs pad.</summary>
    public static byte[] Padding(int groups) => new byte[groups * BlackAndWhiteStream.GroupBytes];
}
