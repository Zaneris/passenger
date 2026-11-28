using Passenger.Roslyn.Extension;

namespace Passenger.App.DependencyMap;

/// <summary>A node of the map: a project, a namespace, a type or a member, as its level's answer gave it, under the node it
/// was asked of, its children once asked, open or closed.</summary>
public sealed class MapNode(DependencyNode given, MapNode? parent)
{
    public string Id => Given.Id;

    public string Name => Given.Name;

    public string Kind => Given.Kind;

    public DependencyNode Given { get; } = given;

    public MapNode? Parent { get; } = parent;

    /// <summary>How deep it stands: 0 a project, 1 a namespace, 2 a type, 3 a member.</summary>
    public int Level { get; } = parent is null ? 0 : parent.Level + 1;

    /// <summary>The level below, once its answer came; null before.</summary>
    public IReadOnlyList<MapNode>? Children { get; internal set; }

    /// <summary>Whether its level below shows in it; a node with none to show is never open.</summary>
    public bool IsOpen { get; internal set; }

    /// <summary>Whether it may open: one whose level is not yet asked may have nodes below it.</summary>
    public bool Expands => Given.Expands && Children is not { Count: 0 };

    /// <summary>Whether it is shown: every node above it open.</summary>
    public bool IsShown => Parent is null || Parent.IsOpen && Parent.IsShown;

    /// <summary>Whether a node stands somewhere below it.</summary>
    public bool Holds(MapNode other)
    {
        for (MapNode? above = other.Parent; above is not null; above = above.Parent)
        {
            if (above == this)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a node is this one or stands below it.</summary>
    public bool Covers(MapNode other) => other == this || Holds(other);

    public override string ToString() => Name;
}

/// <summary>An edge as the map shows it: between two nodes shown, neither inside the other, with how many uses of each kind
/// the code below them makes, and whether any of them breaks the folder's layer rules.</summary>
public sealed record MapEdge(MapNode From, MapNode To, IReadOnlyDictionary<string, int> Kinds, bool Broken = false)
{
    /// <summary>How many uses it stands for, of every kind.</summary>
    public int Count => Kinds.Values.Sum();
}

/// <summary>What the map knows: each level asked for, its nodes kept under the node it was asked of and its edges as they
/// came. What it shows is worked out from them: the nodes every node above which is open, and each use counted once, at the
/// deepest level open where it was made, between the deepest nodes shown on each side (<see cref="Shown"/>). Pure: the
/// window asks the levels and hands them in.</summary>
public sealed class MapGraph
{
    private readonly Dictionary<string, MapNode> nodes = new(StringComparer.Ordinal);
    private readonly List<MapNode> roots = [];

    /// <summary>Each level's edges, by the node it was asked of (the empty string for the projects').</summary>
    private readonly Dictionary<string, IReadOnlyList<DependencyEdge>> levels = new(StringComparer.Ordinal);

    /// <summary>The projects, once their level came.</summary>
    public IReadOnlyList<MapNode> Roots => roots;

    /// <summary>Whether the projects' level has come.</summary>
    public bool HasRoots => levels.ContainsKey(string.Empty);

    public MapNode? this[string id] => nodes.GetValueOrDefault(id);

    /// <summary>A level as it came, the projects' for <paramref name="of"/> null: its nodes kept under that node and its
    /// edges with them. A level asked again takes the place of the one before, its nodes known again by their ids keeping
    /// whether they were open and their own levels.</summary>
    public void Take(MapNode? of, DependenciesFound found)
    {
        List<MapNode> made = [];
        foreach (DependencyNode given in found.Nodes)
        {
            MapNode node = new(given, of);
            if (nodes.GetValueOrDefault(given.Id) is { } was && was.Parent == of)
            {
                node.Children = was.Children;
                node.IsOpen = was.IsOpen && was.Children is { Count: > 0 };
                Adopt(node, was.Children);
            }
            nodes[given.Id] = node;
            made.Add(node);
        }
        foreach (MapNode gone in (of is null ? roots : of.Children ?? []).Where(n => !made.Any(m => m.Id == n.Id)))
        {
            Forget(gone);
        }
        if (of is null)
        {
            roots.Clear();
            roots.AddRange(made);
        }
        else
        {
            of.Children = made;
            of.IsOpen = made.Count > 0;
        }
        levels[of?.Id ?? string.Empty] = found.Edges;
    }

    /// <summary>The nodes kept below a node known again, put under it in place of the one it was.</summary>
    private void Adopt(MapNode node, IReadOnlyList<MapNode>? below)
    {
        if (below is null)
        {
            return;
        }
        List<MapNode> moved = [];
        foreach (MapNode child in below)
        {
            MapNode now = new(child.Given, node) { Children = child.Children, IsOpen = child.IsOpen };
            nodes[child.Id] = now;
            Adopt(now, child.Children);
            moved.Add(now);
        }
        node.Children = moved;
    }

    /// <summary>A node no longer in its level, its own levels with it.</summary>
    private void Forget(MapNode gone)
    {
        nodes.Remove(gone.Id);
        levels.Remove(gone.Id);
        foreach (MapNode child in gone.Children ?? [])
        {
            Forget(child);
        }
    }

    /// <summary>Whether a node's level below has come.</summary>
    public bool HasLevel(MapNode node) => levels.ContainsKey(node.Id);

    /// <summary>Opens or closes a node whose level has come.</summary>
    public void SetOpen(MapNode node, bool open) => node.IsOpen = open && node.Children is { Count: > 0 };

    /// <summary>The nodes shown, each before those below it, in their levels' order.</summary>
    public IReadOnlyList<MapNode> ShownNodes()
    {
        List<MapNode> shown = [];
        Add(roots);
        return shown;

        void Add(IReadOnlyList<MapNode> level)
        {
            foreach (MapNode node in level)
            {
                shown.Add(node);
                if (node.IsOpen && node.Children is { } below)
                {
                    Add(below);
                }
            }
        }
    }

    /// <summary>The edges shown, of the kinds <paramref name="kinds"/> lets through: each level's whose node is shown open
    /// (the projects' always), but an edge from a node whose own level is open, which counts its uses itself; each from its
    /// node to the deepest node shown on the path of what it uses, none where one end holds the other, those between the
    /// same two nodes made one, broken where any edge of the levels it stands for is (the extension judges each by the
    /// folder's layer rules).</summary>
    public IReadOnlyList<MapEdge> Shown(Func<string, bool> kinds)
    {
        Dictionary<(MapNode, MapNode), Dictionary<string, int>> joined = [];
        HashSet<(MapNode, MapNode)> broken = [];
        foreach ((string level, IReadOnlyList<DependencyEdge> edges) in levels)
        {
            if (level.Length > 0 && (nodes.GetValueOrDefault(level) is not { IsOpen: true } of || !of.IsShown))
            {
                continue;
            }
            foreach (DependencyEdge edge in edges)
            {
                if (nodes.GetValueOrDefault(edge.From) is not { } from || (from.Id != level && from.IsOpen) || !from.IsShown || To(edge.To) is not { } to
                    || to == from || to.Holds(from) || from.Holds(to))
                {
                    continue;
                }
                bool breaks = edge.Broken;
                foreach ((string kind, int count) in edge.Kinds)
                {
                    if (!kinds(kind))
                    {
                        continue;
                    }
                    if (breaks)
                    {
                        broken.Add((from, to));
                    }
                    if (!joined.TryGetValue((from, to), out Dictionary<string, int>? counted))
                    {
                        joined[(from, to)] = counted = new Dictionary<string, int>(StringComparer.Ordinal);
                    }
                    counted[kind] = counted.GetValueOrDefault(kind) + count;
                }
            }
        }
        return [.. joined.Select(j => new MapEdge(j.Key.Item1, j.Key.Item2, j.Value, broken.Contains(j.Key)))];
    }

    /// <summary>A node's edges among those shown: what leaves it, or a node below it while it is open, for a node outside
    /// it, and what comes into it so; an open node's own uses being counted at the nodes below it, these are its whole.</summary>
    public static (IReadOnlyList<MapEdge> Out, IReadOnlyList<MapEdge> In) Crossing(MapNode node, IReadOnlyList<MapEdge> shown) =>
        ([.. shown.Where(e => node.Covers(e.From) && !node.Covers(e.To))], [.. shown.Where(e => node.Covers(e.To) && !node.Covers(e.From))]);

    /// <summary>The deepest node shown on a path of ids from the project down; null where even its project is unknown.</summary>
    private MapNode? To(IReadOnlyList<string> path)
    {
        MapNode? deepest = null;
        foreach (string id in path)
        {
            if (nodes.GetValueOrDefault(id) is not { } node || !node.IsShown)
            {
                break;
            }
            deepest = node;
        }
        return deepest;
    }
}
