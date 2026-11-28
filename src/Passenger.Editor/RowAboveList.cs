using System.Collections;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A document's rows above lines in offset order (rows at one offset as given), kept in runs of rows and chunks of
/// runs that the lists carried through later edits share: an edit moves row by row only the runs it reaches, the rest of
/// their chunk by a run's shift each and every chunk after it by one shift, so a key moves the rows near it rather than
/// every row of the file. The rows before an offset are counted by halving, which is how the editor finds a line's rows.
/// Never changed once made; the arrays it is made from are shared.</summary>
public sealed class RowAboveList : IReadOnlyList<RowAbove>
{
    private const int RunLength = 64;
    private const int ChunkLength = 16;

    public static readonly RowAboveList Empty = new([]);

    private readonly Chunk[] chunks;

    /// <summary>How many rows the chunks before each hold; one more than the chunks, the last all of them.</summary>
    private readonly int[] before;

    private RowAboveList(Chunk[] chunks)
    {
        this.chunks = chunks;
        before = new int[chunks.Length + 1];
        for (int k = 0; k < chunks.Length; k++)
        {
            before[k + 1] = before[k] + chunks[k].Count;
        }
    }

    public int Count => before[^1];

    public RowAbove this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            int k = Sorted.FirstAfter<int>(before, index) - 1;
            return chunks[k].At(index - before[k]);
        }
    }

    /// <summary>The rows as a list in offset order: the list itself when it is one, else sorted (keeping the order of rows at
    /// one offset) only when they are not in order already.</summary>
    public static RowAboveList Of(IReadOnlyList<RowAbove> rows)
    {
        if (rows is RowAboveList list)
        {
            return list;
        }
        if (rows.Count == 0)
        {
            return Empty;
        }
        RowAbove[] all = [.. rows];
        if (!InOrder(all))
        {
            all = [.. all.OrderBy(r => r.Offset)];
        }
        return new RowAboveList(Chunked(Cut(all)));
    }

    /// <summary>How many rows stand before an offset.</summary>
    public int CountBefore(int offset)
    {
        int k = Sorted.FirstAtOrAfter(chunks.AsSpan(), static c => c.Highest + c.Shift, offset);
        return k == chunks.Length ? Count : before[k] + chunks[k].CountBefore(offset - chunks[k].Shift);
    }

    /// <summary>The rows carried through a change: text typed at a row's offset goes before it, so a line broken there takes
    /// the row down with its text; an edit that deletes the character a row stands on together with a line break takes the
    /// row away with its line. The runs the edits reach are moved row by row, the other runs of their chunks by a shift
    /// each, the chunks wholly before them kept and those wholly after them shifted whole.</summary>
    public RowAboveList Through(DocumentChangedEventArgs change)
    {
        IReadOnlyList<TextEdit> edits = change.Edits;
        if (edits.Count == 0 || Count == 0)
        {
            return this;
        }
        int from = edits[0].Start;
        int to = edits[^1].End;
        int shift = 0;
        foreach (TextEdit edit in edits)
        {
            shift += edit.Text.Length - edit.Length;
        }
        int first = -1;
        int last = -1;
        for (int k = 0; k < chunks.Length; k++)
        {
            if (chunks[k].Highest + chunks[k].Shift >= from && chunks[k].Lowest + chunks[k].Shift <= to)
            {
                first = first < 0 ? k : first;
                last = k;
            }
        }
        if (first < 0)
        {
            Chunk[] shifted = new Chunk[chunks.Length];
            for (int k = 0; k < chunks.Length; k++)
            {
                shifted[k] = chunks[k].Highest + chunks[k].Shift < from ? chunks[k] : chunks[k] with { Shift = chunks[k].Shift + shift };
            }
            return new RowAboveList(shifted);
        }
        // The chunks reached as runs, each with its chunk's shift: those the edits reach moved row by row into one array.
        int runCount = 0;
        int rowCount = 0;
        for (int k = first; k <= last; k++)
        {
            foreach (Run run in chunks[k].Runs)
            {
                int shifted = run.Shift + chunks[k].Shift;
                bool touched = run.Highest + shifted >= from && run.Lowest + shifted <= to;
                runCount += touched ? 0 : 1;
                rowCount += touched ? run.Rows.Count : 0;
            }
        }
        RowAbove[] reached = new RowAbove[rowCount];
        int kept = 0;
        Run[] runs = new Run[runCount + ((rowCount - 1) / RunLength) + 1];
        int made = 0;
        int cutAt = -1;
        for (int k = first; k <= last; k++)
        {
            Chunk chunk = chunks[k];
            foreach (Run held in chunk.Runs)
            {
                Run run = held with { Shift = held.Shift + chunk.Shift };
                if (run.Highest + run.Shift < from)
                {
                    runs[made++] = run;
                }
                else if (run.Lowest + run.Shift > to)
                {
                    runs[made++] = run with { Shift = run.Shift + shift };
                }
                else
                {
                    cutAt = cutAt < 0 ? made : cutAt;
                    for (int i = 0; i < run.Rows.Count; i++)
                    {
                        RowAbove row = run.At(i);
                        if (!TakenAway(change, row.Offset))
                        {
                            reached[kept++] = row with { Offset = TextEditing.MapOffset(row.Offset, edits, afterInsertions: true) };
                        }
                    }
                }
            }
        }
        Run[] cut = kept > 0 ? Cut(new ArraySegment<RowAbove>(reached, 0, kept)) : [];
        if (cutAt >= 0)
        {
            // The runs reached lie side by side: those cut from them go where they stood.
            Array.Copy(runs, cutAt, runs, cutAt + cut.Length, made - cutAt);
            cut.CopyTo(runs, cutAt);
            made += cut.Length;
        }
        Chunk[] chunked = made > 0 ? Chunked(new ArraySegment<Run>(runs, 0, made)) : [];
        Chunk[] moved = new Chunk[chunks.Length - (last - first + 1) + chunked.Length];
        chunks.AsSpan(0, first).CopyTo(moved);
        chunked.CopyTo(moved, first);
        for (int k = last + 1; k < chunks.Length; k++)
        {
            moved[first + chunked.Length + k - last - 1] = chunks[k] with { Shift = chunks[k].Shift + shift };
        }
        return new RowAboveList(moved);
    }

    /// <summary>Whether two lists draw alike: the same words at the same offsets.</summary>
    public static bool DrawnAlike(RowAboveList a, RowAboveList b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        using IEnumerator<RowAbove> other = b.GetEnumerator();
        foreach (RowAbove row in a)
        {
            other.MoveNext();
            if (!row.DrawnAlike(other.Current))
            {
                return false;
            }
        }
        return true;
    }

    public IEnumerator<RowAbove> GetEnumerator()
    {
        foreach (Chunk chunk in chunks)
        {
            foreach (Run run in chunk.Runs)
            {
                for (int i = 0; i < run.Rows.Count; i++)
                {
                    RowAbove row = run.At(i);
                    yield return chunk.Shift == 0 ? row : row with { Offset = row.Offset + chunk.Shift };
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Whether a change deletes the character at an offset along with a line break.</summary>
    private static bool TakenAway(DocumentChangedEventArgs change, int offset)
    {
        foreach (TextEdit edit in change.Edits)
        {
            if (edit.Start > offset)
            {
                return false;
            }
            if (offset < edit.End)
            {
                return change.Before.GetLineFromOffset(edit.Start) != change.Before.GetLineFromOffset(edit.End);
            }
        }
        return false;
    }

    private static bool InOrder(RowAbove[] rows)
    {
        for (int i = 1; i < rows.Length; i++)
        {
            if (rows[i].Offset < rows[i - 1].Offset)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Rows cut into runs of <see cref="RunLength"/>, the last shorter, sharing their array.</summary>
    private static Run[] Cut(ArraySegment<RowAbove> rows)
    {
        Run[] made = new Run[((rows.Count - 1) / RunLength) + 1];
        for (int k = 0; k < made.Length; k++)
        {
            int start = k * RunLength;
            made[k] = Run.Of(rows.Slice(start, Math.Min(RunLength, rows.Count - start)));
        }
        return made;
    }

    /// <summary>Runs gathered into chunks of <see cref="ChunkLength"/>, the last shorter, each its own array of them.</summary>
    private static Chunk[] Chunked(ArraySegment<Run> runs)
    {
        Chunk[] made = new Chunk[((runs.Count - 1) / ChunkLength) + 1];
        for (int k = 0; k < made.Length; k++)
        {
            int start = k * ChunkLength;
            made[k] = Chunk.Of([.. runs.Slice(start, Math.Min(ChunkLength, runs.Count - start))]);
        }
        return made;
    }

    /// <summary>Rows as they stood when the run was made, and what edits wholly before them have added to their offsets
    /// since; the lowest and highest offset among them as made.</summary>
    private readonly record struct Run(ArraySegment<RowAbove> Rows, int Shift, int Lowest, int Highest)
    {
        public static Run Of(ArraySegment<RowAbove> rows) => new(rows, 0, rows[0].Offset, rows[^1].Offset);

        public RowAbove At(int index)
        {
            RowAbove row = Rows[index];
            return Shift == 0 ? row : row with { Offset = row.Offset + Shift };
        }
    }

    /// <summary>Runs side by side, the rows the runs before each hold, what edits wholly before them have added to their
    /// offsets since, and the lowest and highest offset among them as made.</summary>
    private readonly record struct Chunk(Run[] Runs, int[] Before, int Shift, int Lowest, int Highest)
    {
        public int Count => Before[^1];

        public static Chunk Of(Run[] runs)
        {
            int[] before = new int[runs.Length + 1];
            for (int k = 0; k < runs.Length; k++)
            {
                before[k + 1] = before[k] + runs[k].Rows.Count;
            }
            return new Chunk(runs, before, 0, runs[0].Lowest + runs[0].Shift, runs[^1].Highest + runs[^1].Shift);
        }

        public RowAbove At(int index)
        {
            int k = Sorted.FirstAfter<int>(Before, index) - 1;
            RowAbove row = Runs[k].At(index - Before[k]);
            return Shift == 0 ? row : row with { Offset = row.Offset + Shift };
        }

        /// <summary>How many of its rows stand before an offset, in its offsets as made.</summary>
        public int CountBefore(int offset)
        {
            int k = Sorted.FirstAtOrAfter(Runs.AsSpan(), static r => r.Highest + r.Shift, offset);
            if (k == Runs.Length)
            {
                return Count;
            }
            Run run = Runs[k];
            return Before[k] + Sorted.FirstAtOrAfter(run.Rows.AsSpan(), static r => r.Offset, offset - run.Shift);
        }
    }
}
