using Passenger.Core;
using Passenger.Host;

namespace Passenger.Workspace;

/// <summary>The open folder's solution and projects, decided here alone for the Explorer, the language server, Build,
/// Run, NuGet and the test runner: the solution, solution filter or project opened by name while it is there, else the
/// solution at the folder's top; with none, the projects anywhere under the folder but its build output, dependencies and
/// hidden folders. Beside them, its file-based programs (<see cref="IsProgram"/>) outside every project's folder, each a
/// project of its own named by its C# file. Read when the folder opens and again when the folder watcher sees a
/// solution, project or program change, never on each use.</summary>
public sealed record WorkspaceModel(string Folder, SolutionModel? Solution, IReadOnlyList<ProjectModel> Projects)
{
    /// <summary>The most projects taken from a folder with no solution, so a monorepo does not load hundreds at once.</summary>
    private const int ProjectLimit = 200;

    /// <summary>The most folders searched for them, so a folder that is really a disk is not walked for minutes.</summary>
    private const int FolderLimit = 5_000;

    /// <summary>The C# project the workspace was opened as: its one project, with no solution, whatever else its folder
    /// holds. Null for a folder or a solution.</summary>
    public string? ProjectFile { get; init; }

    /// <summary>The solution filter it was opened as: its solution is <see cref="Solution"/>, every project of it is in
    /// <see cref="Projects"/>, and those the filter leaves out are the session's to unload. Null for any other workspace.</summary>
    public SolutionFilter? Filter { get; init; }

    /// <summary>The solution the folder opens by that does not read, in whose place the folder's own projects are read;
    /// null for any other workspace.</summary>
    public UnreadSolution? Unread { get; init; }

    /// <summary>What it was opened as, which Recent Solutions keeps and reopens: the solution filter, the solution, the
    /// project, else the folder.</summary>
    public string OpenedAs => Filter?.Path ?? Solution?.Path ?? ProjectFile ?? Folder;

    /// <summary>Its name as the toolbar, the window's title and Recent Solutions show it: the solution filter's, the
    /// solution's, the project's, else the folder's.</summary>
    public string Name => Filter is { } filter ? Path.GetFileNameWithoutExtension(filter.Path)
        : Solution?.Name ?? (ProjectFile is { } project ? Path.GetFileNameWithoutExtension(project) : Path.GetFileName(Folder));

    /// <summary>Whether a file opens as the workspace rather than in an editor: a solution (.sln, .slnx), a solution
    /// filter (.slnf) or a C# project (.csproj).</summary>
    public static bool Opens(string path) => IsSolution(path) || IsProject(path);

    /// <summary>Whether a file is a solution or a solution filter, either of which opens a solution.</summary>
    public static bool IsSolution(string path) => Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf";

    public static bool IsProject(string path) => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a solution's project is a Docker Compose project (.dcproj).</summary>
    public static bool IsComposeProject(string path) => Path.GetExtension(path).Equals(".dcproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a folder, with the solution or project it was opened by, which stays its workspace while it is there,
    /// and walks it for its programs (a folder without either for its projects too), which is a worker's job, never the
    /// window's thread's.</summary>
    public static WorkspaceModel Read(string folder, string? named = null) =>
        ReadSolution(folder, named, out UnreadSolution? unread) is { } read
            ? read.WithPrograms(Walk(folder, [.. read.Projects.Where(p => !p.IsProgram).Select(p => p.Folder)]))
            : new WorkspaceModel(folder, null, [.. Walk(folder, null).Select(ProjectModel.Unevaluated)]) { Unread = unread };

    /// <summary>A folder read through the solution or project it was opened by, else its own solution: a listing of the
    /// folder's top and the files the solution names, quick enough for the window's thread. Null for a folder without one
    /// (or one that does not read), whose projects only a walk of its folders finds.</summary>
    public static WorkspaceModel? ReadSolution(string folder, string? named = null) => ReadSolution(folder, named, out _);

    /// <summary>A folder read as <see cref="ReadSolution(string, string?)"/> reads it, with why its solution did not read in
    /// <paramref name="unread"/> when that is why there is none.</summary>
    public static WorkspaceModel? ReadSolution(string folder, string? named, out UnreadSolution? unread)
    {
        unread = null;
        if (named is not null && IsProject(named) && File.Exists(named))
        {
            return new WorkspaceModel(folder, null, [ProjectModel.Unevaluated(named)]) { ProjectFile = named };
        }
        string? path = named is not null && IsSolution(named) && File.Exists(named) ? named : SolutionModel.Find(folder);
        SolutionFilter? filter = path is not null && SolutionFilter.Is(path) ? SolutionFilter.Read(path) : null;
        if (path is not null && SolutionFilter.Is(path))
        {
            path = filter?.Solution;
        }
        // A Docker Compose project (.dcproj) is read as XML for its run configurations, never loaded as a project.
        return path is not null && SolutionModel.Load(path, out unread) is { } solution
            ? new WorkspaceModel(folder, solution, [.. solution.Projects.Where(p => !IsComposeProject(p.Path))]) { Filter = filter }
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

    /// <summary>A solution's or project's read with the programs found beside it before, those still there, until the walk
    /// for them lands: what the language server loaded stays loaded meanwhile.</summary>
    public WorkspaceModel KeepingPrograms(WorkspaceModel before) =>
        WithPrograms(before.Programs.Where(p => File.Exists(p.Path)).Select(p => p.Path));

    /// <summary>The same workspace with these programs in place of those it had, after its solution's or folder's projects.</summary>
    public WorkspaceModel WithPrograms(IEnumerable<string> programs) =>
        this with { Projects = [.. Projects.Where(p => !p.IsProgram), .. programs.Distinct(PathNames.Comparer).Select(ProjectModel.Unevaluated)] };

    /// <summary>Its file-based programs, which no solution lists: those beside a solution's or project's, or among a folder's.</summary>
    public IEnumerable<ProjectModel> Programs => Projects.Where(p => p.IsProgram);

    /// <summary>What building the whole builds: the solution and each program beside it, or each project loaded in turn
    /// when some are unloaded or there is no solution; the folder when it has neither, for the .NET command line to say why.</summary>
    public IReadOnlyList<string> BuildTargets(IReadOnlyList<ProjectModel> loaded) =>
        Solution is not null && loaded.Count == Projects.Count && ComposeProjects.Count == 0 ? [Solution.Path, .. Programs.Select(p => p.Path)]
        : Solution is null && Projects.Count == 0 ? [Folder]
        : [.. loaded.Select(p => p.Path)];

    /// <summary>Whether a language server loaded from one has the other's projects: the same solution listing the same
    /// projects, through the same filter of them if any, or the same projects without one. A server is told its solution
    /// or projects once, when it starts, so other projects need a new server.</summary>
    public bool LoadsAs(WorkspaceModel other) =>
        Solution?.Path == other.Solution?.Path && Projects.Select(p => p.Path).SequenceEqual(other.Projects.Select(p => p.Path))
        && (Filter is null ? other.Filter is null : Filter.Keeps(other.Filter));

    /// <summary>The programs a read of the same folder has beyond this one's, when nothing else differs: the same solution
    /// through the same filter, every project of this one's still there. Null for any other difference, or none.</summary>
    public IReadOnlyList<string>? ProgramsAddedIn(WorkspaceModel read)
    {
        List<string> added = [.. read.Programs.Select(p => p.Path).Where(p => !Projects.Any(q => PathNames.Same(q.Path, p)))];
        bool kept = Solution?.Path == read.Solution?.Path && (Filter is null ? read.Filter is null : Filter.Keeps(read.Filter))
            && Projects.All(p => read.Projects.Any(q => PathNames.Same(q.Path, p.Path)))
            && read.Projects.Count == Projects.Count + added.Count;
        return kept && added.Count > 0 ? added : null;
    }

    /// <summary>The project files and programs under a folder, those nearer the top first, as a walk for what projects build
    /// finds them (<see cref="FolderWalk"/>): hidden folders are version control's, IDEs' and tools' own (a worktree's copy
    /// of them). A program is looked for only outside every project's folder. Given the folders of a solution's or project's
    /// own (<paramref name="solved"/>), the walk enters none of them and takes programs alone, a project file it finds
    /// keeping its folder's files its own.</summary>
    private static List<string> Walk(string folder, IReadOnlyList<string>? solved)
    {
        List<string> found = [];
        List<string> projectFolders = [.. solved ?? []];
        if (projectFolders.Any(p => PathNames.Within(folder, p)))
        {
            return found;
        }
        HashSet<string> passed = new(projectFolders, PathNames.Comparer);
        foreach ((string at, IReadOnlyList<string> files) in FolderWalk.Folders(folder, built: true, enters: f => !passed.Contains(f)).Take(FolderLimit))
        {
            List<string> projects = [.. files.Where(IsProject)];
            if (projects.Count > 0)
            {
                projectFolders.Add(at);
                if (solved is null)
                {
                    found.AddRange(projects);
                }
            }
            else if (!projectFolders.Any(p => PathNames.IsUnder(at, p)))
            {
                found.AddRange(ProgramsAmong(files));
            }
            if (found.Count >= ProjectLimit)
            {
                break;
            }
        }
        // A file a program includes, from any folder or by a wildcard, is that program's code, whatever its own head says.
        List<(string Program, List<string> Patterns)> including = [.. found.Where(ProgramFiles.IsProgram).Select(p => (p, Included(p).ToList()))];
        found.RemoveAll(q => ProgramFiles.IsProgram(q) && including.Any(i => !PathNames.Same(i.Program, q) && i.Patterns.Any(pattern => Globbed(pattern, q, PathNames.CaseIgnoredIn))));
        // A program another references (#:ref lib.cs) is one, its project the SDK's, whatever its head says.
        for (int i = 0; i < found.Count; i++)
        {
            foreach (string referenced in ProgramFiles.IsProgram(found[i]) ? Named(found[i], "ref").ToList() : [])
            {
                if (File.Exists(referenced) && !found.Contains(referenced, PathNames.Comparer))
                {
                    found.Add(referenced);
                }
            }
        }
        return [.. found.Take(ProjectLimit)];
    }

    /// <summary>The C# files a program's directives of a kind name by a path of their own (no wildcard nor property in it).</summary>
    private static IEnumerable<string> Named(string program, string kind)
    {
        foreach (string directive in ProgramFiles.Head(program))
        {
            if (ProgramFiles.Parse(directive) is ({ } named, { Length: > 0 } value) && named == kind && value.IndexOfAny(['*', '?', '$']) < 0
                && ProgramFiles.IsProgram(value))
            {
                yield return PathNames.Canonical(Path.GetFullPath(value, Path.GetDirectoryName(program)!));
            }
        }
    }

    /// <summary>The C# files a program's <c>#:include</c> directives name, each a full path that may hold MSBuild's wildcards
    /// (<c>lib/*.cs</c>, <c>**</c>) as the SDK hands it to MSBuild; none that names a property.</summary>
    private static IEnumerable<string> Included(string program)
    {
        foreach (string directive in ProgramFiles.Head(program))
        {
            if (ProgramFiles.Parse(directive) is ("include", { Length: > 0 } value) && !value.Contains('$', StringComparison.Ordinal)
                && (ProgramFiles.IsProgram(value) || value.EndsWith('*')))
            {
                yield return Path.GetFullPath(value, Path.GetDirectoryName(program)!);
            }
        }
    }

    /// <summary>Whether a file matches an include as MSBuild's globs match: <c>*</c> and <c>?</c> within a name, <c>**</c>
    /// across folders, a name differing only in letter case where its folder ignores case (<paramref name="caseIgnoredIn"/>).</summary>
    internal static bool Globbed(string pattern, string path, Func<string, bool> caseIgnoredIn) =>
        Matches(pattern.Split(Separators), 0, path, path.Split(Separators), 0, caseIgnoredIn);

    private static readonly char[] Separators = ['/', '\\'];

    private static bool Matches(string[] parts, int part, string path, string[] names, int name, Func<string, bool> caseIgnoredIn) =>
        part == parts.Length ? name == names.Length
        : parts[part] == "**" ? Matches(parts, part + 1, path, names, name, caseIgnoredIn) || (name < names.Length && Matches(parts, part, path, names, name + 1, caseIgnoredIn))
        : name < names.Length && MatchesAt(parts[part], path, names, name, caseIgnoredIn) && Matches(parts, part + 1, path, names, name + 1, caseIgnoredIn);

    /// <summary>Whether a path's name at an index matches one part of a glob, in the letter case its folder tells.</summary>
    private static bool MatchesAt(string part, string path, string[] names, int name, Func<string, bool> caseIgnoredIn) =>
        NameMatches(part, names[name], ignoreCase: false)
        || NameMatches(part, names[name], ignoreCase: true) && caseIgnoredIn(path[..names.Take(name).Sum(n => n.Length + 1)]);

    /// <summary>Whether a name matches one part of a glob; the empty part before a rooted path's first separator, which the
    /// system's matcher matches with nothing, matches the empty name.</summary>
    private static bool NameMatches(string part, string name, bool ignoreCase) => part.AsSpan().IndexOfAny('*', '?') < 0
        ? name.Equals(part, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        : System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(part, name, ignoreCase);

    /// <summary>Whether a C# file's head makes it a file-based program, as <c>dotnet run</c> runs it: a <c>#!</c> line or a
    /// <c>#:</c> directive, unless it is a single-file Aspire AppHost, which Aspire runs. A folder's walk takes one only
    /// outside every project's folder, whose files are the project's.</summary>
    public static bool IsProgram(string path, IReadOnlyList<string> head) =>
        ProgramFiles.IsProgram(path) && head.Count > 0 && !head.Any(IsAspireSdk);

    /// <summary>The C# files among one folder's whose heads make them programs.</summary>
    private static IEnumerable<string> ProgramsAmong(IReadOnlyList<string> files) => files.Where(f => IsProgram(f, ProgramFiles.Head(f)));

    private static bool IsAspireSdk(string directive) =>
        ProgramFiles.Parse(directive) is ("sdk", { } sdk) && sdk.Split('@')[0].Trim().Equals("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase);
}
