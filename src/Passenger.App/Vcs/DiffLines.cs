using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Vcs;

/// <summary>A change's lines on the side a line is read from and on the side it is mapped to.</summary>
public readonly record struct LinesAcross(int FromStart, int FromCount, int ToStart, int ToCount);

/// <summary>What a diff and a merge share around their panes: where a line of one pane stands on another, the panes kept
/// level as one scrolls, the keys that step between differences, and the bar and titles over the panes.</summary>
public static class DiffLines
{
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

    /// <summary>Next Difference (1) or Previous Difference (-1) for a key, 0 for any other: F7 and Shift+F7, and F8 and
    /// Shift+F8 too off the Mac, as Rider's Visual Studio keymap binds them.</summary>
    public static int Step(KeyEventArgs e) =>
        (e.Key == Key.F7 || (!OperatingSystem.IsMacOS() && e.Key == Key.F8)) && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift
            ? (e.KeyModifiers == KeyModifiers.Shift ? -1 : 1)
            : 0;

    /// <summary>The bar over the panes: <paramref name="tools"/> first, then Previous and Next Difference, the file's
    /// name and <paramref name="status"/>, with <paramref name="end"/> at its right.</summary>
    public static Border Bar(string name, Action<int> step, TextBlock status, IEnumerable<Control> tools, Control? end = null)
    {
        StackPanel start = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        start.Children.AddRange(tools);
        start.Children.Add(ToolButtons.Of(IconKeys.ArrowUp, $"Previous Difference ({Keys("Shift+F7")})", () => step(-1)));
        start.Children.Add(ToolButtons.Of(IconKeys.ArrowDown, $"Next Difference ({Keys("F7")})", () => step(1)));
        start.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(12, 0);
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Classes.Add("muted");
        start.Children.Add(status);
        DockPanel row = new();
        if (end is not null)
        {
            DockPanel.SetDock(end, Dock.Right);
            end.Margin = new Thickness(0, 0, 6, 0);
            end.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(end);
        }
        row.Children.Add(start);
        // A tool window's ground over the editors', told apart by shade.
        Border bar = new() { [DockPanel.DockProperty] = Dock.Top, Height = 34, Child = row };
        bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable(TokenKeys.Panel));
        return bar;

        static string Keys(string shown) => OperatingSystem.IsMacOS() ? shown : $"{shown}, {shown.Replace("F7", "F8", StringComparison.Ordinal)}";
    }

    /// <summary>What a pane shows, over it in its column, ending in an ellipsis where it has no room.</summary>
    public static Control Title(string text, int column)
    {
        TextBlock label = new() { Text = text, FontSize = 12, Margin = new Thickness(12, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        label.Classes.Add("muted");
        Grid.SetColumn(label, column);
        return label;
    }
}
