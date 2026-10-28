namespace Passenger.Text;

/// <summary>One step of a document's undo history, kept as what it changed alone: its edits, in the text before it, and
/// the text each took out, from which an undo or a redo makes the text it leaves. A key that continues the step is composed
/// into its edits (<see cref="Continue"/>), so the step holds what its keys changed, not a version of the text for each,
/// and undoes as one change. Where a key changes only text the step put in, that edit's text changes; what a key takes out
/// of the text before the step, or puts beside an edit that replaced some, stays an edit of its own, so an undo or a redo
/// carries the anchors there as the keys one by one did.</summary>
internal sealed class UndoEntry
{
    /// <summary>What an entry holds besides its edits' text and its carets: itself, its two arrays and its two sets.</summary>
    private const int Overhead = 96 + 2 * 24 + 2 * 32;

    public UndoEntry(long id, EditKind kind, SelectionSet selectionsBefore, SelectionSet selectionsAfter, long time, TextEdit[] edits, string[] removed)
    {
        Id = id;
        Kind = kind;
        SelectionsBefore = selectionsBefore;
        SelectionsAfter = selectionsAfter;
        Time = time;
        Edits = edits;
        Removed = removed;
        EndsGap = EndsInSpace(edits);
        Bytes = Count();
    }

    /// <summary>Which place in the history this step leaves the text at, never given to another.</summary>
    public long Id { get; }
    public EditKind Kind { get; }
    public SelectionSet SelectionsBefore { get; }
    public SelectionSet SelectionsAfter { get; private set; }
    public long Time { get; private set; }

    /// <summary>Sorted and apart, in the text before the step; two at one place only as an insertion and then the edit
    /// that starts there.</summary>
    public TextEdit[] Edits { get; private set; }

    /// <summary>The text each edit took out of the text before the step.</summary>
    public string[] Removed { get; private set; }

    /// <summary>Whether the last key's text ended in white space, after which white space typed still joins the step.</summary>
    public bool EndsGap { get; private set; }

    /// <summary>About what the step keeps, in bytes: its strings, arrays and caret sets.</summary>
    public long Bytes { get; private set; }

    /// <summary>What <see cref="TextDocument.Perform"/> was given to run after the step is undone, and after it is redone.</summary>
    public Action? Undone { get; init; }
    public Action? Redone { get; init; }

    /// <summary>The edits that take the text after the step back to the text before it, in the text after it.</summary>
    public TextEdit[] Inverse()
    {
        TextEdit[] inverse = new TextEdit[Edits.Length];
        int shift = 0;
        for (int i = 0; i < Edits.Length; i++)
        {
            TextEdit edit = Edits[i];
            inverse[i] = new TextEdit(edit.Start + shift, edit.Text.Length, Removed[i]);
            shift += edit.Text.Length - edit.Length;
        }
        return inverse;
    }

    /// <summary>Takes a key's edits, in the text the step left, and the text they took out, into the step.</summary>
    public void Continue(TextEdit[] edits, string[] removed, SelectionSet selectionsAfter, long time)
    {
        (Edits, Removed) = Compose(Edits, Removed, edits, removed);
        SelectionsAfter = selectionsAfter;
        Time = time;
        EndsGap = EndsInSpace(edits);
        Bytes = Count();
    }

    private static bool EndsInSpace(TextEdit[] edits) =>
        edits.Length > 0 && edits[0].Text.Length > 0 && char.IsWhiteSpace(edits[0].Text[^1]);

    private long Count()
    {
        long bytes = Overhead + 16L * SelectionsBefore.Count;
        if (!ReferenceEquals(SelectionsAfter, SelectionsBefore))
        {
            bytes += 32 + 16L * SelectionsAfter.Count;
        }
        for (int i = 0; i < Edits.Length; i++)
        {
            bytes += 24 + StringBytes(Edits[i].Text) + StringBytes(Removed[i]);
        }
        return bytes;
    }

    private static long StringBytes(string text) => text.Length == 0 ? 0 : (22 + 2L * text.Length + 7) & ~7L;

    /// <summary>A step's edits (<paramref name="edits"/>, taking out <paramref name="removed"/>) followed by a key's
    /// (<paramref name="next"/>, in the text the step left, taking out <paramref name="taken"/>), as one step's. The key's
    /// edits go from the last, so the pieces before each are still as the step left them.</summary>
    private static (TextEdit[], string[]) Compose(TextEdit[] edits, string[] removed, TextEdit[] next, string[] taken)
    {
        List<Piece> pieces = new(edits.Length + next.Length);
        int[] shifts = new int[edits.Length + 1];
        for (int i = 0; i < edits.Length; i++)
        {
            TextEdit edit = edits[i];
            pieces.Add(new Piece(edit.Start, edit.Length, edit.Text, removed[i], edit.Start + shifts[i]));
            shifts[i + 1] = shifts[i] + edit.Text.Length - edit.Length;
        }
        // The pieces below this are the step's own, at their own indexes, so their shifts hold.
        int untouched = pieces.Count;
        int after = pieces.Count;
        for (int j = next.Length - 1; j >= 0; j--)
        {
            TextEdit edit = next[j];
            while (after > 0 && pieces[after - 1].From > edit.End)
            {
                after--;
            }
            int first = after;
            while (first > 0 && pieces[first - 1].To >= edit.Start)
            {
                first--;
            }
            untouched = Math.Min(untouched, first);
            Take(pieces, first, after, edit, taken[j], shifts, untouched);
            // What this edit made lies at or after the first piece it touched, which a lower edit may touch in turn.
            after = Math.Min(pieces.Count, after + 1);
        }

        int kept = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Length > 0 || pieces[i].Text.Length > 0)
            {
                kept++;
            }
        }
        TextEdit[] composed = new TextEdit[kept];
        string[] took = new string[kept];
        int at = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];
            if (piece.Length > 0 || piece.Text.Length > 0)
            {
                composed[at] = new TextEdit(piece.Start, piece.Length, piece.Text);
                took[at] = piece.Removed;
                at++;
            }
        }
        return (composed, took);
    }

    /// <summary>One edit of a key, in the text the step left, taken into the pieces <paramref name="first"/> up to
    /// <paramref name="after"/>, the ones it touches: into a piece's text where it lies within it, else as a piece of its
    /// own, joined with every piece it shares text with (and a deletion it meets, when it is one).</summary>
    private static void Take(List<Piece> pieces, int first, int after, TextEdit edit, string taken, int[] shifts, int untouched)
    {
        int start = edit.Start;
        int end = edit.End;
        for (int k = first; k < after; k++)
        {
            Piece piece = pieces[k];
            bool inserted = piece.Length == 0;
            if (inserted ? piece.From <= start && end <= piece.To : piece.From < start && end < piece.To)
            {
                string text = string.Concat(piece.Text.AsSpan(0, start - piece.From), edit.Text, piece.Text.AsSpan(end - piece.From));
                pieces[k] = piece with { Text = text };
                return;
            }
        }

        bool deletion = edit.Length > 0 && edit.Text.Length == 0;
        int joinFirst = -1;
        int joinLast = -1;
        for (int k = first; k < after; k++)
        {
            Piece piece = pieces[k];
            bool empty = piece.From == piece.To;
            bool joins = empty
                ? (start < piece.From && piece.From < end) || (deletion && (piece.From == start || piece.From == end))
                : piece.From < end && piece.To > start;
            if (joins)
            {
                joinFirst = joinFirst < 0 ? k : joinFirst;
                joinLast = k;
            }
        }

        if (joinFirst < 0)
        {
            int index = first;
            while (index < after && Before(pieces[index], edit))
            {
                index++;
            }
            int from = start - Shift(pieces, index, shifts, untouched);
            pieces.Insert(index, new Piece(from, edit.Length, edit.Text, taken, start));
            return;
        }

        Piece low = pieces[joinFirst];
        Piece high = pieces[joinLast];
        int s0 = start < low.From ? start - Shift(pieces, joinFirst, shifts, untouched) : low.Start;
        int e0 = high.To >= end ? high.Start + high.Length : end - Shift(pieces, joinLast + 1, shifts, untouched);
        ReadOnlySpan<char> head = low.From < start ? low.Text.AsSpan(0, start - low.From) : [];
        ReadOnlySpan<char> tail = high.To > end ? high.Text.AsSpan(end - high.From) : [];
        string joined = string.Concat(head, edit.Text, tail);

        // The text taken out of the text before the step: each piece's own, and what this edit took between them.
        int length = 0;
        int cursor = Math.Min(start, low.From);
        for (int k = joinFirst; k <= joinLast; k++)
        {
            length += Math.Max(0, pieces[k].From - cursor) + pieces[k].Removed.Length;
            cursor = Math.Max(cursor, pieces[k].To);
        }
        length += Math.Max(0, end - cursor);
        string took = string.Create(length, (pieces, joinFirst, joinLast, start, end, taken), static (span, state) =>
        {
            int written = 0;
            int cursor = Math.Min(state.start, state.pieces[state.joinFirst].From);
            for (int k = state.joinFirst; k <= state.joinLast; k++)
            {
                Piece piece = state.pieces[k];
                if (piece.From > cursor)
                {
                    state.taken.AsSpan(cursor - state.start, piece.From - cursor).CopyTo(span[written..]);
                    written += piece.From - cursor;
                }
                piece.Removed.AsSpan().CopyTo(span[written..]);
                written += piece.Removed.Length;
                cursor = Math.Max(cursor, piece.To);
            }
            if (state.end > cursor)
            {
                state.taken.AsSpan(cursor - state.start, state.end - cursor).CopyTo(span[written..]);
            }
        });
        pieces[joinFirst] = new Piece(s0, e0 - s0, joined, took, Math.Min(start, low.From));
        pieces.RemoveRange(joinFirst + 1, joinLast - joinFirst);
    }

    /// <summary>Whether a piece the edit touches comes before it: one ending where it starts, but a deletion there when the
    /// edit only inserts, which goes after the insertion.</summary>
    private static bool Before(Piece piece, TextEdit edit) =>
        piece.To < edit.Start || (piece.To == edit.Start && (piece.From < edit.Start || edit.Length > 0));

    /// <summary>How far the pieces before <paramref name="index"/> move what follows them.</summary>
    private static int Shift(List<Piece> pieces, int index, int[] shifts, int untouched)
    {
        int below = Math.Min(index, untouched);
        int shift = shifts[below];
        for (int k = below; k < index; k++)
        {
            shift += pieces[k].Text.Length - pieces[k].Length;
        }
        return shift;
    }

    /// <summary>An edit of the step: <see cref="Length"/> characters at <see cref="Start"/> in the text before it replaced by
    /// <see cref="Text"/>, which stands at <see cref="From"/> in the text the step leaves.</summary>
    private readonly record struct Piece(int Start, int Length, string Text, string Removed, int From)
    {
        public int To => From + Text.Length;
    }
}
