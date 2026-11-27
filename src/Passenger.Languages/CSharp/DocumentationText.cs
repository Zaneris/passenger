using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Languages.CSharp;

/// <summary>The XML of a written summary, with the C# comment exterior left out.</summary>
internal static class DocumentationText
{
    public static string? Of(DocumentationCommentTriviaSyntax comment)
    {
        if (comment.ContainsDiagnostics || !comment.Content.OfType<XmlElementSyntax>().Any(element =>
            element.StartTag.Name.LocalName.ValueText == "summary" && element.Content.Any(HasContent)))
        {
            return null;
        }
        return string.Join('\n', comment.ToFullString().ReplaceLineEndings("\n").Split('\n').Select(XmlLine));
    }

    private static bool HasContent(XmlNodeSyntax node) => node is not XmlTextSyntax text
        || text.TextTokens.Any(token => !string.IsNullOrWhiteSpace(token.ValueText));

    private static string XmlLine(string line)
    {
        ReadOnlySpan<char> content = line.AsSpan().TrimStart();
        if (content.StartsWith("///", StringComparison.Ordinal))
        {
            content = content[3..];
            return content.StartsWith(" ", StringComparison.Ordinal) ? content[1..].ToString() : content.ToString();
        }
        return line;
    }
}
