namespace Passenger.App.ToolWindows;

/// <summary>How a tool window shares out its room, from widths alone: which parts of its bar stand in a row
/// (<see cref="ToolFlow"/>), how wide each is there, and how its two panes stand (<see cref="PaneSplit"/>).</summary>
public static class ToolLayout
{
    /// <summary>The width under which two panes side by side leave a row's words too little.</summary>
    public const double Narrow = 480;

    /// <summary>The width a part that fills needs to stay in a row with others, and the most it takes beside them.</summary>
    public sealed record Fills(double Least, double Most = double.PositiveInfinity);

    /// <summary>A part's own width, or the least it fills from, and how it fills.</summary>
    public readonly record struct Piece(double Width, Fills? Fills = null);

    /// <summary>Whether two panes stand one over the other: a short window is left side by side, however narrow, as one over
    /// the other would leave neither any height.</summary>
    public static bool Stacks(double width, double height) => width < Narrow && height > width;

    /// <summary>Where each row starts among the pieces: a row holds what fits, and at least one.</summary>
    public static IReadOnlyList<int> RowStarts(IReadOnlyList<Piece> pieces, double width, double spacing)
    {
        List<int> starts = [];
        double used = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            bool fits = starts.Count > 0 && used + spacing + pieces[i].Width <= width;
            used = fits ? used + spacing + pieces[i].Width : pieces[i].Width;
            if (!fits)
            {
                starts.Add(i);
            }
        }
        return starts;
    }

    /// <summary>Each piece's width in a row of a width: what they leave goes first to those with a most, as far as it, then
    /// to the first with none. One that fills alone takes the row, and none is wider than the row.</summary>
    public static double[] Spread(IReadOnlyList<Piece> row, double width, double spacing)
    {
        double[] widths = [.. row.Select(p => Math.Min(p.Width, width))];
        if (row is [{ Fills: not null }])
        {
            widths[0] = width;
            return widths;
        }
        double left = width - widths.Sum() - (spacing * (row.Count - 1));
        for (int i = 0; i < row.Count && left > 0; i++)
        {
            if (row[i].Fills is { } fills && !double.IsInfinity(fills.Most))
            {
                double taken = Math.Min(left, Math.Max(0, fills.Most - widths[i]));
                widths[i] += taken;
                left -= taken;
            }
        }
        for (int i = 0; i < row.Count && left > 0; i++)
        {
            if (row[i].Fills is { Most: double.PositiveInfinity })
            {
                widths[i] += left;
                left = 0;
            }
        }
        return widths;
    }
}
