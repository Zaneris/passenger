using System.Buffers;
using System.Globalization;

namespace Passenger.Text;

/// <summary>Where a move by word lands: macOS stops at the end of the word ahead, Windows at the
/// start of the next one. Moving back always stops at a word's start.</summary>
public enum WordStop
{
    End,
    Start,
}

/// <summary>Offsets a caret moves to. A move steps over a whole grapheme (an emoji, a letter and its
/// accents) and treats a line break as one position, never landing inside "\r\n". A move reads what it steps over and
/// a little around it, never its whole line, so a key on a line of a million characters costs what it does on a short one.</summary>
public static class TextNavigation
{
    /// <summary>How far back a move looks for a character no grapheme reaches back over, where reading graphemes can
    /// begin; past it (a long run of marks and symbols with no space or Latin letter in it), reading begins that far back.</summary>
    private const int Lookback = 1024;

    /// <summary>How far back a move looks first, which nearly every line's text holds such a character within.</summary>
    private const int Near = 64;

    /// <summary>The most a step forward reads on the stack.</summary>
    private const int Stacked = 512;

    /// <summary>The offset after the grapheme at an offset: printable ASCII that no mark follows is one character, read
    /// alone; anything else is read into a buffer on the stack (a pooled one past it), a little more each time a grapheme
    /// runs to the end of what was read.</summary>
    public static int NextGrapheme(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        if (offset >= line.End)
        {
            return line.Line == text.LineCount - 1 ? line.End : line.EndIncludingBreak;
        }
        int available = line.End - offset;
        if (text[offset] is >= ' ' and <= '~' && (available == 1 || text[offset + 1] < '\u0080'))
        {
            return offset + 1;
        }
        Span<char> stack = stackalloc char[Stacked];
        for (int read = Math.Min(available, 32); ; read = Math.Min(available, read * 4))
        {
            char[]? pooled = read > Stacked ? ArrayPool<char>.Shared.Rent(read) : null;
            Span<char> chars = (pooled ?? stack)[..read];
            text.CopyTo(offset, chars);
            // A grapheme as long as what was read may go on past it.
            int length = StringInfo.GetNextTextElementLength(chars);
            if (pooled is not null)
            {
                ArrayPool<char>.Shared.Return(pooled);
            }
            if (length < read || read == available)
            {
                return offset + length;
            }
        }
    }

    /// <summary>The offset of the grapheme before an offset: the character before it when no grapheme reaches back over
    /// that one, read alone; otherwise what is before it is read onto the stack from the last such character, a short
    /// stretch first and <see cref="Lookback"/> where that holds none.</summary>
    public static int PreviousGrapheme(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        if (offset <= line.Start)
        {
            return line.Line == 0 ? 0 : text.GetLine(line.Line - 1).End;
        }
        int end = Math.Min(offset, line.End);
        if (BeginsGrapheme(text[end - 1]))
        {
            return end - 1;
        }
        Span<char> chars = stackalloc char[Lookback];
        int from = Math.Max(line.Start, end - Near);
        int at = BeginningBefore(text, from, end, chars);
        if (at == 0 && from > line.Start)
        {
            // Nothing in the short stretch begins a grapheme for sure: read as far back as a move looks.
            from = Math.Max(line.Start, end - Lookback);
            at = BeginningBefore(text, from, end, chars);
        }
        ReadOnlySpan<char> content = chars[..(end - from)];
        int previous = at;
        while (at < content.Length)
        {
            previous = at;
            at += StringInfo.GetNextTextElementLength(content[at..]);
        }
        return from + previous;
    }

    /// <summary>Reads the text from <paramref name="from"/> to <paramref name="end"/> into <paramref name="chars"/>, and
    /// finds the last place after its first character that begins a grapheme for sure, or 0 with none.</summary>
    private static int BeginningBefore(TextSnapshot text, int from, int end, Span<char> chars)
    {
        Span<char> content = chars[..(end - from)];
        text.CopyTo(from, content);
        int at = content.Length - 1;
        while (at > 0 && !BeginsGrapheme(content[at]))
        {
            at--;
        }
        return at;
    }

    public static int NextWord(TextSnapshot text, int offset, WordStop stop)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        if (offset >= line.End)
        {
            return line.Line == text.LineCount - 1 ? line.End : line.EndIncludingBreak;
        }
        LineReader content = new(text, line);
        int i = offset;
        int end = line.End;
        if (stop == WordStop.End)
        {
            while (i < end && ClassAt(text, content, i) == CharClass.Space)
            {
                i++;
            }
            if (i < end)
            {
                CharClass kind = ClassAt(text, content, i);
                while (i < end && ClassAt(text, content, i) == kind)
                {
                    i++;
                }
            }
        }
        else
        {
            CharClass kind = ClassAt(text, content, i);
            if (kind != CharClass.Space)
            {
                while (i < end && ClassAt(text, content, i) == kind)
                {
                    i++;
                }
            }
            while (i < end && ClassAt(text, content, i) == CharClass.Space)
            {
                i++;
            }
        }
        return i;
    }

    public static int PreviousWord(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        if (offset <= line.Start)
        {
            return line.Line == 0 ? 0 : text.GetLine(line.Line - 1).End;
        }
        LineReader content = new(text, line);
        int i = Math.Min(offset, line.End);
        while (i > line.Start && ClassAt(text, content, i - 1) == CharClass.Space)
        {
            i--;
        }
        if (i > line.Start)
        {
            CharClass kind = ClassAt(text, content, i - 1);
            while (i > line.Start && ClassAt(text, content, i - 1) == kind)
            {
                i--;
            }
        }
        return i;
    }

    /// <summary>The word, run of punctuation or run of spaces under <paramref name="offset"/>, as a
    /// double-click selects it. At a boundary a word on the left wins over punctuation on the right.</summary>
    public static TextRange WordAt(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        if (line.Length == 0)
        {
            return new TextRange(line.Start, 0);
        }
        LineReader content = new(text, line);
        int i = Math.Min(offset, line.End);
        bool leftIsWord = i > line.Start && ClassAt(text, content, i - 1) == CharClass.Word;
        if (i == line.End || (leftIsWord && ClassAt(text, content, i) != CharClass.Word))
        {
            i--;
        }
        CharClass kind = ClassAt(text, content, i);
        int start = i;
        int end = i + 1;
        while (start > line.Start && ClassAt(text, content, start - 1) == kind)
        {
            start--;
        }
        while (end < line.End && ClassAt(text, content, end) == kind)
        {
            end++;
        }
        return TextRange.FromBounds(start, end);
    }

    /// <summary>Whether a range is one whole word, as a double-click chooses it: no word character carries on from
    /// either end, and nothing but a word's characters lies between. Reads the word at its start, never past it.</summary>
    public static bool IsWholeWord(TextSnapshot text, TextRange range) =>
        range.Length > 0 && range.End <= text.Length && WordCharacters.IsWordAt(text, range.Start) && WordAt(text, range.Start) == range;

    /// <summary>Home: to the line's first non-space character, or to column 0 if the caret is already
    /// there (or the line is only spaces).</summary>
    public static int SmartHome(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        int indent = IndentEnd(text, line);
        return offset == indent || indent == line.End ? line.Start : indent;
    }

    public static int LineEnd(TextSnapshot text, int offset) => text.GetLine(text.GetLineFromOffset(offset)).End;

    /// <summary>Where a line's leading spaces and tabs end, read no further than they go.</summary>
    public static int IndentEnd(TextSnapshot text, LineSpan line)
    {
        LineReader content = new(text, line);
        int i = line.Start;
        while (i < line.End && content[i] is ' ' or '\t')
        {
            i++;
        }
        return i;
    }

    /// <summary>The spaces and tabs the line holding an offset begins with.</summary>
    public static string IndentOf(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        return text.GetText(line.Start, IndentEnd(text, line) - line.Start);
    }

    /// <summary>Where the leading spaces and tabs of a line of a string end, the line starting at
    /// <paramref name="lineStart"/>.</summary>
    public static int IndentEnd(string text, int lineStart)
    {
        int i = lineStart;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }
        return i;
    }

    /// <summary>The spaces and tabs the line of a string holding an offset begins with.</summary>
    public static string IndentOf(string text, int offset)
    {
        int start = LineStart(text, offset);
        return text[start..IndentEnd(text, start)];
    }

    /// <summary>Where the line of a string holding an offset starts, lines broken at each '\n'; an offset past the text's
    /// end is on its last line.</summary>
    public static int LineStart(string text, int offset) => offset <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(offset, text.Length) - 1) + 1;

    /// <summary>Where the line of a string holding an offset ends, before its '\n' and a '\r' just before that, never before
    /// the offset; the text's end on its last line.</summary>
    public static int LineEnd(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int end = text.IndexOf('\n', offset);
        end = end < 0 ? text.Length : end;
        return end > offset && text[end - 1] == '\r' ? end - 1 : end;
    }

    /// <summary>Where the line after the one of a string holding an offset starts, past its '\n'; the text's end on its last line.</summary>
    public static int NextLineStart(string text, int offset) => text.IndexOf('\n', Math.Clamp(offset, 0, text.Length)) is var end and >= 0 ? end + 1 : text.Length;

    /// <summary>Where the run of whitespace ending at <paramref name="offset"/> on a line begins, read no further back
    /// than it goes.</summary>
    public static int WhitespaceBefore(TextSnapshot text, LineSpan line, int offset)
    {
        LineReader content = new(text, line);
        int i = Math.Min(offset, line.End);
        while (i > line.Start && char.IsWhiteSpace(content[i - 1]))
        {
            i--;
        }
        return i;
    }

    /// <summary>How many leading spaces and tabs a line has.</summary>
    public static int IndentLength(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }
        return i;
    }

    /// <summary>The column a piece of text reaches on screen drawn from <paramref name="column"/>: a tab to its next
    /// stop, every other character its <see cref="WideCharacters.Cells"/>.</summary>
    public static int ColumnAfter(ReadOnlySpan<char> piece, int column, int tabWidth)
    {
        for (int i = 0; i < piece.Length; i++)
        {
            char c = piece[i];
            if (c == '\t')
            {
                int stop = Math.Max(1, tabWidth);
                column = (column / stop + 1) * stop;
            }
            else if (c < '\u0300')
            {
                column++;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < piece.Length && char.IsLowSurrogate(piece[i + 1]))
            {
                column += WideCharacters.Cells(char.ConvertToUtf32(c, piece[++i]));
            }
            else
            {
                column += WideCharacters.Cells(c);
            }
        }
        return column;
    }

    /// <summary>The column a stretch of a line reaches on screen drawn from <paramref name="column"/>, read a block at a
    /// time onto the stack and counted as <see cref="ColumnAfter(ReadOnlySpan{char}, int, int)"/> counts.</summary>
    public static int ColumnAfter(TextSnapshot text, int from, int to, int column, int tabWidth)
    {
        Span<char> block = stackalloc char[256];
        for (int at = from; at < to;)
        {
            int length = Math.Min(block.Length, to - at);
            text.CopyTo(at, block[..length]);
            if (length > 1 && at + length < to && char.IsHighSurrogate(block[length - 1]))
            {
                // A pair the block's end cuts is read whole with the next.
                length--;
            }
            column = ColumnAfter(block[..length], column, tabWidth);
            at += length;
        }
        return column;
    }

    /// <summary>The column an offset lands in on screen, counted from its line's start.</summary>
    public static int VisualColumn(TextSnapshot text, int offset, int tabWidth)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        return ColumnAfter(text, line.Start, Math.Min(offset, line.End), 0, tabWidth);
    }

    private enum CharClass
    {
        Space,
        Word,
        Punctuation,
    }

    private static CharClass Classify(char c) =>
        char.IsWhiteSpace(c) ? CharClass.Space : WordCharacters.IsWord(c) ? CharClass.Word : CharClass.Punctuation;

    /// <summary>The class of the character at an offset of a line, a mark or joiner taking the class of the character
    /// it carries on (<see cref="WordCharacters.Base"/>).</summary>
    private static CharClass ClassAt(TextSnapshot text, LineReader content, int offset)
    {
        char c = content[offset];
        return WordCharacters.Extends(c) ? Classify(text[WordCharacters.Base(text, offset)]) : Classify(c);
    }

    /// <summary>Whether a grapheme always begins at this character, whatever comes before it: a Latin or CJK one, which
    /// no mark, joiner or emoji sequence reaches back over (but for the two emoji among them).</summary>
    private static bool BeginsGrapheme(char c) =>
        (c < '̀' && c is not '©' and not '®') || (c >= '一' && c <= '鿿');
}

/// <summary>A line's characters, read a block at a time around where a move looks rather than copied whole.</summary>
internal sealed class LineReader(TextSnapshot text, LineSpan line)
{
    private const int Block = 256;

    private readonly char[] block = new char[Block];
    private int start = -1;
    private int length;

    /// <summary>The character at an offset of the line's content.</summary>
    public char this[int offset]
    {
        get
        {
            if (offset < start || offset >= start + length)
            {
                start = Math.Max(line.Start, offset - (offset % Block));
                length = Math.Min(Block, line.End - start);
                text.CopyTo(start, block.AsSpan(0, length));
            }
            return block[offset - start];
        }
    }
}
