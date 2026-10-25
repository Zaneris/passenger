using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>Design-time builds for Roslyn's server: each project (each target framework of a multi-targeted one) built to
/// its compiler's command line without compiling, with its documents, additional files, .editorconfig files and outputs,
/// written as the ProjectFileInfo the server's project loader reads. The builds of one request share one build session
/// on MSBuild's nodes (half the processors), one session at a time.</summary>
internal static class DesignTimeBuilds
{
    /// <summary>The global properties of a design-time build that runs the compiler's task for its command line, alone.</summary>
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DesignTimeBuild"] = "true",
        ["NonExistentFile"] = "__NonExistentSubDir__\\__NonExistentFile__",
        ["BuildProjectReferences"] = "false",
        ["BuildingProject"] = "false",
        ["ProvideCommandLineArgs"] = "true",
        ["SkipCompilerExecution"] = "true",
        ["ContinueOnError"] = "ErrorAndContinue",
        ["ShouldUnsetParentConfigurationAndPlatform"] = "false",
        // Else Compile's dependencies make (and on macOS sign) obj/…/apphost, which a build beside this one makes too.
        ["UseAppHost"] = "false",
    };

    /// <summary>ProjectFileInfo as the server's loader reads it: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions Written = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One build session at a time: MSBuild's build manager is the process's.</summary>
    private static readonly SemaphoreSlim Session = new(1, 1);

    /// <summary>The design-time builds that generate XAML's output in obj/ and the IDE's own builds holding them off
    /// (<see cref="HoldAsync"/>) take turns: both write WPF's obj/…/App.g.cs and Avalonia's obj/…/Avalonia/resources,
    /// which Windows lets one writer open at a time, and would rewrite them under the other's compiler. Other design-time
    /// builds write nothing a build reads, and never wait.</summary>
    private static readonly object Turns = new();

    /// <summary>The targets a design-time build runs that write what the project's build then reads: WPF's markup
    /// compilation, and Avalonia's resources (before CoreCompile, so in a design-time build too, unless up to date).</summary>
    private static readonly string[] Generating = ["DesignTimeMarkupCompilation", "GenerateAvaloniaResources"];

    private static readonly List<string> Running = [];
    private static int holds;
    private static TaskCompletionSource unheld = Completed();
    private static TaskCompletionSource idle = Completed();

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
                    async (path, _) => built(await Build(path, collection, context, global, diagnostics, cancel).ConfigureAwait(false))).ConfigureAwait(false);
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

    /// <summary>Holds XAML-generating design-time builds off for a build of the IDE's own until <paramref name="release"/>
    /// is cancelled: told first of those running, if any, then that they are held once none runs.</summary>
    public static async Task HoldAsync(Action<HoldState> told, CancellationToken release)
    {
        string[] running;
        Task drained;
        lock (Turns)
        {
            if (holds++ == 0)
            {
                unheld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (Running.Count > 0 && idle.Task.IsCompleted)
            {
                idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            running = [.. Running];
            drained = running.Length == 0 ? Task.CompletedTask : idle.Task;
        }
        try
        {
            if (running.Length > 0)
            {
                told(new HoldState(running, false));
            }
            await drained.WaitAsync(release).ConfigureAwait(false);
            told(new HoldState([], true));
            await Task.Delay(Timeout.Infinite, release).ConfigureAwait(false);
        }
        finally
        {
            lock (Turns)
            {
                if (--holds == 0)
                {
                    unheld.TrySetResult();
                }
            }
        }
    }

    /// <summary>A XAML-generating design-time build's turn, begun once no build of the IDE's holds them off; disposed as
    /// it ends.</summary>
    private static async Task<IDisposable> TurnAsync(string path, CancellationToken cancel)
    {
        while (true)
        {
            Task heldOff;
            lock (Turns)
            {
                if (holds == 0)
                {
                    Running.Add(path);
                    return new Turn(path);
                }
                heldOff = unheld.Task;
            }
            await heldOff.WaitAsync(cancel).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource Completed()
    {
        TaskCompletionSource completed = new();
        completed.SetResult();
        return completed;
    }

    private sealed class Turn(string path) : IDisposable
    {
        public void Dispose()
        {
            lock (Turns)
            {
                Running.Remove(path);
                if (Running.Count == 0)
                {
                    idle.TrySetResult();
                }
            }
        }
    }

    private static async Task<BuiltProject> Build(string path, ProjectCollection collection, EvaluationContext context, Dictionary<string, string> global, DiagnosticLogger diagnostics,
        CancellationToken cancel)
    {
        Stamp file = Stamps.File(path);
        IReadOnlyList<Stamp> folders = Evaluation.Walked(Path.GetDirectoryName(path)!);
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
            Project outer = Evaluation.Load(path, collection, context, global, null);
            loaded.Add(outer);
            // As the evaluation found them, not as the builds leave obj/.
            List<Stamp> looked = [.. Evaluation.LookedFor(path, outer)];
            string single = outer.GetPropertyValue("TargetFramework");
            string[] frameworks = outer.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<Project> each = single.Length > 0 || frameworks.Length == 0 ? [outer] : [.. frameworks.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => Evaluation.Load(path, collection, context, global, f))];
            loaded.AddRange(each.Where(p => p != outer));
            FileGlobs[] globs = [.. outer.GetAllGlobs().Select(g => new FileGlobs([.. g.IncludeGlobs.Select(Full)], [.. g.Excludes.Select(Full)], [.. g.Removes.Select(Full)]))];
            List<RoslynProjectFileInfo> infos = [];
            List<BuildDiagnostic> said = [];
            // A restore arriving during a build must invalidate it, rather than look like an input it read.
            IReadOnlyList<Stamp> restored = [.. each
                .Select(p => p.GetPropertyValue("ProjectAssetsFile") is { Length: > 0 } asset ? Path.GetFullPath(asset, p.DirectoryPath) : null)
                .OfType<string>()
                .Distinct(PathNames.Comparer)
                .Select(Stamps.File)];
            foreach (Project project in each)
            {
                (ProjectInstance instance, IReadOnlyList<BuildDiagnostic> logged) = await BuildAsync(project, diagnostics, cancel).ConfigureAwait(false);
                said.AddRange(logged.Where(d => !said.Contains(d)));
                RoslynProjectFileInfo info = Read(language, path, instance, globs);
                if (info.CommandLineArgs.Length == 0 && !logged.Any(d => d.Error))
                {
                    return new BuiltProject(path, "[]", [file], $"{Path.GetFileName(path)}{(info.TargetFramework is { } framework ? $" ({framework})" : string.Empty)}: its design-time build gave no compiler command line");
                }
                infos.Add(info);
            }
            IReadOnlyList<Stamp> inputs = Evaluation.Inputs(file, loaded, Evaluation.OutsideRoots(outer), [.. restored, .. looked]);
            IReadOnlyList<Stamp> walked = Evaluation.Folders(path, folders, infos.SelectMany(i => i.Documents.Concat(i.AdditionalDocuments)).Select(d => d.FilePath));
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

    /// <summary>Compile and CoreCompile, and XAML's markup compilation where a project has it, with the errors and
    /// warnings the build logged. One that generates XAML's output (<see cref="Generating"/>) takes its turn with the
    /// IDE's own builds (<see cref="HoldAsync"/>).</summary>
    private static async Task<(ProjectInstance Built, IReadOnlyList<BuildDiagnostic> Logged)> BuildAsync(Project project, DiagnosticLogger diagnostics, CancellationToken cancel)
    {
        ProjectInstance instance = project.CreateProjectInstance();
        if (!instance.Targets.ContainsKey("Compile") || !instance.Targets.ContainsKey("CoreCompile"))
        {
            return (instance, []);
        }
        List<string> targets = ["Compile", "CoreCompile"];
        bool markup = instance.Targets.ContainsKey("DesignTimeMarkupCompilation");
        if (markup)
        {
            targets.Add("DesignTimeMarkupCompilation");
        }
        using IDisposable? turn = Generating.Any(instance.Targets.ContainsKey) ? await TurnAsync(project.FullPath, cancel).ConfigureAwait(false) : null;
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

    /// <summary>A built project read into the ProjectFileInfo the server takes.</summary>
    private static RoslynProjectFileInfo Read(string language, string path, ProjectInstance instance, FileGlobs[] globs)
    {
        string directory = Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar;
        string? Absolute(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Path.Combine(directory, value));
        string? Property(string name) => instance.GetProperty(name)?.EvaluatedValue is { Length: > 0 } value ? value : null;
        string[] arguments = [.. instance.GetItems(language == "C#" ? "CscCommandLineArgs" : "VbcCommandLineArgs").Select(i => i.EvaluatedInclude)];
        return new RoslynProjectFileInfo
        {
            Language = language,
            FilePath = path,
            OutputFilePath = Absolute(Property("TargetPath")),
            OutputRefFilePath = Absolute(Property("TargetRefPath")),
            GeneratedFilesOutputDirectory = Absolute(Property("CompilerGeneratedFilesOutputPath")),
            IntermediateOutputFilePath = Absolute(instance.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude),
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

    /// <summary>A document's place: where it is, where it shows (its Link, else its path in the project, else its name),
    /// and the project's folders it is under.</summary>
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

    /// <summary>The errors and warnings each build logged, by its submission.</summary>
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

// Roslyn's ProjectFileInfo and what it holds (Microsoft.CodeAnalysis.Workspaces.MSBuild's contracts), which the server's
// loader reads.

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
