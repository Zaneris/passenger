using System.Text.RegularExpressions;

namespace Passenger.Text;

/// <summary>The lines that begin in text whose leading whitespace a language's reader keeps though a grammar's strings don't
/// show it, each by the language's own rule: a Dockerfile's heredocs, Godot's quoted values, the text between XML's tags
/// that holds words, HTML's preformatted elements. Each errs towards a line kept: an end never found runs to the text's. Pure.</summary>
public static partial class LiteralLines
{
    [GeneratedRegex("""<<(?<strip>-)?(?<quote>["']?)(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\k<quote>""")]
    private static partial Regex Heredoc();

    /// <summary>The lines of each heredoc a Dockerfile's line opens, its own delimiter's line with them, those of one line's
    /// several in turn.</summary>
    public static IReadOnlySet<int> Heredocs(TextSnapshot text)
    {
        HashSet<int> lines = [];
        for (int line = 0; line < text.LineCount; line++)
        {
            string written = text.GetLineText(line);
            if (written.TrimStart().StartsWith('#'))
            {
                continue;
            }
            foreach (Match opened in Heredoc().Matches(written))
            {
                string name = opened.Groups["name"].Value;
                bool strips = opened.Groups["strip"].Success;
                while (++line < text.LineCount)
                {
                    lines.Add(line);
                    string now = text.GetLineText(line).TrimEnd('\r');
                    if ((strips ? now.TrimStart('\t') : now) == name)
                    {
                        break;
                    }
                }
            }
        }
        return lines;
    }

    /// <summary>The lines that begin inside a double-quoted value of Godot's text resources, a built-in script's or shader's
    /// source among them, which a backslash escapes a quote in.</summary>
    public static IReadOnlySet<int> GodotStrings(TextSnapshot text)
    {
        HashSet<int> lines = [];
        string all = text.ToString();
        bool inside = false;
        int line = 0;
        for (int at = 0; at < all.Length; at++)
        {
            char c = all[at];
            if (c == '\n')
            {
                line++;
                if (inside)
                {
                    lines.Add(line);
                }
            }
            else if (inside && c == '\\')
            {
                at++;
            }
            else if (c == '"')
            {
                inside = !inside;
            }
        }
        return lines;
    }

    /// <summary>The lines that begin in XML's text with words in it, which a reader keeps whole (a resource's value, an element
    /// written over lines); never text of whitespace alone, which is the file's own indentation.</summary>
    public static IReadOnlySet<int> XmlText(TextSnapshot text) => InMarkup(text, html: false);

    /// <summary>The lines that begin inside HTML's <c>pre</c> and <c>textarea</c>, the elements that show their whitespace.</summary>
    public static IReadOnlySet<int> Preformatted(TextSnapshot text) => InMarkup(text, html: true);

    private static HashSet<int> InMarkup(TextSnapshot text, bool html)
    {
        HashSet<int> lines = [];
        string all = text.ToString();
        int preformatted = 0;
        int at = 0;
        while (at < all.Length)
        {
            int open = all.IndexOf('<', at);
            int end = open < 0 ? all.Length : open;
            if (preformatted > 0 || (!html && all.AsSpan(at, end - at).Trim().Length > 0))
            {
                Between(text, at, end, lines);
            }
            at = open < 0 ? all.Length : Past(all, open, html, ref preformatted);
        }
        return lines;
    }

    /// <summary>Where what begins at a <c>&lt;</c> ends: a comment, a CDATA section, a declaration, or a tag, whose quoted values
    /// may hold a <c>&gt;</c>; an HTML script's or style's text is passed over with it, and a preformatted element counted.</summary>
    private static int Past(string all, int open, bool html, ref int preformatted)
    {
        ReadOnlySpan<char> from = all.AsSpan(open);
        if (from.StartsWith("<!--"))
        {
            return After(all, "-->", open);
        }
        if (from.StartsWith("<![CDATA["))
        {
            return After(all, "]]>", open);
        }
        if (from.StartsWith("<?"))
        {
            return After(all, "?>", open);
        }
        int close = open + 1;
        char quote = '\0';
        while (close < all.Length && (all[close] != '>' || quote != '\0'))
        {
            quote = quote != '\0' ? (all[close] == quote ? '\0' : quote) : all[close] is '"' or '\'' ? all[close] : '\0';
            close++;
        }
        close = Math.Min(close + 1, all.Length);
        if (!html)
        {
            return close;
        }
        bool closing = open + 1 < all.Length && all[open + 1] == '/';
        int name = open + (closing ? 2 : 1);
        int nameEnd = name;
        while (nameEnd < all.Length && char.IsAsciiLetterOrDigit(all[nameEnd]))
        {
            nameEnd++;
        }
        ReadOnlySpan<char> tag = all.AsSpan(name, nameEnd - name);
        if (tag.Equals("pre", StringComparison.OrdinalIgnoreCase) || tag.Equals("textarea", StringComparison.OrdinalIgnoreCase))
        {
            preformatted = Math.Max(0, preformatted + (closing ? -1 : 1));
        }
        else if (!closing && (tag.Equals("script", StringComparison.OrdinalIgnoreCase) || tag.Equals("style", StringComparison.OrdinalIgnoreCase)))
        {
            int ending = all.IndexOf("</" + tag.ToString(), close, StringComparison.OrdinalIgnoreCase);
            return ending < 0 ? all.Length : ending;
        }
        return close;
    }

    private static int After(string all, string ending, int from)
    {
        int found = all.IndexOf(ending, from, StringComparison.Ordinal);
        return found < 0 ? all.Length : found + ending.Length;
    }

    /// <summary>The lines starting after a place and before another.</summary>
    private static void Between(TextSnapshot text, int start, int end, HashSet<int> lines)
    {
        for (int line = text.GetLineFromOffset(start) + 1; line < text.LineCount && text.GetLineStart(line) < end; line++)
        {
            lines.Add(line);
        }
    }
}
