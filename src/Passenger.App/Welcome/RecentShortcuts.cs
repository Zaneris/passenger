using Avalonia;
using Avalonia.Controls;
using Passenger.Editor;

namespace Passenger.App.Welcome;

/// <summary>The system's lists of Passenger outside its windows, kept in step with the recent folders and solutions of the
/// window that last changed them: the Windows taskbar's jump list (<see cref="WindowsJumpList"/>), whose entries start
/// Passenger as a second start does, and the macOS Dock's menu, whose items open one as File, Recent Solutions does. Only
/// a Passenger a person sees sets them; a UI-thread object.</summary>
public static class RecentShortcuts
{
    /// <summary>How many recent ones the Dock's menu lists; the jump list lists as many as the taskbar says it shows.</summary>
    public const int DockShows = 10;

    /// <summary>How long the recent list rests before the system's lists follow it, so the windows a start reopens set them once.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private static Debounce? following;

    /// <summary>The task starting a window with nothing open, from the Passenger running or a new one.</summary>
    public static RecentShortcut NewWindow { get; } = new(OpenProjectDialog.NewWindow, "A Passenger window with nothing open", [LaunchOptions.NewWindowFlag]);

    /// <summary>The window whose recent list the system's lists follow next.</summary>
    private static MainWindow? from;

    /// <summary>The window's recent list changed, or it opened on it: the system's lists follow once it rests.</summary>
    public static void Follow(MainWindow window)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }
        from = window;
        following ??= new Debounce(Settle, () =>
        {
            if (from is { } asked && PassengerApp.Windows.Contains(asked))
            {
                Set(asked);
            }
            return Task.CompletedTask;
        });
        following.Restart();
    }

    private static void Set(MainWindow window)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsJumpList.Set(window.Shortcuts, [NewWindow], window.ForgetRemoved);
        }
        else if (OperatingSystem.IsMacOS() && Application.Current is { } app)
        {
            NativeDock.SetMenu(app, DockMenu(window.Shortcuts.Take(DockShows)));
        }
    }

    /// <summary>The Dock's menu: the recent ones, each opened from the window in front as File, Recent Solutions opens it,
    /// then New Window.</summary>
    private static NativeMenu DockMenu(IEnumerable<(string Path, RecentShortcut Shortcut)> recent)
    {
        NativeMenu menu = new();
        foreach ((string path, RecentShortcut shortcut) in recent)
        {
            NativeMenuItem item = new(shortcut.Title) { ToolTip = shortcut.Tip };
            item.Click += (_, _) => PassengerApp.Front?.OpenRecent(path);
            menu.Add(item);
        }
        if (menu.Items.Count > 0)
        {
            menu.Add(new NativeMenuItemSeparator());
        }
        NativeMenuItem newWindow = new(NewWindow.Title);
        newWindow.Click += (_, _) =>
        {
            if (PassengerApp.Front is { } front)
            {
                PassengerApp.BringForward(PassengerApp.NewWindow(front));
            }
        };
        menu.Add(newWindow);
        return menu;
    }
}
