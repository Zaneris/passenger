using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Updates;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Debugging a .NET program in a container Passenger did not start, as a person does with Attach to Process: the
/// program listed under its container, attached to by Passenger's own adapter inside the container (copied in first, over
/// docker exec), stopping at a breakpoint, evaluating and stepping, and running on after Detach; on Alpine (musl) too; a
/// build whose PDB names the container's paths, or a Windows machine's, stopping at its breakpoint by the document in the
/// container, the path map at the protocol's edge, and case as Windows names files; and the tools downloaded from their
/// release, checked against SHA256SUMS. Each skips, saying why, where no Linux engine runs. The program is built on this
/// machine for .NET 10 and copied into images of .NET's own runtime, labelled as the run's (<see cref="Turns"/>).</summary>
[Collection(Alone.Name)]
public sealed class ContainerDebuggingTests(ContainerDebuggingTests.Turns turns) : IClassFixture<ContainerDebuggingTests.Turns>
{
    /// <summary>The line (from zero) the tests stop at, and the one a step over goes on to.</summary>
    private const int Said = 6;
    private const int Slept = 7;

    [AvaloniaFact]
    public void A_program_in_a_container_passenger_did_not_start_is_listed_attached_to_stopped_stepped_evaluated_and_runs_on_after_detach()
    {
        Assert.SkipWhen(turns.WhyNot is not null, turns.WhyNot ?? string.Empty);
        string container = Start("glibc", turns.Glibc);
        MainWindow window = Open(turns.Source("host"));
        try
        {
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            DotnetProcess listed = Assert.Single(dialog.Shown, p => p.Container == container);
            Assert.Equal("Turns", listed.Program);
            Assert.Contains(dialog.Rows, r => r.Heading == $"Container {container}: {ContainerProcesses.CopiesTools}");
            ChooseIn(dialog, container);
            Assert.Equal(container, dialog.Chosen?.Container);
            dialog.Attach();
            StoppedAtBreakpoint(window, "the program in the container");
            ShownAt(window, Said);
            DebugSession session = window.Debugger.Session!;
            Assert.False(session.IsLocal, "A container's program was taken for one of this machine's, which Stop could kill by its id");
            Assert.Null(window.Debugger.HotReload);
            Assert.Matches("^turn [0-9]+$", Evaluate(session, "said").Trim('"'));

            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => session.State == DebugState.Paused && window.EditorArea.CodeEditor.LineMarks.Contains(new LineMark(Slept, LineMarkKind.ExecutionPoint)),
                "the step over to stop on the next line");

            Assert.True(window.Perform(AppActions.Detach), "Detach could not run on the program in the container");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go of the program in the container");
            Assert.Equal("running", Docker.State(container));
            Ui.WaitFor(() => !Top(container).Contains(ContainerTools.Executable, StringComparison.Ordinal), "the adapter in the container to end with Detach");
            int seen = Turned(container);
            Ui.WaitFor(() => Turned(container) > seen, "the program let go of to count on");

            Ui.Run(window, AppActions.ReattachToProcess);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused or DebugState.Running } again && again != session, "Reattach to Process to attach again");
            DebugSession reattached = window.Debugger.Session!;
            Processes.Kill(reattached.AdapterProcessId);
            Ui.WaitFor(() => reattached.State == DebugState.Ended, "the session to end with its docker exec");
            Ui.WaitFor(() => !Top(container).Contains(ContainerTools.Executable, StringComparison.Ordinal), "the adapter in the container to end once docker exec's CLI was killed");
            Assert.Equal("running", Docker.State(container));
            seen = Turned(container);
            Ui.WaitFor(() => Turned(container) > seen, "the program to count on after its debugger went");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void On_alpine_the_debugger_runs_in_the_container_on_musl_and_stops_at_a_breakpoint_and_a_program_with_diagnostics_off_says_why()
    {
        Assert.SkipWhen(turns.WhyNot is not null, turns.WhyNot ?? string.Empty);
        string container = Start("alpine", turns.Alpine);
        MainWindow window = Open(turns.Source("host"));
        try
        {
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            ChooseIn(dialog, container);
            dialog.Attach();
            StoppedAtBreakpoint(window, "the program in the Alpine container");
            ShownAt(window, Said);
            Assert.Matches("^[0-9]+$", Evaluate(window.Debugger.Session!, "turn"));
            Assert.True(window.Perform(AppActions.Detach), "Detach could not run on the program in the Alpine container");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go of the program in the Alpine container");
            Assert.Equal("running", Docker.State(container));

            string off = Start("alpineoff", turns.Alpine, "-e", "DOTNET_EnableDiagnostics=0");
            dialog = OpenAttachToProcess(window);
            ChooseIn(dialog, off);
            dialog.Attach();
            Ui.WaitFor(() => window.Status?.Contains("DOTNET_EnableDiagnostics=0", StringComparison.Ordinal) == true, "the status bar to say why the program cannot be attached to");
            Assert.False(window.Debugger.IsDebugging, "A session started on a program whose runtime has its debugger off");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What holds a program at its start until a debugger lets it go, as a container run's Debug starts it.</summary>
    private const string Held = "DOTNET_DefaultDiagnosticPortSuspend=1";

    /// <summary>A program held at its start with DOTNET_MODIFIABLE_ASSEMBLIES=debug, as Debug of a container run will start
    /// each service: listed, attached to, let go by the attach so its first turn stops at the breakpoint, and offered Hot
    /// Reload, which a program not started so is not.</summary>
    [AvaloniaFact]
    public void A_program_held_at_its_start_in_a_container_is_let_go_by_the_attach_stops_at_its_breakpoint_and_is_offered_hot_reload()
    {
        Assert.SkipWhen(turns.WhyNot is not null, turns.WhyNot ?? string.Empty);
        string container = Start("held", turns.Glibc, "-e", Held, "-e", "DOTNET_MODIFIABLE_ASSEMBLIES=debug");
        MainWindow window = Open(turns.Source("host"));
        try
        {
            Assert.Equal(0, Turned(container));
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            ChooseIn(dialog, container);
            dialog.Attach();
            StoppedAtBreakpoint(window, "the program held at its start");
            Assert.Equal("turn 1", Evaluate(window.Debugger.Session!, "said").Trim('"'));
            Assert.NotNull(window.Debugger.HotReload);
            Assert.True(window.Perform(AppActions.Detach), "Detach could not run on the held program");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go of the held program");
            Ui.WaitFor(() => Turned(container) > 1, "the program let go of to count on");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A build whose PDB names the container's own paths (/src/Turns, its sources there too): attached to with no
    /// map, its breakpoint binds by the document in the container; through a path map at the protocol's edge (what a container
    /// run configuration gives), its stop shows in the editor's file.</summary>
    [AvaloniaFact]
    public void A_build_naming_the_containers_paths_stops_at_its_breakpoint_by_the_document_there_and_through_a_path_map_in_the_editor()
    {
        Assert.SkipWhen(turns.WhyNot is not null, turns.WhyNot ?? string.Empty);
        string container = Start("mapped", turns.Mapped);
        string folder = turns.Folder("mapped");
        MainWindow window = Open(turns.Source("mapped"));
        try
        {
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            ChooseIn(dialog, container);
            dialog.Attach();
            StoppedAtBreakpoint(window, "the program whose PDB names the container's paths");
            Assert.True(window.Perform(AppActions.Detach), "Detach could not run");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go");

            ContainerPaths map = new([("/src/Turns/", folder)]);
            AttachThrough(window, container, map);
            StoppedAtBreakpoint(window, "the program attached to through a path map");
            ShownAt(window, Said);
            Assert.True(window.Perform(AppActions.Detach), "Detach could not run");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A build whose PDB names a Windows machine's paths (C:\work\Turns), debugged in a Linux container: the
    /// breakpoint's path, given in another letter case as Windows may (c:\WORK\turns), binds there as Windows names files.</summary>
    [AvaloniaFact]
    public void A_build_naming_a_windows_machines_paths_stops_at_its_breakpoint_in_a_linux_container()
    {
        Assert.SkipWhen(turns.WhyNot is not null, turns.WhyNot ?? string.Empty);
        string container = Start("windows", turns.Windows);
        string folder = turns.Folder("windows");
        MainWindow window = Open(turns.Source("windows"));
        try
        {
            // The breakpoint's path goes in by the first pair, another letter case than the PDB's; the stop's comes back by the second.
            ContainerPaths map = new([("c:/WORK/turns/", folder), ("C:/work/Turns/", folder)]);
            AttachThrough(window, container, map);
            StoppedAtBreakpoint(window, "the program whose PDB names a Windows machine's paths");
            ShownAt(window, Said);
            Assert.True(window.Perform(AppActions.Detach), "Detach could not run");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>With no copy of the tools here, attaching downloads the release's of this version from its feed (a stand-in
    /// on loopback), checked against the release's SHA256SUMS, and caches them; a SHA256SUMS that names other bytes is
    /// refused, the status bar saying so, and nothing is copied into the container.</summary>
    [AvaloniaFact]
    public void The_container_tools_download_from_their_release_checked_against_its_SHA256SUMS_and_a_wrong_checksum_is_refused()
    {
        Assert.SkipWhen(turns.WhyNot is not null, turns.WhyNot ?? string.Empty);
        using UpdatesTests.StandIn github = new();
        string tag = $"v{Product.Version}";
        string asset = ContainerTools.Asset(turns.Rid);
        byte[] archive = turns.Archive(turns.Rid);
        github.Serve(tag, asset, archive);

        github.Serve(tag, "SHA256SUMS", Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData((byte[])[1, 2, 3]))}  {asset}\n"));
        string refusing = Start("refused", turns.Glibc);
        string badCache = TestFolders.Create("tools-bad-");
        MainWindow refused = Open(turns.Source("host"), new ContainerToolsSource([], new ReleaseFeed(github.Latest, TimeSpan.FromSeconds(5)), badCache));
        try
        {
            AttachToProcessDialog dialog = OpenAttachToProcess(refused);
            ChooseIn(dialog, refusing);
            dialog.Attach();
            Ui.WaitFor(() => refused.Status is { } said && (said.Contains("SHA256SUMS", StringComparison.Ordinal) || said.StartsWith("Could not", StringComparison.Ordinal)),
                "the status bar to say the download was refused");
            Assert.Contains("are not what the", refused.Status, StringComparison.Ordinal);
            Assert.False(refused.Debugger.IsDebugging, "A session started with tools whose checksum did not match");
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(badCache, Product.Version)));
            Assert.Contains("No such file", Docker.Ask(["exec", refusing, "ls", ContainerTools.Copied]), StringComparison.Ordinal);
        }
        finally
        {
            refused.Close();
        }

        github.Serve(tag, "SHA256SUMS", Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(archive))}  {asset}\n"));
        string container = Start("download", turns.Glibc);
        string cache = TestFolders.Create("tools-cache-");
        MainWindow window = Open(turns.Source("host"), new ContainerToolsSource([], new ReleaseFeed(github.Latest, TimeSpan.FromSeconds(5)), cache));
        try
        {
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            ChooseIn(dialog, container);
            dialog.Attach();
            StoppedAtBreakpoint(window, "the program attached to with downloaded tools");
            Assert.True(File.Exists(Path.Combine(cache, Product.Version, turns.Rid, ContainerTools.Executable)), "The downloaded tools were not kept in the cache");
            Assert.Equal(2, github.RequestsFor(asset).Length);
            Assert.True(window.Perform(AppActions.Detach), "Detach could not run");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A container of the image, started as a person would start one (not by Passenger), labelled as the run's.</summary>
    private static string Start(string name, string image, params string[] options)
    {
        string named = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}turns{name}";
        Docker.Run(["run", "-d", .. Docker.AnyPids, "--name", named, "--label", Docker.RunLabel, .. options, image]);
        if (!options.Contains(Held))
        {
            Ui.WaitFor(() => Turned(named) > 0, "the program in the container to start counting");
        }
        return named;
    }

    /// <summary>The last turn the program in a container wrote.</summary>
    private static int Turned(string container)
    {
        string said = Docker.Ask(["logs", "--tail", "1", container]).Trim();
        return said.StartsWith("turn ", StringComparison.Ordinal) && int.TryParse(said[5..], CultureInfo.InvariantCulture, out int turn) ? turn : 0;
    }

    /// <summary>A container's processes as docker top shows them.</summary>
    private static string Top(string container) => Docker.Ask(["top", container]);

    /// <summary>A window on the program's source, its breakpoint set on the line the tests stop at.</summary>
    private static MainWindow Open(string source, ContainerToolsSource? tools = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, ContainerToolsFrom = tools };
        window.Show();
        window.OpenFile(source);
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(Said, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        return window;
    }

    /// <summary>Chooses the program in a container, as a click on its row does, failing with the rows listed when it has none.</summary>
    private static void ChooseIn(AttachToProcessDialog dialog, string container)
    {
        dialog.Choose(container, "Turns.dll");
        Assert.True(dialog.Chosen?.Container == container,
            $"Attach to Process listed no program in {container}: {string.Join(", ", dialog.Rows.Select(r => r.Heading ?? $"{r.Process?.Container}/{r.Process?.CommandLine}"))}");
    }

    private static AttachToProcessDialog OpenAttachToProcess(MainWindow window)
    {
        Ui.Run(window, AppActions.AttachToProcess);
        AttachToProcessDialog dialog = Assert.Single(window.OwnedWindows.OfType<AttachToProcessDialog>());
        Ui.WaitFor(() => dialog.Listed.IsCompleted, "Attach to Process to list the .NET programs");
        return dialog;
    }

    /// <summary>Attaches to the program in a container through a path map, as a container run configuration's Debug does: the
    /// tools put in, the program found by its id there, the adapter started there over docker exec.</summary>
    private static void AttachThrough(MainWindow window, string container, ContainerPaths map)
    {
        (string tools, int id) = Task.Run(async () =>
        {
            string executable = await ContainerTools.InContainerAsync(window.Engine, container, CancellationToken.None)
                ?? await ContainerTools.CopyIntoAsync(window.Engine, container, ContainerToolsSource.Default(window.Releases).Local
                    .Select(l => Path.Combine(l, "linux-" + Turns.Processor)).First(Directory.Exists), CancellationToken.None);
            IReadOnlyList<DotnetProcess> there = await ContainerTools.ProcessesAsync(window.Engine, container, executable, CancellationToken.None);
            return (executable, there.Single(p => p.CommandLine.Contains("Turns.dll", StringComparison.Ordinal)).Id);
        }).GetAwaiter().GetResult();
        AdapterPlace place = new(arguments => window.Engine.Command(ContainerTools.AdapterCommand(container, tools, arguments)), ContainerTools.SymbolCache, map, container);
        Task<string?> attached = window.Debugger.AttachAsync(id, $"Turns in {container}", place: place);
        Ui.WaitFor(() => attached.IsCompleted, "the debugger to attach through the path map");
        Assert.Null(attached.Result);
    }

    /// <summary>Waits for the program to stop at its breakpoint, failing with what the status bar said when the attach failed.</summary>
    private static void StoppedAtBreakpoint(MainWindow window, string what)
    {
        Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused, StopReason: Passenger.Dap.StopReasons.Breakpoint }
            || window.Status is { } said && (said.StartsWith("Could not", StringComparison.Ordinal) || said.StartsWith("Cannot", StringComparison.Ordinal)),
            $"{what} to stop at its breakpoint");
        Assert.True(window.Debugger.Session is { State: DebugState.Paused }, $"{what} did not stop at its breakpoint: {window.Status}");
    }

    /// <summary>Waits for the stop to show in the editor's file, failing with where the frames said it was.</summary>
    private static void ShownAt(MainWindow window, int line)
    {
        DebugSession session = window.Debugger.Session!;
        try
        {
            Ui.WaitFor(() => window.EditorArea.CodeEditor.LineMarks.Contains(new LineMark(line, LineMarkKind.ExecutionPoint)), "the stop to show in the editor");
        }
        catch (TimeoutException)
        {
            Task<IReadOnlyList<Passenger.Dap.DapStackFrame>> frames = session.StackTraceAsync(session.StoppedThread ?? 0);
            Ui.WaitFor(() => frames.IsCompleted, "the stop's frames");
            Assert.Fail("The stop did not show in the editor; its frames: "
                + string.Join(", ", frames.Result.Take(3).Select(f => $"{f.Name} at {f.Source?.Path}:{f.Line}")));
        }
    }

    private static string Evaluate(DebugSession session, string expression)
    {
        Task<int> thread = Task.FromResult(session.StoppedThread ?? 0);
        Task<IReadOnlyList<Passenger.Dap.DapStackFrame>> frames = session.StackTraceAsync(thread.Result);
        Ui.WaitFor(() => frames.IsCompleted, "the stop's frames");
        Task<Passenger.Dap.EvaluateResponse> value = session.EvaluateAsync(expression, frames.Result[0].Id, "watch");
        Ui.WaitFor(() => value.IsCompleted, $"{expression} to be evaluated");
        return value.Result.Result;
    }

    /// <summary>The program the tests debug, built here in Debug for .NET 10 four ways (as it is, naming the container's
    /// paths, naming a Windows machine's), each copied into an image of .NET's runtime (Debian's, and Alpine's for musl),
    /// and Passenger's container tools for the engine's processor made where a dev build finds them
    /// (artifacts/container-tools), as <c>just container-tools</c> makes them: a publish of each system, some seconds when
    /// they are up to date. The images carry the run's label and go with the run's containers as it ends (Docker.RemoveEverything).</summary>
    public sealed class Turns : IDisposable
    {
        private const string Program = """
            // A program that counts its turns until it is ended, for the debugger to attach to in a container.
            int turn = 0;
            while (true)
            {
                turn++;
                string said = $"turn {turn}";
                Console.WriteLine(said);
                Thread.Sleep(100);
            }

            """;

        private const string Project = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>Turns</AssemblyName>
              </PropertyGroup>
            </Project>

            """;

        private readonly string root;

        public Turns()
        {
            WhyNot = Docker.WhyNot;
            root = TestFolders.Unowned("turns-");
            if (WhyNot is not null)
            {
                return;
            }
            Processor = ContainerPrograms.Processor();
            Rid = $"linux-{Processor}";
            foreach (string rid in (string[])[Rid, $"linux-musl-{Processor}"])
            {
                ContainerPrograms.Publish(rid);
            }
            string host = Build("host", null);
            string mapped = Build("mapped", "/src/Turns/");
            string windows = Build("windows", @"C:\work\Turns\");
            Glibc = Image("glibc", "mcr.microsoft.com/dotnet/runtime:10.0", host, null);
            Alpine = Image("alpine", "mcr.microsoft.com/dotnet/runtime:10.0-alpine", host, null);
            Mapped = Image("mapped", "mcr.microsoft.com/dotnet/runtime:10.0", mapped, Folder("mapped"));
            Windows = Image("windows", "mcr.microsoft.com/dotnet/runtime:10.0", windows, null);
        }

        /// <summary>Why no container runs here, or null.</summary>
        public string? WhyNot { get; }

        /// <summary>The engine's processor, as .NET names it (x64, arm64).</summary>
        public static string Processor { get; private set; } = "x64";

        /// <summary>The tools' glibc system for the engine.</summary>
        public string Rid { get; } = "linux-x64";

        public string Glibc { get; } = string.Empty;

        public string Alpine { get; } = string.Empty;

        public string Mapped { get; } = string.Empty;

        public string Windows { get; } = string.Empty;

        /// <summary>A build's project folder, canonical, as the IDE names it.</summary>
        public string Folder(string variant) => PathNames.Canonical(Path.Combine(root, variant, "Turns"));

        public string Source(string variant) => Path.Combine(Folder(variant), "Program.cs");

        /// <summary>The tools for a system as the release carries them: their folder's files in a gzipped tar.</summary>
        public byte[] Archive(string rid)
        {
            using MemoryStream packed = new();
            using (GZipStream zipped = new(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                TarFile.CreateFromDirectory(Path.Combine(Debugging.Helpers.Debuggees.Root, "artifacts", "container-tools", rid), zipped, includeBaseDirectory: false);
            }
            return packed.ToArray();
        }

        /// <summary>The program's project in a folder of its own, built in Debug with its paths mapped to <paramref name="mapped"/>
        /// (the PDB naming them so) or not; its output folder.</summary>
        private string Build(string variant, string? mapped)
        {
            string output = Path.Combine(root, "out", variant);
            ContainerPrograms.Build(Path.Combine(root, variant, "Turns"), Project, Program, output, mapped);
            return output;
        }

        /// <summary>An image of the build on a runtime image, with its sources at /src/Turns when given; its tag.</summary>
        private string Image(string variant, string from, string build, string? sources) =>
            ContainerPrograms.Image(Path.Combine(root, "image-" + variant), variant, from, build, "Turns", sources, sources is null ? null : "/src/Turns/");

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
