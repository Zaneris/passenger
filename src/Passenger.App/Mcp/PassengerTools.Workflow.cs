using System.ComponentModel;
using ModelContextProtocol.Server;
using Passenger.App.Packages;
using Passenger.App.Profiling;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Packages;
using Passenger.Terminal;
using Passenger.Text;
using Passenger.Vcs;
using Passenger.Workspace;

namespace Passenger.App.Mcp;

/// <summary>The daily loop's tools: Git as the Commit and Git windows drive it, NuGet as its window does, and the IDE's
/// terminal, each through the window's own code so the agent and the person see one state.</summary>
public sealed partial class PassengerTools
{
    // Version control

    [McpServerTool(Name = McpTools.VcsStatus, Title = "Changed files", ReadOnly = true), Description(
        "The branch and the files changed since the last commit (modified, added, deleted, renamed, unversioned, conflicted). " +
        "While a merge, rebase, unstash or cherry-pick is stopped for conflicts, conflicts says what is under way, which " +
        "branches yours and theirs are (in a rebase yours is the branch being rebased) and whether each side modified or " +
        "deleted each file in conflict; vcs_resolve resolves them. With path, that file's changes since the last commit as a " +
        "unified diff: the editor's text when it is open (unsaved changes included), else the file on disk.")]
    public async Task<object> VcsStatus(string? path = null, CancellationToken cancel = default) =>
        path is null ? await VcsChanges(cancel) : await VcsDiff(path, cancel);

    private Task<VcsStatus> VcsChanges(CancellationToken cancel) => OnUi(cancel, async () =>
    {
        VcsController vcs = Session.Vcs;
        await vcs.Opened;
        await vcs.RefreshAsync();
        if (vcs.Repository is null)
        {
            throw new InvalidOperationException(Refusals.NoRepository);
        }
        ConflictSides sides = vcs.Sides;
        VcsConflicts? conflicts = vcs.Conflicts.Count == 0 ? null : new VcsConflicts(sides.Description, sides.Yours, sides.Theirs,
            [.. vcs.Conflicts.Select(c => new VcsConflict(Relative(c.Path), sides.YoursHas(c) ? "modified" : "deleted", sides.TheirsHas(c) ? "modified" : "deleted"))]);
        return new VcsStatus(vcs.Branch, [.. vcs.Changes.Select(c => new VcsChange(Relative(c.Path), c.Kind.ToString().ToLowerInvariant(), c.From is { } from ? Relative(from) : null))], conflicts);
    });

    [McpServerTool(Name = McpTools.VcsResolve, Title = "Resolve a conflict", Destructive = true), Description(
        "Resolves a file in conflict (vcs_status lists them) as the Conflicts dialog does: take yours or theirs, that side's " +
        "version whole (a side that deleted it deletes it), or give text, the merged file to write; then marks it resolved. " +
        "Once none is left, vcs_commit finishes a merge, cherry-pick or revert, and the Continue Rebase action (ide_action) a " +
        "rebase.")]
    public Task<string> VcsResolve(string path, string? take = null, string? text = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        VcsController vcs = Session.Vcs;
        await vcs.Opened;
        await vcs.RefreshAsync();
        string full = Resolve(path);
        GitConflict conflict = vcs.Conflicts.FirstOrDefault(c => c.Path == full)
            ?? throw new ArgumentException(vcs.Conflicts.Count == 0 ? "No file is in conflict" : $"{Relative(full)} is not in conflict; {string.Join(", ", vcs.Conflicts.Select(c => Relative(c.Path)))} are");
        VcsOutcome done = (take, text) switch
        {
            ("yours" or "theirs", null) => await vcs.AcceptAsync([conflict], yours: take == "yours"),
            (null, { } merged) => await vcs.ResolveAsync(full, merged),
            _ => throw new ArgumentException("Give take (yours or theirs) or text, the merged file, not both"),
        };
        if (done.Problem is { } failed)
        {
            throw new InvalidOperationException(failed);
        }
        int left = vcs.Conflicts.Count;
        return left == 0 ? $"{Relative(full)} resolved; no conflicts are left" : $"{Relative(full)} resolved; {Plural.Of(left, "file")} {(left == 1 ? "is" : "are")} still in conflict";
    });

    private Task<string> VcsDiff(string path, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        string full = Resolve(path);
        string name = Relative(full).Replace('\\', '/');
        await Session.Vcs.Opened;
        string head = await Session.Vcs.HeadTextAsync(full) ?? string.Empty;
        Passenger.App.Editors.OpenTexts texts = Editors.Texts();
        // Reading and comparing a large file would hold the person's window: the thread pool does both.
        string diff = await BackgroundWork.Run(() => LineDiff.Unified(head, texts.Text(full) ?? string.Empty, name), cancel);
        return diff.Length == 0 ? "No changes" : diff;
    });

    [McpServerTool(Name = McpTools.VcsCommit, Title = "Commit", Destructive = true), Description(
        "Commits the paths given, else every changed file but the unversioned (saved first), with message; amend changes the " +
        "last commit, push pushes after. Returns the commit and how the push went; a push the remote refuses is an error with " +
        "git's reason, the commit made all the same.")]
    public Task<string> VcsCommit(string message, IReadOnlyList<string>? paths = null, bool amend = false, bool push = false, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        VcsController vcs = Session.Vcs;
        await vcs.Opened;
        await vcs.RefreshAsync();
        IReadOnlyList<FileChange> chosen = paths is null
            ? [.. vcs.Changes.Where(c => c.Kind is not ChangeKind.Unversioned)]
            : [.. paths.Select(Resolve).Select(p => vcs.Changes.FirstOrDefault(c => c.Path == p) ?? throw new ArgumentException($"{Relative(p)} has no changes to commit"))];
        AgentCall.Say($"Committing {Plural.Of(chosen.Count, "file")}{(push ? " and pushing" : string.Empty)}");
        CommitResult result = await vcs.CommitAsync(chosen, message, amend, push);
        if (!result.Commit.Succeeded)
        {
            throw new InvalidOperationException(result.Commit.Message);
        }
        string made = result.Made is { } head ? $"{head.Short} {head.Subject}" : result.Commit.Message;
        return result.Push switch
        {
            null => made,
            { Succeeded: true } pushed => $"{made}; {pushed.Message}",
            { } refused => throw new InvalidOperationException($"Committed {made}, but the push did not go: {refused.Message}"),
        };
    });

    [McpServerTool(Name = McpTools.VcsLog, Title = "The history", ReadOnly = true), Description(
        "The newest commits of every branch, with their branches and tags. With a path, Show History's: the commits from HEAD " +
        "that changed that file (followed through renames) or folder, each with what it did there; with startLine and endLine " +
        "too, those that changed those lines.")]
    public Task<IReadOnlyList<VcsCommit>> VcsLog(int count = 20, string? path = null, int? startLine = null, int? endLine = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        await Session.Vcs.Opened;
        Git repository = Session.Vcs.Repository ?? throw new InvalidOperationException(Refusals.NoRepository);
        int most = Math.Clamp(count, 1, 500);
        (int, int)? lines = (startLine, endLine) switch
        {
            (null, null) => null,
            (int first, int last) when path is not null && first >= 1 && last >= first => (first - 1, last - 1),
            _ => throw new ArgumentException("startLine and endLine come together, with a path, from 1 and the last at or after the first"),
        };
        if (path is null)
        {
            return (IReadOnlyList<VcsCommit>)[.. (await repository.LogAsync(most)).Select(c => Commit(c, null))];
        }
        HistoryScope scope = await window.HistoryScopeAsync(Resolve(path), lines);
        return [.. (await repository.HistoryAsync(scope, most)).Select(h => Commit(h.Commit, h.Changes))];

        VcsCommit Commit(CommitInfo commit, IReadOnlyList<FileChange>? changes) => new(
            commit.Short,
            commit.Subject,
            commit.Author,
            commit.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            commit.Refs,
            changes?.Select(c => new VcsChange(Relative(c.Path), c.Kind.ToString().ToLowerInvariant(), c.From is { } from ? Relative(from) : null)).ToList());
    });

    [McpServerTool(Name = McpTools.VcsCheckout, Title = "Check out a branch", Destructive = true), Description(
        "Checks out a branch (with create, a new one from the head), as the branch menu does; open files take what is on disk after.")]
    public Task<string> VcsCheckout(string branch, bool create = false, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        VcsController vcs = Session.Vcs;
        await vcs.Opened;
        AgentCall.Say($"Checking out {branch}");
        VcsOutcome switched = create ? await vcs.NewBranchAsync(branch) : await vcs.CheckoutAsync(branch);
        if (switched.Problem is { } failed)
        {
            throw new InvalidOperationException(failed);
        }
        return $"On {vcs.Branch}";
    });

    [McpServerTool(Name = McpTools.VcsStash, Title = "Stash or unstash changes", Destructive = true), Description(
        "Stash Changes and Unstash Changes. action: list (the stashes, newest first, by index), push (every uncommitted " +
        "change, new files too, put away under message, open files saved first), pop or apply (stash index's changes back; pop " +
        "drops it after), drop (throw stash index away).")]
    public Task<IReadOnlyList<StashSummary>> VcsStash(string action = "list", string message = "", int index = 0, CancellationToken cancel = default) => OnUi<IReadOnlyList<StashSummary>>(cancel, async () =>
    {
        VcsController vcs = Session.Vcs;
        await vcs.Opened;
        if (action != "list")
        {
            IReadOnlyList<StashInfo> before = await vcs.StashesAsync();
            StashInfo? stash = before.FirstOrDefault(s => s.Index == index);
            if (action != "push" && stash is null)
            {
                throw new InvalidOperationException($"No stash {index}; there are {before.Count}");
            }
            Editors.SaveAll();
            VcsOutcome done = action switch
            {
                "push" => await vcs.StashAsync(message),
                "pop" or "apply" => await vcs.UnstashAsync(stash!, action == "pop"),
                "drop" => await vcs.DropStashAsync(stash!),
                _ => throw new ArgumentException($"No stash action '{action}'; there are list, push, pop, apply and drop", nameof(action)),
            };
            if (done.Problem is { } failed)
            {
                throw new InvalidOperationException(failed);
            }
        }
        return [.. (await vcs.StashesAsync()).Select(s => new StashSummary(s.Index, s.Message, s.Time))];
    });

    // Packages

    [McpServerTool(Name = McpTools.NugetSearch, Title = "Search NuGet", ReadOnly = true), Description(
        "Packages a query finds on the feeds of the folder's nuget.config files (every enabled one, or feed), each with its " +
        "newest version, downloads and the feeds that have it. A feed that does not answer fails the search only when none " +
        "does.")]
    public Task<IReadOnlyList<PackageSummary>> NuGetSearch(string query, bool prerelease = false,
        [Description("A feed's name, as the NuGet window lists it")] string? feed = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
        await (await Session.Packages.FeedAsync(feed)).SearchAsync(query, prerelease, cancel));

    [McpServerTool(Name = McpTools.NugetInstall, Title = "Install or remove a package", Destructive = true), Description(
        "Adds a package to a project (its name or .csproj) at a version (else the newest on the folder's enabled feeds), or " +
        "moves it to that version, through dotnet add package as the NuGet window does, restoring from the folder's " +
        "nuget.config feeds; with remove, takes it out through dotnet remove package. Answers the version installed and the " +
        "files it changed (the project, and Directory.Packages.props under central package management).")]
    public Task<PackageChange> NuGetInstall(string project, string id, string? version = null, bool remove = false, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        ProjectModel chosen = Project(project);
        if (remove)
        {
            AgentCall.Follow(window.Actions.Work, FolderPackages.Changing);
            return new PackageChange(id, null, chosen.Name, Changed(await Session.Packages.RemoveAsync(chosen, id)));
        }
        AgentCall.Say($"Finding {id} on the folder's feeds");
        IPackageFeed feeds = await Session.Packages.FeedAsync(null);
        string at = version ?? (await feeds.VersionsAsync(id, prerelease: false, cancel)).FirstOrDefault()
            ?? throw new ArgumentException($"{feeds.Name} has no package {id}");
        AgentCall.Say(null);
        AgentCall.Follow(window.Actions.Work, FolderPackages.Changing);
        return new PackageChange(id, at, chosen.Name, Changed(await Session.Packages.InstallAsync(chosen, id, at)));
    });

    /// <summary>The files a package change wrote, named as the answer names them (the window's change took them into the
    /// editors and read the projects again); one refused, or that dotnet failed, is an error saying why.</summary>
    private IReadOnlyList<string> Changed(PackageOutcome outcome) =>
        outcome.Refused is { } why ? throw new InvalidOperationException(why)
        : outcome.Done ? [.. outcome.Changed.Select(Relative)]
        : throw new InvalidOperationException(string.Join('\n', outcome.Said.TakeLast(15)));

    // The profiler

    [McpServerTool(Name = McpTools.Profile, Title = "Profile a configuration", Destructive = true), Description(
        "Profiles a configuration from its start, as Profile does, until it ends or for seconds (then the snapshot; it runs on " +
        "until stop). Returns its length, the hot spots by own time (the folder's with source, path:line), the busiest " +
        "thread's hot path, and for an Avalonia app its frames: the UI thread's median and longest, measure, arrange, render " +
        "and compositor totals, the layout table by control type with the heaviest controls and their XAML lines, and the " +
        "longest frame's breakdown (or 0 and why). With attach, Attach Profiler instead, from now, left running: the debugged " +
        "program, else Run's, or container's .NET program (debug_state of processes lists them). With resource, that one, its " +
        "configuration started if none runs it.")]
    public Task<ProfileInfo> Profile(
        string? configuration = null,
        string? arguments = null,
        int? seconds = null,
        [Description("How many hot spots")] int top = 25,
        [Description("With seconds: a memory snapshot too, the heap's types by size")] bool memory = false,
        bool attach = false,
        string? container = null,
        string? resource = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        ProjectSession current = Session;
        if (resource is not null && container is not null)
        {
            throw new ArgumentException("Name resource or container, not both");
        }
        if (attach && (arguments is not null || configuration is not null && resource is null))
        {
            throw new ArgumentException("An attached profiler profiles the program already running: name no configuration or arguments");
        }
        if (container is not null && !attach)
        {
            throw new ArgumentException("A container's program is profiled as it runs: give attach with container");
        }
        if (!attach && resource is null)
        {
            Choose(configuration);
        }
        AgentCall.Follow(window.Actions.Work, AppActions.Profile, AppActions.Build, AppActions.AttachProfiler, AppActions.RunProgram);
        using (LaunchBuild built = new(Builds))
        {
            Task<string?> started = resource is not null ? ProfileResource()
                : !attach ? window.ProfileAsync(arguments)
                : container is null ? window.AttachProfilerAsync()
                : window.AttachProfilerAsync(await ProfiledIn(container));
            if (await started.WaitAsync(cancel) is { } problem)
            {
                throw new InvalidOperationException(WithBuildErrors(problem, built.Ended));
            }
        }
        if (resource is not null && (Session != current || current.IsEnded))
        {
            throw new InvalidOperationException("The workspace changed while starting the resource profiler");
        }
        AgentCall.Say(seconds is { } lasting ? $"Profiling for {Math.Clamp(lasting, 1, 3600)} s" : "Profiling until the program ends (10 minutes at most)");
        Passenger.Profiler.ProfilingSession running = Session.Profiling!;
        // An Avalonia app's inspector, which records its frames; null for any other program.
        LiveTree.LiveApp? inspected = Session.ProfiledApp;
        IReadOnlyList<HeapTypeInfo>? heap = null;
        if (seconds is { } limit)
        {
            await When(() => running.HasExited, TimeSpan.FromSeconds(Math.Clamp(limit, 1, 3600)), cancel, Ends(running.ExitedAsync()), Ends(running.PutAway));
            CheckProfileTarget();
            if (memory && !running.HasExited && await window.MemorySnapshotAsync(cancel) is { } taken)
            {
                heap = [.. taken.Types.Take(Math.Clamp(top, 1, 200)).Select(t => new HeapTypeInfo(t.Name, t.Count, t.Bytes))];
            }
        }
        else
        {
            await When(() => running.HasExited, TimeSpan.FromMinutes(10), cancel, Ends(running.ExitedAsync()), Ends(running.PutAway));
        }
        AgentCall.Say("Reading the profile");
        CheckProfileTarget();
        SnapshotRead read = await window.SnapshotAsync();
        CheckProfileTarget(snapshotCompleted: true);
        Passenger.Profiler.Snapshot snapshot = read.Snapshot ?? throw new InvalidOperationException(read.Message);
        List<ProfileFrame> path = [];
        if (snapshot.Threads.FirstOrDefault() is { } busiest)
        {
            path.Add(Frame(busiest.Name, busiest.Total, busiest.Self));
            for (Passenger.Profiler.CallNode at = busiest; at.Children.Count > 0 && path.Count < 80;)
            {
                at = at.Ordered[0];
                path.Add(Frame(at.Name, at.Total, at.Self));
            }
        }
        // The folder's own methods by their place, from their PDBs (read in its container for a program there).
        IReadOnlyList<Passenger.Profiler.HotSpot> spots = [.. snapshot.HotSpots.Take(Math.Clamp(top, 1, 200))];
        IReadOnlyDictionary<Passenger.Profiler.MethodCode, (string File, int Line)> sources =
            await Passenger.Profiler.MethodSources.FindAsync(snapshot, spots.Select(h => h.Code).OfType<Passenger.Profiler.MethodCode>(), cancel);
        Func<string, TextSnapshot?> texts = CSharp.ServerPlaces.Texts(Editors);
        return new ProfileInfo(
            Math.Round(snapshot.Duration.TotalMilliseconds),
            snapshot.Samples,
            snapshot.Threads.Count,
            [.. spots.Select(h => Frame(h.Name, h.Total, h.Self) with { Source = Source(h) })],
            path,
            snapshot.TracePath,
            heap)
        {
            Frames = snapshot.Place is { } elsewhere && snapshot.HotSpots.Any(h => h.Module.StartsWith("Avalonia", StringComparison.Ordinal))
                ? new UiFramesInfo(0, 0, 0, 0, 0, []) { Why = Profiling.FramesView.InContainer(elsewhere.Where) }
                : UiFramesOf(snapshot.UiFrames, Math.Clamp(top, 1, 200), inspected),
        };

        ProfileFrame Frame(string method, int total, int self) =>
            new(method, (int)Math.Round(snapshot.Milliseconds(total)), (int)Math.Round(snapshot.Milliseconds(self)));

        string? Source(Passenger.Profiler.HotSpot spot)
        {
            if (spot.Code is not { } code || !sources.TryGetValue(code, out (string File, int Line) at) || Session.Folder is not { } folder)
            {
                return null;
            }
            string file = PathNames.Canonical(at.File);
            // The PDB's line is its compiler's, counted apart from the editor's in a file holding a break only it counts.
            return PathNames.IsUnder(file, folder)
                ? $"{Relative(file)}:{CSharp.ServerPlaces.PositionOfCompiled(file, texts(file), new Lsp.LspPosition(at.Line - 1, 0)).Line + 1}"
                : null;
        }

        // A compose service as it runs, else its configuration started for it profiled; else an Aspire resource.
        async Task<string?> ProfileResource()
        {
            if (await ComposeTargetAsync(resource!, configuration, starts: !attach, cancel) is { } compose)
            {
                return arguments is not null ? throw new ArgumentException("A compose service runs as its compose files say; omit arguments")
                    : compose.Start is null ? await ProfileRunningServiceAsync(compose)
                    : await StartComposeServiceAsync(compose, global::Passenger.App.Run.RunStart.Profile, cancel);
            }
            return await ProfileAspireTarget();
        }

        Task<string?> ProfileRunningServiceAsync(ComposeTarget compose)
        {
            Debuggable(compose, global::Passenger.App.Run.RunStart.Profile);
            return window.ServiceActionAsync(AppActions.ServiceProfile, compose.Service);
        }

        async Task<string?> ProfileAspireTarget()
        {
            (Aspire.AspireSelection selection, bool launched) = await PrepareAspireResource(resource!, configuration,
                attach ? null : global::Passenger.App.Run.RunStart.Profile, arguments, cancel);
            return launched ? null : await window.RunAspireToolResourceAsync(selection, "profile", cancel);
        }

        void CheckProfileTarget(bool snapshotCompleted = false)
        {
            if (resource is not null && (Session != current || current.IsEnded || current.Profiling != (snapshotCompleted ? null : running)))
            {
                throw new InvalidOperationException("The workspace or resource profile changed");
            }
        }
    });

    /// <summary>The .NET program running in a container for the profiler, as Attach to Process lists it: the one there, or
    /// a refusal naming them.</summary>
    private async Task<DotnetProcess> ProfiledIn(string container)
    {
        IReadOnlyList<DotnetProcess> there = [.. (await window.AttachableAsync()).Where(p => p.Container == container)];
        List<DotnetProcess> reachable = [.. there.Where(p => p.WhyNot is null)];
        return reachable switch
        {
            [{ } only] => only,
            [] when there.Count > 0 => throw new InvalidOperationException($"No program in {container} can be profiled: {there[0].WhyNot}"),
            [] => throw new InvalidOperationException($"No .NET program runs in a container named {container}; debug_state of processes lists those there are"),
            _ => throw new InvalidOperationException($"{reachable.Count} .NET programs run in {container} ({string.Join(", ", reachable.Select(p => p.Program))}): "
                + "attach the debugger to the one to profile (debug_attach), then profile with attach alone"),
        };
    }

    /// <summary>An Avalonia app's frames as the profile tool says them, the heaviest controls first; for an app the
    /// inspector was given that recorded none, none and why; null for any other program.</summary>
    private UiFramesInfo? UiFramesOf(IReadOnlyList<Passenger.Profiler.UiFrame> frames, int top, LiveTree.LiveApp? inspected)
    {
        if (frames.Count == 0)
        {
            return inspected is null ? null : new UiFramesInfo(0, 0, 0, 0, 0, [])
            {
                Why = inspected?.ProcessId is null
                    ? "The app's inspector never connected: it had not started its UI when the snapshot was taken"
                    : "The app drew no frame while it was profiled: profile it for longer, or until it has shown its window",
            };
        }
        double[] times = [.. frames.Select(f => f.DurationMs).Order()];
        Func<string, TextSnapshot?> texts = CSharp.ServerPlaces.Texts(Editors);
        IReadOnlyList<Passenger.Profiler.LayoutCost> types = Passenger.Profiler.UiFrames.ByType(frames);
        IEnumerable<Passenger.Profiler.LayoutCost> controls = types.SelectMany(type => type.Controls);
        Passenger.Profiler.UiFrame longest = frames.MaxBy(f => f.DurationMs)!;
        double start = frames.Min(f => f.StartMs);
        return new UiFramesInfo(frames.Count, Math.Round(Passenger.Profiler.UiFrames.Percentile(times, 0.5), 1), Math.Round(times[^1], 1), Math.Round(frames.Sum(f => f.LayoutMs), 1),
            Math.Round(frames.Sum(f => f.RecordMs + f.CompositeMs), 1),
            [.. controls.OrderByDescending(c => c.Work.SelfMs).Take(top).Select(Layout)])
        {
            MeasureMs = Math.Round(frames.Sum(f => f.MeasureMs), 1),
            ArrangeMs = Math.Round(frames.Sum(f => f.ArrangeMs), 1),
            RecordMs = Math.Round(frames.Sum(f => f.RecordMs), 1),
            CompositeMs = Math.Round(frames.Sum(f => f.CompositeMs), 1),
            Types = [.. types.Take(top).Select(t => new TypeLayoutInfo(t.Name, t.Controls.Count, t.Work.Measures, Math.Round(t.Work.MeasureMs, 1), t.Work.Arranges,
                Math.Round(t.Work.ArrangeMs, 1), Math.Round(t.Work.SelfMs, 1), [.. t.Controls.Take(5).Select(Layout)]))],
            Longest = new FrameInfo(longest.Number, Math.Round(longest.StartMs - start, 1), Math.Round(longest.DurationMs, 1), longest.MeasurePasses, longest.ArrangePasses,
                longest.Measures, longest.Arranges, Math.Round(longest.MeasureMs, 1), Math.Round(longest.ArrangeMs, 1), Math.Round(longest.RecordMs, 1),
                Math.Round(longest.CompositeMs, 1)),
        };

        ControlLayoutInfo Layout(Passenger.Profiler.LayoutCost c) => new(c.Name, c.Work.Measures, Math.Round(c.Work.MeasureMs, 1),
            c.Work.Arranges, Math.Round(c.Work.ArrangeMs, 1), Math.Round(c.Work.SelfMs, 1),
            c.Control?.File is { } file ? $"{Relative(file)}:{LiveTree.AppPlaces.LineOf(c.Control, texts)}" : null);
    }

    // The terminal

    [McpServerTool(Name = McpTools.TerminalRun, Title = "Run in the terminal", Destructive = true), Description(
        "Types a command into the IDE's Terminal window (opened if need be) and presses Enter, as the person would, then " +
        "waits until the shell prompts again or the output has been quiet for 2 s, and returns what it printed. While the tab " +
        "runs a terminal configuration's program, the line goes to it.")]
    public Task<string> TerminalRun(string command, int waitSeconds = 15, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        TerminalSession shell = await Terminal(cancel);
        TerminalScreen screen = shell.Screen;
        // By its number, which stays the line's own as the scrollback's oldest drop past its limit.
        long before = screen.ScreenTop + screen.CursorRow;
        string prompt = Prompt(screen);
        int version = screen.Version;
        shell.Write(command + "\r");
        AgentCall.Say($"Waiting up to {Math.Clamp(waitSeconds, 1, 600)} s for the terminal's command to finish");
        DateTime until = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 1, 600));
        DateTime last = DateTime.UtcNow;
        bool answered = false;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100, cancel);
            if (screen.Version != version)
            {
                version = screen.Version;
                last = DateTime.UtcNow;
                answered = true;
                continue;
            }
            // Its prompt back below it ends a command; one that leaves another prompt (a cd) or none (a server) ends once quiet.
            bool prompted = prompt.Length > 0 && screen.ScreenTop + screen.CursorRow > before && Prompt(screen) == prompt;
            if (answered && DateTime.UtcNow - last > (prompted ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(2)))
            {
                break;
            }
        }
        long end = screen.ScreenTop + screen.CursorRow;
        List<string> lines = [];
        for (long line = Math.Max(before, screen.FirstLine); line <= end; line++)
        {
            lines.Add(screen.LineAt(line).Text());
        }
        return string.Join('\n', lines).TrimEnd();
    });

    private async Task<TerminalSession> Terminal(CancellationToken cancel)
    {
        if (window.TerminalPanel.Active is { IsRunning: true } running)
        {
            return running;
        }
        window.ShowToolWindow(ToolWindowIds.Terminal);
        TerminalSession shell = window.TerminalPanel.Active is { IsRunning: true } started ? started : await window.TerminalPanel.NewSessionAsync();
        // Its first prompt before anything is typed: output alone is not it, since ConPTY clears the screen before the shell starts.
        DateTime limit = DateTime.UtcNow.AddSeconds(20);
        DateTime changed = DateTime.UtcNow;
        int version = -1;
        while (DateTime.UtcNow < limit && (Prompt(shell.Screen).Length == 0 || DateTime.UtcNow - changed < TimeSpan.FromMilliseconds(300)))
        {
            if (shell.Screen.Version != version)
            {
                version = shell.Screen.Version;
                changed = DateTime.UtcNow;
            }
            await Task.Delay(50, cancel);
        }
        return shell;
    }

    /// <summary>The text before the cursor on its line, which at a shell waiting for a command is its prompt.</summary>
    private static string Prompt(TerminalScreen screen) =>
        screen.IsAlternate ? string.Empty : screen.Lines[screen.CursorRow].Text(0, screen.CursorColumn);

    private ProjectModel Project(string project)
    {
        IReadOnlyList<ProjectModel> projects = Session.Projects;
        string full = Resolve(project);
        return projects.FirstOrDefault(p => p.Path == full || p.Name.Equals(project, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No project {project}; the folder has {string.Join(", ", projects.Select(p => p.Name))}");
    }
}

public sealed record VcsStatus(string? Branch, IReadOnlyList<VcsChange> Changes, VcsConflicts? Conflicts = null);

/// <summary>What stopped for conflicts, the branches yours and theirs are, and the files in conflict.</summary>
public sealed record VcsConflicts(string Operation, string? Yours, string? Theirs, IReadOnlyList<VcsConflict> Files);

/// <summary>A file in conflict, and whether yours and theirs modified or deleted it.</summary>
public sealed record VcsConflict(string Path, string Yours, string Theirs);

public sealed record VcsChange(string Path, string Kind, string? From);

/// <summary>A commit as vcs_log lists it; with a path, what it did there.</summary>
public sealed record VcsCommit(string Hash, string Subject, string Author, string Date, IReadOnlyList<string> Refs, IReadOnlyList<VcsChange>? Changes = null);

/// <summary>A frame of a profile: a method, the milliseconds with it on the stack, and those in it alone; for a hot spot in
/// the folder's own code, where the method is (path:line).</summary>
public sealed record ProfileFrame(string Method, int TotalMs, int SelfMs, string? Source = null);

public sealed record ProfileInfo(double DurationMs, int Samples, int Threads, IReadOnlyList<ProfileFrame> HotSpots, IReadOnlyList<ProfileFrame> HotPath, string Trace, IReadOnlyList<HeapTypeInfo>? Heap = null)
{
    /// <summary>An Avalonia app's frames, as the Frames page shows them; null for any other program.</summary>
    public UiFramesInfo? Frames { get; init; }
}

/// <summary>An Avalonia app's frames in a profile: how many, their UI thread's time at the median and at the longest, the time
/// measuring and arranging and recording what to render in all, and the controls heaviest in layout by their own time.</summary>
public sealed record UiFramesInfo(int Count, double MedianMs, double LongestMs, double LayoutMs, double RenderMs, IReadOnlyList<ControlLayoutInfo> Heaviest)
{
    /// <summary>Why there are none, when there are none.</summary>
    public string? Why { get; init; }

    /// <summary>The time measuring, arranging, recording what to render and in the compositor, in all, as the Frames page's summary says.</summary>
    public double? MeasureMs { get; init; }

    public double? ArrangeMs { get; init; }

    public double? RecordMs { get; init; }

    public double? CompositeMs { get; init; }

    /// <summary>The Frames page's layout table: each control type by its own time, the heaviest first, with its heaviest controls.</summary>
    public IReadOnlyList<TypeLayoutInfo>? Types { get; init; }

    /// <summary>The longest frame, as the Frames page says it when it is chosen.</summary>
    public FrameInfo? Longest { get; init; }
}

/// <summary>A control type's layout in a profile, as the Frames page's table has it: its controls, their measures and arranges with
/// their time, their own time, and the heaviest of them.</summary>
public sealed record TypeLayoutInfo(string Type, int ControlCount, int Measures, double MeasureMs, int Arranges, double ArrangeMs, double OwnMs,
    IReadOnlyList<ControlLayoutInfo> Controls);

/// <summary>One frame: its number, when it began from the first frame, its UI thread's time, its passes, measures and arranges, and its
/// time measuring, arranging, recording what to render and in the compositor.</summary>
public sealed record FrameInfo(int Number, double AtMs, double DurationMs, int MeasurePasses, int ArrangePasses, int Measures, int Arranges,
    double MeasureMs, double ArrangeMs, double RecordMs, double CompositeMs);

/// <summary>A control's layout in a profile: Type#Name, its measures and arranges with their time, its own time without
/// what it measured or arranged in turn, and its XAML's file:line when the app's build recorded it.</summary>
public sealed record ControlLayoutInfo(string Control, int Measures, double MeasureMs, int Arranges, double ArrangeMs, double OwnMs, string? Xaml);

/// <summary>A type on the heap of a memory snapshot: its live objects and their bytes.</summary>
public sealed record HeapTypeInfo(string Type, int Count, long Bytes);

/// <summary>What nuget_install did: the package, the version installed (none for a removal), the project, and
/// the files it changed.</summary>
public sealed record PackageChange(string Package, string? Version, string Project, IReadOnlyList<string> Changed);

/// <summary>A stash as the vcs_stash tool lists it.</summary>
public sealed record StashSummary(int Index, string Message, DateTimeOffset Time);
