using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A component's or page's scoped stylesheet (Counter.razor.css beside Counter.razor) and what its selectors may name:
/// the HTML elements its markup's start tags write (a component's tag, which takes no scope of its own, left out), the classes
/// their <c>class</c> attributes give and the ids their <c>id</c> attributes give, each written as plain text; and whether a
/// place in the stylesheet is where a selector is written, outside comments and a rule's declarations.</summary>
public static partial class ScopedCss
{
    /// <summary>The markup a scoped stylesheet styles, by its path; null for a stylesheet of no component or page.</summary>
    public static string? MarkupOf(string path) =>
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && RazorFiles.Is(path[..^4]) ? path[..^4] : null;

    /// <summary>The names a component's markup gives its selectors, each once in the order written.</summary>
    public static (IReadOnlyList<string> Elements, IReadOnlyList<string> Classes, IReadOnlyList<string> Ids) Names(string markup)
    {
        List<string> elements = [];
        List<string> classes = [];
        List<string> ids = [];
        foreach (MarkupTag tag in MarkupTags.Read(markup, razor: true))
        {
            if (Element().IsMatch(tag.Name) && !elements.Contains(tag.Name, StringComparer.Ordinal))
            {
                elements.Add(tag.Name);
            }
            foreach (MarkupAttribute attribute in tag.Attributes)
            {
                bool givesClasses = attribute.Name.Equals("class", StringComparison.OrdinalIgnoreCase);
                if (!givesClasses && !attribute.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                List<string> into = givesClasses ? classes : ids;
                // A word Razor writes (@expression) or one that carries a Razor expression is no name the stylesheet can know.
                foreach (string word in markup[attribute.ValueStart..attribute.ValueEnd].Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Identifier().IsMatch(word) && !into.Contains(word, StringComparer.Ordinal))
                    {
                        into.Add(word);
                    }
                }
            }
        }
        return (elements, classes, ids);
    }

    /// <summary>Where the name typed at a place of a stylesheet starts, and what comes before it ('.' for a class, '#' for an
    /// id, none for an element), where a selector is written there; null inside a rule's declarations, a comment or a string.</summary>
    public static (int Start, char? Mark)? SelectorAt(string css, int caret)
    {
        if (caret < 0 || caret > css.Length)
        {
            return null;
        }
        int depth = 0;
        int statement = 0;
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
                statement = css.AsSpan(statement, i - statement).IsWhiteSpace() ? end + 2 : statement;
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
            statement = c is '{' or '}' or ';' ? i + 1 : statement;
            i++;
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
        // Inside braces, an at-rule's body (@media) holds selectors, and a rule's those of the rules nested in it.
        if (depth > 0 && !InAtRule(css, caret) && !IsNestedSelector(css.AsSpan(statement, start - statement - (mark is null ? 0 : 1)).Trim(), mark))
        {
            return null;
        }
        return (start, mark);
    }

    /// <summary>Whether a name in a rule's body, after <paramref name="before"/> in its statement, is a nested rule's selector, no
    /// declaration: marked at the statement's start, after what no property starts with, or with no ':' to make it a value. A
    /// name alone may yet be a property.</summary>
    private static bool IsNestedSelector(ReadOnlySpan<char> before, char? mark) =>
        before.IsEmpty ? mark is not null : before[0] is '&' or '.' or '#' or '>' or '+' or '~' or '[' or '*' or ':' || !before.Contains(':');

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

    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Element();

    [GeneratedRegex(@"^-?[A-Za-z_][\w-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
