using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>The files a language server registered to hear of (<see cref="WatchedFiles"/>), watched for it: those of the
/// open folder by the folder's own watcher (<see cref="Heard"/>), the rest by watchers of this one's own, each folder once:
/// outside the folder, inside a folder the folder's watcher passes over, and the build output in a folder registered with
/// what is under it; with no folder watcher, every folder registered. A watcher starts as its registration comes, and what
/// was written in its folder since the server started is told then, so nothing written before it watched is missed. What
/// changed reaches the server from the thread pool, one notification for each batch, in the order seen. The folders
/// watched, and any that cannot be, are said in the IDE's log.</summary>
public sealed class ServerWatches : IDisposable
{
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(150);

    /// <summary>How far a file system's clock may round a file's time down: one written as the server started counts.</summary>
    private static readonly TimeSpan Rounding = TimeSpan.FromSeconds(2);

    private readonly LanguageServer server;
    private readonly WatchedFiles watched;
    private readonly Lock gate = new();
    private readonly Lock refreshing = new();
    private readonly Dictionary<(string Folder, bool Recursive), FileSystemWatcher> own = [];
    private readonly Dictionary<string, FileChangeType> heard = new(PathNames.Comparer);

    /// <summary>Passed-over folders come or gone since the last refresh, whose watchers are made anew: one deleted and made
    /// again in a moment is another folder to Linux's and Windows' watchers.</summary>
    private readonly HashSet<string> renewed = new(PathNames.Comparer);

    /// <summary>The folders a watcher could not be started for, said once each.</summary>
    private readonly HashSet<string> unwatchable = new(PathNames.Comparer);
    private string? folder;
    private bool pending;
    private bool refreshRunning;
    private bool refreshAgain;
    private bool disposed;

    /// <summary>What is told the server, one batch after another in the order they were seen, so a file made and then
    /// deleted is never told the other way round.</summary>
    private Task telling = Task.CompletedTask;

    /// <summary>Watches what the server registered, and what it registers from now on, beside the watcher of a folder
    /// (canonical) whose changes it is given (<see cref="Heard"/>); with none (a server no window has), alone.</summary>
    public ServerWatches(LanguageServer server, string? folder)
    {
        this.server = server;
        watched = server.WatchedFiles;
        this.folder = folder;
        watched.Changed += OnRegistered;
        Refresh();
    }

    /// <summary>The folders watched apart from the folder's own watcher, each with whether its subfolders are.</summary>
    public IReadOnlyList<(string Folder, bool Recursive)> Watching
    {
        get
        {
            lock (gate)
            {
                return [.. own.Keys];
            }
        }
    }

    /// <summary>Watches from now on beside another folder's watcher, or alone (null), the watchers it then needs started
    /// before those it no longer needs stop, so nothing changes unheard in between: a server a window hands over or takes up.</summary>
    public void Follow(string? folder)
    {
        lock (gate)
        {
            this.folder = folder;
        }
        RefreshNow();
    }

    /// <summary>What the folder's watcher saw: each file told as made, changed or gone, every file of the folder when what
    /// changed was lost; and a passed-over folder come or gone watched or let go of.</summary>
    public void Heard(FolderChanges changes)
    {
        string? open;
        lock (gate)
        {
            open = folder;
        }
        if (open is null)
        {
            return;
        }
        if (changes.Lost)
        {
            InTurn(() => TellEvery(open, recursive: true, IgnoredFolders.Is, since: null));
        }
        else if (changes.Paths.Count > 0)
        {
            InTurn(() => Tell(changes));
        }
        List<string> outputs = [.. changes.ComeOrGone.Where(IgnoredFolders.Is)];
        if (outputs.Count > 0)
        {
            lock (gate)
            {
                renewed.UnionWith(outputs);
            }
            Refresh();
        }
    }

    public void Dispose()
    {
        watched.Changed -= OnRegistered;
        List<FileSystemWatcher> stopped;
        lock (gate)
        {
            disposed = true;
            stopped = [.. own.Values];
            own.Clear();
            heard.Clear();
        }
        stopped.ForEach(w => w.Dispose());
    }

    private void OnRegistered(object? sender, EventArgs e) => Refresh();

    private void Tell(FolderChanges changes)
    {
        List<FileEvent> events = new(changes.Paths.Count);
        foreach (string path in changes.Paths)
        {
            bool there = File.Exists(path) || Directory.Exists(path);
            events.Add(new FileEvent(path, !there ? FileChangeType.Deleted : changes.ComeOrGone.Contains(path) ? FileChangeType.Created : FileChangeType.Changed));
        }
        watched.Tell(events);
    }

    /// <summary>The files in a folder told as changed: in the folders under it too when <paramref name="recursive"/>, but
    /// those <paramref name="passedOver"/> names; every one for changes lost there, or those written <paramref name="since"/>.</summary>
    private void TellEvery(string top, bool recursive, Func<string, bool> passedOver, DateTime? since)
    {
        List<FileEvent> events = [];
        foreach ((string _, IReadOnlyList<string> files) in PathNames.FilesUnder(top, sub => recursive && !passedOver(sub)))
        {
            events.AddRange(files.Where(file => since is not { } after || Written(file) >= after).Select(file => new FileEvent(file, FileChangeType.Changed)));
        }
        watched.Tell(events);
    }

    private static DateTime Written(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Watches anew what the registrations need, at once on the thread pool, a burst of registrations (a load
    /// registers a project's folder, file and references one at a time) taken by one refresh after another; held until done.</summary>
    private void Refresh()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (refreshRunning)
            {
                refreshAgain = true;
                return;
            }
            refreshRunning = true;
        }
        IdeErrors.Observe(BackgroundWork.Run(() =>
        {
            while (true)
            {
                RefreshNow();
                lock (gate)
                {
                    if (!refreshAgain || disposed)
                    {
                        refreshRunning = false;
                        return;
                    }
                    refreshAgain = false;
                }
            }
        }));
    }

    /// <summary>Starts the watchers the registrations need before stopping those they no longer do, and says what changed.</summary>
    private void RefreshNow()
    {
        lock (refreshing)
        {
            HashSet<(string Folder, bool Recursive)> needed = Needed();
            List<(string Folder, bool Recursive)> starting;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                foreach ((string Folder, bool Recursive) made in own.Keys.Where(k => renewed.Any(r => PathNames.Within(k.Folder, r))).ToList())
                {
                    own[made].Dispose();
                    own.Remove(made);
                }
                renewed.Clear();
                starting = [.. needed.Where(k => !own.ContainsKey(k))];
            }
            List<string> started = [.. starting.Where(at => Start(at.Folder, at.Recursive)).Select(at => at.Folder)];
            List<FileSystemWatcher> stopped = [];
            List<string> stoppedAt = [];
            lock (gate)
            {
                foreach ((string Folder, bool Recursive) gone in own.Keys.Where(k => !needed.Contains(k)).ToList())
                {
                    stopped.Add(own[gone]);
                    stoppedAt.Add(gone.Folder);
                    own.Remove(gone);
                }
            }
            stopped.ForEach(w => w.Dispose());
            if (started.Count > 0 || stoppedAt.Count > 0)
            {
                IdeErrors.Write($"C# language server (process {server.ProcessId}): {Plural.Of(Watching.Count, "folder")} watched for it beside the folder's watcher"
                    + (started.Count > 0 ? $"; from now on {string.Join(", ", started)}" : string.Empty)
                    + (stoppedAt.Count > 0 ? $"; no longer {string.Join(", ", stoppedAt)}" : string.Empty));
            }
        }
    }

    /// <summary>The folders the registrations need watched that the folder's watcher does not hear, none under another
    /// watched with its subfolders, and only those there now (a folder made later comes with a registration or
    /// <see cref="Heard"/>).</summary>
    private HashSet<(string Folder, bool Recursive)> Needed()
    {
        string? beside;
        lock (gate)
        {
            beside = folder;
        }
        List<(string Folder, bool Recursive)> wanted = [];
        foreach (FileWatch watch in watched.Watches.DistinctBy(w => (w.Folder, w.Recursive)))
        {
            if (watch.Folder is not { } at)
            {
                // A glob over whole paths: the folder's watcher's changes are matched against it, and nothing more is known of where.
                continue;
            }
            if (beside is null || !PathNames.Within(at, beside) || IgnoredFolders.AnyIn(beside, PathNames.Below(at, beside)))
            {
                wanted.Add((at, watch.Recursive));
            }
            else if (watch.Recursive)
            {
                wanted.AddRange(BuildOutput(at).Select(output => (output, true)));
            }
        }
        HashSet<(string Folder, bool Recursive)> needed = [];
        List<string> whole = [.. wanted.Where(w => w.Recursive).Select(w => w.Folder)];
        foreach ((string at, bool recursive) in wanted)
        {
            if (whole.Any(above => PathNames.IsUnder(at, above) || (!recursive && PathNames.Same(at, above))))
            {
                continue;
            }
            if (Directory.Exists(at))
            {
                needed.Add((at, recursive));
            }
            else
            {
                Unwatchable(at, "it is not there; it is watched if a registration names it once it is");
            }
        }
        return needed;
    }

    /// <summary>The passed-over folders in a folder the folder's watcher hears that a build writes (a project's obj, with the
    /// sources it generates, its bin, artifacts): neither version control's and other IDEs' (hidden) nor dependencies'
    /// (node_modules), which hold no project's files and would take a watch for each of their many folders on Linux.</summary>
    private static IEnumerable<string> BuildOutput(string at)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(at).Where(sub => IgnoredFolders.Is(sub) && Path.GetFileName(sub) is not ['.', ..] and not "node_modules")];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Starts watching a folder, then tells what was written in it since the server started, which it may have read
    /// before; false, said in the log, when it cannot be watched.</summary>
    private bool Start(string at, bool recursive)
    {
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(at)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
        }
        catch (ArgumentException)
        {
            Unwatchable(at, "it is not there; it is watched if a registration names it once it is");
            return false;
        }
        watcher.Created += (_, e) => Note(e.FullPath, FileChangeType.Created);
        watcher.Changed += (_, e) => Note(e.FullPath, FileChangeType.Changed);
        watcher.Deleted += (_, e) => Note(e.FullPath, FileChangeType.Deleted);
        watcher.Renamed += (_, e) =>
        {
            Note(e.OldFullPath, FileChangeType.Deleted);
            Note(e.FullPath, FileChangeType.Created);
        };
        watcher.Error += (_, e) => Failed(at, recursive, e.GetException());
        lock (gate)
        {
            if (disposed || own.ContainsKey((at, recursive)))
            {
                watcher.Dispose();
                return false;
            }
            own[(at, recursive)] = watcher;
        }
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            lock (gate)
            {
                own.Remove((at, recursive));
            }
            watcher.Dispose();
            Unwatchable(at, FolderWatching.Platform.OutOfWatches(error) ?? error.Message);
            return false;
        }
        catch (ObjectDisposedException)
        {
            // Let go of meanwhile.
            return false;
        }
        DateTime since = watched.Started - Rounding;
        InTurn(() => TellEvery(at, recursive, _ => false, since));
        return true;
    }

    /// <summary>A watcher's error: changes lost there (too many at once) have every file of its folder told as changed; one
    /// that stopped it (its folder gone, the system's watches used up) is said in the log.</summary>
    private void Failed(string at, bool recursive, Exception error)
    {
        if (error is InternalBufferOverflowException)
        {
            InTurn(() => TellEvery(at, recursive, _ => false, since: null));
            return;
        }
        Unwatchable(at, FolderWatching.Platform.OutOfWatches(error) ?? error.Message);
    }

    private void Unwatchable(string at, string why)
    {
        lock (gate)
        {
            if (disposed || !unwatchable.Add(at))
            {
                return;
            }
        }
        IdeErrors.Write($"C# language server (process {server.ProcessId}): {at} cannot be watched for it, so it hears nothing changed there: {why}");
    }

    /// <summary>A change one of this one's watchers saw that the server asked for, gathered for a moment with the others: a
    /// file made and then written is still one made. A build's churn the server never asked for is never held for.</summary>
    private void Note(string path, FileChangeType change)
    {
        FileEvent seen = new(path, change);
        if (!watched.Watches.Any(watch => watch.Covers(seen)))
        {
            return;
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            heard[path] = heard.TryGetValue(path, out FileChangeType was) && was == FileChangeType.Created && change == FileChangeType.Changed ? was : change;
            if (pending)
            {
                return;
            }
            pending = true;
        }
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Delay(Gather).ContinueWith(_ =>
        {
            try
            {
                List<FileEvent> events;
                lock (gate)
                {
                    events = [.. heard.Select(h => new FileEvent(h.Key, h.Value))];
                    heard.Clear();
                    pending = false;
                }
                InTurn(() => watched.Tell(events));
            }
            finally
            {
                held.Dispose();
            }
        }, TaskScheduler.Default));
    }

    /// <summary>Tells the server on the thread pool after what was told before, held as background work until it has.</summary>
    private void InTurn(Action tell)
    {
        IDisposable held = BackgroundWork.Scope();
        lock (gate)
        {
            telling = telling.ContinueWith(_ =>
            {
                try
                {
                    tell();
                }
                finally
                {
                    held.Dispose();
                }
            }, TaskScheduler.Default);
            IdeErrors.Observe(telling);
        }
    }
}

/// <summary>What a folder's watcher saw while nothing could tell the language server (its watches not made yet), kept in
/// order for what will, and past <see cref="Limit"/> paths taken as everything changed.</summary>
public sealed class HeldChanges(string folder)
{
    private const int Limit = 50_000;

    private readonly List<FolderChanges> held = [];
    private int paths;

    public void Add(FolderChanges changes)
    {
        paths += changes.Paths.Count;
        if (held is [{ Lost: true }])
        {
            return;
        }
        if (changes.Lost || paths > Limit)
        {
            held.Clear();
            held.Add(new FolderChanges(new HashSet<string>(PathNames.Comparer) { folder }, Structure: true, Projects: true, Git: true) { Lost = true });
            return;
        }
        held.Add(changes);
    }

    /// <summary>What was kept, in order, let go of here.</summary>
    public IReadOnlyList<FolderChanges> Take()
    {
        List<FolderChanges> taken = [.. held];
        held.Clear();
        paths = 0;
        return taken;
    }
}
