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
    /// <summary>The kind of the character at an offset for word motions: 0 blank (<see cref="IsBlank"/>) or line break,
    /// 1 punctuation, a control character among it, 2 a word's (<see cref="WordCharacters"/>), a mark or joiner taking the
    /// kind of the character it carries on; a WORD's every non-blank is 2.</summary>
    public static int KindAt(TextSnapshot text, int offset, bool bigWord)
    {
        char c = text[offset];
        c = WordCharacters.Extends(c) ? text[WordCharacters.Base(text, offset)] : c;
        bool lone = c == '\r' && (offset + 1 >= text.Length || text[offset + 1] != '\n');
        return IsBlank(c) && !lone ? 0 : bigWord || WordCharacters.IsWord(c) ? 2 : 1;
    }

    /// <summary>Whether a character is blank to words: white space but the control characters a line shows as marks (<see
    /// cref="ControlCharacters"/>), with a tab and the line breaks.</summary>
    public static bool IsBlank(char c) => c is '\t' or '\n' or '\r' || (char.IsWhiteSpace(c) && !char.IsControl(c));

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

    /// <summary>The strings between double quotes on a line, each from its opening quote to its closing one; none where
    /// the line holds an odd number of them. A quote after a backslash is no quote.</summary>
    private static List<(int Start, int End)> Quoted(TextSnapshot text, LineSpan line)
    {
        List<int> marks = [];
        for (int p = line.Start; p < line.End; p++)
        {
            if (IsQuote(text, line, p))
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

    /// <summary>%: the bracket matching the first one at or after the caret on its line, inclusive; with a count, that
    /// percentage of the way through the file, linewise.</summary>
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
        return Partner(text, at, Brackets[kind & ~1], Brackets[kind | 1]) is var partner and >= 0 ? new VimTarget(partner, VimMotionKind.Inclusive) : null;
    }

    /// <summary>Whether a backslash, not itself escaped, stands before the place.</summary>
    public static bool Escaped(TextSnapshot text, int offset)
    {
        int slashes = 0;
        for (int p = offset - 1; p >= 0 && text[p] == '\\'; p--)
        {
            slashes++;
        }
        return slashes % 2 == 1;
    }

    /// <summary>The bracket matching the one at <paramref name="at"/>, or -1, counting brackets as <see cref="BracketCounting"/>
    /// counts them from it.</summary>
    public static int Partner(TextSnapshot text, int at, char open, char close)
    {
        bool forward = text[at] == open;
        BracketCounting counting = new(text, at);
        int depth = 0;
        for (int p = at; forward ? p < text.Length : p >= 0; p += forward ? 1 : -1)
        {
            char c = text[p];
            if ((c != open && c != close) || (p != at && !counting.Counts(p)))
            {
                continue;
            }
            // Forward an opening one goes deeper, backward a closing one.
            depth += (c == open) == forward ? 1 : -1;
            if (depth == 0)
            {
                return p;
            }
        }
        return -1;
    }

    /// <summary>Which brackets count from a place, as % and [( count them: an escaped one only from an escaped place, none
    /// between a character's single quotes, and of those between double quotes on a line with an even number of them, on
    /// the place's own line those with an even number of quotes between them and the place, on another those outside.</summary>
    private sealed class BracketCounting(TextSnapshot text, int from)
    {
        private readonly bool escaped = Escaped(text, from);
        private readonly LineSpan home = LineOf(text, from);
        private LineSpan quotesOf = LineOf(text, from);
        private List<(int Start, int End)> quotes = Quoted(text, LineOf(text, from));

        /// <summary>How many quotes stand on the place's line before each column of it, counted once it is asked.</summary>
        private int[]? quotesBefore;

        public bool Counts(int p)
        {
            if (Escaped(text, p) != escaped || InCharacterQuotes(text, p))
            {
                return false;
            }
            if (p < quotesOf.Start || p > quotesOf.End)
            {
                quotesOf = LineOf(text, p);
                quotes = Quoted(text, quotesOf);
            }
            if (quotesOf.Start != home.Start)
            {
                return !quotes.Exists(q => q.Start < p && p < q.End);
            }
            if (quotes.Count == 0)
            {
                return true;
            }
            if (quotesBefore is null)
            {
                quotesBefore = new int[home.Length + 1];
                for (int q = home.Start; q < home.End; q++)
                {
                    quotesBefore[q - home.Start + 1] = quotesBefore[q - home.Start] + (IsQuote(text, home, q) ? 1 : 0);
                }
            }
            int low = Math.Min(p, from) + 1 - home.Start;
            int high = Math.Max(p, from) - home.Start;
            return (high > low ? quotesBefore[high] - quotesBefore[low] : 0) % 2 == 0;
        }
    }

    /// <summary>Whether a double quote stands at a place of a line, one after a backslash being none.</summary>
    private static bool IsQuote(TextSnapshot text, LineSpan line, int p) => text[p] == '"' && (p == line.Start || text[p - 1] != '\\');

    /// <summary>} and {: past the next (or last) run of lines with text to the empty line or macro line (<see cref="MacroLine"/>)
    /// after (before) it, or the text's end (start), where a } stands on the last character, which an operator takes too; a
    /// count with paragraphs left over at the text's edge fails.</summary>
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
                if (!first && passedText && Bound(text, line))
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

    /// <summary>]] and [[ (][ and [], <paramref name="closing"/>): to the next (previous) line beginning with a { (a }), a form
    /// feed or a section macro (<see cref="SectionMacros"/>), under an operator ]] also to below a } line. With none left,
    /// the last (first) line's first non-blank; under an operator the text's start, ][ the last line's, ]] the last line
    /// whole but where empty or a } not gone below, from which itself nowhere (a case operator, <paramref name="casing"/>,
    /// takes the rest of it below the first line). A count with sections left over fails.</summary>
    public static VimTarget? Section(TextSnapshot text, int caret, int n, bool forward, bool closing, bool operating, bool casing, bool breakEnds)
    {
        int line = text.GetLineFromOffset(caret);
        int from = line;
        int last = LastLine(text, breakEnds);
        int step = forward ? 1 : -1;
        char brace = closing ? '}' : '{';
        bool stopsAtClose = operating && forward && !closing;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            found = false;
            for (bool first = true; ; first = false)
            {
                if (!first && (Begins(line, brace) || SectionLine(text, line) || (stopsAtClose && Begins(line, '}'))))
                {
                    found = true;
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
        bool below = found && stopsAtClose && Begins(line, '}') && line < last;
        if (below)
        {
            line++;
        }
        if (forward && operating && line == last)
        {
            LineSpan end = text.GetLine(line);
            bool closes = end.Length > 0 && text[end.Start] == '}';
            if (closes && !closing && line == from)
            {
                return new VimTarget(casing && line > 0 ? end.End : caret, VimMotionKind.Exclusive);
            }
            return closing || (closes && !below) || end.Length == 0 ? new VimTarget(end.Start, VimMotionKind.Exclusive)
                : new VimTarget(SettledOn(text, end, end.End), VimMotionKind.Inclusive);
        }
        if (found)
        {
            return new VimTarget(text.GetLineStart(line), VimMotionKind.Exclusive);
        }
        return new VimTarget(operating ? 0 : FirstNonBlank(text, line, breakEnds), VimMotionKind.Exclusive);

        bool Begins(int at, char c) => at >= 0 && text.GetLine(at) is { Length: > 0 } span && text[span.Start] == c;
    }

    /// <summary>The nroff macros a section begins at, as Vim's 'sections' option names them by default: a line of a dot and
    /// one of them, a one-letter one then ending or going on after a blank.</summary>
    private static readonly string[] SectionMacros = ["SH", "NH", "H", "HU", "nh", "sh"];

    /// <summary>The nroff macros a paragraph begins at, as Vim's 'paragraphs' option names them by default.</summary>
    private static readonly string[] ParagraphMacros = ["IP", "LP", "PP", "QP", "P", "TP", "HP", "LI", "Pp", "Lp", "It", "pp", "lp", "ip", "bp"];

    /// <summary>Whether a line begins a section whatever its braces: a form feed, or a section macro, first.</summary>
    private static bool SectionLine(TextSnapshot text, int line) =>
        text.GetLine(line) is { Length: > 0 } span && (text[span.Start] == '\f' || Macro(text, span, SectionMacros));

    /// <summary>Whether a line begins a paragraph whatever is around it: a section's line (<see cref="SectionLine"/>), or one
    /// beginning with a paragraph macro, which paragraphs and sections end before and take as their first.</summary>
    public static bool MacroLine(TextSnapshot text, int line) =>
        SectionLine(text, line) || text.GetLine(line) is { Length: > 0 } span && Macro(text, span, ParagraphMacros);

    /// <summary>Whether a line begins with a dot and one of the macros, a one-letter one then ending or going on after a blank.</summary>
    private static bool Macro(TextSnapshot text, LineSpan span, string[] macros)
    {
        if (text[span.Start] != '.' || span.Length < 2)
        {
            return false;
        }
        char first = text[span.Start + 1];
        char? second = span.Length > 2 ? text[span.Start + 2] : null;
        foreach (string macro in macros)
        {
            if (macro[0] == first && (macro.Length == 2 ? second == macro[1] : second is null or ' '))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>[{ [( and ]} ]): the [count]th unmatched open bracket before the caret, or close bracket after it, as far as
    /// there are, exclusive; a pair between counted out, with brackets counted as % counts them (<see cref="BracketCounting"/>). Fails
    /// where there is none.</summary>
    public static VimTarget? Unmatched(TextSnapshot text, int caret, int n, char open, char close, bool forward)
    {
        BracketCounting counting = new(text, caret);
        int at = caret;
        for (int i = 0; i < n; i++)
        {
            int depth = 0;
            int p = at + (forward ? 1 : -1);
            for (; forward ? p < text.Length : p >= 0; p += forward ? 1 : -1)
            {
                char c = text[p];
                if ((c != open && c != close) || !counting.Counts(p))
                {
                    continue;
                }
                if (c == (forward ? open : close))
                {
                    depth++;
                }
                else if (depth == 0)
                {
                    break;
                }
                else
                {
                    depth--;
                }
            }
            if (forward ? p >= text.Length : p < 0)
            {
                break;
            }
            at = p;
        }
        return at == caret ? null : new VimTarget(at, VimMotionKind.Exclusive);
    }

    /// <summary>Whether a line is empty; a line of blanks is not.</summary>
    public static bool Blank(TextSnapshot text, int line) => text.GetLine(line).Length == 0;

    /// <summary>Whether a line bounds paragraphs and sentences: an empty line, or a macro line (<see cref="MacroLine"/>).</summary>
    public static bool Bound(TextSnapshot text, int line) => Blank(text, line) || MacroLine(text, line);

    /// <summary>Whether a place is the start of a line that bounds sentences (<see cref="Bound"/>), the one place on it where
    /// sentences break; past a macro line's first character its text reads as any other.</summary>
    private static bool BoundAt(TextSnapshot text, int p) => LineOf(text, p) is var line && p == line.Start && Bound(text, line.Line);

    /// <summary>) and (: the start of the next sentence, or of this one or the one before; exclusive. Past the last
    /// sentence ) stands on the text's last character and takes it, from there too; ( stops at the text's very start,
    /// twice where the text begins with an empty line. A count with sentences left over at the text's edge fails.</summary>
    public static VimTarget? Sentence(TextSnapshot text, int caret, int n, bool forward, bool breakEnds)
    {
        int last = LastLine(text, breakEnds);
        int textEnd = text.GetLine(last).End;
        int at = Math.Min(caret, textEnd);
        if (forward)
        {
            for (int i = 0; i < n; i++)
            {
                int next = SentenceForward(text, last, at);
                if (next == Stuck)
                {
                    return null;
                }
                // From the text's end, where a sentence ending the text or a run of empty lines took it, ) goes nowhere.
                if (next < 0 || next >= textEnd && at >= textEnd)
                {
                    if (i < n - 1)
                    {
                        return null;
                    }
                    next = textEnd;
                }
                at = next;
            }
            int end = Settled(text, textEnd, breakEnds);
            return at >= textEnd ? new VimTarget(end, LineOf(text, end).Length == 0 ? VimMotionKind.Exclusive : VimMotionKind.Inclusive)
                : new VimTarget(at, VimMotionKind.Exclusive);
        }
        bool topAgain = Bound(text, 0);
        for (int i = 0; i < n; i++)
        {
            int back = SentenceBack(text, last, at);
            if (back >= 0)
            {
                at = back;
                continue;
            }
            if (i == 0)
            {
                // From the text's start one ( stays there, an operator taking nothing; more fail.
                return n == 1 ? new VimTarget(at, VimMotionKind.Exclusive) : null;
            }
            if (!topAgain || at != 0)
            {
                return null;
            }
            topAgain = false;
        }
        return new VimTarget(at, VimMotionKind.Exclusive);
    }

    /// <summary>Where one ) goes from a place: from a bounding line's start past it (<see cref="PastBound"/>), else past
    /// the end of the sentence the place belongs to; the text's end (its last line's end) where none is, -1 where a count
    /// can't go on from there, <see cref="Stuck"/> where ) fails. A gap the place stands in counts only past <paramref
    /// name="beyond"/>.</summary>
    private static int SentenceForward(TextSnapshot text, int last, int caret, int beyond = -1)
    {
        int gap = -1;
        int p = BoundAt(text, caret) ? caret : SentenceTailBack(text, last, caret, out gap);
        if (gap > beyond)
        {
            return FromGap(text, last, gap);
        }
        if (BoundAt(text, p))
        {
            return PastBound(text, last, p);
        }
        int start = SentenceStartOf(text, last, p, p, top: false);
        return BoundAt(text, start) && start > p ? start : StartAfter(text, last, start);
    }

    /// <summary>What <see cref="SentenceForward"/> answers where ) fails outright, from a macro line's start on the last line.</summary>
    private const int Stuck = -2;

    /// <summary>Where ) goes from a bounding line's start: from an empty line past its run (<see cref="PastEmptyLines"/>),
    /// from a macro line to the next line's first text; <see cref="Stuck"/> on the last line.</summary>
    private static int PastBound(TextSnapshot text, int last, int p)
    {
        LineSpan line = LineOf(text, p);
        return line.Length == 0 ? PastEmptyLines(text, last, p) : line.Line < last ? FirstText(text, last, text.GetLine(line.Line + 1).Start) : Stuck;
    }

    /// <summary>The start of the sentence after the one starting at <paramref name="start"/>: past its first end, or the
    /// first empty line after it; -1 where neither comes.</summary>
    private static int StartAfter(TextSnapshot text, int last, int start)
    {
        for (int q = start; q >= 0; q = Inc(text, last, q))
        {
            if (q != start && BoundAt(text, q))
            {
                return q;
            }
            if (EndsSentenceAt(text, q))
            {
                return StartAfterEnd(text, last, q);
            }
        }
        return -1;
    }

    /// <summary>Whether a line has only blanks from an offset to its end.</summary>
    internal static bool BlanksTo(TextSnapshot text, int from, int lineEnd)
    {
        for (int at = from; at < lineEnd; at++)
        {
            if (text[at] is not (' ' or '\t'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>is and as: <paramref name="n"/> of the sentences and the runs of white between them, from the one the
    /// caret is in; the outer one takes each sentence with the white after it, or before where none is after.</summary>
    public static VimSpan? SentenceObject(TextSnapshot text, int caret, int n, bool inner, bool breakEnds) => SentenceObject(text, caret, n, inner, breakEnds, asText: false);

    /// <summary><see cref="SentenceObject(TextSnapshot, int, int, bool, bool)"/>, a place past a macro line's first character
    /// read as any other text where <paramref name="asText"/>.</summary>
    private static VimSpan? SentenceObject(TextSnapshot text, int caret, int n, bool inner, bool breakEnds, bool asText)
    {
        int last = LastLine(text, breakEnds);
        int textEnd = text.GetLine(last).End;
        caret = Math.Min(caret, textEnd);
        if (last > 0 && text.GetLine(last) is { Length: > 0 } closing && Bound(text, last) && caret < closing.Start
            && text.GetLine(last - 1) is var penult && (!Bound(text, last - 1) || caret > penult.Start) && caret >= penult.Start)
        {
            // Read with a macro line ending the text emptied, as no sentence of its own, from the line above it.
            TextSnapshot emptied = text.Replace(closing.Start, closing.Length, string.Empty);
            return SentenceObject(emptied, caret, n, inner, breakEnds, asText) is { } found
                ? found with { Start = Math.Min(found.Start, closing.Start), End = Math.Min(found.End, closing.Start) } : null;
        }
        if (!EmptyAt(text, caret) && EndsSentenceAt(text, caret) && Dec(text, caret) is var above && above >= 0 && EmptyAt(text, above))
        {
            // An end mark opening a paragraph stands for the empty line before it.
            caret = above;
        }
        if ((!inner || n > 1) && !EmptyAt(text, caret) && LeadingBlank(text, last, caret))
        {
            // In blank lines opening the text, as and a count of is take its first place alone.
            LineSpan first = text.GetLine(0);
            return new VimSpan(0, first.Length > 1 ? 1 : first.EndIncludingBreak, false);
        }
        if (LineOf(text, caret) is { Length: > 0 } ending && ending.Line == last && Bound(text, last))
        {
            int blanks = ending.End;
            while (blanks > ending.Start + 1 && text[blanks - 1] is ' ' or '\t')
            {
                blanks--;
            }
            if (caret >= blanks && blanks < ending.End && blanks > ending.Start + 1)
            {
                // In the blanks ending it after its text, an odd count of is takes them, else nothing at their start.
                return new VimSpan(blanks, inner && n % 2 == 1 ? ending.End : blanks, false);
            }
            // As on an empty last line, past the line's start for an odd count of is alone; a lone macro line, its first character.
            return last == 0 ? new VimSpan(0, 1, false) : caret == ending.Start || (inner && n % 2 == 1) ? EmptyEnd(text, last, last, n, inner, breakEnds)
                : new VimSpan(ending.Start, ending.Start, false);
        }
        int start = -1;
        int end;
        int next;
        bool white = false;
        if (MacroAt(text, last, caret) is { } macro && !(asText && caret > macro.Start) && !(Bound(text, macro.Line + 1) && text[macro.End - 1] is ' ' or '\t'))
        {
            if (!(caret > macro.Start && OwnSentence(text, macro)) && SentenceBack(text, last, Forward(text, last, caret)) is var found && !BoundAt(text, found))
            {
                // On a macro line the sentence is the one ( finds from where ) goes, past the line's first character read as any text.
                return SentenceObject(text, found, n, inner, breakEnds, asText: true);
            }
            (start, end) = (macro.Start, text.GetLine(macro.Line + 1).Start);
            next = WhiteEnd(text, last, start, end);
        }
        else if (EmptyAt(text, caret))
        {
            LineSpan line = LineOf(text, caret);
            int runEnd = line.Line;
            while (runEnd < last && text.GetLine(runEnd + 1).Length == 0)
            {
                runEnd++;
            }
            if (line.Line == last || runEnd == last)
            {
                return EmptyEnd(text, last, line.Line, n, inner, breakEnds);
            }
            start = text.GetLine(runEnd).Start;
            end = SentenceEnd(text, last, start);
            next = Math.Max(end, Forward(text, last, start));
        }
        else
        {
            int mark = EndMarkBehind(text, last, caret, out int gap);
            int following = mark >= 0 && mark < caret ? StartAfterEnd(text, last, mark) : -1;
            if (following > caret && CharAt(text, caret) is ' ' or '\t')
            {
                // In the white after a sentence's end.
                int whiteStart = mark + 1;
                while (IsCloser(CharAt(text, whiteStart)))
                {
                    whiteStart++;
                }
                (end, next, white) = (TextEndBefore(text, last, mark, whiteStart), following, true);
            }
            else if (following >= 0 && following < caret)
            {
                // Among the marks and closers an end carries over from the line before: that end's sentence.
                int before = Dec(text, mark);
                start = before < 0 ? SentenceStartOf(text, last, mark, mark, top: true)
                    : EmptyAt(text, before) ? before : SentenceStartOf(text, last, before, mark, top: true);
                next = following;
                end = TextEndBefore(text, last, start, next);
            }
            else if (BlankParagraph(text, last, caret) is { } blank)
            {
                // Blank lines opening a paragraph with no text: their white.
                (end, next, white) = (blank.Start, blank.End, true);
            }
            else
            {
                start = Math.Max(0, SentenceBack(text, last, Inc(text, last, caret) is var after && after >= 0 ? after : caret));
                if (BoundAt(text, start) && start < caret && LineOf(text, start).Line != LineOf(text, caret).Line)
                {
                    start = FirstText(text, last, text.GetLine(LineOf(text, start).Line + 1).Start);
                }
                bool gapBeforeText = gap >= 0 && gap < textEnd && !EmptyAt(text, gap);
                if ((gap < 0 || gapBeforeText) && Forward(text, last, start) == caret)
                {
                    // At the next sentence's start, that sentence.
                    start = caret;
                }
                if (start == caret && gapBeforeText && IsSentenceEnd(CharAt(text, caret)))
                {
                    // A lone end mark before text is no start of its own.
                    start = FirstText(text, last, gap);
                }
                next = Forward(text, last, start);
                if (next <= start)
                {
                    // A sentence ) cannot pass: nothing, at its start.
                    return new VimSpan(start, start, false);
                }
                end = SentenceEnd(text, last, start);
                white = end <= caret && caret < next;
            }
        }
        int from = white ? end : start;
        int to = from;
        if (!inner && white)
        {
            // as from white: n times the white and the sentence after it.
            bool toEmpty = false;
            for (int i = 0; i < n; i++)
            {
                int sentence = next;
                if (MacroStart(text, last, sentence) && LineOf(text, sentence) is var macroLine && text.GetLine(macroLine.Line + 1).Start is var below
                    && WhiteEnd(text, last, sentence, below) is var linesWhite && WrittenIn(text, below, linesWhite) && text[below] is not (' ' or '\t'))
                {
                    // A macro line after the white is a sentence whose white, an unindented next line's sentence, goes with it.
                    (to, next, toEmpty) = (linesWhite, linesWhite, false);
                    continue;
                }
                LineSpan sentenceLine = LineOf(text, sentence);
                if (i == 0 && sentenceLine.Length == 0 && EmptyRunEnd(text, last, sentenceLine.Line) is var runEnd && runEnd < last
                    && text.GetLine(runEnd + 1) is var textLine && TextNavigation.IndentEnd(text, textLine) == textLine.Start)
                {
                    // From white ending a paragraph: to the last empty line's start past one; counted, a sentence after for each more.
                    if (n == 1)
                    {
                        // White opening its line (blanks alone) takes the lines to the last empty one whole.
                        return runEnd > sentenceLine.Line && from == LineOf(text, from).Start ? new VimSpan(from, text.GetLine(runEnd).Start, true)
                            : new VimSpan(from, runEnd > sentenceLine.Line ? text.GetLine(runEnd).Start : from, false, EndKept: true);
                    }
                    (to, next, toEmpty) = (text.GetLine(runEnd).Start, textLine.Start, true);
                    continue;
                }
                int textUntil = SentenceEnd(text, last, sentence);
                int after = Math.Max(textUntil, Forward(text, last, sentence));
                bool marked = EndsWithMark(text, sentence, textUntil);
                LineSpan until = LineOf(text, textUntil);
                int empty = until.Length == 0 ? until.Line : until.Line + 1;
                bool blanksEnding = until.Length > 0 && textUntil < until.End && BlanksTo(text, textUntil, until.End);
                if (i < n - 1 && (until.Length == 0 || textUntil == until.End || blanksEnding)
                    && sentenceLine.Line < empty && empty < last
                    && text.GetLine(empty).Length == 0 && TextNavigation.IndentEnd(text, text.GetLine(empty - 1)) < text.GetLine(empty - 1).End)
                {
                    // Counted past its paragraph: the sentence, the empty lines to the last one's start, the sentence after.
                    int lastEmpty = EmptyRunEnd(text, last, empty);
                    // Empty lines ending the text: to the last one but one's start.
                    to = text.GetLine(lastEmpty == last ? lastEmpty - 1 : lastEmpty).Start;
                    if (++i == n - 1 || lastEmpty == last)
                    {
                        // Past blanks ending the paragraph, one empty line before an unindented one ends the count at the sentence's mark.
                        return blanksEnding && lastEmpty == empty && lastEmpty < last && text.GetLine(lastEmpty + 1) is var opening
                            && TextNavigation.IndentEnd(text, opening) == opening.Start ? new VimSpan(from, textUntil, false) : new VimSpan(from, to, false, EndKept: true);
                    }
                    next = FirstText(text, last, text.GetLine(lastEmpty + 1).Start);
                    toEmpty = true;
                    continue;
                }
                bool closes = (textUntil == after && after >= textEnd)
                    || ((until.Length == 0 || (i == n - 1 && textUntil == until.Start && MacroLine(text, until.Line))) && until.Line > LineOf(text, sentence).Line)
                    || (textUntil == until.End && until.Line < text.LineCount - 1 && text.GetLine(until.Line + 1).Length == 0);
                if (!(closes && !marked) && (textUntil < after || after < textEnd && marked))
                {
                    (to, next, toEmpty) = (textUntil, after, false);
                    continue;
                }
                if (!closes)
                {
                    return new VimSpan(from, i == 0 ? from : to, false, EndKept: i > 0 && toEmpty);
                }
                // Ending the text or its paragraph with no mark: taken but for a last word after a blank.
                int lastChar = Dec(text, textUntil);
                int cut = Dec(text, lastChar);
                bool blanks = cut > sentence && CharAt(text, cut) is ' ' or '\t';
                while (cut > sentence && CharAt(text, cut) is ' ' or '\t')
                {
                    cut = Dec(text, cut);
                }
                // A word opening its line, unindented, is taken whole after another sentence, one character alone only below a line ending in none of blanks.
                bool belowBlanks = sentenceLine.Line > 0 && text.GetLine(sentenceLine.Line - 1) is { Length: > 0 } prior && CharAt(text, prior.End - 1) is ' ' or '\t';
                int kept = blanks ? cut + 1 : lastChar > sentence || (i > 0 && sentence == sentenceLine.Start && !belowBlanks) ? lastChar + 1 : sentence;
                // Taken whole up to an empty line, it ends at that line, which takes whole lines from an indent.
                LineSpan keptLine = LineOf(text, kept);
                int keptTo = kept == keptLine.End && keptLine.Line < last && text.GetLine(keptLine.Line + 1).Length == 0
                    ? keptLine.EndIncludingBreak : kept;
                return kept > sentence ? new VimSpan(from, keptTo, false) : new VimSpan(from, i == 0 ? from : to, false, EndKept: i > 0 && toEmpty);
            }
            return new VimSpan(from, to, false);
        }
        bool emptyFirst = !white && EmptyAt(text, start);
        bool lastWhiteEmpty = false;
        // Each unit is a sentence or the white after it; is takes n of them, as n sentences with their white.
        bool onWhite = white;
        int sentenceTo = to;
        bool macroWhite = false;
        for (int i = 0; i < (inner ? n : 2 * n); i++)
        {
            if (i > 0 && !onWhite && (start >= textEnd || (start == text.GetLine(last).Start && last > 0 && text.GetLine(last).Length > 0 && Bound(text, last))))
            {
                // Past the text's end a count takes what there is, is ending by turns at its last sentence and white, but for a macro line's.
                to = inner && (n - i) % 2 == 1 && !macroWhite ? sentenceTo : to;
                break;
            }
            sentenceTo = onWhite ? sentenceTo : end;
            to = onWhite ? next : end;
            // A macro line's white is the next line's sentence where that holds text, else blanks as any white.
            macroWhite = onWhite && MacroStart(text, last, start) && WrittenIn(text, end, next);
            // A macro line's sentence after it with no white of its own beyond it leaves as none too.
            lastWhiteEmpty = onWhite && (next == end || (macroWhite && (next >= textEnd || next == LineOf(text, next).Start)));
            if (onWhite)
            {
                start = next;
                end = SentenceEnd(text, last, start);
                next = WhiteEnd(text, last, start, end);
            }
            onWhite = !onWhite;
        }
        if (!inner && lastWhiteEmpty && MacroStart(text, last, from) && WhiteBefore(text, from) < from && to > from && to < textEnd && to == LineOf(text, to).Start)
        {
            // Over a macro line, as taking the white before ends at the line's end where it would end at the next's start.
            to = text.GetLine(LineOf(text, to).Line - 1).End;
        }
        if (!inner && lastWhiteEmpty)
        {
            int blankFrom = emptyFirst ? BlankLinesBefore(text, from) : from;
            if (emptyFirst && n == 1 && LineOf(text, blankFrom).Line > 0 && text.GetLine(LineOf(text, blankFrom).Line - 1) is { Length: > 0 } prior
                && text[prior.End - 1] is ' ' or '\t' && TextNavigation.IndentEnd(text, prior) < prior.End)
            {
                // An empty line after a line ending in blanks, lines of blanks alone between too: those blanks and the breaks, the empty line's own cut off.
                return new VimSpan(WhiteBefore(text, blankFrom), LineOf(text, from).EndIncludingBreak, false);
            }
            from = WhiteBefore(text, blankFrom);
        }
        return new VimSpan(from, Math.Max(from, to), false);
    }

    /// <summary>The macro line a place is on, short of the last line; null on any other.</summary>
    private static LineSpan? MacroAt(TextSnapshot text, int last, int p) =>
        LineOf(text, p) is { Length: > 0 } line && line.Line < last && Bound(text, line.Line) ? line : null;

    /// <summary>The start of the macro line a place is on, short of the last line; null on any other line.</summary>
    public static int? MacroLineStart(TextSnapshot text, int p, bool breakEnds) => MacroAt(text, LastLine(text, breakEnds), p)?.Start;

    /// <summary>Whether a place is the start of a macro line short of the last line, a sentence of its own.</summary>
    private static bool MacroStart(TextSnapshot text, int last, int p) => p >= 0 && BoundAt(text, p) && MacroAt(text, last, p) is not null;

    /// <summary>Where the white after a macro line's own sentence ends (<see cref="WhiteEnd"/>), from the line's start short
    /// of the last line.</summary>
    public static int MacroWhiteEnd(TextSnapshot text, int start, bool breakEnds) =>
        WhiteEnd(text, LastLine(text, breakEnds), start, text.GetLine(LineOf(text, start).Line + 1).Start);

    /// <summary>Where the white after the sentence from <paramref name="start"/> to <paramref name="end"/> ends, where the
    /// next sentence begins; after a macro line's own sentence, where the next line's sentence ends unless that line is a
    /// bounding one (<see cref="Bound"/>), or the blanks after it where the macro line's text ends a sentence itself, as is
    /// and as count what follows it.</summary>
    private static int WhiteEnd(TextSnapshot text, int last, int start, int end)
    {
        if (BoundAt(text, start) && LineOf(text, start) is { Length: > 0 } macro)
        {
            if (macro.Line < last && OwnSentence(text, macro))
            {
                // A macro line ending its own sentence has the blanks after it as white, as any sentence's end.
                return Math.Max(end, FirstText(text, last, text.GetLine(macro.Line + 1).Start));
            }
            return macro.Line < last && !Bound(text, macro.Line + 1) ? Math.Max(end, Forward(text, last, text.GetLine(macro.Line + 1).Start)) : end;
        }
        return Math.Max(end, Forward(text, last, start));
    }

    /// <summary>The lines of blanks the place is on where they open a paragraph after an empty or macro line with no text
    /// before the next such line or the text's end: from their start to there; null otherwise.</summary>
    private static TextRange? BlankParagraph(TextSnapshot text, int last, int caret)
    {
        LineSpan line = LineOf(text, caret);
        int top = line.Line;
        while (top > 0 && text.GetLine(top - 1) is { Length: > 0 } above && TextNavigation.IndentEnd(text, above) == above.End)
        {
            top--;
        }
        if (top == 0 || !Bound(text, top - 1) || TextNavigation.IndentEnd(text, line) < line.End)
        {
            return null;
        }
        int paragraph = text.GetLine(top).Start;
        int first = FirstText(text, last, paragraph);
        return first >= text.GetLine(last).End || BoundAt(text, first) ? TextRange.FromBounds(paragraph, first) : null;
    }

    /// <summary>Whether the place is on the lines of blanks alone that open the text, with no text before an empty line
    /// or the text's end.</summary>
    private static bool LeadingBlank(TextSnapshot text, int last, int caret)
    {
        int line = 0;
        while (line <= last && text.GetLine(line) is { Length: > 0 } span && TextNavigation.IndentEnd(text, span) == span.End)
        {
            line++;
        }
        return line > LineOf(text, caret).Line && (line > last || text.GetLine(line).Length == 0);
    }

    /// <summary>Whether the text from <paramref name="start"/> to <paramref name="end"/> ends with a sentence's end mark
    /// and the closers after it.</summary>
    private static bool EndsWithMark(TextSnapshot text, int start, int end)
    {
        int q = end;
        while (q > start && text[q - 1] is '\n' or '\r')
        {
            q--;
        }
        while (q > start && IsCloser(text[q - 1]))
        {
            q--;
        }
        return q > start && IsSentenceEnd(text[q - 1]);
    }

    /// <summary>Back from a sentence's start over the blanks before it, on through line breaks after lines that end in a
    /// blank, never past an empty line.</summary>
    private static int WhiteBefore(TextSnapshot text, int from)
    {
        while (from > 0)
        {
            LineSpan line = LineOf(text, from);
            if (from > line.Start && text[from - 1] is ' ' or '\t')
            {
                from--;
            }
            else if (from == line.Start && line.Line > 0 && text.GetLine(line.Line - 1) is { Length: > 0 } above && text[above.End - 1] is ' ' or '\t')
            {
                from = above.End;
            }
            else
            {
                break;
            }
        }
        return from;
    }

    /// <summary>Back from an empty line over the whole lines of blanks before it.</summary>
    private static int BlankLinesBefore(TextSnapshot text, int from)
    {
        int line = LineOf(text, from).Line;
        while (line > 0 && text.GetLine(line - 1) is { Length: > 0 } above && TextNavigation.IndentEnd(text, above) == above.End)
        {
            line--;
        }
        return text.GetLine(line).Start;
    }

    /// <summary>The first mark that ends a sentence in the end ) takes the place back to (<see cref="SentenceTailBack"/>),
    /// -1 where none does.</summary>
    private static int EndMarkBehind(TextSnapshot text, int last, int caret, out int gap)
    {
        int first = SentenceTailBack(text, last, caret, out gap);
        int upTo = BlanksBack(text, caret, toEmpty: false);
        for (int q = first; IsSentenceEnd(CharAt(text, first)) && q >= 0 && q <= upTo; q = Inc(text, last, q))
        {
            if (EndsSentenceAt(text, q))
            {
                return q;
            }
        }
        return -1;
    }

    /// <summary>is and as on a run of empty lines that ends the text, or on its last line: the sentence before the run as
    /// whole lines, or the white the line before ends with.</summary>
    private static VimSpan? EmptyEnd(TextSnapshot text, int last, int line, int n, bool inner, bool breakEnds)
    {
        if (line == last)
        {
            int here = text.GetLine(line).Start;
            if (!inner && line > 0)
            {
                return AsFromLastEmpty(text, line, here);
            }
            if (line == 0 || text.GetLine(line - 1) is not { Length: > 0 } before || !inner)
            {
                return new VimSpan(here, here, false);
            }
            int blank = BlanksBefore(text, before, before.End);
            if (blank == before.End)
            {
                return new VimSpan(here, here, false);
            }
            int top = before.Line;
            while (blank == before.Start && top > 0 && text.GetLine(top - 1) is { Length: > 0 } above && TextNavigation.IndentEnd(text, above) == above.End)
            {
                top--;
            }
            // Below blank lines, the white begins at the blanks ending the line of text above them, where it has some.
            int white = blank > before.Start ? blank
                : top > 0 && text.GetLine(top - 1) is { Length: > 0 } textAbove && BlanksBefore(text, textAbove, textAbove.End) is var trailing
                    && trailing < textAbove.End ? trailing : text.GetLine(top).Start;
            // A count: even, nothing at the white's start; odd, the white, whole lines where it begins one.
            return n % 2 == 0 ? new VimSpan(white, white, false)
                : white == text.GetLine(top).Start && blank == before.Start ? new VimSpan(white, before.Start, true) : new VimSpan(white, before.End, false);
        }
        int first = line;
        while (first > 0 && text.GetLine(first - 1).Length == 0)
        {
            first--;
        }
        if (first == 0)
        {
            return new VimSpan(0, text.GetLine(last - 1).Start, true);
        }
        // Before a run of empty lines that ends the text, the sentence the text before it ends with.
        LineSpan previous = text.GetLine(first - 1);
        return SentenceObject(text, BlanksBack(text, previous.End, toEmpty: false), n, inner, breakEnds);
    }

    /// <summary>The last line of the run of empty lines from <paramref name="line"/>.</summary>
    private static int EmptyRunEnd(TextSnapshot text, int last, int line)
    {
        int end = line;
        while (end < last && text.GetLine(end + 1).Length == 0)
        {
            end++;
        }
        return end;
    }

    /// <summary>as on the text's last line, empty: after a line ending in one character after a blank, that character
    /// with the blanks before it (alone on its line, the lines of blanks above and the blanks ending the line of text above them); after one empty line, the blanks
    /// ending the last line of text and its break, or that line where it is blank, to here; else nothing, from where white
    /// ending the text begins.</summary>
    private static VimSpan AsFromLastEmpty(TextSnapshot text, int line, int here)
    {
        // Where white ending the text begins, never past an empty line.
        int white = here;
        while (white > 0 && Dec(text, white) is var before && !EmptyAt(text, before) && CharAt(text, before) is ' ' or '\t')
        {
            white = before;
        }
        int lastChar = Dec(text, white);
        if (lastChar < 0)
        {
            return new VimSpan(here, here, false);
        }
        if (EmptyAt(text, lastChar))
        {
            if (white < here)
            {
                return new VimSpan(white, white, false);
            }
            // One empty line between: the blanks ending the last line of text and its break; more, nothing.
            int above = LineOf(text, lastChar).Line;
            while (above > 0 && text.GetLine(above).Length == 0)
            {
                above--;
            }
            LineSpan textLine = text.GetLine(above);
            int ending = BlanksBefore(text, textLine, textLine.End);
            return textLine.Length == 0 || ending == textLine.End || line != above + 2 ? new VimSpan(here, here, false)
                : ending == textLine.Start ? new VimSpan(ending, here, false)
                : new VimSpan(ending, text.GetLine(above + 1).EndIncludingBreak, false);
        }
        if (white < here)
        {
            // White ends the text: nothing, from where it begins.
            return new VimSpan(white, white, false);
        }
        LineSpan last = LineOf(text, lastChar);
        int word = lastChar;
        while (word > last.Start && CharAt(text, word - 1) is not (' ' or '\t'))
        {
            word--;
        }
        if (word != lastChar)
        {
            return new VimSpan(here, here, false);
        }
        int from = BlanksBefore(text, last, word);
        if (from == last.Start)
        {
            // Back over lines of blanks alone to the blanks ending the line of text above them, where it has some.
            int top = last.Line;
            while (top > 0 && text.GetLine(top - 1) is { Length: > 0 } blankLine && TextNavigation.IndentEnd(text, blankLine) == blankLine.End)
            {
                top--;
            }
            from = top > 0 && text.GetLine(top - 1) is { Length: > 0 } previous && BlanksBefore(text, previous, previous.End) is var trailing
                && trailing < previous.End ? trailing : top < last.Line ? text.GetLine(top).Start : from;
        }
        // From the text's first place it starts a place on, as the search back past the text's start does.
        return from < word ? new VimSpan(from == 0 ? 1 : from, here, false) : new VimSpan(here, here, false);
    }

    /// <summary>Where the blanks just before <paramref name="end"/> on a line begin; <paramref name="end"/> with none.</summary>
    private static int BlanksBefore(TextSnapshot text, LineSpan line, int end)
    {
        while (end > line.Start && text[end - 1] is ' ' or '\t')
        {
            end--;
        }
        return end;
    }

    /// <summary>Where the sentence at <paramref name="start"/> ends: a run of empty lines short of the last line at the
    /// next line's start, else its text's end.</summary>
    private static int SentenceEnd(TextSnapshot text, int last, int start)
    {
        LineSpan line = LineOf(text, start);
        if (line.Length > 0 && line.Line == last && BoundAt(text, start))
        {
            // From a macro line ending the text ) goes nowhere: its sentence is none.
            return start;
        }
        if (line.Length > 0)
        {
            return TextEndBefore(text, last, start, Forward(text, last, start));
        }
        int run = line.Line;
        while (run + 1 < last && text.GetLine(run + 1).Length == 0)
        {
            run++;
        }
        return run < last ? text.GetLine(run + 1).Start : line.End;
    }

    /// <summary>Where one ) goes from a place, the last line's end where it goes past the last sentence.</summary>
    private static int Forward(TextSnapshot text, int last, int p)
    {
        int next = SentenceForward(text, last, p);
        return next < 0 ? text.GetLine(last).End : next;
    }

    /// <summary>Where the text of a sentence from <paramref name="start"/> ends before <paramref name="next"/>: back over
    /// blanks and line ends; at a line's end, the next line's start.</summary>
    private static int TextEndBefore(TextSnapshot text, int last, int start, int next)
    {
        int end = next;
        while (end > start && text[end - 1] is ' ' or '\t' or '\n' or '\r')
        {
            end--;
        }
        LineSpan line = LineOf(text, end);
        return end == line.End && end > start && line.Line < last ? text.GetLine(line.Line + 1).Start : end;
    }

    /// <summary>Where ) goes from a place in a gap: nowhere further before the text's end, from an empty line past its
    /// run, before another line as from the text that line begins with.</summary>
    private static int FromGap(TextSnapshot text, int last, int gap)
    {
        int textEnd = text.GetLine(last).End;
        return gap >= textEnd ? -1 : BoundAt(text, gap) ? PastBound(text, last, gap) : SentenceForward(text, last, FirstText(text, last, gap), gap);
    }

    /// <summary>The first text after the run of empty lines the place is on, the last line's end where none is.</summary>
    private static int PastEmptyLines(TextSnapshot text, int last, int p)
    {
        int textEnd = text.GetLine(last).End;
        int line = LineOf(text, p).Line;
        while (line <= last && text.GetLine(line).Length == 0)
        {
            line++;
        }
        return line <= last ? FirstText(text, last, text.GetLine(line).Start) : textEnd;
    }

    /// <summary>Where one ( goes from a place: from an empty line to the start of the sentence before its run, else to
    /// the start of the sentence the place before it belongs to; -1 at the text's start.</summary>
    private static int SentenceBack(TextSnapshot text, int last, int caret)
    {
        int p;
        if (EmptyAt(text, caret))
        {
            int line = LineOf(text, caret).Line;
            while (line > 0 && text.GetLine(line - 1).Length == 0)
            {
                line--;
            }
            if (line == 0)
            {
                return caret != 0 ? 0 : -1;
            }
            LineSpan before = text.GetLine(line - 1);
            p = before.Start + Math.Max(0, before.Length - 1);
        }
        else
        {
            p = Dec(text, caret);
            if (p < 0)
            {
                int top = SentenceStartOf(text, last, caret, caret, top: true);
                return top != caret ? top : -1;
            }
            if (EmptyAt(text, p))
            {
                return p;
            }
        }
        p = BlanksBack(text, p, toEmpty: true);
        int q = p;
        while (q >= 0 && IsCloser(CharAt(text, q)))
        {
            q = Dec(text, q);
        }
        p = q >= 0 && IsSentenceEnd(CharAt(text, q)) ? q : p;
        if (EmptyAt(text, p))
        {
            return p;
        }
        if (IsSentenceEnd(CharAt(text, p)))
        {
            // The sentence before an end's mark is the one it ends.
            int before = Dec(text, p);
            if (before >= 0 && EmptyAt(text, before))
            {
                return before;
            }
            if (before >= 0)
            {
                return SentenceStartOf(text, last, before, p, top: true);
            }
        }
        return SentenceStartOf(text, last, p, p, top: true);
    }

    /// <summary>The start of the sentence holding the place: after the nearest sentence's end behind it whose sentence
    /// starts at or before <paramref name="limit"/>, the text after the nearest bounding line (<see cref="Bound"/>), that
    /// line itself where the place is on it, or the text's start; with <paramref name="top"/> a text opening with a
    /// sentence's end starts its first sentence after it.</summary>
    private static int SentenceStartOf(TextSnapshot text, int last, int p, int limit, bool top)
    {
        for (int q = p; ; )
        {
            if (BoundAt(text, q))
            {
                return LineOf(text, q).Line == LineOf(text, p).Line ? q : FirstText(text, last, text.GetLine(LineOf(text, q).Line + 1).Start);
            }
            if (EndsSentenceAt(text, q) && StartAfterEnd(text, last, q) <= limit)
            {
                return StartAfterEnd(text, last, q);
            }
            int before = Dec(text, q);
            if (before < 0)
            {
                return top && EndsSentenceAt(text, q) ? StartAfterEnd(text, last, q) : 0;
            }
            q = before;
        }
    }

    /// <summary>Where ) takes a place to stand for the sentence it belongs to: back over the blanks before it, then from
    /// a sentence's mark or a closing bracket or quote back to the first mark of the end it closes. A mark or closer
    /// opening a line carries on the line before's end where a closer or blank follows it or that line, of marks alone,
    /// carries on its own line before's (<see cref="JoinsLineBefore"/>); alone on a line it otherwise leaves the place in
    /// the <paramref name="gap"/> before the next line's start, or the text's end.</summary>
    private static int SentenceTailBack(TextSnapshot text, int last, int caret, out int gap)
    {
        gap = -1;
        int p = BlanksBack(text, caret, toEmpty: false);
        char c = CharAt(text, p);
        if (!IsSentenceEnd(c) && !IsCloser(c))
        {
            return p;
        }
        int best = IsSentenceEnd(c) ? p : -1;
        int held = -1;
        for (int x = p; ; )
        {
            int y = Dec(text, x);
            char before = y < 0 ? '\0' : CharAt(text, y);
            if (y < 0 || EmptyAt(text, y) || !IsSentenceEnd(before) && !IsCloser(before) || IsSentenceEnd(CharAt(text, x)) && !IsSentenceEnd(before))
            {
                break;
            }
            LineSpan line = LineOf(text, x);
            if (y < line.Start)
            {
                int after = AfterLineStart(text, last, line);
                bool closes = after >= 0 && (CharAt(text, after) is ' ' or '\t' || IsCloser(CharAt(text, after)))
                    || MarksOnly(text, LineOf(text, y)) && JoinsLineBefore(text, last, LineOf(text, y));
                // Alone on its line before text that is no end mark, a line's or the text's end, the place is in the gap.
                if (!closes && (held >= 0 || line.Length != 1 || after >= 0 && IsSentenceEnd(CharAt(text, after))))
                {
                    break;
                }
                if (!closes)
                {
                    held = after >= 0 ? after : line.End;
                }
            }
            x = y;
            best = IsSentenceEnd(before) ? x : best;
            if (held >= 0 && best >= 0)
            {
                gap = held;
                return p;
            }
        }
        return held >= 0 ? p : best >= 0 ? best : p;
    }

    /// <summary>What follows a line's first character: the next on the line, else the next line's start; -1 past the
    /// last line.</summary>
    private static int AfterLineStart(TextSnapshot text, int last, LineSpan line) =>
        line.Start + 1 < line.End ? line.Start + 1 : line.Line < last ? text.GetLine(line.Line + 1).Start : -1;

    /// <summary>Whether a line of end marks and closers carries on the end that closes the line before: its first
    /// character takes that line's last as ) walks back over an end, a blank or closer following it, or that line too
    /// being one that carries on the line before it.</summary>
    private static bool JoinsLineBefore(TextSnapshot text, int last, LineSpan line)
    {
        if (line.Line == 0 || text.GetLine(line.Line - 1) is not { Length: > 0 } before)
        {
            return false;
        }
        char first = text[line.Start];
        char end = text[before.End - 1];
        if (!IsSentenceEnd(end) && !IsCloser(end) || IsSentenceEnd(first) && !IsSentenceEnd(end))
        {
            return false;
        }
        int after = AfterLineStart(text, last, line);
        return after >= 0 && (CharAt(text, after) is ' ' or '\t' || IsCloser(CharAt(text, after)))
            || MarksOnly(text, before) && JoinsLineBefore(text, last, before);
    }

    /// <summary>Whether a line holds only sentences' end marks and closers.</summary>
    private static bool MarksOnly(TextSnapshot text, LineSpan line)
    {
        for (int i = line.Start; i < line.End; i++)
        {
            if (!IsSentenceEnd(text[i]) && !IsCloser(text[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Back from a blank over the blanks and line ends before it, as far as the text's start, onto an empty line
    /// only where <paramref name="toEmpty"/>.</summary>
    private static int BlanksBack(TextSnapshot text, int p, bool toEmpty)
    {
        while (CharAt(text, p) is ' ' or '\t' or '\0' && !EmptyAt(text, p))
        {
            int before = Dec(text, p);
            if (before < 0 || !toEmpty && EmptyAt(text, before))
            {
                return p;
            }
            p = before;
        }
        return p;
    }

    /// <summary>Where the sentence after the end at <paramref name="q"/> starts: past the closers after it on its line,
    /// then the blanks and line ends; an empty line where one comes first, the last line's end where nothing does.</summary>
    private static int StartAfterEnd(TextSnapshot text, int last, int q)
    {
        LineSpan line = LineOf(text, q);
        int p = q + 1;
        while (p < line.End && IsCloser(text[p]))
        {
            p++;
        }
        return p < line.End ? FirstText(text, last, p) : line.Line < last ? FirstText(text, last, text.GetLine(line.Line + 1).Start) : line.End;
    }

    /// <summary>The first character from a place on that is not a blank, an empty line where one comes first, the last
    /// line's end where there is none.</summary>
    private static int FirstText(TextSnapshot text, int last, int p)
    {
        while (!EmptyAt(text, p) && CharAt(text, p) is ' ' or '\t' or '\0')
        {
            int next = Inc(text, last, p);
            if (next < 0)
            {
                return p;
            }
            p = next;
        }
        return p;
    }

    /// <summary>Whether a '.', '!' or '?' stands at the place and ends a sentence: closers after it on its line, then a
    /// blank or the line's end.</summary>
    private static bool EndsSentenceAt(TextSnapshot text, int q)
    {
        if (!IsSentenceEnd(CharAt(text, q)))
        {
            return false;
        }
        LineSpan line = LineOf(text, q);
        int p = q + 1;
        while (p < line.End && IsCloser(text[p]))
        {
            p++;
        }
        return p >= line.End || text[p] is ' ' or '\t';
    }

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?';

    /// <summary>Whether anything but blanks and line breaks stands between two places.</summary>
    private static bool WrittenIn(TextSnapshot text, int from, int to)
    {
        for (int p = from; p < to; p++)
        {
            if (text[p] is not (' ' or '\t' or '\n' or '\r'))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a macro line's text past its first character is one sentence its line's end ends: its one end mark,
    /// closers after it alone, ending the line.</summary>
    private static bool OwnSentence(TextSnapshot text, LineSpan macro)
    {
        int q = macro.End - 1;
        while (q > macro.Start && IsCloser(text[q]))
        {
            q--;
        }
        if (q <= macro.Start || !IsSentenceEnd(text[q]))
        {
            return false;
        }
        for (int p = macro.Start + 1; p < q; p++)
        {
            if (EndsSentenceAt(text, p))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsCloser(char c) => c is ')' or ']' or '"' or '\'';

    /// <summary>The character at a place, '\0' at a line's end.</summary>
    private static char CharAt(TextSnapshot text, int p) => p < LineOf(text, p).End ? text[p] : '\0';

    /// <summary>Whether the place is an empty line.</summary>
    private static bool EmptyAt(TextSnapshot text, int p) => LineOf(text, p).Length == 0;

    /// <summary>The place before: the character before on the line, else the last character of the line before (its
    /// start where it is empty); -1 at the text's start.</summary>
    private static int Dec(TextSnapshot text, int p)
    {
        LineSpan line = LineOf(text, p);
        if (p > line.Start)
        {
            return p - 1;
        }
        if (line.Line == 0)
        {
            return -1;
        }
        LineSpan before = text.GetLine(line.Line - 1);
        return before.Start + Math.Max(0, before.Length - 1);
    }

    /// <summary>The place after: the next character on the line or its end, else the next line's start; -1 past the
    /// last line's end.</summary>
    private static int Inc(TextSnapshot text, int last, int p)
    {
        LineSpan line = LineOf(text, p);
        return p < line.End ? p + 1 : line.Line < last ? text.GetLine(line.Line + 1).Start : -1;
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
