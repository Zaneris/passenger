using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Passenger.App.Debugging;
using Passenger.App.Telemetry;
using Passenger.Dap;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>The Debug tool window over its <see cref="DebugModel"/>, built as it is first shown: a toolbar down the left
/// (rerun, resume, pause, stop, detach, then View Breakpoints and Mute Breakpoints), the stepping actions (Smart Step Into
/// after Step Into) across the top beside the Debugger and Console tabs, and under Debugger the frames of the chosen
/// thread on the left with the variables of the chosen frame on the right, an expression to evaluate or watch above
/// them. Each program debugged has a tab in the window's header (<see cref="DebugModel.Sessions"/>), whose frames,
/// values, console and toolbar the window shows while it is chosen.</summary>
public sealed class DebugView : DockPanel, IOwnKeys
{
    private readonly DebugModel model;
    private readonly ComboBox threads;
    private readonly ListBox frames;
    private readonly VariablesList variables;
    private readonly TextBox evaluate;
    private readonly DockPanel debugger;
    /// <summary>The Debugger, Console and Telemetry tabs, by their words.</summary>
    private readonly ToolTabs.Strip<string> tabs = new();
    internal DebugMemoryView Memory { get; }
    private readonly TelemetryPane telemetry = new() { IsVisible = false };
    private readonly Dictionary<string, Button> tools = [];
    private readonly ToggleButton mute;
    private readonly EmptyState placeholder;
    private readonly Banner offer = new();
    private readonly Button offerLink;
    private readonly DockPanel consolePane;
    private readonly ListBox snapshots;
    private readonly EmptyState noSnapshots;
    private readonly Panel snapshotsPane;
    private readonly ListBox moments;
    private readonly EmptyState noMoments;
    private readonly TextBlock timelineSaid;
    private readonly DockPanel timelinePane;
    private readonly ToggleButton record;
    private bool showingFrames;

    public DebugView(DebugModel model)
    {
        this.model = model;
        // What is typed into the console is the program's input.
        consolePane = new DockPanel { IsVisible = false, Children = { model.Console } };
        // The sessions' tabs are in the window's header, after its title.
        ItemsControl sessions = new()
        {
            ItemsSource = model.Sessions,
            VerticalAlignment = VerticalAlignment.Center,
            // In a narrow window the last tabs give way first, each name ending in an ellipsis.
            ItemsPanel = new FuncTemplate<Panel?>(() => new ShrinkingRow()),
        };
        ReusedRows.Use(sessions, SessionTab);
        HeaderTools = sessions;

        threads = new ToolDropDown("No paused threads")
        {
            [DockProperty] = Dock.Top,
            Margin = new Thickness(Gaps.Small, Gaps.Small, Gaps.Small, Gaps.Tight),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = model.Threads,
        };
        Avalonia.Automation.AutomationProperties.SetName(threads, "Threads");
        ReusedRows.Use(threads, () =>
        {
            TextBlock label = new();
            return label.Showing<DapThread>(thread => label.Text = ThreadLabel(thread));
        });
        threads.SelectionChanged += (_, _) =>
        {
            if (!showingFrames && threads.SelectedItem is DapThread thread)
            {
                model.ChooseThread(thread);
            }
        };
        frames = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = model.Frames,
        };
        ReusedRows.Use(frames, FrameRow);
        frames.SelectionChanged += (_, _) =>
        {
            if (!showingFrames && frames.SelectedItem is DapStackFrame frame)
            {
                model.ChooseFrame(frame);
            }
        };
        // F4 goes to the chosen frame's line, as its menu's Go to Source does.
        ListKeys.Act(frames, new ListActs
        {
            Source = () =>
            {
                if (frames.SelectedItem is DapStackFrame { Source: not null } frame)
                {
                    model.GoToFrame(frame);
                }
            },
        });
        DockPanel framesPane = new() { Children = { threads, frames } };

        evaluate = new TextBox
        {
            [DockProperty] = Dock.Top,
            [Avalonia.Automation.AutomationProperties.NameProperty] = "Evaluate expression",
            Margin = new Thickness(Gaps.Small, Gaps.Small, Gaps.Small, Gaps.Tight),
        };
        string enter = KeyWords.Of(Key.Enter);
        ActionTips.SetWords(evaluate, AppActions.AddToWatches, keys => keys is null ? $"Evaluate expression ({enter})" : $"Evaluate expression ({enter}) or add a watch ({keys})");
        evaluate.AddHandler(KeyDownEvent, OnEvaluateKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The debugger the model has now asked, whichever folder's it is: each folder's sets it as it is made.
        variables = new VariablesList(model.Tree) { DataWatch = node => model.DataWatch!(node) };
        variables.EvaluateRequested += (_, request) => model.Evaluate(request.Expression, request.Watch);
        variables.WatchRemoved += (_, watch) => model.Unwatch(watch);
        variables.SetValueRequested += (_, request) => model.SetValue(request.Node, request.Value);
        variables.RunWithThreadsRequested += (_, node) => model.RunWithThreads(node);
        variables.PinRequested += (_, node) => model.Pin(node);
        variables.MarkRequested += (_, node) => model.Mark(node);
        variables.MarksWhyNot = () => model.MarksWhyNot();
        variables.DataBreakpointRequested += (_, node) => model.ToggleDataBreakpoint(node);
        variables.TextOpened += (_, node) => model.OpenText(node);
        Memory = new DebugMemoryView(model);
        variables.MemoryRequested += (_, request) =>
        {
            model.ShowMemory();
            IdeErrors.Observe(Memory.OpenAsync(request.Address, request.Expression));
        };
        variables.CanReadMemory = () => model.Sessions.Any(tab => tab.IsChosen && tab.Session is { ReadsMemory: true, State: DebugState.Paused });
        ListMenus.Attach(frames, FramesMenu);
        ListMenus.Attach(threads, ThreadsMenu);
        // Over the values, once every thread ran in an evaluation: those read before it may have moved.
        Banner moved = new();
        moved.Link(ReadAgain, model.ReadAgain);
        moved.Say(model.Moved);
        // Over the values of a snapshot shown as a past stop: which it is, its values as they were then.
        Banner past = Banner.Wrapping();
        past.Link(BackToSnapshots, model.ShowSnapshots);
        past.Say(PastSaid());
        // Over the values of a recorded moment shown as a past stop: which it is, the live stop a link away.
        Banner recalled = Banner.Wrapping();
        recalled.Link(BackToLive, model.BackToLive);
        recalled.Say(RecalledSaid());
        model.MovedChanged += (_, _) =>
        {
            moved.Say(model.Moved);
            past.Say(PastSaid());
            recalled.Say(RecalledSaid());
            ShowTools();
        };
        DockPanel variablesPane = new() { Children = { evaluate, moved, past, recalled, variables } };

        // The frames a third of the window and the variables the rest, so a narrow window narrows both.
        PaneSplit split = new(framesPane, variablesPane, "*,1,2*");
        placeholder = new EmptyState("Not debugging");
        placeholder.Offer(AppActions.Debug, "Debug");
        placeholder.ActionRequested += (_, action) => model.Request(action);
        // Over the frames and values, what the chosen program's console offers: a terminal, for one stopped needing it.
        offerLink = offer.Link(null, () => model.Console.TakeOffer());
        debugger = new DockPanel { Children = { offer, new Panel { Children = { split, placeholder } } } };

        // The folder's snapshots, the newest first: Enter or a double click shows one as a past stop.
        snapshots = new ListBox { Classes = { StyleClasses.Tree }, ItemsSource = model.Snapshots };
        Avalonia.Automation.AutomationProperties.SetName(snapshots, SnapshotsTab);
        ReusedRows.Use(snapshots, SnapshotRow);
        ListKeys.Act(snapshots, new ListActs
        {
            Open = () =>
            {
                if (snapshots.SelectedItem is TakenSnapshot chosen)
                {
                    model.Choose(chosen);
                }
            },
        });
        ListMenus.Attach(snapshots, () => snapshots.SelectedItem is not TakenSnapshot chosen ? [] :
        [
            ListMenus.Open(ShowAsStop, () => model.Choose(chosen)),
            ListMenus.Item("Copy Stack", () => IdeErrors.Observe(ListMenus.CopyAsync(this, string.Join('\n', chosen.Frames.Select(f => f.Source?.Path is { } path ? $"{f.Name} in {path}:{f.Line}" : f.Name))))),
        ]);
        noSnapshots = new EmptyState(Refusals.NoSnapshots.Shown);
        noSnapshots.Offer(AppActions.ToggleSnapshotPoint, "Toggle Snapshot Point");
        noSnapshots.ActionRequested += (_, action) => model.Request(action);
        snapshotsPane = new Panel { IsVisible = false, Children = { snapshots, noSnapshots } };
        model.Snapshots.CollectionChanged += (_, _) => noSnapshots.IsVisible = model.Snapshots.Count == 0;

        // The chosen program's recorded moments, oldest first: Enter or a double click shows one as a past stop.
        moments = new ListBox { Classes = { StyleClasses.Tree } };
        Avalonia.Automation.AutomationProperties.SetName(moments, TimelineTab);
        ReusedRows.Use(moments, MomentRow);
        ListKeys.Act(moments, new ListActs
        {
            Open = () =>
            {
                if (moments.SelectedItem is DapMoment chosen)
                {
                    model.Choose(chosen);
                }
            },
        });
        ListMenus.Attach(moments, () => moments.SelectedItem is not DapMoment chosen ? [] : [ListMenus.Open(ShowAsStop, () => model.Choose(chosen))]);
        noMoments = new EmptyState(Refusals.NothingRecorded.Shown);
        noMoments.Offer(AppActions.RecordCalls, "Record Calls");
        noMoments.ActionRequested += (_, action) => model.Request(action);
        timelineSaid = new TextBlock { [DockProperty] = Dock.Top, Margin = new Thickness(Gaps.Normal, Gaps.Small), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        timelineSaid.Classes.Add(StyleClasses.Muted);
        timelinePane = new DockPanel { IsVisible = false, Children = { timelineSaid, new Panel { Children = { moments, noMoments } } } };
        model.TimelineChanged += (_, _) => ShowTimeline();

        tabs.Add(DebuggerTab, DebuggerTab);
        tabs.Add(ConsoleTab, ConsoleTab);
        tabs.Add(MemoryTab, MemoryTab, name: "debugMemoryTab");
        tabs.Add(RunWindows.TelemetryTab, RunWindows.TelemetryTab, name: "debugTelemetryTab").IsVisible = false;
        tabs.Add(SnapshotsTab, SnapshotsTab, name: "debugSnapshotsTab");
        tabs.Add(TimelineTab, TimelineTab, name: "debugTimelineTab");
        tabs.Choice = DebuggerTab;
        tabs.Chosen += (_, tab) =>
        {
            if (tab == RunWindows.TelemetryTab)
            {
                model.ShowTelemetry();
            }
            else if (tab == SnapshotsTab)
            {
                model.ShowSnapshots();
            }
            else if (tab == TimelineTab)
            {
                model.ShowTimelineTab();
            }
            else if (tab == MemoryTab)
            {
                model.ShowMemory();
            }
            else
            {
                model.ShowTab(console: tab == ConsoleTab);
            }
        };
        // Pressed while the programs debugged record their calls.
        record = ToolButtons.Toggle(IconKeys.RecordCalls, "Record Calls");
        ActionTips.Set(record, AppActions.RecordCalls, "Record Calls");
        record.Click += (_, _) =>
        {
            model.Request(AppActions.RecordCalls);
            record.IsChecked = model.ShowsRecording;
        };

        ToolFlow top = new()
        {
            Margin = new Thickness(Gaps.Small, Gaps.Hair),
            Children =
            {
                tabs,
                new Border { Width = 12 },
                Tool(AppActions.StepOver, IconKeys.StepOver, "Step Over"),
                Tool(AppActions.StepInto, IconKeys.StepInto, "Step Into"),
                Tool(AppActions.SmartStepInto, IconKeys.SmartStepInto, "Smart Step Into"),
                Tool(AppActions.StepOut, IconKeys.StepOut, "Step Out"),
                record,
                Tool(AppActions.StepBack, IconKeys.StepBack, "Step Back"),
                Tool(AppActions.StepForward, IconKeys.StepForward, "Step Forward"),
                Tool(AppActions.RunToCursor, IconKeys.RunToCursor, "Run to Cursor"),
                Tool(AppActions.Evaluate, IconKeys.Evaluate, "Evaluate Expression"),
                new Border { Width = 12 },
                model.Overlays,
                model.GameAudio,
            },
        };
        Border topBar = ToolBars.Top(top);

        // Under the program's own buttons: pressed while every breakpoint is muted.
        mute = ToolButtons.Toggle(IconKeys.MuteBreakpoints, "Mute Breakpoints");
        ActionTips.Set(mute, AppActions.MuteBreakpoints, "Mute Breakpoints");
        mute.Click += (_, _) =>
        {
            model.Request(AppActions.MuteBreakpoints);
            mute.IsChecked = model.ShowsMuted;
        };

        Border leftBar = ToolBars.Side(
            Tool(AppActions.Rerun, IconKeys.Rerun, "Rerun"),
            Tool(AppActions.Resume, IconKeys.Resume, "Resume Program"),
            Tool(AppActions.Pause, IconKeys.Pause, "Pause Program"),
            Tool(AppActions.Stop, IconKeys.Stop, "Stop"),
            Tool(AppActions.Detach, IconKeys.Detach, "Detach"),
            HotReloadTool(),
            ToolBars.Gap(),
            Tool(AppActions.ViewBreakpoints, IconKeys.Breakpoints, "View Breakpoints and Exceptions"),
            mute);

        Children.Add(leftBar);
        Children.Add(topBar);
        Children.Add(new Panel { Children = { debugger, consolePane, Memory, telemetry, snapshotsPane, timelinePane } });
        model.ToolsChanged += (_, _) => ShowTools();
        model.FramesShown += (_, _) => ShowChosen();
        model.TabShown += (_, _) => ShowTab();
        model.OfferChanged += (_, _) => ShowOffer();
        model.EvaluateFocused += (_, _) => FocusEvaluate();
        model.PlacesMoved += (_, _) => ShowPlacesAgain();
        ShowTools();
        ShowChosen();
        ShowTab();
        if (model.EvaluateText is { } text)
        {
            evaluate.Text = text;
        }
    }

    /// <summary>The link over Variables that reads the frame's values anew once every thread ran in an evaluation.</summary>
    public const string ReadAgain = "Read Again";

    /// <summary>The link over a snapshot's values back to the list of snapshots.</summary>
    public const string BackToSnapshots = "All Snapshots";

    /// <summary>The link over a recorded moment's values back to the live stop.</summary>
    public const string BackToLive = "Live Stop";

    /// <summary>The tab of the chosen program's recorded moments.</summary>
    public const string TimelineTab = "Timeline";

    /// <summary>The Timeline tab's list, oldest first.</summary>
    public ListBox MomentsList => moments;

    /// <summary>What the Timeline tab says over its moments.</summary>
    public string? TimelineWords => timelineSaid.Text;

    /// <summary>A snapshot's menu item that shows it as a past stop, as Enter does.</summary>
    public const string ShowAsStop = "Show Frames and Values";

    private const string DebuggerTab = "Debugger";

    private const string ConsoleTab = "Console";
    private const string MemoryTab = "Memory";

    /// <summary>The tab of the folder's snapshots.</summary>
    public const string SnapshotsTab = "Snapshots";

    /// <summary>The Snapshots tab's list, newest first.</summary>
    public ListBox SnapshotsList => snapshots;

    /// <summary>What Variables says over a snapshot's values shown as a past stop, null while it shows none.</summary>
    private string? PastSaid() => model.ShownSnapshot is { Moment: null } shown ? shown.Taken.Cut is { } cut ? $"{shown.Said} {cut}." : shown.Said : null;

    /// <summary>What Variables says over a recorded moment's values shown as a past stop, null while it shows none.</summary>
    private string? RecalledSaid() => model.ShownSnapshot is { Moment: not null } shown ? shown.Taken.Cut is { } cut ? $"{shown.Said} {cut}." : shown.Said : null;

    /// <summary>A recorded moment's row: what happened, indented by the recorded calls it ran in, then where and when, muted.</summary>
    private Control MomentRow()
    {
        TextBlock said = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock where = new()
        {
            Classes = { StyleClasses.Caption },
            Margin = new Thickness(Gaps.Normal, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        where.Classes.Add(StyleClasses.Muted);
        ShrinkingRow row = new() { Height = ListRow.Height, Children = { said, where } };
        return row.Showing<DapMoment>(moment =>
        {
            row.Margin = new Thickness(moment.Depth * Gaps.Indent, 0, 0, 0);
            said.Text = moment.Said;
            string place = moment.Place.Source?.Path is { } path ? $"{System.IO.Path.GetFileName(path)}:{moment.Place.Line}" : moment.Place.Name;
            where.Text = $"{place} · {moment.At.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)}";
        });
    }

    /// <summary>The timeline as last read, the moment shown chosen in it.</summary>
    private void ShowTimeline()
    {
        if (!ReferenceEquals(moments.ItemsSource, model.Moments))
        {
            moments.ItemsSource = model.Moments;
        }
        timelineSaid.Text = model.TimelineSaid;
        timelineSaid.IsVisible = model.TimelineSaid is not null;
        noMoments.IsVisible = model.Moments.Count == 0;
        DapMoment? shown = model.MomentShown is { } moment ? model.Moments.FirstOrDefault(m => m.Index == moment.Index) : null;
        moments.SelectedItem = shown;
        if (shown is not null)
        {
            moments.ScrollIntoView(shown);
        }
    }

    /// <summary>A snapshot's row: its point and number, then when, on which thread and how long the program was held, muted.</summary>
    private Control SnapshotRow()
    {
        TextBlock point = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock when = new()
        {
            Classes = { StyleClasses.Caption },
            Margin = new Thickness(Gaps.Normal, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        when.Classes.Add(StyleClasses.Muted);
        ShrinkingRow row = new() { Height = ListRow.Height, Children = { point, when } };
        return row.Showing<TakenSnapshot>(snapshot =>
        {
            point.Text = $"{snapshot.Point} #{snapshot.Number}";
            when.Text = $"{snapshot.Time} · {snapshot.Program} · {snapshot.ThreadName} · held {Core.Durations.Said(snapshot.Paused)}";
        });
    }

    /// <summary>The window header's own tools: the sessions' tabs.</summary>
    public Control HeaderTools { get; }

    /// <summary>Starts typing over the selected row's value, as F2 does (Set Value).</summary>
    public bool EditSelectedValue() => variables.EditSelectedValue();

    /// <summary>The Variables view's list, whose rows are <see cref="DebugModel.Tree"/>'s.</summary>
    public VariablesList VariablesList => variables;

    /// <summary>A session's tab for whichever session the strip hands it: its name, muted once the session has ended, and
    /// its ×, which closes the session it shows.</summary>
    private Control SessionTab()
    {
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        ToggleButton button = null!;
        button = ToolTabs.Tab(name, () =>
        {
            if (button.DataContext is DebugTab shown)
            {
                model.Close(shown);
            }
        }, "Close Session");
        button.Click += (_, _) =>
        {
            if (button.DataContext is DebugTab shown)
            {
                model.Choose(shown);
            }
        };
        return button.Showing<DebugTab>(tab =>
        {
            name.Text = tab.Name;
            name.Classes.Set(StyleClasses.Muted, tab.HasEnded);
            button.IsChecked = tab.IsChosen;
        });
    }

    /// <summary>The thread and frame the model chose, without raising the choice back.</summary>
    private void ShowChosen()
    {
        showingFrames = true;
        try
        {
            threads.SelectedItem = model.SelectedThread;
            frames.SelectedItem = model.SelectedFrame;
        }
        finally
        {
            showingFrames = false;
        }
    }

    /// <summary>What can be pressed in the state the model shows.</summary>
    private void ShowTools()
    {
        DebugState state = model.State;
        bool paused = state == DebugState.Paused;
        foreach (string action in new[] { AppActions.StepOver, AppActions.StepInto, AppActions.SmartStepInto, AppActions.StepOut, AppActions.StepBack, AppActions.StepForward,
            AppActions.RunToCursor, AppActions.Evaluate, AppActions.Resume })
        {
            tools[action].IsEnabled = paused;
        }
        tools[AppActions.Pause].IsEnabled = state == DebugState.Running;
        tools[AppActions.Stop].IsEnabled = state is DebugState.Running or DebugState.Paused or DebugState.Starting;
        tools[AppActions.Detach].IsEnabled = model.Detachable;
        tools[AppActions.ApplyHotReloadChanges].IsVisible = model.HotReloadShown.On;
        tools[AppActions.ApplyHotReloadChanges].IsEnabled = model.HotReloadShown.CanApply;
        mute.IsChecked = model.ShowsMuted;
        record.IsChecked = model.ShowsRecording;
        evaluate.IsEnabled = paused && model.ShownSnapshot is null;
        placeholder.IsVisible = state == DebugState.Ended && model.Frames.Count == 0 && model.ShownSnapshot is null;
    }

    /// <summary>The Debugger or the Console tab, as the model shows it, the chosen session's console in the latter.</summary>
    private void ShowTab()
    {
        if (consolePane.Children[0] != model.Console)
        {
            consolePane.Children.Clear();
            consolePane.Children.Add(model.Console);
        }
        // The Telemetry tab is there while the chosen program's run has records; without them its place is the console's.
        telemetry.Source = model.ChosenDashboard?.Telemetry;
        tabs[RunWindows.TelemetryTab].IsVisible = telemetry.Source is not null;
        bool records = model.IsTelemetryShown && telemetry.Source is not null;
        bool console = model.IsConsoleShown || model.IsTelemetryShown && !records;
        bool taken = model.IsSnapshotsShown;
        bool recorded = model.IsTimelineShown;
        bool memory = model.IsMemoryShown;
        tabs.Choice = memory ? MemoryTab : recorded ? TimelineTab : taken ? SnapshotsTab : records ? RunWindows.TelemetryTab : console ? ConsoleTab : DebuggerTab;
        debugger.IsVisible = !memory && !console && !records && !taken && !recorded;
        Memory.IsVisible = memory;
        Memory.UpdateStop();
        timelinePane.IsVisible = recorded;
        consolePane.IsVisible = console;
        telemetry.IsVisible = records;
        snapshotsPane.IsVisible = taken;
        noSnapshots.IsVisible = model.Snapshots.Count == 0;
        ShowOffer();
    }

    /// <summary>What the chosen program's console offers, over the Debugger tab too.</summary>
    private void ShowOffer()
    {
        ShownWords.Set(offerLink, ContentControl.ContentProperty, model.Console.OfferWords?.Link);
        offer.Say(model.Console.OfferWords?.Said);
    }

    /// <summary>What the Debugger tab offers over its frames and values, null while it offers nothing.</summary>
    public string? Offered => offer.Said;

    /// <summary>The chosen program's Telemetry, the Telemetry tab's.</summary>
    public TelemetryPane Telemetry => telemetry;

    private void FocusEvaluate()
    {
        evaluate.Text = model.EvaluateText;
        evaluate.SelectAll();
        evaluate.Focus();
    }

    /// <summary>Add to Watches from the Evaluate field, by the keys the keymap in force gives it there
    /// (<see cref="AppActions.KeysIn"/>), ahead of what they run elsewhere.</summary>
    public bool TakesKey(KeyEventArgs e) => evaluate.IsKeyboardFocusWithin && AppActions.Presses(AppActions.AddToWatches, e);

    private void OnEvaluateKey(object? sender, KeyEventArgs e)
    {
        bool watch = AppActions.Presses(AppActions.AddToWatches, e);
        if ((!watch && e.Key != Key.Enter) || string.IsNullOrWhiteSpace(evaluate.Text))
        {
            return;
        }
        model.Evaluate(evaluate.Text.Trim(), watch);
        if (watch)
        {
            evaluate.Text = string.Empty;
        }
        e.Handled = true;
    }

    /// <summary>The Frames menu: Copy Stack, every frame a line, and Go to Source for the one chosen.</summary>
    private IReadOnlyList<Control> FramesMenu()
    {
        if (model.Frames.Count == 0)
        {
            return [];
        }
        DapStackFrame? chosen = frames.SelectedItem as DapStackFrame;
        return
        [
            ListMenus.Item("Copy Stack", () => IdeErrors.Observe(ListMenus.CopyAsync(this, string.Join('\n', model.Frames.Select(f => f.Source?.Path is { } path ? $"{f.Name} in {path}:{model.LineOf(f)}" : f.Name))))),
            ListMenus.Source(() => model.GoToFrame(chosen!), chosen is null ? Refusals.NoFrameChosen : chosen.Source is null ? Refusals.FrameUnplaced : null),
        ];
    }

    private IReadOnlyList<Control> ThreadsMenu() =>
        threads.SelectedItem is DapThread thread ? [ListMenus.Item("Copy Name", () => IdeErrors.Observe(ListMenus.CopyAsync(this, ThreadLabel(thread))))] : [];

    /// <summary>Apply Hot Reload Changes, a flame: shown while the session has Hot Reload.</summary>
    private Button HotReloadTool()
    {
        Button button = Tool(AppActions.ApplyHotReloadChanges, IconKeys.HotReload, "Apply Hot Reload Changes");
        ((PathIcon)button.Content!).Bind(PathIcon.ForegroundProperty, button.GetResourceObservable(TokenKeys.HotReload));
        button.IsVisible = false;
        return button;
    }

    private Button Tool(string action, string icon, string tip) => tools[action] = ToolButtons.Of(action, icon, tip, model.Request);

    private static string ThreadLabel(DapThread thread) =>
        string.IsNullOrEmpty(thread.Name) || thread.Name == "<No Name>" ? $"Thread {thread.Id}" : $"{thread.Name} ({thread.Id})";

    /// <summary>Says again the place each frame's row names, whose code an edit made while paused may have moved.</summary>
    private void ShowPlacesAgain()
    {
        foreach (TextBlock where in frames.GetVisualDescendants().OfType<TextBlock>())
        {
            if (where.Tag is DapStackFrame { Source.Name: { } file } frame)
            {
                where.Text = $"{file}:{model.LineOf(frame)}";
            }
        }
    }

    /// <summary>A frame's row: its method, then its place (its module where it has no source) muted, a frame the
    /// adapter calls subtle faded.</summary>
    private Control FrameRow()
    {
        TextBlock method = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock where = new()
        {
            Classes = { StyleClasses.Caption },
            Margin = new Thickness(Gaps.Normal, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        where.Classes.Add(StyleClasses.Muted);
        // Its place gives way first, then its name, each ending in an ellipsis.
        ShrinkingRow row = new() { Height = ListRow.Height, Children = { method, where } };
        return row.Showing<DapStackFrame>(frame =>
        {
            // A frame shows as its method, its module after: the module tells user code from the framework's.
            string name = frame.Name;
            string module = string.Empty;
            if (name.IndexOf('!', StringComparison.Ordinal) is int bang and > 0)
            {
                module = name[..bang];
                name = name[(bang + 1)..];
            }
            method.Text = name;
            where.Tag = frame;
            where.Text = frame.Source?.Name is { } file ? $"{file}:{model.LineOf(frame)}" : module;
            ListRow.Dim(row, frame.PresentationHint == FrameHints.Subtle);
        });
    }
}
