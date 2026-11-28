using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>A start of a run of services under way: its action's work, which Stop and the next start of the same end, what it
/// starts (a container run's one place, or an Aspire configuration's AppHosts), and its end.</summary>
public sealed record StartUnderWay(Working Work, Task Ended, IReadOnlyCollection<string> Starts);

/// <summary>What is done to a .NET service of a run of services (<see cref="ServiceRun"/>), written once over the model for
/// every kind of run: the held start's attach (a session per service held, all at once, while the run comes up), Debug
/// (restarted held and attached, kept for the run's restarts and a Rerun), Attach (as it runs), Run without Debugger (its
/// session let go, unheld from its next restart), Restart (its session ended first, held again where debugged and attached
/// again), and the restart for an edit Hot Reload cannot apply (only the services whose project holds it); of one the run
/// left unstarted (Don't start), Debug, Run and Profile start it later (<see cref="StartLaterAsync"/>) and Attach refuses.
/// What a kind does its own way (how a service restarts, where its program is) is the run's.</summary>
public sealed partial class BuildRunController
{
    /// <summary>What a person chose for each .NET service of a configuration's run as it ran (debugged, or run without the
    /// debugger), by configuration: its restarts and a Rerun that brings it up again keep it, until it is chosen otherwise.</summary>
    private readonly Dictionary<string, Dictionary<string, bool>> chosenDebugged = new(StringComparer.Ordinal);


    /// <summary>One start at a time of what a run starts (<paramref name="starts"/>): a start ends each one under way that
    /// starts any of the same (Debug pressed as Run brings it up), as Stop ends it, and goes on once it has ended; held in
    /// <see cref="ProjectSession.Starting"/>, which the folder's end ends, until the start is up or has given up (disposed).
    /// Null when this start was stopped meanwhile.</summary>
    private async Task<StartTaken?> TakeStartAsync(IReadOnlyCollection<string> starts, Working work)
    {
        while (session.Starting.FirstOrDefault(s => s.Starts.Any(started => starts.Contains(started, PathNames.Comparer))) is { } earlier)
        {
            earlier.Work.Cancel();
            try
            {
                await earlier.Ended.WaitAsync(work.Stopping);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartUnderWay mine = new(work, ended.Task, starts);
        session.Starting.Add(mine);
        return new StartTaken(session, mine, ended);
    }

    /// <summary>A start held in the session's starts, let go as it is up or has given up.</summary>
    private sealed class StartTaken(ProjectSession session, StartUnderWay mine, TaskCompletionSource ended) : IDisposable
    {
        public void Dispose()
        {
            session.Starting.Remove(mine);
            ended.TrySetResult();
        }
    }

    /// <summary>Attaches to each service held at its start, all at once, while its run comes up: null once each is attached
    /// (one whose runtime has its debugger off runs on undebugged, said in <paramref name="notes"/>), else why not.</summary>
    private async Task<Words?> AttachAllAsync(ServiceRun run, IReadOnlyList<IRunService> services, Task up, List<string> notes, Working work, CancellationToken stop,
        RunStart? purpose = null) =>
        (await AttachEachAsync(run, services, up, notes, work, stop, purpose)).Select(a => a.Refused).FirstOrDefault(s => s is not null);

    /// <summary>Attaches to each service held at its start, all at once: each with why it was not attached, or null.</summary>
    private Task<(IRunService Service, Words? Refused)[]> AttachEachAsync(ServiceRun run, IReadOnlyList<IRunService> services, Task up, List<string> notes, Working work,
        CancellationToken stop, RunStart? purpose = null)
    {
        string named = string.Join(", ", services.Select(s => s.Name));
        work.Say(purpose == RunStart.Profile ? LaunchWords.AttachingTool("profiler", named) : services.All(s => run.HeldFor(s) == RunStart.HotReload) ? LaunchWords.AttachingForReload
            : LaunchWords.AttachingTool("debugger", named));
        return Task.WhenAll(services.Select(async s => (s, await AttachAsync(run, s, up, notes, stop, purpose))));
    }

    /// <summary>A service's start held for the debugger (quietly for Hot Reload), attached to as its kind starts it, once:
    /// an Aspire service's, which DCP asks for (<see cref="ServiceRun.AttachesItsStarts"/>). Null once attached, else why
    /// not, which its run's console says.</summary>
    public Task<Words?> AttachStartAsync(ServiceRun run, IRunService service, RunStart purpose, List<string> notes, CancellationToken stop) =>
        AttachAsync(run, service, Task.CompletedTask, notes, stop, purpose);

    /// <summary>One service, held at its start or running: its program, once its run has started it, where its kind runs it
    /// (<see cref="ServiceRun.ProgramAsync"/>), and a session attached to it, which lets it go: one with its tab in the Debug
    /// window, or, under Run with Hot Reload, a quiet one; or the profiler recording it. Null once attached, else why not.</summary>
    private async Task<Words?> AttachAsync(ServiceRun run, IRunService service, Task up, List<string> notes, CancellationToken stop, RunStart? given = null,
        bool asItRuns = false)
    {
        UiThread.Check();
        // Held for the debugger (in a tab, or quietly for Hot Reload), held for the profiler, or attached to as it runs.
        RunStart purpose = given ?? run.HeldFor(service) ?? RunStart.Debug;
        ServiceProgram? found;
        try
        {
            found = await run.ProgramAsync(service, up, stop);
        }
        catch (ServiceUnreachedException failed)
        {
            return LaunchWords.CouldNotAttach(purpose == RunStart.Profile ? "profile" : "debug", service.Name, RefusedException.Of(failed));
        }
        if (found is not { } program)
        {
            // Its run ended without starting it: what it said is in its console, and the run's failure says why.
            return null;
        }
        // Stopped or its folder left meanwhile: a session begun now would outlive the Stop, or open in the next folder's window.
        stop.ThrowIfCancellationRequested();
        if (run.IsStopped || session.IsEnded)
        {
            throw new OperationCanceledException(stop);
        }
        if (program.WhyNot is { } why)
        {
            run.Unreached[service.Name] = why;
            // Held at its start, it runs on; asked for as it runs, the profiler that cannot reach it refuses.
            Words without = LaunchWords.RunsWithout(purpose == RunStart.Profile ? "profiler" : "debugger", service.Name, why);
            if (asItRuns && purpose == RunStart.Profile)
            {
                return without;
            }
            notes.Add(without.Shown);
            return null;
        }
        run.Unreached.Remove(service.Name);
        foreach (string note in program.Notes)
        {
            notes.Add($"{service.Name}: {note}");
        }
        Words? refused;
        string name = run.SessionName(service);
        if (purpose == RunStart.Profile)
        {
            // The one recording of a service of either kind, held or running, which sets what Stop reads first.
            refused = parts.ProfileService is { } profile ? await profile(run, service, program, asItRuns, stop) : LaunchWords.ProfilerUnavailable;
        }
        else
        {
            run.Debugged.Add(name);
            // Followed since its build saved the files, or a copy of those its run keeps: its session takes them, else they end here.
            using BuiltEdits? edits = builtEdits.Remove(service.Name, out BuiltEdits? built) ? built : run.KeptFor(service) is { } kept ? session.Debug.Copied(kept) : null;
            // Built as its run builds, the folder's choice when it started where the run names none of its own.
            ProgramBuild? builds = (run.Workspace ?? session.Workspace) is { } runs ? new ProgramBuild(name, service.Project, runs) : null;
            refused = purpose == RunStart.HotReload
                ? await session.Debug.AttachForHotReloadAsync(program.Id, name, (line, error) => run.Say(service, line, error), program.Place, stop, edits, program.Handlers,
                    program.Browser, program.App, builds)
                : await session.Debug.AttachAsync(program.Id, name, hotReload: program.HotReload, place: program.Place, group: run.Group, app: program.App, edits: edits,
                    handlers: program.Handlers, browser: program.Browser, builds: builds);
            if (refused is null)
            {
                DebugTab? tab = purpose == RunStart.HotReload ? null : session.Debug.Tabs.LastOrDefault(t => t.Name == name && t.IsLive);
                run.Attached(service, program, tab, tab?.Session ?? session.Debug.Running.LastOrDefault(r => r.Name == name)?.Session);
                // A Blazor Web App an AppHost starts debugged has its WebAssembly half debugged beside it, as a local one does.
                if (tab is not null && service is Aspire.AspireResourceRun { Plan: { InBrowser.Count: > 0 } plan })
                {
                    IdeErrors.Observe(DebugServiceBrowserAsync(name, plan));
                }
            }
        }
        return refused is null ? null : program.Caveat is { } caveat ? LaunchWords.WithCaveat(refused, caveat) : refused;
    }

    /// <summary>A debugged service's code in the browser debugged beside its tab, at the page its run serves: the address its
    /// orchestrator gave it (ASPNETCORE_URLS) on its launch profile's path; one no browser debugs says why.</summary>
    private async Task DebugServiceBrowserAsync(string name, LaunchPlan plan)
    {
        LaunchPlan paged = plan with { Name = name, Page = PageOf(plan) };
        IReadOnlyList<Passenger.Workspace.ProjectModel> projects = session.Projects;
        PageBrowser? named = PageBrowsers.Of(paged.BrowserLine, paged.Name, parts.Settings().Browser);
        (Debugging.BrowserDebugging? inBrowser, string? notInBrowser) = await BackgroundWork.Run(() => Debugging.BrowserDebugging.For(paged, projects, named));
        if (session.IsEnded || session.Debug.SessionNamed(name) is not { State: not DebugState.Ended } server)
        {
            return;
        }
        if (inBrowser is null)
        {
            parts.Say(LaunchWords.BrowserNotDebugged(name, notInBrowser ?? LaunchWords.NoBrowserDebugs));
            return;
        }
        await DebugBrowserAsync(paged with { Browser = inBrowser }, inBrowser, DebugEnded(server));
    }

    /// <summary>The page a service serves as its run started it: the first address of ASPNETCORE_URLS its orchestrator gave it
    /// (a wildcard host made localhost), the scheme its launch profile's page has where both are given, on that page's path.</summary>
    internal static Uri? PageOf(LaunchPlan plan)
    {
        Uri? page = plan.Page;
        if (plan.Environment.GetValueOrDefault(AspNetCoreVariables.Urls) is not { Length: > 0 } urls)
        {
            return page;
        }
        Uri[] served = [.. urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(url => url.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal)
                .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal).Replace("://[::]", "://localhost", StringComparison.Ordinal))
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https" ? address : null).OfType<Uri>()];
        if ((served.FirstOrDefault(s => s.Scheme == page?.Scheme) ?? served.FirstOrDefault()) is not { } at)
        {
            return page;
        }
        return new UriBuilder(at.Scheme, at.Host, at.Port, page?.AbsolutePath ?? "/") { Query = page?.Query.TrimStart('?') ?? string.Empty }.Uri;
    }

    /// <summary>Restarts services of a run (their sessions and recording ended first, so no debugger loses its program under
    /// it and the profiler's trace keeps its rundown), their projects built again between their stop and start where
    /// <paramref name="build"/> asks it of a kind that builds then, and attaches again to those held for the debugger and those
    /// attached to as they ran. Null once done, else why not: a failure its kind says (a command refused, a time limit) is
    /// said as the restart's refusal, whoever asked.</summary>
    private async Task<Words?> RestartServicesAsync(ServiceRun run, IReadOnlyList<IRunService> services, Working work, string? why = null, bool build = false)
    {
        List<IRunService> held = run.AttachesItsStarts ? [] : [.. run.Held.Where(h => services.Any(s => run.KeyOf(s) == run.KeyOf(h)))];
        List<IRunService> running = [.. services.Where(s => run.AttachedRunning.Contains(run.KeyOf(s)) && run.HeldFor(s) is null)];
        List<(IRunService Service, bool HotReload)> sessions = Reattached(run, services);
        // Restarted on the build its program runs, none followed since: its next session takes the ended one's edits.
        foreach ((IRunService service, bool hotReload) in sessions.Where(a => !build && !builtEdits.ContainsKey(a.Service.Name) && run.RestartsOnItsBuild(a.Service)))
        {
            if (session.Debug.Carried(run.SessionName(service), hotReload) is { } carried)
            {
                builtEdits[service.Name] = carried;
            }
        }
        await EndSessionsAsync(run, services);
        try
        {
            // Built as they restart from the files as saved now: an edit made after is one their next sessions apply.
            if (run.BuildsAsItRestarts)
            {
                FollowBuilt(sessions.Select(a => (a.Service.Name, a.HotReload)));
                if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
                {
                    return unsaved;
                }
            }
            if (upHeld is { } hold)
            {
                upHeld = null;
                hold.Holding.TrySetResult();
                await hold.Until.WaitAsync(work.Stopping);
            }
            if (await run.RestartAsync(services, work.Stopping, why, build) is { } failed)
            {
                return failed;
            }
            List<string> notes = [];
            (IRunService Service, Words? Refused)[] attached = held.Count == 0 ? [] : await AttachEachAsync(run, held, Task.CompletedTask, notes, work, work.Stopping);
            Words? unattached = attached.Select(a => a.Refused).FirstOrDefault(r => r is not null);
            // Each attached to in turn, whatever one before it refused: the first refusal is the one said.
            foreach (IRunService again in running)
            {
                Words? refused = await AttachAsync(run, again, Task.CompletedTask, notes, work.Stopping, RunStart.Debug);
                unattached ??= refused;
            }
            // Restarted held, a service no debugger reached would wait for good: it is made again unheld, its hold let go.
            if (attached.Where(a => a.Refused is not null).Select(a => a.Service).ToList() is { Count: > 0 } stuck)
            {
                foreach (IRunService service in stuck)
                {
                    await run.HoldAsync(service, null, CancellationToken.None);
                }
                string names = string.Join(", ", stuck.Select(s => s.Name));
                string? unheld = await run.RestartAsync(stuck, CancellationToken.None, "to run without the debugger, which could not attach");
                return unheld is null ? LaunchWords.RestartedUnheld(unattached!, names) : LaunchWords.NotRestartedUnheld(unattached!, names, unheld);
            }
            return unattached;
        }
        catch (OperationCanceledException)
        {
            return StoppedRestarting(run.Name);
        }
        catch (Exception failed) when (run.Refused(failed))
        {
            return LaunchWords.ServicesNotRestarted(string.Join(", ", services.Select(s => s.Name)), run.Name, RefusedException.Of(failed));
        }
        finally
        {
            LetGoBuilt(sessions.Select(a => a.Service.Name));
        }
    }

    /// <summary>The services of those given whose restart a session of the debugger or Hot Reload attaches to again (held for
    /// either, or attached to as they ran), each with whether it applies Hot Reload; none where the run's kind attaches to
    /// each start itself.</summary>
    private List<(IRunService Service, bool HotReload)> Reattached(ServiceRun run, IEnumerable<IRunService> services) => run.AttachesItsStarts ? []
        : [.. services.Where(s => run.HeldFor(s) is RunStart.Debug or RunStart.HotReload || run.HeldFor(s) is null && run.AttachedRunning.Contains(run.KeyOf(s)))
            .Select(s => (s, run.HeldFor(s) == RunStart.HotReload || session.Debug.HotReloadOn()))];

    /// <summary>The run and its .NET service a session debugs or runs with Hot Reload, or null where it is none of a run's.</summary>
    public (ServiceRun Run, IRunService Service)? ServiceDebuggedBy(DebugSession debugged) =>
        session.ServiceRuns.Where(run => !run.IsStopped)
            .SelectMany(run => run.Services.Where(s => run.SessionOf(session.Debug, s) == debugged || run.QuietOf(session.Debug, s) == debugged).Select(s => (run, s)))
            .FirstOrDefault() is ({ } of, { } service) ? (of, service) : null;

    /// <summary>A .NET service of the container run under way, by its name, or why there is none to act on.</summary>
    public (IRunService? Service, Words? WhyNot) ServiceOfRun(string? name) => Containers is { } run && IsRunning ? ServiceOf(run, name)
        : (null, NoContainerRun);

    private static Words NoContainerRun => LaunchWords.NoContainerRun;

    /// <summary>A .NET service of a run, by its name, or why there is none to act on.</summary>
    public (IRunService? Service, Words? WhyNot) ServiceOf(ServiceRun run, string? name)
    {
        if (run.IsStopped)
        {
            return (null, Refusals.NotRunning(run.Name));
        }
        if (name is null || run.ServiceNamed(name) is not { } service)
        {
            return (null, name is null ? LaunchWords.NoServiceChosen(run.Name)
                // One of its compose files whose project nothing named: the person names it, as a copy of the configuration keeps.
                : run is ContainerRun { Plan.Backing: var backing } && backing.Contains(name) ? LaunchWords.ServiceOfNoProject(name, run.Name)
                : LaunchWords.NotDotnetService(name, run.Name));
        }
        // One it left unstarted is acted on too: its Debug, Run and Profile start it later.
        return service.Starts ? (service, null)
            : run.LeftUnstarted(service) ? run.WhyNotStartedLater(service) is { } unfit ? (null, unfit) : (service, null)
            : (null, LaunchWords.DoesNotStart(run.Name, name));
    }

    /// <summary>Why one of a service's actions (<see cref="AppActions.ServiceDebug"/> and the rest) cannot act on a .NET service
    /// of a run (the container run under way, when none is given) now, or null: no such service there, or what its run says
    /// (<see cref="ServiceRun.WhyNot"/>).</summary>
    public Words? WhyNotOnService(string action, string? name, ServiceRun? run = null) =>
        (run ?? (IsRunning ? Containers : null)) is not { } of ? NoContainerRun
        : ServiceOf(of, name) is ({ } service, null) ? of.WhyNot(action, service)
        : ServiceOf(of, name).WhyNot;

    /// <summary>Whether the container run under way left a .NET service unstarted (marked Don't start), not started since.</summary>
    public bool LeftUnstarted(string name) => Containers is { } run && run.LeftUnstarted(name);

    /// <summary>A .NET service the run left unstarted (Don't start) started later by its action, the rest left as they run:
    /// its project built here first where its program runs that build (fast mode; as deployed its image is built as it
    /// starts), containers of it Passenger did not make for the folder asked about first, then started held as chosen (once
    /// for the profiler when <paramref name="profiled"/>) and attached to where held; the run's from then on (Stop takes it
    /// down, Rerun keeps it started). Null once started, else why not.</summary>
    private async Task<Words?> StartLaterAsync(ServiceRun run, IRunService service, Working work, string why, bool profiled = false)
    {
        if (run.WhyNotStartedLater(service) is { } unfit)
        {
            return unfit;
        }
        try
        {
            if (run.BuildsBeforeRestart)
            {
                work.Say(BuildWords.Doing("build", Path.GetFileNameWithoutExtension(service.Project)));
                BuildOutcome built = await BuildForAsync([service.Project], work.Stopping, work,
                    reading: () => FollowBuilt(Reattached(run, [service]).Select(a => (a.Service.Name, a.HotReload))), configured: run.Workspace);
                work.Stopping.ThrowIfCancellationRequested();
                if (!built.Succeeded)
                {
                    return built.Message;
                }
            }
            if (run is ContainerRun containers
                && await ReplacesOthersAsync($"{service.Name} of {containers.Name}", containers.Plan with { Touched = [service.Name] }, containers.Engine, work) is { } kept)
            {
                return kept;
            }
            if (run.IsStopped || session.IsEnded)
            {
                throw new OperationCanceledException(work.Stopping);
            }
            if (run.StartLater(service, profiled) is { } refused)
            {
                return refused;
            }
            work.Say(LaunchWords.Starting(service.Name));
            Words? failed = await RestartServicesAsync(run, [service], work, why);
            if (failed is not null || !profiled)
            {
                return failed;
            }
            // Held for the profiler alone, one that cannot be recorded is made again unheld rather than waiting for good.
            if (await AttachAsync(run, service, Task.CompletedTask, [], work.Stopping, RunStart.Profile) is not { } unrecorded)
            {
                return null;
            }
            string? unheld = await run.RestartAsync([service], CancellationToken.None, "to run without the profiler, which could not attach");
            return unheld is null ? LaunchWords.RestartedUnrecorded(unrecorded, service.Name) : LaunchWords.NotRestartedUnheld(unrecorded, service.Name, unheld);
        }
        finally
        {
            // Followed since its build, what its session did not take ends with its start.
            LetGoBuilt([service.Name]);
        }
    }

    /// <summary>Debug of one .NET service of the container run under way (<see cref="DebugServiceAsync(ServiceRun, string, IRunService)"/>).</summary>
    public Task<Words?> DebugServiceAsync(string name) => DebugServiceAsync(Containers, name);

    /// <summary>Debug of one .NET service of a run, the others left as they run: its session ended, its program made again
    /// held at its start, and a session attached in a Debug tab of its own, so its startup stops at its breakpoints; kept for
    /// the run's restarts and a Rerun until chosen otherwise, and for its configuration's next run (<see cref="NextRun"/>). One
    /// the run left unstarted is started now, held. The start given (a replica its window chose), else the service of that
    /// name. Null once attached, else why not.</summary>
    public Task<Words?> DebugServiceAsync(ServiceRun? run, string name, IRunService? start = null) => OnServiceAsync(run, name, AppActions.ServiceDebug, async (run, service, work) =>
    {
        Choose(run, name, debug: true);
        run.AttachedRunning.Remove(run.KeyOf(service));
        await run.HoldAsync(service, RunStart.Debug, work.Stopping);
        parts.Dock.Show(ToolWindowIds.Debug);
        parts.Views.Debug.ShowTab(console: true);
        // One the run left unstarted is started now, held.
        return run.LeftUnstarted(service)
            ? Done(await StartLaterAsync(run, service, work, "to debug it from its start"), LaunchWords.DebuggingFromStartNow(name, run.Name))
            : Done(await RestartServicesAsync(run, [service], work, "to debug it from its start"),
                NextRun(LaunchWords.DebuggingFromStart(run.ReplicaOf(service) ?? name, run.Name), run, service, debug: true));
    }, start);

    /// <summary>Attach to one .NET service of the container run under way (<see cref="AttachServiceAsync(ServiceRun, string, IRunService)"/>).</summary>
    public Task<Words?> AttachServiceAsync(string name) => AttachServiceAsync(Containers, name);

    /// <summary>Attach to one .NET service of a run as it runs, no restart: its startup has run already, which is said
    /// (Debug restarts it held to stop there). One a Debug tab debugs already has that tab shown, its session kept. Run
    /// with Hot Reload's quiet session of it debugs it where Hot Reload changed it (<see cref="DebugQuietAsync"/>), else is
    /// let go first, the program never stopped. A restart attaches again; one the run left unstarted has nothing to reach,
    /// said. The start given, else the service of that name. Null once attached, else why not.</summary>
    public Task<Words?> AttachServiceAsync(ServiceRun? run, string name, IRunService? start = null) => OnServiceAsync(run, name, AppActions.ServiceAttach, async (run, service, work) =>
    {
        if (run.WhyNot(AppActions.ServiceAttach, service) is { } unreached)
        {
            return Said(unreached);
        }
        if (run.SessionOf(session.Debug, service) is { } debugged && session.Debug.TabOf(debugged) is { IsLive: true } tab)
        {
            parts.Dock.Show(ToolWindowIds.Debug);
            await session.Debug.ChooseAsync(tab);
            return Done(null, LaunchWords.DebuggedAlready(run.ReplicaOf(service) ?? name, run.Name));
        }
        DebugSession? quiet = run.QuietOf(session.Debug, service);
        if (quiet is not null && session.Debug.HotReloadChanged(quiet))
        {
            return await DebugQuietAsync(run, service, quiet, work);
        }
        if (quiet is not null)
        {
            await session.Debug.DetachAsync(quiet);
        }
        await EndSessionsAsync(run, [service]);
        run.AttachedRunning.Add(run.KeyOf(service));
        parts.Dock.Show(ToolWindowIds.Debug);
        List<string> notes = [];
        Words? refused = await AttachAsync(run, service, Task.CompletedTask, notes, work.Stopping, RunStart.Debug);
        if (refused is not null || run.SessionOf(session.Debug, service) is not { State: not Debugging.DebugState.Ended })
        {
            run.AttachedRunning.Remove(run.KeyOf(service));
            return Said(refused ?? (notes.Count > 0 ? string.Join("; ", notes) : LaunchWords.NotAttached(name)));
        }
        return Done(null, notes.Count > 0 ? LaunchWords.DebuggingAsItRunsNoted(name, run.Name, string.Join("; ", notes)) : LaunchWords.DebuggingAsItRuns(name, run.Name));
    }, start);

    /// <summary>Attach to a service whose program Hot Reload changed under its quiet session, which its runtime keeps as its
    /// one debugger: that session debugs it in a Debug tab, the program running on. Null once it does, else why not.</summary>
    private async Task<Words?> DebugQuietAsync(ServiceRun run, IRunService service, DebugSession quiet, Working work)
    {
        ServiceProgram? program;
        try
        {
            program = await run.ProgramAsync(service, Task.CompletedTask, work.Stopping);
        }
        catch (ServiceUnreachedException failed)
        {
            return Said(LaunchWords.CouldNotAttach("debug", service.Name, RefusedException.Of(failed)));
        }
        if (program is null || run.IsStopped || session.IsEnded)
        {
            return Said(Refusals.NotRunning(service.Name, run.Name));
        }
        parts.Dock.Show(ToolWindowIds.Debug);
        if (await session.Debug.DebugQuietAsync(quiet, run.Group, program.App) is { } refused)
        {
            return Said(refused);
        }
        run.AttachedRunning.Add(run.KeyOf(service));
        run.Attached(service, program, session.Debug.TabOf(quiet), quiet);
        return Done(null, LaunchWords.DebuggingByReloadSession(service.Name, run.Name));
    }

    /// <summary>Run without debugger of one .NET service of the container run under way (<see cref="RunServiceAsync(ServiceRun, string, IRunService)"/>).</summary>
    public Task<Words?> RunServiceAsync(string name) => RunServiceAsync(Containers, name);

    /// <summary>Run without debugger of one .NET service of a run, kept as Debug's choice is: its session let go, the program
    /// running on, its next restart making it anew unheld; or, where its kind restarts it so (<see cref="ServiceRun.RestartsToRunWithoutDebugger"/>)
    /// or Hot Reload changed its program, restarted unheld at once, only when it is held or debugged. One running unheld with
    /// nothing debugging it is left as it is, said; one the run left unstarted is started now, unheld. The start given, else
    /// the service of that name. Null once done, else why not.</summary>
    public Task<Words?> RunServiceAsync(ServiceRun? run, string name, IRunService? start = null) => OnServiceAsync(run, name, AppActions.ServiceRun, async (run, service, work) =>
    {
        bool held = run.HeldFor(service) is not null;
        bool debugged = run.Debugged.Contains(run.SessionName(service)) && run.SessionOf(session.Debug, service) is { State: not Debugging.DebugState.Ended };
        bool changed = run.HotReloadChanged(session.Debug, service);
        Choose(run, name, debug: false);
        run.AttachedRunning.Remove(run.KeyOf(service));
        await run.HoldAsync(service, null, work.Stopping);
        if (run.LeftUnstarted(service))
        {
            return Done(await StartLaterAsync(run, service, work, "to run it"), LaunchWords.StartedUndebugged(name, run.Name));
        }
        string said = run.ReplicaOf(service) ?? name;
        if (!held && !debugged)
        {
            return Done(null, NextRun(LaunchWords.RunsUndebuggedAlready(said, run.Name), run, service, debug: false));
        }
        // A program Hot Reload changed is restarted: its runtime, which lets no debugger go then, would hold it for good.
        if (run.RestartsToRunWithoutDebugger || changed)
        {
            return Done(await RestartServicesAsync(run, [service], work, "to run without the debugger"),
                NextRun(changed && !run.RestartsToRunWithoutDebugger ? LaunchWords.RestartedUndebuggedChanged(said, run.Name) : LaunchWords.RestartedUndebugged(said, run.Name),
                    run, service, debug: false));
        }
        await session.Debug.DetachAsync(run.SessionName(service));
        return Done(null, LaunchWords.RunsOnUndebugged(name, run.Name));
    }, start);

    /// <summary>Restart of one .NET service of the container run under way (<see cref="RestartServiceAsync(ServiceRun, string, IRunService)"/>).</summary>
    public Task<Words?> RestartServiceAsync(string name) => RestartServiceAsync(Containers, name);

    /// <summary>Restart of one .NET service from its window or an agent, through its run: its session ended first,
    /// restarted (held again where it is debugged), and attached to again; one the run left unstarted started. The start
    /// given (a replica its window chose), else the service of that name. Null once done, else why not.</summary>
    public Task<Words?> RestartServiceAsync(ServiceRun? run, string name, IRunService? start = null) => OnServiceAsync(run, name, AppActions.ContainerRestart, async (run, service, work) => run.LeftUnstarted(service)
            ? Done(await StartLaterAsync(run, service, work, "started from its window"), LaunchWords.StartedService(run.Name, name))
            : Done(await RestartServicesAsync(run, [service], work, "restarted from its window"), LaunchWords.RestartedService(run.Name, name)), start);

    /// <summary>Profile of one .NET service of a run as it runs: its program where its kind runs it, recorded by the one
    /// recording of a service (<see cref="BuildRunParts.ProfileService"/>), which Stop, a restart and Rerun read first; one
    /// the run left unstarted started now, held for the profiler until it records. The start given, else the service of that
    /// name. Null once it records, else why not (said).</summary>
    public Task<Words?> ProfileServiceAsync(ServiceRun? run, string name, IRunService? start = null) => OnServiceAsync(run, name, AppActions.ServiceProfile, async (run, service, work) => await (run.LeftUnstarted(service) ? StartLaterAsync(run, service, work, "to profile it from its start", profiled: true)
            : AttachAsync(run, service, Task.CompletedTask, [], work.Stopping, RunStart.Profile, asItRuns: true)) is { } refused
            ? Said(refused) : null, start);

    /// <summary>Ends what reaches one .NET service of the container run from here (its session, the profiler's recording),
    /// before something takes its program from under it (the Containers window's Stop or Remove).</summary>
    public Task EndServiceSessionsAsync(string name) =>
        Containers is { } run && run.ServiceNamed(name) is { } service ? EndSessionsAsync(run, [service]) : Task.CompletedTask;

    /// <summary>Ends what reaches services of a run from here, its Stop's order (<see cref="ServiceRun.EndSessionsAsync"/>).</summary>
    private Task EndSessionsAsync(ServiceRun run, IEnumerable<IRunService> services) => run.EndSessionsAsync(session.Debug, services);

    /// <summary>Says how a service's action ended (what failed, else <paramref name="done"/>), and gives back what failed.</summary>
    private Words? Done(Words? failed, Words done)
    {
        parts.Say(failed ?? done);
        return failed;
    }

    /// <summary>What a service's action did (<paramref name="said"/>), with what a choice of one replica of several keeps for its
    /// configuration's next run after it: every replica's, as that run's replicas are new (their instances are made again each
    /// run); nothing more for a service of one.</summary>
    private static Words NextRun(Words said, ServiceRun run, IRunService service, bool debug) => run.ReplicaOf(service) is null ? said
        : Clauses.Then(said, debug ? LaunchWords.NextRunDebugs(service.Name) : LaunchWords.NextRunUndebugged(service.Name));

    /// <summary>What a person chose for a service of the run, kept for its configuration.</summary>
    private void Choose(ServiceRun run, string service, bool debug)
    {
        if (!chosenDebugged.TryGetValue(run.Name, out Dictionary<string, bool>? choices))
        {
            chosenDebugged[run.Name] = choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        choices[service] = debug;
    }

    /// <summary>One service's action under its own <see cref="Working"/>, Stop ending it: refused saying why when there is no
    /// run, the run has no such service, or the action is under way. On the start given while its run runs it, else on the
    /// service of that name.</summary>
    private async Task<Words?> OnServiceAsync(ServiceRun? run, string name, string action, Func<ServiceRun, IRunService, Working, Task<Words?>> act,
        IRunService? start = null)
    {
        string acting = action switch
        {
            AppActions.ServiceDebug => "debug",
            AppActions.ServiceAttach => "attach",
            AppActions.ServiceRun => "run",
            AppActions.ContainerRestart => "restart",
            _ => "profile",
        };
        UiThread.Check();
        if (run is null)
        {
            return Said(ServiceOfRun(name).WhyNot!);
        }
        if ((start is not null && !run.IsStopped && run.Services.Contains(start) ? (start, null) : ServiceOf(run, name)) is not ({ } service, null))
        {
            return Said(ServiceOf(run, name).WhyNot!);
        }
        if (parts.Work.Begin(action, LaunchWords.ServiceActing(acting, name)) is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(action)!));
        }
        using (work)
        {
            using IDisposable held = BackgroundWork.Scope();
            try
            {
                return await act(run, service, work);
            }
            catch (OperationCanceledException)
            {
                return Said(LaunchWords.ServiceActionStopped(acting, name));
            }
            catch (Exception failed) when (run.Refused(failed))
            {
                return Said(LaunchWords.ServiceActionFailed(acting, name, RefusedException.Of(failed)));
            }
        }
    }

    /// <summary>Hot Reload's restart for edits it could not apply to services of the run whose sessions <paramref name="names"/>
    /// are: those whose project holds an edit that needs it (Roslyn calls an edit to another project rude in each service's
    /// session), built again here first where their programs run this build and a build can write over them, restarted held,
    /// and attached to again. True once they are.</summary>
    public async Task<bool> RestartServicesForEditAsync(Debugging.HotReloadOutcome outcome, IReadOnlyList<string> names)
    {
        if (session.ServiceRuns.Select(run => (Run: run, Named: run.Held.Where(h => names.Contains(run.SessionName(h))).ToList())).FirstOrDefault(r => r.Named.Count > 0)
            is not ({ } run, { } named))
        {
            return false;
        }
        List<string> rude = [.. outcome.Problems.Where(p => p.Severity == ProblemSeverity.Error && p.Path is not null).Select(p => p.Path!)];
        List<IRunService> services = [.. named.Where(s => rude.Any(path => PathNames.IsUnder(path, Path.GetDirectoryName(s.Project)!)))];
        if (services.Count == 0)
        {
            services = named;
        }
        names = [.. services.Select(s => s.Name)];
        string action = run.How == RunStart.Debug ? AppActions.Debug : AppActions.RunProgram;
        if (parts.Work.Begin(action, LaunchWords.RestartingToApplyServices(string.Join(", ", names))) is not { } work)
        {
            return false;
        }
        using (work)
        {
            using IDisposable held = BackgroundWork.Scope();
            // Built here first, or as their images are built in their restart, each saving the files and following from there.
            List<(string Service, bool HotReload)> again = [.. Reattached(run, services).Select(a => (a.Service.Name, a.HotReload))];
            try
            {
                if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
                {
                    parts.Say(unsaved);
                    return false;
                }
                List<string> projects = [.. services.Select(s => s.Project).Distinct(StringComparer.Ordinal)];
                if (run.BuildsBeforeRestart && !(await BuildForAsync(projects, work.Stopping, work, reading: () => FollowBuilt(again), configured: run.Workspace)).Succeeded)
                {
                    return false;
                }
                Words? failed = await RestartServicesAsync(run, services, work, build: true);
                parts.Say(failed ?? LaunchWords.RestartedToApply(run.Name, string.Join(", ", names)));
                return failed is null;
            }
            finally
            {
                LetGoBuilt(again.Select(a => a.Service));
            }
        }
    }
}
