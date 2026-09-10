using Vxp.Config;
using Vxp.Input;
using Vxp.Ui;
using Xunit;

namespace Vxp.Tests;

public class MenuPageTests
{
    private static MenuPage Page(params MenuItem[] items)
        => new() { Title = "Test", Items = items };

    private static MenuAction Action(string label, Action? onActivate = null)
        => new() { Label = label, OnActivate = onActivate ?? (() => { }) };

    [Fact]
    public void SelectionSkipsHeadings()
    {
        var page = Page(
            new MenuHeading { Label = "Group" },
            Action("First"),
            Action("Second"));

        page.SelectFirst();
        Assert.Equal(1, page.Selected);

        page.Move(1);
        Assert.Equal(2, page.Selected);
    }

    [Fact]
    public void SelectionWrapsAtBothEnds()
    {
        var page = Page(Action("a"), Action("b"));
        page.SelectFirst();

        page.Move(-1);
        Assert.Equal(1, page.Selected);

        page.Move(1);
        Assert.Equal(0, page.Selected);
    }

    [Fact]
    public void APageOfOnlyHeadingsDoesNotSpin()
    {
        var page = Page(new MenuHeading { Label = "a" }, new MenuHeading { Label = "b" });

        page.SelectFirst();
        page.Move(1);

        Assert.InRange(page.Selected, 0, 1);
    }
}

public class MenuItemTests
{
    [Fact]
    public void ToggleFlipsAndReportsItsState()
    {
        var value = false;
        var item = new MenuToggle
        {
            Label = "Mute",
            Get = () => value,
            Set = v => value = v,
            Default = true,
        };

        Assert.Equal("Off", item.Value);

        Assert.True(item.Activate());
        Assert.True(value);
        Assert.Equal("On", item.Value);

        item.Reset();
        Assert.True(value);
    }

    [Fact]
    public void NumberClampsToItsRange()
    {
        var value = 50;
        var item = new MenuNumber
        {
            Label = "Volume",
            Get = () => value,
            Set = v => value = v,
            Minimum = 0,
            Maximum = 100,
            Step = 25,
            Default = 80,
        };

        item.Adjust(1);
        Assert.Equal(75, value);

        item.Adjust(1);
        Assert.Equal(100, value);

        // Already at the top, so nothing changes and the item says so.
        Assert.False(item.Adjust(1));

        item.Reset();
        Assert.Equal(80, value);
    }

    [Fact]
    public void NumberUsesItsFormatterForDisplay()
    {
        var item = new MenuNumber
        {
            Label = "Speed",
            Get = () => 300,
            Set = _ => { },
            Minimum = 25,
            Maximum = 800,
            Format = v => $"{v / 100.0:0.##}x",
        };

        Assert.Equal("3x", item.Value);
    }

    [Fact]
    public void ChoiceCyclesInBothDirections()
    {
        var index = 0;
        var item = new MenuChoice
        {
            Label = "Scaling",
            Options = ["One", "Two", "Three"],
            Get = () => index,
            Set = v => index = v,
        };

        Assert.Equal("One", item.Value);

        item.Adjust(1);
        Assert.Equal("Two", item.Value);

        item.Adjust(-1);
        item.Adjust(-1);
        Assert.Equal("Three", item.Value);
    }

    [Fact]
    public void BindingShowsAndClearsItsControls()
    {
        var map = InputMap.CreateDefault();
        var item = new MenuBinding
        {
            Label = "Play / pause",
            Action = InputAction.TogglePause,
            Map = map,
        };

        Assert.Contains("Space", item.Value);

        item.Adjust(-1);
        Assert.Equal("unbound", item.Value);

        item.Reset();
        Assert.Contains("Space", item.Value);
    }
}

public class MenuControllerTests
{
    private static MenuPage Page(params MenuItem[] items)
        => new() { Title = "Test", Items = items };

    [Fact]
    public void StartsClosed()
        => Assert.False(new MenuController().IsOpen);

    [Fact]
    public void PushingAndPoppingTracksOpenState()
    {
        var menu = new MenuController();

        menu.Push(Page(new MenuAction { Label = "a", OnActivate = () => { } }));
        Assert.True(menu.IsOpen);

        menu.Handle(InputAction.MenuBack);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void SubmenusStackAndUnwindOneAtATime()
    {
        var menu = new MenuController();
        var inner = Page(new MenuAction { Label = "inner", OnActivate = () => { } });

        menu.Push(Page(new MenuSubmenu { Label = "open", Open = () => inner }));
        menu.Handle(InputAction.MenuSelect);
        Assert.True(menu.IsOpen);

        menu.Handle(InputAction.MenuBack);
        Assert.True(menu.IsOpen); // back on the outer page

        menu.Handle(InputAction.MenuBack);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void ChangingASettingRaisesChanged()
    {
        var changed = 0;
        var value = false;

        var menu = new MenuController();
        menu.Changed += () => changed++;
        menu.Push(Page(new MenuToggle { Label = "flag", Get = () => value, Set = v => value = v }));

        menu.Handle(InputAction.MenuRight);

        Assert.True(value);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void NavigationDoesNotCountAsAChange()
    {
        var changed = 0;
        var menu = new MenuController();
        menu.Changed += () => changed++;
        menu.Push(Page(
            new MenuAction { Label = "a", OnActivate = () => { } },
            new MenuAction { Label = "b", OnActivate = () => { } }));

        menu.Handle(InputAction.MenuDown);
        menu.Handle(InputAction.MenuUp);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void UnrelatedActionsAreSwallowedWhileOpen()
    {
        var menu = new MenuController();
        menu.Push(Page(new MenuAction { Label = "a", OnActivate = () => { } }));

        Assert.True(menu.Handle(InputAction.NextTrack));
    }

    [Fact]
    public void NothingIsHandledWhileClosed()
        => Assert.False(new MenuController().Handle(InputAction.MenuDown));

    [Fact]
    public void ActivatingABindingStartsCapture()
    {
        var map = InputMap.CreateDefault();
        var menu = new MenuController();

        menu.Push(Page(new MenuBinding { Label = "Play", Action = InputAction.TogglePause, Map = map }));
        menu.Handle(InputAction.MenuSelect);

        Assert.True(menu.IsCapturing);
    }

    [Fact]
    public void CaptureAssignsTheNextControlPressed()
    {
        var map = InputMap.CreateDefault();
        var menu = new MenuController();

        menu.Push(Page(new MenuBinding { Label = "Play", Action = InputAction.TogglePause, Map = map }));
        menu.Handle(InputAction.MenuSelect);

        KeyNames.TryParse("P", out var p);
        Assert.True(menu.Capture(Binding.Key(p), map));

        Assert.False(menu.IsCapturing);
        Assert.Equal([Binding.Key(p)], map.BindingsFor(InputAction.TogglePause));
    }

    [Fact]
    public void EscapeLeavesABindingAlone()
    {
        var map = InputMap.CreateDefault();
        var original = map.BindingsFor(InputAction.TogglePause).ToArray();
        var menu = new MenuController();

        menu.Push(Page(new MenuBinding { Label = "Play", Action = InputAction.TogglePause, Map = map }));
        menu.Handle(InputAction.MenuSelect);

        KeyNames.TryParse("Escape", out var escape);
        Assert.True(menu.Capture(Binding.Key(escape), map));

        Assert.False(menu.IsCapturing);
        Assert.Equal(original, map.BindingsFor(InputAction.TogglePause));
    }

    [Fact]
    public void CaptureIgnoresControlsWhenNotCapturing()
    {
        var map = InputMap.CreateDefault();
        Assert.False(new MenuController().Capture(Binding.Key(65), map));
    }

    [Fact]
    public void ResettingAnItemRaisesChanged()
    {
        var value = 10;
        var changed = 0;

        var menu = new MenuController();
        menu.Changed += () => changed++;
        menu.Push(Page(new MenuNumber
        {
            Label = "n",
            Get = () => value,
            Set = v => value = v,
            Minimum = 0,
            Maximum = 100,
            Default = 42,
        }));

        menu.Handle(InputAction.MenuResetItem);

        Assert.Equal(42, value);
        Assert.Equal(1, changed);
    }
}

public class MenuBuildTests
{
    private static MenuContext Context(VxpSettings? settings = null)
    {
        settings ??= new VxpSettings();

        return new MenuContext
        {
            Settings = settings,
            Input = InputMap.CreateDefault(),
            Player = null!,
            Disc = null!,
            CloseMenu = () => { },
            Screenshot = () => { },
            ToggleFullscreen = () => { },
            Quit = () => { },
            Toast = _ => { },
        };
    }

    [Fact]
    public void EveryPictureSettingIsReachable()
    {
        var page = Menus.Video(Context());
        var labels = page.Items.Select(i => i.Label).ToArray();

        Assert.Contains("Scaling", labels);
        Assert.Contains("Brightness", labels);
        Assert.Contains("Contrast", labels);
        Assert.Contains("Saturation", labels);
        Assert.Contains("Gamma", labels);
        Assert.Contains("Channel order", labels);
        Assert.Contains("LCD pixel grid", labels);
        Assert.Contains("Scanlines", labels);
    }

    [Fact]
    public void AdjustingAMenuItemWritesThroughToSettings()
    {
        var settings = new VxpSettings();
        var page = Menus.Video(Context(settings));

        var brightness = page.Items.First(i => i.Label == "Brightness");
        brightness.Adjust(1);

        Assert.Equal(5, settings.Video.Brightness);
    }

    [Fact]
    public void ChannelOrderMenuMatchesTheStoredSetting()
    {
        var settings = new VxpSettings();
        var page = Menus.Video(Context(settings));
        var item = page.Items.First(i => i.Label == "Channel order");

        Assert.Equal("RGB", item.Value);

        item.Adjust(1);
        Assert.Equal(settings.Video.ChannelOrder, item.Value);
        Assert.NotEqual("RGB", settings.Video.ChannelOrder);
    }

    [Fact]
    public void ControlsPageListsEveryAction()
    {
        var page = Menus.Controls(Context());
        var bound = page.Items.OfType<MenuBinding>().Select(b => b.Action).ToHashSet();

        foreach (var action in InputActions.All) Assert.Contains(action, bound);
    }

    [Fact]
    public void PlaybackPageCoversTheInteractiveSettings()
    {
        var page = Menus.Playback(Context());
        var labels = page.Items.Select(i => i.Label).ToArray();

        Assert.Contains("Speed", labels);
        Assert.Contains("At a choice point", labels);
        Assert.Contains("Jump immediately", labels);
        Assert.Contains("Segment order", labels);
        Assert.Contains("Loop", labels);
    }

    [Fact]
    public void EnumMenuItemsOfferEveryValue()
    {
        var page = Menus.Playback(Context());
        var loop = (MenuChoice)page.Items.First(i => i.Label == "Loop");

        Assert.Equal(Enum.GetValues<Emulation.LoopMode>().Length, loop.Options.Count);
    }
}
