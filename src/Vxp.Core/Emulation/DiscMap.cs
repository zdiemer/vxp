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
/// <param name="Kind">How the segment behaves when it ends.</param>
/// <param name="ContinueTrack">Value of register 0x4F, whose role is not fully established.</param>
/// <param name="Branches">Destinations the segment's branch table names.</param>
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
/// Building a map reads the first frame of every track, which is fast enough to do on
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
    public static DiscMap Build(DiscImage disc)
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
            var header = reader.ReadFrame(0)?.ReadHeader();

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
                header?.Branches ?? []));
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

    /// <summary>Tracks that can directly follow <paramref name="number"/>.</summary>
    public IReadOnlyList<int> Successors(int number)
    {
        var track = Find(number);
        if (track is null || !track.HasVideo) return [];

        var next = new List<int>();

        foreach (var branch in track.Branches)
        {
            if (Find(branch.Track)?.HasVideo == true) next.Add(branch.Track);
        }

        if (track.Kind is SegmentKind.Hub or SegmentKind.Restart
            && Find(track.ContinueTrack)?.HasVideo == true)
        {
            next.Add(track.ContinueTrack);
        }

        if (track.Kind is not (SegmentKind.Terminal or SegmentKind.Restart) && !track.OffersChoice)
        {
            var following = Tracks
                .SkipWhile(t => t.Number != number)
                .Skip(1)
                .FirstOrDefault(t => t.HasVideo);

            if (following is not null) next.Add(following.Number);
        }

        return next.Distinct().ToArray();
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
            foreach (var branch in track.Branches) referenced.Add(branch.Track);
            if (track.Kind is SegmentKind.Hub or SegmentKind.Restart) referenced.Add(track.ContinueTrack);
        }

        return PlayableTracks.Where(t => !referenced.Contains(t.Number)).Select(t => t.Number).ToArray();
    }

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
