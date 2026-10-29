using Avalonia.Platform.Storage;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The Solution view's edits to the solution itself: New Solution Folder, Existing Project and Existing Item
/// into a solution folder (or a folder or project into the solution), a folder's Rename, and Remove of a folder (with
/// all it holds, once asked) or of a file it lists. Each changes the .sln or .slnx through <see cref="SolutionEdits"/>,
/// and the Explorer, Build, Run and the language server read the solution again.</summary>
public partial class MainWindow
{
    /// <summary>Asks the person for files (Existing Project, Existing Item, Compare With, an Aspire AppHost's file
    /// input), by a title, the types it takes (any file with none) and whether it takes several, in the system's own
    /// dialog.</summary>
    public Func<string, IReadOnlyList<FilePickerFileType>?, bool, Task<IReadOnlyList<string>>> PickFiles
    {
        get => pickFiles ?? PickFilesAsync;
        set => pickFiles = value;
    }

    private Func<string, IReadOnlyList<FilePickerFileType>?, bool, Task<IReadOnlyList<string>>>? pickFiles;

    private async Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FilePickerFileType>? types, bool several)
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = several,
            SuggestedStartLocation = Folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(Folder),
            FileTypeFilter = types,
        });
        return [.. picked.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <summary>New Solution Folder: a folder in a solution folder (by its path in the solution), or at the solution's
    /// top with none. Null once made, else why not.</summary>
    public async Task<string?> AddSolutionFolderAsync(string? parent, string name)
    {
        string? problem = await EditSolutionAsync(file => SolutionEdits.AddFolder(file, parent, name), $"Added solution folder {name}");
        if (problem is null)
        {
            Explorer.RevealSolutionFolder($"{parent ?? "/"}{name}/");
        }
        return problem;
    }

    /// <summary>Renames a solution folder, which stays open if it was.</summary>
    public async Task<string?> RenameSolutionFolderAsync(string path, string name)
    {
        string renamed = $"{path[..(path.TrimEnd('/').LastIndexOf('/') + 1)]}{name}/";
        bool open = Explorer.Tree.Rows.Any(r => r.SolutionPath == path && r.Kind == NodeKind.SolutionFolder && r.IsExpanded);
        string? problem = await EditSolutionAsync(file => SolutionEdits.RenameFolder(file, path, name), $"Renamed solution folder {FolderName(path)} to {name}");
        if (problem is null)
        {
            Explorer.RevealSolutionFolder(renamed, open);
        }
        return problem;
    }

    /// <summary>Remove on a solution folder: once the person agrees to what goes with it (its projects, the folders and
    /// files in it), all of it leaves the solution, and its projects the solution filter open, as one command Undo takes
    /// back whole; every file stays on disk.</summary>
    public async Task<bool> RemoveSolutionFolderAsync(string path)
    {
        if (Solution is not { } solution || FindFolder(solution.Entries, path) is not { } removing)
        {
            return false;
        }
        List<SolutionEntry> under = [.. Under(removing)];
        List<string> going = [];
        Name(under.OfType<ProjectModel>(), "project");
        Name(under.OfType<SolutionFolder>(), "solution folder");
        Name(under.OfType<SolutionItem>(), "file");
        string question = going.Count == 0
            ? $"Remove the empty solution folder {removing.Name} from {solution.Name}?"
            : $"Remove the solution folder {removing.Name} from {solution.Name}, and with it {Together(going)}? Their files stay on disk.";
        if (!await Confirm("Remove Solution Folder", question, "Remove"))
        {
            return false;
        }
        return await EditSolutionAsync(file => SolutionEdits.RemoveFolder(file, path), $"Removed solution folder {removing.Name} from {solution.Name}",
            [.. under.OfType<ProjectModel>().Select(p => p.Path)], $"Remove Solution Folder {removing.Name}") is null;

        void Name(IEnumerable<SolutionEntry> entries, string kind)
        {
            List<string> names = [.. entries.Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase)];
            if (names.Count > 0)
            {
                going.Add($"the {Plural.Noun(names.Count, kind)} {Together(names)}");
            }
        }
    }

    /// <summary>Takes a file out of the solution folder that lists it; the file stays on disk.</summary>
    public async Task<bool> RemoveSolutionItemAsync(string holder, string path)
    {
        string name = Path.GetFileName(path);
        if (!await Confirm("Remove Item", $"Remove {name} from the solution folder {FolderName(holder)}? The file stays on disk.", "Remove"))
        {
            return false;
        }
        return await EditSolutionAsync(file => SolutionEdits.RemoveFile(file, holder, path), $"Removed {name} from {FolderName(holder)}") is null;
    }

    /// <summary>Add Existing Project: the projects the person picks, put in a solution folder, or at the solution's top
    /// with none, and in the solution filter open, as one command Undo takes back whole.</summary>
    public async Task AddExistingProjectsAsync(string? into)
    {
        string where = into is null ? "the Solution" : $"Solution Folder '{FolderName(into)}'";
        IReadOnlyList<string> picked = await PickFiles($"Add Existing Project to {where}",
            [new FilePickerFileType("Projects") { Patterns = ["*.csproj", "*.fsproj", "*.vbproj"] }], true);
        if (picked.Count == 0)
        {
            return;
        }
        using CommandHistory.Recording recording = Editors.Commands.RecordWrites($"Add Existing Project {Together([.. picked.Select(Path.GetFileNameWithoutExtension).OfType<string>()])}");
        if (await AddEachAsync(picked, (solution, project) => SolutionEdits.AddProject(solution, into, project), Path.GetFileNameWithoutExtension, recording, projects: true) is { } last)
        {
            Reveal(into, last);
        }
    }

    /// <summary>Add Existing Item: the files the person picks, listed in a solution folder.</summary>
    public async Task AddExistingItemsAsync(string into)
    {
        IReadOnlyList<string> picked = await PickFiles($"Add Existing Item to '{FolderName(into)}'", null, true);
        if (await AddEachAsync(picked, (solution, file) => SolutionEdits.AddFile(solution, into, file), Path.GetFileName) is { } last)
        {
            Reveal(into, last);
        }
    }

    /// <summary>Adds each path picked, then, once the solution's file has landed, reads the solution once: the last one
    /// added, or null with none, the status bar saying what was added or why one was not. Projects go in the solution
    /// filter open too; the files it writes are watched first by a command's recording, for its undo.</summary>
    private async Task<string?> AddEachAsync(IReadOnlyList<string> picked, Func<string, string, string?> add, Func<string, string> named,
        CommandHistory.Recording? recording = null, bool projects = false)
    {
        if (Solution is not { } solution || picked.Count == 0)
        {
            return null;
        }
        // Reopened, the filter would leave out a project it does not list.
        string? filter = projects ? session.Workspace?.Filter?.Path : null;
        string[] writing = filter is null ? [solution.Path] : [solution.Path, filter];
        if (SaveBeforeWriting(writing) is { } unsaved)
        {
            status.Text = $"{solution.Name} could not be changed: {unsaved}";
            return null;
        }
        foreach (string path in writing)
        {
            recording?.Watch(path);
        }
        List<string> added = [];
        List<string> refused = [];
        foreach (string path in picked.Select(PathNames.Canonical))
        {
            if (Change(() => add(solution.Path, path), solution.Name) is { } problem)
            {
                refused.Add(problem);
                continue;
            }
            added.Add(path);
            if (filter is not null && Change(() => SolutionEdits.AddToFilter(filter, path), Path.GetFileName(filter)) is { } unfiltered)
            {
                refused.Add(unfiltered);
            }
        }
        if (added.Count > 0 && await UnlandedAsync(writing) is { } unlanded)
        {
            added.Clear();
            refused = [$"{solution.Name} could not be changed: {unlanded.Message}"];
        }
        FollowWritten(writing);
        if (added.Count > 0)
        {
            ReloadSolution();
            refused.Insert(0, $"Added {Together([.. added.Select(named)])} to {solution.Name}");
        }
        status.Text = string.Join("; ", refused);
        return added.LastOrDefault();
    }

    /// <summary>Shows what was just added: in its solution folder, opened, or at the solution's top.</summary>
    private void Reveal(string? into, string added)
    {
        if (into is null)
        {
            Explorer.Reveal(added);
            return;
        }
        Explorer.RevealSolutionFolder(into, child: added);
    }

    /// <summary>Makes one change to the open solution's file and, once it has landed, reads the solution again: null when it
    /// was made, else why not, which the status bar says too. The projects it takes out (<paramref name="leaving"/>) leave
    /// the solution filter open too, both files changed in one command Undo takes back whole, named
    /// <paramref name="command"/>.</summary>
    private async Task<string?> EditSolutionAsync(Func<string, string?> edit, string done, IReadOnlyCollection<string>? leaving = null, string? command = null)
    {
        if (Solution is not { } solution)
        {
            return Refusals.NoSolution;
        }
        // Opened again, the filter would name a project the solution no longer has.
        string? filter = leaving is not null && session.Workspace?.Filter is { } open && leaving.Any(open.Projects.Contains) ? open.Path : null;
        string[] writing = filter is null ? [solution.Path] : [solution.Path, filter];
        using CommandHistory.Recording? recording = command is null ? null : Editors.Commands.RecordWrites(command);
        string? problem = SaveBeforeWriting(writing) is { } unsaved ? $"{solution.Name} could not be changed: {unsaved}" : null;
        string? unfiltered = null;
        if (problem is null)
        {
            foreach (string path in writing)
            {
                recording?.Watch(path);
            }
            problem = Change(() => edit(solution.Path), solution.Name);
            unfiltered = problem is null && filter is not null ? Change(() => SolutionEdits.RemoveFromFilter(filter, leaving!), Path.GetFileName(filter)) : null;
        }
        if (problem is null && await UnlandedAsync(writing) is { } unlanded)
        {
            problem = $"{solution.Name} could not be changed: {unlanded.Message}";
        }
        FollowWritten(writing);
        if (problem is null)
        {
            ReloadSolution();
        }
        status.Text = problem ?? (unfiltered is null ? done : $"{done}; {unfiltered}");
        return problem;
    }

    private static string? Change(Func<string?> edit, string solution)
    {
        try
        {
            return edit();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return $"{solution} could not be changed: {error.Message}";
        }
    }

    /// <summary>Run Unit Tests on projects or solution folders in the Solution view: the tests of the test projects
    /// among them, found first when they have not been.</summary>
    private void RunProjectTests(IReadOnlyList<string> projects, string what, bool debug)
    {
        HashSet<string> chosen = new(projects, StringComparer.Ordinal);
        RunTests(async () =>
        {
            if (Found().Count == 0 && await session.Tests.DiscoverAsync(Folder!) is { } problem)
            {
                return problem;
            }
            return Found() is { Count: > 0 } found ? await session.Tests.RunAsync(Folder!, found, debug) : $"No tests were found in {what}";
        });

        List<Testing.TestNode> Found() => [.. session.Tests.Tree.Methods.Where(m => m.ProjectPath is { } project && chosen.Contains(project))];
    }

    private static SolutionFolder? FindFolder(IEnumerable<SolutionEntry> entries, string path)
    {
        foreach (SolutionFolder candidate in entries.OfType<SolutionFolder>())
        {
            if (candidate.Path == path)
            {
                return candidate;
            }
            if (path.StartsWith(candidate.Path, StringComparison.Ordinal) && FindFolder(candidate.Children, path) is { } inside)
            {
                return inside;
            }
        }
        return null;
    }

    private static IEnumerable<SolutionEntry> Under(SolutionFolder parent) =>
        parent.Children.SelectMany(c => c is SolutionFolder inner ? Under(inner).Prepend(c) : [c]);

    /// <summary>A solution folder's own name, from its path in the solution.</summary>
    private static string FolderName(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    private static string Together(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
