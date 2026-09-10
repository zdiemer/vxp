using Vxp.Discs;

namespace Vxp.Format;

/// <summary>Identifies which VideoNow variant a disc or track was mastered in.</summary>
/// <remarks>
/// Color and XP share the same sync word, so they are told apart by how many times it
/// repeats at the head of a frame: 24 for Color, 12 for XP.
/// </remarks>
public static class FormatDetector
{
    /// <summary>
    /// Detects the layout of a single track, or <see langword="null"/> if it holds no
    /// recognisable VideoNow stream.
    /// </summary>
    public static FrameLayout? Detect(DiscTrack track)
    {
        var probeLength = (int)Math.Min(track.ByteLength, 64 * 1024);
        if (probeLength < FrameLayout.GroupBytes * 32) return null;

        var buffer = new byte[probeLength];
        var read = track.Read(0, buffer);
        return Detect(buffer.AsSpan(0, read));
    }

    /// <summary>Detects the layout of a raw interleaved byte stream.</summary>
    public static FrameLayout? Detect(ReadOnlySpan<byte> stream)
    {
        var sync = FrameLayout.SyncWord;
        var start = stream.IndexOf(sync);
        if (start < 0) return null;

        // Count how many consecutive interleave groups begin with the sync word.
        var repeats = 0;
        var offset = start;
        while (offset + FrameLayout.GroupBytes <= stream.Length
               && stream.Slice(offset, sync.Length).SequenceEqual(sync))
        {
            repeats++;
            offset += FrameLayout.GroupBytes;
        }

        return FrameLayout.Known.FirstOrDefault(l => l.SyncRepeatCount == repeats);
    }

    /// <summary>
    /// Detects the layout of a whole disc by taking the most common per-track result.
    /// </summary>
    public static FrameLayout? Detect(DiscImage disc)
    {
        var votes = new Dictionary<FrameLayout, int>();
        foreach (var track in disc.Tracks)
        {
            var layout = Detect(track);
            if (layout is null) continue;
            votes[layout] = votes.GetValueOrDefault(layout) + 1;
        }

        return votes.Count == 0 ? null : votes.MaxBy(kv => kv.Value).Key;
    }
}
