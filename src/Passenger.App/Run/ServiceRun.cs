using Passenger.App.Containers;
using Passenger.App.Debugging;

namespace Passenger.App.Run;

/// <summary>How a run of services is started, a container configuration's and an Aspire AppHost's alike: run; debugged, its
/// .NET services (those marked Debug, else every one) held at their start until a session per service attaches; run with
/// Hot Reload, every .NET service held and attached to quietly, no breakpoint sent, so the edits reach it through the
/// debugger (dotnet watch would need the SDK in a container's image); or profiled, one .NET service held at its start until
/// the profiler records it. What a service is held at its start for is one of these too (<see cref="ServiceRun.HeldFor"/>).</summary>
public enum RunStart
{
    Run,
    Debug,
    HotReload,
    Profile,
}

/// <summary>A .NET service of a run of services (<see cref="ServiceRun"/>): its name in the run, the project it runs, and
/// whether the run starts it.</summary>
public interface IRunService
{
    string Name { get; }

    string Project { get; }

    bool Starts { get; }
}

/// <summary>A service's program as the debugger and the profiler reach it where its run's kind runs it: its id there, the
/// adapter's place (null on this machine), where the profiler records it (null on this machine), why its runtime refuses
/// a debugger (<see cref="WhyNot"/>), what is worth saying of it (<see cref="Notes"/>: a Release build, emulation), what a
/// refusal adds (<see cref="Caveat"/>) and whether it was started open to Hot Reload.</summary>
public sealed record ServiceProgram(int Id, AdapterPlace? Place, Profiler.ProfilePlace? Recorded)
{
    public string? WhyNot { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public string? Caveat { get; init; }

    public bool HotReload { get; init; }

    /// <summary>Its inspected app (an Avalonia service's, its inspector started with it), which its Debug tab holds.</summary>
    public LiveTree.LiveApp? App { get; init; }
}

/// <summary>Why a service's program could not be reached where its run's kind runs it (its container, its tools, its
/// process), said as the service's action's failure.</summary>
public sealed class ServiceUnreachedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A .NET service of a run named where it is written (a compose file's service, an AppHost's <c>AddProject</c>,
/// whose gutter icon names it), which the service actions act on: its run and its name there, whether it runs now or not.</summary>
public sealed record ChosenService(ServiceRun Run, string Name);

/// <summary>A run of several .NET services, the one model of every kind of it (a container run, an Aspire AppHost's run):
/// it lists its .NET services, and keeps for each what it is held at its start for (the debugger, quietly for Hot Reload;
/// none: unheld), the sessions that reach it from here, whether it was attached to as it ran, and the one the profiler
/// records, which <see cref="BuildRunController"/>'s service actions and Stop's order work on, written once. A kind
/// supplies only what is its own: how its services restart (<see cref="RestartAsync"/>), how a hold chosen takes effect
/// at their next start (<see cref="HoldsChangedAsync"/>) and where a service's program is (<see cref="ProgramAsync"/>). A
/// UI-thread object of the session's.</summary>
public abstract class ServiceRun
{
    /// <summary>What each .NET service is held at its start for, as last chosen (debugged, or attached to quietly for Hot
    /// Reload): a restart holds it again and attaches again. A service not here runs unheld.</summary>
    private readonly Dictionary<string, RunStart> holds = new(StringComparer.Ordinal);

    /// <summary>The run's name, its configuration's, by which its Debug tabs are grouped and the person's choices kept.</summary>
    public abstract string Name { get; }

    /// <summary>Its .NET services.</summary>
    public abstract IReadOnlyList<IRunService> Services { get; }

    /// <summary>Whether it was taken down (or is being).</summary>
    public abstract bool IsStopped { get; }

    /// <summary>How it was started: run, debugged, run with Hot Reload through the debugger, or profiled.</summary>
    public RunStart How { get; init; }

    /// <summary>The Debug tabs of the services debugged (or attached to for Hot Reload), by their names: Stop ends them before
    /// it takes their programs.</summary>
    public HashSet<string> Debugged { get; } = new(StringComparer.Ordinal);

    /// <summary>The services attached to as they ran (Attach, not held at their start), which a restart attaches to again.</summary>
    public HashSet<string> AttachedRunning { get; } = new(StringComparer.Ordinal);

    /// <summary>A service as the profiler window and the status bar name what is recorded: by the run too.</summary>
    public virtual string NameOf(IRunService service) => $"{service.Name} of {Name}";

    /// <summary>The service the profiler records, which Stop ends (<see cref="EndProfiling"/>, its snapshot read) before it
    /// takes the programs; null when none is.</summary>
    public string? Profiled { get; set; }

    /// <summary>Ends the profiler's recording of <see cref="Profiled"/>, reading its snapshot, done once it has.</summary>
    public Func<Task>? EndProfiling { get; set; }

    /// <summary>What the run said of itself as it came up (its notes: what serves HTTP, a service that runs without the
    /// debugger), which an agent's answer carries too.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>The services that what came to attach to them could not reach as they last started, each with why (its
    /// program's <see cref="ServiceProgram.WhyNot"/>): they run on without it.</summary>
    public Dictionary<string, string> Unreached { get; } = new(StringComparer.Ordinal);

    /// <summary>Where its services' logs and traces are read (an AppHost's dashboard, a container run's own), null while it
    /// has none.</summary>
    public virtual Telemetry.TelemetrySource? Telemetry => null;

    /// <summary>The .NET services held at their start for the debugger, which a restart holds again.</summary>
    public IReadOnlyList<IRunService> Held => [.. Services.Where(s => holds.ContainsKey(s.Name))];

    /// <summary>The names of the services held at their start, whatever for.</summary>
    protected IReadOnlyCollection<string> HeldNames => holds.Keys;

    /// <summary>What a service is held at its start for (Debug or Hot Reload), or null when it runs unheld.</summary>
    public RunStart? HeldFor(string service) => holds.TryGetValue(service, out RunStart how) ? how : null;

    /// <summary>The services as they were brought up: those held for the debugger (or the one for the profiler, whose hold its
    /// recording lets go, never asked again).</summary>
    public void Brought(IReadOnlyList<(IRunService Service, RunStart How)> held)
    {
        foreach ((IRunService service, RunStart how) in held)
        {
            if (how != RunStart.Profile)
            {
                holds[service.Name] = how;
            }
            MadeHeld(service.Name);
        }
    }

    /// <summary>A service brought up held at its start, which its kind may keep account of.</summary>
    protected virtual void MadeHeld(string service)
    {
    }

    /// <summary>Chooses what a service is held at its start for from now (null: unheld), which its next start makes so.</summary>
    public Task HoldAsync(string service, RunStart? how, CancellationToken cancel)
    {
        if (how is { } held)
        {
            holds[service] = held;
        }
        else
        {
            holds.Remove(service);
        }
        return HoldsChangedAsync(cancel);
    }

    /// <summary>The service of this run by its name, or null.</summary>
    public virtual IRunService? ServiceNamed(string name) => Services.FirstOrDefault(s => s.Name == name);

    /// <summary>Whether the run left a service unstarted, as its configuration says Don't start: its Debug, Run and Profile
    /// start it later (<see cref="StartLater"/>), held as chosen, and Attach has nothing to reach. A kind whose unstarted
    /// services are started otherwise (an Aspire AppHost's, by its resource's Start) leaves none so.</summary>
    public virtual bool LeftUnstarted(IRunService service) => false;

    /// <summary>Whether the run left the service of that name unstarted (<see cref="LeftUnstarted(IRunService)"/>), named
    /// where its kind has no service for it to act on yet.</summary>
    public virtual bool LeftUnstarted(string name) => ServiceNamed(name) is { } service && LeftUnstarted(service);

    /// <summary>What starts a service the run left unstarted, as its kind starts one.</summary>
    public abstract string StartsIt { get; }

    /// <summary>How a service the run left unstarted is said where an action has nothing to reach in it, every kind's words.</summary>
    public string WhyNotStarted(string name) => Refusals.NotStarted(ServiceNamed(name) is { } service ? NameOf(service) : $"{name} of {Name}", StartsIt);

    /// <summary>Why one of a service's actions (<see cref="AppActions.ServiceDebug"/> and the rest) cannot act on a service of
    /// the run, or null: Attach has nothing to reach in one the run left unstarted.</summary>
    public virtual string? WhyNot(string action, IRunService service) => action == AppActions.ServiceAttach && LeftUnstarted(service) ? WhyNotStarted(service.Name) : null;

    /// <summary>Why a service the run left unstarted cannot be started later where its kind runs it, or null when it can.</summary>
    public virtual string? WhyNotStartedLater(IRunService service) => null;

    /// <summary>Makes a service the run left unstarted one it starts from now, its next restart starting it (held once for the
    /// profiler, when <paramref name="profiled"/>, besides the hold chosen), and Stop, Rerun and its restarts taking it as any
    /// other of the run's: null once so, else why not (its build not there to run).</summary>
    public virtual string? StartLater(IRunService service, bool profiled) => $"{Name} does not start {service.Name}";

    /// <summary>Ends what reaches services of the run from here (every one, given none): their debugging sessions (in a tab
    /// or for Hot Reload) and the profiler's recording, its snapshot read; done once they have. Stop's order for every stop of
    /// a service or a run: these end before the programs do, so no debugger loses its program under it and the profiler's
    /// trace keeps its rundown. As the folder ends (<paramref name="closing"/>) each session is closed, needing no UI thread
    /// once begun, and the recording is left to the folder's profiler.</summary>
    public Task EndSessionsAsync(DebugController debug, IEnumerable<IRunService>? services = null, bool closing = false)
    {
        List<IRunService>? given = services?.ToList();
        IReadOnlyList<IRunService> each = given ?? Services;
        HashSet<string> named = [.. each.Select(SessionName)];
        // The whole run's: a session left of a service no longer among its services ends too.
        List<Task> ending = [.. each.Select(s => EndSessionAsync(debug, s, closing)),
            .. given is null && !closing ? Debugged.Where(name => !named.Contains(name)).Select(debug.StopAsync) : []];
        if (!closing && (given is null || Profiled is { } profiled && given.Any(s => s.Name == profiled)) && EndProfiling is { } end)
        {
            ending.Add(EndRecordingAsync(end));
        }
        return Task.WhenAll(ending);
    }

    /// <summary>Ends one service's session in Stop's order: by its name (its Debug tab's), unless its kind keeps each start's
    /// own (an Aspire service, a later start of which has its own of the same name).</summary>
    protected virtual Task EndSessionAsync(DebugController debug, IRunService service, bool closing) =>
        Debugged.Contains(SessionName(service)) ? debug.StopAsync(SessionName(service)) : Task.CompletedTask;

    /// <summary>The recording ended and its snapshot read, which says itself what came of it: one that could not end (its
    /// program gone, the folder left as it ended) is logged, never Passenger's error, and never holds up Stop's order.</summary>
    private static async Task EndRecordingAsync(Func<Task> end)
    {
        try
        {
            await end();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception unended) when (unended is IOException or InvalidOperationException or TimeoutException or Profiler.ProfilingException)
        {
            IdeLog.Write(IdeLog.Ide, $"The profiler's recording of a service did not end cleanly: {unended.Message}");
        }
    }

    /// <summary>The Debug tabs' group of the run's sessions, which Hot Reload's changes reach together: its name, unless its
    /// kind groups otherwise (an Aspire configuration's AppHosts, linked, are one group).</summary>
    public virtual string Group => Name;

    /// <summary>The name of a service's debugging session, its Debug tab's (<see cref="Debugged"/> holds these): the
    /// service's own, unless its kind names it otherwise (an Aspire service by its AppHost too, as two AppHosts' may share a
    /// resource's name).</summary>
    public virtual string SessionName(IRunService service) => service.Name;

    /// <summary>Whether each start of a held service is attached to by its kind as it starts, once (an Aspire service, whose
    /// start DCP asks for): a restart waits for that attach rather than attaching again.</summary>
    public virtual bool AttachesItsStarts => false;

    /// <summary>Whether Run without Debugger restarts the service unheld (an Aspire service, its program this machine's,
    /// back in moments), rather than letting its session go with its program running on (a container's).</summary>
    public virtual bool RestartsToRunWithoutDebugger => false;

    /// <summary>Told once a session attached to a service's program (its tab, null for a quiet one, and its session), which its kind may
    /// keep (an Aspire service's tab, which the Aspire window chooses).</summary>
    public virtual void Attached(IRunService service, ServiceProgram program, ToolWindows.DebugTab? tab, DebugSession? debugged)
    {
    }

    /// <summary>Says a line of the IDE's about a service (what a Hot Reload apply to it came to) where its output is shown.</summary>
    public abstract void Say(IRunService service, string line, bool error);

    /// <summary>Makes the holds chosen take effect at each service's next start (a container run's override written again).</summary>
    protected abstract Task HoldsChangedAsync(CancellationToken cancel);

    /// <summary>Restarts services (<paramref name="why"/> said in the run's console), each held at its start as now chosen,
    /// their projects built again first where <paramref name="build"/> asks it and the kind builds between their stop and
    /// their start (an Aspire service, whose files a build cannot write while it runs on Windows): null once done, else why
    /// not. The run's Stop ends it.</summary>
    public abstract Task<string?> RestartAsync(IReadOnlyList<IRunService> services, CancellationToken cancel, string? why = null, bool build = false);

    /// <summary>A service's program once its run has started it (null when the run ended without it), for the debugger or
    /// the profiler to reach; <paramref name="up"/> is the run's start under way, done once it has ended. Throws a
    /// <see cref="ServiceUnreachedException"/> saying why it could not be reached.</summary>
    public abstract Task<ServiceProgram?> ProgramAsync(IRunService service, Task up, CancellationToken stop);

    /// <summary>Where a started service answers HTTP from this machine, as its kind publishes it; null when it serves at none.
    /// Throws as <see cref="Refused"/> says when its kind does not answer.</summary>
    public abstract Task<string?> AddressAsync(IRunService service, CancellationToken cancel);

    /// <summary>Whether its services' projects are built here before an edit Hot Reload cannot apply restarts them (their
    /// program reads the build here), rather than by their restart.</summary>
    public abstract bool BuildsBeforeRestart { get; }

    /// <summary>Whether a failure of a service's action is one its kind says (an engine's refusal, a file it could not
    /// write), said as the action's failure rather than reported as Passenger's.</summary>
    public abstract bool Refused(Exception failure);
}
