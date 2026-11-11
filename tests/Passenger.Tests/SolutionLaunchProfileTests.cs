using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A solution's multi-project launch profile, as Visual Studio writes it beside the solution, run and debugged from
/// Passenger's toolbar: listed among the run configurations; Run builds its projects once and runs each in the launch profile
/// it names, each in a tab of the Terminal window; Debug debugs the one set to Start, stopping at its breakpoint, and runs the
/// one set to Start without debugging; Stop ends them all; a project whose own configuration collects its logs and traces
/// collects them started by the profile too. It debugs through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class SolutionLaunchProfileTests(RestoredFolders folders) : IClassFixture<RestoredFolders>, IDisposable
{
    private const string Both = "Both";

    private string? folder;

    public void Dispose()
    {
        if (folder is not null)
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void Run_of_a_launch_profile_runs_each_project_in_its_launch_profile_and_Stop_ends_them_all()
    {
        MainWindow window = Open();
        try
        {
            Ui.Launched(window, window.RunAsync(), "the profile's projects to start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.RunCopies.Select(r => r.Name).SequenceEqual(["Api: second", "Worker"])
                    && Terminal(window.RunCopies[0]).Contains("Api says second", StringComparison.Ordinal) && Terminal(window.RunCopies[1]).Contains("Worker up", StringComparison.Ordinal),
                () => $"each project to run in its own tab, Api in the launch profile the file names ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(window.Status == "Running Both: Api: second and Worker, each in a tab of the Terminal window", $"The status bar says {window.Status}");
            Assert.False(window.Debugger.IsDebugging, "Run of the profile debugged a project");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunCopies.Count == 0 && !window.IsRunning, () => $"Stop to end both projects ({Runs(window)})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    [AvaloniaFact]
    public void Debug_of_a_launch_profile_debugs_the_project_set_to_Start_runs_the_other_and_Stop_ends_both()
    {
        MainWindow window = Open();
        try
        {
            string program = Path.Combine(folder!, "Api", "Program.cs");
            window.OpenFile(program);
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(Array.FindIndex(editor.Document.Snapshot.ToString().Split('\n'), l => l.Contains("Thread.Sleep", StringComparison.Ordinal)), 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);

            Ui.Launched(window, window.DebugAsync(), "the profile's projects to start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Tab(window, "Api: second")?.Session?.State == DebugState.Paused && Console(window, "Api: second").Contains("Api says second", StringComparison.Ordinal),
                () => $"Api to stop at its breakpoint under the debugger ({Sessions(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => window.RunCopies.Select(r => r.Name).SequenceEqual(["Worker"]) && Terminal(window.RunCopies[0]).Contains("Worker up", StringComparison.Ordinal),
                () => $"Worker to run without the debugger in a tab of the Terminal window ({Runs(window)}; {Sessions(window)})", TimeSpan.FromMinutes(1));
            Assert.True(Tab(window, "Worker") is null, $"Worker, set to start without debugging, was debugged: {Sessions(window)}");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.Tabs.Any(t => t.IsLive) && window.RunCopies.Count == 0 && !window.IsRunning,
                () => $"Stop to end both projects ({Sessions(window)}; {Runs(window)})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The window on the test's restored Shop, its profile chosen in the toolbar, listed after the projects' own.</summary>
    /// <summary>With no container engine, Api, whose own configuration collects its logs and traces, says so as the profile
    /// starts it, run or debugged, as it does started alone, and runs as it would; Worker, which collects none, says nothing.</summary>
    [AvaloniaFact]
    public void A_project_that_collects_its_logs_and_traces_says_why_none_come_with_no_engine_as_the_profile_starts_it()
    {
        MainWindow window = Open(new ContainerEngine(cli: null, search: false));
        const string Without = "Api: second runs without its logs and traces collected: No docker command was found";
        try
        {
            Assert.True(window.Session.Configurations.Collect("Api: second", on: true) && window.SelectConfiguration(Both), "Api's configuration could not collect its logs and traces");
            Ui.Launched(window, window.RunAsync(), "the profile's projects to start", TimeSpan.FromMinutes(3));
            Assert.True(window.Status?.Split(Without).Length == 2 && !window.Status.Contains("Worker runs without", StringComparison.Ordinal),
                $"Run of the profile did not say once that Api's logs and traces are not collected, as Api's own run does: {window.Status}");
            Ui.WaitFor(() => window.RunCopies.Count == 2 && Terminal(window.RunCopies[0]).Contains("Api says second", StringComparison.Ordinal),
                () => $"Api to run as it would ({Runs(window)})", TimeSpan.FromMinutes(1));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunCopies.Count == 0 && !window.IsRunning, () => $"Stop to end both projects ({Runs(window)})", TimeSpan.FromMinutes(1));

            Ui.Launched(window, window.DebugAsync(), "the profile's projects to start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Console(window, "Api: second").Contains(Without, StringComparison.Ordinal),
                () => $"Api's Debug tab to say why its logs and traces are not collected ({Sessions(window)}: {Console(window, "Api: second")})", TimeSpan.FromMinutes(1));
            Assert.Empty(window.Session.Dashboards);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Against a Linux engine that holds the dashboard's image (no test pulls it), Api, whose own configuration
    /// collects its logs and traces, has Aspire's dashboard brought up for it as the profile runs it, its run saying where they
    /// go, and none for Worker; Stop takes the dashboard down with the programs.</summary>
    [AvaloniaFact]
    public void A_project_that_collects_its_logs_and_traces_has_its_dashboard_as_the_profile_runs_it_and_Stop_takes_it_down()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(!Ui.AwaitPumped(Waiting.Apart(() => ContainerEngine.Shared.RunAsync(["image", "inspect", ContainerTelemetry.Image], TimeSpan.FromSeconds(30).Scaled())),
            $"docker image inspect {ContainerTelemetry.Image}", TimeSpan.FromSeconds(30) + TestFolders.Spare).Succeeded,
            $"The engine lacks the dashboard's image, which no test pulls: pull {ContainerTelemetry.Image} to run it");
        MainWindow window = Open(ContainerEngine.Shared);
        try
        {
            Assert.True(window.Session.Configurations.Collect("Api: second", on: true) && window.SelectConfiguration(Both), "Api's configuration could not collect its logs and traces");
            Ui.Launched(window, window.RunAsync(), "the profile's projects to start with Api's dashboard", TimeSpan.FromMinutes(5));
            Passenger.App.Telemetry.RunDashboard dashboard = Assert.Single(window.Session.Dashboards);
            Assert.True(dashboard.Name == "Api: second" && window.Status?.Contains($"Api: second's logs and traces go to Aspire's dashboard ({dashboard.ProjectName})", StringComparison.Ordinal) == true,
                $"Run of the profile did not bring up Api's dashboard as Api's own run does: {dashboard.Name}; {window.Status}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunCopies.Count == 0 && !window.IsRunning && window.Session.Dashboards.Count == 0,
                () => $"Stop to end both projects and Api's dashboard ({Runs(window)}; {window.Session.Dashboards.Count} dashboards)", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    private MainWindow Open(ContainerEngine? engine = null)
    {
        folder = folders.Take("launch-", root => Shop(root, waits: true));
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, Engine = engine ?? ContainerEngine.Shared };
        window.Show();
        window.OpenFolder(folder);
        // Listed as the solution is read, it follows its projects' own once they load.
        Ui.WaitFor(() => window.Configurations.Select(c => c.Name).SequenceEqual(["Api: first", "Api: second", "Worker", Both]),
            () => $"the solution's launch profile to be listed after its projects' own ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        Assert.True(window.SelectConfiguration(Both), "The profile could not be chosen");
        return window;
    }

    /// <summary>Shop: Api, which says the greeting its launch profile gives (its first, then its second), and Worker, which
    /// says it is up, each then waiting where <paramref name="waits"/>, else ending (Worker with 3); in Shop.slnx, beside it
    /// Shop.slnLaunch as Visual Studio writes it, its profile Both starting Api in its second launch profile and Worker
    /// without debugging. The solution, to restore.</summary>
    public static string Shop(string root, bool waits)
    {
        string wait = waits ? "Thread.Sleep(Timeout.Infinite);\n" : string.Empty;
        TestFolders.Write(root, "Api/Api.csproj", ConsoleProject());
        TestFolders.Write(root, "Api/Program.cs", $"string greeting = Environment.GetEnvironmentVariable(\"GREETING\") ?? \"nothing\";\nConsole.WriteLine($\"Api says {{greeting}}\");\n{wait}");
        TestFolders.Write(root, "Api/Properties/launchSettings.json", """
            {
              "profiles": {
                "first": { "commandName": "Project", "environmentVariables": { "GREETING": "first" } },
                "second": { "commandName": "Project", "environmentVariables": { "GREETING": "second" } }
              }
            }
            """);
        TestFolders.Write(root, "Worker/Worker.csproj", ConsoleProject());
        TestFolders.Write(root, "Worker/Program.cs", $"Console.WriteLine(\"Worker up\");\n{wait}{(waits ? string.Empty : "return 3;\n")}");
        TestFolders.Write(root, "Shop.slnx", "<Solution>\n  <Project Path=\"Api/Api.csproj\" />\n  <Project Path=\"Worker/Worker.csproj\" />\n</Solution>\n");
        TestFolders.Write(root, "Shop.slnLaunch", "[\n  {\n    \"Name\": \"Both\",\n    \"Projects\": [\n      {\n        \"Path\": \"Api\\\\Api.csproj\",\n"
            + "        \"Action\": \"Start\",\n        \"DebugTarget\": \"second\"\n      },\n      {\n        \"Path\": \"Worker\\\\Worker.csproj\",\n"
            + "        \"Action\": \"StartWithoutDebugging\"\n      }\n    ]\n  }\n]");
        return Path.Combine(root, "Shop.slnx");
    }

    /// <summary>A console app on the runtime Passenger itself runs on, which every machine that runs the tests has.</summary>
    private static string ConsoleProject() => TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>")
        .Replace($"<TargetFramework>{TestProjects.Framework}</TargetFramework>", "<TargetFramework>net11.0</TargetFramework>", StringComparison.Ordinal);

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll));
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the projects to end ({Sessions(window)}; {Runs(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>What a program run in a tab of the Terminal window has written there, its scrollback and its screen.</summary>
    private static string Terminal(TerminalRun run) =>
        string.Join('\n', run.Session.Screen.Scrollback.Select(l => l.Text()).Concat(run.Session.Screen.Lines.Select(l => l.Text())));

    private static string Runs(MainWindow window) => string.Join(", ", window.RunCopies.Select(r => $"{r.Name}: {string.Join(" / ", Terminal(r).Split('\n').Where(l => l.Length > 0).TakeLast(2))}"));

    private static DebugTab? Tab(MainWindow window, string name) => window.Debugger.Tabs.FirstOrDefault(t => t.Name == name && t.IsLive);

    private static string Console(MainWindow window, string name) =>
        Tab(window, name)?.Session is { } session ? window.Debugger.Output(session).ToString() : string.Empty;

    private static string Sessions(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name}: {t.Session?.State.ToString() ?? "none"}"));
}
