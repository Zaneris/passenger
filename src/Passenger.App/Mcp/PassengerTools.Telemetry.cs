using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Passenger.App.Aspire;
using Passenger.App.Run;
using Passenger.App.Telemetry;

namespace Passenger.App.Mcp;

public sealed partial class PassengerTools
{
    [McpServerTool(Name = McpTools.Telemetry, Title = "Logs and traces", ReadOnly = true), Description(
        "A run's structured logs or traces (kind) from its dashboard: an Aspire AppHost's, or the container run's when its "
        + "configuration collects them; a resource's or Compose service's (resource) or one trace's (traceId) alone.")]
    public Task<TelemetryInfo> GetTelemetry(string kind = "logs", string? appHost = null,
        string? resource = null, string? traceId = null, int limit = 200,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (kind is not ("logs" or "traces"))
        {
            throw new ArgumentException("kind must be logs or traces");
        }
        ProjectSession current = Session;
        (ServiceRun run, TelemetrySource? source, string? filter) = await TelemetryOfAsync(current, appHost, resource, cancel);
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

    /// <summary>The run an agent's <c>telemetry</c> reads, with the resource it reads alone: a Compose service of the container
    /// run that collects telemetry, else an owned AppHost's resource or the AppHost <paramref name="appHost"/> names; with
    /// neither named, the one run there is that has telemetry.</summary>
    private async Task<(ServiceRun Run, TelemetrySource? Source, string? Filter)> TelemetryOfAsync(ProjectSession current, string? appHost, string? resource,
        CancellationToken cancel)
    {
        IReadOnlyList<ServiceRun> runs = current.ServiceRuns;
        Containers.ContainerRun? running = runs.OfType<Containers.ContainerRun>().FirstOrDefault();
        bool hosts = runs.OfType<AspireAppRun>().Any();
        Containers.ContainerRun? containers = running is { Plan.Telemetry: not null } ? running : null;
        // A container run that collects nothing, named or the only run there is: what to turn on, not Aspire's refusal.
        if (appHost is null && running is { Plan.Telemetry: null } uncollected
            && (resource is null && !hosts || resource is not null && OfRun(uncollected, resource) is not null))
        {
            throw new InvalidOperationException($"{uncollected.Name} collects no logs or traces: turn on its configuration's Collect logs and traces with "
                + "Aspire's dashboard (Edit Configurations), then run it again");
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
}

public sealed record TraceInfo(string TraceId, JsonElement Summary, IReadOnlyList<JsonElement> Spans);
public sealed record TelemetryInfo(IReadOnlyList<JsonElement> Logs, IReadOnlyList<TraceInfo> Traces);
