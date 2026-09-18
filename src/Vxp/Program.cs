using Vxp;
using Vxp.Cli;

// Double-clicked, or started with nothing to say: open the player with no disc in it.
// "vxp --help" is where the usage lives.
if (args.Length == 0) args = ["play"];

var command = args[0].ToLowerInvariant();

// "vxp disc.cue --fullscreen" names no command, so the whole line is the play command's.
// Parsing it once, here, means the unknown-option warning below reads the same parse the
// command did rather than a second one nothing looked at.
string[] commands =
[
    "play", "info", "tracks", "map", "graph", "headers", "verify", "export", "frame", "audio",
    "run", "config", "bind", "help", "-h", "--help", "version", "--version",
];

var named = commands.Contains(command);
var rest = CommandLine.Parse(named ? args[1..] : args);

try
{
    var exitCode = command switch
    {
        "play" => PlayCommand.Run(rest),
        "info" => DiscCommands.Info(rest),
        "tracks" => DiscCommands.Tracks(rest),
        "map" => DiscCommands.Map(rest),
        "graph" => DiscCommands.Graph(rest),
        "headers" => DiscCommands.Headers(rest),
        "verify" => DiscCommands.Verify(rest),
        "export" => MediaCommands.Export(rest),
        "frame" => MediaCommands.Frame(rest),
        "audio" => MediaCommands.Audio(rest),
        "run" => SessionCommand.Run(rest),
        "config" => ConfigCommands.Config(rest),
        "bind" => ConfigCommands.Bind(rest),
        "help" or "-h" or "--help" => Usage.Print(),
        "version" or "--version" => Usage.Version(),

        // Anything else is taken as a disc to play, so "vxp disc.cue" just works.
        _ => PlayCommand.Run(rest),
    };

    foreach (var unknown in rest.Unrecognised())
        Console.Error.WriteLine($"vxp: warning: ignored unknown option --{unknown}");

    return exitCode;
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or IOException or SdlException)
{
    Console.Error.WriteLine($"vxp: {ex.Message}");
    return 1;
}

/// <summary>The help text.</summary>
internal static class Usage
{
    public static int Version()
    {
        var version = typeof(Usage).Assembly.GetName().Version;
        Console.WriteLine($"vxp {version?.ToString(3) ?? "unknown"}");
        return 0;
    }

    public static int Print()
    {
        Console.WriteLine("""
            vxp - a VideoNow XP emulator

            PLAYING
              vxp                              Open the player with no disc in it.
              vxp <disc.cue> [options]         Play a disc.
              vxp play <disc.cue> [options]    The same, spelled out.

              Another disc can be opened at any time with Ctrl+O, File > Open Disc,
              the menu's Recent discs, or by dropping a file on the window.

              Anywhere a <disc.cue> is taken, a .zip holding the cue sheet and its
              tracks works too. Options for this run are not saved.

                --track N          Start on this track.
                --frame N          Start at this frame of that track.
                --scale N          Window scale factor. Default comes from settings.
                --fullscreen       Start full screen. --windowed does the opposite.
                --speed N          Playback rate as a percentage, 25 to 800.
                --rate HZ          Disc sample rate, 16000 to 19000. Default 17784.
                --loop MODE        none, track or disc.
                --navigation MODE  discOrder or followHeader.
                --choice-timeout M firstBranch, discOrder or wait.
                --mute             Start silent.
                --volume N         Volume, 0 to 100.
                --no-config        Use defaults; neither read nor write the settings file.

            INSPECTING
              vxp info <disc.cue> [--json]     Format, timing and the track list.
              vxp tracks <disc.cue> [--playable] [--json]
              vxp map <disc.cue> [--json]      Segment kinds and the branch table.
              vxp graph <disc.cue> [--format dot|mermaid|json] [--out FILE]
              vxp headers <disc.cue> [--track N] [--frames N] [--all]
              vxp verify <disc.cue> [--deep] [--json]

            EXPORTING
              vxp export <disc.cue> --out DIR [--track N] [--start N] [--frames N]
                                              [--every N] [--scale N] [--no-audio] [--no-video]
              vxp frame <disc.cue> --track N [--frame N] --out FILE.png [--scale N]
              vxp audio <disc.cue> --out FILE.wav [--track N]

              Picture options accepted by export and frame:
                --brightness N  --contrast N  --gamma N  --saturation N  --swizzle RGB

            SCRIPTING
              vxp run <disc.cue> [--script FILE | --commands "a; b; c"] [options]

                Runs the emulator with no window, as fast as it decodes.
                --script -         Read the script from standard input.
                --wav FILE         Record the session soundtrack.
                --frames-out DIR   Write every decoded frame as a PNG.
                --max-frames N     Stop writing frames after N of them.
                --max-seconds N    Ceiling on "play all" and "play track". Default 3600.
                --json             Machine-readable status output.

                Script commands, one per line or separated by semicolons:
                  track N            Jump to a track          play [5s|90f|track|all]
                  pause / resume     Transport                stop
                  next / prev        Track skip               back
                  seek 5s / seek -2s Move within a track      frame N
                  step [N]           Step frames and pause    speed 2.0
                  choice N           Queue a branch           choose N (take it now)
                  screenshot FILE    Write a PNG              status
                  expect FIELD VALUE Check track, frame, state, kind or choices
                  echo TEXT          Print a line             # comment

            SETTINGS
              vxp config list [filter] [--json]
              vxp config get <setting> [--json]
              vxp config set <setting> <value>
              vxp config reset [<setting>|all]
              vxp config recent [clear]
              vxp config path

              vxp bind list [--json]
              vxp bind set <action> <control>      e.g. vxp bind set TogglePause Space
              vxp bind add <action> <control>      e.g. vxp bind add TogglePause Pad:A
              vxp bind clear <action>
              vxp bind reset [<action>|all]
              vxp bind keys                        List every bindable key name.

            Press the menu key while playing (Escape by default) for the settings,
            track browser and controls pages.
            """);

        return 0;
    }
}
