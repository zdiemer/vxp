using System.IO.Compression;

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

    /// <summary>
    /// Absolute path of the backing binary file, or its entry name when the disc was
    /// opened from a zip.
    /// </summary>
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
/// the single-<c>.bin</c> and one-<c>.bin</c>-per-track layouts produced by ripping tools,
/// either loose on disk or inside a <c>.zip</c> the way Redump sets are distributed.
/// </summary>
/// <remarks>
/// A zip entry compressed with Deflate cannot be seeked, and playback seeks constantly, so
/// each track file is decompressed, as far as reads have reached, into a temporary file
/// that the operating system deletes when it is closed — even if the process is killed.
/// The archive itself is only ever read, so a set stays byte-identical to its DAT.
/// </remarks>
public sealed class DiscImage : IDisposable
{
    private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArchiveTrackFile> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _inflateBuffer = new byte[1 << 16];
    private readonly object _readLock = new();
    private readonly ZipArchive? _archive;
    private bool _disposed;

    private DiscImage(string cuePath, IReadOnlyList<DiscTrack> tracks, ZipArchive? archive)
    {
        CuePath = cuePath;
        Tracks = tracks;
        _archive = archive;
    }

    /// <summary>Path of the cue sheet, or of the <c>.zip</c> holding it, this disc was mounted from.</summary>
    public string CuePath { get; }

    /// <summary>Tracks in disc order. Index 0 is track 1.</summary>
    public IReadOnlyList<DiscTrack> Tracks { get; }

    /// <summary>Disc name derived from the cue sheet (or archive) file name.</summary>
    public string Name => Path.GetFileNameWithoutExtension(CuePath);

    /// <summary>Whether the tracks are read out of a zip archive.</summary>
    public bool IsArchive => _archive is not null;

    /// <summary>
    /// Mounts the disc described by the cue sheet at <paramref name="path"/>, or by the cue
    /// sheet inside the <c>.zip</c> at <paramref name="path"/>.
    /// </summary>
    public static DiscImage Open(string path)
    {
        path = Path.GetFullPath(path);
        return IsZip(path) ? OpenArchive(path) : OpenCue(path);
    }

    /// <summary>Whether <paramref name="path"/> names a zip archive rather than a cue sheet.</summary>
    public static bool IsZip(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    private static DiscImage OpenCue(string cuePath)
    {
        var directory = Path.GetDirectoryName(cuePath) ?? ".";
        var cueTracks = CueSheet.Load(cuePath);

        var image = new DiscImage(cuePath, new List<DiscTrack>(cueTracks.Count), archive: null);
        image.Resolve(cueTracks, name =>
        {
            var filePath = ResolveTrackFile(directory, name);
            return (filePath, new FileInfo(filePath).Length);
        });

        return image;
    }

    private static DiscImage OpenArchive(string zipPath)
    {
        var archive = ZipFile.OpenRead(zipPath);

        try
        {
            var cueEntry = FindCueEntry(archive, zipPath);
            using var reader = new StreamReader(cueEntry.Open());
            var cueTracks = CueSheet.Parse(reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n'));

            var image = new DiscImage(zipPath, new List<DiscTrack>(cueTracks.Count), archive);
            image.Resolve(cueTracks, name =>
            {
                var entry = ResolveTrackEntry(archive, cueEntry, name);
                return (entry.FullName, entry.Length);
            });

            return image;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private void Resolve(IReadOnlyList<CueTrack> cueTracks, Func<string, (string Path, long Length)> locate)
    {
        var resolved = (List<DiscTrack>)Tracks;

        for (var i = 0; i < cueTracks.Count; i++)
        {
            var cue = cueTracks[i];
            var (filePath, fileLength) = locate(cue.FileName);

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

            resolved.Add(new DiscTrack(this, cue.Number, cue.Title, filePath, startByte, endByte - startByte));
        }
    }

    /// <summary>Returns the track with the given 1-based <paramref name="number"/>, or <see langword="null"/>.</summary>
    public DiscTrack? FindTrack(int number)
        => Tracks.FirstOrDefault(t => t.Number == number);

    /// <summary>
    /// For a disc read from a zip, decompresses every track file on a background thread, in
    /// disc order, so that jumping to a branch later does not wait on the archive. Does
    /// nothing for a loose cue sheet.
    /// </summary>
    /// <remarks>
    /// Reads never wait for this to finish: work is done a chunk at a time with the lock
    /// released in between, and a read decompresses whatever it needs itself.
    /// </remarks>
    public void PrecacheInBackground()
    {
        if (_archive is null) return;

        const long Chunk = 1 << 20;
        var names = Tracks.Select(t => t.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var thread = new Thread(() =>
        {
            try
            {
                foreach (var name in names)
                {
                    while (true)
                    {
                        lock (_readLock)
                        {
                            if (_disposed) return;

                            var file = ArchiveFile(name);
                            if (file.Complete) break;
                            file.FillTo(file.Filled + Chunk);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Leave it for the read that needs it, which reports the failure properly.
            }
        })
        {
            IsBackground = true,
            Name = "vxp disc precache",
            Priority = ThreadPriority.BelowNormal,
        };

        thread.Start();
    }

    internal int ReadFile(string path, long offset, Span<byte> destination)
    {
        lock (_readLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            FileStream stream;
            if (_archive is null)
            {
                stream = LooseFile(path);
            }
            else
            {
                var file = ArchiveFile(path);
                file.FillTo(offset + destination.Length);
                stream = file.Cache;
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

    /// <summary>The open stream behind a loose track file. Call under the read lock.</summary>
    private FileStream LooseFile(string path)
    {
        if (_files.TryGetValue(path, out var stream)) return stream;

        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
        _files[path] = stream;
        return stream;
    }

    /// <summary>The decompression cache behind a track file inside the zip. Call under the read lock.</summary>
    private ArchiveTrackFile ArchiveFile(string entryName)
    {
        if (_entries.TryGetValue(entryName, out var file)) return file;

        var entry = _archive!.GetEntry(entryName)
            ?? throw new FileNotFoundException($"Zip entry vanished: {entryName}", entryName);

        file = new ArchiveTrackFile(entry, _inflateBuffer);
        _entries[entryName] = file;
        return file;
    }

    /// <summary>
    /// One zip entry, decompressed only as far as anything has asked to read, into a
    /// temporary file the operating system deletes when it is closed.
    /// </summary>
    /// <remarks>
    /// The player surveys every track when a disc opens but reads only each one's first
    /// frame, so decompressing whole entries up front would pull the entire disc across a
    /// network share before the window appears.
    /// </remarks>
    private sealed class ArchiveTrackFile : IDisposable
    {
        private readonly ZipArchiveEntry _entry;
        private readonly byte[] _buffer;
        private Stream? _source;

        public ArchiveTrackFile(ZipArchiveEntry entry, byte[] buffer)
        {
            _entry = entry;
            _buffer = buffer;

            var directory = Path.Combine(Path.GetTempPath(), "vxp");
            Directory.CreateDirectory(directory);

            Cache = new FileStream(
                Path.Combine(directory, Path.GetRandomFileName()),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 0, FileOptions.DeleteOnClose);
        }

        /// <summary>The decompressed bytes so far.</summary>
        public FileStream Cache { get; }

        /// <summary>How many bytes of the entry have been decompressed.</summary>
        public long Filled { get; private set; }

        /// <summary>Whether the whole entry has been decompressed.</summary>
        public bool Complete => Filled >= _entry.Length;

        /// <summary>Decompresses until at least <paramref name="end"/> bytes are cached, or the entry ends.</summary>
        public void FillTo(long end)
        {
            end = Math.Min(end, _entry.Length);
            if (Filled >= end) return;

            _source ??= _entry.Open();
            Cache.Seek(Filled, SeekOrigin.Begin);

            while (Filled < end)
            {
                var read = _source.Read(_buffer, 0, (int)Math.Min(_buffer.Length, _entry.Length - Filled));
                if (read <= 0)
                    throw new InvalidDataException($"{_entry.FullName} ended after {Filled} bytes; the archive says {_entry.Length}.");

                Cache.Write(_buffer, 0, read);
                Filled += read;
            }

            if (Complete)
            {
                _source.Dispose();
                _source = null;
            }
        }

        public void Dispose()
        {
            _source?.Dispose();
            Cache.Dispose();
        }
    }

    private static ZipArchiveEntry FindCueEntry(ZipArchive archive, string zipPath)
    {
        var cues = archive.Entries
            .Where(e => string.Equals(Path.GetExtension(e.Name), ".cue", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return cues.Length switch
        {
            0 => throw new InvalidDataException($"No cue sheet inside {Path.GetFileName(zipPath)}."),
            1 => cues[0],

            // Several discs in one archive: the one named like the archive is the one meant.
            _ => cues.FirstOrDefault(e => string.Equals(
                    Path.GetFileNameWithoutExtension(e.Name),
                    Path.GetFileNameWithoutExtension(zipPath),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(
                    $"{Path.GetFileName(zipPath)} holds {cues.Length} cue sheets and none is named after the archive."),
        };
    }

    private static ZipArchiveEntry ResolveTrackEntry(ZipArchive archive, ZipArchiveEntry cue, string fileName)
    {
        // FILE names are relative to the cue sheet, which may itself sit in a folder.
        var folder = cue.FullName[..^cue.Name.Length];
        var relative = (folder + fileName).Replace('\\', '/');

        var bare = Path.GetFileName(fileName.Replace('\\', '/'));
        return archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, relative, StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(e => string.Equals(e.Name, bare, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Track file referenced by the cue sheet is not in the archive: {fileName}", fileName);
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
            if (_disposed) return;
            _disposed = true;

            foreach (var stream in _files.Values) stream.Dispose();
            foreach (var entry in _entries.Values) entry.Dispose();
            _files.Clear();
            _entries.Clear();
            _archive?.Dispose();
        }
    }
}
