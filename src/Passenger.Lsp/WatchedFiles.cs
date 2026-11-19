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
    private readonly Dictionary<string, IReadOnlyList<FileWatch>> registered = new(StringComparer.Ordinal);

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
    private IReadOnlyList<FileWatch> watches = [];

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
                return watches;
            }
        }
    }

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
            registered[id] = read;
            watches = [.. registered.Values.SelectMany(w => w)];
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
            watches = [.. registered.Values.SelectMany(w => w)];
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
        List<string> folders = [];
        try
        {
            using JsonDocument read = JsonDocument.Parse(infos);
            if (read.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
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
            // Builds the server cannot read either: it loads nothing of them, so nothing it read is left untold.
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
                server.Note($"Files changed on disk were let go past {KeptLimit}: commands find those changed since only by the time they were written");
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
            foreach (FileEvent change in events)
            {
                if (watches.Any(watch => watch.Covers(change)))
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
                    server.Note($"Changes on disk no registration covered yet were let go past {KeptLimit}");
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
        IReadOnlyList<FileWatch> now = Watches;
        List<string> asked = [.. changes.Where(c => now.Any(w => w.EveryAssembly && w.Covers(c)) && !now.Any(w => !w.EveryAssembly && w.Covers(c))).Select(c => c.Path)];
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
            + (gone.Count > 0 ? $", {gone.Count} of them gone, taken once the projects holding them have let go of them" : string.Empty));
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

/// <summary>A change on disk as the protocol numbers it.</summary>
public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

/// <summary>A file or folder changed on disk, by its canonical path.</summary>
public readonly record struct FileEvent(string Path, FileChangeType Change);

/// <summary>One watcher a server registered: the paths under <see cref="Folder"/> whose names from it match
/// <see cref="Pattern"/> (with no folder, whole paths that match it), for the changes of <see cref="Kinds"/>.</summary>
public sealed record FileWatch(string? Folder, string Pattern, WatchKinds Kinds)
{
    /// <summary>The watcher as a log shows it.</summary>
    public string Shown => $"{Folder}{(Folder is null ? "" : " ")}{Pattern} ({Kinds})";

    /// <summary>What a file's name must end with, or be, for each pattern: read from its last name (<c>*.cs</c>,
    /// <c>*.{cs,csx}</c>, <c>*Tests.cs</c>, <c>project.assets.json</c>); none where any name may match it.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, bool Whole)[]> NamesOfPattern = new(StringComparer.Ordinal);

    /// <summary>Whether a file of this name may match the glob, by its last name alone and in any letter case: false only
    /// for a name it never matches, told without a string made.</summary>
    public bool MayMatch(ReadOnlySpan<char> name)
    {
        (string Text, bool Whole)[] names = NamesOfPattern.GetOrAdd(Pattern, NamesOf);
        if (names.Length == 0)
        {
            return true;
        }
        foreach ((string text, bool whole) in names)
        {
            if (whole ? name.Equals(text, StringComparison.OrdinalIgnoreCase) : name.EndsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>What a pattern's last name asks a file's name to end with or be, each choice's where the whole pattern is
    /// one brace of them; none where it is not of those shapes.</summary>
    private static (string Text, bool Whole)[] NamesOf(string pattern)
    {
        string[] choices = ChoicesOf.GetOrAdd(pattern, Choices);
        if (choices.Length > 1)
        {
            (string Text, bool Whole)[][] each = [.. choices.Select(NamesOf)];
            return each.Any(names => names.Length == 0) ? [] : [.. each.SelectMany(names => names).Distinct()];
        }
        string last = pattern[(pattern.LastIndexOf('/') + 1)..];
        if (last.Length == 0 || last.IndexOfAny(['?', '[', ']', '!', '}']) >= 0 && !last.StartsWith("*.{", StringComparison.Ordinal))
        {
            return [];
        }
        if (last.StartsWith("*.{", StringComparison.Ordinal))
        {
            string inner = last[3..];
            return inner.EndsWith('}') && inner[..^1] is var alternatives && alternatives.IndexOfAny(['*', '?', '[', ']', '{', '}', '!', '/']) < 0
                ? [.. alternatives.Split(',').Select(a => ("." + a, false))]
                : [];
        }
        if (last[0] == '*')
        {
            string rest = last[1..];
            return rest.Length == 0 || rest.IndexOfAny(['*', '{']) >= 0 ? [] : [(rest, false)];
        }
        return last.IndexOfAny(['*', '{']) >= 0 ? [] : [(last, true)];
    }

    /// <summary>Whether the watcher takes every assembly under its folder however deep (<c>**/*.dll</c> alone): a folder of
    /// reference assemblies watched whole rather than file by file, as Roslyn's server watches the .NET installation's packs.</summary>
    public bool EveryAssembly => Folder is not null && Pattern == "**/*.dll";

    /// <summary>Whether the pattern reaches into the folders under <see cref="Folder"/>, where it can match a path of more
    /// than one name.</summary>
    public bool Recursive => Pattern.Contains('/', StringComparison.Ordinal) || Pattern.Contains("**", StringComparison.Ordinal);

    /// <summary>Whether the watcher asks for this change of this path.</summary>
    public bool Covers(FileEvent change)
    {
        WatchKinds kind = change.Change switch
        {
            FileChangeType.Created => WatchKinds.Create,
            FileChangeType.Deleted => WatchKinds.Delete,
            _ => WatchKinds.Change,
        };
        return (Kinds & kind) != 0 && Matches(change.Path);
    }

    /// <summary>Whether the watcher's glob matches a path, whatever changed there: a path outside its folder, or whose name
    /// its glob's last name cannot match (<see cref="MayMatch"/>), told without a string made, as a build's churn is.</summary>
    public bool Matches(string path)
    {
        if (Folder is not null && !PathNames.IsUnder(path, Folder) || !MayMatch(Path.GetFileName(path.AsSpan())))
        {
            return false;
        }
        string named = Folder is null ? PathNames.Slashed(path) : PathNames.Relative(path, Folder) ?? path;
        string relative = "/" + named.TrimStart('/');
        foreach (string choice in ChoicesOf.GetOrAdd(Pattern, Choices))
        {
            string glob = "/" + choice.TrimStart('/');
            // Where the path's folder takes names in any letter case, so does the glob.
            if (EditorConfig.Matches(glob, relative)
                || EditorConfig.Matches(glob.ToLowerInvariant(), relative.ToLowerInvariant()) && PathNames.CaseIgnoredIn(Path.GetDirectoryName(path) ?? path))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Each pattern's choices (<see cref="Choices"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> ChoicesOf = new(StringComparer.Ordinal);

    /// <summary>A pattern whose whole is one brace of choices (<c>{**/*.cs,**/*.csproj}</c>) as its choices, each matched as
    /// a pattern of its own, so one starting <c>**/</c> matches a file in the folder itself, as it would alone; any other
    /// pattern as itself.</summary>
    private static string[] Choices(string pattern)
    {
        if (pattern.Length < 2 || pattern[0] != '{' || pattern[^1] != '}')
        {
            return [pattern];
        }
        List<string> choices = [];
        int depth = 0;
        int start = 1;
        for (int i = 1; i < pattern.Length - 1; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when depth == 0:
                    // The first brace closes before the end: the pattern is more than one brace.
                    return [pattern];
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    choices.Add(pattern[start..i]);
                    start = i + 1;
                    break;
            }
        }
        choices.Add(pattern[start..^1]);
        return depth == 0 ? [.. choices] : [pattern];
    }

    /// <summary>Whether a folder may hold a path the watcher matches, or lies on the way down to its folder: one under its
    /// folder no deeper than the glob's names reach (any depth past a <c>**</c>), or one over it.</summary>
    public bool Reaches(string folder)
    {
        if (Folder is null)
        {
            return Recursive;
        }
        if (PathNames.Within(Folder, folder))
        {
            return true;
        }
        if (!PathNames.IsUnder(folder, Folder))
        {
            return false;
        }
        if (Pattern.Contains("**", StringComparison.Ordinal))
        {
            return true;
        }
        int names = 1;
        foreach (char at in PathNames.Below(folder, Folder))
        {
            names += PathNames.Separators.Contains(at) ? 1 : 0;
        }
        return names <= Pattern.Trim('/').Count('/');
    }

    /// <summary>A watcher as the protocol gives it: its glob a string, or a pattern relative to a folder or workspace
    /// folder's URI; its kinds every change unless it names them. Null for one that cannot be read.</summary>
    internal static FileWatch? Read(JsonElement watcher)
    {
        if (JsonMembers.Value(watcher, "globPattern") is not { } glob)
        {
            return null;
        }
        WatchKinds kinds = JsonMembers.Number(watcher, "kind") is { } given ? (WatchKinds)given : WatchKinds.All;
        if (glob.ValueKind == JsonValueKind.String)
        {
            return new FileWatch(null, glob.GetString()!, kinds);
        }
        if (JsonMembers.Text(glob, "pattern") is not { } pattern || JsonMembers.Value(glob, "baseUri") is not { } place)
        {
            return null;
        }
        string? uri = place.ValueKind == JsonValueKind.String ? place.GetString() : JsonMembers.Text(place, "uri");
        return uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile
            ? new FileWatch(DocumentUris.ToPath(uri), pattern, kinds)
            : null;
    }
}

/// <summary>The kinds of change a watcher asks for, as the protocol's flags.</summary>
[Flags]
public enum WatchKinds
{
    None = 0,
    Create = 1,
    Change = 2,
    Delete = 4,
    All = Create | Change | Delete,
}
