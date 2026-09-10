namespace Vxp.Format;

/// <summary>The three VideoNow disc variants.</summary>
public enum DiscFormat
{
    /// <summary>Unrecognised or non-VideoNow content.</summary>
    Unknown = 0,

    /// <summary>Original monochrome VideoNow.</summary>
    BlackAndWhite,

    /// <summary>VideoNow Color / Color FX / Jr.</summary>
    Color,

    /// <summary>VideoNow XP.</summary>
    Xp,
}

/// <summary>
/// Geometry of one VideoNow frame as it appears in the raw CD byte stream.
/// </summary>
/// <remarks>
/// <para>
/// A VideoNow disc is pressed as a plain CD-DA. Its raw sector payload is not audio
/// samples but a byte stream in which <b>nine video bytes alternate with one audio byte</b>.
/// A frame therefore occupies <see cref="StreamBytes"/> bytes of the disc, splitting into
/// <see cref="VideoBytes"/> video bytes and <see cref="AudioBytes"/> audio bytes.
/// </para>
/// <para>
/// The video half opens with a <see cref="HeaderBytes"/>-byte header (display-controller
/// register writes, see <see cref="FrameHeader"/>) followed by
/// <see cref="PixelBytes"/> bytes of packed 4-bit-per-channel image data.
/// </para>
/// </remarks>
public sealed record FrameLayout
{
    /// <summary>Video bytes per interleave group.</summary>
    public const int VideoBytesPerGroup = 9;

    /// <summary>Audio bytes per interleave group.</summary>
    public const int AudioBytesPerGroup = 1;

    /// <summary>Total bytes per interleave group.</summary>
    public const int GroupBytes = VideoBytesPerGroup + AudioBytesPerGroup;

    /// <summary>Raw CD byte rate: 75 sectors per second of 2352 bytes.</summary>
    public const int DiscBytesPerSecond = 75 * 2352;

    /// <summary>
    /// Audio sample rate in Hz. Exactly one tenth of the disc byte rate, because one
    /// byte in ten is audio. Samples are unsigned 8-bit mono.
    /// </summary>
    public const int AudioSampleRate = DiscBytesPerSecond / GroupBytes;

    /// <summary>Displayed picture width in pixels.</summary>
    public const int Width = 144;

    /// <summary>Displayed picture height in pixels.</summary>
    public const int Height = 80;

    /// <summary>Packed pixel bytes per frame. Three 4-bit channels per pixel.</summary>
    public const int PixelBytes = Width * Height * 3 / 2;

    /// <summary>Bytes per packed pixel row half. Two of these combine into one output row.</summary>
    public const int PixelRowStride = PixelBytes / (Height * 2);

    /// <summary>The nine-byte word repeated at the start of every frame header.</summary>
    public static ReadOnlySpan<byte> SyncWord =>
        [0x81, 0xE3, 0xE3, 0xC7, 0xC7, 0x81, 0x81, 0xE3, 0xC7];

    /// <summary>Which disc variant this layout describes.</summary>
    public required DiscFormat Format { get; init; }

    /// <summary>Bytes the frame occupies in the interleaved disc stream.</summary>
    public required int StreamBytes { get; init; }

    /// <summary>Size of the frame's video header in de-interleaved video bytes.</summary>
    public required int HeaderBytes { get; init; }

    /// <summary>
    /// How many times <see cref="SyncWord"/> repeats back to back at the start of a
    /// frame. This is what distinguishes Color from XP.
    /// </summary>
    public required int SyncRepeatCount { get; init; }

    /// <summary>De-interleaved video bytes per frame.</summary>
    public int VideoBytes => StreamBytes / GroupBytes * VideoBytesPerGroup;

    /// <summary>De-interleaved audio bytes (samples) per frame.</summary>
    public int AudioBytes => StreamBytes / GroupBytes * AudioBytesPerGroup;

    /// <summary>Exact frame rate implied by the disc byte rate.</summary>
    public double FrameRate => (double)DiscBytesPerSecond / StreamBytes;

    /// <summary>Exact duration of one frame.</summary>
    public TimeSpan FrameDuration => TimeSpan.FromSeconds(AudioBytes / (double)AudioSampleRate);

    /// <summary>VideoNow XP: 1976 interleave groups per frame, 504-byte header, 12 sync words.</summary>
    public static readonly FrameLayout Xp = new()
    {
        Format = DiscFormat.Xp,
        StreamBytes = 19760,
        HeaderBytes = 504,
        SyncRepeatCount = 12,
    };

    /// <summary>VideoNow Color: 1960 interleave groups per frame, 360-byte header, 24 sync words.</summary>
    public static readonly FrameLayout Color = new()
    {
        Format = DiscFormat.Color,
        StreamBytes = 19600,
        HeaderBytes = 360,
        SyncRepeatCount = 24,
    };

    /// <summary>All layouts that <see cref="FormatDetector"/> can identify.</summary>
    public static readonly IReadOnlyList<FrameLayout> Known = [Xp, Color];
}
