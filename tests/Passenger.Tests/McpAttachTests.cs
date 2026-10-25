using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent set up with <c>--mcp-attach</c> before any Passenger runs: its session has the tools from the start,
/// its first call has Passenger open its folder (headless here, or a window of the running Passenger's), the window asks
/// the person before the agent works there, and a window closed is found again at the next call. A window on another
/// folder is never joined, and on a person's window an agent's open of another folder is theirs to allow; an agent let in
/// on one folder is asked again once the person opens another there, and Settings keep a folder let in always from
/// another window meanwhile. What is held to a time is <see cref="McpAttachTimingTests"/>'.</summary>
public sealed class McpAttachTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpAttachTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Fact]
    public async Task An_agent_set_up_before_Passenger_runs_has_the_tools_and_its_first_call_starts_Passenger_on_its_folder_which_asks()
    {
        // The agent's own state, where no window of this run's listens, its path longer than a socket's 104 bytes.
        string root = TestFolders.Create("mcp-state-");
        DeleteAtEnd(root);
        string state = Path.Combine(root, new string('s', 60), new string('t', 60));
        string notes = Plain("mcp-start-");
        Process proxy = StartPassenger(["--mcp-attach"], notes, new() { [StateStore.Variable] = state });
        await using McpClient client = await Connect(proxy, ["--mcp-attach"]);

        IList<McpClientTool> tools = await Exchange("Listing the tools", TimeSpan.FromMinutes(1), cancel => client.ListToolsAsync(cancellationToken: cancel).AsTask());
        string[] built = [.. typeof(PassengerTools).GetMethods().Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>().Order(StringComparer.Ordinal)];
        Assert.Equal(built, tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.True(client.ServerCapabilities.Resources is null && client.ServerCapabilities.Prompts is null, "The agent's session offers resources or prompts no window has");
        foreach (string said in (string[])["Begin with workspace", "refactor with describe true", "in a window the person sees", "the window asks them to let the agent in"])
        {
            Assert.True(client.ServerInstructions?.Contains(said, StringComparison.Ordinal) == true, $"The server's instructions leave out \"{said}\": {client.ServerInstructions}");
        }
        string sockets = McpHost.SocketFolderFor(state);
        DeleteAtEnd(sockets);
        Assert.NotEqual(McpHost.SocketFolder, sockets);
        Assert.True(Started(proxy).Length == 0, $"Listing the tools started a Passenger: {string.Join("; ", Started(proxy).Select(Processes.Describe))}");

        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        Task<CallToolResult> waiting = CallGivingUp(client, McpTools.Workspace, new(), giveUp.Token);
        string? window = null;
        // The window writes its socket's .folder as it opens, so the read must not fail beside that write.
        await Until(() => (window = Directory.Exists(sockets) ? Directory.GetFiles(sockets, "*.sock").FirstOrDefault(s =>
            Ui.Shared(Path.ChangeExtension(s, ".folder")) == PathNames.Canonical(notes)) : null) is not null,
            "Passenger to start with a window on the agent's folder");
        Assert.Contains(McpHostProcess(window!), Started(proxy));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(window!) < 104, $"The window's socket is {window!.Length} characters long: {window}");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(sockets));
        }
        string log = Path.Combine(state, "logs", "passenger.log");
        await Until(() => File.Exists(log) && Ui.Shared(log).Contains($"Agents: Test Agent asks to work in the window on {PathNames.Canonical(notes)}", StringComparison.Ordinal),
            "the started Passenger's window to ask the person to let Test Agent in");
        Assert.False(waiting.IsCompleted, $"The call was answered before the person answered: {waiting.Status}");

        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        int started = McpHostProcess(window!);
        await client.DisposeAsync();
        await Until(() => !Processes.IsRunning(started), "the Passenger the agent started to end with its session");
    }

    /// <summary>An agent's client that asks the system to end its server 20 ms after closing the server's input (OpenCode
    /// 1's end) has it end cleanly: exit code 0, and the headless Passenger it started ended with it and its socket taken
    /// away.</summary>
    [Fact]
    public async Task An_agents_server_the_system_asks_to_end_as_its_input_closes_ends_cleanly_with_the_passenger_it_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no SIGTERM for an agent's client to send");
        string root = TestFolders.Create("mcp-term-");
        DeleteAtEnd(root);
        string state = Path.Combine(root, "state");
        string notes = Plain("mcp-term-");
        Process proxy = StartPassenger(["--mcp-attach"], notes, new() { [StateStore.Variable] = state });
        McpClient client = await Connect(proxy, ["--mcp-attach"]);
        string sockets = McpHost.SocketFolderFor(state);
        DeleteAtEnd(sockets);
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        Task<CallToolResult> waiting = CallGivingUp(client, McpTools.Workspace, new(), giveUp.Token);
        string? socket = null;
        await Until(() => (socket = Directory.Exists(sockets) ? Directory.GetFiles(sockets, "*.sock").FirstOrDefault() : null) is not null,
            "Passenger to start with a window on the agent's folder");
        int started = McpHostProcess(socket!);
        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        proxy.StandardInput.Close();
        Waiting.For(TimeSpan.FromMilliseconds(20), "OpenCode 1's moment between closing the input and SIGTERM");
        // Gone already, the signal finds no process, which is as good.
        await ChildProcesses.RunAsync(ChildProcesses.Command("/bin/kill", ["-TERM", proxy.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            limit: TimeSpan.FromSeconds(30), answers: [1], cancel: Cancel);
        await Until(() => proxy.HasExited, "the agent's server to end");
        Assert.Equal(0, proxy.ExitCode);
        await Until(() => !Processes.IsRunning(started), "the Passenger the agent started to end with it");
        Assert.Empty(Directory.Exists(sockets) ? Directory.GetFiles(sockets, "*.sock") : []);
    }

    [AvaloniaFact]
    public void An_agents_first_call_has_the_running_Passenger_open_its_folder_in_a_window_of_its_own_which_asks_first()
    {
        string wanted = Plain("mcp-wanted-");
        string unrelated = Plain("mcp-unrelated-");
        MainWindow first = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        first.Show();
        first.KeepLayout();
        first.KeepSettings();
        first.OpenFolder(unrelated);
        Ui.Settle();
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", wanted])), "the agent's session starting");
        try
        {
            Task<CallToolResult> denied = Task.Run(() => Result(client, McpTools.Workspace));
            WaitFor(() => PassengerApp.Windows.Count == 2, "the running Passenger to open the agent's folder in a window of its own");
            MainWindow second = PassengerApp.Windows.Single(w => w != first);
            Assert.Equal(PathNames.Canonical(wanted), second.Folder);
            AgentAdmissionDialog asked = Admission(second, "the window to ask whether the agent may work there");
            Assert.Contains("Test Agent wants to work in this window", Texts(asked));
            Assert.Empty(first.OwnedWindows.OfType<AgentAdmissionDialog>());

            Press(asked, AgentAdmissionDialog.DontAllow);
            CallToolResult no = Await(denied, "the call the person did not allow");
            Assert.True(no.IsError == true && Text(no).Contains("did not let Test Agent work there", StringComparison.Ordinal),
                $"A call the person did not let in was answered: {Text(no)}");
            Assert.True(first.Agents == 0 && second.Agents == 0, "An agent the person did not let in is counted on a window");

            Task<JsonElement> allowed = Task.Run(() => Call(client, McpTools.Workspace));
            AgentAdmissionDialog again = Admission(second, "the window to ask again at the next call");
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(again).OfType<CheckBox>().Single(c => (string?)c.Content == "Always for this folder").IsChecked = true;
            Press(again, AgentAdmissionDialog.Allow);
            Assert.Equal(second.Folder, Await(allowed, "the call the person allowed").GetProperty("folder").GetString());
            WaitFor(() => second.AgentStatus == "Agent connected", "the status bar to say an agent is on the window");
            Assert.Null(first.AgentStatus);

            second.Close();
            Ui.Settle();
            JsonElement reopened = Await(Task.Run(() => Call(client, McpTools.Workspace)), "the call after the agent's window closed");
            MainWindow third = PassengerApp.Windows.Single(w => w != first);
            Assert.Equal(third.Folder, reopened.GetProperty("folder").GetString());
            Assert.Empty(third.OwnedWindows.OfType<AgentAdmissionDialog>());

            McpClient another = Await(Task.Run(() => Start(["--mcp-attach"], wanted)), "a second agent's session starting");
            try
            {
                Assert.Equal(third.Folder, Await(Task.Run(() => Call(another, McpTools.Workspace)), "the second agent's call").GetProperty("folder").GetString());
                Assert.Equal(2, PassengerApp.Windows.Count);
                WaitFor(() => third.AgentStatus == "2 agents connected", "the window to count both agents");
            }
            finally
            {
                Gone(another);
            }

            SettingsDialog taking = DialogButtonsTests.Check(third.ShowSettings());
            taking.Page = SettingsDialog.McpPage;
            Ui.Settle();
            Assert.Equal([PathNames.Canonical(wanted)], taking.AgentFolderList.Items.OfType<string>());
            Assert.False(taking.RemoveAgentFolderButton.IsEnabled, "Remove was offered with no folder chosen");
            taking.AgentFolderList.SelectedIndex = 0;
            Ui.Click(taking.RemoveAgentFolderButton, settle: false);
            Assert.Empty(taking.AgentFolderList.Items.OfType<string>());
            taking.Ok();
            Ui.Settle();
            Assert.False(third.AlwaysLetsAgentsIn, "The folder taken back still lets agents in unasked");
            Task<CallToolResult> afterwards = Task.Run(() => Result(client, McpTools.Workspace));
            Press(Admission(third, "the window to ask again once its folder was taken back"), AgentAdmissionDialog.DontAllow);
            Assert.True(Await(afterwards, "the call after the folder was taken back").IsError, "A call after the folder was taken back went on unasked");
        }
        finally
        {
            Gone(client);
            foreach (MainWindow window in PassengerApp.Windows.Reverse().ToList())
            {
                window.Close();
            }
        }
    }

    /// <summary>An agent that agreed an older protocol version with --mcp-attach (OpenCode's 2025-11-25) is answered in
    /// that version: the window is joined in it, so nothing of a later one (a result's serverInfo and resultType) reaches
    /// an agent that never asked for it.</summary>
    [AvaloniaFact]
    public void An_agent_on_an_older_protocol_is_answered_in_its_own_version_through_attach()
    {
        string older = Plain("mcp-older-");
        MainWindow window = Ui.OpenFolder(older, languageServer: false);
        window.ApplySettings(window.Settings with { McpServer = true });
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId], protocol: "2025-11-25")), "the agent attaching");
        try
        {
            Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
            CallToolResult answer = Await(Task.Run(() => Result(client, McpTools.Workspace)), "the workspace call");
            Assert.True(answer.IsError != true, $"workspace failed: {Text(answer)}");
            Assert.True(answer.Meta is null && answer.ResultType is null,
                $"An agent on 2025-11-25 was answered with a later version's fields: _meta {answer.Meta?.ToJsonString()}, resultType {answer.ResultType}");
        }
        finally
        {
            window.ApplySettings(window.Settings with { McpServer = false });
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent that gives up on the call the window's let-in question holds takes the question away: nothing waits
    /// on it, and a later "Allow" would let in an agent that asked nothing. The window's limit is long, so only the giving up
    /// can close it within the wait.</summary>
    [AvaloniaFact]
    public void An_agent_giving_up_on_its_call_takes_the_windows_question_away()
    {
        string unattended = Plain("mcp-given-up-");
        MainWindow window = new() { Width = 1200, Height = 800, AgentAnswerLimit = TimeSpan.FromMinutes(10), StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(unattended);
        Ui.Settle();
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            using CancellationTokenSource giveUp = new();
            Task<CallToolResult> givenUp = Task.Run(() => CallGivingUp(client, McpTools.Workspace, new(), giveUp.Token));
            WaitFor(() => window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible), "the window to ask whether the agent may work there");
            // Headless windows never lose activation, so only the words standing out can show a question waits.
            Button agent = window.FindControl<Button>("AgentButton")!;
            Assert.True(agent.Classes.Contains("asking"), "The status bar's words on a question an agent waits on should stand out (asking)");
            giveUp.Cancel();
            WaitFor(() => givenUp.IsCompleted && !window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible) && window.AgentStatus is null,
                "the question to go once the agent gave up on the call that waited on it");
            Assert.False(agent.Classes.Contains("asking"), "The status bar's words should stop standing out once no question waits");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>Settings' MCP Server page gives each agent its setup for this copy of Passenger, as the person pastes it: the
    /// running program's own path in every command and configuration, Claude Desktop's with the window's folder after
    /// --mcp-attach, the configurations JSON and TOML that read, and each Copy putting its text on the clipboard whole.</summary>
    [AvaloniaFact]
    public void Settings_gives_each_agent_its_setup_for_this_copy_and_copies_it_whole()
    {
        string project = Plain("mcp-setup-");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(project);
        Ui.Settle();
        SettingsDialog dialog = DialogButtonsTests.Check(window.ShowSettings());
        try
        {
            dialog.Query = "claude";
            Ui.Settle();
            Assert.Equal([SettingsDialog.McpPage], dialog.Pages);
            dialog.Query = string.Empty;
            dialog.Page = SettingsDialog.McpPage;
            Ui.Settle();
            Dictionary<string, string> setups = dialog.AgentSetups.ToDictionary(s => s.Heading, s => s.Text);
            Assert.Equal(["Claude Code", "A repository's .mcp.json", "Codex", "Codex's config.toml", "Claude Desktop", "OpenCode", "OpenCode 2"], dialog.AgentSetups.Select(s => s.Heading));

            string claude = setups["Claude Code"];
            Assert.StartsWith("claude mcp add passenger -- ", claude, StringComparison.Ordinal);
            Assert.EndsWith(" --mcp-attach", claude, StringComparison.Ordinal);
            string passenger = claude["claude mcp add passenger -- ".Length..^" --mcp-attach".Length];
            Assert.True(File.Exists(passenger) && PathNames.Same(passenger, PathNames.Canonical(passenger)), $"Claude Code's command names no program: {claude}");
            Assert.Equal($"codex mcp add passenger -- {passenger} --mcp-attach", setups["Codex"]);
            Assert.Equal($"[mcp_servers.passenger]\ncommand = {JsonSerializer.Serialize(passenger)}\nargs = [\"--mcp-attach\"]", setups["Codex's config.toml"]);
            foreach ((string heading, string[] arguments) in new (string, string[])[] { ("A repository's .mcp.json", ["--mcp-attach"]), ("Claude Desktop", ["--mcp-attach", PathNames.Canonical(project)]) })
            {
                JsonElement server = JsonDocument.Parse(setups[heading]).RootElement.GetProperty("mcpServers").GetProperty("passenger");
                Assert.Equal(passenger, server.GetProperty("command").GetString());
                Assert.Equal(arguments, server.GetProperty("args").EnumerateArray().Select(a => a.GetString()!));
            }
            // OpenCode's in each version's shape, OpenCode 2's tools called directly rather than through Code Mode.
            JsonElement openCode = JsonDocument.Parse(setups["OpenCode"]).RootElement.GetProperty("mcp").GetProperty("passenger");
            JsonElement openCode2 = JsonDocument.Parse(setups["OpenCode 2"]).RootElement.GetProperty("mcp").GetProperty("servers").GetProperty("passenger");
            foreach (JsonElement server in (JsonElement[])[openCode, openCode2])
            {
                Assert.Equal("local", server.GetProperty("type").GetString());
                Assert.Equal([passenger, "--mcp-attach"], server.GetProperty("command").EnumerateArray().Select(a => a.GetString()!));
            }
            Assert.Equal((true, 600_000), (openCode.GetProperty("enabled").GetBoolean(), openCode.GetProperty("timeout").GetInt32()));
            Assert.Equal((false, 1_800_000), (openCode2.GetProperty("codemode").GetBoolean(), openCode2.GetProperty("timeout").GetProperty("execution").GetInt32()));

            Button copy = dialog.CopyButtons["Claude Desktop"];
            Ui.Click(copy, settle: false);
            Ui.WaitFor(() => Ui.Clipboard(dialog) == setups["Claude Desktop"], "Claude Desktop's configuration on the clipboard");
            Assert.Equal("Copied", copy.Content);
            Ui.Click(dialog.CopyButtons["Claude Code"], settle: false);
            Ui.WaitFor(() => Ui.Clipboard(dialog) == claude, "Claude Code's command on the clipboard");
            Assert.Equal(("Copy", "Copied"), (copy.Content, dialog.CopyButtons["Claude Code"].Content));
        }
        finally
        {
            dialog.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_agents_open_of_another_folder_is_the_persons_to_allow_on_their_window_and_not_asked_on_the_agents_own()
    {
        string theirs = Plain("mcp-theirs-");
        string elsewhere = Plain("mcp-elsewhere-");
        MainWindow window = AgentsWindow(theirs, languageServer: false);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", theirs])), "the agent attaching");
        try
        {
            Task<CallToolResult> refused = Task.Run(() => Result(client, McpTools.Open, new() { ["path"] = elsewhere }));
            ConfirmDialog asked = DialogButtonsTests.Check(Question(window, "the question for the agent's open"));
            Assert.Contains(Texts(asked), t => t.StartsWith("Test Agent wants to open ", StringComparison.Ordinal) && t.EndsWith($" here, replacing {window.Folder}.", StringComparison.Ordinal));
            Press(asked, AgentAdmissionDialog.DontAllow);
            CallToolResult no = Await(refused, "the open the person did not allow");
            Assert.True(no.IsError == true && Text(no).Contains("did not let Test Agent open", StringComparison.Ordinal), $"An open the person did not allow was answered: {Text(no)}");
            Assert.Equal(PathNames.Canonical(theirs), window.Folder);

            Task<JsonElement> opening = Task.Run(() => Call(client, McpTools.Open, new() { ["path"] = elsewhere }));
            Press(Question(window, "the open asked again"), AgentAdmissionDialog.Allow);
            Assert.Equal(PathNames.Canonical(elsewhere), Await(opening, "the open the person allowed").GetProperty("folder").GetString());
            Assert.Equal(PathNames.Canonical(elsewhere), window.Folder);
        }
        finally
        {
            Gone(client);
            window.Close();
        }

        // The agent's own Passenger has nobody to ask.
        McpClient own = Await(Task.Run(() => Start(["--mcp", theirs])), "the agent's own Passenger starting");
        try
        {
            Assert.Equal(PathNames.Canonical(elsewhere), Await(Task.Run(() => Call(own, McpTools.Open, new() { ["path"] = elsewhere })), "the open on the agent's own Passenger")
                .GetProperty("folder").GetString());
        }
        finally
        {
            Gone(own);
        }
    }

    /// <summary>An agent let in on one folder works there alone: the person opening another in its window shuts it out, and its
    /// next call is asked again, changing nothing in the folder the person opened meanwhile. The agent's own open of a
    /// folder, which the person allows, keeps it let in there, though its first change there is asked for again.</summary>
    [AvaloniaFact]
    public void An_agent_let_in_on_one_folder_is_asked_again_once_the_person_opens_another_in_its_window()
    {
        string first = Plain("mcp-first-");
        string second = Plain("mcp-second-");
        string third = Plain("mcp-third-");
        MainWindow window = Ui.OpenFolder(first, languageServer: false);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> editing = Task.Run(() => Call(client, McpTools.Edit, Note("first")));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            Press(Question(window, "the question for the agent's edit"), "Allow");
            Await(editing, "the edit in the first folder");
            Assert.Equal("notes first\n", File.ReadAllText(Path.Combine(first, "Notes.txt")));
            WaitFor(() => window.Agents == 1, "the window to count the agent it let in");

            window.OpenFolder(second);
            Ui.Settle();
            WaitFor(() => window.Agents == 0, "the agent let in on the first folder to be shut out as the window opened the second");
            Task<CallToolResult> again = Task.Run(() => Result(client, McpTools.Edit, Note("second")));
            AgentAdmissionDialog asked = Admission(window, "the window to ask again on the folder the person opened");
            Assert.Contains(Texts(asked), t => t.Contains($"read and search {Path.GetFileName(second.TrimEnd(Path.DirectorySeparatorChar))},", StringComparison.Ordinal));
            Press(asked, AgentAdmissionDialog.DontAllow);
            CallToolResult refused = Await(again, "the edit after the window opened another folder");
            Assert.True(refused.IsError == true && Text(refused).Contains("did not let Test Agent work there", StringComparison.Ordinal),
                $"The agent's edit after the person opened another folder was answered: {Text(refused)}");
            Assert.Equal("notes\n", File.ReadAllText(Path.Combine(second, "Notes.txt")));

            Task<JsonElement> editingSecond = Task.Run(() => Call(client, McpTools.Edit, Note("second")));
            Press(Admission(window, "the window to ask at the agent's next call"), AgentAdmissionDialog.Allow);
            Press(Question(window, "the question for the agent's edit in the second folder"), "Allow");
            Await(editingSecond, "the edit in the second folder");
            Assert.Equal("notes second\n", File.ReadAllText(Path.Combine(second, "Notes.txt")));
            Task<JsonElement> opening = Task.Run(() => Call(client, McpTools.Open, new() { ["path"] = third }));
            Press(Question(window, "the question for the agent's open"), AgentAdmissionDialog.Allow);
            Assert.Equal(PathNames.Canonical(third), Await(opening, "the agent's open the person allowed").GetProperty("folder").GetString());
            Task<JsonElement> editingThird = Task.Run(() => Call(client, McpTools.Edit, Note("third")));
            ConfirmDialog allowing = Question(window, "the question for the agent's first edit in the folder it opened");
            Assert.Contains("Allow edit?", Texts(allowing));
            Assert.DoesNotContain(window.OwnedWindows.OfType<AgentAdmissionDialog>(), d => d.IsVisible);
            Press(allowing, "Allow");
            Await(editingThird, "the edit in the folder the agent opened");
            Assert.Equal("notes third\n", File.ReadAllText(Path.Combine(third, "Notes.txt")));
            Assert.Equal(1, window.Agents);
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>With Enable MCP Server on, a window keeps its agents as the person opens another folder there, but an agent
    /// set up in the first goes on in it: --mcp-attach joins a window on its own folder again (the running Passenger opening
    /// one), never the window now on the other.</summary>
    [AvaloniaFact]
    public void An_agent_set_up_in_a_folder_goes_on_there_once_its_window_opens_another()
    {
        string own = Plain("mcp-own-");
        string other = Plain("mcp-other-");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.KeepSettings();
        window.OpenFolder(own);
        Ui.Settle();
        window.ApplySettings(window.Settings with { McpServer = true });
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", own])), "the agent attaching");
        try
        {
            Assert.Equal(PathNames.Canonical(own), Await(Task.Run(() => Call(client, McpTools.Workspace)), "the agent's first call").GetProperty("folder").GetString());
            window.OpenFolder(other);
            Ui.Settle();
            JsonElement after = Await(Task.Run(() => Call(client, McpTools.Workspace)), "the agent's call after its window opened another folder");
            Assert.Equal(PathNames.Canonical(own), after.GetProperty("folder").GetString());
            MainWindow joined = PassengerApp.Windows.Single(w => w != window);
            Assert.Equal((PathNames.Canonical(own), PathNames.Canonical(other)), (joined.Folder, window.Folder));
        }
        finally
        {
            Gone(client);
            foreach (MainWindow open in PassengerApp.Windows.Reverse().ToList())
            {
                open.Close();
            }
        }
    }

    /// <summary>A folder let in always from one window stays let in when another window's Settings, open since before, take
    /// back a different one: the list merges what each changed.</summary>
    [AvaloniaFact]
    public void A_folder_let_in_always_from_one_window_stays_when_another_windows_settings_take_back_a_different_one()
    {
        string taken = Plain("mcp-taken-");
        string kept = Plain("mcp-kept-");
        MainWindow settingsWindow = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        settingsWindow.Show();
        settingsWindow.KeepLayout();
        settingsWindow.KeepSettings();
        settingsWindow.OpenFolder(taken);
        Ui.Settle();
        settingsWindow.ApplySettings(settingsWindow.Settings with { AgentFolders = [PathNames.Canonical(taken)] });
        MainWindow asking = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        asking.Show();
        asking.KeepLayout();
        asking.KeepSettings();
        asking.OpenFolder(kept);
        Ui.Settle();
        SettingsDialog dialog = DialogButtonsTests.Check(settingsWindow.ShowSettings());
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", kept])), "the agent attaching");
        try
        {
            dialog.Page = SettingsDialog.McpPage;
            Ui.Settle();
            Assert.Equal([PathNames.Canonical(taken)], dialog.AgentFolderList.Items.OfType<string>());

            Task<JsonElement> letIn = Task.Run(() => Call(client, McpTools.Workspace));
            AgentAdmissionDialog asked = Admission(asking, "the other window to ask whether the agent may work there");
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(asked).OfType<CheckBox>().Single(c => (string?)c.Content == "Always for this folder").IsChecked = true;
            Press(asked, AgentAdmissionDialog.Allow);
            Await(letIn, "the call let in always");
            Assert.True(asking.AlwaysLetsAgentsIn, "Always for this folder did not keep the folder");

            dialog.AgentFolderList.SelectedIndex = 0;
            Ui.Click(dialog.RemoveAgentFolderButton, settle: false);
            dialog.Ok();
            Ui.Settle();
            Assert.False(settingsWindow.AlwaysLetsAgentsIn, "The folder Settings took back still lets agents in unasked");
            Assert.True(asking.AlwaysLetsAgentsIn, "The folder the other window let in always while Settings were open was dropped as Settings took back another");
            Assert.Equal([PathNames.Canonical(kept)], settingsWindow.Settings.AgentFolders);
        }
        finally
        {
            dialog.Close();
            Gone(client);
            asking.Close();
            settingsWindow.Close();
        }
    }

    /// <summary>An edit of Notes.txt, as <see cref="McpRig.Plain"/> writes it, that adds a word.</summary>
    private static Dictionary<string, object?> Note(string word) => new() { ["path"] = "Notes.txt", ["oldText"] = "notes", ["newText"] = $"notes {word}" };

    /// <summary>The process a window's socket is named for (<c>PID-WINDOW.sock</c>).</summary>
    private static int McpHostProcess(string socket) => int.Parse(Path.GetFileNameWithoutExtension(socket).Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The Passengers a process has started, as the system lists them now: those it started, at any depth, that are
    /// its own program.</summary>
    private static int[] Started(Process proxy)
    {
        string name = proxy.ProcessName;
        return [.. Processes.Descendants(proxy.Id).Where(id =>
        {
            try
            {
                using Process child = Process.GetProcessById(id);
                return child.ProcessName == name;
            }
            catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        })];
    }
}

/// <summary>McpAttachTests' agents where what is tested is held to a time: an attach given up during initialization, one
/// past a full socket backlog, an attached diagnostic on a suspended server given up or shut out, and a call nobody answers
/// coming back at the window's limit. So the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class McpAttachTimingTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpAttachTimingTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [AvaloniaFact]
    public void Cancelling_an_attach_during_initialization_closes_its_stream_and_a_queued_call_does_not_lose_the_join()
    {
        string notes = Plain("mcp-initialize-");
        MainWindow window = AgentsWindow(notes, languageServer: false);
        McpClient? client = null;
        using CancellationTokenSource firstCancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        using CancellationTokenSource queuedCancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        try
        {
            using WithheldWindow fake = new(window);
            client = Await(Task.Run(() => Start(["--mcp-attach", notes])), "the agent's session starting");
            using CancellationTokenSource acceptLimit = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            acceptLimit.CancelAfter(TimeSpan.FromSeconds(10).Scaled());
            Task<Socket> accepting = fake.Listener.AcceptAsync(acceptLimit.Token).AsTask();
            Task<CallToolResult> first = Task.Run(() => CallGivingUp(client, McpTools.Workspace, new(), firstCancel.Token));
            using Socket accepted = Await(accepting, "the proxy connecting to the withheld window");
            using NetworkStream stream = new(accepted, ownsSocket: false);
            using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            string? initialize = Await(reader.ReadLineAsync(Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10).Scaled(), Cancel), "the proxy's initialize request");
            Assert.NotNull(initialize);
            using JsonDocument request = JsonDocument.Parse(initialize);
            Assert.Contains(request.RootElement.GetProperty("method").GetString(), new[] { "server/discover", "initialize" });

            TaskCompletionSource queuedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<CallToolResult> queued = Task.Run(async () =>
            {
                Task<CallToolResult> call = CallGivingUp(client, McpTools.Workspace, new(), queuedCancel.Token);
                queuedStarted.SetResult();
                return await call;
            });
            Ui.PumpUntil(() => queuedStarted.Task.IsCompleted, "the second call starting while initialization holds the join", TimeSpan.FromSeconds(3));
            queuedCancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Await(queued.WaitAsync(TimeSpan.FromSeconds(3).Scaled(), Cancel), "the queued call giving up"));
            Assert.False(first.IsCompleted, "Cancelling a queued call ended the join another call still owns");

            Task<JsonElement> retry = Task.Run(() => Call(client, McpTools.Workspace));
            Assert.False(Await(Task.Run(() => Waiting.Until(() => fake.Listener.Poll(0, SelectMode.SelectRead), TimeSpan.FromSeconds(1))),
                "checking whether the cancelled waiter let another join pass"), "Cancelling a queued call released another call's join lock");
            Assert.False(retry.IsCompleted, "A third call passed the join while initialization was still held");
            fake.Dispose();
            firstCancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Await(first.WaitAsync(TimeSpan.FromSeconds(3).Scaled(), Cancel), "the initializing call giving up"));
            Await(reader.ReadToEndAsync(Cancel).WaitAsync(TimeSpan.FromSeconds(10).Scaled(), Cancel),
                "the cancelled initialization's stream to reach EOF");
            Assert.Equal(window.Folder, Await(retry.WaitAsync(TimeSpan.FromSeconds(10).Scaled(), Cancel), "the next call joining the real window").GetProperty("folder").GetString());
        }
        finally
        {
            firstCancel.Cancel();
            queuedCancel.Cancel();
            try
            {
                if (client is not null)
                {
                    Gone(client);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void An_attach_passes_a_full_socket_backlog_and_joins_the_next_window()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This test fills a Linux Unix-domain socket backlog");
        string notes = Plain("mcp-backlog-");
        MainWindow window = AgentsWindow(notes, languageServer: false);
        McpClient? client = null;
        List<Socket> fillers = [];
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        try
        {
            using WithheldWindow fake = new(window);
            bool full = false;
            for (int i = 0; i < 16; i++)
            {
                Socket filler = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                fillers.Add(filler);
                try
                {
                    filler.Connect(new UnixDomainSocketEndPoint(fake.Path));
                }
                catch (SocketException blocked) when (blocked.SocketErrorCode == SocketError.WouldBlock)
                {
                    full = true;
                    break;
                }
            }
            Assert.True(full, "Sixteen nonblocking connections did not fill the withheld window's backlog");
            client = Await(Task.Run(() => Start(["--mcp-attach", notes])), "the agent's session starting");
            Task<CallToolResult> workspace = Task.Run(() => CallGivingUp(client, McpTools.Workspace, new(), giveUp.Token));
            CallToolResult result = Await(workspace.WaitAsync(TimeSpan.FromSeconds(15).Scaled(), Cancel), "the proxy passing the full backlog and joining the real window");
            Assert.False(result.IsError == true, Text(result));
            using JsonDocument seen = JsonDocument.Parse(Text(result));
            Assert.Equal(window.Folder, seen.RootElement.GetProperty("folder").GetString());
        }
        finally
        {
            giveUp.Cancel();
            foreach (Socket filler in fillers)
            {
                filler.Dispose();
            }
            try
            {
                if (client is not null)
                {
                    Gone(client);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_attached_diagnostic_stops_waiting_on_a_suspended_server_when_cancelled_or_shut_out(bool revokeConnection)
    {
        MainWindow window = AgentsWindow(Folder);
        McpClient? client = null;
        int suspended = 0;
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        try
        {
            window.OpenFile(Path.Combine(window.Folder!, "Helper.cs"));
            Ui.Settle();
            client = Await(Task.Run(() => Start(["--mcp-attach", Folder])), "the agent attaching");
            Await(Task.Run(() => Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" })), "the language server loading the sample");
            Ui.Settle();
            Assert.DoesNotContain(InFlight.Now(), piece => piece.What.Contains(Passenger.Lsp.LspMethods.DocumentDiagnostic, StringComparison.Ordinal));
            Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
            (server.SlowRequest, server.ResponseLimit) = (TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            suspended = server.ProcessId;
            Processes.Suspend(suspended, "The test's language server");

            Task<CallToolResult> diagnostic = Task.Run(() => revokeConnection
                ? Result(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" })
                : CallGivingUp(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }, giveUp.Token));
            Ui.PumpUntil(() => InFlight.Now().Any(piece => piece.What.Contains(Passenger.Lsp.LspMethods.DocumentDiagnostic, StringComparison.Ordinal)),
                "the attached agent's diagnostic to wait on the suspended server", TimeSpan.FromSeconds(10));
            if (revokeConnection)
            {
                window.ApplySettings(window.Settings with { McpServer = false });
                Ui.PumpUntil(() => diagnostic.IsCompleted, "the shut-out agent's diagnostic to return", TimeSpan.FromSeconds(10));
                CallToolResult refused = diagnostic.GetAwaiter().GetResult();
                Assert.True(refused.IsError == true, Text(refused));
                Assert.Contains("connection to the Passenger window ended", Text(refused), StringComparison.Ordinal);
                Ui.PumpUntil(() => window.Agents == 0, "the still-open window to let the agent go", TimeSpan.FromSeconds(10));
                Assert.True(window.IsVisible, "Shutting out an agent closed the person's window");
            }
            else
            {
                giveUp.Cancel();
                Ui.PumpUntil(() => diagnostic.IsCompleted, "the agent's diagnostic giving up", TimeSpan.FromSeconds(10));
                Assert.ThrowsAny<OperationCanceledException>(() => diagnostic.GetAwaiter().GetResult());
            }
            Ui.PumpUntil(() => !InFlight.Now().Any(piece => piece.What.Contains(Passenger.Lsp.LspMethods.DocumentDiagnostic, StringComparison.Ordinal)),
                "the diagnostic request to clear while its server is still suspended", TimeSpan.FromSeconds(10));
            Assert.True(Processes.IsRunning(suspended), "The request cleared only because its language server ended");
        }
        finally
        {
            giveUp.Cancel();
            if (suspended != 0)
            {
                Processes.Kill(suspended);
            }
            try
            {
                if (client is not null)
                {
                    Gone(client);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>A live socket for this process and folder, ranked before the real window, which never answers initialize.</summary>
    private sealed class WithheldWindow : IDisposable
    {
        // Named as a window's socket is, short: macOS takes a socket path of at most 104 bytes, and its temporary folder is long.
        public string Path { get; } = System.IO.Path.Combine(McpHost.SocketFolder, $"{ProcessId}-held{Guid.NewGuid().ToString("N")[..6]}.sock");
        public Socket Listener { get; } = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        public WithheldWindow(MainWindow window)
        {
            try
            {
                Listener.Bind(new UnixDomainSocketEndPoint(Path));
                Listener.Listen(1);
                File.WriteAllText(System.IO.Path.ChangeExtension(Path, ".folder"), window.Folder);
                File.SetLastWriteTimeUtc(Path, File.GetLastWriteTimeUtc(window.AgentSocket!).AddMinutes(1));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Listener.Dispose();
            File.Delete(Path);
            File.Delete(System.IO.Path.ChangeExtension(Path, ".folder"));
        }
    }

    /// <summary>Nobody at the window: an agent's call that waits for the person to let it in, and one that waits for their
    /// leave to build, each come back once the window's limit is up, saying nobody answered, the question gone; meanwhile
    /// the status bar says who waits on what and the agent hears, through --mcp-attach, what it waits on; a later call asks
    /// again. With protocol traces on, mcp.log has each call's waits and ends with their times.</summary>
    [AvaloniaFact]
    public void An_agents_call_nobody_answers_comes_back_at_the_windows_limit_and_hears_what_it_waits_on_meanwhile()
    {
        TimeSpan limit = TimeSpan.FromSeconds(5).Scaled();
        string unattended = Plain("mcp-unanswered-");
        MainWindow window = new() { Width = 1200, Height = 800, AgentAnswerLimit = limit, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(unattended);
        Ui.Settle();
        window.ApplySettings(window.Settings with { TraceProtocols = true });
        string where = $"the Passenger window on {window.Folder}";
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Heard letIn = new();
            Stopwatch asked = Stopwatch.StartNew();
            Task<CallToolResult> unanswered = Task.Run(() => Result(client, McpTools.Workspace, heard: letIn));
            string? said = null;
            WaitFor(() => window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible) && (said = window.AgentStatus) is not null,
                "the window to ask whether the agent may work there");
            Assert.Equal("Test Agent waits for you: let it in?", said);
            CallToolResult notLetIn = Await(unanswered, "the call nobody let in");
            Assert.True(asked.Elapsed < limit + TimeSpan.FromSeconds(30).Scaled(), $"The call nobody answered came back {asked.Elapsed.TotalSeconds:0} s after it was asked, its limit {limit.TotalSeconds:0} s");
            Assert.True(notLetIn.IsError == true && Text(notLetIn).Contains($"Nobody at {where} answered within {limit.TotalSeconds:0} s whether Test Agent may work there. A later call asks again.", StringComparison.Ordinal),
                $"The call nobody answered was answered: {Text(notLetIn)}");
            Assert.DoesNotContain(window.OwnedWindows.OfType<AgentAdmissionDialog>(), d => d.IsVisible);
            Assert.Null(window.AgentStatus);
            Assert.True(letIn.Told.Any(t => t.Message?.StartsWith($"Waiting for the person at {where} to let Test Agent in", StringComparison.Ordinal) == true),
                $"The agent never heard what its call waited on: {letIn}");

            window.ApplySettings(window.Settings with { McpServer = true });
            Heard leave = new();
            asked.Restart();
            Task<CallToolResult> unallowed = Task.Run(() => Result(client, McpTools.Build, heard: leave));
            said = null;
            WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible) && (said = window.AgentStatus) is not null,
                "the window to ask whether the agent may build");
            Assert.Equal("Test Agent waits for you: allow build?", said);
            Assert.Contains(Texts(window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible)),
                t => t.StartsWith("Test Agent, connected through Passenger's MCP server, wants to build", StringComparison.Ordinal));
            CallToolResult notAllowed = Await(unallowed, "the build nobody allowed");
            Assert.True(asked.Elapsed < limit + TimeSpan.FromSeconds(30).Scaled(), $"The build nobody allowed came back {asked.Elapsed.TotalSeconds:0} s after it was asked, its limit {limit.TotalSeconds:0} s");
            Assert.True(notAllowed.IsError == true && Text(notAllowed).Contains($"Nobody at {where} answered within {limit.TotalSeconds:0} s whether Test Agent may use build. A later call asks again.", StringComparison.Ordinal),
                $"The build nobody allowed was answered: {Text(notAllowed)}");
            WaitFor(() => !window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible) && window.AgentStatus == "Agent connected",
                "the question nobody answered to close and the status bar to stop saying the agent waits");
            Assert.False(window.Builder.IsRunning, "A build nobody allowed ran");
            IReadOnlyList<ModelContextProtocol.ProgressNotificationValue> told = leave.Told;
            Assert.True(told.Any(t => t.Message?.StartsWith($"Waiting for the person at {where} to allow Test Agent to use build", StringComparison.Ordinal) == true),
                $"The agent never heard its build waited for the person: {leave}");
            // The SDK's client hands the progress over as it comes, not always in order.
            Assert.True(told.Select(t => t.Progress).Distinct().Count() == told.Count, $"The progress told through --mcp-attach repeated a count: {leave}");
            // The window's word passed on as it said it, never the proxy's own beat over it.
            Assert.True(!told.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t.Message ?? string.Empty, @"\(\d+\.\d s\) \(\d+\.\d s\)$")),
                $"--mcp-attach told its own beat over the window's words: {leave}");

            Task<CallToolResult> again = Task.Run(() => Result(client, McpTools.Build));
            Press(Question(window, "the question asked again"), "Deny");
            Assert.Contains("did not allow build", Text(Await(again, "the build the person denied")), StringComparison.Ordinal);

            IdeLog.Flush();
            string trace = Ui.Shared(IdeLog.PathOf(IdeLog.Mcp));
            foreach (string line in (string[])[
                "workspace] began, from Test Agent, told progress",
                $"workspace] waiting: Waiting for the person at {where} to let Test Agent in",
                "workspace] answered an error after ",
                $"build] waiting: Waiting for the person at {where} to allow Test Agent to use build",
                "build] answered an error after "])
            {
                Assert.True(trace.Contains(line, StringComparison.Ordinal), $"mcp.log does not have \"{line}\":\n{string.Join('\n', trace.Split('\n').TakeLast(30))}");
            }
            Assert.Matches(@"\[window call \d+ build\] waited \d+\.\d s \(Waiting for the person .* to allow Test Agent to use build .*\): Unanswered", trace);
        }
        finally
        {
            window.ApplySettings(window.Settings with { TraceProtocols = false });
            Gone(client);
            window.Close();
        }
    }
}
