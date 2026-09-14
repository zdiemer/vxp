using Vxp.Cli;
using Vxp.Config;
using Vxp.Emulation;
using Vxp.Format;
using Vxp.Video;
using Xunit;

namespace Vxp.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void EveryOptionIsReachableByPath()
    {
        var settings = new VxpSettings();
        var paths = SettingsStore.Enumerate(settings).Select(e => e.Path).ToArray();

        Assert.Contains("video.brightness", paths);
        Assert.Contains("audio.volume", paths);
        Assert.Contains("emulation.speedPercent", paths);
        Assert.Contains("interface.overlay", paths);
    }

    [Fact]
    public void PathsAreUnique()
    {
        var paths = SettingsStore.Enumerate(new VxpSettings()).Select(e => e.Path).ToArray();
        Assert.Equal(paths.Length, paths.Distinct().Count());
    }

    [Fact]
    public void LookupIgnoresCapitalisation()
    {
        var settings = new VxpSettings();
        Assert.NotNull(SettingsStore.Find(settings, "VIDEO.BRIGHTNESS"));
    }

    [Fact]
    public void UnknownPathsReturnNothing()
        => Assert.Null(SettingsStore.Find(new VxpSettings(), "video.nonsense"));

    [Theory]
    [InlineData("video.brightness", "40", "40")]
    [InlineData("audio.muted", "true", "true")]
    [InlineData("audio.muted", "yes", "true")]
    [InlineData("audio.muted", "off", "false")]
    [InlineData("interface.overlay", "always", "Always")]
    [InlineData("emulation.loop", "Disc", "Disc")]
    [InlineData("video.pixelAspect", "1.25", "1.25")]
    [InlineData("audio.device", "Speakers", "Speakers")]
    public void ValuesAreConvertedToTheOptionType(string path, string input, string expected)
    {
        var settings = new VxpSettings();
        var entry = SettingsStore.Find(settings, path)!;

        SettingsStore.Set(entry, input);

        Assert.Equal(expected, entry.Value);
    }

    [Theory]
    [InlineData("video.brightness", "loud")]
    [InlineData("audio.muted", "perhaps")]
    [InlineData("interface.overlay", "sideways")]
    public void BadValuesAreRejectedWithAnExplanation(string path, string input)
    {
        var settings = new VxpSettings();
        var entry = SettingsStore.Find(settings, path)!;

        var error = Assert.Throws<ArgumentException>(() => SettingsStore.Set(entry, input));
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void EnumOptionsAdvertiseTheirLegalValues()
    {
        var entry = SettingsStore.Find(new VxpSettings(), "emulation.loop")!;
        Assert.Equal(Enum.GetNames<LoopMode>(), entry.Choices);
    }

    [Fact]
    public void BooleanOptionsAdvertiseTrueAndFalse()
    {
        var entry = SettingsStore.Find(new VxpSettings(), "audio.muted")!;
        Assert.Equal(["true", "false"], entry.Choices);
    }

    [Fact]
    public void RecentDiscsKeepTheNewestFirstWithoutDuplicates()
    {
        var settings = new VxpSettings();

        settings.RecordRecentDisc("a.cue");
        settings.RecordRecentDisc("b.cue");
        settings.RecordRecentDisc("a.cue");

        Assert.Equal(2, settings.RecentDiscs.Count);
        Assert.EndsWith("a.cue", settings.RecentDiscs[0]);
    }

    [Fact]
    public void RecentDiscsAreCapped()
    {
        var settings = new VxpSettings();
        for (var i = 0; i < VxpSettings.MaxRecentDiscs + 5; i++) settings.RecordRecentDisc($"disc{i}.cue");

        Assert.Equal(VxpSettings.MaxRecentDiscs, settings.RecentDiscs.Count);
    }

    [Fact]
    public void SettingsSurviveAWriteAndReadCycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vxp-tests", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("VXP_CONFIG_DIR");
        Environment.SetEnvironmentVariable("VXP_CONFIG_DIR", directory);

        try
        {
            var settings = new VxpSettings();
            settings.Video.Brightness = 33;
            settings.Emulation.Loop = LoopMode.Disc;
            settings.Interface.Overlay = OverlayMode.Always;
            settings.StoreInputMap(settings.BuildInputMap());

            SettingsStore.Save(settings);
            var reloaded = SettingsStore.Load();

            Assert.Equal(33, reloaded.Video.Brightness);
            Assert.Equal(LoopMode.Disc, reloaded.Emulation.Loop);
            Assert.Equal(OverlayMode.Always, reloaded.Interface.Overlay);
            Assert.NotEmpty(reloaded.Bindings);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VXP_CONFIG_DIR", previous);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ACorruptSettingsFileFallsBackToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vxp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("VXP_CONFIG_DIR");
        Environment.SetEnvironmentVariable("VXP_CONFIG_DIR", directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{ this is not json");

            var settings = SettingsStore.Load();

            Assert.Equal(new VxpSettings().Video.Brightness, settings.Video.Brightness);
            Assert.True(File.Exists(Path.Combine(directory, "settings.json.bad")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VXP_CONFIG_DIR", previous);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CommandLineOverridesAreNotWrittenBack()
    {
        WithConfigDirectory(() =>
        {
            var settings = new VxpSettings();
            settings.Audio.Volume = 40;
            SettingsStore.Save(settings);

            var live = SettingsStore.Load();
            var session = SettingsSession.Stored();
            PlayCommand.ApplyOverrides(
                CommandLine.Parse(["disc.cue", "--fullscreen", "--volume", "90", "--loop", "disc"]), live, session);

            Assert.True(live.Video.Fullscreen);
            Assert.Equal(90, live.Audio.Volume);

            // The viewer changes something unrelated, and one overridden option, in the menus.
            live.Video.Brightness = 12;
            live.Emulation.Loop = LoopMode.Track;
            session.Save(live);

            var saved = SettingsStore.Load();
            Assert.False(saved.Video.Fullscreen);
            Assert.Equal(40, saved.Audio.Volume);
            Assert.Equal(12, saved.Video.Brightness);
            Assert.Equal(LoopMode.Track, saved.Emulation.Loop);

            // Saving must not disturb the live values the window is still using.
            Assert.True(live.Video.Fullscreen);
            Assert.Equal(90, live.Audio.Volume);
        });
    }

    [Fact]
    public void ADetachedSessionNeverWrites()
    {
        WithConfigDirectory(() =>
        {
            var settings = new VxpSettings();
            settings.Video.Brightness = 5;
            SettingsSession.Detached().Save(settings);

            Assert.False(File.Exists(SettingsStore.FilePath));
        });
    }

    private static void WithConfigDirectory(Action body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vxp-tests", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("VXP_CONFIG_DIR");
        Environment.SetEnvironmentVariable("VXP_CONFIG_DIR", directory);

        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("VXP_CONFIG_DIR", previous);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}

public class CommandLineTests
{
    [Fact]
    public void SplitsOptionsFromPositionalArguments()
    {
        var args = CommandLine.Parse(["disc.cue", "--track", "5", "--json"]);

        Assert.Equal(["disc.cue"], args.Positional);
        Assert.Equal(5, args.Int("track", 0));
        Assert.True(args.Json);
    }

    [Fact]
    public void AcceptsEqualsSyntax()
    {
        var args = CommandLine.Parse(["--scale=4"]);
        Assert.Equal(4, args.Int("scale", 0));
    }

    [Fact]
    public void NegativeNumbersAreNotMistakenForOptions()
    {
        var args = CommandLine.Parse(["--brightness", "-20"]);
        Assert.Equal(-20, args.Int("brightness", 0));
    }

    [Fact]
    public void ValuelessOptionsReadAsFlags()
    {
        var args = CommandLine.Parse(["--fullscreen", "--scale", "3"]);

        Assert.True(args.Has("fullscreen"));
        Assert.Equal(3, args.Int("scale", 0));
    }

    [Fact]
    public void KnownFlagsDoNotSwallowTheDiscPath()
    {
        var args = CommandLine.Parse(["--fullscreen", "--no-config", "disc.cue", "--mute"]);

        Assert.Equal(["disc.cue"], args.Positional);
        Assert.True(args.Has("fullscreen"));
        Assert.True(args.Has("no-config"));
        Assert.True(args.Has("mute"));
    }

    [Fact]
    public void UnreadOptionsAreReported()
    {
        var args = CommandLine.Parse(["--track", "2", "--tarck", "3"]);
        args.Int("track", 0);

        Assert.Equal(["tarck"], args.Unrecognised());
    }

    [Fact]
    public void MissingRequiredOptionsExplainThemselves()
    {
        var args = CommandLine.Parse(["disc.cue"]);
        var error = Assert.Throws<ArgumentException>(() => args.Require("out"));

        Assert.Contains("--out", error.Message);
    }

    [Theory]
    [InlineData("90f", 90)]
    [InlineData("10frames", 10)]
    [InlineData("2s", 20)]
    [InlineData("2", 20)]
    [InlineData("500ms", 5)]
    [InlineData("1m", 600)]
    public void DurationsConvertToFrames(string text, int expected)
        => Assert.Equal(expected, CommandLine.ParseDurationInFrames(text, frameRate: 10));

    [Theory]
    [InlineData("later")]
    [InlineData("5 parsecs")]
    public void MalformedDurationsAreRejected(string text)
        => Assert.Throws<ArgumentException>(() => CommandLine.ParseDurationInFrames(text, 10));
}

public class PictureAdjustmentTests
{
    private static byte[] Grey(byte level)
    {
        var rgba = new byte[VideoDecoder.RgbaFrameBytes];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = level;
            rgba[i + 1] = level;
            rgba[i + 2] = level;
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    [Fact]
    public void NeutralSettingsLeaveThePictureAlone()
    {
        var adjustment = new PictureAdjustment();
        var pixels = Grey(0x77);
        var original = (byte[])pixels.Clone();

        Assert.True(adjustment.IsIdentity);
        adjustment.Apply(pixels);

        Assert.Equal(original, pixels);
    }

    [Fact]
    public void BrightnessRaisesEveryChannel()
    {
        var adjustment = new PictureAdjustment { Brightness = 25 };
        var pixels = Grey(0x40);

        adjustment.Apply(pixels);

        Assert.True(pixels[0] > 0x40);
    }

    [Fact]
    public void FullNegativeSaturationProducesGrey()
    {
        var adjustment = new PictureAdjustment { Saturation = -100 };
        var pixels = new byte[VideoDecoder.RgbaFrameBytes];
        pixels[0] = 0xFF;
        pixels[1] = 0x00;
        pixels[2] = 0x00;

        adjustment.Apply(pixels);

        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
    }

    [Fact]
    public void SettingsAreCarriedIntoTheAdjustment()
    {
        var settings = new VideoSettings { Brightness = 10, Contrast = -20, Gamma = 120, Saturation = 30 };
        var adjustment = PictureAdjustment.FromSettings(settings);

        Assert.Equal(10, adjustment.Brightness);
        Assert.Equal(-20, adjustment.Contrast);
        Assert.Equal(120, adjustment.Gamma);
        Assert.Equal(30, adjustment.Saturation);
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        var adjustment = new PictureAdjustment { Brightness = 500, Gamma = 5 };

        Assert.Equal(100, adjustment.Brightness);
        Assert.Equal(50, adjustment.Gamma);
    }

    [Fact]
    public void AMalformedChannelOrderFallsBackToRgb()
    {
        var adjustment = new PictureAdjustment();
        adjustment.SetOrder("nonsense");

        Assert.Equal(ChannelOrder.Default, adjustment.Order);
    }
}
