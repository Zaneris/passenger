using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Chat;

/// <summary>The Chat window: a conversation with a coding agent the person runs on their own account, over the Agent
/// Client Protocol. Passenger holds no key and no login for it. Until an agent is connected it says what it is doing and
/// offers Connect.</summary>
public sealed class ChatView : DockPanel
{
    private readonly TextBlock said = new()
    {
        Classes = { StyleClasses.EmptyText },
        Text = "No agent connected",
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private readonly Button connect = new()
    {
        Content = "Connect to Claude Code",
        Classes = { StyleClasses.Primary },
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public ChatView()
    {
        connect.Click += (_, _) => ConnectRequested?.Invoke(this, EventArgs.Empty);
        Children.Add(new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(16),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { said, connect },
        });
    }

    /// <summary>Connect was pressed.</summary>
    public event EventHandler? ConnectRequested;

    /// <summary>What it says now: "No agent connected", "Starting Claude Code…", "Connected to Claude Agent 0.107.3".</summary>
    public string Said => said.Text ?? string.Empty;

    /// <summary>Whether Connect is offered: while no agent is connected or starting.</summary>
    public bool OffersConnect => connect.IsVisible;

    /// <summary>Says <paramref name="text"/>, as an error where it is one, with Connect offered while no agent is.</summary>
    public void Say(string text, bool error = false, bool offerConnect = false)
    {
        said.Text = text;
        said.Classes.Set(StyleClasses.Error, error);
        connect.IsVisible = offerConnect;
    }
}
