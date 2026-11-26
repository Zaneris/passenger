using System.Collections.Immutable;
using System.Composition;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Completion;
using Microsoft.CodeAnalysis.LanguageServer.Handler.SemanticTokens;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.MetadataAsSource;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SignatureHelp;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;
using LspCompletionItem = Roslyn.LanguageServer.Protocol.CompletionItem;
using LspHover = Roslyn.LanguageServer.Protocol.Hover;
using LspSignatureHelp = Roslyn.LanguageServer.Protocol.SignatureHelp;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace Passenger.Roslyn.Extension;

/// <summary>Code in a C# card as <see cref="CodeParts"/>: Roslyn's tagged text, each tag's classification named by the
/// legend's token type, a line break inside code as "\n".</summary>
internal static class CodeCards
{
    /// <summary>The legend a client without Visual Studio's extensions is given, whose token types the client colours by.</summary>
    private static readonly SemanticTokensSchema Legend = SemanticTokensSchema.GetSchema(clientSupportsVisualStudioExtensions: false);

    /// <summary>A signature as an overload of a card shows it: what it returns, its type's name and its own, its type
    /// parameters, and each parameter with its type, name, modifiers and default.</summary>
    public static readonly SymbolDisplayFormat Signature = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>A stretch of a classification's text; no type for a name the server could not bind, as its colours leave out.</summary>
    public static CodePart Part(string classification, string text, string? declaration = null) =>
        new(text, classification != ClassificationTypeNames.Identifier && Legend.TokenTypeMap.TryGetValue(classification, out string? type) ? type : null, declaration);

    /// <summary>The blocks of code among tagged text, each the parts between a code block's start and its end; null for none.</summary>
    public static List<CodePart[]>? Blocks(IEnumerable<TaggedText> tags, Func<TaggedText, string?>? declaration = null)
    {
        List<CodePart[]> blocks = [];
        List<CodePart>? block = null;
        foreach (TaggedText tag in tags)
        {
            if (tag.Tag == TextTags.CodeBlockStart)
            {
                block = [];
            }
            else if (block is null)
            {
                continue;
            }
            else if (tag.Tag == TextTags.CodeBlockEnd)
            {
                blocks.Add([.. block]);
                block = null;
            }
            else if (tag.Tag == TextTags.LineBreak)
            {
                block.Add(new CodePart("\n", null));
            }
            else if (tag.Text.Length > 0)
            {
                block.Add(Part(tag.Tag.ToClassificationTypeName(), tag.Text, declaration?.Invoke(tag)));
            }
        }
        return blocks.Count > 0 ? blocks : null;
    }
}

/// <summary><see cref="ExtensionMethods.Hover"/>: textDocument/hover as the server's own answers a client without Visual
/// Studio's extensions, its quick info's sections one after another, with the parts of its blocks of code.</summary>
[Method(ExtensionMethods.Hover)]
internal sealed class CodedHoverHandler(IGlobalOptionService options, IMetadataAsSourceFileService metadata)
    : ILspServiceDocumentRequestHandler<TextDocumentPositionParams, CodedHover?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(TextDocumentPositionParams request) => request.TextDocument;

    public async Task<CodedHover?> HandleRequestAsync(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken)
    {
        Document document = await context.GetRequiredDocumentAsync(cancellationToken).ConfigureAwait(false);
        bool markdown = context.GetRequiredClientCapabilities().TextDocument?.Hover?.ContentFormat?.Contains(MarkupKind.Markdown) == true;
        int position = await document.GetPositionFromLinePositionAsync(ProtocolConversions.PositionToLinePosition(request.Position), cancellationToken).ConfigureAwait(false);
        SymbolDescriptionOptions description = options.GetSymbolDescriptionOptions(document.Project.Language);
        if (document.Project.Language == LanguageNames.CSharp)
        {
            description = description with { QuickInfoOptions = description.QuickInfoOptions with { IncludeNavigationHintsInQuickInfo = true } };
        }
        QuickInfoItem? info = await document.GetRequiredLanguageService<QuickInfoService>()
            .GetQuickInfoAsync(document, position, description, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }
        ImmutableArray<QuickInfoSection> sections = document.Project.Language == LanguageNames.CSharp
            ? await HoverAttributes.AnnotateAsync(document, info, cancellationToken).ConfigureAwait(false) : info.Sections;
        // A line break after each section, as the server's own sets them apart.
        ImmutableArray<TaggedText> tags = [.. sections.SelectMany(s => s.TaggedParts.Add(new TaggedText(TextTags.LineBreak, Environment.NewLine)))];
        SourceText text = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        List<CodePart[]> code = [];
        foreach (QuickInfoSection section in info.Sections)
        {
            Func<TaggedText, string?>? declaration = null;
            if (section.Kind == QuickInfoSectionKinds.Description && document.Project.Language == LanguageNames.CSharp
                && section.TaggedParts.Any(part => part.NavigationTarget is not null)
                && await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation)
            {
                declaration = part => HoverDeclarations.NavigationKey(part, compilation, metadata, cancellationToken);
            }
            if (CodeCards.Blocks(section.TaggedParts, declaration) is { } blocks)
            {
                code.AddRange(blocks);
            }
        }
        return new CodedHover
        {
            Range = ProtocolConversions.TextSpanToRange(info.Span, text),
            Contents = ProtocolConversions.GetDocumentationMarkupContent(tags, document.Project.Language, markdown),
            Code = code.Count > 0 ? code : null,
        };
    }
}

/// <summary>A hover with its blocks of code's parts.</summary>
internal sealed class CodedHover : LspHover
{
    [JsonPropertyName(CodeParts.Member)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CodePart[]>? Code { get; init; }
}

/// <summary><see cref="ExtensionMethods.CompletionResolve"/>: completionItem/resolve by the server's own resolving, with the
/// parts of its documentation's blocks of code from the description of the item the server's kept list holds, and for an
/// item naming several overloads each one's signature in parts, with whether it fits the arguments of a call the name
/// already makes (<see cref="SignatureFits.OfNameAsync"/>).</summary>
[Method(ExtensionMethods.CompletionResolve)]
internal sealed class CodedCompletionResolveHandler(IGlobalOptionService options) : ILspServiceDocumentRequestHandler<CodedCompletionItem, LspCompletionItem>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(CodedCompletionItem request) =>
        (request.Data is JsonElement data ? data.Deserialize<DocumentResolveData>() : request.Data as DocumentResolveData)?.TextDocument
        ?? throw new InvalidOperationException("A completion item to resolve names no document");

    public async Task<LspCompletionItem> HandleRequestAsync(CodedCompletionItem request, RequestContext context, CancellationToken cancellationToken)
    {
        Document document = await context.GetRequiredDocumentAsync(cancellationToken).ConfigureAwait(false);
        CompletionListCache kept = context.GetRequiredLspService<CompletionListCache>();
        LspCompletionItem resolved = await CompletionResolveHandler.ResolveCompletionItemAsync(request, document, options,
            new CompletionCapabilityHelper(context.GetRequiredClientCapabilities()), kept, cancellationToken).ConfigureAwait(false);
        if (resolved is not CodedCompletionItem coded || !kept.TryGetCompletionListCacheEntry(request, out CompletionListCache.CacheEntry? entry)
            || entry.CompletionList.ItemsList.FirstOrDefault(i => Same(request, i)) is not { } item)
        {
            return resolved;
        }
        string language = document.Project.Language;
        CompletionDescription? description = await document.GetRequiredLanguageService<CompletionService>().GetDescriptionAsync(document, item,
            options.GetCompletionOptions(language), options.GetSymbolDescriptionOptions(language), cancellationToken).ConfigureAwait(false);
        coded.Code = description is null ? null : CodeCards.Blocks(description.TaggedParts);
        if (await new CompletionItemKinds.ItemSymbols(document).OfAsync(item, cancellationToken).ConfigureAwait(false) is { } symbols
            && symbols.OfType<IMethodSymbol>().ToArray() is { Length: > 1 } overloads
            && SymbolCompletionItem.GetContextPosition(item) is >= 0 and int at
            && await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is { } model)
        {
            coded.Overloads = [.. overloads.Select(o => o.ToMinimalDisplayParts(model, at, CodeCards.Signature).ToTaggedText()
                .Select(t => CodeCards.Part(t.Tag.ToClassificationTypeName(), t.Text)).ToArray())];
            coded.OverloadFits = await SignatureFits.OfNameAsync(document, at, overloads, cancellationToken).ConfigureAwait(false);
        }
        return coded;
    }

    /// <summary>Whether the kept item is the one asked about, as the server's own resolving matches them: by the label, and
    /// by the sort text where they differ, an item of a namespace not imported having one of its own.</summary>
    private static bool Same(LspCompletionItem asked, RoslynCompletionItem kept) =>
        asked.Label == kept.GetEntireDisplayText()
        && (asked.Label == kept.SortText || asked.SortText?.EndsWith(kept.SortText, StringComparison.Ordinal) == true);
}

/// <summary>A completion item, resolved with its documentation's blocks of code's parts, and each of its overloads'
/// signatures in parts with whether it fits.</summary>
internal sealed class CodedCompletionItem : LspCompletionItem
{
    [JsonPropertyName(CodeParts.Member)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CodePart[]>? Code { get; set; }

    [JsonPropertyName(CodeParts.Overloads)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CodePart[]>? Overloads { get; set; }

    [JsonPropertyName(CodeParts.OverloadFits)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool[]? OverloadFits { get; set; }
}

/// <summary><see cref="ExtensionMethods.SignatureHelp"/>: textDocument/signatureHelp by the server's own, asked as for a
/// client with Visual Studio's extensions so each label comes classified, answered as for one without them with each
/// label's parts, whether the signature still fits the call's arguments and the parameter the caret's fills there
/// (<see cref="SignatureFits"/>), and its documentation and each parameter's as Markdown where the client reads it.</summary>
[Method(ExtensionMethods.SignatureHelp)]
internal sealed class CodedSignatureHelpHandler(SignatureHelpService signatures) : ILspServiceDocumentRequestHandler<TextDocumentPositionParams, LspSignatureHelp?>
{
    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(TextDocumentPositionParams request) => request.TextDocument;

    public async Task<LspSignatureHelp?> HandleRequestAsync(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken)
    {
        Document document = await context.GetRequiredDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (await SignatureHelpHandler.GetSignatureHelpAsync(signatures, document,
            ProtocolConversions.PositionToLinePosition(request.Position), supportsVisualStudioExtensions: true, cancellationToken).ConfigureAwait(false) is not { } help)
        {
            return null;
        }
        int position = await document.GetPositionFromLinePositionAsync(ProtocolConversions.PositionToLinePosition(request.Position), cancellationToken).ConfigureAwait(false);
        (_, SignatureHelpItems? items) = await signatures.GetSignatureHelpAsync(document, position,
            new SignatureHelpTriggerInfo(SignatureHelpTriggerReason.InvokeSignatureHelpCommand), cancellationToken).ConfigureAwait(false);
        bool markdown = context.GetRequiredClientCapabilities().TextDocument?.SignatureHelp?.SignatureInformation?.DocumentationFormat?.Contains(MarkupKind.Markdown) == true;
        if (items?.Items.Count != help.Signatures.Length)
        {
            items = null;
        }
        (bool Fits, int Parameter)[]? fits = items is not null
            ? await SignatureFits.OfAsync(document, position, items, cancellationToken).ConfigureAwait(false)
            : null;
        help.Signatures = [.. help.Signatures.Select((s, i) => new CodedSignatureInformation
        {
            Label = s.Label,
            Documentation = markdown && items is not null
                ? Documentation(items.Items[i].DocumentationFactory(cancellationToken))
                : s.Documentation,
            Parameters = markdown && items is not null && s.Parameters?.Length == items.Items[i].Parameters.Length
                ? [.. s.Parameters.Select((p, index) => new ParameterInformation
                {
                    Label = p.Label,
                    Documentation = Documentation(items.Items[i].Parameters[index].DocumentationFactory(cancellationToken)),
                })]
                : s.Parameters,
            ActiveParameter = fits?[i] is { Parameter: >= 0 } fit ? fit.Parameter : s.ActiveParameter,
            Code = s is VSInternalSignatureInformation { ColorizedLabel: { } classified }
                ? [.. classified.Runs.Select(r => CodeCards.Part(r.ClassificationTypeName, r.Text))]
                : null,
            Fits = fits?[i].Fits,
        })];
        return help;

        MarkupContent Documentation(IEnumerable<TaggedText> tags) =>
            ProtocolConversions.GetDocumentationMarkupContent([.. tags], document.Project.Language, featureSupportsMarkdown: true);
    }
}

/// <summary>A signature with its label's parts, and whether it still fits the call's arguments.</summary>
internal sealed class CodedSignatureInformation : SignatureInformation
{
    [JsonPropertyName(CodeParts.Member)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodePart[]? Code { get; init; }

    [JsonPropertyName(CodeParts.Fits)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Fits { get; init; }
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(CodedHoverHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class CodedHoverHandlerFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;
    private readonly IMetadataAsSourceFileService metadata;

    [ImportingConstructor]
    public CodedHoverHandlerFactory(IGlobalOptionService options, IMetadataAsSourceFileService metadata)
    {
        this.options = options;
        this.metadata = metadata;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new CodedHoverHandler(options, metadata);
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(CodedCompletionResolveHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class CodedCompletionResolveHandlerFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;

    [ImportingConstructor]
    public CodedCompletionResolveHandlerFactory(IGlobalOptionService options)
    {
        this.options = options;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new CodedCompletionResolveHandler(options);
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(CodedSignatureHelpHandler), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class CodedSignatureHelpHandlerFactory : ILspServiceFactory
{
    private readonly SignatureHelpService signatures;

    [ImportingConstructor]
    public CodedSignatureHelpHandlerFactory(SignatureHelpService signatures)
    {
        this.signatures = signatures;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new CodedSignatureHelpHandler(signatures);
}
