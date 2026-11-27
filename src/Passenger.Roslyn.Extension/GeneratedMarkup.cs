using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.Roslyn.Extension;

/// <summary>Markup a source generator makes a program's code of, an additional document of its project: a Razor file, and a
/// .NET MAUI XAML file, whose generator also writes each page's patch for MAUI's XAML Incremental Hot Reload. That patch
/// is made from the text the generator last made code of, which it keeps process-wide, so Passenger runs MAUI's generator
/// under one lock (<see cref="Generating"/>), a program's updates on their own record of it (<see cref="XamlPatchBaselines"/>).</summary>
internal static class GeneratedMarkup
{
    /// <summary>Held while Passenger runs MAUI's XAML generator, whose patches follow the text it ran on last.</summary>
    public static readonly SemaphoreSlim Generating = new(1, 1);

    /// <summary>Whether a path names markup whose generator's code is the program's.</summary>
    public static bool Is(string? path) => RazorFiles.Is(path) || IsXaml(path);

    private static bool IsXaml(string? path) => path?.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The solution with a MAUI app's XAML as the editor has it (<paramref name="markup"/>), every project of the
    /// program's (<paramref name="scope"/>, every one when null) that holds XAML generated afresh through the program's own
    /// record of what its pages show (<paramref name="baselines"/>), first over the texts the app holds, so each page's patch
    /// is from what its open pages show. With the generator's errors in the XAML, which keep the update from being made.</summary>
    public static async Task<(Solution Solution, IReadOnlyList<HotReloadProblem> Errors, IReadOnlyList<string> Remade)> RegeneratedAsync(Solution solution,
        IReadOnlyList<HotReloadMarkup> markup, IReadOnlySet<ProjectId>? scope, XamlPatchBaselines baselines, CancellationToken cancel)
    {
        List<ProjectId> projects = [.. solution.Projects.Where(p => (scope?.Contains(p.Id) != false) && p.AdditionalDocuments.Any(d => IsXaml(d.FilePath))).Select(p => p.Id)];
        if (projects.Count == 0)
        {
            return (solution, [], []);
        }
        Solution held = solution;
        Solution now = solution;
        foreach (HotReloadMarkup file in markup)
        {
            foreach (DocumentId id in SyntaxReads.DocumentIdsOf(solution, file.Path).Where(i => projects.Contains(i.ProjectId) && solution.GetAdditionalDocument(i) is not null))
            {
                held = held.WithAdditionalDocumentText(id, SourceText.From(file.Before, Encoding.UTF8));
                now = now.WithAdditionalDocumentText(id, SourceText.From(file.Text, Encoding.UTF8));
            }
        }
        // Run from scratch, so the generator reads every file again rather than the ones changed since its last run.
        held = Afresh(held, projects);
        now = Afresh(now, projects);
        HashSet<string> edited = new(markup.Where(m => m.Text != m.Before).Select(m => m.Path), PathNames.Comparer);
        HashSet<string> remade = new(PathNames.Comparer);
        List<HotReloadProblem> errors = await baselines.RunAsync([.. projects.Select(p => solution.GetProject(p)!)],
            async () =>
            {
                foreach (ProjectId project in projects)
                {
                    await held.GetProject(project)!.GetSourceGeneratedDocumentsAsync(cancel).ConfigureAwait(false);
                }
            },
            async () =>
            {
                List<HotReloadProblem> found = [];
                foreach (ProjectId id in projects)
                {
                    Project project = now.GetProject(id)!;
                    string folder = Path.GetDirectoryName(project.FilePath) ?? string.Empty;
                    foreach (Diagnostic diagnostic in await now.CompilationState.GetSourceGeneratorDiagnosticsAsync(project.State, cancel).ConfigureAwait(false))
                    {
                        FileLinePositionSpan place = diagnostic.Location.GetLineSpan();
                        if (diagnostic.Severity != DiagnosticSeverity.Error || diagnostic.IsSuppressed || !place.IsValid || place.Path.Length == 0
                            || Path.GetFullPath(Path.Combine(folder, place.Path)) is not { } file || !edited.Contains(file)
                            || found.Any(e => e.Id == diagnostic.Id && e.Path is { } known && PathNames.Same(known, file) && e.StartLine == place.StartLinePosition.Line))
                        {
                            continue;
                        }
                        found.Add(new HotReloadProblem(diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture), true, false, file,
                            place.StartLinePosition.Line, place.StartLinePosition.Character, place.EndLinePosition.Line, place.EndLinePosition.Character, project.Name));
                    }
                    remade.UnionWith(await UnpatchedAsync(project, edited, cancel).ConfigureAwait(false));
                }
                return found;
            }).ConfigureAwait(false);
        return (now, errors, [.. remade]);
    }

    /// <summary>The edited XAML files of a project whose open pages MAUI's patch does not bring to the text: its generator
    /// wrote no patch (a change it makes only whole, or of comments alone, which the app tells apart), or one that passes
    /// over part of the change ("skipped", as a binding's or a C# expression's).</summary>
    private static async Task<IEnumerable<string>> UnpatchedAsync(Project project, IReadOnlySet<string> edited, CancellationToken cancel)
    {
        List<string> unpatched = [];
        Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider options = project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
        IEnumerable<SourceGeneratedDocument> generated = await project.GetSourceGeneratedDocumentsAsync(cancel).ConfigureAwait(false);
        foreach (AdditionalText file in project.AnalyzerOptions.AdditionalFiles.Where(f => edited.Contains(f.Path)))
        {
            if (!options.GetOptions(file).TryGetValue("build_metadata.additionalfiles.RelativePath", out string? relative)
                || generated.FirstOrDefault(g => g.HintName == HintOf(relative, options.GetOptions(file), "uc.xsg")) is not { } patch
                || await patch.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
                || root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "UpdateComponent") is not { Body: { } body })
            {
                continue;
            }
            if (body.Statements.Count == 0
                || body.DescendantTrivia().Any(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineCommentTrivia) && t.ToString().Contains("skipped", StringComparison.OrdinalIgnoreCase)))
            {
                unpatched.Add(file.Path);
            }
        }
        return unpatched;
    }

    /// <summary>The name MAUI's generator gives what it writes of a XAML file (<paramref name="kind"/>: "xsg" its
    /// InitializeComponent, "uc.xsg" its patch), as its own GetHintName makes it.</summary>
    public static string HintOf(string relative, Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions options, string kind)
    {
        string prefix = (Path.GetDirectoryName(relative) ?? string.Empty).Replace(Path.DirectorySeparatorChar, '_').Replace(':', '_');
        string written = options.TryGetValue("build_metadata.additionalfiles.GenKind", out string? named) ? named.ToLowerInvariant() : "none";
        return $"{(prefix.Length > 0 ? prefix + "_" : string.Empty)}{Path.GetFileNameWithoutExtension(relative)}.{written}.{kind}.cs";
    }

    /// <summary>The solution with the projects' generators to be run again from scratch.</summary>
    private static Solution Afresh(Solution solution, IEnumerable<ProjectId> projects) =>
        solution.UpdateSpecificSourceGeneratorExecutionVersions(new SourceGeneratorExecutionVersionMap(
            projects.Aggregate(ImmutableSortedDictionary<ProjectId, SourceGeneratorExecutionVersion>.Empty,
                (versions, project) => versions.Add(project, solution.GetSourceGeneratorExecutionVersion(project).IncrementMajorVersion()))));
}
