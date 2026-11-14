namespace Passenger.App.Welcome;

/// <summary>One entry of what the system lists of Passenger outside its windows (the taskbar's jump list, the Dock's
/// menu): its words, the tip the system shows beside them, and the arguments Passenger is started with when it is chosen, which a
/// running Passenger takes as a second start's.</summary>
public sealed record RecentShortcut(string Title, string Tip, IReadOnlyList<string> Arguments)
{
    /// <summary>The recent folders, solutions and projects, given newest first by path and name, each a start of Passenger
    /// naming its path alone, those <paramref name="exists"/> finds gone left out; read as taken, so a list showing a few
    /// looks on disk only for those.</summary>
    public static IEnumerable<(string Path, RecentShortcut Shortcut)> Recent(IEnumerable<(string Path, string Name)> newest, Func<string, bool> exists) =>
        newest.Where(r => exists(r.Path)).Select(r => (r.Path, new RecentShortcut(r.Name, r.Path, [r.Path])));

    /// <summary>The recent ones a list shows together, titled so none reads as another: one whose name another shares is
    /// followed by the folder it is in, as few of the folder's last names as tell it from the other folders, and its own
    /// name too where another is in the same folder. <paramref name="shown"/> gives a path as the person reads it (from ~).</summary>
    public static IReadOnlyList<RecentShortcut> ToldApart(IReadOnlyList<(string Path, RecentShortcut Shortcut)> listed, Func<string, string> shown)
    {
        string[] titles = [.. listed.Select(entry => entry.Shortcut.Title)];
        int[][] named = [.. Enumerable.Range(0, listed.Count).GroupBy(i => titles[i], StringComparer.Ordinal).Select(same => same.ToArray()).Where(same => same.Length > 1)];
        foreach (int[] alike in named)
        {
            string name = titles[alike[0]];
            string[] paths = [.. alike.Select(i => shown(listed[i].Path))];
            string[] folders = [.. paths.Select(FolderOf)];
            string[] wheres = new string[alike.Length];
            for (int each = 0; each < alike.Length; each++)
            {
                string folder = folders[each];
                string[] elsewhere = [.. folders.Where(other => !string.Equals(other, folder, StringComparison.Ordinal))];
                int names = 1;
                while (!Tail(folder, names).Whole && elsewhere.Any(other => Tail(other, names).Text == Tail(folder, names).Text))
                {
                    names++;
                }
                wheres[each] = folders.Length - elsewhere.Length > 1 ? Tail(paths[each], names + 1).Text : Tail(folder, names).Text;
            }
            for (int each = 0; each < alike.Length; each++)
            {
                // A folder's last names can read as another's path's; their whole paths never do.
                bool twice = wheres.Where((told, other) => other != each && told == wheres[each]).Any();
                titles[alike[each]] = $"{name} – {(twice ? paths[each] : wheres[each])}";
            }
        }
        return [.. listed.Select((entry, i) => entry.Shortcut with { Title = titles[i] })];
    }

    /// <summary>The last <paramref name="names"/> names of a path as it writes them, or the whole of one with no more.</summary>
    private static (string Text, bool Whole) Tail(string path, int names)
    {
        int end = path.Length;
        while (end > 1 && IsSeparator(path[end - 1]))
        {
            end--;
        }
        int start = end;
        for (int taken = 0; taken < names; taken++)
        {
            start = path.AsSpan(0, start).LastIndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (start <= 0)
            {
                return (path, true);
            }
        }
        return (path[(start + 1)..end], false);
    }

    /// <summary>The folder a path is in, as written, or the path itself at a root.</summary>
    private static string FolderOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } folder ? folder : path;

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
