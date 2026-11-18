using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The main menu: its menus from the action catalog, each item with the shortcut the keymap gives it now and
/// disabled where it cannot run; an item chosen runs its action, an editing one where the focus is. The toolbar's menus,
/// opened by a click, take the keys. A submenu opens beside its item, never over it.</summary>
public sealed class MenuTests : IDisposable
{
    private readonly string folder = TestFolders.Create("menu-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Settings_applied_again_keep_the_menu_bar_macOS_was_given_with_each_menu_once()
    {
        MainWindow window = Ui.OpenFolder(folder);
        NativeMenu given = NativeMenu.GetMenu(window)!;
        int menus = given.Items.Count;

        // As a start and Settings' OK apply them.
        window.ApplySettings(window.Settings);
        window.ApplySettings(window.Settings);
        Ui.Settle();

        Assert.True(ReferenceEquals(given, NativeMenu.GetMenu(window)),
            "The window was given a second menu bar, which macOS's menu exporter refuses, ending Passenger at start");
        Assert.Equal(menus, given.Items.Count);
        Assert.Equal(given.Items.OfType<NativeMenuItem>().Select(i => i.Header).Distinct().Count(), given.Items.OfType<NativeMenuItem>().Count());
        window.Close();
    }

    /// <summary>macOS's application menu is the one the application holds as its setup ends, where the platform takes it
    /// before any window (with none, macOS shows Avalonia's own About): About Passenger, Check for Updates… and Settings…
    /// on ⌘,, each running its action in the window in front, a second window adding none of them again.</summary>
    [AvaloniaFact]
    public void The_application_menu_macOS_takes_at_start_holds_About_Passenger_Check_for_Updates_and_Settings_each_run_in_the_window_in_front()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The application menu is macOS's");
        NativeMenu taken = TestApp.ApplicationMenuAtSetup
            ?? throw new Xunit.Sdk.XunitException("The application had no menu as its setup ended, where macOS takes it, so macOS's menu bar shows Avalonia's About and none of Passenger's");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.True(ReferenceEquals(taken, MainWindow.ApplicationMenu),
            "The window gave the application another menu than the one macOS took as Passenger started, which macOS never shows");
        string[] Shown() => [.. taken.Items.Select(i => (i as NativeMenuItem)?.Header ?? "-")];
        Assert.Equal(["About Passenger", "Check for Updates…", "-", "Settings…"], Shown());
        NativeMenuItem Item(string title) => taken.Items.OfType<NativeMenuItem>().Single(i => i.Header == title);
        Assert.True(new KeyGesture(Key.OemComma, KeyModifiers.Meta).Equals(Item("Settings…").Gesture),
            $"Settings… shows {Item("Settings…").Gesture?.ToString() ?? "no keys"} in the application menu, not ⌘,");

        Item("About Passenger").Command!.Execute(null);
        Ui.Settle();
        Assert.IsType<AboutDialog>(window.Frontmost).Close();
        Ui.Settle();
        Item("Settings…").Command!.Execute(null);
        Ui.Settle();
        Assert.IsType<Passenger.App.Settings.SettingsDialog>(window.Frontmost).Close();
        Ui.Settle();
        Item("Check for Updates…").Command!.Execute(null);
        Ui.Settle();
        // The run's release feed is a closed port, so the check that ran says it could not ask.
        Assert.StartsWith("Could not check for updates: ", window.Status);

        MainWindow second = PassengerApp.NewWindow(window);
        Ui.Settle();
        Assert.Equal(["About Passenger", "Check for Updates…", "-", "Settings…"], Shown());
        Item("About Passenger").Command!.Execute(null);
        Ui.Settle();
        Assert.True(second.Frontmost is AboutDialog && window.Frontmost is not AboutDialog,
            "About Passenger chosen from the application menu did not open over the window in front, the newer one");
        second.Frontmost.Close();
        second.Close();
        window.Close();
    }

    /// <summary>An editor tab's menu offers what the Explorer's row offers for its file: Copy Path, and Open In's file
    /// manager and terminal, an HTML page's browser too.</summary>
    [AvaloniaFact]
    public void An_editor_tabs_menu_copies_its_files_path_and_opens_it_in_the_file_manager_or_a_terminal()
    {
        string one = Path.Combine(folder, "One.cs");
        File.WriteAllText(one, "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(one);
        Ui.Settle();
        Control tab = window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == "One.cs" && t.Classes.Contains(StyleClasses.TabName)).FindAncestorOfType<Border>()!;
        Ui.ChooseFromMenu(tab, "Copy Path");
        Ui.WaitFor(() => Ui.Clipboard(window) == window.EditorArea.Active!.Path, "the tab's file's path on the clipboard");
        tab.RaiseEvent(new ContextRequestedEventArgs());
        Ui.Settle();
        MenuItem openIn = tab.ContextMenu!.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Header == "Open In")
            ?? throw new Xunit.Sdk.XunitException("The tab's menu has no Open In");
        Assert.Equal([window.Actions[AppActions.RevealInFileManager]!.Title, "Open in Terminal"], openIn.Items.OfType<MenuItem>().Select(i => i.Header as string));
        Assert.True(openIn.Items.OfType<MenuItem>().All(i => i.IsEnabled), "Open In's items are greyed on a file shown in a tab");
        tab.ContextMenu.Close();

        string page = Path.Combine(folder, "page.html");
        File.WriteAllText(page, "<p>Hi</p>\n");
        window.OpenFile(page);
        Ui.Settle();
        Control pageTab = window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == "page.html" && t.Classes.Contains(StyleClasses.TabName)).FindAncestorOfType<Border>()!;
        Ui.ChooseFromMenu(pageTab, "Open In", "Open in Browser");
        Assert.Contains(window.Browsed, uri => uri.IsFile && uri.LocalPath.EndsWith("page.html", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void The_menus_show_each_actions_shortcut_disable_what_cannot_run_and_run_what_is_chosen()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("One.cs");
        Ui.Settle();
        NativeMenu bar = window.MenuBar;
        Assert.Equal(["File", "Edit", "View", "Navigate", "Code", "Refactor", "Build", "Run", "Tests", "Tools", "Git", "Window", "Help"],
            bar.Items.OfType<NativeMenuItem>().Select(i => i.Header));

        NativeMenuItem saveAll = Item(Open(bar, "File"), "Save All");
        Shortcut keys = AppActions.Keymap().ShortcutsFor(AppActions.SaveAll)[0];
        Assert.Equal(new KeyGesture(keys.First.Key, keys.First.Modifiers), saveAll.Gesture);

        // Nothing is being debugged: stepping cannot run; a toggled breakpoint can.
        NativeMenu run = Open(bar, "Run");
        Assert.False(Item(run, "Step Over").IsEnabled);
        Assert.True(Item(run, "Toggle Breakpoint").IsEnabled);

        Item(Open(Open(bar, "Window"), "Editor Tabs"), "Split Right").Command!.Execute(null);
        Ui.Settle();
        Assert.Equal(2, window.EditorArea.Groups.Count);
        NativeMenu toolWindows = Open(Open(bar, "View"), MainMenu.ToolWindows);
        Item(toolWindows, "Problems").Command!.Execute(null);
        Ui.Settle();
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems));

        window.EditorArea.CodeEditor.Focus();
        Item(Open(bar, "Edit"), "Select All").Command!.Execute(null);
        Assert.Equal("class One { }\n".Length, window.EditorArea.CodeEditor.Selections.Primary.Length);
        Ui.Run(window, AppActions.GoToFile);
        TextBox query = Assert.IsType<TextBox>(window.FocusManager?.GetFocusedElement());
        query.Text = "One";
        query.CaretIndex = 3;
        Item(Open(bar, "Edit"), "Select All").Command!.Execute(null);
        Assert.Equal(3, query.SelectionEnd - query.SelectionStart);
        window.Close();
    }

    /// <summary>A menu in the window shows an action whose keys are all chords with its first chord ("⌘E, C" for Code
    /// Cleanup), where a gesture could hold only one keystroke.</summary>
    [AvaloniaFact]
    public void The_editors_menu_shows_a_chord_for_an_action_whose_keys_are_all_chords()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("One.cs");
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.ShowContextMenu);
        Ui.WaitFor(() => window.OpenContextMenu is not null, "the editor's menu");
        Ui.Settle();
        Shortcut chord = AppActions.Keymap().ShortcutsFor(AppActions.CodeCleanup)[0];
        Assert.NotNull(chord.Second);
        MenuItem cleanup = window.OpenContextMenu!.Items.OfType<MenuItem>().First(i => i.Header as string == "Code Cleanup");
        string? shown = cleanup.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_InputGestureText")?.Text;
        Assert.True(shown == chord.Display(), $"Code Cleanup's item shows '{shown}' for its keys, not {chord.Display()}");
        window.OpenContextMenu.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void A_toolbar_menu_opened_by_a_click_takes_the_keys_so_escape_closes_it_and_the_arrows_and_enter_choose()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.SetConfigurations([new RunConfiguration { Name = "First" }, new RunConfiguration { Name = "Second" }], "First");
        Ui.Settle();

        // The main menu's button is there where the system has no menu bar of its own (not on macOS, where its place is hidden).
        string[] widgets = window.FindControl<Button>("MenuButton")!.IsEffectivelyVisible
            ? ["MenuButton", "ProjectButton", "BranchButton", "RunConfigButton"]
            : ["ProjectButton", "BranchButton", "RunConfigButton"];
        foreach (string widget in widgets)
        {
            ClickOpen(window, widget);
            Ui.Press(window, "ESCAPE");
            Assert.False(MenuOpen(window), $"Escape left the menu a click on {widget} opened still open: it never took the keys");

            // A dialog over the window (an agent's question) answered gives the window back the focus it had before the menu.
            ClickOpen(window, widget);
            ConfirmDialog question = new("Allow app_key?", "An agent wants to press keys in Passenger.", "Allow", "Deny");
            _ = question.ShowDialog<bool>(window);
            Ui.Settle();
            question.Close(true);
            Ui.Settle();
            Ui.Press(window, "ESCAPE");
            Assert.False(MenuOpen(window), $"Once a dialog over the window was answered, Escape left the menu a click on {widget} opened still open: the keys went back under it");
        }

        ClickOpen(window, "RunConfigButton");
        Ui.Press(window, "DOWN");
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Assert.False(MenuOpen(window), "Enter on a run configuration left its menu open");
        Assert.Equal("Second", window.Configuration?.Name);
        ClickOpen(window, "RunConfigButton");
        Ui.Press(window, "UP");
        Ui.Press(window, "UP");
        Ui.Press(window, "UP");
        Ui.Press(window, "ENTER");
        Assert.Equal("First", window.Configuration?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void The_main_menu_as_a_bar_shows_the_menus_the_toolbar_has_room_for_and_the_rest_behind_its_button()
    {
        // The menu on the toolbar, as on Windows and Linux, rather than in macOS's own menu bar.
        MainWindow.SystemMenuBar = false;
        MainWindow window = Ui.OpenFolder(folder, width: 1800);
        try
        {
            window.ApplySettings(window.Settings with { MainMenuBar = true });
            Ui.Settle();
            MainMenuPlace place = window.FindControl<MainMenuPlace>("MainMenuPlace")!;
            Button button = window.FindControl<Button>("MenuButton")!;
            Button[] titles = [.. place.Children.OfType<Button>().Where(b => b != button)];
            Assert.Equal(["File", "Edit", "View"], titles.Take(3).Select(t => t.Content as string));

            Assert.True(place.MenusShown == titles.Length, $"At 1800 wide the bar shows {place.MenusShown} of its {titles.Length} menus");
            Assert.True(button.Opacity == 0 && !button.IsHitTestVisible, "The main menu's button still shows beside a bar that holds every menu");
            Color words = window.TryFindResource(TokenKeys.TitleBar_Text, window.ActualThemeVariant, out object? token) && token is ISolidColorBrush own
                ? own.Color : throw new Xunit.Sdk.XunitException("The theme has no title bar words");
            Assert.All(titles, t => Assert.True(t.Foreground is ISolidColorBrush ink && ink.Color == words, $"The bar's {t.Content} is not in the title bar's own words"));

            Ui.Click(titles[0]);
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Open Solution…"), "the File menu");
            Ui.Click(titles[1]);
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Undo"), "the Edit menu in the File menu's place");
            Assert.False(OpenMenuTitles(window).Contains("Open Solution…"), "The File menu stayed open under the Edit menu");
            Hover(window, titles[2]);
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Recent Locations"), "the View menu, opened by the pointer moving over its title");
            Hover(window, titles[0]);
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Open Solution…"), "the File menu again, opened by the pointer moving back over its title");
            Ui.Press(window, "ESCAPE");
            Assert.False(MenuOpen(window), "Escape left the bar's File menu open: it never took the keys");
            Ui.Click(titles[1]);
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Undo"), "the Edit menu");
            Ui.Click(titles[1]);
            Assert.False(MenuOpen(window), "A click on the open Edit menu's title left it open");
            Ui.Click(titles[1]);
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Undo"), "the Edit menu, opened again by the next click");
            Ui.Press(window, "ESCAPE");

            window.Width = 1200;
            Ui.Settle();
            int shown = place.MenusShown;
            Assert.True(shown >= 2 && shown < titles.Length, $"At 1200 wide the bar shows {shown} of its {titles.Length} menus, where it has room for some but not all");
            Assert.True(button.Opacity == 1 && button.Bounds.Left >= titles[shown - 1].Bounds.Right, "The menus the bar has no room for are not behind a button after it");
            Assert.True(titles.Skip(shown).All(t => t.Opacity == 0 && !t.IsHitTestVisible), "A menu the bar has no room for still shows on it");
            ClickOpen(window, "MenuButton");
            Assert.Equal(titles.Skip(shown).Select(t => t.Content as string), OpenMenuTitles(window));
            Ui.Press(window, "ESCAPE");
            double project = window.FindControl<Button>("ProjectButton")!.Bounds.Width;

            window.ApplySettings(window.Settings with { MainMenuBar = false });
            Ui.Settle();
            Assert.True(Math.Abs(window.FindControl<Button>("ProjectButton")!.Bounds.Width - project) < 0.5,
                $"Beside the bar the project's name was {project} wide, {window.FindControl<Button>("ProjectButton")!.Bounds.Width} without it: it narrowed while the bar still had menus to put behind its button");
            Assert.True(titles.All(t => t.Opacity == 0) && button.Opacity == 1, "With Show main menu as a menu bar off, the bar still shows");
            ClickOpen(window, "MenuButton");
            Assert.Equal(titles.Select(t => t.Content as string), OpenMenuTitles(window));
            Ui.Press(window, "ESCAPE");
        }
        finally
        {
            MainWindow.SystemMenuBar = OperatingSystem.IsMacOS();
            window.Close();
        }
    }

    /// <summary>The bar takes the keys as a menu bar does: Alt tapped alone takes its first title, Left and Right move
    /// among the titles and, with a menu open, open the next one's, Down opens a menu, the open menu's title shows pressed,
    /// and Escape gives the keys back to the editor.</summary>
    [AvaloniaFact]
    public void The_main_menu_bar_takes_the_keys_as_a_menu_bar_does_and_gives_them_back_to_the_editor()
    {
        MainWindow.SystemMenuBar = false;
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes\n");
        MainWindow window = Ui.OpenFolder(folder, width: 1800);
        try
        {
            window.OpenFile("Notes.txt");
            window.ApplySettings(window.Settings with { MainMenuBar = true });
            window.EditorArea.CodeEditor.Focus();
            Ui.Settle();
            MainMenuPlace place = window.FindControl<MainMenuPlace>("MainMenuPlace")!;
            Button[] titles = [.. place.Children.OfType<Button>().Where(b => b.Tag is string)];

            TapAlt(window);
            Assert.True(Focused(window) == titles[0], $"Alt tapped alone gave the keys to {Focused(window)?.GetType().Name}, not the bar's File");
            Ui.Press(window, "RIGHT");
            Assert.True(Focused(window) == titles[1], "Right did not move from File to Edit");
            Ui.Press(window, "LEFT");
            Ui.Press(window, "LEFT");
            Assert.True(Focused(window) == titles[^1], "Left from File did not go round to the last title, Help");
            Ui.Press(window, "RIGHT");
            Assert.True(Focused(window) == titles[0], "Right from Help did not go round to File");

            Ui.Press(window, "DOWN");
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Open Solution…"), "the File menu, opened by Down");
            Assert.True(Focused(window) is MenuItem, $"The File menu opened by Down left the keys with {Focused(window)?.GetType().Name}");
            Assert.True(titles[0].Classes.Contains(StyleClasses.Open), "The open File menu's title does not show pressed");
            Ui.Press(window, "RIGHT");
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Undo"), "the Edit menu, opened by Right from File's");
            Assert.False(titles[0].Classes.Contains(StyleClasses.Open), "File's title still shows pressed with Edit's menu open");
            Assert.True(titles[1].Classes.Contains(StyleClasses.Open) && Focused(window) is MenuItem, "Edit's menu opened by Right is not shown pressed with the keys");
            Ui.Press(window, "LEFT");
            Ui.WaitFor(() => OpenMenuTitles(window).Contains("Open Solution…"), "the File menu again, opened by Left from Edit's");

            Ui.Press(window, "ESCAPE");
            Assert.False(MenuOpen(window), "Escape left the bar's menu open");
            Assert.True(Focused(window) == window.EditorArea.CodeEditor, $"Escape gave the keys to {Focused(window)?.GetType().Name}, not back to the editor");
            Assert.False(titles[0].Classes.Contains(StyleClasses.Open), "File's title still shows pressed with its menu closed");
            TapAlt(window);
            TapAlt(window);
            Assert.True(Focused(window) == window.EditorArea.CodeEditor, "Alt tapped again did not give the keys back to the editor");

            window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
            Ui.Press(window, "alt UP");
            window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
            Ui.Settle();
            Assert.False(Focused(window) is Button, "Alt released after another key gave the keys to the bar");
        }
        finally
        {
            MainWindow.SystemMenuBar = OperatingSystem.IsMacOS();
            window.Close();
        }
    }

    /// <summary>Alt held through a drag in the editor, a column's selection, gives the bar nothing as it is let go, though
    /// Windows repeats the held Alt's press during the drag: the keys stay with the editor and its column (#1635).</summary>
    [AvaloniaFact]
    public void Alt_let_go_after_an_alt_drag_leaves_the_keys_and_the_column_with_the_editor()
    {
        MainWindow.SystemMenuBar = false;
        File.WriteAllText(Path.Combine(folder, "Columns.txt"), "first line\nsecond line\nthird line\n");
        MainWindow window = Ui.OpenFolder(folder, width: 1800);
        try
        {
            window.OpenFile("Columns.txt");
            window.ApplySettings(window.Settings with { MainMenuBar = true });
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            Ui.Settle();
            Point from = editor.TranslatePoint(editor.PointOf(0) + new Point(1, editor.LineHeight / 2), window)!.Value;
            Point to = editor.TranslatePoint(editor.PointOf("first line\nsecond line\n".Length + 5) + new Point(1, editor.LineHeight / 2), window)!.Value;

            window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
            Ui.Drag(window, from, to, MouseButton.Left, RawInputModifiers.Alt);
            window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
            window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
            Ui.Settle();

            Assert.True(Focused(window) == editor, $"Alt let go after an Alt-drag gave the keys to {Focused(window)?.GetType().Name}, so the column's selection can't be typed into");
            Assert.True(editor.Selections.Count == 3, $"Alt let go after an Alt-drag over three lines left {editor.Selections.Count} selections, not the column's three");
            TapAlt(window);
            Assert.True(Focused(window) is Button, "Alt tapped alone after the drag no longer takes the bar");
        }
        finally
        {
            MainWindow.SystemMenuBar = OperatingSystem.IsMacOS();
            window.Close();
        }
    }

    /// <summary>A submenu opens beside its item, never over it, on whichever side it has room, in the editor's menu and the
    /// main menu alike: its frame starts past the item's chevron (or ends before the item, opened to the left), its first
    /// item in line with the item, and its popup, the room its shadow is drawn in too, lies over none of the item, since a
    /// desktop gives a popup the pointer across its whole rect and the item would lose it to the shadow.</summary>
    [AvaloniaFact]
    public void A_submenu_opens_beside_its_item_its_shadow_over_none_of_it_whichever_side_it_opens()
    {
        // The main menu behind the toolbar's button, as on Windows and Linux, rather than in macOS's own menu bar.
        MainWindow.SystemMenuBar = false;
        MainWindow window = OpenOne();
        try
        {
            (string What, Func<MenuItem> Item, Action Close)[] menus =
            [
                ("the editor's menu's Refactor, opened to the right", () => EditorMenuItem(window, left: false), () => window.OpenContextMenu!.Close()),
                ("the editor's menu's Refactor, opened to the left by the window's right edge", () => EditorMenuItem(window, left: true), () => window.OpenContextMenu!.Close()),
                ("the main menu's View", () => MainMenuItem(window, "View"), () => CloseMainMenu(window)),
            ];
            foreach ((string what, Func<MenuItem> open, Action close) in menus)
            {
                MenuItem item = open();
                Border frame = (Border)OpenSubmenu(item).Child!;
                Rect row = Placed(window, item);
                Rect chevron = Placed(window, item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "PART_ChevronPath"));
                Rect drawn = Placed(window, frame);
                Rect whole = Placed(window, (Visual)frame.GetVisualParent()!);
                bool left = drawn.Center.X < row.Center.X;
                Assert.True(left ? drawn.Right <= row.Left + 0.5 : drawn.Left >= chevron.Right - 0.5,
                    $"The submenu of {what} is drawn at {drawn}, over its item at {row} (its chevron at {chevron}), which it covers");
                Assert.False(whole.Intersects(row),
                    $"The submenu of {what} takes {whole} with the room for its shadow, over its item at {row}, so on a desktop the item loses the pointer to the shadow");
                Rect first = Placed(window, frame.GetVisualDescendants().OfType<MenuItem>().First());
                Assert.True(Math.Abs(first.Top - row.Top) <= 0.5, $"The submenu of {what} begins its items at {first.Top:0.#}, out of line with its item at {row.Top:0.#}");
                close();
                Ui.Settle();
            }
        }
        finally
        {
            MainWindow.SystemMenuBar = OperatingSystem.IsMacOS();
            window.Close();
        }
    }

    /// <summary>A pointer resting along a submenu's edge, in the shadow round its frame, beside its item, above, below and
    /// past it, keeps the submenu open past the delay a submenu the pointer left closes after: the shadow is the submenu's
    /// own, so it never closes and opens again under a still pointer.</summary>
    [AvaloniaFact]
    public void A_pointer_in_a_submenus_shadow_along_its_edge_keeps_it_open_with_no_close_and_open_again()
    {
        MainWindow window = OpenOne();
        try
        {
            MenuItem item = EditorMenuItem(window, left: false);
            Popup popup = OpenSubmenu(item);
            Border frame = (Border)popup.Child!;
            Rect drawn = Placed(window, frame);
            Thickness room = frame.Margin;
            Rect row = Placed(window, item);
            int opened = 0;
            int closed = 0;
            popup.Opened += (_, _) => opened++;
            popup.Closed += (_, _) => closed++;
            (string Where, Point At)[] shadow =
            [
                ("beside its item", new Point(drawn.Left - (room.Left / 2), row.Center.Y)),
                ("above it", new Point(drawn.Center.X, drawn.Top - (room.Top / 2))),
                ("past it", new Point(drawn.Right + (room.Right / 2), drawn.Center.Y)),
                ("below it", new Point(drawn.Center.X, drawn.Bottom + (room.Bottom / 2))),
                ("by its corner", new Point(drawn.Left - (room.Left / 2), drawn.Bottom + (room.Bottom / 2))),
            ];
            foreach ((string where, Point at) in shadow)
            {
                window.MouseMove(at);
                Ui.Hold(DefaultMenuInteractionHandler.MenuShowDelay * 2, "past the delay a submenu the pointer left closes after");
                Assert.True(item.IsSubMenuOpen && opened == 0 && closed == 0,
                    $"With the pointer at {at}, in Refactor's submenu's shadow {where} (its frame at {drawn}), the submenu closed {closed} times and opened {opened}, so a pointer resting there makes it flicker or go");
            }
            window.OpenContextMenu!.Close();
            Ui.Settle();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The pointer carried from an item's middle across the gap into its submenu, 2 px a step at a hand's pace,
    /// slowing in the gap past the delay a menu waits before it acts on a move: straight to the submenu's first item,
    /// and on a slant to a lower one across the item below, which has a submenu of its own (the editor's Local History,
    /// Git below it). The submenu never closes, no other opens, and the item reached takes the pointer: the gap is the
    /// submenu's, so resting there gives the menu back to its item.</summary>
    [AvaloniaFact]
    public void A_pointer_carried_from_an_item_across_the_gap_into_its_submenu_keeps_it_open_and_reaches_its_items()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        TestGit.Init(folder);
        MainWindow window = OpenOne();
        try
        {
            foreach (bool slant in (bool[])[false, true])
            {
                MenuItem item = EditorMenuItem(window, left: false, "Local History");
                MenuItem below = window.OpenContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == "Git");
                Popup popup = OpenSubmenu(item);
                Border frame = (Border)popup.Child!;
                MenuItem[] others = [.. window.OpenContextMenu.Items.OfType<MenuItem>().Where(i => i != item && i.ItemCount > 0)];
                MenuItem target = frame.GetVisualDescendants().OfType<MenuItem>().ElementAt(slant ? 1 : 0);
                Rect row = Placed(window, item);
                Rect reached = Placed(window, target);
                Point from = row.Center;
                Point to = new(reached.Left + 24, reached.Center.Y);
                // Where the path crosses the middle of the gap between the item and the submenu's frame.
                double middle = (row.Right + Placed(window, frame).Left) / 2;
                Point gap = from + ((to - from) * ((middle - from.X) / (to.X - from.X)));
                string path = $"{(slant ? "on a slant" : "straight")} from Local History's middle to {target.Header}";
                bool crossed = false;
                int closed = 0;
                popup.Closed += (_, _) => closed++;
                void Holds(Point at, string where)
                {
                    crossed |= below.IsPointerOver;
                    MenuItem? opened = others.FirstOrDefault(o => o.IsSubMenuOpen);
                    Assert.True(item.IsSubMenuOpen && closed == 0 && opened is null,
                        $"With the pointer carried {path}, at {at} ({where}), Local History's submenu {(closed > 0 ? "closed" : "is open")}{(opened is null ? "" : $" and {opened.Header}'s opened")}, so the pointer never reaches it");
                }
                Carry(window, from, gap, at => Holds(at, "on its way to the gap"));
                Assert.True(crossed == slant, $"The pointer carried {path} {(crossed ? "crossed" : "never crossed")} Git below it");
                Ui.Hold(DefaultMenuInteractionHandler.MenuShowDelay * 2, "a hand slowing in the gap past the delay a menu waits before it acts on a move");
                Holds(gap, "resting in the gap");
                Carry(window, gap, to, at => Holds(at, "on its way into the submenu"));
                Ui.Hold(DefaultMenuInteractionHandler.MenuShowDelay * 2, "past the delay a menu waits before it acts on a move");
                Holds(to, $"resting on {target.Header}");
                Assert.True(target.IsPointerOver && target.IsSelected,
                    $"With the pointer carried {path}, {target.Header} is {(target.IsPointerOver ? "" : "not ")}under it and {(target.IsSelected ? "" : "not ")}chosen");
                window.OpenContextMenu!.Close();
                Ui.Settle();
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The pointer moved from one point to another 2 px a step at a hand's 1,000 px a second, each step when the
    /// hand would reach it, a step already due going at once, so a wait longer than asked slows the hand by no more than
    /// itself. <paramref name="each"/> checks where the pointer is after each step.</summary>
    private static void Carry(MainWindow window, Point from, Point to, Action<Point> each)
    {
        Vector way = to - from;
        double length = Math.Sqrt((way.X * way.X) + (way.Y * way.Y));
        int steps = Math.Max(1, (int)Math.Ceiling(length / 2));
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int step = 1; step <= steps; step++)
        {
            Point at = from + (way * ((double)step / steps));
            window.MouseMove(at);
            each(at);
            TimeSpan due = TimeSpan.FromSeconds(length * step / steps / 1000) - System.Diagnostics.Stopwatch.GetElapsedTime(start);
            if (due > TimeSpan.Zero)
            {
                Ui.Hold(due, "a hand's pace");
            }
        }
    }

    /// <summary>A window on a C# file, with no language server: a menu's place is the window's.</summary>
    private MainWindow OpenOne()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("One.cs");
        Ui.Settle();
        return window;
    }

    /// <summary>The editor's menu opened, from the keys at the caret, or by a right click by the window's right edge, where
    /// a submenu has no room to its right; its item of a title, Refactor unless named.</summary>
    private static MenuItem EditorMenuItem(MainWindow window, bool left, string title = "Refactor")
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        if (left)
        {
            Ui.RightClick(editor, new Point(editor.Bounds.Width - 30, 30));
        }
        else
        {
            editor.Focus();
            Ui.Run(window, AppActions.ShowContextMenu);
        }
        Ui.WaitFor(() => window.OpenContextMenu is not null, "the editor's menu");
        Ui.Settle();
        return window.OpenContextMenu!.Items.OfType<MenuItem>().First(i => i.Header as string == title);
    }

    /// <summary>The main menu opened by the toolbar's button; its item of a menu's title.</summary>
    private static MenuItem MainMenuItem(MainWindow window, string title)
    {
        ClickOpen(window, "MenuButton");
        return window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Where(p => p.IsEffectivelyVisible)
            .SelectMany(p => p.Items.OfType<MenuItem>()).First(i => i.Header as string == title);
    }

    /// <summary>The main menu closed by Escape, its open submenu by the first press.</summary>
    private static void CloseMainMenu(MainWindow window)
    {
        Ui.Press(window, "ESCAPE");
        if (MenuOpen(window))
        {
            Ui.Press(window, "ESCAPE");
        }
        Assert.False(MenuOpen(window), "Escape left the main menu open");
    }

    /// <summary>An item's submenu opened by the pointer resting on the item; its popup.</summary>
    private static Popup OpenSubmenu(MenuItem item)
    {
        Ui.Hover(item);
        Ui.WaitFor(() => item.IsSubMenuOpen, $"{item.Header}'s submenu, opened by the pointer resting on its item");
        Ui.Settle();
        return item.GetVisualDescendants().OfType<Popup>().Single();
    }

    private static Rect Placed(Visual window, Visual control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void TapAlt(MainWindow window)
    {
        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Ui.Settle();
    }

    private static object? Focused(MainWindow window) => window.FocusManager?.GetFocusedElement();

    /// <summary>The pointer moved over a control, from beside it, as a person's moves.</summary>
    private static void Hover(MainWindow window, Control target)
    {
        window.MouseMove(target.TranslatePoint(new Point(2, target.Bounds.Height / 2), window)!.Value);
        window.MouseMove(target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value);
        Ui.Settle();
    }

    /// <summary>The items of the menu open over the window, by their titles.</summary>
    private static string[] OpenMenuTitles(MainWindow window) =>
        [.. window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Where(p => p.IsEffectivelyVisible).SelectMany(p => p.Items.OfType<MenuItem>()).Select(i => i.Header as string ?? "")];

    /// <summary>A toolbar widget clicked, as a person's pointer clicks it, and its menu waited for.</summary>
    private static void ClickOpen(MainWindow window, string widget)
    {
        Ui.Click(window.FindControl<Button>(widget)!, settle: false);
        Ui.WaitFor(() => MenuOpen(window), $"the menu of the toolbar's {widget}");
    }

    /// <summary>Whether a menu is open over the window: a widget's, or the main menu's.</summary>
    private static bool MenuOpen(MainWindow window) =>
        window.OpenContextMenu is { IsOpen: true } || window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Any(p => p.IsEffectivelyVisible);

    private static NativeMenu Open(NativeMenu parent, string title) => Ui.Submenu(parent, title);

    private static NativeMenuItem Item(NativeMenu menu, string title) =>
        menu.Items.OfType<NativeMenuItem>().First(i => i.Header == title);
}
