using Vxp.Discs;
using Vxp.Emulation;
using Vxp.Format;
using Xunit;

namespace Vxp.Tests;

public class BlackAndWhiteStreamTests
{
    private static byte[] Picture(Func<int, byte> fill)
    {
        var picture = new byte[3200];
        for (var i = 0; i < picture.Length; i++) picture[i] = fill(i);
        return picture;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void IsRecognisedByItsMarkersRatherThanASyncWord()
    {
        var stream = Concat(
            SyntheticBlackAndWhite.Padding(776),
            SyntheticBlackAndWhite.StampedFrame(1, 0),
            SyntheticBlackAndWhite.StampedFrame(1, 1));

        var layout = FormatDetector.Detect(stream);

        Assert.NotNull(layout);
        Assert.Equal(DiscFormat.BlackAndWhite, layout!.Format);
    }

    [Fact]
    public void AHeaderWithoutAWholePictureIsNotAFrame()
    {
        // Header, then only half a picture before the data runs out.
        var frame = SyntheticBlackAndWhite.StampedFrame(1, 0);
        var cut = frame.AsSpan(0, (BlackAndWhiteStream.HeaderGroups + 800) * BlackAndWhiteStream.GroupBytes);

        Assert.False(BlackAndWhiteStream.LooksLike(cut));
        Assert.Null(FormatDetector.Detect(cut));
    }

    [Fact]
    public void VideoBytesThatRepeatTheMarkerDoNotShiftTheGroupAlignment()
    {
        // Header video bytes equal the header marker, so a search that ignored alignment
        // could start a byte early. The picture run is what pins it down.
        var stream = Concat(SyntheticBlackAndWhite.Padding(10), SyntheticBlackAndWhite.StampedFrame(1, 0));

        var start = BlackAndWhiteStream.FindFrame(stream, 0, out var picture);

        Assert.Equal(10 * BlackAndWhiteStream.GroupBytes, start);
        Assert.Equal(start + BlackAndWhiteStream.HeaderGroups * BlackAndWhiteStream.GroupBytes, picture);
    }

    [Fact]
    public void PixelsAreHighNibbleFirstFromBlackToWhite()
    {
        var picture = Picture(i => i == 0 ? (byte)0x0F : i == 40 ? (byte)0x80 : (byte)0x00);
        var rgba = new byte[80 * 80 * 4];

        BlackAndWhiteStream.DecodeRgba(picture, rgba);

        // Byte 0 is pixels 0 and 1 of row 0: black then white.
        Assert.Equal([0x00, 0x00, 0x00, 0xFF], rgba[0..4]);
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], rgba[4..8]);

        // Byte 40 opens row 1: its high nibble, 8, is pixel (0, 1).
        var row1 = 80 * 4;
        Assert.Equal([0x88, 0x88, 0x88, 0xFF], rgba[row1..(row1 + 4)]);
    }

    [Fact]
    public void DeinterleaveSplitsVideoFromMarkerAndSoundAndSilencesPadding()
    {
        byte[] stream = [0x12, 0x34, 0x5A, 0x90, 0x00, 0x00, 0x00, 0x00];
        var video = new byte[4];
        var audio = new byte[2];

        BlackAndWhiteStream.Deinterleave(stream, video, audio);

        Assert.Equal([0x12, 0x34, 0x00, 0x00], video);
        Assert.Equal([0x90, 0x80], audio);
    }
}

public class BlackAndWhiteTrackTests
{
    [Fact]
    public void CountsOnlyFramesWhosePictureIsWhole()
    {
        // A track as the discs cut them: the tail of a frame from the previous track,
        // three whole frames, and the start of one that the next track finishes.
        var partialTail = SyntheticBlackAndWhite.StampedFrame(1, 9)[(2000 * BlackAndWhiteStream.GroupBytes)..];
        var partialHead = SyntheticBlackAndWhite.StampedFrame(1, 3)[..(1500 * BlackAndWhiteStream.GroupBytes)];

        using var file = SyntheticDiscFile.FromBytes(Concat(
            partialTail,
            SyntheticBlackAndWhite.StampedFrame(1, 0),
            SyntheticBlackAndWhite.StampedFrame(1, 1),
            SyntheticBlackAndWhite.StampedFrame(1, 2),
            partialHead));
        using var disc = DiscImage.Open(file.CuePath);

        var reader = new TrackReader(disc.Tracks[0], FrameLayout.BlackAndWhite);

        Assert.Equal(3, reader.FrameCount);
        Assert.Equal(partialTail.Length, reader.StartOffset);
        for (var i = 0; i < 3; i++)
            Assert.Equal((byte)i, (byte)(reader.ReadFrame(i)!.PixelData[0] & 0x0F));
    }

    [Fact]
    public void NoSoundIsLostAtTheEdgesOfATrack()
    {
        var lead = SyntheticBlackAndWhite.Padding(776);
        var partialHead = SyntheticBlackAndWhite.StampedFrame(1, 5)[..(1500 * BlackAndWhiteStream.GroupBytes)];
        var bytes = Concat(
            lead,
            SyntheticBlackAndWhite.StampedFrame(1, 0),
            SyntheticBlackAndWhite.StampedFrame(1, 1),
            partialHead);

        using var file = SyntheticDiscFile.FromBytes(bytes);
        using var disc = DiscImage.Open(file.CuePath);
        var reader = new TrackReader(disc.Tracks[0], FrameLayout.BlackAndWhite);

        var first = reader.ReadFrame(0)!;
        var last = reader.ReadFrame(1)!;

        // Every sample in the track is played once: the padding before the first frame as
        // silence, and the partial frame after the last as part of it.
        Assert.Equal(bytes.Length / BlackAndWhiteStream.GroupBytes, first.Audio.Length + last.Audio.Length);
        Assert.All(first.Audio.Take(776), s => Assert.Equal(0x80, s));
        Assert.All(first.Audio.Skip(776), s => Assert.Equal(0x80, s));
        Assert.All(last.Audio.Take(2940), s => Assert.Equal(0x81, s));
        Assert.All(last.Audio.Skip(2940), s => Assert.Equal(0x85, s));
    }

    [Fact]
    public void AShortHeaderDoesNotThrowTheFramesOut()
    {
        // Rugrats: All Growed Up part 1, track 7, opens with a header of 213 groups. A
        // reader that counted 2940 groups a frame from there would drift by 457 groups.
        var picture = new byte[3200];
        var audio = new byte[2940];
        picture[0] = 0x01;
        var shortOne = SyntheticBlackAndWhite.BuildFrame(picture, audio, headerGroups: 213);

        using var file = SyntheticDiscFile.FromBytes(Concat(
            SyntheticBlackAndWhite.StampedFrame(1, 0),
            shortOne,
            SyntheticBlackAndWhite.StampedFrame(1, 2),
            SyntheticBlackAndWhite.StampedFrame(1, 3),
            SyntheticBlackAndWhite.StampedFrame(1, 4)));
        using var disc = DiscImage.Open(file.CuePath);
        var reader = new TrackReader(disc.Tracks[0], FrameLayout.BlackAndWhite);

        Assert.Equal(5, reader.FrameCount);
        for (var i = 0; i < 5; i++)
            Assert.Equal((byte)i, (byte)(reader.ReadFrame(i)!.PixelData[0] & 0x0F));
    }

    [Fact]
    public void FramesHaveNoHeaderAndSoNoBranches()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 3, Layout: FrameLayout.BlackAndWhite));
        using var image = DiscImage.Open(disc.CuePath);
        var reader = new TrackReader(image.Tracks[0], FrameLayout.BlackAndWhite);

        var header = reader.ReadFrame(0)!.ReadHeader();

        Assert.Equal(SegmentKind.None, header.Kind);
        Assert.Empty(header.Branches);
        Assert.Equal(0, header.ContinueTrack);
        Assert.False(reader.IsBlank());
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}

public class BlackAndWhitePlayerTests
{
    [Fact]
    public void ADiscIsSurveyedAtFifteenFramesASecond()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 15, Layout: FrameLayout.BlackAndWhite),
            new TrackSpec(2, 30, Layout: FrameLayout.BlackAndWhite),
            new TrackSpec(3, 0, Empty: true));
        using var image = DiscImage.Open(disc.CuePath);

        var map = DiscMap.Build(image);

        Assert.Equal(DiscFormat.BlackAndWhite, map.Layout!.Format);
        Assert.Equal(1.0, map.Find(1)!.Duration.TotalSeconds, 4);
        Assert.Equal(3.0, map.TotalDuration.TotalSeconds, 4);
        Assert.False(map.Find(3)!.HasVideo);
    }

    [Fact]
    public void PlaysGreyPicturesThroughInDiscOrder()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 2, Layout: FrameLayout.BlackAndWhite),
            new TrackSpec(2, 2, Layout: FrameLayout.BlackAndWhite));
        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.Equal(80 * 80 * 4, player.Framebuffer.Length);
        Assert.Equal(44100, player.Layout.PlaybackSampleRate);

        player.Play();
        var buffer = new short[FrameLayout.BlackAndWhite.AudioBytes];
        for (var i = 0; i < 3; i++) player.RenderAudio(buffer);

        // Track 2, frame 0: the first pixel's grey is the track number, 2 (0x22 after
        // expansion), and grey means all three channels agree.
        Assert.Equal(2, player.CurrentTrack);
        Assert.Equal(0x22, player.Framebuffer[0]);
        Assert.Equal(player.Framebuffer[0], player.Framebuffer[1]);
        Assert.Equal(player.Framebuffer[0], player.Framebuffer[2]);
        Assert.Equal(1 / 15.0, player.Position.TotalSeconds, 4);
    }
}

public class MixedFormatDiscTests
{
    /// <summary>
    /// XP discs carry Color-format tracks: the logo on Thomas' Rescue Adventures, the
    /// end-of-disc clip on Aly &amp; AJ. Each is timed by its own frames, the same way
    /// in the survey and in the player.
    /// </summary>
    [Fact]
    public void AColorTrackOnAnXpDiscIsTimedTheSameEverywhere()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 18, Layout: FrameLayout.Color),
            new TrackSpec(2, 4),
            new TrackSpec(3, 2));
        using var image = DiscImage.Open(disc.CuePath);

        var map = DiscMap.Build(image);
        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.Equal(DiscFormat.Xp, map.Layout!.Format);
        Assert.Equal(DiscFormat.Color, map.Find(1)!.Format);
        Assert.Equal(1.0, map.Find(1)!.Duration.TotalSeconds, 4);

        Assert.Equal(1, player.CurrentTrack);
        Assert.Equal(DiscFormat.Color, player.TrackLayout.Format);
        Assert.Equal(map.Find(1)!.Duration, player.TrackDuration);
    }

    [Fact]
    public void AColorTrackPlaysOutInItsOwnRunningTime()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 18, Layout: FrameLayout.Color),
            new TrackSpec(2, 4),
            new TrackSpec(3, 4));
        using var player = new VideoNowPlayer(DiscImage.Open(disc.CuePath));

        Assert.Equal(DiscFormat.Xp, player.Layout.Format);
        player.Play();

        // One second of output at the disc's rate plays exactly the 18 Color frames.
        var second = new short[player.Layout.PlaybackSampleRate - 1];
        player.RenderAudio(second);
        Assert.Equal(1, player.CurrentTrack);
        Assert.Equal(18, player.CurrentFrame);

        player.RenderAudio(new short[2]);
        Assert.Equal(2, player.CurrentTrack);
    }
}
