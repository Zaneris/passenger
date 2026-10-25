using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Profiler;
using Passenger.Core;
using Passenger.App.ToolWindows;

namespace Passenger.App.Profiling;

/// <summary>A row of the call tree or of a type's back traces: a node, how deep it sits, and whether its callees are listed
/// under it.</summary>
public sealed class CallRow(CallNode node, int depth, TreeRows<CallRow> list, string unit, int whole, double scale = 1) : Observable, ITreeRow
{
    private bool isExpanded;
    private List<CallRow>? callees;

    public CallNode Node { get; } = node;
    public int Depth { get; } = depth;

    /// <summary>The rows the row is in, which its callees join when it opens.</summary>
    public TreeRows<CallRow> List { get; } = list;

    /// <summary>What its weight counts (ms of samples, KB allocated), and the whole a share is of.</summary>
    public string Unit { get; } = unit;
    public int Whole { get; } = whole;

    /// <summary>How much of the unit one count of the weight is: a sample's milliseconds (<see cref="Snapshot.SampleMs"/>).</summary>
    public double Scale { get; } = scale;

    public bool Expands => Node.Children.Count > 0;

    /// <summary>Its callees' rows, the heaviest first, made the first time it opens.</summary>
    public IReadOnlyList<CallRow> Callees => callees ??= [.. Node.Ordered.Select(c => new CallRow(c, Depth + 1, List, Unit, Whole, Scale))];

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }
}

/// <summary>The profiler window's pages, in the order of their tabs.</summary>
public enum ProfilerPage
{
    CallTree,
    HotSpots,
    FlameGraph,
    Frames,
    Allocations,
    Collections,
    Memory,
    Snapshots,
}

/// <summary>The profiler's tool window: while a run goes, its time and Get Snapshot (which ends the run and leaves the
/// program going) and Kill; then the snapshot as a call tree by thread with the hot path opened, the hot spots by their
/// own time, a flame graph, or an Avalonia app's frames (<see cref="FramesView"/>). A double-click on a method goes to its
/// source. The Memory page shows the heap (<see cref="MemoryView"/>); the Snapshots page lists the open folder's earlier
/// runs.</summary>
public sealed class ProfilerView : DockPanel
{
    private readonly TreeRows<CallRow> rows = [];
    private readonly ListBox tree;
    private readonly ListBox spots;
    private readonly TreeRows<CallRow> traces = [];
    private readonly ListBox types;
    private readonly ListBox backTraces;
    private readonly Grid allocations;
    private readonly TextBlock collectionsSummary;
    private readonly ListBox collections;
    private readonly DockPanel garbage;
    private readonly MemoryView memory;
    private readonly FramesView frames;
    private readonly Button heap;
    private readonly FlameGraph flame;
    private readonly ScrollViewer flameScroll;
    private readonly ListBox kept;
    private readonly TextBlock keptSummary;
    private readonly DockPanel keptPage;
    private string? keptFolder;
    private readonly TextBlock status;
    private readonly TextBlock placeholder;
    private readonly Button stop;
    private readonly Button kill;
    private readonly ToggleButton[] pages;
    private readonly DispatcherTimer ticking;
    private readonly Timeline timeline;
    private readonly Border timelineBand;
    private string snapshotName = string.Empty;
    private Func<TimeSpan>? elapsed;
    private string running = string.Empty;

    public ProfilerView()
    {
        tree = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<CallRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        tree.DoubleTapped += (_, _) =>
        {
            if (tree.SelectedItem is CallRow row)
            {
                Open(row.Node.Name);
            }
        };
        TreeKeys.Attach<CallRow>(tree, Toggle, OpenRow);
        spots = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<HotSpot>((spot, _) => spot is null ? new TextBlock() : Spot(spot), supportsRecycling: false),
        };
        spots.DoubleTapped += (_, _) =>
        {
            if (spots.SelectedItem is HotSpot spot)
            {
                Open(spot.Name);
            }
        };
        types = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<AllocatedType>((type, _) => type is null ? new TextBlock() : Allocated(type), supportsRecycling: false),
        };
        types.SelectionChanged += (_, _) => ShowBackTraces();
        backTraces = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = traces,
            ItemTemplate = new FuncDataTemplate<CallRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        TreeKeys.Attach<CallRow>(backTraces, Toggle, OpenRow);
        backTraces.DoubleTapped += (_, _) =>
        {
            if (backTraces.SelectedItem is CallRow row)
            {
                Open(row.Node.Name);
            }
        };
        allocations = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1,3*") };
        HairlineDivider between = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        Grid.SetColumn(backTraces, 2);
        allocations.Children.Add(types);
        allocations.Children.Add(between);
        allocations.Children.Add(backTraces);
        collectionsSummary = new TextBlock { Margin = new Thickness(12, 8), TextWrapping = TextWrapping.Wrap };
        collections = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<Collection>((gc, _) => gc is null ? new TextBlock() : CollectionRow(gc), supportsRecycling: false),
        };
        garbage = new DockPanel();
        DockPanel.SetDock(collectionsSummary, Dock.Top);
        garbage.Children.Add(collectionsSummary);
        garbage.Children.Add(collections);
        memory = new MemoryView();
        frames = new FramesView();
        flame = new FlameGraph();
        flameScroll = new ScrollViewer { Content = flame, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        kept = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<SnapshotFile>((file, _) => file is null ? new TextBlock() : KeptRow(file), supportsRecycling: false),
        };
        kept.DoubleTapped += (_, _) => OpenKept();
        kept.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenKept();
                e.Handled = true;
            }
        };
        keptSummary = new TextBlock { Margin = new Thickness(12, 8), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Button reveal = new()
        {
            Content = FileManager.Showing,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        reveal.Click += (_, _) => RevealSnapshotsRequested?.Invoke(this, EventArgs.Empty);
        DockPanel keptHead = new() { [DockProperty] = Dock.Top };
        DockPanel.SetDock(reveal, Dock.Right);
        keptHead.Children.Add(reveal);
        keptHead.Children.Add(keptSummary);
        keptPage = new DockPanel();
        keptPage.Children.Add(keptHead);
        keptPage.Children.Add(kept);
        placeholder = new TextBlock
        {
            Text = "Profile runs the chosen configuration under the profiler; Get Snapshot shows where its time went",
            Classes = { "emptyText" },
        };

        status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
        stop = new Button { Name = "ProfilerGetSnapshot", Content = "Get Snapshot", Classes = { "primary" }, IsVisible = false };
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        kill = new Button { Content = "Kill", IsVisible = false };
        kill.Click += (_, _) => KillRequested?.Invoke(this, EventArgs.Empty);
        pages = [PageButton("Call Tree"), PageButton("Hot Spots"), PageButton("Flame Graph"), PageButton("Frames"), PageButton("Allocations"), PageButton("GC"),
            PageButton("Memory"), PageButton("Snapshots")];
        // Named for a screenshot's --click-name: ProfilerHotSpots, ProfilerMemory and the rest.
        foreach (ProfilerPage page in Enum.GetValues<ProfilerPage>())
        {
            pages[(int)page].Name = $"Profiler{page}";
        }
        StackPanel left = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (ToggleButton page in pages)
        {
            left.Children.Add(page);
        }
        Button open = ToolButtons.Of(IconKeys.FolderOpen, "Open Snapshot", () => OpenSnapshotRequested?.Invoke(this, EventArgs.Empty));
        heap = new Button { Name = "ProfilerGetMemorySnapshot", Content = "Get Memory Snapshot", IsVisible = false };
        heap.Click += (_, _) => MemoryRequested?.Invoke(this, EventArgs.Empty);
        StackPanel right = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { heap, stop, kill, open } };
        DockPanel bar = new();
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(right);
        bar.Children.Add(status);
        Border top = ToolBars.Top(bar);
        Children.Add(top);
        timeline = new Timeline();
        timeline.Chosen += (_, stretch) => IdeErrors.Observe(ChooseAsync(stretch));
        timelineBand = new Border { [DockProperty] = Dock.Top, BorderThickness = new Thickness(0, 0, 0, 1), Child = timeline, IsVisible = false, Padding = new Thickness(0, 4) };
        timelineBand.Bind(Border.BorderBrushProperty, timelineBand.GetResourceObservable(TokenKeys.Divider));
        Children.Add(timelineBand);
        Children.Add(new Panel { Children = { tree, spots, flameScroll, frames, allocations, garbage, memory, keptPage, placeholder } });
        ticking = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        ticking.Tick += (_, _) => status.Text = $"Profiling {running}: {elapsed?.Invoke().TotalSeconds:0.0} s";
        ShowPage(ProfilerPage.CallTree);
        foreach (ToggleButton page in pages)
        {
            page.IsEnabled = false;
        }
    }

    /// <summary>The Frames page: an Avalonia app's frames and their layout by control.</summary>
    public FramesView FramesPage => frames;

    /// <summary>Get Snapshot pressed: end the run and show what it found.</summary>
    public event EventHandler? StopRequested;

    public event EventHandler? KillRequested;

    /// <summary>Get Memory Snapshot pressed: the running program's heap to read.</summary>
    public event EventHandler? MemoryRequested;

    /// <summary>The last memory snapshot taken, or null.</summary>
    public MemorySnapshot? Memory { get; private set; }

    /// <summary>The Memory page: the run's memory snapshots, their comparisons and object sets.</summary>
    public MemoryView MemoryPage => memory;

    /// <summary>The Memory page's types as listed.</summary>
    public IReadOnlyList<HeapType> HeapTypes => memory.TypeRows;

    /// <summary>Lists a memory snapshot on the Memory page and shows it.</summary>
    public void ShowMemory(MemorySnapshot snapshot)
    {
        Memory = snapshot;
        memory.Add(snapshot);
        pages[(int)ProfilerPage.Memory].IsEnabled = true;
        ShowPage(ProfilerPage.Memory);
    }

    /// <summary>The open folder's snapshots, the newest first, as the Snapshots page lists them.</summary>
    public IReadOnlyList<SnapshotFile> Kept => kept.ItemsSource as IReadOnlyList<SnapshotFile> ?? [];

    /// <summary>The Snapshots page's line saying where they are kept.</summary>
    public string KeptSummary => keptSummary.Text ?? string.Empty;

    /// <summary>A kept snapshot chosen to open: its path.</summary>
    public event EventHandler<string>? OpenKeptRequested;

    /// <summary>The folder the snapshots are kept in, to show in the file manager.</summary>
    public event EventHandler? RevealSnapshotsRequested;

    /// <summary>Lists the snapshots kept for the open folder on the Snapshots page; with nothing else to show, the window
    /// opens on them.</summary>
    public void ShowKept(string folder, IReadOnlyList<SnapshotFile> files)
    {
        keptFolder = folder;
        kept.ItemsSource = files;
        keptSummary.Text = files.Count == 0
            ? $"No snapshots yet. Each run's is kept in {folder}, the newest {Snapshots.Kept}."
            : $"{Plural.Of(files.Count, "snapshot")}, the newest first, kept in {folder}. Past {Snapshots.Kept}, the oldest go.";
        pages[(int)ProfilerPage.Snapshots].IsEnabled = true;
        int current = Array.FindIndex(pages, p => p.IsChecked == true);
        ShowPage(Snapshot is null && Memory is null && !ticking.IsEnabled && files.Count > 0 ? ProfilerPage.Snapshots : (ProfilerPage)Math.Max(0, current));
    }

    private void OpenKept()
    {
        if (kept.SelectedItem is SnapshotFile file)
        {
            OpenKeptRequested?.Invoke(this, file.Path);
        }
    }

    private static Control KeptRow(SnapshotFile file)
    {
        TextBlock when = Muted(file.Written.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture), 160);
        when.FontFamily = CSharp.Fonts.Mono;
        when.FontSize = 12;
        TextBlock size = Muted($"{file.Bytes / 1048576.0,7:N1} MB", 100);
        size.FontFamily = CSharp.Fonts.Mono;
        size.FontSize = 12;
        // Named for its program and when it ran, which the row says already.
        string program = System.Text.RegularExpressions.Regex.Replace(file.Name, @"-\d{8}-\d{6}(-\d+)?$", string.Empty);
        return new ShrinkingRow
        {
            Height = ListRow.Height,
            Margin = new Thickness(6, 0),
            Children = { when, size, new TextBlock { Text = program, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } },
        };
    }

    /// <summary>Open Snapshot pressed: a .nettrace file to read.</summary>
    public event EventHandler? OpenSnapshotRequested;

    /// <summary>A method to find in the source, by its name as the profiler gives it.</summary>
    public event EventHandler<string>? OpenRequested;

    public Snapshot? Snapshot { get; private set; }

    /// <summary>The call tree's rows as listed now.</summary>
    public IReadOnlyList<CallRow> Rows => rows;

    public FlameGraph Flame => flame;

    public string Status => status.Text ?? string.Empty;

    /// <summary>A run is starting, its program not yet at the profiler: Kill ends it, and nothing is recorded to snapshot.</summary>
    public void Starting(string name)
    {
        stop.IsVisible = false;
        heap.IsVisible = false;
        kill.IsVisible = true;
        status.Text = $"Starting {name} under the profiler…";
    }

    /// <summary>A run has begun; one attached to a program Profile did not start has no Kill, the program being another's.</summary>
    public void Running(string name, Func<TimeSpan> clock, bool killable = true)
    {
        running = name;
        elapsed = clock;
        stop.IsVisible = true;
        kill.IsVisible = killable;
        heap.IsVisible = true;
        placeholder.Text = $"Profiling {name}. Get Snapshot when it has done what is to be measured.";
        ticking.Start();
        if (Snapshot is null)
        {
            ShowPage(ProfilerPage.CallTree);
        }
    }

    /// <summary>The run is over, or never began; when it found nothing to show, says why, in the window's middle while
    /// it has no snapshot to show.</summary>
    public void Stopped(string? why = null)
    {
        ticking.Stop();
        stop.IsVisible = false;
        kill.IsVisible = false;
        heap.IsVisible = false;
        if (why is not null)
        {
            status.Text = why;
            if (Snapshot is null)
            {
                placeholder.Text = why;
                ShowPage(ProfilerPage.CallTree);
            }
        }
    }

    /// <summary>Kill shown or not whatever the run: the program Profile started goes on after its snapshot, until it ends.</summary>
    public void ShowKill(bool shown) => kill.IsVisible = shown;

    /// <summary>Whether Kill is there.</summary>
    public bool CanKill => kill.IsVisible;

    /// <summary>Whether the window follows a run as it goes: its time ticking, Get Snapshot there.</summary>
    public bool IsFollowing => ticking.IsEnabled;

    /// <summary>What the window's middle says while it has nothing on the page shown.</summary>
    public string Placeholder => placeholder.IsVisible ? placeholder.Text ?? string.Empty : string.Empty;

    public void Show(Snapshot snapshot, string name)
    {
        Stopped();
        snapshotName = name;
        choosing++;
        timeline.Snapshot = snapshot;
        timelineBand.IsVisible = true;
        // A snapshot opened from the list is shown, not the list again.
        if (pages[(int)ProfilerPage.Snapshots].IsChecked == true)
        {
            pages[(int)ProfilerPage.Snapshots].IsChecked = false;
            pages[(int)ProfilerPage.CallTree].IsChecked = true;
        }
        Fill(snapshot);
    }

    /// <summary>The timeline, where a drag chooses the stretch the other views show.</summary>
    public Timeline Timeline => timeline;

    /// <summary>Which stretch was chosen last, so one worked out after another was chosen is dropped.</summary>
    private int choosing;

    /// <summary>Shows a stretch of the run, or all of it. A stretch's trees are worked out off the UI thread, which a long
    /// run's would hold for a while; the window shows the run as it was until they land.</summary>
    private async Task ChooseAsync((double From, double To)? stretch)
    {
        if (timeline.Snapshot is not { } whole)
        {
            return;
        }
        int chosen = ++choosing;
        if (stretch is not { } span)
        {
            Fill(whole);
            return;
        }
        status.Text = $"{snapshotName}: reading {span.From - whole.StartMs:N0} to {span.To - whole.StartMs:N0} ms…";
        Snapshot within = await BackgroundWork.Run(() => whole.Within(span.From, span.To));
        if (chosen == choosing)
        {
            Fill(within);
        }
    }

    /// <summary>Shows a snapshot, the whole run's or a stretch of it, in every view but the timeline.</summary>
    private void Fill(Snapshot snapshot)
    {
        ProfilerPage current = (ProfilerPage)Math.Max(0, Array.FindIndex(pages, p => p.IsChecked == true));
        Snapshot = snapshot;
        Snapshot whole = snapshot.Whole;
        status.Text = ReferenceEquals(whole, snapshot)
            ? $"{snapshotName}: {snapshot.Duration.TotalSeconds:0.0} s, {Plural.Of(snapshot.Threads.Count, "thread")}, {snapshot.Samples:N0} samples"
            : $"{snapshotName}: {snapshot.StartMs - whole.StartMs:N0} to {snapshot.EndMs - whole.StartMs:N0} ms of {whole.Duration.TotalMilliseconds:N0}, {snapshot.Samples:N0} samples";
        foreach (ToggleButton page in pages)
        {
            page.IsEnabled = true;
        }
        rows.Clear();
        foreach (CallNode thread in snapshot.Threads)
        {
            rows.Add(new CallRow(thread, 0, rows, "ms", snapshot.Samples, snapshot.SampleMs));
        }
        if (rows.Count > 0)
        {
            CallRow at = rows[0];
            for (int depth = 0; depth < 60 && at.Node.Children.Count > 0; depth++)
            {
                Expand(at);
                CallNode heaviest = at.Node.Ordered[0];
                if (heaviest.Total * 2 < at.Node.Total && depth > 0)
                {
                    break;
                }
                at = rows.First(r => r.Node == heaviest);
            }
            tree.SelectedItem = at;
        }
        spots.ItemsSource = snapshot.HotSpots;
        flame.SampleMs = snapshot.SampleMs;
        flame.Root = snapshot.All;
        frames.Show(snapshot.UiFrames, whole.StartMs, snapshot.Place is { } elsewhere ? FramesView.InContainer(elsewhere.Where) : null);
        ShowPageLater(current);
        types.ItemsSource = snapshot.Allocations;
        types.SelectedIndex = snapshot.Allocations.Count > 0 ? 0 : -1;
        ShowBackTraces();
        IReadOnlyList<Collection> gcs = snapshot.Collections;
        collections.ItemsSource = gcs;
        collectionsSummary.Text = gcs.Count == 0
            ? "No garbage collections ran"
            : $"{gcs.Count} collections ({string.Join(", ", gcs.GroupBy(c => c.Generation).OrderBy(g => g.Key).Select(g => $"{g.Count()} of generation {g.Key}"))}), "
              + $"the program paused {gcs.Sum(c => c.PauseMs):0.0} ms in all, {gcs.Max(c => c.PauseMs):0.0} ms at the longest";
        ShowPage(shownPage);
    }

    private ProfilerPage shownPage;

    private void ShowPageLater(ProfilerPage page) => shownPage = page;

    /// <summary>The types allocated, the most first.</summary>
    public IReadOnlyList<AllocatedType> Types => types.ItemsSource as IReadOnlyList<AllocatedType> ?? [];

    /// <summary>The chosen type's back traces as listed now.</summary>
    public IReadOnlyList<CallRow> BackTraces => traces;

    /// <summary>The GC page's summary line.</summary>
    public string CollectionsSummary => collectionsSummary.Text ?? string.Empty;

    /// <summary>Chooses a type on the Allocations page.</summary>
    public void SelectType(AllocatedType type) => types.SelectedItem = type;

    private void ShowBackTraces()
    {
        traces.Clear();
        if (types.SelectedItem is not AllocatedType type)
        {
            return;
        }
        CallRow root = new(type.BackTraces, 0, traces, "KB", type.BackTraces.Total);
        traces.Add(root);
        Expand(root);
        CallRow at = root;
        for (int depth = 0; depth < 12 && at.Node.Children.Count > 0; depth++)
        {
            CallNode heaviest = at.Node.Ordered[0];
            at = traces.First(r => r.Node == heaviest);
            Expand(at);
        }
    }

    private Control Allocated(AllocatedType type)
    {
        long whole = Snapshot?.Allocations.Sum(a => a.Bytes) ?? 0;
        TextBlock size = Muted($"{(whole > 0 ? 100.0 * type.Bytes / whole : 0),5:0.0}%  {type.Bytes / 1048576.0,8:N1} MB", 170);
        size.FontFamily = CSharp.Fonts.Mono;
        size.FontSize = 12;
        return new ShrinkingRow
        {
            Height = ListRow.Height,
            Margin = new Thickness(6, 0),
            Children = { size, new TextBlock { Text = type.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } },
        };
    }

    private static Control CollectionRow(Collection gc)
    {
        TextBlock what = new() { Text = $"#{gc.Number}  generation {gc.Generation}", Width = 170, VerticalAlignment = VerticalAlignment.Center };
        TextBlock when = Muted($"at {gc.AtMs:N0} ms", 110);
        TextBlock pause = Muted($"paused {gc.PauseMs:0.00} ms", 140);
        TextBlock why = Muted(gc.Reason, double.NaN);
        return new ShrinkingRow { Height = ListRow.Height, Margin = new Thickness(6, 0), Children = { what, when, pause, why } };
    }

    public void Toggle(CallRow row)
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

    private static void Expand(CallRow row)
    {
        if (row.IsExpanded || !row.Expands)
        {
            return;
        }
        row.IsExpanded = true;
        row.List.Open(row, TreeRows<CallRow>.Shown(row.Callees, r => r.Callees));
    }

    private static void Collapse(CallRow row)
    {
        row.IsExpanded = false;
        row.List.Close(row);
    }

    /// <summary>A row's method opened at its source; false for a thread or a type, at the top, which stand for no method.</summary>
    private bool OpenRow(CallRow row) => row.Depth > 0 && Open(row.Node.Name);

    /// <summary>Goes to a method's source; false for a row no source has (a thread, the runtime's own frames).</summary>
    private bool Open(string method)
    {
        if (method.StartsWith('[') || method.StartsWith("Thread ", StringComparison.Ordinal))
        {
            return false;
        }
        OpenRequested?.Invoke(this, method);
        return true;
    }

    private ToggleButton PageButton(string label)
    {
        ToggleButton button = new() { Content = label, Classes = { "panelTab" } };
        button.Click += (_, _) => ShowPage((ProfilerPage)Array.IndexOf(pages, button));
        return button;
    }

    /// <summary>Shows a page; the placeholder's words when it has nothing to show.</summary>
    public void ShowPage(ProfilerPage page)
    {
        Control[] views = [tree, spots, flameScroll, frames, allocations, garbage, memory, keptPage];
        bool shown = false;
        for (int i = 0; i < views.Length; i++)
        {
            bool showing = i == (int)page && ((ProfilerPage)i switch
            {
                ProfilerPage.Memory => Memory is not null,
                ProfilerPage.Snapshots => keptFolder is not null,
                _ => Snapshot is not null,
            });
            views[i].IsVisible = showing;
            shown |= showing;
            pages[i].IsChecked = i == (int)page;
        }
        placeholder.IsVisible = !shown;
        // The run's timeline goes with its views, not with the list of runs; the Frames page has its frames' own.
        timelineBand.IsVisible = timeline.Snapshot is not null && page is not (ProfilerPage.Snapshots or ProfilerPage.Frames);
    }

    private string Share(int samples) => Snapshot is { Samples: > 0 } shot ? $"{100.0 * samples / shot.Samples,5:0.0}%" : "";

    private Control Row(CallRow row)
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
        string percent = row.Whole > 0 ? $"{100.0 * row.Node.Total / row.Whole,5:0.0}%" : string.Empty;
        TextBlock share = Muted($"{percent}  {row.Node.Total * row.Scale,7:N0} {row.Unit}", 170);
        share.FontFamily = CSharp.Fonts.Mono;
        share.FontSize = 12;
        return new ShrinkingRow
        {
            Height = ListRow.Height,
            Margin = new Thickness(row.Depth * 14, 0, 0, 0),
            Children = { toggle, share, MethodName(row.Node.Name, row.Depth == 0) },
        };
    }

    private Control Spot(HotSpot spot)
    {
        double sampleMs = Snapshot?.SampleMs ?? 1;
        TextBlock own = Muted($"{Share(spot.Self)}  {spot.Self * sampleMs,7:N0} ms", 170);
        own.FontFamily = CSharp.Fonts.Mono;
        own.FontSize = 12;
        TextBlock total = Muted($"{spot.Total * sampleMs,7:N0} ms in all", 110);
        total.FontSize = 12;
        return new ShrinkingRow { Height = ListRow.Height, Margin = new Thickness(6, 0), Children = { own, total, MethodName(spot.Name, false) } };
    }

    /// <summary>A method's name: its own strong, its type's and namespace's muted before it.</summary>
    private static Control MethodName(string name, bool thread)
    {
        if (thread || name.StartsWith('['))
        {
            return new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontWeight = thread ? FontWeight.SemiBold : FontWeight.Normal };
        }
        string bare = name.Split('(')[0];
        int dot = bare.LastIndexOf('.');
        string owner = dot > 0 ? bare[..(dot + 1)] : string.Empty;
        string method = name[owner.Length..];
        TextBlock before = Muted(owner, double.NaN);
        return new ShrinkingRow
        {
            Children = { before, new TextBlock { Text = method, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } },
        };
    }

    private static TextBlock Muted(string text, double width)
    {
        TextBlock block = MutedText.Of(text);
        block.Width = width;
        return block;
    }
}
