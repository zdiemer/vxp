using System.Text.Json.Serialization;
using Vxp.Emulation;
using Vxp.Input;

namespace Vxp.Config;

/// <summary>How the picture is fitted into the window.</summary>
public enum ScaleMode
{
    /// <summary>Largest whole-number multiple that fits. No uneven pixels, some letterboxing.</summary>
    IntegerScale,

    /// <summary>Fill the window as far as the aspect ratio allows.</summary>
    FitWindow,

    /// <summary>Fill the window completely, distorting the picture if need be.</summary>
    Stretch,

    /// <summary>Draw at one screen pixel per source pixel.</summary>
    OneToOne,
}

/// <summary>Texture filtering used when the picture is scaled up.</summary>
public enum ScaleFilter
{
    /// <summary>Hard pixel edges, as the panel itself looks.</summary>
    Nearest,

    /// <summary>Smoothed, closer to how a small LCD reads at arm's length.</summary>
    Linear,
}

/// <summary>Where the status overlay sits.</summary>
public enum OverlayCorner
{
    /// <summary>Top left.</summary>
    TopLeft,

    /// <summary>Top right.</summary>
    TopRight,

    /// <summary>Bottom left.</summary>
    BottomLeft,

    /// <summary>Bottom right.</summary>
    BottomRight,
}

/// <summary>How long the status overlay stays up.</summary>
public enum OverlayMode
{
    /// <summary>Never shown.</summary>
    Hidden,

    /// <summary>Appears briefly when something changes, then fades out.</summary>
    Auto,

    /// <summary>Always on screen.</summary>
    Always,
}

/// <summary>Picture settings.</summary>
public sealed class VideoSettings
{
    /// <summary>How the picture is fitted into the window.</summary>
    public ScaleMode ScaleMode { get; set; } = ScaleMode.IntegerScale;

    /// <summary>Filtering used when scaling up.</summary>
    public ScaleFilter Filter { get; set; } = ScaleFilter.Nearest;

    /// <summary>Window scale factor applied at startup.</summary>
    public int WindowScale { get; set; } = 5;

    /// <summary>Start the window full screen.</summary>
    public bool Fullscreen { get; set; }

    /// <summary>Wait for vertical blank before presenting.</summary>
    public bool VSync { get; set; } = true;

    /// <summary>
    /// Width of a source pixel relative to its height.
    /// </summary>
    /// <remarks>
    /// The stored 144 x 80 grid is not the shape of the picture: the panel's pixels are
    /// appreciably taller than they are wide, and the titles are 4:3 broadcast animation.
    /// The default puts a 144 x 80 frame back at 4:3, which is also within a few percent
    /// of the geometry of the real panel. Set it to 1.0 to see the stored grid instead,
    /// which is useful for studying the format and wrong for watching anything.
    /// </remarks>
    public double PixelAspect { get; set; } = 0.74;

    /// <summary>Brightness adjustment, -100 to 100.</summary>
    public int Brightness { get; set; }

    /// <summary>Contrast adjustment, -100 to 100.</summary>
    public int Contrast { get; set; }

    /// <summary>Colour saturation, -100 (greyscale) to 100.</summary>
    public int Saturation { get; set; }

    /// <summary>Gamma, 50 to 300, where 100 leaves the picture untouched.</summary>
    public int Gamma { get; set; } = 100;

    /// <summary>Strength of the simulated LCD pixel grid, 0 to 100.</summary>
    public int LcdGrid { get; set; }

    /// <summary>Strength of simulated scanlines, 0 to 100.</summary>
    public int Scanlines { get; set; }

    /// <summary>
    /// Order the three stored channel samples are mapped to red, green and blue. RGB is
    /// correct for retail discs; the rest are for investigating the format.
    /// </summary>
    public string ChannelOrder { get; set; } = "RGB";

    /// <summary>Colour of the letterbox area around the picture, as #RRGGBB.</summary>
    public string BackgroundColor { get; set; } = "#000000";
}

/// <summary>Sound settings.</summary>
public sealed class AudioSettings
{
    /// <summary>Output volume, 0 to 100.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Silence the output without disturbing the volume setting.</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// How much audio to keep queued, in milliseconds. Lower is more responsive; too low
    /// and the sound breaks up.
    /// </summary>
    public int BufferMilliseconds { get; set; } = 200;

    /// <summary>Name of the output device, or empty for the system default.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Keep playing when the window loses focus.</summary>
    public bool PlayInBackground { get; set; } = true;
}

/// <summary>Playback and interactive behaviour.</summary>
public sealed class EmulationSettings
{
    /// <summary>Start playing as soon as a disc is loaded.</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>How the player chooses the track that follows a segment.</summary>
    public NavigationPolicy Navigation { get; set; } = NavigationPolicy.FollowHeader;

    /// <summary>What happens at a choice point when the viewer does nothing.</summary>
    public ChoiceTimeout ChoiceTimeout { get; set; } = ChoiceTimeout.FirstBranch;

    /// <summary>What happens at the end of a track or of the disc.</summary>
    public LoopMode Loop { get; set; } = LoopMode.None;

    /// <summary>Take a branch the moment it is chosen instead of at the end of the segment.</summary>
    public bool InstantChoices { get; set; }

    /// <summary>Playback rate as a percentage of the disc's own rate.</summary>
    public int SpeedPercent { get; set; } = 100;

    /// <summary>
    /// The rate the disc's sound is played at, in samples per second; the picture follows it.
    /// </summary>
    /// <remarks>
    /// One-times CD speed would give 17 640 Hz, but the retail XP discs only run to the
    /// length of their episodes at twice that, 35 280 Hz. It stays adjustable until
    /// hardware pins it down.
    /// </remarks>
    public int DiscSampleRate { get; set; } = 35280;

    /// <summary>Rate used while the fast-forward control is held, as a percentage.</summary>
    public int FastForwardPercent { get; set; } = 300;

    /// <summary>How far the seek controls jump, in seconds.</summary>
    public int SeekSeconds { get; set; } = 5;

    /// <summary>Skip tracks that carry no video, such as end-of-disc padding.</summary>
    public bool SkipEmptyTracks { get; set; } = true;
}

/// <summary>On-screen display settings.</summary>
public sealed class InterfaceSettings
{
    /// <summary>How long the status overlay stays up.</summary>
    public OverlayMode Overlay { get; set; } = OverlayMode.Auto;

    /// <summary>Which corner the status overlay sits in.</summary>
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopLeft;

    /// <summary>Seconds the overlay stays visible in <see cref="OverlayMode.Auto"/>.</summary>
    public int OverlaySeconds { get; set; } = 3;

    /// <summary>Text size for the overlay and menus, 1 to 6. Zero picks a size from the window.</summary>
    public int FontScale { get; set; }

    /// <summary>Show the track number and running time in the overlay.</summary>
    public bool ShowTrackInfo { get; set; } = true;

    /// <summary>Show the branch choices offered by the current segment.</summary>
    public bool ShowChoices { get; set; } = true;

    /// <summary>Show frame rate and decode timing.</summary>
    public bool ShowPerformance { get; set; }

    /// <summary>Dim the picture behind an open menu.</summary>
    public bool DimBehindMenu { get; set; } = true;

    /// <summary>
    /// Put a native menu bar on the window. Windows only, and ignored elsewhere, where
    /// the in-window menu is the only one. Full screen hides the bar either way.
    /// </summary>
    public bool NativeMenuBar { get; set; } = true;

    /// <summary>Ask before quitting.</summary>
    public bool ConfirmQuit { get; set; }

    /// <summary>Where screenshots are written. Empty means alongside the settings file.</summary>
    public string ScreenshotDirectory { get; set; } = string.Empty;
}

/// <summary>The complete emulator configuration, as stored on disk.</summary>
public sealed class VxpSettings
{
    /// <summary>Schema version, so older files can be migrated.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Schema version this build writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Picture settings.</summary>
    public VideoSettings Video { get; set; } = new();

    /// <summary>Sound settings.</summary>
    public AudioSettings Audio { get; set; } = new();

    /// <summary>Playback and interactive behaviour.</summary>
    public EmulationSettings Emulation { get; set; } = new();

    /// <summary>On-screen display settings.</summary>
    public InterfaceSettings Interface { get; set; } = new();

    /// <summary>Control bindings, keyed by action name.</summary>
    public Dictionary<string, List<string>> Bindings { get; set; } = new();

    /// <summary>Discs opened recently, most recent first.</summary>
    public List<string> RecentDiscs { get; set; } = new();

    /// <summary>Largest number of entries kept in <see cref="RecentDiscs"/>.</summary>
    [JsonIgnore]
    public const int MaxRecentDiscs = 12;

    /// <summary>Records that a disc was opened, moving it to the head of the recent list.</summary>
    public void RecordRecentDisc(string path)
    {
        var full = Path.GetFullPath(path);
        RecentDiscs.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentDiscs.Insert(0, full);
        if (RecentDiscs.Count > MaxRecentDiscs) RecentDiscs.RemoveRange(MaxRecentDiscs, RecentDiscs.Count - MaxRecentDiscs);
    }

    /// <summary>Reads the bindings into a usable input map, filling any gaps with defaults.</summary>
    public InputMap BuildInputMap() => InputMap.FromSettings(Bindings);

    /// <summary>Writes an input map back into <see cref="Bindings"/> ready to save.</summary>
    public void StoreInputMap(InputMap map) => Bindings = map.ToSettings();
}
