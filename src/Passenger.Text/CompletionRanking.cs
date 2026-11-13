namespace Passenger.Text;

/// <summary>A completion list's items ranked for the name typed: the best first by <see cref="FuzzyMatch.Label"/>'s score
/// (two scores alike when equal once divided by how alike they must be), then the one preselected, then by sort text, then
/// in the list's order. Each item's letters are read once, as the list is ranked first, so an item lacking a letter typed
/// is passed over with one AND; and a key that lengthens what was typed scores only the items the last one matched, since
/// no other can match it (<see cref="Narrows"/>). Safe to ask from any thread, one at a time.</summary>
public sealed class CompletionRanking
{
    /// <summary>An item as it is ranked: the text it is matched by (its filter text, else its label), whether that is
    /// matched by all that was typed (it has filter text of its own) or by the name alone, whether the list preselects it,
    /// and the text it sorts by (its sort text, else its label).</summary>
    public readonly record struct Entry(string Matched, bool ByTyped, bool Preselected, string Sort);

    private readonly Entry[] entries;
    private readonly ulong[] letters;
    private readonly Lock gate = new();
    private readonly PriorityQueue<int, (int Score, int Index)> best;

    /// <summary>The items the last query matched, in the list's order, and the room the next query's are written to.</summary>
    private int[] matched;
    private int[] room;
    private int matchedCount;
    private string? lastTyped;
    private string? lastBare;

    public CompletionRanking(IReadOnlyList<Entry> entries)
    {
        this.entries = [.. entries];
        letters = new ulong[this.entries.Length];
        for (int i = 0; i < this.entries.Length; i++)
        {
            letters[i] = FuzzyMatch.Letters(this.entries[i].Matched);
        }
        matched = new int[this.entries.Length];
        room = new int[this.entries.Length];
        best = new PriorityQueue<int, (int Score, int Index)>(new WorstFirst(this.entries));
    }

    /// <summary>How many items have been scored, an item passed over by its letters not counted.</summary>
    internal long Scored { get; private set; }

    /// <summary>The indices of the best <paramref name="most"/> items matching what was typed, the best first. An item with
    /// filter text of its own is matched by <paramref name="typed"/>, any other by <paramref name="bare"/> (what was typed
    /// less what came before the name); <paramref name="alike"/> is how alike two scores must be to rank as one.</summary>
    public int[] Shown(string typed, string bare, int alike, int most)
    {
        lock (gate)
        {
            bool narrowed = lastTyped is not null && Narrows(lastTyped, typed) && Narrows(lastBare!, bare);
            int count = narrowed ? matchedCount : entries.Length;
            ulong wantedTyped = FuzzyMatch.Wanted(typed);
            ulong wantedBare = FuzzyMatch.Wanted(bare);
            WorstFirst order = (WorstFirst)best.Comparer;
            best.Clear();
            int found = 0;
            for (int k = 0; k < count; k++)
            {
                int i = narrowed ? matched[k] : k;
                Entry entry = entries[i];
                if (((entry.ByTyped ? wantedTyped : wantedBare) & ~letters[i]) != 0)
                {
                    continue;
                }
                Scored++;
                if (FuzzyMatch.Label(entry.ByTyped ? typed : bare, entry.Matched) is not { } score)
                {
                    continue;
                }
                room[found++] = i;
                (int, int) ranked = (score / alike, i);
                if (best.Count < most)
                {
                    best.Enqueue(i, ranked);
                }
                else if (best.TryPeek(out _, out (int Score, int Index) worst) && order.Compare(ranked, worst) > 0)
                {
                    best.DequeueEnqueue(i, ranked);
                }
            }
            (matched, room) = (room, matched);
            matchedCount = found;
            lastTyped = typed;
            lastBare = bare;
            int[] shown = new int[best.Count];
            for (int i = shown.Length - 1; i >= 0; i--)
            {
                shown[i] = best.Dequeue();
            }
            return shown;
        }
    }

    /// <summary>Whether every item <paramref name="next"/> matches was matched by <paramref name="before"/>: the same query,
    /// or one it lengthens past a single letter (a single letter matches only a name's start, two may match anywhere), each
    /// way of matching then finding the shorter's letters where it found the longer's. Not where a query holds half of a
    /// surrogate pair, which ignoring case compares whole.</summary>
    internal static bool Narrows(string before, string next)
    {
        if (next == before)
        {
            return true;
        }
        if (before.Length == 1 || !next.StartsWith(before, StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char c in next)
        {
            if (char.IsSurrogate(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Matches in the order they go from the list's best: a lower score, then not preselected, then later by sort
    /// text, then later in the list.</summary>
    private sealed class WorstFirst(Entry[] entries) : IComparer<(int Score, int Index)>
    {
        public int Compare((int Score, int Index) x, (int Score, int Index) y)
        {
            int byScore = x.Score.CompareTo(y.Score);
            if (byScore != 0)
            {
                return byScore;
            }
            Entry a = entries[x.Index];
            Entry b = entries[y.Index];
            int byPreselect = a.Preselected.CompareTo(b.Preselected);
            if (byPreselect != 0)
            {
                return byPreselect;
            }
            int bySort = string.CompareOrdinal(b.Sort, a.Sort);
            return bySort != 0 ? bySort : y.Index.CompareTo(x.Index);
        }
    }
}
