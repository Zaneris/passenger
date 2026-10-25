using Passenger.App.Containers;
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
    private async Task<string?> AttachAllAsync(ServiceRun run, IReadOnlyList<IRunService> services, Task up, List<string> notes, Working work, CancellationToken stop,
        RunStart? purpose = null) =>
        (await AttachEachAsync(run, services, up, notes, work, stop, purpose)).Select(a => a.Refused).FirstOrDefault(s => s is not null);

    /// <summary>Attaches to each service held at its start, all at once: each with why it was not attached, or null.</summary>
    private Task<(IRunService Service, string? Refused)[]> AttachEachAsync(ServiceRun run, IReadOnlyList<IRunService> services, Task up, List<string> notes, Working work,
        CancellationToken stop, RunStart? purpose = null)
    {
        string named = string.Join(", ", services.Select(s => s.Name));
        work.Say(purpose == RunStart.Profile ? $"Attaching the profiler to {named}…" : services.All(s => run.HeldFor(s.Name) == RunStart.HotReload) ? "Attaching for Hot Reload…"
            : $"Attaching the debugger to {named}…");
        return Task.WhenAll(services.Select(async s => (s, await AttachAsync(run, s, up, notes, stop, purpose))));
    }

    /// <summary>A service's start held for the debugger (quietly for Hot Reload), attached to as its kind starts it, once:
    /// an Aspire service's, which DCP asks for (<see cref="ServiceRun.AttachesItsStarts"/>). Null once attached, else why
    /// not, which its run's console says.</summary>
    public Task<string?> AttachStartAsync(ServiceRun run, IRunService service, RunStart purpose, List<string> notes, CancellationToken stop) =>
        AttachAsync(run, service, Task.CompletedTask, notes, stop, purpose);

    /// <summary>One service, held at its start or running: its program, once its run has started it, where its kind runs it
    /// (<see cref="ServiceRun.ProgramAsync"/>), and a session attached to it, which lets it go: one with its tab in the Debug
    /// window, or, under Run with Hot Reload, a quiet one; or the profiler recording it. Null once attached, else why not.</summary>
    private async Task<string?> AttachAsync(ServiceRun run, IRunService service, Task up, List<string> notes, CancellationToken stop, RunStart? given = null,
        bool asItRuns = false)
    {
        UiThread.Check();
        // Held for the debugger (in a tab, or quietly for Hot Reload), held for the profiler, or attached to as it runs.
        RunStart purpose = given ?? run.HeldFor(service.Name) ?? RunStart.Debug;
        ServiceProgram? found;
        try
        {
            found = await run.ProgramAsync(service, up, stop);
        }
        catch (ServiceUnreachedException failed)
        {
            return $"Could not {(purpose == RunStart.Profile ? "profile" : "debug")} {service.Name}: {failed.Message}";
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
            string without = $"{service.Name} runs without the {(purpose == RunStart.Profile ? "profiler" : "debugger")}: {why}";
            if (asItRuns && purpose == RunStart.Profile)
            {
                return without;
            }
            notes.Add(without);
            return null;
        }
        run.Unreached.Remove(service.Name);
        foreach (string note in program.Notes)
        {
            notes.Add($"{service.Name}: {note}");
        }
        string? refused;
        string name = run.SessionName(service);
        if (purpose == RunStart.Profile)
        {
            // The one recording of a service of either kind, held or running, which sets what Stop reads first.
            refused = parts.ProfileService is { } profile ? await profile(run, service, program, asItRuns, stop) : "The profiler is not available";
        }
        else
        {
            run.Debugged.Add(name);
            refused = purpose == RunStart.HotReload
                ? await session.Debug.AttachForHotReloadAsync(program.Id, name, (line, error) => run.Say(service, line, error), program.Place, stop)
                : await session.Debug.AttachAsync(program.Id, name, hotReload: program.HotReload, place: program.Place, group: run.Group, app: program.App);
            if (refused is null)
            {
                DebugTab? tab = purpose == RunStart.HotReload ? null : session.Debug.Tabs.LastOrDefault(t => t.Name == name && t.IsLive);
                run.Attached(service, program, tab, tab?.Session ?? session.Debug.Running.LastOrDefault(r => r.Name == name)?.Session);
            }
        }
        return refused is null ? null : program.Caveat is { } caveat ? $"{refused} ({caveat})" : refused;
    }

    /// <summary>Restarts services of a run (their sessions and recording ended first, so no debugger loses its program under
    /// it and the profiler's trace keeps its rundown), their projects built again between their stop and start where
    /// <paramref name="build"/> asks it of a kind that builds then, and attaches again to those held for the debugger and those
    /// attached to as they ran. Null once done, else why not: a failure its kind says (a command refused, a time limit) is
    /// said as the restart's refusal, whoever asked.</summary>
    private async Task<string?> RestartServicesAsync(ServiceRun run, IReadOnlyList<IRunService> services, Working work, string? why = null, bool build = false)
    {
        List<IRunService> held = run.AttachesItsStarts ? [] : [.. run.Held.Where(h => services.Any(s => s.Name == h.Name))];
        List<IRunService> running = [.. services.Where(s => run.AttachedRunning.Contains(s.Name) && run.HeldFor(s.Name) is null)];
        await EndSessionsAsync(run, services);
        try
        {
            if (await run.RestartAsync(services, work.Stopping, why, build) is { } failed)
            {
                return failed;
            }
            List<string> notes = [];
            (IRunService Service, string? Refused)[] attached = held.Count == 0 ? [] : await AttachEachAsync(run, held, Task.CompletedTask, notes, work, work.Stopping);
            string? unattached = attached.Select(a => a.Refused).FirstOrDefault(r => r is not null);
            foreach (IRunService again in running)
            {
                unattached ??= await AttachAsync(run, again, Task.CompletedTask, notes, work.Stopping, RunStart.Debug);
            }
            // Restarted held, a service no debugger reached would wait for good: it is made again unheld, its hold let go.
            if (attached.Where(a => a.Refused is not null).Select(a => a.Service).ToList() is { Count: > 0 } stuck)
            {
                foreach (IRunService service in stuck)
                {
                    await run.HoldAsync(service.Name, null, CancellationToken.None);
                }
                string names = string.Join(", ", stuck.Select(s => s.Name));
                string? unheld = await run.RestartAsync(stuck, CancellationToken.None, "to run without the debugger, which could not attach");
                return $"{unattached}; {names} {(unheld is null ? "restarted to run without the debugger" : $"could not be restarted to run without it: {unheld}")}";
            }
            return unattached;
        }
        catch (OperationCanceledException)
        {
            return StoppedRestarting(run.Name);
        }
        catch (Exception failed) when (run.Refused(failed))
        {
            return $"{string.Join(", ", services.Select(s => s.Name))} of {run.Name} could not be restarted: {failed.Message}";
        }
    }

    /// <summary>A .NET service of the container run under way, by its name, or why there is none to act on.</summary>
    public (IRunService? Service, string? WhyNot) ServiceOfRun(string? name) => Containers is { } run && IsRunning ? ServiceOf(run, name)
        : (null, NoContainerRun);

    private const string NoContainerRun = "No container run is under way: Run, Debug or Profile a container configuration first";

    /// <summary>A .NET service of a run, by its name, or why there is none to act on.</summary>
    public (IRunService? Service, string? WhyNot) ServiceOf(ServiceRun run, string? name)
    {
        if (run.IsStopped)
        {
            return (null, $"{run.Name} is not running");
        }
        if (name is null || run.ServiceNamed(name) is not { } service)
        {
            return (null, name is null ? $"No .NET service of {run.Name} is chosen" : $"{name} is not a .NET service of {run.Name}");
        }
        // One it left unstarted is acted on too: its Debug, Run and Profile start it later.
        return service.Starts ? (service, null)
            : run.LeftUnstarted(service) ? run.WhyNotStartedLater(service) is { } unfit ? (null, unfit) : (service, null)
            : (null, $"{run.Name} does not start {name}");
    }

    /// <summary>Why one of a service's actions (<see cref="AppActions.ServiceDebug"/> and the rest) cannot act on a .NET service
    /// of a run (the container run under way, when none is given) now, or null: no such service there, or what its run says
    /// (<see cref="ServiceRun.WhyNot"/>).</summary>
    public string? WhyNotOnService(string action, string? name, ServiceRun? run = null) =>
        (run ?? (IsRunning ? Containers : null)) is not { } of ? NoContainerRun
        : ServiceOf(of, name) is ({ } service, null) ? of.WhyNot(action, service)
        : ServiceOf(of, name).WhyNot;

    /// <summary>Whether the container run under way left a .NET service unstarted (marked Don't start), not started since.</summary>
    public bool LeftUnstarted(string name) => Containers is { } run && run.LeftUnstarted(name);

    /// <summary>A .NET service the run left unstarted (Don't start) started later by its action, the rest left as they run:
    /// its project built here first where its program runs that build (fast mode; as deployed its image is built as it
    /// starts), containers of it Passenger did not make asked about first, then started held as chosen (once for the profiler
    /// when <paramref name="profiled"/>) and attached to where held; the run's from then on (Stop takes it down, Rerun keeps
    /// it started). Null once started, else why not.</summary>
    private async Task<string?> StartLaterAsync(ServiceRun run, IRunService service, Working work, string why, bool profiled = false)
    {
        if (run.WhyNotStartedLater(service) is { } unfit)
        {
            return unfit;
        }
        if (run.BuildsBeforeRestart)
        {
            work.Say($"Building {Path.GetFileNameWithoutExtension(service.Project)}…");
            BuildOutcome built = await BuildForAsync([service.Project], work.Stopping);
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
        work.Say($"Starting {service.Name}…");
        string? failed = await RestartServicesAsync(run, [service], work, why);
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
        return $"{unrecorded}; {service.Name} {(unheld is null ? "restarted to run without the profiler" : $"could not be restarted to run without it: {unheld}")}";
    }

    /// <summary>Debug of one .NET service of the container run under way (<see cref="DebugServiceAsync(ServiceRun, string)"/>).</summary>
    public Task<string?> DebugServiceAsync(string name) => DebugServiceAsync(Containers, name);

    /// <summary>Debug of one .NET service of a run, the others left as they run: its session ended, its program made again
    /// held at its start, and a session attached in a Debug tab of its own, so its startup stops at its breakpoints; kept for
    /// the run's restarts and a Rerun until chosen otherwise. One the run left unstarted is started now, held. Null once
    /// attached, else why not.</summary>
    public Task<string?> DebugServiceAsync(ServiceRun? run, string name) => OnServiceAsync(run, name, AppActions.ServiceDebug, $"Restarting {name} to debug it…", async (run, service, work) =>
    {
        Choose(run, name, debug: true);
        run.AttachedRunning.Remove(name);
        await run.HoldAsync(name, RunStart.Debug, work.Stopping);
        parts.Dock.Show(ToolWindowIds.Debug);
        parts.Views.Debug.ShowTab(console: true);
        // One the run left unstarted is started now, held.
        return run.LeftUnstarted(service)
            ? Done(await StartLaterAsync(run, service, work, "to debug it from its start"), $"Debugging {name} of {run.Name} from its start, started now; the rest run on as they were")
            : Done(await RestartServicesAsync(run, [service], work, "to debug it from its start"), $"Debugging {name} of {run.Name} from its start; the rest run on as they were");
    });

    /// <summary>Attach to one .NET service of the container run under way (<see cref="AttachServiceAsync(ServiceRun, string)"/>).</summary>
    public Task<string?> AttachServiceAsync(string name) => AttachServiceAsync(Containers, name);

    /// <summary>Attach to one .NET service of a run as it runs, no restart: its startup has run already, which is said
    /// (Debug restarts it held to stop there). A restart attaches to it again. One the run left unstarted has no program to
    /// reach, which is said. Null once attached, else why not.</summary>
    public Task<string?> AttachServiceAsync(ServiceRun? run, string name) => OnServiceAsync(run, name, AppActions.ServiceAttach, $"Attaching the debugger to {name}…", async (run, service, work) =>
    {
        if (run.WhyNot(AppActions.ServiceAttach, service) is { } unreached)
        {
            return Said(unreached);
        }
        await EndSessionsAsync(run, [service]);
        run.AttachedRunning.Add(name);
        parts.Dock.Show(ToolWindowIds.Debug);
        List<string> notes = [];
        string? refused = await AttachAsync(run, service, Task.CompletedTask, notes, work.Stopping, RunStart.Debug);
        if (refused is not null || session.Debug.SessionNamed(run.SessionName(service)) is not { State: not Debugging.DebugState.Ended })
        {
            run.AttachedRunning.Remove(name);
            return Said(refused ?? (notes.Count > 0 ? string.Join("; ", notes) : $"{name} could not be attached to"));
        }
        return Done(null, $"Debugging {name} of {run.Name} as it runs: its startup has run already, which Debug catches by restarting it held"
            + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty));
    });

    /// <summary>Run without debugger of one .NET service of the container run under way (<see cref="RunServiceAsync(ServiceRun, string)"/>).</summary>
    public Task<string?> RunServiceAsync(string name) => RunServiceAsync(Containers, name);

    /// <summary>Run without debugger of one .NET service of a run, kept as Debug's choice is: its session let go (detached,
    /// not restarted), the program running on as it is, held at its start, its next restart making it anew unheld; or, where
    /// its kind restarts it so (<see cref="ServiceRun.RestartsToRunWithoutDebugger"/>), restarted unheld at once, only when it
    /// is held or debugged. One running unheld with nothing debugging it is left as it is, said; one the run left unstarted
    /// is started now, unheld. Null once done, else why not.</summary>
    public Task<string?> RunServiceAsync(ServiceRun? run, string name) => OnServiceAsync(run, name, AppActions.ServiceRun,
        $"Running {name} without the debugger…", async (run, service, work) =>
    {
        bool held = run.HeldFor(name) is not null;
        bool debugged = run.Debugged.Contains(run.SessionName(service)) && session.Debug.SessionNamed(run.SessionName(service)) is { State: not Debugging.DebugState.Ended };
        Choose(run, name, debug: false);
        run.AttachedRunning.Remove(name);
        await run.HoldAsync(name, null, work.Stopping);
        if (run.LeftUnstarted(service))
        {
            return Done(await StartLaterAsync(run, service, work, "to run it"), $"{name} of {run.Name} started, without the debugger; the rest run on as they were");
        }
        if (!held && !debugged)
        {
            return Done(null, $"{name} of {run.Name} already runs without the debugger: nothing restarted");
        }
        if (run.RestartsToRunWithoutDebugger)
        {
            return Done(await RestartServicesAsync(run, [service], work, "to run without the debugger"), $"{name} of {run.Name} restarted without the debugger");
        }
        await session.Debug.DetachAsync(run.SessionName(service));
        return Done(null, $"{name} of {run.Name} runs on without the debugger, not restarted; its next restart starts it unheld");
    });

    /// <summary>Restart of one .NET service of the container run under way (<see cref="RestartServiceAsync(ServiceRun, string)"/>).</summary>
    public Task<string?> RestartServiceAsync(string name) => RestartServiceAsync(Containers, name);

    /// <summary>Restart of one .NET service from its window or an agent, through its run: its session ended first,
    /// restarted (held again where it is debugged), and attached to again; one the run left unstarted started. Null once
    /// done, else why not.</summary>
    public Task<string?> RestartServiceAsync(ServiceRun? run, string name) => OnServiceAsync(run, name, AppActions.ContainerRestart, $"Restarting {name}…", async (run, service, work) =>
        run.LeftUnstarted(service)
            ? Done(await StartLaterAsync(run, service, work, "started from its window"), $"{run.Name}: started {name}")
            : Done(await RestartServicesAsync(run, [service], work, "restarted from its window"), $"{run.Name}: restarted {name}"));

    /// <summary>Profile of one .NET service of a run as it runs: its program where its kind runs it, recorded by the one
    /// recording of a service (<see cref="BuildRunParts.ProfileService"/>), which Stop, a restart and Rerun read first; one
    /// the run left unstarted started now, held for the profiler until it records. Null once it records, else why not (said).</summary>
    public Task<string?> ProfileServiceAsync(ServiceRun? run, string name) => OnServiceAsync(run, name, AppActions.ServiceProfile, $"Attaching the profiler to {name}…",
        async (run, service, work) => await (run.LeftUnstarted(service) ? StartLaterAsync(run, service, work, "to profile it from its start", profiled: true)
            : AttachAsync(run, service, Task.CompletedTask, [], work.Stopping, RunStart.Profile, asItRuns: true)) is { } refused
            ? Said(refused) : null);

    /// <summary>Ends what reaches one .NET service of the container run from here (its session, the profiler's recording),
    /// before something takes its program from under it (the Containers window's Stop or Remove).</summary>
    public Task EndServiceSessionsAsync(string name) =>
        Containers is { } run && run.ServiceNamed(name) is { } service ? EndSessionsAsync(run, [service]) : Task.CompletedTask;

    /// <summary>Ends what reaches services of a run from here, its Stop's order (<see cref="ServiceRun.EndSessionsAsync"/>).</summary>
    private Task EndSessionsAsync(ServiceRun run, IEnumerable<IRunService> services) => run.EndSessionsAsync(session.Debug, services);

    /// <summary>Says how a service's action ended (what failed, else <paramref name="done"/>), and gives back what failed.</summary>
    private string? Done(string? failed, string done)
    {
        parts.Say(failed ?? done);
        return failed;
    }

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
    /// run, the run has no such service, or the action is under way.</summary>
    private async Task<string?> OnServiceAsync(ServiceRun? run, string name, string action, string stage, Func<ServiceRun, IRunService, Working, Task<string?>> act)
    {
        UiThread.Check();
        if (run is null)
        {
            return Said(ServiceOfRun(name).WhyNot!);
        }
        if (ServiceOf(run, name) is not ({ } service, null))
        {
            return Said(ServiceOf(run, name).WhyNot!);
        }
        if (parts.Work.Begin(action, stage) is not { } work)
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
                return Said($"{stage.TrimEnd('…')} was stopped");
            }
            catch (Exception failed) when (run.Refused(failed))
            {
                return Said($"{stage.TrimEnd('…')} failed: {failed.Message}");
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
        if (parts.Work.Begin(action, $"Restarting {string.Join(", ", names)} to apply changes…") is not { } work)
        {
            return false;
        }
        using (work)
        {
            using IDisposable held = BackgroundWork.Scope();
            parts.Editors.SaveAll();
            List<string> projects = [.. services.Select(s => s.Project).Distinct(StringComparer.Ordinal)];
            if (run.BuildsBeforeRestart && !(await BuildForAsync(projects, work.Stopping)).Succeeded)
            {
                return false;
            }
            string? failed = await RestartServicesAsync(run, services, work, build: true);
            parts.Say(failed ?? $"{run.Name}: restarted {string.Join(", ", names)} to apply changes");
            return failed is null;
        }
    }
}
