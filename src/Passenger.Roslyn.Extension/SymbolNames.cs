using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>The solution's own symbols a name names exactly (<see cref="SymbolNames"/>).</summary>
public sealed class SymbolsNamed : IExtensionWorkspaceMessageHandler<SymbolsAsked, SymbolsFound>
{
    public Task<SymbolsFound> ExecuteAsync(SymbolsAsked message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        SymbolNames.FindAsync(context.Solution, message.Name, message.Limit, cancellationToken);
}

/// <summary>The symbol at a caret, the one a name there refers to or a declaration there declares, with its documentation id.</summary>
public sealed class SymbolAt : IExtensionDocumentMessageHandler<Caret, SymbolsFound>
{
    public async Task<SymbolsFound> ExecuteAsync(Caret message, ExtensionMessageContext context, Document document, CancellationToken cancellationToken)
    {
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int position = text.Lines.GetPosition(new LinePosition(message.At.Line, message.At.Character));
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        return new SymbolsFound(symbol is null ? [] : [SymbolNames.Named(context.Solution, symbol)], 0);
    }
}

internal static class SymbolNames
{
    private static readonly SymbolDisplayFormat Short = SymbolDisplayFormat.CSharpShortErrorMessageFormat;

    public static async Task<SymbolsFound> FindAsync(Solution solution, string name, int limit, CancellationToken cancel)
    {
        string asked = name.Trim();
        List<ISymbol> found = [];
        if (asked.Length > 2 && asked[1] == ':' && "NTFPME".Contains(asked[0], StringComparison.Ordinal))
        {
            foreach (Project project in solution.Projects)
            {
                if (await project.GetCompilationAsync(cancel).ConfigureAwait(false) is { } compilation)
                {
                    found.AddRange(DocumentationCommentId.GetSymbolsForDeclarationId(asked, compilation));
                }
            }
        }
        else
        {
            (List<string> path, string[]? parameters) = Parse(asked);
            if (path.Count == 0 || path[^1].Length == 0)
            {
                return new SymbolsFound([], 0);
            }
            // An indexer is this[], its parameters' types inside the brackets, or Item, its metadata's name where no [IndexerName] gives another.
            string last = path[^1];
            bool bracketed = last.StartsWith("this[", StringComparison.Ordinal) && last.EndsWith(']');
            if (bracketed && last[5..^1].Trim().Length > 0)
            {
                parameters ??= [.. Split(last[5..^1], ',').Select(Normal)];
            }
            if (bracketed || last == "Item")
            {
                // Its declaration's index names it this, its compilation's member names this[].
                foreach (ISymbol symbol in await SymbolFinder.FindSourceDeclarationsAsync(solution, n => n is "this" or WellKnownMemberNames.Indexer, SymbolFilter.Member, cancel)
                    .ConfigureAwait(false))
                {
                    if (symbol is IPropertySymbol { IsIndexer: true } indexer && (bracketed || indexer.MetadataName == last) && In(indexer, path) && Takes(indexer, parameters))
                    {
                        found.Add(indexer);
                    }
                }
            }
            foreach (ISymbol symbol in bracketed ? [] : await SymbolFinder.FindSourceDeclarationsAsync(solution, Bare(last), ignoreCase: false, cancel).ConfigureAwait(false))
            {
                if (In(symbol, path) && Takes(symbol, parameters))
                {
                    found.Add(symbol);
                }
                // Type.Type names the type's constructors, as C# declares them.
                if (path.Count >= 2 && Bare(path[^1]) == Bare(path[^2]) && symbol is INamedTypeSymbol type && In(type, path[..^1]))
                {
                    found.AddRange(type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared && Takes(c, parameters)));
                }
            }
        }
        // Each target framework's project declares the symbol again: one of each, by its id and its first place.
        List<NamedSymbol> named = [.. found.Select(s => Named(solution, s)).Where(s => s.Path is not null)
            .DistinctBy(s => (s.Id ?? s.Shown, s.Path, s.At))
            .OrderBy(s => s.Shown, StringComparer.Ordinal).ThenBy(s => s.Path, StringComparer.Ordinal)];
        return new SymbolsFound([.. named.Take(limit)], Math.Max(0, named.Count - limit));
    }

    public static NamedSymbol Named(Solution solution, ISymbol symbol)
    {
        (string? path, Place? at) = Declared(solution, symbol);
        string? container = symbol.ContainingType?.ToDisplayString(Short)
            ?? (symbol.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() : null);
        return new NamedSymbol(symbol.Name, symbol.ToDisplayString(Short), KindOf(symbol), symbol.OriginalDefinition.GetDocumentationCommentId(), container, path, at);
    }

    /// <summary>The first of a symbol's declarations in a file of the solution's own, a Razor file's where its generated
    /// code maps to one; a source generator's own files are none.</summary>
    private static (string? Path, Place? At) Declared(Solution solution, ISymbol symbol)
    {
        foreach (Location location in symbol.Locations.Where(l => l.IsInSource && l.SourceTree is not null))
        {
            FileLinePositionSpan mapped = location.GetMappedLineSpan();
            if (mapped.HasMappedPath && mapped.Path.Length > 0)
            {
                return (mapped.Path, new Place(mapped.StartLinePosition.Line, mapped.StartLinePosition.Character));
            }
            if (solution.GetDocument(location.SourceTree!) is { FilePath: { Length: > 0 } file } document && document is not SourceGeneratedDocument)
            {
                LinePosition start = location.GetLineSpan().StartLinePosition;
                return (file, new Place(start.Line, start.Character));
            }
        }
        return (null, null);
    }

    /// <summary>A name's dotted parts, generic arguments kept inside their part, and its parameters' types when it gives
    /// them (none for <c>()</c>).</summary>
    private static (List<string> Path, string[]? Parameters) Parse(string name)
    {
        string[]? parameters = null;
        int open = name.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            string inside = name[(open + 1)..].TrimEnd().TrimEnd(')');
            parameters = inside.Trim().Length == 0 ? [] : [.. Split(inside, ',').Select(Normal)];
            name = name[..open];
        }
        return ([.. Split(name, '.').Select(p => p.Trim())], parameters);
    }

    /// <summary>Text split at a separator outside angle brackets, parentheses and square brackets.</summary>
    private static IEnumerable<string> Split(string text, char separator)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            depth += text[i] is '<' or '(' or '[' ? 1 : text[i] is '>' or ')' or ']' ? -1 : 0;
            if (text[i] == separator && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    private static string Bare(string part) => part.IndexOf('<', StringComparison.Ordinal) is int angle and >= 0 ? part[..angle].Trim() : part.Trim();

    /// <summary>Whether the parts before a name are the names of what the symbol is in, the nearest last.</summary>
    private static bool In(ISymbol symbol, List<string> path)
    {
        ISymbol? container = symbol.ContainingSymbol;
        for (int i = path.Count - 2; i >= 0; i--)
        {
            if (container is null or INamespaceSymbol { IsGlobalNamespace: true } || container.Name != Bare(path[i]))
            {
                return false;
            }
            container = container.ContainingSymbol;
        }
        return true;
    }

    /// <summary>Whether a symbol takes the parameters given: a method, constructor, indexer or delegate type with as many,
    /// each type as C# writes it in short or in full, or as its metadata name.</summary>
    private static bool Takes(ISymbol symbol, string[]? parameters)
    {
        if (parameters is null)
        {
            return true;
        }
        IReadOnlyList<IParameterSymbol>? taken = symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            INamedTypeSymbol { DelegateInvokeMethod: { } invoke } => invoke.Parameters,
            _ => null,
        };
        return taken is not null && taken.Count == parameters.Length && taken.Select((p, i) => Forms(p.Type).Contains(parameters[i])).All(same => same);

        static HashSet<string> Forms(ITypeSymbol type) =>
        [
            Normal(type.ToDisplayString(Short)),
            Normal(type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)),
            Normal(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal)),
            Normal(type.MetadataName),
            Normal(type.Name),
        ];
    }

    /// <summary>A parameter's type as compared: no spaces, and no ref, out, in, params or this before it.</summary>
    private static string Normal(string type)
    {
        string trimmed = type.Trim();
        foreach (string modifier in (string[])["ref ", "out ", "in ", "params ", "this ", "scoped "])
        {
            if (trimmed.StartsWith(modifier, StringComparison.Ordinal))
            {
                trimmed = trimmed[modifier.Length..].TrimStart();
            }
        }
        return trimmed.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>A symbol's kind as LSP's SymbolKind names it, as Go to Symbol's are.</summary>
    private static string KindOf(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "delegate",
        INamedTypeSymbol => "class",
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum member",
        IFieldSymbol { IsConst: true } => "constant",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "symbol",
    };
}
