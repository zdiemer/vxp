using System.Runtime.InteropServices;

namespace Vxp.Ui.Native;

/// <summary>
/// Renders the menu model as a real Windows menu bar attached to the player's window.
/// </summary>
/// <remarks>
/// <para>
/// The bar is built from the same <see cref="MenuPage"/> objects the in-window menu
/// draws, so there is one description of the menus and two ways of showing them. The
/// mapping is the obvious one: an action becomes a command, a toggle becomes a checked
/// item, a choice or a number becomes a submenu of marked options, and a binding becomes
/// a read-only row showing the control it sits on, because a native menu has nowhere to
/// capture a keypress.
/// </para>
/// <para>
/// Item text and marks are refreshed on <c>WM_INITMENUPOPUP</c>, just before a popup is
/// shown, so the bar reflects the settings as they stand without being rebuilt. Changes
/// to which rows exist need a rebuild, which the host defers until no menu is open.
/// </para>
/// </remarks>
internal sealed class Win32MenuBar : IDisposable
{
    private const int FirstCommandId = 0x1000;
    private const int MaximumDepth = 5;

    private readonly nint _window;
    private readonly Action _changed;
    private readonly Dictionary<int, Node> _byCommand = [];
    private readonly Dictionary<nint, PopupNode> _byPopup = [];
    private readonly HashSet<char> _mnemonics = [];

    private nint _bar;
    private int _nextCommandId = FirstCommandId;
    private bool _disposed;

    /// <summary>Builds the bar. Nothing appears until <see cref="Attach"/> is called.</summary>
    public Win32MenuBar(nint window, IReadOnlyList<MenuBarSection> sections, Action changed)
    {
        _window = window;
        _changed = changed;

        _bar = Win32.CreateMenu();
        if (_bar == 0) throw new InvalidOperationException("CreateMenu failed.");

        foreach (var section in sections)
        {
            var title = section.Title;
            BuildPopup(() => title, section.Page(), depth: 0).Append(_bar);

            var marker = title.IndexOf('&', StringComparison.Ordinal);
            if (marker >= 0 && marker + 1 < title.Length) _mnemonics.Add(char.ToLowerInvariant(title[marker + 1]));
        }
    }

    /// <summary>
    /// Whether a character is the mnemonic of one of the bar's menus, so Alt with that
    /// key should open it.
    /// </summary>
    public bool HasMnemonic(char key) => _mnemonics.Contains(char.ToLowerInvariant(key));

    /// <summary>Puts the bar on the window.</summary>
    public void Attach()
    {
        Win32.SetMenu(_window, _bar);
        Win32.DrawMenuBar(_window);
    }

    /// <summary>
    /// Takes the bar off the window without destroying it, for full screen.
    /// </summary>
    /// <remarks>
    /// Does nothing if some other bar has since taken the window, so that disposing a bar
    /// that has already been replaced cannot strip the replacement off.
    /// </remarks>
    public void Detach()
    {
        if (Win32.GetMenu(_window) != _bar) return;

        Win32.SetMenu(_window, 0);
        Win32.DrawMenuBar(_window);
    }

    /// <summary>
    /// Grows the window so the client area is as tall as it was before the bar took a
    /// strip off the top. SDL sizes windows without accounting for a menu, so without
    /// this the picture loses that strip on every launch.
    /// </summary>
    public void PreserveClientHeight(int desiredHeight)
    {
        if (!Win32.GetClientRect(_window, out var client)) return;

        var shortfall = desiredHeight - client.Height;
        if (shortfall <= 0) return;

        if (!Win32.GetWindowRect(_window, out var frame)) return;

        Win32.SetWindowPos(
            _window, 0, 0, 0, frame.Width, frame.Height + shortfall,
            Win32.SwpNoMove | Win32.SwpNoZOrder | Win32.SwpNoActivate);
    }

    /// <summary>Runs the command behind a menu id. Returns false if the id is not ours.</summary>
    public bool Invoke(int commandId)
    {
        if (!_byCommand.TryGetValue(commandId, out var node)) return false;

        node.Activate();
        _changed();
        return true;
    }

    /// <summary>Brings one popup's labels and marks up to date, just before it is shown.</summary>
    public void RefreshPopup(nint popup)
    {
        if (!_byPopup.TryGetValue(popup, out var node)) return;

        foreach (var child in node.Children) child.Refresh(popup);
    }

    private PopupNode BuildPopup(Func<string> text, MenuPage page, int depth)
    {
        var children = new List<Node>();
        foreach (var item in page.Items) Add(children, item, depth);

        // An empty popup renders as a stray grey box, which reads as a bug.
        if (children.Count == 0) children.Add(new LabelNode(() => "(nothing here)"));

        return Finish(text, children);
    }

    private PopupNode Finish(Func<string> text, List<Node> children)
    {
        var handle = Win32.CreatePopupMenu();

        for (var i = 0; i < children.Count; i++)
        {
            children[i].Position = i;
            children[i].Append(handle);

            // Append can only carry a plain label, so the first refresh is what puts the
            // check and radio marks on. Without it a popup would come up unmarked the
            // very first time it is shown.
            children[i].Refresh(handle);
        }

        var popup = new PopupNode(text, handle, children);
        _byPopup[handle] = popup;
        return popup;
    }

    private void Add(List<Node> children, MenuItem item, int depth)
    {
        switch (item)
        {
            case MenuHeading heading:
                children.Add(string.IsNullOrWhiteSpace(heading.Label)
                    ? new SeparatorNode()
                    : new LabelNode(() => NativeMenuLayout.Escape(heading.Label)));
                break;

            case MenuSubmenu submenu when depth < MaximumDepth:
                children.Add(BuildPopup(() => NativeMenuLayout.Escape(submenu.Label), submenu.Open(), depth + 1));
                break;

            case MenuSubmenu submenu:
                children.Add(new LabelNode(() => NativeMenuLayout.Escape(submenu.Label)));
                break;

            case MenuToggle toggle:
                children.Add(Register(new MarkNode(
                    NextId(),
                    () => NativeMenuLayout.Escape(toggle.Label),
                    toggle.Get,
                    () => toggle.Activate(),
                    radio: false)));
                break;

            case MenuChoice choice:
                children.Add(BuildChoice(choice));
                break;

            case MenuNumber number:
                children.Add(BuildNumber(number));
                break;

            // Rebinding needs a captured keypress, which a native menu cannot take, so
            // the row is read-only and the in-window Controls page does the capturing.
            case MenuBinding binding:
                children.Add(new LabelNode(() => NativeMenuLayout.Compose(binding.Label, binding.Value)));
                break;

            case MenuAction action:
                children.Add(Register(new ActionNode(
                    NextId(),
                    () => NativeMenuLayout.Compose(action.Label, action.Value),
                    () => action.Activate())));
                break;
        }
    }

    private PopupNode BuildChoice(MenuChoice choice)
    {
        var children = new List<Node>();

        for (var i = 0; i < choice.Options.Count; i++)
        {
            var index = i;
            children.Add(Register(new MarkNode(
                NextId(),
                () => NativeMenuLayout.Escape(choice.Options[index]),
                () => choice.Get() == index,
                () => choice.Set(index),
                radio: true)));
        }

        return Finish(() => NativeMenuLayout.Compose(choice.Label, choice.Value), children);
    }

    private PopupNode BuildNumber(MenuNumber number)
    {
        var children = new List<Node>();

        foreach (var stop in NativeMenuLayout.NumberStops(number))
        {
            var value = stop;
            children.Add(Register(new MarkNode(
                NextId(),
                () => NativeMenuLayout.Escape(number.Format?.Invoke(value) ?? value.ToString()),
                () => number.Get() == value,
                () => number.Set(value),
                radio: true)));
        }

        children.Add(new SeparatorNode());
        children.Add(Register(new ActionNode(NextId(), () => "Reset", () => number.Reset())));

        return Finish(() => NativeMenuLayout.Compose(number.Label, number.Value), children);
    }

    private T Register<T>(T node) where T : Node
    {
        _byCommand[node.CommandId] = node;
        return node;
    }

    private int NextId() => _nextCommandId++;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Detach();

        // DestroyMenu frees every popup hanging off the bar along with it.
        if (_bar != 0) Win32.DestroyMenu(_bar);
        _bar = 0;

        _byCommand.Clear();
        _byPopup.Clear();
    }

    /// <summary>One row of a native popup.</summary>
    private abstract class Node
    {
        /// <summary>Index of the row within its popup, which is how it is addressed.</summary>
        public int Position { get; set; }

        /// <summary>Menu command id, or zero for rows that cannot be chosen.</summary>
        public virtual int CommandId => 0;

        /// <summary>Adds the row to a freshly created popup.</summary>
        public abstract void Append(nint menu);

        /// <summary>Brings the row's text and marks up to date.</summary>
        public virtual void Refresh(nint menu) { }

        /// <summary>Runs whatever the row does.</summary>
        public virtual void Activate() { }

        protected static void Update(nint menu, int position, string text, uint type, uint state)
        {
            var info = new Win32.MenuItemInfo
            {
                Size = (uint)Marshal.SizeOf<Win32.MenuItemInfo>(),
                Mask = Win32.MiimString | Win32.MiimState | Win32.MiimFType,
                Type = type,
                State = state,
            };

            var buffer = Marshal.StringToHGlobalUni(text);
            try
            {
                info.TypeData = buffer;
                Win32.SetMenuItemInfo(menu, (uint)position, byPosition: true, ref info);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private sealed class SeparatorNode : Node
    {
        public override void Append(nint menu) => Win32.AppendMenu(menu, Win32.MfSeparator, 0, null);
    }

    /// <summary>A row that shows something but cannot be chosen.</summary>
    private sealed class LabelNode(Func<string> text) : Node
    {
        public override void Append(nint menu)
            => Win32.AppendMenu(menu, Win32.MfString | Win32.MfGrayed, 0, text());

        public override void Refresh(nint menu)
            => Update(menu, Position, text(), Win32.MftString, Win32.MfsGrayed);
    }

    /// <summary>A row that runs a command.</summary>
    private sealed class ActionNode(int id, Func<string> text, Action invoke) : Node
    {
        public override int CommandId => id;

        public override void Append(nint menu)
            => Win32.AppendMenu(menu, Win32.MfString, (nuint)id, text());

        public override void Refresh(nint menu)
            => Update(menu, Position, text(), Win32.MftString, Win32.MfsEnabled);

        public override void Activate() => invoke();
    }

    /// <summary>A row carrying a check mark or a radio mark.</summary>
    private sealed class MarkNode(int id, Func<string> text, Func<bool> isMarked, Action activate, bool radio) : Node
    {
        public override int CommandId => id;

        public override void Append(nint menu)
            => Win32.AppendMenu(menu, Win32.MfString, (nuint)id, text());

        public override void Refresh(nint menu)
            => Update(
                menu, Position, text(),
                radio ? Win32.MftString | Win32.MftRadioCheck : Win32.MftString,
                isMarked() ? Win32.MfsChecked : Win32.MfsUnchecked);

        public override void Activate() => activate();
    }

    /// <summary>A row that opens a submenu.</summary>
    private sealed class PopupNode(Func<string> text, nint handle, List<Node> children) : Node
    {
        public List<Node> Children => children;

        public override void Append(nint menu)
            => Win32.AppendMenu(menu, Win32.MfString | Win32.MfPopup, (nuint)handle, text());

        public override void Refresh(nint menu)
            => Update(menu, Position, text(), Win32.MftString, Win32.MfsEnabled);
    }
}
