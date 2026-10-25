using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>The run's timeline: from left to right, a row per busy thread shaded where it was sampled (the denser, the
/// darker), the garbage collections marked above them. A drag across it selects a stretch, which the other views then
/// show alone; a click without a drag goes back to the whole run.</summary>
public sealed class Timeline : Control
{
    private const double Label = 170;
    private const double Row = 16;
    private const double CollectionsRow = 12;
    private const double Axis = 16;
    private const int MostThreads = 8;

    private Snapshot? whole;
    private (double From, double To)? chosen;
    private double? dragFrom;

    public Timeline()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>A stretch was chosen (null for the whole run again).</summary>
    public event EventHandler<(double From, double To)?>? Chosen;

    /// <summary>The run drawn.</summary>
    public Snapshot? Snapshot
    {
        get => whole;
        set
        {
            whole = value?.Whole;
            chosen = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>The stretch chosen, in milliseconds from the trace's start, or null for all of it.</summary>
    public (double From, double To)? Selection => chosen;

    /// <summary>Chooses a stretch, as a drag across it does.</summary>
    public void Select(double fromMs, double toMs)
    {
        chosen = (Math.Min(fromMs, toMs), Math.Max(fromMs, toMs));
        InvalidateVisual();
        Chosen?.Invoke(this, chosen);
    }

    public void ClearSelection()
    {
        chosen = null;
        InvalidateVisual();
        Chosen?.Invoke(this, null);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, whole is null ? 0 : CollectionsRow + (Math.Min(MostThreads, whole.Timeline.Count) * Row) + Axis + 4);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush(TokenKeys.Editor_Background, Brushes.Transparent), new Rect(Bounds.Size));
        if (whole is null || whole.EndMs <= whole.StartMs)
        {
            return;
        }
        double width = Math.Max(1, Bounds.Width - Label - 8);
        IBrush text = Brush(TokenKeys.TextMuted, Brushes.Transparent);
        Typeface face = new("Inter");

        // The collections, each as long as it paused the program (at least a hairline).
        IBrush gc = Brush(TokenKeys.Graph_2, Brushes.Transparent);
        Draw(context, "GC", 0, face, text);
        foreach (Collection collection in whole.Collections)
        {
            double x = X(collection.AtMs, width);
            context.FillRectangle(gc, new Rect(x, 2, Math.Max(1.5, collection.PauseMs / (whole.EndMs - whole.StartMs) * width), CollectionsRow - 4));
        }

        IBrush busy = Brush(TokenKeys.Graph_0, Brushes.Transparent);
        double msPerPixel = (whole.EndMs - whole.StartMs) / width;
        IReadOnlyList<double[]> rows = Columns(width);
        double full = Math.Max(1, msPerPixel);
        for (int row = 0; row < rows.Count; row++)
        {
            double y = CollectionsRow + (row * Row);
            Draw(context, Short(whole.Timeline[row].Name), y, face, text);
            double[] counts = rows[row];
            for (int column = 0; column < counts.Length; column++)
            {
                if (counts[column] <= 0)
                {
                    continue;
                }
                // A sample a millisecond: a column whose every millisecond was sampled is solid.
                using (context.PushOpacity(Math.Clamp(0.25 + (0.75 * counts[column] / full), 0.25, 1)))
                {
                    // A sample stands for its millisecond, which may be wider than a pixel.
                    context.FillRectangle(busy, new Rect(Label + column, y + 2, Math.Max(1, 1 / msPerPixel), Row - 4));
                }
            }
        }

        double axisY = CollectionsRow + (Math.Min(MostThreads, whole.Timeline.Count) * Row) + 2;
        double span = whole.EndMs - whole.StartMs;
        double step = Nice(span / 8);
        for (double at = 0; at <= span; at += step)
        {
            double x = Label + (at / span * width);
            context.DrawLine(new Pen(text, 0.5), new Point(x, axisY), new Point(x, axisY + 3));
            FormattedText tick = new(at >= 1000 ? $"{at / 1000:0.#} s" : $"{at:0} ms", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 10, text);
            context.DrawText(tick, new Point(x + 2, axisY + 1));
        }

        if (chosen is { } stretch)
        {
            double from = X(stretch.From, width);
            double to = X(stretch.To, width);
            using (context.PushOpacity(0.25))
            {
                context.FillRectangle(Brush(TokenKeys.Accent, Brushes.Transparent), new Rect(from, 0, Math.Max(1, to - from), axisY));
            }
            context.DrawLine(new Pen(Brush(TokenKeys.Accent, Brushes.Transparent), 1), new Point(from, 0), new Point(from, axisY));
            context.DrawLine(new Pen(Brush(TokenKeys.Accent, Brushes.Transparent), 1), new Point(to, 0), new Point(to, axisY));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (whole is null || e.GetPosition(this).X < Label)
        {
            return;
        }
        dragFrom = Ms(e.GetPosition(this).X);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragFrom is { } from)
        {
            chosen = (Math.Min(from, Ms(e.GetPosition(this).X)), Math.Max(from, Ms(e.GetPosition(this).X)));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragFrom is not { } from)
        {
            return;
        }
        dragFrom = null;
        e.Pointer.Capture(null);
        double to = Ms(e.GetPosition(this).X);
        // A click is no stretch: the whole run again.
        if (Math.Abs(X(to, Math.Max(1, Bounds.Width - Label - 8)) - X(from, Math.Max(1, Bounds.Width - Label - 8))) < 3)
        {
            ClearSelection();
            return;
        }
        Select(from, to);
    }

    /// <summary>The samples in each pixel's column of the busiest threads' rows, a run's spread evenly over the columns it
    /// spans; counted once for the run and the width, not again for each frame a drag draws.</summary>
    private IReadOnlyList<double[]> Columns(double width)
    {
        if (columns is { } cached && ReferenceEquals(cached.Of, whole) && cached.Width == width)
        {
            return cached.Rows;
        }
        Snapshot run = whole!;
        double msPerPixel = (run.EndMs - run.StartMs) / width;
        List<double[]> rows = [];
        foreach (ThreadLine line in run.Timeline.Take(MostThreads))
        {
            double[] row = new double[(int)Math.Ceiling(width) + 1];
            foreach (SampleRun samples in line.Runs)
            {
                int from = Math.Clamp((int)((samples.FromMs - run.StartMs) / msPerPixel), 0, row.Length - 1);
                int to = Math.Clamp((int)((samples.ToMs - run.StartMs) / msPerPixel), 0, row.Length - 1);
                double each = (double)samples.Count / (to - from + 1);
                for (int column = from; column <= to; column++)
                {
                    row[column] += each;
                }
            }
            rows.Add(row);
        }
        columns = (run, width, rows);
        return rows;
    }

    private (Snapshot Of, double Width, IReadOnlyList<double[]> Rows)? columns;

    private double X(double ms, double width) => whole is null ? Label : Label + ((ms - whole.StartMs) / (whole.EndMs - whole.StartMs) * width);

    private double Ms(double x)
    {
        double width = Math.Max(1, Bounds.Width - Label - 8);
        return whole is null ? 0 : whole.StartMs + (Math.Clamp((x - Label) / width, 0, 1) * (whole.EndMs - whole.StartMs));
    }

    private static void Draw(DrawingContext context, string label, double y, Typeface face, IBrush brush)
    {
        FormattedText text = new(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 10, brush) { MaxTextWidth = Label - 10, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(text, new Point(6, y + 1));
    }

    private static string Short(string thread) => thread.StartsWith("Main thread", StringComparison.Ordinal) ? "Main thread" : thread;

    /// <summary>A round step for the axis: 1, 2 or 5 times a power of ten.</summary>
    private static double Nice(double rough)
    {
        double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rough, 0.001))));
        double unit = rough / power;
        return (unit < 1.5 ? 1 : unit < 3.5 ? 2 : unit < 7.5 ? 5 : 10) * power;
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? found) && found is IBrush brush ? brush : fallback;
}
