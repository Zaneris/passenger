using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Passenger.App;

/// <summary>A row of a tree a list draws as rows (<see cref="TreeRows{T}"/>, <see cref="TreeKeys"/>): how deep it sits,
/// whether rows go under it (or may, before they are asked for), and whether they are shown.</summary>
public interface ITreeRow
{
    int Depth { get; }

    bool Expands { get; }

    bool IsExpanded { get; }
}

/// <summary>A tree's rows as a list draws them, the rows of every open row after it, one deeper. Opening or closing a row
/// moves only the rows under it, as one change, so the rest stay the same rows (chosen if they were) and a list draws
/// a class opened or closed once rather than once a row. A row closed keeps whether the rows under it were open, so it
/// opens again as it was left (<see cref="Shown"/>).</summary>
public sealed class TreeRows<T> : ObservableCollection<T>
    where T : class, ITreeRow
{
    /// <summary>A level's rows, each open one followed by its own, by <paramref name="children"/>, through every row open.</summary>
    public static IEnumerable<T> Shown(IEnumerable<T> level, Func<T, IEnumerable<T>?> children)
    {
        foreach (T row in level)
        {
            yield return row;
            if (row.IsExpanded)
            {
                foreach (T below in Shown(children(row) ?? [], children))
                {
                    yield return below;
                }
            }
        }
    }

    /// <summary>Puts the rows under an open row in after it, where it is shown.</summary>
    public void Open(T row, IEnumerable<T> below)
    {
        int at = IndexOf(row);
        if (at >= 0)
        {
            InsertRange(at + 1, below);
        }
    }

    /// <summary>Takes out the rows under a row, where it is shown.</summary>
    public void Close(T row)
    {
        int at = IndexOf(row);
        if (at >= 0)
        {
            RemoveRange(at + 1, End(at) - at - 1);
        }
    }

    /// <summary>The row a row sits under: the nearest above it, less deep; null for a top row.</summary>
    public T? Holder(T row)
    {
        for (int at = IndexOf(row) - 1; at >= 0; at--)
        {
            if (this[at].Depth < row.Depth)
            {
                return this[at];
            }
        }
        return null;
    }

    /// <summary>Where the rows under the row at an index end: the next row as shallow as it, or the end.</summary>
    private int End(int at)
    {
        int end = at + 1;
        while (end < Count && this[end].Depth > this[at].Depth)
        {
            end++;
        }
        return end;
    }

    public void InsertRange(int index, IEnumerable<T> rows)
    {
        List<T> added = [.. rows];
        if (added.Count == 0)
        {
            return;
        }
        CheckReentrancy();
        ((List<T>)Items).InsertRange(index, added);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, index));
    }

    public void RemoveRange(int index, int count)
    {
        if (count == 0)
        {
            return;
        }
        CheckReentrancy();
        List<T> removed = ((List<T>)Items).GetRange(index, count);
        ((List<T>)Items).RemoveRange(index, count);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    /// <summary>Shows these rows: the runs of rows that went are taken out and the runs that came put in, so the rows that
    /// stay are the same rows, chosen if they were; rows that moved are shown again whole.</summary>
    public void Show(IReadOnlyList<T> wanted) => Show(0, Count, wanted);

    /// <summary>Shows these rows under a row, where it is shown, as <see cref="Show(IReadOnlyList{T})"/> shows them all:
    /// what the rest of the tree shows is left as it is, and costs nothing however many rows it holds.</summary>
    public void ShowUnder(T row, IReadOnlyList<T> wanted)
    {
        int at = IndexOf(row);
        if (at >= 0)
        {
            Show(at + 1, End(at), wanted);
        }
    }

    /// <summary>Shows these rows in place of those from one index to before another.</summary>
    private void Show(int from, int to, IReadOnlyList<T> wanted)
    {
        HashSet<T> keep = new(wanted, ReferenceEqualityComparer.Instance);
        List<T> shown = ((List<T>)Items).GetRange(from, to - from);
        HashSet<T> staying = new(shown.Where(keep.Contains), ReferenceEqualityComparer.Instance);
        if (!shown.Where(keep.Contains).SequenceEqual(wanted.Where(staying.Contains)))
        {
            if (from == 0 && to == Count)
            {
                CheckReentrancy();
                Items.Clear();
                ((List<T>)Items).AddRange(wanted);
                Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                return;
            }
            RemoveRange(from, to - from);
            InsertRange(from, wanted);
            return;
        }
        for (int at = to - 1; at >= from;)
        {
            if (keep.Contains(this[at]))
            {
                at--;
                continue;
            }
            int end = at;
            while (at >= from && !keep.Contains(this[at]))
            {
                at--;
            }
            RemoveRange(at + 1, end - at);
        }
        // What stays is now in the order wanted, so each run of new rows goes in where the next row staying is.
        for (int at = 0; at < wanted.Count;)
        {
            int end = at;
            while (end < wanted.Count && !staying.Contains(wanted[end]))
            {
                end++;
            }
            if (end > at)
            {
                InsertRange(from + at, wanted.Skip(at).Take(end - at));
            }
            at = end + 1;
        }
    }

    private void Changed(NotifyCollectionChangedEventArgs change)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(change);
    }
}
