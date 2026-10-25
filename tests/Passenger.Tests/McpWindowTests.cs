using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Agents joining the window a person is using (<c>--mcp-attach</c>): what the window asks them before
/// an agent's first change, what it shows of their work, and what each tool answers there.</summary>
public sealed class McpWindowTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpWindowTests(RestoredFolders folders)
        : base(folders)
    {
    }

    /// <summary>An agent's package change is the NuGet window's own: the window shows it as its work while dotnet runs and
    /// lists the package installed once it is, and while the window's own change runs the agent's is refused, saying what is
    /// under way.</summary>
    [AvaloniaFact]
    public void An_agents_package_change_shows_as_the_nuget_windows_work_and_waits_its_turn_behind_the_windows_own()
    {
        string library = PathNames.Canonical(TestFolders.Create("mcp-nuget-window-"));
        DeleteAtEnd(library);
        string feed = Directory.CreateDirectory(Path.Combine(library, "feed")).FullName;
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Other.2.0.0.nupkg"), StandInFeed.Package("Passenger.Other", "2.0.0"));
        File.WriteAllText(Path.Combine(library, "nuget.config"),
            "<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"local\" value=\"feed\" />\n  </packageSources>\n"
            + "  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        string lib = Path.Combine(library, "Lib.csproj");
        File.WriteAllText(lib, TestProjects.Library());
        // dotnet add package's restore graph, held once the test puts "hold" there until it puts "go" (ten minutes at most).
        File.WriteAllText(Path.Combine(library, "Directory.Build.targets"), """
            <Project>
              <Target Name="Held" BeforeTargets="GenerateRestoreGraphFile" Condition="Exists('hold')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" Command="rm -f hold; touch held; n=0; while [ ! -f go ] &amp;&amp; [ $n -lt 2400 ]; do sleep 0.25; n=`expr $n + 1`; done" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Remove-Item hold; New-Item held -ItemType File | Out-Null; $n = 0; while (-not (Test-Path go) -and $n -lt 2400) { Start-Sleep -Milliseconds 250; $n++ }&quot;" />
              </Target>
            </Project>
            """);
        MainWindow window = AgentsWindow(library, languageServer: false);
        global::Passenger.App.ToolWindows.NuGetView nuget = window.NuGet;
        window.Perform(AppActions.ShowNuGet);
        Ui.Settle();
        nuget.Query = "sample";
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == "Passenger.Sample", "the NuGet window to list Passenger.Sample");
        Assert.False(nuget.Selected!.IsInstalled);
        List<string> worked = [];
        window.Actions.Work.Changed += (_, change) =>
        {
            if (change.Work.Id == global::Passenger.App.Packages.FolderPackages.Changing && !change.Ended)
            {
                worked.Add(change.Work.Stage);
            }
        };
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> installing = Task.Run(() => Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample" }));
            Press(Question(window, "the question for the agent's install"), "Allow");
            JsonElement installed = Await(installing, "the agent's install");
            Assert.Equal(["Lib.csproj"], installed.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!));
            Assert.True(worked.Contains("Installing Passenger.Sample 1.0.0 in Lib…"), $"The agent's install did not show as the NuGet window's work; it showed: {string.Join(" | ", worked)}");
            WaitFor(() => nuget.Selected is { Id: "Passenger.Sample", IsInstalled: true }, "the NuGet window to list the agent's package as installed");

            nuget.Query = "other";
            searched = nuget.RefreshAsync();
            Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == "Passenger.Other" && nuget.Versions.Count == 1, "Passenger.Other and its version");
            File.WriteAllText(Path.Combine(library, "hold"), string.Empty);
            Task<bool> windows = nuget.InstallAsync(Assert.Single(window.Projects));
            Ui.PumpUntil(() => File.Exists(Path.Combine(library, "held")), "the window's install to reach its held restore", TimeSpan.FromMinutes(2));
            Task<CallToolResult> again = Task.Run(() => Result(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.0.0" }));
            Ui.PumpUntil(() => again.IsCompleted, "the agent's install while the window's runs", AgentLimit);
            CallToolResult refused = again.GetAwaiter().GetResult();
            Assert.True(refused.IsError == true && Text(refused).Contains("Already under way: Installing Passenger.Other 2.0.0 in Lib…", StringComparison.Ordinal),
                $"The agent's install while the window's ran was answered: {Text(refused)}");
            File.WriteAllText(Path.Combine(library, "go"), string.Empty);
            Ui.Landed(() => windows.IsCompleted, "the window's install to end");
            Assert.True(windows.Result, nuget.Log);
            string written = File.ReadAllText(lib);
            Assert.True(written.Contains("Passenger.Other", StringComparison.Ordinal) && written.Contains("Passenger.Sample", StringComparison.Ordinal),
                $"Lib.csproj after both installs:\n{written}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(library, "go"), string.Empty);
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent searches the folder's feeds and installs a package with the NuGet window never built, its view
    /// made only as the person opens it, where the package is listed installed.</summary>
    [AvaloniaFact]
    public void An_agents_package_search_and_install_leave_the_nuget_window_unbuilt_until_the_person_opens_it()
    {
        string library = PathNames.Canonical(TestFolders.Create("mcp-nuget-unbuilt-"));
        DeleteAtEnd(library);
        string feed = Directory.CreateDirectory(Path.Combine(library, "feed")).FullName;
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        File.WriteAllText(Path.Combine(library, "nuget.config"),
            "<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"local\" value=\"feed\" />\n  </packageSources>\n"
            + "  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        MainWindow window = AgentsWindow(library, languageServer: false);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement found = Assert.Single(Await(Task.Run(() => Call(client, McpTools.NugetSearch, new() { ["query"] = "sample", ["feed"] = "local" })), "the agent's search").EnumerateArray());
            Assert.Equal("Passenger.Sample", found.GetProperty("id").GetString());
            Task<JsonElement> installing = Task.Run(() => Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample" }));
            Press(Question(window, "the question for the agent's install"), "Allow");
            JsonElement installed = Await(installing, "the agent's install");
            Assert.Equal(["Lib.csproj"], installed.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!));
            Assert.False(window.DockHost[ToolWindowIds.NuGet].IsBuilt, "The agent's package search or install built the hidden NuGet window's view");

            window.Perform(AppActions.ShowNuGet);
            global::Passenger.App.ToolWindows.NuGetView nuget = window.NuGet;
            Ui.Landed(() => nuget.Selected is { Id: "Passenger.Sample", IsInstalled: true }, "the NuGet window, opened, to list the agent's package as installed");
            Assert.True(nuget.FeedChoices.SequenceEqual([global::Passenger.Packages.FeedSet.All, "local"]),
                $"The NuGet window opened after the agent read the feeds offers [{string.Join(", ", nuget.FeedChoices)}], not All Feeds and the folder's feed");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A tool's answer is what its own operation came to, whatever the status bar says by the time the answer is
    /// written: here the person presses Run (with nothing to run) just as the agent's push lands.</summary>
    [AvaloniaFact]
    public void An_agents_answer_is_its_operations_outcome_whatever_the_status_bar_says_meanwhile()
    {
        string root = TestFolders.Create("mcp-push-");
        try
        {
            MainWindow window = AgentsWindow(Repository(root, refusing: false), languageServer: false);
            string? pressed = null;
            window.Vcs.Message += (_, message) =>
            {
                if (message.StartsWith("Pushed", StringComparison.Ordinal) || message.StartsWith("Push failed", StringComparison.Ordinal))
                {
                    _ = window.RunAsync();
                    pressed = window.Status;
                }
            };
            McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            try
            {
                Task<JsonElement> committing = Task.Run(() => Call(client, McpTools.VcsCommit, new() { ["message"] = "Two as a number", ["push"] = true }));
                Press(Question(window, "the question for the agent's commit"), "Allow");
                string answer = Await(committing, "the agent's commit and push").GetString()!;
                Assert.Equal("Nothing to run: no executable project", pressed);
                Assert.True(answer.EndsWith(" Two as a number; Pushed main", StringComparison.Ordinal),
                    $"The agent's commit and push was answered with what the status bar said by then, not what came of it: {answer}");
            }
            finally
            {
                Gone(client);
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [AvaloniaFact]
    public void An_agents_action_no_action_has_or_one_that_cannot_run_here_is_an_error_saying_why()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> misspelt = Task.Run(() => Result(client, McpTools.IdeAction, new() { ["action"] = "app.biuld" }));
            Press(Question(window, "the question for the agent's action"), "Allow");
            CallToolResult unknown = Await(misspelt, "the misspelt action");
            string said = Text(unknown);
            Assert.True(unknown.IsError == true, $"An action no action has was answered as run: {said}");
            Assert.Contains("No action is called app.biuld", said, StringComparison.Ordinal);

            CallToolResult commit = Await(Task.Run(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.Commit })), "the commit with no repository");
            said = Text(commit);
            Assert.True(commit.IsError == true, $"Commit in a folder with no repository was answered as run: {said}");
            Assert.Contains(Refusals.NoRepository, said, StringComparison.Ordinal);
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Commit), "Commit, refused for want of a repository, opened the Commit window anyway");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's question of an open file costs the person's window what it asks, not the file's size: the disk,
    /// which the agent's own file tools may have written, is looked at off the UI thread and the file read only once its
    /// length or time changed, so editor_text of one line of an open 20,000-line file allocates on the UI thread about what
    /// one of a ten-line file does; and the agent's own write is still taken before its next question.</summary>
    [AvaloniaFact]
    public void An_agents_question_of_an_open_file_costs_the_window_what_it_asks_not_the_files_size()
    {
        string small = Path.Combine(Folder, "Small.txt");
        string large = Path.Combine(Folder, "Large.txt");
        File.WriteAllText(small, string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i} of a short file\n")));
        File.WriteAllText(large, string.Concat(Enumerable.Range(1, 20_000).Select(i => $"line {i} of a long file, some words to make it as long as code is\n")));
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        Assert.Null(window.OpenFile(small));
        Assert.Null(window.OpenFile(large));
        Ui.Settle();
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            long few = Costs(small);
            long many = Costs(large);
            Assert.True(many < few + 64 * 1024,
                $"editor_text of an open 20,000-line file allocated {many / 1024} KB on the UI thread, of a ten-line file {few / 1024} KB: the file is read again there at each question");

            // As the agent's own file tools write.
            File.WriteAllText(large, "written by the agent's own tools\n" + File.ReadAllText(large));
            string said = Text(Await(Task.Run(() => Result(client, McpTools.EditorText, new() { ["path"] = large, ["from"] = 1, ["to"] = 1 })), "the question after the agent's write"));
            Assert.Contains("written by the agent's own tools", said, StringComparison.Ordinal);
        }
        finally
        {
            Gone(client);
            window.Close();
        }

        // The least one question of a file's first line allocated on the UI thread.
        long Costs(string path)
        {
            long least = long.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                CallToolResult answer = Await(Task.Run(() => Result(client, McpTools.EditorText, new() { ["path"] = path, ["from"] = 1, ["to"] = 1 })), "the agent's question of the file");
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.Contains("line 1 of", Text(answer), StringComparison.Ordinal);
            }
            return least;
        }
    }

    /// <summary>A fault of the IDE's met by an agent's call, stood in for by an action that fails as no action should.</summary>
    [AvaloniaFact]
    public void An_agents_call_that_meets_a_fault_of_the_IDEs_is_answered_with_it_and_logged()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        window.Actions.Add(new IdeAction("tests.fault", "Fault", "Tests", _ => throw new NullReferenceException("The fault's own words")));
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> asking = Task.Run(() => Result(client, McpTools.IdeAction, new() { ["action"] = "tests.fault" }));
            Press(Question(window, "the question for the agent's action"), "Allow");
            CallToolResult faulted = Await(asking, "the action that fails");
            string said = Text(faulted);
            Assert.True(faulted.IsError == true && said.Contains("NullReferenceException: The fault's own words", StringComparison.Ordinal),
                $"An agent's call that met a fault of the IDE's was answered without it: {said}");
            Assert.True(IdeErrors.Errors.Any(e => e.Kind == "NullReferenceException" && e.Message == "The fault's own words"),
                "A fault an agent's call met is not among the IDE's errors");
            IdeErrors.Flush();
            Assert.True(Ui.Shared(IdeErrors.LogPath).Contains("The fault's own words", StringComparison.Ordinal), "A fault an agent's call met is not in passenger.log");
            IdeErrors.Clear();
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_agent_attached_to_the_window_sets_a_breakpoint_the_person_sees()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        string program = Path.Combine(window.Folder!, "Program.cs");
        window.OpenFile(program);
        Ui.Settle();
        string socket = window.AgentSocket!;

        // The agent's calls run on the UI thread, which the test keeps running; it stays until the window is looked at.
        TaskCompletionSource looked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> agent = Task.Run(async () =>
        {
            await using McpClient client = await Start(["--mcp-attach", ProcessId]);
            await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 4 });
            string? folderSeen = (await Call(client, McpTools.Workspace)).GetProperty("folder").GetString();
            await looked.Task.WaitAsync(AgentLimit);
            return folderSeen;
        });
        WaitFor(() => window.Agents == 1 || agent.IsCompleted, "the window to show the agent", AgentLimit);
        int shown = window.Agents;
        // A breakpoint is asked about first: its condition or log message runs in the person's later sessions too.
        Press(Question(window, "the question for the agent's breakpoint"), "Allow");
        looked.TrySetResult();
        WaitFor(() => agent.IsCompleted, "the agent's calls", AgentLimit);
        Assert.Equal(1, shown);
        WaitFor(() => window.Agents == 0, "the window to see the agent go");
        Assert.Equal(window.Folder, agent.GetAwaiter().GetResult());
        Assert.Equal(3, Assert.Single(window.Debugger.Breakpoints.In(program)).Line);
        Assert.Contains(new LineMark(3, LineMarkKind.Breakpoint), window.EditorArea.CodeEditor.LineMarks);

        window.Close();
        Assert.False(File.Exists(socket), "A closed window left its socket for agents to try");
    }

    [AvaloniaFact]
    public void With_the_MCP_server_off_the_window_asks_to_let_an_agent_in_on_it_lets_agents_in_unasked_and_off_again_shuts_them_out()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        window.Show();
        window.KeepSettings();
        window.OpenFolder(Folder);
        Ui.Settle();

        string socket = window.AgentSocket ?? throw new InvalidOperationException($"The window does not listen for agents: {window.Status}");
        Assert.Equal([socket], Sockets());
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", Folder])), "the agent attaching");
        McpClient? second = null;
        try
        {
            Task<JsonElement> asking = Task.Run(() => Call(client, McpTools.Workspace));
            AgentAdmissionDialog asked = Admission(window, "the window to ask whether the agent may work there");
            Assert.Equal(0, window.Agents);
            Press(asked, AgentAdmissionDialog.Allow);
            Assert.Equal(window.Folder, Await(asking, "the agent's workspace call").GetProperty("folder").GetString());
            WaitFor(() => window.AgentStatus == "Agent connected", "the status bar to say an agent is connected");

            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Query = "agents";
            Ui.Settle();
            Assert.Equal([SettingsDialog.McpPage], dialog.Pages);
            Assert.Equal("Enable MCP Server", dialog.Shown[0]);
            dialog.Change(s => s with { McpServer = true });
            dialog.Ok();
            Ui.Settle();
            second = Await(Task.Run(() => Start(["--mcp-attach", Folder])), "a second agent attaching");
            Assert.Equal(window.Folder, Await(Task.Run(() => Call(second, McpTools.Workspace)), "the second agent's call").GetProperty("folder").GetString());
            Assert.Empty(window.OwnedWindows.OfType<AgentAdmissionDialog>());
            WaitFor(() => window.AgentStatus == "2 agents connected", "the status bar to count both agents");

            window.ApplySettings(window.Settings with { McpServer = false });
            WaitFor(() => window.AgentStatus is null, "the status bar to say no agent is on the window");
            Assert.True(File.Exists(socket), "Turning the MCP server off closed the window's socket, where it is to ask instead");
            Task<CallToolResult> again = Task.Run(() => Result(client, McpTools.Workspace));
            Press(Admission(window, "the window to ask again once agents were shut out"), AgentAdmissionDialog.DontAllow);
            CallToolResult refused = Await(again, "the call after agents were shut out");
            Assert.True(refused.IsError == true && Text(refused).Contains("did not let Test Agent work there", StringComparison.Ordinal),
                $"A call the person did not let in was answered: {Text(refused)}");
        }
        finally
        {
            Gone(client);
            if (second is not null)
            {
                Gone(second);
            }
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_agent_asks_before_its_first_run_and_the_window_stops_waiting_when_it_gives_up_or_goes()
    {
        Write("Program.cs", "Console.WriteLine(\"waiting\");\nThread.Sleep(Timeout.Infinite);\n");
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        Dictionary<string, object?> run = new() { ["waitSeconds"] = 600 };
        try
        {
            Task<CallToolResult> denied = Task.Run(() => Result(client, McpTools.Run, run));
            ConfirmDialog asked = Question(window, "the question for the agent's run");
            string[] said = Texts(asked);
            Assert.Contains("Allow run?", said);
            Assert.Contains(said, t => t.Contains("wants to run a configuration", StringComparison.Ordinal));
            Assert.Contains("waitSeconds: 600", said);
            Press(asked, "Deny");
            CallToolResult no = Await(denied, "the denied run");
            Assert.True(no.IsError, "A run the person denied ran");
            Assert.Contains("did not allow run", Assert.IsType<TextContentBlock>(Assert.Single(no.Content)).Text, StringComparison.Ordinal);
            Assert.False(window.Runner.IsRunning);

            using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            Task<CallToolResult> allowed = Task.Run(() => CallGivingUp(client, McpTools.Run, run, giveUp.Token));
            Press(Question(window, "the question asked again"), "Allow");
            WaitFor(() => window.Runner.IsRunning && window.AgentStatus == "Agent: Run a configuration", "the agent's run in the status bar");

            giveUp.Cancel();
            WaitFor(() => allowed.IsCompleted, "the call given up");
            WaitFor(() => window.AgentStatus == "Agent connected", "the window to stop waiting on the run the agent gave up on");
            Assert.True(window.Runner.IsRunning, "Giving up on the call stopped the program");
            window.Runner.Stop();
            WaitFor(() => !window.Runner.IsRunning, "the program stopped");

            Task<CallToolResult> again = Task.Run(() => Result(client, McpTools.Run, run));
            WaitFor(() => window.Runner.IsRunning || window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the run allowed before");
            Assert.Empty(window.OwnedWindows.OfType<ConfirmDialog>());
            WaitFor(() => window.AgentStatus == "Agent: Run a configuration", "the second run in the status bar");
            Gone(client);
            WaitFor(() => window.AgentStatus is null, "the window to stop waiting on the run of the agent that went");
            WaitFor(() => again.IsCompleted, "the call of the agent that went");
            Assert.True(again.Exception is not null || again.IsCanceled, "A call was answered after its agent went");
        }
        finally
        {
            window.Runner.Stop();
            WaitFor(() => !window.Runner.IsRunning, "the program stopped");
            window.Close();
        }
    }

    /// <summary>Each solution window of one Passenger listens for agents on a socket of its own, the second opened by File,
    /// Open in a window of its own as Settings says and letting agents in as the first does; an agent started in either's
    /// folder reaches that window, and a window closing takes its own socket alone.</summary>
    [AvaloniaFact]
    public void Each_solution_window_has_its_own_socket_and_an_agent_attaches_to_the_one_over_its_folder()
    {
        string own = Plain("mcp-first-");
        Directory.CreateDirectory(Path.Combine(own, "Inner"));
        string other = Plain("mcp-second-");
        MainWindow first = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        first.Show();
        first.KeepLayout();
        first.KeepSettings();
        first.Start(LaunchOptions.Empty with { Folder = own });
        Ui.Settle();
        first.ApplySettings(first.Settings with { McpServer = true, OpenProjectIn = OpenProjectIn.NewWindow });
        Assert.Null(first.Open(other));
        Ui.Settle();
        MainWindow second = Assert.Single(PassengerApp.Windows, w => w != first);
        Assert.Equal(PathNames.Canonical(other), second.Folder);
        string firstSocket = first.AgentSocket ?? throw new InvalidOperationException($"The first window does not listen for agents: {first.Status}");
        string secondSocket = second.AgentSocket ?? throw new InvalidOperationException($"The window File, Open opened does not listen for agents: {second.Status}");
        Assert.NotEqual(firstSocket, secondSocket);
        Assert.Equal(new[] { firstSocket, secondSocket }.Order(StringComparer.Ordinal), Sockets().Order(StringComparer.Ordinal));

        Assert.Equal(first.Folder, AttachedFolder(Path.Combine(own, "Inner")));
        Assert.Equal(second.Folder, AttachedFolder(other));

        second.Close();
        Assert.False(File.Exists(secondSocket), "A closed window left its socket");
        Assert.True(File.Exists(firstSocket), "Closing one window took another's socket");
        first.Close();
    }

    /// <summary>vcs_log with a path lists the commits Show History does: a file followed through its rename, and a
    /// selection's.</summary>
    [AvaloniaFact]
    public void An_agents_vcs_log_with_a_path_lists_the_commits_show_history_does_renames_followed()
    {
        string repository = TestFolders.Create("mcp-history-");
        const string shop = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        File.WriteAllText(Path.Combine(repository, "src", "Shop.cs"), shop);
        TestGit.Init(repository);
        File.WriteAllText(Path.Combine(repository, "src", "Shop.cs"), shop.Replace("count++", "count += 2", StringComparison.Ordinal));
        TestGit.Run(repository, "commit", "-q", "-am", "Twice");
        TestGit.Run(repository, "mv", "src/Shop.cs", "src/Store.cs");
        TestGit.Run(repository, "commit", "-q", "-m", "Store");
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "notes\n");
        TestGit.Run(repository, "add", "-A");
        TestGit.Run(repository, "commit", "-q", "-m", "Notes");
        MainWindow window = AgentsWindow(repository, languageServer: false);
        string store = Path.Combine(window.Folder!, "src", "Store.cs");
        Passenger.App.ToolWindows.GitLogView history = Await(window.ShowHistoryAsync(store), "the file's history")!;
        Ui.Landed(() => history.Rows.Count == 3, "the file's three commits");
        Passenger.App.ToolWindows.GitLogView lines = Await(window.ShowSelectionHistoryAsync(store, 4, 4), "line 5's history")!;
        Ui.Landed(() => lines.Rows.Count == 2, "the two commits that changed line 5");
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement log = Await(Task.Run(() => Call(client, McpTools.VcsLog, new() { ["path"] = Path.Combine("src", "Store.cs") })), "the agent's vcs_log of the file");
            Assert.Equal(history.Rows.Select(r => r.Commit.Short), log.EnumerateArray().Select(c => c.GetProperty("hash").GetString()));
            Assert.Equal([Path.Combine("src", "Store.cs"), Path.Combine("src", "Shop.cs"), Path.Combine("src", "Shop.cs")],
                log.EnumerateArray().Select(c => c.GetProperty("changes")[0].GetProperty("path").GetString()));
            Assert.Equal("renamed", log[0].GetProperty("changes")[0].GetProperty("kind").GetString());

            JsonElement changed = Await(Task.Run(() => Call(client, McpTools.VcsLog, new() { ["path"] = Path.Combine("src", "Store.cs"), ["startLine"] = 5, ["endLine"] = 5 })),
                "the agent's vcs_log of line 5");
            Assert.Equal(lines.Rows.Select(r => r.Commit.Short), changed.EnumerateArray().Select(c => c.GetProperty("hash").GetString()));
            Assert.Equal(["Twice", "First"], changed.EnumerateArray().Select(c => c.GetProperty("subject").GetString()));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>The folder of the window an agent started in a folder reaches with <c>--mcp-attach</c>.</summary>
    private string? AttachedFolder(string workingDirectory) => Await(Task.Run(async () =>
    {
        await using McpClient client = await Start(["--mcp-attach"], workingDirectory);
        return (await Call(client, McpTools.Workspace)).GetProperty("folder").GetString();
    }), $"an agent attaching from {workingDirectory}");
}

/// <summary>McpWindowTests' agents on the person's window where one debugs through the window (a program in the terminal,
/// stopped, evaluated and typed into) or where what it tests is held to a time (a project file's diagnostics during a
/// build, the window's keys while ten thousand usages land), so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class McpWindowDebuggingTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpWindowDebuggingTests(RestoredFolders folders)
        : base(folders)
    {
    }

    /// <summary>An agent's diagnostics for a project file wait for that file's own checks alone, never for a build running
    /// meanwhile.</summary>
    [AvaloniaFact]
    public void An_agents_diagnostics_for_a_project_file_come_from_its_own_checks_while_a_build_runs()
    {
        File.WriteAllText(Path.Combine(Folder, "Directory.Build.targets"), """
            <Project>
              <Target Name="Slow" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true' And Exists('slow')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; slow.pid; exec sleep 120" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path slow.pid -Value $PID; Start-Sleep -Seconds 120&quot;" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(Folder, "slow"), string.Empty);
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        int step = 0;
        try
        {
            Assert.True(window.Perform(AppActions.Build));
            step = Processes.WrittenId(Path.Combine(Folder, "slow.pid"), "the build's slow step to start");

            window.OpenFile(Path.Combine(Folder, "Sum.csproj"));
            window.EditorArea.CodeEditor.GoTo(new Passenger.Text.TextPosition(6, 0));
            window.KeyTextInput("<Import Project=\"Missing.props\" />");
            Stopwatch asked = Stopwatch.StartNew();
            Task<JsonElement> diagnostics = Task.Run(() => Call(client, McpTools.Diagnostics, new() { ["path"] = "Sum.csproj" }));
            Ui.PumpUntil(() => diagnostics.IsCompleted, "the project file's diagnostics", AgentLimit);
            asked.Stop();
            JsonElement missing = Assert.Single(diagnostics.GetAwaiter().GetResult().EnumerateArray());
            Assert.Contains("Missing.props", missing.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.True(window.IsBuilding, "The build ended before the agent's diagnostics were answered");
            Assert.True(asked.Elapsed < TimeSpan.FromSeconds(20).Scaled(), $"The project file's diagnostics took {asked.Elapsed.TotalSeconds:F0} s while a build ran");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Processes.Kill(step);
            Ui.PumpUntil(() => !window.IsBuilding, "the build to stop", TimeSpan.FromMinutes(1));
            Gone(client);
            window.Close();
        }
    }

    /// <summary>On the person's window an agent is asked once, the first time, before it runs a configuration, sets a
    /// breakpoint, debugs, evaluates code in the paused program (setting a value there unasked after) or types into its
    /// input; saving and stopping are never asked about. The configuration uses the terminal: run answers with what the
    /// program's tab shows, debug_start stops at a breakpoint on Main's first line, debug_input types into its tab, and
    /// output reads the tab, the program's end in it.</summary>
    [AvaloniaFact]
    public void An_agent_is_asked_once_before_it_first_runs_sets_a_breakpoint_evaluates_or_types_into_a_program_in_the_terminal()
    {
        Write("Program.cs", File.ReadAllText(Debugging.Helpers.Debuggees.Source("TerminalConsoleApp", "Program.cs")));
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        window.SetConfigurations([new global::Passenger.App.Run.RunConfiguration
        {
            Name = "In the terminal",
            Project = Path.Combine(window.Folder!, "Sum.csproj"),
            Console = global::Passenger.App.Run.ConsoleKind.Terminal,
        }], "In the terminal");
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Assert.Equal("Nothing to save", Unasked(window, client, McpTools.SaveAll).GetString());
            JsonElement ran = Asked(window, client, McpTools.Run, "run a configuration", new() { ["waitSeconds"] = 5 });
            string output = ran.GetProperty("output").GetString()!;
            Assert.True(ran.GetProperty("stillRunning").GetBoolean() && output.Contains("red at the third row's fifth column", StringComparison.Ordinal),
                $"The run in the terminal was answered without the program waiting for its key in its tab: {ran}");
            Assert.Equal("Stopped", Unasked(window, client, McpTools.Stop).GetString());
            WaitFor(() => window.RunInTerminal is { IsRunning: false }, "the program in the terminal to end");

            Asked(window, client, McpTools.BreakpointSet, "set a breakpoint", new() { ["path"] = "Program.cs", ["line"] = 1 });
            Unasked(window, client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 24 });
            JsonElement stop = Asked(window, client, McpTools.DebugStart, "debug a configuration");
            Assert.True(stop.GetProperty("state").GetString() == "paused" && stop.GetProperty("location").GetString()!.StartsWith("Program.cs:1:", StringComparison.Ordinal),
                $"Debugged in the terminal, the program did not stop on Main's first line: {stop}");
            Assert.Equal("42", Asked(window, client, McpTools.DebugEvaluate, "evaluate an expression", new() { ["expression"] = "6 * 7" }).GetProperty("value").GetString());
            Assert.Equal("0", Unasked(window, client, McpTools.DebugEvaluate, new() { ["expression"] = "args.Length" }).GetProperty("value").GetString());
            string set = Unasked(window, client, McpTools.DebugEvaluate, new() { ["expression"] = "line", ["value"] = "\"forty\"" }).GetProperty("value").GetString()!;
            Assert.Contains("forty", set, StringComparison.Ordinal);
            set = Unasked(window, client, McpTools.DebugEvaluate, new() { ["expression"] = "line", ["value"] = "\"again\"" }).GetProperty("value").GetString()!;
            Assert.Contains("again", set, StringComparison.Ordinal);

            Await(Task.Run(() => Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 1, ["remove"] = true })), "the breakpoint's removal");
            Await(Task.Run(() => Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 24, ["remove"] = true })), "the other breakpoint's removal");
            Await(Task.Run(() => Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 1 })), "the program let go");
            WaitFor(() => window.Debugger.Session?.Terminal?.Session.Screen.Lines.Any(l => l.Text() == "key?") == true, "the program to ask for a key in its tab");
            Asked(window, client, McpTools.DebugInput, "type into the program's console", new() { ["text"] = "q", ["newline"] = false });
            Unasked(window, client, McpTools.DebugInput, new() { ["text"] = "hello" });
            WaitFor(() => !window.Debugger.IsDebugging && window.Debugger.Session?.Terminal?.Ended.IsCompleted == true, "the program's end");
            string shown = Await(Task.Run(() => Call(client, McpTools.Output, new() { ["of"] = "debug" })), "the program's output").GetString()!;
            Assert.True(shown.Contains("key? key q", StringComparison.Ordinal) && shown.Contains("line hello", StringComparison.Ordinal)
                && shown.Contains("Process finished with exit code 3.", StringComparison.Ordinal), $"output read, of the program's tab:\n{shown}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_window_takes_keys_while_an_attached_agent_finds_ten_thousand_usages_each_with_its_line()
    {
        // Every call under a long comment, so a usage's line read from its file's start reads each file 2,500 times over.
        for (int file = 0; file < 4; file++)
        {
            System.Text.StringBuilder text = new($"static class Uses{file}\n{{\n    public static int All()\n    {{\n        int total = 0;\n");
            for (int call = 0; call < 2500; call++)
            {
                text.Append("        // ").Append('=', 100).Append('\n');
                text.Append("        total += Helper.Twice(").Append(call).Append(");\n");
            }
            Write($"Uses{file}.cs", text.Append("        return total;\n    }\n}\n").ToString());
        }
        Write("Notes.txt", string.Empty);
        MainWindow window = AgentsWindow(Folder);
        window.OpenFile(Path.Combine(window.Folder!, "Helper.cs"));
        window.OpenFile(Path.Combine(window.Folder!, "Notes.txt"));
        window.EditorArea.CodeEditor.Focus();
        Ui.Settle();
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            // The language server has the folder loaded before the window is timed, which is the server's work, not the agent's.
            Await(Task.Run(() => Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" })), "the language server");

            Task<JsonElement> usages = Task.Run(() => Call(client, McpTools.Usages, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 }));
            int typed = 0;
            TimeSpan longest;
            using (InputWait waits = new())
            {
                // The call's own limit ends this if Passenger never answers.
                while (!usages.IsCompleted)
                {
                    window.KeyTextInput("x");
                    typed++;
                    Ui.Hold(TimeSpan.FromMilliseconds(50), "a pause between keys, as a person types");
                }
                waits.Stop();
                longest = waits.Longest;
            }
            IReadOnlyList<(string Path, int Line, int Column, string Text)> found = Used(usages.GetAwaiter().GetResult());
            Ui.Settle();

            Assert.Equal(10_002, found.Count);
            Assert.All(found, place => Assert.Contains("Twice(", place.Text, StringComparison.Ordinal));
            Assert.Contains(found, place => place.Path == "Uses3.cs" && place.Line == 5005 && place.Text == "total += Helper.Twice(2499);");
            Assert.Equal(new string('x', typed), window.EditorArea.Active!.Text.Snapshot.ToString());
            Assert.True(longest < TimeSpan.FromMilliseconds(250),
                $"a key waited {longest.TotalMilliseconds:F0} ms for the window while the agent's {found.Count} usages were found");

            JsonElement callers = Await(Task.Run(() => Call(client, McpTools.Hierarchy, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 })), "the callers");
            Assert.Equal(["Uses0.cs:7 total += Helper.Twice(0);", "Uses1.cs:7 total += Helper.Twice(0);", "Uses2.cs:7 total += Helper.Twice(0);", "Uses3.cs:7 total += Helper.Twice(0);"],
                callers.EnumerateArray().Select(c => $"{c.GetProperty("path").GetString()}:{c.GetProperty("line").GetInt32()} {c.GetProperty("text").GetString()}")
                    .Where(c => c.StartsWith("Uses", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }
}
