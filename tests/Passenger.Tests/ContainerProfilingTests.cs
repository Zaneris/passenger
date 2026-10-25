using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Profiling;
using Passenger.Core;
using Passenger.Profiler;

namespace Passenger.Tests;

/// <summary>Profiling a .NET program in a container, as a person does with Attach Profiler: the program chosen among
/// the running containers' when nothing of the folder's runs, or the one debugged there first, recorded there by
/// Passenger's tools and read here, its hot method leading; the Frames page saying why a container's run has none; and
/// memory snapshots taken there, with the container's own DAC, listing what the program keeps and compared object by
/// object. Each skips, saying why, where no Linux engine runs. The program is built here for .NET 10 and copied into an
/// image of .NET's runtime, labelled as the run's (<see cref="Spins"/>).</summary>
[Collection(Alone.Name)]
public sealed class ContainerProfilingTests(ContainerProfilingTests.Spins.Built built) : IClassFixture<ContainerProfilingTests.Spins.Built>
{
    private Spins spins => built.Spins;

    [AvaloniaFact]
    public void Attach_Profiler_offers_the_containers_programs_records_the_one_chosen_there_and_shows_its_hot_method_and_why_it_has_no_frames()
    {
        Assert.SkipWhen(spins.WhyNot is not null, spins.WhyNot ?? string.Empty);
        string container = spins.Start("chosen");
        MainWindow window = Open();
        try
        {
            Assert.True(window.Perform(AppActions.AttachProfiler), "Attach Profiler could not run");
            Ui.Settle();
            AttachToProcessDialog dialog = Assert.Single(window.OwnedWindows.OfType<AttachToProcessDialog>());
            Assert.Equal("Attach Profiler", dialog.Title);
            Ui.WaitFor(() => dialog.Listed.IsCompleted, "Attach Profiler to list the containers' .NET programs");
            DialogButtonsTests.Check(dialog);
            dialog.Choose(container, "Spins.dll");
            Assert.True(dialog.Chosen?.Container == container,
                $"Attach Profiler listed no program in {container}: {string.Join(", ", dialog.Rows.Select(r => r.Heading ?? $"{r.Process?.Container}/{r.Process?.CommandLine}"))}");
            dialog.Attach();
            Recording(window, container);

            Ui.Hold(TimeSpan.FromSeconds(2), "the profiler in the container to sample the spinning program");
            SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the snapshot of the program in the container", TimeSpan.FromMinutes(2));
            Snapshot snapshot = read.Snapshot ?? throw new Xunit.Sdk.XunitException($"No snapshot of the program in the container: {read.Message}");
            Assert.True(snapshot.HotSpots.Take(5).Any(h => h.Name.StartsWith("Hot.Spin", StringComparison.Ordinal)),
                $"The hot method is not among the container program's first hot spots: {string.Join(", ", snapshot.HotSpots.Take(5).Select(h => $"{h.Name} {h.Self}"))}");
            Assert.Contains(window.ProfilerView.Rows, r => r.Node.Name.StartsWith("Hot.Spin", StringComparison.Ordinal));
            Assert.Equal(FramesView.InContainer(container), window.ProfilerView.FramesPage.Placeholder);

            Ui.WaitFor(() => !Spins.Top(container).Contains(ContainerTools.Executable, StringComparison.Ordinal), "the tools' recording in the container to end with the snapshot");
            int kept = Spins.Kept(container);
            Ui.WaitFor(() => Spins.Kept(container) > kept, "the program in the container to run on after its snapshot");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Memory_snapshots_of_a_program_in_a_container_are_taken_there_and_compared_once_its_snapshot_can_be_written_and_none_is_recorded_before()
    {
        Assert.SkipWhen(spins.WhyNot is not null, spins.WhyNot ?? string.Empty);
        string container = spins.Start("memory");
        MainWindow window = Open();
        List<string> blocking = [];
        try
        {
            DotnetProcess program = Assert.Single(Ui.Await(window.AttachableAsync(), "the containers' programs to be listed"), p => p.Container == container);

            // A folder in the place of each name the snapshot could take for the next while, so none can be written.
            DateTime now = DateTime.Now;
            for (int ahead = -1; ahead <= TimeSpan.FromMinutes(2).Scaled().TotalSeconds; ahead++)
            {
                string stamp = now.AddSeconds(ahead).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                blocking.Add(Directory.CreateDirectory(Path.Combine(window.SnapshotFolder, $"Spins-{stamp}.nettrace")).FullName);
            }
            string? refused = Ui.Await(window.AttachProfilerAsync(program), "Attach Profiler's answer");
            Assert.True(refused?.StartsWith("Could not profile Spins: no snapshot can be written in ", StringComparison.Ordinal) == true, $"Attach Profiler answered: {refused}");
            Assert.Null(window.Profiling);
            Assert.DoesNotContain($"{ContainerTools.Executable} trace", Spins.Top(container), StringComparison.Ordinal);
            int ranOn = Spins.Kept(container);
            Ui.WaitFor(() => Spins.Kept(container) > ranOn, "the program in the container to run on");
            blocking.ForEach(Directory.Delete);
            blocking.Clear();

            Ui.Launched(window, window.AttachProfilerAsync(program), "the profiler to attach to the program in the container");
            Recording(window, container);

            MemorySnapshot first = MemorySnapshot(window, 1);
            HeapType keepsakes = Assert.Single(first.Types, t => t.Name == "Keepsake");
            Assert.True(keepsakes.Count > 0, "The first snapshot held no Keepsake");
            int had = Spins.Kept(container);
            Ui.WaitFor(() => Spins.Kept(container) > had + 400, "the program in the container to keep more");
            MemorySnapshot second = MemorySnapshot(window, 2);
            MemoryView memory = window.ProfilerView.MemoryPage;
            Assert.Equal([first, second], memory.Snapshots);

            // The first's Keepsakes all survived, moved or not, and those kept since are new.
            Ui.ClickButton(memory, "Compare with…");
            MenuItem older = Assert.Single(memory.CompareMenu!.Items.OfType<MenuItem>());
            Ui.Click(older, settle: false);
            memory.CompareMenu.Hide();
            Ui.WaitFor(() => memory.Comparison is not null, "the comparison of the container's snapshots");
            Assert.True(memory.Comparison!.Inexact is null, $"The container's snapshots were compared inexactly: {memory.Comparison.Inexact}");
            TypeDelta kept = Assert.Single(memory.DeltaRows, t => t.Name == "Keepsake");
            Assert.Equal((keepsakes.Count, 0), (kept.SurvivedObjects, kept.DeadObjects));
            Assert.True(kept.NewObjects >= 400, $"Only {kept.NewObjects} Keepsakes were new in the second snapshot");

            memory.Choose(kept);
            Ui.ClickButton(memory, "Open New Objects");
            Ui.WaitFor(() => memory.Set is not null, "the new Keepsakes");
            Assert.Equal(kept.NewObjects, memory.ObjectRows.Count);
            Assert.Same(second, memory.Set!.Snapshot);

            Ui.Await(window.SnapshotAsync(), "the run's end", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => !Spins.Top(container).Contains(ContainerTools.Executable, StringComparison.Ordinal), "the tools' heap and trace in the container to end with the run");
            Assert.Equal("running", Docker.State(container));
        }
        finally
        {
            window.Close();
            // The folder's snapshots are the class's other tests' too.
            blocking.ForEach(Directory.Delete);
        }
    }

    [AvaloniaFact]
    public void Attach_Profiler_records_the_program_debugged_in_its_container_without_asking()
    {
        Assert.SkipWhen(spins.WhyNot is not null, spins.WhyNot ?? string.Empty);
        string container = spins.Start("debugged");
        MainWindow window = Open();
        try
        {
            Ui.Run(window, AppActions.AttachToProcess);
            AttachToProcessDialog debugging = Assert.Single(window.OwnedWindows.OfType<AttachToProcessDialog>());
            Ui.WaitFor(() => debugging.Listed.IsCompleted, "Attach to Process to list the .NET programs");
            debugging.Choose(container, "Spins.dll");
            debugging.Attach();
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Running, Elsewhere: true } || window.Status?.StartsWith("Could not", StringComparison.Ordinal) == true,
                "the debugger to attach to the program in the container");
            Assert.True(window.Debugger.Session is { State: DebugState.Running }, $"The debugger did not attach in the container: {window.Status}");

            Assert.True(window.Perform(AppActions.AttachProfiler), "Attach Profiler could not run");
            Ui.Settle();
            Assert.Empty(window.OwnedWindows.OfType<AttachToProcessDialog>());
            Recording(window, container);
            Ui.Hold(TimeSpan.FromSeconds(2), "the profiler in the container to sample the debugged program");
            SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the snapshot of the debugged program", TimeSpan.FromMinutes(2));
            Assert.True(read.Snapshot?.HotSpots.Any(h => h.Name.StartsWith("Hot.Spin", StringComparison.Ordinal)) == true, read.Message);

            Assert.True(window.Perform(AppActions.Detach), "Detach could not run on the program in the container");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go of the program in the container");
            Assert.Equal("running", Docker.State(container));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A program held at its start in its container, as a container run's Profile will start each service: Attach
    /// Profiler lets it go once it records, so the run has it from its first turn.</summary>
    [AvaloniaFact]
    public void A_program_held_at_its_start_in_a_container_is_let_go_by_Attach_Profiler_and_recorded_from_its_first_turn()
    {
        Assert.SkipWhen(spins.WhyNot is not null, spins.WhyNot ?? string.Empty);
        string container = spins.Start("held", held: true);
        MainWindow window = Open();
        try
        {
            Assert.Equal(0, Spins.Kept(container));
            DotnetProcess program = Assert.Single(Ui.Await(window.AttachableAsync(), "the containers' programs to be listed"), p => p.Container == container);
            Ui.Launched(window, window.AttachProfilerAsync(program), "the profiler to attach to the held program in the container");
            Recording(window, container);
            Ui.WaitFor(() => Spins.Kept(container) > 0, "the held program to be let go once the profiler records it");
            Ui.Hold(TimeSpan.FromSeconds(1), "the profiler in the container to sample the program let go");
            SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the snapshot of the program let go", TimeSpan.FromMinutes(2));
            Snapshot snapshot = read.Snapshot ?? throw new Xunit.Sdk.XunitException($"No snapshot of the program let go: {read.Message}");
            Assert.Contains(snapshot.HotSpots, h => h.Name.StartsWith("Hot.Spin", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A window on the program's folder.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(spins.Folder, languageServer: false);
        return window;
    }

    /// <summary>Waits for the profiler to record in the container, failing with what the window said when it could not.</summary>
    private static void Recording(MainWindow window, string container)
    {
        Ui.WaitFor(() => window.Profiling is { Place: not null } || window.ProfilerView.Placeholder.StartsWith("Could not", StringComparison.Ordinal)
            || window.Status?.StartsWith("Could not", StringComparison.Ordinal) == true, $"the profiler to record in {container}");
        Assert.True(window.Profiling?.Place?.Where == container, $"The profiler is not recording in {container}: {window.Status} / {window.ProfilerView.Placeholder}");
    }

    /// <summary>Get Memory Snapshot, as its button takes one: the snapshot numbered so, or the test fails with why there is none.</summary>
    private static MemorySnapshot MemorySnapshot(MainWindow window, int number)
    {
        Ui.ClickButton(window.ProfilerView, "Get Memory Snapshot");
        Ui.WaitFor(() => window.ProfilerView.Memory?.Number == number || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true,
            $"memory snapshot #{number} of the program in the container", TimeSpan.FromMinutes(2));
        Assert.True(window.ProfilerView.Memory?.Number == number, window.Status);
        return window.ProfilerView.Memory!;
    }

    /// <summary>The program the tests profile, built here in Debug for .NET 10 and copied into an image of .NET's runtime:
    /// it spins in a hot method and keeps a few objects every turn, saying how many. Passenger's container tools for the
    /// engine's processor are made where a dev build finds them. Made once a run (<see cref="Shared"/>), for the agent's
    /// test too; the image carries the run's label and goes with the run's containers as it ends.</summary>
    public sealed class Spins
    {
        public const string Program = """
            // A program for the profiler to attach to in a container: it spins in a hot method and keeps what it makes.
            using System.Runtime.CompilerServices;

            while (true)
            {
                Hot.Spin();
                Kept.Grow();
                Console.WriteLine($"kept {Kept.Count}");
                Thread.Sleep(10);
            }

            static class Hot
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                public static double Spin()
                {
                    double total = 0;
                    for (int i = 1; i < 10_000_000; i++)
                    {
                        total += Math.Sqrt(i);
                    }
                    return total;
                }
            }

            static class Kept
            {
                private static readonly List<Keepsake> All = [];

                public static int Count => All.Count;

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static void Grow()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        All.Add(new Keepsake(All.Count));
                    }
                }
            }

            sealed class Keepsake(int number)
            {
                public int Number { get; } = number;
            }

            """;

        private const string Project = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>Spins</AssemblyName>
              </PropertyGroup>
            </Project>

            """;

        private static readonly Lazy<Spins> Made = new(() => new Spins(), LazyThreadSafetyMode.ExecutionAndPublication);

        private Spins()
        {
            WhyNot = Docker.WhyNot;
            string root = TestFolders.Unowned("spins-");
            Folder = PathNames.Canonical(Path.Combine(root, "Spins"));
            if (WhyNot is not null)
            {
                return;
            }
            ContainerPrograms.Publish($"linux-{ContainerPrograms.Processor()}");
            string output = Path.Combine(root, "out");
            ContainerPrograms.Build(Folder, Project, Program, output);
            Image = ContainerPrograms.Image(Path.Combine(root, "image"), "spins", "mcr.microsoft.com/dotnet/runtime:10.0", output, "Spins");
        }

        /// <summary>The program and its image, made once for the run.</summary>
        public static Spins Shared => Made.Value;

        /// <summary>Why no container runs here, or null.</summary>
        public string? WhyNot { get; }

        /// <summary>The program's project folder, canonical, as the IDE names it.</summary>
        public string Folder { get; }

        public string Image { get; } = string.Empty;

        /// <summary>The line (from one) of the hot method's first sequence point, its opening brace, as its PDB has it.</summary>
        public static int SpinLine => Array.FindIndex(Program.Split('\n'), l => l.Contains("public static double Spin()", StringComparison.Ordinal)) + 2;

        /// <summary>A container of the image, started as a person would start one (not by Passenger), labelled as the run's,
        /// once its program says what it keeps; <paramref name="held"/>, its runtime held at its start
        /// (DOTNET_DefaultDiagnosticPortSuspend=1), as a container run's Profile will start each service; <paramref
        /// name="offWindow"/> for an agent's test, which has no window whose loop to run meanwhile.</summary>
        public string Start(string name, bool held = false, bool offWindow = false)
        {
            string named = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}spins{name}";
            Docker.Run(["run", "-d", .. Docker.AnyPids, "--name", named, "--label", Docker.RunLabel, .. held ? (string[])["-e", "DOTNET_DefaultDiagnosticPortSuspend=1"] : [], Image]);
            // Not Dispatcher.UIThread.CheckAccess(): in a process no window test has run in yet, asking makes the asking thread Avalonia's.
            Assert.True(held || (offWindow ? Waiting.Until(() => Kept(named) > 0, TimeSpan.FromMinutes(1))
                : Ui.Pumped(() => Kept(named) > 0, TimeSpan.FromMinutes(1))),
                $"The program in {named} never said what it keeps: {Docker.Ask(["logs", "--tail", "5", named])}");
            return named;
        }

        /// <summary>How many objects the program in a container last said it keeps.</summary>
        public static int Kept(string container)
        {
            string said = Docker.Ask(["logs", "--tail", "1", container]).Trim();
            return said.StartsWith("kept ", StringComparison.Ordinal) && int.TryParse(said[5..], CultureInfo.InvariantCulture, out int kept) ? kept : 0;
        }

        /// <summary>A container's processes as docker top shows them.</summary>
        public static string Top(string container) => Docker.Ask(["top", container]);

        /// <summary>The class fixture: the program made before the class's tests, off the window's thread.</summary>
        public sealed class Built
        {
            public Spins Spins { get; } = Shared;
        }
    }
}
