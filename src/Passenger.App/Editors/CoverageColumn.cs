using System.ComponentModel;
using Avalonia;
using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>A line's coverage mark's shape beside its colour: a whole bar where it ran with every branch taken (or every test
/// running it passed), a bar half filled where some of its branches were never taken, a hollow bar where it never ran (or no
/// test runs it), a bar broken in two where a test running it failed, and a dotted one where its tests are yet to run again.</summary>
public enum CoverageShape
{
    Bar,
    Half,
    Hollow,
    Broken,
    Dotted,
}

/// <summary>The gutter's column of the coverage measured, nearest the text: a bar beside each line a run with coverage
/// measured, in the colour of a test that passed where it ran whole, a warning's where it ran with branches never taken,
/// a failure's where it never ran, each a shape of its own too (<see cref="ShapeOf"/>). Where continuous testing maps the
/// file, its marks show in their place: how the tests running each line last ended (<see cref="ShapeOf(ReachState)"/>), a
/// press on one asking which they are (<see cref="Asked"/>). It takes no room in a file with none, and draws from the file's
/// marks for the lines on screen alone.</summary>
public sealed class CoverageColumn : IGutterColumn
{
    /// <summary>Its strip, between the change bars and the pinned errors.</summary>
    public const double StripWidth = 7;

    private const double BarWidth = 4;

    private readonly FollowedDocument file;

    public CoverageColumn() => file = FollowedDocument.OfProperties(OnFileChanged);

    public event EventHandler? Changed;

    /// <summary>A press on a line's mark of the tests running it: the line (from zero) and the mark; true where it was taken.</summary>
    public Func<int, ReachedLine, bool>? Asked { get; set; }

    /// <summary>The marks of the file shown, none before a run with coverage or once it is cleared.</summary>
    public CoveredLines? Lines => file.Document?.Coverage;

    /// <summary>The marks of the tests running the file shown's lines, none while continuous testing maps nothing of it.</summary>
    public ReachedLines? Reached => file.Document?.Reach;

    public static CoverageShape ShapeOf(ReachState state) => state switch
    {
        ReachState.Passed => CoverageShape.Bar,
        ReachState.Failed => CoverageShape.Broken,
        ReachState.Waiting => CoverageShape.Dotted,
        _ => CoverageShape.Hollow,
    };

    public static string BrushOf(ReachState state) => state switch
    {
        ReachState.Passed => TokenKeys.Test_Passed,
        ReachState.Waiting => TokenKeys.Editor_Warning,
        _ => TokenKeys.Test_Failed,
    };

    public static CoverageShape ShapeOf(CoverageState state) => state switch
    {
        CoverageState.Covered => CoverageShape.Bar,
        CoverageState.Partial => CoverageShape.Half,
        _ => CoverageShape.Hollow,
    };

    /// <summary>The colour a state is drawn in.</summary>
    public static string BrushOf(CoverageState state) => state switch
    {
        CoverageState.Covered => TokenKeys.Test_Passed,
        CoverageState.Partial => TokenKeys.Editor_Warning,
        _ => TokenKeys.Test_Failed,
    };

    public void Show(OpenDocument? document)
    {
        if (file.Show(document))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OpenDocument.Coverage) or nameof(OpenDocument.Reach))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public double Width(GutterFont font) => Lines is { Count: > 0 } || Reached is { Count: > 0 } ? StripWidth : 0;

    public void Draw(GutterFrame frame)
    {
        if (Reached is { Count: > 0 } reached)
        {
            for (int at = reached.From(frame.First); at < reached.Count; at++)
            {
                int line = reached.LineOf(at);
                if (line > frame.Last)
                {
                    break;
                }
                if (reached.Stands(at) && !frame.IsHidden(line))
                {
                    DrawMark(frame, ShapeOf(reached[at].State), BrushOf(reached[at].State), frame.Left, line);
                }
            }
            return;
        }
        if (Lines is not { Count: > 0 } lines)
        {
            return;
        }
        for (int at = lines.From(frame.First); at < lines.Count; at++)
        {
            int line = lines.LineOf(at);
            if (line > frame.Last)
            {
                break;
            }
            if (lines.Stands(at) && !frame.IsHidden(line))
            {
                DrawMark(frame, lines[at].State, frame.Left, line);
            }
        }
    }

    /// <summary>A line's mark of a state, in a strip whose left edge is given, beside the line's rows.</summary>
    public static void DrawMark(GutterFrame frame, CoverageState state, double left, int line) => DrawMark(frame, ShapeOf(state), BrushOf(state), left, line);

    /// <summary>A line's mark of a shape and colour, in a strip whose left edge is given, beside the line's rows.</summary>
    public static void DrawMark(GutterFrame frame, CoverageShape shape, string brush, double left, int line)
    {
        double top = frame.TopOf(line);
        Rect bar = new(left + ((StripWidth - BarWidth) / 2), top + 1, BarWidth, Math.Max(2, frame.BottomOf(line) - top - 2));
        switch (shape)
        {
            case CoverageShape.Bar:
                frame.Context.FillRectangle(frame.Brush(brush), bar);
                break;
            case CoverageShape.Half:
                frame.Context.FillRectangle(frame.Brush(brush), bar.WithHeight(bar.Height / 2));
                frame.Context.DrawRectangle(frame.Pen(brush, 1), bar.Deflate(0.5));
                break;
            case CoverageShape.Broken:
                // Two halves with a gap of a third of the bar between.
                frame.Context.FillRectangle(frame.Brush(brush), bar.WithHeight(bar.Height / 3));
                frame.Context.FillRectangle(frame.Brush(brush), new Rect(bar.X, bar.Bottom - (bar.Height / 3), bar.Width, bar.Height / 3));
                break;
            case CoverageShape.Dotted:
                for (double y = bar.Top; y < bar.Bottom; y += BarWidth * 1.5)
                {
                    frame.Context.FillRectangle(frame.Brush(brush), new Rect(bar.X, y, bar.Width, Math.Min(BarWidth / 2, bar.Bottom - y)));
                }
                break;
            default:
                frame.Context.DrawRectangle(frame.Pen(brush, 1), bar.Deflate(0.5));
                break;
        }
    }

    public GutterAnswer Press(GutterPress press)
    {
        if (press.Click.Context || Reached?.At(press.Click.Line) is not { } line || Asked is not { } asked)
        {
            return GutterAnswer.Passed;
        }
        return asked(press.Click.Line, line) ? GutterAnswer.Taken : GutterAnswer.Passed;
    }
}
