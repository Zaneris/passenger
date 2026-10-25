namespace Passenger.App;

/// <summary>What the window started ends with it: programs and builds at once, the debugger, the test runner and the
/// language server as asked, and the process waits a little for those before it goes (<see cref="Ended"/>). Close
/// Solution ends what the folder runs the same way and leaves the window on the Welcome screen.</summary>
public partial class MainWindow
{
    /// <summary>How long the process waits, once the window has closed, for the debugger, the test runner and the
    /// language server to end as asked; each kills its own process when it will not go.</summary>
    public static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(5);

    /// <summary>Finishes when what the window started has ended, or <see cref="EndLimit"/> after it closed; null while it
    /// is open. It needs no UI thread, so the process waits for it once the window's loop has stopped (Program, the MCP
    /// host, a screenshot). A logout does not wait: macOS ends the process, after the programs' and builds' kills.</summary>
    public Task? Ended { get; private set; }

    private void AddShutdown()
    {
        Closed += (_, _) => EndChildren();
        actions!.Add(new IdeAction(AppActions.CloseSolution, "Close Solution", "IDE", _ => CloseSolution())
        {
            WhyNot = _ => Folder is null ? Refusals.NoFolder : null,
            Menus = [new MenuPlace("File", 0, 5)],
        });
    }

    /// <summary>The session's programs and builds go now, so a server lets go of its port; the rest are asked to end.</summary>
    private void EndChildren()
    {
        IdeErrors.Observe(terminal?.CloseAllAsync() ?? Task.CompletedTask);
        Ended = session.CloseAsync(
            ("the solution left before", ending),
            ("Discord", EndDiscord()));
    }

    /// <summary>Close Solution: its changed files saved as closing the window saves them (one that cannot be keeps it
    /// open, with why), its editors and layout kept with it and the solution at the top of the recent ones, everything
    /// it runs ended (programs, builds, the debugger, tests, its terminals, the language server), and the Welcome
    /// screen in the editors' place. Null once it is closed, else why not.</summary>
    public string? CloseSolution()
    {
        UiThread.Check();
        if (Folder is null)
        {
            return Refusals.NoFolder;
        }
        if (!Autosave())
        {
            const string unsaved = "The solution was not closed: a changed file could not be saved";
            status.Text = unsaved;
            return unsaved;
        }
        Remember();
        ForgetOpen();
        if (keepsWorkspace)
        {
            KeepDock();
        }
        if (!LeaveFolder())
        {
            const string open = "The solution was not closed: an open file could not be saved";
            status.Text = open;
            return open;
        }
        EndSession();
        AgentsGoTo(null, null);
        ProjectName.Text = "No solution";
        ProjectBadge.IsVisible = false;
        Editors.ShowWelcome(welcome);
        welcome.Say(null);
        ShowRecent();
        UpdateStatus();
        welcome.FocusSearch();
        return null;
    }
}
