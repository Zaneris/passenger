using Avalonia;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Vcs;

/// <summary>The gutter's change bars against the code, the lines the editor's decorations mark as differing from the last
/// commit, read for the lines on screen: green for lines added, blue for lines changed, red in conflict, a grey wedge where
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
            if (change.Kind == ChangeMarkKind.Deleted)
            {
                GutterShape key = new(nameof(ChangeMarkKind.Deleted), x, top, 4);
                if (!frame.TryShape(key, out StreamGeometry? wedge))
                {
                    wedge = frame.AddShape(key);
                    using StreamGeometryContext path = wedge.Open();
                    path.BeginFigure(new Point(x - 1, top - 4), true);
                    path.LineTo(new Point(x + 4, top));
                    path.LineTo(new Point(x - 1, top + 4));
                    path.EndFigure(true);
                }
                frame.Context.DrawGeometry(frame.Brush(TokenKeys.Vcs_Deleted), null, wedge);
                continue;
            }
            IBrush brush = frame.Brush(change.Kind switch
            {
                ChangeMarkKind.Added => TokenKeys.Vcs_Added,
                ChangeMarkKind.Conflict => TokenKeys.Vcs_Conflict,
                _ => TokenKeys.Vcs_Modified,
            });
            double bottom = frame.BottomOf(Math.Min(change.Line + change.Count - 1, frame.LineCount - 1));
            frame.Context.FillRectangle(brush, new Rect(x, top + 1, 3, Math.Max(1, bottom - top - 2)), 1);
        }
    }

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
