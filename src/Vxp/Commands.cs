using Vxp.Discs;
using Vxp.Format;

namespace Vxp;

/// <summary>Command-line argument helpers shared by the subcommands.</summary>
internal static class Options
{
    /// <summary>Returns the value following <paramref name="name"/>, or null.</summary>
    public static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>True if <paramref name="name"/> is present.</summary>
    public static bool Flag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the first non-option argument, which is always the cue sheet path.</summary>
    public static string RequireCue(string[] args)
    {
        var cue = args.FirstOrDefault(a => !a.StartsWith('-'))
                  ?? throw new ArgumentException("A .cue file path is required.");

        if (!File.Exists(cue)) throw new FileNotFoundException($"Cue sheet not found: {cue}");
        return cue;
    }
}

/// <summary>The headless inspection and export subcommands.</summary>
internal static class Commands
{
    /// <summary>Prints disc format, timing and the track list.</summary>
    public static int Info(string[] args)
    {
        using var disc = DiscImage.Open(Options.RequireCue(args));
        var layout = FormatDetector.Detect(disc);

        Console.WriteLine($"Disc:   {disc.Name}");
        Console.WriteLine($"Tracks: {disc.Tracks.Count}");

        if (layout is null)
        {
            Console.WriteLine("Format: not a recognised VideoNow disc");
            return 1;
        }

        Console.WriteLine($"Format: VideoNow {layout.Format}");
        Console.WriteLine($"Video:  {FrameLayout.Width}x{FrameLayout.Height}, 4 bits per channel, {layout.FrameRate:0.####} fps");
        Console.WriteLine($"Audio:  {FrameLayout.AudioSampleRate} Hz mono, 8-bit unsigned on disc");
        Console.WriteLine($"Frame:  {layout.StreamBytes} bytes on disc = {layout.VideoBytes} video + {layout.AudioBytes} audio");
        Console.WriteLine();
        Console.WriteLine($"{"Trk",3}  {"Frames",7}  {"Duration",9}  {"Fmt",5}  Title");

        var total = TimeSpan.Zero;
        foreach (var track in disc.Tracks)
        {
            var trackLayout = FormatDetector.Detect(track);
            if (trackLayout is null)
            {
                Console.WriteLine($"{track.Number,3}  {"-",7}  {"-",9}  {"-",5}  {track.Title ?? "(no video stream)"}");
                continue;
            }

            var reader = new TrackReader(track, trackLayout);
            total += reader.Duration;
            Console.WriteLine($"{track.Number,3}  {reader.FrameCount,7}  {reader.Duration:mm\\:ss\\.ff}  {trackLayout.Format,5}  {track.Title}");
        }

        Console.WriteLine();
        Console.WriteLine($"Total video runtime: {total:hh\\:mm\\:ss}");
        return 0;
    }

    /// <summary>Prints each segment's kind and branch table, which is the shape of the story.</summary>
    public static int Map(string[] args)
    {
        using var disc = DiscImage.Open(Options.RequireCue(args));

        Console.WriteLine($"{"Trk",3}  {"Frames",6}  {"Kind",13}  {"0x4F",4}  Branches (key:track)");
        Console.WriteLine(new string('-', 78));

        foreach (var track in disc.Tracks)
        {
            var layout = FormatDetector.Detect(track);
            if (layout is null) continue;

            var reader = new TrackReader(track, layout);
            var frame = reader.ReadFrame(0);
            if (frame is null) continue;

            var header = frame.ReadHeader();
            var branches = header.Branches.Count == 0
                ? "-"
                : string.Join("  ", header.Branches.Select(b =>
                    b.Tag == 0 ? $"{b.Slot + 1}:{b.Track}" : $"{b.Slot + 1}:{b.Track}(tag {b.Tag:X2})"));

            var continueTrack = header.ContinueTrack == 0 ? "-" : header.ContinueTrack.ToString();
            Console.WriteLine($"{track.Number,3}  {reader.FrameCount,6}  {header.Kind,13}  {continueTrack,4}  {branches}");
        }

        return 0;
    }

    /// <summary>Dumps the display-controller register file frame by frame.</summary>
    public static int Headers(string[] args)
    {
        using var disc = DiscImage.Open(Options.RequireCue(args));
        var showAll = Options.Flag(args, "--all");
        var trackFilter = Options.Value(args, "--track") is { } t ? int.Parse(t) : (int?)null;

        foreach (var track in disc.Tracks)
        {
            if (trackFilter is not null && track.Number != trackFilter) continue;

            var layout = FormatDetector.Detect(track);
            if (layout is null) continue;

            var reader = new TrackReader(track, layout);
            Console.WriteLine($"=== Track {track.Number}  ({reader.FrameCount} frames)  {track.Title}");

            var headers = new List<FrameHeader>();
            for (var i = 0; i < Math.Min(reader.FrameCount, 16); i++)
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

    /// <summary>Exports frames as PNG files and the soundtrack as a WAV.</summary>
    public static int Export(string[] args)
    {
        var cue = Options.RequireCue(args);
        var outDir = Options.Value(args, "--out") ?? throw new ArgumentException("--out <dir> is required.");
        var trackNumber = Options.Value(args, "--track") is { } t ? int.Parse(t) : (int?)null;
        var maxFrames = Options.Value(args, "--frames") is { } f ? int.Parse(f) : int.MaxValue;
        var scale = Options.Value(args, "--scale") is { } s ? int.Parse(s) : 1;
        var order = Options.Value(args, "--swizzle") is { } o ? ChannelOrder.Parse(o) : ChannelOrder.Default;

        Directory.CreateDirectory(outDir);

        using var disc = DiscImage.Open(cue);
        var tracks = trackNumber is null
            ? disc.Tracks
            : [disc.FindTrack(trackNumber.Value) ?? throw new ArgumentException($"No track {trackNumber}.")];

        using var wav = new WavWriter(Path.Combine(outDir, "audio.wav"), FrameLayout.AudioSampleRate);
        var pcm = new short[65536];
        var rgba = new byte[VideoDecoder.RgbaFrameBytes];
        var exported = 0;

        foreach (var track in tracks)
        {
            var layout = FormatDetector.Detect(track);
            if (layout is null) continue;

            var reader = new TrackReader(track, layout);
            for (var i = 0; i < reader.FrameCount && exported < maxFrames; i++)
            {
                var frame = reader.ReadFrame(i);
                if (frame is null) break;

                VideoDecoder.DecodeRgba(frame.PixelData, rgba, order);
                PngWriter.Write(
                    Path.Combine(outDir, $"t{track.Number:D2}_f{i:D5}.png"),
                    rgba, FrameLayout.Width, FrameLayout.Height, scale);

                AudioDecoder.DecodePcm16(frame.Audio, pcm);
                wav.Write(pcm.AsSpan(0, frame.Audio.Length));

                exported++;
                if (exported % 50 == 0) Console.Write($"\rExported {exported} frames...");
            }

            if (exported >= maxFrames) break;
        }

        Console.WriteLine($"\rExported {exported} frames and audio.wav to {outDir}");
        return 0;
    }
}
