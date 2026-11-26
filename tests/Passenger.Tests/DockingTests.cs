using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>Tool windows docked: a stripe button shows its window and hides the one in its place, a button or a header
/// dragged elsewhere moves the window there, and the person's window opens on the layout they left, fitted to the
/// window it opens in.</summary>
public sealed class DockingTests
{
    /// <summary>A stripe's buttons, each told apart by an icon of its own, show and hide their windows and dock them where
    /// dragged, and its checklist takes a window off the stripe and back, as one flow, each step the check one test made in
    /// an instance of its own.</summary>
    [AvaloniaFact]
    public void Stripe_buttons_show_hide_and_dock_tool_windows_where_they_are_dropped()
    {
        Flow.StepApart<DockingTests>("every tool window wears an icon no other tool window wears", t => t.Every_tool_window_wears_an_icon_no_other_tool_window_wears());
        Flow.StepApart<DockingTests>("a stripe button shows its window in its place and hides the one there", t => t.A_stripe_button_shows_its_window_in_its_place_and_hides_the_one_there());
        Flow.StepApart<DockingTests>("a stripe button dragged to the other stripe docks its window on that side", t => t.A_stripe_button_dragged_to_the_other_stripe_docks_its_window_on_that_side());
        Flow.StepApart<DockingTests>("a drop above the first lower button of a stripe with no upper ones docks in its upper group", t => t.A_drop_above_the_first_lower_button_of_a_stripe_with_no_upper_ones_docks_in_its_upper_group());
        Flow.StepApart<DockingTests>("a header dragged onto the bottom right docks the window there beside the bottom left one", t => t.A_header_dragged_onto_the_bottom_right_docks_the_window_there_beside_the_bottom_left_one());
        Flow.StepApart<DockingTests>("a stripe button dropped between two of its places lands between them past a window off the stripe and its own place", t => t.A_stripe_button_dropped_between_two_of_its_places_lands_between_them_past_a_window_off_the_stripe_and_its_own_place());
        Flow.StepApart<DockingTests>("a right click on a stripe lists the windows of the icons place or of the place nearest its empty space (true)", t => t.A_right_click_on_a_stripe_lists_the_windows_of_the_icons_place_or_of_the_place_nearest_its_empty_space(true));
        Flow.StepApart<DockingTests>("a window unchecked on its stripes checklist leaves the stripe open or not and comes back checked or from View", t => t.A_window_unchecked_on_its_stripes_checklist_leaves_the_stripe_open_or_not_and_comes_back_checked_or_from_View());
        Flow.StepApart<DockingTests>("a stripe whose every icon was unchecked stays and its checklist puts one back", t => t.A_stripe_whose_every_icon_was_unchecked_stays_and_its_checklist_puts_one_back());
    }

    /// <summary>A tool window floated, windowed or maximised comes back where it stood, closes with its own window
    /// alone and is let go once closed, as one flow, each step in an instance of its own.</summary>
    [AvaloniaFact]
    public void Floating_and_maximised_tool_windows_come_back_where_they_stood_and_are_let_go()
    {
        Flow.StepApart<DockingTests>("a floating tool window takes its content to a window of its own and back", t => t.A_floating_tool_window_takes_its_content_to_a_window_of_its_own_and_back());
        Flow.StepApart<DockingTests>("Shift Escape hides the docked window still shown once a floating windows own window is closed", t => t.Shift_Escape_hides_the_docked_window_still_shown_once_a_floating_windows_own_window_is_closed());
        Flow.StepApart<DockingTests>("a windowed tool window closes with its window alone and opens again where it stood", t => t.A_windowed_tool_window_closes_with_its_window_alone_and_opens_again_where_it_stood());
        Flow.StepApart<DockingTests>("a floating tool window closed by the applications quit opens again where it stood", t => t.A_floating_tool_window_closed_by_the_applications_quit_opens_again_where_it_stood());
        Flow.StepApart<DockingTests>("Maximize gives a tool window the frame and hide all clears it for the editor", t => t.Maximize_gives_a_tool_window_the_frame_and_hide_all_clears_it_for_the_editor());
        Flow.StepApart<DockingTests>("a tool window maximised when the window closes opens again at the size it had before", t => t.A_tool_window_maximised_when_the_window_closes_opens_again_at_the_size_it_had_before());
        Flow.StepApart<DockingTests>("a floating window closed is let go and not kept alive by its tool window", t => t.A_floating_window_closed_is_let_go_and_not_kept_alive_by_its_tool_window());
    }

    /// <summary>The window opens on the layout the person left, its areas fitted to a smaller screen, until Restore
    /// Default Layout puts each window back; a hidden window's view is made as it first shows, with what was written
    /// before, as one flow, each step in an instance of its own.</summary>
    [AvaloniaFact]
    public void The_layout_left_is_the_one_the_window_opens_on_until_Restore_Default_Layout()
    {
        Flow.StepApart<DockingTests>("the persons window opens on the layout they left", t => t.The_persons_window_opens_on_the_layout_they_left());
        Flow.StepApart<DockingTests>("areas kept on a larger screen leave the editor its share of a smaller window and come back on a larger", t => t.Areas_kept_on_a_larger_screen_leave_the_editor_its_share_of_a_smaller_window_and_come_back_on_a_larger());
        Flow.StepApart<DockingTests>("a wide window runs its sides the whole height by itself until it is narrow again each layout keeping its sizes through a restart", t => t.A_wide_window_runs_its_sides_the_whole_height_by_itself_until_it_is_narrow_again_each_layout_keeping_its_sizes_through_a_restart());
        Flow.StepApart<DockingTests>("a place with every window off the stripe is on its stripes checklist through a restart until Restore Default Layout", t => t.A_place_with_every_window_off_the_stripe_is_on_its_stripes_checklist_through_a_restart_until_Restore_Default_Layout());
        Flow.StepApart<DockingTests>("Restore Default Layout puts every tool window back where it first was docked at its first size", t => t.Restore_Default_Layout_puts_every_tool_window_back_where_it_first_was_docked_at_its_first_size());
        Flow.StepApart<DockingTests>("a hidden tool windows view is built only as it is first shown and a console shows what was written before", t => t.A_hidden_tool_windows_view_is_built_only_as_it_is_first_shown_and_a_console_shows_what_was_written_before());
        Flow.StepApart<DockingTests>("Find in the Run console finds in its output", t => t.Find_in_the_Run_console_finds_in_its_output());
    }

    /// <summary>A stripe button is told from the others by its icon alone: no two tool windows wear one shape, whatever
    /// their icons' keys.</summary>
    private void Every_tool_window_wears_an_icon_no_other_tool_window_wears()
    {
        string icons = File.ReadAllText(Path.Combine(Passenger.Tests.Debugging.Helpers.Debuggees.Root, "src", "Passenger.App", "Theme", "Icons.axaml"));
        Dictionary<string, string> shapes = System.Text.RegularExpressions.Regex.Matches(icons, @"x:Key=""([^""]+)"">([^<]+)<")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        MainWindow window = Open();
        ToolWindow[] tools = [.. window.DockHost.Windows];
        window.Close();
        Assert.True(tools.Length == typeof(ToolWindowIds).GetFields().Length,
            $"The window holds {tools.Length} tool windows, not one for each of the {typeof(ToolWindowIds).GetFields().Length} ids, so the rest wear icons unchecked");
        string[] shared = [.. tools.GroupBy(t => shapes[t.Icon]).Where(g => g.Count() > 1).Select(g => string.Join(" and ", g.Select(t => t.Title)))];
        Assert.True(shared.Length == 0,
            $"{string.Join("; ", shared)} wear one icon, so their stripe buttons cannot be told apart: give each its own in tools/icons.py");
    }

    private void A_stripe_button_shows_its_window_in_its_place_and_hides_the_one_there()
    {
        MainWindow window = Open();
        Ui.Click(Button(window, ToolWindowIds.Debug));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Debug));
        Ui.Click(Button(window, ToolWindowIds.Run));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Run));
        Assert.False(window.DockHost.IsShown(ToolWindowIds.Debug));
        Assert.True(Button(window, ToolWindowIds.Run).IsChecked);
        Assert.False(Button(window, ToolWindowIds.Debug).IsChecked);

        Ui.Click(Button(window, ToolWindowIds.Run));
        Assert.False(window.DockHost.IsShown(ToolWindowIds.Run));
        window.Close();
    }

    private void A_stripe_button_dragged_to_the_other_stripe_docks_its_window_on_that_side()
    {
        MainWindow window = Open();
        window.DockHost.Show(ToolWindowIds.Debug);
        Ui.Settle();
        ToggleButton debug = Button(window, ToolWindowIds.Debug);
        Point from = Ui.Centre(debug, window);
        Point to = new(window.Bounds.Width - 20, 120);
        Ui.Drag(window, from, to);
        ToolWindow moved = window.DockHost[ToolWindowIds.Debug];
        Assert.Equal(ToolAnchor.RightTop, moved.Anchor);
        Assert.True(moved.IsShown);
        Assert.True(window.DockHost.ColumnDefinitions[5].ActualWidth > 100);
        Assert.Equal(0, window.DockHost.RowDefinitions[2].ActualHeight);
        Assert.Contains(Button(window, ToolWindowIds.Debug), Stripe(window, right: true));
        window.Close();
    }

    private void A_drop_above_the_first_lower_button_of_a_stripe_with_no_upper_ones_docks_in_its_upper_group()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Move(dock[ToolWindowIds.Hierarchy], ToolAnchor.RightBottom);
        foreach (ToolWindow other in dock.Windows.Where(w => w.Id != ToolWindowIds.Hierarchy
            && w.Anchor is ToolAnchor.RightTop or ToolAnchor.RightBottom or ToolAnchor.BottomRight).ToList())
        {
            dock.Move(other, ToolAnchor.LeftBottom);
        }
        Ui.Settle();
        string[] right = [.. Stripe(window, right: true).Select(b => (string)b.Tag!)];
        Assert.True(right is [ToolWindowIds.Hierarchy], $"The right stripe holds {string.Join(", ", right)}, not Hierarchy's button alone");
        Point lowest = Ui.Centre(Button(window, ToolWindowIds.Hierarchy), window);

        Ui.Drag(window, Ui.Centre(Button(window, ToolWindowIds.Debug), window), lowest - new Point(0, 4));
        Assert.Equal(ToolAnchor.RightTop, dock[ToolWindowIds.Debug].Anchor);

        // Below the middle of the lower group's first button, a drop joins that group.
        dock.Move(dock[ToolWindowIds.Debug], ToolAnchor.BottomLeft);
        Ui.Settle();
        Ui.Drag(window, Ui.Centre(Button(window, ToolWindowIds.Run), window), Ui.Centre(Button(window, ToolWindowIds.Hierarchy), window) + new Point(0, 4));
        Assert.Equal(ToolAnchor.RightBottom, dock[ToolWindowIds.Run].Anchor);
        window.Close();
    }

    private void A_header_dragged_onto_the_bottom_right_docks_the_window_there_beside_the_bottom_left_one()
    {
        MainWindow window = Open();
        window.DockHost.Show(ToolWindowIds.Run);
        Ui.Settle();
        Control header = Header(window, "Explorer");
        Point from = Ui.Centre(header, window);
        Point to = new(window.Bounds.Width * 0.8, window.Bounds.Height * 0.85);
        Ui.Drag(window, from, to);
        Assert.Equal(ToolAnchor.BottomRight, window.DockHost[ToolWindowIds.Explorer].Anchor);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Explorer));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Run));
        Assert.Equal(0, window.DockHost.ColumnDefinitions[1].ActualWidth);
        window.Close();
    }

    private void The_persons_window_opens_on_the_layout_they_left()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = Open(settings);
        first.KeepLayout();
        first.DockHost.Move(first.DockHost[ToolWindowIds.Tests], ToolAnchor.RightBottom);
        first.DockHost.Show(ToolWindowIds.Tests);
        first.DockHost.Hide(ToolWindowIds.Explorer);
        first.Close();

        MainWindow second = Open(settings);
        second.KeepLayout();
        Ui.Settle();
        Assert.Equal(ToolAnchor.RightBottom, second.DockHost[ToolWindowIds.Tests].Anchor);
        Assert.True(second.DockHost.IsShown(ToolWindowIds.Tests));
        Assert.False(second.DockHost.IsShown(ToolWindowIds.Explorer));
        second.Close();
    }

    private void A_floating_tool_window_takes_its_content_to_a_window_of_its_own_and_back()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show(ToolWindowIds.Debug);
        ToolWindow debug = dock[ToolWindowIds.Debug];
        dock.SetMode(debug, ToolMode.Floating);
        Ui.Settle();
        Window floating = Assert.Single(window.OwnedWindows);
        Assert.Contains(window.DebugWindow, floating.GetVisualDescendants());
        Assert.Equal(0, dock.RowDefinitions[2].ActualHeight);
        Assert.True(debug.IsShown);

        dock.Toggle(ToolWindowIds.Debug);
        Assert.Empty(window.OwnedWindows);
        dock.Toggle(ToolWindowIds.Debug);
        Assert.Single(window.OwnedWindows);

        window.OwnedWindows[0].Close();
        Ui.Settle();
        Assert.False(debug.IsShown);
        dock.SetMode(debug, ToolMode.Docked);
        dock.Show(ToolWindowIds.Debug);
        Ui.Settle();
        Assert.True(dock.RowDefinitions[2].ActualHeight > 100);
        Assert.Contains(window.DebugWindow, dock.GetVisualDescendants());
        window.Close();
    }

    /// <summary>A floating tool window whose own window the person closes is hidden as Hide hides one: Shift+Escape then
    /// hides the docked window still shown, not the one already gone.</summary>
    private void Shift_Escape_hides_the_docked_window_still_shown_once_a_floating_windows_own_window_is_closed()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        if (dock.IsShown(ToolWindowIds.Explorer))
        {
            Ui.Click(Button(window, ToolWindowIds.Explorer));
        }
        dock.SetMode(dock[ToolWindowIds.Structure], ToolMode.Floating);
        Ui.Click(Button(window, ToolWindowIds.Run));
        Ui.Click(Button(window, ToolWindowIds.Structure));
        Window floating = Assert.Single(window.OwnedWindows);
        Assert.True(dock.Windows.Where(w => w.IsShown).Select(w => w.Id).Order().SequenceEqual([ToolWindowIds.Run, ToolWindowIds.Structure]),
            $"Run docked and Structure floating are not the only windows shown: {string.Join(", ", dock.Windows.Where(w => w.IsShown).Select(w => w.Id))}");

        floating.Close();
        Ui.Settle();
        Assert.False(dock.IsShown(ToolWindowIds.Structure), "Structure stayed shown once its floating window was closed");
        Ui.Press(window, "shift ESCAPE");
        Assert.False(dock.IsShown(ToolWindowIds.Run), "Shift+Escape left the docked Run window shown once the floating Structure window was closed");
        window.Close();
    }

    /// <summary>A tool window set to Window, and one floating, close with the IDE's window they belong to, quietly: with
    /// two windows open, closing one closes only its own, and the next window on the layout kept opens them again as they
    /// were, where they stood.</summary>
    private void A_windowed_tool_window_closes_with_its_window_alone_and_opens_again_where_it_stood()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = Open(settings);
        first.KeepLayout();
        MainWindow other = Open();
        List<Window> firsts = [];
        List<Window> others = [];
        first.DockHost.WindowOpened += (_, opened) => firsts.Add(opened);
        other.DockHost.WindowOpened += (_, opened) => others.Add(opened);
        foreach ((DockHost dock, string id, ToolMode mode) in new[] { (first.DockHost, ToolWindowIds.Git, ToolMode.Windowed), (first.DockHost, ToolWindowIds.Structure, ToolMode.Floating), (other.DockHost, ToolWindowIds.Git, ToolMode.Windowed) })
        {
            dock.Show(id);
            dock.SetMode(dock[id], mode);
        }
        Ui.Settle();
        Window git = Assert.Single(firsts, w => w.Title == "Git");
        git.Position = new PixelPoint(140, 90);
        (git.Width, git.Height) = (610, 430);
        Ui.Settle();
        PixelRect stood = new(140, 90, 610, 430);
        Window othersGit = Assert.Single(others);
        Assert.True(firsts.All(w => w.IsVisible) && othersGit.IsVisible, "A tool window set to Window or floating did not open a window of its own");

        first.Close();
        Ui.Settle();
        Assert.False(first.IsVisible);
        Assert.All(firsts, w => Assert.False(w.IsVisible, $"The {w.Title} window, set to {(w.Owner is null ? "Window" : "Floating")}, stayed open after its IDE window closed"));
        Assert.True(othersGit.IsVisible, "Closing one IDE window closed the Git window set to Window of the other");

        MainWindow next = Open(settings);
        List<Window> nexts = [];
        next.DockHost.WindowOpened += (_, opened) => nexts.Add(opened);
        next.KeepLayout();
        Ui.Settle();
        ToolWindow nextGit = next.DockHost[ToolWindowIds.Git];
        Assert.True(nextGit is { IsShown: true, Mode: ToolMode.Windowed }, "The Git window set to Window, closed with its IDE window, was not kept open in the layout");
        Assert.True(next.DockHost[ToolWindowIds.Structure] is { IsShown: true, Mode: ToolMode.Floating }, "The floating Structure window, closed with its IDE window, was not kept open in the layout");
        Assert.Equal(stood, nextGit.WindowBounds);
        Assert.Equal(["Git", "Structure"], nexts.Where(w => w.IsVisible).Select(w => w.Title).Order());
        next.Close();
        other.Close();
        Ui.Settle();
        Assert.False(othersGit.IsVisible, "The other IDE window's Git window set to Window stayed open after it closed");
    }

    /// <summary>A floating tool window closed by the application's shutdown (the application menu's Quit, a logout), which
    /// closes it before the IDE's window it floats over, is kept as it stood: the next window on the layout kept opens it
    /// floating there.</summary>
    private void A_floating_tool_window_closed_by_the_applications_quit_opens_again_where_it_stood()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = Open(settings);
        first.KeepLayout();
        List<Window> opened = [];
        first.DockHost.WindowOpened += (_, window) => opened.Add(window);
        first.DockHost.Show(ToolWindowIds.Structure);
        first.DockHost.SetMode(first.DockHost[ToolWindowIds.Structure], ToolMode.Floating);
        Ui.Settle();
        Window structure = Assert.Single(opened);
        structure.Position = new PixelPoint(160, 110);
        (structure.Width, structure.Height) = (430, 380);
        Ui.Settle();

        Ui.CloseAsQuit(first);
        Ui.Settle();
        Assert.False(first.IsVisible || structure.IsVisible, "The application's quit left the IDE's window or its floating Structure window open");

        MainWindow next = Open(settings);
        List<Window> nexts = [];
        next.DockHost.WindowOpened += (_, window) => nexts.Add(window);
        next.KeepLayout();
        Ui.Settle();
        ToolWindow kept = next.DockHost[ToolWindowIds.Structure];
        Assert.True(kept is { IsShown: true, Mode: ToolMode.Floating },
            $"The floating Structure window, closed by the application's quit, was kept {(kept.IsShown ? "shown" : "hidden")} and {kept.Mode}, not shown floating");
        Assert.Equal(new PixelRect(160, 110, 430, 380), kept.WindowBounds);
        Assert.Equal(["Structure"], nexts.Where(w => w.IsVisible).Select(w => w.Title));
        next.Close();
    }

    private void Maximize_gives_a_tool_window_the_frame_and_hide_all_clears_it_for_the_editor()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show(ToolWindowIds.Debug);
        Ui.Settle();
        double before = dock.RowDefinitions[2].ActualHeight;
        Ui.Run(window, AppActions.MaximizeToolWindow);
        Assert.True(dock.RowDefinitions[2].ActualHeight > window.Bounds.Height * 0.75);
        Ui.Run(window, AppActions.MaximizeToolWindow);
        Assert.Equal(before, dock.RowDefinitions[2].ActualHeight, 1);

        Ui.Run(window, AppActions.HideAllToolWindows);
        Assert.DoesNotContain(dock.Windows, w => w.IsShown);
        Ui.Run(window, AppActions.HideAllToolWindows);
        Assert.True(dock.IsShown(ToolWindowIds.Debug));
        Assert.True(dock.IsShown(ToolWindowIds.Explorer));
        window.Close();
    }

    private void A_tool_window_maximised_when_the_window_closes_opens_again_at_the_size_it_had_before()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = Open(settings);
        first.KeepLayout();
        DockHost dock = first.DockHost;
        dock.Show(ToolWindowIds.Build);
        Ui.Settle();
        // The person's own size, dragged above the default.
        Control splitter = dock.Children.First(c => Grid.GetRow(c) == 1).GetVisualDescendants().OfType<GridSplitter>().Single();
        Point grip = Ui.Centre(splitter, first);
        Ui.Drag(first, grip, grip - new Point(0, 60));
        double restored = dock.RowDefinitions[2].ActualHeight;
        Assert.True(restored > 320, $"The bottom area was not dragged taller: {restored}");
        Ui.Run(first, AppActions.MaximizeToolWindow);
        dock.Show(ToolWindowIds.Structure);
        Ui.Settle();
        Assert.True(dock.RowDefinitions[2].ActualHeight > restored + 100);
        first.Close();

        MainWindow second = Open(settings);
        second.KeepLayout();
        Ui.Settle();
        Assert.True(second.DockHost.IsShown(ToolWindowIds.Build));
        Assert.False(second.DockHost.IsMaximized);
        Assert.True(Math.Abs(second.DockHost.RowDefinitions[2].ActualHeight - restored) < 1,
            $"The Build window opened {second.DockHost.RowDefinitions[2].ActualHeight} tall, the maximised height kept, not the {restored} it had before");
        second.Close();
    }

    private void A_floating_window_closed_is_let_go_and_not_kept_alive_by_its_tool_window()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show(ToolWindowIds.Debug);
        dock.SetMode(dock[ToolWindowIds.Debug], ToolMode.Floating);
        Ui.Settle();
        WeakReference<Window> hidden = Closed(window, () => dock.Toggle(ToolWindowIds.Debug));
        dock.Toggle(ToolWindowIds.Debug);
        Ui.Settle();
        WeakReference<Window> closed = Closed(window, () => window.OwnedWindows[0].Close());
        dock.SetMode(dock[ToolWindowIds.Debug], ToolMode.Docked);
        dock.Show(ToolWindowIds.Debug);
        Ui.Settle();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(hidden.TryGetTarget(out _), "The floating window hidden by its button is still alive, held by its tool window");
        Assert.False(closed.TryGetTarget(out _), "The floating window the system closed is still alive, held by its tool window");
        Assert.Contains(window.DebugWindow, dock.GetVisualDescendants());
        window.Close();
    }

    private void Areas_kept_on_a_larger_screen_leave_the_editor_its_share_of_a_smaller_window_and_come_back_on_a_larger()
    {
        string settings = TestFolders.Create("settings-");
        File.WriteAllText(Path.Combine(settings, "layout.json"), """
            {
              "version": 1,
              "windows": {
                "explorer": { "anchor": "LeftTop", "order": 0, "shown": true },
                "tests": { "anchor": "RightTop", "order": 0, "shown": true },
                "build": { "anchor": "BottomLeft", "order": 0, "shown": true }
              },
              "leftWidth": 1400,
              "rightWidth": 900,
              "bottomHeight": 1100
            }
            """);
        MainWindow window = Open(settings);
        window.KeepLayout();
        Ui.Settle();
        DockHost dock = window.DockHost;
        double inner = dock.Bounds.Width - 80;
        Assert.True(dock.ColumnDefinitions[3].ActualWidth >= inner / 4 - 1,
            $"The editor was left {dock.ColumnDefinitions[3].ActualWidth} of {inner} between the side areas");
        Assert.True(dock.RowDefinitions[0].ActualHeight >= dock.Bounds.Height / 4 - 1,
            $"The editor was left {dock.RowDefinitions[0].ActualHeight} of {dock.Bounds.Height} above the bottom area");
        Assert.True(Math.Abs((dock.ColumnDefinitions[1].ActualWidth / dock.ColumnDefinitions[5].ActualWidth) - (1400.0 / 900)) < 0.01);
        Control rightStripe = dock.Children.First(c => Grid.GetColumn(c) == 6);
        Assert.True(rightStripe.Bounds.Right <= dock.Bounds.Width, $"The right stripe was pushed out of the window, to {rightStripe.Bounds}");

        // On a screen as large as the one they were kept on, they are as large as they were.
        window.Width = 3200;
        window.Height = 2000;
        Ui.Settle();
        Assert.Equal((1400, 900, 1100), (dock.ColumnDefinitions[1].ActualWidth, dock.ColumnDefinitions[5].ActualWidth, dock.RowDefinitions[2].ActualHeight));
        window.Close();
    }

    /// <summary>A window more than 2.2 times as wide as it is high runs its side areas down the whole frame, the bottom
    /// area between them, and goes back only under twice as wide, so a resize between leaves it as it stands; the windows
    /// open stay open, each layout keeps the bottom area's height it was given, through a restart too, a drop low over a
    /// side docks at that side, and the Settings row keeps either layout, shown before OK.</summary>
    private void A_wide_window_runs_its_sides_the_whole_height_by_itself_until_it_is_narrow_again_each_layout_keeping_its_sizes_through_a_restart()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow window = Open(settings);
        window.KeepLayout();
        DockHost dock = window.DockHost;
        string[] open = [ToolWindowIds.Explorer, ToolWindowIds.Hierarchy, ToolWindowIds.Build];
        foreach (string id in open)
        {
            dock.Show(id);
        }
        Ui.Settle();
        double across = dock.RowDefinitions[2].ActualHeight;
        Shaped(window, wide: false, "1200 by 800");

        Sized(window, 2560, 1080);
        Shaped(window, wide: true, "2560 by 1080");
        Point divider = Ui.Centre(Divider(dock, c => Grid.GetRow(c) == 1), window) + new Point(0, 3);
        Ui.Drag(window, divider, divider - new Point(0, 120));
        double wide = dock.RowDefinitions[2].ActualHeight;
        Assert.True(wide > across + 100, $"The wide layout's bottom area dragged 120 px taller is {wide} tall, from {across}");

        Sized(window, 2100, 1000);
        Shaped(window, wide: true, "2100 by 1000, narrowed from wide");
        Sized(window, 1900, 1000);
        Shaped(window, wide: false, "1900 by 1000");
        Assert.True(Math.Abs(dock.RowDefinitions[2].ActualHeight - across) < 1, $"Back from wide, the bottom area is {dock.RowDefinitions[2].ActualHeight} tall, not the {across} it had");
        Assert.True(open.All(dock.IsShown), $"Back from wide, {string.Join(", ", open.Where(id => !dock.IsShown(id)))} no longer shows");
        Sized(window, 2100, 1000);
        Shaped(window, wide: false, "2100 by 1000, widened from 1900");

        Sized(window, 2560, 1080);
        Assert.True(Math.Abs(dock.RowDefinitions[2].ActualHeight - wide) < 1, $"Wide again, the bottom area is {dock.RowDefinitions[2].ActualHeight} tall, not the {wide} it was given wide");
        // Low beside the editors, the left side, running the whole height, takes the drop, and between the sides the bottom.
        Rect left = Placed(dock, dock[ToolWindowIds.Explorer].Content);
        Ui.Drag(window, Ui.Centre(Button(window, ToolWindowIds.Requests), window), new Point(left.Center.X, window.Bounds.Height - 60));
        Assert.Equal(ToolAnchor.LeftBottom, dock[ToolWindowIds.Requests].Anchor);
        Rect editors = Placed(window, window.EditorArea);
        Ui.Drag(window, Ui.Centre(Button(window, ToolWindowIds.Structure), window), new Point(editors.Left + (editors.Width * 0.75), window.Bounds.Height - 60));
        Assert.Equal(ToolAnchor.BottomRight, dock[ToolWindowIds.Structure].Anchor);
        window.Close();

        MainWindow next = Open(settings);
        next.KeepLayout();
        Ui.Settle();
        Assert.True(Math.Abs(next.DockHost.RowDefinitions[2].ActualHeight - across) < 1, $"After a restart, the bottom area is {next.DockHost.RowDefinitions[2].ActualHeight} tall, not the {across} it had");
        Sized(next, 2560, 1080);
        Assert.True(Math.Abs(next.DockHost.RowDefinitions[2].ActualHeight - wide) < 1, $"After a restart, the wide layout's bottom area is {next.DockHost.RowDefinitions[2].ActualHeight} tall, not the {wide} it was given");

        SettingsDialog dialog = next.ShowSettings();
        dialog.Page = SettingsDialog.Appearance;
        Ui.Settle();
        SettingsTests.Editor<ComboBox>(dialog, "Wide layout").SelectedItem = "Never";
        Ui.Settle();
        Assert.False(next.DockHost.IsWide, "Wide layout set to Never in Settings did not show the other layout before OK");
        dialog.Ok();
        Sized(next, 1200, 800);
        dialog = next.ShowSettings();
        dialog.Page = SettingsDialog.Appearance;
        Ui.Settle();
        SettingsTests.Editor<ComboBox>(dialog, "Wide layout").SelectedItem = "Always";
        dialog.Ok();
        Ui.Settle();
        Shaped(next, wide: true, "1200 by 800, Wide layout set to Always");
        next.Close();
    }

    private static void Sized(MainWindow window, double width, double height)
    {
        (window.Width, window.Height) = (width, height);
        Ui.Settle();
    }

    /// <summary>Checks the Explorer at the left, Hierarchy at the right and Build at the bottom stand as the layout lays
    /// them: the sides down the whole frame with the bottom area between them where wide, else the bottom area under all.</summary>
    private static void Shaped(MainWindow window, bool wide, string size)
    {
        DockHost dock = window.DockHost;
        Rect left = Placed(dock, dock[ToolWindowIds.Explorer].Content);
        Rect right = Placed(dock, dock[ToolWindowIds.Hierarchy].Content);
        Rect bottom = Placed(dock, dock[ToolWindowIds.Build].Content);
        bool sides = left.Bottom >= bottom.Bottom - 1 && right.Bottom >= bottom.Bottom - 1 && bottom.Left >= left.Right && bottom.Right <= right.Left;
        bool under = left.Bottom <= bottom.Top && right.Bottom <= bottom.Top && bottom.Left < left.Right && bottom.Right > right.Left;
        Assert.True(dock.IsWide == wide && (wide ? sides : under),
            $"At {size} the frame stands {(dock.IsWide ? "wide" : "with the bottom area across")}, the left side at {left}, the right at {right} and the bottom at {bottom}, "
            + $"where it is to {(wide ? "run the sides down the whole frame, the bottom area between them" : "run the bottom area under the sides")}");
    }

    private static Rect Placed(Visual within, Visual control) => new(control.TranslatePoint(default, within) ?? default, control.Bounds.Size);

    private void Restore_Default_Layout_puts_every_tool_window_back_where_it_first_was_docked_at_its_first_size()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow window = Open(settings);
        window.KeepLayout();
        DockHost dock = window.DockHost;
        Dictionary<string, (ToolAnchor Anchor, int Order, ToolMode Mode)> first = dock.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order, w.Mode));
        dock.Show(ToolWindowIds.Build);
        Ui.Settle();
        double bottom = dock.RowDefinitions[2].ActualHeight;
        dock.Hide(ToolWindowIds.Build);
        dock.Show(ToolWindowIds.Explorer);

        Ui.Drag(window, Ui.Centre(Button(window, ToolWindowIds.Debug), window), new Point(window.Bounds.Width - 20, 120));
        dock.Move(dock[ToolWindowIds.Tests], ToolAnchor.LeftBottom, 0);
        dock.Show(ToolWindowIds.Tests);
        dock.Show(ToolWindowIds.Run);
        dock.SetMode(dock[ToolWindowIds.Run], ToolMode.Floating);
        dock.Hide(ToolWindowIds.Explorer);
        dock.Show(ToolWindowIds.Build);
        Ui.Settle();
        Point divider = Ui.Centre(Divider(dock, c => Grid.GetRow(c) == 1), window) + new Point(0, 3);
        Ui.Drag(window, divider, divider - new Point(0, 150));
        Assert.Equal(ToolAnchor.RightTop, dock[ToolWindowIds.Debug].Anchor);
        Assert.Single(window.OwnedWindows);
        Assert.True(dock.RowDefinitions[2].ActualHeight > bottom + 100, $"The bottom area dragged 150 px taller is {dock.RowDefinitions[2].ActualHeight} tall, from {bottom}");

        NativeMenuItem restore = Ui.Submenu(Ui.Submenu(window.MenuBar, "Window"), "Layout Settings").Items.OfType<NativeMenuItem>().Single(i => i.Header == "Restore Default Layout");
        restore.Command!.Execute(null);
        Ui.Settle();
        Assert.Equal(first, dock.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order, w.Mode)));
        Assert.All(dock.Windows, w => Assert.Equal(w.InEditorAtFirst ? ToolMode.Editor : ToolMode.Docked, w.Mode));
        Assert.Empty(window.OwnedWindows);
        Assert.Equal([ToolWindowIds.Explorer], dock.Windows.Where(w => w.IsShown).Select(w => w.Id));
        Assert.Contains(Button(window, ToolWindowIds.Debug), Stripe(window, right: false));
        window.Close();

        MainWindow next = Open(settings);
        next.KeepLayout();
        Assert.Equal(first, next.DockHost.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order, w.Mode)));
        Assert.Equal([ToolWindowIds.Explorer], next.DockHost.Windows.Where(w => w.IsShown).Select(w => w.Id));
        next.DockHost.Show(ToolWindowIds.Build);
        Ui.Settle();
        Assert.Equal(bottom, next.DockHost.RowDefinitions[2].ActualHeight, 1);
        next.Close();
    }

    /// <summary>A window's start makes no view of a tool window it hides, and no console's editor: each is built as it
    /// is first shown, by its layout kept too, with what the IDE wrote meanwhile there whole (a run's output, the caret
    /// at its end; a build's problems; a debugged program's console; an AppHost's resources; a request
    /// heard).</summary>
    private void A_hidden_tool_windows_view_is_built_only_as_it_is_first_shown_and_a_console_shows_what_was_written_before()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow window = Open(settings);
        window.KeepLayout();
        DockHost dock = window.DockHost;
        string[] built = [ToolWindowIds.Structure, ToolWindowIds.Find, ToolWindowIds.Hierarchy, ToolWindowIds.Bookmarks, ToolWindowIds.LocalHistory, ToolWindowIds.Endpoints, ToolWindowIds.NuGet, ToolWindowIds.Containers, ToolWindowIds.Terminal, ToolWindowIds.Git, ToolWindowIds.Commit,
            ToolWindowIds.Problems, ToolWindowIds.Debug, ToolWindowIds.LiveTree, ToolWindowIds.Profiler, ToolWindowIds.Aspire, ToolWindowIds.Requests, ToolWindowIds.Migrations, ToolWindowIds.StepLibrary];
        foreach (string id in built)
        {
            Assert.False(dock[id].IsBuilt, $"The {dock[id].Title} window's view was made as the window started, though it is hidden");
        }
        Passenger.App.ToolWindows.ToolViews bottom = window.BottomPanel;
        Assert.False(bottom.Run.HasEditor || bottom.Build.HasEditor || bottom.Debug.FirstConsole.HasEditor,
            "A console's editor was made as the window started, though its window is hidden");
        Assert.False(bottom.Tests.HasDetails, "The Unit Tests window's details editor was made as the window started, though it is hidden");

        bottom.Run.Append("Building…\nRunning Hello\n");
        Ui.Settle();
        Assert.False(bottom.Run.HasEditor, "Output written to the hidden Run window made its editor");
        dock.Show(ToolWindowIds.Run);
        Ui.Settle();
        Assert.Contains(bottom.Run.Editor, dock.GetVisualDescendants());
        Assert.Equal("Building…\nRunning Hello\n", bottom.Run.Document.Snapshot.ToString());
        Assert.Equal(bottom.Run.Document.Snapshot.Length, bottom.Run.Editor.Selections.Primary.Caret);
        bottom.Run.Append("Done\n");
        Assert.Equal(bottom.Run.Document.Snapshot.Length, bottom.Run.Editor.Selections.Primary.Caret);

        Passenger.App.Run.Problem broken = new(Passenger.App.Run.ProblemSeverity.Error, "The name 'x' does not exist", Path.Combine(settings, "Program.cs"), "Hello",
            new Passenger.Text.TextPosition(2, 4), "Program.cs:3");
        bottom.Problems.Show([broken]);
        bottom.Debug.Sessions.Add(new Passenger.App.ToolWindows.DebugTab("Hello", bottom.Debug.FirstConsole));
        bottom.Debug.FirstConsole.Append("Hello from the program\n");
        bottom.Debug.ShowTab(console: true);
        bottom.Debug.ShowMuted(true);
        Ui.Settle();
        Assert.False(dock[ToolWindowIds.Problems].IsBuilt || dock[ToolWindowIds.Debug].IsBuilt, "What the IDE wrote to the hidden Problems or Debug window built its view");
        dock.Show(ToolWindowIds.Problems);
        Ui.Settle();
        Control problems = dock[ToolWindowIds.Problems].Content;
        Assert.Contains(problems, dock.GetVisualDescendants());
        Assert.Contains(problems.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsChecked == true && t.Content as string == "Build  1");
        Assert.Contains(problems.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == broken.Message);
        dock.Show(ToolWindowIds.Debug);
        Ui.Settle();
        Passenger.App.ToolWindows.DebugView debug = window.DebugWindow;
        Assert.Contains(debug, dock.GetVisualDescendants());
        Assert.Contains(bottom.Debug.FirstConsole.Editor, debug.GetVisualDescendants());
        Assert.Equal("Hello from the program\n", bottom.Debug.FirstConsole.Document.Snapshot.ToString());
        Assert.Contains(debug.HeaderTools.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Hello");
        Assert.Contains(debug.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsChecked == true && ToolTip.GetTip(t) is string tip && tip.StartsWith("Mute Breakpoints", StringComparison.Ordinal));
        dock.Hide(ToolWindowIds.Debug);
        Ui.Settle();

        string appHost = Path.Combine(settings, "AppHost", "AppHost.csproj");
        window.Aspire.SetApps([new Passenger.App.Aspire.AspireApp(appHost, 42, "Running")]);
        window.Aspire.ApplyResourceSnapshot([new Passenger.App.Aspire.AspireResource(System.Text.Json.JsonDocument.Parse(
            """{"name":"apiservice","displayName":"apiservice","state":"Running","resourceType":"Project"}""").RootElement)]);
        window.RequestLog.Hear(new Passenger.Profiler.RequestBegan("heard", DateTime.UtcNow, "heard", "GET", "/orders/2", string.Empty), "Shop", null);
        window.RequestLog.Hear(new Passenger.Profiler.RequestEnded("heard", DateTime.UtcNow, "heard", 200), "Shop", null);
        Ui.Settle();
        Assert.All((string[])[ToolWindowIds.Aspire, ToolWindowIds.Requests, ToolWindowIds.Migrations],
            id => Assert.False(dock[id].IsBuilt, $"What the IDE wrote to the hidden {dock[id].Title} window built its view"));
        dock.Show(ToolWindowIds.Aspire);
        Ui.Settle();
        Passenger.App.Aspire.AspireView aspire = window.AspireWindow;
        Assert.Contains(aspire, dock.GetVisualDescendants());
        Assert.Equal("apiservice", Assert.IsType<Passenger.App.ToolWindows.RunItem<Passenger.App.Aspire.AspireRow>>(aspire.ResourceList.SelectedItem).Data.Resource.Name);
        Assert.Contains(aspire.ResourceList.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "apiservice");
        Assert.Contains(aspire.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Live · 1 resource");
        Assert.Equal("apiservice", aspire.Details.Resource?.Name);
        dock.Hide(ToolWindowIds.Aspire);
        dock.Show(ToolWindowIds.Requests);
        Ui.Settle();
        Assert.Contains(window.Requests, dock.GetVisualDescendants());
        Assert.Equal("/orders/2", Assert.IsType<Passenger.App.Endpoints.LiveRequest>(Assert.Single(window.Requests.Rows)).Path);
        Assert.Contains(window.Requests.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "/orders/2");
        dock.Hide(ToolWindowIds.Requests);
        dock.Show(ToolWindowIds.Migrations);
        Ui.Settle();
        Assert.Contains(window.MigrationsView, dock.GetVisualDescendants());
        Assert.Contains(window.MigrationsView.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == Refusals.NoEfCore);
        dock.Hide(ToolWindowIds.Migrations);
        Ui.Settle();

        dock.Show(ToolWindowIds.LiveTree);
        Ui.Settle();
        Assert.Contains(window.LiveTree, dock.GetVisualDescendants());
        Assert.Contains(window.LiveTree.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == Passenger.App.LiveTree.LiveTreeController.NothingShown);
        dock.Hide(ToolWindowIds.LiveTree);
        Ui.Settle();

        MainWindow profiled = Ui.OpenFolder(TestFolders.Create("profiled-"), languageServer: false);
        string[] followers = [ToolWindowIds.Profiler, ToolWindowIds.LiveTree, ToolWindowIds.Aspire, ToolWindowIds.Requests, ToolWindowIds.Migrations];
        Assert.All(followers, id => Assert.False(profiled.DockHost[id].IsBuilt, $"A folder's start built the {profiled.DockHost[id].Title} window's view, though it is hidden"));
        profiled.OpenFolder(TestFolders.Create("profiled-second-"));
        Ui.Settle();
        Assert.All(followers, id => Assert.False(profiled.DockHost[id].IsBuilt, $"A second folder's start built the {profiled.DockHost[id].Title} window's view, though it is hidden"));
        profiled.DockHost.Show(ToolWindowIds.Profiler);
        Ui.Settle();
        Assert.Contains(profiled.ProfilerView, profiled.DockHost.GetVisualDescendants());
        Assert.StartsWith("No snapshots yet.", profiled.ProfilerView.KeptSummary, StringComparison.Ordinal);
        profiled.Close();

        dock.Show(ToolWindowIds.Structure);
        Ui.Settle();
        Assert.Contains(window.StructureView, dock.GetVisualDescendants());
        window.Close();
        MainWindow next = Open(settings);
        next.KeepLayout();
        Ui.Settle();
        Assert.True(next.DockHost.IsShown(ToolWindowIds.Structure));
        Assert.Contains(next.StructureView, next.DockHost.GetVisualDescendants());
        Assert.All(built.Skip(1), id => Assert.False(next.DockHost[id].IsBuilt, $"The {next.DockHost[id].Title} window's view was made, though it is hidden"));
        next.Close();
    }

    private static GridSplitter Divider(DockHost dock, Func<Control, bool> place) =>
        dock.Children.First(place).GetVisualDescendants().OfType<GridSplitter>().Single();

    /// <summary>A floating window, closed as given, held only weakly here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<Window> Closed(MainWindow window, Action close)
    {
        WeakReference<Window> weak = new(Assert.Single(window.OwnedWindows));
        close();
        Ui.Settle();
        Assert.Empty(window.OwnedWindows);
        return weak;
    }

    /// <summary>Find's keys in the Run window's console find in its output, not in the file the editor shows.</summary>
    private void Find_in_the_Run_console_finds_in_its_output()
    {
        string folder = TestFolders.Create("dock-find-");
        string one = TestFolders.Write(folder, "One.cs", "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(one);
            Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Run;
            console.Sink.Write("started\nticked once\nticked twice\n");
            window.ShowToolWindow(ToolWindowIds.Run);
            Ui.WaitFor(() => console.Document.Snapshot.ToString().Contains("ticked twice", StringComparison.Ordinal), "the program's output in the Run window");
            console.Editor.Focus();
            Ui.Settle();
            Ui.Press(window, Ui.Shortcut(AppActions.Find));
            window.KeyTextInput("ticked");
            Ui.Settle();
            Assert.True(console.Find.IsVisible, "Find's keys in the Run window opened no Find over its console");
            Assert.False(window.EditorArea.FindBar.IsVisible, "Find's keys in the Run window opened Find over the editor's file");
            Assert.Equal("1/2", console.Find.CountText);
            Ui.Press(window, Ui.Shortcut(AppActions.FindNext));
            Assert.Equal("2/2", console.Find.CountText);
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    private void A_right_click_on_a_stripe_lists_the_windows_of_the_icons_place_or_of_the_place_nearest_its_empty_space(bool right)
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            ToolAnchor upper = right ? ToolAnchor.RightTop : ToolAnchor.LeftTop;
            ToolAnchor lower = right ? ToolAnchor.RightBottom : ToolAnchor.LeftBottom;
            ToolAnchor bottom = right ? ToolAnchor.BottomRight : ToolAnchor.BottomLeft;
            dock.Move(dock[ToolWindowIds.Explorer], upper);
            dock.Move(dock[ToolWindowIds.Structure], lower);
            dock.Move(dock[ToolWindowIds.Run], bottom);
            Ui.Settle();
            Control stripe = StripeBar(window, right);
            foreach (ToolAnchor place in (ToolAnchor[])[upper, lower, bottom])
            {
                ToolWindow first = dock.Windows.Where(w => w.Anchor == place).OrderBy(w => w.Order).First();
                Ui.RightClick(Button(window, first.Id));
                Assert.Equal(Titles(dock, place), Listed(dock.StripeMenu));
                dock.StripeMenu!.Close();
            }
            // Halfway between two places' nearest icons is the boundary between their lists.
            double upperEnd = PlaceButtons(window, dock, upper).Max(b => Ui.Centre(b, stripe).Y);
            double lowerStart = PlaceButtons(window, dock, lower).Min(b => Ui.Centre(b, stripe).Y);
            double lowerEnd = PlaceButtons(window, dock, lower).Max(b => Ui.Centre(b, stripe).Y);
            double bottomStart = PlaceButtons(window, dock, bottom).Min(b => Ui.Centre(b, stripe).Y);
            foreach ((double y, ToolAnchor place) in new[]
            {
                (((upperEnd + lowerStart) / 2) - 2, upper),
                (((upperEnd + lowerStart) / 2) + 2, lower),
                (((lowerEnd + bottomStart) / 2) - 2, lower),
                (((lowerEnd + bottomStart) / 2) + 2, bottom),
            })
            {
                Ui.RightClick(stripe, new Point(4, y));
                Assert.True(Titles(dock, place).SequenceEqual(Listed(dock.StripeMenu)),
                    $"A right click on the stripe's empty space at {y:0} listed [{string.Join(", ", Listed(dock.StripeMenu))}], not {ToolWindow.Name(place)}'s windows");
                dock.StripeMenu!.Close();
            }
        }
        finally
        {
            window.Close();
        }
    }

    private void A_window_unchecked_on_its_stripes_checklist_leaves_the_stripe_open_or_not_and_comes_back_checked_or_from_View()
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            dock.Show(ToolWindowIds.Structure);
            Ui.Settle();
            ToolWindow structure = dock[ToolWindowIds.Structure];
            Ui.RightClick(Button(window, ToolWindowIds.Structure));
            Assert.True(Checked(dock.StripeMenu, structure.Title), "Structure, on its stripe, is not checked on its place's checklist");
            Ui.Choose(dock.StripeMenu, structure.Title);
            Assert.True(!dock.IsShown(ToolWindowIds.Structure) && StripeButton(window, ToolWindowIds.Structure) is null,
                "Structure, open and unchecked on its stripe's checklist, stayed open or kept its stripe icon");
            Assert.Equal(structure.Anchor, dock[ToolWindowIds.Structure].Anchor);

            ToolWindow neighbour = dock.Windows.First(w => w.Anchor == structure.Anchor && w != structure);
            Ui.RightClick(Button(window, neighbour.Id));
            Assert.False(Checked(dock.StripeMenu, structure.Title), "Structure, off its stripe, is still checked on its place's checklist");
            Ui.Choose(dock.StripeMenu, structure.Title);
            Assert.True(StripeButton(window, ToolWindowIds.Structure) is not null && !dock.IsShown(ToolWindowIds.Structure),
                "Structure, checked again, did not come back to its stripe hidden");

            Ui.RightClick(Button(window, ToolWindowIds.Structure));
            Ui.Choose(dock.StripeMenu, structure.Title);
            Assert.Null(StripeButton(window, ToolWindowIds.Structure));
            NativeMenu tools = Ui.Submenu(Ui.Submenu(window.MenuBar, "View"), MainMenu.ToolWindows);
            tools.Items.OfType<NativeMenuItem>().Single(i => i.Header == structure.Title).Command!.Execute(null);
            Ui.Settle();
            Assert.True(dock.IsShown(ToolWindowIds.Structure) && StripeButton(window, ToolWindowIds.Structure) is not null,
                "View > Tool Windows > Structure did not open it on its stripe again");
        }
        finally
        {
            window.Close();
        }
    }

    private void A_place_with_every_window_off_the_stripe_is_on_its_stripes_checklist_through_a_restart_until_Restore_Default_Layout()
    {
        string state = TestFolders.Create("stripe-checklist-");
        try
        {
            MainWindow window = Open(state);
            window.KeepLayout();
            DockHost dock = window.DockHost;
            ToolAnchor emptied = dock[ToolWindowIds.Structure].Anchor;
            foreach (ToolWindow each in dock.Windows.Where(w => w.Anchor == emptied).ToList())
            {
                Ui.RightClick(Button(window, each.Id));
                Ui.Choose(dock.StripeMenu, each.Title);
            }
            Assert.DoesNotContain(window.DockHost.GetVisualDescendants().OfType<ToggleButton>(), b => b.Tag is string id && dock[id].Anchor == emptied);
            window.Close();
            Ui.Settle();

            MainWindow next = Open(state);
            try
            {
                next.KeepLayout();
                Ui.Settle();
                dock = next.DockHost;
                Assert.DoesNotContain(dock.GetVisualDescendants().OfType<ToggleButton>(), b => b.Tag is string id && dock[id].Anchor == emptied);
                Ui.RightClick(Button(next, ToolWindowIds.Explorer));
                MenuItem place = dock.StripeMenu!.Items.OfType<MenuItem>().SingleOrDefault(i => Equals(i.Header, ToolWindow.Name(emptied)))
                    ?? throw new Xunit.Sdk.XunitException($"The left stripe's checklist does not offer {ToolWindow.Name(emptied)}, whose windows are all off it: [{string.Join(", ", Listed(dock.StripeMenu))}]");
                Assert.Equal(Titles(dock, emptied), place.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
                Assert.All(place.Items.OfType<MenuItem>(), i => Assert.False(i.IsChecked));
                Ui.Choose(dock.StripeMenu, ToolWindow.Name(emptied), "Structure");
                Assert.NotNull(StripeButton(next, ToolWindowIds.Structure));

                dock.RestoreDefault();
                Ui.Settle();
                Assert.All(dock.Windows, w => Assert.False(w.IsClosed, $"{w.Title} stayed off its stripe after Restore Default Layout"));
            }
            finally
            {
                next.Close();
            }
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    /// <summary>A stripe whose every icon the person unchecked on its checklists stays shown, holding none, where it folded
    /// away with them and its checklist could no longer be opened: a right click on it lists its places' windows, and one
    /// checked there comes back to it.</summary>
    private void A_stripe_whose_every_icon_was_unchecked_stays_and_its_checklist_puts_one_back()
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            Control stripe = StripeBar(window, right: true);
            for (int taken = 0; taken < dock.Windows.Count && Icons(stripe).FirstOrDefault() is { } icon; taken++)
            {
                Ui.RightClick(icon);
                Ui.Choose(dock.StripeMenu, dock[(string)icon.Tag!].Title);
            }
            Assert.Empty(Icons(stripe));
            Assert.True(dock.ColumnDefinitions[6].ActualWidth == 40 && stripe.IsEffectivelyVisible,
                $"The right stripe, every icon unchecked, takes {dock.ColumnDefinitions[6].ActualWidth:0.#} units: its checklist is out of reach");

            Ui.RightClick(stripe, new Point(stripe.Bounds.Width / 2, 60));
            string title = Listed(dock.StripeMenu).FirstOrDefault()
                ?? throw new Xunit.Sdk.XunitException("A right click on the emptied stripe lists no window to put back");
            Ui.Choose(dock.StripeMenu, title);
            ToolWindow back = dock.Windows.Single(w => w.Title == title);
            Assert.True(StripeButton(window, back.Id) is { IsEffectivelyVisible: true } && !back.IsClosed,
                $"{title}, checked on the emptied stripe's checklist, has no icon on it");
        }
        finally
        {
            window.Close();
        }

        static IEnumerable<ToggleButton> Icons(Control stripe) =>
            stripe.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Tag is string && b.IsEffectivelyVisible);
    }

    private void A_stripe_button_dropped_between_two_of_its_places_lands_between_them_past_a_window_off_the_stripe_and_its_own_place()
    {
        MainWindow window = Open();
        try
        {
            // Run, the place's first, goes down past its own place.
            DropBetween(window, ToolWindowIds.Run, ToolWindowIds.Tests, ToolWindowIds.Build);
            // Debug, off the stripe, stands before Aspire in the place's order; Problems comes from below.
            window.DockHost.SetOnStripe(ToolWindowIds.Debug, false);
            Ui.Settle();
            DropBetween(window, ToolWindowIds.Problems, ToolWindowIds.Aspire, ToolWindowIds.Tests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Drags a window's stripe button to halfway between two others' and checks it now stands between them.</summary>
    private static void DropBetween(MainWindow window, string id, string above, string below)
    {
        Point to = (Ui.Centre(Button(window, above), window) + Ui.Centre(Button(window, below), window)) / 2;
        Ui.Drag(window, Ui.Centre(Button(window, id), window), to);
        DockHost dock = window.DockHost;
        string[] shown = [.. PlaceButtons(window, dock, dock[above].Anchor).OrderBy(b => Ui.Centre(b, window).Y).Select(b => (string)b.Tag!)];
        int at = Array.IndexOf(shown, id);
        Assert.True(at > 0 && at < shown.Length - 1 && shown[at - 1] == above && shown[at + 1] == below,
            $"{id} dropped between {above} and {below} stands in [{string.Join(", ", shown)}]");
    }

    /// <summary>The titles of a place's windows in their order, as its checklist lists them.</summary>
    private static IEnumerable<string> Titles(DockHost dock, ToolAnchor place) =>
        dock.Windows.Where(w => w.Anchor == place).OrderBy(w => w.Order).Select(w => w.Title);

    /// <summary>What a stripe's checklist lists, its windows' titles.</summary>
    private static List<string> Listed(ContextMenu? menu) =>
        [.. (menu ?? throw new Xunit.Sdk.XunitException("The right click opened no checklist")).Items.OfType<MenuItem>().TakeWhile(i => i.ToggleType == MenuItemToggleType.CheckBox).Select(i => (string)i.Header!)];

    private static bool Checked(ContextMenu? menu, string title) =>
        (menu ?? throw new Xunit.Sdk.XunitException("The right click opened no checklist")).Items.OfType<MenuItem>().Single(i => Equals(i.Header, title)).IsChecked;

    /// <summary>A place's stripe icons, shown.</summary>
    private static IEnumerable<ToggleButton> PlaceButtons(MainWindow window, DockHost dock, ToolAnchor place) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Tag is string id && dock[id].Anchor == place && b.IsEffectivelyVisible);

    /// <summary>A window's stripe icon; null where it has none.</summary>
    private static ToggleButton? StripeButton(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(b => Equals(b.Tag, id));

    /// <summary>The left or right stripe itself, whose empty space a right click lands on.</summary>
    private static Control StripeBar(MainWindow window, bool right) =>
        window.DockHost.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.StripeBar))
            .OrderBy(b => Ui.Centre(b, window).X).ToList()[right ? 1 : 0];

    /// <summary>A window, keeping its state in a folder of the test's own when it is given one.</summary>
    private static MainWindow Open(string? state = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state ?? Passenger.App.State.StateStore.DefaultRoot };
        window.Show();
        // The dock's own ways, with every window in it, whatever a folder would bring.
        Ui.ServeEveryWindow(window);
        return window;
    }

    private static ToggleButton Button(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => Equals(b.Tag, id));

    private static IEnumerable<ToggleButton> Stripe(MainWindow window, bool right) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>()
            .Where(b => b.Tag is string && (Ui.Centre(b, window).X > window.Bounds.Width / 2) == right);

    /// <summary>A tool window's header, by the title on it.</summary>
    private static Control Header(MainWindow window, string title) =>
        window.DockHost.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == title && t.FontWeight == Avalonia.Media.FontWeight.SemiBold)
            .FindAncestorOfType<Border>()!;
}
