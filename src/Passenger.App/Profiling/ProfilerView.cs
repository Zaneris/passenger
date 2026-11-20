using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Editor;
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

/// <summary>A method a row of the profiler window names: as the profiler gives it, and its module and token, which find
/// its source by its PDB; none for a native frame.</summary>
public readonly record struct ProfiledMethod(string Name, MethodCode? Code);

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
    private readonly TreeList tree;
    private readonly ListBox spots;
    private readonly TreeRows<CallRow> traces = [];
    private readonly ListBox types;
    private readonly TreeList backTraces;
    private readonly PaneSplit allocations;
    private readonly TextBlock collectionsSummary;
    private readonly ListBox collections;
    private readonly DockPanel garbage;
    private readonly Banner offer = new();
    private readonly Button offerLink;

    /// <summary>The console whose offer the window shows over its run, while it shows one.</summary>
    private OutputView? offering;
    private readonly MemoryView memory;
    private readonly FramesView frames;
    private readonly GameFramesView game;
    private readonly Panel framesPage;
    private readonly Button heap;
    private readonly FlameGraph flame;
    private readonly ScrollViewer flameScroll;
    private readonly ListBox kept;
    private readonly TextBlock keptSummary;
    private readonly DockPanel keptPage;
    private string? keptFolder;
    private readonly TextBlock status;
    private readonly EmptyState placeholder;
    private readonly Button stop;
    private readonly Button kill;
    private readonly ToolTabs.Strip<ProfilerPage> pages = new() { Margin = new Thickness(Gaps.Small, 0) };
    private readonly DispatcherTimer ticking;
    private readonly Timeline timeline;
    private readonly Border timelineBand;
    private string snapshotName = string.Empty;
    private Func<TimeSpan>? elapsed;
    private string running = string.Empty;

    public ProfilerView()
    {
        tree = new TreeList
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = rows,
        };
        ReusedRows.Use(tree, Row);
        TreeKeys.Attach<CallRow>(tree, Toggle, OpenRow);
        spots = new ListBox
        {
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(spots, Spot);
        ListKeys.Act(spots, new ListActs { Open = OpenSpot, Source = OpenSpot });
        types = new ListBox
        {
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(types, Allocated);
        types.SelectionChanged += (_, _) => ShowBackTraces();
        backTraces = new TreeList
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = traces,
        };
        ReusedRows.Use(backTraces, Row);
        TreeKeys.Attach<CallRow>(backTraces, Toggle, row => Open(row.Node.Name, row.Node.Code));
        allocations = new PaneSplit(types, backTraces, "2*,1,3*");
        collectionsSummary = new TextBlock { Margin = new Thickness(Gaps.Wide, Gaps.Normal), TextWrapping = TextWrapping.Wrap };
        collections = new ListBox
        {
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(collections, CollectionRow);
        garbage = new DockPanel();
        DockPanel.SetDock(collectionsSummary, Dock.Top);
        garbage.Children.Add(collectionsSummary);
        garbage.Children.Add(collections);
        memory = new MemoryView();
        frames = new FramesView();
        game = new GameFramesView { IsVisible = false };
        game.FrameOpened += (_, frame) => IdeErrors.Observe(OpenFrameAsync(frame));
        framesPage = new Panel { Children = { frames, game } };
        flame = new FlameGraph();
        flameScroll = new ScrollViewer { Content = flame, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        kept = new ListBox
        {
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(kept, KeptRow);
        ListKeys.Act(kept, new ListActs { Open = OpenKept });
        keptSummary = new TextBlock { Margin = new Thickness(Gaps.Wide, Gaps.Normal), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Button reveal = new()
        {
            Content = FileManager.Showing,
            Margin = new Thickness(0, 0, Gaps.Wide, 0),
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
        placeholder = new EmptyState("The chosen configuration runs under the profiler; Get Snapshot shows where its time went");
        placeholder.Offer(AppActions.Profile, "Profile");
        placeholder.ActionRequested += (_, action) => ActionRequested?.Invoke(this, action);

        // Cut where it is longer than its line, its whole text on hover.
        status = new TextBlock { Name = "ProfilerSummary", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Wide, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        status.Bind(ToolTip.TipProperty, status.GetObservable(TextBlock.TextProperty));
        stop = new Button { Name = "ProfilerGetSnapshot", Content = "Get Snapshot", Classes = { StyleClasses.Primary }, IsVisible = false };
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        kill = new Button { Content = "Kill", IsVisible = false };
        kill.Click += (_, _) => KillRequested?.Invoke(this, EventArgs.Empty);
        string[] titles = ["Call Tree", "Hot Spots", "Flame Graph", "Frames", "Allocations", "GC", "Memory", "Snapshots"];
        foreach (ProfilerPage page in Enum.GetValues<ProfilerPage>())
        {
            // Named for a screenshot's --click-name: ProfilerHotSpots, ProfilerMemory and the rest.
            pages.Add(page, titles[(int)page], name: $"Profiler{page}");
        }
        pages.Chosen += (_, page) => ShowPage(page);
        Button open = ToolButtons.Of(IconKeys.FolderOpen, "Open Snapshot", () => OpenSnapshotRequested?.Invoke(this, EventArgs.Empty));
        open.Name = "ProfilerOpenSnapshot";
        heap = new Button { Name = "ProfilerGetMemorySnapshot", Content = "Get Memory Snapshot", IsVisible = false };
        heap.Click += (_, _) => MemoryRequested?.Invoke(this, EventArgs.Empty);
        ActionButtons.Mark(heap, AppActions.MemorySnapshot);
        StackPanel right = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, Margin = new Thickness(Gaps.Small, 0), VerticalAlignment = VerticalAlignment.Center, Children = { heap, stop, kill, open } };
        DockPanel bar = new();
        DockPanel.SetDock(right, Dock.Right);
        // The run's summary follows the pages, wrapping with them where the pane has no room for both on one line, as at 640 wide.
        pages.Children.Add(status);
        bar.Children.Add(right);
        bar.Children.Add(pages);
        Border top = ToolBars.Top(bar);
        Children.Add(top);
        offerLink = offer.Link(null, () => offering?.TakeOffer());
        Children.Add(offer);
        timeline = new Timeline();
        timeline.Chosen += (_, stretch) => IdeErrors.Observe(ChooseAsync(stretch));
        timelineBand = new Border { [DockProperty] = Dock.Top, BorderThickness = Hairlines.Below, Child = timeline, IsVisible = false, Padding = new Thickness(0, Gaps.Tight) };
        timelineBand.Bind(Border.BorderBrushProperty, timelineBand.GetResourceObservable(TokenKeys.Divider));
        Children.Add(timelineBand);
        Children.Add(new Panel { Children = { tree, spots, flameScroll, framesPage, allocations, garbage, memory, keptPage, placeholder } });
        ticking = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        ticking.Tick += (_, _) => status.Text = $"Profiling {running}: {Durations.Said(elapsed?.Invoke() ?? TimeSpan.Zero)}";
        ShowPage(ProfilerPage.CallTree);
        foreach (ToggleButton page in pages.Toggles)
        {
            page.IsEnabled = false;
        }
    }

    /// <summary>The Frames page: an Avalonia app's frames and their layout by control.</summary>
    public FramesView FramesPage => frames;

    /// <summary>The Frames page of a Godot game's run: its frames as Godot measured them, joined with the C# samples.</summary>
    public GameFramesView GamePage => game;

    /// <summary>The action the empty window offers (Profile), asked for by its link.</summary>
    public event EventHandler<string>? ActionRequested;

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
        pages[ProfilerPage.Memory].IsEnabled = true;
        ShowPage(ProfilerPage.Memory);
    }

    /// <summary>Puts the Memory page's snapshots away, their objects let go: the folder whose runs they were of has ended.</summary>
    public void ForgetMemory()
    {
        Memory = null;
        memory.Clear();
        if (pages.Choice == ProfilerPage.Memory)
        {
            ShowPage(ProfilerPage.Memory);
        }
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
        pages[ProfilerPage.Snapshots].IsEnabled = true;
        ShowPage(Snapshot is null && Memory is null && !ticking.IsEnabled && files.Count > 0 ? ProfilerPage.Snapshots : pages.Choice);
    }

    private void OpenSpot()
    {
        if (spots.SelectedItem is HotSpot spot)
        {
            Open(spot.Name, spot.Code);
        }
    }

    private void OpenKept()
    {
        if (kept.SelectedItem is SnapshotFile file)
        {
            OpenKeptRequested?.Invoke(this, file.Path);
        }
    }

    /// <summary>A row for whichever kept snapshot the list hands its container: when it ran, its size and its program.</summary>
    private static Control KeptRow()
    {
        TextBlock when = Mono(Muted(string.Empty, 160));
        TextBlock size = Mono(Muted(string.Empty, 100));
        TextBlock program = new() { Classes = { StyleClasses.Strong }, VerticalAlignment = VerticalAlignment.Center };
        ShrinkingRow row = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Small, 0), Children = { when, size, program } };
        return row.Showing<SnapshotFile>(file =>
        {
            when.Text = file.Written.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
            size.Text = Sizes.Said(file.Bytes).PadLeft(10);
            // Named for its program and when it ran, which the row says already.
            program.Text = System.Text.RegularExpressions.Regex.Replace(file.Name, @"-\d{8}-\d{6}(-\d+)?$", string.Empty);
        });
    }

    /// <summary>Open Snapshot pressed: a .nettrace file to read.</summary>
    public event EventHandler? OpenSnapshotRequested;

    /// <summary>A method to find in the source: its name as the profiler gives it, and where it is.</summary>
    public event EventHandler<ProfiledMethod>? OpenRequested;

    public Snapshot? Snapshot { get; private set; }

    /// <summary>The call tree's rows as listed now.</summary>
    public IReadOnlyList<CallRow> Rows => rows;

    public FlameGraph Flame => flame;

    public string Status => status.Text ?? string.Empty;

    /// <summary>Offers over the run what a console offers of its program (a terminal, for one needing it), until that
    /// console's offer goes.</summary>
    public void Offer(OutputView console)
    {
        if (offering != console)
        {
            LetGoOfOffer();
            offering = console;
            console.OfferChanged += ShowOffer;
        }
        ShowOffer(console, EventArgs.Empty);
    }

    private void LetGoOfOffer()
    {
        if (offering is not null)
        {
            offering.OfferChanged -= ShowOffer;
            offering = null;
        }
    }

    /// <summary>What the window offers over its run, null while it offers nothing.</summary>
    public string? Offered => offer.Said;

    private void ShowOffer(object? sender, EventArgs e)
    {
        if (offering?.Offered is not { } said)
        {
            LetGoOfOffer();
            offer.Say(null);
            return;
        }
        offerLink.Content = offering.OfferLink;
        offer.Say(said);
    }

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
        placeholder.Offer(null, null);
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
        placeholder.Offer(AppActions.Profile, "Profile");
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
        if (pages.Choice == ProfilerPage.Snapshots)
        {
            pages.Choice = ProfilerPage.CallTree;
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
        // Shown on a later turn of the window's loop even when worked out already, so a choice never fills the views itself.
        Snapshot within = await BackgroundWork.Run(() => whole.Within(span.From, span.To)).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        if (chosen == choosing)
        {
            Fill(within);
        }
    }

    /// <summary>Shows a snapshot, the whole run's or a stretch of it, in every view but the timeline.</summary>
    private void Fill(Snapshot snapshot)
    {
        ProfilerPage current = pages.Choice;
        Snapshot = snapshot;
        Snapshot whole = snapshot.Whole;
        status.Text = snapshot.FramesAlone
            ? $"{snapshotName}: {Durations.Said(snapshot.Duration)}, {Plural.Of(snapshot.GameFrames.Count, "frame")} as Godot's profiler measured them; no samples, as a game of GDScript alone runs no .NET code"
            : ReferenceEquals(whole, snapshot)
            ? $"{snapshotName}: {Durations.Said(snapshot.Duration)}, {Plural.Of(snapshot.Threads.Count, "thread")}, {snapshot.Samples:N0} samples{(snapshot.CutShort is null ? string.Empty : ", its trace cut short")}"
            : $"{snapshotName}: {snapshot.StartMs - whole.StartMs:N0} to {snapshot.EndMs - whole.StartMs:N0} ms of {whole.Duration.TotalMilliseconds:N0}, {snapshot.Samples:N0} samples";
        foreach (ToggleButton page in pages.Toggles)
        {
            page.IsEnabled = true;
        }
        rows.Clear();
        // A Godot game's frames give its GDScript and Godot's own work, counted as samples of the run's period, beside the threads.
        double period = snapshot.SampleMs > 0 ? snapshot.SampleMs : 1;
        IReadOnlyList<CallNode> gameRoots = whole.Game is { } profile && snapshot.GameFrames.Count > 0
            ? profile.CallRoots(snapshot.GameFrames, snapshot.Samples > 0 ? snapshot.Joined(snapshot.GameFrames).FrameMs : [], period)
            : [];
        int counted = snapshot.Samples > 0 ? snapshot.Samples : Math.Max(1, gameRoots.Sum(r => r.Total));
        foreach (CallNode thread in snapshot.Threads)
        {
            rows.Add(new CallRow(thread, 0, rows, "ms", counted, period));
        }
        foreach (CallNode root in gameRoots)
        {
            rows.Add(new CallRow(root, 0, rows, "ms", counted, period));
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
        // A Godot game's run shows Godot's own frames on the page, an Avalonia app's (or none) every other's.
        game.IsVisible = whole.Game is not null;
        frames.IsVisible = whole.Game is null;
        if (whole.Game is { } played)
        {
            game.Show(played, snapshot.GameFrames, snapshot.Joined, whole.StartMs);
        }
        // A run of a game's frames alone has no samples for the pages of calls to show.
        ShowPageLater(snapshot.FramesAlone && current is ProfilerPage.CallTree or ProfilerPage.HotSpots or ProfilerPage.FlameGraph ? ProfilerPage.Frames : current);
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

    /// <summary>A row for whichever allocated type the list hands its container: its share and size, and its name.</summary>
    private Control Allocated()
    {
        TextBlock size = Mono(Muted(string.Empty, 170));
        TextBlock name = new() { Classes = { StyleClasses.Strong }, VerticalAlignment = VerticalAlignment.Center };
        ShrinkingRow row = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Small, 0), Children = { size, name } };
        return row.Showing<AllocatedType>(type =>
        {
            long whole = Snapshot?.Allocations.Sum(a => a.Bytes) ?? 0;
            size.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(whole > 0 ? 100.0 * type.Bytes / whole : 0),5:0.0}%  {Sizes.Said(type.Bytes),11}");
            name.Text = type.Name;
        });
    }

    /// <summary>A row for whichever collection the list hands its container: its generation, when, its pause and why.</summary>
    private static Control CollectionRow()
    {
        TextBlock what = new() { Width = 170, VerticalAlignment = VerticalAlignment.Center };
        TextBlock when = Muted(string.Empty, 110);
        TextBlock pause = Muted(string.Empty, 140);
        TextBlock why = Muted(string.Empty, double.NaN);
        ShrinkingRow row = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Small, 0), Children = { what, when, pause, why } };
        return row.Showing<Collection>(gc =>
        {
            what.Text = $"#{gc.Number}  generation {gc.Generation}";
            when.Text = $"at {gc.AtMs:N0} ms";
            pause.Text = $"paused {gc.PauseMs:0.00} ms";
            why.Text = gc.Reason;
        });
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

    /// <summary>A row's method opened at its source, a GDScript function's at its script's line; false for a thread or a
    /// type, at the top, which stand for no method.</summary>
    private bool OpenRow(CallRow row)
    {
        if (row.Node.Script is { } function && Snapshot?.Whole.Game is { } game)
        {
            ScriptOpened?.Invoke(this, (function, game.Folder));
            return true;
        }
        return row.Depth > 0 && Open(row.Node.Name, row.Node.Code);
    }

    /// <summary>The page last asked shown.</summary>
    public ProfilerPage Page { get; private set; }

    /// <summary>The call tree's row chosen, or null.</summary>
    public CallRow? ChosenCall => tree.SelectedItem as CallRow;

    /// <summary>A GDScript function's row of the call tree opened: its script, at its line.</summary>
    public event EventHandler<(ScriptFunction Function, string Folder)>? ScriptOpened;

    /// <summary>A frame of a Godot game opened on what it spent: the window narrowed to the frame, the call tree shown with
    /// the frame's costliest function chosen, a GDScript function's or a C# method's by its own time.</summary>
    public async Task OpenFrameAsync(GameFrame frame)
    {
        await ChooseAsync((frame.StartMs, frame.EndMs));
        ShowPage(ProfilerPage.CallTree);
        if (Costliest(rows.Where(r => r.Depth == 0).Select(r => r.Node)) is not { } costliest)
        {
            return;
        }
        // Up to its top row: a thread's node has the whole run's above it, which no row shows.
        HashSet<CallNode> tops = [.. rows.Where(r => r.Depth == 0).Select(r => r.Node)];
        List<CallNode> path = [];
        for (CallNode? at = costliest; at is not null; at = tops.Contains(at) ? null : at.Parent)
        {
            path.Insert(0, at);
        }
        CallRow? row = null;
        foreach (CallNode node in path)
        {
            if (row is not null)
            {
                Expand(row);
            }
            row = rows.FirstOrDefault(r => r.Node == node);
            if (row is null)
            {
                return;
            }
        }
        tree.SelectedItem = row;
        tree.ScrollIntoView(row!);
    }

    /// <summary>The function under some roots that spent the most time in itself: a GDScript function, or a C# method with
    /// its source; the runtime's and Godot's native frames left aside.</summary>
    public static CallNode? Costliest(IEnumerable<CallNode> roots)
    {
        CallNode? best = null;
        Stack<CallNode> left = new(roots);
        while (left.TryPop(out CallNode? node))
        {
            if ((node.Script is not null || node.Code is not null) && (best is null || node.Self > best.Self))
            {
                best = node;
            }
            foreach (CallNode child in node.Children)
            {
                left.Push(child);
            }
        }
        return best;
    }

    /// <summary>Goes to a method's source; false for a row no source has (a thread, the runtime's own frames).</summary>
    private bool Open(string method, MethodCode? code)
    {
        if (method.StartsWith('[') || method.StartsWith("Thread ", StringComparison.Ordinal))
        {
            return false;
        }
        OpenRequested?.Invoke(this, new ProfiledMethod(method, code));
        return true;
    }

    /// <summary>Shows a page; the placeholder's words when it has nothing to show.</summary>
    public void ShowPage(ProfilerPage page)
    {
        Page = page;
        pages.Choice = page;
        Control[] views = [tree, spots, flameScroll, framesPage, allocations, garbage, memory, keptPage];
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
        }
        placeholder.IsVisible = !shown;
        // The run's timeline goes with its views, not with the list of runs; the Frames page has its frames' own.
        timelineBand.IsVisible = timeline.Snapshot is not null && page is not (ProfilerPage.Snapshots or ProfilerPage.Frames);
    }

    private string Share(int samples) => Snapshot is { Samples: > 0 } shot ? $"{100.0 * samples / shot.Samples,5:0.0}%" : "";

    /// <summary>A row for whichever call the list hands its container: its chevron, its share and weight, and its method.</summary>
    private Control Row()
    {
        RowExpander chevron = RowExpander.Of<CallRow>(Toggle);
        TextBlock share = Mono(Muted(string.Empty, 170));
        MethodName method = new();
        ShrinkingRow line = new() { Height = ListRow.Height, Children = { chevron, share, method.Row } };
        return line.Showing<CallRow>(row =>
        {
            line.Margin = new Thickness(row.Depth * Gaps.Indent, 0, 0, 0);
            chevron.Expands = row.Node.Children.Count > 0;
            chevron.IsOpen = row.IsExpanded;
            string percent = row.Whole > 0 ? $"{100.0 * row.Node.Total / row.Whole,5:0.0}%" : string.Empty;
            share.Text = $"{percent}  {row.Node.Total * row.Scale,7:N0} {row.Unit}";
            method.Show(row.Node.Name, row.Depth == 0);
        });
    }

    /// <summary>A row for whichever hot spot the list hands its container: its own time and share, its time in all, and its method.</summary>
    private Control Spot()
    {
        TextBlock own = Mono(Muted(string.Empty, 170));
        TextBlock total = Muted(string.Empty, 110);
        total.Classes.Add(StyleClasses.Caption);
        MethodName method = new();
        ShrinkingRow row = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Small, 0), Children = { own, total, method.Row } };
        return row.Showing<HotSpot>(spot =>
        {
            double sampleMs = Snapshot?.SampleMs ?? 1;
            own.Text = $"{Share(spot.Self)}  {spot.Self * sampleMs,7:N0} ms";
            total.Text = $"{spot.Total * sampleMs,7:N0} ms in all";
            method.Show(spot.Name, false);
        });
    }

    /// <summary>A method's name: its own strong, its type's and namespace's muted before it; a thread's, strong, alone.</summary>
    private sealed class MethodName
    {
        private readonly TextBlock before = Muted(string.Empty, double.NaN);
        private readonly TextBlock own = new() { VerticalAlignment = VerticalAlignment.Center };

        public MethodName() => Row = new ShrinkingRow { Children = { before, own } };

        public ShrinkingRow Row { get; }

        public void Show(string name, bool thread)
        {
            if (thread || name.StartsWith('['))
            {
                before.IsVisible = false;
                own.Text = name;
                own.Classes.Set(StyleClasses.Strong, thread);
                return;
            }
            string bare = name.Split('(')[0];
            int dot = bare.LastIndexOf('.');
            string owner = dot > 0 ? bare[..(dot + 1)] : string.Empty;
            before.IsVisible = true;
            before.Text = owner;
            own.Text = name[owner.Length..];
            own.Classes.Set(StyleClasses.Strong, true);
        }
    }

    /// <summary>Words in the code font, at the size of a row's numbers.</summary>
    private static TextBlock Mono(TextBlock block)
    {
        block.Classes.Add(StyleClasses.Code);
        return block;
    }

    private static TextBlock Muted(string text, double width)
    {
        TextBlock block = MutedText.Of(text);
        block.Width = width;
        return block;
    }
}
