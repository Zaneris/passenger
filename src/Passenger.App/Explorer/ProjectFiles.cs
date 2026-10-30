using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>An evaluated project's files by where the Solution view shows them: each file the project holds (its item
/// types that are files, Visible but for false) in its folder under the project's, a file from elsewhere at its Link or
/// else at the project's top, and its Folder items; a file the project leaves out (a Remove, an Exclude) is not there,
/// nor its folder unless something of the project is in it. A folder on disk with nothing in it at all shows too, since
/// MSBuild has nothing to say of it and one just made would otherwise vanish.</summary>
internal sealed class ProjectFiles
{
    private readonly string root;

    /// <summary>Whether it is a file-based program's, whose folder on disk is not its own.</summary>
    private readonly bool program;

    /// <summary>How the places in the project and the names in each compare, as its folder tells case.</summary>
    private readonly StringComparer names;
    private readonly Dictionary<string, List<(string Name, string Path)>> files;
    private readonly Dictionary<string, HashSet<string>> folders;

    /// <summary>Where each file the project holds shows, by its path: the place's folders and its name there.</summary>
    private readonly Dictionary<string, (string Folder, string Name)> places = new(PathNames.Comparer);

    public ProjectFiles(ProjectModel project)
    {
        root = project.Folder;
        program = project.IsProgram;
        names = PathNames.NamesIn(root);
        files = new(names);
        folders = new(names);
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (EvaluatedItem item in project.Evaluated?.Items ?? [])
        {
            string logical = Logical(item);
            if (item.Type == "Folder")
            {
                AddFolder(logical);
                continue;
            }
            int slash = logical.LastIndexOf('/');
            string folder = slash < 0 ? string.Empty : logical[..slash];
            string name = logical[(slash + 1)..];
            if (name.Length == 0 || PathNames.IsSystemFile(name) || !seen.Add(item.Path))
            {
                continue;
            }
            AddFolder(folder);
            if (!files.TryGetValue(folder, out List<(string, string)>? held))
            {
                files[folder] = held = [];
            }
            held.Add((name, item.Path));
            places.TryAdd(item.Path, (folder, name));
        }
    }

    /// <summary>Where an item shows, relative to the project's folder with '/' between folders.</summary>
    private string Logical(EvaluatedItem item)
    {
        if (item.Link is { Length: > 0 } link)
        {
            return link.Replace('\\', '/').Trim('/');
        }
        string full = Path.TrimEndingDirectorySeparator(item.Path);
        return PathNames.IsUnder(full, root) ? Path.GetRelativePath(root, full).Replace('\\', '/') : Path.GetFileName(full);
    }

    private void AddFolder(string relative)
    {
        while (relative.Length > 0)
        {
            int slash = relative.LastIndexOf('/');
            string parent = slash < 0 ? string.Empty : relative[..slash];
            if (!folders.TryGetValue(parent, out HashSet<string>? held))
            {
                folders[parent] = held = new(names);
            }
            if (!held.Add(relative[(slash + 1)..]))
            {
                return;
            }
            relative = parent;
        }
    }

    /// <summary>Where a file of the project shows in it, the folders down to it separated by '/'; null for a file the
    /// project does not hold.</summary>
    public string? LogicalOf(string path) =>
        places.TryGetValue(path, out (string Folder, string Name) place) ? place.Folder.Length == 0 ? place.Name : $"{place.Folder}/{place.Name}" : null;

    /// <summary>The place a file of the project shows at, the folders down to it separated by '/' ("" at the project's top),
    /// as <see cref="FilesIn"/> takes it; null for a file the project does not hold.</summary>
    public string? PlaceOf(string path) => places.TryGetValue(path, out (string Folder, string Name) place) ? place.Folder : null;

    /// <summary>A place in the project as a path on disk, whether or not a folder is there.</summary>
    public string PathOf(string relative) => relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The folders at a place: those the project holds something in, and but for a program's, those on disk with
    /// nothing in them.</summary>
    public IEnumerable<string> FoldersIn(string relative)
    {
        HashSet<string> shown = folders.TryGetValue(relative, out HashSet<string>? held) ? new(held, names) : new(names);
        if (program)
        {
            return shown;
        }
        try
        {
            foreach (DirectoryInfo folder in new DirectoryInfo(PathOf(relative)).EnumerateDirectories())
            {
                if (!folder.Name.StartsWith('.') && folder.Name is not ("bin" or "obj") && !shown.Contains(folder.Name) && Empty(folder))
                {
                    shown.Add(folder.Name);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not on disk (a linked file's place), or gone: only what the project holds is there.
        }
        return shown;
    }

    /// <summary>The files at a place, each with the name it shows by and its path.</summary>
    public IEnumerable<(string Name, string Path)> FilesIn(string relative) => files.TryGetValue(relative, out List<(string, string)>? held) ? held : [];

    private static bool Empty(DirectoryInfo folder)
    {
        try
        {
            return !folder.EnumerateFiles("*", SearchOption.AllDirectories).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
