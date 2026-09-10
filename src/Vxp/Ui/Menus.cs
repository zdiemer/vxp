using Vxp.Config;
using Vxp.Emulation;
using Vxp.Format;
using Vxp.Input;

namespace Vxp.Ui;

/// <summary>What the menus need from the host to do their work.</summary>
public sealed class MenuContext
{
    /// <summary>The settings being edited.</summary>
    public required VxpSettings Settings { get; init; }

    /// <summary>The control bindings being edited.</summary>
    public required InputMap Input { get; init; }

    /// <summary>The running player.</summary>
    public required VideoNowPlayer Player { get; init; }

    /// <summary>The disc survey, for the track browser and the info page.</summary>
    public required DiscMap Disc { get; init; }

    /// <summary>Closes the menu and returns to playback.</summary>
    public required Action CloseMenu { get; init; }

    /// <summary>Writes the current frame to a file.</summary>
    public required Action Screenshot { get; init; }

    /// <summary>Switches between windowed and full screen.</summary>
    public required Action ToggleFullscreen { get; init; }

    /// <summary>Quits the emulator.</summary>
    public required Action Quit { get; init; }

    /// <summary>Shows a short message.</summary>
    public required Action<string> Toast { get; init; }
}

/// <summary>Builds every menu page.</summary>
public static class Menus
{
    /// <summary>The page shown when the menu key is pressed.</summary>
    public static MenuPage Root(MenuContext context) => new()
    {
        Title = "vxp",
        Subtitle = context.Disc.Name,
        Items =
        [
            new MenuAction
            {
                Label = "Resume",
                Help = "Return to the disc.",
                OnActivate = context.CloseMenu,
            },
            new MenuSubmenu
            {
                Label = "Tracks",
                Help = "Browse the disc and jump to any segment.",
                Open = () => TrackBrowser(context),
            },
            new MenuSubmenu
            {
                Label = "Disc information",
                Help = "What this disc is and how it is put together.",
                Open = () => DiscInfo(context),
            },
            new MenuHeading { Label = "" },
            new MenuSubmenu
            {
                Label = "Picture",
                Help = "Scaling, colour and panel simulation.",
                Open = () => Video(context),
            },
            new MenuSubmenu
            {
                Label = "Sound",
                Help = "Volume and output buffering.",
                Open = () => Audio(context),
            },
            new MenuSubmenu
            {
                Label = "Playback",
                Help = "Speed, looping and how interactive choices behave.",
                Open = () => Playback(context),
            },
            new MenuSubmenu
            {
                Label = "On-screen display",
                Help = "The status overlay and menu appearance.",
                Open = () => Interface(context),
            },
            new MenuSubmenu
            {
                Label = "Controls",
                Help = "Rebind the keyboard and game controller.",
                Open = () => Controls(context),
            },
            new MenuHeading { Label = "" },
            new MenuAction
            {
                Label = "Take a screenshot",
                Help = "Write the frame on screen to a PNG file.",
                OnActivate = context.Screenshot,
            },
            new MenuAction
            {
                Label = "Quit",
                Help = "Close the emulator.",
                OnActivate = context.Quit,
            },
        ],
    };

    /// <summary>Picture settings.</summary>
    public static MenuPage Video(MenuContext context)
    {
        var video = context.Settings.Video;

        return new MenuPage
        {
            Title = "Picture",
            Items =
            [
                EnumChoice("Scaling", () => video.ScaleMode, v => video.ScaleMode = v,
                    "How the picture is fitted into the window."),
                EnumChoice("Filtering", () => video.Filter, v => video.Filter = v,
                    "Nearest keeps hard pixel edges; linear smooths them."),
                new MenuNumber
                {
                    Label = "Window scale",
                    Help = "Size of the window as a multiple of the 144x80 picture.",
                    Get = () => video.WindowScale,
                    Set = v => video.WindowScale = v,
                    Minimum = 1, Maximum = 16, Default = 5,
                    Format = v => $"{v}x  ({FrameLayout.Width * v}x{FrameLayout.Height * v})",
                },
                new MenuToggle
                {
                    Label = "Full screen",
                    Help = "Fill the display.",
                    Get = () => video.Fullscreen,
                    Set = _ => context.ToggleFullscreen(),
                },
                new MenuToggle
                {
                    Label = "Wait for vertical blank",
                    Help = "Smoother presentation at the cost of a little latency. Takes effect on restart.",
                    Get = () => video.VSync,
                    Set = v => video.VSync = v,
                    Default = true,
                },
                new MenuNumber
                {
                    Label = "Pixel aspect",
                    Help = "Width of a source pixel relative to its height, in hundredths.",
                    Get = () => (int)Math.Round(video.PixelAspect * 100),
                    Set = v => video.PixelAspect = v / 100.0,
                    Minimum = 50, Maximum = 200, Step = 5, Default = 100,
                    Format = v => (v / 100.0).ToString("0.00"),
                },

                new MenuHeading { Label = "Colour" },
                Slider("Brightness", () => video.Brightness, v => video.Brightness = v, -100, 100, 5, 0),
                Slider("Contrast", () => video.Contrast, v => video.Contrast = v, -100, 100, 5, 0),
                Slider("Saturation", () => video.Saturation, v => video.Saturation = v, -100, 100, 5, 0),
                new MenuNumber
                {
                    Label = "Gamma",
                    Help = "Below 100 lifts the shadows, above 100 deepens them.",
                    Get = () => video.Gamma,
                    Set = v => video.Gamma = v,
                    Minimum = 50, Maximum = 300, Step = 5, Default = 100,
                },
                new MenuChoice
                {
                    Label = "Channel order",
                    Help = "RGB is correct for retail discs. The rest are for studying the format.",
                    Options = ["RGB", "RBG", "GRB", "GBR", "BRG", "BGR"],
                    Get = () => Math.Max(0, Array.IndexOf(Swizzles, video.ChannelOrder)),
                    Set = i => video.ChannelOrder = Swizzles[i],
                    Default = 0,
                },

                new MenuHeading { Label = "Panel simulation" },
                Slider("LCD pixel grid", () => video.LcdGrid, v => video.LcdGrid = v, 0, 100, 5, 0),
                Slider("Scanlines", () => video.Scanlines, v => video.Scanlines = v, 0, 100, 5, 0),
            ],
        };
    }

    private static readonly string[] Swizzles = ["RGB", "RBG", "GRB", "GBR", "BRG", "BGR"];

    /// <summary>Sound settings.</summary>
    public static MenuPage Audio(MenuContext context)
    {
        var audio = context.Settings.Audio;

        return new MenuPage
        {
            Title = "Sound",
            Items =
            [
                new MenuNumber
                {
                    Label = "Volume",
                    Help = "Output level.",
                    Get = () => audio.Volume,
                    Set = v => audio.Volume = v,
                    Minimum = 0, Maximum = 100, Step = 5, Default = 80,
                    Format = v => $"{v}%",
                },
                new MenuToggle
                {
                    Label = "Mute",
                    Help = "Silence the output without losing the volume setting.",
                    Get = () => audio.Muted,
                    Set = v => audio.Muted = v,
                },
                new MenuNumber
                {
                    Label = "Buffer",
                    Help = "Lower is more responsive; too low and the sound breaks up.",
                    Get = () => audio.BufferMilliseconds,
                    Set = v => audio.BufferMilliseconds = v,
                    Minimum = 40, Maximum = 500, Step = 20, Default = 200,
                    Format = v => $"{v} ms",
                },
                new MenuToggle
                {
                    Label = "Play in background",
                    Help = "Keep playing when the window loses focus.",
                    Get = () => audio.PlayInBackground,
                    Set = v => audio.PlayInBackground = v,
                    Default = true,
                },
                new MenuHeading { Label = "" },
                new MenuHeading { Label = $"Disc audio: {FrameLayout.AudioSampleRate} Hz mono, 8-bit" },
            ],
        };
    }

    /// <summary>Playback and interactive behaviour.</summary>
    public static MenuPage Playback(MenuContext context)
    {
        var emulation = context.Settings.Emulation;

        return new MenuPage
        {
            Title = "Playback",
            Items =
            [
                new MenuNumber
                {
                    Label = "Speed",
                    Help = "Playback rate. Sound is resampled to match, so pitch follows.",
                    Get = () => emulation.SpeedPercent,
                    Set = v => emulation.SpeedPercent = v,
                    Minimum = 25, Maximum = 800, Step = 25, Default = 100,
                    Format = v => $"{v / 100.0:0.##}x",
                },
                new MenuNumber
                {
                    Label = "Fast forward speed",
                    Help = "Rate used while the fast-forward control is held.",
                    Get = () => emulation.FastForwardPercent,
                    Set = v => emulation.FastForwardPercent = v,
                    Minimum = 100, Maximum = 800, Step = 50, Default = 300,
                    Format = v => $"{v / 100.0:0.##}x",
                },
                new MenuNumber
                {
                    Label = "Seek step",
                    Help = "How far the seek controls jump.",
                    Get = () => emulation.SeekSeconds,
                    Set = v => emulation.SeekSeconds = v,
                    Minimum = 1, Maximum = 60, Default = 5,
                    Format = v => $"{v} s",
                },
                EnumChoice("Loop", () => emulation.Loop, v => emulation.Loop = v,
                    "What happens at the end of a track or of the disc."),
                new MenuToggle
                {
                    Label = "Play on load",
                    Help = "Start playing as soon as a disc is opened.",
                    Get = () => emulation.AutoPlay,
                    Set = v => emulation.AutoPlay = v,
                    Default = true,
                },
                new MenuToggle
                {
                    Label = "Skip empty tracks",
                    Help = "Pass over tracks with no video, such as end-of-disc padding.",
                    Get = () => emulation.SkipEmptyTracks,
                    Set = v => emulation.SkipEmptyTracks = v,
                    Default = true,
                },

                new MenuHeading { Label = "Interactive titles" },
                EnumChoice("At a choice point", () => emulation.ChoiceTimeout, v => emulation.ChoiceTimeout = v,
                    "What happens when a choice segment ends and nothing was pressed."),
                new MenuToggle
                {
                    Label = "Jump immediately",
                    Help = "Take a branch the moment it is chosen instead of at the end of the scene.",
                    Get = () => emulation.InstantChoices,
                    Set = v => emulation.InstantChoices = v,
                },
                EnumChoice("Segment order", () => emulation.Navigation, v => emulation.Navigation = v,
                    "followHeader obeys register 0x4F, whose meaning is not fully established."),
            ],
        };
    }

    /// <summary>On-screen display settings.</summary>
    public static MenuPage Interface(MenuContext context)
    {
        var ui = context.Settings.Interface;

        return new MenuPage
        {
            Title = "On-screen display",
            Items =
            [
                EnumChoice("Status overlay", () => ui.Overlay, v => ui.Overlay = v,
                    "Auto shows the overlay briefly whenever something changes."),
                EnumChoice("Overlay corner", () => ui.OverlayCorner, v => ui.OverlayCorner = v,
                    "Where the status overlay sits."),
                new MenuNumber
                {
                    Label = "Overlay timeout",
                    Help = "How long the overlay stays up in Auto mode.",
                    Get = () => ui.OverlaySeconds,
                    Set = v => ui.OverlaySeconds = v,
                    Minimum = 1, Maximum = 30, Default = 3,
                    Format = v => $"{v} s",
                },
                new MenuNumber
                {
                    Label = "Text size",
                    Help = "Zero picks a size from the window.",
                    Get = () => ui.FontScale,
                    Set = v => ui.FontScale = v,
                    Minimum = 0, Maximum = 6, Default = 0,
                    Format = v => v == 0 ? "Automatic" : $"{v}x",
                },
                new MenuToggle
                {
                    Label = "Show track and time",
                    Get = () => ui.ShowTrackInfo,
                    Set = v => ui.ShowTrackInfo = v,
                    Default = true,
                },
                new MenuToggle
                {
                    Label = "Show branch choices",
                    Help = "List the destinations a decision point offers.",
                    Get = () => ui.ShowChoices,
                    Set = v => ui.ShowChoices = v,
                    Default = true,
                },
                new MenuToggle
                {
                    Label = "Show performance",
                    Help = "Frame rate and audio buffer depth.",
                    Get = () => ui.ShowPerformance,
                    Set = v => ui.ShowPerformance = v,
                },
                new MenuToggle
                {
                    Label = "Dim behind menus",
                    Get = () => ui.DimBehindMenu,
                    Set = v => ui.DimBehindMenu = v,
                    Default = true,
                },
                new MenuToggle
                {
                    Label = "Confirm before quitting",
                    Get = () => ui.ConfirmQuit,
                    Set = v => ui.ConfirmQuit = v,
                },
            ],
        };
    }

    /// <summary>Control bindings, grouped by what they do.</summary>
    public static MenuPage Controls(MenuContext context)
    {
        var items = new List<MenuItem>();

        foreach (var category in Enum.GetValues<ActionCategory>())
        {
            var actions = InputActions.InCategory(category).ToArray();
            if (actions.Length == 0) continue;

            items.Add(new MenuHeading { Label = category.ToString() });

            foreach (var action in actions)
            {
                items.Add(new MenuBinding
                {
                    Label = InputActions.Label(action),
                    Help = "Enter rebinds, Left clears, Delete restores the default.",
                    Action = action,
                    Map = context.Input,
                });
            }
        }

        items.Add(new MenuHeading { Label = "" });
        items.Add(new MenuAction
        {
            Label = "Restore all defaults",
            Help = "Put every control back the way it shipped.",
            OnActivate = () =>
            {
                context.Input.ResetAll();
                context.Toast("All controls reset");
            },
        });

        return new MenuPage
        {
            Title = "Controls",
            Subtitle = "Keyboard and game controller",
            Items = items,
        };
    }

    /// <summary>Every track on the disc, with its timing and branch structure.</summary>
    public static MenuPage TrackBrowser(MenuContext context)
    {
        var items = new List<MenuItem>();

        foreach (var track in context.Disc.Tracks)
        {
            if (!track.HasVideo)
            {
                if (context.Settings.Emulation.SkipEmptyTracks) continue;

                items.Add(new MenuHeading { Label = $"{track.Number,3}  (no video)" });
                continue;
            }

            var number = track.Number;
            var detail = Describe(track);

            items.Add(new MenuAction
            {
                Label = $"{number,3}  {track.Duration:mm\\:ss}  {track.Title ?? ""}",
                Help = detail,
                Detail = () => context.Player.CurrentTrack == number ? "playing" : string.Empty,
                OnActivate = () =>
                {
                    context.Player.SelectTrack(number);
                    context.Player.Play();
                    context.CloseMenu();
                },
            });
        }

        var page = new MenuPage
        {
            Title = "Tracks",
            Subtitle = $"{context.Disc.PlayableTracks.Count()} segments, {context.Disc.TotalDuration:hh\\:mm\\:ss}",
            Items = items,
        };

        // Open the list on whatever is playing rather than at the top.
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].Label.TrimStart().StartsWith($"{context.Player.CurrentTrack} ")) continue;
            page.Selected = i;
            break;
        }

        return page;
    }

    private static string Describe(TrackInfo track)
    {
        if (track.Branches.Count == 0) return $"{track.Kind}, {track.FrameCount} frames";

        var destinations = string.Join(", ", track.Branches.Select(b => $"{b.Slot + 1}->{b.Track}"));
        return $"{track.Kind}: {destinations}";
    }

    /// <summary>Read-only facts about the disc.</summary>
    public static MenuPage DiscInfo(MenuContext context)
    {
        var disc = context.Disc;
        var layout = disc.Layout;

        var items = new List<MenuItem>
        {
            new MenuHeading { Label = disc.Name },
            new MenuHeading { Label = "" },
            new MenuHeading { Label = $"Format         VideoNow {layout?.Format.ToString() ?? "unknown"}" },
            new MenuHeading { Label = $"Picture        {FrameLayout.Width}x{FrameLayout.Height}, 4 bits per channel" },
            new MenuHeading { Label = $"Frame rate     {layout?.FrameRate ?? 0:0.####} fps" },
            new MenuHeading { Label = $"Sound          {FrameLayout.AudioSampleRate} Hz mono" },
            new MenuHeading { Label = $"Frame size     {layout?.StreamBytes ?? 0} bytes on disc" },
            new MenuHeading { Label = $"Tracks         {disc.Tracks.Count} ({disc.PlayableTracks.Count()} with video)" },
            new MenuHeading { Label = $"Running time   {disc.TotalDuration:hh\\:mm\\:ss}" },
            new MenuHeading { Label = $"Interactive    {(disc.IsInteractive ? "yes" : "no")}" },
        };

        if (disc.IsInteractive)
        {
            var choicePoints = disc.PlayableTracks.Count(t => t.OffersChoice);
            items.Add(new MenuHeading { Label = $"Choice points  {choicePoints}" });
        }

        return new MenuPage { Title = "Disc information", Items = items };
    }

    private static MenuItem EnumChoice<T>(string label, Func<T> get, Action<T> set, string? help = null)
        where T : struct, Enum
    {
        var values = Enum.GetValues<T>();

        return new MenuChoice
        {
            Label = label,
            Help = help,
            Options = values.Select(Humanise).ToArray(),
            Get = () => Math.Max(0, Array.IndexOf(values, get())),
            Set = i => set(values[i]),
        };
    }

    private static MenuItem Slider(string label, Func<int> get, Action<int> set, int min, int max, int step, int fallback)
        => new MenuNumber
        {
            Label = label,
            Get = get,
            Set = set,
            Minimum = min,
            Maximum = max,
            Step = step,
            Default = fallback,
        };

    /// <summary>Turns an enum name such as <c>FollowHeader</c> into <c>Follow header</c>.</summary>
    private static string Humanise<T>(T value) where T : struct, Enum
    {
        var name = value.ToString()!;
        var text = new System.Text.StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ');
                text.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                text.Append(name[i]);
            }
        }

        return text.ToString();
    }
}
