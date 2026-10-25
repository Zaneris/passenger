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

    /// <summary>A bookmark's ribbon: a strip with a notch cut from its foot.</summary>
    private static void DrawRibbon(GutterFrame frame, double x, double top)
    {
        double height = Math.Min(frame.LineHeight - 4, 12);
        double y = top + ((frame.LineHeight - height) / 2);
        StreamGeometry ribbon = new();
        using (StreamGeometryContext path = ribbon.Open())
        {
            path.BeginFigure(new Point(x, y), true);
            path.LineTo(new Point(x + 8, y));
            path.LineTo(new Point(x + 8, y + height));
            path.LineTo(new Point(x + 4, y + height - 3));
            path.LineTo(new Point(x, y + height));
            path.EndFigure(true);
        }
        frame.Context.DrawGeometry(frame.Brush(TokenKeys.Editor_Bookmark), null, ribbon);
    }
}
