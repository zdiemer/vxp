using System.IO.Compression;
using Vxp.Discs;
using Vxp.Emulation;
using Xunit;

namespace Vxp.Tests;

public class ArchiveTests
{
    private static byte[] ReadAll(DiscTrack track)
    {
        var bytes = new byte[track.ByteLength];
        Assert.Equal(bytes.Length, track.Read(0, bytes));
        return bytes;
    }

    [Fact]
    public void AZipMountsTheSameDiscAsItsCueSheet()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 3, Title: "01intro.vn5"),
            new TrackSpec(2, 5),
            new TrackSpec(3, 0, Empty: true));

        var zip = disc.Zip();

        using var loose = DiscImage.Open(disc.CuePath);
        using var zipped = DiscImage.Open(zip);

        Assert.True(zipped.IsArchive);
        Assert.Equal("disc", zipped.Name);
        Assert.Equal(loose.Tracks.Count, zipped.Tracks.Count);

        for (var i = 0; i < loose.Tracks.Count; i++)
        {
            Assert.Equal(loose.Tracks[i].Number, zipped.Tracks[i].Number);
            Assert.Equal(loose.Tracks[i].Title, zipped.Tracks[i].Title);
            Assert.Equal(loose.Tracks[i].ByteLength, zipped.Tracks[i].ByteLength);
            Assert.Equal(ReadAll(loose.Tracks[i]), ReadAll(zipped.Tracks[i]));
        }
    }

    [Fact]
    public void ReadsSeekBackwardsInsideACompressedTrack()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 6));
        using var loose = DiscImage.Open(disc.CuePath);
        using var zipped = DiscImage.Open(disc.Zip());

        var track = zipped.Tracks[0];
        var expected = ReadAll(loose.Tracks[0]);

        var tail = new byte[64];
        var head = new byte[64];
        zipped.Tracks[0].Read(track.ByteLength - tail.Length, tail);
        zipped.Tracks[0].Read(0, head);

        Assert.Equal(expected[^64..], tail);
        Assert.Equal(expected[..64], head);
    }

    [Fact]
    public void PartialReadsInterleavedAcrossTracksMatchTheLooseDisc()
    {
        // Big enough that every entry needs many inflate passes, so tracks are left
        // half-decompressed while others are read.
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 60), new TrackSpec(2, 60), new TrackSpec(3, 60));
        using var loose = DiscImage.Open(disc.CuePath);
        using var zipped = DiscImage.Open(disc.Zip());

        var expected = loose.Tracks.Select(ReadAll).ToArray();
        var offsets = new long[] { 0, 30_000, 5_000, 900_000, 100, 1_150_000, 400_000 };

        foreach (var offset in offsets)
        {
            for (var t = 0; t < zipped.Tracks.Count; t++)
            {
                var chunk = new byte[20_000];
                var read = zipped.Tracks[t].Read(offset, chunk);
                var want = expected[t].AsSpan((int)Math.Min(offset, expected[t].Length)).ToArray();

                Assert.Equal(Math.Min(chunk.Length, want.Length), read);
                Assert.Equal(want[..read], chunk[..read]);
            }
        }
    }

    [Fact]
    public void PlaysFromAZip()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 2), new TrackSpec(2, 2));
        using var player = new VideoNowPlayer(DiscImage.Open(disc.Zip()));

        player.Disc.PrecacheInBackground();
        player.Play();

        var buffer = new short[player.Layout.AudioBytes];
        for (var i = 0; i < 3; i++) player.RenderAudio(buffer);

        Assert.Equal(2, player.CurrentTrack);
    }

    [Fact]
    public void DisposingWhilePrecachingIsSafe()
    {
        using var disc = SyntheticDiscFile.Create(
            new TrackSpec(1, 40), new TrackSpec(2, 40), new TrackSpec(3, 40), new TrackSpec(4, 40));

        var image = DiscImage.Open(disc.Zip());
        image.PrecacheInBackground();
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => image.Tracks[0].Read(0, new byte[16]));
    }

    [Fact]
    public void AZipWithNoCueSheetIsReported()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 2));
        var zip = Path.Combine(disc.Directory, "nocue.zip");

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(Path.Combine(disc.Directory, "disc (Track 01).bin"), "disc (Track 01).bin");

        var error = Assert.Throws<InvalidDataException>(() => DiscImage.Open(zip));
        Assert.Contains("No cue sheet", error.Message);
    }

    [Fact]
    public void AMissingTrackInsideTheZipIsReported()
    {
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 2), new TrackSpec(2, 2));
        var zip = Path.Combine(disc.Directory, "short.zip");

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(disc.CuePath, "disc.cue");
            archive.CreateEntryFromFile(Path.Combine(disc.Directory, "disc (Track 01).bin"), "disc (Track 01).bin");
        }

        Assert.Throws<FileNotFoundException>(() => DiscImage.Open(zip));
    }

    [Fact]
    public void ABackslashInACueSheetFileNameIsAFolder()
    {
        // Cue sheets come from Windows tools, but are opened on Linux and macOS too,
        // where a backslash is an ordinary file name character.
        using var disc = SyntheticDiscFile.Create(new TrackSpec(1, 3));
        using var loose = DiscImage.Open(disc.CuePath);
        var expected = ReadAll(loose.Tracks[0]);

        var folder = Path.Combine(disc.Directory, "tracks");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(disc.Directory, "disc (Track 01).bin"), Path.Combine(folder, "disc (Track 01).bin"));

        var cue = Path.Combine(disc.Directory, "nested.cue");
        File.WriteAllText(cue, File.ReadAllText(disc.CuePath).Replace("\"disc (Track 01).bin\"", "\"tracks\\disc (Track 01).bin\""));

        using var nested = DiscImage.Open(cue);
        Assert.Equal(expected, ReadAll(nested.Tracks[0]));
    }
}
