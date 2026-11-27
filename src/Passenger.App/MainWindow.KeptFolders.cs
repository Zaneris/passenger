using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>A project keeps an empty folder of its own in its file, as Visual Studio does, by a Folder item: New Folder in
/// a project writes one, and the Explorer's changes that put a file or folder into a folder so kept (a new file, class or
/// item, a paste, a move) or that take the folder away (a delete, a move out of its project) take it out, within the
/// command that made the change, so one Undo takes back both; a rename or move within its project takes it along with its
/// folder (<see cref="FollowMoved"/>).</summary>
public partial class MainWindow
{
    /// <summary>The project a folder made at a path is kept by: the one owning it but a file-based program's, the path under
    /// its folder and in none of it never shown for having nothing in it (<see cref="ProjectFiles.LeftOut"/>); null for none.</summary>
    private ProjectModel? KeepingFolder(string path) =>
        ProjectOf(path) is { IsProgram: false } project && PathNames.Relative(path, project.Folder) is { } below && below != "."
            && !below.Split('/').Any(ProjectFiles.LeftOut) ? project : null;

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
            yield return (project.Path, keep, folder => went.Any(w => PathNames.Within(folder, w)) || came.Any(c => PathNames.IsUnder(c, folder))
                || !PathNames.IsUnder(folder, own) && came.Any(c => PathNames.Within(folder, c)));
        }
    }
}
