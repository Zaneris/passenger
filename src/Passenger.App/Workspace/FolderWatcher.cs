using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Workspace;

/// <summary>What changed in a folder while it was watched: the files created, changed, renamed or deleted, whether
/// any of that changed which files there are, whether a project or solution file was among them, and whether Git's
/// own state moved (a commit or checkout made elsewhere).</summary>
public sealed record FolderChanges(IReadOnlySet<string> Paths, bool Structure, bool Projects, bool Git);

/// <summary>Watches a folder for what other programs change in it (a terminal's <c>git pull</c>, an agent's edits,
/// <c>dotnet new</c>), as Rider's fsnotifier does. Changes are gathered for a moment and handed over together on the
/// UI thread, as background work so a screenshot waits for them. Build output, dependencies and Git's folder are
/// passed over, except Git's HEAD, index and refs; where each folder is watched on its own (Linux), they are never
/// watched at all, and a folder made later is watched as it appears.</summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(150);

    private static readonly string[] ProjectExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

    private readonly string folder;
    private readonly FolderWatching watching;
    private readonly object gate = new();
    private readonly Dictionary<string, FileSystemWatcher> watches = new(StringComparer.Ordinal);
    private readonly HashSet<string> paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> ownWrites = new(StringComparer.Ordinal);

    /// <summary>The changes to which folders are watched, made one at a time in the order they were seen.</summary>
    private Task following = Task.CompletedTask;
    private bool rewalking;
    private bool structure;
    private bool projects;
    private bool git;
    private bool pending;
    private bool disposed;
    private int outOfWatches;

    public FolderWatcher(string folder, FolderWatching watching)
    {
        this.folder = folder;
        this.watching = watching;
        FileText.Writing += OnWriting;
        if (watching.EachFolder)
        {
            // Off the UI thread: a large folder has thousands of folders to start watching.
            Rewalk();
        }
        else
        {
            Start(folder, recursive: true);
        }
    }

    /// <summary>The system's watchers by the folder each watches: the whole tree's one, or one for each kept folder.</summary>
    public IReadOnlyDictionary<string, FileSystemWatcher> Watches
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, FileSystemWatcher>(watches, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>A file Passenger is saving: its events in the next moments are its content changing, not a new file.</summary>
    private void OnWriting(string path)
    {
        lock (gate)
        {
            ownWrites[PathNames.Canonical(path)] = DateTime.UtcNow;
        }
    }

    /// <summary>Raised on the UI thread with what changed since the last time.</summary>
    public event EventHandler<FolderChanges>? Changed;

    public void Dispose()
    {
        FileText.Writing -= OnWriting;
        List<FileSystemWatcher> stopped;
        lock (gate)
        {
            disposed = true;
            stopped = [.. watches.Values];
            watches.Clear();
        }
        stopped.ForEach(w => w.Dispose());
    }

    /// <summary>Starts watching a folder, or the whole tree under it; false when it is gone.</summary>
    private bool Start(string path, bool recursive)
    {
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }
            if (watches.ContainsKey(path))
            {
                return true;
            }
        }
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
        }
        catch (ArgumentException) when (!recursive)
        {
            // Deleted since it was seen.
            return false;
        }
        watcher.Created += (_, e) => OnCreated(e.FullPath);
        watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
        watcher.Changed += (_, e) => Note(e.FullPath, created: false);
        watcher.Renamed += (_, e) =>
        {
            OnDeleted(e.OldFullPath);
            OnCreated(e.FullPath);
        };
        watcher.Error += (_, e) => OnError(watcher, e.GetException());
        lock (gate)
        {
            if (disposed)
            {
                watcher.Dispose();
                return false;
            }
            watches[path] = watcher;
        }
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (ObjectDisposedException)
        {
            // The folder watcher was disposed meanwhile.
        }
        return true;
    }

    private void OnCreated(string path)
    {
        if (watching.EachFolder && Kept(path) && Directory.Exists(path))
        {
            Follow(() => WatchFrom(path));
        }
        Note(path, created: true);
    }

    private void OnDeleted(string path)
    {
        if (watching.EachFolder && IsWatched(path))
        {
            Follow(() => Unwatch(path));
        }
        Note(path, created: true);
    }

    private void OnError(FileSystemWatcher source, Exception error)
    {
        if (watching.OutOfWatches(error) is { } advice)
        {
            // Its folder went unwatched; the person hears once for the folder opened, not once for each folder.
            Follow(() => Stop(source));
            if (Interlocked.Exchange(ref outOfWatches, 1) == 0)
            {
                IdeErrors.Report(new IOException(advice, error));
            }
            return;
        }
        if (source.Path != folder && (error is DirectoryNotFoundException || !Directory.Exists(source.Path)))
        {
            // A folder watched on its own was deleted or moved: the folder above it says so, and where it went.
            Follow(() => Stop(source));
            return;
        }
        // Changes lost (too many at once) or a watch that failed: everything is read again, and walked again if lost.
        Note(folder, created: true);
        if (watching.EachFolder && error is InternalBufferOverflowException)
        {
            Rewalk();
        }
    }

    /// <summary>Changes which folders are watched off the thread that saw why, after those already asked for, held as
    /// background work until it is done, since a file made in a folder only watched from then on shows only then.</summary>
    private void Follow(Action change)
    {
        IDisposable held = BackgroundWork.Scope();
        lock (gate)
        {
            following = following.ContinueWith(_ =>
            {
                try
                {
                    change();
                }
                finally
                {
                    held.Dispose();
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Watches every kept folder not watched yet and lets go of those gone: the first walk, and the one after
    /// events were lost. Asked for once however many watchers lost theirs.</summary>
    private void Rewalk()
    {
        lock (gate)
        {
            if (rewalking)
            {
                return;
            }
            rewalking = true;
        }
        Follow(() =>
        {
            lock (gate)
            {
                rewalking = false;
            }
            foreach (string gone in Watches.Keys.Where(path => !Directory.Exists(path)))
            {
                Unwatch(gone);
            }
            WatchFrom(folder);
        });
    }

    /// <summary>Watches a kept folder and the kept folders under it, each before what is in it is read, so a folder made
    /// meanwhile is either read here or heard of from the folder above it.</summary>
    private void WatchFrom(string top)
    {
        Stack<string> folders = new([top]);
        while (folders.Count > 0)
        {
            string at = folders.Pop();
            if (!Directory.Exists(at) || new DirectoryInfo(at).LinkTarget is not null || !Start(at, recursive: false))
            {
                continue;
            }
            try
            {
                foreach (DirectoryInfo child in new DirectoryInfo(at).EnumerateDirectories())
                {
                    // A linked folder is not followed, since it can loop.
                    if (child.LinkTarget is null && Kept(child.FullName))
                    {
                        folders.Push(child.FullName);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Lets go of a folder deleted or moved away and every folder watched under it, whose watches went with it
    /// or now follow it under another name; one made again in its place is watched anew after.</summary>
    private void Unwatch(string path)
    {
        List<FileSystemWatcher> stopped = [];
        lock (gate)
        {
            foreach (string watched in watches.Keys.Where(w => PathNames.Within(w, path)).ToList())
            {
                stopped.Add(watches[watched]);
                watches.Remove(watched);
            }
        }
        stopped.ForEach(w => w.Dispose());
    }

    private bool IsWatched(string path)
    {
        lock (gate)
        {
            return watches.ContainsKey(path);
        }
    }

    /// <summary>Lets go of one watcher whose folder could not be watched or is gone, unless another has its place.</summary>
    private void Stop(FileSystemWatcher watcher)
    {
        lock (gate)
        {
            if (watches.TryGetValue(watcher.Path, out FileSystemWatcher? held) && held == watcher)
            {
                watches.Remove(watcher.Path);
            }
        }
        watcher.Dispose();
    }

    /// <summary>Whether a folder is watched where each is watched on its own: the open folder and the kept folders under
    /// it, and of Git's store only its top and refs, whose files say a commit or checkout happened.</summary>
    private bool Kept(string path)
    {
        string[]? parts = Parts(path);
        return parts is null ? path == folder
            : parts[0] == ".git" ? parts.Length == 1 || parts[1] == "refs"
            : !parts.Any(IgnoredFolders.Contains);
    }

    /// <summary>A path's names from the open folder down; null for the folder itself or a path outside it.</summary>
    private string[]? Parts(string path) =>
        PathNames.IsUnder(path, folder) ? Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar) : null;

    private void Note(string path, bool created)
    {
        if (FileText.IsSaving(path))
        {
            return;
        }
        string[] parts = Parts(path) ?? ["."];
        bool inGit = parts[0] == ".git";
        if (inGit && !(parts.Length >= 2 && parts[1] is "HEAD" or "index" or "refs" or "packed-refs"))
        {
            return;
        }
        if (!inGit && (parts.Any(IgnoredFolders.Contains) || WorkspaceModel.IsTemporaryProject(path)))
        {
            return;
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (inGit)
            {
                git = true;
            }
            else
            {
                string canonical = PathNames.Canonical(path);
                paths.Add(canonical);
                // A save moves its temporary file over the file: a change, not a file come or gone.
                bool saved = ownWrites.TryGetValue(canonical, out DateTime at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(5);
                structure |= created && !saved;
                projects |= ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase);
            }
            if (pending)
            {
                return;
            }
            pending = true;
        }
        // Held while the changes gather, then by the crossing, which lets go of what it can never deliver.
        IDisposable held = BackgroundWork.Scope();
        _ = Task.Delay(Gather).ContinueWith(_ =>
        {
            UiThread.Post(Deliver);
            held.Dispose();
        }, TaskScheduler.Default);
    }

    private void Deliver()
    {
        FolderChanges changes;
        bool live;
        lock (gate)
        {
            changes = new FolderChanges(new HashSet<string>(paths, StringComparer.Ordinal), structure, projects, git);
            paths.Clear();
            structure = projects = git = pending = false;
            live = !disposed;
        }
        if (live)
        {
            Changed?.Invoke(this, changes);
        }
    }
}
