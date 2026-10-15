using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The toolbar is the main window's title bar, as Rider's main toolbar is: the window's title (solution and
/// file) still named for the taskbar, Alt+Tab and Mission Control, the bar's empty space moving the window and a double
/// click on it maximizing it, what is on the bar keeping its own clicks; the window's buttons at its right on Windows and
/// Linux, macOS's traffic lights' room at its left; and on Linux alone Rider's choice of the merged bar, on at first, a
/// right press on it showing the window manager's menu. Where Windows decides what a press in the bar is (its caption, a
/// button, the bar's own), the roles it asks are checked, as no test sees the system's own window.</summary>
public sealed class TitleBarTests : IDisposable
{
    private readonly string folder = TestFolders.Create("title-bar-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void The_toolbar_is_the_title_bar_whose_empty_space_moves_the_window_and_a_double_click_on_it_maximizes_it()
    {
        MainWindow window = Open();
        Assert.SkipUnless(window.TitleBarMerged, $"This desktop keeps the system's title bar: {TitleBars.NotOnThisDesktop}");
        Assert.True(window.ExtendClientAreaToDecorationsHint, "The window did not extend into its frame, so the system's title bar stays over the toolbar");
        // The name the taskbar, Alt+Tab and Mission Control show, which the bar does not draw.
        Assert.Equal($"{Path.GetFileName(folder)} – One.cs", window.Title);
        Assert.DoesNotContain(Bar(window).GetVisualDescendants().OfType<TextBlock>(), text => text.Text == window.Title);

        // Windows takes a press on the bar's empty space as one on its own caption: moving, snapping, the window menu.
        Point empty = EmptySpace(window);
        Assert.Equal(OperatingSystem.IsWindows() ? WindowDecorationsElementRole.TitleBar : null, ChromeAt(window, empty));
        Click(window, empty);
        Assert.True(window.MovesStarted == 1, "A press on the bar's empty space did not move the window");

        // What is on the bar keeps its clicks: Windows hands them to it, and a click on Settings opens Settings.
        foreach (Button button in Bar(window).GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && !b.Classes.Contains("windowButton")))
        {
            Assert.True(ChromeAt(window, Centre(window, button)) == WindowDecorationsElementRole.User,
                $"Windows would take a press on the toolbar's {button.Name ?? ToolTip.GetTip(button)} as one on its caption, moving the window instead of clicking it");
        }
        Button settings = Bar(window).GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Settings");
        Click(window, Centre(window, settings));
        Assert.IsType<SettingsDialog>(window.Frontmost);
        Assert.True(window.MovesStarted == 1, "A click on the toolbar's Settings moved the window");
        window.Frontmost.Close();
        Ui.Settle();

        // A double click does what the system's own title bar's does: maximize and restore (macOS: as its settings say).
        WindowState? doubled = TitleBars.DoubleClicked(WindowState.Normal);
        DoubleClick(window, empty);
        Assert.Equal(doubled ?? WindowState.Normal, window.WindowState);
        if (window.WindowState == WindowState.Maximized)
        {
            DoubleClick(window, empty + new Point(40, 0));
            Assert.True(window.WindowState == WindowState.Normal, "A second double click on the bar did not restore the window");
        }
        window.WindowState = WindowState.Normal;
        window.Close();
    }

    [AvaloniaFact]
    public void The_window_buttons_stand_at_the_bars_right_end_and_minimize_maximize_and_close_the_window()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS's own traffic lights are the window's buttons");
        MainWindow window = Open();
        Assert.SkipUnless(window.TitleBarMerged, $"This desktop keeps the system's title bar: {TitleBars.NotOnThisDesktop}");
        Button minimize = Find<Button>(window, "MinimizeButton");
        Button maximize = Find<Button>(window, "MaximizeButton");
        Button close = Find<Button>(window, "CloseButton");
        Assert.True(minimize.IsEffectivelyVisible && maximize.IsEffectivelyVisible && close.IsEffectivelyVisible, "The bar shows no window buttons");
        // At the bar's right end: the window's, but for Linux's edge where Passenger draws the frame.
        Control root = Find<Control>(window, "Root");
        Assert.Equal(root.TranslatePoint(new Point(root.Bounds.Width, 0), window)!.Value.X, close.TranslatePoint(new Point(close.Bounds.Width, 0), window)!.Value.X, 1);
        // Windows takes them as its own caption buttons: Windows 11's snap layouts open over the maximize button.
        Assert.Equal(WindowDecorationsElementRole.MinimizeButton, ChromeAt(window, Centre(window, minimize)));
        Assert.Equal(WindowDecorationsElementRole.MaximizeButton, ChromeAt(window, Centre(window, maximize)));
        Assert.Equal(WindowDecorationsElementRole.CloseButton, ChromeAt(window, Centre(window, close)));

        Click(window, Centre(window, maximize));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.True(AutomationProperties.GetName(maximize) == "Restore", "The maximized window's button does not offer to restore it");
        Click(window, Centre(window, maximize));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Click(window, Centre(window, minimize));
        Assert.Equal(WindowState.Minimized, window.WindowState);
        window.WindowState = WindowState.Normal;
        Ui.Settle();

        bool closed = false;
        window.Closed += (_, _) => closed = true;
        Click(window, Centre(window, close));
        Assert.True(closed, "The bar's Close did not close the window");
    }

    [AvaloniaFact]
    public void On_macOS_the_bar_leaves_the_traffic_lights_their_room_at_its_left_but_in_full_screen()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The traffic lights are macOS's");
        MainWindow window = Open();
        Control start = Find<Control>(window, "ToolbarStart");
        Assert.False(Find<Control>(window, "WindowButtons").IsVisible, "The bar draws window buttons of its own beside macOS's");
        Assert.True(start.TranslatePoint(default, window)!.Value.X >= 72, "The project widget stands where macOS draws the traffic lights");
        window.WindowState = WindowState.FullScreen;
        Ui.Settle();
        Assert.True(start.TranslatePoint(default, window)!.Value.X < 20, "In full screen, with no traffic lights, the bar still leaves their room");
        window.WindowState = WindowState.Normal;
        window.Close();
    }

    [AvaloniaFact]
    public void Linux_alone_offers_the_merged_title_bar_on_at_first_as_riders_merge_main_menu_with_window_title()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "merge main menu with window title";
        Ui.Settle();
        if (!TitleBars.Offered)
        {
            Assert.True(dialog.Pages.Count == 0, $"Settings offers the system's title bar on a system where the toolbar is always the title bar: {string.Join(", ", dialog.Pages)}");
            dialog.Close();
            window.Close();
            return;
        }
        Assert.Equal([SettingsDialog.Appearance], dialog.Pages);
        CheckBox merge = Editor<CheckBox>(dialog, "Merge main menu with window title");
        Assert.True(merge.IsChecked == true, "Merge main menu with window title is off before anyone turned it off");
        if (TitleBars.NotOnThisDesktop is { } why)
        {
            // As IntelliJ, a desktop it does not merge them on keeps the system's, saying why.
            Assert.False(merge.IsEnabled, "The option can be changed on a desktop that keeps the system's title bar");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == why);
            Assert.False(window.TitleBarMerged || window.ExtendClientAreaToDecorationsHint || Find<Control>(window, "WindowButtons").IsVisible,
                "A desktop that keeps the system's title bar has the toolbar as the title bar");
            Click(window, EmptySpace(window));
            Assert.True(window.MovesStarted == 0, "The toolbar under the system's title bar moves the window");
            dialog.Close();
            window.Close();
            return;
        }
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == TitleBars.OwnFrame);
        Assert.True(window.TitleBarMerged && window.ExtendClientAreaToDecorationsHint && Find<Control>(window, "WindowButtons").IsVisible,
            "On at first, the toolbar is not the title bar with the window's buttons");
        Click(window, EmptySpace(window));
        Assert.True(window.MovesStarted == 1, "The merged title bar's empty space did not move the window");
        // A right press there asks the window manager for its window menu, as on its own title bar; one on a button not.
        RightClick(window, EmptySpace(window));
        Button settings = Bar(window).GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Settings");
        RightClick(window, Centre(window, settings));
        Assert.True(window.WindowMenusAsked == 1, $"Right presses on the merged bar asked for the window manager's menu {window.WindowMenusAsked} times, not once (on its empty space)");
        Assert.True(window.MovesStarted == 1, "A right press on the merged bar moved the window");

        dialog.Change(s => s with { MergeMainMenuWithWindowTitle = false });
        dialog.Ok();
        Ui.Settle();
        Assert.False(window.TitleBarMerged || window.ExtendClientAreaToDecorationsHint || Find<Control>(window, "WindowButtons").IsVisible,
            "Turned off, the system's title bar did not come back over the toolbar");
        Click(window, EmptySpace(window));
        Assert.True(window.MovesStarted == 1, "The toolbar under the system's title bar moves the window");

        // On again, as every other window of the run has it.
        dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Change(s => s with { MergeMainMenuWithWindowTitle = true });
        dialog.Ok();
        Ui.Settle();
        Assert.True(window.TitleBarMerged, "Turned on again, the toolbar did not become the title bar");
        window.Close();
    }

    [AvaloniaFact]
    public void Half_a_laptop_screen_keeps_the_toolbar_and_window_buttons_inside_it_even_with_long_names_and_a_live_run()
    {
        const string name = "A_solution_with_a_long_name_that_would_push_the_window_buttons_off_a_half_width_screen";
        string project = Path.Combine(folder, name + ".csproj");
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "Console.WriteLine(\"ready at half width\");\nThread.Sleep(Timeout.Infinite);\n");
        TestGit.Init(folder);
        TestGit.Run(folder, "checkout", "-q", "-b", "a-branch-with-a-long-name-that-must-stay-in-its-own-toolbar-widget");
        Restore.Run(folder);
        MainWindow window = new() { Width = 683, Height = 768 };
        window.Show();
        try
        {
            window.OpenFolder(project);
            window.OpenFile("Program.cs");
            Ui.Settle();
            string chosen = "The run configuration whose full name stays available when its label is shortened";
            window.SetConfigurations([new RunConfiguration { Name = chosen, Project = PathNames.Canonical(project) }], chosen);
            window.ApplySettings(window.Settings with { HotReload = true });
            Assert.Null(Ui.Await(window.RunAsync(), "the half-width window's program to run", TimeSpan.FromMinutes(3)));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("ready at half width", StringComparison.Ordinal),
                "the program to run under dotnet watch", TimeSpan.FromMinutes(2));
            Ui.Settle();
            Assert.True(Find<Button>(window, "StopButton").IsVisible && Find<Button>(window, "HotReloadButton").IsVisible,
                "A live run with Hot Reload must exercise the toolbar's Stop and Apply Changes buttons too");
            Fits(683);
            Assert.Contains(chosen, (string)ToolTip.GetTip(Find<Button>(window, "RunConfigButton"))!, StringComparison.Ordinal);
            Click(window, Centre(window, Find<Button>(window, "RunConfigButton")));
            ContextMenu chooser = Assert.IsType<ContextMenu>(window.OpenContextMenu);
            Assert.Contains(chooser.Items.OfType<MenuItem>(), item => item.Header as string == chosen);
            chooser.Close();
            Assert.True(window.Actions[AppActions.Build]!.Menus.Count > 0 && window.Actions[AppActions.Profile]!.Menus.Count > 0,
                "Build and Profile must stay reachable from their menu when the toolbar gives their icons' room back");

            window.Width = 1200;
            Ui.Settle();
            Fits(1200);
            Assert.True(Find<Button>(window, "BuildButton").IsVisible && Find<Button>(window, "ProfileButton").IsVisible
                && Find<TextBlock>(window, "ProjectName").IsVisible && Find<TextBlock>(window, "BranchName").IsVisible,
                "Widening the window must restore the toolbar's icons and names");
            Assert.Equal(chosen, window.Configuration?.Name);
            foreach (double width in new double[] { 640, 800, 900, 1024, 1400, 683 })
            {
                window.Width = width;
                Ui.Settle();
                Fits(width);
                Assert.Equal(chosen, window.Configuration?.Name);
            }
        }
        finally
        {
            window.Close();
        }

        void Fits(double width)
        {
            Assert.True(Math.Abs(window.Bounds.Width - width) < 1, $"The window's minimum prevented resizing it to {width}px");
            Border bar = Bar(window);
            Rect toolbar = new(bar.TranslatePoint(default, window)!.Value, bar.Bounds.Size);
            Button[] buttons = [.. Bar(window).GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];
            Rect[] bounds = [.. buttons.Select(b => new Rect(b.TranslatePoint(default, window)!.Value, b.Bounds.Size))];
            for (int i = 0; i < buttons.Length; i++)
            {
                Assert.True(bounds[i].X >= toolbar.X && bounds[i].Right <= toolbar.Right
                    && bounds[i].Y >= toolbar.Y && bounds[i].Bottom <= toolbar.Bottom,
                    $"The {width}px toolbar's {buttons[i].Name ?? ToolTip.GetTip(buttons[i])} is cut off at {bounds[i]}");
                Assert.True(bounds.Where((_, index) => index != i).All(other => !other.Intersects(bounds[i])),
                    $"The {width}px toolbar's {buttons[i].Name ?? ToolTip.GetTip(buttons[i])} overlaps another button");
            }
        }
    }

    [AvaloniaFact]
    public void At_the_smallest_width_the_welcome_screens_project_widget_still_opens_solutions()
    {
        MainWindow window = new() { Width = 640, Height = 768 };
        window.Show();
        try
        {
            Ui.Settle();
            Button project = Find<Button>(window, "ProjectButton");
            Assert.True(project.IsEffectivelyVisible && project.Bounds.Width >= 20,
                "Without a solution's name or badge the compact project widget must still have a visible click target");
            Click(window, Centre(window, project));
            ContextMenu menu = Assert.IsType<ContextMenu>(window.OpenContextMenu);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Header as string == "Open...");
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    private MainWindow Open()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("One.cs");
        Ui.Settle();
        return window;
    }

    private static Border Bar(MainWindow window) => Find<Border>(window, "Toolbar");

    private static T Find<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"The window has no {name}");

    /// <summary>Halfway between the bar's widgets at its left and those at its right.</summary>
    private static Point EmptySpace(MainWindow window)
    {
        Control start = Find<Control>(window, "ToolbarStart");
        Control end = Find<Control>(window, "ToolbarEnd");
        double left = start.TranslatePoint(new Point(start.Bounds.Width, 0), window)!.Value.X;
        double right = end.TranslatePoint(default, window)!.Value.X;
        Assert.True(right - left > 100, $"The bar has no empty space to take hold of between {left} and {right}");
        return new Point((left + right) / 2, Bar(window).Bounds.Height / 2);
    }

    private static Point Centre(MainWindow window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>What Windows is told a press there is: its caption, one of its buttons, the window's own (User), or nothing
    /// of the frame's. Avalonia answers Windows' non-client hit test (and X11's press) from this, which it keeps internal.</summary>
    private static WindowDecorationsElementRole? ChromeAt(MainWindow window, Point at)
    {
        System.Reflection.MethodInfo hitTest = typeof(IInputRoot).GetMethod("HitTestChromeElement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia no longer answers the platform's chrome hit test by IInputRoot.HitTestChromeElement");
        return (WindowDecorationsElementRole?)hitTest.Invoke(window.GetPresentationSource(), [at]);
    }

    private static void Click(MainWindow window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }

    private static void RightClick(MainWindow window, Point at)
    {
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Ui.Settle();
    }

    private static void DoubleClick(MainWindow window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }

    private static T Editor<T>(SettingsDialog dialog, string label) where T : Control =>
        ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label).Parent!).GetVisualDescendants().OfType<T>().First();
}
