using Vxp.Discs;
using Vxp.Format;
using Xunit;

namespace Vxp.Tests;

public class FrameLayoutTests
{
    [Fact]
    public void XpFrameSplitsIntoVideoAndAudioExactly()
    {
        var layout = FrameLayout.Xp;

        Assert.Equal(19760, layout.StreamBytes);
        Assert.Equal(17784, layout.VideoBytes);
        Assert.Equal(1976, layout.AudioBytes);
        Assert.Equal(layout.StreamBytes, layout.VideoBytes + layout.AudioBytes);
    }

    [Fact]
    public void HeaderPlusPixelDataFillsTheVideoHalf()
    {
        Assert.Equal(FrameLayout.Xp.VideoBytes, FrameLayout.Xp.HeaderBytes + FrameLayout.PixelBytes);
        Assert.Equal(FrameLayout.Color.VideoBytes, FrameLayout.Color.HeaderBytes + FrameLayout.PixelBytes);
    }

    [Fact]
    public void AudioIsOneTenthOfTheDiscByteRate()
    {
        Assert.Equal(17640, FrameLayout.AudioBytesPerSecond);
        Assert.Equal(FrameLayout.DiscBytesPerSecond / 10, FrameLayout.AudioBytesPerSecond);
    }

    [Theory]
    [InlineData(1976, 17640)] // XP
    [InlineData(1960, 17640)] // Color
    public void TheStreamFrameRateFollowsFromTheAudioBytes(int audioBytesPerFrame, int bytesPerSecond)
    {
        var layout = audioBytesPerFrame == 1976 ? FrameLayout.Xp : FrameLayout.Color;
        Assert.Equal(bytesPerSecond / (double)audioBytesPerFrame, layout.StreamFrameRate, 6);
    }

    [Fact]
    public void XpPlaysAtTwiceCdSpeed()
    {
        Assert.Equal(2, FrameLayout.Xp.DiscSpeed);
        Assert.Equal(35280, FrameLayout.Xp.PlaybackSampleRate);
        Assert.Equal(17.854, FrameLayout.Xp.PlaybackFrameRate, 3);
        Assert.Equal(2 * FrameLayout.Xp.StreamFrameRate, FrameLayout.Xp.PlaybackFrameRate, 6);
    }

    [Fact]
    public void ColorStaysAtOneTimesUntilADiscSaysOtherwise()
    {
        Assert.Equal(1, FrameLayout.Color.DiscSpeed);
        Assert.Equal(17640, FrameLayout.Color.PlaybackSampleRate);
        Assert.Equal(9.0, FrameLayout.Color.PlaybackFrameRate, 6);
    }

    [Fact]
    public void AFrameLastsItsSamplesAtThePlaybackRate()
    {
        // 1976 samples at 35 280 Hz: 56.009 ms, not the 112.018 ms of one-times speed.
        Assert.Equal(1976 / 35280.0, FrameLayout.Xp.FrameDuration.TotalSeconds, 6);
        Assert.Equal(1960 / 17640.0, FrameLayout.Color.FrameDuration.TotalSeconds, 6);
    }

    [Fact]
    public void PackedRowStrideIsHalfADisplayedRow()
    {
        // Two 108-byte half-rows make one 144-pixel row of three 4-bit channels.
        Assert.Equal(108, FrameLayout.PixelRowStride);
        Assert.Equal(FrameLayout.Width * 3, FrameLayout.PixelRowStride * 2 * 2);
    }
}

public class DeinterleaveTests
{
    [Fact]
    public void SplitsNineVideoBytesFromEveryTenth()
    {
        var layout = FrameLayout.Xp;
        var stream = new byte[layout.StreamBytes];
        for (var i = 0; i < stream.Length; i++) stream[i] = (byte)(i % 10 == 9 ? 0xAA : i % 251);

        var video = new byte[layout.VideoBytes];
        var audio = new byte[layout.AudioBytes];
        TrackReader.Deinterleave(stream, video, audio);

        Assert.All(audio, b => Assert.Equal(0xAA, b));

        // Every video byte must come from a stream position that is not a tenth byte,
        // and they must stay in order across the group boundary.
        for (var i = 0; i < video.Length; i++)
        {
            var source = i / 9 * 10 + i % 9;
            Assert.Equal(stream[source], video[i]);
        }
    }
}

public class CueSheetTests
{
    [Fact]
    public void ParsesTracksTitlesAndIndices()
    {
        var tracks = CueSheet.Parse([
            "FILE \"disc (Track 01).bin\" BINARY",
            "  TRACK 01 AUDIO",
            "    TITLE \"01logo.vn5\"",
            "    FLAGS DCP",
            "    INDEX 01 00:00:00",
            "FILE \"disc (Track 02).bin\" BINARY",
            "  TRACK 02 AUDIO",
            "    TITLE \"02-titles.vn5\"",
            "    INDEX 01 00:00:00",
        ]);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(1, tracks[0].Number);
        Assert.Equal("01logo.vn5", tracks[0].Title);
        Assert.Equal("disc (Track 02).bin", tracks[1].FileName);
        Assert.Equal("AUDIO", tracks[1].DataType);
    }

    [Fact]
    public void ParsesSingleFileImagesWithAbsoluteIndices()
    {
        var tracks = CueSheet.Parse([
            "FILE \"disc.bin\" BINARY",
            "  TRACK 01 AUDIO",
            "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO",
            "    INDEX 00 00:11:37",
            "    INDEX 01 00:12:00",
        ]);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(0, tracks[0].IndexOneSector);
        Assert.Equal(11 * 75 + 37, tracks[1].IndexZeroSector);
        Assert.Equal(12 * 75, tracks[1].IndexOneSector);
        Assert.All(tracks, t => Assert.Equal("disc.bin", t.FileName));
    }

    [Theory]
    [InlineData("00:00:00", 0)]
    [InlineData("00:02:00", 150)]
    [InlineData("01:00:00", 4500)]
    [InlineData("74:00:00", 333000)]
    public void ConvertsMsfToSectors(string msf, int expected)
        => Assert.Equal(expected, CueSheet.ParseMsf(msf));

    [Fact]
    public void RejectsACueSheetWithNoTracks()
        => Assert.Throws<InvalidDataException>(() => CueSheet.Parse(["REM nothing here"]));
}

public class FormatDetectorTests
{
    [Fact]
    public void RecognisesXpFromTwelveSyncWords()
    {
        var frame = SyntheticDisc.BuildFrame(
            FrameLayout.Xp,
            new Dictionary<int, byte>(),
            SyntheticDisc.GradientPixels(),
            []);

        var layout = FormatDetector.Detect(frame);

        Assert.NotNull(layout);
        Assert.Equal(DiscFormat.Xp, layout!.Format);
    }

    [Fact]
    public void RecognisesColorFromTwentyFourSyncWords()
    {
        var frame = SyntheticDisc.BuildFrame(
            FrameLayout.Color,
            new Dictionary<int, byte>(),
            SyntheticDisc.GradientPixels(),
            []);

        var layout = FormatDetector.Detect(frame);

        Assert.NotNull(layout);
        Assert.Equal(DiscFormat.Color, layout!.Format);
    }

    [Fact]
    public void RejectsDataWithNoSyncWord()
    {
        var noise = new byte[FrameLayout.Xp.StreamBytes];
        Random.Shared.NextBytes(noise);
        for (var i = 0; i < noise.Length; i++) noise[i] &= 0x3F; // keep sync bytes impossible

        Assert.Null(FormatDetector.Detect(noise));
    }
}

public class VideoDecoderTests
{
    [Fact]
    public void ProducesAFullyOpaqueFrameOfTheRightSize()
    {
        var rgba = VideoDecoder.DecodeRgba(SyntheticDisc.GradientPixels());

        Assert.Equal(FrameLayout.Width * FrameLayout.Height * 4, rgba.Length);
        for (var i = 3; i < rgba.Length; i += 4) Assert.Equal(0xFF, rgba[i]);
    }

    [Fact]
    public void NibblesExpandSoFullScaleReachesWhite()
    {
        var pixels = new byte[FrameLayout.PixelBytes];
        Array.Fill(pixels, (byte)0xFF);

        var rgba = VideoDecoder.DecodeRgba(pixels);

        Assert.All(rgba, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ZeroSamplesDecodeToBlack()
    {
        var rgba = VideoDecoder.DecodeRgba(new byte[FrameLayout.PixelBytes]);

        for (var i = 0; i < rgba.Length; i += 4)
        {
            Assert.Equal(0x00, rgba[i]);
            Assert.Equal(0x00, rgba[i + 1]);
            Assert.Equal(0x00, rgba[i + 2]);
        }
    }

    [Fact]
    public void FirstPixelTakesItsChannelsFromBothHalfRows()
    {
        // Pixel 0 is (a0.lo, b0.lo, b0.hi), where a0 is the first byte of the upper
        // half-row and b0 the first byte of the lower one.
        var pixels = new byte[FrameLayout.PixelBytes];
        pixels[0] = 0x03;                              // a0: low nibble 3
        pixels[FrameLayout.PixelRowStride] = 0x51;     // b0: low nibble 1, high nibble 5

        var rgba = VideoDecoder.DecodeRgba(pixels);

        Assert.Equal(0x33, rgba[0]);
        Assert.Equal(0x11, rgba[1]);
        Assert.Equal(0x55, rgba[2]);
    }

    [Fact]
    public void ChannelOrderRemapsOutputChannels()
    {
        var pixels = new byte[FrameLayout.PixelBytes];
        pixels[0] = 0x03;
        pixels[FrameLayout.PixelRowStride] = 0x51;

        var rgba = new byte[VideoDecoder.RgbaFrameBytes];
        VideoDecoder.DecodeRgba(pixels, rgba, ChannelOrder.Parse("GBR"));

        // Sample 0 now drives green, sample 1 blue and sample 2 red.
        Assert.Equal(0x55, rgba[0]);
        Assert.Equal(0x33, rgba[1]);
        Assert.Equal(0x11, rgba[2]);
    }
}

public class AudioDecoderTests
{
    [Fact]
    public void SilenceSitsAtMidScale()
    {
        var pcm = new short[4];
        AudioDecoder.DecodePcm16([0x80, 0x80, 0x80, 0x80], pcm);

        Assert.All(pcm, s => Assert.Equal(0, s));
    }

    [Fact]
    public void ExtremesMapToNearFullScale()
    {
        var pcm = new short[2];
        AudioDecoder.DecodePcm16([0x00, 0xFF], pcm);

        Assert.Equal(-32768, pcm[0]);
        Assert.Equal(32512, pcm[1]);
    }

    [Fact]
    public void FloatOutputIsNormalised()
    {
        var samples = new float[3];
        AudioDecoder.DecodeFloat([0x00, 0x80, 0xFF], samples);

        Assert.Equal(-1f, samples[0], 3);
        Assert.Equal(0f, samples[1], 3);
        Assert.Equal(0.992f, samples[2], 3);
    }
}
