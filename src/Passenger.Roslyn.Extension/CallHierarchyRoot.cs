using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CallHierarchy;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.CallHierarchy;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary><see cref="ExtensionMethods.CallHierarchy"/>: textDocument/prepareCallHierarchy, its item made by the server's
/// own parts in a document declaring the symbol, the one asked about where it does, so a use of a member declared in another
/// document has one; at a record's positional parameter, the property it declares (<see cref="SymbolNames.PositionalPropertyAt"/>).
/// Null where the server has no call hierarchy for the symbol (a type, a local) or no document of the file.</summary>
[Method(ExtensionMethods.CallHierarchy)]
internal sealed class CallHierarchyRootHandler : ILspServiceDocumentRequestHandler<CallHierarchyPrepareParams, CallHierarchyItem[]?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(CallHierarchyPrepareParams request) => request.TextDocument;

    public async Task<CallHierarchyItem[]?> HandleRequestAsync(CallHierarchyPrepareParams request, RequestContext context, CancellationToken cancellationToken)
    {
        if (await context.GetDocumentAsync(cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return null;
        }
        int position = await document.GetPositionFromLinePositionAsync(ProtocolConversions.PositionToLinePosition(request.Position), cancellationToken).ConfigureAwait(false);
        SemanticModel model = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (await SymbolFinder.FindSymbolAtPositionAsync(model, position, document.Project.Solution.Services, includeType: true, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }
        ISymbol symbol = SymbolNames.PositionalPropertyAt(found, model.SyntaxTree, position) ?? found;
        if (await document.GetRequiredLanguageService<ICallHierarchyService>().CreateItemAsync(symbol, document.Project, cancellationToken).ConfigureAwait(false) is not { } described)
        {
            return null;
        }
        DocumentId? declaring = symbol.OriginalDefinition.DeclaringSyntaxReferences.Any(d => d.SyntaxTree == model.SyntaxTree) ? document.Id : null;
        return await CallHierarchyHelpers.CreateItemAsync(described, document.Project.Solution, declaring, cancellationToken).ConfigureAwait(false) is { } item ? [item] : null;
    }
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(CallHierarchyRootHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class CallHierarchyRootHandlerFactory : ILspServiceFactory
{
    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new CallHierarchyRootHandler();
}
