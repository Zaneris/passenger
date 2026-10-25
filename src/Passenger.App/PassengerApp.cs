using Avalonia;
using Avalonia.Controls;
using Passenger.App.Discord;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Where a folder or solution opens: the window it is opened from, or one of its own (This Window or New
/// Window).</summary>
public enum OpenTarget
{
    ThisWindow,
    NewWindow,
}

/// <summary>The process: its project windows, a folder or solution each, and the one way anything opens one
/// (<see cref="OpenProject"/>), which a start, File Open, the recent solutions, the Welcome screen, the Window menu,
/// the system handing Passenger a solution and an agent's open all go through. A window it makes is made as the one it
/// is opened from (the state folder, its layout and settings kept or not, agents let in or not, Discord shown or not),
/// so every window is composed the same way; the updates are watched by one window at a time and Discord shows the
/// window in front alone. Static because the process is one, as its windows are; UI thread only.</summary>
public static class PassengerApp
{
    /// <summary>How far a new window sits from the one it was opened from, as the system cascades them.</summary>
    private const int Cascade = 30;

    private static readonly List<MainWindow> Open = [];

    /// <summary>The windows in the order they were last in front, the front one last.</summary>
    private static readonly List<MainWindow> Fronts = [];

    /// <summary>What closed windows started and still end (a debugger, a language server), which the process waits for.</summary>
    private static readonly List<Task> Ending = [];

    /// <summary>The solutions the system asked to open before a window could (a double-click in the Finder that started
    /// Passenger), which the first window opens in place of what it would reopen.</summary>
    private static readonly List<string> Pending = [];

    /// <summary>Discord's presence of the windows that show their activity, one per place Discord is looked for, so two
    /// windows never connect twice: the window in front alone gives it its activity.</summary>
    private static readonly Dictionary<DiscordIpc, DiscordPresence> Presences = new(ReferenceEqualityComparer.Instance);

    /// <summary>The window that watches for updates, the process's one check (<see cref="MainWindow.WatchForUpdates"/>),
    /// handed on as it closes; it installs what was made ready as the process ends.</summary>
    private static MainWindow? updates;

    /// <summary>What a quit a window's question holds tells if the person keeps that window open (<see cref="Quit"/>).</summary>
    private static Action? quitGivenUp;

    /// <summary>The project windows open, in the order they opened: the Window menu's list, which Next and Previous
    /// Solution Window step through.</summary>
    public static IReadOnlyList<MainWindow> Windows => Open;

    /// <summary>The window the person was in last, or the newest.</summary>
    public static MainWindow? Front => Fronts.Count > 0 ? Fronts[^1] : null;

    /// <summary>Whether the windows are closing because Passenger quits (File, Exit, the application menu's Quit, the
    /// system's shutdown), so each keeps its solution to reopen, rather than one window closing alone.</summary>
    public static bool Quitting { get; private set; }

    /// <summary>A window came in front, or one closed: Discord follows the front one.</summary>
    public static event EventHandler? FrontChanged;

    /// <summary>A window is the process's from its making until it closes.</summary>
    internal static void Add(MainWindow window)
    {
        UiThread.Check();
        Open.Add(window);
        Fronts.Add(window);
        window.Activated += (_, _) => InFront(window);
        window.Closed += (_, _) => Remove(window);
    }

    /// <summary>Opens a folder, solution (.sln, .slnx) or C# project (.csproj), a path checked already
    /// (<see cref="MainWindow.Open"/> says why one cannot be), in the window it is opened from or one of its own; a
    /// window with nothing open takes it either way. One another window has open (its folder) is brought forward there
    /// instead, taking the solution named. The window it opened in; <paramref name="then"/> is told that window once
    /// the solution's session has begun there, never when it was not opened.</summary>
    public static MainWindow OpenProject(string path, OpenTarget target, MainWindow from, Action<MainWindow>? then = null)
    {
        UiThread.Check();
        string full = PathNames.Canonical(path);
        string folder = File.Exists(full) && WorkspaceModel.Opens(full) ? Path.GetDirectoryName(full)! : full;
        // A window's folder may still be opening (its documents read off the window's thread): it holds that one already.
        if (Open.FirstOrDefault(w => w != from && w.FolderHeld is { } other && PathNames.Same(other, folder)) is { } already)
        {
            if (already.WorkspaceModel?.OpenedAs is not { } opened || !PathNames.Same(opened, full))
            {
                IdeErrors.Observe(already.OpenFolder(full, opened: then is null ? null : () => then(already)));
            }
            else
            {
                then?.Invoke(already);
            }
            BringForward(already);
            return already;
        }
        MainWindow into = target == OpenTarget.NewWindow && from.FolderHeld is { } open && !PathNames.Same(open, folder) ? NewWindow(from) : from;
        IdeErrors.Observe(into.OpenFolder(full, opened: then is null ? null : () => then(into)));
        if (into != from)
        {
            BringForward(into);
        }
        return into;
    }

    /// <summary>A window made as another is: its state folder, its layout and settings kept or not, its agents, its
    /// Discord, its updates' feed and release notes and whether it starts the language server, beside it at its size;
    /// shown with nothing open.</summary>
    public static MainWindow NewWindow(MainWindow like)
    {
        UiThread.Check();
        MainWindow window = new()
        {
            StateFolder = like.StateFolder,
            AgentsAllowed = like.AgentsAllowed,
            AgentAnswerLimit = like.AgentAnswerLimit,
            Autosaves = like.Autosaves,
            StartsLanguageServer = like.StartsLanguageServer,
            Releases = like.Releases,
            Notes = like.Notes,
            DiscordIpc = like.DiscordIpc,
            DiscordIdleAfter = like.DiscordIdleAfter,
            Width = like.ClientSize.Width > 0 ? like.ClientSize.Width : like.Width,
            Height = like.ClientSize.Height > 0 ? like.ClientSize.Height : like.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = like.Position + new PixelPoint(Cascade, Cascade),
        };
        window.StartBeside();
        // Cascaded from the one it is opened from, yet never past the edge of that one's screen.
        window.FitToScreen(keepPlace: true);
        if (like.KeepsLayout)
        {
            window.KeepLayout();
        }
        if (like.KeepsSettings)
        {
            window.KeepSettings();
        }
        window.Show();
        if (like.SharesActivity)
        {
            window.ShowActivityInDiscord();
        }
        return window;
    }

    /// <summary>Next Solution Window (a step of 1) and Previous Solution Window (-1): the window after or before this
    /// one, in the order they opened, brought forward; with one window, nothing.</summary>
    public static void Step(MainWindow from, int step)
    {
        UiThread.Check();
        List<MainWindow> shown = [.. Open.Where(w => w.IsVisible)];
        int at = shown.IndexOf(from);
        if (shown.Count < 2 || at < 0)
        {
            return;
        }
        BringForward(shown[(at + step + shown.Count) % shown.Count]);
    }

    /// <summary>A window in front of the rest, the keys its, as choosing it from the Window menu does.</summary>
    public static void BringForward(MainWindow window)
    {
        UiThread.Check();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
        InFront(window);
    }

    /// <summary>File, Exit (the application menu's Quit on macOS): every window closes as the person closing it would,
    /// saving its files, the last opened first, and each one's solution is reopened by the next start. A window that
    /// cannot close (a file it could not save) keeps itself and those not yet closed open; false then, and
    /// <paramref name="givenUp"/> is told. One that asks the person first holds the quit until they answer, when it
    /// carries on or is given up (<see cref="Answered"/>).</summary>
    public static bool Quit(Action? givenUp = null)
    {
        UiThread.Check();
        Quitting = true;
        try
        {
            foreach (MainWindow window in Enumerable.Reverse(Open).ToList())
            {
                window.Close();
                if (window.IsVisible)
                {
                    if (window.AsksBeforeClosing)
                    {
                        quitGivenUp += givenUp;
                    }
                    else
                    {
                        givenUp?.Invoke();
                    }
                    return false;
                }
            }
            return true;
        }
        finally
        {
            Quitting = false;
        }
    }

    /// <summary>A window closed as Passenger quit (by <see cref="Quit"/> or the system's) asked what to do with changes it
    /// could not save, and the person answered: the quit carries on, every window still open closing and keeping its
    /// solution, or, kept open, it is given up.</summary>
    internal static void Answered(bool carryOn)
    {
        UiThread.Check();
        Action? givenUp = quitGivenUp;
        quitGivenUp = null;
        if (carryOn)
        {
            Quit(givenUp);
        }
        else
        {
            givenUp?.Invoke();
        }
    }

    /// <summary>Files or folders the system hands Passenger (the Finder's Open, a drop on the Dock's icon), opened as File,
    /// Open opens them from the window in front: a solution, project or folder asked where, unless Settings says.</summary>
    public static void OpenFromSystem(IReadOnlyList<string> paths)
    {
        UiThread.Check();
        if (Front is not { IsStarted: true } from)
        {
            Pending.AddRange(paths);
            return;
        }
        foreach (string path in paths)
        {
            from.Open(path);
        }
    }

    /// <summary>What the system asked to open before the first window started, taken once.</summary>
    internal static IReadOnlyList<string> TakePending()
    {
        string[] taken = [.. Pending];
        Pending.Clear();
        return taken;
    }

    /// <summary>The window watching for updates, which hands it on as it closes.</summary>
    internal static void WatchUpdatesIn(MainWindow window) => updates = window;

    /// <summary>Once every window has closed: waits, for at most each one's limit, for what they started to end
    /// (<see cref="MainWindow.Ended"/>), then installs an update made ready, last of all. It needs no UI thread.</summary>
    public static void End()
    {
        Task[] ending;
        lock (Ending)
        {
            ending = [.. Ending];
        }
        Task.WaitAll(ending);
        updates?.InstallUpdate();
    }

    /// <summary>Discord's presence for the windows looking for it at one place.</summary>
    internal static DiscordPresence Presence(DiscordIpc ipc)
    {
        if (!Presences.TryGetValue(ipc, out DiscordPresence? presence))
        {
            presence = new DiscordPresence(ipc, IdeErrors.Write);
            Presences[ipc] = presence;
        }
        return presence;
    }

    /// <summary>The window whose activity Discord shows: of those showing it at that place, the one in front last that has
    /// a solution open, else the one in front last (whose activity is none).</summary>
    internal static MainWindow? ShowsInDiscord(DiscordIpc ipc) =>
        Fronts.LastOrDefault(w => w.SharesActivity && w.Folder is not null && ReferenceEquals(w.DiscordIpc, ipc))
        ?? Fronts.LastOrDefault(w => w.SharesActivity && ReferenceEquals(w.DiscordIpc, ipc));

    /// <summary>A window stops showing its activity: the window then in front shows its own, or with none left Discord
    /// is let go of, which the closing window's end waits for.</summary>
    internal static Task LetGoOfDiscord(DiscordIpc ipc)
    {
        if (ShowsInDiscord(ipc) is { } next)
        {
            next.FollowInDiscord();
            return Task.CompletedTask;
        }
        if (!Presences.Remove(ipc, out DiscordPresence? presence))
        {
            return Task.CompletedTask;
        }
        return presence.CloseAsync();
    }

    private static void InFront(MainWindow window)
    {
        if (Fronts.Count > 0 && Fronts[^1] == window)
        {
            return;
        }
        Fronts.Remove(window);
        if (Open.Contains(window))
        {
            Fronts.Add(window);
        }
        FrontChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Remove(MainWindow window)
    {
        Open.Remove(window);
        Fronts.Remove(window);
        if (window.Ended is { } ended)
        {
            lock (Ending)
            {
                Ending.RemoveAll(e => e.IsCompleted);
                Ending.Add(ended);
            }
        }
        if (updates == window && window.HandsOnUpdates() && Front is { } next)
        {
            // Not at once: the closing window's own end (its update's word in the log) comes first.
            updates = next;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => next.TakeOverUpdates(window));
        }
        FrontChanged?.Invoke(null, EventArgs.Empty);
    }
}
