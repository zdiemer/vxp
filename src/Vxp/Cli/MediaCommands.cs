using Vxp.Discs;
using Vxp.Format;
using Vxp.Video;

namespace Vxp.Cli;

/// <summary>Commands that turn disc content into ordinary image and audio files.</summary>
public static class MediaCommands
{
    /// <summary>Exports frames as PNG files and the soundtrack as a WAV.</summary>
    public static int Export(CommandLine args)
    {
        var cue = args.RequireCue();
        var outDir = args.Require("out");
        var trackNumber = args.Int("track");
        var maxFrames = args.Int("frames", int.MaxValue);
        var startFrame = args.Int("start", 0);
        var scale = args.Int("scale", 1);
        var everyNth = Math.Max(1, args.Int("every", 1));
        var noAudio = args.Has("no-audio");
        var noVideo = args.Has("no-video");
        var adjust = PictureAdjustment.FromArgs(args);

        Directory.CreateDirectory(outDir);

        using var disc = DiscImage.Open(cue);
        var tracks = trackNumber is null
            ? disc.Tracks
            : [disc.FindTrack(trackNumber.Value) ?? throw new ArgumentException($"No track {trackNumber}.")];

        using var wav = noAudio ? null : new WavWriter(Path.Combine(outDir, "audio.wav"), PlaybackSampleRate(disc));
        var pcm = new short[65536];
        byte[] rgba = [];
        var exported = 0;

        foreach (var track in tracks)
        {
            var layout = FormatDetector.Detect(track);
            if (layout is null) continue;

            var reader = new TrackReader(track, layout);
            for (var i = startFrame; i < reader.FrameCount && exported < maxFrames; i++)
            {
                var frame = reader.ReadFrame(i);
                if (frame is null) break;

                if (!noVideo && (i - startFrame) % everyNth == 0)
                {
                    if (rgba.Length != layout.RgbaBytes) rgba = new byte[layout.RgbaBytes];
                    VideoDecoder.Decode(frame, rgba, adjust.Order);
                    adjust.Apply(rgba);
                    PngWriter.Write(
                        Path.Combine(outDir, $"t{track.Number:D2}_f{i:D5}.png"),
                        rgba, layout.PictureWidth, layout.PictureHeight, scale);
                }

                if (wav is not null)
                {
                    if (pcm.Length < frame.Audio.Length) pcm = new short[frame.Audio.Length];
                    AudioDecoder.DecodePcm16(frame.Audio, pcm);
                    wav.Write(pcm.AsSpan(0, frame.Audio.Length));
                }

                exported++;
                if (exported % 100 == 0) Console.Write($"\rExported {exported} frames...");
            }

            if (exported >= maxFrames) break;
        }

        Console.WriteLine($"\rExported {exported} frames{(wav is null ? "" : " and audio.wav")} to {outDir}");
        return 0;
    }

    /// <summary>Writes a single frame to a PNG file.</summary>
    public static int Frame(CommandLine args)
    {
        var cue = args.RequireCue();
        var output = args.Require("out");
        var trackNumber = args.Int("track") ?? throw new ArgumentException("--track <n> is required.");
        var frameIndex = args.Int("frame", 0);
        var scale = args.Int("scale", 1);
        var adjust = PictureAdjustment.FromArgs(args);

        using var disc = DiscImage.Open(cue);
        var track = disc.FindTrack(trackNumber) ?? throw new ArgumentException($"No track {trackNumber}.");
        var layout = FormatDetector.Detect(track) ?? throw new InvalidDataException($"Track {trackNumber} has no video.");

        var reader = new TrackReader(track, layout);
        var frame = reader.ReadFrame(frameIndex)
                    ?? throw new ArgumentException($"Track {trackNumber} has no frame {frameIndex}.");

        var rgba = new byte[layout.RgbaBytes];
        VideoDecoder.Decode(frame, rgba, adjust.Order);
        adjust.Apply(rgba);

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        PngWriter.Write(output, rgba, layout.PictureWidth, layout.PictureHeight, scale);
        Console.WriteLine($"Wrote {output} (track {trackNumber}, frame {frameIndex}).");
        return 0;
    }

    /// <summary>Writes a track's soundtrack, or the whole disc's, to a WAV file.</summary>
    public static int Audio(CommandLine args)
    {
        var cue = args.RequireCue();
        var output = args.Require("out");
        var trackNumber = args.Int("track");

        using var disc = DiscImage.Open(cue);
        var tracks = trackNumber is null
            ? disc.Tracks
            : [disc.FindTrack(trackNumber.Value) ?? throw new ArgumentException($"No track {trackNumber}.")];

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var sampleRate = PlaybackSampleRate(disc);
        using var wav = new WavWriter(output, sampleRate);
        var pcm = new short[65536];
        var samples = 0L;

        foreach (var track in tracks)
        {
            var layout = FormatDetector.Detect(track);
            if (layout is null) continue;

            var reader = new TrackReader(track, layout);
            for (var i = 0; i < reader.FrameCount; i++)
            {
                var frame = reader.ReadFrame(i);
                if (frame is null) break;

                if (pcm.Length < frame.Audio.Length) pcm = new short[frame.Audio.Length];
                AudioDecoder.DecodePcm16(frame.Audio, pcm);
                wav.Write(pcm.AsSpan(0, frame.Audio.Length));
                samples += frame.Audio.Length;
            }
        }

        var duration = TimeSpan.FromSeconds(samples / (double)sampleRate);
        Console.WriteLine($"Wrote {output} ({duration:hh\\:mm\\:ss}).");
        return 0;
    }

    /// <summary>
    /// The rate a disc's soundtrack is written at: the rate it plays at, so the file runs
    /// at the right speed, rather than the stream's one-times byte rate.
    /// </summary>
    /// <remarks>
    /// One rate serves a disc that mixes Color and XP tracks, since both play at 35 280 Hz.
    /// </remarks>
    internal static int PlaybackSampleRate(DiscImage disc)
        => (FormatDetector.Detect(disc) ?? FrameLayout.Xp).PlaybackSampleRate;
}
