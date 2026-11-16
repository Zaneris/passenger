using System.ComponentModel;
using Passenger.Acp;
using Passenger.App.Chat;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The window's Chat: its tool window, and the coding agent it connects to in the open folder, ended with the
/// window.</summary>
public partial class MainWindow
{
    private ChatAgent? chatAgent;

    /// <summary>The Chat tool window, built as it is first shown or asked for.</summary>
    public ChatView Chat => (ChatView)Dock[ToolWindowIds.Chat].Content;

    /// <summary>Starts the Chat window's agent in a folder: the Claude adapter, or a test's stand-in.</summary>
    public Func<string, Task<ChatAgent>> StartChatAgent { get; set; } = ChatAgent.StartClaudeAsync;

    private void AddChat()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Chat, "Chat", IconKeys.Chat, () =>
        {
            ChatView view = new();
            view.Say("No agent connected", offerConnect: true);
            view.ConnectRequested += (_, _) =>
            {
                Task connecting = ConnectChatAsync(view);
                BackgroundWork.Track(connecting);
                IdeErrors.Observe(connecting);
            };
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Action = AppActions.ShowChat,
            Menus = [new MenuPlace("Tools", 0, 10)],
        });
        Closed += (_, _) => EndChatAgent();
    }

    /// <summary>Starts the agent in the open folder and opens its session, saying each step and, failing, why.</summary>
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
            AgentInfo info = await agent.Session.InitializeAsync();
            await agent.Session.NewSessionAsync(folder);
            view.Say($"Connected to {info.Title ?? info.Name}{(info.Version is { } version ? " " + version : "")}");
        }
        catch (Exception error) when (error is AcpError or Win32Exception or IOException or InvalidOperationException)
        {
            EndChatAgent();
            view.Say($"Could not start the agent: {error.Message}", error: true, offerConnect: true);
        }
    }

    private void EndChatAgent()
    {
        if (chatAgent is { } agent)
        {
            chatAgent = null;
            IdeErrors.Observe(agent.DisposeAsync().AsTask());
        }
    }
}
