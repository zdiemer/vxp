using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vxp.Emulation;

namespace Vxp.Config;

/// <summary>One settable option, discovered by reflection over the settings model.</summary>
/// <param name="Path">Dotted path such as <c>video.brightness</c>.</param>
/// <param name="Property">The property behind it.</param>
/// <param name="Owner">The object the property lives on.</param>
public sealed record SettingEntry(string Path, PropertyInfo Property, object Owner)
{
    /// <summary>The value as text.</summary>
    public string Value => SettingsStore.Format(Property.GetValue(Owner));

    /// <summary>The type of value this option accepts.</summary>
    public Type Type => Property.PropertyType;

    /// <summary>Legal values for an enum or boolean option, otherwise empty.</summary>
    public IReadOnlyList<string> Choices =>
        Type.IsEnum ? Enum.GetNames(Type)
        : Type == typeof(bool) ? ["true", "false"]
        : [];
}

/// <summary>
/// Loads and saves <see cref="VxpSettings"/>, and exposes every option by dotted path so
/// the command line can read and write the same settings the menus do.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Directory holding the settings file: <c>%APPDATA%\vxp</c> on Windows and
    /// <c>$XDG_CONFIG_HOME/vxp</c> (or <c>~/.config/vxp</c>) elsewhere. Override with
    /// the <c>VXP_CONFIG_DIR</c> environment variable.
    /// </summary>
    public static string Directory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("VXP_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

            var baseDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create);

            if (string.IsNullOrEmpty(baseDirectory))
            {
                baseDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(baseDirectory, "vxp");
        }
    }

    /// <summary>Full path of the settings file.</summary>
    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>
    /// Reads the settings file, returning defaults if it is missing. A corrupt file is
    /// kept as <c>settings.json.bad</c> rather than being silently overwritten.
    /// </summary>
    public static VxpSettings Load()
    {
        var path = FilePath;
        if (!File.Exists(path)) return new VxpSettings();

        try
        {
            return Migrate(JsonSerializer.Deserialize<VxpSettings>(File.ReadAllText(path), Json) ?? new VxpSettings());
        }
        catch (JsonException)
        {
            try
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
            catch (IOException)
            {
                // Keeping the broken file is a courtesy; failing to is not worth reporting.
            }

            return new VxpSettings();
        }
    }

    /// <summary>Brings settings written by an older version up to the current schema.</summary>
    public static VxpSettings Migrate(VxpSettings settings)
    {
        if (settings.Version < 2)
        {
            // Version 1 defaulted to disc order because register 0x4F was not understood,
            // and saved that default like any other value. Following the disc is now both
            // understood and the default, so a stored disc order is taken to be the old
            // default rather than a choice.
            if (settings.Emulation.Navigation == NavigationPolicy.DiscOrder)
                settings.Emulation.Navigation = NavigationPolicy.FollowHeader;
        }

        if (settings.Version < 3)
        {
            // Versions 1 and 2 wrote their defaults into every file: answering an unanswered
            // choice with its first entry, and holding a key press until the segment ended.
            // Both turned out wrong against the discs, so the stored values are taken to be
            // those defaults rather than choices, as with disc order above.
            if (settings.Emulation.ChoiceTimeout == ChoiceTimeout.FirstBranch)
                settings.Emulation.ChoiceTimeout = ChoiceTimeout.Wait;

            settings.Emulation.InstantChoices = true;
        }

        settings.Version = VxpSettings.CurrentVersion;
        return settings;
    }

    /// <summary>Writes the settings file, creating its directory if need be.</summary>
    public static void Save(VxpSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Write to a temporary file first so an interrupted save cannot truncate the
        // settings that are already there.
        var path = FilePath;
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Every settable option, in a stable order.</summary>
    public static IReadOnlyList<SettingEntry> Enumerate(VxpSettings settings)
    {
        var entries = new List<SettingEntry>();

        foreach (var section in typeof(VxpSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!section.CanWrite || section.PropertyType.IsPrimitive) continue;
            if (section.PropertyType.Namespace?.StartsWith("Vxp.Config") != true) continue;

            var owner = section.GetValue(settings);
            if (owner is null) continue;

            foreach (var option in section.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!option.CanRead || !option.CanWrite) continue;
                entries.Add(new SettingEntry($"{Camel(section.Name)}.{Camel(option.Name)}", option, owner));
            }
        }

        return entries;
    }

    /// <summary>Finds an option by dotted path, ignoring capitalisation.</summary>
    public static SettingEntry? Find(VxpSettings settings, string path)
        => Enumerate(settings).FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sets an option from text, converting to the option's type.
    /// </summary>
    /// <exception cref="ArgumentException">The value does not fit the option.</exception>
    public static void Set(SettingEntry entry, string value)
        => entry.Property.SetValue(entry.Owner, Convert(entry, value));

    private static object Convert(SettingEntry entry, string value)
    {
        var type = Nullable.GetUnderlyingType(entry.Type) ?? entry.Type;

        if (type == typeof(string)) return value;

        if (type == typeof(bool))
        {
            return value.ToLowerInvariant() switch
            {
                "true" or "yes" or "on" or "1" => true,
                "false" or "no" or "off" or "0" => false,
                _ => throw new ArgumentException($"'{entry.Path}' takes true or false, not '{value}'."),
            };
        }

        if (type.IsEnum)
        {
            if (Enum.TryParse(type, value, ignoreCase: true, out var parsed) && parsed is not null) return parsed;
            throw new ArgumentException($"'{entry.Path}' takes one of: {string.Join(", ", Enum.GetNames(type))}.");
        }

        if (type == typeof(int))
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
            throw new ArgumentException($"'{entry.Path}' takes a whole number, not '{value}'.");
        }

        if (type == typeof(double))
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            throw new ArgumentException($"'{entry.Path}' takes a number, not '{value}'.");
        }

        throw new ArgumentException($"'{entry.Path}' cannot be set from the command line.");
    }

    /// <summary>Renders a value the way the command line and menus show it.</summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
