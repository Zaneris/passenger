using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.App.Profiling;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Roslyn.Extension;

namespace Passenger.App.DependencyMap;

/// <summary>The dependency map drawn: each closed node a box with its name, each open one a frame around its level under its
/// name, and an arrow for each edge shown from the node that uses to the node used, the chosen node's edges drawn over the
/// rest. It is drawn once for what it shows and then moved and zoomed as a whole (its layer's transform), so panning and
/// zooming draw nothing again; choosing redraws only the layer of the chosen node's marks. The wheel pans, with the command
/// key it zooms about the pointer, and a drag pans; a click chooses a node, a click on its chevron opens or closes it, and a
/// double click goes to its code, as Enter does on the node chosen.</summary>
public sealed class MapPicture : Control
{
    /// <summary>A closed node's height and its least and most widths.</summary>
    public const double NodeHeight = 28;
    private const double LeastWidth = 72;
    private const double MostWidth = 280;

    /// <summary>How far in from a node's left its name stands, past its chevron's column, as a tree row's.</summary>
    private const double Inset = RowExpander.Column;

    private readonly Layer layer = new() { RenderTransformOrigin = RelativePoint.TopLeft };
    private readonly Drawing drawing;
    private readonly Marks marks;

    private MapGraph? graph;
    private IReadOnlyList<MapEdge> edges = [];
    private IReadOnlyDictionary<MapNode, MapBox> boxes = new Dictionary<MapNode, MapBox>();
    private readonly Dictionary<MapNode, Label> labels = [];
    private Size content;
    private double zoom = 1;
    private Vector offset;
    private Point? pressed;
    private Vector pressedOffset;
    private bool dragged;

    public MapPicture()
    {
        drawing = new Drawing(this);
        marks = new Marks(this);
        layer.Children.Add(drawing);
        layer.Children.Add(marks);
        VisualChildren.Add(layer);
        LogicalChildren.Add(layer);
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => Restyle();
        ResourcesChanged += (_, _) => Restyle();
    }

    /// <summary>A node chosen in the picture.</summary>
    public event EventHandler<MapNode>? Chose;

    /// <summary>A node whose chevron was pressed, to open or close.</summary>
    public event EventHandler<MapNode>? Toggled;

    /// <summary>A node double clicked, whose code to go to.</summary>
    public event EventHandler<MapNode>? Opened;

    /// <summary>The zoom changed.</summary>
    public event EventHandler? Zoomed;

    /// <summary>The node chosen, whose edges are drawn over the rest.</summary>
    public MapNode? Chosen
    {
        get => chosen;
        set
        {
            chosen = value;
            marks.InvalidateVisual();
        }
    }

    private MapNode? chosen;

    /// <summary>The zoom the map is shown at: 1 is a unit a pixel.</summary>
    public double Zoom => zoom;

    /// <summary>Each node shown and its box, in the map's units.</summary>
    public IReadOnlyDictionary<MapNode, MapBox> Boxes => boxes;

    /// <summary>The edges drawn.</summary>
    public IReadOnlyList<MapEdge> Edges => edges;

    /// <summary>The map's size in its own units.</summary>
    public Size Content => content;

    /// <summary>The edges drawn that break the folder's layer rules, in the error's colour.</summary>
    public IReadOnlySet<MapEdge> Broken => broken;

    private IReadOnlySet<MapEdge> broken = new HashSet<MapEdge>();

    /// <summary>Lays the map out again for what it shows and draws it, keeping the zoom and the chosen node where it stood
    /// (else the place shown), however the layout moved it, those of its edges <paramref name="breaking"/> the layer rules
    /// in the error's colour.</summary>
    public void Show(MapGraph shown, IReadOnlyList<MapEdge> drawn, IReadOnlySet<MapEdge>? breaking = null)
    {
        UiThread.Check();
        Point? stood = chosen is { } before && Shown(before) is { } was ? was.TopLeft : null;
        graph = shown;
        edges = drawn;
        broken = breaking ?? new HashSet<MapEdge>();
        Palette palette = Colours();
        foreach (MapNode gone in labels.Keys.Where(n => shown[n.Id] != n).ToList())
        {
            labels.Remove(gone);
        }
        (IReadOnlyDictionary<MapNode, MapBox> laid, double width, double height) = MapLayout.Of(shown.Roots, drawn, node => SizeOf(node, palette), NodeHeight);
        boxes = laid;
        content = new Size(width, height);
        drawing.Width = width;
        drawing.Height = height;
        marks.Width = width;
        marks.Height = height;
        drawing.InvalidateVisual();
        marks.InvalidateVisual();
        if (stood is { } at && chosen is { } kept && boxes.TryGetValue(kept, out MapBox box))
        {
            offset = new Vector(at.X - (box.X * zoom), at.Y - (box.Y * zoom));
        }
        Place();
    }

    /// <summary>A closed node's size: its name's width past its chevron, between the least and the most.</summary>
    private (double Width, double Height) SizeOf(MapNode node, Palette palette) =>
        (Math.Clamp(LabelOf(node, palette).Width + Inset + Gaps.Normal, LeastWidth, MostWidth), NodeHeight);

    private Label LabelOf(MapNode node, Palette palette)
    {
        if (!labels.TryGetValue(node, out Label? label))
        {
            labels[node] = label = new Label(node.Name);
        }
        return label.For(palette);
    }

    public void ZoomIn() => ZoomAbout(ZoomedPicture.StepIn(zoom), Middle);

    public void ZoomOut() => ZoomAbout(ZoomedPicture.StepOut(zoom), Middle);

    public void ActualSize() => ZoomAbout(1, Middle);

    /// <summary>The zoom at which the whole map fits, centred.</summary>
    public void Fit()
    {
        if (content.Width <= 0 || Bounds.Width <= 0)
        {
            return;
        }
        zoom = Math.Min(1, Math.Min(Bounds.Width / content.Width, Bounds.Height / content.Height));
        offset = new Vector((Bounds.Width - (content.Width * zoom)) / 2, (Bounds.Height - (content.Height * zoom)) / 2);
        Place();
        Zoomed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the map so a node stands in view, centred where it was out of it.</summary>
    public void Reveal(MapNode node)
    {
        if (!boxes.TryGetValue(node, out MapBox box) || Bounds.Width <= 0)
        {
            return;
        }
        Rect shown = new(-offset.X / zoom, -offset.Y / zoom, Bounds.Width / zoom, Bounds.Height / zoom);
        if (shown.Contains(new Rect(box.X, box.Y, Math.Min(box.Width, shown.Width), Math.Min(box.Height, shown.Height))))
        {
            return;
        }
        offset = new Vector((Bounds.Width / 2) - ((box.X + (Math.Min(box.Width, Bounds.Width / zoom) / 2)) * zoom),
            (Bounds.Height / 2) - ((box.Y + (Math.Min(box.Height, Bounds.Height / zoom) / 2)) * zoom));
        Place();
    }

    private Point Middle => new(Bounds.Width / 2, Bounds.Height / 2);

    /// <summary>The pane given another size keeps the chosen node in view.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (chosen is { } kept)
        {
            Reveal(kept);
        }
    }

    private void ZoomAbout(double to, Point at)
    {
        double next = Math.Clamp(to, 0.05, ZoomedPicture.Most);
        // The map's point under the pointer stays under it.
        Point on = new((at.X - offset.X) / zoom, (at.Y - offset.Y) / zoom);
        zoom = next;
        offset = new Vector(at.X - (on.X * zoom), at.Y - (on.Y * zoom));
        Place();
        Zoomed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The layer moved and zoomed to where the map stands: its transform alone, drawing nothing again.</summary>
    private void Place() => layer.RenderTransform = new MatrixTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(offset.X, offset.Y));

    protected override Size MeasureOverride(Size availableSize)
    {
        layer.Measure(Size.Infinity);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        layer.Arrange(new Rect(content));
        return finalSize;
    }

    /// <summary>The node at a point of the control: the deepest whose box holds it, a closed node before the frame around it.</summary>
    public MapNode? NodeAt(Point at)
    {
        Point on = new((at.X - offset.X) / zoom, (at.Y - offset.Y) / zoom);
        MapNode? found = null;
        foreach ((MapNode node, MapBox box) in boxes)
        {
            if (box.Contains(on.X, on.Y) && (found is null || found.Holds(node)))
            {
                found = node;
            }
        }
        return found;
    }

    /// <summary>Where a node stands in the control as it is shown now.</summary>
    public Rect? Shown(MapNode node) => boxes.TryGetValue(node, out MapBox box)
        ? new Rect((box.X * zoom) + offset.X, (box.Y * zoom) + offset.Y, box.Width * zoom, box.Height * zoom)
        : null;

    /// <summary>Whether a point of the control is on a node's chevron: the column left of its name, on its title's row.</summary>
    private bool OnChevron(MapNode node, Point at) =>
        node.Expands && Shown(node) is { } box && at.X - box.X < Inset * zoom && at.Y - box.Y < NodeHeight * zoom;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        Point at = e.GetPosition(this);
        pressed = at;
        pressedOffset = offset;
        dragged = false;
        e.Pointer.Capture(this);
        if (e.ClickCount == 2 && NodeAt(at) is { } twice && !OnChevron(twice, at))
        {
            Opened?.Invoke(this, twice);
            pressed = null;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point at = e.GetPosition(this);
        if (pressed is { } from)
        {
            Vector moved = at - from;
            dragged |= Math.Abs(moved.X) + Math.Abs(moved.Y) > 4;
            if (dragged)
            {
                offset = pressedOffset + moved;
                Place();
            }
            return;
        }
        MapNode? over = NodeAt(at);
        if (over != tipped)
        {
            tipped = over;
            ShownWords.Set(this, ToolTip.TipProperty, over is null ? null : TipOf(over));
        }
    }

    private MapNode? tipped;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pressed is not { } at)
        {
            return;
        }
        pressed = null;
        e.Pointer.Capture(null);
        if (dragged || NodeAt(at) is not { } node)
        {
            return;
        }
        if (OnChevron(node, at))
        {
            Toggled?.Invoke(this, node);
        }
        Chose?.Invoke(this, node);
    }

    /// <summary>The keys a tree row takes, on the node chosen: Enter or F4 goes to its code, Right opens it, Left closes it
    /// or, closed, chooses the node it stands in.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || chosen is not { } node)
        {
            return;
        }
        switch (e.Key)
        {
            case ListKeys.OpenKey or ListKeys.SourceKey:
                Opened?.Invoke(this, node);
                break;
            case Key.Right when node.Expands && !node.IsOpen:
            case Key.Left when node.IsOpen:
                Toggled?.Invoke(this, node);
                break;
            case Key.Left when node.Parent is { } above:
                Chose?.Invoke(this, above);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if ((e.KeyModifiers & Keystroke.Command) != 0)
        {
            ZoomAbout(zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        }
        else
        {
            offset += new Vector(e.Delta.X, e.Delta.Y) * 40;
            Place();
        }
        e.Handled = true;
    }

    /// <summary>What a node's tip says: its name and kind, and what it uses and what uses it, counted.</summary>
    private Words TipOf(MapNode node)
    {
        (IReadOnlyList<MapEdge> uses, IReadOnlyList<MapEdge> used) = MapGraph.Crossing(node, edges);
        return MapWords.NodeTip(Words.Verbatim(node.Name), MapWords.NodeKind(node.Kind), uses.Count, used.Count);
    }

    private void Restyle()
    {
        palette = null;
        foreach (Label label in labels.Values)
        {
            label.Forget();
        }
        if (graph is not null)
        {
            Show(graph, edges, broken);
        }
    }

    private Palette? palette;

    private Palette Colours() => palette ??= Palette.From(this);

    /// <summary>What moves and zooms the drawing and its marks together.</summary>
    private sealed class Layer : Panel;

    /// <summary>The theme's brushes and pens the map draws with, and its words' face.</summary>
    private sealed class Palette
    {
        public required IBrush Text { get; init; }
        public required IBrush Muted { get; init; }
        public required IBrush Box { get; init; }
        public required IBrush Frame { get; init; }
        public required Pen Border { get; init; }
        public required Pen Edge { get; init; }
        public required IBrush EdgeHead { get; init; }
        public required Pen BrokenEdge { get; init; }
        public required IBrush BrokenHead { get; init; }
        public required Pen ChosenEdge { get; init; }
        public required Pen ChosenBroken { get; init; }
        public required IBrush ChosenHead { get; init; }
        public required Pen Chosen { get; init; }
        public required double Radius { get; init; }
        public required Typeface Face { get; init; }

        public static Palette From(Control control)
        {
            IBrush muted = Tokens.Brush(control, TokenKeys.TextMuted);
            IBrush link = Tokens.Brush(control, TokenKeys.Link);
            IBrush error = Tokens.Brush(control, TokenKeys.Error);
            double radius = control.TryFindResource(TokenKeys.ControlRadius, control.ActualThemeVariant, out object? found) && found is CornerRadius corner ? corner.TopLeft : 0;
            return new Palette
            {
                Text = Tokens.Brush(control, TokenKeys.Text),
                Muted = muted,
                Box = Tokens.Brush(control, TokenKeys.Field),
                Frame = Tokens.Brush(control, TokenKeys.Panel),
                Border = new Pen(Tokens.Brush(control, TokenKeys.FieldBorder), 1),
                Edge = new Pen(muted, 1),
                EdgeHead = muted,
                BrokenEdge = new Pen(error, 1.5),
                BrokenHead = error,
                ChosenEdge = new Pen(link, 2),
                ChosenBroken = new Pen(error, 2.5),
                ChosenHead = link,
                Chosen = new Pen(Tokens.Brush(control, TokenKeys.Focus), 2),
                Radius = radius,
                Face = new Typeface(TextElement.GetFontFamily(control)),
            };
        }
    }

    /// <summary>A node's name laid out once for the face it is drawn in, as wide as it is.</summary>
    private sealed class Label(string words)
    {
        private TextLayout? text;
        private Palette? laidFor;

        public double Width => text?.WidthIncludingTrailingWhitespace ?? 0;

        public Label For(Palette palette)
        {
            if (laidFor != palette)
            {
                text?.Dispose();
                text = Labels.Of(words, palette.Face, TypeRamp.Dense, palette.Text, null);
                laidFor = palette;
            }
            return this;
        }

        /// <summary>The name drawn from a point, cut to a room where it is wider.</summary>
        public void Draw(DrawingContext context, Point at, double room)
        {
            if (text is null)
            {
                return;
            }
            using (context.PushClip(new Rect(at.X, at.Y, Math.Max(0, room), text.Height)))
            {
                text.Draw(context, at);
            }
        }

        public double Height => text?.Height ?? 0;

        public void Forget()
        {
            text?.Dispose();
            text = null;
            laidFor = null;
        }
    }

    /// <summary>The nodes and edges, drawn once for what the map shows.</summary>
    private sealed class Drawing(MapPicture map) : Control
    {
        public override void Render(DrawingContext context)
        {
            Palette palette = map.Colours();
            // Frames first, the outer under the inner, then the edges, then the closed nodes over them.
            foreach ((MapNode node, MapBox box) in map.boxes.Where(b => b.Key.IsOpen).OrderBy(b => b.Key.Level))
            {
                Rect frame = new(box.X, box.Y, box.Width, box.Height);
                context.DrawRectangle(palette.Frame, palette.Border, frame, palette.Radius, palette.Radius);
                Title(context, palette, node, box);
            }
            foreach (MapEdge edge in map.edges)
            {
                bool broken = map.broken.Contains(edge);
                map.DrawEdge(context, edge, broken ? palette.BrokenEdge : palette.Edge, broken ? palette.BrokenHead : palette.EdgeHead);
            }
            foreach ((MapNode node, MapBox box) in map.boxes.Where(b => !b.Key.IsOpen))
            {
                context.DrawRectangle(palette.Box, palette.Border, new Rect(box.X, box.Y, box.Width, box.Height), palette.Radius, palette.Radius);
                Title(context, palette, node, box);
            }
        }

        /// <summary>A node's chevron and name along its top.</summary>
        private void Title(DrawingContext context, Palette palette, MapNode node, MapBox box)
        {
            Label label = map.LabelOf(node, palette);
            double y = box.Y + ((NodeHeight - label.Height) / 2);
            label.Draw(context, new Point(box.X + Inset, y), box.Width - Inset - Gaps.Small);
            if (node.Expands)
            {
                Point middle = new(box.X + (Inset / 2), box.Y + (NodeHeight / 2));
                StreamGeometry chevron = new();
                using (StreamGeometryContext draw = chevron.Open())
                {
                    if (node.IsOpen)
                    {
                        draw.BeginFigure(middle + new Vector(-4, -2), false);
                        draw.LineTo(middle + new Vector(0, 2));
                        draw.LineTo(middle + new Vector(4, -2));
                    }
                    else
                    {
                        draw.BeginFigure(middle + new Vector(-2, -4), false);
                        draw.LineTo(middle + new Vector(2, 0));
                        draw.LineTo(middle + new Vector(-2, 4));
                    }
                    draw.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(palette.Muted, 1.5), chevron);
            }
        }
    }

    /// <summary>The chosen node's ring and its edges, over the drawing, drawn again as the choice changes.</summary>
    private sealed class Marks(MapPicture map) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (map.chosen is not { } node || !map.boxes.TryGetValue(node, out MapBox box))
            {
                return;
            }
            Palette palette = map.Colours();
            (IReadOnlyList<MapEdge> leaving, IReadOnlyList<MapEdge> coming) = MapGraph.Crossing(node, map.edges);
            foreach (MapEdge edge in leaving.Concat(coming))
            {
                bool broken = map.broken.Contains(edge);
                map.DrawEdge(context, edge, broken ? palette.ChosenBroken : palette.ChosenEdge, broken ? palette.BrokenHead : palette.ChosenHead);
            }
            context.DrawRectangle(null, palette.Chosen, new Rect(box.X - 1, box.Y - 1, box.Width + 2, (node.IsOpen ? box.Height : NodeHeight) + 2), palette.Radius, palette.Radius);
        }
    }

    /// <summary>An edge from the side of its user facing what it uses to that side of the node used, curved, its head at the
    /// node used.</summary>
    private void DrawEdge(DrawingContext context, MapEdge edge, Pen pen, IBrush head)
    {
        if (!boxes.TryGetValue(edge.From, out MapBox from) || !boxes.TryGetValue(edge.To, out MapBox to))
        {
            return;
        }
        // An open node's edges meet its title, where its name stands.
        double fromBottom = edge.From.IsOpen ? from.Y + NodeHeight : from.Bottom;
        double toBottom = edge.To.IsOpen ? to.Y + NodeHeight : to.Bottom;
        Point start;
        Point end;
        Vector bend;
        if (to.Y >= fromBottom)
        {
            start = new Point(from.X + (from.Width / 2), fromBottom);
            end = new Point(to.X + (to.Width / 2), to.Y);
            bend = new Vector(0, Math.Max(16, (end.Y - start.Y) / 2));
        }
        else if (toBottom <= from.Y)
        {
            start = new Point(from.X + (from.Width / 2), from.Y);
            end = new Point(to.X + (to.Width / 2), toBottom);
            bend = new Vector(0, -Math.Max(16, (start.Y - end.Y) / 2));
        }
        else
        {
            bool right = to.X >= from.Right;
            start = new Point(right ? from.Right : from.X, from.Y + (NodeHeight / 2));
            end = new Point(right ? to.X : to.Right, to.Y + (NodeHeight / 2));
            bend = new Vector(right ? Math.Max(16, (end.X - start.X) / 2) : -Math.Max(16, (start.X - end.X) / 2), 0);
        }
        Point first = start + bend;
        Point second = end - bend;
        // A curve that would cross a closed node between bows out past the right of every one it would cross.
        double? past = null;
        foreach ((MapNode node, MapBox box) in boxes)
        {
            if (!node.IsOpen && node != edge.From && node != edge.To && Crosses(box, start, end))
            {
                past = Math.Max(past ?? double.MinValue, box.Right + Gaps.Group);
            }
        }
        if (past is { } clear)
        {
            // A curve reaches three quarters of the way to its bend's points.
            double x = Math.Max(start.X, end.X) + ((clear - Math.Max(start.X, end.X)) / 0.75);
            first = new Point(x, start.Y + bend.Y);
            second = new Point(x, end.Y - bend.Y);
        }
        StreamGeometry curve = new();
        using (StreamGeometryContext draw = curve.Open())
        {
            draw.BeginFigure(start, false);
            draw.CubicBezierTo(first, second, end);
            draw.EndFigure(false);
        }
        context.DrawGeometry(null, pen, curve);
        // The head points along the curve's last stretch.
        Vector back = end - second;
        double length = Math.Max(1, Math.Sqrt((back.X * back.X) + (back.Y * back.Y)));
        Vector along = back / length * 7;
        Vector across = new Vector(-along.Y, along.X) * 0.55;
        StreamGeometry arrow = new();
        using (StreamGeometryContext draw = arrow.Open())
        {
            draw.BeginFigure(end, true);
            draw.LineTo(end - along + across);
            draw.LineTo(end - along - across);
            draw.EndFigure(true);
        }
        context.DrawGeometry(head, null, arrow);
    }

    /// <summary>Whether the straight way between two points passes through a box.</summary>
    private static bool Crosses(MapBox box, Point start, Point end)
    {
        for (int step = 1; step < 16; step++)
        {
            Point at = start + ((end - start) * (step / 16.0));
            if (box.Contains(at.X, at.Y))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>An edge's kinds in words, the most first ("calls 3, uses 1").</summary>
    public static Words KindsOf(MapEdge edge) => edge.Kinds.Count == 0 ? Words.Verbatim(string.Empty) :
        edge.Kinds.OrderByDescending(k => k.Value).ThenBy(k => DependencyKinds.All.ToList().IndexOf(k.Key)).Select(k => MapWords.KindCounted(MapWords.Kind(k.Key), k.Value))
            .Aggregate(MapWords.KindsListed);
}
