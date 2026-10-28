using System.Collections;

namespace Passenger.Terminal;

/// <summary>The scrollback: lines in the order they left the screen, at most <c>limit</c> of them, the oldest dropped as
/// a new one comes without moving the rest. A line's number is <see cref="Dropped"/> plus its index, and stays its own
/// as lines drop from the front, so a selection or a read from a line on keeps to its text past the limit.</summary>
internal sealed class LineRing(int limit) : IReadOnlyList<TerminalLine>
{
    private TerminalLine[] items = [];
    private int start;

    public int Count { get; private set; }

    /// <summary>How many lines have left the front, which is the number of the oldest kept.</summary>
    public long Dropped { get; private set; }

    public TerminalLine this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return items[(start + index) % items.Length];
        }
    }

    /// <summary>Adds the newest line. At the limit the oldest leaves, and is given back, since nothing else keeps it; else null.</summary>
    public TerminalLine? Add(TerminalLine line)
    {
        if (Count == limit)
        {
            TerminalLine oldest = items[start];
            items[start] = line;
            start = (start + 1) % items.Length;
            Dropped++;
            return oldest;
        }
        if (Count == items.Length)
        {
            Grow();
        }
        items[(start + Count) % items.Length] = line;
        Count++;
        return null;
    }

    public TerminalLine RemoveLast()
    {
        int at = (start + Count - 1) % items.Length;
        TerminalLine line = items[at];
        items[at] = null!;
        Count--;
        return line;
    }

    /// <summary>Lets every line go, their numbers spent as if they had dropped from the front, so the screen's keep theirs.</summary>
    public void Clear()
    {
        Dropped += Count;
        Empty();
    }

    /// <summary>Takes every line out, oldest first, to be added again: the first one added back takes the number the
    /// oldest had.</summary>
    public List<TerminalLine> TakeAll()
    {
        List<TerminalLine> lines = [.. this];
        Empty();
        return lines;
    }

    public IEnumerator<TerminalLine> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return items[(start + i) % items.Length];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Empty()
    {
        Array.Clear(items);
        start = 0;
        Count = 0;
    }

    /// <summary>Room for twice as many, up to the limit: a terminal that never scrolls far never holds the limit's array.</summary>
    private void Grow()
    {
        TerminalLine[] grown = new TerminalLine[Math.Min(limit, Math.Max(64, items.Length * 2))];
        for (int i = 0; i < Count; i++)
        {
            grown[i] = items[(start + i) % items.Length];
        }
        items = grown;
        start = 0;
    }
}
