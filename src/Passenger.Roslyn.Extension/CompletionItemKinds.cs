using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.Recommendations;
using Microsoft.CodeAnalysis.Tags;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace Passenger.Roslyn.Extension;

/// <summary>What Roslyn's completion items name (<see cref="CompletionKind"/>) and the types they give, read from the items
/// its completion made: their tags, and the symbols an item of a value or a method keeps the keys of, or, for one keeping
/// only its symbols' name and kind, those of that name and kind recommended where the list was asked for, as the server
/// finds them to describe one.</summary>
internal static class CompletionItemKinds
{
    /// <summary>A type as a row shows it: its name and its type arguments' names, a keyword for a type that has one, and
    /// a nullable reference's mark.</summary>
    private static readonly SymbolDisplayFormat TypeShown = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>The tags of the items that give a type: a value's, or a method's return.</summary>
    private static readonly string[] Typed =
    [
        WellKnownTags.Local, WellKnownTags.Parameter, WellKnownTags.Field, WellKnownTags.Property, WellKnownTags.Constant,
        WellKnownTags.Event, WellKnownTags.Method, WellKnownTags.ExtensionMethod,
    ];

    public static CompletionKind Of(RoslynCompletionItem item) =>
        item.Tags.Contains(WellKnownTags.ExtensionMethod) ? CompletionKind.ExtensionMethod
        : item.Tags.Contains(WellKnownTags.Parameter) ? (item.DisplayTextSuffix == ":" ? CompletionKind.NamedArgument : CompletionKind.Parameter)
        : item.Tags.Contains(WellKnownTags.Local) || item.Tags.Contains(WellKnownTags.RangeVariable) ? CompletionKind.Local
        : CompletionKind.AsListed;

    /// <summary>The type each item of a value gives, and each item of methods every one of which returns the same, by its
    /// place among <paramref name="items"/>; null for none. Read in the document's compilation, which its completion has
    /// just made, the symbols recommended at a place found once for every item asked for there.</summary>
    public static async Task<Dictionary<int, string>?> TypesAsync(Document document, IReadOnlyList<RoslynCompletionItem> items, CancellationToken cancellationToken)
    {
        ItemSymbols read = new(document);
        Dictionary<int, string>? types = null;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Tags.Any(Typed.Contains) && await read.OfAsync(items[i], cancellationToken).ConfigureAwait(false) is { } symbols
                && Agreed(symbols) is { } type)
            {
                (types ??= [])[i] = type;
            }
        }
        return types;
    }

    /// <summary>The symbols Roslyn's completion items name, read again in a document's compilation: from the keys an item
    /// keeps, or, for one keeping only its symbols' name and kind, those of that name and kind recommended where its list
    /// was asked for, as the server finds them to describe one; each place's recommendations found once.</summary>
    public sealed class ItemSymbols(Document document)
    {
        private readonly Dictionary<int, ILookup<(string Name, SymbolKind Kind, bool Generic), ISymbol>> recommended = [];
        private Compilation? compilation;

        /// <summary>An item's symbols, an unread key's left out; null for an item that names none.</summary>
        public async Task<IReadOnlyList<ISymbol>?> OfAsync(RoslynCompletionItem item, CancellationToken cancellationToken)
        {
            if (item.TryGetProperty("Symbols", out string? keys) && !string.IsNullOrEmpty(keys))
            {
                compilation ??= await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                return compilation is null ? null : [.. keys.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(key => SymbolKey.ResolveString(key, compilation, cancellationToken: cancellationToken).GetAnySymbol()).OfType<ISymbol>()];
            }
            if (SymbolCompletionItem.GetSymbolName(item) is not { } name || SymbolCompletionItem.GetKind(item) is not { } kind
                || SymbolCompletionItem.GetContextPosition(item) is not (>= 0 and int at))
            {
                return null;
            }
            if (!recommended.TryGetValue(at, out ILookup<(string Name, SymbolKind Kind, bool Generic), ISymbol>? there))
            {
                there = (await Recommender.GetRecommendedSymbolsAtPositionAsync(document, at, cancellationToken: cancellationToken).ConfigureAwait(false))
                    .ToLookup(s => (s.Name, s.Kind, Arity(s) > 0));
                recommended[at] = there;
            }
            return [.. there[(name, kind, SymbolCompletionItem.GetSymbolIsGeneric(item))]];
        }
    }

    private static int Arity(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Arity,
        INamedTypeSymbol type => type.Arity,
        _ => 0,
    };

    /// <summary>The type an item's symbols give, where they agree; null for none, or where one can't be read or they differ.</summary>
    private static string? Agreed(IReadOnlyList<ISymbol> symbols)
    {
        string? agreed = null;
        foreach (ISymbol symbol in symbols)
        {
            ITypeSymbol? type = symbol switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol parameter => parameter.Type,
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                IEventSymbol @event => @event.Type,
                IMethodSymbol method => method.ReturnType,
                _ => null,
            };
            string? shown = type?.ToDisplayString(TypeShown);
            if (shown is null || (agreed is not null && agreed != shown))
            {
                return null;
            }
            agreed = shown;
        }
        return agreed;
    }
}
