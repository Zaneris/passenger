using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary><c>--mcp-attach [PID] [FOLDER]</c>: the agent's server for its whole session, standing in for a person's
/// window. It answers initialize and tools/list itself, from the build's own tools (<see cref="McpHost.ToolMethods"/>),
/// and joins a window only when a tool is called: the innermost whose folder holds FOLDER (else the agent's working
/// folder), never one on an unrelated folder; with a PID, that process's windows alone, the newest when none holds it.
/// With no window to join it has the running Passenger open the folder (<see cref="AppSocket"/>), else starts Passenger
/// on it, and waits for that folder's window, which lets the agent in or asks the person first. A window that closes, or
/// that opens another folder than the one it was joined on (but by the agent's own open), is joined again, or another
/// started, at the next call. Resources and prompts are none, here as in the window.</summary>
public sealed class McpProxy
{
    /// <summary><c>headless</c> has a call with no window start this build headless, as its own child, in the state
    /// PASSENGER_SETTINGS names, never the person's installed Passenger.</summary>
    [IdentifierHome]
    public const string StartVariable = "PASSENGER_START";

    /// <summary>What the proxy tells a Passenger it starts for an agent (<c>agent</c>), which takes it out of its own
    /// environment as it starts, so nothing it runs inherits it.</summary>
    [IdentifierHome]
    public const string StartedForVariable = "PASSENGER_STARTED_FOR";

    /// <summary>Whether an agent's --mcp-attach started this Passenger, to open a folder for it: its window shows the person
    /// no What's New, which waits for a start of their own.</summary>
    public static bool StartedForAgent { get; private set; }

    /// <summary>How long joining may take, including old connection cleanup, opening a window and initialization.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long a call's failure waits for its connection's end to be told, as the thread pool may run it late on a
    /// busy machine; only a failure that is no such end waits it out.</summary>
    private static readonly TimeSpan EndingLimit = TimeSpan.FromSeconds(5);

    private readonly int? processId;
    private readonly string here;
    private readonly SemaphoreSlim joining = new(1, 1);
    private McpClient? joined;

    /// <summary>The calls on the window that the agent asked progress of, by the token the window tells it under.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProgressToken, AgentCall> told = new();

    /// <summary>The socket <see cref="joined"/> talks over.</summary>
    private Socket? joinedSocket;

    /// <summary>The joined window's socket file, beside which it writes its folder.</summary>
    private string? joinedPath;

    /// <summary>The folder the joined window had open as it was joined, or as the agent's own open left it: the agent's to
    /// work in there, and no other the person opens in that window.</summary>
    private string? joinedOn;

    /// <summary>The agent's own opens under way on the joined window, which move it on purpose.</summary>
    private int opening;

    /// <summary>The headless Passenger a call started (<see cref="StartVariable"/>), which ends with this
    /// one.</summary>
    private Process? headless;

    private McpProxy(int? processId, string here)
    {
        this.processId = processId;
        this.here = here;
    }

    /// <summary>Fails a start whose <see cref="StartVariable"/> or <see cref="StartedForVariable"/> is not one this build
    /// knows, as a malformed flag does; the second is read into <see cref="StartedForAgent"/> and taken out.</summary>
    public static void CheckVariable()
    {
        if (Environment.GetEnvironmentVariable(StartVariable) is { Length: > 0 } how && how != "headless")
        {
            throw new FormatException($"{StartVariable} takes headless (the test run's), not '{how}'");
        }
        if (Environment.GetEnvironmentVariable(StartedForVariable) is { Length: > 0 } started)
        {
            if (started != "agent")
            {
                throw new FormatException($"{StartedForVariable} takes agent (a Passenger an agent's --mcp-attach started), not '{started}'");
            }
            StartedForAgent = true;
            Environment.SetEnvironmentVariable(StartedForVariable, null);
        }
    }

    /// <summary>Serves the agent on standard input and output until it goes, then lets go of the window it joined.</summary>
    public static int Run(int? processId, string? folder)
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        // Standard output is the protocol's alone, and standard error the agent may never read: the console goes to the log.
        IdeLog.TakeConsole();
        McpProxy proxy = new(processId, PathNames.Canonical(folder ?? Environment.CurrentDirectory));
        McpServerPrimitiveCollection<McpServerTool> tools = [];
        foreach (McpServerTool tool in McpHost.Tools)
        {
            // Never called: each call goes on to the window's own tool of the name.
            tools.Add(new ForwardedTool(tool, proxy));
        }
        McpServerOptions options = new()
        {
            ServerInfo = McpHost.ServerInfo,
            ServerInstructions = McpHost.Instructions,
            ToolCollection = tools,
        };
        using CancellationTokenSource gone = new();
        StreamServerTransport transport = new(input, output, "passenger");
        McpServer server = McpServer.Create(new PeerTransport(transport), options);
        Task running = server.RunAsync(gone.Token);
        // The server waits out calls still going as the agent goes, so its going, or SIGTERM, is watched for here.
        TaskCompletionSource terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using System.Runtime.InteropServices.PosixSignalRegistration? signal = McpHost.EndOnSignal(() => terminated.TrySetResult());
        Task.WaitAny(running, transport.MessageReader.Completion, terminated.Task);
        gone.Cancel();
        proxy.Leave();
        try
        {
            running.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // What the agent's going cut short.
        }
        return 0;
    }

    /// <summary>One call of the agent's, on the window it is joined to: told to the agent while it joins one, and the
    /// window's progress for it passed on, when the agent asked for progress.</summary>
    private async Task<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancel)
    {
        string tool = request.Params?.Name ?? "a tool";
        await using AgentCall call = AgentCall.Begin(request.Server, request.Params?.ProgressToken, tool, $"Joining the Passenger window on {here}",
            AgentConnection.NameOf(request.Server.ClientInfo), "attach", Traced());
        try
        {
            CallToolResult answer = await ForwardAsync(request, tool, call, cancel);
            call.Ended(answer.IsError == true ? "answered an error" : "answered");
            return answer;
        }
        catch (OperationCanceledException)
        {
            call.Ended("given up by the agent");
            throw;
        }
        catch (Exception error)
        {
            call.Ended($"failed ({error.GetType().Name})");
            throw;
        }
    }

    private async Task<CallToolResult> ForwardAsync(RequestContext<CallToolRequestParams> request, string tool, AgentCall call, CancellationToken cancel)
    {
        McpClient window;
        try
        {
            window = await JoinAsync(request.Server.ClientInfo, request.Server.NegotiatedProtocolVersion, call, cancel);
        }
        catch (NotJoined why)
        {
            call.Logged($"no window: {why.Message}");
            return Refused(why.Message);
        }
        string name = $"agent-{Guid.NewGuid():N}";
        RequestId id = new(name);
        // The window tells its progress under a token of the proxy's, which this call's agent hears as its own.
        ProgressToken? asked = call.Token is null ? null : new ProgressToken(name);
        if (asked is { } token)
        {
            told[token] = call;
        }
        call.Logged("forwarded to the window");
        AgentCall.Say($"Waiting for the Passenger window on {here} to answer");
        bool opens = tool == McpTools.Open;
        if (opens)
        {
            Interlocked.Increment(ref opening);
        }
        try
        {
            Task<CallToolResult> answer = window.SendRequestAsync<CallToolRequestParams, CallToolResult>(RequestMethods.ToolsCall, Forwarded(request.Params!, asked), requestId: id).AsTask();
            return await answer.WaitAsync(cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The agent gave up: the window is told, as the protocol has it, and stops waiting on the call.
            try
            {
                await window.SendNotificationAsync(NotificationMethods.CancelledNotification, new CancelledNotificationParams { RequestId = id });
            }
            catch (Exception error) when (error is IOException or McpException or ObjectDisposedException or InvalidOperationException)
            {
                // The window went too.
            }
            throw;
        }
        // The window's own error answers (a protocol error) are passed on as they came.
        catch (Exception error) when (error is not McpProtocolException && !cancel.IsCancellationRequested)
        {
            if (!await EndedAsync(window, cancel))
            {
                throw;
            }
            return Refused($"The connection to the Passenger window ended before it answered {tool}; a later call joins one again.");
        }
        finally
        {
            if (asked is { } forwarded)
            {
                told.TryRemove(forwarded, out _);
            }
            if (opens)
            {
                await FollowAsync(window);
                Interlocked.Decrement(ref opening);
            }
        }
    }

    /// <summary>The agent's own open answered: the window it joined is on the folder that open left it on, the agent's to
    /// work in from now.</summary>
    private async Task FollowAsync(McpClient window)
    {
        await joining.WaitAsync();
        try
        {
            if (joined == window && joinedPath is { } path)
            {
                joinedOn = McpHost.WindowFolder(path);
            }
        }
        finally
        {
            joining.Release();
        }
    }

    /// <summary>Whether the joined window has another folder open than the one the agent was let in on there, which the
    /// person opened (the agent's own opens under way aside): the window shuts the agent out, unless it lets agents in
    /// unasked there, and the agent goes on with its own folder.</summary>
    private bool Moved() =>
        Volatile.Read(ref opening) == 0 && joinedPath is { } path && !string.Equals(McpHost.WindowFolder(path), joinedOn, StringComparison.Ordinal);

    /// <summary>The agent's call as the window is sent it: the same, but for its progress token, the proxy's own or none.</summary>
    private static CallToolRequestParams Forwarded(CallToolRequestParams call, ProgressToken? token)
    {
        JsonObject sent = JsonSerializer.SerializeToNode(call, McpJsonUtilities.DefaultOptions)!.AsObject();
        JsonObject meta = sent["_meta"] as JsonObject ?? [];
        meta.Remove("progressToken");
        if (token is { } given)
        {
            meta["progressToken"] = JsonSerializer.SerializeToNode(given, McpJsonUtilities.DefaultOptions);
        }
        if (meta.Count == 0)
        {
            sent.Remove("_meta");
        }
        else
        {
            sent["_meta"] = meta;
        }
        return sent.Deserialize<CallToolRequestParams>(McpJsonUtilities.DefaultOptions)!;
    }

    /// <summary>The window's progress on a call the agent asked progress of, passed on to the agent.</summary>
    private ValueTask Heard(JsonRpcNotification notification, CancellationToken cancel)
    {
        if (notification.Params?.Deserialize<ProgressNotificationParams>(McpJsonUtilities.DefaultOptions) is { } progress
            && told.TryGetValue(progress.ProgressToken, out AgentCall? call))
        {
            call.Heard(progress.Progress.Message);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether Settings' protocol traces are on in the state this proxy shares with the person's windows, read as
    /// each call begins, since the proxy keeps no settings of its own.</summary>
    private static bool Traced()
    {
        try
        {
            using FileStream file = new(Path.Combine(State.StateStore.DefaultRoot, State.StateStore.SettingsFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument settings = JsonDocument.Parse(file);
            return settings.RootElement.ValueKind == JsonValueKind.Object
                && settings.RootElement.TryGetProperty("traceProtocols", out JsonElement on) && on.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The window the agent works in: the one joined, while it stays, else one over the folder, which a running
    /// Passenger is asked to open or a new one started on, waited for. Joined in the protocol version the agent agreed
    /// with this server, so what the window answers, passed on as it is, has nothing of a later version in it.
    /// The whole join has <see cref="StartLimit"/>.</summary>
    private async Task<McpClient> JoinAsync(Implementation? agent, string? protocol, AgentCall call, CancellationToken cancel)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(StartLimit);
        CancellationToken joiningCancel = limit.Token;
        bool acquired = false;
        try
        {
            await joining.WaitAsync(joiningCancel);
            acquired = true;
            joiningCancel.ThrowIfCancellationRequested();
            if (joined is { Completion.IsCompleted: false } live && !Closed(joinedSocket) && !Moved())
            {
                return live;
            }
            McpClient? old = joined;
            Socket? oldSocket = joinedSocket;
            joined = null;
            joinedSocket = null;
            joinedPath = null;
            joinedOn = null;
            oldSocket?.Dispose();
            if (old is not null)
            {
                await Quietly(old).WaitAsync(joiningCancel);
            }
            (NetworkStream stream, string socket) = await ConnectAsync(joiningCancel) ?? await StartAsync(call, joiningCancel);
            try
            {
                joined = await McpClient.CreateAsync(PeerTransport.Of(new StreamClientTransport(stream, stream)), new McpClientOptions
                {
                    // The agent's own name, which the window shows the person when it asks.
                    ClientInfo = agent ?? new Implementation { Name = "An agent", Version = "0" },
                    ProtocolVersion = protocol,
                    // This build's own server always answers the latest version's probe, so it is waited for, never given up on.
                    DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
                }, cancellationToken: joiningCancel);
                joined.RegisterNotificationHandler(NotificationMethods.ProgressNotification, Heard);
                joinedSocket = stream.Socket;
                joinedPath = socket;
                joinedOn = McpHost.WindowFolder(socket);
            }
            catch (Exception error)
            {
                await stream.DisposeAsync();
                if (error is IOException or McpException or InvalidOperationException)
                {
                    throw new NotJoined($"The connection to the Passenger window on {here} ended as the agent joined it; a later call tries again.");
                }
                throw;
            }
            return joined;
        }
        catch (OperationCanceledException) when (joiningCancel.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            throw new NotJoined($"Joining the Passenger window on {here} timed out after {StartLimit.TotalSeconds:0} s; a later call tries again.");
        }
        finally
        {
            if (acquired)
            {
                joining.Release();
            }
        }
    }

    /// <summary>Whether a call's failure is its connection to the window ending: the session fails the calls still waiting as
    /// its reader stops, and says it is complete only a moment after, on another of the pool's threads.</summary>
    private static async Task<bool> EndedAsync(McpClient window, CancellationToken cancel)
    {
        try
        {
            await window.Completion.WaitAsync(EndingLimit, cancel);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Whether the window's end of a connection looks closed, readable with nothing to read; only a hint, since
    /// the session's own reader may have taken what was there.</summary>
    private static bool Closed(Socket? socket)
    {
        try
        {
            return socket is null || (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>A connection to the window to join, with its socket's file, or null when none connects. Each endpoint has five seconds.</summary>
    private async Task<(NetworkStream Stream, string Socket)?> ConnectAsync(CancellationToken cancel)
    {
        McpHost.RemoveStale();
        foreach (string path in Windows())
        {
            Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token);
                return (new NetworkStream(socket, ownsSocket: true), path);
            }
            catch (Exception error) when (error is SocketException || (error is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        return null;
    }

    /// <summary>The windows' sockets an agent here may join, the best first: those whose folder holds the agent's, the
    /// innermost first, then the newest; with a process named, any of its windows, the newest after those.</summary>
    private IEnumerable<string> Windows()
    {
        if (McpHost.FoundSocketFolder() is not { } sockets)
        {
            return [];
        }
        return Directory.EnumerateFiles(sockets, "*.sock")
            .Where(path => processId is not int pid || McpHost.ProcessOf(path) == pid)
            .Select(path => (Path: path, Holding: McpHost.Holding(McpHost.WindowFolder(path), here)))
            .Where(socket => processId is not null || socket.Holding >= 0)
            .OrderByDescending(socket => socket.Holding)
            .ThenByDescending(socket => File.GetLastWriteTimeUtc(socket.Path))
            .Select(socket => socket.Path)
            .ToList();
    }

    /// <summary>Has a Passenger open the folder, the running one's or one started on it, and joins its window once it
    /// listens.</summary>
    private async Task<(NetworkStream Stream, string Socket)> StartAsync(AgentCall call, CancellationToken cancel)
    {
        if (processId is int pid)
        {
            throw new NotJoined($"Passenger {pid} has no window an agent can join. Open {here} in it, or attach without a process id to have Passenger open it.");
        }
        if (!Directory.Exists(here))
        {
            throw new NotJoined($"{here} is no folder for Passenger to open.");
        }
        // An agent started in no folder of the person's (Claude Desktop's is the disk's root) has no project to open.
        if (PathNames.Same(here, Path.GetPathRoot(here) ?? here) || PathNames.Same(here, PathNames.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))
        {
            throw new NotJoined($"The agent works in {here}, which is no project folder for Passenger to open. Give --mcp-attach the folder to work in "
                + "(--mcp-attach FOLDER), or start the agent in it.");
        }
        if (await AppSocket.AskAsync(here, cancel) is null)
        {
            call.Waits($"Starting Passenger on {here} and waiting for its window (at most {StartLimit.TotalSeconds:0} s)");
            await LaunchAsync(cancel);
        }
        else
        {
            call.Waits($"Waiting for the running Passenger to open {here} (at most {StartLimit.TotalSeconds:0} s)");
        }
        while (true)
        {
            if (await ConnectAsync(cancel) is { } stream)
            {
                call.Waited("its window listens");
                return stream;
            }
            if (headless is { HasExited: true } ended)
            {
                throw new NotJoined($"The Passenger started on {here} ended with exit code {ended.ExitCode} before its window opened.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancel);
        }
    }

    /// <summary>Starts Passenger on the folder: the installed app this is, through LaunchServices on macOS, else apart
    /// from this process, which it outlives; under <see cref="StartVariable"/>, this build headless, as a child. Each
    /// is told it was started for an agent (<see cref="StartedForVariable"/>).</summary>
    private async Task LaunchAsync(CancellationToken cancel)
    {
        string self = Environment.ProcessPath ?? throw new NotJoined("Passenger cannot tell where it is installed, to start it.");
        if (Environment.GetEnvironmentVariable(StartVariable) == "headless")
        {
            ProcessStartInfo start = ChildProcesses.Command(self, ["--headless", here], here);
            start.Environment[StartedForVariable] = "agent";
            // Its input kept, so its end ends it with this process; its output and error are never the agent's.
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            headless = ChildProcesses.Start(start);
            IdeErrors.Observe(headless.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None));
            IdeErrors.Observe(headless.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None));
            return;
        }
        if (OperatingSystem.IsMacOS() && Bundle(self) is { } app)
        {
            // The variable reaches only a Passenger LaunchServices starts; one running already opens the folder as it is.
            ChildResult opened = await ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/open", ["--env", $"{StartedForVariable}=agent", "-a", app, here]),
                limit: TimeSpan.FromSeconds(30), cancel: cancel);
            if (!opened.Succeeded)
            {
                throw new NotJoined($"LaunchServices did not start Passenger: {opened.Error.Trim()}");
            }
            return;
        }
        try
        {
            ProcessStartInfo apart = ChildProcesses.Command(self, [here], here);
            apart.Environment[StartedForVariable] = "agent";
            ChildProcesses.StartApart(apart);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new NotJoined($"Passenger did not start: {error.Message}");
        }
    }

    /// <summary>The app bundle an executable is inside (<c>X.app/Contents/MacOS/Passenger</c>), or null.</summary>
    private static string? Bundle(string executable)
    {
        string? macOS = Path.GetDirectoryName(executable);
        string? contents = macOS is null ? null : Path.GetDirectoryName(macOS);
        string? app = contents is null ? null : Path.GetDirectoryName(contents);
        return app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(macOS) == "MacOS" ? app : null;
    }

    /// <summary>The agent went: the window it joined sees it go.</summary>
    private void Leave()
    {
        if (joined is { } window)
        {
            Quietly(window).Wait(TimeSpan.FromSeconds(5));
        }
        // The headless window ends with its input, cleanly, before the watchdog would kill it with its socket left.
        if (headless is { HasExited: false } child)
        {
            try
            {
                child.StandardInput.Close();
                child.WaitForExit(McpHost.EndLimit);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                // Gone already.
            }
        }
    }

    private static async Task Quietly(McpClient window)
    {
        try
        {
            await window.DisposeAsync();
        }
        catch (Exception error) when (error is IOException or McpException or ObjectDisposedException or OperationCanceledException)
        {
            // Gone already.
        }
    }

    private static CallToolResult Refused(string why) => new() { IsError = true, Content = [new TextContentBlock { Text = why }] };

    /// <summary>Why the agent's call found no window to go on to.</summary>
    private sealed class NotJoined(string message) : Exception(message);

    /// <summary>A tool as the build defines it, listed by the proxy itself, its calls going on to the window's.</summary>
    private sealed class ForwardedTool(McpServerTool tool, McpProxy proxy) : DelegatingMcpServerTool(tool)
    {
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
            new(proxy.CallAsync(request, cancellationToken));
    }
}
