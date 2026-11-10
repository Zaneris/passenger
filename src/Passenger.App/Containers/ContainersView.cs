using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Run;
using Passenger.App.Telemetry;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Containers;

/// <summary>What the Containers window works with: the window's engine, its actions' work, the status bar, the browser a
/// port's link opens in, the folder the state's copies of a container's files go under, its actions' titles, and the items
/// of its rows' menus as the window makes them (greyed with why).</summary>
public sealed record ContainersParts(Func<ContainerEngine> Engine, ActionWork Work, StatusSay Say, Action<Uri> Browse, Func<string> StateFolder, Func<string, string> Title,
    Func<string, MenuItem> Item);

/// <summary>What a lookup of a .NET service's program in its container came to: its process there and the place Passenger's
/// tools reach it by (<see cref="Found"/>), or why it was not reached (<see cref="Missing"/>).</summary>
public sealed record DotnetLookup(int Process, Profiler.ProfilePlace? Place, string? WhyNot)
{
    public static DotnetLookup Found(int process, Profiler.ProfilePlace place) => new(process, place, null);

    public static DotnetLookup Missing(string why) => new(0, null, why);
}

/// <summary>The Containers window: the engine's Compose projects with their services and containers (stopped ones still
/// listed), then every other container (Testcontainers' apart), images, volumes and networks, each row with its state,
/// health, ports (links) and image; and the chosen container's Log, Stats, Files and Details (a service's its container's),
/// or a project's or an AppHost's merged Log and Stats; and for a container run that collects them, its Telemetry. It asks
/// the engine only while shown: its lists once, then again as the engine's events say something changed; a container's
/// log and stats stream only while their tab is shown. On the frame of a run's windows (<see cref="RunWindowFrame"/>), its
/// toolbar's buttons and its tabs run the window's actions.</summary>
public sealed class ContainersView : DockPanel
{
    // The tabs' names are those every list of a run's services has, in its order (RunWindows.Tabs).
    private const string LogTab = RunWindows.LogTab;
    private const string StatsTab = RunWindows.StatsTab;
    private const string FilesTab = RunWindows.FilesTab;
    private const string DetailsTab = RunWindows.DetailsTab;
    private const string TelemetryTab = RunWindows.TelemetryTab;

    private readonly ContainersParts parts;
    private readonly ListBox list;
    private readonly System.Collections.ObjectModel.ObservableCollection<RunItem<ContainerRow>> items = [];
    private readonly TextBlock empty;
    private readonly TextBlock nothingChosen;
    private readonly IReadOnlyDictionary<string, ToggleButton> tabs;
    private readonly ContentControl detail = new();
    private readonly HashSet<string> collapsed = ["group:Images", "group:Volumes", "group:Networks"];
    private readonly Debounce refreshing;
    private IDisposable? following;
    private bool shown;
    private bool filling;

    /// <summary>Events that change nothing the window lists: a command run in a container, an attach, a copy.</summary>
    private static bool Quiet(string action) =>
        action.StartsWith("exec_", StringComparison.Ordinal) || action is "exec" or "attach" or "detach" or "resize" or "top" or "archive-path" or "extract-to-dir"
            or "export" or "copy";
    private int reads;
    private string shownTab = LogTab;

    public ContainersView(ContainersParts parts)
    {
        this.parts = parts;
        refreshing = new Debounce(TimeSpan.FromMilliseconds(300), () => RefreshAsync());
        // The chosen row's engine: an AppHost's containers its orchestrator ran on its own runtime are that runtime's.
        Func<ContainerEngine> chosen = () => EngineOf(Chosen);
        Log = new ContainerLogPane(chosen);
        Stats = new ContainerStatsPane(chosen);
        Files = new ContainerFilesPane(chosen, parts.StateFolder);
        Details = new ContainerDetailsPane(chosen, parts.Browse, () => State?.Endpoint);
        ImageDetails = new ImageDetailsPane(chosen);
        statsPage = new Grid { RowDefinitions = new RowDefinitions("*,0"), Children = { Stats, Metrics } };
        Grid.SetRow(Metrics, 1);
        Metrics.IsVisible = false;
        Telemetry = new TelemetryView(AppActions.ContainersShowTrace, AppActions.ContainersShowRelatedLogs, id =>
        {
            Request(id);
            return true;
        });
        Log.LinkClicked += (_, link) => LinkClicked?.Invoke(this, link);
        Telemetry.LinkClicked += (_, link) => LinkClicked?.Invoke(this, link);
        Telemetry.ShownChanged += (_, _) => ShowChosen();

        ToolFlow tools = RunWindowFrame.Tools();
        // Its Refresh, then the service actions, then a container's own.
        foreach ((string action, string icon) in (ReadOnlySpan<(string, string)>)[
            (AppActions.ContainersRefresh, IconKeys.Rerun),
            .. RunWindows.ServiceActions,
            (AppActions.ContainerStart, IconKeys.Run),
            (AppActions.ContainerStop, IconKeys.Stop),
            (AppActions.ContainerRestart, IconKeys.Update),
            (AppActions.ContainerPause, IconKeys.Pause),
            (AppActions.ContainerRemove, IconKeys.Close),
            (AppActions.ContainerShell, IconKeys.Terminal),
            (AppActions.ContainerInspect, IconKeys.DocumentText),
            (AppActions.ContainerPull, IconKeys.ArrowDown),
            (AppActions.ContainerScale, IconKeys.Add),
            (AppActions.ContainerPrune, IconKeys.Clear)])
        {
            tools.Children.Add(ToolButtons.Of(action, icon, parts.Title(action), Request));
        }

        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = items,
        };
        RunRows.Use<ContainerRow>(list, Row, Toggle, parts.Browse, "containerPort");
        list.SelectionChanged += (_, _) =>
        {
            if (!filling)
            {
                ShowChosen();
                RowChosen?.Invoke(this, EventArgs.Empty);
            }
        };
        RunRows.Toggles<ContainerRow>(list, Toggle);
        // The service actions first (greyed with why where it is no .NET service of the run under way), then the row's own.
        ListMenus.Attach(list, () => Chosen is not { Kind: not ContainerRowKind.Group } ? [] :
        [
            .. Chosen is { Kind: ContainerRowKind.Service or ContainerRowKind.Container }
                ? (Control[])[.. RunWindows.ServiceActions.Select(service => Item(service.Action)), new Separator()] : [],
            Item(AppActions.ContainerStart),
            Item(AppActions.ContainerStop),
            Item(AppActions.ContainerRestart),
            Item(AppActions.ContainerPause),
            new Separator(),
            Item(AppActions.ContainerShell),
            Item(AppActions.ContainerInspect),
            Item(AppActions.ContainerPull),
            Item(AppActions.ContainerScale),
            new Separator(),
            Item(AppActions.ContainerRemove),
        ]);
        empty = new TextBlock { Classes = { StyleClasses.EmptyText }, Text = "Reading what the engine holds…" };
        nothingChosen = new TextBlock { Classes = { StyleClasses.EmptyText }, Text = NothingChosen };

        tabs = RunWindowFrame.Build(this, tools, new Panel { Children = { list, empty } }, new Panel { Children = { detail, nothingChosen } }, Pages, PageAction,
            "containers", Request);
        ShowChosen();
    }

    /// <summary>One of the window's actions asked for by a button or a tab.</summary>
    public event EventHandler<string>? ActionRequested;

    private void Request(string action) => ActionRequested?.Invoke(this, action);

    /// <summary>The tabs' names, as their tabs and actions say them, in their order.</summary>
    public static IReadOnlyList<string> Pages { get; } = [.. RunWindows.Tabs.Where(tab => tab is LogTab or StatsTab or FilesTab or DetailsTab or TelemetryTab)];

    /// <summary>The action that shows a tab, which its tab runs and Find Action reaches.</summary>
    public static string PageAction(string name) => name switch
    {
        LogTab => AppActions.ContainersLog,
        StatsTab => AppActions.ContainersStats,
        FilesTab => AppActions.ContainersFiles,
        DetailsTab => AppActions.ContainersDetails,
        TelemetryTab => AppActions.ContainersTelemetry,
        _ => throw new ArgumentException("Unknown Containers tab.", nameof(name)),
    };

    /// <summary>A person chose a row (a click, the keys), never the list read again.</summary>
    public event EventHandler? RowChosen;

    /// <summary>A link in a log clicked: a file's place, or an address for the browser.</summary>
    public event EventHandler<LinkTarget>? LinkClicked;

    /// <summary>The engine answered with another status (as shown, or as an action found).</summary>
    public event EventHandler<EngineState>? StateRead;

    public ContainerLogPane Log { get; }

    public ContainerStatsPane Stats { get; }

    public ContainerFilesPane Files { get; }

    public ContainerDetailsPane Details { get; }

    /// <summary>An image's Details tab, its known vulnerabilities through Docker Scout once asked.</summary>
    public ImageDetailsPane ImageDetails { get; }

    /// <summary>A .NET service's metrics under its Stats, read in its container while the tab is shown.</summary>
    public MetricsPane Metrics { get; } = new();

    /// <summary>A .NET service's program in its container, by its Compose project and service: found, or why it is not
    /// reached yet; null for one that is no .NET service of the folder's run, or once that run has ended.</summary>
    public Func<string, string, Task<DotnetLookup?>>? DotnetOf { get; set; }

    /// <summary>The Stats tab: the engine's stats, a .NET service's metrics under them.</summary>
    private readonly Grid statsPage;

    /// <summary>The service whose metrics are shown, each lookup of its program counted so a late one is let go; whether one
    /// is under way; why the last found none, while it is looked for again; and how long the next waits, and its wait.</summary>
    private string? metricsOf;
    private int metricsAsked;
    private bool metricsLooking;
    private string? metricsMissing;
    private TimeSpan metricsWait;
    private CancellationTokenSource? metricsLater;

    /// <summary>The wait before a program not reached is looked for again, doubled after each lookup that finds none, up to
    /// <see cref="LongestLookWait"/>.</summary>
    private static readonly TimeSpan FirstLookWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestLookWait = TimeSpan.FromSeconds(15);

    /// <summary>What the last lookup of the chosen service's program came to, for a failure to say: under way, found, why
    /// not, or none to look for; empty where none was made.</summary>
    public string ProgramLookup { get; private set; } = string.Empty;

    /// <summary>What a .NET service's metrics say while its program is looked for.</summary>
    public static string Looking(string service) => $"Looking for {service}'s program in its container…";

    /// <summary>What a .NET service's metrics say once a lookup found no program, <paramref name="why"/>, until the next.</summary>
    public static string LookingAgain(string why) => $"{why}; looking again…";

    /// <summary>The Telemetry tab's logs and traces, a log's trace and a span's logs the window's actions.</summary>
    public TelemetryView Telemetry { get; }

    /// <summary>Where a Compose project's logs and traces are read, by its name: the container run of this window's folder
    /// that runs Passenger's dashboard for it, or a project run's dashboard's own; null for any other.</summary>
    public Func<string, TelemetrySource?>? TelemetryOf { get; set; }

    /// <summary>What the engine held at the last read.</summary>
    public ContainerSnapshot Snapshot { get; private set; } = ContainerSnapshot.Empty;

    /// <summary>The label Aspire's orchestrator (DCP) puts on every container it makes.</summary>
    private const string OrchestratorLabel = "com.microsoft.developer.usvc-dev.uid";

    /// <summary>The .NET services the folder's container run of a Compose project (by its name) left unstarted, as its
    /// configuration says Don't start, listed under it as not started; none for any other project.</summary>
    public Func<string, IReadOnlyList<string>>? UnstartedOf { get; set; }

    /// <summary>The AppHost a container is of (its key and name), when the folder runs one whose resources name it.</summary>
    public Func<ContainerInfo, (string Key, string Name)?>? AppHostOf { get; set; }

    /// <summary>The engines besides the window's that hold the folder's AppHosts' containers (an orchestrator's own runtime,
    /// Podman's beside Docker's): their orchestrator's containers listed among the window's, each acted on there.</summary>
    public Func<IReadOnlyList<ContainerEngine>>? OtherEngines { get; set; }

    /// <summary>The other engines' events followed while the window shows, by engine.</summary>
    private readonly Dictionary<ContainerEngine, IDisposable> othersFollowed = [];

    /// <summary>The engine a row's commands go to: its containers' own where they are another's, else the window's.</summary>
    private ContainerEngine EngineOf(ContainerRow? row) => row?.Containers.FirstOrDefault(container => container.Engine is not null)?.Engine ?? parts.Engine();

    /// <summary><see cref="OtherEngines"/> changed: read again, followed from now, while the window shows.</summary>
    public void OtherEnginesChanged()
    {
        UiThread.Check();
        if (shown)
        {
            refreshing.Restart();
        }
    }

    /// <summary>Follows each other engine's events while the window shows, as the window's own are, and no longer one that
    /// holds none of the folder's containers.</summary>
    private IReadOnlyList<ContainerEngine> FollowOthers()
    {
        IReadOnlyList<ContainerEngine> others = shown ? OtherEngines?.Invoke() ?? [] : [];
        foreach (ContainerEngine gone in othersFollowed.Keys.Except(others).ToArray())
        {
            othersFollowed.Remove(gone, out IDisposable? following);
            following?.Dispose();
        }
        foreach (ContainerEngine other in others.Where(other => !othersFollowed.ContainsKey(other)))
        {
            othersFollowed[other] = other.Follow(told =>
            {
                if (told.Type == "container" && !Quiet(told.Action) || told.Type == ContainerEngine.EngineEvent)
                {
                    UiThread.Post(() =>
                    {
                        if (shown)
                        {
                            refreshing.Restart();
                        }
                    });
                }
            });
        }
        return others;
    }

    /// <summary>The containers an orchestrator made on another engine, each marked as that engine's; none where it does
    /// not answer, said in the IDE's log.</summary>
    private static async Task<IReadOnlyList<ContainerInfo>> OrchestratedOnAsync(ContainerEngine other)
    {
        try
        {
            return [.. (await other.JsonLinesAsync(["ps", "-a", "--filter", $"label={OrchestratorLabel}", "--format", "json"]))
                .Select(row => ContainerInfo.Read(row) with { Engine = other })];
        }
        catch (Exception failed) when (failed is ContainerCommandException or TimeoutException or System.Text.Json.JsonException or FileNotFoundException
            or System.ComponentModel.Win32Exception)
        {
            IdeErrors.Write($"Containers: {other.Cli} did not list an AppHost's containers: {failed.Message}");
            return [];
        }
    }

    /// <summary>The rows grouped again as what an AppHost's resources name changed, the engine not read again.</summary>
    public void Regroup()
    {
        UiThread.Check();
        if (shown && HasRead)
        {
            Fill();
        }
    }

    /// <summary>The engine's state at the last read; null before one.</summary>
    public EngineState? State { get; private set; }

    /// <summary>Whether a read has landed since the window was shown.</summary>
    public bool HasRead { get; private set; }

    /// <summary>The rows listed, headings among them.</summary>
    public IReadOnlyList<ContainerRow> Rows => [.. items.Select(item => item.Data)];

    /// <summary>The row chosen, which the window's actions act on.</summary>
    public ContainerRow? Chosen => (list.SelectedItem as RunItem<ContainerRow>)?.Data;

    /// <summary>The detail tab shown: Log, Stats, Files, Details or Telemetry.</summary>
    public string ShownTab => shownTab;

    /// <summary>Chooses a row by its key, as a click would.</summary>
    public bool Choose(Func<ContainerRow, bool> which)
    {
        if (items.FirstOrDefault(item => which(item.Data)) is not { } row)
        {
            return false;
        }
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        return true;
    }

    /// <summary>A Compose project a run brought up, chosen once the window lists it (now, or when it next reads the engine).</summary>
    private string? revealing;

    /// <summary>Chooses a Compose project (a run's), its group and row opened, now or once the window lists it.</summary>
    public void Reveal(string project)
    {
        string key = $"project:{project}";
        collapsed.Remove(key);
        if (!Choose(r => r.Key == key))
        {
            revealing = key;
            if (shown)
            {
                refreshing.Restart();
            }
        }
    }

    /// <summary>Why a detail tab cannot be shown now, or null: the row chosen (or none) has no such tab.</summary>
    public string? WhyNotTab(string name) => tabs[name].IsVisible ? null
        : Chosen is { } row ? $"{row.Name} has no {name} tab in the Containers window" : $"Nothing chosen in the Containers window has a {name} tab";

    /// <summary>Shows a detail tab.</summary>
    public void ShowTab(string name)
    {
        shownTab = name;
        ShowChosen();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        shown = true;
        following ??= parts.Engine().Follow(told =>
        {
            // Its own tabs' exec, attach and copy change nothing listed, and would read again for ever.
            if (told.Type is "container" or "network" or "volume" or "image" or ContainerEngine.EngineEvent && !Quiet(told.Action))
            {
                UiThread.Post(() =>
                {
                    if (shown)
                    {
                        refreshing.Restart();
                    }
                });
            }
        });
        IdeErrors.Observe(RefreshAsync());
        ShowChosen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        shown = false;
        HasRead = false;
        refreshing.Stop();
        following?.Dispose();
        following = null;
        FollowOthers();
        Log.Stop();
        Stats.Stop();
        ShowMetrics(null);
        Details.Show(null);
        ImageDetails.Show(null);
        Telemetry.Watch(null, null);
    }

    /// <summary>A run's telemetry came or went (its dashboard up, the run stopped): the tabs follow.</summary>
    public void TelemetryChanged() => ShowChosen();

    /// <summary>Reads the engine's state and what it holds again, off the UI thread; the rows follow, the one chosen kept.</summary>
    public async Task RefreshAsync()
    {
        UiThread.Check();
        int read = ++reads;
        ContainerEngine engine = parts.Engine();
        IReadOnlyList<ContainerEngine> others = FollowOthers();
        (EngineState state, ContainerSnapshot? snapshot, string? problem, IReadOnlyList<ContainerInfo> elsewhere) = await BackgroundWork.Run(async () =>
        {
            Task<IReadOnlyList<ContainerInfo>[]> orchestrated = Task.WhenAll(others.Select(OrchestratedOnAsync));
            EngineState state = await engine.StateAsync(again: true);
            IReadOnlyList<ContainerInfo> elsewhere = [.. (await orchestrated).SelectMany(containers => containers)];
            if (state.Status is not (EngineStatus.Ready or EngineStatus.NoCompose))
            {
                return (state, (ContainerSnapshot?)null, (string?)null, elsewhere);
            }
            try
            {
                return (state, await ContainerSnapshot.ReadAsync(engine), null, elsewhere);
            }
            catch (Exception failed) when (failed is ContainerCommandException or TimeoutException or System.Text.Json.JsonException)
            {
                return (state, null, failed.Message, elsewhere);
            }
        });
        if (read != reads)
        {
            return;
        }
        bool changed = State?.Status != state.Status;
        State = state;
        Snapshot = snapshot ?? ContainerSnapshot.Empty;
        if (elsewhere.Count > 0)
        {
            Snapshot = Snapshot with { Containers = [.. Snapshot.Containers.Concat(elsewhere).OrderBy(c => c.Name, StringComparer.Ordinal)] };
        }
        HasRead = true;
        empty.Text = state.Problem ?? problem ?? "The engine holds no containers, images, volumes or networks";
        Fill();
        if (revealing is { } project && Choose(r => r.Key == project))
        {
            revealing = null;
        }
        if (changed)
        {
            StateRead?.Invoke(this, state);
        }
    }

    private void Fill()
    {
        string? chosen = Chosen?.Key;
        List<ContainerRow> rows = [];
        ContainerSnapshot snapshot = Snapshot;
        if (snapshot.Projects.Count > 0)
        {
            Group("Compose", Core.Plural.Of(snapshot.Projects.Count, "project"), () =>
            {
                foreach (ComposeProject project in snapshot.Projects)
                {
                    string key = $"project:{project.Name}";
                    bool open = !collapsed.Contains(key);
                    rows.Add(new ContainerRow(ContainerRowKind.Project, 1, key, project.Name) { Detail = project.Status, Project = project, Expands = true, Expanded = open });
                    if (!open)
                    {
                        continue;
                    }
                    foreach ((string service, IReadOnlyList<ContainerInfo> containers) in project.Services)
                    {
                        string serviceKey = $"service:{project.Name}/{service}";
                        bool serviceOpen = !collapsed.Contains(serviceKey) && containers.Count > 1;
                        rows.Add(new ContainerRow(ContainerRowKind.Service, 2, serviceKey, service)
                        {
                            Detail = containers.Count == 1 ? containers[0].Image : $"{containers.Count} containers",
                            Project = project,
                            Service = service,
                            Container = containers.Count == 1 ? containers[0] : null,
                            Expands = containers.Count > 1,
                            Expanded = serviceOpen,
                        });
                        if (serviceOpen)
                        {
                            rows.AddRange(containers.Select(c => ContainerRowOf(c, 3)));
                        }
                    }
                    // What its run left unstarted has no container yet, so the engine lists none: said not started.
                    foreach (string service in UnstartedOf?.Invoke(project.Name) ?? [])
                    {
                        if (project.Services.All(s => s.Service != service))
                        {
                            rows.Add(new ContainerRow(ContainerRowKind.Service, 2, $"service:{project.Name}/{service}", service)
                            {
                                Detail = NotStarted,
                                Project = project,
                                Service = service,
                                LeftUnstarted = true,
                            });
                        }
                    }
                }
            });
        }
        List<ContainerInfo> others = [.. snapshot.Containers.Where(c => !c.IsTestcontainers)];
        // An AppHost's containers under it; any other its orchestrator made (a Persistent one kept) under the heading.
        List<(string Key, string Name, List<ContainerInfo> Containers)> hosts = [];
        List<ContainerInfo> orchestrated = [];
        foreach (ContainerInfo container in others)
        {
            if (AppHostOf?.Invoke(container) is { } host)
            {
                if (hosts.FindIndex(h => h.Key == host.Key) is var at and >= 0)
                {
                    hosts[at].Containers.Add(container);
                }
                else
                {
                    hosts.Add((host.Key, host.Name, [container]));
                }
            }
            else if (container.Labels.ContainsKey(OrchestratorLabel))
            {
                orchestrated.Add(container);
            }
        }
        others.RemoveAll(c => orchestrated.Contains(c) || hosts.Any(h => h.Containers.Contains(c)));
        if (hosts.Count + orchestrated.Count > 0)
        {
            Group("Aspire", hosts.Count > 0 ? Core.Plural.Of(hosts.Count, "AppHost") : $"{orchestrated.Count}", () =>
            {
                foreach ((string hostKey, string name, List<ContainerInfo> containers) in hosts.OrderBy(h => h.Name, StringComparer.Ordinal))
                {
                    string key = $"apphost:{hostKey}";
                    bool open = !collapsed.Contains(key);
                    rows.Add(new ContainerRow(ContainerRowKind.AppHost, 1, key, name)
                    {
                        Detail = string.Join(", ", containers.GroupBy(c => c.State).Select(g => $"{g.Key}({g.Count()})")),
                        Grouped = containers,
                        Expands = true,
                        Expanded = open,
                    });
                    if (open)
                    {
                        rows.AddRange(containers.Select(c => ContainerRowOf(c, 2)));
                    }
                }
                rows.AddRange(orchestrated.Select(c => ContainerRowOf(c, 1)));
            });
        }
        if (others.Count > 0)
        {
            Group("Containers", $"{others.Count}", () => rows.AddRange(others.Select(c => ContainerRowOf(c, 1))));
        }
        List<ContainerInfo> tested = [.. snapshot.Containers.Where(c => c.IsTestcontainers)];
        if (tested.Count > 0)
        {
            Group("Testcontainers", $"{tested.Count}", () => rows.AddRange(tested.Select(c => ContainerRowOf(c, 1))));
        }
        foreach ((string name, ContainerRowKind kind, IReadOnlyList<EngineObject> objects) in (ReadOnlySpan<(string, ContainerRowKind, IReadOnlyList<EngineObject>)>)[
            ("Images", ContainerRowKind.Image, snapshot.Images), ("Volumes", ContainerRowKind.Volume, snapshot.Volumes), ("Networks", ContainerRowKind.Network, snapshot.Networks)])
        {
            if (objects.Count > 0)
            {
                Group(name, $"{objects.Count}", () => rows.AddRange(objects.Select(o =>
                    new ContainerRow(kind, 1, $"{o.Kind}:{o.Id}", o.Name) { Detail = o.Detail, Object = o })));
            }
        }
        // Rows taken out may choose nothing for a moment, which must not end the chosen row's streams.
        filling = true;
        try
        {
            RunRows.Put(items, [.. rows.Select(row => (row.Key, row))]);
            list.SelectedItem = chosen is null ? null : items.FirstOrDefault(item => item.Key == chosen);
        }
        finally
        {
            filling = false;
        }
        empty.IsVisible = rows.Count == 0;
        ShowChosen();

        void Group(string name, string count, Action fill)
        {
            string key = $"group:{name}";
            bool open = !collapsed.Contains(key);
            rows.Add(new ContainerRow(ContainerRowKind.Group, 0, key, name) { Detail = count, Expands = true, Expanded = open });
            if (open)
            {
                fill();
            }
        }
    }

    private const string NothingChosen = "Choose a container or a Compose project to see its log and stats";

    /// <summary>How a service its run left unstarted is said, in an AppHost's words.</summary>
    public const string NotStarted = Aspire.AspireAppRun.NotStarted;

    private static ContainerRow ContainerRowOf(ContainerInfo container, int depth) =>
        new(ContainerRowKind.Container, depth, $"container:{container.Name}", container.Name) { Detail = container.Image, Container = container };


    /// <summary>Opens every group, project, AppHost and service of several containers.</summary>
    public void ExpandAll()
    {
        collapsed.Clear();
        Fill();
    }

    /// <summary>Closes every row with rows under it, those of a closed group too, leaving the groups.</summary>
    public void CollapseAll()
    {
        collapsed.Clear();
        Fill();
        collapsed.UnionWith(items.Select(item => item.Data).Where(row => row.Expands).Select(row => row.Key));
        Fill();
    }

    /// <summary>Expands or collapses a row with rows under it.</summary>
    public void Toggle(ContainerRow? row)
    {
        if (row is not { Expands: true })
        {
            return;
        }
        if (!collapsed.Remove(row.Key))
        {
            collapsed.Add(row.Key);
        }
        Fill();
    }

    /// <summary>The chosen row's details: a container's Log, Stats, Files and Details, a service's Log, Stats and its
    /// container's Details, a project's or an AppHost's Log and Stats, an image's Details alone, nothing for the rest;
    /// streamed only while shown.</summary>
    private void ShowChosen()
    {
        ContainerRow? row = Chosen;
        bool container = row?.Container is not null;
        bool image = row is { Kind: ContainerRowKind.Image, Object: not null };
        bool detailed = row is { Kind: ContainerRowKind.Service or ContainerRowKind.Container } && row.Containers.Count > 0 || image;
        // A service its run left unstarted has no container to stream from: the pane says what starts it.
        bool streams = shown && (row is { Kind: ContainerRowKind.Project or ContainerRowKind.AppHost or ContainerRowKind.Service or ContainerRowKind.Container, LeftUnstarted: false } || image);
        if (image)
        {
            shownTab = DetailsTab;
        }
        tabs[LogTab].IsVisible = !image;
        tabs[StatsTab].IsVisible = !image;
        nothingChosen.Text = row is { LeftUnstarted: true } ? ContainerCommands.NotStarted(row) : NothingChosen;
        TelemetrySource? telemetry = (row?.Project?.Name ?? row?.Container?.Project) is { } project ? TelemetryOf?.Invoke(project) : null;
        tabs[FilesTab].IsVisible = container;
        tabs[DetailsTab].IsVisible = detailed;
        tabs[TelemetryTab].IsVisible = telemetry is not null;
        if ((shownTab == FilesTab && !container) || (shownTab == DetailsTab && !detailed) || (shownTab == TelemetryTab && telemetry is null))
        {
            shownTab = LogTab;
        }
        foreach ((string name, ToggleButton tab) in tabs)
        {
            tab.IsChecked = name == shownTab;
            tab.IsEnabled = streams;
        }
        nothingChosen.IsVisible = !streams;
        detail.IsVisible = streams;
        detail.Content = shownTab switch
        {
            StatsTab => statsPage,
            FilesTab => Files,
            DetailsTab => image ? ImageDetails : Details,
            TelemetryTab => Telemetry.Both,
            _ => Log,
        };
        Log.Show(streams && shownTab == LogTab ? row : null);
        Stats.Show(streams && shownTab == StatsTab ? row : null);
        ShowMetrics(streams && shownTab == StatsTab ? row : null);
        Files.Show(streams && shownTab == FilesTab ? row : null);
        Details.Show(streams && shownTab == DetailsTab && !image ? row : null);
        ImageDetails.Show(streams && image ? row!.Object : null);
        // A service's or container's own records; a project's every service's, as the dashboard's own row's are.
        string? service = row?.Service ?? row?.Container?.Service;
        Telemetry.Watch(streams && shownTab == TelemetryTab ? telemetry : null, Telemetry.Shown, service == ContainerTelemetry.Service ? null : service);
    }

    /// <summary>A .NET service's metrics under its Stats while they are shown, its program looked for as it is chosen (the
    /// engine asked for it), none for any other row. One not reached yet is looked for again as what the window shows
    /// changes (the engine's events, the folder's runs), else after a wait.</summary>
    private void ShowMetrics(ContainerRow? row)
    {
        string? key = row is { Kind: ContainerRowKind.Service, Project: { } project, Service: { } service } && DotnetOf is not null ? $"{project.Name}/{service}" : null;
        if (key == metricsOf)
        {
            if (key is not null && metricsMissing is not null && !metricsLooking)
            {
                LookForProgram(row!.Project!.Name, row.Service!);
            }
            return;
        }
        metricsOf = key;
        metricsAsked++;
        metricsLooking = false;
        metricsMissing = null;
        metricsWait = FirstLookWait;
        metricsLater?.Cancel();
        metricsLater = null;
        ProgramLookup = string.Empty;
        Metrics.Show(null, null);
        Metered(false);
        if (key is not null)
        {
            LookForProgram(row!.Project!.Name, row.Service!);
        }
    }

    /// <summary>Looks for a service's program once: found, its metrics are read; not reached, why is said in their place
    /// until the next lookup; none to look for, the pane is let go.</summary>
    private void LookForProgram(string project, string service)
    {
        int asked = ++metricsAsked;
        metricsLooking = true;
        metricsLater?.Cancel();
        metricsLater = null;
        ProgramLookup = "under way";
        Task<DotnetLookup?> lookup = DotnetOf!(project, service);
        // A lookup that asks the engine says so; one answered at once (no .NET service of a run) shows nothing meanwhile.
        if (!lookup.IsCompleted && metricsMissing is null)
        {
            Metered(true);
            Metrics.Show(null, null, Looking(service));
        }
        IdeErrors.Observe(LandAsync());

        async Task LandAsync()
        {
            DotnetLookup? found = await lookup;
            if (asked != metricsAsked)
            {
                return;
            }
            metricsLooking = false;
            metricsMissing = found?.WhyNot;
            if (found is null)
            {
                ProgramLookup = "none to look for: no .NET service of the folder's run, or its run ended";
                Metrics.Show(null, null);
                Metered(false);
                return;
            }
            Metered(true);
            if (found.WhyNot is { } why)
            {
                ProgramLookup = $"not reached: {why}";
                Metrics.Show(null, null, LookingAgain(why));
                LookLater(project, service);
                return;
            }
            ProgramLookup = $"found, process {found.Process.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            Metrics.Show(service, found.Process, null, found.Place);
        }
    }

    /// <summary>Looks for the service's program again after the wait, which doubles for the one after, unless another lookup
    /// or another row came first.</summary>
    private void LookLater(string project, string service)
    {
        CancellationTokenSource later = metricsLater = new();
        TimeSpan wait = metricsWait;
        metricsWait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestLookWait.Ticks));
        IdeErrors.Observe(WaitAsync());

        async Task WaitAsync()
        {
            // Not background work: it lasts until the program can be reached, which no screenshot or settling test waits for.
            await Task.Delay(wait, later.Token).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (!later.IsCancellationRequested && metricsLater == later)
            {
                metricsLater = null;
                LookForProgram(project, service);
            }
        }
    }

    /// <summary>The folder's runs started or ended programs: a .NET service's program not reached yet is looked for again.</summary>
    public void ProgramsChanged()
    {
        UiThread.Check();
        if (metricsMissing is not null)
        {
            ShowChosen();
        }
    }

    /// <summary>Whether the Stats tab gives a .NET service's metrics room under the engine's stats.</summary>
    private void Metered(bool shown)
    {
        Metrics.IsVisible = shown;
        statsPage.RowDefinitions[1].Height = shown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>Runs an action's command on a row as the window's one action of that id under way, the status bar saying
    /// its stage and then what came of it; the lists read again after. Null once done, else why it failed or did not run.</summary>
    public async Task<string?> ActAsync(string id, string action, ContainerRow row, IReadOnlyList<string>? given = null)
    {
        UiThread.Check();
        (IReadOnlyList<string>? words, string? whyNot) = given is null ? ContainerCommands.For(action, row) : (given, null);
        if (words is null)
        {
            return whyNot ?? $"{row.Name} cannot {action}";
        }
        if (parts.Work.Begin(id, ContainerCommands.Stage(action, row)) is not { } working)
        {
            return ActionWork.Busy(parts.Work.Of(id)!);
        }
        using (working)
        {
            ContainerEngine engine = EngineOf(row);
            string? problem = await BackgroundWork.Run(async () =>
            {
                try
                {
                    Core.ChildResult result = await engine.RunAsync(words, ContainerEngine.AskLimit * 15, working.Stopping);
                    return result.Succeeded ? null : ContainerEngine.Said(result);
                }
                catch (OperationCanceledException)
                {
                    return "Stopped";
                }
                catch (Exception failed) when (ChildFailures.Of(failed))
                {
                    return failed.Message;
                }
            });
            parts.Say(problem is null ? $"{ContainerEngine.Words(words)}: done" : $"{ContainerEngine.Words(words)} failed: {problem}");
            if (shown)
            {
                await RefreshAsync();
            }
            return problem;
        }
    }

    /// <summary>A row menu's item for one of the window's actions, as the window's row menus make them: titled as the action
    /// is, greyed with why while it cannot run.</summary>
    private MenuItem Item(string action) => parts.Item(action);

    /// <summary>A row (<see cref="RunRows"/>): a heading with its count, a project with its status, a service or container
    /// with its health and ports (links on the engine's machine while it runs), an image, volume or network with what it
    /// is.</summary>
    private RunRow Row(ContainerRow row)
    {
        IReadOnlyList<ContainerInfo> containers = row.Containers;
        bool runs = row.Kind is ContainerRowKind.Project or ContainerRowKind.AppHost or ContainerRowKind.Service or ContainerRowKind.Container;
        bool serves = row.Kind is ContainerRowKind.Service or ContainerRowKind.Container;
        bool trailing = row.Kind is ContainerRowKind.Project or ContainerRowKind.AppHost or ContainerRowKind.Group;
        return new RunRow(row.Depth, row.Label)
        {
            Heading = row.Kind == ContainerRowKind.Group,
            Expands = row.Expands,
            Expanded = row.Expanded,
            State = runs ? StateOf(containers) : RunState.None,
            StateTip = row.LeftUnstarted ? $"{NotStarted}: {Refusals.DontStart}; {ContainerRun.StartsService}" : string.Join(", ", containers.Select(c => $"{c.Name}: {c.Status}")),
            Health = serves ? containers.Select(c => c.Health).FirstOrDefault(h => h is not null) : null,
            Links = serves ? [.. containers.SelectMany(c => c.Ports).Distinct().Take(3).Select(port =>
                new RunLink(port.ToString(), containers.Any(c => c.IsRunning) ? port.LinkOn(State?.Endpoint) : null))] : [],
            Detail = trailing ? null : row.Detail,
            Trailing = trailing ? row.Detail ?? string.Empty : null,
            Tip = row.Container is { } one ? $"{one.Name} ({one.Id})\n{one.Image}\n{one.Status}" : row.Object is { } thing ? $"{thing.Name}\n{thing.Id}" : row.Name,
        };
    }

    /// <summary>The state dot's: failed (one restarting after each crash, or ended with an error) first, then running
    /// (unhealthy while its health check fails), paused between states, the rest stopped.</summary>
    private static RunState StateOf(IReadOnlyList<ContainerInfo> containers) =>
        containers.Any(c => c.IsFailed) ? RunState.Failed
        : containers.Any(c => c.IsRunning && c.Health == "unhealthy") ? RunState.Unhealthy
        : containers.Any(c => c.IsRunning) ? RunState.Running
        : containers.Any(c => c.IsPaused) ? RunState.Between
        : RunState.Stopped;
}
