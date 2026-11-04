using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A snapshot read from the runtime's own GC heap dump: its type names written as a dump's reader writes them, an
/// object no root the dump names reaches kept under the runtime's own root rather than dropped, and a path through holders
/// whose fields the dump does not name told by their types, an array's by its elements.</summary>
public sealed class GcHeapDumpTests
{
    [Theory]
    [InlineData("Hoarded", "Hoarded")]
    [InlineData("System.Int32[]", "System.Int32[]")]
    [InlineData("System.Int32[,]", "System.Int32[,]")]
    [InlineData("System.Collections.Generic.Dictionary`2[System.String,System.Int32]", "System.Collections.Generic.Dictionary<System.String, System.Int32>")]
    [InlineData("System.Collections.Generic.List`1[System.Func`1[System.Diagnostics.Tracing.EventSource]]",
        "System.Collections.Generic.List<System.Func<System.Diagnostics.Tracing.EventSource>>")]
    [InlineData("Entry[System.String,System.Object][]", "Entry<System.String, System.Object>[]")]
    [InlineData("System.Collections.Generic.Dictionary`2[System.String,System.Int32][]", "System.Collections.Generic.Dictionary<System.String, System.Int32>[]")]
    [InlineData("Outer`1+Inner[System.Int32]", "Outer+Inner<System.Int32>")]
    [InlineData("System.Collections.Generic.List`1[[System.String, System.Private.CoreLib, Version=11.0.0.0]]", "System.Collections.Generic.List<System.String>")]
    public void A_runtime_type_name_is_written_as_a_dump_reader_writes_it(string written, string shown) =>
        Assert.Equal(shown, GcHeapDump.TypeName(written));

    [Fact]
    public void An_object_no_named_root_reaches_is_kept_under_the_runtimes_root_and_unnamed_holders_are_told_by_their_types()
    {
        HeapGraph.Building parts = new();
        int list = parts.TypeOf(0x10, () => new HeapTypeInfo("System.Collections.Generic.List<Item>", 0x10, 0, false));
        int array = parts.TypeOf(0x20, () => new HeapTypeInfo("Item[]", 0x20, 0, true));
        int item = parts.TypeOf(0x30, () => new HeapTypeInfo("Item", 0x30, 0, false));
        parts.Add(0x1000, list, 32);
        parts.Refers(0x2000, HeapGraph.UnnamedOffset);
        parts.Add(0x2000, array, 40);
        parts.Refers(0x3000, HeapGraph.UnnamedOffset);
        parts.Refers(0x3100, HeapGraph.UnnamedOffset);
        parts.Add(0x3000, item, 24);
        parts.Add(0x3100, item, 24);
        // Nothing the dump names as a root reaches this one.
        parts.Add(0x4000, item, 24);
        parts.EndObjects();
        parts.Root(0x1000, new GcRoot(RootKind.StaticReference, "Items"));
        HeapGraph graph = parts.Analyse(8, CancellationToken.None, new GcRoot(RootKind.Runtime, string.Empty));

        Assert.Equal(5, graph.Count);
        Assert.Equal(RootKind.Runtime, graph.Roots[graph.Root[graph.IndexOf(0x4000)]].Kind);
        Assert.Equal("Kept by the runtime", graph.Roots[graph.Root[graph.IndexOf(0x4000)]].Text);
        Assert.Equal(24, graph.Retained[graph.IndexOf(0x4000)]);
        Assert.Equal(32 + 40 + 24 + 24, graph.Retained[graph.IndexOf(0x1000)]);

        MemorySnapshot snapshot = new(new KeptSnapshots(), 1, DateTime.Now, graph, TimeSpan.Zero, string.Empty, new Dictionary<int, Identity>());
        ObjectSet items = snapshot.ObjectsOf(snapshot.Types.Single(t => t.Name == "Item"));
        Assert.Equal(
            ["Static reference: Items → System.Collections.Generic.List<Item> → Item[] elements → Item", "Kept by the runtime → Item"],
            items.KeyRetentionPaths().Select(p => p.Text));
    }
}
