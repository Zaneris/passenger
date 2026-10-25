using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>What a breakpoint does, as its popup and the Breakpoints window edit it: on or off, whether it stops, its
/// condition and hit count, and a message to log; for an exception breakpoint, "Suspend If", where the exception was
/// thrown and where it is handled. Enter submits, Escape gives up. A hit count the debugger cannot read is said under its
/// field as it is typed; the debugger refuses it in the same words.</summary>
public sealed class BreakpointProperties : UserControl
{
    private readonly CheckBox enabled = new() { Content = "Enabled" };
    private readonly CheckBox suspend = new() { Content = "Suspend execution" };
    private readonly TextBox condition = Field("A C# condition: count > 2");
    private readonly TextBox hitCount = Field("Stop on hits: ==3, >=2, %2");
    private readonly TextBlock hitCountProblem = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBox log = Field("Logged on each hit: count is {count}");
    private readonly CheckBox thrownInUserCode = new() { Content = "User code" };
    private readonly CheckBox thrownInExternalCode = new() { Content = "External code" };
    private readonly CheckBox handledInUserCode = new() { Content = "User code" };
    private readonly CheckBox handledInExternalCode = new() { Content = "External code" };
    private readonly CheckBox unhandled = new() { Content = "Unhandled" };
    private readonly TextBlock inactive = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel suspendIf;

    public BreakpointProperties()
    {
        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 10 };
        AddRow(fields, 0, "Condition:", condition);
        AddRow(fields, 1, "Hit count:", hitCount);
        Grid.SetRow(hitCountProblem, 2);
        Grid.SetColumn(hitCountProblem, 1);
        fields.Children.Add(hitCountProblem);
        AddRow(fields, 3, "Log message:", log);
        hitCountProblem.Bind(TextBlock.ForegroundProperty, hitCountProblem.GetResourceObservable(TokenKeys.Error));
        inactive.Bind(TextBlock.ForegroundProperty, inactive.GetResourceObservable(TokenKeys.Error));
        hitCount.TextChanged += (_, _) => CheckHitCount();

        // The "Suspend If" panel: the exception breakpoint stops when one of each row holds.
        Grid scenarios = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 4, ColumnSpacing = 10 };
        AddRow(scenarios, 0, "Exception thrown in:", Row(thrownInUserCode, thrownInExternalCode));
        AddRow(scenarios, 1, "Exception handler:", Row(handledInUserCode, handledInExternalCode, unhandled));
        foreach (CheckBox box in Scenarios)
        {
            box.IsCheckedChanged += (_, _) => CheckScenarios();
        }
        suspendIf = new StackPanel
        {
            Spacing = 6,
            IsVisible = false,
            Children = { new TextBlock { Text = "Suspend If", FontWeight = FontWeight.SemiBold }, scenarios, inactive },
        };
        Content = new StackPanel
        {
            Spacing = 8,
            Children = { Row(enabled, suspend), fields, suspendIf },
        };
        foreach (TextBox box in new[] { condition, hitCount, log })
        {
            box.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }
    }

    public Breakpoint? Breakpoint { get; private set; }

    /// <summary>Enter in one of the fields: the changes are to stand.</summary>
    public event EventHandler? Submitted;

    /// <summary>Escape: the editing is over, the changes as they are.</summary>
    public event EventHandler? Dismissed;

    private CheckBox[] Scenarios => [thrownInUserCode, thrownInExternalCode, handledInUserCode, handledInExternalCode, unhandled];

    public void Show(Breakpoint breakpoint)
    {
        Breakpoint = breakpoint;
        enabled.IsChecked = breakpoint.Enabled;
        suspend.IsChecked = breakpoint.Suspend;
        condition.Text = breakpoint.Condition ?? string.Empty;
        // A property breakpoint's condition reads the change it stops at, and XAML Binding Errors' the failed binding.
        condition.PlaceholderText = breakpoint switch
        {
            PropertyBreakpoint => "A C# condition: newValue != oldValue",
            BindingErrorBreakpoint => $"A C# condition: {BindingStop.Name}.Path == \"Name\"",
            _ => "A C# condition: count > 2",
        };
        log.PlaceholderText = breakpoint is BindingErrorBreakpoint ? $"Logged on each hit: {{{BindingStop.Name}.Description,nq}}" : "Logged on each hit: count is {count}";
        hitCount.Text = breakpoint.HitCondition ?? string.Empty;
        log.Text = breakpoint.LogMessage ?? string.Empty;
        suspendIf.IsVisible = breakpoint is ExceptionBreakpoint;
        if (breakpoint is ExceptionBreakpoint exception)
        {
            thrownInUserCode.IsChecked = exception.Scenarios.HasFlag(ExceptionScenarios.ThrownInUserCode);
            thrownInExternalCode.IsChecked = exception.Scenarios.HasFlag(ExceptionScenarios.ThrownInExternalCode);
            handledInUserCode.IsChecked = exception.Scenarios.HasFlag(ExceptionScenarios.HandledInUserCode);
            handledInExternalCode.IsChecked = exception.Scenarios.HasFlag(ExceptionScenarios.HandledInExternalCode);
            unhandled.IsChecked = exception.Scenarios.HasFlag(ExceptionScenarios.Unhandled);
        }
        CheckScenarios();
    }

    /// <summary>Writes the fields to the breakpoint, if they change anything.</summary>
    public void Apply(BreakpointStore store)
    {
        if (Breakpoint is not { } breakpoint)
        {
            return;
        }
        bool on = enabled.IsChecked == true;
        bool stops = suspend.IsChecked == true;
        string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
        if (on != breakpoint.Enabled || stops != breakpoint.Suspend || Blank(condition.Text)?.Trim() != breakpoint.Condition
            || Blank(hitCount.Text)?.Trim() != breakpoint.HitCondition || Blank(log.Text) != breakpoint.LogMessage)
        {
            store.Update(breakpoint, on, stops, condition.Text, hitCount.Text, log.Text);
        }
        if (breakpoint is ExceptionBreakpoint exception)
        {
            store.SetScenarios(exception, Chosen());
        }
    }

    public void FocusCondition() => condition.Focus();

    /// <summary>The scenarios as their boxes are checked.</summary>
    private ExceptionScenarios Chosen() =>
        (thrownInUserCode.IsChecked == true ? ExceptionScenarios.ThrownInUserCode : 0)
        | (thrownInExternalCode.IsChecked == true ? ExceptionScenarios.ThrownInExternalCode : 0)
        | (handledInUserCode.IsChecked == true ? ExceptionScenarios.HandledInUserCode : 0)
        | (handledInExternalCode.IsChecked == true ? ExceptionScenarios.HandledInExternalCode : 0)
        | (unhandled.IsChecked == true ? ExceptionScenarios.Unhandled : 0);

    /// <summary>Says that an exception breakpoint with no box of a row checked never stops.</summary>
    private void CheckScenarios()
    {
        ExceptionScenarios chosen = Chosen();
        inactive.Text = (chosen & ExceptionScenarios.Thrown) == 0
            ? "Exception breakpoint is currently inactive because all 'Exception thrown in' options are disabled"
            : (chosen & (ExceptionScenarios.HandledInUserCode | ExceptionScenarios.HandledInExternalCode | ExceptionScenarios.Unhandled)) == 0
                ? "Exception breakpoint is currently inactive because all 'Exception handler' options are disabled"
                : null;
        inactive.IsVisible = inactive.Text is not null;
    }

    private void CheckHitCount()
    {
        string? problem = null;
        bool readable = string.IsNullOrWhiteSpace(hitCount.Text) || HitCondition.TryParse(hitCount.Text, out _, out problem);
        hitCountProblem.Text = problem;
        hitCountProblem.IsVisible = !readable;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submitted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private static TextBox Field(string watermark) => new() { Classes = { "search" }, PlaceholderText = watermark, MinWidth = 280 };

    private static StackPanel Row(params Control[] controls)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.AddRange(controls);
        return row;
    }

    private static void AddRow(Grid grid, int row, string label, Control field)
    {
        TextBlock name = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
        Grid.SetRow(name, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(name);
        grid.Children.Add(field);
    }
}

/// <summary>The popup on a breakpoint (a right-click on it, or Alt+F9): its place, its properties, and the
/// way to the Breakpoints window. The changes stand when it closes, however it closes.</summary>
public sealed class BreakpointPopup : UserControl
{
    public BreakpointPopup(LineBreakpoint breakpoint, BreakpointStore store, string? moreShortcut)
    {
        Properties.Show(breakpoint);
        Button remove = new() { Content = "Remove" };
        Button more = new() { Content = moreShortcut is null ? "More" : $"More ({moreShortcut})" };
        Button done = new() { Content = "Done", Classes = { "primary" } };
        remove.Click += (_, _) =>
        {
            store.Remove(breakpoint);
            Closed?.Invoke(this, false);
        };
        more.Click += (_, _) => Close(store, more: true);
        done.Click += (_, _) => Close(store, more: false);
        Properties.Submitted += (_, _) => Close(store, more: false);
        Properties.Dismissed += (_, _) => Close(store, more: false);
        Content = new Border
        {
            Classes = { "popup" },
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = breakpoint.Location, Classes = { "popupTitle" } },
                    Properties,
                    new DockPanel
                    {
                        Children =
                        {
                            Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { more, done } }, Dock.Right),
                            remove,
                        },
                    },
                },
            },
        };
        Width = PopupFrame.Medium;
    }

    public BreakpointProperties Properties { get; } = new();

    /// <summary>The popup is done with, and whether the Breakpoints window is to open in its place.</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>Applies the fields and closes: what a click outside it does too.</summary>
    public void Close(BreakpointStore store, bool more)
    {
        Properties.Apply(store);
        Closed?.Invoke(this, more);
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}

/// <summary>The Breakpoints window: the line breakpoints (XAML's among them) under ".NET Line Breakpoints", the session's
/// data breakpoints under "Data Breakpoints", its property breakpoints under "Avalonia Property Breakpoints", the
/// exception breakpoints under "CLR Exception Breakpoints" ("Any exception" first) and the binding errors' one under
/// "XAML Binding Errors", each turned on or off by its box, and the chosen one's properties beside the list. + adds an
/// exception breakpoint by its type, offering the exception types that match what is typed (Add CLR Exception
/// Breakpoints); − removes the chosen one. Double-click (or Go to Source) shows a line breakpoint's line.</summary>
public sealed class BreakpointsWindow : UserControl
{
    private readonly BreakpointStore store;
    private readonly Func<Task<IReadOnlyList<string>>> exceptionTypes;
    private readonly ListBox list = new() { Classes = { "tree" } };
    private readonly BreakpointProperties properties = new();
    private readonly TextBlock title = new() { Classes = { "popupTitle" } };
    private readonly Button remove = new() { Content = "Remove" };
    private readonly Button goTo = new() { Content = "Go to Source" };
    private readonly Button removeTool = ToolButtons.Of(IconKeys.Minimize, "Remove");
    private readonly StackPanel chosenPane;
    private readonly StackPanel addPane;
    private readonly TextBox typed = new() { Classes = { "search" }, PlaceholderText = "An exception type: InvalidOperationException" };
    private readonly ListBox found = new() { Classes = { "tree" }, Height = 210 };
    private IReadOnlyList<string> known = [];

    public BreakpointsWindow(BreakpointStore store, Breakpoint? chosen, Func<Task<IReadOnlyList<string>>> exceptionTypes)
    {
        this.store = store;
        this.exceptionTypes = exceptionTypes;
        Button add = ToolButtons.Of(IconKeys.Add, "Add CLR Exception Breakpoints");
        Button done = new() { Content = "Done", Classes = { "primary" } };
        add.Click += (_, _) => ShowAdd();
        removeTool.Click += (_, _) => RemoveChosen();
        remove.Click += (_, _) => RemoveChosen();
        goTo.Click += (_, _) => GoTo();
        done.Click += (_, _) => Close();
        list.SelectionChanged += (_, _) =>
        {
            properties.Apply(store);
            ShowChosen();
        };
        list.DoubleTapped += (_, _) => GoTo();
        list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        ToolWindows.ListMenus.Attach(list, () => Chosen is not { } breakpoint ? [] :
        [
            ToolWindows.ListMenus.Item("Go to Source", "F4", GoTo, enabled: breakpoint is LineBreakpoint),
            ToolWindows.ListMenus.Item(breakpoint.Enabled ? "Disable" : "Enable", null, () =>
            {
                store.Update(breakpoint, !breakpoint.Enabled, breakpoint.Suspend, breakpoint.Condition, breakpoint.HitCondition, breakpoint.LogMessage);
                Fill(breakpoint);
            }),
            ToolWindows.ListMenus.Item("Remove", "Delete", RemoveChosen, enabled: Removable(breakpoint)),
        ]);
        // Escape closes it wherever the focus is in it, as a dialog's does.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => list.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        properties.Submitted += (_, _) => properties.Apply(store);
        properties.Dismissed += (_, _) => Close();

        // Add CLR Exception Breakpoints, in the window: the types that match what is typed, Enter adds the one chosen.
        typed.TextChanged += (_, _) => ShowFound();
        typed.AddHandler(KeyDownEvent, OnTypedKeyDown, RoutingStrategies.Tunnel);
        found.DoubleTapped += (_, _) => AddChosenType();
        Button addType = new() { Content = "Add", Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel" };
        addType.Click += (_, _) => AddChosenType();
        cancel.Click += (_, _) => ShowChosen();
        addPane = new StackPanel
        {
            Spacing = 8,
            IsVisible = false,
            Children =
            {
                new TextBlock { Text = "Add CLR Exception Breakpoints", FontWeight = FontWeight.SemiBold },
                typed,
                Framed(found),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, addType } },
            },
        };
        chosenPane = new StackPanel { Spacing = 10, Children = { title, properties } };

        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("310,*"), ColumnSpacing = 14, Height = 340 };
        DockPanel listSide = new()
        {
            Children =
            {
                Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 4), Children = { add, removeTool } }, Dock.Top),
                Framed(list),
            },
        };
        Grid.SetColumn(chosenPane, 1);
        Grid.SetColumn(addPane, 1);
        body.Children.Add(listSide);
        body.Children.Add(chosenPane);
        body.Children.Add(addPane);
        Content = new Border
        {
            Classes = { "popup" },
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Breakpoints", Classes = { "popupTitle" } },
                    body,
                    new DockPanel
                    {
                        Children =
                        {
                            Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { goTo, done } }, Dock.Right),
                            remove,
                        },
                    },
                },
            },
        };
        Width = PopupFrame.Widest;
        Fill(chosen);
    }

    /// <summary>The window is done with: closed, or a breakpoint's line to show.</summary>
    public event EventHandler<LineBreakpoint?>? Closed;

    public Breakpoint? Chosen => (list.SelectedItem as ListBoxItem)?.Tag as Breakpoint;

    /// <summary>The breakpoints as listed, line breakpoints first.</summary>
    public IReadOnlyList<Breakpoint> Listed => [.. list.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<Breakpoint>()];

    /// <summary>The exception types Add CLR Exception Breakpoints offers for what is typed, while it is open.</summary>
    public IReadOnlyList<string> Offered => [.. found.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag!)];

    /// <summary>Applies the chosen breakpoint's fields and closes.</summary>
    public void Close()
    {
        properties.Apply(store);
        Closed?.Invoke(this, null);
    }

    /// <summary>Opens Add CLR Exception Breakpoints, the exception types read as it opens.</summary>
    private void ShowAdd()
    {
        properties.Apply(store);
        chosenPane.IsVisible = false;
        addPane.IsVisible = true;
        typed.Text = string.Empty;
        ShowFound();
        IdeErrors.Observe(LoadTypes());
        Avalonia.Threading.Dispatcher.UIThread.Post(() => typed.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async Task LoadTypes()
    {
        // Held until the list shows them, so a screenshot taken as it opens shows them too.
        using IDisposable held = Core.BackgroundWork.Scope();
        known = await exceptionTypes();
        if (addPane.IsVisible)
        {
            ShowFound();
        }
    }

    private void ShowFound()
    {
        found.Items.Clear();
        foreach (string type in ExceptionTypes.Matching(known, typed.Text ?? string.Empty).Take(200))
        {
            string name = type[(type.LastIndexOf('.') + 1)..];
            string space = type.Length > name.Length ? type[..(type.Length - name.Length - 1)] : string.Empty;
            found.Items.Add(new ListBoxItem
            {
                Tag = type,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = space, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            });
        }
        found.SelectedIndex = found.Items.Count > 0 ? 0 : -1;
    }

    /// <summary>Adds the type chosen in the list, or, with none there, the name as typed, and shows its properties.</summary>
    private void AddChosenType()
    {
        string? type = (found.SelectedItem as ListBoxItem)?.Tag as string ?? (typed.Text is { } text && text.Trim().Length > 0 ? text.Trim() : null);
        if (type is null)
        {
            return;
        }
        ExceptionBreakpoint added = store.AddException(type);
        addPane.IsVisible = false;
        Fill(added);
        list.Focus();
    }

    private void OnTypedKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                AddChosenType();
                e.Handled = true;
                break;
            case Key.Escape:
                ShowChosen();
                list.Focus();
                e.Handled = true;
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when ListKeys.Move(found, e):
                e.Handled = true;
                break;
        }
    }

    /// <summary>The chosen breakpoint's place and properties, or nothing with none chosen.</summary>
    private void ShowChosen()
    {
        addPane.IsVisible = false;
        chosenPane.IsVisible = true;
        if (Chosen is { } breakpoint)
        {
            title.Text = breakpoint.Location;
            properties.Show(breakpoint);
            properties.IsVisible = true;
        }
        else
        {
            title.Text = string.Empty;
            properties.IsVisible = false;
        }
        bool removable = Chosen is { } chosen && Removable(chosen);
        remove.IsEnabled = removable;
        removeTool.IsEnabled = removable;
        goTo.IsEnabled = Chosen is LineBreakpoint;
    }

    private void RemoveChosen()
    {
        if (Chosen is { } breakpoint && Removable(breakpoint))
        {
            store.Remove(breakpoint);
            Fill(null);
        }
    }

    /// <summary>Every breakpoint but the folder's own two, "Any exception" and XAML Binding Errors, turned off instead.</summary>
    private static bool Removable(Breakpoint breakpoint) => breakpoint is not (ExceptionBreakpoint { IsAny: true } or BindingErrorBreakpoint);

    private void GoTo()
    {
        if (Chosen is LineBreakpoint breakpoint)
        {
            properties.Apply(store);
            Closed?.Invoke(this, breakpoint);
        }
    }

    private void Fill(Breakpoint? chosen)
    {
        list.Items.Clear();
        List<LineBreakpoint> lines = [.. store.All.OrderBy(b => Path.GetFileName(b.Path), StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line)];
        if (lines.Count > 0)
        {
            list.Items.Add(Group(".NET Line Breakpoints"));
            foreach (LineBreakpoint breakpoint in lines)
            {
                list.Items.Add(Item(breakpoint));
            }
        }
        if (store.Data.Count > 0)
        {
            list.Items.Add(Group("Data Breakpoints"));
            foreach (DataBreakpoint breakpoint in store.Data)
            {
                list.Items.Add(Item(breakpoint));
            }
        }
        if (store.Properties.Count > 0)
        {
            list.Items.Add(Group("Avalonia Property Breakpoints"));
            foreach (PropertyBreakpoint breakpoint in store.Properties)
            {
                list.Items.Add(Item(breakpoint));
            }
        }
        list.Items.Add(Group("CLR Exception Breakpoints"));
        foreach (ExceptionBreakpoint breakpoint in store.Exceptions)
        {
            list.Items.Add(Item(breakpoint));
        }
        list.Items.Add(Group("XAML Binding Errors"));
        list.Items.Add(Item(store.BindingErrors));
        list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => chosen is not null && i.Tag == chosen) ?? list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is Breakpoint);
        ShowChosen();
    }

    /// <summary>A group's heading, one for each kind of breakpoint; it is never chosen.</summary>
    private static ListBoxItem Group(string name)
    {
        TextBlock text = new() { Text = name, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        // Disabled, so neither a click nor the arrow keys choose it, but in the list's own colour rather than greyed.
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(TokenKeys.Text));
        return new ListBoxItem { IsEnabled = false, Content = text };
    }

    private ListBoxItem Item(Breakpoint breakpoint)
    {
        CheckBox on = new() { IsChecked = breakpoint.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        on.IsCheckedChanged += (_, _) =>
        {
            if (on.IsChecked != breakpoint.Enabled)
            {
                store.Update(breakpoint, on.IsChecked == true, breakpoint.Suspend, breakpoint.Condition, breakpoint.HitCondition, breakpoint.LogMessage);
                if (Chosen == breakpoint)
                {
                    properties.Show(breakpoint);
                }
            }
        };
        string what = breakpoint.LogMessage is { } message ? $"logs {message}" : breakpoint.Condition is { } condition ? $"when {condition}" : breakpoint.Suspend ? string.Empty : "does not stop";
        return new ListBoxItem
        {
            Tag = breakpoint,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    on,
                    new TextBlock { Text = breakpoint.Location, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 },
                    new TextBlock { Text = what, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 100 },
                },
            },
        };
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            GoTo();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Chosen is not null)
        {
            RemoveChosen();
            e.Handled = true;
        }
    }

    private static Border Framed(Control content)
    {
        Border frame = new() { Classes = { "framed" }, Child = content };
        return frame;
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
