using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.Docking;
using Passenger.App.Workspace;

namespace Passenger.App.Editors;

/// <summary>One window's editors, the main window's editor area or an editor window's whole: a tree of splits of groups
/// drawn into its surface, the layers over them (where a dragged tab would drop, completion's popups), and the group
/// last worked in there, which the window's keys go to when it is in front.</summary>
internal sealed class EditorFrame
{
    private readonly ContentControl splits = new();

    public EditorFrame(Node root, EditorGroup current, Func<EditorFrame, Point, DropZone?> zoneAt, Action<EditorFrame, EditorGroup, OpenDocument, Point> drop, Control? empty = null)
    {
        Root = root;
        Current = current;
        Surface = new Panel();
        Surface.Children.Add(splits);
        if (empty is not null)
        {
            Surface.Children.Add(empty);
        }
        Surface.Children.Add(Assist);
        Surface.Children.Add(Drop);
        TabDrag = new DragGesture<(EditorGroup Group, OpenDocument Document)>(Surface, Drop, (_, at) => zoneAt(this, at), (tab, at) => drop(this, tab.Group, tab.Document, at));
    }

    public Node Root { get; set; }

    /// <summary>The group last worked in here, and still here.</summary>
    public EditorGroup Current { get; set; }

    /// <summary>The splits with the layers over them, in whose coordinates a tab dragged from here moves.</summary>
    public Panel Surface { get; }

    /// <summary>The layer over the splits that completion and the code actions' popups sit on.</summary>
    public Canvas Assist { get; } = new() { ClipToBounds = false };

    public DropOverlay Drop { get; } = new();

    /// <summary>A tab of a group here dragged onto a group of any window's, onto an edge to split it, or out of them all.</summary>
    public DragGesture<(EditorGroup Group, OpenDocument Document)> TabDrag { get; }

    /// <summary>The window of its own this is, or null for the main window's editors.</summary>
    public EditorWindow? Window { get; set; }

    /// <summary>Set while the person closes its window: its tabs are going, and the window with them.</summary>
    public bool Closing { get; set; }

    public IEnumerable<EditorGroup> Groups => Root.Groups;

    public bool IsEmpty => !Root.Groups.Any(g => g.Tabs.Count > 0);

    /// <summary>Draws the tree again, every group moved into its place in it.</summary>
    public void Rebuild()
    {
        foreach (EditorGroup group in Root.Groups)
        {
            (group.Parent as Panel)?.Children.Remove(group);
        }
        splits.Content = Build(Root);
    }

    private static Control Build(Node node)
    {
        if (node is Leaf leaf)
        {
            (leaf.Group.Parent as Panel)?.Children.Remove(leaf.Group);
            return new Panel { Children = { leaf.Group } };
        }
        SplitNode split = (SplitNode)node;
        bool across = split.Orientation == Orientation.Horizontal;
        Grid grid = across
            ? new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*") }
            : new Grid { RowDefinitions = new RowDefinitions("*,1,*") };
        Control first = Build(split.First);
        Control second = Build(split.Second);
        // Drawn again (a group gone elsewhere), a split keeps the share it was dragged to.
        double ratio = split.Ratio;
        split.Built = grid;
        if (across)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
        }
        else
        {
            grid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
            grid.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star);
        }
        HairlineDivider divider = new(across ? GridResizeDirection.Columns : GridResizeDirection.Rows);
        if (across)
        {
            Grid.SetColumn(second, 2);
            Grid.SetColumn(divider, 1);
        }
        else
        {
            Grid.SetRow(second, 2);
            Grid.SetRow(divider, 1);
        }
        grid.Children.Add(first);
        grid.Children.Add(divider);
        grid.Children.Add(second);
        return grid;
    }

    internal abstract class Node
    {
        public abstract IEnumerable<EditorGroup> Groups { get; }

        /// <summary>Whether its groups, in order, are those of a list from a place on, which it moves past them.</summary>
        public abstract bool Lists(EditorGroup[] groups, ref int at);

        /// <summary>Adds its groups, in order, to a list.</summary>
        public abstract void AddTo(List<EditorGroup> groups);

        /// <summary>The tree with a group's leaf replaced by what a function makes of it.</summary>
        public abstract Node Replace(EditorGroup group, Func<Leaf, Node> with);

        /// <summary>The tree without a group's leaf, its sibling in its split's place; null when it was all.</summary>
        public abstract Node? Without(EditorGroup group);

        /// <summary>The tree with another node in one of its nodes' place.</summary>
        public Node Replace(Node target, Node with) => this == target ? with
            : this is SplitNode split ? new SplitNode(split.Orientation, split.First.Replace(target, with), split.Second.Replace(target, with)) { Ratio = split.Ratio }
            : this;
    }

    internal sealed class Leaf(EditorGroup group) : Node
    {
        public EditorGroup Group { get; } = group;
        public override IEnumerable<EditorGroup> Groups => [Group];
        public override bool Lists(EditorGroup[] groups, ref int at) => at < groups.Length && groups[at++] == Group;
        public override void AddTo(List<EditorGroup> groups) => groups.Add(Group);
        public override Node Replace(EditorGroup target, Func<Leaf, Node> with) => target == Group ? with(this) : this;
        public override Node? Without(EditorGroup target) => target == Group ? null : this;
    }

    internal sealed class SplitNode(Orientation orientation, Node first, Node second) : Node
    {
        public Orientation Orientation { get; } = orientation;
        public Node First { get; } = first;
        public Node Second { get; } = second;

        /// <summary>The first part's share of the room: as dragged, once its grid is laid out.</summary>
        public double Ratio
        {
            get
            {
                if (Built is not { } grid)
                {
                    return ratio;
                }
                (double a, double b) = Orientation == Orientation.Horizontal
                    ? (grid.ColumnDefinitions[0].ActualWidth, grid.ColumnDefinitions[2].ActualWidth)
                    : (grid.RowDefinitions[0].ActualHeight, grid.RowDefinitions[2].ActualHeight);
                return a + b > 0 ? a / (a + b) : ratio;
            }
            init => ratio = value;
        }

        private readonly double ratio = 0.5;

        /// <summary>The grid this split was last drawn as.</summary>
        public Grid? Built { get; set; }
        public override IEnumerable<EditorGroup> Groups => First.Groups.Concat(Second.Groups);
        public override bool Lists(EditorGroup[] groups, ref int at) => First.Lists(groups, ref at) && Second.Lists(groups, ref at);

        public override void AddTo(List<EditorGroup> groups)
        {
            First.AddTo(groups);
            Second.AddTo(groups);
        }

        public override Node Replace(EditorGroup target, Func<Leaf, Node> with) =>
            new SplitNode(Orientation, First.Replace(target, with), Second.Replace(target, with)) { Ratio = Ratio };

        public override Node? Without(EditorGroup target)
        {
            Node? first = First.Without(target);
            Node? second = Second.Without(target);
            return first is null ? second : second is null ? first : new SplitNode(Orientation, first, second) { Ratio = Ratio };
        }
    }
}
