using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>What changed in a folder while it was watched: the files created, changed, renamed or deleted, whether
/// any of that changed which files there are, whether a project or solution file was among them, and whether Git's
/// own state moved (a commit or checkout made elsewhere); and, apart from the files, the restores written (each
/// project's <c>obj/project.assets.json</c>), whatever ran them: the language server, a terminal's dotnet, a build.</summary>
public sealed record FolderChanges(IReadOnlySet<string> Paths, bool Structure, bool Projects, bool Git)
{
    public IReadOnlySet<string> Restored { get; init; } = new HashSet<string>();

    /// <summary>The files and folders among <see cref="Paths"/> that came or went (created, deleted or renamed), each also
    /// as a walk names it, its folder's canonical path and its own name: what Go to File's index puts in or takes out.</summary>
    public IReadOnlySet<string> ComeOrGone { get; init; } = new HashSet<string>();

    /// <summary>Whether the system lost changes (too many at once, as a large checkout makes) or a watch failed: what they
    /// were is not known, so everything shown from the folder is read again (the open files, the Explorer, the solution,
    /// Go to File's index). <see cref="Paths"/> then holds the folder itself.</summary>
    public bool Lost { get; init; }

    /// <summary>Whether a project's launch profiles (its <c>launchSettings.json</c>, a file-based program's <c>.run.json</c>)
    /// were among the files: the run configurations are made of them, and nothing the solution or the Explorer shows is.</summary>
    public bool LaunchProfiles { get; init; }
}

/// <summary>Watches a folder for what other programs change in it (a terminal's <c>git pull</c>, an agent's edits,
/// <c>dotnet new</c>). Changes are gathered for a moment and handed over together on the UI thread, as background work.
/// Build output, dependencies and Git's folder are passed over, except Git's HEAD, index and refs and a restore's
/// assets; where each folder is watched on its own (Linux), they are never watched at all but for Git's and each obj
/// itself, and a folder made later is watched as it appears.</summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(150);

    /// <summary>How long after a save its file's events are taken for the save's own.</summary>
    private static readonly TimeSpan OwnWriteHeard = TimeSpan.FromSeconds(5);

    private static readonly string[] ProjectExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

    /// <summary>The file a restore writes in a project's obj (or, with UseArtifactsOutput, in artifacts/obj/Project).</summary>
    private const string Assets = "project.assets.json";

    private readonly string folder;
    private readonly FolderWatching watching;
    private readonly object gate = new();
    private readonly Dictionary<string, FileSystemWatcher> watches = new(StringComparer.Ordinal);
    private readonly HashSet<string> paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> restored = new(StringComparer.Ordinal);
    private readonly HashSet<string> comeOrGone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> ownWrites = new(StringComparer.Ordinal);

    /// <summary>The changes to which folders are watched, made one at a time in the order they were seen.</summary>
    private Task following = Task.CompletedTask;
    private bool rewalking;
    private bool structure;
    private bool projects;
    private bool launchProfiles;
    private bool git;
    private bool lost;
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

    /// <summary>A file Passenger is saving: its events in the next moments are its content changing, not a new file.
    /// The saves heard of longer ago than that are let go here, so the watcher keeps only its last moments' saves.</summary>
    private void OnWriting(string path)
    {
        string canonical = PathNames.Canonical(path);
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;
            foreach ((string written, DateTime at) in ownWrites)
            {
                if (now - at >= OwnWriteHeard)
                {
                    // A dictionary's enumeration survives a removal (since .NET Core 3.0).
                    ownWrites.Remove(written);
                }
            }
            ownWrites[canonical] = now;
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
            Follow(() =>
            {
                WatchFrom(path);
                // A restore that made obj (or artifacts) may have written its assets before the folder was watched.
                foreach (string assets in AssetsIn(path))
                {
                    Note(assets, created: true);
                }
            });
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
        lock (gate)
        {
            lost = true;
        }
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
    /// it, of Git's store only its top and refs, whose files say a commit or checkout happened, and of build output only
    /// the folders a restore writes its assets in (<see cref="HoldsRestores"/>).</summary>
    private bool Kept(string path)
    {
        ReadOnlySpan<char> parts = PathNames.Below(path, folder);
        if (parts.IsEmpty)
        {
            return path == folder;
        }
        ReadOnlySpan<char> first = First(parts, out ReadOnlySpan<char> rest);
        return first.SequenceEqual(".git") ? First(parts, out _).Length == parts.Length || First(rest, out _).SequenceEqual("refs")
            : !IgnoredFolders.AnyIn(folder, parts) || HoldsRestores(parts);
    }

    /// <summary>Whether a folder of build output is where a restore writes its assets, or above it to see that made: a
    /// project's obj itself, and UseArtifactsOutput's artifacts, artifacts/obj and artifacts/obj/Project.</summary>
    private bool HoldsRestores(ReadOnlySpan<char> parts)
    {
        bool two = Last(parts, out ReadOnlySpan<char> above, out ReadOnlySpan<char> last);
        bool three = Last(above, out ReadOnlySpan<char> aboveSecond, out ReadOnlySpan<char> second);
        Last(aboveSecond, out ReadOnlySpan<char> aboveThird, out ReadOnlySpan<char> third);
        int output = three && second.SequenceEqual("obj") && third.SequenceEqual("artifacts") ? 3
            : two && last.SequenceEqual("obj") && second.SequenceEqual("artifacts") ? 2
            : last.SequenceEqual("artifacts") || last.SequenceEqual("obj") ? 1
            : 0;
        return output > 0 && !IgnoredFolders.AnyIn(folder, output switch { 3 => aboveThird, 2 => aboveSecond, _ => above });
    }

    /// <summary>The restore assets in a folder just made, or in the build output folders that hold them under it.</summary>
    private List<string> AssetsIn(string top)
    {
        List<string> found = [];
        Stack<string> folders = new([top]);
        while (folders.Count > 0)
        {
            string at = folders.Pop();
            string assets = Path.Combine(at, Assets);
            if (PathNames.IsUnder(assets, folder) && IsRestore(PathNames.Below(assets, folder)) && File.Exists(assets))
            {
                found.Add(assets);
            }
            try
            {
                foreach (DirectoryInfo child in new DirectoryInfo(at).EnumerateDirectories())
                {
                    if (child.LinkTarget is null && PathNames.IsUnder(child.FullName, folder) && HoldsRestores(PathNames.Below(child.FullName, folder)))
                    {
                        folders.Push(child.FullName);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    /// <summary>Whether a path, by its names from the open folder, is a restore's assets, which the rest of build output's
    /// files are not.</summary>
    private static bool IsRestore(ReadOnlySpan<char> parts)
    {
        if (!Last(parts, out ReadOnlySpan<char> above, out ReadOnlySpan<char> last) || !last.SequenceEqual(Assets))
        {
            return false;
        }
        bool three = Last(above, out ReadOnlySpan<char> aboveSecond, out ReadOnlySpan<char> second);
        Last(aboveSecond, out _, out ReadOnlySpan<char> third);
        return second.SequenceEqual("obj") || three && third.SequenceEqual("obj");
    }

    /// <summary>A path's first name and the names after it.</summary>
    private static ReadOnlySpan<char> First(ReadOnlySpan<char> parts, out ReadOnlySpan<char> rest)
    {
        int at = parts.IndexOfAny(PathNames.Separators);
        rest = at < 0 ? [] : parts[(at + 1)..];
        return at < 0 ? parts : parts[..at];
    }

    /// <summary>A path's last name and the names before it; false when it has one name alone.</summary>
    private static bool Last(ReadOnlySpan<char> parts, out ReadOnlySpan<char> above, out ReadOnlySpan<char> last)
    {
        int at = parts.LastIndexOfAny(PathNames.Separators);
        above = at < 0 ? [] : parts[..at];
        last = at < 0 ? parts : parts[(at + 1)..];
        return at >= 0;
    }

    /// <summary>Takes in what the system saw at a path, by its names from the open folder read in place, so the events of
    /// what is passed over (build output, dependencies, Git's objects) cost no string; only those kept are made canonical.</summary>
    private void Note(string path, bool created)
    {
        if (FileText.IsSaving(path))
        {
            return;
        }
        ReadOnlySpan<char> parts = PathNames.Below(path, folder);
        bool inGit = First(parts, out ReadOnlySpan<char> rest).SequenceEqual(".git");
        if (inGit && !(First(rest, out _) is "HEAD" or "index" or "refs" or "packed-refs"))
        {
            return;
        }
        bool restore = !inGit && IsRestore(parts);
        // A passed-over folder itself, come or gone, is its parent changing; what changes in it is never followed.
        if (!inGit && !restore && IgnoredFolders.AnyIn(folder, parts) && !(created && !IgnoredFolders.AnyIn(folder, Above(parts))))
        {
            return;
        }
        string? canonical = inGit ? null : PathNames.Canonical(path);
        // As a walk names it: its own name in its folder, which a link's canonical path is not.
        string? named = inGit || restore || !created || Path.GetDirectoryName(path) is not { } parent ? null : Path.Join(PathNames.Canonical(parent), Path.GetFileName(path.AsSpan()));
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
            else if (restore)
            {
                restored.Add(canonical!);
            }
            else
            {
                paths.Add(canonical!);
                // A save moves its temporary file over the file: a change, not a file come or gone.
                bool saved = ownWrites.TryGetValue(canonical!, out DateTime at) && DateTime.UtcNow - at < OwnWriteHeard;
                if (created && !saved)
                {
                    structure = true;
                    comeOrGone.Add(canonical!);
                    if (named is not null)
                    {
                        comeOrGone.Add(named);
                    }
                }
                projects |= IsProjectFile(path);
                launchProfiles |= Path.GetFileName(path.AsSpan()).Equals(Run.LaunchProfile.SettingsFile, StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".run.json", StringComparison.OrdinalIgnoreCase);
            }
            if (pending)
            {
                return;
            }
            pending = true;
        }
        DeliverLater();
    }

    /// <summary>Hands the changes over once they have gathered, held while they gather, then by the crossing, which lets go
    /// of what it can never deliver; a method of its own, so its closure is made only as a batch begins.</summary>
    private void DeliverLater()
    {
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Delay(Gather).ContinueWith(_ =>
        {
            UiThread.Post(Deliver);
            held.Dispose();
        }, TaskScheduler.Default));
    }

    /// <summary>A path's names but its last.</summary>
    private static ReadOnlySpan<char> Above(ReadOnlySpan<char> parts)
    {
        Last(parts, out ReadOnlySpan<char> above, out _);
        return above;
    }

    /// <summary>Whether a file is a solution, project, props or targets file, which the solution is read again for.</summary>
    private static bool IsProjectFile(string path)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
        foreach (string project in ProjectExtensions)
        {
            if (extension.Equals(project, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void Deliver()
    {
        FolderChanges changes;
        bool live;
        lock (gate)
        {
            changes = new FolderChanges(new HashSet<string>(paths, StringComparer.Ordinal), structure, projects, git)
            {
                Restored = new HashSet<string>(restored, StringComparer.Ordinal),
                ComeOrGone = new HashSet<string>(comeOrGone, StringComparer.Ordinal),
                Lost = lost,
                LaunchProfiles = launchProfiles,
            };
            paths.Clear();
            restored.Clear();
            comeOrGone.Clear();
            structure = projects = launchProfiles = git = lost = pending = false;
            live = !disposed;
        }
        if (live)
        {
            Changed?.Invoke(this, changes);
        }
    }
}
