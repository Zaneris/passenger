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
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpAttachTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    [Gate]
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

        OwnProcess asked = OwnProcess.Of(proxy);
        proxy.StandardInput.Close();
        Waiting.For(TimeSpan.FromMilliseconds(20), "OpenCode 1's moment between closing the input and SIGTERM");
        // Gone already, it is sent nothing, which is as good.
        asked.Terminate();
        await Until(() => !DotnetProcesses.Runs(proxy), "the agent's server to end");
        Assert.Equal(0, proxy.ExitCode);
        await Until(() => !Processes.IsRunning(started), "the Passenger the agent started to end with it");
        Assert.Empty(Directory.Exists(sockets) ? Directory.GetFiles(sockets, "*.sock") : []);
    }

    /// <summary>The gate's attach as one flow: an agent's first call having the running Passenger open its folder in a
    /// window of its own that asks first, an older protocol answered in its own version, a call given up taking the
    /// window's question away, and Settings giving each agent its setup, each step the gate test it names in an
    /// instance of its own.</summary>
    [AvaloniaFact]
    public void An_attached_agent_opens_its_folder_in_a_window_of_its_own_asking_first_in_its_own_protocol()
    {
        Flow.StepApart("an agents first call has the running Passenger open its folder in a window of its own which asks first", () => new McpAttachTests(restored), t => t.An_agents_first_call_has_the_running_Passenger_open_its_folder_in_a_window_of_its_own_which_asks_first());
        Flow.StepApart("an agent on an older protocol is answered in its own version through attach", () => new McpAttachTests(restored), t => t.An_agent_on_an_older_protocol_is_answered_in_its_own_version_through_attach());
        Flow.StepApart("an agent giving up on its call takes the windows question away", () => new McpAttachTests(restored), t => t.An_agent_giving_up_on_its_call_takes_the_windows_question_away());
        Flow.StepApart("settings gives each agent its setup for this copy and copies it whole", () => new McpAttachTests(restored), t => t.Settings_gives_each_agent_its_setup_for_this_copy_and_copies_it_whole());
    }

    /// <summary>The gate's folders an attached agent is let into as one flow: another folder's open the person's to
    /// allow, one let in asked again once the person opens another in its window, one set up in a folder going on
    /// there, and a folder let in always staying when another window's settings take back a different one, each step
    /// the gate test it names in an instance of its own.</summary>
    [AvaloniaFact]
    public void An_attached_agent_is_let_into_a_folder_only_as_the_person_allows_and_asked_again_for_another()
    {
        Flow.StepApart("an agents open of another folder is the persons to allow on their window and not asked on the agents own", () => new McpAttachTests(restored), t => t.An_agents_open_of_another_folder_is_the_persons_to_allow_on_their_window_and_not_asked_on_the_agents_own());
        Flow.StepApart("an agent let in on one folder is asked again once the person opens another in its window", () => new McpAttachTests(restored), t => t.An_agent_let_in_on_one_folder_is_asked_again_once_the_person_opens_another_in_its_window());
        Flow.StepApart("an agent set up in a folder goes on there once its window opens another", () => new McpAttachTests(restored), t => t.An_agent_set_up_in_a_folder_goes_on_there_once_its_window_opens_another());
        Flow.StepApart("a folder let in always from one window stays when another windows settings take back a different one", () => new McpAttachTests(restored), t => t.A_folder_let_in_always_from_one_window_stays_when_another_windows_settings_take_back_a_different_one());
    }

    /// <summary>An attached agent's questions as one flow, each step one test's check in an instance of its own: a
    /// question taken away let go, a folder opened while one shows taking it away and refusing its call, the keys left
    /// in the editor with an Enter never allowing, an edit allowed once asked again and one allowed for the session not
    /// its input to pass, and every tool's request going on unasked once allowed while a denial holds.</summary>
    [AvaloniaFact]
    public void An_attached_agents_questions_are_answered_by_the_person_alone_and_let_go_once_taken_away()
    {
        Flow.StepApart("an agents question taken away is let go", () => new McpAttachTests(restored), t => t.An_agents_question_taken_away_is_let_go());
        Flow.StepApart("a folder opened while an agents question shows takes the question away and refuses its call", () => new McpAttachTests(restored), t => t.A_folder_opened_while_an_agents_question_shows_takes_the_question_away_and_refuses_its_call());
        Flow.StepApart("an agents question leaves the keys in the editor and an Enter never allows", () => new McpAttachTests(restored), t => t.An_agents_question_leaves_the_keys_in_the_editor_and_an_Enter_never_allows());
        Flow.StepApart("an agents edit allowed once is asked again at its next and one allowed for the session is not its input to passenger asked apart", () => new McpAttachTests(restored), t => t.An_agents_edit_allowed_once_is_asked_again_at_its_next_and_one_allowed_for_the_session_is_not_its_input_to_passenger_asked_apart());
        Flow.StepApart("an agents request for every tool lets each go on unasked once allowed and denied holds for the session", () => new McpAttachTests(restored), t => t.An_agents_request_for_every_tool_lets_each_go_on_unasked_once_allowed_and_denied_holds_for_the_session());
    }

    /// <summary>An attached agent's actions as one flow, each step one test's check in an instance of its own: the
    /// files it read staying open without a tab until it goes, a tool window shown without the person's keys or a
    /// window brought forward, a popup or dialog action refused with its build behind, no list, menu, box or dialog
    /// opened over the person's code, and a closed file it opens or edits given a tab behind the person's.</summary>
    [AvaloniaFact]
    public void An_attached_agents_actions_and_files_stay_behind_the_persons_keys_and_windows()
    {
        Flow.StepApart("the files an agent read stay open without a tab until it goes and the persons own stay open after", () => new McpAttachTests(restored), t => t.The_files_an_agent_read_stay_open_without_a_tab_until_it_goes_and_the_persons_own_stay_open_after());
        Flow.StepApart("an agents ide action shows a tool window without the persons keys and brings no window forward", () => new McpAttachTests(restored), t => t.An_agents_ide_action_shows_a_tool_window_without_the_persons_keys_and_brings_no_window_forward());
        Flow.StepApart("an agents ide action of a popup or dialog is refused and its build shows the Build window behind", () => new McpAttachTests(restored), t => t.An_agents_ide_action_of_a_popup_or_dialog_is_refused_and_its_build_shows_the_Build_window_behind());
        Flow.StepApart("an agents ide action opens no list menu box or dialog over the persons code and leaves their keys", () => new McpAttachTests(restored), t => t.An_agents_ide_action_opens_no_list_menu_box_or_dialog_over_the_persons_code_and_leaves_their_keys());
        Flow.StepApart("an agents open or edit of a closed file gives it a tab behind the persons which keeps the keys", () => new McpAttachTests(restored), t => t.An_agents_open_or_edit_of_a_closed_file_gives_it_a_tab_behind_the_persons_which_keeps_the_keys());
    }

    [Gate]
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
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", wanted])), "the agent's session starting");
        try
        {
            Task<CallToolResult> denied = Waiting.Apart(() => Result(client, McpTools.Workspace));
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

            Task<JsonElement> allowed = Waiting.Apart(() => Call(client, McpTools.Workspace));
            AgentAdmissionDialog again = Admission(second, "the window to ask again at the next call");
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(again).OfType<CheckBox>().Single(c => (string?)c.Content == "Always for this folder").IsChecked = true;
            Press(again, AgentAdmissionDialog.Allow);
            Assert.Equal(second.Folder, Await(allowed, "the call the person allowed").GetProperty("folder").GetString());
            WaitFor(() => second.AgentStatus == "Agent connected", "the status bar to say an agent is on the window");
            Assert.Null(first.AgentStatus);

            second.Close();
            Ui.Settle();
            JsonElement reopened = Await(Waiting.Apart(() => Call(client, McpTools.Workspace)), "the call after the agent's window closed");
            MainWindow third = PassengerApp.Windows.Single(w => w != first);
            Assert.Equal(third.Folder, reopened.GetProperty("folder").GetString());
            Assert.Empty(third.OwnedWindows.OfType<AgentAdmissionDialog>());

            McpClient another = Await(Waiting.Apart(() => Start(["--mcp-attach"], wanted)), "a second agent's session starting");
            try
            {
                Assert.Equal(third.Folder, Await(Waiting.Apart(() => Call(another, McpTools.Workspace)), "the second agent's call").GetProperty("folder").GetString());
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
            Task<CallToolResult> afterwards = Waiting.Apart(() => Result(client, McpTools.Workspace));
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
    [Gate]
    [AvaloniaFact]
    public void An_agent_on_an_older_protocol_is_answered_in_its_own_version_through_attach()
    {
        string older = Plain("mcp-older-");
        MainWindow window = Ui.OpenFolder(older, languageServer: false);
        window.ApplySettings(window.Settings with { McpServer = true });
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId], protocol: "2025-11-25")), "the agent attaching");
        try
        {
            Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
            CallToolResult answer = Await(Waiting.Apart(() => Result(client, McpTools.Workspace)), "the workspace call");
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
    /// <summary>The files an agent's calls read stay open after them, with no tab, until the agent goes; a file the person
    /// has open in a tab, which the agent read too, stays as it was once the agent goes.</summary>
    private void The_files_an_agent_read_stay_open_without_a_tab_until_it_goes_and_the_persons_own_stay_open_after()
    {
        string folder = Plain("mcp-kept-");
        File.WriteAllText(Path.Combine(folder, "Mine.txt"), "mine\n");
        MainWindow window = AgentsWindow(folder, languageServer: false);
        Assert.Null(window.OpenFile(Path.Combine(folder, "Mine.txt")));
        Ui.Settle();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Await(Waiting.Apart(() => Call(client, McpTools.EditorText, new() { ["path"] = "Notes.txt" })), "the agent reading Notes.txt");
            Await(Waiting.Apart(() => Call(client, McpTools.EditorText, new() { ["path"] = "Mine.txt" })), "the agent reading Mine.txt");
            Passenger.App.Workspace.OpenDocument? notes = window.EditorArea.Documents.FirstOrDefault(d => Path.GetFileName(d.Path) == "Notes.txt");
            Assert.True(notes is not null && !window.EditorArea.HasTab(notes),
                $"Notes.txt, which the agent read, was not kept open without a tab after its call: the window has open {string.Join(", ", window.EditorArea.Documents.Select(d => Path.GetFileName(d.Path)))} ({PassengerApp.Windows.Count} windows)");
        }
        finally
        {
            Gone(client);
        }
        WaitFor(() => window.EditorArea.Documents.All(d => Path.GetFileName(d.Path) != "Notes.txt"), "Notes.txt, kept for the agent, to close as the agent went");
        Passenger.App.Workspace.OpenDocument mine = Assert.Single(window.EditorArea.Documents, d => Path.GetFileName(d.Path) == "Mine.txt");
        Assert.True(window.EditorArea.HasTab(mine), "The person's own Mine.txt lost its tab as the agent that read it went");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void An_agent_giving_up_on_its_call_takes_the_windows_question_away()
    {
        string unattended = Plain("mcp-given-up-");
        MainWindow window = new() { Width = 1200, Height = 800, AgentAnswerLimit = TimeSpan.FromMinutes(10), StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(unattended);
        Ui.Settle();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            using CancellationTokenSource giveUp = new();
            Task<CallToolResult> givenUp = Waiting.Apart(() => CallGivingUp(client, McpTools.Workspace, new(), giveUp.Token));
            WaitFor(() => window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible), "the window to ask whether the agent may work there");
            // Headless windows never lose activation, so only the words standing out can show a question waits.
            Button agent = window.FindControl<Button>("AgentButton")!;
            Assert.True(agent.Classes.Contains(StyleClasses.Asking), "The status bar's words on a question an agent waits on should stand out (asking)");
            giveUp.Cancel();
            WaitFor(() => givenUp.IsCompleted && !window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible) && window.AgentStatus is null,
                "the question to go once the agent gave up on the call that waited on it");
            Assert.False(agent.Classes.Contains(StyleClasses.Asking), "The status bar's words should stop standing out once no question waits");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's question taken away, the agent having given up on the call that waited on it: the question goes,
    /// held by nothing the window keeps for the questions after it.</summary>
    private void An_agents_question_taken_away_is_let_go()
    {
        string unattended = Plain("mcp-question-gone-");
        MainWindow window = new() { Width = 1200, Height = 800, AgentAnswerLimit = TimeSpan.FromMinutes(10), StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(unattended);
        Ui.Settle();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Ui.Collected(AskedAndTakenAway(window, client), "the question taken away to be let go: held by the window, each question an agent "
                + "raised would keep its dialog for the window's life");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>The window asked whether the agent may work there, then the agent giving up on its call: the question held only
    /// weakly here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference<AgentAdmissionDialog> AskedAndTakenAway(MainWindow window, McpClient client)
    {
        using CancellationTokenSource giveUp = new();
        Task<CallToolResult> givenUp = Waiting.Apart(() => CallGivingUp(client, McpTools.Workspace, new(), giveUp.Token));
        WaitFor(() => window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible), "the window to ask whether the agent may work there");
        WeakReference<AgentAdmissionDialog> asked = new(window.OwnedWindows.OfType<AgentAdmissionDialog>().Single(d => d.IsVisible));
        giveUp.Cancel();
        WaitFor(() => givenUp.IsCompleted && !window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(), "the question to go once the agent gave up on its call");
        return asked;
    }

    /// <summary>Settings' MCP Server page gives each agent its setup for this copy of Passenger, as the person pastes it: the
    /// running program's own path in every command and configuration, Claude Desktop's with the window's folder after
    /// --mcp-attach, the configurations JSON and TOML that read, and each Copy putting its text on the clipboard whole.</summary>
    [Gate]
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

    [Gate]
    [AvaloniaFact]
    public void An_agents_open_of_another_folder_is_the_persons_to_allow_on_their_window_and_not_asked_on_the_agents_own()
    {
        string theirs = Plain("mcp-theirs-");
        string elsewhere = Plain("mcp-elsewhere-");
        MainWindow window = AgentsWindow(theirs, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", theirs])), "the agent attaching");
        try
        {
            Task<CallToolResult> refused = Waiting.Apart(() => Result(client, McpTools.Open, new() { ["path"] = elsewhere }));
            Window asked = DialogButtonsTests.Check(Question(window, "the question for the agent's open"));
            Assert.Contains(Texts(asked), t => t.StartsWith("Test Agent wants to open ", StringComparison.Ordinal) && t.EndsWith($" here, replacing {window.Folder}.", StringComparison.Ordinal));
            Press(asked, AgentAdmissionDialog.DontAllow);
            CallToolResult no = Await(refused, "the open the person did not allow");
            Assert.True(no.IsError == true && Text(no).Contains("did not let Test Agent open", StringComparison.Ordinal), $"An open the person did not allow was answered: {Text(no)}");
            Assert.Equal(PathNames.Canonical(theirs), window.Folder);

            Task<JsonElement> opening = Waiting.Apart(() => Call(client, McpTools.Open, new() { ["path"] = elsewhere }));
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
        McpClient own = Await(Waiting.Apart(() => Start(["--mcp", theirs])), "the agent's own Passenger starting");
        try
        {
            Assert.Equal(PathNames.Canonical(elsewhere), Await(Waiting.Apart(() => Call(own, McpTools.Open, new() { ["path"] = elsewhere })), "the open on the agent's own Passenger")
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
    [Gate]
    [AvaloniaFact]
    public void An_agent_let_in_on_one_folder_is_asked_again_once_the_person_opens_another_in_its_window()
    {
        string first = Plain("mcp-first-");
        string second = Plain("mcp-second-");
        string third = Plain("mcp-third-");
        MainWindow window = Ui.OpenFolder(first, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> editing = Waiting.Apart(() => Call(client, McpTools.Edit, Note("first")));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            Press(Question(window, "the question for the agent's edit"), AgentToolDialog.AllowForSession);
            Await(editing, "the edit in the first folder");
            Assert.Equal("notes first\n", File.ReadAllText(Path.Combine(first, "Notes.txt")));
            WaitFor(() => window.Agents == 1, "the window to count the agent it let in");

            window.OpenFolder(second);
            Ui.Settle();
            WaitFor(() => window.Agents == 0, "the agent let in on the first folder to be shut out as the window opened the second");
            Task<CallToolResult> again = Waiting.Apart(() => Result(client, McpTools.Edit, Note("second")));
            AgentAdmissionDialog asked = Admission(window, "the window to ask again on the folder the person opened");
            Assert.Contains(Texts(asked), t => t.Contains($"read and search {Path.GetFileName(second.TrimEnd(Path.DirectorySeparatorChar))},", StringComparison.Ordinal));
            Press(asked, AgentAdmissionDialog.DontAllow);
            CallToolResult refused = Await(again, "the edit after the window opened another folder");
            Assert.True(refused.IsError == true && Text(refused).Contains("did not let Test Agent work there", StringComparison.Ordinal),
                $"The agent's edit after the person opened another folder was answered: {Text(refused)}");
            Assert.Equal("notes\n", File.ReadAllText(Path.Combine(second, "Notes.txt")));

            Task<JsonElement> editingSecond = Waiting.Apart(() => Call(client, McpTools.Edit, Note("second")));
            Press(Admission(window, "the window to ask at the agent's next call"), AgentAdmissionDialog.Allow);
            Press(Question(window, "the question for the agent's edit in the second folder"), AgentToolDialog.AllowForSession);
            Await(editingSecond, "the edit in the second folder");
            Assert.Equal("notes second\n", File.ReadAllText(Path.Combine(second, "Notes.txt")));
            Task<JsonElement> opening = Waiting.Apart(() => Call(client, McpTools.Open, new() { ["path"] = third }));
            Press(Question(window, "the question for the agent's open"), AgentAdmissionDialog.Allow);
            Assert.Equal(PathNames.Canonical(third), Await(opening, "the agent's open the person allowed").GetProperty("folder").GetString());
            Task<JsonElement> editingThird = Waiting.Apart(() => Call(client, McpTools.Edit, Note("third")));
            Window allowing = Question(window, "the question for the agent's first edit in the folder it opened");
            Assert.Contains("Allow edit?", Texts(allowing));
            Assert.DoesNotContain(window.OwnedWindows.OfType<AgentAdmissionDialog>(), d => d.IsVisible);
            Press(allowing, AgentToolDialog.AllowForSession);
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

    /// <summary>A question an agent's call waits on goes as the person opens another folder in the window, its call refused
    /// saying the folder changed, so an Allow never makes the change in the folder opened since: with Enable MCP Server on,
    /// the agent stays let in, its edit asked about in the first folder is made in neither, and its next edit is asked about
    /// in the folder now open.</summary>
    private void A_folder_opened_while_an_agents_question_shows_takes_the_question_away_and_refuses_its_call()
    {
        string first = Plain("mcp-asked-");
        string second = Plain("mcp-opened-");
        MainWindow window = Ui.OpenFolder(first, languageServer: false);
        window.ApplySettings(window.Settings with { McpServer = true });
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> editing = Waiting.Apart(() => Result(client, McpTools.Edit, Note("asked")));
            Window asked = Question(window, "the question for the agent's edit in the first folder");
            window.OpenFolder(second);
            Ui.Settle();
            Assert.False(asked.IsVisible, "The question for the agent's edit in the folder left stayed open over the folder opened, where an Allow would make it");
            CallToolResult refused = Await(editing, "the agent's edit asked about in the folder left");
            Assert.True(refused.IsError == true && Text(refused).Contains(Refusals.FolderChanged(PathNames.Canonical(first)), StringComparison.Ordinal),
                $"The agent's edit asked about in the folder left was answered: {Text(refused)}");
            Assert.Equal(("notes\n", "notes\n"), (File.ReadAllText(Path.Combine(first, "Notes.txt")), File.ReadAllText(Path.Combine(second, "Notes.txt"))));

            Task<JsonElement> again = Waiting.Apart(() => Call(client, McpTools.Edit, Note("opened")));
            Press(Question(window, "the question for the agent's edit in the folder opened"), AgentToolDialog.AllowForSession);
            Await(again, "the agent's edit in the folder opened");
            Assert.Equal(("notes\n", "notes opened\n"), (File.ReadAllText(Path.Combine(first, "Notes.txt")), File.ReadAllText(Path.Combine(second, "Notes.txt"))));
        }
        finally
        {
            window.ApplySettings(window.Settings with { McpServer = false });
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's questions come over the window without the keys and no key answers Allow: an Enter the person types
    /// in the editor goes into their file, and one pressed in the question itself leaves it waiting, the agent not let in and
    /// its edit not made, until a click of Allow answers each.</summary>
    private void An_agents_question_leaves_the_keys_in_the_editor_and_an_Enter_never_allows()
    {
        string folder = Plain("mcp-enter-");
        string typed = TestFolders.Write(folder, "Typed.txt", "typed\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(typed);
        Ui.Settle();
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "typed");
        editor.Focus();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> editing = Waiting.Apart(() => Call(client, McpTools.Edit, Note("allowed")));
            AgentAdmissionDialog admission = Admission(window, "the window to ask whether the agent may work there");
            EnterLeavesWaiting(window, editor, admission, editing, "typed\n\n");
            Assert.Equal(0, window.Agents);
            Press(admission, AgentAdmissionDialog.Allow);

            Window asked = DialogButtonsTests.Check(Question(window, "the question for the agent's edit"));
            EnterLeavesWaiting(window, editor, asked, editing, "typed\n\n\n");
            Assert.Equal("notes\n", File.ReadAllText(Path.Combine(folder, "Notes.txt")));
            Press(asked, AgentToolDialog.AllowForSession);
            Await(editing, "the agent's edit, allowed");
            Assert.Equal("notes allowed\n", File.ReadAllText(Path.Combine(folder, "Notes.txt")));
            Assert.True(ReferenceEquals(editor, window.FocusManager?.GetFocusedElement()),
                $"Once the person answered, the keys are with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not back in the editor");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A call of a tool that changes something is the person's to allow once or for the session: Allow Once, the
    /// accent's answer that no key gives, lets the call its question shows go on alone, another call of the tool waiting with
    /// it asked its own with its own arguments, and the next edit asked again; Allow for This Session lets every later edit go
    /// on unasked. app_click's input to Passenger's own windows (app ide) is asked apart, though app_click is allowed for the
    /// session, so no allowance for an app's input drives the person's IDE.</summary>
    private void An_agents_edit_allowed_once_is_asked_again_at_its_next_and_one_allowed_for_the_session_is_not_its_input_to_passenger_asked_apart()
    {
        string folder = Plain("mcp-once-");
        string more = TestFolders.Write(folder, "More.txt", "more\n");
        string later = TestFolders.Write(folder, "Later.txt", "later\n");
        string notes = Path.Combine(folder, "Notes.txt");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            // Two edits at once: the question shows one call, Allow Once lets that one alone go on, and the other asks its own.
            Task<CallToolResult> editingNotes = Waiting.Apart(() => Result(client, McpTools.Edit, new() { ["path"] = "Notes.txt", ["oldText"] = "notes", ["newText"] = "allowed" }));
            Task<CallToolResult> editingMore = Waiting.Apart(() => Result(client, McpTools.Edit, new() { ["path"] = "More.txt", ["oldText"] = "more", ["newText"] = "allowed" }));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            AgentToolDialog first = DialogButtonsTests.Check(Assert.IsType<AgentToolDialog>(Question(window, "the question for the agent's first edit")));
            Button[] answers = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(first).OfType<Button>().Where(b => b.Content is string)];
            string[] order = OperatingSystem.IsMacOS() ? [AgentToolDialog.Deny, AgentToolDialog.AllowForSession, AgentToolDialog.AllowOnce]
                : [AgentToolDialog.AllowOnce, AgentToolDialog.AllowForSession, AgentToolDialog.Deny];
            Assert.Equal(order, answers.Select(b => (string)b.Content!));
            Assert.True(answers.Single(b => b.Classes.Contains(StyleClasses.Primary)).Content is AgentToolDialog.AllowOnce && !answers.Any(b => b.IsDefault),
                "Allow Once is not the question's one accented answer, or one of its answers is Enter's");
            bool notesFirst = Texts(first).Any(t => t.Contains("path: Notes.txt", StringComparison.Ordinal));
            Assert.True(notesFirst || Texts(first).Any(t => t.Contains("path: More.txt", StringComparison.Ordinal)),
                $"The question shows neither edit's arguments: {string.Join(" | ", Texts(first))}");
            Press(first, AgentToolDialog.AllowOnce);
            (Task<CallToolResult> allowed, Task<CallToolResult> waited, string other) = notesFirst
                ? (editingNotes, editingMore, "path: More.txt")
                : (editingMore, editingNotes, "path: Notes.txt");
            CallToolResult once = Await(allowed, "the edit allowed once");
            Assert.False(once.IsError == true, $"The edit allowed once was refused: {Text(once)}");
            Window second = Question(window, "the question for the other edit, which waited with the first");
            Assert.True(Texts(second).Any(t => t.Contains(other, StringComparison.Ordinal)), $"The other edit's question said: {string.Join(" | ", Texts(second))}");
            Assert.False(waited.IsCompleted, "The edit that waited with the one allowed once went on without its own question");
            Press(second, AgentToolDialog.Deny);
            Assert.Contains("did not allow edit", Text(Await(waited, "the edit denied")), StringComparison.Ordinal);
            Assert.Equal(notesFirst ? ("allowed\n", "more\n") : ("notes\n", "allowed\n"), (File.ReadAllText(notes), File.ReadAllText(more)));

            Task<CallToolResult> again = Waiting.Apart(() => Result(client, McpTools.Edit, new() { ["path"] = "Later.txt", ["oldText"] = "later", ["newText"] = "session" }));
            Window asking = Question(window, "the question for the agent's next edit, as the one before was allowed once");
            Assert.Contains("Allow edit?", Texts(asking));
            Press(asking, AgentToolDialog.AllowForSession);
            CallToolResult session = Await(again, "the edit allowed for the session");
            Assert.False(session.IsError == true, $"The edit allowed for the session was refused: {Text(session)}");
            Unasked(window, client, McpTools.Edit, new() { ["path"] = "Later.txt", ["oldText"] = "session", ["newText"] = "unasked" });
            Assert.Equal("unasked\n", File.ReadAllText(later));

            // app_click on an app, allowed for the session, answers that none runs; its input to Passenger's own windows asks apart.
            Task<CallToolResult> clicking = Waiting.Apart(() => Result(client, McpTools.AppClick, new() { ["x"] = 10, ["y"] = 10 }));
            Window app = Question(window, "the question for the agent's click in an app");
            Assert.Contains("Allow app_click?", Texts(app));
            Press(app, AgentToolDialog.AllowForSession);
            Assert.Contains("No Avalonia, WPF or MAUI app", Text(Await(clicking, "the click in an app")), StringComparison.Ordinal);
            Task<CallToolResult> clickingOwn = Waiting.Apart(() => Result(client, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "#RunConfigButton" }));
            Window own = Question(window, "the question for the agent's click in Passenger's own windows");
            string[] said = Texts(own);
            Assert.True(said.Contains("Allow app_click in Passenger's windows?") && said.Any(t => t.Contains("the app being Passenger's own windows", StringComparison.Ordinal))
                && said.Any(t => t.Contains("app: ide", StringComparison.Ordinal)),
                $"The question for the agent's click in Passenger's own windows said: {string.Join(" | ", said)}");
            Press(own, AgentToolDialog.Deny);
            CallToolResult refused = Await(clickingOwn, "the click in Passenger's own windows the person denied");
            Assert.True(refused.IsError == true && Text(refused).Contains("did not allow app_click in Passenger's own windows (app ide)", StringComparison.Ordinal),
                $"The agent's click in Passenger's own windows, denied, was answered: {Text(refused)}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's request_full_access asks once for every tool that changes something, naming what they do and
    /// listing them, its one Allow accented and no key answering it: allowed for the session, the edit whose question waited
    /// goes on, and an edit, a build and a click in an app go on unasked, until the agent opens another
    /// folder, where each tool asks again; denied there, a later request is refused unasked while an edit still asks at its
    /// call. The agent's own Passenger answers that nothing asks.</summary>
    private void An_agents_request_for_every_tool_lets_each_go_on_unasked_once_allowed_and_denied_holds_for_the_session()
    {
        string folder = Plain("mcp-every-");
        string other = Plain("mcp-every-other-");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> waiting = Waiting.Apart(() => Result(client, McpTools.Edit, Note("waited")));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            Window edit = Question(window, "the question for the agent's edit");
            Task<CallToolResult> requesting = Waiting.Apart(() => Result(client, McpTools.RequestFullAccess));
            WaitFor(() => EveryTool(window) is not null, "the question for every tool");
            AgentToolDialog asked = DialogButtonsTests.Check(EveryTool(window)!);
            string[] said = Texts(asked);
            Assert.True(said.Contains(AgentToolDialog.AllowEveryTool) && said.Any(t => t.StartsWith("Test Agent, connected through Passenger's MCP server, asks to use the tools below", StringComparison.Ordinal)
                && t.Contains("click or type in the apps it runs and in Passenger's own windows", StringComparison.Ordinal)),
                $"The question for every tool said: {string.Join(" | ", said)}");
            string[] listed = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(asked).OfType<SelectableTextBlock>().Single().Text!.Split(", ");
            Assert.True(((string[])[McpTools.Edit, McpTools.Build, McpTools.AppClick, McpTools.VcsCommit, McpTools.DebugEvaluate, McpTools.TerminalRun]).All(listed.Contains)
                && !((string[])[McpTools.Workspace, McpTools.SaveAll, McpTools.Stop, McpTools.DebugStep, McpTools.Open, McpTools.RequestFullAccess]).Any(listed.Contains),
                $"The question for every tool lists the tools that ask otherwise than they do: {string.Join(", ", listed)}");
            Button[] answers = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(asked).OfType<Button>().Where(b => b.Content is string)];
            string[] order = OperatingSystem.IsMacOS() ? [AgentToolDialog.Deny, AgentToolDialog.AllowForSession] : [AgentToolDialog.AllowForSession, AgentToolDialog.Deny];
            Assert.Equal(order, answers.Select(b => (string)b.Content!));
            Assert.True(answers.Single(b => b.Classes.Contains(StyleClasses.Primary)).Content is AgentToolDialog.AllowForSession && !answers.Any(b => b.IsDefault),
                "Allow for This Session is not the question's one accented answer, or one of its answers is Enter's");
            Ui.Click(window.FindControl<Button>("AgentButton")!);
            Assert.True(asked.IsActive, "Choosing the status bar's words left the question for every tool, the last asked, behind");
            Ui.Press(asked, "enter");
            Assert.True(asked.IsVisible && !requesting.IsCompleted, "An Enter in the question for every tool answered it");

            Press(asked, AgentToolDialog.AllowForSession);
            CallToolResult allowed = Await(requesting, "the request for every tool, allowed");
            Assert.True(allowed.IsError != true && Text(allowed).Contains("allowed every tool", StringComparison.Ordinal), $"The request for every tool, allowed, was answered: {Text(allowed)}");
            CallToolResult waited = Await(waiting, "the edit whose question waited as every tool was allowed");
            Assert.True(waited.IsError != true && !edit.IsVisible, $"The edit asked about before every tool was allowed was answered: {Text(waited)}");
            Unasked(window, client, McpTools.Edit, Note("unasked"));
            Assert.Equal("notes unasked waited\n", File.ReadAllText(Path.Combine(folder, "Notes.txt")));
            Unasked(window, client, McpTools.Build);
            // In an app, none running: no test but McpAvaloniaAppTests' inspects Passenger's own windows in the test process.
            string clicked = Text(UnaskedResult(window, client, McpTools.AppClick, new() { ["x"] = 10, ["y"] = 10 }));
            Assert.Contains("No Avalonia, WPF or MAUI app", clicked, StringComparison.Ordinal);
            Assert.Contains("allowed already", Text(UnaskedResult(window, client, McpTools.RequestFullAccess)), StringComparison.Ordinal);

            // The agent's own open, still the person's to allow, leaves every tool asking again in the folder it opens.
            Task<JsonElement> opening = Waiting.Apart(() => Call(client, McpTools.Open, new() { ["path"] = other }));
            Press(Question(window, "the question for the agent's open"), AgentAdmissionDialog.Allow);
            Assert.Equal(PathNames.Canonical(other), Await(opening, "the agent's open the person allowed").GetProperty("folder").GetString());
            Task<CallToolResult> editingOther = Waiting.Apart(() => Result(client, McpTools.Edit, Note("other")));
            Window again = Question(window, "the question for the agent's edit in the folder it opened");
            Assert.Contains("Allow edit?", Texts(again));
            Press(again, AgentToolDialog.Deny);
            Assert.Contains("did not allow edit", Text(Await(editingOther, "the edit denied in the folder opened")), StringComparison.Ordinal);

            Task<CallToolResult> requestingThere = Waiting.Apart(() => Result(client, McpTools.RequestFullAccess));
            Window everyThere = Question(window, "the question for every tool in the folder the agent opened");
            Assert.Contains(AgentToolDialog.AllowEveryTool, Texts(everyThere));
            Press(everyThere, AgentToolDialog.Deny);
            CallToolResult denied = Await(requestingThere, "the request for every tool, denied");
            Assert.True(denied.IsError == true && Text(denied).Contains("denied every tool at once", StringComparison.Ordinal), $"The request for every tool, denied, was answered: {Text(denied)}");
            CallToolResult refused = UnaskedResult(window, client, McpTools.RequestFullAccess);
            Assert.True(refused.IsError == true && Text(refused).Contains("not asked again", StringComparison.Ordinal), $"The request after every tool was denied was answered: {Text(refused)}");
            Task<CallToolResult> stillAsked = Waiting.Apart(() => Result(client, McpTools.Edit, Note("asked")));
            Window asking = Question(window, "the question for the agent's edit after every tool was denied");
            Assert.Contains("Allow edit?", Texts(asking));
            Press(asking, AgentToolDialog.AllowOnce);
            CallToolResult once = Await(stillAsked, "the edit allowed once after every tool was denied");
            Assert.False(once.IsError == true, $"The edit allowed once was refused: {Text(once)}");
            Assert.Equal("notes asked\n", File.ReadAllText(Path.Combine(other, "Notes.txt")));
        }
        finally
        {
            Gone(client);
            window.Close();
        }

        // The agent's own Passenger has nobody to ask.
        McpClient own = Await(Waiting.Apart(() => Start(["--mcp", folder], languageServer: false)), "the agent's own Passenger starting");
        try
        {
            Assert.Equal(AgentConnection.NothingAsks, Await(Waiting.Apart(() => Call(own, McpTools.RequestFullAccess)), "the request on the agent's own Passenger").GetString());
        }
        finally
        {
            Gone(own);
        }

        static AgentToolDialog? EveryTool(MainWindow window) =>
            window.OwnedWindows.OfType<AgentToolDialog>().FirstOrDefault(d => d.IsVisible && d.Title == AgentToolDialog.AllowEveryTool);
    }

    /// <summary>An agent's ide_action shows what it shows without the person's keys or a window brought forward: Problems
    /// shown for it leaves an Enter the person types going into their file, and its Focus Editor, whose whole work is to
    /// move the keys, is refused with a floating tool window in front, which stays there.</summary>
    private void An_agents_ide_action_shows_a_tool_window_without_the_persons_keys_and_brings_no_window_forward()
    {
        string folder = Plain("mcp-action-keys-");
        string typed = TestFolders.Write(folder, "Typed.txt", "typed\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ApplySettings(window.Settings with { McpServer = true });
        window.OpenFile(typed);
        Ui.Settle();
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "typed");
        editor.Focus();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Asked(window, client, McpTools.IdeAction, "run an IDE action", new() { ["action"] = AppActions.ShowProblems });
            Ui.Settle();
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems), "The agent's ide_action of Problems did not show the Problems window");
            Ui.Press(window, "enter");
            Assert.True(editor.Document.Snapshot.ToString() == "typed\n\n",
                $"After the agent showed Problems, an Enter the person typed went to {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not into their file");

            List<Window> floats = [];
            window.DockHost.WindowOpened += (_, opened) => floats.Add(opened);
            window.DockHost.Show(ToolWindowIds.Structure);
            window.DockHost.SetMode(window.DockHost[ToolWindowIds.Structure], Passenger.App.Docking.ToolMode.Floating);
            Ui.Settle();
            Window structure = Assert.Single(floats);
            Ui.Leave(window);
            structure.Activate();
            Ui.Settle();
            Assert.True(structure.IsActive && !window.IsActive, $"The floating Structure window did not come in front of the IDE's window: it is {(structure.IsActive ? "active" : "inactive")}, the IDE's {(window.IsActive ? "active" : "inactive")}");
            CallToolResult focusing = Await(Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.FocusEditor })), "the agent's Focus Editor");
            Ui.Settle();
            Assert.True(structure.IsActive && !window.IsActive,
                $"The agent's Focus Editor left the IDE's window {(window.IsActive ? "active" : "inactive")} and the floating Structure window {(structure.IsActive ? "active" : "inactive")}");
            Assert.True(focusing.IsError == true && Text(focusing).Contains(Refusals.PersonsKeys, StringComparison.Ordinal),
                $"The agent's Focus Editor was answered: {Text(focusing)}");
            string listed = Assert.Single(Await(Waiting.Apart(() => Call(client, McpTools.IdeActions)), "the agent's list of actions").EnumerateArray()
                .Select(a => a.GetString()!), a => a.StartsWith($"{AppActions.FocusEditor}: ", StringComparison.Ordinal));
            Assert.True(listed.EndsWith($" - cannot run: {Refusals.PersonsKeys}", StringComparison.Ordinal), $"ide_actions lists Focus Editor for the agent as {listed}");
        }
        finally
        {
            window.ApplySettings(window.Settings with { McpServer = false });
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's ide_action of an action that asks the person in a popup (Go to File) or a dialog (Edit
    /// Configurations) is refused saying why, opening neither, so an Enter the person types still goes into their file; and
    /// the Build window, floating, shows for the agent's build behind what the person is in, activating no window.</summary>
    private void An_agents_ide_action_of_a_popup_or_dialog_is_refused_and_its_build_shows_the_Build_window_behind()
    {
        string typed = TestFolders.Write(Folder, "Typed.txt", "typed\n");
        MainWindow window = Ui.OpenFolder(Folder, languageServer: false);
        window.ApplySettings(window.Settings with { McpServer = true });
        window.OpenFile(typed);
        Ui.Settle();
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "typed");
        editor.Focus();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Asked(window, client, McpTools.IdeAction, "run an IDE action", new() { ["action"] = AppActions.ShowProblems });
            Ui.Settle();
            foreach (string action in (string[])[AppActions.GoToFile, AppActions.EditConfigurations])
            {
                int owned = window.OwnedWindows.Count;
                CallToolResult refused = Await(Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = action })), $"the agent's {action}");
                Ui.Settle();
                Assert.True(refused.IsError == true && Text(refused).Contains(Refusals.AsksThePerson, StringComparison.Ordinal),
                    $"The agent's {action} in the person's window was answered: {Text(refused)}");
                Assert.True(!window.IsPopupOpen && window.OwnedWindows.Count == owned,
                    $"The agent's {action} opened {(window.IsPopupOpen ? window.Popup?.GetType().Name : window.OwnedWindows.LastOrDefault()?.GetType().Name)} over the person's work");
            }
            Ui.Press(window, "enter");
            Assert.True(editor.Document.Snapshot.ToString() == "typed\n\n",
                $"After the agent's Go to File and Edit Configurations, an Enter the person typed went to {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not into their file");

            Asked(window, client, McpTools.Build, "build");
            window.DockHost.SetMode(window.DockHost[ToolWindowIds.Build], Passenger.App.Docking.ToolMode.Floating);
            window.DockHost.Hide(ToolWindowIds.Build);
            Ui.Settle();
            List<Window> floats = [];
            window.DockHost.WindowOpened += (_, opened) => floats.Add(opened);
            Ui.Leave(window);
            Assert.False(window.IsActive, "The IDE's window is still active once the person went elsewhere");
            JsonElement built = Await(Waiting.Apart(() => Call(client, McpTools.Build)), "the agent's second build");
            Ui.Settle();
            Window build = Assert.Single(floats);
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Build) && build.IsVisible, $"The agent's build did not show the floating Build window: {built}");
            Assert.True(!build.IsActive && !window.IsActive,
                $"The agent's build activated {(build.IsActive ? "the floating Build window" : "the IDE's window")}, bringing it in front of what the person was in");
        }
        finally
        {
            window.ApplySettings(window.Settings with { McpServer = false });
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's ide_action of what would open a list, a menu, an inline box or a dialog over the person's code (Show
    /// Context Actions, Find, Rename, Show Context Menu, Surround With, Safe Delete of a local still used) is refused: nothing
    /// opens over the person's file, which keeps the keys and its caret, and an Enter they type goes into it; the person's
    /// own Show Context Actions there lists the fixes the agent's did not.</summary>
    private void An_agents_ide_action_opens_no_list_menu_box_or_dialog_over_the_persons_code_and_leaves_their_keys()
    {
        MainWindow window = AgentsWindow(Folder);
        string program = Path.Combine(Folder, "Program.cs");
        window.OpenFile(program);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the project ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int tot");
        editor.Focus();
        string before = editor.Document.Snapshot.ToString();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Asked(window, client, McpTools.IdeAction, "run an IDE action", new() { ["action"] = AppActions.ShowProblems });
            Ui.Settle();
            foreach (string action in (string[])[AppActions.QuickFix, AppActions.Find, AppActions.Rename, AppActions.ShowContextMenu, AppActions.SurroundWith, AppActions.SafeDelete])
            {
                int owned = window.OwnedWindows.Count;
                CallToolResult answered = Await(Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = action })), $"the agent's {action}");
                Ui.Settle();
                string said = Text(answered);
                Assert.True(answered.IsError == true ? said.Contains(Refusals.AsksThePerson, StringComparison.Ordinal) : said == $"Ran {action}",
                    $"The agent's {action} in the person's window was answered: {said}");
                string? opened = window.IsPopupOpen ? window.Popup?.GetType().Name
                    : window.EditorArea.FindBar.IsVisible ? "the find bar"
                    : window.OpenContextMenu is not null ? "a context menu"
                    : window.Assist.Offered.Count > 0 ? $"the list of {window.Assist.OfferedTitle}"
                    : window.Assist.Renaming is not null ? "the rename box"
                    : window.Assist.IsCompletionOpen ? "the completion list"
                    : window.OwnedWindows.Count != owned ? window.OwnedWindows.LastOrDefault()?.GetType().Name
                    : null;
                Assert.True(opened is null, $"The agent's {action} opened {opened} over the person's work");
                Assert.True(ReferenceEquals(editor, window.FocusManager?.GetFocusedElement()),
                    $"After the agent's {action}, the keys are with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not the person's file");
                Assert.True(editor.Document.Snapshot.ToString() == before && editor.Selections.Primary is { IsEmpty: true, Caret: 7 },
                    $"The agent's {action} changed the person's file or moved their caret to {editor.Selections.Primary}:\n{editor.Document.Snapshot}");
            }
            int lines = editor.Document.Snapshot.LineCount;
            Ui.Press(window, "enter");
            Assert.True(editor.Document.Snapshot.LineCount == lines + 1 && editor.CaretPosition.Line == 1,
                $"After the agent's actions, an Enter the person typed went to {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not into their file:\n{editor.Document.Snapshot}");

            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Ui.PlaceAfter(editor, "int tot");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the person's own Show Context Actions to list the fixes the agent's did not");
            Ui.Press(window, "escape");
        }
        finally
        {
            window.ApplySettings(window.Settings with { McpServer = false });
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A file an agent opens or edits on the person's window comes in a tab behind theirs: with the keys in their file,
    /// the agent's open of a closed file, and its edit of that file once its tab is closed again, each leave the person's tab
    /// shown and an Enter they type going into it, the agent's file's tab there behind.</summary>
    private void An_agents_open_or_edit_of_a_closed_file_gives_it_a_tab_behind_the_persons_which_keeps_the_keys()
    {
        string folder = Plain("mcp-behind-");
        string typed = TestFolders.Write(folder, "Typed.txt", "typed\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ApplySettings(window.Settings with { McpServer = true });
        window.OpenFile(typed);
        Ui.Settle();
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "typed");
        editor.Focus();
        Passenger.App.Workspace.OpenDocument persons = window.EditorArea.Active!;
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            string expected = "typed\n";
            void LeftBehind(string what)
            {
                Ui.Settle();
                Passenger.App.Workspace.OpenDocument? notes = window.EditorArea.Documents.FirstOrDefault(d => d.Name == "Notes.txt");
                Assert.True(window.EditorArea.Active == persons && ReferenceEquals(editor, window.FocusManager?.GetFocusedElement()),
                    $"The agent's {what} showed {window.EditorArea.Active?.Name ?? "nothing"}, the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, in place of the person's Typed.txt");
                Assert.True(notes is not null && window.EditorArea.HasTab(notes), $"The agent's {what} left Notes.txt with no tab");
                Ui.Press(window, "enter");
                expected += "\n";
                Assert.True(editor.Document.Snapshot.ToString() == expected,
                    $"After the agent's {what}, an Enter the person typed went to {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not into their file: {editor.Document.Snapshot}");
            }

            Await(Waiting.Apart(() => Call(client, McpTools.Open, new() { ["path"] = "Notes.txt", ["line"] = 1, ["column"] = 3 })), "the agent's open");
            LeftBehind("open");
            Assert.True(window.EditorArea.Close(window.EditorArea.Documents.Single(d => d.Name == "Notes.txt")), "Notes.txt's tab did not close");
            Ui.Settle();
            Asked(window, client, McpTools.Edit, "edit a file", Note("behind"));
            LeftBehind("edit");
            Assert.Equal("notes behind\n", File.ReadAllText(Path.Combine(folder, "Notes.txt")));
        }
        finally
        {
            window.ApplySettings(window.Settings with { McpServer = false });
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A question shown without the keys: an Enter in the window goes to the editor; chosen from the status bar's
    /// words, the question takes the keys, and an Enter there answers nothing, the agent's call still waiting.</summary>
    private void EnterLeavesWaiting(MainWindow window, Passenger.Editor.CodeEditor editor, Window question, Task waiting, string afterEnter)
    {
        Assert.False(question.IsActive, $"\"{question.Title}\" took the keys from the editor as it came");
        Assert.True(ReferenceEquals(editor, window.FocusManager?.GetFocusedElement()),
            $"With \"{question.Title}\" asked, the keys are with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not the editor the person typed in");
        Ui.Press(window, "enter");
        Assert.Equal(afterEnter, editor.Document.Snapshot.ToString());

        Ui.Click(window.FindControl<Avalonia.Controls.Button>("AgentButton")!);
        Assert.True(question.IsActive && !editor.IsKeyboardFocusWithin,
            $"Choosing the status bar's words on \"{question.Title}\" left it {(question.IsActive ? "active" : "inactive")}, the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}");
        Ui.Press(question, "enter");
        Assert.True(question.IsVisible, $"An Enter in \"{question.Title}\" answered it");
        Assert.False(waiting.IsCompleted, $"An Enter in \"{question.Title}\" let the agent's call go on");
        Assert.Equal(afterEnter, editor.Document.Snapshot.ToString());
    }

    /// <summary>With Enable MCP Server on, a window keeps its agents as the person opens another folder there, but an agent
    /// set up in the first goes on in it: --mcp-attach joins a window on its own folder again (the running Passenger opening
    /// one), never the window now on the other.</summary>
    [Gate]
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
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", own])), "the agent attaching");
        try
        {
            Assert.Equal(PathNames.Canonical(own), Await(Waiting.Apart(() => Call(client, McpTools.Workspace)), "the agent's first call").GetProperty("folder").GetString());
            window.OpenFolder(other);
            Ui.Settle();
            JsonElement after = Await(Waiting.Apart(() => Call(client, McpTools.Workspace)), "the agent's call after its window opened another folder");
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
    [Gate]
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
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", kept])), "the agent attaching");
        try
        {
            dialog.Page = SettingsDialog.McpPage;
            Ui.Settle();
            Assert.Equal([PathNames.Canonical(taken)], dialog.AgentFolderList.Items.OfType<string>());

            Task<JsonElement> letIn = Waiting.Apart(() => Call(client, McpTools.Workspace));
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
