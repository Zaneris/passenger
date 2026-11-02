using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>The user folders a Godot Passenger starts keeps its settings, caches, logs and each project's user:// in: the
/// person's own, unless the window is given folders of its own or <see cref="HomeVariable"/> names a folder for them (a test
/// run's), which every Godot the window starts then takes: the game Run, Debug or Profile starts, Godot's editor, GDScript's
/// headless editor, GdUnit4's and GUT's tests, and the editor settings Passenger writes for Godot.</summary>
public static class GodotUserFolders
{
    /// <summary>The variable naming a folder under which every Godot Passenger starts keeps its user folders in place of the
    /// person's: a test run's.</summary>
    [IdentifierHome]
    public const string HomeVariable = "PASSENGER_GODOT_HOME";

    /// <summary>The variables that keep a Godot's user folders under <paramref name="home"/>: HOME (and XDG's) on macOS and
    /// Linux, APPDATA and LOCALAPPDATA on Windows.</summary>
    public static Dictionary<string, string> Under(string home)
    {
        string full = Directory.CreateDirectory(Path.GetFullPath(home)).FullName;
        return OperatingSystem.IsWindows()
            ? new() { ["APPDATA"] = Path.Combine(full, "Roaming"), ["LOCALAPPDATA"] = Path.Combine(full, "Local") }
            : new()
            {
                ["HOME"] = full,
                ["XDG_CONFIG_HOME"] = Path.Combine(full, ".config"),
                ["XDG_DATA_HOME"] = Path.Combine(full, ".local", "share"),
                ["XDG_CACHE_HOME"] = Path.Combine(full, ".cache"),
            };
    }

    /// <summary>The user folders <see cref="HomeVariable"/> names, or null where it names none.</summary>
    public static IReadOnlyDictionary<string, string>? OfVariable() =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? Under(home) : null;

    /// <summary>A Godot's environment with the user folders' variables it does not set itself.</summary>
    public static IReadOnlyDictionary<string, string> Into(IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string>? folders)
    {
        // Windows reads a variable's name in any case.
        StringComparison names = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Sets(string name) => environment.Keys.Any(k => string.Equals(k, name, names));
        if (folders is null || folders.Keys.All(Sets))
        {
            return environment;
        }
        Dictionary<string, string> merged = new(environment, StringComparer.Ordinal);
        foreach ((string name, string value) in folders.Where(f => !Sets(f.Key)))
        {
            merged[name] = value;
        }
        return merged;
    }

    /// <summary>A variable as a Godot given these folders finds it: theirs, else the IDE's own.</summary>
    public static string? Variable(IReadOnlyDictionary<string, string>? folders, string name) =>
        folders is not null && folders.TryGetValue(name, out string? given) ? given : Environment.GetEnvironmentVariable(name);
}
