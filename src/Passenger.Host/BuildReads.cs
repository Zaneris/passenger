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
/// each time, every file stamped before the build it comes before.</summary>
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

    public static BuildInputs Run(InputsParams asked, CancellationToken cancel)
    {
        Dictionary<string, string> globals = new(asked.Properties, StringComparer.OrdinalIgnoreCase);
        string? framework = globals.Remove("TargetFramework", out string? given) ? given : null;
        using ProjectCollection collection = new(globals, loggers: null, ToolsetDefinitionLocations.Default);
        EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        Dictionary<string, Stamp> reads = new(PathNames.Comparer);
        HashSet<string> seen = new(PathNames.Comparer) { asked.Project };
        HashSet<string> repositories = new(PathNames.Comparer);
        HashSet<string> restored = new(PathNames.Comparer);
        Queue<string> references = new();
        List<string> referenced = [];
        string? output = null;
        string? written = null;
        try
        {
            foreach (Project project in Evaluations(asked.Project, framework, collection, context, globals, reads))
            {
                if (Unknown(project, references, seen, reads, repositories, restored) is { } why)
                {
                    return new BuildInputs { Unknown = why };
                }
                if (output is null)
                {
                    output = PathOf(project, "OutDir");
                    written = Path.Combine(PathOf(project, "IntermediateOutputPath"), project.GetPropertyValue("CleanFile"));
                }
            }
            while (references.TryDequeue(out string? reference))
            {
                cancel.ThrowIfCancellationRequested();
                foreach (Project project in Evaluations(reference, null, collection, context, globals, reads))
                {
                    if (Unknown(project, references, seen, reads, repositories, restored) is { } why)
                    {
                        return new BuildInputs { Unknown = why };
                    }
                    if (project.GetPropertyValue("TargetPath") is { Length: > 0 } target)
                    {
                        referenced.Add(Path.GetFullPath(Separated(target), project.DirectoryPath));
                    }
                }
            }
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new BuildInputs { Unknown = error.Message };
        }
        finally
        {
            foreach (Project loaded in collection.LoadedProjects.ToList())
            {
                collection.UnloadProject(loaded);
            }
        }
        return new BuildInputs
        {
            Reads = [.. reads.Values.OrderBy(s => s.Path, StringComparer.Ordinal)], Output = output, Written = written, Referenced = referenced,
            Restored = [.. restored.Where(reads.ContainsKey)],
        };
    }

    /// <summary>A project's evaluations a build reads: of the framework given, else its one, else its outer one and each of its
    /// frameworks'; its file stamped before it is read.</summary>
    private static List<Project> Evaluations(string path, string? framework, ProjectCollection collection, EvaluationContext context,
        IReadOnlyDictionary<string, string> globals, Dictionary<string, Stamp> reads)
    {
        reads.TryAdd(path, Stamps.File(path));
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
            // What no build reads: a launch profile (dotnet run's alone, which the IDE writes), the system's own files.
            if (item.ItemType == "None" && (PathNames.IsSystemFile(Path.GetFileName(item.EvaluatedInclude))
                || item.EvaluatedInclude.Replace('\\', '/').EndsWith("Properties/launchSettings.json", StringComparison.OrdinalIgnoreCase)))
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
