using Vxp.Discs;
using Vxp.Emulation;
using Vxp.Format;
using Xunit;

namespace Vxp.Tests;

public class PlayerTransportTests
{
    /// <summary>Runs the player forward by the audio it would consume in one whole frame.</summary>
    private static void RunFrames(VideoNowPlayer player, int frames)
    {
        var buffer = new short[player.Layout.AudioBytes];
        for (var i = 0; i < frames; i++) player.RenderAudio(buffer);
    }

    [Fact]
    public void StartsOnTheFirstPlayableTrack()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 3),
            new TrackSpec(2, 3));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.Equal(1, player.CurrentTrack);
        Assert.Equal(3, player.TrackFrameCount);
        Assert.Equal(TransportState.Stopped, player.State);
    }

    [Fact]
    public void SkipsPaddingTracksWithNoVideoStream()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 0, Empty: true),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.NextTrack();

        Assert.False(player.IsPlayable(2));
        Assert.Equal(3, player.CurrentTrack);
    }

    /// <summary>
    /// Batman vs The Joker leaves 217 frames of black silence (tracks 3 and 4) between the
    /// title sequence and the first choice, and playing into them looked like a hang.
    /// </summary>
    [Fact]
    public void PlaysOverBlankTracksBetweenTheTitleAndTheFirstScene()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2, Blank: true),
            new TrackSpec(4, 3, Blank: true),
            new TrackSpec(5, 4, SegmentKind.Choice, Branches: [6]),
            new TrackSpec(6, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.SelectTrack(2);
        player.Play();
        RunFrames(player, 3);

        Assert.True(player.IsBlank(3));
        Assert.True(player.IsBlank(4));
        Assert.False(player.IsBlank(5));
        Assert.Equal(5, player.CurrentTrack);
        Assert.True(player.IsChoicePoint);
    }

    [Fact]
    public void SkippingTracksPassesOverBlankOnesButTheyCanStillBeSelected()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2, Blank: true),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        player.NextTrack();
        Assert.Equal(3, player.CurrentTrack);

        player.PreviousTrack();
        Assert.Equal(1, player.CurrentTrack);

        player.SelectTrack(2);
        Assert.Equal(2, player.CurrentTrack);
    }

    [Fact]
    public void BlankChoiceSegmentsAreStillVisited()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2, SegmentKind.TaggedChoice, Branches: [3], Blank: true),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.NextTrack();

        Assert.False(player.IsBlank(2));
        Assert.Equal(2, player.CurrentTrack);
    }

    [Fact]
    public void PlaysStraightIntoTheNextTrack()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        RunFrames(player, 3);

        Assert.Equal(2, player.CurrentTrack);
    }

    [Fact]
    public void StopsAtTheEndOfTheDisc()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        RunFrames(player, 6);

        Assert.Equal(TransportState.Stopped, player.State);
    }

    [Fact]
    public void PausingWritesSilenceAndHoldsPosition()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 8));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        RunFrames(player, 2);

        var frame = player.CurrentFrame;
        player.Pause();

        var buffer = new short[512];
        Array.Fill(buffer, (short)1234);
        player.RenderAudio(buffer);

        Assert.All(buffer, s => Assert.Equal(0, s));
        Assert.Equal(frame, player.CurrentFrame);
    }

    [Fact]
    public void SeekingMovesWithinTheTrackAndClampsToItsEnds()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 20));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        player.SeekToFrame(10);
        Assert.Equal(10, player.CurrentFrame);

        player.SeekToFrame(500);
        Assert.Equal(19, player.CurrentFrame);

        player.SeekToFrame(-5);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Fact]
    public void SteppingAFramePauses()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 10));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        player.StepFrame(3);

        Assert.Equal(TransportState.Paused, player.State);
        Assert.Equal(3, player.CurrentFrame);
    }

    [Fact]
    public void SkipBackRestartsTheTrackOncePlaybackHasMovedOn()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 40),
            new TrackSpec(2, 40));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.SelectTrack(2);
        player.Play();
        RunFrames(player, 20);

        player.PreviousTrack();
        Assert.Equal(2, player.CurrentTrack);
        Assert.Equal(0, player.CurrentFrame);

        player.PreviousTrack();
        Assert.Equal(1, player.CurrentTrack);
    }

    [Fact]
    public void DoubleSpeedConsumesTwiceAsManyFrames()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 40));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        player.Speed = 2.0;
        RunFrames(player, 5);

        // Five frames' worth of output audio at double speed covers ten frames of disc.
        Assert.InRange(player.CurrentFrame, 9, 11);
    }

    [Fact]
    public void HalfSpeedConsumesHalfAsManyFrames()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 40));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        player.Speed = 0.5;
        RunFrames(player, 8);

        Assert.InRange(player.CurrentFrame, 3, 5);
    }

    [Fact]
    public void SpeedIsClampedToWhatThePlayerSupports()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 4));
        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        player.Speed = 100;
        Assert.Equal(VideoNowPlayer.MaxSpeed, player.Speed);

        player.Speed = 0.001;
        Assert.Equal(VideoNowPlayer.MinSpeed, player.Speed);
    }

    [Fact]
    public void RenderedSampleCountAlwaysMatchesTheRequest()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 3));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        var buffer = new short[777];
        for (var i = 0; i < 20; i++) Assert.Equal(buffer.Length, player.RenderAudio(buffer));
    }
}

public class PlayerBranchTests
{
    private static void RunToNextTrack(VideoNowPlayer player, int maxFrames = 200)
    {
        var buffer = new short[player.Layout.AudioBytes];
        var start = player.CurrentTrack;

        for (var i = 0; i < maxFrames && player.CurrentTrack == start; i++)
        {
            if (player.State != TransportState.Playing) break;
            player.RenderAudio(buffer);
        }
    }

    [Fact]
    public void ReadsTheBranchTableOffTheSegment()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Choice, Branches: [3, 4]),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2),
            new TrackSpec(4, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.True(player.IsChoicePoint);
        Assert.Equal([3, 4], player.Branches.Select(b => b.Track));
    }

    [Fact]
    public void TakesTheChosenBranchWhenTheSegmentEnds()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Choice, Branches: [3, 4]),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2),
            new TrackSpec(4, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        Assert.True(player.PressChoice(1));
        RunToNextTrack(player);

        Assert.Equal(4, player.CurrentTrack);
    }

    [Fact]
    public void RejectsABranchTheSegmentDoesNotOffer()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Choice, Branches: [3]),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.False(player.PressChoice(4));
        Assert.Equal(-1, player.SelectedChoice);
    }

    [Fact]
    public void TakingABranchImmediatelySkipsTheRestOfTheScene()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 40, SegmentKind.Choice, Branches: [3, 4]),
            new TrackSpec(3, 2),
            new TrackSpec(4, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        Assert.True(player.TakeChoiceNow(1));
        Assert.Equal(4, player.CurrentTrack);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Theory]
    [InlineData(ChoiceTimeout.FirstBranch, 3)]
    [InlineData(ChoiceTimeout.DiscOrder, 2)]
    public void TimeoutBehaviourDecidesWhereAnUnansweredChoiceGoes(ChoiceTimeout timeout, int expected)
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Choice, Branches: [3, 4]),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2),
            new TrackSpec(4, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath)) { Timeout = timeout };
        player.Play();
        RunToNextTrack(player);

        Assert.Equal(expected, player.CurrentTrack);
    }

    [Fact]
    public void WaitingAtAChoicePointPausesInsteadOfMovingOn()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Choice, Branches: [3]),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath)) { Timeout = ChoiceTimeout.Wait };
        player.Play();

        var buffer = new short[player.Layout.AudioBytes];
        for (var i = 0; i < 10; i++) player.RenderAudio(buffer);

        Assert.Equal(1, player.CurrentTrack);
        Assert.Equal(TransportState.Paused, player.State);
    }

    [Fact]
    public void TaggedChoicesPutTheDestinationInTheSecondByte()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.TaggedChoice, Branches: [3, 4]),
            new TrackSpec(3, 2),
            new TrackSpec(4, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.Equal([3, 4], player.Branches.Select(b => b.Track));
        Assert.All(player.Branches, b => Assert.Equal(0x6D, b.Tag));
    }

    [Fact]
    public void ScoringSegmentsPlayOnRatherThanStopping()
    {
        // Kind 4 was once read as "stop here", but every such segment on the retail discs
        // is a right-answer clip that the quiz carries on from.
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.ScoreUp),
            new TrackSpec(2, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        RunToNextTrack(player);

        Assert.Equal(TransportState.Playing, player.State);
        Assert.Equal(2, player.CurrentTrack);
        Assert.Equal(VideoNowPlayer.InitialScore + 1, player.Score);
    }

    [Theory]
    [InlineData(SegmentKind.Linear)]
    [InlineData(SegmentKind.ScoreDown)]
    [InlineData(SegmentKind.ScoreReset)]
    public void TheContinuePointerNamesTheNextTrack(SegmentKind kind)
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, kind, ContinueTrack: 3),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        RunToNextTrack(player);

        Assert.Equal(3, player.CurrentTrack);
    }

    [Fact]
    public void DiscOrderNavigationIgnoresTheContinuePointer()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Linear, ContinueTrack: 3),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath))
        {
            Navigation = NavigationPolicy.DiscOrder,
        };

        player.Play();
        RunToNextTrack(player);

        Assert.Equal(2, player.CurrentTrack);
    }

    /// <summary>
    /// Teen Titans cuts each missed prompt as a segment whose 0x4F names the next scene
    /// (4 → 10 → 5 → 11 → 6 ...). Read in disc order, the last of those clips pointed
    /// back to the first scene and the title looped for ever.
    /// </summary>
    [Fact]
    public void MissedPromptsCarryOnToTheNextSceneInsteadOfLooping()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, ContinueTrack: 4),
            new TrackSpec(2, 2, ContinueTrack: 5),
            new TrackSpec(3, 2, ContinueTrack: 6),
            new TrackSpec(4, 2, SegmentKind.ScoreDown, ContinueTrack: 2),
            new TrackSpec(5, 2, SegmentKind.ScoreDown, ContinueTrack: 3),
            new TrackSpec(6, 2, SegmentKind.ScoreDown, ContinueTrack: 7),
            new TrackSpec(7, 2, SegmentKind.Choice, Branches: [7]));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        var visited = new List<int> { player.CurrentTrack };
        for (var i = 0; i < 6; i++)
        {
            RunToNextTrack(player);
            visited.Add(player.CurrentTrack);
        }

        Assert.Equal([1, 4, 2, 5, 3, 6, 7], visited);
        Assert.Equal(VideoNowPlayer.InitialScore - 3, player.Score);
    }

    [Fact]
    public void ASegmentNamingItselfRepeatsUntilTheViewerActs()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 3, ContinueTrack: 1, Branches: [0, 0, 2]),
            new TrackSpec(2, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        var buffer = new short[player.Layout.AudioBytes];
        for (var i = 0; i < 10; i++) player.RenderAudio(buffer);
        Assert.Equal(1, player.CurrentTrack);
        Assert.True(player.IsChoicePoint);

        Assert.True(player.PressChoice(2));
        RunToNextTrack(player);
        Assert.Equal(2, player.CurrentTrack);
    }

    [Fact]
    public void ABranchPlaysTheTracksQueuedBehindIt()
    {
        // Quiz questions are written this way: an answer plays its "right" or "wrong"
        // clip, then the entry's next byte names the following question.
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Choice, PlayLists: [[3, 5]]),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2, SegmentKind.ScoreUp),
            new TrackSpec(4, 2),
            new TrackSpec(5, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        Assert.True(player.PressChoice(0));

        RunToNextTrack(player);
        Assert.Equal(3, player.CurrentTrack);
        Assert.Equal([5], player.FollowOn);

        RunToNextTrack(player);
        Assert.Equal(5, player.CurrentTrack);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 3)]
    [InlineData(3, 2)]
    public void TaggedSegmentsBranchOnTheScore(int rightAnswers, int expected)
    {
        // Tracks 5-7 are three questions' clips, right ones adding to the score; track 8
        // sends the viewer to one of three endings by how many were right.
        var tracks = new List<TrackSpec>
        {
            new(1, 2, ContinueTrack: 5),
            new(2, 2),
            new(3, 2),
            new(4, 2),
        };

        for (var i = 0; i < 3; i++)
            tracks.Add(new TrackSpec(5 + i, 2, i < rightAnswers ? SegmentKind.ScoreUp : SegmentKind.Linear));

        tracks.Add(new TrackSpec(8, 2, SegmentKind.TaggedChoice, Branches: [2, 3, 4], Thresholds: [0x67, 0x65, 0x64]));

        using var disc = SyntheticDiscFile.Create([.. tracks]);
        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        for (var i = 0; i < 5 && player.CurrentTrack != 8; i++) RunToNextTrack(player);
        Assert.Equal(8, player.CurrentTrack);

        RunToNextTrack(player);
        Assert.Equal(expected, player.CurrentTrack);
    }

    [Fact]
    public void AScoreResetPutsTheScoreBack()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.ScoreUp),
            new TrackSpec(2, 2, SegmentKind.ScoreUp),
            new TrackSpec(3, 2, SegmentKind.ScoreReset),
            new TrackSpec(4, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        // A segment's effect lands as its first frame plays.
        RunToNextTrack(player);
        Assert.Equal(2, player.CurrentTrack);
        Assert.Equal(VideoNowPlayer.InitialScore + 2, player.Score);

        RunToNextTrack(player);
        Assert.Equal(3, player.CurrentTrack);
        Assert.Equal(VideoNowPlayer.InitialScore, player.Score);
    }

    /// <summary>
    /// Batman's quick-time scenes offer the next scene under one key for a dozen frames,
    /// then send every key to the failure clip; 0x4F names the same clip for no press.
    /// </summary>
    [Theory]
    [InlineData(-1, 3)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    public void TimedPromptsAnswerFromTheFrameOnScreen(int pressAtFrame, int expected)
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 6, ContinueTrack: 3, Prompts: [(2, 3, [2, 3]), (4, 5, [3, 3])]),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();

        var buffer = new short[player.Layout.AudioBytes];
        for (var frame = 0; player.CurrentTrack == 1 && frame < 20; frame++)
        {
            player.RenderAudio(buffer);
            if (frame == pressAtFrame)
            {
                Assert.True(player.IsChoicePoint);
                Assert.True(player.PressChoice(0));
            }
        }

        Assert.Equal(expected, player.CurrentTrack);
    }

    [Fact]
    public void TheStandingMenuButtonIsNotAChoicePoint()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, Branches: [0, 0, 0, 0, 0, 2]),
            new TrackSpec(2, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.False(player.IsChoicePoint);
        Assert.True(player.PressChoice(5));
    }

    [Fact]
    public void GoingBackReturnsToThePreviousSegment()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.Play();
        RunToNextTrack(player);
        Assert.Equal(2, player.CurrentTrack);

        Assert.True(player.GoBack());
        Assert.Equal(1, player.CurrentTrack);
    }

    [Fact]
    public void GoingBackTwiceUnwindsTwoSegments()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));
        player.SelectTrack(2);
        player.SelectTrack(3);

        Assert.True(player.GoBack());
        Assert.Equal(2, player.CurrentTrack);

        Assert.True(player.GoBack());
        Assert.Equal(1, player.CurrentTrack);
    }

    [Fact]
    public void GoingBackWithNoHistoryDoesNothing()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 2));
        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.False(player.GoBack());
    }

    [Fact]
    public void LoopingATrackNeverLeavesIt()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath)) { Loop = LoopMode.Track };
        player.Play();

        var buffer = new short[player.Layout.AudioBytes];
        for (var i = 0; i < 20; i++) player.RenderAudio(buffer);

        Assert.Equal(1, player.CurrentTrack);
        Assert.Equal(TransportState.Playing, player.State);
    }

    [Fact]
    public void LoopingTheDiscReturnsToTheFirstTrack()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2));

        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath)) { Loop = LoopMode.Disc };
        player.Play();

        var buffer = new short[player.Layout.AudioBytes];
        for (var i = 0; i < 12; i++) player.RenderAudio(buffer);

        Assert.Equal(TransportState.Playing, player.State);
    }
}

public class DiscMapTests
{
    [Fact]
    public void SurveysEveryTrackIncludingPadding()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 4, Title: "01intro.vn5"),
            new TrackSpec(2, 6, SegmentKind.Choice, Branches: [3, 4]),
            new TrackSpec(3, 2),
            new TrackSpec(4, 2),
            new TrackSpec(5, 0, Empty: true));

        using var image = DiscImage.Open(disc.CuePath);
        var map = DiscMap.Build(image);

        Assert.Equal(5, map.Tracks.Count);
        Assert.Equal(4, map.PlayableTracks.Count());
        Assert.True(map.IsInteractive);
        Assert.Equal("01intro.vn5", map.Find(1)!.Title);
        Assert.False(map.Find(5)!.HasVideo);
    }

    [Fact]
    public void FrameCountsAgreeWithWhatTheDiscDeclares()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 7));

        using var image = DiscImage.Open(disc.CuePath);
        var track = DiscMap.Build(image).Find(1)!;

        Assert.Equal(7, track.FrameCount);
        Assert.Equal(7, track.DeclaredFrameCount);
        Assert.False(track.FrameCountMismatch);
    }

    [Fact]
    public void SuccessorsCoverBranchesAndDiscOrder()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2),
            new TrackSpec(2, 2, SegmentKind.Choice, Branches: [4, 5]),
            new TrackSpec(3, 2),
            new TrackSpec(4, 2),
            new TrackSpec(5, 2, SegmentKind.ScoreUp));

        using var image = DiscImage.Open(disc.CuePath);
        var map = DiscMap.Build(image);

        Assert.Equal([2], map.Successors(1));
        Assert.Equal([4, 5], map.Successors(2).Order());
        Assert.Empty(map.Successors(5));
    }

    [Fact]
    public void ReachabilityFollowsTheBranchGraph()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, SegmentKind.Choice, Branches: [3]),
            new TrackSpec(2, 2, SegmentKind.ScoreUp),
            new TrackSpec(3, 2, SegmentKind.ScoreUp));

        using var image = DiscImage.Open(disc.CuePath);
        var map = DiscMap.Build(image);

        // Track 2 is only reachable in disc order, which a choice point does not use.
        Assert.Equal([1, 3], map.Reachable(1));
    }

    [Fact]
    public void SuccessorsFollowTheContinuePointerAndQueuedTracks()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, ContinueTrack: 3),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2, SegmentKind.Choice, PlayLists: [[4, 6]]),
            new TrackSpec(4, 2),
            new TrackSpec(5, 2),
            new TrackSpec(6, 2));

        using var image = DiscImage.Open(disc.CuePath);
        var map = DiscMap.Build(image);

        Assert.Equal([3], map.Successors(1));
        Assert.Equal([6], map.Successors(4));
        Assert.Equal([1, 3, 4, 6], map.Reachable(1));
        Assert.Equal([4, 6], map.Find(3)!.Branches.Single().PlayList);
    }

    [Fact]
    public void ASurveyOfEveryFrameFindsTimedPrompts()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 6, ContinueTrack: 3, Prompts: [(2, 3, [2, 3])]),
            new TrackSpec(2, 2),
            new TrackSpec(3, 2));

        using var image = DiscImage.Open(disc.CuePath);

        Assert.Empty(DiscMap.Build(image).Find(1)!.Branches);
        Assert.Equal([2, 3], DiscMap.Build(image, everyFrame: true).Successors(1).Order());
    }

    [Fact]
    public void EqualLengthRunsAreReportedAsParallelTakes()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 9),
            new TrackSpec(2, 4),
            new TrackSpec(3, 4),
            new TrackSpec(4, 4),
            new TrackSpec(5, 7));

        using var image = DiscImage.Open(disc.CuePath);
        var groups = DiscMap.Build(image).ParallelGroups();

        Assert.Contains(groups, g => g.SequenceEqual([2, 3, 4]));
    }
}
