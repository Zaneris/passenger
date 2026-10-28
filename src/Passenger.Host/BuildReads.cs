using System.Collections.Concurrent;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>What a build of a project reads, as its evaluations with the build's global properties can say, and what it
/// makes, with each project it references in turn (every framework of one, with those properties but the framework, as a
/// reference is built). A build that reads more than its evaluations show is said as such: a project not SDK-style C#, a
/// target or build event of the project's own, a response file, a reference given other properties. Evaluated afresh
/// each time, every file stamped before the build it comes before; several projects asked at once share their
/// evaluations, each project they reach evaluated once, side by side.</summary>
internal static class BuildReads
{
    /// <summary>A reference's metadata that builds it with other global properties than its evaluation here has.</summary>
    private static readonly string[] Reglobalizing =
        ["SetConfiguration", "SetPlatform", "SetTargetFramework", "AdditionalProperties", "GlobalPropertiesToRemove", "Properties", "UndefineProperties"];

    /// <summary>Properties naming a file the build reads that may be no item a build reads (an icon, a key, a manifest).</summary>
    private static readonly string[] FileProperties =
    [
        "ApplicationIcon", "ApplicationManifest", "Win32Resource", "Win32Manifest", "KeyOriginatorFile", "AssemblyOriginatorKeyFile",
        "CodeAnalysisRuleSet", "UserRuntimeConfig", "AppConfig",
    ];

    /// <summary>What a build of each project asked reads and makes, in the order asked.</summary>
    public static IReadOnlyList<BuildInputs> Run(InputsParams asked, CancellationToken cancel)
    {
        Dictionary<string, string> globals = new(asked.Properties, StringComparer.OrdinalIgnoreCase);
        string? framework = globals.Remove("TargetFramework", out string? given) ? given : null;
        using ProjectCollection collection = new(globals, loggers: null, ToolsetDefinitionLocations.Default);
        Evaluated evaluated = new(collection, EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared), globals);
        try
        {
            evaluated.Load([.. asked.Projects.Select(project => (project, framework))], cancel);
            return [.. asked.Projects.Select(project => Inputs(project, framework, evaluated, cancel))];
        }
        finally
        {
            foreach (Project loaded in collection.LoadedProjects.ToList())
            {
                collection.UnloadProject(loaded);
            }
        }
    }

    /// <summary>What a build of one project reads and makes, from the evaluations made (<see cref="Evaluated"/>).</summary>
    private static BuildInputs Inputs(string asked, string? framework, Evaluated evaluated, CancellationToken cancel)
    {
        Dictionary<string, Stamp> reads = new(PathNames.Comparer);
        HashSet<string> seen = new(PathNames.Comparer) { asked };
        HashSet<string> restored = new(PathNames.Comparer);
        Queue<string> references = new();
        List<string> referenced = [];
        string? output = null;
        string? written = null;
        try
        {
            foreach ((Project project, Walk walk) in evaluated.Of(asked, framework, reads))
            {
                if (walk.Why is { } why)
                {
                    return new BuildInputs { Unknown = why };
                }
                Take(walk);
                if (output is null)
                {
                    output = PathOf(project, "OutDir");
                    written = Path.Combine(PathOf(project, "IntermediateOutputPath"), project.GetPropertyValue("CleanFile"));
                }
            }
            while (references.TryDequeue(out string? reference))
            {
                cancel.ThrowIfCancellationRequested();
                foreach ((Project project, Walk walk) in evaluated.Of(reference, null, reads))
                {
                    if (walk.Why is { } why)
                    {
                        return new BuildInputs { Unknown = why };
                    }
                    Take(walk);
                    if (project.GetPropertyValue("TargetPath") is { Length: > 0 } target)
                    {
                        referenced.Add(Path.GetFullPath(Separated(target), project.DirectoryPath));
                    }
                }
            }
        }
        catch (Exception error) when (Unevaluated(error))
        {
            return new BuildInputs { Unknown = error.Message };
        }
        return new BuildInputs
        {
            Reads = [.. reads.Values.OrderBy(s => s.Path, StringComparer.Ordinal)], Output = output, Written = written, Referenced = referenced,
            Restored = [.. restored.Where(reads.ContainsKey)],
        };

        void Take(Walk walk)
        {
            foreach ((string path, Stamp stamp) in walk.Reads)
            {
                reads.TryAdd(path, stamp);
            }
            foreach (string reference in walk.References)
            {
                if (seen.Add(reference))
                {
                    references.Enqueue(reference);
                }
            }
            restored.UnionWith(walk.Restored);
        }
    }

    /// <summary>What one evaluation says a build of it reads, the projects it references and those of its reads a restore
    /// writes (<see cref="Unknown"/>); or why a build of it reads more than that.</summary>
    private sealed record Walk(string? Why, Dictionary<string, Stamp> Reads, IReadOnlyList<string> References, HashSet<string> Restored)
    {
        public static Walk Of(Project project)
        {
            Dictionary<string, Stamp> reads = new(PathNames.Comparer);
            Queue<string> references = new();
            HashSet<string> restored = new(PathNames.Comparer);
            string? why = Unknown(project, references, new HashSet<string>(PathNames.Comparer), reads, new HashSet<string>(PathNames.Comparer), restored);
            return new Walk(why, reads, [.. references], restored);
        }
    }

    /// <summary>Whether an evaluation's failure says only that the project's build cannot be accounted for.</summary>
    private static bool Unevaluated(Exception error) =>
        error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    /// <summary>The evaluations a request's answers read, each project's made and walked once (its file stamped before it is
    /// read): those asked first, side by side, then the projects they reference, a round at a time. One a round did not
    /// reach is made when first read.</summary>
    private sealed class Evaluated(ProjectCollection collection, EvaluationContext context, IReadOnlyDictionary<string, string> globals)
    {
        private readonly ConcurrentDictionary<string, Lazy<(Stamp File, List<(Project Project, Walk Walk)>? Projects, Exception? Failed)>> made = new(PathNames.Comparer);

        /// <summary>Makes the evaluations of the projects given, and of every project they reference in turn, as a build of
        /// each reaches it (a reference with every framework of its own).</summary>
        public void Load(IReadOnlyList<(string Path, string? Framework)> projects, CancellationToken cancel)
        {
            ParallelOptions parallel = new() { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancel };
            HashSet<string> reached = new(PathNames.Comparer);
            List<(string Path, string? Framework)> round = [.. projects];
            while (round.Count > 0)
            {
                Parallel.ForEach(round, parallel, each => _ = Get(each.Path, each.Framework));
                round = [.. round.SelectMany(each => Get(each.Path, each.Framework).Projects ?? []).SelectMany(each => each.Walk.References)
                    .Where(reference => reached.Add(reference) && !made.ContainsKey(Key(reference, null))).Select(reference => (reference, (string?)null))];
            }
        }

        /// <summary>A project's evaluations a build reads (<see cref="Evaluations"/>), each with its walk, its file's stamp put
        /// in <paramref name="reads"/>; the failure of either thrown again.</summary>
        public List<(Project Project, Walk Walk)> Of(string path, string? framework, Dictionary<string, Stamp> reads)
        {
            (Stamp file, List<(Project Project, Walk Walk)>? projects, Exception? failed) = Get(path, framework);
            reads.TryAdd(path, file);
            return projects ?? throw failed!;
        }

        private (Stamp File, List<(Project Project, Walk Walk)>? Projects, Exception? Failed) Get(string path, string? framework) =>
            made.GetOrAdd(Key(path, framework), _ => new(() =>
            {
                Stamp file = Stamps.File(path);
                try
                {
                    return (file, [.. Evaluations(path, framework, collection, context, globals).Select(project => (project, Walk.Of(project)))], null);
                }
                catch (Exception error) when (Unevaluated(error))
                {
                    return (file, null, error);
                }
            })).Value;

        private static string Key(string path, string? framework) => $"{framework}|{path}";
    }

    /// <summary>A project's evaluations a build reads: of the framework given, else its one, else its outer one and each of its
    /// frameworks'.</summary>
    private static List<Project> Evaluations(string path, string? framework, ProjectCollection collection, EvaluationContext context,
        IReadOnlyDictionary<string, string> globals)
    {
        Project first = Evaluation.Load(path, collection, context, globals, framework);
        if (framework is not null || first.GetPropertyValue("TargetFramework").Length > 0)
        {
            return [first];
        }
        return [first, .. first.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(each => Evaluation.Load(path, collection, context, globals, each))];
    }

    /// <summary>What one evaluation says a build reads, added to <paramref name="reads"/> and its references to
    /// <paramref name="references"/>; or why a build of it reads more than that.</summary>
    private static string? Unknown(Project project, Queue<string> references, HashSet<string> seen, Dictionary<string, Stamp> reads, HashSet<string> repositories,
        HashSet<string> restored)
    {
        string name = Path.GetFileName(project.FullPath);
        if (!project.FullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || !project.GetPropertyValue("UsingMicrosoftNETSdk").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} is no SDK-style C# project";
        }
        HashSet<string> installed = Evaluation.OutsideRoots(project);
        if (project.Targets.Values.FirstOrDefault(t => !Installed(t.FullPath, installed)) is { } own)
        {
            return $"{name} has a target of its own, {own.Name} in {Path.GetFileName(own.FullPath)}";
        }
        foreach (string step in (string[])["PreBuildEvent", "PostBuildEvent"])
        {
            if (project.GetPropertyValue(step).Trim().Length > 0)
            {
                return $"{name} has a {step}";
            }
        }
        for (string? at = project.DirectoryPath; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "Directory.Build.rsp")))
            {
                return $"{Path.Combine(at, "Directory.Build.rsp")} gives the build switches of its own";
            }
        }
        foreach (Stamp input in Evaluation.Inputs(Stamps.File(project.FullPath), [project], installed, Evaluation.LookedFor(project.FullPath, project)))
        {
            reads.TryAdd(input.Path, input);
        }
        string assets = PathOf(project, "ProjectAssetsFile");
        Read(assets);
        restored.Add(assets);
        string extensions = PathOf(project, "MSBuildProjectExtensionsPath");
        string named = Path.GetFileName(project.FullPath);
        restored.UnionWith([Path.Combine(extensions, named + ".nuget.g.props"), Path.Combine(extensions, named + ".nuget.g.targets"), PathOf(project, "NuGetLockFilePath")]);
        foreach (string property in FileProperties)
        {
            Read(PathOf(project, property));
        }
        // What the build itself writes, some of which items name (the intermediate assembly, its symbols, markers).
        string[] built = [.. ((string[])["BaseOutputPath", "BaseIntermediateOutputPath", "OutDir", "IntermediateOutputPath"])
            .Select(folder => PathOf(project, folder)).Where(folder => folder.Length > 0)];
        HashSet<string> sources = new(PathNames.Comparer);
        foreach (ProjectItem item in project.AllEvaluatedItems)
        {
            if (item.ItemType == "ProjectReference")
            {
                if (Reglobalizing.FirstOrDefault(m => item.GetMetadataValue(m).Length > 0) is { } metadata)
                {
                    return $"{name} references {item.EvaluatedInclude} with {metadata}";
                }
                string reference = item.GetMetadataValue("FullPath");
                if (seen.Add(reference))
                {
                    references.Enqueue(reference);
                }
                continue;
            }
            // A None file the build does not copy is none it reads: a launch profile, a database the app writes beside it.
            if (item.ItemType == "None" && item.GetMetadataValue("CopyToOutputDirectory") is var copy && (copy.Length == 0 || copy.Equals("Never", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            // What Clean deletes, which a build writes rather than reads: a file-based program's caches beside its output.
            if (item.ItemType == "Clean")
            {
                continue;
            }
            string path = item.ItemType == "Reference" && item.GetMetadataValue("HintPath") is { Length: > 0 } hint
                ? Path.GetFullPath(Separated(hint), project.DirectoryPath)
                : FullPath(item);
            if (path.Length > 0 && !Installed(path, installed) && !built.Any(folder => PathNames.IsUnder(path, folder)) && File.Exists(path))
            {
                Read(path);
                if (item.ItemType is "Compile" or "AdditionalFiles")
                {
                    sources.Add(Path.GetDirectoryName(path)!);
                }
            }
        }
        // The analyzers' settings over every source, which the compiler finds by walking up from each.
        HashSet<string> above = new(PathNames.Comparer);
        foreach (string folder in sources)
        {
            for (string? at = folder; at is not null && above.Add(at); at = Path.GetDirectoryName(at))
            {
                Read(Path.Combine(at, ".editorconfig"));
                Read(Path.Combine(at, ".globalconfig"));
            }
        }
        Repository(project.DirectoryPath, repositories, Read);
        return null;

        void Read(string file)
        {
            if (file.Length > 0)
            {
                reads.TryAdd(file, Stamps.File(file));
            }
        }
    }

    /// <summary>A property's path made full from the project's folder, its separators this system's as MSBuild's tasks take
    /// them; empty for a property with no value.</summary>
    private static string PathOf(Project project, string property) =>
        project.GetPropertyValue(property) is { Length: > 0 } value ? Path.GetFullPath(Separated(value), project.DirectoryPath) : string.Empty;

    private static string Separated(string path) => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>An item's full path, or none for an include that names no path.</summary>
    private static string FullPath(ProjectItem item)
    {
        try
        {
            return item.GetMetadataValue("FullPath");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static bool Installed(string path, HashSet<string> installed) => installed.Any(root => path.StartsWith(root, PathNames.Comparison));

    /// <summary>The Git repository over a folder, once: its HEAD and the branch it names, loose and packed, since the SDK
    /// stamps the assembly's informational version with the commit (a worktree's .git file leads to its own HEAD).</summary>
    private static void Repository(string folder, HashSet<string> repositories, Action<string> read)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            string dotGit = Path.Combine(at, ".git");
            string? git = Directory.Exists(dotGit) ? dotGit
                : File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is { } link && link.StartsWith("gitdir:", StringComparison.Ordinal)
                    ? Path.GetFullPath(link["gitdir:".Length..].Trim(), at)
                    : null;
            if (git is null)
            {
                continue;
            }
            if (!repositories.Add(git))
            {
                return;
            }
            string head = Path.Combine(git, "HEAD");
            read(head);
            string common = File.Exists(Path.Combine(git, "commondir")) ? Path.GetFullPath(File.ReadAllText(Path.Combine(git, "commondir")).Trim(), git) : git;
            read(Path.Combine(common, "packed-refs"));
            if (File.Exists(head) && File.ReadAllText(head).Trim() is { } said && said.StartsWith("ref:", StringComparison.Ordinal))
            {
                read(Path.Combine(common, said["ref:".Length..].Trim()));
            }
            return;
        }
    }
}
