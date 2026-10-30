using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Profiling;
using Passenger.Profiler;

namespace Passenger.Tests;

/// <summary>Memory snapshots, from the profiler window: two snapshots of a run compared object by object (new, dead and
/// survived, however the GC moved what lived on), a type's objects opened to the shortest paths that keep them from the
/// GC roots with each root's kind and what they retain, and a heap of a million objects read, compared and opened; only
/// the newest three keep their objects, a program that ends as it is snapshot is said to have ended, and a snapshot given
/// up leaves no dump behind.</summary>
public sealed class MemorySnapshotsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("memory-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Two_snapshots_compared_show_the_objects_made_and_collected_between_them_and_those_the_GC_moved_as_survived()
    {
        // Survivors with garbage between them, so a compacting collection moves them.
        MainWindow window = Profiled("Grower", """
            using System.Runtime.CompilerServices;
            Holder.Start();
            Console.WriteLine("ready");
            while (Console.ReadLine() is { } line)
            {
                Holder.Grow(4000);
                Console.WriteLine("grown");
            }
            static class Holder
            {
                public static readonly List<Survivor> Survivors = [];
                public static List<Doomed>? Doomed = [];
                public static readonly List<Leak> Leaks = [];
                private static readonly List<byte[]> ballast = [];

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static void Start()
                {
                    for (int i = 0; i < 500; i++)
                    {
                        Survivors.Add(new Survivor(i));
                        ballast.Add(new byte[200]);
                    }
                    for (int i = 0; i < 300; i++)
                    {
                        Doomed!.Add(new Doomed());
                    }
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static void Grow(int count)
                {
                    Doomed = null;
                    ballast.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        Leaks.Add(new Leak(i));
                    }
                    long garbage = 0;
                    for (int i = 0; i < 200_000; i++)
                    {
                        garbage += new byte[100].Length;
                    }
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.KeepAlive(garbage);
                }
            }
            sealed class Survivor(int number) { public int Number { get; } = number; }
            sealed class Doomed { public long Value { get; } = 1; }
            sealed class Leak(int number) { public int Number { get; } = number; }
            """);
        ProfilerView view = window.ProfilerView;
        MemoryView memory = view.MemoryPage;
        MemorySnapshot first = Snapshot(window, 1);
        window.Profiling!.Input!.WriteLine("grow");
        window.Profiling.Input!.Flush();
        Ui.PumpUntil(() => Console(window).Contains("grown", StringComparison.Ordinal), "the program to grow");
        MemorySnapshot second = Snapshot(window, 2);
        Assert.Equal([first, second], memory.Snapshots);
        Assert.Same(second, memory.Snapshot);

        Ui.ClickButton(memory, "Compare with…");
        MenuItem older = Assert.Single(memory.CompareMenu!.Items.OfType<MenuItem>());
        Assert.StartsWith("Snapshot #1", (string?)older.Header, StringComparison.Ordinal);
        Ui.Click(older, settle: false);
        memory.CompareMenu.Hide();
        Ui.WaitFor(() => memory.Comparison is not null, "the comparison");
        Assert.Equal("Snapshot #2 › Comparison of Snapshot #1 to Snapshot #2", memory.Trail);
        Assert.Null(memory.Comparison!.Inexact);

        // The new objects grew the most, so they lead; the doomed died; the survivors, moved, are the same objects.
        TypeDelta leaks = memory.DeltaRows[0];
        Assert.Equal(("Leak", 4000, 4000, 0, 0), (leaks.Name, leaks.ObjectsDelta, leaks.NewObjects, leaks.DeadObjects, leaks.SurvivedObjects));
        TypeDelta doomed = Assert.Single(memory.DeltaRows, t => t.Name == "Doomed");
        Assert.Equal((-300, 0, 300, 0), (doomed.ObjectsDelta, doomed.NewObjects, doomed.DeadObjects, doomed.SurvivedObjects));
        TypeDelta survivors = Assert.Single(memory.DeltaRows, t => t.Name == "Survivor");
        Assert.Equal((0, 0, 0, 500), (survivors.ObjectsDelta, survivors.NewObjects, survivors.DeadObjects, survivors.SurvivedObjects));
        Assert.Equal(survivors.SurvivedBytes, first.Types.Single(t => t.Name == "Survivor").Bytes);

        memory.Choose(survivors);
        Ui.ClickButton(memory, "Open Survived Objects");
        Ui.WaitFor(() => memory.Set is not null, "the survived objects");
        Assert.Equal("Survived objects of the type Survivor", memory.Set!.Title);
        HashSet<ulong> before = [.. Ui.Await(Waiting.Apart(() => first.ObjectsOf(first.Types.Single(t => t.Name == "Survivor")).Objects.Select(o => o.Address).ToList()), "the first snapshot's survivors")];
        int moved = memory.ObjectRows.Count(o => !before.Contains(o.Address));
        Assert.Equal(500, memory.ObjectRows.Count);
        Assert.True(moved > 250, $"Only {moved} of the 500 survivors moved: the compacting collection was to move them, so their being survived proves nothing");

        Button back = memory.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Comparison of Snapshot #1 to Snapshot #2");
        Ui.Click(back);
        Assert.NotNull(memory.Comparison);
        memory.Choose(memory.DeltaRows[0]);
        Ui.ClickButton(memory, "Open New Objects");
        Ui.WaitFor(() => memory.Set is not null, "the new objects");
        Assert.Equal("Snapshot #2 › Comparison of Snapshot #1 to Snapshot #2 › New objects of the type Leak", memory.Trail);
        Assert.Same(second, memory.Set!.Snapshot);
        Assert.Equal(4000, memory.ObjectRows.Count);
        Assert.All(memory.ObjectRows, o => Assert.Equal("Leak", o.Type));
        Assert.Equal(4000, memory.ObjectRows.Select(o => o.Address).Distinct().Count());

        back = memory.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Comparison of Snapshot #1 to Snapshot #2");
        Ui.Click(back);
        memory.Choose(doomed);
        Ui.ClickButton(memory, "Open Dead Objects");
        Ui.WaitFor(() => memory.Set is not null, "the dead objects");
        Assert.Equal("Dead objects of the type Doomed", memory.Set!.Title);
        Assert.Same(first, memory.Set.Snapshot);
        Assert.Equal(300, memory.ObjectRows.Count);
        window.Profiling.Kill();
        window.Close();
    }

    [AvaloniaFact]
    public void A_types_objects_open_to_their_shortest_paths_from_the_roots_with_each_roots_kind_and_what_they_retain()
    {
        // A thousand entries a static field keeps through its list, and one only a GC handle does.
        MainWindow window = Profiled("Cacher", """
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            Cache.Fill(1000);
            GCHandle handle = Cache.Hold();
            Console.WriteLine("ready");
            Console.ReadLine();
            handle.Free();
            static class Cache
            {
                public static readonly List<Entry> Entries = [];

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static void Fill(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Entries.Add(new Entry($"entry {i}", new byte[64]));
                    }
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static GCHandle Hold() => GCHandle.Alloc(new Entry("held", new byte[64]));
            }
            sealed class Entry(string name, byte[] data)
            {
                public string Name { get; } = name;
                public byte[] Data { get; } = data;
            }
            """);
        MemoryView memory = window.ProfilerView.MemoryPage;
        Snapshot(window, 1);
        HeapType entries = Assert.Single(memory.TypeRows, t => t.Name == "Entry");
        Assert.Equal(1001, entries.Count);
        Assert.True(entries.RetainedBytes > entries.Bytes, "The entries' retained bytes leave out the names and data only they hold");

        Focus(memory.TypeList, entries);
        Ui.Press(window, "enter");
        Ui.WaitFor(() => memory.Set is not null, "the entries");
        ObjectSet set = memory.Set!;
        Assert.Equal(("Entry", 1001), (set.Title, set.Count));
        Assert.Equal(set.RetainedBytes, memory.ObjectRows.Sum(o => o.RetainedBytes));
        Assert.All(memory.ObjectRows, o => Assert.True(o.RetainedBytes >= o.Bytes + 64, $"An entry retains {o.RetainedBytes} bytes, less than itself and its data"));
        ulong held = memory.ObjectRows.Single(o => set.Snapshot.ObjectAt(o.Address)!.ShortestPathsToRoots().Children[0].Root?.Kind == RootKind.Handle).Address;

        Ui.ClickButton(memory, "Shortest Paths to Roots");
        Assert.Equal(1, memory.SetTab);
        string[] shown = [.. memory.PathRows.Select(r => $"{new string(' ', r.Depth)}{r.Node.Text} {r.Node.Objects}")];
        Assert.Equal(
            [
                "Entry 1001",
                " Entry[] elements 1000",
                "  System.Collections.Generic.List<Entry>._items 1000",
                "   Static reference: Cache.Entries 1000",
                " Handle 1",
            ],
            shown);
        RetentionRow staticRoot = memory.PathRows[3];
        Assert.Equal(RootKind.StaticReference, staticRoot.Node.Root?.Kind);
        long cached = memory.ObjectRows.Where(o => o.Address != held).Sum(o => o.RetainedBytes);
        Assert.Equal(cached, staticRoot.Node.RetainedBytes);
        Assert.Equal(cached, memory.PathRows[1].Node.RetainedBytes);
        Assert.Equal(cached, memory.PathRows[2].Node.RetainedBytes);
        Assert.Equal(RootKind.Handle, memory.PathRows[4].Node.Root?.Kind);
        TreeKeysCheck.Press(window, memory.PathRows, memory.PathRows[0], "Shortest Paths to Roots");

        Ui.ClickButton(memory, "Key Retention Paths");
        Assert.Equal(
            [
                "Static reference: Cache.Entries → System.Collections.Generic.List<Entry>._items → Entry[] elements → Entry",
                "Handle → Entry",
            ],
            memory.KeyPathRows.Select(p => p.Text));
        Assert.Equal((1000, cached), (memory.KeyPathRows[0].Objects, memory.KeyPathRows[0].RetainedBytes));

        Ui.ClickButton(memory, "Objects");
        HeapObject one = memory.ObjectRows.First(o => o.Address != held);
        Focus(memory.ObjectList, one);
        Ui.Press(window, "enter");
        Ui.WaitFor(() => memory.Set?.Count == 1, "the entry alone");
        Assert.Equal(1, memory.SetTab);
        Assert.Equal($"Entry at 0x{one.Address:x}", memory.Set!.Title);
        Assert.Matches(@"^Entry\[\] at \[\d+\]$", memory.PathRows[1].Node.Text);
        Assert.Equal("Static reference: Cache.Entries", memory.PathRows[^1].Node.Text);
        Assert.Equal(one.RetainedBytes, memory.Set.RetainedBytes);
        window.Profiling!.Kill();
        window.Close();
    }

    /// <summary>What a ConditionalWeakTable keeps alive for a key whose type holds no references (a string) is kept through
    /// the key: a 10 MB array listed, its shortest path from the static that holds the key, and the key's strings retaining it.</summary>
    [AvaloniaFact]
    public void A_conditional_weak_tables_value_is_retained_through_its_key_though_the_key_holds_no_references()
    {
        MainWindow window = Profiled("Attacher", """
            using System.Runtime.CompilerServices;
            Attached.Fill();
            Console.WriteLine("ready");
            Console.ReadLine();
            static class Attached
            {
                public static readonly string Key = new('k', 16);
                private static readonly ConditionalWeakTable<string, byte[]> table = new();

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static void Fill() => table.Add(Key, new byte[10_000_000]);
            }
            """);
        MemoryView memory = window.ProfilerView.MemoryPage;
        Snapshot(window, 1);
        HeapType arrays = Assert.Single(memory.TypeRows, t => t.Name == "System.Byte[]");
        Assert.True(arrays.Bytes >= 10_000_000, $"The 10 MB array the table keeps for its key is not among the live objects: the byte arrays take {arrays.Bytes:N0} bytes");
        HeapType strings = Assert.Single(memory.TypeRows, t => t.Name == "System.String");
        Assert.True(strings.RetainedBytes >= 10_000_000, $"The strings retain {strings.RetainedBytes:N0} bytes, not the key's 10 MB array");

        Focus(memory.TypeList, arrays);
        Ui.Press(window, "enter");
        Ui.WaitFor(() => memory.Set is not null, "the byte arrays");
        HeapObject payload = memory.ObjectRows[0];
        Assert.True(payload.Bytes >= 10_000_000, $"The largest byte array takes {payload.Bytes:N0} bytes");
        Focus(memory.ObjectList, payload);
        Ui.Press(window, "enter");
        Ui.WaitFor(() => memory.Set?.Count == 1, "the array alone");
        Assert.Equal(1, memory.SetTab);
        Assert.Equal(["System.String.dependent handle", "Static reference: Attached.Key"], memory.PathRows.Skip(1).Select(r => r.Node.Text));
        window.Profiling!.Kill();
        window.Close();
    }

    [AvaloniaFact]
    public void A_heap_of_a_million_objects_is_read_compared_and_opened()
    {
        MainWindow window = Profiled("Million", """
            using System.Runtime.CompilerServices;
            Heap.Add(1_000_000);
            Console.WriteLine("ready");
            while (Console.ReadLine() is { } line)
            {
                Heap.Add(200_000);
                Console.WriteLine("added");
            }
            static class Heap
            {
                public static readonly List<Node> Nodes = [];

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static void Add(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Nodes.Add(new Node(i));
                    }
                }
            }
            sealed class Node(int number) { public int Number { get; } = number; }
            """);
        MemoryView memory = window.ProfilerView.MemoryPage;
        MemorySnapshot first = Snapshot(window, 1);
        window.Profiling!.Input!.WriteLine("add");
        window.Profiling.Input!.Flush();
        Ui.PumpUntil(() => Console(window).Contains("added", StringComparison.Ordinal), "the program to add its nodes");
        MemorySnapshot second = Snapshot(window, 2);
        Assert.True(second.Objects > 1_200_000, $"{second.Objects} objects in the second snapshot");

        Task compared = memory.CompareWithAsync(first);
        Ui.PumpUntil(() => memory.Comparison is not null, "the comparison", TimeSpan.FromMinutes(2));
        TypeDelta nodes = memory.DeltaRows[0];
        Assert.Equal(("Node", 200_000, 200_000, 1_000_000), (nodes.Name, nodes.ObjectsDelta, nodes.NewObjects, nodes.SurvivedObjects));

        memory.Choose(nodes);
        Ui.ClickButton(memory, "Open New Objects");
        Ui.PumpUntil(() => memory.Set is not null, "the new nodes and their paths", TimeSpan.FromMinutes(2));
        Assert.Equal(200_000, memory.ObjectRows.Count);
        Assert.Equal("Static reference: Heap.Nodes", memory.PathRows[^1].Node.Text);
        Assert.True(compared.IsCompletedSuccessfully);

        TestContext.Current.TestOutputHelper?.WriteLine($"Snapshot #1, {first.Objects:N0} objects: {first.Phases}; snapshot #2, {second.Objects:N0} objects: {second.Phases}");
        window.Profiling.Kill();
        window.Close();
    }

    [AvaloniaFact]
    public void A_memory_snapshot_of_a_program_on_dotnet_11_lists_its_objects_and_the_static_that_keeps_them()
    {
        MainWindow window = Profiled("Eleven", """
            using System.Runtime.CompilerServices;
            Kept.Fill(100);
            Console.WriteLine("ready");
            Console.ReadLine();
            static class Kept
            {
                public static readonly List<Item> Items = [];

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static void Fill(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Items.Add(new Item(i));
                    }
                }
            }
            sealed class Item(int number) { public int Number { get; } = number; }
            """, "net11.0");
        MemoryView memory = window.ProfilerView.MemoryPage;
        Snapshot(window, 1);
        HeapType items = Assert.Single(memory.TypeRows, t => t.Name == "Item");
        Assert.Equal(100, items.Count);
        Focus(memory.TypeList, items);
        Ui.Press(window, "enter");
        Ui.WaitFor(() => memory.Set is not null, "the items");
        Ui.ClickButton(memory, "Shortest Paths to Roots");
        Assert.Equal("Static reference: Kept.Items", memory.PathRows[^1].Node.Text);
        Assert.False(window.Profiling!.HasExited, "The program profiled ended with its memory snapshot");
        window.Profiling.Kill();
        window.Close();
    }

    [AvaloniaFact]
    public void A_memory_snapshot_writes_nothing_into_the_output_of_a_program_that_echoes_its_input_byte_for_byte()
    {
        // A program whose output is its input, as a language or MCP server's output is its protocol's alone.
        MainWindow window = Profiled("Echo", """
            Stream input = Console.OpenStandardInput();
            Stream output = Console.OpenStandardOutput();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                output.Flush();
            }
            """, sent: "ready\n");
        string before = Console(window);
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(false);
        };
        Ui.ClickButton(window.ProfilerView, "Get Memory Snapshot");
        // Bounded, so a createdump that never finishes fails here, not the run.
        Ui.PumpUntil(() => window.ProfilerView.Memory is not null || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true,
            "the memory snapshot", TimeSpan.FromMinutes(2));
        if (asked.Count == 0)
        {
            Assert.True(window.ProfilerView.Memory?.Number == 1, window.Status);
        }
        else
        {
            // Only where Passenger may not read a program it did not start (Linux's ptrace_scope) is it asked first.
            Assert.True(OperatingSystem.IsLinux(), $"Passenger asked before a memory snapshot, saying: {asked[0]}");
            Assert.Contains("createdump", asked[0], StringComparison.Ordinal);
            Assert.Null(window.ProfilerView.Memory);
        }
        window.Profiling!.Input!.Write("after\n");
        window.Profiling.Input.Flush();
        Ui.PumpUntil(() => Console(window).EndsWith("after\n", StringComparison.Ordinal), "the program to echo what it was sent after the snapshot");
        Assert.Equal(before + "after\n", Console(window));
        window.Profiling.Kill();
        window.Close();
    }

    /// <summary>Past three snapshots the oldest lets go of its objects, its row and its page saying so and a comparison with
    /// it saying why there is none, while the rest compare as before; Delete removes a snapshot, its objects let go at once,
    /// and the folder's end lets go of the others.</summary>
    [AvaloniaFact]
    public void Past_three_snapshots_the_oldest_lets_go_of_its_objects_which_a_comparison_names_and_Delete_and_the_folders_end_let_go_of_the_rest()
    {
        MainWindow window = Profiled("Keeper", """
            Console.WriteLine("ready");
            Console.ReadLine();
            """);
        MemoryView memory = window.ProfilerView.MemoryPage;
        MemorySnapshot first = Snapshot(window, 1);
        MemorySnapshot second = Snapshot(window, 2);
        MemorySnapshot third = Snapshot(window, 3);
        Assert.All(memory.SnapshotRows, r => Assert.Null(r.Gone));
        MemorySnapshot fourth = Snapshot(window, 4);

        const string Gone = "Snapshot #1's objects were let go as Snapshot #4 was taken: a run keeps those of its newest 3 snapshots";
        Assert.Equal([first, second, third, fourth], memory.Snapshots);
        Assert.Equal([Gone, null, null, null], memory.SnapshotRows.Select(r => r.Gone));
        Assert.Single(memory.SnapshotList.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "objects let go" && t.IsEffectivelyVisible);
        Assert.Same(fourth, memory.Snapshot);

        Ui.ClickButton(memory, "Compare with…");
        MenuItem oldest = memory.CompareMenu!.Items.OfType<MenuItem>().First();
        Assert.StartsWith("Snapshot #1", (string?)oldest.Header, StringComparison.Ordinal);
        Ui.Click(oldest, settle: false);
        memory.CompareMenu.Hide();
        Ui.WaitFor(() => memory.Summary == Gone, () => $"the comparison with the oldest to say why there is none, not '{memory.Summary}'");
        Assert.Null(memory.Comparison);
        Ui.Await(memory.CompareWithAsync(second), "the comparison with the second");
        Assert.Equal("Snapshot #4 › Comparison of Snapshot #2 to Snapshot #4", memory.Trail);

        // The oldest's page lists its types and says its objects no longer open, as Enter on one finds.
        Focus(memory.SnapshotList, memory.SnapshotRows[0]);
        Assert.Same(first, memory.Snapshot);
        Assert.EndsWith($". {Gone}: its types are listed, its objects no longer open", memory.Summary, StringComparison.Ordinal);
        Focus(memory.TypeList, memory.TypeRows[0]);
        Ui.Press(window, "enter");
        Ui.WaitFor(() => memory.Summary == Gone, () => $"the oldest's objects to be said let go, not '{memory.Summary}'");
        Assert.Null(memory.Set);

        Focus(memory.SnapshotList, memory.SnapshotRows[2]);
        Ui.Press(window, "delete");
        Assert.Equal([first, second, fourth], memory.Snapshots);
        Assert.Equal("Snapshot #3 was removed", third.Gone);
        Assert.Same(fourth, memory.Snapshot);

        Assert.Null(window.CloseSolution());
        Assert.Empty(memory.Snapshots);
        Assert.Null(window.ProfilerView.Memory);
        Assert.All([second, fourth], s => Assert.NotNull(s.Gone));
        window.Close();
    }

    /// <summary>A memory snapshot of a program that ends as it is taken says the program ended, not what failed as it went.</summary>
    [AvaloniaFact]
    public void A_memory_snapshot_of_a_program_that_ends_as_it_is_taken_says_the_program_ended()
    {
        MainWindow window = Profiled("Ender", """
            Console.WriteLine("ready");
            Console.ReadLine();
            """);
        Task<(MemorySnapshot? Heap, string? Note)> taking = window.MemorySnapshotUnaskedAsync(CancellationToken.None);
        window.Profiling!.Kill();
        (MemorySnapshot? heap, string? note) = Ui.Await(taking, "the memory snapshot of the program that ended", TimeSpan.FromMinutes(1));
        Assert.Null(heap);
        Assert.Equal("Ender ended before its memory snapshot", note);
        window.Close();
    }

    /// <summary>An agent's memory snapshot given up while its dump is on disk leaves none there once the dump's writer and
    /// reader have ended; the program goes on, and the next snapshot is taken as before.</summary>
    [AvaloniaFact]
    public void An_agents_memory_snapshot_given_up_as_its_dump_is_written_leaves_no_dump_behind()
    {
        MainWindow window = Profiled("Quitter", """
            Console.WriteLine("ready");
            Console.ReadLine();
            """);
        int program = window.Profiling!.ProcessId;
        using CancellationTokenSource giveUp = new();
        Task<(MemorySnapshot? Heap, string? Note)> taking = window.MemorySnapshotUnaskedAsync(giveUp.Token);
        Ui.PumpUntil(() => Dumps(program).Length > 0 || taking.IsCompleted, "the snapshot's dump to be written", TimeSpan.FromMinutes(1));
        Assert.False(taking.IsCompleted, "The snapshot was over before its dump was seen on disk");
        giveUp.Cancel();
        (MemorySnapshot? heap, string? note) = Ui.Await(taking, "the snapshot given up", TimeSpan.FromMinutes(1));
        Assert.Null(heap);
        Assert.Equal("it was stopped", note);
        Ui.PumpUntil(() => Dumps(program).Length == 0, "the given-up snapshot's dump to be deleted", TimeSpan.FromMinutes(1));
        Assert.False(window.Profiling.HasExited, "The program ended with its snapshot given up");
        Snapshot(window, 1);
        window.Profiling.Kill();
        window.Close();
    }

    /// <summary>This process's dumps of a program in the temporary folder.</summary>
    private static string[] Dumps(int program) => Directory.GetFiles(Path.GetTempPath(), $"passenger-heap-{Environment.ProcessId}-{program}-*.dmp");

    /// <summary>A console project, built and started under the profiler from the window, run until it says it is ready
    /// (having been <paramref name="sent"/> a line to say back, for a program that says nothing by itself).</summary>
    private MainWindow Profiled(string name, string program, string framework = TestProjects.Framework, string? sent = null)
    {
        string project = Path.Combine(folder, name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, $"{name}.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>{framework}</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(project, "Program.cs"), program);
        Restore.Run(project);
        MainWindow window = Ui.OpenFolder(project, width: 1400, height: 900, languageServer: false);
        Task<string?> started = window.ProfileAsync();
        Ui.Launched(window, started, "the build and the start", TimeSpan.FromMinutes(3));
        if (sent is not null)
        {
            window.Profiling!.Input!.Write(sent);
            window.Profiling.Input.Flush();
        }
        Ui.PumpUntil(() => Console(window).Contains("ready", StringComparison.Ordinal), "the program to be ready");
        return window;
    }

    /// <summary>Get Memory Snapshot pressed, and its snapshot listed.</summary>
    private static MemorySnapshot Snapshot(MainWindow window, int number)
    {
        // Where only the runtime can write it, into the program's output (Linux's ptrace_scope), that is let.
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ClickButton(window.ProfilerView, "Get Memory Snapshot");
        // Bounded, so a createdump that never finishes fails here, not the run.
        Ui.PumpUntil(() => window.ProfilerView.Memory?.Number == number || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true,
            $"memory snapshot #{number}", TimeSpan.FromMinutes(2));
        Assert.True(window.ProfilerView.Memory?.Number == number, window.Status);
        Ui.Settle();
        return window.ProfilerView.Memory!;
    }

    /// <summary>A row chosen and given the keys, as a click on it gives them.</summary>
    private static void Focus(ListBox list, object row)
    {
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        Ui.Settle();
        Assert.True(list.ContainerFromItem(row)?.Focus() == true, "The chosen row could not take the keys");
    }

    private static string Console(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
