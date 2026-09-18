using System.Buffers.Binary;
using Vxp.Cli;
using Vxp.Format;
using Xunit;

namespace Vxp.Tests;

/// <summary>
/// Everything that writes or reports time does it at the rate the disc plays at, twice CD
/// speed on XP, and not at the one-times rate the byte stream works out to.
/// </summary>
public class PlaybackRateTests
{
    private static (int SampleRate, int DataBytes) ReadWavHeader(string path)
    {
        var header = File.ReadAllBytes(path).AsSpan(0, 44);
        return (BinaryPrimitives.ReadInt32LittleEndian(header[24..]), BinaryPrimitives.ReadInt32LittleEndian(header[40..]));
    }

    [Fact]
    public void AnExportedSoundtrackIsWrittenAtThePlaybackRate()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 4), new TrackSpec(2, 4));
        var wav = Path.Combine(disc.Directory, "out.wav");

        Assert.Equal(0, MediaCommands.Audio(CommandLine.Parse([disc.CuePath, "--out", wav])));

        var (rate, bytes) = ReadWavHeader(wav);
        Assert.Equal(FrameLayout.Xp.PlaybackSampleRate, rate);
        Assert.Equal(35280, rate);
        Assert.Equal(8 * FrameLayout.Xp.AudioBytes * sizeof(short), bytes);
    }

    [Fact]
    public void AFrameExportsSoundtrackIsWrittenAtThePlaybackRate()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 3));
        var outDir = Path.Combine(disc.Directory, "frames");

        Assert.Equal(0, MediaCommands.Export(CommandLine.Parse([disc.CuePath, "--out", outDir, "--no-video"])));

        Assert.Equal(35280, ReadWavHeader(Path.Combine(outDir, "audio.wav")).SampleRate);
    }

    [Fact]
    public void ASessionRecordingIsWrittenAtThePlaybackRate()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 4));
        var wav = Path.Combine(disc.Directory, "session.wav");

        Assert.Equal(0, SessionCommand.Run(CommandLine.Parse([disc.CuePath, "--commands", "play all", "--wav", wav])));

        Assert.Equal(35280, ReadWavHeader(wav).SampleRate);
    }

    [Fact]
    public void PlayingForSomeSecondsCountsThemAtThePlaybackRate()
    {
        // One second at 17.854 fps is 18 frames; at the old one-times rate it was 9.
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 40));
        var commands = "play 1s; expect frame 18";

        Assert.Equal(0, SessionCommand.Run(CommandLine.Parse([disc.CuePath, "--commands", commands])));
    }
}
