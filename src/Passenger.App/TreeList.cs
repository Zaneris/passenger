using System.Collections.Specialized;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A list drawing a tree's rows (<see cref="ITreeRow"/>), the one list every tree window draws its rows in: a
/// screen reader meets it as a tree and each row as a tree item, saying its level, its place among the rows under the
/// same row ("3 of 12") and whether it is open or closed, changing as it opens or closes (<see cref="TreeRowPeer"/>).
/// Nothing is worked out until a reader first asks of a row, so a tree no reader reads costs nothing more to show.</summary>
public class TreeList : ListBox
{
    /// <summary>Each row's level and place, worked out as a reader first asks of one since the rows last changed.</summary>
    private (int Level, int Position, int Size)[]? places;

    /// <summary>Whether a reader has asked of a row, after which its rows' peers are told of what changes.</summary>
    private bool heard;

    /// <summary>Whether the rows' peers are to be told again, once the rows have settled.</summary>
    private bool telling;

    private Func<object, ITreeRow?> rowOf = item => item as ITreeRow;
    private Action<object>? toggle;

    public TreeList()
    {
        Items.CollectionChanged += (_, _) =>
        {
            places = null;
            Retell();
        };
    }

    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>Reads its items' rows by <paramref name="row"/> (an item that holds its row), and opens or closes one by
    /// <paramref name="opens"/>, as a reader's expand and collapse do; a list given none reads each item as its row and
    /// opens none.</summary>
    public void Rows<T>(Func<T, ITreeRow> row, Action<T>? opens)
        where T : class
    {
        rowOf = item => item is T each ? row(each) : null;
        toggle = opens is null ? null : item =>
        {
            if (item is T each)
            {
                opens(each);
            }
        };
        places = null;
    }

    /// <summary>Has the peers of the rows shown tell a reader again whether each is open, once the rows have settled: a row
    /// opened or closed with nothing under it changes no row.</summary>
    public void Retell()
    {
        if (!heard || telling)
        {
            return;
        }
        telling = true;
        UiThread.Post(() =>
        {
            telling = false;
            foreach (Control container in GetRealizedContainers())
            {
                (ControlAutomationPeer.FromElement(container) as TreeRowPeer)?.Tell();
            }
        });
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new TreeRowItem();

    protected override void ContainerForItemPreparedOverride(Control container, object? item, int index)
    {
        base.ContainerForItemPreparedOverride(container, item, index);
        Retell();
    }

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
        Retell();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreeListPeer(this);

    /// <summary>The row a container shows, if it shows one.</summary>
    private ITreeRow? RowIn(Control container) => ItemFromContainer(container) is { } item ? rowOf(item) : null;

    /// <summary>A container's row's level (from 1), its place among the rows under the row it sits under, and how many
    /// those are; null for a container showing nothing.</summary>
    private (int Level, int Position, int Size)? PlaceOf(Control container)
    {
        int index = IndexFromContainer(container);
        if (index < 0 || index >= ItemCount)
        {
            return null;
        }
        heard = true;
        places ??= Places();
        return places[index];
    }

    /// <summary>Every row's level and place, in one pass down the rows: a row starts a run of rows beside it at its depth
    /// where none is open there, and a row less deep ends the runs deeper than it.</summary>
    private (int Level, int Position, int Size)[] Places()
    {
        int count = ItemCount;
        int[] depths = new int[count];
        int least = int.MaxValue;
        for (int at = 0; at < count; at++)
        {
            depths[at] = Items[at] is { } item && rowOf(item) is { } row ? row.Depth : 0;
            least = Math.Min(least, depths[at]);
        }
        int[] runOf = new int[count];
        int[] positions = new int[count];
        List<int> sizes = [];
        List<int> open = [];
        for (int at = 0; at < count; at++)
        {
            int level = depths[at] - least;
            if (open.Count > level + 1)
            {
                open.RemoveRange(level + 1, open.Count - level - 1);
            }
            while (open.Count < level + 1)
            {
                open.Add(-1);
            }
            if (open[level] < 0)
            {
                open[level] = sizes.Count;
                sizes.Add(0);
            }
            runOf[at] = open[level];
            positions[at] = ++sizes[open[level]];
        }
        (int Level, int Position, int Size)[] made = new (int, int, int)[count];
        for (int at = 0; at < count; at++)
        {
            made[at] = (depths[at] - least + 1, positions[at], sizes[runOf[at]]);
        }
        return made;
    }

    /// <summary>A tree's row as the list holds it, met by a reader through its <see cref="TreeRowPeer"/>.</summary>
    private sealed class TreeRowItem : ListBoxItem
    {
        protected override Type StyleKeyOverride => typeof(ListBoxItem);

        protected override AutomationPeer OnCreateAutomationPeer() => new TreeRowPeer(this);
    }

    private sealed class TreeListPeer(TreeList owner) : ListBoxAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;
    }

    /// <summary>A tree's row as a reader meets it: a tree item, open or closed where it has rows under it (a row with none
    /// says neither), its level and place given to the system as <see cref="AutomationProperties.PositionInSetProperty"/> and
    /// <see cref="AutomationProperties.SizeOfSetProperty"/>. Avalonia's bridges pass a place on only to Linux's AT-SPI and a
    /// level to none, so its name, the words it shows, says the level, and the place where the system is not told it.</summary>
    public sealed class TreeRowPeer : ListItemAutomationPeer, IExpandCollapseProvider
    {
        /// <summary>Whether the system is told a row's place by its own bridge, which reads the properties set here.</summary>
        private static readonly bool PlaceToldApart = OperatingSystem.IsLinux();

        /// <summary>Whether a row with nothing under it still offers opening: macOS's bridge reads a row's state as it changes
        /// without asking whether it has one, throwing on the window's thread where it has none (#1966); elsewhere a leaf
        /// offering it is said to be closed.</summary>
        public static readonly bool LeavesOffered = OperatingSystem.IsMacOS();

        /// <summary>Whether the row was open, closed or neither when a reader was last told.</summary>
        private ExpandCollapseState told;

        internal TreeRowPeer(ListBoxItem owner)
            : base(owner)
        {
            told = ExpandCollapseState;
            Place();
        }

        private TreeList? List => ItemsControlFromItemContainer(Owner) as TreeList;

        private ITreeRow? Row => List?.RowIn(Owner);

        /// <summary>Its level, from 1 for the rows nothing is over; 0 while it shows no row.</summary>
        public int Level => List?.PlaceOf(Owner)?.Level ?? 0;

        /// <summary>Its place among the rows under the row it sits under, from 1.</summary>
        public int PositionInSet => List?.PlaceOf(Owner)?.Position ?? 0;

        /// <summary>How many rows sit under the row it sits under, itself among them.</summary>
        public int SizeOfSet => List?.PlaceOf(Owner)?.Size ?? 0;

        public ExpandCollapseState ExpandCollapseState => Row switch
        {
            { Expands: true, IsExpanded: true } => ExpandCollapseState.Expanded,
            { Expands: true } => ExpandCollapseState.Collapsed,
            _ => ExpandCollapseState.LeafNode,
        };

        public bool ShowsMenu => false;

        public void Expand() => Open(ExpandCollapseState.Collapsed);

        public void Collapse() => Open(ExpandCollapseState.Expanded);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;

        protected override string? GetNameCore()
        {
            string? words = base.GetNameCore();
            if (List?.PlaceOf(Owner) is not { } place)
            {
                return words;
            }
            string level = $"level {place.Level}";
            string said = PlaceToldApart ? level : $"{level}, {place.Position} of {place.Size}";
            return string.IsNullOrWhiteSpace(words) ? said : $"{words}, {said}";
        }

        /// <summary>A row with nothing under it is no row that opens, so a reader says neither open nor closed of it, but on
        /// macOS (<see cref="LeavesOffered"/>).</summary>
        protected override object? GetProviderCore(Type providerType) =>
            providerType == typeof(IExpandCollapseProvider) && !LeavesOffered && ExpandCollapseState == ExpandCollapseState.LeafNode
                ? null
                : base.GetProviderCore(providerType);

        /// <summary>Tells a reader whether the row is open now, where that changed, and gives the system its place again.</summary>
        internal void Tell()
        {
            Place();
            ExpandCollapseState now = ExpandCollapseState;
            if (now != told)
            {
                ExpandCollapseState was = told;
                told = now;
                RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, was, now);
            }
        }

        private void Place()
        {
            (int Level, int Position, int Size)? place = List?.PlaceOf(Owner);
            AutomationProperties.SetPositionInSet(Owner, place?.Position ?? -1);
            AutomationProperties.SetSizeOfSet(Owner, place?.Size ?? -1);
        }

        /// <summary>Opens or closes the row as its tree's keys do, where it stands as <paramref name="from"/> in a tree that
        /// opens and closes its rows.</summary>
        private void Open(ExpandCollapseState from)
        {
            EnsureEnabled();
            if (ExpandCollapseState == from && List is { toggle: { } opens } list && list.ItemFromContainer(Owner) is { } item)
            {
                opens(item);
                list.Retell();
            }
        }
    }
}
