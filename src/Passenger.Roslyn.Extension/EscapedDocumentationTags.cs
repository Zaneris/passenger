using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Passenger.Roslyn.Extension;

/// <summary>A documentation comment's XML with the tags its words write escaped (<c>&amp;lt;b&amp;gt;</c>, as some
/// generators write a comment) read as the tags they write, so Roslyn formats them as it formats a tag written as one: a
/// stretch of words only where every escaped tag of a documentation name in it reads as XML, never inside code
/// (<c>c</c>, <c>code</c>, <c>tt</c>), and a tag of any other name, <c>List&lt;T&gt;</c>'s, as written.</summary>
internal static class EscapedDocumentationTags
{
    /// <summary>The tags Roslyn's formatting reads inside a section of a comment.</summary>
    private static readonly HashSet<string> Read = new(StringComparer.Ordinal)
    {
        "a", "b", "br", "c", "code", "description", "em", "i", "item", "list", "listheader", "para", "paramref", "see", "seealso",
        "strong", "term", "tt", "typeparamref", "u",
    };

    /// <summary>The tags whose words are code, written as they stand.</summary>
    private static readonly HashSet<string> Code = new(StringComparer.Ordinal) { "c", "code", "tt" };

    private const string Lt = "&lt;";

    private const string Gt = "&gt;";

    /// <summary>The XML with its escaped tags read as tags; as given where it has none, or is no XML.</summary>
    public static string? Of(string? xml)
    {
        if (xml is null || !xml.Contains(Lt, StringComparison.Ordinal))
        {
            return xml;
        }
        XElement root;
        try
        {
            root = XElement.Parse($"<doc>{xml}</doc>", LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return xml;
        }
        bool changed = false;
        foreach (XText words in root.DescendantNodes().OfType<XText>()
            .Where(t => t is not XCData && !t.Ancestors().Any(e => Code.Contains(e.Name.LocalName))).ToArray())
        {
            if (Tagged(words.Value) is { } nodes)
            {
                words.ReplaceWith(nodes);
                changed = true;
            }
        }
        return changed ? string.Concat(root.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))) : xml;
    }

    /// <summary>A stretch of words as the nodes its escaped tags write; null where it writes none, or they read as no XML.</summary>
    private static XNode[]? Tagged(string words)
    {
        string escaped = words.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", Lt, StringComparison.Ordinal)
            .Replace(">", Gt, StringComparison.Ordinal);
        StringBuilder read = new(escaped.Length);
        bool any = false;
        int from = 0;
        for (int lt = escaped.IndexOf(Lt, StringComparison.Ordinal); lt >= 0; lt = escaped.IndexOf(Lt, from, StringComparison.Ordinal))
        {
            int gt = escaped.IndexOf(Gt, lt + Lt.Length, StringComparison.Ordinal);
            if (gt < 0)
            {
                break;
            }
            int next = escaped.IndexOf(Lt, lt + Lt.Length, StringComparison.Ordinal);
            if (next >= 0 && next < gt)
            {
                // A '<' of the words themselves before this one closes: the next one may begin a tag.
                read.Append(escaped, from, next - from);
                from = next;
                continue;
            }
            string tag = escaped[(lt + Lt.Length)..gt];
            read.Append(escaped, from, lt - from);
            if (Read.Contains(NameOf(tag)))
            {
                read.Append('<').Append(tag).Append('>');
                any = true;
            }
            else
            {
                read.Append(escaped, lt, gt + Gt.Length - lt);
            }
            from = gt + Gt.Length;
        }
        if (!any)
        {
            return null;
        }
        read.Append(escaped, from, escaped.Length - from);
        try
        {
            return [.. XElement.Parse($"<doc>{read}</doc>", LoadOptions.PreserveWhitespace).Nodes()];
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>A tag's name: its letters after the slash an end tag begins with, up to a space or a slash.</summary>
    private static string NameOf(string tag)
    {
        int start = tag.StartsWith('/') ? 1 : 0;
        int end = start;
        while (end < tag.Length && char.IsAsciiLetter(tag[end]))
        {
            end++;
        }
        return end < tag.Length && !char.IsWhiteSpace(tag[end]) && tag[end] != '/' ? string.Empty : tag[start..end];
    }
}
