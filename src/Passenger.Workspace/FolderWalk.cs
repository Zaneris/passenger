using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>The one walk of the open folder's files: Go to File's and Find in Files' index, Local History's look at a
/// folder made or deleted elsewhere, the projects of a folder with no solution, a project's own files and the XAML a
/// class is declared in. One rule for what it passes over: <see cref="IgnoredFolders"/>, and where it looks for what a
/// project builds also hidden folders, which MSBuild's own globs leave out (<c>**/.*/**</c>); the system's own files and a
/// save's temporary file; a link resolved once, never into a folder twice or out of the walk (<see cref="PathNames.FilesUnder"/>).</summary>
public static class FolderWalk
{
    /// <summary>The files under a folder, nearest first, by their canonical paths. <paramref name="built"/> passes over
    /// hidden folders too, as a walk for what projects build or hold; <paramref name="enters"/> may pass over more, given
    /// each folder's canonical path.</summary>
    public static IEnumerable<string> Files(string top, bool built = false, Func<string, bool>? enters = null) =>
        Folders(top, built, enters).SelectMany(f => f.Files);

    /// <summary>A project's own files (those of <paramref name="extension"/> when one is given, as ".cs"): under its folder,
    /// passing over what a project builds or holds passes over, and a project nested in its folder, whose files are its
    /// own; a file-based program's, its C# file and those its evaluation found it includes. Paths are canonical, as the
    /// projects' are.</summary>
    public static IEnumerable<string> ProjectFiles(ProjectModel project, IEnumerable<ProjectModel> projects, string? extension = null)
    {
        if (project.IsProgram)
        {
            return ((string[])[project.Path, .. project.Evaluated?.Items.Select(i => i.Path) ?? []])
                .Distinct(PathNames.Comparer)
                .Where(file => (extension is null || file.EndsWith(extension, PathNames.Comparison)) && File.Exists(file));
        }
        HashSet<string> nested = new(projects.Where(p => PathNames.IsUnder(p.Folder, project.Folder)).Select(p => p.Folder), PathNames.Comparer);
        return Files(project.Folder, built: true, enters: folder => !nested.Contains(folder))
            .Where(file => extension is null || file.EndsWith(extension, PathNames.Comparison));
    }

    /// <summary>The same files folder by folder, for a walk that reads no more than so many folders.</summary>
    public static IEnumerable<(string Folder, IReadOnlyList<string> Files)> Folders(string top, bool built = false, Func<string, bool>? enters = null)
    {
        string root = PathNames.Canonical(top);
        return PathNames.FilesUnder(root, folder => Enters(root, folder, built) && enters?.Invoke(folder) != false, Joined);
    }

    /// <summary>The files of the walk of the open folder <paramref name="root"/> (canonical) under one of its folders
    /// (<paramref name="from"/>, the whole when none), by their paths from the open folder ("src/App/App.cs"), each made
    /// once from the system's entry. A folder under it is entered or passed over as the walk of the whole one does.</summary>
    public static IEnumerable<string> Below(string root, string? from = null) =>
        PathNames.FilesUnder(from ?? root, folder => Enters(root, folder, built: false), (folder, name) => Relative(root, folder, name)).SelectMany(f => f.Files);

    private static string? Joined(string folder, ReadOnlySpan<char> name) => FileText.IsSaving(name) ? null : Path.Join(folder, name);

    private static string? Relative(string root, string folder, ReadOnlySpan<char> name)
    {
        if (FileText.IsSaving(name))
        {
            return null;
        }
        ReadOnlySpan<char> below = PathNames.Below(folder, root);
        return below.IsEmpty ? name.ToString() : string.Concat(below, Separator, name);
    }

    private static readonly string Separator = Path.DirectorySeparatorChar.ToString();

    /// <summary>Whether the walk from <paramref name="root"/> enters a folder under it: none of the names between them is one
    /// passed over, though a link led to it.</summary>
    private static bool Enters(string root, string folder, bool built)
    {
        ReadOnlySpan<char> names = PathNames.Below(folder, root);
        return !IgnoredFolders.AnyIn(root, names) && !(built && IgnoredFolders.AnyHidden(names));
    }
}
