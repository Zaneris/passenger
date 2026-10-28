using System.Globalization;
using System.Text.Json;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>An agent's <c>resource</c> names a service of a run of services, an Aspire AppHost's or the container run's (a
/// compose service): run, debug_start, debug_attach, profile and stop act on a compose service through the run of services
/// as the Containers window's rows do, as on an Aspire resource as the Aspire window's do; with no run under way that has it,
/// run, debug_start and profile start the configuration that names it, that service alone, as the gutter's run icon does.
/// What either kind answers is one shape: how the resource and the rest of its run stand, and its own console.</summary>
public sealed partial class PassengerTools
{
    /// <summary>A compose service an agent's resource names: the container run's under way (<see cref="Start"/> null), or one
    /// the configuration <see cref="Start"/> runs, with whether it runs a .NET project (debugged and profiled; any other only
    /// run or stopped).</summary>
    private sealed record ComposeTarget(string Service, RunConfiguration? Start, bool Dotnet);

    /// <summary>The compose service an agent's resource names, or null for an Aspire resource: the container run's under way
    /// when it has it, else (an owned AppHost having no resource of that name, the chosen configuration no Aspire one) one to
    /// start where <paramref name="starts"/>: of the configuration named, else the chosen one when Compose's model of its files
    /// has it, else the one Compose configuration whose model does, refused naming them when several do. A tool that starts
    /// nothing (attach, stop) is refused saying no run has it.</summary>
    private async Task<ComposeTarget?> ComposeTargetAsync(string resource, string? configuration, bool starts, CancellationToken cancel)
    {
        if (resource.Contains("::", StringComparison.Ordinal))
        {
            return null;
        }
        ProjectSession current = Session;
        RunConfiguration? named = configuration is null ? null
            : current.Configurations.All.FirstOrDefault(c => c.Name.Equals(configuration, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No run configuration '{configuration}'");
        if (named is not null && current.Aspire.IsAspire(named))
        {
            return null;
        }
        if (Builds.Containers is { } run && OfRun(run, resource) is { } running
            && (named is null || run.Name == named.Name || run.Name.StartsWith($"{named.Name}: ", StringComparison.Ordinal)))
        {
            return running;
        }
        bool aspireRuns = current.Aspire.Apps.Any(app => !app.IsTest && !app.Stopping);
        if (named is null && aspireRuns && await window.ResolveOwnedAspireResourceAsync(resource, null, cancel, allowMissing: true) is not null)
        {
            return null;
        }
        RunConfiguration? chosen = named ?? current.Configurations.Chosen;
        if (named is null && chosen is not null && current.Aspire.IsAspire(chosen))
        {
            return null;
        }
        if (!starts)
        {
            return aspireRuns ? null : throw new InvalidOperationException(Builds.Containers is { } up
                ? Builds.ServiceOf(up, resource).WhyNot ?? $"{up.Name} does not start {resource}" : NoRunHas(resource));
        }
        if (named is not null && named.Kind != RunKind.Compose)
        {
            throw new ArgumentException($"{named.Name} runs no compose services: name a Compose configuration");
        }
        List<RunConfiguration> composing = [.. (chosen is { Kind: RunKind.Compose } ? [chosen] : Array.Empty<RunConfiguration>())
            .Concat(named is null ? current.Configurations.All.Where(c => c.Kind == RunKind.Compose && c != chosen) : [])];
        List<ComposeTarget> having = [];
        foreach (RunConfiguration candidate in composing)
        {
            IReadOnlyList<(ComposeService Service, string? Project, bool Built)> services;
            try
            {
                services = await ContainerPlans.ServicesAsync(candidate, current.Projects, window.Engine, cancel);
            }
            catch (ContainerCommandException unread) when (candidate != named)
            {
                // A configuration whose files Compose cannot read runs none of them: the others may.
                IdeLog.Write(IdeLog.Ide, $"An agent's {resource}: {candidate.Name}'s compose files did not read ({unread.Message})");
                continue;
            }
            if (Session != current || current.IsEnded)
            {
                throw new InvalidOperationException("The workspace changed");
            }
            if (services.FirstOrDefault(s => s.Service.Name == resource) is { Service: not null } found)
            {
                having.Add(new ComposeTarget(resource, candidate, found.Project is not null));
                if (candidate == chosen)
                {
                    break;
                }
            }
        }
        return having switch
        {
            [{ } only] => only,
            [] when named is not null => throw new ArgumentException($"{named.Name}'s compose files have no service {resource}"),
            [] => throw new InvalidOperationException($"{NoRunHas(resource)}, and no Compose configuration's files have a service of that name"),
            _ => throw new InvalidOperationException($"{resource} is a service of {having.Count} configurations: name one by configuration ("
                + string.Join(", ", having.Select(h => h.Start!.Name)) + ")"),
        };
    }

    /// <summary>A service the container run under way brings up, by its name: a .NET service it starts from Passenger's build,
    /// one it left unstarted (marked Don't start), which run, debug_start and profile start later from that build, or one
    /// Compose starts as its files say (a backing service).</summary>
    private static ComposeTarget? OfRun(ContainerRun run, string name) =>
        run.ServiceNamed(name) is { } dotnet && run.LeftUnstarted(dotnet) ? new ComposeTarget(name, null, true)
        : run.Plan.Touched.Count > 0 && !run.Plan.Touched.Contains(name) ? null
            : run.Plan.Dotnet.Any(d => d.Service == name) ? new ComposeTarget(name, null, run.Plan.Starts(name))
            : run.Plan.Backing.Contains(name) ? new ComposeTarget(name, null, false)
            : null;

    private static string NoRunHas(string resource) => $"No run under way has {resource} (workspace lists them)";

    /// <summary>One of a compose service's actions through the run of services, refused saying why.</summary>
    private async Task ComposeServiceActionAsync(string action, string service)
    {
        if (await window.ServiceActionAsync(action, service) is { } refused)
        {
            throw new InvalidOperationException(refused);
        }
    }

    /// <summary>The configuration that runs a compose service started for it, that service alone with what it depends on, as
    /// the gutter's run icon starts one (<see cref="MainWindow.RunTarget"/>): run, debugged (the service alone) or profiled.
    /// Null once it is up, else why not; given up on by the agent before it is, it comes down as Stop takes it.</summary>
    private Task<string?> StartComposeServiceAsync(ComposeTarget target, RunStart how, CancellationToken cancel)
    {
        if (how != RunStart.Run)
        {
            Debuggable(target, how);
        }
        return Started(window.RunTarget(new RunTarget(0, target.Start!, target.Service), how),
            how switch { RunStart.Debug => AppActions.Debug, RunStart.Profile => AppActions.Profile, _ => AppActions.RunProgram }, cancel);
    }

    /// <summary>Refuses a compose service the debugger or the profiler cannot reach: one its run does not start from
    /// Passenger's build (a backing service, a .NET one only depended on).</summary>
    private void Debuggable(ComposeTarget target, RunStart how)
    {
        if (!target.Dotnet)
        {
            throw new InvalidOperationException($"{target.Service} is no .NET service {target.Start?.Name ?? Builds.Containers?.Name} starts from Passenger's build, "
                + $"which {(how == RunStart.Profile ? "profile" : "the debugger")} needs: run starts it");
        }
    }

    /// <summary>run's compose service: the configuration that runs it started for it, or, in the run under way, a .NET
    /// service's session let go (Run without Debugger); answered as an Aspire resource's run is, once it is up or has ended, or
    /// waitSeconds pass.</summary>
    private async Task<RunResult> RunComposeServiceAsync(ComposeTarget target, int waitSeconds, CancellationToken cancel)
    {
        AgentCall.Follow(window.Actions.Work, AppActions.ServiceRun, AppActions.RunProgram, AppActions.Build);
        using (LaunchBuild built = new(Builds))
        {
            if (target.Start is not null && await StartComposeServiceAsync(target, RunStart.Run, cancel) is { } refused)
            {
                throw new InvalidOperationException(WithBuildErrors(refused, built.Ended));
            }
        }
        if (target.Start is null && target.Dotnet)
        {
            await ComposeServiceActionAsync(AppActions.ServiceRun, target.Service);
        }
        return await ComposeServiceUpAsync(target.Service, waitSeconds, cancel);
    }

    /// <summary>debug_start's and debug_attach's compose service: debugged from its start (restarted held, or the configuration
    /// that runs it started with it held), or attached to as it runs, through the run of services; where it is once it stops,
    /// ends, or waitSeconds pass.</summary>
    private async Task<DebugSnapshot> DebugComposeServiceAsync(ComposeTarget target, bool attach, int waitSeconds, CancellationToken cancel)
    {
        string action = target.Start is not null ? AppActions.Debug : attach ? AppActions.ServiceAttach : AppActions.ServiceDebug;
        AgentCall.Follow(window.Actions.Work, action, AppActions.Build);
        if (target.Start is not null)
        {
            using LaunchBuild built = new(Builds);
            if (await StartComposeServiceAsync(target, RunStart.Debug, cancel) is { } refused)
            {
                throw new InvalidOperationException(WithBuildErrors(refused, built.Ended));
            }
        }
        else
        {
            Debuggable(target, RunStart.Debug);
            await ComposeServiceActionAsync(action, target.Service);
        }
        DebugSession debugged = Debugging(target.Service) ?? throw new InvalidOperationException($"{target.Service} has no debugging session");
        return await AfterResume(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 120)), cancel, debugged);
    }

    /// <summary>What an agent's run of a compose service answers, as an Aspire resource's does (<see cref="ResourceAnswer"/>):
    /// once it is up or has ended, or waitSeconds have passed, the engine's word on its project's containers read again as
    /// they change, how it and the run's other services stand, and its own log's last lines.</summary>
    private async Task<RunResult> ComposeServiceUpAsync(string service, int waitSeconds, CancellationToken cancel)
    {
        ContainerRun run = Builds.Containers ?? throw new InvalidOperationException($"The container run that runs {service} has ended");
        AgentCall.Say($"Waiting up to {Math.Max(0, waitSeconds)} s for {service} to be up");
        IReadOnlyList<ServiceState> states = await ComposeStatesAsync(run, now => now.FirstOrDefault(s => s.Service == service) is { } it
            && (Standing.Of(it).IsUp || Standing.Of(it).HasEnded), TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), cancel);
        string output = await ComposeLogAsync(run, service, 200, cancel);
        if (Builds.Containers != run)
        {
            throw new InvalidOperationException($"The container run that runs {service} has ended");
        }
        return ResourceAnswer(service, run.Name, "Services", states.FirstOrDefault(s => s.Service == service) is { } own ? Standing.Of(own) : null,
            [.. ServiceStandings(run, states)], run.Notes, output, waitSeconds);
    }

    /// <summary>The container run's services as the engine says they stand once <paramref name="done"/> holds or the time is
    /// up, asked again whenever the engine says a container of its project changed (and every 2 s besides).</summary>
    private async Task<IReadOnlyList<ServiceState>> ComposeStatesAsync(ContainerRun run, Func<IReadOnlyList<ServiceState>, bool> done, TimeSpan limit,
        CancellationToken cancel)
    {
        DateTime until = DateTime.UtcNow + limit;
        using SemaphoreSlim changed = new(0, 1);
        using IDisposable following = run.Engine.Follow(seen =>
        {
            if (seen.Attributes.TryGetValue("com.docker.compose.project", out string? project) && project == run.Plan.ProjectName)
            {
                try
                {
                    changed.Release();
                }
                catch (Exception told) when (told is SemaphoreFullException or ObjectDisposedException)
                {
                    // Told already, or the wait is over.
                }
            }
        });
        while (true)
        {
            IReadOnlyList<ServiceState> states;
            try
            {
                states = await run.StatesAsync(cancel);
            }
            catch (JsonException unread)
            {
                throw new InvalidOperationException($"The container engine's answer could not be read: {unread.Message}", unread);
            }
            TimeSpan left = until - DateTime.UtcNow;
            if (done(states) || left <= TimeSpan.Zero || run.IsStopped)
            {
                return states;
            }
            await changed.WaitAsync(Min(left, TimeSpan.FromSeconds(2)), cancel);
        }
    }

    /// <summary>A compose service's log, its last lines, as <c>compose logs</c> gives them without their prefix.</summary>
    private static async Task<string> ComposeLogAsync(ContainerRun run, string service, int lines, CancellationToken cancel)
    {
        IReadOnlyList<string> words = [.. run.Plan.Compose, "logs", "--no-color", "--no-log-prefix", "--tail",
            Math.Clamp(lines, 1, 10_000).ToString(CultureInfo.InvariantCulture), "--", service];
        // Not held: the agent's answer, which its call waits for; nothing drawn changes.
        ChildResult result = await BackgroundWork.Unheld(() => run.Engine.RunAsync(words, ContainerEngine.AskLimit, cancel), cancel);
        return result.Succeeded ? result.Output.TrimEnd('\n')
            : throw new InvalidOperationException($"{service}'s log could not be read: {ContainerEngine.Said(result)}");
    }

    /// <summary>The container run's services, one standing each, in its plan's order: its .NET services (those it does not
    /// start said so) and the others its files run, then any other the engine has of its project.</summary>
    private static IEnumerable<Standing> ServiceStandings(ContainerRun run, IReadOnlyList<ServiceState> states)
    {
        IEnumerable<string> names = run.Plan.Dotnet.Select(d => d.Service).Concat(run.Plan.Backing).Concat(states.Select(s => s.Service)).Distinct(StringComparer.Ordinal);
        foreach (string name in names)
        {
            DotnetService? dotnet = run.Plan.Dotnet.FirstOrDefault(d => d.Service == name);
            Standing standing = states.FirstOrDefault(s => s.Service == name) is { } state ? Standing.Of(state)
                : new Standing(name, dotnet is not null && !run.Plan.Starts(name) ? "Not started" : "Not created", null, null);
            yield return standing with { Detail = dotnet is null ? standing.Detail : $"{standing.Detail}, from Passenger's build" };
        }
    }

    /// <summary>What an agent's workspace says of the container run under way: its configuration, Compose project, how it was
    /// started and its state, and each service as an AppHost's resources are said (<see cref="ResourceInfo"/>), its .NET ones
    /// with what reaches them from here; null with none.</summary>
    private async Task<ContainerRunInfo?> ContainerRunInfoAsync(CancellationToken cancel)
    {
        if (Builds.Containers is not { } run)
        {
            return null;
        }
        ProjectSession current = Session;
        string state = current.Runner.IsRunning ? "Running" : "Starting";
        IReadOnlyList<ServiceState> states = [];
        try
        {
            states = await run.StatesAsync(cancel);
        }
        catch (Exception unread) when (unread is ContainerCommandException or TimeoutException or JsonException)
        {
            // The rest of the workspace still answers: this run's state is why its services' cannot be read.
            state = $"The engine did not say how its services stand: {unread.Message}";
        }
        if (Session != current || current.IsEnded || Builds.Containers != run)
        {
            return null;
        }
        return new ContainerRunInfo(run.Name, run.Plan.ProjectName, Words(run.How), state, [.. ServiceStandings(run, states).Select(standing =>
        {
            IRunService? dotnet = run.ServiceNamed(standing.Name);
            ServiceState? engine = states.FirstOrDefault(s => s.Service == standing.Name);
            return Reached(new ResourceInfo(standing.Name, dotnet is null ? "Container" : "Project", standing.State, standing.Health)
            {
                Target = standing.Name,
                Ports = engine?.Ports is { Length: > 0 } ports ? ports : null,
                Container = engine?.Container,
            }, run, dotnet);
        })]) { Notes = run.Notes.Count > 0 ? [.. run.Notes] : null };
    }

    /// <summary>A resource as workspace says it with what reaches its .NET service from here, a run of services' of either
    /// kind: what it is held at its start for, the Debug tab debugging it (the session the debug tools take) and its state, a
    /// quiet session's for Hot Reload, and whether the profiler records it.</summary>
    private ResourceInfo Reached(ResourceInfo info, ServiceRun run, IRunService? service)
    {
        if (service is null)
        {
            return info;
        }
        string name = run.SessionName(service);
        DebugSession? tabbed = run.Debugged.Contains(name) ? Debugging(name) : null;
        DebugSession? quiet = Session.Debug.Running.FirstOrDefault(r => r.Name == name)?.Session;
        DebugSession? debugged = tabbed is { State: not DebugState.Ended } ? tabbed : quiet is { State: not DebugState.Ended } ? quiet : null;
        return info with
        {
            Held = run.HeldFor(service) is { } how ? Words(how) : null,
            Session = debugged is not null && debugged == tabbed ? name : null,
            Debugger = debugged is null ? null : debugged == quiet ? "hotReload" : debugged.State.ToString().ToLowerInvariant(),
            Profiled = run.Profiled is { } profiled && run.KeyOf(profiled) == run.KeyOf(service) ? true : null,
        };
    }

    /// <summary>The session of a Debug tab of that name, its live one before one an earlier run of it left ended.</summary>
    private DebugSession? Debugging(string tab) =>
        Session.Debug.Tabs.LastOrDefault(t => t.Name == tab && t.IsLive)?.Session ?? Session.Debug.SessionNamed(tab);

    /// <summary>How a run of services was started, or a service is held, in an agent's words: run, debug, hotReload, profile.</summary>
    private static string Words(RunStart how) => JsonNamingPolicy.CamelCase.ConvertName(how.ToString());

    /// <summary>What an agent's run of a resource of a run of services answers, an AppHost's and a container run's alike:
    /// whether it is up (running, and not unhealthy where it reports its health), ended, or neither within the wait, every
    /// resource of its run one line each, the run's notes, and the resource's own console's last lines.</summary>
    private static RunResult ResourceAnswer(string name, string run, string listed, Standing? resource, IReadOnlyList<Standing> all,
        IEnumerable<string> notes, string output, int waitSeconds, string? unstarted = null)
    {
        string status = resource is null ? $"{name} is no longer one of {run}'s {listed.ToLowerInvariant()}"
            : unstarted is not null ? $"{name} of {run}: {unstarted}"
            : resource.IsUp ? $"{name} of {run} is up"
            : resource.HasEnded ? $"{name} of {run} ended: {resource.State}{(resource.ExitCode is { } code ? $" (exit code {code})" : string.Empty)}"
            : $"{name} of {run} is not up within {Math.Max(0, waitSeconds)} s: {resource.State ?? "Unknown"}{(resource.Health is { } said ? $" ({said})" : string.Empty)}";
        status += $"\n{listed} of {run}:\n{string.Join('\n', all.Select(s => $"  {s}"))}" + string.Concat(notes.Select(note => $"\nNote: {note}"));
        return new RunResult(resource is not null && !resource.HasEnded, resource is { HasEnded: true } ? resource.ExitCode : null, status, output);
    }

    /// <summary>A resource of a run of services as its run's answer says it: its state (<see cref="Shown"/> where the window
    /// words it otherwise than its kind does), health and exit code, and what follows them on its line.</summary>
    private sealed record Standing(string Name, string? State, string? Health, int? ExitCode)
    {
        public string? Shown { get; init; }

        public string Detail { get; init; } = string.Empty;

        /// <summary>Running, and not unhealthy where it reports its health, nor still starting its health check.</summary>
        public bool IsUp => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase)
            && !(Health is { } health && (health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) || health.Equals("Starting", StringComparison.OrdinalIgnoreCase)));

        /// <summary>Exited, finished, failed to start or dead: it runs no more.</summary>
        public bool HasEnded => State is { } state && Ended.Any(ended => ended.Equals(state, StringComparison.OrdinalIgnoreCase));

        private static readonly string[] Ended = ["Exited", "Finished", "FailedToStart", "Dead"];

        public static Standing Of(ServiceState state) => new(state.Service, state.State, state.Health, state.ExitCode)
        {
            Detail = state.Ports.Length > 0 ? $", ports {state.Ports}" : string.Empty,
        };

        public override string ToString() => $"{Name}: {Shown ?? State ?? "Unknown"}"
            + $"{(Health is { } health && string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase) ? $" ({health})" : string.Empty)}{Detail}";
    }
}

/// <summary>The container run under way as an agent's workspace lists it, beside the AppHosts: its configuration, Compose
/// project, how it was started (run, debug, hotReload, profile), its state, each service as an AppHost's resource is said,
/// and what the run said of itself as it came up.</summary>
public sealed record ContainerRunInfo(string Configuration, string Project, string How, string State, IReadOnlyList<ResourceInfo> Resources)
{
    public IReadOnlyList<string>? Notes { get; init; }
}
