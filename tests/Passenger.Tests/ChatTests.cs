using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Chat;

namespace Passenger.Tests;

/// <summary>The Chat window: shown and hidden from its stripe button and shown by its action, as each tool window is.</summary>
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
        Assert.Contains(window.Chat.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "No agent connected");
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
}
