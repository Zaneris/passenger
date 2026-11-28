using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>A project, or a shared project's items file, keeps an empty folder of its own, as Visual Studio does, by a
/// Folder item: New Folder there writes one, and the Explorer's changes that put a file or folder into a folder so kept (a
/// new file, class or item, a paste, a move) or that take the folder away (a delete, a move out of its project) take it out,
/// within the command that made the change, so one Undo takes back both; a rename or move within its project takes it along
/// with its folder (<see cref="FollowMoved"/>). What any other hand puts there or takes away (the terminal, an agent's
/// write, a code action) lets the item go too, in a command of its own (<see cref="FollowFolderItems"/>).</summary>
public partial class MainWindow
{
    /// <summary>The project a folder made at a path is kept by: the one owning it but a file-based program's, the path under
    /// its folder and in none of it never shown for having nothing in it (<see cref="ProjectFiles.LeftOut"/>); null for none.</summary>
    private ProjectModel? KeepingFolder(string path) =>
        ProjectOf(path) is { IsProgram: false } project && Below(path, project.Folder) ? project : null;

    /// <summary>Whether a path lies under a project's folder and in none of it never shown (<see cref="ProjectFiles.LeftOut"/>).</summary>
    private static bool Below(string path, string folder) =>
        PathNames.Relative(path, folder) is { } below && below != "." && !below.Split('/').Any(ProjectFiles.LeftOut);

    /// <summary>Whether a change would change a project's file keeping its folders (<see cref="KeepFolders"/>), as its text
    /// stands now: what makes a change that writes nothing else one command of Undo.</summary>
    private bool KeepsFolders(IReadOnlyCollection<string> made, IReadOnlyCollection<string> came, IReadOnlyCollection<string> went) =>
        Keeping(made, came, went).Any(k => Editors.TextOf(k.Project)?.ToString() is { } text && ProjectEdits.WithFoldersKept(text, k.Project, k.Keep, k.LetGo) is not null);

    /// <summary>The projects' files keeping their folders through a change, as part of the command recording it: each folder
    /// <paramref name="made"/> by New Folder kept, and let go of, each folder something <paramref name="came"/> into (or that
    /// came, moved out of its project) and each that <paramref name="went"/>, or lay in one that went. Null once written, else
    /// why not.</summary>
    private string? KeepFolders(IReadOnlyCollection<string> made, IReadOnlyCollection<string> came, IReadOnlyCollection<string> went, CommandHistory.Recording recording)
    {
        foreach ((string project, IReadOnlyCollection<string> keep, Func<string, bool> letGo) in Keeping(made, came, went))
        {
            try
            {
                if (!File.Exists(project) || Editors.TextOf(project)?.ToString() is not { } text || ProjectEdits.WithFoldersKept(text, project, keep, letGo) is null)
                {
                    continue;
                }
                recording.Watch(project);
                if (SaveBeforeWriting([project]) is { } unsaved)
                {
                    return $"{Path.GetFileName(project)} could not follow its folders: {unsaved}";
                }
                if (ProjectEdits.KeepFolders(project, keep, letGo))
                {
                    // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                    FollowWritten([project]);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"{Path.GetFileName(project)} could not follow its folders: {error.Message}";
            }
        }
        return null;
    }

    /// <summary>Each project a change reaches (one whose folder holds a path of it), with the folders made it keeps and which
    /// of its kept folders the change lets go of.</summary>
    private IEnumerable<(string Project, IReadOnlyCollection<string> Keep, Func<string, bool> LetGo)> Keeping(IReadOnlyCollection<string> made,
        IReadOnlyCollection<string> came, IReadOnlyCollection<string> went)
    {
        List<string> reached = [.. made, .. came, .. went];
        foreach (ProjectModel project in session.Workspace?.Projects ?? [])
        {
            if (project.IsProgram || !reached.Any(p => PathNames.IsUnder(p, project.Folder)))
            {
                continue;
            }
            string own = project.Folder;
            List<string> keep = [.. made.Where(m => KeepingFolder(m)?.Path == project.Path)];
            yield return (project.Path, keep, LetsGo(own));
        }
        foreach (string items in reached.Select(SharedItemsOf).OfType<string>().Distinct(PathNames.Comparer))
        {
            string own = Path.GetDirectoryName(items)!;
            List<string> keep = [.. made.Where(m => SharedItemsOf(m) is { } of && PathNames.Same(of, items) && Below(m, own))];
            yield return (items, keep, LetsGo(own));
        }

        Func<string, bool> LetsGo(string own) => folder => went.Any(w => PathNames.Within(folder, w)) || came.Any(c => PathNames.IsUnder(c, folder))
            || !PathNames.IsUnder(folder, own) && came.Any(c => PathNames.Within(folder, c));
    }

    /// <summary>Files and folders come or gone by any hand, not yet followed: those a command being made brought wait for its end.</summary>
    private readonly HashSet<string> cameUnfollowed = new(PathNames.Comparer);

    /// <summary>Whether the end of each command follows what came meanwhile.</summary>
    private bool followsCommands;

    /// <summary>Files and folders come or gone by any hand, followed once no command is being made, taken back or made again,
    /// as they stand then: a kept folder a file came into, or that went, lets its item go in a command of its own, where the
    /// Explorer's own change has not taken it out in its command already.</summary>
    private void FollowFolderItems(ProjectSession following, IReadOnlyCollection<string> paths)
    {
        if (!followsCommands)
        {
            followsCommands = true;
            Editors.Commands.Ended += (_, _) => UiThread.Post(() => FollowCame(session), Avalonia.Threading.DispatcherPriority.Background);
        }
        // A build's output, a repository's own files and the like are in no folder a project keeps.
        cameUnfollowed.UnionWith(paths.Where(p => following.Folder is { } root && Below(p, root)));
        UiThread.Post(() => FollowCame(following), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Whether a folder holds a file, at any depth, the system's own aside.</summary>
    private static bool HoldsFile(string folder) => PathNames.FilesUnder(folder, _ => true).Any(f => f.Files.Count > 0);

    private void FollowCame(ProjectSession following)
    {
        if (session != following || following.IsEnded)
        {
            cameUnfollowed.Clear();
            return;
        }
        if (cameUnfollowed.Count == 0 || Editors.Commands.IsMaking)
        {
            return;
        }
        string[] paths = [.. cameUnfollowed];
        cameUnfollowed.Clear();
        // A folder come empty puts nothing of the project's into a kept folder, as only its files do.
        List<string> came = [.. paths.Where(p => File.Exists(p) || (Directory.Exists(p) && HoldsFile(p)))];
        List<string> went = [.. paths.Where(p => !File.Exists(p) && !Directory.Exists(p))];
        if (!KeepsFolders([], came, went))
        {
            return;
        }
        using CommandHistory.Recording recording = Editors.Commands.Record("Folder Items Followed");
        if (KeepFolders([], came, went, recording) is { } unkept)
        {
            status.Text = unkept;
        }
    }
}
