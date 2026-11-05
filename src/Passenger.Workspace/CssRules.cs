using System.Text;

namespace Passenger.Workspace;

/// <summary>A class a stylesheet's rule names in its selector: the class, where its name is written, the rule's selector
/// and declarations as places in the text, and whether it is named after CSS isolation's <c>::deep</c> (in its selector or
/// that of a rule it is nested in), which a scoped stylesheet's rule reaches the components its component renders with.</summary>
public readonly record struct CssClassRule(string Name, int At, int RuleStart, int BodyStart, int BodyEnd, bool Deep = false);

/// <summary>What a stylesheet gives the markup that links it: every class its rules' selectors name, a rule nested in an
/// at-rule (<c>@media</c>, <c>@supports</c>, <c>@layer</c>) or in another rule included, and the stylesheets its
/// <c>@import</c>s name.</summary>
public sealed record CssSheet(string Text, IReadOnlyList<CssClassRule> Rules, IReadOnlyList<string> Imports)
{
    public static CssSheet Empty { get; } = new(string.Empty, [], []);

    /// <summary>Each class it names, with the rules that name it in the order written.</summary>
    public ILookup<string, CssClassRule> Classes => classes ??= Rules.ToLookup(r => r.Name, StringComparer.Ordinal);

    private ILookup<string, CssClassRule>? classes;

    /// <summary>A rule as a person reads it: its selector, then each declaration on a line of its own, the declarations cut
    /// at <paramref name="most"/>, each at <paramref name="widest"/> characters when given.</summary>
    public string Describe(CssClassRule rule, int most = 12, int? widest = null)
    {
        StringBuilder shown = new();
        shown.Append(Squeezed(Text.AsSpan(rule.RuleStart, rule.BodyStart - rule.RuleStart - 1))).Append(" {");
        int count = 0;
        foreach (string declaration in CssRules.Declarations(Text.AsSpan(rule.BodyStart, rule.BodyEnd - rule.BodyStart)))
        {
            if (++count > most)
            {
                shown.Append("\n    …");
                break;
            }
            int colon = declaration.IndexOf(':', StringComparison.Ordinal);
            string line = colon < 0 ? declaration : $"{declaration[..colon].TrimEnd()}: {declaration[(colon + 1)..].TrimStart()}";
            shown.Append("\n    ").Append(widest is { } cut && line.Length > cut ? line[..(cut - 1)] + "…" : line + ";");
        }
        return shown.Append("\n}").ToString();
    }

    /// <summary>Text with each run of whitespace one space, trimmed.</summary>
    private static string Squeezed(ReadOnlySpan<char> text)
    {
        StringBuilder squeezed = new(text.Length);
        bool space = false;
        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && squeezed.Length > 0)
            {
                squeezed.Append(' ');
            }
            space = false;
            squeezed.Append(c);
        }
        return squeezed.ToString();
    }
}

/// <summary>Reads a stylesheet's rules without a CSS engine: comments, strings and an unquoted <c>url(…)</c> passed over, a
/// selector read from the end of the rule, at-rule or declaration before it to its '{', its classes the names after each
/// '.' outside an attribute selector's brackets, escapes read as CSS reads them (<c>.md\:flex</c> is <c>md:flex</c>); a
/// keyframe's steps and a custom property's block are passed over. Pure, and as quick as one pass over the text.</summary>
public static class CssRules
{
    /// <summary>At-rules whose block holds rules, as <c>@media</c>'s does; any other's block is passed over.</summary>
    private static readonly HashSet<string> Grouping = new(StringComparer.OrdinalIgnoreCase) { "media", "supports", "layer", "container", "document", "scope", "starting-style" };

    public static CssSheet Read(string css)
    {
        List<CssClassRule> rules = [];
        List<string> imports = [];
        int i = 0;
        int depth = 0;
        int start = 0;
        // The blocks open that hold rules, a rule's own too, and whether what each holds is past a ::deep: deeper, a block is no rule's.
        Stack<bool> ruling = new();
        while (i < css.Length)
        {
            char c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                bool leading = css.AsSpan(start, i - start).IsWhiteSpace();
                i = end < 0 ? css.Length : end + 2;
                // A comment before a rule is no part of its selector.
                if (leading)
                {
                    start = i;
                }
                continue;
            }
            if (c is '"' or '\'')
            {
                i = StringEnd(css, i);
                continue;
            }
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == '(' && UnquotedUrl(css, i))
            {
                int end = css.IndexOf(')', i);
                i = end < 0 ? css.Length : end + 1;
                continue;
            }
            if (c == '{')
            {
                if (depth == ruling.Count)
                {
                    ReadOnlySpan<char> head = css.AsSpan(start, i - start).TrimStart();
                    bool within = ruling.TryPeek(out bool past) && past;
                    if (head.Length > 0 && head[0] == '@')
                    {
                        if (Grouping.Contains(AtName(head[1..])))
                        {
                            ruling.Push(within);
                        }
                    }
                    else if (!CustomProperty(head))
                    {
                        ruling.Push(Selector(css, start, i, i + 1, BlockEnd(css, i + 1), within, rules));
                    }
                }
                depth++;
                i++;
                start = i;
                continue;
            }
            if (c == '}')
            {
                if (depth > 0)
                {
                    depth--;
                }
                if (depth < ruling.Count)
                {
                    ruling.Pop();
                }
                i++;
                start = i;
                continue;
            }
            if (c == ';' && depth == ruling.Count)
            {
                if (depth == 0 && Import(css.AsSpan(start, i - start)) is { } imported)
                {
                    imports.Add(imported);
                }
                i++;
                start = i;
                continue;
            }
            i++;
        }
        // The text's end closes a statement as its ';' does.
        if (depth == 0 && Import(css.AsSpan(start)) is { } last)
        {
            imports.Add(last);
        }
        return new CssSheet(css, rules, imports);
    }

    /// <summary>Whether the '(' at a place opens a <c>url(…)</c> without quotes, whose text is an address, not CSS.</summary>
    private static bool UnquotedUrl(string css, int at)
    {
        if (at < 3 || !css.AsSpan(at - 3, 3).Equals("url", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        ReadOnlySpan<char> value = css.AsSpan(at + 1).TrimStart();
        return value.Length == 0 || value[0] is not ('"' or '\'');
    }

    /// <summary>Whether what comes before a '{' is a custom property's name, whose value a block may be: no selector.</summary>
    private static bool CustomProperty(ReadOnlySpan<char> head) => head.StartsWith("--") && head.Contains(':');

    /// <summary>The declarations of a rule's block, each trimmed, a nested block left out.</summary>
    public static IEnumerable<string> Declarations(ReadOnlySpan<char> body)
    {
        List<string> found = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c is '"' or '\'')
            {
                int end = body[(i + 1)..].IndexOf(c);
                i = end < 0 ? body.Length : i + 1 + end;
                continue;
            }
            if (c == '/' && i + 1 < body.Length && body[i + 1] == '*')
            {
                int end = body[(i + 2)..].IndexOf("*/");
                i = end < 0 ? body.Length : i + 3 + end;
                continue;
            }
            if (c is '(' or '{')
            {
                depth++;
            }
            else if (c is ')' or '}' && depth > 0)
            {
                depth--;
                if (c == '}' && depth == 0)
                {
                    start = i + 1;
                }
            }
            else if (c == ';' && depth == 0)
            {
                Add(body[start..i]);
                start = i + 1;
            }
        }
        Add(body[start..]);
        return found;

        void Add(ReadOnlySpan<char> declaration)
        {
            ReadOnlySpan<char> trimmed = declaration.Trim();
            if (trimmed.Length > 0)
            {
                found.Add(trimmed.ToString());
            }
        }
    }

    /// <summary>The classes a selector from <paramref name="start"/> to its '{' names, each once per rule, those of a rule
    /// nested past a ::deep (<paramref name="within"/>) all deep; whether what its block holds is past one.</summary>
    private static bool Selector(string css, int start, int brace, int bodyStart, int bodyEnd, bool within, List<CssClassRule> rules)
    {
        int ruleStart = start;
        while (ruleStart < brace && char.IsWhiteSpace(css[ruleStart]))
        {
            ruleStart++;
        }
        HashSet<string>? named = null;
        int bracket = 0;
        // Past a ::deep in this selector of the list: each ',' begins another.
        bool deep = within;
        bool holdsDeep = within;
        for (int i = ruleStart; i < brace; i++)
        {
            char c = css[i];
            if (c == ',' && bracket == 0)
            {
                deep = within;
            }
            else if (c == ':' && bracket == 0 && css.AsSpan(i, brace - i).StartsWith("::deep", StringComparison.OrdinalIgnoreCase))
            {
                deep = true;
                holdsDeep = true;
                i += 5;
            }
            else if (c is '"' or '\'')
            {
                i = StringEnd(css, i) - 1;
            }
            else if (c == '/' && i + 1 < brace && css[i + 1] == '*')
            {
                int end = css.IndexOf("*/", i + 2, brace - i - 2, StringComparison.Ordinal);
                i = end < 0 ? brace : end + 1;
            }
            else if (c == '[')
            {
                bracket++;
            }
            else if (c == ']' && bracket > 0)
            {
                bracket--;
            }
            else if (c == '\\')
            {
                i++;
            }
            else if (c == '.' && bracket == 0 && Name(css, i + 1, brace) is ({ Length: > 0 } name, int end))
            {
                if ((named ??= new HashSet<string>(StringComparer.Ordinal)).Add(name))
                {
                    rules.Add(new CssClassRule(name, i + 1, ruleStart, bodyStart, bodyEnd, deep));
                }
                i = end - 1;
            }
        }
        return holdsDeep;
    }

    /// <summary>A CSS identifier from <paramref name="at"/>, its escapes read, and where it ends; empty for none (a number's
    /// '.').</summary>
    public static (string Name, int End) Name(string css, int at, int limit)
    {
        StringBuilder? read = null;
        int i = at;
        while (i < limit)
        {
            char c = css[i];
            if (c == '\\' && i + 1 < limit)
            {
                read ??= new StringBuilder(css, at, i - at, 64);
                i = Escape(css, i + 1, limit, read);
                continue;
            }
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c > 0x7F))
            {
                break;
            }
            read?.Append(c);
            i++;
        }
        string name = read?.ToString() ?? css[at..i];
        // An identifier starts with a letter, '_', '-' or an escape: '.5' and '.-5' are numbers'.
        bool number = i > at && css[at] != '\\' && (char.IsAsciiDigit(css[at]) || (css[at] == '-' && i > at + 1 && char.IsAsciiDigit(css[at + 1])));
        return name.Length == 0 || name == "-" || number ? (string.Empty, i) : (name, i);
    }

    /// <summary>One escape after its backslash read into <paramref name="read"/>: up to six hex digits and a space after
    /// them, or the one character; where it ends.</summary>
    private static int Escape(string css, int at, int limit, StringBuilder read)
    {
        int i = at;
        while (i < limit && i - at < 6 && char.IsAsciiHexDigit(css[i]))
        {
            i++;
        }
        if (i == at)
        {
            read.Append(css[at]);
            return at + 1;
        }
        int code = int.Parse(css.AsSpan(at, i - at), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        read.Append(code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "�");
        return i < limit && css[i] == ' ' ? i + 1 : i;
    }

    /// <summary>Where a block whose '{' is just before <paramref name="from"/> ends: at its '}' (not past it), or the text's end.</summary>
    private static int BlockEnd(string css, int from)
    {
        int depth = 1;
        int i = from;
        while (i < css.Length)
        {
            char c = css[i];
            if (c is '"' or '\'')
            {
                i = StringEnd(css, i);
                continue;
            }
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
            i++;
        }
        return css.Length;
    }

    /// <summary>Where a quoted string that opens at <paramref name="at"/> ends: past its closing quote, or at the end of its
    /// line where it has none.</summary>
    private static int StringEnd(string css, int at)
    {
        char quote = css[at];
        int i = at + 1;
        while (i < css.Length && css[i] != quote && css[i] != '\n')
        {
            i += css[i] == '\\' ? 2 : 1;
        }
        return Math.Min(i + 1, css.Length);
    }

    private static string AtName(ReadOnlySpan<char> head)
    {
        int end = 0;
        while (end < head.Length && (char.IsAsciiLetterOrDigit(head[end]) || head[end] == '-'))
        {
            end++;
        }
        return head[..end].ToString();
    }

    /// <summary>The stylesheet an <c>@import</c> names, by <c>url(…)</c> or a string; null for another statement.</summary>
    private static string? Import(ReadOnlySpan<char> statement)
    {
        ReadOnlySpan<char> text = statement.Trim();
        if (!text.StartsWith("@import", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        text = text[7..].TrimStart();
        if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = text.IndexOf(')');
            if (close < 0)
            {
                return null;
            }
            text = text[4..close].Trim();
        }
        if (text.Length >= 2 && text[0] is '"' or '\'')
        {
            int close = text[1..].IndexOf(text[0]);
            text = close < 0 ? [] : text[1..(close + 1)];
        }
        else
        {
            int space = text.IndexOfAny(' ', '\t');
            text = space < 0 ? text : text[..space];
        }
        return text.Length > 0 ? text.ToString() : null;
    }
}
