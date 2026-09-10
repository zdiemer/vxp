using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vxp.Discs;
using Vxp.Emulation;
using Vxp.Format;

namespace Vxp.Cli;

/// <summary>Commands that report on a disc without playing it.</summary>
public static class DiscCommands
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Prints disc format, timing and the track list.</summary>
    public static int Info(CommandLine args)
    {
        using var disc = DiscImage.Open(args.RequireCue());
        var map = DiscMap.Build(disc);

        if (args.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                disc = map.Name,
                format = map.Layout?.Format.ToString() ?? "unknown",
                interactive = map.IsInteractive,
                width = FrameLayout.Width,
                height = FrameLayout.Height,
                frameRate = map.Layout?.FrameRate,
                audioSampleRate = FrameLayout.AudioSampleRate,
                frameBytes = map.Layout?.StreamBytes,
                trackCount = map.Tracks.Count,
                playableTrackCount = map.PlayableTracks.Count(),
                totalDuration = map.TotalDuration.ToString(),
                tracks = map.Tracks,
            }, Json));

            return 0;
        }

        if (map.Layout is null)
        {
            Console.WriteLine($"Disc:   {map.Name}");
            Console.WriteLine("Format: not a recognised VideoNow disc");
            return 1;
        }

        Console.WriteLine($"Disc:   {map.Name}");
        Console.WriteLine($"Format: VideoNow {map.Layout.Format}{(map.IsInteractive ? " (interactive)" : "")}");
        Console.WriteLine($"Video:  {FrameLayout.Width}x{FrameLayout.Height}, 4 bits per channel, {map.Layout.FrameRate:0.####} fps");
        Console.WriteLine($"Audio:  {FrameLayout.AudioSampleRate} Hz mono, 8-bit unsigned on disc");
        Console.WriteLine($"Frame:  {map.Layout.StreamBytes} bytes on disc = {map.Layout.VideoBytes} video + {map.Layout.AudioBytes} audio");
        Console.WriteLine($"Tracks: {map.Tracks.Count} ({map.PlayableTracks.Count()} with video)");
        Console.WriteLine();

        PrintTrackTable(map);
        Console.WriteLine();
        Console.WriteLine($"Total video runtime: {map.TotalDuration:hh\\:mm\\:ss}");
        return 0;
    }

    /// <summary>Prints just the track list.</summary>
    public static int Tracks(CommandLine args)
    {
        using var disc = DiscImage.Open(args.RequireCue());
        var map = DiscMap.Build(disc);
        var playableOnly = args.Has("playable");

        var tracks = playableOnly ? map.PlayableTracks.ToArray() : map.Tracks.ToArray();

        if (args.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(tracks, Json));
            return 0;
        }

        PrintTrackTable(map, playableOnly);
        return 0;
    }

    private static void PrintTrackTable(DiscMap map, bool playableOnly = false)
    {
        Console.WriteLine($"{"Trk",3}  {"Frames",7}  {"Duration",9}  {"Fmt",7}  Title");

        foreach (var track in map.Tracks)
        {
            if (!track.HasVideo)
            {
                if (playableOnly) continue;
                Console.WriteLine($"{track.Number,3}  {"-",7}  {"-",9}  {"-",7}  {track.Title ?? "(no video stream)"}");
                continue;
            }

            var warning = track.FrameCountMismatch ? "  [declared " + track.DeclaredFrameCount + "]" : "";
            Console.WriteLine(
                $"{track.Number,3}  {track.FrameCount,7}  {track.Duration:mm\\:ss\\.ff}  " +
                $"{track.Format,7}  {track.Title}{warning}");
        }
    }

    /// <summary>Prints each segment's kind and branch table.</summary>
    public static int Map(CommandLine args)
    {
        using var disc = DiscImage.Open(args.RequireCue());
        var map = DiscMap.Build(disc);

        if (args.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                disc = map.Name,
                interactive = map.IsInteractive,
                unreferenced = map.Unreferenced(),
                parallelGroups = map.ParallelGroups(),
                tracks = map.PlayableTracks.Select(t => new
                {
                    t.Number,
                    t.Title,
                    t.FrameCount,
                    kind = t.Kind,
                    t.ContinueTrack,
                    t.Branches,
                    successors = map.Successors(t.Number),
                }),
            }, Json));

            return 0;
        }

        Console.WriteLine($"{"Trk",3}  {"Frames",6}  {"Kind",13}  {"0x4F",4}  Branches (key:track)");
        Console.WriteLine(new string('-', 78));

        foreach (var track in map.PlayableTracks)
        {
            var branches = track.Branches.Count == 0
                ? "-"
                : string.Join("  ", track.Branches.Select(b =>
                    b.Tag == 0 ? $"{b.Slot + 1}:{b.Track}" : $"{b.Slot + 1}:{b.Track}(tag {b.Tag:X2})"));

            var continueTrack = track.ContinueTrack == 0 ? "-" : track.ContinueTrack.ToString();
            Console.WriteLine($"{track.Number,3}  {track.FrameCount,6}  {track.Kind,13}  {continueTrack,4}  {branches}");
        }

        var groups = map.ParallelGroups();
        if (groups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Runs of equal-length tracks, which are usually alternative takes of one scene:");
            foreach (var group in groups)
                Console.WriteLine($"  {string.Join(", ", group)}");
        }

        return 0;
    }

    /// <summary>Writes the branch graph in a form other tools can read.</summary>
    public static int Graph(CommandLine args)
    {
        using var disc = DiscImage.Open(args.RequireCue());
        var map = DiscMap.Build(disc);
        var format = args.Value("format", "dot").ToLowerInvariant();

        var text = format switch
        {
            "dot" => GraphAsDot(map),
            "mermaid" => GraphAsMermaid(map),
            "json" => JsonSerializer.Serialize(
                map.PlayableTracks.ToDictionary(t => t.Number.ToString(), t => map.Successors(t.Number)), Json),
            _ => throw new ArgumentException($"Unknown graph format '{format}'. Use dot, mermaid or json."),
        };

        var output = args.Value("out");
        if (output is null) Console.WriteLine(text);
        else File.WriteAllText(output, text);

        return 0;
    }

    private static string GraphAsDot(DiscMap map)
    {
        var text = new StringBuilder();
        text.AppendLine("digraph disc {");
        text.AppendLine("  rankdir=LR;");
        text.AppendLine("  node [shape=box, fontname=\"sans-serif\"];");

        foreach (var track in map.PlayableTracks)
        {
            var shape = track.OffersChoice ? "diamond" : track.Kind == SegmentKind.Terminal ? "doublecircle" : "box";
            text.AppendLine($"  t{track.Number} [label=\"{track.Number}\\n{track.Duration:mm\\:ss}\", shape={shape}];");
        }

        foreach (var track in map.PlayableTracks)
        {
            foreach (var branch in track.Branches)
            {
                if (map.Find(branch.Track)?.HasVideo != true) continue;
                text.AppendLine($"  t{track.Number} -> t{branch.Track} [label=\"{branch.Slot + 1}\"];");
            }

            if (!track.OffersChoice)
            {
                foreach (var next in map.Successors(track.Number))
                {
                    if (track.Branches.Any(b => b.Track == next)) continue;
                    text.AppendLine($"  t{track.Number} -> t{next} [style=dashed];");
                }
            }
        }

        text.AppendLine("}");
        return text.ToString();
    }

    private static string GraphAsMermaid(DiscMap map)
    {
        var text = new StringBuilder();
        text.AppendLine("flowchart LR");

        foreach (var track in map.PlayableTracks)
        {
            foreach (var next in map.Successors(track.Number))
            {
                var slot = track.Branches.FirstOrDefault(b => b.Track == next);
                var label = slot.Track == next ? $"|{slot.Slot + 1}|" : string.Empty;
                text.AppendLine($"  T{track.Number} -->{label} T{next}");
            }
        }

        return text.ToString();
    }

    /// <summary>Dumps the display-controller register file frame by frame.</summary>
    public static int Headers(CommandLine args)
    {
        using var disc = DiscImage.Open(args.RequireCue());
        var showAll = args.Has("all");
        var trackFilter = args.Int("track");
        var sampleCount = args.Int("frames", 16);

        foreach (var track in disc.Tracks)
        {
            if (trackFilter is not null && track.Number != trackFilter) continue;

            var layout = FormatDetector.Detect(track);
            if (layout is null) continue;

            var reader = new TrackReader(track, layout);
            Console.WriteLine($"=== Track {track.Number}  ({reader.FrameCount} frames)  {track.Title}");

            var headers = new List<FrameHeader>();
            for (var i = 0; i < Math.Min(reader.FrameCount, sampleCount); i++)
            {
                var frame = reader.ReadFrame(i);
                if (frame is not null) headers.Add(frame.ReadHeader());
            }

            if (headers.Count == 0) continue;

            for (var register = 0; register < 256; register++)
            {
                var values = headers.Select(h => h[register]).ToArray();
                var varies = values.Distinct().Count() > 1;

                if (!showAll && register < FrameHeader.FirstStateRegister) continue;
                if (!showAll && !varies && values[0] == 0) continue;

                var cells = string.Join(" ", values.Select(v => v.ToString("X2")));
                Console.WriteLine($"  reg {register:X2}: {cells}{(varies ? "   <- varies" : "")}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Checks that every track decodes, that frame counts agree with what the disc
    /// declares, and that no branch points at a track that is not there.
    /// </summary>
    public static int Verify(CommandLine args)
    {
        using var disc = DiscImage.Open(args.RequireCue());
        var map = DiscMap.Build(disc);
        var problems = new List<string>();

        if (map.Layout is null) problems.Add("No VideoNow stream was found anywhere on this disc.");

        foreach (var track in map.Tracks)
        {
            if (!track.HasVideo)
            {
                if (track.ByteLength > 0 && track.Number < map.Tracks.Count)
                    problems.Add($"Track {track.Number} carries no VideoNow stream.");

                continue;
            }

            if (track.FrameCountMismatch)
            {
                problems.Add(
                    $"Track {track.Number} holds {track.FrameCount} frames but declares {track.DeclaredFrameCount}.");
            }

            foreach (var branch in track.Branches)
            {
                var target = map.Find(branch.Track);
                if (target is null) problems.Add($"Track {track.Number} branches to track {branch.Track}, which is not on the disc.");
                else if (!target.HasVideo) problems.Add($"Track {track.Number} branches to track {branch.Track}, which has no video.");
            }
        }

        // Decode every frame of every track to prove the whole disc reads back cleanly.
        if (args.Has("deep"))
        {
            var rgba = new byte[VideoDecoder.RgbaFrameBytes];
            foreach (var info in map.PlayableTracks)
            {
                var track = disc.FindTrack(info.Number)!;
                var layout = FormatDetector.Detect(track)!;
                var reader = new TrackReader(track, layout);

                for (var i = 0; i < reader.FrameCount; i++)
                {
                    var frame = reader.ReadFrame(i);
                    if (frame is null)
                    {
                        problems.Add($"Track {info.Number} frame {i} could not be read.");
                        break;
                    }

                    VideoDecoder.DecodeRgba(frame.PixelData, rgba);
                }

                Console.Write($"\rChecked track {info.Number}...");
            }

            Console.Write("\r                          \r");
        }

        if (args.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = problems.Count == 0, problems }, Json));
            return problems.Count == 0 ? 0 : 1;
        }

        if (problems.Count == 0)
        {
            Console.WriteLine($"{map.Name}: OK ({map.PlayableTracks.Count()} tracks, {map.TotalDuration:hh\\:mm\\:ss}).");
            return 0;
        }

        Console.WriteLine($"{map.Name}: {problems.Count} problem(s).");
        foreach (var problem in problems) Console.WriteLine($"  {problem}");
        return 1;
    }
}
