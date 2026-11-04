using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Passenger.App.Aspire;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.Telemetry;
using Passenger.Core;
using Passenger.Profiler;

namespace Passenger.App.Mcp;

public sealed partial class PassengerTools
{
    [McpServerTool(Name = McpTools.Telemetry, Title = "Logs, traces and metrics", ReadOnly = true), Description(
        "A run's structured logs or traces (kind) from its dashboard: an Aspire AppHost's, or a container or project run's when its "
        + "configuration collects them; a resource's, Compose service's or project's (resource) or one trace's (traceId) alone. "
        + "kind metrics: a resource's .NET program's instruments read from its runtime a few seconds, each one's latest value and rate; "
        + "or a running Godot game's monitors, its own custom ones among them (resource the game's name, the chosen game when none is given).")]
    public Task<TelemetryInfo> GetTelemetry(string kind = "logs", string? appHost = null,
        string? resource = null, string? traceId = null, int limit = 200,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (kind is not ("logs" or "traces" or "metrics"))
        {
            throw new ArgumentException("kind must be logs, traces or metrics");
        }
        ProjectSession current = Session;
        if (kind == "metrics")
        {
            return await MetricsOfAsync(current, appHost, resource, limit, cancel);
        }
        (ITelemetryRun run, TelemetrySource? source, string? filter) = await TelemetryOfAsync(current, appHost, resource, cancel);
        if (source is null)
        {
            throw new InvalidOperationException(TelemetrySource.NoDashboard(run.Name));
        }
        IReadOnlyList<AspireStructuredLog> logs;
        IReadOnlyList<AspireTrace> traces;
        try
        {
            logs = kind == "logs" ? await AspireTelemetry.ReadLogsAsync(source.Endpoint, source.ApiKey, filter, traceId, limit, cancel) : [];
            traces = kind == "traces" ? await AspireTelemetry.ReadTracesAsync(source.Endpoint, source.ApiKey, filter, traceId, limit, cancel) : [];
        }
        catch (Exception unread) when (TelemetrySource.Said(unread) && !cancel.IsCancellationRequested)
        {
            // What the dashboard answered or failed to, said as the Telemetry view says it, never reported as Passenger's fault.
            throw new InvalidOperationException($"{run.Name}: {source.Unread(unread)}", unread);
        }
        if (Session != current || current.IsEnded || run.IsStopped)
        {
            throw new InvalidOperationException("The workspace or its run changed");
        }
        return new TelemetryInfo([.. logs.Select(log => log.Data)],
            [.. traces.Select(trace => new TraceInfo(trace.TraceId, trace.Data, [.. trace.Spans.Select(span => span.Data)]))]);
    });

    /// <summary>The run an agent's <c>telemetry</c> reads, with the resource it reads alone: a project run's that collects
    /// telemetry, named by its configuration or its records' name, or a Compose service of the container run that does, else an
    /// owned AppHost's resource or the AppHost <paramref name="appHost"/> names; with neither named, the one run there is that
    /// has telemetry.</summary>
    private async Task<(ITelemetryRun Run, TelemetrySource? Source, string? Filter)> TelemetryOfAsync(ProjectSession current, string? appHost, string? resource,
        CancellationToken cancel)
    {
        IReadOnlyList<ServiceRun> runs = current.ServiceRuns;
        Containers.ContainerRun? running = runs.OfType<Containers.ContainerRun>().FirstOrDefault();
        bool hosts = runs.OfType<AspireAppRun>().Any();
        Containers.ContainerRun? containers = running is { Plan.Telemetry: not null } ? running : null;
        IReadOnlyList<RunDashboard> projects = [.. current.Dashboards.Where(d => !d.IsStopped)];
        if (appHost is null && (resource is null ? projects.Count == 1 && running is null && !hosts ? projects[0] : null
            : projects.FirstOrDefault(d => d.Name == resource || d.Records == resource)) is { } project)
        {
            return (project, project.Telemetry, resource is null ? null : project.Records);
        }
        if (appHost is null && resource is null && projects.Count > 1 && running is null && !hosts)
        {
            throw new InvalidOperationException($"Name the run: resource, one of {Plural.Listed([.. projects.Select(d => d.Records)])}");
        }
        // A project run that collects nothing, the only run there is: what to turn on, not Aspire's refusal.
        if (appHost is null && resource is null && projects.Count == 0 && running is null && !hosts
            && current.Running.FirstOrDefault(p => p.Kind is RunningKind.Run or RunningKind.Debug or RunningKind.Profile) is { } program
            && current.Configurations.All.FirstOrDefault(c => c.Name == program.Name) is { Kind: RunKind.Project, Aspire: false, Telemetry: false } uncollecting)
        {
            throw new InvalidOperationException(Uncollected(uncollecting.Name));
        }
        // A container run that collects nothing, named or the only run there is: what to turn on, not Aspire's refusal.
        if (appHost is null && running is { Plan.Telemetry: null } uncollected
            && (resource is null && !hosts || resource is not null && OfRun(uncollected, resource) is not null))
        {
            throw new InvalidOperationException(Uncollected(uncollected.Name));
        }
        if (appHost is null && containers is not null
            && (resource is null && !hosts
                || resource is not null && !resource.Contains("::", StringComparison.Ordinal)
                    && (containers.Plan.Dotnet.Any(d => d.Service == resource) || containers.Plan.Backing.Contains(resource))))
        {
            return (containers, containers.Telemetry, resource);
        }
        string? qualified = resource is null ? null : appHost is not null && !resource.Contains("::", StringComparison.Ordinal)
            ? $"{Resolve(appHost)}::{resource}" : resource;
        AspireSelection? selected = qualified is null ? null : await window.ResolveOwnedAspireResourceAsync(qualified, null, cancel);
        string? path = appHost is null ? selected?.AppHostPath : Resolve(appHost);
        if (selected is not null && path is not null && !Passenger.Core.PathNames.Same(path, selected.AppHostPath))
        {
            throw new ArgumentException("appHost and resource name different AppHosts");
        }
        AspireAppRun[] apps = [.. current.ServiceRuns.OfType<AspireAppRun>().Where(app => path is null || Passenger.Core.PathNames.Same(path, app.Configuration.Project))];
        AspireAppRun app = apps.Length == 1 ? apps[0]
            : throw new InvalidOperationException(containers is not null && path is null
                ? $"Name the run: appHost, or resource (a service of {containers.Name} or an AppHost's); workspace lists them"
                : apps.Length == 0 && path is null && containers is null && current.Running.Count == 0
                ? $"Nothing runs whose logs and traces a dashboard collects: {McpTools.Run} with telemetry true runs a project so, and an AppHost's runs always are"
                : "Name one owned AppHost by appHost or a unique resource; workspace lists targets");
        if (Session != current || current.IsEnded)
        {
            throw new InvalidOperationException("The workspace or its run changed");
        }
        if (app.Telemetry is null)
        {
            throw new InvalidOperationException(TelemetrySource.NoDashboard(app.Name));
        }
        return (app, app.Telemetry, selected?.Resource.Name);
    }

    /// <summary>A running Godot game's monitors for an agent, as the Live Visual Tree charts them: the next it sends (once a
    /// second), else the last, the engine's and its own custom ones, at most <paramref name="limit"/>.</summary>
    private static async Task<TelemetryInfo> GameMetricsAsync(GodotGame game, int limit, CancellationToken cancel)
    {
        if (game.State != LiveAppState.Connected)
        {
            throw new InvalidOperationException(game.State == LiveAppState.Waiting ? $"{game.Name} has not connected to Passenger yet" : $"{game.Name} has ended: run or debug_start starts it again");
        }
        GodotMonitors? before = game.Monitors;
        await When(() => game.Monitors is not null && !ReferenceEquals(game.Monitors, before), Waits.Of(TimeSpan.FromSeconds(3)), cancel,
            On(h => game.MonitorsChanged += h, h => game.MonitorsChanged -= h));
        GodotMonitors monitors = game.Monitors ?? throw new InvalidOperationException($"{game.Name} sent no monitors in 3 s: it sends them once a second as it runs");
        MetricInfo[] heard = [.. MetricsPane.Combined(monitors.Readings()).Select(v => new MetricInfo(v.Meter, v.Instrument, v.Unit, v.Shown))];
        (IReadOnlyList<MetricInfo> kept, string? more) = AgentAnswers.Listed(heard, Math.Max(1, limit), "monitor", "a larger limit gives them");
        return new TelemetryInfo([], []) { Metrics = kept, More = more };
    }

    /// <summary>A resource's .NET program's metrics for an agent, as the Aspire window's Stats tab reads them: its runtime's
    /// instruments heard for a few collections (the meters it publishes itself read in a second session), each one's latest
    /// value and rate, at most <paramref name="limit"/>, saying how many more there were.</summary>
    private async Task<TelemetryInfo> MetricsOfAsync(ProjectSession current, string? appHost, string? resource, int limit, CancellationToken cancel)
    {
        if (appHost is null && (resource is null ? current.Game : current.Games.FirstOrDefault(g => g.Name.Equals(resource, StringComparison.OrdinalIgnoreCase))) is { } game)
        {
            return await GameMetricsAsync(game, limit, cancel);
        }
        if (resource is null)
        {
            throw new ArgumentException("kind metrics reads one resource's program: give resource, as workspace lists them");
        }
        string qualified = appHost is not null && !resource.Contains("::", StringComparison.Ordinal) ? $"{Resolve(appHost)}::{resource}" : resource;
        AspireSelection selected = await window.ResolveOwnedAspireResourceAsync(qualified, null, cancel)
            ?? throw new InvalidOperationException($"No owned AppHost has a resource {resource}; workspace lists them");
        int program = AspireController.ProgramOf(selected.Resource) ?? current.Aspire.FindService(selected)?.ProcessId
            ?? throw new InvalidOperationException($"{selected.Resource.DisplayName} runs no .NET program on this machine to read metrics from");
        Dictionary<(string, string), MetricValue> latest = [];
        HashSet<string> published = new(StringComparer.Ordinal);
        await HearAsync(LiveMetrics.Shipped);
        if (published.Count > 0)
        {
            await HearAsync([.. LiveMetrics.Shipped, .. published]);
        }
        if (Session != current || current.IsEnded)
        {
            throw new InvalidOperationException("The workspace or its run changed");
        }
        MetricInfo[] heard = [.. latest.Values.OrderBy(v => LiveMetrics.Shipped.Contains(v.Meter) ? 0 : 1).ThenBy(v => v.Meter, StringComparer.Ordinal)
            .ThenBy(v => v.Instrument, StringComparer.Ordinal).Select(v => new MetricInfo(v.Meter, v.Instrument, v.Unit, v.Shown))];
        (IReadOnlyList<MetricInfo> kept, string? more) = AgentAnswers.Listed(heard, Math.Max(1, limit), "instrument", "a larger limit gives them");
        return new TelemetryInfo([], []) { Metrics = kept, More = more };

        // Its readings of three collections at most, or of a few seconds; an idle histogram keeps the value it last had.
        async Task HearAsync(IReadOnlyCollection<string> meters)
        {
            object gate = new();
            int collections = 0;
            TaskCompletionSource enough = new(TaskCreationOptions.RunContinuationsAsynchronously);
            MetricsHeard listened = new(readings =>
            {
                lock (gate)
                {
                    foreach (MetricValue value in MetricsPane.Combined(readings))
                    {
                        if (!value.Idle || !latest.ContainsKey((value.Meter, value.Instrument)))
                        {
                            latest[(value.Meter, value.Instrument)] = value;
                        }
                    }
                    if (++collections >= 3)
                    {
                        enough.TrySetResult();
                    }
                }
            }, meter =>
            {
                lock (gate)
                {
                    published.Add(meter);
                }
            }, why => enough.TrySetException(new InvalidOperationException($"{selected.Resource.DisplayName}: {why}")));
            LiveMetrics live;
            try
            {
                live = await LiveMetrics.AttachAsync(program, meters, listened, cancel);
            }
            catch (ProfilingException failed)
            {
                throw new InvalidOperationException($"{selected.Resource.DisplayName}: {failed.Message}", failed);
            }
            await using (live)
            {
                await Task.WhenAny(enough.Task, Task.Delay(TimeSpan.FromSeconds(6), cancel), live.Ended);
                if (enough.Task.IsFaulted)
                {
                    await enough.Task;
                }
            }
        }
    }

    /// <summary>What a run that collects no logs or traces says to an agent: what to turn on.</summary>
    private static string Uncollected(string name) =>
        $"{name} collects no logs or traces: run it again with run's telemetry true (Edit Configurations' {RunConfigurationsDialog.TelemetryWords})";
}

public sealed record TraceInfo(string TraceId, JsonElement Summary, IReadOnlyList<JsonElement> Spans);
public sealed record TelemetryInfo(IReadOnlyList<JsonElement> Logs, IReadOnlyList<TraceInfo> Traces)
{
    /// <summary>kind metrics': each instrument's latest value as the Stats tab says it.</summary>
    public IReadOnlyList<MetricInfo>? Metrics { get; init; }

    /// <summary>What it left out of a long answer, and how to read it.</summary>
    public string? More { get; init; }
}

/// <summary>An instrument of a program's, its meter, unit and latest value with its rate ("12/s, median 0.14 ms, 95% under 0.22 ms").</summary>
public sealed record MetricInfo(string Meter, string Instrument, string Unit, string Value);
