using System.Globalization;
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
            ChildResult built = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", Path.Combine(folder, app, $"{app}.csproj"), "-c", configuration,
                "-o", Path.Combine(folder, app, "out"), $"-p:PathMap={folder}{Path.DirectorySeparatorChar}=/src/", "-nologo", "-v:q"], folder), null,
                TimeSpan.FromMinutes(5).Scaled(), CancellationToken.None)).GetAwaiter().GetResult();
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
                Task<IReadOnlyList<Passenger.Dap.DapStackFrame>> frames = session.StackTraceAsync(thread);
                Ui.WaitFor(() => frames.IsCompleted, "the stop's frames");
                Passenger.Dap.DapStackFrame top = frames.Result.First(f => f.Source?.Path is not null);
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
        Task<IReadOnlyList<Passenger.Dap.DapStackFrame>> frames = session.StackTraceAsync(session.StoppedThread ?? 0);
        Ui.WaitFor(() => frames.IsCompleted, "the stop's frames");
        Task<Passenger.Dap.EvaluateResponse> value = session.EvaluateAsync(expression, frames.Result[0].Id, "watch");
        Ui.WaitFor(() => value.IsCompleted, $"{expression} to be evaluated");
        return value.Result.Result;
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
    internal static Task<string> Get(int port) => Task.Run(async () =>
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
    public sealed class Tools
    {
        public Tools()
        {
            WhyNot = Docker.WhyNot;
            if (WhyNot is null)
            {
                ContainerPrograms.Publish($"linux-{ContainerPrograms.Processor()}");
            }
        }

        /// <summary>Why no container runs here, or null.</summary>
        public string? WhyNot { get; }
    }
}
