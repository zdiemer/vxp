using Vxp.Config;
using Vxp.Emulation;

namespace Vxp.Ui;

/// <summary>Facts the host feeds the overlay each frame.</summary>
/// <param name="Volume">Output level, 0 to 100.</param>
/// <param name="Muted">Whether sound is silenced.</param>
/// <param name="FramesPerSecond">Presentation rate, for the performance line.</param>
/// <param name="AudioBufferMs">How much audio is queued, for the performance line.</param>
public readonly record struct HostStatus(int Volume, bool Muted, double FramesPerSecond, int AudioBufferMs);

/// <summary>
/// Draws the status line, the branch prompt at a decision point, and a progress bar.
/// </summary>
public sealed class StatusOverlay
{
    private DateTime _visibleUntil = DateTime.MinValue;
    private string _lastSummary = string.Empty;

    /// <summary>Shows the overlay for the configured time, used when something changes.</summary>
    public void Flash(InterfaceSettings settings)
        => _visibleUntil = DateTime.UtcNow.AddSeconds(Math.Max(1, settings.OverlaySeconds));

    /// <summary>Whether the overlay should be drawn right now.</summary>
    public bool ShouldDraw(InterfaceSettings settings, VideoNowPlayer player)
    {
        if (settings.Overlay == OverlayMode.Hidden) return false;
        if (settings.Overlay == OverlayMode.Always) return true;

        // A choice point stays up until the viewer deals with it.
        if (settings.ShowChoices && player.IsChoicePoint) return true;

        // Pausing is a state worth seeing, not a passing event.
        if (player.State != TransportState.Playing) return true;

        return DateTime.UtcNow < _visibleUntil;
    }

    /// <summary>
    /// Notices when the summary line changes so the overlay can flash by itself,
    /// which is what makes Auto mode feel right without the host tracking every event.
    /// </summary>
    public void Track(VideoNowPlayer player, InterfaceSettings settings)
    {
        var summary = $"{player.State}|{player.CurrentTrack}|{player.Speed:0.##}";
        if (summary == _lastSummary) return;

        _lastSummary = summary;
        Flash(settings);
    }

    /// <summary>Draws the overlay onto <paramref name="canvas"/>.</summary>
    public void Draw(
        Canvas canvas, int scale, VideoNowPlayer player,
        VxpSettings settings, HostStatus host)
    {
        var ui = settings.Interface;
        var lines = new List<(string Text, Rgba Color)>();

        if (ui.ShowTrackInfo)
        {
            var state = player.State switch
            {
                TransportState.Playing => player.Speed > 1.01 ? $">> {player.Speed:0.##}x"
                                        : player.Speed < 0.99 ? $"> {player.Speed:0.##}x"
                                        : ">",
                TransportState.Paused => "||",
                _ => "[]",
            };

            var volume = host.Muted ? "muted" : $"vol {host.Volume}";
            var loop = player.Loop == LoopMode.None ? string.Empty : $"  loop {player.Loop}";

            lines.Add((
                $"{state}  track {player.CurrentTrack:00}/{TrackCount(player)}  " +
                $"{player.Position:mm\\:ss}/{player.TrackDuration:mm\\:ss}  {volume}{loop}",
                Rgba.White));
        }

        if (ui.ShowPerformance)
        {
            lines.Add((
                $"{host.FramesPerSecond:0} fps  audio {host.AudioBufferMs} ms  " +
                $"frame {player.CurrentFrame}/{player.TrackFrameCount}",
                Rgba.Grey));
        }

        if (ui.ShowChoices && player.IsChoicePoint)
        {
            var choices = string.Join("   ", player.Branches.Select(b => $"[{b.Slot + 1}] track {b.Track}"));
            lines.Add(($"Choose:  {choices}", Rgba.Accent));

            if (player.SelectedChoice >= 0)
                lines.Add(($"Chosen: {player.SelectedChoice + 1}", Rgba.Warn));
        }

        if (lines.Count == 0) return;

        var padding = 6 * scale;
        var lineHeight = BitmapFont.LineAdvance * scale;
        var width = lines.Max(l => BitmapFont.Measure(l.Text, scale)) + padding * 2;
        var height = lines.Count * lineHeight + padding;

        var (x, y) = ui.OverlayCorner switch
        {
            OverlayCorner.TopRight => (canvas.Width - width - padding, padding),
            OverlayCorner.BottomLeft => (padding, canvas.Height - height - padding),
            OverlayCorner.BottomRight => (canvas.Width - width - padding, canvas.Height - height - padding),
            _ => (padding, padding),
        };

        canvas.Fill(x, y, width, height, new Rgba(0, 0, 0, 140));

        for (var i = 0; i < lines.Count; i++)
            canvas.ShadowText(x + padding, y + padding / 2 + i * lineHeight, lines[i].Text, scale, lines[i].Color);

        DrawProgress(canvas, scale, player, x, y + height, width);
    }

    private static void DrawProgress(Canvas canvas, int scale, VideoNowPlayer player, int x, int y, int width)
    {
        if (player.TrackFrameCount <= 0) return;

        var height = Math.Max(1, scale);
        var filled = (int)(width * (player.CurrentFrame / (double)player.TrackFrameCount));

        canvas.Fill(x, y, width, height, new Rgba(0, 0, 0, 140));
        canvas.Fill(x, y, Math.Clamp(filled, 0, width), height, Rgba.Accent);
    }

    private static string TrackCount(VideoNowPlayer player)
        => player.Disc.Tracks.Count.ToString("00");
}
