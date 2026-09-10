using Vxp.Input;

namespace Vxp.Ui;

/// <summary>
/// Owns the stack of open menu pages, turns input actions into navigation, and draws the
/// whole thing onto a <see cref="Canvas"/>.
/// </summary>
public sealed class MenuController
{
    private readonly Stack<MenuPage> _pages = new();

    private InputAction? _capturing;
    private string? _toast;
    private DateTime _toastUntil;

    /// <summary>True while a menu is open.</summary>
    public bool IsOpen => _pages.Count > 0;

    /// <summary>True while a message is still on screen, so the host keeps drawing.</summary>
    public bool HasToast => _toast is not null && DateTime.UtcNow < _toastUntil;

    /// <summary>
    /// True while waiting for the viewer to press the control they want to bind. The host
    /// must route raw input here instead of acting on it.
    /// </summary>
    public bool IsCapturing => _capturing is not null;

    /// <summary>Raised when a setting changes, so the host can save and apply it.</summary>
    public event Action? Changed;

    /// <summary>Opens a page, putting any current page behind it.</summary>
    public void Push(MenuPage page)
    {
        page.SelectFirst();
        _pages.Push(page);
    }

    /// <summary>Closes the top page, and the menu itself if it was the last one.</summary>
    public void Pop()
    {
        if (_pages.Count > 0) _pages.Pop();
    }

    /// <summary>Closes every page.</summary>
    public void Close()
    {
        _pages.Clear();
        _capturing = null;
    }

    /// <summary>Shows a short message at the foot of the screen.</summary>
    public void Toast(string message, double seconds = 2.0)
    {
        _toast = message;
        _toastUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    /// <summary>Handles a navigation action. Returns true if the menu consumed it.</summary>
    public bool Handle(InputAction action)
    {
        if (!IsOpen) return false;

        var page = _pages.Peek();

        switch (action)
        {
            case InputAction.MenuUp:
                page.Move(-1);
                return true;

            case InputAction.MenuDown:
                page.Move(1);
                return true;

            case InputAction.MenuPageUp:
                for (var i = 0; i < 8; i++) page.Move(-1);
                return true;

            case InputAction.MenuPageDown:
                for (var i = 0; i < 8; i++) page.Move(1);
                return true;

            case InputAction.MenuLeft:
                if (page.Current?.Adjust(-1) == true) Changed?.Invoke();
                return true;

            case InputAction.MenuRight:
                if (page.Current?.Adjust(1) == true) Changed?.Invoke();
                return true;

            case InputAction.MenuSelect:
                Activate(page);
                return true;

            case InputAction.MenuResetItem:
                if (page.Current?.Reset() == true)
                {
                    Changed?.Invoke();
                    Toast("Reset to default");
                }

                return true;

            case InputAction.MenuBack or InputAction.ToggleMenu:
                Pop();
                return true;

            default:
                // While a menu is open it swallows everything else, so playback controls
                // do not fire behind it.
                return true;
        }
    }

    private void Activate(MenuPage page)
    {
        var item = page.Current;
        if (item is null) return;

        if (item is MenuSubmenu submenu) submenu.Push = Push;
        if (item is MenuBinding binding) binding.BeginCapture = action => _capturing = action;

        if (item.Activate()) Changed?.Invoke();
    }

    /// <summary>
    /// Offers a control to the binding capture. Returns true if it was taken, in which
    /// case the host must not treat it as an ordinary input.
    /// </summary>
    public bool Capture(Binding binding, InputMap map)
    {
        if (_capturing is not { } action) return false;

        _capturing = null;

        // Escape leaves the binding alone, which is the only way out of capture mode.
        if (binding.Kind == BindingKind.Key && binding.Code == 27)
        {
            Toast("Binding unchanged");
            return true;
        }

        var conflicts = map.Conflicts(binding, action);
        map.Rebind(action, binding);
        Changed?.Invoke();

        Toast(conflicts.Count == 0
            ? $"{InputActions.Label(action)} = {binding}"
            : $"{binding} also triggers {InputActions.Label(conflicts[0])}");

        return true;
    }

    /// <summary>Draws the menu, if one is open, plus any toast message.</summary>
    public void Draw(Canvas canvas, int scale, bool dimBackground)
    {
        DrawToast(canvas, scale);
        if (!IsOpen) return;

        var page = _pages.Peek();

        if (dimBackground) canvas.Dim(150);

        var lineHeight = BitmapFont.LineAdvance * scale;
        var padding = 8 * scale;

        var panelWidth = Math.Min(canvas.Width - padding * 2, Math.Max(64 * BitmapFont.Advance * scale, 320));
        var headerHeight = lineHeight + padding + (page.Subtitle is null ? 0 : lineHeight);
        var footerHeight = lineHeight + padding;

        var available = canvas.Height - padding * 2 - headerHeight - footerHeight;
        var visibleRows = Math.Max(1, available / lineHeight);

        EnsureVisible(page, visibleRows);

        var panelHeight = headerHeight + footerHeight + Math.Min(visibleRows, page.Items.Count) * lineHeight + padding;
        var panelX = (canvas.Width - panelWidth) / 2;
        var panelY = (canvas.Height - panelHeight) / 2;

        canvas.Fill(panelX, panelY, panelWidth, panelHeight, Rgba.Panel);
        canvas.Fill(panelX, panelY, panelWidth, headerHeight, Rgba.PanelHeader);
        canvas.Outline(panelX, panelY, panelWidth, panelHeight, Rgba.Accent.WithAlpha(140));

        canvas.Text(panelX + padding, panelY + padding / 2, page.Title, scale, Rgba.Accent);

        if (page.Subtitle is not null)
        {
            canvas.Text(
                panelX + padding, panelY + padding / 2 + lineHeight,
                BitmapFont.Fit(page.Subtitle, panelWidth - padding * 2, scale), scale, Rgba.Grey);
        }

        DrawRows(canvas, page, scale, panelX, panelY + headerHeight, panelWidth, padding, lineHeight, visibleRows);
        DrawFooter(canvas, page, scale, panelX, panelY + panelHeight - footerHeight, panelWidth, padding);
    }

    private void DrawRows(
        Canvas canvas, MenuPage page, int scale,
        int panelX, int top, int panelWidth, int padding, int lineHeight, int visibleRows)
    {
        var last = Math.Min(page.Items.Count, page.ScrollTop + visibleRows);

        for (var i = page.ScrollTop; i < last; i++)
        {
            var item = page.Items[i];
            var y = top + (i - page.ScrollTop) * lineHeight;
            var selected = i == page.Selected && item.Selectable;

            if (selected) canvas.Fill(panelX + padding / 2, y - scale, panelWidth - padding, lineHeight, Rgba.Accent);

            var labelColor = selected ? Rgba.Black : item.Selectable ? Rgba.White : Rgba.Accent;
            var valueColor = selected ? Rgba.Black : Rgba.Grey;

            var valueText = item.Value;
            var valueWidth = BitmapFont.Measure(valueText, scale);
            var labelSpace = panelWidth - padding * 2 - valueWidth - BitmapFont.Advance * scale;

            canvas.Text(panelX + padding, y, BitmapFont.Fit(item.Label, labelSpace, scale), scale, labelColor);

            if (valueText.Length > 0)
            {
                var capturing = IsCapturing && selected && item is MenuBinding;
                canvas.TextRight(
                    panelX + panelWidth - padding, y,
                    capturing ? "press a control..." : valueText,
                    scale,
                    capturing ? Rgba.Warn : valueColor);
            }
        }

        // Scroll indicators, so a long list does not look like it ends here.
        if (page.ScrollTop > 0) canvas.TextRight(panelX + panelWidth - padding / 2, top - lineHeight / 2, "^", scale, Rgba.Grey);
        if (last < page.Items.Count) canvas.TextRight(panelX + panelWidth - padding / 2, top + visibleRows * lineHeight - lineHeight / 2, "v", scale, Rgba.Grey);
    }

    private static void DrawFooter(Canvas canvas, MenuPage page, int scale, int panelX, int y, int panelWidth, int padding)
    {
        var help = page.Current?.Help ?? "Arrows move and adjust, Enter selects, Delete resets, Escape goes back.";
        canvas.Text(panelX + padding, y, BitmapFont.Fit(help, panelWidth - padding * 2, scale), scale, Rgba.Grey);
    }

    private static void EnsureVisible(MenuPage page, int visibleRows)
    {
        if (page.Selected < page.ScrollTop) page.ScrollTop = page.Selected;
        else if (page.Selected >= page.ScrollTop + visibleRows) page.ScrollTop = page.Selected - visibleRows + 1;

        page.ScrollTop = Math.Clamp(page.ScrollTop, 0, Math.Max(0, page.Items.Count - visibleRows));
    }

    private void DrawToast(Canvas canvas, int scale)
    {
        if (_toast is null) return;

        if (DateTime.UtcNow > _toastUntil)
        {
            _toast = null;
            return;
        }

        var padding = 6 * scale;
        var width = BitmapFont.Measure(_toast, scale) + padding * 2;
        var height = BitmapFont.LineHeight(scale) + padding;
        var x = (canvas.Width - width) / 2;
        var y = canvas.Height - height - 12 * scale;

        canvas.Fill(x, y, width, height, Rgba.Panel);
        canvas.Outline(x, y, width, height, Rgba.Accent.WithAlpha(120));
        canvas.Text(x + padding, y + padding / 2, _toast, scale, Rgba.White);
    }
}
