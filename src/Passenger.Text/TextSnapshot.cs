using System.Buffers;
using System.Text;

namespace Passenger.Text;

/// <summary>One version of a document's text, immutable and safe to read from any thread. Lines end
/// at '\n'; a '\r' just before it belongs to the break, never to the line, so a caret can never sit
/// between the two halves of "\r\n".</summary>
public sealed class TextSnapshot
{
    public static readonly TextSnapshot Empty = new(RopeLeaf.Empty);

    private readonly RopeNode root;

    private TextSnapshot(RopeNode root) => this.root = root;

    public static TextSnapshot From(string text) => new(Rope.Build(text));

    public int Length => root.Length;

    public int LineCount => root.Breaks + 1;

    public char this[int offset]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, Length);
            return Rope.CharAt(root, offset);
        }
    }

    public string GetText(int start, int length)
    {
        CheckRange(start, length);
        if (length == 0)
        {
            return string.Empty;
        }
        return string.Create(length, (root, start), static (span, state) => Rope.CopyTo(state.root, state.start, span));
    }

    public string GetText(TextRange range) => GetText(range.Start, range.Length);

    public void CopyTo(int start, Span<char> destination)
    {
        CheckRange(start, destination.Length);
        Rope.CopyTo(root, start, destination);
    }

    /// <summary>The text of a range as the rope holds it, one slice per leaf, with no copy.</summary>
    public void VisitChunks(int start, int length, Action<ReadOnlyMemory<char>> visit)
    {
        CheckRange(start, length);
        Rope.Visit(root, start, length, visit);
    }

    /// <summary>Where <paramref name="needle"/> occurs from <paramref name="start"/> on, in order, matched ordinally; each
    /// after the last one's end, or for <paramref name="overlapping"/> after its start. The text is read a block at a
    /// time, so taking the first few reads little of it.</summary>
    public IEnumerable<int> Occurrences(string needle, int start = 0, bool overlapping = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (needle.Length == 0)
        {
            yield break;
        }
        const int Block = 1 << 16;
        char[] buffer = new char[Block + needle.Length - 1];
        int from = start;
        while (from + needle.Length <= Length)
        {
            int read = Math.Min(buffer.Length, Length - from);
            CopyTo(from, buffer.AsSpan(0, read));
            int searched = 0;
            while (true)
            {
                int found = buffer.AsSpan(searched, read - searched).IndexOf(needle.AsSpan(), StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }
                yield return from + searched + found;
                searched += found + (overlapping ? 1 : needle.Length);
            }
            // Every start up to a needle's length from the block's end has been looked at; the rest begin the next block.
            from += Math.Max(searched, read - needle.Length + 1);
        }
    }

    /// <summary>How many lines this thread has sought from the rope's root, each a walk down it: what a batch of places
    /// costs, counted rather than timed.</summary>
    public static long LinesSought => sought;

    [ThreadStatic]
    private static long sought;

    public int GetLineStart(int line)
    {
        CheckLine(line);
        sought++;
        return Rope.OffsetAfterBreaks(root, line);
    }

    public LineSpan GetLine(int line)
    {
        int start = GetLineStart(line);
        if (line == LineCount - 1)
        {
            return new LineSpan(line, start, Length - start, 0);
        }
        int next = Rope.OffsetAfterBreaks(root, line + 1);
        int contentEnd = next - 1;
        if (contentEnd > start && Rope.CharAt(root, contentEnd - 1) == '\r')
        {
            contentEnd--;
        }
        return new LineSpan(line, start, contentEnd - start, next - contentEnd);
    }

    public string GetLineText(int line)
    {
        LineSpan span = GetLine(line);
        return GetText(span.Start, span.Length);
    }

    public int GetLineFromOffset(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);
        return Rope.BreaksBefore(root, offset);
    }

    /// <summary>The line and column of an offset. An offset inside a "\r\n" reports the end of
    /// the line's content.</summary>
    public TextPosition GetPosition(int offset)
    {
        LineSpan line = GetLine(GetLineFromOffset(offset));
        return new TextPosition(line.Line, Math.Min(offset - line.Start, line.Length));
    }

    /// <summary>The offset of a line and column, the column clamped to the line's content.</summary>
    public int GetOffset(TextPosition position)
    {
        LineSpan line = GetLine(Math.Clamp(position.Line, 0, LineCount - 1));
        return line.Start + Math.Clamp(position.Column, 0, line.Length);
    }

    /// <summary>The offsets of many lines and columns, each as <see cref="GetOffset"/> gives it, in any order: taken in the
    /// order of their lines and the text read forward from one to the next where they lie near, so places on nearby lines
    /// cost a read of the lines between them rather than a walk down the rope each.</summary>
    public void GetOffsets(ReadOnlySpan<TextPosition> positions, Span<int> offsets)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(offsets.Length, positions.Length);
        int count = positions.Length;
        int[] lines = ArrayPool<int>.Shared.Rent(count);
        int[] order = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                lines[i] = Math.Clamp(positions[i].Line, 0, LineCount - 1);
                order[i] = i;
            }
            Array.Sort(lines, order, 0, count);
            using LineWalk walk = new(this);
            for (int k = 0; k < count; k++)
            {
                LineSpan line = walk.To(lines[k]);
                int at = order[k];
                offsets[at] = line.Start + Math.Clamp(positions[at].Column, 0, line.Length);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(lines);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    public TextSnapshot Replace(int start, int length, string text)
    {
        CheckRange(start, length);
        return new TextSnapshot(Rope.Replace(root, start, length, text));
    }

    /// <summary>The snapshot with every edit applied. The edits do not overlap and their ranges are in
    /// this snapshot's coordinates, so they are applied from the last to the first.</summary>
    public TextSnapshot Apply(IReadOnlyList<TextEdit> edits)
    {
        RopeNode node = root;
        for (int i = edits.Count - 1; i >= 0; i--)
        {
            TextEdit edit = edits[i];
            node = Rope.Replace(node, edit.Start, edit.Length, edit.Text);
        }
        return new TextSnapshot(node);
    }

    public override string ToString()
    {
        StringBuilder builder = new(Length);
        Rope.AppendTo(root, builder);
        return builder.ToString();
    }

    private void CheckRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, Length);
    }

    private void CheckLine(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);
    }
}

/// <summary>A line: where it starts, its content's length, and how long its break is (0 on the last
/// line, 1 for "\n", 2 for "\r\n").</summary>
public readonly record struct LineSpan(int Line, int Start, int Length, int BreakLength)
{
    public int End => Start + Length;
    public int EndIncludingBreak => Start + Length + BreakLength;
}

/// <summary>A zero-based line and a column in UTF-16 code units.</summary>
public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
}

public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;

    public static TextRange FromBounds(int start, int end) => new(start, end - start);

    public bool Contains(int offset) => offset >= Start && offset < End;
}

/// <summary>Replace <see cref="Length"/> characters at <see cref="Start"/> with <see cref="Text"/>.</summary>
public readonly record struct TextEdit(int Start, int Length, string Text)
{
    public int End => Start + Length;

    /// <summary>The one edit that makes one text another: what lies between their common start and their common end.</summary>
    public static TextEdit Between(string was, string now)
    {
        int start = 0;
        while (start < was.Length && start < now.Length && was[start] == now[start])
        {
            start++;
        }
        int end = 0;
        while (end < was.Length - start && end < now.Length - start && was[^(end + 1)] == now[^(end + 1)])
        {
            end++;
        }
        return new TextEdit(start, was.Length - start - end, now[start..^end]);
    }
}
