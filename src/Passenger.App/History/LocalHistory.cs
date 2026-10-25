using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.History;

/// <summary>What Local History kept of a path: its content as first seen, a change to it, its making, its deletion, its
/// move, or a label (the person's own, or the IDE's at an event such as a test run).</summary>
public enum HistoryKind
{
    Seen,
    Changed,
    Created,
    Deleted,
    Moved,
    Label,
}

/// <summary>One thing Local History kept, in a change set (what happened together: a save, the changes made elsewhere
/// seen at once, a revert, a Git checkout): when, to which path (and where a move took it), its content before and after
/// by hash (null for none: not made yet, or deleted), and the set's name when it has one.</summary>
public sealed record HistoryEvent(long Set, DateTimeOffset Time, HistoryKind Kind, string? Path, string? To, string? Before, string? After, string? Name, bool System = false);

/// <summary>How long and how much Local History keeps: 5 days of activity (a pause of 12 hours or more counts for
/// nothing, so a holiday keeps what came before it), and no more content than a size; and its clock.</summary>
public sealed record HistoryKeeping(int Days = 5, long Bytes = 256L * 1024 * 1024)
{
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;
}

/// <summary>What a delete took, read before it went, so Local History can bring it back.</summary>
public sealed record HistoryDeletion(string Path, IReadOnlyList<(string Path, byte[] Bytes)> Files);

/// <summary>The Local History of an open folder, under <c>workspaces/&lt;name&gt;-&lt;hash&gt;/localHistory/</c>: each
/// version of its text files saved, changed elsewhere, deleted or moved, once per hash, compressed, in <c>blobs/</c>, and
/// what happened a line each in <c>changes.log</c>, labels too. Recording is queued and done in order off the UI thread as
/// background work, so a save only reads the file it writes over; it is pruned to <see cref="HistoryKeeping"/> as it opens
/// and once a day. Thread-safe; <see cref="Changed"/> comes on the UI thread.</summary>
public sealed class LocalHistory
{
    /// <summary>The name of changes made by another program.</summary>
    public const string ExternalChange = "External change";

    /// <summary>The most files a folder made or deleted elsewhere is read for at once.</summary>
    private const int WalkLimit = 5000;

    /// <summary>The pause between activities past which the time between counts for nothing.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromHours(12);

    /// <summary>How long after a named activity ends the changes seen elsewhere still take its name: what a Git checkout
    /// wrote is heard a moment after it ended.</summary>
    private static readonly TimeSpan Lingering = TimeSpan.FromSeconds(2);

    /// <summary>A blob none of the history names is deleted only once it is this old, since another Passenger on the
    /// same folder may have written it for a line it is about to add.</summary>
    private static readonly TimeSpan Orphaned = TimeSpan.FromMinutes(10);

    /// <summary>How long a change to the history waits for another Passenger keeping it in the same state to let go of it:
    /// past that an append goes on all the same, and a prune waits for its next turn.</summary>
    private static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, LocalHistory> Stores = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly object gate = new();
    private readonly List<HistoryEvent> events = [];
    private readonly Dictionary<string, string?> known = new(PathNames.Comparer);

    /// <summary>The saves of each path queued and not yet recorded, whose new content the disk may already hold.</summary>
    private readonly Dictionary<string, int> writing = new(PathNames.Comparer);
    private readonly Queue<Action> jobs = new();
    private readonly string log;
    private readonly string held;
    private readonly string blobs;
    private readonly string stateFolder;
    private readonly DateTime openedUtc = DateTime.UtcNow;
    private bool draining;
    private bool changed;
    private int users;
    private long lastSet;
    private DateOnly prunedOn;
    private int sincePrune;
    private (long Set, string Name)? activity;
    private int activityDepth;
    private (long Set, string Name, DateTime Until)? lingering;

    private LocalHistory(string folder, string stateFolder, HistoryKeeping keeping)
    {
        Folder = folder;
        this.stateFolder = stateFolder;
        Keeping = keeping;
        Root = Path.Combine(State.ProjectState.FolderOf(stateFolder, folder), "localHistory");
        log = Path.Combine(Root, "changes.log");
        held = Path.Combine(Root, "changes.lock");
        blobs = Path.Combine(Root, "blobs");
        lastSet = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        // Read first: what is recorded from now on follows what was kept.
        Enqueue(Load);
        FileText.Wrote += OnWrote;
    }

    /// <summary>The open folder whose files it keeps.</summary>
    public string Folder { get; }

    /// <summary>Where it is kept.</summary>
    public string Root { get; }

    public HistoryKeeping Keeping { get; }

    /// <summary>What it kept changed: a change recorded, or old ones pruned. On the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>A folder's history, shared by every window of this process that has the folder open with the same state
    /// folder; let go with <see cref="Release"/>.</summary>
    public static LocalHistory Open(string folder, string stateFolder, HistoryKeeping keeping)
    {
        string key = State.ProjectState.FolderOf(stateFolder, folder);
        lock (Stores)
        {
            if (!Stores.TryGetValue(key, out LocalHistory? history))
            {
                history = new LocalHistory(folder, stateFolder, keeping);
                Stores[key] = history;
            }
            history.users++;
            return history;
        }
    }

    /// <summary>A window no longer has the folder open; the last to let go stops recording (what is queued is still
    /// written).</summary>
    public void Release()
    {
        lock (Stores)
        {
            if (--users > 0)
            {
                return;
            }
            Stores.Remove(State.ProjectState.FolderOf(stateFolder, Folder));
            FileText.Wrote -= OnWrote;
        }
    }

    /// <summary>Names what is recorded until disposed, one change set (an activity: a revert, a Git checkout, a
    /// refactoring): the saves made meanwhile, and what the folder watcher sees changed meanwhile and a moment after.
    /// Nested, the outermost names them.</summary>
    public IDisposable During(string name)
    {
        lock (gate)
        {
            if (activityDepth++ == 0)
            {
                activity = (NewSet(), name);
            }
        }
        return new Ending(this);
    }

    private void End()
    {
        lock (gate)
        {
            if (--activityDepth == 0 && activity is { } ended)
            {
                lingering = (ended.Set, ended.Name, DateTime.UtcNow + Lingering);
                activity = null;
            }
        }
    }

    private sealed class Ending(LocalHistory history) : IDisposable
    {
        private int ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                history.End();
            }
        }
    }

    /// <summary>A file the editor opened, with the bytes it read: its content is known from now on, so a change made
    /// elsewhere has something to compare with; one changed since the history last saw it is an external change.</summary>
    public void Looked(string path, byte[] bytes)
    {
        if (Keeps(path))
        {
            DateTimeOffset time = Keeping.Clock();
            Enqueue(() => RecordLook(path, bytes, time));
        }
    }

    /// <summary>Paths the folder watcher saw change: each file's content is read and compared with what the history
    /// knows; a change is kept under the activity under way, else as an external change. A folder made is read for its
    /// files, one deleted takes what the history knew under it, and a file gone whose content another just made has is
    /// a move.</summary>
    public void Noticed(IReadOnlyCollection<string> paths)
    {
        List<string> kept = [.. paths.Where(Keeps)];
        if (kept.Count == 0)
        {
            return;
        }
        (long set, string name) = Seen();
        DateTimeOffset time = Keeping.Clock();
        Enqueue(() => RecordNotice(kept, set, name, time));
    }

    /// <summary>Reads what a delete is about to take, off the UI thread: the file, or every text file under the folder.</summary>
    public Task<HistoryDeletion?> CaptureAsync(string path)
    {
        if (!Keeps(path))
        {
            return Task.FromResult<HistoryDeletion?>(null);
        }
        return BackgroundWork.Run<HistoryDeletion?>(() =>
        {
            List<(string, byte[])> files = [];
            foreach (string file in Directory.Exists(path) ? Walk(path) : [path])
            {
                if (ReadKept(file) is { } bytes)
                {
                    files.Add((file, bytes));
                }
            }
            return new HistoryDeletion(path, files);
        });
    }

    /// <summary>A delete made here went through: what it took is kept, deleted.</summary>
    public void Deleted(HistoryDeletion deletion)
    {
        (long set, string? name) = Named();
        DateTimeOffset time = Keeping.Clock();
        Enqueue(() => RecordDeletion(deletion, set, name, time));
    }

    /// <summary>A file or folder moved here (a rename in the Explorer, a code fix): its history follows it.</summary>
    public void Moved(string from, string to)
    {
        if (!Keeps(from) && !Keeps(to))
        {
            return;
        }
        (long set, string? name) = Named();
        DateTimeOffset time = Keeping.Clock();
        Enqueue(() => Append([new HistoryEvent(set, time, HistoryKind.Moved, from, to, null, null, name)]));
    }

    /// <summary>Put Label: a label across the folder's history, now; a system label is the IDE's own (Tests
    /// Passed).</summary>
    public void Label(string name, bool system = false)
    {
        long set = NewSet();
        DateTimeOffset time = Keeping.Clock();
        Enqueue(() => Append([new HistoryEvent(set, time, HistoryKind.Label, null, null, null, null, name, system)]));
    }

    /// <summary>Whether what has happened is still being recorded: an agent's look at the history waits for its own saves.</summary>
    public bool IsRecording
    {
        get
        {
            lock (gate)
            {
                return draining || jobs.Count > 0;
            }
        }
    }

    /// <summary>Everything kept so far, oldest first, and the content each path is known to have now (null for deleted).</summary>
    public (IReadOnlyList<HistoryEvent> Events, IReadOnlyDictionary<string, string?> Known) Snapshot()
    {
        lock (gate)
        {
            return ([.. events], new Dictionary<string, string?>(known, (StringComparer)known.Comparer));
        }
    }

    /// <summary>A version's bytes, or null when they are no longer kept.</summary>
    public byte[]? Content(string hash)
    {
        try
        {
            using FileStream stream = new(BlobPath(hash), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using BrotliStream brotli = new(stream, CompressionMode.Decompress);
            using MemoryStream bytes = new();
            brotli.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The content hash Local History names bytes by.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes)[..16]);

    /// <summary>Whether the history keeps a path: a file under the folder, outside the folders every walk passes over and
    /// outside the state store, and neither a save's temporary file nor the system's own.</summary>
    public bool Keeps(string path) =>
        PathNames.IsUnder(path, Folder) && !PathNames.Within(path, stateFolder) && !FileText.IsSaving(path) && !PathNames.IsSystemFile(Path.GetFileName(path))
        && !IgnoredFolders.AnyOf(Folder, Path.GetRelativePath(Folder, path).Split(Path.DirectorySeparatorChar));

    /// <summary>The set and name a save is kept under: the activity under way, else a set of its own.</summary>
    private (long Set, string? Name) Named()
    {
        lock (gate)
        {
            return activity is { } named ? (named.Set, named.Name) : (NewSet(), null);
        }
    }

    /// <summary>The set and name what the folder watcher saw is kept under: the activity under way or just ended, else an
    /// external change.</summary>
    private (long Set, string Name) Seen()
    {
        lock (gate)
        {
            if (activity is { } named)
            {
                return named;
            }
            if (lingering is { } after && DateTime.UtcNow < after.Until)
            {
                return (after.Set, after.Name);
            }
            return (NewSet(), ExternalChange);
        }
    }

    private long NewSet() => Interlocked.Increment(ref lastSet);

    private void OnWrote(string path, byte[]? before, byte[] after)
    {
        try
        {
            string full = PathNames.Canonical(path);
            if (!Keeps(full))
            {
                return;
            }
            (long set, string? name) = Named();
            DateTimeOffset time = Keeping.Clock();
            lock (gate)
            {
                writing[full] = writing.GetValueOrDefault(full) + 1;
            }
            Enqueue(() =>
            {
                try
                {
                    RecordWrite(full, before, after, set, name, time);
                }
                finally
                {
                    lock (gate)
                    {
                        if (--writing[full] == 0)
                        {
                            writing.Remove(full);
                        }
                    }
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            IdeErrors.Write($"Local History: a save of {path} was not kept: {error.Message}");
        }
    }

    // What follows runs on the queue, one job at a time, in the order they were asked for.

    private void Enqueue(Action job)
    {
        lock (gate)
        {
            jobs.Enqueue(job);
            if (draining)
            {
                return;
            }
            draining = true;
        }
        IdeErrors.Observe(BackgroundWork.Run(Drain));
    }

    private void Drain()
    {
        while (true)
        {
            Action job;
            lock (gate)
            {
                if (jobs.Count == 0)
                {
                    draining = false;
                    break;
                }
                job = jobs.Dequeue();
            }
            try
            {
                job();
                if (sincePrune > 1000 || DateOnly.FromDateTime(Keeping.Clock().LocalDateTime) != prunedOn)
                {
                    Prune();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                IdeErrors.Write($"Local History of {Folder}: {error.Message}");
            }
            catch (Exception error)
            {
                // A fault in one job is the IDE's to report; the queue goes on, so the history never stops recording.
                IdeErrors.Report(error);
            }
        }
        bool raise;
        lock (gate)
        {
            raise = changed;
            changed = false;
        }
        if (raise)
        {
            UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    private void Load()
    {
        List<(string Line, HistoryEvent? Event)> logged = Logged();
        List<HistoryEvent> read = [.. logged.Select(l => l.Event).OfType<HistoryEvent>()];
        int bad = logged.Count - read.Count;
        if (bad > 0)
        {
            IdeErrors.Write($"Local History: {bad} lines of {log} did not read and were passed over");
        }
        lock (gate)
        {
            events.AddRange(read);
            foreach (HistoryEvent kept in read)
            {
                Know(kept);
                lastSet = Math.Max(lastSet, kept.Set);
            }
            changed = read.Count > 0;
        }
        Prune();
    }

    /// <summary>The log's lines as they are on disk, each with its event, or null for one this build does not read (a
    /// later build's kind, a line cut short by a crash); read sharing writes, as another Passenger may be appending.</summary>
    private List<(string Line, HistoryEvent? Event)> Logged()
    {
        List<(string Line, HistoryEvent? Event)> logged = [];
        if (!File.Exists(log))
        {
            return logged;
        }
        using FileStream stream = new(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                logged.Add((line, Parse(line)));
            }
        }
        return logged;
    }

    private void RecordWrite(string path, byte[]? before, byte[] after, long set, string? name, DateTimeOffset time)
    {
        if (!IsText(after))
        {
            return;
        }
        string afterHash = Hash(after);
        if (before is not null && !IsText(before))
        {
            Store(after);
            Append([new HistoryEvent(set, time, HistoryKind.Seen, path, null, null, afterHash, null)]);
            return;
        }
        string? beforeHash = before is null ? null : Hash(before);
        List<HistoryEvent> made = [];
        bool isKnown;
        string? was;
        lock (gate)
        {
            isKnown = known.TryGetValue(path, out was);
        }
        if (isKnown && was != beforeHash)
        {
            // The disk held something else than the history knew: changed elsewhere, and not seen yet.
            if (before is not null)
            {
                Store(before);
            }
            made.Add(External(path, was, beforeHash, time));
        }
        else if (before is not null)
        {
            Store(before);
        }
        if (beforeHash != afterHash)
        {
            Store(after);
            made.Add(new HistoryEvent(set, time, before is null ? HistoryKind.Created : HistoryKind.Changed, path, null, beforeHash, afterHash, name));
        }
        Append(made);
    }

    private void RecordLook(string path, byte[] bytes, DateTimeOffset time)
    {
        if (!IsText(bytes))
        {
            return;
        }
        string hash = Hash(bytes);
        bool isKnown;
        string? was;
        lock (gate)
        {
            isKnown = known.TryGetValue(path, out was);
        }
        if (isKnown && was == hash)
        {
            return;
        }
        Store(bytes);
        Append([isKnown ? External(path, was, hash, time) : new HistoryEvent(NewSet(), time, HistoryKind.Seen, path, null, null, hash, null)]);
    }

    /// <summary>A change made elsewhere, found by comparing what the disk holds with what the history knew.</summary>
    private HistoryEvent External(string path, string? was, string? now, DateTimeOffset time) =>
        new(NewSet(), time, was is null ? HistoryKind.Created : now is null ? HistoryKind.Deleted : HistoryKind.Changed, path, null, was, now, ExternalChange);

    private void RecordNotice(IReadOnlyList<string> paths, long set, string name, DateTimeOffset time)
    {
        List<(string Path, string Hash, byte[] Bytes, bool Made)> found = [];
        List<(string Path, string Hash)> gone = [];
        List<string> later = [];
        HashSet<string> looked = new(PathNames.Comparer);
        foreach (string path in paths)
        {
            lock (gate)
            {
                if (writing.Keys.Any(w => PathNames.Within(w, path)))
                {
                    // A save here is queued behind this, and the disk may hold what it wrote: looked at once it is kept.
                    later.Add(path);
                    continue;
                }
            }
            if (Directory.Exists(path))
            {
                // A folder whose files it knows has their changes already; a link holds nothing new.
                bool knownUnder;
                lock (gate)
                {
                    knownUnder = known.Keys.Any(k => PathNames.IsUnder(k, path));
                }
                foreach (string file in knownUnder || new DirectoryInfo(path).LinkTarget is not null ? [] : Walk(path))
                {
                    Look(file);
                }
            }
            else if (File.Exists(path))
            {
                Look(path);
            }
            else
            {
                lock (gate)
                {
                    gone.AddRange(known.Where(k => k.Value is not null && PathNames.Within(k.Key, path) && !File.Exists(k.Key)).Select(k => (k.Key, k.Value!)));
                }
            }
        }
        List<HistoryEvent> made = [];
        HashSet<string> paired = new(PathNames.Comparer);
        foreach ((string path, string hash, byte[] bytes, bool isNew) in found)
        {
            string? was;
            bool isKnown;
            lock (gate)
            {
                isKnown = known.TryGetValue(path, out was);
            }
            // A file gone whose content one made just now has is the same file moved.
            if (!isKnown && gone.FirstOrDefault(g => g.Hash == hash && !paired.Contains(g.Path)) is { Path: not null } from)
            {
                paired.Add(from.Path);
                made.Add(new HistoryEvent(set, time, HistoryKind.Moved, from.Path, path, null, null, name));
                continue;
            }
            Store(bytes);
            made.Add(!isKnown
                ? new HistoryEvent(isNew ? set : NewSet(), time, isNew ? HistoryKind.Created : HistoryKind.Seen, path, null, null, hash, isNew ? name : null)
                : new HistoryEvent(set, time, was is null ? HistoryKind.Created : HistoryKind.Changed, path, null, was, hash, name));
        }
        foreach ((string path, string hash) in gone.Where(g => !paired.Contains(g.Path)))
        {
            made.Add(new HistoryEvent(set, time, HistoryKind.Deleted, path, null, hash, null, name));
        }
        Append(made);
        if (later.Count > 0)
        {
            Enqueue(() => RecordNotice(later, set, name, time));
        }

        void Look(string file)
        {
            if (!Keeps(file) || !looked.Add(file) || ReadKept(file) is not { } bytes)
            {
                return;
            }
            string hash = Hash(bytes);
            lock (gate)
            {
                if (known.TryGetValue(file, out string? was) && was == hash)
                {
                    return;
                }
            }
            found.Add((file, hash, bytes, IsNew(file)));
        }
    }

    /// <summary>Whether a file the history has not seen was made since the folder opened, rather than there unseen.</summary>
    private bool IsNew(string file)
    {
        try
        {
            return File.GetCreationTimeUtc(file) >= openedUtc - TimeSpan.FromSeconds(2);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RecordDeletion(HistoryDeletion deletion, long set, string? name, DateTimeOffset time)
    {
        List<HistoryEvent> made = [];
        HashSet<string> taken = new(PathNames.Comparer);
        foreach ((string path, byte[] bytes) in deletion.Files)
        {
            if (!IsText(bytes))
            {
                continue;
            }
            taken.Add(path);
            made.Add(new HistoryEvent(set, time, HistoryKind.Deleted, path, null, Store(bytes), null, name));
        }
        lock (gate)
        {
            made.AddRange(known.Where(k => k.Value is not null && PathNames.Within(k.Key, deletion.Path) && !taken.Contains(k.Key))
                .Select(k => new HistoryEvent(set, time, HistoryKind.Deleted, k.Key, null, k.Value, null, name)));
        }
        Append(made);
    }

    /// <summary>Keeps events: in memory, in what each path is known to hold, and as lines of the log.</summary>
    private void Append(IReadOnlyList<HistoryEvent> made)
    {
        if (made.Count == 0)
        {
            return;
        }
        lock (gate)
        {
            foreach (HistoryEvent kept in made)
            {
                events.Add(kept);
                Know(kept);
            }
            changed = true;
        }
        Directory.CreateDirectory(Root);
        StringBuilder lines = new();
        foreach (HistoryEvent kept in made)
        {
            lines.Append(Line(kept)).Append('\n');
        }
        using (Hold())
        {
            File.AppendAllText(log, lines.ToString());
        }
        sincePrune += made.Count;
    }

    /// <summary>What a path holds after an event, held under the gate.</summary>
    private void Know(HistoryEvent kept)
    {
        switch (kept.Kind)
        {
            case HistoryKind.Seen or HistoryKind.Changed or HistoryKind.Created:
                known[kept.Path!] = kept.After;
                break;
            case HistoryKind.Deleted:
                known[kept.Path!] = null;
                break;
            case HistoryKind.Moved:
                foreach (KeyValuePair<string, string?> moving in known.Where(k => PathNames.Within(k.Key, kept.Path!)).ToList())
                {
                    known.Remove(moving.Key);
                    known[PathNames.Moved(moving.Key, kept.Path!, kept.To!)] = moving.Value;
                }
                break;
        }
    }

    /// <summary>The purge, as the store opens and once a day: the change sets older than <see cref="HistoryKeeping.Days"/>
    /// of activity go, counting back from the newest, a pause of 12 hours or more counted as nothing, then the oldest until the
    /// rest needs no more content than <see cref="HistoryKeeping.Bytes"/>; the log is written again without them and content
    /// nothing names is deleted. It weighs the log on disk, which another Passenger on the same state (an agent's, over --mcp)
    /// appends to too, held against that one's appends from its read to its last delete (<see cref="Hold"/>).</summary>
    private void Prune()
    {
        DateTimeOffset now = Keeping.Clock();
        prunedOn = DateOnly.FromDateTime(now.LocalDateTime);
        sincePrune = 0;
        using FileStream? holding = Hold();
        if (holding is null)
        {
            // Held past the wait: the next day's prune, or the next thousand changes', tries again.
            return;
        }
        List<(string Line, HistoryEvent? Event)> logged = Logged();
        List<HistoryEvent> all = [.. logged.Select(l => l.Event).OfType<HistoryEvent>()];
        HashSet<string> onDisk = new(all.Select(Key), PathNames.Comparer);
        List<HistoryEvent> unlogged;
        lock (gate)
        {
            // What this store recorded that the log lacks (an append the disk refused) is weighed and written too.
            unlogged = [.. events.Where(e => !onDisk.Contains(Key(e)))];
        }
        all.AddRange(unlogged);
        List<(long Set, DateTimeOffset Time, bool Active)> sets = [.. all.GroupBy(e => e.Set)
            .Select(g => (g.Key, g.Min(e => e.Time), g.Any(e => e.Kind != HistoryKind.Seen))).OrderByDescending(s => s.Item2)];
        // Only changes are activity: a file opened is not, though what was seen of it goes with the changes around it.
        DateTimeOffset? cutoff = null;
        TimeSpan period = TimeSpan.FromDays(Keeping.Days);
        TimeSpan active = TimeSpan.Zero;
        DateTimeOffset? newer = null;
        foreach ((_, DateTimeOffset time, _) in sets.Where(s => s.Active))
        {
            if (newer is { } after)
            {
                TimeSpan delta = after - time;
                active += delta < Pause ? delta : TimeSpan.FromMilliseconds(1);
            }
            newer = time;
            if (active >= period)
            {
                cutoff = time;
                break;
            }
        }
        HashSet<long> dropped = cutoff is { } last ? [.. sets.Where(s => s.Time <= last).Select(s => s.Set)] : [];
        Dictionary<string, long> sizes = BlobSizes();
        Dictionary<string, int> references = [];
        foreach (HistoryEvent kept in all.Where(e => !dropped.Contains(e.Set)))
        {
            Refer(references, kept, 1);
        }
        long total = references.Keys.Sum(h => sizes.GetValueOrDefault(h));
        // The newest set always stays, whatever its size.
        foreach ((long set, _, _) in Enumerable.Reverse(sets).Where(s => !dropped.Contains(s.Set)).SkipLast(1))
        {
            if (total <= Keeping.Bytes)
            {
                break;
            }
            dropped.Add(set);
            foreach (HistoryEvent kept in all.Where(e => e.Set == set))
            {
                foreach (string gone in Refer(references, kept, -1))
                {
                    total -= sizes.GetValueOrDefault(gone);
                }
            }
        }
        if (dropped.Count > 0)
        {
            // Each line kept as it was read, one this build does not read too (a later build's).
            IEnumerable<string> left = logged.Where(l => l.Event is not { } read || !dropped.Contains(read.Set)).Select(l => l.Line)
                .Concat(unlogged.Where(e => !dropped.Contains(e.Set)).Select(Line));
            lock (gate)
            {
                events.RemoveAll(e => dropped.Contains(e.Set));
                changed = true;
            }
            AtomicFile.Write(log, Encoding.UTF8.GetBytes(string.Concat(left.Select(line => line + "\n"))));
        }
        DateTime old = DateTime.UtcNow - Orphaned;
        foreach (string hash in sizes.Keys.Where(h => !references.ContainsKey(h)))
        {
            string file = BlobPath(hash);
            if (File.GetLastWriteTimeUtc(file) < old)
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>An event as it is told apart from the others whether read from the log or held since it was recorded,
    /// whose times differ in their precision.</summary>
    private static string Key(HistoryEvent kept) => $"{kept.Set}|{kept.Kind}|{kept.Before}|{kept.After}|{kept.Path}|{kept.To}";

    /// <summary>Counts an event's content in or out; the hashes no longer named by any.</summary>
    private static List<string> Refer(Dictionary<string, int> references, HistoryEvent kept, int step)
    {
        List<string> unnamed = [];
        foreach (string? hash in new[] { kept.Before, kept.After })
        {
            if (hash is null)
            {
                continue;
            }
            int count = references.GetValueOrDefault(hash) + step;
            if (count <= 0)
            {
                references.Remove(hash);
                unnamed.Add(hash);
            }
            else
            {
                references[hash] = count;
            }
        }
        return unnamed;
    }

    private Dictionary<string, long> BlobSizes()
    {
        Dictionary<string, long> sizes = new(StringComparer.Ordinal);
        if (!Directory.Exists(blobs))
        {
            return sizes;
        }
        foreach (FileInfo blob in new DirectoryInfo(blobs).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (blob.Directory is { } shard && blob.Name.Length == 30 && shard.Name.Length == 2)
            {
                sizes[shard.Name + blob.Name] = blob.Length;
            }
        }
        return sizes;
    }

    /// <summary>Keeps bytes once, compressed, by their hash, written whole so a reader never finds half of them; their
    /// hash.</summary>
    private string Store(byte[] bytes)
    {
        string hash = Hash(bytes);
        string file = BlobPath(hash);
        if (File.Exists(file))
        {
            using (Hold())
            {
                try
                {
                    // Named again: its time says so, so no prune, here or in another Passenger, deletes it as long unnamed.
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                    return hash;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Pruned as unnamed a moment ago: written again.
                }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using MemoryStream compressed = new();
        using (BrotliStream brotli = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(bytes);
        }
        AtomicFile.Write(file, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        return hash;
    }

    private string BlobPath(string hash) => Path.Combine(blobs, hash[..2], hash[2..]);

    /// <summary>The history held against every other Passenger keeping it in the same state (an agent's, over --mcp) while
    /// this one changes it: the log appended to or written again, content taken up again or deleted. A lock file open
    /// sharing nothing, waited for on the queue (never the UI thread) up to <see cref="HeldFor"/>; null when not had.</summary>
    private FileStream? Hold()
    {
        Directory.CreateDirectory(Root);
        long began = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(held, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(began) < HeldFor)
            {
                Thread.Sleep(10);
            }
            catch (IOException refused)
            {
                IdeErrors.Write($"Local History of {Folder}: another Passenger on the same state held {held} past {HeldFor.TotalSeconds:0} s: {refused.Message}");
                return null;
            }
        }
    }

    /// <summary>A file's bytes when the history keeps its content: text, and not too large; else null.</summary>
    private static byte[]? ReadKept(string file)
    {
        try
        {
            return new FileInfo(file).Length <= FileText.KeptLimit && FileText.Read(file) is { } bytes && IsText(bytes) ? bytes : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Text as the editor takes it (<see cref="FileText.LooksBinary"/>), and not too large to keep.</summary>
    private static bool IsText(byte[] bytes) => bytes.Length <= FileText.KeptLimit && !FileText.LooksBinary(bytes);

    /// <summary>The files under a folder the history keeps, as many as it reads at once, found as every walk of the open
    /// folder finds them (<see cref="FolderWalk"/>).</summary>
    private IEnumerable<string> Walk(string top) => FolderWalk.Files(top).Where(Keeps).Take(WalkLimit);

    private string Line(HistoryEvent kept)
    {
        JsonObject line = new()
        {
            ["s"] = kept.Set,
            ["t"] = kept.Time.ToUnixTimeMilliseconds(),
            ["k"] = kept.Kind.ToString().ToLowerInvariant(),
        };
        if (kept.Path is { } path)
        {
            line["p"] = Relative(path);
        }
        if (kept.To is { } to)
        {
            line["o"] = Relative(to);
        }
        if (kept.Before is { } before)
        {
            line["b"] = before;
        }
        if (kept.After is { } after)
        {
            line["a"] = after;
        }
        if (kept.Name is { } name)
        {
            line["n"] = name;
        }
        if (kept.System)
        {
            line["y"] = true;
        }
        return line.ToJsonString(Compact);
    }

    /// <summary>A line of the log, or null when it does not read (a later build's kind, a line cut short by a crash).</summary>
    private HistoryEvent? Parse(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject line || line["s"] is not JsonValue set || line["t"] is not JsonValue time
                || !Enum.TryParse(line["k"]?.GetValue<string>(), ignoreCase: true, out HistoryKind kind))
            {
                return null;
            }
            string? path = line["p"]?.GetValue<string>() is { } relative ? Full(relative) : null;
            if (path is null && kind != HistoryKind.Label || kind == HistoryKind.Moved && line["o"] is null)
            {
                return null;
            }
            return new HistoryEvent(set.GetValue<long>(), DateTimeOffset.FromUnixTimeMilliseconds(time.GetValue<long>()).ToLocalTime(), kind, path,
                line["o"]?.GetValue<string>() is { } to ? Full(to) : null, line["b"]?.GetValue<string>(), line["a"]?.GetValue<string>(),
                line["n"]?.GetValue<string>(), line["y"]?.GetValue<bool>() == true);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private string Relative(string path) => Path.GetRelativePath(Folder, path).Replace('\\', '/');

    private string Full(string relative) => Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar));
}
