using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Passenger.App.Welcome;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Opening a workspace: File, Open Solution (a solution or project) and Open Folder, with Open File for a file in
/// the editor, and the recent folders, solutions and projects, kept in the store's recentSolutions.json by the window a
/// person uses as each opens, listed by the Welcome screen (in the editor area while nothing is open), File, Recent
/// Solutions and the toolbar's project widget, and those open as Passenger quit reopened on a start that names nothing.
/// Each opens here or in a window of its own (MainWindow.ProjectWindows). Clone Repository is beside them.</summary>
public partial class MainWindow
{
    /// <summary>The Welcome screen while it is shown or asked for: let go of while a folder is open, made again as it shows.</summary>
    private WelcomeView? welcome;

    /// <summary>What the Welcome screen says (a recent one gone, a file that would not open), kept while it is not made.</summary>
    private string? welcomeSays;

    /// <summary>Why the Welcome screen's New Solution greys, as the action does; null where it runs. Kept while it is not made.</summary>
    private string? newSolutionWhyNot;

    /// <summary>What Open Solution's panel offers, and all it offers: the files that open as the workspace.</summary>
    private static readonly FilePickerFileType SolutionsAndProjects = new("Solutions and Projects") { Patterns = [.. WorkspaceModel.OpenedExtensions.Select(e => "*" + e)] };

    /// <summary>Asks the person for a folder, by a title, in the system's own panel, beside the folder opened last.</summary>
    public Func<string, Task<string?>> PickFolder
    {
        get => pickFolder ?? PickFolderAsync;
        set => pickFolder = value;
    }

    private Func<string, Task<string?>>? pickFolder;

    /// <summary>The Welcome screen, which the editor area shows until a folder opens: made as it is first asked for, with the
    /// recent ones, what it says and New Solution's state.</summary>
    public WelcomeView Welcome => welcome ??= MadeWelcome();

    private void AddWelcome()
    {
        Editors.ShowWelcome(Welcome);
        AddNewProjectActions();
        // The system's panels ask the person, never an agent.
        Func<ActionContext, string?> persons = c => c.ByAgent ? Refusals.AsksThePerson : null;
        actions!.Add(new IdeAction(AppActions.OpenSolution, "Open Solution…", "IDE", _ => IdeErrors.Observe(PickSolutionAsync()))
        {
            WhyNot = persons,
            Menus = [new MenuPlace("File", 0, -1)],
            KeepsMenuOrder = true,
            Words = ["Open Project"],
        });
        actions.Add(new IdeAction(AppActions.OpenFolder, "Open Folder…", "IDE", _ => IdeErrors.Observe(OpenPickedFolderAsync()))
        {
            WhyNot = persons,
            Menus = [new MenuPlace("File", 0, 0)],
            KeepsMenuOrder = true,
        });
        actions.Add(new IdeAction(AppActions.OpenFile, "Open File…", "IDE", _ => IdeErrors.Observe(PickFileAsync()))
        {
            WhyNot = persons,
            Menus = [new MenuPlace("File", 0, 1)],
            KeepsMenuOrder = true,
        });
        actions.Add(new IdeAction(AppActions.CloneRepository, "Clone Repository…", "Git", _ => ShowCloneRepository())
        {
            Menus = [new MenuPlace("File", 0, 2), new MenuPlace("Git", 4, 0)],
        });
    }

    private WelcomeView MadeWelcome()
    {
        WelcomeView made = new();
        made.NewSolutionRequested += (_, _) => Request(AppActions.NewSolution);
        made.OpenSolutionRequested += (_, _) => Request(AppActions.OpenSolution);
        made.OpenFolderRequested += (_, _) => Request(AppActions.OpenFolder);
        made.OpenFileRequested += (_, _) => Request(AppActions.OpenFile);
        made.CloneRequested += (_, _) => Request(AppActions.CloneRepository);
        made.Chosen += (_, row) => OpenRecent(row.Path);
        made.RemoveRequested += (_, row) => ForgetRecent(row.Path);
        made.RevealRequested += (_, row) => ShowInFileManager(row.Path);
        made.Show(RecentSolutions);
        made.Say(welcomeSays);
        GreyNewSolution(made);
        return made;
    }

    /// <summary>What the Welcome screen says, now where it is made and as it is made next.</summary>
    private void SayOnWelcome(string? text)
    {
        welcomeSays = text;
        welcome?.Say(text);
    }

    /// <summary>The Welcome screen's New Solution greys as the action does, saying why on its tip.</summary>
    private void GreyNewSolution(WelcomeView shown)
    {
        ToolButtons.Grey(shown.NewSolutionButton, newSolutionWhyNot);
    }

    /// <summary>Opens what File, Open Solution or Open Folder chose (or a recent one, the system's, a new solution or a
    /// clone): a solution (.sln, .slnx) or C# project (.csproj) as the workspace over its folder, or a folder, here or in
    /// another window as Settings says or the person answers (<see cref="Asking"/>); a file-based program with no folder
    /// open, its folder with it in a tab; any other file in a tab here. Null once open or asked where, else why not, which
    /// the status bar and the Welcome screen say; <paramref name="then"/> is told the window a workspace opened in once
    /// begun.</summary>
    public string? Open(string path, Action<MainWindow>? then = null)
    {
        string full = PathNames.Canonical(path);
        if (File.Exists(full) && Folder is null && WorkspaceModel.OpensAsProgram(full))
        {
            // Its folder makes it a project of its own, which runs and which the language server loads.
            SayOnWelcome(null);
            OpenChosen(Path.GetDirectoryName(full)!, window =>
            {
                window.OpenFile(full);
                then?.Invoke(window);
            });
            return null;
        }
        if (File.Exists(full) && !WorkspaceModel.Opens(full))
        {
            string? problem = OpenFile(full);
            SayOnWelcome(problem);
            return problem;
        }
        string? why = Directory.Exists(full) || File.Exists(full) ? null : $"{RecentRow.Home(full)} was not opened: it does not exist";
        SayOnWelcome(why);
        if (why is not null)
        {
            status.Text = why;
            return why;
        }
        OpenChosen(full, then);
        return null;
    }

    /// <summary>File, Open Solution: the system's panel offering only the solutions and projects that open as the workspace
    /// (<see cref="SolutionsAndProjects"/>), the one chosen opened as it.</summary>
    private async Task PickSolutionAsync()
    {
        if ((await PickFiles("Open Solution", [SolutionsAndProjects], false)) is [{ } path])
        {
            Open(path);
        }
    }

    private async Task OpenPickedFolderAsync()
    {
        if (await PickFolder("Open Folder") is { } path)
        {
            Open(path);
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(BesideLast()),
        });
        return picked.Count == 1 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>File, Open File: any file in the system's panel, with no filter, opened in the editor
    /// (<see cref="OpenInEditor"/>).</summary>
    private async Task PickFileAsync()
    {
        if ((await PickFiles("Open File", null, false)) is [{ } path])
        {
            OpenInEditor(path);
        }
    }

    /// <summary>Opens a file in the editor, never as the workspace: a solution or project as its text, a file-based program
    /// alone. In this window, a file of its folder where it lies inside, else on its own; why not, where it does not open,
    /// said on the status bar and the Welcome screen.</summary>
    private void OpenInEditor(string path)
    {
        string full = PathNames.Canonical(path);
        if (!File.Exists(full))
        {
            string gone = $"{RecentRow.Home(full)} was not opened: it does not exist";
            status.Text = gone;
            SayOnWelcome(gone);
            return;
        }
        SayOnWelcome(OpenFile(full));
    }

    /// <summary>The project widget: New Solution, Open Solution, Open Folder, Open File and Clone Repository, then with
    /// another window open the Open Solutions (this one checked), then the Recent Solutions to open.</summary>
    public ContextMenu ShowProjectMenu()
    {
        ActionContext context = actions!.Context();
        List<Control> items = [.. ((string[])[AppActions.NewSolution, AppActions.OpenSolution, AppActions.OpenFolder, AppActions.OpenFile, AppActions.CloneRepository])
            .Select(id => FlyoutEntry(new MenuEntry(id), context))];
        IReadOnlyList<ListedItem> windows = SolutionWindows();
        if (windows.Count > 1)
        {
            items.Add(new Separator());
            items.Add(ToolWindows.ListMenus.Heading("Open Solutions"));
            items.AddRange(windows.Select(FlyoutListed));
        }
        IReadOnlyList<(string Title, Action Open)> recent = RecentMenu();
        if (recent.Count > 0)
        {
            items.Add(new Separator());
            if (windows.Count > 1)
            {
                items.Add(ToolWindows.ListMenus.Heading(MainMenu.RecentSolutions));
            }
            items.AddRange(recent.Select(r => ToolWindows.ListMenus.Item(r.Title, r.Open)));
        }
        ContextMenu menu = new() { ItemsSource = items };
        Open(menu, ProjectButton);
        return menu;
    }

    /// <summary>The recent folders, solutions and projects, the newest first, each marked when it is gone from disk; none in
    /// a window that keeps no state.</summary>
    public IReadOnlyList<RecentRow> RecentSolutions =>
        keepsWorkspace ? [.. state!.Recent.Value.Newest().Select(recent => RecentRow.Of(recent.Path, recent.Entry))] : [];

    /// <summary>Opens a recent folder, solution or project, as the Welcome screen and File, Recent Solutions do: null once it
    /// is open, else why not (it is gone), which the status bar and the Welcome screen say.</summary>
    public string? OpenRecent(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            string gone = $"{RecentRow.Home(path)} no longer exists: it was moved or deleted, or its drive is not connected";
            status.Text = gone;
            SayOnWelcome(gone);
            ShowRecent();
            return gone;
        }
        return Open(path);
    }

    /// <summary>Takes a folder, solution or project off the recent ones for good.</summary>
    public void ForgetRecent(string path)
    {
        if (keepsWorkspace)
        {
            state!.Recent.Set(state.Recent.Value.Without(path));
            SayOnWelcome(null);
            ShowRecent();
        }
    }

    /// <summary>Puts the workspace just opened at the top of the recent ones as what it was opened as (its solution, the
    /// project opened alone, or the folder without either), by the name the toolbar shows.</summary>
    private void Remember()
    {
        if (!keepsWorkspace || session.Workspace is not { } workspace)
        {
            return;
        }
        RememberOpen(workspace.OpenedAs);
        state!.Recent.Set(state.Recent.Value.Opened(workspace.OpenedAs, workspace.Name, DateTimeOffset.UtcNow));
        ShowRecent();
    }

    /// <summary>The recent ones on the Welcome screen, and in the system's lists outside the window
    /// (<see cref="RecentShortcuts"/>), which only a Passenger a person sees sets.</summary>
    private void ShowRecent()
    {
        welcome?.Show(RecentSolutions);
        if (keepsWorkspace)
        {
            RecentShortcuts.Follow(this);
        }
    }

    /// <summary>The recent list this window keeps, or null where it keeps no state.</summary>
    internal State.StateDocument<RecentSolutionsState>? RecentKept => keepsWorkspace ? state!.Recent : null;

    /// <summary>The recent folders, solutions and projects as the system's lists outside the window give them, the newest
    /// first, those gone from disk left out, each with its path; none in a window that keeps no state.</summary>
    public IEnumerable<(string Path, RecentShortcut Shortcut)> Shortcuts =>
        keepsWorkspace
            ? RecentShortcut.Recent(state!.Recent.Value.Newest().Select(r => (r.Path, r.Entry.Name)), path => Directory.Exists(path) || File.Exists(path))
            : [];

    /// <summary>Forgets the recent ones the person took off the taskbar's jump list, as Forget on the Welcome screen does,
    /// leaving that list to the change under way.</summary>
    internal void ForgetRemoved(IReadOnlyList<string> paths)
    {
        if (!keepsWorkspace)
        {
            return;
        }
        state!.Recent.Set(paths.Aggregate(state.Recent.Value, (kept, path) => kept.Without(path)));
        welcome?.Show(RecentSolutions);
    }

    /// <summary>Started with nothing named, the window that keeps state reopens what was open as Passenger last quit,
    /// unless Reopen projects on startup is off: the oldest here and each other in a window of its own, the newest last
    /// and so in front. One gone from disk is left on the Welcome screen, marked, with why.</summary>
    private void ReopenLast()
    {
        if (!settings.ReopenProjectsOnStartup || !keepsWorkspace)
        {
            return;
        }
        bool here = true;
        foreach ((string path, RecentSolution entry) in state!.Recent.Value.StillOpen())
        {
            RecentRow row = RecentRow.Of(path, entry);
            if (row.Missing)
            {
                string gone = $"{row.Name} was not reopened: {row.Shown} no longer exists";
                status.Text = gone;
                SayOnWelcome(gone);
                continue;
            }
            PassengerApp.OpenProject(path, here ? OpenTarget.ThisWindow : OpenTarget.NewWindow, this);
            here = false;
        }
    }

    /// <summary>Clone Repository: the dialog over this window, and the clone opened once it is made.</summary>
    public Vcs.CloneDialog ShowCloneRepository()
    {
        Vcs.CloneDialog dialog = new(BesideLast());
        dialog.Closed += (_, _) =>
        {
            if (dialog.Cloned is { } cloned)
            {
                // A clone the person made here is trusted as it opens, never asked of, but in a Git repository only the person trusts.
                if (WorkspaceTrust.TrustsAlone(PathNames.Canonical(cloned)))
                {
                    TrustFolder(PathNames.Canonical(cloned));
                }
                Open(cloned);
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>Beside the folder opened last, else the home folder: where a clone goes unless the person says otherwise,
    /// where Open Folder's panel starts, and a file panel with no folder open.</summary>
    private string BesideLast()
    {
        string? last = RecentSolutions.FirstOrDefault()?.Path;
        string? lastFolder = last is null ? null : File.Exists(last) ? Path.GetDirectoryName(Path.GetDirectoryName(last)) : Path.GetDirectoryName(last);
        return lastFolder is not null && Directory.Exists(lastFolder) ? lastFolder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>File, Recent Solutions: each but those open in a window, newest first, by name and where it is.</summary>
    private IReadOnlyList<(string Title, Action Open)> RecentMenu() =>
        [.. RecentSolutions.Where(r => !PassengerApp.Windows.Append(this).Distinct().Any(w => w.Folder is { } open
                && (PathNames.Same(r.Path, open) || PathNames.Same(r.Path, w.WorkspaceModel?.OpenedAs ?? string.Empty))))
            .Select(r => ($"{r.Name} – {r.Shown}", (Action)(() => OpenRecent(r.Path))))];
}
