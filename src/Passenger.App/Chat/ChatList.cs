using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Chat;

/// <summary>A row of a <see cref="ChatList"/>: its title, what is said under it (a date), whether it is the one shown now,
/// what a click on it does, and what its delete button does and says.</summary>
public sealed record ChatListRow(string Title, string? Detail, bool Current, Action Open, Action Delete, string DeleteTip);

/// <summary>The Chat window's lists over the window, its plans and its earlier chats, in the popups' frame: a title, then
/// a row each, its title whole over two lines at most and its date under it, the one shown now marked, and at its end a
/// delete button always in sight. A click on a row opens it and closes the list; a delete takes its row away and leaves
/// the list open for the next.</summary>
public sealed class ChatList : StackPanel
{
    private readonly StackPanel rows = new() { Spacing = 2 };
    private readonly TextBlock none;

    public ChatList(string title, string empty, IReadOnlyList<ChatListRow> listed, Action close)
    {
        Width = PopupFrame.Medium;
        Spacing = 8;
        Children.Add(new TextBlock { Classes = { StyleClasses.PopupTitle }, Text = title });
        none = new TextBlock { Classes = { StyleClasses.Muted }, Text = empty, TextWrapping = TextWrapping.Wrap, IsVisible = listed.Count == 0 };
        foreach (ChatListRow row in listed)
        {
            rows.Children.Add(Row(row, close));
        }
        Children.Add(none);
        Children.Add(new ScrollViewer { MaxHeight = 420, Content = rows });
    }

    /// <summary>Each row's open and delete buttons, in its order.</summary>
    public IReadOnlyList<(Button Open, Button Delete)> Rows =>
        [.. rows.Children.OfType<DockPanel>().Select(row => (row.Children.OfType<Button>().Last(), row.Children.OfType<Button>().First()))];

    private DockPanel Row(ChatListRow row, Action close)
    {
        DockPanel line = new();
        Button delete = ToolButtons.Of(IconKeys.Close, row.DeleteTip, () =>
        {
            rows.Children.Remove(line);
            none.IsVisible = rows.Children.Count == 0;
            row.Delete();
        });
        delete.Margin = new Thickness(8, 0, 0, 0);
        delete.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(delete, Dock.Right);
        StackPanel words = new()
        {
            Children =
            {
                new TextBlock
                {
                    Text = row.Title,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = row.Current ? FontWeight.SemiBold : FontWeight.Normal,
                },
            },
        };
        if (row.Detail is { Length: > 0 } detail || row.Current)
        {
            words.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = row.Current ? $"Shown now{(row.Detail is { Length: > 0 } when ? " · " + when : string.Empty)}" : row.Detail });
        }
        Button open = new()
        {
            Classes = { StyleClasses.TextLink },
            Content = words,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4),
        };
        ToolTip.SetTip(open, row.Title);
        open.Click += (_, _) =>
        {
            close();
            row.Open();
        };
        line.Children.Add(delete);
        line.Children.Add(open);
        return line;
    }
}
