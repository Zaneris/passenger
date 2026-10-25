using Passenger.Core;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Passenger.App.Aspire;
using Passenger.App.Debugging;
using Passenger.App.Run;

namespace Passenger.App.Mcp;

public sealed partial class PassengerTools
{
    private async Task<(AspireSelection Selection, bool Started)> PrepareAspireResource(string resource, string? configuration,
        RunStart? mode, string? arguments, CancellationToken cancel)
    {
        ProjectSession current = Session;
        RunConfiguration? explicitConfiguration = configuration is null ? null
            : current.Configurations.All.FirstOrDefault(candidate => candidate.Name.Equals(configuration, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No run configuration '{configuration}'");
        if (explicitConfiguration is not null && !current.Aspire.IsAspire(explicitConfiguration))
        {
            throw new ArgumentException("resource requires an Aspire configuration");
        }
        AspireSelection? selected = await window.ResolveOwnedAspireResourceAsync(resource, explicitConfiguration, cancel, allowMissing: mode is not null);
        CheckWorkspace();
        if (selected is not null)
        {
            if (arguments is not null)
            {
                throw new ArgumentException("A running resource uses its AppHost's arguments; omit arguments");
            }
            return (selected, false);
        }
        RunConfiguration chosen = explicitConfiguration ?? current.Configurations.Chosen
            ?? throw new InvalidOperationException("Select or name an Aspire configuration before starting a resource");
        bool started = await window.StartAspireToolResourceAsync(resource, chosen, arguments, mode!.Value, cancel).WaitAsync(cancel);
        CheckWorkspace();
        if (!started)
        {
            throw new InvalidOperationException("The selected AppHost is running but does not expose that resource; workspace lists targets");
        }
        AspireAppRun app = current.Aspire.FindApp(chosen.Project)
            ?? throw new InvalidOperationException("The AppHost is no longer owned by the workspace");
        selected = await window.FollowAspireAsync(current, app, _ => window.ResolveOwnedAspireResourceAsync(resource, chosen, cancel, allowMissing: true),
            Waits.Of(TimeSpan.FromSeconds(60)), "The AppHost started but the requested resource was not reported within 60 seconds", cancel);
        CheckWorkspace();
        return (selected, true);

        void CheckWorkspace()
        {
            if (Session != current || current.IsEnded)
            {
                throw new InvalidOperationException("The workspace changed");
            }
        }
    }

    private async Task<DebugSnapshot> DebugAspireTarget(string resource, string? configuration, string? arguments,
        bool attach, int waitSeconds, CancellationToken cancel)
    {
        ProjectSession current = Session;
        AgentCall.Follow(window.Actions.Work, AppActions.Debug, AppActions.RunProgram, AppActions.Build);
        using LaunchBuild built = new(Builds);
        (AspireSelection selection, bool started) = await PrepareAspireResource(resource, configuration,
            attach ? null : RunStart.Debug, arguments, cancel);
        DebugSession? previous = current.Aspire.FindService(selection)?.DebugTab?.Session;
        if (!started && await window.RunAspireToolResourceAsync(selection, attach ? "attach" : "debug", cancel) is { } problem)
        {
            throw new InvalidOperationException(WithBuildErrors(problem, built.Ended));
        }
        DateTime until = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 1, 120));
        AspireAppRun app = current.Aspire.FindApp(selection.AppHostPath)
            ?? throw new InvalidOperationException("The AppHost is no longer owned by the workspace");
        DebugSession debugged;
        try
        {
            debugged = await window.FollowAspireAsync(current, app, resources =>
            {
                AspireResource? fresh = resources.FirstOrDefault(resource => resource.Name == selection.Resource.Name);
                DebugSession? now = fresh is null ? null : current.Aspire.FindService(new AspireSelection(selection.AppHostPath, fresh))?.DebugTab?.Session;
                return Task.FromResult(now is not null && (attach || started || now != previous) ? now : null);
            }, until - DateTime.UtcNow, "The requested resource did not establish a debug session in this workspace", cancel);
        }
        catch (Exception failed) when (failed is TimeoutException || failed is InvalidOperationException && (Session != current || current.IsEnded))
        {
            throw new InvalidOperationException("The requested resource did not establish a debug session in this workspace");
        }
        DebugSnapshot snapshot = await AfterResume(until - DateTime.UtcNow, cancel, debugged);
        if (Session != current || current.IsEnded)
        {
            throw new InvalidOperationException("The workspace changed while reading the resource's debug session");
        }
        return snapshot;
    }

    /// <summary>Waits until the AppHost an agent's run started is up: its resource service has listed its resources and
    /// each shown one is up, has ended or was left unstarted; or until the AppHost ends or the limit passes. It never
    /// ends by itself nor shows a UI, so a run that waited for either waited it all. An AppHost that lists no resource
    /// waits the limit, as one whose resources have yet to come.</summary>
    private async Task AppHostUpAsync(AspireAppRun app, TimeSpan limit, CancellationToken cancel)
    {
        if (limit <= TimeSpan.Zero)
        {
            return;
        }
        try
        {
            await window.FollowAspireAsync(Session, app, resources => Task.FromResult(resources.Count > 0 && resources
                .Where(shown => !(shown.Data.TryGetProperty("isHidden", out JsonElement hidden) && hidden.ValueKind == JsonValueKind.True))
                .All(resource => StandingOf(app, resource) is { } standing && (standing.IsUp || standing.HasEnded) || app.IsLeftUnstarted(resource)) ? resources : null),
                limit, $"{app.Name} is not up yet", cancel);
        }
        catch (Exception unsettled) when (unsettled is TimeoutException or InvalidOperationException or IOException
            || unsettled is OperationCanceledException && !cancel.IsCancellationRequested)
        {
            // The time is up, the AppHost ended or its resource stream did: the run answers as it stands.
        }
    }

    /// <summary>What an agent's run of a resource answers once the resource is up or has ended, or waitSeconds have passed,
    /// following its AppHost's resource stream as it goes, in the shape a compose service's run answers
    /// (<see cref="ResourceAnswer"/>): how it and its AppHost's other resources stand, and its own console. A start that
    /// failed is an error.</summary>
    private async Task<RunResult> AspireResourceUpAsync(AspireSelection target, int waitSeconds, CancellationToken cancel)
    {
        ProjectSession current = Session;
        AspireAppRun app = current.Aspire.FindApp(target.AppHostPath)
            ?? throw new InvalidOperationException("The AppHost is no longer owned by the workspace");
        string name = target.Resource.Name;
        AgentCall.Say($"Waiting up to {Math.Max(0, waitSeconds)} s for {name} to be up");
        IReadOnlyList<AspireResource> resources;
        try
        {
            resources = await window.FollowAspireAsync(current, app, now => Task.FromResult(now.FirstOrDefault(resource => resource.Name == name) is { } it
                && (StandingOf(app, it).IsUp || StandingOf(app, it).HasEnded) ? now : null), TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), $"{name} is not up yet", cancel);
        }
        catch (TimeoutException)
        {
            resources = await (app.Interactions ?? throw new InvalidOperationException("The AppHost resource connection is still starting")).GetResourcesAsync(cancel);
        }
        if (Session != current || current.IsEnded || current.Aspire.FindApp(target.AppHostPath) != app)
        {
            throw new InvalidOperationException("The workspace or owned AppHost changed");
        }
        AspireResource? resource = resources.FirstOrDefault(resource => resource.Name == name);
        AspireResourceRun? service = app.Services.Where(run => ((IRunService)run).Name == name).MaxBy(run => run.Sequence);
        service?.Console.Flush();
        string output = service is null ? string.Empty : Tail(service.Console.Document.Snapshot, 200);
        bool unstarted = resource is not null && app.IsLeftUnstarted(resource);
        RunResult answer = ResourceAnswer(name, app.Name, "Resources", resource is null ? null : StandingOf(app, resource),
            [.. resources.Where(shown => !(shown.Data.TryGetProperty("isHidden", out JsonElement hidden) && hidden.ValueKind == JsonValueKind.True))
                .Select(shown => StandingOf(app, shown))], app.Notes, output, waitSeconds,
            unstarted ? $"{AspireAppRun.NotStarted}. {AspireAppRun.NotStartedWhy}" : null);
        if (resource?.State == "FailedToStart" && !unstarted)
        {
            throw new InvalidOperationException($"{answer.Status}\n{output}".TrimEnd());
        }
        return answer;
    }

    /// <summary>An AppHost's resource as its run's answer says it: its state (Not started where its run left it so), health and
    /// exit code.</summary>
    private static Standing StandingOf(AspireAppRun app, AspireResource resource) =>
        new(resource.Name, resource.State, resource.HealthStatus, ExitCode(resource)) { Shown = app.StateOf(resource) };

    private static int? ExitCode(AspireResource resource) => AspireJson.Properties(resource.Data, "properties")
        .Where(property => property.Name == "resource.exitCode")
        .Select(property => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int code) ? code
            : property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out int said) ? said : (int?)null)
        .FirstOrDefault();

    /// <summary>The workspace with the runs of services under way: each AppHost the window started, with its resources, and
    /// the container run, with its services, in the one shape (<see cref="ResourceInfo"/>).</summary>
    private async Task<Workspace> RunsWorkspace(Workspace snapshot, CancellationToken cancel)
    {
        ProjectSession current = Session;
        List<AspireOwnedAppInfo> apps = [];
        foreach (AspireAppRun app in current.Aspire.Apps.Where(app => !app.IsTest && !app.Stopping).ToArray())
        {
            IReadOnlyList<AspireResource> resources = [];
            string state = app.Interactions is null ? "Starting" : "Running";
            try
            {
                resources = app.Interactions is { } connection ? await connection.GetResourcesAsync(cancel) : [];
            }
            catch (Exception unread) when (unread is IOException or TimeoutException)
            {
                // The rest of the workspace still answers: this AppHost's state is why its resources cannot be read.
                state = unread.Message;
            }
            if (Session != current || current.IsEnded || current.Aspire.FindApp(app.Configuration.Project) != app || app.Stopping)
            {
                throw new InvalidOperationException("The workspace or owned AppHost changed");
            }
            apps.Add(new AspireOwnedAppInfo(app.Configuration.Project, app.ProcessId ?? 0, state, app.Group,
                [.. resources.Select(resource => ResourceInfoOf(app, resource))]) { How = Words(app.How) });
        }
        ContainerRunInfo? containers = await ContainerRunInfoAsync(cancel);
        if (Session != current || current.IsEnded)
        {
            throw new InvalidOperationException("The workspace changed");
        }
        return snapshot with { AspireApps = apps, ContainerRun = containers };
    }

    /// <summary>A resource as an agent's workspace has it, its state as the window says it (Not started for one its run
    /// left unstarted, as its configuration says Don't start), its container's id where it is one, and what reaches its .NET
    /// service from here.</summary>
    private ResourceInfo ResourceInfoOf(AspireAppRun app, AspireResource resource) => Reached(new ResourceInfo(resource.Name,
        AspireJson.String(resource.Data, "resourceType"), app.StateOf(resource), resource.HealthStatus)
    {
        Target = $"{app.Configuration.Project}::{resource.Name}",
        Generation = resource.Generation,
        DisplayName = resource.DisplayName,
        Urls = [.. AspireJson.Array(resource.Data, "urls").Select(url => JsonSerializer.SerializeToElement(new
        {
            name = AspireJson.String(url, "name"),
            url = SafeUrl(AspireJson.String(url, "url")),
        }))],
        Relationships = AspireJson.Array(resource.Data, "relationships"),
        Commands = [.. resource.Commands.Where(command => command.Enabled).Select(command => command.Name)],
        Container = AspireJson.Properties(resource.Data, "properties").FirstOrDefault(property => property.Name == "container.id").Value
            is { ValueKind: JsonValueKind.String } id ? id.GetString() : null,
    }, app, Session.Aspire.FindService(new AspireSelection(app.Configuration.Project, resource)));

    /// <summary>The AppHost resource command an agent's container_action names: an owned AppHost's resource, by its name or
    /// target, and the command its action names (its own name, or start, stop or restart as Aspire's resource-… one); none
    /// when it names no resource (a container), refused listing the resource's commands when it has none of that name.</summary>
    private async Task<(string? AppHost, string? Resource, string? Command)> AspireCommandOfAsync(string container, string action, CancellationToken cancel)
    {
        bool qualified = container.Contains("::", StringComparison.Ordinal);
        if (!qualified && !Session.Aspire.Apps.Any(app => !app.IsTest && !app.Stopping)
            || await window.ResolveOwnedAspireResourceAsync(container, null, cancel, allowMissing: !qualified) is not { } selected)
        {
            return default;
        }
        AspireResourceCommand[] shown = [.. selected.Resource.Commands.Where(command => !command.Hidden)];
        AspireResourceCommand named = shown.FirstOrDefault(command => command.Name == action) ?? shown.FirstOrDefault(command => command.Name == $"resource-{action}")
            ?? throw new ArgumentException($"{selected.Resource.Name} has no command {action}: "
                + (shown.Length == 0 ? "it has none" : $"its commands are {string.Join(", ", shown.Select(command => command.Name))}"));
        return named.Enabled ? (selected.AppHostPath, selected.Resource.Name, named.Name)
            : throw new InvalidOperationException($"{selected.Resource.Name}'s {named.Name} is not enabled now ({selected.Resource.State ?? "Unknown"})");
    }

    private static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
        ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri : null;
}

public sealed record AspireAppInfo(string AppHostPath, int ProcessId, string State, bool Owned, string? Group);

/// <summary>A resource of a run of services as an agent's workspace has it, an AppHost's resource and the container run's
/// compose service alike: its name, type (Project for a .NET service), state and health, the target the tools' resource
/// takes; an AppHost's generation, display name, addresses, relationships and enabled commands (container_action runs
/// them), a compose service's published ports; its container (an id, or a compose service's container's name); and for a
/// .NET service what reaches it from here: what it is held at its start for, the Debug tab debugging it (debug tools'
/// session) and that session's state, and whether the profiler records it.</summary>
public sealed record ResourceInfo(string Name, string? Type, string? State, string? Health)
{
    public string? Target { get; init; }
    public string? Generation { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<JsonElement>? Urls { get; init; }
    public string? Ports { get; init; }
    public IReadOnlyList<JsonElement>? Relationships { get; init; }
    public IReadOnlyList<string>? Commands { get; init; }
    public string? Container { get; init; }
    public string? Held { get; init; }
    public string? Session { get; init; }
    public string? Debugger { get; init; }
    public bool? Profiled { get; init; }
}

public sealed record AspireOwnedAppInfo(string AppHostPath, int ProcessId, string State, string Group, IReadOnlyList<ResourceInfo> Resources)
{
    /// <summary>How its run was started: run, debug, hotReload or profile.</summary>
    public string? How { get; init; }
}
