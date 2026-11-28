using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Passenger.Roslyn.Extension;

/// <summary>A C# card's words that name a symbol, made links its client follows to the symbol's declaration
/// (<see cref="SymbolLinks"/>): outside its code, a part naming one by its key (a cref's, an exception's type), and in what
/// the documentation says a parameter or type parameter it names (paramref, typeparamref) as the documented symbol's own;
/// a keyword the documentation names alone (langword) is code.</summary>
internal static class HoverLinks
{
    /// <summary>The quick info's sections, the words of each that name a symbol made links to it.</summary>
    public static async Task<ImmutableArray<QuickInfoSection>> LinkAsync(Document document, QuickInfoItem quickInfo, ImmutableArray<QuickInfoSection> sections,
        CancellationToken cancel)
    {
        SemanticModel model = await document.GetRequiredSemanticModelAsync(cancel).ConfigureAwait(false);
        TokenSemanticInfo info = await SymbolFinder.GetSemanticInfoAtPositionAsync(model, quickInfo.Span.Start, document.Project.Solution.Services, cancel)
            .ConfigureAwait(false);
        ISymbol? symbol = info.AliasSymbol?.Target ?? (info.ReferencedSymbols is [var referenced] ? referenced : info.DeclaredSymbol);
        ISymbol? documented = ((symbol as IMethodSymbol)?.ReducedFrom ?? symbol)?.OriginalDefinition;
        return [.. sections.Select(s => QuickInfoSection.Create(s.Kind, Linked(s.TaggedParts, Documentation(s.Kind), documented)))];
    }

    /// <summary>Whether a section is what a documentation comment says: its summary, remarks, returns, value or exceptions.</summary>
    private static bool Documentation(string kind) => kind is QuickInfoSectionKinds.DocumentationComments or QuickInfoSectionKinds.RemarksDocumentationComments
        or QuickInfoSectionKinds.ReturnsDocumentationComments or QuickInfoSectionKinds.ValueDocumentationComments or QuickInfoSectionKinds.Exception;

    private static ImmutableArray<TaggedText> Linked(ImmutableArray<TaggedText> parts, bool documentation, ISymbol? documented)
    {
        ImmutableArray<TaggedText>.Builder linked = ImmutableArray.CreateBuilder<TaggedText>(parts.Length);
        bool code = false;
        foreach (TaggedText part in parts)
        {
            code = part.Tag == TextTags.CodeBlockStart || (code && part.Tag != TextTags.CodeBlockEnd);
            linked.Add(code ? part : Linked(part, documentation, documented));
        }
        return linked.MoveToImmutable();
    }

    private static TaggedText Linked(TaggedText part, bool documentation, ISymbol? documented)
    {
        if (part.NavigationTarget is { Length: > 0 } target)
        {
            // A web address is its own target and hint, which the server already writes as a link.
            return part.NavigationHint == target ? part : Link(part, target);
        }
        if (!documentation)
        {
            return part;
        }
        if (part.Tag is TextTags.Parameter or TextTags.TypeParameter && Named(documented, part) is { } named)
        {
            return Link(part, SymbolKey.CreateString(named));
        }
        return part.Tag == TextTags.Keyword ? new TaggedText(part.Tag, part.Text, part.Style | TaggedTextStyle.Code, null, null) : part;
    }

    private static TaggedText Link(TaggedText part, string key)
    {
        string link = SymbolLinks.Of(key, part.Text);
        return new TaggedText(part.Tag, part.Text, part.Style, link, link);
    }

    /// <summary>The documented symbol's parameter or type parameter a part names, a type parameter of a type holding it too;
    /// none where it has none of that name.</summary>
    private static ISymbol? Named(ISymbol? documented, TaggedText part)
    {
        if (documented is null)
        {
            return null;
        }
        if (part.Tag == TextTags.Parameter)
        {
            return ((documented as INamedTypeSymbol)?.DelegateInvokeMethod ?? documented).GetParameters().FirstOrDefault(p => p.Name == part.Text);
        }
        for (ISymbol? holder = documented; holder is not null; holder = holder.ContainingType)
        {
            if (holder.GetTypeParameters().FirstOrDefault(t => t.Name == part.Text) is { } typeParameter)
            {
                return typeParameter;
            }
        }
        return null;
    }
}
