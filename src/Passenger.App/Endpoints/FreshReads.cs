using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Passenger.App.Endpoints;

/// <summary>Reads kept by key for a moment, so asks made together share one, and let go once past it: each read kept lets go
/// of every read that has ended and stood longer, so what is held is only what was read within a moment of the last. Times
/// are <see cref="Stopwatch"/> timestamps, given by the caller. Any thread.</summary>
public sealed class FreshReads<T>(TimeSpan fresh, IEqualityComparer<string> keys)
{
    private readonly ConcurrentDictionary<string, (Task<T> Read, long At)> reads = new(keys);

    /// <summary>How many reads are held.</summary>
    public int Count => reads.Count;

    /// <summary>The read of a key still under way, or ended within the moment.</summary>
    public bool TryGet(string key, long now, [NotNullWhen(true)] out Task<T>? read)
    {
        if (reads.TryGetValue(key, out (Task<T> Read, long At) known) && (!known.Read.IsCompleted || Stopwatch.GetElapsedTime(known.At, now) < fresh))
        {
            read = known.Read;
            return true;
        }
        read = null;
        return false;
    }

    /// <summary>Holds a read begun at <paramref name="now"/>, in place of the key's last, letting go of every read past the moment.</summary>
    public void Add(string key, Task<T> read, long now)
    {
        reads[key] = (read, now);
        foreach (KeyValuePair<string, (Task<T> Read, long At)> held in reads)
        {
            if (held.Value.Read.IsCompleted && Stopwatch.GetElapsedTime(held.Value.At, now) >= fresh)
            {
                reads.TryRemove(held);
            }
        }
    }
}
