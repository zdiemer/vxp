using System.Globalization;

namespace Vxp.Cli;

/// <summary>
/// A parsed command line: positional arguments plus <c>--name value</c> options and
/// <c>--name</c> flags.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine()
    {
    }

    /// <summary>Arguments that were not options, in order.</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>
    /// Parses <paramref name="args"/>. An option is any argument starting with <c>-</c>;
    /// it takes the next argument as its value unless that is another option.
    /// </summary>
    public static CommandLine Parse(IEnumerable<string> args)
    {
        var line = new CommandLine();
        var pending = (string?)null;

        foreach (var arg in args)
        {
            if (arg.StartsWith('-') && arg.Length > 1 && !IsNegativeNumber(arg))
            {
                if (pending is not null) line._options[pending] = null;

                var name = arg.TrimStart('-');
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    line._options[name[..equals]] = name[(equals + 1)..];
                    pending = null;
                }
                else
                {
                    pending = name;
                }

                continue;
            }

            if (pending is not null)
            {
                line._options[pending] = arg;
                pending = null;
            }
            else
            {
                line._positional.Add(arg);
            }
        }

        if (pending is not null) line._options[pending] = null;
        return line;
    }

    private static bool IsNegativeNumber(string arg)
        => arg.Length > 1 && (char.IsDigit(arg[1]) || arg[1] == '.');

    /// <summary>True if the option was given, with or without a value.</summary>
    public bool Has(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    /// <summary>The option's value, or null if it was absent or valueless.</summary>
    public string? Value(string name)
    {
        _used.Add(name);
        return _options.GetValueOrDefault(name);
    }

    /// <summary>The option's value, or <paramref name="fallback"/>.</summary>
    public string Value(string name, string fallback) => Value(name) ?? fallback;

    /// <summary>The option as a whole number, or <paramref name="fallback"/>.</summary>
    public int Int(string name, int fallback)
        => int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>The option as a whole number, or null.</summary>
    public int? Int(string name)
        => int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>The option as a number, or <paramref name="fallback"/>.</summary>
    public double Double(string name, double fallback)
        => double.TryParse(Value(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>Positional argument at <paramref name="index"/>, or null.</summary>
    public string? At(int index) => index < _positional.Count ? _positional[index] : null;

    /// <summary>Whether JSON output was asked for.</summary>
    public bool Json => Has("json");

    /// <summary>
    /// The cue sheet path, taken from the first positional argument.
    /// </summary>
    /// <exception cref="ArgumentException">No path was given.</exception>
    /// <exception cref="FileNotFoundException">The path does not exist.</exception>
    public string RequireCue(int index = 0)
    {
        var path = At(index) ?? throw new ArgumentException("A .cue file path is required.");
        if (!File.Exists(path)) throw new FileNotFoundException($"Cue sheet not found: {path}");
        return path;
    }

    /// <summary>An option that must be present.</summary>
    /// <exception cref="ArgumentException">The option is missing or has no value.</exception>
    public string Require(string name)
        => Value(name) ?? throw new ArgumentException($"--{name} <value> is required.");

    /// <summary>
    /// Options that were given but never read, which almost always means a typo. Call
    /// after a command has taken everything it wants.
    /// </summary>
    public IReadOnlyList<string> Unrecognised()
        => _options.Keys.Where(k => !_used.Contains(k)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Parses a duration such as <c>5s</c>, <c>250ms</c>, <c>90f</c> (frames) or a bare
    /// number of seconds, into frames at <paramref name="frameRate"/>.
    /// </summary>
    public static int ParseDurationInFrames(string text, double frameRate)
    {
        text = text.Trim();

        var digits = text.TrimEnd("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray());
        var suffix = text[digits.Length..].ToLowerInvariant();

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"'{text}' is not a duration.");

        return suffix switch
        {
            "ms" => (int)Math.Round(value / 1000.0 * frameRate),
            "f" or "frame" or "frames" => (int)Math.Round(value),
            "m" or "min" or "mins" => (int)Math.Round(value * 60 * frameRate),
            "" or "s" or "sec" or "secs" or "seconds" => (int)Math.Round(value * frameRate),
            _ => throw new ArgumentException($"'{text}' has an unknown duration unit '{suffix}'."),
        };
    }
}
