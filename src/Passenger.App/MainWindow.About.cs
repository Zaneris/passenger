using Avalonia.Controls;

namespace Passenger.App;

/// <summary>About Passenger: the application menu's first item on macOS, Help's last elsewhere, after Check for
/// Updates.</summary>
public partial class MainWindow
{
    private void AddAbout() =>
        actions!.Add(new IdeAction(AppActions.About, "About", "Help", _ => ShowAbout())
        {
            Menus = OperatingSystem.IsMacOS() ? [] : [new MenuPlace("Help", 3, 2)],
        });

    /// <summary>About, over this window until it is closed; its links close it and open the notices or the licence in
    /// an editor here, read-only.</summary>
    public AboutDialog ShowAbout()
    {
        AboutDialog dialog = new(path => OpenFile(path));
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>macOS's application menu: About Passenger and Check for Updates, then Settings (Cmd+,); the system adds
    /// Services, Hide and Quit after them. The process has the one, made from its first window, and each item runs in
    /// the window in front.</summary>
    public NativeMenu ApplicationMenu()
    {
        // macOS's application menu names the application after About.
        NativeMenuItem about = new($"{actions![AppActions.About]!.Title} Passenger") { Command = new MenuCommand(() => InFront().Request(AppActions.About)) };
        NativeMenuItem updates = new(actions[AppActions.CheckForUpdates]!.Title) { Command = new MenuCommand(() => InFront().Request(AppActions.CheckForUpdates)) };
        NativeMenuItem settings = new(actions[AppActions.ShowSettings]!.Title)
        {
            Gesture = actions.MenuBarGesture(AppActions.ShowSettings),
            Command = new MenuCommand(() => InFront().Request(AppActions.ShowSettings)),
        };
        return new NativeMenu { Items = { about, updates, new NativeMenuItemSeparator(), settings } };

        MainWindow InFront() => PassengerApp.Front is { IsVisible: true } front ? front : this;
    }
}
