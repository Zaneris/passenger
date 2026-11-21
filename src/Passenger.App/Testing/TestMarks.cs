using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Editor;

namespace Passenger.App.Testing;

/// <summary>A test's mark in the gutter beside a test method or class: how it last ran.</summary>
public enum TestMarkKind
{
    NotRun,
    Passed,
    Failed,
    Running,
}

/// <summary>A test's run icon on a line (from zero), right of the line numbers; a click on it asks for its menu.</summary>
public readonly record struct TestMark(int Line, TestMarkKind Kind);

/// <summary>A test's run icon as the gutter draws it, each result a shape of its own as well as its colour
/// (<see cref="ShapeOf"/>): the run triangle alone before it has run, with a tick once passed and a dot once failed, and a
/// dot alone while it runs.</summary>
public static class TestMarks
{
    /// <summary>The shape a test's mark takes, apart from its colour.</summary>
    public static TestMarkShape ShapeOf(TestMarkKind kind) => kind switch
    {
        TestMarkKind.Passed => TestMarkShape.Ticked,
        TestMarkKind.Failed => TestMarkShape.Dotted,
        TestMarkKind.Running => TestMarkShape.Dot,
        _ => TestMarkShape.Triangle,
    };

    /// <summary>The run icon from a column's left edge, in its last result's colour; a dot while it runs.</summary>
    public static void Draw(GutterFrame frame, TestMarkKind kind, double left, double top)
    {
        DrawingContext context = frame.Context;
        IBrush brush = frame.Brush(kind switch
        {
            TestMarkKind.Passed => TokenKeys.Test_Passed,
            TestMarkKind.Failed => TokenKeys.Test_Failed,
            TestMarkKind.Running => TokenKeys.Editor_ExecutionPoint,
            _ => TokenKeys.Run,
        });
        double size = Math.Min(frame.LineHeight - 8, 11);
        double x = left + 6;
        double y = top + ((frame.LineHeight - size) / 2);
        TestMarkShape shape = ShapeOf(kind);
        if (shape == TestMarkShape.Dot)
        {
            context.DrawEllipse(brush, null, new Point(x + (size / 2), y + (size / 2)), size / 2.6, size / 2.6);
            return;
        }
        GutterShape key = new(nameof(TestMarks), size);
        if (!frame.TryShape(key, out StreamGeometry? triangle))
        {
            triangle = frame.AddShape(key);
            using StreamGeometryContext path = triangle.Open();
            path.BeginFigure(new Point(0, 0), true);
            path.LineTo(new Point(size * 0.9, size / 2));
            path.LineTo(new Point(0, size));
            path.EndFigure(true);
        }
        frame.DrawShape(triangle, brush, null, x, y);
        if (shape == TestMarkShape.Dotted)
        {
            // A failed test: the run icon with a red mark on it.
            context.DrawEllipse(frame.Brush(TokenKeys.Test_Failed), frame.Pen(TokenKeys.Editor_Gutter, 1.2), new Point(x + size, y + size - 1), 2.6, 2.6);
        }
        else if (shape == TestMarkShape.Ticked)
        {
            GutterShape ticked = new($"{nameof(TestMarks)}.tick", size);
            if (!frame.TryShape(ticked, out StreamGeometry? tick))
            {
                tick = frame.AddShape(ticked);
                using StreamGeometryContext path = tick.Open();
                path.BeginFigure(new Point(-2.4, 0), false);
                path.LineTo(new Point(-0.6, 1.8));
                path.LineTo(new Point(2.6, -2));
                path.EndFigure(false);
            }
            // Over a ring of the gutter's ground, so it stands apart from the triangle under it.
            frame.DrawShape(tick, null, frame.Pen(TokenKeys.Editor_Gutter, 4, PenLineCap.Round, PenLineJoin.Round), x + size, y + size - 1);
            frame.DrawShape(tick, null, frame.Pen(TokenKeys.Test_Passed, 1.6, PenLineCap.Round, PenLineJoin.Round), x + size, y + size - 1);
        }
    }
}

/// <summary>A test's mark in the gutter: the run triangle alone, with a tick, with a dot, or a dot alone.</summary>
public enum TestMarkShape
{
    Triangle,
    Ticked,
    Dotted,
    Dot,
}

/// <summary>How a feature's step went in the run that last took it, beside its line (from zero), and what its failure said
/// (a failed or ambiguous step's first line), which the editor shows after the line.</summary>
public readonly record struct StepMark(int Line, Passenger.Workspace.Gherkin.StepOutcome Outcome, string? Failure = null);

/// <summary>A step's outcome as the gutter draws it, as the Unit Tests window marks a test: a tick passed, a cross failed or
/// ambiguous, a dash skipped, a ring pending, a question mark undefined.</summary>
public static class StepMarks
{
    public static void Draw(GutterFrame frame, Passenger.Workspace.Gherkin.StepOutcome outcome, double left, double top)
    {
        double size = Math.Min(frame.LineHeight - 8, 9);
        double x = left + 3;
        double y = top + ((frame.LineHeight - size) / 2);
        switch (outcome)
        {
            case Passenger.Workspace.Gherkin.StepOutcome.Undefined:
                TextLine mark = frame.Label("?", TokenKeys.Editor_Warning, 0.85, bold: true);
                mark.Draw(frame.Context, new Point(x + ((size - mark.WidthIncludingTrailingWhitespace) / 2), top + ((frame.LineHeight - mark.Height) / 2)));
                return;
            case Passenger.Workspace.Gherkin.StepOutcome.Pending:
                frame.Context.DrawEllipse(null, frame.Pen(TokenKeys.Editor_Warning, 1.4), new Point(x + (size / 2), y + (size / 2)), size / 2.4, size / 2.4);
                return;
        }
        (string kind, string brush) = outcome switch
        {
            Passenger.Workspace.Gherkin.StepOutcome.Passed => ("tick", TokenKeys.Test_Passed),
            Passenger.Workspace.Gherkin.StepOutcome.Skipped => ("dash", TokenKeys.Test_Skipped),
            _ => ("cross", TokenKeys.Test_Failed),
        };
        GutterShape key = new($"{nameof(StepMarks)}.{kind}", size);
        if (!frame.TryShape(key, out StreamGeometry? shape))
        {
            shape = frame.AddShape(key);
            using StreamGeometryContext path = shape.Open();
            switch (kind)
            {
                case "tick":
                    path.BeginFigure(new Point(0, size * 0.55), false);
                    path.LineTo(new Point(size * 0.38, size * 0.9));
                    path.LineTo(new Point(size, size * 0.1));
                    path.EndFigure(false);
                    break;
                case "dash":
                    path.BeginFigure(new Point(size * 0.1, size / 2), false);
                    path.LineTo(new Point(size * 0.9, size / 2));
                    path.EndFigure(false);
                    break;
                default:
                    path.BeginFigure(new Point(size * 0.1, size * 0.1), false);
                    path.LineTo(new Point(size * 0.9, size * 0.9));
                    path.EndFigure(false);
                    path.BeginFigure(new Point(size * 0.9, size * 0.1), false);
                    path.LineTo(new Point(size * 0.1, size * 0.9));
                    path.EndFigure(false);
                    break;
            }
        }
        frame.DrawShape(shape, null, frame.Pen(brush, 1.6, PenLineCap.Round, PenLineJoin.Round), x, y);
    }
}
