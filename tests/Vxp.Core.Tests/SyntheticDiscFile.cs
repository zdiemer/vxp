using System.Text;
using Vxp.Format;

namespace Vxp.Tests;

/// <summary>How one track of a synthetic disc should be built.</summary>
/// <param name="Number">Track number.</param>
/// <param name="Frames">How many frames the track holds.</param>
/// <param name="Kind">Segment kind written to register 0x4B.</param>
/// <param name="ContinueTrack">Value written to register 0x4F.</param>
/// <param name="Branches">Destination tracks for the branch table, in slot order.</param>
/// <param name="Title">Cue sheet title.</param>
/// <param name="Empty">When true the track holds no VideoNow stream, like end-of-disc padding.</param>
public sealed record TrackSpec(
    int Number,
    int Frames,
    SegmentKind Kind = SegmentKind.Linear,
    int ContinueTrack = 0,
    int[]? Branches = null,
    string? Title = null,
    bool Empty = false);

/// <summary>
/// Writes a playable VideoNow XP disc image to a temporary directory, so the player can
/// be driven end to end without a real disc.
/// </summary>
public sealed class SyntheticDiscFile : IDisposable
{
    private SyntheticDiscFile(string directory, string cuePath)
    {
        Directory = directory;
        CuePath = cuePath;
    }

    /// <summary>Directory holding the cue sheet and its track files.</summary>
    public string Directory { get; }

    /// <summary>Path of the cue sheet.</summary>
    public string CuePath { get; }

    /// <summary>Builds a disc from the given track specifications.</summary>
    public static SyntheticDiscFile Create(params TrackSpec[] tracks)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vxp-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var cue = new StringBuilder();
        var layout = FrameLayout.Xp;

        foreach (var spec in tracks)
        {
            var fileName = $"disc (Track {spec.Number:D2}).bin";
            var path = Path.Combine(directory, fileName);

            if (spec.Empty)
            {
                // Padding: sector-aligned, but carrying no sync word.
                File.WriteAllBytes(path, new byte[layout.StreamBytes]);
            }
            else
            {
                using var file = File.Create(path);
                for (var frame = 0; frame < spec.Frames; frame++)
                    file.Write(BuildFrame(spec, frame, layout));
            }

            cue.AppendLine($"FILE \"{fileName}\" BINARY");
            cue.AppendLine($"  TRACK {spec.Number:D2} AUDIO");
            if (spec.Title is not null) cue.AppendLine($"    TITLE \"{spec.Title}\"");
            cue.AppendLine("    INDEX 01 00:00:00");
        }

        var cuePath = Path.Combine(directory, "disc.cue");
        File.WriteAllText(cuePath, cue.ToString());
        return new SyntheticDiscFile(directory, cuePath);
    }

    /// <summary>
    /// Packs the cue sheet and track files into a zip beside them, laid out flat the way
    /// Redump sets ship, and returns its path.
    /// </summary>
    public string Zip(string name = "disc.zip")
    {
        var zipPath = Path.Combine(Directory, name);

        using var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory).Where(f => f != zipPath).Order())
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, file, Path.GetFileName(file));

        return zipPath;
    }

    private static byte[] BuildFrame(TrackSpec spec, int frameIndex, FrameLayout layout)
    {
        var registers = new Dictionary<int, byte>
        {
            [FrameHeader.RegSegmentKind] = (byte)spec.Kind,
            [FrameHeader.RegFrameCountLow] = (byte)(spec.Frames & 0xFF),
            [FrameHeader.RegFrameCountHigh] = (byte)(spec.Frames >> 8),
            [FrameHeader.RegContinueTrack] = (byte)spec.ContinueTrack,
            [FrameHeader.RegFrameIndexLow] = (byte)(frameIndex & 0xFF),
            [FrameHeader.RegFrameIndexHigh] = (byte)(frameIndex >> 8),
            [FrameHeader.RegTrackNumber] = (byte)spec.Number,
        };

        var branches = spec.Branches ?? [];
        for (var slot = 0; slot < branches.Length && slot < FrameHeader.BranchEntryCount; slot++)
        {
            var register = FrameHeader.RegBranchTableBase + slot * FrameHeader.BranchEntryStride;

            if (spec.Kind == SegmentKind.TaggedChoice)
            {
                registers[register] = 0x6D;                    // selector tag
                registers[register + 1] = (byte)branches[slot]; // destination
            }
            else
            {
                registers[register] = (byte)branches[slot];
            }
        }

        // Stamp the track and frame into the picture so tests can tell frames apart.
        var pixels = new byte[FrameLayout.PixelBytes];
        pixels[0] = (byte)(spec.Number & 0x0F);
        pixels[FrameLayout.PixelRowStride] = (byte)(frameIndex & 0x0F);

        // A ramp in the audio makes it obvious which frame a sample came from.
        var audio = new byte[layout.AudioBytes];
        Array.Fill(audio, (byte)(0x80 + (frameIndex & 0x0F)));

        return SyntheticDisc.BuildFrame(layout, registers, pixels, audio);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
