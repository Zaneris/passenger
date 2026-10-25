using Avalonia.Controls;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The window among the process's others (<see cref="PassengerApp"/>): a solution opened from it goes where
/// Settings' Open project in says, or where the person answers the Open Solution question; the Window menu lists the
/// solution windows open, the one in front checked, and Next and Previous Solution Window step through them; closing it
/// alone takes its solution off what the next start reopens, while quitting keeps every window's.</summary>
public partial class MainWindow
{
    private bool started;
    private bool quitting;

    /// <summary>The path this window last put among the recent solutions as open, which it takes off as it leaves it.</summary>
    private string? remembered;

    /// <summary>Open Solution's question while it asks, else null.</summary>
    public OpenProjectDialog? Asking { get; private set; }

    /// <summary>Whether the window has started (taken its command line, or been opened beside another) or opened a
    /// folder, so a solution the system hands Passenger goes to it rather than waiting for the first window's start.</summary>
    public bool IsStarted => started || Folder is not null;

    /// <summary>A window opened beside another (<see cref="PassengerApp.NewWindow"/>) has no command line to take: it is
    /// started as it is made, so a solution handed to Passenger opens from it whatever it has open.</summary>
    internal void StartBeside() => started = true;

    private void AddProjectWindows()
    {
        actions!.Add(new IdeAction(AppActions.NextProjectWindow, "Next Solution Window", "Window", _ => PassengerApp.Step(this, 1))
        {
            WhyNot = _ => OtherWindows(),
            Menus = [new MenuPlace("Window", 3, 0)],
            FromTerminal = true,
        });
        actions.Add(new IdeAction(AppActions.PreviousProjectWindow, "Previous Solution Window", "Window", _ => PassengerApp.Step(this, -1))
        {
            WhyNot = _ => OtherWindows(),
            Menus = [new MenuPlace("Window", 3, 1)],
            FromTerminal = true,
        });
        PassengerApp.FrontChanged += OnFrontChanged;
        Closed += (_, _) => PassengerApp.FrontChanged -= OnFrontChanged;
        Closing += (_, _) => LeaveRecentOpen();

        string? OtherWindows() => PassengerApp.Windows.Any(w => w != this && w.IsVisible) ? null : "No other solution window is open";
    }

    /// <summary>The window in front changed: Discord shows the one in front.</summary>
    private void OnFrontChanged(object? sender, EventArgs e) => FollowInDiscord();

    /// <summary>A path checked as one Open takes, opened as the person chose it: in this window while it has nothing open
    /// or the path is in its own folder, where another window has it open there, else where Settings' Open project in
    /// says, or as the person answers Open Solution; <paramref name="then"/> told the window once it has opened there.</summary>
    private void OpenChosen(string full, Action<MainWindow>? then)
    {
        string opening = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        if (Folder is null || PathNames.Same(Folder, opening)
            || PassengerApp.Windows.Any(w => w.Folder is { } other && PathNames.Same(other, opening)))
        {
            PassengerApp.OpenProject(full, OpenTarget.ThisWindow, this, then);
            return;
        }
        switch (settings.OpenProjectIn)
        {
            case OpenProjectIn.CurrentWindow:
                PassengerApp.OpenProject(full, OpenTarget.ThisWindow, this, then);
                return;
            case OpenProjectIn.NewWindow:
                PassengerApp.OpenProject(full, OpenTarget.NewWindow, this, then);
                return;
        }
        Asking?.Close();
        OpenProjectDialog dialog = new(File.Exists(full) ? Path.GetFileNameWithoutExtension(full) : Path.GetFileName(full));
        Asking = dialog;
        dialog.Closed += (_, _) =>
        {
            if (Asking == dialog)
            {
                Asking = null;
            }
            if (dialog.Chosen is not { } target)
            {
                return;
            }
            if (dialog.Remember)
            {
                ApplySettings(settings with { OpenProjectIn = target == OpenTarget.NewWindow ? OpenProjectIn.NewWindow : OpenProjectIn.CurrentWindow });
            }
            // Once the question has gone and this window has the keys back, so a new window comes in front of it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PassengerApp.OpenProject(full, target, this, then));
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
    }

    /// <summary>Window's list: each solution window open, by its solution's name (Welcome to Passenger for one with
    /// nothing open), this one checked; another chosen is brought forward through <see cref="PassengerApp.OpenProject"/>.</summary>
    private IReadOnlyList<ListedItem> SolutionWindows() =>
        [.. PassengerApp.Windows.Where(w => w.IsVisible).Select(w => new ListedItem(w.SolutionTitle, () =>
        {
            if (w != this && w.WorkspaceModel is { } open)
            {
                PassengerApp.OpenProject(open.OpenedAs, OpenTarget.ThisWindow, this);
            }
            else
            {
                PassengerApp.BringForward(w);
            }
        }, w == this))];

    /// <summary>The window as a list of windows names it: its solution (or project, or folder), else the Welcome window's.</summary>
    public string SolutionTitle => Folder is null ? "Welcome to Passenger" : session.Workspace?.Name ?? Path.GetFileName(Folder);

    /// <summary>The workspace just opened is marked open among the recent ones, and the one it replaced in this window no
    /// longer is.</summary>
    private void RememberOpen(string openedAs)
    {
        if (remembered is { } before && !PathNames.Same(before, openedAs))
        {
            state!.Recent.Set(state.Recent.Value.Closed(before));
        }
        remembered = openedAs;
    }

    /// <summary>Its workspace no longer open in this window (Close Solution, or the window closed alone), so a start does
    /// not reopen it.</summary>
    private void ForgetOpen()
    {
        if (keepsWorkspace && remembered is { } open)
        {
            state!.Recent.Set(state.Recent.Value.Closed(open));
        }
        remembered = null;
    }

    /// <summary>As the window closes: alone, while other windows stay, its solution is closed as Close Solution's is;
    /// quitting, or the last window, it stays open for the next start to reopen.</summary>
    private void LeaveRecentOpen()
    {
        if (!quitting && PassengerApp.Windows.Any(w => w != this && w.IsVisible))
        {
            ForgetOpen();
        }
    }

    /// <summary>Whether this window shows its activity in Discord while it is in front and Settings says so.</summary>
    public bool SharesActivity => sharesActivity;
}
