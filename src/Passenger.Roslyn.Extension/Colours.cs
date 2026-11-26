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
using EmbeddedSql = Passenger.Lsp.EmbeddedSql;

namespace Passenger.Roslyn.Extension;

/// <summary><see cref="ExtensionMethods.Colours"/> and <see cref="ExtensionMethods.RangeColours"/>: a C# file's semantic
/// tokens as the server's own handlers make them, in the legend the server gave, but for a name the server could not bind
/// (a type whose using is missing, a project's references not read yet), which the legend calls a variable as it does a
/// local and which is left out, so the colour the file's syntax gives it stands. The tokens are made in the schema naming
/// every classification apart and written in the legend (<see cref="ColourTokens"/>). A file of a project is coloured once
/// the project has all its information, as by the server's own; a file in no project, a document of the server's
/// Miscellaneous Files, whose project never has it, from the references it has.</summary>
internal static class Colours
{
    /// <summary>For each token type of the schema naming every classification apart, its place in the legend a client
    /// without Visual Studio's extensions is given, and in the one a client with them is.</summary>
    private static readonly int[] InLspLegend = TypesIn(SemanticTokensSchema.GetSchema(clientSupportsVisualStudioExtensions: false));
    private static readonly int[] InVisualStudioLegend = TypesIn(SemanticTokensSchema.GetSchema(clientSupportsVisualStudioExtensions: true));

    /// <summary>The colours of the file asked about, all of it or a range; null where the server has no document of it.</summary>
    public static async Task<ColoursResult?> OfAsync(global::Roslyn.LanguageServer.Protocol.Range? range, RequestContext context, IGlobalOptionService options, CancellationToken cancel)
    {
        if (await context.GetDocumentAsync(cancel).ConfigureAwait(false) is not { } document)
        {
            return null;
        }
        bool loose = document.Project.Solution.WorkspaceKind == WorkspaceKind.MiscellaneousFiles;
        if (!loose && !document.Project.State.HasAllInformation)
        {
            return new ColoursResult([], null);
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        LinePositionSpan span = range is { } asked
            ? ProtocolConversions.RangeToLinePositionSpan(asked) : text.Lines.GetLinePositionSpan(new TextSpan(0, text.Length));
        int[] named = await SemanticTokensHelpers.HandleRequestHelperAsync(document, [span], supportsVisualStudioExtensions: true,
            options.GetClassificationOptions(document.Project.Language), cancel).ConfigureAwait(false);
        int[] data = ColourTokens.Retyped(named, context.GetRequiredClientCapabilities().HasVisualStudioLspCapability() ? InVisualStudioLegend : InLspLegend);
        EmbeddedSql[]? sqlStrings = document.Project.Language == LanguageNames.CSharp
            ? await SqlStrings.InAsync(document, text.Lines.GetTextSpan(span), cancel).ConfigureAwait(false) : null;
        if (!loose)
        {
            // Made of frozen partial semantics: the server tells the client to ask again once the project's compilation is whole.
            await context.GetRequiredLspService<SemanticTokensRefreshQueue>().TryEnqueueRefreshComputationAsync(document.Project, cancel).ConfigureAwait(false);
        }
        return new ColoursResult(data, sqlStrings);
    }

    /// <summary>For each token type of the schema naming every classification apart, the place in a legend of the type the
    /// legend gives that classification; <see cref="ColourTokens.LeftOut"/> for a plain identifier, a name not bound.</summary>
    private static int[] TypesIn(SemanticTokensSchema legend)
    {
        SemanticTokensSchema named = SemanticTokensSchema.GetSchema(clientSupportsVisualStudioExtensions: true);
        int[] types = new int[named.AllTokenTypes.Length];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = legend.TokenTypeToIndex.TryGetValue(named.AllTokenTypes[i], out int same) ? same : ColourTokens.LeftOut;
        }
        foreach ((string classification, string type) in named.TokenTypeMap)
        {
            types[named.TokenTypeToIndex[type]] = classification == ClassificationTypeNames.Identifier
                ? ColourTokens.LeftOut
                : legend.TokenTypeToIndex[legend.TokenTypeMap[classification]];
        }
        return types;
    }
}

[Method(ExtensionMethods.Colours)]
internal sealed class ColoursHandler(IGlobalOptionService options) : ILspServiceDocumentRequestHandler<ColoursParams, ColoursResult?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(ColoursParams request) => request.TextDocument;

    public Task<ColoursResult?> HandleRequestAsync(ColoursParams request, RequestContext context, CancellationToken cancellationToken) =>
        Colours.OfAsync(null, context, options, cancellationToken);
}

[Method(ExtensionMethods.RangeColours)]
internal sealed class RangeColoursHandler(IGlobalOptionService options) : ILspServiceDocumentRequestHandler<ColoursParams, ColoursResult?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(ColoursParams request) => request.TextDocument;

    public Task<ColoursResult?> HandleRequestAsync(ColoursParams request, RequestContext context, CancellationToken cancellationToken) =>
        Colours.OfAsync(request.Range, context, options, cancellationToken);
}

/// <summary>What <see cref="ExtensionMethods.Colours"/> and <see cref="ExtensionMethods.RangeColours"/> are asked with: a
/// file, and the range of it asked for, as the protocol's semantic tokens requests are.</summary>
internal sealed record ColoursParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] global::Roslyn.LanguageServer.Protocol.Range? Range);

internal sealed record ColoursResult(
    [property: JsonPropertyName("data")] int[] Data,
    [property: JsonPropertyName("sqlStrings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EmbeddedSql[]? SqlStrings);

[ExportCSharpVisualBasicLspServiceFactory(typeof(ColoursHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class ColoursHandlerFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;

    [ImportingConstructor]
    public ColoursHandlerFactory(IGlobalOptionService options)
    {
        this.options = options;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new ColoursHandler(options);
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(RangeColoursHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class RangeColoursHandlerFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;

    [ImportingConstructor]
    public RangeColoursHandlerFactory(IGlobalOptionService options)
    {
        this.options = options;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new RangeColoursHandler(options);
}
