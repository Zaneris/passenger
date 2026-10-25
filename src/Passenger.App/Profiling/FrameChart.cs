using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>An Avalonia app's frames as bars, one a frame in order, each as tall as its time: measuring, arranging and
/// recording what to render on the UI thread, the rest of its UI work, then the compositor drawing it; a line at a 60 Hz
/// frame's 16.7 ms. The scale is the frames' usual (a first frame many times longer is cut short, marked at its top), so
/// one slow start leaves the rest readable. A click chooses a frame, which the Frames page then shows alone; a click on it
/// again, all of them.</summary>
public sealed class FrameChart : Control
{
    private const double Bar = 7;
    private const double Gap = 2;
    private const double Axis = 34;
    private const double Budget = 1000.0 / 60;
    private const double Tall = 64;

    private IReadOnlyList<UiFrame> frames = [];
    private int? chosen;

    public FrameChart()
    {
        Height = Tall;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>A frame was chosen (by its index), or none.</summary>
    public event EventHandler<int?>? Chosen;

    /// <summary>The frames drawn, in order.</summary>
    public IReadOnlyList<UiFrame> Frames
    {
        get => frames;
        set
        {
            frames = value;
            chosen = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>The frame chosen, by its index, or null for all.</summary>
    public int? Selection => chosen;

    /// <summary>Chooses a frame, as a click on it does (null for all again).</summary>
    public void Select(int? index)
    {
        chosen = index is int at && at >= 0 && at < frames.Count ? at : null;
        InvalidateVisual();
        Chosen?.Invoke(this, chosen);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Axis + (frames.Count * (Bar + Gap)) + 8, Tall);

    /// <summary>A frame's height in milliseconds: its UI thread's work and the compositor's after.</summary>
    private static double Total(UiFrame frame) => Math.Max(frame.DurationMs, frame.LayoutMs + frame.RecordMs) + frame.CompositeMs;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush(TokenKeys.Editor_Background, Brushes.Transparent), new Rect(Bounds.Size));
        if (frames.Count == 0)
        {
            return;
        }
        double top = 6;
        double bottom = Bounds.Height - 4;
        double[] totals = [.. frames.Select(Total).Order()];
        double span = Math.Max(Budget * 2, Math.Min(totals[^1], UiFrames.Percentile(totals, 0.9) * 1.5));
        double scale = (bottom - top) / span;
        IBrush muted = Brush(TokenKeys.TextMuted, Brushes.Transparent);
        Typeface face = new("Inter");

        double budget = bottom - (Budget * scale);
        context.DrawLine(new Pen(muted, 0.5, new DashStyle([3, 3], 0)), new Point(Axis - 4, budget), new Point(Bounds.Width, budget));
        Label(context, "16.7 ms", budget, face, muted);
        Label(context, span >= 1000 ? $"{span / 1000:0.0} s" : $"{span:0} ms", top, face, muted);

        IBrush measure = Brush(TokenKeys.Graph_0, Brushes.Transparent);
        IBrush arrange = Brush(TokenKeys.Graph_1, Brushes.Transparent);
        IBrush record = Brush(TokenKeys.Graph_2, Brushes.Transparent);
        IBrush composite = Brush(TokenKeys.Graph_3, Brushes.Transparent);
        for (int i = 0; i < frames.Count; i++)
        {
            UiFrame frame = frames[i];
            double x = Axis + (i * (Bar + Gap));
            double y = bottom;
            Stack(measure, frame.MeasureMs, 1);
            Stack(arrange, frame.ArrangeMs, 1);
            Stack(record, frame.RecordMs, 1);
            Stack(muted, Math.Max(0, frame.DurationMs - frame.LayoutMs - frame.RecordMs), 0.35);
            Stack(composite, frame.CompositeMs, 1);
            if (Total(frame) > span)
            {
                // Cut short: its top is the scale's, marked.
                context.FillRectangle(Brush(TokenKeys.Graph_4, Brushes.Transparent), new Rect(x, top - 3, Bar, 2));
            }
            if (chosen == i)
            {
                context.DrawRectangle(null, new Pen(Brush(TokenKeys.Accent, Brushes.Transparent), 1.5), new Rect(x - 1.5, top - 5, Bar + 3, bottom - top + 7));
            }

            void Stack(IBrush brush, double ms, double opacity)
            {
                double height = Math.Min(ms * scale, y - top);
                if (height <= 0)
                {
                    return;
                }
                // Even a sliver shows, so a quick frame is still a bar.
                height = Math.Max(height, 1);
                using (context.PushOpacity(opacity))
                {
                    context.FillRectangle(brush, new Rect(x, y - height, Bar, height));
                }
                y -= height;
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        int at = (int)Math.Floor((e.GetPosition(this).X - Axis) / (Bar + Gap));
        if (at < 0 || at >= frames.Count)
        {
            return;
        }
        Select(chosen == at ? null : at);
        e.Handled = true;
    }

    private static void Label(DrawingContext context, string text, double y, Typeface face, IBrush brush)
    {
        FormattedText label = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 9, brush);
        context.DrawText(label, new Point(Math.Max(0, Axis - 6 - label.Width), y - (label.Height / 2)));
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? found) && found is IBrush brush ? brush : fallback;
}
