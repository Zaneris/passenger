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

/// <summary>An agent's own Passenger beside the person's window and on its own: one Local History kept in their
/// one state, and what it started gone with it when it is killed. What its builds leave for the machine's next build is
/// <see cref="McpBuildNodesTests"/>'.</summary>
public sealed class McpProcessesTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpProcessesTests(RestoredFolders folders)
        : base(folders)
    {
    }

    /// <summary>An agent's windowless Passenger over the person's folder keeps its Local History in the person's store: what
    /// it kept (a file as it was before the agent changed it) outlasts the person's window pruning the history at a day's
    /// turn.</summary>
    [AvaloniaFact]
    public void What_an_agents_own_passenger_keeps_in_local_history_outlasts_the_persons_window_pruning_the_history()
    {
        string notes = Path.Combine(Folder, "Notes.txt");
        File.WriteAllText(notes, "first draft\n");
        long[] clock = [new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero).UtcTicks];
        DateTimeOffset Now() => new(Interlocked.Read(ref clock[0]), TimeSpan.Zero);
        void Later(TimeSpan by) => Interlocked.Add(ref clock[0], by.Ticks);
        global::Passenger.App.History.HistoryKeeping day = new(Days: 1) { Clock = Now };
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = OwnState, HistoryKeeping = day, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(Folder);
        window.OpenFile(Path.Combine(Folder, "Helper.cs"));
        Ui.Settle();
        void SaveEdit(string comment)
        {
            window.EditorArea.CodeEditor.Focus();
            window.EditorArea.CodeEditor.ApplyEdits([new global::Passenger.Text.TextEdit(0, 0, $"// {comment}\n")]);
            Ui.Settle();
            Ui.Run(window, AppActions.Save);
        }
        // A day and a half of the person's saves.
        for (int save = 1; save <= 6; save++)
        {
            Later(TimeSpan.FromHours(6));
            SaveEdit($"save {save}");
        }

        McpClient client = Await(Task.Run(() => Start(["--mcp", Folder], languageServer: false)), "the agent's Passenger starting");
        try
        {
            Await(Task.Run(() => Call(client, McpTools.Open, new() { ["path"] = "Notes.txt" })), "the agent opening Notes.txt");
            Await(Task.Run(() => Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = "second " })), "the agent typing");
            Await(Task.Run(() => Call(client, McpTools.SaveAll)), "the agent saving");
        }
        finally
        {
            Gone(client);
        }
        Process agents = LastPassenger();
        WaitFor(() => agents.HasExited, "the agent's Passenger to end");
        Ui.WaitFor(() => File.ReadAllText(notes) == "second first draft\n", "the person's window to see the agent's change");

        // The next day's save, at which the window prunes the oldest of its history.
        Later(TimeSpan.FromHours(25));
        SaveEdit("the next day");
        Ui.Settle();
        window.Close();

        MainWindow again = new() { Width = 1200, Height = 800, StateFolder = OwnState, HistoryKeeping = day, StartsLanguageServer = false };
        again.Show();
        again.OpenFolder(Folder);
        Ui.Settle();
        try
        {
            global::Passenger.App.History.LocalHistory history = again.LocalHistory!;
            string kept = PathNames.Canonical(notes);
            bool Drafted() => history.Snapshot().Events.Any(e => PathNames.Same(e.Path ?? string.Empty, kept)
                && new[] { e.Before, e.After }.OfType<string>().Any(hash => history.Content(hash) is { } bytes && System.Text.Encoding.UTF8.GetString(bytes) == "first draft\n"));
            Ui.WaitFor(Drafted, "Local History to have Notes.txt as it was before the agent changed it: the person's window pruning its history erased what the agent's Passenger kept",
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            again.Close();
        }
    }

    /// <summary>The person's window and an agent's windowless Passenger keep one Local History in one state at the same
    /// time: every label the agent's Passenger puts is in it after the window has pruned the history again and again
    /// meanwhile, each prune of a long history holding the agent's lines back until it is done.</summary>
    [AvaloniaFact]
    public void Labels_an_agents_passenger_puts_as_the_persons_window_prunes_the_shared_history_are_all_kept()
    {
        // Hours of earlier labels, which both keep, so each prune reads and writes the log at length.
        string log = Path.Combine(global::Passenger.App.State.ProjectState.FolderOf(OwnState, PathNames.Canonical(Folder)), "localHistory", "changes.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const int Earlier = 40_000;
        File.WriteAllText(log, string.Concat(Enumerable.Range(0, Earlier).Select(i =>
            $"{{\"s\":{(now - 100_000_000) * 1000 + i},\"t\":{now - ((Earlier - i) * 200L)},\"k\":\"label\",\"n\":\"earlier {i}\"}}\n")));
        McpClient client = Await(Task.Run(() => Start(["--mcp", Folder], languageServer: false)), "the agent's Passenger starting");
        long[] clock = [new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero).UtcTicks];
        global::Passenger.App.History.HistoryKeeping day = new(Days: 1) { Clock = () => new(Interlocked.Read(ref clock[0]), TimeSpan.Zero) };
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = OwnState, HistoryKeeping = day, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(Folder);
        window.OpenFile(Path.Combine(Folder, "Helper.cs"));
        Ui.Settle();
        try
        {
            Assert.Equal(PathNames.Canonical(Path.GetDirectoryName(log)!), PathNames.Canonical(window.LocalHistory!.Root));
            Await(Task.Run(() => Call(client, McpTools.Workspace)), "the agent's Passenger to have the folder open");
            bool saving = true;
            Task<int> labelling = Task.Run(async () =>
            {
                int put = 0;
                while (Volatile.Read(ref saving) && put < 500)
                {
                    put++;
                    await Call(client, McpTools.LocalHistoryLabel, new() { ["name"] = $"agent {put}" });
                }
                return put;
            });
            // The person's saves six hours apart on the window's clock: as each day turns the window prunes, dropping its oldest.
            for (int save = 1; save <= 32; save++)
            {
                Interlocked.Add(ref clock[0], TimeSpan.FromHours(6).Ticks);
                window.EditorArea.CodeEditor.Focus();
                window.EditorArea.CodeEditor.ApplyEdits([new global::Passenger.Text.TextEdit(0, 0, $"// save {save}\n")]);
                Ui.Settle();
                Ui.Run(window, AppActions.Save);
            }
            Volatile.Write(ref saving, false);
            int labels = Await(labelling, "the agent's labels");
            Ui.Settle();
            HashSet<string> kept = [.. File.ReadAllLines(log).Select(line => System.Text.Json.Nodes.JsonNode.Parse(line)?["n"]?.GetValue<string>())
                .OfType<string>().Where(name => name.StartsWith("agent ", StringComparison.Ordinal))];
            List<string> lost = [.. Enumerable.Range(1, labels).Select(n => $"agent {n}").Where(label => !kept.Contains(label))];
            Assert.True(lost.Count == 0, $"Of the {labels} labels the agent's Passenger put, the person's window pruning the shared history lost {lost.Count}: {string.Join(", ", lost.Take(20))}");
            Assert.True(File.ReadAllLines(log).Count(line => line.Contains("\"earlier ", StringComparison.Ordinal)) == Earlier, "The earlier labels, which both keep, were not all kept");
        }
        finally
        {
            window.Close();
            Gone(client);
        }
    }

    /// <summary>Passenger killed outright (SIGKILL, TerminateProcess), as a crash ends it, while it runs (and has profiled)
    /// a program, debugs another and builds the project and its language server loads it. Each program writes its id to a
    /// file named for how it was started; the load and, once the folder has a linger file, the build run a step that would
    /// take ten minutes. The programs are two launch profiles', as Debug of the one Run runs would end that run.</summary>
    [Fact]
    public async Task A_Passenger_killed_while_it_loads_runs_profiles_debugs_and_builds_a_project_leaves_none_of_it_running()
    {
        Write("Program.cs", $"File.WriteAllText(Path.Combine(@\"{Folder}\", args[0] + \".pid\"), Environment.ProcessId.ToString());\nThread.Sleep(Timeout.Infinite);\n");
        Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
        Write("Properties/launchSettings.json", "{\n  \"profiles\": {\n    \"Ran\": { \"commandName\": \"Project\" },\n    \"Debugged\": { \"commandName\": \"Project\" }\n  }\n}\n");
        Write("Sum.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
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
        // No compiler server left for others' builds to use, which the kill would end under them.
        Process passenger = StartPassenger(["--mcp", Folder], environment: new() { ["UseSharedCompilation"] = "false" });
        List<int> started = [];
        Task<CallToolResult>? building = null;
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        McpClient? client = null;
        try
        {
            client = await Connect(passenger, ["--mcp", Folder]);
            int load = await WrittenId("load.pid", "the language server's load of the project");
            await Call(client, McpTools.Run, new() { ["configuration"] = "Sum: Ran", ["arguments"] = "run", ["waitSeconds"] = 1 });
            int run = await WrittenId("run.pid", "the program Run started");
            // The Run console is Run's program's, so the profiler attaches to it rather than start another there.
            await Call(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 1 });
            await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Sum: Debugged", ["arguments"] = "debug", ["waitSeconds"] = 1 });
            int debugged = await WrittenId("debug.pid", "the program debugged");
            File.WriteAllText(Path.Combine(Folder, "linger"), string.Empty);
            // The build never ends: the test gives its call up once it has seen the step start.
            building = Result(client, McpTools.Build, giveUp: giveUp);
            int build = await WrittenId("build.pid", "the build's step");
            await Call(client, McpTools.TerminalRun, new()
            {
                ["command"] = OperatingSystem.IsWindows() ? "Set-Content -Path shell.pid -Value $PID" : "echo $$ > shell.pid; sleep 600 &",
                ["waitSeconds"] = 2,
            });
            int shell = await WrittenId("shell.pid", "the terminal's shell");

            started.AddRange(Processes.Descendants(passenger.Id));
            Assert.Subset(started.ToHashSet(), new HashSet<int> { load, run, debugged, build, shell });
            giveUp.Cancel();
            passenger.Kill();
            string[] left = [.. started.Where(id => !Processes.Gone(id)).Select(Processes.Describe)];
            Assert.True(left.Length == 0, $"Passenger, killed, left running {left.Length} of the {started.Count} processes it had started:\n{string.Join('\n', left)}");
        }
        finally
        {
            // Given up before the kill, so the client's word of it reaches a Passenger still there.
            giveUp.Cancel();
            ChildProcesses.Kill(passenger);
            foreach (int id in started)
            {
                Processes.Kill(id);
            }
            // The build's call ends with the server; its failure is nobody's to report.
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
}

/// <summary>The MSBuild nodes an agent's own Passenger's builds leave end with it, as it ends or is killed. MSBuild keeps
/// a node only while the machine's count of nodes allows, which other builds beside it would take, so the class runs
/// alone.</summary>
[Collection(Alone.Name)]
public sealed class McpBuildNodesTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpBuildNodesTests(RestoredFolders folders)
        : base(folders)
    {
    }

    /// <summary>The MSBuild nodes a build of Passenger's leaves for its next build, which no other build takes up, end with
    /// Passenger, whether it ends as its agent leaves or is killed. The two projects build at once, each with a step of a few
    /// seconds, so MSBuild runs one in a node the test sees; restored first, with no language server, whose restore would
    /// leave a node no process's child. MSBuild sometimes ends a node it meant to keep (<see cref="NodeBuilds"/>): the test
    /// builds until one stays.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_msbuild_nodes_a_build_leaves_for_the_next_end_with_passenger(bool killed)
    {
        Nice.SkipUnlessMSBuildKeepsNodes();
        string two = Path.Combine(Folder, "two");
        Directory.CreateDirectory(Path.Combine(two, "A"));
        Directory.CreateDirectory(Path.Combine(two, "B"));
        Write("two/Two.slnx", "<Solution>\n  <Project Path=\"A/A.csproj\" />\n  <Project Path=\"B/B.csproj\" />\n</Solution>\n");
        foreach (string project in new[] { "two/A/A.csproj", "two/B/B.csproj" })
        {
            Write(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{TestProjects.Framework}</TargetFramework>
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
        Process passenger = StartPassenger(["--mcp", two], environment: new() { ["UseSharedCompilation"] = "false" }, languageServer: false);
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
                Task<JsonElement> building = Call(client, McpTools.Build);
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
                Assert.True(passenger.WaitForExit(MainWindow.EndLimit + TimeSpan.FromSeconds(5).Scaled()), "Passenger did not end once its agent left");
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

    /// <summary>The builds the test makes for one to leave a node: rarely, on a loaded machine, MSBuild ends a node it meant
    /// to keep, and the next build starts one again.</summary>
    private const int NodeBuilds = 3;

    /// <summary>Waits, before a build, until MSBuild would keep its node: while it counts more nodes than it keeps (<see
    /// cref="Processes.MSBuildNodesCounted"/>), for other builds to end. One that stays over skips the test, saying why:
    /// no build could leave a node to see end.</summary>
    private static async Task UntilMSBuildKeepsNodes()
    {
        (int counted, int most) = (0, 0);
        await Waiting.UntilAsync(() => ((counted, most) = Processes.MSBuildNodesCounted()).counted <= most, TimeSpan.FromMinutes(2), every: TimeSpan.FromMilliseconds(500));
        Assert.SkipWhen(counted > most,
            $"MSBuild keeps no node for the next build while it counts more than {most} nodes (the MSBuild executable's, which its server starts), and {counted} still ran after two minutes");
    }

    /// <summary>Why the last build may have left no node, for its failure to say: what it ran (id, parent, nice value,
    /// command) and Passenger's MSBuild nodes now, with their MSBuild variables.</summary>
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
}
