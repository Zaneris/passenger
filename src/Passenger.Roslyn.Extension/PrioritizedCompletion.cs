using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Completion;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;
using LspCompletionContext = Roslyn.LanguageServer.Protocol.CompletionContext;
using LspCompletionList = Roslyn.LanguageServer.Protocol.CompletionList;
using LspTriggerKind = Roslyn.LanguageServer.Protocol.CompletionTriggerKind;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using RoslynCompletionOptions = Microsoft.CodeAnalysis.Completion.CompletionOptions;
using RoslynTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;

namespace Passenger.Roslyn.Extension;

/// <summary><see cref="ExtensionMethods.Completion"/>: textDocument/completion, answered by the server's own handler, its
/// sort texts keeping its order past 10,000 items (<see cref="CompletionOrder"/>), with how well each item fits where it
/// is asked for (<see cref="CompletionPriorities"/>) beside its items. The priorities are those Roslyn's completion gave
/// the list the server keeps for resolving its items, read in its order, which is the answer's own while lists are sent
/// whole (<see cref="WholeCompletionLists"/>); a list cut, or no longer kept, is answered as the server answers it. Asked
/// in parts (<see cref="CompletionParts"/>), the items of namespaces not imported are worked out beside the rest, which is
/// answered as soon as it is found, they kept for the client to ask for (<see cref="LaterCompletions"/>).</summary>
[Method(ExtensionMethods.Completion)]
internal sealed class PrioritizedCompletionHandler(IGlobalOptionService options, LaterCompletions later)
    : ILspServiceDocumentRequestHandler<PartedCompletionParams, PrioritizedCompletionList?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(PartedCompletionParams request) => request.TextDocument;

    public async Task<PrioritizedCompletionList?> HandleRequestAsync(PartedCompletionParams request, RequestContext context, CancellationToken cancellationToken)
    {
        Document document = await context.GetRequiredDocumentAsync(cancellationToken).ConfigureAwait(false);
        int position = await document.GetPositionFromLinePositionAsync(ProtocolConversions.PositionToLinePosition(request.Position), cancellationToken).ConfigureAwait(false);
        CompletionListCache kept = context.GetRequiredLspService<CompletionListCache>();
        CompletionCapabilityHelper capabilities = new(context.GetRequiredClientCapabilities());
        RoslynCompletionOptions completion = options.GetCompletionOptionsForLsp(document.Project.Language, capabilities);
        if (request.Parts != true || !completion.ShouldShowItemsFromUnimportedNamespaces)
        {
            return await CompletionHandler.GetCompletionListAsync(document, position, request.Context, options, capabilities, kept, cancellationToken).ConfigureAwait(false) is { } whole
                ? await PrioritizedAsync(document, whole, kept, laterPart: false, cancellationToken).ConfigureAwait(false)
                : null;
        }
        // As the server's own handler begins, the trigger asked of the whole list once.
        SourceText text = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        RoslynTrigger trigger = await ProtocolConversions.LSPToRoslynCompletionTriggerAsync(request.Context, document, position, cancellationToken).ConfigureAwait(false);
        CompletionService service = document.GetRequiredLanguageService<CompletionService>();
        Project project = document.Project;
        if (request.Context?.TriggerKind is not LspTriggerKind.TriggerForIncompleteCompletions
            && !service.ShouldTriggerCompletion(project, project.Services, text, position, trigger, completion, project.Solution.Options, roles: null))
        {
            return null;
        }
        int most = options.GetOption(LspOptionsStorage.MaxCompletionListSize);
        Task<(RoslynCompletionList CompletionList, bool IsIncomplete, bool isHardSelection, long ResultId)?> Listed(ExpandedCompletionMode part, CancellationToken cancel) =>
            CompletionHandler.GetFilteredCompletionListAsync(request.Context, document, text, position, trigger, completion with { ExpandedCompletionBehavior = part },
                capabilities, service, kept, most, cancel);
        using LaterCompletions.Pending rest = later.Begin(cancellationToken);
        Task<(RoslynCompletionList CompletionList, bool IsIncomplete, bool isHardSelection, long ResultId)?> first =
            Listed(ExpandedCompletionMode.NonExpandedItemsOnly, cancellationToken);
        // Begun once the first part is found, which has the server to itself; none where a provider holds the list to its own.
        rest.Work(async cancel =>
        {
            await ((Task)first).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!first.IsCompletedSuccessfully || first.Result is { CompletionList.IsExclusive: true }
                || await Listed(ExpandedCompletionMode.ExpandedItemsOnly, cancel).ConfigureAwait(false) is not { } listed)
            {
                return null;
            }
            // In the whole list's selection, which the first part's providers say (a lambda's parameter being named).
            bool hard = first.Result is { } before ? before.isHardSelection : listed.isHardSelection;
            return await PrioritizedAsync(document, await CompletionResultFactory.ConvertToLspCompletionListAsync(document, capabilities, listed.CompletionList,
                listed.IsIncomplete, hard, listed.ResultId, cancel).ConfigureAwait(false), kept, laterPart: true, cancel).ConfigureAwait(false);
        });
        (RoslynCompletionList CompletionList, bool IsIncomplete, bool isHardSelection, long ResultId)? scoped = await first.ConfigureAwait(false);
        PrioritizedCompletionList? answer = scoped is { } inScope
            ? await PrioritizedAsync(document, await CompletionResultFactory.ConvertToLspCompletionListAsync(document, capabilities, inScope.CompletionList,
                inScope.IsIncomplete, inScope.isHardSelection, inScope.ResultId, cancellationToken).ConfigureAwait(false), kept, laterPart: false, cancellationToken).ConfigureAwait(false)
            : null;
        if (scoped is { CompletionList.IsExclusive: true })
        {
            return answer;
        }
        answer ??= new PrioritizedCompletionList { Items = [] };
        answer.Later = rest.Keep();
        return answer;
    }

    /// <summary>A list answered with its sort texts keeping its order, a later part's after any first part's, how well each
    /// item fits, what each names (<see cref="CompletionKinds"/>) and the types its values have and its methods return.</summary>
    private async Task<PrioritizedCompletionList> PrioritizedAsync(Document document, LspCompletionList list, CompletionListCache kept, bool laterPart,
        CancellationToken cancellationToken)
    {
        if (CompletionOrder.Widened([.. list.Items.Select(i => i.SortText)], laterPart) is { } widened)
        {
            for (int i = 0; i < widened.Length; i++)
            {
                list.Items[i].SortText = widened[i];
            }
        }
        int[]? priorities = null;
        int[]? kinds = null;
        Dictionary<int, string>? types = null;
        if (options.GetOption(LspOptionsStorage.MaxCompletionListSize) < 0
            && (list.ItemDefaults?.Data ?? list.Items.FirstOrDefault()?.Data) is CompletionResolveData resolving
            && kept.GetCachedEntry(resolving.ResultId)?.CompletionList is RoslynCompletionList listed
            && listed.ItemsList.Count == list.Items.Length)
        {
            priorities = CompletionPriorities.Runs(list.Items.Length, i => listed.ItemsList[i].Rules.MatchPriority);
            kinds = CompletionKinds.Runs(list.Items.Length, i => CompletionItemKinds.Of(listed.ItemsList[i]));
            types = await CompletionItemKinds.TypesAsync(document, listed.ItemsList, cancellationToken).ConfigureAwait(false);
        }
        return new PrioritizedCompletionList
        {
            IsIncomplete = list.IsIncomplete,
            ItemDefaults = list.ItemDefaults,
            Items = list.Items,
            MatchPriorities = priorities,
            ItemKinds = kinds,
            ItemTypes = types,
        };
    }
}

/// <summary>textDocument/completion's parameters, and whether the list is asked for in parts (<see cref="CompletionParts.Asked"/>).</summary>
internal sealed class PartedCompletionParams : TextDocumentPositionParams
{
    [JsonPropertyName("context")]
    public LspCompletionContext? Context { get; set; }

    [JsonPropertyName(CompletionParts.Asked)]
    public bool? Parts { get; set; }
}

/// <summary>A completion list with how well each item fits (<see cref="CompletionPriorities"/>), what each names and the
/// types they give (<see cref="CompletionKinds"/>), and the id its later part is asked by (<see cref="CompletionParts.Later"/>).</summary>
internal sealed class PrioritizedCompletionList : LspCompletionList
{
    [JsonPropertyName(CompletionPriorities.Member)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? MatchPriorities { get; init; }

    [JsonPropertyName(CompletionKinds.Member)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? ItemKinds { get; init; }

    [JsonPropertyName(CompletionKinds.TypesMember)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<int, string>? ItemTypes { get; init; }

    [JsonPropertyName(CompletionParts.Later)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Later { get; set; }
}

/// <summary><see cref="ExtensionMethods.LaterCompletion"/>: a list's later part, as soon as it is found; null for one let go
/// of (a newer list's come since) or none. Its asking given up stops its work.</summary>
[Method(ExtensionMethods.LaterCompletion)]
internal sealed class LaterCompletionHandler(LaterCompletions later) : ILspServiceRequestHandler<LaterAsked, PrioritizedCompletionList?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => false;

    public async Task<PrioritizedCompletionList?> HandleRequestAsync(LaterAsked request, RequestContext context, CancellationToken cancellationToken)
    {
        if (later.Take(request.Later) is not { } pending)
        {
            return null;
        }
        try
        {
            return await pending.Answer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pending.Stop();
        }
    }
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

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) =>
        new PrioritizedCompletionHandler(options, lspServices.GetRequiredService<LaterCompletions>());
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(LaterCompletionHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class LaterCompletionHandlerFactory : ILspServiceFactory
{
    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) =>
        new LaterCompletionHandler(lspServices.GetRequiredService<LaterCompletions>());
}
