using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Editor;

namespace Passenger.App.Vcs;

/// <summary>A change's lines on the side a line is read from and on the side it is mapped to.</summary>
public readonly record struct LinesAcross(int FromStart, int FromCount, int ToStart, int ToCount);

/// <summary>What a diff and a merge share around their panes: where a line of one pane stands on another, the panes kept
/// level as one scrolls, the keys that step between differences, the bar and titles over the panes, and the strips between
/// them that a drag moves.</summary>
public static class DiffLines
{
    /// <summary>The least width a drag leaves a pane: room for its line numbers and a few words.</summary>
    public const double LeastPane = 120;

    /// <summary>Puts what stands between two panes (a strip of changes) in its column of <paramref name="panes"/>, under the
    /// titles' row, over a divider as wide as it: a drag on it anywhere that takes no press of its own moves the panes
    /// either side, each kept to <see cref="LeastPane"/>, its own width as it was.</summary>
    public static void Divide(Grid panes, Control between, int column)
    {
        HairlineDivider divider = HairlineDivider.Under(GridResizeDirection.Columns);
        Grid.SetColumn(divider, column);
        Grid.SetRowSpan(divider, 2);
        Grid.SetColumn(between, column);
        Grid.SetRow(between, 1);
        panes.Children.Add(divider);
        panes.Children.Add(between);
        panes.ColumnDefinitions[column - 1].MinWidth = LeastPane;
        panes.ColumnDefinitions[column + 1].MinWidth = LeastPane;
    }

    /// <summary>Where a line (fractional, from zero) stands on the other side, through the changes above it, given in
    /// order as <paramref name="count"/> spans.</summary>
    public static double Map(double line, int count, Func<int, LinesAcross> span)
    {
        double delta = 0;
        for (int i = 0; i < count; i++)
        {
            LinesAcross s = span(i);
            if (line < s.FromStart)
            {
                break;
            }
            if (line < s.FromStart + s.FromCount)
            {
                return s.ToStart + ((line - s.FromStart) * s.ToCount / s.FromCount);
            }
            delta = s.ToStart + s.ToCount - (s.FromStart + s.FromCount);
        }
        return line + delta;
    }

    /// <summary>The line at the top of a pane, fractional, from zero.</summary>
    public static double Top(CodeEditor pane) => pane.ScrollOffset.Y / pane.LineHeight;

    /// <summary>Scrolls a pane so that a line (fractional) stands at its top, across as far as another pane is.</summary>
    public static void ScrollTo(CodeEditor pane, double line, CodeEditor across) =>
        pane.ScrollTo(new Vector(across.ScrollOffset.X, line * pane.LineHeight));

    /// <summary>Next Difference's keys: F7, and F8 too off the Mac, as Rider's Visual Studio keymap binds them; Previous
    /// Difference's are each with Shift held.</summary>
    private static readonly Key[] StepKeys = OperatingSystem.IsMacOS() ? [Key.F7] : [Key.F7, Key.F8];

    /// <summary>Next Difference (1) or Previous Difference (-1) for a key, 0 for any other (<see cref="StepKeys"/>).</summary>
    public static int Step(KeyEventArgs e) =>
        StepKeys.Contains(e.Key) && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift
            ? (e.KeyModifiers == KeyModifiers.Shift ? -1 : 1)
            : 0;

    /// <summary>The keys of a step (1 Next Difference, -1 Previous Difference) in words, as its button's tip names them.</summary>
    private static string StepKeysShown(int step) =>
        KeyWords.Either(StepKeys.Select(key => new Keystroke(key, step < 0 ? KeyModifiers.Shift : KeyModifiers.None)));

    /// <summary>The bar over the panes: <paramref name="tools"/> first, then Previous and Next Difference, the file's
    /// name and <paramref name="status"/>, with <paramref name="end"/> at its right.</summary>
    public static Border Bar(string name, Action<int> step, TextBlock status, IEnumerable<Control> tools, Control? end = null)
    {
        // In a narrow diff the count, then the file's name, give way, ending in an ellipsis, before the arrows and Settings.
        ShrinkingRow start = new()
        {
            Spacing = Gaps.Hair,
            Margin = new Thickness(Gaps.Small, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        start.Children.AddRange(tools);
        start.Children.Add(ToolButtons.Of(IconKeys.ArrowUp, ActionTips.Named("Previous Difference", StepKeysShown(-1)), () => step(-1)));
        start.Children.Add(ToolButtons.Of(IconKeys.ArrowDown, ActionTips.Named("Next Difference", StepKeysShown(1)), () => step(1)));
        start.Children.Add(new TextBlock { Classes = { StyleClasses.Strong }, Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0) });
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(Gaps.Wide, 0);
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Classes.Add(StyleClasses.Muted);
        start.Children.Add(status);
        DockPanel row = new();
        if (end is not null)
        {
            DockPanel.SetDock(end, Dock.Right);
            end.Margin = new Thickness(0, 0, Gaps.Small, 0);
            end.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(end);
        }
        row.Children.Add(start);
        return ToolBars.OverEditor(row);
    }

    /// <summary>What a pane shows, over it in its column, ending in an ellipsis where it has no room.</summary>
    public static Control Title(string text, int column)
    {
        TextBlock label = new() { Classes = { StyleClasses.Caption }, Text = text, Margin = new Thickness(Gaps.Wide, Gaps.Tight), TextTrimming = TextTrimming.CharacterEllipsis };
        label.Classes.Add(StyleClasses.Muted);
        Grid.SetColumn(label, column);
        return label;
    }
}
