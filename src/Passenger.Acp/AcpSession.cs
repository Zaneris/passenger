using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Passenger.Acp;

/// <summary>One conversation with an ACP agent: the methods the client calls, and the agent's updates and questions
/// turned into the types in AcpTypes. Every ACP message shape lives in this class, so a protocol change is fixed here.
/// Events are raised on the connection's reading thread; a UI moves them onto its own thread.</summary>
public sealed class AcpSession : IAsyncDisposable
{
    private readonly JsonRpcConnection connection;
    private readonly ConcurrentDictionary<string, ToolCall> toolCalls = new();
    private CancellationTokenSource turn = new();

    public AcpSession(JsonRpcConnection connection)
    {
        this.connection = connection;
        connection.NotificationReceived += OnNotification;
        connection.RequestHandler = OnRequestAsync;
    }

    /// <summary>A session over the streams of an agent the host has started.</summary>
    public static AcpSession Over(Stream fromAgent, Stream toAgent)
    {
        return new AcpSession(new JsonRpcConnection(fromAgent, toAgent));
    }

    public JsonRpcConnection Connection => connection;

    public AgentInfo? Agent { get; private set; }

    public string? SessionId { get; private set; }

    public IReadOnlyList<SessionMode> Modes { get; private set; } = [];

    public string? CurrentModeId { get; private set; }

    public event Action<TextChunk>? TextReceived;

    public event Action<PlanUpdated>? PlanUpdated;

    public event Action<ToolCall>? ToolCallChanged;

    public event Action<UsageUpdated>? UsageUpdated;

    /// <summary>The session's settings changed (mode, model, effort…), all of them given.</summary>
    public event Action<IReadOnlyList<ConfigOption>>? ConfigChanged;

    /// <summary>The slash commands and skills the agent offers, all of them given.</summary>
    public event Action<IReadOnlyList<AgentCommand>>? CommandsChanged;

    /// <summary>Who the agent is signed in as.</summary>
    public event Action<AccountStatus>? AccountChanged;

    /// <summary>The session's settings as last told.</summary>
    public IReadOnlyList<ConfigOption> Config { get; private set; } = [];

    /// <summary>Any update this class does not turn into a type yet, by its sessionUpdate kind, for a log.</summary>
    public event Action<string, JsonNode>? OtherUpdate;

    /// <summary>Asks the person; returns an option's id, or null to cancel. The token is cancelled when the turn is stopped,
    /// and then the agent is told cancelled at once, whatever the person does.</summary>
    public Func<PermissionRequest, CancellationToken, Task<string?>>? AskPermission { get; set; }

    /// <summary>Reads a file for the agent. Set before <see cref="InitializeAsync"/>, or the agent reads files itself.</summary>
    public Func<FileReadRequest, Task<string>>? ReadFile { get; set; }

    /// <summary>Writes a file for the agent, or returns false if the person rejected it. Set before
    /// <see cref="InitializeAsync"/>, or the agent writes files itself.</summary>
    public Func<FileWriteRequest, Task<bool>>? WriteFile { get; set; }

    public async Task<AgentInfo> InitializeAsync(CancellationToken cancel = default)
    {
        connection.Start();
        JsonNode result = await connection.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = ReadFile is not null, ["writeTextFile"] = WriteFile is not null },
            },
        }, cancel);
        JsonNode? info = result["agentInfo"];
        Agent = new AgentInfo(Text(info?["name"]) ?? "unknown agent", Text(info?["title"]), Text(info?["version"]));
        return Agent;
    }

    /// <summary>Starts a conversation in <paramref name="folder"/>, which must be an absolute path.</summary>
    public async Task<string> NewSessionAsync(string folder, CancellationToken cancel = default)
    {
        JsonNode result = await connection.RequestAsync("session/new", new JsonObject
        {
            ["cwd"] = folder,
            ["mcpServers"] = new JsonArray(),
        }, cancel);
        SessionId = Text(result["sessionId"]) ?? throw new AcpError(AcpError.InternalError, "session/new returned no sessionId");
        JsonNode? modes = result["modes"];
        CurrentModeId = Text(modes?["currentModeId"]);
        List<SessionMode> available = [];
        foreach (JsonNode? mode in modes?["availableModes"] as JsonArray ?? [])
        {
            if (Text(mode?["id"]) is string id)
            {
                available.Add(new SessionMode(id, Text(mode?["name"]) ?? id, Text(mode?["description"])));
            }
        }

        Modes = available;
        TakeConfig(result["configOptions"]);
        return SessionId;
    }

    /// <summary>Sets one of the session's settings (mode, model, effort…) to one of its choices.</summary>
    public async Task SetConfigAsync(string configId, string value, CancellationToken cancel = default)
    {
        JsonNode result = await connection.RequestAsync("session/set_config_option", new JsonObject
        {
            ["sessionId"] = RequireSession(),
            ["configId"] = configId,
            ["value"] = value,
        }, cancel);
        TakeConfig(result["configOptions"]);
    }

    /// <summary>The agent's past conversations in <paramref name="folder"/>, the latest first as the agent lists them.</summary>
    public async Task<IReadOnlyList<PastSession>> ListSessionsAsync(string folder, CancellationToken cancel = default)
    {
        JsonNode result = await connection.RequestAsync("session/list", new JsonObject { ["cwd"] = folder }, cancel);
        List<PastSession> sessions = [];
        foreach (JsonNode? session in result["sessions"] as JsonArray ?? [])
        {
            if (Text(session?["sessionId"]) is string id)
            {
                sessions.Add(new PastSession(id, Text(session?["title"]), When(session?["updatedAt"])));
            }
        }
        return sessions;
    }

    public async Task SetModeAsync(string modeId, CancellationToken cancel = default)
    {
        await connection.RequestAsync("session/set_mode", new JsonObject
        {
            ["sessionId"] = RequireSession(),
            ["modeId"] = modeId,
        }, cancel);
        CurrentModeId = modeId;
    }

    /// <summary>Sends the person's message and waits for the turn to end. Cancelling <paramref name="stop"/> is the Stop
    /// button: the agent is told to stop and the turn still ends with its answer, normally cancelled.</summary>
    public async Task<TurnEnded> PromptAsync(string text, CancellationToken stop = default)
    {
        CancellationTokenSource thisTurn = new();
        Interlocked.Exchange(ref turn, thisTurn).Dispose();
        using CancellationTokenRegistration stopping = stop.Register(() => _ = CancelAsync());
        JsonNode result = await connection.RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = RequireSession(),
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        });
        return new TurnEnded(Text(result["stopReason"]) ?? "end_turn");
    }

    /// <summary>Stops the current turn: open permission questions are answered cancelled, then the agent is told.</summary>
    public async Task CancelAsync()
    {
        turn.Cancel();
        await connection.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = RequireSession() });
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        if (method == "auth/status_update")
        {
            List<AgentAccount> accounts = [];
            foreach (JsonNode? account in parameters?["accounts"] as JsonArray ?? [])
            {
                accounts.Add(new AgentAccount(Text(account?["label"]) ?? "Account", Text(account?["email"]), account?["active"] is JsonValue active && active.TryGetValue(out bool on) && on));
            }
            AccountChanged?.Invoke(new AccountStatus(Text(parameters?["state"]) ?? "unknown", [.. accounts.OrderByDescending(account => account.Active)]));
            return;
        }
        if (method != "session/update" || parameters?["update"] is not JsonObject update)
        {
            return;
        }

        string kind = Text(update["sessionUpdate"]) ?? "";
        switch (kind)
        {
            case "agent_message_chunk":
                if (Text(update["content"]?["text"]) is string chunk)
                {
                    TextReceived?.Invoke(new TextChunk(chunk));
                }

                break;
            case "plan":
                PlanUpdated?.Invoke(new PlanUpdated(ReadPlan(update["entries"])));
                break;
            case "tool_call":
            case "tool_call_update":
                if (Merge(update) is ToolCall call)
                {
                    ToolCallChanged?.Invoke(call);
                }

                break;
            case "usage_update":
                UsageUpdated?.Invoke(new UsageUpdated(Number(update["used"]), Number(update["size"]), Windows(update["_meta"]?["_claude/rateLimit"]?["unifiedWindows"])));
                break;
            case "config_option_update":
                TakeConfig(update["configOptions"]);
                break;
            case "available_commands_update":
                List<AgentCommand> commands = [];
                foreach (JsonNode? command in update["availableCommands"] as JsonArray ?? [])
                {
                    if (Text(command?["name"]) is string name)
                    {
                        commands.Add(new AgentCommand(name, Text(command?["description"]), Text(command?["input"]?["hint"])));
                    }
                }
                CommandsChanged?.Invoke(commands);
                break;
            case "current_mode_update":
                CurrentModeId = Text(update["currentModeId"]) ?? CurrentModeId;
                OtherUpdate?.Invoke(kind, update);
                break;
            default:
                OtherUpdate?.Invoke(kind, update);
                break;
        }
    }

    private async Task<JsonNode?> OnRequestAsync(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "session/request_permission":
                return await AnswerPermissionAsync(parameters);
            case "fs/read_text_file" when ReadFile is not null:
                string content = await ReadFile(new FileReadRequest(
                    Text(parameters?["path"]) ?? "", OptionalInt(parameters?["line"]), OptionalInt(parameters?["limit"])));
                return new JsonObject { ["content"] = content };
            case "fs/write_text_file" when WriteFile is not null:
                bool written = await WriteFile(new FileWriteRequest(Text(parameters?["path"]) ?? "", Text(parameters?["content"]) ?? ""));
                return written ? new JsonObject() : throw new AcpError(AcpError.Refused, "The user rejected this change.");
            default:
                throw new AcpError(AcpError.MethodNotFound, $"{method} is not supported");
        }
    }

    private async Task<JsonNode> AnswerPermissionAsync(JsonNode? parameters)
    {
        // The question carries the tool call in full, its diff included, so it is merged like any update.
        ToolCall call = parameters?["toolCall"] is JsonObject asked ? Merge(asked) ?? EmptyCall("") : EmptyCall("");
        ToolCallChanged?.Invoke(call);
        List<PermissionOption> options = [];
        foreach (JsonNode? option in parameters?["options"] as JsonArray ?? [])
        {
            if (Text(option?["optionId"]) is string id)
            {
                options.Add(new PermissionOption(id, Text(option?["name"]) ?? id, Text(option?["kind"]) ?? "other"));
            }
        }

        CancellationToken stopped = turn.Token;
        string? chosen = null;
        if (AskPermission is not null && !stopped.IsCancellationRequested)
        {
            try
            {
                chosen = await AskPermission(new PermissionRequest(call, options), stopped).WaitAsync(stopped);
            }
            catch (OperationCanceledException)
            {
                chosen = null;
            }
        }

        JsonObject outcome = chosen is null
            ? new JsonObject { ["outcome"] = "cancelled" }
            : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen };
        return new JsonObject { ["outcome"] = outcome };
    }

    /// <summary>Folds an update into what is known of its tool call: an update carries only the fields that changed.</summary>
    private ToolCall? Merge(JsonObject update)
    {
        if (Text(update["toolCallId"]) is not string id)
        {
            return null;
        }

        ToolCall was = toolCalls.TryGetValue(id, out ToolCall? known) ? known : EmptyCall(id);
        IReadOnlyList<FileDiff> diffs = was.Diffs;
        IReadOnlyList<string> texts = was.Texts;
        if (update["content"] is JsonArray content)
        {
            (diffs, texts) = ReadContent(content);
        }

        IReadOnlyList<string> locations = was.Locations;
        if (update["locations"] is JsonArray places)
        {
            locations = [.. places.Select(place => Text(place?["path"])).OfType<string>()];
        }

        ToolCall now = new(
            id,
            Text(update["title"]) ?? was.Title,
            Text(update["kind"]) ?? was.Kind,
            Text(update["status"]) ?? was.Status,
            diffs,
            texts,
            locations);
        toolCalls[id] = now;
        return now;
    }

    private static (IReadOnlyList<FileDiff> Diffs, IReadOnlyList<string> Texts) ReadContent(JsonArray content)
    {
        List<FileDiff> diffs = [];
        List<string> texts = [];
        foreach (JsonNode? item in content)
        {
            switch (Text(item?["type"]))
            {
                case "diff":
                    diffs.Add(new FileDiff(Text(item?["path"]) ?? "", Text(item?["oldText"]), Text(item?["newText"]) ?? ""));
                    break;
                case "content":
                    if (Text(item?["content"]?["text"]) is string text)
                    {
                        texts.Add(text);
                    }

                    break;
            }
        }

        return (diffs, texts);
    }

    private static List<PlanEntry> ReadPlan(JsonNode? entries)
    {
        List<PlanEntry> plan = [];
        foreach (JsonNode? entry in entries as JsonArray ?? [])
        {
            plan.Add(new PlanEntry(Text(entry?["content"]) ?? "", Text(entry?["priority"]) ?? "medium", Text(entry?["status"]) ?? "pending"));
        }

        return plan;
    }

    /// <summary>Takes the settings an answer or update lists, all of them, where it lists any.</summary>
    private void TakeConfig(JsonNode? options)
    {
        if (options is not JsonArray list)
        {
            return;
        }
        List<ConfigOption> config = [];
        foreach (JsonNode? option in list)
        {
            if (Text(option?["id"]) is not string id)
            {
                continue;
            }
            List<ConfigChoice> choices = [];
            foreach (JsonNode? choice in option?["options"] as JsonArray ?? [])
            {
                if (Text(choice?["value"]) is string value)
                {
                    choices.Add(new ConfigChoice(value, Text(choice?["name"]) ?? value, Text(choice?["description"])));
                }
            }
            config.Add(new ConfigOption(id, Text(option?["name"]) ?? id, Text(option?["description"]), Text(option?["category"]), Text(option?["currentValue"]) ?? string.Empty, choices));
        }
        Config = config;
        if (config.FirstOrDefault(option => option.Id == "mode") is { } mode)
        {
            CurrentModeId = mode.Current;
        }
        ConfigChanged?.Invoke(config);
    }

    /// <summary>The plan's usage windows as Claude Code tells them in its usage updates; none from an agent that does not.</summary>
    private static List<UsageWindow> Windows(JsonNode? windows)
    {
        List<UsageWindow> read = [];
        foreach ((string name, JsonNode? window) in windows as JsonObject ?? [])
        {
            if (window?["utilization"] is JsonValue used && used.TryGetValue(out double share))
            {
                DateTimeOffset? resets = window["resetsAt"] is JsonValue at && at.TryGetValue(out long seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
                read.Add(new UsageWindow(name, share, resets));
            }
        }
        return read;
    }

    private static DateTimeOffset? When(JsonNode? node) =>
        Text(node) is string text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset at) ? at : null;

    private static ToolCall EmptyCall(string id)
    {
        return new ToolCall(id, "", "other", "pending", [], [], []);
    }

    private string RequireSession()
    {
        return SessionId ?? throw new InvalidOperationException("Call NewSessionAsync first.");
    }

    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    private static long Number(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out long number) ? number : 0;
    }

    private static int? OptionalInt(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out int number) ? number : null;
    }

    public async ValueTask DisposeAsync()
    {
        turn.Cancel();
        turn.Dispose();
        await connection.DisposeAsync();
    }
}
