using Avalonia.Controls;
using Avalonia.Threading;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Passenger.App.Aspire.Protocol;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>One authenticated AppHost connection. Dispose it when the AppHost or workspace ends.</summary>
public sealed partial class AspireInteractions : IDisposable, IAsyncDisposable
{
    private const int MessageLimit = 4 * 1024 * 1024;
    private const int DialogLimit = 16;
    private const long FileLimit = 100 * 1024 * 1024;
    private readonly GrpcChannel channel;
    private readonly DashboardService.DashboardServiceClient client;
    private readonly Metadata headers;
    private readonly Window owner;
    private readonly string appName;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken cancellation;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly SemaphoreSlim commanding = new(1, 1);
    private readonly Dictionary<int, AspireInteractionDialog> dialogs = [];
    private AsyncDuplexStreamingCall<WatchInteractionsRequestUpdate, WatchInteractionsResponseUpdate>? call;
    private Task listening = Task.CompletedTask;
    private int disposed;

    private AspireInteractions(GrpcChannel channel, string apiKey, Window owner, string appName, CancellationToken cancel)
    {
        this.channel = channel;
        this.owner = owner;
        this.appName = appName;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cancellation = lifetime.Token;
        headers = new Metadata { { "x-resource-service-api-key", apiKey } };
        client = new DashboardService.DashboardServiceClient(channel);
    }

    public event EventHandler<string>? Failed;
    public string? Failure { get; private set; }
    public IReadOnlyList<AspireInteractionDialog> OpenDialogs
    {
        get
        {
            UiThread.Check();
            return dialogs.Values.ToArray();
        }
    }

    public static async Task<AspireInteractions> StartAsync(Uri endpoint, string apiKey, Window owner, string appName, CancellationToken cancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!endpoint.IsAbsoluteUri || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https")
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
        {
            throw new ArgumentException("The AppHost resource service endpoint must be an HTTP or HTTPS URL.", nameof(endpoint));
        }
        GrpcChannel channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = MessageLimit,
            MaxSendMessageSize = MessageLimit,
        });
        AspireInteractions? connection = null;
        try
        {
            connection = new AspireInteractions(channel, apiKey, owner, appName, cancel);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await connection.client.GetApplicationInformationAsync(new ApplicationInformationRequest(),
                new CallOptions(connection.headers, cancellationToken: timeout.Token).WithWaitForReady(true)).ResponseAsync.ConfigureAwait(false);
            connection.call = connection.client.WatchInteractions(connection.headers, cancellationToken: connection.cancellation);
            connection.listening = connection.ListenAsync();
            return connection;
        }
        catch (RpcException) when (cancel.IsCancellationRequested)
        {
            connection?.Dispose();
            channel.Dispose();
            throw new OperationCanceledException(cancel);
        }
        catch (RpcException error)
        {
            connection?.Dispose();
            channel.Dispose();
            throw new IOException($"The AppHost resource service connection failed ({error.StatusCode}): {AspireClient.SanitizeDiagnostic(error.Status.Detail, apiKey)}");
        }
        catch
        {
            connection?.Dispose();
            channel.Dispose();
            throw;
        }
    }

    private async Task ListenAsync()
    {
        // The subscription is idle for the AppHost's lifetime; only each delivered UI update holds BackgroundWork.
        try
        {
            while (await call!.ResponseStream.MoveNext(cancellation).ConfigureAwait(false))
            {
                WatchInteractionsResponseUpdate update = call.ResponseStream.Current;
                using IDisposable held = BackgroundWork.Scope();
                await UiThread.InvokeAsync(() =>
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        Apply(update);
                    }
                    return true;
                }).WaitAsync(cancellation).ConfigureAwait(false);
            }
            if (!lifetime.IsCancellationRequested)
            {
                throw new IOException("The AppHost interaction stream ended.");
            }
        }
        catch (Exception error) when (error is RpcException or OperationCanceledException or IOException or InvalidDataException or NotSupportedException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                string message = error is RpcException rpc
                    ? $"The AppHost interaction connection failed ({rpc.StatusCode})." : error.Message;
                using IDisposable held = BackgroundWork.Scope();
                await UiThread.InvokeAsync(() =>
                {
                    Failure = message;
                    Failed?.Invoke(this, message);
                    return true;
                }).WaitAsync(cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            bool canceled = lifetime.IsCancellationRequested;
            lifetime.Cancel();
            if (!canceled && Volatile.Read(ref disposed) == 0)
            {
                UiThread.Post(CloseDialogs);
            }
        }
    }

    private void Apply(WatchInteractionsResponseUpdate update)
    {
        UiThread.Check();
        update = update.Clone();
        update.Title = $"{appName} · {update.Title}";
        if (update.Complete is not null)
        {
            if (dialogs.Remove(update.InteractionId, out AspireInteractionDialog? closed))
            {
                closed.Complete();
            }
            return;
        }
        if (update.KindCase == WatchInteractionsResponseUpdate.KindOneofCase.None)
        {
            throw new NotSupportedException("The AppHost sent an unsupported interaction kind. No response was sent.");
        }
        if (dialogs.TryGetValue(update.InteractionId, out AspireInteractionDialog? existing))
        {
            existing.Apply(update);
            return;
        }
        if (dialogs.Count >= DialogLimit)
        {
            throw new InvalidDataException("The AppHost exceeded 16 simultaneous interactions. The interaction connection was closed.");
        }
        CancellationTokenSource dialogLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        CancellationToken dialogCancellation = dialogLifetime.Token;
        bool released = false;
        try
        {
            AspireInteractionDialog dialog = new(update, response => SendAsync(response, dialogCancellation),
                (input, file) => UploadAsync(update.InteractionId, input, file, dialogCancellation), dialogCancellation);
            dialogs.Add(update.InteractionId, dialog);
            dialog.Closed += (_, _) => ReleaseDialog();
            if (update.Notification is not null)
            {
                dialog.Show(owner);
            }
            else
            {
                IdeErrors.Observe(dialog.ShowDialog(owner));
            }
        }
        catch
        {
            ReleaseDialog();
            throw;
        }

        void ReleaseDialog()
        {
            if (released)
            {
                return;
            }
            released = true;
            dialogs.Remove(update.InteractionId);
            dialogLifetime.Cancel();
            dialogLifetime.Dispose();
        }
    }

    private async Task SendAsync(WatchInteractionsRequestUpdate response, CancellationToken cancel)
    {
        await writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            cancel.ThrowIfCancellationRequested();
            // A committed write belongs to the shared connection, not the closing dialog.
            await call!.RequestStream.WriteAsync(response, cancellation).ConfigureAwait(false);
        }
        finally
        {
            writing.Release();
        }
    }

    private async Task<string> UploadAsync(int interactionId, InteractionInput input, string file, CancellationToken cancel)
    {
        long limit = input.MaxFileSize > 0 ? Math.Min(input.MaxFileSize, FileLimit) : FileLimit;
        if (new FileInfo(file).Length > limit)
        {
            throw new InvalidDataException($"The selected file exceeds the {limit} byte upload limit.");
        }
        using AsyncClientStreamingCall<UploadFileChunk, UploadFileResponse> upload = client.UploadFile(headers, cancellationToken: cancel);
        await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        bool first = true;
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            total += count;
            if (total > limit)
            {
                throw new InvalidDataException("The selected file exceeded its upload limit while reading.");
            }
            if (count == 0 && !first)
            {
                break;
            }
            UploadFileChunk chunk = new() { Data = ByteString.CopyFrom(buffer, 0, count) };
            if (first)
            {
                chunk.FileName = Path.GetFileName(file);
                chunk.InteractionId = interactionId;
                chunk.InputName = input.Name;
            }
            await upload.RequestStream.WriteAsync(chunk, cancel).ConfigureAwait(false);
            first = false;
            if (count == 0)
            {
                break;
            }
        }
        await upload.RequestStream.CompleteAsync().ConfigureAwait(false);
        return (await upload.ResponseAsync.ConfigureAwait(false)).FileId;
    }

    /// <summary>Collects typed command arguments, revalidating both the native snapshot and selection before execution; its
    /// confirmation asked of the person unless <paramref name="confirmed"/> (an agent's call, which is its own).</summary>
    public async Task<ResourceCommandResponse?> ExecuteCommandAsync(AspireClient snapshots, AspireSelection selected,
        string commandName, Func<bool> isCurrent, CancellationToken cancel, bool confirmed = false)
    {
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancel, cancellation);
        CancellationToken token = operation.Token;
        await commanding.WaitAsync(token).ConfigureAwait(false);
        try
        {
            (Resource resource, ResourceCommand command) metadata;
            using (BackgroundWork.Scope())
            {
                await ValidateSelectionAsync(snapshots, selected, commandName, isCurrent, token).ConfigureAwait(false);
                metadata = await ReadCommandAsync(selected.Resource.Name, commandName, token).ConfigureAwait(false);
            }
            (Resource resource, ResourceCommand command) = metadata;
            if (!confirmed && command.HasConfirmationMessage && command.ConfirmationMessage.Length > 0
                && !await ConfirmAsync(command, token).ConfigureAwait(false))
            {
                return null;
            }
            using (BackgroundWork.Scope())
            {
                await ValidateSelectionAsync(snapshots, selected, commandName, isCurrent, token).ConfigureAwait(false);
                (Resource currentResource, ResourceCommand currentCommand) = await ReadCommandAsync(selected.Resource.Name, commandName, token).ConfigureAwait(false);
                if (currentResource.Uid != resource.Uid || !currentCommand.Equals(command))
                {
                    throw new InvalidOperationException("The resource command changed. Refresh before executing.");
                }
            }
            await UiThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!isCurrent())
                {
                    throw new OperationCanceledException(token);
                }
                return true;
            }).WaitAsync(token).ConfigureAwait(false);
            // It may wait for the person at the AppHost's prompt, which holding BackgroundWork would keep from drawing.
            token.ThrowIfCancellationRequested();
            return await client.ExecuteResourceCommandAsync(new ResourceCommandRequest
            {
                ResourceName = resource.Name,
                ResourceType = resource.ResourceType,
                CommandName = command.Name,
            }, headers, cancellationToken: token).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException error) when (token.IsCancellationRequested && error.StatusCode == StatusCode.Cancelled)
        {
            throw new OperationCanceledException(token);
        }
        catch (RpcException error)
        {
            throw new IOException($"The AppHost resource command failed ({error.StatusCode}).");
        }
        finally
        {
            operation.Cancel();
            commanding.Release();
        }
    }

    private async Task<bool> ConfirmAsync(ResourceCommand command, CancellationToken cancel)
    {
        TaskCompletionSource<bool> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AspireInteractionDialog? dialog = null;
        int finished = 0;
        try
        {
            await UiThread.InvokeAsync(() =>
            {
                cancel.ThrowIfCancellationRequested();
                if (Volatile.Read(ref finished) != 0)
                {
                    return false;
                }
                dialog = new AspireInteractionDialog(new WatchInteractionsResponseUpdate
                {
                    InteractionId = -1,
                    Title = $"{appName} · {command.DisplayName}",
                    Message = command.ConfirmationMessage,
                    ShowDismiss = true,
                    ShowSecondaryButton = true,
                    MessageBox = new InteractionMessageBox(),
                }, response =>
                {
                    result.TrySetResult(response.MessageBox?.Result == true);
                    dialog!.Complete();
                    return Task.CompletedTask;
                }, (_, _) => throw new InvalidOperationException("Confirmation dialogs do not accept files."), cancel);
                if (cancel.IsCancellationRequested || Volatile.Read(ref finished) != 0)
                {
                    dialog.Complete();
                    return false;
                }
                dialog.Closed += (_, _) => result.TrySetResult(false);
                IdeErrors.Observe(dialog.ShowDialog(owner));
                return true;
            }).WaitAsync(cancel).ConfigureAwait(false);
            return await result.Task.WaitAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref finished, 1);
            if (Dispatcher.UIThread.CheckAccess())
            {
                dialog?.Complete();
            }
            else
            {
                UiThread.Post(() => dialog?.Complete());
            }
        }
    }

    private async Task ValidateSelectionAsync(AspireClient snapshots, AspireSelection selected, string command,
        Func<bool> isCurrent, CancellationToken cancel)
    {
        AspireResource? current = (await GetResourcesAsync(cancel).ConfigureAwait(false))
            .FirstOrDefault(resource => resource.Name == selected.Resource.Name);
        if (current is null || current.Generation != selected.Resource.Generation
            || current.Commands.FirstOrDefault(item => item.Name == command) is not { Enabled: true })
        {
            throw new InvalidOperationException("The resource or command changed. Refresh before executing.");
        }
        if (!await UiThread.InvokeAsync(isCurrent).WaitAsync(cancel).ConfigureAwait(false))
        {
            throw new OperationCanceledException(cancel);
        }
    }

    private async Task<(Resource Resource, ResourceCommand Command)> ReadCommandAsync(string resourceName, string commandName, CancellationToken cancel)
    {
        await GetResourcesAsync(cancel).ConfigureAwait(false);
        lock (resourceSync)
        {
            if (!watchedResources.TryGetValue(resourceName, out (Resource Protocol, AspireResource Snapshot) current))
            {
                throw new InvalidOperationException("The resource no longer exists.");
            }
            Resource resource = current.Protocol;
            ResourceCommand? command = resource.Commands.FirstOrDefault(item => item.Name == commandName);
            if (resource.IsHidden || command is null || command.State != ResourceCommandState.Enabled)
            {
                throw new InvalidOperationException("The resource command is no longer available.");
            }
            return (resource.Clone(), command.Clone());
        }
    }

    private void CloseDialogs()
    {
        foreach (AspireInteractionDialog dialog in dialogs.Values.ToArray())
        {
            dialog.Complete();
        }
        dialogs.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        lifetime.Cancel();
        call?.Dispose();
        channel.Dispose();
        if (Dispatcher.UIThread.CheckAccess())
        {
            CloseDialogs();
        }
        else
        {
            UiThread.Post(CloseDialogs);
        }
        IdeErrors.Observe(Task.WhenAll(listening, resourceListening).ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await Task.WhenAll(listening, resourceListening).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }
}
