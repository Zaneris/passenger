using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A solution's multi-project launch profile, as Visual Studio writes it beside the solution, run and debugged from
/// Passenger's toolbar: listed among the run configurations; Run builds its projects once and runs each in the launch profile
/// it names, each in a tab of the Run window; Debug debugs the one set to Start, stopping at its breakpoint, and runs the
/// one set to Start without debugging; Stop ends them all; a project whose own configuration collects its logs and traces
/// collects them started by the profile too; and a run of several parts in the folder's passenger-runs.json starts a command
/// as a step and each part once the one before it is ready, restarts one part alone and ends all on Stop, and debugs its
/// .NET parts, a part waiting for a debugged one's line hearing it from its start. It debugs through the window, so run
/// alone.</summary>
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

    /// <summary>A solution's launch profile runs each project in its own profile, debugs the one set to Start beside
    /// the others, Stop ending them all, and a project that collects its logs says why none come with no engine, as one
    /// flow, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void A_solutions_launch_profile_runs_and_debugs_each_project_and_Stop_ends_them_all()
    {
        Flow.StepApart("Run of a launch profile runs each project in its launch profile and Stop ends them all", () => new SolutionLaunchProfileTests(folders), t => t.Run_of_a_launch_profile_runs_each_project_in_its_launch_profile_and_Stop_ends_them_all());
        Flow.StepApart("Debug of a launch profile debugs the project set to Start runs the other and Stop ends both", () => new SolutionLaunchProfileTests(folders), t => t.Debug_of_a_launch_profile_debugs_the_project_set_to_Start_runs_the_other_and_Stop_ends_both());
        Flow.StepApart("a project that collects its logs and traces says why none come with no engine as the profile starts it", () => new SolutionLaunchProfileTests(folders), t => t.A_project_that_collects_its_logs_and_traces_says_why_none_come_with_no_engine_as_the_profile_starts_it());
        Flow.StepApart("a run of several parts starts each once the one before is ready restarts one alone and Stop ends them all", () => new SolutionLaunchProfileTests(folders), t => t.A_run_of_several_parts_starts_each_once_the_one_before_is_ready_restarts_one_alone_and_Stop_ends_them_all());
    }

    private void Run_of_a_launch_profile_runs_each_project_in_its_launch_profile_and_Stop_ends_them_all()
    {
        MainWindow window = Open();
        try
        {
            Ui.Launched(window, window.RunAsync(), "the profile's projects to start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.RunParts.Select(r => r.Name).SequenceEqual(["Api: second", "Worker"])
                    && Text(window.RunParts[0]).Contains("Api says second", StringComparison.Ordinal) && Text(window.RunParts[1]).Contains("Worker up", StringComparison.Ordinal),
                () => $"each project to run in its own tab, Api in the launch profile the file names ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(window.Status == "Running Both: Api: second and Worker, each in a tab of the Run window", $"The status bar says {window.Status}");
            Assert.False(window.Debugger.IsDebugging, "Run of the profile debugged a project");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunParts.All(r => !r.IsRunning) && !window.IsRunning, () => $"Stop to end both projects ({Runs(window)})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    private void Debug_of_a_launch_profile_debugs_the_project_set_to_Start_runs_the_other_and_Stop_ends_both()
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
            Ui.WaitFor(() => window.RunParts.Select(r => r.Name).SequenceEqual(["Worker"]) && Text(window.RunParts[0]).Contains("Worker up", StringComparison.Ordinal),
                () => $"Worker to run without the debugger in a tab of the Run window ({Runs(window)}; {Sessions(window)})", TimeSpan.FromMinutes(1));
            Assert.True(Tab(window, "Worker") is null, $"Worker, set to start without debugging, was debugged: {Sessions(window)}");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.Tabs.Any(t => t.IsLive) && window.RunParts.All(r => !r.IsRunning) && !window.IsRunning,
                () => $"Stop to end both projects ({Sessions(window)}; {Runs(window)})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A run of several parts in the folder's passenger-runs.json: dotnet --version as a step, then Api, which the
    /// part after it waits for until it says its greeting, then Worker. Run starts the step and Api only once the step ended
    /// with exit code 0, and Worker only once Api said its line, each in a tab of the Run window; Worker's Rerun restarts it
    /// alone while Api runs on in the same process, and Stop ends every part. Debug of it debugs Api and Worker, each in a
    /// Debug tab, Worker started once Api's greeting is heard though Api says it as it starts, before its launch returns.</summary>
    private void A_run_of_several_parts_starts_each_once_the_one_before_is_ready_restarts_one_alone_and_Stop_ends_them_all()
    {
        MainWindow window = Open();
        try
        {
            string host = Dotnet.Host().Replace("\\", "/", StringComparison.Ordinal);
            TestFolders.Write(folder!, RunsFile.FileName, $$"""
                {
                  "passengerRuns": 1,
                  "runs": [
                    {
                      "name": "Steps",
                      "parts": [
                        { "command": "\"{{host}}\" --version", "name": "version", "readyWhen": "exited" },
                        { "project": "Api/Api.csproj", "launchProfile": "second", "readyWhen": { "line": "says sec.nd" } },
                        { "project": "Worker/Worker.csproj" }
                      ]
                    }
                  ]
                }
                """);
            Ui.WaitFor(() => window.SelectConfiguration("Steps"), () => $"the folder's run to be listed ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
            Ui.Launched(window, window.RunAsync(), "the run's parts to start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.RunParts.Select(r => r.Name).SequenceEqual(["version", "Api: second", "Worker"]) && Text(window.RunParts[2]).Contains("Worker up", StringComparison.Ordinal),
                () => $"each part to start in its own tab of the Run window ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            PartRun version = window.RunParts[0];
            PartRun api = window.RunParts[1];
            PartRun worker = window.RunParts[2];
            Assert.True(version is { State: PartState.Ended, Ended.IsCompletedSuccessfully: true } && Text(version).Contains("Process finished with exit code 0", StringComparison.Ordinal),
                $"The step did not run to its end before the parts after it started: {Text(version)}");
            Assert.True(window.Status == "Running Steps: version, Api: second and Worker, each in a tab of the Run window", $"The status bar says {window.Status}");
            Assert.True(api.IsRunning && worker.IsRunning, $"A part ended once the next started: {Runs(window)}");

            int? apiProcess = api.ProcessId;
            int? workerProcess = worker.ProcessId;
            Ui.Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Name == "runPart3Tab"));
            Ui.Click(worker.Console.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => Avalonia.Controls.ToolTip.GetTip(b) is "Rerun" && b.IsEffectivelyVisible));
            Ui.WaitFor(() => worker.IsRunning && worker.ProcessId != workerProcess && Text(worker).Contains("Worker up", StringComparison.Ordinal),
                () => $"Worker's Rerun to start it again in its tab ({Runs(window)}; {window.Status})", TimeSpan.FromMinutes(2));
            Assert.True(api.IsRunning && api.ProcessId == apiProcess, $"Worker's restart ended Api, which was to run on: {Runs(window)}");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunParts.All(r => !r.IsRunning) && !window.IsRunning, () => $"Stop to end every part ({Runs(window)})", TimeSpan.FromMinutes(1));

            // Api's launch held once its program runs, until its greeting is in its console: Worker waits for that line all the same.
            TaskCompletionSource greeted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Task held = window.Builds.HoldNextLaunch(greeted.Task, opened: true);
                Task<string?> debugging = window.DebugAsync();
                // Not settled: the start holds the window's background work until it has started every part.
                Ui.PumpUntil(() => held.IsCompleted || debugging.IsCompleted, "Api's launch to be held once its program runs", TimeSpan.FromMinutes(3));
                Ui.PumpUntil(() => Console(window, "Api: second").Contains("Api says second", StringComparison.Ordinal) || debugging.IsCompleted,
                    "Api to say its greeting under the debugger as its launch is held", TimeSpan.FromMinutes(1));
                greeted.SetResult();
                Ui.Launched(window, debugging, "Worker to start once Api's greeting, said before its launch returned, is heard", TimeSpan.FromMinutes(1));
            }
            finally
            {
                greeted.TrySetResult();
            }
            Ui.WaitFor(() => Console(window, "Api: second").Contains("Api says second", StringComparison.Ordinal) && Console(window, "Worker").Contains("Worker up", StringComparison.Ordinal),
                () => $"Api and Worker to run under the debugger, each in its Debug tab ({Sessions(window)}; {Runs(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.True(window.RunParts.Select(r => r.Name).SequenceEqual(["version"]), $"A .NET part under the debugger also ran in a tab of the Run window: {Runs(window)}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.Tabs.Any(t => t.IsLive) && window.RunParts.All(r => !r.IsRunning) && !window.IsRunning,
                () => $"Stop to end every part ({Sessions(window)}; {Runs(window)})", TimeSpan.FromMinutes(1));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>With no container engine, Api, whose own configuration collects its logs and traces, says so as the profile
    /// starts it, run or debugged, as it does started alone, and runs as it would; Worker, which collects none, says nothing.</summary>
    private void A_project_that_collects_its_logs_and_traces_says_why_none_come_with_no_engine_as_the_profile_starts_it()
    {
        MainWindow window = Open(new ContainerEngine(cli: null, search: false));
        const string Without = "Api: second runs without its logs and traces collected: No docker command was found";
        try
        {
            Assert.True(window.Session.Configurations.Collect("Api: second", on: true) && window.SelectConfiguration(Both), "Api's configuration could not collect its logs and traces");
            Ui.Launched(window, window.RunAsync(), "the profile's projects to start", TimeSpan.FromMinutes(3));
            Assert.True(window.Status?.Split(Without).Length == 2 && !window.Status.Contains("Worker runs without", StringComparison.Ordinal),
                $"Run of the profile did not say once that Api's logs and traces are not collected, as Api's own run does: {window.Status}");
            Ui.WaitFor(() => window.RunParts.Count == 2 && Text(window.RunParts[0]).Contains("Api says second", StringComparison.Ordinal),
                () => $"Api to run as it would ({Runs(window)})", TimeSpan.FromMinutes(1));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.RunParts.All(r => !r.IsRunning) && !window.IsRunning, () => $"Stop to end both projects ({Runs(window)})", TimeSpan.FromMinutes(1));

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
            Ui.WaitFor(() => window.RunParts.All(r => !r.IsRunning) && !window.IsRunning && window.Session.Dashboards.Count == 0,
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
            Assert.Null(window.Actions.Run(AppActions.StopAll)?.Shown);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the projects to end ({Sessions(window)}; {Runs(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>What a part run in a tab of the Run window has written there.</summary>
    private static string Text(PartRun part) => part.Runner.Shown().ToString();

    private static string Runs(MainWindow window) => string.Join(", ", window.RunParts.Select(r => $"{r.Name} ({r.Said}): {string.Join(" / ", Text(r).Split('\n').Where(l => l.Length > 0).TakeLast(2))}"));

    private static DebugTab? Tab(MainWindow window, string name) => window.Debugger.Tabs.FirstOrDefault(t => t.Name == name && t.IsLive);

    private static string Console(MainWindow window, string name) =>
        Tab(window, name)?.Session is { } session ? window.Debugger.Output(session).ToString() : string.Empty;

    private static string Sessions(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name}: {t.Session?.State.ToString() ?? "none"}"));
}
