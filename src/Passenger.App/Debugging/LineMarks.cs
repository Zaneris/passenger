using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>What the gutter shows at a line, and how the line behind it is tinted: a breakpoint (set, bound to code while
/// debugging, failing to bind, or disabled), and the debugger's execution point.</summary>
public enum LineMarkKind
{
    /// <summary>A breakpoint, not yet known to bind or not: a red dot over the line number.</summary>
    Breakpoint,

    /// <summary>A breakpoint the running debugger bound to code: the dot with a tick.</summary>
    BreakpointBound,

    /// <summary>A breakpoint the running debugger could not bind (no code there, no symbols): a ring.</summary>
    BreakpointInvalid,

    /// <summary>A breakpoint turned off: a ring, and no tint on its line.</summary>
    BreakpointDisabled,

    /// <summary>Where the debugger stopped: an arrow beside the number and the line tinted over any other.</summary>
    ExecutionPoint,
}

/// <summary>A mark on a line (from zero) of a file. A breakpoint with a condition or hit count shows a question mark on its
/// dot, a temporary one a 1, one waiting for another a chevron, a snapshot point a lens, one that does not stop (a
/// tracepoint, a snapshot point) is amber, and a muted one (Mute Breakpoints) is grey. A breakpoint on part of its line (an XAML attribute) has that <see cref="Range"/> tinted
/// rather than the whole line, and its dot only where the line has no breakpoint of its own. A method breakpoint, on the
/// line declaring its method, is a diamond, which tints nothing and gives way to the line's own dot.</summary>
public readonly record struct LineMark(int Line, LineMarkKind Kind, bool Conditional = false, bool Tracepoint = false, bool Muted = false,
    TextRange? Range = null, bool Temporary = false, bool Waiting = false, bool Method = false, bool Snapshot = false);

/// <summary>The debugger's marks as the gutter draws them: a breakpoint's dot over its line's number, the execution point's
/// arrow right of it, and the tints of their lines, each looked up once a frame for the lines on screen.</summary>
public sealed class DebugMarks
{
    private readonly HashSet<int> executing = [];
    private readonly Dictionary<int, LineMark> breakpoints = [];

    /// <summary>The marks, from the debugger.</summary>
    public IReadOnlyList<LineMark> Marks { get; set; } = [];

    /// <summary>Whether the execution point is on a line.</summary>
    public bool Executes(int line)
    {
        for (int i = 0; i < Marks.Count; i++)
        {
            if (Marks[i] is { Kind: LineMarkKind.ExecutionPoint } mark && mark.Line == line)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Takes the marks on the lines from <paramref name="first"/> to <paramref name="last"/>, for a frame.</summary>
    public void Find(int first, int last)
    {
        executing.Clear();
        breakpoints.Clear();
        LineOrdered<LineMark> marks = LineOrdered<LineMark>.Of(Marks, static m => m.Line);
        for (int i = marks.From(first); i < marks.Count && marks.LineOf(i) <= last; i++)
        {
            LineMark mark = marks[i];
            if (mark.Kind == LineMarkKind.ExecutionPoint)
            {
                executing.Add(mark.Line);
                continue;
            }
            // The line's own breakpoint gives the dot over one on a part of it, and over a method's diamond.
            if (!breakpoints.TryGetValue(mark.Line, out LineMark there) || (there.Range is not null && mark.Range is null && !mark.Method)
                || (there.Method && !mark.Method))
            {
                breakpoints[mark.Line] = mark;
            }
        }
    }

    /// <summary>Whether the execution point is on a line found.</summary>
    public bool IsExecuting(int line) => executing.Contains(line);

    /// <summary>The breakpoint whose dot a line found shows.</summary>
    public bool TryBreakpoint(int line, out LineMark mark) => breakpoints.TryGetValue(line, out mark);

    /// <summary>Tints the lines on screen: the execution point's over a breakpoint's, none for a breakpoint that is off or
    /// cannot bind, and a breakpoint on part of a line that part.</summary>
    public void Tint(LineTints tints)
    {
        Find(tints.First, tints.Last);
        LineOrdered<LineMark> marks = LineOrdered<LineMark>.Of(Marks, static m => m.Line);
        for (int i = marks.From(tints.First); i < marks.Count && marks.LineOf(i) <= tints.Last; i++)
        {
            LineMark mark = marks[i];
            if (mark.Kind == LineMarkKind.ExecutionPoint)
            {
                continue;
            }
            if (mark.Range is { } range && Tinted(mark))
            {
                tints.Part(mark.Line, range, tints.Brush(TokenKeys.Editor_BreakpointLine));
            }
        }
        foreach ((int line, LineMark mark) in breakpoints)
        {
            if (mark.Range is null && Tinted(mark))
            {
                tints.Line(line, tints.Brush(TokenKeys.Editor_BreakpointLine));
            }
        }
        foreach (int line in executing)
        {
            tints.Line(line, tints.Brush(TokenKeys.Editor_ExecutionLine));
        }
    }

    /// <summary>Whether a breakpoint tints what it is on: one that stops and can, at a line of its own.</summary>
    private static bool Tinted(LineMark mark) => !mark.Tracepoint && !mark.Muted && !mark.Method && mark.Kind is LineMarkKind.Breakpoint or LineMarkKind.BreakpointBound;

    /// <summary>A breakpoint's dot (a method breakpoint's diamond), centred at a point: filled when set, with a tick once bound,
    /// a ring when it cannot bind or is off; a chevron on it while it waits for another, else a lens for a snapshot point, else
    /// a 1 for a temporary one, else a question mark for a condition; amber for a tracepoint or a snapshot point, grey muted.</summary>
    public static void DrawBreakpoint(GutterFrame frame, LineMark mark, double x, double y)
    {
        double radius = Math.Min(frame.LineHeight - 6, 14) / 2;
        string fillKey = mark.Muted ? TokenKeys.Editor_BreakpointMuted : mark.Tracepoint ? TokenKeys.Editor_Tracepoint : TokenKeys.Editor_Breakpoint;
        IBrush fill = frame.Brush(fillKey);
        if (mark.Muted && mark.Kind == LineMarkKind.BreakpointDisabled)
        {
            Dot(frame, mark, null, frame.Pen(fillKey, 1.5), x, y, radius - 0.75);
            return;
        }
        if (mark.Kind is LineMarkKind.Breakpoint or LineMarkKind.BreakpointBound && (mark.Waiting || mark.Snapshot || mark.Temporary || mark.Conditional))
        {
            Dot(frame, mark, fill, null, x, y, radius);
            if (mark.Snapshot && !mark.Waiting)
            {
                // A lens: a ring in the dot, with its centre.
                frame.Context.DrawEllipse(null, frame.Pen(TokenKeys.Editor_Background, 1.4), new Point(x, y), radius * 0.52, radius * 0.52);
                frame.Context.DrawEllipse(frame.Brush(TokenKeys.Editor_Background), null, new Point(x, y), radius * 0.18, radius * 0.18);
                return;
            }
            if (mark.Waiting)
            {
                GutterShape chevron = new(nameof(LineMark.Waiting), radius);
                if (!frame.TryShape(chevron, out StreamGeometry? waits))
                {
                    waits = frame.AddShape(chevron);
                    using StreamGeometryContext path = waits.Open();
                    path.BeginFigure(new Point(-radius * 0.2, -radius * 0.42), false);
                    path.LineTo(new Point(radius * 0.25, 0));
                    path.LineTo(new Point(-radius * 0.2, radius * 0.42));
                    path.EndFigure(false);
                }
                frame.DrawShape(waits, null, frame.Pen(TokenKeys.Editor_Background, 1.6, PenLineCap.Round, PenLineJoin.Round), x, y);
                return;
            }
            TextLine label = frame.Label(mark.Temporary ? "1" : "?", TokenKeys.Editor_Background, 0.75, bold: true);
            label.Draw(frame.Context, new Point(x - label.WidthIncludingTrailingWhitespace / 2, y - label.Height / 2));
            return;
        }
        switch (mark.Kind)
        {
            case LineMarkKind.Breakpoint:
                Dot(frame, mark, fill, null, x, y, radius);
                break;
            case LineMarkKind.BreakpointBound:
                Dot(frame, mark, fill, null, x, y, radius);
                GutterShape key = new(nameof(LineMarkKind.BreakpointBound), radius);
                if (!frame.TryShape(key, out StreamGeometry? tick))
                {
                    tick = frame.AddShape(key);
                    using StreamGeometryContext path = tick.Open();
                    path.BeginFigure(new Point(-radius * 0.45, radius * 0.02), false);
                    path.LineTo(new Point(-radius * 0.1, radius * 0.38));
                    path.LineTo(new Point(radius * 0.48, -radius * 0.35));
                    path.EndFigure(false);
                }
                frame.DrawShape(tick, null, frame.Pen(TokenKeys.Editor_Background, 1.6, PenLineCap.Round, PenLineJoin.Round), x, y);
                break;
            case LineMarkKind.BreakpointInvalid:
                Dot(frame, mark, null, frame.Pen(fillKey, 1.5), x, y, radius - 0.75);
                break;
            default:
                Dot(frame, mark, null, frame.Pen(TokenKeys.Editor_LineNumber, 1.5), x, y, radius - 0.75);
                break;
        }
    }

    /// <summary>A breakpoint's own shape of a radius: a method breakpoint's diamond, any other's circle.</summary>
    private static void Dot(GutterFrame frame, LineMark mark, IBrush? fill, IPen? pen, double x, double y, double radius)
    {
        if (!mark.Method)
        {
            frame.Context.DrawEllipse(fill, pen, new Point(x, y), radius, radius);
            return;
        }
        GutterShape key = new(nameof(LineMark.Method), radius);
        if (!frame.TryShape(key, out StreamGeometry? diamond))
        {
            diamond = frame.AddShape(key);
            using StreamGeometryContext path = diamond.Open();
            // A little past the circle's radius, so the diamond inks about as much as a dot.
            double reach = radius * 1.2;
            path.BeginFigure(new Point(0, -reach), true);
            path.LineTo(new Point(reach, 0));
            path.LineTo(new Point(0, reach));
            path.LineTo(new Point(-reach, 0));
            path.EndFigure(true);
        }
        frame.DrawShape(diamond, fill, pen, x, y);
    }

    /// <summary>The execution point's arrow, from a column's left edge.</summary>
    public static void DrawExecutionArrow(GutterFrame frame, double left, double top)
    {
        double height = Math.Min(frame.LineHeight - 6, 12);
        double y = top + (frame.LineHeight - height) / 2;
        double x = left + 5;
        const double Width = 13;
        GutterShape key = new(nameof(DrawExecutionArrow), height);
        if (!frame.TryShape(key, out StreamGeometry? arrow))
        {
            arrow = frame.AddShape(key);
            using StreamGeometryContext path = arrow.Open();
            path.BeginFigure(new Point(0, 0), true);
            path.LineTo(new Point(Width - height / 2, 0));
            path.LineTo(new Point(Width, height / 2));
            path.LineTo(new Point(Width - height / 2, height));
            path.LineTo(new Point(0, height));
            path.EndFigure(true);
        }
        frame.DrawShape(arrow, frame.Brush(TokenKeys.Editor_ExecutionPoint), null, x, y);
    }
}
