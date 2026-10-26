using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Profiling;
using Passenger.Dap;
using Passenger.Profiler;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The long lists a running program fills, scrolled with the wheel as <see cref="LongListsTests"/> scrolls the
/// IDE's own: its values at a stop, its profile's call tree, hot spots and allocated types, and a heap snapshot's objects,
/// each of a hundred items against a thousand, from one program built once (<see cref="Lots"/>). Debugged and profiled
/// through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class ProgramListsTests : IClassFixture<ProgramListsTests.Lots>
{
    private readonly Lots lots;

    public ProgramListsTests(Lots lots)
    {
        this.lots = lots;
        lots.Reset();
    }

    [AvaloniaFact]
    public void A_scroll_step_down_an_objects_thousand_fields_in_variables_hands_the_rows_other_fields_never_making_one_again()
    {
        MainWindow window = lots.Open(lots.PathOf("Program.cs"), languageServer: false);
        try
        {
            window.EditorArea.CodeEditor.GoTo(new TextPosition(Lots.Line("Console.WriteLine($\"fields"), 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync("fields"), "the program to build and start under the debugger", TimeSpan.FromMinutes(3));
            Passenger.App.ToolWindows.DebugModel debug = window.BottomPanel.Debug;
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && debug.Tree.Rows.Any(r => r.Name == "many"),
                () => $"the stop at the breakpoint and its locals; the status bar said \"{window.Status}\"");
            VariablesList values = window.GetVisualDescendants().OfType<VariablesList>().Single(l => l.Tree == debug.Tree);
            LongListsTests.Holds("Variables", "fields", 100, 1000, count =>
            {
                VariableNode shown = debug.Tree.Rows.Single(r => r.Name == (count == 100 ? "few" : "many"));
                DebuggerHoverTests.Toggle(window, values, shown);
                Ui.WaitFor(() => debug.Tree.Rows.Count(r => r.Parent == shown) == count, () => $"{shown.Name}'s {count} fields, where Variables shows {DebuggerHoverTests.Shown(values)}");
                Ui.Settle();
                return (values, () => DebuggerHoverTests.Toggle(window, values, shown));
            });
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to end");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_scroll_step_down_a_profiles_thousand_methods_and_types_hands_the_rows_others_never_making_one_again()
    {
        MainWindow window = lots.Open(null, languageServer: false);
        try
        {
            ProfilerView view = window.ProfilerView;
            Dictionary<int, string> traces = [];
            foreach (int count in (int[])[100, 1000])
            {
                string? before = view.Snapshot?.TracePath;
                Ui.Launched(window, window.ProfileAsync($"calls {count}"), $"the program calling {count} methods to build and start under the profiler", TimeSpan.FromMinutes(3));
                Ui.WaitFor(() => view.Snapshot is { } snapshot && snapshot.TracePath != before, () => $"the end of the program calling {count} methods to show its snapshot ({view.Status})",
                    TimeSpan.FromMinutes(2));
                traces[count] = view.Snapshot!.TracePath;
            }
            Holds("The profiler's call tree", "methods", ProfilerPage.CallTree, l => ReferenceEquals(l.ItemsSource, view.Rows),
                () => view.Rows.Count(r => r.Node.Name.StartsWith("Calls.M", StringComparison.Ordinal)));
            Holds("The profiler's hot spots", "methods", ProfilerPage.HotSpots, l => l.ItemsSource is IReadOnlyList<HotSpot>,
                () => view.Snapshot!.HotSpots.Count(s => s.Name.StartsWith("Calls.M", StringComparison.Ordinal)));
            Holds("The profiler's allocated types", "types", ProfilerPage.Allocations, l => l.ItemsSource is IReadOnlyList<AllocatedType>,
                () => view.Types.Count(t => t.Name.StartsWith("Made", StringComparison.Ordinal)));

            // Each snapshot opened on the page, listing most of what the program made: a sampler can miss a method's few milliseconds.
            void Holds(string list, string items, ProfilerPage page, Func<ListBox, bool> which, Func<int> listed) =>
                LongListsTests.Holds(list, items, 100, 1000, count =>
                {
                    Snapshot? opened = Ui.Await(window.OpenSnapshotAsync(traces[count]), $"the snapshot of {count} methods to open");
                    Assert.True(opened is not null, $"The snapshot of {count} methods did not open: {window.Status}");
                    view.ShowPage(page);
                    Ui.Settle();
                    Assert.True(listed() >= count / 2, $"{list} listed {listed()} of the {count} {items} the program made");
                    return (view.GetVisualDescendants().OfType<ListBox>().Single(l => l.IsEffectivelyVisible && which(l)), () => { });
                });
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_scroll_step_down_a_heap_snapshots_thousand_objects_of_a_type_hands_the_rows_other_objects_never_making_one_again()
    {
        MainWindow window = lots.Open(null, languageServer: false);
        try
        {
            ProfilerView view = window.ProfilerView;
            MemoryView memory = view.MemoryPage;
            Ui.Launched(window, window.ProfileAsync("holds"), "the program to build and start under the profiler", TimeSpan.FromMinutes(3));
            Ui.PumpUntil(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("ready", StringComparison.Ordinal), "the program to fill its heap");
            Ui.ClickButton(view, "Get Memory Snapshot");
            // Bounded, so a createdump that never finishes fails here, not the run.
            Ui.WaitFor(() => view.Memory is not null || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true, "the memory snapshot", TimeSpan.FromMinutes(2));
            Assert.True(view.Memory is not null, window.Status);
            LongListsTests.Holds("A heap snapshot's objects", "objects", 100, 1000, count =>
            {
                string type = count == 100 ? "FewHeld" : "ManyHeld";
                HeapType held = Assert.Single(memory.TypeRows, t => t.Name == type);
                memory.TypeList.SelectedItem = held;
                memory.TypeList.ScrollIntoView(held);
                Ui.Settle();
                Assert.True(memory.TypeList.ContainerFromItem(held)?.Focus() == true, $"{type}'s row could not take the keys");
                Ui.Press(window, "enter");
                Ui.WaitFor(() => memory.Set is not null && memory.ObjectRows.Count == count, () => $"the {count} objects of {type}, where the page shows {memory.Trail}");
                Ui.Settle();
                return (memory.ObjectList, () => Ui.Click(memory.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Snapshot #1")));
            });
            window.Profiling!.Kill();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A program of long lists, chosen by its arguments: "fields" makes an object of a hundred fields and one of a
    /// thousand and writes them, where a breakpoint stops it; "calls N" calls N of its thousand methods, each spinning a few
    /// milliseconds in its own body and making objects of a type of its own; "holds" keeps a hundred objects of one type and
    /// a thousand of another, says it is ready and waits on its input.</summary>
    public sealed class Lots : SharedProject
    {
        private const int Methods = 1000;

        public Lots()
            : base("lists-program-")
        {
            File.WriteAllText(PathOf("Lots.csproj"), TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
            File.WriteAllText(PathOf("Program.cs"), Program);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }

        /// <summary>The line (from zero) of the program holding some text.</summary>
        public static int Line(string text) => Array.FindIndex(Program.Split('\n'), l => l.Contains(text, StringComparison.Ordinal));

        private static readonly string Program = Written();

        private static string Written()
        {
            StringBuilder text = new("""
                using System.Runtime.CompilerServices;
                switch (args)
                {
                    case ["fields"]:
                        Few few = new();
                        Many many = new();
                        Console.WriteLine($"fields {few.F0 + many.F0}");
                        break;
                    case ["calls", string count]:
                        Console.WriteLine($"called {Calls.Run(int.Parse(count))}");
                        break;
                    case ["holds"]:
                        Held.Fill();
                        Console.WriteLine("ready");
                        Console.ReadLine();
                        GC.KeepAlive(Held.Kept);
                        break;
                }

                static class Held
                {
                    public static readonly List<object> Kept = [];

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    public static void Fill()
                    {
                        for (int i = 0; i < 100; i++) { Kept.Add(new FewHeld(i)); }
                        for (int i = 0; i < 1000; i++) { Kept.Add(new ManyHeld(i)); }
                    }
                }

                sealed class FewHeld(int number) { public int Number { get; } = number; }

                sealed class ManyHeld(int number) { public int Number { get; } = number; }

                static class Calls
                {
                    private static readonly object[] kept = new object[16];

                    public static long Run(int count)
                    {
                        long total = 0;
                        for (int i = 0; i < count; i++) { total += Methods[i](); }
                        return total;
                    }

                """);
            text.Append("    private static readonly Func<long>[] Methods = [").AppendJoin(", ", Enumerable.Range(0, Methods).Select(i => $"M{i}")).Append("];\n");
            for (int i = 0; i < Methods; i++)
            {
                // 380 KB of its own type, then its own arithmetic, calling nothing, so samples land in its body.
                text.Append(CultureInfo.InvariantCulture, $"    [MethodImpl(MethodImplOptions.NoInlining)] private static long M{i}() {{ for (int i = 0; i < 8000; i++) {{ kept[i & 15] = new Made{i}(); }} ")
                    .Append("long spun = 0; for (long i = 0; i < 4_000_000; i++) { spun += (i * 31) ^ (spun >> 3); } return spun; }\n");
            }
            text.Append("}\n\n");
            for (int i = 0; i < Methods; i++)
            {
                text.Append(CultureInfo.InvariantCulture, $"sealed class Made{i} {{ public long A, B, C, D; }}\n");
            }
            text.Append("\nsealed class Few { public int ").AppendJoin(", ", Enumerable.Range(0, 100).Select(i => $"F{i} = {i}")).Append("; }\n");
            text.Append("\nsealed class Many { public int ").AppendJoin(", ", Enumerable.Range(0, 1000).Select(i => $"F{i} = {i}")).Append("; }\n");
            return text.ToString();
        }
    }
}
