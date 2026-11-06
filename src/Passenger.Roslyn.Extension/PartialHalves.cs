using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary>The other half of the partial member at a place: the half with its body, or the half that declares it, which a
/// call to it is bound to. A generator's implementing half and the code that declares it are one member to a person, and
/// to the server two symbols, each with only some of its calls.</summary>
public sealed class PartialHalf : IExtensionWorkspaceMessageHandler<HalfAsked, HalfFound>
{
    public async Task<HalfFound> ExecuteAsync(HalfAsked message, ExtensionMessageContext context, CancellationToken cancellationToken)
    {
        if (!System.Uri.TryCreate(message.At.Uri, UriKind.Absolute, out Uri? uri)
            || (await context.Solution.GetTextDocumentsAsync(new DocumentUri(uri), cancellationToken).ConfigureAwait(false)).FirstOrDefault() is not Document document)
        {
            return new HalfFound(null);
        }
        SourceText text = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, WrittenPlaces.Offset(text, message.At.Start), cancellationToken).ConfigureAwait(false);
        return HalfOf(symbol?.OriginalDefinition, message.Body)?.Locations.FirstOrDefault(l => l.IsInSource) is { } place
            && context.Solution.GetDocument(place.SourceTree) is { } holder
            ? new HalfFound(await SpanAsync(holder, place.SourceSpan, cancellationToken).ConfigureAwait(false))
            : new HalfFound(null);
    }

    /// <summary>The half asked of a partial member; null for any other symbol, and for the half that already is it.</summary>
    private static ISymbol? HalfOf(ISymbol? symbol, bool body) => symbol switch
    {
        IMethodSymbol method => body ? method.PartialImplementationPart : method.PartialDefinitionPart,
        IPropertySymbol property => body ? property.PartialImplementationPart : property.PartialDefinitionPart,
        IEventSymbol @event => body ? @event.PartialImplementationPart : @event.PartialDefinitionPart,
        _ => null,
    };

    internal static async Task<UriSpan> SpanAsync(Document document, TextSpan span, CancellationToken cancel)
    {
        LinePositionSpan lines = (await document.GetValueTextAsync(cancel).ConfigureAwait(false)).Lines.GetLinePositionSpan(span);
        return new UriSpan(document.GetURI().UriString, new Place(lines.Start.Line, lines.Start.Character), new Place(lines.End.Line, lines.End.Character));
    }
}

/// <summary>The parts of the type at a place that source generators made, which the server's answer to Go to Declaration
/// leaves out of any type with a part of the person's own. None for a file that holds no C#, as a Razor component's.</summary>
public sealed class GeneratedParts : IExtensionWorkspaceMessageHandler<PartsAsked, PartsFound>
{
    public async Task<PartsFound> ExecuteAsync(PartsAsked message, ExtensionMessageContext context, CancellationToken cancellationToken)
    {
        List<UriSpan> parts = [];
        if (System.Uri.TryCreate(message.Uri, UriKind.Absolute, out Uri? uri)
            && (await context.Solution.GetTextDocumentsAsync(new DocumentUri(uri), cancellationToken).ConfigureAwait(false)).FirstOrDefault() is Document document)
        {
            SourceText text = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
            ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, WrittenPlaces.Offset(text, message.At), cancellationToken).ConfigureAwait(false);
            foreach (Microsoft.CodeAnalysis.Location place in (symbol?.OriginalDefinition as INamedTypeSymbol)?.Locations.Where(l => l.IsInSource) ?? [])
            {
                if (context.Solution.GetDocument(place.SourceTree) is SourceGeneratedDocument made)
                {
                    parts.Add(await PartialHalf.SpanAsync(made, place.SourceSpan, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        return new PartsFound(parts);
    }
}
