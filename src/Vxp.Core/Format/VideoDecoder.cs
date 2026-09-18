namespace Vxp.Format;

/// <summary>
/// Unpacks the packed pixel payload of a VideoNow Color/XP frame into 32-bit RGBA.
/// </summary>
/// <remarks>
/// <para>
/// Each pixel carries three 4-bit channel samples, so two packed bytes hold four
/// channel samples and six packed bytes hold four whole pixels. The samples are not
/// laid out left to right: the encoder splits a displayed row across two 108-byte
/// half-rows and walks them in the order the panel's column drivers were wired, so a
/// group of four output pixels is assembled from three bytes of each half-row.
/// </para>
/// <para>
/// For half-row bytes <c>a0 a1 a2</c> (upper) and <c>b0 b1 b2</c> (lower), writing
/// <c>lo</c>/<c>hi</c> for the low and high nibble, the four pixels are:
/// </para>
/// <code>
/// pixel 0 = (a0.lo, b0.lo, b0.hi)
/// pixel 1 = (b1.lo, a0.hi, a1.lo)
/// pixel 2 = (a1.hi, b1.hi, b2.lo)
/// pixel 3 = (b2.hi, a2.lo, a2.hi)
/// </code>
/// </remarks>
public static class VideoDecoder
{
    /// <summary>Bytes in a decoded Color or XP RGBA frame; see <see cref="FrameLayout.RgbaBytes"/>.</summary>
    public const int RgbaFrameBytes = FrameLayout.Width * FrameLayout.Height * 4;

    /// <summary>
    /// Decodes <paramref name="frame"/>'s picture into <paramref name="rgba"/>, which must hold
    /// <see cref="FrameLayout.RgbaBytes"/> of the frame's layout: colour for Color and XP,
    /// grey for black and white, where <paramref name="order"/> has nothing to act on.
    /// </summary>
    public static void Decode(VideoNowFrame frame, Span<byte> rgba, ChannelOrder? order = null)
    {
        if (frame.Layout.Monochrome) BlackAndWhiteStream.DecodeRgba(frame.PixelData, rgba);
        else DecodeRgba(frame.PixelData, rgba, order ?? ChannelOrder.Default);
    }

    /// <summary>
    /// Decodes <paramref name="pixelData"/> (<see cref="FrameLayout.PixelBytes"/> bytes,
    /// i.e. the video payload with its header already removed) into
    /// <paramref name="rgba"/> as tightly packed R,G,B,A rows top to bottom.
    /// </summary>
    public static void DecodeRgba(ReadOnlySpan<byte> pixelData, Span<byte> rgba)
        => DecodeRgba(pixelData, rgba, ChannelOrder.Default);

    /// <summary>
    /// Decodes with an explicit <paramref name="order"/> mapping from the panel channel
    /// samples to output R, G and B.
    /// </summary>
    public static void DecodeRgba(ReadOnlySpan<byte> pixelData, Span<byte> rgba, ChannelOrder order)
    {
        if (pixelData.Length < FrameLayout.PixelBytes)
            throw new ArgumentException($"Expected at least {FrameLayout.PixelBytes} packed bytes.", nameof(pixelData));
        if (rgba.Length < RgbaFrameBytes)
            throw new ArgumentException($"Expected at least {RgbaFrameBytes} output bytes.", nameof(rgba));

        const int stride = FrameLayout.PixelRowStride;   // 108 packed bytes per half-row
        const int groupsPerRow = FrameLayout.Width / 4;  // 36 groups of four pixels

        for (var y = 0; y < FrameLayout.Height; y++)
        {
            var upper = y * stride * 2;
            var lower = upper + stride;
            var outIndex = y * FrameLayout.Width * 4;

            for (var g = 0; g < groupsPerRow; g++)
            {
                var i = g * 3;
                byte a0 = pixelData[upper + i], a1 = pixelData[upper + i + 1], a2 = pixelData[upper + i + 2];
                byte b0 = pixelData[lower + i], b1 = pixelData[lower + i + 1], b2 = pixelData[lower + i + 2];

                Write(rgba, ref outIndex, order, Low(a0), Low(b0), High(b0));
                Write(rgba, ref outIndex, order, Low(b1), High(a0), Low(a1));
                Write(rgba, ref outIndex, order, High(a1), High(b1), Low(b2));
                Write(rgba, ref outIndex, order, High(b2), Low(a2), High(a2));
            }
        }
    }

    /// <summary>Decodes into a freshly allocated RGBA buffer.</summary>
    public static byte[] DecodeRgba(ReadOnlySpan<byte> pixelData)
    {
        var buffer = new byte[RgbaFrameBytes];
        DecodeRgba(pixelData, buffer);
        return buffer;
    }

    private static void Write(Span<byte> rgba, ref int index, ChannelOrder order, byte c0, byte c1, byte c2)
    {
        rgba[index++] = Pick(order.Red, c0, c1, c2);
        rgba[index++] = Pick(order.Green, c0, c1, c2);
        rgba[index++] = Pick(order.Blue, c0, c1, c2);
        rgba[index++] = 0xFF;
    }

    private static byte Pick(int channel, byte c0, byte c1, byte c2)
        => channel switch { 0 => c0, 1 => c1, _ => c2 };

    // Expand a 4-bit sample to 8 bits by nibble replication, so 0xF maps to 0xFF.
    private static byte Low(byte value) => (byte)((value & 0x0F) * 0x11);

    private static byte High(byte value) => (byte)((value >> 4) * 0x11);
}

/// <summary>
/// Maps the three channel samples a pixel carries onto output red, green and blue.
/// </summary>
/// <remarks>
/// The packed stream stores three samples per pixel in the order the panel column
/// drivers consume them, which is not necessarily R, G, B. This type makes that mapping
/// explicit so it can be verified against real footage rather than assumed.
/// </remarks>
/// <param name="Red">Which of the three samples (0, 1 or 2) feeds output red.</param>
/// <param name="Green">Which sample feeds output green.</param>
/// <param name="Blue">Which sample feeds output blue.</param>
public readonly record struct ChannelOrder(int Red, int Green, int Blue)
{
    /// <summary>The mapping that reproduces the picture as the hardware shows it.</summary>
    public static ChannelOrder Default { get; } = new(0, 1, 2);

    /// <summary>Parses a three-letter mapping such as <c>"RGB"</c> or <c>"BRG"</c>.</summary>
    /// <remarks>
    /// The letters name where each of the three stored samples is sent, in order. So
    /// <c>"GBR"</c> means sample 0 is green, sample 1 is blue and sample 2 is red.
    /// </remarks>
    public static ChannelOrder Parse(string text)
    {
        if (text.Length != 3) throw new FormatException("Channel order must be three letters, for example RGB.");

        var slots = new int[3];
        for (var i = 0; i < 3; i++)
        {
            slots[char.ToUpperInvariant(text[i]) switch
            {
                'R' => 0,
                'G' => 1,
                'B' => 2,
                _ => throw new FormatException($"Unexpected channel letter '{text[i]}'."),
            }] = i;
        }

        return new ChannelOrder(slots[0], slots[1], slots[2]);
    }
}
