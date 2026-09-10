using Vxp.Config;
using Vxp.Discs;
using Vxp.Emulation;

namespace Vxp.Cli;

/// <summary>Opens a disc in the windowed player.</summary>
public static class PlayCommand
{
    /// <summary>Runs the player until the viewer quits.</summary>
    public static int Run(CommandLine args)
    {
        var cuePath = args.RequireCue();

        var settings = args.Has("no-config") ? new VxpSettings() : SettingsStore.Load();
        ApplyOverrides(args, settings);

        using var disc = DiscImage.Open(cuePath);
        using var player = new VideoNowPlayer(disc);

        if (args.Int("track") is { } track) player.SelectTrack(track);
        if (args.Int("frame") is { } frame) player.SeekToFrame(frame);

        if (!args.Has("no-config"))
        {
            settings.RecordRecentDisc(cuePath);
            TrySave(settings);
        }

        using var window = new PlayerWindow(player, settings, settings.BuildInputMap());
        window.Run();
        return 0;
    }

    /// <summary>
    /// Applies command-line overrides on top of the stored settings, so a one-off run can
    /// differ without disturbing what is saved.
    /// </summary>
    private static void ApplyOverrides(CommandLine args, VxpSettings settings)
    {
        if (args.Int("scale") is { } scale) settings.Video.WindowScale = scale;
        if (args.Has("fullscreen")) settings.Video.Fullscreen = true;
        if (args.Has("windowed")) settings.Video.Fullscreen = false;
        if (args.Int("volume") is { } volume) settings.Audio.Volume = Math.Clamp(volume, 0, 100);
        if (args.Has("mute")) settings.Audio.Muted = true;
        if (args.Int("speed") is { } speed) settings.Emulation.SpeedPercent = Math.Clamp(speed, 25, 800);

        if (args.Value("loop") is { } loop && Enum.TryParse<LoopMode>(loop, ignoreCase: true, out var loopMode))
            settings.Emulation.Loop = loopMode;

        if (args.Value("navigation") is { } navigation
            && Enum.TryParse<NavigationPolicy>(navigation, ignoreCase: true, out var policy))
        {
            settings.Emulation.Navigation = policy;
        }

        if (args.Value("choice-timeout") is { } timeout
            && Enum.TryParse<ChoiceTimeout>(timeout, ignoreCase: true, out var choiceTimeout))
        {
            settings.Emulation.ChoiceTimeout = choiceTimeout;
        }
    }

    private static void TrySave(VxpSettings settings)
    {
        try
        {
            SettingsStore.Save(settings);
        }
        catch (IOException)
        {
            // Recording the recent-disc list is a convenience, not a reason to fail.
        }
    }
}
