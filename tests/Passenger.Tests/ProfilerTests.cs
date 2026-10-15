using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Profiling;
using Passenger.App.Run;
using Passenger.Profiler;

namespace Passenger.Tests;

/// <summary>The profiler as dotTrace's sampling, from the window: a configuration profiled is caught from its first
/// instruction to its end, its busy method on the main thread's hot path, its allocations and collections listed, and a
/// .NET program it starts runs free rather than held at the profiler's port; one that never reaches the profiler is said
/// to, and a running one's heap is counted by type.</summary>
[Collection(Alone.Name)]
public sealed class ProfilerTests : IDisposable
{
    private readonly string folder = TestFolders.Create("profile-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_completed_profile_explains_missing_memory_and_keeps_long_rows_reachable_through_scrolling_and_resize(bool light)
    {
        const string space = "Example.Production.Analytics.Sampling.With.Deliberately.Long.Names.For.Readable.Profiler.Results";
        const string typeName = "ATypeAllocatedByTheSampleWhoseCompleteNameMustRemainAccessibleAtNarrowWidths";
        const string methodName = "KeepCalculatingAResultWhileSamplingSoTheWholeMethodNameIsReachableFromTheProfiler";
        string project = Project("LongRows", """
            using System.Diagnostics;
            using System.Runtime.CompilerServices;
            using LongNamespace;
            Console.WriteLine($"ready {Work.LongMethod(TimeSpan.FromSeconds(1))}");
            while (!File.Exists("finish")) { Thread.Sleep(10); }
            namespace LongNamespace
            {
                sealed class PayloadType(long value)
                {
                    public long A = value, B = value, C = value, D = value;
                }
                static class Work
                {
                    private static readonly List<PayloadType> Kept = [];
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    public static double LongMethod(TimeSpan time)
                    {
                        for (int i = 0; i < 100_000; i++) { Kept.Add(new PayloadType(i)); }
                        Stopwatch clock = Stopwatch.StartNew();
                        double total = 0;
                        while (clock.Elapsed < time)
                        {
                            for (int i = 1; i < 10000; i++) { total += Math.Sqrt(i); }
                        }
                        return total;
                    }
                }
            }
            """.Replace("LongNamespace", space, StringComparison.Ordinal).Replace("PayloadType", typeName, StringComparison.Ordinal)
                .Replace("LongMethod", methodName, StringComparison.Ordinal));
        MainWindow window = new() { Width = 1200, Height = 900, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(project);
        window.OpenFile(Path.Combine(project, "Program.cs"));
        Ui.Settle();
        window.SetConfigurations([new RunConfiguration { Name = "Long-name sampling", Project = Path.Combine(project, "LongRows.csproj"), WorkingDirectory = project }], "Long-name sampling");
        Assert.Equal(project, window.Configuration?.WorkingDirectory);
        try
        {
            Assert.Null(Ui.Await(window.ProfileAsync(), "the real long-name program to start", TimeSpan.FromMinutes(3)));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("ready ", StringComparison.Ordinal), "the sampled workload to finish");
            File.WriteAllText(Path.Combine(project, "finish"), string.Empty);
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, "the completed sampling snapshot", TimeSpan.FromMinutes(2));
            Ui.Settle();
            ProfilerView view = window.ProfilerView;
            Page("Memory");
            foreach (double width in new double[] { 1200, 683, 640 })
            {
                window.Width = width;
                Ui.Settle();
                Assert.Equal("No memory snapshot was taken. Profile a configuration and choose Get Memory Snapshot while it runs.", view.Placeholder);
                Assert.False(view.Timeline.IsEffectivelyVisible, "The sampling timeline should not describe an absent heap snapshot");
                UiAudit.Capture(window, $"profiler-memory-not-taken-{width:0}-{(light ? "light" : "dark")}");
            }
            foreach ((string page, string listName) in new[] { ("Call Tree", "ProfilerCallTree"), ("Hot Spots", "ProfilerHotSpots"), ("Allocations", "ProfilerAllocatedTypes") })
            {
                window.Width = 1200;
                Page(page);
                ListBox list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), box => box.Name == listName);
                object item = Assert.Single(list.Items.Cast<object>(), value => value switch
                {
                    TreeRow row => row.Node.Name.Contains(methodName, StringComparison.Ordinal),
                    HotSpot spot => spot.Name.Contains(methodName, StringComparison.Ordinal),
                    AllocatedType type => type.Name == space + "." + typeName,
                    _ => false,
                });
                list.ScrollIntoView(list.Items[0]!);
                Ui.Settle();
                Assert.True(list.ContainerFromIndex(0)?.Focus() == true, $"The first {page} row could not take the keys");
                Ui.Press(window, "home");
                for (int position = 0; position < list.Items.IndexOf(item); position++)
                {
                    Ui.Press(window, "down");
                }
                Assert.Same(item, list.SelectedItem);
                Control row = list.ContainerFromItem(item)!;
                Assert.True(row.Focus());
                object? focus = window.FocusManager?.GetFocusedElement();
                ScrollViewer scroll = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
                foreach (double width in new double[] { 1200, 683, 640, 1200 })
                {
                    window.Width = width;
                    Ui.Settle();
                    Assert.Same(item, list.SelectedItem);
                    Assert.Same(focus, window.FocusManager?.GetFocusedElement());
                    Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 40, $"The complete {page} row was clipped instead of measured for horizontal scrolling");
                    Point over = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;
                    for (int attempt = 0; attempt < 20 && scroll.Offset.X < scroll.Extent.Width - scroll.Viewport.Width - 1; attempt++)
                    {
                        window.MouseMove(over);
                        window.MouseWheel(over, new Vector(-30, 0));
                        Ui.Settle();
                    }
                    Assert.True(scroll.Offset.X > 0, $"The {page} row cannot be read by scrolling horizontally");
                    Assert.InRange(scroll.Offset.X, scroll.Extent.Width - scroll.Viewport.Width - 1, scroll.Extent.Width - scroll.Viewport.Width + 1);
                    UiAudit.Capture(window, $"profiler-long-{page}-{width:0}-{(light ? "light" : "dark")}");
                }
                int index = list.SelectedIndex;
                Ui.Press(window, index == 0 ? "down" : "up");
                Assert.NotSame(item, list.SelectedItem);
                Ui.Press(window, index == 0 ? "up" : "down");
                Assert.Same(item, list.SelectedItem);
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(project, "finish"), string.Empty);
            window.Close();
        }

        void Page(string name)
        {
            ToggleButton button = Assert.Single(window.ProfilerView.GetVisualDescendants().OfType<ToggleButton>(), button => Equals(button.Content, name));
            button.BringIntoView();
            Ui.Settle();
            Point point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Ui.Settle();
            Assert.True(button.IsChecked == true);
        }
    }

    [AvaloniaFact]
    public void Profile_builds_and_runs_the_configuration_and_its_end_shows_the_snapshot()
    {
        // Twenty threads: the main one spins, the others wait for it all along. Then 60 MB of short-lived arrays, and a
        // .NET program of its own, which would wait for ever at the profiler's port if it took it from its parent.
        string project = Project("Spinner", """
            using System.Diagnostics;
            if (args is ["child"])
            {
                Console.WriteLine("child ran");
                return;
            }
            using ManualResetEventSlim spun = new();
            Thread[] waiting = [.. Enumerable.Range(0, 19).Select(i => new Thread(() => Work.Wait(spun)))];
            foreach (Thread thread in waiting) { thread.Start(); }
            Console.WriteLine($"spun {Work.Spin(TimeSpan.FromMilliseconds(1000)) > 0}");
            spun.Set();
            foreach (Thread thread in waiting) { thread.Join(); }
            Console.WriteLine($"allocated {Work.Allocate(60_000)}");
            using Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [typeof(Work).Assembly.Location, "child"]) { RedirectStandardOutput = true })!;
            Console.WriteLine($"child said: {(child.WaitForExit(15000) ? child.StandardOutput.ReadToEnd().Trim() : "nothing, held at its start")}");
            static class Work
            {
                public static long Allocate(int count)
                {
                    long made = 0;
                    for (int i = 0; i < count; i++)
                    {
                        byte[] block = new byte[1024];
                        block[i % 1024] = 1;
                        made += block.Length;
                    }
                    return made;
                }

                public static double Spin(TimeSpan time)
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    double total = 0;
                    while (clock.Elapsed < time)
                    {
                        for (int i = 1; i < 10000; i++) { total += Math.Sqrt(i); }
                    }
                    return total;
                }

                [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
                public static void Wait(ManualResetEventSlim until) => until.Wait();
            }
            """);
        // The GC sizes its youngest generation from the machine's largest cache, which on CI's Windows outgrows the 60 MB.
        Directory.CreateDirectory(Path.Combine(project, "Properties"));
        File.WriteAllText(Path.Combine(project, "Properties", "launchSettings.json"),
            "{ \"profiles\": { \"Young\": { \"commandName\": \"Project\", \"environmentVariables\": { \"DOTNET_GCgen0size\": \"0x400000\" } } } }");
        string settings = TestFolders.Create("settings-");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(project);
        Ui.Settle();
        // The folder's snapshots are kept with its state, as many as the cap from earlier runs, the first the oldest.
        string kept = window.SnapshotFolder;
        Assert.StartsWith(Path.Combine(settings, "workspaces", "Spinner-"), kept, StringComparison.Ordinal);
        Directory.CreateDirectory(kept);
        for (int i = 0; i < Snapshots.Kept; i++)
        {
            string earlier = Path.Combine(kept, $"Spinner-earlier-{i}.nettrace");
            File.WriteAllText(earlier, "an earlier run");
            File.SetLastWriteTimeUtc(earlier, DateTime.UtcNow.AddHours(-Snapshots.Kept + i));
        }

        Task<string?> started = window.ProfileAsync();
        Ui.WaitFor(() => started.IsCompleted, "the build and the start", TimeSpan.FromMinutes(3));
        Assert.Null(started.Result);
        Assert.True(window.DockHost.IsShown("profiler"));
        Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, "the program's end to show its snapshot", TimeSpan.FromMinutes(2));
        Ui.Settle();

        Passenger.App.Profiling.ProfilerView view = window.ProfilerView;
        Snapshot snapshot = view.Snapshot!;
        Assert.StartsWith("Main thread", snapshot.Threads[0].Name, StringComparison.Ordinal);
        Assert.Equal(snapshot.Samples, view.Flame.Root!.Total);
        // The hot path is open down the main thread: Spin is listed without a click.
        Assert.Contains(view.Rows, r => r.Node.Name.StartsWith("Work.Spin", StringComparison.Ordinal));
        Assert.StartsWith("Spinner-", Path.GetFileName(snapshot.TracePath), StringComparison.Ordinal);
        string console = window.BottomPanel.Run.Document.Snapshot.ToString();
        Assert.Contains("spun True", console, StringComparison.Ordinal);
        Assert.Contains("child said: child ran", console, StringComparison.Ordinal);

        // The arrays lead the Allocations page, Allocate first in their back traces, and the GC page has young collections.
        Assert.Equal("System.Byte[]", view.Types[0].Name);
        Assert.InRange(view.Types[0].Bytes, 30_000_000, 120_000_000);
        Assert.Contains(view.BackTraces, r => r.Node.Name.StartsWith("Work.Allocate", StringComparison.Ordinal));
        Assert.Contains("of generation 0", view.CollectionsSummary, StringComparison.Ordinal);

        // What the run holds grows with where its threads were caught, not with how often: each stack is kept once,
        // and a waiting thread's samples, all at one stack, as a handful of runs.
        Assert.True(snapshot.Stacks * 10 < snapshot.Samples, $"{snapshot.Stacks} stacks kept for {snapshot.Samples} samples");
        Assert.True(snapshot.Runs * 4 < snapshot.Samples, $"{snapshot.Runs} runs kept for {snapshot.Samples} samples");
        IReadOnlyList<CallNode> waiters = [.. snapshot.Threads.Where(t => Find(t.Children, n => n.Name.StartsWith("Work.Wait", StringComparison.Ordinal)) is not null)];
        Assert.Equal(19, waiters.Count);
        foreach (CallNode waiter in waiters)
        {
            ThreadLine line = Assert.Single(snapshot.Timeline, l => l.Name == waiter.Name);
            Assert.True(line.Runs.Count * 4 <= line.Samples, $"{waiter.Name} waited through {line.Samples} samples, kept as {line.Runs.Count} runs");
        }

        // The run is kept with the earlier ones, the oldest gone past the cap, and the status bar says where.
        Assert.Equal(kept, Path.GetDirectoryName(snapshot.TracePath));
        Assert.Equal(Snapshots.Kept, Directory.GetFiles(kept, "*.nettrace").Length);
        Assert.False(File.Exists(Path.Combine(kept, "Spinner-earlier-0.nettrace")), "the oldest snapshot was kept past the cap");
        Assert.True(File.Exists(Path.Combine(kept, "Spinner-earlier-1.nettrace")), "a snapshot within the cap was removed");
        Assert.Contains($"kept in {kept}", window.Status, StringComparison.Ordinal);
        Assert.Equal(snapshot.TracePath, view.Kept[0].Path);
        Assert.Equal(Snapshots.Kept, view.Kept.Count);
        Assert.Contains(kept, view.KeptSummary, StringComparison.Ordinal);

        // A stretch chosen on the timeline is what the other views show, worked out off the UI thread: the choice
        // takes the UI thread a sliver of what working the stretch out takes, and the stretch lands once it is done.
        (double From, double To) half = (snapshot.StartMs, snapshot.StartMs + ((snapshot.EndMs - snapshot.StartMs) / 2));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Snapshot worked = snapshot.Within(half.From, half.To);
        long working = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        view.Timeline.Select(half.From, half.To);
        long choosing = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(choosing * 4 < working, $"choosing a stretch made {choosing} bytes on the UI thread, working it out {working}");
        Assert.Same(snapshot, view.Snapshot);
        Ui.Settle();
        Assert.NotSame(snapshot, view.Snapshot);
        Assert.Same(snapshot, view.Snapshot!.Whole);
        Assert.Equal(worked.Samples, view.Snapshot.Samples);
        Assert.True(view.Snapshot.Samples < snapshot.Samples);
        Assert.Contains("ms of", view.Status, StringComparison.Ordinal);

        // Stretches chosen one after another show the last, never an earlier one landing late; a click, the whole run.
        view.Timeline.Select(snapshot.StartMs, snapshot.EndMs - 1);
        view.Timeline.Select(half.To, snapshot.EndMs);
        Ui.Settle();
        Assert.Equal(half.To, view.Snapshot.StartMs);
        view.Timeline.Select(half.From, half.To);
        view.Timeline.ClearSelection();
        Ui.Settle();
        Assert.Same(snapshot, view.Snapshot);
        window.Close();
    }

    [AvaloniaFact]
    public void A_configuration_that_ends_before_it_reaches_the_profiler_is_said_to_and_leaves_no_port_listening()
    {
        // Built, then its runtime's settings taken away: the .NET host ends at once, before any runtime starts to reach
        // the profiler's port, as a program that is not .NET does.
        string project = Project("Spinner", "Console.WriteLine(\"ran\");\n",
            "<Target Name=\"Unrunnable\" AfterTargets=\"Build\" Condition=\"'$(DesignTimeBuild)' != 'true'\"><Delete Files=\"$(TargetDir)$(TargetName).runtimeconfig.json\" /></Target>");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(project);
        Ui.Settle();
        IReadOnlyList<string> before = Ports();

        Task<string?> started = window.ProfileAsync();
        Ui.WaitFor(() => started.IsCompleted, "the build and the start", TimeSpan.FromMinutes(3));
        Assert.StartsWith("Spinner never reached the profiler: it ended first, with exit code ", started.Result, StringComparison.Ordinal);
        Assert.Null(window.Profiling);
        Assert.Equal(started.Result, window.ProfilerView.Status);
        Assert.Equal(before, Ports());
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_memory_snapshot_of_a_program_being_profiled_counts_its_heap_by_type_and_it_runs_on(bool light)
    {
        const string longType = "ACompanyWithSeveralProducts.Analytics.HeapSnapshots.ATypeWhoseLongNameMustStayReachableBesideAllTheMemoryMetricColumns";
        string project = Project("Spinner", """
            Holder.Held = Enumerable.Range(0, 40).Select(_ => new LongType()).ToArray();
            Console.WriteLine($"waiting with {Holder.Held.Length} held objects");
            Console.WriteLine($"read {Console.ReadLine()}");
            static class Holder { public static LongType[] Held = []; }
            namespace ACompanyWithSeveralProducts.Analytics.HeapSnapshots
            {
                sealed class ATypeWhoseLongNameMustStayReachableBesideAllTheMemoryMetricColumns { }
            }
            """.Replace("LongType", longType, StringComparison.Ordinal));
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(project);
        Ui.Settle();
        Task<string?> started = window.ProfileAsync();
        Ui.WaitFor(() => started.IsCompleted, "the build and the start", TimeSpan.FromMinutes(3));
        Assert.Null(started.Result);
        Passenger.App.Profiling.ProfilerView view = window.ProfilerView;
        Ui.PumpUntil(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("waiting with 40 held objects", StringComparison.Ordinal), "the program to hold its objects and wait on its input");

        // Get Memory Snapshot while it waits: the heap by type on the Memory page, the program still there.
        Avalonia.Controls.Button take = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.Button>()
            .Single(b => b.Content as string == "Get Memory Snapshot");
        take.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        // Bounded, so a createdump that never finishes (CI's macOS runner was left with one) fails here, not the run.
        Ui.WaitFor(() => view.Memory is not null || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true,
            "the memory snapshot", TimeSpan.FromMinutes(2));
        Assert.True(view.Memory is not null, window.Status);
        HeapType strings = Assert.Single(view.HeapTypes, t => t.Name == "System.String");
        Assert.True(strings.Count > 10, $"{strings.Count} strings");
        Assert.Equal(view.Memory.Bytes, view.HeapTypes.Sum(t => t.Bytes));
        Assert.True(window.Profiling is { HasExited: false }, "The program being profiled ended with its memory snapshot");
        HeapType allocated = Assert.Single(view.HeapTypes, type => type.Name == longType);
        Assert.Equal(40, allocated.Count);
        ListBox typeList = view.MemoryPage.TypeList;
        typeList.ScrollIntoView(allocated);
        Ui.Settle();
        Control row = typeList.ContainerFromItem(allocated)!;
        Assert.True(row.Focus());
        typeList.SelectedItem = allocated;
        object? focus = window.FocusManager?.GetFocusedElement();
        ScrollViewer scroll = Assert.Single(typeList.GetVisualAncestors().OfType<ScrollViewer>(), viewer => viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);
        foreach (double width in new double[] { 1200, 683, 640, 1200 })
        {
            double horizontal = scroll.Offset.X;
            window.Width = width;
            Ui.Settle();
            Assert.Same(allocated, typeList.SelectedItem);
            Assert.Same(focus, window.FocusManager?.GetFocusedElement());
            Assert.InRange(Math.Abs(scroll.Offset.X - Math.Min(horizontal, scroll.Extent.Width - scroll.Viewport.Width)), 0, 1);
            Point rowAt = row.TranslatePoint(default, typeList)!.Value;
            Assert.True(rowAt.Y >= -1 && rowAt.Y + row.Bounds.Height <= typeList.Bounds.Height + 1, "Resize hid the selected type outside the vertical reading viewport");
            TextBlock name = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == longType);
            Assert.True(name.Bounds.Width >= 240, $"The type name collapsed to {name.Bounds.Width}px beside its numeric columns");
            Assert.Equal(longType, ToolTip.GetTip(name));
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width, "The memory table has no horizontal access to the full type and all metrics");
            Point over = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(over, new Vector(-30, 0));
            Ui.Settle();
            Assert.True(scroll.Offset.X > 0, "The memory table did not scroll horizontally");
            Grid header = Assert.Single(scroll.GetVisualDescendants().OfType<Grid>(), grid => grid.ColumnDefinitions.Count == 4 && grid.Children.OfType<Button>().Count() == 4);
            Grid cells = Assert.Single(row.GetVisualDescendants().OfType<Grid>());
            foreach (int column in Enumerable.Range(1, 3))
            {
                Control label = Assert.Single(header.Children, child => Grid.GetColumn(child) == column);
                Control value = Assert.Single(cells.Children, child => Grid.GetColumn(child) == column);
                Assert.InRange(Math.Abs(label.TranslatePoint(new Point(label.Bounds.Width, 0), scroll)!.Value.X
                    - value.TranslatePoint(new Point(value.Bounds.Width, 0), scroll)!.Value.X), 0, 1);
            }
            Assert.True(typeList.Bounds.Height < view.Bounds.Height, "The table lost its finite vertical viewport");
            Assert.True(typeList.GetVisualDescendants().OfType<ListBoxItem>().Count() < typeList.Items.Count, "The memory table materialized every type instead of its visible rows");
            UiAudit.Capture(window, $"profiler-heap-types-{width:0}-{(light ? "light" : "dark")}");
        }
        window.Width = 640;
        Ui.Settle();
        Point tableAt = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(tableAt, new Vector(1000, 0));
        Ui.Settle();
        Assert.Equal(0, scroll.Offset.X);
        UiAudit.Capture(window, $"profiler-heap-types-name-640-{(light ? "light" : "dark")}");

        window.Width = 1200;
        Ui.Settle();
        ScrollViewer vertical = Assert.Single(typeList.GetVisualDescendants().OfType<ScrollViewer>());
        Point rowsAt = typeList.TranslatePoint(new Point(typeList.Bounds.Width / 2, typeList.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(rowsAt, new Vector(0, -1000));
        Ui.Settle();
        Assert.True(vertical.Offset.Y > 0);
        Assert.True(typeList.ContainerFromItem(allocated) is not { } selected
            || selected.TranslatePoint(default, typeList) is not { } selectedAt || selectedAt.Y + selected.Bounds.Height <= 0
            || selectedAt.Y >= typeList.Bounds.Height, "The scroll-away fixture still shows its selected type");
        double reading = vertical.Offset.Y;
        window.Width = 640;
        Ui.Settle();
        Assert.Same(allocated, typeList.SelectedItem);
        Assert.InRange(Math.Abs(reading - vertical.Offset.Y), 0, 1);
        window.Profiling.Kill();
        window.Close();
    }

    [AvaloniaFact]
    public void Profiling_a_configuration_whose_working_folder_is_gone_says_why_in_the_profiler_window()
    {
        string project = Project("Spinner", "Console.WriteLine(\"ran\");\n");
        Directory.CreateDirectory(Path.Combine(project, "Properties"));
        File.WriteAllText(Path.Combine(project, "Properties", "launchSettings.json"),
            "{ \"profiles\": { \"Elsewhere\": { \"commandName\": \"Project\", \"workingDirectory\": \"gone\" } } }");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(project);
        Ui.Settle();
        Assert.Equal("Spinner: Elsewhere", window.Configuration?.Name);
        IReadOnlyList<string> before = Ports();

        Task<string?> started = window.ProfileAsync();
        Ui.WaitFor(() => started.IsCompleted, "the build and the start", TimeSpan.FromMinutes(3));
        string gone = Path.Combine(window.Folder!, "gone");
        Assert.Equal($"Could not profile Spinner: its working folder {gone} does not exist", started.Result);
        Assert.Null(window.Profiling);
        Assert.True(window.DockHost.IsShown("profiler"));
        Assert.Equal(started.Result, window.ProfilerView.Status);
        Assert.Equal(started.Result, window.ProfilerView.Placeholder);
        Assert.Equal(before, Ports());
        window.Close();
    }

    /// <summary>A console project restored in the tests' folder, with this as its Program.cs and anything more its project
    /// file is to hold.</summary>
    private string Project(string name, string program, string more = "")
    {
        string project = Path.Combine(folder, name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, $"{name}.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  {more}\n</Project>\n");
        File.WriteAllText(Path.Combine(project, "Program.cs"), program);
        Restore.Run(project);
        return project;
    }

    /// <summary>The diagnostic ports this process's profiler is listening at: a socket in /tmp each, or a named pipe on Windows.</summary>
    private static IReadOnlyList<string> Ports() => OperatingSystem.IsWindows()
        ? [.. Directory.GetFiles(@"\\.\pipe\").Where(p => Path.GetFileName(p).StartsWith($"passenger-profile-{Environment.ProcessId}-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)]
        : [.. Directory.GetFiles(Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath(), $"pp-{Environment.ProcessId}-*.sock").Order(StringComparer.Ordinal)];

    private static CallNode? Find(IEnumerable<CallNode> nodes, Func<CallNode, bool> match)
    {
        foreach (CallNode node in nodes)
        {
            if (match(node))
            {
                return node;
            }
            if (Find(node.Children, match) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
