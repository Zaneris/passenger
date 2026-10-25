using Avalonia.Controls;
using Avalonia.Input.Platform;
using Passenger.App.Explorer;
using Passenger.Editor;
using E = Passenger.Editor.EditorActions;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The Explorer's actions: a row's menu is the window's actions run on the row it opened on
/// (<see cref="ActionContext.ExplorerRow"/>), as a tab's menu runs them on its tab, and its keys are the keymap's, run
/// on the row chosen while the Explorer has them. One key runs two actions told apart by where it is pressed, so the
/// editor's own Cut, Copy, Paste, Delete, Undo and Redo act on the Explorer's rows (Rider's $Cut, $Delete and the rest;
/// MainWindow.FileMoves.cs), Rename renames a row (RenameElement), and the editor's Duplicate keys compare files where
/// Rider's Visual Studio keymap gives Compare Files (CompareTwoFiles) the same Ctrl+D (Cmd+D on macOS, apart from
/// Duplicate's Ctrl+D).</summary>
public partial class MainWindow
{
    private const string ExplorerGroup = "Explorer";
    private const string NoRow = "Nothing is chosen in the Explorer";

    private void AddExplorerActions()
    {
        Explorer.MenuFor = ExplorerMenu;
        Row(A.NewFile, "New File…", row => AskName("New File", string.Empty, name => CreateFile(NewGoesIn(row)!, name, string.Empty)), Holds);
        Row(A.NewFolder, "New Folder…", row => AskName("New Folder", string.Empty, name => CreateFolder(NewGoesIn(row)!, name)), Holds);
        // Class, Interface, Record, Struct and Enum share this place, chosen in its popup.
        Row(A.NewType, "Class/Interface…", row => AskType(NewGoesIn(row)!), Holds);
        Row(A.NewSolutionFolder, "New Solution Folder…", row => AskName("New Solution Folder", string.Empty, name => AddSolutionFolder(row.SolutionPath, name)),
            row => row.Kind is NodeKind.Solution or NodeKind.SolutionFolder ? null : "A solution folder goes in the solution or one of its folders");
        Row(A.AddExistingProject, "Add Existing Project…", row => IdeErrors.Observe(AddExistingProjectsAsync(row.SolutionPath)),
            row => row.Kind is NodeKind.Solution or NodeKind.SolutionFolder ? null : "A project is added to the solution or one of its folders", _ => "Existing Project…");
        Row(A.AddExistingItem, "Add Existing Item…", row => IdeErrors.Observe(AddExistingItemsAsync(row.SolutionPath!)),
            row => row is { Kind: NodeKind.SolutionFolder, SolutionPath: not null } ? null : "An existing item is added to a solution folder", _ => "Existing Item…");
        Row(A.AddReference, "Add Reference…", row => ShowAddReference(Referencing(row)!),
            row => Referencing(row) is null ? $"{row.Name} takes no reference" : null, row => row.Kind == NodeKind.Project ? "Reference…" : "Add Reference…");
        Row(A.AddDockerSupport, "Add Docker Support…", row => ShowDockerSupport(row.Path!),
            row => row is { Kind: NodeKind.Project, Path: not null } ? null : "Docker support is added to a project", _ => "Docker Support…");
        Row(A.AddDockerCompose, "Add Docker Compose…", _ => ShowDockerCompose(),
            row => row.Kind == NodeKind.Solution ? null : "Docker Compose is added to the solution", _ => "Docker Compose…");
        // The solution's, from its row or a solution folder's, and from Find Action wherever a solution is open.
        actions!.Add(new IdeAction(A.AddAspireOrchestration, "Add Aspire Orchestration…", ExplorerGroup, _ => ShowAddAspireOrchestration())
        {
            WhyNot = c => RowOf(c) is { } row
                ? row.Kind is NodeKind.Solution or NodeKind.SolutionFolder ? null : "Aspire orchestration is added to the solution"
                : Solution is null ? Refusals.NoSolution : null,
            TitleIn = c => c.ExplorerRow is null ? "Add Aspire Orchestration…" : "Aspire Orchestration…",
        });
        Row(A.ManageNuGet, "Manage NuGet Packages", _ => ShowToolWindow(ToolWindowIds.NuGet),
            row => row is { Kind: NodeKind.Project } or { Kind: NodeKind.Dependencies, Owner: not null } ? null : $"{row.Name} has no packages");
        Row(A.EditProjectFile, "Edit Project File", row => OpenFile(row.Path!), row => row is { Kind: NodeKind.Project, Path: not null } ? null : $"{row.Name} is not a project");
        Row(A.UnloadProject, "Unload Project", row => SetUnloaded(row.Path!, true),
            row => row is not { Kind: NodeKind.Project, Path: { } project } ? $"{row.Name} is not a project" : Explorer.Tree.Unloaded.Contains(project) ? $"{row.Name} is unloaded" : null);
        Row(A.ReloadProject, "Reload Project", row => SetUnloaded(row.Path!, false),
            row => row is not { Kind: NodeKind.Project, Path: { } project } ? $"{row.Name} is not a project" : Explorer.Tree.Unloaded.Contains(project) ? null : $"{row.Name} is loaded");
        // Compare With... for one file, Compare Files for two chosen together.
        actions!.Add(new IdeAction(A.CompareFiles, "Compare Files", ExplorerGroup, c => IdeErrors.Observe(CompareAsync(RowOf(c)!)))
        {
            WhyNot = c => RowOf(c) is not { } row ? NoRow : row is { Kind: NodeKind.File, IsSolutionItem: false, Path: not null } ? null : "Choose a file in the Explorer",
            TitleIn = c => RowOf(c) is { } row && Explorer.SelectedNodes.Contains(row) && Explorer.SelectedNodes.Count(n => n.IsFile) == 2 ? "Compare Files" : "Compare With…",
            // CompareTwoFiles is $default's Ctrl+D: the editor's Duplicate keys in Visual Studio's keymap off macOS.
            KeysOf = () => AppActions.Preset == KeymapPreset.VisualStudio && !OperatingSystem.IsMacOS() ? E.Duplicate : null,
        });
        // The path of what is chosen in the Explorer, else of the file shown.
        OnDisk(A.CopyPath, "Copy Path", path => IdeErrors.Observe(CopyPathAsync(path)));
        OnDisk(A.RevealInFileManager, FileManager.Showing, ShowInFileManager, FileManager.Name);
        OnDisk(A.OpenInTerminal, "Open in Terminal", path => IdeErrors.Observe(OpenTerminalAt(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!)), "Terminal");
        // Rider's RenameElement: the Explorer's row where its menu or keys ask, else what is at the editor's caret.
        actions.Add(new IdeAction(A.Rename, "Rename…", "Code", c =>
        {
            if (RowOf(c) is { } row)
            {
                RenameRow(row);
            }
            else
            {
                assist.Rename();
            }
        })
        {
            WhyNot = c => RowOf(c) is { } row ? (row.CanRename ? null : $"{row.Name} cannot be renamed") : assist.WhyNotRename(c),
            Menus = [new MenuPlace("Refactor", 1, 0)],
        });

        static string? Holds(ExplorerNode row) => NewGoesIn(row) is null ? $"Nothing new goes in {row.Name}" : null;
        static string? Referencing(ExplorerNode row) => row.Kind == NodeKind.Project ? row.Path : row.Kind == NodeKind.Dependencies ? row.Owner : null;
    }

    /// <summary>One of the Explorer's own actions, on its row.</summary>
    private void Row(string id, string title, Action<ExplorerNode> run, Func<ExplorerNode, string?> whyNot, Func<ExplorerNode, string>? titleIn = null) =>
        actions!.Add(new IdeAction(id, title, ExplorerGroup, c => run(RowOf(c)!))
        {
            WhyNot = c => RowOf(c) is { } row ? whyNot(row) : NoRow,
            TitleIn = titleIn is null ? null : c => RowOf(c) is { } row ? titleIn(row) : title,
        });

    /// <summary>An action on what is on disk: the Explorer's row while its menu or keys ask, else the file shown.</summary>
    private void OnDisk(string id, string title, Action<string> run, string? titleInMenu = null) =>
        actions!.Add(new IdeAction(id, title, ExplorerGroup, c => run(PathOnDisk(c)!))
        {
            WhyNot = c => PathOnDisk(c) is not null ? null : RowOf(c) is { } row ? $"{row.Name} is in no folder on disk" : Refusals.NoFile,
            TitleIn = titleInMenu is null ? null : c => c.ExplorerRow is null ? title : titleInMenu,
        });

    private string? PathOnDisk(ActionContext context) => RowOf(context) is { } row
        ? row.Kind is NodeKind.SolutionFolder or NodeKind.Dependencies or NodeKind.ProjectReference ? null : row.Path
        : context.File?.Path;

    /// <summary>The Explorer's row an action acts on: the one whose menu it was chosen from, else the one chosen while the
    /// Explorer has the keys (not its search field's).</summary>
    private ExplorerNode? RowOf(ActionContext context) => context.ExplorerRow ?? (ExplorerHasKeys(context) ? Explorer.Selected : null);

    private static bool ExplorerHasKeys(ActionContext context) =>
        context.ExplorerRow is not null || context.ToolWindow?.Id == ToolWindowIds.Explorer && context.Focused is not TextBox;

    /// <summary>Where something new goes from a row: into a folder or a project's folder, beside a file; nothing for what
    /// is the solution's own (made in a solution folder by its own actions) and the dependencies.</summary>
    private static string? NewGoesIn(ExplorerNode row) => row.IsSolutionItem ? null : row.Kind switch
    {
        NodeKind.Folder => row.Path,
        NodeKind.Project or NodeKind.Solution or NodeKind.File when row.Path is not null => Path.GetDirectoryName(row.Path),
        _ => null,
    };

    /// <summary>Rename on a row: a solution folder's name in the solution, a project's file with what names it, a file
    /// or folder on disk, offering to rename a C# type named after it.</summary>
    private void RenameRow(ExplorerNode row)
    {
        switch (row)
        {
            case { Kind: NodeKind.SolutionFolder, SolutionPath: { } renaming }:
                AskName("Rename Solution Folder", row.Name, name => RenameSolutionFolder(renaming, name));
                break;
            case { Kind: NodeKind.Project, Path: { } project }:
                AskName("Rename Project", Path.GetFileNameWithoutExtension(project), name => RenameProject(project, name));
                break;
            case { Path: { } path }:
                AskRename(path);
                break;
        }
    }

    /// <summary>Delete on a row: what is the solution's own (a project, a solution folder, a file it lists) and a
    /// project reference leave it, their files staying on disk; a file or folder goes to the Trash.</summary>
    private async Task DeleteRowAsync(ExplorerNode row)
    {
        switch (row)
        {
            case { Kind: NodeKind.SolutionFolder, SolutionPath: { } removing }:
                await RemoveSolutionFolderAsync(removing);
                break;
            case { IsSolutionItem: true, SolutionPath: { } holder, Path: { } item }:
                await RemoveSolutionItemAsync(holder, item);
                break;
            case { Kind: NodeKind.ProjectReference, Owner: { } owner, Referenced: { } referenced }:
                await RemoveReferenceAsync(owner, referenced);
                break;
            case { Kind: NodeKind.Project, Path: { } project }:
                await RemoveProjectAsync(project);
                break;
            case { Path: { } path }:
                await DeleteAsync(path);
                break;
        }
    }

    private async Task CopyPathAsync(string path)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(path);
            status.Text = $"Copied {path}";
        }
    }

    /// <summary>A row's menu, its items the window's actions run on the row: Build, Rebuild and Clean for the solution
    /// or the projects chosen, their unit tests, a project's packages, file and unloading, Add where things can go,
    /// Edit (the clipboard's actions, Delete or for the solution's own Remove, Rename, Find and Replace in Files), the
    /// path of what is on disk, Open In, Tools' Compare, Local History and Git's history. A project's Dependencies add
    /// a reference, and a project reference is removed. None for a row with nothing to do (a package).</summary>
    private IReadOnlyList<Control>? ExplorerMenu(ExplorerNode node)
    {
        bool onDisk = node.Kind is NodeKind.File or NodeKind.Folder or NodeKind.Project;
        bool holds = node.Kind is NodeKind.Folder or NodeKind.Project or NodeKind.Solution or NodeKind.File && !node.IsSolutionItem;
        bool solutionFolder = node.Kind == NodeKind.SolutionFolder;
        bool dependency = node is { Kind: NodeKind.Dependencies or NodeKind.ProjectReference, Owner: not null };
        if (!onDisk && !holds && !solutionFolder && !dependency)
        {
            return null;
        }
        MenuEntry separator = new(null);
        if (node.Kind == NodeKind.Dependencies && node.Owner is not null)
        {
            return Made([Item(A.AddReference), Item(A.ManageNuGet)]);
        }
        if (node.Kind == NodeKind.ProjectReference && node.Referenced is not null)
        {
            return Made([Item(E.Delete)]);
        }
        List<MenuEntry> entries = [];
        if (node.Kind is NodeKind.Solution or NodeKind.Project or NodeKind.SolutionFolder)
        {
            entries.AddRange(node.Kind == NodeKind.Solution
                ? [Item(A.Build), Item(A.RebuildSolution), Item(A.CleanSolution)]
                : [Item(A.BuildSelected), Item(A.RebuildSelected), Item(A.CleanSelected)]);
            if (node.Kind == NodeKind.Project)
            {
                entries.Add(Item(A.Publish));
            }
            entries.Add(separator);
        }
        if (node.Kind is NodeKind.Project or NodeKind.SolutionFolder)
        {
            entries.AddRange([Item(A.RunTests), Item(A.DebugTests), separator]);
        }
        if (node.Kind == NodeKind.Project)
        {
            bool unloaded = node.Path is { } project && Explorer.Tree.Unloaded.Contains(project);
            entries.AddRange([Item(A.ManageNuGet), Item(A.ManageUserSecrets), Item(A.EditProjectFile), Item(unloaded ? A.ReloadProject : A.UnloadProject), separator]);
        }
        if (holds || solutionFolder)
        {
            List<MenuEntry> add = [];
            if (node.Kind is NodeKind.Solution or NodeKind.SolutionFolder)
            {
                add.AddRange([Item(A.NewProject), Item(A.AddAspireOrchestration), Item(A.NewSolutionFolder), Item(A.AddExistingProject)]);
                if (node.Kind == NodeKind.Solution)
                {
                    add.Add(Item(A.AddDockerCompose));
                }
            }
            if (solutionFolder)
            {
                add.Add(Item(A.AddExistingItem));
            }
            if (holds)
            {
                if (add.Count > 0)
                {
                    add.Add(separator);
                }
                add.AddRange([Item(A.NewFile), Item(A.NewFolder), Item(A.NewType)]);
            }
            if (node.Kind == NodeKind.Project)
            {
                add.AddRange([separator, Item(A.AddReference), Item(A.AddDockerSupport)]);
            }
            entries.AddRange([Sub("Add", add), separator]);
        }
        List<MenuEntry> edit = [];
        if (node.IsMovable)
        {
            edit.AddRange([Item(E.Cut), Item(E.Copy)]);
        }
        if (node.Into is not null)
        {
            edit.Add(Item(E.Paste));
        }
        if (edit.Count > 0 && (node.CanRemove || node.CanRename))
        {
            edit.Add(separator);
        }
        if (node.CanRemove)
        {
            edit.Add(Item(E.Delete));
        }
        if (node.CanRename)
        {
            edit.Add(Item(A.Rename));
        }
        if (edit.Count > 0)
        {
            edit.Add(separator);
        }
        edit.AddRange([Item(A.FindInFiles), Item(A.ReplaceInFiles)]);
        entries.Add(Sub("Edit", edit));
        // A solution folder is in no folder on disk, so it has no path to copy or show.
        if (!solutionFolder)
        {
            entries.AddRange([Item(A.CopyPath), Sub("Open In", [Item(A.RevealInFileManager), Item(A.OpenInTerminal)])]);
        }
        if (node.Kind == NodeKind.File && !node.IsSolutionItem)
        {
            entries.Add(Sub("Tools", [Item(A.CompareFiles)]));
        }
        bool histories = node.Kind is NodeKind.File or NodeKind.Folder or NodeKind.Project or NodeKind.Solution && node.Path is not null && !node.IsSolutionItem;
        if (histories)
        {
            entries.AddRange([separator, Sub("Local History", [Item(A.ShowLocalHistory)])]);
        }
        if (node.Kind is NodeKind.File or NodeKind.Folder or NodeKind.Project or NodeKind.Solution && node.Path is { } versioned
            && session.Vcs.Repository?.IsInside(versioned) == true)
        {
            List<MenuEntry> git = node.Kind == NodeKind.File ? [Item(A.CompareWithRevision), Item(A.CompareWithBranch)] : [];
            git.Add(Item(A.ShowHistory));
            if (!histories)
            {
                entries.Add(separator);
            }
            entries.Add(Sub("Git", git));
        }
        return Made(entries);

        IReadOnlyList<Control> Made(IReadOnlyList<MenuEntry> made)
        {
            ActionContext context = new(Editors.Active, Editors.CodeEditor, FocusManager?.GetFocusedElement(), Dock[ToolWindowIds.Explorer],
                session.Debug.IsDebugging ? session.Debug.Session?.State : null)
            {
                ExplorerRow = node,
            };
            return [.. made.Select(entry => FlyoutEntry(entry, context))];
        }

        static MenuEntry Item(string action) => new(action);
        static MenuEntry Sub(string title, IReadOnlyList<MenuEntry> entries) => new(null, title, entries);
    }

    /// <summary>The editor's keys the Explorer takes while it has them: Cut, Copy, Paste, Delete, Backspace, Undo and
    /// Redo act on its chosen row, and the keys Compare Files shares (Duplicate's in Visual Studio's keymap off macOS)
    /// compare files. True once taken.</summary>
    private bool ExplorerTakes(string action)
    {
        ActionContext context = actions!.Context();
        string? taken = ExplorerEdits(action, context) ? action
            : actions[A.CompareFiles]?.KeysOf?.Invoke() == action && RowOf(context) is not null ? A.CompareFiles
            : null;
        if (taken is null)
        {
            return false;
        }
        Request(taken, context);
        return true;
    }
}
