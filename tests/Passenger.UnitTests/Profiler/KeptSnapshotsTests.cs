using System.Runtime.CompilerServices;
using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>Which memory snapshots of a run keep their objects: the newest three, an older one letting go of its graph,
/// and of how the others' objects are found in it, as a newer is taken, and one removed at once; one let go keeps its
/// types and says why in a comparison.</summary>
public sealed class KeptSnapshotsTests
{
    private const int Objects = 4;

    [Fact]
    public void Taking_more_snapshots_than_are_kept_lets_go_of_the_oldest_graph_which_a_comparison_names_as_gone()
    {
        KeptSnapshots kept = new();
        (MemorySnapshot first, WeakReference firstGraph) = Take(kept, 1);
        MemorySnapshot second = Take(kept, 2).Snapshot;
        MemorySnapshot third = Take(kept, 3).Snapshot;
        Assert.Null(first.Gone);
        MemorySnapshot fourth = Take(kept, 4).Snapshot;

        Assert.Equal([2, 3, 4], kept.Held);
        string gone = "Snapshot #1's objects were let go as Snapshot #4 was taken: a run keeps those of its newest 3 snapshots";
        Assert.Equal(gone, first.Gone);
        Assert.All([second, third, fourth], s => Assert.Null(s.Gone));
        Assert.Equal(gone, fourth.WhyNotComparable(first));
        Assert.Equal(gone, first.WhyNotComparable(fourth));
        Assert.Equal(gone, Assert.Throws<InvalidOperationException>(() => fourth.CompareWith(first)).Message);
        Assert.Equal(gone, Assert.Throws<InvalidOperationException>(() => first.ObjectsOf(first.Types[0])).Message);
        Assert.Equal(Objects, first.Types[0].Count);
        Assert.Equal([2, 3], fourth.Earlier.Keys.Order());
        Assert.Equal(Objects, fourth.CompareWith(second).SurvivedObjects);
        Assert.True(Collected(firstGraph), "The oldest snapshot's graph was still held once a fourth was taken");
    }

    [Fact]
    public void A_snapshot_removed_lets_go_of_its_graph_at_once_and_the_run_follows_it_no_further()
    {
        List<int> unfollowed = [];
        KeptSnapshots kept = new(unfollowed.Add);
        MemorySnapshot first = Take(kept, 1).Snapshot;
        (MemorySnapshot second, WeakReference secondGraph) = Take(kept, 2);
        MemorySnapshot third = Take(kept, 3).Snapshot;

        second.Remove();
        second.Remove();
        Assert.Equal([1, 3], kept.Held);
        Assert.Equal([2], unfollowed);
        Assert.Equal("Snapshot #2 was removed", second.Gone);
        Assert.Equal([1], third.Earlier.Keys);
        Assert.Equal(Objects, third.CompareWith(first).SurvivedObjects);
        Assert.True(Collected(secondGraph), "A removed snapshot's graph was still held");
    }

    /// <summary>A snapshot taken into the run, with how each snapshot it keeps has its objects in this one (every object
    /// lived on, where it was), and a weak reference to its graph that nothing here holds.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (MemorySnapshot Snapshot, WeakReference Graph) Take(KeptSnapshots kept, int number)
    {
        HeapGraph graph = Graph();
        Dictionary<int, Identity> earlier = [];
        foreach (int older in kept.Held.Where(n => n > number - KeptSnapshots.Count))
        {
            earlier[older] = new Identity([.. Enumerable.Range(0, Objects)], null);
        }
        MemorySnapshot snapshot = new(kept, number, new DateTime(2026, 10, 29, 12, 0, number, DateTimeKind.Local), graph, TimeSpan.FromSeconds(1), "dump 1.0 s", earlier);
        kept.Took(snapshot);
        return (snapshot, new WeakReference(graph));
    }

    private static HeapGraph Graph() => new()
    {
        Address = [0x1000, 0x1020, 0x1040, 0x1060],
        Type = new int[Objects],
        Size = [24, 24, 24, 24],
        EdgeStart = new int[Objects + 1],
        EdgeTarget = [],
        EdgeOffset = [],
        Types = [new HeapTypeInfo("Item", 0x7000, 0, false)],
        Roots = [new GcRoot(RootKind.StaticReference, "Kept.Items")],
        Root = new int[Objects],
        Parent = [-1, -1, -1, -1],
        ParentOffset = new int[Objects],
        Depth = new int[Objects],
        Retained = [24, 24, 24, 24],
        Enter = [0, 2, 4, 6],
        Leave = [1, 3, 5, 7],
        TypeRetained = [96],
        PointerSize = 8,
    };

    private static bool Collected(WeakReference graph)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return !graph.IsAlive;
    }
}
