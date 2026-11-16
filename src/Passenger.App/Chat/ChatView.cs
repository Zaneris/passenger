using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Chat;

/// <summary>The Chat window: a conversation with a coding agent the person runs on their own account, over the Agent
/// Client Protocol. Passenger holds no key and no login for it. Until an agent is connected it says so.</summary>
public sealed class ChatView : DockPanel
{
    public ChatView()
    {
        Children.Add(new TextBlock
        {
            Classes = { StyleClasses.EmptyText },
            Text = "No agent connected",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }
}
