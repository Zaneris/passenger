using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Debugging;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>An Azure Functions app run and debugged through the window, as Azure Functions Core Tools' func runs it with the
/// debugger on its worker: added to an AppHost with AddAzureFunctionsProject, and as its own project. The class shares one
/// restored app (<see cref="AspireFunctionsApp"/>); a window starts no C# language server, as nothing here reads C#.</summary>
[Collection(Alone.Name)]
public sealed class AspireFunctionsTests : IClassFixture<AspireFunctionsApp>, IDisposable
{
    private const string Resource = "fn";

    private readonly AspireFunctionsApp project;

    public AspireFunctionsTests(AspireFunctionsApp project)
    {
        this.project = project;
        if (project.WhyNot is null)
        {
            project.Reset();
        }
    }

    [AvaloniaFact(Skip = AspireFunctionsApp.AppHostSkipped, SkipType = typeof(AspireFunctionsApp), SkipWhen = nameof(AspireFunctionsApp.IsAppHostUnavailable))]
    public void Debug_of_an_AppHost_stops_in_its_Functions_apps_function_and_Restart_and_Stop_act_on_func_and_its_worker()
    {
        MainWindow window = project.Open(project.HostProgram, languageServer: false);
        try
        {
            int line = BreakAt(window, project.Function, AspireFunctionsApp.Answering);
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Aspire && PathNames.Same(configuration.Project, project.Host)
                && configuration.LaunchProfile == "http"), "the AppHost's http configuration");
            Assert.True(window.SelectConfiguration("Aspire: Host: http"));
            Ui.Launched(window, window.DebugAsync(), "the AppHost under the debugger", TimeSpan.FromMinutes(3));
            AspireResourceRun first = Debugged(window, null);
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2).Scaled() };
            StopsAndAnswers(window, Assert.IsType<DebugTab>(first.DebugTab), new Uri(HttpUrl(Snapshot(window)), "/api/Hello"), line, http);

            AspireResourceCommand restart = Assert.Single(Snapshot(window).Commands, command => command.Name is "restart" or "resource-restart");
            ChooseResource(window);
            Ui.WaitFor(() => window.Aspire.SelectedResource?.Commands.Any(command => command.Name == restart.Name && command.Enabled) == true,
                $"the Functions app's {restart.DisplayName} command");
            Ui.Click(window.AspireWindow.Tools[AppActions.AspireCommand]);
            Ui.Choose(window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(menu => menu.IsOpen), restart.DisplayName);
            AspireResourceRun second = Debugged(window, first);
            Ui.WaitFor(() => !Processes.IsRunning(first.ProcessId!.Value) && !Processes.IsRunning(first.Worker!.Value), "Restart to end the first func and its worker");
            StopsAndAnswers(window, Assert.IsType<DebugTab>(second.DebugTab), new Uri(HttpUrl(Snapshot(window)), "/api/Hello"), line, http);

            int[] owned = [.. window.AspireRuntime.Apps.SelectMany(app => new[] { app.ProcessId, app.Runner.ProcessId }).Append(second.ProcessId).Append(second.Worker).OfType<int>()];
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0 && owned.All(pid => !Processes.IsRunning(pid)), "Stop to end the AppHost, func and its worker");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = AspireFunctionsApp.FuncSkipped, SkipType = typeof(AspireFunctionsApp), SkipWhen = nameof(AspireFunctionsApp.IsUnavailable))]
    public void A_Functions_projects_own_Run_serves_its_function_through_func_and_its_Debug_stops_in_it()
    {
        MainWindow window = project.Open(project.Function, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => PathNames.Same(configuration.Project, project.FunctionProject) && configuration.LaunchProfile == "Fn"),
                "the Functions app's own configuration");
            string own = window.Configurations.First(configuration => PathNames.Same(configuration.Project, project.FunctionProject) && configuration.LaunchProfile == "Fn").Name;
            Assert.True(window.SelectConfiguration(own));
            // Hot Reload on, which a Functions app runs without, saying so.
            window.ApplySettings(window.Settings with { HotReload = true });
            Uri function = new($"http://localhost:{project.OwnPort}/api/Hello");
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2).Scaled() };
            Ui.Launched(window, window.RunAsync(), "the Functions app's build and func's start", TimeSpan.FromMinutes(3));
            Func<string> ran = () => window.BottomPanel.Run.Document.Snapshot.ToString();
            Ui.WaitFor(() => ran().Contains(function.AbsolutePath, StringComparison.Ordinal), () => $"func to serve the function; the Run window shows:\n{ran()}",
                TimeSpan.FromMinutes(2));
            Assert.Contains("Azure Functions Core Tools", ran(), StringComparison.Ordinal);
            Assert.Equal(AspireFunctionsApp.Answer, Ui.AwaitPumped(http.GetStringAsync(Listening(function)), "the function's answer under Run"));
            Assert.Contains(Refusals.FunctionsUnreloaded, window.Status, StringComparison.Ordinal);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => Leftovers().Count == 0, () => $"Stop to end func and its worker: {string.Join("; ", Leftovers().Values)}");

            int line = BreakAt(window, project.Function, AspireFunctionsApp.Answering);
            Ui.Launched(window, window.DebugAsync(), "func's start with the debugger on its worker", TimeSpan.FromMinutes(3));
            DebugTab tab = Assert.Single(window.Debugger.Tabs, tab => tab.Name == own && tab.IsLive);
            DebugSession session = Assert.IsType<DebugSession>(tab.Session);
            Assert.True(session.Attached, "The debugger launched func, not attached to the worker func started");
            Assert.NotNull(window.Debugger.WhyNotDetachOf(session));
            StopsAndAnswers(window, tab, function, line, http);
            Assert.Contains("Azure Functions Core Tools", tab.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && Leftovers().Count == 0,
                () => $"Stop to end the session, func and its worker: {string.Join("; ", Leftovers().Values)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The Functions app's start under the debugger, once its resource runs: func, the worker it started, and the
    /// session attached to that worker in its tab; another than <paramref name="before"/>'s, after a restart.</summary>
    private AspireResourceRun Debugged(MainWindow window, AspireResourceRun? before)
    {
        ShowResources(window);
        Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == Resource && resource.State == "Running"
            && window.AspireRuntime.FindService(new(project.Host, resource)) is AspireResourceRun { ProcessId: > 0, Worker: > 0, DebugTab.IsLive: true } run
            && run.ProcessId != before?.ProcessId),
            () => $"func to start the Functions app's worker under the debugger ({window.Status}; {Seen(window)}; {Said(window)})", TimeSpan.FromMinutes(3));
        AspireResourceRun started = Service(window);
        Assert.Contains(started.Worker!.Value, Processes.Descendants(started.ProcessId!.Value));
        return started;
    }

    /// <summary>A request of the function stops at its breakpoint with its local shown, and once let go answers.</summary>
    private void StopsAndAnswers(MainWindow window, DebugTab tab, Uri function, int line, HttpClient http)
    {
        Task<string> request = http.GetStringAsync(Listening(function));
        Ui.WaitFor(() => request.IsCompleted || tab.Session is { State: DebugState.Paused, StoppedThread: not null }, $"the request's breakpoint in {project.Function}:{line + 1}");
        if (request.IsCompleted)
        {
            Assert.Fail($"The request ended without stopping at the function's breakpoint, answered {Ui.Await(request, "the request's end")}");
        }
        DebugSession session = Assert.IsType<DebugSession>(tab.Session);
        DapStackFrame top = Assert.IsType<DapStackFrame>(Ui.Await(session.StackTraceAsync(session.StoppedThread!.Value), "the worker's stack").FirstOrDefault());
        Assert.Equal(line + 1, top.Line);
        Assert.True(PathNames.Same(project.Function, top.Source!.Path!), $"The worker stopped in {top.Source.Path}, not the function's file");
        Ui.WaitFor(() => window.BottomPanel.Debug.Tree.Rows.Any(row => row.Name == "answer" && row.Value == "42"), "the function's local in the Variables");
        window.Perform(AppActions.Resume);
        Assert.Equal(AspireFunctionsApp.Answer, Ui.AwaitPumped(request, "the function's answer once let go"));
    }

    /// <summary>The function's address once func takes connections there, which it lists before its host listens.</summary>
    private static Uri Listening(Uri function)
    {
        Assert.True(Ui.AwaitPumped(Waiting.Apart(() => Waiting.UntilAsync(() => TakesAsync(function), TimeSpan.FromMinutes(1), every: TimeSpan.FromMilliseconds(250))),
            "func's listener"), $"func took no connection at {function}");
        return function;
    }

    private static async Task<bool> TakesAsync(Uri address)
    {
        using TcpClient client = new();
        try
        {
            await client.ConnectAsync(address.Host, address.Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int BreakAt(MainWindow window, string path, string statement)
    {
        int line = Array.FindIndex(File.ReadAllLines(path), text => text.Contains(statement, StringComparison.Ordinal));
        Assert.True(line >= 0, $"{path} has no {statement}");
        window.OpenFile(path);
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(line, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        return line;
    }

    /// <summary>The Aspire window's resources shown, as Find Action shows them.</summary>
    private static void ShowResources(MainWindow window)
    {
        Ui.Run(window, AppActions.FindAction);
        window.KeyTextInput("Show Aspire Resources");
        Ui.Settle();
        Ui.Press(window, "ENTER");
    }

    private static void ChooseResource(MainWindow window)
    {
        ShowResources(window);
        Ui.WaitFor(() => window.Aspire.Rows.Any(row => row.Resource.DisplayName == Resource), "the Functions app's row in the Aspire window");
        Ui.Click(AspireStandInTests.Words(AspireStandInTests.Row(window, Resource), Resource));
        Assert.Equal(Resource, window.Aspire.SelectedResource?.DisplayName);
    }

    private static AspireResource Snapshot(MainWindow window) => Assert.Single(window.Aspire.ResourceSnapshots, resource => resource.DisplayName == Resource);

    private AspireResourceRun Service(MainWindow window) => Assert.IsType<AspireResourceRun>(window.AspireRuntime.FindService(new(project.Host, Snapshot(window))));

    private static Uri HttpUrl(AspireResource resource) => resource.Data.GetProperty("urls").EnumerateArray()
        .Select(url => new Uri(url.GetProperty("url").GetString()!)).First(url => url.Scheme == "http");

    /// <summary>What the window shows of the run and the debugger, for a wait that gave up.</summary>
    private static string Seen(MainWindow window) =>
        "resources: " + string.Join(", ", window.Aspire.ResourceSnapshots.Select(resource => $"{resource.DisplayName} {resource.State}"))
        + "; services: " + string.Join(", ", window.AspireRuntime.Apps.SelectMany(app => app.Services).OfType<AspireResourceRun>()
            .Select(run => $"{run.ResourceName} func={run.ProcessId} worker={run.Worker} tab={run.DebugTab?.Name}/{run.DebugTab?.IsLive}/{run.DebugTab?.Session?.State}"))
        + "; tabs: " + string.Join(", ", window.Debugger.Tabs.Select(tab => $"{tab.Name} {tab.IsLive} {tab.Session?.State}: {Last(tab.Console.Document.Snapshot.ToString())}"));

    private static string Last(string text) => text.Length > 1024 ? text[^1024..] : text;

    /// <summary>What the AppHost's console and the Functions app's said, for a wait that gave up.</summary>
    private static string Said(MainWindow window)
    {
        window.AspireRuntime.LastAppConsole?.Flush();
        string host = window.AspireRuntime.LastAppConsole?.Document.Snapshot.ToString() ?? "no AppHost output";
        string functions = string.Join("\n", window.AspireRuntime.Apps.SelectMany(app => app.Services).OfType<AspireResourceRun>()
            .Select(run => { run.Console.Flush(); return run.Console.Document.Snapshot.ToString(); }));
        string said = $"AppHost:\n{host}\nFunctions app:\n{functions}";
        return said.Length > 8192 ? said[^8192..] : said;
    }

    /// <summary>After each test, its windows closed: one that left func, its worker or the AppHost running fails, naming
    /// them, and they are ended, so the next test's have its ports.</summary>
    public void Dispose()
    {
        if (project.WhyNot is not null || Waiting.Until(() => Leftovers().Count == 0, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)))
        {
            return;
        }
        Dictionary<int, string> left = Leftovers();
        foreach (int id in left.Keys)
        {
            OwnProcess.Of(id).Kill(alone: true);
        }
        Assert.Fail($"The test's programs outlived its window, and were ended:\n{string.Join("\n", left.Values)}");
    }

    /// <summary>Each program of the app's now running: its worker and the AppHost by the folder they were built in, and func
    /// among the run's own programs.</summary>
    private Dictionary<int, string> Leftovers()
    {
        string built = Path.Combine(PathNames.Canonical(project.Folder), "Fn", "bin");
        string host = Path.Combine(PathNames.Canonical(project.Folder), "Host", "bin");
        HashSet<int> own = [.. Processes.Descendants(Environment.ProcessId)];
        return Processes.Listed().Where(process => process.Value.Contains(built, StringComparison.Ordinal) || process.Value.Contains(host, StringComparison.Ordinal)
                || own.Contains(process.Key) && process.Value.Contains("func start", StringComparison.Ordinal))
            .ToDictionary(process => process.Key, process => $"{process.Key}: {process.Value}");
    }
}
