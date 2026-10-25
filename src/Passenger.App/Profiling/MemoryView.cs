using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.Core;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>A row of an object set's Shortest Paths to Roots: a step, how deep it sits, and whether the steps after it are
/// listed under it.</summary>
public sealed class RetentionRow(RetentionNode node, int depth) : Observable, ITreeRow
{
    private bool isExpanded;
    private List<RetentionRow>? steps;

    public RetentionNode Node { get; } = node;

    public int Depth { get; } = depth;

    public bool Expands => Node.Children.Count > 0;

    /// <summary>The rows of the steps after it, made the first time it opens.</summary>
    public IReadOnlyList<RetentionRow> Steps => steps ??= [.. Node.Children.Select(c => new RetentionRow(c, Depth + 1))];

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }
}

/// <summary>The profiler window's Memory page: the run's memory snapshots on the left; on the right the chosen one's
/// types, a comparison of two (Compare with…), or an object set opened from either, with its objects, Shortest Paths to
/// Roots and Key Retention Paths; above it the trail of what was opened, to go back along. What takes a while on a large
/// heap (a comparison, an object set's paths) is worked out off the UI thread.</summary>
public sealed class MemoryView : DockPanel
{
    private const double NumberWidth = 104;

    private readonly ObservableCollection<MemorySnapshot> snapshots = [];
    private readonly ListBox snapshotList;
    private readonly StackPanel crumbs;
    private readonly StackPanel actions;
    private readonly TextBlock summary;
    private readonly TextBlock waiting;
    private readonly ListBox types;
    private readonly ListBox deltas;
    private readonly ListBox objects;
    private readonly TreeRows<RetentionRow> pathRows = [];
    private readonly ListBox paths;
    private readonly ListBox keyPaths;
    private readonly ToggleButton[] setTabs;
    private readonly DockPanel typePage;
    private readonly DockPanel deltaPage;
    private readonly DockPanel setPage;
    private readonly DockPanel objectsPage;
    private readonly DockPanel pathsPage;
    private readonly DockPanel keyPathsPage;
    private readonly List<Page> trail = [];
    private int loading;
    private int typeSort;
    private int deltaSort;

    public MemoryView()
    {
        snapshotList = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = snapshots,
            ItemTemplate = new FuncDataTemplate<MemorySnapshot>((snapshot, _) => snapshot is null ? new TextBlock() : SnapshotRow(snapshot), supportsRecycling: false),
        };
        snapshotList.SelectionChanged += (_, _) =>
        {
            if (snapshotList.SelectedItem is MemorySnapshot snapshot && (trail.Count == 0 || trail[0] is not SnapshotPage { Snapshot: var shown } || shown != snapshot))
            {
                Go([new SnapshotPage(snapshot)]);
            }
        };
        Border left = new() { [DockProperty] = Dock.Left, Width = 230, Child = snapshotList, BorderThickness = new Thickness(0, 0, 1, 0) };
        left.Bind(Border.BorderBrushProperty, left.GetResourceObservable(TokenKeys.Divider));
        Children.Add(left);

        crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, [DockProperty] = Dock.Right };
        DockPanel head = new() { Margin = new Thickness(12, 4, 8, 0), Height = 30 };
        head.Children.Add(actions);
        head.Children.Add(crumbs);
        summary = Muted(string.Empty);
        summary.Margin = new Thickness(12, 0, 12, 6);
        summary.TextWrapping = TextWrapping.Wrap;
        StackPanel top = new() { [DockProperty] = Dock.Top, Children = { head, summary } };

        Table typeTable = new(("Type", 0), ("Objects", NumberWidth), ("Bytes", NumberWidth), ("Retained bytes", NumberWidth + 16));
        types = List<HeapType>(type => typeTable.Row(Cell(type.Name), Number($"{type.Count:N0}"), Number(Size(type.Bytes)), Number(Size(type.RetainedBytes))));
        OnEnter(types, OpenType);
        typePage = Framed(typeTable.Header(SortTypes), types);

        Table deltaTable = new(("Type", 0), ("Objects delta", NumberWidth), ("Bytes delta", NumberWidth), ("New objects", NumberWidth),
            ("New bytes", NumberWidth), ("Dead objects", NumberWidth), ("Dead bytes", NumberWidth), ("Survived objects", NumberWidth + 16));
        deltas = List<TypeDelta>(type => deltaTable.Row(Cell(type.Name), Number(Signed(type.ObjectsDelta, n => $"{n:N0}")), Number(Signed(type.BytesDelta, Size)),
            Number($"{type.NewObjects:N0}"), Number(Size(type.NewBytes)), Number($"{type.DeadObjects:N0}"), Number(Size(type.DeadBytes)), Number($"{type.SurvivedObjects:N0}")));
        OnEnter(deltas, () => OpenDelta(Kind.New));
        deltaPage = Framed(deltaTable.Header(SortDeltas), deltas);

        Table objectTable = new(("Object", 0), ("Bytes", NumberWidth), ("Retained bytes", NumberWidth + 16));
        objects = List<HeapObject>(item => objectTable.Row(ObjectCell(item), Number(Size(item.Bytes)), Number(Size(item.RetainedBytes))));
        OnEnter(objects, OpenObject);
        objectsPage = Framed(objectTable.Header(null), objects);

        Table pathTable = new(("Path", 0), ("Objects", NumberWidth), ("Retained bytes", NumberWidth + 16));
        paths = List<RetentionRow>(row => pathTable.Row(PathStep(row), Number($"{row.Node.Objects:N0}"), Number(Size(row.Node.RetainedBytes))));
        paths.ItemsSource = pathRows;
        TreeKeys.Attach<RetentionRow>(paths, Toggle);
        pathsPage = Framed(pathTable.Header(null), paths);

        Table keyTable = new(("Key retention path", 0), ("Objects", NumberWidth), ("Retained bytes", NumberWidth + 16));
        keyPaths = List<RetentionPath>(path => keyTable.Row(Cell(path.Text), Number($"{path.Objects:N0}"), Number(Size(path.RetainedBytes))));
        keyPathsPage = Framed(keyTable.Header(null), keyPaths);

        setTabs = [Tab("Objects", 0), Tab("Shortest Paths to Roots", 1), Tab("Key Retention Paths", 2)];
        StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0, 8, 4), [DockProperty] = Dock.Top };
        foreach (ToggleButton tab in setTabs)
        {
            tabs.Children.Add(tab);
        }
        setPage = new DockPanel { Children = { tabs, new Panel { Children = { objectsPage, pathsPage, keyPathsPage } } } };

        waiting = Muted(string.Empty);
        waiting.HorizontalAlignment = HorizontalAlignment.Center;
        waiting.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(top);
        Children.Add(new Panel { Children = { typePage, deltaPage, setPage, waiting } });
        Show(null);
    }

    /// <summary>The run's memory snapshots, the oldest first.</summary>
    public IReadOnlyList<MemorySnapshot> Snapshots => snapshots;

    /// <summary>The snapshot whose types are shown, or null.</summary>
    public MemorySnapshot? Snapshot => Current is SnapshotPage page ? page.Snapshot : null;

    /// <summary>The comparison shown, or null.</summary>
    public SnapshotComparison? Comparison => Current is ComparisonPage page ? page.Comparison : null;

    /// <summary>The object set shown, or null.</summary>
    public ObjectSet? Set => Current is SetPage page ? page.Set : null;

    /// <summary>The rows as listed now: the snapshot's types, the comparison's, the set's objects, its paths' steps (as far
    /// as they are open) and its key retention paths.</summary>
    public IReadOnlyList<HeapType> TypeRows => types.ItemsSource as IReadOnlyList<HeapType> ?? [];

    public IReadOnlyList<TypeDelta> DeltaRows => deltas.ItemsSource as IReadOnlyList<TypeDelta> ?? [];

    public IReadOnlyList<HeapObject> ObjectRows => objects.ItemsSource as IReadOnlyList<HeapObject> ?? [];

    public IReadOnlyList<RetentionRow> PathRows => pathRows;

    public IReadOnlyList<RetentionPath> KeyPathRows => keyPaths.ItemsSource as IReadOnlyList<RetentionPath> ?? [];

    /// <summary>The trail of what was opened, as its crumbs read: "Snapshot #2 › New objects of the type Item".</summary>
    public string Trail => string.Join(" › ", trail.Select(p => p.Crumb));

    public string Summary => summary.Text ?? string.Empty;

    /// <summary>What the page says while its work is under way, or why it shows nothing.</summary>
    public string Waiting => waiting.IsVisible ? waiting.Text ?? string.Empty : string.Empty;

    /// <summary>Compare with…'s menu as last opened.</summary>
    public MenuFlyout? CompareMenu { get; private set; }

    /// <summary>The lists, for a person's keys.</summary>
    public ListBox TypeList => types;

    public ListBox DeltaList => deltas;

    public ListBox ObjectList => objects;

    /// <summary>Which of the object set's views is shown: its objects (0), Shortest Paths to Roots (1), Key Retention Paths (2).</summary>
    public int SetTab => Array.FindIndex(setTabs, t => t.IsChecked == true);

    /// <summary>A snapshot taken: listed and shown. The first of a run puts the earlier run's away, which it cannot be compared with.</summary>
    public void Add(MemorySnapshot snapshot)
    {
        if (snapshot.Number == 1)
        {
            snapshots.Clear();
        }
        snapshots.Add(snapshot);
        snapshotList.SelectedItem = snapshot;
    }

    /// <summary>Chooses a row in its list: a snapshot's type, a comparison's, or an object set's object.</summary>
    public void Choose(object row)
    {
        ListBox list = row is TypeDelta ? deltas : row is HeapObject ? objects : types;
        list.SelectedItem = row;
    }

    /// <summary>Compares the snapshot shown with another, the older to the newer, as Compare with…'s menu does.</summary>
    public async Task CompareWithAsync(MemorySnapshot other)
    {
        if (Current is not SnapshotPage { Snapshot: var shown })
        {
            return;
        }
        if (shown.WhyNotComparable(other) is { } why)
        {
            summary.Text = why;
            return;
        }
        int load = Begin("Comparing snapshots…");
        SnapshotComparison comparison = await BackgroundWork.Run(() => shown.CompareWith(other));
        if (load == loading)
        {
            Go([.. trail, new ComparisonPage(comparison)]);
        }
    }

    private Page? Current => trail.Count == 0 ? null : trail[^1];

    /// <summary>Work for a page begun: the page gives way to what is under way, and a later page's work supersedes it.</summary>
    private int Begin(string what)
    {
        waiting.Text = what;
        foreach (Control page in new Control[] { typePage, deltaPage, setPage })
        {
            page.IsVisible = false;
        }
        waiting.IsVisible = true;
        return ++loading;
    }

    /// <summary>Shows the last page of a trail, the trail's crumbs above it.</summary>
    private void Go(IReadOnlyList<Page> pages)
    {
        loading++;
        trail.Clear();
        trail.AddRange(pages);
        Show(Current);
    }

    private void Show(Page? page)
    {
        crumbs.Children.Clear();
        for (int i = 0; i < trail.Count; i++)
        {
            if (i > 0)
            {
                crumbs.Children.Add(Muted("›"));
            }
            if (i == trail.Count - 1)
            {
                crumbs.Children.Add(new TextBlock { Text = trail[i].Crumb, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                continue;
            }
            int back = i + 1;
            Button crumb = new() { Classes = { "link" }, Content = trail[i].Crumb, VerticalAlignment = VerticalAlignment.Center };
            crumb.Click += (_, _) => Go([.. trail.Take(back)]);
            crumbs.Children.Add(crumb);
        }
        actions.Children.Clear();
        typePage.IsVisible = page is SnapshotPage;
        deltaPage.IsVisible = page is ComparisonPage;
        setPage.IsVisible = page is SetPage;
        waiting.IsVisible = page is null;
        waiting.Text = page is null ? "Get Memory Snapshot while a program is profiled to see what its heap holds" : string.Empty;
        switch (page)
        {
            case SnapshotPage { Snapshot: var snapshot }:
                summary.Text = $"{snapshot.Objects:N0} objects, {Size(snapshot.Bytes)} live, in {snapshot.Types.Count:N0} types; taken at "
                    + $"{snapshot.Taken:HH:mm:ss} in {snapshot.Took.TotalSeconds:0.0} s ({snapshot.Phases})";
                types.ItemsSource = snapshot.Types;
                types.SelectedIndex = snapshot.Types.Count > 0 ? 0 : -1;
                Button compare = Action("Compare with…", () => ShowCompareMenu(snapshot));
                compare.IsEnabled = snapshots.Count > 1;
                ToolTip.SetTip(compare, "Compare this snapshot with another of the run: its new, dead and survived objects");
                break;
            case ComparisonPage { Comparison: var comparison }:
                summary.Text = $"{comparison.NewObjects:N0} new, {comparison.DeadObjects:N0} dead and {comparison.SurvivedObjects:N0} survived objects "
                    + $"from {comparison.Older.Name} to {comparison.Newer.Name}"
                    + (comparison.Inexact is { } inexact ? $". {inexact}: some objects may show as dead and new though they survived." : string.Empty);
                deltas.ItemsSource = comparison.Types;
                deltas.SelectedIndex = comparison.Types.Count > 0 ? 0 : -1;
                Action("Open New Objects", () => OpenDelta(Kind.New));
                Action("Open Dead Objects", () => OpenDelta(Kind.Dead));
                Action("Open Survived Objects", () => OpenDelta(Kind.Survived));
                break;
            case SetPage set:
                summary.Text = $"{set.Set.Count:N0} {Core.Plural.Noun(set.Set.Count, "object")}, {Size(set.Set.Bytes)}, retained {Size(set.Set.RetainedBytes)}, in {set.Set.Snapshot.Name}";
                objects.ItemsSource = set.Set.Objects;
                objects.SelectedIndex = set.Set.Count > 0 ? 0 : -1;
                ShowPaths(set.Paths);
                keyPaths.ItemsSource = set.KeyPaths;
                ShowTab(set.Tab);
                break;
            default:
                summary.Text = string.Empty;
                break;
        }
    }

    private void ShowCompareMenu(MemorySnapshot snapshot)
    {
        MenuFlyout menu = new();
        foreach (MemorySnapshot other in snapshots.Where(s => s != snapshot))
        {
            MenuItem item = new() { Header = $"{other.Name} ({other.Taken:HH:mm:ss})" };
            item.Click += (_, _) => IdeErrors.Observe(CompareWithAsync(other));
            menu.Items.Add(item);
        }
        CompareMenu = menu;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.ShowAt(actions.Children.OfType<Button>().First());
    }

    private enum Kind
    {
        New,
        Dead,
        Survived,
    }

    private void OpenType()
    {
        if (Current is SnapshotPage { Snapshot: var snapshot } && types.SelectedItem is HeapType type)
        {
            IdeErrors.Observe(OpenSetAsync(() => snapshot.ObjectsOf(type), 0));
        }
    }

    private void OpenDelta(Kind kind)
    {
        if (Current is ComparisonPage { Comparison: var comparison } && deltas.SelectedItem is TypeDelta type)
        {
            IdeErrors.Observe(OpenSetAsync(kind switch
            {
                Kind.New => () => comparison.NewObjectsOf(type),
                Kind.Dead => () => comparison.DeadObjectsOf(type),
                _ => () => comparison.SurvivedObjectsOf(type),
            }, 0));
        }
    }

    private void OpenObject()
    {
        if (Current is SetPage { Set: var set } && objects.SelectedItem is HeapObject item)
        {
            IdeErrors.Observe(OpenSetAsync(() => set.Snapshot.ObjectAt(item.Address)!, 1));
        }
    }

    /// <summary>Opens an object set, found with its paths off the UI thread, on one of its views.</summary>
    private async Task OpenSetAsync(Func<ObjectSet> find, int tab)
    {
        List<Page> from = [.. trail];
        int load = Begin("Building the shortest paths to roots…");
        SetPage page = await BackgroundWork.Run(() =>
        {
            ObjectSet set = find();
            return new SetPage(set, set.ShortestPathsToRoots(), set.KeyRetentionPaths(), tab);
        });
        if (load == loading)
        {
            Go([.. from, page]);
        }
    }

    private void ShowTab(int tab)
    {
        for (int i = 0; i < setTabs.Length; i++)
        {
            setTabs[i].IsChecked = i == tab;
        }
        objectsPage.IsVisible = tab == 0;
        pathsPage.IsVisible = tab == 1;
        keyPathsPage.IsVisible = tab == 2;
        if (Current is SetPage page && page.Tab != tab)
        {
            trail[^1] = page with { Tab = tab };
        }
    }

    /// <summary>The paths' steps, the set's most retaining path open down to its root.</summary>
    private void ShowPaths(RetentionNode top)
    {
        pathRows.Clear();
        RetentionRow row = new(top, 0);
        pathRows.Add(row);
        while (row.Node.Children.Count > 0)
        {
            Expand(row);
            row = pathRows[pathRows.IndexOf(row) + 1];
        }
        paths.SelectedItem = row;
    }

    /// <summary>Opens or closes a step of the paths.</summary>
    public void Toggle(RetentionRow row)
    {
        if (row.IsExpanded)
        {
            Collapse(row);
        }
        else
        {
            Expand(row);
        }
    }

    private void Expand(RetentionRow row)
    {
        if (row.IsExpanded || !row.Expands)
        {
            return;
        }
        row.IsExpanded = true;
        pathRows.Open(row, TreeRows<RetentionRow>.Shown(row.Steps, r => r.Steps));
    }

    private void Collapse(RetentionRow row)
    {
        row.IsExpanded = false;
        pathRows.Close(row);
    }

    /// <summary>Enter on a row opens it, as a double-click does; the row takes Enter as handled already, so it is heard
    /// handled or not.</summary>
    private static void OnEnter(ListBox list, Action open)
    {
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                open();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        list.DoubleTapped += (_, _) => open();
    }

    /// <summary>A column's header clicked: the list by that column, the most first (the type's name from A); again, the other way.</summary>
    private void SortTypes(int column)
    {
        if (Current is not SnapshotPage { Snapshot: var snapshot })
        {
            return;
        }
        typeSort = typeSort == column + 1 ? -(column + 1) : column + 1;
        IEnumerable<HeapType> sorted = column switch
        {
            0 => snapshot.Types.OrderBy(t => t.Name, StringComparer.Ordinal),
            1 => snapshot.Types.OrderByDescending(t => t.Count),
            2 => snapshot.Types.OrderByDescending(t => t.Bytes),
            _ => snapshot.Types.OrderByDescending(t => t.RetainedBytes),
        };
        types.ItemsSource = typeSort < 0 ? [.. sorted.Reverse()] : sorted.ToList();
    }

    private void SortDeltas(int column)
    {
        if (Current is not ComparisonPage { Comparison: var comparison })
        {
            return;
        }
        deltaSort = deltaSort == column + 1 ? -(column + 1) : column + 1;
        Func<TypeDelta, long> key = column switch
        {
            1 => t => t.ObjectsDelta,
            2 => t => t.BytesDelta,
            3 => t => t.NewObjects,
            4 => t => t.NewBytes,
            5 => t => t.DeadObjects,
            6 => t => t.DeadBytes,
            _ => t => t.SurvivedObjects,
        };
        IEnumerable<TypeDelta> sorted = column == 0 ? comparison.Types.OrderBy(t => t.Name, StringComparer.Ordinal) : comparison.Types.OrderByDescending(key);
        deltas.ItemsSource = deltaSort < 0 ? [.. sorted.Reverse()] : sorted.ToList();
    }

    private ToggleButton Tab(string label, int index)
    {
        ToggleButton tab = new() { Content = label, Classes = { "panelTab" } };
        tab.Click += (_, _) => ShowTab(index);
        return tab;
    }

    private Button Action(string label, Action run)
    {
        Button button = new() { Content = label, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => run();
        actions.Children.Add(button);
        return button;
    }

    private static ListBox List<T>(Func<T, Control> row)
    {
        ListBox list = new()
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<T>((item, _) => item is null ? new TextBlock() : row(item), supportsRecycling: false),
        };
        // Rows fill the list's width, so their columns line up under the header's.
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        });
        return list;
    }

    private static DockPanel Framed(Control header, ListBox list)
    {
        Border band = new() { [DockProperty] = Dock.Top, BorderThickness = new Thickness(0, 0, 0, 1), Child = header, Padding = new Thickness(0, 0, 0, 3) };
        band.Bind(Border.BorderBrushProperty, band.GetResourceObservable(TokenKeys.Divider));
        return new DockPanel { Children = { band, list } };
    }

    private Control PathStep(RetentionRow row)
    {
        PathIcon chevron = new() { Classes = { "chevron" }, IsVisible = row.Node.Children.Count > 0, RenderTransformOrigin = RelativePoint.Center };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(IconKeys.ChevronRight));
        chevron.RenderTransform = new RotateTransform(row.IsExpanded ? 90 : 0);
        row.FollowWhileShown(chevron, (_, _) => chevron.RenderTransform = new RotateTransform(row.IsExpanded ? 90 : 0));
        Border toggle = new() { Width = 18, Height = ListRow.Height, Background = Brushes.Transparent, Child = chevron };
        toggle.PointerPressed += (_, e) =>
        {
            Toggle(row);
            e.Handled = true;
        };
        TextBlock text = Cell(row.Node.Text);
        if (row.Node.Root is { } root)
        {
            text.Text = null;
            text.Inlines = new Avalonia.Controls.Documents.InlineCollection { new Avalonia.Controls.Documents.Run(root.KindName) { FontWeight = FontWeight.SemiBold } };
            if (root.Name.Length > 0)
            {
                text.Inlines.Add(new Avalonia.Controls.Documents.Run($": {root.Name}"));
            }
        }
        else if (row.Depth == 0)
        {
            text.FontWeight = FontWeight.SemiBold;
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(row.Depth * 14, 0, 0, 0), Children = { toggle, text } };
    }

    private static Control SnapshotRow(MemorySnapshot snapshot) => new StackPanel
    {
        Margin = new Thickness(2, 4),
        Children =
        {
            new TextBlock { Text = snapshot.Name, FontWeight = FontWeight.SemiBold },
            Muted($"{snapshot.Taken:HH:mm:ss} · {snapshot.Objects:N0} objects · {Size(snapshot.Bytes)}"),
        },
    };

    /// <summary>A name, cut short with an ellipsis where its column ends, and whole in its tooltip.</summary>
    private static TextBlock Cell(string text)
    {
        TextBlock block = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(block, text);
        return block;
    }

    /// <summary>An object: its type, then its address in the code font, muted.</summary>
    private static TextBlock ObjectCell(HeapObject item)
    {
        TextBlock block = Cell(item.Type);
        Avalonia.Controls.Documents.Run address = new($"  0x{item.Address:x}") { FontFamily = CSharp.Fonts.Mono, FontSize = 12 };
        address.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, block.GetResourceObservable(TokenKeys.TextMuted));
        block.Text = null;
        block.Inlines = new Avalonia.Controls.Documents.InlineCollection { new Avalonia.Controls.Documents.Run(item.Type), address };
        return block;
    }

    private static TextBlock Number(string text)
    {
        TextBlock block = new() { Text = text, FontFamily = CSharp.Fonts.Mono, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (text is "0" or "0 B")
        {
            block.Classes.Add("muted");
        }
        return block;
    }

    private static TextBlock Muted(string text)
    {
        TextBlock block = MutedText.Of(text);
        block.FontSize = 12;
        return block;
    }

    /// <summary>Bytes, plain under a kilobyte, then in KB and MB.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / 1048576.0:N2} MB",
    };

    private static string Signed(long value, Func<long, string> format) => value switch
    {
        > 0 => $"+{format(value)}",
        < 0 => $"−{format(-value)}",
        _ => "0",
    };

    /// <summary>A list's columns: the first takes what the others leave.</summary>
    private sealed class Table(params (string Title, double Width)[] columns)
    {
        private ColumnDefinitions Definitions() => new(string.Join(",", columns.Select(c => c.Width == 0 ? "*" : $"{c.Width}")));

        public Control Header(Action<int>? sort)
        {
            // The list's padding and its rows', so the titles stand over their columns.
            Grid header = new() { ColumnDefinitions = Definitions(), Margin = new Thickness(10, 0) };
            for (int i = 0; i < columns.Length; i++)
            {
                TextBlock title = Muted(columns[i].Title);
                title.TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right;
                title.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                Control cell = title;
                if (sort is not null)
                {
                    int column = i;
                    Button button = new() { Classes = { "crumb" }, Content = title, HorizontalAlignment = title.HorizontalAlignment, Padding = new Thickness(0) };
                    button.Click += (_, _) => sort(column);
                    cell = button;
                }
                Grid.SetColumn(cell, i);
                header.Children.Add(cell);
            }
            return header;
        }

        public Grid Row(params Control[] cells)
        {
            Grid row = new() { ColumnDefinitions = Definitions(), Height = ListRow.Height };
            for (int i = 0; i < cells.Length; i++)
            {
                Grid.SetColumn(cells[i], i);
                row.Children.Add(cells[i]);
            }
            return row;
        }
    }

    private abstract record Page(string Crumb);

    private sealed record SnapshotPage(MemorySnapshot Snapshot) : Page(Snapshot.Name);

    private sealed record ComparisonPage(SnapshotComparison Comparison) : Page(Comparison.Title);

    private sealed record SetPage(ObjectSet Set, RetentionNode Paths, IReadOnlyList<RetentionPath> KeyPaths, int Tab) : Page(Set.Title);
}
