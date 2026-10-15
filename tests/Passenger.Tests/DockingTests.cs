using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;

namespace Passenger.Tests;

/// <summary>Tool windows as IntelliJ docks them: a stripe button shows its window and hides the one in its place,
/// a button or a header dragged elsewhere moves the window there, and the person's window opens on the layout
/// they left, fitted to the window it opens in.</summary>
public sealed class DockingTests
{
    [AvaloniaFact]
    public void A_stripe_button_shows_its_window_in_its_place_and_hides_the_one_there()
    {
        MainWindow window = Open();
        Click(window, Button(window, "debug"));
        Assert.True(window.DockHost.IsShown("debug"));
        Click(window, Button(window, "run"));
        Assert.True(window.DockHost.IsShown("run"));
        Assert.False(window.DockHost.IsShown("debug"));
        Assert.True(Button(window, "run").IsChecked);
        Assert.False(Button(window, "debug").IsChecked);

        // Its button again closes it.
        Click(window, Button(window, "run"));
        Assert.False(window.DockHost.IsShown("run"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_stripe_button_dragged_to_the_other_stripe_docks_its_window_on_that_side()
    {
        MainWindow window = Open();
        window.DockHost.Show("debug");
        Ui.Settle();
        ToggleButton debug = Button(window, "debug");
        Point from = Middle(window, debug);
        Point to = new(window.Bounds.Width - 20, 120);
        Drag(window, from, to);
        ToolWindow moved = window.DockHost["debug"]!;
        Assert.Equal(ToolAnchor.RightTop, moved.Anchor);
        // A shown window stays shown where it went, beside the editor and no longer under it.
        Assert.True(moved.IsShown);
        Assert.True(window.DockHost.ColumnDefinitions[5].ActualWidth > 100);
        Assert.Equal(0, window.DockHost.RowDefinitions[2].ActualHeight);
        Assert.Contains(Button(window, "debug"), Stripe(window, right: true));
        window.Close();
    }

    [AvaloniaFact]
    public void A_drop_above_the_first_lower_button_of_a_stripe_with_no_upper_ones_docks_in_its_upper_group()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Move(dock["hierarchy"]!, ToolAnchor.RightBottom);
        dock.Move(dock["liveTree"]!, ToolAnchor.LeftBottom);
        Ui.Settle();
        Assert.Single(Stripe(window, right: true));
        Point lowest = Middle(window, Button(window, "hierarchy"));

        Drag(window, Middle(window, Button(window, "debug")), lowest - new Point(0, 4));
        Assert.Equal(ToolAnchor.RightTop, dock["debug"]!.Anchor);

        // Below the middle of the lower group's first button, a drop joins that group.
        dock.Move(dock["debug"]!, ToolAnchor.BottomLeft);
        Ui.Settle();
        Drag(window, Middle(window, Button(window, "run")), Middle(window, Button(window, "hierarchy")) + new Point(0, 4));
        Assert.Equal(ToolAnchor.RightBottom, dock["run"]!.Anchor);
        window.Close();
    }

    [AvaloniaFact]
    public void A_header_dragged_onto_the_bottom_right_docks_the_window_there_beside_the_bottom_left_one()
    {
        MainWindow window = Open();
        window.DockHost.Show("run");
        Ui.Settle();
        Control header = Header(window, "Explorer");
        Point from = Middle(window, header);
        Point to = new(window.Bounds.Width * 0.8, window.Bounds.Height * 0.85);
        Drag(window, from, to);
        Assert.Equal(ToolAnchor.BottomRight, window.DockHost["explorer"]!.Anchor);
        Assert.True(window.DockHost.IsShown("explorer"));
        Assert.True(window.DockHost.IsShown("run"));
        Assert.Equal(0, window.DockHost.ColumnDefinitions[1].ActualWidth);
        window.Close();
    }

    [AvaloniaFact]
    public void The_persons_window_opens_on_the_layout_they_left()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = Open(settings);
        first.KeepLayout();
        first.DockHost.Move(first.DockHost["tests"]!, ToolAnchor.RightBottom);
        first.DockHost.Show("tests");
        first.DockHost.Hide("explorer");
        first.Close();

        MainWindow second = Open(settings);
        second.KeepLayout();
        Ui.Settle();
        Assert.Equal(ToolAnchor.RightBottom, second.DockHost["tests"]!.Anchor);
        Assert.True(second.DockHost.IsShown("tests"));
        Assert.False(second.DockHost.IsShown("explorer"));
        second.Close();
    }

    [AvaloniaFact]
    public void A_floating_tool_window_takes_its_content_to_a_window_of_its_own_and_back()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show("debug");
        ToolWindow debug = dock["debug"]!;
        dock.SetMode(debug, ToolMode.Floating);
        Ui.Settle();
        Window floating = Assert.Single(window.OwnedWindows);
        Assert.Contains(window.BottomPanel.Debug, floating.GetVisualDescendants());
        Assert.Equal(0, dock.RowDefinitions[2].ActualHeight);
        Assert.True(debug.IsShown);

        // Its stripe button hides it, closing its window; again, and it floats again.
        dock.Toggle("debug");
        Assert.Empty(window.OwnedWindows);
        dock.Toggle("debug");
        Assert.Single(window.OwnedWindows);

        // Closed by the system, it is hidden, not lost; docked again, it is in its place.
        window.OwnedWindows[0].Close();
        Ui.Settle();
        Assert.False(debug.IsShown);
        dock.SetMode(debug, ToolMode.Docked);
        dock.Show("debug");
        Ui.Settle();
        Assert.True(dock.RowDefinitions[2].ActualHeight > 100);
        Assert.Contains(window.BottomPanel.Debug, dock.GetVisualDescendants());
        window.Close();
    }

    [AvaloniaFact]
    public void Maximize_gives_a_tool_window_the_frame_and_hide_all_clears_it_for_the_editor()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show("debug");
        Ui.Settle();
        double before = dock.RowDefinitions[2].ActualHeight;
        Ui.Run(window, AppActions.MaximizeToolWindow);
        Assert.True(dock.RowDefinitions[2].ActualHeight > window.Bounds.Height * 0.75);
        Ui.Run(window, AppActions.MaximizeToolWindow);
        Assert.Equal(before, dock.RowDefinitions[2].ActualHeight, 1);

        // Hide All closes every docked window; again, and they are all back.
        Ui.Run(window, AppActions.HideAllToolWindows);
        Assert.DoesNotContain(dock.Windows, w => w.IsShown);
        Ui.Run(window, AppActions.HideAllToolWindows);
        Assert.True(dock.IsShown("debug"));
        Assert.True(dock.IsShown("explorer"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_tool_window_maximised_when_the_window_closes_opens_again_at_the_size_it_had_before()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = Open(settings);
        first.KeepLayout();
        DockHost dock = first.DockHost;
        dock.Show("build");
        Ui.Settle();
        // The person's own size, dragged above the default.
        Control splitter = dock.Children.First(c => Grid.GetRow(c) == 1).GetVisualDescendants().OfType<GridSplitter>().Single();
        Point grip = Middle(first, splitter);
        Drag(first, grip, grip - new Point(0, 60));
        double restored = dock.RowDefinitions[2].ActualHeight;
        Assert.True(restored > 320, $"The bottom area was not dragged taller: {restored}");
        Ui.Run(first, AppActions.MaximizeToolWindow);
        // A window shown while it is maximised keeps the layout as well.
        dock.Show("structure");
        Ui.Settle();
        Assert.True(dock.RowDefinitions[2].ActualHeight > restored + 100);
        first.Close();

        MainWindow second = Open(settings);
        second.KeepLayout();
        Ui.Settle();
        Assert.True(second.DockHost.IsShown("build"));
        Assert.False(second.DockHost.IsMaximized);
        Assert.True(Math.Abs(second.DockHost.RowDefinitions[2].ActualHeight - restored) < 1,
            $"The Build window opened {second.DockHost.RowDefinitions[2].ActualHeight} tall, the maximised height kept, not the {restored} it had before");
        second.Close();
    }

    [AvaloniaFact]
    public void A_floating_window_closed_is_let_go_and_not_kept_alive_by_its_tool_window()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show("debug");
        dock.SetMode(dock["debug"]!, ToolMode.Floating);
        Ui.Settle();
        // Hidden by its stripe button, and closed by the system.
        WeakReference<Window> hidden = Closed(window, () => dock.Toggle("debug"));
        dock.Toggle("debug");
        Ui.Settle();
        WeakReference<Window> closed = Closed(window, () => window.OwnedWindows[0].Close());
        dock.SetMode(dock["debug"]!, ToolMode.Docked);
        dock.Show("debug");
        Ui.Settle();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(hidden.TryGetTarget(out _), "The floating window hidden by its button is still alive, held by its tool window");
        Assert.False(closed.TryGetTarget(out _), "The floating window the system closed is still alive, held by its tool window");
        Assert.Contains(window.BottomPanel.Debug, dock.GetVisualDescendants());
        window.Close();
    }

    [AvaloniaFact]
    public void Areas_kept_on_a_larger_screen_leave_the_editor_its_share_of_a_smaller_window_and_come_back_on_a_larger()
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
        // The sides keep the proportion they were kept in, and the right stripe is still in the window.
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

    [AvaloniaFact]
    public void Half_a_laptop_screen_collapses_the_other_side_without_losing_its_windows_sizes_or_editor_selection()
    {
        string folder = TestFolders.Create("narrow-dock-");
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Open();
        try
        {
            window.OpenFolder(folder);
            window.OpenFile("One.cs");
            DockHost dock = window.DockHost;
            dock.Show("hierarchy");
            Ui.Settle();
            DockLayout before = dock.Capture();
            object selected = window.EditorArea.Active!;
            window.ExplorerView.FocusRows();
            Assert.True(window.ExplorerView.IsKeyboardFocusWithin);
            foreach (double width in new double[] { 683, 640 })
            {
                window.Width = width;
                Ui.Settle();
                Assert.True(dock.Center!.Bounds.Width >= 320, $"At {width}px the two side panes crowded the editor down to {dock.Center.Bounds.Width}px");
                Assert.True(dock.IsShown("hierarchy") && !dock.IsShown("explorer"), "The most recently opened side must stay visible while the other collapses");
                Assert.True(Button(window, "hierarchy").IsChecked == true && Button(window, "explorer").IsChecked == false,
                    "The stripes must distinguish the visible side from the temporarily collapsed one");
                Assert.Equal(0, dock.ColumnDefinitions[1].ActualWidth);
                Assert.Same(selected, window.EditorArea.Active);
                Assert.Equal(before, dock.Capture() with { Windows = before.Windows });
                Assert.Equal(before.Windows, dock.Capture().Windows);
                if (width == 683)
                {
                    Assert.True(window.EditorArea.CodeEditor.IsFocused, "Collapsing the focused Explorer must give the visible editor the keys");
                }

                // The collapsed stripe reveals its remembered window rather than toggling the saved choice off.
                Click(window, Button(window, "explorer"));
                Assert.True(dock.IsShown("explorer") && !dock.IsShown("hierarchy"));
                Assert.True(dock["hierarchy"]!.IsShown, "Revealing the other side must not close the remembered hierarchy window");
                Assert.True(dock.Center.Bounds.Width >= 320);
                Click(window, Button(window, "hierarchy"));
                Assert.True(dock.IsShown("hierarchy") && !dock.IsShown("explorer"));
            }

            window.Width = 1200;
            window.EditorArea.CodeEditor.Focus();
            Ui.Settle();
            Assert.True(dock.IsShown("explorer") && dock.IsShown("hierarchy"), "Both side panes must return when there is room again");
            Assert.Equal(before.LeftWidth, dock.ColumnDefinitions[1].ActualWidth);
            Assert.Equal(before.RightWidth, dock.ColumnDefinitions[5].ActualWidth);
            Assert.Equal(before.Windows, dock.Capture().Windows);
            Assert.Same(selected, window.EditorArea.Active);
            Assert.True(window.EditorArea.CodeEditor.IsFocused, "Restoring a collapsed pane on resize must not take the editor's keys");

            window.Width = 640;
            Ui.Settle();
            dock.Hide("explorer");
            window.Width = 1200;
            Ui.Settle();
            Assert.False(dock.IsShown("explorer"), "Explicitly closing a collapsed pane must remove its restore intent");
            dock.Show("explorer");
            window.Width = 640;
            Ui.Settle();
            dock.Hide("explorer");
            Ui.Settle();
            Assert.True(dock.IsShown("hierarchy"), "Closing the visible left pane must reveal the remembered right pane");
            dock.Show("explorer");
            dock.Show("hierarchy");
            dock.Hide("hierarchy");
            Ui.Settle();
            Assert.True(dock.IsShown("explorer"), "Explicitly hiding the visible side must reveal the other remembered side");
            window.Width = 1200;
            Ui.Settle();
            Assert.False(dock.IsShown("hierarchy"), "A side explicitly closed at narrow width must not reopen on resize");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void The_frames_dividers_take_the_pointer_on_either_side_of_their_line_and_a_closed_areas_divider_takes_none()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show("explorer");
        dock.Show("hierarchy");
        dock.Show("build");
        Ui.Settle();
        (double left, double right, double bottom) = (dock.ColumnDefinitions[1].ActualWidth, dock.ColumnDefinitions[5].ActualWidth, dock.RowDefinitions[2].ActualHeight);

        // Each grabbed 3 px past its line, over the area after it.
        Point grip = Middle(window, Divider(dock, c => Grid.GetColumn(c) == 2)) + new Point(3, 0);
        Drag(window, grip, grip + new Point(60, 0));
        Assert.True(Math.Abs(dock.ColumnDefinitions[1].ActualWidth - (left + 60)) < 1,
            $"The left area was {left} wide, and {dock.ColumnDefinitions[1].ActualWidth} once its divider, grabbed right of its line, was dragged 60 px right");
        grip = Middle(window, Divider(dock, c => Grid.GetColumn(c) == 4)) + new Point(3, 0);
        Drag(window, grip, grip - new Point(60, 0));
        Assert.True(Math.Abs(dock.ColumnDefinitions[5].ActualWidth - (right + 60)) < 1,
            $"The right area was {right} wide, and {dock.ColumnDefinitions[5].ActualWidth} once its divider, grabbed right of its line, was dragged 60 px left");
        grip = Middle(window, Divider(dock, c => Grid.GetRow(c) == 1)) + new Point(0, 3);
        Drag(window, grip, grip - new Point(0, 60));
        Assert.True(Math.Abs(dock.RowDefinitions[2].ActualHeight - (bottom + 60)) < 1,
            $"The bottom area was {bottom} tall, and {dock.RowDefinitions[2].ActualHeight} once its divider, grabbed below its line, was dragged 60 px up");

        // Closed, the right area leaves the editor's edge to the editor: a drag there opens no empty area.
        dock.Hide("hierarchy");
        Ui.Settle();
        Control center = dock.Center!;
        Point edge = center.TranslatePoint(new Point(center.Bounds.Width - 2, center.Bounds.Height / 2), window)!.Value;
        Drag(window, edge, edge - new Point(60, 0));
        Assert.True(dock.ColumnDefinitions[5].ActualWidth == 0,
            $"A drag at the editor's right edge, the right area closed, opened {dock.ColumnDefinitions[5].ActualWidth} px of empty area there");
        window.Close();
    }

    [AvaloniaFact]
    public void The_unit_tests_debug_and_git_windows_draw_a_line_between_their_panes()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        Avalonia.Media.Color line = Application.Current!.TryFindResource("Passenger.Divider", window.ActualThemeVariant, out object? brush) && brush is Avalonia.Media.ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException("The theme has no divider colour");
        foreach (string id in new[] { "tests", "debug", "git" })
        {
            dock.Show(id);
            Ui.Settle();
            GridSplitter[] splitters = [.. dock[id]!.Content.GetVisualDescendants().OfType<GridSplitter>().Where(s => s.IsEffectivelyVisible)];
            Assert.NotEmpty(splitters);
            foreach (GridSplitter splitter in splitters)
            {
                Avalonia.Media.Color shown = Ui.Pixel(window, Middle(window, splitter));
                Assert.True(shown == line, $"The {id} window is {shown} on the divider between its panes, not the divider colour {line}");
            }
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Restore_Default_Layout_puts_every_tool_window_back_where_it_first_was_docked_at_its_first_size()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow window = Open(settings);
        window.KeepLayout();
        DockHost dock = window.DockHost;
        Dictionary<string, (ToolAnchor Anchor, int Order)> first = dock.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order));
        dock.Show("build");
        Ui.Settle();
        double bottom = dock.RowDefinitions[2].ActualHeight;
        dock.Hide("build");
        dock.Show("explorer");

        // Moved, floated, hidden, shown and resized.
        Drag(window, Middle(window, Button(window, "debug")), new Point(window.Bounds.Width - 20, 120));
        dock.Move(dock["tests"]!, ToolAnchor.LeftBottom, 0);
        dock.Show("tests");
        dock.Show("run");
        dock.SetMode(dock["run"]!, ToolMode.Floating);
        dock.Hide("explorer");
        dock.Show("build");
        Ui.Settle();
        Point divider = Middle(window, Divider(dock, c => Grid.GetRow(c) == 1)) + new Point(0, 3);
        Drag(window, divider, divider - new Point(0, 150));
        Assert.Equal(ToolAnchor.RightTop, dock["debug"]!.Anchor);
        Assert.Single(window.OwnedWindows);
        Assert.True(dock.RowDefinitions[2].ActualHeight > bottom + 100, $"The bottom area dragged 150 px taller is {dock.RowDefinitions[2].ActualHeight} tall, from {bottom}");

        // Window > Layout Settings > Restore Default Layout.
        NativeMenuItem restore = Ui.Submenu(Ui.Submenu(window.MenuBar, "Window"), "Layout Settings").Items.OfType<NativeMenuItem>().Single(i => i.Header == "Restore Default Layout");
        restore.Command!.Execute(null);
        Ui.Settle();
        Assert.Equal(first, dock.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order)));
        Assert.All(dock.Windows, w => Assert.Equal(ToolMode.Docked, w.Mode));
        Assert.Empty(window.OwnedWindows);
        Assert.Equal(["explorer"], dock.Windows.Where(w => w.IsShown).Select(w => w.Id));
        Assert.Contains(Button(window, "debug"), Stripe(window, right: false));
        window.Close();

        // The layout restored is the one kept.
        MainWindow next = Open(settings);
        next.KeepLayout();
        Assert.Equal(first, next.DockHost.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order)));
        Assert.Equal(["explorer"], next.DockHost.Windows.Where(w => w.IsShown).Select(w => w.Id));
        next.DockHost.Show("build");
        Ui.Settle();
        Assert.Equal(bottom, next.DockHost.RowDefinitions[2].ActualHeight, 1);
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

    /// <summary>A window, keeping its state in a folder of the test's own when it is given one.</summary>
    private static MainWindow Open(string? state = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state ?? Passenger.App.State.StateStore.DefaultRoot };
        window.Show();
        Ui.Settle();
        return window;
    }

    private static ToggleButton Button(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => Equals(b.Tag, id));

    private static IEnumerable<ToggleButton> Stripe(MainWindow window, bool right) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>()
            .Where(b => b.Tag is string && (Middle(window, b).X > window.Bounds.Width / 2) == right);

    /// <summary>A tool window's header, by the title on it.</summary>
    private static Control Header(MainWindow window, string title) =>
        window.DockHost.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == title && t.FontWeight == Avalonia.Media.FontWeight.SemiBold)
            .FindAncestorOfType<Border>()!;

    private static Point Middle(MainWindow window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(MainWindow window, Control control)
    {
        Point at = Middle(window, control);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }

    private static void Drag(MainWindow window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(from + ((to - from) * (step / 10.0)), RawInputModifiers.LeftMouseButton);
        }
        window.MouseUp(to, MouseButton.Left);
        Ui.Settle();
    }
}
