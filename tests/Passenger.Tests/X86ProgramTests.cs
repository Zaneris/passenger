using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A program built for 32-bit x86 runs as Windows runs it, by its own apphost on an x86 .NET, never handed to the
/// 64-bit .NET, which cannot load its assembly; Debug follows it from the debugger's 32-bit host, stopping at a breakpoint,
/// reading its locals and stepping, as Attach does a 32-bit program running, and a 32-bit WPF app too; an x86 .NET lacking
/// what the program runs on is named before it starts; and off Windows it is refused, as .NET runs x86 programs on Windows
/// alone. Profile records it as Run starts it, and Run with Hot Reload runs it under dotnet watch. Its packs come from
/// nuget.org, which the test folder names. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class X86ProgramTests
{
    /// <summary>The program: where it was started from and the processor it runs as, each on a line of its own.</summary>
    private const string Program = """
        using System.Runtime.InteropServices;

        string path = Environment.ProcessPath ?? "none";
        Console.WriteLine($"started as {path}");
        int bits = IntPtr.Size * 8;
        Console.WriteLine($"runs as {RuntimeInformation.ProcessArchitecture}, 64-bit {bits == 64}");
        Console.WriteLine("ended");

        """;

    /// <summary>Program's line (from zero) writing where it started, its path read; the next two make <c>bits</c>, then
    /// use it.</summary>
    private const int Started = 3;

    [AvaloniaFact]
    public void A_32_bit_program_runs_debugs_and_profiles_by_its_apphost_as_x86()
    {
        // Carrying its x86 runtime, it runs on any Windows, x64 or Arm64, whatever x86 .NET is installed.
        string folder = Folder("x86-run-", "<RuntimeIdentifier>win-x86</RuntimeIdentifier>", "<SelfContained>true</SelfContained>");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                string? elsewhere = Ui.Await(window.RunAsync(), "Run's refusal of an x86 program off Windows", TimeSpan.FromMinutes(2));
                Assert.True(elsewhere?.Contains(Refusals.X86OnWindowsAlone, StringComparison.Ordinal) == true,
                    $"Run of an x86 program off Windows was not refused saying .NET runs one only on Windows, so it would fail loading its assembly: {elsewhere ?? "it ran"}");
                return;
            }
            Ui.Launched(window, window.RunAsync(), "the build and the run's start", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, () => $"the run to end; the Run window shows:\n{Output(window)}", TimeSpan.FromMinutes(2));
            string ran = Output(window);
            string apphost = AppHost(folder, "Hello");
            Assert.True(ran.Contains($"started as {apphost}\n", StringComparison.Ordinal) && ran.Contains("runs as X86, 64-bit False\n", StringComparison.Ordinal),
                $"Run did not start the x86 program by its apphost {apphost} as a 32-bit process, so a 64-bit .NET would fail to load its assembly:\n{ran}");
            Assert.DoesNotContain("not by its apphost", ran, StringComparison.Ordinal);

            window.OpenFile(Path.Combine(PathNames.Canonical(folder), "Program.cs"));
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new TextPosition(Started, 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start", TimeSpan.FromMinutes(2));
            StoppedAt(window, Started, "the breakpoint in the x86 program");
            Ui.WaitFor(() => Shown(window, "path") is not null, () => $"Variables to show the x86 program's path: {window.Debugger.Output()}");
            Assert.True(Shown(window, "path")?.Contains(apphost.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal) == true,
                $"The x86 program's local path read {Shown(window, "path") ?? "nothing"} at its breakpoint, not its apphost {apphost}");
            SteppedToBits(window, Started, "the x86 program");
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, () => $"the debugged x86 program to run to its end: {window.Debugger.Output()}");

            Ui.Launched(window, window.ProfileAsync(), "the profiled start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, () => $"the profiled program's end to show its snapshot; the Run window shows:\n{Output(window)}",
                TimeSpan.FromMinutes(2));
            string profiled = Output(window);
            Assert.True(profiled.Contains($"started as {apphost}\n", StringComparison.Ordinal) && profiled.Contains("runs as X86, 64-bit False\n", StringComparison.Ordinal),
                $"Profile did not start the x86 program by its apphost {apphost} as a 32-bit process:\n{profiled}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>On Windows a running 32-bit program is listed for Attach and attached to, a 32-bit WPF app debugs, a 32-bit
    /// program run with Hot Reload runs as x86 under dotnet watch, and one whose x86 .NET is missing has it named for the
    /// person to install before it starts, as one flow of Windows's steps, each in an instance of its own.</summary>
    [AvaloniaFact]
    public void On_Windows_32_bit_programs_attach_debug_as_wpf_apps_run_under_dotnet_watch_and_name_a_missing_x86_dotnet()
    {
        Flow.StepApart<X86ProgramTests>("a running 32 bit program is listed for attach and attached to, stopped at a breakpoint with its locals",
            t => t.A_running_32_bit_program_is_listed_for_attach_and_attached_to_stopped_at_a_breakpoint_with_its_locals());
        Flow.StepApart<X86ProgramTests>("a 32 bit wpf app stops at a breakpoint reads its locals and steps",
            t => t.A_32_bit_wpf_app_stops_at_a_breakpoint_reads_its_locals_and_steps());
        Flow.StepApart<X86ProgramTests>("a 32 bit program run with hot reload runs as x86 under dotnet watch", t => t.A_32_bit_program_run_with_hot_reload_runs_as_x86_under_dotnet_watch());
        Flow.StepApart<X86ProgramTests>("a 32 bit programs missing x86 net is named for the person to install before it starts", t => t.A_32_bit_programs_missing_x86_net_is_named_for_the_person_to_install_before_it_starts());
    }

    private void A_running_32_bit_program_is_listed_for_attach_and_attached_to_stopped_at_a_breakpoint_with_its_locals()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(".NET runs x86 programs only on Windows: elsewhere Run refuses one before its build, as the test beside this holds");
        }
        string folder = Folder("x86-attach-", "<RuntimeIdentifier>win-x86</RuntimeIdentifier>", "<SelfContained>true</SelfContained>");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Process? program = null;
        try
        {
            window.Build(BuildKind.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the build");
            string apphost = AppHost(folder, "Hello");
            Assert.True(File.Exists(apphost), $"The build made no {apphost}: {Ui.Said(window)}");
            window.Debugger.Breakpoints.Toggle(Path.Combine(PathNames.Canonical(folder), "Program.cs"), Started);

            // Held at its start by its diagnostic port, which the attach lets go once the debugger has hold of its runtime.
            ProcessStartInfo start = ChildProcesses.Command(apphost, []);
            start.Environment[RuntimeVariables.DefaultDiagnosticPortSuspend] = "1";
            start.RedirectStandardOutput = true;
            program = ChildProcesses.Start(start);
            Task<string> output = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            int id = program.Id;
            Ui.WaitFor(() => DotnetProcesses.List().Any(p => p.Id == id), "the held x86 program's runtime to start");
            DotnetProcess listed = Assert.Single(Ui.Await(window.AttachableAsync(), "the programs Attach lists"), p => p.Id == id);
            Assert.True(listed.WhyNot is null, $"Attach listed the running x86 program as one it cannot attach to: {listed.WhyNot}");

            Assert.Null(Ui.Await(window.AttachAsync(listed), "the debugger to attach to the x86 program"));
            StoppedAt(window, Started, "the breakpoint in the x86 program attached to");
            Ui.WaitFor(() => Shown(window, "path") is not null, () => $"Variables to show the attached x86 program's path: {window.Debugger.Output()}");
            Assert.True(Shown(window, "path")?.Contains(apphost.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal) == true,
                $"The x86 program attached to read its local path as {Shown(window, "path") ?? "nothing"} at its breakpoint, not its apphost {apphost}");
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, () => $"the x86 program attached to to run to its end: {window.Debugger.Output()}");
            Assert.True(program.WaitForExit(TimeSpan.FromSeconds(30).Scaled()), "The x86 program attached to did not end");
            Assert.Contains("runs as X86, 64-bit False", Ui.Await(output, "the x86 program's output to its end"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            if (program is not null)
            {
                ChildProcesses.Kill(program);
                program.Dispose();
            }
        }
    }

    /// <summary>The WPF app's window: <c>bits</c> made on <see cref="WpfStarted"/>'s next line, then shown.</summary>
    private const string WpfWindowCode = """
        namespace Plate;

        public partial class MainWindow : System.Windows.Window
        {
            public MainWindow()
            {
                InitializeComponent();
                int bits = IntPtr.Size * 8;
                Said.Text = $"runs as {bits}-bit";
            }
        }

        """;

    /// <summary>The line (from zero) of the WPF window's code where its window has its parts, before <c>bits</c> is
    /// made.</summary>
    private const int WpfStarted = 6;

    private void A_32_bit_wpf_app_stops_at_a_breakpoint_reads_its_locals_and_steps()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WPF and x86 programs run only on Windows");
        }
        string folder = TestFolders.Create("x86-wpf-");
        TestFolders.Write(folder, "Plate.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>{TestProjects.Framework}-windows</TargetFramework>
                <UseWPF>true</UseWPF>
                <ImplicitUsings>enable</ImplicitUsings>
                <PlatformTarget>x86</PlatformTarget>
                <RuntimeIdentifier>win-x86</RuntimeIdentifier>
                <SelfContained>true</SelfContained>
              </PropertyGroup>
            </Project>

            """);
        TestFolders.Write(folder, "App.xaml", """
            <Application x:Class="Plate.App" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" StartupUri="MainWindow.xaml" />

            """);
        TestFolders.Write(folder, "App.xaml.cs", "namespace Plate;\n\npublic partial class App : System.Windows.Application\n{\n}\n");
        TestFolders.Write(folder, "MainWindow.xaml", """
            <Window x:Class="Plate.MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Plate" Width="300" Height="200">
                <TextBlock x:Name="Said" />
            </Window>

            """);
        TestFolders.Write(folder, "MainWindow.xaml.cs", WpfWindowCode);
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        Restore.Run(Path.Combine(folder, "Plate.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(PathNames.Canonical(folder), "MainWindow.xaml.cs"));
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new TextPosition(WpfStarted, 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the WPF app's debug session's start", TimeSpan.FromMinutes(4));
            StoppedAt(window, WpfStarted, "the breakpoint in the x86 WPF app's window");
            Ui.WaitFor(() => Shown(window, "this") is not null, () => $"Variables to show the x86 WPF app's window as this: {window.Debugger.Output()}");
            SteppedToBits(window, WpfStarted, "the x86 WPF app");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, () => $"the debugged x86 WPF app to end at Stop: {window.Debugger.Output()}");
        }
        finally
        {
            window.Close();
        }
    }

    private void A_32_bit_program_run_with_hot_reload_runs_as_x86_under_dotnet_watch()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(".NET runs x86 programs only on Windows: elsewhere Run refuses one before its build, as the test beside this holds");
        }
        string folder = Folder("x86-watch-", "<RuntimeIdentifier>win-x86</RuntimeIdentifier>", "<SelfContained>true</SelfContained>");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.Launched(window, window.Builds.RunAsync(hotReload: true), "dotnet watch's start", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => Output(window).Contains("ended\n", StringComparison.Ordinal) || window.Runner.Ended.IsCompleted,
                () => $"the program under dotnet watch to end; the Run window shows:\n{Output(window)}", TimeSpan.FromMinutes(4));
            string ran = Output(window);
            Assert.True(ran.Contains("runs as X86, 64-bit False\n", StringComparison.Ordinal),
                $"Run with Hot Reload did not run the x86 program as a 32-bit process under dotnet watch:\n{ran}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, () => $"dotnet watch to end at Stop; the Run window shows:\n{Output(window)}", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    private void A_32_bit_programs_missing_x86_net_is_named_for_the_person_to_install_before_it_starts()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(".NET runs x86 programs only on Windows, where an x86 .NET is installed: elsewhere Run refuses one before its build, as the test beside this holds");
        }
        string folder = Folder("x86-missing-");
        // A stand-in for a machine without the x86 .NET: the folder the apphost reads first, empty.
        string none = Directory.CreateDirectory(Path.Combine(PathNames.Canonical(folder), "no-x86-dotnet")).FullName;
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            // Named apart from the project's own configuration, which its generated one takes.
            RunConfiguration configuration = new()
            {
                Name = "Without x86 .NET",
                Project = Path.Combine(PathNames.Canonical(folder), "Hello.csproj"),
                Environment = new Dictionary<string, string> { [RuntimeVariables.RootFor(AppHosts.X86)] = none },
            };
            window.SetConfigurations([configuration], configuration.Name);
            string? refused = Ui.Await(window.RunAsync(), "Run's refusal of an x86 program with no x86 .NET", TimeSpan.FromMinutes(4));
            string expected = $"Could not run Hello: {Refusals.X86RuntimeMissing(none, [(".NET Runtime", TestProjects.Framework["net".Length..])])}";
            Assert.True(refused == expected,
                $"Run of an x86 program with no x86 .NET did not name the runtime to install, so the program would fail as it starts:\n{refused ?? "it ran"}\nwhere it should say\n{expected}");
            Assert.DoesNotContain("started as", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A folder of one console project, Hello, built for x86 with the properties given, restored from nuget.org on
    /// Windows, where it runs.</summary>
    private static string Folder(string name, params string[] properties)
    {
        string folder = TestFolders.Create(name);
        TestFolders.Write(folder, "Hello.csproj", TestProjects.Console(["<ImplicitUsings>enable</ImplicitUsings>", "<PlatformTarget>x86</PlatformTarget>", .. properties]));
        TestFolders.Write(folder, "Program.cs", Program);
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        if (OperatingSystem.IsWindows())
        {
            Restore.Run(Path.Combine(folder, "Hello.csproj"));
        }
        return folder;
    }

    /// <summary>The apphost a self-contained x86 project's Debug build makes.</summary>
    private static string AppHost(string folder, string name) => Path.Combine(PathNames.Canonical(folder), "bin", "Debug", TestProjects.Framework, "win-x86", name + ".exe");

    /// <summary>Waits for the program to stop on <paramref name="line"/> (from zero) of the file shown.</summary>
    private static void StoppedAt(MainWindow window, int line, string what) =>
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.EditorArea.CodeEditor.LineMarks.Contains(new LineMark(line, LineMarkKind.ExecutionPoint)),
            () => $"{what} to stop on line {line + 1}; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: {Ui.Said(window)}\n{window.Debugger.Output()}",
            TimeSpan.FromMinutes(2));

    /// <summary>From a stop on <paramref name="line"/>, steps over it and the line making <c>bits</c>, each step stopping on
    /// the next line, and reads <c>bits</c> as a 32-bit program's.</summary>
    private static void SteppedToBits(MainWindow window, int line, string what)
    {
        Ui.Run(window, AppActions.StepOver);
        StoppedAt(window, line + 1, $"the step over in {what}");
        Ui.Run(window, AppActions.StepOver);
        StoppedAt(window, line + 2, $"the step over the line making bits in {what}");
        Ui.WaitFor(() => Shown(window, "bits") == "32",
            () => $"{what} to read bits as a 32-bit program's 32 after the step that made it, where Variables shows {Shown(window, "bits") ?? "none"}: {window.Debugger.Output()}");
    }

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string local) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == local)?.Value;

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
