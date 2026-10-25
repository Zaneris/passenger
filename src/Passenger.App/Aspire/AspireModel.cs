using System.Collections.ObjectModel;
using System.Text.Json;
using Passenger.App.Telemetry;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>What the Aspire window shows of a folder's AppHosts, kept whether or not its view is built (#447): the AppHosts
/// found and the one chosen, its resources as rows and the row chosen, the tab shown, what the window says, the engine's
/// word, the AppHost's console, and the reads and commands that change them.</summary>
public sealed class AspireModel : IDisposable
{
    // The tabs' names are those every list of a run's services has, in its order (RunWindows.Tabs).
    internal const string LogTab = RunWindows.LogTab;
    internal const string DetailsTab = RunWindows.DetailsTab;
    internal const string TelemetryTab = RunWindows.TelemetryTab;
    internal const string AppHostTab = RunWindows.AppHostLogTab;

    private readonly AspireClient client;
    private readonly HashSet<string> collapsed = new(StringComparer.Ordinal);
    private readonly ObservableCollection<RunItem<AspireRow>> items = [];
    private readonly CancellationTokenSource commandLifetime = new();
    private IReadOnlyList<AspireResource> resources = [];
    private IReadOnlyList<AspireApp> apps = [];
    private RunItem<AspireRow>? chosen;
    private CancellationTokenSource? reading;
    private int generation;
    private int resourceUpdateVersion;
    private bool disposed;
    private bool executing;
    private bool liveResources;
    private bool displayVisible;
    private bool filling;
    private Func<AspireSelection, string, Func<bool>, CancellationToken, Task>? argumentCommand;

    /// <summary>Discover or Refresh asked while the window was hidden, which their actions show it for first: done as it shows.</summary>
    private bool discoverWhenShown;
    private bool refreshWhenShown;
    private string? pendingCommand;
    private AspireSelection? pendingCommandSelection;
    private Uri? pendingLink;

    public AspireModel(AspireClient? client = null) => this.client = client ?? new AspireClient();

    /// <summary>The running AppHosts found, the window's and those the CLI discovered.</summary>
    public IReadOnlyList<AspireApp> Apps => apps;

    /// <summary>The AppHost whose resources are listed.</summary>
    public AspireApp? SelectedApp { get; private set; }

    /// <summary>The rows listed now.</summary>
    public IReadOnlyList<AspireRow> Rows => [.. items.Select(item => item.Data)];

    /// <summary>The rows as the list holds them, each kept by its resource's name.</summary>
    internal ObservableCollection<RunItem<AspireRow>> Items => items;

    /// <summary>Whether the rows are being put in place, which no choice of the list's is.</summary>
    internal bool Filling => filling;

    /// <summary>The list's item of a resource (by its name or the name it shows), which its row's control is made from.</summary>
    public RunItem<AspireRow>? ItemOf(string name) =>
        items.FirstOrDefault(item => item.Key == name) ?? items.FirstOrDefault(item => item.Data.Resource.DisplayName == name);

    /// <summary>The row chosen, which the view's list follows.</summary>
    internal RunItem<AspireRow>? ChosenItem => chosen;

    internal AspireRow? Chosen => chosen?.Data;
    public AspireResource? SelectedResource => Chosen?.Resource;
    public AspireSelection? Selection => SelectedApp is { } app && SelectedResource is { } resource ? new(app.AppHostPath, resource) : null;

    /// <summary>What the window says of its AppHost and resources, beside its toolbar and in the list's place while it is empty.</summary>
    public string Status { get; private set; } = "Choose a running Aspire AppHost, or refresh to discover one.";

    public IReadOnlyList<AspireResource> ResourceSnapshots => resources;

    /// <summary>The tab shown: Log, Details, Telemetry or AppHost Log.</summary>
    public string ShownPage { get; private set; } = LogTab;

    /// <summary>What the container engine's state means for the AppHost's container resources, over its console.</summary>
    public string ContainerStatus { get; private set; } = string.Empty;

    /// <summary>The chosen AppHost's own console, or the last one run.</summary>
    public OutputView? AppHostConsole { get; private set; }

    /// <summary>Whether the window shows its resources now: only then are they read, and their consoles and telemetry streamed.</summary>
    public bool IsDisplayed => displayVisible;

    /// <summary>Whether a resource command runs now, which the commands' menu greys the others for.</summary>
    internal bool Executing => executing;

    internal bool IsDisposed => disposed;

    /// <summary>Returns an owned connection snapshot task, or null to use the CLI for an external AppHost. Called on the UI thread.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<AspireResource>>?>? ResourceProvider { get; set; }

    /// <summary>Returns an owned live console stream, or null if unavailable. Called on the UI thread.</summary>
    public Func<AspireSelection, CancellationToken, IAsyncEnumerable<IReadOnlyList<AspireConsoleLog>>?>? ConsoleProvider { get; set; }

    /// <summary>Returns this app's dashboard endpoint and key on the UI thread. Null means no owned telemetry.</summary>
    public Func<string, TelemetrySource?>? TelemetryProvider { get; set; }

    /// <summary>Whether the run of the AppHost at a path left a resource unstarted, as its configuration says Don't start
    /// (<see cref="AspireAppRun.IsLeftUnstarted"/>); null for an AppHost the window did not start. Called on the UI thread.</summary>
    public Func<string, AspireResource, bool>? LeftUnstarted { get; set; }

    /// <summary>Shows a container in the Containers window, by its id.</summary>
    public Action<string>? SelectContainer { get; set; }

    public string? SelectedContainerId => SelectedResource is { } resource
        ? AspireJson.Properties(resource.Data, "properties").FirstOrDefault(property => property.Name == "container.id").Value is { ValueKind: JsonValueKind.String } id ? id.GetString() : null
        : null;

    /// <summary>The chosen resource's addresses a browser opens.</summary>
    public IReadOnlyList<Uri> SelectedLinks => SelectedResource is { } resource ? Links(resource) : [];

    /// <summary>Native execution for all owned commands, including server-owned argument prompts. Recheck the selection guard before sending.</summary>
    public Func<AspireSelection, string, Func<bool>, CancellationToken, Task>? ArgumentCommand
    {
        get => argumentCommand;
        set
        {
            UiThread.Check();
            argumentCommand = value;
            RowsChanged?.Invoke(this, false);
        }
    }

    public event EventHandler<AspireApp?>? AppSelectionChanged;
    public event EventHandler<AspireSelection?>? ResourceSelectionChanged;

    /// <summary>The AppHosts found, or the one chosen, changed: the view's chooser follows.</summary>
    internal event EventHandler? AppsChanged;

    /// <summary>The rows or the one chosen changed (true: the one chosen to be scrolled to): the view's list and Details follow.</summary>
    internal event EventHandler<bool>? RowsChanged;

    /// <summary>What the window says changed.</summary>
    internal event EventHandler? StatusChanged;

    /// <summary>The engine's word or the AppHost's console changed.</summary>
    internal event EventHandler? AppHostChanged;

    /// <summary>The tab shown was set (true when it is another).</summary>
    internal event EventHandler<bool>? PageShown;

    /// <summary>The consoles and telemetry follow what is shown now: the window shown or hidden, a resource chosen, the
    /// resources read.</summary>
    internal event EventHandler? StreamsDue;

    /// <summary>A read of the resources begins: the consoles' streams end, and what they and the telemetry showed is cleared.</summary>
    internal event EventHandler? Reading;

    /// <summary>Another AppHost chosen: the consoles' toggles and the telemetry are another's.</summary>
    internal event EventHandler? AppReset;

    /// <summary>The window shows its resources, or no longer does (the view hidden, its tool window moved or closed): a
    /// Discover or Refresh asked while hidden is done as it shows.</summary>
    public void SetDisplayVisible(bool visible)
    {
        UiThread.Check();
        if (displayVisible == visible || disposed)
        {
            return;
        }
        displayVisible = visible;
        if (!visible)
        {
            reading?.Cancel();
            generation++;
        }
        StreamsDue?.Invoke(this, EventArgs.Empty);
        if (visible && discoverWhenShown)
        {
            BackgroundWork.Track(RefreshAppsAsync());
        }
        else if (visible && refreshWhenShown)
        {
            BackgroundWork.Track(RequestRefreshAsync());
        }
    }

    public void SetAppHostConsole(OutputView? output)
    {
        UiThread.Check();
        AppHostConsole = output;
        AppHostChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetContainerStatus(string text)
    {
        UiThread.Check();
        ContainerStatus = text;
        AppHostChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The address a menu of the chosen resource's addresses gave Open Aspire Resource Endpoint, taken once.</summary>
    public Uri? TakePendingLink()
    {
        Uri? link = pendingLink;
        pendingLink = null;
        return link;
    }

    /// <summary>The command a menu of a resource's commands gave Run Aspire Resource Command, taken once, while the same
    /// resource of the same generation is chosen.</summary>
    public string? TakePendingCommand()
    {
        string? command = pendingCommand;
        pendingCommand = null;
        AspireSelection? captured = pendingCommandSelection;
        pendingCommandSelection = null;
        return command is not null && Selection is { } current && captured is not null && Same(current, captured) ? command : null;
    }

    /// <summary>Asks for the window's action with an address of the chosen resource's to open.</summary>
    internal void AskLink(Uri target, Action ask)
    {
        pendingLink = target;
        ask();
    }

    /// <summary>Asks for the window's action with a command of a resource's, while that resource is still the one chosen.</summary>
    internal void AskCommand(AspireSelection selected, string command, Action ask)
    {
        if (Selection is not { } current || !Same(current, selected))
        {
            return;
        }
        pendingCommand = command;
        pendingCommandSelection = selected;
        ask();
        pendingCommand = null;
        pendingCommandSelection = null;
    }

    private static bool Same(AspireSelection one, AspireSelection other) => one.AppHostPath == other.AppHostPath
        && one.Resource.Name == other.Resource.Name && one.Resource.Generation == other.Resource.Generation;

    public void SetApps(IEnumerable<AspireApp> found)
    {
        UiThread.Check();
        ObjectDisposedException.ThrowIf(disposed, this);
        AspireApp? old = SelectedApp;
        AspireApp[] next = found.Take(AspireClient.ResourceLimit + 1)
            .Select(app => app with { AppHostPath = AspireClient.CanonicalAppHost(app.AppHostPath) }).ToArray();
        if (next.Length > AspireClient.ResourceLimit)
        {
            throw new ArgumentException("Too many AppHosts.", nameof(found));
        }
        apps = next;
        SelectedApp = next.FirstOrDefault(app => app.AppHostPath == old?.AppHostPath) ?? next.FirstOrDefault();
        AppsChanged?.Invoke(this, EventArgs.Empty);
        if (old?.AppHostPath != SelectedApp?.AppHostPath)
        {
            ResetApp();
            AppSelectionChanged?.Invoke(this, SelectedApp);
        }
    }

    /// <summary>Another AppHost chosen in the view's chooser.</summary>
    internal void ChooseApp(AspireApp? app)
    {
        UiThread.Check();
        if (disposed || app == SelectedApp)
        {
            return;
        }
        SelectedApp = app;
        AppsChanged?.Invoke(this, EventArgs.Empty);
        ResetApp();
        AppSelectionChanged?.Invoke(this, SelectedApp);
    }

    /// <summary>Another row chosen in the view's list (a click, the keys).</summary>
    internal void Choose(RunItem<AspireRow>? item)
    {
        UiThread.Check();
        if (disposed || item == chosen)
        {
            return;
        }
        chosen = item;
        Chose();
    }

    /// <summary>Opens or closes a row with rows under it, as its chevron does; a row it hides that was chosen gives its
    /// choice to the row closed.</summary>
    public void Toggle(AspireRow? row)
    {
        if (row is not { Expands: true } || disposed)
        {
            return;
        }
        if (!collapsed.Remove(row.Resource.Name))
        {
            collapsed.Add(row.Resource.Name);
        }
        string? before = SelectedResource?.Name;
        Fill(before, row.Resource.Name);
        if (SelectedResource?.Name != before)
        {
            Chose();
        }
    }

    /// <summary>What a choice of another row does: its details shown, the window told, its streams followed.</summary>
    private void Chose()
    {
        RowsChanged?.Invoke(this, false);
        ResourceSelectionChanged?.Invoke(this, Selection);
        StreamsDue?.Invoke(this, EventArgs.Empty);
    }

    public void ShowPage(string name)
    {
        UiThread.Check();
        if (!Pages.Contains(name))
        {
            throw new ArgumentException("Unknown Aspire page.", nameof(name));
        }
        bool another = ShownPage != name;
        ShownPage = name;
        PageShown?.Invoke(this, another);
    }

    /// <summary>Call after runtime changes, as well as from the Refresh button. No perpetual background work is retained.</summary>
    public void Refresh()
    {
        UiThread.Check();
        if (!disposed)
        {
            IdeErrors.Observe(RequestRefreshAsync());
        }
    }

    public async Task RequestRefreshAsync()
    {
        UiThread.Check();
        if (disposed)
        {
            return;
        }
        refreshWhenShown = !displayVisible;
        if (refreshWhenShown)
        {
            return;
        }
        await RefreshAsync();
    }

    public async Task RefreshAppsAsync()
    {
        UiThread.Check();
        discoverWhenShown = !displayVisible;
        if (disposed || discoverWhenShown)
        {
            return;
        }
        (int version, CancellationToken cancel) = BeginRead();
        Say("Checking Aspire CLI and discovering running AppHosts…");
        await WorkAsync(version, cancel, async () =>
        {
            AspireAvailability available = await client.ProbeAsync(cancel);
            if (!available.Available)
            {
                await ApplyAsync(version, () => { SetApps([]); Say(available.Message); });
                return;
            }
            IReadOnlyList<AspireApp> found = await client.DiscoverRunningAppHostsAsync(cancel);
            await ApplyAsync(version, () =>
            {
                SetApps(found);
                if (found.Count == 0)
                {
                    Say("No running AppHosts. Start an AppHost with Aspire, then refresh AppHosts.");
                }
            });
        });
        if (!disposed && displayVisible && SelectedApp is not null)
        {
            await RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        UiThread.Check();
        if (disposed || !displayVisible || SelectedApp is not { } app)
        {
            return;
        }
        (int version, CancellationToken cancel) = BeginRead();
        int resourceUpdates = resourceUpdateVersion;
        string? selectedName = SelectedResource?.Name;
        Say("Refreshing resource snapshot…");
        Reading?.Invoke(this, EventArgs.Empty);
        await WorkAsync(version, cancel, async () =>
        {
            (IReadOnlyList<AspireResource> found, bool live) = await ReadResourcesAsync(app.AppHostPath, cancel);
            await ApplyAsync(version, () =>
            {
                if (live && resourceUpdates != resourceUpdateVersion)
                {
                    StreamsDue?.Invoke(this, EventArgs.Empty);
                    return;
                }
                resources = found;
                Fill(selectedName);
                liveResources = live;
                Say(live ? $"Live · {Core.Plural.Of(resources.Count, "resource")}" : $"Snapshot · {Core.Plural.Of(resources.Count, "resource")} · {DateTime.Now:T} · Refresh to update");
                StreamsDue?.Invoke(this, EventArgs.Empty);
                ResourceSelectionChanged?.Invoke(this, Selection);
            });
        });
    }

    public async Task ExecuteSelectedCommandAsync(string name)
    {
        UiThread.Check();
        if (Selection is not { } selected || disposed || executing)
        {
            return;
        }
        (int version, _) = BeginRead();
        CancellationToken cancel = commandLifetime.Token;
        executing = true;
        Say("Executing advertised resource command…");
        try
        {
            if ((liveResources && argumentCommand is not null)
                || selected.Resource.Commands.FirstOrDefault(command => command.Name == name) is { HasArguments: true })
            {
                try
                {
                    Func<AspireSelection, string, Func<bool>, CancellationToken, Task> execute = argumentCommand
                        ?? throw new InvalidOperationException("Native command arguments are unavailable for this AppHost.");
                    await execute(selected, name, () => !disposed && version == generation && Selection is { } current && Same(current, selected), cancel);
                    if (version == generation)
                    {
                        await RequestRefreshAsync();
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                }
                catch (Exception error) when (Expected(error))
                {
                    await ApplyAsync(version, () => Say(SafeError(error)));
                }
                return;
            }
            await WorkAsync(version, cancel, () => client.ExecuteCommandAsync(selected.AppHostPath, selected.Resource, name, cancel));
            if (!disposed && version == generation)
            {
                await RequestRefreshAsync();
            }
        }
        finally
        {
            executing = false;
        }
    }

    /// <summary>Applies one owned AppHost batch. Health-only changes keep telemetry and the active console intact.</summary>
    public void ApplyResourceSnapshot(IReadOnlyList<AspireResource> snapshot)
    {
        UiThread.Check();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (snapshot.Count > AspireInteractions.ResourceLimit
            || snapshot.Select(resource => resource.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Count)
        {
            throw new InvalidDataException("The owned AppHost snapshot exceeds 256 resources or has duplicate names.");
        }
        AspireResource? old = SelectedResource;
        bool consoleChanged = !resources.Where(resource => !resource.IsHidden)
            .Select(resource => (resource.Name, resource.Generation)).OrderBy(item => item.Name)
            .SequenceEqual(snapshot.Where(resource => !resource.IsHidden)
                .Select(resource => (resource.Name, resource.Generation)).OrderBy(item => item.Name));
        resources = Array.AsReadOnly(snapshot.ToArray());
        Fill(old?.Name);
        liveResources = true;
        resourceUpdateVersion++;
        Say($"Live · {Core.Plural.Of(resources.Count, "resource")}");
        AspireResource? selected = SelectedResource;
        if (old?.Name != selected?.Name || old?.Generation != selected?.Generation)
        {
            ResourceSelectionChanged?.Invoke(this, Selection);
            StreamsDue?.Invoke(this, EventArgs.Empty);
        }
        else if (consoleChanged)
        {
            StreamsDue?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<(IReadOnlyList<AspireResource> Resources, bool Live)> ReadResourcesAsync(string appHost, CancellationToken cancel)
    {
        Task<IReadOnlyList<AspireResource>>? request = await UiThread.InvokeAsync<Task<IReadOnlyList<AspireResource>>?>(() => ResourceProvider?.Invoke(appHost, cancel))
            .WaitAsync(cancel);
        return request is not null ? (await request.WaitAsync(cancel), true)
            : (await client.GetResourcesAsync(appHost, cancel), false);
    }

    private async Task WorkAsync(int version, CancellationToken cancel, Func<Task> work)
    {
        try
        {
            await BackgroundWork.Run(work, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (Exception error) when (Expected(error))
        {
            string message = SafeError(error);
            await ApplyAsync(version, () => Say(message));
        }
    }

    private Task<bool> ApplyAsync(int version, Action apply) => UiThread.InvokeAsync(() =>
    {
        if (!disposed && version == generation)
        {
            apply();
            return true;
        }
        return false;
    });

    private (int Version, CancellationToken Cancel) BeginRead()
    {
        UiThread.Check();
        ObjectDisposedException.ThrowIf(disposed, this);
        reading?.Cancel();
        reading?.Dispose();
        reading = new CancellationTokenSource();
        return (++generation, reading.Token);
    }

    private void ResetApp()
    {
        liveResources = false;
        reading?.Cancel();
        generation++;
        resources = [];
        collapsed.Clear();
        Fill(null);
        AppReset?.Invoke(this, EventArgs.Empty);
        Say(SelectedApp is null ? "No AppHost selected." : "AppHost selected. Refreshing…");
    }

    private void Say(string words)
    {
        Status = words;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The resources the AppHost lists (its hidden ones left out).</summary>
    internal AspireResource[] Visible() => [.. resources.Where(resource => !resource.IsHidden)];

    /// <summary>Each resource's parent among those listed, a loop or a missing parent's child listed as a root.</summary>
    private static Dictionary<string, string> Parents(IReadOnlyList<AspireResource> visible)
    {
        HashSet<string> names = visible.Select(resource => resource.Name).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> parents = new(StringComparer.Ordinal);
        foreach (AspireResource resource in visible)
        {
            string? parent = AspireJson.Array(resource.Data, "relationships")
                .Where(relation => string.Equals(AspireJson.String(relation, "type"), "Parent", StringComparison.OrdinalIgnoreCase))
                .Select(relation => AspireJson.String(relation, "resourceName")).FirstOrDefault();
            if (parent is not null && names.Contains(parent) && parent != resource.Name)
            {
                parents[resource.Name] = parent;
            }
        }
        foreach (string name in parents.Keys.ToArray())
        {
            HashSet<string> path = new(StringComparer.Ordinal) { name };
            for (string current = name; parents.TryGetValue(current, out string? parent); current = parent)
            {
                if (!path.Add(parent))
                {
                    parents.Remove(name);
                    break;
                }
            }
        }
        return parents;
    }

    /// <summary>The rows: each root, then its children under it while it is open, the one chosen kept (else the
    /// <paramref name="fallback"/>'s, else the first).</summary>
    private void Fill(string? kept, string? fallback = null)
    {
        AspireResource[] visible = Visible();
        Dictionary<string, string> parents = Parents(visible);
        ILookup<string, AspireResource> children = visible.Where(resource => parents.ContainsKey(resource.Name)).ToLookup(resource => parents[resource.Name], StringComparer.Ordinal);
        List<(string, AspireRow)> rows = [];
        foreach (AspireResource root in visible.Where(resource => !parents.ContainsKey(resource.Name)))
        {
            Add(root, 0);
        }
        filling = true;
        try
        {
            // A row kept keeps its control, so a click on it lands however often the AppHost's updates come.
            RunRows.Put(items, rows);
        }
        finally
        {
            filling = false;
        }
        chosen = items.FirstOrDefault(item => item.Key == kept) ?? items.FirstOrDefault(item => item.Key == fallback) ?? items.FirstOrDefault();
        RowsChanged?.Invoke(this, false);

        void Add(AspireResource resource, int depth)
        {
            bool expands = children[resource.Name].Any();
            bool open = expands && !collapsed.Contains(resource.Name);
            rows.Add((resource.Name, new AspireRow(resource, depth, expands, open)
            {
                LeftUnstarted = SelectedApp is { } app && LeftUnstarted?.Invoke(app.AppHostPath, resource) == true,
            }));
            if (open)
            {
                foreach (AspireResource child in children[resource.Name])
                {
                    Add(child, depth + 1);
                }
            }
        }
    }

    /// <summary>A resource's addresses a browser opens: its http and https URLs, not those inactive or the AppHost's own.</summary>
    internal static IReadOnlyList<Uri> Links(AspireResource resource) =>
    [
        .. AspireJson.Array(resource.Data, "urls")
            .Where(url => !(url.TryGetProperty("isInactive", out JsonElement inactive) && inactive.ValueKind == JsonValueKind.True))
            .Select(url => Uri.TryCreate(AspireJson.String(url, "url"), UriKind.Absolute, out Uri? target) && target.Scheme is "http" or "https" ? target : null)
            .OfType<Uri>(),
    ];

    public static string PageAction(string name) => name switch
    {
        LogTab => AppActions.AspireConsole, DetailsTab => AppActions.AspireResources,
        TelemetryTab => AppActions.AspireTelemetry, AppHostTab => AppActions.AspireAppHostConsole,
        _ => throw new ArgumentException("Unknown Aspire page.", nameof(name)),
    };

    /// <summary>The tabs' names, as their tabs and actions say them, in their order.</summary>
    public static IReadOnlyList<string> Pages { get; } = [.. RunWindows.Tabs.Where(tab => tab is LogTab or DetailsTab or TelemetryTab or AppHostTab)];

    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or InvalidOperationException or TimeoutException
        or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;

    private static string SafeError(Exception error) => error is InvalidDataException or TimeoutException or NotSupportedException
        || error is InvalidOperationException && error.Message.StartsWith("Aspire", StringComparison.Ordinal)
        ? error.Message : "Aspire data is unavailable. Check the AppHost, advertised command state, CLI installation, and certificate trust, then refresh.";

    /// <summary>Ends what it reads and runs: a session's model, made anew with the next.</summary>
    public void Dispose()
    {
        UiThread.Check();
        if (disposed)
        {
            return;
        }
        disposed = true;
        commandLifetime.Cancel();
        commandLifetime.Dispose();
        generation++;
        reading?.Cancel();
        reading?.Dispose();
        reading = null;
    }
}
