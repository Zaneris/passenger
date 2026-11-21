using System.Text;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>gq's and gw's formatting of lines: each paragraph's words joined and broken again so no line runs past the
/// width where a break can keep it inside, paragraphs parted by blank lines, lines holding only a comment's mark, and a
/// change of mark; a comment's mark (the language's line comment, a block comment's *, a quote's &gt;) begins every line
/// it wraps to, and a list item's - begins only its first, the lines after it lining up with its text.</summary>
public static class VimWrapping
{
    /// <summary>The marks a line may begin with where the language names no line comment: Vim's own.</summary>
    private static readonly string[] PlainMarks = ["//", "#", "%", ">"];

    /// <summary>The width gq wraps at where no width is set: Vim's own.</summary>
    public const int DefaultWidth = 79;

    /// <summary>A word wrapping moved: where it stood and where it went, its length, and the blanks after it on its line;
    /// for a word beginning a line joined to the one before, that line's lead before it (-1 for any other word), whether
    /// the lead holds a comment's mark, and the blanks the join put before the word.</summary>
    public readonly record struct Moved(int From, int To, int Length, int Trail, int Lead, bool Marked, int Joined);

    /// <summary>What wrapping lines made: the edits, in order, every paragraph wrapped (as an edit, though it made the text it
    /// held), and where each word of theirs went, for gw to put the caret back in the text.</summary>
    public sealed record Wrapped(IReadOnlyList<TextEdit> Edits, IReadOnlyList<TextEdit> Paragraphs, IReadOnlyList<Moved> Words)
    {
        /// <summary>An offset of the text before carried into the text after (<paramref name="after"/>), in any paragraph
        /// wrapped, though it made the text it held: inside a word, the same place in it; in a paragraph's indent, as far from
        /// its line's start, up to the indent made again; between the indent and the first word, as far before it; on the
        /// blanks after a word, the same blank; on a joined line's lead, its next word where the lead holds a mark, else as far
        /// back from the blanks the join put as the lead runs on; from blanks a line break took the place of, the line's end.</summary>
        public int Map(int offset, TextSnapshot after)
        {
            foreach (TextEdit edit in Paragraphs)
            {
                if (offset < edit.Start || offset > edit.End)
                {
                    continue;
                }
                Moved? before = null;
                foreach (Moved word in Words)
                {
                    if (word.From < edit.Start || word.From > edit.End)
                    {
                        continue;
                    }
                    if (offset < word.From + word.Length)
                    {
                        return offset >= word.From ? word.To + (offset - word.From)
                            : before is not { } last ? Indented(word, edit.Start)
                            : word.Lead >= 0 && offset >= word.From - word.Lead ? InLead(last, word)
                            : Blank(last);
                    }
                    before = word;
                }
                if (before is { } end)
                {
                    return Blank(end);
                }
            }
            return TextEditing.MapOffset(offset, Edits, false);

            int Indented(Moved first, int lineStart)
            {
                // What stands between the indent and the first word (a mark, the blanks after it) is kept as it was.
                LineSpan line = after.GetLine(after.GetLineFromOffset(first.To));
                int indentEnd = TextNavigation.IndentEnd(after, line);
                int kept = first.To - indentEnd;
                return offset < first.From - kept ? Math.Min(line.Start + (offset - lineStart), indentEnd) : first.To - (first.From - offset);
            }

            int Blank(Moved word)
            {
                int end = word.To + word.Length;
                int at = end + Math.Min(offset - (word.From + word.Length), word.Trail);
                return Broken(end, at + 1) ? end : at;
            }

            int InLead(Moved last, Moved word)
            {
                if (word.Marked)
                {
                    return word.To;
                }
                int join = last.To + last.Length + last.Trail;
                int at = join + Math.Max(0, word.Joined + offset - word.From);
                return !Broken(last.To + last.Length, word.To) ? at : at < join + word.Joined ? last.To + last.Length : word.To;
            }

            bool Broken(int from, int to)
            {
                for (int p = from; p < to && p < after.Length; p++)
                {
                    if (after[p] is '\r' or '\n')
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }

    /// <summary>The lines of each range, first to last, wrapped at <paramref name="width"/> columns, lines joining as J
    /// joins them (two spaces after a sentence's end with <paramref name="sentenceSpaces"/>, joinspaces); the ranges in
    /// order, none overlapping.</summary>
    public static Wrapped Wrap(TextSnapshot text, IReadOnlyList<(int First, int Last)> ranges, int width, IndentOptions indent, string lineEnding,
        string? lineComment, bool sentenceSpaces)
    {
        List<TextEdit> edits = [];
        List<TextEdit> paragraphs = [];
        List<Moved> words = [];
        int shift = 0;
        foreach ((int first, int last) in ranges)
        {
            Wrap(text, first, last, width, indent, lineEnding, lineComment, sentenceSpaces, edits, paragraphs, words, ref shift);
        }
        return new Wrapped(edits, paragraphs, words);
    }

    private static void Wrap(TextSnapshot text, int first, int last, int width, IndentOptions indent, string lineEnding, string? lineComment, bool sentenceSpaces,
        List<TextEdit> edits, List<TextEdit> paragraphs, List<Moved> words, ref int shift)
    {
        int line = first;
        while (line <= last)
        {
            Line head = Read(text, line, lineComment);
            if (head.Separates)
            {
                line++;
                continue;
            }
            int end = line;
            while (end + 1 <= last && Read(text, end + 1, lineComment) is { Separates: false, Bullet: false } next && next.Mark == head.Mark)
            {
                end++;
            }
            int start = text.GetLineStart(line);
            int stop = text.GetLine(end).End;
            string was = text.GetText(start, stop - start);
            (string made, List<Moved> placed) = Paragraph(text, line, end, head, width, indent, lineEnding, lineComment, sentenceSpaces);
            paragraphs.Add(new TextEdit(start, stop - start, made));
            if (made != was)
            {
                edits.Add(paragraphs[^1]);
            }
            foreach (Moved word in placed)
            {
                words.Add(word with { From = start + word.From, To = start + shift + word.To });
            }
            shift += made.Length - was.Length;
            line = end + 1;
        }
    }

    /// <summary>How a line begins: its indent, then a mark and the blanks after it, where its words start, whether it
    /// parts paragraphs (blank, or a mark alone) and whether its mark is a list item's, which begins a paragraph.</summary>
    private readonly record struct Line(int Start, int Indent, string Mark, int Words, int End, bool Separates, bool Bullet);

    private static Line Read(TextSnapshot text, int number, string? lineComment)
    {
        LineSpan span = text.GetLine(number);
        int indent = TextNavigation.IndentEnd(text, span);
        string rest = text.GetText(indent, Math.Min(span.End - indent, 8));
        string mark = MarkOf(rest, lineComment);
        int words = indent + mark.Length;
        while (words < span.End && text[words] is ' ' or '\t')
        {
            words++;
        }
        bool bullet = mark == "-";
        bool separates = words >= span.End || rest.StartsWith("/*", StringComparison.Ordinal) || rest.StartsWith("*/", StringComparison.Ordinal);
        return new Line(span.Start, indent, bullet ? string.Empty : mark, words, span.End, separates, bullet);
    }

    /// <summary>The comment's or list's mark a line's text begins with after its indent, empty with none: the language's
    /// line comment (doubled as /// is), else Vim's own marks; a block comment's * and a list's - only with a blank after.</summary>
    private static string MarkOf(string rest, string? lineComment)
    {
        foreach (string mark in lineComment is { Length: > 0 } own ? [own] : PlainMarks)
        {
            // Vim's own # marks a comment only with a blank after it, so #include is no comment.
            if (rest.StartsWith(mark, StringComparison.Ordinal) && (lineComment is { Length: > 0 } || mark != "#" || rest.Length == 1 || rest[1] is ' ' or '\t'))
            {
                int end = mark.Length;
                while (end < rest.Length && rest[end] == mark[^1] && mark[^1] != '>')
                {
                    end++;
                }
                return rest[..end];
            }
        }
        if (rest.Length >= 2 && rest[0] is '*' or '-' && rest[1] is ' ' or '\t' && !rest.StartsWith("*/", StringComparison.Ordinal))
        {
            return rest[..1];
        }
        return rest == "*" ? rest : string.Empty;
    }

    /// <summary>One paragraph's lines as wrapped text, and where each word went in it from where it stood (both from the
    /// paragraph's start). Each line's indent is made again from its columns, as Vim's autoindent makes it: spaces under
    /// expandtab, else tabs as far as they go.</summary>
    private static (string Text, List<Moved> Words) Paragraph(TextSnapshot text, int first, int last, Line head, int width, IndentOptions options,
        string lineEnding, string? lineComment, bool sentenceSpaces)
    {
        int tabWidth = options.TabWidth;
        int paragraphStart = head.Start;
        int columns = TextNavigation.ColumnAfter(text.GetText(head.Start, head.Indent - head.Start), 0, tabWidth);
        string indent = options.Whitespace(columns);
        string firstLead = indent + text.GetText(head.Indent, head.Words - head.Indent);
        string afterMark = text.GetText(head.Indent + (head.Bullet ? 1 : head.Mark.Length), head.Words - head.Indent - (head.Bullet ? 1 : head.Mark.Length));
        string nextLead = head.Bullet ? options.Whitespace(TextNavigation.ColumnAfter(afterMark, columns + 1, tabWidth)) : indent + head.Mark + afterMark;
        List<(string Word, string Before, int From, bool Glued, int Trail, int Lead)> pieces = [];
        for (int number = first; number <= last; number++)
        {
            Line line = number == first ? head : Read(text, number, lineComment);
            string body = text.GetText(line.Words, line.End - line.Words);
            int at = 0;
            bool joined = number > first;
            while (at < body.Length)
            {
                int white = at;
                while (at < body.Length && body[at] is ' ' or '\t')
                {
                    at++;
                }
                int wordStart = at;
                while (at < body.Length && body[at] is not (' ' or '\t'))
                {
                    at++;
                }
                string before = body[white..wordStart];
                bool glued = false;
                if (wordStart == at)
                {
                    // Blanks a line ends with stay after its last word.
                    if (pieces.Count > 0)
                    {
                        pieces[^1] = pieces[^1] with { Word = pieces[^1].Word + before };
                    }
                    break;
                }
                if (joined)
                {
                    // The last word holds the blanks its line ended with: as much of the line's end as the join reads.
                    before = Vim.JoinSpace(pieces.Count > 0 ? pieces[^1].Word : string.Empty, body.AsSpan(wordStart), pieces.Count > 0, sentenceSpaces);
                    // Joined with no blank between, the two are one word, as J leaves them.
                    glued = before.Length == 0 && pieces.Count > 0 && pieces[^1].Word[^1] is not (' ' or '\t');
                    joined = false;
                }
                int trail = at;
                while (trail < body.Length && body[trail] is ' ' or '\t')
                {
                    trail++;
                }
                int lead = number > first && white == 0 ? line.Words - line.Start + wordStart : -1;
                pieces.Add((body[wordStart..at], before, line.Words - paragraphStart + wordStart, glued, trail - at, lead));
            }
        }
        StringBuilder made = new(firstLead);
        int column = TextNavigation.ColumnAfter(firstLead, 0, tabWidth);
        bool lineHasWord = false;
        List<Moved> placed = [];
        for (int i = 0; i < pieces.Count; i++)
        {
            (string word, string before, int from, bool glued, int trail, int lead) = pieces[i];
            string bare = word.TrimEnd(' ', '\t');
            if (lineHasWord && !glued && TextNavigation.ColumnAfter(before, column, tabWidth) + WidthFrom(pieces, i, tabWidth) > width)
            {
                // A line broken loses the blanks it ended with.
                int kept = made.Length;
                while (kept > 0 && made[kept - 1] is ' ' or '\t')
                {
                    kept--;
                }
                made.Length = kept;
                made.Append(lineEnding).Append(nextLead);
                column = TextNavigation.ColumnAfter(nextLead, 0, tabWidth);
            }
            else if (lineHasWord)
            {
                made.Append(before);
                column = TextNavigation.ColumnAfter(before, column, tabWidth);
            }
            placed.Add(new Moved(from, made.Length, bare.Length, trail, lead, head.Mark.Length > 0, lead >= 0 ? before.Length : 0));
            made.Append(word);
            column = TextNavigation.ColumnAfter(word, column, tabWidth);
            lineHasWord = true;
        }
        return (made.ToString(), placed);
    }

    /// <summary>The columns a word takes with those glued to it, without the blanks the last ends with.</summary>
    private static int WidthFrom(List<(string Word, string Before, int From, bool Glued, int Trail, int Lead)> pieces, int first, int tabWidth)
    {
        int width = TextNavigation.ColumnAfter(pieces[first].Word.AsSpan().TrimEnd(" \t"), 0, tabWidth);
        for (int i = first + 1; i < pieces.Count && pieces[i].Glued; i++)
        {
            width = TextNavigation.ColumnAfter(pieces[i].Word.AsSpan().TrimEnd(" \t"), width, tabWidth);
        }
        return width;
    }
}
