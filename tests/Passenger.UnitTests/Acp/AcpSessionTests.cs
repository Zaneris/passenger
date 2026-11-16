using System.Text.Json.Nodes;
using Passenger.Acp;

namespace Passenger.UnitTests.Acp;

/// <summary>The ACP session played against a stand-in agent: what it sends, and what it makes of what the agent sends, from
/// lines the Claude adapter sent in real runs.</summary>
public sealed class AcpSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // Stops a waiting test at once when the run is cancelled.
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Initialize_reads_the_agent_and_declares_no_file_access_without_handlers()
    {
        await using StandInAgent agent = new();
        Task<AgentInfo> initialized = agent.Session.InitializeAsync(Stop);
        JsonNode request = await agent.AnswerAsync("initialize", """{"protocolVersion":1,"agentInfo":{"name":"claude-agent-acp","title":"Claude Agent","version":"0.107.3"}}""");

        AgentInfo info = await initialized.WaitAsync(Patience, Stop);
        Assert.Equal("Claude Agent", info.Title);
        Assert.Equal("0.107.3", info.Version);
        Assert.False(request["params"]!["clientCapabilities"]!["fs"]!["writeTextFile"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_new_session_reads_its_id_and_modes()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();

        Assert.Equal("sess_1", agent.Session.SessionId);
        Assert.Equal("auto", agent.Session.CurrentModeId);
        Assert.Equal(["default", "plan"], agent.Session.Modes.Select(mode => mode.Id));
    }

    [Fact]
    public async Task Reply_chunks_arrive_in_order_and_the_turn_ends_with_its_stop_reason()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();
        List<string> chunks = [];
        agent.Session.TextReceived += chunk => chunks.Add(chunk.Text);

        Task<TurnEnded> turn = agent.Session.PromptAsync("hi", Stop);
        JsonNode prompt = await agent.ReceiveAsync();
        foreach (string text in new[] { "I'll ", "add a ", "README." })
        {
            await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":""" + JsonValue.Create(text).ToJsonString() + "}}}}");
        }

        await agent.SendAsync($$$"""{"jsonrpc":"2.0","id":{{{prompt["id"]!.ToJsonString()}}},"result":{"stopReason":"end_turn"}}""");

        Assert.Equal("end_turn", (await turn.WaitAsync(Patience, Stop)).StopReason);
        Assert.Equal(["I'll ", "add a ", "README."], chunks);
    }

    [Fact]
    public async Task A_tool_calls_updates_merge_into_one_call()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();
        List<ToolCall> seen = [];
        agent.Session.ToolCallChanged += seen.Add;

        Task<TurnEnded> turn = agent.Session.PromptAsync("add a README", Stop);
        JsonNode prompt = await agent.ReceiveAsync();
        // As the Claude adapter sent them in Phase 1: an empty card, then its title, then its diff, then done.
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"toolCallId":"call_7","sessionUpdate":"tool_call","status":"pending","title":"Preparing file…","kind":"edit","content":[],"locations":[]}}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"toolCallId":"call_7","sessionUpdate":"tool_call_update","title":"Write README.md","locations":[{"path":"C:\\work\\demo\\README.md"}]}}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"toolCallId":"call_7","sessionUpdate":"tool_call_update","content":[{"type":"diff","path":"C:\\work\\demo\\README.md","oldText":null,"newText":"# Demo\n"}]}}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"toolCallId":"call_7","sessionUpdate":"tool_call_update","status":"completed"}}}""");
        await agent.SendAsync($$$"""{"jsonrpc":"2.0","id":{{{prompt["id"]!.ToJsonString()}}},"result":{"stopReason":"end_turn"}}""");
        await turn.WaitAsync(Patience, Stop);

        Assert.Equal(4, seen.Count);
        ToolCall last = seen[^1];
        Assert.Equal("Write README.md", last.Title);
        Assert.Equal("edit", last.Kind);
        Assert.Equal("completed", last.Status);
        Assert.Equal(["C:\\work\\demo\\README.md"], last.Locations);
        FileDiff diff = Assert.Single(last.Diffs);
        Assert.Null(diff.OldText);
        Assert.Equal("# Demo\n", diff.NewText);
    }

    [Fact]
    public async Task A_plan_replaces_the_whole_list()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();
        TaskCompletionSource<PlanUpdated> planned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.Session.PlanUpdated += plan => planned.TrySetResult(plan);

        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"plan","entries":[{"content":"Read the project files","priority":"high","status":"completed"},{"content":"Write README.md","priority":"high","status":"in_progress"}]}}}""");

        PlanUpdated plan = await planned.Task.WaitAsync(Patience, Stop);
        Assert.Equal(["completed", "in_progress"], plan.Entries.Select(entry => entry.Status));
        Assert.Equal("Write README.md", plan.Entries[1].Content);
    }

    [Fact]
    public async Task A_permission_is_answered_with_the_persons_choice_and_carries_the_diff()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();
        PermissionRequest? asked = null;
        agent.Session.AskPermission = (request, _) =>
        {
            asked = request;
            return Task.FromResult<string?>("allow-once");
        };

        // As the Claude adapter asked it in Phase 1, before writing hello.py.
        await agent.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"sess_1","toolCall":{"toolCallId":"call_9","status":"pending","title":"Write hello.py","kind":"edit","content":[{"type":"diff","path":"C:\\work\\demo\\hello.py","oldText":null,"newText":"print('hi')\n"}]},"options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"reject","name":"No","kind":"reject_once"}]}}""");
        JsonNode answer = await agent.ReceiveAsync();

        Assert.Equal(0, answer["id"]!.GetValue<int>());
        Assert.Equal("selected", answer["result"]!["outcome"]!["outcome"]!.GetValue<string>());
        Assert.Equal("allow-once", answer["result"]!["outcome"]!["optionId"]!.GetValue<string>());
        Assert.NotNull(asked);
        Assert.Equal("Write hello.py", asked.ToolCall.Title);
        Assert.Equal("C:\\work\\demo\\hello.py", Assert.Single(asked.ToolCall.Diffs).Path);
        Assert.Equal(["allow_once", "reject_once"], asked.Options.Select(option => option.Kind));
    }

    [Fact]
    public async Task Stop_answers_an_open_permission_cancelled_and_tells_the_agent()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();
        TaskCompletionSource asking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // A person who never clicks.
        agent.Session.AskPermission = (_, _) =>
        {
            asking.TrySetResult();
            return new TaskCompletionSource<string?>().Task;
        };
        using CancellationTokenSource stop = new();

        Task<TurnEnded> turn = agent.Session.PromptAsync("write it", stop.Token);
        JsonNode prompt = await agent.ReceiveAsync();
        await agent.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"sess_1","toolCall":{"toolCallId":"call_9","title":"Write hello.py"},"options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"}]}}""");
        await asking.Task.WaitAsync(Patience, Stop);
        stop.Cancel();

        List<JsonNode> sent = [await agent.ReceiveAsync(), await agent.ReceiveAsync()];
        JsonNode answer = sent.Single(message => message["id"] is not null);
        Assert.Equal("cancelled", answer["result"]!["outcome"]!["outcome"]!.GetValue<string>());
        Assert.Contains(sent, message => message["method"]?.GetValue<string>() == "session/cancel");

        await agent.SendAsync($$$"""{"jsonrpc":"2.0","id":{{{prompt["id"]!.ToJsonString()}}},"result":{"stopReason":"cancelled"}}""");
        Assert.Equal("cancelled", (await turn.WaitAsync(Patience, Stop)).StopReason);
    }

    [Fact]
    public async Task A_question_the_session_cannot_answer_is_refused_not_ignored()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();

        await agent.SendAsync("""{"jsonrpc":"2.0","id":"q1","method":"terminal/create","params":{"sessionId":"sess_1","command":"ls"}}""");
        JsonNode answer = await agent.ReceiveAsync();

        Assert.Equal("q1", answer["id"]!.GetValue<string>());
        Assert.Equal(AcpError.MethodNotFound, answer["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_request_waiting_when_the_agent_ends_fails_instead_of_hanging()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();

        Task<TurnEnded> turn = agent.Session.PromptAsync("hi", Stop);
        await agent.ReceiveAsync();
        agent.CloseOutput();

        AcpError error = await Assert.ThrowsAsync<AcpError>(() => turn.WaitAsync(Patience, Stop));
        Assert.Equal(AcpError.ConnectionClosed, error.Code);
    }

    [Fact]
    public async Task Settings_come_with_the_session_and_a_set_one_takes_the_agents_answer()
    {
        await using StandInAgent agent = new();
        Task<AgentInfo> initialized = agent.Session.InitializeAsync(Stop);
        await agent.AnswerAsync("initialize", """{"protocolVersion":1,"agentInfo":{"name":"claude-agent-acp"}}""");
        await initialized.WaitAsync(Patience, Stop);
        Task<string> created = agent.Session.NewSessionAsync("/work", Stop);
        // As the Claude adapter answered, its options cut to two of each.
        await agent.AnswerAsync("session/new", """{"sessionId":"sess_1","configOptions":[{"id":"mode","name":"Mode","category":"mode","type":"select","currentValue":"auto","options":[{"value":"default","name":"Manual","description":"Always ask before making changes"},{"value":"acceptEdits","name":"Accept edits"}]},{"id":"model","name":"Model","category":"model","type":"select","currentValue":"opus","options":[{"value":"opus","name":"Opus 5.5"},{"value":"sonnet","name":"Sonnet 5.5"}]}]}""");
        await created.WaitAsync(Patience, Stop);

        Assert.Equal(["mode", "model"], agent.Session.Config.Select(option => option.Id));
        Assert.Equal("auto", agent.Session.CurrentModeId);
        Assert.Equal("Always ask before making changes", agent.Session.Config[0].Choices[0].Description);

        Task set = agent.Session.SetConfigAsync("model", "sonnet", Stop);
        JsonNode request = await agent.AnswerAsync("session/set_config_option", """{"configOptions":[{"id":"model","name":"Model","type":"select","currentValue":"sonnet","options":[{"value":"opus","name":"Opus 5.5"},{"value":"sonnet","name":"Sonnet 5.5"}]}]}""");
        await set.WaitAsync(Patience, Stop);

        Assert.Equal("model", request["params"]!["configId"]!.GetValue<string>());
        Assert.Equal("sonnet", request["params"]!["value"]!.GetValue<string>());
        Assert.Equal("sonnet", agent.Session.Config.Single().Current);
    }

    [Fact]
    public async Task The_agents_commands_its_account_and_its_plans_usage_are_read()
    {
        await using StandInAgent agent = new();
        await agent.StartSessionAsync();
        TaskCompletionSource<IReadOnlyList<AgentCommand>> commands = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<AccountStatus> account = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<UsageUpdated> usage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.Session.CommandsChanged += told => commands.TrySetResult(told);
        agent.Session.AccountChanged += told => account.TrySetResult(told);
        agent.Session.UsageUpdated += told => usage.TrySetResult(told);

        // Lines as the Claude adapter sent them in a real run.
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"code-review","description":"Review the current diff","input":{"hint":"<effort> <target>"}},{"name":"compact","description":"Clear the conversation but keep a summary"}]}}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"auth/status_update","params":{"state":"authenticated","accounts":[{"label":"Claude Pro","active":true,"email":"person@example.com","organization":"Org"}]}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"usage_update","used":38477,"size":1000000,"_meta":{"_claude/rateLimit":{"status":"allowed","unifiedWindows":{"five_hour":{"utilization":0.3,"resetsAt":1794848400,"status":"allowed"},"seven_day":{"utilization":0.04,"resetsAt":1795428000,"status":"allowed"}}}}}}}""");

        IReadOnlyList<AgentCommand> offered = await commands.Task.WaitAsync(Patience, Stop);
        Assert.Equal(["code-review", "compact"], offered.Select(command => command.Name));
        Assert.Equal("<effort> <target>", offered[0].Hint);
        AccountStatus signedIn = await account.Task.WaitAsync(Patience, Stop);
        Assert.Equal("authenticated", signedIn.State);
        Assert.Equal(("Claude Pro", "person@example.com", true), (signedIn.Accounts[0].Label, signedIn.Accounts[0].Email, signedIn.Accounts[0].Active));
        UsageUpdated used = await usage.Task.WaitAsync(Patience, Stop);
        Assert.Equal((38477, 1000000), (used.Used, used.Size));
        Assert.Equal([("five_hour", 0.3), ("seven_day", 0.04)], used.Windows.Select(window => (window.Name, window.Used)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794848400), used.Windows[0].Resets);
    }
}
