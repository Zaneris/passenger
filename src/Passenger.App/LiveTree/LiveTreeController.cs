using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.App.LiveTree;

/// <summary>What a window lends its Live Visual Tree: the tool windows' frame, the window's actions, the status bar, going to
/// a place in a file, the editors, whose text the app's XAML places are read in, a change of files as one command of Undo
/// (a Godot value kept; null once made, else why not), and a popup beside a control of the window's.</summary>
public sealed record LiveTreeParts(DockHost Dock, ActionRegistry Actions, Action<string> Say, Action<string, TextPosition> Navigate, EditorArea Editors,
    Func<string, IReadOnlyList<Passenger.Xaml.Features.XamlFileChange>, Task<string?>> Change, Action<Avalonia.Controls.Control, Avalonia.Controls.Control> ShowPopup,
    Action ClosePopup);

/// <summary>The window's Live Visual Tree, over the session it has: it follows the Avalonia or WPF app of the Debug tab chosen,
/// else the Godot game launched last (<see cref="ProjectSession.LiveSource"/>), shows its window as the app's UI comes up,
/// a game's monitors in its status line, and asks the app for its tree
/// (again whenever its controls come and go), for the chosen control's details, for Select Element and for the outline over
/// the control chosen. While the app is paused nothing is asked, since its UI thread answers nothing: the window keeps what
/// it last had and says so, and asks again once the app runs. A UI-thread object of the window.</summary>
public sealed partial class LiveTreeController
{
    private readonly LiveTreeParts parts;
    private readonly ToolWindow window;
    private ProjectSession? session;
    private ILiveTreeSource? app;
    private DebugController? followedDebugger;
    private long? chosen;

    /// <summary>Bumped by each ask for the tree, and for details, so an answer that lands after a later ask is dropped.</summary>
    private int treeAsks;
    private int detailAsks;

    /// <summary>Bumped by each tree the game's period asks for, apart from <see cref="treeAsks"/>, which it never overtakes.</summary>
    private int followAsks;

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

    /// <summary>The splits the person dragged, for each kind of source, and where a drag is kept once the window keeps its
    /// state (<see cref="KeepSplits"/>).</summary>
    private LiveTreeState splits = new();
    private Action<LiveTreeState>? keepSplits;

    public LiveTreeController(LiveTreeParts parts)
    {
        this.parts = parts;
        window = new ToolWindow(ToolWindowIds.LiveTree, "Live Visual Tree", IconKeys.LiveTree, Make, ToolAnchor.RightTop) { Action = AppActions.ShowLiveTree };
        parts.Dock.Add(window);
        window.PropertyChanged += OnWindowChanged;
        parts.Actions.Add(new IdeAction(AppActions.SelectElement, "Select Element", "Debug", context => IdeErrors.Observe(PickAsync(!picking)))
        {
            WhyNot = _ => WhyNotAsk() ?? (app as Godot.GodotGame)?.EditorHolds,
        });
        parts.Actions.Add(new IdeAction(AppActions.LiveTreeRefresh, "Refresh Live Visual Tree", "Debug", context => IdeErrors.Observe(RefreshAsync()))
        {
            WhyNot = _ => WhyNotAsk(),
        });
        parts.Actions.Add(new IdeAction(AppActions.SelectIn3D, "Select Element Among 3D Nodes", "Debug", context => IdeErrors.Observe(PickInAsync(!pickIn3D)))
        {
            IsChecked = _ => pickIn3D,
            WhyNot = _ => app is Godot.GodotGame game ? game.EditorHolds : "Select Element picks among 3D nodes in a Godot game: Run or Debug one",
        });
        parts.Actions.Add(new IdeAction(AppActions.PauseGameLoop, "Pause Game Loop", "Debug", context => IdeErrors.Observe(HoldLoopAsync()))
        {
            TitleIn = _ => app is Godot.GodotGame { LoopHeld: true } ? "Resume Game Loop" : "Pause Game Loop",
            IsChecked = _ => app is Godot.GodotGame { LoopHeld: true },
            WhyNot = _ => WhyNotLoop(),
            Words = ["Suspend Game", "Resume Game"],
        });
        parts.Actions.Add(new IdeAction(AppActions.NextFrame, "Next Frame", "Debug", context => IdeErrors.Observe(StepAsync()))
        {
            WhyNot = _ => WhyNotLoop() ?? (app is Godot.GodotGame { LoopHeld: false } game ? $"{game.Name}'s loop runs: Pause Game Loop holds it first" : null),
            Words = ["Step Frame"],
        });
        ShowState();
    }

    /// <summary>The splits kept for each kind of source (the window's state), a drag of the person's kept from now on by
    /// <paramref name="keep"/>.</summary>
    public void KeepSplits(LiveTreeState kept, Action<LiveTreeState> keep)
    {
        UiThread.Check();
        splits = kept;
        keepSplits = keep;
        if (view is not null)
        {
            view.DraggedSplit = splits.SplitOf(app);
        }
    }

    /// <summary>A split the person dragged, kept for the kind of source shown; for none, it stands only until one is.</summary>
    private void KeepSplit(double share)
    {
        if (app is null)
        {
            return;
        }
        splits = splits.WithSplit(app, share);
        keepSplits?.Invoke(splits);
    }

    /// <summary>The Live Visual Tree's view, built as it is asked for.</summary>
    public LiveTreeView View => view ?? (LiveTreeView)window.Content;

    /// <summary>The view, as it is first shown or asked for, wired and showing what the controller kept for it.</summary>
    private LiveTreeView Make()
    {
        LiveTreeView made = view = new LiveTreeView();
        made.ElementChosen += (_, id) => IdeErrors.Observe(ChooseAsync(id));
        made.PickRequested += (_, on) => IdeErrors.Observe(PickAsync(on));
        made.RefreshRequested += (_, _) =>
        {
            if (parts.Actions.Run(AppActions.LiveTreeRefresh) is { } why)
            {
                parts.Say(why);
            }
        };
        made.GoToRequested += (_, place) => GoTo(place);
        made.BreakRequested += (_, value) => ToggleBreak(value);
        made.Watched = value => details.Details is { } shown && Debugged() is { } debugged && session!.Debug.Breakpoints.FindProperty(debugged, shown.Id, value.Name) is not null;
        made.WhyNotBreak = () => app is not LiveApp ? "Break When Value Changes watches an Avalonia or WPF app's properties"
            : Debugged() is { State: not DebugState.Ended } ? null : "Break When Value Changes needs the app debugged";
        made.WhyNotEdit = value => app is ILiveTreeValues values && details.Details is { } shown ? values.WhyNotSet(shown.Id, value) : "Only a Godot game's properties are set here";
        made.ValueSet += (_, set) => IdeErrors.Observe(SetAsync(set.Value, set.Text));
        made.NotEdited += (_, why) => parts.Say(why);
        made.PropertiesAlone = app is Godot.GodotGame;
        made.Cells = app is Godot.GodotGame tuned ? new Godot.GodotValueCells(tuned, this) : null;
        made.DraggedSplit = splits.SplitOf(app);
        made.SplitDragged += (_, share) => KeepSplit(share);
        made.ShowAside(unkept);
        made.ShowTree(tree);
        made.ShowDetails(details.Details, details.Problem);
        if (chosen is long id && tree is not null)
        {
            made.Choose(id);
        }
        made.ShowStatus(status.Message, status.Line);
        made.ShowBanner(banner);
        made.ShowMonitors(monitorsShown);
        made.ShowCharts(charting && app is Godot.GodotGame ? monitorCharts : null);
        made.ChartsRequested += (_, _) =>
        {
            charting = !charting;
            FollowCharts();
        };
        made.SelectElement.IsChecked = picking;
        made.ShowGame(app is Godot.GodotGame, app is Godot.GodotGame { LoopHeld: true });
        made.ShowPickIn(pickIn3D);
        made.PickInChosen += (_, in3D) => IdeErrors.Observe(PickInAsync(in3D));
        made.LoopRequested += (_, action) =>
        {
            if (parts.Actions.Run(action) is { } why)
            {
                parts.Say(why);
            }
        };
        made.SourceChosen += (_, at) =>
        {
            if (session is { LiveApp: null } current && at < current.Games.Count)
            {
                current.ChooseGame(current.Games[at]);
            }
        };
        ShowSources();
        return made;
    }

    private void ShowStatus(string? message, string? line)
    {
        status = (message, line);
        view?.ShowStatus(message, line);
    }

    /// <summary>The game's monitors as last shown, or null.</summary>
    private string? monitorsShown;

    private void ShowMonitors(string? text)
    {
        monitorsShown = text;
        view?.ShowMonitors(text);
    }

    private void ShowBanner(string? text)
    {
        banner = text;
        view?.ShowBanner(text);
    }

    private void ShowTree(AppTree? shown)
    {
        tree = shown;
        if (shown is not null && app is Godot.GodotGame && !pickChosen)
        {
            ShowPickIn(Has3D(shown));
        }
        view?.ShowTree(shown);
    }

    private void ShowDetails(ElementDetails? shown, string? problem)
    {
        details = (shown, problem);
        view?.ShowDetails(shown, problem);
        view?.ShowFailures(FailuresOf(shown));
    }

    /// <summary>A binding failed in the app: the control chosen shows it among its own once it is that control's.</summary>
    private void OnBindingFailed(object? sender, BindingFailedEventArgs e)
    {
        if (sender == app && details.Details is { } shown)
        {
            view?.ShowFailures(FailuresOf(shown));
        }
    }

    /// <summary>The binding failures of a control's own XAML element, as the app last told them: those failed where its
    /// element is written (the editor's line, as its details have it), each saying what it binds, as the Debug console
    /// says a failure it stops on (<see cref="BindingErrors.Stopped"/>); then what the last XAML Hot Reload said in its
    /// start tag's lines.</summary>
    private IReadOnlyList<string> FailuresOf(ElementDetails? shown)
    {
        if (shown?.Source is not { Line: > 0 } source || app is not LiveApp live)
        {
            return [];
        }
        Func<string, TextSnapshot?> texts = CSharp.ServerPlaces.Texts(parts.Editors);
        List<string> failed = [.. live.Bindings.Where(failure => failure.Place is { } place && Path.GetFileName(place.File) == Path.GetFileName(source.File)
            && AppPlaces.InEditor(place, texts) is { } at && at.Line == source.Line && PathNames.Same(at.File, source.File)).Select(BindingErrors.Stopped)];
        if (reloadProblems.Count > 0 && TagLines(source, texts) is (int first, int last))
        {
            failed.AddRange(reloadProblems.Where(p => PathNames.Same(p.Path!, source.File) && p.Position.Line >= first && p.Position.Line <= last)
                .Select(p => $"XAML Hot Reload {(p.Severity == Run.ProblemSeverity.Error ? "error" : "warning")}: {p.Message}"));
        }
        return failed;
    }

    /// <summary>What the last XAML Hot Reload said of the app's XAML, each problem at the editor's line.</summary>
    private IReadOnlyList<Run.Problem> reloadProblems = [];

    /// <summary>What the last Hot Reload came to, which the chosen control shows among its own failures where a problem of
    /// its XAML is in its start tag.</summary>
    public void ShowHotReload(IReadOnlyList<Run.Problem> problems)
    {
        UiThread.Check();
        reloadProblems = [.. problems.Where(p => p.Path is { } path && CSharp.ServerPlaces.CountedAsXml(path))];
        view?.ShowFailures(FailuresOf(details.Details));
    }

    /// <summary>The lines (from zero) a control's element's start tag spans in its file's text as the editor has it.</summary>
    private static (int First, int Last)? TagLines(SourcePlace source, Func<string, TextSnapshot?> texts)
    {
        if (texts(PathNames.Canonical(source.File)) is not { } text || source.Line > text.LineCount)
        {
            return null;
        }
        int offset = Math.Min(text.GetLineStart(source.Line - 1) + Math.Max(0, source.Column - 1), text.Length);
        Passenger.Xaml.Syntax.XmlTree tree = Passenger.Xaml.Syntax.XmlTree.Parse(text.ToString());
        return Passenger.Xaml.XamlFile.ObjectAt(tree, offset) is { } element
            ? (text.GetLineFromOffset(element.Start), text.GetLineFromOffset(Math.Max(element.Start, element.StartTagEnd - 1)))
            : null;
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

    /// <summary>The app or game the window follows, or null.</summary>
    public ILiveTreeSource? App => app;

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
    private DebugSession? Debugged() => app switch
    {
        LiveApp live => session?.Debug.SessionOf(live),
        Godot.GodotGame game => game.Debugged,
        _ => null,
    };

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
        Follow(session?.LiveSource);
        ShowSources();
        OnDebugState();
        OnPropertiesChanged(null, EventArgs.Empty);
    }

    /// <summary>The running games the chooser offers, while no Avalonia or WPF app is followed: each by its configuration's
    /// name, with its process where two share one.</summary>
    private void ShowSources()
    {
        if (view is null)
        {
            return;
        }
        IReadOnlyList<Godot.GodotGame> games = session is { LiveApp: null } current ? current.Games : [];
        string[] names = [.. games.Select(g => games.Count(other => other.Name == g.Name) > 1 && g.ProcessId is int pid ? $"{g.Name} ({pid})" : g.Name)];
        view.ShowSources(names, app is Godot.GodotGame game ? games.ToList().IndexOf(game) : -1);
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

    private void Follow(ILiveTreeSource? next)
    {
        if (app == next)
        {
            return;
        }
        if (app is not null)
        {
            app.StateChanged -= OnAppState;
            app.TreeChanged -= OnTreeChanged;
        }
        if (app is LiveApp left)
        {
            left.Picked -= OnPicked;
            left.BindingFailed -= OnBindingFailed;
        }
        if (app is Godot.GodotGame was)
        {
            was.MonitorsChanged -= OnMonitors;
            was.Picked -= OnGamePicked;
            was.LoopChanged -= OnLoopChanged;
            was.Tuning.Changed -= OnTuning;
            OfferUnkept(was);
        }
        app = next;
        pickChosen = false;
        if (view is not null)
        {
            view.PropertiesAlone = next is Godot.GodotGame;
            view.ShowGame(next is Godot.GodotGame, next is Godot.GodotGame { LoopHeld: true });
            view.Cells = next is Godot.GodotGame tuned ? new Godot.GodotValueCells(tuned, this) : null;
            view.DraggedSplit = splits.SplitOf(next);
        }
        ShowMonitors(MonitorsOf(next as Godot.GodotGame));
        FollowCharts();
        Choose(null);
        treeAsks++;
        detailAsks++;
        Picking(false);
        ShowBanner(null);
        ShowTree(null);
        ShowDetails(null, NoneChosen);
        if (next is not null)
        {
            next.StateChanged += OnAppState;
            next.TreeChanged += OnTreeChanged;
        }
        if (next is LiveApp live)
        {
            live.Picked += OnPicked;
            live.BindingFailed += OnBindingFailed;
        }
        if (next is Godot.GodotGame game)
        {
            game.MonitorsChanged += OnMonitors;
            game.Picked += OnGamePicked;
            game.LoopChanged += OnLoopChanged;
            game.Tuning.Changed += OnTuning;
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
                ShowStatus(NothingShown, null);
                break;
            case { State: LiveAppState.Waiting }:
                ShowStatus($"Waiting for {app.Name} to show its windows…", null);
                break;
            case { State: LiveAppState.Refused }:
                ShowStatus(app.Problem, null);
                break;
            case { State: LiveAppState.Ended }:
                ShowTree(null);
                ShowDetails(null, NoneChosen);
                ShowBanner(null);
                ShowMonitors(null);
                Picking(false);
                ShowStatus($"{app.Name} has ended.", null);
                break;
            default:
                ShowStatus(null, $"{app.Name} · {app.Framework}");
                break;
        }
    }

    /// <summary>What the details say while no element is chosen: a game's are its nodes, chosen in the tree alone.</summary>
    private string? NoneChosen => app is Godot.GodotGame ? "Choose a node in the tree." : null;

    /// <summary>What the window says while it follows nothing.</summary>
    public const string NothingShown = "Debug an Avalonia or WPF app to see its windows and controls here, or run a Godot game to see its nodes.";

    /// <summary>Whether the person asked for the game's monitors charted (<see cref="LiveTreeView.ChartsLink"/>).</summary>
    private bool charting;

    /// <summary>The monitors' charts, made as they are first asked for.</summary>
    private Telemetry.MetricsPane? monitorCharts;

    /// <summary>The game's monitors charted while the person asked for them and the window shows them, its connected game's
    /// fed each second its monitors come; else let go, nothing kept.</summary>
    private void FollowCharts()
    {
        if (charting && window.IsShown && app is Godot.GodotGame { State: LiveAppState.Connected } game)
        {
            if (monitorCharts?.Feeding != game.Name)
            {
                IdeErrors.Observe(game.NameMonitorsAsync());
            }
            monitorCharts ??= new Telemetry.MetricsPane();
            monitorCharts.ShowFed(game.Name, $"Waiting for {game.Name}'s monitors, which it sends every second");
        }
        else
        {
            monitorCharts?.Stop();
        }
        view?.ShowCharts(charting && app is Godot.GodotGame ? monitorCharts : null);
    }

    /// <summary>A game's monitors came, once a second: its status line says them, its charts take them while shown, and
    /// while the window is shown its tree is asked again, so a node the game renames, moves, adds or frees shows without
    /// Refresh; nothing is asked while it is hidden or the game paused.</summary>
    private void OnMonitors(object? sender, EventArgs e)
    {
        if (sender == app && app is Godot.GodotGame { State: LiveAppState.Connected } game)
        {
            ShowMonitors(MonitorsOf(game));
            if (charting && window.IsShown && game.Monitors is { } monitors)
            {
                FollowCharts();
                monitorCharts?.Feed(monitors.Readings());
            }
            ShowSources();
            if (window.IsShown && !Paused && !following)
            {
                IdeErrors.Observe(FollowAsync(game));
            }
            if (window.IsShown && !Paused && !counting && game is { Frame: not null, LoopHeld: false })
            {
                IdeErrors.Observe(CountAsync(game));
            }
        }
    }

    /// <summary>A count of the game's frames asked for by its period is under way.</summary>
    private bool counting;

    /// <summary>The game's frames counted again by its period, once Passenger's helper counts them: the line under the buttons
    /// follows the loop as it runs.</summary>
    private async Task CountAsync(Godot.GodotGame game)
    {
        counting = true;
        try
        {
            await game.TryCountAsync();
        }
        finally
        {
            counting = false;
        }
    }

    /// <summary>A game's line under the buttons: its frames run once counted (held at one while its loop is paused), then its
    /// monitors; null for none.</summary>
    private static string? MonitorsOf(Godot.GodotGame? game)
    {
        if (game is null || (game.Monitors is null && game.Frame is null))
        {
            return null;
        }
        string? frame = game.Frame is long count ? $"{(game.LoopHeld ? "Paused at frame" : "Frame")} {Counted(count)}" : game.LoopHeld ? "Paused" : null;
        return string.Join(" · ", new[] { frame, game.Monitors?.Said }.OfType<string>());
    }

    /// <summary>A count of frames as the window writes one: 1,234.</summary>
    private static string Counted(long frames) => frames.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The game's loop was held or let go, or its frames counted: its tools and line show it.</summary>
    private void OnLoopChanged(object? sender, EventArgs e)
    {
        if (sender == app && app is Godot.GodotGame game)
        {
            view?.ShowGame(true, game.LoopHeld);
            ShowMonitors(MonitorsOf(game));
        }
    }

    /// <summary>Why the game's loop cannot be paused or stepped now: no game followed, not up, gone, paused in the debugger, or
    /// held by the Godot editor that plays it.</summary>
    private string? WhyNotLoop() => app switch
    {
        Godot.GodotGame { EditorHolds: { } held } => held,
        Godot.GodotGame { State: LiveAppState.Waiting } game => $"{game.Name} has not connected to Passenger yet",
        Godot.GodotGame { State: LiveAppState.Ended } game => $"{game.Name} has ended",
        Godot.GodotGame game when Paused => $"{game.Name} is paused in the debugger, which holds its loop already",
        Godot.GodotGame => null,
        _ => "No Godot game is running: Run or Debug one to pause its loop",
    };

    /// <summary>Pause Game Loop: the game's loop held at its frame, or let go on where it is held; the status bar (and an agent's
    /// ide_action) says the frame it is at.</summary>
    private async Task HoldLoopAsync()
    {
        if (app is not Godot.GodotGame game || WhyNotLoop() is not null)
        {
            return;
        }
        bool hold = !game.LoopHeld;
        using Working? work = parts.Actions.Work.Begin(AppActions.PauseGameLoop, hold ? $"Pausing {game.Name}'s loop…" : $"Resuming {game.Name}'s loop…", stoppable: false);
        long? frame;
        try
        {
            frame = await game.HoldLoopAsync(hold);
        }
        catch (LiveAppException failed)
        {
            work?.Tell(false, $"Pause Game Loop: {failed.Message}");
            parts.Say($"Pause Game Loop: {failed.Message}");
            return;
        }
        string at = frame is long count ? $" at frame {Counted(count)}" : string.Empty;
        string said = hold ? $"{game.Name}'s loop is paused{at}: Next Frame runs one frame, and Resume Game Loop lets it go on"
            : $"{game.Name}'s loop runs on{(frame is long from ? $" from frame {Counted(from)}" : string.Empty)}";
        work?.Tell(true, said);
        parts.Say(said);
    }

    /// <summary>Next Frame: one frame of the game's loop run, the loop held again after it; the status bar (and an agent's
    /// ide_action) says the frame it is at then.</summary>
    private async Task StepAsync()
    {
        if (app is not Godot.GodotGame { LoopHeld: true } game || WhyNotLoop() is not null)
        {
            return;
        }
        using Working? work = parts.Actions.Work.Begin(AppActions.NextFrame, $"Running one frame of {game.Name}…", stoppable: false);
        try
        {
            long frame = await game.StepAsync();
            string said = $"{game.Name} ran one frame, and is paused at frame {Counted(frame)}";
            work?.Tell(true, said);
            parts.Say(said);
        }
        catch (LiveAppException failed)
        {
            work?.Tell(false, $"Next Frame: {failed.Message}");
            parts.Say($"Next Frame: {failed.Message}");
        }
    }

    /// <summary>A tree asked for by the game's period is under way.</summary>
    private bool following;

    /// <summary>The game's tree asked again by its period: shown only where it differs from the one shown, the node chosen
    /// kept chosen, its details left as they are (a value being typed among them), unless it left the game.</summary>
    private async Task FollowAsync(Godot.GodotGame game)
    {
        following = true;
        // A follow never overtakes a refresh (a pick's, the person's), which shows the chosen node's details with its tree.
        int ask = ++followAsks;
        int refreshed = treeAsks;
        AppTree next;
        try
        {
            next = await game.TreeAsync();
        }
        catch (LiveAppException)
        {
            // The period asks again in a second; Refresh says why.
            return;
        }
        finally
        {
            following = false;
        }
        if (ask != followAsks || refreshed != treeAsks || app != game || (tree is not null && Same(tree.Roots, next.Roots)))
        {
            return;
        }
        ShowTree(next);
        if (chosen is long id && !View.Choose(id))
        {
            Choose(null);
            ShowDetails(null, "The node chosen has left the game.");
        }
    }

    /// <summary>Whether two trees name the same elements, by id, type, name and whether each shows, in the same places.</summary>
    private static bool Same(IReadOnlyList<Element> shown, IReadOnlyList<Element> next)
    {
        if (shown.Count != next.Count)
        {
            return false;
        }
        for (int i = 0; i < shown.Count; i++)
        {
            Element a = shown[i];
            Element b = next[i];
            if (a.Id != b.Id || a.Type != b.Type || a.Name != b.Name || a.Visible != b.Visible || !Same(a.Children, b.Children))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A property of the chosen element set to what the person typed, its details asked again to show what the
    /// app made of it; the status bar says what came of it.</summary>
    private async Task SetAsync(PropertyValue value, string text)
    {
        if (app is not ILiveTreeValues values || details.Details is not { } shown)
        {
            return;
        }
        string element = shown.Name is { Length: > 0 } name ? $"{shown.Type} {name}" : shown.Type;
        try
        {
            await values.SetAsync(shown.Id, value, text);
        }
        catch (LiveAppException error)
        {
            parts.Say($"{element}'s {value.Name} was not set: {error.Message}");
            return;
        }
        parts.Say($"{element}'s {value.Name} set to {text}");
        await ShowDetailsAsync(shown.Id);
    }

    private void OnAppState(object? sender, EventArgs e)
    {
        if (sender != app)
        {
            return;
        }
        ShowState();
        if (app is Godot.GodotGame { State: LiveAppState.Ended } ended)
        {
            OfferUnkept(ended);
        }
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
        FollowCharts();
        if (window.IsShown && stale)
        {
            IdeErrors.Observe(RefreshAsync());
        }
        else if (!window.IsShown && WhyNotAsk() is null && app is LiveApp live)
        {
            // Nothing outlined in the app while the window that chose it is away.
            IdeErrors.Observe(Unoutline(live));
        }
        else if (!window.IsShown && WhyNotAsk() is null && app is Godot.GodotGame game && outlined)
        {
            outlined = false;
            IdeErrors.Observe(UnoutlineAsync(game));
        }
    }

    /// <summary>Whether a node of the game followed is outlined in its window, Godot drawing the outline.</summary>
    private bool outlined;

    private async Task UnoutlineAsync(Godot.GodotGame game)
    {
        try
        {
            await game.OutlineAsync(null, Outline());
        }
        catch (LiveAppException)
        {
            // A game that cannot be told has no outline to take away that matters.
        }
    }

    /// <summary>The colour a game draws its outline of the node chosen in: the window's accent, red, green and blue from 0 to 1.</summary>
    private (double Red, double Green, double Blue) Outline() =>
        parts.Dock.TryFindResource(TokenKeys.Accent, parts.Dock.ActualThemeVariant, out object? found) && found is ISolidColorBrush { Color: var accent }
            ? (accent.R / 255.0, accent.G / 255.0, accent.B / 255.0)
            : throw new InvalidOperationException("The theme gives no accent");

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
        null => "No Avalonia or WPF app is being debugged, nor a Godot game running",
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

    /// <summary>A control the person chose in the tree, or picked in the app with Select Element, has its details shown;
    /// raised on the UI thread.</summary>
    public event EventHandler<ElementDetails>? ElementShown;

    /// <summary>A control chosen in the tree: its details shown, and it outlined in the app.</summary>
    public Task ChooseAsync(long id) => ChooseAsync(id, byPerson: true);

    /// <summary>The control an Avalonia or WPF app made at a place of a XAML file (where its element's name starts, as the
    /// app's build counted, from one) chosen in the tree, its details shown and it outlined in the app, as a person's choice
    /// of its row would, but told to no one; of several made there, the one nearest the control chosen. False when the app
    /// made nothing there, or cannot be asked now.</summary>
    public async Task<bool> ChooseMadeAsync(string file, XamlSpot spot)
    {
        UiThread.Check();
        if (app is not LiveApp { State: LiveAppState.Connected } live || Paused)
        {
            return false;
        }
        XamlValues made;
        try
        {
            made = await live.MadeAsync(new XamlRead(file, [spot], chosen));
        }
        catch (LiveAppException)
        {
            return false;
        }
        if (app != live || made.Elements is not [{ Id: long id }, ..])
        {
            return false;
        }
        if (id != chosen)
        {
            if (view is not null && tree is not null)
            {
                view.Choose(id);
            }
            await ChooseAsync(id, byPerson: false);
        }
        return true;
    }

    private async Task ChooseAsync(long id, bool byPerson)
    {
        UiThread.Check();
        Choose(id);
        await ShowDetailsAsync(id);
        if (byPerson && chosen == id && details.Details is { } shown && shown.Id == id)
        {
            ElementShown?.Invoke(this, shown);
        }
        if (app is LiveApp { State: LiveAppState.Connected } live && !Paused)
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
        else if (app is Godot.GodotGame { State: LiveAppState.Connected, Headless: false } game && !Paused && window.IsShown)
        {
            try
            {
                outlined = true;
                await game.OutlineAsync((ulong)id, Outline());
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
                ShowKeepAll();
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
        if (app is Godot.GodotGame game)
        {
            await PickInGameAsync(game, on);
            return;
        }
        if (app is not LiveApp live)
        {
            Picking(false);
            return;
        }
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

    /// <summary>Whether Select Element picks the game's 3D nodes: as chosen, else as the game's tree has them.</summary>
    private bool pickIn3D;

    /// <summary>Whether <see cref="pickIn3D"/> was chosen for the game followed, which its tree then no longer chooses.</summary>
    private bool pickChosen;

    /// <summary>Whether a game's tree holds a node of Godot's 3D kinds (a Node3D, a Camera3D), which Select Element then picks
    /// among at first.</summary>
    private static bool Has3D(AppTree shown) => AppQuery.Flatten(shown.Roots).Any(n => n.Element.Type.EndsWith("3D", StringComparison.Ordinal));

    private void ShowPickIn(bool in3D)
    {
        pickIn3D = in3D;
        view?.ShowPickIn(in3D);
    }

    /// <summary>Select Element made to pick among the game's 3D nodes, or its 2D ones; told to the game at once where it picks.</summary>
    private async Task PickInAsync(bool in3D)
    {
        pickChosen = true;
        ShowPickIn(in3D);
        if (picking && app is Godot.GodotGame { State: LiveAppState.Connected } game)
        {
            await PickInGameAsync(game, on: true);
        }
    }

    /// <summary>Select Element on or off in a game: a click in its window picks a node among its 2D or 3D ones, as the
    /// window's chooser says, which the tree then chooses; the status bar (and an agent's ide_action) says what to do.</summary>
    private async Task PickInGameAsync(Godot.GodotGame game, bool on)
    {
        Godot.GodotPicking kind = !on ? Godot.GodotPicking.Off : pickIn3D ? Godot.GodotPicking.In3D : Godot.GodotPicking.In2D;
        using Working? work = parts.Actions.Work.Begin(AppActions.SelectElement, on ? $"Telling {game.Name} to pick a node…" : $"Telling {game.Name} to take its input again…", stoppable: false);
        try
        {
            await game.PickAsync(kind, Outline());
        }
        catch (LiveAppException error)
        {
            Picking(false);
            work?.Tell(false, $"Select Element: {error.Message}");
            parts.Say($"Select Element: {error.Message}");
            return;
        }
        string among = kind == Godot.GodotPicking.In3D ? "3D nodes (in a game drawing nothing, those with a collision shape alone)" : "2D nodes and controls";
        string said = !on ? $"Select Element is off: {game.Name} takes its own input again"
            : game.Headless ? $"Select Element: {game.Name} runs headless, with no window to click in: a click an agent gives at a point (app_click) picks among its {among} there"
            : $"Select Element: click a node in {game.Name}'s window, which picks among its {among}";
        work?.Tell(true, said);
        parts.Say(said);
    }

    /// <summary>A click in the game picked a node, or found none: the tree, asked again, chooses the node, its details and
    /// script's link shown; Select Element is off once one is picked.</summary>
    private void OnGamePicked(object? sender, Godot.GodotObjectState? picked)
    {
        if (sender != app || app is not Godot.GodotGame game)
        {
            return;
        }
        if (picked is null)
        {
            string said = $"Select Element: {game.Name} has no node there that Godot picks";
            parts.Say(said);
            if (game.Picking == Godot.GodotPicking.In3D)
            {
                IdeErrors.Observe(SayMissedAsync(game, said));
            }
            return;
        }
        Picking(false);
        outlined = true;
        Choose((long)picked.Id);
        parts.Dock.Show(window.Id);
        IdeErrors.Observe(RefreshAsync());
    }

    /// <summary>What a 3D pick that found nothing says once the helper looked at the point: why, where it can say.</summary>
    private async Task SayMissedAsync(Godot.GodotGame game, string said)
    {
        if (await game.MissedAsync() is { } missed && app == game)
        {
            parts.Say($"{said}: {missed}");
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
            IdeErrors.Observe(PickedAsync(element));
        }
    }

    /// <summary>The tree asked again for the control picked in the app, which is told as the person's choice once its
    /// details are shown.</summary>
    private async Task PickedAsync(long element)
    {
        await RefreshAsync();
        if (chosen == element && details.Details is { } shown && shown.Id == element)
        {
            ElementShown?.Invoke(this, shown);
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
