using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The status bar's path in a line, its children each part's mark and button in turn (a hidden part taking no
/// room): every part whole where the room holds them all; else the longest shortened first, to an ellipsis after its first
/// letters but never narrower than <see cref="Shortest"/>; and where even that leaves too little room, the outermost
/// parts give way, so the file and what the caret is in stay. It is measured whole, never against the room, so a part
/// come since is measured as drawn and the line it is in lays it out anew (<see cref="StatusLine"/>): the room is
/// shared out as it is arranged, where each part's words end in an ellipsis (Shell.axaml's crumb).</summary>
public sealed class PathCrumbs : Panel
{
    /// <summary>The narrowest a shortened part draws: a few letters and the ellipsis.</summary>
    private const double Shortest = 40;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (child.IsVisible)
            {
                width += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The parts shown, each its mark's and button's widths; the first part's mark is never drawn.
        List<(Control? Mark, Control Part)> parts = [];
        for (int i = 1; i < Children.Count; i += 2)
        {
            if (Children[i].IsVisible)
            {
                parts.Add((parts.Count == 0 ? null : Children[i - 1], Children[i]));
            }
        }
        double[] widths = [.. parts.Select(p => p.Part.DesiredSize.Width)];
        double Marks(int from) => parts.Skip(from + 1).Sum(p => p.Mark?.DesiredSize.Width ?? 0);
        // The outermost part kept: the first from which the rest fit at their shortest.
        int first = 0;
        while (first < parts.Count - 1 && Marks(first) + widths.Skip(first).Sum(w => Math.Min(w, Shortest)) > finalSize.Width)
        {
            first++;
        }
        double room = Math.Max(0, finalSize.Width - Marks(first));
        double[] kept = widths[first..];
        double cap = Cap(kept, room);
        double x = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            (Control? mark, Control part) = parts[i];
            if (i < first)
            {
                Away(mark);
                Away(part);
                continue;
            }
            if (i == first)
            {
                Away(mark);
            }
            else if (mark is not null)
            {
                mark.Arrange(new Rect(x, 0, mark.DesiredSize.Width, finalSize.Height));
                x += mark.DesiredSize.Width;
            }
            double width = parts.Count - first == 1 ? Math.Min(widths[i], room) : Math.Min(widths[i], cap);
            part.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;

        // Laid out whole left of the line, where the path's clip hides it.
        void Away(Control? child) => child?.Arrange(new Rect(-child.DesiredSize.Width, 0, child.DesiredSize.Width, finalSize.Height));
    }

    /// <summary>The widest any of these parts may be for them all to fit the room, the widest giving way first: none where
    /// they fit whole.</summary>
    private static double Cap(double[] widths, double room)
    {
        if (widths.Sum() <= room)
        {
            return double.PositiveInfinity;
        }
        double[] ordered = [.. widths.Order()];
        double below = 0;
        for (int i = 0; i < ordered.Length; i++)
        {
            // Those from i on all at one width share what those below leave.
            double share = (room - below) / (ordered.Length - i);
            if (share <= ordered[i])
            {
                return Math.Max(share, Shortest);
            }
            below += ordered[i];
        }
        return Shortest;
    }
}
