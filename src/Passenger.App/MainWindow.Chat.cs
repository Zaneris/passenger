using System.ComponentModel;
using Avalonia.Threading;
using Passenger.Acp;
using Passenger.App.Chat;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The window's Chat: its tool window, and the coding agent it connects to in the open folder, ended with the
/// window. The agent's updates come on its connection's thread and land on the window's, each held as background work.</summary>
public partial class MainWindow
{
    private ChatAgent? chatAgent;
    private CancellationTokenSource? chatTurn;

    /// <summary>The Chat tool window, built as it is first shown or asked for.</summary>
    public ChatView Chat => (ChatView)Dock[ToolWindowIds.Chat].Content;

    /// <summary>Starts the Chat window's agent in a folder: the Claude adapter, or a test's stand-in.</summary>
    public Func<string, Task<ChatAgent>> StartChatAgent { get; set; } = ChatAgent.StartClaudeAsync;

    private void AddChat()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Chat, "Chat", IconKeys.Chat, () =>
        {
            ChatView view = new() { Browse = Browse };
            view.Say("No agent connected", offerConnect: true);
            view.ConnectRequested += (_, _) =>
            {
                Task connecting = ConnectChatAsync(view);
                BackgroundWork.Track(connecting);
                IdeErrors.Observe(connecting);
            };
            // A turn is not held: it lasts as long as the agent works, which no wait for the IDE to be quiet may wait out.
            view.SendRequested += (_, text) => IdeErrors.Observe(SendChatAsync(view, text));
            view.StopRequested += (_, _) =>
            {
                view.CancelQuestions();
                chatTurn?.Cancel();
            };
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Action = AppActions.ShowChat,
            Menus = [new MenuPlace("Tools", 0, 10)],
        });
        Closed += (_, _) => EndChatAgent();
    }

    /// <summary>Starts the agent in the open folder and opens its session in Manual mode, so it asks before it changes
    /// anything, saying each step and, failing, why.</summary>
    private async Task ConnectChatAsync(ChatView view)
    {
        if (Folder is not { } folder)
        {
            view.Say("Open a folder to chat with an agent in it", offerConnect: true);
            return;
        }
        EndChatAgent();
        view.Say("Starting Claude Code…");
        try
        {
            ChatAgent agent = await StartChatAgent(folder);
            chatAgent = agent;
            Follow(agent, view);
            AgentInfo info = await agent.Session.InitializeAsync();
            await agent.Session.NewSessionAsync(folder);
            if (agent.Session.Modes.Any(mode => mode.Id == ChatAgent.ManualMode))
            {
                await agent.Session.SetModeAsync(ChatAgent.ManualMode);
            }
            string name = info.Title ?? info.Name;
            view.Connected(name, $"{name}{(info.Version is { } version ? " " + version : "")}");
        }
        catch (Exception error) when (error is AcpError or Win32Exception or IOException or InvalidOperationException)
        {
            EndChatAgent();
            view.Say($"Could not start the agent: {error.Message}", error: true, offerConnect: true);
        }
    }

    /// <summary>Sends the person's message and shows the reply as it streams, until the agent ends its turn.</summary>
    private async Task SendChatAsync(ChatView view, string text)
    {
        if (chatAgent is not { } agent)
        {
            return;
        }
        using CancellationTokenSource turn = new();
        chatTurn = turn;
        view.Asked(text);
        try
        {
            TurnEnded ended = await agent.Session.PromptAsync(text, turn.Token);
            // The turn's answer comes after its updates, but its continuation outruns their posts: they land first.
            await Dispatcher.UIThread.InvokeAsync(static () => { });
            view.Answered(ended.StopReason switch
            {
                "end_turn" => null,
                "cancelled" => "Stopped",
                "max_tokens" => "Stopped: the reply reached its length limit",
                "refusal" => "The agent declined to answer",
                _ => $"Stopped: {ended.StopReason}",
            });
        }
        catch (AcpError error)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { });
            view.Answered($"The agent failed: {error.Message}", error: true);
        }
        finally
        {
            chatTurn = null;
        }
    }

    /// <summary>Shows what the agent says and does on the window's thread, and puts each question it asks before it acts
    /// to the person as a card, its answer the one clicked: an edit with its diff, the file read for its text before
    /// where the agent sent none.</summary>
    private void Follow(ChatAgent agent, ChatView view)
    {
        agent.Session.TextReceived += chunk => OnWindowThread(() => view.Streamed(chunk.Text));
        agent.Session.ToolCallChanged += call => OnWindowThread(() => view.Tool(call));
        agent.Session.AskPermission = async (request, _) =>
        {
            ChatChange[] changes = [.. request.ToolCall.Diffs.Select(diff => ChatChange.Of(diff.Path, diff.OldText, diff.NewText))];
            TaskCompletionSource<Task<string?>> shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            OnWindowThread(() => shown.SetResult(view.Ask(request, changes)));
            return await await shown.Task;
        };
        IdeErrors.Observe(agent.Session.Connection.Completion.ContinueWith(_ => OnWindowThread(() =>
        {
            if (chatAgent == agent)
            {
                chatAgent = null;
                view.Say("The agent ended", error: true, offerConnect: true);
            }
        }), TaskScheduler.Default));
    }

    /// <summary>Runs on the window's thread, held as background work until it has, so a test or a screenshot waits for it.</summary>
    private static void OnWindowThread(Action show)
    {
        IDisposable held = BackgroundWork.Scope();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                show();
            }
            finally
            {
                held.Dispose();
            }
        });
    }

    private void EndChatAgent()
    {
        chatTurn?.Cancel();
        if (chatAgent is { } agent)
        {
            chatAgent = null;
            IdeErrors.Observe(agent.DisposeAsync().AsTask());
        }
    }
}
