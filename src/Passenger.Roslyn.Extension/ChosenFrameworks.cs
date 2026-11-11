using System.Collections.Immutable;
using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.DiagnosticSources;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Passenger.Core;
using Roslyn.LanguageServer.Protocol;
using LspDiagnostic = Roslyn.LanguageServer.Protocol.Diagnostic;
using WorkspacePartialReport = Roslyn.LanguageServer.Protocol.SumType<Roslyn.LanguageServer.Protocol.WorkspaceDiagnosticReport, Roslyn.LanguageServer.Protocol.WorkspaceDiagnosticReportPartialResult>;

namespace Passenger.Roslyn.Extension;

/// <summary><see cref="ExtensionMethods.WorkspaceDiagnostic"/>: workspace/diagnostic, streamed and held open until a change,
/// with solution-wide analysis taking each multi-targeted project the client names in the one framework chosen for it, its
/// other frameworks left unanalysed. Every report names its project and a previous result is read in the project it names,
/// so a file several projects compile keeps a report for each, and a framework no longer chosen has its reports said to be
/// gone.</summary>
[Method(ExtensionMethods.WorkspaceDiagnostic)]
internal sealed class ChosenFrameworksHandler(LspWorkspaceManager workspaces, LspWorkspaceRegistrationService registrations, IDiagnosticSourceManager sources,
    IDiagnosticsRefresher refresher, IGlobalOptionService options)
    : AbstractWorkspacePullDiagnosticsHandler<ChosenFrameworksParams, WorkspacePartialReport, WorkspaceDiagnosticReport?>(workspaces, registrations, sources, refresher, options),
    ILspServiceRequestHandler<ChosenFrameworksParams, WorkspaceDiagnosticReport?>
{
    /// <summary>The pull, a previous result whose project no longer compiles its document (a framework no longer targeted, a
    /// project loaded anew) said to be gone first and passed over: read in the document of another project, it would never
    /// be said to be gone.</summary>
    public new async Task<WorkspaceDiagnosticReport?> HandleRequestAsync(ChosenFrameworksParams request, RequestContext context, CancellationToken cancellationToken)
    {
        if (request.PartialResultToken is { } progress)
        {
            Solution solution = await context.GetRequiredSolutionAsync(cancellationToken).ConfigureAwait(false);
            List<PreviousResultId> held = [];
            foreach (PreviousResultId previous in request.PreviousResultIds)
            {
                if (previous.ProjectContext is { } named && !await HoldsAsync(solution, previous.Uri, named, cancellationToken).ConfigureAwait(false))
                {
                    progress.Report(Report(new VSTextDocumentIdentifier { DocumentUri = previous.Uri, ProjectContext = named }, [], null));
                    continue;
                }
                held.Add(previous);
            }
            request.PreviousResultIds = [.. held];
        }
        return await base.HandleRequestAsync(request, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the server reads a document in the project a previous result names, as it finds the document of one:
    /// that project has the document, or is the project file.</summary>
    private static async Task<bool> HoldsAsync(Solution solution, DocumentUri uri, VSProjectContext named, CancellationToken cancel)
    {
        VSTextDocumentIdentifier identifier = new() { DocumentUri = uri, ProjectContext = named };
        ProjectId project = ProtocolConversions.ProjectContextToProjectId(named);
        return (await solution.GetTextDocumentAsync(identifier, cancel).ConfigureAwait(false))?.Project.Id == project || solution.GetProject(identifier)?.Id == project;
    }

    public override string? GetRequestDiagnosticCategory(ChosenFrameworksParams diagnosticsParams) => diagnosticsParams.Identifier;

    public override ImmutableArray<PreviousPullResult>? GetPreviousResults(ChosenFrameworksParams diagnosticsParams) =>
        diagnosticsParams.PreviousResultIds.Select(id => new PreviousPullResult(id.Value, new VSTextDocumentIdentifier { DocumentUri = id.Uri, ProjectContext = id.ProjectContext }))
            .ToImmutableArray();

    public override async ValueTask<ImmutableArray<IDiagnosticSource>> GetOrderedDiagnosticSourcesAsync(ChosenFrameworksParams diagnosticsParams, string? requestDiagnosticCategory,
        RequestContext context, CancellationToken cancellationToken) =>
        requestDiagnosticCategory == PullDiagnosticCategories.WorkspaceDocumentsAndProject
            ? await AnalysedAsync(context, diagnosticsParams.Frameworks, cancellationToken).ConfigureAwait(false)
            : await base.GetOrderedDiagnosticSourcesAsync(diagnosticsParams, requestDiagnosticCategory, context, cancellationToken).ConfigureAwait(false);

    /// <summary>The sources of Roslyn's own provider of solution-wide analysis (WorkspaceDocumentsAndProjectDiagnosticSourceProvider),
    /// in its order, over every project but the frameworks not chosen.</summary>
    private async Task<ImmutableArray<IDiagnosticSource>> AnalysedAsync(RequestContext context, Dictionary<string, string> frameworks, CancellationToken cancel)
    {
        Solution solution = await context.GetRequiredSolutionAsync(cancel).ConfigureAwait(false);
        ICodeAnalysisDiagnosticAnalyzerService codeAnalysis = solution.Services.GetRequiredService<ICodeAnalysisDiagnosticAnalyzerService>();
        HashSet<ProjectId> unchosen = Unchosen(solution, frameworks);
        ImmutableArray<IDiagnosticSource>.Builder found = ImmutableArray.CreateBuilder<IDiagnosticSource>();
        foreach (Project project in WorkspaceDiagnosticSourceHelpers.GetProjectsInPriorityOrder(solution, context.SupportedLanguages))
        {
            bool full = GlobalOptions.IsFullSolutionAnalysisEnabled(project.Language, out bool compiler, out bool analyzers);
            if (unchosen.Contains(project.Id) || (!full && !codeAnalysis.HasProjectBeenAnalyzed(project.Id)))
            {
                continue;
            }
            AnalyzerFilter filter = (compiler ? AnalyzerFilter.CompilerAnalyzer : 0) | (analyzers ? AnalyzerFilter.NonCompilerAnalyzer : 0);
            IEnumerable<TextDocument> documents = project.Documents.Concat<TextDocument>(project.AdditionalDocuments)
                .Concat(await project.GetSourceGeneratedDocumentsAsync(cancel).ConfigureAwait(false));
            foreach (TextDocument document in documents.Where(d => !WorkspaceDiagnosticSourceHelpers.ShouldSkipDocument(context, d)))
            {
                found.Add(full ? AbstractWorkspaceDocumentDiagnosticSource.CreateForFullSolutionAnalysisDiagnostics(document, filter)
                    : AbstractWorkspaceDocumentDiagnosticSource.CreateForCodeAnalysisDiagnostics(document, codeAnalysis));
            }
            found.Add(full ? AbstractProjectDiagnosticSource.CreateForFullSolutionAnalysisDiagnostics(project, filter)
                : AbstractProjectDiagnosticSource.CreateForCodeAnalysisDiagnostics(project, codeAnalysis));
        }
        return found.ToImmutable();
    }

    /// <summary>The projects left unanalysed: of each project file the client chose a framework for, the server's projects of
    /// its other frameworks, where it has the one chosen (one it lacks, its restore failed say, leaves all of them in).</summary>
    private static HashSet<ProjectId> Unchosen(Solution solution, IReadOnlyDictionary<string, string> frameworks)
    {
        Dictionary<string, string> chosen = new(PathNames.Comparer);
        foreach ((string path, string framework) in frameworks)
        {
            chosen[path] = framework;
        }
        HashSet<ProjectId> unchosen = [];
        foreach (IGrouping<string, Project> same in solution.Projects.Where(p => p.FilePath is { } path && chosen.ContainsKey(path)).GroupBy(p => p.FilePath!, PathNames.Comparer))
        {
            string framework = chosen[same.Key];
            if (same.Any(p => Targets(p, framework)))
            {
                unchosen.UnionWith(same.Where(p => !Targets(p, framework)).Select(p => p.Id));
            }
        }
        return unchosen;
    }

    /// <summary>Whether a project is a multi-targeted project's build for a framework, as the server names it ("Lib (net10.0)").</summary>
    private static bool Targets(Project project, string framework) =>
        string.Equals(project.State.NameAndFlavor.flavor, framework, StringComparison.OrdinalIgnoreCase);

    public override WorkspacePartialReport CreateReport(TextDocumentIdentifier identifier, LspDiagnostic[] diagnostics, string resultId, ClientCapabilities clientCapabilities) =>
        Report(identifier, diagnostics, resultId);

    public override WorkspacePartialReport CreateRemovedReport(TextDocumentIdentifier identifier, ClientCapabilities clientCapabilities) => Report(identifier, [], null);

    /// <summary>One document's report, named by its project; a removed one has no result.</summary>
    private static WorkspacePartialReport Report(TextDocumentIdentifier identifier, LspDiagnostic[] diagnostics, string? resultId) => new(new WorkspaceDiagnosticReport
    {
        Items =
        [
            new WorkspaceFullDocumentDiagnosticReport
            {
                Uri = identifier.DocumentUri,
                ProjectContext = (identifier as VSTextDocumentIdentifier)?.ProjectContext,
                Items = diagnostics,
                Version = null,
                ResultId = resultId,
            },
        ],
    });

    /// <summary>A document unchanged since its result is said by saying nothing of it: a solution's unchanged reports would
    /// cost more to send and read than they say.</summary>
    public override bool TryCreateUnchangedReport(TextDocumentIdentifier identifier, string resultId, out WorkspacePartialReport report)
    {
        report = default;
        return false;
    }

    public override WorkspaceDiagnosticReport? CreateReturn(BufferedProgress<WorkspacePartialReport> progress) => new()
    {
        Items = progress.GetValues() is { } values ? [.. values.SelectMany(report => report.Match(r => r.Items, partial => partial.Items))] : [],
    };

    public override TestAccessor GetTestAccessor() => new(this);
}

/// <summary>What <see cref="ExtensionMethods.WorkspaceDiagnostic"/> is asked with: workspace/diagnostic's parameters, and
/// the framework solution-wide analysis takes each multi-targeted project in, by its project file.</summary>
internal sealed class ChosenFrameworksParams : IPartialResultParams<WorkspacePartialReport>
{
    [JsonPropertyName(Methods.PartialResultTokenName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IProgress<WorkspacePartialReport>? PartialResultToken { get; set; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }

    [JsonPropertyName("previousResultIds")]
    public PreviousResultId[] PreviousResultIds { get; set; } = [];

    [JsonPropertyName("frameworks")]
    public Dictionary<string, string> Frameworks { get; set; } = [];
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(ChosenFrameworksHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class ChosenFrameworksHandlerFactory : ILspServiceFactory
{
    private readonly IDiagnosticSourceManager sources;
    private readonly IDiagnosticsRefresher refresher;
    private readonly IGlobalOptionService options;

    [ImportingConstructor]
    public ChosenFrameworksHandlerFactory(IDiagnosticSourceManager sources, IDiagnosticsRefresher refresher, IGlobalOptionService options)
    {
        this.sources = sources;
        this.refresher = refresher;
        this.options = options;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) =>
        new ChosenFrameworksHandler(lspServices.GetRequiredService<LspWorkspaceManager>(), lspServices.GetRequiredService<LspWorkspaceRegistrationService>(), sources, refresher, options);
}
