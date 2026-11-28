using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Passenger.App.Docking;

/// <summary>One place's window: its header (title, its own action's button, a tree's Expand All and Collapse All, Move
/// to and Hide in a menu, a hide button) over its content. The header drags the window elsewhere. In a narrow one, what
/// follows the title gives way, then goes whole, then the title gives way down to a few letters, then the buttons go
/// whole, Close and Hide first (Options holds both), then the window's own: nothing is cut or drawn over another
/// (<see cref="ToolbarRow"/>), and the place is never narrower than that (<see cref="LeastWidth"/>).</summary>
internal sealed class ToolPane : DockPanel
{
    private readonly DockHost host;
    private readonly TextBlock title = new() { Classes = { StyleClasses.ToolTitle }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl body = new();
    // What the window puts after its title (its tabs, the Explorer's view switch) narrows before the title does, down to
    // its own least, then goes whole.
    private readonly ContentControl tools = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0), [ShrinkingRow.GoesWholeProperty] = true };
    private readonly Border header;
    private readonly ToolbarRow headerRow;
    private readonly Button expandAll;
    private readonly Button collapseAll;

    /// <summary>The header's buttons, one apart from the next, as the main toolbar's are.</summary>
    private readonly ShrinkingRow buttons;

    /// <summary>The Options menu this pane opened, while it may be open, and whether the last press on Options came while it
    /// was: that press closed it, and its click opens nothing.</summary>
    private MenuFlyout? optionsMenu;
    private bool pressedOpen;

    /// <summary>The titled tabs along its foot of the windows docked in its place, while several are and the dock names them
    /// so (<see cref="ShowTabs"/>).</summary>
    private readonly Border tabStrip;
    private readonly FittingTabs tabRow;
    private readonly Dictionary<ToolWindow, Border> tabs = [];

    /// <summary>At the tabs' end, shown while some have no room: a menu of them.</summary>
    private readonly Button hiddenTabs;

    /// <summary>The header's ×, shown while a window closed leaves its stripe.</summary>
    private readonly Button close;

    /// <summary>The header's Dock, shown while the pane stands over the editors (<see cref="Peeking"/>): the pin lying
    /// down, as a window set to hide shows it.</summary>
    private readonly Button dock;

    /// <summary>The header's Hide, and the glyphs it shows: its own, or the pin standing up as Auto Hide
    /// (<see cref="AutoHides"/>).</summary>
    private readonly Button hide;
    private readonly PathIcon hideGlyph;
    private readonly PathIcon pinGlyph;

    /// <summary>The window's own action's button (<see cref="ToolWindow.HeaderAction"/>), made as the pane shows it.</summary>
    private Button? own;

    public ToolPane(DockHost host)
    {
        this.host = host;
        expandAll = ToolButtons.Of(AppActions.ExpandAll, IconKeys.ExpandAll, "Expand All", Ask);
        collapseAll = ToolButtons.Of(AppActions.CollapseAll, IconKeys.CollapseAll, "Collapse All", Ask);
        Button options = HeaderButton(IconKeys.More, "Options");
        hide = HeaderButton(IconKeys.Minimize, "Hide");
        hideGlyph = (PathIcon)hide.Content!;
        pinGlyph = Pin(standing: true);
        close = HeaderButton(IconKeys.Close, "Close");
        dock = HeaderButton(IconKeys.Pin, "Dock");
        dock.Content = Pin(standing: false);
        dock.IsVisible = false;
        dock.Click += (_, _) =>
        {
            if (Window is { } window)
            {
                host.Show(window.Id);
            }
        };
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
        // Read before the open menu's light dismiss closes it, which then passes the press on to Options.
        options.AddHandler(PointerPressedEvent, (_, _) => pressedOpen = optionsMenu is { IsOpen: true }, RoutingStrategies.Tunnel, handledEventsToo: true);
        options.AddHandler(PointerReleasedEvent, (_, _) => pressedOpen = false, handledEventsToo: true);
        options.Click += (_, _) =>
        {
            if (pressedOpen)
            {
                pressedOpen = false;
                return;
            }
            MenuFlyout menu = OptionsMenu();
            // Down from the ⋮ and inside the pane, where Fluent's default centres it on the button.
            menu.Placement = PlacementMode.BottomEdgeAlignedRight;
            // A click elsewhere that closes it does what it does there too: another window's Options opens its menu.
            menu.OverlayDismissEventPassThrough = true;
            host.OpenOptions = menu;
            optionsMenu = menu;
            menu.Closed += (_, _) =>
            {
                host.OpenOptions = host.OpenOptions == menu ? null : host.OpenOptions;
                optionsMenu = optionsMenu == menu ? null : optionsMenu;
            };
            menu.ShowAt(options);
        };
        hide.Click += (_, _) =>
        {
            if (Window is { } window)
            {
                host.Hide(window.Id);
            }
        };
        foreach (Button going in (Button[])[expandAll, collapseAll, dock, hide, close])
        {
            ShrinkingRow.SetGoesWhole(going, true);
        }
        // Whole while what follows it can go, then down to a few letters and its ellipsis before a button goes.
        title[ShrinkingRow.LeastProperty] = 32;
        ShrinkingRow.SetGivesWayLast(title, true);
        buttons = new ShrinkingRow
        {
            [DockProperty] = Dock.Right,
            Spacing = Gaps.Hair,
            Margin = new Thickness(Gaps.Normal, 0, Gaps.Small, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { expandAll, collapseAll, options, dock, hide, close },
        };
        headerRow = new ToolbarRow { Children = { buttons, new ShrinkingRow { VerticalAlignment = VerticalAlignment.Center, Children = { title, tools } } } };
        header = new Border
        {
            Classes = { StyleClasses.ToolHeader },
            [DockProperty] = Dock.Top,
            Child = headerRow,
        };
        header.PointerPressed += (_, e) =>
        {
            if (Window is not { } window || (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null))
            {
                return;
            }
            if (e.ClickCount == 2 && Peeking)
            {
                host.Show(window.Id);
                e.Handled = true;
            }
            else if (e.ClickCount == 2 && Floating is null)
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
        hiddenTabs = ToolButtons.Of(IconKeys.ChevronDown, "Hidden Tool Windows", ShowHiddenTabs);
        // Unlike a toolbar's button, it takes the keys: it lists windows to go to, as the tabs beside it do.
        hiddenTabs.Focusable = true;
        hiddenTabs.VerticalAlignment = VerticalAlignment.Center;
        hiddenTabs.Margin = new Thickness(0, 0, Gaps.Small, 0);
        tabRow = new FittingTabs(hiddenTabs);
        tabStrip = new Border
        {
            Classes = { StyleClasses.TabStrip, StyleClasses.ToolTabStrip },
            [DockProperty] = Dock.Bottom,
            IsVisible = false,
            Child = tabRow,
        };
        tabStrip.Styles.Add(TurnedOver(tabStrip));
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

    /// <summary>The least its place may be, where its header holds Options and its title's few letters, every other part
    /// gone whole: known once it has been laid out.</summary>
    public double LeastWidth => headerRow.LeastWidth + header.Padding.Left + header.Padding.Right;

    /// <summary>Its <see cref="LeastWidth"/> as it was last told.</summary>
    private double toldLeast;

    /// <summary>Its <see cref="LeastWidth"/> changed as it was laid out (a theme's sizes), told once that layout is done, so
    /// its place is held to it.</summary>
    public event EventHandler? LeastChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        Size measured = base.MeasureOverride(availableSize);
        if (Math.Abs(LeastWidth - toldLeast) > 0.5)
        {
            toldLeast = LeastWidth;
            UiThread.Post(() => LeastChanged?.Invoke(this, EventArgs.Empty));
        }
        return measured;
    }

    public ToolWindow? Window { get; private set; }

    /// <summary>Whether its header offers Close beside Hide: a window closed leaves its stripe until opened again.</summary>
    public bool Closes
    {
        get => close.IsVisible;
        set => close.IsVisible = value;
    }

    /// <summary>Whether it stands over the editors beside its edge (<see cref="DockHost.Peek"/>): its header then offers
    /// Dock, as its Options menu and a double click on it do, which puts the window in its place.</summary>
    public bool Peeking
    {
        get => dock.IsVisible;
        init
        {
            dock.IsVisible = value;
            hide.IsVisible = !value;
        }
    }

    /// <summary>Whether its Hide is Auto Hide, under titled tabs: the pin standing up, which sends the place's windows to
    /// the window's edge as Hide does; else Hide's own glyph.</summary>
    public bool AutoHides
    {
        get => hide.Content == pinGlyph;
        set
        {
            if (value != AutoHides)
            {
                hide.Content = value ? pinGlyph : hideGlyph;
                ActionTips.Set(hide, AppActions.HideToolWindow, value ? "Auto Hide" : "Hide");
            }
        }
    }

    /// <summary>The pin a header shows, standing up (the window docked, Auto Hide) or lying down (over the editors, Dock).</summary>
    private static PathIcon Pin(bool standing)
    {
        PathIcon glyph = new() { Classes = { StyleClasses.Small }, RenderTransform = new RotateTransform(standing ? -45 : 45) };
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(IconKeys.Pin));
        return glyph;
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
        tools.IsVisible = tools.Content is not null;
        if (own is not null)
        {
            buttons.Children.Remove(own);
            own = null;
        }
        if (window?.HeaderAction is { } asked)
        {
            own = ToolButtons.Of(asked.Action, asked.Icon, asked.Title, Ask);
            ShrinkingRow.SetGoesWhole(own, true);
            buttons.Children.Insert(0, own);
        }
        expandAll.IsVisible = window?.ExpandAll is not null;
        collapseAll.IsVisible = window?.CollapseAll is not null;
        IsVisible = window is not null;
    }

    /// <summary>Titled tabs along its foot for the windows docked in its place, in their order, the one shown joined to it;
    /// none while fewer than two dock there. A tab opens its window as a click on its stripe icon does, and drags it; each
    /// takes the keys by Tab, Enter or Space opening its window, and is a button of its name to a screen reader.</summary>
    public void ShowTabs(IReadOnlyList<ToolWindow> docked)
    {
        bool shown = docked.Count > 1;
        tabStrip.IsVisible = shown;
        tabRow.Hold(shown ? [.. docked.Select(TabOf)] : []);
        foreach ((ToolWindow window, Border tab) in tabs)
        {
            tab.Classes.Set(StyleClasses.Active, window == Window);
        }
        // The open window's tab stays in the strip, however many crowd it.
        tabRow.Kept = Window is { } open && tabs.TryGetValue(open, out Border? shownTab) ? shownTab : null;
    }

    /// <summary>The theme's tab turned over for a place's foot: the room and the corners it keeps at its top taken to its
    /// foot, so a tab that rises from the editor below it hangs here from the window above it.</summary>
    private static Style TurnedOver(Border strip)
    {
        Style tab = new(x => x.OfType<Border>().Class(StyleClasses.Tab));
        tab.Setters.Add(new Setter(MarginProperty, strip.GetResourceObservable(TokenKeys.TabMargin,
            margin => margin is Thickness given ? new Thickness(given.Left, given.Bottom, given.Right, given.Top) : margin).ToBinding()));
        tab.Setters.Add(new Setter(Border.CornerRadiusProperty, strip.GetResourceObservable(TokenKeys.TabRadius,
            radius => radius is CornerRadius given ? new CornerRadius(given.BottomLeft, given.BottomRight, given.TopRight, given.TopLeft) : radius).ToBinding()));
        return tab;
    }

    /// <summary>The windows whose tabs have no room in the strip, in its order, which its last button lists.</summary>
    public IReadOnlyList<ToolWindow> HiddenTabs =>
        [.. tabRow.Tabs.Where(tabRow.IsGone).Select(tab => tabs.First(t => t.Value == tab).Key)];

    /// <summary>The menu of the tabs out of sight, each opening its window as its tab would.</summary>
    private void ShowHiddenTabs()
    {
        ContextMenu menu = new() { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (ToolWindow window in HiddenTabs)
        {
            string id = window.Id;
            menu.Items.Add(ToolWindows.ListMenus.Item(window.Title, () => host.Show(id), icon: window.Icon));
        }
        host.HiddenTabsMenu = menu;
        menu.Closed += (_, _) => host.HiddenTabsMenu = host.HiddenTabsMenu == menu ? null : host.HiddenTabsMenu;
        menu.Open(hiddenTabs);
    }

    /// <summary>A window's tab, made the first time: its icon and its title as it is now.</summary>
    private Border TabOf(ToolWindow window)
    {
        if (tabs.TryGetValue(window, out Border? made))
        {
            return made;
        }
        RowIcon icon = new() { DataKey = window.Icon };
        TextBlock name = new() { Classes = { StyleClasses.TabName }, Text = window.Title, VerticalAlignment = VerticalAlignment.Center };
        Border tab = new() { Classes = { StyleClasses.Tab }, Tag = window.Id, Focusable = true };
        ChosenMark.Hold(tab, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, VerticalAlignment = VerticalAlignment.Center, Children = { icon, name } });
        Avalonia.Automation.AutomationProperties.SetName(tab, window.Title);
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(tab, Avalonia.Automation.Peers.AutomationControlType.Button);
        tab.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                host.Show(window.Id);
                e.Handled = true;
            }
        };
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
        // Where the window stands and how it is shown are each one value among several, the one in force ticked.
        MenuItem move = ToolWindows.ListMenus.Sub("Move to", Enum.GetValues<ToolAnchor>().Select(anchor => MenuChoice.Of(ToolWindow.Name(anchor), Window?.Anchor == anchor, () =>
        {
            if (Window is { } window)
            {
                host.Move(window, anchor);
            }
        })));
        MenuItem mode = ToolWindows.ListMenus.Sub("View Mode", new[] { (ToolMode.Docked, "Dock"), (ToolMode.Floating, "Float"), (ToolMode.Windowed, "Window"), (ToolMode.Editor, "Editor Tab") }
            .Select(each => MenuChoice.Of(each.Item2, Window?.Mode == each.Item1, () =>
            {
                if (Window is { } window)
                {
                    host.SetMode(window, each.Item1);
                }
            })));
        MenuItem maximize = ToolWindows.ListMenus.Item(host.IsMaximized ? "Restore Size" : "Maximize", () => host.ToggleMaximize(Window), Floating is null && !Peeking ? null : Refusals.Unmaximized);
        MenuItem hide = ToolWindows.ListMenus.Item("Hide", () =>
        {
            if (Window is { } window)
            {
                host.Hide(window.Id);
            }
        });
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
        if (Peeking)
        {
            menu.Items.Add(ToolWindows.ListMenus.Item("Dock", () =>
            {
                if (Window is { } window)
                {
                    host.Show(window.Id);
                }
            }));
        }
        menu.Items.Add(hide);
        if (Closes)
        {
            menu.Items.Add(ToolWindows.ListMenus.Item("Close", () =>
            {
                if (Window is { } window)
                {
                    host.Close(window.Id);
                }
            }));
        }
        return menu;
    }

    private static Button HeaderButton(string icon, string tip) => ToolButtons.Glyph(icon, tip);
}
