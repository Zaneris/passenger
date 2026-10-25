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
/// dot, one that does not stop (a tracepoint) is amber, and a muted one (Mute Breakpoints) is grey. A breakpoint on part of
/// its line (an XAML attribute) has that <see cref="Range"/> tinted rather than the whole line, and its dot only where the
/// line has no breakpoint of its own.</summary>
public readonly record struct LineMark(int Line, LineMarkKind Kind, bool Conditional = false, bool Tracepoint = false, bool Muted = false,
    TextRange? Range = null);

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
        for (int i = 0; i < Marks.Count; i++)
        {
            LineMark mark = Marks[i];
            if (mark.Line < first || mark.Line > last)
            {
                continue;
            }
            if (mark.Kind == LineMarkKind.ExecutionPoint)
            {
                executing.Add(mark.Line);
                continue;
            }
            // The line's own breakpoint gives the dot over one on a part of it.
            if (!breakpoints.TryGetValue(mark.Line, out LineMark there) || (there.Range is not null && mark.Range is null))
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
        for (int i = 0; i < Marks.Count; i++)
        {
            LineMark mark = Marks[i];
            if (mark.Line < tints.First || mark.Line > tints.Last || mark.Kind == LineMarkKind.ExecutionPoint)
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

    /// <summary>Whether a breakpoint tints what it is on: one that stops and can.</summary>
    private static bool Tinted(LineMark mark) => !mark.Tracepoint && !mark.Muted && mark.Kind is LineMarkKind.Breakpoint or LineMarkKind.BreakpointBound;

    /// <summary>A breakpoint's dot, centred at a point: filled when set, with a tick once bound, a ring when it cannot bind
    /// or is off; a question mark on it for a condition, amber for a tracepoint, grey muted.</summary>
    public static void DrawBreakpoint(GutterFrame frame, LineMark mark, double x, double y)
    {
        DrawingContext context = frame.Context;
        double radius = Math.Min(frame.LineHeight - 6, 14) / 2;
        Point centre = new(x, y);
        IBrush fill = frame.Brush(mark.Muted ? TokenKeys.Editor_BreakpointMuted : mark.Tracepoint ? TokenKeys.Editor_Tracepoint : TokenKeys.Editor_Breakpoint);
        if (mark.Muted && mark.Kind == LineMarkKind.BreakpointDisabled)
        {
            context.DrawEllipse(null, new Pen(fill, 1.5), centre, radius - 0.75, radius - 0.75);
            return;
        }
        if (mark.Conditional && mark.Kind is LineMarkKind.Breakpoint or LineMarkKind.BreakpointBound)
        {
            context.DrawEllipse(fill, null, centre, radius, radius);
            TextLine question = frame.Label("?", TokenKeys.Editor_Background, 0.75, bold: true);
            question.Draw(context, new Point(x - question.WidthIncludingTrailingWhitespace / 2, y - question.Height / 2));
            return;
        }
        switch (mark.Kind)
        {
            case LineMarkKind.Breakpoint:
                context.DrawEllipse(fill, null, centre, radius, radius);
                break;
            case LineMarkKind.BreakpointBound:
                context.DrawEllipse(fill, null, centre, radius, radius);
                StreamGeometry tick = new();
                using (StreamGeometryContext path = tick.Open())
                {
                    path.BeginFigure(new Point(x - radius * 0.45, y + radius * 0.02), false);
                    path.LineTo(new Point(x - radius * 0.1, y + radius * 0.38));
                    path.LineTo(new Point(x + radius * 0.48, y - radius * 0.35));
                    path.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(frame.Brush(TokenKeys.Editor_Background), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), tick);
                break;
            case LineMarkKind.BreakpointInvalid:
                context.DrawEllipse(null, new Pen(fill, 1.5), centre, radius - 0.75, radius - 0.75);
                break;
            default:
                context.DrawEllipse(null, new Pen(frame.Brush(TokenKeys.Editor_LineNumber), 1.5), centre, radius - 0.75, radius - 0.75);
                break;
        }
    }

    /// <summary>The execution point's arrow, from a column's left edge.</summary>
    public static void DrawExecutionArrow(GutterFrame frame, double left, double top)
    {
        double height = Math.Min(frame.LineHeight - 6, 12);
        double y = top + (frame.LineHeight - height) / 2;
        double x = left + 5;
        const double Width = 13;
        StreamGeometry arrow = new();
        using (StreamGeometryContext path = arrow.Open())
        {
            path.BeginFigure(new Point(x, y), true);
            path.LineTo(new Point(x + Width - height / 2, y));
            path.LineTo(new Point(x + Width, y + height / 2));
            path.LineTo(new Point(x + Width - height / 2, y + height));
            path.LineTo(new Point(x, y + height));
            path.EndFigure(true);
        }
        frame.Context.DrawGeometry(frame.Brush(TokenKeys.Editor_ExecutionPoint), null, arrow);
    }
}
