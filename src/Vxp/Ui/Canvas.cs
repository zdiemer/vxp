namespace Vxp.Ui;

/// <summary>A colour with alpha, as the overlay stores it.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha, where 255 is opaque.</param>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Text and outlines.</summary>
    public static readonly Rgba White = new(0xF2, 0xF2, 0xF2);

    /// <summary>Secondary text.</summary>
    public static readonly Rgba Grey = new(0x9A, 0x9A, 0x9A);

    /// <summary>Text on a highlighted row.</summary>
    public static readonly Rgba Black = new(0x10, 0x10, 0x10);

    /// <summary>The highlight itself.</summary>
    public static readonly Rgba Accent = new(0x4C, 0xC2, 0xD6);

    /// <summary>Warnings and unbound controls.</summary>
    public static readonly Rgba Warn = new(0xE0, 0x9A, 0x4A);

    /// <summary>Panel background.</summary>
    public static readonly Rgba Panel = new(0x14, 0x16, 0x1A, 0xE8);

    /// <summary>Panel heading strip.</summary>
    public static readonly Rgba PanelHeader = new(0x20, 0x24, 0x2C, 0xF0);

    /// <summary>Fully transparent.</summary>
    public static readonly Rgba None = new(0, 0, 0, 0);

    /// <summary>Parses <c>#RRGGBB</c>, falling back to black.</summary>
    public static Rgba Parse(string text)
    {
        var hex = text.TrimStart('#');
        if (hex.Length != 6
            || !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(hex[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new Rgba(0, 0, 0);
        }

        return new Rgba(r, g, b);
    }

    /// <summary>The same colour at a different opacity.</summary>
    public Rgba WithAlpha(byte alpha) => this with { A = alpha };
}

/// <summary>
/// A software drawing surface for the on-screen display, uploaded to a texture once a
/// frame. Everything the emulator draws over the picture goes through here.
/// </summary>
public sealed class Canvas
{
    private byte[] _pixels = [];

    /// <summary>Width of the surface in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Height of the surface in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>The RGBA pixel buffer.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>Resizes the surface, discarding its contents. Returns true if it changed.</summary>
    public bool Resize(int width, int height)
    {
        if (width == Width && height == Height) return false;

        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _pixels = new byte[Width * Height * 4];
        return true;
    }

    /// <summary>Clears the whole surface to transparent.</summary>
    public void Clear() => Array.Clear(_pixels);

    /// <summary>Fills a rectangle, blending with what is already there.</summary>
    public void Fill(int x, int y, int width, int height, Rgba color)
    {
        if (color.A == 0) return;

        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(Width, x + width);
        var y1 = Math.Min(Height, y + height);

        for (var py = y0; py < y1; py++)
        {
            var row = py * Width;
            for (var px = x0; px < x1; px++) Blend(row + px, color);
        }
    }

    /// <summary>Draws a one-pixel-thick rectangle outline.</summary>
    public void Outline(int x, int y, int width, int height, Rgba color, int thickness = 1)
    {
        Fill(x, y, width, thickness, color);
        Fill(x, y + height - thickness, width, thickness, color);
        Fill(x, y, thickness, height, color);
        Fill(x + width - thickness, y, thickness, height, color);
    }

    /// <summary>Draws text and returns the x position just past it.</summary>
    public int Text(int x, int y, string text, int scale, Rgba color)
    {
        var penX = x;

        foreach (var character in text)
        {
            var glyph = BitmapFont.Glyph(character);

            for (var column = 0; column < BitmapFont.GlyphWidth; column++)
            {
                var bits = glyph[column];
                if (bits == 0) continue;

                for (var row = 0; row < BitmapFont.GlyphHeight; row++)
                {
                    if ((bits & (1 << row)) == 0) continue;
                    Fill(penX + column * scale, y + row * scale, scale, scale, color);
                }
            }

            penX += BitmapFont.Advance * scale;
        }

        return penX;
    }

    /// <summary>Draws text with a dark outline, so it stays legible over any picture.</summary>
    public int ShadowText(int x, int y, string text, int scale, Rgba color)
    {
        Text(x + scale, y + scale, text, scale, new Rgba(0, 0, 0, 190));
        return Text(x, y, text, scale, color);
    }

    /// <summary>Draws text ending at <paramref name="right"/>.</summary>
    public void TextRight(int right, int y, string text, int scale, Rgba color)
        => Text(right - BitmapFont.Measure(text, scale), y, text, scale, color);

    /// <summary>Draws text centred on <paramref name="centreX"/>.</summary>
    public void TextCentred(int centreX, int y, string text, int scale, Rgba color)
        => Text(centreX - BitmapFont.Measure(text, scale) / 2, y, text, scale, color);

    /// <summary>Darkens the whole surface, used behind an open menu.</summary>
    public void Dim(byte amount) => Fill(0, 0, Width, Height, new Rgba(0, 0, 0, amount));

    private void Blend(int pixel, Rgba color)
    {
        var index = pixel * 4;

        if (color.A == 255)
        {
            _pixels[index] = color.R;
            _pixels[index + 1] = color.G;
            _pixels[index + 2] = color.B;
            _pixels[index + 3] = 255;
            return;
        }

        var alpha = color.A / 255f;
        var inverse = 1f - alpha;

        _pixels[index] = (byte)(color.R * alpha + _pixels[index] * inverse);
        _pixels[index + 1] = (byte)(color.G * alpha + _pixels[index + 1] * inverse);
        _pixels[index + 2] = (byte)(color.B * alpha + _pixels[index + 2] * inverse);
        _pixels[index + 3] = (byte)(color.A + _pixels[index + 3] * inverse);
    }
}
