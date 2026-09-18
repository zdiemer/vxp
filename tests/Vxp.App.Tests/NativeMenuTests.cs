using Vxp.Config;
using Vxp.Input;
using Vxp.Ui;
using Vxp.Ui.Native;
using Xunit;

namespace Vxp.Tests;

public class NumberStopsTests
{
    private static MenuNumber Number(int minimum, int maximum, int step, int value, int fallback = 0)
        => new()
        {
            Label = "Test",
            Get = () => value,
            Set = _ => { },
            Minimum = minimum,
            Maximum = maximum,
            Step = step,
            Default = fallback,
        };

    [Fact]
    public void AShortRangeIsOfferedWholeInStepsOfItsOwn()
    {
        var stops = NativeMenuLayout.NumberStops(Number(1, 5, 1, 3, fallback: 1));

        Assert.Equal([1, 2, 3, 4, 5], stops);
    }

    [Fact]
    public void ALongRangeIsThinnedButStaysOnStepBoundaries()
    {
        var number = Number(-100, 100, 5, 0, fallback: 0);
        var stops = NativeMenuLayout.NumberStops(number, maximumStops: 11);

        Assert.True(stops.Count <= 14, $"expected a short menu, got {stops.Count} stops");
        Assert.All(stops, stop => Assert.True(
            stop == number.Minimum || stop == number.Maximum || stop % 5 == 0,
            $"{stop} is not a value the item could be nudged to"));
    }

    [Fact]
    public void TheEndsAndTheCurrentValueAreAlwaysOffered()
    {
        // 137 is deliberately off any sensible grid.
        var stops = NativeMenuLayout.NumberStops(Number(50, 300, 5, 137, fallback: 100));

        Assert.Contains(50, stops);
        Assert.Contains(300, stops);
        Assert.Contains(137, stops);
    }

    [Fact]
    public void TheDefaultIsOfferedEvenWhenTheStrideIsWidened()
    {
        // Speed: 25 to 800 in steps of 25 is too long to list, but 100 is 1x and has to
        // be on the menu.
        var stops = NativeMenuLayout.NumberStops(Number(25, 800, 25, 400, fallback: 100));

        Assert.Contains(100, stops);
    }

    [Fact]
    public void StopsComeOutInOrderWithNoRepeats()
    {
        var stops = NativeMenuLayout.NumberStops(Number(0, 100, 5, 80, fallback: 80));

        Assert.Equal(stops.OrderBy(v => v), stops);
        Assert.Equal(stops.Distinct().Count(), stops.Count);
    }

    [Fact]
    public void ARangeOfOneValueDoesNotSpin()
    {
        var stops = NativeMenuLayout.NumberStops(Number(7, 7, 1, 7, fallback: 7));

        Assert.Equal([7], stops);
    }
}

public class MenuTextTests
{
    [Fact]
    public void AnAmpersandIsDoubledSoItIsNotReadAsAMnemonic()
    {
        // Real disc title: "Operation - R.O.B.B.E.R.S. & Operation - U.T.O.P.I.A."
        Assert.Equal("R.O.B.B.E.R.S. && Operation", NativeMenuLayout.Escape("R.O.B.B.E.R.S. & Operation"));
    }

    [Fact]
    public void ATabIsRemovedSoItDoesNotStartTheShortcutColumn()
    {
        Assert.Equal("a b", NativeMenuLayout.Escape("a\tb"));
    }

    [Fact]
    public void ComposePutsTheAccessoryInTheShortcutColumn()
    {
        Assert.Equal("Play / Pause\tSpace", NativeMenuLayout.Compose("Play / Pause", "Space"));
    }

    [Fact]
    public void ComposeLeavesOutAnEmptyAccessory()
    {
        Assert.Equal("Stop", NativeMenuLayout.Compose("Stop", null));
        Assert.Equal("Stop", NativeMenuLayout.Compose("Stop", ""));
    }
}

public class MenuBarTests
{
    private static MenuContext Context(VxpSettings? settings = null)
    {
        settings ??= new VxpSettings();

        return new MenuContext
        {
            Settings = settings,
            Input = InputMap.CreateDefault(),
            Player = null,
            Disc = null,
            CloseMenu = () => { },
            Screenshot = () => { },
            ToggleFullscreen = () => { },
            Quit = () => { },
            Toast = _ => { },
            Perform = _ => { },
            SelectTrack = _ => { },
            OpenPage = _ => { },
            ShowInfo = (_, _) => { },
            OpenScreenshots = () => { },
            OpenRecent = _ => { },
        };
    }

    [Fact]
    public void TheBarHasTheMenusAnApplicationIsExpectedToHave()
    {
        var titles = Menus.Bar(Context()).Select(s => s.Title).ToArray();

        Assert.Equal(["&File", "&Playback", "&Tracks", "&View", "&Help"], titles);
    }

    // Tracks is left out: it is the one page that needs a mounted disc.
    private static MenuPage[] PagesWithoutADisc(MenuContext context)
        => [Menus.File(context), Menus.Settings(context), Menus.Transport(context), Menus.View(context), Menus.Help(context)];

    [Fact]
    public void EveryBarPageBuildsAndHasSomethingOnIt()
    {
        foreach (var page in PagesWithoutADisc(Context()))
        {
            Assert.NotEmpty(page.Items);
            Assert.Contains(page.Items, i => i.Selectable);
        }
    }

    [Fact]
    public void SettingsIsReachedFromFileAsAnApplicationMenuWouldHaveIt()
    {
        var context = Context();
        var settings = Assert.IsType<MenuSubmenu>(Menus.File(context).Items.Single(i => i.Label == "Settings"));

        var labels = settings.Open().Items.Select(i => i.Label).ToArray();

        Assert.Contains("Picture", labels);
        Assert.Contains("Sound", labels);
        Assert.Contains("Playback", labels);
        Assert.Contains("Controls...", labels);
    }

    [Fact]
    public void TransportRowsCarryTheControlTheyAreBoundTo()
    {
        var context = Context();
        var play = Assert.IsType<MenuAction>(Menus.Transport(context).Items.First(i => i.Label == "Play / Pause"));

        Assert.Equal(context.Input.Describe(InputAction.TogglePause), play.Value);
    }

    [Fact]
    public void ATransportRowRunsThroughTheHostRatherThanTouchingThePlayer()
    {
        var performed = new List<InputAction>();

        var recording = new MenuContext
        {
            Settings = new VxpSettings(),
            Input = InputMap.CreateDefault(),
            Player = null,
            Disc = null,
            CloseMenu = () => { },
            Screenshot = () => { },
            ToggleFullscreen = () => { },
            Quit = () => { },
            Toast = _ => { },
            Perform = performed.Add,
            SelectTrack = _ => { },
            OpenPage = _ => { },
            ShowInfo = (_, _) => { },
            OpenScreenshots = () => { },
            OpenRecent = _ => { },
        };

        Menus.Transport(recording).Items.First(i => i.Label == "Stop").Activate();

        Assert.Equal([InputAction.Stop], performed);
    }

    [Fact]
    public void ASettingShownInTwoMenusIsTheSameSetting()
    {
        var settings = new VxpSettings();
        var context = Context(settings);

        // "Show performance" appears on both the View menu and the display settings page.
        var fromView = context.Settings.Interface.ShowPerformance;
        var view = (MenuToggle)Menus.View(context).Items.First(i => i.Label == "Show Performance");
        view.Activate();

        var display = (MenuToggle)Menus.Interface(context).Items.First(i => i.Label == "Show performance");

        Assert.NotEqual(fromView, settings.Interface.ShowPerformance);
        Assert.Equal(settings.Interface.ShowPerformance, display.Get());
    }
}
