namespace Passenger.Debugger.Relay;

/// <summary>An enumerator in the app read many elements at a time and handed to the engine as it asks, one at a time or
/// more, with the results the app's own enumerator would give: an element after another, then its end (S_FALSE, said
/// again however often asked) or a failure where the app's enumerator failed (said once, as the app's moved past that
/// element). Each element held keeps its reference until the engine takes it or the buffer lets it go.</summary>
internal sealed class EnumBuffer<T>
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    private readonly Queue<T> _held = new();
    private int? _end;

    /// <summary>The app's Next: up to <c>count</c> elements put into the list, and its result.</summary>
    public delegate int Fetch(int count, List<T> into);

    public int HeldCount => _held.Count;

    public int Next(int count, Span<T> into, out int fetched, Fetch fetch, int batch)
    {
        fetched = 0;
        while (fetched < count)
        {
            if (_held.TryDequeue(out T? each))
            {
                into[fetched++] = each;
                continue;
            }
            if (_end is int end)
            {
                if (end < 0)
                {
                    _end = null;
                }
                return end;
            }
            List<T> got = [];
            int result = fetch(Math.Max(count - fetched, batch), got);
            foreach (T element in got)
            {
                _held.Enqueue(element);
            }
            if (result != S_OK || got.Count == 0)
            {
                _end = result == S_OK ? S_FALSE : result;
            }
        }
        return S_OK;
    }

    /// <summary>Passes over <paramref name="count"/> elements: those held let go, a failure held counted as one, the rest
    /// skipped in the app.</summary>
    public int Skip(int count, Action<T> letGo, Func<int, int> skip)
    {
        while (count > 0 && _held.TryDequeue(out T? each))
        {
            letGo(each);
            count--;
        }
        if (count > 0 && _end is < 0)
        {
            _end = null;
            count--;
        }
        return count > 0 ? skip(count) : S_OK;
    }

    /// <summary>Lets go of everything held, as the enumerator starts again or goes.</summary>
    public void Clear(Action<T> letGo)
    {
        while (_held.TryDequeue(out T? each))
        {
            letGo(each);
        }
        _end = null;
    }

    /// <summary>Gives a clone of the app's enumerator, which stands where the app's does, what this one holds, each element
    /// held again.</summary>
    public void CopyTo(EnumBuffer<T> clone, Func<T, T> holdAgain)
    {
        foreach (T each in _held)
        {
            clone._held.Enqueue(holdAgain(each));
        }
        clone._end = _end;
    }
}
