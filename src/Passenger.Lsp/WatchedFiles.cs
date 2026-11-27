using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.Lsp;

/// <summary>The files a server registered to hear of as they change on disk (<c>workspace/didChangeWatchedFiles</c>),
/// which the client watches for it in place of a watcher of the server's own: each registration's watchers, a glob from a
/// folder (or over whole paths) with the kinds of change it asks for, until the server unregisters it. What is told
/// (<see cref="Tell"/>) goes to the server as one notification of the changes some watcher covers, in the order told,
/// files gone in one of their own to a server with Passenger's loader (<see cref="HasLoader"/>); one no watcher covers yet
/// is kept for a registration that does, which a load sends as it goes and may send after the server said it ended, and
/// told as that comes.
/// Every file heard changed is kept until a command has seen the server hold it as the disk has it (<see cref="Changing"/>).
/// Every registration and notification is written in the server's log. Thread-safe; <see cref="Changed"/> is raised on
/// the thread that registered or unregistered, <see cref="Sent"/> on the telling one.</summary>
public sealed class WatchedFiles
{
    /// <summary>How many changes are kept for a registration still to come: past it they are let go, said in the log.</summary>
    private const int KeptLimit = 100_000;

    private readonly Lock gate = new();
    private readonly Registrations<FileWatch> registered = new();

    /// <summary>The watchers by folder as last made (<see cref="Index"/>), of the list it was made from.</summary>
    private WatchIndex? index;

    /// <summary>The latest change of each file no watcher covered as it was told, kept until one does.</summary>
    private readonly Dictionary<string, FileChangeType> kept = new(PathNames.Comparer);

    /// <summary>How long a changed file the server was found holding otherwise than the disk is waited for, from the first
    /// time it was: a server that never reads it again would otherwise hold every command that waits for it. Longer than a
    /// command's own wait for the server (10 s, as much longer on a slower machine, <see cref="Waits"/>), so the command that
    /// first found it so still waits for it in its last attempt.</summary>
    private static TimeSpan UnreadLimit => Waits.Of(TimeSpan.FromSeconds(15));

    /// <summary>The files heard changed on disk, told the server or about to be, that no command has yet seen it hold as the
    /// disk has them: each with the number of its latest change heard, and when the server was first found holding it otherwise
    /// (a timestamp of Stopwatch's).</summary>
    private readonly Dictionary<string, (long Heard, long? Unread)> changing = new(PathNames.Comparer);

    /// <summary>The number of the latest change heard (<see cref="changing"/>).</summary>
    private long heard;

    /// <summary>When the server was last told of each file's change (a timestamp of Stopwatch's), as far back as
    /// <see cref="KeptLimit"/> files: for what a command that waited out its time says of the files it waited for.</summary>
    private readonly Dictionary<string, long> told = new(PathNames.Comparer);

    /// <summary>Whether changed files were let go past <see cref="KeptLimit"/>, said once until they are fewer.</summary>
    private bool changesOverflowed;
    private readonly LanguageServer server;

    /// <summary>Whether changes were let go past <see cref="KeptLimit"/>, said once until fewer are kept.</summary>
    private bool overflowed;

    private DateTime since = DateTime.UtcNow;

    /// <summary>Each file the builds the server was given write, as it stood when the server was first given them
    /// (<see cref="Given"/>), null for one not there then.</summary>
    private readonly Dictionary<string, FileStamp?> given = new(PathNames.Comparer);

    /// <summary>The most files kept as given (<see cref="Given"/>): past it, the rest are told as a walk finds them.</summary>
    private const int GivenLimit = 20_000;

    internal WatchedFiles(LanguageServer server) => this.server = server;

    /// <summary>Whether the server has Passenger's project loader, which takes files gone from disk once the projects holding
    /// them have let go of them (<see cref="ProjectMethods.Deleted"/>).</summary>
    internal bool HasLoader { get; set; }

    /// <summary>Since when a file may have changed after the server, or the design-time builds it was given to load first,
    /// read it: the server's start, or the moment the earliest of those builds began reading, if before (<see cref="ReadFrom"/>).</summary>
    public DateTime Since
    {
        get
        {
            lock (gate)
            {
                return since;
            }
        }
    }

    /// <summary>The watchers registered changed: one came or went.</summary>
    public event EventHandler? Changed;

    /// <summary>A notification sent the server, with the changes it told.</summary>
    public event EventHandler<IReadOnlyList<FileEvent>>? Sent;

    /// <summary>Every watcher registered now, of every registration.</summary>
    public IReadOnlyList<FileWatch> Watches
    {
        get
        {
            lock (gate)
            {
                return registered.All;
            }
        }
    }

    /// <summary>Every watcher registered now by the folder it is under, made again only as it is read after a registration
    /// came or went, as <see cref="Watches"/> is.</summary>
    public WatchIndex Index
    {
        get
        {
            lock (gate)
            {
                return Indexed();
            }
        }
    }

    /// <summary>The index as it stands, under the gate.</summary>
    private WatchIndex Indexed() => index is { } made && ReferenceEquals(made.All, registered.All) ? made : index = new WatchIndex(registered.All);

    /// <summary>A registration's watchers taken as the server gave them (one with none it can read kept as none), and the
    /// changes kept for it told now.</summary>
    internal void Register(string id, JsonElement? options)
    {
        List<FileWatch> read = [];
        if (JsonMembers.Value(options, "watchers") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (JsonElement watcher in list.EnumerateArray())
            {
                if (FileWatch.Read(watcher) is { } watch)
                {
                    read.Add(watch);
                }
            }
        }
        if (Volatile.Read(ref registrationsHeld) is { } hold && !hold.Until.IsCompleted && read.Any(w => w.Folder is { } at && PathNames.Within(at, hold.Folder)))
        {
            lock (gate)
            {
                heldBack.Add(id);
            }
            UnawaitedWork.Observe(TakeHeldAsync(id, read, hold.Until));
            return;
        }
        Take(id, read);
    }

    /// <summary>The registrations a test holds back (<see cref="HoldRegistrations"/>): those watching under a folder, until a
    /// task has ended.</summary>
    private sealed record HeldRegistrations(string Folder, Task Until);

    private static HeldRegistrations? registrationsHeld;

    /// <summary>The ids of the registrations held back not yet taken, under the gate: one unregistered meanwhile is never taken.</summary>
    private readonly HashSet<string> heldBack = new(StringComparer.Ordinal);

    /// <summary>Takes every server's registrations of watchers under <paramref name="folder"/> only once <paramref name="until"/>
    /// has ended, as registrations that come after a file there was written, for a test.</summary>
    public static void HoldRegistrations(string folder, Task until) => Volatile.Write(ref registrationsHeld, new HeldRegistrations(folder, until));

    /// <summary>A registration held back taken once what it was held for has ended, unless unregistered meanwhile.</summary>
    private async Task TakeHeldAsync(string id, List<FileWatch> read, Task until)
    {
        await until.Ended();
        lock (gate)
        {
            if (!heldBack.Remove(id))
            {
                return;
            }
        }
        Take(id, read);
    }

    /// <summary>A registration's watchers taken, and the changes kept for them told now.</summary>
    private void Take(string id, List<FileWatch> read)
    {
        List<FileEvent> waited;
        lock (gate)
        {
            registered.Set(id, read);
            waited = [.. kept.Select(k => new FileEvent(k.Key, k.Value)).Where(change => read.Any(watch => watch.Covers(change)))];
            foreach (FileEvent change in waited)
            {
                kept.Remove(change.Path);
            }
            server.Note($"Files watched for the server, registration {id}: {string.Join("; ", read.Select(w => w.Shown))}");
            waited = Send(waited, "heard before it registered them");
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Said(waited);
    }

    internal void Unregister(string id)
    {
        lock (gate)
        {
            if (heldBack.Remove(id) || !registered.Remove(id))
            {
                return;
            }
            server.Note($"Files no longer watched for the server, registration {id}");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The server is given a project's design-time builds (<paramref name="infos"/>, Roslyn's ProjectFileInfo for
    /// each of its frameworks), which it reads what they name from as it loads them: each file of the folders they and the
    /// restore before them write (where the project's assets file and its intermediate assembly lie) is kept as the builds
    /// read it (<paramref name="inputs"/>), else as it stands now, the first time it is given, so a watcher's first walk
    /// tells the server of none that stands so still (<see cref="StandsAsGiven"/>).</summary>
    internal void Given(string infos, IReadOnlyList<InputRead> inputs)
    {
        // Builds the server cannot read either: it loads nothing of them, so nothing it read is left untold.
        if (WrittenFolders(infos) is not { } folders)
        {
            return;
        }
        lock (gate)
        {
            foreach (InputRead input in inputs)
            {
                if (given.Count < GivenLimit && folders.Any(f => PathNames.IsUnder(input.Path, f)))
                {
                    given.TryAdd(input.Path, input.Stamp);
                }
            }
        }
        foreach (string folder in folders)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    FileStamp? stamp = FileStamp.Of(file);
                    lock (gate)
                    {
                        if (given.Count >= GivenLimit)
                        {
                            return;
                        }
                        given.TryAdd(file, stamp);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or changing as it was listed: what was listed is kept, the rest told as a walk finds it.
            }
        }
    }

    /// <summary>The folders a project's design-time builds (<paramref name="infos"/>) and the restore before them write, where
    /// its assets file and its intermediate assembly lie, none within another; null for builds that are not Roslyn's
    /// ProjectFileInfo array.</summary>
    internal static List<string>? WrittenFolders(string infos)
    {
        List<string> folders = [];
        try
        {
            using JsonDocument read = JsonDocument.Parse(infos);
            if (read.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (JsonElement info in read.RootElement.EnumerateArray())
            {
                foreach (string? file in (string?[])[JsonMembers.Text(info, "projectAssetsFilePath"), JsonMembers.Text(info, "intermediateOutputFilePath")])
                {
                    if (Path.GetDirectoryName(file) is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) && !folders.Any(f => PathNames.Within(folder, f)))
                    {
                        folders.RemoveAll(f => PathNames.IsUnder(f, folder));
                        folders.Add(folder);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return folders;
    }

    /// <summary>Whether a file stands as it did when the server was first given the builds that write it (<see cref="Given"/>),
    /// so what the server read of it, after that, is what it holds.</summary>
    public bool StandsAsGiven(string path)
    {
        FileStamp? then;
        lock (gate)
        {
            if (!given.TryGetValue(path, out then))
            {
                return false;
            }
        }
        return then is { } stamp && FileStamp.Of(path) == stamp;
    }

    /// <summary>The server is given design-time builds that began reading at <paramref name="read"/> (none with null),
    /// before it loads anything, so before anything is watched for it.</summary>
    internal void ReadFrom(DateTime? read)
    {
        lock (gate)
        {
            if (read is { } began && began < since)
            {
                since = began;
            }
        }
    }

    /// <summary>Files changed on disk whose change is on its way to the server, kept as <see cref="Changing"/> from now, before
    /// it is told (<see cref="Tell"/>).</summary>
    public void Hearing(IEnumerable<string> paths)
    {
        lock (gate)
        {
            foreach (string path in paths)
            {
                Heard(path);
            }
        }
    }

    /// <summary>The files heard changed on disk that no command has seen the server hold as the disk has them since, and the
    /// number of the latest change among them, to settle them by (<see cref="Settle"/>).</summary>
    public (IReadOnlyList<string> Paths, long AsOf) Changing()
    {
        lock (gate)
        {
            return ([.. changing.Keys], heard);
        }
    }

    /// <summary>When the server was last told a file changed on disk, a timestamp of Stopwatch's; null when it was not since
    /// it started, or too many files ago.</summary>
    public long? ToldAt(string path)
    {
        lock (gate)
        {
            return told.TryGetValue(path, out long at) ? at : null;
        }
    }

    /// <summary>Of the files <see cref="Changing"/> gave as of a number, those not heard changed since let go but those
    /// <paramref name="behind"/>, which the command waits for; of those, each the server was found holding otherwise than the
    /// disk (<paramref name="unread"/>) let go once <see cref="UnreadLimit"/> has passed since it first was, said in the log.</summary>
    public void Settle(IReadOnlyList<string> asked, long asOf, IReadOnlyCollection<string> behind, IReadOnlyCollection<string> unread)
    {
        HashSet<string> waited = new(behind, PathNames.Comparer);
        HashSet<string> notRead = new(unread, PathNames.Comparer);
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            foreach (string path in asked)
            {
                if (!changing.TryGetValue(path, out (long Heard, long? Unread) change) || change.Heard > asOf)
                {
                    continue;
                }
                if (!waited.Contains(path))
                {
                    changing.Remove(path);
                }
                else if (notRead.Contains(path) && change.Unread is { } first && Stopwatch.GetElapsedTime(first, now) > UnreadLimit)
                {
                    changing.Remove(path);
                    server.Note($"The server still held {path} otherwise than the disk {Durations.Said(UnreadLimit)} after it was first found so: "
                        + "commands no longer wait for it to read it");
                }
                else if (notRead.Contains(path) && change.Unread is null)
                {
                    changing[path] = change with { Unread = now };
                }
            }
            changesOverflowed &= changing.Count >= KeptLimit;
        }
    }

    /// <summary>A file heard changed, under the gate.</summary>
    private void Heard(string path)
    {
        if (changing.Count >= KeptLimit && !changing.ContainsKey(path))
        {
            if (!changesOverflowed)
            {
                changesOverflowed = true;
                server.Note($"Files changed on disk were let go past {Numbers.Said(KeptLimit)}: commands find those changed since only by the time they were written");
            }
            return;
        }
        changing[path] = (++heard, null);
    }

    /// <summary>Tells the server of the changes some watcher covers, by their canonical paths, in one notification, keeping
    /// the rest for a registration to come; how many it was told of. Each is matched against every watcher, so the caller
    /// tells a batch from off the UI thread.</summary>
    public int Tell(IReadOnlyCollection<FileEvent> events) => Tell(events, why: null, heard: true);

    /// <summary>Tells the server, as <see cref="Tell"/> does, of files found on disk otherwise than it holds them, which no
    /// watcher told it of since they were written (a file written in place where the system's watcher is behind).</summary>
    public int TellFound(IReadOnlyCollection<FileEvent> found) => Tell(found, "found held otherwise than the disk", heard: false);

    /// <summary>The changes some watcher covers told, the rest kept; <paramref name="heard"/> when a watcher heard them.</summary>
    private int Tell(IReadOnlyCollection<FileEvent> events, string? why, bool heard)
    {
        List<FileEvent> covered = [];
        lock (gate)
        {
            WatchIndex watches = Indexed();
            foreach (FileEvent change in events)
            {
                if (watches.Covers(change))
                {
                    covered.Add(change);
                }
                else if (kept.Count < KeptLimit || kept.ContainsKey(change.Path))
                {
                    kept[change.Path] = change.Change;
                }
                else if (!overflowed)
                {
                    overflowed = true;
                    server.Note($"Changes on disk no registration covered yet were let go past {Numbers.Said(KeptLimit)}");
                }
            }
            covered = Send(covered, why, heard);
        }
        Said(covered);
        return covered.Count;
    }

    /// <summary>The changes a test holds back from the server (<see cref="HoldTelling"/>): which files, until what, whether
    /// those found otherwise than the server holds them too, those held back so far, and what is done once one is; under
    /// the gate.</summary>
    private (Func<string, bool> Which, Task Until, bool Found, List<FileEvent> Kept, TaskCompletionSource Reached)? held;

    /// <summary>Holds back from the server the changes a watcher heard to the files <paramref name="which"/> takes until
    /// <paramref name="until"/> has ended, then tells it of them, as a watcher slow to hear them would, for a test; with
    /// <paramref name="found"/>, those found otherwise than it holds them too (<see cref="TellFound"/>), as a server that
    /// never reads them again. Done once one is held back.</summary>
    public Task HoldTelling(Func<string, bool> which, Task until, bool found = false)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            held = (which, until, found, [], reached);
        }
        UnawaitedWork.Observe(TellHeldAsync(until));
        return reached.Task;
    }

    /// <summary>Tells the server of the changes held back once what they were held for has ended.</summary>
    private async Task TellHeldAsync(Task until)
    {
        await until.Ended();
        List<FileEvent> kept;
        lock (gate)
        {
            if (held is not { } hold || hold.Until != until)
            {
                return;
            }
            held = null;
            kept = Send(hold.Kept, "held back until now");
        }
        Said(kept);
    }

    /// <summary>Of changes about to be told (<see cref="Tell"/>), those where only a folder of assemblies watched whole
    /// covers them (<see cref="FileWatch.EveryAssembly"/>, the .NET installation's packs) kept only for a file a project of
    /// the server's references, as Passenger's extension answers: told any .dll written there, Roslyn's server runs every
    /// project's source generators again. Those passed over are said in its log; every change is kept where the extension
    /// cannot be asked.</summary>
    public async Task<IReadOnlyList<FileEvent>> ReferencedAsync(IReadOnlyList<FileEvent> changes, CancellationToken cancel)
    {
        WatchIndex now = Index;
        List<string> asked = [.. changes.Where(now.CoveredByEveryAssemblyAlone).Select(c => c.Path)];
        if (asked.Count == 0 || server.ExtensionProblem is not null)
        {
            return changes;
        }
        HashSet<string> referenced;
        try
        {
            ReferencedFound found = await RoslynExtension.AskWorkspaceAsync<ReferencedFound>(server, Handlers.ReferencedFiles, new ReferencedAsked(asked), cancel).ConfigureAwait(false);
            referenced = new(found.Paths, PathNames.Comparer);
        }
        catch (LanguageServerFailed)
        {
            return changes;
        }
        HashSet<string> passed = new(asked.Where(path => !referenced.Contains(path)), PathNames.Comparer);
        if (passed.Count == 0)
        {
            return changes;
        }
        server.Note($"Passed over {Plural.Of(passed.Count, "assembly", "assemblies")} changed on disk in a folder watched whole that no project references, {asked.First(passed.Contains)} first");
        return [.. changes.Where(c => !passed.Contains(c.Path))];
    }

    /// <summary>The notification itself, under the gate, so what is told reaches the server in the order told; files gone
    /// in one of their own to a server with Passenger's loader. What it told, all but those a test holds back of what a
    /// watcher <paramref name="heard"/>, or of what was found.</summary>
    private List<FileEvent> Send(List<FileEvent> changes, string? why, bool heard = true)
    {
        if (held is { } hold && (heard || hold.Found) && changes.Any(c => hold.Which(c.Path)))
        {
            hold.Kept.AddRange(changes.Where(c => hold.Which(c.Path)));
            hold.Reached.TrySetResult();
            changes = [.. changes.Where(c => !hold.Which(c.Path))];
        }
        if (changes.Count == 0)
        {
            return changes;
        }
        if (told.Count + changes.Count > KeptLimit)
        {
            told.Clear();
        }
        long now = Stopwatch.GetTimestamp();
        foreach (FileEvent change in changes)
        {
            Heard(change.Path);
            told[change.Path] = now;
            // An earlier change kept for a registration to come is behind this one.
            kept.Remove(change.Path);
        }
        overflowed &= kept.Count >= KeptLimit;
        List<FileEvent> gone = HasLoader ? [.. changes.Where(c => c.Change == FileChangeType.Deleted)] : [];
        List<FileEvent> rest = gone.Count == 0 ? changes : [.. changes.Where(c => c.Change != FileChangeType.Deleted)];
        if (rest.Count > 0)
        {
            server.Notify(LspMethods.DidChangeWatchedFiles, Notified(rest));
        }
        if (gone.Count > 0)
        {
            server.Notify(ProjectMethods.Deleted, Notified(gone));
        }
        server.Note($"Told the server of {Plural.Of(changes.Count, "file")} changed on disk{(why is null ? string.Empty : ", " + why)}, {changes[0].Path} first"
            + (gone.Count > 0 ? $", {Numbers.Said(gone.Count)} of them gone, taken once the projects holding them have let go of them" : string.Empty));
        return changes;
    }

    private static object Notified(List<FileEvent> changes) =>
        new { changes = changes.Select(c => new { uri = new Uri(c.Path).AbsoluteUri, type = (int)c.Change }).ToList() };

    private void Said(List<FileEvent> changes)
    {
        if (changes.Count > 0)
        {
            Sent?.Invoke(this, changes);
        }
    }
}
