using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Completion;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.LanguageServer.Protocol;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;

namespace Passenger.Roslyn.Extension;

/// <summary><see cref="ExtensionMethods.Completion"/>: textDocument/completion, answered by the server's own handler, its
/// sort texts keeping its order past 10,000 items (<see cref="CompletionOrder"/>), with how well each item fits where it
/// is asked for (<see cref="CompletionPriorities"/>) beside its items. The priorities are those Roslyn's completion gave
/// the list the server keeps for resolving its items, read in its order, which is the answer's own while lists are sent
/// whole (<see cref="WholeCompletionLists"/>); a list cut, or no longer kept, is answered as the server answers it.</summary>
[Method(ExtensionMethods.Completion)]
internal sealed class PrioritizedCompletionHandler(IGlobalOptionService options) : ILspServiceDocumentRequestHandler<CompletionParams, PrioritizedCompletionList?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(CompletionParams request) => request.TextDocument;

    public async Task<PrioritizedCompletionList?> HandleRequestAsync(CompletionParams request, RequestContext context, CancellationToken cancellationToken)
    {
        Document document = await context.GetRequiredDocumentAsync(cancellationToken).ConfigureAwait(false);
        int position = await document.GetPositionFromLinePositionAsync(ProtocolConversions.PositionToLinePosition(request.Position), cancellationToken).ConfigureAwait(false);
        CompletionListCache kept = context.GetRequiredLspService<CompletionListCache>();
        if (await CompletionHandler.GetCompletionListAsync(document, position, request.Context, options, new CompletionCapabilityHelper(context.GetRequiredClientCapabilities()),
                kept, cancellationToken).ConfigureAwait(false) is not { } list)
        {
            return null;
        }
        if (CompletionOrder.Widened([.. list.Items.Select(i => i.SortText)]) is { } widened)
        {
            for (int i = 0; i < widened.Length; i++)
            {
                list.Items[i].SortText = widened[i];
            }
        }
        int[]? priorities = null;
        if (options.GetOption(LspOptionsStorage.MaxCompletionListSize) < 0
            && (list.ItemDefaults?.Data ?? list.Items.FirstOrDefault()?.Data) is CompletionResolveData resolving
            && kept.GetCachedEntry(resolving.ResultId)?.CompletionList is RoslynCompletionList listed
            && listed.ItemsList.Count == list.Items.Length)
        {
            priorities = CompletionPriorities.Runs(list.Items.Length, i => listed.ItemsList[i].Rules.MatchPriority);
        }
        return new PrioritizedCompletionList
        {
            IsIncomplete = list.IsIncomplete,
            ItemDefaults = list.ItemDefaults,
            Items = list.Items,
            MatchPriorities = priorities,
        };
    }
}

/// <summary>A completion list with how well each item fits (<see cref="CompletionPriorities"/>).</summary>
internal sealed class PrioritizedCompletionList : CompletionList
{
    [JsonPropertyName(CompletionPriorities.Member)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? MatchPriorities { get; init; }
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(PrioritizedCompletionHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class PrioritizedCompletionHandlerFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;

    [ImportingConstructor]
    public PrioritizedCompletionHandlerFactory(IGlobalOptionService options)
    {
        this.options = options;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new PrioritizedCompletionHandler(options);
}
