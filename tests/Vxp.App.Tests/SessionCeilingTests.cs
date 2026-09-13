using Vxp.Cli;
using Vxp.Format;
using Xunit;

namespace Vxp.Tests;

/// <summary>
/// A scripted run has to come back, whatever the disc asks for.
/// </summary>
/// <remarks>
/// Retail discs do ask for a segment to repeat for ever: a menu whose first branch names
/// its own track sits there until the viewer picks something, which is exactly what Teen
/// Titans track 23 does. That is right on the hardware and a hang in a script, so every
/// open-ended play answers to <c>--max-seconds</c>.
/// </remarks>
public class SessionCeilingTests
{
    private static int RunWithin(string cue, string commands, int maxSeconds, int waitSeconds = 30)
    {
        var args = CommandLine.Parse([cue, "--commands", commands, "--max-seconds", maxSeconds.ToString(), "--choice-timeout", "firstBranch"]);

        // Run on another thread: if the ceiling ever stops working, the test should fail
        // on a deadline rather than wedge the whole test run.
        var run = System.Threading.Tasks.Task.Run(() => SessionCommand.Run(args));

        Assert.True(
            run.Wait(TimeSpan.FromSeconds(waitSeconds)),
            $"'{commands}' never came back; the --max-seconds ceiling is not holding.");

        return run.Result;
    }

    [Fact]
    public void PlayTrackComesBackFromASegmentThatBranchesToItself()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 8),
            // The first branch names this very track, so nothing pressed means play it again.
            new TrackSpec(2, 8, SegmentKind.Choice, Branches: [2]),
            new TrackSpec(3, 8));

        Assert.Equal(0, RunWithin(disc.CuePath, "track 2; play track", maxSeconds: 4));
    }

    [Fact]
    public void PlayAllComesBackFromADiscThatLoops()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 8),
            new TrackSpec(2, 8, SegmentKind.Choice, Branches: [2]));

        Assert.Equal(0, RunWithin(disc.CuePath, "play all", maxSeconds: 4));
    }

    [Fact]
    public void AnOrdinaryTrackStillEndsWhenItEndsRatherThanAtTheCeiling()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 8),
            new TrackSpec(2, 8),
            new TrackSpec(3, 8));

        // A ceiling far longer than the track: reaching the end must be what stops it.
        var started = DateTime.UtcNow;
        Assert.Equal(0, RunWithin(disc.CuePath, "track 2; play track", maxSeconds: 3600));

        // Eight frames is a fraction of a second of disc time; if the ceiling were what
        // stopped this, it would have had to render an hour of audio first.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the track did not end on its own");
    }
}
