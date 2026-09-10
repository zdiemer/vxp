using Vxp;
using Vxp.Discs;
using Vxp.Emulation;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

try
{
    // Inspection subcommands run headless; anything else is a disc to play.
    return args[0].ToLowerInvariant() switch
    {
        "info" => Commands.Info(args[1..]),
        "map" => Commands.Map(args[1..]),
        "headers" => Commands.Headers(args[1..]),
        "export" => Commands.Export(args[1..]),
        "play" => Play(args[1..]),
        _ => Play(args),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"vxp: {ex.Message}");
    return 1;
}

static int Play(string[] args)
{
    var cuePath = args.FirstOrDefault(a => !a.StartsWith('-'))
                  ?? throw new ArgumentException("A .cue file path is required.");

    if (!File.Exists(cuePath))
        throw new FileNotFoundException($"Cue sheet not found: {cuePath}");

    using var disc = DiscImage.Open(cuePath);
    using var player = new VideoNowPlayer(disc);

    if (Options.Flag(args, "--follow-header")) player.Navigation = NavigationPolicy.FollowHeader;
    if (Options.Value(args, "--track") is { } track && int.TryParse(track, out var trackNumber))
        player.SelectTrack(trackNumber);

    var scale = Options.Value(args, "--scale") is { } s && int.TryParse(s, out var parsed) ? parsed : 5;

    using var window = new PlayerWindow(player, scale, Options.Flag(args, "--fullscreen"));
    window.Run();
    return 0;
}

static void PrintUsage() => Console.WriteLine("""
    vxp - a VideoNow XP emulator

    Usage:
      vxp <disc.cue> [options]            Play a disc.
      vxp info    <disc.cue>              Format, frame rate, track list and durations.
      vxp map     <disc.cue>              Segment kinds and the interactive branch table.
      vxp headers <disc.cue> [--track N] [--all]
                                          Dump the per-frame controller register file.
      vxp export  <disc.cue> --out <dir> [--track N] [--frames N] [--scale N] [--swizzle RGB]
                                          Write PNG frames and a WAV soundtrack.

    Play options:
      --track N         Start on this track instead of the first.
      --scale N         Window scale factor. Default 5, giving 720x400.
      --fullscreen      Start full screen.
      --follow-header   Follow register 0x4F as a next-track pointer. Experimental;
                        see docs/format.md.

    Controls:
      Space             Play / pause
      Left / Right      Previous / next track
      1 - 6             Take a branch at an interactive decision point
      Up / Down         Volume
      Backspace         Stop and rewind to the start of the disc
      F                 Toggle full screen
      Tab               Toggle the status overlay
      Esc               Quit
    """);
