using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>Every rule the solution's own analyzers and its C# projects' report whose severity can be set, each id once, by id
/// (<see cref="InspectionsFound"/>).</summary>
public sealed class Inspections : IExtensionWorkspaceMessageHandler<InspectionsAsked, InspectionsFound>
{
    public Task<InspectionsFound> ExecuteAsync(InspectionsAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        Solution solution = context.Solution;
        Dictionary<string, EditorConfigDiagnostic> rules = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnalyzerReference reference in solution.AnalyzerReferences.Concat(solution.Projects.Where(p => p.Language == LanguageNames.CSharp).SelectMany(p => p.AnalyzerReferences)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (EditorConfigDiagnostic rule in EditorConfigKeys.ReportedBy(reference))
            {
                rules.TryAdd(rule.Id, rule);
            }
        }
        return Task.FromResult(new InspectionsFound([.. rules.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)]));
    }
}

/// <summary>One rule run over every C# project whose analyzers report it, turned on as a warning whatever the projects'
/// options and .editorconfig set it to, a place a pragma suppresses left out (<see cref="InspectionFound"/>): each place it
/// reports in a file of the solution, once however many of a project's frameworks report it, with the severity it has there
/// as set.</summary>
public sealed class RunInspection : IExtensionWorkspaceMessageHandler<InspectionAsked, InspectionFound>
{
    public async Task<InspectionFound> ExecuteAsync(InspectionAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        Solution solution = context.Solution;
        ImmutableArray<DiagnosticAnalyzer> host = AnalyzersOf(solution.AnalyzerReferences, message.Id);
        List<InspectionPlace> places = [];
        HashSet<(string, int, int)> seen = [];
        int projects = 0;
        foreach (Project project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            ImmutableArray<DiagnosticAnalyzer> analyzers = host.AddRange(AnalyzersOf(project.AnalyzerReferences, message.Id));
            if (analyzers.IsEmpty || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }
            projects++;
            CompilationOptions set = compilation.Options;
            Compilation forced = compilation.WithOptions(set
                .WithSpecificDiagnosticOptions(set.SpecificDiagnosticOptions.SetItem(message.Id, ReportDiagnostic.Warn))
                .WithSyntaxTreeOptionsProvider(new Forced(set.SyntaxTreeOptionsProvider, message.Id)));
            CompilationWithAnalyzers analyzed = forced.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(project.AnalyzerOptions, onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false));
            foreach (Diagnostic diagnostic in await analyzed.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(diagnostic.Id, message.Id, StringComparison.OrdinalIgnoreCase) || diagnostic.Location is not { IsInSource: true, SourceTree: { FilePath: { Length: > 0 } path } tree }
                    || !seen.Add((path, diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)))
                {
                    continue;
                }
                LinePosition at = diagnostic.Location.GetLineSpan().StartLinePosition;
                places.Add(new InspectionPlace(path, new Place(at.Line, at.Character), diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                    SeverityOf(set, tree, diagnostic.Descriptor, cancellationToken), EditorConfigKeys.NameOf(project)));
            }
        }
        return new InspectionFound([.. places.OrderBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.At.Line).ThenBy(p => p.At.Character)], projects);
    }

    /// <summary>The analyzers of these references that report the rule, as their own lists say.</summary>
    private static ImmutableArray<DiagnosticAnalyzer> AnalyzersOf(IEnumerable<AnalyzerReference> references, string id)
    {
        ImmutableArray<DiagnosticAnalyzer>.Builder found = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (AnalyzerReference reference in references)
        {
            if (!EditorConfigKeys.ReportedBy(reference).Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            foreach (DiagnosticAnalyzer analyzer in reference.GetAnalyzers(LanguageNames.CSharp))
            {
                try
                {
                    if (analyzer.SupportedDiagnostics.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(analyzer);
                    }
                }
                catch (Exception failed) when (failed is not OperationCanceledException)
                {
                    // An analyzer whose list throws reports nothing the server runs either.
                }
            }
        }
        return found.ToImmutable();
    }

    /// <summary>The rule's severity in a file as the project sets it, as .editorconfig writes it: the file's own setting,
    /// else the project's, else the rule's default (silent where it is off by default).</summary>
    private static string SeverityOf(CompilationOptions set, SyntaxTree tree, DiagnosticDescriptor rule, CancellationToken cancel)
    {
        ReportDiagnostic severity = set.SyntaxTreeOptionsProvider is { } options && (options.TryGetDiagnosticValue(tree, rule.Id, cancel, out ReportDiagnostic own)
            || options.TryGetGlobalDiagnosticValue(rule.Id, cancel, out own)) ? own
            : set.SpecificDiagnosticOptions.TryGetValue(rule.Id, out ReportDiagnostic project) ? project
            : rule.IsEnabledByDefault ? DiagnosticSeverityExtensions.ToReportDiagnostic(rule.DefaultSeverity) : ReportDiagnostic.Suppress;
        return ReportDiagnosticExtensions.ToEditorConfigString(severity == ReportDiagnostic.Default ? DiagnosticSeverityExtensions.ToReportDiagnostic(rule.DefaultSeverity) : severity);
    }

    /// <summary>A project's tree options with one rule a warning in every file, the rest as they were.</summary>
    private sealed class Forced(SyntaxTreeOptionsProvider? set, string id) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) => set?.IsGenerated(tree, cancellationToken) ?? GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            if (string.Equals(diagnosticId, id, StringComparison.OrdinalIgnoreCase))
            {
                severity = ReportDiagnostic.Warn;
                return true;
            }
            severity = ReportDiagnostic.Default;
            return set?.TryGetDiagnosticValue(tree, diagnosticId, cancellationToken, out severity) == true;
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            if (string.Equals(diagnosticId, id, StringComparison.OrdinalIgnoreCase))
            {
                severity = ReportDiagnostic.Warn;
                return true;
            }
            severity = ReportDiagnostic.Default;
            return set?.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out severity) == true;
        }
    }
}
