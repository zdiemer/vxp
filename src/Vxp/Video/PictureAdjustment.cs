using Vxp.Cli;
using Vxp.Config;
using Vxp.Format;

namespace Vxp.Video;

/// <summary>
/// Brightness, contrast, gamma and saturation applied to a decoded frame.
/// </summary>
/// <remarks>
/// Brightness, contrast and gamma are per-channel and collapse into a single 256-entry
/// lookup table, rebuilt only when a setting changes. Saturation mixes across channels so
/// it is applied afterwards, which at 144x80 costs nothing worth optimising.
/// </remarks>
public sealed class PictureAdjustment
{
    private readonly byte[] _lut = new byte[256];

    private int _brightness;
    private int _contrast;
    private int _gamma = 100;
    private int _saturation;
    private bool _lutValid;

    /// <summary>Which stored sample feeds which output channel.</summary>
    public ChannelOrder Order { get; set; } = ChannelOrder.Default;

    /// <summary>Brightness, -100 to 100.</summary>
    public int Brightness
    {
        get => _brightness;
        set => Assign(ref _brightness, Math.Clamp(value, -100, 100));
    }

    /// <summary>Contrast, -100 to 100.</summary>
    public int Contrast
    {
        get => _contrast;
        set => Assign(ref _contrast, Math.Clamp(value, -100, 100));
    }

    /// <summary>Gamma, 50 to 300, where 100 leaves the picture untouched.</summary>
    public int Gamma
    {
        get => _gamma;
        set => Assign(ref _gamma, Math.Clamp(value, 50, 300));
    }

    /// <summary>Saturation, -100 (greyscale) to 100.</summary>
    public int Saturation
    {
        get => _saturation;
        set => _saturation = Math.Clamp(value, -100, 100);
    }

    /// <summary>True when every setting is neutral and <see cref="Apply"/> would do nothing.</summary>
    public bool IsIdentity => _brightness == 0 && _contrast == 0 && _gamma == 100 && _saturation == 0;

    /// <summary>Builds an adjustment from stored settings.</summary>
    public static PictureAdjustment FromSettings(VideoSettings settings)
    {
        var adjustment = new PictureAdjustment
        {
            Brightness = settings.Brightness,
            Contrast = settings.Contrast,
            Gamma = settings.Gamma,
            Saturation = settings.Saturation,
        };

        adjustment.SetOrder(settings.ChannelOrder);
        return adjustment;
    }

    /// <summary>Builds an adjustment from command-line options.</summary>
    public static PictureAdjustment FromArgs(CommandLine args)
    {
        var adjustment = new PictureAdjustment
        {
            Brightness = args.Int("brightness", 0),
            Contrast = args.Int("contrast", 0),
            Gamma = args.Int("gamma", 100),
            Saturation = args.Int("saturation", 0),
        };

        adjustment.SetOrder(args.Value("swizzle", "RGB"));
        return adjustment;
    }

    /// <summary>Sets the channel order from a three-letter name, ignoring anything malformed.</summary>
    public void SetOrder(string text)
    {
        try
        {
            Order = ChannelOrder.Parse(text);
        }
        catch (FormatException)
        {
            Order = ChannelOrder.Default;
        }
    }

    /// <summary>Adjusts an RGBA frame in place.</summary>
    public void Apply(Span<byte> rgba)
    {
        if (IsIdentity) return;

        if (!_lutValid) BuildLut();

        var saturation = 1f + _saturation / 100f;

        for (var i = 0; i < rgba.Length; i += 4)
        {
            var r = _lut[rgba[i]];
            var g = _lut[rgba[i + 1]];
            var b = _lut[rgba[i + 2]];

            if (_saturation != 0)
            {
                // Rec. 601 luma, which is what a panel of this era was tuned against.
                var luma = 0.299f * r + 0.587f * g + 0.114f * b;
                r = Clamp(luma + (r - luma) * saturation);
                g = Clamp(luma + (g - luma) * saturation);
                b = Clamp(luma + (b - luma) * saturation);
            }

            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
        }
    }

    private void BuildLut()
    {
        var brightness = _brightness * 255 / 100.0;
        var contrast = (_contrast + 100) / 100.0;
        var gamma = 100.0 / _gamma;

        for (var i = 0; i < _lut.Length; i++)
        {
            var value = i / 255.0;

            value = (value - 0.5) * contrast + 0.5;
            value += brightness / 255.0;
            value = Math.Clamp(value, 0, 1);

            if (_gamma != 100) value = Math.Pow(value, gamma);

            _lut[i] = (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        }

        _lutValid = true;
    }

    private void Assign(ref int field, int value)
    {
        if (field == value) return;
        field = value;
        _lutValid = false;
    }

    private static byte Clamp(float value) => (byte)Math.Clamp(value, 0f, 255f);
}
