using System.ComponentModel;
using ModelContextProtocol.Server;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>The container engine for an agent, as the Containers window shows and acts on it: its state, Compose projects
/// and containers, a container's log, a command run in one, and the window's start, stop, restart and remove.</summary>
public sealed partial class PassengerTools
{
    // A container's log is this tool's with container: a tool of its own cost every session's list more.
    [McpServerTool(Name = McpTools.Containers, Title = "The container engine", ReadOnly = true), Description(
        "The docker engine of the person's current context: its status (with what to do when it cannot serve), its Compose " +
        "projects with their services, and every container with its state, health, ports and image. With container (its name " +
        "or id, a Compose project or project/service with every service's lines), its log instead: the last tail lines, or " +
        "those since a time (2026-10-21T10:00:00, or 10m); followSeconds keeps reading new lines up to that long.")]
    public async Task<object> Containers(string? container = null, int tail = 200, string? since = null, int followSeconds = 0, bool timestamps = false,
        CancellationToken cancel = default) =>
        container is null ? await Engine(cancel) : await ContainerLogs(container, tail, since, followSeconds, timestamps, cancel);

    private Task<ContainersInfo> Engine(CancellationToken cancel) => OnUi(cancel, async () =>
    {
        AgentCall.Say("Asking the container engine");
        (EngineState state, ContainerSnapshot snapshot) = await EngineHoldsAsync(again: true, cancel);
        return new ContainersInfo(state.Status.ToString(), state.Problem, state.Context, state.ServerVersion, state.ComposeVersion,
            [.. snapshot.Projects.Select(p => new ComposeInfo(p.Name, p.Status, [.. p.Services.Select(s => s.Service)]))],
            [.. snapshot.AllContainers.Select(c => new ContainerSummary(c.Name, c.Id, c.Image, c.State, c.Health, c.Project, c.Service, [.. c.Ports.Select(p => p.ToString())],
                c.IsTestcontainers ? true : null))]);
    });

    private Task<string> ContainerLogs(string container, int tail, string? since, int followSeconds, bool timestamps, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        ContainerRow row = await NamedContainer(container, cancel);
        List<string> options = since is null ? ["--tail", Math.Clamp(tail, 1, 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture)] : ["--since", since];
        if (timestamps)
        {
            options.Add("-t");
        }
        if (followSeconds > 0)
        {
            options.Add("-f");
        }
        IReadOnlyList<string> words = row.Container is { } one ? ["logs", .. options, one.Id]
            : [.. row.Project!.Arguments, "logs", "--no-color", .. options, .. row.Service is { } service ? ["--", service] : Array.Empty<string>()];
        ContainerEngine engine = window.Engine;
        TimeSpan time = TimeSpan.FromSeconds(Math.Clamp(followSeconds, 0, 300)) + ContainerEngine.AskLimit;
        AgentCall.Say(followSeconds > 0 ? $"Following {row.Name}'s log for {followSeconds} s" : $"Reading {row.Name}'s log");
        ChildResult result = await Task.Run(() => followSeconds > 0
            ? engine.RunForAsync(words, TimeSpan.FromSeconds(Math.Clamp(followSeconds, 1, 300)), cancel)
            : engine.RunAsync(words, time, cancel), cancel);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{ContainerEngine.Words(words)} failed: {ContainerEngine.Said(result)}");
        }
        // A container's log is its output and its errors, which docker logs gives back on each of its own.
        return row.Container is null ? result.Output : string.Join('\n', new[] { result.Output.TrimEnd('\n'), result.Error.TrimEnd('\n') }.Where(s => s.Length > 0));
    });

    [McpServerTool(Name = McpTools.ContainerExec, Title = "Run a command in a container", Destructive = true), Description(
        "Runs a shell command line (sh -c) in a running container (its name or id) and answers its exit code and output.")]
    public Task<ContainerExec> ContainerExec(string container, string command, int waitSeconds = 30, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        ContainerRow row = await NamedContainer(container, cancel);
        if (row.Container is not { } one)
        {
            throw new ArgumentException($"{container} is a Compose project or service, not a container: name one of its containers");
        }
        ContainerEngine engine = window.Engine;
        AgentCall.Say($"Running in {one.Name}: {command}");
        ChildResult result = await Task.Run(() => engine.RunAsync(["exec", one.Id, "sh", "-c", command], TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 600)), cancel), cancel);
        return new ContainerExec(result.ExitCode, result.Output, result.Error.Length > 0 ? result.Error : null);
    });

    // An AppHost resource's commands are this tool's too: a parameter costs every session's tool list less than a tool.
    [McpServerTool(Name = McpTools.ContainerAction, Title = "Start, stop, restart or remove a container", Destructive = true), Description(
        "As the Containers window's buttons, action start, stop, restart or remove on a container (its name or id), a Compose " +
        "project (remove takes it down, its volumes kept) or a project/service; never a volume or image. Given an Aspire " +
        "resource (its target), action is one of its commands, run as the Aspire window does.")]
    public Task<string> ContainerAction(string container, string action,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (await AspireCommandOfAsync(container, action, cancel) is ({ } appHost, { } resource, { } command))
        {
            AgentCall.Say($"Running {resource}'s {command}");
            return $"{resource}: {command} {await window.ExecuteAspireResourceCommandAsync(appHost, resource, command, cancel)}";
        }
        if (!ContainerCommands.AgentActions.Contains(action))
        {
            throw new ArgumentException($"{action} is not an action: start, stop, restart or remove");
        }
        ContainerRow row = await NamedContainer(container, cancel);
        string id = action switch
        {
            "start" => AppActions.ContainerStart,
            "stop" => AppActions.ContainerStop,
            "restart" => AppActions.ContainerRestart,
            _ => AppActions.ContainerRemove,
        };
        AgentCall.Say(ContainerCommands.Stage(action, row));
        return await window.ContainerActAsync(id, action, row) is { } problem
            ? throw new InvalidOperationException($"{action} {row.Name} failed: {problem}")
            : $"{row.Name}: {action} done";
    });

    /// <summary>A container, project or service by the name an agent gave, the engine asked; refused saying why when the
    /// engine cannot serve or nothing has that name.</summary>
    private async Task<ContainerRow> NamedContainer(string name, CancellationToken cancel)
    {
        // Its containers and projects alone: a name is never an image's, volume's or network's (docker images is slow on a big engine).
        (EngineState state, ContainerSnapshot snapshot) = await EngineHoldsAsync(again: false, cancel, containersOnly: true);
        if (state.Problem is { } problem && state.Status is not EngineStatus.NoCompose)
        {
            throw new InvalidOperationException(problem);
        }
        return ContainerRow.Named(snapshot, name) ?? throw new ArgumentException($"No container, Compose project or project/service is named {name}: containers lists them");
    }

    /// <summary>The engine's state and, when it can serve, what it holds, read off the window's thread; an answer of the
    /// CLI's that does not read is the agent's to hear, as the engine's refusal is.</summary>
    private async Task<(EngineState State, ContainerSnapshot Snapshot)> EngineHoldsAsync(bool again, CancellationToken cancel, bool containersOnly = false)
    {
        ContainerEngine engine = window.Engine;
        try
        {
            return await Task.Run(async () =>
            {
                EngineState state = await engine.StateAsync(again, cancel);
                return (state, state.Status is EngineStatus.Ready or EngineStatus.NoCompose ? await ContainerSnapshot.ReadAsync(engine, cancel, containersOnly) : ContainerSnapshot.Empty);
            }, cancel);
        }
        catch (System.Text.Json.JsonException unread)
        {
            throw new InvalidOperationException($"The container engine's answer could not be read: {unread.Message}", unread);
        }
    }
}

/// <summary>The engine as an agent reads it: its status and problem, context and versions, Compose projects and containers.</summary>
public sealed record ContainersInfo(string Status, string? Problem, string? Context, string? Version, string? Compose, IReadOnlyList<ComposeInfo> Projects,
    IReadOnlyList<ContainerSummary> Containers);

/// <summary>A Compose project: its name, status and services.</summary>
public sealed record ComposeInfo(string Name, string Status, IReadOnlyList<string> Services);

/// <summary>A container: name, id, image, state, health, its project and service, its published ports, and whether
/// Testcontainers started it.</summary>
public sealed record ContainerSummary(string Name, string Id, string Image, string State, string? Health, string? Project, string? Service,
    IReadOnlyList<string> Ports, bool? Testcontainers);

/// <summary>A command run in a container: its exit code, output and errors.</summary>
public sealed record ContainerExec(int ExitCode, string Output, string? Errors);
