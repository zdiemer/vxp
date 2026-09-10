using System.Globalization;

namespace Vxp.Discs;

/// <summary>A single <c>TRACK</c> entry parsed out of a cue sheet.</summary>
public sealed class CueTrack
{
    /// <summary>1-based track number as written in the cue sheet.</summary>
    public required int Number { get; init; }

    /// <summary>Track datatype, e.g. <c>AUDIO</c>. VideoNow discs are always AUDIO.</summary>
    public required string DataType { get; init; }

    /// <summary>Path of the backing binary file, relative to the cue sheet.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Optional <c>TITLE</c>. Mastering tools for VideoNow discs stored the source
    /// clip name here (for example <c>12-Something.vn5</c>), which is useful metadata
    /// but carries no playback semantics.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>Sector offset of INDEX 01 within <see cref="FileName"/>.</summary>
    public required int IndexOneSector { get; init; }

    /// <summary>Sector offset of INDEX 00 (pregap) within the file, if present.</summary>
    public int? IndexZeroSector { get; init; }
}

/// <summary>
/// Minimal cue sheet reader covering the subset emitted by CD preservation tools:
/// <c>FILE</c>, <c>TRACK</c>, <c>TITLE</c>, <c>FLAGS</c>, <c>PREGAP</c> and <c>INDEX</c>.
/// Both single-file and one-file-per-track layouts are supported.
/// </summary>
public static class CueSheet
{
    /// <summary>Bytes in a raw (Mode 0 / CD-DA) CD sector.</summary>
    public const int SectorSize = 2352;

    /// <summary>Parses the cue sheet at <paramref name="path"/>.</summary>
    public static IReadOnlyList<CueTrack> Load(string path)
        => Parse(File.ReadAllLines(path));

    /// <summary>Parses cue sheet <paramref name="lines"/> that have already been read into memory.</summary>
    public static IReadOnlyList<CueTrack> Parse(IEnumerable<string> lines)
    {
        var tracks = new List<CueTrack>();

        string? currentFile = null;
        int? number = null;
        string? dataType = null;
        string? title = null;
        int? index0 = null;
        int? index1 = null;

        void Flush()
        {
            if (number is null) return;
            if (currentFile is null)
                throw new InvalidDataException($"TRACK {number} appears before any FILE directive.");

            tracks.Add(new CueTrack
            {
                Number = number.Value,
                DataType = dataType ?? "AUDIO",
                FileName = currentFile,
                Title = title,
                IndexOneSector = index1 ?? index0 ?? 0,
                IndexZeroSector = index0,
            });

            number = null;
            dataType = null;
            title = null;
            index0 = null;
            index1 = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var keyword = FirstToken(line);
            switch (keyword.ToUpperInvariant())
            {
                case "FILE":
                    Flush();
                    currentFile = ExtractQuoted(line) ?? SecondToken(line);
                    break;

                case "TRACK":
                {
                    Flush();
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        throw new InvalidDataException($"Malformed TRACK line: {line}");
                    number = n;
                    dataType = parts.Length > 2 ? parts[2] : "AUDIO";
                    break;
                }

                case "TITLE":
                    // A TITLE before the first TRACK is the disc title; ignore it.
                    if (number is not null) title = ExtractQuoted(line);
                    break;

                case "INDEX":
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) break;
                    if (!int.TryParse(parts[1], out var indexNumber)) break;
                    var sector = ParseMsf(parts[2]);
                    if (indexNumber == 0) index0 = sector;
                    else if (indexNumber == 1) index1 = sector;
                    break;
                }
            }
        }

        Flush();

        if (tracks.Count == 0)
            throw new InvalidDataException("Cue sheet contains no tracks.");

        return tracks;
    }

    /// <summary>Converts an <c>MM:SS:FF</c> timestamp to an absolute sector index (75 frames per second).</summary>
    public static int ParseMsf(string msf)
    {
        var parts = msf.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var minutes)
            || !int.TryParse(parts[1], out var seconds)
            || !int.TryParse(parts[2], out var frames))
        {
            throw new InvalidDataException($"Malformed MSF timestamp: {msf}");
        }

        return (minutes * 60 + seconds) * 75 + frames;
    }

    private static string FirstToken(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? line : line[..space];
    }

    private static string SecondToken(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    private static string? ExtractQuoted(string line)
    {
        var open = line.IndexOf('"');
        if (open < 0) return null;
        var close = line.LastIndexOf('"');
        return close > open ? line[(open + 1)..close] : null;
    }
}
