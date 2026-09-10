namespace Vxp.Format;

/// <summary>
/// Converts the VideoNow audio byte stream into signed 16-bit PCM.
/// </summary>
/// <remarks>
/// The audio half of the interleave is unsigned 8-bit mono at
/// <see cref="FrameLayout.AudioSampleRate"/> Hz; silence sits at 0x80.
/// </remarks>
public static class AudioDecoder
{
    /// <summary>Converts unsigned 8-bit samples to signed 16-bit PCM.</summary>
    public static void DecodePcm16(ReadOnlySpan<byte> samples, Span<short> destination)
    {
        if (destination.Length < samples.Length)
            throw new ArgumentException("Destination is too small.", nameof(destination));

        for (var i = 0; i < samples.Length; i++)
            destination[i] = (short)((samples[i] - 128) << 8);
    }

    /// <summary>Converts unsigned 8-bit samples to normalised floats in [-1, 1).</summary>
    public static void DecodeFloat(ReadOnlySpan<byte> samples, Span<float> destination)
    {
        if (destination.Length < samples.Length)
            throw new ArgumentException("Destination is too small.", nameof(destination));

        for (var i = 0; i < samples.Length; i++)
            destination[i] = (samples[i] - 128) / 128f;
    }
}
