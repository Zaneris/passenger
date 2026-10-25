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

/// <summary>Tool windows docked: a stripe button shows its window and hides the one in its place, a button or a header
/// dragged elsewhere moves the window there, and the person's window opens on the layout they left, fitted to the
/// window it opens in.</summary>
public sealed class DockingTests
{
    [AvaloniaFact]
    public void A_stripe_button_shows_its_window_in_its_place_and_hides_the_one_there()
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

    [AvaloniaFact]
    public void A_stripe_button_dragged_to_the_other_stripe_docks_its_window_on_that_side()
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

    [AvaloniaFact]
    public void A_drop_above_the_first_lower_button_of_a_stripe_with_no_upper_ones_docks_in_its_upper_group()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Move(dock[ToolWindowIds.Hierarchy], ToolAnchor.RightBottom);
        dock.Move(dock[ToolWindowIds.LiveTree], ToolAnchor.LeftBottom);
        dock.Move(dock[ToolWindowIds.Endpoints], ToolAnchor.LeftBottom);
        dock.Move(dock[ToolWindowIds.Requests], ToolAnchor.LeftBottom);
        Ui.Settle();
        Assert.Single(Stripe(window, right: true));
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

    [AvaloniaFact]
    public void A_header_dragged_onto_the_bottom_right_docks_the_window_there_beside_the_bottom_left_one()
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

    [AvaloniaFact]
    public void The_persons_window_opens_on_the_layout_they_left()
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

    [AvaloniaFact]
    public void A_floating_tool_window_takes_its_content_to_a_window_of_its_own_and_back()
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

    [AvaloniaFact]
    public void Maximize_gives_a_tool_window_the_frame_and_hide_all_clears_it_for_the_editor()
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

    [AvaloniaFact]
    public void A_tool_window_maximised_when_the_window_closes_opens_again_at_the_size_it_had_before()
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

    [AvaloniaFact]
    public void A_floating_window_closed_is_let_go_and_not_kept_alive_by_its_tool_window()
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
    public void The_frames_dividers_take_the_pointer_on_either_side_of_their_line_and_a_closed_areas_divider_takes_none()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        dock.Show(ToolWindowIds.Explorer);
        dock.Show(ToolWindowIds.Hierarchy);
        dock.Show(ToolWindowIds.Build);
        Ui.Settle();
        (double left, double right, double bottom) = (dock.ColumnDefinitions[1].ActualWidth, dock.ColumnDefinitions[5].ActualWidth, dock.RowDefinitions[2].ActualHeight);

        // Each grabbed 3 px past its line, over the area after it.
        Point grip = Ui.Centre(Divider(dock, c => Grid.GetColumn(c) == 2), window) + new Point(3, 0);
        Ui.Drag(window, grip, grip + new Point(60, 0));
        Assert.True(Math.Abs(dock.ColumnDefinitions[1].ActualWidth - (left + 60)) < 1,
            $"The left area was {left} wide, and {dock.ColumnDefinitions[1].ActualWidth} once its divider, grabbed right of its line, was dragged 60 px right");
        grip = Ui.Centre(Divider(dock, c => Grid.GetColumn(c) == 4), window) + new Point(3, 0);
        Ui.Drag(window, grip, grip - new Point(60, 0));
        Assert.True(Math.Abs(dock.ColumnDefinitions[5].ActualWidth - (right + 60)) < 1,
            $"The right area was {right} wide, and {dock.ColumnDefinitions[5].ActualWidth} once its divider, grabbed right of its line, was dragged 60 px left");
        grip = Ui.Centre(Divider(dock, c => Grid.GetRow(c) == 1), window) + new Point(0, 3);
        Ui.Drag(window, grip, grip - new Point(0, 60));
        Assert.True(Math.Abs(dock.RowDefinitions[2].ActualHeight - (bottom + 60)) < 1,
            $"The bottom area was {bottom} tall, and {dock.RowDefinitions[2].ActualHeight} once its divider, grabbed below its line, was dragged 60 px up");

        dock.Hide(ToolWindowIds.Hierarchy);
        Ui.Settle();
        Control center = dock.Center!;
        Point edge = center.TranslatePoint(new Point(center.Bounds.Width - 2, center.Bounds.Height / 2), window)!.Value;
        Ui.Drag(window, edge, edge - new Point(60, 0));
        Assert.True(dock.ColumnDefinitions[5].ActualWidth == 0,
            $"A drag at the editor's right edge, the right area closed, opened {dock.ColumnDefinitions[5].ActualWidth} px of empty area there");
        window.Close();
    }

    [AvaloniaFact]
    public void The_unit_tests_debug_and_git_windows_draw_a_line_between_their_panes()
    {
        MainWindow window = Open();
        DockHost dock = window.DockHost;
        Avalonia.Media.Color line = Application.Current!.TryFindResource(TokenKeys.Divider, window.ActualThemeVariant, out object? brush) && brush is Avalonia.Media.ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException("The theme has no divider colour");
        foreach (string id in new[] { ToolWindowIds.Tests, ToolWindowIds.Debug, ToolWindowIds.Git })
        {
            dock.Show(id);
            Ui.Settle();
            GridSplitter[] splitters = [.. dock[id].Content.GetVisualDescendants().OfType<GridSplitter>().Where(s => s.IsEffectivelyVisible)];
            Assert.NotEmpty(splitters);
            foreach (GridSplitter splitter in splitters)
            {
                Avalonia.Media.Color shown = Ui.Pixel(window, Ui.Centre(splitter, window));
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
        Assert.Equal(first, dock.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order)));
        Assert.All(dock.Windows, w => Assert.Equal(ToolMode.Docked, w.Mode));
        Assert.Empty(window.OwnedWindows);
        Assert.Equal([ToolWindowIds.Explorer], dock.Windows.Where(w => w.IsShown).Select(w => w.Id));
        Assert.Contains(Button(window, ToolWindowIds.Debug), Stripe(window, right: false));
        window.Close();

        MainWindow next = Open(settings);
        next.KeepLayout();
        Assert.Equal(first, next.DockHost.Windows.ToDictionary(w => w.Id, w => (w.Anchor, w.Order)));
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
    [AvaloniaFact]
    public void A_hidden_tool_windows_view_is_built_only_as_it_is_first_shown_and_a_console_shows_what_was_written_before()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow window = Open(settings);
        window.KeepLayout();
        DockHost dock = window.DockHost;
        string[] built = [ToolWindowIds.Structure, ToolWindowIds.Find, ToolWindowIds.Hierarchy, ToolWindowIds.Bookmarks, ToolWindowIds.LocalHistory, ToolWindowIds.Endpoints, ToolWindowIds.NuGet, ToolWindowIds.Containers, ToolWindowIds.Terminal, ToolWindowIds.Git, ToolWindowIds.Commit,
            ToolWindowIds.Problems, ToolWindowIds.Debug, ToolWindowIds.LiveTree, ToolWindowIds.Profiler, ToolWindowIds.Aspire, ToolWindowIds.Requests, ToolWindowIds.Migrations];
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
        Assert.Contains(window.MigrationsView.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "No project here uses EF Core");
        dock.Hide(ToolWindowIds.Migrations);
        Ui.Settle();

        dock.Show(ToolWindowIds.LiveTree);
        Ui.Settle();
        Assert.Contains(window.LiveTree, dock.GetVisualDescendants());
        Assert.Contains(window.LiveTree.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Debug an Avalonia or WPF app to see its windows and controls here.");
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
            .Where(b => b.Tag is string && (Ui.Centre(b, window).X > window.Bounds.Width / 2) == right);

    /// <summary>A tool window's header, by the title on it.</summary>
    private static Control Header(MainWindow window, string title) =>
        window.DockHost.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == title && t.FontWeight == Avalonia.Media.FontWeight.SemiBold)
            .FindAncestorOfType<Border>()!;
}
