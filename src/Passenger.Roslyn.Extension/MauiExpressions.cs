using System.Collections.Immutable;
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
using Passenger.Xaml.Dialects;

namespace Passenger.Roslyn.Extension;

/// <summary>Completion at a place in a MAUI XAML file's C# expression (<see cref="MauiExpressionDocument"/>): Roslyn's own,
/// and where a name starts that MAUI may bind to the <c>x:DataType</c>, that type's members first.</summary>
public sealed class MauiExpressionCompletion : IExtensionWorkspaceMessageHandler<MauiExpressionAsked, MauiCompletions>
{
    public async Task<MauiCompletions> ExecuteAsync(MauiExpressionAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        if (MauiExpressionDocument.ProjectOf(context.Solution, message.Path) is not { } project
            || MauiExpressionCode.Of(message.Expression.Value) is not { } code)
        {
            return new MauiCompletions(message.At, []);
        }
        int at = code.CodeAt(message.At);
        List<MauiCompletion> items = [];
        int? start = null;
        if (message.Expression is { DataType: not null, Event: null } && MauiExpressionDocument.BoundStart(code.Code, at) is { } bound
            && await MauiExpressionDocument.MakeAsync(project, [message.Expression], bound, cancellationToken).ConfigureAwait(false) is { } bindable)
        {
            (int? from, IReadOnlyList<MauiCompletion> found) = await CompleteAsync(bindable, at, "0", cancellationToken).ConfigureAwait(false);
            start = from;
            items.AddRange(found);
        }
        if (await MauiExpressionDocument.MakeAsync(project, [message.Expression], null, cancellationToken).ConfigureAwait(false) is { } plain)
        {
            (int? from, IReadOnlyList<MauiCompletion> found) = await CompleteAsync(plain, at, "1", cancellationToken).ConfigureAwait(false);
            start ??= from;
            HashSet<string> had = [.. items.Select(i => i.Label)];
            items.AddRange(found.Where(i => !had.Contains(i.Label)));
        }
        return new MauiCompletions(start ?? message.At, items);
    }

    /// <summary>Roslyn's completion at a place of the document's expression, each item's sort text after <paramref name="rank"/>,
    /// so the data type's members come before what the page's code may name.</summary>
    private static async Task<(int? Start, IReadOnlyList<MauiCompletion> Items)> CompleteAsync(MauiExpressionDocument made, int at, string rank, CancellationToken cancel)
    {
        MauiExpressionDocument.Placed placed = made.Expressions[0];
        if (CompletionService.GetService(made.Document) is not { } service)
        {
            return (null, []);
        }
        CompletionList list = await service.GetCompletionsAsync(made.Document, placed.DocumentAt(at), cancellationToken: cancel).ConfigureAwait(false);
        List<MauiCompletion> items = [];
        foreach (CompletionItem item in list.ItemsList)
        {
            if (item.DisplayText.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }
            string insert = item.TryGetProperty("InsertionText", out string? written) && written is { Length: > 0 } ? written : item.DisplayText;
            items.Add(new MauiCompletion(item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix, insert, item.Tags.FirstOrDefault() ?? string.Empty,
                item.InlineDescription is { Length: > 0 } inline ? inline : null, rank + item.SortText));
        }
        return (placed.Code.WrittenAt(placed.CodeAt(list.Span.Start)), items);
    }
}

/// <summary>The symbol a place in a MAUI XAML file's C# expression names, as MAUI's source generator binds it.</summary>
public sealed class MauiExpressionSymbol : IExtensionWorkspaceMessageHandler<MauiExpressionAsked, SymbolsFound>
{
    public async Task<SymbolsFound> ExecuteAsync(MauiExpressionAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        if (MauiExpressionDocument.ProjectOf(context.Solution, message.Path) is not { } project
            || MauiExpressionCode.Of(message.Expression.Value) is not { } code
            || await MauiExpressionDocument.MakeAsync(project, [message.Expression], null, cancellationToken).ConfigureAwait(false) is not { } made)
        {
            return new SymbolsFound([], 0);
        }
        int position = made.Expressions[0].DocumentAt(code.CodeAt(message.At));
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(made.Document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is null && position > 0)
        {
            symbol = await SymbolFinder.FindSymbolAtPositionAsync(made.Document, position - 1, cancellationToken).ConfigureAwait(false);
        }
        return new SymbolsFound(symbol is null || symbol.Name.StartsWith("__", StringComparison.Ordinal) ? [] : [SymbolNames.Named(made.Document.Project.Solution, symbol)], 0);
    }
}

/// <summary>A MAUI XAML file checked as the editor has it, before any build: MAUI's source generator run again over it
/// for its own diagnostics of the file, and each of its C# expressions compiled as the generator writes it, for what C#
/// says of it there, placed in the expression. Errors alone, but an argument's conversion, which the generator settles by
/// the parameter's type (a one-character string as a char), are said of an expression.</summary>
public sealed class MauiXamlChecks : IExtensionWorkspaceMessageHandler<MauiChecksAsked, MauiChecksFound>
{
    /// <summary>What C# says that the generator's own writing may settle otherwise: an argument's conversion.</summary>
    private static readonly HashSet<string> Settled = ["CS1503"];

    public async Task<MauiChecksFound> ExecuteAsync(MauiChecksAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        Solution solution = context.Solution;
        if (solution.GetDocumentIdsWithFilePath(message.Path).FirstOrDefault(id => solution.GetAdditionalDocument(id) is not null) is not { } id)
        {
            return new MauiChecksFound([]);
        }
        solution = solution.WithAdditionalDocumentText(id, SourceText.From(message.Text, Encoding.UTF8));
        ProjectId projectId = id.ProjectId;
        SourceGeneratorExecutionVersion version = solution.GetSourceGeneratorExecutionVersion(projectId);
        solution = solution.UpdateSpecificSourceGeneratorExecutionVersions(
            new SourceGeneratorExecutionVersionMap(ImmutableSortedDictionary<ProjectId, SourceGeneratorExecutionVersion>.Empty.Add(projectId, version.IncrementMinorVersion())));
        Project project = solution.GetProject(projectId)!;
        List<MauiCheck> checks = [];
        string folder = Path.GetDirectoryName(project.FilePath) ?? string.Empty;
        foreach (Diagnostic diagnostic in await solution.CompilationState.GetSourceGeneratorDiagnosticsAsync(project.State, cancellationToken).ConfigureAwait(false))
        {
            FileLinePositionSpan place = diagnostic.Location.GetLineSpan();
            if (diagnostic.Severity is DiagnosticSeverity.Hidden || diagnostic.IsSuppressed || !place.IsValid || place.Path.Length == 0
                || !PathNames.Same(Path.GetFullPath(Path.Combine(folder, place.Path)), message.Path))
            {
                continue;
            }
            checks.Add(new MauiCheck(-1, new Place(place.StartLinePosition.Line, place.StartLinePosition.Character), 0, 0,
                diagnostic.Severity == DiagnosticSeverity.Error, diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture)));
        }
        if (message.Expressions.Count > 0 && await MauiExpressionDocument.MakeAsync(project, message.Expressions, null, cancellationToken).ConfigureAwait(false) is { } made
            && await made.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is { } model)
        {
            foreach (Diagnostic diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error || Settled.Contains(diagnostic.Id)
                    || made.Expressions.FirstOrDefault(p => p.Holds(diagnostic.Location.SourceSpan.Start)) is not { } placed)
                {
                    continue;
                }
                TextSpan span = diagnostic.Location.SourceSpan;
                checks.Add(new MauiCheck(placed.Index, null, placed.Code.WrittenAt(placed.CodeAt(span.Start)), placed.Code.WrittenAt(placed.CodeAt(Math.Min(span.End, placed.End))),
                    true, diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture)));
            }
        }
        return new MauiChecksFound(checks);
    }
}

/// <summary>A document of MAUI XAML expressions written as MAUI's source generator writes each into the code it makes of
/// the page (<c>Page.xaml.xsg.cs</c>), in a part of the page's class added to its project: one bound to the <c>x:DataType</c>
/// as <c>__source => (code, true)</c>, a root name the type has (MAUI's choice) qualified as <c>__source.Name</c>, a leading
/// <c>.</c> or <c>BindingContext.</c> taken for that type's; an event's handler added to the element's event; any other
/// as a value of the page. The generator's own document maps none of its code to the XAML and holds the file as last
/// saved, so the server answers from this one, whose every place is a place of the expression's code.</summary>
internal sealed class MauiExpressionDocument
{
    private const string Source = "__source.";

    private MauiExpressionDocument(Document document, IReadOnlyList<Placed> expressions)
    {
        Document = document;
        Expressions = expressions;
    }

    public Document Document { get; }

    /// <summary>Each expression's code as written in the document, in the order asked; one too deep to read is left out.</summary>
    public IReadOnlyList<Placed> Expressions { get; }

    /// <summary>An expression's code where the document writes it.</summary>
    /// <param name="Index">Its place in the expressions asked of.</param>
    /// <param name="Start">Where its code starts in the document.</param>
    /// <param name="Code">Its code as MAUI reads it.</param>
    /// <param name="CodeOf">For each character the document writes of it, and its end, the code's place it stands for.</param>
    /// <param name="WrittenOf">For each place of the code, and its end, where the document writes it.</param>
    internal sealed record Placed(int Index, int Start, MauiExpressionCode Code, int[] CodeOf, int[] WrittenOf)
    {
        public int End => Start + CodeOf.Length - 1;

        public bool Holds(int position) => position >= Start && position <= End;

        public int DocumentAt(int code) => Start + WrittenOf[Math.Clamp(code, 0, WrittenOf.Length - 1)];

        public int CodeAt(int position) => CodeOf[Math.Clamp(position - Start, 0, CodeOf.Length - 1)];
    }

    /// <summary>The project the server holds a XAML file in, as one of its additional files; null for none.</summary>
    public static Project? ProjectOf(Solution solution, string path) =>
        solution.GetDocumentIdsWithFilePath(path).FirstOrDefault(id => solution.GetAdditionalDocument(id) is not null) is { } id ? solution.GetProject(id.ProjectId) : null;

    /// <summary>Where a name being written at a place of the code starts, where MAUI may bind it to the data type: not
    /// after a dot, in a string or past a name's end; null elsewhere.</summary>
    public static int? BoundStart(string code, int at)
    {
        int start = Math.Clamp(at, 0, code.Length);
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
        {
            start--;
        }
        int before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(code[before]))
        {
            before--;
        }
        if ((before >= 0 && code[before] == '.' && before > 0) || InString(code, start))
        {
            return null;
        }
        return start;
    }

    /// <summary>Whether a place of the code is inside a string's text, not an interpolation's hole.</summary>
    private static bool InString(string code, int at)
    {
        ExpressionSyntax parsed = DeepText.Read(code.Length, () => SyntaxFactory.ParseExpression(code));
        if (at <= 0 || at - 1 >= parsed.FullSpan.End)
        {
            return false;
        }
        SyntaxToken token = parsed.FindToken(at - 1);
        return token.Span.Contains(at - 1) && token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.CharacterLiteralToken;
    }

    /// <summary>The expressions written into a document added to a project of the solution; <paramref name="bound"/>, a place
    /// of the first one's code where a name starts, qualified as the data type's whatever the type has. Null where none can
    /// be written.</summary>
    public static async Task<MauiExpressionDocument?> MakeAsync(Project project, IReadOnlyList<MauiExpression> expressions, int? bound, CancellationToken cancel)
    {
        if (await project.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compilation)
        {
            return null;
        }
        StringBuilder text = new("#nullable enable\n");
        List<Placed> placed = [];
        for (int i = 0; i < expressions.Count; i++)
        {
            MauiExpression expression = expressions[i];
            if (MauiExpressionCode.Of(expression.Value) is not { } code || DeepText.TooDeep(code.Code, csharp: true))
            {
                continue;
            }
            INamedTypeSymbol? root = expression.Root is { } rootName ? compilation.GetTypeByMetadataName(rootName) : null;
            INamedTypeSymbol? data = expression.DataType is { } dataName ? compilation.GetTypeByMetadataName(dataName) : null;
            INamedTypeSymbol? owner = expression.EventOwner is { } ownerName ? compilation.GetTypeByMetadataName(ownerName) : null;
            if (data is null && expression.DataType is not null)
            {
                // A data type the compilation lacks: what the code names on it can't be told.
                continue;
            }
            bool partOfRoot = root is { ContainingType: null, IsGenericType: false, TypeKind: TypeKind.Class };
            if (partOfRoot && !root!.ContainingNamespace.IsGlobalNamespace)
            {
                text.Append("namespace ").Append(root.ContainingNamespace.ToDisplayString()).Append(" {\n");
            }
            text.Append(partOfRoot ? $"partial class {root!.Name} {{\n" : "internal static class __PassengerXaml {\n");
            text.Append(partOfRoot ? "private void " : "private static void ").Append("__PassengerExpression").Append(i.ToString(CultureInfo.InvariantCulture)).Append("() {\n");
            (string written, int[] codeOf, int[] writtenOf) = owner is not null || data is null
                ? Plain(code.Code)
                : Qualified(code.Code, data, i == 0 ? bound : null);
            if (owner is not null && expression.Event is { } name)
            {
                text.Append("default(").Append(owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(")!.").Append(name).Append(" += (");
            }
            else if (data is not null)
            {
                text.Append("global::System.Func<").Append(data.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(", object?> __value = __source => (");
            }
            else
            {
                text.Append("object? __value = (");
            }
            placed.Add(new Placed(i, text.Length, code, codeOf, writtenOf));
            text.Append(written).Append("\n);\n}\n}\n");
            if (partOfRoot && !root!.ContainingNamespace.IsGlobalNamespace)
            {
                text.Append("}\n");
            }
        }
        if (placed.Count == 0)
        {
            return null;
        }
        Document document = project.AddDocument("__PassengerMauiExpressions.cs", SourceText.From(text.ToString(), Encoding.UTF8));
        return new MauiExpressionDocument(document, placed);
    }

    private static (string Written, int[] CodeOf, int[] WrittenOf) Plain(string code) =>
        (code, [.. Enumerable.Range(0, code.Length + 1)], [.. Enumerable.Range(0, code.Length + 1)]);

    /// <summary>The code with each root name the data type has qualified as <c>__source.Name</c>, as MAUI's generator
    /// writes it: not a member's name after a dot, an argument's name, nor a lambda's parameter; a leading <c>.</c> or
    /// <c>BindingContext.</c> dropped, the name after it read as the type's.</summary>
    private static (string Written, int[] CodeOf, int[] WrittenOf) Qualified(string code, ITypeSymbol data, int? bound)
    {
        HashSet<int> dropped = [];
        int body = 0;
        if (code.Length > 1 && code[0] == '.' && char.IsLetter(code[1]))
        {
            dropped.Add(0);
            body = 1;
        }
        else if (code.StartsWith("BindingContext.", StringComparison.Ordinal))
        {
            dropped.UnionWith(Enumerable.Range(0, "BindingContext.".Length));
            body = "BindingContext.".Length;
        }
        string read = code[body..];
        HashSet<int> qualified = [];
        ExpressionSyntax parsed = DeepText.Read(read.Length, () => SyntaxFactory.ParseExpression(read));
        foreach (IdentifierNameSyntax name in parsed.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            string named = name.Identifier.ValueText;
            if (name.IsMissing || named.StartsWith("__", StringComparison.Ordinal) || IsMemberName(name) || IsParameter(name, named) || !Has(data, named))
            {
                continue;
            }
            qualified.Add(body + name.SpanStart);
        }
        if (bound is { } at && at >= body)
        {
            qualified.Add(at);
        }
        StringBuilder written = new();
        List<int> codeOf = [];
        int[] writtenOf = new int[code.Length + 1];
        for (int c = 0; c <= code.Length; c++)
        {
            if (qualified.Contains(c))
            {
                foreach (char q in Source)
                {
                    written.Append(q);
                    codeOf.Add(c);
                }
            }
            writtenOf[c] = written.Length;
            if (c < code.Length && !dropped.Contains(c))
            {
                written.Append(code[c]);
                codeOf.Add(c);
            }
        }
        codeOf.Add(code.Length);
        return (written.ToString(), [.. codeOf], writtenOf);
    }

    private static bool IsMemberName(IdentifierNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax access => access.Name == name,
        MemberBindingExpressionSyntax => true,
        QualifiedNameSyntax qualified => qualified.Right == name,
        NameColonSyntax or NameEqualsSyntax => true,
        _ => false,
    };

    private static bool IsParameter(IdentifierNameSyntax name, string named) =>
        name.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any(f => f switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText == named,
            ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters.Any(p => p.Identifier.ValueText == named),
            AnonymousMethodExpressionSyntax method => method.ParameterList?.Parameters.Any(p => p.Identifier.ValueText == named) == true,
            _ => false,
        });

    /// <summary>Whether a type, its bases or (an interface) the interfaces it extends have a property, field or method of a
    /// name, as MAUI's generator asks of the data type.</summary>
    private static bool Has(ITypeSymbol type, string name)
    {
        for (ITypeSymbol? at = type; at is not null; at = at.BaseType)
        {
            if (at.GetMembers(name).Any(m => m is IPropertySymbol or IFieldSymbol or IMethodSymbol))
            {
                return true;
            }
        }
        return type.TypeKind == TypeKind.Interface && type.AllInterfaces.Any(i => i.GetMembers(name).Any(m => m is IPropertySymbol or IFieldSymbol or IMethodSymbol));
    }
}
