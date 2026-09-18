using Vxp.Config;
using Vxp.Input;
using Vxp.Ui;
using Xunit;

namespace Vxp.Tests;

/// <summary>
/// The player can start with no disc and change discs while it runs; these cover the
/// parts of that which are not the window itself.
/// </summary>
public class EmptyPlayerMenuTests
{
    private static MenuContext Context(
        VxpSettings? settings = null,
        LoadedDisc? disc = null,
        Action<InputAction>? perform = null,
        Action<string>? openRecent = null,
        Action<string, string>? showInfo = null)
    {
        return new MenuContext
        {
            Settings = settings ?? new VxpSettings(),
            Input = InputMap.CreateDefault(),
            Player = disc?.Player,
            Disc = disc?.Map,
            CloseMenu = () => { },
            Screenshot = () => { },
            ToggleFullscreen = () => { },
            Quit = () => { },
            Toast = _ => { },
            Perform = perform ?? (_ => { }),
            SelectTrack = _ => { },
            OpenPage = _ => { },
            ShowInfo = showInfo ?? ((_, _) => { }),
            OpenScreenshots = () => { },
            OpenRecent = openRecent ?? (_ => { }),
        };
    }

    [Fact]
    public void TheRootMenuWithNoDiscOffersToOpenOneAndNothingThatNeedsOne()
    {
        var page = Menus.Root(Context());
        var labels = page.Items.Select(i => i.Label).ToArray();

        Assert.Equal("No disc loaded", page.Subtitle);
        Assert.Contains("Open a disc...", labels);
        Assert.Contains("Recent discs", labels);
        Assert.Contains("Quit", labels);

        Assert.DoesNotContain("Resume", labels);
        Assert.DoesNotContain("Tracks", labels);
        Assert.DoesNotContain("Close disc", labels);
        Assert.DoesNotContain("Take a screenshot", labels);
    }

    [Fact]
    public void TheRootMenuWithADiscCanSwapOrEjectIt()
    {
        using var file = SyntheticDiscFile.Create(new TrackSpec(1, 8), new TrackSpec(2, 8));
        using var disc = LoadedDisc.Open(file.CuePath);

        var page = Menus.Root(Context(disc: disc));
        var labels = page.Items.Select(i => i.Label).ToArray();

        Assert.Equal("disc", page.Subtitle);
        Assert.Contains("Resume", labels);
        Assert.Contains("Tracks", labels);
        Assert.Contains("Open another disc...", labels);
        Assert.Contains("Close disc", labels);
    }

    [Fact]
    public void FileMenuOpensAndClosesDiscsThroughTheHost()
    {
        var performed = new List<InputAction>();
        var file = Menus.File(Context(perform: performed.Add));

        file.Items.Single(i => i.Label == "Open Disc...").Activate();
        file.Items.Single(i => i.Label == "Close Disc").Activate();

        Assert.Equal([InputAction.OpenDisc, InputAction.CloseDisc], performed);
        Assert.IsType<MenuSubmenu>(file.Items.Single(i => i.Label == "Recent Discs"));
    }

    [Fact]
    public void OpenDiscShowsTheControlBoundToIt()
    {
        var context = Context();
        var open = Menus.File(context).Items.Single(i => i.Label == "Open Disc...");

        Assert.Equal(context.Input.Describe(InputAction.OpenDisc), open.Value);
        Assert.Equal("Ctrl+O", open.Value);
    }

    [Fact]
    public void EveryBarPageBuildsWithNoDiscIncludingTracks()
    {
        var context = Context();

        foreach (var section in Menus.Bar(context))
            Assert.NotEmpty(section.Page().Items);

        var tracks = Menus.TrackMenu(context);
        Assert.All(tracks.Items, i => Assert.False(i.Selectable));
        Assert.All(Menus.TrackBrowser(context).Items, i => Assert.False(i.Selectable));
        Assert.All(Menus.DiscInfo(context).Items, i => Assert.False(i.Selectable));
    }

    [Fact]
    public void DiscInformationWithNoDiscSaysSo()
    {
        string? shown = null;
        var help = Menus.Help(Context(showInfo: (_, body) => shown = body));

        help.Items.Single(i => i.Label == "Disc Information...").Activate();

        Assert.Equal("No disc is loaded.", shown);
    }

    [Fact]
    public void RecentDiscsListsNewestFirstAndOpensTheOneChosen()
    {
        var settings = new VxpSettings();
        settings.RecordRecentDisc("first.cue");
        settings.RecordRecentDisc("second.zip");

        var opened = new List<string>();
        var page = Menus.RecentDiscs(Context(settings, openRecent: opened.Add));
        var discs = page.Items.OfType<MenuAction>().Where(i => i.Label != "Clear list").ToArray();

        Assert.Equal(["second", "first"], discs.Select(d => d.Label));

        discs[1].Activate();
        Assert.Single(opened);
        Assert.EndsWith("first.cue", opened[0]);
    }

    [Fact]
    public void RecentDiscsCanBeCleared()
    {
        var settings = new VxpSettings();
        settings.RecordRecentDisc("a.cue");

        Menus.RecentDiscs(Context(settings)).Items.Single(i => i.Label == "Clear list").Activate();

        Assert.Empty(settings.RecentDiscs);
        Assert.All(Menus.RecentDiscs(Context(settings)).Items, i => Assert.False(i.Selectable));
    }
}

public class DiscActionTests
{
    private static int Key(string name)
    {
        Assert.True(KeyNames.TryParse(name, out var code));
        return code;
    }

    [Fact]
    public void CtrlOOpensADiscAndCtrlWClosesIt()
    {
        var map = InputMap.CreateDefault();

        Assert.Equal(InputAction.OpenDisc, InputRouter.Resolve(map.MatchKey(Key("O"), KeyModifiers.Control), menuOpen: false));
        Assert.Equal(InputAction.CloseDisc, InputRouter.Resolve(map.MatchKey(Key("W"), KeyModifiers.Control), menuOpen: false));
        Assert.Empty(map.MatchKey(Key("O"), KeyModifiers.None));
    }

    [Fact]
    public void ASettingsFileFromBeforeDiscSwappingStillGetsTheNewBindings()
    {
        // Written by a version that had no OpenDisc: the file only mentions Quit.
        var stored = new Dictionary<string, List<string>> { ["Quit"] = ["Ctrl+Q"] };
        var map = InputMap.FromSettings(stored);

        Assert.Equal("Ctrl+O", map.Describe(InputAction.OpenDisc));
    }

    [Theory]
    [InlineData(InputAction.TogglePause)]
    [InlineData(InputAction.NextTrack)]
    [InlineData(InputAction.FastForward)]
    [InlineData(InputAction.Choice1)]
    [InlineData(InputAction.Choice6)]
    [InlineData(InputAction.TrackBrowser)]
    [InlineData(InputAction.Screenshot)]
    [InlineData(InputAction.CloseDisc)]
    public void TransportAndChoicesNeedADisc(InputAction action)
        => Assert.True(InputActions.NeedsDisc(action));

    [Theory]
    [InlineData(InputAction.OpenDisc)]
    [InlineData(InputAction.ToggleMenu)]
    [InlineData(InputAction.VolumeUp)]
    [InlineData(InputAction.ToggleMute)]
    [InlineData(InputAction.ToggleFullscreen)]
    [InlineData(InputAction.SpeedUp)]
    [InlineData(InputAction.ToggleLoop)]
    [InlineData(InputAction.Quit)]
    public void SettingsTheMenuAndOpeningADiscWorkWithout(InputAction action)
        => Assert.False(InputActions.NeedsDisc(action));

    [Fact]
    public void DiscActionsAreListedWithTheGeneralControls()
    {
        Assert.Equal(ActionCategory.General, InputActions.Category(InputAction.OpenDisc));
        Assert.Equal("Open disc", InputActions.Label(InputAction.OpenDisc));
    }
}

public class DiscFileTests
{
    [Theory]
    [InlineData("disc.cue", true)]
    [InlineData("DISC.ZIP", true)]
    [InlineData("disc (Track 01).bin", true)]
    [InlineData("notes.txt", false)]
    [InlineData("disc", false)]
    public void OnlyDiscFilesAreAcceptedFromADrop(string path, bool expected)
        => Assert.Equal(expected, DiscFiles.IsDiscFile(path));

    [Fact]
    public void TheDialogFilterIsNulSeparatedPairsEndingInTwoNuls()
    {
        var filter = DiscFiles.DialogFilter;

        Assert.EndsWith("\0\0", filter);
        var parts = filter.TrimEnd('\0').Split('\0');
        Assert.Equal(4, parts.Length);
        Assert.Equal("*.cue;*.zip;*.bin", parts[1]);
        Assert.Equal("*.*", parts[3]);
    }

    [Fact]
    public void ABinIsOpenedThroughTheCueSheetThatNamesIt()
    {
        using var file = SyntheticDiscFile.Create(new TrackSpec(1, 8), new TrackSpec(2, 8));
        var track = Path.Combine(file.Directory, "disc (Track 02).bin");

        Assert.Equal(file.CuePath, DiscFiles.Resolve(track));

        using var disc = LoadedDisc.Open(track);
        Assert.Equal(Path.GetFullPath(file.CuePath), disc.Path);
        Assert.Equal(2, disc.Map.Tracks.Count);
    }

    [Fact]
    public void ABinBesideACueOfTheSameNameUsesThatCue()
    {
        var directory = Directory.CreateTempSubdirectory("vxp-tests-").FullName;
        try
        {
            var bin = Path.Combine(directory, "game.bin");
            File.WriteAllBytes(bin, [0]);
            File.WriteAllText(Path.Combine(directory, "game.cue"), "");

            Assert.Equal(Path.Combine(directory, "game.cue"), DiscFiles.Resolve(bin));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ABinWithNoCueSheetIsReportedRatherThanGuessedAt()
    {
        var directory = Directory.CreateTempSubdirectory("vxp-tests-").FullName;
        try
        {
            var bin = Path.Combine(directory, "orphan.bin");
            File.WriteAllBytes(bin, [0]);

            var error = Assert.Throws<FileNotFoundException>(() => DiscFiles.Resolve(bin));
            Assert.Contains("orphan.bin", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingDiscIsFileNotFound()
        => Assert.Throws<FileNotFoundException>(() => LoadedDisc.Open(Path.Combine(Path.GetTempPath(), "vxp-no-such-disc.cue")));

    [Fact]
    public void AFileThatIsNotADiscFailsAndLeavesNothingOpen()
    {
        var directory = Directory.CreateTempSubdirectory("vxp-tests-").FullName;
        try
        {
            // A valid cue sheet over a track with no VideoNow stream in it.
            File.WriteAllBytes(Path.Combine(directory, "junk.bin"), new byte[CueSheetSectors * 2352]);
            File.WriteAllText(
                Path.Combine(directory, "junk.cue"),
                "FILE \"junk.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");

            Assert.Throws<InvalidDataException>(() => LoadedDisc.Open(Path.Combine(directory, "junk.cue")));

            // Were the image left mounted, Windows would refuse to delete its track file.
            File.Delete(Path.Combine(directory, "junk.bin"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AZipWithNoCueSheetInsideFails()
    {
        var directory = Directory.CreateTempSubdirectory("vxp-tests-").FullName;
        try
        {
            var zip = Path.Combine(directory, "empty.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                archive.CreateEntry("readme.txt");

            Assert.Throws<InvalidDataException>(() => LoadedDisc.Open(zip));
            File.Delete(zip);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisposingALoadedDiscReleasesItsFiles()
    {
        using var file = SyntheticDiscFile.Create(new TrackSpec(1, 8));

        var disc = LoadedDisc.Open(file.CuePath);
        disc.Player.RenderAudio(new short[4096]);
        disc.Dispose();

        File.Delete(Path.Combine(file.Directory, "disc (Track 01).bin"));
    }

    private const int CueSheetSectors = 64;
}
