using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The solution's shared projects keep their items files as the Explorer makes, moves and deletes their files: a
/// file made in a shared project's folder is written into its items file, one deleted taken out, within the command that
/// made or deleted it, so one Undo takes back both; moves follow in <see cref="FollowMoved"/>. Include in Project and Exclude
/// from Project take a file in or out alone, and a project stops importing one from its Dependencies.</summary>
public partial class MainWindow
{
    /// <summary>The items file of the solution's shared project whose folder holds a path (<see cref="ProjectSession.SharedProjectOf"/>),
    /// where the items file's own folder holds it too; null for a path in none.</summary>
    private string? SharedItemsOf(string path) =>
        session.SharedProjectOf(path) is { } shared && SharedProjects.ItemsFileOf(shared) is var items && PathNames.IsUnder(path, Path.GetDirectoryName(items)!)
            ? items : null;

    /// <summary>Whether any of these paths lies in a shared project's folder.</summary>
    private bool InShared(IEnumerable<string> paths) => paths.Any(p => SharedItemsOf(p) is not null);

    /// <summary>Files made in shared projects' folders written into their items files, as part of the command recording
    /// them: null once written, else why not.</summary>
    private string? IntoShared(IEnumerable<string> made, CommandHistory.Recording recording) => KeepShared(made, recording, SharedProjects.Add);

    /// <summary>Files and folders deleted from shared projects' folders taken out of their items files, as part of the
    /// command recording them: null once written, else why not.</summary>
    private string? OutOfShared(IEnumerable<string> gone, CommandHistory.Recording recording) => KeepShared(gone, recording, SharedProjects.Remove);

    private string? KeepShared(IEnumerable<string> paths, CommandHistory.Recording recording, Func<string, IReadOnlyCollection<string>, bool> change)
    {
        foreach (IGrouping<string, string> held in paths.Select(p => (Path: p, Items: SharedItemsOf(p))).Where(p => p.Items is not null && File.Exists(p.Items))
            .GroupBy(p => p.Items!, p => p.Path, PathNames.Comparer))
        {
            string items = held.Key;
            try
            {
                recording.Watch(items);
                if (SaveBeforeWriting([items]) is { } unsaved)
                {
                    return $"{Path.GetFileName(items)} could not follow the files: {unsaved}";
                }
                if (change(items, [.. held]))
                {
                    // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                    FollowWritten([items]);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"{Path.GetFileName(items)} could not follow the files: {error.Message}";
            }
        }
        return null;
    }

    /// <summary>The rows Include in Project (<paramref name="unlisted"/>) or Exclude from Project takes: the row, or every
    /// row chosen with it, that is a file of a shared project's folder its items file leaves out, or names.</summary>
    private IReadOnlyList<ExplorerNode> SharedRows(ExplorerNode row, bool unlisted) =>
        [.. (Explorer.SelectedNodes.Contains(row) ? Explorer.SelectedNodes : [row]).Where(n => n is { Kind: NodeKind.File, Shared: not null, Path: not null } && n.Unlisted == unlisted)];

    private void SetShared(IReadOnlyList<ExplorerNode> rows, bool include) => IdeErrors.Observe(SetSharedAsync([.. rows.Select(r => r.Path!)], include));

    /// <summary>Include in Project or Exclude from Project: files of shared projects' folders written into their items files,
    /// or taken out of them and left on disk, one command Undo takes back. Null once done, else why not, which the status
    /// bar says too.</summary>
    public async Task<string?> SetSharedAsync(IReadOnlyList<string> paths, bool include)
    {
        string names = Plural.Listed([.. paths.Select(Path.GetFileName).OfType<string>()]);
        using CommandHistory.Recording recording = Editors.Commands.RecordWrites($"{(include ? "Include" : "Exclude")} {names}");
        List<string> items = [.. paths.Select(SharedItemsOf).OfType<string>().Distinct(PathNames.Comparer)];
        if (await recording.ClearAsync(items) is { } held)
        {
            return status.Text = held;
        }
        if ((include ? IntoShared(paths, recording) : OutOfShared(paths, recording)) is { } refused)
        {
            return status.Text = await recording.TakenBackAsync(refused);
        }
        if (await UnlandedAsync(recording) is { } unlanded)
        {
            return status.Text = await recording.TakenBackAsync(unlanded.Message);
        }
        // At once, rather than once the watcher hears the items file written.
        Explorer.UpdateProjects(session.Workspace!, [.. Solution?.Projects.Where(p => items.Any(i => PathNames.Same(SharedProjects.ItemsFileOf(p.Path), i))) ?? []]);
        string shared = string.Join(", ", items.Select(Path.GetFileNameWithoutExtension));
        status.Text = include ? $"Included {names} in {shared}" : $"Excluded {names} from {shared}; {Plural.Agreeing(paths.Count, "it stays", "they stay")} on disk";
        return null;
    }

    /// <summary>Remove on a shared project under a project's Dependencies, once asked: the project no longer imports its
    /// items file (<see cref="ProjectEdits.RemoveSharedImport"/>), as one command Undo takes back.</summary>
    public async Task<bool> RemoveSharedImportAsync(string project, string shared)
    {
        string name = Path.GetFileNameWithoutExtension(project);
        string other = Path.GetFileNameWithoutExtension(shared);
        if (!await Confirm("Remove Shared Project", $"Stop {name} importing {other}'s files?", "Remove"))
        {
            return false;
        }
        string items = SharedProjects.IsItemsFile(shared) ? shared : SharedProjects.ItemsFileOf(shared);
        string? problem = await ChangeFileAsync($"Remove {other} from {name}", [project], () => ProjectEdits.RemoveSharedImport(project, items));
        if (problem is null)
        {
            ReloadSolution();
        }
        status.Text = problem is null ? $"{name} no longer imports {other}" : $"{name} could not be changed: {problem}";
        return problem is null;
    }

    /// <summary>The files under a path now: a file itself, a folder's every file.</summary>
    private static IEnumerable<string> FilesAt(string path) => Directory.Exists(path)
        ? PathNames.FilesUnder(path, _ => true).SelectMany(f => f.Files)
        : File.Exists(path) ? [PathNames.Canonical(path)] : [];
}
