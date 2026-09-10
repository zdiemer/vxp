namespace Vxp.Input;

/// <summary>
/// Converts between SDL keycodes and the names used in the settings file and on screen.
/// </summary>
/// <remarks>
/// SDL gives printable keys their ASCII value and everything else a scancode with bit 30
/// set. Spelling the table out here keeps the binding format readable and stops the
/// settings file depending on binding library enum names.
/// </remarks>
public static class KeyNames
{
    private const int ScancodeMask = 1 << 30;

    private static int FromScancode(int scancode) => ScancodeMask | scancode;

    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 32,
        ["Return"] = 13,
        ["Enter"] = 13,
        ["Escape"] = 27,
        ["Backspace"] = 8,
        ["Tab"] = 9,
        ["Delete"] = 127,

        ["Minus"] = '-',
        ["Equals"] = '=',
        ["LeftBracket"] = '[',
        ["RightBracket"] = ']',
        ["Backslash"] = '\\',
        ["Semicolon"] = ';',
        ["Apostrophe"] = '\'',
        ["Grave"] = '`',
        ["Comma"] = ',',
        ["Period"] = '.',
        ["Slash"] = '/',
        ["+"] = FromScancode(87),

        ["Right"] = FromScancode(79),
        ["Left"] = FromScancode(80),
        ["Down"] = FromScancode(81),
        ["Up"] = FromScancode(82),

        ["Insert"] = FromScancode(73),
        ["Home"] = FromScancode(74),
        ["PageUp"] = FromScancode(75),
        ["End"] = FromScancode(77),
        ["PageDown"] = FromScancode(78),

        ["CapsLock"] = FromScancode(57),
        ["PrintScreen"] = FromScancode(70),
        ["ScrollLock"] = FromScancode(71),
        ["Pause"] = FromScancode(72),

        ["NumpadDivide"] = FromScancode(84),
        ["NumpadMultiply"] = FromScancode(85),
        ["NumpadMinus"] = FromScancode(86),
        ["NumpadPlus"] = FromScancode(87),
        ["NumpadEnter"] = FromScancode(88),
        ["NumpadPeriod"] = FromScancode(99),

        ["LeftCtrl"] = FromScancode(224),
        ["LeftShift"] = FromScancode(225),
        ["LeftAlt"] = FromScancode(226),
        ["LeftGui"] = FromScancode(227),
        ["RightCtrl"] = FromScancode(228),
        ["RightShift"] = FromScancode(229),
        ["RightAlt"] = FromScancode(230),
        ["RightGui"] = FromScancode(231),
    };

    private static readonly Dictionary<int, string> ByCode;

    static KeyNames()
    {
        for (var i = 0; i < 12; i++) ByName[$"F{i + 1}"] = FromScancode(58 + i);
        for (var i = 0; i < 9; i++) ByName[$"Numpad{i + 1}"] = FromScancode(89 + i);
        ByName["Numpad0"] = FromScancode(98);

        for (var c = 'A'; c <= 'Z'; c++) ByName[c.ToString()] = char.ToLowerInvariant(c);
        for (var c = '0'; c <= '9'; c++) ByName[c.ToString()] = c;

        // First name wins, so the canonical spellings above beat their aliases.
        ByCode = new Dictionary<int, string>();
        foreach (var (name, code) in ByName) ByCode.TryAdd(code, name);

        // Prefer the plain letter and digit names over anything added later.
        for (var c = 'A'; c <= 'Z'; c++) ByCode[char.ToLowerInvariant(c)] = c.ToString();
        for (var c = '0'; c <= '9'; c++) ByCode[c] = c.ToString();
        ByCode[13] = "Return";
        ByCode[FromScancode(87)] = "NumpadPlus";
    }

    /// <summary>Every key name that can be bound, in a stable order.</summary>
    public static IEnumerable<string> KnownNames => ByName.Keys.Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>The display name for an SDL keycode.</summary>
    public static string Name(int keycode)
    {
        if (ByCode.TryGetValue(keycode, out var name)) return name;

        // Any other printable character stands for itself.
        if (keycode is > 32 and < 127) return char.ToUpperInvariant((char)keycode).ToString();

        return $"Key{keycode}";
    }

    /// <summary>Looks up an SDL keycode by name.</summary>
    public static bool TryParse(string name, out int keycode)
    {
        name = name.Trim();

        if (ByName.TryGetValue(name, out keycode)) return true;

        if (name.Length == 1 && name[0] is > (char)32 and < (char)127)
        {
            keycode = char.ToLowerInvariant(name[0]);
            return true;
        }

        if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name[3..], out keycode))
        {
            return true;
        }

        keycode = 0;
        return false;
    }
}
