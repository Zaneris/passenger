using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Passenger.App.Welcome;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Opening a workspace: File, Open (a solution or project) and Open Folder, and the recent folders, solutions
/// and projects, kept in the store's recentSolutions.json by the window a person uses as each opens, listed by the
/// Welcome screen (in the editor area while nothing is open), File, Recent Solutions and the toolbar's project widget,
/// and those open as Passenger quit reopened on a start that names nothing. Each opens here or in a window of its own
/// (MainWindow.ProjectWindows). Clone Repository is beside them.</summary>
public partial class MainWindow
{
    private readonly WelcomeView welcome = new();

    /// <summary>What Open's panel offers beside All Files, which it lists first since Open takes any file.</summary>
    private static readonly FilePickerFileType SolutionsAndProjects = new("Solutions and Projects") { Patterns = ["*.sln", "*.slnx", "*.csproj"] };

    /// <summary>The Welcome screen, which the editor area shows until a folder opens.</summary>
    public WelcomeView Welcome => welcome;

    private void AddWelcome()
    {
        Editors.ShowWelcome(welcome);
        welcome.NewSolutionRequested += (_, _) => Request(AppActions.NewSolution);
        welcome.OpenRequested += (_, _) => Request(AppActions.Open);
        welcome.OpenFolderRequested += (_, _) => Request(AppActions.OpenFolder);
        welcome.CloneRequested += (_, _) => Request(AppActions.CloneRepository);
        welcome.Chosen += (_, row) => OpenRecent(row.Path);
        welcome.RemoveRequested += (_, row) => ForgetRecent(row.Path);
        welcome.RevealRequested += (_, row) => ShowInFileManager(row.Path);
        AddNewProjectActions();
        actions!.Add(new IdeAction(AppActions.OpenFolder, "Open Folder…", "IDE", context => IdeErrors.Observe(PickFolder())) { Menus = [new MenuPlace("File", 0, 1)] });
        actions.Add(new IdeAction(AppActions.CloneRepository, "Clone Repository…", "Git", _ => ShowCloneRepository())
        {
            Menus = [new MenuPlace("File", 0, 2), new MenuPlace("Git", 4, 0)],
        });
    }

    /// <summary>Opens what File, Open or Open Folder chose (or a recent one, the system's, a new solution or a clone):
    /// a solution (.sln, .slnx) or C# project (.csproj) as the workspace over its folder, or a folder, here or in
    /// another window as Settings says or the person answers (<see cref="Asking"/>); any other file in a tab here. Null
    /// once open or asked where, else why not, which the status bar and the Welcome screen say; <paramref name="then"/>
    /// is told the window a workspace opened in once its session has begun.</summary>
    public string? Open(string path, Action<MainWindow>? then = null)
    {
        string full = PathNames.Canonical(path);
        if (File.Exists(full) && !WorkspaceModel.Opens(full))
        {
            string? problem = OpenFile(full);
            welcome.Say(problem);
            return problem;
        }
        string? why = Directory.Exists(full) || File.Exists(full) ? null : $"{RecentRow.Home(full)} was not opened: it does not exist";
        welcome.Say(why);
        if (why is not null)
        {
            status.Text = why;
            return why;
        }
        OpenChosen(full, then);
        return null;
    }

    /// <summary>File, Open: any file in the system's panel, a solution or project opening as the workspace. The panel
    /// takes files or folders, never both, so a folder is Open Folder's.</summary>
    private async Task PickWorkspace()
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            FileTypeFilter = [FilePickerFileTypes.All, SolutionsAndProjects],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(BesideLast()),
        });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            Open(path);
        }
    }

    private async Task PickFolder()
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Folder",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(BesideLast()),
        });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            Open(path);
        }
    }

    /// <summary>The project widget: New Solution, Open, Open Folder and Clone Repository, then with another window open
    /// the Open Solutions (this one checked), then the Recent Solutions to open.</summary>
    public ContextMenu ShowProjectMenu()
    {
        ActionContext context = actions!.Context();
        List<Control> items = [.. ((string[])[AppActions.NewSolution, AppActions.Open, AppActions.OpenFolder, AppActions.CloneRepository]).Select(id => FlyoutEntry(new MenuEntry(id), context))];
        IReadOnlyList<ListedItem> windows = SolutionWindows();
        if (windows.Count > 1)
        {
            items.Add(new Separator());
            items.Add(Heading("Open Solutions"));
            items.AddRange(windows.Select(FlyoutListed));
        }
        IReadOnlyList<(string Title, Action Open)> recent = RecentMenu();
        if (recent.Count > 0)
        {
            items.Add(new Separator());
            if (windows.Count > 1)
            {
                items.Add(Heading(MainMenu.RecentSolutions));
            }
            items.AddRange(recent.Select(r => Command(r.Title, r.Open)));
        }
        ContextMenu menu = new() { ItemsSource = items };
        Open(menu, ProjectButton);
        return menu;

        static MenuItem Heading(string text) => new() { Header = text, IsEnabled = false };
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
            welcome.Say(gone);
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
            welcome.Say(null);
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

    private void ShowRecent() => welcome.Show(RecentSolutions);

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
                welcome.Say(gone);
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
                Open(cloned);
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>Beside the folder opened last, else the home folder: where a clone goes unless the person says otherwise,
    /// and where Open's panel starts.</summary>
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
