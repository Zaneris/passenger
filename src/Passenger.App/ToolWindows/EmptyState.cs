using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.ToolWindows;

/// <summary>What a tool window's list says while it has nothing to show, over where its rows would be: its words, muted
/// and centred as every such text is (<c>emptyText</c>), and under them, while one is offered, a link to the action that
/// would give it something to show (Create Git Repository where the folder is in no repository), which
/// <see cref="ActionRequested"/> asks the window for.</summary>
public sealed class EmptyState : StackPanel
{
    private readonly TextBlock words = new() { Classes = { "emptyText" } };
    private readonly Button link = new() { Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    private string? offered;

    public EmptyState()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(words);
        Children.Add(link);
        link.Click += (_, _) =>
        {
            if (offered is { } action)
            {
                ActionRequested?.Invoke(this, action);
            }
        };
    }

    /// <summary>What it says.</summary>
    public string? Text
    {
        get => words.Text;
        set => words.Text = value;
    }

    /// <summary>The action its link asks for, by its id and title, or none with null.</summary>
    public void Offer(string? action, string? title)
    {
        offered = action;
        link.Content = title;
        link.IsVisible = action is not null;
        if (action is null)
        {
            words.ClearValue(MarginProperty);
        }
        else
        {
            // The link sits under the words as their next line, not a paragraph apart.
            words.Margin = new Thickness(16, 16, 16, 4);
        }
    }

    /// <summary>The link pressed, naming the action it offers.</summary>
    public event EventHandler<string>? ActionRequested;
}
