using Vxp.Discs;
using Vxp.Emulation;

namespace Vxp;

/// <summary>
/// A disc mounted for the player: the image, a player over it and its survey, owned
/// together so a swap disposes all three at once.
/// </summary>
public sealed class LoadedDisc : IDisposable
{
    private LoadedDisc(string path, VideoNowPlayer player, DiscMap map)
    {
        Path = path;
        Player = player;
        Map = map;
    }

    /// <summary>The file that was opened, as a full path.</summary>
    public string Path { get; }

    /// <summary>The player over the disc. Disposing this also unmounts the image.</summary>
    public VideoNowPlayer Player { get; }

    /// <summary>The disc survey, for the track browser and the info page.</summary>
    public DiscMap Map { get; }

    /// <summary>
    /// Mounts the disc at <paramref name="path"/>: a cue sheet, a <c>.zip</c> holding one,
    /// or a <c>.bin</c> with its cue sheet beside it.
    /// </summary>
    /// <remarks>Nothing is left open if this throws.</remarks>
    /// <exception cref="FileNotFoundException">The file, or a track it names, is missing.</exception>
    /// <exception cref="InvalidDataException">The file is not a VideoNow disc.</exception>
    public static LoadedDisc Open(string path)
    {
        var resolved = DiscFiles.Resolve(path);
        var image = DiscImage.Open(resolved);

        try
        {
            var player = new VideoNowPlayer(image);
            return new LoadedDisc(System.IO.Path.GetFullPath(resolved), player, DiscMap.Build(image));
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Player.Dispose();
}

/// <summary>Which files can be opened as a disc, and how to get from one to its cue sheet.</summary>
public static class DiscFiles
{
    /// <summary>Extensions offered by the open dialog and accepted from a drop.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".cue", ".zip", ".bin"];

    /// <summary>
    /// The filter string for a Windows open-file dialog: pairs of description and pattern,
    /// each ending in a NUL, with a second NUL closing the list.
    /// </summary>
    public static string DialogFilter
    {
        get
        {
            var patterns = string.Join(';', Extensions.Select(e => "*" + e));
            return $"VideoNow discs ({patterns})\0{patterns}\0All files (*.*)\0*.*\0\0";
        }
    }

    /// <summary>Whether <paramref name="path"/> has an extension a disc is opened from.</summary>
    public static bool IsDiscFile(string path)
        => Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The path <see cref="DiscImage.Open"/> should be given for <paramref name="path"/>.
    /// A cue sheet or zip is used as it is; a <c>.bin</c> is traded for the cue sheet that
    /// names it, because the track layout lives in the cue sheet and not in the binary.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist, or a <c>.bin</c> has no cue sheet beside it.
    /// </exception>
    public static string Resolve(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Disc not found: {path}", path);

        if (!string.Equals(System.IO.Path.GetExtension(path), ".bin", StringComparison.OrdinalIgnoreCase))
            return path;

        var sibling = System.IO.Path.ChangeExtension(path, ".cue");
        if (File.Exists(sibling)) return sibling;

        // A multi-track rip names its cue sheet after the disc, not after any one track,
        // so look for whichever cue sheet in the folder refers to this file.
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        var name = System.IO.Path.GetFileName(path);

        foreach (var cue in Directory.EnumerateFiles(directory, "*.cue").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.ReadAllText(cue).Contains(name, StringComparison.OrdinalIgnoreCase)) return cue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable cue sheet is just not the one being looked for.
            }
        }

        throw new FileNotFoundException($"No cue sheet beside {name}; open the .cue or .zip instead.", path);
    }
}
