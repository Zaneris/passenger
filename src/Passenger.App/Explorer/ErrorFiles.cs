using Passenger.Core;

namespace Passenger.App.Explorer;

/// <summary>The files with errors the Explorer marks, and every folder above one, which it marks too: each file's marking
/// set as what tells its errors changes, the files under each folder counted, so a change costs its own file's folders,
/// never a walk of the tree. UI thread.</summary>
public sealed class ErrorFiles
{
    private readonly HashSet<string> files = new(PathNames.Comparer);

    /// <summary>How many files with errors lie under each folder above one, at any depth.</summary>
    private readonly Dictionary<string, int> under = new(PathNames.Comparer);

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
                int count = under.GetValueOrDefault(at) + (errors ? 1 : -1);
                if (count == 0)
                {
                    under.Remove(at);
                }
                else
                {
                    under[at] = count;
                }
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether a row stands for what holds errors: a file with them, a folder or project over one, a solution
    /// folder with such a project, or the solution while any file has them.</summary>
    public bool Marks(ExplorerNode node) => node.Kind switch
    {
        NodeKind.File => node.Path is { } file && files.Contains(file),
        NodeKind.Folder => node.Path is { } folder && under.ContainsKey(folder),
        NodeKind.Project => node.Path is { } project && (files.Contains(project) || Path.GetDirectoryName(project) is { } folder && under.ContainsKey(folder)),
        NodeKind.SolutionFolder => node.Projects.Any(project => files.Contains(project) || Path.GetDirectoryName(project) is { } folder && under.ContainsKey(folder)),
        NodeKind.Solution => files.Count > 0,
        _ => false,
    };
}
