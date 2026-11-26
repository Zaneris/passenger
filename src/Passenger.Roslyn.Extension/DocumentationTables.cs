using System.Collections.Immutable;
using System.Composition;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.DocumentationComments;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageService;

namespace Passenger.Roslyn.Extension;

/// <summary>Keep table rows and cells through Roslyn's documentation formatting, which normally flattens them.</summary>
[ExportLanguageService(typeof(IDocumentationCommentFormattingService), LanguageNames.CSharp, ServiceLayer.Host)]
[Shared]
[method: ImportingConstructor]
internal sealed class DocumentationTables() : IDocumentationCommentFormattingService
{
    private static readonly Formatter Stock = new();

    public string? Format(string? rawXmlText, Compilation? compilation = null) => Stock.Format(rawXmlText, compilation);

    public ImmutableArray<TaggedText> Format(string? rawXmlText, ISymbol symbol, SemanticModel semanticModel, int position,
        SymbolDisplayFormat format, StructuralTypeDisplayInfo typeDisplayInfo, CancellationToken cancellationToken) =>
        Stock.Format(KeepTables(rawXmlText), symbol, semanticModel, position, format, typeDisplayInfo, cancellationToken);

    private static string? KeepTables(string? xml)
    {
        if (xml is null || !xml.Contains("<list", StringComparison.Ordinal) || !xml.Contains("table", StringComparison.Ordinal))
        {
            return xml;
        }
        try
        {
            XElement root = XElement.Parse($"<doc>{xml}</doc>", LoadOptions.PreserveWhitespace);
            foreach (XElement table in root.Descendants("list").Where(list => (string?)list.Attribute("type") == "table"
                && !list.Ancestors("code").Any()).ToArray())
            {
                string carried = table.ToString(SaveOptions.DisableFormatting).ReplaceLineEndings("&#10;");
                table.ReplaceWith(new XElement("code", CodeParts.DocumentationTable + carried));
            }
            return string.Concat(root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
        }
        catch (XmlException)
        {
            return xml;
        }
    }

    private sealed class Formatter : AbstractDocumentationCommentFormattingService;
}
