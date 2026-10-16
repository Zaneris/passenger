using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace Passenger.Host;

/// <summary>Design-time builds, as Roslyn's own build host runs them for its language server: each project (each target
/// framework of a multi-targeted one) built to its compiler's command line without compiling, with its documents,
/// additional files, .editorconfig files and outputs, written as Roslyn's ProjectFileInfo so the server's own project
/// loader takes them as it takes its build host's. The builds of one request share one build session, on MSBuild's nodes
/// (half the processors, as Roslyn's), one session at a time.</summary>
internal static class DesignTimeBuilds
{
    /// <summary>Roslyn's build host's: a design-time build that runs the compiler's task for its command line, alone.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DesignTimeBuild"] = "true",
        ["NonExistentFile"] = "__NonExistentSubDir__\\__NonExistentFile__",
        ["BuildProjectReferences"] = "false",
        ["BuildingProject"] = "false",
        ["ProvideCommandLineArgs"] = "true",
        ["SkipCompilerExecution"] = "true",
        ["ContinueOnError"] = "ErrorAndContinue",
        ["ShouldUnsetParentConfigurationAndPlatform"] = "false",
    };

    private const ProjectLoadSettings Settings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreEmptyImports
        | ProjectLoadSettings.IgnoreInvalidImports | ProjectLoadSettings.RejectCircularImports
        | ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition | ProjectLoadSettings.FailOnUnresolvedSdk;

    /// <summary>As Roslyn's build host writes ProjectFileInfo: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions Written = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One build session at a time: MSBuild's build manager is the process's.</summary>
    private static readonly SemaphoreSlim Session = new(1, 1);

    private static readonly string[] LocalPaths = ["IntermediateOutputPath", "MSBuildProjectExtensionsPath", "ProjectAssetsFile", "OutputPath", "TargetPath", "TargetRefPath"];

    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Builds the projects asked, handing each over as it is built. Cancelled, it starts no other project and
    /// cancels the builds running, so the session ends at once for the next request's.</summary>
    public static async Task Run(BuildParams asked, Action<BuiltProject> built, CancellationToken cancel)
    {
        await Session.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> global = new(Defaults, StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string value) in asked.Properties)
            {
                global[name] = value;
            }
            DiagnosticLogger diagnostics = new();
            using ProjectCollection collection = new(global, loggers: null, ToolsetDefinitionLocations.Default);
            BuildParameters parameters = new(collection)
            {
                EnableNodeReuse = false,
                MaxNodeCount = Math.Max(Environment.ProcessorCount / 2, 1),
                Loggers = [diagnostics],
            };
            EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
            BuildManager.DefaultBuildManager.BeginBuild(parameters);
            try
            {
                using CancellationTokenRegistration stopping = cancel.Register(BuildManager.DefaultBuildManager.CancelAllSubmissions);
                await Parallel.ForEachAsync(asked.Projects, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancel },
                    async (path, token) => built(await Build(path, collection, context, global, diagnostics, token).ConfigureAwait(false))).ConfigureAwait(false);
            }
            finally
            {
                BuildManager.DefaultBuildManager.EndBuild();
                // A referenced project's output or a file changed before the next request: nothing of this one is reused.
                BuildManager.DefaultBuildManager.ResetCaches();
            }
        }
        finally
        {
            Session.Release();
        }
    }

    private static async Task<BuiltProject> Build(string path, ProjectCollection collection, EvaluationContext context, Dictionary<string, string> global, DiagnosticLogger diagnostics, CancellationToken cancel)
    {
        Stamp file = Stamps.File(path);
        IReadOnlyList<Stamp> folders = Evaluation.Walked([Path.GetDirectoryName(path)!]);
        string? language = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csproj" => "C#",
            ".vbproj" => "Visual Basic",
            _ => null,
        };
        if (language is null || file.Ticks == 0)
        {
            return new BuiltProject(path, "[]", [file], language is null ? null : $"{path} does not exist");
        }
        List<Project> loaded = [];
        try
        {
            Project outer = Load(path, collection, context, global, null);
            loaded.Add(outer);
            string single = outer.GetPropertyValue("TargetFramework");
            string[] frameworks = outer.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<Project> each = single.Length > 0 || frameworks.Length == 0 ? [outer] : [.. frameworks.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => Load(path, collection, context, global, f))];
            loaded.AddRange(each.Where(p => p != outer));
            FileGlobs[] globs = [.. outer.GetAllGlobs().Select(g => new FileGlobs([.. g.IncludeGlobs.Select(Full)], [.. g.Excludes.Select(Full)], [.. g.Removes.Select(Full)]))];
            List<RoslynProjectFileInfo> infos = [];
            List<BuildDiagnostic> said = [];
            HashSet<string> assets = new(PathComparer);
            foreach (Project project in each)
            {
                string? intermediateAssembly = project.GetItems("IntermediateAssembly").FirstOrDefault()?.GetMetadataValue("FullPath");
                Dictionary<string, string> isolated = new(project.GlobalProperties, StringComparer.OrdinalIgnoreCase);
                string identity = JsonSerializer.Serialize(new
                {
                    Sdk = typeof(Project).Assembly.Location,
                    Path = path,
                    Properties = project.Properties.OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(property => new { property.Name, Value = property.EvaluatedValue }),
                });
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
                string root = global.GetValueOrDefault("PassengerDesignTimeRoot") ?? Path.Combine(Path.GetTempPath(), "passenger-design-time");
                string directory = Path.Combine(root, Path.GetFileNameWithoutExtension(path) + "-" + key);
                Directory.CreateDirectory(directory);
                using FileStream ownership = await OwnAsync(Path.Combine(directory, "generated-files.lock"), cancel).ConfigureAwait(false);
                isolated["IntermediateOutputPath"] = directory + Path.DirectorySeparatorChar;
                foreach (string property in (string[])["MSBuildProjectExtensionsPath", "ProjectAssetsFile", "OutputPath", "TargetPath", "TargetRefPath"])
                {
                    if (project.GetPropertyValue(property) is { Length: > 0 } value)
                    {
                        isolated[property] = value;
                    }
                }
                Project design = Load(path, collection, context, isolated, project.GetPropertyValue("TargetFramework") is { Length: > 0 } targetFramework ? targetFramework : null);
                loaded.Add(design);
                if (!Path.GetFullPath(design.GetPropertyValue("IntermediateOutputPath")).Equals(Path.GetFullPath(isolated["IntermediateOutputPath"]), PathComparison)
                    || project.Imports.Select(imported => imported.ImportedProject).Prepend(project.Xml)
                        .Any(xml => project.ExpandString(xml.TreatAsLocalProperty).Split(';', StringSplitOptions.TrimEntries).Contains("IntermediateOutputPath", StringComparer.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"{path} overrides the isolated IntermediateOutputPath; its design-time build was stopped to keep generated files apart from Build and dotnet watch");
                }
                collection.UnloadProject(project);
                loaded.Remove(project);
                // Keep evaluated paths local so every nested MSBuild task inherits the caller's original global properties.
                design.SkipEvaluation = true;
                foreach (string property in LocalPaths)
                {
                    if (project.GlobalProperties.TryGetValue(property, out string? value))
                    {
                        design.SetGlobalProperty(property, value);
                    }
                    else
                    {
                        design.RemoveGlobalProperty(property);
                    }
                }
                Dictionary<string, string> local = LocalPaths.Where(isolated.ContainsKey).ToDictionary(property => property, property => isolated[property], StringComparer.OrdinalIgnoreCase);
                (ProjectInstance instance, IReadOnlyList<BuildDiagnostic> logged) = await BuildAsync(design, diagnostics, local).ConfigureAwait(false);
                said.AddRange(logged.Where(d => !said.Contains(d)));
                RoslynProjectFileInfo info = Read(language, path, instance, globs, intermediateAssembly);
                if (info.CommandLineArgs.Length == 0 && !logged.Any(d => d.Error))
                {
                    return new BuiltProject(path, "[]", [file], $"{Path.GetFileName(path)}{(info.TargetFramework is { } framework ? $" ({framework})" : string.Empty)}: its design-time build gave no compiler command line");
                }
                infos.Add(info);
                if (info.ProjectAssetsFilePath is { Length: > 0 } asset)
                {
                    assets.Add(asset);
                }
            }
            HashSet<string> outside = Evaluation.OutsideRoots(outer);
            List<Stamp> inputs = [file, .. loaded
                .SelectMany(p => p.Imports.Select(i => i.ImportedProject.FullPath))
                .Distinct(PathComparer)
                .Where(i => !outside.Any(root => i.StartsWith(root, PathComparison)))
                .Select(Stamps.File), .. assets.Select(Stamps.File)];
            string own = Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar;
            List<Stamp> walked = [.. folders, .. infos
                .SelectMany(i => i.Documents.Concat(i.AdditionalDocuments))
                .Select(d => Path.GetDirectoryName(d.FilePath)!)
                .Where(d => !d.StartsWith(own, PathComparison))
                .Distinct(PathComparer)
                .Where(Directory.Exists)
                .Select(Stamps.Folder)];
            return new BuiltProject(path, JsonSerializer.Serialize(infos, Written), [.. inputs, .. walked], null, said);
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new BuiltProject(path, "[]", [file], error.Message);
        }
        finally
        {
            foreach (Project project in loaded)
            {
                collection.UnloadProject(project);
            }
        }
    }

    private static Project Load(string path, ProjectCollection collection, EvaluationContext context, Dictionary<string, string> global, string? framework)
    {
        Dictionary<string, string> properties = new(global, StringComparer.OrdinalIgnoreCase);
        if (framework is not null)
        {
            properties["TargetFramework"] = framework;
        }
        return WindowsTargeting.Load(given => Project.FromFile(path, new ProjectOptions
        {
            GlobalProperties = given,
            ProjectCollection = collection,
            EvaluationContext = context,
            LoadSettings = Settings,
        }), properties, collection);
    }

    private static async Task<FileStream> OwnAsync(string path, CancellationToken cancel)
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 11 or 32 or 33 or 35)
            {
                await Task.Delay(50, cancel).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Compile and CoreCompile, and XAML's markup compilation where a project has it, as Roslyn's build host runs,
    /// with the errors and warnings the build logged, which the server reports as its build host's.</summary>
    private static async Task<(ProjectInstance Built, IReadOnlyList<BuildDiagnostic> Logged)> BuildAsync(Project project, DiagnosticLogger diagnostics, Dictionary<string, string> local)
    {
        ProjectInstance instance = project.CreateProjectInstance();
        foreach ((string property, string value) in local)
        {
            instance.SetProperty(property, value);
        }
        ProjectTargetInstance? changesIntermediates = instance.Targets.Values.FirstOrDefault(target =>
            target.Children.OfType<ProjectPropertyGroupTaskInstance>().SelectMany(group => group.Properties)
                .Any(property => property.Name.Equals("IntermediateOutputPath", StringComparison.OrdinalIgnoreCase))
            || target.Children.OfType<ProjectTaskInstance>().SelectMany(task => task.Outputs).OfType<ProjectTaskOutputPropertyInstance>()
                .Any(property => instance.ExpandString(property.PropertyName).Equals("IntermediateOutputPath", StringComparison.OrdinalIgnoreCase)));
        if (changesIntermediates is not null)
        {
            throw new InvalidOperationException($"{project.FullPath}: target {changesIntermediates.Name} assigns IntermediateOutputPath during the build; its design-time build was stopped to keep generated files apart from Build and dotnet watch");
        }
        if (!instance.Targets.ContainsKey("Compile") || !instance.Targets.ContainsKey("CoreCompile"))
        {
            return (instance, []);
        }
        List<string> targets = ["Compile", "CoreCompile"];
        if (instance.Targets.ContainsKey("DesignTimeMarkupCompilation"))
        {
            targets.Add("DesignTimeMarkupCompilation");
        }
        BuildRequestData request = new(instance, [.. targets], null, BuildRequestDataFlags.ProvideProjectStateAfterBuild);
        TaskCompletionSource<BuildResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BuildSubmission submission = BuildManager.DefaultBuildManager.PendBuildRequest(request);
        List<BuildDiagnostic> logged = diagnostics.Register(submission.SubmissionId);
        submission.ExecuteAsync(s =>
        {
            try
            {
                done.TrySetResult(s.BuildResult);
            }
            catch (Exception error)
            {
                done.TrySetException(error);
            }
        }, null);
        BuildResult result = await done.Task.ConfigureAwait(false);
        return (result.ProjectStateAfterBuild ?? instance, diagnostics.Take(submission.SubmissionId));
    }

    /// <summary>A built project as Roslyn's build host reads one (its ProjectInstanceReader).</summary>
    private static RoslynProjectFileInfo Read(string language, string path, ProjectInstance instance, FileGlobs[] globs, string? intermediateAssembly)
    {
        string directory = Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar;
        string? Absolute(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Path.Combine(directory, value));
        string? Property(string name) => instance.GetProperty(name)?.EvaluatedValue is { Length: > 0 } value ? value : null;
        string? assembly = Absolute(intermediateAssembly);
        string[] arguments = [.. instance.GetItems(language == "C#" ? "CscCommandLineArgs" : "VbcCommandLineArgs").Select(i => i.EvaluatedInclude)];
        // Roslyn's options processor takes its baseline assembly from /out, overriding the supplied output metadata.
        if (assembly is not null)
        {
            arguments = [.. arguments.Select(argument => argument.StartsWith("/out:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-out:", StringComparison.OrdinalIgnoreCase) ? argument[..5] + "\"" + assembly + "\"" : argument)];
        }
        return new RoslynProjectFileInfo
        {
            Language = language,
            FilePath = path,
            OutputFilePath = Absolute(Property("TargetPath")),
            OutputRefFilePath = Absolute(Property("TargetRefPath")),
            GeneratedFilesOutputDirectory = Absolute(Property("CompilerGeneratedFilesOutputPath")),
            // Roslyn's Edit and Continue reads the real build's assembly here; this design-time build emits none.
            IntermediateOutputFilePath = assembly,
            DefaultNamespace = Property("RootNamespace") ?? string.Empty,
            TargetFramework = Property("TargetFramework"),
            TargetFrameworkIdentifier = Property("TargetFrameworkIdentifier"),
            TargetFrameworkVersion = Property("TargetFrameworkVersion"),
            ProjectAssetsFilePath = Property("ProjectAssetsFile"),
            CommandLineArgs = arguments,
            Documents = [.. instance.GetItems("Compile").Where(i => !Path.GetFileName(i.EvaluatedInclude).StartsWith("TemporaryGeneratedFile_", StringComparison.Ordinal)).Select(i => Document(i, directory))],
            AdditionalDocuments = [.. instance.GetItems("AdditionalFiles").Select(i => Document(i, directory))],
            AnalyzerConfigDocuments = [.. instance.GetItems("EditorConfigFiles").Select(i => Document(i, directory))],
            ProjectReferences = [.. instance.GetItems("ProjectReference").Select(r => new ProjectFileReference(r.EvaluatedInclude, Aliases(r), !r.GetMetadataValue("ReferenceOutputAssembly").Equals("false", StringComparison.OrdinalIgnoreCase)))],
            PackageReferences = [.. instance.GetItems("PackageReference").Select(p => new PackageReferenceItem(p.EvaluatedInclude, p.GetMetadataValue("Version"))).Distinct()],
            ProjectCapabilities = [.. instance.GetItems("ProjectCapability").Select(c => c.EvaluatedInclude)],
            ContentFilePaths = [.. instance.GetItems("Content").Select(c => c.GetMetadataValue("FullPath"))],
            CodePage = int.TryParse(Property("CodePage"), out int page) && page > 0 ? page : 0,
            ChecksumAlgorithm = Property("ChecksumAlgorithm") ?? Property("PdbChecksumAlgorithm"),
            FileGlobs = globs,
        };
    }

    private static string[] Aliases(ProjectItemInstance item) =>
        item.GetMetadataValue("Aliases") is { Length: > 0 } aliases ? [.. aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [];

    /// <summary>A document as Roslyn's build host places it: where it is, where it shows (its Link, else its path in the
    /// project, else its name), and the project's folders it is under.</summary>
    private static DocumentFileInfo Document(ProjectItemInstance item, string directory)
    {
        string spec = item.EvaluatedInclude;
        string full = Path.GetFullPath(Path.Combine(directory, spec));
        string link = item.GetMetadataValue("Link");
        string logical = link.Length > 0 ? link
            : !Path.IsPathRooted(spec) ? spec
            : full.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ? full[directory.Length..]
            : Path.GetFileName(full);
        string? folder = link.Length > 0 ? Path.GetDirectoryName(link) : Path.GetDirectoryName(Path.GetRelativePath(directory, spec));
        string[] folders = folder is { Length: > 0 } ? folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries) : [];
        return new DocumentFileInfo(full, logical, link.Length > 0, false, folders);
    }

    private static string Full(string glob) => Path.GetFullPath(glob);

    /// <summary>The errors and warnings each build logged, by its submission, as Roslyn's build host's logger keeps them.</summary>
    private sealed class DiagnosticLogger : ILogger
    {
        private readonly ConcurrentDictionary<int, List<BuildDiagnostic>> bySubmission = new();

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;

        public string? Parameters { get; set; }

        public List<BuildDiagnostic> Register(int submission) => bySubmission.GetOrAdd(submission, _ => []);

        public IReadOnlyList<BuildDiagnostic> Take(int submission)
        {
            if (!bySubmission.TryRemove(submission, out List<BuildDiagnostic>? logged))
            {
                return [];
            }
            lock (logged)
            {
                return [.. logged];
            }
        }

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (_, e) => Add(e.BuildEventContext, new BuildDiagnostic(true, e.Message ?? string.Empty, e.ProjectFile ?? string.Empty));
            eventSource.WarningRaised += (_, e) => Add(e.BuildEventContext, new BuildDiagnostic(false, e.Message ?? string.Empty, e.ProjectFile ?? string.Empty));
        }

        private void Add(BuildEventContext? context, BuildDiagnostic diagnostic)
        {
            if (context is not null && bySubmission.TryGetValue(context.SubmissionId, out List<BuildDiagnostic>? logged))
            {
                lock (logged)
                {
                    logged.Add(diagnostic);
                }
            }
        }

        public void Shutdown()
        {
        }
    }
}

// Roslyn's ProjectFileInfo and what it holds, as its build host writes them (Microsoft.CodeAnalysis.Workspaces.MSBuild's
// contracts): the server's own loader reads these.

internal sealed record RoslynProjectFileInfo
{
    public bool IsEmpty { get; init; }

    public required string Language { get; init; }

    public string? FilePath { get; init; }

    public string? OutputFilePath { get; init; }

    public string? OutputRefFilePath { get; init; }

    public string? IntermediateOutputFilePath { get; init; }

    public string? GeneratedFilesOutputDirectory { get; init; }

    public string? DefaultNamespace { get; init; }

    public string? TargetFramework { get; init; }

    public string? TargetFrameworkIdentifier { get; init; }

    public required string[] CommandLineArgs { get; init; }

    public required DocumentFileInfo[] Documents { get; init; }

    public required DocumentFileInfo[] AdditionalDocuments { get; init; }

    public required DocumentFileInfo[] AnalyzerConfigDocuments { get; init; }

    public required ProjectFileReference[] ProjectReferences { get; init; }

    public required string[] ProjectCapabilities { get; init; }

    public required string[] ContentFilePaths { get; init; }

    public string? ProjectAssetsFilePath { get; init; }

    public required PackageReferenceItem[] PackageReferences { get; init; }

    public MetadataReferenceItem[] MetadataReferences { get; init; } = [];

    public int CodePage { get; init; }

    public string? ChecksumAlgorithm { get; init; }

    public string? TargetFrameworkVersion { get; init; }

    public required FileGlobs[] FileGlobs { get; init; }
}

internal sealed record DocumentFileInfo(string FilePath, string LogicalPath, bool IsLinked, bool IsGenerated, string[] Folders);

internal sealed record ProjectFileReference(string Path, string[] Aliases, bool ReferenceOutputAssembly);

internal sealed record PackageReferenceItem(string Name, string VersionRange);

internal sealed record MetadataReferenceItem(string Path, string[] Aliases);

internal sealed record FileGlobs(string[] Includes, string[] Excludes, string[] Removes);
