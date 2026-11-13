using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The build configuration chosen reaches what starts a program as Run does: Debug stops in the chosen
/// configuration's build, Profile starts it, and Run with Hot Reload has dotnet watch build and run it, none building
/// anything as Debug. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BuildConfigurationLaunchTests : IDisposable
{
    /// <summary>The program: the folder it was built into, then a line of its own for Debug's build and another for every
    /// other's.</summary>
    private const string Program = """
        System.Console.WriteLine($"built into {System.AppContext.BaseDirectory}");
        #if DEBUG
        System.Console.WriteLine("debug");
        #else
        System.Console.WriteLine("optimized");
        #endif

        """;

    /// <summary>The line that has code only in a build without DEBUG, counted from 0.</summary>
    private const int OptimizedLine = 4;

    private readonly string folder = PathNames.Canonical(TestFolders.Create("configuration-launch-"));

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Debug_profile_and_hot_reload_under_release_start_the_programs_release_build()
    {
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console());
        string program = TestFolders.Write(folder, "Hello/Program.cs", Program);
        Restore.Run(Path.Combine(folder, "Hello", "Hello.csproj"));
        string release = Path.Combine(folder, "Hello", "bin", "Release", TestProjects.Framework) + Path.DirectorySeparatorChar;
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => BuildConfigurationTests.Offered(window) == "Debug, Release", () => $"Debug and Release offered, where the widget offers {BuildConfigurationTests.Offered(window)}");
            BuildConfigurationTests.Choose(window, "Release");

            window.OpenFile(program);
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new TextPosition(OptimizedLine, 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused, StopReason: Passenger.Dap.StopReasons.Breakpoint } || window.Debugger.Session is null or { State: DebugState.Ended },
                () => $"the stop at the breakpoint on the line only a Release build has code for; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: {Ui.Said(window)}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.Debugger.Session is { State: DebugState.Paused },
                $"Debug under Release ran past the line only a Release build has code for, so it debugged another build:\n{Debugged(window)}");
            Ui.WaitFor(() => window.EditorArea.CodeEditor.LineMarks.Contains(new LineMark(OptimizedLine, LineMarkKind.ExecutionPoint)), "the stop to show on the Release build's line");
            Assert.True(Debugged(window).Contains($"built into {release}", StringComparison.Ordinal),
                $"Debug under Release did not start the program its Release build made, in {release}:\n{Debugged(window)}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end");

            Ui.Launched(window, window.ProfileAsync(), "the profiled start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, () => $"the profiled program's end to show its snapshot; the Run window shows:\n{Output(window)}",
                TimeSpan.FromMinutes(2));
            Assert.True(Output(window).Contains($"built into {release}", StringComparison.Ordinal) && Output(window).Contains("optimized", StringComparison.Ordinal),
                $"Profile under Release did not start the program its Release build made, in {release}:\n{Output(window)}");

            window.ApplySettings(window.Settings with { HotReload = true });
            Assert.True(window.Perform(AppActions.RunProgram), $"Run with Hot Reload did not start: {window.Status}");
            Ui.WaitFor(() => Output(window).Contains("Waiting for a file to change", StringComparison.Ordinal),
                () => $"dotnet watch to run the program and wait for a change; the status bar says \"{window.Status}\" and the Run window:\n{Output(window)}", TimeSpan.FromMinutes(3));
            Assert.True(Output(window).Contains($"built into {release}", StringComparison.Ordinal) && Output(window).Contains("optimized", StringComparison.Ordinal),
                $"Run with Hot Reload under Release did not have dotnet watch run the program's Release build, in {release}:\n{Output(window)}");
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "dotnet watch to end");

            Assert.False(Directory.Exists(Path.Combine(folder, "Hello", "bin", "Debug")), "Debug, Profile or Hot Reload under Release built the program as Debug");
        }
        finally
        {
            window.Close();
        }
    }

    private static string Output(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    private static string Debugged(MainWindow window)
    {
        window.BottomPanel.Debug.Console.Flush();
        return window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
    }
}
