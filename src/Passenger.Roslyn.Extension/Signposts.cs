using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>The counts of the signposts above a document's declarations on screen, in one request: each declaration's own
/// usages (the references whose symbol is the declaration itself, never those of what implements or overrides it) and
/// what implements, overrides or derives from it, found in-process; asked for, the places its usages count, so a list of
/// them is the count's own. What was found is kept for each version of the solution, so scrolling back over what was
/// counted, or pressing what was, asks nothing again.</summary>
public sealed class Signposts : IExtensionDocumentMessageHandler<SignpostsAsked, SignpostsFound>
{
    private static readonly ConditionalWeakTable<Solution, Found> Kept = [];

    public async Task<SignpostsFound> ExecuteAsync(SignpostsAsked message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        Solution solution = context.Solution;
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        Found kept = Kept.GetOrCreateValue(solution);
        List<SignpostCount> counts = [];
        foreach (Place at in message.At)
        {
            ISymbol? symbol = model is null || root is null || at.Line >= text.Lines.Count ? null
                : DeclaredAt(model, root, WrittenPlaces.Offset(text, at), cancellationToken);
            if (symbol is null)
            {
                counts.Add(new SignpostCount(null, null));
                continue;
            }
            string key = symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString();
            IReadOnlyList<(Document Document, TextSpan Span)>? uses = null;
            if (message.Usages && !kept.Uses.TryGetValue(key, out uses))
            {
                uses = kept.Uses[key] = await UsesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            }
            int? inheriting = null;
            if (message.Inheriting && !kept.Inheriting.TryGetValue(key, out inheriting))
            {
                inheriting = kept.Inheriting[key] = await InheritingAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            }
            counts.Add(new SignpostCount(uses?.Count, inheriting,
                message.Places && uses is not null ? await PlacesAsync(uses, cancellationToken).ConfigureAwait(false) : null));
        }
        return new SignpostsFound(counts);
    }

    /// <summary>The symbol a declaration whose name stands at a position declares.</summary>
    private static ISymbol? DeclaredAt(SemanticModel model, SyntaxNode root, int position, CancellationToken cancel)
    {
        SyntaxToken token = root.FindToken(position);
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            if (model.GetDeclaredSymbol(node, cancel) is { } declared)
            {
                return declared;
            }
        }
        return null;
    }

    /// <summary>The places that use a symbol itself (a type's own constructors' calls counted as its uses), the declarations
    /// and those of what implements or overrides it left out, each once, in the order found.</summary>
    private static async Task<IReadOnlyList<(Document Document, TextSpan Span)>> UsesAsync(ISymbol symbol, Solution solution, CancellationToken cancel)
    {
        ISymbol own = symbol.OriginalDefinition;
        HashSet<(DocumentId, TextSpan)> seen = [];
        List<(Document, TextSpan)> places = [];
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(own, solution, cancel).ConfigureAwait(false))
        {
            ISymbol found = referenced.Definition.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(found, own)
                || (own is INamedTypeSymbol && found is IMethodSymbol { MethodKind: MethodKind.Constructor } made && SymbolEqualityComparer.Default.Equals(made.ContainingType, own)))
            {
                foreach (ReferenceLocation place in referenced.Locations)
                {
                    if (!place.IsImplicit && !place.IsCandidateLocation && seen.Add((place.Document.Id, place.Location.SourceSpan)))
                    {
                        places.Add((place.Document, place.Location.SourceSpan));
                    }
                }
            }
        }
        return places;
    }

    /// <summary>The places a count counted as the server names them: their documents' URIs and their ends.</summary>
    private static async Task<IReadOnlyList<UriSpan>> PlacesAsync(IReadOnlyList<(Document Document, TextSpan Span)> uses, CancellationToken cancel)
    {
        List<UriSpan> named = new(uses.Count);
        foreach ((Document document, TextSpan span) in uses)
        {
            named.Add(await PartialHalf.SpanAsync(document, span, cancel).ConfigureAwait(false));
        }
        return named;
    }

    /// <summary>What one version of the solution was found to have: each symbol's uses and what inherits from it.</summary>
    private sealed class Found
    {
        public ConcurrentDictionary<string, IReadOnlyList<(Document Document, TextSpan Span)>> Uses { get; } = [];

        public ConcurrentDictionary<string, int?> Inheriting { get; } = [];
    }

    /// <summary>What implements an interface or its member, overrides an abstract or virtual member, or derives from an
    /// abstract class; null for a symbol nothing may.</summary>
    private static async Task<int?> InheritingAsync(ISymbol symbol, Solution solution, CancellationToken cancel) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } type =>
            (await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancel).ConfigureAwait(false)).Count(),
        INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: true } type =>
            (await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancel).ConfigureAwait(false)).Count(),
        INamedTypeSymbol => null,
        { IsStatic: true } or { IsSealed: true } => null,
        { ContainingType.TypeKind: TypeKind.Interface } =>
            (await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancel).ConfigureAwait(false)).Count(),
        { IsAbstract: true } or { IsVirtual: true } =>
            (await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancel).ConfigureAwait(false)).Count(),
        _ => null,
    };
}
