using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Passenger.Editor;

/// <summary>The gutter: the host's columns left of the line numbers, the numbers, the host's columns right of them and the
/// folds' chevrons against the code. The numbers keep a margin each side (<see cref="GutterPadLeft"/>,
/// <see cref="GutterPadRight"/>) that the columns beside them take first. A press on the gutter is offered to each column
/// in turn, left to right, and one none takes is the host's (<see cref="GutterClicked"/>); a column draws only the lines on
/// screen, the outermost first, so one reaching into its neighbour nearer the numbers is drawn over it.</summary>
public sealed partial class CodeEditor
{
    private IReadOnlyList<IGutterColumn> leftColumns = [];
    private IReadOnlyList<IGutterColumn> rightColumns = [];

    /// <summary>The room left and right of the numbers, as the columns there last measured themselves.</summary>
    private (double Left, double Right) columnRoom = (GutterPadLeft, GutterPadRight);

    private GutterFrame? gutterFrame;
    private LineTints? lineTints;

    /// <summary>The column a press was taken to drag by, while it drags.</summary>
    private IGutterColumn? dragging;

    private readonly Dictionary<string, IBrush> gutterBrushes = [];
    private readonly Dictionary<(string Key, double Scale, bool Bold), TextRunProperties> gutterRuns = [];
    private readonly LabelCache<(string Text, string Key, double Scale, bool Bold)> gutterLabels = new();

    /// <summary>Sets the host's columns: those left of the numbers, outermost first, and those right of them, nearest first.</summary>
    public void SetGutterColumns(IReadOnlyList<IGutterColumn> left, IReadOnlyList<IGutterColumn> right)
    {
        foreach (IGutterColumn column in Columns())
        {
            column.Changed -= OnColumnChanged;
        }
        leftColumns = left;
        rightColumns = right;
        foreach (IGutterColumn column in Columns())
        {
            column.Changed += OnColumnChanged;
        }
        MeasureColumns();
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>Every column, left to right.</summary>
    private IEnumerable<IGutterColumn> Columns() => leftColumns.Concat(rightColumns);

    private void OnColumnChanged(object? sender, EventArgs e)
    {
        if (MeasureColumns())
        {
            InvalidateArrange();
        }
        InvalidateVisual();
    }

    /// <summary>Measures the columns at the font in force: whether the room they take changed.</summary>
    private bool MeasureColumns()
    {
        GutterFont font = new(metrics.CharWidth, metrics.LineHeight);
        double left = 0;
        double right = 0;
        for (int i = 0; i < leftColumns.Count; i++)
        {
            left += leftColumns[i].Width(font);
        }
        for (int i = 0; i < rightColumns.Count; i++)
        {
            right += rightColumns[i].Width(font);
        }
        (double Left, double Right) room = (Math.Max(GutterPadLeft, left), Math.Max(GutterPadRight, right));
        if (room == columnRoom)
        {
            return false;
        }
        columnRoom = room;
        return true;
    }

    /// <summary>A column's strip: its left edge, in the editor's coordinates, and its width.</summary>
    private (double Left, double Width) StripOf(IGutterColumn target)
    {
        GutterFont font = new(metrics.CharWidth, metrics.LineHeight);
        double x = columnRoom.Left;
        for (int i = leftColumns.Count - 1; i >= 0; i--)
        {
            double width = leftColumns[i].Width(font);
            x -= width;
            if (leftColumns[i] == target)
            {
                return (x, width);
            }
        }
        x = NumbersRight;
        for (int i = 0; i < rightColumns.Count; i++)
        {
            double width = rightColumns[i].Width(font);
            if (rightColumns[i] == target)
            {
                return (x, width);
            }
            x += width;
        }
        return (0, 0);
    }

    /// <summary>Offers a press to the columns from the <paramref name="from"/>th, left to right, until one takes it: whether
    /// one did. One taken to drag holds the pointer until it lets go.</summary>
    private bool PressColumns(GutterClick click, IPointer? pointer, int from = 0)
    {
        int index = 0;
        foreach (IGutterColumn column in Columns())
        {
            if (index++ < from)
            {
                continue;
            }
            (double left, double width) = StripOf(column);
            switch (column.Press(new GutterPress(click, click.Position.X - left, width)))
            {
                case GutterAnswer.Taken:
                    return true;
                case GutterAnswer.Dragging:
                    dragging = column;
                    drag = new Drag(DragMode.Gutter, click.Line, click.Position);
                    pointer?.Capture(this);
                    return true;
            }
        }
        return false;
    }

    /// <summary>The drag a column took let go: one that came to nothing is the click it began as, which the columns after it
    /// and then the host hear.</summary>
    private void DropColumn(KeyModifiers modifiers, Point at)
    {
        IGutterColumn column = dragging!;
        dragging = null;
        GutterClick click = new(drag.Anchor, modifiers, at, Context: false);
        (double left, double width) = StripOf(column);
        bool done = column.Drop(new GutterPress(click, at.X - left, width));
        InvalidateVisual();
        if (!done && !PressColumns(click, null, Columns().TakeWhile(c => c != column).Count() + 1))
        {
            GutterClicked?.Invoke(this, click);
        }
    }

    internal double GutterTopOf(int line) => TopOf(line) - scroll.Y;

    internal double GutterBottomOf(int line) => BottomOf(line) - scroll.Y;

    internal bool GutterHides(int line) => IsHidden(line);

    /// <summary>A token's brush in the look in force, kept until the look changes; transparent for one the look lacks.</summary>
    internal IBrush GutterBrush(string key)
    {
        if (!gutterBrushes.TryGetValue(key, out IBrush? brush))
        {
            gutterBrushes[key] = brush = FindBrush(key) ?? Brushes.Transparent;
        }
        return brush;
    }

    private IBrush? FindBrush(string key) => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value as IBrush : null;

    /// <summary>Whether a brush the columns drew with is another in the resources now (a colour scheme laid over them).</summary>
    private bool GutterBrushesChanged()
    {
        foreach ((string key, IBrush brush) in gutterBrushes)
        {
            if (!ReferenceEquals(FindBrush(key) ?? Brushes.Transparent, brush))
            {
                return true;
            }
        }
        return false;
    }

    internal TextLine GutterLabel(string text, string key, double scale, bool bold) =>
        gutterLabels.TryGet((text, key, scale, bold), out TextLine? label) ? label : gutterLabels.Add((text, key, scale, bold), Format(text, [], GutterRun(key, scale, bold)));

    private TextRunProperties GutterRun(string key, double scale, bool bold)
    {
        if (!gutterRuns.TryGetValue((key, scale, bold), out TextRunProperties? run))
        {
            IBrush brush = GutterBrush(key);
            gutterRuns[(key, scale, bold)] = run = scale == 1 && !bold ? RunWith(brush)
                : new GenericTextRunProperties(new Typeface(FontFamily, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), FontSize * scale,
                    foregroundBrush: brush, fontFeatures: ligatures ? null : NoLigatures);
        }
        return run;
    }

    /// <summary>Lets go of the brushes, runs and labels the columns drew with, and measures them again, for a new look or font.</summary>
    private void ForgetGutterLook()
    {
        gutterBrushes.Clear();
        gutterRuns.Clear();
        gutterLabels.Clear();
        MeasureColumns();
    }

    /// <summary>The tints the columns lay behind the lines on screen, asked for once a frame.</summary>
    private LineTints TintsOf(int first, int last)
    {
        LineTints tints = lineTints ??= new LineTints(this);
        tints.Begin(first, last);
        if (ShowLineNumbers && last >= first)
        {
            for (int i = 0; i < leftColumns.Count; i++)
            {
                leftColumns[i].Tint(tints);
            }
            for (int i = 0; i < rightColumns.Count; i++)
            {
                rightColumns[i].Tint(tints);
            }
        }
        return tints;
    }

    /// <summary>The gutter beside the rows shown: the columns and the folds' chevrons, then each line's number beside its
    /// first row (a wrapped line's other rows left bare) unless a column covered it.</summary>
    private void DrawGutter(DrawingContext context, List<RowSpan> shown, int row, HashSet<int> caretLines, double gutter, double height)
    {
        context.FillRectangle(palette.Gutter, new Rect(0, 0, gutter, height));
        if (!ShowLineNumbers || shown.Count == 0)
        {
            return;
        }
        int first = shown[0].Line;
        int last = shown[^1].Line;
        double right = NumbersRight;
        GutterFrame frame = gutterFrame ??= new GutterFrame(this);
        frame.Begin(context, right, height, metrics.LineHeight, metrics.CharWidth, first, last, document.Snapshot.LineCount);
        for (int i = 0; i < shown.Count; i++)
        {
            if (shown[i].Row == 0)
            {
                frame.Shown.Add(new GutterLine(shown[i].Line, ((row + i) * metrics.LineHeight) - scroll.Y));
            }
        }
        GutterFont font = new(metrics.CharWidth, metrics.LineHeight);
        double x = right + columnRoom.Right;
        for (int i = rightColumns.Count - 1; i >= 0; i--)
        {
            double width = rightColumns[i].Width(font);
            x -= width;
            frame.Strip(x, width);
            rightColumns[i].Draw(frame);
        }
        x = columnRoom.Left;
        for (int i = leftColumns.Count - 1; i >= 0; i--)
        {
            x -= leftColumns[i].Width(font);
        }
        for (int i = 0; i < leftColumns.Count; i++)
        {
            double width = leftColumns[i].Width(font);
            frame.Strip(x, width);
            leftColumns[i].Draw(frame);
            x += width;
        }
        DrawFoldMarkers(context, first, last, gutter);
        foreach (GutterLine line in frame.Lines)
        {
            if (frame.Covers(line.Line))
            {
                continue;
            }
            bool current = caretLines.Contains(line.Line);
            // Found without a closure made for each number each frame.
            if (!numbers.TryGet((line.Line + 1, current), out TextLine? number))
            {
                number = numbers.Add((line.Line + 1, current), Format((line.Line + 1).ToString(CultureInfo.InvariantCulture), [], current ? numberRuns.Current : numberRuns.Other));
            }
            double y = line.Top + (metrics.LineHeight - number.Height) / 2;
            number.Draw(context, new Point(right - number.WidthIncludingTrailingWhitespace, y));
        }
        frame.End();
        gutterLabels.EndFrame();
    }
}
