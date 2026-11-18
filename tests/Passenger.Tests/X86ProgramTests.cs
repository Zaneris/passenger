using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A program built for 32-bit x86 runs as Windows runs it, by its own apphost on an x86 .NET, never handed to the
/// 64-bit .NET, which cannot load its assembly; Debug is refused before anything starts, saying why, as Passenger's debugger
/// follows only 64-bit programs; an x86 .NET lacking what the program runs on is named before it starts; and off Windows
/// it is refused, as .NET runs x86 programs on Windows alone. Profile records it as Run starts it, and Run with Hot Reload
/// runs it under dotnet watch. Its packs come from nuget.org, which the test folder names.</summary>
public sealed class X86ProgramTests
{
    /// <summary>The program: where it was started from and the processor it runs as, each on a line of its own.</summary>
    private const string Program = """
        using System.Runtime.InteropServices;

        Console.WriteLine($"started as {Environment.ProcessPath}");
        Console.WriteLine($"runs as {RuntimeInformation.ProcessArchitecture}, 64-bit {Environment.Is64BitProcess}");
        Console.WriteLine("ended");

        """;

    [AvaloniaFact]
    public void A_32_bit_program_runs_and_profiles_by_its_apphost_as_x86_and_debug_is_refused_saying_why()
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
            string apphost = Path.Combine(PathNames.Canonical(folder), "bin", "Debug", TestProjects.Framework, "win-x86", "Hello.exe");
            Assert.True(ran.Contains($"started as {apphost}\n", StringComparison.Ordinal) && ran.Contains("runs as X86, 64-bit False\n", StringComparison.Ordinal),
                $"Run did not start the x86 program by its apphost {apphost} as a 32-bit process, so a 64-bit .NET would fail to load its assembly:\n{ran}");
            Assert.DoesNotContain("not by its apphost", ran, StringComparison.Ordinal);

            string? refused = Ui.Await(window.DebugAsync(), "Debug's refusal of an x86 program", TimeSpan.FromMinutes(2));
            Assert.True(refused == Refusals.X86Undebugged("Hello") && !window.Debugger.IsDebugging,
                $"Debug of an x86 program was not refused before it started, saying Passenger's debugger follows only 64-bit programs: {refused ?? "it started"}");

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

    [AvaloniaFact]
    public void A_32_bit_program_run_with_hot_reload_runs_as_x86_under_dotnet_watch()
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

    [AvaloniaFact]
    public void A_32_bit_programs_missing_x86_net_is_named_for_the_person_to_install_before_it_starts()
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

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
