using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>A flame graph of a call tree, drawn as an icicle (the first frame at the top, callees below), each method a
/// bar as wide as its share of the samples, coloured by its namespace. A click zooms into a bar so it fills the width;
/// a click on the top bar, or Escape, steps back out. The pointer's bar is named in a tooltip with its time. A bar's
/// colour and label are worked out once for its node, its text laid out again only when its width changes, so a frame
/// draws each bar as its rectangle and its label's one glyph run alone.</summary>
public sealed class FlameGraph : Control
{
    private const double RowHeight = 20;
    private const double Dim = 0.3;
    private const double Lit = 0.55;
    private static readonly Typeface Face = new("Inter");
    private static readonly string[] Colours = [TokenKeys.Graph_0, TokenKeys.Graph_1, TokenKeys.Graph_2, TokenKeys.Graph_3, TokenKeys.Graph_4, TokenKeys.Graph_5];

    /// <summary>What the tooltip was last set for when its words no longer stand: no bar is it.</summary>
    private static readonly CallNode Stale = new(string.Empty, string.Empty, null);

    private CallNode? root;
    private CallNode? zoom;
    private CallNode? tipped;
    private readonly List<(Rect Bar, CallNode Node)> bars = [];
    private readonly List<CallNode> callers = [];
    private readonly Dictionary<CallNode, BarLook> looks = [];
    private Palette? palette;

    public FlameGraph()
    {
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => Restyle();
        ResourcesChanged += (_, _) => Restyle();
    }

    /// <summary>The tree drawn: its root spans the width.</summary>
    public CallNode? Root
    {
        get => root;
        set
        {
            root = value;
            zoom = value;
            tipped = Stale;
            foreach (BarLook look in looks.Values)
            {
                look.Forget();
            }
            looks.Clear();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>How long a sample stands for, which the tooltip's times are counted in (<see cref="Snapshot.SampleMs"/>).</summary>
    public double SampleMs
    {
        get => sampleMs;
        set
        {
            sampleMs = value;
            tipped = Stale;
        }
    }

    private double sampleMs = 1;

    /// <summary>The bar zoomed into, which spans the width.</summary>
    public CallNode? Zoomed => zoom;

    /// <summary>How many bars the last frame drew.</summary>
    public int BarsDrawn => bars.Count;

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
        Palette colours = palette ??= Palette.From(this);
        context.FillRectangle(colours.Background, new Rect(Bounds.Size));
        if (zoom is null || zoom.Total == 0)
        {
            return;
        }
        // The zoomed bar's callers stay above it as a path back out.
        callers.Clear();
        for (CallNode? up = zoom.Parent; up is not null; up = up.Parent)
        {
            callers.Add(up);
        }
        double y = 0;
        for (int i = callers.Count - 1; i >= 0; i--)
        {
            Draw(context, colours, callers[i], new Rect(0, y, Bounds.Width, RowHeight - 1), dim: true);
            y += RowHeight;
        }
        Layout(context, colours, zoom, 0, Bounds.Width, y);
    }

    private void Layout(DrawingContext context, Palette colours, CallNode node, double x, double width, double y)
    {
        if (width < 1 || y > Bounds.Height)
        {
            return;
        }
        Draw(context, colours, node, new Rect(x, y, width, RowHeight - 1), dim: false);
        double at = x;
        IReadOnlyList<CallNode> callees = node.Ordered;
        for (int i = 0; i < callees.Count; i++)
        {
            CallNode child = callees[i];
            double share = width * child.Total / node.Total;
            Layout(context, colours, child, at, share, y + RowHeight);
            at += share;
        }
    }

    private void Draw(DrawingContext context, Palette colours, CallNode node, Rect bar, bool dim)
    {
        bars.Add((bar, node));
        if (!looks.TryGetValue(node, out BarLook? look))
        {
            looks[node] = look = new BarLook(Colour(node.Name), Short(node.Name));
        }
        Rect fill = bar.Deflate(new Thickness(0, 0, 1, 0));
        if (colours.Fill(look.Colour, dim) is { } faded)
        {
            context.FillRectangle(faded, fill);
        }
        else
        {
            using (context.PushOpacity(dim ? Dim : Lit))
            {
                context.FillRectangle(colours.Graph[look.Colour], fill);
            }
        }
        if (bar.Width < 24)
        {
            return;
        }
        double room = Math.Max(1, bar.Width - 8);
        TextLayout text = look.Text is { } laid && look.Room == room ? laid : look.LayOut(colours.Text, room);
        // Trimmed to its room, the label stays inside the bar's 4 pixels each side without a clip.
        Point at = new(bar.X + 4, bar.Y + ((RowHeight - 1 - text.Height) / 2));
        if (look.Run is { } run)
        {
            using (context.PushTransform(Matrix.CreateTranslation(at.X, at.Y)))
            {
                context.DrawGlyphRun(colours.Text, run);
            }
        }
        else
        {
            text.Draw(context, at);
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
        if (node == tipped)
        {
            return;
        }
        tipped = node;
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

    private void Restyle()
    {
        palette = null;
        foreach (BarLook look in looks.Values)
        {
            look.Forget();
        }
        InvalidateVisual();
    }

    private string Percent(CallNode node) => root is { Total: > 0 } top ? $"{100.0 * node.Total / top.Total:0.0}%" : "0%";

    /// <summary>How many rows a node and its callees take.</summary>
    private static int Rows(CallNode node)
    {
        int deepest = 0;
        IReadOnlyList<CallNode> callees = node.Ordered;
        for (int i = 0; i < callees.Count; i++)
        {
            deepest = Math.Max(deepest, Rows(callees[i]));
        }
        return 1 + deepest;
    }

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
        ReadOnlySpan<char> bare = Bare(name);
        if (bare.StartsWith('['))
        {
            return bare.Length == name.Length ? name : bare.ToString();
        }
        // A constructor is "Type..ctor": its dot is the first of the two.
        int split = bare.EndsWith("..ctor", StringComparison.Ordinal) || bare.EndsWith("..cctor", StringComparison.Ordinal)
            ? bare.LastIndexOf("..", StringComparison.Ordinal)
            : bare.LastIndexOf('.');
        if (split <= 0)
        {
            return bare.Length == name.Length ? name : bare.ToString();
        }
        ReadOnlySpan<char> type = bare[..split];
        return string.Concat(type[(type.LastIndexOf('.') + 1)..], ".", bare[(split + 1)..]);
    }

    /// <summary>A colour per namespace, so a library's frames read as one.</summary>
    private static int Colour(string name)
    {
        ReadOnlySpan<char> space = Bare(name);
        int dot = space.IndexOf('.');
        ReadOnlySpan<char> first = dot > 0 ? space[..dot] : space;
        // A hash of our own: string's is different every run, and a namespace keeps its colour across snapshots.
        uint hash = 2166136261;
        foreach (char c in first)
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)(hash % (uint)Colours.Length);
    }

    /// <summary>A method's name up to its parameters.</summary>
    private static ReadOnlySpan<char> Bare(string name) => name.IndexOf('(') is var open and >= 0 ? name.AsSpan(0, open) : name;

    /// <summary>A node's bar as worked out once: its colour, its label, and the label laid out for the room it last had,
    /// with its glyphs as one run where they can be (<see cref="Labels.Run"/>).</summary>
    private sealed class BarLook(int colour, string label)
    {
        public int Colour { get; } = colour;
        public string Label { get; } = label;
        public TextLayout? Text { get; private set; }
        public GlyphRun? Run { get; private set; }
        public double Room { get; private set; }

        public TextLayout LayOut(IBrush brush, double room)
        {
            Forget();
            TextLayout text = Labels.Of(Label, Face, 11, brush, room);
            Text = text;
            Run = Labels.Run(text);
            Room = room;
            return text;
        }

        public void Forget()
        {
            Run?.Dispose();
            Text?.Dispose();
            Run = null;
            Text = null;
        }
    }

    /// <summary>The theme's brushes a frame draws with, each graph colour also faded as a bar is, where its brush is a
    /// solid colour that can carry the fade itself.</summary>
    private sealed class Palette(IBrush background, IBrush text, IBrush[] graph)
    {
        private readonly IBrush?[] faded = [.. graph.SelectMany(g => (IBrush?[])[Faded(g, Lit), Faded(g, Dim)])];

        public IBrush Background { get; } = background;
        public IBrush Text { get; } = text;
        public IBrush[] Graph { get; } = graph;

        /// <summary>A graph colour faded for a bar, or null where it must be faded by the frame's opacity.</summary>
        public IBrush? Fill(int colour, bool dim) => faded[(colour * 2) + (dim ? 1 : 0)];

        public static Palette From(Control control) =>
            new(Brush(control, TokenKeys.Editor_Background), Brush(control, TokenKeys.Text), [.. Colours.Select(key => Brush(control, key))]);

        private static IBrush? Faded(IBrush brush, double opacity) =>
            brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * opacity) : null;

        private static IBrush Brush(Control control, string key) =>
            control.TryFindResource(key, control.ActualThemeVariant, out object? found) && found is IBrush brush ? brush : Brushes.Transparent;
    }
}
