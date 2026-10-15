using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Debugging;
using Passenger.Dap;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>The Debug tool window, laid out as Rider's: a toolbar down the left (rerun, resume,
/// pause, stop, detach, then View Breakpoints and Mute Breakpoints), the stepping actions (Smart Step Into after Step Into,
/// as Rider's) across the top beside the Debugger and Console tabs, and under
/// Debugger the frames of the chosen thread on the left with the variables of the chosen frame on the
/// right, an expression to evaluate or watch above them.</summary>
public sealed class DebugView : DockPanel
{
    private readonly ComboBox threads;
    private readonly ListBox frames;
    private readonly ListBox variables;
    private readonly TextBox evaluate;
    private readonly Panel debugger;
    private readonly ToggleButton debuggerTab;
    private readonly ToggleButton consoleTab;
    private readonly Dictionary<string, Button> tools = [];
    private readonly ToggleButton mute;
    private readonly TextBlock placeholder;
    private readonly DockPanel consolePane;
    private bool showingFrames;

    public DebugView()
    {
        VariablesTree tree = Tree;
        Console = new OutputView();
        // What is typed into the console is the program's input, as in Rider's Debug console.
        consolePane = new DockPanel { IsVisible = false, Children = { Console } };

        threads = new ComboBox
        {
            [DockProperty] = Dock.Top,
            Margin = new Thickness(6, 6, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<DapThread>((thread, _) => new TextBlock { Text = thread is null ? "" : ThreadLabel(thread) }),
            ItemsSource = Threads,
        };
        threads.SelectionChanged += (_, _) =>
        {
            if (!showingFrames && threads.SelectedItem is DapThread thread)
            {
                ThreadChosen?.Invoke(this, thread);
            }
        };
        frames = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = Frames,
            ItemTemplate = new FuncDataTemplate<DapStackFrame>((frame, _) => FrameRow(frame), supportsRecycling: false),
        };
        frames.SelectionChanged += (_, _) =>
        {
            if (!showingFrames && frames.SelectedItem is DapStackFrame frame)
            {
                FrameChosen?.Invoke(this, frame);
            }
        };
        DockPanel framesPane = new() { Children = { threads, frames } };

        evaluate = new TextBox
        {
            [DockProperty] = Dock.Top,
            Classes = { "search" },
            Margin = new Thickness(6, 6, 6, 4),
            PlaceholderText = $"Evaluate expression (Enter) or add a watch ({(OperatingSystem.IsMacOS() ? "⇧⌘Enter" : "Ctrl+Shift+Enter")})",
        };
        evaluate.AddHandler(KeyDownEvent, OnEvaluateKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        variables = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = tree.Rows,
            ItemTemplate = new FuncDataTemplate<VariableNode>((node, _) => VariableRow(node), supportsRecycling: false),
        };
        variables.DoubleTapped += (_, _) =>
        {
            // A double-click opens what has members, and sets what has none, as in Rider.
            if (variables.SelectedItem is VariableNode node && node.HasChildren)
            {
                _ = tree.Toggle(node);
            }
            else
            {
                EditSelectedValue();
            }
        };
        variables.KeyDown += OnVariablesKey;
        ListMenus.Attach(variables, VariablesMenu);
        ListMenus.Attach(frames, FramesMenu);
        ListMenus.Attach(threads, ThreadsMenu);
        DockPanel variablesPane = new() { Children = { evaluate, variables } };

        Grid split = new() { ColumnDefinitions = new ColumnDefinitions("320,1,*") };
        Grid.SetColumn(framesPane, 0);
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        Grid.SetColumn(variablesPane, 2);
        split.Children.Add(framesPane);
        split.Children.Add(divider);
        split.Children.Add(variablesPane);
        placeholder = new TextBlock
        {
            Text = "Not debugging. Debug starts with Alt+F5.",
            Classes = { "emptyHint" },
        };
        debugger = new Panel { Children = { split, placeholder } };

        debuggerTab = new ToggleButton { Content = "Debugger", Classes = { "panelTab" }, IsChecked = true };
        consoleTab = new ToggleButton { Content = "Console", Classes = { "panelTab" } };
        debuggerTab.Click += (_, _) => ShowTab(console: false);
        consoleTab.Click += (_, _) => ShowTab(console: true);
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
                Tool(AppActions.StepOver, "Icon.StepOver", "Step Over"),
                Tool(AppActions.StepInto, "Icon.StepInto", "Step Into"),
                Tool(AppActions.SmartStepInto, "Icon.SmartStepInto", "Smart Step Into"),
                Tool(AppActions.StepOut, "Icon.StepOut", "Step Out"),
                Tool(AppActions.RunToCursor, "Icon.RunToCursor", "Run to Cursor"),
                Tool(AppActions.Evaluate, "Icon.Evaluate", "Evaluate Expression"),
                new Border { Width = 12 },
                Overlays,
            },
        };
        Border topBar = new() { [DockProperty] = Dock.Top, Height = 32, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
        topBar.Bind(Border.BorderBrushProperty, topBar.GetResourceObservable("Passenger.Divider"));

        // Rider's toggle under the program's own buttons: pressed while every breakpoint is muted.
        mute = new ToggleButton { Classes = { "tool" }, Content = new PathIcon() };
        string? muteKeys = AppActions.Keymap().ShortcutsFor(AppActions.MuteBreakpoints).FirstOrDefault()?.Display();
        ToolTip.SetTip(mute, muteKeys is null ? "Mute Breakpoints" : $"Mute Breakpoints ({muteKeys})");
        ((PathIcon)mute.Content).Bind(PathIcon.DataProperty, mute.GetResourceObservable("Icon.MuteBreakpoints"));
        mute.Click += (_, _) => ActionRequested?.Invoke(this, AppActions.MuteBreakpoints);

        StackPanel left = new()
        {
            Spacing = 2,
            Margin = new Thickness(0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                Tool(AppActions.Rerun, "Icon.Rerun", "Rerun"),
                Tool(AppActions.Resume, "Icon.Resume", "Resume Program"),
                Tool(AppActions.Pause, "Icon.Pause", "Pause Program"),
                Tool(AppActions.Stop, "Icon.Stop", "Stop"),
                Tool(AppActions.Detach, "Icon.Detach", "Detach"),
                HotReloadTool(),
                new Border { Height = 8 },
                Tool(AppActions.ViewBreakpoints, "Icon.Breakpoints", "View Breakpoints and Exceptions"),
                mute,
            },
        };
        Border leftBar = new() { [DockProperty] = Dock.Left, Width = 36, BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        leftBar.Bind(Border.BorderBrushProperty, leftBar.GetResourceObservable("Passenger.Divider"));

        Children.Add(leftBar);
        Children.Add(topBar);
        Children.Add(new Panel { Children = { debugger, consolePane } });
        SetState(DebugState.Ended);
    }

    /// <summary>A toolbar button pressed: the app action it runs.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>Avalonia's debug overlays in the app being debugged, shown while one with the inspector in it runs.</summary>
    public OverlaysButton Overlays { get; } = new();

    public event EventHandler<DapThread>? ThreadChosen;

    public event EventHandler<DapStackFrame>? FrameChosen;

    /// <summary>An expression to evaluate once (false), or to keep as a watch (true).</summary>
    public event EventHandler<(string Expression, bool Watch)>? EvaluateRequested;

    /// <summary>A watch to stop watching.</summary>
    public event EventHandler<string>? WatchRemoved;

    /// <summary>Set Value: a row's new value typed in its place (a C# expression), Enter pressed.</summary>
    public event EventHandler<(VariableNode Node, string Value)>? SetValueRequested;

    /// <summary>Set Data Breakpoint, or Remove Data Breakpoint, chosen on a row.</summary>
    public event EventHandler<VariableNode>? DataBreakpointRequested;

    /// <summary>What Set Data Breakpoint offers on a row, the debugger asked: the menu's item says so once it answers.</summary>
    public Func<VariableNode, Task<DataWatchOffer>>? DataWatch { get; set; }

    /// <summary>Starts typing over the selected row's value, as F2 does: Rider's Set Value.</summary>
    public bool EditSelectedValue()
    {
        if (variables.SelectedItem is not VariableNode { Expression: not null } node)
        {
            return false;
        }
        node.IsEditing = true;
        return true;
    }

    public VariablesTree Tree { get; } = new();

    /// <summary>The Variables view's list, whose rows are <see cref="Tree"/>'s.</summary>
    public ListBox VariablesList => variables;

    /// <summary>The program's output, as Rider's Console tab shows it.</summary>
    public OutputView Console { get; }

    public ObservableCollection<DapThread> Threads { get; } = [];

    public ObservableCollection<DapStackFrame> Frames { get; } = [];

    public DapStackFrame? SelectedFrame => frames.SelectedItem as DapStackFrame;

    public bool IsConsoleShown => consolePane.IsVisible;

    /// <summary>Shows a thread's frames, one of them chosen, without raising the choice back.</summary>
    public void ShowFrames(IReadOnlyList<DapThread> all, DapThread? thread, IReadOnlyList<DapStackFrame> stack, DapStackFrame? chosen)
    {
        showingFrames = true;
        try
        {
            Threads.Clear();
            foreach (DapThread each in all)
            {
                Threads.Add(each);
            }
            threads.SelectedItem = thread is null ? null : Threads.FirstOrDefault(t => t.Id == thread.Id);
            Frames.Clear();
            foreach (DapStackFrame frame in stack)
            {
                Frames.Add(frame);
            }
            frames.SelectedItem = chosen;
        }
        finally
        {
            showingFrames = false;
        }
    }

    /// <summary>What can be pressed in each state. The panes show what the controller gives them: a stop's frames and
    /// values, cleared a moment after the program goes on, as Rider's are.</summary>
    public void SetState(DebugState state)
    {
        bool paused = state == DebugState.Paused;
        bool live = state is DebugState.Running or DebugState.Paused or DebugState.Starting;
        foreach (string action in new[] { AppActions.StepOver, AppActions.StepInto, AppActions.SmartStepInto, AppActions.StepOut, AppActions.RunToCursor, AppActions.Evaluate, AppActions.Resume })
        {
            tools[action].IsEnabled = paused;
        }
        tools[AppActions.Pause].IsEnabled = state == DebugState.Running;
        tools[AppActions.Stop].IsEnabled = live;
        tools[AppActions.Detach].IsEnabled &= live;
        evaluate.IsEnabled = paused;
        placeholder.IsVisible = state == DebugState.Ended && Frames.Count == 0;
    }

    /// <summary>Detach is pressed only while the debugger has hold of a program it attached to.</summary>
    public void ShowDetachable(bool attached) => tools[AppActions.Detach].IsEnabled = attached;

    /// <summary>Shows Mute Breakpoints pressed while the breakpoints are muted.</summary>
    public void ShowMuted(bool muted) => mute.IsChecked = muted;

    /// <summary>Whether Mute Breakpoints shows pressed.</summary>
    public bool ShowsMuted => mute.IsChecked == true;

    /// <summary>Clears what the last session left, when a new one starts.</summary>
    public void Clear()
    {
        ShowFrames([], null, [], null);
        Tree.Show([]);
        Console.Clear();
    }

    public void FocusEvaluate(string text)
    {
        ShowTab(console: false);
        evaluate.Text = text;
        evaluate.SelectAll();
        evaluate.Focus();
    }

    public void ShowTab(bool console)
    {
        debuggerTab.IsChecked = !console;
        consoleTab.IsChecked = console;
        debugger.IsVisible = !console;
        consolePane.IsVisible = console;
    }

    /// <summary>Rider's Add to Watches from the Evaluate field, Ctrl+Shift+Enter (⇧⌘Enter), the keys Complete Current
    /// Statement has elsewhere: true when the field has the keyboard and an expression, and took them.</summary>
    public bool TakesAction(string action)
    {
        if (action != AppActions.CompleteStatement || !evaluate.IsKeyboardFocusWithin || string.IsNullOrWhiteSpace(evaluate.Text))
        {
            return false;
        }
        EvaluateRequested?.Invoke(this, (evaluate.Text.Trim(), true));
        evaluate.Text = string.Empty;
        return true;
    }

    private void OnEvaluateKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(evaluate.Text))
        {
            return;
        }
        KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        bool watch = e.KeyModifiers.HasFlag(command) && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        EvaluateRequested?.Invoke(this, (evaluate.Text.Trim(), watch));
        if (watch)
        {
            evaluate.Text = string.Empty;
        }
        e.Handled = true;
    }

    private void OnVariablesKey(object? sender, KeyEventArgs e)
    {
        if (variables.SelectedItem is not VariableNode node)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Right when node.HasChildren && !node.IsExpanded:
            case Key.Enter when node.HasChildren:
                _ = Tree.Toggle(node);
                e.Handled = true;
                break;
            case Key.Left when node.IsExpanded:
                _ = Tree.Collapse(node);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when node.Role == VariableRole.Watch:
                WatchRemoved?.Invoke(this, node.Name);
                e.Handled = true;
                break;
            case Key.F2 when node.Expression is not null:
                node.IsEditing = true;
                e.Handled = true;
                break;
        }
    }

    /// <summary>Rider's Variables and Watches menu: Set Value, Copy Value and Copy Name, Add to Watches, Inspect (the
    /// value evaluated on its own, to open), Remove Watch for a watch, and last, as Rider adds it, Set Data Breakpoint
    /// (Remove Data Breakpoint on a member watched), greyed with why where it cannot be set.</summary>
    private IReadOnlyList<Control> VariablesMenu()
    {
        if (variables.SelectedItem is not VariableNode node)
        {
            return [];
        }
        List<Control> items =
        [
            ListMenus.Item("Set Value...", "F2", () => EditSelectedValue(), node.Expression is not null),
            ListMenus.Item("Copy Value", null, () => _ = ListMenus.CopyAsync(this, node.Value)),
            ListMenus.Item("Copy Name", null, () => _ = ListMenus.CopyAsync(this, node.Name)),
            new Separator(),
            ListMenus.Item("Add to Watches", null, () => EvaluateRequested?.Invoke(this, (node.Expression!, true)), node.Expression is not null && node.Role != VariableRole.Watch),
            ListMenus.Item("Inspect", null, () => EvaluateRequested?.Invoke(this, (node.Expression!, false)), node.Expression is not null),
        ];
        if (node.Role == VariableRole.Watch)
        {
            items.Add(new Separator());
            items.Add(ListMenus.Item("Remove Watch", "Delete", () => WatchRemoved?.Invoke(this, node.Name)));
        }
        if (DataWatch is { } offer)
        {
            MenuItem watch = ListMenus.Item(DataWatchOffer.Set, null, () => DataBreakpointRequested?.Invoke(this, node), enabled: false);
            ToolTip.SetShowOnDisabled(watch, true);
            items.Add(new Separator());
            items.Add(watch);
            _ = Offer(watch, offer(node));
            // Asked again each time the menu opens: the row may have been watched, or let go, since it was chosen.
            watch.AttachedToVisualTree += (_, _) => _ = Offer(watch, offer(node));
        }
        return items;
    }

    /// <summary>Shows on the menu's item what the debugger said can be done with the row: held until then, so a picture
    /// taken as the menu opens shows its answer.</summary>
    private static async Task Offer(MenuItem item, Task<DataWatchOffer> asked)
    {
        using IDisposable held = BackgroundWork.Scope();
        DataWatchOffer offered = await asked;
        item.Header = offered.Title;
        item.IsEnabled = offered.WhyNot is null;
        ToolTip.SetTip(item, offered.WhyNot);
    }

    /// <summary>Rider's Frames menu: Copy Stack, every frame a line, and Go to Source for the one chosen.</summary>
    private IReadOnlyList<Control> FramesMenu()
    {
        if (Frames.Count == 0)
        {
            return [];
        }
        DapStackFrame? chosen = frames.SelectedItem as DapStackFrame;
        return
        [
            ListMenus.Item("Copy Stack", null, () => _ = ListMenus.CopyAsync(this, string.Join('\n', Frames.Select(f => f.Source?.Path is { } path ? $"{f.Name} in {path}:{f.Line}" : f.Name)))),
            ListMenus.Item("Go to Source", "F4", () => FrameChosen?.Invoke(this, chosen!), chosen?.Source is not null),
        ];
    }

    private IReadOnlyList<Control> ThreadsMenu() =>
        threads.SelectedItem is DapThread thread ? [ListMenus.Item("Copy Name", null, () => _ = ListMenus.CopyAsync(this, ThreadLabel(thread)))] : [];

    /// <summary>Apply Hot Reload Changes, in Rider's flame: shown while the session has Hot Reload.</summary>
    private Button HotReloadTool()
    {
        Button button = Tool(AppActions.ApplyHotReloadChanges, "Icon.HotReload", "Apply Hot Reload Changes");
        ((PathIcon)button.Content!).Bind(PathIcon.ForegroundProperty, button.GetResourceObservable("Passenger.HotReload"));
        button.IsVisible = false;
        return button;
    }

    /// <summary>Shows Apply Hot Reload Changes while the session has Hot Reload, pressable while its program runs.</summary>
    public void ShowHotReload(bool on, bool canApply)
    {
        tools[AppActions.ApplyHotReloadChanges].IsVisible = on;
        tools[AppActions.ApplyHotReloadChanges].IsEnabled = canApply;
    }

    private Button Tool(string action, string icon, string tip)
    {
        Button button = new() { Classes = { "tool" }, Content = new PathIcon() };
        string? keys = AppActions.Keymap().ShortcutsFor(action).FirstOrDefault()?.Display();
        ToolTip.SetTip(button, keys is null ? tip : $"{tip} ({keys})");
        ((PathIcon)button.Content).Bind(PathIcon.DataProperty, button.GetResourceObservable(icon));
        button.Click += (_, _) => ActionRequested?.Invoke(this, action);
        tools[action] = button;
        return button;
    }

    private static string ThreadLabel(DapThread thread) =>
        string.IsNullOrEmpty(thread.Name) || thread.Name == "<No Name>" ? $"Thread {thread.Id}" : $"{thread.Name} ({thread.Id})";

    private static Control FrameRow(DapStackFrame? frame)
    {
        if (frame is null)
        {
            return new TextBlock();
        }
        // "Hello.dll!Program.<Main>$(string[] args)" reads, as Rider shows it, as the method with
        // its module after: the module is what tells user code from the framework.
        string name = frame.Name;
        string module = string.Empty;
        if (name.IndexOf('!', StringComparison.Ordinal) is int bang and > 0)
        {
            module = name[..bang];
            name = name[(bang + 1)..];
        }
        TextBlock where = new()
        {
            Text = frame.Source?.Name is { } file ? $"{file}:{frame.Line}" : module,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        where.Bind(TextBlock.ForegroundProperty, where.GetResourceObservable("Passenger.TextMuted"));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 24,
            Opacity = frame.PresentationHint == "subtle" ? 0.55 : 1,
            Children =
            {
                new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 },
                where,
            },
        };
    }

    private Control VariableRow(VariableNode? node)
    {
        if (node is null)
        {
            return new TextBlock();
        }
        PathIcon chevron = new() { Classes = { "chevron" }, IsVisible = node.HasChildren, RenderTransformOrigin = RelativePoint.Center };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable("Icon.ChevronRight"));
        chevron.RenderTransform = new RotateTransform(node.ChevronAngle);
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VariableNode.ChevronAngle))
            {
                chevron.RenderTransform = new RotateTransform(node.ChevronAngle);
            }
        };
        Border toggle = new() { Width = 18, Height = 24, Background = Brushes.Transparent, Child = chevron };
        toggle.PointerPressed += (_, e) =>
        {
            if (node.HasChildren)
            {
                _ = Tree.Toggle(node);
                e.Handled = true;
            }
        };
        PathIcon icon = new() { Margin = new Thickness(0, 0, 6, 0) };
        // Rider's own marks for what the debugger adds to a frame's values: a call's value, and the exception stopped at.
        string iconKey = node switch
        {
            { Role: VariableRole.Watch } => "Icon.Evaluate",
            { IsReturnValue: true } => "Icon.StepOut",
            { IsException: true } => "Icon.Error",
            _ => "Icon.Variable",
        };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(iconKey));
        TextBlock type = new() { Text = node.TypeLabel, VerticalAlignment = VerticalAlignment.Center };
        type.Bind(TextBlock.ForegroundProperty, type.GetResourceObservable("Passenger.TextMuted"));
        TextBlock value = new() { Text = node.Value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 };
        if (node.Role == VariableRole.Error)
        {
            value.Bind(TextBlock.ForegroundProperty, value.GetResourceObservable("Passenger.Editor.Error"));
        }
        ToolTip.SetTip(value, node.Value);
        TextBox editor = new() { Classes = { "search" }, MinHeight = 22, Height = 22, MinWidth = 240, Padding = new Thickness(6, 0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SetValueRequested?.Invoke(this, (node, editor.Text ?? string.Empty));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                node.IsEditing = false;
                variables.Focus();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.LostFocus += (_, _) => node.IsEditing = false;
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(VariableNode.IsEditing))
            {
                return;
            }
            editor.IsVisible = node.IsEditing;
            value.IsVisible = type.IsVisible = !node.IsEditing;
            if (node.IsEditing)
            {
                // It starts from the value as shown, all of it chosen, so typing replaces it and an arrow edits it.
                editor.Text = node.Value;
                Dispatcher.UIThread.Post(() =>
                {
                    editor.Focus();
                    editor.SelectAll();
                }, DispatcherPriority.Loaded);
            }
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 24,
            Margin = node.Indent,
            Children =
            {
                toggle,
                icon,
                new TextBlock { Text = node.Name, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = " = ", VerticalAlignment = VerticalAlignment.Center },
                type,
                value,
                editor,
            },
        };
    }
}
