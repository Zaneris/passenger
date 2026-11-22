using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Welcome;

/// <summary>The folders and solutions opened lately, by canonical path (a solution file, a project opened alone, or a
/// folder without either): what the Welcome screen and File, Recent Solutions list, and those still open in a window as
/// Passenger last quit, which a start reopens. Kept by path rather than as a list, so two windows' openings and
/// removals merge entry by entry.</summary>
public sealed record RecentSolutionsState : IStateDocument<RecentSolutionsState>
{
    /// <summary>How many are kept, the oldest dropped past it.</summary>
    public const int Limit = 50;

    public Dictionary<string, RecentSolution> Solutions { get; init; } = [];

    /// <summary>Version 2 marks which solutions are open; in an older file, the newest is.</summary>
    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, MarkNewestOpen];

    /// <summary>The newest first, each with its path.</summary>
    public IReadOnlyList<(string Path, RecentSolution Entry)> Newest() =>
        [.. Solutions.OrderByDescending(s => s.Value.Opened).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => (s.Key, s.Value))];

    /// <summary>Those a window had open as Passenger last quit, or has open now, oldest first, so the newest opens last
    /// and in front.</summary>
    public IReadOnlyList<(string Path, RecentSolution Entry)> StillOpen() => [.. Newest().Where(s => s.Entry.Open).Reverse()];

    /// <summary>With a path opened as asked for <paramref name="at"/>, and open.</summary>
    public RecentSolutionsState Opened(string path, string name, DateTimeOffset at)
    {
        Dictionary<string, RecentSolution> next = new(Solutions)
        {
            [path] = new RecentSolution { Name = name, Opened = at, Open = true },
        };
        return this with { Solutions = Kept(next) };
    }

    /// <summary>When a path asked for <paramref name="now"/> is kept as opened: then, but later than every one kept and than
    /// <paramref name="after"/>, one asked for before it and not kept yet, even on a clock that has not moved since.</summary>
    public DateTimeOffset Next(DateTimeOffset now, DateTimeOffset after)
    {
        DateTimeOffset latest = Solutions.Values.Select(s => s.Opened).Append(after).Max();
        return now > latest ? now : latest.AddTicks(1);
    }

    /// <summary>With a path no window has open any longer (its window closed alone, or another workspace opened in it), so
    /// a start leaves it closed; kept among the recent ones.</summary>
    public RecentSolutionsState Closed(string path)
    {
        if (!Solutions.TryGetValue(path, out RecentSolution? entry) || !entry.Open)
        {
            return this;
        }
        return this with { Solutions = new(Solutions) { [path] = entry with { Open = false } } };
    }

    private static void MarkNewestOpen(JsonObject document)
    {
        if (document["solutions"] is not JsonObject solutions)
        {
            return;
        }
        JsonObject? newest = null;
        DateTimeOffset latest = DateTimeOffset.MinValue;
        foreach ((string _, JsonNode? entry) in solutions)
        {
            if (entry is JsonObject each && each["opened"] is JsonValue opened && opened.TryGetValue(out string? text)
                && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset when)
                && when >= latest)
            {
                latest = when;
                newest = each;
            }
        }
        if (newest is not null)
        {
            newest["open"] = true;
        }
    }

    public RecentSolutionsState Without(string path)
    {
        Dictionary<string, RecentSolution> next = new(Solutions);
        next.Remove(path);
        return this with { Solutions = next };
    }

    /// <summary>An entry without a path is left out, a name missing is the path's own, and only the newest are kept.</summary>
    public RecentSolutionsState Clamped() => this with
    {
        Solutions = Kept((Solutions ?? []).Where(s => !string.IsNullOrEmpty(s.Key) && s.Value is not null)
            .ToDictionary(s => s.Key, s => s.Value with { Name = string.IsNullOrEmpty(s.Value.Name) ? Path.GetFileName(s.Key.TrimEnd('/', '\\')) : s.Value.Name })),
    };

    private static Dictionary<string, RecentSolution> Kept(Dictionary<string, RecentSolution> all) =>
        all.Count <= Limit ? all : all.OrderByDescending(s => s.Value.Opened).Take(Limit).ToDictionary(s => s.Key, s => s.Value);
}

/// <summary>One recent folder or solution: its name as the window showed it, when it was last opened, and whether a
/// window still had it open as Passenger last quit.</summary>
public sealed record RecentSolution
{
    public string Name { get; init; } = string.Empty;

    public DateTimeOffset Opened { get; init; }

    public bool Open { get; init; }
}

/// <summary>A recent folder or solution as a list shows it: where it is, its name, its path with the home folder as ~,
/// and whether it is gone from disk.</summary>
public sealed record RecentRow(string Path, string Name, string Shown, bool Missing)
{
    public static RecentRow Of(string path, RecentSolution entry) =>
        new(path, entry.Name, Home(path), !Directory.Exists(path) && !File.Exists(path));

    /// <summary>A path under the home folder, shown from ~.</summary>
    public static string Home(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && Core.PathNames.IsUnder(path, home) ? "~" + path[home.TrimEnd('/', '\\').Length..] : path;
    }
}
