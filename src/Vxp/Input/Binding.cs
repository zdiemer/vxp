namespace Vxp.Input;

/// <summary>Modifier keys that can qualify a keyboard binding.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Either shift key.</summary>
    Shift = 1,

    /// <summary>Either control key.</summary>
    Control = 2,

    /// <summary>Either alt or option key.</summary>
    Alt = 4,
}

/// <summary>What kind of control a binding refers to.</summary>
public enum BindingKind
{
    /// <summary>A keyboard key.</summary>
    Key,

    /// <summary>A game controller button.</summary>
    ControllerButton,

    /// <summary>A game controller axis pushed past its threshold in one direction.</summary>
    ControllerAxis,
}

/// <summary>
/// One control that triggers an action: a key with optional modifiers, a controller
/// button, or a controller axis deflected one way.
/// </summary>
/// <param name="Kind">Which kind of control this is.</param>
/// <param name="Code">SDL keycode, controller button index, or controller axis index.</param>
/// <param name="Modifiers">Modifier keys required, for keyboard bindings.</param>
/// <param name="Positive">For an axis, whether the positive direction triggers.</param>
public readonly record struct Binding(BindingKind Kind, int Code, KeyModifiers Modifiers = KeyModifiers.None, bool Positive = true)
{
    /// <summary>A keyboard binding.</summary>
    public static Binding Key(int keycode, KeyModifiers modifiers = KeyModifiers.None)
        => new(BindingKind.Key, keycode, modifiers);

    /// <summary>A controller button binding.</summary>
    public static Binding Button(ControllerButton button)
        => new(BindingKind.ControllerButton, (int)button);

    /// <summary>A controller axis binding.</summary>
    public static Binding Axis(ControllerAxis axis, bool positive)
        => new(BindingKind.ControllerAxis, (int)axis, KeyModifiers.None, positive);

    /// <summary>The stored form, as it appears in the settings file and on the controls page.</summary>
    public override string ToString() => Kind switch
    {
        BindingKind.ControllerButton => $"Pad:{(ControllerButton)Code}",
        BindingKind.ControllerAxis => $"Pad:{(ControllerAxis)Code}{(Positive ? "+" : "-")}",
        _ => Describe(),
    };

    private string Describe()
    {
        var name = KeyNames.Name(Code);
        if (Modifiers == KeyModifiers.None) return name;

        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(name);
        return string.Join("+", parts);
    }

    /// <summary>Parses the stored form. Returns false rather than throwing on bad input.</summary>
    public static bool TryParse(string? text, out Binding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();

        if (text.StartsWith("Pad:", StringComparison.OrdinalIgnoreCase))
        {
            var name = text[4..];

            if (name.EndsWith('+') || name.EndsWith('-'))
            {
                var axisName = name[..^1];
                if (Enum.TryParse<ControllerAxis>(axisName, ignoreCase: true, out var axis))
                {
                    binding = Axis(axis, name.EndsWith('+'));
                    return true;
                }
            }

            if (Enum.TryParse<ControllerButton>(name, ignoreCase: true, out var button))
            {
                binding = Button(button);
                return true;
            }

            return false;
        }

        var modifiers = KeyModifiers.None;
        var segments = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A trailing "+" is the plus key itself, which Split has just eaten.
        if (text.EndsWith('+') && segments.Length > 0) segments = [.. segments, "+"];

        for (var i = 0; i < segments.Length - 1; i++)
        {
            switch (segments[i].ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= KeyModifiers.Control; break;
                case "alt" or "option": modifiers |= KeyModifiers.Alt; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                default: return false;
            }
        }

        if (segments.Length == 0) return false;
        if (!KeyNames.TryParse(segments[^1], out var keycode)) return false;

        binding = Key(keycode, modifiers);
        return true;
    }
}

/// <summary>Game controller buttons, matching SDL's controller layout.</summary>
public enum ControllerButton
{
    /// <summary>Bottom face button.</summary>
    A = 0,

    /// <summary>Right face button.</summary>
    B = 1,

    /// <summary>Left face button.</summary>
    X = 2,

    /// <summary>Top face button.</summary>
    Y = 3,

    /// <summary>Back or select.</summary>
    Back = 4,

    /// <summary>Guide or home.</summary>
    Guide = 5,

    /// <summary>Start.</summary>
    Start = 6,

    /// <summary>Left stick pressed in.</summary>
    LeftStick = 7,

    /// <summary>Right stick pressed in.</summary>
    RightStick = 8,

    /// <summary>Left shoulder.</summary>
    LeftShoulder = 9,

    /// <summary>Right shoulder.</summary>
    RightShoulder = 10,

    /// <summary>D-pad up.</summary>
    DPadUp = 11,

    /// <summary>D-pad down.</summary>
    DPadDown = 12,

    /// <summary>D-pad left.</summary>
    DPadLeft = 13,

    /// <summary>D-pad right.</summary>
    DPadRight = 14,
}

/// <summary>Game controller axes, matching SDL's controller layout.</summary>
public enum ControllerAxis
{
    /// <summary>Left stick, horizontal.</summary>
    LeftX = 0,

    /// <summary>Left stick, vertical.</summary>
    LeftY = 1,

    /// <summary>Right stick, horizontal.</summary>
    RightX = 2,

    /// <summary>Right stick, vertical.</summary>
    RightY = 3,

    /// <summary>Left trigger.</summary>
    LeftTrigger = 4,

    /// <summary>Right trigger.</summary>
    RightTrigger = 5,
}
