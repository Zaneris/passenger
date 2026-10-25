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
    private readonly Dictionary<string, List<(string Name, string Path)>> files = new(PathNames.Comparer);
    private readonly Dictionary<string, HashSet<string>> folders = new(PathNames.Comparer);

    public ProjectFiles(ProjectModel project)
    {
        root = project.Folder;
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
            if (!folders.TryGetValue(parent, out HashSet<string>? names))
            {
                folders[parent] = names = new(PathNames.Comparer);
            }
            if (!names.Add(relative[(slash + 1)..]))
            {
                return;
            }
            relative = parent;
        }
    }

    /// <summary>Where a file of the project shows in it, the folders down to it separated by '/'; null for a file the
    /// project does not hold.</summary>
    public string? LogicalOf(string path)
    {
        foreach ((string folder, List<(string Name, string Path)> held) in files)
        {
            if (held.FirstOrDefault(f => PathNames.Same(f.Path, path)) is { Name: { } name })
            {
                return folder.Length == 0 ? name : $"{folder}/{name}";
            }
        }
        return null;
    }

    /// <summary>A place in the project as a path on disk, whether or not a folder is there.</summary>
    public string PathOf(string relative) => relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The folders at a place: those the project holds something in, and those on disk with nothing in them.</summary>
    public IEnumerable<string> FoldersIn(string relative)
    {
        HashSet<string> names = folders.TryGetValue(relative, out HashSet<string>? held) ? new(held, PathNames.Comparer) : new(PathNames.Comparer);
        try
        {
            foreach (DirectoryInfo folder in new DirectoryInfo(PathOf(relative)).EnumerateDirectories())
            {
                if (!folder.Name.StartsWith('.') && folder.Name is not ("bin" or "obj") && !names.Contains(folder.Name) && Empty(folder))
                {
                    names.Add(folder.Name);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not on disk (a linked file's place), or gone: only what the project holds is there.
        }
        return names;
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
