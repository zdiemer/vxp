using Vxp.Format;
using Xunit;

namespace Vxp.Tests;

public class FrameHeaderTests
{
    private static FrameHeader Parse(Dictionary<int, byte> registers)
    {
        var stream = SyntheticDisc.BuildFrame(
            FrameLayout.Xp, registers, SyntheticDisc.GradientPixels(), []);

        var video = new byte[FrameLayout.Xp.VideoBytes];
        var audio = new byte[FrameLayout.Xp.AudioBytes];
        TrackReader.Deinterleave(stream, video, audio);

        return FrameHeader.Parse(video, FrameLayout.Xp);
    }

    [Fact]
    public void ReadsTrackNumberFrameIndexAndLength()
    {
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegTrackNumber] = 61,
            [FrameHeader.RegFrameIndexLow] = 0x2C,
            [FrameHeader.RegFrameIndexHigh] = 0x01,
            [FrameHeader.RegFrameCountLow] = 0xFC,
            [FrameHeader.RegFrameCountHigh] = 0x06,
        });

        Assert.Equal(61, header.TrackNumber);
        Assert.Equal(0x012C, header.FrameIndex);
        Assert.Equal(0x06FC, header.FrameCount);
    }

    [Fact]
    public void UnwrittenRegistersReadAsZero()
    {
        var header = Parse(new Dictionary<int, byte>());

        Assert.Equal(0, header[FrameHeader.RegTrackNumber]);
        Assert.Equal(SegmentKind.None, header.Kind);
        Assert.Empty(header.Branches);
    }

    [Fact]
    public void SkipsSyncWordsAndPadding()
    {
        // The parser must not mistake the repeated sync words, or the 0xFF group
        // separators, for register writes.
        var header = Parse(new Dictionary<int, byte> { [FrameHeader.RegSegmentKind] = 2 });

        Assert.Equal(SegmentKind.Choice, header.Kind);
        Assert.Equal(0, header[0xC7]);
        Assert.Equal(0, header[0xE3]);
        Assert.Equal(0, header[0x81]);
    }

    [Fact]
    public void ReadsAPlainBranchTable()
    {
        // Single-byte encoding: the branch register itself holds the destination track.
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegSegmentKind] = 2,
            [FrameHeader.RegBranchTableBase + 0 * FrameHeader.BranchEntryStride] = 13,
            [FrameHeader.RegBranchTableBase + 1 * FrameHeader.BranchEntryStride] = 32,
            [FrameHeader.RegBranchTableBase + 2 * FrameHeader.BranchEntryStride] = 49,
        });

        var branches = header.Branches;

        Assert.Equal(SegmentKind.Choice, header.Kind);
        Assert.Equal(3, branches.Count);
        Assert.Equal([13, 32, 49], branches.Select(b => b.Track));
        Assert.Equal([0, 1, 2], branches.Select(b => b.Slot));
        Assert.All(branches, b => Assert.Equal(0, b.Tag));
    }

    [Fact]
    public void ReadsATaggedBranchTable()
    {
        // Two-byte encoding: a selector tag then the destination track.
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegSegmentKind] = 3,
            [FrameHeader.RegBranchTableBase + 0] = 0x6D,
            [FrameHeader.RegBranchTableBase + 1] = 94,
            [FrameHeader.RegBranchTableBase + FrameHeader.BranchEntryStride] = 0x6B,
            [FrameHeader.RegBranchTableBase + FrameHeader.BranchEntryStride + 1] = 93,
        });

        var branches = header.Branches;

        Assert.Equal(SegmentKind.TaggedChoice, header.Kind);
        Assert.Equal(2, branches.Count);
        Assert.Equal(94, branches[0].Track);
        Assert.Equal(0x6D, branches[0].Tag);
        Assert.Equal(93, branches[1].Track);
        Assert.Equal(0x6B, branches[1].Tag);
    }

    [Fact]
    public void EmptyBranchSlotsAreSkippedButSlotNumbersArePreserved()
    {
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegBranchTableBase + 0 * FrameHeader.BranchEntryStride] = 13,
            [FrameHeader.RegBranchTableBase + 3 * FrameHeader.BranchEntryStride] = 26,
        });

        var branches = header.Branches;

        Assert.Equal(2, branches.Count);
        Assert.Equal(0, branches[0].Slot);
        Assert.Equal(3, branches[1].Slot);
        Assert.Equal(26, branches[1].Track);
    }

    [Fact]
    public void AValueOf0xFFIsNotMistakenForASeparator()
    {
        // Frame 255 of a track: the low byte of the frame index is 0xFF, and reading the
        // header as "skip every 0xFF" slipped a byte and lost the registers after it.
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegFrameIndexLow] = 0xFF,
            [FrameHeader.RegFrameIndexHigh] = 0x00,
            [FrameHeader.RegSegmentKind] = 1,
            [FrameHeader.RegTrackNumber] = 32,
        });

        Assert.Equal(255, header.FrameIndex);
        Assert.Equal(SegmentKind.Linear, header.Kind);
        Assert.Equal(32, header.TrackNumber);
    }

    [Fact]
    public void ABranchEntryIsAPlayList()
    {
        // From Batman track 27: key 5 plays 28, 29 and 30, then goes to 22.
        var register = FrameHeader.RegBranchTableBase + 4 * FrameHeader.BranchEntryStride;
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegSegmentKind] = 1,
            [register] = 28,
            [register + 1] = 29,
            [register + 2] = 30,
            [register + 3] = 22,
        });

        var branch = Assert.Single(header.Branches);
        Assert.Equal(4, branch.Slot);
        Assert.Equal(28, branch.Track);
        Assert.Equal([29, 30, 22], branch.FollowOn);
    }

    [Fact]
    public void ATaggedEntryOpensWithItsThreshold()
    {
        // From Teen Titans track 15: at a score of 100 or more, play 16 and then 17.
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegSegmentKind] = 3,
            [FrameHeader.RegBranchTableBase] = 0x64,
            [FrameHeader.RegBranchTableBase + 1] = 16,
            [FrameHeader.RegBranchTableBase + 2] = 17,
        });

        var branch = Assert.Single(header.Branches);
        Assert.Equal(0x64, branch.Tag);
        Assert.Equal([16, 17], branch.PlayList);
    }

    [Fact]
    public void ReadsTheContinuePointer()
    {
        var header = Parse(new Dictionary<int, byte>
        {
            [FrameHeader.RegSegmentKind] = 5,
            [FrameHeader.RegContinueTrack] = 62,
        });

        Assert.Equal(SegmentKind.ScoreDown, header.Kind);
        Assert.Equal(62, header.ContinueTrack);
    }
}

public class ChannelOrderTests
{
    [Fact]
    public void DefaultIsIdentity()
    {
        Assert.Equal(new ChannelOrder(0, 1, 2), ChannelOrder.Default);
        Assert.Equal(ChannelOrder.Default, ChannelOrder.Parse("RGB"));
    }

    [Fact]
    public void ParsesEveryPermutation()
    {
        foreach (var text in new[] { "RGB", "RBG", "GRB", "GBR", "BRG", "BGR" })
        {
            var order = ChannelOrder.Parse(text);
            Assert.Equal([0, 1, 2], new[] { order.Red, order.Green, order.Blue }.Order());
        }
    }

    [Theory]
    [InlineData("RG")]
    [InlineData("RGBA")]
    [InlineData("RGX")]
    public void RejectsMalformedOrders(string text)
        => Assert.Throws<FormatException>(() => ChannelOrder.Parse(text));
}
