using System.IO.Enumeration;
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

    /// <summary>Whether the system said it lost changes, which the folders listed again find, all but a file rewritten in
    /// place where nothing else changed: the open files are each looked at, as few as they are.</summary>
    public bool Overflowed { get; init; }

    /// <summary>Whether a project's launch profiles (its <c>launchSettings.json</c>, a file-based program's <c>.run.json</c>)
    /// were among the files: the run configurations are made of them, and nothing the solution or the Explorer shows is.</summary>
    public bool LaunchProfiles { get; init; }
}

/// <summary>Watches a folder for what other programs change in it (a terminal's <c>git pull</c>, an agent's edits,
/// <c>dotnet new</c>). Changes are gathered for a moment and handed over together on the UI thread, as background work.
/// Build output, dependencies and Git's folder are passed over, except Git's HEAD, index and refs and a restore's
/// assets; where each folder is watched on its own (Linux), they are never watched at all but for Git's and each obj
/// itself, and a folder made later is watched as it appears, what was written in it before then taken as made with it.
/// A burst of changes may lose some unsaid (macOS's watcher drops the word that it did), so what is in each kept folder
/// is recorded, and after a burst the folders it touched or whose entries changed are listed again against the record.</summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(150);

    /// <summary>How long after a save its file's events are taken for the save's own.</summary>
    private static readonly TimeSpan OwnWriteHeard = TimeSpan.FromSeconds(5);

    /// <summary>How many changes heard within <see cref="Burst"/> put them at risk of others lost unsaid: FSEvents drops
    /// what passes its queue of a few hundred, and .NET's watcher passes over its word that it did.</summary>
    private const int AtRisk = 256;

    private static readonly TimeSpan Burst = TimeSpan.FromSeconds(1);

    /// <summary>How long before a burst at risk a file written may have had its change lost, the system's queue having
    /// waited that long for the watcher; a file's change heard since its last write is never handed over again.</summary>
    private static readonly TimeSpan Lag = TimeSpan.FromSeconds(2);

    /// <summary>The most entries the record keeps, and a burst's listing reads; past it, changes at risk are taken for lost.</summary>
    private const int RecordLimit = 200_000;

    private static readonly string[] ProjectExtensions = [".sln", ".slnx", ".slnf", ".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

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

    /// <summary>When each kept path was last heard of, and each kept folder had one of its paths heard, for the last moments.</summary>
    private readonly Dictionary<string, DateTime> heardAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> touched = new(StringComparer.Ordinal);

    /// <summary>The paths found changed by listing a folder again in the last moments, each with its last write then (none
    /// for one gone), so its own change heard late is not handed over twice.</summary>
    private readonly Dictionary<string, (DateTime Written, DateTime At)> handed = new(StringComparer.Ordinal);

    /// <summary>The changes heard in each batch handed over in the last <see cref="Burst"/>, and in the one gathering.</summary>
    private readonly Queue<(DateTime At, int Heard)> batches = new();
    private int heardInBatch;

    /// <summary>When the last change was heard, and the first after a quiet <see cref="Burst"/> before it.</summary>
    private DateTime lastHeard;
    private DateTime burstBegan;

    /// <summary>The record, by kept folder: its entries' names and its stamp as last listed. Read and changed only by
    /// the work <see cref="Follow"/> runs in turn.</summary>
    private readonly Dictionary<string, Listed> record = new(StringComparer.Ordinal);
    private int recorded;

    /// <summary>When the record of the whole folder was done, or null while it is made or once it passed its limit.</summary>
    private DateTime? recordedAt;

    /// <summary>The changes to which folders are watched, made one at a time in the order they were seen.</summary>
    private Task following = Task.CompletedTask;
    private bool rewalking;
    private bool structure;
    private bool projects;
    private bool launchProfiles;
    private bool git;
    private bool lost;
    private bool overflowed;

    /// <summary>A burst at risk is looked at once more when it has been quiet a moment, for what it lost after its last
    /// look: whether that look is due, and asked for now, when the burst was last at risk and when it began.</summary>
    private bool recheckDue;
    private bool recheck;
    private DateTime lastRisk;
    private DateTime riskBegan;
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
            Follow(() => Record(() => WatchFrom(folder)));
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
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
        watcher.Created += (_, e) => OnCreated(e.FullPath, made: true);
        watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
        watcher.Changed += (_, e) => Note(e.FullPath, created: false);
        watcher.Renamed += (_, e) =>
        {
            OnDeleted(e.OldFullPath);
            OnCreated(e.FullPath, made: false);
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

    /// <summary>A file or folder come, <paramref name="made"/> there or moved in. Where each folder is watched on its own,
    /// a folder is watched from now on, so the files already written in one made (with it at once, as <c>dotnet new -o</c>
    /// and an agent write them) are taken as made too, as one watch over the tree hears them; one moved in is itself alone.</summary>
    private void OnCreated(string path, bool made)
    {
        if (watching.EachFolder && Kept(path) && Directory.Exists(path))
        {
            Follow(() =>
            {
                if (made)
                {
                    WatchFrom(path, file => Note(file, created: true));
                    return;
                }
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
        if (error is InternalBufferOverflowException)
        {
            // Too many changes at once: those lost are looked for as a burst at risk's are.
            lock (gate)
            {
                overflowed = true;
                if (pending)
                {
                    return;
                }
                pending = true;
            }
            DeliverLater();
            return;
        }
        // A watch that failed: what it would have heard is not known.
        Lose(overflow: false);
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

    /// <summary>Watches every kept folder not watched yet, lets go of those gone and records them all again: the first
    /// walk, and the one after events were lost. Asked for once however many watchers lost theirs.</summary>
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
            Record(() => WatchFrom(folder));
        });
    }

    /// <summary>Makes the record of the whole folder anew by a walk, which may stop it at its limit.</summary>
    private void Record(Action walk)
    {
        record.Clear();
        recorded = 0;
        recordedAt = null;
        walk();
        if (recorded <= RecordLimit)
        {
            recordedAt = DateTime.UtcNow;
        }
        else
        {
            record.Clear();
        }
    }

    /// <summary>Watches (where each folder is watched on its own) and records a kept folder and the kept folders under it,
    /// each watched before what is in it is read, so a folder or file made meanwhile is either read here or heard of from
    /// its own folder; each file in them is given to <paramref name="found"/>.</summary>
    private void WatchFrom(string top, Action<string>? found = null)
    {
        Stack<string> folders = new([top]);
        while (folders.Count > 0 && !IsDisposed && (watching.EachFolder || recorded <= RecordLimit))
        {
            string at = folders.Pop();
            if (!Directory.Exists(at) || new DirectoryInfo(at).LinkTarget is not null || watching.EachFolder && !Start(at, recursive: false))
            {
                continue;
            }
            if (Listing(at, written: false) is not { } entries)
            {
                continue;
            }
            Keep(at, entries.Stamp, entries.Entries);
            foreach (Entry entry in entries.Entries)
            {
                string path = Path.Join(at, entry.Name);
                // A linked folder is not followed, since it can loop.
                if (entry.Folder && !entry.Link && Kept(path))
                {
                    folders.Push(path);
                }
                else if (!entry.Folder && found is not null)
                {
                    found(path);
                }
            }
        }
    }

    /// <summary>What is in a folder now, its stamp read first so a change made while it is listed moves it past the one
    /// kept, and each file's last write when <paramref name="written"/> (a look at each file's own record); the system's
    /// own files and a save's temporary file passed over, as every walk does. Null when it is gone.</summary>
    private static (DateTime Stamp, List<Entry> Entries)? Listing(string at, bool written)
    {
        DateTime stamp = Directory.GetLastWriteTimeUtc(at);
        try
        {
            FileSystemEnumerable<Entry> entries = new(at, (ref FileSystemEntry entry) => new Entry(entry.FileName.ToString(), entry.IsDirectory,
                entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0, written && !entry.IsDirectory ? entry.LastWriteTimeUtc.UtcDateTime : default), Everything)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => !PathNames.IsSystemFile(entry.FileName) && !FileText.IsSaving(entry.FileName),
            };
            return (stamp, [.. entries]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    /// <summary>A folder's entry as listed: its name, whether it is a folder and a link, and a file's last write.</summary>
    private readonly record struct Entry(string Name, bool Folder, bool Link, DateTime Written);

    /// <summary>A folder as the record last had it.</summary>
    private sealed record Listed(HashSet<string> Names, DateTime Stamp);

    /// <summary>Records a folder as listed, unless the record is past its limit.</summary>
    private void Keep(string at, DateTime stamp, List<Entry> entries)
    {
        if (recorded > RecordLimit)
        {
            return;
        }
        recorded += entries.Count - (record.TryGetValue(at, out Listed? was) ? was.Names.Count : 0);
        record[at] = new(new HashSet<string>(entries.Select(e => e.Name), StringComparer.Ordinal), stamp);
    }

    /// <summary>Keeps the record of what a batch handed over came or went: each name in or out of its folder's record as the
    /// disk has it now, a folder come recorded with what is in it, one gone let go of with every folder recorded under it.
    /// The folders' stamps stay as they were, so a change lost beside these still shows.</summary>
    private void Keep(IEnumerable<string> cameOrWent)
    {
        if (recordedAt is null)
        {
            return;
        }
        foreach (string path in cameOrWent)
        {
            if (Path.GetDirectoryName(path) is not { } parent || !record.TryGetValue(parent, out Listed? listed))
            {
                continue;
            }
            string name = Path.GetFileName(path);
            if (File.Exists(path) || Directory.Exists(path))
            {
                recorded += listed.Names.Add(name) ? 1 : 0;
                if (Directory.Exists(path) && !record.ContainsKey(path) && Kept(path))
                {
                    WatchFrom(path);
                }
            }
            else
            {
                recorded -= listed.Names.Remove(name) ? 1 : 0;
                Forget(path);
            }
        }
    }

    /// <summary>Lets go of a folder's record and of every folder's under it, if it was one; the paths recorded in them, given
    /// to <paramref name="gone"/>.</summary>
    private void Forget(string path, Action<string>? gone = null)
    {
        if (!record.ContainsKey(path))
        {
            return;
        }
        foreach (string under in record.Keys.Where(k => PathNames.Within(k, path)).ToList())
        {
            foreach (string name in record[under].Names)
            {
                gone?.Invoke(Path.Join(under, name));
            }
            recorded -= record[under].Names.Count;
            record.Remove(under);
        }
    }

    /// <summary>Finds what a burst at risk may have lost unsaid: the folders it touched, and those whose stamp moved since
    /// listed, are listed again against the record, and each name come or gone and each file written <paramref
    /// name="since"/> and not heard of after it is taken in as heard. Past the record's limit, or for a burst <paramref
    /// name="began"/> before the record was made, the changes are taken for lost.</summary>
    private void Recover(DateTime began, DateTime since, IReadOnlyCollection<string> folders, Dictionary<string, DateTime> heard)
    {
        if (recordedAt is not { } made || made > began)
        {
            Lose();
            return;
        }
        HashSet<string> listing = new(folders.Where(record.ContainsKey), StringComparer.Ordinal);
        foreach ((string at, Listed listed) in record)
        {
            if (Directory.GetLastWriteTimeUtc(at) != listed.Stamp)
            {
                listing.Add(at);
            }
        }
        int read = 0;
        int found = 0;
        // Each folder after those above it, so one gone is let go of with its parent's listing.
        foreach (string at in listing.OrderBy(f => f.Length))
        {
            if (!record.TryGetValue(at, out Listed? was) || Listing(at, written: true) is not { } now)
            {
                continue;
            }
            read += now.Entries.Count;
            if (read > RecordLimit)
            {
                Lose();
                return;
            }
            HashSet<string> names = new(now.Entries.Select(e => e.Name), StringComparer.Ordinal);
            foreach (string name in was.Names.Where(n => !names.Contains(n)))
            {
                string path = Path.Join(at, name);
                Forget(path, file => Found(file, created: true));
                Found(path, created: true);
            }
            Keep(at, now.Stamp, now.Entries);
            foreach (Entry entry in now.Entries)
            {
                string path = Path.Join(at, entry.Name);
                if (!was.Names.Contains(entry.Name))
                {
                    Found(path, created: true);
                    if (entry.Folder && !entry.Link && Kept(path))
                    {
                        WatchFrom(path, file => Found(file, created: true));
                    }
                }
                else if (!entry.Folder && entry.Written >= since && !(heard.TryGetValue(PathNames.Canonical(path), out DateTime last) && last >= entry.Written))
                {
                    Found(path, created: false);
                }
            }
        }
        IdeErrors.Write($"The folder's watcher listed {Plural.Of(listing.Count, "folder")} again after a burst of changes at risk of some lost unsaid, "
            + $"{Plural.Of(heard.Count, "path")} heard in its last moments: {Plural.Of(found, "change")} not heard found");

        void Found(string path, bool created)
        {
            string canonical = PathNames.Canonical(path);
            found += heard.ContainsKey(canonical) ? 0 : 1;
            lock (gate)
            {
                handed[canonical] = (Written(path), DateTime.UtcNow);
            }
            Note(path, created, heard: false);
        }
    }

    /// <summary>Changes lost (too many at once) or a watch that failed: everything is read again and recorded anew, and
    /// walked again if lost.</summary>
    private void Lose(bool overflow = true)
    {
        lock (gate)
        {
            lost = true;
        }
        Note(folder, created: true, heard: false);
        if (watching.EachFolder && overflow)
        {
            Rewalk();
        }
        else
        {
            Follow(() => Record(() => WatchFrom(folder)));
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
    /// what is passed over (build output, dependencies, Git's objects) cost no string; only those kept are made canonical.
    /// One not <paramref name="heard"/> was found by listing a folder again, and counts toward no burst.</summary>
    private void Note(string path, bool created, bool heard = true)
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
        if (heard && canonical is not null && WasHanded(canonical, path))
        {
            return;
        }
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
                DateTime now = DateTime.UtcNow;
                heardAt[canonical!] = now;
                if (heard)
                {
                    if (now - lastHeard > Burst)
                    {
                        burstBegan = now;
                    }
                    lastHeard = now;
                    heardInBatch++;
                    if (Path.GetDirectoryName(canonical) is { } holder)
                    {
                        touched[holder] = now;
                    }
                }
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

    /// <summary>Hands the changes over once they have gathered, after looking for those lost when they come in a burst at
    /// risk; held while they gather, then by the crossing, which lets go of what it can never deliver; a method of its own,
    /// so its closure is made only as a batch begins.</summary>
    private void DeliverLater()
    {
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Delay(Gather).ContinueWith(_ =>
        {
            if (Risked() is { } burst)
            {
                Follow(() =>
                {
                    Recover(burst.Began, burst.Since, burst.Folders, burst.Heard);
                    UiThread.Post(Deliver);
                });
                if (burst.Again)
                {
                    RecheckLater();
                }
            }
            else
            {
                UiThread.Post(Deliver);
            }
            held.Dispose();
        }, TaskScheduler.Default));
    }

    /// <summary>Asks for one more look at a burst at risk once it has been quiet for <see cref="Burst"/>, as background work.</summary>
    private void RecheckLater()
    {
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Delay(Burst).ContinueWith(_ =>
        {
            bool again;
            bool begin = false;
            lock (gate)
            {
                again = DateTime.UtcNow - lastRisk < Burst && !disposed;
                if (!again)
                {
                    recheckDue = false;
                    recheck = true;
                    begin = !pending;
                    pending = true;
                }
            }
            if (again)
            {
                RecheckLater();
            }
            else if (begin)
            {
                DeliverLater();
            }
            held.Dispose();
        }, TaskScheduler.Default));
    }

    /// <summary>Whether the batch gathered comes in a burst at risk, or is the look at one once quiet, with the folders
    /// touched and the paths heard in its last moments, and whether one more look is to follow; what is older than they
    /// are let go of.</summary>
    private (DateTime Began, DateTime Since, List<string> Folders, Dictionary<string, DateTime> Heard, bool Again)? Risked()
    {
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;
            batches.Enqueue((now, heardInBatch));
            heardInBatch = 0;
            while (batches.Peek().At < now - Burst)
            {
                batches.Dequeue();
            }
            DateTime since = now - Burst - Gather - Lag;
            Prune(heardAt, since);
            Prune(touched, since);
            foreach ((string path, (DateTime _, DateTime at)) in handed)
            {
                if (at < since)
                {
                    handed.Remove(path);
                }
            }
            bool risky = batches.Sum(b => b.Heard) >= AtRisk || overflowed;
            bool looking = recheck;
            recheck = false;
            if (!risky && !looking)
            {
                return null;
            }
            bool again = risky && !recheckDue;
            if (risky)
            {
                riskBegan = overflowed ? since : burstBegan;
                lastRisk = now;
                recheckDue = true;
            }
            return (riskBegan, since, [.. touched.Keys], new Dictionary<string, DateTime>(heardAt, StringComparer.Ordinal), again);
        }
    }

    /// <summary>Whether a path's change heard now was handed over already, found by listing its folder again: the file as
    /// it was then, or still gone. Asked once, as the change it was found for is heard once.</summary>
    private bool WasHanded(string canonical, string path)
    {
        (DateTime Written, DateTime At) found;
        lock (gate)
        {
            if (handed.Count == 0 || !handed.Remove(canonical, out found))
            {
                return false;
            }
        }
        return found.Written == Written(path);
    }

    /// <summary>A file's last write, or none for a path that is not a file.</summary>
    private static DateTime Written(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    private static void Prune(Dictionary<string, DateTime> times, DateTime before)
    {
        foreach ((string path, DateTime at) in times)
        {
            if (at < before)
            {
                times.Remove(path);
            }
        }
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
                Overflowed = overflowed,
                LaunchProfiles = launchProfiles,
            };
            paths.Clear();
            restored.Clear();
            comeOrGone.Clear();
            structure = projects = launchProfiles = git = lost = overflowed = pending = false;
            live = !disposed && (changes.Paths.Count > 0 || changes.Restored.Count > 0 || changes.Git || changes.Overflowed);
        }
        if (live)
        {
            if (changes.ComeOrGone.Count > 0)
            {
                Follow(() => Keep(changes.ComeOrGone));
            }
            Changed?.Invoke(this, changes);
        }
    }
}
