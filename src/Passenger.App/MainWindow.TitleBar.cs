using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App.Settings;

namespace Passenger.App;

/// <summary>The toolbar as the window's title bar, as Rider's main toolbar is (<see cref="TitleBars"/> says where): the
/// window extends into its frame, the bar's empty space moves it and a double click maximizes it, and what is on the bar
/// keeps its own clicks. Windows is told which part is which by roles (<see cref="WindowDecorationProperties"/>), so its
/// own move, snap, double click, window menu and the maximize button's snap layouts work on them; macOS's traffic lights
/// stand at the bar's left, and Windows' and Linux's buttons are the bar's own at its right. The window's title is still
/// set for the taskbar, Alt+Tab and Mission Control; the bar draws none.</summary>
public partial class MainWindow
{
    /// <summary>Room at the bar's left for macOS's traffic lights.</summary>
    private const double MacWindowButtons = 72;

    /// <summary>Linux's edge round a window that draws its own frame, inside the resize grips' width.</summary>
    private const double LinuxEdge = 1;

    private IDisposable? background;

    /// <summary>Whether the toolbar is the window's title bar.</summary>
    public bool TitleBarMerged { get; private set; }

    /// <summary>Window moves the bar's empty space started: what a test reads, the headless platform moving no window.</summary>
    public int MovesStarted { get; private set; }

    private void AddTitleBar()
    {
        WindowDecorationsTheme = (ControlTheme)Resources["TitleBarDecorations"]!;
        Toolbar.SizeChanged += (_, _) => FitToolbar();
        MergeTitleBar(TitleBars.Merged(settings));
    }

    /// <summary>The toolbar becomes the title bar, or the system's comes back over it (Linux's setting changed).</summary>
    private void MergeTitleBar(bool merged)
    {
        TitleBarMerged = merged;
        ExtendClientAreaToDecorationsHint = merged;
        ExtendClientAreaTitleBarHeightHint = merged ? Toolbar.Height : -1;
        // Windows moves the window, snaps it and maximizes it on a double click itself on a caption. X11 would move it on
        // a press there before the window saw the press, so Linux's double click goes through OnToolbarPressed instead.
        WindowDecorationProperties.SetElementRole(Toolbar, merged && OperatingSystem.IsWindows() ? WindowDecorationsElementRole.TitleBar : WindowDecorationsElementRole.None);
        // What shows of the window round its content: Linux's edge where Passenger draws the frame.
        background?.Dispose();
        background = this.Bind(BackgroundProperty, this.GetResourceObservable(merged && TitleBars.Offered ? "Passenger.WindowEdge" : "Passenger.Editor.Background"));
        LayOutTitleBar();
    }

    private void FollowTitleBar(AppSettings next)
    {
        if (TitleBars.Merged(next) != TitleBarMerged)
        {
            MergeTitleBar(!TitleBarMerged);
        }
    }

    /// <summary>The bar as the window stands: the traffic lights' room but in full screen, where macOS hides them; the
    /// window's buttons but in full screen; the restore glyph while maximized; Linux's edge while the window has a frame.</summary>
    private void LayOutTitleBar()
    {
        bool fullScreen = WindowState == WindowState.FullScreen;
        bool trafficLights = TitleBarMerged && OperatingSystem.IsMacOS() && !fullScreen;
        WindowButtons.IsVisible = TitleBarMerged && !OperatingSystem.IsMacOS() && !fullScreen;
        ToolbarContent.Margin = new Thickness(trafficLights ? MacWindowButtons : 8, 0, WindowButtons.IsVisible ? 0 : 8, 0);
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(maximized ? "M0.5,2.5 H7.5 V9.5 H0.5 Z M2.5,2.5 V0.5 H9.5 V7.5 H7.5" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
        bool framed = TitleBarMerged && TitleBars.Offered && !maximized && !fullScreen;
        Root.Margin = new Thickness(framed ? LinuxEdge : 0);
    }

    /// <summary>Half a laptop screen keeps Run, Debug, Stop and the window's buttons reachable: names shorten and the
    /// Build and Profile icons give their space back (their menu actions stay available).</summary>
    private void FitToolbar()
    {
        bool compact = Toolbar.Bounds.Width is > 0 and < 900;
        // Space for every icon, including Stop and Apply Changes, the window's buttons and a place to drag the bar.
        double nameWidth = Math.Clamp((Toolbar.Bounds.Width - 760) / 3, 40, 180);
        ProjectName.IsVisible = !compact;
        BranchName.IsVisible = !compact;
        BuildButton.IsVisible = !compact;
        ProfileButton.IsVisible = !compact;
        ProjectName.MaxWidth = BranchName.MaxWidth = nameWidth;
        RunConfigName.MaxWidth = compact ? 80 : nameWidth;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            LayOutTitleBar();
        }
    }

    /// <summary>Window menus a right press on the bar's empty space asked Linux's window manager for: what a test reads.</summary>
    public int WindowMenusAsked { get; private set; }

    /// <summary>A press on the bar's empty space moves the window, a double click does what the system's title bar's does,
    /// and on Linux a right press shows the window manager's window menu, as on its own title bar. Where Windows has the
    /// bar's roles the press never comes here: Windows takes it as its own caption's.</summary>
    private void OnToolbarPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties pressed = e.GetCurrentPoint(this).Properties;
        if (TitleBarMerged && TitleBars.Offered && pressed.IsRightButtonPressed
            && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            WindowMenusAsked++;
            if (TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle)
            {
                TitleBars.ShowWindowMenu(handle.Handle, this.PointToScreen(e.GetPosition(this)));
            }
            e.Handled = true;
            return;
        }
        if (!TitleBarMerged || !pressed.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            if (TitleBars.DoubleClicked(WindowState) is { } next)
            {
                WindowState = next;
            }
            return;
        }
        MovesStarted++;
        BeginMoveDrag(e);
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
