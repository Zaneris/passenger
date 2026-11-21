using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Editor;

/// <summary>What the editor's rows draw and keep from frame to frame (shapes, labels, a row's hints), by what each is, with
/// the frame that last took it: a row drawn later takes again what the rows on screen drew without making it, and a frame
/// lets go of what no row on screen took (<see cref="Begin"/>), disposing what is disposable.</summary>
internal sealed class FrameKept<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly Dictionary<TKey, (TValue Value, long Drawn)> kept = [];
    private long now;

    /// <summary>How many have been made: what drawing them has cost.</summary>
    public long Made { get; private set; }

    public int Count => kept.Count;

    /// <summary>The one kept for a key, kept on for this frame.</summary>
    public bool TryGet(TKey key, [NotNullWhen(true)] out TValue? value)
    {
        ref (TValue Value, long Drawn) entry = ref CollectionsMarshal.GetValueRefOrNullRef(kept, key);
        if (Unsafe.IsNullRef(ref entry))
        {
            value = null;
            return false;
        }
        entry.Drawn = now;
        value = entry.Value;
        return true;
    }

    /// <summary>The one kept for a key, else the one made now, kept.</summary>
    public TValue Get(TKey key, Func<TValue> make) => TryGet(key, out TValue? value) ? value : Add(key, make());

    /// <summary>Keeps one made for a key, in place of any kept for it.</summary>
    public TValue Add(TKey key, TValue value)
    {
        Made++;
        kept[key] = (value, now);
        return value;
    }

    /// <summary>Starts a frame, letting go of what was last taken before <paramref name="keepFrom"/>.</summary>
    public void Begin(long frame, long keepFrom)
    {
        now = frame;
        foreach ((TKey key, (TValue value, long drawn)) in kept)
        {
            if (drawn < keepFrom)
            {
                (value as IDisposable)?.Dispose();
                kept.Remove(key);
            }
        }
    }

    /// <summary>Lets go of all it keeps, for a new text, look or font.</summary>
    public void Clear()
    {
        foreach ((TValue value, long _) in kept.Values)
        {
            (value as IDisposable)?.Dispose();
        }
        kept.Clear();
    }
}
