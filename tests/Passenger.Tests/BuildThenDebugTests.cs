using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Debug right after a person's Build of the program's project starts no dotnet build, the Build window saying it
/// is up to date, and debugs what the Build built. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BuildThenDebugTests
{
    [AvaloniaFact]
    public void Debug_right_after_build_selected_projects_starts_no_build_and_debugs_what_it_built()
    {
        string folder = LaunchUpToDateTests.WriteSolution();
        List<string> builds = [];
        EventHandler<ChildStart> started = LaunchUpToDateTests.Counting(builds);
        ChildProcesses.Started += started;
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "App"), "App's run configuration");
            Assert.True(window.SelectConfiguration("App"));
            LaunchUpToDateTests.Choose(window, "App");
            LaunchUpToDateTests.Built(window, AppActions.BuildSelected, succeeds: true, "Build Selected Projects of App");
            int before;
            lock (builds)
            {
                before = builds.Count;
            }

            Ui.Launched(window, window.DebugAsync(), "Debug right after Build Selected Projects", LaunchUpToDateTests.Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Ended, "the debugged program to run to its end", LaunchUpToDateTests.Slow);
            int debugged;
            lock (builds)
            {
                debugged = builds.Count - before;
            }
            window.BottomPanel.Build.Flush();
            string console = window.BottomPanel.Build.Document.Snapshot.ToString();
            Assert.True(debugged == 0 && console.Contains(BuildRunController.UpToDateSaid("App"), StringComparison.Ordinal),
                $"Debug right after Build Selected Projects built App again: {debugged} dotnet builds started, and the Build window says:\n{console}");
            window.BottomPanel.Debug.Console.Flush();
            string output = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.True(output.Contains("said: hello-1 lib-1", StringComparison.Ordinal), $"Debug did not debug what the Build built, its console saying:\n{output}");
        }
        finally
        {
            ChildProcesses.Started -= started;
            window.Close();
        }
    }
}
