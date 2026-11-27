using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Languages.CSharp;

/// <summary>The readable text of a local XML documentation block, from the syntax the C# editor already parsed.</summary>
internal static class DocumentationText
{
    public static string? Of(DocumentationCommentTriviaSyntax comment)
    {
        List<string> sections = [];
        bool hasSummary = false;
        foreach (XmlNodeSyntax part in comment.Content)
        {
            if (part is XmlTextSyntax text && string.IsNullOrWhiteSpace(TextOf(text)))
            {
                continue;
            }
            if (part is not XmlElementSyntax element || element.ContainsDiagnostics)
            {
                // A partial comment or one supplied by inheritdoc/include stays available as source.
                return null;
            }
            string name = element.StartTag.Name.LocalName.ValueText;
            string content = Flatten(element);
            if (name == "summary")
            {
                hasSummary = content.Length > 0;
                sections.Add(content);
            }
            else if (content.Length > 0)
            {
                string? label = element.StartTag.Attributes.Select(LabelOf).FirstOrDefault(label => label is not null);
                sections.Add($"{label ?? name}: {content}");
            }
        }
        return hasSummary ? string.Join("  ·  ", sections) : null;
    }

    private static string Flatten(XmlElementSyntax element)
    {
        StringBuilder words = new();
        foreach (SyntaxNode node in element.DescendantNodes(descendIntoChildren: node => node is XmlElementSyntax))
        {
            switch (node)
            {
                case XmlTextSyntax text:
                    words.Append(TextOf(text));
                    break;
                case XmlElementSyntax paragraph when paragraph.StartTag.Name.LocalName.ValueText is "para" or "code" or "item":
                    words.Append(' ');
                    break;
                case XmlEmptyElementSyntax reference:
                    words.Append(reference.Attributes.Select(LabelOf).FirstOrDefault(label => label is not null));
                    break;
                case XmlCDataSectionSyntax data:
                    foreach (SyntaxToken token in data.TextTokens)
                    {
                        words.Append(token.ValueText);
                    }
                    break;
            }
        }
        return string.Join(' ', words.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TextOf(XmlTextSyntax text) => string.Concat(text.TextTokens.Select(token => token.ValueText));

    private static string? LabelOf(XmlAttributeSyntax attribute) => attribute switch
    {
        XmlNameAttributeSyntax name => name.Identifier.Identifier.ValueText,
        XmlCrefAttributeSyntax reference => reference.Cref.ToString(),
        XmlTextAttributeSyntax text when text.Name.LocalName.ValueText is "href" or "langword" =>
            string.Concat(text.TextTokens.Select(token => token.ValueText)),
        _ => null,
    };
}
