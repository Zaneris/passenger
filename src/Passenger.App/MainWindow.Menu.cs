using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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

    private void AddMainMenu()
    {
        BuildMenuBar();
        MenuButton.IsVisible = !OperatingSystem.IsMacOS();
    }

    /// <summary>The menus again, with the shortcuts in force now (after Settings changed the keymap). The window keeps the
    /// one menu bar it was first given and only its items change: macOS's menu exporter refuses a second NativeMenu for
    /// the same window ("The menu being updated does not match") and ended Passenger at start.</summary>
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
        if (actions.Gesture(action.Id) is { } gesture)
        {
            actionItem.Gesture = gesture;
        }
        return actionItem;
    }

    /// <summary>The menu button's menu (Windows, Linux), the same menus as macOS's bar, built as it opens.</summary>
    private void OnMenuClicked(object? sender, RoutedEventArgs e)
    {
        ActionContext context = actions!.Context();
        MenuFlyout flyout = new();
        foreach (MenuEntry menu in MainMenu.Build(actions.All))
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
        ShowKeys(actionItem, action.Id);
        if (action.IsChecked is { } isChecked)
        {
            actionItem.ToggleType = MenuItemToggleType.CheckBox;
            actionItem.IsChecked = isChecked(context);
        }
        actionItem.Click += (_, _) => RunFromMenu(action.Id, context);
        return actionItem;
    }

    private static MenuItem FlyoutListed(ListedItem listed)
    {
        if (listed.Checked is { } chosen)
        {
            return MenuChoice(listed.Title, chosen, listed.Run);
        }
        MenuItem item = new()
        {
            Header = listed.Title,
        };
        item.Click += (_, _) => listed.Run();
        return item;
    }

    /// <summary>A menu's current value is checked and selectable; choosing it again leaves the value alone.</summary>
    private static MenuItem MenuChoice(object header, bool chosen, Action run)
    {
        MenuItem item = new() { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = chosen };
        item.Click += (_, _) =>
        {
            if (!chosen)
            {
                run();
            }
        };
        return item;
    }

    /// <summary>An action's shortcut in an item's column of keys: its one keystroke as the item's gesture, a chord as its
    /// text. The gesture is shown, not bound: the window's keymap runs the keys.</summary>
    private void ShowKeys(MenuItem item, string action)
    {
        item.InputGesture = actions!.Gesture(action);
        if (item.InputGesture is null && actions.Keymap.ShortcutsFor(action).FirstOrDefault() is { Second: not null } chord)
        {
            ShowChord(item, chord);
        }
    }

    /// <summary>A chord in an item's shortcut column, as Rider's own menus show one ("⌘E, C"): a KeyGesture holds one
    /// keystroke, so the text Fluent's item draws its gesture with is given the chord. macOS's menu bar gets none, since
    /// a key equivalent there would take the chord's first key before the window could wait for its second.</summary>
    private static void ShowChord(MenuItem item, Shortcut chord) => item.TemplateApplied += (_, e) =>
    {
        if (e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } keys)
        {
            keys.Text = chord.Display();
        }
    };

    /// <summary>A menu item chosen: its action run where its keys would have gone, in the context its menu opened in when
    /// that is known. False when it cannot run there, which the status bar says.</summary>
    public bool RunFromMenu(string action, ActionContext? context = null)
    {
        context ??= actions!.Context();
        if (context.Focused is Terminal.TerminalView terminal && actions!.Gesture(action) is { } keys
            && (!keys.KeyModifiers.HasFlag(KeyModifiers.Meta) || actions[action] is { InEditor: true }))
        {
            // The terminal takes every key but the IDE's Cmd ones (its own copy and paste included): the menu only caught
            // it on its way there.
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
