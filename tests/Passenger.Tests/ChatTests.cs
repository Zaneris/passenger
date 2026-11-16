using System.ComponentModel;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.Acp;
using Passenger.App;
using Passenger.App.Chat;

namespace Passenger.Tests;

/// <summary>The Chat window: shown and hidden from its stripe button and shown by its action, as each tool window is, and
/// Connect starting its agent in the open folder, a message sent and its reply streamed, its tool lines, its questions
/// as cards the person answers, Markdown, and Stop, a stand-in in memory answering as the Claude adapter does.</summary>
public sealed class ChatTests
{
    [AvaloniaFact]
    public void Its_stripe_button_shows_the_chat_window_and_hides_it_again()
    {
        MainWindow window = Open();
        Ui.Click(Button(window));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Chat));
        Assert.IsType<ChatView>(window.Chat);
        Assert.True(Button(window).IsChecked);

        Ui.Click(Button(window));
        Assert.False(window.DockHost.IsShown(ToolWindowIds.Chat));
        window.Close();
    }

    [AvaloniaFact]
    public void Its_action_shows_the_chat_window_saying_no_agent_is_connected()
    {
        MainWindow window = Open();
        window.Actions.Run(AppActions.ShowChat);
        Ui.Settle();
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Chat));
        Assert.Equal("No agent connected", window.Chat.Said);
        Assert.True(window.Chat.OffersConnect);
        window.Close();
    }

    [AvaloniaFact]
    public void Connect_starts_the_agent_in_the_open_folder_and_says_which_agent_is_connected()
    {
        string folder = TestFolders.Create("chat");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        StandInAgent? agent = null;
        string? startedIn = null;
        window.StartChatAgent = at =>
        {
            startedIn = at;
            agent = new StandInAgent();
            return Task.FromResult(agent.Agent);
        };
        window.ShowToolWindow(ToolWindowIds.Chat);
        Ui.Settle();

        Ui.Click(ConnectButton(window));
        Ui.WaitFor(() => window.Chat.Said == "Connected to Claude Agent 0.107.3", () => $"the agent connected, where the window says \"{window.Chat.Said}\"");

        Assert.Equal(folder, startedIn);
        Assert.Equal(folder, agent!.SessionFolder);
        Assert.Equal("default", agent.Mode);
        Assert.False(window.Chat.OffersConnect);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_sends_the_message_and_the_reply_streams_in_piece_by_piece()
    {
        MainWindow window = Connected(out StandInAgent agent);

        Ui.Click(window.Chat.Input);
        foreach (char c in "Add a README")
        {
            Ui.Type(window, c == ' ' ? "SPACE" : c.ToString(), c.ToString());
        }
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.Chat.Busy && window.Chat.Conversation.Count == 2, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("Add a README", agent.Prompts.Single());
        Assert.Equal([("You", "Add a README"), ("Claude Agent", "I'll add a README.")], window.Chat.Conversation);
        Assert.Equal(string.Empty, window.Chat.Input.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Stop_ends_the_agents_turn_and_says_it_stopped()
    {
        MainWindow window = Connected(out StandInAgent agent);
        window.Chat.Input.Text = "wait for stop";
        Ui.Click(Named(window, "Send"));
        Ui.WaitFor(() => window.Chat.Busy && agent.Prompts.Count == 1, "the agent working");
        Assert.Equal("Thinking…", window.Chat.Doing);

        Ui.Click(Named(window, "Stop"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn stopped, where the conversation is {Shown(window)}");

        Assert.True(agent.Cancelled);
        Assert.Equal(("", "Stopped"), window.Chat.Conversation[^1]);
        Assert.Null(window.Chat.Doing);
        window.Close();
    }

    [AvaloniaFact]
    public void A_tools_line_updates_in_place_and_the_words_after_it_come_below_it()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "tool");
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        ToolLine line = Assert.Single(window.Chat.Tools);
        Assert.Equal("Read hello.py", line.Title);
        Assert.Equal("completed", line.Status);
        Assert.Equal([("You", "tool"), ("Claude Agent", "Reading it."), ("Claude Agent", "It prints hello.")], window.Chat.Conversation);
        window.Close();
    }

    [AvaloniaFact]
    public void An_edit_waits_on_a_card_with_its_diff_and_Accept_answers_allow_once()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "write");
        Ui.WaitFor(() => window.Chat.Questions.Any(), () => $"the question asked, where the conversation is {Shown(window)}");

        ApprovalCard card = window.Chat.Questions.Single();
        Assert.Equal("Needs approval", card.State);
        Assert.Equal("Waiting for your answer", window.Chat.Doing);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "+ # Notes");
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "+1");
        Assert.Null(agent.Answered);

        Ui.Click(InCard(card, "Yes"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("allow-once", agent.Answered);
        Assert.Equal("Accepted", card.State);
        Assert.Equal(("Claude Agent", "Wrote notes.md."), window.Chat.Conversation[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public void Rejecting_an_edit_tells_the_agent_no()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "write");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the question asked");

        Ui.Click(InCard(window.Chat.Questions.Single(), "No"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("reject", agent.Answered);
        Assert.Equal("Rejected", window.Chat.Questions.Single().State);
        window.Close();
    }

    [AvaloniaFact]
    public void Stop_while_asked_answers_the_question_cancelled()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "write");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the question asked");

        Ui.Click(Named(window, "Stop"));
        Ui.WaitFor(() => agent.Answered is not null, "the agent answered");

        Assert.Equal("cancelled", agent.Answered);
        Assert.Equal("Cancelled", window.Chat.Questions.Single().State);
        window.Close();
    }

    [AvaloniaFact]
    public void A_reply_shows_as_Markdown_once_whole()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "bold");
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal(("Claude Agent", "Use **this** one."), window.Chat.Conversation[^1]);
        Assert.DoesNotContain(window.Chat.GetVisualDescendants().OfType<TextBlock>(), text => (text.Text ?? string.Empty).Contains("**", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void A_suggestion_of_the_empty_conversation_fills_the_box()
    {
        MainWindow window = Connected(out _);
        Ui.Click(Named(window, "Explain how this project is structured"));
        Assert.Equal("Explain how this project is structured", window.Chat.Input.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void An_agent_that_cannot_start_says_why_and_offers_connect_again()
    {
        MainWindow window = Ui.OpenFolder(TestFolders.Create("chat"), languageServer: false);
        window.StartChatAgent = _ => Task.FromException<ChatAgent>(new Win32Exception("npx was not found"));
        window.ShowToolWindow(ToolWindowIds.Chat);
        Ui.Settle();

        Ui.Click(ConnectButton(window));
        Ui.WaitFor(() => window.Chat.Said.StartsWith("Could not start", StringComparison.Ordinal), () => $"the failure said, where the window says \"{window.Chat.Said}\"");

        Assert.Equal("Could not start the agent: npx was not found", window.Chat.Said);
        Assert.True(window.Chat.OffersConnect);
        window.Close();
    }

    private static MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = Passenger.App.State.StateStore.DefaultRoot };
        window.Show();
        Ui.Settle();
        return window;
    }

    private static ToggleButton Button(MainWindow window) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => Equals(b.Tag, ToolWindowIds.Chat));

    private static Button ConnectButton(MainWindow window) => Named(window, "Connect to Claude Code");

    private static Button Named(MainWindow window, string content) =>
        window.Chat.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, content) && b.IsEffectivelyVisible);

    /// <summary>A window on a folder of its own with the Chat window connected to a stand-in agent.</summary>
    private static MainWindow Connected(out StandInAgent agent)
    {
        MainWindow window = Ui.OpenFolder(TestFolders.Create("chat"), languageServer: false);
        StandInAgent started = new();
        window.StartChatAgent = _ => Task.FromResult(started.Agent);
        window.ShowToolWindow(ToolWindowIds.Chat);
        Ui.Settle();
        Ui.Click(ConnectButton(window));
        Ui.WaitFor(() => window.Chat.Said == "Connected to Claude Agent 0.107.3", () => $"the agent connected, where the window says \"{window.Chat.Said}\"");
        agent = started;
        return window;
    }

    private static void Send(MainWindow window, string text)
    {
        window.Chat.Input.Text = text;
        Ui.Click(Named(window, "Send"));
    }

    private static Button InCard(ApprovalCard card, string content) =>
        card.GetVisualDescendants().OfType<Button>().First(b => b.Content is TextBlock { Text: { } text } && text == content);

    private static string Shown(MainWindow window) => string.Join(" | ", window.Chat.Conversation.Select(part => $"{part.Who}: {part.Text}"));

    /// <summary>An agent in memory that answers as the Claude adapter did in real runs: initialize, session/new and
    /// set_mode; a prompt with its reply in three pieces, or by its first word: "wait" nothing until session/cancel;
    /// "tool" a read's call with words before and after it; "write" a question with a new file's diff, then its words;
    /// "bold" Markdown.</summary>
    private sealed class StandInAgent
    {
        private readonly Pipe toSession = new();
        private readonly Pipe fromSession = new();
        private readonly List<string> prompts = [];

        public StandInAgent()
        {
            Agent = new ChatAgent(AcpSession.Over(toSession.Reader.AsStream(), fromSession.Writer.AsStream()), () => toSession.Writer.Complete());
            // Unheld: it answers for as long as the session lives, which no wait for the IDE to be quiet may wait out.
            Passenger.Core.UnawaitedWork.Observe(Passenger.Core.BackgroundWork.Unheld(AnswerAsync));
        }

        public ChatAgent Agent { get; }

        /// <summary>The folder the session was opened in.</summary>
        public string? SessionFolder { get; private set; }

        /// <summary>The mode the session was set to.</summary>
        public string? Mode { get; private set; }

        /// <summary>Whether the turn was cancelled.</summary>
        public bool Cancelled { get; private set; }

        /// <summary>The answer to its question: an option's id, or "cancelled".</summary>
        public string? Answered { get; private set; }

        public IReadOnlyList<string> Prompts
        {
            get
            {
                lock (prompts)
                {
                    return [.. prompts];
                }
            }
        }

        private async Task AnswerAsync()
        {
            UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
            using StreamReader reader = new(fromSession.Reader.AsStream(), utf8);
            await using StreamWriter writer = new(toSession.Writer.AsStream(), utf8) { NewLine = "\n", AutoFlush = true };
            string? waiting = null;
            while (await reader.ReadLineAsync() is string line)
            {
                JsonNode message = JsonNode.Parse(line)!;
                string id = message["id"]?.ToJsonString() ?? "null";
                JsonNode? parameters = message["params"];
                switch (message["method"]?.GetValue<string>())
                {
                    case "initialize":
                        await Answer(writer, id, """{"protocolVersion":1,"agentInfo":{"name":"@agentclientprotocol/claude-agent-acp","title":"Claude Agent","version":"0.107.3"},"authMethods":[]}""");
                        break;
                    case "session/new":
                        SessionFolder = parameters?["cwd"]?.GetValue<string>();
                        await Answer(writer, id, """{"sessionId":"sess_1","modes":{"currentModeId":"auto","availableModes":[{"id":"default","name":"Manual"},{"id":"auto","name":"Auto"}]}}""");
                        break;
                    case "session/set_mode":
                        Mode = parameters?["modeId"]?.GetValue<string>();
                        await Answer(writer, id, "{}");
                        break;
                    case "session/prompt":
                        string text = parameters?["prompt"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                        lock (prompts)
                        {
                            prompts.Add(text);
                        }
                        if (text.StartsWith("wait", StringComparison.Ordinal))
                        {
                            waiting = id;
                            break;
                        }
                        if (text == "tool")
                        {
                            await Say(writer, "Reading it.");
                            await Update(writer, """{"toolCallId":"call_1","sessionUpdate":"tool_call","status":"pending","title":"Read File","kind":"read","content":[]}""");
                            await Update(writer, """{"toolCallId":"call_1","sessionUpdate":"tool_call_update","title":"Read hello.py","locations":[{"path":"/work/hello.py"}]}""");
                            await Update(writer, """{"toolCallId":"call_1","sessionUpdate":"tool_call_update","status":"completed"}""");
                            await Say(writer, "It prints hello.");
                            await Answer(writer, id, """{"stopReason":"end_turn"}""");
                            break;
                        }
                        if (text == "write")
                        {
                            waiting = id;
                            await Update(writer, """{"toolCallId":"call_2","sessionUpdate":"tool_call","status":"pending","title":"Preparing file…","kind":"edit","content":[]}""");
                            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":"ask_1","method":"session/request_permission","params":{"sessionId":"sess_1","toolCall":{"toolCallId":"call_2","status":"pending","title":"Write notes.md","kind":"edit","content":[{"type":"diff","path":"/work/notes.md","oldText":"","newText":"# Notes\n"}]},"options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"allow-always","name":"Yes, allow all edits during this session","kind":"allow_always"},{"optionId":"reject","name":"No","kind":"reject_once"}]}}""");
                            break;
                        }
                        if (text == "bold")
                        {
                            await Say(writer, "Use **this** one.");
                            await Answer(writer, id, """{"stopReason":"end_turn"}""");
                            break;
                        }
                        foreach (string piece in new[] { "I'll ", "add a ", "README." })
                        {
                            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":""" + JsonValue.Create(piece).ToJsonString() + "}}}}");
                        }
                        await Answer(writer, id, """{"stopReason":"end_turn"}""");
                        break;
                    case "session/cancel" when waiting is not null && Answered is null && !Asked:
                        Cancelled = true;
                        await Answer(writer, waiting, """{"stopReason":"cancelled"}""");
                        waiting = null;
                        break;
                    case null when message["id"]?.ToJsonString() == "\"ask_1\"":
                        JsonNode? outcome = message["result"]?["outcome"];
                        Answered = outcome?["outcome"]?.GetValue<string>() == "cancelled" ? "cancelled" : outcome?["optionId"]?.GetValue<string>();
                        if (waiting is not null)
                        {
                            await Update(writer, """{"toolCallId":"call_2","sessionUpdate":"tool_call_update","status":"completed"}""");
                            await Say(writer, "Wrote notes.md.");
                            await Answer(writer, waiting, """{"stopReason":"end_turn"}""");
                            waiting = null;
                        }
                        break;
                    case string when message["id"] is not null:
                        await writer.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32601,"message":"not played"}}""");
                        break;
                }
            }
        }

        /// <summary>Whether it has a question open, whose answer ends the turn rather than session/cancel.</summary>
        private bool Asked => Prompts.LastOrDefault() == "write";

        private static Task Say(StreamWriter writer, string text) =>
            Update(writer, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":""" + JsonValue.Create(text).ToJsonString() + "}}");

        private static Task Update(StreamWriter writer, string update) =>
            writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":""" + update + "}}");

        private static Task Answer(StreamWriter writer, string id, string result) =>
            writer.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{{{result}}}}""");
    }
}
