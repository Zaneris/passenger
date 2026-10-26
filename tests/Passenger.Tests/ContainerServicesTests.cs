using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>One .NET service of a running container run acted on as the run goes on, against a real engine, from the
/// Containers window's rows and the gutter's run icon: Debug restarts it held and stops in its Main, the window's
/// Restart goes through the run and holds it again, Run without Debugger lets its session go with no restart, Attach
/// reaches another as it runs (its startup said to have run), and the gutter's menu offers them; the others never
/// restart. Skips, saying why, where no Linux engine runs.</summary>
[Collection(Alone.Name)]
public sealed class ContainerServicesTests(ContainerRunDebuggingTests.Tools tools) : IClassFixture<ContainerRunDebuggingTests.Tools>
{
    /// <summary>Each service's Program.cs: a line in Main and a request's handler.</summary>
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

    private static readonly int InMain = Array.FindIndex(Program.Split('\n'), l => l.StartsWith("string name", StringComparison.Ordinal));
    private static readonly int InRequest = Array.FindIndex(Program.Split('\n'), l => l.Contains("string said", StringComparison.Ordinal));

    [AvaloniaFact]
    public void A_services_Debug_Restart_Run_without_Debugger_and_Attach_act_on_it_alone_and_leave_the_run_up()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = ContainerRunsTests.Shop("services");
        foreach (string app in (string[])["Web", "Api"])
        {
            TestFolders.Write(folder, $"{app}/Program.cs", Program);
        }
        // web is published on the loopback address alone, on a port the engine chooses (which Passenger pins for the run).
        string compose = Path.Combine(folder, "compose.yaml");
        string text = File.ReadAllText(compose);
        int ports = text.IndexOf("ports: [\"8080\"]", StringComparison.Ordinal);
        File.WriteAllText(compose, $"{text[..ports]}ports: [\"127.0.0.1::8080\"]{text[(ports + "ports: [\"8080\"]".Length)..]}");
        MainWindow window = ContainerRunsTests.Open(folder);
        window.Debugger.Breakpoints.Toggle(Source(folder, "Web"), InMain);
        window.Debugger.Breakpoints.Toggle(Source(folder, "Api"), InRequest);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        string WebPublished() => Docker.Run("compose", "-p", project, "port", "web", "8080").Split('\n')[0].Trim();
        try
        {
            Ui.Launched(window, window.RunAsync(), "the services to come up", TimeSpan.FromMinutes(5));
            Assert.False(window.Debugger.IsDebugging, "Run debugged a service");
            string webAt = WebPublished();
            Assert.StartsWith("127.0.0.1:", webAt, StringComparison.Ordinal);
            string apiStarted = ContainerRunsTests.Started(project, "api");
            ContainersView view = Shown(window);

            Act(window, view, project, "web", AppActions.ServiceDebug);
            DebugTab web = ContainerRunDebuggingTests.Tab(window, "web");
            ContainerRunDebuggingTests.StoppedAt(window, web, Source(folder, "Web"), InMain, "web, made again held, to stop in Main");
            Assert.Equal(apiStarted, ContainerRunsTests.Started(project, "api"));
            Assert.True(window.IsRunning, "Debug of one service took the run down");
            Assert.Equal(webAt, WebPublished());
            ContainerRunDebuggingTests.Resume(window, web);

            string webDebugged = ContainerRunsTests.Started(project, "web");
            Act(window, view, project, "web", AppActions.ContainerRestart);
            Ui.WaitFor(() => ContainerRunsTests.Started(project, "web") is { Length: > 0 } now && now != webDebugged, "web to restart", TimeSpan.FromMinutes(2));
            web = ContainerRunDebuggingTests.Tab(window, "web");
            ContainerRunDebuggingTests.StoppedAt(window, web, Source(folder, "Web"), InMain, "web, restarted from the Containers window, to stop in Main again");
            Assert.Equal(webAt, WebPublished());
            ContainerRunDebuggingTests.Resume(window, web);

            string webRestarted = ContainerRunsTests.Started(project, "web");
            Act(window, view, project, "web", AppActions.ServiceRun);
            Ui.WaitFor(() => !window.Debugger.Tabs.Any(t => t.Name == "web" && t.IsLive), "web's session to be let go");
            Assert.Equal(webRestarted, ContainerRunsTests.Started(project, "web"));
            Assert.Equal("web 1", Answered(Request(project, "web"), project, "web"));

            Act(window, view, project, "api", AppActions.ServiceAttach);
            DebugTab api = ContainerRunDebuggingTests.Tab(window, "api");
            Ui.WaitFor(() => api.Session is { State: DebugState.Running }, "api to be attached to as it runs");
            Assert.Contains("its startup has run already", window.Status, StringComparison.Ordinal);
            Assert.Equal(apiStarted, ContainerRunsTests.Started(project, "api"));
            Task<string> answer = ContainerRunDebuggingTests.Get(ContainerRunsTests.Published(project, "api"));
            ContainerRunDebuggingTests.StoppedAt(window, api, Source(folder, "Api"), InRequest, "api's request to stop in its handler");
            ContainerRunDebuggingTests.Resume(window, api);
            Assert.Equal("api 1", Answered(answer, project, "api"));

            Avalonia.Controls.MenuItem[] items = GutterMenu(window, folder, "  web:");
            Assert.Equal(["Debug Service", "Attach to Service", "Run Service without Debugger", "Profile Service"], items.Select(i => (string?)i.Header));
            Assert.IsType<MenuFlyout>(window.RunMarkMenu).Hide();
            Ui.Settle();

            // Stopped from outside, api's program ends, said so, never as a debugger's failure (an IDE error).
            string apiContainer = ContainerRunsTests.Container(project, "api");
            Docker.Run("stop", apiContainer);
            Ui.WaitFor(() => !api.IsLive, "api's session to end with its container", TimeSpan.FromMinutes(1));
            Assert.True(window.Status?.StartsWith($"Debugging api ended: {apiContainer}", StringComparison.Ordinal) == true
                && window.Status.EndsWith(" stopped", StringComparison.Ordinal), $"api's end with its container was said as: {window.Status}");
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the sessions to end with Stop");
        window.Close();
    }

    /// <summary>A container run's .NET services followed by the Requests window through Passenger's tools in their
    /// containers (trace --requests), each request listed with the service that served it; and a route's request sent
    /// from the folder's code goes to the port the engine published for its service.</summary>
    [AvaloniaFact]
    public void The_requests_window_follows_each_service_in_its_container_and_a_routes_request_goes_to_its_published_port()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = ContainerRunsTests.Shop("requests");
        MainWindow window = ContainerRunsTests.Open(folder, languageServer: true);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            Ui.Launched(window, window.RunAsync(), "the services to come up", TimeSpan.FromMinutes(5));
            Ui.WaitFor(() => window.RequestLog.Following.Contains("web of", StringComparison.Ordinal) && window.RequestLog.Following.Contains("api of", StringComparison.Ordinal),
                "the Requests window to follow both services in their containers", TimeSpan.FromMinutes(2));
            Assert.Equal("web 1", Ui.AwaitPumped(ContainerRunDebuggingTests.Get(ContainerRunsTests.Published(project, "web")), "web's answer", TimeSpan.FromMinutes(3.5)));
            Assert.Equal("api 1", Ui.AwaitPumped(ContainerRunDebuggingTests.Get(ContainerRunsTests.Published(project, "api")), "api's answer", TimeSpan.FromMinutes(3.5)));
            Ui.WaitFor(() => window.RequestLog.Requests.Count(r => r.Path == "/" && r.Status == 200) == 2, "both services' requests, listed as they ended", TimeSpan.FromSeconds(30));
            Assert.Equal(["api of Compose: compose.yaml", "web of Compose: compose.yaml"], window.RequestLog.Requests.Select(r => r.From).Order(StringComparer.Ordinal));
            Assert.All(window.RequestLog.Requests, r => Assert.True(r.Route is { } route && route.Project.Contains(r.From[..3], StringComparison.OrdinalIgnoreCase),
                $"{r.From}'s request reached {r.Route?.Project ?? "no route"}"));

            Passenger.Roslyn.Extension.WebRoute webRoute = window.Session.Routes.All.Single(r => r.Project.Contains("Web", StringComparison.Ordinal) && r.Template == "/");
            Task<string?> sent = window.SendRouteAsync(webRoute);
            Ui.WaitFor(() => sent.IsCompleted && window.Endpoints.Response.Response is not null, "web's answer to the route's request", TimeSpan.FromMinutes(1));
            Assert.Null(Ui.Await(sent, "the route's request to be sent"));
            Assert.True(window.Endpoints.Response.Passed, $"web's route did not answer from its container: {window.Endpoints.Response.Summary}");
            Assert.Contains("web 1", window.Endpoints.Response.Shown, StringComparison.Ordinal);
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        window.Close();
    }

    /// <summary>Debug of one service whose debugger cannot attach after its restart (no container tools to put in it: none
    /// here, and no release to download them from) says why, and makes it again unheld, so it runs on without the debugger
    /// rather than waiting at its runtime's start for one that never comes.</summary>
    [AvaloniaFact]
    public void A_service_the_debugger_cannot_attach_to_after_its_restart_runs_on_without_it()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = ContainerRunsTests.Shop("noattach");
        string none = TestFolders.Create("ctr-notools-");
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            ContainerToolsFrom = new ContainerToolsSource([none], Passenger.App.Updates.ReleaseFeed.Default, Path.Combine(none, "cache")),
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        try
        {
            Ui.Launched(window, window.RunAsync(), "the services to come up", TimeSpan.FromMinutes(5));
            Act(window, Shown(window), project, "web", AppActions.ServiceDebug);
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.ServiceDebug) is null, "Debug of web to give up", TimeSpan.FromMinutes(3));
            Assert.Contains("Could not debug web", window.Status, StringComparison.Ordinal);
            Assert.Contains("web restarted to run without the debugger", window.Status, StringComparison.Ordinal);
            Assert.DoesNotContain(window.Debugger.Tabs, t => t.Name == "web" && t.IsLive);
            Assert.Equal("web 1", Answered(Request(project, "web"), project, "web"));
            Assert.True(window.IsRunning, "Debug of web took the run down");
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        window.Close();
    }

    /// <summary>A configuration marking both .NET services Don't start runs only the cache, its rows listing web and api as not
    /// started, Attach greyed saying so; web's Run from its row builds it and starts it later, unheld, and Rerun restarts its
    /// changed build as the run's; api's Debug from its row starts it held, stopping in its Main, web left as it runs; Rerun
    /// while debugging, Debug of the configuration run whole, starts both again; and Stop takes every container down.</summary>
    [AvaloniaFact]
    public void A_service_marked_Dont_start_is_listed_not_started_and_its_Run_and_Debug_start_it_later_in_the_run()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = ContainerRunsTests.Shop("later");
        foreach (string app in (string[])["Web", "Api"])
        {
            TestFolders.Write(folder, $"{app}/Program.cs", Program);
        }
        MainWindow window = ContainerRunsTests.Open(folder);
        window.Debugger.Breakpoints.Toggle(Source(folder, "Api"), InMain);
        RunConfiguration later = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with
        {
            Name = "Later",
            ServiceActions = new Dictionary<string, ServiceStart> { ["web"] = ServiceStart.DontStart, ["api"] = ServiceStart.DontStart },
            Generated = false,
        };
        window.SetConfigurations([later], later.Name);
        try
        {
            Ui.Launched(window, window.RunAsync(), "the cache to come up", TimeSpan.FromMinutes(5));
            Dictionary<string, string> states = ContainerRunsTests.States(project);
            Assert.True(states.GetValueOrDefault("cache") == "running" && !states.ContainsKey("web") && !states.ContainsKey("api"),
                $"A run whose .NET services are marked Don't start left these: {string.Join(", ", states)}");

            ContainersView view = Shown(window);
            foreach (string service in (string[])["web", "api"])
            {
                Ui.WaitFor(() => view.Rows.Any(r => r.Service == service && r.LeftUnstarted && r.Detail == "Not started"), $"{service}'s row, not started");
            }
            Assert.True(view.Choose(r => r.Service == "web" && r.LeftUnstarted), "web's row could not be chosen");
            Ui.Settle();
            Assert.Equal(Refusals.NotStarted("web of Later", ContainerRun.StartsService), window.Actions.WhyNot(AppActions.ServiceAttach, window.Actions.Context()));
            Assert.Equal(Refusals.NotStarted($"{project}/web", ContainerRun.StartsService), window.Actions.WhyNot(AppActions.ContainerStop, window.Actions.Context()));
            Assert.Null(window.Actions.WhyNot(AppActions.ServiceRun, window.Actions.Context()));

            Later(window, view, project, "web", AppActions.ServiceRun);
            Assert.Contains("web of Later started, without the debugger", window.Status, StringComparison.Ordinal);
            Assert.DoesNotContain(window.Debugger.Tabs, t => t.Name == "web" && t.IsLive);
            Assert.Equal("web 1", Answered(Request(project, "web"), project, "web"));
            Ui.WaitFor(() => view.Rows.Any(r => r.Service == "web" && !r.LeftUnstarted && r.Containers.Any(c => c.IsRunning)), "web's row to be its running container's");

            TestFolders.Write(folder, "Web/Message.cs", "public static class Message\n{\n    public const string Text = \"web 2\";\n}\n");
            Assert.True(window.Perform(AppActions.Rerun), "Rerun did not run");
            Assert.True(Ui.Pumped(() => window.Status?.Contains("restarted web with the new build", StringComparison.Ordinal) == true, TimeSpan.FromMinutes(3)),
                $"Rerun did not restart web alone: {window.Status}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            Assert.Equal("web 2", Answered(Request(project, "web"), project, "web"));
            Assert.True(window.IsRunning, "Rerun took the run down");
            string webStarted = ContainerRunsTests.Started(project, "web");

            Later(window, view, project, "api", AppActions.ServiceDebug);
            DebugTab api = ContainerRunDebuggingTests.Tab(window, "api");
            ContainerRunDebuggingTests.StoppedAt(window, api, Source(folder, "Api"), InMain, "api, started later held, to stop in Main");
            Assert.Equal(webStarted, ContainerRunsTests.Started(project, "web"));
            ContainerRunDebuggingTests.Resume(window, api);
            Assert.Equal("api 1", Answered(Request(project, "api"), project, "api"));

            Assert.True(window.Perform(AppActions.Rerun), "Rerun did not run");
            Ui.WaitFor(() => ContainerRunsTests.Started(project, "web") is { Length: > 0 } now && now != webStarted
                && window.Debugger.Tabs.Any(t => t.Name == "api" && t.IsLive && t.Session is { State: DebugState.Paused }), "the run made again, api stopping in Main", TimeSpan.FromMinutes(5));
            ContainerRunDebuggingTests.StoppedAt(window, window.Debugger.Tabs.Single(t => t.Name == "api" && t.IsLive), Source(folder, "Api"), InMain,
                "api, started again by the run made whole, to stop in Main");
            states = ContainerRunsTests.States(project);
            Assert.True(states.GetValueOrDefault("web") == "running" && states.GetValueOrDefault("api") == "running", $"The run made again left these: {string.Join(", ", states)}");
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down, those started later too");
        }
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the sessions to end with Stop");
        window.Close();
    }

    /// <summary>One of a service's actions on its row, its run's build and start of it waited out, failing with what it said.</summary>
    private static void Later(MainWindow window, ContainersView view, string project, string service, string action)
    {
        Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Service && r.Project?.Name == project && r.Service == service), $"{service}'s row");
        Assert.True(window.Perform(action), $"{action} could not run on {service}: {window.Status}");
        Ui.WaitFor(() => window.Actions.Work.Of(action) is null, $"{action} of {service} to build and start it", TimeSpan.FromMinutes(4));
        Assert.True(ContainerRunsTests.StatesNow(project)?.GetValueOrDefault(service) == "running",
            $"{action} did not start {service}: {window.Status}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
    }

    private static string Source(string folder, string app) => Path.Combine(folder, app, "Program.cs");

    /// <summary>A service's answer to a request (<see cref="ContainerRunDebuggingTests.Get"/>, which never fails), or the test
    /// failed with what its container and the debugger's log say of why none came.</summary>
    private static string Answered(Task<string> answering, string project, string service)
    {
        string? answer = null;
        try
        {
            answer = Ui.Await(answering, $"{service}'s answer", TimeSpan.FromMinutes(1.5));
        }
        catch (TimeoutException)
        {
        }
        if (answer is null || answer.StartsWith("(no answer", StringComparison.Ordinal))
        {
            string log = Path.Combine(TestRun.Settings, "logs", "dap.log");
            string said = File.Exists(log) ? Ui.Shared(log) : string.Empty;
            string container = Docker.Ask(["ps", "-aq", "--filter", $"label=com.docker.compose.project={project}", "--filter", $"label=com.docker.compose.service={service}"]).Trim();
            Assert.Fail($"{service} gave no answer: {answer ?? "none in 90 s"}\nits container: {Docker.Ask(["inspect", "-f", "{{.State.Status}} {{.State.ExitCode}} {{.State.StartedAt}} {{.State.FinishedAt}}", container])}"
                + $"\nits processes:\n{Docker.Ask(["top", container])}\nits log's end:\n{Docker.Ask(["logs", "--tail", "25", container])}\n"
                + $"the debugger's log's end:\n{said[Math.Max(0, said.Length - 5000)..]}\nthe engine's log there (with PASSENGER_ENGINE_LOG):\n{Docker.Ask(["exec", container, "tail", "-n", "120", Passenger.App.Debugging.DebugSession.ContainerEngineLog])}");
        }
        return answer;
    }

    /// <summary>A request to a service's published port (<see cref="ContainerRunDebuggingTests.Get"/>), or, while it publishes
    /// none (its container not running), what Compose said of that, as an answer that is no answer.</summary>
    private static Task<string> Request(string project, string service)
    {
        string said = Docker.Ask(["compose", "-p", project, "port", service, "8080"]).Split('\n')[0].Trim();
        // Docker's CLI says 0.0.0.0:port; on Podman's socket ":port", the address left out.
        return said.LastIndexOf(':') is int colon and >= 0 && int.TryParse(said[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            ? ContainerRunDebuggingTests.Get(port)
            : Task.FromResult($"(no answer: {service} publishes no port: {said})");
    }

    internal static ContainersView Shown(MainWindow window)
    {
        window.ShowToolWindow(ToolWindowIds.Containers);
        Ui.Settle();
        return window.ContainersWindow;
    }

    /// <summary>One of the Containers window's actions on a service's row, chosen as a click chooses it.</summary>
    internal static void Act(MainWindow window, ContainersView view, string project, string service, string action)
    {
        Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Service && r.Project?.Name == project && r.Service == service), $"{service}'s row");
        Assert.True(window.Perform(action), $"{action} could not run on {service}: {window.Status}");
        Ui.Settle();
    }

    /// <summary>The menu of the run icon beside a compose file's line, opened by a click there.</summary>
    private static Avalonia.Controls.MenuItem[] GutterMenu(MainWindow window, string folder, string line)
    {
        window.OpenFile(Path.Combine(folder, "compose.yaml"));
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument compose = window.EditorArea.Active!;
        int at = Array.IndexOf(compose.Text.Snapshot.ToString().Split('\n'), line);
        Ui.WaitFor(() => compose.RunMarks.Any(m => m.Line == at), $"the run icon beside {line.Trim()}");
        Passenger.Editor.CodeEditor shown = window.EditorArea.CodeEditor;
        double x = Enumerable.Range(0, 400).First(x => shown.IsOnGutterColumn(shown.Gutter!.Marks, new Avalonia.Point(x, 0))) + 3;
        Ui.Click(shown, new Avalonia.Point(x, shown.PointOf(compose.Text.Snapshot.GetLineStart(at)).Y + (shown.LineHeight / 2)));
        return [.. Assert.IsType<MenuFlyout>(window.RunMarkMenu).Items.OfType<Avalonia.Controls.MenuItem>()];
    }
}
