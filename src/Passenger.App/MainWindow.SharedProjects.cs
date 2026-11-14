using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The solution's shared projects keep their items files as the Explorer makes, moves and deletes their files: a
/// file made in a shared project's folder is written into its items file, one deleted taken out, within the command that
/// made or deleted it, so one Undo takes back both; moves follow in <see cref="FollowMoved"/>.</summary>
public partial class MainWindow
{
    /// <summary>The items file of the solution's shared project whose folder holds a path, the nearest; null for a path in
    /// none, or in a project of its own inside one.</summary>
    private string? SharedItemsOf(string path)
    {
        string? found = null;
        foreach (string shproj in session.Workspace?.SharedProjects ?? [])
        {
            string items = SharedProjects.ItemsFileOf(shproj);
            if (PathNames.IsUnder(path, Path.GetDirectoryName(items)!) && (found is null || items.Length > found.Length))
            {
                found = items;
            }
        }
        return found is not null && ProjectModel.Owning(Projects, path) is { } owner && PathNames.IsUnder(owner.Folder, Path.GetDirectoryName(found)!)
            ? null : found;
    }

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

    /// <summary>The files under a path now: a file itself, a folder's every file.</summary>
    private static IEnumerable<string> FilesAt(string path) => Directory.Exists(path)
        ? PathNames.FilesUnder(path, _ => true).SelectMany(f => f.Files)
        : File.Exists(path) ? [PathNames.Canonical(path)] : [];
}
