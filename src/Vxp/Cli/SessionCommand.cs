using System.Globalization;
using System.Text.Json;
using Vxp.Discs;
using Vxp.Emulation;
using Vxp.Format;
using Vxp.Video;

namespace Vxp.Cli;

/// <summary>
/// Drives the emulator with no window at all, from a script of transport commands.
/// </summary>
/// <remarks>
/// <para>
/// The player is clocked by <see cref="VideoNowPlayer.RenderAudio"/>, so a headless run
/// is simply the same loop without a sound card: pulling PCM advances the emulation, and
/// the frames that fall out can be written to disk or thrown away. That makes scripted
/// runs deterministic and as fast as the machine can decode, rather than real time.
/// </para>
/// </remarks>
public static class SessionCommand
{
    /// <summary>Runs a scripted session.</summary>
    public static int Run(CommandLine args)
    {
        var cue = args.RequireCue();
        var script = ReadScript(args);

        using var disc = DiscImage.Open(cue);
        using var player = new VideoNowPlayer(disc);

        if (args.Value("navigation") is { } navigation
            && Enum.TryParse<NavigationPolicy>(navigation, ignoreCase: true, out var policy))
        {
            player.Navigation = policy;
        }

        if (args.Value("choice-timeout") is { } timeout
            && Enum.TryParse<ChoiceTimeout>(timeout, ignoreCase: true, out var choiceTimeout))
        {
            player.Timeout = choiceTimeout;
        }

        if (args.Value("loop") is { } loop && Enum.TryParse<LoopMode>(loop, ignoreCase: true, out var loopMode))
            player.Loop = loopMode;

        var session = new Session(player, args);
        try
        {
            foreach (var line in script) session.Execute(line);
        }
        finally
        {
            session.Finish();
        }

        return session.Failures == 0 ? 0 : 1;
    }

    private static IReadOnlyList<string> ReadScript(CommandLine args)
    {
        if (args.Value("script") is { } path)
        {
            if (path == "-") return ReadAllLines(Console.In);
            if (!File.Exists(path)) throw new FileNotFoundException($"Script not found: {path}");
            return File.ReadAllLines(path);
        }

        if (args.Value("commands") is { } inline)
            return inline.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // With no script, play the disc through from the start.
        return ["play all"];
    }

    private static List<string> ReadAllLines(TextReader reader)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>State shared by the commands of one scripted run.</summary>
    private sealed class Session
    {
        private readonly VideoNowPlayer _player;
        private readonly CommandLine _args;
        private readonly short[] _buffer = new short[4096];
        private readonly PictureAdjustment _adjust;
        private readonly string? _frameDirectory;
        private readonly WavWriter? _wav;
        private readonly bool _json;
        private readonly int _maxFrames;

        private byte[]? _lastFrame;
        private int _framesSeen;
        private int _framesWritten;

        public Session(VideoNowPlayer player, CommandLine args)
        {
            _player = player;
            _args = args;
            _json = args.Json;
            _adjust = PictureAdjustment.FromArgs(args);
            _maxFrames = args.Int("max-frames", int.MaxValue);

            _frameDirectory = args.Value("frames-out");
            if (_frameDirectory is not null) Directory.CreateDirectory(_frameDirectory);

            if (args.Value("wav") is { } wavPath)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(wavPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                _wav = new WavWriter(wavPath, FrameLayout.AudioSampleRate);
            }

            player.FrameDecoded += OnFrameDecoded;
        }

        /// <summary>Number of failed assertions.</summary>
        public int Failures { get; private set; }

        private void OnFrameDecoded(VideoNowPlayer player)
        {
            _framesSeen++;
            _lastFrame = (byte[])player.Framebuffer.Clone();

            if (_frameDirectory is null || _framesWritten >= _maxFrames) return;

            var rgba = (byte[])_lastFrame.Clone();
            _adjust.Apply(rgba);
            PngWriter.Write(
                Path.Combine(_frameDirectory, $"t{player.CurrentTrack:D2}_f{player.CurrentFrame - 1:D5}.png"),
                rgba, FrameLayout.Width, FrameLayout.Height, _args.Int("scale", 1));

            _framesWritten++;
        }

        public void Execute(string line)
        {
            var text = line.Trim();
            var comment = text.IndexOf('#');
            if (comment >= 0) text = text[..comment].Trim();
            if (text.Length == 0) return;

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var verb = parts[0].ToLowerInvariant();
            var operand = parts.Length > 1 ? parts[1] : null;

            switch (verb)
            {
                case "track" or "select":
                    _player.SelectTrack(Number(operand, verb));
                    break;

                case "play":
                    Play(operand);
                    break;

                case "pause":
                    _player.Pause();
                    break;

                case "resume":
                    _player.Play();
                    break;

                case "stop":
                    _player.Stop();
                    break;

                case "next":
                    _player.NextTrack();
                    break;

                case "prev" or "previous":
                    _player.PreviousTrack();
                    break;

                case "back":
                    if (!_player.GoBack()) Report("back: no history to return to.");
                    break;

                case "seek":
                    Seek(operand);
                    break;

                case "frame":
                    _player.SeekToFrame(Number(operand, verb));
                    break;

                case "step":
                    _player.StepFrame(operand is null ? 1 : Number(operand, verb));
                    break;

                case "choice" or "press":
                    if (!_player.PressChoice(Number(operand, verb) - 1))
                        Report($"choice {operand}: the current segment does not offer that branch.");
                    break;

                case "choose":
                    if (!_player.TakeChoiceNow(Number(operand, verb) - 1))
                        Report($"choose {operand}: the current segment does not offer that branch.");
                    break;

                case "speed":
                    _player.Speed = double.Parse(operand ?? "1", CultureInfo.InvariantCulture);
                    break;

                case "screenshot":
                    Screenshot(operand ?? throw new ArgumentException("screenshot needs a file name."));
                    break;

                case "status":
                    Status();
                    break;

                case "expect" or "assert":
                    Expect(parts);
                    break;

                case "echo":
                    Console.WriteLine(string.Join(' ', parts.Skip(1)));
                    break;

                default:
                    throw new ArgumentException($"Unknown script command '{verb}'.");
            }
        }

        private void Play(string? operand)
        {
            _player.Play();

            // Both open-ended forms answer to the same ceiling. A disc can quite legally
            // ask for a segment to repeat for ever: a menu whose first branch names its
            // own track sits there until the viewer picks something, which is what Teen
            // Titans track 23 does. That is the right thing on the hardware and a hang in
            // a script, so a scripted run always has a way back out.
            var limit = (long)_args.Int("max-seconds", 3600) * FrameLayout.AudioSampleRate;
            var rendered = 0L;

            if (operand is null or "all")
            {
                while (_player.State == TransportState.Playing && rendered < limit)
                {
                    _player.RenderAudio(_buffer);
                    Capture();
                    rendered += _buffer.Length;
                }

                WarnIfCapped(rendered, limit, "all");
                return;
            }

            if (operand == "track")
            {
                var track = _player.CurrentTrack;
                while (_player.State == TransportState.Playing && _player.CurrentTrack == track && rendered < limit)
                {
                    _player.RenderAudio(_buffer);
                    Capture();
                    rendered += _buffer.Length;
                }

                WarnIfCapped(rendered, limit, $"track {track}");
                return;
            }

            var frames = CommandLine.ParseDurationInFrames(operand, _player.Layout.FrameRate);
            var samples = (long)frames * _player.Layout.AudioBytes;
            var done = 0L;

            while (done < samples && _player.State == TransportState.Playing)
            {
                var take = (int)Math.Min(_buffer.Length, samples - done);
                _player.RenderAudio(_buffer.AsSpan(0, take));
                Capture(take);
                done += take;
            }
        }

        /// <summary>
        /// Says so when a play ran into the ceiling rather than reaching its own end, so
        /// a truncated run cannot quietly look like a complete one.
        /// </summary>
        private void WarnIfCapped(long rendered, long limit, string what)
        {
            if (rendered < limit) return;

            Report($"play {what}: stopped at the --max-seconds ceiling with the disc still playing.");
        }

        private void Capture(int count = -1)
        {
            if (_wav is null) return;
            _wav.Write(_buffer.AsSpan(0, count < 0 ? _buffer.Length : count));
        }

        private void Seek(string? operand)
        {
            if (operand is null) throw new ArgumentException("seek needs a duration, such as '5s' or '-2s'.");

            var frames = CommandLine.ParseDurationInFrames(operand.TrimStart('+'), _player.Layout.FrameRate);
            _player.SeekToFrame(_player.CurrentFrame + frames);
        }

        private void Screenshot(string path)
        {
            if (_lastFrame is null)
            {
                Report("screenshot: nothing has been decoded yet.");
                return;
            }

            var rgba = (byte[])_lastFrame.Clone();
            _adjust.Apply(rgba);

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            PngWriter.Write(path, rgba, FrameLayout.Width, FrameLayout.Height, _args.Int("scale", 1));
            if (!_json) Console.WriteLine($"Wrote {path}.");
        }

        private void Status()
        {
            var branches = _player.Branches.Select(b => new { slot = b.Slot + 1, track = b.Track, tag = b.Tag, then = b.FollowOn });

            if (_json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    state = _player.State.ToString(),
                    track = _player.CurrentTrack,
                    frame = _player.CurrentFrame,
                    trackFrames = _player.TrackFrameCount,
                    position = _player.Position.ToString(),
                    speed = _player.Speed,
                    kind = _player.CurrentHeader?.Kind.ToString(),
                    choices = branches,
                    history = _player.History,
                    framesDecoded = _framesSeen,
                }, DiscCommands.Json));

                return;
            }

            Console.WriteLine(
                $"{_player.State} track {_player.CurrentTrack} frame {_player.CurrentFrame}/{_player.TrackFrameCount} " +
                $"({_player.Position:mm\\:ss\\.ff}) speed {_player.Speed:0.##}x" +
                (_player.Branches.Count == 0
                    ? ""
                    : "  choices: " + string.Join(" ", _player.Branches.Select(b => $"{b.Slot + 1}->{b.Track}"))));
        }

        private void Expect(string[] parts)
        {
            if (parts.Length < 3) throw new ArgumentException("expect needs a field and a value, such as 'expect track 13'.");

            var field = parts[1].ToLowerInvariant();
            var wanted = parts[2];

            var actual = field switch
            {
                "track" => _player.CurrentTrack.ToString(),
                "frame" => _player.CurrentFrame.ToString(),
                "state" => _player.State.ToString(),
                "kind" => _player.CurrentHeader?.Kind.ToString() ?? "None",
                "choices" => _player.Branches.Count.ToString(),
                _ => throw new ArgumentException($"Cannot check '{field}'."),
            };

            if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
            {
                if (!_json) Console.WriteLine($"ok: {field} is {actual}");
                return;
            }

            Failures++;
            Report($"expect {field} {wanted}: was {actual}");
        }

        private void Report(string message)
        {
            Console.Error.WriteLine(message);
        }

        private static int Number(string? operand, string verb)
            => int.TryParse(operand, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ArgumentException($"{verb} needs a number.");

        public void Finish()
        {
            _player.FrameDecoded -= OnFrameDecoded;
            _wav?.Dispose();

            if (_json) return;

            Console.WriteLine(
                $"Decoded {_framesSeen} frames" +
                (_framesWritten > 0 ? $", wrote {_framesWritten}" : "") +
                (Failures > 0 ? $", {Failures} failed check(s)" : "") + ".");
        }
    }
}
