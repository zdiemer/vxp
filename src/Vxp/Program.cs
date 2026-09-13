using Vxp.Cli;

if (args.Length == 0)
{
    Usage.Print();
    return 1;
}

var command = args[0].ToLowerInvariant();
var rest = CommandLine.Parse(args[1..]);

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
        _ => PlayCommand.Run(CommandLine.Parse(args)),
    };

    foreach (var unknown in rest.Unrecognised())
        Console.Error.WriteLine($"vxp: warning: ignored unknown option --{unknown}");

    return exitCode;
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or IOException)
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
              vxp <disc.cue> [options]         Play a disc.
              vxp play <disc.cue> [options]    The same, spelled out.

                --track N          Start on this track.
                --frame N          Start at this frame of that track.
                --scale N          Window scale factor. Default comes from settings.
                --fullscreen       Start full screen.
                --speed N          Playback rate as a percentage, 25 to 800.
                --loop MODE        none, track or disc.
                --navigation MODE  discOrder or followHeader.
                --choice-timeout M firstBranch, discOrder or wait.
                --mute             Start silent.
                --volume N         Volume, 0 to 100.
                --no-config        Ignore the settings file and use defaults.

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
