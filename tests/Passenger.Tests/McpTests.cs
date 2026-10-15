using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Passenger as an agent's IDE, over the Model Context Protocol with the SDK's own client: an
/// agent that starts Passenger with no window (<c>--mcp</c>) and works a project through it, and one
/// that joins the window a person is using (<c>--mcp-attach</c>) and changes what they see, once the
/// person lets agents in, asking them before it first changes anything; and agents that look at and drive the Avalonia app
/// Passenger runs (McpTests.AvaloniaApp.cs). Every exchange with Passenger has a time limit,
/// and one that runs out or fails ends the test naming it and quoting what Passenger said meanwhile.</summary>
[Collection(Alone.Name)]
public sealed partial class McpTests : IClassFixture<RestoredFolders>, IClassFixture<AgentAppFolder>, IDisposable
{
    /// <summary>The build's, beside the tests, or an installed copy that PASSENGER_EXECUTABLE names: CI's installer job
    /// has an agent debug through the Passenger its Windows installer installed.</summary>
    private static readonly string Passenger = Environment.GetEnvironmentVariable("PASSENGER_EXECUTABLE") is { Length: > 0 } installed
        ? installed
        : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");

    private readonly string folder;
    private readonly string settings = TestFolders.Create("agents-");
    private readonly List<string> errors = [];
    private readonly List<Process> passengers = [];

    public McpTests(RestoredFolders folders, AgentAppFolder app)
    {
        appFolder = app.Folder;
        string made = string.Empty;
        TestFolders.Prepare(() => made = folders.Take("mcp-", project =>
        {
            File.WriteAllText(Path.Combine(project, "Sum.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(project, "Program.cs"),
                "int total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += Helper.Twice(i);\n}\nConsole.WriteLine($\"total {total}\");\nthrow new InvalidOperationException(\"the end\");\n");
            File.WriteAllText(Path.Combine(project, "Helper.cs"),
                "static class Helper\n{\n    public static int Twice(int value)\n    {\n        int unused = 1;\n        return value * 2;\n    }\n}\n");
            return project;
        }), settings);
        folder = made;
    }

    public void Dispose()
    {
        // Each Passenger the test started is given its input's end and its window's end limit to go, then killed.
        lock (passengers)
        {
            foreach (Process passenger in passengers)
            {
                EndInput(passenger);
                if (!passenger.WaitForExit(MainWindow.EndLimit + TimeSpan.FromSeconds(5)))
                {
                    ChildProcesses.Kill(passenger);
                }
                passenger.Dispose();
            }
        }
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
        foreach (string made in madeFolders)
        {
            TestFolders.Delete(made);
        }
    }

    [Fact]
    public async Task An_agent_with_a_windowless_Passenger_finds_code_reads_its_warnings_and_debugs_it()
    {
        await using McpClient client = await Start(["--mcp", folder]);
        IList<McpClientTool> tools = await Exchange("Listing the tools", TimeSpan.FromMinutes(1), cancel => client.ListToolsAsync(cancellationToken: cancel).AsTask());
        Assert.Contains(tools, t => t.Name == "debug_start");

        JsonElement symbol = Assert.Single((await Call(client, "find_symbol", new() { ["query"] = "Twice" })).EnumerateArray());
        Assert.Equal("Helper.cs", symbol.GetProperty("path").GetString());
        Assert.Equal(3, symbol.GetProperty("line").GetInt32());

        JsonElement warning = Assert.Single((await Call(client, "diagnostics", new() { ["path"] = "Helper.cs" })).EnumerateArray(),
            d => d.GetProperty("code").GetString() == "CS0219");
        Assert.Equal("warning", warning.GetProperty("severity").GetString());
        Assert.Equal(5, warning.GetProperty("line").GetInt32());

        // The breakpoint is the window's own: the stop comes back with where it is and the locals there.
        await Call(client, "breakpoint_set", new() { ["path"] = "Program.cs", ["line"] = 4 });
        JsonElement stop = await Call(client, "debug_start");
        Assert.Equal("paused", stop.GetProperty("state").GetString());
        Assert.StartsWith("Program.cs:4:", stop.GetProperty("location").GetString(), StringComparison.Ordinal);
        Assert.Equal("1", Local(stop, "i"));
        Assert.Equal("0", Local(stop, "total"));
        Assert.Equal("42", (await Call(client, "debug_evaluate", new() { ["expression"] = "Helper.Twice(i) + 40" })).GetProperty("value").GetString());
        string architecture = (await Call(client, "debug_evaluate", new() { ["expression"] = "(int)System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture" })).GetProperty("value").GetString()!;
        Assert.Equal(((int)RuntimeInformation.ProcessArchitecture).ToString(CultureInfo.InvariantCulture), architecture);
        TestContext.Current.TestOutputHelper?.WriteLine($"Native architecture: test host {RuntimeInformation.ProcessArchitecture}, debuggee {(Architecture)int.Parse(architecture, CultureInfo.InvariantCulture)}");

        stop = await Call(client, "debug_continue");
        Assert.Equal("2", Local(stop, "i"));

        // With the breakpoint gone the program runs to its unhandled exception, which is named.
        await Call(client, "breakpoint_remove", new() { ["path"] = "Program.cs", ["line"] = 4 });
        stop = await Call(client, "debug_continue");
        Assert.Equal("exception", stop.GetProperty("reason").GetString());
        Assert.StartsWith("System.InvalidOperationException: the end", stop.GetProperty("exception").GetString(), StringComparison.Ordinal);
        Assert.Contains("total 12", (await Call(client, "debug_output")).GetString(), StringComparison.Ordinal);

        CallToolResult picture = await Result(client, "screenshot");
        ImageContentBlock image = Assert.IsType<ImageContentBlock>(Assert.Single(picture.Content));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], image.DecodedData.Slice(0, 4).ToArray());

        await Call(client, "stop");
        Assert.Equal("not debugging", (await Call(client, "debug_state")).GetProperty("state").GetString());

        // What a tool refuses reaches the agent in words that say what to do instead.
        CallToolResult refused = await Result(client, "debug_continue");
        Assert.True(refused.IsError);
        Assert.Contains("debug_start starts it", Assert.IsType<TextContentBlock>(Assert.Single(refused.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_attaches_to_a_program_already_running_stops_it_at_a_breakpoint_and_detaches_leaving_it_running()
    {
        // Started by the test as a terminal would start it, in its loop when it says so.
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost("LoopingConsoleApp"), ["sleep"]);
        start.RedirectStandardOutput = true;
        using Process looping = ChildProcesses.Start(start);
        try
        {
            Assert.Equal("looping", looping.StandardOutput.ReadLine());
            await using McpClient client = await Start(["--mcp", folder]);
            JsonElement listed = Assert.Single((await Call(client, "debug_processes")).EnumerateArray(), p => p.GetProperty("processId").GetInt32() == looping.Id);
            Assert.Equal("LoopingConsoleApp", listed.GetProperty("program").GetString());

            string source = Debugging.Helpers.Debuggees.Source("LoopingConsoleApp", "Program.cs");
            int line = Debugging.Helpers.Debuggees.Line("LoopingConsoleApp", "turns++;");
            await Call(client, "breakpoint_set", new() { ["path"] = source, ["line"] = line });
            JsonElement stop = await Call(client, "debug_attach", new() { ["processId"] = looping.Id });
            Assert.Equal("paused", stop.GetProperty("state").GetString());
            Assert.Equal("breakpoint", stop.GetProperty("reason").GetString());
            Assert.EndsWith($"Program.cs:{line}:5", stop.GetProperty("location").GetString(), StringComparison.Ordinal);
            Assert.True(long.TryParse(Local(stop, "turns"), out long turns) && turns >= 0, $"The stop's locals had no count of turns: {stop}");

            // Let go, the program runs on in its loop, which the debugger no longer stops.
            Assert.Equal("Detached from LoopingConsoleApp, which runs on", (await Call(client, "debug_detach")).GetString());
            Assert.Equal("not debugging", (await Call(client, "debug_state")).GetProperty("state").GetString());
            Assert.False(looping.WaitForExit(TimeSpan.FromSeconds(2)), "The program the agent detached from ended");
        }
        finally
        {
            Processes.Kill(looping.Id);
        }
    }

    [Fact]
    public async Task An_agent_sets_an_exception_breakpoint_on_a_type_and_the_program_stops_where_that_type_is_thrown()
    {
        Write("Program.cs", "for (int turn = 1; turn <= 2; turn++)\n{\n    try { throw new ArgumentException(\"argument\"); } catch (ArgumentException) { }\n"
            + "    try { throw new InvalidOperationException($\"operation {turn}\"); } catch (InvalidOperationException) { }\n}\nConsole.WriteLine(\"done\");\n");
        await using McpClient client = await Start(["--mcp", folder]);
        await Call(client, "breakpoint_set", new() { ["exception"] = "System.InvalidOperationException" });
        // Listed with Rider's "Any exception", the new one in every scenario as Rider sets one on a type.
        JsonElement[] exceptions = [.. (await Call(client, "breakpoints")).EnumerateArray()
            .Where(b => b.TryGetProperty("exception", out JsonElement type) && type.ValueKind == JsonValueKind.String)];
        Assert.Equal(["Any exception", "System.InvalidOperationException"], exceptions.Select(b => b.GetProperty("exception").GetString()));
        Assert.Equal(["thrownInUserCode", "thrownInExternalCode", "handledInUserCode", "handledInExternalCode", "unhandled"],
            exceptions[1].GetProperty("scenarios").EnumerateArray().Select(s => s.GetString()));

        // The argument exception passes; the stop is the first turn's, at the line that throws the type named.
        JsonElement stop = await Call(client, "debug_start");
        Assert.Equal("exception", stop.GetProperty("reason").GetString());
        Assert.StartsWith("System.InvalidOperationException: operation 1", stop.GetProperty("exception").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Program.cs:4:", stop.GetProperty("location").GetString(), StringComparison.Ordinal);

        // Removed while paused, it lets the second turn's pass: the program runs to its end.
        await Call(client, "breakpoint_remove", new() { ["exception"] = "System.InvalidOperationException" });
        stop = await Call(client, "debug_continue");
        Assert.Equal("not debugging", stop.GetProperty("state").GetString());
        Assert.Contains("done", (await Call(client, "debug_output")).GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_reads_and_commits_changes_and_runs_a_command_in_the_ides_terminal()
    {
        string repository = TestFolders.Create("mcp-git-");
        try
        {
            File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\nthree\n");
            TestGit.Init(repository);
            File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n2\nthree\n");
            File.WriteAllText(Path.Combine(repository, "New.txt"), "new\n");

            await using McpClient client = await Start(["--mcp", repository]);
            JsonElement status = await Call(client, "vcs_status");
            Assert.Equal("main", status.GetProperty("branch").GetString());
            Assert.Equal(["Notes.txt modified", "New.txt unversioned"], status.GetProperty("changes").EnumerateArray()
                .Select(c => $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}").Order(StringComparer.Ordinal).Reverse());
            string diff = (await Call(client, "vcs_diff", new() { ["path"] = "Notes.txt" })).GetString()!;
            Assert.Contains("-two\n+2\n", diff, StringComparison.Ordinal);

            // With no paths, every change but the new file goes in, as the Commit window has them chosen.
            Assert.EndsWith(" Two as a number", (await Call(client, "vcs_commit", new() { ["message"] = "Two as a number" })).GetString(), StringComparison.Ordinal);
            JsonElement log = await Call(client, "vcs_log", new() { ["count"] = 5 });
            Assert.Equal(["Two as a number", "First"], log.EnumerateArray().Select(c => c.GetProperty("subject").GetString()));
            Assert.Equal(["New.txt"], (await Call(client, "vcs_status")).GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()));

            // A branch an agent names is a name, never one of git's options: git switch --orphan=x would empty the tree.
            CallToolResult orphan = await Result(client, "vcs_checkout", new() { ["branch"] = "--orphan=x" });
            Assert.True(orphan.IsError, "vcs_checkout took the branch --orphan=x for an option, and checked out an empty branch");
            Assert.True(File.Exists(Path.Combine(repository, "Notes.txt")), "Checking out the branch --orphan=x emptied the working tree");
            Assert.Equal("main", (await Call(client, "vcs_status")).GetProperty("branch").GetString());

            // The IDE's own terminal runs it in the folder, and its answer comes back.
            // Windows' own PowerShell (5.1, where pwsh is not installed) has no &&, and sh has no PowerShell subexpressions.
            string command = OperatingSystem.IsWindows() ? "echo (6*7); ls" : "echo $((6*7)) && ls";
            string answer = (await Call(client, "terminal_run", new() { ["command"] = command })).GetString()!;
            Assert.Contains("42", answer, StringComparison.Ordinal);
            Assert.Contains("Notes.txt", answer, StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                // PowerShell's history is the run's (HISTFILE), as sh's is: never the person's, which PSReadLine keeps.
                string history = Path.Combine(TestRun.Root, "history");
                Assert.True(File.Exists(history) && File.ReadAllText(history).Contains(command, StringComparison.Ordinal),
                    $"PowerShell kept the command typed into the terminal, {command}, elsewhere than the run's history ({history}): in the person's own");
            }
            else
            {
                // Past the scrollback's limit, where each line printed drops the oldest, a command's answer is still its own.
                int beyond = global::Passenger.Terminal.TerminalScreen.ScrollbackLimit + 100;
                await Call(client, "terminal_run", new() { ["command"] = $"seq 1 {beyond}" });
                string past = (await Call(client, "terminal_run", new() { ["command"] = "echo one; echo two" })).GetString()!;
                Assert.True(past.Contains("one\ntwo", StringComparison.Ordinal), $"past the scrollback's limit, terminal_run answered a command that printed one and two with:\n{past}");
            }
        }
        finally
        {
            TestFolders.Delete(repository);
        }
    }

    /// <summary>A merge stopped for a conflict: the agent finds it in vcs_status as the Conflicts dialog lists it (what is
    /// under way, each side's branch, what each did to the file), resolves it with theirs, and its commit finishes the merge.</summary>
    [Fact]
    public async Task An_agent_sees_a_merges_conflict_in_vcs_status_resolves_it_with_theirs_and_commits_the_merge()
    {
        string repository = TestFolders.Create("mcp-conflicts-");
        try
        {
            string notes = Path.Combine(repository, "Notes.txt");
            File.WriteAllText(notes, "one\ntwo\nthree\n");
            TestGit.Init(repository);
            TestGit.Run(repository, "switch", "-q", "-c", "feature");
            File.WriteAllText(notes, "one\n2\nthree\n");
            TestGit.Run(repository, "commit", "-q", "-am", "Two as a number");
            TestGit.Run(repository, "switch", "-q", "main");
            File.WriteAllText(notes, "one\nII\nthree\n");
            TestGit.Run(repository, "commit", "-q", "-am", "Two in Roman");
            ChildResult merging = await ChildProcesses.RunAsync(ChildProcesses.Command("git", ["merge", "feature"], repository), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
            Assert.False(merging.Succeeded, $"The merge was to stop for its conflict: {merging.Output}");

            await using McpClient client = await Start(["--mcp", repository]);
            JsonElement conflicts = (await Call(client, "vcs_status")).GetProperty("conflicts");
            Assert.Equal("Merging branch feature into branch main", conflicts.GetProperty("operation").GetString());
            Assert.Equal(("main", "feature"), (conflicts.GetProperty("yours").GetString(), conflicts.GetProperty("theirs").GetString()));
            JsonElement file = Assert.Single(conflicts.GetProperty("files").EnumerateArray());
            Assert.Equal(("Notes.txt", "modified", "modified"), (file.GetProperty("path").GetString(), file.GetProperty("yours").GetString(), file.GetProperty("theirs").GetString()));
            CallToolResult early = await Result(client, "vcs_commit", new() { ["message"] = "Half a merge" });
            Assert.True(early.IsError == true, "A commit was made with a file still in conflict");

            Assert.Equal("Notes.txt resolved; no conflicts are left", (await Call(client, "vcs_resolve", new() { ["path"] = "Notes.txt", ["take"] = "theirs" })).GetString());
            Assert.Equal("one\n2\nthree\n", File.ReadAllText(notes));
            JsonElement status = await Call(client, "vcs_status");
            Assert.True(!status.TryGetProperty("conflicts", out JsonElement left) || left.ValueKind == JsonValueKind.Null, $"vcs_status still lists conflicts: {left}");

            await Call(client, "vcs_commit", new() { ["message"] = "Merge feature" });
            Assert.Equal(3, TestGit.Run(repository, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal("one\n2\nthree\n", TestGit.Run(repository, "show", "HEAD:Notes.txt"));
        }
        finally
        {
            TestFolders.Delete(repository);
        }
    }

    /// <summary>Commit and Push to a remote that refuses every push: the agent hears the refusal, in git's words, as the
    /// tool's error, and the commit stays made, as the Commit window's Commit and Push leaves it.</summary>
    [Fact]
    public async Task An_agents_push_the_remote_refuses_is_an_error_naming_the_refusal_and_the_commit_stays_made()
    {
        string root = TestFolders.Create("mcp-push-");
        try
        {
            string work = Repository(root, refusing: true);
            await using McpClient client = await Start(["--mcp", work]);
            CallToolResult refused = await Result(client, "vcs_commit", new() { ["message"] = "Two as a number", ["push"] = true });
            string said = string.Concat(refused.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.True(refused.IsError == true, $"A push the remote refused was answered as done: {said}");
            Assert.Contains("pre-receive hook declined", said, StringComparison.Ordinal);
            Assert.Contains("pushes are closed here", said, StringComparison.Ordinal);
            Assert.Equal("Two as a number", TestGit.Run(work, "log", "-1", "--format=%s").Trim());
            Assert.True(TestGit.Run(Path.Combine(root, "remote.git"), "for-each-ref", "refs/heads").Trim().Length == 0, "The remote took the push it refused");
        }
        finally
        {
            TestFolders.Delete(root);
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
            MainWindow window = AgentsWindow(Repository(root, refusing: false));
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
                Task<JsonElement> committing = Task.Run(() => Call(client, "vcs_commit", new() { ["message"] = "Two as a number", ["push"] = true }));
                Press(Question(window, "the question for the agent's commit"), "Allow");
                string answer = Await(committing, "the agent's commit and push").GetString()!;
                Assert.Equal("Nothing to run: no executable project", pressed);
                Assert.True(answer.EndsWith(" Two as a number; Pushed main", StringComparison.Ordinal),
                    $"The agent's commit and push was answered with what the status bar said by then, not what came of it: {answer}");
            }
            finally
            {
                Await(Task.Run(async () =>
                {
                    await client.DisposeAsync();
                    return true;
                }), "the agent going");
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
        MainWindow window = AgentsWindow(folder);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            // A misspelt id, asked about first as any action an agent runs on the person's window is.
            Task<CallToolResult> misspelt = Task.Run(() => Result(client, "ide_action", new() { ["action"] = "app.biuld" }));
            Press(Question(window, "the question for the agent's action"), "Allow");
            CallToolResult unknown = Await(misspelt, "the misspelt action");
            string said = string.Concat(unknown.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.True(unknown.IsError == true, $"An action no action has was answered as run: {said}");
            Assert.Contains("No action is called app.biuld", said, StringComparison.Ordinal);

            // Commit, in a folder in no repository, says that is why; the Commit window stays as it was.
            CallToolResult commit = Await(Task.Run(() => Result(client, "ide_action", new() { ["action"] = AppActions.Commit })), "the commit with no repository");
            said = string.Concat(commit.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.True(commit.IsError == true, $"Commit in a folder with no repository was answered as run: {said}");
            Assert.Contains("not in a Git repository", said, StringComparison.Ordinal);
            Assert.False(window.DockHost.IsShown("commit"), "Commit, refused for want of a repository, opened the Commit window anyway");
        }
        finally
        {
            Await(Task.Run(async () =>
            {
                await client.DisposeAsync();
                return true;
            }), "the agent going");
            window.Close();
        }
    }

    /// <summary>Passenger killed outright (SIGKILL, TerminateProcess), as a crash ends it, while it runs, profiles, debugs
    /// and builds a program and its language server loads the project. Each program writes its id to a file named for how
    /// it was started and runs until it is ended; the load (the BuildHost's design-time build) and, once the folder has a
    /// linger file, the build run a step that would take ten minutes, as a slow tool or a hung test would.</summary>
    [Fact]
    public async Task A_Passenger_killed_while_it_loads_runs_profiles_debugs_and_builds_a_project_leaves_none_of_it_running()
    {
        Write("Program.cs", $"File.WriteAllText(Path.Combine(@\"{folder}\", args[0] + \".pid\"), Environment.ProcessId.ToString());\nThread.Sleep(Timeout.Infinite);\n");
        Write("Sum.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <Target Name="Linger" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true' And Exists('linger')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; build.pid; exec sleep 600" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path build.pid -Value $PID; Start-Sleep -Seconds 600&quot;" />
              </Target>
              <Target Name="Load" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; load.pid; exec sleep 600" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path load.pid -Value $PID; Start-Sleep -Seconds 600&quot;" />
              </Target>
            </Project>
            """);
        // No compiler server left for others' builds to use, which the kill would end under them (the IDE's nodes are its own).
        Process passenger = StartPassenger(["--mcp", folder], environment: new() { ["UseSharedCompilation"] = "false" });
        List<int> started = [];
        Task<CallToolResult>? building = null;
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        McpClient? client = null;
        try
        {
            client = await Connect(passenger, ["--mcp", folder]);
            int load = await WrittenId("load.pid", "the language server's load of the project");
            await Call(client, "run", new() { ["arguments"] = "run", ["waitSeconds"] = 1 });
            int run = await WrittenId("run.pid", "the program Run started");
            await Call(client, "profile", new() { ["arguments"] = "profile", ["seconds"] = 1 });
            int profiled = await WrittenId("profile.pid", "the program profiled");
            await Call(client, "debug_start", new() { ["arguments"] = "debug", ["waitSeconds"] = 1 });
            int debugged = await WrittenId("debug.pid", "the program debugged");
            File.WriteAllText(Path.Combine(folder, "linger"), string.Empty);
            // The build never ends: the test gives its call up once it has seen the step start.
            building = Result(client, "build", giveUp: giveUp);
            int build = await WrittenId("build.pid", "the build's step");
            // The terminal's shell (PowerShell on Windows), with a program it runs in the background.
            await Call(client, "terminal_run", new()
            {
                ["command"] = OperatingSystem.IsWindows() ? "Set-Content -Path shell.pid -Value $PID" : "echo $$ > shell.pid; sleep 600 &",
                ["waitSeconds"] = 2,
            });
            int shell = await WrittenId("shell.pid", "the terminal's shell");

            started.AddRange(Processes.Descendants(passenger.Id));
            Assert.Subset(started.ToHashSet(), new HashSet<int> { load, run, profiled, debugged, build, shell });
            giveUp.Cancel();
            passenger.Kill();
            string[] left = [.. started.Where(id => !Processes.Gone(id)).Select(Processes.Describe)];
            Assert.True(left.Length == 0, $"Passenger, killed, left running {left.Length} of the {started.Count} processes it had started:\n{string.Join('\n', left)}");
        }
        finally
        {
            ChildProcesses.Kill(passenger);
            foreach (int id in started)
            {
                Processes.Kill(id);
            }
            // The build's call, given up, ends with the server; its failure is nobody's to report.
            giveUp.Cancel();
            _ = building?.ContinueWith(call => call.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (Exception gone) when (gone is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // The server is gone, as the test made it.
                }
            }
        }
    }

    /// <summary>The MSBuild nodes a build of Passenger's leaves for its next build, which no other build takes up, end with
    /// Passenger, whether it ends as its agent leaves or is killed. The two projects build at once, each with a step of a
    /// few seconds, so MSBuild runs one in a node the test sees. They are restored first: the language server restores a
    /// solution never restored, and a build after that runs in the node its restore left, no process's child. MSBuild
    /// sometimes ends a node it meant to keep (<see cref="NodeBuilds"/>): the test builds until one stays.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_msbuild_nodes_a_build_leaves_for_the_next_end_with_passenger(bool killed)
    {
        Nice.SkipUnlessMSBuildKeepsNodes();
        string two = Path.Combine(folder, "two");
        Directory.CreateDirectory(Path.Combine(two, "A"));
        Directory.CreateDirectory(Path.Combine(two, "B"));
        Write("two/Two.slnx", "<Solution>\n  <Project Path=\"A/A.csproj\" />\n  <Project Path=\"B/B.csproj\" />\n</Solution>\n");
        foreach (string project in new[] { "two/A/A.csproj", "two/B/B.csproj" })
        {
            Write(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Target Name="Wait" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true'">
                    <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; step.pid; sleep 3" />
                    <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path step.pid -Value $PID; Start-Sleep -Seconds 3&quot;" />
                  </Target>
                </Project>
                """);
        }
        await Restore.RunAsync(two);
        // No compiler server among what the build starts, which is the machine's to share.
        Process passenger = StartPassenger(["--mcp", two], environment: new() { ["UseSharedCompilation"] = "false" });
        List<int> left = [];
        McpClient? client = null;
        try
        {
            client = await Connect(passenger, ["--mcp", two]);
            for (int build = 1; left.Count == 0; build++)
            {
                await UntilMSBuildKeepsNodes();
                // The ids a build's steps write are that build's, so what the test sees running is too.
                File.Delete(Path.Combine(two, "A", "step.pid"));
                File.Delete(Path.Combine(two, "B", "step.pid"));
                HashSet<int> before = [.. Processes.Descendants(passenger.Id)];
                Task<JsonElement> building = Call(client, "build");
                await WrittenId("two/A/step.pid", "project A's step");
                await WrittenId("two/B/step.pid", "project B's step");
                int[] during = [.. Processes.Descendants(passenger.Id).Where(id => !before.Contains(id))];
                IReadOnlyDictionary<int, string> listed = Processes.Listed();
                JsonElement built = await building;
                Assert.True(built.GetProperty("succeeded").GetBoolean(), built.GetProperty("outcome").GetString());
                left.AddRange(during.Where(Processes.IsRunning));
                if (left.Count == 0 && build == NodeBuilds)
                {
                    Assert.Fail($"{NodeBuilds} builds left nothing for the next build to use, so there is nothing to see end with Passenger.{NoNodeLeft(passenger.Id, during, listed)}");
                }
            }

            if (killed)
            {
                passenger.Kill();
            }
            else
            {
                await client.DisposeAsync();
                client = null;
                Assert.True(passenger.WaitForExit(MainWindow.EndLimit + TimeSpan.FromSeconds(5)), "Passenger did not end once its agent left");
            }
            string[] running = [.. left.Where(id => !Processes.Gone(id)).Select(Processes.Describe)];
            Assert.True(running.Length == 0, $"After Passenger {(killed ? "was killed" : "ended")}, {running.Length} of the {left.Count} processes its build left went on:\n{string.Join('\n', running)}");
        }
        finally
        {
            ChildProcesses.Kill(passenger);
            left.ForEach(Processes.Kill);
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (Exception gone) when (gone is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // The server is gone, as the test made it.
                }
            }
        }
    }

    /// <summary>The builds the test makes for one to leave a node. A node MSBuild meant to keep ends when its build, given the
    /// node's last message, closes their pipe before the node has stopped taking a closed pipe for a failure
    /// (OutOfProcNode.HandleShutdown): rarely, on a loaded machine, and the next build starts a node again.</summary>
    private const int NodeBuilds = 3;

    /// <summary>Waits, before a build, until MSBuild would keep its node: while it counts more nodes than it keeps (<see
    /// cref="Processes.MSBuildNodesCounted"/>), for other builds to end. One that stays over skips the test, saying why:
    /// no build could leave a node to see end.</summary>
    private static async Task UntilMSBuildKeepsNodes()
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        (int counted, int most) = Processes.MSBuildNodesCounted();
        while (counted > most && DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            (counted, most) = Processes.MSBuildNodesCounted();
        }
        Assert.SkipWhen(counted > most,
            $"MSBuild keeps no node for the next build while it counts more than {most} nodes (the MSBuild executable's, which its server starts), and {counted} still ran after two minutes");
    }

    /// <summary>Why the last build may have left no node, for its failure to say: what it ran (id, parent, nice value,
    /// command) and Passenger's MSBuild nodes now, with their MSBuild variables. MSBuild keeps none at a nice value other
    /// than 0, nor while it counts more than it keeps (<see cref="Processes.MSBuildNodesCounted"/>), and ends one it kept
    /// as <see cref="NodeBuilds"/> says; an idle node a build took up, no process's child, is not among what it ran.</summary>
    private static string NoNodeLeft(int passenger, IReadOnlyList<int> ran, IReadOnlyDictionary<int, string> listed)
    {
        IReadOnlyDictionary<int, string> now = Processes.Listed();
        IReadOnlyDictionary<int, string> nodes = Processes.MSBuildNodes();
        string[] ours = [.. nodes.Where(n => $"{n.Value} ".Contains($"passenger-{passenger} ", StringComparison.Ordinal)).Select(n => $"{Seen(n.Key)} {n.Value}")];
        (int counted, int most) = Processes.MSBuildNodesCounted();
        return $"\nPassenger: {Seen(passenger)}\nWhat its build ran while both steps did:\n{string.Join('\n', ran.Select(Seen))}" +
            $"\nPassenger's MSBuild nodes now:\n{string.Join('\n', ours)}" +
            $"\nMSBuild keeps no node at a nice value other than 0, nor while it counts more than {most} nodes (the MSBuild executable's): {counted} of the machine's {nodes.Count} run;" +
            " and a node it meant to keep ends when its build closes their pipe first, now and then";

        string Seen(int id) => listed.TryGetValue(id, out string? line) || now.TryGetValue(id, out line) ? line : Processes.Describe(id);
    }

    [AvaloniaFact]
    public void An_agent_attached_to_the_window_sets_a_breakpoint_the_person_sees()
    {
        MainWindow window = AgentsWindow(folder);
        string program = Path.Combine(window.Folder!, "Program.cs");
        window.OpenFile(program);
        Ui.Settle();
        string socket = window.AgentSocket!;

        // The agent's calls run on the UI thread, which the test keeps running; it stays until the window is looked at.
        TaskCompletionSource looked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> agent = Task.Run(async () =>
        {
            await using McpClient client = await Start(["--mcp-attach", ProcessId]);
            await Call(client, "breakpoint_set", new() { ["path"] = "Program.cs", ["line"] = 4 });
            string? folderSeen = (await Call(client, "workspace")).GetProperty("folder").GetString();
            await looked.Task.WaitAsync(AgentLimit);
            return folderSeen;
        });
        WaitFor(() => window.Agents == 1 || agent.IsCompleted, "the window to show the agent", AgentLimit);
        int shown = window.Agents;
        looked.TrySetResult();
        WaitFor(() => agent.IsCompleted, "the agent's calls", AgentLimit);
        // The status bar says an agent is on the window while it is, and stops saying so once it goes.
        Assert.Equal(1, shown);
        WaitFor(() => window.Agents == 0, "the window to see the agent go");
        Assert.Equal(window.Folder, agent.GetAwaiter().GetResult());
        Assert.Equal(3, Assert.Single(window.Debugger.Breakpoints.In(program)).Line);
        Assert.Contains(new LineMark(3, LineMarkKind.Breakpoint), window.EditorArea.CodeEditor.LineMarks);

        // A closed IDE leaves no socket for the next agent to try.
        window.Close();
        Assert.False(File.Exists(socket), "A closed window left its socket for agents to try");
    }

    [AvaloniaFact]
    public void An_agent_is_refused_until_the_person_enables_the_MCP_server_and_shut_out_when_they_turn_it_off()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepSettings();
        window.OpenFolder(folder);
        Ui.Settle();

        // Off as the window opens: no socket, and an agent that tries to attach is told what would let it in.
        Assert.Null(window.AgentSocket);
        Assert.Empty(Sockets());
        Exception? refused = Await(Task.Run(async () =>
        {
            try
            {
                await using McpClient client = await Start(["--mcp-attach", ProcessId]);
                return null;
            }
            catch (Exception error) when (error is not (OperationCanceledException or TimeoutException))
            {
                return error;
            }
        }), "the agent trying to attach");
        Assert.True(refused is not null, "An agent reached a window whose MCP server is off");
        WaitFor(() => Errors().Any(l => l.Contains("Enable MCP Server", StringComparison.Ordinal)), "the refused agent told to turn on Enable MCP Server");

        // The person turns it on in Settings, under MCP Server, and the agent is in.
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "agents";
        Ui.Settle();
        Assert.Equal([SettingsDialog.McpPage], dialog.Pages);
        Assert.Equal(["Enable MCP Server"], dialog.Shown);
        dialog.Change(s => s with { McpServer = true });
        dialog.Ok();
        Ui.Settle();
        string socket = Assert.Single(Sockets());
        Assert.Equal(socket, window.AgentSocket);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        Assert.Equal(window.Folder, Await(Task.Run(async () => (await Call(client, "workspace")).GetProperty("folder").GetString()), "the agent's workspace call"));
        WaitFor(() => window.AgentStatus == "Agent connected", "the status bar to say an agent is connected");

        // Turned off again, the agent's connection ends and the socket goes.
        window.ApplySettings(window.Settings with { McpServer = false });
        WaitFor(() => window.AgentStatus is null, "the status bar to say no agent is on the window");
        Assert.False(File.Exists(socket), "The window still listens for agents with its MCP server off");
        // Its connection ends, which its client hears: a call made as it ends can leave the SDK a failed request unawaited.
        Await(client.Completion, "the shut-out agent's connection to end");
        Await(Task.Run(async () =>
        {
            await client.DisposeAsync();
            return true;
        }), "the agent going");
        window.Close();
    }

    [AvaloniaFact]
    public void An_agent_asks_before_its_first_run_and_the_window_stops_waiting_when_it_gives_up_or_goes()
    {
        Write("Program.cs", "Console.WriteLine(\"waiting\");\nThread.Sleep(Timeout.Infinite);\n");
        MainWindow window = AgentsWindow(folder);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        Dictionary<string, object?> run = new() { ["waitSeconds"] = 600 };
        try
        {
            // The first run waits for the person, who sees what it asks for and denies it.
            Task<CallToolResult> denied = Task.Run(() => Result(client, "run", run));
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

            // Asked again, and allowed: it runs, and the status bar says what the agent is doing.
            using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            Task<CallToolResult> allowed = Task.Run(() => CallGivingUp(client, "run", run, giveUp.Token));
            Press(Question(window, "the question asked again"), "Allow");
            WaitFor(() => window.Runner.IsRunning && window.AgentStatus == "Agent: Run a configuration", "the agent's run in the status bar");

            // The agent gives up on the call: the window stops waiting for it, and the program, the person's now, runs on.
            giveUp.Cancel();
            WaitFor(() => allowed.IsCompleted, "the call given up");
            WaitFor(() => window.AgentStatus == "Agent connected", "the window to stop waiting on the run the agent gave up on");
            Assert.True(window.Runner.IsRunning, "Giving up on the call stopped the program");
            window.Runner.Stop();
            WaitFor(() => !window.Runner.IsRunning, "the program stopped");

            // Allowed once, run is not asked again; and an agent that goes takes its waits with it.
            Task<CallToolResult> again = Task.Run(() => Result(client, "run", run));
            WaitFor(() => window.Runner.IsRunning || window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the run allowed before");
            Assert.Empty(window.OwnedWindows.OfType<ConfirmDialog>());
            WaitFor(() => window.AgentStatus == "Agent: Run a configuration", "the second run in the status bar");
            Await(Task.Run(async () =>
            {
                await client.DisposeAsync();
                return true;
            }), "the agent going");
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

    /// <summary>An agent runs and debugs a configuration that uses the terminal: run answers with what the program's tab of
    /// the Terminal window shows, stop ends it, debug_start stops at a breakpoint on Main's first line before any of it
    /// runs, debug_input types into its tab, and debug_output reads the tab, the program's end in it.</summary>
    [AvaloniaFact]
    public void An_agent_runs_and_debugs_a_configuration_that_uses_the_terminal_and_types_into_its_tab()
    {
        Write("Program.cs", File.ReadAllText(Debugging.Helpers.Debuggees.Source("TerminalConsoleApp", "Program.cs")));
        MainWindow window = AgentsWindow(folder);
        window.SetConfigurations([new global::Passenger.App.Run.RunConfiguration
        {
            Name = "In the terminal",
            Project = Path.Combine(window.Folder!, "Sum.csproj"),
            Console = global::Passenger.App.Run.ConsoleKind.Terminal,
        }], "In the terminal");
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> running = Task.Run(() => Call(client, "run", new() { ["waitSeconds"] = 5 }));
            Press(Question(window, "the question for the agent's run"), "Allow");
            JsonElement ran = Await(running, "the agent's run");
            string output = ran.GetProperty("output").GetString()!;
            Assert.True(ran.GetProperty("stillRunning").GetBoolean() && output.Contains("red at the third row's fifth column", StringComparison.Ordinal),
                $"The run in the terminal was answered without the program waiting for its key in its tab: {ran}");
            Assert.Equal("Stopped", Await(Task.Run(() => Call(client, "stop")), "the agent's stop").GetString());
            WaitFor(() => window.RunInTerminal is { IsRunning: false }, "the program in the terminal to end");

            Await(Task.Run(() => Call(client, "breakpoint_set", new() { ["path"] = "Program.cs", ["line"] = 1 })), "the breakpoint");
            Task<JsonElement> debugging = Task.Run(() => Call(client, "debug_start"));
            Press(Question(window, "the question for the agent's debug"), "Allow");
            JsonElement stop = Await(debugging, "the stop");
            Assert.True(stop.GetProperty("state").GetString() == "paused" && stop.GetProperty("location").GetString()!.StartsWith("Program.cs:1:", StringComparison.Ordinal),
                $"Debugged in the terminal, the program did not stop on Main's first line: {stop}");
            Await(Task.Run(() => Call(client, "breakpoint_remove", new() { ["path"] = "Program.cs", ["line"] = 1 })), "the breakpoint's removal");
            Await(Task.Run(() => Call(client, "debug_continue", new() { ["waitSeconds"] = 1 })), "the program let go");
            WaitFor(() => window.Debugger.Session?.Terminal?.Session.Screen.Lines.Any(l => l.Text() == "key?") == true, "the program to ask for a key in its tab");
            Await(Task.Run(() => Call(client, "debug_input", new() { ["text"] = "q", ["newline"] = false })), "the key typed");
            Await(Task.Run(() => Call(client, "debug_input", new() { ["text"] = "hello" })), "the line typed");
            WaitFor(() => !window.Debugger.IsDebugging && window.Debugger.Session?.Terminal?.Ended.IsCompleted == true, "the program's end");
            string shown = Await(Task.Run(() => Call(client, "debug_output")), "the program's output").GetString()!;
            Assert.True(shown.Contains("key? key q", StringComparison.Ordinal) && shown.Contains("line hello", StringComparison.Ordinal)
                && shown.Contains("Process finished with exit code 3.", StringComparison.Ordinal), $"debug_output read, of the program's tab:\n{shown}");
        }
        finally
        {
            Await(Task.Run(async () =>
            {
                await client.DisposeAsync();
                return true;
            }), "the agent going");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_agent_attaches_to_the_second_solution_window_of_one_Passenger_by_its_folder()
    {
        string other = TestFolders.Create("mcp-second-");
        File.WriteAllText(Path.Combine(other, "Notes.txt"), "notes\n");
        MainWindow first = new() { Width = 1200, Height = 800, StateFolder = settings };
        first.Show();
        first.KeepLayout();
        first.KeepSettings();
        first.Start(LaunchOptions.Empty with { Folder = folder });
        Ui.Settle();
        first.ApplySettings(first.Settings with { McpServer = true, OpenProjectIn = OpenProjectIn.NewWindow });

        // File, Open of another folder, in a window of its own as Settings says: it lets agents in as the first does.
        Assert.Null(first.Open(other));
        Ui.Settle();
        MainWindow second = Assert.Single(PassengerApp.Windows, w => w != first);
        Assert.Equal(PathNames.Canonical(other), second.Folder);
        Assert.True(first.AgentSocket is not null && second.AgentSocket is not null && first.AgentSocket != second.AgentSocket,
            $"Each solution window of the one Passenger was to listen for agents on a socket of its own: {first.AgentSocket}, {second.AgentSocket}");

        // An agent started in either's folder reaches that window.
        Assert.Equal(second.Folder, AttachedFolder(other));
        Assert.Equal(first.Folder, AttachedFolder(folder));
        second.Close();
        first.Close();
    }

    [AvaloniaFact]
    public void Each_window_has_its_own_socket_and_an_agent_attaches_to_the_one_over_its_folder()
    {
        string other = TestFolders.Create("mcp-other-");
        File.WriteAllText(Path.Combine(other, "Notes.txt"), "notes\n");
        Directory.CreateDirectory(Path.Combine(folder, "Inner"));
        MainWindow first = AgentsWindow(folder);
        MainWindow second = AgentsWindow(other);
        string firstSocket = first.AgentSocket!;
        string secondSocket = second.AgentSocket!;
        Assert.NotEqual(firstSocket, secondSocket);
        Assert.Equal(new[] { firstSocket, secondSocket }.Order(StringComparer.Ordinal), Sockets().Order(StringComparer.Ordinal));

        // Started in the first window's folder (deep in it), the agent reaches that window though the second is newer.
        Assert.Equal(first.Folder, AttachedFolder(Path.Combine(folder, "Inner")));
        Assert.Equal(second.Folder, AttachedFolder(other));

        // One window closing takes its own socket, not the other's.
        second.Close();
        Assert.False(File.Exists(secondSocket), "A closed window left its socket");
        Assert.True(File.Exists(firstSocket), "Closing one window took another's socket");
        // From a folder no window has, the agent reaches the window there is.
        Assert.Equal(first.Folder, AttachedFolder(Path.GetTempPath()));
        first.Close();
    }

    /// <summary>vcs_log with a path is Show History's, read by the window's own code: the agent gets the commits the
    /// person's history tab lists, a file followed through its rename, and a selection's.</summary>
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
        MainWindow window = AgentsWindow(repository);
        string store = Path.Combine(window.Folder!, "src", "Store.cs");
        Passenger.App.ToolWindows.GitLogView history = Await(window.ShowHistoryAsync(store), "the file's history")!;
        Ui.Landed(() => history.Rows.Count == 3, "the file's three commits");
        Passenger.App.ToolWindows.GitLogView lines = Await(window.ShowSelectionHistoryAsync(store, 4, 4), "line 5's history")!;
        Ui.Landed(() => lines.Rows.Count == 2, "the two commits that changed line 5");
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement log = Await(Task.Run(() => Call(client, "vcs_log", new() { ["path"] = Path.Combine("src", "Store.cs") })), "the agent's vcs_log of the file");
            Assert.Equal(history.Rows.Select(r => r.Commit.Short), log.EnumerateArray().Select(c => c.GetProperty("hash").GetString()));
            // The rename, then the file under its old name.
            Assert.Equal([Path.Combine("src", "Store.cs"), Path.Combine("src", "Shop.cs"), Path.Combine("src", "Shop.cs")],
                log.EnumerateArray().Select(c => c.GetProperty("changes")[0].GetProperty("path").GetString()));
            Assert.Equal("renamed", log[0].GetProperty("changes")[0].GetProperty("kind").GetString());

            JsonElement changed = Await(Task.Run(() => Call(client, "vcs_log", new() { ["path"] = Path.Combine("src", "Store.cs"), ["startLine"] = 5, ["endLine"] = 5 })),
                "the agent's vcs_log of line 5");
            Assert.Equal(lines.Rows.Select(r => r.Commit.Short), changed.EnumerateArray().Select(c => c.GetProperty("hash").GetString()));
            Assert.Equal(["Twice", "First"], changed.EnumerateArray().Select(c => c.GetProperty("subject").GetString()));
        }
        finally
        {
            Await(Task.Run(async () =>
            {
                await client.DisposeAsync();
                return true;
            }), "the agent going");
            window.Close();
        }
    }

    /// <summary>The folder of the window an agent started in a folder reaches with <c>--mcp-attach</c>.</summary>
    private string? AttachedFolder(string workingDirectory) => Await(Task.Run(async () =>
    {
        await using McpClient client = await Start(["--mcp-attach"], workingDirectory);
        return (await Call(client, "workspace")).GetProperty("folder").GetString();
    }), $"an agent attaching from {workingDirectory}");

    [AvaloniaFact]
    public void The_window_takes_keys_while_an_attached_agent_finds_ten_thousand_usages_each_with_its_line()
    {
        // Four files calling Helper.Twice 2,500 times each, every call under a long comment: a usage's line read from its
        // file's start every time reads each file 2,500 times over.
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
        MainWindow window = AgentsWindow(folder);
        window.OpenFile(Path.Combine(window.Folder!, "Helper.cs"));
        window.OpenFile(Path.Combine(window.Folder!, "Notes.txt"));
        window.EditorArea.CodeEditor.Focus();
        Ui.Settle();
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            // The language server has the folder loaded before the window is timed, which is the server's work, not the agent's.
            Await(Task.Run(() => Call(client, "diagnostics", new() { ["path"] = "Helper.cs" })), "the language server");

            // The person types in another file while the agent asks for every usage of Twice.
            Task<JsonElement> usages = Task.Run(() => Call(client, "usages", new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 }));
            int typed = 0;
            TimeSpan longest;
            using (InputWait waits = new())
            {
                // The call's own limit ends this if Passenger never answers.
                while (!usages.IsCompleted)
                {
                    window.KeyTextInput("x");
                    typed++;
                    Screenshot.Pump(TimeSpan.FromMilliseconds(50));
                }
                waits.Stop();
                longest = waits.Longest;
            }
            JsonElement[] found = [.. usages.GetAwaiter().GetResult().EnumerateArray()];
            Ui.Settle();

            // Every usage comes back, each with its own line, and every key the person typed meanwhile landed at once.
            Assert.Equal(10_002, found.Length);
            Assert.All(found, place => Assert.Contains("Twice(", place.GetProperty("text").GetString(), StringComparison.Ordinal));
            Assert.Contains(found, place => place.GetProperty("path").GetString() == "Uses3.cs" && place.GetProperty("line").GetInt32() == 5005
                && place.GetProperty("text").GetString() == "total += Helper.Twice(2499);");
            Assert.Equal(new string('x', typed), window.EditorArea.Active!.Text.Snapshot.ToString());
            Assert.True(longest < TimeSpan.FromMilliseconds(250),
                $"a key waited {longest.TotalMilliseconds:F0} ms for the window while the agent's {found.Length} usages were found");

            // The callers' hierarchy has each caller's line the same way: each file's method at its first call.
            JsonElement callers = Await(Task.Run(() => Call(client, "hierarchy", new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 })), "the callers");
            Assert.Equal(["Uses0.cs:7 total += Helper.Twice(0);", "Uses1.cs:7 total += Helper.Twice(0);", "Uses2.cs:7 total += Helper.Twice(0);", "Uses3.cs:7 total += Helper.Twice(0);"],
                callers.EnumerateArray().Select(c => $"{c.GetProperty("path").GetString()}:{c.GetProperty("line").GetInt32()} {c.GetProperty("text").GetString()}")
                    .Where(c => c.StartsWith("Uses", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
        finally
        {
            Await(Task.Run(async () =>
            {
                await client.DisposeAsync();
                return true;
            }), "the agent going");
            window.Close();
        }
    }

    [Fact]
    public async Task Started_as_the_Finder_starts_it_Passenger_runs_the_git_the_login_shell_finds()
    {
        if (OperatingSystem.IsWindows())
        {
            // A Windows program gets the person's whole environment, PATH included.
            return;
        }
        // A login profile that puts its own git first, as Homebrew's shellenv does; the git notes that it ran.
        string home = TestFolders.Create("home-");
        try
        {
            string tools = Path.Combine(home, "tools");
            Directory.CreateDirectory(tools);
            string git = Path.Combine(tools, "git");
            string real = global::Passenger.Core.Programs.Git()!;
            File.WriteAllText(git, $"#!/bin/sh\ntouch \"{Path.Combine(home, "used")}\"\nexec {real} \"$@\"\n");
            File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(home, ".zprofile"), $"export PATH=\"{tools}:$PATH\"\n");
            File.WriteAllText(Path.Combine(home, ".profile"), $"export PATH=\"{tools}:$PATH\"\n");
            TestGit.Run(folder, "init", "-q");

            // launchd's environment: the bare PATH, no terminal, no .NET or zsh settings of the person's.
            await using McpClient client = await Start(["--mcp", folder], environment: new()
            {
                ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin",
                ["HOME"] = home,
                ["SHELL"] = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh",
                ["TERM"] = null,
                ["ZDOTDIR"] = null,
                ["DOTNET_ROOT"] = null,
            });
            await Call(client, "vcs_status");
            Assert.True(File.Exists(Path.Combine(home, "used")), "Passenger ran a git other than the one its login shell's PATH finds");
        }
        finally
        {
            TestFolders.Delete(home);
        }
    }

    /// <summary>Longer than any one exchange may take, for a wait on an agent's calls, whose own limits fail first and
    /// name the call.</summary>
    private static readonly TimeSpan AgentLimit = TimeSpan.FromMinutes(6);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string ProcessId => Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The debugger engine's log of what Passenger debugs, which a failed call quotes.</summary>
    private string EngineLog => Path.Combine(settings, "engine.log");

    /// <summary>Passenger started as an agent's MCP server (<see cref="StartPassenger"/>), and the agent's client to it.</summary>
    private Task<McpClient> Start(IList<string> arguments, string? workingDirectory = null, Dictionary<string, string?>? environment = null) =>
        Connect(StartPassenger(arguments, workingDirectory, environment), arguments);

    /// <summary>Passenger started as the IDE starts a child, with a state folder of the test's own for its log (or, joining
    /// a window, the run's, where the windows' sockets are), the engine's log and any other change to the environment
    /// (null takes a variable away), its standard error kept. Not the SDK's own process transport: that ends a server with the runtime's tree kill, which can hang
    /// this process (ChildProcesses.Kill says why). The agent going closes its input; <see cref="Dispose"/> ends the rest.</summary>
    private Process StartPassenger(IList<string> arguments, string? workingDirectory = null, Dictionary<string, string?>? environment = null)
    {
        ProcessStartInfo start = ChildProcesses.Command(Passenger, arguments, workingDirectory);
        start.Environment["PASSENGER_SETTINGS"] = arguments.Contains("--mcp-attach") ? TestRun.Settings : settings;
        start.Environment["PASSENGER_ENGINE_LOG"] = EngineLog;
        foreach ((string name, string? value) in environment ?? new())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process passenger = ChildProcesses.Start(start);
        passenger.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.Add(e.Data);
                }
            }
        };
        passenger.BeginErrorReadLine();
        lock (passengers)
        {
            passengers.Add(passenger);
        }
        return passenger;
    }

    /// <summary>An agent's client to a Passenger this test started, over its standard input and output.</summary>
    private Task<McpClient> Connect(Process passenger, IList<string> arguments) =>
        Exchange($"Starting passenger {string.Join(' ', arguments)}", TimeSpan.FromMinutes(1), cancel => McpClient.CreateAsync(new PassengerTransport(passenger), cancellationToken: cancel));

    /// <summary>The SDK's stream transport over a Passenger's input and output; the agent going closes the input, as the
    /// SDK's process transport does, and Passenger ends on its own.</summary>
    private sealed class PassengerTransport(Process passenger) : IClientTransport
    {
        public string Name => "passenger";

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            new Session(await new StreamClientTransport(passenger.StandardInput.BaseStream, passenger.StandardOutput.BaseStream).ConnectAsync(cancellationToken), passenger);

        private sealed class Session(ITransport inner, Process passenger) : ITransport
        {
            public string? SessionId => inner.SessionId;

            public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => inner.SendMessageAsync(message, cancellationToken);

            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                EndInput(passenger);
            }
        }
    }

    /// <summary>Closes a Passenger's input, which ends it as an agent going does; one that has gone already is left.</summary>
    private static void EndInput(Process passenger)
    {
        try
        {
            passenger.StandardInput.Close();
        }
        catch (Exception gone) when (gone is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // It had gone, its pipe with it.
        }
    }

    /// <summary>A window over a folder whose person has let agents in.</summary>
    private static MainWindow AgentsWindow(string at)
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(at);
        Ui.Settle();
        window.ApplySettings(window.Settings with { McpServer = true });
        Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
        return window;
    }

    /// <summary>The windows' sockets for agents, in the run's state folder (<see cref="TestRun"/>), not the person's.</summary>
    private static string[] Sockets() =>
        Directory.Exists(global::Passenger.App.Mcp.McpHost.SocketFolder) ? Directory.GetFiles(global::Passenger.App.Mcp.McpHost.SocketFolder, "*.sock") : [];

    private string[] Errors()
    {
        lock (errors)
        {
            return [.. errors];
        }
    }

    /// <summary>The id a program Passenger started wrote to a file in the folder; a wait that runs out says what Passenger
    /// said meanwhile.</summary>
    private async Task<int> WrittenId(string file, string what)
    {
        try
        {
            return await Processes.WrittenIdAsync(Path.Combine(folder, file), what);
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException(waited.Message + Said(), waited);
        }
    }

    /// <summary>A task's result once done, the window's loop running meanwhile, since the agent's calls are answered on it.</summary>
    private T Await<T>(Task<T> task, string what)
    {
        WaitFor(() => task.IsCompleted, what, AgentLimit);
        return task.GetAwaiter().GetResult();
    }

    /// <summary><see cref="Ui.WaitFor"/>, failing with what Passenger said meanwhile.</summary>
    private void WaitFor(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        try
        {
            Ui.WaitFor(condition, what, limit);
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException(waited.Message + Said(), waited);
        }
    }

    /// <summary>The window's question to the person, once it is asked.</summary>
    private ConfirmDialog Question(MainWindow window, string what)
    {
        WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), what);
        return window.OwnedWindows.OfType<ConfirmDialog>().Single();
    }

    private static string[] Texts(ConfirmDialog dialog) =>
        [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? string.Empty)];

    private static void Press(ConfirmDialog dialog, string button)
    {
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>().First(b => (string?)b.Content == button)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Ui.Settle();
    }

    /// <summary>A tool's answer, read as the JSON it is; a tool's error fails the test with its words and
    /// what Passenger said meanwhile.</summary>
    private async Task<JsonElement> Call(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        CallToolResult result = await Result(client, tool, arguments);
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        if (result.IsError == true)
        {
            Assert.Fail($"{tool} failed: {text}{Said()}");
        }
        // A tool that answers in words (stop's "Stopped") is a JSON string of them.
        return text.StartsWith('[') || text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : JsonSerializer.SerializeToElement(text);
    }

    /// <summary>A tool's whole result, an error's included, within the time the tool may take, or until the test gives up
    /// on it.</summary>
    private Task<CallToolResult> Result(McpClient client, string tool, Dictionary<string, object?>? arguments = null, CancellationTokenSource? giveUp = null) =>
        Exchange($"The {tool} call", Limit(tool), cancel => client.CallToolAsync(tool, arguments, cancellationToken: cancel).AsTask(), giveUp);

    /// <summary>A tool call the agent gives up on as the protocol has it: sent under an id of its own, and on giving up a
    /// "cancelled" notification naming that id. The SDK's own call can drop that notification: it registers sending it and
    /// then waits on the same token in a using that disposes the registration, and a token's callbacks run newest first,
    /// so the wait's end can dispose the sending before it runs (1 in 200 under load), leaving the window waiting.</summary>
    private static async Task<CallToolResult> CallGivingUp(McpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken giveUp)
    {
        RequestId id = new($"given-up-{Guid.NewGuid():N}");
        CallToolRequestParams call = new()
        {
            Name = tool,
            Arguments = arguments.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value, ModelContextProtocol.McpJsonUtilities.DefaultOptions)),
        };
        Task<CallToolResult> answer = client.SendRequestAsync<CallToolRequestParams, CallToolResult>(RequestMethods.ToolsCall, call, requestId: id).AsTask();
        try
        {
            return await answer.WaitAsync(giveUp);
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
        {
            // Sending it also ends the SDK's wait for the answer that will not come.
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification, new CancelledNotificationParams { RequestId = id });
            throw;
        }
    }

    /// <summary>How long a tool may take before the test calls it hung: minutes for one that builds first; for one that asks
    /// the language server, longer than the 120 s Passenger waits for it to load the folder, so its own words come back;
    /// a minute for any other.</summary>
    private static TimeSpan Limit(string tool) => tool switch
    {
        "build" or "debug_start" or "run" or "tests_run" or "profile" or "nuget_install" or "nuget_remove" => TimeSpan.FromMinutes(5),
        "diagnostics" or "symbol_info" or "definition" or "usages" or "hierarchy" or "implementations" or "rename" or "code_actions"
            or "apply_code_action" or "format" or "find_symbol" or "outline" => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromMinutes(1),
    };

    /// <summary>One exchange with Passenger, given up after a limit: one that runs out of time or fails ends the test naming
    /// it, with what Passenger said meanwhile, so a hang fails saying why rather than stopping the run in silence.</summary>
    private async Task<T> Exchange<T>(string what, TimeSpan limit, Func<CancellationToken, Task<T>> exchange, CancellationTokenSource? giveUp = null)
    {
        CancellationToken given = giveUp?.Token ?? default;
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(Cancel, given);
        timer.CancelAfter(limit);
        try
        {
            return await exchange(timer.Token);
        }
        catch (Exception error) when (!Cancel.IsCancellationRequested && !given.IsCancellationRequested
            && (error is TimeoutException || (error is OperationCanceledException && timer.IsCancellationRequested)))
        {
            throw new TimeoutException($"{what} had no answer in {limit.TotalSeconds:0} s.{Said()}", error);
        }
        catch (Exception error) when (!Cancel.IsCancellationRequested && !given.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{what} failed: {error.Message}{Said()}", error);
        }
    }

    /// <summary>What Passenger said before something went wrong: its standard error, and the end of its log and of the
    /// debugger engine's, which on a machine nobody can watch (CI's) are all there is to go by.</summary>
    private string Said()
    {
        string[] written = Errors();
        return $"\nPassenger's standard error:{(written.Length == 0 ? " nothing" : "\n  " + string.Join("\n  ", written.TakeLast(40)))}"
            + Tail("Passenger's log", Path.Combine(settings, "logs", "passenger.log"), 20)
            + Tail("The debugger engine's log", EngineLog, 30);

        static string Tail(string name, string path, int lines)
        {
            try
            {
                // Shared, since its writer may still have it open, which Windows otherwise refuses a reader.
                using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return $"\n{name} ends:\n  {string.Join("\n  ", reader.ReadToEnd().TrimEnd().Split('\n').TakeLast(lines))}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    private static string? Local(JsonElement stop, string name) =>
        stop.GetProperty("locals").EnumerateArray().FirstOrDefault(l => l.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } local
            ? local.GetProperty("value").GetString()
            : null;

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(folder, relative), text);

    /// <summary>A working tree under a folder (work), one commit in and Notes.txt changed since, whose origin is a bare
    /// repository beside it (remote.git) that refuses every push when <paramref name="refusing"/>, its pre-receive hook
    /// saying why.</summary>
    private static string Repository(string root, bool refusing)
    {
        string remote = Path.Combine(root, "remote.git");
        string work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        TestGit.Run(root, "init", "-q", "--bare", remote);
        if (refusing)
        {
            string hook = Path.Combine(remote, "hooks", "pre-receive");
            File.WriteAllText(hook, "#!/bin/sh\necho 'pushes are closed here' >&2\nexit 1\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        File.WriteAllText(Path.Combine(work, "Notes.txt"), "one\ntwo\nthree\n");
        TestGit.Init(work);
        TestGit.Run(work, "remote", "add", "origin", remote);
        File.WriteAllText(Path.Combine(work, "Notes.txt"), "one\n2\nthree\n");
        return work;
    }
}
