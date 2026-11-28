namespace Passenger.App.DependencyMap;

/// <summary>Where a node of the map stands, in the map's own units from its top left: a closed node's box, or an open one's
/// frame around the nodes below it.</summary>
public readonly record struct MapBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>The map laid out, nodes in layers: within the projects, and within each open node's frame, a node stands a layer
/// below every node of its level that uses it (what nothing uses at the top, a loop broken where it comes round), its place
/// in its layer near those it is joined to, and a layer wider than the level's share wrapped into rows. An open node's frame
/// holds its level laid out the same way under its name. Pure: the window hands in each closed node's size.</summary>
public static class MapLayout
{
    /// <summary>The room between two nodes of a row, and between a layer's rows.</summary>
    public const double Gap = Gaps.Group;

    /// <summary>The room between two layers, where the edges between them run.</summary>
    public const double LayerGap = Gaps.Screen;

    /// <summary>The room inside an open node's frame around its level.</summary>
    public const double Pad = Gaps.Wide;

    /// <summary>The room around the whole map.</summary>
    public const double Margin = Gaps.Page;

    /// <summary>Each shown node's box, and the whole map's size.</summary>
    /// <param name="roots">The projects.</param>
    /// <param name="edges">The edges shown, which order the layers.</param>
    /// <param name="closed">A closed node's size; an open one's title's width as its least.</param>
    /// <param name="title">How tall an open node's name stands above its level.</param>
    public static (IReadOnlyDictionary<MapNode, MapBox> Boxes, double Width, double Height) Of(IReadOnlyList<MapNode> roots, IReadOnlyList<MapEdge> edges,
        Func<MapNode, (double Width, double Height)> closed, double title)
    {
        Dictionary<MapNode, (double X, double Y)> within = [];
        Dictionary<MapNode, (double Width, double Height)> sizes = [];
        (double width, double height) = Lay(roots, null);
        Dictionary<MapNode, MapBox> boxes = [];
        Place(roots, Margin, Margin);
        return (boxes, width + (2 * Margin), height + (2 * Margin));

        // A level's nodes laid out from its top left: each one's place in it, and its size.
        (double Width, double Height) Lay(IReadOnlyList<MapNode> level, MapNode? owner)
        {
            if (level.Count == 0)
            {
                return (0, 0);
            }
            foreach (MapNode node in level)
            {
                (double w, double h) = closed(node);
                if (node.IsOpen && node.Children is { Count: > 0 } below)
                {
                    (double inner, double tall) = Lay(below, node);
                    sizes[node] = (Math.Max(w, inner + (2 * Pad)), title + tall + (2 * Pad));
                }
                else
                {
                    sizes[node] = (w, h);
                }
            }
            List<List<MapNode>> layers = Layers(level, owner, edges);
            double area = level.Sum(n => sizes[n].Width * sizes[n].Height);
            double room = Math.Max(level.Max(n => sizes[n].Width), Math.Sqrt(area * 3));
            List<(List<MapNode> Row, double Width, double Height, bool LastOfLayer)> rows = [];
            foreach (List<MapNode> layer in layers)
            {
                List<MapNode> row = [];
                double rowWidth = 0;
                foreach (MapNode node in layer)
                {
                    double wide = sizes[node].Width;
                    if (row.Count > 0 && rowWidth + Gap + wide > room)
                    {
                        rows.Add((row, rowWidth, row.Max(n => sizes[n].Height), false));
                        row = [];
                        rowWidth = 0;
                    }
                    rowWidth += (row.Count > 0 ? Gap : 0) + wide;
                    row.Add(node);
                }
                rows.Add((row, rowWidth, row.Max(n => sizes[n].Height), true));
            }
            double width = rows.Max(r => r.Width);
            double y = 0;
            foreach ((List<MapNode> row, double rowWidth, double rowHeight, bool last) in rows)
            {
                double x = (width - rowWidth) / 2;
                foreach (MapNode node in row)
                {
                    within[node] = (x, y + ((rowHeight - sizes[node].Height) / 2));
                    x += sizes[node].Width + Gap;
                }
                y += rowHeight + (last ? LayerGap : Gap);
            }
            return (width, y - LayerGap);
        }

        void Place(IReadOnlyList<MapNode> level, double left, double top)
        {
            foreach (MapNode node in level)
            {
                (double x, double y) = within[node];
                (double w, double h) = sizes[node];
                boxes[node] = new MapBox(left + x, top + y, w, h);
                if (node.IsOpen && node.Children is { Count: > 0 } below)
                {
                    Place(below, left + x + Pad, top + y + title + Pad);
                }
            }
        }
    }

    /// <summary>A level's nodes in layers: each a layer below the deepest of its level that uses it, by the edges between what
    /// stands in them, an edge closing a loop left out; in each layer those used by the layer above nearer where their
    /// users stand, else in the level's order.</summary>
    public static List<List<MapNode>> Layers(IReadOnlyList<MapNode> level, MapNode? owner, IReadOnlyList<MapEdge> edges)
    {
        Dictionary<MapNode, int> index = level.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
        List<int>[] uses = [.. level.Select(_ => new List<int>())];
        foreach (MapEdge edge in edges)
        {
            if (In(edge.From) is int from && In(edge.To) is int to && from != to && !uses[from].Contains(to))
            {
                uses[from].Add(to);
            }
        }
        // A loop is broken at the edge that comes back round to a node still being walked.
        int[] state = new int[level.Count];
        List<(int From, int To)> kept = [];
        for (int i = 0; i < level.Count; i++)
        {
            Walk(i);
        }
        int[] layer = new int[level.Count];
        bool moved = true;
        for (int round = 0; moved && round <= level.Count; round++)
        {
            moved = false;
            foreach ((int from, int to) in kept)
            {
                if (layer[to] < layer[from] + 1)
                {
                    layer[to] = layer[from] + 1;
                    moved = true;
                }
            }
        }
        List<List<MapNode>> layers = [.. Enumerable.Range(0, level.Count == 0 ? 0 : layer.Max() + 1).Select(_ => new List<MapNode>())];
        for (int i = 0; i < level.Count; i++)
        {
            layers[layer[i]].Add(level[i]);
        }
        // Each layer ordered by where its nodes' users stand in the layer above, those with none keeping their order.
        for (int l = 1; l < layers.Count; l++)
        {
            Dictionary<MapNode, int> above = layers[l - 1].Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
            layers[l] = [.. layers[l].Select((n, i) => (Node: n, At: Users(index[n], above) ?? i)).OrderBy(p => p.At).Select(p => p.Node)];
        }
        return [.. layers.Where(l => l.Count > 0)];

        void Walk(int at)
        {
            if (state[at] != 0)
            {
                return;
            }
            state[at] = 1;
            foreach (int next in uses[at])
            {
                if (state[next] != 1)
                {
                    kept.Add((at, next));
                    Walk(next);
                }
            }
            state[at] = 2;
        }

        double? Users(int at, Dictionary<MapNode, int> above)
        {
            double[] places = [.. kept.Where(k => k.To == at && above.ContainsKey(level[k.From])).Select(k => (double)above[level[k.From]])];
            return places.Length == 0 ? null : places.Average();
        }

        // The node of the level that is, or holds, an end of an edge.
        int? In(MapNode end)
        {
            for (MapNode? node = end; node is not null; node = node.Parent)
            {
                if (node.Parent == owner && index.TryGetValue(node, out int at))
                {
                    return at;
                }
            }
            return null;
        }
    }
}
