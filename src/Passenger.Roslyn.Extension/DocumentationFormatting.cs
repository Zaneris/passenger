using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.DocumentationComments;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageService;

namespace Passenger.Roslyn.Extension;

/// <summary>C# documentation comments formatted by Roslyn's own formatting, a comment's tags written escaped read first as
/// the tags they write (<see cref="EscapedDocumentationTags"/>), so every card's words come of one formatting.</summary>
[ExportLanguageService(typeof(IDocumentationCommentFormattingService), LanguageNames.CSharp, ServiceLayer.Host)]
[Shared]
internal sealed class DocumentationFormatting : IDocumentationCommentFormattingService
{
    private static readonly RoslynFormatting Roslyns = new();

    [ImportingConstructor]
    public DocumentationFormatting()
    {
    }

    public string? Format(string? rawXmlText, Compilation? compilation = null) => Roslyns.Format(EscapedDocumentationTags.Of(rawXmlText), compilation);

    public ImmutableArray<TaggedText> Format(string? rawXmlText, ISymbol symbol, SemanticModel semanticModel, int position,
        SymbolDisplayFormat format, StructuralTypeDisplayInfo typeDisplayInfo, CancellationToken cancellationToken) =>
        Roslyns.Format(EscapedDocumentationTags.Of(rawXmlText), symbol, semanticModel, position, format, typeDisplayInfo, cancellationToken);

    private sealed class RoslynFormatting : AbstractDocumentationCommentFormattingService;
}
