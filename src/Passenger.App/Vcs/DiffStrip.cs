using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A change between two panes: its top and bottom on each, in the strip's coordinates, and its colour.</summary>
public readonly record struct DiffBand(int Index, double LeftTop, double LeftBottom, double RightTop, double RightBottom, ChangeMarkKind Kind);

/// <summary>The strip between two panes of a diff or a merge: each change a band from its lines on the left to its lines
/// on the right, tinted by its kind, with the arrow that takes it (pointing away from <paramref name="iconSide"/>) and the
/// X that ignores it at that side's end, while <paramref name="takes"/> and <paramref name="ignores"/> allow them.</summary>
/// <param name="pane">The pane at <paramref name="iconSide"/>, whose line the icons stand beside.</param>
/// <param name="pressed">A click on a change's arrow (false) or X (true), with the keys held.</param>
public sealed class DiffStrip(Func<IEnumerable<DiffBand>> bands, MergeSide iconSide, CodeEditor pane, Func<bool> takes, bool ignores,
    Action<DiffBand, bool, KeyModifiers> pressed) : Control
{
    private const double IconSize = 9;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Find(TokenKeys.Editor_Background), new Rect(Bounds.Size));
        double width = Bounds.Width;
        foreach (DiffBand band in bands())
        {
            if (Math.Max(band.LeftBottom, band.RightBottom) < -IconSize || Math.Min(band.LeftTop, band.RightTop) > Bounds.Height + IconSize)
            {
                continue;
            }
            (string tint, string edge) = band.Kind switch
            {
                ChangeMarkKind.Conflict => (TokenKeys.Diff_Conflict, TokenKeys.Vcs_Conflict),
                ChangeMarkKind.Added => (TokenKeys.Diff_Added, TokenKeys.Vcs_Added),
                ChangeMarkKind.Deleted => (TokenKeys.Diff_Deleted, TokenKeys.Vcs_Deleted),
                _ => (TokenKeys.Diff_Modified, TokenKeys.Vcs_Modified),
            };
            StreamGeometry shape = new();
            using (StreamGeometryContext path = shape.Open())
            {
                path.BeginFigure(new Point(0, band.LeftTop), true);
                path.CubicBezierTo(new Point(width / 2, band.LeftTop), new Point(width / 2, band.RightTop), new Point(width, band.RightTop));
                path.LineTo(new Point(width, band.RightBottom));
                path.CubicBezierTo(new Point(width / 2, band.RightBottom), new Point(width / 2, band.LeftBottom), new Point(0, band.LeftBottom));
                path.EndFigure(true);
            }
            IBrush line = Find(edge);
            context.DrawGeometry(Find(tint), new Pen(line, 1), shape);
            Pen pen = new(line, 1.6, lineCap: PenLineCap.Round);
            if (Icon(band, ignore: false) is { } arrow)
            {
                // ">>" from the left, "<<" from the right: the lines toward the other pane.
                double point = iconSide == MergeSide.Left ? 1 : -1;
                for (int k = 0; k < 2; k++)
                {
                    double x = iconSide == MergeSide.Left ? arrow.X + (k * 4.5) : arrow.Right - (k * 4.5);
                    context.DrawLine(pen, new Point(x, arrow.Y), new Point(x + (point * 4), arrow.Center.Y));
                    context.DrawLine(pen, new Point(x + (point * 4), arrow.Center.Y), new Point(x, arrow.Bottom));
                }
            }
            if (Icon(band, ignore: true) is { } cross)
            {
                Rect x = cross.Deflate(1);
                context.DrawLine(pen, x.TopLeft, x.BottomRight);
                context.DrawLine(pen, x.TopRight, x.BottomLeft);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point at = e.GetPosition(this);
        foreach (DiffBand band in bands())
        {
            foreach (bool ignore in (bool[])[false, true])
            {
                if (Icon(band, ignore) is { } icon && icon.Inflate(3).Contains(at))
                {
                    pressed(band, ignore, e.KeyModifiers);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    /// <summary>Where the change of <paramref name="index"/> has its arrow or X, or null while it has none.</summary>
    public Rect? Icon(int index, bool ignore) =>
        bands().Where(b => b.Index == index).Select(b => Icon(b, ignore)).FirstOrDefault();

    /// <summary>A band's arrow or X beside its first line at the icons' end, the arrow nearest the pane and the X after it.</summary>
    private Rect? Icon(DiffBand band, bool ignore)
    {
        if (ignore ? !ignores : !takes())
        {
            return null;
        }
        (double top, double bottom) = iconSide == MergeSide.Left ? (band.LeftTop, band.LeftBottom) : (band.RightTop, band.RightBottom);
        double y = top + Math.Min(pane.LineHeight / 2, Math.Max((IconSize / 2) + 1, (bottom - top) / 2));
        double offset = ignore ? 18 : 4;
        double x = iconSide == MergeSide.Left ? offset : Bounds.Width - offset - IconSize;
        return new Rect(x, y - (IconSize / 2), IconSize, IconSize);
    }

    private IBrush Find(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? found) && found is IBrush brush ? brush : Brushes.Transparent;
}
