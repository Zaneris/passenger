using System.Collections;
using Passenger.Core;

namespace Passenger.Text;

/// <summary>A document change's edits, sorted and apart, with the shift each leaves before it worked out once: every
/// offset a key carries through them (carets, folds, markers, squiggles) finds its place by halving, so a key with a
/// thousand carets costs what the carets do, not that times what moves with them.</summary>
internal sealed class SortedEdits : IReadOnlyList<TextEdit>
{
    private readonly TextEdit[] edits;

    /// <summary>What the edits before each one add to an offset after them, the last entry all of theirs; made by the
    /// first offset carried, the same by whichever thread makes it.</summary>
    private int[]? shifts;

    private SortedEdits(TextEdit[] edits) => this.edits = edits;

    /// <summary>The edits of a change, already sorted and apart, as the document's change gives them.</summary>
    public static SortedEdits Of(IReadOnlyList<TextEdit> edits) => edits as SortedEdits ?? new SortedEdits(edits as TextEdit[] ?? [.. edits]);

    public int Count => edits.Length;

    public TextEdit this[int index] => edits[index];

    public IEnumerator<TextEdit> GetEnumerator() => ((IEnumerable<TextEdit>)edits).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>An offset carried through the edits: inside a replaced range to its start, or its end when
    /// <paramref name="afterInsertions"/>, which also puts text inserted at it before it. The edits wholly before it
    /// are a run from the first, found by halving their ends (which never go back): one ending at the offset is before it
    /// when it replaced text, an insertion there only <paramref name="afterInsertions"/>, so it counts after a replacement
    /// ending there.</summary>
    public int Map(int offset, bool afterInsertions)
    {
        int[] before = shifts ??= Shifts(edits);
        int low = Sorted.FirstAfter(edits, static e => (e.End, e.Length == 0 ? 1 : 0), (offset, afterInsertions ? 1 : 0));
        int shift = before[low];
        if (low < edits.Length)
        {
            TextEdit inside = edits[low];
            if (inside.Start < offset && offset < inside.Start + inside.Length)
            {
                return inside.Start + shift + (afterInsertions ? inside.Text.Length : 0);
            }
        }
        return offset + shift;
    }

    private static int[] Shifts(TextEdit[] edits)
    {
        int[] shifts = new int[edits.Length + 1];
        for (int i = 0; i < edits.Length; i++)
        {
            shifts[i + 1] = shifts[i] + edits[i].Text.Length - edits[i].Length;
        }
        return shifts;
    }
}
