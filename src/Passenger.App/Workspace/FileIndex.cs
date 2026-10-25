using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>Every file under the open folder, by relative path, for Go to File and Find in Files. Built off the UI
/// thread when the folder opens, by the walk every look at the open folder's files takes (<see cref="FolderWalk"/>): build
/// output, dependencies, version control, the system's own files and a save's temporary file (gone once the save is done,
/// which walks nothing again) left out, a linked folder never walked twice or out of the folder.</summary>
public sealed class FileIndex
{
    /// <summary>The most files indexed, so a folder that is really a disk does not hold the IDE up.</summary>
    private const int Limit = 200_000;

    private IReadOnlyList<string> files = [];

    public string? Root { get; private set; }

    public int Count => files.Count;

    /// <summary>Every file indexed, by its path from the root.</summary>
    public IReadOnlyList<string> Files => files;

    /// <summary>Whether a walk is under way whose list has not landed: an agent's search waits for it, as the folder opens.</summary>
    public bool IsWalking { get; private set; }

    /// <summary>The newest walk landed (its list, or the one before when it failed), on the UI thread: what else looks for files
    /// among the folder's (its single-file AppHosts) takes them from it rather than walking the folder again.</summary>
    public event EventHandler? Walked;

    /// <summary>Walks the folder again off the UI thread. The files found before stay searchable until the walk is done,
    /// and only the newest walk's list lands; another folder's list goes at once.</summary>
    public void Build(string root)
    {
        if (Root != root)
        {
            files = [];
        }
        Root = root;
        int walk = ++walks;
        IsWalking = true;
        // Held through the walk, then by the crossing, which lets go of what it can never deliver.
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Run(() => Walk(root)).ContinueWith(walked =>
        {
            UiThread.Post(() =>
            {
                if (walked.IsCompletedSuccessfully && walk == walks)
                {
                    files = walked.Result;
                }
                if (walk == walks)
                {
                    IsWalking = false;
                    Walked?.Invoke(this, EventArgs.Empty);
                }
            });
            held.Dispose();
        }, TaskScheduler.Default));
    }

    private int walks;

    /// <summary>No folder's files, a walk under way dropped: the folder was closed.</summary>
    public void Clear()
    {
        walks++;
        Root = null;
        files = [];
        IsWalking = false;
    }

    /// <summary>The best matches for a query, best first (the highest score, then the shortest path, then by name), as
    /// relative paths. Only the best <paramref name="limit"/> are kept as the files are scored, the worst of them first
    /// in line to go, since a first letter matches most of a large folder.</summary>
    public IReadOnlyList<string> Search(string query, int limit = 60)
    {
        string trimmed = query.Trim();
        if (limit <= 0)
        {
            return [];
        }
        PriorityQueue<string, (int Score, string Path)> best = new(limit + 1, WorstFirst.Instance);
        foreach (string path in files)
        {
            if (FuzzyMatch.Score(trimmed, path) is not { } score)
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

    /// <summary>The files under a folder, by their paths from it, left out and limited as the index's are: what Find in
    /// Files reads of a directory outside the open folder.</summary>
    internal static List<string> Walk(string root)
    {
        string top = PathNames.Canonical(root);
        return [.. FolderWalk.Files(top).Take(Limit).Select(file => Path.GetRelativePath(top, file))];
    }
}
