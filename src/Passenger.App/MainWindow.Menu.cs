using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>The main menu: macOS's menu bar, or the toolbar's menu button elsewhere, built from the actions' places
/// (<see cref="MainMenu"/>). Each item shows the shortcut the keymap gives its action now and is disabled where the action
/// cannot run. On macOS a menu item's shortcut reaches the menu before the window, so an item chosen runs where its keys
/// would have gone: a key the terminal takes back to the terminal, an editing action to the text box or editor with the
/// focus.</summary>
public partial class MainWindow
{
    private NativeMenu? menuBar;

    /// <summary>The menu bar's menus, as macOS shows them.</summary>
    public NativeMenu MenuBar => menuBar!;

    /// <summary>Whether the system shows the main menu in a menu bar of its own (macOS's), so the toolbar has neither
    /// the menu button nor the bar; read as a window is made.</summary>
    public static bool SystemMenuBar { get; set; } = OperatingSystem.IsMacOS();

    private void AddMainMenu()
    {
        BuildMenuBar();
        MainMenuPlace.IsVisible = !SystemMenuBar;
    }

    /// <summary>The main menu as a bar on the toolbar or behind its button, as Settings says; its titles made once, each
    /// menu built from the actions as it opens.</summary>
    private void FollowMainMenu(AppSettings next)
    {
        MainMenuPlace.ShowsBar = next.MainMenuBar && MainMenuPlace.IsVisible;
        if (MainMenuPlace.ShowsBar && MainMenuPlace.Children.Count == 1)
        {
            foreach (string menu in MainMenu.Titles)
            {
                Button title = new() { Content = menu, Tag = menu, Classes = { StyleClasses.Widget, StyleClasses.MenuBarItem } };
                title.Click += (_, _) => OnBarTitleClicked(title);
                MainMenuPlace.Children.Add(title);
            }
            AddHandler(PointerPressedEvent, NoteBarPress, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnBarKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyUpEvent, OnBarKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    /// <summary>What has the keys before the bar took them (Alt, or a press on a title), given them back as the bar lets go.</summary>
    private IInputElement? barReturn;

    /// <summary>The context the bar's keys were taken in, which a menu they open runs its items in.</summary>
    private ActionContext? barKeysContext;

    /// <summary>Whether Alt went down alone and nothing else has come since: its release takes the bar.</summary>
    private bool altAlone;

    /// <summary>The bar's titles and button a key moves among, as shown, in order.</summary>
    private List<Button> BarStops() =>
        [.. MainMenuPlace.Children.OfType<Button>().Where(b => b.Tag is string && b.IsHitTestVisible), .. MenuButton.IsHitTestVisible ? [MenuButton] : Array.Empty<Button>()];

    private bool IsBarStop(object? focused) => focused is Button button && MainMenuPlace.ShowsBar && BarStops().Contains(button);

    /// <summary>Alt tapped alone takes the bar's first title (its menus then opened by the keys), or gives the keys
    /// back when the bar has them. F10 stays Step Over, as the Visual Studio keymap has it.</summary>
    private void OnBarKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftAlt or Key.RightAlt) || !altAlone || !MainMenuPlace.ShowsBar)
        {
            return;
        }
        altAlone = false;
        e.Handled = true;
        if (barMenu is { IsOpen: true } || IsBarStop(FocusManager?.GetFocusedElement()))
        {
            LeaveBar();
            return;
        }
        barReturn = FocusManager?.GetFocusedElement();
        barKeysContext = actions!.Context();
        BarStops().FirstOrDefault()?.Focus(NavigationMethod.Tab);
    }

    /// <summary>The bar's keys: Left and Right move among its titles, or with a menu open open the next one's; Down, Enter
    /// or Space open the title's menu; Escape gives the keys back to what had them.</summary>
    private void OnBarKeyDown(object? sender, KeyEventArgs e)
    {
        altAlone = e.Key is Key.LeftAlt or Key.RightAlt && (e.KeyModifiers & ~KeyModifiers.Alt) == KeyModifiers.None;
        if (!MainMenuPlace.ShowsBar || e.Handled && barMenu is not { IsOpen: true })
        {
            return;
        }
        object? focused = FocusManager?.GetFocusedElement();
        if (barMenu is { IsOpen: true } && barMenuFrom is { } from)
        {
            // Heard by the window and the item both where the menu stands in the window's own tree: once.
            if (e.Handled || focused is not MenuItem item)
            {
                return;
            }
            bool atTop = item.Parent is not MenuItem;
            if (e.Key == Key.Left && atTop || e.Key == Key.Right && item.ItemCount == 0)
            {
                e.Handled = true;
                Button next = Beside(from, e.Key == Key.Left ? -1 : 1);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OpenByKeys(next));
            }
            else if (e.Key == Key.Escape && atTop)
            {
                e.Handled = true;
                LeaveBar();
            }
            return;
        }
        if (!IsBarStop(focused))
        {
            return;
        }
        Button title = (Button)focused!;
        switch (e.Key)
        {
            case Key.Left or Key.Right:
                Beside(title, e.Key == Key.Left ? -1 : 1).Focus(NavigationMethod.Directional);
                e.Handled = true;
                break;
            case Key.Down or Key.Enter or Key.Space:
                OpenByKeys(title);
                e.Handled = true;
                break;
            case Key.Escape:
                LeaveBar();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The title (or button) a step to one side of another, round from one end to the other.</summary>
    private Button Beside(Button title, int step)
    {
        List<Button> stops = BarStops();
        int at = stops.IndexOf(title);
        return at < 0 ? title : stops[(at + step + stops.Count) % stops.Count];
    }

    /// <summary>A title's menu (the button's, those it holds) opened from the keys, its first item taking them.</summary>
    private void OpenByKeys(Button title)
    {
        if (title == MenuButton)
        {
            barMenu?.Hide();
            OnMenuClicked(MenuButton, new RoutedEventArgs());
            return;
        }
        OpenBarMenu(title);
        if (barMenu is { } flyout)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                flyout.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsEffectivelyEnabled)?.Focus(NavigationMethod.Directional));
        }
    }

    /// <summary>The bar lets go: its menu closed and the keys back with what had them before (the editor when nothing did).</summary>
    private void LeaveBar()
    {
        IInputElement? back = barReturn;
        (barReturn, barKeysContext) = (null, null);
        barMenu?.Hide();
        if (back is Visual visual && TopLevel.GetTopLevel(visual) is not null && back.Focus())
        {
            return;
        }
        if (Editors.Active is not null)
        {
            Editors.CodeEditor.Focus();
        }
    }

    /// <summary>A title clicked opens its menu, or closes it when that menu was open as the press came: the press closed
    /// it, passing through to the title, which would open it again.</summary>
    private void OnBarTitleClicked(Button title)
    {
        if (barPressedOpen == title)
        {
            barPressedOpen = null;
            return;
        }
        OpenBarMenu(title);
    }

    /// <summary>Which open menu's title a press came on, before the menu's light dismiss closes it, and what had the keys
    /// before a press on a title took them.</summary>
    private void NoteBarPress(object? sender, PointerPressedEventArgs e)
    {
        altAlone = false;
        Button? title = BarTitleAt(this, e);
        barPressedOpen = barMenu is { IsOpen: true } && title is not null && title == barMenuFrom ? title : null;
        if (title is not null && FocusManager?.GetFocusedElement() is { } focused && !IsBarStop(focused) && focused is not MenuItem)
        {
            barReturn = focused;
        }
    }

    /// <summary>The bar's title shown under the pointer, an event of the window's or of a menu's own popup turned into the
    /// window's place through the screen.</summary>
    private Button? BarTitleAt(Visual root, PointerEventArgs e)
    {
        Point at = this.PointToClient(root.PointToScreen(e.GetPosition(root)));
        return MainMenuPlace.Children.OfType<Button>().FirstOrDefault(t => t.Tag is string && t.IsHitTestVisible
            && t.TranslatePoint(default, this) is { } corner && new Rect(corner, t.Bounds.Size).Contains(at));
    }

    /// <summary>While one of the bar's menus is open, the pointer moving over another title opens that one. The open
    /// menu holds the pointer, so its moves are followed from the menu's own popup as well as the window.</summary>
    private void FollowBarPointer(object? sender, PointerEventArgs e)
    {
        if (barMenu is not { IsOpen: true } || sender is not Visual root)
        {
            return;
        }
        Button? over = BarTitleAt(root, e);
        if (over is not null && over != barMenuFrom)
        {
            // Not inside the event of the popup the switch closes.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (barMenu is { IsOpen: true } && barMenuFrom != over)
                {
                    OpenBarMenu(over);
                }
            });
        }
    }

    private MenuFlyout? barMenu;
    private Button? barMenuFrom;
    private ActionContext? barMenuContext;
    private Button? barPressedOpen;

    /// <summary>One of the bar's menus opened under its title, built from the actions as it opens, in the context the first
    /// of them opened in (the focus is the menu's once one is open).</summary>
    private void OpenBarMenu(Button title)
    {
        ActionContext context = barMenu is { IsOpen: true } && barMenuContext is { } kept ? kept : barKeysContext ?? actions!.Context();
        barMenu?.Hide();
        MenuEntry entry = MainMenu.Build(actions!.All).First(m => m.Title == (string)title.Tag!);
        MenuFlyout flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, OverlayDismissEventPassThrough = true };
        if (FlyoutEntry(entry, context) is MenuItem built)
        {
            foreach (object? item in built.Items.ToList())
            {
                built.Items.Remove(item);
                flyout.Items.Add(item);
            }
        }
        (barMenu, barMenuFrom, barMenuContext) = (flyout, title, context);
        title.Classes.Add(StyleClasses.Open);
        Action release = KeepKeys(title, () => flyout.IsOpen ? flyout.Items.OfType<Control>().FirstOrDefault()?.FindLogicalAncestorOfType<MenuBase>() : null);
        AddHandler(PointerMovedEvent, FollowBarPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        flyout.ShowAt(title);
        // A popup of its own where the platform gives one; headless, the menu stands in the window's own tree.
        TopLevel? popup = flyout.Items.OfType<Control>().FirstOrDefault() is { } first ? TopLevel.GetTopLevel(first) : null;
        if (popup is not null && popup != this)
        {
            popup.AddHandler(PointerMovedEvent, FollowBarPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        // Its items' keys, which reach them in a popup of its own (the window's tunnel never sees them there).
        foreach (MenuItem item in flyout.Items.OfType<MenuItem>())
        {
            item.AddHandler(KeyDownEvent, OnBarKeyDown, RoutingStrategies.Tunnel);
        }
        flyout.Closed += (_, _) =>
        {
            release();
            title.Classes.Remove(StyleClasses.Open);
            popup?.RemoveHandler(PointerMovedEvent, FollowBarPointer);
            if (barMenu == flyout)
            {
                RemoveHandler(PointerMovedEvent, FollowBarPointer);
                (barMenu, barMenuFrom, barMenuContext) = (null, null, null);
                // Closed with an item chosen or by a click elsewhere: the keys go back, unless what ran took them.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (barMenu is null && FocusManager?.GetFocusedElement() is var focused && (focused is null or MenuItem || IsBarStop(focused)))
                    {
                        LeaveBar();
                    }
                });
            }
        };
    }

    /// <summary>The menus again, with the shortcuts in force now (after Settings changed the keymap). The window keeps
    /// the one menu bar it was first given and only its items change, since macOS's menu exporter refuses a second
    /// NativeMenu for the same window.</summary>
    private void BuildMenuBar()
    {
        bool first = menuBar is null;
        menuBar ??= new NativeMenu();
        menuBar.Items.Clear();
        ActionContext context = actions!.Context();
        foreach (MenuEntry menu in MainMenu.Build(actions.All))
        {
            menuBar.Items.Add(NativeEntry(menu, context));
        }
        if (first)
        {
            NativeMenu.SetMenu(this, menuBar);
        }
    }

    private NativeMenuItemBase NativeEntry(MenuEntry entry, ActionContext context)
    {
        if (entry.IsSeparator)
        {
            return new NativeMenuItemSeparator();
        }
        if (entry.IsListed)
        {
            NativeMenu listed = new();
            listed.NeedsUpdate += (_, _) =>
            {
                listed.Items.Clear();
                foreach (ListedItem item in Listed(entry.Title!))
                {
                    listed.Items.Add(NativeListed(item));
                }
                if (listed.Items.Count == 0)
                {
                    listed.Items.Add(new NativeMenuItem("Nothing yet") { IsEnabled = false });
                }
            };
            return new NativeMenuItem(entry.Title!) { Menu = listed };
        }
        if (entry.Items is { } items)
        {
            NativeMenu menu = new();
            List<InlineList> inline = [];
            foreach (MenuEntry item in items)
            {
                if (item is { IsListed: true, Inline: true })
                {
                    inline.Add(new InlineList(item.Title!, menu.Items.Count));
                    continue;
                }
                menu.Items.Add(NativeEntry(item, context));
            }
            FillInline(menu, inline);
            menu.NeedsUpdate += (_, _) =>
            {
                ActionContext now = actions!.Context();
                foreach (NativeMenuItem item in menu.Items.OfType<NativeMenuItem>().Where(i => i.CommandParameter is string))
                {
                    IdeAction listed = actions[(string)item.CommandParameter!]!;
                    item.IsEnabled = listed.CanRun(now);
                    item.Header = listed.TitleWhere(now);
                    item.IsChecked = listed.IsChecked?.Invoke(now) == true;
                }
                FillInline(menu, inline);
            };
            return new NativeMenuItem(entry.Title!) { Menu = menu };
        }
        IdeAction action = actions![entry.Action!]!;
        NativeMenuItem actionItem = new(action.TitleWhere(context))
        {
            Command = new MenuCommand(() => RunFromMenu(action.Id)),
            CommandParameter = action.Id,
            IsEnabled = action.CanRun(context),
        };
        if (action.IsChecked is { } isChecked)
        {
            actionItem.ToggleType = MenuItemToggleType.CheckBox;
            actionItem.IsChecked = isChecked(context);
        }
        if (actions.MenuBarGesture(action.Id) is { } gesture)
        {
            actionItem.Gesture = gesture;
        }
        return actionItem;
    }

    /// <summary>The menu button's menu (Windows, Linux), the same menus as macOS's bar, built as it opens: those the bar
    /// has no room for while it shows.</summary>
    private void OnMenuClicked(object? sender, RoutedEventArgs e)
    {
        ActionContext context = actions!.Context();
        MenuFlyout flyout = new();
        foreach (MenuEntry menu in MainMenu.Build(actions.All).Skip(MainMenuPlace.ShowsBar ? MainMenuPlace.MenusShown : 0))
        {
            flyout.Items.Add(FlyoutEntry(menu, context));
        }
        Action release = KeepKeys(MenuButton, () => flyout.IsOpen ? flyout.Items.OfType<Control>().FirstOrDefault()?.FindLogicalAncestorOfType<MenuBase>() : null);
        flyout.Closed += (_, _) => release();
        flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(MenuButton);
    }

    /// <summary>What a listed submenu holds as it opens: every tool window the dock has, the recent solutions, or the
    /// solution windows open.</summary>
    private IReadOnlyList<ListedItem> Listed(string submenu) => submenu switch
    {
        MainMenu.ToolWindows => [.. Dock.Windows.OrderBy(w => w.Title, StringComparer.Ordinal).Select(w => new ListedItem(w.Title, () => ShowToolWindow(w.Id)))],
        MainMenu.RecentSolutions => [.. RecentMenu().Select(r => new ListedItem(r.Title, r.Open))],
        MainMenu.OpenSolutionWindows => SolutionWindows(),
        _ => throw new InvalidOperationException($"No window lists a submenu called {submenu}"),
    };

    private static NativeMenuItem NativeListed(ListedItem item) => new(item.Title)
    {
        Command = new MenuCommand(item.Run),
        ToggleType = item.Checked is null ? MenuItemToggleType.None : MenuItemToggleType.Radio,
        IsChecked = item.Checked == true,
    };

    /// <summary>The lists that stand in a native menu's own place, filled again as it opens: what each held taken out, and
    /// what it holds now put in after a separator, those after it moving along.</summary>
    private void FillInline(NativeMenu menu, List<InlineList> lists)
    {
        int moved = 0;
        foreach (InlineList list in lists)
        {
            list.Start += moved;
            for (int i = 0; i < list.Count; i++)
            {
                menu.Items.RemoveAt(list.Start);
            }
            IReadOnlyList<ListedItem> items = Listed(list.Title);
            List<NativeMenuItemBase> now = items.Count == 0 ? [] : [new NativeMenuItemSeparator(), .. items.Select(NativeListed)];
            for (int i = 0; i < now.Count; i++)
            {
                menu.Items.Insert(list.Start + i, now[i]);
            }
            moved += now.Count - list.Count;
            list.Count = now.Count;
        }
    }

    /// <summary>A menu item a list gives: its title, what choosing it does, and whether it is the one checked (null for a
    /// list that checks none).</summary>
    private sealed record ListedItem(string Title, Action Run, bool? Checked = null);

    /// <summary>Where an inline list's items stand in their native menu, and how many there are.</summary>
    private sealed class InlineList(string title, int start)
    {
        public string Title { get; } = title;

        public int Start { get; set; } = start;

        public int Count { get; set; }
    }

    /// <summary>An entry of a menu in the window (the menu button's, a context menu): its action's title and one-key
    /// shortcut, or its chord where it has none, disabled where it cannot run in the context the menu opened in, and run
    /// there when chosen.</summary>
    private Control FlyoutEntry(MenuEntry entry, ActionContext context)
    {
        if (entry.IsSeparator)
        {
            return new Separator();
        }
        if (entry.IsListed)
        {
            MenuItem listed = new() { Header = entry.Title };
            foreach (ListedItem item in Listed(entry.Title!))
            {
                listed.Items.Add(FlyoutListed(item));
            }
            listed.IsEnabled = listed.Items.Count > 0;
            return listed;
        }
        if (entry.Items is { } items)
        {
            MenuItem menu = new() { Header = entry.Title };
            foreach (MenuEntry item in items)
            {
                if (item is { IsListed: true, Inline: true })
                {
                    IReadOnlyList<ListedItem> listed = Listed(item.Title!);
                    if (listed.Count > 0)
                    {
                        menu.Items.Add(new Separator());
                        foreach (ListedItem each in listed)
                        {
                            menu.Items.Add(FlyoutListed(each));
                        }
                    }
                    continue;
                }
                menu.Items.Add(FlyoutEntry(item, context));
            }
            return menu;
        }
        IdeAction action = actions![entry.Action!]!;
        MenuItem actionItem = new() { Header = action.TitleWhere(context), IsEnabled = action.CanRun(context) };
        if (action.WhyNot(context) is { } why)
        {
            // Greyed, it says why on hover, as running it would (a library's Publish..., a project's tests).
            ToolTip.SetTip(actionItem, why);
        }
        actions.ShowKeys(actionItem, action.Id);
        if (action.IsChecked is { } isChecked)
        {
            actionItem.ToggleType = MenuItemToggleType.CheckBox;
            actionItem.IsChecked = isChecked(context);
        }
        actionItem.Click += (_, _) => RunFromMenu(action.Id, context);
        return actionItem;
    }

    /// <summary>An item of a tool window's row menu (the Containers and Aspire windows'), its action's as the window's menus
    /// show it: its title there and keys, greyed with why while it cannot run, worked out each time the menu opens, and run
    /// there when chosen.</summary>
    private MenuItem RowMenuItem(string id)
    {
        IdeAction action = actions![id]!;
        MenuItem item = new() { Header = action.Title };
        actions.ShowKeys(item, action.Id);
        item.AttachedToVisualTree += (_, _) =>
        {
            ActionContext context = actions.Context();
            item.Header = action.TitleWhere(context);
            string? why = actions.WhyNot(action.Id, context);
            item.IsEnabled = why is null;
            ToolTip.SetTip(item, why);
        };
        item.Click += (_, _) => Request(action.Id);
        return item;
    }

    private static MenuItem FlyoutListed(ListedItem listed)
    {
        MenuItem item = new()
        {
            Header = listed.Title,
            ToggleType = listed.Checked is null ? MenuItemToggleType.None : MenuItemToggleType.Radio,
            IsChecked = listed.Checked == true,
        };
        item.Click += (_, _) => listed.Run();
        return item;
    }

    /// <summary>A menu item chosen: its action run where its keys would have gone, in the context its menu opened in when
    /// that is known. False when it cannot run there, which the status bar says.</summary>
    public bool RunFromMenu(string action, ActionContext? context = null)
    {
        context ??= actions!.Context();
        if (context.Focused is Terminal.TerminalView terminal && actions!.MenuBarGesture(action) is { } keys
            && (!keys.KeyModifiers.HasFlag(KeyModifiers.Meta) || actions[action] is { InEditor: true }))
        {
            // The terminal takes every key but the IDE's Cmd ones; the menu only caught it on its way there.
            terminal.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = keys.Key, KeyModifiers = keys.KeyModifiers, Source = terminal });
            return true;
        }
        return Request(action, context);
    }

    /// <summary>An editing action on a text box (a search field, a dialog's), as a native Edit menu sends it there.</summary>
    private static bool Edit(TextBox box, string action)
    {
        switch (action)
        {
            case EditorActions.Undo:
                box.Undo();
                return true;
            case EditorActions.Redo:
                box.Redo();
                return true;
            case EditorActions.Cut:
                box.Cut();
                return true;
            case EditorActions.Copy:
                box.Copy();
                return true;
            case EditorActions.Paste:
                box.Paste();
                return true;
            case EditorActions.SelectAll:
                box.SelectAll();
                return true;
            default:
                return false;
        }
    }

    /// <summary>A menu item's command: runs its action; whether it can is the item's IsEnabled, set as its menu opens.</summary>
    private sealed class MenuCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }
}
