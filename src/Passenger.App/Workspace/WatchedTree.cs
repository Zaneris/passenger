using System.Diagnostics;
using System.IO.Enumeration;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>A folder, and the kept folders under it, watched for what other programs change in them: changes are gathered
/// for a moment and handed over together, as background work, a folder come or gone with each file in it. A burst of
/// changes may lose some unsaid (macOS's watcher drops the word that it did), so what is in each kept folder is recorded
/// as it was handed over, and after a burst the folders it touched or whose entries changed are listed again against the
/// record; changes lost past finding have the folder walked again, and what the record held that the walk no longer finds
/// is handed over as gone. Which folders are kept, which of their entries recorded, what a change is taken for and where
/// a batch goes are the watcher's own.</summary>
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
    private readonly Dictionary<string, Watch> watches = new(StringComparer.Ordinal);

    /// <summary>How many holds on the watcher's hearing nothing are let go of not yet (<see cref="Deaf"/>).</summary>
    private int deaf;

    /// <summary>When each kept path was last heard of, and each kept folder had one of its paths heard, for the last moments
    /// (each path for as long as the system's watcher is behind).</summary>
    private readonly Dictionary<string, DateTime> heardAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> touched = new(StringComparer.Ordinal);

    /// <summary><see cref="touched"/> by a folder's path as a span of a change's: a folder heard before costs no string.</summary>
    private readonly Dictionary<string, DateTime>.AlternateLookup<ReadOnlySpan<char>> touchedAt;

    /// <summary>The paths come or gone in the batch gathering, which the record follows once it is handed over.</summary>
    private readonly HashSet<string> cameOrWent = new(StringComparer.Ordinal);

    /// <summary>The paths found changed by listing a folder again in the last moments, each with its last write then (none
    /// for one gone), so its own change heard late is not handed over twice.</summary>
    private readonly Dictionary<string, (DateTime Written, DateTime At)> handed = new(StringComparer.Ordinal);

    /// <summary>The changes heard in each batch handed over in the last <see cref="Burst"/>, and in the one gathering, each by
    /// when it was handed over, a timestamp of <see cref="Stopwatch.GetTimestamp"/>'s.</summary>
    private readonly Queue<(long At, int Heard)> batches = new();
    private int heardInBatch;

    /// <summary>When the last change was heard, which measures the quiet, and the first after a quiet <see cref="Burst"/>
    /// before it, which is set against when the record was made: timestamps of <see cref="Stopwatch.GetTimestamp"/>'s.</summary>
    private long lastHeard;
    private long burstBegan;

    /// <summary>The record, by kept folder: its entries' names and its stamp as last listed. Read and changed only by
    /// the work <see cref="Follow"/> runs in turn.</summary>
    private readonly Dictionary<string, Listed> record = new(StringComparer.Ordinal);
    private int recorded;

    /// <summary>When the record of the whole folder was done, a timestamp of <see cref="Stopwatch.GetTimestamp"/>'s, or null
    /// while it is made or once it passed its limit; and when the walk that made it began, on the wall clock, as files'
    /// writes are set against it, written there by the work that keeps the record.</summary>
    private long? recordedAt;
    private long recordBeganTicks;

    /// <summary>The changes to which folders are watched, made one at a time in the order they were seen.</summary>
    private Task following = Task.CompletedTask;

    /// <summary>How many walks apart (<see cref="WalksApart"/>) are queued or under way: work queued behind one is held as
    /// background work only once it runs.</summary>
    private int walksApart;
    private bool rewalking;

    /// <summary>The look (<see cref="LookAgainAsync"/>) asked for and not yet begun, done once it has run.</summary>
    private TaskCompletionSource? looking;
    private bool lost;
    private bool overflowed;

    /// <summary>Whether the system said it overflowed since a batch was last looked at for what a burst lost: one said as a
    /// batch is handed over, after its look, is looked for by a batch of its own.</summary>
    private bool unlooked;

    /// <summary>What the next hand-over runs as it looks at the disk, for a test (<see cref="WhileNextHandOver"/>).</summary>
    private Action? meanwhile;

    /// <summary>A burst at risk is looked at once more when it has been quiet a moment, for what it lost after its last
    /// look: whether that look is due, and asked for now, when the burst was last at risk and when it began (timestamps of
    /// <see cref="Stopwatch.GetTimestamp"/>'s).</summary>
    private bool recheckDue;
    private bool recheck;
    private long lastRisk;
    private long riskBegan;
    private bool pending;
    private bool disposed;

    /// <summary>Whether a look for what the system's watcher, behind, has not handed over is asked for and not yet begun, and
    /// when the last such look began on the wall clock (<see cref="OnBehind"/>).</summary>
    private bool lookingBehind;
    private DateTime? lookedBehind;

    /// <summary>How often a look while the system's watcher is behind lists every recorded folder, for a file written in
    /// place, which moves no folder's stamp; the looks between list only those whose stamp moved. When the last such
    /// listing began, a timestamp of <see cref="Stopwatch.GetTimestamp"/>'s, or 0, and on the wall clock, from which the next
    /// takes in what was written.</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(10);
    private long swept;
    private DateTime? sweptAt;

    /// <summary>The tree under <paramref name="folder"/> (canonical), watched the system's way, or each kept folder on its own
    /// (<paramref name="eachFolder"/>); nothing is watched until <see cref="Begin"/>.</summary>
    protected WatchedTree(string folder, FolderWatching watching, bool eachFolder)
    {
        Top = folder;
        this.watching = watching;
        this.eachFolder = eachFolder;
        touchedAt = touched.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The folder watched.</summary>
    public string Top { get; }

    /// <summary>How many folders and entries the record holds: as they are in the work that keeps the record (<see
    /// cref="Begin"/>'s walk's), a moment old elsewhere.</summary>
    public (int Folders, int Entries) RecordSize => (record.Count, Volatile.Read(ref recorded));

    /// <summary>Whether the watcher handed over, from its first walk, whatever was written since before it watched, so a burst
    /// begun before its record was made, or made again, is listed against the record rather than taken for lost.</summary>
    protected virtual bool FoundFromStart => false;

    /// <summary>Whether the walks that make the record hold no background work, nor what waits behind them until it runs: a
    /// folder nothing the window shows waits on, whose walk on a cold disk can go on for minutes.</summary>
    protected virtual bool WalksApart => false;

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

    /// <summary>The folders the system watches: the whole tree's top, or each kept folder.</summary>
    public IReadOnlyCollection<string> Watched
    {
        get
        {
            lock (gate)
            {
                return [.. watches.Keys];
            }
        }
    }

    /// <summary>Has the watcher pass over all the system says (its changes and its watches' failures) until let go of, as
    /// changes the system dropped without a word: for a test of what finds them otherwise.</summary>
    public IDisposable Deaf()
    {
        Interlocked.Increment(ref deaf);
        return new Hearing(this);
    }

    /// <summary>A hold on the watcher's hearing nothing, let go of once.</summary>
    private sealed class Hearing(WatchedTree tree) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Interlocked.Decrement(ref tree.deaf);
            }
        }
    }

    private bool IsDeaf => Volatile.Read(ref deaf) > 0;

    /// <summary>The system's watch of <paramref name="at"/> failing with <paramref name="error"/>, as the system fails it
    /// (its watches used up, too many changes at once, its folder gone), heard however deaf: for a test.</summary>
    public void Fail(string at, Exception error)
    {
        Watch? failed;
        lock (gate)
        {
            failed = watches.GetValueOrDefault(at);
        }
        OnError(failed ?? throw new InvalidOperationException($"{at} is not watched"), error);
    }

    /// <summary>Holds the reading of each of macOS's streams of the tree until let go of, as a reader held up by a loaded
    /// system is: what comes meanwhile waits in the system's queue, which may drop it and say so. For a test.</summary>
    public IDisposable HoldReading()
    {
        List<IDisposable> held;
        lock (gate)
        {
            held = [.. watches.Values.Select(w => w.Mac?.HoldReading()).OfType<IDisposable>()];
        }
        return new Held(held);
    }

    /// <summary>Holds let go of together.</summary>
    private sealed class Held(List<IDisposable> holds) : IDisposable
    {
        public void Dispose() => holds.ForEach(h => h.Dispose());
    }

    /// <summary>How many of the system's watches every tree of the process holds now (<see cref="Watch"/>): back where it
    /// was once the trees made since are disposed. For a test.</summary>
    public static int SystemWatches => Volatile.Read(ref Watch.Held);

    /// <summary>The folder each of macOS's streams the process holds reads from (<see cref="MacFileEvents"/>), which its trees
    /// share; none elsewhere. For a test.</summary>
    public static IReadOnlyList<string> MacStreams => FolderWatching.ReadsMacEvents ? MacFileEvents.Streams : [];

    /// <summary>One of the system's watches of a folder: on macOS a stream of Passenger's own (<see cref="MacFileEvents"/>),
    /// which says when it dropped changes, elsewhere .NET's watcher; counted among those held (<see cref="SystemWatches"/>)
    /// from when it is made until it is disposed.</summary>
    private sealed class Watch : IDisposable
    {
        public static int Held;

        private int disposed;

        public Watch(string path)
        {
            Path = path;
            Interlocked.Increment(ref Held);
        }

        public string Path { get; }

        public FileSystemWatcher? Watcher { get; set; }

        public MacFileEvents? Mac { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }
            Interlocked.Decrement(ref Held);
            Watcher?.Dispose();
            Mac?.Dispose();
        }
    }

    /// <summary>Starts watching, the folders walked off the calling thread (a large tree has thousands to start watching),
    /// once <paramref name="after"/> ends (a test's hold); <paramref name="walked"/> runs once the first walk is done, as the
    /// same work, with each file it found written from <paramref name="since"/> on, whatever its name, read from the same
    /// listings.</summary>
    protected void Begin(DateTime? since = null, Action<List<string>>? walked = null, Task? after = null)
    {
        Start(Top, recursive: !eachFolder);
        FollowWalk(() =>
        {
            List<string> written = [];
            Record(() => WatchFrom(Top, written: since is { } from ? (from, written) : null));
            walked?.Invoke(written);
        }, after);
        lock (gate)
        {
            Ready = following;
        }
        WatchLag.Behind += OnBehind;
    }

    /// <summary>Once disposed, done when what it had queued has run: a walk under way stops at the next folder and nothing
    /// is queued after, so nothing of it reads its folders from then on, which Windows would refuse a move or delete of
    /// one above them for.</summary>
    public Task Stopped
    {
        get
        {
            lock (gate)
            {
                return following;
            }
        }
    }

    public virtual void Dispose()
    {
        WatchLag.Behind -= OnBehind;
        List<Watch> stopped;
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

    /// <summary>Whether a folder's entry, by its name, may be one <see cref="Records"/> keeps, asked before a walk makes a
    /// string of it: an entry it says no to is passed over as if not there (a build's output no registration can match).</summary>
    protected virtual bool Passes(ReadOnlySpan<char> name, bool folder) => true;

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

    /// <summary>Lets go of the batch gathered and hands it over, as the work that keeps the record, after what it asked for
    /// before: a folder come or gone in it brings in each file in it (<see cref="Spread"/>), what came or went is looked at
    /// on disk off the gate, and the record takes each as it was then (<see cref="Keep"/>), before anything the batch goes
    /// to looks.</summary>
    private void HandOver()
    {
        List<string> named;
        Action? then;
        lock (gate)
        {
            named = disposed ? [] : [.. cameOrWent];
            then = meanwhile;
            meanwhile = null;
        }
        then?.Invoke();
        Dictionary<string, Seen> seen = new(StringComparer.Ordinal);
        foreach (string path in named)
        {
            seen[path] = Seen.Now(path);
        }
        Spread(seen);
        FolderChanges? changes;
        List<string> comeOrGone;
        bool watching;
        bool again;
        lock (gate)
        {
            changes = Gathered(lost, overflowed);
            again = unlooked && !disposed;
            lost = overflowed = false;
            pending = again;
            watching = !disposed;
            comeOrGone = watching ? [.. cameOrWent] : [];
            cameOrWent.Clear();
        }
        foreach (string path in comeOrGone.Where(path => !seen.ContainsKey(path)))
        {
            seen[path] = Seen.Now(path);
        }
        Keep([.. comeOrGone.Select(path => seen[path])]);
        if (changes is not null && watching)
        {
            Hand(changes);
        }
        if (again)
        {
            DeliverLater();
        }
    }

    /// <summary>Runs <paramref name="what"/> in the next batch's hand-over, once, as it begins to look at the disk, after the
    /// batch was looked at for a burst at risk: for a test of what the system's watcher says then.</summary>
    public void WhileNextHandOver(Action what)
    {
        lock (gate)
        {
            meanwhile = what;
        }
    }

    /// <summary>Brings into the batch gathering each file of a folder come or gone in it, as found: a folder come that the
    /// record has not is recorded with what is in it, and a recorded folder gone let go of with all it held.</summary>
    private void Spread(Dictionary<string, Seen> named)
    {
        if (recordedAt is null)
        {
            return;
        }
        foreach (Seen seen in named.Values)
        {
            if (!seen.There && record.ContainsKey(seen.Path))
            {
                Forget(seen.Path, Beside);
            }
            else if (seen.Folder && !record.ContainsKey(seen.Path) && Path.GetDirectoryName(seen.Path) is { } parent && record.ContainsKey(parent) && Kept(seen.Path))
            {
                WatchFrom(seen.Path, Beside);
            }
        }

        void Beside(string path)
        {
            if (!named.ContainsKey(path))
            {
                Found(path, created: true);
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
        Watch watch = new(path);
        if (FolderWatching.ReadsMacEvents)
        {
            return StartMac(watch, recursive);
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
            watch.Dispose();
            Unwatched(path, gone);
            return false;
        }
        watch.Watcher = watcher;
        watcher.Created += (_, e) => OnCreated(e.FullPath, made: true);
        watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
        watcher.Changed += (_, e) => OnChanged(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            OnDeleted(e.OldFullPath);
            OnCreated(e.FullPath, made: false);
        };
        watcher.Error += (_, e) =>
        {
            if (!IsDeaf)
            {
                OnError(watch, e.GetException());
            }
        };
        if (!Keep(watch))
        {
            return false;
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
            Stop(watch);
            Unwatched(path, error);
            return false;
        }
        return true;
    }

    /// <summary>Starts macOS's stream of a folder, or the whole tree under it (<see cref="MacFileEvents"/>), its word that it
    /// dropped changes taken as too many at once, and its folder moved or deleted as gone.</summary>
    private bool StartMac(Watch watch, bool recursive)
    {
        if (!Directory.Exists(watch.Path))
        {
            watch.Dispose();
            Unwatched(watch.Path, new DirectoryNotFoundException($"{watch.Path} is not there to watch"));
            return false;
        }
        try
        {
            watch.Mac = new MacFileEvents(watch.Path, recursive, heard => OnMacEvent(watch, heard));
        }
        catch (IOException error)
        {
            watch.Dispose();
            Unwatched(watch.Path, error);
            return false;
        }
        return Keep(watch);
    }

    /// <summary>Takes a watch in as its folder's, unless the watcher was disposed meanwhile or another watch of the folder
    /// was taken in first, either letting go of it.</summary>
    private bool Keep(Watch watch)
    {
        bool watching;
        bool taken;
        lock (gate)
        {
            watching = !disposed;
            taken = watching && watches.TryAdd(watch.Path, watch);
        }
        if (!taken)
        {
            watch.Dispose();
        }
        return watching;
    }

    private void OnMacEvent(Watch watch, MacFileEvent heard)
    {
        if (IsDeaf)
        {
            return;
        }
        switch (heard.Kind)
        {
            case MacFileEventKind.Changed:
                OnChanged(heard.Path);
                break;
            case MacFileEventKind.Made:
                OnCreated(heard.Path, made: true);
                break;
            case MacFileEventKind.MovedIn:
                OnCreated(heard.Path, made: false);
                break;
            case MacFileEventKind.Deleted:
                OnDeleted(heard.Path);
                break;
            case MacFileEventKind.Dropped:
                OnError(watch, new InternalBufferOverflowException($"macOS dropped changes under {watch.Path}, saying so at {heard.Path}"));
                break;
            case MacFileEventKind.Gone:
                OnError(watch, new DirectoryNotFoundException($"{watch.Path} was moved or deleted"));
                break;
        }
    }

    /// <summary>A file or folder come, <paramref name="made"/> there or moved in. Where each folder is watched on its own,
    /// a folder is watched from now on and the files already in it taken in with it: those of one made (with it at once,
    /// as <c>dotnet new -o</c> and an agent write them) as made, as one watch over the tree hears them; those of one moved
    /// in as found.</summary>
    private void OnCreated(string path, bool made)
    {
        if (IsDeaf || IsUnheard(path))
        {
            return;
        }
        if (eachFolder && Kept(path) && Directory.Exists(path))
        {
            Follow(() =>
            {
                if (made)
                {
                    WatchFrom(path, file => Note(file, created: true));
                    return;
                }
                WatchFrom(path, file => Found(file, created: true));
            });
        }
        Note(path, created: true);
    }

    private void OnDeleted(string path)
    {
        if (IsDeaf || IsUnheard(path))
        {
            return;
        }
        if (eachFolder && IsWatched(path))
        {
            Follow(() => Unwatch(path));
        }
        Note(path, created: true);
    }

    private void OnChanged(string path)
    {
        if (!IsDeaf && !IsUnheard(path))
        {
            Note(path, created: false);
        }
    }

    /// <summary>The variable naming a file whose changes every watcher passes over as the system drops one without a word,
    /// by its name in whatever folder: a test's hold, for what finds such a change otherwise.</summary>
    [IdentifierHome]
    public const string UnheardVariable = "PASSENGER_UNHEARD";

    /// <summary>The name <see cref="UnheardVariable"/> gives, or null; a value that is more than a file's name is fatal, as a
    /// malformed flag is.</summary>
    private static readonly string? Unheard = Environment.GetEnvironmentVariable(UnheardVariable) switch
    {
        null or "" => null,
        { } name when name.AsSpan().IndexOfAny(PathNames.Separators) < 0 && name is not ("." or "..") => name,
        { } other => throw new FormatException($"{UnheardVariable} is \"{other}\": it must be a file's name, or unset"),
    };

    /// <summary>Whether a change heard is one <see cref="UnheardVariable"/> has passed over.</summary>
    private static bool IsUnheard(string path) => Unheard is { } name && Path.GetFileName(path.AsSpan()).Equals(name, StringComparison.Ordinal);

    private void OnError(Watch source, Exception error)
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
                unlooked = true;
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
    /// background work until it is done, since a file made in a folder only watched from then on shows only then; behind
    /// a walk apart, held only once it runs. A change that fails is reported as it fails, and those after it still run.
    /// False, nothing queued, once the watcher is disposed.</summary>
    private bool Follow(Action change)
    {
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }
            bool behind = walksApart > 0;
            IDisposable? held = behind ? null : BackgroundWork.Scope();
            following = following.ContinueWith(_ =>
            {
                IDisposable? running = behind ? BackgroundWork.Scope() : null;
                try
                {
                    change();
                }
                finally
                {
                    held?.Dispose();
                    running?.Dispose();
                }
            }, TaskScheduler.Default);
            IdeErrors.Observe(following);
        }
        return true;
    }

    /// <summary>Makes the record anew in turn as <see cref="Follow"/> does, once <paramref name="after"/> ends (a test's
    /// hold): held as background work as other changes are, unless the watcher walks apart.</summary>
    private void FollowWalk(Action walk, Task? after = null)
    {
        bool apart = WalksApart;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            IDisposable? held = apart ? null : BackgroundWork.Scope();
            walksApart += apart ? 1 : 0;
            Task before = after is null ? following : following.ContinueWith(_ => after.Ended(), TaskScheduler.Default).Unwrap();
            following = before.ContinueWith(_ =>
            {
                try
                {
                    walk();
                }
                finally
                {
                    held?.Dispose();
                    if (apart)
                    {
                        lock (gate)
                        {
                            walksApart--;
                        }
                    }
                }
            }, TaskScheduler.Default);
            IdeErrors.Observe(following);
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
        FollowWalk(() =>
        {
            lock (gate)
            {
                rewalking = false;
            }
            foreach (string gone in Watched.Where(path => !Directory.Exists(path)))
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
        Volatile.Write(ref recordBeganTicks, DateTime.UtcNow.Ticks);
        walk();
        if (recorded > RecordLimit)
        {
            record.Clear();
            return;
        }
        recordedAt = Stopwatch.GetTimestamp();
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
    /// its own folder; each file in them is given to <paramref name="found"/>, and each written from <paramref
    /// name="written"/>'s time on, whatever its name, added to its files. The folder itself is looked at first; one under
    /// it is taken as its parent's listing found it, a folder and no link.</summary>
    private void WatchFrom(string top, Action<string>? found = null, (DateTime From, List<string> Files)? written = null)
    {
        if (IsDisposed || !Directory.Exists(top) || new DirectoryInfo(top).LinkTarget is not null)
        {
            return;
        }
        Stack<string> folders = new([top]);
        while (folders.Count > 0 && !IsDisposed && (eachFolder || recorded <= RecordLimit))
        {
            string at = folders.Pop();
            if (eachFolder && !Start(at, recursive: false) || Listing(at, written: written is not null, since: written?.From) is not { } entries)
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
                else if (!entry.Folder)
                {
                    if (entry.Passed)
                    {
                        found?.Invoke(path);
                    }
                    if (written is { } since && entry.Written >= since.From)
                    {
                        since.Files.Add(path);
                    }
                }
            }
        }
    }

    /// <summary>What is in a folder now, its stamp read first so a change made while it is listed moves it past the one
    /// kept, and each file's last write when <paramref name="written"/> (read with the listing on Windows, a look at each
    /// file's own record elsewhere); the system's own files, a save's temporary file and what the watcher never records
    /// (<see cref="Passes"/>) passed over, as every walk does, with no string made for them, but a file written from
    /// <paramref name="since"/> on. Null when it is gone.</summary>
    private (DateTime Stamp, List<Entry> Entries)? Listing(string at, bool written, DateTime? since = null)
    {
        DateTime stamp = Directory.GetLastWriteTimeUtc(at);
        try
        {
            FileSystemEnumerable<Entry> entries = new(at, (ref FileSystemEntry entry) => new Entry(entry.FileName.ToString(), entry.IsDirectory,
                entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0, written && !entry.IsDirectory ? entry.LastWriteTimeUtc.UtcDateTime : default,
                since is null || Passes(entry.FileName, entry.IsDirectory)), Everything)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !PathNames.IsSystemFile(entry.FileName) && !FileText.IsSaving(entry.FileName)
                    && (Passes(entry.FileName, entry.IsDirectory) || since is { } from && !entry.IsDirectory && entry.LastWriteTimeUtc.UtcDateTime >= from),
            };
            return (stamp, [.. entries]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The entries of a folder's listing the record keeps.</summary>
    private List<Entry> Recorded(string at, List<Entry> entries) => [.. entries.Where(entry => entry.Passed && Records(at, entry.Name, entry.Folder))];

    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    /// <summary>A folder's entry as listed: its name, whether it is a folder and a link, a file's last write, and whether
    /// it is one the watcher may record (<see cref="Passes"/>), not listed for its time alone.</summary>
    private readonly record struct Entry(string Name, bool Folder, bool Link, DateTime Written, bool Passed);

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
    /// and is found if it goes unheard; a folder come recorded with what is in it, what the batch did not hand found, and
    /// one gone let go of with every folder recorded under it. The folders' stamps stay as they were, so a change lost
    /// beside these still shows.</summary>
    private void Keep(List<Seen> batch)
    {
        if (recordedAt is null)
        {
            return;
        }
        HashSet<string> handedOver = new(batch.Select(seen => seen.Path), StringComparer.Ordinal);
        // A folder before what is in it, so its files are kept as handed once it is recorded.
        foreach (Seen seen in batch.OrderBy(seen => seen.Path.Length))
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
                    WatchFrom(seen.Path, file =>
                    {
                        if (!handedOver.Contains(file))
                        {
                            Found(file, created: true);
                        }
                    });
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
    private void Recover(long began, DateTime since, IReadOnlyCollection<string> folders, Dictionary<string, DateTime> heard)
    {
        if (recordedAt is not { } made || (made > began && !FoundFromStart))
        {
            Lose();
            return;
        }
        HashSet<string> listing = StampsMoved();
        listing.UnionWith(folders.Where(record.ContainsKey));
        if (ListAgain(listing, since, heard) is not { } found)
        {
            Lose();
            return;
        }
        IdeErrors.Write($"{Shown} listed {Plural.Of(listing.Count, "folder")} again after a burst of changes at risk of some lost unsaid: "
            + $"{Plural.Of(found.Found, "change")} found, {found.Unheard} at a path neither heard nor found in its last moments ({Plural.Of(heard.Count, "path")})");
    }

    /// <summary>Lists again, after the changes already asked for, each recorded folder whose stamp moved since it was listed:
    /// a name come or gone that the system dropped without a word (macOS's watcher passes over its word that it did) is
    /// found against the record and handed over. A file written in place, which moves no folder's stamp, is left to its own
    /// change. Asked again before a look begins, it adds none.</summary>
    public void LookAgain() => IdeErrors.Observe(LookAgainAsync());

    /// <summary><see cref="LookAgain"/>, done once the look has run and what it found was handed over (<see cref="Hand"/>).
    /// While the first walk, or a walk apart, is under way none is asked: the walk reads what is there itself, and one
    /// apart can go on for minutes.</summary>
    public Task LookAgainAsync()
    {
        TaskCompletionSource asked;
        lock (gate)
        {
            if (disposed || !Ready.IsCompleted || walksApart > 0)
            {
                return Task.CompletedTask;
            }
            if (looking is { } waiting)
            {
                return waiting.Task;
            }
            asked = looking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (!Follow(() => Look(asked)))
        {
            // Disposed meanwhile: there is nothing to look at.
            asked.TrySetResult();
        }
        return asked.Task;
    }

    /// <summary>The look <see cref="LookAgainAsync"/> asks for, in turn: what it found handed over at once rather than
    /// gathered a moment, so the caller waiting on it reads it next.</summary>
    private void Look(TaskCompletionSource asked)
    {
        lock (gate)
        {
            looking = null;
        }
        try
        {
            if (recordedAt is null || IsDisposed || StampsMoved() is not { Count: > 0 } listing)
            {
                return;
            }
            if (ListAgain(listing, since: null, new Dictionary<string, DateTime>(StringComparer.Ordinal)) is not { } found)
            {
                Lose();
            }
            else if (found.Found > 0)
            {
                IdeErrors.Write($"{Shown} found {Plural.Of(found.Found, "change")} it never heard, looking again at {Plural.Of(listing.Count, "folder")} whose stamp moved");
            }
            else
            {
                return;
            }
            HandOver();
        }
        finally
        {
            asked.TrySetResult();
        }
    }

    /// <summary>The system's watcher is behind (<see cref="WatchLag"/>): the folders whose stamp moved, and every recorded
    /// folder at each <see cref="SweepEvery"/>, are listed again against the record, each file there written since the last
    /// such look (from <paramref name="since"/>, or the record's walk, at first) and not heard of after it taken in too, in
    /// turn, one look asked at a time. Not while the first walk, or a walk apart, is under way, which reads what is there.</summary>
    private void OnBehind(DateTime since)
    {
        DateTime from;
        lock (gate)
        {
            if (disposed || lookingBehind || !Ready.IsCompleted || walksApart > 0)
            {
                return;
            }
            lookingBehind = true;
            // Nothing written before the record's walk began went unheard of it.
            DateTime recorded = new(Volatile.Read(ref recordBeganTicks), DateTimeKind.Utc);
            from = recorded > since ? recorded : since;
        }
        Follow(() => LookBehind(from));
    }

    /// <summary>The look <see cref="OnBehind"/> asks for, in turn: what it found handed over at once.</summary>
    private void LookBehind(DateTime first)
    {
        Dictionary<string, DateTime> heard;
        DateTime? last;
        DateTime began = DateTime.UtcNow;
        long now = Stopwatch.GetTimestamp();
        bool sweep = swept == 0 || Stopwatch.GetElapsedTime(swept, now) >= SweepEvery;
        lock (gate)
        {
            lookingBehind = false;
            // From a moment before the last look of the kind began, for a write its listing just missed.
            last = sweep ? sweptAt : lookedBehind;
            lookedBehind = began;
            sweptAt = sweep ? began : sweptAt;
            heard = new Dictionary<string, DateTime>(heardAt, StringComparer.Ordinal);
        }
        if (recordedAt is null || IsDisposed)
        {
            return;
        }
        swept = sweep ? now : swept;
        DateTime since = last is { } previous && previous - Lag > first ? previous - Lag : first;
        HashSet<string> listing = sweep ? new(record.Keys, StringComparer.Ordinal) : StampsMoved();
        if (listing.Count == 0)
        {
            return;
        }
        if (ListAgain(listing, since, heard) is not { } found)
        {
            Lose();
        }
        else if (found.Found > 0)
        {
            IdeErrors.Write($"{Shown} found {Plural.Of(found.Found, "change")} the system's watcher, behind, had not handed over, looking at {Plural.Of(listing.Count, "folder")}");
        }
        else
        {
            return;
        }
        HandOver();
    }

    /// <summary>The recorded folders whose stamp moved since they were listed.</summary>
    private HashSet<string> StampsMoved()
    {
        HashSet<string> moved = new(StringComparer.Ordinal);
        foreach ((string at, Listed listed) in record)
        {
            if (Directory.GetLastWriteTimeUtc(at) != listed.Stamp)
            {
                moved.Add(at);
            }
        }
        return moved;
    }

    /// <summary>Lists recorded folders again against the record, taking in each name come or gone and, from <paramref
    /// name="since"/> when given, each file written then and not heard of after it: how many changes were found, and how many
    /// of those at a path not heard; null past the record's limit, where changes are taken for lost.</summary>
    private (int Found, int Unheard)? ListAgain(HashSet<string> listing, DateTime? since, Dictionary<string, DateTime> heard)
    {
        int read = 0;
        int found = 0;
        int unheard = 0;
        // Each folder after those above it, so one gone is let go of with its parent's listing.
        foreach (string at in listing.OrderBy(f => f.Length))
        {
            if (!record.TryGetValue(at, out Listed? was))
            {
                continue;
            }
            if (Listing(at, written: since is not null) is not { } now)
            {
                if (at == Top && !Directory.Exists(at))
                {
                    Forget(at, file => Find(file, created: true));
                }
                continue;
            }
            read += now.Entries.Count;
            if (read > RecordLimit)
            {
                return null;
            }
            List<Entry> entries = Recorded(at, now.Entries);
            HashSet<string> names = new(entries.Select(e => e.Name), StringComparer.Ordinal);
            foreach (string name in was.Names.Where(n => !names.Contains(n)))
            {
                string path = Path.Join(at, name);
                Forget(path, file => Find(file, created: true));
                Find(path, created: true);
            }
            Keep(at, now.Stamp, entries);
            foreach (Entry entry in entries)
            {
                string path = Path.Join(at, entry.Name);
                if (!was.Names.Contains(entry.Name) || entry.Folder && !entry.Link && Kept(path) && !record.ContainsKey(path))
                {
                    // Come, or a folder now where a file was: what is in it comes with it.
                    Find(path, created: true);
                    if (entry.Folder && !entry.Link && Kept(path))
                    {
                        WatchFrom(path, file => Find(file, created: true));
                    }
                }
                else if (!entry.Folder && record.ContainsKey(path))
                {
                    // A file now where a folder was: what was in it is gone.
                    Forget(path, file => Find(file, created: true));
                    Find(path, created: true);
                }
                else if (!entry.Folder && since is { } from && entry.Written >= from && !(heard.TryGetValue(PathNames.Canonical(path), out DateTime last) && last >= entry.Written))
                {
                    Find(path, created: false);
                }
            }
        }
        return (found, unheard);

        void Find(string path, bool created)
        {
            string canonical = PathNames.Canonical(path);
            found++;
            unheard += heard.ContainsKey(canonical) ? 0 : 1;
            Found(path, canonical, created);
        }
    }

    /// <summary>Takes in a change found by listing or walking rather than heard, marked handed over, so its own change heard
    /// later is not handed over twice.</summary>
    private void Found(string path, bool created) => Found(path, PathNames.Canonical(path), created);

    /// <summary>Takes in a file written as it stands, as one found rather than heard: one the watcher knows of before the
    /// system's watcher says so (a save of the IDE's own), which may be minutes behind.</summary>
    protected void TakeWritten(string path) => Found(path, created: false);

    /// <inheritdoc cref="Found(string, bool)"/>
    private void Found(string path, string canonical, bool created)
    {
        lock (gate)
        {
            handed[canonical] = (Written(path), DateTime.UtcNow);
        }
        Note(path, created, heard: false);
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
            FollowWalk(() => Record(() => WatchFrom(Top)));
        }
    }

    /// <summary>Lets go of a folder deleted or moved away and every folder watched under it, whose watches went with it
    /// or now follow it under another name; one made again in its place is watched anew after.</summary>
    private void Unwatch(string path)
    {
        List<Watch> stopped = [];
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

    /// <summary>Lets go of one watch whose folder could not be watched or is gone, unless another has its place.</summary>
    private void Stop(Watch watch)
    {
        lock (gate)
        {
            if (watches.TryGetValue(watch.Path, out Watch? held) && held == watch)
            {
                watches.Remove(watch.Path);
            }
        }
        watch.Dispose();
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
                    long ticks = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(lastHeard, ticks) > Burst)
                    {
                        burstBegan = ticks;
                    }
                    lastHeard = ticks;
                    heardInBatch++;
                    if (Path.GetDirectoryName((notice.Canonical ?? path).AsSpan()) is { IsEmpty: false } holder)
                    {
                        touchedAt[holder] = now;
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
                Follow(HandOver);
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
                again = Stopwatch.GetElapsedTime(lastRisk) < Burst && !disposed;
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
    private (long Began, DateTime Since, List<string> Folders, Dictionary<string, DateTime> Heard, bool Again)? Risked()
    {
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;
            long ticks = Stopwatch.GetTimestamp();
            batches.Enqueue((ticks, heardInBatch));
            heardInBatch = 0;
            while (Stopwatch.GetElapsedTime(batches.Peek().At, ticks) > Burst)
            {
                batches.Dequeue();
            }
            DateTime since = now - Burst - Gather - Lag;
            // Kept while the system's watcher is behind, whose looks pass over what was heard after it was written.
            if (!WatchLag.IsBehind)
            {
                Prune(heardAt, since);
            }
            Prune(touched, since);
            foreach ((string path, (DateTime _, DateTime at)) in handed)
            {
                if (at < since)
                {
                    handed.Remove(path);
                }
            }
            bool risky = batches.Sum(b => b.Heard) >= AtRisk || unlooked;
            bool looking = recheck;
            recheck = false;
            if (!risky && !looking)
            {
                return null;
            }
            bool again = risky && !recheckDue;
            if (risky)
            {
                // Said overflowed, it began as far back as a file written since may have been lost.
                riskBegan = unlooked ? ticks - (long)((Burst + Gather + Lag).TotalSeconds * Stopwatch.Frequency) : burstBegan;
                lastRisk = ticks;
                recheckDue = true;
            }
            unlooked = false;
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
