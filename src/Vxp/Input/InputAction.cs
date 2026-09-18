namespace Vxp.Input;

/// <summary>Everything the emulator can be told to do. Each is separately bindable.</summary>
public enum InputAction
{
    /// <summary>Start playing, or pause if already playing.</summary>
    TogglePause,

    /// <summary>Stop and rewind to the start of the disc.</summary>
    Stop,

    /// <summary>Skip to the next track.</summary>
    NextTrack,

    /// <summary>Skip back a track, or restart the current one.</summary>
    PreviousTrack,

    /// <summary>Return to the segment played before this one.</summary>
    GoBack,

    /// <summary>Jump forward within the current track.</summary>
    SeekForward,

    /// <summary>Jump backward within the current track.</summary>
    SeekBackward,

    /// <summary>Play faster while held.</summary>
    FastForward,

    /// <summary>Advance one frame and pause.</summary>
    FrameForward,

    /// <summary>Step back one frame and pause.</summary>
    FrameBackward,

    /// <summary>Raise the playback rate a notch.</summary>
    SpeedUp,

    /// <summary>Lower the playback rate a notch.</summary>
    SpeedDown,

    /// <summary>Return the playback rate to normal.</summary>
    SpeedReset,

    /// <summary>Raise the volume.</summary>
    VolumeUp,

    /// <summary>Lower the volume.</summary>
    VolumeDown,

    /// <summary>Silence or restore the sound.</summary>
    ToggleMute,

    /// <summary>Cycle through the loop modes.</summary>
    ToggleLoop,

    /// <summary>Take the first branch offered at a decision point.</summary>
    Choice1,

    /// <summary>Take the second branch offered at a decision point.</summary>
    Choice2,

    /// <summary>Take the third branch offered at a decision point.</summary>
    Choice3,

    /// <summary>Take the fourth branch offered at a decision point.</summary>
    Choice4,

    /// <summary>Take the fifth branch offered at a decision point.</summary>
    Choice5,

    /// <summary>Take the sixth branch offered at a decision point.</summary>
    Choice6,

    /// <summary>Open or close the menu.</summary>
    ToggleMenu,

    /// <summary>Open the track browser directly.</summary>
    TrackBrowser,

    /// <summary>Show or hide the status overlay.</summary>
    ToggleOverlay,

    /// <summary>Switch between windowed and full screen.</summary>
    ToggleFullscreen,

    /// <summary>Write the current frame to a PNG file.</summary>
    Screenshot,

    /// <summary>Choose a disc to open in place of the one playing.</summary>
    OpenDisc,

    /// <summary>Eject the disc and return to the empty player.</summary>
    CloseDisc,

    /// <summary>Quit the emulator.</summary>
    Quit,

    /// <summary>Move up in a menu.</summary>
    MenuUp,

    /// <summary>Move down in a menu.</summary>
    MenuDown,

    /// <summary>Decrease a value, or move left in a menu.</summary>
    MenuLeft,

    /// <summary>Increase a value, or move right in a menu.</summary>
    MenuRight,

    /// <summary>Activate the highlighted menu item.</summary>
    MenuSelect,

    /// <summary>Leave the current menu.</summary>
    MenuBack,

    /// <summary>Jump a page up in a long list.</summary>
    MenuPageUp,

    /// <summary>Jump a page down in a long list.</summary>
    MenuPageDown,

    /// <summary>Restore the highlighted setting to its default.</summary>
    MenuResetItem,
}

/// <summary>Grouping used to lay out the controls page.</summary>
public enum ActionCategory
{
    /// <summary>Play, pause, skip and seek.</summary>
    Transport,

    /// <summary>Interactive branch selection.</summary>
    Choices,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Windowing, overlay and screenshots.</summary>
    Display,

    /// <summary>Moving around the menus.</summary>
    Menu,

    /// <summary>Everything else.</summary>
    General,
}

/// <summary>Descriptions and grouping for <see cref="InputAction"/>.</summary>
public static class InputActions
{
    /// <summary>Every action, in the order the controls page shows them.</summary>
    public static readonly IReadOnlyList<InputAction> All = Enum.GetValues<InputAction>();

    private static readonly Dictionary<InputAction, (string Label, ActionCategory Category)> Info = new()
    {
        [InputAction.TogglePause] = ("Play / pause", ActionCategory.Transport),
        [InputAction.Stop] = ("Stop", ActionCategory.Transport),
        [InputAction.NextTrack] = ("Next track", ActionCategory.Transport),
        [InputAction.PreviousTrack] = ("Previous track", ActionCategory.Transport),
        [InputAction.GoBack] = ("Back a segment", ActionCategory.Transport),
        [InputAction.SeekForward] = ("Seek forward", ActionCategory.Transport),
        [InputAction.SeekBackward] = ("Seek backward", ActionCategory.Transport),
        [InputAction.FastForward] = ("Fast forward (hold)", ActionCategory.Transport),
        [InputAction.FrameForward] = ("Frame forward", ActionCategory.Transport),
        [InputAction.FrameBackward] = ("Frame back", ActionCategory.Transport),
        [InputAction.SpeedUp] = ("Speed up", ActionCategory.Transport),
        [InputAction.SpeedDown] = ("Slow down", ActionCategory.Transport),
        [InputAction.SpeedReset] = ("Normal speed", ActionCategory.Transport),
        [InputAction.ToggleLoop] = ("Cycle loop mode", ActionCategory.Transport),

        [InputAction.Choice1] = ("Choice 1", ActionCategory.Choices),
        [InputAction.Choice2] = ("Choice 2", ActionCategory.Choices),
        [InputAction.Choice3] = ("Choice 3", ActionCategory.Choices),
        [InputAction.Choice4] = ("Choice 4", ActionCategory.Choices),
        [InputAction.Choice5] = ("Choice 5", ActionCategory.Choices),
        [InputAction.Choice6] = ("Choice 6", ActionCategory.Choices),

        [InputAction.VolumeUp] = ("Volume up", ActionCategory.Audio),
        [InputAction.VolumeDown] = ("Volume down", ActionCategory.Audio),
        [InputAction.ToggleMute] = ("Mute", ActionCategory.Audio),

        [InputAction.ToggleFullscreen] = ("Full screen", ActionCategory.Display),
        [InputAction.ToggleOverlay] = ("Status overlay", ActionCategory.Display),
        [InputAction.Screenshot] = ("Screenshot", ActionCategory.Display),

        [InputAction.ToggleMenu] = ("Open menu", ActionCategory.Menu),
        [InputAction.TrackBrowser] = ("Track browser", ActionCategory.Menu),
        [InputAction.MenuUp] = ("Menu up", ActionCategory.Menu),
        [InputAction.MenuDown] = ("Menu down", ActionCategory.Menu),
        [InputAction.MenuLeft] = ("Menu left", ActionCategory.Menu),
        [InputAction.MenuRight] = ("Menu right", ActionCategory.Menu),
        [InputAction.MenuSelect] = ("Menu select", ActionCategory.Menu),
        [InputAction.MenuBack] = ("Menu back", ActionCategory.Menu),
        [InputAction.MenuPageUp] = ("Menu page up", ActionCategory.Menu),
        [InputAction.MenuPageDown] = ("Menu page down", ActionCategory.Menu),
        [InputAction.MenuResetItem] = ("Reset setting", ActionCategory.Menu),

        [InputAction.OpenDisc] = ("Open disc", ActionCategory.General),
        [InputAction.CloseDisc] = ("Close disc", ActionCategory.General),
        [InputAction.Quit] = ("Quit", ActionCategory.General),
    };

    /// <summary>
    /// Whether the action only means anything with a disc in the player. With none loaded
    /// these are turned away; settings, the menu and opening a disc still work.
    /// </summary>
    public static bool NeedsDisc(InputAction action) => action
        is InputAction.TogglePause or InputAction.Stop
        or InputAction.NextTrack or InputAction.PreviousTrack or InputAction.GoBack
        or InputAction.SeekForward or InputAction.SeekBackward or InputAction.FastForward
        or InputAction.FrameForward or InputAction.FrameBackward
        or (>= InputAction.Choice1 and <= InputAction.Choice6)
        or InputAction.TrackBrowser or InputAction.Screenshot or InputAction.CloseDisc;

    /// <summary>A human-readable name for the action.</summary>
    public static string Label(InputAction action)
        => Info.TryGetValue(action, out var info) ? info.Label : action.ToString();

    /// <summary>Which group the action belongs to.</summary>
    public static ActionCategory Category(InputAction action)
        => Info.TryGetValue(action, out var info) ? info.Category : ActionCategory.General;

    /// <summary>Actions in a category, in declaration order.</summary>
    public static IEnumerable<InputAction> InCategory(ActionCategory category)
        => All.Where(a => Category(a) == category);

    /// <summary>Parses an action name, accepting any capitalisation.</summary>
    public static bool TryParse(string name, out InputAction action)
        => Enum.TryParse(name, ignoreCase: true, out action);
}
