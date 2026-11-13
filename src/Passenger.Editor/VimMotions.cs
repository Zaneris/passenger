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
    /// <summary>A character's kind for word motions: 0 blank or line break, 1 punctuation, 2 a word's; a WORD's every
    /// non-blank is 2.</summary>
    public static int Kind(char c, bool bigWord) => char.IsWhiteSpace(c) ? 0 : bigWord || TextNavigation.IsWordChar(c) ? 2 : 1;

    public static LineSpan LineOf(TextSnapshot text, int offset) => text.GetLine(text.GetLineFromOffset(offset));

    /// <summary>The last line Vim would count: the empty one after a final line break is the file's end, not a line.</summary>
    public static int LastLine(TextSnapshot text)
    {
        int last = text.LineCount - 1;
        return last > 0 && text.GetLine(last).Length == 0 ? last - 1 : last;
    }

    /// <summary>Where a normal-mode caret may stand nearest the offset: on a character of its line, never past its last
    /// one, but at the start of an empty line.</summary>
    public static int Settled(TextSnapshot text, int offset)
    {
        LineSpan line = LineOf(text, offset);
        if (line.Line > LastLine(text))
        {
            // The empty line after a final line break is no line to stand on.
            line = text.GetLine(LastLine(text));
            offset = line.End;
        }
        return line.Length == 0 || offset < line.End ? Math.Max(offset, line.Start) : Math.Max(StepBack(text, line.End), line.Start);
    }

    public static int StepBack(TextSnapshot text, int offset) => offset <= 0 ? 0 : TextNavigation.PreviousGrapheme(text, offset);

    public static int FirstNonBlank(TextSnapshot text, int line) => Settled(text, TextNavigation.IndentEnd(text, text.GetLine(line)));

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

    /// <summary>The cells from its line's start to the offset: a tab reaching its next stop, a wide character two.</summary>
    public static int ColumnOf(TextSnapshot text, int offset, int tabWidth)
    {
        LineSpan line = LineOf(text, offset);
        int column = 0;
        for (int p = line.Start; p < offset && p < line.End; p = TextNavigation.NextGrapheme(text, p))
        {
            column += CellsAt(text, p, column, tabWidth);
        }
        return column;
    }

    /// <summary>The offset on a line whose cells take in the column, or its last character's where the line is shorter.</summary>
    public static int OffsetAtColumn(TextSnapshot text, int lineNumber, int column, int tabWidth)
    {
        LineSpan line = text.GetLine(lineNumber);
        int at = 0;
        for (int p = line.Start; p < line.End; p = TextNavigation.NextGrapheme(text, p))
        {
            at += CellsAt(text, p, at, tabWidth);
            if (at > column)
            {
                return p;
            }
        }
        return Settled(text, line.End);
    }

    private static int CellsAt(TextSnapshot text, int offset, int column, int tabWidth)
    {
        char c = text[offset];
        if (c == '\t')
        {
            return tabWidth - (column % Math.Max(1, tabWidth));
        }
        int rune = char.IsHighSurrogate(c) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? char.ConvertToUtf32(c, text[offset + 1]) : c;
        return rune >= 0x1100 && !char.IsSurrogate((char)Math.Min(rune, 0xFFFF)) || rune > 0xFFFF ? (WideCharacters.Contains(rune) ? 2 : 1) : 1;
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
    public static VimTarget? Right(TextSnapshot text, int caret, int n, bool crossing, bool operating)
    {
        int to = Repeated(n, caret, p =>
        {
            LineSpan line = LineOf(text, p);
            int next = TextNavigation.NextGrapheme(text, p);
            if (crossing && (next >= line.End || p >= line.End))
            {
                return line.Line < LastLine(text) ? (operating && p < line.End ? line.End : line.EndIncludingBreak) : Math.Min(next, operating ? line.End : p);
            }
            return Math.Min(next, operating ? line.End : Settled(text, line.End));
        });
        return to == caret ? null : new VimTarget(to, VimMotionKind.Exclusive);
    }

    /// <summary>j and k, + and -, Enter, _: <paramref name="lines"/> lines down (up when negative), failing on the line
    /// it cannot leave; the column is the caller's.</summary>
    public static int? LinesAway(TextSnapshot text, int caret, long lines)
    {
        int from = text.GetLineFromOffset(caret);
        int to = (int)Math.Clamp(from + lines, 0, LastLine(text));
        return to == from && lines != 0 ? null : to;
    }

    /// <summary>$: the last character of the line <paramref name="n"/> - 1 lines down, inclusive.</summary>
    public static VimTarget? LineEnd(TextSnapshot text, int caret, int n)
    {
        int from = text.GetLineFromOffset(caret);
        if (n > 1 && from >= LastLine(text))
        {
            return null;
        }
        LineSpan line = text.GetLine((int)Math.Min(from + (long)n - 1, LastLine(text)));
        return new VimTarget(Settled(text, line.End), VimMotionKind.Inclusive);
    }

    /// <summary>w and W: the start of the next word, an empty line counting as one. Under an operator its last step stops
    /// at the line's end rather than go on to the next line's first word.</summary>
    public static VimTarget? WordStart(TextSnapshot text, int caret, int n, bool bigWord, bool operating)
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
        if (p < n && Kind(text[p], bigWord) is var kind and not 0)
        {
            while (p < n && Kind(text[p], bigWord) == kind)
            {
                p++;
            }
        }
        while (p < n && Kind(text[p], bigWord) == 0)
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

    /// <summary>e and E: the last character of this word if the caret is before it, else of the next; inclusive.</summary>
    public static VimTarget? WordEnd(TextSnapshot text, int caret, int n, bool bigWord)
    {
        int to = Repeated(n, caret, p => NextWordEnd(text, p, bigWord));
        return to == caret ? null : new VimTarget(to, VimMotionKind.Inclusive);
    }

    private static int NextWordEnd(TextSnapshot text, int offset, bool bigWord)
    {
        int n = text.Length;
        int p = offset + 1;
        while (p < n && Kind(text[p], bigWord) == 0)
        {
            p++;
        }
        if (p >= n)
        {
            // No word after: the text's last place, where that is further on.
            return Math.Max(offset, Settled(text, n));
        }
        int kind = Kind(text[p], bigWord);
        while (p + 1 < n && Kind(text[p + 1], bigWord) == kind)
        {
            p++;
        }
        return p;
    }

    /// <summary>b and B: the start of this word if the caret is past it, else of the one before; an empty line stops it.</summary>
    public static VimTarget? WordBack(TextSnapshot text, int caret, int n, bool bigWord)
    {
        int to = Repeated(n, caret, p => PreviousWordStart(text, p, bigWord));
        return to == caret ? null : new VimTarget(to, VimMotionKind.Exclusive);
    }

    private static int PreviousWordStart(TextSnapshot text, int offset, bool bigWord)
    {
        if (offset == 0)
        {
            return 0;
        }
        int p = offset - 1;
        while (p > 0 && Kind(text[p], bigWord) == 0)
        {
            if (EmptyLineAt(text, p))
            {
                return p;
            }
            p--;
        }
        int kind = Kind(text[p], bigWord);
        while (p > 0 && kind != 0 && Kind(text[p - 1], bigWord) == kind)
        {
            p--;
        }
        return p;
    }

    /// <summary>ge and gE: the last character of the word before, an empty line counting as one; inclusive.</summary>
    public static VimTarget? WordEndBack(TextSnapshot text, int caret, int n, bool bigWord)
    {
        int to = Repeated(n, caret, p => PreviousWordEnd(text, p, bigWord));
        return to == caret ? null : new VimTarget(to, VimMotionKind.Inclusive);
    }

    private static int PreviousWordEnd(TextSnapshot text, int offset, bool bigWord)
    {
        if (offset == 0)
        {
            return 0;
        }
        int p = offset;
        int kind = p < text.Length ? Kind(text[p], bigWord) : 0;
        if (kind != 0)
        {
            while (p > 0 && Kind(text[p - 1], bigWord) == kind)
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
            if (Kind(text[p], bigWord) != 0)
            {
                return p;
            }
        }
        return 0;
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
    public static VimTarget? MatchPair(TextSnapshot text, int caret, int count)
    {
        if (count > 0)
        {
            if (count > 100)
            {
                return null;
            }
            int lines = LastLine(text) + 1;
            int line = (int)Math.Clamp(((long)count * lines + 99) / 100 - 1, 0, LastLine(text));
            return new VimTarget(FirstNonBlank(text, line), VimMotionKind.Linewise);
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
        for (int p = at; forward ? p < text.Length : p >= 0; p += forward ? 1 : -1)
        {
            char c = text[p];
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
    public static VimTarget? Paragraph(TextSnapshot text, int caret, int n, bool forward)
    {
        int line = text.GetLineFromOffset(caret);
        int last = LastLine(text);
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
            return new VimTarget(Settled(text, end.End), end.Length > 0 ? VimMotionKind.Inclusive : VimMotionKind.Exclusive);
        }
        return new VimTarget(text.GetLineStart(line), VimMotionKind.Exclusive);
    }

    /// <summary>Whether a line is empty, a paragraph's bound; a line of blanks is not.</summary>
    public static bool Blank(TextSnapshot text, int line) => text.GetLine(line).Length == 0;

    /// <summary>) and (: the start of the next sentence, or of this one or the one before; exclusive.</summary>
    public static VimTarget? Sentence(TextSnapshot text, int caret, int n, bool forward)
    {
        int to = Repeated(n, caret, p => forward ? NextSentenceStart(text, p) : PreviousSentenceStart(text, p));
        return to == caret ? null : new VimTarget(to, VimMotionKind.Exclusive);
    }

    /// <summary>The start of the first sentence after the offset: past a sentence's end and the blanks after it, an empty
    /// line, or the first text after an empty line; the text's last character where none is.</summary>
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
            return p >= n ? Settled(text, n) : SkipBlanks(text, p);
        }
        for (int p = offset; p < n; p++)
        {
            char c = text[p];
            if (c == '\n' && p + 1 <= n && (p + 1 == n || EmptyLineAt(text, p + 1)))
            {
                return p + 1 >= n ? Settled(text, n) : p + 1;
            }
            if (c is '.' or '!' or '?' && EndsSentence(text, p, out int after) && after > offset)
            {
                int start = SkipBlanks(text, after);
                if (start >= n)
                {
                    return Settled(text, n);
                }
                if (start > offset)
                {
                    return start;
                }
            }
        }
        return Settled(text, n);
    }

    /// <summary>The start of the sentence the offset is in when past it, else of the one before: sentences counted
    /// forward from the top of the run of lines two runs before, as far back as one can be.</summary>
    public static int PreviousSentenceStart(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(offset);
        for (int runs = 0; runs < 3 && line > 0; runs++)
        {
            bool blank = Blank(text, line);
            while (line > 0 && Blank(text, line - 1) == blank)
            {
                line--;
            }
            if (runs < 2 && line > 0)
            {
                line--;
            }
        }
        int start = text.GetLineStart(line);
        int best = Blank(text, line) ? start : SkipBlanks(text, start);
        if (best >= offset)
        {
            return 0;
        }
        while (true)
        {
            int next = NextSentenceStart(text, best);
            if (next >= offset || next <= best)
            {
                return best;
            }
            best = next;
        }
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

    /// <summary>The text of a key that a find or a replace takes: a character, or a named key's own.</summary>
    public static string? Typed(string key) => key switch
    {
        "<Tab>" => "\t",
        "<Space>" => " ",
        _ when key.Length == 1 || (key.Length == 2 && char.IsSurrogatePair(key[0], key[1])) => key,
        _ => null,
    };
}
