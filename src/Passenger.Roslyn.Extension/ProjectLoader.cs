using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Composition;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.Extensions.Logging;

namespace Passenger.Roslyn.Extension;

// The server's internal types, reached through the publicized assemblies the extension is compiled against.

/// <summary>Projects loaded from the design-time builds Passenger gives (<see cref="Load"/>) or, for a project it names
/// without them or one the server finds changed, asks it for (<see cref="ProjectMethods.Info"/>).</summary>
internal sealed class PassengerProjectLoader : LanguageServerProjectLoader, ILspService
{
    /// <summary>Each project's design-time builds as given, read as its load takes them, so a batch's reading is spread over
    /// the server's parallel loads rather than done one after another in the notification.</summary>
    private readonly ConcurrentDictionary<string, (string Infos, IReadOnlyList<ProjectDiagnostic>? Logged)> given = new(StringComparer.Ordinal);
    private readonly IClientLanguageServerManager client;
    private readonly LspServices services;

    public PassengerProjectLoader(LspServices services, IGlobalOptionService options, ILoggerFactory loggers, IAsynchronousOperationListenerProvider listeners,
        ServerConfigurationFactory configuration, IBinLogPathProvider binaryLogs, DotnetCliHelper dotnet)
        : base(services, options, loggers, listeners, configuration, binaryLogs, dotnet)
    {
        client = services.GetRequiredService<IClientLanguageServerManager>();
        this.services = services;
    }

    /// <summary>Loads projects, again for those loaded already, and says the load is complete once each has loaded.</summary>
    public async Task Load(LoadProjectsParams request, IProgress<global::Roslyn.LanguageServer.Protocol.WorkDoneProgress> progress)
    {
        if (request.Solution is { } solution)
        {
            _workspaceFactory.HostProjectFactory.SolutionPath = solution;
        }
        await using WorkDoneProgressTracker? tracker = request.Projects.Count > 0 ? new WorkDoneProgressTracker(progress, request.Projects.Count) : null;
        foreach (ProjectToOpen project in request.Projects)
        {
            if (project.Infos is { } infos)
            {
                given[project.Path] = (infos, project.Diagnostics);
            }
            // A program found in the folder as the server started, before it was told not to look, is this loader's alone.
            if (project.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && services.GetService<ILspMiscellaneousFilesWorkspaceProvider>() is Microsoft.CodeAnalysis.LanguageServer.FileBasedPrograms.FileBasedProgramsProjectSystem own)
            {
                await own.TryUnloadProjectAsync(project.Path).ConfigureAwait(false);
            }
            await BeginLoadingProjectAsync(project.Path, null, tracker).ConfigureAwait(false);
            // One loaded already loads again with what it was given; one just begun is the same work, done once.
            _projectsToReload.AddWork(new Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.ProjectToLoad(project.Path, null, ReportTelemetry: false));
        }
        await WaitForProjectsToFinishLoadingAsync().ConfigureAwait(false);
        await ProjectInitializationHandler.SendProjectInitializationCompleteNotificationAsync(client).ConfigureAwait(false);
    }

    /// <summary>Loads again the projects that hold any of the files as documents, which lets go of those gone from disk, and
    /// says how many once they have.</summary>
    public async Task<int> LetGoAsync(IReadOnlyList<string> paths)
    {
        Solution solution = _workspaceFactory.HostWorkspace.CurrentSolution;
        HashSet<string> holding = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            foreach (DocumentId document in solution.GetDocumentIdsWithFilePath(path))
            {
                if (solution.GetProject(document.ProjectId)?.FilePath is { } project)
                {
                    holding.Add(project);
                }
            }
        }
        if (holding.Count == 0)
        {
            return 0;
        }
        foreach (string project in holding)
        {
            _projectsToReload.AddWork(new Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.ProjectToLoad(project, null, ReportTelemetry: false));
        }
        await WaitForProjectsToFinishLoadingAsync().ConfigureAwait(false);
        return holding.Count;
    }

    /// <summary>Says in the server's log what a deletion's projects did before the server was told of it.</summary>
    public void LetGo(int projects, int files, TimeSpan took) =>
        _logger.LogInformation("{Projects} project(s) holding {Files} file(s) gone from disk loaded again without them in {Seconds:0.00} s, then told they are gone", projects, files, took.TotalSeconds);

    /// <summary>What a load restores: the projects of the solution open restored with the solution, whose restore leaves
    /// its MSBuild nodes with the server, and the rest on their own.</summary>
    public override async ValueTask<ImmutableArray<string>> GetPathsToRestoreAsync(ImmutableArray<string> projectsThatNeedRestore, CancellationToken cancellationToken)
    {
        if (_workspaceFactory.HostProjectFactory.SolutionPath is not { } solution)
        {
            return projectsThatNeedRestore;
        }
        ImmutableHashSet<string> inSolution;
        try
        {
            inSolution = (await SolutionFileReader.ReadSolutionFileAsync(solution, DiagnosticReportingMode.Throw, cancellationToken).ConfigureAwait(false)).Item2
                .Select(p => p.ProjectPath).ToImmutableHashSet(global::Roslyn.Utilities.PathUtilities.Comparer);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "Unable to read solution '{SolutionPath}' to determine restore scope; restoring {ProjectCount} project(s) individually.", solution, projectsThatNeedRestore.Length);
            return projectsThatNeedRestore;
        }
        ImmutableArray<string> outside = [.. projectsThatNeedRestore.Where(p => !inSolution.Contains(p))];
        return outside.Length == projectsThatNeedRestore.Length ? projectsThatNeedRestore : [solution, .. outside];
    }

    // Public as the publicized base declares it; the runtime takes a public override of a protected method.
    public override async Task<RemoteProjectLoadResult?> TryLoadProjectInMSBuildHostAsync(BuildHostProcessManager buildHostProcessManager, string projectPath, CancellationToken cancellationToken)
    {
        if (given.TryRemove(projectPath, out (string Infos, IReadOnlyList<ProjectDiagnostic>? Logged) held))
        {
            return Result(projectPath, Read(held.Infos), Logged(held.Logged));
        }
        ProjectInfoResult answer = await client.SendRequestAsync<ProjectInfoParams, ProjectInfoResult>(ProjectMethods.Info, new ProjectInfoParams(projectPath), cancellationToken).ConfigureAwait(false);
        if (answer.Infos is null)
        {
            return Result(projectPath, [], [new DiagnosticLogItem(DiagnosticLogItemKind.Error, answer.Problem ?? "Passenger's project host gave no design-time build", projectPath)]);
        }
        return Result(projectPath, Read(answer.Infos), Logged(answer.Diagnostics));
    }

    /// <summary>What the builds logged, as the server's log items.</summary>
    private static DiagnosticLogItem[] Logged(IReadOnlyList<ProjectDiagnostic>? logged) =>
        [.. (logged ?? []).Select(d => new DiagnosticLogItem(d.Error ? DiagnosticLogItemKind.Error : DiagnosticLogItemKind.Warning, d.Message, d.Project))];

    private RemoteProjectLoadResult Result(string projectPath, ProjectFileInfo[] infos, DiagnosticLogItem[] problems) => new()
    {
        ProjectFileInfos = ImmutableArray.Create(infos),
        DiagnosticLogItems = ImmutableArray.Create(problems),
        ProjectRestorePath = projectPath,
        ProjectFactory = _workspaceFactory.HostProjectFactory,
        // A file-based program's project is named by its C# file.
        IsFileBasedProgram = projectPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
        IsMiscellaneousFile = false,
        HasFileBasedAppDirectives = projectPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
        HasAllInformation = true,
        PreferredBuildHostKind = BuildHostProcessKind.NetCore,
        ActualBuildHostKind = BuildHostProcessKind.NetCore,
    };

    /// <summary>Design-time builds read from the JSON Passenger's host writes (Roslyn's ProjectFileInfo).</summary>
    private static ProjectFileInfo[] Read(string json) => JsonSerializer.Deserialize<ProjectFileInfo[]>(json, JsonSettings.SingleLineSerializerOptions) ?? [];
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(PassengerProjectLoader), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class PassengerProjectLoaderFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;
    private readonly IAsynchronousOperationListenerProvider listeners;
    private readonly ServerConfigurationFactory configuration;

    [ImportingConstructor]
    public PassengerProjectLoaderFactory(IGlobalOptionService options, IAsynchronousOperationListenerProvider listeners, ServerConfigurationFactory configuration)
    {
        this.options = options;
        this.listeners = listeners;
        this.configuration = configuration;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) =>
        new PassengerProjectLoader(lspServices, options, lspServices.GetRequiredService<ILoggerFactory>(), listeners, configuration,
            lspServices.GetRequiredService<IBinLogPathProvider>(), lspServices.GetRequiredService<DotnetCliHelper>());
}

/// <summary><see cref="ProjectMethods.Load"/>: loads the projects named, through <see cref="PassengerProjectLoader"/>, its
/// progress reported to the client.</summary>
[Method(ProjectMethods.Load)]
internal sealed class LoadProjectsHandler(PassengerProjectLoader loader, WorkDoneProgressManager progress) : ILspServiceNotificationHandler<LoadProjectsParams>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => false;

    public async Task HandleNotificationAsync(LoadProjectsParams request, RequestContext requestContext, CancellationToken cancellationToken)
    {
        int count = request.Projects.Count;
        string loading = string.Format(System.Globalization.CultureInfo.CurrentCulture, LanguageServerResources.Loading_0_projects, count);
        await using IWorkDoneProgressReporter reporter = await progress.CreateWorkDoneProgressAsync(reportProgressToClient: true, loading, loading,
            string.Format(System.Globalization.CultureInfo.CurrentCulture, LanguageServerResources.Loaded_0_projects, count), clientCanCancel: false, cancellationToken).ConfigureAwait(false);
        await loader.Load(request, reporter).ConfigureAwait(false);
    }
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(LoadProjectsHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class LoadProjectsHandlerFactory : ILspServiceFactory
{
    [ImportingConstructor]
    public LoadProjectsHandlerFactory()
    {
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) =>
        new LoadProjectsHandler(lspServices.GetRequiredService<PassengerProjectLoader>(), lspServices.GetRequiredService<WorkDoneProgressManager>());
}

/// <summary><see cref="ProjectMethods.Deleted"/>: files gone from disk taken as workspace/didChangeWatchedFiles once the
/// projects holding them have loaded again without them (<see cref="PassengerProjectLoader.LetGoAsync"/>).</summary>
[Method(ProjectMethods.Deleted)]
internal sealed class FilesDeletedHandler(PassengerProjectLoader loader, LspDidChangeWatchedFilesHandler watched)
    : ILspServiceNotificationHandler<global::Roslyn.LanguageServer.Protocol.DidChangeWatchedFilesParams>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => false;

    public async Task HandleNotificationAsync(global::Roslyn.LanguageServer.Protocol.DidChangeWatchedFilesParams request, RequestContext requestContext, CancellationToken cancellationToken)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            int projects = await loader.LetGoAsync([.. request.Changes.Select(change => change.Uri.GetRequiredParsedUri().LocalPath)]).ConfigureAwait(false);
            if (projects > 0)
            {
                loader.LetGo(projects, request.Changes.Length, System.Diagnostics.Stopwatch.GetElapsedTime(began));
            }
        }
        finally
        {
            // Taken as gone whatever the loads came to.
            await ((Microsoft.CommonLanguageServerProtocol.Framework.INotificationHandler<global::Roslyn.LanguageServer.Protocol.DidChangeWatchedFilesParams, RequestContext>)watched)
                .HandleNotificationAsync(request, requestContext, cancellationToken).ConfigureAwait(false);
        }
    }
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(FilesDeletedHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class FilesDeletedHandlerFactory : ILspServiceFactory
{
    [ImportingConstructor]
    public FilesDeletedHandlerFactory()
    {
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) =>
        new FilesDeletedHandler(lspServices.GetRequiredService<PassengerProjectLoader>(), lspServices.GetRequiredService<LspDidChangeWatchedFilesHandler>());
}

/// <summary><see cref="ProjectMethods.Ready"/>: true, from a server that has Passenger's loader.</summary>
[Method(ProjectMethods.Ready)]
internal sealed class LoaderReadyHandler : ILspServiceRequestHandler<LoaderReadyParams, bool>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => false;

    public Task<bool> HandleRequestAsync(LoaderReadyParams request, RequestContext context, CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>What <see cref="ProjectMethods.Ready"/> is asked with: nothing.</summary>
internal sealed record LoaderReadyParams;

[ExportCSharpVisualBasicLspServiceFactory(typeof(LoaderReadyHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class LoaderReadyHandlerFactory : ILspServiceFactory
{
    [ImportingConstructor]
    public LoaderReadyHandlerFactory()
    {
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new LoaderReadyHandler();
}
