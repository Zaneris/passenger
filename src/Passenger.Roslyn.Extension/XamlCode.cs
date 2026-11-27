using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Languages.CSharp;

namespace Passenger.Roslyn.Extension;

/// <summary>Completion at a place in a XAML page's x:Code, as C# completes the page's class there.</summary>
public sealed class XamlCodeCompletion : IExtensionWorkspaceMessageHandler<XamlCodeAsked, MauiCompletions>
{
    public async Task<MauiCompletions> ExecuteAsync(XamlCodeAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        if (await XamlCodeDocument.MakeAsync(context.Solution, message.Path, message.Code, cancellationToken).ConfigureAwait(false) is not { } made
            || made.DocumentAt(message.At) is not int position || CompletionService.GetService(made.Document) is not { } service)
        {
            return new MauiCompletions(message.At, []);
        }
        CompletionList list = await service.GetCompletionsAsync(made.Document, position, cancellationToken: cancellationToken).ConfigureAwait(false);
        List<MauiCompletion> items = [];
        foreach (CompletionItem item in list.ItemsList)
        {
            string insert = item.TryGetProperty("InsertionText", out string? written) && written is { Length: > 0 } ? written : item.DisplayText;
            items.Add(new MauiCompletion(item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix, insert, item.Tags.FirstOrDefault() ?? string.Empty,
                item.InlineDescription is { Length: > 0 } inline ? inline : null, item.SortText));
        }
        return new MauiCompletions(made.WrittenAt(list.Span.Start) ?? message.At, items);
    }
}

/// <summary>The symbol a place in a XAML page's x:Code names, one the x:Code declares at its place in the XAML.</summary>
public sealed class XamlCodeSymbol : IExtensionWorkspaceMessageHandler<XamlCodeAsked, SymbolsFound>
{
    public async Task<SymbolsFound> ExecuteAsync(XamlCodeAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        if (await XamlCodeDocument.MakeAsync(context.Solution, message.Path, message.Code, cancellationToken).ConfigureAwait(false) is not { } made
            || made.DocumentAt(message.At) is not int position)
        {
            return new SymbolsFound([], 0);
        }
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(made.Document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is null && position > 0)
        {
            symbol = await SymbolFinder.FindSymbolAtPositionAsync(made.Document, position - 1, cancellationToken).ConfigureAwait(false);
        }
        if (symbol is null)
        {
            return new SymbolsFound([], 0);
        }
        SyntaxTree tree = (await made.Document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
        NamedSymbol named = SymbolNames.Named(made.Document.Project.Solution, symbol);
        // Declared in the x:Code itself: at its place in the XAML.
        if (symbol.Locations.FirstOrDefault(l => l.SourceTree == tree) is { } declared && made.WrittenAt(declared.SourceSpan.Start) is int written)
        {
            named = named with { Path = message.Path, At = SyntaxReads.PlaceOf(SourceText.From(message.Text).Lines.GetLinePosition(written)), Generated = null };
        }
        return new SymbolsFound([named], 0);
    }
}

/// <summary>The problems C# finds in a XAML page's x:Code, at their places in the XAML.</summary>
public sealed class XamlCodeChecks : IExtensionWorkspaceMessageHandler<XamlCodeChecked, XamlCodeChecksFound>
{
    /// <summary>A member declared twice: the copy of the x:Code a source generator also made, which no edit of the
    /// solution can take out.</summary>
    private static readonly HashSet<string> Twice = ["CS0101", "CS0102", "CS0111"];

    public async Task<XamlCodeChecksFound> ExecuteAsync(XamlCodeChecked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        if (await XamlCodeDocument.MakeAsync(context.Solution, message.Path, message.Code, cancellationToken).ConfigureAwait(false) is not { } made
            || await made.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return new XamlCodeChecksFound([]);
        }
        List<XamlCodeCheck> checks = [];
        foreach (Diagnostic diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning) || diagnostic.IsSuppressed || Twice.Contains(diagnostic.Id)
                || made.WrittenAt(diagnostic.Location.SourceSpan.Start) is not int start)
            {
                continue;
            }
            int end = made.WrittenAt(diagnostic.Location.SourceSpan.End) ?? start;
            checks.Add(new XamlCodeCheck(start, Math.Max(end, start), diagnostic.Severity == DiagnosticSeverity.Error, diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture)));
        }
        return new XamlCodeChecksFound(checks);
    }
}

/// <summary>A XAML page's x:Code written as its compiler puts it in the page's class: a part of the class added to its
/// project, each block's code in it, the usings a block begins with written at the file's top, and the copy of the blocks
/// a build put in the project's own files (WPF's markup compiler's, mapped to the XAML by its #line) taken out, so what
/// the server answers there is what the compiler sees, every place one of the XAML's.</summary>
internal sealed class XamlCodeDocument
{
    private readonly (int Start, XamlCodeBlock Block)[] placed;

    private XamlCodeDocument(Document document, (int, XamlCodeBlock)[] placed)
    {
        Document = document;
        this.placed = placed;
    }

    public Document Document { get; }

    /// <summary>Where in the document an offset of the XAML is; null outside every block.</summary>
    public int? DocumentAt(int written)
    {
        foreach ((int start, XamlCodeBlock block) in placed)
        {
            if (written >= block.WrittenOf[0] && written <= block.WrittenOf[^1])
            {
                int at = 0;
                while (at < block.WrittenOf.Count - 1 && block.WrittenOf[at] < written)
                {
                    at++;
                }
                return start + at;
            }
        }
        return null;
    }

    /// <summary>The offset of the XAML a place of the document is written at; null outside every block.</summary>
    public int? WrittenAt(int position)
    {
        foreach ((int start, XamlCodeBlock block) in placed)
        {
            if (position >= start && position <= start + block.Code.Length)
            {
                return block.WrittenOf[position - start];
            }
        }
        return null;
    }

    public static async Task<XamlCodeDocument?> MakeAsync(Solution solution, string path, XamlCode code, CancellationToken cancel)
    {
        if (code.Blocks.Count == 0 || await ProjectAsync(solution, path, code.Class, cancel).ConfigureAwait(false) is not (Project project, INamedTypeSymbol page))
        {
            return null;
        }
        solution = await WithoutCopiesAsync(project, path, cancel).ConfigureAwait(false);
        project = solution.GetProject(project.Id)!;
        StringBuilder usings = new();
        StringBuilder members = new();
        List<(int, XamlCodeBlock)> blocks = [];
        string space = page.ContainingNamespace.IsGlobalNamespace ? string.Empty : $"namespace {page.ContainingNamespace.ToDisplayString()};\n";
        foreach (XamlCodeBlock block in code.Blocks)
        {
            if (CodeBrackets.Parse(SourceText.From(block.Code), CSharpParseOptions.Default.WithKind(SourceCodeKind.Script)).GetRoot(cancel) is not CompilationUnitSyntax unit)
            {
                continue;
            }
            // A block's usings go to the file's top, as MAUI's generator hoists them; the block keeps its length.
            char[] kept = block.Code.ToCharArray();
            foreach (UsingDirectiveSyntax used in unit.Usings)
            {
                usings.Append(used.ToString()).Append('\n');
                for (int i = used.SpanStart; i < used.Span.End; i++)
                {
                    kept[i] = kept[i] is '\r' or '\n' ? kept[i] : ' ';
                }
            }
            blocks.Add((members.Length, block));
            members.Append(kept).Append('\n');
        }
        string head = $"#nullable enable\n{usings}{space}partial class {page.Name}\n{{\n";
        Document document = project.AddDocument("__PassengerXamlCode.cs", SourceText.From(head + members + "}\n", Encoding.UTF8));
        return new XamlCodeDocument(document, [.. blocks.Select(b => (b.Item1 + head.Length, b.Item2))]);
    }

    /// <summary>The project whose compilation has the page's class, of those holding the XAML file (an additional file of
    /// MAUI's, else a file under its folder).</summary>
    private static async Task<(Project, INamedTypeSymbol)?> ProjectAsync(Solution solution, string path, string page, CancellationToken cancel)
    {
        IEnumerable<Project> holding = MauiExpressionDocument.ProjectOf(solution, path) is { } held ? [held]
            : solution.Projects.Where(p => p.FilePath is { } file && PathNames.IsUnder(path, Path.GetDirectoryName(file)!) && p.Language == LanguageNames.CSharp);
        foreach (Project project in holding)
        {
            if (await project.GetCompilationAsync(cancel).ConfigureAwait(false) is { } compilation && compilation.GetTypeByMetadataName(page) is { } type)
            {
                return (project, type);
            }
        }
        return null;
    }

    /// <summary>The solution with the members a build copied into the project's files from the XAML's x:Code (those its
    /// #line maps to the XAML) taken out.</summary>
    private static async Task<Solution> WithoutCopiesAsync(Project project, string path, CancellationToken cancel)
    {
        Solution solution = project.Solution;
        string name = Path.GetFileName(path);
        foreach (Document document in project.Documents)
        {
            SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
            if (document.FilePath is not { } file || !text.ToString().Contains(name, StringComparison.OrdinalIgnoreCase) || !text.ToString().Contains("#line", StringComparison.Ordinal)
                || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
            {
                continue;
            }
            List<MemberDeclarationSyntax> copied = [.. root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Where(m => m.Parent is TypeDeclarationSyntax && m is not TypeDeclarationSyntax && m.GetLocation().GetMappedLineSpan() is { HasMappedPath: true } mapped
                    && PathNames.Same(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, mapped.Path)), path))];
            if (copied.Count > 0 && root.RemoveNodes(copied, SyntaxRemoveOptions.KeepDirectives) is { } kept)
            {
                solution = solution.WithDocumentSyntaxRoot(document.Id, kept);
            }
        }
        return solution;
    }
}
