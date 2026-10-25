namespace Passenger.Workspace;

/// <summary>The open folder's solution and projects, decided here alone for the Explorer, the language server, Build,
/// Run, NuGet and the test runner: the solution or project opened by name while it is there, else the solution at the
/// folder's top; with none, the projects anywhere under the folder but its build output, dependencies and hidden folders.
/// Read when the folder opens and again when the folder watcher sees a solution or project change, never on each use.</summary>
public sealed record WorkspaceModel(string Folder, SolutionModel? Solution, IReadOnlyList<ProjectModel> Projects)
{
    /// <summary>The most projects taken from a folder with no solution, so a monorepo does not load hundreds at once.</summary>
    private const int ProjectLimit = 200;

    /// <summary>The most folders searched for them, so a folder that is really a disk is not walked for minutes.</summary>
    private const int FolderLimit = 5_000;

    /// <summary>The C# project the workspace was opened as: its one project, with no solution, whatever else its folder
    /// holds. Null for a folder or a solution.</summary>
    public string? ProjectFile { get; init; }

    /// <summary>What it was opened as, which Recent Solutions keeps and reopens: the solution, the project, else the
    /// folder.</summary>
    public string OpenedAs => Solution?.Path ?? ProjectFile ?? Folder;

    /// <summary>Its name as the toolbar, the window's title and Recent Solutions show it: the solution's, the project's,
    /// else the folder's.</summary>
    public string Name => Solution?.Name ?? (ProjectFile is { } project ? Path.GetFileNameWithoutExtension(project) : Path.GetFileName(Folder));

    /// <summary>Whether a file opens as the workspace rather than in an editor: a solution (.sln, .slnx) or a C#
    /// project (.csproj).</summary>
    public static bool Opens(string path) => IsSolution(path) || IsProject(path);

    public static bool IsSolution(string path) => Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx";

    public static bool IsProject(string path) => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a solution's project is a Docker Compose project (.dcproj).</summary>
    public static bool IsComposeProject(string path) => Path.GetExtension(path).Equals(".dcproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a folder, with the solution or project it was opened by, which stays its workspace while it is there.
    /// A folder without either is walked for its projects, which is a worker's job, never the window's thread's.</summary>
    public static WorkspaceModel Read(string folder, string? named = null) =>
        ReadSolution(folder, named) ?? new WorkspaceModel(folder, null, [.. ProjectsUnder(folder).Select(ProjectModel.Unevaluated)]);

    /// <summary>A folder read through the solution or project it was opened by, else its own solution: a listing of the
    /// folder's top and the files the solution names, quick enough for the window's thread. Null for a folder without one
    /// (or one that does not read), whose projects only a walk of its folders finds.</summary>
    public static WorkspaceModel? ReadSolution(string folder, string? named = null)
    {
        if (named is not null && IsProject(named) && File.Exists(named))
        {
            return new WorkspaceModel(folder, null, [ProjectModel.Unevaluated(named)]) { ProjectFile = named };
        }
        string? path = named is not null && IsSolution(named) && File.Exists(named) ? named : SolutionModel.Find(folder);
        // A Docker Compose project (.dcproj) is read as XML for its run configurations, never loaded as a project.
        return path is not null && SolutionModel.Load(path) is { } solution
            ? new WorkspaceModel(folder, solution, [.. solution.Projects.Where(p => !IsComposeProject(p.Path))])
            : null;
    }

    /// <summary>The solution's Docker Compose projects (.dcproj), each a compose configuration of its own.</summary>
    public IReadOnlyList<string> ComposeProjects => Solution is null ? []
        : [.. Solution.Projects.Where(p => p.Exists && IsComposeProject(p.Path)).Select(p => p.Path)];

    /// <summary>The same workspace with each project as <paramref name="map"/> gives it: MSBuild's evaluation of it, in
    /// the solution's folders and in <see cref="Projects"/> alike.</summary>
    public WorkspaceModel WithProjects(Func<ProjectModel, ProjectModel> map) =>
        this with { Solution = Solution?.WithProjects(map), Projects = [.. Projects.Select(map)] };

    /// <summary>A folder without a solution read again through the projects found before: those still there, as quick as
    /// a solution's read. Only the walk finds those added since.</summary>
    public WorkspaceModel Reread() =>
        new(Folder, null, [.. Projects.Where(p => File.Exists(p.Path)).Select(p => ProjectModel.Unevaluated(p.Path))]);

    /// <summary>What building the whole builds: the solution, or each project loaded in turn when some are unloaded or
    /// there is no solution; the folder when it has neither, for the .NET command line to say why.</summary>
    public IReadOnlyList<string> BuildTargets(IReadOnlyList<ProjectModel> loaded) =>
        Solution is not null && loaded.Count == Projects.Count && ComposeProjects.Count == 0 ? [Solution.Path]
        : Solution is null && Projects.Count == 0 ? [Folder]
        : [.. loaded.Select(p => p.Path)];

    /// <summary>Whether a language server loaded from one has the other's projects: the same solution listing the same
    /// projects, or the same projects without one. A server is told its solution or projects once, when it starts, so
    /// other projects need a new server.</summary>
    public bool LoadsAs(WorkspaceModel other) =>
        Solution?.Path == other.Solution?.Path && Projects.Select(p => p.Path).SequenceEqual(other.Projects.Select(p => p.Path));

    /// <summary>The project files under a folder, those nearer the top first, as a walk for what projects build finds them
    /// (<see cref="FolderWalk"/>): hidden folders are version control's, IDEs' and tools' own (a worktree's copy of them).</summary>
    private static List<string> ProjectsUnder(string folder) =>
        [.. FolderWalk.Folders(folder, built: true).Take(FolderLimit).SelectMany(f => f.Files).Where(IsProject).Take(ProjectLimit)];
}
