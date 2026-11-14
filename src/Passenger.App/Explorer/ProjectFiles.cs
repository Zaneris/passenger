using Passenger.Core;
using Passenger.Host;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>An evaluated project's files by where the Solution view shows them: each file the project holds (its item
/// types that are files, Visible but for false) in its folder under the project's, a file from elsewhere at its Link or
/// else at the project's top, and its Folder items; a file the project leaves out (a Remove, an Exclude) is not there,
/// nor its folder unless something of the project is in it. A folder on disk with nothing in it at all shows too, since
/// MSBuild has nothing to say of it and one just made would otherwise vanish; once shown, it shows until the project is
/// evaluated again, a file come into it since being that evaluation's to judge, so its row stays the row it was. A
/// Docker Compose project's are the items its text names (<see cref="Containers.ContainerConfigurations.ItemsOf"/>), a
/// shared project's its items file's, with what its folders hold that the file leaves out (<see cref="UnlistedIn"/>), and the
/// files a shared project brings into a project are that shared project's.</summary>
internal sealed class ProjectFiles
{
    private readonly string root;

    /// <summary>Whether its folder on disk is not its own (a file-based program's, a Docker Compose project's), so it holds
    /// only what it names.</summary>
    private readonly bool program;

    /// <summary>How the places in the project and the names in each compare, as its folder tells case.</summary>
    private readonly StringComparer names;
    private readonly Dictionary<string, List<(string Name, string Path)>> files;
    private readonly Dictionary<string, HashSet<string>> folders;

    /// <summary>The places of the folders shown with nothing in them, which <see cref="FoldersIn"/> shows while they are
    /// on disk.</summary>
    private readonly HashSet<string> shownEmpty;

    /// <summary>Where each file the project holds shows, by its path: the place's folders and its name there.</summary>
    private readonly Dictionary<string, (string Folder, string Name)> places = new(PathNames.Comparer);

    /// <summary>A shared project's items file, whose folders show every folder and file on disk, those it leaves out
    /// apart (<see cref="UnlistedIn"/>); null for any other project.</summary>
    public string? Items { get; }

    /// <summary>The files it holds that say they depend on another (DependentUpon, relative to the file's own folder), each
    /// with that file's path.</summary>
    public Dictionary<string, string> DependentUpon { get; } = new(PathNames.Comparer);

    public ProjectFiles(ProjectModel project)
        : this(project.Folder, project.Evaluated?.Items.Where(i => i.Shared is null) ?? [], project.IsProgram)
    {
    }

    /// <summary>The files of the items a project in a folder holds; <paramref name="onlyItems"/> where its folder on disk is
    /// not its own.</summary>
    public ProjectFiles(string projectFolder, IEnumerable<EvaluatedItem> items, bool onlyItems, string? shared = null)
    {
        root = projectFolder;
        Items = shared;
        program = onlyItems;
        names = PathNames.NamesIn(root);
        files = new(names);
        folders = new(names);
        shownEmpty = new(names);
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (EvaluatedItem item in items)
        {
            if (item.DependentUpon is { } parent)
            {
                DependentUpon.TryAdd(item.Path, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(item.Path)!, parent.Replace('\\', '/'))));
            }
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
    /// nothing in them, or that had nothing in them when shown before; a shared project's, every one on disk.</summary>
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
                if (folder.Name.StartsWith('.') || folder.Name is "bin" or "obj" || shown.Contains(folder.Name))
                {
                    continue;
                }
                string place = relative.Length == 0 ? folder.Name : $"{relative}/{folder.Name}";
                if (Items is not null || shownEmpty.Contains(place) || Empty(folder))
                {
                    shown.Add(folder.Name);
                    shownEmpty.Add(place);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not on disk (a linked file's place), or gone: only what the project holds is there.
        }
        return shown;
    }

    /// <summary>For a shared project, the files on disk at a place that its items file leaves out, each by its name and path,
    /// its items file and .shproj aside; none for any other project.</summary>
    public IEnumerable<(string Name, string Path)> UnlistedIn(string relative)
    {
        if (Items is null)
        {
            return [];
        }
        try
        {
            return [.. new DirectoryInfo(PathOf(relative)).EnumerateFiles()
                .Where(f => !PathNames.IsSystemFile(f.Name) && !FileText.IsSaving(f.Name) && !SharedProjects.Is(f.FullName) && !SharedProjects.IsItemsFile(f.FullName))
                .Select(f => (f.Name, PathNames.Canonical(f.FullName)))
                .Where(f => !places.ContainsKey(f.Item2))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
