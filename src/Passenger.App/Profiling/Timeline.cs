using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>The run's timeline: from left to right, a row per busy thread shaded where it was sampled (the denser, the
/// darker), the garbage collections marked above them. A drag across it selects a stretch, which the other views then
/// show alone; a click without a drag goes back to the whole run. The stretch is drawn by a layer of its own above the
/// rows, so a drag draws the stretch alone; the rows' shades and words are worked out once for the run and the width.</summary>
public sealed class Timeline : Control
{
    private const double Label = 170;
    private const double Row = 16;
    private const double CollectionsRow = 12;
    private const double Axis = 16;
    private const int MostThreads = 8;
    private static readonly Typeface Face = new("Inter");

    private readonly Stretch stretch;
    private Snapshot? whole;
    private (double From, double To)? chosen;
    private double? dragFrom;
    private Palette? palette;
    private Drawn? drawn;

    public Timeline()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        stretch = new Stretch(this) { IsHitTestVisible = false };
        VisualChildren.Add(stretch);
        ActualThemeVariantChanged += (_, _) => Restyle();
        ResourcesChanged += (_, _) => Restyle();
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
            stretch.InvalidateVisual();
        }
    }

    /// <summary>The stretch chosen, in milliseconds from the trace's start, or null for all of it.</summary>
    public (double From, double To)? Selection => chosen;

    /// <summary>Chooses a stretch, as a drag across it does.</summary>
    public void Select(double fromMs, double toMs)
    {
        chosen = (Math.Min(fromMs, toMs), Math.Max(fromMs, toMs));
        stretch.InvalidateVisual();
        Chosen?.Invoke(this, chosen);
    }

    public void ClearSelection()
    {
        chosen = null;
        stretch.InvalidateVisual();
        Chosen?.Invoke(this, null);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        stretch.Measure(availableSize);
        return new Size(0, whole is null ? 0 : CollectionsRow + (Math.Min(MostThreads, whole.Timeline.Count) * Row) + Axis + 4);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        stretch.Arrange(new Rect(finalSize));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        Palette colours = palette ??= Palette.From(this);
        context.FillRectangle(colours.Background, new Rect(Bounds.Size));
        if (whole is null || whole.EndMs <= whole.StartMs)
        {
            return;
        }
        double width = RowsWidth();
        Drawn shown = Draw(width, colours);

        // The collections, each as long as it paused the program (at least a hairline).
        shown.Collections.Draw(context, new Point(6, 1));
        foreach (Collection collection in whole.Collections)
        {
            double x = X(collection.AtMs, width);
            context.FillRectangle(colours.Collections, new Rect(x, 2, Math.Max(1.5, collection.PauseMs / (whole.EndMs - whole.StartMs) * width), CollectionsRow - 4));
        }

        double msPerPixel = (whole.EndMs - whole.StartMs) / width;
        for (int row = 0; row < shown.Rows.Length; row++)
        {
            double y = CollectionsRow + (row * Row);
            shown.Names[row].Draw(context, new Point(6, y + 1));
            double[] counts = shown.Rows[row];
            IBrush?[] shades = shown.Shades[row];
            for (int column = 0; column < counts.Length; column++)
            {
                if (counts[column] <= 0)
                {
                    continue;
                }
                // A sample stands for its millisecond, which may be wider than a pixel.
                Rect sampled = new(Label + column, y + 2, Math.Max(1, 1 / msPerPixel), Row - 4);
                if (shades[column] is { } shade)
                {
                    context.FillRectangle(shade, sampled);
                    continue;
                }
                using (context.PushOpacity(Shade(counts[column], msPerPixel)))
                {
                    context.FillRectangle(colours.Busy, sampled);
                }
            }
        }

        double axisY = AxisY();
        double span = whole.EndMs - whole.StartMs;
        for (int tick = 0; tick < shown.Ticks.Length; tick++)
        {
            double x = Label + (shown.Ticks[tick].At / span * width);
            context.DrawLine(colours.Tick, new Point(x, axisY), new Point(x, axisY + 3));
            shown.Ticks[tick].Text.Draw(context, new Point(x + 2, axisY + 1));
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
            stretch.InvalidateVisual();
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
        if (Math.Abs(X(to, RowsWidth()) - X(from, RowsWidth())) < 3)
        {
            ClearSelection();
            return;
        }
        Select(from, to);
    }

    /// <summary>The rows, their shades and the words drawn for the run and a width: the samples in each pixel's column of
    /// the busiest threads' rows, a run's spread evenly over the columns it spans, counted once for the run and the width,
    /// not again for each frame.</summary>
    private Drawn Draw(double width, Palette colours)
    {
        if (drawn is { } kept && ReferenceEquals(kept.Of, whole) && kept.Width == width && ReferenceEquals(kept.Colours, colours))
        {
            return kept;
        }
        Snapshot run = whole!;
        double msPerPixel = (run.EndMs - run.StartMs) / width;
        int threads = Math.Min(MostThreads, run.Timeline.Count);
        double[][] rows = new double[threads][];
        IBrush?[][] shades = new IBrush?[threads][];
        TextLayout[] names = new TextLayout[threads];
        Dictionary<double, IBrush?> faded = [];
        for (int row = 0; row < threads; row++)
        {
            ThreadLine line = run.Timeline[row];
            names[row] = Text(Short(line.Name), colours.Text, Label - 10);
            double[] counts = new double[(int)Math.Ceiling(width) + 1];
            foreach (SampleRun samples in line.Runs)
            {
                int from = Math.Clamp((int)((samples.FromMs - run.StartMs) / msPerPixel), 0, counts.Length - 1);
                int to = Math.Clamp((int)((samples.ToMs - run.StartMs) / msPerPixel), 0, counts.Length - 1);
                double each = (double)samples.Count / (to - from + 1);
                for (int column = from; column <= to; column++)
                {
                    counts[column] += each;
                }
            }
            IBrush?[] shaded = new IBrush?[counts.Length];
            for (int column = 0; column < counts.Length; column++)
            {
                if (counts[column] > 0)
                {
                    double opacity = Shade(counts[column], msPerPixel);
                    if (!faded.TryGetValue(opacity, out IBrush? shade))
                    {
                        faded[opacity] = shade = colours.Faded(opacity);
                    }
                    shaded[column] = shade;
                }
            }
            rows[row] = counts;
            shades[row] = shaded;
        }
        double span = run.EndMs - run.StartMs;
        double step = Nice(span / 8);
        List<(double At, TextLayout Text)> ticks = [];
        for (double at = 0; at <= span; at += step)
        {
            ticks.Add((at, Text(at >= 1000 ? $"{at / 1000:0.#} s" : $"{at:0} ms", colours.Text, null)));
        }
        Drawn made = new(run, width, colours, Text("GC", colours.Text, Label - 10), names, rows, shades, [.. ticks]);
        drawn?.Dispose();
        drawn = made;
        return made;
    }

    /// <summary>A column's shade for its samples: one a millisecond, a column whose every millisecond was sampled, is solid.</summary>
    private static double Shade(double count, double msPerPixel) => Math.Clamp(0.25 + (0.75 * count / Math.Max(1, msPerPixel)), 0.25, 1);

    private static TextLayout Text(string words, IBrush brush, double? room) => Labels.Of(words, Face, 10, brush, room);

    private void Restyle()
    {
        palette = null;
        drawn?.Dispose();
        drawn = null;
        InvalidateVisual();
        stretch.InvalidateVisual();
    }

    private double RowsWidth() => Math.Max(1, Bounds.Width - Label - 8);

    private double AxisY() => CollectionsRow + (Math.Min(MostThreads, whole?.Timeline.Count ?? 0) * Row) + 2;

    private double X(double ms, double width) => whole is null ? Label : Label + ((ms - whole.StartMs) / (whole.EndMs - whole.StartMs) * width);

    private double Ms(double x)
    {
        double width = RowsWidth();
        return whole is null ? 0 : whole.StartMs + (Math.Clamp((x - Label) / width, 0, 1) * (whole.EndMs - whole.StartMs));
    }

    private static string Short(string thread) => thread.StartsWith("Main thread", StringComparison.Ordinal) ? "Main thread" : thread;

    /// <summary>A round step for the axis: 1, 2 or 5 times a power of ten.</summary>
    private static double Nice(double rough)
    {
        double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rough, 0.001))));
        double unit = rough / power;
        return (unit < 1.5 ? 1 : unit < 3.5 ? 2 : unit < 7.5 ? 5 : 10) * power;
    }

    /// <summary>What the rows of a run at a width draw: each row's samples per column and its shade there (null where
    /// the frame's opacity must fade it), the rows' names, the axis's ticks.</summary>
    private sealed record Drawn(Snapshot Of, double Width, Palette Colours, TextLayout Collections, TextLayout[] Names, double[][] Rows,
        IBrush?[][] Shades, (double At, TextLayout Text)[] Ticks) : IDisposable
    {
        public void Dispose()
        {
            Collections.Dispose();
            foreach (TextLayout name in Names)
            {
                name.Dispose();
            }
            foreach ((double _, TextLayout text) in Ticks)
            {
                text.Dispose();
            }
        }
    }

    /// <summary>The theme's brushes and pens.</summary>
    private sealed record Palette(IBrush Background, IBrush Text, IBrush Collections, IBrush Busy, IBrush Accent, IPen Tick, IPen Edge)
    {
        public static Palette From(Control control)
        {
            IBrush text = Brush(TokenKeys.TextMuted);
            IBrush accent = Brush(TokenKeys.Accent);
            return new Palette(Brush(TokenKeys.Editor_Background), text, Brush(TokenKeys.Graph_2), Brush(TokenKeys.Graph_0), accent, new Pen(text, 0.5),
                new Pen(accent, 1));

            IBrush Brush(string key) =>
                control.TryFindResource(key, control.ActualThemeVariant, out object? found) && found is IBrush brush ? brush : Brushes.Transparent;
        }

        /// <summary>The busy colour faded to an opacity, or null where its brush is no solid colour to carry it.</summary>
        public IBrush? Faded(double opacity) => Busy is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * opacity) : null;
    }

    /// <summary>The stretch chosen, drawn over the rows as a layer of its own.</summary>
    private sealed class Stretch(Timeline timeline) : Control
    {
        public override void Render(DrawingContext context)
        {
            // What the layer draws spans the timeline, so a change of the stretch draws the rows below it again whole.
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            if (timeline.whole is null || timeline.whole.EndMs <= timeline.whole.StartMs || timeline.chosen is not { } chosen)
            {
                return;
            }
            Palette colours = timeline.palette ??= Palette.From(timeline);
            double width = timeline.RowsWidth();
            double from = timeline.X(chosen.From, width);
            double to = timeline.X(chosen.To, width);
            double axisY = timeline.AxisY();
            using (context.PushOpacity(0.25))
            {
                context.FillRectangle(colours.Accent, new Rect(from, 0, Math.Max(1, to - from), axisY));
            }
            context.DrawLine(colours.Edge, new Point(from, 0), new Point(from, axisY));
            context.DrawLine(colours.Edge, new Point(to, 0), new Point(to, axisY));
        }
    }
}
