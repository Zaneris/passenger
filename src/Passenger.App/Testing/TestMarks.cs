using Avalonia;
using Avalonia.Media;
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

/// <summary>A test's run icon as the gutter draws it.</summary>
public static class TestMarks
{
    /// <summary>The run icon from a column's left edge, in its last result's colour; a dot while it runs.</summary>
    public static void Draw(GutterFrame frame, TestMarkKind kind, double left, double top)
    {
        DrawingContext context = frame.Context;
        IBrush brush = frame.Brush(kind switch
        {
            TestMarkKind.Passed => TokenKeys.Test_Passed,
            TestMarkKind.Failed => TokenKeys.Editor_Error,
            TestMarkKind.Running => TokenKeys.Editor_ExecutionPoint,
            _ => TokenKeys.Run,
        });
        double size = Math.Min(frame.LineHeight - 8, 11);
        double x = left + 6;
        double y = top + ((frame.LineHeight - size) / 2);
        if (kind == TestMarkKind.Running)
        {
            context.DrawEllipse(brush, null, new Point(x + (size / 2), y + (size / 2)), size / 2.6, size / 2.6);
            return;
        }
        StreamGeometry triangle = new();
        using (StreamGeometryContext path = triangle.Open())
        {
            path.BeginFigure(new Point(x, y), true);
            path.LineTo(new Point(x + (size * 0.9), y + (size / 2)));
            path.LineTo(new Point(x, y + size));
            path.EndFigure(true);
        }
        context.DrawGeometry(brush, null, triangle);
        if (kind == TestMarkKind.Failed)
        {
            // A failed test: the run icon with a red mark on it.
            context.DrawEllipse(frame.Brush(TokenKeys.Editor_Error), new Pen(frame.Brush(TokenKeys.Editor_Gutter), 1.2), new Point(x + size, y + size - 1), 2.6, 2.6);
        }
    }
}
