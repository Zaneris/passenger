using Avalonia;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Vcs;

/// <summary>The gutter's change bars against the code, the lines the editor's decorations mark as differing from the last
/// commit, read for the lines on screen, each kind a shape of its own as well as its colour (<see cref="ShapeOf"/>): a
/// green bar for lines added, a blue bar broken into dashes for lines changed, two red rails in conflict, a grey wedge where
/// lines were deleted. A click on one is <see cref="Pressed"/>'s.</summary>
public sealed class ChangeColumn(CodeEditor editor) : IGutterColumn
{
    /// <summary>Its strip, between the marks and the folds.</summary>
    public const double StripWidth = 8;

    /// <summary>Never raised: its width stays, and the editor draws its decorations again as they are set.</summary>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <summary>A click on a change: its popup.</summary>
    public event EventHandler<(GutterClick Click, ChangeMark Change)>? Pressed;

    /// <summary>The change a click on a line falls on: one with lines, on one of them, a deletion on either line beside it.</summary>
    public static ChangeMark? At(IReadOnlyList<ChangeMark> marks, int line) =>
        marks.Cast<ChangeMark?>().FirstOrDefault(m => m!.Value.Count == 0
            ? line == m.Value.Line || line == m.Value.Line - 1
            : line >= m.Value.Line && line < m.Value.Line + m.Value.Count);

    public double Width(GutterFont font) => StripWidth;

    public void Draw(GutterFrame frame)
    {
        double x = frame.Left + StripWidth - 5;
        LineOrdered<ChangeMark> changes = editor.Decorations.ChangesByLine;
        for (int i = changes.From(frame.First); i < changes.Count && changes.LineOf(i) <= frame.Last + 1; i++)
        {
            ChangeMark change = changes[i];
            if (change.Line + Math.Max(1, change.Count) <= frame.First || change.Line > frame.Last + 1)
            {
                continue;
            }
            double top = frame.TopOf(change.Line);
            if (ShapeOf(change.Kind) == ChangeShape.Wedge)
            {
                GutterShape key = new(nameof(ChangeMarkKind.Deleted), 4);
                if (!frame.TryShape(key, out StreamGeometry? wedge))
                {
                    wedge = frame.AddShape(key);
                    using StreamGeometryContext path = wedge.Open();
                    path.BeginFigure(new Point(-1, -4), true);
                    path.LineTo(new Point(4, 0));
                    path.LineTo(new Point(-1, 4));
                    path.EndFigure(true);
                }
                frame.DrawShape(wedge, frame.Brush(TokenKeys.Vcs_Deleted), null, x, top);
                continue;
            }
            IBrush brush = frame.Brush(change.Kind switch
            {
                ChangeMarkKind.Added => TokenKeys.Vcs_Added,
                ChangeMarkKind.Conflict => TokenKeys.Vcs_Conflict,
                _ => TokenKeys.Vcs_Modified,
            });
            double bottom = frame.BottomOf(Math.Min(change.Line + change.Count - 1, frame.LineCount - 1));
            Rect bar = new(x, top + 1, BarWidth, Math.Max(1, bottom - top - 2));
            switch (ShapeOf(change.Kind))
            {
                case ChangeShape.Rails:
                    // Two pixels apart, so they stay two where the strip stands between pixels.
                    frame.Context.FillRectangle(brush, bar.WithWidth(1));
                    frame.Context.FillRectangle(brush, bar.WithX(bar.Right).WithWidth(1));
                    break;
                case ChangeShape.Dashes:
                    // From the bar's top, so a block's dashes line up across its rows; only those beside this row.
                    double first = bar.Top + (Math.Max(0, Math.Floor((-bar.Top - Dash) / DashPeriod)) * DashPeriod);
                    for (double y = first; y < Math.Min(bar.Bottom, frame.Height); y += DashPeriod)
                    {
                        frame.Context.FillRectangle(brush, new Rect(x, y, BarWidth, Math.Min(Dash, bar.Bottom - y)));
                    }
                    break;
                default:
                    frame.Context.FillRectangle(brush, bar, 1);
                    break;
            }
        }
    }

    private const double BarWidth = 3;
    private const double Dash = 3;
    private const double DashPeriod = 5;

    /// <summary>The shape a kind of change takes in the gutter, apart from its colour.</summary>
    public static ChangeShape ShapeOf(ChangeMarkKind kind) => kind switch
    {
        ChangeMarkKind.Added => ChangeShape.Bar,
        ChangeMarkKind.Deleted => ChangeShape.Wedge,
        ChangeMarkKind.Conflict => ChangeShape.Rails,
        _ => ChangeShape.Dashes,
    };

    public GutterAnswer Press(GutterPress press)
    {
        if (press.Click.Context || !press.OnStrip || Pressed is null || At(editor.ChangeMarks, press.Click.Line) is not { } change)
        {
            return GutterAnswer.Passed;
        }
        Pressed(this, (press.Click, change));
        return GutterAnswer.Taken;
    }
}

/// <summary>A change's shape in the gutter: a whole bar, a bar broken into dashes, two thin rails, or a wedge between lines.</summary>
public enum ChangeShape
{
    Bar,
    Dashes,
    Rails,
    Wedge,
}
