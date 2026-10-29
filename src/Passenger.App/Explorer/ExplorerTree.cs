using Avalonia;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Host;
using Passenger.Text;
using Passenger.Vcs;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>What a row of the Explorer stands for.</summary>
public enum NodeKind
{
    Folder,
    File,
    Solution,
    SolutionFolder,
    Project,
    Dependencies,
    Framework,
    Package,
    ProjectReference,
}

/// <summary>A folder at a project's top that the .NET SDK reads by its name, in the order the Explorer lists them before
/// the project's other folders: none is renamed, deleted or moved, as what reads it would no longer find it.</summary>
public enum SdkFolder
{
    /// <summary>Properties, which holds the project's launch and publish profiles.</summary>
    Properties,

    /// <summary>A web project's wwwroot, its web root, whose files ASP.NET Core serves.</summary>
    WebRoot,
}

/// <summary>The Explorer's tree, in either of its two views: the Solution (its folders with their files, and projects,
/// each project's dependencies and its files, a file's dependents nested under it, <see cref="FileNesting"/>) or the
/// File System. Flattened into the rows currently visible, so a virtualizing list draws only what is on screen; a
/// node's children are made the first time it opens.</summary>
public sealed class ExplorerTree
{
    /// <summary>Never listed: version control's own store, which nobody edits by hand (and the system's own files,
    /// <see cref="PathNames.IsSystemFile"/>). The other folders the walks pass over are listed dimmed
    /// (<see cref="IgnoredFolders"/>).</summary>
    private const string GitStore = ".git";

    private IReadOnlyDictionary<string, ChangeKind> changes = new Dictionary<string, ChangeKind>();

    public TreeRows<ExplorerNode> Rows { get; } = [];

    /// <summary>Colours the files version control says changed.</summary>
    public void SetChanges(IReadOnlyDictionary<string, ChangeKind> changed)
    {
        changes = changed;
        foreach (ExplorerNode row in Rows)
        {
            Mark(row);
        }
    }

    private void Mark(ExplorerNode node)
    {
        node.Change = node.IsFile && node.Path is { } path && changes.TryGetValue(path, out ChangeKind kind) ? kind : null;
        node.IsCut = cut.Count > 0 && node.Kind is NodeKind.File or NodeKind.Folder && node.Path is { } at && IsCut(at);
    }

    /// <summary>Whether a path was cut or is inside a folder that was, found by its own folders: every row is marked, and
    /// compared with each of thousands of files cut, each row would cost them all.</summary>
    private bool IsCut(string path)
    {
        for (string? at = path; at is not null; at = Path.GetDirectoryName(at))
        {
            if (cut.Contains(at))
            {
                return true;
            }
        }
        return false;
    }

    private HashSet<string> cut = new(PathNames.Comparer);

    /// <summary>Dims what was cut and waits to be pasted, and what it holds.</summary>
    public void SetCut(IReadOnlyList<string> paths)
    {
        cut = new HashSet<string>(paths, PathNames.Comparer);
        foreach (ExplorerNode row in Rows)
        {
            Mark(row);
        }
    }

    public ExplorerNode? Root { get; private set; }

    public bool IsSolutionView { get; private set; }

    /// <summary>The solution shown, whose projects say which one a file is revealed in.</summary>
    private SolutionModel? solution;

    /// <summary>The file-based programs beside the solution shown: no solution lists one, so each is a project of the
    /// folder's after the solution's own, as a folder with no solution has its programs beside its projects.</summary>
    private IReadOnlyList<ProjectModel> programs = [];

    /// <summary>A folder's files, the projects among them telling which of its folders the SDK reads (<see cref="SdkFolder"/>).</summary>
    public void LoadFileSystem(string folder, IReadOnlyList<ProjectModel> within)
    {
        IsSolutionView = false;
        solution = null;
        programs = [];
        Take(within);
        Show(Folder(PathNames.Canonical(folder), 0, dimmed: false));
    }

    /// <summary>A solution: its folders, projects and items, then the programs beside it, each by name.</summary>
    public void LoadSolution(SolutionModel solution, IReadOnlyList<ProjectModel> beside)
    {
        IsSolutionView = true;
        this.solution = solution;
        programs = beside;
        Take([.. solution.Projects, .. beside]);
        int count = solution.Projects.Count() + beside.Count;
        ExplorerNode root = new(NodeKind.Solution, solution.Name, solution.Path, 0, false,
            node => [.. Entries(solution.Entries, node.Depth + 1, null),
                .. beside.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => Project(p, node.Depth + 1))],
            Plural.Of(count, "project"));
        Show(root);
    }

    /// <summary>A project opened alone: the project at the top, its dependencies and files under it.</summary>
    public void LoadProject(ProjectModel project)
    {
        IsSolutionView = true;
        solution = null;
        Take([project]);
        Show(Project(project, 0));
    }

    /// <summary>The projects shown, as last evaluated, by path: a project's rows read theirs from here as they are made,
    /// so one evaluated again shows anew in the rows already there (<see cref="UpdateProjects"/>).</summary>
    private readonly Dictionary<string, ProjectModel> projects = new(StringComparer.Ordinal);

    /// <summary>Each evaluated project's files by where they show, made the first time they are wanted.</summary>
    private readonly Dictionary<string, ProjectFiles> files = new(StringComparer.Ordinal);

    private void Take(IEnumerable<ProjectModel> shown)
    {
        projects.Clear();
        files.Clear();
        foreach (ProjectModel project in shown)
        {
            projects[project.Path] = project;
        }
    }

    private ProjectModel Current(ProjectModel project) => projects.GetValueOrDefault(project.Path, project);

    private ProjectFiles? FilesOf(ProjectModel project)
    {
        if (!project.IsEvaluated)
        {
            return null;
        }
        if (!files.TryGetValue(project.Path, out ProjectFiles? held))
        {
            files[project.Path] = held = new ProjectFiles(project);
        }
        return held;
    }

    /// <summary>Projects evaluated again: their rows read them anew, every row still there kept as it was (open, chosen,
    /// focused), and the rows that came or went put in their places; in the File System view, the folders of those whose
    /// web root came or went.</summary>
    public void UpdateProjects(IEnumerable<ProjectModel> updated)
    {
        if (Root is null)
        {
            return;
        }
        if (!IsSolutionView)
        {
            List<string> served = [];
            foreach (ProjectModel project in updated)
            {
                if (projects.TryGetValue(project.Path, out ProjectModel? before))
                {
                    projects[project.Path] = project;
                    if (before.IsWeb != project.IsWeb)
                    {
                        served.Add(project.Folder);
                    }
                }
            }
            RefreshFolders(served);
            return;
        }
        HashSet<string> changed = new(StringComparer.Ordinal);
        foreach (ProjectModel project in updated)
        {
            if (projects.ContainsKey(project.Path))
            {
                projects[project.Path] = project;
                files.Remove(project.Path);
                changed.Add(project.Path);
            }
        }
        if (solution is not null)
        {
            solution = solution.WithProjects(Current);
            programs = [.. programs.Select(Current)];
        }
        List<ExplorerNode> reloaded = [];
        foreach (ExplorerNode node in Loaded(Root, n => n.Kind == NodeKind.Project && n.Path is { } path && changed.Contains(path)))
        {
            ProjectModel project = projects[node.Path!];
            node.Detail = Unloaded.Contains(project.Path) ? "(unloaded)" : DetailOf(project);
            node.Tip = Unloaded.Contains(project.Path) ? null : TipOf(project);
            foreach (ExplorerNode below in Loaded(node, _ => true))
            {
                if (below.Reload())
                {
                    reloaded.Add(below);
                }
            }
        }
        ShowAgain(reloaded);
    }

    public void Toggle(ExplorerNode node)
    {
        if (node.IsExpanded)
        {
            Collapse(node);
        }
        else
        {
            Expand(node);
        }
    }

    public void Expand(ExplorerNode node)
    {
        if (!node.HasChildren || node.IsExpanded)
        {
            return;
        }
        node.IsExpanded = true;
        List<ExplorerNode> shown = [.. Visible(node)];
        foreach (ExplorerNode child in shown)
        {
            Mark(child);
        }
        Rows.Open(node, shown);
    }

    public void Collapse(ExplorerNode node)
    {
        if (!node.IsExpanded)
        {
            return;
        }
        node.IsExpanded = false;
        Rows.Close(node);
    }

    /// <summary>Closes every row but the top one, the rows out of sight under a closed one too, so none opens again with
    /// its own rows open.</summary>
    public void CollapseAll()
    {
        if (Root is null)
        {
            return;
        }
        foreach (ExplorerNode row in Rows.Reverse().Where(r => r != Root && r.IsExpanded).ToList())
        {
            Collapse(row);
        }
        foreach (ExplorerNode hidden in Loaded(Root, n => n != Root && n.IsExpanded))
        {
            hidden.IsExpanded = false;
        }
    }

    /// <summary>Reads again only the folders (and projects) that hold these, keeping the rows still there as they were (open,
    /// chosen), and puts the rows that came or went in their places.</summary>
    public void RefreshFolders(IEnumerable<string> folders)
    {
        if (Root is null)
        {
            return;
        }
        HashSet<string> changed = new(folders, StringComparer.Ordinal);
        List<ExplorerNode> reloaded = [];
        foreach (ExplorerNode node in Loaded(Root, n => n.Kind switch
        {
            NodeKind.Folder => n.Path is { } folder && changed.Contains(folder),
            NodeKind.Project => n.Path is { } project && Path.GetDirectoryName(project) is { } holds && changed.Contains(holds),
            _ => false,
        }))
        {
            if (node.Reload())
            {
                reloaded.Add(node);
            }
        }
        ShowAgain(reloaded);
    }

    /// <summary>Projects unloaded or loaded again: each one's row made anew in its place, every other row kept as it was;
    /// false where one is the root, the project opened alone, which only showing the tree again remakes.</summary>
    public bool ShowProjectsAgain(IReadOnlyCollection<string> paths)
    {
        if (Root is null)
        {
            return true;
        }
        if (Root is { Kind: NodeKind.Project, Path: { } top } && paths.Contains(top))
        {
            return false;
        }
        bool Picked(ExplorerNode node) => node is { Kind: NodeKind.Project, Path: { } project } && paths.Contains(project);
        List<ExplorerNode> reloaded = [];
        foreach (ExplorerNode node in Loaded(Root, n => n.LoadedChildren?.Any(Picked) == true))
        {
            if (node.Reload(Picked))
            {
                reloaded.Add(node);
            }
        }
        ShowAgain(reloaded);
        return true;
    }

    /// <summary>The rows under each node read again made anew from it as it now is, where it is shown open: those still
    /// there kept, the rest taken out or put in, and every other row left as it was.</summary>
    private void ShowAgain(List<ExplorerNode> reloaded)
    {
        foreach (ExplorerNode node in reloaded.Where(n => n.IsExpanded))
        {
            List<ExplorerNode> wanted = [.. Visible(node)];
            foreach (ExplorerNode row in wanted)
            {
                Mark(row);
            }
            Rows.ShowUnder(node, wanted);
        }
    }

    /// <summary>Those of the node and the nodes under it whose children have been read that are picked, all found before
    /// any is read again, by a walk that makes nothing for the nodes it passes.</summary>
    private static List<ExplorerNode> Loaded(ExplorerNode node, Func<ExplorerNode, bool> picked)
    {
        List<ExplorerNode> found = [];
        Walk(node);
        return found;

        void Walk(ExplorerNode at)
        {
            if (picked(at))
            {
                found.Add(at);
            }
            if (at.LoadedChildren is { } children)
            {
                foreach (ExplorerNode child in children)
                {
                    Walk(child);
                }
            }
        }
    }

    /// <summary>Opens every node down to <paramref name="path"/> and returns its row, so a file can be shown where it
    /// lives: in its folder, or in the solution, in the project it belongs to (<see cref="ProjectModel.Owning"/>), the
    /// nearest of those whose folders hold it rather than the first listed.</summary>
    public ExplorerNode? Reveal(string path)
    {
        if (Root is null)
        {
            return null;
        }
        string full = PathNames.Canonical(path);
        string? owner = solution is null ? null : ProjectModel.Owning([.. solution.Projects, .. programs], full)?.Path;
        // A file a project takes from elsewhere shows where its Link puts it, in the project that holds it.
        string? linked = null;
        if (owner is null && solution is not null)
        {
            foreach (ProjectModel project in solution.Projects.Concat(programs))
            {
                if (FilesOf(Current(project))?.LogicalOf(full) is { } logical)
                {
                    (owner, linked) = (project.Path, logical);
                    break;
                }
            }
        }
        ExplorerNode node = Root;
        while (node.Path != full)
        {
            Expand(node);
            if (linked is not null && node.Kind == NodeKind.Project && node.Path == owner)
            {
                return RevealLinked(node, linked, full);
            }
            ExplorerNode? next = node.Children?
                .Where(c => c.Kind is not (NodeKind.Dependencies or NodeKind.Framework or NodeKind.Package or NodeKind.ProjectReference))
                .FirstOrDefault(c => c.Path == full || c.Kind switch
                {
                    NodeKind.Project => owner is not null && c.Path == owner,
                    // A file no project holds may be one a solution folder does.
                    NodeKind.SolutionFolder => c.Contains(owner ?? full),
                    _ => c.Contains(full),
                });
            if (next is null)
            {
                return null;
            }
            node = next;
        }
        return node;
    }

    /// <summary>Opens a project's folders down to a file it takes from elsewhere, by where it shows, and returns its row.</summary>
    private ExplorerNode? RevealLinked(ExplorerNode project, string logical, string full)
    {
        ExplorerNode node = project;
        string[] folders = logical.Split('/');
        foreach (string folder in folders[..^1])
        {
            Expand(node);
            if (node.Children?.FirstOrDefault(c => c.Kind == NodeKind.Folder && c.Name == folder) is not { } next)
            {
                return null;
            }
            node = next;
        }
        Expand(node);
        return node.Children?.FirstOrDefault(c => c.Path == full);
    }

    private void Show(ExplorerNode root)
    {
        Rows.Clear();
        Root = root;
        Rows.Add(root);
        Expand(root);
    }

    /// <summary>Nothing shown, as before a folder opens and once its solution is closed.</summary>
    public void Clear()
    {
        Rows.Clear();
        Root = null;
        solution = null;
        programs = [];
        IsSolutionView = false;
    }

    /// <summary>The projects unloaded, by path: shown dimmed, with nothing under them. The open folder's own set, which
    /// its session keeps.</summary>
    public ISet<string> Unloaded { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>A level of the solution, whatever order the file declares: its folders, then its projects, then the
    /// files a folder holds, each by name.</summary>
    private List<ExplorerNode> Entries(IEnumerable<SolutionEntry> entries, int depth, string? folder) =>
        [.. entries
            .OrderBy(e => e switch { SolutionFolder => 0, ProjectModel => 1, _ => 2 })
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => Entry(e, depth, folder))];

    private ExplorerNode Entry(SolutionEntry entry, int depth, string? parent) => entry switch
    {
        SolutionFolder folder => new ExplorerNode(NodeKind.SolutionFolder, folder.Name, null, depth, false,
            node => Entries(folder.Children, node.Depth + 1, folder.Path)) { SolutionPath = folder.Path },
        ProjectModel project => Project(project, depth),
        SolutionItem item => new ExplorerNode(NodeKind.File, item.Name, item.Path, depth, !File.Exists(item.Path), null) { SolutionPath = parent },
        _ => throw new InvalidOperationException($"Unknown solution entry {entry}"),
    };

    /// <summary>The projects a row stands for: a project, or the loaded ones anywhere under a solution folder.</summary>
    public IEnumerable<string> ProjectsOf(ExplorerNode node) => node.Kind switch
    {
        NodeKind.Project when node.Path is { } project => [project],
        NodeKind.SolutionFolder => (node.Children ?? []).SelectMany(ProjectsOf).Where(p => !Unloaded.Contains(p)),
        _ => [],
    };

    /// <summary>Opens the solution folders down to one (by its path in the solution) and returns its row.</summary>
    public ExplorerNode? RevealSolutionFolder(string path)
    {
        ExplorerNode? node = Root;
        while (node is not null && node.SolutionPath != path)
        {
            Expand(node);
            node = node.Children?.FirstOrDefault(c => c.Kind == NodeKind.SolutionFolder && path.StartsWith(c.SolutionPath!, StringComparison.Ordinal));
        }
        return node;
    }

    /// <summary>"Show files with the same names as nested": a project's files nest by name as well as by what the
    /// project says they depend on.</summary>
    public bool NestFiles { get; set; } = true;

    /// <summary>How a project's files nest under one another: by its DependentUpon, and by name when asked; and once it
    /// is evaluated, the files it holds.</summary>
    private sealed record Nesting(ProjectModel Project, bool ByName, ProjectFiles? Files = null);

    /// <summary>A project's row: once MSBuild has evaluated it, its files are the ones it holds as evaluated
    /// (<see cref="ProjectFiles"/>); until then, or when it could not be, the files in its folder.</summary>
    private ExplorerNode Project(ProjectModel given, int depth) => Unloaded.Contains(given.Path)
        ? new(NodeKind.Project, given.Name, given.Path, depth, dimmed: true, null, "(unloaded)")
        : new(NodeKind.Project, given.Name, given.Path, depth, !given.Exists, node =>
        {
            List<ExplorerNode> children = [Dependencies(given, node.Depth + 1)];
            children.AddRange(HeldIn(given, string.Empty, node.Depth + 1));
            return children;
        }, DetailOf(Current(given))) { Tip = TipOf(Current(given)) };

    /// <summary>What a project's row says beside its name: its frameworks, and off Windows those that run only on Windows
    /// (a WPF or Windows Forms app's); or that MSBuild could not evaluate it.</summary>
    private static string? DetailOf(ProjectModel project)
    {
        if (project.Problem is not null)
        {
            return "(load failed)";
        }
        if (project.TargetFrameworks.Count == 0)
        {
            return null;
        }
        string frameworks = string.Join(", ", project.TargetFrameworks);
        IReadOnlyList<(string Framework, string Desktop)> windowsOnly = project.WindowsOnly;
        // The mark first, so a narrow Solution view still shows it.
        return windowsOnly.Count == 0 ? frameworks
            : windowsOnly.Count >= project.TargetFrameworks.Count ? $"Windows only · {frameworks}"
            : $"{frameworks} · {string.Join(", ", windowsOnly.Select(w => w.Framework))} Windows only";
    }

    /// <summary>What hovering a Windows-only project's row says: why, and what Passenger still does with it here.</summary>
    private static string? TipOf(ProjectModel project) =>
        project.WindowsOnly is [(string framework, _), ..] && project.RunsOnlyOnWindows(framework) is { } why
            ? $"{why}: Passenger builds {project.Name} here for its code help (EnableWindowsTargeting), and Run, Debug and Profile need Windows"
            : null;

    /// <summary>A project's dependencies as MSBuild evaluates them: for a project of one framework, it with the
    /// projects and packages it references; for a multi-targeted one, each framework with its own.</summary>
    private ExplorerNode Dependencies(ProjectModel given, int depth) =>
        new(NodeKind.Dependencies, "Dependencies", null, depth, false, node =>
        {
            ProjectModel project = Current(given);
            int below = node.Depth + 1;
            List<ExplorerNode> children = [];
            IReadOnlyList<FrameworkDependencies> frameworks = project.Evaluated?.Dependencies ?? [];
            if (frameworks is [{ Framework: not null }, ..])
            {
                children.AddRange(frameworks.Select(f => new ExplorerNode(NodeKind.Framework, f.Framework!, null, below, false, framework =>
                {
                    // Read when opened, so one evaluated again shows its packages as they are now.
                    ProjectModel now = Current(given);
                    FrameworkDependencies? its = now.Evaluated?.Dependencies.FirstOrDefault(d => d.Framework == framework.Name);
                    return [.. Referenced(now, (its?.Projects ?? []).Select(PathNames.Canonical), its?.Packages ?? [], framework.Depth + 1)];
                })
                {
                    Owner = given.Path,
                }));
                return children;
            }
            children.AddRange(project.TargetFrameworks.Select(f => Leaf(NodeKind.Framework, f, null, below, project)));
            children.AddRange(Referenced(project, project.ProjectReferences, frameworks is [{ } one] ? one.Packages : [], below));
            return children;
        }) { Owner = given.Path };

    /// <summary>The projects and packages a project (or one framework of it) references; packages the SDK adds of
    /// itself are left out.</summary>
    private static IEnumerable<ExplorerNode> Referenced(ProjectModel project, IEnumerable<string> projects, IReadOnlyList<PackageDependency> packages, int depth) =>
        projects.Select(r => Leaf(NodeKind.ProjectReference, Path.GetFileNameWithoutExtension(r), null, depth, project, r))
            .Concat((packages.Count > 0 ? packages.Where(p => !p.Implicit).Select(p => (p.Name, p.Version)) : project.Packages)
                .Select(p => Leaf(NodeKind.Package, p.Name, p.Version, depth, project)));

    private static ExplorerNode Leaf(NodeKind kind, string name, string? detail, int depth, ProjectModel owner, string? referenced = null) =>
        new(kind, name, null, depth, false, null, detail) { Owner = owner.Path, Referenced = referenced };

    private ExplorerNode Folder(string path, int depth, bool dimmed, Nesting? nesting = null) =>
        new(NodeKind.Folder, Path.GetFileName(path) is { Length: > 0 } name ? name : path, path, depth, dimmed,
            node => Contents(path, node.Depth + 1, node.IsDimmed, skip: null, nesting)) { SdkFolder = SdkFolderAt(path) };

    /// <summary>What the SDK makes of a folder on disk, as the shown projects whose folder holds it read it; most folders'
    /// names alone say it is none.</summary>
    private SdkFolder? SdkFolderAt(string path)
    {
        string name = Path.GetFileName(path);
        if (!string.Equals(name, PropertiesFolder, PathNames.Comparison) && !string.Equals(name, WebRootFolder, PathNames.Comparison))
        {
            return null;
        }
        string? holder = Path.GetDirectoryName(path);
        foreach (ProjectModel project in projects.Values)
        {
            if (holder is not null && PathNames.Same(project.Folder, holder) && SdkFolderOf(project, name) is { } read)
            {
                return read;
            }
        }
        return null;
    }

    private const string PropertiesFolder = "Properties";
    private const string WebRootFolder = "wwwroot";

    /// <summary>What the SDK makes of a folder of a project, by where it is in it ('/' between folders): Properties at the
    /// top of a project (a file-based program has its own launch profiles), the wwwroot at a web project's top.</summary>
    private static SdkFolder? SdkFolderOf(ProjectModel project, string relative) =>
        string.Equals(relative, PropertiesFolder, PathNames.Comparison) && !project.IsProgram ? SdkFolder.Properties
        : string.Equals(relative, WebRootFolder, PathNames.Comparison) && project.IsWeb ? SdkFolder.WebRoot
        : null;

    /// <summary>Where a row goes among its folder's: the SDK's folders first, in their order, then the rest as they came.</summary>
    private static int Listed(ExplorerNode node) => node.SdkFolder is { } read ? (int)read : int.MaxValue;

    /// <summary>What a project holds at one place in it (its top, or a folder of it), read as it is now: once evaluated,
    /// the files it holds there; until then, or when it could not be, the files on disk there.</summary>
    private List<ExplorerNode> HeldIn(ProjectModel given, string relative, int depth, bool dimmed = false)
    {
        ProjectModel project = Current(given);
        ProjectFiles? files = FilesOf(project);
        Nesting nesting = new(project, NestFiles, files);
        if (files is null && project.IsProgram)
        {
            // Its folder is not its own: until its evaluation says what it includes, its C# file alone.
            return [new ExplorerNode(NodeKind.File, Path.GetFileName(project.Path), project.Path, depth, dimmed, null)];
        }
        if (files is null)
        {
            string folder = relative.Length == 0 ? project.Folder : Path.Combine(project.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
            return Contents(folder, depth, dimmed, skip: relative.Length == 0 ? project.Path : null, nesting, given);
        }
        return Held(given, files, relative, depth, nesting, dimmed);
    }

    /// <summary>A folder of an evaluated project: what the project holds in it, by where each file shows (a linked file at
    /// its Link), whether or not the folder is on disk; dimmed, with all it holds, where the folder's walks pass over it.</summary>
    private ExplorerNode HeldFolder(ProjectModel given, string path, string relative, int depth, bool dimmed) =>
        new(NodeKind.Folder, Path.GetFileName(relative), path, depth, dimmed || IgnoredFolders.Is(path), node => HeldIn(given, relative, node.Depth + 1, node.IsDimmed))
        {
            SdkFolder = SdkFolderOf(Current(given), relative),
        };

    /// <summary>What an evaluated project holds at one place in it: its folders (the SDK's first), then its files, each by
    /// name, a file's dependents nested under it; a folder on disk with nothing in it at all shows too, as one just made does.</summary>
    private List<ExplorerNode> Held(ProjectModel given, ProjectFiles files, string relative, int depth, Nesting nesting, bool dimmed)
    {
        List<ExplorerNode> nodes = [.. files.FoldersIn(relative)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => relative.Length == 0 ? name : $"{relative}/{name}")
            .Select(inner => HeldFolder(given, files.PathOf(inner), inner, depth, dimmed))
            .OrderBy(Listed)];
        List<(string Name, string Path)> held = [.. files.FilesIn(relative)
            .Where(f => !FileText.IsSaving(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
        IReadOnlyDictionary<string, string> parents = FileNesting.Parents([.. held.Select(f => f.Path)], nesting.Project, nesting.ByName);
        ILookup<string, string> nestedUnder = NestedUnder(held.Select(f => f.Path), parents);
        foreach ((string name, string path) in held.Where(f => !parents.ContainsKey(f.Path)))
        {
            List<string> nested = [.. nestedUnder[path]];
            nodes.Add(nested.Count == 0
                ? new ExplorerNode(NodeKind.File, name, path, depth, dimmed, null)
                : new ExplorerNode(NodeKind.File, name, path, depth, dimmed, node =>
                    [.. nested.Select(n => new ExplorerNode(NodeKind.File, Path.GetFileName(n), n, node.Depth + 1, dimmed, null))])
                {
                    Nested = string.Join('/', nested.Select(Path.GetFileName)),
                });
        }
        return nodes;
    }

    /// <summary>A folder's entries, folders first (the SDK's first of all), a save's temporary file left out (nothing reads
    /// the folder again once the save is done); a project hides its own build output and file, and nests its files under
    /// those they belong to.</summary>
    private List<ExplorerNode> Contents(string folder, int depth, bool dimmed, string? skip, Nesting? nesting, ProjectModel? within = null)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(e => e.Name != GitStore && !(e is FileInfo && (FileText.IsSaving(e.Name) || PathNames.IsSystemFile(e.Name))))
                .Where(e => skip is null || (e.FullName != skip && !(e is DirectoryInfo && e.Name is "bin" or "obj")))
                .OrderBy(e => e is FileInfo)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return [];
        }
        IReadOnlyDictionary<string, string> parents = nesting is null
            ? new Dictionary<string, string>()
            : FileNesting.Parents([.. entries.OfType<FileInfo>().Select(e => e.FullName)], nesting.Project, nesting.ByName);
        ILookup<string, string> nestedUnder = NestedUnder(entries.OfType<FileInfo>().Select(e => e.FullName), parents);
        List<ExplorerNode> nodes = [];
        foreach (FileSystemInfo entry in entries)
        {
            if (entry is DirectoryInfo)
            {
                // A project's folder reads what it holds when opened, so it shows the project as evaluated once it is.
                bool passed = IgnoredFolders.Is(entry.FullName);
                nodes.Add(within is not null && !passed
                    ? HeldFolder(within, entry.FullName, Path.GetRelativePath(within.Folder, entry.FullName).Replace('\\', '/'), depth, dimmed)
                    : Folder(entry.FullName, depth, dimmed || passed, nesting));
            }
            else if (!parents.ContainsKey(entry.FullName))
            {
                List<string> nested = [.. nestedUnder[entry.FullName]];
                nodes.Add(nested.Count == 0
                    ? new ExplorerNode(NodeKind.File, entry.Name, entry.FullName, depth, dimmed, null)
                    : new ExplorerNode(NodeKind.File, entry.Name, entry.FullName, depth, dimmed, node =>
                        [.. nested.Select(n => new ExplorerNode(NodeKind.File, Path.GetFileName(n), n, node.Depth + 1, dimmed, null))])
                    {
                        Nested = string.Join('/', nested.Select(Path.GetFileName)),
                    });
            }
        }
        return nodes.Any(n => n.SdkFolder is not null) ? [.. nodes.OrderBy(Listed)] : nodes;
    }

    /// <summary>The files nested under each of a folder's files, in the folder's order, grouped once for the folder
    /// rather than looked for in the whole folder for each file.</summary>
    private static ILookup<string, string> NestedUnder(IEnumerable<string> files, IReadOnlyDictionary<string, string> parents) =>
        files.Where(parents.ContainsKey).ToLookup(file => parents[file], PathNames.Comparer);

    /// <summary>A node's descendants as rows, in order, through every node already open.</summary>
    private static IEnumerable<ExplorerNode> Visible(ExplorerNode node) => TreeRows<ExplorerNode>.Shown(node.Children ?? [], child => child.Children);
}

/// <summary>One row of the Explorer: a folder or file, or a part of the solution.</summary>
public sealed class ExplorerNode : Observable, ITreeRow
{
    private readonly Func<ExplorerNode, List<ExplorerNode>>? load;
    private List<ExplorerNode>? children;
    private bool isExpanded;

    public ExplorerNode(NodeKind kind, string name, string? path, int depth, bool dimmed, Func<ExplorerNode, List<ExplorerNode>>? load, string? detail = null)
    {
        Kind = kind;
        Name = name;
        Path = path;
        Depth = depth;
        IsDimmed = dimmed;
        this.detail = detail;
        this.load = load;
    }

    public NodeKind Kind { get; }
    public string Name { get; }
    public string? Path { get; }
    public int Depth { get; }
    public bool IsDimmed { get; }

    /// <summary>A quieter word beside the name: a package's version, a project's frameworks (which an evaluation changes).</summary>
    public string? Detail
    {
        get => detail;
        set => Set(ref detail, value);
    }

    private string? detail;

    /// <summary>What hovering the row says, when it says anything: why a project runs only on Windows.</summary>
    public string? Tip
    {
        get => tip;
        set => Set(ref tip, value);
    }

    private string? tip;

    /// <summary>Where the row is among the solution's own folders: a solution folder's path in the solution ("/src/tools/"),
    /// or for a file one holds, that folder's; null for every other row.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>A file the solution lists in a solution folder (Solution Items), rather than one of a project or folder.</summary>
    public bool IsSolutionItem => Kind == NodeKind.File && SolutionPath is not null;

    /// <summary>The project a dependency (the Dependencies row and each one under it) belongs to.</summary>
    public string? Owner { get; init; }

    /// <summary>The project a project reference names.</summary>
    public string? Referenced { get; init; }

    /// <summary>The names of the files nested under a file, which make it another row when they change.</summary>
    internal string? Nested { get; init; }

    /// <summary>What the SDK reads a folder at a project's top as, which lists it first and keeps it where it is; null for
    /// every other row.</summary>
    public SdkFolder? SdkFolder { get; init; }

    /// <summary>Why Rename, Delete and Cut leave the row where it is: a folder the SDK reads by its name; null for every
    /// other row.</summary>
    public string? Kept => SdkFolder is { } read ? Refusals.ReadBySdk(read, Name) : null;

    /// <summary>Cut and waiting to be pasted, which dims it.</summary>
    public bool IsCut
    {
        get => isCut;
        set
        {
            if (Set(ref isCut, value))
            {
                Raise(nameof(Opacity));
            }
        }
    }

    private bool isCut;

    /// <summary>The folder on disk what is pasted or dropped onto this row goes into: a folder, a project's folder, or for
    /// a file of either, the folder holding it; null for the solution's own rows and the dependencies.</summary>
    public string? Into => Kind switch
    {
        NodeKind.Folder => Path,
        NodeKind.Project when Path is { } project => System.IO.Path.GetDirectoryName(project),
        NodeKind.File when !IsSolutionItem && Path is { } file => System.IO.Path.GetDirectoryName(file),
        _ => null,
    };

    /// <summary>A file or folder on disk that can be cut, copied or dragged, rather than a part of the solution.</summary>
    public bool IsMovable => Kind is NodeKind.Folder || Kind == NodeKind.File && !IsSolutionItem;

    /// <summary>A file-based program beside the solution, which is no solution's to rename or take out: its C# file is,
    /// as the row under it.</summary>
    public bool IsProgram => Kind == NodeKind.Project && Path is { } program && ProgramFiles.IsProgram(program);

    /// <summary>What Rename renames: what is on disk, but a file only the solution lists and a folder the SDK reads, and a
    /// solution folder.</summary>
    public bool CanRename => Kept is null
        && (Kind is NodeKind.Folder or NodeKind.SolutionFolder || Kind == NodeKind.Project && !IsProgram || Kind == NodeKind.File && !IsSolutionItem);

    /// <summary>What Delete takes away: a file or folder to the Trash (but a folder the SDK reads), or what is the solution's
    /// own out of it.</summary>
    public bool CanRemove => Kept is null && (Kind is NodeKind.File or NodeKind.Folder or NodeKind.SolutionFolder || Kind == NodeKind.Project && !IsProgram
        || Kind == NodeKind.ProjectReference && Referenced is not null);

    /// <summary>What Delete only takes out of the solution or its project, its files staying on disk: a project, a
    /// solution folder, a file it lists, a project reference.</summary>
    public bool LeavesFilesBehind => Kind is NodeKind.Project or NodeKind.SolutionFolder or NodeKind.ProjectReference || IsSolutionItem;

    public bool HasChildren => load is not null;

    bool ITreeRow.Expands => HasChildren;

    public bool IsFile => Kind == NodeKind.File;
    public List<ExplorerNode>? Children => load is null ? null : children ??= load(this);

    /// <summary>The children once read, without reading them.</summary>
    internal List<ExplorerNode>? LoadedChildren => children;

    /// <summary>Reads the children again, keeping each one still there (with what is open under it) but those
    /// <paramref name="remade"/> picks, which are made anew; whether any came or went.</summary>
    internal bool Reload(Func<ExplorerNode, bool>? remade = null)
    {
        if (load is null || children is null)
        {
            return false;
        }
        Dictionary<string, ExplorerNode> before = children.ToDictionary(Identity);
        List<ExplorerNode> fresh = [.. load(this).Select(c => before.TryGetValue(Identity(c), out ExplorerNode? kept) && remade?.Invoke(kept) != true ? kept : c)];
        bool same = fresh.Count == children.Count && fresh.SequenceEqual(children);
        children = fresh;
        return !same;
    }

    /// <summary>What makes a row the same row when its parent is read again: a package's version is part of it, so one
    /// updated shows its new version, and what the SDK reads a folder as, so a web root found shows as one.</summary>
    private static string Identity(ExplorerNode node) => $"{node.Kind}/{node.Path ?? node.Name}/{node.Nested}/{(node.Path is null ? node.Detail : null)}/{node.SdkFolder}";

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                Raise(nameof(Icon));
            }
        }
    }

    /// <summary>How version control says the file changed, or null when it did not.</summary>
    public ChangeKind? Change
    {
        get => change;
        set
        {
            if (Set(ref change, value))
            {
                Raise(nameof(IsVcsModified));
                Raise(nameof(IsVcsAdded));
                Raise(nameof(IsVcsUnversioned));
                Raise(nameof(IsVcsConflicted));
            }
        }
    }

    private ChangeKind? change;

    public bool IsVcsModified => change is ChangeKind.Modified or ChangeKind.Renamed;
    public bool IsVcsAdded => change is ChangeKind.Added;
    public bool IsVcsUnversioned => change is ChangeKind.Unversioned;
    public bool IsVcsConflicted => change is ChangeKind.Conflicted;

    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public double Opacity => IsDimmed || IsCut ? 0.55 : 1;
    public Geometry Icon => FileIcons.For(this);
    public IBrush IconBrush => FileIcons.BrushFor(this);

    /// <summary>Whether a path lies under this node: inside its folder, or its project's folder, or nested under its file.</summary>
    public bool Contains(string path)
    {
        string? folder = Kind switch
        {
            NodeKind.Folder => Path,
            NodeKind.Project => System.IO.Path.GetDirectoryName(Path),
            _ => null,
        };
        if (folder is null)
        {
            return Kind is NodeKind.SolutionFolder or NodeKind.File && (Children?.Any(c => c.Path == path || c.Contains(path)) ?? false);
        }
        return PathNames.IsUnder(path, folder);
    }
}
