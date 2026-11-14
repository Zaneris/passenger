using System.Diagnostics;
using Passenger.App.Debugging;
using Passenger.App.Telemetry;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Run and Debug of a multi-project configuration (a solution's launch profile, <see cref="SolutionLaunch"/>): each
/// project it starts worked out as its own configuration would launch it (its launch profile's, with the person's changes),
/// all of them built at once, then started in the profile's order, each a program of its own named for that configuration.
/// Debug debugs those marked Start, each in a Debug tab of its own, and runs those marked Start without debugging; Run runs
/// every one; a program run goes to a tab of the Terminal window, its own console, as a game's copies do. Stop ends them
/// together, as it ends a game's copies, and a start of the profile again ends its last.</summary>
public sealed partial class BuildRunController
{
    /// <summary>The programs a multi-project configuration's Debug started under the debugger, by their tabs' names: the
    /// configuration and the project's own configuration, which a restart for Hot Reload launches again in its place.</summary>
    private readonly Dictionary<string, (RunConfiguration Profile, RunConfiguration Project, int Place)> profileTabs = new(StringComparer.Ordinal);

    /// <summary>What each multi-project configuration's last start started, by its name: its programs debugged, by their tabs'
    /// names, and those run, each in its tab of the Terminal window, in the order they started, with what those said of
    /// their start.</summary>
    private readonly Dictionary<string, (IReadOnlyList<string> Debugged, IReadOnlyList<TerminalRun> Ran, IReadOnlyList<string> Notes)> profileStarts = new(StringComparer.Ordinal);

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
        List<(RunConfiguration Configuration, LaunchPlan Plan, bool Debugged)> starts = [];
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (ProjectStart start in chosen.Starts.Where(s => s.Action != ProjectLaunchAction.None))
        {
            (RunConfiguration? configuration, string? unmade) = session.Configurations.ProjectOf(start);
            if (configuration is null)
            {
                return $"{chosen.Name} cannot start {Path.GetFileNameWithoutExtension(start.Project)}: {unmade}";
            }
            (LaunchPlan? plan, string? problem) = await ResolvePlanAsync(configuration, null, work.Stopping);
            if (Checkpoint(work, chosen.Name) is { } stopped)
            {
                return stopped;
            }
            if (plan is null)
            {
                return $"{chosen.Name} cannot start {configuration.Name}: {problem}";
            }
            if (plan.Godot is not null)
            {
                return $"{chosen.Name} cannot start {configuration.Name}: a Godot game starts from its own configuration, which follows its scenes and copies";
            }
            // Its program goes by its own configuration's name, set apart where the profile starts that one twice.
            string name = RunConfiguration.UniqueName(configuration.Name, named);
            named.Add(name);
            starts.Add((configuration, plan with { Name = name, Copy = new GameCopy(chosen.Name, starts.Count + 1) }, debug && start.Action == ProjectLaunchAction.Start));
        }
        // One build of every project, as each alone would build, the dashboards of those that collect coming up meanwhile.
        BuiltEdits? edits = null;
        bool followsEdits = starts.Any(s => s.Debugged) || hotReload;
        using CancellationTokenSource collecting = CancellationTokenSource.CreateLinkedTokenSource(work.Stopping);
        Task<LaunchPlan>?[] told = [.. starts.Select(s => Collects(s.Configuration, s.Plan) ? CollectAsync(s.Plan, collecting.Token) : null)];
        // The dashboards come up for each start, its program's once it has started.
        RunDashboard?[] dashboards = new RunDashboard?[starts.Count];
        try
        {
            // Those built as one build configuration are built together, each as its own configuration names.
            foreach (IGrouping<(string? Framework, string? Output, string? As), (BuildStep Step, WorkspaceModel? Workspace)> together in starts
                .Where(s => s.Plan.Build is not null).Select(s => (Step: s.Plan.Build!, s.Plan.Workspace))
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
                    work.Say($"Starting Aspire's dashboard for {starts[i].Plan.Name}'s logs and traces…");
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
                dashboards[i] = starts[i].Plan.Dashboard;
            }
            if (Checkpoint(work, chosen.Name) is { } left)
            {
                return left;
            }
            List<string> inTabs = [];
            List<TerminalRun> ran = [];
            profileStarts[chosen.Name] = (inTabs, ran, []);
            for (int i = 0; i < starts.Count; i++)
            {
                (RunConfiguration configuration, LaunchPlan plan, bool underDebugger) = starts[i];
                // Each program follows its own copy of the edits since the build: the last takes the first's.
                BuiltEdits? its = edits is null ? null : i == starts.Count - 1 ? Take(ref edits) : session.Debug.Copied(edits);
                work.Say($"Starting {plan.Name}, {i + 1} of {starts.Count}…");
                string? notStarted;
                if (underDebugger)
                {
                    profileTabs[plan.Name] = (chosen, configuration, i + 1);
                    debugged.Remove(plan.Name);
                    inTabs.Add(plan.Name);
                    notStarted = await LaunchDebuggedAsync(configuration, plan, work, its, copies: chosen.Name);
                    if (notStarted is null && session.Debug.Tabs.FirstOrDefault(t => t.Name == plan.Name && t.Plan?.Dashboard == dashboards[i]) is { } tab)
                    {
                        // Debug's own Stop of the session takes it down (DebugController.StopAsync).
                        Keep(Take(ref dashboards[i]), DebugEnded(session.Debug.SessionNamed(plan.Name)), () => false, said => tab.Console.Append($"{said}\n"));
                    }
                }
                else
                {
                    notStarted = await RunProjectAsync(chosen, configuration, plan, i + 1, hotReload && configuration.NamesNoOtherProgram(session.Projects) && !plan.Functions, work, its, ran);
                    if (notStarted is null && ran.Count > 0 && ran[^1].Name == plan.Name)
                    {
                        TerminalRun run = ran[^1];
                        Keep(Take(ref dashboards[i]), run.Ended, () => run.WasStopped, said => parts.Say(said));
                    }
                }
                if (notStarted is not null)
                {
                    List<string> started = [.. starts.Take(i).Select(s => s.Plan.Name)];
                    return started.Count == 0 ? notStarted : $"{plan.Name} did not start ({notStarted}); {Plural.Listed(started)} {Plural.Agreeing(started.Count, "runs", "run")} on";
                }
            }
            RunningChanged?.Invoke(this, EventArgs.Empty);
            // What a program run in the terminal says of its start goes with the status, its tab being the program's own.
            List<string> notes = [.. starts.Where(s => !s.Debugged).SelectMany(s => s.Plan.Notes)];
            profileStarts[chosen.Name] = (inTabs, ran, notes);
            parts.Say(string.Join("; ", [ProjectsStarted(chosen.Name, inTabs, [.. ran.Select(r => r.Name)]), .. notes]),
                ran.Count > 0 && inTabs.Count == 0 ? StatusPlace.Window(parts.Dock, ToolWindowIds.Terminal) : null);
            return null;
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
    }

    /// <summary>What a multi-project start says once its programs have started: those debugged, each in a Debug tab, and those
    /// run, each in a tab of the Terminal window.</summary>
    public static string ProjectsStarted(string name, IReadOnlyList<string> debugged, IReadOnlyList<string> ran) =>
        $"{(debugged.Count > 0 ? "Debugging" : "Running")} {name}: "
        + string.Join("; ", ((string?[])[
            debugged.Count == 0 ? null : $"{Plural.Listed(debugged)} under the debugger, {(debugged.Count == 1 ? "in a Debug tab" : "each in a Debug tab of its own")}",
            ran.Count == 0 ? null : $"{Plural.Listed(ran)}{(debugged.Count > 0 ? " without it" : string.Empty)}, {(ran.Count == 1 ? "in a tab of the Terminal window" : "each in a tab of the Terminal window")}",
        ]).OfType<string>());

    /// <summary>Runs one project of a multi-project configuration in a tab of the Terminal window of its own, as one copy of a
    /// game runs (<see cref="RunCopyAsync"/>), the browser opened where its configuration says once it listens; with
    /// <paramref name="reloaded"/>, held at its runtime's start and attached to quietly for Hot Reload with
    /// <paramref name="edits"/>, which it takes. Null once it runs, else why not.</summary>
    private async Task<string?> RunProjectAsync(RunConfiguration profile, RunConfiguration configuration, LaunchPlan plan, int place, bool reloaded, Working work,
        BuiltEdits? edits, List<TerminalRun> started)
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
        await AttachForHotReloadAsync(plan.Name, run.Debuggee, null, work, edits, $"Running {plan.Name} with Hot Reload: a save applies its changes");
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
            List<TerminalRun> again = [];
            bool restarted = await RunProjectAsync(its.Profile, its.Project, plan with { Name = running.Name, Copy = new GameCopy(its.Profile.Name, its.Place) }, its.Place, reloaded: true,
                work, Take(ref edits), again) is null;
            if (restarted)
            {
                TerminalRun run = again[0];
                Keep(plan.Dashboard, run.Ended, () => run.WasStopped, said => parts.Say(said));
            }
            else
            {
                plan.Dashboard?.Dispose();
            }
            if (restarted && profileStarts.TryGetValue(its.Profile.Name, out (IReadOnlyList<string> Debugged, IReadOnlyList<TerminalRun> Ran, IReadOnlyList<string> Notes) last))
            {
                profileStarts[its.Profile.Name] = (last.Debugged, [.. last.Ran.Select(r => r == running ? again[0] : r)], last.Notes);
            }
            return restarted;
        }
    }

    /// <summary>What a multi-project configuration's last start started: its programs debugged, by their Debug tabs' names,
    /// those run, each in its tab of the Terminal window (ended since or not), in the order they started, and what those
    /// run said of their start.</summary>
    public (IReadOnlyList<string> Debugged, IReadOnlyList<TerminalRun> Ran, IReadOnlyList<string> Notes) ProjectsOf(string name) =>
        profileStarts.TryGetValue(name, out (IReadOnlyList<string> Debugged, IReadOnlyList<TerminalRun> Ran, IReadOnlyList<string> Notes) started)
            ? ([.. started.Debugged], [.. started.Ran], [.. started.Notes]) : ([], [], []);

    /// <summary>Ends what a multi-project configuration's last start runs and debugs, done once each has ended.</summary>
    private Task EndProjectsAsync(string name) =>
        Task.WhenAll([EndCopyRunsAsync(name), .. session.Debug.CopiesOf(name).Select(c => session.Debug.StopAsync(c.Tab))]);

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
