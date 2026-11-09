using System.Collections;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A document's fold regions in order of where they start, kept in runs that the lists carried through later edits
/// share: an edit moves region by region only the runs it reaches, and a run wholly after it by one shift, so a key moves
/// the regions near it rather than every region of the file. Never changed once made; the array it is made from is shared,
/// never copied, as the regions handed about are replaced, never changed in place.</summary>
public sealed class FoldList : IReadOnlyList<FoldRegion>
{
    private const int RunLength = 64;

    public static readonly FoldList Empty = new([], 0);

    /// <summary>Every run <see cref="RunLength"/> regions long but the last, so a region's run is its index's quotient.</summary>
    private readonly Run[] runs;

    private FoldList(Run[] runs, int count)
    {
        this.runs = runs;
        Count = count;
    }

    public int Count { get; }

    public FoldRegion this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return runs[index / RunLength].At(index % RunLength);
        }
    }

    /// <summary>The regions as a list of runs, in order of where they start: the list itself when it is one.</summary>
    public static FoldList Of(IReadOnlyList<FoldRegion> regions)
    {
        if (regions is FoldList list)
        {
            return list;
        }
        if (regions.Count == 0)
        {
            return Empty;
        }
        FoldRegion[] all = regions as FoldRegion[] ?? [.. regions];
        if (!InOrder(all))
        {
            all = [.. all.OrderBy(r => r.Start)];
        }
        Run[] made = new Run[((all.Length - 1) / RunLength) + 1];
        for (int k = 0; k < made.Length; k++)
        {
            int from = k * RunLength;
            made[k] = Run.Of(new ArraySegment<FoldRegion>(all, from, Math.Min(RunLength, all.Length - from)));
        }
        return new FoldList(made, all.Length);
    }

    /// <summary>The regions carried through edits (sorted and apart, in this list's offsets), each offset as one after text
    /// inserted at it is carried: the runs the edits reach moved region by region, those wholly before them kept and those
    /// wholly after them shifted whole.</summary>
    public FoldList Through(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0 || Count == 0)
        {
            return this;
        }
        int from = edits[0].Start;
        int to = edits[^1].Start + edits[^1].Length;
        int shift = 0;
        foreach (TextEdit edit in edits)
        {
            shift += edit.Text.Length - edit.Length;
        }
        Run[] moved = new Run[runs.Length];
        for (int k = 0; k < runs.Length; k++)
        {
            Run run = runs[k];
            moved[k] = run.Highest + run.Shift < from ? run
                : run.Lowest + run.Shift >= to ? run with { Shift = run.Shift + shift }
                : run.Through(edits);
        }
        return new FoldList(moved, Count);
    }

    /// <summary>These regions with those starting at one of <paramref name="starts"/> (in order) folded and the rest open:
    /// only the runs where that changes a region are made again, the rest shared.</summary>
    public FoldList FoldedAt(IReadOnlyList<int> starts)
    {
        Run[]? made = null;
        int next = 0;
        for (int k = 0; k < runs.Length; k++)
        {
            Run run = runs[k];
            int first = run.At(0).Start;
            while (next < starts.Count && starts[next] < first)
            {
                next++;
            }
            if (!run.AnyFolded && (next == starts.Count || starts[next] > run.At(run.Regions.Count - 1).Start))
            {
                continue;
            }
            FoldRegion[] regions = new FoldRegion[run.Regions.Count];
            bool changed = false;
            int at = next;
            for (int i = 0; i < regions.Length; i++)
            {
                FoldRegion region = run.At(i);
                while (at < starts.Count && starts[at] < region.Start)
                {
                    at++;
                }
                bool folded = at < starts.Count && starts[at] == region.Start;
                changed |= folded != region.Folded;
                regions[i] = region with { Folded = folded };
            }
            if (changed)
            {
                made ??= (Run[])runs.Clone();
                made[k] = Run.Of(regions);
            }
        }
        return made is null ? this : new FoldList(made, Count);
    }

    /// <summary>The first folded region at or after an index, or <see cref="Count"/>: runs with none folded are passed over
    /// whole.</summary>
    public int NextFolded(int index)
    {
        for (int k = index / RunLength; k < runs.Length; k++)
        {
            Run run = runs[k];
            if (!run.AnyFolded)
            {
                continue;
            }
            for (int i = k == index / RunLength ? index % RunLength : 0; i < run.Regions.Count; i++)
            {
                if (run.Regions[i].Folded)
                {
                    return (k * RunLength) + i;
                }
            }
        }
        return Count;
    }

    public IEnumerator<FoldRegion> GetEnumerator()
    {
        foreach (Run run in runs)
        {
            for (int i = 0; i < run.Regions.Count; i++)
            {
                yield return run.At(i);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool InOrder(FoldRegion[] regions)
    {
        for (int i = 1; i < regions.Length; i++)
        {
            if (regions[i].Start < regions[i - 1].Start)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Regions as they stood when the run was made, and what edits wholly before them have added to their offsets
    /// since; the lowest and highest offset among them as made, and whether any is folded.</summary>
    private readonly record struct Run(ArraySegment<FoldRegion> Regions, int Shift, int Lowest, int Highest, bool AnyFolded)
    {
        public static Run Of(ArraySegment<FoldRegion> regions)
        {
            int lowest = int.MaxValue;
            int highest = int.MinValue;
            bool folded = false;
            foreach (FoldRegion region in regions)
            {
                lowest = Math.Min(lowest, Math.Min(region.Start, region.End));
                highest = Math.Max(highest, Math.Max(region.Start, region.End));
                folded |= region.Folded;
            }
            return new Run(regions, 0, lowest, highest, folded);
        }

        public FoldRegion At(int index)
        {
            FoldRegion region = Regions[index];
            return Shift == 0 ? region : region with { Start = region.Start + Shift, End = region.End + Shift };
        }

        /// <summary>The run's regions each carried through edits it reaches.</summary>
        public Run Through(IReadOnlyList<TextEdit> edits)
        {
            FoldRegion[] moved = new FoldRegion[Regions.Count];
            for (int i = 0; i < moved.Length; i++)
            {
                FoldRegion region = At(i);
                moved[i] = region with
                {
                    Start = TextEditing.MapOffset(region.Start, edits, afterInsertions: true),
                    End = TextEditing.MapOffset(region.End, edits, afterInsertions: true),
                };
            }
            return Of(moved);
        }
    }
}
