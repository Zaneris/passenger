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

    private AgentSocket(MainWindow window, string path, Socket listener)
    {
        this.window = window;
        this.listener = listener;
        Path = path;
        // Not held: it listens for as long as the window does.
        IdeErrors.Observe(Task.Run(AcceptAsync));
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

    private async Task AcceptAsync()
    {
        while (!closed.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(closed.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            IdeErrors.Observe(ServeAsync(client));
        }
    }

    private async Task ServeAsync(Socket client)
    {
        using CancellationTokenSource gone = CancellationTokenSource.CreateLinkedTokenSource(closed.Token, letIn.Token);
        AgentConnection connection = new(gone.Token, attached: true);
        clients[client] = new Served(connection, gone);
        try
        {
            await using NetworkStream stream = new(client, ownsSocket: true);
            // Its tools are made once a process, off the UI thread, which the first connection's would hold; nothing drawn changes.
            McpServerOptions options = await Task.Run(() => McpHost.Options(window, connection));
            StreamServerTransport transport = new(stream, stream, "passenger");
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
}

/// <summary>What the person said to an agent's question: whether it may work in the window, use a tool, open a folder.</summary>
public enum Admission
{
    Allowed,
    NotAllowed,

    /// <summary>Nobody answered within the window's limit, or every call waiting for the answer went first.</summary>
    Unanswered,
}

/// <summary>One agent's connection: its calls end when it does; on a person's window (attached) it is let in before its
/// first call goes on, and a tool that changes something waits the first time for the person to allow it, each question
/// waited for at most the window's <see cref="MainWindow.AgentAnswerLimit"/>. UI thread only, but for <see cref="Gone"/>.</summary>
public sealed class AgentConnection(CancellationToken gone, bool attached)
{
    /// <summary>The tools the person allowed, which the agent uses unasked until it goes or the window opens another folder.</summary>
    private readonly HashSet<string> allowed = new(StringComparer.Ordinal);

    /// <summary>Each tool's question while the person is asked it, which every call of that tool waits on.</summary>
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
            asking = new(window.AskToLetInAsync(Agent, takenAway.Token), takenAway);
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
        return said == Admission.NotAllowed
            ? $"The person at {Where(window)} did not let {Agent} work there. Ask them, or do without it; a later call asks them again."
            : $"Nobody at {Where(window)} answered within {Limit(window)} whether {Agent} may work there. A later call asks again.";
    }

    /// <summary>The window as an answer names it.</summary>
    internal static string Where(MainWindow window) => window.Folder is { } folder ? $"the Passenger window on {folder}" : "the Passenger window";

    /// <summary>The window's limit on a question, as an answer says it.</summary>
    internal static string Limit(MainWindow window) =>
        window.AgentAnswerLimit.TotalSeconds >= 120 && window.AgentAnswerLimit.TotalSeconds % 60 == 0
            ? $"{window.AgentAnswerLimit.TotalMinutes:0} minutes"
            : $"{window.AgentAnswerLimit.TotalSeconds:0} s";

    /// <summary>The window opened another folder and the agent stays let in (its own open, or agents let in unasked there):
    /// the tools allowed in the one it left are asked for again.</summary>
    internal void Moved()
    {
        UiThread.Check();
        allowed.Clear();
    }

    /// <summary>The connection ended: the window stops counting it.</summary>
    internal void Left(MainWindow window)
    {
        UiThread.Check();
        if (Admitted)
        {
            Admitted = false;
            window.Agents--;
        }
    }

    /// <summary>Whether the person lets this agent use a tool, or why not: asked the first time, and again after a no or no
    /// answer within the window's limit; never on a window nobody is at (<see cref="MainWindow.AgentsUnattended"/>), whose
    /// agent uses them all. Every call of the tool waits on the one question, which goes away unanswered once none waits.</summary>
    internal async Task<string?> AllowAsync(MainWindow window, Tool tool, IDictionary<string, JsonElement>? arguments, AgentCall call, CancellationToken cancel)
    {
        UiThread.Check();
        if (window.AgentsUnattended || allowed.Contains(tool.Name))
        {
            return null;
        }
        if (!allowing.TryGetValue(tool.Name, out Asked? asked))
        {
            CancellationTokenSource takenAway = CancellationTokenSource.CreateLinkedTokenSource(Gone);
            asked = new(window.AllowAgentAsync(Agent, tool.Name, tool.Title ?? tool.Name,
                [.. (arguments ?? new Dictionary<string, JsonElement>()).Select(a => (a.Key, Shown(a.Value)))], takenAway.Token), takenAway);
            allowing[tool.Name] = asked;
        }
        asked.Waiting++;
        call.Waits($"Waiting for the person at {Where(window)} to allow {Agent} to use {tool.Name} (at most {Limit(window)})");
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
                Forget(tool.Name, asked);
            }
        }
        Forget(tool.Name, asked);
        if (said == Admission.Allowed)
        {
            allowed.Add(tool.Name);
            return null;
        }
        return said == Admission.NotAllowed
            ? $"The person at {Where(window)} did not allow {tool.Name}. Ask them, or do without it; a later call asks them again."
            : $"Nobody at {Where(window)} answered within {Limit(window)} whether {Agent} may use {tool.Name}. A later call asks again.";
    }

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

    /// <summary>A tool's question while it is asked: its answer, what takes it away unanswered, and how many calls wait on it.</summary>
    private sealed class Asked(Task<Admission> answer, CancellationTokenSource takenAway)
    {
        public Task<Admission> Answer { get; } = answer;

        public CancellationTokenSource TakenAway { get; } = takenAway;

        public int Waiting { get; set; }
    }
}

/// <summary>A tool as one connection reaches it: its call cancelled with the connection, and on a person's window held
/// until the agent is let in, then first allowed by them when it changes something (not read-only nor declared harmless),
/// and named in the status bar while it runs; told to the agent as it goes and logged (<see cref="AgentCall"/>).</summary>
internal sealed class ConnectedTool(McpServerTool tool, MainWindow? window, AgentConnection connection, Task<PassengerTools> tools) : DelegatingMcpServerTool(tool)
{
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Gone);
        Tool shown = ProtocolTool;
        await using AgentCall told = AgentCall.Begin(request.Server, request.Params?.ProgressToken, shown.Name, shown.Title ?? shown.Name,
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
            throw;
        }
    }

    private async Task<CallToolResult> ServeAsync(RequestContext<CallToolRequestParams> request, Tool shown, AgentCall told, CancellationToken cancel)
    {
        if (!connection.Attached)
        {
            // --mcp's own IDE, whose server answers before the IDE is up: a call waits for it.
            McpHost.Serving.Value = await tools.WaitAsync(cancel);
            return await base.InvokeAsync(request, cancel);
        }
        // Read-only or not, no call goes on before the agent is let into the window.
        string? refused = await UiThread.InvokeAsync(() =>
        {
            connection.Named(request.Server.ClientInfo);
            return connection.AdmitAsync(window!, told, cancel);
        });
        if (refused is null && shown.Annotations is not ({ ReadOnlyHint: true } or { DestructiveHint: false }))
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

    /// <summary>What is wrong with a call's arguments by its tool's schema (a parameter it lacks, a required one missing, a
    /// value of another type), with every parameter it takes, else null: the SDK answers each with no word of which.</summary>
    private static string? Misgiven(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        JsonElement schema = tool.InputSchema;
        List<(string Name, string Type)> parameters = [];
        if (schema.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object)
        {
            parameters.AddRange(properties.EnumerateObject().Select(p =>
                (p.Name, p.Value.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : "any")));
        }
        HashSet<string> required = schema.TryGetProperty("required", out JsonElement needed) && needed.ValueKind == JsonValueKind.Array
            ? [.. needed.EnumerateArray().Select(n => n.GetString() ?? string.Empty)]
            : [];
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
