using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Profiling;
using Passenger.Profiler;

namespace Passenger.Tests;

/// <summary>dotMemory's memory snapshots, from the profiler window: two snapshots of a run compared object by object (new,
/// dead and survived, however the GC moved what lived on), a type's objects opened to the shortest paths that keep them
/// from the GC roots with each root's kind and what they retain, and a heap of a million objects read, compared and opened
/// while the window goes on answering.</summary>
[Collection(Alone.Name)]
public sealed class MemorySnapshotsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("memory-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Two_snapshots_compared_show_the_objects_made_and_collected_between_them_and_those_the_GC_moved_as_survived()
    {
        // Survivors with garbage between them, so a compacting collection moves them; then, asked to grow, 4,000 new
        // objects held, the doomed ones let go, and collections young and full.
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

        // Compare with… on the newer: the older chosen from its menu.
        Click(memory, "Compare with…");
        MenuItem older = Assert.Single(memory.CompareMenu!.Items.OfType<MenuItem>());
        Assert.StartsWith("Snapshot #1", (string?)older.Header, StringComparison.Ordinal);
        older.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
        Click(memory, "Open Survived Objects");
        Ui.WaitFor(() => memory.Set is not null, "the survived objects");
        Assert.Equal("Survived objects of the type Survivor", memory.Set!.Title);
        HashSet<ulong> before = [.. Ui.Await(Task.Run(() => first.ObjectsOf(first.Types.Single(t => t.Name == "Survivor")).Objects.Select(o => o.Address).ToList()), "the first snapshot's survivors")];
        int moved = memory.ObjectRows.Count(o => !before.Contains(o.Address));
        Assert.Equal(500, memory.ObjectRows.Count);
        Assert.True(moved > 250, $"Only {moved} of the 500 survivors moved: the compacting collection was to move them, so their being survived proves nothing");

        // Back along the trail, the new objects of the type that grew opened: each one listed, in the newer snapshot.
        Button back = memory.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Comparison of Snapshot #1 to Snapshot #2");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
        Assert.NotNull(memory.Comparison);
        memory.Choose(memory.DeltaRows[0]);
        Click(memory, "Open New Objects");
        Ui.WaitFor(() => memory.Set is not null, "the new objects");
        Assert.Equal("Snapshot #2 › Comparison of Snapshot #1 to Snapshot #2 › New objects of the type Leak", memory.Trail);
        Assert.Same(second, memory.Set!.Snapshot);
        Assert.Equal(4000, memory.ObjectRows.Count);
        Assert.All(memory.ObjectRows, o => Assert.Equal("Leak", o.Type));
        Assert.Equal(4000, memory.ObjectRows.Select(o => o.Address).Distinct().Count());

        // Its dead objects are the older snapshot's.
        back = memory.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Comparison of Snapshot #1 to Snapshot #2");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
        memory.Choose(doomed);
        Click(memory, "Open Dead Objects");
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

        // The type opened, as a double-click or Enter opens it.
        Focus(memory.TypeList, entries);
        Ui.Press(window, "enter");
        Ui.WaitFor(() => memory.Set is not null, "the entries");
        ObjectSet set = memory.Set!;
        Assert.Equal(("Entry", 1001), (set.Title, set.Count));
        Assert.Equal(set.RetainedBytes, memory.ObjectRows.Sum(o => o.RetainedBytes));
        Assert.All(memory.ObjectRows, o => Assert.True(o.RetainedBytes >= o.Bytes + 64, $"An entry retains {o.RetainedBytes} bytes, less than itself and its data"));
        ulong held = memory.ObjectRows.Single(o => set.Snapshot.ObjectAt(o.Address)!.ShortestPathsToRoots().Children[0].Root?.Kind == RootKind.Handle).Address;

        // Shortest Paths to Roots: the static field's, open from the entries down to it through what holds them.
        Click(memory, "Shortest Paths to Roots");
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

        // Key Retention Paths: each from its root, the most retaining first.
        Click(memory, "Key Retention Paths");
        Assert.Equal(
            [
                "Static reference: Cache.Entries → System.Collections.Generic.List<Entry>._items → Entry[] elements → Entry",
                "Handle → Entry",
            ],
            memory.KeyPathRows.Select(p => p.Text));
        Assert.Equal((1000, cached), (memory.KeyPathRows[0].Objects, memory.KeyPathRows[0].RetainedBytes));

        // One entry opened alone: its own path, the element of the array it is.
        Click(memory, "Objects");
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_heap_of_a_million_objects_is_read_compared_and_opened_while_the_window_goes_on_answering(bool light)
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
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.SetRenderScaling(2);
        MemoryView memory = window.ProfilerView.MemoryPage;
        TimeSpan stall = TimeSpan.Zero;
        Stopwatch clock = Stopwatch.StartNew();
        MemorySnapshot first = Snapshot(window, 1, s => stall = s > stall ? s : stall);
        TimeSpan firstTook = clock.Elapsed;
        window.Profiling!.Input!.WriteLine("add");
        window.Profiling.Input!.Flush();
        Ui.PumpUntil(() => Console(window).Contains("added", StringComparison.Ordinal), "the program to add its nodes");
        clock.Restart();
        MemorySnapshot second = Snapshot(window, 2, s => stall = s > stall ? s : stall);
        TimeSpan secondTook = clock.Elapsed;
        Assert.True(second.Objects > 1_200_000, $"{second.Objects} objects in the second snapshot");

        window.Width = 640;
        Ui.Settle();
        clock.Restart();
        bool comparingShown = false;
        Task comparing = memory.CompareWithAsync(first);
        Responsive(() => memory.Comparison is not null, "the comparison", s => stall = s > stall ? s : stall,
            () => Busy("comparing", "Comparing snapshots…", ref comparingShown));
        TimeSpan compared = clock.Elapsed;
        TypeDelta nodes = memory.DeltaRows[0];
        Assert.Equal(("Node", 200_000, 200_000, 1_000_000), (nodes.Name, nodes.ObjectsDelta, nodes.NewObjects, nodes.SurvivedObjects));
        Audit("million-comparison", memory.DeltaList);

        clock.Restart();
        bool pathsShown = false;
        memory.Choose(nodes);
        Button open = Assert.Single(memory.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Open New Objects"));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Responsive(() => memory.Set is not null, "the new nodes and their paths", s => stall = s > stall ? s : stall,
            () => Busy("paths", "Building the shortest paths to roots…", ref pathsShown));
        TimeSpan opened = clock.Elapsed;
        Assert.Equal(200_000, memory.ObjectRows.Count);
        Assert.Equal("Static reference: Heap.Nodes", memory.PathRows[^1].Node.Text);
        Assert.True(comparing.IsCompletedSuccessfully);
        Audit("million-objects", memory.ObjectList);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Snapshot #1, {first.Objects:N0} objects: {firstTook.TotalSeconds:0.0} s ({first.Phases}); "
            + $"snapshot #2, {second.Objects:N0} objects: {secondTook.TotalSeconds:0.0} s ({second.Phases}); "
            + $"compared in {compared.TotalMilliseconds:N0} ms; 200,000 new nodes and their paths opened in {opened.TotalMilliseconds:N0} ms; "
            + $"the UI thread held at most {stall.TotalMilliseconds:N0} ms at a time; comparison busy frame observed={comparingShown}, paths busy frame observed={pathsShown}");
        Assert.True(stall < TimeSpan.FromMilliseconds(500), $"The window stopped answering for {stall.TotalMilliseconds:N0} ms while a million objects were read and opened");
        window.Profiling.Kill();
        window.Close();

        void Busy(string phase, string message, ref bool observed)
        {
            if (!observed && memory.Waiting == message)
            {
                UiAudit.Capture(window, $"profiler-memory-phase-{phase}-640-{(light ? "light" : "dark")}");
                Assert.Empty(UiAudit.HorizontalOverflow(window, $"Memory {phase}", memory));
                observed = memory.Waiting == message;
            }
        }

        void Audit(string page, ListBox list)
        {
            List<string> problems = [];
            object? selected = list.SelectedItem;
            foreach (double width in new double[] { 1200, 683, 640 })
            {
                window.Width = width;
                Ui.Settle();
                Assert.Equal(selected, list.SelectedItem);
                UiAudit.Capture(window, $"profiler-memory-{page}-{width:0}-{(light ? "light" : "dark")}");
                problems.AddRange(UiAudit.HorizontalOverflow(window, page, memory));
                if (list.Bounds.Height < 22)
                {
                    problems.Add($"Memory {page}: its summary consumed the table's vertical reading viewport at {width}px");
                }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>A console project, built and started under the profiler from the window, run until it says it is ready.</summary>
    private MainWindow Profiled(string name, string program)
    {
        string project = Path.Combine(folder, name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, $"{name}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(project, "Program.cs"), program);
        Restore.Run(project);
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(project);
        Ui.Settle();
        Task<string?> started = window.ProfileAsync();
        Ui.WaitFor(() => started.IsCompleted, "the build and the start", TimeSpan.FromMinutes(3));
        Assert.Null(started.Result);
        Ui.PumpUntil(() => Console(window).Contains("ready", StringComparison.Ordinal), "the program to be ready");
        return window;
    }

    /// <summary>Get Memory Snapshot pressed, and its snapshot listed, the window answering meanwhile.</summary>
    private static MemorySnapshot Snapshot(MainWindow window, int number, Action<TimeSpan>? stalled = null)
    {
        Click(window.ProfilerView, "Get Memory Snapshot");
        // Bounded, so a createdump that never finishes fails here, not the run.
        Responsive(() => window.ProfilerView.Memory?.Number == number || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true,
            $"memory snapshot #{number}", stalled ?? (_ => { }));
        Assert.True(window.ProfilerView.Memory?.Number == number, window.Status);
        Ui.Settle();
        return window.ProfilerView.Memory!;
    }

    /// <summary>Waits for something while background work goes on, the window's own work given its turns as a person's
    /// clicks would be: how long each turn took past what it was given is how long the window did not answer.</summary>
    private static void Responsive(Func<bool> done, string what, Action<TimeSpan> stalled, Action? during = null)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        TimeSpan turn = TimeSpan.FromMilliseconds(20);
        Stopwatch each = new();
        while (!done())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Waited 120 s for {what}");
            }
            each.Restart();
            Screenshot.Pump(turn);
            stalled(each.Elapsed - turn);
            during?.Invoke();
        }
    }

    private static void Click(Avalonia.Visual within, string label)
    {
        Control found = within.GetVisualDescendants().OfType<Control>()
            .First(c => c is Button { Content: string text } && text == label || c is ToggleButton { Content: string tab } && tab == label);
        found.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
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
