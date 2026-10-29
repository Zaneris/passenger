using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Passenger.App.Containers;
using Passenger.App.Aspire.Protocol;
using Passenger.Core;

namespace Passenger.App.Aspire;

public sealed partial class AspireInteractions
{
    public const int ResourceLimit = 256;
    private const string Redacted = "[redacted]";
    private static readonly TimeSpan ResourcesWait = TimeSpan.FromSeconds(30);
    private readonly object resourceSync = new();
    private readonly Dictionary<string, (Resource Protocol, AspireResource Snapshot)> watchedResources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceCommand[]> resourceTypeCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<CancellationTokenSource, (string Name, string Generation)> consoleWatches = [];
    private TaskCompletionSource resourcesLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource resourcesMoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim resourceReading = new(1, 1);
    private CancellationTokenSource? resourceWatch;
    private bool displayWatching;
    private volatile bool saysArguments;
    private int following;
    private Task resourceListening = Task.CompletedTask;
    private IOException? resourceFailure;

    /// <summary>One immutable bounded snapshot per server batch, delivered on the UI thread.</summary>
    public event EventHandler<IReadOnlyList<AspireResource>>? ResourcesChanged;

    /// <summary>Whether the AppHost says its resources' arguments (<c>resource.appArgs</c>, which Aspire's AppHosts send from
    /// 9.1 on, for containers and executables alike): a resource it sent has carried them.</summary>
    public bool SaysArguments => saysArguments;

    /// <summary>Controls display-only subscriptions. Interactions remain connected while hidden.</summary>
    public void SetWatching(bool watching)
    {
        lock (resourceSync)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }
            displayWatching = watching;
            if (!watching)
            {
                foreach (CancellationTokenSource source in consoleWatches.Keys.ToArray())
                {
                    source.Cancel();
                }
            }
            Listen();
        }
    }

    /// <summary>Keeps one resource stream open while held, shown or not, for a caller that waits on what the resources become
    /// (<see cref="FollowedAsync"/>) rather than asking again: the display's own stream while it watches.</summary>
    public IDisposable Follow()
    {
        lock (resourceSync)
        {
            following++;
            Listen();
        }
        return new Following(this);
    }

    /// <summary>The resources as the followed stream (<see cref="Follow"/>) has them once it has its first batch, and what
    /// completes at its next batch or its end; its failure, or its end, is thrown.</summary>
    public async Task<(IReadOnlyList<AspireResource> Resources, Task Moved)> FollowedAsync(CancellationToken cancel)
    {
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancel, cancellation);
        Task loaded;
        lock (resourceSync)
        {
            loaded = resourcesLoaded.Task;
        }
        try
        {
            await loaded.WaitAsync(request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!request.IsCancellationRequested)
        {
        }
        request.Token.ThrowIfCancellationRequested();
        lock (resourceSync)
        {
            if (resourceFailure is not null)
            {
                throw resourceFailure;
            }
            if (resourceWatch is null || !loaded.IsCompletedSuccessfully)
            {
                throw new IOException("The AppHost resource stream ended.");
            }
            return (ResourceSnapshot(), resourcesMoved.Task);
        }
    }

    private sealed class Following(AspireInteractions interactions) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                lock (interactions.resourceSync)
                {
                    interactions.following--;
                    interactions.Listen();
                }
            }
        }
    }

    /// <summary>Opens the resource stream while the display watches or a caller follows it, and closes it once neither does.
    /// Called under <see cref="resourceSync"/>.</summary>
    private void Listen()
    {
        bool wanted = (displayWatching || following > 0) && !cancellation.IsCancellationRequested;
        if (wanted && resourceWatch is null)
        {
            resourceFailure = null;
            resourcesLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            resourceWatch = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            resourceListening = WatchResourcesAsync(resourceWatch, resourcesLoaded);
        }
        else if (!wanted && resourceWatch is not null)
        {
            resourceWatch.Cancel();
            resourceWatch = null;
        }
    }

    /// <summary>Wakes whoever follows the resources: a batch came, or the stream ended. Called under <see cref="resourceSync"/>.</summary>
    private void Moved()
    {
        resourcesMoved.TrySetResult();
        resourcesMoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task WatchResourcesAsync(CancellationTokenSource source, TaskCompletionSource loaded)
    {
        try
        {
            await resourceReading.WaitAsync(source.Token).ConfigureAwait(false);
            try
            {
                await ListenResourcesAsync(source.Token, loaded, singleSnapshot: false).ConfigureAwait(false);
            }
            finally
            {
                resourceReading.Release();
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            loaded.TrySetCanceled(source.Token);
        }
        finally
        {
            lock (resourceSync)
            {
                if (ReferenceEquals(resourceWatch, source))
                {
                    resourceWatch = null;
                }
                source.Dispose();
                Moved();
            }
        }
    }

    /// <summary>The AppHost's resources now. A connection that has ended, or an AppHost that sends none in time, is said as
    /// such (an <see cref="IOException"/> or a <see cref="TimeoutException"/>): only the caller's own cancellation cancels.</summary>
    public async Task<IReadOnlyList<AspireResource>> GetResourcesAsync(CancellationToken cancel = default)
    {
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancel, cancellation);
        request.CancelAfter(ResourcesWait);
        try
        {
            return await ReadResourcesAsync(request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw cancellation.IsCancellationRequested
                ? new IOException(Failure ?? "The AppHost's resource connection has ended.")
                : new TimeoutException($"The AppHost sent no resources within {ResourcesWait.TotalSeconds:0} s.");
        }
    }

    private async Task<IReadOnlyList<AspireResource>> ReadResourcesAsync(CancellationToken request)
    {
        while (true)
        {
            Task? loaded;
            Task acquire;
            lock (resourceSync)
            {
                loaded = resourceWatch is not null ? resourcesLoaded.Task : null;
                acquire = loaded is null ? resourceReading.WaitAsync(request) : Task.CompletedTask;
            }
            if (loaded is not null)
            {
                try
                {
                    await loaded.WaitAsync(request).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (!request.IsCancellationRequested)
                {
                    continue;
                }
            }
            await acquire.ConfigureAwait(false);
            try
            {
                lock (resourceSync)
                {
                    if (resourceWatch is not null)
                    {
                        continue;
                    }
                    resourceFailure = null;
                }
                await ListenResourcesAsync(request, new(TaskCreationOptions.RunContinuationsAsynchronously), singleSnapshot: true).ConfigureAwait(false);
                break;
            }
            finally
            {
                resourceReading.Release();
            }
        }
        request.ThrowIfCancellationRequested();
        lock (resourceSync)
        {
            if (resourceFailure is not null)
            {
                throw resourceFailure;
            }
            return ResourceSnapshot();
        }
    }

    private IReadOnlyList<AspireResource> ResourceSnapshot() => Array.AsReadOnly(watchedResources.Values
        .Select(item => item.Snapshot).OrderBy(resource => resource.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());

    private async Task ListenResourcesAsync(CancellationToken cancel, TaskCompletionSource loaded, bool singleSnapshot)
    {
        try
        {
            using AsyncServerStreamingCall<WatchResourcesUpdate> watch = client.WatchResources(new WatchResourcesRequest(), headers, cancellationToken: cancel);
            bool initialized = false;
            while (await watch.ResponseStream.MoveNext(cancel).ConfigureAwait(false))
            {
                WatchResourcesUpdate update = watch.ResponseStream.Current;
                IReadOnlyList<AspireResource> snapshot;
                lock (resourceSync)
                {
                    cancel.ThrowIfCancellationRequested();
                    Dictionary<string, (Resource Protocol, AspireResource Snapshot)> next = new(watchedResources, StringComparer.Ordinal);
                    if (update.InitialData is { } initial && !initialized)
                    {
                        next.Clear();
                        resourceTypeCommands.Clear();
                        if (initial.Resources.Count > ResourceLimit || initial.ResourceTypes.Count > ResourceLimit)
                        {
                            throw new InvalidDataException("The AppHost exceeds the 256 resource/type limit.");
                        }
                        foreach (ResourceType type in initial.ResourceTypes)
                        {
                            ValidateIdentity(type.UniqueName);
                            if (!resourceTypeCommands.TryAdd(type.UniqueName, type.Commands.Select(SafeCommand).ToArray()))
                            {
                                throw new InvalidDataException("The AppHost sent duplicate resource types.");
                            }
                        }
                        foreach (Resource resource in initial.Resources)
                        {
                            AddResource(next, resource, rejectDuplicate: true);
                        }
                        initialized = true;
                    }
                    else if (update.Changes is { } changes && initialized)
                    {
                        foreach (WatchResourcesChange change in changes.Value)
                        {
                            if (change.Upsert is { } resource)
                            {
                                AddResource(next, resource, rejectDuplicate: false);
                            }
                            else if (change.Delete is { } deletion)
                            {
                                ValidateIdentity(deletion.ResourceName);
                                ValidateIdentity(deletion.ResourceType);
                                if (next.TryGetValue(deletion.ResourceName, out (Resource Protocol, AspireResource Snapshot) existing)
                                    && existing.Protocol.ResourceType != deletion.ResourceType)
                                {
                                    throw new InvalidDataException("The AppHost deleted a resource with a different type.");
                                }
                                next.Remove(deletion.ResourceName);
                            }
                            else
                            {
                                throw new InvalidDataException("The AppHost sent an unsupported resource update.");
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("The AppHost resource stream did not follow its initial-snapshot protocol.");
                    }
                    if (next.Count > ResourceLimit)
                    {
                        throw new InvalidDataException("The AppHost exceeded the 256 resource limit.");
                    }
                    watchedResources.Clear();
                    foreach (KeyValuePair<string, (Resource Protocol, AspireResource Snapshot)> item in next)
                    {
                        watchedResources.Add(item.Key, item.Value);
                    }
                    foreach ((CancellationTokenSource source, (string name, string generation)) in consoleWatches.ToArray())
                    {
                        if (!watchedResources.TryGetValue(name, out (Resource Protocol, AspireResource Snapshot) current) || current.Snapshot.Generation != generation)
                        {
                            source.Cancel();
                        }
                    }
                    snapshot = ResourceSnapshot();
                    loaded.TrySetResult();
                    Moved();
                }
                if (singleSnapshot)
                {
                    return;
                }
                using IDisposable held = BackgroundWork.Scope();
                await UiThread.InvokeAsync(() =>
                {
                    if (!cancel.IsCancellationRequested)
                    {
                        ResourcesChanged?.Invoke(this, snapshot);
                    }
                    return true;
                }).WaitAsync(cancel).ConfigureAwait(false);
            }
            cancel.ThrowIfCancellationRequested();
            throw new IOException("The AppHost resource stream ended.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            loaded.TrySetCanceled(cancel);
        }
        catch (Exception error) when (error is RpcException or IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            if (cancel.IsCancellationRequested)
            {
                loaded.TrySetCanceled(cancel);
                return;
            }
            IOException failure = new(error is RpcException rpc
                ? $"The AppHost resource stream failed ({rpc.StatusCode})."
                : error is InvalidDataException ? error.Message : "The AppHost resource stream returned invalid or incomplete data.");
            lock (resourceSync)
            {
                resourceFailure = failure;
                foreach (CancellationTokenSource source in consoleWatches.Keys.ToArray())
                {
                    source.Cancel();
                }
                loaded.TrySetResult();
                Moved();
            }
            if (singleSnapshot)
            {
                return;
            }
            using IDisposable held = BackgroundWork.Scope();
            await UiThread.InvokeAsync(() =>
            {
                if (!cancel.IsCancellationRequested)
                {
                    Failure = failure.Message;
                    Failed?.Invoke(this, failure.Message);
                }
                return true;
            }).WaitAsync(cancel).ConfigureAwait(false);
        }
    }

    private static void ValidateIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
        {
            throw new InvalidDataException("The AppHost sent an invalid resource identity.");
        }
    }

    private void AddResource(Dictionary<string, (Resource Protocol, AspireResource Snapshot)> target, Resource incoming, bool rejectDuplicate)
    {
        ValidateIdentity(incoming.Name);
        ValidateIdentity(incoming.ResourceType);
        ValidateIdentity(incoming.Uid);
        if (target.Any(item => item.Key != incoming.Name && item.Value.Protocol.Uid == incoming.Uid))
        {
            throw new InvalidDataException("The AppHost sent duplicate resource UIDs.");
        }
        if (rejectDuplicate && target.ContainsKey(incoming.Name))
        {
            throw new InvalidDataException("The AppHost sent duplicate resources.");
        }
        // A secret masked by ContainerSecrets' rule, and one the AppHost marks sensitive whatever its name.
        Resource resource = incoming.Clone();
        saysArguments |= resource.Properties.Any(property => property.Name == "resource.appArgs");
        ResourceProperty? arguments = MaskArguments(resource);
        foreach (ResourceProperty property in resource.Properties)
        {
            if (property.IsSensitive && !ReferenceEquals(property, arguments))
            {
                property.Value = Google.Protobuf.WellKnownTypes.Value.ForString(ContainerSecrets.Masked);
            }
            else if (property.Value?.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue)
            {
                property.Value = Google.Protobuf.WellKnownTypes.Value.ForString(ContainerSecrets.Shown(property.Name, property.Value.StringValue));
            }
        }
        foreach (Protocol.EnvironmentVariable variable in resource.Environment)
        {
            if (variable.HasValue)
            {
                variable.Value = ContainerSecrets.Shown(variable.Name, variable.Value);
            }
        }
        foreach (HealthReport report in resource.HealthReports)
        {
            if (report.Exception.Length > 0)
            {
                report.Exception = ContainerSecrets.InValue(report.Exception);
            }
        }
        ResourceCommand[] commands = resource.Commands.Select(SafeCommand).ToArray();
        resource.Commands.Clear();
        resource.Commands.Add(commands);
        if (resourceTypeCommands.TryGetValue(resource.ResourceType, out ResourceCommand[]? inherited))
        {
            resource.Commands.Add(inherited.Where(command => !commands.Any(own => own.Name == command.Name)).Select(command => command.Clone()));
        }
        if (resource.Commands.Select(command => command.Name).Distinct(StringComparer.Ordinal).Count() != resource.Commands.Count)
        {
            throw new InvalidDataException("The AppHost sent duplicate resource commands.");
        }
        target[resource.Name] = (resource, ToSnapshot(resource));
    }

    /// <summary>A resource's arguments (<c>resource.appArgs</c>) masked in place by ContainerSecrets' rule where the AppHost
    /// sends its marks with them (<c>resource.appArgsSensitivity</c>: a number an argument, other than 0 for one it marks
    /// sensitive, which shows as the dots; a mark of another kind read as sensitive). The property so masked, or null where
    /// it holds no list of words or comes with no marks.</summary>
    private static ResourceProperty? MaskArguments(Resource resource)
    {
        if (resource.Properties.FirstOrDefault(property => property.Name == "resource.appArgs") is not { } arguments
            || arguments.Value?.KindCase != Google.Protobuf.WellKnownTypes.Value.KindOneofCase.ListValue
            || arguments.Value.ListValue.Values.Any(word => word.KindCase != Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue)
            || resource.Properties.FirstOrDefault(property => property.Name == "resource.appArgsSensitivity")?.Value is not { } said
            || said.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue)
        {
            return null;
        }
        // Marks that are no list match no argument, so every argument is masked.
        bool[] marks = said.KindCase != Google.Protobuf.WellKnownTypes.Value.KindOneofCase.ListValue ? []
            : [.. said.ListValue.Values.Select(mark => mark.KindCase switch
            {
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue => mark.NumberValue != 0,
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue => mark.BoolValue,
                _ => true,
            })];
        IReadOnlyList<string> shown = ContainerSecrets.Arguments([.. arguments.Value.ListValue.Values.Select(word => word.StringValue)], marks);
        arguments.Value = Google.Protobuf.WellKnownTypes.Value.ForList([.. shown.Select(Google.Protobuf.WellKnownTypes.Value.ForString)]);
        return arguments;
    }

    private static ResourceCommand SafeCommand(ResourceCommand incoming)
    {
        ValidateIdentity(incoming.Name);
        ResourceCommand command = incoming.Clone();
        foreach (InteractionInput input in command.ArgumentInputs)
        {
            if (input.InputType == InputType.SecretText && input.Value.Length > 0)
            {
                input.Value = Redacted;
            }
        }
        return command;
    }

    private static AspireResource ToSnapshot(Resource resource)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        foreach (ResourceProperty property in resource.Properties)
        {
            ValidateIdentity(property.Name);
            properties[property.Name] = property.Value is null ? null : JsonDocumentValue(property.Value);
        }
        string? source = null;
        foreach (string key in new[] { "project.path", "executable.path", "container.image" })
        {
            ResourceProperty? property = resource.Properties.FirstOrDefault(property => property.Name == key && !property.IsSensitive);
            if (property?.Value?.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue
                && property.Value.StringValue.Length > 0
                && (key == "container.image" || Path.IsPathFullyQualified(property.Value.StringValue)))
            {
                source = property.Value.StringValue;
                break;
            }
        }
        if (resource.HealthReports.Any(report => report.HasStatus && report.Status is not (HealthStatus.Healthy or HealthStatus.Unhealthy or HealthStatus.Degraded)))
        {
            throw new InvalidDataException("The AppHost sent an unsupported health status.");
        }
        string? health = resource.State != "Running" ? null
            : resource.HealthReports.Any(report => !report.HasStatus || report.Status == HealthStatus.Unhealthy) ? "Unhealthy"
            : resource.HealthReports.Any(report => report.Status == HealthStatus.Degraded) ? "Degraded" : "Healthy";
        JsonElement data = JsonSerializer.SerializeToElement(new
        {
            name = resource.Name,
            displayName = resource.DisplayName,
            type = resource.ResourceType,
            resourceType = resource.ResourceType,
            uid = resource.Uid,
            state = resource.HasState ? resource.State : null,
            stateStyle = resource.HasStateStyle ? resource.StateStyle : null,
            creationTimestamp = Timestamp(resource.CreatedAt),
            startTimestamp = Timestamp(resource.StartedAt),
            stopTimestamp = Timestamp(resource.StoppedAt),
            source,
            healthStatus = health,
            properties,
            propertyMetadata = resource.Properties.ToDictionary(property => property.Name, property => new { isSensitive = property.IsSensitive, displayName = property.DisplayName }),
            environment = resource.Environment.ToDictionary(variable => variable.Name, variable => variable.HasValue ? variable.Value : null),
            environmentMetadata = resource.Environment.ToDictionary(variable => variable.Name, variable => new { isFromSpec = variable.IsFromSpec, isMasked = variable.HasValue }),
            urls = resource.Urls.Select(url => new { name = url.HasEndpointName ? url.EndpointName : null, displayName = url.DisplayProperties?.DisplayName, url = ContainerSecrets.PublicHttpUrl(url.FullUrl), isInternal = url.IsInternal, isInactive = url.IsInactive }),
            healthReports = resource.HealthReports.ToDictionary(report => report.Key, report => new { status = report.HasStatus ? report.Status.ToString() : null, description = report.Description, exceptionMessage = report.Exception.Length > 0 ? report.Exception : null, lastRunAt = Timestamp(report.LastRunAt) }),
            relationships = resource.Relationships.Select(relationship => new { resourceName = relationship.ResourceName, type = relationship.Type }),
            volumes = resource.Volumes.Select(volume => new { source = volume.Source, target = volume.Target, mountType = volume.MountType, isReadOnly = volume.IsReadOnly }),
            commands = resource.Commands.ToDictionary(command => command.Name, command => new
            {
                displayName = command.DisplayName,
                description = command.DisplayDescription,
                state = command.State.ToString(),
                confirmationMessage = command.HasConfirmationMessage ? command.ConfirmationMessage : null,
                argumentInputs = command.ArgumentInputs.Select(input => new
                {
                    name = input.Name,
                    label = input.Label,
                    inputType = input.InputType.ToString(),
                    required = input.Required,
                    value = input.Value,
                    placeholder = input.Placeholder,
                    options = input.Options.ToDictionary(),
                    description = input.Description,
                    enableDescriptionMarkdown = input.EnableDescriptionMarkdown,
                    maxLength = input.MaxLength,
                    allowCustomChoice = input.AllowCustomChoice,
                    disabled = input.Disabled,
                    loading = input.Loading,
                    updateStateOnChange = input.UpdateStateOnChange,
                    maxFileSize = input.MaxFileSize,
                    allowMultipleFiles = input.AllowMultipleFiles,
                    fileFilter = input.FileFilter,
                }),
            }),
            isHidden = resource.IsHidden,
            supportsDetailedTelemetry = resource.SupportsDetailedTelemetry,
        });
        return new AspireResource(data);
    }

    private static JsonElement JsonDocumentValue(IMessage value)
    {
        using JsonDocument document = JsonDocument.Parse(JsonFormatter.Default.Format(value));
        return document.RootElement.Clone();
    }

    private static string? Timestamp(Google.Protobuf.WellKnownTypes.Timestamp? value) => value?.ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Backlog followed by live lines. Enumeration is canceled when this resource's generation changes.</summary>
    public async IAsyncEnumerable<IReadOnlyList<AspireConsoleLog>> WatchConsoleAsync(AspireResource resource,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancel, cancellation);
        await GetResourcesAsync(request.Token).ConfigureAwait(false);
        lock (resourceSync)
        {
            if (resourceFailure is not null)
            {
                throw resourceFailure;
            }
            if (!watchedResources.TryGetValue(resource.Name, out (Resource Protocol, AspireResource Snapshot) current) || current.Snapshot.Generation != resource.Generation)
            {
                throw new InvalidOperationException("The resource generation changed. Refresh before reading its console.");
            }
            if (consoleWatches.Keys.Count(watch => !watch.IsCancellationRequested) >= AspireClient.ResourceLimit)
            {
                throw new InvalidOperationException($"The AppHost already has {AspireClient.ResourceLimit} active console subscriptions.");
            }
            consoleWatches.Add(request, (resource.Name, resource.Generation));
        }
        try
        {
            using AsyncServerStreamingCall<WatchResourceConsoleLogsUpdate> watch = client.WatchResourceConsoleLogs(
                new WatchResourceConsoleLogsRequest { ResourceName = resource.Name }, headers, cancellationToken: request.Token);
            int? lastLineNumber = null;
            while (await MoveConsoleNextAsync(watch.ResponseStream, request.Token).ConfigureAwait(false))
            {
                request.Token.ThrowIfCancellationRequested();
                if (watch.ResponseStream.Current.LogLines.Count > 8192)
                {
                    throw new InvalidDataException("The AppHost console batch exceeds 8192 lines.");
                }
                List<AspireConsoleLog> lines = [];
                foreach (ConsoleLogLine line in watch.ResponseStream.Current.LogLines)
                {
                    if (lastLineNumber is { } previous && line.LineNumber <= previous)
                    {
                        continue;
                    }
                    lastLineNumber = line.LineNumber;
                    lines.Add(new AspireConsoleLog(resource.Name, line.Text, line.IsStdErr, null, line.LineNumber));
                }
                if (lines.Count > 0)
                {
                    yield return Array.AsReadOnly(lines.ToArray());
                }
            }
        }
        finally
        {
            lock (resourceSync)
            {
                consoleWatches.Remove(request);
            }
        }
    }

    private static async Task<bool> MoveConsoleNextAsync(IAsyncStreamReader<WatchResourceConsoleLogsUpdate> stream, CancellationToken cancel)
    {
        try
        {
            return await stream.MoveNext(cancel).ConfigureAwait(false);
        }
        catch (RpcException error) when (cancel.IsCancellationRequested && error.StatusCode == StatusCode.Cancelled)
        {
            throw new OperationCanceledException(cancel);
        }
        catch (RpcException error)
        {
            throw new IOException($"The AppHost console stream failed ({error.StatusCode}).");
        }
    }
}
