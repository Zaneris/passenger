using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>A project's references to the solution's other projects: Add, Reference... (and Add Reference... on its
/// Dependencies) checks and unchecks them in <see cref="AddReferenceDialog"/>, and Remove on one under Dependencies
/// takes it out. Each changes the project file's text through <see cref="ProjectEdits"/>, and the solution is read
/// again, so the Explorer's Dependencies, Build and the language server (which reloads a project file that changes)
/// follow.</summary>
public partial class MainWindow
{
    /// <summary>The dialog over the solution's other projects, those the project references checked; OK makes the project
    /// reference just those. Null, saying why, when there is no other project.</summary>
    public AddReferenceDialog? ShowAddReference(string project)
    {
        string name = Path.GetFileNameWithoutExtension(project);
        IReadOnlyList<ProjectModel> all = session.Workspace?.Projects ?? [];
        List<ProjectModel> others = [.. all.Where(p => !PathNames.Same(p.Path, project)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        if (others.Count == 0)
        {
            status.Text = $"The solution has no other project for {name} to reference";
            return null;
        }
        HashSet<string> referenced = new(all.FirstOrDefault(p => PathNames.Same(p.Path, project))?.ProjectReferences ?? [], StringComparer.Ordinal);
        HashSet<string> dependents = Dependents(all, project);
        AddReferenceDialog dialog = new(name, [.. others.Select(p => new ReferenceChoice(p.Path, p.Name, InFolder(p.Path), referenced.Contains(p.Path), dependents.Contains(p.Path)))]);
        IdeErrors.Observe(ApplyAsync());
        return dialog;

        async Task ApplyAsync()
        {
            if (await dialog.ShowDialog<IReadOnlyList<string>?>(this) is { } chosen)
            {
                SetReferences(project, chosen);
            }
        }
    }

    /// <summary>Makes a project reference the solution's projects given and no others of them: those not referenced yet are
    /// added, those no longer given taken out. Null once done, else why not, which the status bar says too.</summary>
    public string? SetReferences(string project, IReadOnlyCollection<string> wanted)
    {
        string name = Path.GetFileNameWithoutExtension(project);
        IReadOnlyList<string> had = session.Workspace?.Projects.FirstOrDefault(p => PathNames.Same(p.Path, project))?.ProjectReferences ?? [];
        List<string> adding = [.. wanted.Where(w => !had.Any(h => PathNames.Same(h, w)))];
        // Only the solution's own projects are the dialog's to take out; a reference to one outside it stays.
        List<string> removing = [.. had.Where(h => !wanted.Any(w => PathNames.Same(h, w)) && (session.Workspace?.Projects.Any(p => PathNames.Same(p.Path, h)) ?? false))];
        if (adding.Count == 0 && removing.Count == 0)
        {
            return null;
        }
        if (SaveBeforeWriting([project]) is { } unsaved)
        {
            status.Text = $"{name} could not be changed: {unsaved}";
            return unsaved;
        }
        List<string> said = [];
        try
        {
            foreach (string added in adding)
            {
                said.Add(ProjectEdits.AddReference(project, added) ?? $"Added a reference to {Path.GetFileNameWithoutExtension(added)} to {name}");
            }
            foreach (string removed in removing)
            {
                said.Add(ProjectEdits.RemoveReference(project, removed) ?? $"Removed the reference to {Path.GetFileNameWithoutExtension(removed)} from {name}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            said.Add($"{name} could not be changed: {error.Message}");
            FollowWritten([project]);
            ReloadSolution();
            status.Text = string.Join("; ", said);
            return error.Message;
        }
        FollowWritten([project]);
        ReloadSolution();
        status.Text = string.Join("; ", said);
        return null;
    }

    /// <summary>Remove on a project reference under Dependencies, once asked: the project file loses it.</summary>
    public async Task<bool> RemoveReferenceAsync(string project, string referenced)
    {
        string name = Path.GetFileNameWithoutExtension(project);
        string other = Path.GetFileNameWithoutExtension(referenced);
        if (!await Confirm("Remove Reference", $"Remove the reference to {other} from {name}?", "Remove"))
        {
            return false;
        }
        string? problem = SaveBeforeWriting([project]);
        try
        {
            problem ??= ProjectEdits.RemoveReference(project, referenced);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        FollowWritten([project]);
        if (problem is null)
        {
            ReloadSolution();
        }
        status.Text = problem is null ? $"Removed the reference to {other} from {name}" : $"{name} could not be changed: {problem}";
        return problem is null;
    }

    /// <summary>The projects that reference one, directly or through others.</summary>
    private static HashSet<string> Dependents(IReadOnlyList<ProjectModel> projects, string project)
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        Queue<string> next = new([project]);
        while (next.TryDequeue(out string? at))
        {
            foreach (ProjectModel referrer in projects.Where(p => p.ProjectReferences.Any(r => PathNames.Same(r, at))))
            {
                if (found.Add(referrer.Path))
                {
                    next.Enqueue(referrer.Path);
                }
            }
        }
        return found;
    }
}
