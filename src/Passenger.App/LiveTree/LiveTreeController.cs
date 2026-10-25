using System.ComponentModel;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.App.LiveTree;

/// <summary>What a window lends its Live Visual Tree: the tool windows' frame, the window's actions, the status bar, going to
/// a place in a file, and the editors, whose text the app's XAML places are read in.</summary>
public sealed record LiveTreeParts(DockHost Dock, ActionRegistry Actions, Action<string> Say, Action<string, TextPosition> Navigate, EditorArea Editors);

/// <summary>The window's Live Visual Tree, over the session it has: it follows the Avalonia or WPF app of the Debug tab chosen
/// (<see cref="ProjectSession.LiveApp"/>), shows its window as the app's UI comes up, and asks the app for its tree
/// (again whenever its controls come and go), for the chosen control's details, for Select Element and for the outline over
/// the control chosen. While the app is paused nothing is asked, since its UI thread answers nothing: the window keeps what
/// it last had and says so, and asks again once the app runs. A UI-thread object of the window.</summary>
public sealed class LiveTreeController
{
    private readonly LiveTreeParts parts;
    private readonly ToolWindow window;
    private ProjectSession? session;
    private LiveApp? app;
    private DebugController? followedDebugger;
    private long? chosen;

    /// <summary>Bumped by each ask for the tree, and for details, so an answer that lands after a later ask is dropped.</summary>
    private int treeAsks;
    private int detailAsks;

    /// <summary>The app's tree changed while the window could not ask (paused, hidden).</summary>
    private bool stale;

    /// <summary>The property breakpoints changed while an app was paused, which its inspector hears of once it runs.</summary>
    private bool watchesStale;
    private bool syncing;
    private bool syncAgain;

    /// <summary>The chosen element's properties with a breakpoint, as its rows last showed them.</summary>
    private string watchedShown = string.Empty;

    /// <summary>The window's view once it is first shown or asked for, and what it shows, kept until then.</summary>
    private LiveTreeView? view;
    private (string? Message, string? Line) status;
    private string? banner;
    private AppTree? tree;
    private (ElementDetails? Details, string? Problem) details;
    private bool picking;

    public LiveTreeController(LiveTreeParts parts)
    {
        this.parts = parts;
        window = new ToolWindow(ToolWindowIds.LiveTree, "Live Visual Tree", IconKeys.LiveTree, Make, ToolAnchor.RightTop) { Action = AppActions.ShowLiveTree };
        parts.Dock.Add(window);
        window.PropertyChanged += OnWindowChanged;
        parts.Actions.Add(new IdeAction(AppActions.SelectElement, "Select Element", "Debug", context => IdeErrors.Observe(PickAsync(!picking)))
        {
            WhyNot = _ => WhyNotAsk(),
        });
        ShowState();
    }

    /// <summary>The Live Visual Tree's view, built as it is asked for.</summary>
    public LiveTreeView View => view ?? (LiveTreeView)window.Content;

    /// <summary>The view, as it is first shown or asked for, wired and showing what the controller kept for it.</summary>
    private LiveTreeView Make()
    {
        LiveTreeView made = view = new LiveTreeView();
        made.ElementChosen += (_, id) => IdeErrors.Observe(ChooseAsync(id));
        made.PickRequested += (_, on) => IdeErrors.Observe(PickAsync(on));
        made.RefreshRequested += (_, _) => IdeErrors.Observe(RefreshAsync());
        made.GoToRequested += (_, place) => GoTo(place);
        made.BreakRequested += (_, value) => ToggleBreak(value);
        made.Watched = value => details.Details is { } shown && Debugged() is { } debugged && session!.Debug.Breakpoints.FindProperty(debugged, shown.Id, value.Name) is not null;
        made.WhyNotBreak = () => Debugged() is { State: not DebugState.Ended } ? null : "Break When Value Changes needs the app debugged";
        made.ShowTree(tree);
        made.ShowDetails(details.Details, details.Problem);
        if (chosen is long id && tree is not null)
        {
            made.Choose(id);
        }
        made.ShowStatus(status.Message, status.Line);
        made.ShowBanner(banner);
        made.SelectElement.IsChecked = picking;
        return made;
    }

    private void ShowStatus(string? message, string? line)
    {
        status = (message, line);
        view?.ShowStatus(message, line);
    }

    private void ShowBanner(string? text)
    {
        banner = text;
        view?.ShowBanner(text);
    }

    private void ShowTree(AppTree? shown)
    {
        tree = shown;
        view?.ShowTree(shown);
    }

    private void ShowDetails(ElementDetails? shown, string? problem)
    {
        details = (shown, problem);
        view?.ShowDetails(shown, problem);
    }

    /// <summary>Select Element shown pressed while the app waits for a control to be pointed at.</summary>
    private void Picking(bool on)
    {
        picking = on;
        if (view is not null)
        {
            view.SelectElement.IsChecked = on;
        }
    }

    /// <summary>The app the window follows, or null.</summary>
    public LiveApp? App => app;

    /// <summary>The control chosen in the tree (by the inspector's id for it), or null.</summary>
    public long? Chosen => chosen;

    /// <summary>Another control was chosen, or none is now.</summary>
    public event EventHandler? ChosenChanged;

    private void Choose(long? id)
    {
        if (chosen != id)
        {
            chosen = id;
            ChosenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The session debugging the app the window follows, which its property breakpoints are the elements of.</summary>
    private DebugSession? Debugged() => app is not null ? session?.Debug.SessionOf(app) : null;

    /// <summary>The window follows a session from now: the app of its chosen Debug tab, and whether that app is paused.</summary>
    public void Bind(ProjectSession next)
    {
        UiThread.Check();
        if (session is { } previous)
        {
            previous.LiveAppChanged -= OnSelectedAppChanged;
            previous.Debug.SelectionChanged -= OnSelectedAppChanged;
            previous.Debug.StateChanged -= OnSelectedDebugStateChanged;
        }
        session = next;
        next.LiveAppChanged += OnSelectedAppChanged;
        next.Debug.SelectionChanged += OnSelectedAppChanged;
        next.Debug.StateChanged += OnSelectedDebugStateChanged;
        OnSelectedAppChanged(null, EventArgs.Empty);
    }

    private void OnSelectedAppChanged(object? sender, EventArgs e)
    {
        UiThread.Check();
        if (followedDebugger != session?.Debug)
        {
            if (followedDebugger is not null)
            {
                followedDebugger.Breakpoints.PropertiesChanged -= OnPropertiesChanged;
            }
            followedDebugger = session?.Debug;
            if (followedDebugger is not null)
            {
                followedDebugger.Breakpoints.PropertiesChanged += OnPropertiesChanged;
            }
        }
        Follow(session?.LiveApp);
        OnDebugState();
        OnPropertiesChanged(null, EventArgs.Empty);
    }

    private void OnSelectedDebugStateChanged(object? sender, EventArgs e) => OnDebugState();

    private void OnPropertiesChanged(object? sender, EventArgs e)
    {
        UiThread.Check();
        ShowWatched();
        IdeErrors.Observe(SyncWatchesAsync());
    }

    /// <summary>Break When Value Changes on a property of the chosen element: a property breakpoint set, or the one there
    /// taken away.</summary>
    private void ToggleBreak(PropertyValue value)
    {
        if (session is not { } current || details.Details is not { } shown || Debugged() is not { } debugged)
        {
            return;
        }
        BreakpointStore store = current.Debug.Breakpoints;
        if (store.FindProperty(debugged, shown.Id, value.Name) is { } existing)
        {
            store.Remove(existing);
            return;
        }
        PropertyBreakpoint added = store.AddProperty(debugged, shown.Id, shown.Name is { Length: > 0 } name ? $"{shown.Type}#{name}" : shown.Type, value.Name);
        parts.Say($"Break When Value Changes: the app stops where {added.Location} is changed");
    }

    /// <summary>The chosen element's rows marked again when its property breakpoints changed.</summary>
    private void ShowWatched()
    {
        string now = WatchedNow();
        if (now != watchedShown)
        {
            watchedShown = now;
            view?.RefreshProperties();
        }
    }

    private string WatchedNow() => details.Details is { } shown && session is { } current && Debugged() is { } debugged
        ? string.Join(';', current.Debug.Breakpoints.Properties.Where(p => p.Session == debugged && p.Element == shown.Id).Select(p => p.Property).Order(StringComparer.Ordinal))
        : string.Empty;

    /// <summary>The inspectors in the apps told of the property breakpoints as they are now (while each runs): once it ends,
    /// the next input to an app finds the property watched.</summary>
    public Task Watching { get; private set; } = Task.CompletedTask;

    /// <summary>Tells the inspector in each debugged app which of its properties to watch, as the property breakpoints of its
    /// session that are on (none while muted) and their hit counts say; while an app is paused, once it runs. One at a time,
    /// the last word winning.</summary>
    private Task SyncWatchesAsync()
    {
        if (syncing)
        {
            syncAgain = true;
            return Watching;
        }
        Watching = SyncWatchesLoopAsync();
        return Watching;
    }

    private async Task SyncWatchesLoopAsync()
    {
        syncing = true;
        try
        {
            do
            {
                syncAgain = false;
                await SyncWatchesOnceAsync();
            }
            while (syncAgain);
        }
        finally
        {
            syncing = false;
        }
    }

    private async Task SyncWatchesOnceAsync()
    {
        if (session is not { } current)
        {
            return;
        }
        watchesStale = false;
        BreakpointStore store = current.Debug.Breakpoints;
        // Held, so what is done next in the app finds the property watched.
        using IDisposable held = BackgroundWork.Scope();
        foreach ((DebugTab tab, LiveApp live) in current.Debug.Apps.ToList())
        {
            if (live.State != LiveAppState.Connected || tab.Session is not { State: not DebugState.Ended } debugged)
            {
                continue;
            }
            if (debugged.State == DebugState.Paused)
            {
                watchesStale = true;
                continue;
            }
            await SyncWatchesAsync(current, store, debugged, live);
        }
    }

    /// <summary>One app's inspector told of its session's property breakpoints, as <see cref="LiveApp.Watched"/> last said them.</summary>
    private async Task SyncWatchesAsync(ProjectSession current, BreakpointStore store, DebugSession debugged, LiveApp live)
    {
        Dictionary<PropertyBreakpoint, string> wanted = store.Muted ? []
            : store.Properties.Where(p => p.Enabled && p.Session == debugged).ToDictionary(p => p, p => p.HitCondition ?? string.Empty);
        Dictionary<PropertyBreakpoint, string> watched = live.Watched;
        foreach (PropertyBreakpoint gone in watched.Keys.Where(p => !wanted.ContainsKey(p)).ToList())
        {
            if (session != current || current.IsEnded || current.Debug.SessionOf(live) != debugged)
            {
                return;
            }
            watched.Remove(gone);
            try
            {
                await live.UnwatchAsync(gone.Id);
            }
            catch (LiveAppException)
            {
                // An app that cannot answer watches nothing that matters: its debugger has the breakpoint no more.
            }
        }
        foreach ((PropertyBreakpoint breakpoint, string hits) in wanted)
        {
            if (session != current || current.IsEnded || current.Debug.SessionOf(live) != debugged)
            {
                return;
            }
            if (watched.TryGetValue(breakpoint, out string? was) && was == hits)
            {
                continue;
            }
            watched[breakpoint] = hits;
            try
            {
                await live.WatchAsync(breakpoint.Id, breakpoint.Element, breakpoint.Property, breakpoint.HitCondition);
            }
            catch (LiveAppException error)
            {
                if (session == current && !current.IsEnded && current.Debug.SessionOf(live) == debugged)
                {
                    parts.Say($"Break When Value Changes on {breakpoint.Location}: {error.Message}");
                }
            }
        }
    }

    private bool Paused => Debugged() is { State: DebugState.Paused };

    private void Follow(LiveApp? next)
    {
        if (app == next)
        {
            return;
        }
        if (app is not null)
        {
            app.StateChanged -= OnAppState;
            app.TreeChanged -= OnTreeChanged;
            app.Picked -= OnPicked;
        }
        app = next;
        Choose(null);
        treeAsks++;
        detailAsks++;
        Picking(false);
        ShowBanner(null);
        ShowTree(null);
        ShowDetails(null, null);
        if (next is not null)
        {
            next.StateChanged += OnAppState;
            next.TreeChanged += OnTreeChanged;
            next.Picked += OnPicked;
        }
        ShowState();
        if (next is { State: LiveAppState.Connected })
        {
            // An app up already (another Debug tab's, chosen again): its tree as it is now, or that it is paused.
            stale = true;
            OnDebugState();
        }
    }

    /// <summary>What the window says of the app: nothing to inspect, waiting for it, why not, gone, or what it runs.</summary>
    private void ShowState()
    {
        switch (app)
        {
            case null:
                ShowStatus("Debug an Avalonia or WPF app to see its windows and controls here.", null);
                break;
            case { State: LiveAppState.Waiting }:
                ShowStatus($"Waiting for {app.Name} to show its windows…", null);
                break;
            case { State: LiveAppState.Refused }:
                ShowStatus(app.Problem, null);
                break;
            case { State: LiveAppState.Ended }:
                ShowTree(null);
                ShowDetails(null, null);
                ShowBanner(null);
                Picking(false);
                ShowStatus($"{app.Name} has ended.", null);
                break;
            default:
                ShowStatus(null, $"{app.Name} · {app.Framework}");
                break;
        }
    }

    private void OnAppState(object? sender, EventArgs e)
    {
        if (sender != app)
        {
            return;
        }
        ShowState();
        if (app!.State == LiveAppState.Connected)
        {
            parts.Dock.Show(window.Id);
            IdeErrors.Observe(RefreshAsync());
            IdeErrors.Observe(SyncWatchesAsync());
        }
    }

    private void OnTreeChanged(object? sender, EventArgs e)
    {
        if (sender == app)
        {
            IdeErrors.Observe(RefreshAsync());
        }
    }

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolWindow.IsShown))
        {
            return;
        }
        if (window.IsShown && stale)
        {
            IdeErrors.Observe(RefreshAsync());
        }
        else if (!window.IsShown && WhyNotAsk() is null)
        {
            // Nothing outlined in the app while the window that chose it is away.
            IdeErrors.Observe(Unoutline(app!));
        }
    }

    private static async Task Unoutline(LiveApp live)
    {
        try
        {
            await live.HighlightAsync(null);
        }
        catch (LiveAppException)
        {
            // An app that cannot answer has no outline to take away that matters.
        }
    }

    private void OnDebugState()
    {
        bool paused = Paused && app is { State: LiveAppState.Connected };
        ShowBanner(paused ? $"{app!.Name} is paused: its tree and values are as it last ran, and are asked again once it runs." : null);
        if (!paused && stale)
        {
            IdeErrors.Observe(RefreshAsync());
        }
        // Any app's session may have run on since its watches changed, the one followed or another.
        if (watchesStale)
        {
            IdeErrors.Observe(SyncWatchesAsync());
        }
    }

    /// <summary>Why the window cannot ask the app now: nothing inspected, not up yet, refused, gone, or paused.</summary>
    private string? WhyNotAsk() => app switch
    {
        null => "No Avalonia or WPF app is being debugged",
        { State: LiveAppState.Waiting } => $"{app.Name} has not shown its windows yet",
        { State: LiveAppState.Refused } => app.Problem,
        { State: LiveAppState.Ended } => $"{app.Name} has ended",
        _ when Paused => $"{app.Name} is paused",
        _ => null,
    };

    /// <summary>Asks the app for its tree and shows it, the chosen control's details again with it; marked to ask again
    /// later while the app is paused or the window hidden.</summary>
    public async Task RefreshAsync()
    {
        UiThread.Check();
        if (app is not { State: LiveAppState.Connected } live)
        {
            return;
        }
        if (Paused || !window.IsShown)
        {
            stale = true;
            return;
        }
        stale = false;
        int ask = ++treeAsks;
        using IDisposable held = BackgroundWork.Scope();
        AppTree tree;
        try
        {
            tree = await live.TreeAsync();
        }
        catch (LiveAppException error)
        {
            if (ask == treeAsks && app == live)
            {
                // Asked again once the app runs, should it have paused while asked.
                stale = true;
                ShowStatus(null, error.Message);
            }
            return;
        }
        if (ask != treeAsks || app != live)
        {
            return;
        }
        ShowTree(tree);
        ShowState();
        if (chosen is long id)
        {
            if (View.Choose(id))
            {
                await ShowDetailsAsync(id);
            }
            else
            {
                Choose(null);
                ShowDetails(null, "The control chosen has left the app.");
            }
        }
    }

    /// <summary>A control chosen in the tree: its details shown, and it outlined in the app.</summary>
    public async Task ChooseAsync(long id)
    {
        UiThread.Check();
        Choose(id);
        await ShowDetailsAsync(id);
        if (app is { State: LiveAppState.Connected } live && !Paused)
        {
            try
            {
                await live.HighlightAsync(id);
            }
            catch (LiveAppException)
            {
                // The outline is a courtesy: the details say what went wrong, if anything did.
            }
        }
    }

    private async Task ShowDetailsAsync(long id)
    {
        if (app is not { State: LiveAppState.Connected } live)
        {
            return;
        }
        if (Paused)
        {
            if (details.Details?.Id != id)
            {
                ShowDetails(null, $"{live.Name} is paused: a control's properties show once it runs again.");
            }
            return;
        }
        int ask = ++detailAsks;
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            ElementDetails details = await live.DetailsAsync(id);
            if (ask == detailAsks && app == live)
            {
                ShowDetails(AppPlaces.InEditor(details, parts.Editors), null);
                watchedShown = WatchedNow();
            }
        }
        catch (LiveAppException error)
        {
            if (ask == detailAsks && app == live)
            {
                ShowDetails(null, error.Message);
            }
        }
    }

    /// <summary>Select Element on or off in the app; the status bar says what to do, or why it cannot.</summary>
    public async Task PickAsync(bool on)
    {
        UiThread.Check();
        Picking(on);
        if (WhyNotAsk() is { } why)
        {
            Picking(false);
            parts.Say($"Select Element: {why}");
            return;
        }
        LiveApp live = app!;
        try
        {
            await live.PickAsync(on);
        }
        catch (LiveAppException error)
        {
            Picking(false);
            parts.Say($"Select Element: {error.Message}");
            return;
        }
        if (on)
        {
            parts.Say($"Select Element: point at a control in {live.Name} and click it, or press Escape there");
        }
    }

    /// <summary>Chooses a control by the inspector's id for it, when the app the window follows is that process: the tree,
    /// asked again, chooses it. False for any other process.</summary>
    public bool Reveal(int processId, long id)
    {
        UiThread.Check();
        if (app is not { State: LiveAppState.Connected, ProcessId: int running } || running != processId)
        {
            return false;
        }
        Choose(id);
        parts.Dock.Show(window.Id);
        IdeErrors.Observe(RefreshAsync());
        return true;
    }

    /// <summary>The person pointed at a control in the app (or pressed Escape there): the tree, asked again, chooses it.</summary>
    private void OnPicked(object? sender, long? id)
    {
        if (sender != app)
        {
            return;
        }
        Picking(false);
        if (id is long element)
        {
            Choose(element);
            parts.Dock.Show(window.Id);
            IdeErrors.Observe(RefreshAsync());
        }
    }

    /// <summary>Opens the XAML a link names: a control's element, a style's selector or a setter, at its line (the details
    /// shown have the editor's, <see cref="AppPlaces"/>); a root recorded by its file alone at its root element.</summary>
    private void GoTo(SourcePlace place)
    {
        string path = PathNames.Canonical(place.File);
        if (!File.Exists(path))
        {
            parts.Say($"{place.File} is not on this machine");
            return;
        }
        TextPosition at = place.Line > 0 ? new TextPosition(place.Line - 1, Math.Max(0, place.Column - 1)) : RootElement(CSharp.ServerPlaces.TextOf(parts.Editors, path));
        parts.Navigate(path, at);
    }

    /// <summary>Where a XAML file's root element starts in the editor's lines: its first tag that is no declaration or comment.</summary>
    private static TextPosition RootElement(TextSnapshot? text)
    {
        for (int line = 0; text is not null && line < text.LineCount; line++)
        {
            LineSpan span = text.GetLine(line);
            string shown = text.GetText(span.Start, span.Length);
            int tag = shown.IndexOf('<', StringComparison.Ordinal);
            if (tag >= 0 && !shown.AsSpan(tag).StartsWith("<?") && !shown.AsSpan(tag).StartsWith("<!"))
            {
                return new TextPosition(line, tag + 1);
            }
        }
        return new TextPosition(0, 0);
    }
}
