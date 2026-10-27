using System.Buffers;

namespace Passenger.Text;

/// <summary>Which markup a file is, for the editing that reads its tags: XML (XAML, MSBuild), or HTML, whose void
/// elements never close and whose script and style hold text that is not markup.</summary>
public enum MarkupKind
{
    None,
    Xml,
    Html,
}

/// <summary>The indentation Enter gives a new line in markup, from the tags before the caret: a level in from the
/// element the caret is inside, the element's own on a line its end tag starts, and under a tag's first attribute
/// inside the tag. HTML's elements whose end tag may be left out (li, p, td, ...) close as its parser has them. The text is
/// read into a pooled buffer and its tags' names kept as places in it, so an Enter makes no string of the text or a tag.</summary>
internal static class MarkupIndent
{
    /// <summary>The text an XML file's Enter reads back from the caret first, then four times as much, and so on, until it
    /// reaches the elements that hold the caret: a key costs the elements it is in, not the file before it.</summary>
    private const int FirstReach = 4096;

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "textarea", "title" };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> RawText = RawTextElements.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The start tags that close an open p (HTML's "close a p element" in button scope).</summary>
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "dd", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "listing",
        "main", "menu", "nav", "ol", "p", "plaintext", "pre", "search", "section", "summary", "table", "ul", "xmp",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ParagraphClosers = ClosesParagraph.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The elements no implied end reaches past (most of HTML's scope markers).</summary>
    private static readonly string[] Scopes = ["html", "body", "table", "td", "th", "caption", "template", "object", "applet", "marquee"];

    private static readonly string[] ListBounds = ["ul", "ol", "menu", .. Scopes];
    private static readonly string[] TableBounds = ["table", "template", "html"];
    private static readonly string[] DefinitionBounds = ["dl", .. Scopes];
    private static readonly string[] SelectBounds = ["select", "datalist", "template"];
    private static readonly string[] GroupBounds = ["select", "template"];
    private static readonly string[] ParagraphBounds = ["button", .. Scopes];

    /// <summary>The new line's indentation for Enter at <paramref name="at"/> whose line goes on at <paramref name="rest"/>,
    /// with the end tag's own line's when the caret sits between a start tag and an end tag; null where Enter keeps the
    /// line's indentation (in a comment, a quoted value, a declaration, a script).</summary>
    public static (string Indent, string? EndTagIndent)? ForNewLine(TextSnapshot text, int at, int rest, int limit, MarkupKind kind, IndentOptions indent)
    {
        bool endTagFollows = limit - rest >= 2 && text[rest] == '<' && text[rest + 1] == '/';
        string? following = endTagFollows ? EndTagAt(text, rest, limit) : null;
        // HTML's left-out end tags depend on what holds an element as far out as the file's start; XML's tags pair up.
        Place place = kind == MarkupKind.Xml ? Back(text, at, following) : ReadWhole(text, at, kind, following);
        switch (place.Kind)
        {
            case PlaceKind.Content:
            {
                string parent = place.Parent < 0 ? string.Empty : LineIndent(text, place.Parent);
                string child = place.Parent < 0 ? string.Empty : parent + indent.Unit;
                if (!endTagFollows)
                {
                    return (child, null);
                }
                // The end tag goes to its own element's indentation, which an HTML element's left-out end tag makes an outer one's.
                string closing = place.Closer < 0 ? parent : LineIndent(text, place.Closer);
                return place.Opened ? (child, closing) : (closing, null);
            }
            case PlaceKind.Tag:
            {
                LineSpan line = text.GetLine(text.GetLineFromOffset(place.Tag));
                return place.Attribute >= 0
                    ? (indent.Whitespace(indent.Columns(text.GetText(line.Start, place.Attribute - line.Start))), null)
                    : (LineIndent(text, place.Tag) + indent.Unit, null);
            }
            default:
                return null;
        }
    }

    private enum PlaceKind
    {
        Content,
        Tag,
        Other,
    }

    /// <summary>Where a caret is among the tags: in an element's content (<see cref="Parent"/> its start tag's offset, -1 at
    /// the top; <see cref="Opened"/> when only whitespace follows that start tag; <see cref="Closer"/> the start tag of the
    /// open element the end tag after the caret closes, -1 with none), in a start tag (<see cref="Tag"/> its offset;
    /// <see cref="Attribute"/> its first attribute's, when that is on the tag's line before the caret), or other.</summary>
    private readonly record struct Place(PlaceKind Kind, int Parent = -1, bool Opened = false, int Tag = -1, int Attribute = -1, int Closer = -1);

    /// <summary>An element open at a place of the text read forwards: its name's place in the text, its start tag's and where
    /// its content begins.</summary>
    private readonly record struct Open(int NameStart, int NameLength, int Start, int Inside);

    /// <summary>The name of the end tag that starts at <paramref name="at"/>, null while it has none yet.</summary>
    private static string? EndTagAt(TextSnapshot text, int at, int limit)
    {
        int end = at + 2;
        while (end < limit && end - at < 256 && MarkupNames.IsNameChar(text[end]))
        {
            end++;
        }
        return end > at + 2 ? text.GetText(at + 2, end - at - 2) : null;
    }

    /// <summary>The place at the caret read forwards from the file's start, the text before it read into a pooled buffer.</summary>
    private static Place ReadWhole(TextSnapshot text, int at, MarkupKind kind, string? following)
    {
        char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, at));
        try
        {
            Span<char> before = chars.AsSpan(0, at);
            text.CopyTo(0, before);
            return Read(before, 0, kind, following);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>An XML file's place read back from the caret, a stretch at a time, as far as the elements that hold it; read
    /// from the file's start as a whole where what it found back there does not pair up (an end tag with no start). Each
    /// stretch is read into a pooled buffer.</summary>
    private static Place Back(TextSnapshot text, int at, string? following)
    {
        for (int reach = FirstReach; ; reach *= 4)
        {
            int from = Math.Max(0, at - reach);
            char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, at - from));
            try
            {
                Span<char> s = chars.AsSpan(0, at - from);
                text.CopyTo(from, s);
                if (Back(s, from, from == 0, following) is { } place && (from == 0 || !Closes(text, at, Math.Min(reach, text.Length - at))))
                {
                    return place;
                }
                if (from == 0)
                {
                    return Read(s, 0, MarkupKind.Xml, following);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }
    }

    /// <summary>Whether the text after the caret ends a comment or CDATA section it does not begin, one the caret may be in
    /// though its beginning is further back than was read. Only a caret further than a stretch from both ends of one (an
    /// inline task's long code) is read as the markup it holds.</summary>
    private static bool Closes(TextSnapshot text, int at, int length)
    {
        char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, length));
        try
        {
            Span<char> after = chars.AsSpan(0, length);
            text.CopyTo(at, after);
            return Ends(after, "<!--", "-->") || Ends(after, "<![CDATA[", "]]>");
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static bool Ends(ReadOnlySpan<char> after, string opener, string closer)
    {
        int end = after.IndexOf(closer);
        return end >= 0 && after[..end].IndexOf(opener) < 0;
    }

    /// <summary>Where <paramref name="value"/> last is in <paramref name="s"/> wholly at or before <paramref name="last"/>.</summary>
    private static int LastBefore(ReadOnlySpan<char> s, string value, int last) => s[..(last + 1)].LastIndexOf(value);

    /// <summary>Where <paramref name="value"/> first is in <paramref name="s"/> from <paramref name="from"/> on, wholly before
    /// <paramref name="end"/>.</summary>
    private static int IndexIn(ReadOnlySpan<char> s, string value, int from, int end) => s[from..end].IndexOf(value) is var at and >= 0 ? from + at : -1;

    /// <summary>Whether the '&lt;' at <paramref name="lt"/> follows a tag that has ended (its '>' outside its quoted values,
    /// which MSBuild's item transforms put '->' in), not one it would be inside of (typed among another tag's attributes);
    /// unknown, and so false, where the stretch holds no tag before it.</summary>
    private static bool AfterTag(ReadOnlySpan<char> s, int lt, bool whole)
    {
        int before = lt == 0 ? -1 : s[..lt].LastIndexOf('<');
        if (before < 0)
        {
            return whole;
        }
        string? closer = s[before..].StartsWith("<!--") ? "-->" : s[before..].StartsWith("<![CDATA[") ? "]]>" : s[before..].StartsWith("<?") ? "?>" : null;
        if (closer is not null)
        {
            return IndexIn(s, closer, before + 2, lt) >= 0;
        }
        return TagEnd(s, before + 1, lt).End < lt;
    }

    /// <summary>The place at the end of <paramref name="s"/> (the text from <paramref name="from"/> to the caret), read tag
    /// by tag backwards; null where it needs the text before <paramref name="s"/>, or (<paramref name="whole"/>) where the
    /// tags do not pair up, which only reading forwards from the start settles.</summary>
    private static Place? Back(ReadOnlySpan<char> s, int from, bool whole, string? following)
    {
        // The caret inside a comment, CDATA section or processing instruction begun in the stretch: the line's indentation.
        foreach ((string opener, string ender) in (ReadOnlySpan<(string, string)>)[("<!--", "-->"), ("<![CDATA[", "]]>"), ("<?", "?>")])
        {
            if (Unclosed(s, opener, ender) is >= 0 and var begun)
            {
                return AfterTag(s, begun, whole) ? new Place(PlaceKind.Other) : null;
            }
        }
        // The end tags read back that no start tag has paired yet, by their names' places, as a stack.
        List<(int Start, int Length)>? closed = null;
        int innermost = -1;
        int inside = -1;
        int p = s.Length;
        while (true)
        {
            int lt = p == 0 ? -1 : s[..p].LastIndexOf('<');
            if (lt < 0)
            {
                // The file's start: whatever was found open, or the top level; an end tag left unpaired needs the forward read.
                return !whole || closed is { Count: > 0 } ? null : Found(s, from, innermost, inside, -1);
            }
            // A comment, CDATA section or processing instruction that ends before what was read after it, or a '<' inside one.
            if (Within(s, lt, p, "<!--", "-->") is { } comment)
            {
                if (comment < 0)
                {
                    return null;
                }
                p = comment;
                continue;
            }
            if ((Within(s, lt, p, "<![CDATA[", "]]>") ?? Within(s, lt, p, "<?", "?>")) is { } section)
            {
                if (section < 0)
                {
                    return null;
                }
                p = section;
                continue;
            }
            // Any other '<' follows a tag that has ended: one among a tag's attributes is that tag's, which reads forwards.
            if (!AfterTag(s, lt, whole))
            {
                return null;
            }
            if (s[lt..].StartsWith("<!"))
            {
                // A declaration (DOCTYPE), whose '>' ends it before what was read after it.
                if (MarkupNames.DeclarationEnd(s[..p], lt) < 0)
                {
                    return p == s.Length ? new Place(PlaceKind.Other) : null;
                }
                p = lt;
                continue;
            }
            bool endTag = lt + 1 < p && s[lt + 1] == '/';
            int nameStart = lt + (endTag ? 2 : 1);
            int nameEnd = nameStart;
            while (nameEnd < p && MarkupNames.IsNameChar(s[nameEnd]))
            {
                nameEnd++;
            }
            if (nameEnd == nameStart)
            {
                // A '<' just typed is a tag begun, where Enter keeps the line's indentation; any other is text.
                if (nameStart >= s.Length)
                {
                    return new Place(PlaceKind.Other);
                }
                p = lt;
                continue;
            }
            (int gt, char quote, int attribute) = TagEnd(s, nameEnd, p);
            if (gt == p)
            {
                // A tag that runs into what was read after it was misread; one that runs to the caret holds it.
                if (p != s.Length)
                {
                    return null;
                }
                bool onTagLine = attribute >= 0 && s[lt..attribute].IndexOfAny('\r', '\n') < 0;
                return quote != '\0' || endTag ? new Place(PlaceKind.Other) : new Place(PlaceKind.Tag, Tag: from + lt, Attribute: onTagLine ? from + attribute : -1);
            }
            p = lt;
            ReadOnlySpan<char> name = s[nameStart..nameEnd];
            if (endTag)
            {
                (closed ??= []).Add((nameStart, nameEnd - nameStart));
                continue;
            }
            if (s[gt - 1] == '/')
            {
                continue;
            }
            if (closed is { Count: > 0 })
            {
                // An end tag that is not this element's: only the whole file read forwards says which it closes.
                if (!s.Slice(closed[^1].Start, closed[^1].Length).SequenceEqual(name))
                {
                    return null;
                }
                closed.RemoveAt(closed.Count - 1);
                continue;
            }
            // Open at the caret: the innermost first, then those holding it until the one the end tag after the caret closes.
            if (innermost < 0)
            {
                innermost = lt;
                inside = gt + 1;
            }
            if (following is null || name.SequenceEqual(following))
            {
                return Found(s, from, innermost, inside, following is null ? -1 : lt);
            }
        }
    }

    /// <summary>The caret in an element's content read back: the innermost element open at it (none at the top level) and the
    /// one an end tag after the caret closes.</summary>
    private static Place Found(ReadOnlySpan<char> s, int from, int start, int inside, int closer) => start < 0
        ? new Place(PlaceKind.Content)
        : new Place(PlaceKind.Content, Parent: from + start, Opened: s[inside..].IsWhiteSpace(), Closer: closer < 0 ? -1 : from + closer);

    /// <summary>Where the last <paramref name="opener"/> in <paramref name="s"/> is when no <paramref name="closer"/> follows
    /// it, else -1.</summary>
    private static int Unclosed(ReadOnlySpan<char> s, string opener, string closer)
    {
        int open = s.LastIndexOf(opener);
        return open >= 0 && s[(open + opener.Length)..].IndexOf(closer) < 0 ? open : -1;
    }

    /// <summary>Where the construct holding a '&lt;' at <paramref name="lt"/> begins, when one ends between it and
    /// <paramref name="p"/>; -1 when its beginning is not in <paramref name="s"/> or does not pair with that end, null with none.</summary>
    private static int? Within(ReadOnlySpan<char> s, int lt, int p, string opener, string closer)
    {
        int end = IndexIn(s, closer, lt + 1, p);
        if (end < 0)
        {
            return null;
        }
        int start = LastBefore(s, opener, lt);
        return start >= 0 && IndexIn(s, closer, start + opener.Length, s.Length) == end ? start : -1;
    }

    /// <summary>A tag's '>' (outside its quoted values) from its name's end, <paramref name="limit"/> where it has none
    /// before it, with the quote still open there and its first attribute's offset (-1 with none).</summary>
    private static (int End, char Quote, int Attribute) TagEnd(ReadOnlySpan<char> s, int from, int limit)
    {
        char quote = '\0';
        int attribute = -1;
        int gt = from;
        for (; gt < limit; gt++)
        {
            char c = s[gt];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                break;
            }
            else if (attribute < 0 && !char.IsWhiteSpace(c) && c != '/')
            {
                attribute = gt;
            }
        }
        return (gt, quote, attribute);
    }

    /// <summary>The place at the end of <paramref name="s"/>, read forwards from the file's start (<paramref name="from"/> 0).</summary>
    private static Place Read(ReadOnlySpan<char> s, int from, MarkupKind kind, string? following)
    {
        StringComparison names = kind == MarkupKind.Html ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<Open> open = [];
        int i = 0;
        while (i < s.Length && s[i..].IndexOf('<') is var found and >= 0)
        {
            int lt = i + found;
            if (Skip(s, lt, "<!--", "-->") is { } comment)
            {
                if (comment < 0)
                {
                    return new Place(PlaceKind.Other);
                }
                i = comment;
                continue;
            }
            if ((Skip(s, lt, "<![CDATA[", "]]>") ?? Skip(s, lt, "<?", "?>") ?? (s[lt..].StartsWith("<!") ? MarkupNames.DeclarationEnd(s, lt) : null)) is { } declaration)
            {
                if (declaration < 0)
                {
                    return new Place(PlaceKind.Other);
                }
                i = declaration;
                continue;
            }
            bool endTag = lt + 1 < s.Length && s[lt + 1] == '/';
            int nameStart = lt + (endTag ? 2 : 1);
            int nameEnd = nameStart;
            while (nameEnd < s.Length && MarkupNames.IsNameChar(s[nameEnd]))
            {
                nameEnd++;
            }
            if (nameEnd == nameStart)
            {
                // A '<' just typed is a tag begun, where Enter keeps the line's indentation; any other is text.
                if (nameStart >= s.Length)
                {
                    return new Place(PlaceKind.Other);
                }
                i = lt + 1;
                continue;
            }
            ReadOnlySpan<char> name = s[nameStart..nameEnd];
            (int gt, char quote, int attribute) = TagEnd(s, nameEnd, s.Length);
            if (gt == s.Length)
            {
                bool onTagLine = attribute >= 0 && s[lt..attribute].IndexOfAny('\r', '\n') < 0;
                return quote != '\0' || endTag ? new Place(PlaceKind.Other) : new Place(PlaceKind.Tag, Tag: from + lt, Attribute: onTagLine ? from + attribute : -1);
            }
            i = gt + 1;
            if (endTag)
            {
                int match = LastOpen(s, open, name, names);
                if (match >= 0)
                {
                    open.RemoveRange(match, open.Count - match);
                }
                continue;
            }
            if (kind == MarkupKind.Html)
            {
                CloseImplied(s, open, name);
            }
            if (s[gt - 1] == '/' || kind == MarkupKind.Html && MarkupNames.IsVoid(name))
            {
                continue;
            }
            open.Add(new Open(nameStart, nameEnd - nameStart, lt, gt + 1));
            if (kind == MarkupKind.Html && RawText.Contains(name))
            {
                int close = EndTagOf(s, name, gt + 1);
                if (close < 0)
                {
                    return new Place(PlaceKind.Other);
                }
                i = close;
            }
        }
        if (open.Count == 0)
        {
            return new Place(PlaceKind.Content);
        }
        Open innermost = open[^1];
        int closer = following is null ? -1 : LastOpen(s, open, following, names);
        return new Place(PlaceKind.Content, Parent: from + innermost.Start, Opened: s[innermost.Inside..].IsWhiteSpace(), Closer: closer < 0 ? -1 : from + open[closer].Start);
    }

    /// <summary>The last open element of a name, -1 with none.</summary>
    private static int LastOpen(ReadOnlySpan<char> s, List<Open> open, ReadOnlySpan<char> name, StringComparison names)
    {
        for (int k = open.Count - 1; k >= 0; k--)
        {
            if (s.Slice(open[k].NameStart, open[k].NameLength).Equals(name, names))
            {
                return k;
            }
        }
        return -1;
    }

    /// <summary>Where the first end tag of a raw text element ("&lt;/script", any case) is from <paramref name="from"/> on, -1
    /// with none.</summary>
    private static int EndTagOf(ReadOnlySpan<char> s, ReadOnlySpan<char> name, int from)
    {
        for (int at = from; at + 2 + name.Length <= s.Length && s[at..].IndexOf("</") is var found and >= 0; at += found + 1)
        {
            int tag = at + found;
            if (tag + 2 + name.Length <= s.Length && s.Slice(tag + 2, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }
        return -1;
    }

    /// <summary>The open elements a start tag ends first, as HTML's parser ends those whose end tag may be left out: an li
    /// by the next li, a p by a block, a cell by the next cell or row, and so on, never past what bounds each (the list,
    /// the table, the select), in the order HTML's parser ends them.</summary>
    private static void CloseImplied(ReadOnlySpan<char> s, List<Open> open, ReadOnlySpan<char> started)
    {
        Span<char> lowered = started.Length <= 64 ? stackalloc char[started.Length] : new char[started.Length];
        started.ToLowerInvariant(lowered);
        switch (lowered)
        {
            case "li":
                End(s, open, ["li"], ListBounds);
                break;
            case "dt" or "dd":
                End(s, open, ["dt", "dd"], DefinitionBounds);
                break;
            case "td" or "th":
                End(s, open, ["td", "th"], TableBounds);
                break;
            case "tr":
                End(s, open, ["td", "th"], TableBounds);
                End(s, open, ["tr"], TableBounds);
                break;
            case "tbody" or "thead" or "tfoot":
                End(s, open, ["td", "th"], TableBounds);
                End(s, open, ["tr"], TableBounds);
                End(s, open, ["tbody", "thead", "tfoot"], TableBounds);
                break;
            case "option":
                End(s, open, ["option"], SelectBounds);
                break;
            case "optgroup":
                End(s, open, ["option"], SelectBounds);
                End(s, open, ["optgroup"], GroupBounds);
                break;
            case "rt" or "rp":
                End(s, open, ["rt", "rp"], ["ruby"]);
                break;
        }
        if (ParagraphClosers.Contains(lowered))
        {
            End(s, open, ["p"], ParagraphBounds);
        }
    }

    /// <summary>Ends the innermost open element named among <paramref name="ends"/> and all inside it, unless one named
    /// among <paramref name="bounds"/> comes first; the names compared in lower case.</summary>
    private static void End(ReadOnlySpan<char> s, List<Open> open, ReadOnlySpan<string> ends, ReadOnlySpan<string> bounds)
    {
        Span<char> lowered = stackalloc char[64];
        for (int k = open.Count - 1; k >= 0; k--)
        {
            ReadOnlySpan<char> name = s.Slice(open[k].NameStart, open[k].NameLength);
            if (name.Length > lowered.Length)
            {
                // Longer than any name HTML ends or bounds by.
                continue;
            }
            Span<char> lower = lowered[..name.Length];
            name.ToLowerInvariant(lower);
            if (Among(lower, ends))
            {
                open.RemoveRange(k, open.Count - k);
                break;
            }
            if (Among(lower, bounds))
            {
                break;
            }
        }
    }

    private static bool Among(ReadOnlySpan<char> name, ReadOnlySpan<string> names)
    {
        foreach (string one in names)
        {
            if (name.SequenceEqual(one))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Past a construct starting at <paramref name="at"/> with <paramref name="opener"/>: after its
    /// <paramref name="closer"/>, -1 when the text ends inside it, null when it does not start there.</summary>
    private static int? Skip(ReadOnlySpan<char> s, int at, string opener, string closer)
    {
        if (!s[at..].StartsWith(opener))
        {
            return null;
        }
        int end = IndexIn(s, closer, at + opener.Length, s.Length);
        return end < 0 ? -1 : end + closer.Length;
    }

    private static string LineIndent(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        return text.GetText(line.Start, TextNavigation.IndentEnd(text, line) - line.Start);
    }
}
