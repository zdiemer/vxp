namespace Vxp.Ui.Native;

/// <summary>
/// One top-level menu of the native menu bar.
/// </summary>
/// <param name="Title">
/// The bar title. Taken verbatim, so it may carry an <c>&amp;</c> mnemonic such as
/// <c>"&amp;File"</c>. Item labels inside the page are escaped instead, because they
/// include disc and track titles that really do contain ampersands.
/// </param>
/// <param name="Page">
/// Builds the page this menu shows. Called when the bar is built and again whenever the
/// bar is rebuilt, so a page may reflect the current settings.
/// </param>
public sealed record MenuBarSection(string Title, Func<MenuPage> Page);

/// <summary>
/// Turns the parts of the menu model that native menus cannot show directly into
/// something they can.
/// </summary>
/// <remarks>
/// A native menu has commands, check marks, radio marks and submenus, and nothing that
/// behaves like a slider. A <see cref="MenuNumber"/> therefore becomes a submenu of
/// discrete stops with the current value marked, the way a zoom menu works.
/// </remarks>
public static class NativeMenuLayout
{
    /// <summary>Default ceiling on how many stops a number submenu may list.</summary>
    public const int DefaultMaximumStops = 21;

    /// <summary>
    /// Picks the values a <see cref="MenuNumber"/> should offer as a submenu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the range in whole multiples of the item's own step, widening the stride when
    /// the range would otherwise make an unusably long menu. Every stop therefore sits on
    /// a value the item could have reached by nudging.
    /// </para>
    /// <para>
    /// The walk is anchored on the item's default rather than on its minimum, so the value
    /// that matters most is always one of the stops: a speed menu offers 1x and not
    /// 0.75x and 1.25x either side of it. The minimum, the maximum and the current value
    /// are added whatever the stride, so the menu can always show where the setting
    /// stands, which puts the count a little over <paramref name="maximumStops"/> at worst.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> NumberStops(MenuNumber number, int maximumStops = DefaultMaximumStops)
    {
        ArgumentNullException.ThrowIfNull(number);
        if (maximumStops < 2) throw new ArgumentOutOfRangeException(nameof(maximumStops));

        var minimum = number.Minimum;
        var maximum = Math.Max(number.Minimum, number.Maximum);
        var step = Math.Max(1, number.Step);
        var span = maximum - minimum;

        var stride = step;
        if (span / step + 1 > maximumStops) stride = step * (int)Math.Ceiling((span / (double)step + 1) / maximumStops);

        var anchor = Math.Clamp(number.Default, minimum, maximum);
        var stops = new SortedSet<int> { minimum, maximum, Math.Clamp(number.Get(), minimum, maximum) };

        for (var value = anchor; value <= maximum; value += stride) stops.Add(value);
        for (var value = anchor; value >= minimum; value -= stride) stops.Add(value);

        return stops.ToList();
    }

    /// <summary>
    /// Escapes text for a menu item label: <c>&amp;</c> starts a mnemonic and a tab
    /// starts the shortcut column, so both have to be neutralised in text that came from
    /// a disc.
    /// </summary>
    public static string Escape(string text)
        => text.Replace("&", "&&", StringComparison.Ordinal).Replace('\t', ' ');

    /// <summary>Joins a label and its right-hand shortcut column.</summary>
    public static string Compose(string label, string? accessory)
        => string.IsNullOrEmpty(accessory) ? Escape(label) : $"{Escape(label)}\t{Escape(accessory)}";
}
