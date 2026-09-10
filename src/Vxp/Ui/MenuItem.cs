using Vxp.Input;

namespace Vxp.Ui;

/// <summary>One row of a menu page.</summary>
public abstract class MenuItem
{
    /// <summary>Text shown on the left of the row.</summary>
    public required string Label { get; init; }

    /// <summary>One-line explanation shown at the foot of the page.</summary>
    public string? Help { get; init; }

    /// <summary>False for headings and separators, which cannot be highlighted.</summary>
    public virtual bool Selectable => true;

    /// <summary>Text shown on the right of the row.</summary>
    public virtual string Value => string.Empty;

    /// <summary>Called when the item is activated. Returns true if anything changed.</summary>
    public virtual bool Activate() => false;

    /// <summary>Called when the item is nudged left or right. Returns true if anything changed.</summary>
    public virtual bool Adjust(int direction) => false;

    /// <summary>Restores the item to its default. Returns true if anything changed.</summary>
    public virtual bool Reset() => false;
}

/// <summary>A heading or blank line.</summary>
public sealed class MenuHeading : MenuItem
{
    /// <inheritdoc />
    public override bool Selectable => false;
}

/// <summary>A row that runs something when activated.</summary>
public sealed class MenuAction : MenuItem
{
    /// <summary>What to run.</summary>
    public required Action OnActivate { get; init; }

    /// <summary>Optional trailing text, such as a duration or a keyboard hint.</summary>
    public Func<string>? Detail { get; init; }

    /// <inheritdoc />
    public override string Value => Detail?.Invoke() ?? string.Empty;

    /// <inheritdoc />
    public override bool Activate()
    {
        OnActivate();
        return true;
    }
}

/// <summary>A row that opens another page.</summary>
public sealed class MenuSubmenu : MenuItem
{
    /// <summary>Builds the page to open. Called each time, so pages stay up to date.</summary>
    public required Func<MenuPage> Open { get; init; }

    /// <summary>Set by the controller so activating the row can push the new page.</summary>
    internal Action<MenuPage>? Push { get; set; }

    /// <inheritdoc />
    public override string Value => ">";

    /// <inheritdoc />
    public override bool Activate()
    {
        Push?.Invoke(Open());
        return true;
    }
}

/// <summary>An on/off setting.</summary>
public sealed class MenuToggle : MenuItem
{
    /// <summary>Reads the current state.</summary>
    public required Func<bool> Get { get; init; }

    /// <summary>Writes a new state.</summary>
    public required Action<bool> Set { get; init; }

    /// <summary>The value used by <see cref="Reset"/>.</summary>
    public bool Default { get; init; }

    /// <inheritdoc />
    public override string Value => Get() ? "On" : "Off";

    /// <inheritdoc />
    public override bool Activate() => Toggle();

    /// <inheritdoc />
    public override bool Adjust(int direction) => Toggle();

    /// <inheritdoc />
    public override bool Reset()
    {
        Set(Default);
        return true;
    }

    private bool Toggle()
    {
        Set(!Get());
        return true;
    }
}

/// <summary>A whole number setting, adjusted in steps.</summary>
public sealed class MenuNumber : MenuItem
{
    /// <summary>Reads the current value.</summary>
    public required Func<int> Get { get; init; }

    /// <summary>Writes a new value.</summary>
    public required Action<int> Set { get; init; }

    /// <summary>Smallest allowed value.</summary>
    public required int Minimum { get; init; }

    /// <summary>Largest allowed value.</summary>
    public required int Maximum { get; init; }

    /// <summary>How much one nudge changes the value.</summary>
    public int Step { get; init; } = 1;

    /// <summary>The value used by <see cref="Reset"/>.</summary>
    public int Default { get; init; }

    /// <summary>Renders the value; defaults to the plain number.</summary>
    public Func<int, string>? Format { get; init; }

    /// <inheritdoc />
    public override string Value
    {
        get
        {
            var value = Get();
            return Format?.Invoke(value) ?? value.ToString();
        }
    }

    /// <inheritdoc />
    public override bool Adjust(int direction)
    {
        var value = Math.Clamp(Get() + direction * Step, Minimum, Maximum);
        if (value == Get()) return false;

        Set(value);
        return true;
    }

    /// <inheritdoc />
    public override bool Activate() => Adjust(1);

    /// <inheritdoc />
    public override bool Reset()
    {
        Set(Math.Clamp(Default, Minimum, Maximum));
        return true;
    }
}

/// <summary>A setting chosen from a fixed list.</summary>
public sealed class MenuChoice : MenuItem
{
    /// <summary>The options, in the order they cycle.</summary>
    public required IReadOnlyList<string> Options { get; init; }

    /// <summary>Reads the selected index.</summary>
    public required Func<int> Get { get; init; }

    /// <summary>Writes the selected index.</summary>
    public required Action<int> Set { get; init; }

    /// <summary>The index used by <see cref="Reset"/>.</summary>
    public int Default { get; init; }

    /// <inheritdoc />
    public override string Value
    {
        get
        {
            var index = Get();
            return index >= 0 && index < Options.Count ? Options[index] : "?";
        }
    }

    /// <inheritdoc />
    public override bool Adjust(int direction)
    {
        if (Options.Count == 0) return false;

        var index = (Get() + direction) % Options.Count;
        if (index < 0) index += Options.Count;

        Set(index);
        return true;
    }

    /// <inheritdoc />
    public override bool Activate() => Adjust(1);

    /// <inheritdoc />
    public override bool Reset()
    {
        Set(Default);
        return true;
    }
}

/// <summary>
/// A control binding. Activating it puts the menu into capture mode, where the next
/// control pressed becomes the binding.
/// </summary>
public sealed class MenuBinding : MenuItem
{
    /// <summary>The action being bound.</summary>
    public required InputAction Action { get; init; }

    /// <summary>The map being edited.</summary>
    public required InputMap Map { get; init; }

    /// <summary>Set by the controller so activating the row starts capture.</summary>
    internal Action<InputAction>? BeginCapture { get; set; }

    /// <inheritdoc />
    public override string Value => Map.Describe(Action);

    /// <inheritdoc />
    public override bool Activate()
    {
        BeginCapture?.Invoke(Action);
        return false;
    }

    /// <inheritdoc />
    public override bool Reset()
    {
        Map.ResetToDefault(Action);
        return true;
    }

    /// <inheritdoc />
    public override bool Adjust(int direction)
    {
        if (direction >= 0) return false;

        Map.Clear(Action);
        return true;
    }
}

/// <summary>A page of menu items.</summary>
public sealed class MenuPage
{
    /// <summary>Heading shown at the top of the page.</summary>
    public required string Title { get; init; }

    /// <summary>The rows.</summary>
    public required IReadOnlyList<MenuItem> Items { get; init; }

    /// <summary>Index of the highlighted row.</summary>
    public int Selected { get; set; }

    /// <summary>Row at the top of the visible window, for pages that scroll.</summary>
    public int ScrollTop { get; set; }

    /// <summary>Optional note shown under the title.</summary>
    public string? Subtitle { get; init; }

    /// <summary>The highlighted item, or null if the page is empty.</summary>
    public MenuItem? Current => Selected >= 0 && Selected < Items.Count ? Items[Selected] : null;

    /// <summary>Moves the highlight, skipping headings and wrapping at the ends.</summary>
    public void Move(int direction)
    {
        if (Items.Count == 0) return;

        for (var step = 0; step < Items.Count; step++)
        {
            Selected += direction;

            if (Selected < 0) Selected = Items.Count - 1;
            else if (Selected >= Items.Count) Selected = 0;

            if (Items[Selected].Selectable) return;
        }
    }

    /// <summary>Puts the highlight on the first row that can take it.</summary>
    public void SelectFirst()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (!Items[i].Selectable) continue;
            Selected = i;
            return;
        }

        Selected = 0;
    }
}
