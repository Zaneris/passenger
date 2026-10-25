using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>A flame graph of a call tree, drawn as an icicle (the first frame at the top, callees below), each method a
/// bar as wide as its share of the samples, coloured by its namespace. A click zooms into a bar so it fills the width;
/// a click on the top bar, or Escape, steps back out. The pointer's bar is named in a tooltip with its time.</summary>
public sealed class FlameGraph : Control
{
    private const double RowHeight = 20;

    private CallNode? root;
    private CallNode? zoom;
    private readonly List<(Rect Bar, CallNode Node)> bars = [];

    public FlameGraph()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>The tree drawn: its root spans the width.</summary>
    public CallNode? Root
    {
        get => root;
        set
        {
            root = value;
            zoom = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>How long a sample stands for, which the tooltip's times are counted in (<see cref="Snapshot.SampleMs"/>).</summary>
    public double SampleMs { get; set; } = 1;

    /// <summary>The bar zoomed into, which spans the width.</summary>
    public CallNode? Zoomed => zoom;

    /// <summary>Zooms into a node, or back out to the root with null.</summary>
    public void ZoomTo(CallNode? node)
    {
        zoom = node ?? root;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, zoom is null ? 0 : (Ancestors(zoom) + Rows(zoom)) * RowHeight);

    public override void Render(DrawingContext context)
    {
        bars.Clear();
        context.FillRectangle(Brush(TokenKeys.Editor_Background, Brushes.Transparent), new Rect(Bounds.Size));
        if (zoom is null || zoom.Total == 0)
        {
            return;
        }
        // The zoomed bar's callers stay above it as a path back out.
        List<CallNode> path = [];
        for (CallNode? up = zoom.Parent; up is not null; up = up.Parent)
        {
            path.Insert(0, up);
        }
        double y = 0;
        foreach (CallNode caller in path)
        {
            Draw(context, caller, new Rect(0, y, Bounds.Width, RowHeight - 1), dim: true);
            y += RowHeight;
        }
        Layout(context, zoom, 0, Bounds.Width, y);
    }

    private void Layout(DrawingContext context, CallNode node, double x, double width, double y)
    {
        if (width < 1 || y > Bounds.Height)
        {
            return;
        }
        Draw(context, node, new Rect(x, y, width, RowHeight - 1), dim: false);
        double at = x;
        foreach (CallNode child in node.Ordered)
        {
            double share = width * child.Total / node.Total;
            Layout(context, child, at, share, y + RowHeight);
            at += share;
        }
    }

    private void Draw(DrawingContext context, CallNode node, Rect bar, bool dim)
    {
        bars.Add((bar, node));
        IBrush fill = Brush($"Passenger.Graph.{Colour(node)}", Brushes.Transparent);
        using (context.PushOpacity(dim ? 0.3 : 0.55))
        {
            context.FillRectangle(fill, bar.Deflate(new Thickness(0, 0, 1, 0)));
        }
        if (bar.Width < 24)
        {
            return;
        }
        FormattedText label = new(Short(node.Name), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11, Brush(TokenKeys.Text, Brushes.Transparent))
        {
            MaxTextWidth = Math.Max(1, bar.Width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        using (context.PushClip(bar))
        {
            context.DrawText(label, new Point(bar.X + 4, bar.Y + ((RowHeight - 1 - label.Height) / 2)));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (At(e.GetPosition(this)) is { } node)
        {
            // The top bar, or a caller above it, steps out; any other zooms in.
            ZoomTo(node == zoom ? zoom.Parent ?? root : node);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        CallNode? node = At(e.GetPosition(this));
        ToolTip.SetTip(this, node is null ? null : $"{node.Name}\n{node.Total * SampleMs:N0} ms ({Percent(node)}), {node.Self * SampleMs:N0} ms in itself");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && zoom is not null && zoom != root)
        {
            ZoomTo(zoom.Parent);
            e.Handled = true;
        }
    }

    private CallNode? At(Point point)
    {
        for (int i = bars.Count - 1; i >= 0; i--)
        {
            if (bars[i].Bar.Contains(point))
            {
                return bars[i].Node;
            }
        }
        return null;
    }

    private string Percent(CallNode node) => root is { Total: > 0 } top ? $"{100.0 * node.Total / top.Total:0.0}%" : "0%";

    /// <summary>How many rows a node and its callees take.</summary>
    private static int Rows(CallNode node) => 1 + node.Children.Select(Rows).DefaultIfEmpty(0).Max();

    private static int Ancestors(CallNode node)
    {
        int above = 0;
        for (CallNode? up = node.Parent; up is not null; up = up.Parent)
        {
            above++;
        }
        return above;
    }

    /// <summary>A method's name without its parameters and namespace, as a bar has room for.</summary>
    internal static string Short(string name)
    {
        string bare = name.Split('(')[0];
        if (bare.StartsWith('['))
        {
            return bare;
        }
        // A constructor is "Type..ctor": its dot is the first of the two.
        int split = bare.EndsWith("..ctor", StringComparison.Ordinal) || bare.EndsWith("..cctor", StringComparison.Ordinal)
            ? bare.LastIndexOf("..", StringComparison.Ordinal)
            : bare.LastIndexOf('.');
        if (split <= 0)
        {
            return bare;
        }
        string type = bare[..split];
        return $"{type[(type.LastIndexOf('.') + 1)..]}.{bare[(split + 1)..]}";
    }

    /// <summary>A colour per namespace, so a library's frames read as one.</summary>
    private static int Colour(CallNode node)
    {
        string space = node.Name.Split('(')[0];
        int dot = space.IndexOf('.', StringComparison.Ordinal);
        string first = dot > 0 ? space[..dot] : space;
        // A hash of our own: string's is different every run, and a namespace keeps its colour across snapshots.
        uint hash = 2166136261;
        foreach (char c in first)
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)(hash % 6);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? found) && found is IBrush brush ? brush : fallback;
}
