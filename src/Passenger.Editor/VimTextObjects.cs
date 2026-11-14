using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The text an operator takes: from <see cref="Start"/> to before <see cref="End"/>, or the whole lines they are
/// on (<see cref="Linewise"/>).</summary>
public readonly record struct VimSpan(int Start, int End, bool Linewise);

/// <summary>Vim's text objects, the "inner" and "a" ones, as pure functions of the text and a caret: words, WORDs,
/// sentences, paragraphs, bracketed blocks, quoted strings and tag blocks. Null where there is no such object.</summary>
public static class VimTextObjects
{
    /// <summary>The object a key after i or a names (w W s p, a bracket or b B, a quote, t) around the caret, the
    /// <paramref name="n"/>th one out for blocks and tags.</summary>
    public static VimSpan? Of(TextSnapshot text, int caret, string key, bool inner, int n, bool breakEnds) => key switch
    {
        "w" => Word(text, caret, n, inner, bigWord: false, breakEnds),
        "W" => Word(text, caret, n, inner, bigWord: true, breakEnds),
        "s" => Sentence(text, caret, n, inner),
        "p" => Paragraph(text, caret, n, inner, breakEnds),
        "(" or ")" or "b" => Block(text, caret, n, inner, '(', ')'),
        "{" or "}" or "B" => Block(text, caret, n, inner, '{', '}'),
        "[" or "]" => Block(text, caret, n, inner, '[', ']'),
        "<" or ">" => Block(text, caret, n, inner, '<', '>'),
        "\"" or "'" or "`" => Quoted(text, caret, key[0], inner),
        "t" => Tag(text, caret, n, inner),
        _ => null,
    };

    private static bool White(char c) => c is ' ' or '\t';

    /// <summary>The run of one kind (word, punctuation, white) the offset is in, within its line.</summary>
    private static (int Start, int End) Run(TextSnapshot text, int offset, bool bigWord)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        if (offset >= line.End)
        {
            return (offset, offset);
        }
        int kind = White(text[offset]) ? 0 : VimMotions.Kind(text[offset], bigWord);
        int start = offset;
        while (start > line.Start && KindIn(text, start - 1, bigWord) == kind)
        {
            start--;
        }
        int end = offset + 1;
        while (end < line.End && KindIn(text, end, bigWord) == kind)
        {
            end++;
        }
        return (start, end);
    }

    /// <summary>Where the run of one kind (word, punctuation, white) the offset is in begins on its line.</summary>
    public static int RunStart(TextSnapshot text, int offset, bool bigWord) => Run(text, offset, bigWord).Start;

    private static int KindIn(TextSnapshot text, int offset, bool bigWord) => White(text[offset]) ? 0 : VimMotions.Kind(text[offset], bigWord);

    /// <summary>iw, aw, iW and aW: <paramref name="n"/> words, white between them counted too for the inner object; the
    /// outer one takes the white after, or before where none is after or the caret was in white.</summary>
    private static VimSpan? Word(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds)
    {
        LineSpan line = VimMotions.LineOf(text, caret);
        if (line.Length == 0)
        {
            return inner ? new VimSpan(caret, caret, false) : null;
        }
        (int start, int end) = Run(text, caret, bigWord);
        bool onWhite = KindIn(text, caret, bigWord) == 0;
        if (inner)
        {
            for (int i = 1; i < n; i++)
            {
                int next = NextRunEnd(text, end, bigWord, breakEnds);
                if (next == end)
                {
                    return null;
                }
                end = next;
            }
            return new VimSpan(start, end, false);
        }
        if (onWhite)
        {
            // White before a word: the white and the word after it, n times.
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    end = WhiteEnd(text, end);
                }
                int next = NextRunEnd(text, end, bigWord, breakEnds);
                if (next == end)
                {
                    return null;
                }
                end = next;
            }
            return new VimSpan(start, end, false);
        }
        int wordEnd = end;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                wordEnd = NextRunEnd(text, WhiteEnd(text, wordEnd), bigWord, breakEnds);
            }
            end = WhiteEnd(text, wordEnd);
            if (i + 1 < n && end == wordEnd)
            {
                wordEnd = end;
            }
        }
        if (end == wordEnd)
        {
            // No white after: the white before goes instead, unless it is the line's indent.
            int white = start;
            while (white > line.Start && White(text[white - 1]))
            {
                white--;
            }
            start = white > line.Start ? white : start;
        }
        return new VimSpan(start, end, false);
    }

    /// <summary>The end of the white run starting at the offset on its line, or the offset itself.</summary>
    private static int WhiteEnd(TextSnapshot text, int offset)
    {
        int lineEnd = VimMotions.LineOf(text, offset).End;
        int p = offset;
        while (p < lineEnd && White(text[p]))
        {
            p++;
        }
        return p;
    }

    /// <summary>The end of the run after the one ending at the offset, the next line's first where the line ends there.</summary>
    private static int NextRunEnd(TextSnapshot text, int offset, bool bigWord, bool breakEnds)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        if (offset >= line.End)
        {
            if (line.Line >= VimMotions.LastLine(text, breakEnds))
            {
                return offset;
            }
            LineSpan next = text.GetLine(line.Line + 1);
            return next.Length == 0 ? next.Start : Run(text, next.Start, bigWord).End;
        }
        return Run(text, offset, bigWord).End;
    }

    /// <summary>is and as: the sentence the caret is in, or the white after it; the outer one takes the white after, or
    /// before where none is after.</summary>
    private static VimSpan? Sentence(TextSnapshot text, int caret, int n, bool inner)
    {
        int start = caret < text.Length ? VimMotions.PreviousSentenceStart(text, caret + 1) : VimMotions.PreviousSentenceStart(text, caret);
        int end = start;
        bool onWhite = false;
        int bodyEnd = BodyEnd(text, start);
        if (caret >= bodyEnd)
        {
            onWhite = true;
            start = bodyEnd;
        }
        for (int i = 0; i < n; i++)
        {
            int body = BodyEnd(text, end == start && !onWhite ? start : end);
            int next = SentenceAfter(text, end);
            if (onWhite)
            {
                end = inner && i == 0 ? next : (i == 0 ? BodyEnd(text, next) : BodyEnd(text, SentenceAfter(text, end)));
                if (!inner && i == 0)
                {
                    continue;
                }
                continue;
            }
            end = inner ? (i % 2 == 0 ? body : next) : next;
        }
        if (!inner && !onWhite && end == BodyEnd(text, start) && end == SentenceBodyLimit(text, end))
        {
            while (start > VimMotions.LineOf(text, start).Start && White(text[start - 1]))
            {
                start--;
            }
        }
        return new VimSpan(start, Math.Max(start, end), false);
    }

    private static int SentenceAfter(TextSnapshot text, int offset)
    {
        int next = VimMotions.NextSentenceStart(text, offset);
        return next <= offset ? text.Length : next;
    }

    /// <summary>Where a sentence's text ends, before the white that leads to the next one.</summary>
    private static int BodyEnd(TextSnapshot text, int start)
    {
        int end = SentenceAfter(text, start);
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        return end;
    }

    private static int SentenceBodyLimit(TextSnapshot text, int offset)
    {
        int end = offset;
        while (end < text.Length && White(text[end]))
        {
            end++;
        }
        return end;
    }

    /// <summary>ip and ap, linewise: <paramref name="n"/> runs of lines with text or of blank ones (white alone counts as
    /// blank here); the outer one takes the blank lines after each paragraph, or before the first where none come after.
    /// A count running past the text fails.</summary>
    private static VimSpan? Paragraph(TextSnapshot text, int caret, int n, bool inner, bool breakEnds)
    {
        int last = VimMotions.LastLine(text, breakEnds);
        int first = text.GetLineFromOffset(caret);
        bool blank = BlankLine(text, first);
        while (first > 0 && BlankLine(text, first - 1) == blank)
        {
            first--;
        }
        int end = RunEnd(text, first, last);
        int runs = inner ? n : n * 2;
        for (int i = 1; i < runs; i++)
        {
            if (end >= last)
            {
                if (inner || i < runs - 1 || blank)
                {
                    return null;
                }
                // No blank lines after the last paragraph: the ones before the first go instead.
                while (first > 0 && BlankLine(text, first - 1))
                {
                    first--;
                }
                break;
            }
            end = RunEnd(text, end + 1, last);
        }
        return new VimSpan(text.GetLineStart(first), text.GetLineStart(end), true);
    }

    /// <summary>The last line of the run of lines alike (blank or not) that begins at the line.</summary>
    private static int RunEnd(TextSnapshot text, int line, int last)
    {
        bool blank = BlankLine(text, line);
        while (line < last && BlankLine(text, line + 1) == blank)
        {
            line++;
        }
        return line;
    }

    /// <summary>ip and ap carrying a visual stretch over several lines on: from its cursor's line <paramref name="n"/>
    /// runs of lines with text or of blank ones further (ap a run of each), up or down, as far as the text's edge.</summary>
    public static int ParagraphsOn(TextSnapshot text, int line, bool up, int n, bool inner, bool breakEnds)
    {
        int edge = up ? 0 : VimMotions.LastLine(text, breakEnds);
        int step = up ? -1 : 1;
        for (int i = 0; i < n; i++)
        {
            if (line == edge)
            {
                break;
            }
            bool? before = null;
            for (int t = 0; t < 2; t++)
            {
                line += step;
                bool blank = BlankLine(text, line);
                if (before == blank)
                {
                    line -= step;
                    break;
                }
                while (line != edge && BlankLine(text, line + step) == blank)
                {
                    line += step;
                }
                if (inner || line == edge)
                {
                    break;
                }
                before = blank;
            }
        }
        return line;
    }

    private static bool BlankLine(TextSnapshot text, int line)
    {
        LineSpan span = text.GetLine(line);
        return TextNavigation.IndentEnd(text, span) >= span.End;
    }

    /// <summary>The first <paramref name="open"/> from an offset on, -1 where none is or a <paramref name="close"/> comes
    /// first, ending the block the offset is in.</summary>
    private static int Ahead(TextSnapshot text, int from, char open, char close)
    {
        for (int p = from; p < text.Length; p++)
        {
            if (text[p] == open)
            {
                return p;
            }
            if (text[p] == close)
            {
                return -1;
            }
        }
        return -1;
    }

    /// <summary>A bracketed block, the <paramref name="n"/>th around the caret (a bracket under it counting as its own),
    /// or outside any the <paramref name="n"/>th ahead before a closing bracket; inner, a block whose brackets end and begin lines takes only the
    /// lines between, linewise.</summary>
    private static VimSpan? Block(TextSnapshot text, int caret, int n, bool inner, char open, char close)
    {
        int at = caret < text.Length && text[caret] == close ? Match(text, caret, open, close, forward: false) : caret;
        if (at < 0)
        {
            return null;
        }
        int start = -1;
        int from = at < text.Length && text[at] == open ? at : at - 1;
        bool ahead = false;
        for (int i = 0; i < n; i++)
        {
            start = ahead ? Ahead(text, start + 1, open, close) : Unclosed(text, i == 0 ? from : start - 1, open, close);
            if (start < 0 && i == 0)
            {
                // Outside any block, the first one ahead, and a count's more the next ones ahead.
                ahead = true;
                start = Ahead(text, caret, open, close);
            }
            if (start < 0)
            {
                return null;
            }
        }
        int end = Match(text, start, open, close, forward: true);
        if (end < 0)
        {
            return null;
        }
        if (!inner)
        {
            return new VimSpan(start, end + 1, false);
        }
        int innerStart = start + 1;
        int innerEnd = end;
        LineSpan openLine = VimMotions.LineOf(text, start);
        LineSpan closeLine = VimMotions.LineOf(text, end);
        if (innerStart == openLine.End && closeLine.Line > openLine.Line)
        {
            innerStart = openLine.EndIncludingBreak;
        }
        if (closeLine.Line > openLine.Line && TextNavigation.IndentEnd(text, closeLine) >= end)
        {
            if (innerStart == openLine.EndIncludingBreak && closeLine.Line - 1 >= openLine.Line + 1)
            {
                return new VimSpan(innerStart, text.GetLineStart(closeLine.Line - 1), true);
            }
            innerEnd = closeLine.Line - 1 >= 0 ? text.GetLine(closeLine.Line - 1).End : innerEnd;
            innerEnd = Math.Max(innerEnd, innerStart);
        }
        return new VimSpan(innerStart, innerEnd, false);
    }

    /// <summary>The nearest unclosed <paramref name="open"/> at or before the offset, or -1.</summary>
    private static int Unclosed(TextSnapshot text, int offset, char open, char close)
    {
        int depth = 0;
        for (int p = Math.Min(offset, text.Length - 1); p >= 0; p--)
        {
            if (text[p] == close)
            {
                depth++;
            }
            else if (text[p] == open)
            {
                if (depth == 0)
                {
                    return p;
                }
                depth--;
            }
        }
        return -1;
    }

    private static int Match(TextSnapshot text, int offset, char open, char close, bool forward)
    {
        int depth = 0;
        for (int p = offset; forward ? p < text.Length : p >= 0; p += forward ? 1 : -1)
        {
            if (text[p] == open)
            {
                depth += forward ? 1 : -1;
            }
            else if (text[p] == close)
            {
                depth += forward ? -1 : 1;
            }
            if (depth == 0)
            {
                return p;
            }
        }
        return -1;
    }

    /// <summary>A quoted string on the caret's line: the one the caret is in or on, else the first after it; the outer
    /// one takes its quotes and the white after them, or before where none is after.</summary>
    private static VimSpan? Quoted(TextSnapshot text, int caret, char quote, bool inner)
    {
        LineSpan line = VimMotions.LineOf(text, caret);
        List<int> quotes = [];
        for (int p = line.Start; p < line.End; p++)
        {
            if (text[p] == '\\')
            {
                p++;
                continue;
            }
            if (text[p] == quote)
            {
                quotes.Add(p);
            }
        }
        int open = -1;
        int close = -1;
        int on = quotes.IndexOf(caret);
        if (on >= 0)
        {
            (open, close) = on % 2 == 0 ? (caret, on + 1 < quotes.Count ? quotes[on + 1] : -1) : (quotes[on - 1], caret);
        }
        else
        {
            int before = quotes.FindLastIndex(q => q < caret);
            int after = quotes.FindIndex(q => q > caret);
            if (before >= 0 && after >= 0)
            {
                (open, close) = (quotes[before], quotes[after]);
            }
            else if (after >= 0 && after + 1 < quotes.Count)
            {
                (open, close) = (quotes[after], quotes[after + 1]);
            }
        }
        if (open < 0 || close < 0)
        {
            return null;
        }
        if (inner)
        {
            return new VimSpan(open + 1, close, false);
        }
        int start = open;
        int end = close + 1;
        int white = end;
        while (white < line.End && White(text[white]))
        {
            white++;
        }
        if (white > end)
        {
            end = white;
        }
        else
        {
            while (start > line.Start && White(text[start - 1]))
            {
                start--;
            }
        }
        return new VimSpan(start, end, false);
    }

    /// <summary>A tag block, the <paramref name="n"/>th around the caret: inner, what is between its start and end tags;
    /// outer, the tags too. Names match whatever their case; a tag closed on itself (&lt;br/&gt;) is no block.</summary>
    private static VimSpan? Tag(TextSnapshot text, int caret, int n, bool inner)
    {
        int from = caret;
        if (TagAt(text, caret) is { } on && !on.Closing && !on.SelfClosed)
        {
            from = on.End;
        }
        else if (TagAt(text, caret) is { Closing: true } closing)
        {
            from = closing.Start;
        }
        int found = 0;
        List<string> ended = [];
        for (int p = Math.Min(from, text.Length) - 1; p >= 0; p--)
        {
            if (text[p] != '<' || TagStartingAt(text, p) is not { } tag)
            {
                continue;
            }
            if (tag.Closing)
            {
                ended.Add(tag.Name);
                continue;
            }
            if (tag.SelfClosed || tag.End > from && tag.Start != p)
            {
                continue;
            }
            int unmatched = ended.FindLastIndex(name => string.Equals(name, tag.Name, StringComparison.OrdinalIgnoreCase));
            if (unmatched >= 0)
            {
                ended.RemoveAt(unmatched);
                continue;
            }
            if (EndTagOf(text, tag) is not { } end)
            {
                continue;
            }
            if (end.End <= caret && !(caret < end.End && caret >= end.Start))
            {
                continue;
            }
            if (++found == n)
            {
                return inner ? new VimSpan(tag.End, end.Start, false) : new VimSpan(tag.Start, end.End, false);
            }
        }
        return null;
    }

    private sealed record TagRead(int Start, int End, string Name, bool Closing, bool SelfClosed);

    /// <summary>The tag whose angle brackets take in the offset, if any.</summary>
    private static TagRead? TagAt(TextSnapshot text, int offset)
    {
        for (int p = Math.Min(offset, text.Length - 1); p >= 0 && offset - p < 4096; p--)
        {
            if (text[p] == '>' && p < offset)
            {
                return null;
            }
            if (text[p] == '<')
            {
                return TagStartingAt(text, p) is { } tag && tag.End > offset ? tag : null;
            }
        }
        return null;
    }

    private static TagRead? TagStartingAt(TextSnapshot text, int start)
    {
        int p = start + 1;
        bool closing = p < text.Length && text[p] == '/';
        if (closing)
        {
            p++;
        }
        int nameStart = p;
        while (p < text.Length && (char.IsLetterOrDigit(text[p]) || text[p] is '-' or '_' or ':' or '.'))
        {
            p++;
        }
        if (p == nameStart)
        {
            return null;
        }
        string name = text.GetText(nameStart, p - nameStart);
        char? inQuote = null;
        for (; p < text.Length; p++)
        {
            char c = text[p];
            if (inQuote is { } q)
            {
                inQuote = c == q ? null : inQuote;
            }
            else if (c is '"' or '\'')
            {
                inQuote = c;
            }
            else if (c == '<')
            {
                return null;
            }
            else if (c == '>')
            {
                return new TagRead(start, p + 1, name, closing, !closing && text[p - 1] == '/');
            }
        }
        return null;
    }

    /// <summary>The end tag that closes a start tag, other blocks of its name between them nested.</summary>
    private static TagRead? EndTagOf(TextSnapshot text, TagRead start)
    {
        int depth = 0;
        for (int p = start.End; p < text.Length; p++)
        {
            if (text[p] != '<' || TagStartingAt(text, p) is not { } tag || !string.Equals(tag.Name, start.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (tag.Closing)
            {
                if (depth == 0)
                {
                    return tag;
                }
                depth--;
            }
            else if (!tag.SelfClosed)
            {
                depth++;
            }
            p = tag.End - 1;
        }
        return null;
    }
}
