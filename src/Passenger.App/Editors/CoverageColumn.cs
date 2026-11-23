using System.ComponentModel;
using Avalonia;
using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>A line's coverage mark's shape beside its colour: a whole bar where it ran with every branch taken, a bar half
/// filled where some of its branches were never taken, a hollow bar where it never ran.</summary>
public enum CoverageShape
{
    Bar,
    Half,
    Hollow,
}

/// <summary>The gutter's column of the coverage measured, nearest the text: a bar beside each line a run with coverage
/// measured, in the colour of a test that passed where it ran whole, a warning's where it ran with branches never taken,
/// a failure's where it never ran, each a shape of its own too (<see cref="ShapeOf"/>). It takes no room in a file with
/// none, and draws from the file's marks for the lines on screen alone.</summary>
public sealed class CoverageColumn : IGutterColumn
{
    /// <summary>Its strip, between the change bars and the pinned errors.</summary>
    public const double StripWidth = 7;

    private const double BarWidth = 4;

    private readonly FollowedDocument file;

    public CoverageColumn() => file = FollowedDocument.OfProperties(OnFileChanged);

    public event EventHandler? Changed;

    /// <summary>The marks of the file shown, none before a run with coverage or once it is cleared.</summary>
    public CoveredLines? Lines => file.Document?.Coverage;

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
        if (e.PropertyName == nameof(OpenDocument.Coverage))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public double Width(GutterFont font) => Lines is { Count: > 0 } ? StripWidth : 0;

    public void Draw(GutterFrame frame)
    {
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
    public static void DrawMark(GutterFrame frame, CoverageState state, double left, int line)
    {
        double top = frame.TopOf(line);
        Rect bar = new(left + ((StripWidth - BarWidth) / 2), top + 1, BarWidth, Math.Max(2, frame.BottomOf(line) - top - 2));
        switch (ShapeOf(state))
        {
            case CoverageShape.Bar:
                frame.Context.FillRectangle(frame.Brush(BrushOf(state)), bar);
                break;
            case CoverageShape.Half:
                frame.Context.FillRectangle(frame.Brush(BrushOf(state)), bar.WithHeight(bar.Height / 2));
                frame.Context.DrawRectangle(frame.Pen(BrushOf(state), 1), bar.Deflate(0.5));
                break;
            default:
                frame.Context.DrawRectangle(frame.Pen(BrushOf(state), 1), bar.Deflate(0.5));
                break;
        }
    }

    public GutterAnswer Press(GutterPress press) => GutterAnswer.Passed;
}
