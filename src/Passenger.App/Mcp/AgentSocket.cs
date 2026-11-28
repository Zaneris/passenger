using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>A window's socket for agents, open while the window may let them in at all (not <c>--no-mcp</c>): in <see
/// cref="McpHost.SocketFolder"/>, which only the person's own processes reach, named for the process and the window so two
/// windows of one Passenger never take each other's, with the window's folder written beside it for <c>--mcp-attach</c>
/// to choose by. A connection is served from the start, but each of its tool calls waits until it is let in: by Enable
/// MCP Server, by the folder's Always, or by the person's Allow (<see cref="AgentConnection.AdmitAsync"/>), so a process
/// that connects learns only the build's own tool list until the person says so. Closing it ends every connection.</summary>
public sealed class AgentSocket : IDisposable
{
    /// <summary>The sockets the process has opened, every window's, which number each one's name apart.</summary>
    private static int opened;

    private readonly MainWindow window;
    private readonly Socket listener;
    private readonly CancellationTokenSource closed = new();

    /// <summary>Cancelled, and made anew, when the person shuts out the agents connected (Enable MCP Server turned off).</summary>
    private CancellationTokenSource letIn = new();

    /// <summary>The agents' connections, each with what ends its calls, closed at once when they are shut out or the
    /// window closes, so an agent's next call finds the window gone rather than sending into its end.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Socket, Served> clients = new();

    /// <summary>How long the next line written to an agent stalls, in ticks, until a write takes it (<see cref="StallNextLine"/>).</summary>
    private long nextLineStall;

    private AgentSocket(MainWindow window, string path, Socket listener)
    {
        this.window = window;
        this.listener = listener;
        Path = path;
        // Not held: it listens for as long as the window does.
        IdeErrors.Observe(BackgroundWork.Unheld(AcceptAsync));
    }

    /// <summary>The socket's file.</summary>
    public string Path { get; }

    /// <summary>Listens for the window's agents, or throws saying why it cannot.</summary>
    public static AgentSocket Open(MainWindow window)
    {
        // The folder is the user's alone: nobody else reaches the IDE through it.
        string folder = McpHost.OwnSocketFolder();
        McpHost.RemoveStale();
        string path = System.IO.Path.Combine(folder, $"{Environment.ProcessId}-{Interlocked.Increment(ref opened)}.sock");
        // Only a dead Passenger that had this process's number leaves one by this name.
        File.Delete(path);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        return new AgentSocket(window, path, listener);
    }

    /// <summary>Writes the window's open folder beside the socket, as it opens and changes: whole, so an agent choosing a
    /// window by its folder never reads half of one (<see cref="McpHost.WindowFolder"/>).</summary>
    public void Describe(string? folder)
    {
        try
        {
            AtomicFile.Write(McpHost.FolderFile(Path), System.Text.Encoding.UTF8.GetBytes(folder ?? string.Empty));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unnamed, the window is still reached: only no longer chosen for its folder.
        }
    }

    /// <summary>Ends every agent's connection to the window; each is asked to be let in again at its next call.</summary>
    public void ShutOut()
    {
        Interlocked.Exchange(ref letIn, new CancellationTokenSource()).Cancel();
        Hangup();
    }

    /// <summary>The window leaves its folder for another, or none: the agents let in on the one it leaves are shut out,
    /// each asked to be let in again at its next call, unless <paramref name="staying"/> (the window lets agents in unasked
    /// where it goes); the agent whose own open moves it (<paramref name="opener"/>) goes with it. Those kept are asked
    /// again before their first use of each tool there. UI thread only.</summary>
    public void Moving(bool staying, AgentConnection? opener)
    {
        UiThread.Check();
        foreach ((Socket client, Served served) in clients)
        {
            if (staying || served.Connection == opener)
            {
                served.Connection.Moved();
                continue;
            }
            try
            {
                // At once, so a call of the folder left that has yet to run does nothing in the one opened.
                served.Gone.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Its connection ended as it was shut out.
            }
            Hangup(client);
        }
    }

    /// <summary>A test's slow agent: the next line written to an agent stalls this long with its words written and its end
    /// not, as a write into a full pipe stalls, its sender's cancellation cutting it short as it would that write.</summary>
    public void StallNextLine(TimeSpan stall) => Interlocked.Exchange(ref nextLineStall, stall.Ticks);

    public void Dispose()
    {
        if (closed.IsCancellationRequested)
        {
            return;
        }
        closed.Cancel();
        listener.Dispose();
        Hangup();
        File.Delete(Path);
        File.Delete(McpHost.FolderFile(Path));
    }

    private void Hangup()
    {
        foreach (Socket client in clients.Keys)
        {
            Hangup(client);
        }
    }

    private static void Hangup(Socket client)
    {
        try
        {
            client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            // Gone already.
        }
    }

    private Task AcceptAsync() =>
        Accepting.EachAsync(stop => listener.AcceptAsync(stop), client => IdeErrors.Observe(ServeAsync(client)), listener.Dispose, "The agents' socket", closed.Token);

    private async Task ServeAsync(Socket client)
    {
        using CancellationTokenSource gone = CancellationTokenSource.CreateLinkedTokenSource(closed.Token, letIn.Token);
        AgentConnection connection = new(gone.Token, attached: true);
        clients[client] = new Served(connection, gone);
        try
        {
            await using NetworkStream stream = new(client, ownsSocket: true);
            // Its tools are made once a process, off the UI thread, which the first connection's would hold; nothing drawn changes.
            McpServerOptions options = await BackgroundWork.Unheld(() => McpHost.Options(window, connection));
            StreamServerTransport transport = new(stream, new Lines(stream, this), "passenger");
            await using McpServer server = McpServer.Create(new PeerTransport(transport), options);
            Task running = server.RunAsync(gone.Token);
            // The server waits out the calls still running when the agent goes, so its going is watched for here.
            await Task.WhenAny(running, transport.MessageReader.Completion);
            await gone.CancelAsync();
            await running;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException)
        {
        }
        finally
        {
            clients.TryRemove(client, out _);
            UiThread.Post(() => connection.Left(window));
        }
    }

    /// <summary>An agent's connection as the socket serves it, and what ends its calls.</summary>
    private sealed record Served(AgentConnection Connection, CancellationTokenSource Gone);

    /// <summary>What the window writes to an agent, passed on, but for the stall a test gives a line (<see cref="StallNextLine"/>).</summary>
    private sealed class Lines(NetworkStream inner, AgentSocket socket) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            if (buffer.Length > 0 && buffer.Span[^1] != (byte)'\n' && Interlocked.Exchange(ref socket.nextLineStall, 0) is > 0 and long stall)
            {
                await Task.Delay(TimeSpan.FromTicks(stall), cancellationToken);
            }
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>What the person said to an agent's question: whether it may work in the window, use a tool, open a folder.</summary>
public enum Admission
{
    /// <summary>Allowed, and for a tool for every call of it until the agent goes or the window leaves the folder.</summary>
    Allowed,

    /// <summary>A tool allowed for the one call whose arguments its question showed (<see cref="AgentToolDialog.AllowOnce"/>).</summary>
    AllowedOnce,

    NotAllowed,

    /// <summary>Nobody answered within the window's limit, or every call waiting for the answer went first.</summary>
    Unanswered,

    /// <summary>The window left the folder it was asked in (it opened another, or closed it) before the person answered.</summary>
    FolderChanged,
}

/// <summary>One agent's connection: its calls end when it does; on a person's window (attached) it is let in before its
/// first call goes on, and a call of a tool that changes something waits for the person to allow it, once or for the
/// session, or every tool at once at the agent's request, each question waited for at most the window's
/// <see cref="MainWindow.AgentAnswerLimit"/>. UI thread only, but for <see cref="Gone"/>.</summary>
public sealed class AgentConnection(CancellationToken gone, bool attached)
{
    /// <summary>What every tool at once is asked, allowed and denied under for the session (request_full_access).</summary>
    private const string EveryTool = McpTools.RequestFullAccess;

    /// <summary>The tools the person allowed for the session, which the agent uses unasked until it goes or the window opens
    /// another folder, as <see cref="AllowAsync"/> keys them, every tool at once among them (<see cref="EveryTool"/>).</summary>
    private readonly HashSet<string> allowed = new(StringComparer.Ordinal);

    /// <summary>Whether the person denied every tool at once, which holds as long as an allowance would: a later request
    /// is refused unasked, each tool still asking at its call.</summary>
    private bool everyToolDenied;

    /// <summary>Each tool's question while the person is asked it, which the calls of that tool wait on.</summary>
    private readonly Dictionary<string, Asked> allowing = new(StringComparer.Ordinal);

    /// <summary>The question whether the agent may work in the window, while the person is asked it, which every call waits on.</summary>
    private Asked? asking;

    /// <summary>Cancelled when the agent goes, or the window stops letting agents in.</summary>
    public CancellationToken Gone { get; } = gone;

    /// <summary>A person's window, rather than the agent's own IDE.</summary>
    public bool Attached { get; } = attached;

    /// <summary>The agent as its client names itself (initialize's clientInfo: its title, else its name), once it has
    /// called a tool.</summary>
    public string Agent { get; private set; } = "An agent";

    /// <summary>Let into the window, and so counted in its status bar until it goes.</summary>
    public bool Admitted { get; private set; }

    /// <summary>Takes the agent's name from its client's own words.</summary>
    internal void Named(Implementation? client) => Agent = NameOf(client);

    /// <summary>An agent's name as its client gives it (initialize's clientInfo: its title, else its name).</summary>
    internal static string NameOf(Implementation? client) =>
        client is not null && (client.Title is { Length: > 0 } title ? title : client.Name) is { Length: > 0 } name ? name : "An agent";

    /// <summary>Lets the agent into a person's window before its call goes on, or says why not: at once while Enable
    /// MCP Server is on, the folder's Always holds or nobody is at the window; else the person is asked, once for every
    /// call waiting, for at most the window's limit, the question going away unanswered once none waits. A no, or no
    /// answer, is the call's answer, and a later call asks again.</summary>
    internal async Task<string?> AdmitAsync(MainWindow window, AgentCall call, CancellationToken cancel)
    {
        UiThread.Check();
        if (!Attached || Admitted)
        {
            return null;
        }
        if (window.LetsAgentsIn)
        {
            Admit(window);
            return null;
        }
        if (asking is null)
        {
            CancellationTokenSource takenAway = CancellationTokenSource.CreateLinkedTokenSource(Gone);
            asking = new(window.AskToLetInAsync(Agent, takenAway.Token), takenAway, window.Folder);
        }
        Asked asked = asking;
        asked.Waiting++;
        call.Waits($"Waiting for the person at {Where(window)} to let {Agent} in (at most {Limit(window)})");
        Admission said;
        try
        {
            said = await asked.Answer.WaitAsync(cancel);
        }
        finally
        {
            call.Waited(asked.Answer.IsCompletedSuccessfully ? asked.Answer.Result.ToString() : "given up");
            // The last call waiting gives the question up: it closes unanswered, and the next call asks again.
            if (--asked.Waiting == 0 && !asked.Answer.IsCompleted)
            {
                asked.TakenAway.Cancel();
            }
            if (asking == asked && (asked.Waiting == 0 || asked.Answer.IsCompleted))
            {
                asking = null;
            }
        }
        if (said == Admission.Allowed)
        {
            Admit(window);
            return null;
        }
        return said switch
        {
            Admission.NotAllowed => $"The person at {Where(window)} did not let {Agent} work there. Ask them, or do without it; a later call asks them again.",
            Admission.FolderChanged => Refusals.FolderChanged(asked.Folder).English,
            _ => $"Nobody at {Where(window)} answered within {Limit(window)} whether {Agent} may work there. A later call asks again.",
        };
    }

    /// <summary>The window as an answer names it.</summary>
    internal static string Where(MainWindow window) => window.Folder is { } folder ? $"the Passenger window on {folder}" : "the Passenger window";

    /// <summary>The window's limit on a question, as an answer says it.</summary>
    internal static string Limit(MainWindow window) => Durations.Said(window.AgentAnswerLimit);

    /// <summary>The window opened another folder and the agent stays let in (its own open, or agents let in unasked there):
    /// the tools allowed for the session in the one it left are asked for again, every tool at once too, allowed or denied.</summary>
    internal void Moved()
    {
        UiThread.Check();
        allowed.Clear();
        everyToolDenied = false;
    }

    /// <summary>Something of the agent's session that ends as it goes (the files its calls keep open), on the UI thread.</summary>
    internal void WhenLeft(Action ending) => leaving.Add(ending);

    private readonly List<Action> leaving = [];

    /// <summary>The connection ended: the window stops counting it.</summary>
    internal void Left(MainWindow window)
    {
        UiThread.Check();
        foreach (Action ending in leaving)
        {
            ending();
        }
        leaving.Clear();
        if (Admitted)
        {
            Admitted = false;
            window.Agents--;
        }
    }

    /// <summary>Whether the person lets this call of a tool go on, or why not: asked each time unless they allowed the tool, or
    /// every tool, for the session, an app_ tool's input to Passenger's own windows apart from an app's; never where nobody
    /// is at the window (<see cref="MainWindow.AgentsUnattended"/>), as <see cref="AnswerAsync"/> asks.</summary>
    internal async Task<string?> AllowAsync(MainWindow window, Tool tool, IDictionary<string, JsonElement>? arguments, AgentCall call, CancellationToken cancel)
    {
        UiThread.Check();
        if (window.AgentsUnattended)
        {
            return null;
        }
        bool own = OnOwnWindows(arguments);
        string key = own ? $"{tool.Name} {PassengerTools.OwnWindows}" : tool.Name;
        string named = own ? $"{tool.Name} in Passenger's own windows (app {PassengerTools.OwnWindows})" : tool.Name;
        (Admission said, string? folder) = await AnswerAsync(window, key, named, takenAway => window.AllowAgentAsync(Agent, tool.Name, tool.Title ?? tool.Name, own,
            [.. (arguments ?? new Dictionary<string, JsonElement>()).Select(a => (a.Key, Shown(a.Value)))], takenAway), call, cancel);
        return said switch
        {
            Admission.Allowed or Admission.AllowedOnce => null,
            Admission.NotAllowed => $"The person at {Where(window)} did not allow {named}. Ask them, or do without it; a later call asks them again.",
            Admission.FolderChanged => Refusals.FolderChanged(folder).English,
            _ => $"Nobody at {Where(window)} answered within {Limit(window)} whether {Agent} may use {named}. A later call asks again.",
        };
    }

    /// <summary>What a request for every tool answers on the agent's own IDE, which asks nobody.</summary>
    public const string NothingAsks = "Nothing asks here: this Passenger is the agent's own (--mcp), whose every tool goes on without a question.";

    /// <summary>Whether the person lets every tool that changes something go on unasked for the session (request_full_access),
    /// as if each were allowed for the session by its own question, in words for the agent, with whether they refuse: asked
    /// once, the tools' own questions waiting then taken away as allowed; a Deny holds as an allowance would, a later request
    /// refused unasked while each tool still asks at its call. Nothing asks on the agent's own IDE or a window nobody is at.</summary>
    internal async Task<(string Said, bool Refused)> AllowEveryToolAsync(MainWindow window, AgentCall? call, CancellationToken cancel)
    {
        UiThread.Check();
        if (!Attached || window.AgentsUnattended)
        {
            return (Attached ? $"Nobody is at {Where(window)} to ask: every tool goes on without a question." : NothingAsks, false);
        }
        string lasts = "until the agent disconnects or the window opens another folder";
        if (allowed.Contains(EveryTool))
        {
            return ($"Every tool is allowed already: calls go on without asking the person at {Where(window)} {lasts}.", false);
        }
        if (everyToolDenied)
        {
            return ($"The person at {Where(window)} denied every tool at once, so they are not asked again {lasts}; each tool still asks them at its call.", true);
        }
        IEnumerable<string> asks = McpHost.Tools.Select(t => t.ProtocolTool).Where(ConnectedTool.Asks).Select(t => t.Name).Order(StringComparer.Ordinal);
        (Admission said, string? folder) = await AnswerAsync(window, EveryTool, "every tool", takenAway => window.AllowAgentEveryToolAsync(Agent, asks, takenAway), call, cancel);
        switch (said)
        {
            case Admission.Allowed:
                // Each tool's question still asked has its answer now: its calls go on.
                foreach (Asked waiting in allowing.Values.ToList())
                {
                    waiting.TakenAway.Cancel();
                }
                return ($"The person at {Where(window)} allowed every tool: calls go on without asking them {lasts}. Opening another folder in "
                    + "their window still asks.", false);
            case Admission.NotAllowed:
                everyToolDenied = true;
                return ($"The person at {Where(window)} denied every tool at once: each tool still asks them at its call, and a later request is refused unasked {lasts}.", true);
            case Admission.FolderChanged:
                return (Refusals.FolderChanged(folder).English, true);
            default:
                return ($"Nobody at {Where(window)} answered within {Limit(window)} whether {Agent} may use every tool. A later request asks again.", true);
        }
    }

    /// <summary>The person's answer for this call to the question <paramref name="key"/> names, put through
    /// <paramref name="ask"/> unless it, or every tool, is allowed for the session, with the folder it was asked in. The
    /// calls of one key wait on one question, which shows the call that asked it and goes as that one gives up, the others
    /// then asking their own; an Allow Once lets the asker alone go on, the others asking their own; one taken away as
    /// every tool was allowed lets them all go on. Allowed for the session, the key holds until <see cref="Moved"/>.</summary>
    private async Task<(Admission Said, string? Folder)> AnswerAsync(MainWindow window, string key, string named, Func<CancellationToken, Task<Admission>> ask,
        AgentCall? call, CancellationToken cancel)
    {
        while (!Allows(key))
        {
            bool asker = !allowing.TryGetValue(key, out Asked? asked);
            if (asked is null)
            {
                CancellationTokenSource takenAway = CancellationTokenSource.CreateLinkedTokenSource(Gone);
                asked = new(ask(takenAway.Token), takenAway, window.Folder);
                allowing[key] = asked;
            }
            call?.Waits($"Waiting for the person at {Where(window)} to allow {Agent} to use {named} (at most {Limit(window)})");
            Admission said;
            try
            {
                said = await asked.Answer.WaitAsync(cancel);
            }
            finally
            {
                call?.Waited(asked.Answer is { IsCompletedSuccessfully: true } answer ? answer.Result.ToString() : "given up");
                // The question shows the asker's arguments, so it goes with the asker, and the calls left ask their own.
                if (asker && !asked.Answer.IsCompleted)
                {
                    asked.AskerGaveUp = true;
                    asked.TakenAway.Cancel();
                    Forget(key, asked);
                }
            }
            Forget(key, asked);
            switch (said)
            {
                case Admission.Allowed:
                    allowed.Add(key);
                    return (said, asked.Folder);
                case Admission.AllowedOnce when asker:
                    return (said, asked.Folder);
                case Admission.AllowedOnce:
                case Admission.Unanswered when asked.AskerGaveUp || Allows(key):
                    continue;
                default:
                    return (said, asked.Folder);
            }
        }
        return (Admission.Allowed, window.Folder);
    }

    /// <summary>Whether the person allowed the tool keyed so for the session, or every tool at once.</summary>
    private bool Allows(string key) => allowed.Contains(key) || allowed.Contains(EveryTool);

    /// <summary>Whether a call gives its input to Passenger's own windows (an app_ tool's app ide), where it does what the
    /// person's would.</summary>
    private static bool OnOwnWindows(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null && arguments.TryGetValue("app", out JsonElement app) && app.ValueKind == JsonValueKind.String
        && app.GetString() == PassengerTools.OwnWindows;

    private void Forget(string tool, Asked asked)
    {
        if (allowing.TryGetValue(tool, out Asked? now) && now == asked)
        {
            allowing.Remove(tool);
        }
    }

    private void Admit(MainWindow window)
    {
        if (Admitted || Gone.IsCancellationRequested)
        {
            return;
        }
        Admitted = true;
        window.Agents++;
    }

    private static string Shown(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    /// <summary>A question while it is asked: its answer, what takes it away unanswered, the folder it was asked in, and
    /// how many calls wait on it.</summary>
    private sealed class Asked(Task<Admission> answer, CancellationTokenSource takenAway, string? folder)
    {
        public Task<Admission> Answer { get; } = answer;

        public CancellationTokenSource TakenAway { get; } = takenAway;

        public string? Folder { get; } = folder;

        public int Waiting { get; set; }

        /// <summary>A tool's question taken away as the call it shows gave up, which the other calls waiting ask again for.</summary>
        public bool AskerGaveUp { get; set; }
    }
}

/// <summary>A tool as one connection reaches it: its call cancelled with the connection, and on a person's window held
/// until the agent is let in, then allowed by them when it changes something (not read-only nor declared harmless),
/// and named in the status bar while it runs; after a quiet spell, run once the window looked again for what the system
/// dropped (<see cref="PassengerTools.LookedAgainAsync"/>); told to the agent as it goes and logged (<see cref="AgentCall"/>),
/// an exception it ends with written to the IDE's log whole, its stack and what it wrapped with it.</summary>
internal sealed class ConnectedTool(McpServerTool tool, MainWindow? window, AgentConnection connection, Task<PassengerTools> tools) : DelegatingMcpServerTool(tool)
{
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Gone);
        Tool shown = ProtocolTool;
        await using AgentCall told = AgentCall.Begin(request.Server, connection, request.Params?.ProgressToken, shown.Name, shown.Title ?? shown.Name,
            AgentConnection.NameOf(request.Server.ClientInfo), connection.Attached ? "window" : "mcp", IdeLog.Traces);
        try
        {
            if (Misgiven(shown, request.Params?.Arguments) is { } misgiven)
            {
                told.Ended("answered an error");
                return Refused(misgiven);
            }
            CallToolResult answer = await ServeAsync(request, shown, told, call.Token);
            told.Ended(answer.IsError == true ? "answered an error" : "answered");
            return answer;
        }
        catch (OperationCanceledException)
        {
            told.Ended(cancellationToken.IsCancellationRequested ? "given up by the agent" : "ended as the agent went");
            throw;
        }
        catch (Exception error)
        {
            told.Ended($"failed ({error.GetType().Name})");
            // The agent hears its words alone (the SDK's answer): what threw it, and what it wrapped, are kept here.
            IdeErrors.Write($"An agent's {shown.Name} failed: {error}");
            throw;
        }
    }

    private async Task<CallToolResult> ServeAsync(RequestContext<CallToolRequestParams> request, Tool shown, AgentCall told, CancellationToken cancel)
    {
        if (!connection.Attached)
        {
            // --mcp's own IDE, whose server answers before the IDE is up: a call waits for it.
            McpHost.Serving.Value = await tools.WaitAsync(cancel);
            await McpHost.Serving.Value.LookedAgainAsync(cancel);
            return await base.InvokeAsync(request, cancel);
        }
        // Read-only or not, no call goes on before the agent is let into the window.
        string? refused = await UiThread.InvokeAsync(() =>
        {
            connection.Named(request.Server.ClientInfo);
            return connection.AdmitAsync(window!, told, cancel);
        });
        // A folder not trusted runs none of its code, nor are its feeds asked, for any agent: the person trusts it first.
        if (refused is null && McpTools.Untrusted.TryGetValue(shown.Name, out string? untrusted)
            && await UiThread.InvokeAsync(() => window!.Session is { Folder: not null, Trusted: false }))
        {
            refused = untrusted;
        }
        if (refused is null && Asks(shown) && !ChangesNothing(shown, request.Params?.Arguments))
        {
            refused = await UiThread.InvokeAsync(() => connection.AllowAsync(window!, shown, request.Params?.Arguments, told, cancel));
        }
        if (refused is not null)
        {
            cancel.ThrowIfCancellationRequested();
            return Refused(refused);
        }
        told.Logged("working");
        // The tools are every connection's (McpHost.Tools): the call reaches this one's.
        McpHost.Serving.Value = await tools;
        await McpHost.Serving.Value.LookedAgainAsync(cancel);
        if (shown.Annotations is { ReadOnlyHint: true })
        {
            return await base.InvokeAsync(request, cancel);
        }
        string title = shown.Title ?? shown.Name;
        UiThread.Post(() => window!.AgentWorkStarted(title));
        try
        {
            return await base.InvokeAsync(request, cancel);
        }
        finally
        {
            UiThread.Post(() => window!.AgentWorkEnded(title));
        }
    }

    private static CallToolResult Refused(string why) => new() { IsError = true, Content = [new TextContentBlock { Text = why }] };

    /// <summary>Whether a tool's calls on a person's window wait for them to allow it: it may change something, being neither
    /// read-only nor declared harmless.</summary>
    internal static bool Asks(Tool tool) => tool.Annotations is not ({ ReadOnlyHint: true } or { DestructiveHint: false });

    /// <summary>Whether a call of a tool that may change something changes nothing by what it is given, and so asks nobody:
    /// godot_editor's reads (<see cref="PassengerTools.GodotEditorReads"/>).</summary>
    private static bool ChangesNothing(Tool tool, IDictionary<string, JsonElement>? arguments) =>
        tool.Name == McpTools.GodotEditor && arguments is not null && arguments.TryGetValue("action", out JsonElement action)
        && action.ValueKind == JsonValueKind.String && PassengerTools.GodotEditorReads.Contains(action.GetString()!.Trim().ToLowerInvariant());

    /// <summary>What is wrong with a call's arguments by its tool's schema (a parameter it lacks, a required one missing, a
    /// value of another type), with every parameter it takes, else null: the SDK answers each with no word of which.</summary>
    private static string? Misgiven(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        JsonElement schema = tool.InputSchema;
        List<(string Name, string Type)> parameters = [];
        if (JsonMembers.Object(schema, "properties") is { } properties)
        {
            parameters.AddRange(properties.EnumerateObject().Select(p => (p.Name, JsonMembers.Text(p.Value, "type") ?? "any")));
        }
        HashSet<string> required = [.. JsonMembers.Texts(schema, "required")];
        List<string> wrong = [];
        foreach ((string name, JsonElement value) in arguments ?? new Dictionary<string, JsonElement>())
        {
            if (parameters.FindIndex(p => p.Name == name) is int at and >= 0)
            {
                if (!Fits(value, parameters[at].Type))
                {
                    wrong.Add($"takes {(parameters[at].Type is "integer" or "array" or "object" ? "an" : "a")} {parameters[at].Type} as {name}, not {value.GetRawText()}");
                }
            }
            else
            {
                wrong.Add($"has no parameter {name}");
            }
        }
        wrong.AddRange(parameters.Where(p => required.Contains(p.Name) && arguments?.ContainsKey(p.Name) != true).Select(p => $"needs {p.Name}"));
        return wrong.Count == 0 ? null
            : $"{tool.Name} {string.Join(", and ", wrong)}. Its parameters: {(parameters.Count == 0 ? "none" : string.Join(", ", parameters.Select(p => $"{p.Name} ({p.Type}{(required.Contains(p.Name) ? ", required" : string.Empty)})")))}";

        // A number written as a string passes, as the SDK reads one so; null is the SDK's to judge.
        static bool Fits(JsonElement value, string type) => value.ValueKind == JsonValueKind.Null || type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out _)
                : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "number" => value.ValueKind == JsonValueKind.Number
                || value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => true,
        };
    }
}
