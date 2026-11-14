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

    /// <summary>What wrapping lines made: the edits, in order, and where each word of theirs went, from its old offset to
    /// its new one, for gw to put the caret back in the text.</summary>
    public sealed record Wrapped(IReadOnlyList<TextEdit> Edits, IReadOnlyList<(int From, int To, int Length)> Words)
    {
        /// <summary>An offset of the text before carried into the text after: inside a word, the same place in it; between
        /// words, the start of the next.</summary>
        public int Map(int offset)
        {
            foreach (TextEdit edit in Edits)
            {
                if (offset < edit.Start || offset > edit.End)
                {
                    continue;
                }
                foreach ((int from, int to, int length) in Words)
                {
                    if (from >= edit.Start && from <= edit.End && offset < from + length)
                    {
                        return offset >= from ? to + (offset - from) : to;
                    }
                }
            }
            return TextEditing.MapOffset(offset, Edits, false);
        }
    }

    /// <summary>The lines of each range, first to last, wrapped at <paramref name="width"/> columns; the ranges in order,
    /// none overlapping.</summary>
    public static Wrapped Wrap(TextSnapshot text, IReadOnlyList<(int First, int Last)> ranges, int width, int tabWidth, string lineEnding, string? lineComment)
    {
        List<TextEdit> edits = [];
        List<(int From, int To, int Length)> words = [];
        int shift = 0;
        foreach ((int first, int last) in ranges)
        {
            Wrap(text, first, last, width, tabWidth, lineEnding, lineComment, edits, words, ref shift);
        }
        return new Wrapped(edits, words);
    }

    private static void Wrap(TextSnapshot text, int first, int last, int width, int tabWidth, string lineEnding, string? lineComment,
        List<TextEdit> edits, List<(int From, int To, int Length)> words, ref int shift)
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
            (string made, List<(int From, int To, int Length)> placed) = Paragraph(text, line, end, head, width, tabWidth, lineEnding, lineComment);
            if (made != was)
            {
                edits.Add(new TextEdit(start, stop - start, made));
            }
            foreach ((int from, int to, int length) in placed)
            {
                words.Add((start + from, start + shift + to, length));
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
    /// paragraph's start).</summary>
    private static (string Text, List<(int From, int To, int Length)> Words) Paragraph(TextSnapshot text, int first, int last, Line head, int width, int tabWidth,
        string lineEnding, string? lineComment)
    {
        int paragraphStart = head.Start;
        string firstLead = text.GetText(head.Start, head.Words - head.Start);
        string indent = text.GetText(head.Start, head.Indent - head.Start);
        string afterMark = text.GetText(head.Indent + (head.Bullet ? 1 : head.Mark.Length), head.Words - head.Indent - (head.Bullet ? 1 : head.Mark.Length));
        string nextLead = head.Bullet ? indent + new string(' ', 1 + Columns(afterMark, 0, tabWidth)) : indent + head.Mark + afterMark;
        List<(string Word, string Before, int From)> pieces = [];
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
                    // Lines join as J joins them: one space, none after blanks or before a ')'.
                    bool endsWhite = pieces.Count > 0 && pieces[^1].Word.Length > 0 && pieces[^1].Word[^1] is ' ' or '\t';
                    before = pieces.Count == 0 || endsWhite || body[wordStart] == ')' ? string.Empty : " ";
                    joined = false;
                }
                pieces.Add((body[wordStart..at], before, line.Words - paragraphStart + wordStart));
            }
        }
        StringBuilder made = new(firstLead);
        int column = Columns(firstLead, 0, tabWidth);
        bool lineHasWord = false;
        List<(int From, int To, int Length)> placed = [];
        foreach ((string word, string before, int from) in pieces)
        {
            string bare = word.TrimEnd(' ', '\t');
            if (lineHasWord && column + Columns(before, column, tabWidth) + Columns(bare, 0, tabWidth) > width)
            {
                // A line broken loses the blanks it ended with.
                int kept = made.Length;
                while (kept > 0 && made[kept - 1] is ' ' or '\t')
                {
                    kept--;
                }
                made.Length = kept;
                made.Append(lineEnding).Append(nextLead);
                column = Columns(nextLead, 0, tabWidth);
            }
            else if (lineHasWord)
            {
                made.Append(before);
                column += Columns(before, column, tabWidth);
            }
            placed.Add((from, made.Length, bare.Length));
            made.Append(word);
            column += Columns(word, column, tabWidth);
            lineHasWord = true;
        }
        return (made.ToString(), placed);
    }

    /// <summary>The columns a piece of text takes drawn from <paramref name="column"/>, a tab to its next stop.</summary>
    private static int Columns(string piece, int column, int tabWidth)
    {
        int at = column;
        foreach (char c in piece)
        {
            at = c == '\t' && tabWidth > 0 ? (at / tabWidth + 1) * tabWidth : char.IsLowSurrogate(c) ? at : at + 1;
        }
        return at - column;
    }
}
