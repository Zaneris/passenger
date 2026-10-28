using System.IO.Enumeration;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>A folder, and the kept folders under it, watched for what other programs change in them: changes are gathered
/// for a moment and handed over together, as background work. A burst of changes may lose some unsaid (macOS's watcher
/// drops the word that it did), so what is in each kept folder is recorded, and after a burst the folders it touched or
/// whose entries changed are listed again against the record; changes lost past finding have the folder walked again,
/// and what the record held that the walk no longer finds is handed over as gone. Which folders are kept, which of their
/// entries recorded, what a change is taken for and where a batch goes are the watcher's own.</summary>
public abstract class WatchedTree : IDisposable
{
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(150);

    /// <summary>How many changes heard within <see cref="Burst"/> put them at risk of others lost unsaid: FSEvents drops
    /// what passes its queue of a few hundred, and .NET's watcher passes over its word that it did.</summary>
    private const int AtRisk = 256;

    private static readonly TimeSpan Burst = TimeSpan.FromSeconds(1);

    /// <summary>How long before a burst at risk a file written may have had its change lost, the system's queue having
    /// waited that long for the watcher; a file's change heard since its last write is never handed over again.</summary>
    private static readonly TimeSpan Lag = TimeSpan.FromSeconds(2);

    /// <summary>The most entries the record keeps, and a burst's listing reads; past it, changes at risk are taken for lost.</summary>
    private const int RecordLimit = 200_000;

    private readonly FolderWatching watching;

    /// <summary>Whether each kept folder is watched on its own rather than the whole tree through one watcher.</summary>
    private readonly bool eachFolder;
    private readonly object gate = new();

    /// <summary>Held through a batch's hand-over, so batches go over in the order they were let go of.</summary>
    private readonly Lock handing = new();
    private readonly Dictionary<string, FileSystemWatcher> watches = new(StringComparer.Ordinal);

    /// <summary>When each kept path was last heard of, and each kept folder had one of its paths heard, for the last moments.</summary>
    private readonly Dictionary<string, DateTime> heardAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> touched = new(StringComparer.Ordinal);

    /// <summary>The paths come or gone in the batch gathering, which the record follows once it is handed over.</summary>
    private readonly HashSet<string> cameOrWent = new(StringComparer.Ordinal);

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

    /// <summary>The tree under <paramref name="folder"/> (canonical), watched the system's way, or each kept folder on its own
    /// (<paramref name="eachFolder"/>); nothing is watched until <see cref="Begin"/>.</summary>
    protected WatchedTree(string folder, FolderWatching watching, bool eachFolder)
    {
        Top = folder;
        this.watching = watching;
        this.eachFolder = eachFolder;
    }

    /// <summary>The folder watched.</summary>
    public string Top { get; }

    /// <summary>How many folders and entries the record holds: as they are in the work that keeps the record (<see
    /// cref="Begin"/>'s walk's), a moment old elsewhere.</summary>
    public (int Folders, int Entries) RecordSize => (record.Count, Volatile.Read(ref recorded));

    /// <summary>Whether the watcher handed over, from its first walk, whatever was written since before it watched, so a burst
    /// begun before its record was made, or made again, is listed against the record rather than taken for lost.</summary>
    protected virtual bool FoundFromStart => false;

    /// <summary>The watcher as the IDE's log names it.</summary>
    protected virtual string Shown => $"The watcher of {Top}";

    /// <summary>What the watcher's own state is held by, with what it gathers here.</summary>
    protected object Gate => gate;

    /// <summary>Done once the first walk has watched and recorded every kept folder.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the watcher was disposed, so it watches and hands over nothing more.</summary>
    protected bool IsDisposed
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

    /// <summary>Starts watching, the folders walked off the calling thread (a large tree has thousands to start watching);
    /// <paramref name="walked"/> runs once the first walk is done, as the same work.</summary>
    protected void Begin(Action? walked = null)
    {
        Start(Top, recursive: !eachFolder);
        Follow(() =>
        {
            Record(() => WatchFrom(Top));
            walked?.Invoke();
        });
        lock (gate)
        {
            Ready = following;
        }
    }

    public virtual void Dispose()
    {
        List<FileSystemWatcher> stopped;
        lock (gate)
        {
            disposed = true;
            stopped = [.. watches.Values];
            watches.Clear();
        }
        stopped.ForEach(w => w.Dispose());
    }

    /// <summary>Whether a folder, the top or one under it, is watched and recorded, and walked into.</summary>
    protected abstract bool Kept(string path);

    /// <summary>Whether a kept folder's entry is recorded, and so found come, gone or written by a listing: every one,
    /// unless the watcher wants fewer.</summary>
    protected virtual bool Records(string at, string name, bool folder) => true;

    /// <summary>What a change seen at a path is to the watcher, asked off the gate.</summary>
    protected abstract Notice Noticed(string path, bool created);

    /// <summary>Takes in a change <see cref="Noticed"/> said it takes, under the gate.</summary>
    protected abstract void Take(string path, bool created, Notice notice);

    /// <summary>The batch gathered, let go of under the gate so the next change gathers another, with whether changes were
    /// lost, or the system said it overflowed, since the last batch; null when it has nothing to hand over.</summary>
    protected abstract FolderChanges? Gathered(bool lost, bool overflowed);

    /// <summary>Hands a batch over, from the thread pool to the thread the watcher chooses, once a burst's lost changes
    /// were looked for and the record follows what came or went in it as it was when let go of.</summary>
    protected abstract void Hand(FolderChanges changes);

    /// <summary>The files in a folder moved in that may have changed before it was watched, taken as made with it.</summary>
    protected virtual IEnumerable<string> MovedIn(string folder) => [];

    /// <summary>A folder that could not be watched, or no longer is: the system's watches used up, or another failure.</summary>
    protected virtual void Unwatched(string at, Exception error)
    {
    }

    /// <summary>The system's word that its watches ran out, with how to raise the limit; null for any other error.</summary>
    protected string? OutOfWatches(Exception error) => watching.OutOfWatches(error);

    /// <summary>What a change is to the watcher: <see cref="Takes"/> it in (<see cref="Take"/>); it <see cref="Counts"/>
    /// toward a burst, its folder listed again after one; by its <see cref="Canonical"/> path, when it has one, and as a
    /// walk <see cref="Named"/> it, when that differs. Neither taken nor counted is passed over.</summary>
    protected readonly record struct Notice(bool Takes, bool Counts, string? Canonical, string? Named = null);

    /// <summary>Lets go of the batch gathered and hands it over, from the thread pool: what came or went in it is looked at
    /// off the gate, before anything it goes to looks, and the record takes each as it was then (<see cref="Keep"/>).</summary>
    private void HandOver()
    {
        lock (handing)
        {
            FolderChanges? changes;
            List<string> comeOrGone;
            bool watching;
            lock (gate)
            {
                changes = Gathered(lost, overflowed);
                lost = overflowed = pending = false;
                watching = !disposed;
                comeOrGone = watching ? [.. cameOrWent] : [];
                cameOrWent.Clear();
            }
            if (comeOrGone.Count > 0)
            {
                List<Seen> seen = [.. comeOrGone.Select(Seen.Now)];
                Follow(() => Keep(seen));
            }
            if (changes is not null && watching)
            {
                Hand(changes);
            }
        }
    }

    /// <summary>A path come or gone as a batch was handed over: a folder, a file, or nothing there.</summary>
    private readonly record struct Seen(string Path, bool Folder, bool There)
    {
        public static Seen Now(string path) => Directory.Exists(path) ? new(path, Folder: true, There: true) : new(path, Folder: false, There: File.Exists(path));
    }

    /// <summary>Starts watching a folder, or the whole tree under it; false when it is gone or cannot be watched.</summary>
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
        catch (ArgumentException gone)
        {
            // Deleted since it was seen.
            Unwatched(path, gone);
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
            // The watcher was disposed meanwhile.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Stop(watcher);
            Unwatched(path, error);
            return false;
        }
        return true;
    }

    /// <summary>A file or folder come, <paramref name="made"/> there or moved in. Where each folder is watched on its own,
    /// a folder is watched from now on, so the files already written in one made (with it at once, as <c>dotnet new -o</c>
    /// and an agent write them) are taken as made too, as one watch over the tree hears them; one moved in is itself alone.</summary>
    private void OnCreated(string path, bool made)
    {
        if (eachFolder && Kept(path) && Directory.Exists(path))
        {
            Follow(() =>
            {
                if (made)
                {
                    WatchFrom(path, file => Note(file, created: true));
                    return;
                }
                WatchFrom(path);
                foreach (string file in MovedIn(path))
                {
                    Note(file, created: true);
                }
            });
        }
        Note(path, created: true);
    }

    private void OnDeleted(string path)
    {
        if (eachFolder && IsWatched(path))
        {
            Follow(() => Unwatch(path));
        }
        Note(path, created: true);
    }

    private void OnError(FileSystemWatcher source, Exception error)
    {
        if (OutOfWatches(error) is not null)
        {
            // Its folder went unwatched.
            Follow(() => Stop(source));
            Unwatched(source.Path, error);
            return;
        }
        if (source.Path != Top && (error is DirectoryNotFoundException || !Directory.Exists(source.Path)))
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
    /// background work until it is done, since a file made in a folder only watched from then on shows only then. A
    /// change that fails is reported as it fails, and those after it still run.</summary>
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
            IdeErrors.Observe(following);
        }
    }

    /// <summary>Holds what the watcher does to its record (a batch's come and gone kept, a burst's folders listed again)
    /// until <paramref name="until"/> ends, for a test of what changes on disk meanwhile.</summary>
    public void HoldRecord(Task until)
    {
        lock (gate)
        {
            following = following.ContinueWith(_ => until, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Watches every kept folder not watched yet, lets go of those gone and records them all again: the walk after
    /// events were lost, or after what is kept changed. Asked for once however many watchers lost theirs.</summary>
    protected void Rewalk()
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
            Record(() => WatchFrom(Top));
        });
    }

    /// <summary>Makes the record of the whole folder anew by a walk, which may stop it at its limit; what a whole record
    /// held before that a whole walk no longer finds there is taken as gone.</summary>
    private void Record(Action walk)
    {
        Dictionary<string, Listed>? before = recordedAt is null ? null : new(record, StringComparer.Ordinal);
        record.Clear();
        recorded = 0;
        recordedAt = null;
        walk();
        if (recorded > RecordLimit)
        {
            record.Clear();
            return;
        }
        recordedAt = DateTime.UtcNow;
        foreach ((string at, Listed listed) in before ?? [])
        {
            record.TryGetValue(at, out Listed? now);
            foreach (string name in listed.Names.Where(n => now?.Names.Contains(n) != true))
            {
                string path = Path.Join(at, name);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    Note(path, created: true, heard: false);
                }
            }
        }
    }

    /// <summary>Watches (where each folder is watched on its own) and records a kept folder and the kept folders under it,
    /// each watched before what is in it is read, so a folder or file made meanwhile is either read here or heard of from
    /// its own folder; each file in them is given to <paramref name="found"/>.</summary>
    private void WatchFrom(string top, Action<string>? found = null)
    {
        Stack<string> folders = new([top]);
        while (folders.Count > 0 && !IsDisposed && (eachFolder || recorded <= RecordLimit))
        {
            string at = folders.Pop();
            if (!Directory.Exists(at) || new DirectoryInfo(at).LinkTarget is not null || eachFolder && !Start(at, recursive: false))
            {
                continue;
            }
            if (Listing(at, written: false) is not { } entries)
            {
                continue;
            }
            Keep(at, entries.Stamp, Recorded(at, entries.Entries));
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

    /// <summary>The entries of a folder's listing the record keeps.</summary>
    private List<Entry> Recorded(string at, List<Entry> entries) => [.. entries.Where(entry => Records(at, entry.Name, entry.Folder))];

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

    /// <summary>Keeps the record of what a batch handed over came or went: each name in or out of its folder's record as it
    /// was when handed, so a change since, which what it was handed to may not have seen, still differs from the record
    /// and is found if it goes unheard; a folder come recorded with what is in it, one gone let go of with every folder
    /// recorded under it. The folders' stamps stay as they were, so a change lost beside these still shows.</summary>
    private void Keep(List<Seen> batch)
    {
        if (recordedAt is null)
        {
            return;
        }
        foreach (Seen seen in batch)
        {
            if (Path.GetDirectoryName(seen.Path) is not { } parent || !record.TryGetValue(parent, out Listed? listed))
            {
                continue;
            }
            string name = Path.GetFileName(seen.Path);
            if (seen.There && Records(parent, name, seen.Folder))
            {
                recorded += listed.Names.Add(name) ? 1 : 0;
                if (seen.Folder && !record.ContainsKey(seen.Path) && Kept(seen.Path))
                {
                    WatchFrom(seen.Path);
                }
            }
            else if (!seen.There)
            {
                recorded -= listed.Names.Remove(name) ? 1 : 0;
                Forget(seen.Path);
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
    /// name="since"/> and not heard of after it is taken in as heard; the top gone, all it held. Past the record's limit,
    /// or for a burst <paramref name="began"/> before the record was made, the changes are taken for lost.</summary>
    private void Recover(DateTime began, DateTime since, IReadOnlyCollection<string> folders, Dictionary<string, DateTime> heard)
    {
        if (recordedAt is not { } made || (made > began && !FoundFromStart))
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
            if (!record.TryGetValue(at, out Listed? was))
            {
                continue;
            }
            if (Listing(at, written: true) is not { } now)
            {
                if (at == Top && !Directory.Exists(at))
                {
                    Forget(at, file => Found(file, created: true));
                }
                continue;
            }
            read += now.Entries.Count;
            if (read > RecordLimit)
            {
                Lose();
                return;
            }
            List<Entry> entries = Recorded(at, now.Entries);
            HashSet<string> names = new(entries.Select(e => e.Name), StringComparer.Ordinal);
            foreach (string name in was.Names.Where(n => !names.Contains(n)))
            {
                string path = Path.Join(at, name);
                Forget(path, file => Found(file, created: true));
                Found(path, created: true);
            }
            Keep(at, now.Stamp, entries);
            foreach (Entry entry in entries)
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
        IdeErrors.Write($"{Shown} listed {Plural.Of(listing.Count, "folder")} again after a burst of changes at risk of some lost unsaid, "
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
        IdeErrors.Write($"{Shown} took changes for lost{(overflow ? string.Empty : ", a watch having failed")}: everything in it is read again");
        Note(Top, created: true, heard: false);
        if (eachFolder && overflow)
        {
            Rewalk();
        }
        else
        {
            Follow(() => Record(() => WatchFrom(Top)));
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

    /// <summary>Takes in what the system saw at a path, as the watcher takes it (<see cref="Noticed"/>). One not
    /// <paramref name="heard"/> was found by listing a folder again, and counts toward no burst.</summary>
    protected void Note(string path, bool created, bool heard = true)
    {
        Notice notice = Noticed(path, created);
        if (!notice.Takes && !notice.Counts)
        {
            return;
        }
        if (heard && notice.Canonical is { } canonical && WasHanded(canonical, path))
        {
            return;
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (notice.Takes)
            {
                Take(path, created, notice);
            }
            if (notice.Counts)
            {
                DateTime now = DateTime.UtcNow;
                if (notice.Canonical is { } at)
                {
                    heardAt[at] = now;
                }
                if (heard)
                {
                    if (now - lastHeard > Burst)
                    {
                        burstBegan = now;
                    }
                    lastHeard = now;
                    heardInBatch++;
                    if (Path.GetDirectoryName(notice.Canonical ?? path) is { } holder)
                    {
                        touched[holder] = now;
                    }
                }
                if (created)
                {
                    cameOrWent.Add(notice.Named ?? path);
                }
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
                    try
                    {
                        Recover(burst.Began, burst.Since, burst.Folders, burst.Heard);
                    }
                    finally
                    {
                        // What was heard goes over even if the look failed, or no batch after it would.
                        HandOver();
                    }
                });
                if (burst.Again)
                {
                    RecheckLater();
                }
            }
            else
            {
                HandOver();
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
    protected static DateTime Written(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

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
}
