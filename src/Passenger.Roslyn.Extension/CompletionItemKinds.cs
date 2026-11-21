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
        Compilation? compilation = null;
        Dictionary<int, ILookup<(string Name, SymbolKind Kind, bool Generic), ISymbol>> recommended = [];
        Dictionary<int, string>? types = null;
        for (int i = 0; i < items.Count; i++)
        {
            RoslynCompletionItem item = items[i];
            if (!item.Tags.Any(Typed.Contains))
            {
                continue;
            }
            IEnumerable<ISymbol?> symbols;
            if (item.TryGetProperty("Symbols", out string? keys) && !string.IsNullOrEmpty(keys))
            {
                compilation ??= await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    return null;
                }
                symbols = keys.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(key => SymbolKey.ResolveString(key, compilation, cancellationToken: cancellationToken).GetAnySymbol());
            }
            else if (SymbolCompletionItem.GetSymbolName(item) is { } name && SymbolCompletionItem.GetKind(item) is { } kind
                && SymbolCompletionItem.GetContextPosition(item) is >= 0 and int at)
            {
                if (!recommended.TryGetValue(at, out ILookup<(string Name, SymbolKind Kind, bool Generic), ISymbol>? there))
                {
                    there = (await Recommender.GetRecommendedSymbolsAtPositionAsync(document, at, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .ToLookup(s => (s.Name, s.Kind, Arity(s) > 0));
                    recommended[at] = there;
                }
                symbols = there[(name, kind, SymbolCompletionItem.GetSymbolIsGeneric(item))];
            }
            else
            {
                continue;
            }
            if (Agreed(symbols) is { } type)
            {
                (types ??= [])[i] = type;
            }
        }
        return types;
    }

    private static int Arity(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Arity,
        INamedTypeSymbol type => type.Arity,
        _ => 0,
    };

    /// <summary>The type an item's symbols give, where they agree; null for none, or where one can't be read or they differ.</summary>
    private static string? Agreed(IEnumerable<ISymbol?> symbols)
    {
        string? agreed = null;
        foreach (ISymbol? symbol in symbols)
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
