namespace Vxp.Input;

/// <summary>
/// The binding table: which controls trigger which actions. Every action can carry
/// several bindings, so keyboard and controller can drive the same thing.
/// </summary>
public sealed class InputMap
{
    private readonly Dictionary<InputAction, List<Binding>> _bindings = new();

    private InputMap()
    {
    }

    /// <summary>The bindings the emulator ships with.</summary>
    public static InputMap CreateDefault()
    {
        var map = new InputMap();

        void Bind(InputAction action, params Binding[] bindings) => map._bindings[action] = [.. bindings];

        int Key(string name) => KeyNames.TryParse(name, out var code) ? code : 0;

        Bind(InputAction.TogglePause, Binding.Key(Key("Space")), Binding.Button(ControllerButton.A));
        Bind(InputAction.Stop, Binding.Key(Key("Backspace")), Binding.Button(ControllerButton.B));
        Bind(InputAction.NextTrack, Binding.Key(Key("Right")), Binding.Button(ControllerButton.RightShoulder));
        Bind(InputAction.PreviousTrack, Binding.Key(Key("Left")), Binding.Button(ControllerButton.LeftShoulder));
        Bind(InputAction.GoBack, Binding.Key(Key("B")));
        Bind(InputAction.SeekForward, Binding.Key(Key("Right"), KeyModifiers.Shift));
        Bind(InputAction.SeekBackward, Binding.Key(Key("Left"), KeyModifiers.Shift));
        Bind(InputAction.FastForward, Binding.Key(Key("F")), Binding.Axis(ControllerAxis.RightTrigger, positive: true));
        Bind(InputAction.FrameForward, Binding.Key(Key("Period")));
        Bind(InputAction.FrameBackward, Binding.Key(Key("Comma")));
        Bind(InputAction.SpeedUp, Binding.Key(Key("Equals")));
        Bind(InputAction.SpeedDown, Binding.Key(Key("Minus")));
        Bind(InputAction.SpeedReset, Binding.Key(Key("0")));
        Bind(InputAction.ToggleLoop, Binding.Key(Key("L")));

        // The discs mapped so far offer at most four branches, so the D-pad covers the
        // choices a pad player meets; X and Y reach the last two slots the header has room for. The D-pad
        // is also menu navigation, which InputRouter keeps apart by whether a menu is open.
        Bind(InputAction.Choice1, Binding.Key(Key("1")), Binding.Button(ControllerButton.DPadUp));
        Bind(InputAction.Choice2, Binding.Key(Key("2")), Binding.Button(ControllerButton.DPadRight));
        Bind(InputAction.Choice3, Binding.Key(Key("3")), Binding.Button(ControllerButton.DPadDown));
        Bind(InputAction.Choice4, Binding.Key(Key("4")), Binding.Button(ControllerButton.DPadLeft));
        Bind(InputAction.Choice5, Binding.Key(Key("5")), Binding.Button(ControllerButton.X));
        Bind(InputAction.Choice6, Binding.Key(Key("6")), Binding.Button(ControllerButton.Y));

        Bind(InputAction.VolumeUp, Binding.Key(Key("Up")));
        Bind(InputAction.VolumeDown, Binding.Key(Key("Down")));
        Bind(InputAction.ToggleMute, Binding.Key(Key("M")));

        Bind(InputAction.ToggleFullscreen, Binding.Key(Key("F11")), Binding.Key(Key("Return"), KeyModifiers.Alt));
        Bind(InputAction.ToggleOverlay, Binding.Key(Key("Tab")));
        Bind(InputAction.Screenshot, Binding.Key(Key("F12")));

        Bind(InputAction.ToggleMenu, Binding.Key(Key("Escape")), Binding.Button(ControllerButton.Start));
        Bind(InputAction.TrackBrowser, Binding.Key(Key("T")), Binding.Button(ControllerButton.Back));

        Bind(InputAction.MenuUp, Binding.Key(Key("Up")), Binding.Button(ControllerButton.DPadUp));
        Bind(InputAction.MenuDown, Binding.Key(Key("Down")), Binding.Button(ControllerButton.DPadDown));
        Bind(InputAction.MenuLeft, Binding.Key(Key("Left")), Binding.Button(ControllerButton.DPadLeft));
        Bind(InputAction.MenuRight, Binding.Key(Key("Right")), Binding.Button(ControllerButton.DPadRight));
        Bind(InputAction.MenuSelect, Binding.Key(Key("Return")), Binding.Button(ControllerButton.A));
        Bind(InputAction.MenuBack, Binding.Key(Key("Escape")), Binding.Button(ControllerButton.B));
        Bind(InputAction.MenuPageUp, Binding.Key(Key("PageUp")));
        Bind(InputAction.MenuPageDown, Binding.Key(Key("PageDown")));
        Bind(InputAction.MenuResetItem, Binding.Key(Key("Delete")), Binding.Button(ControllerButton.Y));

        Bind(InputAction.OpenDisc, Binding.Key(Key("O"), KeyModifiers.Control));
        Bind(InputAction.CloseDisc, Binding.Key(Key("W"), KeyModifiers.Control));
        Bind(InputAction.Quit, Binding.Key(Key("Q"), KeyModifiers.Control), Binding.Button(ControllerButton.Guide));

        return map;
    }

    /// <summary>
    /// Builds a map from stored settings, falling back to the default binding for any
    /// action the file does not mention. An action bound to an empty list stays unbound.
    /// </summary>
    public static InputMap FromSettings(IReadOnlyDictionary<string, List<string>> stored)
    {
        var map = CreateDefault();

        foreach (var (name, values) in stored)
        {
            if (!InputActions.TryParse(name, out var action)) continue;

            var parsed = new List<Binding>(values.Count);
            foreach (var value in values)
            {
                if (Binding.TryParse(value, out var binding)) parsed.Add(binding);
            }

            map._bindings[action] = parsed;
        }

        return map;
    }

    /// <summary>Renders the map back into the form stored in the settings file.</summary>
    public Dictionary<string, List<string>> ToSettings()
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var action in InputActions.All)
            result[action.ToString()] = BindingsFor(action).Select(b => b.ToString()).ToList();

        return result;
    }

    /// <summary>The bindings currently assigned to an action.</summary>
    public IReadOnlyList<Binding> BindingsFor(InputAction action)
        => _bindings.TryGetValue(action, out var list) ? list : [];

    /// <summary>A readable summary of an action's bindings, for the controls page.</summary>
    public string Describe(InputAction action)
    {
        var bindings = BindingsFor(action);
        return bindings.Count == 0 ? "unbound" : string.Join(", ", bindings.Select(b => b.ToString()));
    }

    /// <summary>Replaces every binding for an action with a single control.</summary>
    public void Rebind(InputAction action, Binding binding) => _bindings[action] = [binding];

    /// <summary>Adds another control for an action, ignoring duplicates.</summary>
    public void AddBinding(InputAction action, Binding binding)
    {
        var list = _bindings.TryGetValue(action, out var existing) ? existing : _bindings[action] = [];
        if (!list.Contains(binding)) list.Add(binding);
    }

    /// <summary>Removes every binding for an action.</summary>
    public void Clear(InputAction action) => _bindings[action] = [];

    /// <summary>Restores one action to its shipped bindings.</summary>
    public void ResetToDefault(InputAction action)
        => _bindings[action] = [.. CreateDefault().BindingsFor(action)];

    /// <summary>Restores every action to its shipped bindings.</summary>
    public void ResetAll()
    {
        _bindings.Clear();
        foreach (var (action, bindings) in CreateDefault()._bindings) _bindings[action] = bindings;
    }

    /// <summary>Actions that <paramref name="binding"/> would also trigger, other than <paramref name="excluding"/>.</summary>
    public IReadOnlyList<InputAction> Conflicts(Binding binding, InputAction excluding)
        => _bindings
            .Where(kv => kv.Key != excluding && kv.Value.Contains(binding))
            .Select(kv => kv.Key)
            .ToArray();

    /// <summary>
    /// Every action a keyboard event triggers. Bindings with modifiers are preferred, so
    /// binding Shift+Left to seek does not also fire the plain Left binding.
    /// </summary>
    public IReadOnlyList<InputAction> MatchKey(int keycode, KeyModifiers modifiers)
    {
        var exact = new List<InputAction>();
        var plain = new List<InputAction>();

        foreach (var (action, bindings) in _bindings)
        {
            foreach (var binding in bindings)
            {
                if (binding.Kind != BindingKind.Key || binding.Code != keycode) continue;

                if (binding.Modifiers == modifiers) exact.Add(action);
                else if (binding.Modifiers == KeyModifiers.None) plain.Add(action);
            }
        }

        return exact.Count > 0 ? exact : modifiers == KeyModifiers.None ? plain : [];
    }

    /// <summary>Every action a controller button triggers.</summary>
    public IReadOnlyList<InputAction> MatchButton(int button)
        => _bindings
            .Where(kv => kv.Value.Any(b => b.Kind == BindingKind.ControllerButton && b.Code == button))
            .Select(kv => kv.Key)
            .ToArray();

    /// <summary>Every action a controller axis deflection triggers.</summary>
    public IReadOnlyList<InputAction> MatchAxis(int axis, bool positive)
        => _bindings
            .Where(kv => kv.Value.Any(b =>
                b.Kind == BindingKind.ControllerAxis && b.Code == axis && b.Positive == positive))
            .Select(kv => kv.Key)
            .ToArray();
}
