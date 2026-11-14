using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.ToolWindows;

/// <summary>The parts of a tool window's top bar (<see cref="ToolBars.Top"/>): one row of them, and in a window too narrow
/// for that as many rows as they need, the bar growing with them, so none is drawn over another or cut off. A part given
/// <see cref="FillsProperty"/> asks the least it can work in and takes what its row leaves, the whole row where it stands
/// alone.</summary>
public sealed class ToolFlow : Panel
{
    /// <summary>How a part fills its row, if it does.</summary>
    public static readonly AttachedProperty<ToolLayout.Fills?> FillsProperty = AvaloniaProperty.RegisterAttached<ToolFlow, Control, ToolLayout.Fills?>("Fills");

    private readonly List<(Control[] Parts, ToolLayout.Piece[] Pieces)> rows = [];

    // Words coming to a part may move it to a row of its own, which its measure, held to its row's width, never tells.
    static ToolFlow() => AffectsParentMeasure<ToolFlow>(FillsProperty, TextBlock.TextProperty);

    /// <summary>The space between parts, and between rows.</summary>
    public double Spacing { get; init; } = 2;

    public static void SetFills(Control part, ToolLayout.Fills fills) => part.SetValue(FillsProperty, fills);

    /// <summary>A part that only takes what its row leaves, so those after it stand at the row's end.</summary>
    public static Control Spring()
    {
        Border spring = new();
        SetFills(spring, new ToolLayout.Fills(0));
        return spring;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Control[] parts = [.. Children.Where(c => c.IsVisible)];
        ToolLayout.Piece[] pieces = new ToolLayout.Piece[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            ToolLayout.Fills? fills = parts[i].GetValue(FillsProperty);
            pieces[i] = new ToolLayout.Piece(fills is null ? parts[i].DesiredSize.Width : LeastOf(parts[i], fills), fills);
        }
        double room = double.IsInfinity(availableSize.Width) ? pieces.Sum(p => p.Width + Spacing) : availableSize.Width;
        IReadOnlyList<int> starts = ToolLayout.RowStarts(pieces, room, Spacing);
        rows.Clear();
        double height = Math.Max(0, Spacing * (starts.Count - 1));
        for (int r = 0; r < starts.Count; r++)
        {
            Range row = starts[r]..(r + 1 < starts.Count ? starts[r + 1] : parts.Length);
            rows.Add((parts[row], pieces[row]));
            double[] widths = ToolLayout.Spread(pieces[row], room, Spacing);
            double tallest = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                Control part = parts[row][i];
                part.Measure(new Size(widths[i], availableSize.Height));
                tallest = Math.Max(tallest, part.DesiredSize.Height);
            }
            height += tallest;
        }
        return new Size(room, height);
    }

    /// <summary>The least a part that fills asks, which is nothing for words not there to show.</summary>
    private static double LeastOf(Control part, ToolLayout.Fills fills) => part is TextBlock { Text: null or "" } ? 0 : fills.Least;

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach ((Control[] parts, ToolLayout.Piece[] pieces) in rows)
        {
            double[] widths = ToolLayout.Spread(pieces, finalSize.Width, Spacing);
            double tallest = parts.Max(p => p.DesiredSize.Height);
            double x = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i].Arrange(new Rect(x, y, widths[i], tallest));
                x += widths[i] + Spacing;
            }
            y += tallest + Spacing;
        }
        return finalSize;
    }
}
