namespace Passenger.Profiler;

/// <summary>A type on the heap: how many objects of it live, the bytes they take themselves (shallow), and the bytes
/// they keep alive together (retained).</summary>
public sealed record HeapType(string Name, int Count, long Bytes, long RetainedBytes)
{
    internal int Index { get; init; }
}

/// <summary>What a program's heap held at a moment: every object a GC root keeps alive, by type, with what each retains
/// and the shortest path that keeps it. The runtime's createdump writes a dump of the heap (no debugger attaches) and
/// ClrMD reads it; the dump, as large as the heap, is deleted once read. Snapshots of one run can be compared, object
/// by object.</summary>
public sealed class MemorySnapshot
{
    private readonly IReadOnlyDictionary<int, Identity> earlier;

    internal MemorySnapshot(object run, int number, DateTime taken, HeapGraph graph, TimeSpan took, string phases, IReadOnlyDictionary<int, Identity> earlier)
    {
        Run = run;
        Number = number;
        Taken = taken;
        Graph = graph;
        Took = took;
        Phases = phases;
        this.earlier = earlier;
        int[] counts = new int[graph.Types.Length];
        long[] bytes = new long[graph.Types.Length];
        for (int i = 0; i < graph.Count; i++)
        {
            counts[graph.Type[i]]++;
            bytes[graph.Type[i]] += graph.Size[i];
        }
        List<HeapType> types = [];
        for (int t = 0; t < counts.Length; t++)
        {
            if (counts[t] > 0)
            {
                types.Add(new HeapType(graph.Types[t].Name, counts[t], bytes[t], graph.TypeRetained[t]) { Index = t });
            }
        }
        Types = [.. types.OrderByDescending(t => t.Bytes).ThenBy(t => t.Name, StringComparer.Ordinal)];
        Bytes = bytes.Sum();
        Objects = graph.Count;
    }

    /// <summary>Which of the run's snapshots this is, from 1.</summary>
    public int Number { get; }

    /// <summary>"Snapshot #2".</summary>
    public string Name => $"Snapshot #{Number}";

    public DateTime Taken { get; }

    /// <summary>The types, the most bytes first.</summary>
    public IReadOnlyList<HeapType> Types { get; }

    /// <summary>The live objects together, in bytes.</summary>
    public long Bytes { get; }

    public int Objects { get; }

    /// <summary>How long the dump and its reading took, the program held for the dump's part.</summary>
    public TimeSpan Took { get; }

    /// <summary>What the time went on: the dump, its reading, the retention analysis.</summary>
    public string Phases { get; }

    internal HeapGraph Graph { get; }

    /// <summary>The run it was taken in: only snapshots of one run can be compared.</summary>
    internal object Run { get; }

    /// <summary>How each earlier snapshot of the run, by its number, has its objects in this one.</summary>
    internal IReadOnlyDictionary<int, Identity> Earlier => earlier;

    /// <summary>Every object of a type, as an object set.</summary>
    public ObjectSet ObjectsOf(HeapType type)
    {
        List<int> found = [];
        for (int i = 0; i < Graph.Count; i++)
        {
            if (Graph.Type[i] == type.Index)
            {
                found.Add(i);
            }
        }
        return new ObjectSet(this, type.Name, [.. found]);
    }

    /// <summary>The object at an address, alone, as an object set; null when the snapshot has none there.</summary>
    public ObjectSet? ObjectAt(ulong address) =>
        Graph.IndexOf(address) is var at and >= 0 ? new ObjectSet(this, $"{Graph.Types[Graph.Type[at]].Name} at 0x{address:x}", [at]) : null;

    /// <summary>Why this snapshot and another cannot be compared object by object, or null when they can.</summary>
    public string? WhyNotComparable(MemorySnapshot other)
    {
        if (other == this)
        {
            return $"{Name} is the snapshot itself";
        }
        if (other.Run != Run)
        {
            return $"{Name} and {other.Name} are of different runs: objects are told apart only within one";
        }
        (MemorySnapshot older, MemorySnapshot newer) = other.Number < Number ? (other, this) : (this, other);
        // A container's tools say how the older's objects are found: by one entry for each of them.
        return newer.earlier.TryGetValue(older.Number, out Identity? identity) && identity.Later.Length == older.Graph.Count ? null
            : $"The objects of {older.Name} could not be followed to {newer.Name}: the program's collections were not all seen";
    }

    /// <summary>Compares this snapshot with another of the run, the older to the newer, type by type: the new, dead and
    /// survived objects. Matching every object takes a while on a large heap: call it off the UI thread.</summary>
    public SnapshotComparison CompareWith(MemorySnapshot other)
    {
        if (WhyNotComparable(other) is { } why)
        {
            throw new InvalidOperationException(why);
        }
        (MemorySnapshot older, MemorySnapshot newer) = other.Number < Number ? (other, this) : (this, other);
        return new SnapshotComparison(older, newer, newer.earlier[older.Number]);
    }
}

/// <summary>How the objects of an earlier snapshot are found in a later one: each one's index there, or -1 when it died,
/// and why the matching may be off, if it may.</summary>
internal sealed record Identity(int[] Later, string? Inexact);
