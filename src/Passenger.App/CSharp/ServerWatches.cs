using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>The files a language server registered to hear of (<see cref="WatchedFiles"/>), watched for it: those of the
/// open folder by the folder's own watcher (<see cref="Heard"/>), the rest by watchers of this one's own, each folder once:
/// outside the folder, inside a folder the folder's watcher passes over, and the build output however deep under a folder
/// registered with what is under it; with no folder watcher, every folder registered. Each is watched as the folder's
/// watcher watches its folder (<see cref="WatchedTree"/>), recorded only as deep as the registrations over it reach and
/// only the files they match, so what a burst lost is found. A watcher starts as its registration comes, and what was
/// written in its folder since the server, or a design-time build it was given, may have read it is told then
/// (<see cref="WatchedFiles.Since"/>). What changed reaches the server from the thread pool, one notification for each
/// batch, in the order each watcher saw it. The folders watched, and any that cannot be, are said in the IDE's log.</summary>
public sealed class ServerWatches : IDisposable
{
    /// <summary>How far a file system's clock may round a file's time down: one written as the server, or a build, began
    /// reading counts.</summary>
    private static readonly TimeSpan Rounding = TimeSpan.FromSeconds(2);

    private const string NotThere = "it is not there; it is watched if a registration names it once it is";

    private readonly LanguageServer server;
    private readonly WatchedFiles watched;
    private readonly Lock gate = new();
    private readonly Lock refreshing = new();
    private readonly Dictionary<(string Folder, bool Recursive), Watched> own = [];

    /// <summary>Passed-over folders come or gone since the last refresh, whose watchers are made anew: one deleted and made
    /// again in a moment is another folder to Linux's and Windows' watchers.</summary>
    private readonly HashSet<string> renewed = new(PathNames.Comparer);

    /// <summary>The build output found under each folder registered with what is under it (<see cref="BuildOutput"/>), kept
    /// until what the folder's watcher hears may have moved it (<see cref="Heard"/>).</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> outputs = new(PathNames.Comparer);

    /// <summary>How many times <see cref="outputs"/> was let go of, so a walk that ran meanwhile is not kept.</summary>
    private int outputsDropped;

    /// <summary>The folders a watcher could not be started for, said once each.</summary>
    private readonly HashSet<string> unwatchable = new(PathNames.Comparer);

    /// <summary>The folders about to go or move, under which nothing is watched until they are let go of (<see cref="LetGo"/>).</summary>
    private readonly List<string> going = [];

    /// <summary>Watchers no longer needed, stopped once those replacing them watch: one under a folder about to go stops at once.</summary>
    private readonly HashSet<Watched> retiring = [];
    private string? folder;
    private bool refreshRunning;
    private bool refreshAgain;
    private bool disposed;

    /// <summary>What the watchers of this one's own saw is told the server one batch after another in the order they were
    /// seen, so a file made and then deleted is never told the other way round, a watcher's walk as it starts among them.</summary>
    private Task telling = Task.CompletedTask;

    /// <summary>What the folder's watcher saw, told in a turn of its own: none of its files is another watcher's, so a walk
    /// of a large folder watched apart from it (the references' packs) never holds what was written in the folder.</summary>
    private Task tellingHeard = Task.CompletedTask;

    /// <summary>The first walk of a watcher a test holds (<see cref="HoldNextWalk"/>): of which folder, what it waits for, and
    /// done once it waits.</summary>
    private (Func<string, bool> Which, Task Until, TaskCompletionSource Holding)? walkHeld;

    /// <summary>Watches what the server registered, and what it registers from now on, beside the watcher of a folder
    /// (canonical) whose changes it is given (<see cref="Heard"/>); with none (a server no window has), alone.</summary>
    public ServerWatches(LanguageServer server, string? folder)
    {
        this.server = server;
        watched = server.WatchedFiles;
        this.folder = folder;
        watched.Changed += OnRegistered;
        FileText.Writing += OnWriting;
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

    /// <summary>The watcher of a folder watched apart from the folder's own watcher, if it is.</summary>
    public WatchedTree? Of(string folder)
    {
        lock (gate)
        {
            return own.FirstOrDefault(w => PathNames.Same(w.Key.Folder, folder)).Value;
        }
    }

    /// <summary>Watches from now on beside another folder's watcher, or alone (null), the watchers it then needs started
    /// before those it no longer needs stop, so nothing changes unheard in between: a server a window hands over or takes up.</summary>
    public void Follow(string? folder)
    {
        lock (gate)
        {
            this.folder = folder;
            DropOutputs();
        }
        RefreshNow();
    }

    /// <summary>What the folder's watcher saw: each file told as made, changed or gone, every file of the folder too when
    /// what changed was lost; and the build output looked for again where a project, a restore or a passed-over folder came,
    /// went or changed, a passed-over folder come or gone watched anew or let go of.</summary>
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
        if (changes.Lost || changes.Paths.Count > 0)
        {
            watched.Hearing(changes.Paths);
            InTurn(() => Tell(changes, open, recursive: true, IgnoredFolders.Is), heard: true);
        }
        List<string> passedOver = [.. changes.ComeOrGone.Where(IgnoredFolders.Is)];
        // A project's folder moved in, out or within brings its project file; Godot.NET.Sdk's output comes with its first restore.
        if (passedOver.Count > 0 || changes.Projects || changes.Restored.Count > 0 || changes.Lost)
        {
            lock (gate)
            {
                renewed.UnionWith(passedOver);
                DropOutputs();
            }
            Refresh();
        }
    }

    /// <summary>Lets go of the build output found, to be looked for again; under the gate.</summary>
    private void DropOutputs()
    {
        outputs.Clear();
        outputsDropped++;
    }

    public void Dispose()
    {
        watched.Changed -= OnRegistered;
        FileText.Writing -= OnWriting;
        List<Watched> stopped;
        lock (gate)
        {
            disposed = true;
            stopped = [.. own.Values];
            own.Clear();
        }
        stopped.ForEach(w => w.Dispose());
    }

    private void OnRegistered(object? sender, EventArgs e) => Refresh();

    /// <summary>Stops watching what is under folders about to go or move (deleted, renamed or moved by the IDE), and starts
    /// nothing there until disposed, when what the registrations need is watched again: Windows moves and deletes no folder
    /// with a folder inside it watched. Done once no refresh still starts a watcher there.</summary>
    public IDisposable LetGo(IReadOnlyList<string> folders)
    {
        List<Watched> stopped;
        // Taken as a refresh takes it, so none starts a watcher under them after this.
        lock (refreshing)
        {
            lock (gate)
            {
                going.AddRange(folders);
                List<(string Folder, bool Recursive)> under = [.. own.Keys.Where(k => Going(k.Folder))];
                stopped = [.. under.Select(k => own[k]), .. retiring.Where(w => Going(w.Top))];
                under.ForEach(k => own.Remove(k));
            }
        }
        stopped.ForEach(w => w.Dispose());
        if (stopped.Count > 0)
        {
            IdeErrors.Write($"C# language server (process {server.ProcessId}): no longer watched for it, as {Plural.Listed([.. folders])} {Plural.Agreeing(folders.Count, "goes or moves", "go or move")}: "
                + string.Join(", ", stopped.Select(w => w.Top)));
        }
        return new LettingGo(this, folders);
    }

    /// <summary>Whether a folder is under one about to go or move; asked under the gate.</summary>
    private bool Going(string folder) => going.Any(g => PathNames.Within(folder, g));

    private sealed class LettingGo(ServerWatches watches, IReadOnlyList<string> folders) : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            if (released)
            {
                return;
            }
            released = true;
            lock (watches.gate)
            {
                foreach (string folder in folders)
                {
                    watches.going.Remove(folder);
                }
                watches.DropOutputs();
            }
            watches.Refresh();
        }
    }

    /// <summary>Holds the first walk of the next watcher started apart from the folder's for a folder <paramref name="which"/>
    /// takes, in its turn of telling the server, until <paramref name="until"/> ends, as a large folder's walk goes on a while
    /// on a busy machine, for a test of what is told meanwhile. Done once it is held.</summary>
    public Task HoldNextWalk(Func<string, bool> which, Task until)
    {
        TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            walkHeld = (which, until, holding);
        }
        return holding.Task;
    }

    /// <summary>What a watcher's first walk of a folder waits for in its turn: a test's hold of it (<see cref="HoldNextWalk"/>),
    /// else nothing.</summary>
    private Task WalkHeld(string top)
    {
        lock (gate)
        {
            if (walkHeld is not { } held || !held.Which(top))
            {
                return Task.CompletedTask;
            }
            walkHeld = null;
            held.Holding.TrySetResult();
            return held.Until;
        }
    }

    /// <summary>A file of the open folder Passenger is saving is kept as changing from now, on whatever thread saves it: a
    /// command asked before the folder's watcher hears of the save still waits for the server to read it.</summary>
    private void OnWriting(string path)
    {
        string? open;
        lock (gate)
        {
            open = folder;
        }
        string saved = PathNames.Canonical(path);
        if (open is not null && PathNames.IsUnder(saved, open))
        {
            watched.Hearing([saved]);
        }
    }

    /// <summary>What changed in a folder, each file told as made, changed or gone; with changes lost there, every other file
    /// in it (and in the folders under it when <paramref name="recursive"/>, but those <paramref name="passedOver"/> names)
    /// told as changed.</summary>
    private void Tell(FolderChanges changes, string top, bool recursive, Func<string, bool> passedOver)
    {
        List<FileEvent> events = new(changes.Paths.Count);
        foreach (string path in changes.Paths)
        {
            bool there = File.Exists(path) || Directory.Exists(path);
            events.Add(new FileEvent(path, !there ? FileChangeType.Deleted : changes.ComeOrGone.Contains(path) ? FileChangeType.Created : FileChangeType.Changed));
        }
        if (changes.Lost)
        {
            HashSet<string> named = new(changes.Paths, PathNames.Comparer);
            events.AddRange(Every(top, recursive, passedOver, since: null).Where(change => !named.Contains(change.Path)));
        }
        watched.Tell(events);
    }

    /// <summary>The files in a folder, each as changed: in the folders under it too when <paramref name="recursive"/>, but
    /// those <paramref name="passedOver"/> names; every one, or those written <paramref name="since"/>.</summary>
    private static List<FileEvent> Every(string top, bool recursive, Func<string, bool> passedOver, DateTime? since)
    {
        List<FileEvent> events = [];
        foreach ((string _, IReadOnlyList<string> files) in PathNames.FilesUnder(top, sub => recursive && !passedOver(sub)))
        {
            events.AddRange(files.Where(file => since is not { } after || Written(file) >= after).Select(file => new FileEvent(file, FileChangeType.Changed)));
        }
        return events;
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

    /// <summary>Starts the watchers the registrations need, has those kept walk again what the registrations over them reach
    /// now, stops those no longer needed once the new ones watch every folder they keep, and says what changed.</summary>
    private void RefreshNow()
    {
        lock (refreshing)
        {
            HashSet<(string Folder, bool Recursive)> needed = Needed();
            IReadOnlyList<FileWatch> watches = watched.Watches;
            List<(string Folder, bool Recursive)> starting;
            List<Watched> renewing = [];
            List<Watched> kept;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                foreach ((string Folder, bool Recursive) made in own.Keys.Where(k => renewed.Any(r => PathNames.Within(k.Folder, r))).ToList())
                {
                    renewing.Add(own[made]);
                    own.Remove(made);
                }
                renewed.Clear();
                starting = [.. needed.Where(k => !own.ContainsKey(k) && !Going(k.Folder))];
                kept = [.. own.Values];
            }
            renewing.ForEach(w => w.Dispose());
            foreach (Watched tree in kept)
            {
                tree.Concern(Concerning(tree.Top, watches));
            }
            List<Watched> started = [];
            foreach ((string at, bool recursive) in starting)
            {
                Watched tree = new(this, at, recursive, Concerning(at, watches));
                bool taken = false;
                lock (gate)
                {
                    if (!disposed && !own.ContainsKey((at, recursive)) && !Going(at) && tree.Watches.Count > 0)
                    {
                        own[(at, recursive)] = tree;
                        taken = true;
                    }
                }
                if (taken)
                {
                    started.Add(tree);
                }
                else
                {
                    tree.Dispose();
                }
            }
            List<Watched> stopped = [];
            lock (gate)
            {
                foreach ((string Folder, bool Recursive) gone in own.Keys.Where(k => !needed.Contains(k)).ToList())
                {
                    stopped.Add(own[gone]);
                    own.Remove(gone);
                }
            }
            if (stopped.Count > 0)
            {
                lock (gate)
                {
                    retiring.UnionWith(stopped);
                }
                // Where each folder is watched on its own, the new watchers' folders are watched as their first walk goes.
                IdeErrors.Observe(BackgroundWork.Run(async () =>
                {
                    await Task.WhenAll(started.Select(tree => tree.Ready));
                    stopped.ForEach(w => w.Dispose());
                    lock (gate)
                    {
                        retiring.ExceptWith(stopped);
                    }
                }));
            }
            if (started.Count > 0 || stopped.Count > 0)
            {
                IdeErrors.Write($"C# language server (process {server.ProcessId}): {Plural.Of(Watching.Count, "folder")} watched for it beside the folder's watcher"
                    + (started.Count > 0 ? $"; from now on {string.Join(", ", started.Select(w => w.Top))}" : string.Empty)
                    + (stopped.Count > 0 ? $"; no longer {string.Join(", ", stopped.Select(w => w.Top))}" : string.Empty));
            }
        }
    }

    /// <summary>The registrations that may match a path in a folder: those of a folder in it, over it, or of no folder.</summary>
    private static IReadOnlyList<FileWatch> Concerning(string at, IReadOnlyList<FileWatch> watches) =>
        [.. watches.Where(w => w.Folder is not { } over || PathNames.Within(over, at) || PathNames.IsUnder(at, over)).DistinctBy(w => (w.Folder, w.Pattern))];

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
                wanted.AddRange(OutputUnder(at).Select(output => (output, true)));
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
                Unwatchable(at, NotThere);
            }
        }
        return needed;
    }

    /// <summary>The build output under a folder (<see cref="BuildOutput"/>), as found before unless it may have moved since.</summary>
    private IReadOnlyList<string> OutputUnder(string at)
    {
        int asOf;
        lock (gate)
        {
            if (outputs.TryGetValue(at, out IReadOnlyList<string>? found))
            {
                return found;
            }
            asOf = outputsDropped;
        }
        IReadOnlyList<string> walked = BuildOutput(at);
        lock (gate)
        {
            if (outputsDropped == asOf)
            {
                outputs[at] = walked;
            }
        }
        return walked;
    }

    /// <summary>The passed-over folders a build writes in a folder the folder's watcher hears, and in each project's folder
    /// it hears under it through no link, however deep: a project's obj, with the sources it generates, its bin, artifacts,
    /// Godot.NET.Sdk's .godot/mono/temp. Neither version control's and other IDEs' (hidden) nor dependencies' (node_modules),
    /// which hold no project's files and would take a watch for each of their many folders on Linux.</summary>
    private static List<string> BuildOutput(string at) =>
        [.. PathNames.FoldersUnder(at, sub => !IgnoredFolders.Is(sub) && (File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
            .SelectMany(folder => BuildOutputIn(folder, registered: PathNames.Same(folder, at)))];

    /// <summary>The build output in one folder the folder's watcher hears (<see cref="BuildOutput"/>): in the folder
    /// <paramref name="registered"/>, else only if it holds a project, so no other program's bin under it is watched.</summary>
    private static List<string> BuildOutputIn(string folder, bool registered)
    {
        try
        {
            List<string> passedOver = [.. Directory.EnumerateDirectories(folder).Where(IgnoredFolders.Is)];
            if (passedOver.Count == 0 || !registered && !ProjectFolders.HoldsProject(folder))
            {
                return [];
            }
            List<string> found = [];
            foreach (string sub in passedOver)
            {
                string name = Path.GetFileName(sub);
                if (name == IgnoredFolders.GodotCache)
                {
                    string godot = Path.Join(sub, "mono", "temp");
                    if (Directory.Exists(godot))
                    {
                        found.Add(godot);
                    }
                }
                else if (name is not ['.', ..] and not "node_modules")
                {
                    found.Add(sub);
                }
            }
            return found;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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

    /// <summary>Tells the server on the thread pool after what was told before from the same watchers, the folder's
    /// (<paramref name="heard"/>) or this one's own, held as background work until it has.</summary>
    private void InTurn(Action tell, bool heard = false) => InTurn(() =>
    {
        tell();
        return Task.CompletedTask;
    }, heard);

    /// <summary>As <see cref="InTurn(Action, bool)"/>, the next told once what <paramref name="tell"/> waits for has ended.</summary>
    private void InTurn(Func<Task> tell, bool heard = false)
    {
        IDisposable held = BackgroundWork.Scope();
        lock (gate)
        {
            Task next = (heard ? tellingHeard : telling).ContinueWith(async _ =>
            {
                try
                {
                    await tell();
                }
                finally
                {
                    held.Dispose();
                }
            }, TaskScheduler.Default).Unwrap();
            if (heard)
            {
                tellingHeard = next;
            }
            else
            {
                telling = next;
            }
            IdeErrors.Observe(next);
        }
    }

    /// <summary>A folder watched for the server as the folder's watcher watches the open folder, kept to the folders the
    /// registrations over it reach and recording only the files they match: a change the server never asked for (a
    /// build's churn) counts toward a burst but is never told; what was written since the server, or a build it was given,
    /// may have read it is told as it starts; and with changes lost there, every file in it is told, those its record held
    /// now gone too.</summary>
    private sealed class Watched : WatchedTree
    {
        private readonly ServerWatches owner;
        private readonly HashSet<string> paths = new(PathNames.Comparer);
        private readonly HashSet<string> came = new(PathNames.Comparer);
        private IReadOnlyList<FileWatch> concerning;

        public Watched(ServerWatches owner, string top, bool recursive, IReadOnlyList<FileWatch> concerning)
            : base(top, FolderWatching.Platform, FolderWatching.Platform.EachFolder || !recursive)
        {
            this.owner = owner;
            this.concerning = concerning;
            Recursive = recursive;
            Begin(walked: () =>
            {
                (int folders, int entries) = RecordSize;
                IdeErrors.Write($"{Shown} records {Plural.Of(folders, "folder")} and {Plural.Of(entries, "entry", "entries")}");
            });
            if (Watches.Count > 0)
            {
                // Told whether or not a registration matches them yet: one the server's load sends later takes those kept for it.
                DateTime since = owner.watched.Since - Rounding;
                owner.InTurn(async () =>
                {
                    await owner.WalkHeld(top);
                    owner.watched.Tell(Every(top, recursive, _ => false, since));
                });
            }
        }

        public bool Recursive { get; }

        protected override string Shown => $"C# language server (process {owner.server.ProcessId}): the watcher of {Top}";

        protected override bool FoundFromStart => true;

        /// <summary>The registrations over the folder now: when they differ, what they reach is walked and recorded again.</summary>
        public void Concern(IReadOnlyList<FileWatch> now)
        {
            IReadOnlyList<FileWatch> was = Volatile.Read(ref concerning);
            if (was.Count == now.Count && was.Select(w => (w.Folder, w.Pattern)).ToHashSet().SetEquals(now.Select(w => (w.Folder, w.Pattern))))
            {
                return;
            }
            Volatile.Write(ref concerning, now);
            Rewalk();
        }

        protected override bool Kept(string path) => PathNames.Same(path, Top) || Recursive && Volatile.Read(ref concerning).Any(w => w.Reaches(path));

        protected override bool Records(string at, string name, bool folder)
        {
            string path = Path.Join(at, name);
            return folder && Kept(path) || Matched(path);
        }

        private bool Matched(string path) => Volatile.Read(ref concerning).Any(w => w.Matches(path));

        /// <summary>A file no registration over the folder can match by its name is never listed: a build's output under it.</summary>
        protected override bool Passes(ReadOnlySpan<char> name, bool folder)
        {
            if (folder)
            {
                return true;
            }
            IReadOnlyList<FileWatch> over = Volatile.Read(ref concerning);
            for (int i = 0; i < over.Count; i++)
            {
                if (over[i].MayMatch(name))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A change any registration matches now, the latest among them too, is taken; every other counts alone,
        /// told without a string made (a build's churn).</summary>
        protected override Notice Noticed(string path, bool created)
        {
            IReadOnlyList<FileWatch> watches = owner.watched.Watches;
            for (int i = 0; i < watches.Count; i++)
            {
                if (watches[i].Matches(path))
                {
                    return new Notice(Takes: true, Counts: true, PathNames.Canonical(path));
                }
            }
            return new Notice(Takes: false, Counts: true, Canonical: null);
        }

        protected override void Take(string path, bool created, Notice notice)
        {
            paths.Add(notice.Canonical!);
            if (created)
            {
                came.Add(notice.Canonical!);
            }
        }

        protected override FolderChanges? Gathered(bool lost, bool overflowed)
        {
            FolderChanges changes = new(new HashSet<string>(paths, PathNames.Comparer), Structure: came.Count > 0, Projects: false, Git: false)
            {
                ComeOrGone = new HashSet<string>(came, PathNames.Comparer),
                Lost = lost,
                Overflowed = overflowed,
            };
            paths.Clear();
            came.Clear();
            return changes.Paths.Count > 0 || lost ? changes : null;
        }

        /// <summary>Told the server in turn, unless the watcher was disposed meanwhile.</summary>
        protected override void Hand(FolderChanges changes)
        {
            owner.watched.Hearing(changes.Paths);
            owner.InTurn(() =>
            {
                if (!IsDisposed)
                {
                    owner.Tell(changes, Top, Recursive, _ => false);
                }
            });
        }

        /// <summary>Said of the folder itself, and of any the system's watches ran out for; a folder under it gone as it was
        /// walked is no failure.</summary>
        protected override void Unwatched(string at, Exception error)
        {
            if (OutOfWatches(error) is { } advice)
            {
                owner.Unwatchable(at, advice);
            }
            else if (at == Top)
            {
                owner.Unwatchable(at, error is ArgumentException ? NotThere : error.Message);
            }
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

    /// <summary>Keeps a batch; one that lost changes, or one past the limit, makes what is kept one batch that lost them,
    /// naming every path kept while they are few enough, so those gone are told gone.</summary>
    public void Add(FolderChanges changes)
    {
        paths += changes.Paths.Count;
        if (!changes.Lost && paths <= Limit && held is not [{ Lost: true }])
        {
            held.Add(changes);
            return;
        }
        HashSet<string> named = new(PathNames.Comparer) { folder };
        if (paths <= Limit)
        {
            named.UnionWith(held.SelectMany(h => h.Paths));
            named.UnionWith(changes.Paths);
        }
        held.Clear();
        held.Add(new FolderChanges(named, Structure: true, Projects: true, Git: true) { Lost = true });
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
