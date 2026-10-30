namespace Passenger.Profiler;

/// <summary>A run's memory snapshots that keep their objects, the run's only ones to compare within: those of the newest
/// <see cref="Count"/> taken, each holding its heap's graph (some 80 bytes an object) and how the others' objects are found
/// in it. An older one lets go of its objects as a newer is taken, one removed at once; either keeps its types to list.</summary>
internal sealed class KeptSnapshots(Action<int>? removed = null)
{
    /// <summary>Three: a comparison takes two, and a leak is surest found with three (a baseline, then a snapshot after each
    /// of two rounds of the same work, what the second has new that the third still holds being what leaks). Three of a heap
    /// of ten million objects take some 2.4 GB.</summary>
    public const int Count = 3;

    private readonly List<MemorySnapshot> held = [];

    /// <summary>The numbers of the snapshots that keep their objects now.</summary>
    public IReadOnlyList<int> Held
    {
        get
        {
            lock (held)
            {
                return [.. held.Select(s => s.Number)];
            }
        }
    }

    /// <summary>A snapshot taken: kept, and those past the newest <see cref="Count"/> let go of their objects, with what
    /// the rest know of them.</summary>
    public void Took(MemorySnapshot taken)
    {
        lock (held)
        {
            held.Add(taken);
            foreach (MemorySnapshot old in held.Where(s => s.Number <= taken.Number - Count).ToList())
            {
                held.Remove(old);
                old.LetGo($"{old.Name}'s objects were let go as {taken.Name} was taken: a run keeps those of its newest {Count} snapshots");
                Forget(old.Number);
            }
        }
    }

    /// <summary>A snapshot removed: its objects let go at once, and the run follows them no further.</summary>
    public void Remove(MemorySnapshot snapshot)
    {
        lock (held)
        {
            if (!held.Remove(snapshot))
            {
                return;
            }
            snapshot.LetGo($"{snapshot.Name} was removed");
            Forget(snapshot.Number);
        }
        removed?.Invoke(snapshot.Number);
    }

    private void Forget(int number)
    {
        foreach (MemorySnapshot kept in held)
        {
            kept.Forget(number);
        }
    }
}
