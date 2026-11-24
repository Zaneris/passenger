using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App.DependencyMap;

/// <summary>A row of the map's tree: a node shown, at its level.</summary>
public sealed class MapRow(MapNode node) : ITreeRow
{
    public MapNode Node { get; } = node;

    public int Depth => Node.Level;

    public bool Expands => Node.Expands;

    public bool IsExpanded => Node.IsOpen;
}

/// <summary>A row of the chosen node's edges (<see cref="MapGraph.Crossing"/>): an edge leaving it (what it uses) or coming
/// into it (what uses it), from or to a node below it while it is open.</summary>
public sealed record MapEdgeRow(MapEdge Edge, bool Out, MapNode Chosen, bool Broken = false)
{
    /// <summary>The node at the other end.</summary>
    public MapNode Other => Out ? Edge.To : Edge.From;

    /// <summary>The row's words: which way, the other end, the node below the chosen one it is of where it is, and the uses
    /// by kind.</summary>
    public string Words
    {
        get
        {
            MapNode own = Out ? Edge.From : Edge.To;
            string way = own == Chosen ? (Out ? "Uses" : "Used by") : $"{own.Name} {(Out ? "uses" : "used by")}";
            return $"{way} {Other.Name}: {MapPicture.KindsOf(Edge)}{(Broken ? ", breaking the layer rules" : string.Empty)}";
        }
    }
}

/// <summary>The Dependency Map window: how the solution's projects depend on each other, and, a level at a time as each is
/// opened, their namespaces, types and members, from the compiler's binding of the code (Passenger's extension). The
/// picture (<see cref="MapPicture"/>) and the tree beside it show the same nodes, open alike: a screen reader reads the
/// tree, and under it what the chosen node uses and what uses it. The filter narrows the tree by name, its menu choosing
/// the kinds of edge shown; Enter or a double click goes to a node's code.</summary>
public sealed class DependencyMapView : DockPanel
{
    private readonly MapGraph graph = new();
    private readonly TreeRows<MapRow> rows = [];
    private readonly Dictionary<MapNode, MapRow> rowOf = [];
    private readonly TreeList tree;
    private readonly ListBox edgeList;
    private readonly TextBox filter = SearchBox.Finding("Filter nodes");
    private readonly FilterMenu kinds;
    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    private readonly EmptyState placeholder = new();
    private readonly TextBlock heading = new() { Classes = { StyleClasses.PaneTitle }, Margin = new Thickness(Gaps.Normal, Gaps.Small), TextTrimming = TextTrimming.CharacterEllipsis };
    private IReadOnlyList<MapEdge> shown = [];

    /// <summary>Asks a level of the map: the projects for null.</summary>
    private Func<string?, Task<DependenciesFound>>? ask;

    public DependencyMapView()
    {
        Picture = new MapPicture();
        Picture.Chose += (_, node) => Choose(node, fromPicture: true);
        Picture.Toggled += (_, node) => Toggle(node);
        Picture.Opened += (_, node) => GoToRequested?.Invoke(this, node);
        filter.Margin = new Thickness(0, 0, Gaps.Small, 0);
        filter.TextChanged += (_, _) => ListRows();
        kinds = new FilterMenu("Kinds of Edge Shown", () => [.. DependencyKinds.All.Select(kind => (Control)ListMenus.Toggle(Capitalised(MapPicture.KindWords(kind)), !hidden.Contains(kind), on =>
        {
            if (!on)
            {
                hidden.Add(kind);
            }
            else
            {
                hidden.Remove(kind);
            }
            kinds!.Narrowed = hidden.Count > 0;
            Redraw();
        }))]);
        kinds.Inside(filter);
        // Each part its own, so a narrow window wraps the zoom's buttons under the filter rather than over it.
        Children.Add(ToolBars.Top(new ToolFlow
        {
            Margin = new Thickness(Gaps.Normal, Gaps.Hair),
            Spacing = Gaps.Hair,
            Children =
            {
                filter,
                ToolFlow.Spring(),
                ToolButtons.Of(IconKeys.ZoomIn, ActionTips.Named("Zoom In", KeyWords.Of(ZoomedPicture.ZoomInKey)), Picture.ZoomIn),
                ToolButtons.Of(IconKeys.ZoomOut, ActionTips.Named("Zoom Out", KeyWords.Of(ZoomedPicture.ZoomOutKey)), Picture.ZoomOut),
                ToolButtons.Of(IconKeys.ActualSize, ActionTips.Named("Actual Size", KeyWords.Of(ZoomedPicture.ActualSizeKey)), Picture.ActualSize),
                ToolButtons.Of(IconKeys.ZoomFit, "Fit Zoom to Window", Picture.Fit),
                ToolButtons.Of(IconKeys.Rerun, "Read the Map Again", () => IdeErrors.Observe(RefreshAsync())),
            },
        }));

        tree = new TreeList { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(tree, Row);
        TreeKeys.Attach<MapRow>(tree, row => Toggle(row.Node), row =>
        {
            GoToRequested?.Invoke(this, row.Node);
            return true;
        });
        tree.SelectionChanged += (_, _) =>
        {
            if (tree.SelectedItem is MapRow row && row.Node != Chosen)
            {
                Choose(row.Node, fromPicture: false);
            }
        };
        ListMenus.Attach(tree, () => tree.SelectedItem is not MapRow chosen ? [] :
        [
            ListMenus.Source(() => GoToRequested?.Invoke(this, chosen.Node), chosen.Node.Given.Path is null ? Refusals.MapNodeUnplaced : null),
            ListMenus.Item("Copy Name", () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Node.Name))),
        ]);
        edgeList = new ListBox { Classes = { StyleClasses.Tree } };
        ReusedRows.Use(edgeList, EdgeRow);
        ListKeys.Act(edgeList, new ListActs
        {
            Open = () =>
            {
                if (edgeList.SelectedItem is MapEdgeRow chosen)
                {
                    Choose(chosen.Other, fromPicture: false);
                }
            },
        });
        DockPanel side = new();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel edges = new() { Children = { heading, edgeList } };
        PaneSplit lists = new(tree, edges, "3*,1,2*");
        lists.SetPresentation(stack: true, onlySecond: false);
        side.Children.Add(lists);
        Panel drawn = new() { Children = { Picture, placeholder } };
        Children.Add(new PaneSplit(side, drawn, "1*,1,3*"));
        placeholder.Text = "The map shows here once the C# language server has read the solution";
    }

    /// <summary>The picture of the map.</summary>
    public MapPicture Picture { get; }

    /// <summary>The tree of the nodes shown.</summary>
    public TreeList Tree => tree;

    /// <summary>The menu of the kinds of edge shown.</summary>
    public FilterMenu Kinds => kinds;

    /// <summary>A node whose code to go to.</summary>
    public event EventHandler<MapNode>? GoToRequested;

    /// <summary>A level could not be read, and why, for the status bar.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>What the map knows.</summary>
    public MapGraph Graph => graph;

    /// <summary>The edges shown, as the picture draws them.</summary>
    public IReadOnlyList<MapEdge> ShownEdges => shown;

    /// <summary>The folder's layer rules, whose broken edges the map draws in the error's colour; none while it has none.</summary>
    public LayerRules? Rules
    {
        get => rules;
        set
        {
            rules = value;
            if (graph.HasRoots)
            {
                Redraw();
            }
        }
    }

    private LayerRules? rules;

    private IReadOnlySet<MapEdge> broken = new HashSet<MapEdge>();

    /// <summary>The tree's rows.</summary>
    public IReadOnlyList<MapRow> Rows => rows;

    /// <summary>The chosen node's edges as listed under the tree.</summary>
    public IReadOnlyList<MapEdgeRow> EdgeRows => edgeList.ItemsSource as IReadOnlyList<MapEdgeRow> ?? [];

    /// <summary>The node chosen.</summary>
    public MapNode? Chosen { get; private set; }

    /// <summary>What the window says in place of the map; null while it shows one.</summary>
    public string? Said => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>Whether every level asked has come.</summary>
    public bool IsReading => reading > 0;

    private int reading;

    /// <summary>The map read by <paramref name="asker"/> as the code stands (each level open read again, as the extension
    /// keeps what an edit did not reach), then opened down to the node a path of ids names (none: the projects alone),
    /// which is chosen.</summary>
    public async Task ShowAsync(Func<string?, Task<DependenciesFound>> asker, IReadOnlyList<string>? path = null)
    {
        UiThread.Check();
        ask = asker;
        if (graph.HasRoots)
        {
            await RefreshAsync();
        }
        else if (!await LevelAsync(null))
        {
            return;
        }
        MapNode? reached = null;
        foreach (string id in path ?? [])
        {
            if (graph[id] is not { } node)
            {
                break;
            }
            reached = node;
            if (id != path![^1] && !node.IsOpen)
            {
                await OpenAsync(node);
            }
        }
        Redraw();
        if (reached is not null)
        {
            Choose(reached, fromPicture: false);
            Picture.Reveal(reached);
        }
        else if (!fitted)
        {
            fitted = true;
            UiThread.Post(Picture.Fit);
        }
    }

    private bool fitted;

    /// <summary>Says why there is no map, in its place.</summary>
    public void Say(string why)
    {
        placeholder.Text = why;
        placeholder.IsVisible = true;
    }

    /// <summary>Every level open read again, as the code stands now.</summary>
    public async Task RefreshAsync()
    {
        if (ask is null)
        {
            return;
        }
        if (!await LevelAsync(null))
        {
            return;
        }
        // Each level open asked again, the shallower first, as the nodes below are known again by their ids.
        foreach (MapNode node in graph.ShownNodes().Where(n => n.IsOpen).ToList())
        {
            if (graph[node.Id] is { IsOpen: true } still)
            {
                await LevelAsync(still);
            }
        }
        Redraw();
    }

    /// <summary>A level asked and taken in; false, said, where it could not be read.</summary>
    private async Task<bool> LevelAsync(MapNode? of)
    {
        reading++;
        try
        {
            DependenciesFound found = await ask!(of?.Id);
            graph.Take(of, found);
            placeholder.IsVisible = false;
            return true;
        }
        catch (Exception failed) when (failed is Lsp.LanguageServerFailed or InvalidOperationException or TimeoutException)
        {
            string why = $"The dependency map could not read {of?.Name ?? "the solution's projects"}: {failed.Message}";
            if (!graph.HasRoots)
            {
                Say(why);
            }
            Failed?.Invoke(this, why);
            return false;
        }
        finally
        {
            reading--;
        }
    }

    /// <summary>Opens a node, its level asked the first time.</summary>
    public async Task OpenAsync(MapNode node)
    {
        if (node.IsOpen || !node.Expands)
        {
            return;
        }
        if (!graph.HasLevel(node) && !await LevelAsync(node))
        {
            return;
        }
        graph.SetOpen(node, open: true);
        Redraw();
    }

    public void Close(MapNode node)
    {
        if (!node.IsOpen)
        {
            return;
        }
        graph.SetOpen(node, open: false);
        Redraw();
    }

    /// <summary>Opens a node, or closes it, as its chevron does.</summary>
    public void Toggle(MapNode node)
    {
        if (node.IsOpen)
        {
            Close(node);
        }
        else
        {
            IdeErrors.Observe(OpenAsync(node));
        }
    }

    /// <summary>The picture laid out and the tree listed again for what is open and the kinds shown.</summary>
    private void Redraw()
    {
        shown = graph.Shown(kind => !hidden.Contains(kind), rules);
        broken = shown.Where(e => e.Broken).ToHashSet();
        Picture.Show(graph, shown, broken);
        ListRows();
        if (Chosen is { } chosen && graph[chosen.Id] is { } still)
        {
            Choose(still, fromPicture: true);
        }
    }

    /// <summary>The tree's rows: the nodes shown, those the filter's words name and the nodes above them.</summary>
    private void ListRows()
    {
        string words = filter.Text?.Trim() ?? string.Empty;
        IReadOnlyList<MapNode> nodes = graph.ShownNodes();
        HashSet<MapNode>? kept = null;
        if (words.Length > 0)
        {
            kept = [];
            foreach (MapNode node in nodes.Where(n => n.Name.Contains(words, StringComparison.OrdinalIgnoreCase)))
            {
                for (MapNode? up = node; up is not null && kept.Add(up); up = up.Parent)
                {
                }
            }
        }
        MapRow? was = tree.SelectedItem as MapRow;
        rows.Clear();
        foreach (MapNode node in nodes.Where(n => kept is null || kept.Contains(n)))
        {
            if (!rowOf.TryGetValue(node, out MapRow? row))
            {
                rowOf[node] = row = new MapRow(node);
            }
            rows.Add(row);
        }
        foreach (MapNode gone in rowOf.Keys.Where(n => graph[n.Id] != n).ToList())
        {
            rowOf.Remove(gone);
        }
        tree.SelectedItem = was is not null && rows.Contains(was) ? was : Chosen is { } chosen && rowOf.TryGetValue(chosen, out MapRow? now) ? now : null;
        tree.Retell();
    }

    /// <summary>A node chosen: ringed in the picture, its row chosen in the tree, and its edges listed.</summary>
    public void Choose(MapNode node, bool fromPicture)
    {
        Chosen = node;
        Picture.Chosen = node;
        if (!fromPicture)
        {
            Picture.Reveal(node);
        }
        if (rowOf.TryGetValue(node, out MapRow? row) && tree.SelectedItem != row)
        {
            tree.SelectedItem = row;
            tree.ScrollIntoView(row);
        }
        heading.Text = $"{node.Name} ({node.Kind})";
        (IReadOnlyList<MapEdge> leaving, IReadOnlyList<MapEdge> coming) = MapGraph.Crossing(node, shown);
        edgeList.ItemsSource = (IReadOnlyList<MapEdgeRow>)[.. leaving.Select(e => new MapEdgeRow(e, Out: true, node, e.Broken)).OrderBy(e => e.Edge.From != node).ThenBy(e => e.Other.Name, StringComparer.OrdinalIgnoreCase),
            .. coming.Select(e => new MapEdgeRow(e, Out: false, node, e.Broken)).OrderBy(e => e.Edge.To != node).ThenBy(e => e.Other.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The map's zoom keys, with the platform's command modifier, as every zoomed view takes them.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && ZoomedPicture.ZoomKeyOf(e) is int zoom)
        {
            (zoom > 0 ? (Action)Picture.ZoomIn : zoom < 0 ? Picture.ZoomOut : Picture.ActualSize)();
            e.Handled = true;
        }
    }

    private static string Capitalised(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>A node's row: its chevron, its name, and its kind with what it uses and what uses it.</summary>
    private Control Row()
    {
        RowExpander chevron = RowExpander.Of<MapRow>(row => Toggle(row.Node));
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Gaps.Wide, 0, 0, 0),
            Classes = { StyleClasses.Caption, StyleClasses.Muted },
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        StackPanel line = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { chevron, name, detail } };
        return line.Showing<MapRow>(row =>
        {
            line.Margin = new Thickness(Gaps.Normal + (row.Depth * Gaps.Indent), 0, Gaps.Normal, 0);
            chevron.Expands = row.Expands;
            chevron.IsOpen = row.IsExpanded;
            name.Text = row.Node.Name;
            (IReadOnlyList<MapEdge> uses, IReadOnlyList<MapEdge> used) = MapGraph.Crossing(row.Node, shown);
            detail.Text = $"{row.Node.Kind} · uses {Numbers.Said(uses.Count)} · used by {Numbers.Said(used.Count)}";
        });
    }

    private Control EdgeRow()
    {
        TextBlock words = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(Gaps.Normal, 0) };
        Border line = new() { Height = ListRow.Height, Child = words };
        return line.Showing<MapEdgeRow>(row => words.Text = row.Words);
    }
}
