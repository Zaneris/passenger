namespace Passenger.Tests;

/// <summary>The person's own Godot user folders, which no test run writes: every Godot the run's Passengers start keeps its
/// user folders in the run's (<see cref="TestRun"/>). What is under them is noted as the run begins and looked at again
/// after each test (<see cref="TestsLeaveNothingBehindAttribute"/>) and as the run ends, and a change fails the test it came
/// in, or the run after its last test, naming each file and folder added, changed or removed. They are Application
/// Support's and Caches' Godot on macOS; XDG's data, config and cache folders' godot (else ~/.local/share's, ~/.config's
/// and ~/.cache's) on Linux; APPDATA's and LOCALAPPDATA's Godot on Windows.</summary>
public static class PersonsGodot
{
    /// <summary>The most changes a failure names, the rest counted.</summary>
    private const int Named = 20;

    /// <summary>The folders, as the person's environment names them as the run begins.</summary>
    public static IReadOnlyList<string> Folders { get; } = Of(Environment.GetEnvironmentVariable);

    private static readonly Lock Gate = new();

    /// <summary>Each file and folder as the last look found it.</summary>
    private static Dictionary<string, Entry> seen = [];

    /// <summary>The folders a Godot keeps the user's settings, data and caches in, by the variables that place them.</summary>
    internal static IReadOnlyList<string> Of(Func<string, string?> variable)
    {
        string home = variable("HOME") is { Length: > 0 } set ? set : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return [.. ((string[])["APPDATA", "LOCALAPPDATA"]).Select(variable).OfType<string>().Where(Path.IsPathRooted).Select(folder => Path.Combine(folder, "Godot"))];
        }
        if (OperatingSystem.IsMacOS())
        {
            return [Path.Combine(home, "Library", "Application Support", "Godot"), Path.Combine(home, "Library", "Caches", "Godot")];
        }
        // Godot passes over an XDG folder that is not a full path, as it does one unset.
        string Xdg(string name, string fallback) => variable(name) is { Length: > 0 } given && Path.IsPathRooted(given) ? given : Path.Combine(home, fallback);
        return [Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "godot"), Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "godot"),
            Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), "godot")];
    }

    /// <summary>Notes what is under the folders as the run begins.</summary>
    internal static void Begin()
    {
        Dictionary<string, Entry> now = Read() ?? [];
        lock (Gate)
        {
            seen = now;
        }
    }

    /// <summary>Why the run fails where the folders changed since the last look (each change named, <paramref name="when"/>
    /// saying when it came); null where none did. What it found is the next look's start.</summary>
    public static string? Written(string when)
    {
        if (Read() is not { } now)
        {
            return null;
        }
        List<string> changes;
        lock (Gate)
        {
            HashSet<string> added = [.. now.Keys.Except(seen.Keys)];
            HashSet<string> removed = [.. seen.Keys.Except(now.Keys)];
            // A folder an entry came to or went from is named by that entry.
            HashSet<string> holding = [.. added.Concat(removed).Select(Path.GetDirectoryName).OfType<string>()];
            changes = [.. Whole(added, "added"),
                .. now.Where(e => seen.TryGetValue(e.Key, out Entry was) && was != e.Value && !(e.Value.Length < 0 && holding.Contains(e.Key)))
                    .Select(e => e.Key).Order(StringComparer.Ordinal).Select(path => now[path].Length < 0 ? $"wrote into {path}" : $"changed {path}"),
                .. Whole(removed, "removed")];
            seen = now;
        }
        if (changes.Count == 0)
        {
            return null;
        }
        string named = string.Join("; ", changes.Take(Named)) + (changes.Count > Named ? $"; and {changes.Count - Named} more" : string.Empty);
        return $"The person's own Godot user folders were written {when} (by a Godot the run started in the person's, or by another program meanwhile: "
            + $"Godot run by hand, another test run): {named}. Every Godot a test's Passenger starts keeps its user folders in the run's own, "
            + $"which {Passenger.App.Godot.GodotUserFolders.HomeVariable} names, unless its window, its run configuration or its test host gives it a test's";
    }

    /// <summary>Paths added or removed, a folder added or removed whole named alone with how much it held.</summary>
    private static IEnumerable<string> Whole(HashSet<string> paths, string verb) => paths
        .Where(path => Path.GetDirectoryName(path) is not { } parent || !paths.Contains(parent))
        .Order(StringComparer.Ordinal)
        .Select(path => paths.Count(p => p.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal)) is var under and > 0
            ? $"{verb} {path} and {under} under it" : $"{verb} {path}");

    /// <summary>Every file and folder under the folders, the folders themselves among them; null where one could not be read
    /// whole (a folder removed as it was read) three times running, which this look then passes over.</summary>
    private static Dictionary<string, Entry>? Read()
    {
        for (int tries = 0; tries < 3; tries++)
        {
            try
            {
                Dictionary<string, Entry> found = new(StringComparer.Ordinal);
                foreach (DirectoryInfo folder in Folders.Select(f => new DirectoryInfo(f)).Where(f => f.Exists))
                {
                    found[folder.FullName] = new(-1, folder.LastWriteTimeUtc);
                    foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                    {
                        found[entry.FullName] = new(entry is FileInfo file ? file.Length : -1, entry.LastWriteTimeUtc);
                    }
                }
                return found;
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                // Read again: something wrote there as it was read, which the next look names.
            }
        }
        return null;
    }

    /// <summary>A file's length (-1 for a folder) and last write.</summary>
    private readonly record struct Entry(long Length, DateTime Written);
}
