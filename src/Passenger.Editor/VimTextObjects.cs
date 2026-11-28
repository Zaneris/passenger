using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The text an operator takes: from <see cref="Start"/> to before <see cref="End"/>, or the whole lines they are
/// on (<see cref="Linewise"/>); <see cref="EndKept"/> where an end in a later line's first column is no exclusive end.</summary>
public readonly record struct VimSpan(int Start, int End, bool Linewise, bool EndKept = false);

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
        "s" => VimMotions.SentenceObject(text, caret, n, inner, breakEnds),
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

    /// <summary>Where iw, aw, iW or aW as an operator takes them begins in the text, before whole lines are made of it.</summary>
    public static int? WordsStart(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds) =>
        WordsWalked(text, caret, n, inner, bigWord, breakEnds, out _)?.Start;

    /// <summary>Whether iw, aw, iW or aW as an operator takes them ends before its last place, as Vim's exclusive end.</summary>
    public static bool WordsExclusive(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds) =>
        WordsWalked(text, caret, n, inner, bigWord, breakEnds, out _) is { Exclusive: true };

    /// <summary>Where a count of words cut short by the text's end left off, where the walk stopped; null where the
    /// object is found.</summary>
    public static int? WordsRanOut(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds) =>
        WordsWalked(text, caret, n, inner, bigWord, breakEnds, out int ranOut) is null ? ranOut : null;

    /// <summary>iw, aw, iW and aW in visual mode from a single place: the places walked over, the last one taken, a
    /// count the text cuts short stopping where it ran out (<c>CutShort</c>).</summary>
    public static (int Start, int End, bool CutShort)? WordsShown(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds) =>
        WordsWalked(text, caret, n, inner, bigWord, breakEnds, out int ranOut, visual: true) is { } walked
            ? (walked.Start, walked.End, ranOut >= 0 || PastLastCharacter(text, walked.End, breakEnds))
            : null;

    private static bool PastLastCharacter(TextSnapshot text, int place, bool breakEnds) =>
        VimMotions.LineOf(text, place) is var line && line.Length > 0 && place >= line.End && line.Line == VimMotions.LastLine(text, breakEnds);

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
    /// the blanks before where it begins and ends on runs. Null where the text runs out (<paramref name="ranOut"/> the last
    /// place walked, -1 if none in visual mode, which stops the walk there, the last line's end one more place).</summary>
    private static (int Start, int End, bool Exclusive)? WordsWalked(TextSnapshot text, int caret, int n, bool inner, bool bigWord, bool breakEnds,
        out int ranOut, bool visual = false)
    {
        ranOut = visual ? -1 : caret;
        LineSpan line = VimMotions.LineOf(text, caret);
        bool empty = line.Length == 0;
        if (inner && EmptyLastLineReach(text, caret, breakEnds) is { } from)
        {
            // On an empty last line, iw reaches back to the line above's last place; a count of two takes nothing, more fail.
            return n == 1 ? (from, caret, false) : visual ? (caret, caret, false) : n == 2 ? (caret, caret, true) : null;
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

    /// <summary>Where iw reaches back to from an empty last line below another: the line above's last place; null elsewhere.</summary>
    public static int? EmptyLastLineReach(TextSnapshot text, int caret, bool breakEnds)
    {
        LineSpan line = VimMotions.LineOf(text, caret);
        if (line.Length > 0 || line.Line == 0 || NextPlace(text, caret, breakEnds) is not null)
        {
            return null;
        }
        LineSpan above = text.GetLine(line.Line - 1);
        return above.Length == 0 ? above.Start : VimMotions.StepBack(text, above.End);
    }

    /// <summary>iw, aw, iW and aW carrying a longer visual stretch's cursor on, forward or <paramref name="back"/>, as
    /// <see cref="WordsWalked"/> walks (back, a run's blanks before it), the last line's end a place but in linewise mode
    /// (<paramref name="lines"/>), a walk back to the text's very start stopping at its second character where Vim's
    /// does. Where the text runs out the cursor stays; <c>Moved</c> false where it could not move at all, <c>Whole</c>
    /// whether the count was walked whole, <c>Bumped</c> whether the last step's first place back was the text's start.</summary>
    public static (int To, bool Moved, bool Whole, bool Bumped) WordsCarried(TextSnapshot text, int cursor, bool back, int n, bool inner, bool bigWord, bool breakEnds, bool lines)
    {
        int at = cursor;
        bool bumped = false;
        for (long i = 0; i < n; i++)
        {
            if ((back ? PlaceBefore(text, at) : NextPlace(text, at, breakEnds, visual: !lines)) is not { } q)
            {
                return (at, i > 0, false, bumped);
            }
            bumped = back && q == 0;
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
                    // Blanks back to the text's start end the last word there.
                    return (white, true, back && i == n - 1, bumped);
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
        return (at, true, true, bumped);
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

    /// <summary>A sentence object's stretch as an operator takes it: an end in a later line's first column ends at the
    /// end of the line before, and from a line's indent the stretch is whole lines, as Vim's exclusive end there is; one
    /// kept there (<see cref="VimSpan.EndKept"/>) takes that line too from an indent, else stays as it is.</summary>
    public static VimSpan EndingInFirstColumn(TextSnapshot text, VimSpan span)
    {
        LineSpan last = VimMotions.LineOf(text, span.End);
        if (span.Linewise || span.End <= span.Start || span.End != last.Start || last.Line <= VimMotions.LineOf(text, span.Start).Line)
        {
            return span;
        }
        if (span.EndKept)
        {
            return VimMotions.InIndent(text, span.Start) ? new VimSpan(VimMotions.LineOf(text, span.Start).Start, last.Start, true) : span;
        }
        return VimMotions.InIndent(text, span.Start)
            ? new VimSpan(VimMotions.LineOf(text, span.Start).Start, text.GetLineStart(last.Line - 1), true)
            : span with { End = text.GetLine(last.Line - 1).End };
    }

    /// <summary>ip and ap, linewise: <paramref name="n"/> runs of lines with text or of blank ones (white alone counts as
    /// blank here), a macro line (<see cref="VimMotions.MacroLine"/>) beginning a run of text; the outer one takes n runs of
    /// text, each with the blank lines after it, or from blank lines those and n runs of text with those between, and where
    /// none come after the last the blank lines before the first. A count running past the text fails.</summary>
    private static VimSpan? Paragraph(TextSnapshot text, int caret, int n, bool inner, bool breakEnds) => Paragraph(text, caret, n, inner, breakEnds, linewise: false);

    /// <summary>ip and ap in linewise visual mode over one line: where the line is a run of lines alone, held already, and its
    /// paragraph takes no line above it, the line and the paragraph after it; else the paragraph around it. From the first
    /// of blank lines ap takes the blank lines after its last paragraph too while it took fewer runs of them than
    /// <paramref name="n"/>. Null where neither is.</summary>
    public static VimSpan? HeldParagraph(TextSnapshot text, int line, int n, bool inner, bool breakEnds)
    {
        int last = VimMotions.LastLine(text, breakEnds);
        VimSpan? around = Paragraph(text, text.GetLineStart(line), n, inner, breakEnds);
        bool held = RunOfOneLine(text, line, breakEnds) && line < last && (around is not { } own || text.GetLineFromOffset(own.Start) >= line);
        VimSpan? span = held ? Paragraph(text, text.GetLineStart(line + 1), n, inner, breakEnds, linewise: true) : null;
        if (span is null && BlankLine(text, line) && RunStartOf(text, line) == line)
        {
            span = Paragraph(text, text.GetLineStart(line), n, inner, breakEnds, linewise: true);
        }
        return (span ?? around) is { } found ? found with { Start = Math.Min(found.Start, text.GetLineStart(line)) } : null;
    }

    private static VimSpan? Paragraph(TextSnapshot text, int caret, int n, bool inner, bool breakEnds, bool linewise)
    {
        int last = VimMotions.LastLine(text, breakEnds);
        int first = RunStartOf(text, text.GetLineFromOffset(caret));
        bool blank = BlankLine(text, first);
        int end = RunEnd(text, first, last);
        if (inner)
        {
            for (int i = 1; i < n; i++)
            {
                if (end >= last)
                {
                    return null;
                }
                end = RunEnd(text, end + 1, last);
            }
            return new VimSpan(text.GetLineStart(first), text.GetLineStart(end), true);
        }
        int whites = blank ? 1 : 0;
        for (int texts = blank ? 0 : 1; ; texts += BlankLine(text, end) ? 0 : 1, whites += BlankLine(text, end) ? 1 : 0)
        {
            if (texts == n)
            {
                if ((!blank || (linewise && whites < n)) && end < last && BlankLine(text, end + 1))
                {
                    end = RunEnd(text, end + 1, last);
                }
                else if (!blank)
                {
                    // No blank lines after the last paragraph: the ones before the first go instead.
                    while (first > 0 && BlankLine(text, first - 1))
                    {
                        first--;
                    }
                }
                return new VimSpan(text.GetLineStart(first), text.GetLineStart(end), true);
            }
            if (end >= last)
            {
                return null;
            }
            end = RunEnd(text, end + 1, last);
        }
    }

    /// <summary>The last line of the run of lines that begins at the line (<see cref="Continues"/>).</summary>
    private static int RunEnd(TextSnapshot text, int line, int last)
    {
        while (line < last && Continues(text, line + 1))
        {
            line++;
        }
        return line;
    }

    /// <summary>Whether a line goes on with the run of lines before it: alike, blank or not, and no macro line beginning a
    /// run of text.</summary>
    private static bool Continues(TextSnapshot text, int line) =>
        BlankLine(text, line) == BlankLine(text, line - 1) && (BlankLine(text, line) || !VimMotions.MacroLine(text, line));

    /// <summary>ip and ap carrying a visual stretch on from its cursor's line: <paramref name="n"/> runs of lines
    /// (<see cref="Continues"/>) further, ap's each up to a run of text and, where that is the first it takes, the blank
    /// lines beyond it; up to the first line of each run or down to its last. They stop at the text's edge, never failing.</summary>
    public static int ParagraphsOn(TextSnapshot text, int line, bool up, int n, bool inner, bool breakEnds)
    {
        int last = VimMotions.LastLine(text, breakEnds);
        for (int i = 0; i < n; i++)
        {
            for (bool first = true; ; first = false)
            {
                if (Beyond(line) is not { } next)
                {
                    return line;
                }
                line = next;
                if (inner)
                {
                    break;
                }
                if (!BlankLine(text, line))
                {
                    line = first && Beyond(line) is { } blank && BlankLine(text, blank) ? blank : line;
                    break;
                }
            }
        }
        return line;

        // The far line of the run beyond a line's, or null at the text's edge.
        int? Beyond(int at) => up ? (at > 0 ? RunStartOf(text, at - 1) : null) : (at < last ? RunEnd(text, at + 1, last) : null);
    }

    /// <summary>Whether the line is a run of lines alone (<see cref="Continues"/>), neither neighbour going on with it.</summary>
    public static bool RunOfOneLine(TextSnapshot text, int line, bool breakEnds) =>
        (line == 0 || !Continues(text, line)) && (line >= VimMotions.LastLine(text, breakEnds) || !Continues(text, line + 1));

    /// <summary>The first line of the run of lines (<see cref="Continues"/>) that the line is in.</summary>
    private static int RunStartOf(TextSnapshot text, int line)
    {
        while (line > 0 && Continues(text, line))
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

    /// <summary>The first <paramref name="open"/> from an offset on that opens one more than closed since, each
    /// <paramref name="close"/> on the way wanting one more, escaped brackets passed over; -1 where none is.</summary>
    private static int Ahead(TextSnapshot text, int from, char open, char close)
    {
        int depth = 0;
        for (int p = from; p < text.Length; p++)
        {
            if ((text[p] == open || text[p] == close) && VimMotions.Escaped(text, p))
            {
                continue;
            }
            if (text[p] == open && ++depth == 1)
            {
                return p;
            }
            if (text[p] == close)
            {
                depth--;
            }
        }
        return -1;
    }

    /// <summary>A bracketed block over a visual stretch from <paramref name="low"/> to before <paramref name="high"/>:
    /// the <paramref name="n"/>th around its start, one level further out where it starts on an open bracket (with no
    /// level there, the next ones ahead inside that block), outside any the ones ahead. An inner block of more than one
    /// character the stretch holds already gives way to the one around the character before the stretch, and so on back,
    /// never one ahead. Null where none is.</summary>
    public static VimSpan? BlockOver(TextSnapshot text, int low, int high, string key, bool inner, int n)
    {
        (char open, char close) = key switch
        {
            "(" or ")" or "b" => ('(', ')'),
            "{" or "}" or "B" => ('{', '}'),
            "[" or "]" => ('[', ']'),
            _ => ('<', '>'),
        };
        for (int at = low; at >= 0 && at < text.Length; at--)
        {
            int levels = n + (text[at] == open ? 1 : 0);
            if (Block(text, at, levels, inner, open, close, ahead: at == low, pastOpen: at == low) is not { } span)
            {
                return null;
            }
            int end = span.Linewise ? VimMotions.LineOf(text, span.End).EndIncludingBreak : span.End;
            if (!inner || end - span.Start <= 1 || span.Start < low || end > high)
            {
                return span;
            }
        }
        return null;
    }

    /// <summary>A bracketed block, the <paramref name="n"/>th around the caret (a bracket under it counting as its own),
    /// or, with <paramref name="ahead"/>, outside any the <paramref name="n"/>th ahead (<see cref="Ahead"/>), and with
    /// <paramref name="pastOpen"/> past the one opening at the caret the levels left ahead inside it; inner, a block
    /// whose brackets end and begin lines takes only the lines between, linewise, short of lines of blanks before a
    /// closing bracket first on its line.</summary>
    private static VimSpan? Block(TextSnapshot text, int caret, int n, bool inner, char open, char close, bool ahead = true, bool pastOpen = false)
    {
        if (open == '{' && VimMotions.LineOf(text, caret) is var line && TextNavigation.IndentEnd(text, line) is var first
            && caret < first && first < line.End && text[first] is '{' or '}')
        {
            // In the indent before a line's first brace a B block stands on the brace.
            caret = first;
        }
        // A closing bracket under the caret opens its block where counting back, strings and all, finds the opening one.
        int at = caret < text.Length && text[caret] == close ? Unclosed(text, caret - 1, open, close) : caret;
        if (at < 0 && !ahead)
        {
            return null;
        }
        // On a closing bracket no block holds, the blocks ahead, as outside any.
        bool onward = at < 0;
        int start = onward ? caret : -1;
        int from = at >= 0 && at < text.Length && text[at] == open ? at : at - 1;
        for (int i = 0; i < n; i++)
        {
            int found = onward ? Ahead(text, start + 1, open, close) : Unclosed(text, i == 0 ? from : start - 1, open, close);
            if (found < 0 && i == 0 && ahead)
            {
                // Outside any block, the first one ahead, and a count's more the next ones ahead.
                onward = true;
                found = Ahead(text, caret, open, close);
            }
            else if (found < 0 && i == 1 && pastOpen && start == caret)
            {
                onward = true;
                found = Ahead(text, start + 1, open, close);
            }
            start = found;
            if (start < 0)
            {
                return null;
            }
        }
        int end = VimMotions.Partner(text, start, open, close);
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
            // Lines of blanks before the closing line are left out, and with nothing else inside nothing is taken.
            int last = closeLine.Line - 1;
            while (last > openLine.Line && text.GetLine(last) is { Length: > 0 } blanks && TextNavigation.IndentEnd(text, blanks) == blanks.End)
            {
                last--;
            }
            if (innerStart == openLine.EndIncludingBreak && closeLine.Line - 1 >= openLine.Line + 1)
            {
                return last > openLine.Line ? new VimSpan(innerStart, text.GetLineStart(last), true) : new VimSpan(innerStart, innerStart, false);
            }
            innerEnd = Math.Max(text.GetLine(last).End, innerStart);
        }
        return new VimSpan(innerStart, innerEnd, false);
    }

    /// <summary>The nearest unclosed <paramref name="open"/> at or before the offset, or -1, escaped brackets passed over.</summary>
    private static int Unclosed(TextSnapshot text, int offset, char open, char close)
    {
        int depth = 0;
        for (int p = Math.Min(offset, text.Length - 1); p >= 0; p--)
        {
            if ((text[p] == open || text[p] == close) && VimMotions.Escaped(text, p))
            {
                continue;
            }
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
