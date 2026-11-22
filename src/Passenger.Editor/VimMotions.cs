using Passenger.Text;

namespace Passenger.Editor;

/// <summary>How an operator takes the text a motion passes over: up to its end, with its end, or the whole lines.</summary>
public enum VimMotionKind
{
    Exclusive,
    Inclusive,
    Linewise,
}

/// <summary>Where a motion ends and how an operator takes the text up to there.</summary>
public readonly record struct VimTarget(int Offset, VimMotionKind Kind);

/// <summary>Vim's motions as pure functions of the text and a caret: lines, words and WORDs, characters found on a line,
/// matching brackets, paragraphs and sentences, as Vim 9 moves by them. A motion that cannot move fails (null), as Vim's
/// beep stops the command it was part of.</summary>
public static class VimMotions
{
    /// <summary>The kind of the character at an offset for word motions: 0 blank or line break, 1 punctuation, 2 a
    /// word's (<see cref="WordCharacters"/>), a mark or joiner taking the kind of the character it carries on; a WORD's
    /// every non-blank is 2.</summary>
    public static int KindAt(TextSnapshot text, int offset, bool bigWord)
    {
        char c = text[offset];
        c = WordCharacters.Extends(c) ? text[WordCharacters.Base(text, offset)] : c;
        return char.IsWhiteSpace(c) ? 0 : bigWord || WordCharacters.IsWord(c) ? 2 : 1;
    }

    /// <summary>Where the character a run of one kind ends with begins: the start of its grapheme, so a word's end lands
    /// on the letter its marks carry on.</summary>
    private static int LastOfRun(TextSnapshot text, int last) => StepBack(text, last + 1);

    public static LineSpan LineOf(TextSnapshot text, int offset) => text.GetLine(text.GetLineFromOffset(offset));

    /// <summary>The last line Vim would count: where a final line break ends the text (<paramref name="breakEnds"/>, the
    /// file keeping one, as Vim's 'eol'), the empty line after it is the file's end, not a line; where the file keeps
    /// none, a final line break begins an empty last line.</summary>
    public static int LastLine(TextSnapshot text, bool breakEnds)
    {
        int last = text.LineCount - 1;
        return breakEnds && last > 0 && text.GetLine(last).Length == 0 ? last - 1 : last;
    }

    /// <summary>Where a normal-mode caret may stand nearest the offset: on a character of its line, never past its last
    /// one, but at the start of an empty line; never on the empty line after a final line break that ends the text.</summary>
    public static int Settled(TextSnapshot text, int offset, bool breakEnds)
    {
        LineSpan line = LineOf(text, offset);
        if (line.Line > LastLine(text, breakEnds))
        {
            line = text.GetLine(LastLine(text, breakEnds));
            offset = line.End;
        }
        return SettledOn(text, line, offset);
    }

    /// <summary>Where a normal-mode caret may stand on the line given nearest the offset.</summary>
    private static int SettledOn(TextSnapshot text, LineSpan line, int offset) =>
        line.Length == 0 || offset < line.End ? Math.Max(offset, line.Start) : Math.Max(StepBack(text, line.End), line.Start);

    public static int StepBack(TextSnapshot text, int offset) => offset <= 0 ? 0 : TextNavigation.PreviousGrapheme(text, offset);

    public static int FirstNonBlank(TextSnapshot text, int line, bool breakEnds) => Settled(text, TextNavigation.IndentEnd(text, text.GetLine(line)), breakEnds);

    /// <summary>Whether only blanks stand before the offset on its line.</summary>
    public static bool InIndent(TextSnapshot text, int offset) => offset <= TextNavigation.IndentEnd(text, LineOf(text, offset));

    /// <summary>A step taken <paramref name="n"/> times from <paramref name="from"/>, ending early once it stops moving,
    /// so a count of millions costs no more than the text is long.</summary>
    public static int Repeated(int n, int from, Func<int, int> step)
    {
        int at = from;
        for (int i = 0; i < n; i++)
        {
            int next = step(at);
            if (next == at)
            {
                break;
            }
            at = next;
        }
        return at;
    }

    /// <summary>The offset on a line whose cells (<see cref="TextNavigation.ColumnAfter(TextSnapshot, int, int, int, int)"/>)
    /// take in the column, or its last character's where the line is shorter.</summary>
    public static int OffsetAtColumn(TextSnapshot text, int lineNumber, int column, int tabWidth)
    {
        LineSpan line = text.GetLine(lineNumber);
        int at = 0;
        for (int p = line.Start; p < line.End;)
        {
            int next = TextNavigation.NextGrapheme(text, p);
            at = TextNavigation.ColumnAfter(text, p, next, at, tabWidth);
            if (at > column)
            {
                return p;
            }
            p = next;
        }
        return SettledOn(text, line, line.End);
    }

    /// <summary>h, or Backspace crossing into the line before (<paramref name="crossing"/>): fails where it cannot move.</summary>
    public static VimTarget? Left(TextSnapshot text, int caret, int n, bool crossing)
    {
        int to = Repeated(n, caret, p =>
        {
            if (!crossing)
            {
                return Math.Max(StepBack(text, p), LineOf(text, caret).Start);
            }
            // A step back over a line break lands on the line's last character, never past it.
            int back = StepBack(text, p);
            LineSpan line = LineOf(text, back);
            return back < p && line.Length > 0 && back == line.End ? StepBack(text, back) : back;
        });
        return to == caret ? null : new VimTarget(to, VimMotionKind.Exclusive);
    }

    /// <summary>l, or Space crossing into the next line (<paramref name="crossing"/>); an operator's l reaches past the
    /// line's last character.</summary>
    public static VimTarget? Right(TextSnapshot text, int caret, int n, bool crossing, bool operating, bool breakEnds)
    {
        int to = Repeated(n, caret, p =>
        {
            LineSpan line = LineOf(text, p);
            int next = TextNavigation.NextGrapheme(text, p);
            if (crossing && (next >= line.End || p >= line.End))
            {
                return line.Line < LastLine(text, breakEnds) ? (operating && p < line.End ? line.End : line.EndIncludingBreak) : Math.Min(next, operating ? line.End : p);
            }
            return Math.Min(next, operating ? line.End : SettledOn(text, line, line.End));
        });
        return to == caret ? null : new VimTarget(to, VimMotionKind.Exclusive);
    }

    /// <summary>j and k, + and -, Enter, _: <paramref name="lines"/> lines down (up when negative), failing on the line
    /// it cannot leave; the column is the caller's.</summary>
    public static int? LinesAway(TextSnapshot text, int caret, long lines, bool breakEnds)
    {
        int from = text.GetLineFromOffset(caret);
        int to = (int)Math.Clamp(from + lines, 0, LastLine(text, breakEnds));
        return to == from && lines != 0 ? null : to;
    }

    /// <summary>$: the last character of the line <paramref name="n"/> - 1 lines down, inclusive.</summary>
    public static VimTarget? LineEnd(TextSnapshot text, int caret, int n, bool breakEnds)
    {
        int from = text.GetLineFromOffset(caret);
        if (n > 1 && from >= LastLine(text, breakEnds))
        {
            return null;
        }
        LineSpan line = text.GetLine((int)Math.Min(from + (long)n - 1, LastLine(text, breakEnds)));
        return new VimTarget(SettledOn(text, line, line.End), VimMotionKind.Inclusive);
    }

    /// <summary>w and W: the start of the next word, an empty line counting as one. Under an operator its last step stops
    /// at the line's end rather than go on to the next line's first word, and a count reaching past the last line stops
    /// at that line's end (its start where it is empty).</summary>
    public static VimTarget? WordStart(TextSnapshot text, int caret, int n, bool bigWord, bool operating, bool breakEnds = true)
    {
        int at = caret;
        for (int i = 0; i < n; i++)
        {
            bool last = i == n - 1;
            int next = NextWordStart(text, at, bigWord, operating && last);
            if (next == at)
            {
                break;
            }
            at = next;
        }
        if (operating && text.GetLine(LastLine(text, breakEnds)) is var end && at > (end.Length == 0 ? end.Start : end.End))
        {
            at = Math.Max(caret, end.Length == 0 ? end.Start : end.End);
        }
        if (at == caret)
        {
            return null;
        }
        return new VimTarget(at, VimMotionKind.Exclusive);
    }

    private static int NextWordStart(TextSnapshot text, int offset, bool bigWord, bool stopAtLineEnd)
    {
        int p = offset;
        int n = text.Length;
        if (stopAtLineEnd && p < n && EmptyLineAt(text, p))
        {
            // An empty line is a word whose end is its line break: an operator's last step from one stops past it.
            return LineOf(text, p).EndIncludingBreak;
        }
        if (p < n && KindAt(text, p, bigWord) is var kind and not 0)
        {
            while (p < n && KindAt(text, p, bigWord) == kind)
            {
                p++;
            }
        }
        while (p < n && KindAt(text, p, bigWord) == 0)
        {
            if (stopAtLineEnd && text[p] is '\r' or '\n' && p > offset)
            {
                return p;
            }
            p++;
            if (text[p - 1] == '\n' && EmptyLineAt(text, p))
            {
                return p;
            }
        }
        return p;
    }

    /// <summary>e and E: the last character of this word if the caret is before it, else of the next; inclusive. On the
    /// text's last character an operator's takes that character.</summary>
    public static VimTarget? WordEnd(TextSnapshot text, int caret, int n, bool bigWord, bool breakEnds, bool operating = false)
    {
        int to = Repeated(n, caret, p => NextWordEnd(text, p, bigWord, breakEnds));
        return to != caret || (operating && caret == Settled(text, text.Length, breakEnds)) ? new VimTarget(to, VimMotionKind.Inclusive) : null;
    }

    private static int NextWordEnd(TextSnapshot text, int offset, bool bigWord, bool breakEnds)
    {
        int n = text.Length;
        int p = TextNavigation.NextGrapheme(text, offset);
        while (p < n && KindAt(text, p, bigWord) == 0)
        {
            p++;
        }
        if (p >= n)
        {
            // No word after: the text's last place, where that is further on.
            return Math.Max(offset, Settled(text, n, breakEnds));
        }
        int kind = KindAt(text, p, bigWord);
        while (p + 1 < n && KindAt(text, p + 1, bigWord) == kind)
        {
            p++;
        }
        return LastOfRun(text, p);
    }

    /// <summary>b and B: the start of this word if the caret is past it, else of the one before; an empty line stops it.
    /// <paramref name="ranOut"/> says a step was left to take from the empty line that begins the text.</summary>
    public static VimTarget? WordBack(TextSnapshot text, int caret, int n, bool bigWord, out bool ranOut)
    {
        int to = caret;
        int steps = 0;
        for (; steps < n && to > 0; steps++)
        {
            to = PreviousWordStart(text, to, bigWord);
        }
        ranOut = steps < n && EmptyLineAt(text, 0);
        return to == caret ? null : new VimTarget(to, VimMotionKind.Exclusive);
    }

    private static int PreviousWordStart(TextSnapshot text, int offset, bool bigWord)
    {
        if (offset == 0)
        {
            return 0;
        }
        int p = offset - 1;
        while (p > 0 && KindAt(text, p, bigWord) == 0)
        {
            if (EmptyLineAt(text, p))
            {
                return p;
            }
            p--;
        }
        int kind = KindAt(text, p, bigWord);
        while (p > 0 && kind != 0 && KindAt(text, p - 1, bigWord) == kind)
        {
            p--;
        }
        return p;
    }

    /// <summary>ge and gE: the last character of the word before, an empty line counting as one; inclusive.
    /// <paramref name="ranOut"/> says the text began before the count did.</summary>
    public static VimTarget? WordEndBack(TextSnapshot text, int caret, int n, bool bigWord, out bool ranOut)
    {
        int to = caret;
        int steps = 0;
        while (steps < n && PreviousWordEnd(text, to, bigWord) is var next && next != to)
        {
            to = next;
            steps++;
            if (to == 0 && !WordEndsAt(text, 0, bigWord))
            {
                // With no word end before, the text's start ends the count as it stands.
                steps = n;
            }
        }
        ranOut = steps < n;
        return to == caret ? null : new VimTarget(to, VimMotionKind.Inclusive);
    }

    private static int PreviousWordEnd(TextSnapshot text, int offset, bool bigWord)
    {
        if (offset == 0)
        {
            return 0;
        }
        int p = offset;
        int kind = p < text.Length ? KindAt(text, p, bigWord) : 0;
        if (kind != 0)
        {
            while (p > 0 && KindAt(text, p - 1, bigWord) == kind)
            {
                p--;
            }
        }
        while (p > 0)
        {
            p--;
            if (EmptyLineAt(text, p))
            {
                return p;
            }
            if (KindAt(text, p, bigWord) != 0)
            {
                return LastOfRun(text, p);
            }
        }
        return 0;
    }

    /// <summary>Whether a word ends at the offset: a non-blank whose next character is of another kind, or an empty line.</summary>
    private static bool WordEndsAt(TextSnapshot text, int offset, bool bigWord) =>
        EmptyLineAt(text, offset) || (offset < text.Length && KindAt(text, offset, bigWord) != 0 && EndsRun(text, offset, bigWord));

    /// <summary>Whether the character at an offset is its run's last: the character after its grapheme is of another
    /// kind, or the text ends.</summary>
    public static bool EndsRun(TextSnapshot text, int offset, bool bigWord)
    {
        int next = TextNavigation.NextGrapheme(text, offset);
        return next >= text.Length || KindAt(text, next, bigWord) != KindAt(text, offset, bigWord);
    }

    private static bool EmptyLineAt(TextSnapshot text, int offset)
    {
        LineSpan line = LineOf(text, offset);
        return line.Length == 0 && line.Start == offset;
    }

    /// <summary>f, F, t and T: the <paramref name="n"/>th <paramref name="target"/> on the caret's line ahead (behind
    /// when <paramref name="back"/>), or just before it (<paramref name="till"/>). A repeat by ; and , passes over the
    /// character a till already stands against (<paramref name="again"/>).</summary>
    public static VimTarget? Find(TextSnapshot text, int caret, int n, string target, bool back, bool till, bool again)
    {
        LineSpan line = LineOf(text, caret);
        int p = caret;
        if (again && till && n == 1)
        {
            // Standing against the character a till went to, ; and , go on to the next one.
            int beside = back ? StepBack(text, p) : TextNavigation.NextGrapheme(text, p);
            if (beside >= line.Start && beside < line.End && At(text, beside, target))
            {
                p = beside;
            }
        }
        for (int i = 0; i < n; i++)
        {
            int found = -1;
            for (int q = back ? StepBack(text, p) : TextNavigation.NextGrapheme(text, p); back ? q >= line.Start && q < p : q < line.End; q = back ? (q == line.Start ? -1 : StepBack(text, q)) : TextNavigation.NextGrapheme(text, q))
            {
                if (At(text, q, target))
                {
                    found = q;
                    break;
                }
                if (back && q == line.Start)
                {
                    break;
                }
            }
            if (found < 0)
            {
                return null;
            }
            p = found;
        }
        int to = !till ? p : back ? TextNavigation.NextGrapheme(text, p) : StepBack(text, p);
        return new VimTarget(to, back ? VimMotionKind.Exclusive : VimMotionKind.Inclusive);
    }

    private static bool At(TextSnapshot text, int offset, string target) =>
        offset + target.Length <= text.Length && text.GetText(offset, target.Length) == target;

    private const string Brackets = "(){}[]";

    /// <summary>%: the bracket matching the first one at or after the caret on its line, inclusive; with a count, that
    /// percentage of the way through the file, linewise.</summary>
    /// <summary>The strings between double quotes on a line, each from its opening quote to its closing one; none where
    /// the line holds an odd number of them. A quote after a backslash is no quote.</summary>
    private static List<(int Start, int End)> Quoted(TextSnapshot text, LineSpan line)
    {
        List<int> marks = [];
        for (int p = line.Start; p < line.End; p++)
        {
            if (text[p] == '"' && (p == line.Start || text[p - 1] != '\\'))
            {
                marks.Add(p);
            }
        }
        List<(int Start, int End)> quoted = [];
        for (int i = 0; marks.Count % 2 == 0 && i + 1 < marks.Count; i += 2)
        {
            quoted.Add((marks[i], marks[i + 1]));
        }
        return quoted;
    }

    /// <summary>Whether the character at an offset stands between a character's single quotes ('(' or '\(').</summary>
    private static bool InCharacterQuotes(TextSnapshot text, int offset) =>
        offset + 1 < text.Length && text[offset + 1] == '\''
            && ((offset >= 1 && text[offset - 1] == '\'') || (offset >= 2 && text[offset - 1] == '\\' && text[offset - 2] == '\''));

    public static VimTarget? MatchPair(TextSnapshot text, int caret, int count, bool breakEnds)
    {
        if (count > 0)
        {
            if (count > 100)
            {
                return null;
            }
            int lines = LastLine(text, breakEnds) + 1;
            int line = (int)Math.Clamp(((long)count * lines + 99) / 100 - 1, 0, LastLine(text, breakEnds));
            return new VimTarget(FirstNonBlank(text, line, breakEnds), VimMotionKind.Linewise);
        }
        LineSpan span = LineOf(text, caret);
        int at = caret;
        while (at < span.End && Brackets.IndexOf(text[at]) < 0)
        {
            at++;
        }
        if (at >= span.End)
        {
            return null;
        }
        int kind = Brackets.IndexOf(text[at]);
        char open = Brackets[kind & ~1];
        char close = Brackets[kind | 1];
        bool forward = (kind & 1) == 0;
        int depth = 0;
        // Brackets in strings between double quotes, or a character's quotes, count only from within such a string on its line.
        LineSpan quotesOf = span;
        List<(int Start, int End)> quotes = Quoted(text, span);
        bool inQuotes = quotes.Exists(q => q.Start < at && at < q.End);
        for (int p = at; forward ? p < text.Length : p >= 0; p += forward ? 1 : -1)
        {
            char c = text[p];
            if (c != open && c != close)
            {
                continue;
            }
            if (p < quotesOf.Start || p > quotesOf.End)
            {
                quotesOf = LineOf(text, p);
                quotes = Quoted(text, quotesOf);
            }
            if (p != at && (quotes.Exists(q => q.Start < p && p < q.End) != (inQuotes && quotesOf.Line == span.Line) || InCharacterQuotes(text, p)))
            {
                continue;
            }
            if (c == open)
            {
                depth += forward ? 1 : -1;
            }
            else if (c == close)
            {
                depth += forward ? -1 : 1;
            }
            if (depth == 0)
            {
                return new VimTarget(p, VimMotionKind.Inclusive);
            }
        }
        return null;
    }

    /// <summary>} and {: past the next (or last) run of lines with text to the empty line after (before) it, or the text's
    /// end (start), where a } stands on the last character, which an operator takes too; a count with paragraphs left over
    /// at the text's edge fails.</summary>
    public static VimTarget? Paragraph(TextSnapshot text, int caret, int n, bool forward, bool breakEnds)
    {
        int line = text.GetLineFromOffset(caret);
        int last = LastLine(text, breakEnds);
        int step = forward ? 1 : -1;
        for (int i = 0; i < n; i++)
        {
            bool passedText = false;
            for (bool first = true; ; first = false)
            {
                passedText |= !Blank(text, line);
                if (!first && passedText && Blank(text, line))
                {
                    break;
                }
                if (line + step < 0 || line + step > last)
                {
                    if (i < n - 1)
                    {
                        return null;
                    }
                    break;
                }
                line += step;
            }
        }
        if (forward && line == last)
        {
            LineSpan end = text.GetLine(line);
            return new VimTarget(SettledOn(text, end, end.End), end.Length > 0 ? VimMotionKind.Inclusive : VimMotionKind.Exclusive);
        }
        return new VimTarget(text.GetLineStart(line), VimMotionKind.Exclusive);
    }

    /// <summary>Whether a line is empty, a paragraph's bound; a line of blanks is not.</summary>
    public static bool Blank(TextSnapshot text, int line) => text.GetLine(line).Length == 0;

    /// <summary>) and (: the start of the next sentence, or of this one or the one before; exclusive. An empty line is a
    /// sentence, ) stopping at the first of a run of them and ( at the last. Past the last sentence ) stands on the text's
    /// last character and takes it, from there too; ( stops at the text's very start, twice where the text begins with an
    /// empty line. A count with sentences left over at the text's edge fails.</summary>
    public static VimTarget? Sentence(TextSnapshot text, int caret, int n, bool forward, bool breakEnds)
    {
        int at = caret;
        if (forward)
        {
            int end = Settled(text, text.Length, breakEnds);
            // An empty last line has no character to take: the motion ends at its start, exclusive.
            bool emptyEnd = LineOf(text, end).Length == 0;
            // Past a closing '.', '!' or '?' that ends the text's last line, its end is one more sentence's start.
            bool endStops = EndsWithSentence(text) && !emptyEnd;
            VimMotionKind toEnd = emptyEnd ? VimMotionKind.Exclusive : VimMotionKind.Inclusive;
            for (int i = 0; i < n; i++)
            {
                int next = i == 0 ? SentenceStartAfter(text, caret) : NextSentenceStart(text, at);
                if (next < text.Length && next > at)
                {
                    at = next;
                    continue;
                }
                if (endStops && at < text.Length)
                {
                    at = text.Length;
                    continue;
                }
                if (emptyEnd && at < end)
                {
                    // A run of empty lines ending the text ends at its last, one more sentence's start.
                    at = end;
                    continue;
                }
                // The text's last character is one more place after the last sentence's start, even where it is that start.
                if (i < n - 1 || (end < at && at < text.Length))
                {
                    return null;
                }
                return new VimTarget(end, toEnd);
            }
            return at >= text.Length ? new VimTarget(end, toEnd) : new VimTarget(at, VimMotionKind.Exclusive);
        }
        if (text.Length > 0 && text[0] is '.' or '!' or '?' && EndsSentence(text, 0, out _) && NextSentenceStart(text, 0) is var first && first < text.Length)
        {
            // A text opening with a sentence's end begins its first sentence after it, as far back as ( goes.
            at = Math.Max(at, first);
            for (int i = caret < first ? 1 : 0; i < n && at > first; i++)
            {
                at = Math.Max(first, SentenceStartBefore(text, at));
            }
            return new VimTarget(at, VimMotionKind.Exclusive);
        }
        bool topAgain = Blank(text, 0);
        for (int i = 0; i < n; i++)
        {
            if (at == 0)
            {
                if (i == 0)
                {
                    // From the text's start one ( stays there, an operator taking nothing; more fail.
                    return n == 1 ? new VimTarget(0, VimMotionKind.Exclusive) : null;
                }
                if (!topAgain)
                {
                    return null;
                }
                topAgain = false;
                continue;
            }
            at = SentenceStartBefore(text, at);
        }
        return new VimTarget(at, VimMotionKind.Exclusive);
    }

    /// <summary>Whether the text's last non-blank, past closing brackets and quotes, is a '.', '!' or '?'.</summary>
    private static bool EndsWithSentence(TextSnapshot text)
    {
        int p = text.Length - 1;
        while (p >= 0 && char.IsWhiteSpace(text[p]))
        {
            p--;
        }
        while (p >= 0 && text[p] is ')' or ']' or '"' or '\'')
        {
            p--;
        }
        return p >= 0 && text[p] is '.' or '!' or '?';
    }

    /// <summary>The start of the first sentence after the offset, from wherever the offset stands in its own (on its end,
    /// or in the white after it, or in the white between a run of empty lines and the sentence after them, which is that
    /// sentence's); the text's end where none is.</summary>
    private static int SentenceStartAfter(TextSnapshot text, int offset)
    {
        int start = PreviousSentenceStart(text, offset + 1);
        if (EmptyLineAt(text, start) && !EmptyLineAt(text, offset))
        {
            start = NextSentenceStart(text, start);
            offset = Math.Max(offset, start);
        }
        while (start <= offset)
        {
            int next = NextSentenceStart(text, start);
            if (next <= start)
            {
                return text.Length;
            }
            start = next;
        }
        return start;
    }

    /// <summary>Where ( goes from a place past the text's start: the start of the sentence before, but the last of a run
    /// of empty lines where that run is the sentence before, and from an empty line the sentence before its whole run.</summary>
    private static int SentenceStartBefore(TextSnapshot text, int offset)
    {
        int limit = offset;
        if (EmptyLineAt(text, offset))
        {
            int line = text.GetLineFromOffset(offset);
            while (line > 0 && Blank(text, line - 1))
            {
                line--;
            }
            limit = text.GetLineStart(line);
        }
        if (limit == 0)
        {
            return 0;
        }
        int start = PreviousSentenceStart(text, limit);
        if (EmptyLineAt(text, start))
        {
            int line = text.GetLineFromOffset(start);
            while (line + 1 < text.LineCount && Blank(text, line + 1) && text.GetLineStart(line + 1) < limit)
            {
                line++;
            }
            start = text.GetLineStart(line);
        }
        return start;
    }

    /// <summary>The start of the first sentence after the offset: past a sentence's end and the blanks after it, an empty
    /// line, or the first text after an empty line; the text's end where none is.</summary>
    public static int NextSentenceStart(TextSnapshot text, int offset)
    {
        int n = text.Length;
        if (EmptyLineAt(text, offset))
        {
            // An empty line is a sentence of its own; the next begins at the first text after the empty lines.
            int p = offset;
            while (p < n && (EmptyLineAt(text, p) || text[p] is '\r' or '\n'))
            {
                p++;
            }
            return p >= n ? n : SkipBlanks(text, p);
        }
        for (int p = offset; p < n; p++)
        {
            char c = text[p];
            if (c == '\n' && p + 1 <= n && (p + 1 == n || EmptyLineAt(text, p + 1)))
            {
                return p + 1;
            }
            if (c is '.' or '!' or '?' && EndsSentence(text, p, out int after) && after > offset)
            {
                int start = SkipBlanks(text, after);
                if (start > offset)
                {
                    return start;
                }
            }
        }
        return n;
    }

    /// <summary>The start of the sentence the offset is in when past it, else of the one before (the text's start, blanks
    /// and all, where none is before): the sentences counted forward from the first empty line above.</summary>
    public static int PreviousSentenceStart(TextSnapshot text, int offset)
    {
        int start = ParagraphTop(text, offset);
        while (NextSentenceStart(text, start) is var next && next > start && next < offset)
        {
            start = next;
        }
        return start;
    }

    /// <summary>Where the nearest run of empty lines beginning before the offset begins, a sentence's start whatever came
    /// before it; the text's start where there is none.</summary>
    private static int ParagraphTop(TextSnapshot text, int offset)
    {
        if (offset <= 0)
        {
            return 0;
        }
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        for (int l = text.GetLineStart(line) < offset ? line : line - 1; l >= 0; l--)
        {
            if (Blank(text, l))
            {
                while (l > 0 && Blank(text, l - 1))
                {
                    l--;
                }
                return text.GetLineStart(l);
            }
        }
        return 0;
    }

    /// <summary>Whether the '.', '!' or '?' at the offset ends a sentence: closers after it, then a blank or a line's end;
    /// <paramref name="after"/> is just past the closers.</summary>
    public static bool EndsSentence(TextSnapshot text, int offset, out int after)
    {
        int p = offset + 1;
        while (p < text.Length && text[p] is ')' or ']' or '"' or '\'')
        {
            p++;
        }
        after = p;
        return p >= text.Length || text[p] is ' ' or '\t' or '\r' or '\n';
    }

    private static int SkipBlanks(TextSnapshot text, int offset)
    {
        int p = offset;
        while (p < text.Length && text[p] is ' ' or '\t' or '\r' or '\n' && !EmptyLineAt(text, p))
        {
            p++;
        }
        return p;
    }

}
