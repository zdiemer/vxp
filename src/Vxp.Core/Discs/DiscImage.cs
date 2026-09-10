namespace Vxp.Discs;

/// <summary>One track of a mounted <see cref="DiscImage"/>, exposed as a byte range.</summary>
public sealed class DiscTrack
{
    internal DiscTrack(DiscImage disc, int number, string? title, string filePath, long byteOffset, long byteLength)
    {
        Disc = disc;
        Number = number;
        Title = title;
        FilePath = filePath;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
    }

    /// <summary>The disc this track belongs to.</summary>
    public DiscImage Disc { get; }

    /// <summary>1-based track number.</summary>
    public int Number { get; }

    /// <summary>Mastering title from the cue sheet, if any.</summary>
    public string? Title { get; }

    /// <summary>Absolute path of the backing binary file.</summary>
    public string FilePath { get; }

    /// <summary>Byte offset of the track within <see cref="FilePath"/>.</summary>
    public long ByteOffset { get; }

    /// <summary>Length of the track in bytes.</summary>
    public long ByteLength { get; }

    /// <summary>Length of the track in raw CD sectors.</summary>
    public long SectorCount => ByteLength / CueSheet.SectorSize;

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="offset"/> within the track.</summary>
    /// <returns>The number of bytes actually read, which is short at the end of the track.</returns>
    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var available = ByteLength - offset;
        if (available <= 0) return 0;
        if (destination.Length > available) destination = destination[..(int)available];
        return Disc.ReadFile(FilePath, ByteOffset + offset, destination);
    }
}

/// <summary>
/// A VideoNow disc mounted from a cue sheet plus its binary track data. Handles both
/// the single-<c>.bin</c> and one-<c>.bin</c>-per-track layouts produced by ripping tools.
/// </summary>
public sealed class DiscImage : IDisposable
{
    private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _readLock = new();

    private DiscImage(string cuePath, IReadOnlyList<DiscTrack> tracks)
    {
        CuePath = cuePath;
        Tracks = tracks;
    }

    /// <summary>Path of the cue sheet this disc was mounted from.</summary>
    public string CuePath { get; }

    /// <summary>Tracks in disc order. Index 0 is track 1.</summary>
    public IReadOnlyList<DiscTrack> Tracks { get; }

    /// <summary>Disc name derived from the cue sheet file name.</summary>
    public string Name => Path.GetFileNameWithoutExtension(CuePath);

    /// <summary>Mounts the disc described by the cue sheet at <paramref name="cuePath"/>.</summary>
    public static DiscImage Open(string cuePath)
    {
        cuePath = Path.GetFullPath(cuePath);
        var directory = Path.GetDirectoryName(cuePath) ?? ".";
        var cueTracks = CueSheet.Load(cuePath);

        var resolved = new List<DiscTrack>(cueTracks.Count);
        var image = new DiscImage(cuePath, resolved);

        for (var i = 0; i < cueTracks.Count; i++)
        {
            var cue = cueTracks[i];
            var filePath = ResolveTrackFile(directory, cue.FileName);
            var fileLength = new FileInfo(filePath).Length;

            long startByte = (long)cue.IndexOneSector * CueSheet.SectorSize;

            // In a single-file image the next track's INDEX bounds this one. In a
            // file-per-track image each track simply runs to the end of its own file.
            long endByte = fileLength;
            if (i + 1 < cueTracks.Count)
            {
                var next = cueTracks[i + 1];
                if (string.Equals(next.FileName, cue.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    var nextStartSector = next.IndexZeroSector ?? next.IndexOneSector;
                    endByte = (long)nextStartSector * CueSheet.SectorSize;
                }
            }

            if (endByte > fileLength) endByte = fileLength;
            if (endByte < startByte) endByte = startByte;

            resolved.Add(new DiscTrack(image, cue.Number, cue.Title, filePath, startByte, endByte - startByte));
        }

        return image;
    }

    /// <summary>Returns the track with the given 1-based <paramref name="number"/>, or <see langword="null"/>.</summary>
    public DiscTrack? FindTrack(int number)
        => Tracks.FirstOrDefault(t => t.Number == number);

    internal int ReadFile(string path, long offset, Span<byte> destination)
    {
        lock (_readLock)
        {
            if (!_files.TryGetValue(path, out var stream))
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
                _files[path] = stream;
            }

            stream.Seek(offset, SeekOrigin.Begin);

            var total = 0;
            while (total < destination.Length)
            {
                var read = stream.Read(destination[total..]);
                if (read <= 0) break;
                total += read;
            }

            return total;
        }
    }

    private static string ResolveTrackFile(string directory, string fileName)
    {
        var direct = Path.Combine(directory, fileName);
        if (File.Exists(direct)) return direct;

        // Cue sheets are sometimes moved alongside renamed binaries; fall back to a
        // case-insensitive match on the bare file name before giving up.
        var bare = Path.GetFileName(fileName);
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(candidate), bare, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        throw new FileNotFoundException($"Track file referenced by the cue sheet was not found: {fileName}", direct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_readLock)
        {
            foreach (var stream in _files.Values) stream.Dispose();
            _files.Clear();
        }
    }
}
