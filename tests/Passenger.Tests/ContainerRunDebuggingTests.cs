using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Debug of a container run configuration as a person uses it, against a real engine: a Compose solution's two
/// ASP.NET Core services held at their start, a session each in its own Debug tab (the adapter in each container over
/// docker exec), each stopping at a breakpoint in Main and in a request, stepped and evaluated, its tab's console its
/// container's log, and the Containers window's Restart, Stop and Remove going through the run; Hot Reload applied to a
/// paused service; Run with Hot Reload applying an edit through the debugger and restarting a service for one it cannot
/// apply; the gutter's Debug of one service; and images mode stopping through the path map. Each skips, saying why, where
/// no Linux engine runs; the tools are published for the engine's processor where a dev build finds them, and what the runs
/// make carries the run's labels.</summary>
[Collection(Alone.Name)]
public sealed class ContainerRunDebuggingTests(ContainerRunDebuggingTests.Tools tools) : IClassFixture<ContainerRunDebuggingTests.Tools>
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Each service's Program.cs: Main's breakpoint on its second line, the request's inside its handler.</summary>
    private const string Program = """
        WebApplication app = WebApplication.Create(args);
        string name = app.Environment.ApplicationName;
        app.MapGet("/", () =>
        {
            string said = Message.Text;
            return said;
        });
        app.Run();

        """;

    private const int InMain = 1;
    private const int InRequest = 4;

    /// <summary>Two services debugged at once, each stopping in Main and in a request, stepped and evaluated; then the
    /// Containers window's actions on the debugged run go through it: the project's Restart ends the sessions, restarts every
    /// service and attaches to the debugged ones again; a debugged service's Stop and Remove end its session before its
    /// container stops or goes (an adapter losing it would be the IDE's error, which fails the test), the run going on; and
    /// the project's Stop is the run's, its sessions ended before its containers go.</summary>
    [AvaloniaFact]
    public void Two_services_debugged_at_once_stop_in_Main_and_in_a_request_and_the_Containers_windows_Restart_Stop_and_Remove_go_through_the_run()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("both");
        MainWindow window = ContainerRunsTests.Open(folder);
        foreach (string app in (string[])["Web", "Api"])
        {
            window.Debugger.Breakpoints.Toggle(Source(folder, app), InMain);
            window.Debugger.Breakpoints.Toggle(Source(folder, app), InRequest);
        }
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        try
        {
            Debugged(window, "web and api to come up under the debugger");
            DebugTab web = Tab(window, "web");
            DebugTab api = Tab(window, "api");
            Assert.False(web.Session!.IsLocal, "A service's program was taken for one of this machine's");
            Assert.Equal(["api", "cache", "web"], ContainerRunsTests.States(project).Keys.Order(StringComparer.Ordinal));

            // The attach let each go from its start, so each stops at Main's breakpoint.
            StoppedAt(window, web, Source(folder, "Web"), InMain, "web to stop in Main");
            StoppedAt(window, api, Source(folder, "Api"), InMain, "api to stop in Main");
            Assert.Equal("\"Development\"", Evaluate(web.Session, "app.Environment.EnvironmentName"));
            Chosen(window, web);
            Ui.Run(window, AppActions.StepOver);
            StoppedAt(window, web, Source(folder, "Web"), InMain + 1, "the step over in web");
            Assert.Equal("\"Web\"", Evaluate(web.Session, "name"));
            Assert.Equal("\"Api\"", Evaluate(api.Session!, "app.Environment.ApplicationName"));
            Resume(window, web);
            Resume(window, api);

            foreach ((DebugTab tab, string service, string app) in (ReadOnlySpan<(DebugTab, string, string)>)[(web, "web", "Web"), (api, "api", "Api")])
            {
                Task<string> answer = Get(ContainerRunsTests.Published(project, service));
                StoppedAt(window, tab, Source(folder, app), InRequest, $"{service}'s request to stop in its handler");
                Assert.Equal($"\"{service} 1\"", Evaluate(tab.Session!, "Message.Text"));
                Resume(window, tab);
                Assert.Equal($"{service} 1", Ui.Await(answer, $"{service}'s answer", TimeSpan.FromMinutes(1)));
            }
            // Each tab's console is its container's log, which an attach gets none of through the debugger.
            Ui.WaitFor(() => window.Debugger.Output(web.Session).ToString().Contains("Now listening on", StringComparison.Ordinal), "web's log in its console");
            Assert.DoesNotContain(DebugController.AttachedConsole, window.Debugger.Output(api.Session).ToString(), StringComparison.Ordinal);

            foreach (string app in (string[])["Web", "Api"])
            {
                window.Debugger.Breakpoints.Toggle(Source(folder, app), InMain);
                window.Debugger.Breakpoints.Toggle(Source(folder, app), InRequest);
            }
            ContainersView view = ContainerServicesTests.Shown(window);
            string cacheStarted = ContainerRunsTests.Started(project, "cache");
            DebugSession webBefore = web.Session!;
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Project && r.Project?.Name == project), $"{project}'s row");
            Assert.True(window.Perform(AppActions.ContainerRestart), $"Restart could not run on {project}: {window.Status}");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.ContainerRestart) is null, $"{project}'s restart", TimeSpan.FromMinutes(3));
            Assert.Equal("Compose: compose.yaml: restarted its services", window.Status);
            Assert.NotEqual(cacheStarted, ContainerRunsTests.Started(project, "cache"));
            web = Tab(window, "web");
            api = Tab(window, "api");
            Ui.WaitFor(() => web.Session is { State: DebugState.Running } && web.Session != webBefore && api.Session is { State: DebugState.Running },
                "web and api to run under the debugger again");

            ContainerServicesTests.Act(window, view, project, "api", AppActions.ContainerStop);
            Ui.WaitFor(() => !api.IsLive && ContainerRunsTests.StatesNow(project)?.GetValueOrDefault("api") == "exited", "api's session to end and its container to stop",
                TimeSpan.FromMinutes(2));
            Assert.True(web.IsLive, "Stop of api ended web's session");
            ContainerServicesTests.Act(window, view, project, "web", AppActions.ContainerRemove);
            Ui.WaitFor(() => !web.IsLive && ContainerRunsTests.StatesNow(project) is { } states && !states.ContainsKey("web"), "web's session to end and its container to go", TimeSpan.FromMinutes(2));
            Assert.Equal("running", ContainerRunsTests.States(project)["cache"]);

            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Project && r.Project?.Name == project), $"{project}'s row");
            Assert.True(window.Perform(AppActions.ContainerStop), $"Stop could not run on {project}: {window.Status}");
            Docker.TakenDown(project, "the project's Stop to take the run down");
            Ui.WaitFor(() => !window.IsRunning, "the run's log to stop being followed");
            Assert.Null(window.Builds.Containers);
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "both sessions to end with the run");
        window.Close();
    }

    [AvaloniaFact]
    public void Hot_Reload_reaches_a_paused_service_and_Run_with_Hot_Reload_applies_an_edit_and_restarts_a_service_for_a_rude_one()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("reload");
        MainWindow window = ContainerRunsTests.Open(folder, languageServer: true);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        string message = Path.Combine(folder, "Web", "Message.cs");
        try
        {
            window.OpenFile(Path.Combine(folder, "compose.yaml"));
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument compose = window.EditorArea.Active!;
            RunTarget target = window.RunTargets(compose).Single(t => t.Service == "web");
            window.Debugger.Breakpoints.Toggle(Source(folder, "Web"), InRequest);
            Assert.Null(Ui.Await(window.RunTarget(target, RunStart.Debug), "web to come up under the debugger", TimeSpan.FromMinutes(5)));
            DebugTab web = Tab(window, "web");
            Assert.DoesNotContain(window.Debugger.Tabs, t => t.Name == "api");

            Task<string> answer = Get(ContainerRunsTests.Published(project, "web"));
            StoppedAt(window, web, Source(folder, "Web"), InRequest, "web's request to stop in its handler");
            window.OpenFile(message);
            Ui.Settle();
            Typing.Replace(window, "\"web 1\"", "\"web 2\"");
            Assert.NotNull(window.Debugger.HotReload);
            Resume(window, web);
            Assert.Equal("web 2", Ui.Await(answer, "web's answer after the edit", TimeSpan.FromMinutes(1)));
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && Docker.NoneLeft(project),
                "Stop to end web's session and take it down", TimeSpan.FromMinutes(2));
            window.Debugger.Breakpoints.Toggle(Source(folder, "Web"), InRequest);

            Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
            Assert.Null(Ui.Await(window.Builds.RunAsync(hotReload: true), "the services to come up with Hot Reload", TimeSpan.FromMinutes(5)));
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web and api to be attached to for Hot Reload");
            Assert.False(window.Debugger.IsDebugging, "Run with Hot Reload debugged its services");
            int port = ContainerRunsTests.Published(project, "web");
            Assert.Equal("web 2", Ui.Await(Get(port), "web's answer", TimeSpan.FromMinutes(1)));

            // Another program debugged meanwhile, with Hot Reload of its own, takes nothing from them.
            string looping = Debugging.Helpers.Debuggees.Program("LoopingConsoleApp");
            LaunchPlan other = new() { Name = "Looping", Program = looping, Arguments = ["sleep"], WorkingDirectory = Path.GetDirectoryName(looping)! };
            Assert.Null(Ui.Await(window.Debugger.StartAsync(other), "another program to be debugged beside the run"));
            Assert.NotNull(window.Debugger.HotReload);
            window.OpenFile(message);
            Ui.Settle();
            Typing.Replace(window, "\"web 2\"", "\"web 3\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Assert.Equal("web 3", Ui.Await(Get(port), "web's answer after Apply with another program debugged", TimeSpan.FromMinutes(1)));
            Typing.Replace(window, "\"web 3\"", "\"web 5\"");
            Assert.True(window.Perform(AppActions.Save), $"Save could not run: {window.Status}");
            Ui.Settle();
            Assert.Equal("web 5", Ui.Await(Get(port), "web's answer after a save with another program debugged", TimeSpan.FromMinutes(1)));
            _ = window.Debugger.StopAsync("Looping");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the other program to end with Stop");

            // A field's type the runtime cannot change, so web is built again and restarted.
            Typing.Replace(window, "public static int Count;", "public static long Count;");
            Typing.Replace(window, "\"web 5\"", "\"web 4\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Status?.Contains("restarted web to apply changes", StringComparison.Ordinal) == true, $"web to be restarted for the edit: {window.Status}",
                TimeSpan.FromMinutes(3));
            Assert.Equal(HotReloadResult.RestartNeeded, window.Debugger.LastApplied?.Result);
            Assert.Equal("web 4", Ui.Await(Get(ContainerRunsTests.Published(project, "web")), "web's answer after its restart", TimeSpan.FromMinutes(1)));
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web to be attached to again after its restart");
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the Hot Reload sessions to end with Stop");
        window.Close();
    }

    /// <summary>An edit typed while Run with Hot Reload brings its services up, held after their build and before they are
    /// attached to, is one Apply Hot Reload Changes applies once they run: web then answers as the edited code does.</summary>
    [AvaloniaFact]
    public void An_edit_made_while_Run_with_Hot_Reload_brings_its_services_up_is_applied_once_they_run()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("held");
        MainWindow window = ContainerRunsTests.Open(folder, languageServer: true);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        TaskCompletionSource edited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
            Task held = window.Builds.HoldNextUp(edited.Task);
            Task<string?> running = window.Builds.RunAsync(hotReload: true);
            Ui.PumpUntil(() => held.IsCompleted || running.IsCompleted, "Run with Hot Reload to build and come to its up", TimeSpan.FromMinutes(5));
            Assert.False(running.IsCompleted, $"Run with Hot Reload ended before its up: {(running.IsCompleted ? running.Result : null)}");
            // Not settled: the start holds the window's background work until its services are up.
            window.OpenFile(Path.Combine(folder, "Web", "Message.cs"));
            Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            editor.Select(new Passenger.Text.TextRange(editor.Document.Snapshot.ToString().IndexOf("web 1", StringComparison.Ordinal), "web 1".Length));
            window.KeyTextInput("web 2");
            Assert.Contains("\"web 2\"", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

            edited.SetResult();
            string? refused = Ui.Await(running, "the services to come up with Hot Reload", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web and api to be attached to for Hot Reload");
            int port = ContainerRunsTests.Published(project, "web");
            Assert.Equal("web 1", Ui.Await(Get(port), "web's answer as it was built", TimeSpan.FromMinutes(1)));
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Assert.True(window.Debugger.LastApplied?.Result == HotReloadResult.Applied,
                $"The edit made while the services came up was not applied: {window.Debugger.LastApplied?.Said}");
            Assert.Equal("web 2", Ui.Await(Get(port), "web's answer after Apply", TimeSpan.FromMinutes(1)));
        }
        finally
        {
            edited.TrySetResult();
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the Hot Reload sessions to end with Stop");
        window.Close();
    }

    /// <summary>Under Run with Hot Reload, an edit typed while Rerun's restart of web, and then Hot Reload's restart of both
    /// services for edits it cannot apply, is held after their build is one Apply Hot Reload Changes applies once they run
    /// again: each restarted session follows the edits from its build's save, not from its attach.</summary>
    [AvaloniaFact]
    public void An_edit_made_while_Rerun_or_Hot_Reloads_restart_is_held_after_its_build_is_applied_once_the_services_run_again()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("restarted");
        MainWindow window = ContainerRunsTests.Open(folder, languageServer: true);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        TaskCompletionSource rerunEdited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource restartEdited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
            HotReloadRun(window);
            int port = ContainerRunsTests.Published(project, "web");

            window.OpenFile(Path.Combine(folder, "Web", "Message.cs"));
            Ui.Settle();
            Typing.Replace(window, "\"web 1\"", "\"web 2\"");
            // Saved, so the save's apply has ended before Rerun builds the edit.
            Assert.True(window.Perform(AppActions.Save), $"Save could not run: {window.Status}");
            Ui.Settle();
            Task rerunHeld = window.Builds.HoldNextUp(rerunEdited.Task);
            Task<string?> rerunning = window.Builds.RerunAsync();
            Ui.PumpUntil(() => rerunHeld.IsCompleted || rerunning.IsCompleted, "Rerun to build web and come to its restart", TimeSpan.FromMinutes(5));
            Assert.False(rerunning.IsCompleted, $"Rerun ended before its restart: {(rerunning.IsCompleted ? rerunning.Result : null)}");
            Typed(window, "web 2", "web 3");
            rerunEdited.SetResult();
            string? reran = Ui.Await(rerunning, "Rerun to restart web", TimeSpan.FromMinutes(5));
            Assert.True(reran?.Contains("restarted web with the new build", StringComparison.Ordinal) == true, $"{reran}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web to be attached to again after Rerun");
            Assert.Equal("web 2", Ui.Await(Get(port), "web's answer as Rerun built it", TimeSpan.FromMinutes(1)));
            Applied(window, "The edit made while Rerun's restart was held");
            Assert.Equal("web 3", Ui.Await(Get(port), "web's answer after Apply", TimeSpan.FromMinutes(1)));

            // A field's type the runtime cannot change, in each service, so both are built again and restarted.
            window.OpenFile(Path.Combine(folder, "Api", "Message.cs"));
            Ui.Settle();
            Typing.Replace(window, "public static int Count;", "public static long Count;");
            window.OpenFile(Path.Combine(folder, "Web", "Message.cs"));
            Ui.Settle();
            Typing.Replace(window, "public static int Count;", "public static long Count;");
            Typing.Replace(window, "\"web 3\"", "\"web 4\"");
            Task restartHeld = window.Builds.HoldNextUp(restartEdited.Task);
            Ui.Run(window, AppActions.ApplyHotReloadChanges, settle: false);
            Ui.PumpUntil(() => restartHeld.IsCompleted, "Hot Reload's restart to build and come to its restart", TimeSpan.FromMinutes(5));
            Typed(window, "web 4", "web 5");
            restartEdited.SetResult();
            Ui.WaitFor(() => window.Status?.Contains("to apply changes", StringComparison.Ordinal) == true && window.Debugger.Running.Count == 2,
                $"web and api to be restarted for the edits and attached to again: {window.Status}", TimeSpan.FromMinutes(3));
            Assert.Equal(HotReloadResult.RestartNeeded, window.Debugger.LastApplied?.Result);
            Assert.Equal("web 4", Ui.Await(Get(ContainerRunsTests.Published(project, "web")), "web's answer as its restart built it", TimeSpan.FromMinutes(1)));
            Applied(window, "The edit made while Hot Reload's restart was held");
            Assert.Equal("web 5", Ui.Await(Get(ContainerRunsTests.Published(project, "web")), "web's answer after Apply", TimeSpan.FromMinutes(1)));
        }
        finally
        {
            rerunEdited.TrySetResult();
            restartEdited.TrySetResult();
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the Hot Reload sessions to end with Stop");
        window.Close();
    }

    /// <summary>Under Run with Hot Reload, an edit applied to web and then the Containers window's Restart of web, which runs
    /// it again on its build without the edit: Apply Hot Reload Changes applies the edit again, the restarted session taking
    /// the ended one's edits since the build as still to apply.</summary>
    [AvaloniaFact]
    public void An_edit_applied_before_a_Restart_without_a_build_is_applied_again_once_the_service_runs()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("again");
        MainWindow window = ContainerRunsTests.Open(folder, languageServer: true);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        try
        {
            Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
            HotReloadRun(window);
            int port = ContainerRunsTests.Published(project, "web");
            window.OpenFile(Path.Combine(folder, "Web", "Message.cs"));
            Ui.Settle();
            Typing.Replace(window, "\"web 1\"", "\"web 2\"");
            Applied(window, "The edit before the restart");
            Assert.Equal("web 2", Ui.Await(Get(port), "web's answer after Apply", TimeSpan.FromMinutes(1)));

            ContainersView view = ContainerServicesTests.Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Service && r.Project?.Name == project && r.Service == "web"), "web's row");
            Assert.True(window.Perform(AppActions.ContainerRestart), $"Restart could not run on web: {window.Status}");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.ContainerRestart) is null, "web's restart", TimeSpan.FromMinutes(3));
            Assert.Equal("Compose: compose.yaml: restarted web", window.Status);
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web to be attached to again after its restart");
            port = ContainerRunsTests.Published(project, "web");
            Assert.Equal("web 1", Ui.Await(Get(port), "web's answer as built, restarted without a build", TimeSpan.FromMinutes(1)));
            Applied(window, "The edit applied before the restart");
            Assert.Equal("web 2", Ui.Await(Get(port), "web's answer after Apply", TimeSpan.FromMinutes(1)));
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the Hot Reload sessions to end with Stop");
        window.Close();
    }

    /// <summary>Images as deployed under Run with Hot Reload, each image carrying the build made here of the files as saved:
    /// an edit typed while the start is held after it saved the files for the images' build, and one typed while the
    /// Containers window's Restart of web is held after its own save, is one Apply Hot Reload Changes applies once web runs
    /// again, each session following the edits from its images' save, not from its attach.</summary>
    [AvaloniaFact]
    public void In_images_mode_an_edit_made_while_the_start_or_a_restart_builds_the_images_is_applied_once_the_service_runs()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("imgreload");
        foreach (string app in (string[])["Web", "Api"])
        {
            TestFolders.Write(folder, $"{app}/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /src\nCOPY . .\nEXPOSE 8080\n"
                + $"ENTRYPOINT [\"dotnet\", \"/src/{app}/out/{app}.dll\"]\n");
            ChildResult built = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", Path.Combine(folder, app, $"{app}.csproj"),
                "-o", Path.Combine(folder, app, "out"), "-nologo", "-v:q"], folder), null, TimeSpan.FromMinutes(5).Scaled(), CancellationToken.None)),
                $"{app}'s build", TimeSpan.FromMinutes(5) + TestFolders.Spare);
            Assert.True(built.Succeeded, $"{app} did not build: {built.Output}{built.Error}");
        }
        MainWindow window = ContainerRunsTests.Open(folder, languageServer: true);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        RunConfiguration images = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with
        {
            Name = "Shop as deployed",
            Mode = ContainerMode.Images,
            Generated = false,
        };
        window.SetConfigurations([images], images.Name);
        TaskCompletionSource startEdited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource restartEdited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.OpenFile(Path.Combine(folder, "Web", "Message.cs"));
            Ui.Settle();
            Task startHeld = window.Builds.HoldNextUp(startEdited.Task);
            Task<string?> running = window.Builds.RunAsync(hotReload: true);
            Ui.PumpUntil(() => startHeld.IsCompleted || running.IsCompleted, "Run with Hot Reload to save the files for its images and come to its up", TimeSpan.FromMinutes(5));
            Assert.False(running.IsCompleted, $"Run with Hot Reload ended before its up: {(running.IsCompleted ? running.Result : null)}");
            Typed(window, "web 1", "web 2");
            startEdited.SetResult();
            string? refused = Ui.Await(running, "the images to be built and come up with Hot Reload", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}\n{ContainerRunsTests.Run(window)}");
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web and api to be attached to for Hot Reload");
            int port = ContainerRunsTests.Published(project, "web");
            Assert.Equal("web 1", Ui.Await(Get(port), "web's answer as its image carries it", TimeSpan.FromMinutes(1)));
            Applied(window, "The edit made while the start's images were held after its save");
            Assert.Equal("web 2", Ui.Await(Get(port), "web's answer after Apply", TimeSpan.FromMinutes(1)));

            // Back as built, so the restart's save leaves the files as the images carry them.
            Typed(window, "web 2", "web 1");
            Applied(window, "The edit back to the text built");
            ContainersView view = ContainerServicesTests.Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Service && r.Project?.Name == project && r.Service == "web"), "web's row");
            Task restartHeld = window.Builds.HoldNextUp(restartEdited.Task);
            Assert.True(window.Perform(AppActions.ContainerRestart), $"Restart could not run on web: {window.Status}");
            Ui.PumpUntil(() => restartHeld.IsCompleted, "web's restart to save the files for its image and come to its up", TimeSpan.FromMinutes(3));
            Typed(window, "web 1", "web 3");
            restartEdited.SetResult();
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.ContainerRestart) is null, "web's restart", TimeSpan.FromMinutes(3));
            Assert.Equal("Shop as deployed: restarted web", window.Status);
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web to be attached to again after its restart");
            port = ContainerRunsTests.Published(project, "web");
            Assert.Equal("web 1", Ui.Await(Get(port), "web's answer as its image carries it, restarted", TimeSpan.FromMinutes(1)));
            Applied(window, "The edit made while the restart's image was held after its save");
            Assert.Equal("web 3", Ui.Await(Get(port), "web's answer after Apply", TimeSpan.FromMinutes(1)));
        }
        finally
        {
            startEdited.TrySetResult();
            restartEdited.TrySetResult();
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the Hot Reload sessions to end with Stop");
        window.Close();
    }

    /// <summary>Under Debug, an edit applied to web, then Run Service without Debugger letting web go: its runtime, which lets
    /// no debugger go once Hot Reload has changed its code, takes none again, so Attach to web refuses at once saying why
    /// rather than wait out the runtime's refusal.</summary>
    [AvaloniaFact]
    public void Attach_to_a_service_Hot_Reload_changed_under_a_session_that_let_it_go_refuses_at_once_saying_why()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("changed");
        MainWindow window = ContainerRunsTests.Open(folder, languageServer: true);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        try
        {
            Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
            Debugged(window, "web and api to come up under the debugger");
            DebugTab web = Tab(window, "web");
            Ui.WaitFor(() => web.Session is { State: DebugState.Running }, "web to run under the debugger");
            Chosen(window, web);
            int port = ContainerRunsTests.Published(project, "web");
            window.OpenFile(Path.Combine(folder, "Web", "Message.cs"));
            Ui.Settle();
            Typing.Replace(window, "\"web 1\"", "\"web 2\"");
            Applied(window, "The edit before web was let go");
            Assert.Equal("web 2", Ui.Await(Get(port), "web's answer after Apply", TimeSpan.FromMinutes(1)));
            string? letGo = Ui.Await(window.Builds.RunServiceAsync("web"), "Run Service without Debugger to let web go", TimeSpan.FromMinutes(2));
            Assert.True(letGo is null, $"Run Service without Debugger was refused: {letGo}");
            Ui.WaitFor(() => !web.IsLive, "web's session to end");

            Assert.Equal(DebugController.TakesNoDebugger("web"), Ui.Await(window.Builds.AttachServiceAsync("web"), "Attach to web as it runs", TimeSpan.FromSeconds(5)));
            Assert.Equal(DebugController.TakesNoDebugger("web"), window.Status);
            Assert.DoesNotContain(window.Debugger.Tabs, t => t.Name == "web" && t.IsLive);
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the sessions to end with Stop");
        window.Close();
    }

    /// <summary>Runs the chosen configuration with Hot Reload, waiting for web and api to be attached to for it.</summary>
    private static void HotReloadRun(MainWindow window)
    {
        string? refused = Ui.Await(window.Builds.RunAsync(hotReload: true), "the services to come up with Hot Reload", TimeSpan.FromMinutes(5));
        Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
        Ui.WaitFor(() => window.Debugger.Running.Count == 2, "web and api to be attached to for Hot Reload");
    }

    /// <summary>Apply Hot Reload Changes, which applies <paramref name="what"/>.</summary>
    private static void Applied(MainWindow window, string what)
    {
        Ui.Run(window, AppActions.ApplyHotReloadChanges);
        Assert.True(window.Debugger.LastApplied?.Result == HotReloadResult.Applied, $"{what} was not applied: {window.Debugger.LastApplied?.Said}");
    }

    /// <summary>Types over text in the editor shown while a start or restart holds the window's background work, so unsettled.</summary>
    private static void Typed(MainWindow window, string old, string typed)
    {
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.Select(new Passenger.Text.TextRange(editor.Document.Snapshot.ToString().IndexOf(old, StringComparison.Ordinal), old.Length));
        window.KeyTextInput(typed);
        Assert.Contains(typed, editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Images as deployed, of builds made here naming the images' own paths (/src, where each Dockerfile copies the
    /// context), so only the path map puts web's stop in the workspace's file, with no SDK image needed; api is a Release
    /// build, which is said, and a third service of api's image with its runtime's diagnostics off runs on undebugged, saying
    /// why.</summary>
    [AvaloniaFact]
    public void In_images_mode_a_service_stops_through_the_path_map_a_Release_build_is_said_and_one_with_diagnostics_off_runs_on_saying_why()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("images");
        foreach ((string app, string configuration) in (ReadOnlySpan<(string, string)>)[("Web", "Debug"), ("Api", "Release")])
        {
            TestFolders.Write(folder, $"{app}/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /src\nCOPY . .\nEXPOSE 8080\n"
                + $"ENTRYPOINT [\"dotnet\", \"/src/{app}/out/{app}.dll\"]\n");
            ChildResult built = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", Path.Combine(folder, app, $"{app}.csproj"), "-c", configuration,
                "-o", Path.Combine(folder, app, "out"), $"-p:PathMap={folder}{Path.DirectorySeparatorChar}=/src/", "-nologo", "-v:q"], folder), null,
                TimeSpan.FromMinutes(5).Scaled(), CancellationToken.None)), $"{app}'s build", TimeSpan.FromMinutes(5) + TestFolders.Spare);
            Assert.True(built.Succeeded, $"{app} did not build: {built.Output}{built.Error}");
        }
        File.AppendAllText(Path.Combine(folder, "compose.yaml"), "  quiet:\n    build: { context: ., dockerfile: Api/Dockerfile }\n"
            + "    environment: { DOTNET_EnableDiagnostics: \"0\" }\n");
        MainWindow window = ContainerRunsTests.Open(folder);
        RunConfiguration images = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with
        {
            Name = "Shop as deployed",
            Mode = ContainerMode.Images,
            Generated = false,
        };
        window.SetConfigurations([images], images.Name);
        window.Debugger.Breakpoints.Toggle(Source(folder, "Web"), InRequest);
        try
        {
            Debugged(window, "the images to be built and debugged");
            Assert.Contains("api: " + ContainerTools.ReleaseBuild, window.Status, StringComparison.Ordinal);
            Assert.Contains("quiet runs without the debugger: Its runtime was started with DOTNET_EnableDiagnostics=0", window.Status, StringComparison.Ordinal);
            Assert.DoesNotContain(window.Debugger.Tabs, t => t.Name == "quiet");
            Assert.Equal("running", ContainerRunsTests.States(project)["quiet"]);
            DebugTab web = Tab(window, "web");
            Task<string> answer = Get(ContainerRunsTests.Published(project, "web"));
            StoppedAt(window, web, Source(folder, "Web"), InRequest, "web, built with its image's paths, to stop through the path map");
            Assert.Equal("\"web 1\"", Evaluate(web.Session!, "Message.Text"));
            Resume(window, web);
            Assert.Equal("web 1", Ui.Await(answer, "web's answer", TimeSpan.FromMinutes(1)));
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take the services down");
            window.Close();
        }
    }

    /// <summary>A service whose runtime's debugger is turned off, its diagnostic port left on (which would take a hold, with no
    /// debugger coming to let it go), by its compose environment or by its Dockerfile's ENV (its image's own, which Compose's
    /// model never shows), is never held: under Debug and under Profile it comes up and serves, the run's console saying why
    /// neither reaches it, Debug's status too, and Profile's refusal naming it and why.</summary>
    [AvaloniaTheory]
    [InlineData("compose")]
    [InlineData("Dockerfile")]
    public void A_service_whose_environment_turns_its_debugger_off_is_never_held_and_serves_under_Debug_and_Profile_saying_why(string offBy)
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop($"off{offBy.ToLowerInvariant()[..3]}");
        if (offBy == "compose")
        {
            string compose = File.ReadAllText(Path.Combine(folder, "compose.yaml"));
            TestFolders.Write(folder, "compose.yaml", compose.Replace("    depends_on:", "    environment: { DOTNET_EnableDiagnostics_Debugger: \"0\" }\n    depends_on:",
                StringComparison.Ordinal));
        }
        else
        {
            // Fast mode runs the runtime stage, base, whose ENV is the image's environment.
            string dockerfile = File.ReadAllText(Path.Combine(folder, "Web", "Dockerfile"));
            TestFolders.Write(folder, "Web/Dockerfile", dockerfile.Replace("EXPOSE 8080\n", "EXPOSE 8080\nENV DOTNET_EnableDiagnostics_Debugger=0\n", StringComparison.Ordinal));
        }
        MainWindow window = ContainerRunsTests.Open(folder);
        RunConfiguration marked = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with
        {
            Name = "Shop, web debugged",
            ServiceActions = new Dictionary<string, ServiceStart> { ["web"] = ServiceStart.Debug, ["api"] = ServiceStart.Run },
            Generated = false,
        };
        window.SetConfigurations([marked], marked.Name);
        try
        {
            const string Why = "Its runtime was started with DOTNET_EnableDiagnostics_Debugger=0, which turns its debugger off: nothing can attach to it";
            foreach ((string by, Func<Task<string?>> start) in (ReadOnlySpan<(string, Func<Task<string?>>)>)[("debugger", () => window.DebugAsync()),
                ("profiler", () => window.ProfileAsync())])
            {
                string? refused = Ui.Await(start(), $"the run to come up under the {by}", TimeSpan.FromMinutes(5));
                if (by == "debugger")
                {
                    Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
                    Assert.Contains($"web runs without the debugger: {Why}", window.Status, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Equal($"Shop, web debugged started no .NET service the profiler can reach (web: {Why})", refused);
                }
                Assert.Contains($"web: {Why}", ContainerRunsTests.Run(window), StringComparison.Ordinal);
                int port = ContainerRunsTests.Published(project, "web");
                Assert.True(Ui.Pumped(() => ContainerRunsTests.Answer(port) == "web 1", TimeSpan.FromMinutes(1)),
                    $"web, under the {by}, did not answer: it was held at its start with nothing to let it go");
                Assert.DoesNotContain(window.Debugger.Tabs, t => t.Name == "web" && t.IsLive);
                Assert.Null(window.Profiling);
                Docker.Stopped(window, project, $"Stop to take the services down after the {by}'s run");
                Ui.WaitFor(() => !window.IsRunning, "the run's log to stop being followed");
            }
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take the services down");
            window.Close();
        }
    }

    /// <summary>A port Passenger chose for a service that something else on the machine takes before the engine publishes it
    /// (a listener the test opens there on IPv6's and IPv4's every address, while Debug's up is held) is chosen again: it comes up
    /// published on another port, held and attached there (a request to it stops at a breakpoint), the browser opened at it,
    /// and the run's console says which port was taken. Without the choice made again, the run fails with the engine's
    /// "address already in use".</summary>
    [AvaloniaFact]
    public void A_port_Passenger_chose_that_is_taken_before_up_is_chosen_again_and_the_service_comes_up_debugged_on_another()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("taken");
        MainWindow window = ContainerRunsTests.Open(folder);
        window.Debugger.Breakpoints.Toggle(Source(folder, "Api"), InRequest);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        TaskCompletionSource taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = window.Builds.HoldNextUp(taken.Task);
        System.Net.Sockets.TcpListener? holder = null;
        try
        {
            Task<string?> debugging = window.DebugAsync();
            Ui.PumpUntil(() => held.IsCompleted || debugging.IsCompleted, "Debug to choose its ports and come to its up", TimeSpan.FromMinutes(5));
            Assert.False(debugging.IsCompleted, $"Debug ended before its up: {(debugging.IsCompleted ? debugging.Result : null)}");
            // api is the configuration's browser's: the port chosen for it, taken at [::] too, where Docker Desktop binds it.
            int chosen = window.Builds.Containers!.Plan.Ports!.Of("api").Single();
            holder = System.Net.Sockets.TcpListener.Create(chosen);
            holder.Start();
            taken.SetResult();
            string? refused = Ui.Await(debugging, "Debug to come up with api on another port", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            int now = ContainerRunsTests.Published(project, "api");
            Assert.NotEqual(chosen, now);
            Assert.Equal([now], window.Builds.Containers!.Plan.Ports!.Of("api"));
            Assert.Contains($"api's port {chosen} was taken before the engine could publish it: published on {now} instead", ContainerRunsTests.Run(window),
                StringComparison.Ordinal);
            Ui.WaitFor(() => window.Browsed.Any(u => u.Port == now), $"the browser to open at api's new port {now}", TimeSpan.FromMinutes(1));

            DebugTab api = Tab(window, "api");
            Task<string> answer = Get(now);
            StoppedAt(window, api, Source(folder, "Api"), InRequest, "api's request on its new port to stop in its handler");
            Resume(window, api);
            Assert.Equal("api 1", Ui.Await(answer, "api's answer on its new port", TimeSpan.FromMinutes(1)));
        }
        finally
        {
            taken.TrySetResult();
            holder?.Dispose();
            Docker.Stopped(window, project, "Stop to take the services down");
            window.Close();
        }
    }

    /// <summary>ContainerRunsTests' Compose solution, each service's Program.cs and Message.cs as these tests stop in them.</summary>
    private static (string Folder, string Project) Shop(string name)
    {
        (string folder, string project) = ContainerRunsTests.Shop($"dbg{name}");
        foreach (string app in (string[])["Web", "Api"])
        {
            TestFolders.Write(folder, $"{app}/Program.cs", Program);
            TestFolders.Write(folder, $"{app}/Message.cs",
                $"public static class Message\n{{\n    public static int Count;\n\n    public static string Text => \"{app.ToLowerInvariant()} 1\";\n}}\n");
        }
        return (folder, project);
    }

    private static string Source(string folder, string app) => Path.Combine(folder, app, "Program.cs");

    /// <summary>Debugs the chosen configuration, failing with what its services said when it does not come up.</summary>
    private static void Debugged(MainWindow window, string what)
    {
        string? refused = Ui.Await(window.DebugAsync(), what, TimeSpan.FromMinutes(5));
        Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
    }

    /// <summary>A service's Debug tab, once its session has begun.</summary>
    internal static DebugTab Tab(MainWindow window, string service)
    {
        Ui.WaitFor(() => window.Debugger.Tabs.Any(t => t.Name == service && t.Session is not null), $"{service}'s Debug tab");
        return window.Debugger.Tabs.Single(t => t.Name == service);
    }

    /// <summary>Waits for a tab's program to stop at a file's line (from zero), failing with where its frames say it is, and
    /// for the window to have shown the stop (the editor at its line), which a person acts after.</summary>
    internal static void StoppedAt(MainWindow window, DebugTab tab, string path, int line, string what)
    {
        (string? Path, int Line)? at = null;
        try
        {
            Ui.WaitFor(() =>
            {
                if (tab.Session is not { State: DebugState.Paused, StoppedThread: int thread } session)
                {
                    return false;
                }
                Passenger.Dap.DapStackFrame top = Ui.Await(session.StackTraceAsync(thread), "the stop's frames").First(f => f.Source?.Path is not null);
                at = (top.Source!.Path, top.Line - 1);
                return true;
            }, what, TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            tab.Console.Flush();
            Assert.Fail($"Waited for {what}: {tab.Name}'s session is {tab.Session?.State}; its breakpoints: "
                + string.Join(", ", window.Debugger.Breakpoints.In(path).Select(b => $"{b.Line} {b.Status} {b.Message}")) + $"; its console:\n{tab.Console.Document.Snapshot}");
        }
        Assert.True(at is { } stop && stop.Path == path && stop.Line == line, $"{what}: it stopped at {at?.Path}:{at?.Line}");
        // Showing it is the window's background work.
        Ui.Settle();
    }

    internal static string Evaluate(DebugSession session, string expression)
    {
        IReadOnlyList<Passenger.Dap.DapStackFrame> frames = Ui.Await(session.StackTraceAsync(session.StoppedThread ?? 0), "the stop's frames");
        return Ui.Await(session.EvaluateAsync(expression, frames[0].Id, "watch"), $"{expression} to be evaluated").Result;
    }

    /// <summary>Resumes a tab's program as Resume does with its tab chosen, waiting for it to run.</summary>
    internal static void Resume(MainWindow window, DebugTab tab)
    {
        Chosen(window, tab);
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => tab.Session?.State != DebugState.Paused, $"{tab.Name} to go on");
    }

    private static void Chosen(MainWindow window, DebugTab tab)
    {
        Task chosen = window.Debugger.ChooseAsync(tab);
        Ui.WaitFor(() => chosen.IsCompleted, $"{tab.Name}'s tab to be chosen");
    }

    /// <summary>A request to a port, answered once the program goes on (one paused at a breakpoint is waited for); asked
    /// again while nothing listens there yet (the program let go of a moment ago, or restarting). It never fails: a request
    /// that gets no answer within a minute of refusals, or at all, gives back why, so no failure of it outlives its test.</summary>
    internal static Task<string> Get(int port) => Waiting.Apart(async () =>
    {
        Uri address = new($"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/");
        DateTime until = DateTime.UtcNow + TimeSpan.FromMinutes(1).Scaled();
        while (true)
        {
            try
            {
                return await Http.GetStringAsync(address);
            }
            catch (HttpRequestException) when (DateTime.UtcNow < until)
            {
                await Waiting.UntilAsync(() => false, TimeSpan.FromMilliseconds(200));
            }
            catch (Exception failed) when (failed is HttpRequestException or OperationCanceledException or IOException)
            {
                return $"(no answer from port {port.ToString(CultureInfo.InvariantCulture)}: {failed.GetType().Name}: {failed.Message})";
            }
        }
    });

    /// <summary>Passenger's container tools for the engine's processor, published where a dev build finds them.</summary>
    public sealed class Tools : IAsyncLifetime
    {
        public async ValueTask InitializeAsync()
        {
            WhyNot = await Docker.WhyNotAsync();
            if (WhyNot is null)
            {
                await ContainerPrograms.PublishAsync($"linux-{await ContainerPrograms.ProcessorAsync()}");
            }
        }

        /// <summary>Why no container runs here, or null.</summary>
        public string? WhyNot { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
