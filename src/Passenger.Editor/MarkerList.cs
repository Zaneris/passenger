using System.Collections;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A document's markers in the order of their starts, kept in runs that the lists carried through later edits
/// share: an edit moves marker by marker only the runs it reaches, and a run wholly after it by one shift, so a key moves
/// the markers near it rather than every marker of the file. Each is carried by its ends: its start by its
/// <see cref="Marker.StartGravity"/>, its end after text typed at it while it holds text, and both before such text once
/// it holds none. Never changed once made; the array it is made from is shared, never copied, as the markers handed about
/// are replaced, never changed in place.</summary>
public sealed class MarkerList : IReadOnlyList<Marker>
{
    private const int RunLength = 64;

    public static readonly MarkerList Empty = new([], 0, default);

    /// <summary>Every run <see cref="RunLength"/> markers long but the last, so a marker's run is its index's quotient.</summary>
    private readonly Run[] runs;

    /// <summary>The furthest any marker of each run or a run before it reaches, so the first marker reaching an offset is
    /// found by halving.</summary>
    private readonly int[] reach;

    private MarkerList(Run[] runs, int count, (int Errors, int Warnings) problems)
    {
        this.runs = runs;
        Count = count;
        Problems = problems;
        reach = new int[runs.Length];
        for (int k = 0; k < runs.Length; k++)
        {
            reach[k] = Math.Max(k > 0 ? reach[k - 1] : int.MinValue, runs[k].Highest + runs[k].Shift);
        }
    }

    public int Count { get; }

    /// <summary>How many of the markers are errors and how many warnings, which carrying them never changes.</summary>
    public (int Errors, int Warnings) Problems { get; }

    public Marker this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return runs[index / RunLength].At(index % RunLength);
        }
    }

    /// <summary>The markers as a list of runs in the order of their starts: the list itself when it is one, else sorted
    /// (two that start together kept in the order given) only when they are not in order already, which
    /// <paramref name="sorted"/> says.</summary>
    public static MarkerList Of(IReadOnlyList<Marker> markers, out bool sorted)
    {
        sorted = false;
        if (markers is MarkerList list)
        {
            return list;
        }
        if (markers.Count == 0)
        {
            return Empty;
        }
        Marker[] all = markers as Marker[] ?? [.. markers];
        if (!InOrder(all))
        {
            sorted = true;
            all = [.. all.OrderBy(m => m.Range.Start)];
        }
        int errors = 0;
        int warnings = 0;
        foreach (Marker marker in all)
        {
            errors += marker.Kind == MarkerKind.Error ? 1 : 0;
            warnings += marker.Kind == MarkerKind.Warning ? 1 : 0;
        }
        return new MarkerList(Cut(all, all.Length), all.Length, (errors, warnings));
    }

    /// <summary>The markers carried through edits (sorted and apart, in this list's offsets), this list itself when all end
    /// before them. The runs the edits reach are moved marker by marker (put in order again should the edits have taken one
    /// past another that starts with it), those wholly before them kept and those wholly after them shifted whole.</summary>
    public MarkerList Through(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0 || Count == 0 || reach[^1] < edits[0].Start)
        {
            return this;
        }
        int from = edits[0].Start;
        int to = edits[^1].Start + edits[^1].Length;
        int shift = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            shift += edits[i].Text.Length - edits[i].Length;
        }
        // The markers are in order, so the runs the edits reach lie side by side, those before and after them untouched.
        int first = -1;
        int last = -1;
        Run[] moved = new Run[runs.Length];
        for (int k = 0; k < runs.Length; k++)
        {
            Run run = runs[k];
            if (run.Highest + run.Shift < from)
            {
                moved[k] = run;
                continue;
            }
            moved[k] = run with { Shift = run.Shift + shift };
            if (run.Lowest + run.Shift <= to)
            {
                first = first < 0 ? k : first;
                last = k;
            }
        }
        if (first >= 0)
        {
            int start = first * RunLength;
            Marker[] reached = new Marker[(last * RunLength) + runs[last].Markers.Count - start];
            for (int i = 0; i < reached.Length; i++)
            {
                reached[i] = Carried(this[start + i], edits);
            }
            Order(reached);
            Cut(reached, reached.Length).CopyTo(moved, first);
        }
        return new MarkerList(moved, Count, Problems);
    }

    /// <summary>The place of the first marker reaching an offset (ending at it or past it), or <see cref="Count"/>: every
    /// marker before it ends before the offset.</summary>
    public int FirstReaching(int offset)
    {
        int low = Sorted.FirstAtOrAfter(reach, offset);
        if (low < runs.Length)
        {
            Run run = runs[low];
            for (int i = 0; i < run.Markers.Count; i++)
            {
                if (run.Markers[i].Range.End + run.Shift >= offset)
                {
                    return (low * RunLength) + i;
                }
            }
        }
        return Count;
    }

    /// <summary>Whether another list holds the very markers this one does, in order, each perhaps moved: the same kinds and
    /// the very same messages, as lists carried from one through the same edits do, which are compared by the runs they
    /// share; a list made afresh reads its messages anew, so it never holds the same.</summary>
    public bool SameMoved(MarkerList other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (Count != other.Count || runs.Length != other.runs.Length)
        {
            return false;
        }
        for (int k = 0; k < runs.Length; k++)
        {
            ArraySegment<Marker> was = runs[k].Markers;
            ArraySegment<Marker> now = other.runs[k].Markers;
            if (ReferenceEquals(was.Array, now.Array) && was.Offset == now.Offset && was.Count == now.Count)
            {
                continue;
            }
            if (was.Count != now.Count)
            {
                return false;
            }
            for (int i = 0; i < was.Count; i++)
            {
                if (was[i].Kind != now[i].Kind || !ReferenceEquals(was[i].Message, now[i].Message))
                {
                    return false;
                }
            }
        }
        return true;
    }

    public IEnumerator<Marker> GetEnumerator()
    {
        foreach (Run run in runs)
        {
            for (int i = 0; i < run.Markers.Count; i++)
            {
                yield return run.At(i);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>A marker carried through edits by its ends: text typed at its end goes inside it while it holds any, and
    /// once it holds none both ends stay before such text.</summary>
    private static Marker Carried(Marker marker, IReadOnlyList<TextEdit> edits)
    {
        TextRange range = marker.Range;
        bool holds = range.Length > 0;
        int start = TextEditing.MapOffset(range.Start, edits, afterInsertions: holds && marker.StartGravity == AnchorGravity.After);
        int end = TextEditing.MapOffset(range.End, edits, afterInsertions: holds);
        return marker with { Range = TextRange.FromBounds(start, Math.Max(start, end)) };
    }

    private static bool InOrder(Marker[] markers)
    {
        for (int i = 1; i < markers.Length; i++)
        {
            if (markers[i].Range.Start < markers[i - 1].Range.Start)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Puts markers carried out of order back in the order of their starts, those that start together kept as
    /// they were: an insertion moves each only past those it was carried beyond.</summary>
    private static void Order(Marker[] markers)
    {
        for (int i = 1; i < markers.Length; i++)
        {
            Marker marker = markers[i];
            int at = i;
            while (at > 0 && markers[at - 1].Range.Start > marker.Range.Start)
            {
                markers[at] = markers[at - 1];
                at--;
            }
            markers[at] = marker;
        }
    }

    /// <summary>Markers cut into runs of <see cref="RunLength"/>, the last shorter.</summary>
    private static Run[] Cut(Marker[] markers, int count)
    {
        Run[] made = new Run[((count - 1) / RunLength) + 1];
        for (int k = 0; k < made.Length; k++)
        {
            int start = k * RunLength;
            made[k] = Run.Of(new ArraySegment<Marker>(markers, start, Math.Min(RunLength, count - start)));
        }
        return made;
    }

    /// <summary>Markers as they stood when the run was made, and what edits wholly before them have added to their offsets
    /// since; the lowest start and the highest end among them as made.</summary>
    private readonly record struct Run(ArraySegment<Marker> Markers, int Shift, int Lowest, int Highest)
    {
        public static Run Of(ArraySegment<Marker> markers)
        {
            int highest = int.MinValue;
            foreach (Marker marker in markers)
            {
                highest = Math.Max(highest, marker.Range.End);
            }
            return new Run(markers, 0, markers[0].Range.Start, highest);
        }

        public Marker At(int index)
        {
            Marker marker = Markers[index];
            return Shift == 0 ? marker : marker with { Range = new TextRange(marker.Range.Start + Shift, marker.Range.Length) };
        }
    }
}
