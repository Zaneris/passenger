using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Stop while a container configuration is still starting, against a real engine: during the host's build, and
/// during <c>up</c> while the services held at their start are attached to, it leaves no container, no debugger's CLI
/// on this machine and no live Debug tab, and says the start was stopped. Skips, saying why, where no Linux engine runs.</summary>
[Collection(Alone.Name)]
public sealed class ContainerRunStopsTests(ContainerRunDebuggingTests.Tools tools) : IClassFixture<ContainerRunDebuggingTests.Tools>
{
    [AvaloniaFact]
    public void Stop_during_the_build_and_during_up_as_held_services_attach_leaves_no_container_debugger_or_Debug_tab()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = ContainerRunsTests.Shop("stops");
        MainWindow window = ContainerRunsTests.Open(folder);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        try
        {
            // The first build of a folder never built is under way long enough to be stopped.
            StoppedAt(window, project, "Building", "the host's build");
            // The second start's build is up to date: Stop comes as Compose brings the held services up and the debugger attaches.
            StoppedAt(window, project, "Attaching the debugger", "the attach to the held services");
        }
        finally
        {
            Docker.Stopped(window, project, "every container to be taken down");
        }
        window.Close();
    }

    /// <summary>Debug pressed while Run is still bringing the configuration up (its cache not answering its health
    /// check, which up waits on, until the test lets it) takes Run's place: Run's up ends and says it was stopped, and
    /// Debug's run comes up whole, one container a service, its sessions left as they are once Run's start has
    /// ended.</summary>
    [AvaloniaFact]
    public void Debug_while_Run_comes_up_takes_its_place_leaving_one_run_and_its_sessions()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Slow("runthendebug");
        MainWindow window = ContainerRunsTests.Open(folder);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        try
        {
            Task<string?> running = window.RunAsync();
            Ui.PumpUntil(() => running.IsCompleted || window.Actions.Work.Of(AppActions.RunProgram)?.Stage.StartsWith("Bringing", StringComparison.Ordinal) == true
                && Docker.Said("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}") is { Length: > 0 }, "Run to begin bringing the services up", TimeSpan.FromMinutes(5));
            Assert.False(running.IsCompleted, $"Run ended before its services came up: {(running.IsCompleted ? running.Result : null)}");
            Task<string?> debugging = window.DebugAsync();
            // Debug's start holds the window's work until the gate opens, so the wait runs the window's loop without waiting it out.
            Assert.Equal("Compose: compose.yaml was stopped as it started", Ui.AwaitPumped(running, "Run's start to end as Debug takes its place", TimeSpan.FromMinutes(2)));
            Open(folder);
            string? refused = Ui.Await(debugging, "Debug's run to come up", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            DebugTab web = ContainerRunDebuggingTests.Tab(window, "web");
            DebugTab api = ContainerRunDebuggingTests.Tab(window, "api");
            Ui.WaitFor(() => web.Session is { State: DebugState.Running } && api.Session is { State: DebugState.Running }, "web and api to run under the debugger");
            Ui.Hold(TimeSpan.FromSeconds(3), "Run's ended start to show it takes nothing of Debug's");
            Assert.True(web.IsLive && api.IsLive, "Debug's sessions ended after Run's start did");
            string[] containers = Docker.Run("ps", "-q", "--filter", $"label=com.docker.compose.project={project}").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, containers.Length);
            Assert.Equal(["api", "cache", "web"], ContainerRunsTests.States(project).Keys.Order(StringComparer.Ordinal));
            Assert.All(ContainerRunsTests.States(project).Values, state => Assert.Equal("running", state));
            Assert.True(window.IsRunning, "Debug's run is not followed after Run's start ended");
        }
        finally
        {
            Docker.Stopped(window, project, "every container to be taken down");
        }
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the sessions to end with Stop");
        window.Close();
    }

    /// <summary>Another folder opened while Debug brings a configuration up ends that start: its up ended and what it made
    /// taken down, and no session of it opening in the next folder's Debug window.</summary>
    [AvaloniaFact]
    public void Opening_another_folder_while_Debug_comes_up_leaves_no_container_and_no_Debug_tab_in_the_next_folder()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Slow("leave");
        MainWindow window = ContainerRunsTests.Open(folder);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        string other = TestFolders.Create("ctr-next-");
        File.WriteAllText(Path.Combine(other, "notes.txt"), "the next folder\n");
        try
        {
            Task<string?> debugging = window.DebugAsync();
            Ui.PumpUntil(() => debugging.IsCompleted || window.Actions.Work.Of(AppActions.Debug)?.Stage.StartsWith("Attaching the debugger", StringComparison.Ordinal) == true
                && Docker.Said("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}") is { Length: > 0 }, "Debug to bring the services up and attach", TimeSpan.FromMinutes(5));
            Assert.False(debugging.IsCompleted, $"Debug ended before its services came up: {(debugging.IsCompleted ? debugging.Result : null)}");
            Task opening = window.OpenFolder(other);
            Ui.WaitFor(() => opening.IsCompleted, "the next folder to open", TimeSpan.FromMinutes(1));
            Assert.Equal(Refusals.SolutionClosed, Ui.Await(debugging, "Debug's start to end with its folder", TimeSpan.FromMinutes(2)));
            Docker.TakenDown(project, "the left folder's containers to be taken down");
            Ui.Hold(TimeSpan.FromSeconds(3), "a late session of the left folder's to show");
            Assert.Empty(window.Debugger.Tabs.Select(t => t.Name));
            Assert.False(window.Debugger.IsDebugging, "A session of the left folder's is debugging in the next one");
            Assert.Equal(string.Empty, Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}"));
        }
        finally
        {
            foreach (string left in Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Docker.Run("rm", "-f", left);
            }
        }
        window.Close();
    }

    /// <summary>ContainerRunsTests' Compose solution, its cache answering its health check only once the test opens its gate
    /// (<see cref="Open"/>), so up (and web, which needs it healthy) waits on it for as long as the test needs.</summary>
    private static (string Folder, string Project) Slow(string name)
    {
        (string folder, string project) = ContainerRunsTests.Shop(name);
        Directory.CreateDirectory(Path.Combine(folder, "gate"));
        string compose = Path.Combine(folder, "compose.yaml");
        File.WriteAllText(compose, File.ReadAllText(compose)
            .Replace("\"mkdir -p /www", "\"until [ -f /gate/open ]; do sleep 0.2; done && mkdir -p /www", StringComparison.Ordinal)
            .Replace("interval: 1s, retries: 30 }", "interval: 1s, retries: 600 }\n    volumes: [\"./gate:/gate:ro\"]", StringComparison.Ordinal));
        return (folder, project);
    }

    /// <summary>Opens the gate a slow folder's cache waits at, which every cache of its project sees.</summary>
    private static void Open(string folder) => File.WriteAllText(Path.Combine(folder, "gate", "open"), string.Empty);

    /// <summary>Debugs the configuration, presses Stop once Debug's stage says <paramref name="stage"/>, and checks what is left.</summary>
    private static void StoppedAt(MainWindow window, string project, string stage, string what)
    {
        Task<string?> debugging = window.DebugAsync();
        Ui.PumpUntil(() => window.Actions.Work.Of(AppActions.Debug)?.Stage.StartsWith(stage, StringComparison.Ordinal) == true || debugging.IsCompleted,
            $"Debug to reach {what}", TimeSpan.FromMinutes(5));
        Assert.False(debugging.IsCompleted, $"Debug ended before {what}: {(debugging.IsCompleted ? debugging.Result : null)}");
        string[] containers = Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(window.Perform(AppActions.Stop), "Stop could not run");
        string? said = Ui.Await(debugging, $"Debug to end once stopped during {what}", TimeSpan.FromMinutes(2));
        Assert.Equal("Compose: compose.yaml was stopped as it started", said);
        Docker.TakenDown(project, $"no container to be left after Stop during {what}");
        Ui.WaitFor(() => !window.Debugger.Tabs.Any(t => t.IsLive), $"no Debug tab to be left live after Stop during {what}");
        Ui.WaitFor(() => !Adapters(containers).Any(), $"no debugger's docker exec to be left after Stop during {what}: {string.Join("; ", Adapters(containers))}");
        Assert.False(window.IsRunning, $"The run's log is still followed after Stop during {what}");
    }

    /// <summary>This machine's docker exec processes of Passenger's container tools' debugger in the containers given (other
    /// runs' tests may debug in containers of their own meanwhile); none listed on Windows, whose task list has no command lines.</summary>
    private static string[] Adapters(string[] containers)
    {
        if (OperatingSystem.IsWindows() || containers.Length == 0)
        {
            return [];
        }
        ChildResult listed = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("ps", ["-axo", "command"], Environment.CurrentDirectory))), "the processes running here");
        return [.. listed.Output.Split('\n').Where(l => l.Contains("passenger-tools", StringComparison.Ordinal) && l.Contains(" dap", StringComparison.Ordinal)
            && containers.Any(c => l.Contains(c, StringComparison.Ordinal)))];
    }
}
