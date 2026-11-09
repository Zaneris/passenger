using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>An edge of the AppHost's graph: a resource that waits for or references another, by their names as the AppHost
/// gives them, and the kind (WaitFor, Reference).</summary>
public sealed record AspireEdge(string From, string To, string Type);

/// <summary>The AppHost as a whole: its resources as nodes in columns by their waits and references (each right of all it
/// waits for or references), an arrow for each wait and a dashed line for each reference, from the snapshot's relationships
/// alone; each node a button choosing its resource's row. Laid out again only as the resources or their relationships
/// change.</summary>
public sealed class AspireGraph : Canvas
{
    private const double NodeWidth = 170;

    /// <summary>A node's height, a button's (Passenger.ControlHeight).</summary>
    private const double NodeHeight = 28;
    private const double ColumnGap = 70;
    private const double RowGap = 14;
    private const double Inset = 16;

    private readonly Action<string> choose;
    private readonly Dictionary<string, Button> nodes = new(StringComparer.Ordinal);
    private readonly Lines lines;
    private string? shown;

    public AspireGraph(Action<string> choose)
    {
        this.choose = choose;
        lines = new Lines(this);
        ClipToBounds = true;
    }

    /// <summary>Its edges as laid out, each between two resources it shows.</summary>
    public IReadOnlyList<AspireEdge> Edges { get; private set; } = [];

    /// <summary>Its nodes' buttons, by their resources' names.</summary>
    public IReadOnlyDictionary<string, Button> Nodes => nodes;

    /// <summary>Lays the resources out, unless their names and relationships are those shown already.</summary>
    public void Show(IReadOnlyList<AspireResource> resources)
    {
        UiThread.Check();
        AspireResource[] listed = [.. resources.Where(r => !r.IsHidden)];
        List<AspireEdge> edges = [];
        foreach (AspireResource resource in listed)
        {
            foreach ((string name, string type) in AspireDetailsPane.Relations(resource))
            {
                if (type is "WaitFor" or "Reference" && listed.FirstOrDefault(r => r.IsNamed(name) && r.Name != resource.Name) is { } other)
                {
                    edges.Add(new AspireEdge(resource.Name, other.Name, type));
                }
            }
        }
        edges = [.. edges.Distinct()];
        string key = string.Join('\n', listed.Select(r => $"{r.Name}\t{r.DisplayName}")) + "\n\n" + string.Join('\n', edges.Select(e => $"{e.From}\t{e.To}\t{e.Type}"));
        if (key == shown)
        {
            return;
        }
        shown = key;
        Edges = edges;
        Lay(listed, edges);
    }

    /// <summary>Each resource's column, one past the deepest of those it waits for or references (a loop stopping where it
    /// comes round), its row its place in the column in the AppHost's order.</summary>
    private void Lay(AspireResource[] listed, List<AspireEdge> edges)
    {
        Children.Clear();
        nodes.Clear();
        Children.Add(lines);
        Dictionary<string, int> columns = new(StringComparer.Ordinal);
        foreach (AspireResource resource in listed)
        {
            Column(resource.Name, []);
        }
        Dictionary<int, int> rows = [];
        foreach (AspireResource resource in listed)
        {
            int column = columns[resource.Name];
            int row = rows.GetValueOrDefault(column);
            rows[column] = row + 1;
            Button node = new() { Content = resource.DisplayName, Width = NodeWidth, Height = NodeHeight, Name = "aspireGraphNode" };
            AutomationProperties.SetName(node, resource.DisplayName);
            ToolTip.SetTip(node, $"{resource.DisplayName}: {resource.State ?? "unknown state"}. Choose its row");
            string name = resource.Name;
            node.Click += (_, _) => choose(name);
            SetLeft(node, Inset + (column * (NodeWidth + ColumnGap)));
            SetTop(node, Inset + (row * (NodeHeight + RowGap)));
            Children.Add(node);
            nodes[resource.Name] = node;
        }
        Width = Inset * 2 + (rows.Count == 0 ? 0 : ((rows.Keys.Max() + 1) * (NodeWidth + ColumnGap)) - ColumnGap);
        // Room below the rows for the edges that dip under the columns they pass.
        Height = (Inset * 3) + ((rows.Count == 0 ? 0 : rows.Values.Max()) * (NodeHeight + RowGap));
        lines.Width = Width;
        lines.Height = Height;
        lines.InvalidateVisual();

        int Column(string name, HashSet<string> under)
        {
            if (columns.TryGetValue(name, out int known))
            {
                return known;
            }
            if (!under.Add(name))
            {
                return 0;
            }
            int deepest = edges.Where(e => e.From == name).Select(e => Column(e.To, under) + 1).DefaultIfEmpty(0).Max();
            under.Remove(name);
            columns[name] = deepest;
            return deepest;
        }
    }

    /// <summary>The edges under the nodes: from a resource's left side to the right side of the one it waits for (an arrow)
    /// or references (dashed), in the theme's link and muted colours, one past a column between dipping below the rows.</summary>
    private sealed class Lines(AspireGraph graph) : Control
    {
        public override void Render(DrawingContext context)
        {
            IBrush? waits = Tokens.Find(this, TokenKeys.Link);
            IBrush? references = Tokens.Find(this, TokenKeys.TextMuted);
            foreach (AspireEdge edge in graph.Edges)
            {
                if (!graph.nodes.TryGetValue(edge.From, out Button? from) || !graph.nodes.TryGetValue(edge.To, out Button? to))
                {
                    continue;
                }
                bool wait = edge.Type == "WaitFor";
                Pen pen = new(wait ? waits : references, 1.5, wait ? null : new DashStyle([4, 3], 0));
                Point start = new(GetLeft(from), GetTop(from) + (NodeHeight / 2) + (wait ? -3 : 3));
                Point end = new(GetLeft(to) + NodeWidth, GetTop(to) + (NodeHeight / 2) + (wait ? -3 : 3));
                // An edge past a column between dips below the rows, so the nodes it passes never hide it.
                bool skips = start.X - end.X > ColumnGap + (NodeWidth / 2);
                double half = (start.X - end.X) / 2;
                double below = graph.Bounds.Height - (Inset / 2);
                Point first = skips ? new Point(start.X - half / 3, below) : new Point(start.X - half, start.Y);
                Point second = skips ? new Point(end.X + half / 3, below) : new Point(end.X + half, end.Y);
                StreamGeometry curve = new();
                using (StreamGeometryContext draw = curve.Open())
                {
                    draw.BeginFigure(start, false);
                    draw.CubicBezierTo(first, second, end);
                    draw.EndFigure(false);
                }
                context.DrawGeometry(null, pen, curve);
                if (wait)
                {
                    Vector back = second - end;
                    double length = Math.Max(1, Math.Sqrt((back.X * back.X) + (back.Y * back.Y)));
                    Vector along = back / length * 7;
                    Vector across = new Vector(-along.Y, along.X) * 0.6;
                    context.DrawLine(pen, end, end + along + across);
                    context.DrawLine(pen, end, end + along - across);
                }
            }
        }
    }
}
