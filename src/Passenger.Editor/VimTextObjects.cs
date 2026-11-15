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
        "\"" or "'" or "`" => Quoted(text, caret, key[0], inner, withQuotes: inner && n > 1),
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
        int kind = KindIn(text, offset, bigWord);
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

    private static int KindIn(TextSnapshot text, int offset, bool bigWord) => White(text[offset]) ? 0 : VimMotions.KindAt(text, offset, bigWord);

    /// <summary>iw, aw, iW and aW as an operator takes them: the places <see cref="WordsWalked"/> walks over, an end
    /// it leaves at a later line's start ending at the line before, the lines whole where they begin in an indent.</summary>
    private static VimSpan? Word(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds)
    {
        if (WordsWalked(text, caret, n, inner, bigWord, breakEnds, out _) is not { } walked)
        {
            return null;
        }
        (int start, int end, bool exclusive) = walked;
        LineSpan endLine = VimMotions.LineOf(text, end);
        if (!exclusive)
        {
            // An empty line's place adds nothing of its own.
            return new VimSpan(start, endLine.Length == 0 ? end : Math.Max(end + 1, Math.Min(TextNavigation.NextGrapheme(text, end), endLine.End)), false);
        }
        LineSpan startLine = VimMotions.LineOf(text, start);
        if (end != endLine.Start || endLine.Line <= startLine.Line)
        {
            return new VimSpan(start, end, false);
        }
        LineSpan before = text.GetLine(endLine.Line - 1);
        return VimMotions.InIndent(text, start)
            ? new VimSpan(startLine.Start, before.Start, true)
            : new VimSpan(start, before.Length == 0 ? before.Start : before.End, false);
    }

    /// <summary>Where a count of words cut short by the text's end left off, where the walk stopped; null where the
    /// object is found.</summary>
    public static int? WordsRanOut(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds) =>
        WordsWalked(text, caret, n, inner, bigWord, breakEnds, out int ranOut) is null ? ranOut : null;

    /// <summary>iw, aw, iW and aW in visual mode from a single place: the places walked over, the last one taken, a
    /// count the text cuts short stopping where it ran out.</summary>
    public static (int Start, int End)? WordsShown(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds) =>
        WordsWalked(text, caret, n, inner, bigWord, breakEnds, out _, visual: true) is { } walked ? (walked.Start, walked.End) : null;

    /// <summary>The place after one, on the line below past a line's last character; null at the text's last place, but
    /// in visual mode (<paramref name="visual"/>) the last line's end is one more.</summary>
    private static int? NextPlace(TextSnapshot text, int place, bool breakEnds, bool visual = false)
    {
        LineSpan line = VimMotions.LineOf(text, place);
        int next = line.Length == 0 || place >= line.End ? line.End : TextNavigation.NextGrapheme(text, place);
        if (next < line.End)
        {
            return next;
        }
        if (line.Line < VimMotions.LastLine(text, breakEnds))
        {
            return line.EndIncludingBreak;
        }
        return visual && line.Length > 0 && place < line.End ? line.End : null;
    }

    /// <summary>The place before one, on the line above's last character before a line's first; null at the text's start.</summary>
    private static int? PlaceBefore(TextSnapshot text, int place)
    {
        LineSpan line = VimMotions.LineOf(text, place);
        if (place > line.Start)
        {
            return VimMotions.StepBack(text, Math.Min(place, line.End));
        }
        if (line.Line == 0)
        {
            return null;
        }
        LineSpan above = text.GetLine(line.Line - 1);
        return above.Length == 0 ? above.Start : VimMotions.StepBack(text, above.End);
    }

    /// <summary>Whether a place is a blank, the walks' white; a line's end past its last character counts as one.</summary>
    private static bool BlankAt(TextSnapshot text, int place, bool bigWord) =>
        !EmptyAt(text, place) && (place >= VimMotions.LineOf(text, place).End || KindIn(text, place, bigWord) == 0);

    private static bool EmptyAt(TextSnapshot text, int place) => VimMotions.LineOf(text, place).Length == 0;

    /// <summary>The last place of the run of one kind (word, punctuation, blank) a place is in, within its line.</summary>
    private static int RunLast(TextSnapshot text, int place, bool bigWord) =>
        EmptyAt(text, place) || place >= VimMotions.LineOf(text, place).End ? place : VimMotions.StepBack(text, Run(text, place, bigWord).End);

    /// <summary>iw, aw, iW and aW walked a place at a time (a character, or an empty line): iw by runs, a step onto an
    /// empty line going on to the next place, which an operator stops before (<c>Exclusive</c>); aw by a run and its
    /// blanks on its line, or from blanks (an empty line first) over blanks and line breaks to a run or empty line, with
    /// the blanks before where it begins and ends on runs. Null where the text runs out (<paramref name="ranOut"/> the
    /// last place walked), but in visual mode that stops the walk there, the last line's end one more place.</summary>
    private static (int Start, int End, bool Exclusive)? WordsWalked(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds,
        out int ranOut, bool visual = false)
    {
        ranOut = caret;
        LineSpan line = VimMotions.LineOf(text, caret);
        bool empty = line.Length == 0;
        if (inner && empty && line.Line > 0 && NextPlace(text, caret, breakEnds) is null)
        {
            // On an empty last line, iw reaches back to the line above's last place; with a count, to nothing.
            LineSpan above = text.GetLine(line.Line - 1);
            int from = above.Length == 0 ? above.Start : VimMotions.StepBack(text, above.End);
            return n == 1 ? (from, caret, false) : (caret, caret, false);
        }
        int start = empty ? caret : Run(text, caret, bigWord).Start;
        int end = empty ? caret : RunLast(text, caret, bigWord);
        bool exclusive = false;
        if (inner)
        {
            for (long i = 1; i < n; i++)
            {
                if (NextPlace(text, end, breakEnds, visual) is not { } next)
                {
                    ranOut = end;
                    return visual ? (start, end, exclusive) : null;
                }
                (end, exclusive) = !EmptyAt(text, next) ? (RunLast(text, next, bigWord), false)
                    : NextPlace(text, next, breakEnds, visual) is { } after ? (after, true) : (next, true);
            }
            return (start, end, exclusive);
        }
        for (long i = 0; i < n; i++)
        {
            int at = start;
            if (i > 0)
            {
                if (NextPlace(text, end, breakEnds, visual) is not { } next)
                {
                    ranOut = end;
                    return visual ? (start, end, false) : null;
                }
                at = next;
            }
            if (EmptyAt(text, at) || BlankAt(text, at, bigWord))
            {
                // Blanks and line breaks up to a run or an empty line, which ends the unit.
                int white = at;
                int? word = NextPlace(text, white, breakEnds, visual);
                while (word is { } w && BlankAt(text, w, bigWord) && w < VimMotions.LineOf(text, w).End)
                {
                    white = w;
                    word = NextPlace(text, white, breakEnds, visual);
                }
                if (word is not { } found)
                {
                    ranOut = white;
                    if (!visual)
                    {
                        return null;
                    }
                    end = white;
                    break;
                }
                if (visual && i == 0 && found >= VimMotions.LineOf(text, found).End && !EmptyAt(text, found))
                {
                    // Blanks ending the text are taken from the caret, up to the last line's end.
                    start = caret;
                }
                end = RunLast(text, found, bigWord);
                continue;
            }
            end = RunLast(text, at, bigWord);
            if (NextPlace(text, end, breakEnds, visual) is { } after && VimMotions.LineOf(text, after).Line == VimMotions.LineOf(text, end).Line
                && BlankAt(text, after, bigWord) && after < VimMotions.LineOf(text, after).End)
            {
                end = RunLast(text, after, bigWord);
            }
        }
        if (!empty && !BlankAt(text, start, bigWord) && !EmptyAt(text, end) && !BlankAt(text, end, bigWord))
        {
            // Ending on a run, the blanks before the first go too, unless they are the line's indent.
            int white = start;
            while (white > line.Start && White(text[white - 1]))
            {
                white--;
            }
            start = white > line.Start ? white : start;
        }
        return (start, end, false);
    }

    /// <summary>iw, aw, iW and aW carrying a longer visual stretch's cursor on, forward or <paramref name="back"/>, as
    /// <see cref="WordsWalked"/> walks (back, a run's blanks before it), the last line's end a place but in linewise mode
    /// (<paramref name="lines"/>), a walk back to the text's very start stopping at its second character where Vim's
    /// does. Where the text runs out the cursor stays; <c>Moved</c> false where it could not move at all, <c>Whole</c>
    /// whether the count was walked whole.</summary>
    public static (int To, bool Moved, bool Whole) WordsCarried(TextSnapshot text, int cursor, bool back, int n, bool inner, bool bigWord, bool breakEnds, bool lines)
    {
        int at = cursor;
        for (long i = 0; i < n; i++)
        {
            if ((back ? PlaceBefore(text, at) : NextPlace(text, at, breakEnds, visual: !lines)) is not { } q)
            {
                return (at, i > 0, false);
            }
            if (inner)
            {
                at = EmptyAt(text, q) ? (back ? q : NextPlace(text, q, breakEnds, visual: !lines) ?? q)
                    : back ? RunStartWalked(text, q, bigWord, stopsShort: BlankAt(text, q, bigWord)) : RunLast(text, q, bigWord);
                continue;
            }
            if (EmptyAt(text, q) || BlankAt(text, q, bigWord))
            {
                int white = q;
                int? word = back ? PlaceBefore(text, white) : NextPlace(text, white, breakEnds, visual: !lines);
                while (word is { } w && BlankAt(text, w, bigWord) && w < VimMotions.LineOf(text, w).End)
                {
                    white = w;
                    word = back ? PlaceBefore(text, white) : NextPlace(text, white, breakEnds, visual: !lines);
                }
                if (word is not { } found)
                {
                    return (white, true, false);
                }
                at = EmptyAt(text, found) ? found : back ? Run(text, found, bigWord).Start : RunLast(text, found, bigWord);
                continue;
            }
            int edge = back ? RunStartWalked(text, q, bigWord, stopsShort: true) : RunLast(text, q, bigWord);
            if ((back ? PlaceBefore(text, edge) : NextPlace(text, edge, breakEnds, visual: !lines)) is { } beyond
                && VimMotions.LineOf(text, beyond).Line == VimMotions.LineOf(text, edge).Line && BlankAt(text, beyond, bigWord) && beyond < VimMotions.LineOf(text, beyond).End)
            {
                // The blanks before a run walked back stop short of the text's very start too.
                edge = !back ? RunLast(text, beyond, bigWord) : Run(text, beyond, bigWord).Start is var blanks && blanks == 0 ? TextNavigation.NextGrapheme(text, 0) : blanks;
            }
            at = edge;
        }
        return (at, true, true);
    }

    /// <summary>Where a run walked back from a place begins: its start, but its second character where that start is the
    /// text's very first, the walk began past it and it <paramref name="stopsShort"/>.</summary>
    private static int RunStartWalked(TextSnapshot text, int place, bool bigWord, bool stopsShort)
    {
        if (EmptyAt(text, place))
        {
            return place;
        }
        int start = Run(text, place, bigWord).Start;
        return stopsShort && start == 0 && place > 0 ? TextNavigation.NextGrapheme(text, 0) : start;
    }

    /// <summary>is and as: the sentence the caret is in, or the white after it; the outer one takes the white after, or
    /// before where none is after. On a run of empty lines the sentence is the run's last line.</summary>
    private static VimSpan? Sentence(TextSnapshot text, int caret, int n, bool inner)
    {
        int start = caret < text.Length ? VimMotions.PreviousSentenceStart(text, caret + 1) : VimMotions.PreviousSentenceStart(text, caret);
        LineSpan on = VimMotions.LineOf(text, caret);
        if (on.Length == 0)
        {
            int runEnd = on.Line;
            while (runEnd + 1 < text.LineCount && text.GetLine(runEnd + 1).Length == 0 && text.GetLineStart(runEnd + 1) < text.Length)
            {
                runEnd++;
            }
            start = text.GetLineStart(runEnd);
        }
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
        int bodyOfStart = BodyEnd(text, start);
        if (!inner && !onWhite && (end == bodyOfStart || BreaksOnly(text, bodyOfStart, end)) && end == SentenceBodyLimit(text, end))
        {
            while (start > VimMotions.LineOf(text, start).Start && White(text[start - 1]))
            {
                start--;
            }
        }
        end = Math.Max(start, end);
        LineSpan last = VimMotions.LineOf(text, end);
        if (end == last.End && last.Length > 0 && last.BreakLength > 0 && last.Line + 1 < text.LineCount
            && text.GetLine(last.Line + 1) is var below && (below.Length == 0 || !char.IsWhiteSpace(text[below.Start])))
        {
            // Text ending at a line's end runs to the next sentence where that begins the next line, as its first column.
            end = last.EndIncludingBreak;
        }
        return new VimSpan(start, end, false);
    }

    /// <summary>A sentence object's stretch as an operator takes it: an end in a later line's first column ends at the
    /// end of the line before, and from a line's indent the stretch is whole lines, as Vim's exclusive end there is.</summary>
    public static VimSpan EndingInFirstColumn(TextSnapshot text, VimSpan span)
    {
        LineSpan last = VimMotions.LineOf(text, span.End);
        if (span.Linewise || span.End <= span.Start || span.End != last.Start || last.Line <= VimMotions.LineOf(text, span.Start).Line)
        {
            return span;
        }
        return VimMotions.InIndent(text, span.Start)
            ? new VimSpan(VimMotions.LineOf(text, span.Start).Start, text.GetLineStart(last.Line - 1), true)
            : span with { End = text.GetLine(last.Line - 1).End };
    }

    /// <summary>Whether only line breaks stand from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static bool BreaksOnly(TextSnapshot text, int from, int to)
    {
        for (int p = from; p < to; p++)
        {
            if (text[p] is not ('\r' or '\n'))
            {
                return false;
            }
        }
        return from < to;
    }

    private static int SentenceAfter(TextSnapshot text, int offset)
    {
        int next = VimMotions.NextSentenceStart(text, offset);
        return next <= offset ? text.Length : next;
    }

    /// <summary>Where a sentence's text ends, before the white that leads to the next one; an empty line's past its break.</summary>
    private static int BodyEnd(TextSnapshot text, int start)
    {
        LineSpan line = VimMotions.LineOf(text, start);
        if (line.Length == 0 && line.Start == start && line.BreakLength > 0)
        {
            return line.EndIncludingBreak;
        }
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

    /// <summary>ip and ap carrying a visual stretch on from its cursor's line: <paramref name="n"/> runs of lines with
    /// text or of blank ones further (ap two runs for each), up to the first line of each run or down to its last; a run
    /// the line is inside counts as one. They stop at the text's edge, never failing.</summary>
    public static int ParagraphsOn(TextSnapshot text, int line, bool up, int n, bool inner, bool breakEnds)
    {
        int last = VimMotions.LastLine(text, breakEnds);
        long runs = inner ? n : 2L * n;
        for (long i = 0; i < runs; i++)
        {
            int next = up ? line - 1 : line + 1;
            if (next < 0 || next > last)
            {
                break;
            }
            line = up ? RunStartOf(text, next) : RunEnd(text, next, last);
        }
        return line;
    }

    /// <summary>Whether the line is a run of lines alike (blank or not) alone, its neighbours unlike it.</summary>
    public static bool RunOfOneLine(TextSnapshot text, int line, bool breakEnds)
    {
        bool blank = BlankLine(text, line);
        return (line == 0 || BlankLine(text, line - 1) != blank) && (line >= VimMotions.LastLine(text, breakEnds) || BlankLine(text, line + 1) != blank);
    }

    /// <summary>The first line of the run of lines alike (blank or not) that the line is in.</summary>
    private static int RunStartOf(TextSnapshot text, int line)
    {
        bool blank = BlankLine(text, line);
        while (line > 0 && BlankLine(text, line - 1) == blank)
        {
            line--;
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
    /// one takes its quotes and the white after them, or before where none is after; an inner one with a count its
    /// quotes alone (<paramref name="withQuotes"/>).</summary>
    private static VimSpan? Quoted(TextSnapshot text, int caret, char quote, bool inner, bool withQuotes = false)
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
            return withQuotes ? new VimSpan(open, close + 1, false) : new VimSpan(open + 1, close, false);
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
