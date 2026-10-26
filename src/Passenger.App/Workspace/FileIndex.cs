using System.IO.Enumeration;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>Every file under the open folder, by relative path, for Go to File and Find in Files. Built off the UI
/// thread when the folder opens, by the walk every look at the open folder's files takes (<see cref="FolderWalk"/>): build
/// output, dependencies, version control, the system's own files and a save's temporary file (gone once the save is done,
/// which walks nothing again) left out, a linked folder never walked twice or out of the folder. What comes or goes after
/// is put in or taken out in place (<see cref="Apply"/>), only a folder made walked, so a file made costs the same in a
/// folder of any size. Files are kept in the walk's order (<see cref="WalkOrder"/>), which a search reads them in.</summary>
public sealed class FileIndex
{
    /// <summary>The most files indexed, so a folder that is really a disk does not hold the IDE up.</summary>
    private const int Limit = 200_000;

    /// <summary>The first <see cref="count"/> are every file, by its path from the root in the walk's order, changed in place
    /// on the UI thread; <see cref="letters"/> holds each one's letters at the same place.</summary>
    private string[] files = [];

    private PathLetters[] letters = [];
    private int count;

    /// <summary>The most separators in a file's path, which bounds the look for what was under a folder gone.</summary>
    private int deepest;

    /// <summary>Whether the walk stopped at <see cref="Limit"/>, so a file come may sort among those it never listed.</summary>
    private bool cut;

    /// <summary>What <see cref="Files"/> handed out since the files last changed.</summary>
    private string[]? shown;

    /// <summary>Walks begun (and closes), by which a landing finds itself overtaken.</summary>
    private int walks;

    /// <summary>How many of the newest walk's walk and changes have not landed.</summary>
    private int waiting;

    /// <summary>Done once the last walk or change asked for has landed, which the next change lands after.</summary>
    private Task landed = Task.CompletedTask;

    public string? Root { get; private set; }

    public int Count => count;

    /// <summary>Every file indexed, by its path from the root, as it stands now.</summary>
    public IReadOnlyList<string> Files => shown ??= files.AsSpan(0, count).ToArray();

    /// <summary>Whether a walk or a change is under way that has not landed: an agent's search waits for it, as the folder
    /// opens or after a file came.</summary>
    public bool IsWalking => waiting > 0;

    /// <summary>The newest walk landed (its list, or the one before when it failed), on the UI thread: what else looks for files
    /// among the folder's (its single-file AppHosts) takes them from it rather than walking the folder again.</summary>
    public event EventHandler? Walked;

    /// <summary>Files put in and taken out since a walk landed, by their paths from the root, on the UI thread.</summary>
    public event EventHandler<(IReadOnlyList<string> Came, IReadOnlyList<string> Gone)>? Changed;

    /// <summary>Walks the folder again off the UI thread. The files found before stay searchable until the walk is done,
    /// and only the newest walk's list lands; another folder's list goes at once.</summary>
    public void Build(string root)
    {
        if (Root != root)
        {
            Empty();
        }
        Root = root;
        int walk = ++walks;
        waiting = 1;
        Then(walk, BackgroundWork.Unheld(() => Listing.Of(Walk(root))), Task.CompletedTask, listing =>
        {
            if (listing is not null)
            {
                (files, letters, count, deepest, cut) = (listing.Files, listing.Letters, listing.Count, listing.Deepest, listing.Cut);
                shown = null;
            }
            Walked?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Files and folders the folder watcher saw come or go, by their canonical paths: each taken out with what was
    /// under it, then put back as the disk now has it, a folder made walked; read off the UI thread and landed after what was
    /// asked before. Another folder, or one whose walk stopped at the limit, is walked again whole.</summary>
    public void Apply(string root, IReadOnlyCollection<string> comeOrGone)
    {
        if (Root != root || cut)
        {
            Build(root);
            return;
        }
        if (comeOrGone.Count == 0)
        {
            return;
        }
        int walk = walks;
        waiting++;
        Then(walk, BackgroundWork.Unheld(() => Change.Read(root, comeOrGone)), landed, change =>
        {
            // A walk that stopped at the limit may have landed meanwhile.
            if (cut || change is not { Whole: false } || !Applied(change))
            {
                Build(root);
            }
        });
    }

    /// <summary>No folder's files, a walk under way dropped: the folder was closed.</summary>
    public void Clear()
    {
        walks++;
        Root = null;
        Empty();
        waiting = 0;
    }

    private void Empty()
    {
        files = [];
        letters = [];
        count = deepest = 0;
        cut = false;
        shown = null;
    }

    /// <summary>Lands what was read on the UI thread once <paramref name="after"/> has, unless a walk or a close came since,
    /// as background work until then; <paramref name="land"/> is given null for a read that failed.</summary>
    private void Then<T>(int walk, Task<T> read, Task after, Action<T?> land) where T : class
    {
        // Held through the read, then by the crossing, which lets go of what it can never deliver.
        IDisposable held = BackgroundWork.Scope();
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        landed = done.Task;
        IdeErrors.Observe(read);
        IdeErrors.Observe(Task.WhenAll(after, read).ContinueWith(_ =>
        {
            UiThread.Post(() =>
            {
                try
                {
                    if (walk == walks)
                    {
                        waiting--;
                        land(read.IsCompletedSuccessfully ? read.Result : null);
                    }
                }
                finally
                {
                    Let();
                }
            }, Let);
        }, TaskScheduler.Default));

        void Let()
        {
            done.TrySetResult();
            held.Dispose();
        }
    }

    /// <summary>The best matches for a query, best first (the highest score, then the shortest path, then by name), as
    /// relative paths. A file without one of the query's letters is passed over unscored; only the best
    /// <paramref name="limit"/> are kept as the rest are scored, the worst of them first in line to go, since a first
    /// letter matches most of a large folder.</summary>
    public IReadOnlyList<string> Search(string query, int limit = 60)
    {
        string trimmed = query.Trim();
        if (limit <= 0)
        {
            return [];
        }
        PriorityQueue<string, (int Score, string Path)> best = new(limit + 1, WorstFirst.Instance);
        ulong wanted = FuzzyMatch.Wanted(trimmed);
        for (int i = 0; i < count; i++)
        {
            string path = files[i];
            if (FuzzyMatch.Score(trimmed, wanted, path, letters[i]) is not { } score)
            {
                continue;
            }
            if (best.Count < limit)
            {
                best.Enqueue(path, (score, path));
            }
            else if (best.TryPeek(out _, out (int Score, string Path) worst) && WorstFirst.Instance.Compare((score, path), worst) > 0)
            {
                best.DequeueEnqueue(path, (score, path));
            }
        }
        string[] found = new string[best.Count];
        for (int i = found.Length - 1; i >= 0; i--)
        {
            found[i] = best.Dequeue();
        }
        return found;
    }

    /// <summary>Matches in the order they go from a list of the best: a lower score, then a longer path, then later by name.</summary>
    private sealed class WorstFirst : IComparer<(int Score, string Path)>
    {
        public static readonly WorstFirst Instance = new();

        public int Compare((int Score, string Path) x, (int Score, string Path) y)
        {
            int byScore = x.Score.CompareTo(y.Score);
            if (byScore != 0)
            {
                return byScore;
            }
            int byLength = y.Path.Length.CompareTo(x.Path.Length);
            return byLength != 0 ? byLength : StringComparer.OrdinalIgnoreCase.Compare(y.Path, x.Path);
        }
    }

    /// <summary>Takes out what a change found gone and puts in what it found come, in place; false when that would pass the
    /// limit, which a walk keeps to.</summary>
    private bool Applied(Change change)
    {
        List<(int Start, int End)> runs = [];
        foreach (string gone in change.Gone)
        {
            int at = Array.BinarySearch(files, 0, count, gone, WalkOrder.Instance);
            if (at >= 0)
            {
                runs.Add((at, at + 1));
            }
            int depth = WalkOrder.Depth(gone);
            for (int level = depth + 1; level <= deepest; level++)
            {
                int start = FirstUnder(gone, level);
                int end = start;
                while (end < count && WalkOrder.Depth(files[end]) == level && WalkOrder.IsUnder(files[end], gone))
                {
                    end++;
                }
                if (end > start)
                {
                    runs.Add((start, end));
                }
            }
        }
        List<string> came = [];
        List<PathLetters> read = [];
        for (int i = 0; i < change.Came.Count; i++)
        {
            // Those already listed and not taken out stay where they are.
            if ((i == 0 || change.Came[i] != change.Came[i - 1]) && !Kept(change.Came[i], runs))
            {
                came.Add(change.Came[i]);
                read.Add(change.Letters[i]);
            }
        }
        if (count - Taken(runs) + came.Count > Limit)
        {
            return false;
        }
        List<string> removed = TakeOut(runs);
        PutIn(came, read);
        if (came.Count > 0 || removed.Count > 0)
        {
            shown = null;
            Changed?.Invoke(this, (came, removed));
        }
        return true;
    }

    /// <summary>Whether a file is listed now and stays: not in a run about to be taken out.</summary>
    private bool Kept(string file, List<(int Start, int End)> runs)
    {
        int at = Array.BinarySearch(files, 0, count, file, WalkOrder.Instance);
        if (at < 0)
        {
            return false;
        }
        foreach ((int start, int end) in runs)
        {
            if (at >= start && at < end)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The first file at a depth that is under a folder or after it in the walk's order, as the files of each
    /// depth under one folder lie together.</summary>
    private int FirstUnder(string folder, int level)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (WalkOrder.BeforeUnder(files[middle], folder, level))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static int Taken(List<(int Start, int End)> runs)
    {
        int taken = 0;
        int reached = 0;
        foreach ((int start, int end) in Sorted(runs))
        {
            taken += Math.Max(0, end - Math.Max(start, reached));
            reached = Math.Max(reached, end);
        }
        return taken;
    }

    private static List<(int Start, int End)> Sorted(List<(int Start, int End)> runs)
    {
        runs.Sort(static (x, y) => x.Start.CompareTo(y.Start));
        return runs;
    }

    /// <summary>Takes out the files of the runs, moving the rest up in one pass; those taken out, in order.</summary>
    private List<string> TakeOut(List<(int Start, int End)> runs)
    {
        List<string> removed = [];
        int write = 0;
        int read = 0;
        foreach ((int start, int end) in Sorted(runs))
        {
            int from = Math.Max(start, read);
            if (from >= end)
            {
                continue;
            }
            Move(read, write, from - read);
            write += from - read;
            for (int i = from; i < end; i++)
            {
                removed.Add(files[i]);
            }
            read = end;
        }
        if (removed.Count == 0)
        {
            return removed;
        }
        Move(read, write, count - read);
        write += count - read;
        Array.Clear(files, write, count - write);
        count = write;
        return removed;
    }

    private void Move(int from, int to, int length)
    {
        if (length > 0 && from != to)
        {
            Array.Copy(files, from, files, to, length);
            Array.Copy(letters, from, letters, to, length);
        }
    }

    /// <summary>Puts files in their places in the walk's order, sorted already, in one pass from the end, the arrays grown
    /// with room to spare only when they are full.</summary>
    private void PutIn(List<string> came, List<PathLetters> read)
    {
        if (came.Count == 0)
        {
            return;
        }
        int total = count + came.Count;
        if (total > files.Length)
        {
            int size = Spare(total);
            Array.Resize(ref files, size);
            Array.Resize(ref letters, size);
        }
        int i = count - 1;
        int j = came.Count - 1;
        for (int write = total - 1; j >= 0; write--)
        {
            if (i >= 0 && WalkOrder.Instance.Compare(files[i], came[j]) > 0)
            {
                files[write] = files[i];
                letters[write] = letters[i];
                i--;
            }
            else
            {
                files[write] = came[j];
                letters[write] = read[j];
                deepest = Math.Max(deepest, WalkOrder.Depth(came[j]));
                j--;
            }
        }
        count = total;
    }

    /// <summary>Room for so many files and some to come.</summary>
    private static int Spare(int files) => files + files / 8 + 16;

    /// <summary>A walk's files in the walk's order with their letters, in arrays with room to spare.</summary>
    private sealed record Listing(string[] Files, PathLetters[] Letters, int Count, int Deepest, bool Cut)
    {
        public static Listing Of(List<string> walked)
        {
            string[] files = new string[Spare(walked.Count)];
            walked.CopyTo(files);
            // A link reached before the folder it leads to lists that folder's files earlier than its own place.
            for (int i = 1; i < walked.Count; i++)
            {
                if (WalkOrder.Instance.Compare(files[i - 1], files[i]) > 0)
                {
                    Array.Sort(files, 0, walked.Count, WalkOrder.Instance);
                    break;
                }
            }
            PathLetters[] letters = new PathLetters[files.Length];
            int deepest = 0;
            for (int i = 0; i < walked.Count; i++)
            {
                letters[i] = PathLetters.Of(files[i]);
                deepest = Math.Max(deepest, WalkOrder.Depth(files[i]));
            }
            return new(files, letters, walked.Count, deepest, walked.Count >= Limit);
        }
    }

    /// <summary>What came or went, as the disk has it now: the paths named (each with what was under it), and the files
    /// there now among them and in the folders made, sorted in the walk's order with their letters; or the whole folder to
    /// walk again.</summary>
    private sealed record Change(List<string> Gone, List<string> Came, List<PathLetters> Letters, bool Whole)
    {
        private static readonly Change Again = new([], [], [], Whole: true);

        /// <summary>Lists each folder named once, keeping only the entries named, and walks each folder among them as the
        /// walk of the whole folder would. A worker's.</summary>
        public static Change Read(string root, IReadOnlyCollection<string> comeOrGone)
        {
            string top = PathNames.Canonical(root);
            List<string> gone = [];
            Dictionary<string, HashSet<string>> named = new(PathNames.Comparer);
            foreach (string path in comeOrGone)
            {
                ReadOnlySpan<char> relative = PathNames.Below(path, top);
                // A short name on Windows may stand for a file listed by its long one.
                if (relative.IsEmpty && PathNames.Same(path, top) || OperatingSystem.IsWindows() && relative.Contains('~'))
                {
                    return Again;
                }
                if (relative.IsEmpty)
                {
                    continue;
                }
                gone.Add(relative.ToString());
                string folder = Path.GetDirectoryName(path)!;
                if (!named.TryGetValue(folder, out HashSet<string>? names))
                {
                    named[folder] = names = new(PathNames.Comparer);
                }
                names.Add(Path.GetFileName(path));
            }
            List<string> came = [];
            foreach ((string folder, HashSet<string> names) in named)
            {
                if (!PathNames.Same(folder, top) && IgnoredFolders.AnyIn(top, PathNames.Below(folder, top)))
                {
                    continue;
                }
                Listed(top, folder, names, came);
            }
            came.Sort(WalkOrder.Instance);
            List<PathLetters> letters = new(came.Count);
            foreach (string file in came)
            {
                letters.Add(PathLetters.Of(file));
            }
            return new(gone, came, letters, Whole: false);
        }

        /// <summary>The entries of a folder that are named, as its listing has them: a file as the walk names it, a folder
        /// (but a link, whose files the walk lists where it leads) with every file under it.</summary>
        private static void Listed(string top, string folder, HashSet<string> names, List<string> came)
        {
            HashSet<string>.AlternateLookup<ReadOnlySpan<char>> asked = names.GetAlternateLookup<ReadOnlySpan<char>>();
            try
            {
                // Made, the listing opens the folder.
                FileSystemEnumerable<(string Name, bool Folder, bool Link)> entries = new(folder,
                    static (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory, entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0),
                    Options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => asked.Contains(entry.FileName),
                };
                foreach ((string name, bool isFolder, bool link) in entries)
                {
                    string path = Path.Join(folder, name);
                    if (!isFolder)
                    {
                        if (!PathNames.IsSystemFile(name) && !FileText.IsSaving(name))
                        {
                            came.Add(PathNames.Below(path, top).ToString());
                        }
                    }
                    else if (!link && !IgnoredFolders.AnyIn(top, PathNames.Below(path, top)))
                    {
                        came.AddRange(FolderWalk.Below(top, path));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or shut: nothing of it is listed.
            }
        }

        /// <summary>Every entry, as the walk lists a folder.</summary>
        private static readonly EnumerationOptions Options = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
    }

    /// <summary>The walk's order of files by their paths from the root: the shallower first, as the walk takes a folder's
    /// folders after every folder above them, then name by name, each compared as the walk sorts names.</summary>
    private sealed class WalkOrder : IComparer<string>
    {
        public static readonly WalkOrder Instance = new();

        private static readonly char Separator = System.IO.Path.DirectorySeparatorChar;

        public int Compare(string? x, string? y)
        {
            ReadOnlySpan<char> a = x;
            ReadOnlySpan<char> b = y;
            int byDepth = a.Count(Separator).CompareTo(b.Count(Separator));
            if (byDepth != 0)
            {
                return byDepth;
            }
            while (true)
            {
                int ia = a.IndexOf(Separator);
                int ib = b.IndexOf(Separator);
                int byName = (ia < 0 ? a : a[..ia]).SequenceCompareTo(ib < 0 ? b : b[..ib]);
                if (byName != 0 || ia < 0 || ib < 0)
                {
                    return byName;
                }
                a = a[(ia + 1)..];
                b = b[(ib + 1)..];
            }
        }

        public static int Depth(string path) => path.AsSpan().Count(Separator);

        public static bool IsUnder(string path, string folder) =>
            path.Length > folder.Length && path[folder.Length] == Separator && path.StartsWith(folder, StringComparison.Ordinal);

        /// <summary>Whether a file comes before every file at a depth under a folder: shallower, or of that depth and before
        /// the folder's names along its own.</summary>
        public static bool BeforeUnder(string path, string folder, int level)
        {
            int depth = Depth(path);
            if (depth != level)
            {
                return depth < level;
            }
            ReadOnlySpan<char> a = path;
            ReadOnlySpan<char> b = folder;
            while (true)
            {
                int ia = a.IndexOf(Separator);
                int ib = b.IndexOf(Separator);
                int byName = a[..ia].SequenceCompareTo(ib < 0 ? b : b[..ib]);
                if (byName != 0 || ib < 0)
                {
                    return byName < 0;
                }
                a = a[(ia + 1)..];
                b = b[(ib + 1)..];
            }
        }
    }

    /// <summary>The files under a folder, by their paths from it, left out and limited as the index's are: what Find in
    /// Files reads of a directory outside the open folder.</summary>
    internal static List<string> Walk(string root)
    {
        string top = PathNames.Canonical(root);
        List<string> found = [];
        foreach (string file in FolderWalk.Below(top))
        {
            if (found.Count == Limit)
            {
                break;
            }
            found.Add(file);
        }
        return found;
    }
}
