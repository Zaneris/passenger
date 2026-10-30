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
/// by object, while they keep their objects (<see cref="KeptSnapshots"/>); one let go of them keeps its types.</summary>
public sealed class MemorySnapshot
{
    private readonly Dictionary<int, Identity> earlier;
    private HeapGraph? graph;
    private string? gone;

    internal MemorySnapshot(KeptSnapshots run, int number, DateTime taken, HeapGraph graph, TimeSpan took, string phases, Dictionary<int, Identity> earlier)
    {
        Run = run;
        Number = number;
        Taken = taken;
        this.graph = graph;
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

    /// <summary>Its heap's objects, or null once let go of.</summary>
    internal HeapGraph? Graph
    {
        get
        {
            lock (earlier)
            {
                return graph;
            }
        }
    }

    /// <summary>Why its objects were let go of (<see cref="KeptSnapshots"/>), or null while it keeps them.</summary>
    public string? Gone
    {
        get
        {
            lock (earlier)
            {
                return gone;
            }
        }
    }

    /// <summary>The run it was taken in: only snapshots of one run can be compared.</summary>
    internal KeptSnapshots Run { get; }

    /// <summary>How each earlier snapshot of the run, by its number, has its objects in this one.</summary>
    internal IReadOnlyDictionary<int, Identity> Earlier
    {
        get
        {
            lock (earlier)
            {
                return new Dictionary<int, Identity>(earlier);
            }
        }
    }

    /// <summary>Lets go of its objects at once, and the run follows them no further; it is listed no more.</summary>
    public void Remove() => Run.Remove(this);

    /// <summary>Lets go of its objects and of how the earlier snapshots' are found in it, saying why.</summary>
    internal void LetGo(string why)
    {
        lock (earlier)
        {
            graph = null;
            gone ??= why;
            earlier.Clear();
        }
    }

    /// <summary>Lets go of how an earlier snapshot's objects, let go of themselves, are found in it.</summary>
    internal void Forget(int number)
    {
        lock (earlier)
        {
            earlier.Remove(number);
        }
    }

    /// <summary>Every object of a type, as an object set. Throws an <see cref="InvalidOperationException"/> saying why
    /// once its objects are let go of.</summary>
    public ObjectSet ObjectsOf(HeapType type)
    {
        HeapGraph graph = Held();
        List<int> found = [];
        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Type[i] == type.Index)
            {
                found.Add(i);
            }
        }
        return new ObjectSet(this, graph, type.Name, [.. found]);
    }

    /// <summary>The object at an address, alone, as an object set; null when the snapshot has none there. Throws an
    /// <see cref="InvalidOperationException"/> saying why once its objects are let go of.</summary>
    public ObjectSet? ObjectAt(ulong address)
    {
        HeapGraph graph = Held();
        return graph.IndexOf(address) is var at and >= 0 ? new ObjectSet(this, graph, $"{graph.Types[graph.Type[at]].Name} at 0x{address:x}", [at]) : null;
    }

    private HeapGraph Held()
    {
        lock (earlier)
        {
            return graph ?? throw new InvalidOperationException(gone);
        }
    }

    /// <summary>Why this snapshot and another cannot be compared object by object, or null when they can.</summary>
    public string? WhyNotComparable(MemorySnapshot other) => Comparable(other, out _, out _, out _);

    /// <summary>Why not comparable, or null with the older's and the newer's objects and how the older's are found in the
    /// newer, taken together.</summary>
    private string? Comparable(MemorySnapshot other, out HeapGraph? before, out HeapGraph? after, out Identity? identity)
    {
        (before, after, identity) = (null, null, null);
        if (other == this)
        {
            return $"{Name} is the snapshot itself";
        }
        if (other.Run != Run)
        {
            return $"{Name} and {other.Name} are of different runs: objects are told apart only within one";
        }
        (MemorySnapshot older, MemorySnapshot newer) = other.Number < Number ? (other, this) : (this, other);
        lock (older.earlier)
        {
            before = older.graph;
            if (before is null)
            {
                return older.gone;
            }
        }
        lock (newer.earlier)
        {
            after = newer.graph;
            if (after is null)
            {
                return newer.gone;
            }
            // A container's tools say how the older's objects are found: by one entry for each of them.
            return newer.earlier.TryGetValue(older.Number, out identity) && identity.Later.Length == before.Count ? null
                : $"The objects of {older.Name} could not be followed to {newer.Name}: the program's collections were not all seen";
        }
    }

    /// <summary>Compares this snapshot with another of the run, the older to the newer, type by type: the new, dead and
    /// survived objects. Matching every object takes a while on a large heap: call it off the UI thread.</summary>
    public SnapshotComparison CompareWith(MemorySnapshot other)
    {
        if (Comparable(other, out HeapGraph? before, out HeapGraph? after, out Identity? identity) is { } why)
        {
            throw new InvalidOperationException(why);
        }
        (MemorySnapshot older, MemorySnapshot newer) = other.Number < Number ? (other, this) : (this, other);
        return new SnapshotComparison(older, newer, before!, after!, identity!);
    }
}

/// <summary>How the objects of an earlier snapshot are found in a later one: each one's index there, or -1 when it died,
/// and why the matching may be off, if it may.</summary>
internal sealed record Identity(int[] Later, string? Inexact);
