using Avalonia;
using Passenger.Core;

namespace Passenger.App.WinForms;

/// <summary>A box dragged in a form's design held to the lines Windows Forms' designers snap by: an edge to the same edge of a
/// control in the same holder, a text's baseline to another's, a margin to a neighbour's margin or to the holder's padding,
/// each within <see cref="Reach"/> of the frame's pixels; across and down apart, the nearest taken on each.</summary>
public static class DesignSnapping
{
    /// <summary>How near a line comes, in the frame's pixels, before the box goes to it.</summary>
    public const double Reach = 8;

    /// <summary>A line shown where two snapped: down the frame (an edge across) or across it, where it lies, and the stretch it
    /// is drawn along, over both boxes.</summary>
    public readonly record struct Guide(bool Down, double At, double From, double To);

    /// <summary>The sides of a box a gesture moves: all of them for a move, a handle's for a resize.</summary>
    public readonly record struct Sides(bool Left, bool Top, bool Right, bool Bottom)
    {
        public static Sides All => new(true, true, true, true);

        public bool Whole => Left && Top && Right && Bottom;
    }

    /// <summary>A box as the lines near it hold it, and the lines it met; it unchanged where none is near. The box's own lines
    /// are those of <paramref name="moved"/>, where it stood; the others those of the controls <paramref name="holder"/> holds
    /// and the holder's padding.</summary>
    public static (Rect Box, IReadOnlyList<Guide> Guides) Snapped(IReadOnlyList<DesignedBox> boxes, DesignedBox moved, DesignedBox? holder, Rect box, Sides sides)
    {
        List<(DesignSnap Line, DesignedBox Of, bool Holds)> others = [];
        foreach (DesignedBox other in boxes)
        {
            if (other.Field == moved.Field || other.Snaps is not { } lines)
            {
                continue;
            }
            bool holds = holder is not null && other.Field == holder.Field;
            if (holds || holder is not null && other.Parent == holder.Field && other.Width > 0)
            {
                others.AddRange(lines.Where(l => holds == IsPadding(l)).Select(l => (l, other, holds)));
            }
        }
        List<(DesignSnap Line, double From)> own = [.. (moved.Snaps ?? []).Where(l => !IsPadding(l) && Moves(l, sides)).Select(l => (l, Offset(l, moved)))];
        double across = Nearest(true);
        double down = Nearest(false);
        Rect held = Shifted(box, sides, across, down);
        List<Guide> guides = [];
        foreach ((DesignSnap line, double from) in own)
        {
            double at = Place(line, from, held);
            foreach ((DesignSnap other, DesignedBox of, bool holds) in others)
            {
                if (Math.Abs(other.At - at) < 0.5 && Meet(line, other, holds, held, of))
                {
                    bool upright = Upright(line);
                    guides.Add(upright
                        ? new Guide(true, at, Math.Min(held.Top, of.Y), Math.Max(held.Bottom, of.Y + of.Height))
                        : new Guide(false, at, Math.Min(held.Left, of.X), Math.Max(held.Right, of.X + of.Width)));
                }
            }
        }
        return (held, guides);

        double Nearest(bool upright)
        {
            double best = double.NaN;
            foreach ((DesignSnap line, double from) in own.Where(o => Upright(o.Line) == upright))
            {
                double at = Place(line, from, box);
                foreach ((DesignSnap other, DesignedBox of, bool holds) in others)
                {
                    double by = other.At - at;
                    if (Math.Abs(by) <= Reach && (double.IsNaN(best) || Math.Abs(by) < Math.Abs(best)) && Meet(line, other, holds, box, of))
                    {
                        best = by;
                    }
                }
            }
            return double.IsNaN(best) ? 0 : best;
        }
    }

    /// <summary>Whether two lines snap together, as Windows Forms pairs them: edges and baselines of one kind, a margin with the
    /// facing margin of a neighbour beside it (the two side by side or one above the other), or with its holder's padding on
    /// the same side.</summary>
    private static bool Meet(DesignSnap own, DesignSnap other, bool holds, Rect box, DesignedBox of)
    {
        if (own.Type != other.Type)
        {
            return false;
        }
        if (own.Filter is null || other.Filter is null)
        {
            return own.Filter is null && other.Filter is null;
        }
        string side = own.Filter[(own.Filter.IndexOf('.') + 1)..];
        if (holds)
        {
            return other.Filter == $"Padding.{side}";
        }
        string facing = side switch { "Left" => "Right", "Right" => "Left", "Top" => "Bottom", _ => "Top" };
        bool beside = Upright(own) ? box.Top < of.Y + of.Height && of.Y < box.Bottom : box.Left < of.X + of.Width && of.X < box.Right;
        return beside && other.Filter == $"Margin.{facing}";
    }

    private static bool IsPadding(DesignSnap line) => line.Filter?.StartsWith("Padding.", StringComparison.Ordinal) == true;

    /// <summary>Whether a line runs down the frame, so the box moves across to meet it.</summary>
    private static bool Upright(DesignSnap line) => line.Type is "Left" or "Right" or "Vertical";

    /// <summary>Whether a line goes with a side the gesture moves: a move's every line, a resize's those of its moving sides
    /// (a baseline, which follows the text, never).</summary>
    private static bool Moves(DesignSnap line, Sides sides) => sides.Whole || EndSide(line) switch
    {
        "Left" => sides.Left,
        "Right" => sides.Right,
        "Top" => sides.Top,
        "Bottom" => sides.Bottom,
        _ => false,
    };

    /// <summary>The side a line is measured from: a margin's own, an edge's own, a baseline's none.</summary>
    private static string? EndSide(DesignSnap line) => line.Filter is { } filter ? filter[(filter.IndexOf('.') + 1)..] : line.Type is "Baseline" ? null : line.Type;

    /// <summary>How far a line stood from the side it goes with, where the box stood.</summary>
    private static double Offset(DesignSnap line, DesignedBox box) => EndSide(line) switch
    {
        "Right" => line.At - (box.X + box.Width),
        "Bottom" => line.At - (box.Y + box.Height),
        _ => line.At - (Upright(line) ? box.X : box.Y),
    };

    /// <summary>Where a line lies for a box where it is now.</summary>
    private static double Place(DesignSnap line, double offset, Rect box) => EndSide(line) switch
    {
        "Right" => box.Right + offset,
        "Bottom" => box.Bottom + offset,
        _ => (Upright(line) ? box.Left : box.Top) + offset,
    };

    /// <summary>A box with the sides the gesture moves taken across and down by the distances found.</summary>
    private static Rect Shifted(Rect box, Sides sides, double across, double down)
    {
        if (sides.Whole)
        {
            return box.Translate(new Vector(across, down));
        }
        double left = box.Left + (sides.Left ? across : 0);
        double right = box.Right + (sides.Right ? across : 0);
        double top = box.Top + (sides.Top ? down : 0);
        double bottom = box.Bottom + (sides.Bottom ? down : 0);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }
}
