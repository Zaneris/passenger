using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Passenger.App.Docking;

/// <summary>One place's window: its header (title, its own action's button, a tree's Expand All and Collapse All, Move
/// to and Hide in a menu, a hide button) over its content. The header drags the window elsewhere; in a narrow one, what
/// follows the title gives way, then the title, each ending in an ellipsis, never under the buttons.</summary>
internal sealed class ToolPane : DockPanel
{
    private readonly DockHost host;
    private readonly TextBlock title = new() { Classes = { StyleClasses.ToolTitle }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl body = new();
    // What the window puts after its title (its tabs) narrows before the title does and never draws past its room, over
    // the header's buttons.
    private readonly ContentControl tools = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ClipToBounds = true };
    private readonly Border header;
    private readonly Button expandAll;
    private readonly Button collapseAll;
    private readonly StackPanel treeButtons;

    /// <summary>The titled tabs along its foot of the windows docked in its place, while several are and the dock names them
    /// so (<see cref="ShowTabs"/>).</summary>
    private readonly Border tabStrip;
    private readonly StackPanel tabRow = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer tabView;
    private readonly Dictionary<ToolWindow, Border> tabs = [];

    /// <summary>At the tabs' end, shown while some are out of sight: a menu of them.</summary>
    private readonly Button hiddenTabs;

    /// <summary>The header's ×, shown while a window closed leaves its stripe.</summary>
    private readonly Button close;

    /// <summary>The window's own action's button (<see cref="ToolWindow.HeaderAction"/>), made as the pane shows it.</summary>
    private Button? own;

    public ToolPane(DockHost host)
    {
        this.host = host;
        expandAll = ToolButtons.Of(AppActions.ExpandAll, IconKeys.ExpandAll, "Expand All", Ask);
        collapseAll = ToolButtons.Of(AppActions.CollapseAll, IconKeys.CollapseAll, "Collapse All", Ask);
        Button options = HeaderButton(IconKeys.More, "Options");
        Button hide = HeaderButton(IconKeys.Minimize, "Hide");
        close = HeaderButton(IconKeys.Close, "Close");
        close.Name = AppActions.CloseToolWindow;
        ActionTips.Set(close, AppActions.CloseToolWindow, "Close");
        close.IsVisible = false;
        close.Click += (_, _) =>
        {
            if (Window is { } window)
            {
                host.Close(window.Id);
            }
        };
        hide.Name = AppActions.HideToolWindow;
        ActionTips.Set(hide, AppActions.HideToolWindow, "Hide");
        options.Click += (_, _) =>
        {
            MenuFlyout menu = OptionsMenu();
            // Down from the ⋮ and inside the pane, where Fluent's default centres it on the button.
            menu.Placement = PlacementMode.BottomEdgeAlignedRight;
            host.OpenOptions = menu;
            menu.Closed += (_, _) => host.OpenOptions = host.OpenOptions == menu ? null : host.OpenOptions;
            menu.ShowAt(options);
        };
        hide.Click += (_, _) =>
        {
            if (Window is { } window)
            {
                host.Hide(window.Id);
            }
        };
        treeButtons = new StackPanel { [DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 8, 0), Children = { expandAll, collapseAll } };
        header = new Border
        {
            Classes = { StyleClasses.ToolHeader },
            [DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                Children =
                {
                    new StackPanel { [DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), Children = { options, hide, close } },
                    treeButtons,
                    new ShrinkingRow { Children = { title, tools } },
                },
            },
        };
        header.PointerPressed += (_, e) =>
        {
            if (Window is not { } window || (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null))
            {
                return;
            }
            if (e.ClickCount == 2 && Floating is null)
            {
                host.ToggleMaximize(window);
                e.Handled = true;
            }
            else if (Floating is { } own)
            {
                own.BeginMoveDrag(e);
            }
            else
            {
                host.Drag.Begin(window, e, header);
            }
        };
        header.PointerMoved += (_, e) => host.Drag.Continue(e);
        header.PointerReleased += (_, e) => host.Drag.End(e);
        Children.Add(header);
        tabView = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = tabRow };
        hiddenTabs = ToolButtons.Of(IconKeys.ChevronDown, "Hidden Tool Windows", ShowHiddenTabs);
        hiddenTabs.IsVisible = false;
        hiddenTabs.VerticalAlignment = VerticalAlignment.Center;
        hiddenTabs.Margin = new Thickness(0, 0, 6, 0);
        hiddenTabs[DockProperty] = Dock.Right;
        tabView.ScrollChanged += (_, _) => hiddenTabs.IsVisible = HiddenTabs.Count > 0;
        tabStrip = new Border
        {
            Classes = { StyleClasses.TabStrip, StyleClasses.ToolTabStrip },
            [DockProperty] = Dock.Bottom,
            IsVisible = false,
            Child = new DockPanel { Children = { hiddenTabs, tabView } },
        };
        Children.Add(tabStrip);
        Children.Add(new Panel { Children = { body, new ThemeBackdrop(BackgroundPlaces.ToolWindows) } });
        Classes.Add(StyleClasses.ToolPane);
        IsVisible = false;
        AddHandler(KeyDownEvent, OnKeyDown);
    }

    /// <summary>Focus Editor's keys (Escape) that nothing in the window took for its own (a search's words, a rename, a
    /// menu) give the editor the keys, the window left shown.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Window is not { } window || host.Actions is not { } actions || !AppActions.Presses(AppActions.FocusEditor, e))
        {
            return;
        }
        ActionContext context = actions.Context().In(window);
        if (actions.WhyNot(AppActions.FocusEditor, context) is null)
        {
            e.Handled = host.Request?.Invoke(AppActions.FocusEditor, context) == true;
        }
    }

    /// <summary>Styled as a DockPanel of its own class (Shell.axaml's toolPane), its look the theme's.</summary>
    protected override Type StyleKeyOverride => typeof(DockPanel);

    /// <summary>The window of its own it stands in, floating or windowed, if any: its header then moves that window.</summary>
    public Window? Floating { get; init; }

    public ToolWindow? Window { get; private set; }

    /// <summary>Whether its header offers Close beside Hide: a window closed leaves its stripe until opened again.</summary>
    public bool Closes
    {
        get => close.IsVisible;
        set => close.IsVisible = value;
    }

    public void Show(ToolWindow? window)
    {
        if (Window == window)
        {
            return;
        }
        if (Window is not null)
        {
            Window.PropertyChanged -= OnWindowChanged;
        }
        Window = window;
        body.Content = null;
        tools.Content = null;
        if (window is not null)
        {
            window.PropertyChanged += OnWindowChanged;
            // A content lives in one pane at a time, so it leaves its old one first.
            if (window.Content.Parent is ContentControl old)
            {
                old.Content = null;
            }
            body.Content = window.Content;
            title.Text = window.Title;
            if (window.HeaderTools?.Parent is ContentControl holder)
            {
                holder.Content = null;
            }
            tools.Content = window.HeaderTools;
        }
        if (own is not null)
        {
            treeButtons.Children.Remove(own);
            own = null;
        }
        if (window?.HeaderAction is { } asked)
        {
            own = ToolButtons.Of(asked.Action, asked.Icon, asked.Title, Ask);
            treeButtons.Children.Insert(0, own);
        }
        expandAll.IsVisible = window?.ExpandAll is not null;
        collapseAll.IsVisible = window?.CollapseAll is not null;
        IsVisible = window is not null;
    }

    /// <summary>Titled tabs along its foot for the windows docked in its place, in their order, the one shown joined to it;
    /// none while fewer than two dock there. A tab opens its window as a click on its stripe icon does, and drags it.</summary>
    public void ShowTabs(IReadOnlyList<ToolWindow> docked)
    {
        bool shown = docked.Count > 1;
        tabStrip.IsVisible = shown;
        if (!shown)
        {
            tabRow.Children.Clear();
            return;
        }
        Border[] wanted = [.. docked.Select(TabOf)];
        if (!tabRow.Children.SequenceEqual(wanted))
        {
            tabRow.Children.Clear();
            tabRow.Children.AddRange(wanted);
        }
        foreach ((ToolWindow window, Border tab) in tabs)
        {
            tab.Classes.Set(StyleClasses.Active, window == Window);
        }
        // The open window's tab scrolled into sight once laid out, however many crowd the strip.
        if (Window is { } open && tabs.TryGetValue(open, out Border? shownTab))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(shownTab.BringIntoView, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>The windows whose tabs are out of the strip's sight, wholly or in part, in its order.</summary>
    public IReadOnlyList<ToolWindow> HiddenTabs
    {
        get
        {
            double from = tabView.Offset.X;
            double to = from + tabView.Viewport.Width;
            return [.. tabs.Where(t => tabRow.Children.Contains(t.Value) && (t.Value.Bounds.X < from - 0.5 || t.Value.Bounds.Right > to + 0.5))
                .OrderBy(t => tabRow.Children.IndexOf(t.Value)).Select(t => t.Key)];
        }
    }

    /// <summary>The menu of the tabs out of sight, each opening its window as its tab would.</summary>
    private void ShowHiddenTabs()
    {
        ContextMenu menu = new() { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (ToolWindow window in HiddenTabs)
        {
            PathIcon glyph = new();
            glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(window.Icon));
            MenuItem item = new() { Header = window.Title, Icon = glyph };
            string id = window.Id;
            item.Click += (_, _) => host.Show(id);
            menu.Items.Add(item);
        }
        menu.Open(hiddenTabs);
    }

    /// <summary>A window's tab, made the first time: its icon and its title as it is now.</summary>
    private Border TabOf(ToolWindow window)
    {
        if (tabs.TryGetValue(window, out Border? made))
        {
            return made;
        }
        RowIcon icon = new() { Width = 14, Height = 14, DataKey = window.Icon };
        TextBlock name = new() { Classes = { StyleClasses.TabName }, Text = window.Title, VerticalAlignment = VerticalAlignment.Center };
        Border tab = new()
        {
            Classes = { StyleClasses.Tab },
            Tag = window.Id,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { icon, name } },
        };
        Avalonia.Automation.AutomationProperties.SetName(tab, window.Title);
        // Tool windows live as long as the dock, as their tabs do.
        window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolWindow.Title))
            {
                name.Text = window.Title;
            }
        };
        tab.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
            {
                return;
            }
            if (Window != window)
            {
                host.Show(window.Id);
            }
            host.Drag.Begin(window, e, tab);
            e.Handled = true;
        };
        tab.PointerMoved += (_, e) => host.Drag.Continue(e);
        tab.PointerReleased += (_, e) => host.Drag.End(e);
        tabs[window] = tab;
        return tab;
    }

    /// <summary>A header button's action, run on the window the pane shows.</summary>
    private void Ask(string action)
    {
        if (Window is { } window && host.Actions is { } actions)
        {
            host.Request?.Invoke(action, actions.Context().In(window));
        }
    }

    private void OnWindowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolWindow.Title) && Window is { } window)
        {
            title.Text = window.Title;
        }
    }

    private MenuFlyout OptionsMenu()
    {
        MenuFlyout menu = new();
        MenuItem move = new() { Header = "Move to" };
        foreach (ToolAnchor anchor in Enum.GetValues<ToolAnchor>())
        {
            MenuItem item = new() { Header = ToolWindow.Name(anchor), IsEnabled = Window?.Anchor != anchor };
            item.Click += (_, _) =>
            {
                if (Window is { } window)
                {
                    host.Move(window, anchor);
                }
            };
            move.Items.Add(item);
        }
        MenuItem mode = new() { Header = "View Mode" };
        foreach ((ToolMode each, string name) in new[] { (ToolMode.Docked, "Dock"), (ToolMode.Floating, "Float"), (ToolMode.Windowed, "Window") })
        {
            MenuItem item = new() { Header = name, IsEnabled = Window?.Mode != each };
            item.Click += (_, _) =>
            {
                if (Window is { } window)
                {
                    host.SetMode(window, each);
                }
            };
            mode.Items.Add(item);
        }
        MenuItem maximize = new() { Header = host.IsMaximized ? "Restore Size" : "Maximize", IsEnabled = Floating is null };
        maximize.Click += (_, _) => host.ToggleMaximize(Window);
        MenuItem hide = new() { Header = "Hide" };
        hide.Click += (_, _) =>
        {
            if (Window is { } window)
            {
                host.Hide(window.Id);
            }
        };
        if (Window?.Options?.Invoke() is { Count: > 0 } own)
        {
            foreach (Control item in own)
            {
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(mode);
        menu.Items.Add(move);
        menu.Items.Add(maximize);
        menu.Items.Add(new Separator());
        menu.Items.Add(hide);
        if (Closes)
        {
            MenuItem closing = new() { Header = "Close" };
            closing.Click += (_, _) =>
            {
                if (Window is { } window)
                {
                    host.Close(window.Id);
                }
            };
            menu.Items.Add(closing);
        }
        return menu;
    }

    private static Button HeaderButton(string icon, string tip)
    {
        Button button = ToolButtons.Of(icon, tip);
        ((PathIcon)button.Content!).Classes.Add(StyleClasses.Small);
        return button;
    }
}
