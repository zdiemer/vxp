using System.Text.Json;
using Vxp.Config;
using Vxp.Input;

namespace Vxp.Cli;

/// <summary>Reads and writes the settings file from the command line.</summary>
public static class ConfigCommands
{
    /// <summary>Dispatches <c>vxp config ...</c>.</summary>
    public static int Config(CommandLine args)
    {
        var verb = (args.At(0) ?? "list").ToLowerInvariant();
        var settings = SettingsStore.Load();

        switch (verb)
        {
            case "path":
                Console.WriteLine(SettingsStore.FilePath);
                return 0;

            case "list":
                return List(args, settings);

            case "get":
                return Get(args, settings);

            case "set":
                return Set(args, settings);

            case "reset":
                return Reset(args, settings);

            case "recent":
                return Recent(args, settings);

            default:
                throw new ArgumentException($"Unknown config command '{verb}'. Use list, get, set, reset, recent or path.");
        }
    }

    private static int List(CommandLine args, VxpSettings settings)
    {
        var filter = args.At(1);
        var entries = SettingsStore.Enumerate(settings)
            .Where(e => filter is null || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (args.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                entries.Select(e => new { e.Path, e.Value, type = TypeName(e), e.Choices }),
                DiscCommands.Json));

            return 0;
        }

        var width = entries.Length == 0 ? 0 : entries.Max(e => e.Path.Length);
        foreach (var entry in entries)
        {
            var choices = entry.Choices.Count > 0 ? $"   ({string.Join(" | ", entry.Choices)})" : string.Empty;
            Console.WriteLine($"{entry.Path.PadRight(width)}  {entry.Value}{choices}");
        }

        return 0;
    }

    private static int Get(CommandLine args, VxpSettings settings)
    {
        var path = args.At(1) ?? throw new ArgumentException("config get <setting>");
        var entry = SettingsStore.Find(settings, path) ?? throw new ArgumentException($"No setting called '{path}'.");

        Console.WriteLine(args.Json
            ? JsonSerializer.Serialize(new { entry.Path, entry.Value, type = TypeName(entry), entry.Choices }, DiscCommands.Json)
            : entry.Value);

        return 0;
    }

    private static int Set(CommandLine args, VxpSettings settings)
    {
        var path = args.At(1) ?? throw new ArgumentException("config set <setting> <value>");
        var value = args.At(2) ?? throw new ArgumentException("config set <setting> <value>");
        var entry = SettingsStore.Find(settings, path) ?? throw new ArgumentException($"No setting called '{path}'.");

        SettingsStore.Set(entry, value);
        SettingsStore.Save(settings);

        Console.WriteLine($"{entry.Path} = {entry.Value}");
        return 0;
    }

    private static int Reset(CommandLine args, VxpSettings settings)
    {
        var path = args.At(1);

        if (path is null || path == "all")
        {
            var fresh = new VxpSettings { RecentDiscs = settings.RecentDiscs };
            SettingsStore.Save(fresh);
            Console.WriteLine("Every setting is back to its default.");
            return 0;
        }

        var entry = SettingsStore.Find(settings, path) ?? throw new ArgumentException($"No setting called '{path}'.");
        var defaults = SettingsStore.Find(new VxpSettings(), path)!;

        SettingsStore.Set(entry, defaults.Value);
        SettingsStore.Save(settings);

        Console.WriteLine($"{entry.Path} = {entry.Value}");
        return 0;
    }

    private static int Recent(CommandLine args, VxpSettings settings)
    {
        if (args.At(1) == "clear")
        {
            settings.RecentDiscs.Clear();
            SettingsStore.Save(settings);
            Console.WriteLine("Recent discs cleared.");
            return 0;
        }

        if (args.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(settings.RecentDiscs, DiscCommands.Json));
            return 0;
        }

        if (settings.RecentDiscs.Count == 0)
        {
            Console.WriteLine("No discs have been opened yet.");
            return 0;
        }

        foreach (var path in settings.RecentDiscs) Console.WriteLine(path);
        return 0;
    }

    private static string TypeName(SettingEntry entry)
        => entry.Type.IsEnum ? "enum"
            : entry.Type == typeof(bool) ? "bool"
            : entry.Type == typeof(int) ? "int"
            : entry.Type == typeof(double) ? "number"
            : "string";

    /// <summary>Dispatches <c>vxp bind ...</c>.</summary>
    public static int Bind(CommandLine args)
    {
        var verb = (args.At(0) ?? "list").ToLowerInvariant();
        var settings = SettingsStore.Load();
        var map = settings.BuildInputMap();

        switch (verb)
        {
            case "list":
                return ListBindings(args, map);

            case "set":
            {
                var action = RequireAction(args.At(1));
                var control = args.At(2) ?? throw new ArgumentException("bind set <action> <control>");
                if (!Binding.TryParse(control, out var binding))
                    throw new ArgumentException($"'{control}' is not a control. Try a key name such as Space, or Pad:A.");

                var conflicts = map.Conflicts(binding, action);
                map.Rebind(action, binding);
                Persist(settings, map);

                Console.WriteLine($"{action} = {map.Describe(action)}");
                if (conflicts.Count > 0)
                    Console.WriteLine($"note: {control} also triggers {string.Join(", ", conflicts)}.");

                return 0;
            }

            case "add":
            {
                var action = RequireAction(args.At(1));
                var control = args.At(2) ?? throw new ArgumentException("bind add <action> <control>");
                if (!Binding.TryParse(control, out var binding))
                    throw new ArgumentException($"'{control}' is not a control.");

                map.AddBinding(action, binding);
                Persist(settings, map);
                Console.WriteLine($"{action} = {map.Describe(action)}");
                return 0;
            }

            case "clear":
            {
                var action = RequireAction(args.At(1));
                map.Clear(action);
                Persist(settings, map);
                Console.WriteLine($"{action} = unbound");
                return 0;
            }

            case "reset":
            {
                var name = args.At(1);
                if (name is null || name == "all")
                {
                    map.ResetAll();
                    Persist(settings, map);
                    Console.WriteLine("Every control is back to its default.");
                    return 0;
                }

                var action = RequireAction(name);
                map.ResetToDefault(action);
                Persist(settings, map);
                Console.WriteLine($"{action} = {map.Describe(action)}");
                return 0;
            }

            case "keys":
                foreach (var name in KeyNames.KnownNames) Console.WriteLine(name);
                return 0;

            default:
                throw new ArgumentException($"Unknown bind command '{verb}'. Use list, set, add, clear, reset or keys.");
        }
    }

    private static int ListBindings(CommandLine args, InputMap map)
    {
        if (args.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                InputActions.All.Select(a => new
                {
                    action = a.ToString(),
                    label = InputActions.Label(a),
                    category = InputActions.Category(a).ToString(),
                    bindings = map.BindingsFor(a).Select(b => b.ToString()),
                }),
                DiscCommands.Json));

            return 0;
        }

        foreach (var category in Enum.GetValues<ActionCategory>())
        {
            var actions = InputActions.InCategory(category).ToArray();
            if (actions.Length == 0) continue;

            Console.WriteLine($"[{category}]");
            var width = actions.Max(a => a.ToString().Length);
            foreach (var action in actions)
                Console.WriteLine($"  {action.ToString().PadRight(width)}  {map.Describe(action)}");

            Console.WriteLine();
        }

        return 0;
    }

    private static InputAction RequireAction(string? name)
    {
        if (name is null) throw new ArgumentException("An action name is required. Run 'vxp bind list' to see them.");
        if (!InputActions.TryParse(name, out var action)) throw new ArgumentException($"No action called '{name}'.");
        return action;
    }

    private static void Persist(VxpSettings settings, InputMap map)
    {
        settings.StoreInputMap(map);
        SettingsStore.Save(settings);
    }
}
