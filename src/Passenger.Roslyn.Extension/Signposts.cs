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
/// what implements, overrides or derives from it, found in-process. Kept for each version of the solution, so scrolling
/// back over what was counted asks nothing again.</summary>
public sealed class Signposts : IExtensionDocumentMessageHandler<SignpostsAsked, SignpostsFound>
{
    private static readonly ConditionalWeakTable<Solution, ConcurrentDictionary<(string Symbol, bool Usages, bool Inheriting), SignpostCount>> Kept = [];

    public async Task<SignpostsFound> ExecuteAsync(SignpostsAsked message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        Solution solution = context.Solution;
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        ConcurrentDictionary<(string, bool, bool), SignpostCount> kept = Kept.GetOrCreateValue(solution);
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
            (string, bool, bool) key = (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(), message.Usages, message.Inheriting);
            if (!kept.TryGetValue(key, out SignpostCount? count))
            {
                count = new SignpostCount(
                    message.Usages ? await UsagesAsync(symbol, solution, cancellationToken).ConfigureAwait(false) : null,
                    message.Inheriting ? await InheritingAsync(symbol, solution, cancellationToken).ConfigureAwait(false) : null);
                kept[key] = count;
            }
            counts.Add(count);
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
    /// and those of what implements or overrides it left out.</summary>
    private static async Task<int> UsagesAsync(ISymbol symbol, Solution solution, CancellationToken cancel)
    {
        ISymbol own = symbol.OriginalDefinition;
        HashSet<(DocumentId, TextSpan)> places = [];
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(own, solution, cancel).ConfigureAwait(false))
        {
            ISymbol found = referenced.Definition.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(found, own)
                || (own is INamedTypeSymbol && found is IMethodSymbol { MethodKind: MethodKind.Constructor } made && SymbolEqualityComparer.Default.Equals(made.ContainingType, own)))
            {
                foreach (ReferenceLocation place in referenced.Locations)
                {
                    if (!place.IsImplicit && !place.IsCandidateLocation)
                    {
                        places.Add((place.Document.Id, place.Location.SourceSpan));
                    }
                }
            }
        }
        return places.Count;
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
