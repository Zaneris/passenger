using Passenger.App.Chat;

namespace Passenger.App;

/// <summary>The window's Chat: its tool window, where a coding agent's conversation shows.</summary>
public partial class MainWindow
{
    /// <summary>The Chat tool window, built as it is first shown or asked for.</summary>
    public ChatView Chat => (ChatView)Dock[ToolWindowIds.Chat].Content;

    private void AddChat()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Chat, "Chat", IconKeys.Chat, () => new ChatView(), Docking.ToolAnchor.RightTop)
        {
            Action = AppActions.ShowChat,
            Menus = [new MenuPlace("Tools", 0, 10)],
        });
    }
}
