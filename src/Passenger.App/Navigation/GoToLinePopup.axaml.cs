using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>Go to Line: "LINE" or "LINE:COLUMN", both from one as the status bar shows them, the
/// current position as its starting text.</summary>
public partial class GoToLinePopup : UserControl, IAnswers<TextPosition?>
{
    private readonly int lastLine;

    public GoToLinePopup(TextPosition current, int lineCount)
    {
        InitializeComponent();
        this.lastLine = lineCount;
        Query.Text = $"{current.Line + 1}:{current.Column + 1}";
        Query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() =>
        {
            Query.Focus();
            Query.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    public GoToLinePopup()
        : this(new TextPosition(0, 0), 1)
    {
    }

    /// <summary>Where to go (from zero), or null when dismissed.</summary>
    public event EventHandler<TextPosition?>? Done;

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Done?.Invoke(this, null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (Parse(Query.Text ?? string.Empty) is { } position)
            {
                Done?.Invoke(this, position);
            }
            else
            {
                Problem.Text = $"A line from 1 to {lastLine}, and a column after a colon if wanted";
                Problem.IsVisible = true;
            }
            e.Handled = true;
        }
    }

    private TextPosition? Parse(string text)
    {
        string[] parts = text.Trim().Split(':', ',');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out int line) || line < 1 || line > lastLine)
        {
            return null;
        }
        int column = 1;
        if (parts.Length == 2 && parts[1].Length > 0 && (!int.TryParse(parts[1], out column) || column < 1))
        {
            return null;
        }
        return new TextPosition(line - 1, column - 1);
    }
}
