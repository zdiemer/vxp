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

/// <summary>Display names for <see cref="DiscFormat"/>.</summary>
public static class DiscFormatNames
{
    /// <summary>A short name for tables and info pages: <c>Xp</c>, <c>Color</c> or <c>B&amp;W</c>.</summary>
    public static string ShortName(this DiscFormat format)
        => format == DiscFormat.BlackAndWhite ? "B&W" : format.ToString();
}

/// <summary>
/// Geometry of one VideoNow frame as it appears in the raw CD byte stream.
/// </summary>
/// <remarks>
/// <para>
/// A VideoNow disc is pressed as a plain CD-DA. Its raw sector payload is not audio
/// samples but a byte stream split into small fixed groups, each holding some video bytes
/// and <b>one audio byte</b>: nine video bytes to one audio byte on Color and XP, and two
/// video bytes, a marker byte and an audio byte on black and white (see
/// <see cref="BlackAndWhiteStream"/>). A frame occupies <see cref="StreamBytes"/> bytes of
/// the disc, splitting into <see cref="VideoBytes"/> video bytes and
/// <see cref="AudioBytes"/> audio bytes.
/// </para>
/// <para>
/// On Color and XP the video half opens with a <see cref="HeaderBytes"/>-byte header
/// (display-controller register writes, see <see cref="FrameHeader"/>) followed by
/// <see cref="PixelBytes"/> bytes of packed 4-bit-per-channel image data.
/// </para>
/// </remarks>
public sealed record FrameLayout
{
    /// <summary>Video bytes per interleave group on Color and XP.</summary>
    public const int VideoBytesPerGroup = 9;

    /// <summary>Audio bytes per interleave group, on every variant.</summary>
    public const int AudioBytesPerGroup = 1;

    /// <summary>Total bytes per interleave group on Color and XP.</summary>
    public const int GroupBytes = VideoBytesPerGroup + AudioBytesPerGroup;

    /// <summary>Raw CD byte rate: 75 sectors per second of 2352 bytes.</summary>
    public const int DiscBytesPerSecond = 75 * 2352;

    /// <summary>
    /// Audio bytes a Color or XP stream carries per second of disc read at one-times CD
    /// speed: exactly one tenth of the disc byte rate, because one byte in ten is audio.
    /// Each byte is one unsigned 8-bit mono sample.
    /// </summary>
    /// <remarks>
    /// This describes the stream, not the speed it is played at, and not black and white,
    /// whose groups are four bytes long. See <see cref="StreamSampleRate"/> and
    /// <see cref="PlaybackSampleRate"/>.
    /// </remarks>
    public const int AudioBytesPerSecond = DiscBytesPerSecond / GroupBytes;

    /// <summary>
    /// Width of the Color and XP picture, 144 pixels. It is also the frame every variant is
    /// shown in: the window is sized from it and <c>video.pixelAspect</c>, and a picture
    /// stored at another size, such as black and white's 80 x 80, is drawn to fill the same
    /// shape. The stored width of a variant's picture is <see cref="PictureWidth"/>.
    /// </summary>
    public const int Width = 144;

    /// <summary>Height of the Color and XP picture, and of the frame every variant is shown in.</summary>
    public const int Height = 80;

    /// <summary>Packed pixel bytes per Color or XP frame. Three 4-bit channels per pixel.</summary>
    public const int PixelBytes = Width * Height * 3 / 2;

    /// <summary>Bytes per packed Color or XP pixel row half. Two of these combine into one output row.</summary>
    public const int PixelRowStride = PixelBytes / (Height * 2);

    /// <summary>The nine-byte word repeated at the start of every Color and XP frame header.</summary>
    public static ReadOnlySpan<byte> SyncWord =>
        [0x81, 0xE3, 0xE3, 0xC7, 0xC7, 0x81, 0x81, 0xE3, 0xC7];

    /// <summary>Which disc variant this layout describes.</summary>
    public required DiscFormat Format { get; init; }

    /// <summary>
    /// Multiple of CD speed the player reads this variant at: 2 for XP and Color, whose
    /// discs only run to the length of their content at twice CD speed, and 1 for black and
    /// white, whose discs run to the length of theirs at one-times.
    /// </summary>
    public required int DiscSpeed { get; init; }

    /// <summary>Bytes the frame occupies in the interleaved disc stream.</summary>
    public required int StreamBytes { get; init; }

    /// <summary>Size of the frame's video header in de-interleaved video bytes.</summary>
    public required int HeaderBytes { get; init; }

    /// <summary>
    /// How many times <see cref="SyncWord"/> repeats back to back at the start of a
    /// frame. This is what distinguishes Color from XP. Zero for black and white, which has
    /// no sync word.
    /// </summary>
    public required int SyncRepeatCount { get; init; }

    /// <summary>Bytes in one interleave group: 10 on Color and XP, 4 on black and white.</summary>
    public int InterleaveBytes { get; init; } = GroupBytes;

    /// <summary>Video bytes in one interleave group: 9 on Color and XP, 2 on black and white.</summary>
    public int InterleaveVideoBytes { get; init; } = VideoBytesPerGroup;

    /// <summary>Stored picture width in pixels.</summary>
    public int PictureWidth { get; init; } = Width;

    /// <summary>Stored picture height in pixels.</summary>
    public int PictureHeight { get; init; } = Height;

    /// <summary>Bits each stored pixel takes: 12 for three 4-bit channels, 4 for a grey.</summary>
    public int BitsPerPixel { get; init; } = 12;

    /// <summary>True when the picture is grey levels rather than colour.</summary>
    public bool Monochrome => Format == DiscFormat.BlackAndWhite;

    /// <summary>Packed picture bytes per frame.</summary>
    public int PictureBytes => PictureWidth * PictureHeight * BitsPerPixel / 8;

    /// <summary>Bytes in one decoded RGBA picture.</summary>
    public int RgbaBytes => PictureWidth * PictureHeight * 4;

    /// <summary>How a pixel is stored, for the info pages.</summary>
    public string PixelDescription => Monochrome ? "16 greys" : "4 bits per channel";

    /// <summary>De-interleaved video bytes per frame.</summary>
    public int VideoBytes => StreamBytes / InterleaveBytes * InterleaveVideoBytes;

    /// <summary>De-interleaved audio bytes (samples) per frame.</summary>
    public int AudioBytes => StreamBytes / InterleaveBytes * AudioBytesPerGroup;

    /// <summary>
    /// Audio bytes the stream carries per second of disc read at one-times CD speed:
    /// 17 640 on Color and XP, 44 100 on black and white.
    /// </summary>
    public int StreamSampleRate => DiscBytesPerSecond / InterleaveBytes * AudioBytesPerGroup;

    /// <summary>
    /// Frames per second of disc read at one-times CD speed, from the byte rate alone.
    /// A fact about the stream; the rate the picture actually moves at is
    /// <see cref="PlaybackFrameRate"/>.
    /// </summary>
    public double StreamFrameRate => (double)DiscBytesPerSecond / StreamBytes;

    /// <summary>
    /// The rate the sound plays at, in samples per second: the stream's audio rate at
    /// <see cref="DiscSpeed"/>. Every time that is shown or written, and every WAV, uses it.
    /// </summary>
    public int PlaybackSampleRate => StreamSampleRate * DiscSpeed;

    /// <summary>Frames per second at <see cref="PlaybackSampleRate"/>.</summary>
    public double PlaybackFrameRate => PlaybackSampleRate / (double)AudioBytes;

    /// <summary>Exact duration of one frame at <see cref="PlaybackSampleRate"/>.</summary>
    public TimeSpan FrameDuration => TimeSpan.FromSeconds(AudioBytes / (double)PlaybackSampleRate);

    /// <summary>
    /// VideoNow XP: 1976 interleave groups per frame, 504-byte header, 12 sync words, read
    /// at twice CD speed: 35 280 Hz and 17.854 fps.
    /// </summary>
    public static readonly FrameLayout Xp = new()
    {
        Format = DiscFormat.Xp,
        DiscSpeed = 2,
        StreamBytes = 19760,
        HeaderBytes = 504,
        SyncRepeatCount = 12,
    };

    /// <summary>
    /// VideoNow Color: 1960 interleave groups per frame, 360-byte header, 24 sync words,
    /// read at twice CD speed like XP: 35 280 Hz and 18 fps.
    /// </summary>
    public static readonly FrameLayout Color = new()
    {
        Format = DiscFormat.Color,
        DiscSpeed = 2,
        StreamBytes = 19600,
        HeaderBytes = 360,
        SyncRepeatCount = 24,
    };

    /// <summary>
    /// Black and white VideoNow: 2940 four-byte groups per frame carrying an 80 x 80
    /// picture of 16 greys, read at one-times CD speed: 44 100 Hz and 15 fps. See
    /// <see cref="BlackAndWhiteStream"/>.
    /// </summary>
    public static readonly FrameLayout BlackAndWhite = new()
    {
        Format = DiscFormat.BlackAndWhite,
        DiscSpeed = 1,
        StreamBytes = BlackAndWhiteStream.FrameGroups * BlackAndWhiteStream.GroupBytes,
        HeaderBytes = BlackAndWhiteStream.HeaderGroups * BlackAndWhiteStream.VideoBytesPerGroup,
        SyncRepeatCount = 0,
        InterleaveBytes = BlackAndWhiteStream.GroupBytes,
        InterleaveVideoBytes = BlackAndWhiteStream.VideoBytesPerGroup,
        PictureWidth = BlackAndWhiteStream.Width,
        PictureHeight = BlackAndWhiteStream.Height,
        BitsPerPixel = 4,
    };

    /// <summary>All layouts that <see cref="FormatDetector"/> can identify.</summary>
    public static readonly IReadOnlyList<FrameLayout> Known = [Xp, Color, BlackAndWhite];
}
