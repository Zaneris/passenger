namespace Passenger.Workspace;

/// <summary>An attribute of a start tag: its name, and its value's place between its quotes (none for one without a value).</summary>
public readonly record struct MarkupAttribute(string Name, int NameStart, int ValueStart, int ValueEnd);

/// <summary>A start tag: its name, where its '&lt;' is, and its attributes in the order written.</summary>
public sealed record MarkupTag(string Name, int Start, IReadOnlyList<MarkupAttribute> Attributes)
{
    public MarkupAttribute? Attribute(string name) =>
        Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } found ? found : null;
}

/// <summary>A class a markup file writes where a class is written, and where its name is.</summary>
public readonly record struct WrittenClass(string Name, int Start);

/// <summary>The start tags of HTML, a Razor component or a Razor page or view, read without a parser: comments, a script's
/// and a style's text and Razor's <c>@code</c> blocks passed over; a Razor value's expressions (<c>@x</c>,
/// <c>@(…)</c>, <c>@Assets["…"]</c>) read whole, their strings' quotes inside the value's. What a tag's class attributes
/// write (<see cref="Classes"/>): <c>class</c>'s words, a component's <c>Class</c> or <c>CssClass</c>, and in Razor the
/// words of an expression's string literals, a word that runs into an expression being none it names. Pure.</summary>
public static class MarkupTags
{
    /// <summary>The attributes whose value is a list of classes.</summary>
    private static readonly HashSet<string> ClassAttributes = new(StringComparer.OrdinalIgnoreCase) { "class", "CssClass" };

    /// <summary>Elements whose text is not markup.</summary>
    private static readonly string[] RawText = ["script", "style"];

    /// <summary>Every start tag of the text.</summary>
    public static IReadOnlyList<MarkupTag> Read(string text, bool razor)
    {
        List<MarkupTag> tags = [];
        int i = 0;
        while (i < text.Length)
        {
            int lt = text.IndexOfAny(razor ? ['<', '@'] : ['<'], i);
            if (lt < 0 || lt + 1 >= text.Length)
            {
                break;
            }
            if (text[lt] == '@')
            {
                i = razor ? RazorSkip(text, lt) : lt + 1;
                continue;
            }
            if (text.AsSpan(lt).StartsWith("<!--"))
            {
                int end = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 3;
                continue;
            }
            if (!char.IsAsciiLetter(text[lt + 1]))
            {
                i = lt + 1;
                continue;
            }
            (MarkupTag tag, int after) = Tag(text, lt, razor);
            tags.Add(tag);
            i = after;
            if (RawText.Contains(tag.Name, StringComparer.OrdinalIgnoreCase))
            {
                int close = text.IndexOf("</" + tag.Name, after, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? text.Length : close;
            }
        }
        return tags;
    }

    /// <summary>The start tag the place is in, read up to there: null outside every tag.</summary>
    public static MarkupTag? At(string text, int offset, bool razor)
    {
        // A tag that holds the place begins at a '<' and a letter before it, as no '<' between can.
        for (int lt = text.LastIndexOf('<', Math.Max(0, Math.Min(offset, text.Length) - 1)); lt >= 0 && offset - lt < 64 * 1024; lt = lt == 0 ? -1 : text.LastIndexOf('<', lt - 1))
        {
            if (lt + 1 < text.Length && char.IsAsciiLetter(text[lt + 1]))
            {
                (MarkupTag tag, int after) = Tag(text, lt, razor);
                return offset > lt && (offset < after || after >= text.Length) ? tag : null;
            }
        }
        return null;
    }

    /// <summary>Every class the tags' class attributes write.</summary>
    public static IReadOnlyList<WrittenClass> Classes(string text, bool razor)
    {
        List<WrittenClass> found = [];
        foreach (MarkupTag tag in Read(text, razor))
        {
            foreach (MarkupAttribute attribute in tag.Attributes)
            {
                if (ClassAttributes.Contains(attribute.Name) && attribute.ValueEnd > attribute.ValueStart)
                {
                    Words(text, attribute.ValueStart, attribute.ValueEnd, razor, found);
                }
            }
        }
        return found;
    }

    /// <summary>Where the class written at a place begins and ends, the place inside a class attribute's value (on a
    /// word, after one, or where one would begin); null elsewhere, or inside an expression but its strings.</summary>
    public static (int Start, int End)? ClassAt(string text, int offset, bool razor)
    {
        if (At(text, offset, razor) is not { } tag
            || tag.Attributes.FirstOrDefault(a => ClassAttributes.Contains(a.Name) && a.ValueStart <= offset && offset <= a.ValueEnd && a.ValueEnd > 0) is not { Name.Length: > 0 } value)
        {
            return null;
        }
        List<(int Start, int End)> places = [];
        if (!Spans(text, value.ValueStart, value.ValueEnd, razor, places))
        {
            return null;
        }
        foreach ((int start, int end) in places)
        {
            if (offset < start || offset > end)
            {
                continue;
            }
            int from = offset;
            while (from > start && !char.IsWhiteSpace(text[from - 1]))
            {
                from--;
            }
            int to = offset;
            while (to < end && !char.IsWhiteSpace(text[to]))
            {
                to++;
            }
            return text.AsSpan(from, to - from).Contains('@') ? null : (from, to);
        }
        return null;
    }

    /// <summary>A tag from its '&lt;' read to its '>' (past it), and its attributes.</summary>
    private static (MarkupTag Tag, int After) Tag(string text, int lt, bool razor)
    {
        int i = lt + 1;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '-' or ':' or '.' or '_'))
        {
            i++;
        }
        string name = text[(lt + 1)..i];
        List<MarkupAttribute> attributes = [];
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '>')
            {
                return (new MarkupTag(name, lt, attributes), i + 1);
            }
            if (c == '<')
            {
                return (new MarkupTag(name, lt, attributes), i);
            }
            if (char.IsWhiteSpace(c) || c == '/')
            {
                i++;
                continue;
            }
            int nameStart = i;
            if (razor && c == '@' && i + 1 < text.Length && text[i + 1] is '(' or '{')
            {
                i = RazorSkip(text, i);
                continue;
            }
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('=' or '>' or '"' or '\'' or '<') && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>'))
            {
                i++;
            }
            string attribute = text[nameStart..i];
            int at = i;
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            if (at >= text.Length || text[at] != '=')
            {
                attributes.Add(new MarkupAttribute(attribute, nameStart, 0, 0));
                i = Math.Max(i, nameStart + 1);
                continue;
            }
            at++;
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            if (at < text.Length && text[at] is '"' or '\'')
            {
                int end = ValueEnd(text, at + 1, text[at], razor);
                attributes.Add(new MarkupAttribute(attribute, nameStart, at + 1, end));
                i = Math.Min(text.Length, end + 1);
            }
            else
            {
                int end = at;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '>')
                {
                    end = razor && text[end] == '@' ? RazorSkip(text, end) : end + 1;
                }
                attributes.Add(new MarkupAttribute(attribute, nameStart, at, end));
                i = Math.Max(end, at + 1);
            }
        }
        return (new MarkupTag(name, lt, attributes), text.Length);
    }

    /// <summary>Where a quoted value from <paramref name="from"/> ends: at its closing quote, a Razor expression's own
    /// quotes passed over; the text's end where it has none.</summary>
    private static int ValueEnd(string text, int from, char quote, bool razor)
    {
        int i = from;
        while (i < text.Length && text[i] != quote)
        {
            i = razor && text[i] == '@' ? RazorSkip(text, i) : i + 1;
        }
        return Math.Min(i, text.Length);
    }

    /// <summary>Where a Razor transition at <paramref name="at"/> ends: an escaped '@', a comment, a code block whole, an
    /// expression in brackets, or a name and what it reaches through dots, calls and indexers.</summary>
    private static int RazorSkip(string text, int at)
    {
        int i = at + 1;
        if (i >= text.Length)
        {
            return i;
        }
        char next = text[i];
        if (next == '@')
        {
            return i + 1;
        }
        if (next == '*')
        {
            int end = text.IndexOf("*@", i + 1, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + 2;
        }
        if (next is '(' or '{')
        {
            return Balanced(text, i);
        }
        if (!char.IsLetter(next) && next != '_')
        {
            return i;
        }
        int start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        {
            i++;
        }
        string keyword = text[start..i];
        if (keyword is "code" or "functions")
        {
            int brace = i;
            while (brace < text.Length && char.IsWhiteSpace(text[brace]))
            {
                brace++;
            }
            return brace < text.Length && text[brace] == '{' ? Balanced(text, brace) : i;
        }
        while (i < text.Length)
        {
            if (text[i] is '(' or '[')
            {
                i = Balanced(text, i);
            }
            else if (text[i] == '.' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }
            }
            else
            {
                break;
            }
        }
        return i;
    }

    /// <summary>Past the bracket that closes the one at <paramref name="open"/>, C#'s strings and characters passed over.</summary>
    private static int Balanced(string text, int open)
    {
        int depth = 0;
        int i = open;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                i = CSharpStringEnd(text, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' && --depth == 0)
            {
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }

    /// <summary>Past a C# string or character literal that opens at <paramref name="at"/>, verbatim (<c>@"…"</c>) where an
    /// '@' comes before it.</summary>
    private static int CSharpStringEnd(string text, int at)
    {
        char quote = text[at];
        bool verbatim = quote == '"' && at > 0 && (text[at - 1] == '@' || (at > 1 && text[at - 1] == '$' && text[at - 2] == '@'));
        int i = at + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == quote)
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            if (!verbatim && c == '\n')
            {
                return i;
            }
            i++;
        }
        return text.Length;
    }

    /// <summary>The class words of a value from <paramref name="start"/> to <paramref name="end"/>.</summary>
    private static void Words(string text, int start, int end, bool razor, List<WrittenClass> found)
    {
        List<(int Start, int End)> places = [];
        Spans(text, start, end, razor, places);
        foreach ((int from, int to) in places)
        {
            int i = from;
            while (i < to)
            {
                while (i < to && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
                int word = i;
                while (i < to && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
                if (i > word && text.AsSpan(word, i - word).IndexOfAny('@', '{', '}') < 0)
                {
                    found.Add(new WrittenClass(text[word..i], word));
                }
            }
        }
    }

    /// <summary>The stretches of a value that are written classes: its text outside expressions, a word that touches one
    /// cut off, and in Razor the text of an explicit expression's plain and verbatim strings; whether the value could be read.</summary>
    private static bool Spans(string text, int start, int end, bool razor, List<(int Start, int End)> places)
    {
        int i = start;
        int from = start;
        while (i < end)
        {
            if (!razor || text[i] != '@' || (i + 1 < end && text[i + 1] == '@'))
            {
                i += razor && text[i] == '@' ? 2 : 1;
                continue;
            }
            // A word that runs into the expression is the expression's.
            int cut = i;
            while (cut > from && !char.IsWhiteSpace(text[cut - 1]))
            {
                cut--;
            }
            places.Add((from, cut));
            int after = Math.Min(RazorSkip(text, i), end);
            // An explicit expression's strings are what it may come to; an implicit one's are its arguments.
            if (i + 1 < end && text[i + 1] == '(')
            {
                Literals(text, i, after, places);
            }
            i = after;
            while (i < end && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            from = i;
        }
        places.Add((from, end));
        return true;
    }

    /// <summary>The text of the plain and verbatim strings of an expression from <paramref name="start"/> to <paramref name="end"/>.</summary>
    private static void Literals(string text, int start, int end, List<(int Start, int End)> places)
    {
        int i = start + 1;
        while (i < end)
        {
            char c = text[i];
            if (c == '\'')
            {
                i = CSharpStringEnd(text, i);
                continue;
            }
            if (c != '"')
            {
                i++;
                continue;
            }
            int close = Math.Min(CSharpStringEnd(text, i), end);
            bool interpolated = text[i - 1] == '$' || (i > 1 && text[i - 1] == '@' && text[i - 2] == '$');
            if (!interpolated && close - 1 > i + 1)
            {
                places.Add((i + 1, close - 1));
            }
            i = close;
        }
    }
}
