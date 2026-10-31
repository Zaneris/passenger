namespace Passenger.Core;

/// <summary>Places in what rises by a key, found by halving: the first item whose key is at or after a value, or after it,
/// which is also how many come before; the count when none does. Keys may repeat. A key is read by a static lambda, so a
/// search allocates nothing.</summary>
public static class Sorted
{
    /// <summary>The first of the items whose key is at or after <paramref name="value"/>.</summary>
    public static int FirstAtOrAfter<T, TKey>(ReadOnlySpan<T> items, Func<T, TKey> key, TKey value)
        where TKey : IComparable<TKey> => Halve(new SpanKeys<T, TKey>(items, key, value, past: false));

    /// <summary>The first of the items whose key is after <paramref name="value"/>.</summary>
    public static int FirstAfter<T, TKey>(ReadOnlySpan<T> items, Func<T, TKey> key, TKey value)
        where TKey : IComparable<TKey> => Halve(new SpanKeys<T, TKey>(items, key, value, past: true));

    /// <inheritdoc cref="FirstAtOrAfter{T, TKey}(ReadOnlySpan{T}, Func{T, TKey}, TKey)"/>
    public static int FirstAtOrAfter<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key, TKey value)
        where TKey : IComparable<TKey> => Halve(new ListKeys<T, TKey>(items, key, value, past: false));

    /// <inheritdoc cref="FirstAfter{T, TKey}(ReadOnlySpan{T}, Func{T, TKey}, TKey)"/>
    public static int FirstAfter<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key, TKey value)
        where TKey : IComparable<TKey> => Halve(new ListKeys<T, TKey>(items, key, value, past: true));

    /// <summary>The first of the values at or after <paramref name="value"/>.</summary>
    public static int FirstAtOrAfter<T>(ReadOnlySpan<T> values, T value)
        where T : IComparable<T> => Halve(new SpanValues<T>(values, value, past: false));

    /// <summary>The first of the values after <paramref name="value"/>.</summary>
    public static int FirstAfter<T>(ReadOnlySpan<T> values, T value)
        where T : IComparable<T> => Halve(new SpanValues<T>(values, value, past: true));

    /// <summary>The first of the places 0 to <paramref name="count"/> whose key, read from <paramref name="state"/> at the
    /// place, is at or after <paramref name="value"/>: for keys no list holds, or a struct's list that an interface would
    /// box.</summary>
    public static int FirstAtOrAfter<TState, TKey>(int count, TState state, Func<TState, int, TKey> keyAt, TKey value)
        where TKey : IComparable<TKey> => Halve(new StateKeys<TState, TKey>(count, state, keyAt, value, past: false));

    /// <summary>The first of the places 0 to <paramref name="count"/> whose key, read from <paramref name="state"/> at the
    /// place, is after <paramref name="value"/>: for keys no list holds, or a struct's list that an interface would
    /// box.</summary>
    public static int FirstAfter<TState, TKey>(int count, TState state, Func<TState, int, TKey> keyAt, TKey value)
        where TKey : IComparable<TKey> => Halve(new StateKeys<TState, TKey>(count, state, keyAt, value, past: true));

    private static int Halve<TProbe>(scoped in TProbe probe)
        where TProbe : IProbe, allows ref struct
    {
        int low = 0;
        int high = probe.Count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (probe.Before(middle))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    /// <summary>What is searched: how many places, and whether the one at an index comes before the answer.</summary>
    private interface IProbe
    {
        int Count { get; }

        bool Before(int index);
    }

    private readonly ref struct SpanKeys<T, TKey>(ReadOnlySpan<T> items, Func<T, TKey> key, TKey value, bool past) : IProbe
        where TKey : IComparable<TKey>
    {
        private readonly ReadOnlySpan<T> items = items;

        public int Count => items.Length;

        public bool Before(int index) => Ahead(key(items[index]).CompareTo(value), past);
    }

    private readonly struct ListKeys<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key, TKey value, bool past) : IProbe
        where TKey : IComparable<TKey>
    {
        public int Count => items.Count;

        public bool Before(int index) => Ahead(key(items[index]).CompareTo(value), past);
    }

    private readonly ref struct SpanValues<T>(ReadOnlySpan<T> values, T value, bool past) : IProbe
        where T : IComparable<T>
    {
        private readonly ReadOnlySpan<T> values = values;

        public int Count => values.Length;

        public bool Before(int index) => Ahead(values[index].CompareTo(value), past);
    }

    private readonly struct StateKeys<TState, TKey>(int count, TState state, Func<TState, int, TKey> keyAt, TKey value, bool past) : IProbe
        where TKey : IComparable<TKey>
    {
        public int Count => count;

        public bool Before(int index) => Ahead(keyAt(state, index).CompareTo(value), past);
    }

    /// <summary>Whether a key that compares so with the value comes before the answer: below it, or with
    /// <paramref name="past"/> at most it.</summary>
    private static bool Ahead(int comparison, bool past) => past ? comparison <= 0 : comparison < 0;
}
