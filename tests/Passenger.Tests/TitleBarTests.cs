using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>The toolbar is the main window's title bar: the window's title (solution and file) still named for the
/// taskbar, Alt+Tab and Mission Control, the bar's empty space moving the window and a double click on it maximizing it,
/// what is on the bar keeping its own clicks; the window's buttons at its right on Windows and Linux, macOS's traffic
/// lights' room at its left; and on Linux alone the choice of the merged bar, on at first, a right press on it showing
/// the window manager's menu. Where Windows decides what a press in the bar is (its caption, a button, the bar's own),
/// the roles it asks are checked, as no test sees the system's own window.</summary>
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

        // What is on the bar keeps its clicks: Windows hands them to it.
        foreach (Button button in Bar(window).GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && !b.Classes.Contains("windowButton")))
        {
            Assert.True(ChromeAt(window, Ui.Centre(button, window)) == WindowDecorationsElementRole.User,
                $"Windows would take a press on the toolbar's {button.Name ?? ToolTip.GetTip(button)} as one on its caption, moving the window instead of clicking it");
        }
        Button settings = Bar(window).GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Settings");
        Click(window, Ui.Centre(settings, window));
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
    public void The_main_menus_titles_on_the_bar_keep_their_clicks_and_the_space_beside_them_still_moves_the_window()
    {
        // The menu on the toolbar, as on Windows and Linux, rather than in macOS's own menu bar.
        MainWindow.SystemMenuBar = false;
        try
        {
            // Wide enough for several titles beside Windows' own window buttons.
            MainWindow window = Open(width: 1600);
            Assert.SkipUnless(window.TitleBarMerged, $"This desktop keeps the system's title bar: {TitleBars.NotOnThisDesktop}");
            window.ApplySettings(window.Settings with { MainMenuBar = true });
            Ui.Settle();
            Button[] titles = [.. Find<Control>(window, "MainMenuPlace").GetVisualChildren().OfType<Button>().Where(b => b.Tag is string && b.IsHitTestVisible)];
            Assert.True(titles.Length > 3, $"The bar shows {titles.Length} of the main menu's titles at 1600 wide");
            foreach (Button title in titles)
            {
                Assert.True(ChromeAt(window, Ui.Centre(title, window)) == WindowDecorationsElementRole.User,
                    $"Windows would take a press on the bar's {title.Content} as one on its caption, moving the window instead of opening the menu");
            }
            Assert.Equal(OperatingSystem.IsWindows() ? WindowDecorationsElementRole.TitleBar : null, ChromeAt(window, EmptySpace(window)));
            window.Close();
        }
        finally
        {
            MainWindow.SystemMenuBar = OperatingSystem.IsMacOS();
        }
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
        Assert.Equal(WindowDecorationsElementRole.MinimizeButton, ChromeAt(window, Ui.Centre(minimize, window)));
        Assert.Equal(WindowDecorationsElementRole.MaximizeButton, ChromeAt(window, Ui.Centre(maximize, window)));
        Assert.Equal(WindowDecorationsElementRole.CloseButton, ChromeAt(window, Ui.Centre(close, window)));

        Click(window, Ui.Centre(maximize, window));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.True(AutomationProperties.GetName(maximize) == "Restore", "The maximized window's button does not offer to restore it");
        Click(window, Ui.Centre(maximize, window));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Click(window, Ui.Centre(minimize, window));
        Assert.Equal(WindowState.Minimized, window.WindowState);
        window.WindowState = WindowState.Normal;
        Ui.Settle();

        bool closed = false;
        window.Closed += (_, _) => closed = true;
        Click(window, Ui.Centre(close, window));
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
        RightClick(window, EmptySpace(window));
        Button settings = Bar(window).GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Settings");
        RightClick(window, Ui.Centre(settings, window));
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

    private MainWindow Open(double width = 1200)
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder, width: width);
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
