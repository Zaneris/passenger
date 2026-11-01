using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A component's or page's scoped stylesheet (Counter.razor.css beside Counter.razor) and what its selectors may name:
/// the HTML elements its markup writes (a component's tag, which takes no scope of its own, left out), the classes its
/// <c>class</c> attributes give and the ids its <c>id</c> attributes give, each written as plain text; and whether a place in
/// the stylesheet is where a selector is written, outside every rule's braces and comment.</summary>
public static partial class ScopedCss
{
    /// <summary>The markup a scoped stylesheet styles, by its path; null for a stylesheet of no component or page.</summary>
    public static string? MarkupOf(string path) =>
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && RazorFiles.Is(path[..^4]) ? path[..^4] : null;

    /// <summary>The names a component's markup gives its selectors, each once in the order written.</summary>
    public static (IReadOnlyList<string> Elements, IReadOnlyList<string> Classes, IReadOnlyList<string> Ids) Names(string markup)
    {
        List<string> elements = [.. Tags().Matches(markup).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
        List<string> classes = [];
        List<string> ids = [];
        foreach (Match attribute in Attributes().Matches(markup))
        {
            List<string> into = attribute.Groups["name"].Value.Equals("class", StringComparison.OrdinalIgnoreCase) ? classes : ids;
            // A word Razor writes (@expression) or one that carries a Razor expression is no name the stylesheet can know.
            foreach (string word in attribute.Groups["value"].Value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Identifier().IsMatch(word) && !into.Contains(word, StringComparer.Ordinal))
                {
                    into.Add(word);
                }
            }
        }
        return (elements, classes, ids);
    }

    /// <summary>Where the name typed at a place of a stylesheet starts, and what comes before it ('.' for a class, '#' for an
    /// id, none for an element), where a selector is written there; null inside a rule's braces, a comment or a string.</summary>
    public static (int Start, char? Mark)? SelectorAt(string css, int caret)
    {
        if (caret < 0 || caret > css.Length)
        {
            return null;
        }
        int depth = 0;
        int i = 0;
        while (i < caret)
        {
            char c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0 || end + 2 > caret)
                {
                    return null;
                }
                i = end + 2;
                continue;
            }
            if (c is '"' or '\'')
            {
                int end = css.IndexOf(c, i + 1);
                if (end < 0 || end >= caret)
                {
                    return null;
                }
                i = end + 1;
                continue;
            }
            depth += c == '{' ? 1 : c == '}' && depth > 0 ? -1 : 0;
            i++;
        }
        // Inside braces, only an at-rule's body (@media) holds selectors.
        if (depth > 0 && !InAtRule(css, caret))
        {
            return null;
        }
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(css[start - 1]) || css[start - 1] is '-' or '_'))
        {
            start--;
        }
        char? mark = start > 0 && css[start - 1] is '.' or '#' ? css[start - 1] : null;
        if (mark is null && start > 0 && css[start - 1] == ':')
        {
            return null;
        }
        return (start, mark);
    }

    /// <summary>Whether the innermost braces open before a place are an at-rule's (<c>@media (...) {</c>), whose body holds rules.</summary>
    private static bool InAtRule(string css, int caret)
    {
        int open = css.LastIndexOf('{', Math.Max(0, caret - 1));
        if (open < 0 || css.IndexOf('}', open, caret - open) >= 0)
        {
            return false;
        }
        int head = open == 0 ? 0 : css.LastIndexOfAny(['{', '}', ';'], open - 1) + 1;
        return css[head..open].TrimStart().StartsWith('@');
    }

    [GeneratedRegex(@"<([a-z][a-z0-9]*(?:-[a-z0-9]+)*)(?=[\s/>])", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\b(?<name>class|id)\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Attributes();

    [GeneratedRegex(@"^-?[A-Za-z_][\w-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
