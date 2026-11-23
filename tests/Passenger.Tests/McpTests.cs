using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Passenger as an agent's IDE with no window (<c>--mcp</c>), over the Model Context Protocol with the SDK's own
/// client: a project worked through it (its tests, packages, actions, publish, Git and the terminal), a folder given it in
/// other letter case, a start or a load that runs long, and an assembly written into .NET's packs that no project references,
/// which the language server never hears of.</summary>
[AboutMacOS]
public sealed class McpTests : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    /// <summary>The gate's core tools as one flow: the tests listed as the agent starts, its own Passenger opening its
    /// folder while its window is built, a call with a parameter its tool lacks told which, and every action listed,
    /// one that cannot run saying why, each step the gate test it names in an instance of its own.</summary>
    [Gate]
    [Fact]
    public async Task An_agents_Passenger_lists_its_tests_and_actions_takes_its_window_up_and_says_what_a_call_lacks()
    {
        await Flow.StepApartAsync("an agent that lists the tests as it starts gets those the test projects code declares", () => new McpTests(restored), t => t.An_agent_that_lists_the_tests_as_it_starts_gets_those_the_test_projects_code_declares());
        await Flow.StepApartAsync("an agents own passenger opens its folder while its window is built which takes it up and does none of it again", () => new McpTests(restored), t => t.An_agents_own_passenger_opens_its_folder_while_its_window_is_built_which_takes_it_up_and_does_none_of_it_again());
        await Flow.StepApartAsync("an agents call with a parameter its tool lacks or misses hears which and what the tool takes", () => new McpTests(restored), t => t.An_agents_call_with_a_parameter_its_tool_lacks_or_misses_hears_which_and_what_the_tool_takes());
        await Flow.StepApartAsync("an agent lists every action the ide declares and one it runs says why when it cannot", () => new McpTests(restored), t => t.An_agent_lists_every_action_the_ide_declares_and_one_it_runs_says_why_when_it_cannot());
    }

    /// <summary>The gate's build, run and publish as one flow: a file-based program built and run as a project of its
    /// own, a run refused for anything but its own failed build naming no build errors, and a publish told the folder
    /// it landed in or why not, each step the gate test it names in an instance of its own.</summary>
    [Gate]
    [Fact]
    public async Task An_agent_builds_runs_and_publishes_hearing_its_own_build_errors_alone()
    {
        await Flow.StepApartAsync("an agent builds and runs a file based program as a project of its own", () => new McpTests(restored), t => t.An_agent_builds_and_runs_a_file_based_program_as_a_project_of_its_own());
        await Flow.StepApartAsync("an agents run refused for anything but its own failed build names no build errors", () => new McpTests(restored), t => t.An_agents_run_refused_for_anything_but_its_own_failed_build_names_no_build_errors());
        await Flow.StepApartAsync("an agent publishes a project and is told the folder it landed in or why not", () => new McpTests(restored), t => t.An_agent_publishes_a_project_and_is_told_the_folder_it_landed_in_or_why_not());
    }

    /// <summary>The gate's Git as one flow: changes read and committed, a merge's conflict resolved and a command run
    /// in the IDE's terminal, and a push the remote refuses said as an error naming the refusal while the commit stays
    /// made, each step the gate test it names in an instance of its own.</summary>
    [Gate]
    [Fact]
    public async Task An_agent_reads_commits_merges_and_pushes_through_Git_hearing_a_refusal_in_its_words()
    {
        await Flow.StepApartAsync("an agent reads and commits changes resolves a merges conflict and runs a command in the ides terminal", () => new McpTests(restored), t => t.An_agent_reads_and_commits_changes_resolves_a_merges_conflict_and_runs_a_command_in_the_ides_terminal());
        await Flow.StepApartAsync("an agents push the remote refuses is an error naming the refusal and the commit stays made", () => new McpTests(restored), t => t.An_agents_push_the_remote_refuses_is_an_error_naming_the_refusal_and_the_commit_stays_made());
    }

    /// <summary>An agent's test runs as one flow, each step one test's check in an instance of its own: a passing run
    /// after a failure answering no failure it did not run, the Tests actions answering what the run came to, a test
    /// written and run at once by its class's name, a failing test's 200,000 lines held to its last, one failing row
    /// among 500 named, and a test reading standard input reaching its end.</summary>
    [Fact]
    public async Task An_agents_test_runs_answer_what_each_came_to_held_to_what_an_answer_carries()
    {
        await Flow.StepApartAsync("an agents run of a passing test after a failure answers no failure of a test it did not run", () => new McpTests(restored), t => t.An_agents_run_of_a_passing_test_after_a_failure_answers_no_failure_of_a_test_it_did_not_run());
        await Flow.StepApartAsync("an agents unit test actions answer what the run came to", () => new McpTests(restored), t => t.An_agents_unit_test_actions_answer_what_the_run_came_to());
        await Flow.StepApartAsync("an agents test written and run at once by its classs name runs and its failure is told", () => new McpTests(restored), t => t.An_agents_test_written_and_run_at_once_by_its_classs_name_runs_and_its_failure_is_told());
        await Flow.StepApartAsync("an agents run of a failing test that wrote 200000 lines answers its last lines and says the rest was left out", () => new McpTests(restored), t => t.An_agents_run_of_a_failing_test_that_wrote_200000_lines_answers_its_last_lines_and_says_the_rest_was_left_out());
        await Flow.StepApartAsync("an agents run of a theory with one failing row among 500 names that row and counts the rest", () => new McpTests(restored), t => t.An_agents_run_of_a_theory_with_one_failing_row_among_500_names_that_row_and_counts_the_rest());
        await Flow.StepApartAsync("an agents test that reads standard input reads its end and the agents next call is answered", () => new McpTests(restored), t => t.An_agents_test_that_reads_standard_input_reads_its_end_and_the_agents_next_call_is_answered());
    }

    /// <summary>An agent's workspace as one flow, each step one test's check in an instance of its own: an SDK
    /// global.json asks for and no machine has said by workspace and run, its own Passenger trusting its folder,
    /// projects with no SDK, one that did not load and a solution that does not read each said why, a project file it
    /// changed waited for, and a solution filter told with what it leaves out.</summary>
    [Fact]
    public async Task An_agent_hears_why_a_workspace_or_project_did_not_load_and_waits_for_one_it_changed()
    {
        await Flow.StepApartAsync("an agent where global json asks for an SDK not installed hears so from workspace and run", () => new McpTests(restored), t => t.An_agent_where_global_json_asks_for_an_SDK_not_installed_hears_so_from_workspace_and_run());
        await Flow.StepApartAsync("an agents own Passenger trusts the folder it was started on and says so", () => new McpTests(restored), t => t.An_agents_own_Passenger_trusts_the_folder_it_was_started_on_and_says_so());
        await Flow.StepApartAsync("an agent whose projects have no SDK or whose language server could not start hears why from each code tool at once rather than nothing found", () => new McpTests(restored), t => t.An_agent_whose_projects_have_no_SDK_or_whose_language_server_could_not_start_hears_why_from_each_code_tool_at_once_rather_than_nothing_found());
        await Flow.StepApartAsync("an agent asking of a project that did not load hears why and the loaded one answers", () => new McpTests(restored), t => t.An_agent_asking_of_a_project_that_did_not_load_hears_why_and_the_loaded_one_answers());
        await Flow.StepApartAsync("an agents question after its change of a project file waits for the project to load again", () => new McpTests(restored), t => t.An_agents_question_after_its_change_of_a_project_file_waits_for_the_project_to_load_again());
        await Flow.StepApartAsync("an agent on a folder whose solution does not read hears where and why from workspace", () => new McpTests(restored), t => t.An_agent_on_a_folder_whose_solution_does_not_read_hears_where_and_why_from_workspace());
        await Flow.StepApartAsync("an agent on a solution filter is told the filter and the project it leaves out and keeps the program beside it", () => new McpTests(restored), t => t.An_agent_on_a_solution_filter_is_told_the_filter_and_the_project_it_leaves_out_and_keeps_the_program_beside_it());
    }

    /// <summary>Starts and loads that run long as one flow, each step one test's check in an instance of its own: a
    /// load and a head start past their limits said to be under way and ready once done, and a load past its limit
    /// holding no background work once its warning is said.</summary>
    [Fact]
    public async Task A_start_or_load_past_its_limit_is_said_still_under_way_and_ready_once_done()
    {
        await Flow.StepApartAsync("a load past its limit is said to be still loading and once it loads the solution is ready and analysed", () => new McpTests(restored), t => t.A_load_past_its_limit_is_said_to_be_still_loading_and_once_it_loads_the_solution_is_ready_and_analysed());
        await Flow.StepApartAsync("a head start past its limit is said to be still starting and once its builds end the solution is ready and analysed", () => new McpTests(restored), t => t.A_head_start_past_its_limit_is_said_to_be_still_starting_and_once_its_builds_end_the_solution_is_ready_and_analysed());
        await Flow.StepApartAsync("a load past its limit holds no background work once its warning is said", () => new McpTests(restored), t => t.A_load_past_its_limit_holds_no_background_work_once_its_warning_is_said());
    }

    /// <summary>An agent's runs as one flow, each step one test's check in an instance of its own: a program's
    /// characters outside the system's code page answered as written, one needing a terminal said so at a run and at a
    /// debug stop and run in one when asked, a refused run leaving the chosen configuration, a web app answered once it
    /// serves, a breakpoint on a line no code runs on moved, and running programs' command lines masked.</summary>
    [Fact]
    public async Task An_agents_runs_and_debug_starts_answer_as_their_programs_write_and_need_their_secrets_masked()
    {
        await Flow.StepApartAsync("an agents run of a program writing characters outside the systems code page answers each as written", () => new McpTests(restored), t => t.An_agents_run_of_a_program_writing_characters_outside_the_systems_code_page_answers_each_as_written());
        await Flow.StepApartAsync("an agents run of a program needing a terminal says so and terminal true runs it in one", () => new McpTests(restored), t => t.An_agents_run_of_a_program_needing_a_terminal_says_so_and_terminal_true_runs_it_in_one());
        await Flow.StepApartAsync("an agents debug start of a program needing a terminal says so at its stop and terminal true debugs it in one", () => new McpTests(restored), t => t.An_agents_debug_start_of_a_program_needing_a_terminal_says_so_at_its_stop_and_terminal_true_debugs_it_in_one());
        await Flow.StepApartAsync("an agents refused run of another configuration leaves the chosen one and a started run chooses its own", () => new McpTests(restored), t => t.An_agents_refused_run_of_another_configuration_leaves_the_chosen_one_and_a_started_run_chooses_its_own());
        await Flow.StepApartAsync("an agents run of a web app answers once it serves saying where it listens", () => new McpTests(restored), t => t.An_agents_run_of_a_web_app_answers_once_it_serves_saying_where_it_listens());
        await Flow.StepApartAsync("an agents breakpoint on a line no code runs on says where it moves", () => new McpTests(restored), t => t.An_agents_breakpoint_on_a_line_no_code_runs_on_says_where_it_moves());
        await Flow.StepApartAsync("an agents list of running programs masks the secrets their command lines hold", () => new McpTests(restored), t => t.An_agents_list_of_running_programs_masks_the_secrets_their_command_lines_hold());
    }

    /// <summary>An agent's packages as one flow, each step one test's check in an instance of its own: a package
    /// installed and removed shown in the open project file, a search and an install naming vulnerable and deprecated
    /// versions with the version that fixes them, an install for one framework fixing its vulnerable version there
    /// alone, and a lock file or a central versions file named among the files changed, through a parent folder
    /// too.</summary>
    [Fact]
    public async Task An_agents_packages_go_in_and_out_as_each_tool_says_naming_every_file_changed_and_every_vulnerable_version()
    {
        await Flow.StepApartAsync("an agents package installed and removed shows in the open project file as each tool answers", () => new McpTests(restored), t => t.An_agents_package_installed_and_removed_shows_in_the_open_project_file_as_each_tool_answers());
        await Flow.StepApartAsync("an agents package search names the vulnerable and deprecated versions with why", () => new McpTests(restored), t => t.An_agents_package_search_names_the_vulnerable_and_deprecated_versions_with_why());
        await Flow.StepApartAsync("an agents package install of a vulnerable and deprecated version says both and the version that fixes it", () => new McpTests(restored), t => t.An_agents_package_install_of_a_vulnerable_and_deprecated_version_says_both_and_the_version_that_fixes_it());
        await Flow.StepApartAsync("an agents install for one framework fixes the vulnerable version it gets through another package there alone", () => new McpTests(restored), t => t.An_agents_install_for_one_framework_fixes_the_vulnerable_version_it_gets_through_another_package_there_alone());
        await Flow.StepApartAsync("an agents package install in a project restoring with a lock file names the lock file among the files it changed", () => new McpTests(restored), t => t.An_agents_package_install_in_a_project_restoring_with_a_lock_file_names_the_lock_file_among_the_files_it_changed());
        await Flow.StepApartAsync("an agents package install under central versions kept where the build names names that file among those it changed", () => new McpTests(restored), t => t.An_agents_package_install_under_central_versions_kept_where_the_build_names_names_that_file_among_those_it_changed());
        await Flow.StepApartAsync("an agents package install under central versions named through a parent folder writes the version there", () => new McpTests(restored), t => t.An_agents_package_install_under_central_versions_named_through_a_parent_folder_writes_the_version_there());
    }

    /// <summary>An agent's terminal as one flow, each step one test's check in an instance of its own: commands sent at
    /// once taking turns, each answering its own output, a command waited for until the shell prompts and one still
    /// running said so, and what a program and a terminal say reaching the agent with its secrets masked.</summary>
    [Fact]
    public async Task An_agents_terminal_commands_take_turns_each_answering_its_own_output_its_secrets_masked()
    {
        await Flow.StepApartAsync("an agents terminal commands sent at once take turns each answering its own output", () => new McpTests(restored), t => t.An_agents_terminal_commands_sent_at_once_take_turns_each_answering_its_own_output());
        await Flow.StepApartAsync("an agents terminal command is waited for until the shell prompts and one still running says so", () => new McpTests(restored), t => t.An_agents_terminal_command_is_waited_for_until_the_shell_prompts_and_one_still_running_says_so());
        await Flow.StepApartAsync("what a program and a terminal say reaches an agent with its secrets masked", () => new McpTests(restored), t => t.What_a_program_and_a_terminal_say_reaches_an_agent_with_its_secrets_masked());
    }

    /// <summary>An agent's settings and actions as one flow, each step one test's check in an instance of its own:
    /// settings naming each choice and settings_set refusing what Settings refuses, an action that reads before it asks
    /// answering once it asks and one that works saying what it came to, actions narrowed by a query and one there is
    /// not or the person's own refused, the keymap's modifiers and the system's command key, and tools with nothing to
    /// act on saying why.</summary>
    [Fact]
    public async Task An_agents_settings_and_actions_say_what_each_takes_and_came_to()
    {
        await Flow.StepApartAsync("settings names each choice an agent may give and settings set takes one refusing what settings refuses", () => new McpTests(restored), t => t.Settings_names_each_choice_an_agent_may_give_and_settings_set_takes_one_refusing_what_settings_refuses());
        await Flow.StepApartAsync("an agents action that reads before it asks answers once it asks and one that works says what it came to", () => new McpTests(restored), t => t.An_agents_action_that_reads_before_it_asks_answers_once_it_asks_and_one_that_works_says_what_it_came_to());
        await Flow.StepApartAsync("an agents ide actions narrow by its query and ide action refuses one there is not and the persons own", () => new McpTests(restored), t => t.An_agents_ide_actions_narrow_by_its_query_and_ide_action_refuses_one_there_is_not_and_the_persons_own());
        await Flow.StepApartAsync("an agents modifiers are the keymaps words and aliases and its primary the systems command key in a passenger drawn on no screen", () => new McpTests(restored), t => t.An_agents_modifiers_are_the_keymaps_words_and_aliases_and_its_primary_the_systems_command_key_in_a_passenger_drawn_on_no_screen());
        await Flow.StepApartAsync("an agents tools with nothing to act on say why and what would give them something", () => new McpTests(restored), t => t.An_agents_tools_with_nothing_to_act_on_say_why_and_what_would_give_them_something());
    }

    /// <summary>An agent's reads of Git as one flow, each step one test's check in an instance of its own: renames, a
    /// folder's changes committed by its path and a push that did not go, a binary file's and a separator's or mode's
    /// diff, a folder's status and a path naming nothing, lines outside a file refused, what the repository has not
    /// named, a renamed file's diff and history, a repository another user owns, no git on the path, a new repository's
    /// .gitignore, and a push asking a login where nobody is.</summary>
    [Fact]
    public async Task An_agent_reads_Gits_changes_diffs_and_history_as_git_names_them_and_hears_each_refusal_in_its_words()
    {
        await Flow.StepApartAsync("an agent reads a renames two paths commits a folders changes by its path and hears why push did not go", () => new McpTests(restored), t => t.An_agent_reads_a_renames_two_paths_commits_a_folders_changes_by_its_path_and_hears_why_push_did_not_go());
        await Flow.StepApartAsync("an agents diff of a changed binary file says the binary files differ", () => new McpTests(restored), t => t.An_agents_diff_of_a_changed_binary_file_says_the_binary_files_differ());
        await Flow.StepApartAsync("an agents diff of a file changed only in its line separators or its mode says what changed", () => new McpTests(restored), t => t.An_agents_diff_of_a_file_changed_only_in_its_line_separators_or_its_mode_says_what_changed());
        await Flow.StepApartAsync("an agents status of a folder lists its changes and of a path naming nothing is refused", () => new McpTests(restored), t => t.An_agents_status_of_a_folder_lists_its_changes_and_of_a_path_naming_nothing_is_refused());
        await Flow.StepApartAsync("an agent naming a line outside a file or no lines is refused with the files lines", () => new McpTests(restored), t => t.An_agent_naming_a_line_outside_a_file_or_no_lines_is_refused_with_the_files_lines());
        await Flow.StepApartAsync("an agents vcs tools refuse a branch a stash action and lines the repository has not naming what there is", () => new McpTests(restored), t => t.An_agents_vcs_tools_refuse_a_branch_a_stash_action_and_lines_the_repository_has_not_naming_what_there_is());
        await Flow.StepApartAsync("an agents diff of a file git mv renamed is its one changed line from where the last commit has it", () => new McpTests(restored), t => t.An_agents_diff_of_a_file_git_mv_renamed_is_its_one_changed_line_from_where_the_last_commit_has_it());
        await Flow.StepApartAsync("an agents history of a file git mv renamed lists its commits from where the last commit has it", () => new McpTests(restored), t => t.An_agents_history_of_a_file_git_mv_renamed_lists_its_commits_from_where_the_last_commit_has_it());
        await Flow.StepApartAsync("a repository git refuses as another users is said to be refused in gits words naming safe directory", () => new McpTests(restored), t => t.A_repository_git_refuses_as_another_users_is_said_to_be_refused_in_gits_words_naming_safe_directory());
        await Flow.StepApartAsync("with no git on the path a repository is said to need git in the version control menu never taken for none", () => new McpTests(restored), t => t.With_no_git_on_the_path_a_repository_is_said_to_need_git_in_the_version_control_menu_never_taken_for_none());
        await Flow.StepApartAsync("an agents create git repository writes the dotnet gitignore and is refused once the folder is in one", () => new McpTests(restored), t => t.An_agents_create_git_repository_writes_the_dotnet_gitignore_and_is_refused_once_the_folder_is_in_one());
        await Flow.StepApartAsync("an agents push to a remote that asks for a login says so where nobody is at the IDE", () => new McpTests(restored), t => t.An_agents_push_to_a_remote_that_asks_for_a_login_says_so_where_nobody_is_at_the_IDE());
    }

    /// <summary>An agent's commits as one flow, each step one test's check in an instance of its own: a resolved rebase
    /// still said, an amend at an interactive rebase's edit stop, an amend with no paths rewording alone, one of a
    /// file's two changes committed, lines and files staged and committed alone, a change only staged kept staged, a
    /// moved and rewritten file's move first, an edit whose save was refused refusing the commit, a stash dropped by
    /// its commit, and a login profile's job holding up no git.</summary>
    [Fact]
    public async Task An_agents_commits_take_the_paths_and_lines_it_names_keeping_what_the_index_holds_besides()
    {
        await Flow.StepApartAsync("a rebase whose conflict is resolved is still said and an agents commit never goes beside it", () => new McpTests(restored), t => t.A_rebase_whose_conflict_is_resolved_is_still_said_and_an_agents_commit_never_goes_beside_it());
        await Flow.StepApartAsync("at an interactive rebases edit stop an agent amends the commit and the rebase goes on with it", () => new McpTests(restored), t => t.At_an_interactive_rebases_edit_stop_an_agent_amends_the_commit_and_the_rebase_goes_on_with_it());
        await Flow.StepApartAsync("an agents amend with no paths rewords the last commit and leaves what the index holds out of it", () => new McpTests(restored), t => t.An_agents_amend_with_no_paths_rewords_the_last_commit_and_leaves_what_the_index_holds_out_of_it());
        await Flow.StepApartAsync("an agents commit of one of a files two changes leaves the other and refuses lines that name none", () => new McpTests(restored), t => t.An_agents_commit_of_one_of_a_files_two_changes_leaves_the_other_and_refuses_lines_that_name_none());
        await Flow.StepApartAsync("an agent stages lines and files sees what is staged and commits it alone", () => new McpTests(restored), t => t.An_agent_stages_lines_and_files_sees_what_is_staged_and_commits_it_alone());
        await Flow.StepApartAsync("an agents commit of some lines keeps a change only staged staged", () => new McpTests(restored), t => t.An_agents_commit_of_some_lines_keeps_a_change_only_staged_staged());
        await Flow.StepApartAsync("an agents commit of a moved and rewritten file with moves first commits the move as a rename first", () => new McpTests(restored), t => t.An_agents_commit_of_a_moved_and_rewritten_file_with_moves_first_commits_the_move_as_a_rename_first());
        await Flow.StepApartAsync("an agents commit with an edit whose save was refused is refused naming the file", () => new McpTests(restored), t => t.An_agents_commit_with_an_edit_whose_save_was_refused_is_refused_naming_the_file());
        await Flow.StepApartAsync("an agents drop of a stash by its commit takes that stash though another program stashed since the list", () => new McpTests(restored), t => t.An_agents_drop_of_a_stash_by_its_commit_takes_that_stash_though_another_program_stashed_since_the_list());
        await Flow.StepApartAsync("a login profile that leaves a job running holds up no git Passenger starts", () => new McpTests(restored), t => t.A_login_profile_that_leaves_a_job_running_holds_up_no_git_Passenger_starts());
    }

    /// <summary>An agent's edits as one flow, each step one test's check in an instance of its own: an edit its file's
    /// encoding cannot hold saved as UTF-8 saying so, Local History of a path never there and of a deleted file, and
    /// every format, tidy, refactoring, rename and code action of a read-only file refused saying so.</summary>
    [Fact]
    public async Task An_agents_edits_and_history_keep_the_files_bytes_and_a_read_only_file_refuses_every_change()
    {
        await Flow.StepApartAsync("an agents edit its files encoding cannot hold is saved as utf8 and the answer says so", () => new McpTests(restored), t => t.An_agents_edit_its_files_encoding_cannot_hold_is_saved_as_utf8_and_the_answer_says_so());
        await Flow.StepApartAsync("an agents local history of a path never there says so and a deleted files still lists its changes", () => new McpTests(restored), t => t.An_agents_local_history_of_a_path_never_there_says_so_and_a_deleted_files_still_lists_its_changes());
        await Flow.StepApartAsync("an agents format tidy refactorings rename and code actions of a read only xaml or cs file are refused saying it is read only", () => new McpTests(restored), t => t.An_agents_format_tidy_refactorings_rename_and_code_actions_of_a_read_only_xaml_or_cs_file_are_refused_saying_it_is_read_only());
    }

    /// <summary>An agent's signing as one flow, every step needing ssh-keygen, each in an instance of its own: the log
    /// saying each commit's signature good by its signer, unverified or none, and a commit signed with the key git's
    /// config names and one needing a passphrase saying why not.</summary>
    [Fact]
    public async Task An_agents_signed_log_and_commit_say_each_signature_and_why_one_was_not_made()
    {
        await Flow.StepApartAsync("an agents vcs log says each commits signature good by its signer unverified or none", () => new McpTests(restored), t => t.An_agents_vcs_log_says_each_commits_signature_good_by_its_signer_unverified_or_none());
        await Flow.StepApartAsync("an agents vcs commit is signed with the key gits config names and one needing a passphrase says why not", () => new McpTests(restored), t => t.An_agents_vcs_commit_is_signed_with_the_key_gits_config_names_and_one_needing_a_passphrase_says_why_not());
    }

    /// <summary>The login shell's environment as one flow, every step off Windows alone (a Windows program gets the
    /// person's whole environment), each in an instance of its own: a login shell that fails without a terminal logged
    /// in its words and one that skips it giving its path, the environment read from the shell the terminal runs with
    /// no SHELL, and a global dotnet-ef under the DOTNET_CLI_HOME a login profile sets found.</summary>
    [Fact]
    public async Task The_IDEs_environment_is_the_login_shells_however_it_starts_or_fails()
    {
        await Flow.StepApartAsync("a login shell that fails without a terminal is logged in its words and one that skips it while read gives its path", () => new McpTests(restored), t => t.A_login_shell_that_fails_without_a_terminal_is_logged_in_its_words_and_one_that_skips_it_while_read_gives_its_path());
        await Flow.StepApartAsync("with no SHELL the IDEs environment is read from the login shell its terminal runs", () => new McpTests(restored), t => t.With_no_SHELL_the_IDEs_environment_is_read_from_the_login_shell_its_terminal_runs());
        await Flow.StepApartAsync("a global dotnet ef under the DOTNET CLI HOME a login profile sets is found", () => new McpTests(restored), t => t.A_global_dotnet_ef_under_the_DOTNET_CLI_HOME_a_login_profile_sets_is_found());
    }

    private async Task An_agent_that_lists_the_tests_as_it_starts_gets_those_the_test_projects_code_declares()
    {
        // A test project as written, never built or restored: its tests come from its code, which the agent's first look waits for.
        string tests = PathNames.Canonical(TestFolders.Create("mcp-tests-"));
        DeleteAtEnd(tests);
        File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(tests, "SumTests.cs"),
            "using Xunit;\n\nnamespace Sums;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n\n"
            + "    [Fact]\n    public void Three_and_three_make_six() => Assert.Equal(6, 3 + 3);\n}\n");
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        Assert.Equal(2, (await Call(client, McpTools.TestsList)).GetArrayLength());
        JsonElement test = Assert.Single((await Call(client, McpTools.TestsList, new() { ["filter"] = "two_make_four" })).EnumerateArray());
        Assert.Equal(("Sums.SumTests.Two_and_two_make_four", "notRun", "SumTests.cs:8"),
            (test.GetProperty("test").GetString(), test.GetProperty("state").GetString(), test.GetProperty("location").GetString()));
        Assert.Equal(1, (await Call(client, McpTools.TestsList, new() { ["limit"] = 1 })).GetArrayLength());
        Assert.Equal(0, (await Call(client, McpTools.TestsList, new() { ["state"] = "failed" })).GetArrayLength());
        JsonElement notRun = await Call(client, McpTools.TestsList, new() { ["state"] = test.GetProperty("state").GetString() });
        Assert.True(notRun.GetArrayLength() == 2 && notRun.EnumerateArray().All(t => t.GetProperty("state").GetString() == "notRun"),
            $"tests_list asked for the state it says the tests are in answered: {notRun}");
        CallToolResult wrong = await Result(client, McpTools.TestsList, new() { ["state"] = "broken" });
        Assert.True(wrong.IsError == true && Text(wrong).Contains("there are passed, failed, skipped and notRun", StringComparison.Ordinal), $"tests_list in no state answered: {Text(wrong)}");
    }

    /// <summary>Roslyn's server watches the packs folder of the .NET that DOTNET_ROOT names every .dll at once, and told of
    /// any .dll written there runs every project's source generators again, as installing a workload writes thousands: here
    /// a .NET whose packs folder is the test's own, holding a pack of its own beside links to the machine's packs, the rest
    /// of it the machine's through links. An assembly written there that no project references never reaches the server,
    /// and one the project references, written after it, does.</summary>
    [Fact]
    public async Task An_assembly_written_into_the_packs_folder_that_no_project_references_never_reaches_the_language_server()
    {
        string root = PathNames.Canonical(TestFolders.Create("dotnet-root-"));
        DeleteAtEnd(root);
        string pack = Directory.CreateDirectory(Path.Combine(root, "packs", "Passenger.Test.Pack", "1.0.0")).FullName;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(Dotnet.Root()).Where(e => Path.GetFileName(e) != "packs"))
            {
                string link = Path.Combine(root, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    Directory.CreateSymbolicLink(link, entry);
                }
                else
                {
                    File.CreateSymbolicLink(link, entry);
                }
            }
            // Windows' MSBuild takes the SDK through DOTNET_ROOT's links unresolved, so its targeting packs are found here.
            foreach (string machines in Directory.EnumerateDirectories(Path.Combine(Dotnet.Root(), "packs")))
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "packs", Path.GetFileName(machines)), machines);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"A .NET of the test's own takes links to the machine's, which this account may not make: {error.Message}");
        }
        string referenced = Path.Combine(pack, "Referenced.dll");
        File.Copy(typeof(PathNames).Assembly.Location, referenced);
        string folder = PathNames.Canonical(TestFolders.Create("mcp-packs-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "App.csproj", TestProjects.Console().With(
            $"  <ItemGroup>\n    <Reference Include=\"Referenced\">\n      <HintPath>{Passenger.Text.XmlEntities.Encode(referenced)}</HintPath>\n    </Reference>\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(typeof(Passenger.Core.PathNames).Name);\n");
        await Restore.RunAsync(Path.Combine(folder, "App.csproj"));

        await using McpClient client = await Start(["--mcp", folder], environment: new() { [RuntimeVariables.Root] = root });
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });
        JsonElement found = await Call(client, McpTools.Diagnostics, new() { ["path"] = "Program.cs" });
        Assert.True(!found.EnumerateArray().Any(d => d.GetProperty("severity").GetString() == "error"), $"The app referencing the pack's assembly has errors: {found}");
        string packs = Path.Combine(root, "packs");
        string log = Path.Combine(OwnState, "logs", "passenger.log");
        await Until(() => File.Exists(log) && Ui.Shared(log).Contains($"the watcher of {packs} records", StringComparison.Ordinal),
            $"the language server's registration of {packs} to be watched whole for it and its first walk done");

        File.WriteAllBytes(Path.Combine(pack, "Unreferenced.dll"), File.ReadAllBytes(referenced));
        File.Copy(typeof(PathNames).Assembly.Location, referenced, overwrite: true);
        File.SetLastWriteTimeUtc(referenced, DateTime.UtcNow);
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        await Until(() => Told(trace, "/Referenced.dll") > 0, "the language server to be told of the referenced assembly written again");
        Assert.True(Told(trace, "/Unreferenced.dll") == 0,
            $"The language server was told of an assembly written into {packs} that no project references, so it ran every project's source generators again");

        static int Told(string trace, string file) => Ui.Shared(trace).Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal)
            && l.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DidChangeWatchedFiles}\"", StringComparison.Ordinal) && l.Contains(file + "\"", StringComparison.Ordinal));
    }

    /// <summary>A project whose global.json asks for a .NET SDK that is not installed: workspace says so and how the person
    /// mends it, and run refuses in the same words rather than finding no executable project.</summary>
    private async Task An_agent_where_global_json_asks_for_an_SDK_not_installed_hears_so_from_workspace_and_run()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-no-sdk-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), MissingSdkTests.Pinned);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"App\");\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        const string Missing = "The .NET SDK 1.0.100 that global.json asks for is not installed: install it, or have global.json name one that is";
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.TryGetProperty("sdkMissing", out JsonElement missing)
            && missing.GetString() == $"{Missing}. This Passenger, an agent's own, installs nothing: the person installs it with Install .NET SDK in the banner over their Passenger's window",
            $"workspace, where global.json asks for an SDK not installed, answered: {workspace}");
        CallToolResult run = await Result(client, McpTools.Run);
        Assert.True(run.IsError == true && Text(run).Contains(Missing, StringComparison.Ordinal), $"run without the SDK answered: {Text(run)}");
    }

    /// <summary>An agent's own Passenger trusts the folder it was started on, since the agent runs there already: it says
    /// so in its instructions and in workspace, and runs the folder's code without asking.</summary>
    private async Task An_agents_own_Passenger_trusts_the_folder_it_was_started_on_and_says_so()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-trusted-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"App\");\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        Assert.True(client.ServerInstructions?.Contains("trusts the folder it was started on", StringComparison.Ordinal) == true,
            $"The server's instructions leave out that an agent's own Passenger trusts its folder: {client.ServerInstructions}");
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.TryGetProperty("trust", out JsonElement trust) && trust.GetString() == "trusted", $"workspace on an agent's own Passenger's folder answered: {workspace}");
    }

    /// <summary>The code tools asked of a folder whose projects have no SDK to load with, its language server ready with
    /// none of their code: each says so, why and what mends it, as workspace does, never an empty answer. Of a folder whose
    /// language server could not start (its folder missing), each refuses at once saying why, folder-wide diagnostics too,
    /// where each waited out its limit and said the server was still loading.</summary>
    private async Task An_agent_whose_projects_have_no_SDK_or_whose_language_server_could_not_start_hears_why_from_each_code_tool_at_once_rather_than_nothing_found()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-no-sdk-code-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), MissingSdkTests.Pinned);
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(folder, "Greeter.cs"), Greeter);
        File.WriteAllText(Path.Combine(folder, "Caller.cs"), Caller);
        await using McpClient client = await Start(["--mcp", folder]);
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.TryGetProperty("sdkMissing", out _), $"workspace, where global.json asks for an SDK not installed, answered: {workspace}");
        const string Missing = "The folder's projects are not loaded, so the C# language server has none of their code to search or read: "
            + "The .NET SDK 1.0.100 that global.json asks for is not installed";
        foreach ((string tool, Dictionary<string, object?> arguments) in CodeAsks("Greeter.cs", "Caller.cs"))
        {
            CallToolResult answer = await Result(client, tool, arguments);
            Assert.True(answer.IsError == true && Text(answer).Contains(Missing, StringComparison.Ordinal),
                $"{tool} {JsonSerializer.Serialize(arguments)}, of a project with no SDK to load with, answered: {Text(answer)}");
        }

        string unstarted = PathNames.Canonical(TestFolders.Create("mcp-no-server-"));
        DeleteAtEnd(unstarted);
        File.WriteAllText(Path.Combine(unstarted, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(unstarted, "Greeter.cs"), Greeter);
        File.WriteAllText(Path.Combine(unstarted, "Caller.cs"), Caller);
        await using McpClient serverless = await Start(["--mcp", unstarted],
            environment: new() { [global::Passenger.Lsp.RoslynServerFolder.ServerVariable] = Path.Combine(unstarted, "no-server") });
        string couldNot = Refusals.CSharpUnavailable("The Roslyn language server is missing");
        foreach ((string tool, Dictionary<string, object?> arguments) in CodeAsks("Greeter.cs", "Caller.cs").Append((McpTools.Diagnostics, new())))
        {
            CallToolResult answer = await Result(serverless, tool, arguments);
            Assert.True(answer.IsError == true && Text(answer).Contains(couldNot, StringComparison.Ordinal),
                $"{tool} {JsonSerializer.Serialize(arguments)}, where the language server could not start, answered: {Text(answer)}");
        }
    }

    /// <summary>The code tools asked of a project the language server met an error loading (its file cut off before its
    /// end) say it did not load and why, beside a project that loaded, which answers as ever.</summary>
    private async Task An_agent_asking_of_a_project_that_did_not_load_hears_why_and_the_loaded_one_answers()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unloaded-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Good/Good.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Good/Fine.cs", "namespace Good;\n\npublic class Fine\n{\n}\n");
        TestFolders.Write(folder, "Bad/Bad.csproj", TestProjects.Library().Replace("</Project>", string.Empty, StringComparison.Ordinal));
        TestFolders.Write(folder, "Bad/Greeter.cs", Greeter);
        TestFolders.Write(folder, "Bad/Caller.cs", Caller);
        await using McpClient client = await Start(["--mcp", folder]);
        JsonElement fine = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Fine" })).EnumerateArray());
        Assert.Equal(Path.Combine("Good", "Fine.cs"), fine.GetProperty("path").GetString());
        foreach ((string tool, Dictionary<string, object?> arguments) in CodeAsks("Bad/Greeter.cs", "Bad/Caller.cs"))
        {
            CallToolResult answer = await Result(client, tool, arguments);
            string said = Text(answer);
            string[] why = arguments.ContainsKey("query") || arguments.ContainsKey("symbol")
                ? ["Greeter, but the C# language server met errors loading ", "whose code it may not hold: ", "Bad: "]
                : ["Bad did not load into the C# language server, which holds none of its code to answer from: "];
            Assert.True(answer.IsError == true && why.All(w => said.Contains(w, StringComparison.Ordinal)), $"{tool} {JsonSerializer.Serialize(arguments)}, of a project that did not load, answered: {said}");
        }
    }

    /// <summary>An agent's change of a project file, through edit or written by its own file tool, then at once a question
    /// whose answer is what the project compiles now (a folder it removed no longer removed, a symbol it defines): the
    /// project is evaluated anew and the language server's load of it again waited for, so find_symbol finds what the change
    /// brought in.</summary>
    private async Task An_agents_question_after_its_change_of_a_project_file_waits_for_the_project_to_load_again()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-reloaded-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library().With("  <ItemGroup>\n    <Compile Remove=\"Extra/**\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Lib/Fine.cs", "namespace Lib;\n\npublic class Fine\n{\n}\n\n#if PASSENGER_GATED\npublic class Gated\n{\n}\n#endif\n");
        TestFolders.Write(folder, "Lib/Extra/Helper.cs", "namespace Lib;\n\npublic static class Helper\n{\n    public static string Help() => \"help\";\n}\n");
        await using McpClient client = await Start(["--mcp", folder]);
        Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Fine", ["exact"] = true })).EnumerateArray());

        await Call(client, McpTools.Edit, new() { ["path"] = "Lib/Lib.csproj", ["oldText"] = "    <Compile Remove=\"Extra/**\" />\n", ["newText"] = string.Empty });
        JsonElement helper = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Helper", ["exact"] = true })).EnumerateArray());
        Assert.Equal(Path.Combine("Lib", "Extra", "Helper.cs"), helper.GetProperty("path").GetString());

        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library("<DefineConstants>$(DefineConstants);PASSENGER_GATED</DefineConstants>"));
        JsonElement gated = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Gated", ["exact"] = true })).EnumerateArray());
        Assert.Equal(Path.Combine("Lib", "Fine.cs"), gated.GetProperty("path").GetString());
    }

    private const string Greeter = "namespace Lib;\n\npublic class Greeter\n{\n    public string Greet(string name) => \"Hello, \" + name;\n}\n";

    private const string Caller = "namespace Lib;\n\npublic static class Caller\n{\n    public static string Call() => new Greeter().Greet(\"you\");\n}\n";

    /// <summary>Each code tool that reads a project's code, asked of <see cref="Greeter"/> and <see cref="Caller"/> in the files given.</summary>
    private static IEnumerable<(string Tool, Dictionary<string, object?> Arguments)> CodeAsks(string greeter, string caller) =>
    [
        (McpTools.FindSymbol, new() { ["query"] = "Greeter" }),
        (McpTools.Usages, new() { ["symbol"] = "Greeter" }),
        (McpTools.Usages, new() { ["path"] = greeter, ["line"] = 3, ["column"] = 14 }),
        (McpTools.Definition, new() { ["path"] = caller, ["line"] = 5, ["column"] = 42 }),
        (McpTools.Outline, new() { ["path"] = greeter }),
        (McpTools.Hierarchy, new() { ["path"] = greeter, ["line"] = 5, ["column"] = 19 }),
        (McpTools.SymbolInfo, new() { ["path"] = greeter, ["line"] = 5, ["column"] = 19 }),
    ];

    /// <summary>An agent holds modifiers by the keymap's words and the aliases agents write (command, opt, win, super), in
    /// any case, and primary; a word of none is refused naming those there are, before any key is pressed. Its primary is
    /// the command key Passenger's keymap takes on the system it runs on, Cmd on a Mac and Ctrl elsewhere, in a Passenger
    /// drawn on no screen as on one: primary+shift+A opens Find Action, and a text box there takes the system's hotkeys as
    /// one on the screen does, primary+A selecting what it typed for the next key to replace.</summary>
    private async Task An_agents_modifiers_are_the_keymaps_words_and_aliases_and_its_primary_the_systems_command_key_in_a_passenger_drawn_on_no_screen()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-keys-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        // F24, which nothing binds, so each chord is read and pressed and nothing runs.
        foreach (string chord in new[] { "ctrl+shift+F24", "Control+Option+F24", "cmd+opt+F24", "command+alt+F24", "meta+win+F24", "SUPER+primary+F24", "cmdorctrl+F24" })
        {
            CallToolResult pressed = await Result(client, McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = chord });
            Assert.True(pressed.IsError != true, $"app_key {chord} was refused: {Text(pressed)}");
        }
        CallToolResult refused = await Result(client, McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "hyper+F24" });
        Assert.True(refused.IsError == true && Text(refused).Contains("No modifier 'hyper': there are ctrl, shift, alt, cmd (meta) and primary", StringComparison.Ordinal),
            $"app_key with a modifier of no name answered: {Text(refused)}");

        Dictionary<string, object?> findAction = new() { ["app"] = "ide", ["filter"] = "ActionPopup" };
        string before = Text(await Call(client, McpTools.AppTree, findAction));
        Assert.StartsWith("Nothing in Passenger matches ActionPopup", before, StringComparison.Ordinal);
        string opened = Text(await Call(client, McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "primary+shift+A" }));
        string found = before;
        Assert.True(await Waiting.UntilAsync(async () => !(found = Text(await Call(client, McpTools.AppTree, findAction))).StartsWith("Nothing in", StringComparison.Ordinal),
            TimeSpan.FromMinutes(1), Cancel, TimeSpan.FromMilliseconds(100)),
            $"primary+shift+A, the keymap's Find Action with {(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")} as primary, did not open it ({opened}): {found}");
        Assert.Contains("ActionPopup", found, StringComparison.Ordinal);

        string typed = Text(await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = "Settings" }));
        Assert.True(typed.Contains("it shows \"Settings\"", StringComparison.Ordinal), $"Typing into Find Action's search did not reach it: {typed}");
        string replaced = Text(await Call(client, McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "primary+A G" }));
        Assert.True(replaced.Contains("it shows \"g\"", StringComparison.Ordinal),
            $"primary+A, {(CommandKey.IsCmd ? "Cmd" : "Ctrl")}+A, did not select Find Action's search for G to replace, as the system's own Select All does: {replaced}");
    }

    /// <summary>A folder whose .slnx does not read: workspace names it with where its reader stopped and why, and no
    /// solution open.</summary>
    private async Task An_agent_on_a_folder_whose_solution_does_not_read_hears_where_and_why_from_workspace()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unread-solution-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"App\");\n");
        File.WriteAllText(Path.Combine(folder, "Shop.slnx"), "<Solution>\n  <Project Path=\"App.csproj\" />\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.TryGetProperty("unreadSolution", out JsonElement unread)
            && unread.GetString() == "Shop.slnx:3:1: Unexpected end of file has occurred. The following elements are not closed: Solution"
            && !workspace.TryGetProperty("solution", out _),
            $"workspace, where Shop.slnx is cut short, answered: {workspace}");
    }

    /// <summary>tests_run answers with the run it made alone: after a run of every test, one failing, a run of the passing
    /// one by its name tells no failure, while tests_list still says the other failed when it last ran.</summary>
    private async Task An_agents_run_of_a_passing_test_after_a_failure_answers_no_failure_of_a_test_it_did_not_run()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-run-tests-"));
        DeleteAtEnd(tests);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
                $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"),
                "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n\n" +
                "    [Fact]\n    public void Two_and_two_make_five() => Assert.Equal(5, 2 + 2);\n}\n");
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        JsonElement all = await Call(client, McpTools.TestsRun);
        Assert.StartsWith("Tests passed: 1, failed: 1 of 2", all.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("Sums.Tests.SumTests.Two_and_two_make_five", Assert.Single(all.GetProperty("failures").EnumerateArray()).GetProperty("test").GetString());

        JsonElement four = await Call(client, McpTools.TestsRun, new() { ["filter"] = "make_four" });
        Assert.StartsWith("Tests passed: 1, failed: 0 of 1", four.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(four.GetProperty("failures").GetArrayLength() == 0, $"A run of one passing test answered failures of tests it did not run, as if they had failed in it: {four}");
        JsonElement failed = Assert.Single((await Call(client, McpTools.TestsList, new() { ["state"] = "failed" })).EnumerateArray());
        Assert.Equal("Sums.Tests.SumTests.Two_and_two_make_five", failed.GetProperty("test").GetString());
    }

    /// <summary>The Unit Tests actions an agent runs through ide_action answer what the run came to, as tests_run's tally says
    /// it: Run All Tests with one test failing, Rerun Failed Tests of that one, and again once it is mended; Rerun Failed Tests
    /// with none failed, and Run All Tests in a folder of no tests, fail saying why, as tests_run does where nothing runs.</summary>
    private async Task An_agents_unit_test_actions_answer_what_the_run_came_to()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-test-actions-"));
        DeleteAtEnd(tests);
        const string Sums = "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n\n" +
            "    [Fact]\n    public void Two_and_two_make_five() => Assert.Equal(5, 2 + 2);\n}\n";
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
                $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"), Sums);
            await Restore.RunAsync(tests);
        }, tests);
        string none = PathNames.Canonical(TestFolders.Create("mcp-no-tests-"));
        DeleteAtEnd(none);
        File.WriteAllText(Path.Combine(none, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(none, "Program.cs"), "System.Console.WriteLine(\"App\");\n");

        await using (McpClient client = await Start(["--mcp", tests], languageServer: false))
        {
            string all = (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.RunAllTests })).GetString()!;
            Assert.StartsWith("Tests passed: 1, failed: 1 of 2", all, StringComparison.Ordinal);
            string failed = (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.RerunFailedTests })).GetString()!;
            Assert.StartsWith("Tests passed: 0, failed: 1 of 1", failed, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"), Sums.Replace("Assert.Equal(5, 2 + 2)", "Assert.Equal(4, 2 + 2)", StringComparison.Ordinal));
            string mended = (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.RerunFailedTests })).GetString()!;
            Assert.StartsWith("Tests passed: 1, failed: 0 of 1", mended, StringComparison.Ordinal);
            CallToolResult nothing = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.RerunFailedTests });
            Assert.True(nothing.IsError == true && Text(nothing).Contains("No test failed", StringComparison.Ordinal),
                $"Rerun Failed Tests with none failed answered an agent: {Text(nothing)}");
        }
        await using (McpClient client = await Start(["--mcp", none], languageServer: false))
        {
            CallToolResult nowhere = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.RunAllTests });
            Assert.True(nowhere.IsError == true && Text(nowhere).Contains("There are no test projects: none references a test SDK or adapter", StringComparison.Ordinal),
                $"Run All Tests in a folder of no tests answered an agent: {Text(nowhere)}");
        }
    }

    /// <summary>A failing test an agent writes into a test file the IDE has read, run at once by its class's name, runs with
    /// the class's others and is told among the failures: what a run runs is chosen once its build has ended.</summary>
    private async Task An_agents_test_written_and_run_at_once_by_its_classs_name_runs_and_its_failure_is_told()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-written-tests-"));
        DeleteAtEnd(tests);
        const string Two = "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n\n" +
            "    [Fact]\n    public void Three_and_three_make_six() => Assert.Equal(6, 3 + 3);\n";
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
                $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"), Two + "}\n");
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        Assert.Equal(2, (await Call(client, McpTools.TestsList, new() { ["filter"] = "SumTests" })).GetArrayLength());

        File.WriteAllText(Path.Combine(tests, "SumTests.cs"), Two + "\n    [Fact]\n    public void Written_just_now() => Assert.Equal(5, 2 + 2);\n}\n");
        JsonElement run = await Call(client, McpTools.TestsRun, new() { ["filter"] = "Sums.Tests.SumTests" });
        Assert.True(run.GetProperty("summary").GetString()!.StartsWith("Tests passed: 2, failed: 1 of 3", StringComparison.Ordinal),
            $"A run by its class's name of a test written just before it left the test out: {run}");
        Assert.Equal("Sums.Tests.SumTests.Written_just_now", Assert.Single(run.GetProperty("failures").EnumerateArray()).GetProperty("test").GetString());
    }

    /// <summary>A failing test that wrote 200,000 lines is answered with its message and stack whole and its output's last
    /// lines after a note of what was left out, an answer an agent can take in.</summary>
    private async Task An_agents_run_of_a_failing_test_that_wrote_200000_lines_answers_its_last_lines_and_says_the_rest_was_left_out()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-flood-tests-"));
        DeleteAtEnd(tests);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Flood.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
                $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "FloodTests.cs"), """
                using Xunit;
                using Xunit.Abstractions;

                namespace Flood.Tests;

                public class FloodTests(ITestOutputHelper output)
                {
                    [Fact]
                    public void Writes()
                    {
                        for (int line = 0; line < 200_000; line++)
                        {
                            output.WriteLine($"line {line} of the flood");
                        }
                        Assert.Fail("the flood is over");
                    }
                }

                """);
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        CallToolResult answer = await Result(client, McpTools.TestsRun, new() { ["filter"] = "Writes" });
        string text = Text(answer);
        Assert.True(text.Length < 64 * 1024, $"tests_run answered a failing test that wrote 200,000 lines in {text.Length:N0} characters");
        JsonElement failure = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("failures").EnumerateArray());
        string details = failure.GetProperty("details").GetString()!;
        Assert.True(details.Contains("the flood is over", StringComparison.Ordinal) && details.Contains("FloodTests.Writes()", StringComparison.Ordinal)
            && details.Contains("earlier lines of output", StringComparison.Ordinal) && details.TrimEnd().EndsWith("line 199999 of the flood", StringComparison.Ordinal),
            $"tests_run's failure did not give its message, stack and the output's last lines after a note of the rest: {details[..Math.Min(details.Length, 2000)]}");
    }

    /// <summary>A theory of 500 rows one of which fails is answered with that row's failure and a count of the rest, never
    /// every passing row beside it, which buried the one failure in an answer of tens of kilobytes.</summary>
    private async Task An_agents_run_of_a_theory_with_one_failing_row_among_500_names_that_row_and_counts_the_rest()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-rows-tests-"));
        DeleteAtEnd(tests);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Rows.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
                $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "RowTests.cs"), """
                using System.Collections.Generic;
                using System.Linq;
                using Xunit;

                namespace Rows.Tests;

                public class RowTests
                {
                    public static IEnumerable<object[]> Values => Enumerable.Range(0, 500).Select(value => new object[] { value });

                    [Theory]
                    [MemberData(nameof(Values))]
                    public void Is_not_seven(int value) => Assert.NotEqual(7, value);
                }

                """);
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        string answer = Text(await Result(client, McpTools.TestsRun));
        JsonElement run = JsonDocument.Parse(answer).RootElement;
        Assert.StartsWith("Tests passed: 499, failed: 1 of 500", run.GetProperty("summary").GetString(), StringComparison.Ordinal);
        string details = Assert.Single(run.GetProperty("failures").EnumerateArray()).GetProperty("details").GetString()!;
        Assert.True(answer.Length < 5_000 && details.StartsWith("Is_not_seven(value: 7): Failed\n", StringComparison.Ordinal)
            && details.Contains("Assert.NotEqual() Failure", StringComparison.Ordinal) && details.EndsWith("\n\n499 other rows passed", StringComparison.Ordinal),
            $"tests_run answered a theory's one failing row among 500 in {answer.Length:N0} characters, its details: {details[..Math.Min(details.Length, 2000)]}");
    }

    /// <summary>A VSTest test that reads its standard input reads its end at once, never the agent's requests, which are
    /// Passenger's standard input: its run passes, and the agent's next call is answered.</summary>
    private async Task An_agents_test_that_reads_standard_input_reads_its_end_and_the_agents_next_call_is_answered()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-stdin-tests-"));
        DeleteAtEnd(tests);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Input.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
                $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
            // A read that waits is ended by the test's own limit, so a run that reads the agent's pipe fails rather than hangs.
            File.WriteAllText(Path.Combine(tests, "InputTests.cs"), """
                using System;
                using System.Threading.Tasks;
                using Xunit;

                namespace Input.Tests;

                public class InputTests
                {
                    [Fact]
                    public void Reads_no_input()
                    {
                        Task<string> read = Task.Run(() => Console.In.ReadLine());
                        Assert.True(read.Wait(TimeSpan.FromSeconds(20)), "Reading standard input waited for input that never came");
                        Assert.True(read.Result is null, $"Standard input gave: {read.Result}");
                    }
                }

                """);
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        JsonElement run = await Call(client, McpTools.TestsRun, new() { ["filter"] = "Reads_no_input" });
        Assert.True(run.GetProperty("summary").GetString()!.StartsWith("Tests passed: 1, failed: 0 of 1", StringComparison.Ordinal),
            $"A test reading its standard input did not read its end at once: {run}");
        JsonElement passed = Assert.Single((await Call(client, McpTools.TestsList, new() { ["filter"] = "Reads_no_input" })).EnumerateArray());
        Assert.Equal("passed", passed.GetProperty("state").GetString());
    }

    /// <summary>An agent's own Passenger opens its folder as it starts, while its window is still being built: the first C#
    /// answer comes from the language server started then, which loaded the projects as the project host evaluated and built
    /// them then, and the window, once built, takes all of it up. Of what its opening asked of the built window before (an
    /// evaluation, a design-time build, a language server's start and load), it asks none: the window's build is no
    /// longer the first answer's to wait out.</summary>
    private async Task An_agents_own_passenger_opens_its_folder_while_its_window_is_built_which_takes_it_up_and_does_none_of_it_again()
    {
        string folder = PathNames.Canonical(await TakeFolderAsync());
        await using McpClient client = await Start(["--mcp", folder]);
        string answered = (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 })).GetString()!;
        Assert.StartsWith("int Helper.Twice(int value)", answered, StringComparison.Ordinal);

        string logged = Path.Combine(OwnState, "logs", "passenger.log");
        string log = string.Empty;
        await Until(() => (log = Ui.Shared(logged)).Contains(") loaded ", StringComparison.Ordinal) && log.Contains("as MSBuild evaluates them", StringComparison.Ordinal),
            "the log to say the language server loaded the project and the Solution view showed it");
        int begun = log.IndexOf($"Opening {folder} as Passenger starts, ahead of its window", StringComparison.Ordinal);
        int built = log.IndexOf("for an agent, with no window shown: its window built", StringComparison.Ordinal);
        Assert.True(begun >= 0 && built > begun, $"The folder's opening was not begun before the window was built:\n{log}");
        string[] ahead = [.. Regex.Matches(log, $@"C# language server \(process (\d+)\) started ahead for {Regex.Escape(folder)} ")
            .Select(m => m.Groups[1].Value)];
        string[] loaded = [.. Regex.Matches(log, @"C# language server \(process (\d+)\) loaded ").Select(m => m.Groups[1].Value)];
        Assert.True(ahead.Length == 1 && loaded.SequenceEqual(ahead) && log.Contains($"C# language server (process {ahead[0]}) started ahead taken up for {folder}", StringComparison.Ordinal)
            && !log.Contains($"C# language server (process {ahead[0]}) started for", StringComparison.Ordinal),
            $"The answer came from a language server the window started once built, not the one started as Passenger did:\n{log}");
        Assert.False(Regex.IsMatch(log, @"evaluated \d+ of the \d+ projects of"), $"The window evaluated the folder's project again once built:\n{log}");
        Assert.False(log.Contains("at design time in", StringComparison.Ordinal), $"The window built the folder's project at design time again once built:\n{log}");
    }

    /// <summary>An agent's own Passenger, its window built over the folder's head start as <c>--mcp</c> builds it, whose
    /// settings as its C# starts are not the defaults the server started ahead has (solution-wide analysis turned on before
    /// its projects were found) takes that server up, telling it the settings, and starts no second one: Problems' Solution
    /// tab lists the warning of a file nobody opened.</summary>
    [AvaloniaFact]
    public void An_agents_own_passenger_whose_settings_are_not_the_defaults_takes_up_the_server_started_ahead_and_starts_no_second()
    {
        string folder = PathNames.Canonical(Folder);
        LaunchOptions launch = LaunchOptions.Parse([folder]);
        MainWindow window = new() { Width = 1200, Height = 800, AgentsAllowed = false, AgentsOwn = true, HeadStart = HeadStart.Begin(launch, languageServer: true) };
        window.Show();
        window.ApplySettings(window.Settings with { SolutionWideAnalysis = true });
        window.Start(launch);
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0219") == true,
            () => $"the warning of Helper.cs, a file nobody opened, listed by solution-wide analysis on as C# started (listed: {string.Join("; ", problems.SolutionProblems?.Select(p => p.Code) ?? [])})",
            TimeSpan.FromMinutes(2));
        problems.Shown = ProblemsTab.Solution;
        Assert.Contains("Helper.cs", problems.Rows.Where(r => r.Problem is null).Select(r => r.Label));

        string logged = Path.Combine(TestRun.Settings, "logs", "passenger.log");
        string log = string.Empty;
        Regex second = new($@"C# language server \(process \d+\) started for {Regex.Escape(folder)} ");
        Ui.WaitFor(() => (log = Ui.Shared(logged)).Contains($"started ahead taken up for {folder}", StringComparison.Ordinal) || second.IsMatch(log),
            "the log to say which language server the window took");
        string[] ahead = [.. Regex.Matches(log, $@"C# language server \(process (\d+)\) started ahead for {Regex.Escape(folder)} ").Select(m => m.Groups[1].Value)];
        Assert.True(ahead.Length == 1 && log.Contains($"C# language server (process {ahead[0]}) started ahead taken up for {folder}", StringComparison.Ordinal),
            $"The language server started ahead with the default settings was not taken up by a window whose settings differ:\n{Ours(log, folder)}");
        Assert.False(second.IsMatch(log),
            $"A second language server started beside the one started ahead, loading the same project again:\n{Ours(log, folder)}");
        window.Close();

        static string Ours(string log, string folder) => string.Join('\n', log.Split('\n').Where(line => line.Contains(folder, StringComparison.Ordinal)));
    }

    /// <summary>A load that runs past its limit (the agent's Passenger's own, 2 s) is said to be still loading, in the
    /// workspace's state and the status bar, and never given up: once the projects load, the language server is ready, the
    /// status bar says so, and solution-wide analysis lists the warning of a file nobody opened.</summary>
    private async Task A_load_past_its_limit_is_said_to_be_still_loading_and_once_it_loads_the_solution_is_ready_and_analysed()
    {
        string folder = await TakeFolderAsync();
        HoldDesignTimeBuilds(folder);
        // Opened once Passenger runs: the folder it starts on has the server's start wait for the builds, not its load.
        string empty = PathNames.Canonical(TestFolders.Create("mcp-empty-"));
        DeleteAtEnd(empty);
        await using McpClient client = await Start(["--mcp", empty], environment: new() { [global::Passenger.App.CSharp.CSharpService.SlowLoadVariable] = "2000" });
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["solutionWideAnalysis"] = true } });
        await Call(client, McpTools.Open, new() { ["path"] = folder });

        const string still = "Still loading 1 project after 2 s; the language server's log says where it is";
        string state = string.Empty;
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == still, TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server's state past the limit was not that it is still loading but \"{state}\".{Said()}");
        string status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains(still, StringComparison.Ordinal), $"The status bar did not warn that the load runs long: {status}");
        // Asked while the projects load, the code tools wait for them rather than answer from none.
        Task<JsonElement> twice = Call(client, McpTools.FindSymbol, new() { ["query"] = "Twice" });

        ReleaseDesignTimeBuilds(folder);
        JsonElement found = Assert.Single((await twice).EnumerateArray());
        Assert.True(found.GetProperty("path").GetString() == "Helper.cs", $"find_symbol asked as the projects loaded answered once they had: {found}");
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == "ready", TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server was not ready once its load ended past the limit: \"{state}\".{Said()}");
        status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains("C# ready: 1 project loaded after ", StringComparison.Ordinal), $"The status bar did not say the late load ended: {status}");
        string[] solution = [.. (await Call(client, McpTools.Diagnostics)).GetProperty("solution").EnumerateArray()
            .Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(solution.Contains("CS0219 Helper.cs:5"), $"Solution-wide analysis did not start once the late load ended: {string.Join("; ", solution)}");
    }

    /// <summary>An agent's Passenger started on a folder whose design-time build is held, its head start waiting for every
    /// build before its language server loads anything, says past its limit (its own, 2 s) that C# is still starting and
    /// for what, in the workspace's state and the status bar; once the build ends C# is ready, the status bar says so, and
    /// solution-wide analysis turned on while it started lists the warning of a file nobody opened.</summary>
    private async Task A_head_start_past_its_limit_is_said_to_be_still_starting_and_once_its_builds_end_the_solution_is_ready_and_analysed()
    {
        string folder = await TakeFolderAsync();
        HoldDesignTimeBuilds(folder);
        await using McpClient client = await Start(["--mcp", folder], environment: new() { [global::Passenger.App.CSharp.CSharpService.SlowLoadVariable] = "2000" });

        const string still = "Still starting C# for 1 project after 2 s, waiting for its design-time builds";
        string state = string.Empty;
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == still, TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server's state past the limit was not that it is still starting but \"{state}\".{Said()}");
        string status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains(still, StringComparison.Ordinal), $"The status bar did not warn that the start runs long: {status}");
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["solutionWideAnalysis"] = true } });

        ReleaseDesignTimeBuilds(folder);
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == "ready", TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server was not ready once the builds its start waited for ended: \"{state}\".{Said()}");
        status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains("C# ready: 1 project loaded after ", StringComparison.Ordinal), $"The status bar did not say the late start ended: {status}");
        string[] solution = [.. (await Call(client, McpTools.Diagnostics)).GetProperty("solution").EnumerateArray()
            .Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(solution.Contains("CS0219 Helper.cs:5"), $"Solution-wide analysis turned on as C# started did not reach its server: {string.Join("; ", solution)}");
    }

    /// <summary>A load past its limit holds the IDE's background work only until its warning is said: a screenshot of the
    /// folder, which waits for that work, is taken once it is, the load still going on, where it waited out its own limit
    /// and was taken saying work was still pending.</summary>
    private async Task A_load_past_its_limit_holds_no_background_work_once_its_warning_is_said()
    {
        string folder = await TakeFolderAsync();
        HoldDesignTimeBuilds(folder);
        string picture = Path.Combine(OwnState, "loading.png");
        Process passenger = StartPassenger([folder, "--screenshot", picture, "--size", "1000x700"],
            environment: new() { [global::Passenger.App.CSharp.CSharpService.SlowLoadVariable] = "2000" });
        try
        {
            Assert.True(await Waiting.Within(passenger.WaitForExitAsync(Cancel), TimeSpan.FromMinutes(3), Cancel), $"The screenshot of a folder whose load runs long never ended.{Said()}");
            string said = Ui.Shared(Path.Combine(OwnState, "logs", "passenger.log"));
            Assert.True(said.Contains("had not loaded 1 project after 2 s, and goes on loading it", StringComparison.Ordinal), $"The load was never said to run long.{Said()}");
            Assert.True(passenger.ExitCode == 0 && File.Exists(picture),
                $"The screenshot of a folder whose load was said to run long ended with exit code {passenger.ExitCode}, the load holding background work past its warning.{Said()}");
        }
        finally
        {
            ReleaseDesignTimeBuilds(folder);
        }
    }

    /// <summary>Holds the folder's design-time builds, which the language server loads the projects from, until
    /// <see cref="ReleaseDesignTimeBuilds"/>.</summary>
    private static void HoldDesignTimeBuilds(string folder) => File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), """
        <Project>
          <Target Name="HeldLoad" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <Exec Condition="'$(OS)' != 'Windows_NT'" Command="for i in `seq 3000`; do test -f released &amp;&amp; break; sleep 0.1; done" />
            <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;for (%24i = 0; %24i -lt 3000 -and -not (Test-Path released); %24i++) { Start-Sleep -Milliseconds 100 }&quot;" />
          </Target>
        </Project>
        """);

    private static void ReleaseDesignTimeBuilds(string folder) => File.WriteAllText(Path.Combine(folder, "released"), string.Empty);

    /// <summary>The language server's state as workspace gives it: ready, or what it is doing.</summary>
    private async Task<string> LanguageServerState(McpClient client) => (await Call(client, McpTools.Workspace)).GetProperty("languageServer").GetString()!;

    /// <summary>A file-based program (<c>dotnet run app.cs</c>) is a project to an agent: workspace lists it by its C# file,
    /// build builds it and run runs it, the code of the file it includes with it.</summary>
    private async Task An_agent_builds_and_runs_a_file_based_program_as_a_project_of_its_own()
    {
        string program = PathNames.Canonical(TestFolders.Create("mcp-program-"));
        DeleteAtEnd(program);
        // What it builds lies in its folder, which the test removes, rather than in the SDK's cache of the person's.
        File.WriteAllText(Path.Combine(program, "Directory.Build.props"), "<Project>\n  <PropertyGroup>\n    <UseArtifactsOutput>true</UseArtifactsOutput>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(program, "app.cs"), "#!/usr/bin/env dotnet\n#:property PublishAot=false\n#:include helper.cs\nConsole.WriteLine(Helper.Greet());\n");
        File.WriteAllText(Path.Combine(program, "helper.cs"), "static class Helper\n{\n    public static string Greet() => \"Hello from helper.cs\";\n}\n");
        await using McpClient client = await Start(["--mcp", program], languageServer: false);
        JsonElement workspace = await Call(client, McpTools.Workspace);
        JsonElement project = Assert.Single(workspace.GetProperty("projects").EnumerateArray());
        Assert.True(project.GetProperty("path").GetString() == "app.cs" && project.GetProperty("outputType").GetString() == "Exe",
            $"The folder's program is not a project of its own: {workspace}");
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The program did not build: {built}");
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 60 });
        Assert.True(ran.GetProperty("output").GetString()?.Contains("Hello from helper.cs\n", StringComparison.Ordinal) == true, $"The program's run did not print its greeting: {ran}");
    }

    /// <summary>An agent's own Passenger, which starts with no console, runs a program writing characters outside an English
    /// Windows' code page (an é, the narrow no-break space French groups digits with, a CJK letter): run's answer has each
    /// as the program wrote it, on every system.</summary>
    private async Task An_agents_run_of_a_program_writing_characters_outside_the_systems_code_page_answers_each_as_written()
    {
        await TakeFolderAsync();
        Write("Program.cs", "Console.WriteLine(\"caf\\u00e9 1\\u202f234,5 \\u6f22\");\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 60 });
        Assert.True(ran.GetProperty("output").GetString()?.Contains("caf\u00e9 1\u202f234,5 \u6f22\n", StringComparison.Ordinal) == true,
            $"The program wrote caf\u00e9 1\u202f234,5 \u6f22 but its run answered something else: {ran.GetProperty("output").GetString()}");
    }

    /// <summary>An agent's run of a program reading a key, which refuses the Run window's pipe, answers that it needs a
    /// terminal and how to give it one; run with terminal true then runs it in a Terminal tab, where it reads the key the
    /// agent types there, and the configuration keeps running there.</summary>
    private async Task An_agents_run_of_a_program_needing_a_terminal_says_so_and_terminal_true_runs_it_in_one()
    {
        await TakeFolderAsync();
        Write("Program.cs", "Console.WriteLine(\"key?\");\nConsoleKeyInfo key = Console.ReadKey(intercept: true);\nConsole.WriteLine($\"key {key.KeyChar}\");\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 60 });
        string status = ran.GetProperty("status").GetString()!;
        Assert.True(status.EndsWith($". Sum needs a terminal for Console.ReadKey: the Run window gives it pipes: {McpTools.Run} with terminal true runs it in a Terminal tab", StringComparison.Ordinal),
            $"The run of a program refusing the Run window's pipe did not say it needs a terminal: {ran}");

        JsonElement inTerminal = await Call(client, McpTools.Run, new() { ["terminal"] = true, ["waitSeconds"] = 5 });
        Assert.True(inTerminal.GetProperty("stillRunning").GetBoolean(), $"The program did not wait for a key in the terminal: {inTerminal}");
        string typed = Text(await Result(client, McpTools.TerminalRun, new() { ["command"] = "q", ["waitSeconds"] = 30 }));
        JsonElement output = await Call(client, McpTools.Output, new() { ["of"] = "run" });
        Assert.True(output.GetString()?.Contains("key q", StringComparison.Ordinal) == true, $"The program in the terminal did not read the key typed there ({typed}): {output}");
        JsonElement again = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 5 });
        Assert.True(again.GetProperty("stillRunning").GetBoolean() && again.GetProperty("status").GetString()?.Contains("needs a terminal", StringComparison.Ordinal) == false,
            $"The configuration did not keep running in the terminal: {again}");
    }

    /// <summary>An agent's profile of a program reading a key, which refuses the Run window's pipe, answers that it needs a
    /// terminal and how to give it one, and its debug_start stops at the exception and answers so there; debug_start with
    /// terminal true then debugs it in a Terminal tab, where it reads the key the agent types there with debug_input, and
    /// profile with terminal true profiles it there as it waits for its key.</summary>
    private async Task An_agents_debug_start_of_a_program_needing_a_terminal_says_so_at_its_stop_and_terminal_true_debugs_it_in_one()
    {
        await TakeFolderAsync();
        Write("Program.cs", "Console.WriteLine(\"key?\");\nConsoleKeyInfo key = Console.ReadKey(intercept: true);\nConsole.WriteLine($\"key {key.KeyChar}\");\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement profiled = await Call(client, McpTools.Profile);
        Assert.True(profiled.TryGetProperty("needsTerminal", out JsonElement need) && need.GetString() ==
            $"Sum needs a terminal for Console.ReadKey: the Run window gives it pipes: {McpTools.Profile} with terminal true profiles it in a Terminal tab",
            $"The profile of a program refusing the Run window's pipe did not say it needs a terminal: {profiled}");

        JsonElement stopped = await Call(client, McpTools.DebugStart, new() { ["waitSeconds"] = 60 });
        Assert.True(stopped.GetProperty("state").GetString() == "paused" && stopped.GetProperty("status").GetString()?.EndsWith(
            $". Sum needs a terminal for Console.ReadKey: the Debug window gives it pipes: {McpTools.DebugStart} with terminal true debugs it in a Terminal tab", StringComparison.Ordinal) == true,
            $"The stop of a program refusing the Debug window's pipe did not say it needs a terminal: {stopped}");

        JsonElement inTerminal = await Call(client, McpTools.DebugStart, new() { ["terminal"] = true, ["waitSeconds"] = 5 });
        Assert.True(inTerminal.GetProperty("state").GetString() == "running", $"The program did not wait for a key in the terminal: {inTerminal}");
        string typed = Text(await Result(client, McpTools.DebugInput, new() { ["text"] = "q", ["newline"] = false }));
        string shown = string.Empty;
        Assert.True(await Waiting.UntilAsync(async () => (shown = (await Call(client, McpTools.Output, new() { ["of"] = "debug" })).GetString() ?? string.Empty).Contains("key q", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30), Cancel, TimeSpan.FromMilliseconds(100)), $"The program debugged in the terminal did not read the key typed there ({typed}): {shown}");
        Assert.StartsWith("Sum, debugged", shown, StringComparison.Ordinal);

        JsonElement waiting = await Call(client, McpTools.Profile, new() { ["seconds"] = 2, ["terminal"] = true });
        Assert.True(waiting.GetProperty("samples").GetInt32() > 0 && !waiting.TryGetProperty("needsTerminal", out _),
            $"The profile of the program in the terminal as it waited for its key took no samples, or said it needs a terminal: {waiting}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>A run refused for a reason of its own (here a launch profile's command named RetryFailed, which Passenger does
    /// not run) is answered with that reason alone, though the last build's errors are still in Problems; a run refused for
    /// the build it ran failing still names them.</summary>
    private async Task An_agents_run_refused_for_anything_but_its_own_failed_build_names_no_build_errors()
    {
        await TakeFolderAsync();
        Write("Program.cs", "int count = \"three\";\nConsole.WriteLine(count);\n");
        Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
        File.WriteAllText(Path.Combine(Folder, "Properties", "launchSettings.json"), "{\n  \"profiles\": {\n    \"Jobs\": { \"commandName\": \"RetryFailed\" }\n  }\n}\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        JsonElement error = built.GetProperty("problems").EnumerateArray().FirstOrDefault(p => p.GetProperty("severity").GetString() == "error");
        Assert.True(!built.GetProperty("succeeded").GetBoolean() && error.ValueKind == JsonValueKind.Object, $"The build of a string given to an int did not fail with an error: {built}");
        string code = error.GetProperty("code").GetString()!;

        CallToolResult jobs = await Result(client, McpTools.Run, new() { ["configuration"] = "Sum: Jobs" });
        Assert.True(jobs.IsError == true && Text(jobs).Contains("Passenger does not know the RetryFailed command", StringComparison.Ordinal),
            $"The run of a profile Passenger does not run was answered: {Text(jobs)}");
        Assert.False(Text(jobs).Contains(code, StringComparison.Ordinal), $"A run refused before any build named the last build's errors: {Text(jobs)}");

        CallToolResult sum = await Result(client, McpTools.Run, new() { ["configuration"] = "Sum" });
        Assert.True(sum.IsError == true && Text(sum).Contains($"{code} ", StringComparison.Ordinal) && Text(sum).Contains("Program.cs:1:", StringComparison.Ordinal),
            $"A run refused for its own build's failure did not name that build's error: {Text(sum)}");
    }

    /// <summary>An agent's run or debug_start of another configuration that is refused (a launch profile of a command
    /// Passenger does not run) leaves the window's chosen configuration as it was, so the agent's next call and the person's
    /// Run act on the one they chose; a run that starts chooses its configuration, as a person's Run does.</summary>
    private async Task An_agents_refused_run_of_another_configuration_leaves_the_chosen_one_and_a_started_run_chooses_its_own()
    {
        await TakeFolderAsync();
        Write("Program.cs", "Console.WriteLine(\"ran\");\n");
        Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
        File.WriteAllText(Path.Combine(Folder, "Properties", "launchSettings.json"),
            "{\n  \"profiles\": {\n    \"First\": { \"commandName\": \"Project\" },\n    \"Again\": { \"commandName\": \"Project\" },\n    \"Jobs\": { \"commandName\": \"RetryFailed\" }\n  }\n}\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        async Task<string?> ChosenAsync() => (await Call(client, McpTools.Workspace)).GetProperty("chosenConfiguration").GetString();
        string? chosen = await ChosenAsync();
        Assert.True(chosen is not null && chosen != "Sum: Jobs" && chosen != "Sum: Again", $"The folder opened on {chosen} chosen");

        CallToolResult ran = await Result(client, McpTools.Run, new() { ["configuration"] = "Sum: Jobs" });
        Assert.True(ran.IsError == true, $"The run of a profile Passenger does not run was answered: {Text(ran)}");
        Assert.Equal(chosen, await ChosenAsync());
        CallToolResult debugged = await Result(client, McpTools.DebugStart, new() { ["configuration"] = "Sum: Jobs" });
        Assert.True(debugged.IsError == true, $"The debugged start of a profile Passenger does not run was answered: {Text(debugged)}");
        Assert.Equal(chosen, await ChosenAsync());

        JsonElement again = await Call(client, McpTools.Run, new() { ["configuration"] = "Sum: Again", ["waitSeconds"] = 60 });
        Assert.True(again.GetProperty("output").GetString()?.Contains("ran\n", StringComparison.Ordinal) == true, $"The profile's run did not print its line: {again}");
        Assert.Equal("Sum: Again", await ChosenAsync());
    }

    /// <summary>An agent's run of a web app, which serves until it is stopped, answers once the app's host has started,
    /// still running and saying where it listens, rather than waiting out its time for an end that never comes; the app
    /// answers there.</summary>
    private async Task An_agents_run_of_a_web_app_answers_once_it_serves_saying_where_it_listens()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-web-run-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Web.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(folder, "Program.cs", "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/\", () => \"served\");\napp.Run();\n");
        // Port 0: where it listens is the port the app took, which only its own words tell.
        TestFolders.Write(folder, "Properties/launchSettings.json",
            "{\n  \"profiles\": {\n    \"Serve\": { \"commandName\": \"Project\", \"applicationUrl\": \"http://127.0.0.1:0\" }\n  }\n}\n");
        await Restore.RunAsync(Path.Combine(folder, "Web.csproj"));
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        // Far longer than the app takes to start, which the answer is not to wait out.
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = "Web: Serve", ["waitSeconds"] = 600 });
        Match listening = Regex.Match(ran.GetProperty("status").GetString() ?? string.Empty, @"^Running Web: Serve, listening at (http://127\.0\.0\.1:\d+)$");
        Assert.True(ran.GetProperty("stillRunning").GetBoolean() && listening.Success, $"run of a web app answered: {ran.GetRawText()}");
        using (System.Net.Http.HttpClient http = new())
        {
            Assert.Equal("served", await http.GetStringAsync(new Uri(listening.Groups[1].Value), TestContext.Current.CancellationToken));
        }
        await Call(client, McpTools.Stop);
    }

    /// <summary>A package an agent installs or removes is in or out of the project file and Directory.Packages.props the
    /// editor holds once the tool answers, which names the version installed and the files it changed, so the agent's
    /// next look sees what it did, not the text from before.</summary>
    private async Task An_agents_package_installed_and_removed_shows_in_the_open_project_file_as_each_tool_answers()
    {
        string library = PathNames.Canonical(TestFolders.Create("mcp-nuget-"));
        DeleteAtEnd(library);
        string feed = Directory.CreateDirectory(Path.Combine(library, "feed")).FullName;
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        // Only the folder feed, and restores kept out of the person's own package cache.
        File.WriteAllText(Path.Combine(library, "nuget.config"),
            "<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"local\" value=\"feed\" />\n  </packageSources>\n"
            + "  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"),
            TestProjects.Library());
        // Central package management: the version goes in Directory.Packages.props.
        File.WriteAllText(Path.Combine(library, "Directory.Packages.props"),
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        await using McpClient client = await Start(["--mcp", library], languageServer: false);
        Dictionary<string, object?> project = new() { ["path"] = "Lib.csproj" };
        Dictionary<string, object?> versions = new() { ["path"] = "Directory.Packages.props" };
        Assert.DoesNotContain("Passenger.Sample", (await Call(client, McpTools.EditorText, project)).GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Passenger.Sample", (await Call(client, McpTools.EditorText, versions)).GetString(), StringComparison.Ordinal);
        JsonElement found = Assert.Single((await Call(client, McpTools.NugetSearch, new() { ["query"] = "Passenger" })).EnumerateArray());
        Assert.Equal(("Passenger.Sample", "1.0.0"), (found.GetProperty("id").GetString(), found.GetProperty("version").GetString()));

        JsonElement install = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample" });
        Assert.Equal(("Passenger.Sample", "1.0.0", "Lib"), (install.GetProperty("package").GetString(), install.GetProperty("version").GetString(), install.GetProperty("project").GetString()));
        Assert.Equal(["Directory.Packages.props", "Lib.csproj"], install.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal));
        string installed = (await Call(client, McpTools.EditorText, project)).GetString()!;
        Assert.True(installed.Contains("<PackageReference Include=\"Passenger.Sample\" />", StringComparison.Ordinal),
            $"nuget_install answered with the open project file still as it was:\n{installed}");
        string pinned = (await Call(client, McpTools.EditorText, versions)).GetString()!;
        Assert.True(pinned.Contains("<PackageVersion Include=\"Passenger.Sample\" Version=\"1.0.0\" />", StringComparison.Ordinal),
            $"nuget_install answered with the open Directory.Packages.props still as it was:\n{pinned}");

        JsonElement remove = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["remove"] = true });
        Assert.Contains("Lib.csproj", remove.GetProperty("changed").EnumerateArray().Select(f => f.GetString()));
        Assert.True(!remove.TryGetProperty("version", out JsonElement left) || left.ValueKind == JsonValueKind.Null, $"nuget_install with remove answered a version: {remove}");
        string removed = (await Call(client, McpTools.EditorText, project)).GetString()!;
        Assert.False(removed.Contains("Passenger.Sample", StringComparison.Ordinal), $"nuget_install with remove answered with the open project file still holding the package:\n{removed}");
    }

    /// <summary>nuget_search names, of each package found, its newest version and those the projects have that the stand-in
    /// feed's VulnerabilityInfo says are vulnerable or its registration says their owner deprecated, with why, what to use
    /// instead and the projects that have them, so an agent knows before it installs; a package with neither says none.</summary>
    private async Task An_agents_package_search_names_the_vulnerable_and_deprecated_versions_with_why()
    {
        const string Advisory = "https://github.com/advisories/GHSA-pass-mcp0-test";
        string folder = PathNames.Canonical(TestFolders.Create("mcp-nuget-deprecated-"));
        DeleteAtEnd(folder);
        await using StandInFeed feed = new();
        feed.Add("Passenger.Legacy", "1.0.0");
        feed.Add("Passenger.Legacy", "2.0.0");
        feed.Add("Passenger.Modern", "2.0.0");
        feed.Advise("Passenger.Legacy", 2, Advisory, "[1.0.0, 1.0.1)");
        feed.Deprecate("Passenger.Legacy", "1.0.0", ["Legacy"], "Kept no more.", "Passenger.Modern", "[2.0.0, )");
        feed.Deprecate("Passenger.Legacy", "2.0.0", ["CriticalBugs"], "Broken past repair.");
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" allowInsecureConnections="true" />
              </auditSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"),
            TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Legacy\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        JsonElement[] found = [.. (await Call(client, McpTools.NugetSearch, new() { ["query"] = "Passenger" })).EnumerateArray()];
        JsonElement legacy = Assert.Single(found, p => p.GetProperty("id").GetString() == "Passenger.Legacy");
        Assert.Equal([$"1.0.0 (Lib): high severity, {Advisory}"], Texts(legacy, "vulnerable"));
        Assert.Equal(["2.0.0: critical bugs; Broken past repair.", "1.0.0 (Lib): legacy, no longer maintained; use Passenger.Modern 2.0.0 or later instead; Kept no more."],
            Texts(legacy, "deprecated"));
        JsonElement modern = Assert.Single(found, p => p.GetProperty("id").GetString() == "Passenger.Modern");
        Assert.True(Texts(modern, "vulnerable").Length == 0 && Texts(modern, "deprecated").Length == 0 && Texts(modern, "unknown").Length == 0,
            $"nuget_search said Passenger.Modern, which nothing advises against or deprecates, was: {modern.GetRawText()}");

        static string[] Texts(JsonElement package, string member) =>
            !package.TryGetProperty(member, out JsonElement said) || said.ValueKind == JsonValueKind.Null ? []
            : said.ValueKind == JsonValueKind.Array ? [.. said.EnumerateArray().Select(t => t.GetString()!)]
            : [said.GetString()!];
    }

    /// <summary>nuget_install of a version the stand-in feed's VulnerabilityInfo says is vulnerable and its registration says
    /// its owner deprecated puts it in and says both as nuget_search does, the gravest advisory with the least version above
    /// it that none affects, and why, what to use instead and the owner's words; moved to a version with neither, it says none.</summary>
    private async Task An_agents_package_install_of_a_vulnerable_and_deprecated_version_says_both_and_the_version_that_fixes_it()
    {
        const string Advisory = "https://github.com/advisories/GHSA-pass-mcp1-test";
        string folder = PathNames.Canonical(TestFolders.Create("mcp-nuget-install-warned-"));
        DeleteAtEnd(folder);
        await using StandInFeed feed = new();
        feed.Add("Passenger.Legacy", "1.0.0");
        feed.Add("Passenger.Legacy", "1.0.1");
        feed.Add("Passenger.Legacy", "2.0.0");
        feed.Advise("Passenger.Legacy", 2, Advisory, "[1.0.0, 1.0.1)");
        feed.Deprecate("Passenger.Legacy", "1.0.0", ["Legacy"], "Kept no more.", "Passenger.Modern", "[2.0.0, )");
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" allowInsecureConnections="true" />
              </auditSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), TestProjects.Library());
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        JsonElement installed = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Legacy", ["version"] = "1.0.0" });
        Assert.Equal("1.0.0", installed.GetProperty("version").GetString());
        Assert.Equal([$"high severity, {Advisory}; fixed in 1.0.1"], Said(installed, "vulnerable"));
        Assert.Equal(["legacy, no longer maintained; use Passenger.Modern 2.0.0 or later instead; Kept no more."], Said(installed, "deprecated"));

        JsonElement moved = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Legacy", ["version"] = "2.0.0" });
        Assert.True(moved.GetProperty("version").GetString() == "2.0.0" && Said(moved, "vulnerable").Length == 0 && Said(moved, "deprecated").Length == 0
            && Said(moved, "unknown").Length == 0, $"nuget_install of Passenger.Legacy 2.0.0, which nothing advises against or deprecates, answered: {moved.GetRawText()}");

        static string[] Said(JsonElement change, string member) =>
            !change.TryGetProperty(member, out JsonElement said) || said.ValueKind == JsonValueKind.Null ? [] : [said.GetString()!];
    }

    /// <summary>A project built for two frameworks that gets a vulnerable version through another package in one: an agent
    /// finds it among the project file's diagnostics on what brings it in, is offered its fix there among code_actions, and
    /// nuget_install with that framework references the fixed version there alone, the other framework's reference as it
    /// was, as the NuGet window's Fix does; a framework the project is not built for is refused, naming those it is.</summary>
    private async Task An_agents_install_for_one_framework_fixes_the_vulnerable_version_it_gets_through_another_package_there_alone()
    {
        const string Advisory = "https://github.com/advisories/GHSA-pass-mcp2-test";
        string folder = PathNames.Canonical(TestFolders.Create("mcp-nuget-framework-"));
        DeleteAtEnd(folder);
        await using StandInFeed feed = new();
        feed.Add("Passenger.Sample", "1.0.0");
        feed.Add("Passenger.Sample", "1.0.1");
        feed.Add("Passenger.Charts", "1.0.0", ("Passenger.Sample", "1.0.0"));
        feed.Advise("Passenger.Sample", 1, Advisory, "[1.0.0, 1.0.1)");
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" allowInsecureConnections="true" />
              </auditSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        string own = "  <ItemGroup Condition=\"'$(TargetFramework)' == 'net11.0'\">\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.1\" />\n  </ItemGroup>\n";
        string lib = Path.Combine(folder, "Lib.csproj");
        File.WriteAllText(lib, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>net11.0;{TestProjects.Framework}</TargetFrameworks>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageReference Include=\"Passenger.Charts\" Version=\"1.0.0\" />\n  </ItemGroup>\n" + own + "</Project>\n");
        await Restore.RunAsync(lib);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        JsonElement[] found = [.. (await Call(client, McpTools.Diagnostics, new() { ["path"] = "Lib.csproj" })).EnumerateArray()];
        Assert.True(found.Any(f => f.GetProperty("code").GetString() == "NU1902" && f.GetProperty("line").GetInt32() == 6),
            $"diagnostics of Lib.csproj leave out the vulnerable Passenger.Sample 1.0.0 that Passenger.Charts brings in for {TestProjects.Framework}: "
            + string.Join("; ", found.Select(f => f.GetRawText())));
        string[] offered = [.. (await Call(client, McpTools.CodeActions, new() { ["path"] = "Lib.csproj", ["line"] = 6, ["column"] = 32 })).EnumerateArray().Select(a => a.GetString()!)];
        Assert.True(offered.Any(a => a.StartsWith($"Reference Passenger.Sample's fixed version for {TestProjects.Framework}", StringComparison.Ordinal)),
            $"code_actions at Passenger.Charts' reference offer [{string.Join("; ", offered)}], not the fix of what it brings in");

        CallToolResult refused = await Result(client, McpTools.NugetInstall,
            new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.0.1", ["framework"] = "net9.0" });
        Assert.True(refused.IsError == true && Text(refused).Contains($"Lib is not built for net9.0: it builds for net11.0 and {TestProjects.Framework}", StringComparison.Ordinal),
            $"nuget_install for a framework Lib is not built for answered: {Text(refused)}");
        JsonElement installed = await Call(client, McpTools.NugetInstall,
            new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.0.1", ["framework"] = TestProjects.Framework });
        string text = File.ReadAllText(lib);
        Assert.True(installed.GetProperty("changed").EnumerateArray().Select(c => c.GetString()).Contains("Lib.csproj") && text.Contains(own, StringComparison.Ordinal)
            && text.Contains($"  <ItemGroup Condition=\"'$(TargetFramework)' == '{TestProjects.Framework}'\">\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.1\" />\n",
                StringComparison.Ordinal),
            $"nuget_install for {TestProjects.Framework} answered {installed.GetRawText()}, leaving Lib.csproj as:\n{text}");
    }

    /// <summary>In a project that restores with a lock file, the files nuget_install names are all it changed: the lock file
    /// its restore made, then rewrote for the next version, so an agent committing them leaves a locked restore whole.</summary>
    private async Task An_agents_package_install_in_a_project_restoring_with_a_lock_file_names_the_lock_file_among_the_files_it_changed()
    {
        string library = PathNames.Canonical(TestFolders.Create("mcp-nuget-lock-"));
        DeleteAtEnd(library);
        string feed = Directory.CreateDirectory(Path.Combine(library, "feed")).FullName;
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.1.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.1.0"));
        File.WriteAllText(Path.Combine(library, "nuget.config"),
            "<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"local\" value=\"feed\" />\n  </packageSources>\n"
            + "  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>"));
        string lockFile = Path.Combine(library, "packages.lock.json");
        await using McpClient client = await Start(["--mcp", library], languageServer: false);

        JsonElement made = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.0.0" });
        string[] changed = [.. made.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal)];
        Assert.True(changed.SequenceEqual(["Lib.csproj", "packages.lock.json"]) && File.Exists(lockFile),
            $"nuget_install answered that it changed [{string.Join(", ", changed)}], where its restore made packages.lock.json too ({(File.Exists(lockFile) ? "there" : "not there")})");

        JsonElement moved = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.1.0" });
        changed = [.. moved.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal)];
        string locked = File.ReadAllText(lockFile);
        Assert.True(changed.SequenceEqual(["Lib.csproj", "packages.lock.json"]) && locked.Contains("\"resolved\": \"1.1.0\"", StringComparison.Ordinal),
            $"nuget_install's update answered that it changed [{string.Join(", ", changed)}], the lock file now holding:\n{locked}");
    }

    /// <summary>Where a Directory.Build.props names the file central package versions are kept in (DirectoryPackagesPropsPath,
    /// as eng/Versions.props, which on Windows is not the file's full path), nuget_install writes the version there and
    /// names that file among those it changed, not the Directory.Packages.props nearest the project, which the evaluation
    /// never reads.</summary>
    private Task An_agents_package_install_under_central_versions_kept_where_the_build_names_names_that_file_among_those_it_changed() =>
        InstallUnderCentralVersionsAt("eng/Versions.props");

    /// <summary>Where a Directory.Build.props names the central versions file through a parent folder, on every system a
    /// spelling other than its full path, nuget_install writes the version there as where it names it plainly.</summary>
    private Task An_agents_package_install_under_central_versions_named_through_a_parent_folder_writes_the_version_there() =>
        InstallUnderCentralVersionsAt("Lib/../eng/Versions.props");

    private async Task InstallUnderCentralVersionsAt(string spelled)
    {
        string root = PathNames.Canonical(TestFolders.Create("mcp-nuget-central-"));
        DeleteAtEnd(root);
        string feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName;
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        File.WriteAllText(Path.Combine(root, "nuget.config"),
            "<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"local\" value=\"feed\" />\n  </packageSources>\n"
            + "  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
            $"<Project>\n  <PropertyGroup>\n    <DirectoryPackagesPropsPath>$(MSBuildThisFileDirectory){spelled}</DirectoryPackagesPropsPath>\n  </PropertyGroup>\n</Project>\n");
        string central = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "eng")).FullName, "Versions.props");
        File.WriteAllText(central, "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        string library = Directory.CreateDirectory(Path.Combine(root, "Lib")).FullName;
        string nearest = Path.Combine(library, "Directory.Packages.props");
        const string Unread = "<Project>\n  <ItemGroup>\n  </ItemGroup>\n</Project>\n";
        File.WriteAllText(nearest, Unread);
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        await using McpClient client = await Start(["--mcp", root], languageServer: false);

        JsonElement made = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.0.0" });
        string[] changed = [.. made.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal)];
        string versions = File.ReadAllText(central);
        Assert.True(versions.Contains("\"Passenger.Sample\"", StringComparison.Ordinal) && versions.Contains("\"1.0.0\"", StringComparison.Ordinal),
            $"nuget_install did not write the version to {central}, which DirectoryPackagesPropsPath names as {spelled}:\n{versions}");
        string project = File.ReadAllText(Path.Combine(library, "Lib.csproj"));
        Assert.True(project.Contains("<PackageReference Include=\"Passenger.Sample\" />", StringComparison.Ordinal),
            $"nuget_install did not reference the package in Lib.csproj with its version left to the central file:\n{project}");
        string assets = Path.Combine(library, "obj", "project.assets.json");
        Assert.True(File.Exists(assets) && File.ReadAllText(assets).Contains("Passenger.Sample/1.0.0", StringComparison.Ordinal),
            "nuget_install left Lib unrestored with the package, so its build would not find it");
        string[] expected = [.. new[] { Path.Combine("Lib", "Lib.csproj"), Path.Combine("eng", "Versions.props") }.Order(StringComparer.Ordinal)];
        Assert.True(changed.SequenceEqual(expected),
            $"nuget_install answered that it changed [{string.Join(", ", changed)}], where dotnet wrote the version to eng/Versions.props: "
            + "a package change that names, records and puts back the wrong central file leaves the version out of Undo and of what an agent commits");
        Assert.True(File.ReadAllText(nearest) == Unread, $"The Directory.Packages.props nearest the project, which its evaluation never reads, was changed:\n{File.ReadAllText(nearest)}");
    }

    /// <summary>Git's tools on a repository, a merge stopped for a conflict among them: the agent finds it in vcs_status (what
    /// is under way, each side's branch, what each did to the file), resolves it with theirs, and its commit finishes the
    /// merge; and a command in the IDE's terminal.</summary>
    private async Task An_agent_reads_and_commits_changes_resolves_a_merges_conflict_and_runs_a_command_in_the_ides_terminal()
    {
        string repository = TestFolders.Create("mcp-git-");
        try
        {
            File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\nthree\n");
            await TestGit.InitAsync(repository);
            File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n2\nthree\n");
            File.WriteAllText(Path.Combine(repository, "New.txt"), "new\n");

            await using McpClient client = await Start(["--mcp", repository]);
            JsonElement status = await Call(client, McpTools.VcsStatus);
            Assert.Equal("main", status.GetProperty("branch").GetString());
            Assert.Equal(["Notes.txt modified", "New.txt unversioned"], status.GetProperty("changes").EnumerateArray()
                .Select(c => $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}").Order(StringComparer.Ordinal).Reverse());
            string diff = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "Notes.txt" })).GetString()!;
            Assert.Contains("-two\n+2\n", diff, StringComparison.Ordinal);

            // With no paths, every change but the new file goes in.
            Assert.EndsWith(" Two as a number", (await Call(client, McpTools.VcsCommit, new() { ["message"] = "Two as a number" })).GetString(), StringComparison.Ordinal);
            JsonElement log = await Call(client, McpTools.VcsLog, new() { ["count"] = 5 });
            Assert.Equal(["Two as a number", "First"], log.EnumerateArray().Select(c => c.GetProperty("subject").GetString()));
            Assert.Equal(["New.txt"], (await Call(client, McpTools.VcsStatus)).GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()));

            JsonElement stashed = Assert.Single((await Call(client, McpTools.VcsStash, new() { ["action"] = "push", ["message"] = "the new file" })).EnumerateArray());
            Assert.EndsWith("the new file", stashed.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(repository, "New.txt")), "Stash Changes left the new file in place");
            Assert.Empty((await Call(client, McpTools.VcsStash, new() { ["action"] = "pop", ["index"] = 0 })).EnumerateArray());
            Assert.Equal("new\n", File.ReadAllText(Path.Combine(repository, "New.txt")));

            // A branch an agent names is a name, never one of git's options: git switch --orphan=x would empty the tree.
            CallToolResult orphan = await Result(client, McpTools.VcsCheckout, new() { ["branch"] = "--orphan=x" });
            Assert.True(orphan.IsError, "vcs_checkout took the branch --orphan=x for an option, and checked out an empty branch");
            Assert.True(File.Exists(Path.Combine(repository, "Notes.txt")), "Checking out the branch --orphan=x emptied the working tree");
            Assert.Equal("main", (await Call(client, McpTools.VcsStatus)).GetProperty("branch").GetString());

            string notes = Path.Combine(repository, "Notes.txt");
            await TestGit.RunAsync(repository, "switch", "-q", "-c", "feature");
            File.WriteAllText(notes, "one\nTWO\nthree\n");
            await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in capitals");
            await TestGit.RunAsync(repository, "switch", "-q", "main");
            File.WriteAllText(notes, "one\nII\nthree\n");
            await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in Roman");
            ChildResult merging = await ChildProcesses.RunAsync(ChildProcesses.Command("git", ["merge", "feature"], repository), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
            Assert.False(merging.Succeeded, $"The merge was to stop for its conflict: {merging.Output}");
            JsonElement conflicts = (await Call(client, McpTools.VcsStatus)).GetProperty("conflicts");
            Assert.Equal("Merging branch feature into branch main", conflicts.GetProperty("operation").GetString());
            Assert.Equal(("main", "feature"), (conflicts.GetProperty("yours").GetString(), conflicts.GetProperty("theirs").GetString()));
            JsonElement conflicted = Assert.Single(conflicts.GetProperty("files").EnumerateArray());
            Assert.Equal(("Notes.txt", "modified", "modified"), (conflicted.GetProperty("path").GetString(), conflicted.GetProperty("yours").GetString(), conflicted.GetProperty("theirs").GetString()));
            CallToolResult early = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Half a merge" });
            Assert.True(early.IsError == true, "A commit was made with a file still in conflict");
            Assert.Equal("Notes.txt resolved; no conflicts are left", (await Call(client, McpTools.VcsResolve, new() { ["path"] = "Notes.txt", ["take"] = "theirs" })).GetString());
            Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(notes));
            JsonElement resolved = await Call(client, McpTools.VcsStatus);
            Assert.True(!resolved.TryGetProperty("conflicts", out JsonElement left) || left.ValueKind == JsonValueKind.Null, $"vcs_status still lists conflicts: {left}");
            await Call(client, McpTools.VcsCommit, new() { ["message"] = "Merge feature" });
            Assert.Equal(3, (await TestGit.RunAsync(repository, "rev-list", "--parents", "-n", "1", "HEAD")).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal("one\nTWO\nthree\n", await TestGit.RunAsync(repository, "show", "HEAD:Notes.txt"));

            // Windows' own PowerShell (5.1, where pwsh is not installed) has no &&, and sh has no PowerShell subexpressions.
            string command = OperatingSystem.IsWindows() ? "echo (6*7); ls" : "echo $((6*7)) && ls";
            string answer = (await Call(client, McpTools.TerminalRun, new() { ["command"] = command })).GetString()!;
            Assert.Contains("42", answer, StringComparison.Ordinal);
            Assert.Contains("Notes.txt", answer, StringComparison.Ordinal);
            string screen = (await Call(client, McpTools.Output, new() { ["of"] = "terminal" })).GetString()!;
            Assert.True(screen.Contains("42", StringComparison.Ordinal), $"output of terminal did not show what the command printed:\n{screen}");
            if (OperatingSystem.IsWindows())
            {
                // The shells' history is the run's (HISTFILE), never the person's.
                string history = Path.Combine(TestRun.Root, "history");
                Assert.True(File.Exists(history) && File.ReadAllText(history).Contains(command, StringComparison.Ordinal),
                    $"PowerShell kept the command typed into the terminal, {command}, elsewhere than the run's history ({history}): in the person's own");
            }
            else
            {
                // Past the scrollback's limit, where each line printed drops the oldest.
                int beyond = global::Passenger.Terminal.TerminalScreen.ScrollbackLimit + 100;
                await Call(client, McpTools.TerminalRun, new() { ["command"] = $"seq 1 {beyond}" });
                string past = (await Call(client, McpTools.TerminalRun, new() { ["command"] = "echo one; echo two" })).GetString()!;
                Assert.True(past.Contains("one\ntwo", StringComparison.Ordinal), $"past the scrollback's limit, terminal_run answered a command that printed one and two with:\n{past}");
            }
        }
        finally
        {
            TestFolders.Delete(repository);
        }
    }

    /// <summary>Two terminal commands an agent sends at once take turns in the Agent Shell, each answer holding its own
    /// command's output and none of the other's; and settings_set refuses an agent the shell's path, the person's alone,
    /// nothing changed, the Agent Shell still taking commands.</summary>
    private async Task An_agents_terminal_commands_sent_at_once_take_turns_each_answering_its_own_output()
    {
        string folder = TestFolders.Create("mcp-turns-");
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        // Each prints, waits a second and prints again, so a command typed beside it lands amid its output.
        Task<JsonElement> alpha = Call(client, McpTools.TerminalRun, new() { ["command"] = "echo alpha-begins; sleep 1; echo alpha-ends" });
        Task<JsonElement> beta = Call(client, McpTools.TerminalRun, new() { ["command"] = "echo beta-begins; sleep 1; echo beta-ends" });
        string first = (await alpha).GetString()!;
        string second = (await beta).GetString()!;
        Assert.True(first.Contains("alpha-ends", StringComparison.Ordinal) && !first.Contains("beta", StringComparison.Ordinal),
            $"The agent's first command, sent beside another, answered what was not its own:\n{first}");
        Assert.True(second.Contains("beta-ends", StringComparison.Ordinal) && !second.Contains("alpha", StringComparison.Ordinal),
            $"The agent's second command, sent beside another, answered what was not its own:\n{second}");

        CallToolResult refused = await Result(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["terminalShell"] = "\"\"" } });
        Assert.True(refused.IsError == true && Text(refused).Contains("terminalShell names a program Passenger runs, or code it loads: the person sets it in Settings", StringComparison.Ordinal),
            $"settings_set of the shell's path, the person's alone, answered: {Text(refused)}");
        JsonElement shell = (await Call(client, McpTools.Settings)).GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "terminalShell");
        Assert.True(shell.GetProperty("value").ValueKind == JsonValueKind.Null, $"The refused shell path was kept all the same: {shell.GetRawText()}");
        Assert.Contains("gamma", (await Call(client, McpTools.TerminalRun, new() { ["command"] = "echo gamma" })).GetString()!, StringComparison.Ordinal);
    }

    /// <summary>terminal_run waits up to waitSeconds for the shell to prompt again: a command quiet for longer than a moment
    /// answers what it printed once it ended; one still running as the wait ends says so, leaving out what it prints later;
    /// and a cd typed behind it, whose prompt names another folder, answers once it has run, its output there too.</summary>
    private async Task An_agents_terminal_command_is_waited_for_until_the_shell_prompts_and_one_still_running_says_so()
    {
        string folder = TestFolders.Create("mcp-terminal-wait-");
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Inner/inner-marker.txt", "inner\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        // Each prints a number its command does not hold, so a line of it is the command's output, never its echo wrapped.
        string sleep = OperatingSystem.IsWindows() ? "Start-Sleep" : "sleep";
        string Product(int factor) => OperatingSystem.IsWindows() ? $"echo ({factor}*1001)" : $"echo $(({factor}*1001))";
        static bool Printed(string answer, int factor) => answer.Split('\n').Any(line => line.Trim() == $"{factor * 1001}");
        string slept = (await Call(client, McpTools.TerminalRun, new() { ["command"] = $"{sleep} 4; {Product(41)}", ["waitSeconds"] = 60 })).GetString()!;
        Assert.True(Printed(slept, 41) && !slept.Contains("Still running", StringComparison.Ordinal),
            $"terminal_run of a command quiet for 4 s, given 60 s, answered before it ended:\n{slept}");

        string held = (await Call(client, McpTools.TerminalRun, new() { ["command"] = $"{sleep} 6; {Product(42)}", ["waitSeconds"] = 2 })).GetString()!;
        Assert.True(held.Contains($"\nStill running after 2 s: {McpTools.Output} of terminal shows what it prints from here", StringComparison.Ordinal) && !Printed(held, 42),
            $"terminal_run of a command running past its 2 s answered as if it had ended:\n{held}");

        string moved = (await Call(client, McpTools.TerminalRun, new() { ["command"] = "cd Inner", ["waitSeconds"] = 60 })).GetString()!;
        Assert.True(Printed(moved, 42) && !moved.Contains("Still running", StringComparison.Ordinal),
            $"terminal_run of a cd typed while a command ran, which prompts in another folder, did not answer once both had run:\n{moved}");
        string listed = (await Call(client, McpTools.TerminalRun, new() { ["command"] = "ls" })).GetString()!;
        Assert.True(listed.Contains("inner-marker.txt", StringComparison.Ordinal), $"The cd did not run in the Agent Shell, whose ls answered:\n{listed}");
    }

    /// <summary>What a program and a terminal say reaches an agent with its secrets masked by the one rule: a password a
    /// program writes and a token the folder's .env holds, in run's answer and output of run, and a password a command
    /// echoes and that token, in terminal_run's answer and output of terminal, each shown as the mask, the rest as written.</summary>
    private async Task What_a_program_and_a_terminal_say_reaches_an_agent_with_its_secrets_masked()
    {
        const string Password = "hunter2-from-the-program";
        const string Token = "tok-from-the-env-file-0123";
        const string Typed = "hunter3-typed-in-the-terminal";
        await TakeFolderAsync();
        Write(".env", $"SHOP_API_TOKEN={Token}\n");
        Write("Program.cs", $"Console.WriteLine(\"connecting with Password={Password}\");\nConsole.WriteLine(\"the token is {Token}\");\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 60 });
        string output = ran.GetProperty("output").GetString()!;
        string console = (await Call(client, McpTools.Output, new() { ["of"] = "run" })).GetString()!;
        foreach ((string what, string said) in (ReadOnlySpan<(string, string)>)[(McpTools.Run, output), ($"{McpTools.Output} of run", console)])
        {
            Assert.True(said.Contains($"connecting with Password={ContainerSecrets.Masked}\n", StringComparison.Ordinal)
                && said.Contains($"the token is {ContainerSecrets.Masked}\n", StringComparison.Ordinal)
                && !said.Contains(Password, StringComparison.Ordinal) && !said.Contains(Token, StringComparison.Ordinal),
                $"{what} gave an agent the program's secrets: {said}");
        }

        string echoed = (await Call(client, McpTools.TerminalRun, new() { ["command"] = $"echo DB_PASSWORD={Typed} {Token}" })).GetString()!;
        string screen = (await Call(client, McpTools.Output, new() { ["of"] = "terminal" })).GetString()!;
        foreach ((string what, string said) in (ReadOnlySpan<(string, string)>)[(McpTools.TerminalRun, echoed), ($"{McpTools.Output} of terminal", screen)])
        {
            Assert.True(said.Contains($"DB_PASSWORD={ContainerSecrets.Masked}", StringComparison.Ordinal)
                && !said.Contains(Typed, StringComparison.Ordinal) && !said.Contains(Token, StringComparison.Ordinal),
                $"{what} gave an agent the terminal's secrets: {said}");
        }
    }

    /// <summary>A Passenger makes the state it keeps the person's alone as it starts, where an earlier one left it open to
    /// every user: the state folder and its logs 0700, a log written before 0600, the folder's Local History store 0700
    /// with each folder and file it held put right, and the version of a .env it keeps from then on 0600.</summary>
    [Fact]
    public async Task The_state_its_logs_and_local_history_are_made_the_persons_alone_as_passenger_starts()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows keeps the state under the person's profile, theirs alone already, and has no file modes");
            return;
        }
        const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        string folder = PathNames.Canonical(TestFolders.Create("mcp-own-state-"));
        DeleteAtEnd(folder);
        System.IO.File.WriteAllText(Path.Combine(folder, ".env"), "SHOP_API_TOKEN=tok-before\n");
        string state = PathNames.Canonical(OwnState);
        string logs = Path.Combine(state, "logs");
        string history = Path.Combine(global::Passenger.App.State.ProjectState.FolderOf(state, folder), "localHistory");
        string earlier = Path.Combine(history, "blobs", "ab", new string('c', 30));
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.GetDirectoryName(earlier)!);
        System.IO.File.WriteAllText(Path.Combine(logs, "passenger.1.log"), "an earlier start's log\n");
        System.IO.File.WriteAllText(earlier, "an earlier version\n");
        foreach (string made in (string[])[state, logs, history, Path.GetDirectoryName(earlier)!])
        {
            System.IO.File.SetUnixFileMode(made, Folder | Others | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        foreach (string written in (string[])[Path.Combine(logs, "passenger.1.log"), earlier])
        {
            System.IO.File.SetUnixFileMode(written, File | Others);
        }

        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        await Call(client, McpTools.Edit, new() { ["path"] = ".env", ["oldText"] = "tok-before", ["newText"] = "tok-after" });
        string blobs = Path.Combine(history, "blobs");
        // A version is written beside its place first, under a name of its own ending in .tmp.
        IEnumerable<string> Kept() => Directory.EnumerateFiles(blobs, "*", SearchOption.AllDirectories).Where(f => f != earlier && !f.EndsWith(AtomicFile.Temporary, StringComparison.Ordinal));
        Assert.True(await Waiting.UntilAsync(() => Kept().Any(), TimeSpan.FromSeconds(30), Cancel), "Local History kept no version of the .env the agent edited");
        string kept = Kept().First();
        Assert.True(await Waiting.UntilAsync(() => System.IO.File.Exists(Path.Combine(logs, "passenger.log")), TimeSpan.FromSeconds(30), Cancel), "The Passenger wrote no log");
        List<string> open = [];
        foreach ((string path, UnixFileMode own) in (ReadOnlySpan<(string, UnixFileMode)>)[(state, Folder), (logs, Folder), (history, Folder), (blobs, Folder),
            (Path.GetDirectoryName(earlier)!, Folder), (Path.GetDirectoryName(kept)!, Folder), (Path.Combine(logs, "passenger.1.log"), File),
            (Path.Combine(logs, "passenger.log"), File), (earlier, File), (kept, File), (Path.Combine(history, "changes.log"), File)])
        {
            UnixFileMode mode = System.IO.File.GetUnixFileMode(path);
            if (mode != own)
            {
                open.Add($"{path} ({mode})");
            }
        }
        Assert.True(open.Count == 0, $"The state is not the person's alone: {string.Join("; ", open)}");
    }

    /// <summary>settings names what an agent may give a choosing setting, and settings_set takes one and refuses as Settings
    /// does, nothing changed: the editor's and the window's fonts (saying how many installed families it left out), the
    /// Ctrl keys Vim mode may give the keymap (saying what each does on either side), and a theme by its source and name,
    /// one its source does not list refused naming those it lists, a code colour scheme kept before settings_set answers,
    /// the carried Cubicle taken and one of the themes repository's fetched and kept.</summary>
    private async Task Settings_names_each_choice_an_agent_may_give_and_settings_set_takes_one_refusing_what_settings_refuses()
    {
        string folder = TestFolders.Create("mcp-settings-");
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        async Task<JsonElement> ListedAsync(string name) =>
            (await Call(client, McpTools.Settings)).GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

        JsonElement listed = await ListedAsync("editorFontFamily");
        string[] fonts = [.. listed.GetProperty("choices").EnumerateArray().Select(c => c.GetString()!)];
        Assert.True(fonts.Length == 1 && fonts[0] == CodeFonts.Carried, $"settings listed the editor's fonts as: {listed.GetRawText()}");
        Assert.True(!listed.TryGetProperty("note", out JsonElement codeNote) || codeNote.ValueKind == JsonValueKind.Null
            || codeNote.GetString()!.Contains("installed famil", StringComparison.Ordinal) && codeNote.GetString()!.Contains("settings_set takes any by its name", StringComparison.Ordinal),
            $"settings said of the installed code fonts it left out: {listed.GetRawText()}");
        CallToolResult refused = await Result(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["editorFontFamily"] = "Passenger Missing Font" } });
        Assert.True(refused.IsError == true && Text(refused).Contains("Font family Passenger Missing Font (Settings | Editor) is not a font installed on this computer", StringComparison.Ordinal),
            $"settings_set of a font family not installed answered: {Text(refused)}");
        string font = fonts[0];
        JsonElement changed = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["editorFontFamily"] = font } })).EnumerateArray());
        Assert.Equal(font, changed.GetProperty("after").GetString());

        JsonElement window = await ListedAsync("windowFontFamily");
        string[] faces = [.. window.GetProperty("choices").EnumerateArray().Select(c => c.GetString()!)];
        Assert.True(faces is [WindowFontNames.Carried, WindowFontNames.SystemUi], $"settings listed the window's fonts as: {window.GetRawText()}");
        Assert.True(!window.TryGetProperty("note", out JsonElement windowNote) || windowNote.ValueKind == JsonValueKind.Null
            || windowNote.GetString()!.Contains("installed famil", StringComparison.Ordinal),
            $"settings said of the installed families it left out: {window.GetRawText()}");
        CallToolResult missing = await Result(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["windowFontFamily"] = "Passenger Missing Font" } });
        Assert.True(missing.IsError == true && Text(missing).Contains("Font family Passenger Missing Font (Settings | Appearance) is not a font installed on this computer", StringComparison.Ordinal),
            $"settings_set of a window font not installed answered: {Text(missing)}");
        JsonElement system = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["windowFontFamily"] = WindowFontNames.SystemUi } })).EnumerateArray());
        Assert.Equal(WindowFontNames.SystemUi, system.GetProperty("after").GetString());

        JsonElement vimKeys = await ListedAsync("vimKeysToKeymap");
        Assert.True(vimKeys.GetProperty("choices").EnumerateArray().Select(c => c.GetString()).SequenceEqual([VimKeys.CtrlR, VimKeys.CtrlV, VimKeys.CtrlO, VimKeys.CtrlI]),
            $"settings listed Vim mode's Ctrl keys as: {vimKeys.GetRawText()}");
        Assert.True(vimKeys.GetProperty("note").GetString()!.Contains("<C-v> (Vim mode: Visual block mode; keymap: ", StringComparison.Ordinal),
            $"settings said nothing of what each Ctrl key does on either side: {vimKeys.GetRawText()}");
        CallToolResult noKey = await Result(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["vimKeysToKeymap"] = "<C-v> <C-x>" } });
        Assert.True(noKey.IsError == true && Text(noKey).Contains("Vim mode's Ctrl keys (Settings | Editor) has no key <C-x>", StringComparison.Ordinal),
            $"settings_set of a Ctrl key Vim mode does not give answered: {Text(noKey)}");
        JsonElement given = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["vimKeysToKeymap"] = "<C-o> <C-v>" } })).EnumerateArray());
        Assert.True((given.GetProperty("before").GetString(), given.GetProperty("after").GetString()) == ("", "<C-o> <C-v>")
            && (!given.TryGetProperty("note", out JsonElement note) || note.ValueKind == JsonValueKind.Null),
            $"settings_set giving the keymap Ctrl+O and Ctrl+V answered: {given.GetRawText()}");
        Assert.Equal("<C-o> <C-v>", (await ListedAsync("vimKeysToKeymap")).GetProperty("value").GetString());

        foreach (string name in (string[])["Slat", "Cubicle"])
        {
            Dictionary<string, object> unlisted = new() { ["source"] = ThemeSource.RepositoryName, ["name"] = name };
            CallToolResult notListed = await Result(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["look"] = unlisted } });
            Assert.True(notListed.IsError == true && Text(notListed).Contains($"{ThemeSource.RepositoryName} lists no theme named {name}; it lists Bonbon, Harbour, ", StringComparison.Ordinal)
                && Text(notListed).Contains(", Slate and Sorbet; nothing changed", StringComparison.Ordinal), $"settings_set of {name}, a theme the themes repository does not list, answered: {Text(notListed)}");
        }
        JsonElement look = await ListedAsync("look");
        Assert.True(look.GetProperty("value").ValueKind == JsonValueKind.Null, $"A theme refused was set all the same: {look.GetRawText()}");
        Assert.True(look.GetProperty("kind").GetString() == "object"
            && look.GetProperty("note").GetString()!.Contains($"{ThemeSource.RepositoryName} holds Harbour", StringComparison.Ordinal)
            && look.GetProperty("note").GetString()!.Contains($"{ThemeSource.BuiltIn.Name} holds Cubicle", StringComparison.Ordinal),
            $"settings said of the theme: {look.GetRawText()}");

        Dictionary<string, object> reef = new() { ["source"] = ThemeSource.RepositoryName, ["name"] = ThemeSource.FormerScheme };
        JsonElement schemed = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["colourScheme"] = reef } })).EnumerateArray());
        Assert.Equal(ThemeSource.FormerScheme, schemed.GetProperty("after").GetProperty("name").GetString());
        string scheme = Path.Combine(OwnState, Path.GetRelativePath(Passenger.App.State.StateStore.DefaultRoot, KeptThemes.PathOf(ThemeSource.Repository, true, ThemeSource.FormerScheme)));
        Assert.True(File.Exists(scheme), $"settings_set answered before {ThemeSource.FormerScheme} was fetched from the themes repository and kept as {scheme}");

        // The theme Passenger carries, by the source the note names.
        Dictionary<string, object> cubicle = new() { ["source"] = ThemeSource.BuiltIn.Name, ["name"] = "Cubicle" };
        JsonElement carried = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["look"] = cubicle } })).EnumerateArray());
        Assert.Equal((ThemeSource.BuiltIn.Name, "Cubicle"), (carried.GetProperty("after").GetProperty("source").GetString(), carried.GetProperty("after").GetProperty("name").GetString()));
        Dictionary<string, object> orbit = new() { ["source"] = ThemeSource.RepositoryName, ["name"] = "Orbit" };
        JsonElement orbited = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["look"] = orbit } })).EnumerateArray());
        Assert.Equal("Orbit", orbited.GetProperty("after").GetProperty("name").GetString());
        // Kept in the agent's Passenger's own state, as KeptThemes keeps one in the run's.
        string kept = Path.Combine(OwnState, Path.GetRelativePath(Passenger.App.State.StateStore.DefaultRoot, KeptThemes.PathOf(ThemeSource.Repository, false, "Orbit")));
        Assert.True(await Waiting.UntilAsync(() => File.Exists(kept), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            $"Orbit set by an agent was not fetched from the themes repository and kept as {kept}");
    }

    /// <summary>vcs_log gives each signed commit's signature as the Git log's details say it: a good one by its signer
    /// and key, one by a signer no allowed signers file lists with its key, and none for a commit unsigned.</summary>
    private async Task An_agents_vcs_log_says_each_commits_signature_good_by_its_signer_unverified_or_none()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string repository = PathNames.Canonical(TestFolders.Create("mcp-signed-"));
        string keys = PathNames.Canonical(TestFolders.Create("mcp-signed-keys-"));
        DeleteAtEnd(repository);
        DeleteAtEnd(keys);
        (string known, string unknown) = await TestSshKeys.SignedRepositoryAsync(repository, keys);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        JsonElement log = await Call(client, McpTools.VcsLog, new() { ["count"] = 5 });
        string[] said = [.. log.EnumerateArray().Select(c => $"{c.GetProperty("subject").GetString()}: "
            + (c.TryGetProperty("signature", out JsonElement signature) ? signature.GetString() : "none"))];
        string[] expected =
        [
            $"Signed by an unknown signer: Unverified SSH signature, key {await TestSshKeys.FingerprintAsync(unknown)}: no allowed signers file lists its signer",
            $"Signed by a known signer: Good SSH signature by tests@passenger.invalid, key {await TestSshKeys.FingerprintAsync(known)}",
            "First: none",
        ];
        Assert.True(said.SequenceEqual(expected), $"vcs_log said of the commits' signatures\n{string.Join('\n', said)}\nwhere they are\n{string.Join('\n', expected)}");
    }

    /// <summary>vcs_commit signs as the Commit window does, with the key git's config names; where the key needs a
    /// passphrase and nobody is at the agent's own IDE to give it, the commit is not made and the answer says why.</summary>
    private async Task An_agents_vcs_commit_is_signed_with_the_key_gits_config_names_and_one_needing_a_passphrase_says_why_not()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string repository = PathNames.Canonical(TestFolders.Create("mcp-signing-"));
        string keys = PathNames.Canonical(TestFolders.Create("mcp-signing-keys-"));
        DeleteAtEnd(repository);
        DeleteAtEnd(keys);
        string own = await TestSshKeys.MakeAsync(keys, "own");
        await TestSshKeys.SigningRepositoryAsync(repository, own);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        File.WriteAllText(Path.Combine(repository, "Shop.cs"), "class Shop\n{\n    int count;\n}\n");
        Assert.EndsWith(" Signed by an agent", (await Call(client, McpTools.VcsCommit, new() { ["message"] = "Signed by an agent" })).GetString(), StringComparison.Ordinal);
        Assert.Equal("Signed by an agent G", (await TestGit.RunAsync(repository, "log", "-1", "--format=%s %G?")).Trim());

        string guarded = await TestSshKeys.MakeAsync(keys, "guarded", "nobody-types-this");
        await TestGit.RunAsync(repository, "config", Passenger.Vcs.GitConfigKeys.UserSigningKey, guarded + ".pub");
        File.WriteAllText(Path.Combine(repository, "Shop.cs"), "class Shop\n{\n    long count;\n}\n");
        CallToolResult refused = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Needs a passphrase" });
        Assert.True(refused.IsError == true && Text(refused).Contains("The commit was not signed: git asked for the passphrase of key guarded, and nobody is at this IDE to give it", StringComparison.Ordinal),
            $"vcs_commit with a key needing a passphrase nobody can give answered: {Text(refused)}");
        Assert.Equal("Signed by an agent", (await TestGit.RunAsync(repository, "log", "-1", "--format=%s")).Trim());
    }

    /// <summary>An agent's ide_action of an action that reads before it asks is answered once it has asked, as one asking at
    /// once is: Commit Signing's dialog, opened once git's config is read, closed unanswered and said, none left open, and
    /// Unstash Changes' list of the stashes shown as the action answers. One that does its work at once says what it came to:
    /// Convert Indentation the lines it converted, or that none needed it, Save once the file is saved, and Check for Updates
    /// why it could not check.</summary>
    private async Task An_agents_action_that_reads_before_it_asks_answers_once_it_asks_and_one_that_works_says_what_it_came_to()
    {
        string repository = TestFolders.Create("mcp-asks-");
        DeleteAtEnd(repository);
        TestFolders.Write(repository, "Notes.txt", "notes\n");
        TestFolders.Write(repository, "Indented.cs", "class Indented\n{\n    int one;\n}\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "notes, stashed\n");
        await TestGit.RunAsync(repository, "stash");
        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        await Call(client, McpTools.VcsStatus);

        CallToolResult signing = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.CommitSigning });
        Assert.True(signing.IsError == true && Text(signing).Contains("which nobody is at this IDE to answer: it was closed unanswered", StringComparison.Ordinal),
            $"Commit Signing… in an agent's own IDE answered before its dialog asked: {Text(signing)}");
        Assert.True(await Waiting.UntilAsync(async () => (await Call(client, McpTools.AppWindows, new() { ["app"] = "ide" })).GetProperty("windows").GetArrayLength() == 1, TimeSpan.FromSeconds(10), Cancel),
            $"Commit Signing's dialog was left open: {(await Call(client, McpTools.AppWindows, new() { ["app"] = "ide" })).GetRawText()}");

        Assert.Equal($"Ran {AppActions.Unstash}", Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.Unstash })));
        string listed = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "UnstashPopup" }));
        Assert.False(listed.StartsWith("Nothing in", StringComparison.Ordinal), $"Unstash Changes answered before its list of stashes showed: {listed}");
        await Call(client, McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "escape" });

        await Call(client, McpTools.Open, new() { ["path"] = "Indented.cs" });
        string converted = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ConvertIndentationToTabs }));
        Assert.True(converted.StartsWith("Converted the indentation of 1 line of Indented.cs to tabs", StringComparison.Ordinal),
            $"Convert Indentation to Tabs answered: {converted}");
        Assert.Equal($"Ran {AppActions.Save}", Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.Save })));
        Assert.True(File.ReadAllText(Path.Combine(repository, "Indented.cs")) == "class Indented\n{\n\tint one;\n}\n",
            $"Save answered before the file was saved: {File.ReadAllText(Path.Combine(repository, "Indented.cs"))}");
        string again = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ConvertIndentationToTabs }));
        Assert.True(again.StartsWith("Every line of Indented.cs outside its strings already indents with tabs", StringComparison.Ordinal),
            $"Convert Indentation to Tabs with nothing to convert answered: {again}");

        CallToolResult updates = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.CheckForUpdates });
        Assert.True(updates.IsError == true && Text(updates).Contains("Could not check for updates", StringComparison.Ordinal),
            $"Check for Updates… with no release feed to ask answered: {Text(updates)}");
    }

    /// <summary>A renamed file's diff names where it was and where it is, a new file's and a deleted one's are git's own,
    /// from /dev/null and to it, as are those of files whose last line has no break; vcs_commit takes a folder's path as every change under it, the untracked folder vcs_status
    /// lists as one entry and a tracked folder's new file among them, the rest left; and Push run as an action where the
    /// repository has no remote answers why it did not push.</summary>
    private async Task An_agent_reads_a_renames_two_paths_commits_a_folders_changes_by_its_path_and_hears_why_push_did_not_go()
    {
        string repository = TestFolders.Create("mcp-folders-");
        DeleteAtEnd(repository);
        TestFolders.Write(repository, "Notes.txt", "notes\n");
        TestFolders.Write(repository, "Old.txt", "old\n");
        TestFolders.Write(repository, "Lib/Shelf.txt", "shelf\n");
        TestFolders.Write(repository, "Gone.txt", "one\ntwo\nthree\n");
        TestFolders.Write(repository, "Open.txt", "one\ntwo");
        TestFolders.Write(repository, "Kept.txt", "first\nlast");
        await TestGit.InitAsync(repository);
        File.WriteAllText(Path.Combine(repository, "Open.txt"), "one\ntwo\nthree");
        File.WriteAllText(Path.Combine(repository, "Kept.txt"), "First\nlast");
        File.Delete(Path.Combine(repository, "Gone.txt"));
        await TestGit.RunAsync(repository, "mv", "Old.txt", "Renamed.txt");
        File.WriteAllText(Path.Combine(repository, "Renamed.txt"), "renamed\n");
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "notes, changed\n");
        File.WriteAllText(Path.Combine(repository, "Lib", "Shelf.txt"), "shelf, changed\n");
        TestFolders.Write(repository, "Lib/Added.txt", "added\n");
        TestFolders.Write(repository, "Fresh/a.txt", "a\n");
        TestFolders.Write(repository, "Fresh/b.txt", "b\n");

        await using McpClient client = await Start(["--mcp", repository]);
        string diff = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "Renamed.txt" })).GetString()!;
        Assert.True(diff.Contains("--- a/Old.txt\n+++ b/Renamed.txt\n", StringComparison.Ordinal), $"The diff of Old.txt renamed Renamed.txt named it so:\n{diff}");
        // Git applies a deletion's diff only as one to /dev/null: to b/Gone.txt it leaves the file there, empty.
        string gone = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "Gone.txt" })).GetString()!;
        string gitsGone = GitsDiff(await TestGit.RunAsync(repository, "diff", "--", "Gone.txt"));
        Assert.True(gone == gitsGone, $"The diff of Gone.txt deleted, which git apply is to take as its deletion, is not git's own:\n{gone}\ngit's:\n{gitsGone}");
        string added = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "Lib/Added.txt" })).GetString()!;
        // Git applies a diff touching a last line with no break only where it says so after the line, changed or not.
        foreach (string open in (string[])["Open.txt", "Kept.txt"])
        {
            string ours = (await Call(client, McpTools.VcsStatus, new() { ["path"] = open })).GetString()!;
            string gits = GitsDiff(await TestGit.RunAsync(repository, "diff", "--", open));
            Assert.True(ours == gits, $"The diff of {open}, whose last line has no break, which git apply is to take, is not git's own:\n{ours}\ngit's:\n{gits}");
        }
        // An agent's paths are the system's, as every tool answers them.
        JsonElement fresh = Assert.Single((await Call(client, McpTools.VcsStatus)).GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("path").GetString() == "Fresh" + Path.DirectorySeparatorChar);
        Assert.Equal(2, fresh.GetProperty("files").GetInt32());

        await Call(client, McpTools.VcsCommit, new() { ["message"] = "Fresh", ["paths"] = new[] { "Fresh/" } });
        Assert.Equal("A\tFresh/a.txt\nA\tFresh/b.txt", (await TestGit.RunAsync(repository, "show", "--name-status", "--format=", "HEAD")).Trim());
        await Call(client, McpTools.VcsCommit, new() { ["message"] = "Lib", ["paths"] = new[] { "Lib" } });
        Assert.Equal("A\tLib/Added.txt\nM\tLib/Shelf.txt", (await TestGit.RunAsync(repository, "show", "--name-status", "--format=", "HEAD")).Trim());
        string gitsAdded = GitsDiff(await TestGit.RunAsync(repository, "show", "--format=", "HEAD", "--", "Lib/Added.txt"));
        Assert.True(added == gitsAdded, $"The diff of Lib/Added.txt, new, which git apply is to take as making it, is not git's own:\n{added}\ngit's:\n{gitsAdded}");
        Assert.Equal(["Gone.txt deleted", "Kept.txt modified", "Notes.txt modified", "Open.txt modified", "Renamed.txt renamed"], (await Call(client, McpTools.VcsStatus)).GetProperty("changes").EnumerateArray()
            .Select(c => $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}").Order(StringComparer.Ordinal));
        CallToolResult nothing = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Nothing", ["paths"] = new[] { "Lib" } });
        Assert.True(nothing.IsError == true && Text(nothing).Contains("Lib has no changes to commit", StringComparison.Ordinal),
            $"vcs_commit of a folder with no changes left answered: {Text(nothing)}");

        CallToolResult pushed = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.Push });
        Assert.True(pushed.IsError == true && Text(pushed).Contains("the repository has none", StringComparison.Ordinal),
            $"Push where the repository has no remote answered an agent: {Text(pushed)}");
    }

    /// <summary>Git's diff of one file from its "---" line on, as vcs_status answers it: what comes before (its "diff --git",
    /// mode and index lines) left out.</summary>
    private static string GitsDiff(string said) =>
        said.IndexOf("--- ", StringComparison.Ordinal) is >= 0 and int at ? said[at..] : throw new Xunit.Sdk.XunitException($"Git wrote no diff: {said}");

    /// <summary>A rebase stopped on a conflict that the agent resolves is still said by vcs_status, and vcs_commit refuses to
    /// commit beside it, naming Continue Rebase and Abort Rebase by the ids ide_action runs: no commit of the agent's takes
    /// the place of the rebase's on its detached head, and the rebase goes on once the agent runs Continue Rebase.</summary>
    private async Task A_rebase_whose_conflict_is_resolved_is_still_said_and_an_agents_commit_never_goes_beside_it()
    {
        string repository = TestFolders.Create("mcp-rebase-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\nthree\n");
        await TestGit.InitAsync(repository);
        await TestGit.RunAsync(repository, "switch", "-q", "-c", "feature");
        File.WriteAllText(notes, "one\nTWO\nthree\n");
        await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in capitals");
        File.WriteAllText(Path.Combine(repository, "Later.txt"), "later\n");
        await TestGit.RunAsync(repository, "add", "Later.txt");
        await TestGit.RunAsync(repository, "commit", "-q", "-m", "Later");
        await TestGit.RunAsync(repository, "switch", "-q", "main");
        File.WriteAllText(notes, "one\nII\nthree\n");
        await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in Roman");
        await TestGit.RunAsync(repository, "switch", "-q", "feature");
        ChildResult rebasing = await ChildProcesses.RunAsync(ChildProcesses.Command("git", ["rebase", "main"], repository), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
        Assert.False(rebasing.Succeeded, $"The rebase was to stop for its conflict: {rebasing.Output}");

        await using McpClient client = await Start(["--mcp", repository]);
        JsonElement stopped = await Call(client, McpTools.VcsStatus);
        Assert.StartsWith("Rebasing feature onto main", stopped.GetProperty("underWay").GetString(), StringComparison.Ordinal);
        Assert.Equal("Notes.txt resolved; no conflicts are left", (await Call(client, McpTools.VcsResolve, new() { ["path"] = "Notes.txt", ["take"] = "yours" })).GetString());
        JsonElement resolved = await Call(client, McpTools.VcsStatus);
        string? underWay = resolved.TryGetProperty("underWay", out JsonElement said) ? said.GetString() : null;
        Assert.True(underWay?.StartsWith("Rebasing feature onto main", StringComparison.Ordinal) == true,
            $"Once its conflict was resolved vcs_status said nothing of the rebase still in progress: {resolved.GetRawText()}");

        string head = (await TestGit.RunAsync(repository, "rev-parse", "HEAD")).Trim();
        CallToolResult commit = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Agent's commit" });
        Assert.True(commit.IsError == true, $"vcs_commit made a commit of its own in the middle of a rebase: {Text(commit)}");
        Assert.Contains("Continue Rebase", Text(commit), StringComparison.Ordinal);
        Assert.True(Text(commit).Contains($"(ide_action {AppActions.ContinueRebase} and {AppActions.AbortRebase})", StringComparison.Ordinal),
            $"vcs_commit refused in the middle of a rebase named no action an agent can run to go on: {Text(commit)}");
        Assert.Equal(head, (await TestGit.RunAsync(repository, "rev-parse", "HEAD")).Trim());
        Assert.True(Directory.Exists(Path.Combine(repository, ".git", "rebase-merge")), "The rebase is no longer in progress after vcs_commit was refused");

        await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ContinueRebase });
        Assert.False(Directory.Exists(Path.Combine(repository, ".git", "rebase-merge")), "Continue Rebase, run by its id, left the rebase in progress");
        Assert.Equal("Later\nTwo in capitals\nTwo in Roman", (await TestGit.RunAsync(repository, "log", "-3", "--format=%s")).Trim());
    }

    /// <summary>An interactive rebase stopped at an edit step is said by vcs_status as stopped to edit that commit, and there
    /// vcs_commit amends it as git asks: Continue Rebase then replays the rest onto the amended commit, which the branch's
    /// history keeps with the agent's change and message.</summary>
    private async Task At_an_interactive_rebases_edit_stop_an_agent_amends_the_commit_and_the_rebase_goes_on_with_it()
    {
        string repository = TestFolders.Create("mcp-rebase-edit-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(notes, "one\ntwo\n");
        await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two");
        File.WriteAllText(Path.Combine(repository, "Later.txt"), "later\n");
        await TestGit.RunAsync(repository, "add", "Later.txt");
        await TestGit.RunAsync(repository, "commit", "-q", "-m", "Later");
        ProcessStartInfo editing = ChildProcesses.Command("git", ["rebase", "-i", "HEAD~2"], repository);
        // The list's first step, Two's pick, made an edit, as a person's editor would.
        editing.Environment[GitVariables.SequenceEditor] = "sed -i.bak -e 1s/^pick/edit/";
        ChildResult rebasing = await ChildProcesses.RunAsync(editing, limit: TimeSpan.FromMinutes(1), cancel: Cancel);
        Assert.True(File.Exists(Path.Combine(repository, ".git", "rebase-merge", "amend")), $"The rebase was to stop to edit Two: {rebasing.Error}{rebasing.Output}");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        JsonElement stopped = await Call(client, McpTools.VcsStatus);
        string? underWay = stopped.TryGetProperty("underWay", out JsonElement said) ? said.GetString() : null;
        Assert.True(underWay?.StartsWith("Rebasing main onto ", StringComparison.Ordinal) == true && underWay.Contains(", stopped to edit ", StringComparison.Ordinal),
            $"vcs_status did not say the rebase stopped to edit Two: {stopped.GetRawText()}");
        File.WriteAllText(notes, "one\nTWO\n");
        CallToolResult amend = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Two in capitals", ["amend"] = true });
        Assert.True(amend.IsError != true, $"vcs_commit refused to amend the commit an interactive rebase stopped to edit: {Text(amend)}");
        Assert.Equal("Rebase successful", (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ContinueRebase })).GetString());
        Assert.True(await Waiting.UntilAsync(() => !Directory.Exists(Path.Combine(repository, ".git", "rebase-merge")), TimeSpan.FromMinutes(1), Cancel),
            $"Continue Rebase did not finish the rebase after the amend: {(await Call(client, McpTools.VcsStatus)).GetRawText()}");

        Assert.Equal("main", (await TestGit.RunAsync(repository, "rev-parse", "--abbrev-ref", "HEAD")).Trim());
        Assert.Equal("Later\nTwo in capitals\nFirst", (await TestGit.RunAsync(repository, "log", "--format=%s")).Trim().ReplaceLineEndings("\n"));
        Assert.Equal("one\nTWO\n", (await TestGit.RunAsync(repository, "show", "HEAD~1:Notes.txt")).ReplaceLineEndings("\n"));
    }

    /// <summary>An agent's amend with no paths changes the last commit's message alone: a rename and a change staged in the
    /// index (as the IDE's own rename and another program's git add leave them) go into no commit and stay changes.</summary>
    private async Task An_agents_amend_with_no_paths_rewords_the_last_commit_and_leaves_what_the_index_holds_out_of_it()
    {
        string repository = TestFolders.Create("mcp-amend-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        File.WriteAllText(Path.Combine(repository, "Shop.txt"), "shop\n");
        await TestGit.InitAsync(repository);
        await TestGit.RunAsync(repository, "mv", "Shop.txt", "Store.txt");
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\n");
        await TestGit.RunAsync(repository, "add", "Notes.txt");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        Dictionary<string, object?> amend = new() { ["message"] = "First, reworded", ["paths"] = Array.Empty<string>(), ["amend"] = true };
        Assert.EndsWith(" First, reworded", (await Call(client, McpTools.VcsCommit, amend)).GetString(), StringComparison.Ordinal);
        Assert.Equal("1", (await TestGit.RunAsync(repository, "rev-list", "--count", "HEAD")).Trim());
        string held = (await TestGit.RunAsync(repository, "show", "--name-status", "--format=", "HEAD")).Trim().Replace("\r", string.Empty, StringComparison.Ordinal);
        Assert.True(held == "A\tNotes.txt\nA\tShop.txt", $"vcs_commit's amend with no paths took what the index holds into the last commit, which now holds [{held}]");
        Assert.Equal("one\n", await TestGit.RunAsync(repository, "show", "HEAD:Notes.txt"));
        Assert.Equal(["Notes.txt modified", "Store.txt renamed"], (await Call(client, McpTools.VcsStatus)).GetProperty("changes").EnumerateArray()
            .Select(c => $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}").Order(StringComparer.Ordinal));
    }

    /// <summary>vcs_status with the path of a binary file changed since the last commit answers as git's diff does, that the
    /// binary files differ, never its old bytes as lines deleted; the file put back as committed has no changes.</summary>
    private async Task An_agents_diff_of_a_changed_binary_file_says_the_binary_files_differ()
    {
        string repository = TestFolders.Create("mcp-binary-");
        DeleteAtEnd(repository);
        byte[] image = [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 1, 2, 3];
        string path = Path.Combine(repository, "image.png");
        File.WriteAllBytes(path, image);
        await TestGit.InitAsync(repository);
        File.WriteAllBytes(path, [.. image, 4, 5]);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        Dictionary<string, object?> asked = new() { ["path"] = "image.png" };
        string diff = (await Call(client, McpTools.VcsStatus, asked)).GetString()!;
        Assert.True(diff == "Binary files a/image.png and b/image.png differ", $"vcs_status of a changed binary file answered:\n{diff}");
        File.WriteAllBytes(path, image);
        Assert.Equal("No changes", (await Call(client, McpTools.VcsStatus, asked)).GetString());
    }

    /// <summary>vcs_status lists a folder of new files as one entry saying how many, as git's status does; with a folder's
    /// path it lists that folder's changes alone (its own folders of new files one entry each), and with a path that names
    /// nothing it is refused saying so, where each path answered "No changes".</summary>
    private async Task An_agents_status_of_a_folder_lists_its_changes_and_of_a_path_naming_nothing_is_refused()
    {
        string repository = TestFolders.Create("mcp-folder-status-");
        DeleteAtEnd(repository);
        Directory.CreateDirectory(Path.Combine(repository, "Cons"));
        File.WriteAllText(Path.Combine(repository, "Cons", "Program.cs"), "one\n");
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "notes\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(Path.Combine(repository, "Cons", "Program.cs"), "two\n");
        File.WriteAllText(Path.Combine(repository, "Cons", "New.cs"), "new\n");
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "more notes\n");
        for (int i = 0; i < 300; i++)
        {
            TestFolders.Write(repository, Path.Combine("node_modules", $"package{i % 3}", $"file{i}.js"), "made\n");
        }

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        string modules = "node_modules" + Path.DirectorySeparatorChar;
        Assert.Equal([$"{Path.Combine("Cons", "Program.cs")} modified", "Notes.txt modified", $"{Path.Combine("Cons", "New.cs")} unversioned", $"{modules} unversioned 300"],
            Changes(await Call(client, McpTools.VcsStatus)));
        Assert.Equal([$"{Path.Combine("Cons", "Program.cs")} modified", $"{Path.Combine("Cons", "New.cs")} unversioned"],
            Changes(await Call(client, McpTools.VcsStatus, new() { ["path"] = "Cons" })));
        Assert.Equal([.. Enumerable.Range(0, 3).Select(p => $"{modules}package{p}{Path.DirectorySeparatorChar} unversioned 100")],
            Changes(await Call(client, McpTools.VcsStatus, new() { ["path"] = "node_modules" })));
        CallToolResult nowhere = await Result(client, McpTools.VcsStatus, new() { ["path"] = "Nowhere" });
        Assert.True(nowhere.IsError == true && Text(nowhere).Contains("No file or folder Nowhere is here", StringComparison.Ordinal),
            $"vcs_status of a path that names nothing answered: {Text(nowhere)}");

        static IEnumerable<string> Changes(JsonElement status) => status.GetProperty("changes").EnumerateArray().Select(c =>
            $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}{(c.TryGetProperty("files", out JsonElement files) ? $" {files.GetInt32()}" : string.Empty)}");
    }

    /// <summary>A line outside a file, or a count of no lines, is refused in words naming the file's lines: editor_text from
    /// past the end or below the first, to before from, a code tool's place past the end, and output's lines of 0 or less,
    /// where each answered another line, the nearest line's symbol or a raw exception.</summary>
    private async Task An_agent_naming_a_line_outside_a_file_or_no_lines_is_refused_with_the_files_lines()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-lines-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), string.Join('\n', Enumerable.Range(1, 12).Select(i => $"System.Console.WriteLine({i});")));

        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        foreach (int from in (int[])[500, 13, 0, -5])
        {
            CallToolResult past = await Result(client, McpTools.EditorText, new() { ["path"] = "Program.cs", ["from"] = from });
            Assert.True(past.IsError == true && Text(past).Contains($"Line {from} is outside Program.cs, whose lines are 1 to 12", StringComparison.Ordinal),
                $"editor_text from line {from} of a 12-line file answered: {Text(past)}");
        }
        CallToolResult backwards = await Result(client, McpTools.EditorText, new() { ["path"] = "Program.cs", ["from"] = 5, ["to"] = 3 });
        Assert.True(backwards.IsError == true && Text(backwards).Contains("to 3 is before from 5", StringComparison.Ordinal), $"editor_text from 5 to 3 answered: {Text(backwards)}");
        Assert.Equal("    11\tSystem.Console.WriteLine(11);\n    12\tSystem.Console.WriteLine(12);",
            (await Call(client, McpTools.EditorText, new() { ["path"] = "Program.cs", ["from"] = 11, ["to"] = 99 })).GetString());

        int projectLines = TestProjects.Console().Split('\n').Length;
        CallToolResult symbol = await Result(client, McpTools.SymbolInfo, new() { ["path"] = "App.csproj", ["line"] = 999, ["column"] = 1 });
        Assert.True(symbol.IsError == true && Text(symbol).Contains($"Line 999 is outside App.csproj, whose lines are 1 to {projectLines}", StringComparison.Ordinal),
            $"symbol_info at line 999 of App.csproj answered: {Text(symbol)}");

        foreach ((string of, int lines) in (IEnumerable<(string, int)>)[("run", 0), ("build", -3)])
        {
            CallToolResult none = await Result(client, McpTools.Output, new() { ["of"] = of, ["lines"] = lines });
            Assert.True(none.IsError == true && Text(none).Contains($"lines is {lines}: a console's last lines are 1 or more", StringComparison.Ordinal),
                $"output of {of} with lines {lines} answered: {Text(none)}");
        }
    }

    /// <summary>vcs_status with the path of a file whose only change is its line separators says which changed on how many
    /// lines, then git's own diff, each CR in it, where git stores the breaks as they are, and of one whose only change is
    /// its mode (where git keeps modes) says the mode as git's diff does, where each answered no changes.</summary>
    private async Task An_agents_diff_of_a_file_changed_only_in_its_line_separators_or_its_mode_says_what_changed()
    {
        string repository = TestFolders.Create("mcp-separators-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "lines.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(repository, "run.sh"), "echo run\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(Path.Combine(repository, "lines.txt"), "one\r\ntwo\r\nthree\r\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(repository, "run.sh"), File.GetUnixFileMode(Path.Combine(repository, "run.sh")) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        // Git applies a diff of lines a CR ends only where it holds each CR.
        string gits = GitsDiff(await TestGit.RunAsync(repository, "diff", "--", "lines.txt"));
        string said = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "lines.txt" })).GetString()!;
        Assert.True(said == $"Line separators changed: 3 lines from LF to CRLF\n{gits}",
            $"vcs_status of lines.txt, its LF breaks now CRLF, which git stores as they are, is not its words then git's own diff:\n{said}\ngit's:\n{gits}");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("old mode 100644\nnew mode 100755", (await Call(client, McpTools.VcsStatus, new() { ["path"] = "run.sh" })).GetString());
        }
    }

    /// <summary>An agent's vcs tools refuse what the repository has not, naming what there is, and change nothing: a checkout
    /// of a branch not there names create and the branches there are, never git's "fatal: invalid reference"; a log of no
    /// commits is refused; a stash with a message but no action, which lists and so puts nothing away, says to give push, and
    /// an action vcs_stash has not is refused as one; and the history of lines past a file's end names how many it has, never
    /// as lines not committed yet.</summary>
    private async Task An_agents_vcs_tools_refuse_a_branch_a_stash_action_and_lines_the_repository_has_not_naming_what_there_is()
    {
        string repository = TestFolders.Create("mcp-vcs-refused-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\nthree");
        await TestGit.InitAsync(repository);
        File.WriteAllText(notes, "one\n2\nthree");
        await using McpClient client = await Start(["--mcp", repository], languageServer: false);

        CallToolResult refused = await Result(client, McpTools.VcsCheckout, new() { ["branch"] = "nowhere" });
        Assert.True(refused.IsError == true && Text(refused).Contains("No branch nowhere is here: create true makes it from the head; the local branches are main", StringComparison.Ordinal),
            $"A checkout of a branch there is not answered: {Text(refused)}");
        Assert.Equal("main", (await TestGit.RunAsync(repository, "branch", "--show-current")).Trim());
        CallToolResult none = await Result(client, McpTools.VcsLog, new() { ["count"] = 0 });
        Assert.True(none.IsError == true && Text(none).Contains("count is 0: vcs_log lists 1 or more", StringComparison.Ordinal),
            $"vcs_log of no commits answered: {Text(none)}");

        CallToolResult unasked = await Result(client, McpTools.VcsStash, new() { ["message"] = "work in progress" });
        Assert.True(unasked.IsError == true && Text(unasked).Contains("give action push", StringComparison.Ordinal),
            $"A stash with a message and no action, which puts nothing away, answered: {Text(unasked)}");
        CallToolResult unknown = await Result(client, McpTools.VcsStash, new() { ["action"] = "save" });
        Assert.True(unknown.IsError == true && Text(unknown).Contains("No stash action 'save'", StringComparison.Ordinal),
            $"A stash action vcs_stash does not have answered: {Text(unknown)}");
        Assert.Equal("one\n2\nthree", File.ReadAllText(notes));
        Assert.Empty((await Call(client, McpTools.VcsStash)).EnumerateArray());

        CallToolResult past = await Result(client, McpTools.VcsLog, new() { ["path"] = "Notes.txt", ["startLine"] = 20, ["endLine"] = 30 });
        Assert.True(past.IsError == true && Text(past).Contains("Notes.txt has 3 lines", StringComparison.Ordinal),
            $"The history of lines past Notes.txt's end answered: {Text(past)}");
        JsonElement last = Assert.Single((await Call(client, McpTools.VcsLog, new() { ["path"] = "Notes.txt", ["startLine"] = 3, ["endLine"] = 3 })).EnumerateArray());
        Assert.Equal("First", last.GetProperty("subject").GetString());
    }

    /// <summary>An agent's edit of a Latin-1 file with a character Latin-1 cannot hold is saved as UTF-8, as the person's
    /// is, and the agent is told so, as the status bar tells the person, rather than only that it was saved.</summary>
    private async Task An_agents_edit_its_files_encoding_cannot_hold_is_saved_as_utf8_and_the_answer_says_so()
    {
        string folder = TestFolders.Create("mcp-latin1-");
        DeleteAtEnd(folder);
        string menu = Path.Combine(folder, "Menu.txt");
        File.WriteAllBytes(menu, System.Text.Encoding.Latin1.GetBytes("caf\u00e9 cr\u00e8me\n"));

        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string edited = Text(await Call(client, McpTools.Edit, new() { ["path"] = "Menu.txt", ["oldText"] = "crème", ["newText"] = "crème \u2603" }));
        Assert.True(edited.Contains("saved (Menu.txt was saved as UTF-8:", StringComparison.Ordinal),
            $"An agent's edit its file's Latin-1 cannot hold was answered without saying the file is UTF-8 now: {edited}");
        Assert.Equal("caf\u00e9 cr\u00e8me \u2603\n", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(menu)));
    }

    /// <summary>In a folder with no project, no repository and nothing running, an agent's tools say why they have nothing to
    /// act on, never an empty answer it reads as nothing found or a clean file: output, stop, hot_reload and telemetry what
    /// starts a program, vcs_stash that the folder is in no repository, build that there is nothing to build (never MSB1003),
    /// diagnostics that no check reads Markdown, editor_text, edit and diagnostics of a file not there how a new one is made
    /// (of a folder, that it is one), and find_symbol of a kind or limit it has not which it takes.</summary>
    private async Task An_agents_tools_with_nothing_to_act_on_say_why_and_what_would_give_them_something()
    {
        string folder = TestFolders.Create("mcp-nothing-");
        DeleteAtEnd(folder);
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        File.WriteAllText(Path.Combine(folder, "Notes.md"), "# Notes\n\nPlain words.\n");
        File.WriteAllText(Path.Combine(folder, ".editorconfig"), "root = true\n\n[*.cs]\nindent_size = 4\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        Assert.StartsWith("Run has started nothing yet", (await Call(client, McpTools.Output, new() { ["of"] = "run" })).GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Nothing has been debugged yet", (await Call(client, McpTools.Output, new() { ["of"] = "debug" })).GetString(), StringComparison.Ordinal);
        string stopped = (await Call(client, McpTools.Stop)).GetString()!;
        Assert.True(stopped.StartsWith("Nothing is running, so nothing was stopped", StringComparison.Ordinal), $"stop with nothing running answered: {stopped}");

        CallToolResult reloaded = await Result(client, McpTools.HotReload);
        Assert.True(reloaded.IsError == true && Text(reloaded).Contains($"({McpTools.Run} with hotReload true, or {McpTools.DebugStart}, starts one it applies to)", StringComparison.Ordinal),
            $"hot_reload with nothing running answered: {Text(reloaded)}");
        CallToolResult telemetry = await Result(client, McpTools.Telemetry);
        Assert.True(telemetry.IsError == true && Text(telemetry).Contains($"Nothing runs whose logs and traces a dashboard collects: {McpTools.Run} with telemetry true", StringComparison.Ordinal),
            $"telemetry with nothing running answered: {Text(telemetry)}");

        CallToolResult stashes = await Result(client, McpTools.VcsStash);
        Assert.True(stashes.IsError == true && Text(stashes).Contains("not in a Git repository", StringComparison.Ordinal),
            $"vcs_stash outside a repository answered: {Text(stashes)}");

        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("outcome").GetString() == Refusals.NothingToBuild && built.GetProperty("errors").GetInt32() == 0,
            $"A build of a folder with no solution or project answered: {built}");

        CallToolResult unread = await Result(client, McpTools.Diagnostics, new() { ["path"] = "Notes.md" });
        Assert.True(unread.IsError == true && Text(unread).Contains("No check of Passenger's reads Markdown files such as Notes.md: diagnostics answers C#", StringComparison.Ordinal),
            $"diagnostics of a file no check reads answered: {Text(unread)}");
        Assert.Empty((await Call(client, McpTools.Diagnostics, new() { ["path"] = ".editorconfig" })).EnumerateArray());

        foreach ((string tool, Dictionary<string, object?> arguments) in new (string, Dictionary<string, object?>)[]
        {
            (McpTools.EditorText, new() { ["path"] = "Missing.cs" }),
            (McpTools.Edit, new() { ["path"] = "Missing.cs", ["oldText"] = "a", ["newText"] = "b" }),
            (McpTools.Diagnostics, new() { ["path"] = "Missing.cs" }),
        })
        {
            CallToolResult refused = await Result(client, tool, arguments);
            Assert.True(refused.IsError == true && Text(refused).Contains("No file is at Missing.cs: a new file is written on disk, then edited", StringComparison.Ordinal),
                $"{tool} of a file not there answered: {Text(refused)}");
        }
        CallToolResult folderAsked = await Result(client, McpTools.EditorText, new() { ["path"] = "docs" });
        Assert.True(folderAsked.IsError == true && Text(folderAsked).Contains("docs is a folder, not a file", StringComparison.Ordinal),
            $"editor_text of a folder answered: {Text(folderAsked)}");

        CallToolResult kind = await Result(client, McpTools.FindSymbol, new() { ["query"] = "Order", ["kind"] = "klass" });
        Assert.True(kind.IsError == true && Text(kind).Contains("No kind 'klass'; Go to Symbol's kinds are", StringComparison.Ordinal) && Text(kind).Contains("class", StringComparison.Ordinal),
            $"find_symbol of the kind klass answered: {Text(kind)}");
        CallToolResult none = await Result(client, McpTools.FindSymbol, new() { ["query"] = "Order", ["limit"] = 0 });
        Assert.True(none.IsError == true && Text(none).Contains("limit is 0: find_symbol lists 1 or more", StringComparison.Ordinal),
            $"find_symbol with limit 0 answered: {Text(none)}");
    }

    /// <summary>An agent's ide_actions with a query lists only the actions whose id, name or group holds it, where the whole
    /// list runs to hundreds of lines and tens of kilobytes; its ide_action of an action Passenger has not is refused naming
    /// ide_actions; and its own Passenger, which nobody is at, refuses its ide_action of an install, a change of the person's
    /// shell PATH or passenger-ide command, or Restart to Update in the words a person's window refuses an agent's, never
    /// taking them as the window's own.</summary>
    private async Task An_agents_ide_actions_narrow_by_its_query_and_ide_action_refuses_one_there_is_not_and_the_persons_own()
    {
        string folder = TestFolders.Create("mcp-actions-");
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string[] all = [.. (await Call(client, McpTools.IdeActions)).EnumerateArray().Select(a => a.GetString()!)];
        string[] rebasing = [.. (await Call(client, McpTools.IdeActions, new() { ["query"] = "rebase" })).EnumerateArray().Select(a => a.GetString()!)];
        Assert.True(rebasing.Length < all.Length && rebasing.Any(a => a.StartsWith($"{AppActions.ContinueRebase}:", StringComparison.Ordinal))
            && rebasing.All(a => a.Contains("rebase", StringComparison.OrdinalIgnoreCase)),
            $"ide_actions with the query rebase listed {rebasing.Length} of {all.Length}: {string.Join("; ", rebasing.Take(5))}");

        CallToolResult unknown = await Result(client, McpTools.IdeAction, new() { ["action"] = "app.no.such" });
        Assert.True(unknown.IsError == true && Text(unknown).Contains("No action is called app.no.such: ide_actions lists them", StringComparison.Ordinal),
            $"ide_action of an action there is not answered: {Text(unknown)}");

        foreach ((string action, string why) in ((string, string)[])[(AppActions.InstallDotnetSdk, Refusals.AgentInstalls),
            (AppActions.AddDotnetToPath, Refusals.AgentChangesPath), (AppActions.InstallWorkloads, Refusals.AgentInstallsMobile),
            (AppActions.InstallAndroidDependencies, Refusals.AgentInstallsMobile), (AppActions.RestartToUpdate, Refusals.RestartIsThePersons),
            (AppActions.AddCommand, Refusals.CommandIsThePersons), (AppActions.RemoveCommand, Refusals.CommandIsThePersons),
            (AppActions.CloseSolution, Refusals.CloseSolutionIsThePersons)])
        {
            CallToolResult refused = await Result(client, McpTools.IdeAction, new() { ["action"] = action });
            Assert.True(refused.IsError == true && Text(refused).Contains(why, StringComparison.Ordinal),
                $"An agent's own Passenger's ide_action of {action} answered: {Text(refused)}");
        }
    }

    /// <summary>An agent's local_history of a path where no file or folder is, nor any change of one in the history, says
    /// so, never an empty history an agent reads as a file no one changed; a file deleted after a change still lists it.</summary>
    private async Task An_agents_local_history_of_a_path_never_there_says_so_and_a_deleted_files_still_lists_its_changes()
    {
        string folder = TestFolders.Create("mcp-nohistory-");
        DeleteAtEnd(folder);
        string notes = Path.Combine(folder, "Notes.txt");
        File.WriteAllText(notes, "one\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        CallToolResult never = await Result(client, McpTools.LocalHistory, new() { ["path"] = "Nope.txt" });
        Assert.True(never.IsError == true && Text(never).Contains("No file or folder is at Nope.txt, and Local History holds no change of one there", StringComparison.Ordinal),
            $"local_history of a path never there answered: {Text(never)}");
        await Call(client, McpTools.Edit, new() { ["path"] = "Notes.txt", ["oldText"] = "one", ["newText"] = "two" });
        File.Delete(notes);
        JsonElement kept = await Call(client, McpTools.LocalHistory, new() { ["path"] = "Notes.txt" });
        Assert.True(kept.GetProperty("rows").GetArrayLength() > 0, $"local_history of a file deleted after its change lists none: {kept}");
    }

    /// <summary>An agent's breakpoint on a C# line no code runs on, while nothing is debugged, says the line it moves to, or
    /// that no code runs on or after it, never a bare "set" an agent takes for a stop there; one on a statement says nothing
    /// more.</summary>
    private async Task An_agents_breakpoint_on_a_line_no_code_runs_on_says_where_it_moves()
    {
        string folder = TestFolders.Create("mcp-noline-");
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "using System;\n\nint count = 1;\n\nConsole.WriteLine(count);\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        async Task<string?> Said(int line)
        {
            JsonElement set = (await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = line })).EnumerateArray()
                .Single(b => b.GetProperty("line").GetInt32() == line);
            return set.TryGetProperty("message", out JsonElement message) ? message.GetString() : null;
        }
        Assert.Equal("Breakpoint: no code runs on this line, so it moves to line 3, the next that may, as the program is debugged", await Said(2));
        Assert.Null(await Said(5));
        Assert.Equal("Breakpoint: no code runs on this line or after it, so it will not stop the program", await Said(6));
    }

    /// <summary>vcs_commit commits the changes of a file its lines touch, through the Commit window's partial commit: one of
    /// two changes goes in, the other stays in the working tree; lines that no longer name a change, as once the file moved on
    /// since vcs_status numbered it, are refused saying where its changes are now, and nothing is committed; tracking a
    /// remote's branch, vcs_status then says the commit to push.</summary>
    private async Task An_agents_commit_of_one_of_a_files_two_changes_leaves_the_other_and_refuses_lines_that_name_none()
    {
        string repository = TestFolders.Create("mcp-partial-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\nthree\nfour\nfive\nsix\nseven\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(notes, "ONE\ntwo\nthree\nfour\nfive\nsix\nSEVEN\n");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        string diff = Text(await Call(client, McpTools.VcsStatus, new() { ["path"] = "Notes.txt" }));
        Assert.True(diff.Contains("+SEVEN", StringComparison.Ordinal) && diff.Contains("+ONE", StringComparison.Ordinal), $"vcs_status of Notes.txt answered: {diff}");
        // The file moves on after the agent read its diff, so line 7 is no longer SEVEN's.
        await Call(client, McpTools.Edit, new() { ["path"] = "Notes.txt", ["oldText"] = "ONE\n", ["newText"] = "zero\nONE\n" });
        CallToolResult refused = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Capital seven", ["lines"] = new Dictionary<string, string> { ["Notes.txt"] = "7" } });
        Assert.True(refused.IsError == true && Text(refused).Contains("Notes.txt: no change is at line 7: its changes are at lines 1-2 and line 8", StringComparison.Ordinal),
            $"vcs_commit of line 7, which no longer names a change, answered: {Text(refused)}");
        Assert.Equal("First", (await TestGit.RunAsync(repository, "log", "-1", "--format=%s")).Trim());

        string made = Text(await Call(client, McpTools.VcsCommit, new() { ["message"] = "Capital seven", ["lines"] = new Dictionary<string, string> { ["Notes.txt"] = "8" } }));
        Assert.EndsWith(" Capital seven; left uncommitted: 1 change of Notes.txt", made, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\nthree\nfour\nfive\nsix\nSEVEN\n", await TestGit.RunAsync(repository, "show", "HEAD:Notes.txt"));
        Assert.Equal("zero\nONE\ntwo\nthree\nfour\nfive\nsix\nSEVEN\n", File.ReadAllText(notes));
        string left = await TestGit.RunAsync(repository, "diff", "--no-color", "-U0", "--", "Notes.txt");
        Assert.True(left.Contains("-one\n+zero\n+ONE\n", StringComparison.Ordinal) && !left.Contains("SEVEN", StringComparison.Ordinal),
            $"What the working tree holds beyond the commit is not the change left out: {left}");

        // The branch set to track a remote's branch at the first commit, vcs_status says the commit to push.
        await TestGit.RunAsync(repository, "remote", "add", "origin", repository);
        await TestGit.RunAsync(repository, "update-ref", "refs/remotes/origin/main", "HEAD~1");
        await TestGit.RunAsync(repository, "branch", "-q", "--set-upstream-to=origin/main");
        JsonElement tracked = await Call(client, McpTools.VcsStatus);
        Assert.True(tracked.TryGetProperty("upstream", out JsonElement upstream) && upstream.GetString() == "origin/main"
            && tracked.GetProperty("ahead").GetInt32() == 1 && tracked.GetProperty("behind").GetInt32() == 0, $"vcs_status of a branch one commit ahead of its upstream answered: {tracked}");
    }

    /// <summary>An agent stages into git's index as the person does: vcs_commit's stage stages a line of a change of several,
    /// vcs_status then says the file is staged in part and shows what the index holds of it apart, stage false takes a
    /// staged file back out, and vcs_commit with nothing named commits what the index holds alone.</summary>
    private async Task An_agent_stages_lines_and_files_sees_what_is_staged_and_commits_it_alone()
    {
        string repository = TestFolders.Create("mcp-stage-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\nthree\nfour\n");
        File.WriteAllText(Path.Combine(repository, "Other.txt"), "old\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(notes, "ONE\nTWO\nTHREE\nfour\n");
        File.WriteAllText(Path.Combine(repository, "Other.txt"), "new\n");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        string staged = Text(await Call(client, McpTools.VcsCommit, new() { ["stage"] = true, ["lines"] = new Dictionary<string, string> { ["Notes.txt"] = "2" } }));
        Assert.Contains("Staged 1 line of Notes.txt", staged, StringComparison.Ordinal);
        Assert.Equal("one\nTWO\nthree\nfour\n", await TestGit.RunAsync(repository, "show", ":Notes.txt"));
        Text(await Call(client, McpTools.VcsCommit, new() { ["stage"] = true, ["paths"] = new[] { "Other.txt" } }));

        JsonElement status = await Call(client, McpTools.VcsStatus);
        string Staged(string name) => status.GetProperty("changes").EnumerateArray().First(c => c.GetProperty("path").GetString() == name)
            .TryGetProperty("staged", out JsonElement held) ? held.GetString() ?? string.Empty : string.Empty;
        Assert.True(Staged("Notes.txt") == "part" && Staged("Other.txt") == "whole", $"vcs_status answered: {status}");
        string diff = Text(await Call(client, McpTools.VcsStatus, new() { ["path"] = "Notes.txt" }));
        int apart = diff.IndexOf("Staged, git's index against the last commit:", StringComparison.Ordinal);
        Assert.True(apart > 0 && diff[apart..].Contains("+TWO", StringComparison.Ordinal) && !diff[apart..].Contains("+ONE", StringComparison.Ordinal),
            $"vcs_status of Notes.txt shows no staged part of TWO alone: {diff}");

        Text(await Call(client, McpTools.VcsCommit, new() { ["stage"] = false, ["paths"] = new[] { "Other.txt" } }));
        Assert.Equal("Notes.txt", (await TestGit.RunAsync(repository, "diff", "--cached", "--name-only")).Trim());
        string made = Text(await Call(client, McpTools.VcsCommit, new() { ["message"] = "Capital two" }));
        Assert.Contains("Capital two", made, StringComparison.Ordinal);
        Assert.Equal("one\nTWO\nthree\nfour\n", await TestGit.RunAsync(repository, "show", "HEAD:Notes.txt"));
        Assert.Equal("old\n", await TestGit.RunAsync(repository, "show", "HEAD:Other.txt"));
        Assert.Equal("ONE\nTWO\nTHREE\nfour\n", File.ReadAllText(notes));
    }

    /// <summary>vcs_commit's lines commit one change of a file whose index holds a change taken back out of the working
    /// tree since: the change only staged stays staged, the committed one taken in beside it, and the change left out
    /// stays in the working tree.</summary>
    private async Task An_agents_commit_of_some_lines_keeps_a_change_only_staged_staged()
    {
        string repository = TestFolders.Create("mcp-partial-staged-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\nthree\nfour\nfive\nsix\nseven\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(notes, "one\nTWO\nthree\nfour\nfive\nsix\nseven\n");
        await TestGit.RunAsync(repository, "add", "Notes.txt");
        File.WriteAllText(notes, "ONE\ntwo\nthree\nfour\nfive\nsix\nSEVEN\n");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        string made = Text(await Call(client, McpTools.VcsCommit, new() { ["message"] = "Capital one", ["lines"] = new Dictionary<string, string> { ["Notes.txt"] = "1" } }));
        Assert.EndsWith(" Capital one; left uncommitted: 1 change of Notes.txt", made, StringComparison.Ordinal);
        Assert.Equal("ONE\ntwo\nthree\nfour\nfive\nsix\nseven\n", await TestGit.RunAsync(repository, "show", "HEAD:Notes.txt"));
        string index = await TestGit.RunAsync(repository, "show", ":Notes.txt");
        Assert.True(index == "ONE\nTWO\nthree\nfour\nfive\nsix\nseven\n", $"The change only staged did not stay staged beside the one committed: the index holds\n{index}");
        Assert.Equal("ONE\ntwo\nthree\nfour\nfive\nsix\nSEVEN\n", File.ReadAllText(notes));
    }

    /// <summary>vcs_commit with movesFirst commits a file git mv moved and then rewrote past what git finds alike as a rename,
    /// as the Commit window offers: the move first, a commit of its own with that message, then the edits with the
    /// message, the answer naming both; a commit whose moves git keeps with their edits makes no commit of them first.</summary>
    private async Task An_agents_commit_of_a_moved_and_rewritten_file_with_moves_first_commits_the_move_as_a_rename_first()
    {
        string repository = TestFolders.Create("mcp-moves-first-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "Shop.cs"), "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n");
        File.WriteAllText(Path.Combine(repository, "Till.cs"), "class Till\n{\n    int total;\n\n    void Ring(int price) { total += price; }\n}\n");
        await TestGit.InitAsync(repository);
        await TestGit.RunAsync(repository, "mv", "Shop.cs", "Store.cs");
        File.WriteAllText(Path.Combine(repository, "Store.cs"), "record Store(string Name, decimal Takings);\n\nstatic class Stores\n{\n    public static Store Open(string name) => new(name, 0m);\n}\n");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        string made = Text(await Call(client, McpTools.VcsCommit, new() { ["message"] = "Rework the store", ["movesFirst"] = "Rename the shop" }));
        Assert.Matches("^[0-9a-f]{7,} Rename the shop \\(the moves\\), then [0-9a-f]{7,} Rework the store$", made);
        Assert.Equal("Rework the store\nRename the shop\nFirst", (await TestGit.RunAsync(repository, "log", "-3", "--format=%s")).Trim());
        Assert.Equal("R100\tShop.cs\tStore.cs", (await TestGit.RunAsync(repository, "show", "--name-status", "--format=", "-M", "HEAD~1")).Trim());

        // A move git still finds alike once its edit goes in with it is committed with the edit, as one.
        await TestGit.RunAsync(repository, "mv", "Till.cs", "Register.cs");
        File.AppendAllText(Path.Combine(repository, "Register.cs"), "// rung up\n");
        string kept = Text(await Call(client, McpTools.VcsCommit, new() { ["message"] = "Rename the till", ["movesFirst"] = "Move the till" }));
        Assert.Matches("^[0-9a-f]{7,} Rename the till$", kept);
        Assert.Matches("^R[0-9]{3}\tTill.cs\tRegister.cs$", (await TestGit.RunAsync(repository, "show", "--name-status", "--format=", "-M", "HEAD")).Trim());
    }

    /// <summary>An agent's edit the IDE could not save (its file read-only) is never committed as the disk's older text:
    /// vcs_commit is refused naming the file, and nothing is committed.</summary>
    private async Task An_agents_commit_with_an_edit_whose_save_was_refused_is_refused_naming_the_file()
    {
        string repository = TestFolders.Create("mcp-unsaved-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(notes, "one\n2\n");
        File.SetAttributes(notes, FileAttributes.ReadOnly);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        string edited = Text(await Call(client, McpTools.Edit, new() { ["path"] = "Notes.txt", ["oldText"] = "2\n", ["newText"] = "two, edited\n" }));
        Assert.Contains("not saved: Notes.txt is read-only", edited, StringComparison.Ordinal);
        CallToolResult refused = await Result(client, McpTools.VcsCommit, new() { ["message"] = "The disk's text" });
        Assert.True(refused.IsError == true && Text(refused).Contains("Notes.txt could not be saved: Notes.txt is read-only", StringComparison.Ordinal),
            $"vcs_commit with Notes.txt's edit unsaved answered: {Text(refused)}");
        Assert.Equal("First", (await TestGit.RunAsync(repository, "log", "-1", "--format=%s")).Trim());
        File.SetAttributes(notes, FileAttributes.Normal);
    }

    /// <summary>An agent's format, Code Cleanup and Optimize Imports of a file the editor shows read-only, a library's XAML
    /// as Source Link gave it and a framework type the language server decompiled, are refused saying so: nothing there
    /// changes, so nothing is said to be reformatted, tidied or already so; and each other refactoring, rename and code
    /// action there are refused up front in the same words. Reformat Code, Code Cleanup, Optimize Imports, the
    /// refactorings and the Edit menu's changes grey on the decompiled file, saying why.</summary>
    private async Task An_agents_format_tidy_refactorings_rename_and_code_actions_of_a_read_only_xaml_or_cs_file_are_refused_saying_it_is_read_only()
    {
        string folder = await TakeFolderAsync();
        // Where Passenger keeps what Source Link gives of a library, as it would hold Fluent's Button.xaml.
        string button = TestFolders.Write(Path.Combine(OwnState, "MetadataAsSource", "SourceLink", "Avalonia.Themes.Fluent", "0123456789abcdef", "Controls"), "Button.xaml",
            "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\">\n<ControlTheme x:Key=\"{x:Type Button}\"   TargetType=\"Button\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
            + "<Setter Property=\"Padding\"    Value=\"8\"/>\n</ControlTheme>\n</ResourceDictionary>\n");
        string laid = File.ReadAllText(button);
        await using McpClient client = await Start(["--mcp", folder]);

        CallToolResult markup = await Result(client, McpTools.Format, new() { ["path"] = button });
        Assert.True(markup.IsError == true && Text(markup).Contains("Reformat Code was not applied: Button.xaml is read-only", StringComparison.Ordinal),
            $"format of a read-only XAML file answered: {Text(markup)}");
        Assert.True(File.ReadAllText(button) == laid, "format wrote a read-only XAML file");
        CallToolResult cleaned = await Result(client, McpTools.Refactor, new() { ["kind"] = "code_cleanup", ["path"] = button });
        Assert.True(cleaned.IsError == true && Text(cleaned).Contains("Code Cleanup was not applied: Button.xaml is read-only", StringComparison.Ordinal),
            $"code_cleanup of a read-only XAML file answered: {Text(cleaned)}");
        Assert.True(File.ReadAllText(button) == laid, "code_cleanup wrote a read-only XAML file");
        CallToolResult extracted = await Result(client, McpTools.ApplyCodeAction, new() { ["path"] = button, ["line"] = 3, ["column"] = 38, ["title"] = "Extract Resource", ["name"] = "Padding8" });
        Assert.True(extracted.IsError == true && Text(extracted).Contains("'Extract Resource' was not applied: Button.xaml is read-only", StringComparison.Ordinal),
            $"apply_code_action on a read-only XAML file answered: {Text(extracted)}");
        Assert.True(File.ReadAllText(button) == laid, "apply_code_action wrote a read-only XAML file");

        string decompiled = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "Program.cs", ["line"] = 6, ["column"] = 1 })).EnumerateArray())
            .GetProperty("path").GetString()!;
        Assert.Contains("MetadataAsSource", decompiled, StringComparison.Ordinal);
        string written = File.ReadAllText(decompiled);
        CallToolResult code = await Result(client, McpTools.Format, new() { ["path"] = decompiled });
        Assert.True(code.IsError == true && Text(code).Contains($"Reformat Code was not applied: {Path.GetFileName(decompiled)} is read-only", StringComparison.Ordinal),
            $"format of a decompiled C# file answered: {Text(code)}");
        Assert.True(File.ReadAllText(decompiled) == written, "format wrote a decompiled C# file");
        foreach ((string kind, string name) in new[] { ("optimize_imports", "Optimize Imports"), ("code_cleanup", "Code Cleanup") })
        {
            CallToolResult tidied = await Result(client, McpTools.Refactor, new() { ["kind"] = kind, ["path"] = decompiled });
            Assert.True(tidied.IsError == true && Text(tidied).Contains($"{name} was not applied: {Path.GetFileName(decompiled)} is read-only", StringComparison.Ordinal),
                $"{kind} of a decompiled C# file answered: {Text(tidied)}");
        }
        Assert.True(File.ReadAllText(decompiled) == written, "Optimize Imports or Code Cleanup wrote a decompiled C# file");
        // The type's declaration, where each refactoring and rename would begin.
        string[] lines = written.Split('\n');
        int declared = Array.FindIndex(lines, l => l.Contains("class Console", StringComparison.Ordinal));
        Assert.True(declared >= 0, $"The decompiled {Path.GetFileName(decompiled)} declares no class Console:\n{written}");
        int column = lines[declared].IndexOf("Console", StringComparison.Ordinal) + 1;
        List<string> answered = [];
        foreach ((string kind, string name) in new[] { ("change_signature", "Change Signature"), ("safe_delete", "Safe Delete"), ("move", "Move"),
            ("extract_interface", "Extract Interface"), ("pull_members_up", "Pull Members Up"), ("push_members_down", "Push Members Down"),
            ("introduce_field", "Introduce Field"), ("introduce_parameter", "Introduce Parameter"), ("encapsulate_field", "Encapsulate Field") })
        {
            CallToolResult refactored = await Result(client, McpTools.Refactor, new() { ["kind"] = kind, ["path"] = decompiled, ["line"] = declared + 1, ["column"] = column });
            if (refactored.IsError != true || !Text(refactored).Contains($"{name} was not applied: {Path.GetFileName(decompiled)} is read-only", StringComparison.Ordinal))
            {
                answered.Add($"{kind}: {Text(refactored)}");
            }
        }
        CallToolResult renamed = await Result(client, McpTools.Rename, new() { ["path"] = decompiled, ["line"] = declared + 1, ["column"] = column, ["newName"] = "Terminal" });
        if (renamed.IsError != true || !Text(renamed).Contains($"Rename was not applied: {Path.GetFileName(decompiled)} is read-only", StringComparison.Ordinal))
        {
            answered.Add($"rename: {Text(renamed)}");
        }
        // Refused before the server is asked what it offers there, whatever the action.
        CallToolResult acted = await Result(client, McpTools.ApplyCodeAction, new() { ["path"] = decompiled, ["line"] = declared + 1, ["column"] = column, ["title"] = "Generate overrides..." });
        if (acted.IsError != true || !Text(acted).Contains($"'Generate overrides...' was not applied: {Path.GetFileName(decompiled)} is read-only", StringComparison.Ordinal))
        {
            answered.Add($"apply_code_action: {Text(acted)}");
        }
        Assert.True(answered.Count == 0, $"Refactorings, rename or a code action of the decompiled {Path.GetFileName(decompiled)} were not refused as read-only:\n{string.Join('\n', answered)}");
        Assert.True(File.ReadAllText(decompiled) == written, "A refactoring, rename or code action wrote a decompiled C# file");

        await Call(client, McpTools.Open, new() { ["path"] = decompiled });
        foreach (string id in new[] { AppActions.Reformat, AppActions.CodeCleanup, AppActions.OptimizeUsings, AppActions.Rename, AppActions.ExtractMethod, AppActions.ChangeSignature,
            EditorActions.Paste, EditorActions.Duplicate, EditorActions.JoinLines })
        {
            string action = Assert.Single((await Call(client, McpTools.IdeActions, new() { ["query"] = id })).EnumerateArray(),
                a => a.GetString()!.StartsWith($"{id}:", StringComparison.Ordinal)).GetString()!;
            Assert.True(action.EndsWith($" - cannot run: {Path.GetFileName(decompiled)} is read-only", StringComparison.Ordinal),
                $"{id} could run on a decompiled C# file: {action}");
        }
    }

    /// <summary>An agent's push in its own IDE to a remote that asks for a login is refused at once, saying so and that
    /// nobody is there to give it, rather than waiting on a question nobody sees: the commit is made, the remote takes nothing.</summary>
    private async Task An_agents_push_to_a_remote_that_asks_for_a_login_says_so_where_nobody_is_at_the_IDE()
    {
        string repository = TestFolders.Create("mcp-login-");
        DeleteAtEnd(repository);
        string served = TestFolders.Create("mcp-served-");
        DeleteAtEnd(served);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        await TestGit.InitAsync(repository);
        await using StandInGitRemote remote = new(served);
        await TestGit.RunAsync(served, "init", "-q", "--bare", "repo.git");
        await TestGit.RunAsync(repository, "remote", "add", "origin", remote.Url);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\n");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        CallToolResult pushed = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Two", ["push"] = true });
        Assert.True(pushed.IsError == true && Text(pushed).Contains($"git asked for the login to {remote.Place}, and nobody is at this IDE to give it", StringComparison.Ordinal),
            $"vcs_commit's push to a remote asking for a login answered: {Text(pushed)}");
        Assert.Equal("Two", (await TestGit.RunAsync(repository, "log", "-1", "--format=%s")).Trim());
        Assert.Equal(0, remote.SignedIn);
    }

    /// <summary>vcs_status with the path of a file git mv renamed, one line of it changed since, answers that one line, as
    /// the file was where the last commit has it.</summary>
    private async Task An_agents_diff_of_a_file_git_mv_renamed_is_its_one_changed_line_from_where_the_last_commit_has_it()
    {
        string repository = TestFolders.Create("mcp-renamed-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "Foo.txt"), "line 1\nline 2\nline 3\nline 4\n");
        await TestGit.InitAsync(repository);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        await TestGit.RunAsync(repository, "mv", "Foo.txt", "Bar.txt");
        File.WriteAllText(Path.Combine(repository, "Bar.txt"), "line 1\nline 2 changed\nline 3\nline 4\n");
        string diff = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "Bar.txt" })).GetString()!.Replace("\r", string.Empty, StringComparison.Ordinal);
        Assert.True(diff.Contains("-line 2\n+line 2 changed\n", StringComparison.Ordinal) && !diff.Contains("+line 1", StringComparison.Ordinal),
            $"vcs_status of Bar.txt, git mv'd from Foo.txt with one line changed, answered:\n{diff}");
    }

    /// <summary>vcs_log of a file git mv renamed and not committed yet lists its commits, and those of its lines, from where
    /// the last commit has it.</summary>
    private async Task An_agents_history_of_a_file_git_mv_renamed_lists_its_commits_from_where_the_last_commit_has_it()
    {
        string repository = TestFolders.Create("mcp-renamed-log-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "Foo.txt"), "line 1\nline 2\nline 3\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(Path.Combine(repository, "Foo.txt"), "line 1\nline 2, second\nline 3\n");
        await TestGit.RunAsync(repository, "commit", "-q", "-am", "Second");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        await TestGit.RunAsync(repository, "mv", "Foo.txt", "Bar.txt");
        JsonElement log = await Call(client, McpTools.VcsLog, new() { ["path"] = "Bar.txt" });
        string[] subjects = [.. log.EnumerateArray().Select(c => c.GetProperty("subject").GetString()!)];
        Assert.True(subjects.SequenceEqual(["Second", "First"]), $"vcs_log of Bar.txt, git mv'd from Foo.txt and not committed, listed [{string.Join(", ", subjects)}]: {log.GetRawText()}");
        JsonElement lines = await Call(client, McpTools.VcsLog, new() { ["path"] = "Bar.txt", ["startLine"] = 2, ["endLine"] = 2 });
        Assert.Equal(["Second", "First"], lines.EnumerateArray().Select(c => c.GetProperty("subject").GetString()));
    }

    /// <summary>A repository git refuses as another user's (as on a FAT or network drive) is said to be refused, in git's
    /// words naming safe.directory, by vcs_status and the window's status bar, never as no repository; Passenger sets no
    /// safe.directory itself, and once the person's configuration names it the next read takes the repository.</summary>
    private async Task A_repository_git_refuses_as_another_users_is_said_to_be_refused_in_gits_words_naming_safe_directory()
    {
        string repository = TestFolders.Create("mcp-dubious-");
        string home = TestFolders.Create("mcp-dubious-config-");
        DeleteAtEnd(repository);
        DeleteAtEnd(home);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        await TestGit.InitAsync(repository);
        string global = Path.Combine(home, "gitconfig");
        File.WriteAllText(global, string.Empty);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false,
            environment: new() { ["GIT_TEST_ASSUME_DIFFERENT_OWNER"] = "1", ["GIT_CONFIG_GLOBAL"] = global });
        foreach (Dictionary<string, object?>? arguments in new Dictionary<string, object?>?[] { null, new() { ["path"] = "Notes.txt" } })
        {
            CallToolResult refused = await Result(client, McpTools.VcsStatus, arguments);
            string said = Text(refused);
            Assert.True(refused.IsError == true && said.Contains("detected dubious ownership", StringComparison.Ordinal) && said.Contains("safe.directory", StringComparison.Ordinal),
                $"vcs_status {JsonSerializer.Serialize(arguments)} over a repository git refuses as another user's answered, rather than git's reason: {said}");
        }
        string bar = string.Empty;
        await Waiting.UntilAsync(async () => (bar = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["control"] = "#StatusText" })))
            .Contains("Git refuses this folder's repository: detected dubious ownership", StringComparison.Ordinal), TimeSpan.FromSeconds(10), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(bar.Contains("Git refuses this folder's repository: detected dubious ownership", StringComparison.Ordinal),
            $"The window's status bar, over a repository git refuses, does not say why: {bar}");
        Assert.True(File.ReadAllText(global).Length == 0, $"Passenger wrote to the person's Git configuration: {File.ReadAllText(global)}");

        // As the person would, having read git's words.
        File.WriteAllText(global, "[safe]\n\tdirectory = *\n");
        Assert.Equal("main", (await Call(client, McpTools.VcsStatus)).GetProperty("branch").GetString());
    }

    /// <summary>With no git on the PATH, a folder's repository is never taken for none: the Version control menu says "Git is
    /// not installed" in place of its items, a notice the keys and the pointer pass over, where Create Git Repository…
    /// would be, the status bar saying why and how to install it; vcs_status answers that git is not installed, and
    /// ide_actions that Create Git Repository cannot run for it.</summary>
    private async Task With_no_git_on_the_path_a_repository_is_said_to_need_git_in_the_version_control_menu_never_taken_for_none()
    {
        string repository = TestFolders.Create("mcp-no-git-");
        string bare = TestFolders.Create("mcp-no-git-path-");
        DeleteAtEnd(repository);
        DeleteAtEnd(bare);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        await TestGit.InitAsync(repository);

        // macOS's /bin holds no git, as an empty folder holds none elsewhere; a terminal's TERM, so no login shell's PATH is read.
        await using McpClient client = await Start(["--mcp", repository], languageServer: false,
            environment: new() { ["PATH"] = OperatingSystem.IsMacOS() ? "/bin" : bare, ["TERM"] = "dumb" });
        CallToolResult status = await Result(client, McpTools.VcsStatus);
        Assert.True(status.IsError == true && Text(status).Contains("git is not installed", StringComparison.Ordinal),
            $"vcs_status over a repository with no git on the PATH answered, rather than that git is not installed: {Text(status)}");

        await Call(client, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "#BranchButton" });
        Dictionary<string, object?> items = new() { ["app"] = "ide", ["filter"] = "MenuItem" };
        string menu = string.Empty;
        await Waiting.UntilAsync(async () => (menu = Text(await Call(client, McpTools.AppTree, items))).StartsWith("MenuItem", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), Cancel, TimeSpan.FromMilliseconds(200));
        string notice = $"MenuItem.{StyleClasses.MenuNotice} \"Git is not installed\"";
        Assert.True(menu.Split('\n').Any(line => line.StartsWith(notice, StringComparison.Ordinal) && line.Contains("[disabled]", StringComparison.Ordinal))
            && !menu.Contains("Create Git Repository", StringComparison.Ordinal),
            $"The Version control menu over a repository with no git on the PATH holds, rather than the notice that Git is not installed: {menu}");
        string bar = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["control"] = "#StatusText" }));
        Assert.True(bar.Contains("git is not installed", StringComparison.Ordinal),
            $"The status bar, once the Version control menu said Git is not installed, does not say why: {bar}");
        string[] creating = [.. (await Call(client, McpTools.IdeActions, new() { ["query"] = AppActions.CreateRepository })).EnumerateArray().Select(a => a.GetString()!)];
        Assert.True(creating.Any(a => a.StartsWith($"{AppActions.CreateRepository}:", StringComparison.Ordinal) && a.Contains("cannot run: git is not installed", StringComparison.Ordinal)),
            $"ide_actions, over a repository with no git on the PATH, offers Create Git Repository rather than that git is not installed: {string.Join("; ", creating)}");
    }

    /// <summary>An agent's ide_action runs Create Git Repository in a folder of no repository, answered once it is made: the
    /// folder's files and the .NET .gitignore written beside them are its changes, its build output left out; asked again,
    /// it is refused saying the folder is in one.</summary>
    private async Task An_agents_create_git_repository_writes_the_dotnet_gitignore_and_is_refused_once_the_folder_is_in_one()
    {
        string folder = TestFolders.Create("mcp-create-repository-");
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\n");
        TestFolders.Write(folder, $"bin/Debug/{TestProjects.Framework}/Notes.dll", string.Empty);
        TestFolders.Write(folder, $"obj/Debug/{TestProjects.Framework}/Notes.dll", string.Empty);
        // The run gives the window's dotnet new a hive of its own on Windows, which an agent's own IDE has not.
        Dictionary<string, string?>? home = OperatingSystem.IsWindows() ? new() { ["DOTNET_CLI_HOME"] = Directory.CreateDirectory(Path.Combine(TestRun.Root, "mcp-dotnet-home")).FullName } : null;
        await using McpClient client = await Start(["--mcp", folder], environment: home, languageServer: false);
        CallToolResult none = await Result(client, McpTools.VcsStatus);
        Assert.True(none.IsError == true && Text(none).Contains(Refusals.NoRepository, StringComparison.Ordinal), $"vcs_status in a folder of no repository answered: {Text(none)}");

        string made = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.CreateRepository }));
        Assert.Equal($"Created a Git repository in {Path.GetFileName(folder)}", made);
        Assert.Contains("[Oo]bj/", File.ReadAllText(Path.Combine(folder, ".gitignore")), StringComparison.Ordinal);
        Assert.Equal([".gitignore unversioned", "Notes.txt unversioned"], (await Call(client, McpTools.VcsStatus)).GetProperty("changes").EnumerateArray()
            .Select(c => $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}").Order(StringComparer.Ordinal));

        CallToolResult again = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.CreateRepository });
        Assert.True(again.IsError == true && Text(again).Contains(Refusals.InRepository, StringComparison.Ordinal),
            $"Create Git Repository in a folder made a repository answered: {Text(again)}");
    }

    /// <summary>vcs_stash lists each stash with its commit, and an agent's drop by that commit takes that stash wherever it
    /// now is, though another program stashed after the list was read; asked again, it is refused as gone.</summary>
    private async Task An_agents_drop_of_a_stash_by_its_commit_takes_that_stash_though_another_program_stashed_since_the_list()
    {
        string repository = TestFolders.Create("mcp-stash-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\n");
        await TestGit.InitAsync(repository);
        File.AppendAllText(notes, "older\n");
        await TestGit.RunAsync(repository, "stash", "push", "-m", "Older");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        JsonElement older = Assert.Single((await Call(client, McpTools.VcsStash)).EnumerateArray());
        string commit = older.GetProperty("commit").GetString()!;
        Assert.StartsWith(commit, (await TestGit.RunAsync(repository, "rev-parse", "stash@{0}")).Trim(), StringComparison.Ordinal);
        File.AppendAllText(notes, "newer\n");
        await TestGit.RunAsync(repository, "stash", "push", "-m", "Newer");

        Dictionary<string, object?> drop = new() { ["action"] = "drop", ["commit"] = commit };
        string[] left = [.. (await Call(client, McpTools.VcsStash, drop)).EnumerateArray().Select(s => s.GetProperty("message").GetString()!)];
        Assert.True(left is ["On main: Newer"], $"vcs_stash's drop of Older by its commit, after another program stashed Newer, left [{string.Join(", ", left)}]");
        CallToolResult again = await Result(client, McpTools.VcsStash, drop);
        Assert.True(again.IsError == true && Text(again).Contains("No stash in the list has commit", StringComparison.Ordinal), $"A drop of a stash gone from the list answered: {Text(again)}");
        Assert.Equal("On main: Newer", (await TestGit.RunAsync(repository, "stash", "list", "--format=%gs")).Trim());
    }

    /// <summary>Commit and Push to a remote that refuses every push: the agent hears the refusal, in git's words, as the
    /// tool's error, and the commit stays made.</summary>
    private async Task An_agents_push_the_remote_refuses_is_an_error_naming_the_refusal_and_the_commit_stays_made()
    {
        string root = TestFolders.Create("mcp-push-");
        try
        {
            string work = await RepositoryAsync(root, refusing: true);
            await using McpClient client = await Start(["--mcp", work]);
            CallToolResult refused = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Two as a number", ["push"] = true });
            string said = Text(refused);
            Assert.True(refused.IsError == true, $"A push the remote refused was answered as done: {said}");
            Assert.Contains("pre-receive hook declined", said, StringComparison.Ordinal);
            Assert.Contains("pushes are closed here", said, StringComparison.Ordinal);
            Assert.Equal("Two as a number", (await TestGit.RunAsync(work, "log", "-1", "--format=%s")).Trim());
            Assert.True((await TestGit.RunAsync(Path.Combine(root, "remote.git"), "for-each-ref", "refs/heads")).Trim().Length == 0, "The remote took the push it refused");
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    /// <summary>Started as the Finder or the Dock start it, Passenger runs the git its login shell's PATH finds, though the
    /// profile leaves a job running, and no git it starts waits on that job.</summary>
    private async Task A_login_profile_that_leaves_a_job_running_holds_up_no_git_Passenger_starts()
    {
        if (OperatingSystem.IsWindows())
        {
            // A Windows program gets the person's whole environment, PATH included.
            return;
        }
        string home = TestFolders.Create("home-");
        string repository = TestFolders.Create("mcp-shell-");
        try
        {
            // As `some-daemon &` in a profile: the job keeps the shell's output open long after the shell has ended.
            string used = await ProfileGitAsync(home, "sleep 300 &\n");
            await TestGit.RunAsync(repository, "init", "-q");
            await using McpClient client = await Start(["--mcp", repository], environment: Launchd(home));
            await Call(client, McpTools.VcsStatus);
            Assert.True(File.Exists(used), "With a job its profile left running, Passenger never took the login shell's PATH, and ran a git other than the one it finds");

            // Each git start waits for the shell's environment while it is still being read.
            long began = Stopwatch.GetTimestamp();
            await Call(client, McpTools.VcsStatus);
            TimeSpan took = Stopwatch.GetElapsedTime(began);
            Assert.True(took < TimeSpan.FromSeconds(10).Scaled(),
                $"Git's status took {took.TotalSeconds:0.0} s the second time: the git it ran waited for the login shell's environment again, the profile's job holding up its read");
        }
        finally
        {
            TestFolders.Delete(home);
            TestFolders.Delete(repository);
        }
    }

    /// <summary>Started as the Finder starts it, over a login shell that fails with no terminal (as an rc's <c>exec tmux</c>
    /// does), Passenger's log says the shell's environment was not read, naming the shell, in its words; a shell that skips
    /// that step where its environment is being read, as profiles do for the variables that say so, gives Passenger its
    /// PATH, those variables kept from the programs Passenger starts.</summary>
    private async Task A_login_shell_that_fails_without_a_terminal_is_logged_in_its_words_and_one_that_skips_it_while_read_gives_its_path()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A Windows program gets the person's whole environment, PATH included");
            return;
        }
        string home = TestFolders.Create("home-");
        string repository = TestFolders.Create("mcp-shell-fails-");
        DeleteAtEnd(home);
        DeleteAtEnd(repository);
        await TestGit.RunAsync(repository, "init", "-q");
        string fails = Path.Combine(home, "fails-without-terminal.sh");
        Executable(fails, "#!/bin/sh\necho 'open terminal failed: not a terminal' >&2\nexit 1\n");
        Dictionary<string, string?> environment = Launchd(home);
        environment["SHELL"] = fails;
        string log = Path.Combine(OwnState, "logs", "passenger.log");
        await using (McpClient client = await Start(["--mcp", repository], languageServer: false, environment: environment))
        {
            await Call(client, McpTools.VcsStatus);
            string said = string.Empty;
            await Waiting.UntilAsync(() => Task.FromResult((said = File.Exists(log) ? Ui.Shared(log) : string.Empty)
                .Contains("open terminal failed: not a terminal", StringComparison.Ordinal)), TimeSpan.FromSeconds(10), Cancel, TimeSpan.FromMilliseconds(200));
            string? line = said.Split('\n').FirstOrDefault(l => l.Contains("The login shell's environment was not read", StringComparison.Ordinal));
            Assert.True(line?.Contains($"{fails} -l -i: it ended with exit code 1: open terminal failed: not a terminal", StringComparison.Ordinal) == true,
                $"Over a login shell that fails with no terminal, Passenger's log does not say so, naming it, in its words: {line ?? said[Math.Max(0, said.Length - 2000)..]}");
        }

        // As oh-my-zsh's tmux autostart, failing with no terminal unless read; past it, no system profile moves its tools down.
        string tools = Path.Combine(home, "tools");
        Directory.CreateDirectory(tools);
        string seen = Path.Combine(home, "git-environment");
        await EnvironmentGitAsync(tools, seen);
        string skips = Path.Combine(home, "skips-while-read.sh");
        Executable(skips, "#!/bin/sh\nif [ \"$VSCODE_RESOLVING_ENVIRONMENT\" != 1 ] || [ \"$INTELLIJ_ENVIRONMENT_READER\" != 1 ]; then\n"
            + $"  echo 'open terminal failed: not a terminal' >&2\n  exit 1\nfi\nPATH=\"{tools}:$PATH\"; export PATH\nshift 3\nexec /bin/sh -c \"$1\"\n");
        environment["SHELL"] = skips;
        await using (McpClient client = await Start(["--mcp", repository], languageServer: false, environment: environment))
        {
            await Call(client, McpTools.VcsStatus);
        }
        Assert.True(File.Exists(seen), "Over a login shell that skips what needs a terminal while its environment is read, Passenger never took its PATH");
        string[] gitHad = File.ReadAllLines(seen);
        Assert.True(!gitHad.Any(v => v.StartsWith("VSCODE_RESOLVING_ENVIRONMENT=", StringComparison.Ordinal) || v.StartsWith("INTELLIJ_ENVIRONMENT_READER=", StringComparison.Ordinal)),
            $"The variables set while the login shell's environment was read reached the git Passenger started: {string.Join(", ", gitHad.Where(v => v.Contains("ENVIRONMENT", StringComparison.Ordinal)))}");
    }

    /// <summary>Started with no SHELL, as a service or a desktop entry may start it on Linux, Passenger reads its environment
    /// from the login shell its terminal runs: the git it starts has what the profile set that the Agent Shell's shell
    /// reads too.</summary>
    private async Task With_no_SHELL_the_IDEs_environment_is_read_from_the_login_shell_its_terminal_runs()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A Windows program gets the person's whole environment, and its terminal runs PowerShell");
            return;
        }
        string home = TestFolders.Create("home-");
        string repository = TestFolders.Create("mcp-login-shell-");
        DeleteAtEnd(home);
        DeleteAtEnd(repository);
        await TestGit.RunAsync(repository, "init", "-q");
        string tools = Path.Combine(home, "tools");
        Directory.CreateDirectory(tools);
        string seen = Path.Combine(home, "git-environment");
        await EnvironmentGitAsync(tools, seen);
        // Each login profile names itself: sh reads .profile, bash its own before it, zsh .zprofile.
        foreach (string profile in (string[])[".profile", ".bash_profile", ".zprofile"])
        {
            File.WriteAllText(Path.Combine(home, profile), $"export PATH=\"{tools}:$PATH\"\nexport LOGIN_PROFILE={profile}\n");
        }
        Dictionary<string, string?> environment = Launchd(home);
        environment["SHELL"] = null;
        await using McpClient client = await Start(["--mcp", repository], languageServer: false, environment: environment);
        await Call(client, McpTools.VcsStatus);
        string? read = File.Exists(seen) ? File.ReadAllLines(seen).FirstOrDefault(v => v.StartsWith("LOGIN_PROFILE=", StringComparison.Ordinal))?["LOGIN_PROFILE=".Length..] : null;
        Assert.True(read is not null, "With no SHELL, Passenger never took a login shell's PATH, and ran a git other than the one its profile puts first");

        string answer = (await Call(client, McpTools.TerminalRun, new() { ["command"] = "echo \"runs:$LOGIN_PROFILE\"" })).GetString()!;
        string? runs = Regex.Match(answer, @"runs:(\.\w+)") is { Success: true } said ? said.Groups[1].Value : null;
        Assert.True(runs == read,
            $"With no SHELL, Passenger's environment was read through the login profile {read} and its terminal's shell read {runs ?? "none"}: "
            + $"a PATH or variable set in one is not what the other finds. The terminal answered:\n{answer}");
    }

    /// <summary>Started as the Finder starts it, over a login profile that takes a while to set DOTNET_CLI_HOME, Passenger
    /// finds the global dotnet-ef under that home, as the dotnet it starts would: Migrations names it.</summary>
    private async Task A_global_dotnet_ef_under_the_DOTNET_CLI_HOME_a_login_profile_sets_is_found()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A Windows program gets the person's whole environment, and reads no login profile");
            return;
        }
        string folder = await ShopApp.MakeAsync(design: true, tool: null);
        string home = TestFolders.Create("home-");
        DeleteAtEnd(folder);
        DeleteAtEnd(home);
        string cli = Directory.CreateDirectory(Path.Combine(home, "cli")).FullName;
        // A global tool as dotnet tool install -g leaves it, of a major other than the project's EF Core, so Migrations refuses it by name.
        string tools = Directory.CreateDirectory(Path.Combine(cli, ".dotnet", "tools")).FullName;
        Executable(Path.Combine(tools, "dotnet-ef"), "#!/bin/sh\nexit 1\n");
        Directory.CreateDirectory(Path.Combine(tools, ".store", "dotnet-ef", "1.0.0"));
        // NuGet takes its settings from DOTNET_CLI_HOME too: the run's, so nothing is restored anew.
        string settings = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_CLI_HOME")!, ".nuget", "NuGet", "NuGet.Config");
        File.Copy(settings, Path.Combine(Directory.CreateDirectory(Path.Combine(cli, ".nuget", "NuGet")).FullName, "NuGet.Config"));
        foreach (string profile in (string[])[".profile", ".bash_profile", ".zprofile"])
        {
            File.WriteAllText(Path.Combine(home, profile), $"sleep 3\nexport DOTNET_CLI_HOME=\"{cli}\"\n");
        }
        Dictionary<string, string?> environment = Launchd(home);
        environment["DOTNET_CLI_HOME"] = null;
        await using McpClient client = await Start(["--mcp", folder], languageServer: false, environment: environment);
        string found = Text(await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.AddMigration, ["input"] = "Initial" }));
        Assert.True(found.Contains("the global dotnet-ef 1.0.0", StringComparison.OrdinalIgnoreCase),
            $"Add Migration did not name the global dotnet-ef under the DOTNET_CLI_HOME the login profile sets ({cli}), answering: {found}");
    }

    /// <summary>A script at <paramref name="path"/> that only its owner may change, as a shell or a tool on the PATH is.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Executable(string path, string script)
    {
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A call whose arguments its tool does not take is answered with what is wrong and every parameter the tool
    /// takes, before the tool runs.</summary>
    private async Task An_agents_call_with_a_parameter_its_tool_lacks_or_misses_hears_which_and_what_the_tool_takes()
    {
        string folder = TestFolders.Create("mcp-parameters-");
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        CallToolResult named = await Result(client, McpTools.FindSymbol, new() { ["name"] = "Language" });
        Assert.True(named.IsError == true && Text(named) == "find_symbol has no parameter name, and needs query. Its parameters: query (string, required), kind (string), "
            + "limit (integer), exact (boolean)", $"find_symbol given name for query answered: {Text(named)}");
        CallToolResult typed = await Result(client, McpTools.EditorText, new() { ["path"] = "Program.cs", ["from"] = "first" });
        Assert.True(typed.IsError == true && Text(typed).StartsWith("editor_text takes an integer as from, not \"first\". Its parameters: path (string, required), from (integer)", StringComparison.Ordinal),
            $"editor_text given a word for a line answered: {Text(typed)}");
    }

    /// <summary>An agent's view of the IDE's actions: every one the IDE declares, listed with why it cannot run now, those an
    /// answer has no room for read on through the groups it names, and run by its id as its shortcut would.</summary>
    private async Task An_agent_lists_every_action_the_ide_declares_and_one_it_runs_says_why_when_it_cannot()
    {
        await TakeFolderAsync();
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        CallToolResult all = await Result(client, McpTools.IdeActions);
        using JsonDocument first = JsonDocument.Parse(all.Content.OfType<TextContentBlock>().First().Text);
        List<string> listed = [.. first.RootElement.EnumerateArray().Select(a => a.GetString()!)];
        // Cut short, the answer names the groups of those it left out, which an agent reads on through.
        if (all.Content.Skip(1).OfType<TextContentBlock>().FirstOrDefault()?.Text is { } more)
        {
            Assert.True(more.Contains("more action", StringComparison.Ordinal) && more.Contains("they are of the group", StringComparison.Ordinal),
                $"ide_actions cut short said of what it left out: {more}");
            foreach (Match group in Regex.Matches(more, "\"([^\"]+)\""))
            {
                // A group read on is listed whole, with those of it the first answer listed before it was cut.
                HashSet<string> had = [.. listed.Select(Id)];
                listed.AddRange([.. (await Call(client, McpTools.IdeActions, new() { ["query"] = group.Groups[1].Value })).EnumerateArray().Select(a => a.GetString()!)
                    .Where(line => line.Contains($" [{group.Groups[1].Value}]", StringComparison.Ordinal) && !had.Contains(Id(line)))]);
            }
        }
        HashSet<string> ids = [.. listed.Select(Id)];
        string[] declared = [.. typeof(AppActions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)];
        string[] missing = [.. declared.Where(id => !ids.Contains(id))];
        Assert.True(missing.Length == 0, $"ide_actions leaves out {string.Join(", ", missing)}, read on through the groups it named: {Text(all).Length:N0} characters, its last words {Text(all)[^Math.Min(300, Text(all).Length)..]}");

        string hotReload = Assert.Single(listed, l => l.StartsWith($"{AppActions.ApplyHotReloadChanges}: ", StringComparison.Ordinal));
        Assert.Contains(" - cannot run: Nothing runs", hotReload, StringComparison.Ordinal);
        CallToolResult refused = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.ApplyHotReloadChanges });
        Assert.True(refused.IsError == true && Text(refused).Contains($"{AppActions.ApplyHotReloadChanges} did not run: Nothing runs", StringComparison.Ordinal),
            $"Apply Hot Reload Changes with nothing running answered: {Text(refused)}");
        string selectElement = Assert.Single(listed, l => l.StartsWith($"{AppActions.SelectElement}: ", StringComparison.Ordinal));
        Assert.Contains("cannot run: No Avalonia or WPF app is being debugged", selectElement, StringComparison.Ordinal);
        string restart = Assert.Single(listed, l => l.StartsWith($"{AppActions.RestartCSharpServer}: Restart C# Language Server [IDE]", StringComparison.Ordinal));
        Assert.Contains($" - cannot run: {Refusals.NoCSharp}", restart, StringComparison.Ordinal);

        Assert.Equal($"Ran {AppActions.ShowStructure}", (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ShowStructure })).GetString());
        // A console app's folder holds no Godot project, which the Game window serves.
        string game = Assert.Single(listed, l => l.StartsWith($"{AppActions.ShowGame}: ", StringComparison.Ordinal));
        Assert.Contains($" - cannot run: {Refusals.NoGodotProject}", game, StringComparison.Ordinal);

        // An action's id, which its line begins with.
        static string Id(string line) => line[..line.IndexOf(": ", StringComparison.Ordinal)];
    }

    /// <summary>debug_state of processes says each program's command line as Attach to Process shows it, its secrets masked:
    /// a program given a password and an API key on its command line is listed by its name with neither.</summary>
    private async Task An_agents_list_of_running_programs_masks_the_secrets_their_command_lines_hold()
    {
        const string Password = "hunter2-on-the-command-line";
        const string ApiKey = "sk-live-123456";
        string folder = PathNames.Canonical(TestFolders.Create("mcp-processes-"));
        DeleteAtEnd(folder);
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost("LoopingConsoleApp"), ["sleep", $"--Password={Password}", $"--ApiKey={ApiKey}"]);
        start.RedirectStandardOutput = true;
        using Process looping = ChildProcesses.Start(start);
        try
        {
            Assert.Equal("looping", looping.StandardOutput.ReadLine());
            await using McpClient client = await Start(["--mcp", folder], languageServer: false);
            JsonElement program = Assert.Single((await Call(client, McpTools.DebugState, new() { ["of"] = "processes" })).EnumerateArray(),
                p => p.GetProperty("processId").GetInt32() == looping.Id);
            Assert.Equal("LoopingConsoleApp", program.GetProperty("program").GetString());
            string line = program.GetProperty("commandLine").GetString()!;
            Assert.True(line.EndsWith($" sleep --Password={ContainerSecrets.Masked} --ApiKey={ContainerSecrets.Masked}", StringComparison.Ordinal)
                && !line.Contains(Password, StringComparison.Ordinal) && !line.Contains(ApiKey, StringComparison.Ordinal),
                $"debug_state gave an agent the program's secrets: {line}");
        }
        finally
        {
            ChildProcesses.Kill(looping);
        }
    }

    /// <summary>An agent's Passenger started on a solution filter beside a file-based program, then given the solution and the
    /// filter again: while the filter is open, workspace names it as the solution and marks unloaded the project it leaves
    /// out, the program loaded with its run configuration; with the solution, nothing is marked.</summary>
    private async Task An_agent_on_a_solution_filter_is_told_the_filter_and_the_project_it_leaves_out_and_keeps_the_program_beside_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-filter-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "App/Program.cs", "System.Console.WriteLine(\"app\");\n");
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "App.slnf", "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [ \"App\\\\App.csproj\" ]\n  }\n}\n");
        TestFolders.Write(folder, "scripts/tool.cs", "#!/usr/bin/env dotnet\nSystem.Console.WriteLine(\"tool\");\n");
        string filter = Path.Combine(folder, "App.slnf");
        await using McpClient client = await Start(["--mcp", filter], languageServer: false);
        Told(await Call(client, McpTools.Open, new() { ["path"] = "App.slnf" }), filter, "Passenger started on the filter");
        Told(await Call(client, McpTools.Open, new() { ["path"] = "Shop.slnx" }), Path.Combine(folder, "Shop.slnx"), "the solution opened");
        Told(await Call(client, McpTools.Open, new() { ["path"] = "App.slnf" }), filter, "the filter opened over the solution");

        static void Told(JsonElement workspace, string solution, string after)
        {
            string[] projects = [.. workspace.GetProperty("projects").EnumerateArray()
                .Select(p => p.GetProperty("name").GetString() + (p.TryGetProperty("unloaded", out JsonElement marked) && marked.GetBoolean() ? " unloaded" : string.Empty))];
            string[] configurations = [.. workspace.GetProperty("runConfigurations").EnumerateArray().Select(c => c.GetString()!)];
            Assert.True(workspace.GetProperty("solution").GetString() == solution
                && projects.SequenceEqual(["App", solution.EndsWith(".slnf", StringComparison.Ordinal) ? "Lib unloaded" : "Lib", "tool"]) && configurations.Contains("tool"),
                $"After {after}, the workspace answered: {workspace}, where an agent builds and tests what it names and marks loaded");
        }
    }

    /// <summary>An agent's Passenger started on its folder in lower case, from a working folder and a PWD spelling it so (an
    /// agent started in <c>~/documents/app</c>): its workspace answer and the restore and build it makes name the folder as
    /// the disk does, so no build through the disk's spelling restores, compiles or deletes what this one wrote.</summary>
    [Fact]
    public async Task An_agents_passenger_given_its_folder_in_other_letter_case_answers_restores_and_builds_it_as_the_disk_names_it()
    {
        Assert.SkipWhen(PathNames.Comparison == StringComparison.Ordinal, PathsTests.CaseTold);
        string root = PathNames.Canonical(TestFolders.Create("mcp-case-"));
        DeleteAtEnd(root);
        string folder = Path.Combine(root, "Shop Front");
        string project = DiskSpelled.Write(folder);
        string spelled = folder.ToLowerInvariant();
        await using McpClient client = await Start(["--mcp", spelled], spelled, new() { ["PWD"] = spelled }, languageServer: false);
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.GetProperty("folder").GetString() == folder && workspace.GetProperty("solution").GetString() == Path.Combine(folder, "Shop.slnx"),
            $"Started on {spelled}, Passenger answered the workspace: {workspace}");
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The build ended: {built}");
        DiskSpelled.Check(spelled, folder, project);
    }

    /// <summary>An agent's publish, build's with a project to publish: the project to a folder, answered with where it landed
    /// or why it did not.</summary>
    private async Task An_agent_publishes_a_project_and_is_told_the_folder_it_landed_in_or_why_not()
    {
        await TakeFolderAsync();
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement published = await Call(client, McpTools.Build, new() { ["publish"] = "Sum" });
        Assert.True(published.GetProperty("succeeded").GetBoolean(), $"The publish ended: {published}");
        string landed = published.GetProperty("folder").GetString() ?? string.Empty;
        Assert.True(File.Exists(Path.Combine(landed, "Sum.dll")), $"The publish says it landed in {landed}, which holds no Sum.dll");
        Assert.StartsWith("Published Sum to ", published.GetProperty("outcome").GetString(), StringComparison.Ordinal);

        JsonElement missing = await Call(client, McpTools.Build, new() { ["publish"] = "Sum", ["profile"] = "Nightly" });
        Assert.False(missing.GetProperty("succeeded").GetBoolean(), $"A publish with no such profile ended: {missing}");
        Assert.Contains("has no publish profile Nightly", missing.GetProperty("outcome").GetString(), StringComparison.Ordinal);
    }

    /// <summary>A home whose login profile (zsh's and sh's) puts a git of its own first on the PATH, as Homebrew's shellenv
    /// does, and then does <paramref name="more"/>; the git notes that it ran in the file whose path this returns.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task<string> ProfileGitAsync(string home, string more = "")
    {
        string tools = Path.Combine(home, "tools");
        Directory.CreateDirectory(tools);
        string git = Path.Combine(tools, "git");
        string used = Path.Combine(home, "used");
        string real = (await global::Passenger.Core.Programs.GitAsync())!;
        File.WriteAllText(git, $"#!/bin/sh\ntouch \"{used}\"\nexec {real} \"$@\"\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(home, ".zprofile"), $"export PATH=\"{tools}:$PATH\"\n{more}");
        File.WriteAllText(Path.Combine(home, ".profile"), $"export PATH=\"{tools}:$PATH\"\n{more}");
        return used;
    }

    /// <summary>A git in <paramref name="tools"/> that writes the environment it was started with to <paramref name="seen"/>,
    /// whole: beside it, then put in its place, so a read finds one git's whole environment while Passenger starts others.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task EnvironmentGitAsync(string tools, string seen) =>
        Executable(Path.Combine(tools, "git"), $"#!/bin/sh\nenv > \"{seen}.$$\" && mv -f \"{seen}.$$\" \"{seen}\"\nexec {(await Programs.GitAsync())!} \"$@\"\n");

    /// <summary>launchd's environment, as the Finder or the Dock start Passenger: the bare PATH, no terminal, no .NET or zsh
    /// settings of the person's, the home given.</summary>
    private static Dictionary<string, string?> Launchd(string home) => new()
    {
        ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin",
        ["HOME"] = home,
        ["SHELL"] = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh",
        ["TERM"] = null,
        ["ZDOTDIR"] = null,
        [RuntimeVariables.Root] = null,
    };
}
