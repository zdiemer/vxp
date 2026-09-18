using Vxp.Discs;
using Vxp.Format;

namespace Vxp.Emulation;

/// <summary>Everything worth knowing about one track, read from the disc.</summary>
/// <param name="Number">1-based track number.</param>
/// <param name="Title">Mastering title from the cue sheet, if any.</param>
/// <param name="HasVideo">False for padding tracks that carry no VideoNow stream.</param>
/// <param name="Format">Which VideoNow variant this track is mastered in.</param>
/// <param name="FrameCount">Whole frames the track contains.</param>
/// <param name="DeclaredFrameCount">Frame count the disc declares in its own header.</param>
/// <param name="Duration">Running time at the disc's exact frame rate.</param>
/// <param name="ByteLength">Size of the track in bytes.</param>
/// <param name="Kind">What kind of segment this is, from its first frame.</param>
/// <param name="ContinueTrack">Register 0x4F: the track to play next when no branch is taken, or 0.</param>
/// <param name="Branches">Branch table entries: those of the first frame, or of every frame when surveyed in full.</param>
public sealed record TrackInfo(
    int Number,
    string? Title,
    bool HasVideo,
    DiscFormat Format,
    int FrameCount,
    int DeclaredFrameCount,
    TimeSpan Duration,
    long ByteLength,
    SegmentKind Kind,
    int ContinueTrack,
    IReadOnlyList<BranchEntry> Branches)
{
    /// <summary>True when this segment puts a choice to the viewer.</summary>
    public bool OffersChoice => Kind is SegmentKind.Choice or SegmentKind.TaggedChoice;

    /// <summary>
    /// True when the frame count worked out from the track's size disagrees with the
    /// count the disc declares, which suggests a truncated or padded rip.
    /// </summary>
    public bool FrameCountMismatch =>
        HasVideo && DeclaredFrameCount > 0 && Math.Abs(DeclaredFrameCount - FrameCount) > 1;
}

/// <summary>
/// A survey of a whole disc: every track's timing and, for interactive titles, the
/// branch graph that connects them.
/// </summary>
/// <remarks>
/// Building a map reads the first frame header of every track, which is fast enough to do on
/// load and gives both the track browser and the command line one shared model.
/// </remarks>
public sealed class DiscMap
{
    private readonly Dictionary<int, TrackInfo> _byNumber;

    private DiscMap(string name, FrameLayout? layout, IReadOnlyList<TrackInfo> tracks)
    {
        Name = name;
        Layout = layout;
        Tracks = tracks;
        _byNumber = tracks.ToDictionary(t => t.Number);
    }

    /// <summary>Disc name, taken from the cue sheet file name.</summary>
    public string Name { get; }

    /// <summary>Layout the disc is mastered in, or null if it holds no VideoNow stream.</summary>
    public FrameLayout? Layout { get; }

    /// <summary>Every track in disc order.</summary>
    public IReadOnlyList<TrackInfo> Tracks { get; }

    /// <summary>Tracks that actually carry video.</summary>
    public IEnumerable<TrackInfo> PlayableTracks => Tracks.Where(t => t.HasVideo);

    /// <summary>Total running time of every track that carries video.</summary>
    public TimeSpan TotalDuration => PlayableTracks.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration);

    /// <summary>True when any segment offers the viewer a choice.</summary>
    public bool IsInteractive => Tracks.Any(t => t.OffersChoice);

    /// <summary>Surveys <paramref name="disc"/>.</summary>
    /// <param name="disc">The disc to survey.</param>
    /// <param name="everyFrame">
    /// Read the header of every frame rather than only the first, so that timed prompts
    /// partway through a segment appear among its branches. That reads the whole disc,
    /// so it suits the command line better than loading a disc to play.
    /// </param>
    public static DiscMap Build(DiscImage disc, bool everyFrame = false)
    {
        var tracks = new List<TrackInfo>(disc.Tracks.Count);

        foreach (var track in disc.Tracks)
        {
            var layout = FormatDetector.Detect(track);
            if (layout is null)
            {
                tracks.Add(new TrackInfo(
                    track.Number, track.Title, HasVideo: false, DiscFormat.Unknown,
                    0, 0, TimeSpan.Zero, track.ByteLength, SegmentKind.None, 0, []));
                continue;
            }

            var reader = new TrackReader(track, layout);
            var header = reader.ReadHeader(0);
            var branches = header?.Branches ?? [];

            if (everyFrame && header is not null)
            {
                var seen = new List<BranchEntry>(branches);
                for (var i = 1; i < reader.FrameCount; i++)
                {
                    foreach (var branch in reader.ReadHeader(i)?.Branches ?? [])
                        if (!seen.Any(b => SameEntry(b, branch))) seen.Add(branch);
                }

                branches = seen.OrderBy(b => b.Slot).ToArray();
            }

            tracks.Add(new TrackInfo(
                track.Number,
                track.Title,
                HasVideo: reader.FrameCount > 0,
                layout.Format,
                reader.FrameCount,
                header?.FrameCount ?? 0,
                reader.Duration,
                track.ByteLength,
                header?.Kind ?? SegmentKind.None,
                header?.ContinueTrack ?? 0,
                branches));
        }

        return new DiscMap(disc.Name, FormatDetector.Detect(disc), tracks);
    }

    /// <summary>Returns the track with the given number, or null.</summary>
    public TrackInfo? Find(int number) => _byNumber.GetValueOrDefault(number);

    /// <summary>
    /// Every track reachable from <paramref name="start"/> by following branch tables and
    /// disc order, which is the set a viewer could actually see from there.
    /// </summary>
    public IReadOnlyList<int> Reachable(int start)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>();

        if (Find(start)?.HasVideo == true)
        {
            seen.Add(start);
            queue.Enqueue(start);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in Successors(current))
            {
                if (seen.Add(next)) queue.Enqueue(next);
            }
        }

        return seen.Order().ToArray();
    }

    /// <summary>
    /// Tracks that can directly follow <paramref name="number"/> when the disc is played
    /// the way it declares: its branches, then the track register 0x4F names, or failing
    /// that the tracks a branch into it queues behind it, or failing that disc order.
    /// </summary>
    public IReadOnlyList<int> Successors(int number)
    {
        var track = Find(number);
        if (track is null || !track.HasVideo) return [];

        var next = new List<int>();

        foreach (var branch in track.Branches) next.Add(branch.Track);

        if (track.ContinueTrack > 0)
        {
            next.Add(track.ContinueTrack);
        }
        else
        {
            var queued = QueuedAfter(number);
            next.AddRange(queued);

            // A choice segment goes nowhere unasked; any other segment falls through to
            // disc order unless a branch into it queues what comes next.
            if (!track.OffersChoice && queued.Count == 0)
            {
                var following = Tracks
                    .SkipWhile(t => t.Number != number)
                    .Skip(1)
                    .FirstOrDefault(t => t.HasVideo);

                if (following is not null) next.Add(following.Number);
            }
        }

        return next.Where(n => Find(n)?.HasVideo == true).Distinct().ToArray();
    }

    /// <summary>Tracks that branch play lists queue directly behind <paramref name="number"/>.</summary>
    private IReadOnlyList<int> QueuedAfter(int number)
    {
        var after = new List<int>();

        foreach (var track in PlayableTracks)
        foreach (var branch in track.Branches)
        {
            var list = branch.PlayList.ToArray();
            for (var i = 0; i + 1 < list.Length; i++)
                if (list[i] == number) after.Add(list[i + 1]);
        }

        return after;
    }

    /// <summary>
    /// Tracks that no other track reaches, ignoring plain disc order. On an interactive
    /// disc these are usually the entry points of a scene rather than dead content.
    /// </summary>
    public IReadOnlyList<int> Unreferenced()
    {
        var referenced = new HashSet<int>();

        foreach (var track in PlayableTracks)
        {
            foreach (var branch in track.Branches) referenced.UnionWith(branch.PlayList);
            if (track.ContinueTrack > 0 && track.ContinueTrack != track.Number) referenced.Add(track.ContinueTrack);
        }

        return PlayableTracks.Where(t => !referenced.Contains(t.Number)).Select(t => t.Number).ToArray();
    }

    private static bool SameEntry(BranchEntry a, BranchEntry b) =>
        a.Slot == b.Slot && a.Tag == b.Tag && a.PlayList.SequenceEqual(b.PlayList);

    /// <summary>
    /// Runs of consecutive tracks with byte-identical lengths, which on interactive discs
    /// are alternative takes of the same scene cut to the same length.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> ParallelGroups(int minimumSize = 2)
    {
        var groups = new List<IReadOnlyList<int>>();
        var run = new List<TrackInfo>();

        void Flush()
        {
            if (run.Count >= minimumSize) groups.Add(run.Select(t => t.Number).ToArray());
            run.Clear();
        }

        foreach (var track in PlayableTracks)
        {
            if (run.Count > 0 && run[^1].ByteLength != track.ByteLength) Flush();
            run.Add(track);
        }

        Flush();
        return groups;
    }
}
