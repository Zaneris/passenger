using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Passenger.App.Debugging;
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
    private readonly Panel debugger;
    private readonly ToggleButton debuggerTab;
    private readonly ToggleButton consoleTab;
    private readonly Dictionary<string, Button> tools = [];
    private readonly ToggleButton mute;
    private readonly TextBlock placeholder;
    private readonly DockPanel consolePane;
    private bool showingFrames;

    public DebugView(DebugModel model)
    {
        this.model = model;
        // What is typed into the console is the program's input.
        consolePane = new DockPanel { IsVisible = false, Children = { model.Console } };
        // The sessions' tabs are in the window's header, after its title.
        HeaderTools = new ItemsControl
        {
            ItemsSource = model.Sessions,
            VerticalAlignment = VerticalAlignment.Center,
            // In a narrow window the last tabs give way first, each name ending in an ellipsis.
            ItemsPanel = new FuncTemplate<Panel?>(() => new ShrinkingRow()),
            ItemTemplate = new FuncDataTemplate<DebugTab>((tab, _) => tab is null ? new TextBlock() : SessionTab(tab), supportsRecycling: false),
        };

        threads = new ComboBox
        {
            [DockProperty] = Dock.Top,
            Margin = new Thickness(6, 6, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<DapThread>((thread, _) => new TextBlock { Text = thread is null ? "" : ThreadLabel(thread) }),
            ItemsSource = model.Threads,
        };
        threads.SelectionChanged += (_, _) =>
        {
            if (!showingFrames && threads.SelectedItem is DapThread thread)
            {
                model.ChooseThread(thread);
            }
        };
        frames = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = model.Frames,
            ItemTemplate = new FuncDataTemplate<DapStackFrame>((frame, _) => FrameRow(frame), supportsRecycling: false),
        };
        frames.SelectionChanged += (_, _) =>
        {
            if (!showingFrames && frames.SelectedItem is DapStackFrame frame)
            {
                model.ChooseFrame(frame);
            }
        };
        DockPanel framesPane = new() { Children = { threads, frames } };

        evaluate = new TextBox
        {
            [DockProperty] = Dock.Top,
            Classes = { "search" },
            Margin = new Thickness(6, 6, 6, 4),
        };
        string enter = new Keystroke(Key.Enter, KeyModifiers.None).Display();
        ActionTips.SetWords(evaluate, AppActions.AddToWatches, keys => keys is null ? $"Evaluate expression ({enter})" : $"Evaluate expression ({enter}) or add a watch ({keys})");
        evaluate.AddHandler(KeyDownEvent, OnEvaluateKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The debugger the model has now asked, whichever folder's it is: each folder's sets it as it is made.
        variables = new VariablesList(model.Tree) { DataWatch = node => model.DataWatch!(node) };
        variables.EvaluateRequested += (_, request) => model.Evaluate(request.Expression, request.Watch);
        variables.WatchRemoved += (_, watch) => model.Unwatch(watch);
        variables.SetValueRequested += (_, request) => model.SetValue(request.Node, request.Value);
        variables.DataBreakpointRequested += (_, node) => model.ToggleDataBreakpoint(node);
        variables.TextOpened += (_, node) => model.OpenText(node);
        ListMenus.Attach(frames, FramesMenu);
        ListMenus.Attach(threads, ThreadsMenu);
        DockPanel variablesPane = new() { Children = { evaluate, variables } };

        // The frames a third of the window and the variables the rest, so a narrow window narrows both.
        Grid split = new() { ColumnDefinitions = new ColumnDefinitions("*,1,2*") };
        Grid.SetColumn(framesPane, 0);
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        Grid.SetColumn(variablesPane, 2);
        split.Children.Add(framesPane);
        split.Children.Add(divider);
        split.Children.Add(variablesPane);
        placeholder = new TextBlock { Classes = { "emptyText" } };
        ActionTips.SetWords(placeholder, AppActions.Debug, keys => keys is null ? "Not debugging." : $"Not debugging. Debug starts with {keys}.");
        debugger = new Panel { Children = { split, placeholder } };

        debuggerTab = new ToggleButton { Content = "Debugger", Classes = { "panelTab" }, IsChecked = true };
        consoleTab = new ToggleButton { Content = "Console", Classes = { "panelTab" } };
        debuggerTab.Click += (_, _) => model.ShowTab(console: false);
        consoleTab.Click += (_, _) => model.ShowTab(console: true);
        StackPanel top = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(6, 3),
            Children =
            {
                debuggerTab,
                consoleTab,
                new Border { Width = 12 },
                Tool(AppActions.StepOver, IconKeys.StepOver, "Step Over"),
                Tool(AppActions.StepInto, IconKeys.StepInto, "Step Into"),
                Tool(AppActions.SmartStepInto, IconKeys.SmartStepInto, "Smart Step Into"),
                Tool(AppActions.StepOut, IconKeys.StepOut, "Step Out"),
                Tool(AppActions.RunToCursor, IconKeys.RunToCursor, "Run to Cursor"),
                Tool(AppActions.Evaluate, IconKeys.Evaluate, "Evaluate Expression"),
                new Border { Width = 12 },
                model.Overlays,
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
        Children.Add(new Panel { Children = { debugger, consolePane } });
        model.ToolsChanged += (_, _) => ShowTools();
        model.FramesShown += (_, _) => ShowChosen();
        model.TabShown += (_, _) => ShowTab();
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

    /// <summary>The window header's own tools: the sessions' tabs.</summary>
    public Control HeaderTools { get; }

    /// <summary>Starts typing over the selected row's value, as F2 does (Set Value).</summary>
    public bool EditSelectedValue() => variables.EditSelectedValue();

    /// <summary>The Variables view's list, whose rows are <see cref="DebugModel.Tree"/>'s.</summary>
    public VariablesList VariablesList => variables;

    private Control SessionTab(DebugTab tab)
    {
        TextBlock name = new() { Text = tab.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Classes.Set("muted", tab.HasEnded);
        ToggleButton button = ToolTabs.Tab(name, tab.IsChosen, () => model.Close(tab), "Close Session");
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DebugTab.IsChosen))
            {
                button.IsChecked = tab.IsChosen;
            }
            else if (e.PropertyName == nameof(DebugTab.HasEnded))
            {
                name.Classes.Set("muted", tab.HasEnded);
            }
        };
        // A press on the chosen tab leaves it chosen, where the toggle would let it go.
        button.Click += (_, _) =>
        {
            model.Choose(tab);
            button.IsChecked = tab.IsChosen;
        };
        return button;
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
        foreach (string action in new[] { AppActions.StepOver, AppActions.StepInto, AppActions.SmartStepInto, AppActions.StepOut, AppActions.RunToCursor, AppActions.Evaluate, AppActions.Resume })
        {
            tools[action].IsEnabled = paused;
        }
        tools[AppActions.Pause].IsEnabled = state == DebugState.Running;
        tools[AppActions.Stop].IsEnabled = state is DebugState.Running or DebugState.Paused or DebugState.Starting;
        tools[AppActions.Detach].IsEnabled = model.Detachable;
        tools[AppActions.ApplyHotReloadChanges].IsVisible = model.HotReloadShown.On;
        tools[AppActions.ApplyHotReloadChanges].IsEnabled = model.HotReloadShown.CanApply;
        mute.IsChecked = model.ShowsMuted;
        evaluate.IsEnabled = paused;
        placeholder.IsVisible = state == DebugState.Ended && model.Frames.Count == 0;
    }

    /// <summary>The Debugger or the Console tab, as the model shows it, the chosen session's console in the latter.</summary>
    private void ShowTab()
    {
        if (consolePane.Children[0] != model.Console)
        {
            consolePane.Children.Clear();
            consolePane.Children.Add(model.Console);
        }
        debuggerTab.IsChecked = !model.IsConsoleShown;
        consoleTab.IsChecked = model.IsConsoleShown;
        debugger.IsVisible = !model.IsConsoleShown;
        consolePane.IsVisible = model.IsConsoleShown;
    }

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
            ListMenus.Item("Copy Stack", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, string.Join('\n', model.Frames.Select(f => f.Source?.Path is { } path ? $"{f.Name} in {path}:{model.LineOf(f)}" : f.Name))))),
            ListMenus.Item("Go to Source", "F4", () => model.GoToFrame(chosen!), chosen?.Source is not null),
        ];
    }

    private IReadOnlyList<Control> ThreadsMenu() =>
        threads.SelectedItem is DapThread thread ? [ListMenus.Item("Copy Name", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, ThreadLabel(thread))))] : [];

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

    private Control FrameRow(DapStackFrame? frame)
    {
        if (frame is null)
        {
            return new TextBlock();
        }
        // A frame shows as its method, its module after: the module tells user code from the framework's.
        string name = frame.Name;
        string module = string.Empty;
        if (name.IndexOf('!', StringComparison.Ordinal) is int bang and > 0)
        {
            module = name[..bang];
            name = name[(bang + 1)..];
        }
        TextBlock where = new()
        {
            Tag = frame,
            Text = frame.Source?.Name is { } file ? $"{file}:{model.LineOf(frame)}" : module,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        where.Classes.Add("muted");
        // Its place gives way first, then its name, each ending in an ellipsis.
        return new ShrinkingRow
        {
            Height = ListRow.Height,
            Opacity = frame.PresentationHint == "subtle" ? 0.55 : 1,
            Children =
            {
                new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
                where,
            },
        };
    }
}
