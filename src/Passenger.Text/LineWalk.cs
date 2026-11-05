using System.Buffers;

namespace Passenger.Text;

/// <summary>A snapshot's lines read forward, a block of its text held at a time: a line a little after the last one
/// asked costs the lines between, one far ahead or behind a walk down the rope.</summary>
internal sealed class LineWalk(TextSnapshot text) : IDisposable
{
    /// <summary>The most lines read forward to reach a line rather than seeking it from the rope's root.</summary>
    private const int Near = 64;

    private readonly char[] block = ArrayPool<char>.Shared.Rent(4096);
    private int blockStart;
    private int blockLength;
    private LineSpan line = new(-1, 0, 0, 0);

    /// <summary>A line, as <see cref="TextSnapshot.GetLine"/> gives it.</summary>
    public LineSpan To(int number)
    {
        if (line.Line < 0 || number < line.Line || number - line.Line > Near)
        {
            line = At(number, text.GetLineStart(number));
        }
        while (line.Line < number)
        {
            line = At(line.Line + 1, line.EndIncludingBreak);
        }
        return line;
    }

    public void Dispose() => ArrayPool<char>.Shared.Return(block);

    /// <summary>The line that starts at an offset, its content and break found by reading forward to its '\n'.</summary>
    private LineSpan At(int number, int start)
    {
        if (number == text.LineCount - 1)
        {
            return new LineSpan(number, start, text.Length - start, 0);
        }
        int feed = Feed(start);
        int end = feed > start && CharAt(feed - 1) == '\r' ? feed - 1 : feed;
        return new LineSpan(number, start, end - start, feed + 1 - end);
    }

    /// <summary>The first '\n' at or after an offset, which every line but the last ends with.</summary>
    private int Feed(int from)
    {
        while (from < text.Length)
        {
            if (from < blockStart || from >= blockStart + blockLength)
            {
                blockStart = from;
                blockLength = Math.Min(block.Length, text.Length - from);
                text.CopyTo(from, block.AsSpan(0, blockLength));
            }
            int found = block.AsSpan(from - blockStart, blockStart + blockLength - from).IndexOf('\n');
            if (found >= 0)
            {
                return from + found;
            }
            from = blockStart + blockLength;
        }
        return text.Length;
    }

    private char CharAt(int offset) => offset >= blockStart && offset < blockStart + blockLength ? block[offset - blockStart] : text[offset];
}
