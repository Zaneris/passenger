using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>The IDE's internal errors: those caught since the last Clear All, each once with how many times it came,
/// the chosen one's details under them, and the log a button away.</summary>
public sealed class ErrorsPopup : UserControl
{
    private readonly ListBox list;
    private readonly TextBlock occurred;
    private readonly TextBox details;

    /// <param name="unlisted">The errors that came once the list was full, which only the log has.</param>
    public ErrorsPopup(IReadOnlyList<IdeError> errors, int unlisted)
    {
        Width = PopupFrame.Wide;
        list = new ListBox
        {
            Classes = { "tree" },
            Height = 150,
            ItemsSource = errors,
        };
        ReusedRows.Use(list, Row);
        occurred = new TextBlock { Classes = { "muted" } };
        details = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            Height = 220,
            FontFamily = new FontFamily("avares://Passenger.Editor/Fonts#JetBrains Mono"),
            FontSize = 11.5,
        };
        list.SelectionChanged += (_, _) =>
        {
            IdeError? chosen = list.SelectedItem as IdeError;
            details.Text = chosen?.Details ?? string.Empty;
            occurred.Text = chosen is null ? string.Empty : Occurred(chosen);
        };
        list.SelectedIndex = errors.Count - 1;

        Button clear = new() { Content = "Clear All" };
        clear.Click += (_, _) =>
        {
            IdeErrors.Clear();
            Done?.Invoke(this, EventArgs.Empty);
        };
        Button log = new() { Content = $"Show Log in {FileManager.Name}" };
        log.Click += (_, _) => LogAsked?.Invoke(this, EventArgs.Empty);
        Button close = new() { Content = "Close", Classes = { "primary" }, IsDefault = true };
        close.Click += (_, _) => Done?.Invoke(this, EventArgs.Empty);
        Content = new Border
        {
            Classes = { "popup" },
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Classes = { "popupTitle" }, Text = "IDE Internal Errors" },
                    new TextBlock
                    {
                        Classes = { "muted" },
                        TextWrapping = TextWrapping.Wrap,
                        Text = unlisted == 0
                            ? "Passenger caught these and kept running. Each is in the log with its details."
                            : $"Passenger caught these and kept running. Each is in the log with its details, as are {unlisted} more that came after the first {IdeErrors.Limit}.",
                    },
                    list,
                    occurred,
                    details,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { log, clear, close },
                    },
                },
            },
        };
    }

    /// <summary>Raised when the popup is done with, by Close or Clear All.</summary>
    public event EventHandler? Done;

    /// <summary>An error's row: when it first came, its kind and first line, and how many times when more than once.</summary>
    /// <summary>An error's row: when it came, its kind and message on one line, and how often it came at the right.</summary>
    private static Control Row()
    {
        TextBlock count = new()
        {
            Classes = { "muted" },
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };
        TextBlock what = new()
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new DockPanel { Children = { count, what } }.Showing<IdeError>(error =>
        {
            count.Text = error.Count == 1 ? string.Empty : $"{error.Count} times";
            what.Text = $"{error.At:HH:mm:ss}  {error.Kind}: {error.Message.ReplaceLineEndings(" ")}";
        });
    }

    /// <summary>The line over an error's details: how often it came, and when.</summary>
    private static string Occurred(IdeError error) => error.Count == 1
        ? $"Occurred once, at {error.At:HH:mm:ss}"
        : $"Occurred {error.Count} times, from {error.At:HH:mm:ss} to {error.Last:HH:mm:ss}";

    /// <summary>Show Log in Finder (Explorer on Windows) clicked.</summary>
    public event EventHandler? LogAsked;
}
