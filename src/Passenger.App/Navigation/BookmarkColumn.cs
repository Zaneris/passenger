using Avalonia;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Navigation;

/// <summary>The gutter's bookmarks, left of the line numbers: a ribbon on each line the editor's decorations bookmark,
/// read for the lines on screen. A press on it is the gutter's.</summary>
public sealed class BookmarkColumn(CodeEditor editor) : IGutterColumn
{
    /// <summary>Its strip, the numbers' margin.</summary>
    private const double StripWidth = 14;

    /// <summary>Never raised: its width stays, and the editor draws its decorations again as they are set.</summary>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public double Width(GutterFont font) => StripWidth;

    public void Draw(GutterFrame frame)
    {
        LineOrdered<int> lines = editor.Decorations.BookmarksByLine;
        for (int i = lines.From(frame.First); i < lines.Count && lines.LineOf(i) <= frame.Last; i++)
        {
            int marked = lines.LineOf(i);
            if (!frame.IsHidden(marked))
            {
                DrawRibbon(frame, frame.Left + 3, frame.TopOf(marked));
            }
        }
    }

    public GutterAnswer Press(GutterPress press) => GutterAnswer.Passed;

    /// <summary>A bookmark's ribbon: a strip with a notch cut from its foot, kept while one of its height is drawn.</summary>
    private static void DrawRibbon(GutterFrame frame, double x, double top)
    {
        double height = Math.Min(frame.LineHeight - 4, 12);
        double y = top + ((frame.LineHeight - height) / 2);
        GutterShape key = new(nameof(DrawRibbon), height);
        if (!frame.TryShape(key, out StreamGeometry? ribbon))
        {
            ribbon = frame.AddShape(key);
            using StreamGeometryContext path = ribbon.Open();
            path.BeginFigure(new Point(0, 0), true);
            path.LineTo(new Point(8, 0));
            path.LineTo(new Point(8, height));
            path.LineTo(new Point(4, height - 3));
            path.LineTo(new Point(0, height));
            path.EndFigure(true);
        }
        frame.DrawShape(ribbon, frame.Brush(TokenKeys.Editor_Bookmark), null, x, y);
    }
}
