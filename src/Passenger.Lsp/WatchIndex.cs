using Passenger.Core;

namespace Passenger.Lsp;

/// <summary>A server's file watchers (<see cref="FileWatch"/>) by the folder each is under, so those that may match a path are
/// found from the path's own folders (those of no folder, and of each folder the path lies under) and those under a folder
/// from the folders in order, never a look at every watcher: a change, or a folder's watcher, costs what the watchers over it
/// hold. Folders are keyed in any letter case where paths may compare so, each watcher's own folder checked as it is used;
/// each folder's watchers in the order of their patterns. Made whole from a list and never changed.</summary>
public sealed class WatchIndex
{
    public static readonly WatchIndex None = new([]);

    private static readonly StringComparer Keys = PathNames.Comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison KeysCompare = PathNames.Comparison == StringComparison.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly FileWatch[] anywhere;
    private readonly Dictionary<string, FileWatch[]>.AlternateLookup<ReadOnlySpan<char>> byFolder;

    /// <summary>Every folder, in the order <see cref="Keys"/> sorts them, so those under one lie together.</summary>
    private readonly string[] folders;

    /// <summary>The lengths of the shortest and longest folder, between which a path's folders are looked up.</summary>
    private readonly int shortest = int.MaxValue;
    private readonly int longest;

    public WatchIndex(IReadOnlyList<FileWatch> watches)
    {
        All = watches;
        List<FileWatch> none = [];
        Dictionary<string, List<FileWatch>> grouped = new(Keys);
        foreach (FileWatch watch in watches)
        {
            if (watch.Folder is not { } folder)
            {
                none.Add(watch);
                continue;
            }
            string key = Key(folder);
            if (!grouped.TryGetValue(key, out List<FileWatch>? under))
            {
                grouped[key] = under = [];
                shortest = Math.Min(shortest, key.Length);
                longest = Math.Max(longest, key.Length);
            }
            under.Add(watch);
        }
        anywhere = Ordered(none);
        Dictionary<string, FileWatch[]> made = new(grouped.Count, Keys);
        foreach ((string key, List<FileWatch> under) in grouped)
        {
            made[key] = Ordered(under);
        }
        byFolder = made.GetAlternateLookup<ReadOnlySpan<char>>();
        folders = [.. made.Keys];
        Array.Sort(folders, Keys);
    }

    /// <summary>Every watcher, as given.</summary>
    public IReadOnlyList<FileWatch> All { get; }

    /// <summary>The watchers over a path: those of no folder, then of each folder it lies under, nearest first, each
    /// alike in letter case or not (<see cref="FileWatch.Matches"/> checks its own); made nothing.</summary>
    public Over Watching(string path) => new(this, path);

    /// <summary>Whether a watcher matches a path, whatever changed there.</summary>
    public bool Matches(string path)
    {
        foreach (FileWatch watch in Watching(path))
        {
            if (watch.Matches(path))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a watcher asks for this change.</summary>
    public bool Covers(FileEvent change)
    {
        foreach (FileWatch watch in Watching(change.Path))
        {
            if (watch.Covers(change))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether only watchers of every assembly under a folder (<see cref="FileWatch.EveryAssembly"/>) ask for this
    /// change, and one does.</summary>
    public bool CoveredByEveryAssemblyAlone(FileEvent change)
    {
        bool every = false;
        foreach (FileWatch watch in Watching(change.Path))
        {
            if (watch.Covers(change))
            {
                if (!watch.EveryAssembly)
                {
                    return false;
                }
                every = true;
            }
        }
        return every;
    }

    /// <summary>The watchers that may match a path in a folder, each (folder, pattern) once: those of no folder, of the
    /// folder, of one over it and of one in it, in that order.</summary>
    public List<FileWatch> Concerning(string at)
    {
        List<FileWatch> found = [];
        Concerning(at, found);
        return found;
    }

    /// <summary><see cref="Concerning(string)"/> into a list of the caller's, emptied first.</summary>
    public void Concerning(string at, List<FileWatch> found)
    {
        found.Clear();
        Add(found, anywhere, at, above: true);
        ReadOnlySpan<char> top = at.AsSpan().TrimEnd(PathNames.Separators);
        if (byFolder.TryGetValue(top, out FileWatch[]? own))
        {
            Add(found, own, at, above: true);
        }
        foreach (FileWatch[] over in Folders(top))
        {
            Add(found, over, at, above: true);
        }
        if (top.Length + 1 < longest)
        {
            int first = Sorted.FirstNotBefore(folders.Length, (folders, at, top.Length), static (state, i) => BeforeUnder(state.folders[i], state.at.AsSpan(0, state.Length)));
            for (int i = first; i < folders.Length && IsUnderKey(folders[i], top); i++)
            {
                if (byFolder.TryGetValue(folders[i], out FileWatch[]? below))
                {
                    Add(found, below, at, above: false);
                }
            }
        }
    }

    /// <summary>Of one folder's watchers (or those of none), each (folder, pattern) once: those of no folder, and those whose
    /// folder is <paramref name="at"/> or over it where <paramref name="above"/>, else under it.</summary>
    private static void Add(List<FileWatch> found, FileWatch[] watches, string at, bool above)
    {
        FileWatch? last = null;
        foreach (FileWatch watch in watches)
        {
            if (last is not null && string.Equals(last.Pattern, watch.Pattern, StringComparison.Ordinal) && string.Equals(last.Folder, watch.Folder, StringComparison.Ordinal))
            {
                continue;
            }
            if (watch.Folder is not { } folder || (above ? PathNames.Within(at, folder) : PathNames.IsUnder(folder, at)))
            {
                found.Add(watch);
                last = watch;
            }
        }
    }

    /// <summary>Whether a folder's key sorts before every key under a folder (<paramref name="top"/> and a separator), as
    /// <see cref="Keys"/> sorts them: those under it lie together after it.</summary>
    private static bool BeforeUnder(string key, ReadOnlySpan<char> top)
    {
        int common = Math.Min(key.Length, top.Length);
        int order = key.AsSpan(0, common).CompareTo(top[..common], KeysCompare);
        return order != 0 ? order < 0 : key.Length <= top.Length || key.AsSpan(top.Length, 1).CompareTo(PathNames.Separators[..1], KeysCompare) < 0;
    }

    /// <summary>Whether a folder's key is under a folder's, in any letter case where keys are.</summary>
    private static bool IsUnderKey(string key, ReadOnlySpan<char> top) =>
        key.Length > top.Length + 1 && key.AsSpan(0, top.Length).Equals(top, KeysCompare) && key[top.Length] == PathNames.Separators[0];

    /// <summary>Whether two lists of watchers name the same (folder, pattern)s in the same order.</summary>
    public static bool Alike(IReadOnlyList<FileWatch> one, IReadOnlyList<FileWatch> other)
    {
        if (one.Count != other.Count)
        {
            return false;
        }
        for (int i = 0; i < one.Count; i++)
        {
            if (!string.Equals(one[i].Folder, other[i].Folder, StringComparison.Ordinal) || !string.Equals(one[i].Pattern, other[i].Pattern, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The watchers of each folder a path lies under, nearest first.</summary>
    private AboveFolders Folders(ReadOnlySpan<char> path) => new(this, path);

    /// <summary>A folder as a key: without a separator at its end, so the root is empty.</summary>
    private static string Key(string folder) =>
        folder.AsSpan().TrimEnd(PathNames.Separators) is var top && top.Length < folder.Length ? top.ToString() : folder;

    /// <summary>Watchers in the order of their patterns, then their folders, so those alike lie together.</summary>
    private static FileWatch[] Ordered(List<FileWatch> watches)
    {
        FileWatch[] ordered = [.. watches];
        Array.Sort(ordered, static (a, b) => string.CompareOrdinal(a.Pattern, b.Pattern) is var order and not 0 ? order : string.CompareOrdinal(a.Folder, b.Folder));
        return ordered;
    }

    /// <summary>The watchers of each folder a path lies under, nearest first.</summary>
    private ref struct AboveFolders(WatchIndex index, ReadOnlySpan<char> under)
    {
        private PathNames.FoldersAbove folders = PathNames.Above(under);

        public FileWatch[] Current { get; private set; } = [];

        public readonly AboveFolders GetEnumerator() => this;

        public bool MoveNext()
        {
            while (folders.MoveNext())
            {
                ReadOnlySpan<char> folder = folders.Current;
                if (folder.Length >= index.shortest && folder.Length <= index.longest && index.byFolder.TryGetValue(folder, out FileWatch[]? found))
                {
                    Current = found;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>The watchers over a path (<see cref="Watching"/>), one at a time.</summary>
    public ref struct Over(WatchIndex index, string under)
    {
        private AboveFolders folders = index.Folders(under);
        private FileWatch[] watches = index.anywhere;
        private int at = -1;

        public FileWatch Current { get; private set; } = null!;

        public readonly Over GetEnumerator() => this;

        public bool MoveNext()
        {
            while (++at >= watches.Length)
            {
                if (!folders.MoveNext())
                {
                    return false;
                }
                watches = folders.Current;
                at = -1;
            }
            Current = watches[at];
            return true;
        }
    }
}
