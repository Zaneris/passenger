using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>The recent folders and solutions as the system's lists beside Passenger's icon show them (Windows' jump list,
/// macOS's Dock menu): the newest still on disk, each by its name, and on Windows each a link whose arguments name its
/// path, by which a link the person removed is known again.</summary>
internal static class RecentLists
{
    /// <summary>How many a list shows at most.</summary>
    public const int Most = 10;

    /// <summary>The newest (path, name) still on disk, up to <see cref="Most"/>, each titled by its name, a name two
    /// share followed by the path <paramref name="shown"/> gives, so the two can be told apart.</summary>
    public static IReadOnlyList<(string Path, string Title)> Titled(IEnumerable<(string Path, string Name)> newest, Func<string, bool> exists, Func<string, string> shown)
    {
        List<(string Path, string Name)> kept = [.. newest.Where(e => exists(e.Path)).Take(Most)];
        return [.. kept.Select(e => (e.Path, kept.Count(o => o.Name == e.Name) > 1 ? $"{e.Name} – {shown(e.Path)}" : e.Name))];
    }

    /// <summary>The arguments of a link that opens <paramref name="path"/> in Passenger.</summary>
    public static string ArgumentsOf(string path) => WindowsCommandLine.Of([path]);

    /// <summary>The paths among <paramref name="paths"/> whose links' arguments are among those removed.</summary>
    public static IReadOnlyList<string> RemovedOf(IEnumerable<string> paths, IReadOnlyCollection<string> removed) =>
        [.. paths.Where(path => removed.Contains(ArgumentsOf(path), StringComparer.Ordinal))];
}
