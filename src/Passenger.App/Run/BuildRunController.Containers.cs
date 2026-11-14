using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Run of a container configuration: Compose's model of its files read, Passenger's override written, its .NET
/// services built on the host (fast mode), its services brought up and waited for, their merged log followed in the Run
/// console, the browser opened once the launched service answers HTTP at its published port, and the Containers window
/// shown its project; Stop takes it down, and Rerun builds again and restarts only the services whose build changed. Debug
/// brings it up the same way with the services it debugs held at their start, and attaches a session to each in its
/// container (the adapter there over docker exec), its tab's console its container's log; Run with Hot Reload attaches to
/// each quietly.</summary>
public sealed partial class BuildRunController
{
    /// <summary>The container run under way, if any (its services up, its log followed).</summary>
    public ContainerRun? Containers => session.ContainerRun is { IsStopped: false } run ? run : null;

    /// <summary>The next container run's <c>up</c> held by a test (<see cref="HoldNextUp"/>): what it waits for, and done once it waits.</summary>
    private (Task Until, TaskCompletionSource Holding)? upHeld;

    /// <summary>Holds the next container run's <c>up</c> until <paramref name="until"/> ends, its override written and its
    /// ports chosen (<see cref="ContainerPlan.Ports"/>), or the next restart of a run's services (Rerun's, Hot Reload's), its
    /// build done and their sessions ended; as deployed, once the files its images are built from are saved. For a test of
    /// what goes on meanwhile. Done once it is held.</summary>
    public Task HoldNextUp(Task until)
    {
        TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        upHeld = (until, holding);
        return holding.Task;
    }

    /// <summary>Runs (or debugs) a container configuration not chosen in the toolbar (a gutter icon's: one compose service, a
    /// Dockerfile's image), as Run (or Debug) runs the chosen one; a container run under way is taken down first. Null once it
    /// runs.</summary>
    public async Task<string?> RunContainersAsync(RunConfiguration configuration, RunStart how = RunStart.Run)
    {
        UiThread.Check();
        if (!configuration.IsContainer)
        {
            return Said($"{configuration.Name} runs no containers");
        }
        // The Run console is one program's: another configuration's plain program there is named; a container run gives way.
        if (RunsInConsole && session.ContainerRun is not { IsStopped: false })
        {
            return Said(AlreadyRunning());
        }
        string action = how == RunStart.Debug ? AppActions.Debug : AppActions.RunProgram;
        if (parts.Work.Begin(action, $"Working out how to {(how == RunStart.Debug ? "debug" : "run")} {configuration.Name}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(action)!));
        }
        using (work)
        {
            return await RunContainersAsync(configuration, work, how);
        }
    }

    /// <summary>Profile of a container configuration: brought up as Run brings it up, the one .NET service it profiles
    /// (<see cref="ContainerPlan.Profiled"/>) held at its start until the window's profiler records it in its container
    /// (<see cref="BuildRunParts.ProfileService"/>), which lets it go; the rest run as Run runs them. Null once it records,
    /// else why not.</summary>
    public Task<string?> ProfileContainersAsync(RunConfiguration chosen, Working work) => RunContainersAsync(chosen, work, RunStart.Profile);

    /// <summary>One container start at a time, as one container run is up at a time (<see cref="TakeStartAsync"/>): one under
    /// way (Run's, as Debug begins) is ended first, as Stop ends it, and this one goes on once it has.</summary>
    private async Task<string?> RunContainersAsync(RunConfiguration chosen, Working work, RunStart how = RunStart.Run)
    {
        using IDisposable held = BackgroundWork.Scope();
        using StartTaken? taken = await TakeStartAsync([ContainerStarts], work);
        if (taken is null)
        {
            return Said(Canceled(work, chosen.Name));
        }
        if (session.IsEnded)
        {
            return Refusals.SolutionClosed;
        }
        try
        {
            return await StartContainersAsync(chosen, work, how);
        }
        finally
        {
            LetGoBuilt();
        }
    }

    /// <summary>The edits followed since the build a service's program is started or restarted on saved the files (or, for a
    /// restart on the build it ran, since that build), for each service a session of the debugger or Hot Reload attaches to,
    /// by its name: that service's attach takes them, and what none took is let go with the start or restart.</summary>
    private readonly Dictionary<string, BuiltEdits> builtEdits = new(StringComparer.Ordinal);

    /// <summary>Begins anew, as a build saves the files, the edits followed for each service given, with Hot Reload or not.</summary>
    private void FollowBuilt(IEnumerable<(string Service, bool HotReload)> sessions)
    {
        foreach ((string service, bool hotReload) in sessions)
        {
            LetGoBuilt([service]);
            builtEdits[service] = session.Debug.Building(hotReload);
        }
    }

    /// <summary>Lets go of the edits followed for the services given (every one, given none) that no session took.</summary>
    private void LetGoBuilt(IEnumerable<string>? services = null)
    {
        foreach (string service in services?.ToList() ?? [.. builtEdits.Keys])
        {
            if (builtEdits.Remove(service, out BuiltEdits? edits))
            {
                edits.Dispose();
            }
        }
    }

    /// <summary>What every container start starts, so one ends another under way: the folder's one container run.</summary>
    private const string ContainerStarts = "the folder's container run";

    /// <summary>The container run Rerun runs again whole (in images mode, or its log ended), until its start has ended: what
    /// it started later is started again.</summary>
    private ContainerRun? rerun;

    private async Task<string?> StartContainersAsync(RunConfiguration chosen, Working work, RunStart how)
    {
        if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
        {
            return Said(unsaved);
        }
        parts.Views.Run.Clear();
        // The configuration's own fault first: a Dockerfile one with none would send the person to start an engine for nothing.
        if (chosen.Kind == RunKind.Dockerfile && chosen.Dockerfile.Length == 0
            && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project)) is { } project
            && ContainerConfigurations.Dockerfile(project) is null)
        {
            return Said($"{chosen.Name} cannot run: {project.Name} has no Dockerfile");
        }
        ContainerEngine engine = session.Containers.Engine;
        work.Say("Asking the container engine…");
        EngineState state;
        try
        {
            state = await engine.StateAsync(cancel: work.Stopping);
        }
        catch (OperationCanceledException)
        {
            return Said(Canceled(work, chosen.Name));
        }
        if (!state.IsReady)
        {
            return Said($"{chosen.Name} cannot run: {state.Problem}");
        }
        // A Rerun, or a start of this configuration that takes its run's place, starts again what that run started later.
        IReadOnlyCollection<string> later = session.ContainerRun is { } previous && previous.Name == chosen.Name && (!previous.IsStopped || previous == rerun)
            ? previous.StartedLater : [];
        if (session.ContainerRun is { } before)
        {
            // Another run (or this one's, its backing services kept) is wholly down before this one comes: one log at a time.
            if (!before.IsStopped)
            {
                IdeErrors.Observe(session.StopContainersAsync());
            }
            try
            {
                await before.Stopped.WaitAsync(work.Stopping);
            }
            catch (OperationCanceledException)
            {
                return Said(Canceled(work, chosen.Name));
            }
        }
        work.Say($"Reading {chosen.Name}'s compose files…");
        (WorkspaceModel? configured, string? unconfigured) = await session.WorkspaceOfAsync(chosen);
        if (unconfigured is not null)
        {
            return Said(unconfigured);
        }
        ContainerPlan plan;
        try
        {
            plan = await ContainerPlans.ResolveAsync(chosen, session.Projects, configured, parts.StateFolder(), engine, session.Containers.Labels, work.Stopping)
                with { Workspace = configured };
            later = [.. later.Where(s => plan.Dotnet.Any(d => d.Service == s && d.Action == ServiceStart.DontStart && d.Unfit is null))];
            // A run let go of whose Compose project this one brings up again is this one's now.
            session.RunsLetGo.RemoveAll(r => r.Plan.ProjectName == plan.ProjectName);
            plan = later.Count > 0 ? plan.StartingLater(later) : plan;
            parts.Views.Problems.ShowContainers([]);
        }
        catch (ComposeModelException unread)
        {
            parts.Views.Problems.ShowContainers([new Problem(ProblemSeverity.Error, unread.Said, null, null, new Text.TextPosition(0, 0), chosen.Name)]);
            parts.Say(unread.Message, StatusPlace.Problems(parts.Dock, parts.Views.Problems, ProblemsTab.Containers));
            return unread.Message;
        }
        catch (OperationCanceledException) when (work.IsStopped)
        {
            return Said(Canceled(work, chosen.Name));
        }
        catch (Exception refused) when (refused is LaunchException or ContainerCommandException or IOException or UnauthorizedAccessException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            return Said(refused is LaunchException ? refused.Message : $"{chosen.Name} cannot run: {refused.Message}");
        }
        try
        {
            if (await ReplacesOthersAsync(chosen.Name, plan, engine, work) is { } kept)
            {
                return Said(session.IsEnded ? Refusals.SolutionClosed : kept);
            }
        }
        catch (OperationCanceledException)
        {
            return Said(Canceled(work, chosen.Name));
        }
        List<(DotnetService Service, RunStart How)> holding = how switch
        {
            RunStart.Run => [],
            RunStart.Profile => plan.Profiled() is { } profiled ? [(profiled, how)] : [],
            _ => [.. plan.Debugged(marked: how == RunStart.Debug).Select(d => (d, how))],
        };
        // What a person chose for a service of this configuration's last run (Debug, or Run without debugger) holds here too.
        if (how != RunStart.Profile && chosenDebugged.TryGetValue(chosen.Name, out Dictionary<string, bool>? choices))
        {
            holding.RemoveAll(h => choices.ContainsKey(h.Service.Service));
            holding.AddRange(plan.Dotnet.Where(d => plan.Starts(d.Service) && choices.GetValueOrDefault(d.Service)).Select(d => (d, RunStart.Debug)));
        }
        IReadOnlyList<DotnetService> debugged = [.. holding.Select(h => h.Service)];
        if (how is RunStart.Debug or RunStart.Profile && debugged.Count == 0)
        {
            return Said($"Nothing to {(how == RunStart.Debug ? "debug" : "profile")}: {chosen.Name} starts no .NET service");
        }
        // Those a session of the debugger or Hot Reload attaches to, which follow the edits from their build's save.
        List<(string Service, bool HotReload)> sessions = [.. holding.Where(h => h.How != RunStart.Profile)
            .Select(h => (h.Service.Service, h.How == RunStart.HotReload || session.Debug.HotReloadOn()))];
        if (plan.Builds.Count > 0)
        {
            work.Say($"Building {string.Join(", ", plan.Builds.Select(Path.GetFileNameWithoutExtension))}…");
            BuildOutcome built = await BuildForAsync(plan.Builds, work.Stopping, work, reading: () => FollowBuilt(sessions), configured: plan.Workspace);
            if (!built.Succeeded)
            {
                return Checkpoint(work, chosen.Name) is { } stopped ? Said(stopped) : built.Message;
            }
            if (plan.Dotnet.FirstOrDefault(d => d.Starts && !File.Exists(Path.Combine(d.Output, d.Assembly + ".dll"))) is { } unbuilt)
            {
                return Said($"Could not run {chosen.Name}: {unbuilt.Assembly}.dll has not been built in {unbuilt.Output}");
            }
        }
        if (Checkpoint(work, chosen.Name) is { } readied)
        {
            return Said(readied);
        }
        HttpsChoice https;
        try
        {
            https = await CertificateAsync(plan.Name, [.. plan.Https.Where(plan.Starts)], plan.Folder, plan.State, export: true, work);
        }
        catch (OperationCanceledException)
        {
            // Stopped as dev-certs checked, made or exported it, or as the person was asked.
            return Said(Canceled(work, chosen.Name));
        }
        foreach (string line in https.Said)
        {
            parts.Views.Run.Append($"{line}\n", error: true);
        }
        (DevelopmentCertificate.Exported? certificate, string? overHttp, bool unexported) = (https.Certificate, https.OverHttp, https.Failed);
        if (Checkpoint(work, chosen.Name) is { } certified)
        {
            return Said(certified);
        }
        if (debugged.Count > 0)
        {
            // Each held service's image is built to read whether its own environment turns its debugger off.
            work.Say($"Building {Plural.Listed([.. debugged.Select(d => d.Service)])}'s {Plural.Noun(debugged.Count, "image")}…");
        }
        try
        {
            await plan.WriteOverride([.. debugged.Select(d => d.Service)], [.. holding.Where(h => h.How != RunStart.Profile).Select(h => h.Service.Service)], certificate,
                work.Stopping);
        }
        catch (OperationCanceledException)
        {
            return Said(Canceled(work, chosen.Name));
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            return Said($"{chosen.Name} cannot run: Passenger's override could not be written: {unwritten.Message}");
        }
        if (Checkpoint(work, chosen.Name) is { } written)
        {
            return Said(written);
        }
        work.Say($"Bringing {chosen.Name}'s services up…");
        ran = chosen.Name;
        RunProject = plan.Folder;
        DebugProject = plan.Folder;
        session.RunPlan = null;
        session.InTerminal = null;
        parts.Views.Run.Links.Forget();
        parts.Views.Run.SetRunning(true);
        parts.Dock.Show(how == RunStart.Debug ? ToolWindowIds.Debug : ToolWindowIds.Run);
        if (how == RunStart.Debug)
        {
            parts.Views.Debug.ShowTab(console: true);
        }
        parts.Say($"Bringing {chosen.Name} up…");
        ContainerRun run = new(plan, engine, parts.Views.Run.Sink)
        {
            StartedLater = later,
            How = how,
            LabelledFor = session.Folder,
            Certificate = certificate,
            Tools = session.ContainerTools,
        };
        run.Brought([.. holding.Select(h => ((IRunService)h.Service, h.How))]);
        run.ProblemFound += (_, found) =>
        {
            Problem problem = Placed(found, CSharp.ServerPlaces.Texts(parts.Editors));
            if (!session.IsEnded && !session.Problems.Contains(problem))
            {
                session.Problems.Add(problem);
                ShowProblems();
            }
        };
        // Before up: a Stop or the folder's end while it comes up takes down what came.
        session.Containers.StartedProject(plan.Compose);
        session.ContainerRun = run;
        RunningChanged?.Invoke(this, EventArgs.Empty);
        List<string> notes = run.Notes;
        notes.AddRange(plan.Notes);
        if (overHttp is not null)
        {
            notes.Add(overHttp);
        }
        string? failed = null;
        try
        {
            // As deployed, the images are built from the files as saved now: an edit made after is one their sessions apply.
            if (plan.Mode == ContainerMode.Images)
            {
                FollowBuilt(sessions);
                failed = await parts.Editors.SaveAllLandedAsync(work, work.Stopping);
            }
            if (failed is null)
            {
                if (upHeld is { } hold)
                {
                    upHeld = null;
                    hold.Holding.TrySetResult();
                    await hold.Until.WaitAsync(work.Stopping);
                }
                failed = await run.PublishAsync(work.Stopping)
                    ?? await run.UpAsync(work.Stopping, debugged.Count == 0 ? null : (up, stop) => AttachAllAsync(run, debugged, up, notes, work, stop, how == RunStart.Profile ? how : null));
            }
        }
        catch (OperationCanceledException)
        {
            failed = Canceled(work, chosen.Name);
        }
        catch (Exception unstarted) when (unstarted is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            failed = $"{chosen.Name} cannot run: {unstarted.Message}";
        }
        if (failed is null && (session.IsEnded || work.IsStopped || run.IsStopped))
        {
            failed = Canceled(work, chosen.Name);
        }
        if (failed is not null)
        {
            if (session.IsEnded)
            {
                // The folder's end takes down what came, once its up has ended; the window's views are another folder's now.
                return Refusals.SolutionClosed;
            }
            // A run that did not start leaves nothing running, its own alone: a start that replaced it has its own.
            IdeLog.Write(IdeLog.Ide, $"Containers: {chosen.Name} did not start ({failed}): what came up is taken down");
            await session.StopContainersAsync(run);
            if (session.ContainerRun == run && !session.IsEnded)
            {
                parts.Views.Run.SetRunning(false);
                RunningChanged?.Invoke(this, EventArgs.Empty);
            }
            return Said(failed);
        }
        string? following = Started(await session.Runner.StartAsync(engine.Cli!, run.Logs, plan.Folder, ContainerEngine.Variables));
        if (following is not null)
        {
            await session.StopContainersAsync(run);
            return following;
        }
        string what = ServicesStarted(chosen.Name, how, Plural.Listed([.. debugged.Select(s => s.Service)]));
        parts.Say($"{what}{(how == RunStart.Run ? ":" : ";")} {Plural.Of(plan.Dotnet.Count(d => plan.Starts(d.Service)), ".NET service")} from Passenger's build"
            + $"{(plan.Mode == ContainerMode.Images ? " images" : string.Empty)}, {Plural.Of(plan.Backing.Count, "other service")}; Stop takes them down"
            + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty),
            // What dev-certs said of a certificate that did not come is in the Run window.
            unexported ? StatusPlace.Window(parts.Dock, ToolWindowIds.Run) : null);
        parts.Reveal?.Invoke(plan.ProjectName);
        if (plan.BrowserUrl is not null && await run.BrowserUrlAsync(CancellationToken.None) is { } url)
        {
            run.Address = url;
            parts.OpenBrowser(new LaunchPlan { Name = chosen.Name, Program = engine.Cli!, WorkingDirectory = plan.Folder, BrowserUrl = url, BrowserService = plan.LaunchService ?? chosen.Name },
                session.Runner.Ended);
        }
        return null;
    }

    /// <summary>Containers of the run's Compose project, of the services its <c>up</c> makes, that this Passenger did not make
    /// (the person's own, from their own <c>docker compose up</c>): Compose would make them again with Passenger's labels and
    /// settings, and Stop would then remove them. The person is asked first (Replace Them, after which they are this run's,
    /// or Cancel); an agent's run, and an IDE with nobody to ask, replaces none and refuses, saying why. Null when it may go
    /// on, else what it says.</summary>
    private async Task<string?> ReplacesOthersAsync(string starting, ContainerPlan plan, ContainerEngine engine, Working work)
    {
        ChildResult listed;
        try
        {
            listed = await BackgroundWork.Run(() => engine.RunAsync(["ps", "-a", "--filter", $"label={ComposeLabels.Project}={plan.ProjectName}", "--format",
                $"{{{{.Names}}}}\t{{{{.Label \"{ComposeLabels.Service}\"}}}}\t{{{{.Label \"{ContainerLabels.Machine}\"}}}}\t{{{{.Label \"{ContainerLabels.Process}\"}}}}"],
                ContainerEngine.AskLimit, work.Stopping));
        }
        catch (Exception unasked) when (ChildFailures.Of(unasked))
        {
            // An engine that does not list them now refuses up's own commands next, which says why.
            return null;
        }
        // Not trimmed: a container without Passenger's labels ends its line in empty columns.
        string[] others = [.. listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == 4 && plan.Touched.Contains(row[1]) && (row[2] != ContainerLabels.MachineValue || row[3] != ContainerLabels.ProcessValue))
            .Select(row => row[0])];
        if (!listed.Succeeded || others.Length == 0)
        {
            return null;
        }
        string named = string.Join(", ", others);
        if (NoOneToAsk() is { } why)
        {
            return $"{starting} was not run: it would replace {named} of {plan.ProjectName}, which Passenger did not make, and {why}; "
                + "remove them first, or run it from the window";
        }
        bool replace = false;
        if (parts.AskReplace is { } ask)
        {
            // The person's answer takes as long as it takes: the run's hold on what is drawn is let go meanwhile.
            BackgroundWork.End();
            try
            {
                replace = await ask(starting, plan.ProjectName, others, work.Stopping);
            }
            finally
            {
                BackgroundWork.Begin();
            }
        }
        work.Stopping.ThrowIfCancellationRequested();
        return replace ? null : $"{starting} was not run: {named} of {plan.ProjectName}, which Passenger did not make, were left as they are";
    }

    /// <summary>The container run whose log ended by itself while it is asked whether its containers still run, which keeps
    /// Stop meanwhile.</summary>
    private ContainerRun? followingAgain;

    /// <summary>A container run's log that ended while the run was not stopped (Compose's <c>logs --follow</c> can, as a service
    /// is made anew) is followed again from its end, saying so in the Run console, once its containers are known to run, a
    /// restart under way waited out; else it is the run's end.</summary>
    private async Task FollowContainersAgainAsync(ContainerRun run, int code)
    {
        string since = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        bool up;
        try
        {
            up = await run.StillUpAsync(CancellationToken.None);
        }
        catch (Exception unknown) when (unknown is OperationCanceledException or ContainerCommandException or TimeoutException
            or System.Text.Json.JsonException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            up = false;
        }
        if (followingAgain == run)
        {
            followingAgain = null;
        }
        if (session.IsEnded || session.Runner.IsRunning)
        {
            return;
        }
        // A log followed again that ends at once is not followed for good: the run is said to have ended.
        bool atOnce = followedAgainAt is { } at && DateTime.UtcNow - at < TimeSpan.FromSeconds(2);
        if (!up || atOnce || run.IsStopped || session.ContainerRun != run || run.Engine.Cli is not { } cli)
        {
            RunLogEnded(code);
            return;
        }
        parts.Views.Run.Append($"{run.Plan.Name}'s services still run: following their log again\n");
        followedAgainAt = DateTime.UtcNow;
        if (Started(session.Runner.Start(cli, run.LogsSince(since), run.Plan.Folder, ContainerEngine.Variables)) is not null)
        {
            RunLogEnded(code);
        }
    }

    /// <summary>When a container run's log was last followed again.</summary>
    private DateTime? followedAgainAt;

    /// <summary>Whether Detach acts on the container run under way: one is under way, and Detach is the run's whole (see
    /// <see cref="DetachesWhole"/>) or the chosen session is one of its services'.</summary>
    public bool DetachesContainers(ActionContext context, DebugSession? chosen) =>
        Containers is { } run && IsRunning && (DetachesWhole(context, chosen) || ServiceDebuggedBy(chosen!)?.Run == run);

    /// <summary>Whether Detach lets go of the container run whole: asked from the Run window, the run's own, or with no session
    /// of a program chosen; else, a service's debug tab chosen, it lets go of that service's program alone.</summary>
    private static bool DetachesWhole(ActionContext context, DebugSession? chosen) =>
        context.ToolWindow?.Id == ToolWindowIds.Run || chosen is not { State: not DebugState.Ended };

    /// <summary>Why Detach cannot act on the container run under way now: none is; let go of whole, it is still coming up,
    /// the profiler records one of its services (Stop ends the recording), or Hot Reload changed one, which no debugger is
    /// let go of; of one service, what the debugger says of its session.</summary>
    public string? WhyNotDetachContainers(ActionContext context, DebugSession? chosen) =>
        Containers is not { } run || !IsRunning ? NoContainerRun
        : !DetachesWhole(context, chosen) ? session.Debug.WhyNotDetachOf(chosen!)
        : run.IsComingUp ? $"{run.Name} is still coming up"
        : run.Profiled is { } profiled ? $"The profiler records {profiled.Name} of {run.Name}: Stop ends the recording, and Detach lets go of a run nothing records"
        : run.Services.FirstOrDefault(s => run.HotReloadChanged(session.Debug, s)) is { } changed ? DebugController.LetsNoDebuggerGo(run.SessionName(changed))
        : null;

    /// <summary>Detach of the container run under way, said in the Run window and the status. Whole (from the Run window, or
    /// with no service's debug tab chosen): the debugger lets go of each service it holds (running on), Compose's watch ends,
    /// and its log is no longer followed; Run, Debug and Stop no longer act on it, the Containers window still lists its
    /// containers to stop there, and the folder's end takes them down as it takes all it started. Of the service whose tab is
    /// chosen: the debugger lets go of its program alone, the run going on as it was. Null once done, else why not.</summary>
    public async Task<string?> DetachContainersAsync(ActionContext context, DebugSession? chosen)
    {
        UiThread.Check();
        if (WhyNotDetachContainers(context, chosen) is { } why)
        {
            return Said(why);
        }
        ContainerRun run = Containers!;
        if (!DetachesWhole(context, chosen) && ServiceDebuggedBy(chosen!) is ({ } of, { } service))
        {
            if (await session.Debug.DetachAsync(chosen) is { } refused)
            {
                return Said(refused);
            }
            string one = $"Detached from {service.Name} of {of.Name}: the debugger let go of it alone, and the run goes on as it was";
            parts.Views.Run.Append($"{one}\n");
            parts.Say(one, StatusPlace.Window(parts.Dock, ToolWindowIds.Run));
            return null;
        }
        foreach (IRunService each in run.Services)
        {
            await session.Debug.DetachAsync(run.SessionName(each));
        }
        run.LetGo();
        session.RunsLetGo.Add(run);
        parts.Views.Run.Append($"{LetGoSaid(run.Name)}\n");
        // Its log's end is said as the run's (RunLogEnded): what Detach did.
        session.Runner.Stop();
        RunningChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>What Detach of a container run whole says.</summary>
    private static string LetGoSaid(string name) => $"Detached from {name}: its containers run on until the Containers window stops them or the folder closes";

    /// <summary>How a container run whose log stopped is said: let go of by Detach, taken down by Stop, its backing services
    /// kept, or its services all ended by themselves.</summary>
    private string ContainersEnded(ContainerRun run) => run.IsLetGo ? LetGoSaid(ran)
        : !run.IsStopped ? $"{ran}'s services have all ended"
        : run.Plan.KeepBacking && run.Plan.Backing.Count > 0 ? $"{ran} stopped: its .NET services were taken down, {string.Join(", ", run.Plan.Backing)} kept running"
        : $"{ran} stopped: its containers were taken down";

    /// <summary>Rerun of a container run in fast mode (<see cref="RestartedInPlace"/>): its .NET services built again on the
    /// host, and only those whose build changed restarted (their containers kept, reading the new build through the same
    /// mount; no image is built), a service held for Hot Reload attached to again, its session taking the edits made since
    /// the build saved the files. Null once done, else why not.</summary>
    private async Task<string?> RerunContainersAsync(ContainerRun run)
    {
        if (parts.Work.Begin(AppActions.RunProgram, $"Building {run.Plan.Name} again…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(AppActions.RunProgram)!));
        }
        using (work)
        {
            using IDisposable held = BackgroundWork.Scope();
            List<(IRunService Service, bool HotReload)> again = Reattached(run, run.Services);
            try
            {
                if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
                {
                    return Said(unsaved);
                }
                BuildOutcome built = await BuildForAsync(run.Plan.Builds, work.Stopping, work, reading: () => FollowBuilt(again.Select(a => (a.Service.Name, a.HotReload))),
                    configured: run.Plan.Workspace);
                if (!built.Succeeded)
                {
                    return Said(built.Message);
                }
                IReadOnlyList<DotnetService> changed = run.Changed();
                work.Say(changed.Count == 0 ? "No service's build changed" : $"Restarting {string.Join(", ", changed.Select(c => c.Service))}…");
                string? failed = await RestartServicesAsync(run, changed, work);
                // Passenger pins the ports it can; with an older Compose the engine may have chosen another at the restart.
                string moved = failed is null && changed.Count > 0 && run.Address is { } before && await run.BrowserUrlAsync(CancellationToken.None) is { } now && now != before
                    ? $"; it now answers at {run.Address = now}" : string.Empty;
                return Said(failed ?? Reran(run.Plan.Name, [.. changed.Select(c => c.Service)], moved));
            }
            finally
            {
                // A service whose build did not change runs on in its session, which follows the edits already.
                LetGoBuilt(again.Select(a => a.Service.Name));
            }
        }
    }

    /// <summary>The Containers window's Restart, Stop or Remove of the container run's own Compose project, and an agent's,
    /// through the run, as for one of its services: Restart ends its sessions and recording, restarts its other services as
    /// they are and its .NET services as the run's restart does (held again where debugged, attached again); Stop and Remove
    /// are the run's own Stop, its sessions ended before its containers go. Null once done, else why not.</summary>
    public async Task<string?> ProjectActionAsync(string action)
    {
        UiThread.Check();
        if (Containers is not { } run)
        {
            return Said("No container run is under way");
        }
        if (action != "restart")
        {
            await session.StopContainersAsync(run);
            return null;
        }
        if (parts.Work.Begin(AppActions.ContainerRestart, $"Restarting {run.Plan.ProjectName}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(AppActions.ContainerRestart)!));
        }
        using (work)
        {
            using IDisposable held = BackgroundWork.Scope();
            List<DotnetService> dotnet = [.. run.Plan.Dotnet.Where(d => d.Starts && run.Plan.Touched.Contains(d.Service))];
            List<string> others = [.. run.Plan.Touched.Where(s => dotnet.All(d => d.Service != s))];
            await EndSessionsAsync(run, dotnet);
            try
            {
                if (await run.RestartOthersAsync(others, work.Stopping) is { } failed)
                {
                    return Said(failed);
                }
            }
            catch (OperationCanceledException)
            {
                return Said(StoppedRestarting(run.Plan.Name));
            }
            return Done(await RestartServicesAsync(run, dotnet, work, "restarted from the Containers window"), $"{run.Plan.Name}: restarted its services");
        }
    }
}
