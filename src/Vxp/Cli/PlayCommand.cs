using Vxp.Config;
using Vxp.Emulation;
using Vxp.Ui.Native;

namespace Vxp.Cli;

/// <summary>Opens a disc in the windowed player.</summary>
public static class PlayCommand
{
    /// <summary>Runs the player until the viewer quits.</summary>
    /// <remarks>
    /// With no disc named the window opens empty, ready for one to be opened or dropped on
    /// it; that is what double-clicking vxp.exe does. A disc that is named but cannot be
    /// opened is still an error here, so a launcher sees it in the exit code.
    /// </remarks>
    public static int Run(CommandLine args)
    {
        var cuePath = args.At(0) is null ? null : args.RequireCue();

        var detached = args.Has("no-config");
        var settings = detached ? new VxpSettings() : SettingsStore.Load();
        var session = detached ? SettingsSession.Detached() : SettingsSession.Stored();
        ApplyOverrides(args, settings, session);

        var loaded = cuePath is null ? null : LoadedDisc.Open(cuePath);

        try
        {
            if (loaded is not null)
            {
                var player = loaded.Player;
                if (args.Int("track") is { } track) player.SelectTrack(track);
                if (args.Int("frame") is { } frame) player.SeekToFrame(frame);

                // Only a zip needs this: its tracks are decompressed ahead of the branches that
                // will want them, so a jump does not stall on the archive.
                player.Disc.PrecacheInBackground();

                settings.RecordRecentDisc(cuePath!);
                TrySave(session, settings);
            }

            using var window = new PlayerWindow(loaded, settings, settings.BuildInputMap(), session);

            // The window owns the disc from here, and swaps or disposes it itself.
            loaded = null;

            // Only once the window is up, so a start-up failure is still there to read.
            if (OperatingSystem.IsWindows()) Win32.ReleaseOwnConsole();

            window.Run();
            return 0;
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    /// <summary>
    /// Applies command-line overrides on top of the stored settings, so a one-off run can
    /// differ without disturbing what is saved.
    /// </summary>
    public static void ApplyOverrides(CommandLine args, VxpSettings settings, SettingsSession session)
    {
        if (args.Int("scale") is { } scale) session.Override(settings, "video.windowScale", scale);
        if (args.Has("fullscreen")) session.Override(settings, "video.fullscreen", true);
        if (args.Has("windowed")) session.Override(settings, "video.fullscreen", false);
        if (args.Int("volume") is { } volume) session.Override(settings, "audio.volume", Math.Clamp(volume, 0, 100));
        if (args.Has("mute")) session.Override(settings, "audio.muted", true);
        if (args.Int("rate") is { } rate) session.Override(settings, "emulation.discSampleRate", Math.Clamp(rate, 16000, 40000));
        if (args.Int("speed") is { } speed) session.Override(settings, "emulation.speedPercent", Math.Clamp(speed, 25, 800));

        if (args.Value("loop") is { } loop && Enum.TryParse<LoopMode>(loop, ignoreCase: true, out var loopMode))
            session.Override(settings, "emulation.loop", loopMode);

        if (args.Value("navigation") is { } navigation
            && Enum.TryParse<NavigationPolicy>(navigation, ignoreCase: true, out var policy))
        {
            session.Override(settings, "emulation.navigation", policy);
        }

        if (args.Value("choice-timeout") is { } timeout
            && Enum.TryParse<ChoiceTimeout>(timeout, ignoreCase: true, out var choiceTimeout))
        {
            session.Override(settings, "emulation.choiceTimeout", choiceTimeout);
        }
    }

    private static void TrySave(SettingsSession session, VxpSettings settings)
    {
        try
        {
            session.Save(settings);
        }
        catch (IOException)
        {
            // Recording the recent-disc list is a convenience, not a reason to fail.
        }
    }
}
