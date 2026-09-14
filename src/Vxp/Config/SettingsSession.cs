namespace Vxp.Config;

/// <summary>
/// Decides what a playing session writes back to the settings file.
/// </summary>
/// <remarks>
/// <para>
/// The player saves as the viewer changes things, which is right for choices made in the
/// menus and wrong for two other sources of change. Options given on the command line
/// are for this run only, so a launcher that always passes <c>--fullscreen</c> must not
/// turn full screen on for every later windowed run. And <c>--no-config</c> means the
/// file is neither read nor written at all.
/// </para>
/// <para>
/// An override is remembered alongside the value it displaced. When the settings are
/// saved, any overridden option still holding its override value is written as the stored
/// value instead; one the viewer has since changed is written as they left it.
/// </para>
/// </remarks>
public sealed class SettingsSession
{
    private readonly List<(SettingEntry Entry, object? Stored, object? Override)> _overrides = [];

    private SettingsSession(bool persist) => Persists = persist;

    /// <summary>Whether this session writes to the settings file at all.</summary>
    public bool Persists { get; }

    /// <summary>A session that saves to the settings file.</summary>
    public static SettingsSession Stored() => new(persist: true);

    /// <summary>A session that never touches the settings file.</summary>
    public static SettingsSession Detached() => new(persist: false);

    /// <summary>
    /// Sets an option for this run without making it the stored value.
    /// </summary>
    /// <param name="settings">The live settings.</param>
    /// <param name="path">Dotted option path, such as <c>video.fullscreen</c>.</param>
    /// <param name="value">The value for this run.</param>
    public void Override(VxpSettings settings, string path, object value)
    {
        var entry = SettingsStore.Find(settings, path)
            ?? throw new ArgumentException($"No such setting: {path}", nameof(path));

        var stored = entry.Property.GetValue(entry.Owner);
        entry.Property.SetValue(entry.Owner, value);

        // A second override of the same option (--fullscreen --windowed) keeps the value
        // that came from the file, not the first override.
        var existing = _overrides.FindIndex(o => o.Entry.Path == entry.Path);
        if (existing >= 0)
        {
            stored = _overrides[existing].Stored;
            _overrides.RemoveAt(existing);
        }

        _overrides.Add((entry, stored, value));
    }

    /// <summary>Writes the settings, minus this run's overrides, unless the session is detached.</summary>
    public void Save(VxpSettings settings)
    {
        if (!Persists) return;

        var restore = new List<(SettingEntry Entry, object? Live)>();
        foreach (var (entry, stored, overridden) in _overrides)
        {
            var live = entry.Property.GetValue(entry.Owner);
            if (!Equals(live, overridden)) continue;

            entry.Property.SetValue(entry.Owner, stored);
            restore.Add((entry, live));
        }

        try
        {
            SettingsStore.Save(settings);
        }
        finally
        {
            foreach (var (entry, live) in restore) entry.Property.SetValue(entry.Owner, live);
        }
    }
}
