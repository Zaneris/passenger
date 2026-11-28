using Passenger.Core;

namespace Passenger.App.Explorer;

/// <summary>The files with errors the Explorer marks, every folder above one and every project that holds one, which it
/// marks too: each file's marking set as what tells its errors changes, the files under each folder and in each project
/// counted, so a change costs its own file's folders and projects, never a walk of the tree. UI thread.</summary>
public sealed class ErrorFiles
{
    private readonly HashSet<string> files = new(PathNames.Comparer);

    /// <summary>How many files with errors lie under each folder above one, at any depth.</summary>
    private readonly Dictionary<string, int> under = new(PathNames.Comparer);

    /// <summary>How many files with errors each project holds, and the projects each such file was counted in.</summary>
    private readonly Dictionary<string, int> inProject = new(PathNames.Comparer);
    private readonly Dictionary<string, IReadOnlyList<string>> counted = new(PathNames.Comparer);

    /// <summary>The projects a file is in, by their paths, as their evaluations list it.</summary>
    private Func<string, IReadOnlyList<string>> projectsOf = _ => [];

    /// <summary>A file came to have errors or no longer has them.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether a file has errors.</summary>
    public bool Holds(string file) => files.Contains(file);

    /// <summary>Whether a folder holds a file with errors, at any depth.</summary>
    public bool Above(string folder) => under.ContainsKey(folder);

    /// <summary>Sets whether each file has errors, <see cref="Changed"/> raised once where any did not have them as set.</summary>
    public void Set(IEnumerable<(string File, bool Errors)> marks)
    {
        bool changed = false;
        foreach ((string file, bool errors) in marks)
        {
            if (errors ? !files.Add(file) : !files.Remove(file))
            {
                continue;
            }
            changed = true;
            for (string? at = Path.GetDirectoryName(file); at is not null; at = Path.GetDirectoryName(at))
            {
                Count(under, at, errors ? 1 : -1);
            }
            Counted(file, errors);
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Follows which projects hold each file (a linked file its project outside its folder, of two projects in one
    /// folder only the one listing it), counted again for every file with errors now and as the evaluations change.</summary>
    public void FollowProjects(Func<string, IReadOnlyList<string>> of)
    {
        projectsOf = of;
        ProjectsChanged();
    }

    /// <summary>The projects' evaluations changed: each file with errors counted again in the projects that hold it now.</summary>
    public void ProjectsChanged()
    {
        inProject.Clear();
        counted.Clear();
        foreach (string file in files)
        {
            Counted(file, errors: true);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A file with errors counted in the projects holding it, or taken out of those it was counted in.</summary>
    private void Counted(string file, bool errors)
    {
        if (errors)
        {
            IReadOnlyList<string> projects = projectsOf(file);
            counted[file] = projects;
            foreach (string project in projects)
            {
                Count(inProject, project, 1);
            }
        }
        else if (counted.Remove(file, out IReadOnlyList<string>? was))
        {
            foreach (string project in was)
            {
                Count(inProject, project, -1);
            }
        }
    }

    private static void Count(Dictionary<string, int> counts, string key, int by)
    {
        int count = counts.GetValueOrDefault(key) + by;
        if (count == 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = count;
        }
    }

    /// <summary>Whether a row stands for what holds errors: a file with them, a folder over one, a project holding one (or
    /// its own file having them), a solution folder with such a project, or the solution while any file has them.</summary>
    public bool Marks(ExplorerNode node) => node.Kind switch
    {
        NodeKind.File => node.Path is { } file && files.Contains(file),
        NodeKind.Folder => node.Path is { } folder && under.ContainsKey(folder),
        NodeKind.Project => node.Path is { } project && ProjectHolds(project),
        NodeKind.SolutionFolder => node.Projects.Any(ProjectHolds),
        NodeKind.Solution => files.Count > 0,
        _ => false,
    };

    private bool ProjectHolds(string project) => files.Contains(project) || inProject.ContainsKey(project);
}
