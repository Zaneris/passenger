using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Telemetry;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Aspire;

/// <summary>A resource of the Aspire window's list at its depth: a parent's children are listed under it, opened and
/// closed by its chevron.</summary>
public sealed record AspireRow(AspireResource Resource, int Depth, bool Expands, bool Expanded) : ITreeRow
{
    bool ITreeRow.IsExpanded => Expanded;

    /// <summary>Whether its run left it unstarted, as its configuration says Don't start: Not started, never a failure.</summary>
    public bool LeftUnstarted { get; init; }
}

/// <summary>The Aspire window over an AppHost's resources: an icon toolbar of the window's actions, the resources as rows
/// (<see cref="RunRows"/>: a chevron over a parent's children, the state's dot, health's tag and addresses as links), the
/// chosen one's Details (<see cref="AspireDetailsPane"/>), the resources' consoles merged (<see cref="MergedLog"/>), the
/// dashboard's structured logs and traces (<see cref="TelemetryView"/>'s pages), and the AppHost's own console, on the frame
/// of a run's windows (<see cref="RunWindowFrame"/>). A view of the session's <see cref="AspireModel"/>, built as the window is
/// first shown.</summary>
public sealed class AspireView : DockPanel, IDisposable
{
    private const string LogTab = AspireModel.LogTab;
    private const string TelemetryTab = AspireModel.TelemetryTab;

    private readonly TextBlock status = new() { Classes = { "muted" }, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock empty = new() { Classes = { "emptyText" } };
    private readonly List<(Button Button, string Action)> tools = [];
    private readonly Button commands;
    private readonly ContentControl page = new();
    private readonly ContentControl appHostConsole = new();
    private readonly TextBlock readiness = new() { Classes = { "muted" }, Margin = new Thickness(8, 6), TextWrapping = TextWrapping.Wrap };
    private readonly IReadOnlyDictionary<string, ToggleButton> tabs;
    private readonly Dictionary<string, Control> pages;
    private readonly TextBlock consoleStatus = Said();
    private AspireModel model;
    private bool updating;
    private bool disposed;

    /// <summary>Each shown resource's console stream, by its name: the generation it reads and what ends it.</summary>
    private readonly Dictionary<string, (string Generation, CancellationTokenSource Cancel)> consoleStreams = new(StringComparer.Ordinal);
    private (string Name, string Words)[] offeredConsoles = [];

    /// <summary>Whether the console shows its streams' lines from their start, so a stream that comes or restarts adds its
    /// own after them; false once they are stopped, the next update reading the console again whole.</summary>
    private bool consoleLive;
    private int consoleGeneration;
    private Func<string, bool>? requestAction;
    private Func<string, MenuItem>? actionItem;

    public AspireView(AspireModel model)
    {
        this.model = model;
        AppSelector = new ComboBox { MaxWidth = 300, MinWidth = 200, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetName(AppSelector, "Aspire AppHost");
        ToolTip.SetTip(AppSelector, "Running AppHost (every request uses its absolute project path)");
        StackPanel bar = RunWindowFrame.Tools(AppSelector);
        // Its Refresh, then the service actions (titled once bound), then the resource's own.
        foreach ((string action, string icon, string tip) in (ReadOnlySpan<(string, string, string)>)[
            (AppActions.AspireDiscover, IconKeys.Search, "Discover running Aspire AppHosts"),
            (AppActions.AspireRefresh, IconKeys.Rerun, "Refresh resources"),
            .. RunWindows.ServiceActions.Select(service => (service.Action, service.Icon, service.Action)),
            (AppActions.AspireCommand, IconKeys.Menu, "The chosen resource's commands"),
            (AppActions.AspireOpenLink, IconKeys.Endpoints, "Open one of the chosen resource's addresses"),
            (AppActions.AspireSource, IconKeys.DocumentText, "Open the chosen resource's source"),
            (AppActions.AspireContainer, IconKeys.Containers, "Show the chosen resource's container in Containers")])
        {
            Button button = ToolButtons.Of(action, icon, tip, Request);
            tools.Add((button, action));
            bar.Children.Add(button);
        }
        commands = tools.Single(t => t.Action == AppActions.AspireCommand).Button;
        DockPanel top = new() { Children = { bar, status } };
        DockPanel.SetDock(bar, Dock.Left);

        ResourceList = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = RunRows.Template<AspireRow>(Row, row => this.model.Toggle(row), uri => Browse?.Invoke(uri), "aspireUrl"),
        };
        AutomationProperties.SetName(ResourceList, "Aspire resources");
        ResourceList.SelectionChanged += (_, _) =>
        {
            if (!updating && !this.model.Filling)
            {
                this.model.Choose(ResourceList.SelectedItem as RunItem<AspireRow>);
                RowChosen?.Invoke(this, EventArgs.Empty);
            }
        };
        RunRows.Toggles<AspireRow>(ResourceList, row => this.model.Toggle(row));
        ListMenus.Attach(ResourceList, RowMenu);
        AppSelector.SelectionChanged += (_, _) =>
        {
            ToolTip.SetTip(AppSelector, AppSelector.SelectedItem?.ToString() ?? "Choose a running AppHost");
            if (!updating)
            {
                this.model.ChooseApp(AppSelector.SelectedItem as AspireApp);
            }
        };

        Details = new AspireDetailsPane(uri => Browse?.Invoke(uri), OpenFile);
        Log = new MergedLog("aspireLogFind", consoleStatus);
        Log.Toggled += (_, _) => UpdateConsole(again: true);
        // A log's trace and a span's logs are the window's actions here, as its other buttons are.
        Telemetry = new TelemetryView(AppActions.AspireShowTrace, AppActions.AspireShowRelatedLogs, id => requestAction?.Invoke(id) == true);
        Telemetry.LinkClicked += (_, link) => LinkClicked?.Invoke(this, link);
        Telemetry.Folders = AppHostFolders;
        Telemetry.PageWanted += (_, _) => this.model.ShowPage(TelemetryTab);
        Telemetry.ShownChanged += (_, _) => UpdateTelemetry();
        Console.LinkClicked += (_, link) => LinkClicked?.Invoke(this, link);
        Console.Links.Folders = AppHostFolders;

        pages = new(StringComparer.Ordinal)
        {
            [LogTab] = Log,
            [AspireModel.DetailsTab] = Details,
            [TelemetryTab] = Telemetry.Both,
            [AspireModel.AppHostTab] = WithHeader(readiness, appHostConsole),
        };
        tabs = RunWindowFrame.Build(this, top, new Panel { Children = { ResourceList, empty } }, page, AspireModel.Pages, AspireModel.PageAction, "aspire", Request);
        DetachedFromVisualTree += (_, _) => this.model.SetDisplayVisible(false);
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty && !IsVisible)
            {
                this.model.SetDisplayVisible(false);
            }
        };
        Follow(model);
        ShowAll();
    }

    public ComboBox AppSelector { get; }

    /// <summary>The resources, each a row (<see cref="AspireRow"/>) at its depth.</summary>
    public ListBox ResourceList { get; }

    /// <summary>The chosen resource's Details tab.</summary>
    public AspireDetailsPane Details { get; }

    /// <summary>The resources' consoles merged, a toggle each, with Find.</summary>
    public MergedLog Log { get; }

    public Editors.FindBar Find => Log.Find;
    public OutputView Console => Log.Console;

    /// <summary>The AppHost's structured logs and traces.</summary>
    public TelemetryView Telemetry { get; }
    public ListBox StructuredLogs => Telemetry.StructuredLogs;
    public ListBox TraceList => Telemetry.TraceList;
    public ListBox SpanList => Telemetry.SpanList;

    /// <summary>The window's toolbar buttons, by the actions they run.</summary>
    public IReadOnlyDictionary<string, Button> Tools => tools.ToDictionary(t => t.Action, t => t.Button);

    /// <summary>Opens an address in the browser (the window's one way to it).</summary>
    public Action<Uri>? Browse { get; set; }

    /// <summary>A person chose a resource's row (a click, the keys), never the list read again.</summary>
    public event EventHandler? RowChosen;
    public event EventHandler<LinkTarget>? LinkClicked;

    /// <summary>Shows another session's model, what the last one streamed let go.</summary>
    public void Bind(AspireModel next)
    {
        UiThread.Check();
        if (next == model)
        {
            return;
        }
        Unfollow(model);
        model = next;
        Follow(next);
        StopConsole();
        offeredConsoles = [];
        Log.Offer([], forget: true);
        ClearTelemetry();
        ShowAll();
    }

    private void Follow(AspireModel followed)
    {
        followed.AppsChanged += OnAppsChanged;
        followed.RowsChanged += OnRowsChanged;
        followed.StatusChanged += OnStatusChanged;
        followed.AppHostChanged += OnAppHostChanged;
        followed.PageShown += OnPageShown;
        followed.StreamsDue += OnStreamsDue;
        followed.Reading += OnReading;
        followed.AppReset += OnAppReset;
    }

    private void Unfollow(AspireModel followed)
    {
        followed.AppsChanged -= OnAppsChanged;
        followed.RowsChanged -= OnRowsChanged;
        followed.StatusChanged -= OnStatusChanged;
        followed.AppHostChanged -= OnAppHostChanged;
        followed.PageShown -= OnPageShown;
        followed.StreamsDue -= OnStreamsDue;
        followed.Reading -= OnReading;
        followed.AppReset -= OnAppReset;
    }

    /// <summary>Everything the model holds, as the view is built or bound to another.</summary>
    private void ShowAll()
    {
        updating = true;
        try
        {
            ResourceList.ItemsSource = model.Items;
        }
        finally
        {
            updating = false;
        }
        ShowApps();
        ShowRows(reveal: false);
        ShowStatus();
        ShowAppHost();
        ShowPage(another: true);
    }

    private void OnAppsChanged(object? sender, EventArgs e) => ShowApps();
    private void OnRowsChanged(object? sender, bool reveal) => ShowRows(reveal);
    private void OnStatusChanged(object? sender, EventArgs e) => ShowStatus();
    private void OnAppHostChanged(object? sender, EventArgs e) => ShowAppHost();
    private void OnPageShown(object? sender, bool another) => ShowPage(another);

    private void OnStreamsDue(object? sender, EventArgs e)
    {
        UpdateConsole();
        UpdateTelemetry();
    }

    private void OnReading(object? sender, EventArgs e)
    {
        StopConsole();
        ClearTelemetry();
    }

    private void OnAppReset(object? sender, EventArgs e)
    {
        StopConsole();
        offeredConsoles = [];
        Log.Offer([], forget: true);
        ClearTelemetry();
        UpdateTelemetry();
    }

    private void ShowApps()
    {
        updating = true;
        try
        {
            AppSelector.ItemsSource = model.Apps;
            AppSelector.SelectedItem = model.SelectedApp;
        }
        finally
        {
            updating = false;
        }
        ToolTip.SetTip(AppSelector, model.SelectedApp?.ToString() ?? "Choose a running AppHost");
    }

    private void ShowRows(bool reveal)
    {
        updating = true;
        try
        {
            ResourceList.SelectedItem = model.ChosenItem;
        }
        finally
        {
            updating = false;
        }
        if (reveal && model.ChosenItem is { } chosen)
        {
            ResourceList.ScrollIntoView(chosen);
        }
        ShowEmpty();
        Details.Show(model.SelectedResource, model.Chosen?.LeftUnstarted == true);
    }

    /// <summary>What the window says of its AppHost and resources: beside the toolbar, and in the list's place while it is empty.</summary>
    private void ShowStatus()
    {
        status.Text = model.Status;
        ToolTip.SetTip(status, model.Status);
        ShowEmpty();
    }

    private void ShowEmpty()
    {
        empty.Text = model.Status;
        empty.IsVisible = model.Items.Count == 0;
    }

    private void ShowAppHost()
    {
        readiness.Text = model.ContainerStatus;
        if (appHostConsole.Content == model.AppHostConsole)
        {
            return;
        }
        if (appHostConsole.Content is OutputView previous)
        {
            previous.LinkClicked -= AppHostLinkClicked;
        }
        appHostConsole.Content = model.AppHostConsole;
        if (model.AppHostConsole is { } output)
        {
            output.LinkClicked += AppHostLinkClicked;
            output.Links.Folders = AppHostFolders;
        }
    }

    private void AppHostLinkClicked(object? sender, LinkTarget link) => LinkClicked?.Invoke(this, link);

    private IReadOnlyList<string> AppHostFolders() => model.SelectedApp is { } app ? [Path.GetDirectoryName(app.AppHostPath)!] : [];

    private void ShowPage(bool another)
    {
        foreach ((string name, ToggleButton tab) in tabs)
        {
            tab.IsChecked = name == model.ShownPage;
        }
        page.Content = pages[model.ShownPage];
        if (another)
        {
            UpdateConsole();
            UpdateTelemetry();
        }
    }

    /// <summary>The window's actions run by the toolbar's buttons (each showing its work), the tabs and the rows' menus, whose
    /// items <paramref name="item"/> makes as the window's row menus make them (greyed with why).</summary>
    public void BindActions(ActionRegistry actions, Func<string, bool> request, Func<string, MenuItem> item)
    {
        if (requestAction is not null)
        {
            throw new InvalidOperationException("Aspire actions are already bound.");
        }
        requestAction = request;
        actionItem = item;
        foreach ((Button button, string action) in tools)
        {
            ActionTips.Set(button, action, actions[action]?.Title ?? ToolTip.GetTip(button) as string ?? action);
        }
    }

    /// <summary>The chosen resource's addresses as a menu under the toolbar's button, each opened in the browser.</summary>
    public void ShowEndpointChoices()
    {
        if (model.SelectedResource is not { } resource)
        {
            return;
        }
        ContextMenu menu = new();
        foreach (Uri target in AspireModel.Links(resource))
        {
            MenuItem item = new() { Header = target.AbsoluteUri };
            item.Click += (_, _) => model.AskLink(target, () => requestAction?.Invoke(AppActions.AspireOpenLink));
            menu.Items.Add(item);
        }
        menu.Open(tools.Single(t => t.Action == AppActions.AspireOpenLink).Button);
    }

    private void OpenFile(string path) => LinkClicked?.Invoke(this, new FileLink(path, new TextPosition(0, 0)));

    public void OpenFind() => Find.Open(replace: false);

    public void ShowSelectedTrace() => Telemetry.ShowSelectedTrace();

    public void ShowRelatedLogs() => Telemetry.ShowRelatedLogs();

    /// <summary>The chosen resource's commands as a menu under the toolbar's button.</summary>
    public void ShowCommandChoices()
    {
        if (model.Selection is not { } selected)
        {
            return;
        }
        ContextMenu menu = new() { ItemsSource = CommandItems(selected) };
        menu.Open(commands);
    }

    /// <summary>A resource's commands as menu items, those it advertises disabled (or that need a form the CLI must give) greyed.</summary>
    private IReadOnlyList<Control> CommandItems(AspireSelection selected) =>
    [
        .. selected.Resource.Commands.Where(command => !command.Hidden).OrderBy(Order).Select(command =>
        {
            MenuItem item = ListMenus.Item(command.DisplayName, null, () => model.AskCommand(selected, command.Name, () => requestAction?.Invoke(AppActions.AspireCommand)),
                enabled: !model.Executing && command.Enabled && (!command.HasArguments || model.ArgumentCommand is not null));
            ToolTip.SetTip(item, command.HasArguments && model.ArgumentCommand is null
                ? "This command needs an argument form. Use the Aspire CLI; Passenger will not send incomplete arguments."
                : command.Description ?? command.Name);
            return (Control)item;
        }),
    ];

    private static int Order(AspireResourceCommand command) => command.Data.TryGetProperty("sortOrder", out JsonElement order)
        && order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out int number) ? number : 0;

    private void StopConsole()
    {
        consoleGeneration++;
        consoleLive = false;
        foreach ((string _, CancellationTokenSource cancel) in consoleStreams.Values)
        {
            cancel.Cancel();
            cancel.Dispose();
        }
        consoleStreams.Clear();
    }

    /// <summary>Streams the consoles of the resources whose toggles are on into the merged log while the Console page shows.
    /// A resource that comes, or restarts (another generation), has its own stream started and its lines added after those
    /// shown, one that goes has its stream ended, and the rest stream on; the console is cleared and every stream read again
    /// from its start only when it was stopped (hidden, another tab, Refresh) or <paramref name="again"/> (a toggle turned)
    /// asks, so one resource's restart never reads the others' backlogs again.</summary>
    private void UpdateConsole(bool again = false)
    {
        UiThread.Check();
        if (disposed || model.IsDisposed || !model.IsDisplayed || model.ShownPage != LogTab || model.SelectedApp is not { } app || model.ConsoleProvider is not { } provider)
        {
            StopConsole();
            return;
        }
        AspireResource[] offered = model.Visible();
        if (!again)
        {
            (string Name, string Words)[] sources = [.. offered.Select(resource => (resource.Name, resource.DisplayName))];
            if (!sources.SequenceEqual(offeredConsoles))
            {
                offeredConsoles = sources;
                Log.Offer(sources, forget: false);
            }
        }
        AspireResource[] shown = [.. offered.Where(resource => Log.Shows(resource.Name))];
        if (again || !consoleLive)
        {
            StopConsole();
            consoleLive = true;
            Console.Clear();
            consoleStatus.Text = "Live · retained output is bounded";
        }
        HashSet<string> named = shown.Select(resource => resource.Name).ToHashSet(StringComparer.Ordinal);
        foreach ((string name, (string generation, CancellationTokenSource cancel)) in consoleStreams.ToArray())
        {
            if (!named.Contains(name) || shown.First(resource => resource.Name == name).Generation != generation)
            {
                cancel.Cancel();
                cancel.Dispose();
                consoleStreams.Remove(name);
            }
        }
        foreach (AspireResource resource in shown.Where(resource => !consoleStreams.ContainsKey(resource.Name)))
        {
            CancellationTokenSource reading = new();
            consoleStreams.Add(resource.Name, (resource.Generation, reading));
            AspireSelection selected = new(app.AppHostPath, resource);
            IAsyncEnumerable<IReadOnlyList<AspireConsoleLog>>? stream = provider(selected, reading.Token);
            if (stream is not null)
            {
                IdeErrors.Observe(ReadConsoleAsync(stream, consoleGeneration, selected, reading.Token));
            }
        }
    }

    /// <summary>Whether a stream still stands for its resource's console: the console not read again since it began and its
    /// resource's stream the one of its generation.</summary>
    private bool Streams(int version, AspireSelection selected, CancellationToken cancel) =>
        !disposed && model.IsDisplayed && !cancel.IsCancellationRequested && version == consoleGeneration && model.ShownPage == LogTab
        && model.SelectedApp?.AppHostPath == selected.AppHostPath
        && consoleStreams.TryGetValue(selected.Resource.Name, out (string Generation, CancellationTokenSource Cancel) stream)
        && stream.Generation == selected.Resource.Generation;

    private async Task ReadConsoleAsync(IAsyncEnumerable<IReadOnlyList<AspireConsoleLog>> stream, int version,
        AspireSelection selected, CancellationToken cancel)
    {
        string ended = "Live console ended. Refresh to reconnect.";
        int? lastLineNumber = null;
        try
        {
            await foreach (IReadOnlyList<AspireConsoleLog> lines in stream.WithCancellation(cancel).ConfigureAwait(false))
            {
                await UiThread.InvokeAsync(() =>
                {
                    if (Streams(version, selected, cancel))
                    {
                        foreach (AspireConsoleLog line in lines)
                        {
                            if (line.LineNumber is { } lineNumber)
                            {
                                if (lastLineNumber is { } previous && lineNumber <= previous)
                                {
                                    continue;
                                }
                                lastLineNumber = lineNumber;
                            }
                            Console.Sink.Write($"[{selected.Resource.DisplayName}] {line.Content}\n", line.IsError ? ConsoleStream.Error : ConsoleStream.Output);
                        }
                    }
                    return true;
                }).WaitAsync(cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            ended = "Live console stopped. Refresh the resource to reconnect.";
        }
        catch (Exception error) when (AspireModel.Expected(error))
        {
            ended = "Live console unavailable. Refresh the resource and check its connection.";
        }
        if (!cancel.IsCancellationRequested)
        {
            UiThread.Post(() =>
            {
                if (!disposed && !cancel.IsCancellationRequested && version == consoleGeneration)
                {
                    consoleStatus.Text = ended;
                }
            });
        }
    }

    private void UpdateTelemetry() =>
        Telemetry.Watch(!disposed && !model.IsDisposed && model.IsDisplayed && model.SelectedApp is { } selected ? model.TelemetryProvider?.Invoke(selected.AppHostPath) : null,
            model.ShownPage == TelemetryTab ? Telemetry.Shown : null);

    private void ClearTelemetry()
    {
        Console.Clear();
        Telemetry.Clear();
        consoleStatus.Text = string.Empty;
    }

    /// <summary>A resource's row (<see cref="RunRows"/>): its state's dot, what it runs after its name, its health's tag and
    /// its addresses as links.</summary>
    private static RunRow Row(AspireRow row)
    {
        AspireResource resource = row.Resource;
        return new RunRow(row.Depth, resource.DisplayName)
        {
            Expands = row.Expands,
            Expanded = row.Expanded,
            State = row.LeftUnstarted ? RunState.Stopped : StateOf(resource),
            StateTip = row.LeftUnstarted ? $"{AspireAppRun.NotStarted}. {AspireAppRun.NotStartedWhy}" : resource.State ?? "Unknown state",
            Health = AspireDetailsPane.Health(resource),
            Links = [.. AspireModel.Links(resource).Take(3).Select(url => new RunLink(url.Authority, url, url.AbsoluteUri))],
            Detail = resource.Source is { } source ? Path.IsPathFullyQualified(source) ? Path.GetFileName(source) : source
                : AspireJson.String(resource.Data, "resourceType"),
            Tip = resource.ToString(),
        };
    }

    /// <summary>A resource's state as the dot says it: Aspire's own style of it first (an error's red), then running (unhealthy
    /// while a health report fails), starting or stopping between states, the rest stopped.</summary>
    private static RunState StateOf(AspireResource resource) => (resource.State, AspireJson.String(resource.Data, "stateStyle")) switch
    {
        (_, "error") or ("FailedToStart" or "RuntimeUnhealthy", _) => RunState.Failed,
        ("Running", _) => resource.HealthStatus == "Unhealthy" ? RunState.Unhealthy : RunState.Running,
        ("Starting" or "Waiting" or "Stopping" or "Building" or "Activating", _) => RunState.Between,
        _ => RunState.Stopped,
    };

    /// <summary>A row's menu: the service actions first (greyed with why on a resource that is no .NET service), then its
    /// commands, then its address, source and container.</summary>
    private IReadOnlyList<Control> RowMenu()
    {
        if (model.Selection is not { } selected || actionItem is not { } made)
        {
            return [];
        }
        Control Item(string id) => made(id);
        IReadOnlyList<Control> advertised = CommandItems(selected);
        return
        [
            .. RunWindows.ServiceActions.Select(service => Item(service.Action)),
            .. advertised.Count > 0 ? (Control[])[new Separator(), .. advertised] : [],
            new Separator(),
            Item(AppActions.AspireOpenLink),
            Item(AppActions.AspireSource),
            Item(AppActions.AspireContainer),
        ];
    }

    /// <summary>One of the window's actions asked for by a button or a tab, once bound.</summary>
    private void Request(string action) => requestAction?.Invoke(action);

    /// <summary>Shows the Telemetry tab on one of its pages, as a log's trace or a span's logs asks for it.</summary>
    public void ShowTelemetry(TelemetryPage shown)
    {
        model.ShowPage(TelemetryTab);
        Telemetry.ShowPage(shown);
    }

    private static TextBlock Said() => new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private static DockPanel WithHeader(Control header, Control body)
    {
        DockPanel.SetDock(header, Dock.Top);
        return new DockPanel { Children = { header, body } };
    }

    public void Dispose()
    {
        UiThread.Check();
        if (!disposed)
        {
            disposed = true;
            Unfollow(model);
            if (appHostConsole.Content is OutputView shown)
            {
                shown.LinkClicked -= AppHostLinkClicked;
            }
            appHostConsole.Content = null;
            Find.Detach();
            Telemetry.Dispose();
            StopConsole();
        }
    }
}
