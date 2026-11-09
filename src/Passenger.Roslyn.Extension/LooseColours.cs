using System.Collections.Immutable;
using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.SemanticTokens;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary><see cref="ExtensionMethods.LooseColours"/>: the colours of a file in no project, a document of the server's
/// Miscellaneous Files, whose project never has all its information, so the server's own semantic tokens give it none.
/// They are classified from the references it has, as a project's file is; a document a project holds, loaded or still
/// loading, is answered null.</summary>
[Method(ExtensionMethods.LooseColours)]
internal sealed class LooseColoursHandler(IGlobalOptionService options) : ILspServiceDocumentRequestHandler<LooseColoursParams, SemanticTokens?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(LooseColoursParams request) => request.TextDocument;

    public async Task<SemanticTokens?> HandleRequestAsync(LooseColoursParams request, RequestContext context, CancellationToken cancellationToken)
    {
        if (await context.GetDocumentAsync(cancellationToken).ConfigureAwait(false) is not { } document
            || document.Project.Solution.WorkspaceKind != WorkspaceKind.MiscellaneousFiles)
        {
            return null;
        }
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        LinePositionSpan span = request.Range is { } range
            ? ProtocolConversions.RangeToLinePositionSpan(range) : text.Lines.GetLinePositionSpan(new TextSpan(0, text.Length));
        int[] data = await SemanticTokensHelpers.HandleRequestHelperAsync(document, [span], context.GetRequiredClientCapabilities().HasVisualStudioLspCapability(),
            options.GetClassificationOptions(document.Project.Language), cancellationToken).ConfigureAwait(false);
        return new SemanticTokens { Data = data };
    }
}

/// <summary>What <see cref="ExtensionMethods.LooseColours"/> is asked with: a file, and a range of it or none for all of it.</summary>
internal sealed record LooseColoursParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] global::Roslyn.LanguageServer.Protocol.Range? Range);

[ExportCSharpVisualBasicLspServiceFactory(typeof(LooseColoursHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class LooseColoursHandlerFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;

    [ImportingConstructor]
    public LooseColoursHandlerFactory(IGlobalOptionService options)
    {
        this.options = options;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new LooseColoursHandler(options);
}
