using System.Diagnostics;
using Passenger.App.Debugging;
using Passenger.App.Telemetry;
using Passenger.App.ToolWindows;
using Passenger.App.Web;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Run and Debug of a run of several parts (<see cref="RunPart"/>): each .NET project it starts worked out as its own
/// configuration would launch it (its launch profile's, with the person's changes), any other part as its kind runs (a
/// script or install by its package's manager, a command), the projects built at once, then each part started in order once
/// the one before it is ready (<see cref="PartReadiness"/>). Debug debugs the projects marked Start, each in a Debug tab of
/// its own; every other part runs in a tab of the Run window of its own (<see cref="PartRun"/>), or of the Terminal window
/// where its configuration says. Stop ends them together, a part's tab restarts or ends it alone, and a start of the run
/// again ends its last.</summary>
public sealed partial class BuildRunController
{
    /// <summary>The programs a multi-project configuration's Debug started under the debugger, by their tabs' names: the
    /// configuration and the project's own configuration, which a restart for Hot Reload launches again in its place.</summary>
    private readonly Dictionary<string, (RunConfiguration Profile, RunConfiguration Project, int Place)> profileTabs = new(StringComparer.Ordinal);

    /// <summary>What each run of several parts' last start started, by its name: its programs debugged, by their tabs' names,
    /// and those run, each in its tab of the Run or Terminal window, in the order they started, with what those said of their
    /// start.</summary>
    private readonly Dictionary<string, (IReadOnlyList<string> Debugged, IReadOnlyList<INamedRun> Ran, IReadOnlyList<string> Notes)> profileStarts = new(StringComparer.Ordinal);

    /// <summary>The parts of runs of several parts in tabs of the Run window, in the order they were started, those of a run
    /// started since gone.</summary>
    private readonly List<PartRun> partRuns = [];

    /// <summary>The parts of runs of several parts in tabs of the Run window.</summary>
    public IReadOnlyList<PartRun> PartRuns => partRuns;

    /// <summary>The programs a multi-project configuration runs in tabs of the Terminal window, while each runs: the
    /// configuration, the project's own configuration and its place, which a restart for Hot Reload starts again.</summary>
    private readonly Dictionary<TerminalRun, (RunConfiguration Profile, RunConfiguration Project, int Place)> profileRuns = [];

    /// <summary>Starts a multi-project configuration: its projects run, or with <paramref name="debug"/> those it marks Start
    /// debugged and the rest run; under Hot Reload as Settings says, or as <paramref name="hotReload"/> does, each run one held
    /// at its start and attached to quietly. Null once every one has started, else why the first that could not did not, those
    /// before it running on.</summary>
    private async Task<string?> StartProjectsAsync(RunConfiguration chosen, bool debug, bool? hotReload, CancellationToken cancellation)
    {
        string doing = debug ? "debug" : "run";
        string action = debug ? AppActions.Debug : AppActions.RunProgram;
        if (parts.Work.Begin(action, $"Working out how to {doing} {chosen.Name}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(action)!));
        }
        using (work)
        using (cancellation.Register(work.Cancel))
        using (BackgroundWork.Scope())
        {
            string? refused = await StartProjectsAsync(chosen, debug, hotReload ?? parts.Settings().HotReload, work);
            return refused is null ? null : Said(refused);
        }
    }

    private async Task<string?> StartProjectsAsync(RunConfiguration chosen, bool debug, bool hotReload, Working work)
    {
        if (session.Configurations.WhyNotHere(chosen) is { } why)
        {
            return $"{chosen.Name} cannot {(debug ? "debug" : "run")} here: {why}";
        }
        // A start of it again ends its last, as a configuration's start does.
        if (IsRunningConfiguration(chosen.Name))
        {
            work.Say($"Ending {chosen.Name}'s last start…");
            await EndProjectsAsync(chosen.Name);
            if (session.IsEnded)
            {
                return Refusals.SolutionClosed;
            }
        }
        List<PartStart> starts = [];
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (RunPart part in chosen.Starts.Where(s => s.Action != ProjectLaunchAction.None))
        {
            if (part.Kind != RunPartKind.Project)
            {
                (ProcessStartInfo? start, IReadOnlyList<string> notes, string? problem) = await PartStartAsync(part, work);
                if (Checkpoint(work, chosen.Name) is { } left)
                {
                    return left;
                }
                if (start is null)
                {
                    return $"{chosen.Name} cannot start {session.Configurations.NameOf(part)}: {problem}";
                }
                string its = RunConfiguration.UniqueName(session.Configurations.NameOf(part), named);
                named.Add(its);
                starts.Add(new PartStart(part, its, null, null, start, false, notes));
                continue;
            }
            (RunConfiguration? configuration, string? unmade) = session.Configurations.ProjectOf(part);
            if (configuration is null)
            {
                return $"{chosen.Name} cannot start {Path.GetFileNameWithoutExtension(part.Project)}: {unmade}";
            }
            (LaunchPlan? plan, string? unplanned) = await ResolvePlanAsync(configuration, null, work.Stopping);
            if (Checkpoint(work, chosen.Name) is { } stopped)
            {
                return stopped;
            }
            if (plan is null)
            {
                return $"{chosen.Name} cannot start {configuration.Name}: {unplanned}";
            }
            if (plan.Godot is not null)
            {
                return $"{chosen.Name} cannot start {configuration.Name}: a Godot game starts from its own configuration, which follows its scenes and copies";
            }
            // Its program goes by its own configuration's name (or the part's own), set apart where the run starts that one twice.
            string name = RunConfiguration.UniqueName(part.Name.Length > 0 ? part.Name : configuration.Name, named);
            named.Add(name);
            Dictionary<string, string> environment = new(plan.Environment);
            foreach ((string variable, string value) in part.Environment)
            {
                environment[variable] = value;
            }
            LaunchPlan placed = plan with
            {
                Name = name,
                Copy = new GameCopy(chosen.Name, starts.Count + 1),
                Environment = environment,
                BrowserUrl = part.Browser.Length > 0 ? part.Browser : plan.BrowserUrl,
            };
            starts.Add(new PartStart(part, name, configuration, placed, null, debug && part.Action == ProjectLaunchAction.Start, []));
        }
        // One build of every project, as each alone would build, the dashboards of those that collect coming up meanwhile.
        BuiltEdits? edits = null;
        bool followsEdits = starts.Any(s => s.Debugged) || hotReload;
        using CancellationTokenSource collecting = CancellationTokenSource.CreateLinkedTokenSource(work.Stopping);
        Task<LaunchPlan>?[] told = [.. starts.Select(s => s is { Configuration: { } c, Plan: { } p } && Collects(c, p) ? CollectAsync(p, collecting.Token) : null)];
        // The dashboards come up for each start, its program's once it has started.
        RunDashboard?[] dashboards = new RunDashboard?[starts.Count];
        try
        {
            // Those built as one build configuration are built together, each as its own configuration names.
            foreach (IGrouping<(string? Framework, string? Output, string? As), (BuildStep Step, WorkspaceModel? Workspace)> together in starts
                .Where(s => s.Plan?.Build is not null).Select(s => (Step: s.Plan!.Build!, s.Plan.Workspace))
                .DistinctBy(each => (each.Step.Project, each.Step.Framework, each.Step.Output, each.Workspace?.Build?.Chosen.Key))
                .GroupBy(each => (each.Step.Framework, each.Step.Output, each.Workspace?.Build?.Chosen.Key)))
            {
                BuildOutcome built = await BuildForAsync([.. together.Select(each => each.Step.Project)], work.Stopping, work, together.Key.Framework, together.Key.Output,
                    followsEdits ? () =>
                    {
                        edits?.Dispose();
                        edits = session.Debug.Building(debug ? session.Debug.HotReloadOn() : true);
                    } : null, together.First().Workspace);
                if (!built.Succeeded)
                {
                    return Checkpoint(work, chosen.Name) ?? built.Message;
                }
            }
            for (int i = 0; i < starts.Count; i++)
            {
                if (told[i] is not { } collected)
                {
                    continue;
                }
                if (!collected.IsCompleted)
                {
                    work.Say($"Starting Aspire's dashboard for {starts[i].Name}'s logs and traces…");
                }
                try
                {
                    starts[i] = starts[i] with { Plan = await collected };
                }
                catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested)
                {
                    return Checkpoint(work, chosen.Name) ?? StoppedStarting(chosen.Name);
                }
                told[i] = null;
                dashboards[i] = starts[i].Plan!.Dashboard;
            }
            if (Checkpoint(work, chosen.Name) is { } left)
            {
                return left;
            }
            // Each part not debugged and not run in the terminal has a tab of its own in the Run window, waiting its turn.
            EndPartRuns(ended: true);
            PartRun?[] tabs = [.. starts.Select((s, i) => s.Debugged || s.Configuration?.Console == ConsoleKind.Terminal ? null
                : new PartRun(s.Name, chosen.Name, i + 1, s.Part) { Project = s.Configuration })];
            foreach (PartRun tab in tabs.OfType<PartRun>())
            {
                partRuns.Add(tab);
                Follow(tab);
            }
            if (tabs.Any(t => t is not null))
            {
                parts.Views.RunWindow.ShowParts(partRuns);
                parts.Dock.Show(ToolWindowIds.Run);
            }
            List<string> inTabs = [];
            List<INamedRun> ran = [];
            profileStarts[chosen.Name] = (inTabs, ran, []);
            for (int i = 0; i < starts.Count; i++)
            {
                PartStart start = starts[i];
                PartRun? tab = tabs[i];
                // Each program follows its own copy of the edits since the build: the last takes the first's.
                BuiltEdits? its = edits is null || start.Plan is null ? null : i == LastPlanned(starts) ? Take(ref edits) : session.Debug.Copied(edits);
                work.Say($"Starting {start.Name}, {i + 1} of {Numbers.Said(starts.Count)}…");
                // What the parts after it wait for, heard from its first line.
                using PartReadiness readiness = new(start.Part, start.Name, start.Configuration is not null);
                if (tab is not null)
                {
                    readiness.Hear(tab.Console);
                }
                string? notStarted;
                Task<int>? exit = null;
                Task ended;
                if (start.Debugged)
                {
                    LaunchPlan plan = start.Plan!;
                    profileTabs[plan.Name] = (chosen, start.Configuration!, i + 1);
                    debugged.Remove(plan.Name);
                    inTabs.Add(plan.Name);
                    notStarted = await LaunchDebuggedAsync(start.Configuration!, plan, work, its, copies: chosen.Name);
                    DebugTab? launched = session.Debug.Tabs.FirstOrDefault(t => t.Name == plan.Name);
                    ended = DebugEnded(session.Debug.SessionNamed(plan.Name));
                    if (notStarted is null && launched is not null)
                    {
                        readiness.Hear(launched.Console);
                        if (launched.Plan?.Dashboard == dashboards[i])
                        {
                            // Debug's own Stop of the session takes it down (DebugController.StopAsync).
                            Keep(Take(ref dashboards[i]), ended, () => false, said => launched.Console.Append($"{said}\n"));
                        }
                    }
                }
                else if (tab is null)
                {
                    LaunchPlan plan = start.Plan!;
                    notStarted = await RunProjectAsync(chosen, start.Configuration!, plan, i + 1, Reloaded(start, hotReload), work, its, ran);
                    TerminalRun? run = notStarted is null && ran.Count > 0 && ran[^1] is TerminalRun last && last.Name == plan.Name ? last : null;
                    exit = run?.Ended;
                    ended = exit ?? Task.CompletedTask;
                    if (run is not null)
                    {
                        readiness.Watch(run);
                        Keep(Take(ref dashboards[i]), run.Ended, () => run.WasStopped, said => parts.Say(said));
                    }
                }
                else
                {
                    tab.StartedBy = Mcp.AgentCall.Current?.Connection;
                    notStarted = start.Plan is { } plan
                        ? await RunProjectInTabAsync(tab, plan, Reloaded(start, hotReload), work, its)
                        : await tab.StartAsync(start.Start!, start.Part.Environment);
                    exit = tab.Ended;
                    ended = exit;
                    if (notStarted is null)
                    {
                        ran.Add(tab);
                        RunningChanged?.Invoke(this, EventArgs.Empty);
                        if (start.Plan is null && start.Part.Browser.Length > 0)
                        {
                            parts.OpenBrowser(new LaunchPlan { Name = start.Name, Program = start.Start!.FileName, WorkingDirectory = start.Start.WorkingDirectory, BrowserUrl = start.Part.Browser, BrowserService = start.Name }, tab.Ended);
                        }
                        Keep(Take(ref dashboards[i]), tab.Ended, () => tab.WasStopped, said => parts.Say(said));
                    }
                }
                if (notStarted is not null)
                {
                    return Unstarted(i, notStarted);
                }
                // The last part's readiness holds nothing back.
                if (i < starts.Count - 1 && start.Part.Ready != ReadyWhen.Started)
                {
                    work.Say(readiness.Waiting);
                    tab?.Set(PartState.Starting);
                    if (await readiness.ReadyAsync(exit, ended, work.Stopping) is { } unready)
                    {
                        return Checkpoint(work, chosen.Name) ?? Unstarted(i + 1, unready);
                    }
                }
                tab?.Set(tab.IsRunning ? PartState.Running : PartState.Ended);
            }
            RunningChanged?.Invoke(this, EventArgs.Empty);
            // What a program run in the terminal says of its start goes with the status, its tab being the program's own.
            List<string> notes = [.. starts.Where(s => !s.Debugged).SelectMany(s => s.Plan?.Notes ?? s.Notes)];
            profileStarts[chosen.Name] = (inTabs, ran, notes);
            parts.Say(string.Join("; ", [ProjectsStarted(chosen.Name, inTabs, [.. ran.Select(r => r.Name)], [.. ran.Select(r => r is TerminalRun)]), .. notes]),
                ran.Count > 0 && inTabs.Count == 0 ? StatusPlace.Window(parts.Dock, ran.All(r => r is TerminalRun) ? ToolWindowIds.Terminal : ToolWindowIds.Run) : null);
            return null;

            // Why the run stopped short at a part, those before it running on.
            string Unstarted(int at, string why)
            {
                List<string> started = [.. starts.Take(at).Select(s => s.Name)];
                foreach (PartRun waiting in tabs.Skip(at).OfType<PartRun>())
                {
                    waiting.Set(PartState.Ended);
                }
                return started.Count == 0 || at >= starts.Count ? why : $"{starts[at].Name} did not start ({why}); {Plural.Listed(started)} {Plural.Agreeing(started.Count, "runs", "run")} on";
            }
        }
        finally
        {
            edits?.Dispose();
            // What came up for a start that went no further, or for a program that did not start, is taken down.
            if (told.Any(t => t is not null))
            {
                await collecting.CancelAsync();
                foreach (Task<LaunchPlan>? left in told)
                {
                    await AbandonAsync(left);
                }
            }
            foreach (RunDashboard? unused in dashboards)
            {
                unused?.Dispose();
            }
        }

        bool Reloaded(PartStart start, bool on) => on && start.Configuration!.NamesNoOtherProgram(session.Projects) && start.Plan is { Functions: false };
    }

    /// <summary>The place of the last part a build's edits follow, a .NET project's, which takes the edits themselves.</summary>
    private static int LastPlanned(IReadOnlyList<PartStart> starts) => starts.Select((s, i) => (s, i)).LastOrDefault(each => each.s.Plan is not null).i;

    /// <summary>One part of a run of several parts as its start was worked out: its name, a .NET project's configuration and
    /// plan, or any other's start, whether it is debugged, and what its start says.</summary>
    private sealed record PartStart(RunPart Part, string Name, RunConfiguration? Configuration, LaunchPlan? Plan, ProcessStartInfo? Start, bool Debugged,
        IReadOnlyList<string> Notes);

    /// <summary>How a part that is no .NET project starts: a script by its package's own manager, its package's install by it,
    /// a command as a terminal finds its program, in its folder (the open folder's where it names none); with what is said of
    /// its start, or why it cannot start.</summary>
    private async Task<(ProcessStartInfo? Start, IReadOnlyList<string> Notes, string? Problem)> PartStartAsync(RunPart part, Working work)
    {
        string top = session.Folder ?? Path.GetDirectoryName(part.Project) ?? string.Empty;
        if (part.Kind == RunPartKind.Command)
        {
            IReadOnlyList<string> words = CommandLine.Split(part.Command);
            string folder = part.Folder.Length > 0 ? part.Folder : top;
            if (words.Count == 0 || words[0].Length == 0)
            {
                return (null, [], "it names no command to run");
            }
            if (!Directory.Exists(folder))
            {
                return (null, [], $"its folder {PathNames.Shown(folder, session.Folder)} is gone");
            }
            string named = words[0];
            // A program named by a path is the folder's; one named alone is found as a terminal finds it.
            string? program = named.IndexOfAny(['/', '\\']) >= 0 ? Path.GetFullPath(named, folder) : Programs.Command(named);
            if (program is null || !File.Exists(program))
            {
                return (null, [], $"{named} was not found{(named.IndexOfAny(['/', '\\']) >= 0 ? string.Empty : " on the PATH")}");
            }
            return (ChildProcesses.Command(program, words.Skip(1), folder), [], null);
        }
        work.Say($"Reading {session.Configurations.NameOf(part)}'s package.json…");
        string? configured = parts.Settings().NodeInterpreter;
        string? root = session.NodeFiles.Packages.FirstOrDefault(p => PathNames.Same(p.Manifest, part.Project))?.Root;
        (NodePackage? package, NodeRuntime node, IReadOnlyList<string> needs) = await BackgroundWork.Run(async () =>
        {
            NodePackage? read = NodeProjects.Read(top, [part.Project]).FirstOrDefault();
            NodeRuntime found = await NodeRuntime.FindAsync(configured);
            IReadOnlyList<string> asks = read is null ? [] : [.. ((string?[])[found.TooOldFor(read.Manifest, read.Name),
                root is null ? null : found.TooOldFor(root, $"{Path.GetFileName(Path.GetDirectoryName(root))}'s workspace")]).OfType<string>().Distinct()];
            return (read, found, asks);
        });
        if (package is null)
        {
            return (null, [], $"{PathNames.Shown(part.Project, session.Folder)} is gone, or does not read as a package.json");
        }
        if (part.Kind == RunPartKind.Script && !package.Scripts.Any(s => s.Name == part.Script))
        {
            return (null, [], $"{package.Name}'s package.json has no script named {part.Script}");
        }
        (ProcessStartInfo? start, string? problem) = part.Kind == RunPartKind.Install
            ? ScriptRuns.Install(package, node)
            : ScriptRuns.Start(package, part.Script, CommandLine.Split(part.Arguments), node);
        return (start, needs, problem);
    }

    /// <summary>What a run of several parts says once its programs have started: those debugged, each in a Debug tab, and
    /// those run, each in a tab of the Run window or, where <paramref name="inTerminal"/> says, of the Terminal window.</summary>
    public static string ProjectsStarted(string name, IReadOnlyList<string> debugged, IReadOnlyList<string> ran, IReadOnlyList<bool> inTerminal)
    {
        List<string> windows = [.. ran.Where((_, i) => !inTerminal[i])];
        List<string> terminal = [.. ran.Where((_, i) => inTerminal[i])];
        string without = debugged.Count > 0 ? " without it" : string.Empty;
        return $"{(debugged.Count > 0 ? "Debugging" : "Running")} {name}: "
            + string.Join("; ", ((string?[])[
                debugged.Count == 0 ? null : $"{Plural.Listed(debugged)} under the debugger, {(debugged.Count == 1 ? "in a Debug tab" : "each in a Debug tab of its own")}",
                windows.Count == 0 ? null : $"{Plural.Listed(windows)}{without}, {(windows.Count == 1 ? "in a tab of the Run window" : "each in a tab of the Run window")}",
                terminal.Count == 0 ? null : $"{Plural.Listed(terminal)}{without}, {(terminal.Count == 1 ? "in a tab of the Terminal window" : "each in a tab of the Terminal window")}",
            ]).OfType<string>());
    }

    /// <summary>A part's tab follows its buttons: Rerun restarts its part alone, the others running on, and Stop ends it alone.</summary>
    private void Follow(PartRun tab)
    {
        tab.Console.RerunClicked += (_, _) => IdeErrors.Observe(RestartPartAsync(tab));
        tab.Console.StopClicked += (_, _) => tab.Stop();
        tab.Runner.Exited += (_, _) => RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Run window's part tabs let go of: those of ended parts (<paramref name="ended"/>), or every one, each
    /// running one stopped first.</summary>
    private void EndPartRuns(bool ended)
    {
        foreach (PartRun part in partRuns.Where(p => !ended || !p.IsRunning).ToList())
        {
            part.Stop();
            partRuns.Remove(part);
            part.Dispose();
        }
        parts.Views.RunWindow.ShowParts(partRuns);
    }

    /// <summary>Ends the parts a run of several parts runs in tabs of the Run window, done once each has ended.</summary>
    private Task EndPartRunsAsync(string? run = null)
    {
        List<PartRun> ending = [.. partRuns.Where(p => (run is null || p.Run == run) && p.IsRunning)];
        foreach (PartRun part in ending)
        {
            part.Stop();
        }
        return Task.WhenAll(ending.Select(p => p.Ended));
    }

    /// <summary>A part of a run of several parts restarted alone in its tab, the others running on: ended, then started again as
    /// worked out now (a .NET project built again first, under Hot Reload as Settings says), its console saying why where it
    /// cannot. True once it runs again.</summary>
    public async Task<bool> RestartPartAsync(PartRun part)
    {
        UiThread.Check();
        if (parts.Work.Begin(AppActions.RunProgram, $"Restarting {part.Name}…") is not { } work)
        {
            part.Console.Append($"{ActionWork.Busy(parts.Work.Of(AppActions.RunProgram)!)}\n", error: true);
            return false;
        }
        using (work)
        using (BackgroundWork.Scope())
        {
            if (part.IsRunning)
            {
                part.Stop();
                await part.Ended;
            }
            part.Console.Clear();
            string? refused;
            if (part.Project is { } configuration)
            {
                BuiltEdits? edits = null;
                bool hotReload = parts.Settings().HotReload && configuration.NamesNoOtherProgram(session.Projects);
                (LaunchPlan? plan, string? problem, _) = await PrepareAsync(configuration, null, work, reading: hotReload ? () =>
                {
                    edits?.Dispose();
                    edits = session.Debug.Building(hotReload: true);
                } : null);
                refused = plan is null ? problem : await RunProjectInTabAsync(part, plan with { Name = part.Name, Copy = new GameCopy(part.Run, part.Place) },
                    hotReload && plan is { Functions: false }, work, Take(ref edits));
                edits?.Dispose();
            }
            else
            {
                (ProcessStartInfo? start, _, string? problem) = await PartStartAsync(part.Part, work);
                refused = start is null ? problem : await part.StartAsync(start, part.Part.Environment);
            }
            if (refused is not null)
            {
                part.Console.Append($"{part.Name} could not restart: {refused}\n", error: true);
                part.Set(PartState.Ended);
                return false;
            }
            part.Set(PartState.Running);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            parts.Say($"Restarted {part.Name}, {part.Run}'s other parts running on", StatusPlace.Window(parts.Dock, ToolWindowIds.Run));
            return true;
        }
    }

    /// <summary>Runs a .NET project of a run of several parts in its tab of the Run window, the browser opened where its
    /// configuration says once it listens; with <paramref name="reloaded"/>, held at its runtime's start and attached to quietly
    /// for Hot Reload with <paramref name="edits"/>, which it takes. Null once it runs, else why not.</summary>
    private async Task<string?> RunProjectInTabAsync(PartRun tab, LaunchPlan plan, bool reloaded, Working work, BuiltEdits? edits)
    {
        if (plan.WhyNot("run") is { } why)
        {
            edits?.Dispose();
            return why;
        }
        ProcessStartInfo start = ChildProcesses.Command(plan.Program, plan.Arguments, plan.WorkingDirectory, plan.Environment);
        if (reloaded)
        {
            // Held at its runtime's start until the debugger attaches, so each of its modules takes Edit and Continue as it loads.
            ChildProcesses.WithVariables(start, HeldStart.Variables(changeable: true));
        }
        if (await tab.StartAsync(start, null) is { } refused)
        {
            edits?.Dispose();
            return refused;
        }
        parts.OpenBrowser(plan, tab.Ended);
        if (!reloaded || tab.ProcessId is not { } process)
        {
            edits?.Dispose();
            return null;
        }
        await AttachForHotReloadAsync(plan.Name, process, tab.Console.Sink, work, edits, $"Running {plan.Name} with Hot Reload: a save applies its changes",
            builds: ProgramBuild.Of(plan));
        return null;
    }

    /// <summary>Runs one project of a multi-project configuration in a tab of the Terminal window of its own, as one copy of a
    /// game runs (<see cref="RunCopyAsync"/>), the browser opened where its configuration says once it listens; with
    /// <paramref name="reloaded"/>, held at its runtime's start and attached to quietly for Hot Reload with
    /// <paramref name="edits"/>, which it takes. Null once it runs, else why not.</summary>
    private async Task<string?> RunProjectAsync(RunConfiguration profile, RunConfiguration configuration, LaunchPlan plan, int place, bool reloaded, Working work,
        BuiltEdits? edits, List<INamedRun> started)
    {
        if (plan.WhyNot("run") is { } why)
        {
            edits?.Dispose();
            return why;
        }
        ProcessStartInfo start = ChildProcesses.Command(plan.Program, plan.Arguments, plan.WorkingDirectory, plan.Environment);
        if (reloaded)
        {
            // Held at its runtime's start until the debugger attaches, so each of its modules takes Edit and Continue as it loads.
            ChildProcesses.WithVariables(start, HeldStart.Variables(changeable: true));
        }
        TerminalRun run;
        try
        {
            run = await parts.InTerminal(plan.Name, start);
        }
        catch (LaunchException failed)
        {
            edits?.Dispose();
            return failed.Message;
        }
        run.StartedBy = Mcp.AgentCall.Current?.Connection;
        copyRuns.Add((profile.Name, place, run));
        profileRuns[run] = (profile, configuration, place);
        started.Add(run);
        IdeErrors.Observe(ProjectEnded());
        RunningChanged?.Invoke(this, EventArgs.Empty);
        parts.OpenBrowser(plan, run.Ended);
        if (!reloaded)
        {
            edits?.Dispose();
            return null;
        }
        await AttachForHotReloadAsync(plan.Name, run.Debuggee, null, work, edits, $"Running {plan.Name} with Hot Reload: a save applies its changes",
            builds: ProgramBuild.Of(plan));
        return null;

        async Task ProjectEnded()
        {
            await run.Ended;
            copyRuns.RemoveAll(c => c.Run == run);
            profileRuns.Remove(run);
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Hot Reload's restart of one project a multi-project configuration runs with it, for an edit it cannot apply:
    /// ended, its project built again, and started in its tab's place, held and attached to again, the others running on.
    /// True once it runs again; false for a program no such configuration runs.</summary>
    private async Task<bool> RestartProfileRunAsync(TerminalRun running)
    {
        if (!profileRuns.TryGetValue(running, out (RunConfiguration Profile, RunConfiguration Project, int Place) its)
            || parts.Work.Begin(AppActions.RunProgram, $"Restarting {running.Name} to apply the changes…") is not { } work)
        {
            return false;
        }
        using (work)
        using (BackgroundWork.Scope())
        {
            parts.Say($"Hot Reload: restarting {running.Name} to apply the changes");
            running.Stop();
            await running.Ended;
            BuiltEdits? edits = null;
            (LaunchPlan? plan, string? problem, _) = await PrepareAsync(its.Project, null, work, reading: () =>
            {
                edits?.Dispose();
                edits = session.Debug.Building(hotReload: true);
            }, collects: true);
            if (plan is null)
            {
                edits?.Dispose();
                parts.Say($"Hot Reload could not restart {running.Name}: {problem}");
                return false;
            }
            List<INamedRun> again = [];
            bool restarted = await RunProjectAsync(its.Profile, its.Project, plan with { Name = running.Name, Copy = new GameCopy(its.Profile.Name, its.Place) }, its.Place, reloaded: true,
                work, Take(ref edits), again) is null;
            if (restarted)
            {
                INamedRun run = again[0];
                Keep(plan.Dashboard, run.Ended, () => run.WasStopped, said => parts.Say(said));
            }
            else
            {
                plan.Dashboard?.Dispose();
            }
            if (restarted && profileStarts.TryGetValue(its.Profile.Name, out (IReadOnlyList<string> Debugged, IReadOnlyList<INamedRun> Ran, IReadOnlyList<string> Notes) last))
            {
                profileStarts[its.Profile.Name] = (last.Debugged, [.. last.Ran.Select(r => r == running ? again[0] : r)], last.Notes);
            }
            return restarted;
        }
    }

    /// <summary>What a multi-project configuration's last start started: its programs debugged, by their Debug tabs' names,
    /// those run, each in its tab of the Terminal window (ended since or not), in the order they started, and what those
    /// run said of their start.</summary>
    public (IReadOnlyList<string> Debugged, IReadOnlyList<INamedRun> Ran, IReadOnlyList<string> Notes) ProjectsOf(string name) =>
        profileStarts.TryGetValue(name, out (IReadOnlyList<string> Debugged, IReadOnlyList<INamedRun> Ran, IReadOnlyList<string> Notes) started)
            ? ([.. started.Debugged], [.. started.Ran], [.. started.Notes]) : ([], [], []);

    /// <summary>Ends what a multi-project configuration's last start runs and debugs, done once each has ended.</summary>
    private Task EndProjectsAsync(string name) =>
        Task.WhenAll([EndCopyRunsAsync(name), EndPartRunsAsync(name), .. session.Debug.CopiesOf(name).Select(c => session.Debug.StopAsync(c.Tab))]);

    /// <summary>Hot Reload's restart of one project a multi-project configuration debugs, for an edit it cannot apply: its
    /// session stopped, its project built again, and it launched in its tab's place among the others, which debug on. Null once
    /// its session is open, else why not.</summary>
    private async Task<string?> RestartProfileTabAsync(string name, RunConfiguration profile, RunConfiguration configuration, int place)
    {
        if (parts.Work.Begin(AppActions.Debug, $"Restarting {name}…") is not { } work)
        {
            return ActionWork.Busy(parts.Work.Of(AppActions.Debug)!);
        }
        using (work)
        using (BackgroundWork.Scope())
        {
            await session.Debug.StopAsync(name);
            BuiltEdits? edits = null;
            try
            {
                (LaunchPlan? plan, string? problem, _) = await PrepareAsync(configuration, null, work, reading: () =>
                {
                    edits?.Dispose();
                    edits = session.Debug.Building(session.Debug.HotReloadOn());
                }, collects: true);
                if (plan is null)
                {
                    return problem;
                }
                profileTabs[name] = (profile, configuration, place);
                string? refused = await LaunchDebuggedAsync(configuration, plan with { Name = name, Copy = new GameCopy(profile.Name, place) }, work, Take(ref edits), copies: profile.Name);
                if (refused is null && session.Debug.Tabs.FirstOrDefault(t => t.Name == name && t.Plan?.Dashboard == plan.Dashboard) is { } tab)
                {
                    Keep(plan.Dashboard, DebugEnded(session.Debug.SessionNamed(name)), () => false, said => tab.Console.Append($"{said}\n"));
                }
                else
                {
                    plan.Dashboard?.Dispose();
                }
                return refused;
            }
            finally
            {
                edits?.Dispose();
            }
        }
    }
}
