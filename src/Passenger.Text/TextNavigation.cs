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
            while (i < end && char.IsWhiteSpace(content[i]))
            {
                i++;
            }
            if (i < end)
            {
                CharClass kind = Classify(content[i]);
                while (i < end && Classify(content[i]) == kind)
                {
                    i++;
                }
            }
        }
        else
        {
            CharClass kind = Classify(content[i]);
            if (kind != CharClass.Space)
            {
                while (i < end && Classify(content[i]) == kind)
                {
                    i++;
                }
            }
            while (i < end && char.IsWhiteSpace(content[i]))
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
        while (i > line.Start && char.IsWhiteSpace(content[i - 1]))
        {
            i--;
        }
        if (i > line.Start)
        {
            CharClass kind = Classify(content[i - 1]);
            while (i > line.Start && Classify(content[i - 1]) == kind)
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
        bool leftIsWord = i > line.Start && Classify(content[i - 1]) == CharClass.Word;
        if (i == line.End || (leftIsWord && Classify(content[i]) != CharClass.Word))
        {
            i--;
        }
        CharClass kind = Classify(content[i]);
        int start = i;
        int end = i + 1;
        while (start > line.Start && Classify(content[start - 1]) == kind)
        {
            start--;
        }
        while (end < line.End && Classify(content[end]) == kind)
        {
            end++;
        }
        return TextRange.FromBounds(start, end);
    }

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

    /// <summary>The column a character lands in on screen, tabs expanded to the next stop.</summary>
    public static int VisualColumn(string line, int column, int tabWidth)
    {
        int visual = 0;
        for (int i = 0; i < column && i < line.Length; i++)
        {
            visual = line[i] == '\t' ? (visual / tabWidth + 1) * tabWidth : visual + 1;
        }
        return visual;
    }

    public static bool IsWordChar(char c) => Classify(c) == CharClass.Word;

    private enum CharClass
    {
        Space,
        Word,
        Punctuation,
    }

    private static CharClass Classify(char c) =>
        char.IsWhiteSpace(c) ? CharClass.Space
        : char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c) ? CharClass.Word
        : CharClass.Punctuation;

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
