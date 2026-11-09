using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Run, Debug and Profile start a project through the apphost the SDK made for it, as the project's own program
/// on the .NET Passenger builds with: the program's path is its apphost's, the variable naming that .NET for its processor
/// is set, and on Windows the manifest the apphost carries applies (its code page UTF-8, which dotnet's own process is not
/// given). Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class AppHostLaunchTests : IClassFixture<RestoredFolders>, IDisposable
{
    /// <summary>The program: where it was started from, the .NET its processor's variable names, its process's code page
    /// on Windows, each on a line of its own.</summary>
    private const string Program = """
        using System.Runtime.InteropServices;

        string path = Environment.ProcessPath ?? "none";
        Console.WriteLine($"started as {path}");
        string processor = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        Console.WriteLine($"root {Environment.GetEnvironmentVariable("DOTNET_ROOT_" + processor)}");
        Console.WriteLine($"code page {(OperatingSystem.IsWindows() ? Native.GetACP() : 0)}");
        Console.WriteLine("ended");

        static class Native
        {
            [DllImport("kernel32.dll")]
            public static extern uint GetACP();
        }

        """;

    /// <summary>A manifest whose UTF-8 code page Windows gives only a process started from the file that carries it.</summary>
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
          <assemblyIdentity version="1.0.0.0" name="Hello.app" />
          <application xmlns="urn:schemas-microsoft-com:asm.v3">
            <windowsSettings>
              <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">UTF-8</activeCodePage>
            </windowsSettings>
          </application>
        </assembly>

        """;

    private readonly RestoredFolders folders;

    /// <summary>The test's own folder, once it has taken one.</summary>
    private string? taken;

    public AppHostLaunchTests(RestoredFolders folders) => this.folders = folders;

    public void Dispose()
    {
        if (taken is not null)
        {
            TestFolders.Delete(taken);
        }
    }

    /// <summary>The test's own folder of one console project, Hello, with its manifest, restored.</summary>
    private string Folder => taken ??= folders.Take("apphost-", made =>
    {
        TestFolders.Write(made, "Hello.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>", "<ApplicationManifest>app.manifest</ApplicationManifest>"));
        TestFolders.Write(made, "app.manifest", Manifest);
        TestFolders.Write(made, "Program.cs", Program);
        return Path.Combine(made, "Hello.csproj");
    });

    /// <summary>The apphost the project's build makes.</summary>
    private string AppHost => Path.Combine(PathNames.Canonical(Folder), "bin", "Debug", TestProjects.Framework, OperatingSystem.IsWindows() ? "Hello.exe" : "Hello");

    [AvaloniaFact]
    public void Run_debug_and_profile_start_the_projects_apphost_on_the_net_passenger_builds_with_and_on_windows_its_manifest_applies()
    {
        MainWindow window = Ui.OpenFolder(Folder, languageServer: false);
        try
        {
            Ui.Launched(window, window.RunAsync(), "the build and the run's start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, () => $"the run to end; the Run window shows:\n{Output(window)}", TimeSpan.FromMinutes(2));
            string ran = Output(window);
            Assert.True(ran.Contains($"started as {AppHost}\n", StringComparison.Ordinal),
                $"Run did not start the program from its apphost {AppHost}, so on Windows its manifest, icon and name are lost:\n{ran}");
            Assert.True(ran.Contains($"root {Dotnet.Root()}\n", StringComparison.Ordinal),
                $"The apphost was not told the .NET Passenger builds with, {Dotnet.Root()}, so it may run on another:\n{ran}");
            Assert.True(!OperatingSystem.IsWindows() || ran.Contains("code page 65001\n", StringComparison.Ordinal),
                $"The apphost's manifest did not apply, its UTF-8 code page not the process's:\n{ran}");

            window.OpenFile(Path.Combine(PathNames.Canonical(Folder), "Program.cs"));
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new TextPosition(3, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && ShownPath(window) is not null,
                () => $"the stop at the breakpoint with the program's path; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: {Ui.Said(window)}");
            // The debugger shows a string as C# writes one, each backslash of a Windows path doubled.
            Assert.True(ShownPath(window)?.Contains(AppHost.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal) == true,
                $"Debug stopped a program started as {ShownPath(window)}, not from its apphost {AppHost}, so on Windows its manifest, icon and name are lost");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end");

            Ui.Launched(window, window.ProfileAsync(), "the profiled start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, () => $"the profiled program's end to show its snapshot; the Run window shows:\n{Output(window)}",
                TimeSpan.FromMinutes(2));
            string profiled = Output(window);
            Assert.True(profiled.Contains($"started as {AppHost}\n", StringComparison.Ordinal)
                && (!OperatingSystem.IsWindows() || profiled.Contains("code page 65001\n", StringComparison.Ordinal)),
                $"Profile did not start the program from its apphost {AppHost} with its manifest applying, so on Windows its manifest, icon and name are lost:\n{profiled}");
        }
        finally
        {
            window.Close();
        }
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>The program's path as Variables shows it where it stopped; null before it shows one.</summary>
    private static string? ShownPath(MainWindow window) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == "path")?.Value;
}
