namespace Passenger.Profiler;

/// <summary>A type in a comparison of two snapshots: the difference in its objects and bytes, and its objects created
/// since the older snapshot (new), collected since (dead), and in both (survived).</summary>
public sealed record TypeDelta(string Name, int ObjectsDelta, long BytesDelta, int NewObjects, long NewBytes, int DeadObjects, long DeadBytes,
    int SurvivedObjects, long SurvivedBytes)
{
    internal ulong MethodTable { get; init; }
}

/// <summary>Two snapshots of a run compared object by object: an object of the older is the same object in the newer
/// when the run's collections moved it there (the GC's own reports of what it moved), so new and dead objects are
/// those, not a difference in counts. It holds both snapshots' objects for as long as it is kept.</summary>
public sealed class SnapshotComparison
{
    private readonly Identity identity;
    private readonly bool[] survived;
    private readonly HeapGraph before;
    private readonly HeapGraph after;

    internal SnapshotComparison(MemorySnapshot older, MemorySnapshot newer, HeapGraph before, HeapGraph after, Identity identity)
    {
        Older = older;
        Newer = newer;
        this.identity = identity;
        this.before = before;
        this.after = after;
        Inexact = identity.Inexact;
        survived = new bool[after.Count];
        Dictionary<ulong, Tally> tallies = [];
        for (int i = 0; i < before.Count; i++)
        {
            Tally tally = TallyOf(before, before.Type[i]);
            tally.Older++;
            tally.OlderBytes += before.Size[i];
            if (identity.Later[i] is var later and >= 0)
            {
                survived[later] = true;
            }
            else
            {
                tally.Dead++;
                tally.DeadBytes += before.Size[i];
            }
        }
        for (int j = 0; j < after.Count; j++)
        {
            Tally tally = TallyOf(after, after.Type[j]);
            tally.Newer++;
            tally.NewerBytes += after.Size[j];
            if (survived[j])
            {
                tally.Survived++;
                tally.SurvivedBytes += after.Size[j];
            }
            else
            {
                tally.New++;
                tally.NewBytes += after.Size[j];
            }
        }
        Types = [.. tallies.Values
            .Select(t => new TypeDelta(t.Name, t.Newer - t.Older, t.NewerBytes - t.OlderBytes, t.New, t.NewBytes, t.Dead, t.DeadBytes, t.Survived, t.SurvivedBytes) { MethodTable = t.MethodTable })
            .OrderByDescending(t => t.ObjectsDelta)
            .ThenByDescending(t => t.BytesDelta)
            .ThenBy(t => t.Name, StringComparer.Ordinal)];
        NewObjects = Types.Sum(t => t.NewObjects);
        DeadObjects = Types.Sum(t => t.DeadObjects);
        SurvivedObjects = Types.Sum(t => t.SurvivedObjects);

        Tally TallyOf(HeapGraph graph, int type)
        {
            HeapTypeInfo info = graph.Types[type];
            if (!tallies.TryGetValue(info.MethodTable, out Tally? tally))
            {
                tally = new Tally(info.Name, info.MethodTable);
                tallies[info.MethodTable] = tally;
            }
            return tally;
        }
    }

    public MemorySnapshot Older { get; }

    public MemorySnapshot Newer { get; }

    /// <summary>"Comparison of Snapshot #1 to Snapshot #2".</summary>
    public string Title => $"Comparison of {Older.Name} to {Newer.Name}";

    /// <summary>The types, the most grown first.</summary>
    public IReadOnlyList<TypeDelta> Types { get; }

    public int NewObjects { get; }

    public int DeadObjects { get; }

    public int SurvivedObjects { get; }

    /// <summary>Why some objects may show as dead and new though they lived on (the profiler lost some of the program's
    /// events), or null.</summary>
    public string? Inexact { get; }

    /// <summary>The newer snapshot's objects of a type that the older did not have.</summary>
    public ObjectSet NewObjectsOf(TypeDelta type) => Of(Newer, after, type, j => !survived[j], $"New objects of the type {type.Name}");

    /// <summary>The older snapshot's objects of a type that the newer no longer has.</summary>
    public ObjectSet DeadObjectsOf(TypeDelta type) => Of(Older, before, type, i => identity.Later[i] < 0, $"Dead objects of the type {type.Name}");

    /// <summary>The newer snapshot's objects of a type that the older had too.</summary>
    public ObjectSet SurvivedObjectsOf(TypeDelta type) => Of(Newer, after, type, j => survived[j], $"Survived objects of the type {type.Name}");

    private static ObjectSet Of(MemorySnapshot snapshot, HeapGraph graph, TypeDelta type, Func<int, bool> chosen, string title)
    {
        List<int> found = [];
        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Types[graph.Type[i]].MethodTable == type.MethodTable && chosen(i))
            {
                found.Add(i);
            }
        }
        return new ObjectSet(snapshot, graph, title, [.. found]);
    }

    private sealed class Tally(string name, ulong methodTable)
    {
        public string Name { get; } = name;
        public ulong MethodTable { get; } = methodTable;
        public int Older { get; set; }
        public long OlderBytes { get; set; }
        public int Newer { get; set; }
        public long NewerBytes { get; set; }
        public int New { get; set; }
        public long NewBytes { get; set; }
        public int Dead { get; set; }
        public long DeadBytes { get; set; }
        public int Survived { get; set; }
        public long SurvivedBytes { get; set; }
    }
}
