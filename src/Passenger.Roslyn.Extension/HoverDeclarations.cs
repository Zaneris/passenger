using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.MetadataAsSource;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;
using LspLocation = Roslyn.LanguageServer.Protocol.Location;

namespace Passenger.Roslyn.Extension;

internal sealed class HoverDeclarationParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("symbolKey")]
    public required string SymbolKey { get; init; }
}

/// <summary>Resolves the symbol Roslyn associated with a signature part. Metadata is only opened on a click.</summary>
[ExportCSharpVisualBasicStatelessLspService(typeof(HoverDeclarations)), Shared]
[Method(ExtensionMethods.HoverDeclaration)]
[method: ImportingConstructor]
internal sealed class HoverDeclarations(IGlobalOptionService options, IMetadataAsSourceFileService metadata)
    : ILspServiceDocumentRequestHandler<HoverDeclarationParams, LspLocation[]>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(HoverDeclarationParams request) => request.TextDocument;

    public static string? CanOpen(TaggedText part, Compilation compilation, IMetadataAsSourceFileService metadata, CancellationToken cancel)
    {
        if (part.NavigationTarget is { } key && Resolve(key, compilation, cancel) is { } symbol
            && (symbol.Locations.Any(location => location.IsInSource) || metadata.IsNavigableMetadataSymbol(symbol)))
        {
            return key;
        }
        return null;
    }

    public async Task<LspLocation[]> HandleRequestAsync(HoverDeclarationParams request, RequestContext context, CancellationToken cancellationToken)
    {
        if (await context.GetDocumentAsync(cancellationToken).ConfigureAwait(false) is not { } document
            || await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation
            || Resolve(request.SymbolKey, compilation, cancellationToken) is not { } symbol)
        {
            return [];
        }

        Solution solution = document.Project.Solution;
        ISymbol source = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken).ConfigureAwait(false) ?? symbol;
        List<LspLocation> places = [];
        foreach (Microsoft.CodeAnalysis.Location location in source.Locations)
        {
            if (location.SourceTree is { } tree && solution.GetDocument(tree) is { } declaring
                && await ProtocolConversions.TextSpanToLocationAsync(declaring, location.SourceSpan, isStale: false, cancellationToken).ConfigureAwait(false) is { } place)
            {
                places.Add(place);
            }
        }
        if (places.Count > 0 || !metadata.IsNavigableMetadataSymbol(symbol))
        {
            return [.. places];
        }

        Microsoft.CodeAnalysis.Workspace workspace = await context.GetRequiredWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        MetadataAsSourceFile file = await metadata.GetGeneratedFileAsync(workspace, document.Project, symbol, signaturesOnly: false,
            options: options.GetMetadataAsSourceOptions(), cancellationToken: cancellationToken).ConfigureAwait(false);
        LinePositionSpan range = file.IdentifierLocation.GetLineSpan().Span;
        return [new LspLocation
        {
            DocumentUri = ProtocolConversions.CreateAbsoluteDocumentUri(file.FilePath),
            Range = ProtocolConversions.LinePositionToRange(range),
        }];
    }

    private static ISymbol? Resolve(string key, Compilation compilation, CancellationToken cancel)
    {
        ISymbol? symbol = SymbolKey.ResolveString(key, compilation, cancellationToken: cancel).Symbol;
        return (symbol is IAliasSymbol alias ? alias.Target : symbol)?.GetOriginalUnreducedDefinition();
    }
}
