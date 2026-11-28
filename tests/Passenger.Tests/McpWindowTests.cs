using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Mobile;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Agents joining the window a person is using (<c>--mcp-attach</c>): what the window asks them before
/// an agent's first change, what it shows of their work, and what each tool answers there.</summary>
[AboutMacOS]
public sealed class McpWindowTests : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpWindowTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    /// <summary>The gate's answers of an attached agent as one flow: an answer its operation's outcome whatever the
    /// status bar says, an action none has or one that cannot run an error saying why, the person's run configuration
    /// saved as a launch profile, a call meeting a fault of the IDE's answered with it, and vcs_log with a path
    /// following renames, each step the gate test it names in an instance of its own.</summary>
    [Gate]
    [AvaloniaFact]
    public void An_attached_agents_answers_and_refusals_are_its_operations_own_and_a_fault_of_the_IDEs_said_and_logged()
    {
        Flow.StepApart("an agents answer is its operations outcome whatever the status bar says meanwhile", () => new McpWindowTests(restored), t => t.An_agents_answer_is_its_operations_outcome_whatever_the_status_bar_says_meanwhile());
        Flow.StepApart("an agents action no action has or one that cannot run here is an error saying why", () => new McpWindowTests(restored), t => t.An_agents_action_no_action_has_or_one_that_cannot_run_here_is_an_error_saying_why());
        Flow.StepApart("an agent saves the persons run configuration as a launch profile and is told why not for a generated one", () => new McpWindowTests(restored), t => t.An_agent_saves_the_persons_run_configuration_as_a_launch_profile_and_is_told_why_not_for_a_generated_one());
        Flow.StepApart("an agents call that meets a fault of the IDEs is answered with it and logged", () => new McpWindowTests(restored), t => t.An_agents_call_that_meets_a_fault_of_the_IDEs_is_answered_with_it_and_logged());
        Flow.StepApart("an agents vcs log with a path lists the commits show history does renames followed", () => new McpWindowTests(restored), t => t.An_agents_vcs_log_with_a_path_lists_the_commits_show_history_does_renames_followed());
    }

    /// <summary>The gate's attach as one flow: a breakpoint an attached agent sets seen by the person, the MCP server
    /// off asking to let an agent in and on and off again, an agent asked before its first run with the window ceasing
    /// to wait once it gives up or goes, and each solution window's own socket, each step the gate test it names in an
    /// instance of its own.</summary>
    [Gate]
    [AvaloniaFact]
    public void An_agent_attaches_to_its_folders_window_only_as_the_person_lets_it_asking_before_it_first_runs()
    {
        Flow.StepApart("an agent attached to the window sets a breakpoint the person sees", () => new McpWindowTests(restored), t => t.An_agent_attached_to_the_window_sets_a_breakpoint_the_person_sees());
        Flow.StepApart("with the MCP server off the window asks to let an agent in on it lets agents in unasked and off again shuts them out", () => new McpWindowTests(restored), t => t.With_the_MCP_server_off_the_window_asks_to_let_an_agent_in_on_it_lets_agents_in_unasked_and_off_again_shuts_them_out());
        Flow.StepApart("an agent asks before its first run and the window stops waiting when it gives up or goes", () => new McpWindowTests(restored), t => t.An_agent_asks_before_its_first_run_and_the_window_stops_waiting_when_it_gives_up_or_goes());
        Flow.StepApart("each solution window has its own socket and an agent attaches to the one over its folder", () => new McpWindowTests(restored), t => t.Each_solution_window_has_its_own_socket_and_an_agent_attaches_to_the_one_over_its_folder());
    }

    /// <summary>An attached agent's work in the person's window as one flow, each step one test's check in an instance
    /// of its own: a diff tab named a diff of its file, a package change as the NuGet window's work waiting its turn, a
    /// save the person makes while the agent's replace writes kept as theirs, the NuGet window left unbuilt, a push
    /// asking the person for a login, and a terminal command in a shell of its own that the person's program never
    /// takes.</summary>
    [AvaloniaFact]
    public void An_agents_work_shows_in_the_persons_window_as_its_own_never_taking_the_persons_saves_or_programs()
    {
        Flow.StepApart("an agent sees a diff tab in the persons window named as a diff of its file", () => new McpWindowTests(restored), t => t.An_agent_sees_a_diff_tab_in_the_persons_window_named_as_a_diff_of_its_file());
        Flow.StepApart("an agents package change shows as the nuget windows work and waits its turn behind the windows own", () => new McpWindowTests(restored), t => t.An_agents_package_change_shows_as_the_nuget_windows_work_and_waits_its_turn_behind_the_windows_own());
        Flow.StepApart("a save the person makes while an agents replace writes is their own change in local history", () => new McpWindowTests(restored), t => t.A_save_the_person_makes_while_an_agents_replace_writes_is_their_own_change_in_local_history());
        Flow.StepApart("an agents package search and install leave the nuget window unbuilt until the person opens it", () => new McpWindowTests(restored), t => t.An_agents_package_search_and_install_leave_the_nuget_window_unbuilt_until_the_person_opens_it());
        Flow.StepApart("an agents push to a remote that asks for a login asks the person at the window", () => new McpWindowTests(restored), t => t.An_agents_push_to_a_remote_that_asks_for_a_login_asks_the_person_at_the_window());
        Flow.StepApart("an agents terminal command runs in a shell of its own and never reaches the persons program", () => new McpWindowTests(restored), t => t.An_agents_terminal_command_runs_in_a_shell_of_its_own_and_never_reaches_the_persons_program());
        Flow.StepApart("a persons program in the terminal never takes an agents command and the window keeps one agent shell", () => new McpWindowTests(restored), t => t.A_persons_program_in_the_terminal_never_takes_an_agents_command_and_the_window_keeps_one_agent_shell());
    }

    /// <summary>What an attached agent is refused as one flow, each step one test's check in an instance of its own:
    /// what would run a folder's code before it is trusted, trusting it through the person's window, committing,
    /// stashing or checking out a file stored through a git filter there, quitting the person's Passenger, and closing
    /// their solution though every tool is allowed, each refusal told why.</summary>
    [AvaloniaFact]
    public void An_attached_agent_never_does_what_the_person_alone_may_and_runs_nothing_of_a_folder_not_trusted()
    {
        Flow.StepApart("an agent in a window whose folder is not trusted is refused what would run its code and cannot trust it", () => new McpWindowTests(restored), t => t.An_agent_in_a_window_whose_folder_is_not_trusted_is_refused_what_would_run_its_code_and_cannot_trust_it());
        Flow.StepApart("an agents input into the persons window never trusts its folder and is told why", () => new McpWindowTests(restored), t => t.An_agents_input_into_the_persons_window_never_trusts_its_folder_and_is_told_why());
        Flow.StepApart("an agents commit in a folder not trusted leaves out a file stored through a git filter saying why", () => new McpWindowTests(restored), t => t.An_agents_commit_in_a_folder_not_trusted_leaves_out_a_file_stored_through_a_git_filter_saying_why());
        Flow.StepApart("an agents stash and checkout in a folder not trusted are refused where git would write out a file stored through a filter", () => new McpWindowTests(restored), t => t.An_agents_stash_and_checkout_in_a_folder_not_trusted_are_refused_where_git_would_write_out_a_file_stored_through_a_filter());
        Flow.StepApart("an attached agent never quits the persons passenger and is told why", () => new McpWindowTests(restored), t => t.An_attached_agent_never_quits_the_persons_passenger_and_is_told_why());
        Flow.StepApart("an attached agent allowed every tool never closes the persons solution and is told why", () => new McpWindowTests(restored), t => t.An_attached_agent_allowed_every_tool_never_closes_the_persons_solution_and_is_told_why());
    }

    /// <summary>An agent's code questions while the server loads as one flow, each step one test's check in an instance
    /// of its own: a file come into a project that did not load said why, find_symbol answering without a file still
    /// loading and naming it, Reformat of a file not taken in refused naming it, a MAUI expression's rename waiting for
    /// its moved file, a compose file's definition waiting for the YAML server, and a definition asked while the server
    /// loads again refused past the wait.</summary>
    [AvaloniaFact]
    public void An_agents_code_question_waits_for_what_its_file_needs_loaded_and_is_refused_saying_what_past_the_wait()
    {
        Flow.StepApart("an agent asking of a file come into a project that did not load hears why while the project loads again", () => new McpWindowTests(restored), t => t.An_agent_asking_of_a_file_come_into_a_project_that_did_not_load_hears_why_while_the_project_loads_again());
        Flow.StepApart("an agents find symbol answers without a file still loading naming it while a tool of its project waits for it", () => new McpWindowTests(restored), t => t.An_agents_find_symbol_answers_without_a_file_still_loading_naming_it_while_a_tool_of_its_project_waits_for_it());
        Flow.StepApart("an agents Reformat Code of a file the server has not taken in yet is refused naming it", () => new McpWindowTests(restored), t => t.An_agents_Reformat_Code_of_a_file_the_server_has_not_taken_in_yet_is_refused_naming_it());
        Flow.StepApart("an agents rename in a maui expression asked as a type rename moved its file waits for the server to take the file in", () => new McpWindowTests(restored), t => t.An_agents_rename_in_a_maui_expression_asked_as_a_type_rename_moved_its_file_waits_for_the_server_to_take_the_file_in());
        Flow.StepApart("an agents definition on a compose file waits saying so for the yaml server to start and past the wait is refused as still starting", () => new McpWindowTests(restored), t => t.An_agents_definition_on_a_compose_file_waits_saying_so_for_the_yaml_server_to_start_and_past_the_wait_is_refused_as_still_starting());
        Flow.StepApart("an agents definition asked while the server loads again past the wait is refused as still loading", () => new McpWindowTests(restored), t => t.An_agents_definition_asked_while_the_server_loads_again_past_the_wait_is_refused_as_still_loading());
    }

    /// <summary>An agent's questions of a restore's and a generator's code as one flow, each step one test's check in
    /// an instance of its own: a restored generator's declaration waiting for its run past the server's word that
    /// diagnostics moved, for the server to read what the build wrote again, with no watcher telling it, refused where
    /// the server never reads it, and to the end of its project's load batch, a restore's code waiting for its
    /// project's load again, and a restore that left its assets as they were waiting for none.</summary>
    [AvaloniaFact]
    public void An_agents_definition_of_generated_or_restored_code_waits_for_the_servers_rerun_and_load_never_answering_as_before()
    {
        Flow.StepApart("an agents definition of what a restored generator declares waits for its run though the server says its diagnostics moved first", () => new McpWindowTests(restored), t => t.An_agents_definition_of_what_a_restored_generator_declares_waits_for_its_run_though_the_server_says_its_diagnostics_moved_first());
        Flow.StepApart("an agents definition of what a restored generator declares waits for the server to read what its build wrote again", () => new McpWindowTests(restored), t => t.An_agents_definition_of_what_a_restored_generator_declares_waits_for_the_server_to_read_what_its_build_wrote_again());
        Flow.StepApart("an agents definition of what a restored generator declares goes to it though no watcher tells the server what the build wrote again", () => new McpWindowTests(restored), t => t.An_agents_definition_of_what_a_restored_generator_declares_goes_to_it_though_no_watcher_tells_the_server_what_the_build_wrote_again());
        Flow.StepApart("an agents definition of what a restored generator declares is refused naming what the build wrote again where the server never reads it", () => new McpWindowTests(restored), t => t.An_agents_definition_of_what_a_restored_generator_declares_is_refused_naming_what_the_build_wrote_again_where_the_server_never_reads_it());
        Flow.StepApart("an agents definition of what a restored generator declares waits for the end of the batch its project loaded again in", () => new McpWindowTests(restored), t => t.An_agents_definition_of_what_a_restored_generator_declares_waits_for_the_end_of_the_batch_its_project_loaded_again_in());
        Flow.StepApart("an agents definition of what a restore brought waits for its projects load again though another projects load ends first", () => new McpWindowTests(restored), t => t.An_agents_definition_of_what_a_restore_brought_waits_for_its_projects_load_again_though_another_projects_load_ends_first());
        Flow.StepApart("an agents question after a restore that left its assets as they were waits for no later load", () => new McpWindowTests(restored), t => t.An_agents_question_after_a_restore_that_left_its_assets_as_they_were_waits_for_no_later_load());
    }

    /// <summary>An attached agent's faults and costs as one flow, each step one test's check in an instance of its own:
    /// a failing call leaving its exception and stack in the log, removing any exception turning it off in its answer
    /// and the Breakpoints window, an action on a file whose grammar failed refused at once, and a question of an open
    /// file costing the window what it asks, not the file's size.</summary>
    [AvaloniaFact]
    public void An_agents_failed_call_is_logged_whole_and_its_questions_cost_the_window_what_they_ask()
    {
        Flow.StepApart("an agents call that fails leaves its exception and stack in the log", () => new McpWindowTests(restored), t => t.An_agents_call_that_fails_leaves_its_exception_and_stack_in_the_log());
        Flow.StepApart("an agents remove of any exception turns it off in its answer and the breakpoints window", () => new McpWindowTests(restored), t => t.An_agents_remove_of_any_exception_turns_it_off_in_its_answer_and_the_breakpoints_window());
        Flow.StepApart("an agents action on a file whose grammar failed is refused at once saying so", () => new McpWindowTests(restored), t => t.An_agents_action_on_a_file_whose_grammar_failed_is_refused_at_once_saying_so());
        Flow.StepApart("an agents question of an open file costs the window what it asks not the files size", () => new McpWindowTests(restored), t => t.An_agents_question_of_an_open_file_costs_the_window_what_it_asks_not_the_files_size());
    }

    /// <summary>A diff's tab in the person's window is named a diff of its file in the agent's workspace, never as the file's
    /// own tab: among the open files and as the file shown, at its file's side's caret.</summary>
    private void An_agent_sees_a_diff_tab_in_the_persons_window_named_as_a_diff_of_its_file()
    {
        string repo = PathNames.Canonical(TestFolders.Create("mcp-diff-tab-"));
        DeleteAtEnd(repo);
        File.WriteAllText(Path.Combine(repo, "Shop.cs"), "class Shop\n{\n}\n");
        TestGit.Init(repo);
        File.WriteAllText(Path.Combine(repo, "Shop.cs"), "class Shop\n{\n    int count;\n}\n");
        MainWindow window = AgentsWindow(repo, languageServer: false);
        window.ApplySettings(window.Settings with { OpenDiffsIn = global::Passenger.App.Settings.DiffPlace.Tab });
        window.OpenFile(Path.Combine(repo, "Shop.cs"));
        Ui.Settle();
        Assert.True(window.Perform(AppActions.ShowDiff), $"Show Diff could not run: {window.Status}");
        Ui.Landed(() => window.EditorArea.Diffs.Count == 1 && window.EditorArea.Active?.Diff is not null, "Shop.cs's diff in a tab");
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement workspace = Await(Waiting.Apart(() => Call(client, McpTools.Workspace)), "the agent's workspace");
            string[] open = [.. workspace.GetProperty("openFiles").EnumerateArray().Select(f => f.GetString()!)];
            Assert.Contains("diff of Shop.cs (Shop.cs: HEAD and your version)", open);
            string? shown = workspace.GetProperty("activeFile").GetString();
            Assert.True(shown?.StartsWith("diff of Shop.cs (Shop.cs: HEAD and your version):", StringComparison.Ordinal) == true,
                $"The diff's tab was named as {shown} among the files shown, not as a diff of Shop.cs");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's package change is the NuGet window's own: the window shows it as its work while dotnet runs and
    /// lists the package installed once it is, and while the window's own change runs the agent's is refused, saying what is
    /// under way.</summary>
    private void An_agents_package_change_shows_as_the_nuget_windows_work_and_waits_its_turn_behind_the_windows_own()
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
                worked.Add(change.Work.Stage.Shown);
            }
        };
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> installing = Waiting.Apart(() => Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample" }));
            Press(Question(window, "the question for the agent's install"), McpWords.AllowForSession.English);
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
            Task<CallToolResult> again = Waiting.Apart(() => Result(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.0.0" }));
            CallToolResult refused = Ui.AwaitPumped(again, "the agent's install while the window's runs", AgentLimit);
            Assert.True(refused.IsError == true && Text(refused).Contains("Already under way: Installing Passenger.Other 2.0.0 in Lib…", StringComparison.Ordinal),
                $"The agent's install while the window's ran was answered: {Text(refused)}");
            File.WriteAllText(Path.Combine(library, "go"), string.Empty);
            Ui.Landed(() => windows.IsCompleted, "the window's install to end");
            Assert.True(Ui.Await(windows, "the window's install"), nuget.Log);
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

    /// <summary>A file the person saves while an agent's Replace in Files writes its closed files is their own change in Local
    /// History, where it was filed under the agent's replace, whose change set keeps only the replace's own writes.</summary>
    private void A_save_the_person_makes_while_an_agents_replace_writes_is_their_own_change_in_local_history()
    {
        string root = PathNames.Canonical(TestFolders.Create("mcp-replace-history-"));
        DeleteAtEnd(root);
        string notes = TestFolders.Write(root, "Notes.txt", "a tally of things\n");
        string shop = TestFolders.Write(root, "Shop.cs", "class Shop\n{\n}\n");
        MainWindow window = AgentsWindow(root, languageServer: false);
        window.OpenFile(shop);
        Ui.Settle();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task held = window.EditorArea.Commands.HoldNextWrite(release.Task);
            Task<JsonElement> replacing = Waiting.Apart(() => Call(client, McpTools.ReplaceInFiles,
                new() { ["text"] = "tally", ["replacement"] = "count", ["wholeWords"] = true, ["scope"] = "directory", ["directory"] = "." }));
            Avalonia.Controls.Window asked = Question(window, "the question for the agent's replace");
            // Pumped, not settled: the replace holds the window's background work while its write of Notes.txt is held.
            Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(asked).OfType<Avalonia.Controls.Button>().First(b => (string?)b.Content == McpWords.AllowForSession.English), settle: false);
            Ui.PumpUntil(() => held.IsCompleted, "the agent's write of Notes.txt to be held", AgentLimit);
            window.EditorArea.CodeEditor.Focus();
            window.KeyTextInput("// saved meanwhile ");
            Ui.Run(window, AppActions.Save, settle: false);
            Ui.PumpUntil(() => window.EditorArea.Active?.IsModified == false, "Shop.cs saved");
            release.SetResult();
            Await(replacing, "the agent's replace");
            Assert.Equal("a count of things\n", File.ReadAllText(notes));

            JsonElement saved = Await(Waiting.Apart(() => Call(client, McpTools.LocalHistory, new() { ["path"] = "Shop.cs" })), "Shop.cs's history").GetProperty("rows")[0];
            Assert.True(saved.GetProperty("title").GetString() == "Change",
                $"The save made while the agent's replace wrote is filed as '{saved.GetProperty("title").GetString()}', not as the person's own change");
            JsonElement replaced = Await(Waiting.Apart(() => Call(client, McpTools.LocalHistory, new() { ["path"] = "Notes.txt" })), "Notes.txt's history").GetProperty("rows")[0];
            Assert.Equal(global::Passenger.App.Workspace.FilesReplaced.Title(new global::Passenger.Text.SearchQuery("tally", WholeWords: true), "count"),
                replaced.GetProperty("title").GetString());
        }
        finally
        {
            release.TrySetResult();
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent searches the folder's feeds and installs a package with the NuGet window never built, its view
    /// made only as the person opens it, where the package is listed installed.</summary>
    private void An_agents_package_search_and_install_leave_the_nuget_window_unbuilt_until_the_person_opens_it()
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
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement found = Assert.Single(Await(Waiting.Apart(() => Call(client, McpTools.NugetSearch, new() { ["query"] = "sample", ["feed"] = "local" })), "the agent's search").EnumerateArray());
            Assert.Equal("Passenger.Sample", found.GetProperty("id").GetString());
            Task<JsonElement> installing = Waiting.Apart(() => Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample" }));
            Press(Question(window, "the question for the agent's install"), McpWords.AllowForSession.English);
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
    private void An_agents_answer_is_its_operations_outcome_whatever_the_status_bar_says_meanwhile()
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
            McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            try
            {
                Task<JsonElement> committing = Waiting.Apart(() => Call(client, McpTools.VcsCommit, new() { ["message"] = "Two as a number", ["push"] = true }));
                Press(Question(window, "the question for the agent's commit"), McpWords.AllowForSession.English);
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

    private void An_agents_action_no_action_has_or_one_that_cannot_run_here_is_an_error_saying_why()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> misspelt = Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = "app.biuld" }));
            Press(Question(window, "the question for the agent's action"), McpWords.AllowForSession.English);
            CallToolResult unknown = Await(misspelt, "the misspelt action");
            string said = Text(unknown);
            Assert.True(unknown.IsError == true, $"An action no action has was answered as run: {said}");
            Assert.Contains("No action is called app.biuld", said, StringComparison.Ordinal);

            CallToolResult commit = Await(Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.Commit })), "the commit with no repository");
            said = Text(commit);
            Assert.True(commit.IsError == true, $"Commit in a folder with no repository was answered as run: {said}");
            Assert.Contains(Refusals.NoRepository.English, said, StringComparison.Ordinal);
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Commit), "Commit, refused for want of a repository, opened the Commit window anyway");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent in a person's window whose folder they opened without trusting is refused at once the tools that
    /// would run its code, in the words the window greys its actions with, and a search of the feeds its nuget.config names,
    /// asking the person nothing; workspace says why, C# answers and a build are refused too, as the server's instructions say
    /// with each tool the rule refuses named, and the agent cannot trust the folder itself.</summary>
    private void An_agent_in_a_window_whose_folder_is_not_trusted_is_refused_what_would_run_its_code_and_cannot_trust_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-untrusted-"));
        File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"App\");\n");
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        Ui.WaitFor(() => window.AskingTrust is { IsVisible: true }, "the trust question");
        Ui.ClickButton(window.AskingTrust!, WorkspaceTrustDialog.Untrusted);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            foreach (string tool in (string[])[McpTools.Build, McpTools.Run, McpTools.DebugStart, McpTools.TestsRun])
            {
                CallToolResult refused = Await(Waiting.Apart(() => Result(client, tool)), tool);
                Assert.True(refused.IsError == true && Text(refused).Contains(Refusals.Untrusted.English, StringComparison.Ordinal),
                    $"{tool} in a window whose folder is not trusted answered: {Text(refused)}");
            }
            CallToolResult search = Await(Waiting.Apart(() => Result(client, McpTools.NugetSearch, new() { ["query"] = "Serilog" })), McpTools.NugetSearch);
            Assert.True(search.IsError == true && Text(search).Contains(Refusals.UntrustedFeeds.English, StringComparison.Ordinal),
                $"{McpTools.NugetSearch} in a window whose folder is not trusted answered: {Text(search)}");
            JsonElement workspace = Await(Waiting.Apart(() => Call(client, McpTools.Workspace)), "the workspace");
            Assert.Equal(Refusals.Untrusted.English, workspace.GetProperty("trust").GetString());
            // As the instructions say: every tool the one rule refuses named, and the C# answers and builds refused, never waited for.
            string told = client.ServerInstructions ?? string.Empty;
            Assert.True(McpTools.Untrusted.Keys.All(tool => told.Contains(tool, StringComparison.Ordinal)) && !told.Contains("wait for that", StringComparison.Ordinal)
                && told.Contains("refused at once, as are ide_action's actions that would run its code (building it among them) and C# answers", StringComparison.Ordinal),
                $"The instructions misstate what a folder not trusted refuses: {told}");
            CallToolResult symbol = Await(Waiting.Apart(() => Result(client, McpTools.FindSymbol, new() { ["query"] = "Program" })), McpTools.FindSymbol);
            Assert.True(symbol.IsError == true && Text(symbol).Contains(Refusals.Untrusted.English, StringComparison.Ordinal),
                $"{McpTools.FindSymbol} in a window whose folder is not trusted answered: {Text(symbol)}");
            Task<CallToolResult> building = Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.Build }));
            Press(Question(window, "the question for the agent's Build"), McpWords.AllowForSession.English);
            CallToolResult built = Await(building, "the agent's Build");
            Assert.True(built.IsError == true && Text(built).Contains(Refusals.Untrusted.English, StringComparison.Ordinal),
                $"{McpTools.IdeAction} {AppActions.Build} in a window whose folder is not trusted answered: {Text(built)}");

            CallToolResult trust = Await(Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.TrustFolder })), "the agent's Trust Folder");
            Assert.True(trust.IsError == true && Text(trust).Contains(Refusals.TrustIsThePersons.English, StringComparison.Ordinal),
                $"An agent's Trust Folder answered: {Text(trust)}");
            Assert.False(window.Session.Trusted, "An agent trusted the person's folder");
        }
        finally
        {
            Gone(client);
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>An agent's input into the person's window never trusts its folder: its click on the trust question's Trust
    /// never reaches the question, and its click at the point of the banner's Trust Folder, where the person's click trusts
    /// the folder, leaves it untrusted; the agent is told why each time, the status bar saying it too, and the person's
    /// own click then trusts it.</summary>
    private void An_agents_input_into_the_persons_window_never_trusts_its_folder_and_is_told_why()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-input-trust-"));
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes\n");
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        Ui.WaitFor(() => window.AskingTrust is { IsVisible: true }, "the trust question");
        WorkspaceTrustDialog question = window.AskingTrust!;
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            Task<CallToolResult> answering = Waiting.Apart(() => Result(client, McpTools.AppClick,
                new() { ["app"] = "ide", ["window"] = nameof(WorkspaceTrustDialog), ["control"] = $"Button \"{WorkspaceTrustDialog.Trust}\"" }));
            Press(Question(window, "the question for the agent's click in Passenger's windows"), McpWords.AllowForSession.English);
            CallToolResult answered = Await(answering, "the agent's click on the trust question's Trust");
            Assert.True(answered.IsError == true && Text(answered).Contains(Refusals.TrustAnsweredByThePerson.English, StringComparison.Ordinal),
                $"The agent's click on the trust question's Trust was answered: {Text(answered)}{Said()}");
            Assert.True(question.IsVisible && !window.Session.Trusted, "The agent's click answered the trust question the person is asked");

            Ui.ClickButton(question, WorkspaceTrustDialog.Untrusted);
            Ui.WaitFor(() => window.TrustSaid == Refusals.UntrustedSaid.English, "the banner over the folder opened without trusting it");
            string title = window.Actions[AppActions.TrustFolder]!.Title.English;
            Avalonia.Controls.Button link = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => (b.Content as string) == title && b.IsEffectivelyVisible);
            Avalonia.Point at = Avalonia.VisualExtensions.TranslatePoint(link, new Avalonia.Point(link.Bounds.Width / 2, link.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("The banner's Trust Folder is not in the window");
            CallToolResult clicked = Await(Waiting.Apart(() => Result(client, McpTools.AppClick,
                new() { ["app"] = "ide", ["window"] = nameof(MainWindow), ["x"] = at.X, ["y"] = at.Y })), "the agent's click on the banner's Trust Folder");
            Assert.True(clicked.IsError == true && Text(clicked).Contains(Refusals.TrustIsThePersons.English, StringComparison.Ordinal),
                $"The agent's click on the banner's Trust Folder was answered: {Text(clicked)}{Said()}");
            Ui.Settle();
            Assert.False(window.Session.Trusted || WorkspaceTrust.Trusts(folder), "The agent's click on the banner trusted the person's folder");
            Assert.Equal((Refusals.UntrustedSaid.English, Refusals.TrustIsThePersons.English), (window.TrustSaid, window.Status));

            Ui.Click(link);
            Ui.WaitFor(() => window.Session.Trusted && window.TrustSaid is null, "the person's own click on Trust Folder to trust the folder");
        }
        finally
        {
            Gone(client);
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>An agent's commit in a window whose folder is not trusted is refused a file Git stores through a filter,
    /// which does not run there, saying why before anything is committed; a commit of every changed file leaves that one
    /// out, saying which and why, and commits the file beside it.</summary>
    private void An_agents_commit_in_a_folder_not_trusted_leaves_out_a_file_stored_through_a_git_filter_saying_why()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-untrusted-filter-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\n");
        File.WriteAllText(Path.Combine(folder, "Picture.bin"), "one\n");
        File.WriteAllText(Path.Combine(folder, ".gitattributes"), "*.bin filter=keep\n");
        TestGit.Init(folder);
        TestGit.Run(folder, "config", "filter.keep.clean", "cat");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "two\n");
        File.WriteAllText(Path.Combine(folder, "Picture.bin"), "two\n");
        const string Why = "The folder is not trusted: Picture.bin is stored through Git's keep filter, which runs only once the folder is trusted";
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        Ui.WaitFor(() => window.AskingTrust is { IsVisible: true }, "the trust question");
        Ui.ClickButton(window.AskingTrust!, WorkspaceTrustDialog.Untrusted);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            Task<CallToolResult> naming = Waiting.Apart(() => Result(client, McpTools.VcsCommit, new() { ["message"] = "The picture", ["paths"] = new[] { "Picture.bin" } }));
            Press(Question(window, "the question for the agent's vcs_commit"), McpWords.AllowForSession.English);
            CallToolResult refused = Await(naming, "the agent's commit of Picture.bin");
            Assert.True(refused.IsError == true && Text(refused).Contains(Why, StringComparison.Ordinal), $"An agent's commit of Picture.bin in a folder not trusted answered: {Text(refused)}");
            string made = Await(Waiting.Apart(() => Call(client, McpTools.VcsCommit, new() { ["message"] = "The notes" })), "the agent's commit").GetString()!;
            Assert.EndsWith($" The notes; left uncommitted: Picture.bin ({Why})", made, StringComparison.Ordinal);
            Assert.Equal("two\n", TestGit.Run(folder, "show", "HEAD:Notes.txt"));
            Assert.Equal("one\n", TestGit.Run(folder, "show", "HEAD:Picture.bin"));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's stash and checkout in a window whose folder is not trusted are refused, saying why, before
    /// anything is saved or written, where git would store or write out a file it stores through a filter, which does not
    /// run there: the file's bytes, the branch and the stashes stay as they were.</summary>
    private void An_agents_stash_and_checkout_in_a_folder_not_trusted_are_refused_where_git_would_write_out_a_file_stored_through_a_filter()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-untrusted-filter-writes-"));
        DeleteAtEnd(folder);
        string picture = Path.Combine(folder, "Picture.bin");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\n");
        File.WriteAllText(picture, "one\n");
        File.WriteAllText(Path.Combine(folder, ".gitattributes"), "*.bin filter=keep\n");
        TestGit.Init(folder);
        TestGit.Run(folder, "config", "filter.keep.clean", "cat");
        TestGit.Run(folder, "config", "filter.keep.smudge", "cat");
        TestGit.Run(folder, "switch", "-q", "-c", "pictured");
        File.WriteAllText(picture, "pictured\n");
        TestGit.Run(folder, "commit", "-q", "-a", "-m", "The picture changed");
        TestGit.Run(folder, "switch", "-q", "main");
        File.WriteAllText(picture, "two\n");
        const string Why = "The folder is not trusted: Picture.bin is stored through Git's keep filter, which runs only once the folder is trusted";
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        Ui.WaitFor(() => window.AskingTrust is { IsVisible: true }, "the trust question");
        Ui.ClickButton(window.AskingTrust!, WorkspaceTrustDialog.Untrusted);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            Task<CallToolResult> stashing = Waiting.Apart(() => Result(client, McpTools.VcsStash, new() { ["action"] = "push", ["message"] = "Set aside" }));
            Press(Question(window, "the question for the agent's vcs_stash"), McpWords.AllowForSession.English);
            CallToolResult stash = Await(stashing, "the agent's stash");
            Assert.True(stash.IsError == true && Text(stash).Contains(Why, StringComparison.Ordinal), $"An agent's stash of Picture.bin in a folder not trusted answered: {Text(stash)}");
            Task<CallToolResult> checking = Waiting.Apart(() => Result(client, McpTools.VcsCheckout, new() { ["branch"] = "pictured" }));
            Press(Question(window, "the question for the agent's vcs_checkout"), McpWords.AllowForSession.English);
            CallToolResult checkout = Await(checking, "the agent's checkout");
            Assert.True(checkout.IsError == true && Text(checkout).Contains(Why, StringComparison.Ordinal), $"An agent's checkout of pictured in a folder not trusted answered: {Text(checkout)}");
            Assert.Equal(("main\n", string.Empty, "two\n"), (TestGit.Run(folder, "branch", "--show-current"), TestGit.Run(folder, "stash", "list"), File.ReadAllText(picture)));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's input into the person's window never installs what a project needs: its click on the banner's
    /// Install Workloads… installs nothing and asks the person nothing, and its click on Install in the question the
    /// person's own click asked never answers it; the agent is told why each time, the status bar saying it too, and the
    /// question waits for the person, whose Cancel installs nothing.</summary>
    [AvaloniaFact]
    public void An_agents_input_into_the_persons_window_never_installs_what_a_project_needs_and_is_told_why()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-input-install-"));
        DeleteAtEnd(folder);
        (string name, string platform) = OperatingSystem.IsMacOS() ? ("Tv", "tvos") : ("Droid", "android");
        File.WriteAllText(Path.Combine(folder, $"{name}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net{MobilePlatforms.FirstOnCoreClr}.0-{platform}</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "the project to evaluate", TimeSpan.FromMinutes(3));
        Ui.Await(window.MobileAsked(), "what the project needs to be asked", TimeSpan.FromMinutes(3));
        if (window.MobileWant.Workloads.Count == 0)
        {
            window.Close();
            Assert.Skip($"This .NET has the {platform} workload, so nothing is lacking to install");
        }
        List<IReadOnlyList<string>> installed = [];
        MainWindow.WorkloadStandIn = new WorkloadStandIn(folder, arguments =>
        {
            installed.Add(arguments);
            return ChildProcesses.DotnetCommand(["--version"]);
        });
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            string title = window.Actions[AppActions.InstallWorkloads]!.Title.English;
            Avalonia.Controls.Button link = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => (b.Content as string) == title && b.IsEffectivelyVisible);
            Avalonia.Point at = Avalonia.VisualExtensions.TranslatePoint(link, new Avalonia.Point(link.Bounds.Width / 2, link.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("The banner's Install Workloads… is not in the window");
            Task<CallToolResult> clicking = Waiting.Apart(() => Result(client, McpTools.AppClick,
                new() { ["app"] = "ide", ["window"] = nameof(MainWindow), ["x"] = at.X, ["y"] = at.Y }));
            Press(Question(window, "the question for the agent's click in Passenger's windows"), McpWords.AllowForSession.English);
            CallToolResult clicked = Await(clicking, "the agent's click on the banner's Install Workloads…");
            Assert.True(clicked.IsError == true && Text(clicked).Contains(Refusals.AgentInstallsMobile.English, StringComparison.Ordinal),
                $"The agent's click on the banner's Install Workloads… was answered: {Text(clicked)}");
            Ui.Settle();
            Assert.True(window.Actions.Work.Of(AppActions.InstallWorkloads) is null && !window.OwnedWindows.OfType<ConfirmDialog>().Any() && installed.Count == 0,
                $"The agent's click on Install Workloads… began an install ({window.Actions.Work.Of(AppActions.InstallWorkloads)?.Stage.Shown}; {installed.Count} run)");
            Assert.Equal(Refusals.AgentInstallsMobile.English, window.Status);

            Ui.Click(link);
            WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), "the person's own click on Install Workloads… to ask them");
            ConfirmDialog question = window.OwnedWindows.OfType<ConfirmDialog>().Single();
            CallToolResult answered = Await(Waiting.Apart(() => Result(client, McpTools.AppClick,
                new() { ["app"] = "ide", ["window"] = nameof(ConfirmDialog), ["control"] = "Button \"Install\"" })), "the agent's click on the question's Install");
            Assert.True(answered.IsError == true && Text(answered).Contains(Refusals.AgentInstallsMobile.English, StringComparison.Ordinal),
                $"The agent's click on the install question's Install was answered: {Text(answered)}");
            Ui.Settle();
            Assert.True(question.IsVisible && question.Answered is null && installed.Count == 0, "The agent's click answered the install question the person is asked");

            Press(question, "Cancel");
            WaitFor(() => window.Actions.Work.Of(AppActions.InstallWorkloads) is null, "the install the person turned down to end");
            Assert.Equal(($"The .NET workload {platform} was not installed", 0), (window.Status, installed.Count));
        }
        finally
        {
            MainWindow.WorkloadStandIn = null;
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An attached agent never quits the person's Passenger: its ide_action of Exit is refused, so is Exit chosen
    /// in Find Action by its keys, and, where the window draws its own buttons, its click on Close of the last window; the
    /// agent is told why each time, the status bar saying it too, and the window stays open.</summary>
    private void An_attached_agent_never_quits_the_persons_passenger_and_is_told_why()
    {
        string folder = PathNames.Canonical(Plain("mcp-quit-"));
        MainWindow window = AgentsWindow(folder, languageServer: false);
        // The person works in a file, which has the keys the agent's then reach.
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Ui.Settle();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        // Each tool asks the person once, allowed for the session.
        CallToolResult Allowed(string tool, Dictionary<string, object?> arguments, string what)
        {
            Task<CallToolResult> calling = Waiting.Apart(() => Result(client, tool, arguments));
            WaitFor(() => calling.IsCompleted || Questions(window).Any(), what);
            if (Questions(window).FirstOrDefault() is { } asked)
            {
                Press(asked, McpWords.AllowForSession.English);
            }
            return Await(calling, what);
        }
        try
        {
            CallToolResult exited = Allowed(McpTools.IdeAction, new() { ["action"] = AppActions.Exit }, "the agent's Exit");
            Assert.True(exited.IsError == true && Text(exited).Contains(Refusals.QuitIsThePersons.English, StringComparison.Ordinal), $"The agent's Exit answered: {Text(exited)}");
            Assert.True(window.IsVisible, "The agent's Exit closed the person's window");

            CallToolResult found = Allowed(McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "primary+shift+A" }, "the agent's keys for Find Action");
            CallToolResult typed = Allowed(McpTools.AppType, new() { ["app"] = "ide", ["text"] = "Exit" }, "the agent's typing in Find Action");
            CallToolResult entered = Allowed(McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "Enter" }, "the agent's Enter on Find Action's Exit");
            Assert.True(entered.IsError == true && Text(entered).Contains(Refusals.QuitIsThePersons.English, StringComparison.Ordinal),
                $"The agent's Enter on Find Action's Exit answered: {Text(entered)} (Find Action's keys: {Text(found)}; the typing: {Text(typed)})");
            Ui.Settle();
            Assert.True(window.IsVisible, "The agent's Exit from Find Action closed the person's window");
            Assert.Equal(Refusals.QuitIsThePersons.English, window.Status);

            if (Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(window, "CloseButton") is { IsEffectivelyVisible: true })
            {
                CallToolResult closed = Allowed(McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "#CloseButton" }, "the agent's click on the window's Close");
                Assert.True(closed.IsError == true && Text(closed).Contains(Refusals.QuitIsThePersons.English, StringComparison.Ordinal),
                    $"The agent's click on the last window's Close answered: {Text(closed)}");
                Ui.Settle();
                Assert.True(window.IsVisible, "The agent's click on Close closed the person's last window");
            }
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An attached agent never closes the person's solution, though it was allowed every tool: ide_actions lists
    /// Close Solution greyed saying it is the person's, its ide_action is refused, so is Close Solution chosen in Find
    /// Action by its keys, the agent told why each time and the status bar saying it too, and the folder stays open.</summary>
    private void An_attached_agent_allowed_every_tool_never_closes_the_persons_solution_and_is_told_why()
    {
        string folder = PathNames.Canonical(Plain("mcp-close-"));
        MainWindow window = AgentsWindow(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Ui.Settle();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        // What asks the person (every tool, input into Passenger's own windows) is allowed for the session.
        CallToolResult Allowed(string tool, Dictionary<string, object?> arguments, string what)
        {
            Task<CallToolResult> calling = Waiting.Apart(() => Result(client, tool, arguments));
            WaitFor(() => calling.IsCompleted || Questions(window).Any(), what);
            if (Questions(window).FirstOrDefault() is { } asked)
            {
                Press(asked, McpWords.AllowForSession.English);
            }
            return Await(calling, what);
        }
        try
        {
            Assert.Contains("allowed every tool", Text(Allowed(McpTools.RequestFullAccess, [], "the agent's request for every tool")), StringComparison.Ordinal);

            string listed = Text(Allowed(McpTools.IdeActions, new() { ["query"] = AppActions.CloseSolution }, "the agent's ide_actions"));
            Assert.True(listed.Contains($" - cannot run: {Refusals.CloseSolutionIsThePersons.English}", StringComparison.Ordinal), $"ide_actions listed Close Solution as: {listed}");
            CallToolResult closed = Allowed(McpTools.IdeAction, new() { ["action"] = AppActions.CloseSolution }, "the agent's Close Solution");
            Assert.True(closed.IsError == true && Text(closed).Contains(Refusals.CloseSolutionIsThePersons.English, StringComparison.Ordinal), $"The agent's Close Solution answered: {Text(closed)}");
            Ui.Settle();
            Assert.Equal(folder, window.Folder);

            Allowed(McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "primary+shift+A" }, "the agent's keys for Find Action");
            Allowed(McpTools.AppType, new() { ["app"] = "ide", ["text"] = "Close Solution" }, "the agent's typing in Find Action");
            CallToolResult entered = Allowed(McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "Enter" }, "the agent's Enter on Close Solution");
            Assert.True(entered.IsError == true && Text(entered).Contains(Refusals.CloseSolutionIsThePersons.English, StringComparison.Ordinal),
                $"The agent's Enter on Find Action's Close Solution answered: {Text(entered)}");
            Ui.Settle();
            Assert.Equal(folder, window.Folder);
            Assert.Equal(Refusals.CloseSolutionIsThePersons.English, window.Status);
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A file whose grammar failed reading it can never tell the lines inside its strings, so Convert Indentation is
    /// refused at once saying the grammar failed, where the agent waited out its minute for a reading that never came and
    /// was then told the file was still being read.</summary>
    private void An_agents_action_on_a_file_whose_grammar_failed_is_refused_at_once_saying_so()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-grammar-failed-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "tool.py"), "def twice(a):\n    return a * 2\n");
        MainWindow window = AgentsWindow(folder, languageServer: false);
        string variable = global::Passenger.Languages.TextMate.TextMateHighlighter.LineTimeVariable;
        string? was = Environment.GetEnvironmentVariable(variable);
        McpClient? client = null;
        try
        {
            // A time no grammar's pass takes, which fails it.
            Environment.SetEnvironmentVariable(variable, "0");
            window.OpenFile(Path.Combine(folder, "tool.py"));
            global::Passenger.Languages.TextMate.TextMateHighlighter grammar = window.EditorArea.Active?.Grammar ?? throw new InvalidOperationException("tool.py opened with no grammar");
            Ui.WaitFor(() => grammar.Failure is not null, "tool.py's grammar to fail");
            Environment.SetEnvironmentVariable(variable, was);
            Assert.Equal(Refusals.GrammarFailed("tool.py", "Python", grammar.Failure!.Message).English, window.Actions.WhyNot(AppActions.ConvertIndentationToTabs, window.Actions.Context())?.English);

            client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            Task<CallToolResult> converting = Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.ConvertIndentationToTabs }));
            Press(Question(window, "the question for the agent's action"), McpWords.AllowForSession.English);
            CallToolResult refused = Await(converting, "Convert Indentation on tool.py");
            string said = Text(refused);
            Assert.True(refused.IsError == true && said.Contains(Refusals.GrammarFailed("tool.py", "Python", grammar.Failure.Message).English, StringComparison.Ordinal),
                $"Convert Indentation on a file whose grammar failed answered: {said}");
            Assert.Contains(IdeErrors.Errors, e => e.Message == grammar.Failure.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, was);
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
            // The grammar's failure, which the window reports as the IDE's own error, is the test's.
            IdeErrors.Clear();
        }
    }

    /// <summary>An agent saves one of the folder's own run configurations as a launch profile through ide_action, naming
    /// it: the answer says where it went, the window lists it as generated from the profile, and one generated already is
    /// refused, saying why.</summary>
    private void An_agent_saves_the_persons_run_configuration_as_a_launch_profile_and_is_told_why_not_for_a_generated_one()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        string project = PathNames.Canonical(Path.Combine(Folder, "Sum.csproj"));
        window.SetConfigurations([new RunConfiguration { Name = "Twice", Project = project, Arguments = "--twice", WorkingDirectory = "$(ProjectDir)" }], "Twice");
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> saving = Waiting.Apart(() => Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.SaveAsLaunchProfile, ["input"] = "Twice" }));
            Press(Question(window, "the question for the agent's action"), McpWords.AllowForSession.English);
            Assert.Equal("Saved as the launch profile Twice in Sum's Properties/launchSettings.json, made for it: it is now Sum: Twice, generated from that profile",
                Await(saving, "the agent's Save as Launch Profile").GetString());
            Assert.True(window.Configurations.SingleOrDefault(c => c.Name == "Sum: Twice") is { Generated: true, Arguments: "--twice" },
                $"The window does not list the configuration the agent saved as generated from its profile: {string.Join(", ", window.Configurations.Select(c => c.Name))}");

            CallToolResult refused = Await(Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.SaveAsLaunchProfile, ["input"] = "Sum: Twice" })),
                "the agent's Save as Launch Profile of a generated configuration");
            string said = Text(refused);
            Assert.True(refused.IsError == true, $"Save as Launch Profile of a generated configuration was answered as done: {said}");
            Assert.Contains("Sum: Twice is generated from its launch profile already", said, StringComparison.Ordinal);
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A file come into a project the language server met an error loading, asked about while the server's load of
    /// the project again for it is held (its design-time builds not yet given), is in none of the server's projects, and no
    /// load of the project can take it in until the project is mended: an agent's outline of it says at once that the
    /// project did not load and why, never that the file is not loaded yet once the wait for it has run out.</summary>
    private void An_agent_asking_of_a_file_come_into_a_project_that_did_not_load_hears_why_while_the_project_loads_again()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unloaded-came-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Good/Good.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Good/Fine.cs", "namespace Good;\n\npublic class Fine\n{\n}\n");
        string bad = TestFolders.Write(folder, "Bad/Bad.csproj", TestProjects.Library().Replace("</Project>", string.Empty, StringComparison.Ordinal));
        TestFolders.Write(folder, "Bad/Greeter.cs", "namespace Lib;\n\npublic class Greeter\n{\n}\n");
        MainWindow window = AgentsWindow(folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true } csharp && csharp.ProjectProblems.Any(p => PathNames.Same(p.Project, bad)),
            () => $"the language server to load the projects, Bad with an error ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        TaskCompletionSource building = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task held = window.Session.DesignTimeBuilds.HoldNextAsked(bad, building.Task);
            TestFolders.Write(folder, "Bad/Welcome.cs", "namespace Lib;\n\npublic static class Welcome\n{\n    public static string Say() => \"Welcome\";\n}\n");
            // Pumped, not settled: the server's load held waits on the window.
            Ui.PumpUntil(() => held.IsCompleted, "the server's load of Bad again, for the file come into it, to be held", AgentLimit);
            CallToolResult answer = Ui.AwaitPumped(Waiting.Apart(() => Result(client, McpTools.Outline, new() { ["path"] = "Bad/Welcome.cs" })),
                "the agent's outline of Welcome.cs", AgentLimit);
            string said = Text(answer);
            Assert.True(answer.IsError == true && said.Contains("Bad did not load into the C# language server, which holds none of its code to answer from: The project file could not be loaded.",
                StringComparison.Ordinal), $"outline of a file come into a project that did not load, while the project loads again, answered: {said}");
        }
        finally
        {
            building.TrySetResult();
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A file come into a project while the language server's load of the project again for it is held (its
    /// design-time builds not yet given), so in none of the server's projects once the wait for it runs out: an agent's
    /// find_symbol over the solution answers with what the loaded code declares, naming the file it went without, where it
    /// was refused whole; a tool asked of a file of that project still waits for it, refused naming it once the wait runs out,
    /// and the project's Optimize Imports leaves it as it is saying so, where it tidied it from no project.</summary>
    private void An_agents_find_symbol_answers_without_a_file_still_loading_naming_it_while_a_tool_of_its_project_waits_for_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-still-loading-"));
        DeleteAtEnd(folder);
        string lib = TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Greeter.cs", "namespace Lib;\n\npublic class Greeter\n{\n    public string Greet() => \"Hello\";\n}\n");
        MainWindow window = AgentsWindow(folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, () => $"the language server to load Lib ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        TaskCompletionSource building = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task held = window.Session.DesignTimeBuilds.HoldNextAsked(lib, building.Task);
            string written = TestFolders.Write(folder, "Lib/Late.cs", "using System.Text;\n\nnamespace Lib;\n\npublic static class Late\n{\n}\n");
            // Pumped, not settled: the server's load held waits on the window.
            Ui.PumpUntil(() => held.IsCompleted, "the server's load of Lib again, for the file come into it, to be held", AgentLimit);
            string late = Path.Combine("Lib", "Late.cs");

            CallToolResult found = Ui.AwaitPumped(Waiting.Apart(() => Result(client, McpTools.FindSymbol, new() { ["query"] = "Greeter" })),
                "the agent's find_symbol of Greeter", AgentLimit);
            string[] said = [.. found.Content.OfType<TextContentBlock>().Select(t => t.Text)];
            JsonElement[] symbols = found.IsError == true ? [] : [.. JsonDocument.Parse(said[0]).RootElement.EnumerateArray()];
            Assert.True(symbols.Any(s => s.GetProperty("name").GetString() == "Greeter" && s.GetProperty("path").GetString() == Path.Combine("Lib", "Greeter.cs"))
                && said.Skip(1).Any(words => words.Contains(Refusals.Untaken([late]).English, StringComparison.Ordinal)),
                $"find_symbol of Greeter, asked while Late.cs is still loading into Lib, did not find Greeter naming Late.cs as left out: {string.Join(" | ", said)}");

            CallToolResult asked = Ui.AwaitPumped(Waiting.Apart(() => Result(client, McpTools.SymbolInfo, new() { ["path"] = "Lib/Greeter.cs", ["line"] = 3, ["column"] = 14 })),
                "the agent's symbol_info of Greeter", AgentLimit);
            string answered = Text(asked);
            Assert.True(asked.IsError == true && answered.Contains(Refusals.NotTakenIn(late, "Lib").English, StringComparison.Ordinal),
                $"symbol_info in Lib, asked while Late.cs is still loading into it, did not wait for Late.cs and say so: {answered}");

            Task<CallToolResult> tidying = Waiting.Apart(() => Result(client, McpTools.Refactor,
                new() { ["kind"] = "optimize_imports", ["path"] = "Lib/Lib.csproj", ["options"] = new { scope = "project" } }));
            Ui.PumpUntil(() => Questions(window).Any(), "the question for the agent's Optimize Imports", AgentLimit);
            // Pumped, not settled: the server's load held waits on the window.
            Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Questions(window).Single()).OfType<Avalonia.Controls.Button>()
                .First(b => (string?)b.Content == McpWords.AllowForSession.English), settle: false);
            CallToolResult tidied = Ui.AwaitPumped(tidying, "the agent's Optimize Imports of Lib", AgentLimit);
            string[] left = tidied.IsError == true ? [] : [.. JsonDocument.Parse(Text(tidied)).RootElement.GetProperty("notApplied").EnumerateArray().Select(l => l.GetString()!)];
            Assert.True(left.Contains(Refusals.NotTakenIn(late, "Lib").English) && File.ReadAllText(written).StartsWith("using System.Text;", StringComparison.Ordinal),
                $"Optimize Imports of Lib, asked while Late.cs is still loading into it, did not leave Late.cs as it is saying why: {Text(tidied)}");
        }
        finally
        {
            building.TrySetResult();
            Gone(client);
            window.Close();
        }
    }

    /// <summary>Reformat Code an agent runs through ide_action answers what it came to: on a file the language server has,
    /// whether it changed; on a file come into a project while the server's load of the project again for it is held, so the
    /// server, ready, has not taken it in, a refusal naming the file and the project it is not loaded into yet, as the status
    /// bar says it, where the server's state alone was said ("C# ready") and the agent heard only that the action ran.</summary>
    private void An_agents_Reformat_Code_of_a_file_the_server_has_not_taken_in_yet_is_refused_naming_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-reformat-untaken-"));
        DeleteAtEnd(folder);
        string lib = TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        string greeter = TestFolders.Write(folder, "Lib/Greeter.cs", "namespace Lib;\n\npublic class Greeter\n{\n    public string Greet() => \"Hello\";\n}\n");
        MainWindow window = AgentsWindow(folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, () => $"the language server to load Lib ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        TaskCompletionSource building = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.OpenFile(greeter);
            Ui.WaitFor(() => window.CSharpService!.For(window.EditorArea.Active!)?.Lsp is not null, "the server to have Greeter.cs");
            Task<CallToolResult> formatting = Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.Reformat }));
            Press(Question(window, "the question for the agent's Reformat Code"), McpWords.AllowForSession.English);
            CallToolResult formatted = Await(formatting, "the agent's Reformat Code of Greeter.cs");
            Assert.True(formatted.IsError != true && Text(formatted) is "Already formatted" or "Reformatted", $"Reformat Code of Greeter.cs answered an agent: {Text(formatted)}");

            Task held = window.Session.DesignTimeBuilds.HoldNextAsked(lib, building.Task);
            string late = TestFolders.Write(folder, "Lib/Late.cs", "namespace Lib;\n\npublic static class Late\n{\n}\n");
            // Pumped, not settled: the server's load held waits on the window.
            Ui.PumpUntil(() => held.IsCompleted, "the server's load of Lib again, for the file come into it, to be held", AgentLimit);
            window.OpenFile(late);
            Ui.PumpUntil(() => window.EditorArea.Active?.Name == "Late.cs" && window.CSharpService!.For(window.EditorArea.Active) is not null, "Late.cs to open", AgentLimit);
            CallToolResult refused = Ui.AwaitPumped(Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.Reformat })),
                "the agent's Reformat Code of Late.cs", AgentLimit);
            string expected = Refusals.NotInServerYet("Reformat Code", "Late.cs", "Lib").English;
            Assert.True(refused.IsError == true && Text(refused).Contains(expected, StringComparison.Ordinal),
                $"Reformat Code of a file the server has not taken in yet answered an agent: {Text(refused)}");
            Assert.Equal(expected, window.Status);
        }
        finally
        {
            building.TrySetResult();
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A MAUI page's code-behind asked of as its folder first loads: the server's restore brings MAUI's generator,
    /// its load after queues the generator's run, and the server's word that its diagnostics moved, sent of changes made
    /// before that load, is heard just after it ends (held to then). An agent's definition of the InitializeComponent the
    /// generator declares, asked before the first load, goes to that declaration once the generator has run, never
    /// answering nothing because the word came first.</summary>
    private void An_agents_definition_of_what_a_restored_generator_declares_waits_for_its_run_though_the_server_says_its_diagnostics_moved_first()
    {
        string folder = McpNavigationTests.MauiPage("mcp-maui-refreshed-");
        DeleteAtEnd(folder);
        string project = Path.Combine(folder, "MauiApp1.csproj");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reloading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        McpClient? client = null;
        try
        {
            // Pumped, not settled throughout: the server's loads held wait on the window.
            Task opening = window.OpenFolder(folder);
            Ui.PumpUntil(() => opening.IsCompleted && window.CSharpService is not null, "the folder to open with a language server", AgentLimit);
            Task refreshHeld = window.CSharpService!.HoldRefreshesPastRestore();
            Task loadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, loading.Task);
            window.ApplySettings(window.Settings with { McpServer = true });
            Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
            client = Ui.AwaitPumped(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching", AgentLimit);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 }, heard: heard));
            Ui.PumpUntil(() => loadHeld.IsCompleted && (heard.Told.Count > 0 || asked.IsCompleted), "the server's first load of MauiApp1 to be held while the agent's definition waits", AgentLimit);
            Task reloadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, reloading.Task);
            loading.SetResult();
            Ui.PumpUntil(() => reloadHeld.IsCompleted && refreshHeld.IsCompleted, "the server's load of MauiApp1 after its restore to be held, its word that its diagnostics moved heard", AgentLimit);
            reloading.SetResult();
            CallToolResult answer = Ui.AwaitPumped(asked, "the agent's definition of InitializeComponent", AgentLimit);
            string said = Text(answer);
            Assert.True(answer.IsError != true && said.Contains("MainPage.xaml.sg.cs", StringComparison.Ordinal)
                && said.Contains("private partial void InitializeComponent();", StringComparison.Ordinal),
                $"definition of a MAUI page's InitializeComponent, asked as the restore bringing its generator loaded, answered: {said}");
        }
        finally
        {
            loading.TrySetResult();
            reloading.TrySetResult();
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
        }
    }

    /// <summary>A MAUI page's folder whose first load restores MAUI: the design-time build the load after the restore takes
    /// writes again the settings MAUI's generator reads of the page and the global usings, and the server keeps the texts it
    /// read of them until it hears of them, held back here past the generators' run. An agent's definition of the
    /// InitializeComponent the generator declares waits, saying so, until the server has read them, never answering nothing
    /// because the generator ran on the settings of the load before the restore.</summary>
    private void An_agents_definition_of_what_a_restored_generator_declares_waits_for_the_server_to_read_what_its_build_wrote_again()
    {
        string folder = McpNavigationTests.MauiPage("mcp-maui-unread-");
        DeleteAtEnd(folder);
        string project = Path.Combine(folder, "MauiApp1.csproj");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource telling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        McpClient? client = null;
        try
        {
            // Pumped, not settled throughout: the server's load held waits on the window.
            Task opening = window.OpenFolder(folder);
            Ui.PumpUntil(() => opening.IsCompleted && window.CSharpService is not null, "the folder to open with a language server", AgentLimit);
            Task loadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, loading.Task);
            window.ApplySettings(window.Settings with { McpServer = true });
            Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
            client = Ui.AwaitPumped(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching", AgentLimit);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 }, heard: heard));
            Ui.PumpUntil(() => loadHeld.IsCompleted && window.CSharpService!.Session is not null && (heard.Told.Count > 0 || asked.IsCompleted),
                "the server's first load of MauiApp1 to be held while the agent's definition waits", AgentLimit);
            window.CSharpService!.Session!.Server.WatchedFiles.HoldTelling(
                path => path.EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.Ordinal) || path.EndsWith(".GlobalUsings.g.cs", StringComparison.Ordinal), telling.Task);
            loading.SetResult();
            Ui.PumpUntil(() => asked.IsCompleted || heard.Told.Any(t => t.Message?.Contains("which a design-time build wrote again", StringComparison.Ordinal) == true),
                "the agent's definition to wait for the server to read what the build after the restore wrote again", AgentLimit);
            telling.SetResult();
            CallToolResult answer = Ui.AwaitPumped(asked, "the agent's definition of InitializeComponent", AgentLimit);
            string said = Text(answer);
            Assert.True(answer.IsError != true && said.Contains("MainPage.xaml.sg.cs", StringComparison.Ordinal)
                && said.Contains("private partial void InitializeComponent();", StringComparison.Ordinal),
                $"definition of a MAUI page's InitializeComponent, asked before the server read what the build after the restore wrote again, answered: {said}");
        }
        finally
        {
            loading.TrySetResult();
            telling.TrySetResult();
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
        }
    }

    /// <summary>A MAUI page's folder whose first load restores MAUI: the build the load after the restore takes writes again,
    /// in place, the settings MAUI's generator reads of the page and the global usings, which no watcher tells the server of
    /// (held back throughout, as a loaded Mac's watcher, behind the system's, misses a file written in place). An agent's
    /// definition of the InitializeComponent the generator declares goes to it, the wait telling the server of what it holds
    /// otherwise than the disk, never answering nothing as of the global usings before the restore.</summary>
    private void An_agents_definition_of_what_a_restored_generator_declares_goes_to_it_though_no_watcher_tells_the_server_what_the_build_wrote_again()
    {
        string folder = McpNavigationTests.MauiPage("mcp-maui-untold-");
        DeleteAtEnd(folder);
        (CallToolResult answer, string said) = DefinitionWhileTheBuildsOutputsAreUntold(folder, found: false);
        Assert.True(answer.IsError != true && said.Contains("MainPage.xaml.sg.cs", StringComparison.Ordinal)
            && said.Contains("private partial void InitializeComponent();", StringComparison.Ordinal),
            $"definition of a MAUI page's InitializeComponent, asked while no watcher told the server what the build after the restore wrote again, answered: {said}");
    }

    /// <summary>As above, the server never taking the disk's text of the files the build after the restore wrote again,
    /// though told it changed (held back here throughout): the agent's definition is refused once the wait for them has
    /// run out, saying it waited for the server to read them, never answering nothing as of the text before.</summary>
    private void An_agents_definition_of_what_a_restored_generator_declares_is_refused_naming_what_the_build_wrote_again_where_the_server_never_reads_it()
    {
        string folder = McpNavigationTests.MauiPage("mcp-maui-unheard-");
        DeleteAtEnd(folder);
        (CallToolResult answer, string said) = DefinitionWhileTheBuildsOutputsAreUntold(folder, found: true);
        Assert.True(answer.IsError == true && said.Contains(": Passenger waited ", StringComparison.Ordinal)
            && said.Contains(" for the C# language server to read MauiApp1.", StringComparison.Ordinal)
            && said.Contains(", which a design-time build wrote again, which has not come, so the answer would be of the code before it", StringComparison.Ordinal),
            $"definition of a MAUI page's InitializeComponent, asked while the server never read what the build after the restore wrote again, answered: {said}");
    }

    /// <summary>An agent's definition of a MAUI page's InitializeComponent, asked as its folder first loads, while the server
    /// is told nothing a watcher hears of the files the build after the restore writes again in its intermediate output
    /// folder, nor, with <paramref name="found"/>, what the generators' wait finds it holds otherwise than the disk.</summary>
    private (CallToolResult Answer, string Said) DefinitionWhileTheBuildsOutputsAreUntold(string folder, bool found)
    {
        string project = Path.Combine(folder, "MauiApp1.csproj");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource telling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        McpClient? client = null;
        try
        {
            // Pumped, not settled throughout: the server's load held waits on the window.
            Task opening = window.OpenFolder(folder);
            Ui.PumpUntil(() => opening.IsCompleted && window.CSharpService is not null, "the folder to open with a language server", AgentLimit);
            Task loadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, loading.Task);
            window.ApplySettings(window.Settings with { McpServer = true });
            Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
            client = Ui.AwaitPumped(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching", AgentLimit);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 }, heard: heard));
            Ui.PumpUntil(() => loadHeld.IsCompleted && window.CSharpService!.Session is not null && (heard.Told.Count > 0 || asked.IsCompleted),
                "the server's first load of MauiApp1 to be held while the agent's definition waits", AgentLimit);
            string written = Path.Combine(folder, "obj", "Debug", "net11.0");
            window.CSharpService!.Session!.Server.WatchedFiles.HoldTelling(path => PathNames.IsUnder(path, written)
                && (path.EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.Ordinal) || path.EndsWith(".GlobalUsings.g.cs", StringComparison.Ordinal)), telling.Task, found);
            loading.SetResult();
            CallToolResult answer = Ui.AwaitPumped(asked, "the agent's definition of InitializeComponent", AgentLimit);
            return (answer, Text(answer));
        }
        finally
        {
            loading.TrySetResult();
            telling.TrySetResult();
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
        }
    }

    /// <summary>A MAUI page's folder whose first load restores MAUI, the server's report that it loaded the project again
    /// with what the restore brought heard before the end of the batch it loaded in (held back here, as a server that loads
    /// the project once more in that batch keeps its end): an agent's definition of the InitializeComponent MAUI's generator
    /// declares, asked as the folder opens, waits through the batch's end and the generators' run it queues, saying so,
    /// and goes to that declaration, never answering nothing as of the code before the generator ran.</summary>
    private void An_agents_definition_of_what_a_restored_generator_declares_waits_for_the_end_of_the_batch_its_project_loaded_again_in()
    {
        string folder = McpNavigationTests.MauiPage("mcp-maui-batch-");
        DeleteAtEnd(folder);
        string project = Path.Combine(folder, "MauiApp1.csproj");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reloading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        McpClient? client = null;
        try
        {
            // Pumped, not settled throughout: the server's loads and the batch's end held wait on the window.
            Task opening = window.OpenFolder(folder);
            Ui.PumpUntil(() => opening.IsCompleted && window.CSharpService is not null, "the folder to open with a language server", AgentLimit);
            Task loadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, loading.Task);
            Task batchHeld = window.CSharpService!.HoldBatchEndPastRestore(ending.Task);
            // The window's own load again after the restore, which an agent's question would wait for too, comes after.
            window.CSharpService.HoldReloads(project, ending.Task);
            window.ApplySettings(window.Settings with { McpServer = true });
            Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
            client = Ui.AwaitPumped(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching", AgentLimit);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 }, heard: heard));
            Ui.PumpUntil(() => loadHeld.IsCompleted && window.CSharpService.Session is not null && (heard.Told.Count > 0 || asked.IsCompleted),
                "the server's first load of MauiApp1 to be held while the agent's definition waits", AgentLimit);
            Task reloadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, reloading.Task);
            loading.SetResult();
            Ui.PumpUntil(() => asked.IsCompleted || reloadHeld.IsCompleted, "the server's load after the restore to ask for MauiApp1's builds", AgentLimit);
            Ui.Hold(TimeSpan.FromSeconds(2), "the generators' run the first load queued, 1.5 s after it, to go before the load after the restore");
            reloading.SetResult();
            Ui.PumpUntil(() => asked.IsCompleted || (batchHeld.IsCompleted
                && heard.Told.Any(t => t.Message?.Contains("to load again the projects its restore brought packages to", StringComparison.Ordinal) == true)),
                "the agent's definition to wait for the end of the batch the server loaded MauiApp1 again in after its restore", AgentLimit);
            ending.SetResult();
            CallToolResult answer = Ui.AwaitPumped(asked, "the agent's definition of InitializeComponent", AgentLimit);
            string said = Text(answer);
            Assert.True(answer.IsError != true && said.Contains("MainPage.xaml.sg.cs", StringComparison.Ordinal)
                && said.Contains("private partial void InitializeComponent();", StringComparison.Ordinal),
                $"definition of a MAUI page's InitializeComponent, asked as the server's batch loading MauiApp1 again after its restore had still to end, answered: {said}");
        }
        finally
        {
            loading.TrySetResult();
            reloading.TrySetResult();
            ending.TrySetResult();
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
        }
    }

    /// <summary>An agent renames the type a MAUI page's C# expression names, which moves its file (Helper.cs to Speaker.cs),
    /// then at once renames the method the expression calls from the expression, the server's load taking in the moved file
    /// held back until then: the rename waits, saying so, until the server has the file in its project, then renames the
    /// method there and in the expression, never refusing the expression as naming nothing.</summary>
    private void An_agents_rename_in_a_maui_expression_asked_as_a_type_rename_moved_its_file_waits_for_the_server_to_take_the_file_in()
    {
        (string folder, string page) = McpNavigationTests.MauiPageCallingHelper("mcp-maui-moved-");
        DeleteAtEnd(folder);
        string project = Path.Combine(folder, "MauiApp1.csproj");
        MainWindow window = AgentsWindow(folder);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // The first waits through the server's restore of MAUI and the load after it, so the load held next is the move's.
            Task<JsonElement> yelled = Waiting.Apart(() => Call(client, McpTools.Rename, new() { ["path"] = "Helper.cs", ["line"] = 5, ["column"] = 26, ["newName"] = "Yell" }));
            Press(Question(window, "the question for the agent's rename"), McpWords.AllowForSession.English);
            Await(yelled, "the agent's rename of Shout to Yell");
            // Pumped, not settled, from here: the server's load held waits on the window.
            Task loadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, loading.Task);
            Ui.AwaitPumped(Waiting.Apart(() => Call(client, McpTools.Rename, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 21, ["newName"] = "Speaker" })),
                "the agent's rename of Helper to Speaker", AgentLimit);
            string speaker = Path.Combine(folder, "Speaker.cs");
            Assert.True(File.Exists(speaker) && !File.Exists(Path.Combine(folder, "Helper.cs")), "renaming Helper to Speaker did not rename its file Speaker.cs");
            Ui.PumpUntil(() => loadHeld.IsCompleted, "the server's load of MauiApp1 taking in Speaker.cs to be held", AgentLimit);
            string renamed = File.ReadAllText(Path.Combine(folder, "MainPage.xaml"));
            int at = renamed.IndexOf("Yell(", StringComparison.Ordinal);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Rename, new()
            {
                ["path"] = "MainPage.xaml", ["line"] = renamed[..at].Count(c => c == '\n') + 1, ["column"] = at - renamed.LastIndexOf('\n', at) + 1, ["newName"] = "Cheer",
            }, heard: heard));
            Ui.PumpUntil(() => asked.IsCompleted || heard.Told.Any(t => t.Message?.Contains("Speaker.cs", StringComparison.Ordinal) == true),
                "the agent's rename from the expression to wait for the server to take Speaker.cs into MauiApp1", AgentLimit);
            if (asked.IsCompleted)
            {
                Assert.Fail($"the agent's rename from the expression answered while the server's load taking in Speaker.cs was held: {Text(Ui.AwaitPumped(asked, "the agent's rename", AgentLimit))}");
            }
            loading.SetResult();
            CallToolResult answer = Ui.AwaitPumped(asked, "the agent's rename of Yell to Cheer from the expression", AgentLimit);
            Assert.True(answer.IsError != true, $"the agent's rename from the expression, asked as the server's load taking in Speaker.cs was held, answered: {Text(answer)}");
            Assert.Contains("public static string Cheer(string text)", File.ReadAllText(speaker), StringComparison.Ordinal);
            string again = File.ReadAllText(Path.Combine(folder, "MainPage.xaml"));
            Assert.True(again == page.Replace("{local:Helper.Shout(", "{local:Speaker.Cheer(", StringComparison.Ordinal),
                $"renaming Yell to Cheer from the expression left it naming Yell, or changed more: {again}");
        }
        finally
        {
            loading.TrySetResult();
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's definition on a compose file asked as the YAML language server starts (held here, as a first start
    /// on a loaded machine holds it) waits, saying so, and past the agent's wait is refused as the server still starting
    /// after it; asked again once the server is up, it answers. Skipped without a Node.js that runs the YAML server.</summary>
    private void An_agents_definition_on_a_compose_file_waits_saying_so_for_the_yaml_server_to_start_and_past_the_wait_is_refused_as_still_starting()
    {
        DockerFilesTests.Shop.SkipWithoutNode(yaml: true);
        const string Compose = "services:\n  web:\n    image: nginx:1.27\n    depends_on: [api]\n  api:\n    build: .\n";
        string folder = PathNames.Canonical(TestFolders.Create("mcp-yaml-start-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "compose.yaml"), Compose);
        string? limitWas = Environment.GetEnvironmentVariable(PassengerTools.ServerLimitVariable);
        TimeSpan limit = Waits.Of(TimeSpan.FromSeconds(4));
        MainWindow window = AgentsWindow(folder, languageServer: false);
        TaskCompletionSource starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> held = window.Session.Web!.HoldNextConfigure(starting.Task);
        McpClient? client = null;
        Environment.SetEnvironmentVariable(PassengerTools.ServerLimitVariable, ((int)limit.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            client = Ui.AwaitPumped(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching", AgentLimit);
            Dictionary<string, object?> onApi = new() { ["path"] = "compose.yaml", ["line"] = 4, ["column"] = 20 };
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, onApi, heard: heard));
            CallToolResult refused = Ui.AwaitPumped(asked, "the agent's definition, asked as the YAML server's start is held", AgentLimit);
            Assert.True(held.IsCompleted, $"The YAML server's start was never held, the definition answering: {Text(refused)}");
            Assert.True(refused.IsError == true && Text(refused).Contains(Refusals.StillStarting("YAML", limit).English, StringComparison.Ordinal),
                $"definition on a compose file, asked while the YAML server's start was held past the agent's wait, answered: {Text(refused)}");
            Assert.True(heard.Told.Any(t => t.Message?.Contains("Waiting for the YAML language server to start", StringComparison.Ordinal) == true),
                $"The agent was not told its definition waited for the YAML server to start: {heard}");

            starting.SetResult();
            Ui.PumpUntil(() => window.Session.Web.Node is not null && window.EditorArea.Documents.Any(d => window.Session.Web.For(d)?.Lsp is not null),
                "the YAML server to start once let go", AgentLimit);
            CallToolResult answered = Ui.AwaitPumped(Waiting.Apart(() => Result(client, McpTools.Definition, onApi)), "the agent's definition once the YAML server is up", AgentLimit);
            Assert.True(answered.IsError != true && Text(answered).Contains("\"line\":5", StringComparison.Ordinal),
                $"definition of the service api, asked once the YAML server was up, did not lead to its declaration on line 5: {Text(answered)}");
        }
        finally
        {
            starting.TrySetResult();
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
            Environment.SetEnvironmentVariable(PassengerTools.ServerLimitVariable, limitWas);
        }
    }

    /// <summary>A MAUI page's folder whose load after its first restore waits for its design-time builds (held back here)
    /// longer than an agent's code tools wait for the C# language server, given this window a little past the time the
    /// server may say nothing of a load under way: the agent's definition of the InitializeComponent MAUI's generator
    /// declares, asked as the folder opens, is refused as the server still loading once the wait is over, never answered as
    /// nothing from the load before the restore.</summary>
    private void An_agents_definition_asked_while_the_server_loads_again_past_the_wait_is_refused_as_still_loading()
    {
        string? limitWas = Environment.GetEnvironmentVariable(PassengerTools.ServerLimitVariable);
        TimeSpan limit = Waits.Of(TimeSpan.FromSeconds(15)) + TimeSpan.FromSeconds(10).Scaled();
        string folder = McpNavigationTests.MauiPage("mcp-maui-slow-");
        DeleteAtEnd(folder);
        string project = Path.Combine(folder, "MauiApp1.csproj");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reloading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        McpClient? client = null;
        Environment.SetEnvironmentVariable(PassengerTools.ServerLimitVariable, ((int)limit.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            // Pumped, not settled throughout: the server's load held waits on the window.
            Task opening = window.OpenFolder(folder);
            Ui.PumpUntil(() => opening.IsCompleted && window.CSharpService is not null, "the folder to open with a language server", AgentLimit);
            Task loadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, loading.Task);
            window.ApplySettings(window.Settings with { McpServer = true });
            Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
            client = Ui.AwaitPumped(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching", AgentLimit);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 }, heard: heard));
            Ui.PumpUntil(() => loadHeld.IsCompleted && window.CSharpService!.Session is not null && (heard.Told.Count > 0 || asked.IsCompleted),
                "the server's first load of MauiApp1 to be held while the agent's definition waits", AgentLimit);
            Task reloadHeld = window.Session.DesignTimeBuilds.HoldNextAsked(project, reloading.Task);
            loading.SetResult();
            Ui.PumpUntil(() => asked.IsCompleted || reloadHeld.IsCompleted, "the server's load after the restore to ask for MauiApp1's design-time builds", AgentLimit);
            CallToolResult answer = Ui.AwaitPumped(asked, "the agent's definition, asked as the server's load after the restore waits", AgentLimit);
            string said = Text(answer);
            Assert.True(answer.IsError == true && (said.Contains("is still loading the folder's projects", StringComparison.Ordinal)
                || said.Contains("load MauiApp1 again with what its restore brought, which has not come", StringComparison.Ordinal)),
                $"definition of a MAUI page's InitializeComponent, asked while the server's load after the restore waited past the agent's wait, answered: {said}");
        }
        finally
        {
            loading.TrySetResult();
            reloading.TrySetResult();
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
            Environment.SetEnvironmentVariable(PassengerTools.ServerLimitVariable, limitWas);
        }
    }

    /// <summary>Two projects each given a package once loaded, the first's change loaded and restored as the second's waits:
    /// the server's next batch loads the second alone, before it hears of the first's restore (held back here), then
    /// restores the second, which fails, no feed holding its package. An agent's definition of the class the first's
    /// restore brought waits, saying so, for the first project's load again after its restore, never answering as of its
    /// load before it because another project's load ended in between.</summary>
    private void An_agents_definition_of_what_a_restore_brought_waits_for_its_projects_load_again_though_another_projects_load_ends_first()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-restored-apart-"));
        DeleteAtEnd(folder);
        using StandInFeed feed = new();
        feed.Add("Passenger.Sample", "1.0.0", new Dictionary<string, string>
        {
            ["build/Passenger.Sample.targets"] = "<Project>\n  <ItemGroup>\n    <Compile Include=\"$(MSBuildThisFileDirectory)Brought.cs\" />\n  </ItemGroup>\n</Project>\n",
            ["build/Brought.cs"] = "namespace Sample;\n\npublic static class Brought\n{\n    public static string Say() => \"brought\";\n}\n",
        });
        TestFolders.Write(folder, "nuget.config", $"<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"stand-in\" value=\"{feed.Index}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />\n"
            + "  </packageSources>\n  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        string uses = TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Uses/Caller.cs", "namespace Uses;\n\npublic static class Caller\n{\n    public static string Call() => Sample.Brought.Say();\n}\n");
        string lacks = TestFolders.Write(folder, "Lacks/Lacks.csproj", TestProjects.Library());
        MainWindow window = AgentsWindow(folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, () => $"the language server to load Uses and Lacks ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        TaskCompletionSource sampling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource answering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource telling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reloading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        global::Passenger.App.CSharp.CSharpService csharp = window.CSharpService!;
        try
        {
            // The server hears of Uses's files, and the window asks it to load Uses again, only once the definition waits.
            string usesFolder = Path.GetDirectoryName(uses)!;
            csharp.Session!.Server.WatchedFiles.HoldTelling(path => PathNames.IsUnder(path, usesFolder), telling.Task);
            feed.Hold("Passenger.Sample", sampling.Task);
            TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
            Ui.PumpUntil(() => feed.Requests.Any(r => r.Path.StartsWith("/flat/passenger.sample/", StringComparison.Ordinal)), "Uses's restore to ask the feed for its package", AgentLimit);
            csharp.HoldReloads(uses, reloading.Task);
            // Lacks's change waits in the server's queue as Uses's restore runs, so it loads in a batch of its own after it.
            TestFolders.Write(folder, "Lacks/Lacks.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Missing\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
            Ui.PumpUntil(() => csharp.Session.Server.WatchedFiles.ToldAt(lacks) is not null, "the server to be told Lacks.csproj changed as Uses's restore runs", AgentLimit);
            feed.Hold("Passenger.Missing", answering.Task);
            sampling.SetResult();
            Ui.PumpUntil(() => feed.Requests.Any(r => r.Path.StartsWith("/flat/passenger.missing/", StringComparison.Ordinal)), "Lacks's restore to ask the feed for its package", AgentLimit);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, new() { ["path"] = "Uses/Caller.cs", ["line"] = 5, ["column"] = 43 }, heard: heard));
            Ui.PumpUntil(() => heard.Told.Count > 0 || asked.IsCompleted, "the agent's definition to be under way", AgentLimit);
            answering.SetResult();
            Ui.PumpUntil(() => asked.IsCompleted || heard.Told.Any(t => t.Message?.Contains("to load Uses again with what its restore brought", StringComparison.Ordinal) == true),
                "the agent's definition to wait for Uses to load again after its restore", AgentLimit);
            telling.SetResult();
            reloading.SetResult();
            string said = Text(Ui.AwaitPumped(asked, "the agent's definition of Brought", AgentLimit));
            Assert.True(said.Contains("/Brought.cs", StringComparison.Ordinal) || said.Contains($"{Path.DirectorySeparatorChar}Brought.cs", StringComparison.Ordinal),
                $"definition of a class a restore brought, asked as another project's load ended between that restore and its project's load again, answered: {said}");
        }
        finally
        {
            sampling.TrySetResult();
            answering.TrySetResult();
            telling.TrySetResult();
            reloading.TrySetResult();
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A project's package written again as the same version range in other words: the server restores it, the
    /// restore leaving its assets file as it was, and (the window's own load again held back) never loads it again. Once the
    /// server has said nothing of its loads for its quiet time that wait is let go for good, so a later load of another
    /// project, its design-time build held here, keeps no agent's question waiting for a load that will never come:
    /// definition of what the first restore brought answers while that build is held.</summary>
    private void An_agents_question_after_a_restore_that_left_its_assets_as_they_were_waits_for_no_later_load()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-restored-same-"));
        DeleteAtEnd(folder);
        using StandInFeed feed = new();
        feed.Add("Passenger.Sample", "1.0.0", new Dictionary<string, string>
        {
            ["build/Passenger.Sample.targets"] = "<Project>\n  <ItemGroup>\n    <Compile Include=\"$(MSBuildThisFileDirectory)Brought.cs\" />\n  </ItemGroup>\n</Project>\n",
            ["build/Brought.cs"] = "namespace Sample;\n\npublic static class Brought\n{\n    public static string Say() => \"brought\";\n}\n",
        });
        TestFolders.Write(folder, "nuget.config", $"<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"stand-in\" value=\"{feed.Index}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />\n"
            + "  </packageSources>\n  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        string sample = "  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.0\" />\n  </ItemGroup>\n";
        string uses = TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library().With(sample));
        string caller = TestFolders.Write(folder, "Uses/Caller.cs", "namespace Uses;\n\npublic static class Caller\n{\n    public static string Call() => Sample.Brought.Say();\n}\n");
        string other = TestFolders.Write(folder, "Other/Other.csproj", TestProjects.Library());
        string assets = Path.Combine(folder, "Uses", "obj", "project.assets.json");
        MainWindow window = AgentsWindow(folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, () => $"the language server to load Uses and Other ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        TaskCompletionSource building = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource keeping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        global::Passenger.App.CSharp.CSharpService csharp = window.CSharpService!;
        Dictionary<string, object?> brought = new() { ["path"] = "Uses/Caller.cs", ["line"] = 5, ["column"] = 43 };
        try
        {
            // Asked first, so the server's first restore of Uses and its load after it are over.
            string first = Text(Ui.AwaitPumped(Waiting.Apart(() => Result(client, McpTools.Definition, brought)), "the agent's first definition of Brought", AgentLimit));
            Assert.True(first.Contains("Brought.cs", StringComparison.Ordinal), $"definition of a class Uses's first restore brought answered: {first}");
            DateTime written = File.GetLastWriteTimeUtc(assets);

            // The window's own load again of Uses, which its evaluation of the change asks for, held: only the server's comes.
            csharp.HoldReloads(uses, keeping.Task);
            TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library().With(sample.Replace("Version=\"1.0.0\"", "Version=\"[1.0.0, )\"", StringComparison.Ordinal)));
            Ui.PumpUntil(() => csharp.ReloadingWhat(caller)?.Contains("to load Uses again with what its restore brought", StringComparison.Ordinal) == true,
                "the server to restore Uses once its package was written again", AgentLimit);
            Assert.True(File.GetLastWriteTimeUtc(assets) == written, "The restore of the same package written again wrote Uses's assets file again, so the server loads Uses again after all");
            Ui.PumpUntil(() => csharp.ReloadingWhat(caller) is null, "the wait for a load of Uses to be let go once the server said nothing of its loads for its quiet time", AgentLimit);

            Task held = window.Session.DesignTimeBuilds.HoldNextAsked(other, building.Task);
            TestFolders.Write(folder, "Other/Other.csproj", TestProjects.Library().With("  <PropertyGroup>\n    <Description>Changed</Description>\n  </PropertyGroup>\n"));
            Ui.PumpUntil(() => held.IsCompleted, "the server's load of Other to ask for its design-time build, held", AgentLimit);
            Heard heard = new();
            Task<CallToolResult> asked = Waiting.Apart(() => Result(client, McpTools.Definition, brought, heard: heard));
            string said = Text(Ui.AwaitPumped(asked, "the agent's definition of Brought, asked while Other's design-time build is held", AgentLimit));
            Assert.True(said.Contains("Brought.cs", StringComparison.Ordinal) && !building.Task.IsCompleted,
                $"definition of a class a restore brought, asked once the wait for Uses's load had been let go and while another project's build was held, answered: {said}"
                + $" (last told: {heard.Told.LastOrDefault()?.Message})");
            Assert.DoesNotContain(heard.Told, t => t.Message?.Contains("to load Uses again", StringComparison.Ordinal) == true);
        }
        finally
        {
            building.TrySetResult();
            keeping.TrySetResult();
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's question of an open file costs the person's window what it asks, not the file's size: the disk,
    /// which the agent's own file tools may have written, is looked at off the UI thread and the file read only once its
    /// length or time changed, so editor_text of one line of an open 20,000-line file allocates on the UI thread about what
    /// one of a ten-line file does; and the agent's own write is still taken before its next question.</summary>
    private void An_agents_question_of_an_open_file_costs_the_window_what_it_asks_not_the_files_size()
    {
        string small = Path.Combine(Folder, "Small.txt");
        string large = Path.Combine(Folder, "Large.txt");
        File.WriteAllText(small, string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i} of a short file\n")));
        File.WriteAllText(large, string.Concat(Enumerable.Range(1, 20_000).Select(i => $"line {i} of a long file, some words to make it as long as code is\n")));
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        Assert.Null(window.OpenFile(small));
        Assert.Null(window.OpenFile(large));
        Ui.Settle();
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            long few = Costs(small);
            long many = Costs(large);
            Assert.True(many < few + 64 * 1024,
                $"editor_text of an open 20,000-line file allocated {many / 1024} KB on the UI thread, of a ten-line file {few / 1024} KB: the file is read again there at each question");

            // As the agent's own file tools write.
            File.WriteAllText(large, "written by the agent's own tools\n" + File.ReadAllText(large));
            string said = Text(Await(Waiting.Apart(() => Result(client, McpTools.EditorText, new() { ["path"] = large, ["from"] = 1, ["to"] = 1 })), "the question after the agent's write"));
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
                CallToolResult answer = Await(Waiting.Apart(() => Result(client, McpTools.EditorText, new() { ["path"] = path, ["from"] = 1, ["to"] = 1 })), "the agent's question of the file");
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.Contains("line 1 of", Text(answer), StringComparison.Ordinal);
            }
            return least;
        }
    }

    /// <summary>A fault of the IDE's met by an agent's call, stood in for by an action that fails as no action should.</summary>
    private void An_agents_call_that_meets_a_fault_of_the_IDEs_is_answered_with_it_and_logged()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        window.Actions.Add(new IdeAction("tests.fault", "Fault", "Tests", _ => throw new NullReferenceException("The fault's own words")));
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> asking = Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = "tests.fault" }));
            Press(Question(window, "the question for the agent's action"), McpWords.AllowForSession.English);
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

    /// <summary>An agent's call that fails as a file can (an action whose file another program holds), which the agent hears
    /// in the failure's own words alone, leaves the failure in passenger.log whole: the tool, the exception and the frames it
    /// was thrown through.</summary>
    private void An_agents_call_that_fails_leaves_its_exception_and_stack_in_the_log()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        window.Actions.Add(new IdeAction("tests.held", "Held", "Tests", _ => ThrowAsHeld()));
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<CallToolResult> asking = Waiting.Apart(() => Result(client, McpTools.IdeAction, new() { ["action"] = "tests.held" }));
            Press(Question(window, "the question for the agent's action"), McpWords.AllowForSession.English);
            CallToolResult failed = Await(asking, "the action that fails");
            Assert.True(failed.IsError == true && Text(failed).Contains("The file is held by another program", StringComparison.Ordinal),
                $"An agent's call that failed was answered without the failure's words: {Text(failed)}");
            IdeErrors.Flush();
            string log = Ui.Shared(IdeErrors.LogPath);
            int at = log.LastIndexOf($"An agent's {McpTools.IdeAction} failed: ", StringComparison.Ordinal);
            string logged = at < 0 ? string.Empty : log[at..];
            Assert.True(logged.Contains("System.IO.IOException: The file is held by another program", StringComparison.Ordinal)
                && logged.Contains(nameof(ThrowAsHeld), StringComparison.Ordinal),
                $"An agent's call that failed left no exception with its stack in passenger.log: {(at < 0 ? "no line names the call" : logged)}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }

        static void ThrowAsHeld() => throw new IOException("The file is held by another program");
    }

    /// <summary>An agent's remove of "Any exception", the row every folder keeps, turns it off: its answer and the
    /// Breakpoints window show it off, so the program no longer breaks on every exception.</summary>
    private void An_agents_remove_of_any_exception_turns_it_off_in_its_answer_and_the_breakpoints_window()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        ExceptionBreakpoint any = window.Debugger.Breakpoints.FindException(ExceptionBreakpoint.AnyException)!;
        Assert.True(any.Enabled, "Any exception was off before the agent removed it");
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> removing = Waiting.Apart(() => Call(client, McpTools.BreakpointSet, new() { ["exception"] = ExceptionBreakpoint.AnyException, ["remove"] = true }));
            Press(Question(window, "the question for the agent's breakpoint"), McpWords.AllowForSession.English);
            JsonElement answer = Await(removing, "the agent's remove of Any exception");
            JsonElement listed = answer.EnumerateArray().Single(b => b.TryGetProperty("exception", out JsonElement type) && type.GetString() == ExceptionBreakpoint.AnyException);
            Assert.False(listed.GetProperty("enabled").GetBoolean(), $"The agent's remove answered Any exception still on: {listed}");
            Assert.False(any.Enabled, "The agent's remove left Any exception on: the program still breaks on every exception");

            Ui.Run(window, AppActions.ViewBreakpoints);
            BreakpointsWindow breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
            Avalonia.Controls.CheckBox box = breakpoints.GetVisualDescendants().OfType<Avalonia.Controls.ListBoxItem>()
                .Single(i => (i.Tag as Breakpoint)?.Location == ExceptionBreakpoint.AnyException)
                .GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().First();
            Assert.False(box.IsChecked == true, "The Breakpoints window shows Any exception on after the agent removed it");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    private void An_agent_attached_to_the_window_sets_a_breakpoint_the_person_sees()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        string program = Path.Combine(window.Folder!, "Program.cs");
        window.OpenFile(program);
        Ui.Settle();
        string socket = window.AgentSocket!;

        // The agent's calls run on the UI thread, which the test keeps running; it stays until the window is looked at.
        TaskCompletionSource looked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> agent = Waiting.Apart(async () =>
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
        Press(Question(window, "the question for the agent's breakpoint"), McpWords.AllowForSession.English);
        looked.TrySetResult();
        WaitFor(() => agent.IsCompleted, "the agent's calls", AgentLimit);
        Assert.Equal(1, shown);
        WaitFor(() => window.Agents == 0, "the window to see the agent go");
        Assert.Equal(window.Folder, Await(agent, "the agent's calls"));
        Assert.Equal(3, Assert.Single(window.Debugger.Breakpoints.In(program)).Line);
        Assert.Contains(new LineMark(3, LineMarkKind.Breakpoint), window.EditorArea.CodeEditor.LineMarks);

        window.Close();
        Assert.False(File.Exists(socket), "A closed window left its socket for agents to try");
    }

    private void With_the_MCP_server_off_the_window_asks_to_let_an_agent_in_on_it_lets_agents_in_unasked_and_off_again_shuts_them_out()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        window.Show();
        window.KeepSettings();
        window.OpenFolder(Folder);
        Ui.Settle();

        string socket = window.AgentSocket ?? throw new InvalidOperationException($"The window does not listen for agents: {window.Status}");
        Assert.Equal([socket], Sockets());
        // Taken here, on the UI thread, where the folder is restored.
        string taken = Folder;
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", taken])), "the agent attaching");
        McpClient? second = null;
        try
        {
            Task<JsonElement> asking = Waiting.Apart(() => Call(client, McpTools.Workspace));
            AgentAdmissionDialog asked = Admission(window, "the window to ask whether the agent may work there");
            Assert.Equal(0, window.Agents);
            Press(asked, McpWords.Allow.English);
            Assert.Equal(window.Folder, Await(asking, "the agent's workspace call").GetProperty("folder").GetString());
            WaitFor(() => window.AgentStatus == "Agent connected", "the status bar to say an agent is connected");

            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Query = "agents";
            Ui.Settle();
            Assert.Equal([SettingsDialog.AgentsPage], dialog.Pages);
            Assert.Equal("Enable MCP Server", dialog.Shown[0]);
            dialog.Change(s => s with { McpServer = true });
            dialog.Ok();
            Ui.Settle();
            second = Await(Waiting.Apart(() => Start(["--mcp-attach", taken])), "a second agent attaching");
            Assert.Equal(window.Folder, Await(Waiting.Apart(() => Call(second, McpTools.Workspace)), "the second agent's call").GetProperty("folder").GetString());
            Assert.Empty(window.OwnedWindows.OfType<AgentAdmissionDialog>());
            WaitFor(() => window.AgentStatus == "2 agents connected", "the status bar to count both agents");

            window.ApplySettings(window.Settings with { McpServer = false });
            WaitFor(() => window.AgentStatus is null, "the status bar to say no agent is on the window");
            Assert.True(File.Exists(socket), "Turning the MCP server off closed the window's socket, where it is to ask instead");
            Task<CallToolResult> again = Waiting.Apart(() => Result(client, McpTools.Workspace));
            Press(Admission(window, "the window to ask again once agents were shut out"), McpWords.DontAllow.English);
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

    private void An_agent_asks_before_its_first_run_and_the_window_stops_waiting_when_it_gives_up_or_goes()
    {
        Write("Program.cs", "Console.WriteLine(\"waiting\");\nThread.Sleep(Timeout.Infinite);\n");
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        Dictionary<string, object?> run = new() { ["waitSeconds"] = 600 };
        try
        {
            Task<CallToolResult> denied = Waiting.Apart(() => Result(client, McpTools.Run, run));
            Avalonia.Controls.Window asked = Question(window, "the question for the agent's run");
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
            Task<CallToolResult> allowed = Waiting.Apart(() => CallGivingUp(client, McpTools.Run, run, giveUp.Token));
            Press(Question(window, "the question asked again"), McpWords.AllowForSession.English);
            WaitFor(() => window.Runner.IsRunning && window.AgentStatus == "Agent: Run a configuration", "the agent's run in the status bar");

            giveUp.Cancel();
            WaitFor(() => allowed.IsCompleted, "the call given up");
            WaitFor(() => window.AgentStatus == "Agent connected", "the window to stop waiting on the run the agent gave up on");
            Assert.True(window.Runner.IsRunning, "Giving up on the call stopped the program");
            window.Runner.Stop();
            WaitFor(() => !window.Runner.IsRunning, "the program stopped");

            Task<CallToolResult> again = Waiting.Apart(() => Result(client, McpTools.Run, run));
            WaitFor(() => window.Runner.IsRunning || Questions(window).Any(), "the run allowed before");
            Assert.Empty(Questions(window));
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
    private void Each_solution_window_has_its_own_socket_and_an_agent_attaches_to_the_one_over_its_folder()
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
    private void An_agents_vcs_log_with_a_path_lists_the_commits_show_history_does_renames_followed()
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
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement log = Await(Waiting.Apart(() => Call(client, McpTools.VcsLog, new() { ["path"] = Path.Combine("src", "Store.cs") })), "the agent's vcs_log of the file");
            Assert.Equal(history.Rows.Select(r => r.Commit.Short), log.EnumerateArray().Select(c => c.GetProperty("hash").GetString()));
            Assert.Equal([Path.Combine("src", "Store.cs"), Path.Combine("src", "Shop.cs"), Path.Combine("src", "Shop.cs")],
                log.EnumerateArray().Select(c => c.GetProperty("changes")[0].GetProperty("path").GetString()));
            Assert.Equal("renamed", log[0].GetProperty("changes")[0].GetProperty("kind").GetString());

            JsonElement changed = Await(Waiting.Apart(() => Call(client, McpTools.VcsLog, new() { ["path"] = Path.Combine("src", "Store.cs"), ["startLine"] = 5, ["endLine"] = 5 })),
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

    /// <summary>An agent's push to a remote that asks for a login, on a window the person is at, puts the question to them,
    /// saying the answer goes to git and never to the agent, the agent's call waiting; once they sign in, the push goes and
    /// the agent is told so.</summary>
    private void An_agents_push_to_a_remote_that_asks_for_a_login_asks_the_person_at_the_window()
    {
        string repository = TestFolders.Create("mcp-login-");
        DeleteAtEnd(repository);
        string served = TestFolders.Create("mcp-served-");
        DeleteAtEnd(served);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        TestGit.Init(repository);
        using StandInGitRemote remote = new(served);
        TestGit.Run(served, "init", "-q", "--bare", "repo.git");
        TestGit.Run(repository, "remote", "add", "origin", remote.Url);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\n");
        MainWindow window = AgentsWindow(repository, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> committing = Waiting.Apart(() => Call(client, McpTools.VcsCommit, new() { ["message"] = "Two", ["push"] = true }));
            Press(Question(window, "the question for the agent's vcs_commit"), McpWords.AllowForSession.English);
            WaitFor(() => window.GitQuestion is { IsVisible: true }, "the window to ask the person for the login the push needs");
            Passenger.App.Vcs.GitQuestionDialog asked = window.GitQuestion!;
            Assert.Equal($"Sign in to {remote.Place}", asked.Title);
            Assert.Contains(Texts(asked), t => t.Contains("the answer goes to git, never to the agent", StringComparison.Ordinal));
            Assert.False(committing.IsCompleted, "The agent's call ended before the person gave the login");
            Ui.Click(Field(asked, "GitUser"));
            asked.KeyTextInput(StandInGitRemote.User);
            Ui.Click(Field(asked, "GitSecret"));
            asked.KeyTextInput(StandInGitRemote.Password);
            Press(asked, "Sign In");
            string answered = Await(committing, "the agent's commit and push").GetString()!;
            Assert.EndsWith(" Two; Pushed main", answered, StringComparison.Ordinal);
            Assert.Equal(TestGit.Run(repository, "rev-parse", "HEAD").Trim(), TestGit.Run(remote.Repository, "rev-parse", "main").Trim());
        }
        finally
        {
            Gone(client);
            window.Close();
        }

        static Avalonia.Controls.TextBox Field(Avalonia.Controls.Window dialog, string name) =>
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.TextBox>().Single(t => t.Name == name);
    }

    /// <summary>The folder of the window an agent started in a folder reaches with <c>--mcp-attach</c>.</summary>
    private string? AttachedFolder(string workingDirectory) => Await(Waiting.Apart(async () =>
    {
        await using McpClient client = await Start(["--mcp-attach"], workingDirectory);
        return (await Call(client, McpTools.Workspace)).GetProperty("folder").GetString();
    }), $"an agent attaching from {workingDirectory}");

    /// <summary>An agent's terminal command runs in a shell of the agents' own, its Agent Shell tab, while the person's tab
    /// runs cat: what cat reads is left untouched, the person's tab stays the one shown, and the agent hears what its
    /// command printed. Before, the command was typed into whatever the shown tab ran.</summary>
    private void An_agents_terminal_command_runs_in_a_shell_of_its_own_and_never_reaches_the_persons_program()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
        Ui.Run(window, AppActions.ShowTerminal);
        Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the person's shell to start");
        Terminal.TerminalSession persons = window.TerminalPanel.Active!;
        Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the person's terminal");
        string reading = OperatingSystem.IsWindows() ? "more" : "cat";
        window.KeyTextInput(reading);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => TerminalTests.Screen(persons).Contains(reading, StringComparison.Ordinal), $"{reading} typed in the person's terminal");
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> running = Waiting.Apart(() => Call(client, McpTools.TerminalRun, new() { ["command"] = "echo agent-shell", ["waitSeconds"] = 5 }));
            Press(Question(window, "the question for the agent's terminal command"), McpWords.AllowForSession.English);
            string answer = Await(running, "the agent's terminal command").GetString()!;
            Assert.Contains("agent-shell", answer, StringComparison.Ordinal);
            Ui.Settle();
            Assert.DoesNotContain("agent-shell", TerminalTests.Screen(persons), StringComparison.Ordinal);
            Assert.Equal("Local", window.TerminalPanel.ActiveTab?.Name);
            Assert.True(window.TerminalPanel.AgentShell is { } own && own != persons && TerminalTests.Screen(own).Contains("agent-shell", StringComparison.Ordinal),
                $"The agent's command did not run in the Agent Shell tab: {string.Join(", ", window.TerminalPanel.Tabs.Select(t => t.Name))}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>While a person's terminal configuration runs a program reading its input, an attached agent's terminal
    /// commands run in the Agent Shell, the program's screen untouched and its tab still shown; a command asked for while
    /// the Agent Shell starts waits for that start, which leaves one Agent Shell tab; a shell that could not start is said
    /// to the agent and leaves at most one tab, which the next start takes the place of; and once the agent runs the
    /// configuration itself, its program takes the agent's line.</summary>
    private void A_persons_program_in_the_terminal_never_takes_an_agents_command_and_the_window_keeps_one_agent_shell()
    {
        MainWindow window = AgentsWindow(Folder, languageServer: false);
        global::Passenger.App.ToolWindows.TerminalPanel terminal = window.TerminalPanel;
        (string App, string[] Arguments) plain = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
        // The Agent Shell's shell prompts only once the test puts agent-go in the folder (a minute at most).
        terminal.Shell = OperatingSystem.IsWindows()
            ? ("powershell.exe", ["-NoProfile", "-Command", "$n = 0; while (-not (Test-Path agent-go) -and $n -lt 600) { Start-Sleep -Milliseconds 100; $n++ }; cmd.exe"])
            : ("/bin/sh", ["-c", "n=0; while [ ! -f agent-go ] && [ $n -lt 600 ]; do sleep 0.1; n=$((n+1)); done; exec /bin/sh"]);
        window.SetConfigurations([new RunConfiguration
        {
            Name = "Reading",
            Project = Path.Combine(window.Folder!, "Sum.csproj"),
            Program = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/cat",
            WorkingDirectory = window.Folder!,
            BuildBeforeLaunch = false,
            Console = ConsoleKind.Terminal,
        }], "Reading");
        Ui.Launched(window, window.RunAsync(), "the person's program to start", TimeSpan.FromMinutes(1));
        TerminalRun persons = Assert.IsType<TerminalRun>(window.RunInTerminal);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> first = Waiting.Apart(() => Call(client, McpTools.TerminalRun, new() { ["command"] = "echo agent-one", ["waitSeconds"] = 5 }));
            Press(Question(window, "the question for the agent's terminal command"), McpWords.AllowForSession.English);
            WaitFor(() => terminal.AgentShell is not null || first.IsCompleted, "the Agent Shell to start for the agent's first command");
            Assert.True(terminal.AgentShell is not null, $"The agent's command ran in no Agent Shell, the person's program's screen:\n{TerminalTests.Screen(persons.Session)}");
            Heard heard = new();
            Task<CallToolResult> second = Waiting.Apart(() => Result(client, McpTools.TerminalRun, new() { ["command"] = "echo agent-two", ["waitSeconds"] = 5 }, heard: heard));
            WaitFor(() => heard.Told.Any(t => t.Message?.Contains("Agent Shell", StringComparison.Ordinal) == true), "the agent's second command to wait for the Agent Shell's start");
            File.WriteAllText(Path.Combine(window.Folder!, "agent-go"), string.Empty);
            Assert.Contains("agent-one", Await(first, "the agent's first terminal command").GetString()!, StringComparison.Ordinal);
            CallToolResult two = Await(second, "the agent's second terminal command");
            Assert.True(two.IsError != true && Text(two).Contains("agent-two", StringComparison.Ordinal), $"The agent's second terminal command answered: {Text(two)}");
            Ui.Settle();
            Assert.True(AgentShells(terminal) == 1, $"Two commands as the Agent Shell started left its tabs: {Tabs(terminal)}");
            Assert.True(TerminalTests.Screen(terminal.AgentShell!).Contains("agent-two", StringComparison.Ordinal), $"The agent's commands did not run in the Agent Shell: {Tabs(terminal)}");
            Assert.True(persons.IsRunning && !TerminalTests.Screen(persons.Session).Contains("agent-", StringComparison.Ordinal),
                $"The agent's commands reached the person's program, which reads its input:\n{TerminalTests.Screen(persons.Session)}");
            Assert.Equal("Reading", terminal.ActiveTab?.Name);

            Task<JsonElement> exiting = Waiting.Apart(() => Call(client, McpTools.TerminalRun, new() { ["command"] = "exit", ["waitSeconds"] = 1 }));
            WaitFor(() => terminal.AgentShell is null, "the Agent Shell to end at the agent's exit");
            Await(exiting, "the agent's exit");
            // One that is no program ends as it starts on macOS and Linux, and fails to start on Windows; one named by nothing fails to start.
            foreach ((string App, string[] Arguments) failing in ((string, string[])[])[(Path.Combine(window.Folder!, "no-such-shell"), []), (string.Empty, [])])
            {
                terminal.Shell = failing;
                for (int call = 0; call < 2; call++)
                {
                    CallToolResult failed = Await(Waiting.Apart(() => Result(client, McpTools.TerminalRun, new() { ["command"] = "echo agent-three", ["waitSeconds"] = 1 })),
                        $"the agent's command with '{failing.App}' for a shell");
                    Assert.True(failed.IsError == true && Text(failed).Contains($"'{failing.App}'", StringComparison.Ordinal), $"A command whose Agent Shell could not start answered: {Text(failed)}");
                    Ui.Settle();
                    Assert.True(AgentShells(terminal) <= 1, $"Agent Shells that could not start left their tabs: {Tabs(terminal)}");
                }
            }
            Assert.True(AgentShells(terminal) == 1 && terminal.AgentShell is { IsRunning: false } && TerminalTests.Screen(terminal.AgentShell).Contains("could not start", StringComparison.Ordinal),
                $"The Agent Shell that could not start left no tab saying why: {Tabs(terminal)}");
            terminal.Shell = plain;
            string again = Await(Waiting.Apart(() => Call(client, McpTools.TerminalRun, new() { ["command"] = "echo agent-again", ["waitSeconds"] = 5 })), "the agent's command once a shell starts").GetString()!;
            Assert.Contains("agent-again", again, StringComparison.Ordinal);
            Ui.Settle();
            Assert.True(AgentShells(terminal) == 1 && terminal.AgentShell is { IsRunning: true }, $"The Agent Shell started after one that could not left its tabs: {Tabs(terminal)}");
            Assert.True(persons.IsRunning && !TerminalTests.Screen(persons.Session).Contains("agent-", StringComparison.Ordinal),
                $"The agent's commands reached the person's program, which reads its input:\n{TerminalTests.Screen(persons.Session)}");

            // The agent's own run of the configuration, in place of the person's, takes the agent's line.
            Asked(window, client, McpTools.Run, "run a configuration", new() { ["waitSeconds"] = 1 });
            TerminalRun owns = Assert.IsType<TerminalRun>(window.RunInTerminal);
            Assert.True(owns != persons && owns.IsRunning, "The agent's run of the configuration did not start its own program");
            Assert.Contains("agent-own", Await(Waiting.Apart(() => Call(client, McpTools.TerminalRun, new() { ["command"] = "echo agent-own", ["waitSeconds"] = 2 })), "the agent's command to its own program").GetString()!,
                StringComparison.Ordinal);
            Assert.True(TerminalTests.Screen(owns.Session).Contains("agent-own", StringComparison.Ordinal) && !TerminalTests.Screen(terminal.AgentShell!).Contains("agent-own", StringComparison.Ordinal),
                $"The agent's command did not reach the program its own run started:\n{TerminalTests.Screen(owns.Session)}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }

        static int AgentShells(global::Passenger.App.ToolWindows.TerminalPanel terminal) =>
            terminal.Tabs.Count(t => t.Name == global::Passenger.App.ToolWindows.TerminalPanel.AgentShellName);

        static string Tabs(global::Passenger.App.ToolWindows.TerminalPanel terminal) => string.Join(", ", terminal.Tabs.Select(t => t.Name));
    }
}
