using Vxp.Format;

namespace Vxp.Tests;

/// <summary>
/// Builds VideoNow streams in memory so the decoder can be tested without a disc image.
/// </summary>
internal static class SyntheticDisc
{
    /// <summary>
    /// Produces one frame of interleaved disc bytes: a valid header carrying the given
    /// register values, followed by <paramref name="pixelData"/>, with
    /// <paramref name="audio"/> woven into every tenth byte.
    /// </summary>
    public static byte[] BuildFrame(
        FrameLayout layout,
        IReadOnlyDictionary<int, byte> registers,
        ReadOnlySpan<byte> pixelData,
        ReadOnlySpan<byte> audio)
    {
        var video = new byte[layout.VideoBytes];

        // Sync words, then (value, register) pairs, then 0xFF padding to fill the header.
        var offset = 0;
        for (var i = 0; i < layout.SyncRepeatCount; i++)
        {
            FrameLayout.SyncWord.CopyTo(video.AsSpan(offset));
            offset += FrameLayout.SyncWord.Length;
        }

        foreach (var (register, value) in registers)
        {
            video[offset++] = value;
            video[offset++] = (byte)register;
        }

        while (offset < layout.HeaderBytes) video[offset++] = 0xFF;

        pixelData.CopyTo(video.AsSpan(layout.HeaderBytes));

        var stream = new byte[layout.StreamBytes];
        var groups = layout.StreamBytes / FrameLayout.GroupBytes;
        for (var g = 0; g < groups; g++)
        {
            video.AsSpan(g * FrameLayout.VideoBytesPerGroup, FrameLayout.VideoBytesPerGroup)
                 .CopyTo(stream.AsSpan(g * FrameLayout.GroupBytes));
            stream[g * FrameLayout.GroupBytes + FrameLayout.VideoBytesPerGroup] =
                g < audio.Length ? audio[g] : (byte)0x80;
        }

        return stream;
    }

    /// <summary>Packed pixel data where every nibble is derived from its own position.</summary>
    public static byte[] GradientPixels()
    {
        var pixels = new byte[FrameLayout.PixelBytes];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(((i * 7) & 0x0F) | (((i * 3) & 0x0F) << 4));
        return pixels;
    }
}
