using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>A loopback implementation of Aspire's 2024-03-03 IDE execution protocol.</summary>
public sealed class AspireExecutionServer : IAsyncDisposable
{
    internal const string FileServiceUnsupported = "File-based Aspire services are not supported by Passenger";
    private const string InstanceHeader = "Microsoft-Developer-DCP-Instance-ID";
    private const string BaselineProtocol = "2024-03-03";
    private const int MaximumInstances = 64;
    private const int MaximumSessions = 128;
    private const int MaximumLogCharacters = 16384;
    private const int MaximumQueuedLogs = 224;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };
    private static readonly AspireEndpointInfo Info = new([BaselineProtocol], ["project"]);
    private readonly object sync = new();
    private readonly Dictionary<string, Instance> instances = new(StringComparer.Ordinal);
    private readonly List<Exception> cleanupErrors = [];
    private readonly CancellationTokenSource shutdown = new();
    private readonly Func<AspireProjectLaunch, CancellationToken, Task<AspireProjectSession>> createSession;
    private readonly Func<AspireProjectSession, CancellationToken, Task> stopSession;
    private readonly byte[] tokenHash;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Dictionary<AspireExecutionConnection, Task> connections = [];
    private Task accepting = Task.CompletedTask;
    private Task? disposal;

    private AspireExecutionServer(
        Func<AspireProjectLaunch, CancellationToken, Task<AspireProjectSession>> createSession,
        Func<AspireProjectSession, CancellationToken, Task> stopSession)
    {
        this.createSession = createSession;
        this.stopSession = stopSession;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(Token));
    }

    public int Port { get; private set; }
    public string Token { get; }
    public string SessionInfo => JsonSerializer.Serialize(Info, JsonOptions);

    public ImmutableDictionary<string, string> Environment => new Dictionary<string, string>
    {
        [AspireVariables.DebugSessionPort] = $"localhost:{Port.ToString(CultureInfo.InvariantCulture)}",
        [AspireVariables.DebugSessionToken] = Token,
        [AspireVariables.DebugSessionInfo] = SessionInfo
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Callbacks marshal to the UI if needed and must observe cancellation. A failed creation must clean up its own
    /// runner. The creation callback may publish events using the request's instance and session IDs before returning its
    /// handle. Different resources' creations run at once, one resource's creates and deletes in the order DCP sent them, and
    /// a session's delete waits for its own creation alone.</summary>
    public static async Task<AspireExecutionServer> StartAsync(
        Func<AspireProjectLaunch, CancellationToken, Task<AspireProjectSession>> createSession,
        Func<AspireProjectSession, CancellationToken, Task> stopSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createSession);
        ArgumentNullException.ThrowIfNull(stopSession);
        AspireExecutionServer server = new(createSession, stopSession);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            server.listener.Start();
            server.Port = ((IPEndPoint)server.listener.LocalEndpoint).Port;
            server.accepting = server.AcceptAsync();
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool NotifyProcessStarted(string instanceId, string sessionId, int? processId) =>
        Publish(instanceId, sessionId, new("processRestarted", sessionId, ProcessId: processId));

    public bool NotifyProcessTerminated(string instanceId, string sessionId, uint? exitCode = null) =>
        Publish(instanceId, sessionId, new("sessionTerminated", sessionId, ExitCode: exitCode));

    /// <summary>Returns false if the session ended or its notification connection cannot keep up.</summary>
    public bool NotifyServiceLog(string instanceId, string sessionId, string message, bool isStandardError = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        bool accepted = true;
        for (int offset = 0; offset < message.Length || (offset == 0 && message.Length == 0);)
        {
            int length = Math.Min(MaximumLogCharacters, message.Length - offset);
            if (length > 0 && offset + length < message.Length && char.IsHighSurrogate(message[offset + length - 1]))
            {
                length--;
            }
            accepted &= Publish(instanceId, sessionId, new("serviceLogs", sessionId,
                IsStandardError: isStandardError, LogMessage: message.Substring(offset, length)));
            if (!accepted || length == 0)
            {
                break;
            }
            offset += length;
        }
        return accepted;
    }

    private bool Publish(string instanceId, string sessionId, AspireNotification notification)
    {
        lock (sync)
        {
            if (!instances.TryGetValue(instanceId, out Instance? instance) || instance.Token.IsCancellationRequested ||
                !instance.Sessions.TryGetValue(sessionId, out Session? session) || session.Terminated)
            {
                return false;
            }
            if (notification.Type == "sessionTerminated")
            {
                session.Terminated = true;
                if (session.Handle is not null)
                {
                    instance.Sessions.Remove(sessionId);
                }
            }
            else if (notification.Type == "processRestarted")
            {
                session.Started = true;
            }
            if (notification.Type == "serviceLogs" && instance.Notifications.Reader.Count >= MaximumQueuedLogs)
            {
                return false;
            }
            if (instance.Notifications.Writer.TryWrite(notification))
            {
                return true;
            }
            if (notification.Type == "serviceLogs")
            {
                return false;
            }
            // A lost lifecycle event leaves DCP with an incorrect model; fail this connection instead of dropping it.
            instance.Notifications.Writer.TryComplete(new IOException("The notification consumer is too slow."));
            return false;
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(shutdown.Token).ConfigureAwait(false);
                lock (sync)
                {
                    if (shutdown.IsCancellationRequested)
                    {
                        client.Dispose();
                        return;
                    }
                    AspireExecutionConnection connection = new(client, shutdown.Token);
                    connections.Add(connection, HandleConnectionAsync(connection));
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (SocketException) when (shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task HandleConnectionAsync(AspireExecutionConnection context)
    {
        await Task.Yield();
        try
        {
            await HandleRequestAsync(context).ConfigureAwait(false);
            await context.WriteResponseAsync([]).ConfigureAwait(false);
            await context.CloseAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || shutdown.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) when (context.RequestAborted.IsCancellationRequested || shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (sync)
            {
                cleanupErrors.Add(error);
            }
        }
        finally
        {
            try
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    cleanupErrors.Add(error);
                }
            }
            finally
            {
                lock (sync)
                {
                    connections.Remove(context);
                }
            }
        }
    }

    private async Task HandleRequestAsync(AspireExecutionConnection context)
    {
        try
        {
            await context.ReadHeadAsync().ConfigureAwait(false);
            if (!Authenticated(context))
            {
                context.ResponseHeaders["WWW-Authenticate"] = "Bearer";
                await ErrorAsync(context, 401, "Unauthorized", "A valid bearer token is required.").ConfigureAwait(false);
                return;
            }
            string? instanceId = GetInstanceId(context);
            if (instanceId is null)
            {
                await ErrorAsync(context, 400, "InvalidDcpInstanceId", "A valid DCP instance ID is required.").ConfigureAwait(false);
                return;
            }
            if (context.Path.Equals("/info", StringComparison.OrdinalIgnoreCase) && context.Method == "GET")
            {
                await context.WriteJsonAsync(Info, JsonOptions).ConfigureAwait(false);
                return;
            }
            if (!ValidVersion(context))
            {
                await ErrorAsync(context, 400, "UnsupportedProtocol", "Use an api-version of 2024-03-03 or later.").ConfigureAwait(false);
                return;
            }
            if (context.Path.Equals("/run_session/notify", StringComparison.OrdinalIgnoreCase) && context.Method == "GET")
            {
                await SubscribeAsync(context, instanceId).ConfigureAwait(false);
                return;
            }
            if (context.Path.Equals("/run_session", StringComparison.OrdinalIgnoreCase) && context.Method == "PUT")
            {
                await CreateAsync(context, instanceId).ConfigureAwait(false);
                return;
            }
            if (context.Path.StartsWith("/run_session/", StringComparison.OrdinalIgnoreCase) &&
                context.Method == "DELETE" && context.Path.Length > 13 && !context.Path.AsSpan(13).Contains('/'))
            {
                await DeleteAsync(context, instanceId, context.Path[13..]).ConfigureAwait(false);
                return;
            }
            await ErrorAsync(context, 404, "NotFound", "The endpoint does not exist.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || shutdown.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (AspireExecutionConnection.RequestException error)
        {
            await ErrorAsync(context, error.StatusCode, "InvalidRequest", "The request exceeds a limit or is malformed.").ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await ErrorAsync(context, 400, "InvalidJson", "The request body is not a valid run session request.").ConfigureAwait(false);
        }
        catch (LaunchException)
        {
            // A start refused (a service marked Don't start), which its row says in the IDE's words.
            await ErrorAsync(context, 500, "SessionOperationFailed", "The session operation failed.").ConfigureAwait(false);
        }
        catch (Exception error)
        {
            IdeErrors.Write($"Aspire's execution server: a session operation failed: {error}");
            await ErrorAsync(context, 500, "SessionOperationFailed", "The session operation failed.").ConfigureAwait(false);
        }
    }

    private bool Authenticated(AspireExecutionConnection context)
    {
        if (context.Header("Authorization").Count != 1)
        {
            return false;
        }
        string? authorization = context.Header("Authorization")[0];
        if (authorization is null || authorization.Length > 256 ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        byte[] candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]));
        return CryptographicOperations.FixedTimeEquals(candidateHash, tokenHash);
    }

    private static string? GetInstanceId(AspireExecutionConnection context)
    {
        if (context.Header(InstanceHeader).Count != 1)
        {
            return null;
        }
        string? id = context.Header(InstanceHeader)[0];
        if (id is null || id.Length is < 12 or > 128)
        {
            return null;
        }
        foreach (char character in id)
        {
            if (character is < '!' or > '~' or ',')
            {
                return null;
            }
        }
        foreach (char character in id.AsSpan(id.Length - 12))
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9'))
            {
                return null;
            }
        }
        return id;
    }

    private static bool ValidVersion(AspireExecutionConnection context)
    {
        string? version = context.Version;
        return DateOnly.TryParseExact(version, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
            string.CompareOrdinal(version, BaselineProtocol) >= 0;
    }

    private async Task CreateAsync(AspireExecutionConnection context, string instanceId)
    {
        if (!context.HasJsonContentType())
        {
            await ErrorAsync(context, 415, "UnsupportedContentType", "Use application/json.").ConfigureAwait(false);
            return;
        }
        byte[] body = await context.ReadBodyAsync().ConfigureAwait(false);
        AspireRunSessionRequest? payload = JsonSerializer.Deserialize<AspireRunSessionRequest>(body, JsonOptions);
        string sessionId = Guid.NewGuid().ToString("N");
        AspireProjectLaunch? launch = ParseLaunch(payload, instanceId, sessionId);
        if (launch is null)
        {
            await ErrorAsync(context, 400, "InvalidLaunchConfiguration", "A valid project launch configuration and overlays are required.").ConfigureAwait(false);
            return;
        }
        if (Path.GetExtension(launch.ProjectPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            await ErrorAsync(context, 400, "UnsupportedProjectType", FileServiceUnsupported).ConfigureAwait(false);
            return;
        }
        Instance? instance;
        lock (sync)
        {
            instances.TryGetValue(instanceId, out instance);
        }
        if (instance is null)
        {
            await ErrorAsync(context, 409, "NotificationsRequired", "Subscribe to notifications before creating a session.").ConfigureAwait(false);
            return;
        }
        context.MonitorPendingCreate();
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, instance.Token);
        string resource = ResourceOf(launch);
        Task before = Task.CompletedTask;
        Operation operation = null!;
        lock (sync)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (instance.Sessions.Count >= MaximumSessions)
            {
                sessionId = "";
            }
            else
            {
                // Its resource's earlier create or delete (a restart's stop) ends before it begins; other resources' go at once.
                before = instance.Line(resource);
                operation = instance.Begin(resource);
                instance.Sessions.Add(sessionId, new Session { Resource = resource, Creating = operation.Done });
            }
        }
        if (sessionId.Length == 0)
        {
            await ErrorAsync(context, 429, "SessionLimit", "Too many sessions are active for this DCP instance.").ConfigureAwait(false);
            return;
        }
        try
        {
            await before.WaitAsync(lifetime.Token).ConfigureAwait(false);
            AspireProjectSession handle = await createSession(launch, lifetime.Token).ConfigureAwait(false);
            bool terminated;
            lock (sync)
            {
                Session session = instance.Sessions[sessionId];
                session.Handle = handle;
                terminated = session.Terminated;
                if (terminated)
                {
                    instance.Sessions.Remove(sessionId);
                }
                else if (!session.Started)
                {
                    NotifyProcessStarted(instanceId, sessionId, handle.ProcessId);
                }
            }
            lifetime.Token.ThrowIfCancellationRequested();
            await context.CommitCreateAsync($"http://localhost:{Port}/run_session/{sessionId}", lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            Session? failed;
            lock (sync)
            {
                instance.Sessions.Remove(sessionId, out failed);
            }
            if (failed?.Handle is not null && !failed.Terminated)
            {
                await StopForCleanupAsync(failed.Handle).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            End(instance, operation);
        }
    }

    /// <summary>Which of the AppHost's resources a start is, whose creates and deletes keep the order DCP sends them in (a
    /// restart's stop before its start, a resource's replicas one after another): its name, as DCP tells a service it, else
    /// its project.</summary>
    private static string ResourceOf(AspireProjectLaunch launch) =>
        launch.Environment.FirstOrDefault(variable => variable.Name == OtelVariables.ServiceName)?.Value is { Length: > 0 } name ? name : launch.ProjectPath;

    private void End(Instance instance, Operation operation)
    {
        lock (sync)
        {
            instance.End(operation);
        }
    }

    private static AspireProjectLaunch? ParseLaunch(AspireRunSessionRequest? payload, string instanceId, string sessionId)
    {
        if (payload is null || payload.LaunchConfigurations.IsDefaultOrEmpty)
        {
            return null;
        }
        AspireProjectConfiguration? project = payload.LaunchConfigurations.FirstOrDefault(configuration => configuration?.Type == "project");
        if (project is null || string.IsNullOrWhiteSpace(project.ProjectPath) ||
            !Path.IsPathFullyQualified(project.ProjectPath) || project.ProjectPath.Contains('\0') ||
            project.Mode is not (null or "Debug" or "NoDebug"))
        {
            return null;
        }
        ImmutableArray<AspireEnvironmentVariable>.Builder environment = ImmutableArray.CreateBuilder<AspireEnvironmentVariable>();
        foreach (AspireEnvironmentEntry? entry in payload.Environment ?? [])
        {
            if (entry is null || string.IsNullOrEmpty(entry.Name) || entry.Name.IndexOfAny(['=', '\0']) >= 0 ||
                entry.Value is null || entry.Value.Contains('\0'))
            {
                return null;
            }
            environment.Add(new(entry.Name, entry.Value));
        }
        ImmutableArray<string>? arguments = null;
        if (payload.Arguments is { } suppliedArguments)
        {
            ImmutableArray<string>.Builder values = ImmutableArray.CreateBuilder<string>();
            foreach (string? argument in suppliedArguments)
            {
                if (argument is null || argument.Contains('\0'))
                {
                    return null;
                }
                values.Add(argument);
            }
            arguments = values.ToImmutable();
        }
        return new(instanceId, sessionId, project.ProjectPath, project.Mode ?? "Debug", project.LaunchProfile,
            project.DisableLaunchProfile, environment.ToImmutable(), arguments);
    }

    private async Task DeleteAsync(AspireExecutionConnection context, string instanceId, string sessionId)
    {
        Instance? instance;
        lock (sync)
        {
            instances.TryGetValue(instanceId, out instance);
        }
        if (instance is null)
        {
            context.StatusCode = 204;
            return;
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, instance.Token);
        Session? session;
        Operation operation;
        lock (sync)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!instance.Sessions.TryGetValue(sessionId, out session))
            {
                context.StatusCode = 204;
                return;
            }
            // It waits for its own session's create alone; its resource's next create (a restart's start) waits for it.
            operation = instance.Begin(session.Resource);
        }
        try
        {
            await session.Creating.WaitAsync(lifetime.Token).ConfigureAwait(false);
            lock (sync)
            {
                instance.Sessions.TryGetValue(sessionId, out session);
            }
            if (session?.Handle is null)
            {
                context.StatusCode = 204;
                return;
            }
            await stopSession(session.Handle, lifetime.Token).ConfigureAwait(false);
            NotifyProcessTerminated(instanceId, sessionId);
            context.StatusCode = 200;
        }
        finally
        {
            End(instance, operation);
        }
    }

    private async Task SubscribeAsync(AspireExecutionConnection context, string instanceId)
    {
        if (!context.IsWebSocketRequest())
        {
            await ErrorAsync(context, 400, "WebSocketRequired", "Use a WebSocket upgrade request.").ConfigureAwait(false);
            return;
        }
        Instance? instance = null;
        lock (sync)
        {
            if (!shutdown.IsCancellationRequested && instances.Count < MaximumInstances && !instances.ContainsKey(instanceId))
            {
                instance = new Instance(shutdown.Token);
                instances.Add(instanceId, instance);
            }
        }
        if (instance is null)
        {
            await ErrorAsync(context, 409, "NotificationConnectionUnavailable", "This instance is already subscribed or the server is at capacity.").ConfigureAwait(false);
            return;
        }
        try
        {
            using WebSocket socket = await context.AcceptWebSocketAsync().ConfigureAwait(false);
            using CancellationTokenSource connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, instance.Token);
            using CancellationTokenSource receive = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            Task receiving = ReceiveAsync(socket, receive.Token);
            Task sending = SendAsync(socket, instance.Notifications.Reader, connection.Token);
            try
            {
                await Task.WhenAny(receiving, sending).ConfigureAwait(false);
            }
            finally
            {
                await instance.Lifetime.CancelAsync().ConfigureAwait(false);
                await connection.CancelAsync().ConfigureAwait(false);
                try
                {
                    await sending.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (connection.IsCancellationRequested)
                {
                }
                catch (WebSocketException)
                {
                }
                finally
                {
                    await receive.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await receiving.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (receive.IsCancellationRequested)
                    {
                    }
                    catch (WebSocketException)
                    {
                    }
                }
            }
        }
        finally
        {
            await CleanupInstanceAsync(instanceId, instance).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            // DCP sends no application data; WebSocket handles control frames itself.
            if (result.Count != 0 || !result.EndOfMessage)
            {
                return;
            }
        }
    }

    private static async Task SendAsync(WebSocket socket, ChannelReader<AspireNotification> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AspireNotification notification in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notification, JsonOptions) + "\n");
                using CancellationTokenSource send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                send.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, send.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using CancellationTokenSource close = new(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session connection ended", close.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    socket.Abort();
                }
                catch (WebSocketException)
                {
                    socket.Abort();
                }
            }
        }
    }

    /// <summary>Ends one DCP instance without affecting other AppHosts sharing this endpoint.</summary>
    public async Task StopInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        Instance? instance;
        Task cancel;
        lock (sync)
        {
            instances.TryGetValue(instanceId, out instance);
            cancel = instance?.Lifetime.CancelAsync() ?? Task.CompletedTask;
        }
        if (instance is not null)
        {
            await cancel.ConfigureAwait(false);
            await instance.Ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CleanupInstanceAsync(string instanceId, Instance instance)
    {
        await instance.Lifetime.CancelAsync().ConfigureAwait(false);
        // Its creates and deletes under way end first, each told the instance ended; none begins once it has.
        Task[] running;
        lock (sync)
        {
            running = instance.Running();
        }
        await Task.WhenAll(running).ConfigureAwait(false);
        try
        {
            AspireProjectSession[] handles;
            lock (sync)
            {
                handles = instance.Sessions.Values.Where(session => !session.Terminated && session.Handle is not null)
                    .Select(session => session.Handle!).ToArray();
                instance.Sessions.Clear();
            }
            foreach (AspireProjectSession handle in handles)
            {
                await StopForCleanupAsync(handle).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (sync)
            {
                instances.Remove(instanceId);
                instance.Lifetime.Dispose();
            }
            instance.Notifications.Writer.TryComplete();
            instance.Ended.TrySetResult();
        }
    }

    private async Task StopForCleanupAsync(AspireProjectSession handle)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await stopSession(handle, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (sync)
            {
                cleanupErrors.Add(error);
            }
        }
    }

    private static Task ErrorAsync(AspireExecutionConnection context, int status, string code, string message)
    {
        if (context.HasResponded)
        {
            context.Abort();
            return Task.CompletedTask;
        }
        context.StatusCode = status;
        return context.WriteJsonAsync(new AspireErrorResponse(new(code, message)), JsonOptions);
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        try
        {
            await accepting.ConfigureAwait(false);
        }
        finally
        {
            Task[] handlers;
            lock (sync)
            {
                handlers = connections.Values.ToArray();
                foreach (AspireExecutionConnection connection in connections.Keys)
                {
                    connection.Abort();
                }
            }
            await Task.WhenAll(handlers).ConfigureAwait(false);
            shutdown.Dispose();
        }
        lock (sync)
        {
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("One or more Aspire project sessions could not be stopped.", cleanupErrors);
            }
        }
    }

    private sealed class Instance
    {
        public Instance(CancellationToken shutdown)
        {
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            Token = Lifetime.Token;
        }

        public CancellationTokenSource Lifetime { get; }
        public CancellationToken Token { get; }
        public Dictionary<string, Session> Sessions { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<AspireNotification> Notifications { get; } = Channel.CreateBounded<AspireNotification>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        /// <summary>The creates and deletes under way, which its end waits out.</summary>
        private readonly HashSet<Operation> operations = [];

        /// <summary>Each resource's operations so far, which its next create waits for: DCP's order within one resource.</summary>
        private readonly Dictionary<string, Task> lines = new(StringComparer.Ordinal);

        /// <summary>What a resource's next create waits for. Under the server's lock.</summary>
        public Task Line(string resource) => lines.GetValueOrDefault(resource) ?? Task.CompletedTask;

        /// <summary>A create or delete of a resource begins, refused once the instance has ended. Under the server's lock.</summary>
        public Operation Begin(string resource)
        {
            Token.ThrowIfCancellationRequested();
            Operation operation = new(resource);
            operations.Add(operation);
            Task line = Line(resource);
            lines[resource] = line.IsCompleted ? operation.Done : Task.WhenAll(line, operation.Done);
            return operation;
        }

        /// <summary>An operation has ended, whatever came of it. Under the server's lock.</summary>
        public void End(Operation operation)
        {
            operations.Remove(operation);
            operation.Finished.TrySetResult();
            if (lines.TryGetValue(operation.Resource, out Task? line) && (line == operation.Done || line.IsCompleted))
            {
                lines.Remove(operation.Resource);
            }
        }

        /// <summary>The operations under way. Under the server's lock.</summary>
        public Task[] Running() => [.. operations.Select(operation => operation.Done)];
    }

    /// <summary>One create or delete of a resource's session.</summary>
    private sealed class Operation(string resource)
    {
        public string Resource { get; } = resource;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Done => Finished.Task;
    }

    private sealed class Session
    {
        /// <summary>The resource it starts (<c>ResourceOf</c>).</summary>
        public required string Resource { get; init; }

        /// <summary>Its create, which a delete of it waits for, ending it once made.</summary>
        public required Task Creating { get; init; }
        public AspireProjectSession? Handle { get; set; }
        public bool Terminated { get; set; }
        public bool Started { get; set; }
    }
}
