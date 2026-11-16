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
/// Connect starting its agent in the open folder, a stand-in in memory answering as the Claude adapter does.</summary>
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
        Assert.False(window.Chat.OffersConnect);
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

    private static Button ConnectButton(MainWindow window) =>
        window.Chat.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Connect to Claude Code"));

    /// <summary>An agent in memory that answers initialize and session/new as the Claude adapter answered them in a real
    /// run, and refuses anything else.</summary>
    private sealed class StandInAgent
    {
        private readonly Pipe toSession = new();
        private readonly Pipe fromSession = new();

        public StandInAgent()
        {
            Agent = new ChatAgent(AcpSession.Over(toSession.Reader.AsStream(), fromSession.Writer.AsStream()), () => toSession.Writer.Complete());
            // Unheld: it answers for as long as the session lives, which no wait for the IDE to be quiet may wait out.
            Passenger.Core.UnawaitedWork.Observe(Passenger.Core.BackgroundWork.Unheld(AnswerAsync));
        }

        public ChatAgent Agent { get; }

        /// <summary>The folder the session was opened in.</summary>
        public string? SessionFolder { get; private set; }

        private async Task AnswerAsync()
        {
            UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
            using StreamReader reader = new(fromSession.Reader.AsStream(), utf8);
            await using StreamWriter writer = new(toSession.Writer.AsStream(), utf8) { NewLine = "\n", AutoFlush = true };
            while (await reader.ReadLineAsync() is string line)
            {
                JsonNode request = JsonNode.Parse(line)!;
                string id = request["id"]?.ToJsonString() ?? "null";
                string answer = request["method"]?.GetValue<string>() switch
                {
                    "initialize" => """{"protocolVersion":1,"agentInfo":{"name":"@agentclientprotocol/claude-agent-acp","title":"Claude Agent","version":"0.107.3"},"authMethods":[]}""",
                    "session/new" => """{"sessionId":"sess_1","modes":{"currentModeId":"auto","availableModes":[{"id":"default","name":"Manual"}]}}""",
                    _ => "",
                };
                if (request["method"]?.GetValue<string>() == "session/new")
                {
                    SessionFolder = request["params"]?["cwd"]?.GetValue<string>();
                }
                await writer.WriteLineAsync(answer.Length > 0
                    ? $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{{{answer}}}}"""
                    : $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32601,"message":"not played"}}""");
            }
        }
    }
}
