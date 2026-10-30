using Passenger.App.ToolWindows;

namespace Passenger.App;

/// <summary>What the window started ends with it: programs and builds at once, the debugger, the test runner and the
/// language server as asked, and the process waits a little for those before it goes (<see cref="Ended"/>). Close
/// Solution ends what the folder runs the same way and leaves the window on the Welcome screen. Leaving the folder so, by
/// opening another here or by closing the window, asks the person first where it would stop a program Hot Reload changed,
/// which the debugger cannot let go (<see cref="HeldByLeaving"/>).</summary>
public partial class MainWindow
{
    /// <summary>How long the process waits, once the window has closed, for the debugger, the test runner and the
    /// language server to end as asked; each kills its own process when it will not go.</summary>
    public static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(5);

    /// <summary>Finishes when what the window started has ended, or <see cref="EndLimit"/> after it closed; null while it
    /// is open. It needs no UI thread, so the process waits for it once the window's loop has stopped (Program, the MCP
    /// host, a screenshot). A logout does not wait: macOS ends the process, after the programs' and builds' kills.</summary>
    public Task? Ended { get; private set; }

    private const string ClosingSolution = "closing the solution";

    /// <summary>Whether the window asks the person, closed, whether to stop the programs its close would stop.</summary>
    private bool askingHeld;

    private void AddShutdown()
    {
        Closed += (_, _) => EndChildren();
        actions!.Add(new IdeAction(AppActions.CloseSolution, "Close Solution", "IDE", _ => CloseSolution())
        {
            WhyNot = c => Folder is null ? Refusals.NoFolder : c.ByAgent ? WhyNotLeave(ClosingSolution) : null,
            Menus = [new MenuPlace("File", 0, 5)],
        });
    }

    /// <summary>The session's programs and builds go now, so a server lets go of its port; the rest are asked to end.</summary>
    private void EndChildren()
    {
        Ended = session.CloseAsync(
            ("the solution left before", ending),
            ("the terminal", terminal?.CloseAllAsync() ?? Task.CompletedTask),
            ("Discord", EndDiscord()));
    }

    /// <summary>Close Solution: its changed files saved as closing the window saves them (one that cannot be keeps it
    /// open, with why), its editors and layout kept with it and the solution at the top of the recent ones, everything
    /// it runs ended (programs, builds, the debugger, tests, its terminals, the language server), and the Welcome
    /// screen in the editors' place; a program Hot Reload changed that this would stop is asked about first, the solution
    /// closed once it has stopped (<see cref="StopHeldAskedAsync"/>). Null once it is closed, else why not.</summary>
    public string? CloseSolution()
    {
        UiThread.Check();
        if (Folder is null)
        {
            return Refusals.NoFolder;
        }
        if (HeldByLeaving() is { Count: > 0 } held)
        {
            IdeErrors.Observe(CloseSolutionAskedAsync(held));
            return HeldWords(held, ClosingSolution);
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

    /// <summary>The Debug tabs of programs leaving the folder would stop, which the person is asked about first: attached to
    /// and changed by Hot Reload, whose runtime lets no debugger go (<see cref="Debugging.DebugController.AttachedChanged"/>),
    /// and none of a run of services, which ends with the folder anyway.</summary>
    private List<DebugTab> HeldByLeaving() => [.. session.Debug.AttachedChanged.Where(t => t.Session is { } held && buildRun.ServiceDebuggedBy(held) is null)];

    /// <summary>What <paramref name="leaving"/> would do to programs Hot Reload changed (<see cref="HeldByLeaving"/>).</summary>
    private static string HeldWords(IReadOnlyList<DebugTab> held, string leaving)
    {
        string names = held.Count == 1 ? held[0].Name : $"{string.Join(", ", held.SkipLast(1).Select(t => t.Name))} and {held[^1].Name}";
        return held.Count == 1
            ? $"Hot Reload changed the code of {names}, after which its runtime lets no debugger go, so {leaving} would stop it rather than leave it frozen"
            : $"Hot Reload changed the code of {names}, after which their runtimes let no debugger go, so {leaving} would stop them rather than leave them frozen";
    }

    /// <summary>Why an agent may not leave the folder now (<paramref name="leaving"/> it), or null: it would stop a program
    /// Hot Reload changed, which is the person's to choose, so the agent stops it first itself.</summary>
    public string? WhyNotLeave(string leaving) => HeldByLeaving() is { Count: > 0 } held
        ? $"{HeldWords(held, leaving)}; {Mcp.McpTools.Stop} {(held.Count == 1 ? "it" : "them")} first to go on (session {string.Join(", ", held.Select(t => t.Name))})"
        : null;

    /// <summary>Asks the person whether to stop programs Hot Reload changed that <paramref name="leaving"/> would stop
    /// (<see cref="HeldByLeaving"/>), as letting them go would leave them frozen: <paramref name="stopAnd"/> stops them,
    /// true once they have; Cancel keeps them debugged, the folder open.</summary>
    private async Task<bool> StopHeldAskedAsync(IReadOnlyList<DebugTab> held, string leaving, string stopAnd)
    {
        ProjectSession asked = session;
        string them = held.Count == 1 ? "it" : "them";
        string? answer = await Choose(held.Count == 1 ? "Program Changed by Hot Reload" : "Programs Changed by Hot Reload",
            $"{HeldWords(held, leaving)}.\n\nStop {them}, or Cancel to go on debugging {them}.", [stopAnd], null);
        if (answer != stopAnd || session != asked)
        {
            return false;
        }
        await Task.WhenAll(held.Select(asked.Debug.StopAsync));
        return true;
    }

    /// <summary>Close Solution once the programs it would stop have stopped, as the person answered.</summary>
    private async Task CloseSolutionAskedAsync(IReadOnlyList<DebugTab> held)
    {
        if (await StopHeldAskedAsync(held, ClosingSolution, "Stop and Close"))
        {
            CloseSolution();
        }
    }

    /// <summary>The window was closed with programs Hot Reload changed that its close would stop: asked, it closes once they
    /// have stopped, or stays open with them debugged, and a quit it held carries on or is given up (<see
    /// cref="PassengerApp.Answered"/>).</summary>
    private async Task CloseHeldAsync(IReadOnlyList<DebugTab> held, bool quit)
    {
        if (askingHeld)
        {
            return;
        }
        askingHeld = true;
        ClosesLater = true;
        bool stopped;
        try
        {
            stopped = await StopHeldAskedAsync(held, quit ? "quitting" : "closing the window", quit ? "Stop and Quit" : "Stop and Close");
        }
        finally
        {
            askingHeld = false;
            ClosesLater = false;
        }
        if (quit)
        {
            PassengerApp.Answered(carryOn: stopped);
        }
        else if (stopped)
        {
            Close();
        }
    }

    /// <summary>Another folder opened here over programs Hot Reload changed that leaving would stop: asked, it opens once they
    /// have stopped, else not at all.</summary>
    private async Task OpenHeldAsync(IReadOnlyList<DebugTab> held, string path, string folder, Action? opened)
    {
        if (await StopHeldAskedAsync(held, $"opening {Path.GetFileName(folder)}", "Stop and Open"))
        {
            await OpenFolder(path, opened: opened);
        }
    }
}
