using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
/// started, at the next call. Resources and prompts are none, here as in the window. On macOS and Linux an update of the
/// app it runs from has it take its own process's place from the updated app (<see cref="Follow"/>), its agent none the wiser.</summary>
public sealed partial class McpProxy
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

    /// <summary>The file of the session a relay hands the one taking its process's place from the updated app
    /// (<see cref="SessionFile"/>), which that one reads, removes and goes on with.</summary>
    [IdentifierHome]
    public const string SessionVariable = "PASSENGER_RELAY_SESSION";

    /// <summary>The session this relay goes on with, handed over by the one whose process it took (<see cref="SessionVariable"/>).</summary>
    private static RelaySession? followed;

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

    /// <summary>The connection to the agent, which a call notes going on to a window.</summary>
    private RelayTransport? transport;

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
        if (Environment.GetEnvironmentVariable(SessionVariable) is { Length: > 0 } session)
        {
            Environment.SetEnvironmentVariable(SessionVariable, null);
            try
            {
                followed = RelaySession.Read(File.ReadAllText(session));
                File.Delete(session);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                throw new FormatException($"{SessionVariable} names {session}, the session of the relay before this one, which cannot be read: {error.Message}", error);
            }
        }
    }

    /// <summary>Serves the agent on standard input and output until it goes, then lets go of the window it joined; told to
    /// follow an update of the app, the process becomes the updated app's relay instead (<see cref="Follow"/>).</summary>
    public static int Run(int? processId, string? folder)
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        // Standard output is the protocol's alone, and standard error the agent may never read: the console goes to the log.
        IdeLog.TakeConsole();
        McpProxy proxy = new(processId, PathNames.Canonical(folder ?? Environment.CurrentDirectory));
        ToolList tools = [];
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
        RelayTransport transport = new(input, output, followed);
        proxy.transport = transport;
        McpServer server = McpServer.Create(new PeerTransport(transport), options);
        if (followed is { } session)
        {
            string listed = ToolsListed();
            // Once the agent's session is as it was, so a change of tools reaches it the way it asked to hear of one.
            transport.Resumed += () =>
            {
                IdeErrors.Write($"Agents: this relay's agent's session goes on as it was, in protocol version {server.NegotiatedProtocolVersion}");
                if (session.Tools != listed)
                {
                    tools.Relisted();
                }
            };
            IdeErrors.Write($"Agents: this relay took the place of the one before it from the updated Passenger, going on with its agent's session"
                + $" ({Plural.Of(session.Carried.Count, "call")} carried over{(session.Tools == listed ? string.Empty : ", its tools changed")})");
            Unheld();
        }
        Task running = server.RunAsync(gone.Token);
        // The server waits out calls still going as the agent goes, so its going, or SIGTERM, is watched for here.
        TaskCompletionSource terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource following = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using PosixSignalRegistration? signal = McpHost.EndOnSignal(() => terminated.TrySetResult(), kept: () =>
        {
            // Told to follow an update of the app (FollowOrder): the process goes on, as the relay from the updated app.
            if (OperatingSystem.IsWindows() || FollowOrder() is null)
            {
                return false;
            }
            following.TrySetResult();
            return true;
        });
        string? said = Announce(proxy.here);
        try
        {
            // Main's thread, which serves nothing itself, waits here until the agent goes.
            Synchronously.Run(async () => { await Task.WhenAny(running, transport.MessageReader.Completion, terminated.Task, following.Task); });
            if (following.Task.IsCompleted)
            {
                // Comes back only where the process could not be taken over.
                proxy.Follow(server, transport);
            }
            else if (terminated.Task.IsCompleted)
            {
                SayWhyEnded();
            }
            gone.Cancel();
            proxy.Leave();
            // The process ends as Main returns: the server, cut short by the agent's going, has 5 s to end first.
            Synchronously.Run(async () => { await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5))); });
            // What the agent's going cut short.
            _ = running.Exception;
        }
        finally
        {
            if (said is not null)
            {
                File.Delete(said);
            }
        }
        return 0;
    }

    /// <summary>The file in a socket folder that says a process is an agent's relay, and the folder it works in, so an update
    /// of the app it runs from finds it, whether or not it joined a window (<see cref="Updates.Updater.Others"/>).</summary>
    internal static string RelayFile(string sockets, int processId) => Path.Combine(sockets, $"{processId}.relay");

    /// <summary>The file in a socket folder in which an update says why it ends a relay, which the relay passes on to its agent.</summary>
    internal static string EndingFile(string sockets, int processId) => Path.Combine(sockets, $"{processId}.ending");

    /// <summary>Writes this relay's <see cref="RelayFile"/>, and gives it, or null where the socket folder cannot be made,
    /// which leaves an update to name the relay as a program in its way.</summary>
    private static string? Announce(string here)
    {
        try
        {
            string file = RelayFile(McpHost.OwnSocketFolder(), Environment.ProcessId);
            AtomicFile.Write(file, System.Text.Encoding.UTF8.GetBytes(here));
            return file;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Agents: this relay cannot say it is one, so an update of Passenger waits for it to end: {error.Message}");
            return null;
        }
    }

    /// <summary>The folder a running relay works in, as its <see cref="RelayFile"/> says, or null for a process that is no
    /// relay: one with no such file, or started after its file was written (a dead relay's id taken again).</summary>
    internal static string? RelayAt(int processId)
    {
        if (McpHost.FoundSocketFolder() is not { } sockets)
        {
            return null;
        }
        string file = RelayFile(sockets, processId);
        try
        {
            string folder = File.ReadAllText(file);
            DateTime written = File.GetLastWriteTimeUtc(file);
            using Process process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime() <= written ? folder : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Tells a relay why it is about to be ended (<see cref="EndingFile"/>).</summary>
    internal static void TellEnding(int processId, string why)
    {
        if (McpHost.FoundSocketFolder() is { } sockets)
        {
            AtomicFile.Write(EndingFile(sockets, processId), System.Text.Encoding.UTF8.GetBytes(why));
        }
    }

    /// <summary>Ended by the system: why, when an update said so, on standard error, which the agent's client shows in its
    /// log of the server, and in the IDE's log.</summary>
    private static void SayWhyEnded()
    {
        if (McpHost.FoundSocketFolder() is not { } sockets)
        {
            return;
        }
        string file = EndingFile(sockets, Environment.ProcessId);
        try
        {
            string why = File.ReadAllText(file);
            File.Delete(file);
            IdeErrors.Write($"Agents: this relay ends: {why}");
            using Stream error = IdeLog.GivenError();
            error.Write(System.Text.Encoding.UTF8.GetBytes($"passenger: {why}\n"));
            error.Flush();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Ended for another reason, which has nothing to say.
        }
    }

    /// <summary>The file in a socket folder by which an update installing the app a relay runs from orders it to follow
    /// (<see cref="Follow"/>), naming the process installing it; gone once the updated app is in place.</summary>
    internal static string FollowFile(string sockets, int processId) => Path.Combine(sockets, $"{processId}.follow");

    /// <summary>The file in a socket folder that says a following relay has let go of the app, waiting in a shell for the
    /// updated one.</summary>
    internal static string HeldFile(string sockets, int processId) => Path.Combine(sockets, $"{processId}.held");

    /// <summary>The file in a socket folder a following relay hands its agent's session over in, the user's alone.</summary>
    internal static string SessionFile(string sockets, int processId) => Path.Combine(sockets, $"{processId}.session");

    /// <summary>Orders a relay to follow the update this process installs: its calls wait for the updated app's windows
    /// from now on, and SIGTERM has it hand its session over and let go of the app (<see cref="Follow"/>).</summary>
    internal static void OrderFollowing(int processId)
    {
        string sockets = McpHost.OwnSocketFolder();
        File.Delete(HeldFile(sockets, processId));
        AtomicFile.Write(FollowFile(sockets, processId), System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Whether a relay ordered to follow has let go of the app, waiting off it for the updated one.</summary>
    internal static bool LetGo(int processId) => McpHost.FoundSocketFolder() is { } sockets && File.Exists(HeldFile(sockets, processId));

    /// <summary>Takes back the order to follow: the updated app is in place, or there is no update after all.</summary>
    internal static void Unordered(int processId)
    {
        if (McpHost.FoundSocketFolder() is { } sockets)
        {
            File.Delete(FollowFile(sockets, processId));
        }
    }

    /// <summary>The process installing an update of the app this relay runs from, which ordered it to follow, while that
    /// process runs; else null, an order it left by ending taken away.</summary>
    private static int? FollowOrder()
    {
        if (McpHost.FoundSocketFolder() is not { } sockets)
        {
            return null;
        }
        string order = FollowFile(sockets, Environment.ProcessId);
        try
        {
            int installing = int.Parse(File.ReadAllText(order), CultureInfo.InvariantCulture);
            using Process process = Process.GetProcessById(installing);
            if (!process.HasExited)
            {
                return installing;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException
            or InvalidOperationException)
        {
            // No order, or one whose process has ended.
        }
        try
        {
            File.Delete(order);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Read as no order all the same.
        }
        return null;
    }

    /// <summary>The tools a relay lists its agent, as a digest, which the relay after it compares with its own.</summary>
    internal static string ToolsListed()
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (McpServerTool tool in McpHost.Tools)
        {
            digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(tool.ProtocolTool, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool))));
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    /// <summary>The shell a following relay waits in, off the app being replaced, until the order is taken back (the
    /// updated app in place), its installer has ended, or two minutes have passed; then it becomes the app's relay
    /// again, told the session to go on with. It says it waits by making the held file.</summary>
    private const string WaitingShell = $"""
        installing=$1 held=$2 order=$3 session=$4
        shift 4
        : > "$held"
        waited=0
        while [ -e "$order" ] && kill -0 "$installing" 2>/dev/null && [ "$waited" -lt 1200 ]; do
          sleep 0.1
          waited=$((waited + 1))
        done
        {SessionVariable}=$session
        export {SessionVariable}
        exec "$@"
        """;

    /// <summary>Hands the agent's session to the relay that takes this process's place from the updated app: nothing
    /// more is read or written here, what the agent sent and had no answer to goes to <see cref="SessionFile"/>, and the
    /// process waits in a shell, off the app, then becomes the updated app's relay, keeping standard input, output and
    /// error alone. Comes back only where it could not, having told the agent why.</summary>
    private void Follow(McpServer server, RelayTransport agent)
    {
        RelaySession session = agent.Freeze(server.NegotiatedProtocolVersion, ToolsListed(),
            tool => $"The connection to the Passenger window ended before it answered {tool}, as Passenger updated; call it again.");
        string why;
        try
        {
            string sockets = McpHost.OwnSocketFolder();
            int self = Environment.ProcessId;
            string file = SessionFile(sockets, self);
            AtomicFile.Write(file, session.Written(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string installing = (FollowOrder() ?? self).ToString(CultureInfo.InvariantCulture);
            string app = Environment.ProcessPath ?? throw new IOException("Passenger cannot tell where it is installed");
            EndHeadless();
            IdeErrors.Write($"Agents: this relay follows Passenger's update: it lets go of the app until the update is in place, then starts again from {app}"
                + $" with its agent's session ({Plural.Of(session.Carried.Count, "call")} carried over)");
            IdeErrors.Flush();
            why = Become(["/bin/sh", "-c", WaitingShell, "passenger-relay", installing, HeldFile(sockets, self), FollowFile(sockets, self), file,
                app, "--mcp-attach", .. processId is int attached ? [attached.ToString(CultureInfo.InvariantCulture)] : (string[])[], here]);
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            why = error.Message;
        }
        IdeErrors.Write($"Agents: this relay could not follow Passenger's update, and ends: {why}");
        IdeErrors.Flush();
        try
        {
            using Stream said = IdeLog.GivenError();
            said.Write(System.Text.Encoding.UTF8.GetBytes($"passenger: Passenger updated, and this agent's relay could not go on from the updated app ({why}). "
                + "Reconnect the agent to Passenger to go on (/mcp in Claude Code).\n"));
        }
        catch (IOException)
        {
            // The agent went, with nobody to tell.
        }
    }

    /// <summary>The process becomes the program <paramref name="command"/> names, keeping standard input, output and error
    /// (given back as the process was started with it, <see cref="IdeLog.GiveBackStandardError"/>) and nothing else open;
    /// comes back only where it could not, with why.</summary>
    private static unsafe string Become(string[] command)
    {
        IdeLog.GiveBackStandardError();
        Descriptors.CloseAllOnExec();
        nint[] arguments = new nint[command.Length + 1];
        try
        {
            for (int i = 0; i < command.Length; i++)
            {
                arguments[i] = Marshal.StringToCoTaskMemUTF8(command[i]);
            }
            fixed (nint* given = arguments)
            {
                Execute(command[0], given);
            }
            return Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
        }
        finally
        {
            foreach (nint argument in arguments)
            {
                Marshal.FreeCoTaskMem(argument);
            }
        }
    }

    /// <summary>A relay that took another's place from the updated app has the app again: the shell's held file goes.</summary>
    private static void Unheld()
    {
        try
        {
            if (McpHost.FoundSocketFolder() is { } sockets)
            {
                File.Delete(HeldFile(sockets, Environment.ProcessId));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Agents: the held file of this relay's wait for the updated Passenger stays: {error.Message}");
        }
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Execute(string path, nint* arguments);

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
        if (transport?.Forwarding(request.JsonRpcRequest.Id) == false)
        {
            // Handed over: the relay that takes this process's place makes the call.
            await Task.Delay(Timeout.Infinite, cancel);
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
    private void Heard(JsonRpcNotification notification)
    {
        if (notification.Params?.Deserialize<ProgressNotificationParams>(McpJsonUtilities.DefaultOptions) is { } progress
            && told.TryGetValue(progress.ProgressToken, out AgentCall? call))
        {
            call.Heard(progress.Progress.Message);
        }
    }

    /// <summary>The connection to a window, its progress heard as each message is read, in the order the window wrote
    /// them, so a call's last word reaches the call before the answer written after it.</summary>
    private sealed class InOrder(IClientTransport inner, Action<JsonRpcNotification> heard) : IClientTransport
    {
        public string Name => inner.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            new Connected(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false), heard);

        private sealed class Connected(ITransport transport, Action<JsonRpcNotification> heard) : ITransport
        {
            public string? SessionId => transport.SessionId;

            public ChannelReader<JsonRpcMessage> MessageReader { get; } = new Reader(transport.MessageReader, heard);

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
                transport.SendMessageAsync(message, cancellationToken);

            public ValueTask DisposeAsync() => transport.DisposeAsync();
        }

        private sealed class Reader(ChannelReader<JsonRpcMessage> messages, Action<JsonRpcNotification> heard) : ChannelReader<JsonRpcMessage>
        {
            public override Task Completion => messages.Completion;

            public override bool TryRead([MaybeNullWhen(false)] out JsonRpcMessage item)
            {
                if (!messages.TryRead(out item))
                {
                    return false;
                }
                if (item is JsonRpcNotification { Method: NotificationMethods.ProgressNotification } progress)
                {
                    heard(progress);
                }
                return true;
            }

            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => messages.WaitToReadAsync(cancellationToken);
        }
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
                joined = await McpClient.CreateAsync(new InOrder(PeerTransport.Of(new StreamClientTransport(stream, stream)), Heard), new McpClientOptions
                {
                    // The agent's own name, which the window shows the person when it asks.
                    ClientInfo = agent ?? new Implementation { Name = "An agent", Version = "0" },
                    ProtocolVersion = protocol,
                    // This build's own server always answers the latest version's probe, so it is waited for, never given up on.
                    DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
                }, cancellationToken: joiningCancel);
                joinedSocket = stream.Socket;
                joinedPath = socket;
                joinedOn = McpHost.WindowFolder(socket);
            }
            catch (Exception error) when (error is IOException or McpException or InvalidOperationException)
            {
                await stream.DisposeAsync();
                throw new NotJoined($"The connection to the Passenger window on {here} ended as the agent joined it; a later call tries again.");
            }
            catch
            {
                await stream.DisposeAsync();
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
        if (FollowOrder() is not null)
        {
            call.Waits($"Waiting for Passenger to restart into its update, to join its window on {here}");
            while (FollowOrder() is not null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancel);
            }
            if (await ConnectAsync(cancel) is { } restarted)
            {
                call.Waited("its window listens");
                return restarted;
            }
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
            try
            {
                // On Main's thread as the process ends, which has nothing to go on to while the window hears it.
                Synchronously.Run(() => Quietly(window).WaitAsync(TimeSpan.FromSeconds(5)));
            }
            catch (TimeoutException)
            {
                // The window had its 5 s.
            }
        }
        EndHeadless();
    }

    /// <summary>The headless window ends with its input, cleanly, before the watchdog would kill it with its socket left.</summary>
    private void EndHeadless()
    {
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

    /// <summary>The tools the relay lists, which tell the agent their list changed when the relay before this one listed others.</summary>
    private sealed class ToolList : McpServerPrimitiveCollection<McpServerTool>
    {
        public void Relisted() => RaiseChanged();
    }

    /// <summary>Why the agent's call found no window to go on to.</summary>
    private sealed class NotJoined(string message) : Exception(message);

    /// <summary>A tool as the build defines it, listed by the proxy itself, its calls going on to the window's.</summary>
    private sealed class ForwardedTool(McpServerTool tool, McpProxy proxy) : DelegatingMcpServerTool(tool)
    {
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
            new(proxy.CallAsync(request, cancellationToken));
    }
}
