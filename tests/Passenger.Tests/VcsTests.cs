using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App.Editors;
using Passenger.App.History;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Git over a real working tree: an edit grows a change bar (blank lines at the end too, and a large rewrite's
/// off the window's thread), which a rename keeps and a commit clears; Rollback Lines and Rollback put changes back; a
/// branch made from the toolbar is checked out, and going back reloads the open file; Commit, Stash and Unstash run one
/// at a time, a drop asked first and the log leaving the stash out; a status never lists a save's temporary file, and the
/// last commit's texts are read again only when HEAD moves.</summary>
[AboutMacOS]
public sealed partial class VcsTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("git-");
    private readonly string trash = TestFolders.Create("trash-");

    public VcsTests()
    {
        TestFolders.Prepare(() =>
        {
            File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original);
            TestGit.Init(folder);
        }, folder);
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    /// <summary>The gate's change bars and commits as one flow: an edit's bar cleared by a commit from the Commit
    /// window, a renamed file's bars against where the last commit has it, a bar's click and the diff's arrow putting a
    /// change back, Commit and Stash one at a time, and a submodule offered only where its commit moved, each step the
    /// gate test it names in an instance of its own.</summary>
    [Gate]
    [AvaloniaFact]
    public void Changes_show_as_bars_that_a_commit_clears_a_rename_keeps_and_a_bar_or_the_diff_puts_back()
    {
        Flow.StepApart<VcsTests>("an edit shows a change bar and committing it from the commit window clears it", t => t.An_edit_shows_a_change_bar_and_committing_it_from_the_commit_window_clears_it());
        Flow.StepApart<VcsTests>("a renamed file keeps its change bars against what the last commit has where it was", t => t.A_renamed_file_keeps_its_change_bars_against_what_the_last_commit_has_where_it_was());
        Flow.StepApart<VcsTests>("a click on a change bar shows what the lines were and rolls them back", t => t.A_click_on_a_change_bar_shows_what_the_lines_were_and_rolls_them_back());
        Flow.StepApart<VcsTests>("show diff puts the last commit beside the open file and its arrow puts a change back", t => t.Show_diff_puts_the_last_commit_beside_the_open_file_and_its_arrow_puts_a_change_back());
        Flow.StepApart<VcsTests>("commit and stash are off while one runs so a second press makes no second", t => t.Commit_and_stash_are_off_while_one_runs_so_a_second_press_makes_no_second());
        Flow.StepApart<VcsTests>("a submodule whose own files alone changed is not offered for commit and one whose commit moved is", t => t.A_submodule_whose_own_files_alone_changed_is_not_offered_for_commit_and_one_whose_commit_moved_is());
    }

    /// <summary>The gate's branch, update and stash as one flow: a branch made from the toolbar checked out and going
    /// back reloading the open file, Update Project bringing the upstream's commit into the open file, stashing every
    /// change and new file and unstashing them back, and open files saved before a checkout or rollback and taking what
    /// git puts there, each step the gate test it names in an instance of its own.</summary>
    [Gate]
    [AvaloniaFact]
    public void Branches_updates_and_stashes_bring_their_files_in_with_the_open_files_saved_first_and_reloaded()
    {
        Flow.StepApart<VcsTests>("a new branch from the toolbar is checked out and going back reloads the open file", t => t.A_new_branch_from_the_toolbar_is_checked_out_and_going_back_reloads_the_open_file());
        Flow.StepApart<VcsTests>("update project brings in the upstreams commit and the open file shows it", t => t.Update_project_brings_in_the_upstreams_commit_and_the_open_file_shows_it());
        Flow.StepApart<VcsTests>("stashing puts every change away unsaved and new files too and unstashing brings them back", t => t.Stashing_puts_every_change_away_unsaved_and_new_files_too_and_unstashing_brings_them_back());
        Flow.StepApart<VcsTests>("open files are saved before a checkout or a rollback and take what git puts there with no question", t => t.Open_files_are_saved_before_a_checkout_or_a_rollback_and_take_what_git_puts_there_with_no_question());
    }

    /// <summary>The gate's log and annotations as one flow: a merge laid out in two lanes with what a commit did to a
    /// file, and each line's commit named in annotations, a typed line marked and a click choosing the commit, each
    /// step the gate test it names in an instance of its own.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_log_and_annotations_show_each_commit_and_what_it_did()
    {
        Flow.StepApart<VcsTests>("the log lays out a merge in two lanes and shows what a commit did to a file", t => t.The_log_lays_out_a_merge_in_two_lanes_and_shows_what_a_commit_did_to_a_file());
        Flow.StepApart<VcsTests>("annotations name each lines commit mark a typed line and a click chooses the commit", t => t.Annotations_name_each_lines_commit_mark_a_typed_line_and_a_click_chooses_the_commit());
    }

    /// <summary>Rollback Lines and Rollback as one flow, each step one test's check in an instance of its own: the
    /// change at the caret put back, a file another program reads or adds kept safe, an unversioned file trashed and
    /// brought back by Undo, blank lines at the end, a name with brackets, a file chosen through a refresh, and a
    /// save's temporary file never listed.</summary>
    [AvaloniaFact]
    public void Rollback_puts_changes_back_as_the_last_commit_has_them_losing_nothing_another_program_holds_or_added()
    {
        Flow.StepApart<VcsTests>("rollback lines puts the change under the caret back as the last commit has it", t => t.Rollback_lines_puts_the_change_under_the_caret_back_as_the_last_commit_has_it());
        Flow.StepApart<VcsTests>("rollback puts back a file another program was reading and deletes a new one it held once they are let go", t => t.Rollback_puts_back_a_file_another_program_was_reading_and_deletes_a_new_one_it_held_once_they_are_let_go());
        Flow.StepApart<VcsTests>("rollback moves an unversioned file to the Trash and one undo brings it back", t => t.Rollback_moves_an_unversioned_file_to_the_Trash_and_one_undo_brings_it_back());
        Flow.StepApart<VcsTests>("rollback keeps a new file another program added to git on disk and says so first", t => t.Rollback_keeps_a_new_file_another_program_added_to_git_on_disk_and_says_so_first());
        Flow.StepApart<VcsTests>("blank lines added at the end of a file show an added bar and a difference and roll back", t => t.Blank_lines_added_at_the_end_of_a_file_show_an_added_bar_and_a_difference_and_roll_back());
        Flow.StepApart<VcsTests>("a file name with brackets is that file alone to roll back and to commit", t => t.A_file_name_with_brackets_is_that_file_alone_to_roll_back_and_to_commit());
        Flow.StepApart<VcsTests>("a file chosen in the commit window stays chosen through a refresh and rollback names and rolls back it alone", t => t.A_file_chosen_in_the_commit_window_stays_chosen_through_a_refresh_and_rollback_names_and_rolls_back_it_alone());
        Flow.StepApart<VcsTests>("a file saved as git reads the folder is listed as changed and its saves temporary file never is", t => t.A_file_saved_as_git_reads_the_folder_is_listed_as_changed_and_its_saves_temporary_file_never_is());
    }

    /// <summary>The Commit window as one flow, each step one test's check in an instance of its own: git started off
    /// the window's thread, the diff's keys, renames by git mv or the Explorer committed as renames, Amend rewording
    /// alone and keeping what was typed, a message file another program holds, a hook's job holding git's output
    /// done within seconds, the messages committed offered again, and the Git keys of VCS Operations, Commit and Push and
    /// New Branch.</summary>
    [AvaloniaFact]
    public void A_commit_takes_what_the_person_chose_and_typed_as_renames_where_files_moved_never_held_by_another_program()
    {
        Flow.StepApart<VcsTests>("the git commands the window asks for start off its thread", t => t.The_git_commands_the_window_asks_for_start_off_its_thread());
        Flow.StepApart<VcsTests>("the commit windows Show Diff and a diffs Settings name and take the keys of the keymap in force", t => t.The_commit_windows_Show_Diff_and_a_diffs_Settings_name_and_take_the_keys_of_the_keymap_in_force());
        Flow.StepApart<VcsTests>("a file git mv renamed elsewhere is compared with where the last commit has it", t => t.A_file_git_mv_renamed_elsewhere_is_compared_with_where_the_last_commit_has_it());
        Flow.StepApart<VcsTests>("amend with no file chosen rewords the last commit and leaves a rename out of it", t => t.Amend_with_no_file_chosen_rewords_the_last_commit_and_leaves_a_rename_out_of_it());
        Flow.StepApart<VcsTests>("a file renamed in the Explorer is committed as a rename", t => t.A_file_renamed_in_the_Explorer_is_committed_as_a_rename());
        Flow.StepApart<VcsTests>("a copy made at the name an undone rename gave its file is new and compared with nothing", t => t.A_copy_made_at_the_name_an_undone_rename_gave_its_file_is_new_and_compared_with_nothing());
        Flow.StepApart<VcsTests>("amends last message read late keeps what was typed meanwhile and lands nowhere once amend is off", t => t.Amends_last_message_read_late_keeps_what_was_typed_meanwhile_and_lands_nowhere_once_amend_is_off());
        Flow.StepApart<VcsTests>("a commit whose message file another program holds is made and the file goes once let go", t => t.A_commit_whose_message_file_another_program_holds_is_made_and_the_file_goes_once_let_go());
        Flow.StepApart<VcsTests>("a commit whose hook leaves a job holding gits output is done within seconds", t => t.A_commit_whose_hook_leaves_a_job_holding_gits_output_is_done_within_seconds());
        Flow.StepApart<VcsTests>("the commit windows message history offers each message committed newest first to change and commit again", t => t.The_commit_windows_message_history_offers_each_message_committed_newest_first_to_change_and_commit_again());
        Flow.StepApart<VcsTests>("vcs operations commit and push and new branch run from their keys", t => t.Vcs_operations_commit_and_push_and_new_branch_run_from_their_keys());
    }

    /// <summary>The log and annotations as one flow, each step in an instance of its own: annotations in a SHA-256
    /// repository, the Commit and Log menus ignoring a file, reverting a commit and branching from one, a commit's file in
    /// the log jumping to its source, a commit made elsewhere reaching the log of the tree's top and of a folder in it, an
    /// interactive rebase making the history its preview showed, a tag made from the log, annotated and signed, pushed,
    /// moved and deleted here and on the remote, and a worktree made, opened, committed from and removed.</summary>
    [AvaloniaFact]
    public void The_log_follows_commits_made_anywhere_and_its_menus_act_on_the_commit_or_file_chosen()
    {
        Flow.StepApart<VcsTests>("annotations name each lines commit in a sha256 repository", t => t.Annotations_name_each_lines_commit_in_a_sha256_repository());
        Flow.StepApart<VcsTests>("the commit and log menus ignore a file revert a commit and branch from one", t => t.The_commit_and_log_menus_ignore_a_file_revert_a_commit_and_branch_from_one());
        Flow.StepApart<VcsTests>("a commits file in the log jumps to its source and copies its path from its menu", t => t.A_commits_file_in_the_log_jumps_to_its_source_and_copies_its_path_from_its_menu());
        Flow.StepApart<VcsTests>("a commit made elsewhere reaches the log of the working trees top and of a folder in it", t => t.A_commit_made_elsewhere_reaches_the_log_of_the_working_trees_top_and_of_a_folder_in_it());
        Flow.StepApart<VcsTests>("an interactive rebase from the log moves squashes rewords and drops commits making the history its preview showed", t => t.An_interactive_rebase_from_the_log_moves_squashes_rewords_and_drops_commits_making_the_history_its_preview_showed());
        Flow.StepApart<VcsTests>("an interactive rebase stops for conflicts and at an edit and goes on by skip commit and continue rebase", t => t.An_interactive_rebase_stops_for_conflicts_and_at_an_edit_and_goes_on_by_skip_commit_and_continue_rebase());
        Flow.StepApart<VcsTests>("a tag made from the log annotated and signed is pushed moved and deleted here and on the remote as its preview said", t => t.A_tag_made_from_the_log_annotated_and_signed_is_pushed_moved_and_deleted_here_and_on_the_remote_as_its_preview_said());
        Flow.StepApart<VcsTests>("a worktree made from a branchs menu opens in its own window is committed from and removed once asked", t => t.A_worktree_made_from_a_branchs_menu_opens_in_its_own_window_is_committed_from_and_removed_once_asked());
    }

    /// <summary>Stash and Unstash as one flow, each step the check one test made in an instance of its own: nothing to
    /// stash refused saying so, Backspace in Unstash keeping every stash and Delete asking before dropping the one
    /// chosen, Enter and a double click showing a stash's files beside the working tree's and popping nothing, Unstash
    /// taking the stash chosen though another program stashed or dropped since, and a stash leaving the log as it
    /// was.</summary>
    [AvaloniaFact]
    public void Stash_and_Unstash_keep_every_stash_take_the_one_chosen_and_leave_the_log_as_it_was()
    {
        Flow.StepApart<VcsTests>("stash changes with nothing to stash is refused saying so", t => t.Stash_changes_with_nothing_to_stash_is_refused_saying_so());
        Flow.StepApart<VcsTests>("backspace in unstash changes keeps every stash and delete asks before dropping the chosen one once", t => t.Backspace_in_unstash_changes_keeps_every_stash_and_delete_asks_before_dropping_the_chosen_one_once());
        Flow.StepApart<VcsTests>("enter or a double click on a stash shows its files beside the working tree and pops nothing", t => t.Enter_or_a_double_click_on_a_stash_shows_its_files_beside_the_working_tree_and_pops_nothing());
        Flow.StepApart<VcsTests>("unstash changes takes the stash chosen though another program stashed or dropped since the list was read", t => t.Unstash_changes_takes_the_stash_chosen_though_another_program_stashed_or_dropped_since_the_list_was_read());
        Flow.StepApart<VcsTests>("stashing a change and a new file leaves the git log as it was", t => t.Stashing_a_change_and_a_new_file_leaves_the_git_log_as_it_was());
        Flow.StepApart<VcsTests>("the commit windows stashes list each stashs files and diffs then apply pop and drop it as said and undo brings a dropped one back", t => t.The_commit_windows_stashes_list_each_stashs_files_and_diffs_then_apply_pop_and_drop_it_as_said_and_undo_brings_a_dropped_one_back());
    }

    /// <summary>Update Project and Push as one flow, each step the check one test made in an instance of its own: Stop
    /// ending an update whose remote stalls with a commit running after it, a diverged branch merged where git is not
    /// told how and rebased where pull.rebase says, an uncommitted edit kept through both, a push asking the person for
    /// a login, a new branch pushed to the only remote or asking which of several, a rewritten branch force pushed under a
    /// lease, and a checkout refused as git busy while an update pulls.</summary>
    [AvaloniaFact]
    public void Update_push_and_pull_bring_the_upstream_in_as_git_is_told_ask_for_a_login_and_stop_when_asked()
    {
        Flow.StepApart<VcsTests>("stop ends an update whose remote stalls and a commit runs after it", t => t.Stop_ends_an_update_whose_remote_stalls_and_a_commit_runs_after_it());
        Flow.StepApart<VcsTests>("update project merges a diverged branch where git is not told how keeping an uncommitted edit", t => t.Update_project_merges_a_diverged_branch_where_git_is_not_told_how_keeping_an_uncommitted_edit());
        Flow.StepApart<VcsTests>("update project with pull rebase rebases onto the upstream keeping an unrelated uncommitted edit", t => t.Update_project_with_pull_rebase_rebases_onto_the_upstream_keeping_an_unrelated_uncommitted_edit());
        Flow.StepApart<VcsTests>("push to a remote that asks for a login asks the person and goes once they sign in", t => t.Push_to_a_remote_that_asks_for_a_login_asks_the_person_and_goes_once_they_sign_in());
        Flow.StepApart<VcsTests>("push of a new branch goes to the only remote whatever its name and asks which of several", t => t.Push_of_a_new_branch_goes_to_the_only_remote_whatever_its_name_and_asks_which_of_several());
        Flow.StepApart<VcsTests>("force push replaces a rewritten branch on the remote once agreed but never what someone pushed since", t => t.Force_push_replaces_a_rewritten_branch_on_the_remote_once_agreed_but_never_what_someone_pushed_since());
        Flow.StepApart<VcsTests>("a checkout asked for while update project pulls is refused as git busy and the update lands", t => t.A_checkout_asked_for_while_update_project_pulls_is_refused_as_git_busy_and_the_update_lands());
        Flow.StepApart<VcsTests>("remotes are added pushed to renamed repointed and removed as their dialogs said a token in a url masked", t => t.Remotes_are_added_pushed_to_renamed_repointed_and_removed_as_their_dialogs_said_a_token_in_a_url_masked());
    }

    /// <summary>Create Git Repository and Add to .gitignore as one flow, each step the check one test made in an
    /// instance of its own: a new repository leaving build output out of the changes, Add to .gitignore keeping a
    /// file's byte order mark, Windows line breaks and an unsaved edit, making the file as its .editorconfig asks with
    /// Undo and Redo putting back the same bytes, having git ignore the chosen file and no other whatever its name
    /// holds, and a .gitignore read as git reads it, from the Explorer's Add to .gitignore and Go to Ignoring Line.</summary>
    [AvaloniaFact]
    public void A_new_repository_and_gitignore_leave_out_what_the_person_chose_keeping_the_files_bytes()
    {
        Flow.StepApart<VcsTests>("create git repository from the commit windows empty state leaves build output out of the changes", t => t.Create_git_repository_from_the_commit_windows_empty_state_leaves_build_output_out_of_the_changes());
        Flow.StepApart<VcsTests>("add to gitignore keeps the files byte order mark its windows line breaks and an unsaved edit to it", t => t.Add_to_gitignore_keeps_the_files_byte_order_mark_its_windows_line_breaks_and_an_unsaved_edit_to_it());
        Flow.StepApart<VcsTests>("add to gitignore making the file as its editorconfig asks and Undo then Redo put it back in the same bytes", t => t.Add_to_gitignore_making_the_file_as_its_editorconfig_asks_and_Undo_then_Redo_put_it_back_in_the_same_bytes());
        Flow.StepApart<VcsTests>("add to gitignore has git ignore the chosen file and no other whatever its name holds", t => t.Add_to_gitignore_has_git_ignore_the_chosen_file_and_no_other_whatever_its_name_holds());
        Flow.StepApart<VcsTests>("the explorer adds to gitignore and goes to the ignoring line and a gitignore says what each line ignores as git answers", t => t.The_explorer_adds_to_gitignore_and_goes_to_the_ignoring_line_and_a_gitignore_says_what_each_line_ignores_as_git_answers());
    }

    /// <summary>A diff's window as one flow, each step the check one test made in an instance of its own: the IDE's
    /// keys working on the side that has them with Escape still closing it, F4 opening the file at the caret's line
    /// from either side, the next and previous difference by the keymap, a closed diff let go by the open file it
    /// showed, and an edit in the diff's window after the file's tab closed saved with the others and as the window
    /// closes.</summary>
    [AvaloniaFact]
    public void A_diff_window_takes_the_IDEs_keys_opens_its_file_walks_its_changes_and_loses_no_edit()
    {
        Flow.StepApart<VcsTests>("the IDEs keys work in a diffs window on the side with them and Escape still closes it", t => t.The_IDEs_keys_work_in_a_diffs_window_on_the_side_with_them_and_Escape_still_closes_it());
        Flow.StepApart<VcsTests>("f4 in a diff opens its file at the carets line from either side", t => t.F4_in_a_diff_opens_its_file_at_the_carets_line_from_either_side());
        Flow.StepApart<VcsTests>("a diffs next and previous difference are F7 and F8 off the Mac and F7 alone on it", t => t.A_diffs_next_and_previous_difference_are_F7_and_F8_off_the_Mac_and_F7_alone_on_it());
        Flow.StepApart<VcsTests>("a closed diff is let go by the open file it showed", t => t.A_closed_diff_is_let_go_by_the_open_file_it_showed());
        Flow.StepApart<VcsTests>("an edit in a diffs window after the files tab closed is saved with the others and as the window closes", t => t.An_edit_in_a_diffs_window_after_the_files_tab_closed_is_saved_with_the_others_and_as_the_window_closes());
    }

    /// <summary>Changes made outside the IDE as one flow, each step the check one test made in an instance of its own:
    /// a commit and a branch switch from outside a linked worktree and a folder opened below the repository's top
    /// followed by the bars, colours, commit list and branch, a save leaving the Explorer's chosen rows, the log and Go
    /// to File as they were, and coming back to the window reading no last commit's text again while HEAD moved
    /// elsewhere moves every file's bars in one read.</summary>
    [AvaloniaFact]
    public void Commits_and_branch_switches_made_outside_are_followed_by_the_bars_colours_log_and_branch()
    {
        Flow.StepApart<VcsTests>("a commit and a branch switch from outside a linked worktree are followed by the bars colours commit list and branch", t => t.A_commit_and_a_branch_switch_from_outside_a_linked_worktree_are_followed_by_the_bars_colours_commit_list_and_branch());
        Flow.StepApart<VcsTests>("a commit and a branch switch from outside a folder opened below the repositorys top are followed by the bars colours commit list and branch", t => t.A_commit_and_a_branch_switch_from_outside_a_folder_opened_below_the_repositorys_top_are_followed_by_the_bars_colours_commit_list_and_branch());
        Flow.StepApart<VcsTests>("a save leaves the explorers chosen rows the git log and go to file as they were", t => t.A_save_leaves_the_explorers_chosen_rows_the_git_log_and_go_to_file_as_they_were());
        Flow.StepApart<VcsTests>("coming back to the window reads no last commit text again and head moved elsewhere moves every files bars in one read", t => t.Coming_back_to_the_window_reads_no_last_commit_text_again_and_head_moved_elsewhere_moves_every_files_bars_in_one_read());
        Flow.StepApart<VcsTests>("a folder holding a second repository lists and commits each from the commit windows tab of it", t => t.A_folder_holding_a_second_repository_lists_and_commits_each_from_the_commit_windows_tab_of_it());
    }

    /// <summary>A reftable repository's changes from outside as one flow, every step needing a git that makes one (2.45
    /// and later): a commit and a branch switch followed by the bars, colours, commit list and branch, from the working
    /// tree's top and from a folder below it, each in an instance of its own.</summary>
    [AvaloniaFact]
    public void Commits_and_branch_switches_made_outside_a_reftable_repository_are_followed()
    {
        Flow.StepApart<VcsTests>("a commit and a branch switch from outside a reftable repository are followed by the bars colours commit list and branch (false)", t => t.A_commit_and_a_branch_switch_from_outside_a_reftable_repository_are_followed_by_the_bars_colours_commit_list_and_branch(false));
        Flow.StepApart<VcsTests>("a commit and a branch switch from outside a reftable repository are followed by the bars colours commit list and branch (true)", t => t.A_commit_and_a_branch_switch_from_outside_a_reftable_repository_are_followed_by_the_bars_colours_commit_list_and_branch(true));
    }

    /// <summary>Windows' files another program holds as one flow, every step Windows' alone: a commit made while a held
    /// file's save lands waiting for it and committing the saved text, and Add to .gitignore into a .gitignore another
    /// program holds saying it waits and adding once the file is let go, each in an instance of its own.</summary>
    [AvaloniaFact]
    public void A_file_another_program_holds_on_Windows_delays_no_commit_or_gitignore_and_loses_nothing()
    {
        Flow.StepApart<VcsTests>("a commit made while a held files save lands waits for it and commits the saved text", t => t.A_commit_made_while_a_held_files_save_lands_waits_for_it_and_commits_the_saved_text());
        Flow.StepApart<VcsTests>("add to gitignore into a gitignore another program holds says it waits and added once the file is let go", t => t.Add_to_gitignore_into_a_gitignore_another_program_holds_says_it_waits_and_added_once_the_file_is_let_go());
    }

    /// <summary>What a large working tree costs as one flow, each step the check one test made in an instance of its
    /// own: ticking a commit group of twenty thousand files counting its rows once, a save among five thousand tags
    /// allocating what one among five does and reading no ref, and typing in a 20,000-line file rewritten since the
    /// last commit moving its bars off the window's thread.</summary>
    [AvaloniaFact]
    public void A_large_working_tree_costs_a_key_a_save_and_a_tick_what_a_small_one_does()
    {
        Flow.StepApart<VcsTests>("ticking a commit group of twenty thousand files counts its rows once and allocates in proportion to them", t => t.Ticking_a_commit_group_of_twenty_thousand_files_counts_its_rows_once_and_allocates_in_proportion_to_them());
        Flow.StepApart<VcsTests>("a save among five thousand tags allocates on the ui thread what one among five does and reads no ref", t => t.A_save_among_five_thousand_tags_allocates_on_the_ui_thread_what_one_among_five_does_and_reads_no_ref());
        Flow.StepApart<VcsTests>("typing in a 20000 line file rewritten since the last commit moves its bars and a bar opens its change at once", t => t.Typing_in_a_20000_line_file_rewritten_since_the_last_commit_moves_its_bars_and_a_bar_opens_its_change_at_once());
    }

    /// <summary>Git's commands start off the window's thread, since a fork costs a large process tens of milliseconds: the
    /// working tree's status and branch as it opens, an edit's bars, a commit and the log start none of theirs on
    /// it.</summary>
    private void The_git_commands_the_window_asks_for_start_off_its_thread()
    {
        string tree = PathNames.Canonical(folder);
        List<string> onWindow = [];
        int started = 0;
        void Heard(object? sender, ChildStart start)
        {
            if (start.Folder.Length > 0 && PathNames.Canonical(start.Folder) == tree
                && Path.GetFileNameWithoutExtension(start.Command.Split(' ')[0].Trim('"')) == "git")
            {
                Interlocked.Increment(ref started);
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    lock (onWindow)
                    {
                        onWindow.Add(start.Command);
                    }
                }
            }
        }
        ChildProcesses.Started += Heard;
        try
        {
            MainWindow window = Open();
            Ui.Landed(() => window.Vcs.Branch == "main", "the branch read as the working tree opened");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
            window.KeyTextInput("long ");
            Ui.Landed(() => Marks(window).Any(m => m.Kind == ChangeMarkKind.Modified && m.Line == 2), "a modified bar on line 3");
            window.Perform(AppActions.Commit);
            Ui.Landed(() => window.FocusManager?.GetFocusedElement() is TextBox, "the commit message to have the keys");
            window.KeyTextInput("Count with a long");
            Ui.Press(window, OperatingSystem.IsMacOS() ? "meta Enter" : "control Enter");
            Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == "Count with a long", "the commit");
            window.Perform(AppActions.ShowGit);
            Ui.Landed(() => window.GitLog.Rows.Count >= 2, "the log's two commits");
            window.Close();
        }
        finally
        {
            ChildProcesses.Started -= Heard;
        }
        Assert.True(started > 0, "No git command was heard starting in the working tree");
        lock (onWindow)
        {
            Assert.True(onWindow.Count == 0, $"{onWindow.Count} of the {started} git commands started on the window's thread: {string.Join("; ", onWindow.Distinct())}");
        }
    }

    /// <summary>An edit's change bar, and a commit from the Commit window, made by the message's own key (Cmd+Enter, Ctrl+Enter
    /// elsewhere) rather than a new line in it, which clears the bar.</summary>
    private void An_edit_shows_a_change_bar_and_committing_it_from_the_commit_window_clears_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("main", window.Vcs.Branch);

        // Line 3 changed, and a line added after the last.
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        Ui.Landed(() => Marks(window).Any(m => m.Kind == ChangeMarkKind.Modified && m.Line == 2), "a modified bar on line 3");

        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.Landed(() => commit.Chosen.Any(c => c.Name == "Shop.cs" && c.Kind == ChangeKind.Modified), "Shop.cs listed as modified");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Commit));
        Ui.Landed(() => window.FocusManager?.GetFocusedElement() is TextBox, "the commit message to have the keys");
        window.KeyTextInput("Count with a long");
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta Enter" : "control Enter");
        Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == "Count with a long",
            $"Committing from the message's own key: the log's last commit is '{Git("log", "-1", "--format=%s").Trim()}' and the message reads '{commit.Message}'");

        Assert.Contains("long int count;", File.ReadAllText(Path.Combine(folder, "Shop.cs")), StringComparison.Ordinal);
        Ui.Landed(() => Marks(window).Count == 0 && window.Vcs.Changes.Count == 0, "the bars and the changes cleared");
        Assert.Empty(commit.Rows);
        Assert.Empty(commit.Message);
        window.Close();
    }

    /// <summary>The Commit window's Show Diff names the keys it takes there, Compare Files' in Visual Studio's keymap
    /// (Duplicate's off macOS), and a diff's Settings Rider's Ctrl+Shift+D (Cmd+Shift+D); each key opens what it names, and
    /// a key the person sets for either is named and opens it in place of the keymap's.</summary>
    private void The_commit_windows_Show_Diff_and_a_diffs_Settings_name_and_take_the_keys_of_the_keymap_in_force()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        string cmd = OperatingSystem.IsMacOS() ? "meta" : "control";
        MainWindow window = Open();
        try
        {
            window.Perform(AppActions.ShowCommit);
            CommitView commit = window.CommitView;
            Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Shop.cs"), "Shop.cs listed as changed");
            Button showDiff = commit.GetVisualDescendants().OfType<Button>().Single(b => (ToolTip.GetTip(b) as string)?.StartsWith("Show Diff", StringComparison.Ordinal) == true);
            Assert.Equal($"Show Diff ({Shortcut.Parse($"{cmd} D").Display()})", ToolTip.GetTip(showDiff));

            IDiffHost diff = DiffFromCommit(window, $"{cmd} D");
            Assert.Equal($"Settings ({Shortcut.Parse($"{cmd} shift D").Display()})", ToolTip.GetTip(OpenDiffs.ViewOf(diff).SettingsButton));
            SettingsOpen(diff, $"{cmd} shift D");
            diff.Close();
            Ui.Settle();

            window.ApplySettings(window.Settings with
            {
                Shortcuts = new() { [AppActions.ShowDiff] = [$"{cmd} alt D"], [AppActions.DiffSettings] = [$"{cmd} alt shift D"] },
            });
            Ui.Settle();
            Assert.Equal($"Show Diff ({Shortcut.Parse($"{cmd} alt D").Display()})", ToolTip.GetTip(showDiff));
            diff = DiffFromCommit(window, $"{cmd} alt D");
            DiffView view = OpenDiffs.ViewOf(diff);
            Assert.Equal($"Settings ({Shortcut.Parse($"{cmd} alt shift D").Display()})", ToolTip.GetTip(view.SettingsButton));
            view.Right.Focus();
            Ui.Press(OpenDiffs.KeysOf(diff), $"{cmd} shift D");
            Assert.False(view.SettingsButton.ContextMenu?.IsOpen, $"{cmd} shift D still opened the diff's settings, the person having set {cmd} alt shift D");
            SettingsOpen(diff, $"{cmd} alt shift D");
            diff.Close();
        }
        finally
        {
            window.ApplySettings(new Passenger.App.Settings.AppSettings());
            window.Close();
        }

        static IDiffHost DiffFromCommit(MainWindow window, string keys)
        {
            ListBox changes = Find(window.CommitView, window.CommitView.Rows);
            Ui.Click(Ui.Row(changes, window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "Shop.cs")));
            Ui.Press(window, keys);
            return OpenDiffs.Landed(window, $"the diff {keys} in the Commit window opens, as its Show Diff names");
        }

        static void SettingsOpen(IDiffHost diff, string keys)
        {
            DiffView view = OpenDiffs.ViewOf(diff);
            view.Right.Focus();
            Ui.Press(OpenDiffs.KeysOf(diff), keys);
            Assert.True(view.SettingsButton.ContextMenu?.IsOpen, $"{keys}, which the diff's Settings names, did not open them");
            view.SettingsButton.ContextMenu!.Close();
        }
    }

    private void A_renamed_file_keeps_its_change_bars_against_what_the_last_commit_has_where_it_was()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        Ui.Landed(() => Marks(window).Any(m => m.Kind == ChangeMarkKind.Modified && m.Line == 2), "a modified bar on line 3");

        Assert.Null(Ui.Await(window.RenameAsync(PathNames.Canonical(Path.Combine(folder, "Shop.cs")), "Store.cs"), "the rename"));
        editor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        Ui.Landed(() => Marks(window).Any(m => m.Line == 0), "a bar for the line typed after the rename, against the last commit's Shop.cs");
        Assert.Equal("Store.cs", window.EditorArea.Active?.Name);
        Assert.Contains(Marks(window), m => m.Kind == ChangeMarkKind.Modified && m.Line == 2);
        window.Close();
    }

    /// <summary>A file git mv renamed in a terminal before the folder opened, then one line of it changed, is measured from
    /// where the last commit has it: one change bar, Show Diff's one difference, and Rollback Lines putting that line back.</summary>
    private void A_file_git_mv_renamed_elsewhere_is_compared_with_where_the_last_commit_has_it()
    {
        Git("mv", "Shop.cs", "Store.cs");
        string store = Path.Combine(folder, "Store.cs");
        File.WriteAllText(store, Original.Replace("int count", "long count", StringComparison.Ordinal));
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(store);
        Ui.Settle();
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "Store.cs" && c.Kind == ChangeKind.Renamed), "Store.cs listed as renamed from Shop.cs");
        Ui.Landed(() => Marks(window).Count > 0, "Store.cs's change bars against what the last commit has as Shop.cs");
        Assert.True(Marks(window) is [{ Line: 2, Count: 1, Kind: ChangeMarkKind.Modified }],
            $"Store.cs, git mv'd from Shop.cs with one line changed, shows the bars [{string.Join(", ", Marks(window))}]: compared with nothing, every line is new");

        IDiffHost diff = Ui.Landed(window.ShowDiffAsync(PathNames.Canonical(store)), "Store.cs's diff")!;
        DiffView view = OpenDiffs.ViewOf(diff);
        Assert.Equal(Original, view.Left.Document.Snapshot.ToString());
        Assert.True(view.Hunks.Count == 1, $"Show Diff of the renamed Store.cs found {view.Hunks.Count} differences from the last commit's Shop.cs, not its one line");
        diff.Close();
        Ui.Settle();

        window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 0));
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => Text(window) == Original, "Rollback Lines to put line 3 back as the last commit's Shop.cs has it");
        Ui.Landed(() => Marks(window).Count == 0, "the bar gone with the change rolled back");
        window.Close();
    }

    /// <summary>Amend with no file chosen changes the last commit's message alone: the rename the Explorer made in the
    /// index goes into no commit and stays listed.</summary>
    private void Amend_with_no_file_chosen_rewords_the_last_commit_and_leaves_a_rename_out_of_it()
    {
        MainWindow window = Open();
        Assert.Null(Ui.Await(window.RenameAsync(PathNames.Canonical(Path.Combine(folder, "Shop.cs")), "Store.cs"), "the rename"));
        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Store.cs" && r.Change.Kind == ChangeKind.Renamed), "the rename in the Commit window");
        foreach (CommitRow row in commit.Rows.OfType<CommitRow>())
        {
            row.IsChosen = false;
        }
        Ui.ClickButton(commit, "Amend");
        Ui.Landed(() => commit.Message == "First", "Amend's message, the last commit's");
        commit.Message = "First, reworded";
        Ui.ClickButton(commit, "Commit");
        Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == "First, reworded", "the amended commit");

        Assert.Equal("1", Git("rev-list", "--count", "HEAD").Trim());
        string held = Git("show", "--name-status", "--format=", "HEAD").Trim();
        Assert.True(held == "A\tShop.cs", $"Amend with no file chosen took the index's rename into the last commit, which now holds [{held}]");
        string listed = string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"));
        Assert.True(window.Vcs.Changes.Any(c => c.Name == "Store.cs" && c.Kind == ChangeKind.Renamed), $"After the amend the rename is no change: Git lists [{listed}]");
        window.Close();
    }

    /// <summary>A file renamed in the Explorer, which stages the rename, commits with its change chosen: the old name, which
    /// only the last commit still has, is no pathspec git refuses to add.</summary>
    private void A_file_renamed_in_the_Explorer_is_committed_as_a_rename()
    {
        MainWindow window = Open();
        Assert.Null(Ui.Await(window.RenameAsync(PathNames.Canonical(Path.Combine(folder, "Shop.cs")), "Store.cs"), "the rename"));
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "Store.cs" && c.Kind == ChangeKind.Renamed), "Store.cs listed as renamed from Shop.cs");

        CommitResult made = Ui.Landed(window.Vcs.CommitAsync([.. window.Vcs.Changes.Where(c => c.Name == "Store.cs")], "Rename Shop", amend: false, push: false), "the commit");

        Assert.True(made.Commit.Succeeded, $"Committing the renamed file failed with: {made.Commit.Message}");
        Assert.Equal("R\tShop.cs\tStore.cs", Git("show", "--name-status", "--format=", "-M", "HEAD").Trim().Replace("R100", "R", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>A copy of a file made at the name a rename gave it, once Undo took the rename back, is new: Git lists it
    /// as unversioned, and its diff shows no file of the last commit's beside it, never the original's text (#2287).</summary>
    private void A_copy_made_at_the_name_an_undone_rename_gave_its_file_is_new_and_compared_with_nothing()
    {
        MainWindow window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        string shop = PathNames.Canonical(Path.Combine(folder, "Shop.cs"));
        string store = Path.Combine(folder, "Store.cs");
        Assert.Null(Ui.Await(window.RenameAsync(shop, "Store.cs"), "the rename"));
        window.ShowToolWindow(ToolWindowIds.Explorer);
        window.ExplorerView.Reveal(PathNames.Canonical(store));
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == PathNames.Canonical(store)));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.Exists(shop) && !File.Exists(store), () => $"Undo to take the rename back: {window.Status}");

        File.Copy(shop, store);
        store = PathNames.Canonical(store);
        Ui.WaitFor(() => window.Vcs.Changes.Any(c => c.Path == store && c.Kind == ChangeKind.Unversioned),
            () => $"the copy listed as unversioned, where Git lists [{string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"))}]");
        IDiffHost diff = Ui.Landed(window.ShowDiffAsync(store), "the copy's diff")!;
        OpenDiffs.ShowsNoFile(OpenDiffs.ViewOf(diff), MergeSide.Left);
        diff.Close();
        window.Close();
    }

    /// <summary>Amend's last-commit message, read while the person types or unticks Amend, never replaces what was typed
    /// meanwhile nor lands once Amend is off.</summary>
    private void Amends_last_message_read_late_keeps_what_was_typed_meanwhile_and_lands_nowhere_once_amend_is_off()
    {
        MainWindow window = Open();
        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.Settle();

        TaskCompletionSource answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Vcs.HoldNextLastMessage(answer.Task);
        Ui.ClickButton(commit, "Amend");
        commit.FocusMessage();
        window.KeyTextInput("Typed while git read");
        answer.SetResult();
        Ui.Settle();
        Assert.True(commit.Message == "Typed while git read", $"The last commit's message, read as Amend was ticked, replaced what was typed meanwhile: the box reads \"{commit.Message}\"");

        Ui.ClickButton(commit, "Amend");
        commit.Message = string.Empty;
        Ui.Settle();
        answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Vcs.HoldNextLastMessage(answer.Task);
        Ui.ClickButton(commit, "Amend");
        Ui.ClickButton(commit, "Amend");
        answer.SetResult();
        Ui.Settle();
        Assert.False(commit.Amend);
        Assert.True(commit.Message.Length == 0, $"The last commit's message landed in the box after Amend was unticked: it reads \"{commit.Message}\"");
        window.Close();
    }

    /// <summary>Ticking the Commit window's Unversioned Files of 20,000 new files counts its rows once, as for 200, and the
    /// window's thread allocates in proportion to the rows, never once more for each row ticked.</summary>
    private void Ticking_a_commit_group_of_twenty_thousand_files_counts_its_rows_once_and_allocates_in_proportion_to_them()
    {
        (long fewCounted, long fewBytes) = Tick(200);
        (long manyCounted, long manyBytes) = Tick(20_000);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Ticking 200 rows counted {fewCounted} and allocated {fewBytes / 1024:N0} KB on the window's thread; 20,000 rows counted {manyCounted} and allocated {manyBytes / 1024:N0} KB");
        Assert.True(manyCounted <= 2 * 20_000,
            $"Ticking a group of 20,000 files had its groups count {manyCounted:N0} rows (200 files: {fewCounted:N0}): each row ticked counted every row again");
        long perRow = (manyBytes - fewBytes) / (20_000 - 200);
        Assert.True(perRow <= 1024, $"Ticking a group allocated {perRow:N0} bytes on the window's thread for each row more ({fewBytes / 1024:N0} KB for 200, {manyBytes / 1024:N0} KB for 20,000)");

        (long Counted, long Bytes) Tick(int files)
        {
            string tree = TestFolders.Create("git-many-");
            try
            {
                File.WriteAllText(Path.Combine(tree, "Shop.cs"), Original);
                TestGit.Init(tree);
                for (int i = 0; i < files; i++)
                {
                    string under = Path.Combine(tree, "new", (i / 1000).ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(under);
                    File.WriteAllText(Path.Combine(under, $"File{i:D5}.txt"), "new\n");
                }
                MainWindow window = Ui.OpenFolder(tree, languageServer: false);
                window.Perform(AppActions.Commit);
                CommitView commit = window.CommitView;
                Ui.Landed(() => commit.Rows.OfType<CommitGroup>().Any(g => g.Name == "Unversioned Files" && g.Rows.Count == files), $"the {files} new files under Unversioned Files");
                CommitGroup group = commit.Rows.OfType<CommitGroup>().Single(g => g.Name == "Unversioned Files");
                Assert.False(group.IsChosen);
                CheckBox box = Ui.Row(Find(commit, commit.Rows), group).GetVisualDescendants().OfType<CheckBox>().First(b => b.IsVisible);
                long counted = commit.RowsCounted;
                long before = GC.GetAllocatedBytesForCurrentThread();
                Ui.Click(box);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                counted = commit.RowsCounted - counted;
                Assert.True(group.IsChosen == true && commit.Chosen.Count == files, $"Ticking Unversioned Files chose {commit.Chosen.Count} of its {files} files");
                window.Close();
                return (counted, bytes);
            }
            finally
            {
                TestFolders.Delete(tree);
            }
        }
    }

    /// <summary>In a linked worktree, whose Git folder is under the main one's, a commit and a branch switched from outside
    /// the window (a terminal, an agent) are followed: the change bar and the Explorer's colour go, the Commit window empties
    /// and the branch widget names the new branch.</summary>
    private void A_commit_and_a_branch_switch_from_outside_a_linked_worktree_are_followed_by_the_bars_colours_commit_list_and_branch()
    {
        string worktrees = TestFolders.Create("git-worktree-");
        try
        {
            string tree = Path.Combine(worktrees, "feature");
            Git("worktree", "add", "-q", "-b", "feature", tree);
            FollowsGitFromOutside(PathNames.Canonical(tree), "Shop.cs", "feature");
        }
        finally
        {
            TestFolders.Delete(worktrees);
        }
    }

    /// <summary>In a folder opened below the repository's top, whose Git folder is outside it, a commit and a branch switched
    /// from outside the window are followed as they are at the top.</summary>
    private void A_commit_and_a_branch_switch_from_outside_a_folder_opened_below_the_repositorys_top_are_followed_by_the_bars_colours_commit_list_and_branch()
    {
        TestFolders.Write(folder, "src/Shop.cs", Original);
        Git("add", "-A");
        Git("commit", "-q", "-m", "Shop in src");
        FollowsGitFromOutside(PathNames.Canonical(Path.Combine(folder, "src")), "Shop.cs", "main");
    }

    /// <summary>In a reftable repository (Git 2.45's, whose refs are kept in tables, never in HEAD or refs' files), a commit
    /// and a branch switched from outside the window are followed, at the repository's top as below it, where its Git folder
    /// is outside the folder opened.</summary>
    private void A_commit_and_a_branch_switch_from_outside_a_reftable_repository_are_followed_by_the_bars_colours_commit_list_and_branch(bool below)
    {
        string repository = TestFolders.Create("git-reftable-");
        try
        {
            TestFolders.Write(repository, below ? "src/Shop.cs" : "Shop.cs", Original);
            Passenger.Core.ChildResult made = Ui.AwaitPumped(Waiting.Apart(() => Passenger.Core.ChildProcesses.RunAsync(
                Passenger.Core.ChildProcesses.Command("git", ["init", "-q", "-b", "main", "--ref-format=reftable"], repository), limit: TimeSpan.FromMinutes(1).Scaled())),
                "git to make a reftable repository", TimeSpan.FromMinutes(1).Scaled() + TestFolders.Spare);
            Assert.SkipUnless(made.Succeeded, $"The git here makes no reftable repository (Git 2.45 and later do): {made.Error}{made.Output}");
            TestGit.Run(repository, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
            TestGit.Run(repository, "config", "user.name", "Passenger Tests");
            TestGit.Run(repository, "config", "core.autocrlf", "false");
            TestGit.Run(repository, "add", "-A");
            TestGit.Run(repository, "commit", "-q", "-m", "First");
            string top = PathNames.Canonical(repository);
            FollowsGitFromOutside(below ? Path.Combine(top, "src") : top, "Shop.cs", "main");
        }
        finally
        {
            TestFolders.Delete(repository);
        }
    }

    /// <summary>Opens a folder, changes a file there and saves it, then commits it and switches to a new branch with git run
    /// in the folder, as from a terminal, waiting for the window to follow each.</summary>
    private static void FollowsGitFromOutside(string opened, string name, string branch)
    {
        MainWindow window = Ui.OpenFolder(opened, languageServer: false);
        window.OpenFile(Path.Combine(opened, name));
        Ui.Settle();
        Ui.Landed(() => window.Vcs.Branch == branch, $"the branch {branch}");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        window.Perform(AppActions.Save);
        window.Perform(AppActions.Commit);
        CommitView commit = window.CommitView;
        Ui.Landed(() => Marks(window).Count > 0 && commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == name), $"{name}'s change bar and its row in the Commit window");
        Passenger.App.Explorer.ExplorerNode row = window.ExplorerView.Tree.Rows.First(r => r.Name == name);
        Assert.Equal(ChangeKind.Modified, row.Change);
        TextBlock widget = window.FindControl<TextBlock>("BranchName")!;
        Assert.Equal(branch, widget.Text);

        TestGit.Run(opened, "commit", "-q", "-am", "Committed from outside");
        Ui.WaitFor(() => Marks(window).Count == 0 && !commit.Rows.OfType<CommitRow>().Any() && row.Change is null,
            () => $"After a commit from outside the window still shows {Marks(window).Count} change bars, the Commit window lists "
                + $"[{string.Join(", ", commit.Rows.OfType<CommitRow>().Select(r => r.Change.Name))}] and the Explorer colours {name} as {row.Change?.ToString() ?? "unchanged"}");
        TestGit.Run(opened, "switch", "-q", "-c", "switched");
        Ui.WaitFor(() => widget.Text == "switched", () => $"After a branch switched from outside the branch widget still says {widget.Text}");
        window.Close();
    }

    private void Rollback_lines_puts_the_change_under_the_caret_back_as_the_last_commit_has_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.GetLineStart(4), 0, "    // added\n")]);
        editor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("public ");
        Ui.Landed(() => Marks(window).Count == 2, "two change bars");
        Assert.Contains(Marks(window), m => m.Kind == ChangeMarkKind.Added && m.Line == 4 && m.Count == 1);

        editor.GoTo(new TextPosition(4, 2));
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => !Text(window).Contains("// added", StringComparison.Ordinal), "the added line gone");
        Assert.StartsWith("public class Shop", Text(window), StringComparison.Ordinal);
        editor.GoTo(new TextPosition(0, 3));
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => Text(window) == Original, "the file as committed");
        Ui.Landed(() => Marks(window).Count == 0, "the bars cleared");
        window.Close();
    }

    private void A_new_branch_from_the_toolbar_is_checked_out_and_going_back_reloads_the_open_file()
    {
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        window.ShowNewBranch();
        Ui.Settle();
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = "feature/sale";
        Task submitted = popup.SubmitAsync();
        Ui.Landed(() => submitted.IsCompleted && window.Vcs.Branch == "feature/sale", "the new branch checked out");
        Assert.False(window.IsPopupOpen);

        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        window.Vcs.ReloadOpenFiles();
        Ui.Landed(() => Text(window).Contains("count += 2", StringComparison.Ordinal), "the branch's text");
        Task<VcsOutcome> checkout = window.Vcs.CheckoutAsync("main");
        VcsOutcome checkedOut = Ui.Landed(checkout, "the checkout done");
        Assert.True(checkedOut.Succeeded, checkedOut.Message);
        Assert.Equal("main", window.Vcs.Branch);
        Assert.Equal(Original, Text(window));
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    /// <summary>A save writes the new text beside the file and moves it over the file: Git reading the folder in that moment
    /// (a read under way as Save is pressed) lists the file saved as changed, never the temporary file.</summary>
    private void A_file_saved_as_git_reads_the_folder_is_listed_as_changed_and_its_saves_temporary_file_never_is()
    {
        MainWindow window = Open();
        string saving = Path.Combine(folder, "Shop.cs" + FileText.SaveSuffix);
        List<string> listed = [];
        window.Vcs.Changed += (_, _) => listed.AddRange(window.Vcs.Changes.Select(c => c.Name));
        window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        _ = window.Vcs.RefreshAsync();
        window.Perform(AppActions.Save);

        // That read may find the temporary file; one made while it is there, as a save has it, finds it for certain.
        File.WriteAllText(saving, "sealed " + Original);
        _ = window.Vcs.RefreshAsync();
        Ui.Settle();
        Assert.Equal(["Shop.cs"], window.Vcs.Changes.Select(c => c.Name));
        Assert.DoesNotContain(Path.GetFileName(saving), listed);
        window.Close();
    }

    /// <summary>Rollback from the Commit window, the new file chosen by hand (unversioned files are not chosen at first),
    /// asks first, saying the new file is deleted, then puts back the changed file and deletes the new one, once another
    /// program reading them lets them go (Windows refuses git meanwhile).</summary>
    private void Rollback_puts_back_a_file_another_program_was_reading_and_deletes_a_new_one_it_held_once_they_are_let_go()
    {
        MainWindow window = Open();
        string shop = Path.Combine(folder, "Shop.cs");
        string added = Path.Combine(folder, "Prices.cs");
        File.WriteAllText(added, "class Prices { }\n");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        window.Perform(AppActions.Save);
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => window.Vcs.Changes.Count == 2, "a changed file and a new one listed");
        Assert.DoesNotContain(window.CommitView.Chosen, c => c.Kind == ChangeKind.Unversioned);
        string? asked = null;
        window.Confirm = (title, text, yes) =>
        {
            asked = text;
            return Task.FromResult(true);
        };
        foreach (CommitRow row in window.CommitView.Rows.OfType<CommitRow>())
        {
            row.IsChosen = true;
        }

        // Another program reads both until git has tried once, which Windows refuses (git deletes the old file first).
        FileStream reading = new(shop, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream holding = new(added, FileMode.Open, FileAccess.Read, FileShare.Read);
        EventHandler<Passenger.Core.ChildEnd> letGo = (_, end) =>
        {
            if (end.Command.Contains(" restore ", StringComparison.Ordinal))
            {
                reading.Dispose();
                holding.Dispose();
            }
        };
        Passenger.Core.ChildProcesses.Ended += letGo;
        try
        {
            RollbackChosen(window);
            AssertNothingChanged(window);
        }
        finally
        {
            Passenger.Core.ChildProcesses.Ended -= letGo;
            reading.Dispose();
            holding.Dispose();
        }
        Assert.Contains("deleted", asked, StringComparison.Ordinal);
        Assert.Equal(Original, File.ReadAllText(shop));
        Assert.False(File.Exists(added), "The new file stayed after the program holding it let go");
        Ui.Landed(() => Text(window) == Original, "the open file at the last commit's text");
        Assert.Equal("Rolled back 2 files", window.Status);
        window.Close();
    }

    /// <summary>Rollback of a changed file and an unversioned one puts the changed one back and moves the unversioned one to
    /// the Trash in a command of its own, as its question says first: one Undo, asked once, brings the unversioned file back
    /// out of the Trash, listed as unversioned again, the changed one staying as the last commit has it.</summary>
    private void Rollback_moves_an_unversioned_file_to_the_Trash_and_one_undo_brings_it_back()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        string added = Path.Combine(folder, "Prices.cs");
        File.WriteAllText(shop, "sealed " + Original);
        File.WriteAllText(added, "class Prices { }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Landed(() => window.Vcs.Changes.Count == 2, "a changed file and a new one listed");
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        window.ShowToolWindow(ToolWindowIds.Commit);
        Ui.Settle();
        foreach (CommitRow row in window.CommitView.Rows.OfType<CommitRow>())
        {
            row.IsChosen = true;
        }

        RollbackChosen(window);
        Ui.WaitFor(() => window.Status == "Rolled back 2 files" || window.Status?.Contains("could not", StringComparison.Ordinal) == true, () => $"the rollback: {window.Status}");
        Assert.True(asked.Count == 1 && asked[0].Contains("Unversioned files are deleted to the Trash, and Undo brings them back.", StringComparison.Ordinal),
            $"Rollback asked: {string.Join(" | ", asked)}");
        Assert.True(File.ReadAllText(shop) == Original && !File.Exists(added), $"The rollback left Shop.cs changed or Prices.cs there: {window.Status}");
        Assert.True(File.ReadAllText(Path.Combine(trash, "Prices.cs")) == "class Prices { }\n", "Rollback did not move Prices.cs to the Trash");

        asked.Clear();
        window.ShowToolWindow(ToolWindowIds.Explorer);
        window.ExplorerView.Reveal(PathNames.Canonical(shop));
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == PathNames.Canonical(shop)));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.Exists(added), () => $"one Undo to bring Prices.cs back: {window.Status}");
        Assert.True(asked.Count == 1 && asked[0].Contains("brings back Prices.cs", StringComparison.Ordinal), $"Undo of the rollback asked: {string.Join(" | ", asked)}");
        Assert.Equal("class Prices { }\n", File.ReadAllText(added));
        Assert.Equal(Original, File.ReadAllText(shop));
        Assert.Empty(Directory.EnumerateFileSystemEntries(trash));
        Task read = window.Vcs.RefreshAsync();
        Ui.Landed(() => read.IsCompleted, "Git read again");
        string listed = string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"));
        Assert.True(listed == "Prices.cs (Unversioned)", $"After Undo Git lists [{listed}]");
        window.Close();
    }

    /// <summary>Rollback of a new file another program added to Git (git add, or a merge's), which no commit holds, keeps
    /// it on disk as it is now, unversioned, as the question says first; the changed file beside it goes back.</summary>
    private void Rollback_keeps_a_new_file_another_program_added_to_git_on_disk_and_says_so_first()
    {
        MainWindow window = Open();
        string shop = Path.Combine(folder, "Shop.cs");
        string prices = Path.Combine(folder, "Prices.cs");
        File.WriteAllText(prices, "class Prices { }\n");
        Git("add", "Prices.cs");
        File.AppendAllText(prices, "// changed since the add\n");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        window.Perform(AppActions.Save);
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => window.Vcs.Changes.Count == 2 && window.Vcs.Changes.Any(c => c.Name == "Prices.cs" && c.Kind == ChangeKind.Added), "the added file and the changed one listed");
        string? asked = null;
        window.Confirm = (title, text, yes) =>
        {
            asked = text;
            return Task.FromResult(true);
        };

        RollbackChosen(window);
        Ui.Settle();
        Assert.True(asked is not null && asked.Contains("Files added to Git stay on disk", StringComparison.Ordinal) && !asked.Contains("deleted", StringComparison.Ordinal),
            $"Rollback asked \"{asked}\"");
        Assert.True(File.Exists(prices), $"Rollback deleted Prices.cs, which Git was given to add, though it asked \"{asked}\"");
        Assert.Equal("class Prices { }\n// changed since the add\n", File.ReadAllText(prices));
        Assert.Empty(Git("ls-files", "--", "Prices.cs").Trim());
        Assert.Equal(Original, File.ReadAllText(shop));
        string listed = string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"));
        Assert.True(listed == "Prices.cs (Unversioned)", $"After the rollback Git lists [{listed}]");
        Assert.Equal("Rolled back 2 files", window.Status);
        window.Close();
    }

    private void Show_diff_puts_the_last_commit_beside_the_open_file_and_its_arrow_puts_a_change_back()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.GetLineStart(4), 0, "    int total;\n")]);
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        window.Perform(AppActions.Save);
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => window.Vcs.Changes.Count == 1, "Shop.cs listed as changed");
        Passenger.App.Workspace.OpenDocument file = window.EditorArea.Active!;

        Task<IDiffHost?> opening = window.ShowDiffAsync(window.Vcs.Changes[0].Path);
        IDiffHost diff = Ui.Landed(opening, "the diff open")!;
        Assert.True(OpenDiffs.Shown(window) == diff, "Show Diff opened the diff behind what the person sees");
        DiffView view = OpenDiffs.ViewOf(diff);
        Assert.Equal(Original, view.Left.Document.Snapshot.ToString());
        Assert.Same(file.Text, view.Right.Document);
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(4, 0, 4, 1)], view.Hunks);
        // Below the added line, the left side stands one line higher.
        Assert.Equal(5, view.MapLine(6, toLeft: true));

        view.Revert(view.Hunks[1]);
        Ui.Settle();
        Assert.DoesNotContain("int total;", file.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal([new DiffHunk(2, 1, 2, 1)], view.Hunks);
        Assert.True(file.IsModified, "A change put back from the diff left the file it edits unchanged");
        diff.Close();
        window.Close();
    }

    /// <summary>The IDE's keys work in a diff's window as in the main one, the diff keeping its own: Find Action opens over
    /// it and Escape closes that first, the keys back on the file's side; F7 is still the diff's next difference; an
    /// editor's key goes to the side with the keys, not to the main window's editor; and Escape then closes the window.
    /// Diffs are set to open in a window of their own, the window being what this holds of.</summary>
    private void The_IDEs_keys_work_in_a_diffs_window_on_the_side_with_them_and_Escape_still_closes_it()
    {
        MainWindow window = Open();
        window.ApplySettings(window.Settings with { OpenDiffsIn = Passenger.App.Settings.DiffPlace.Window });
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        window.Perform(AppActions.Save);
        editor.GoTo(new TextPosition(0, 0));
        Task<IDiffHost?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        DiffWindow diff = Assert.IsType<DiffWindow>(Ui.Landed(opening, "the diff open"));
        DiffView view = diff.View;
        Ui.Landed(() => view.Right.IsFocused, "the diff's side of the file to have the keys");

        Ui.Run(diff, AppActions.FindAction);
        Control popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        Assert.True(diff.GetVisualDescendants().Contains(popup), "Find Action, pressed in the diff's window, opened over the main window");
        Ui.Press(diff, "ESCAPE");
        Assert.False(window.IsPopupOpen, "Escape left Find Action open over the diff");
        Assert.True(diff.IsVisible, "Escape closed the diff along with Find Action over it");
        Assert.True(view.Right.IsFocused, "Find Action, closed, left the diff's keys away from the file's side");

        Ui.Press(diff, "F7");
        Assert.Equal(2, view.Right.CaretPosition.Line);

        Ui.Run(diff, EditorActions.Duplicate);
        Assert.Equal("class Shop\n{\n    long int count;\n    long int count;\n\n    void Add() { count++; }\n}\n", Text(window));
        Assert.Equal(new TextPosition(0, 0), editor.CaretPosition);

        Ui.Press(diff, "ESCAPE");
        Assert.False(diff.IsVisible, "Escape, with nothing over the diff, left its window open");
        window.Close();
    }

    /// <summary>Jump to Source goes from a diff to its file: F4 on the file's side opens it in the main window at the caret,
    /// the file shown in front of the diff; F4 on the last commit's side lands on the line that one stands at now, past
    /// the lines put in above it; and the diff's toolbar names it with its key.</summary>
    private void F4_in_a_diff_opens_its_file_at_the_carets_line_from_either_side()
    {
        TestFolders.Write(folder, "Notes.txt", "notes\n");
        TestFolders.Write(folder, "Shop.cs", "// One\n// Two\n" + Original);
        MainWindow window = Open();
        string shop = window.EditorArea.Active!.Path;
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Ui.Settle();

        IDiffHost diff = Ui.Landed(window.ShowDiffAsync(shop), "the diff open")!;
        DiffView view = OpenDiffs.ViewOf(diff);
        Ui.Landed(() => view.Right.IsFocused, "the diff's side of the file to have the keys");
        Button jump = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == AppActions.DiffJumpToSource);
        Assert.Equal("Jump to Source (F4)", ToolTip.GetTip(jump));
        view.Right.GoTo(new TextPosition(4, 4));
        Ui.Press(OpenDiffs.KeysOf(diff), "F4");
        Assert.True(OpenDiffs.Shown(window) is null && window.Frontmost == window, "Jump to Source left the diff in front of the file it opened");
        Assert.True(window.EditorArea.Active?.Path == shop, $"Jump to Source opened {window.EditorArea.Active?.Path}, not the diff's file");
        Assert.Equal(new TextPosition(4, 4), window.EditorArea.CodeEditor.CaretPosition);

        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Ui.Settle();
        diff = Ui.Landed(window.ShowDiffAsync(shop), "the diff open again")!;
        view = OpenDiffs.ViewOf(diff);
        view.Left.Focus();
        view.Left.GoTo(new TextPosition(2, 4));
        Ui.Press(OpenDiffs.KeysOf(diff), "F4");
        Assert.True(OpenDiffs.Shown(window) is null && window.Frontmost == window, "Jump to Source from the last commit's side left the diff in front of the file it opened");
        Assert.True(window.EditorArea.Active?.Path == shop && window.EditorArea.CodeEditor.CaretPosition.Line == 4,
            $"F4 on the last commit's line 3 went to line {window.EditorArea.CodeEditor.CaretPosition.Line + 1} of {window.EditorArea.Active?.Path}, not to line 5 of Shop.cs, where it stands now");
        window.Close();
    }

    /// <summary>A diff's Next Difference is F7 everywhere and F8 too off the Mac, as Rider's Visual Studio keymap binds them
    /// there, Shift with either going back; the Mac's keymap leaves F8 to the IDE.</summary>
    private void A_diffs_next_and_previous_difference_are_F7_and_F8_off_the_Mac_and_F7_alone_on_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        editor.GoTo(new TextPosition(4, 4));
        window.KeyTextInput("public ");
        window.Perform(AppActions.Save);
        editor.GoTo(new TextPosition(0, 0));
        Task<IDiffHost?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        IDiffHost diff = Ui.Landed(opening, "the diff open")!;
        DiffView view = OpenDiffs.ViewOf(diff);
        TopLevel keys = OpenDiffs.KeysOf(diff);
        Ui.Landed(() => view.Right.IsFocused, "the diff's side of the file to have the keys");
        Assert.Equal([2, 4], view.Hunks.Select(h => h.NewStart));

        Ui.Press(keys, "F7");
        Assert.Equal(2, view.Right.CaretPosition.Line);
        Ui.Press(keys, "F8");
        if (OperatingSystem.IsMacOS())
        {
            Assert.True(view.Right.CaretPosition.Line == 2, "F8 stepped in a diff on the Mac, whose keymap leaves it to the IDE");
        }
        else
        {
            Assert.True(view.Right.CaretPosition.Line == 4, $"F8 left a diff's caret on line {view.Right.CaretPosition.Line + 1}, not at the next difference");
            Ui.Press(keys, "shift F8");
            Assert.True(view.Right.CaretPosition.Line == 2, $"Shift+F8 left a diff's caret on line {view.Right.CaretPosition.Line + 1}, not at the previous difference");
        }
        diff.Close();
        window.Close();
    }

    private void A_closed_diff_is_let_go_by_the_open_file_it_showed()
    {
        MainWindow window = Open();
        WeakReference<DiffView> closed = DiffAndClose(window);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(closed.TryGetTarget(out _), "The closed diff (its editors and the last commit's text) is still alive, held by the open file it showed");
        window.Close();
    }

    /// <summary>A diff's window outlives its file's tab, holding the file open while it edits it: diffs are set to open in
    /// a window of their own, as a tab closes with its file's last tab.</summary>
    private void An_edit_in_a_diffs_window_after_the_files_tab_closed_is_saved_with_the_others_and_as_the_window_closes()
    {
        MainWindow window = Open();
        window.ApplySettings(window.Settings with { OpenDiffsIn = Passenger.App.Settings.DiffPlace.Window });
        string shop = window.EditorArea.Active!.Path;
        Task<IDiffHost?> opening = window.ShowDiffAsync(shop);
        DiffWindow diff = Assert.IsType<DiffWindow>(Ui.Landed(opening, "the diff open"));

        window.Perform(AppActions.CloseTab);
        Ui.Settle();
        Assert.Null(window.EditorArea.Active);
        CodeEditor right = diff.View.Right;
        right.Focus();
        right.GoTo(new TextPosition(4, 4));
        diff.KeyTextInput("public ");
        Ui.Settle();
        Assert.Contains("    public void Add()", right.Document.Snapshot.ToString(), StringComparison.Ordinal);

        window.Perform(AppActions.SaveAll);
        Assert.Contains("    public void Add()", File.ReadAllText(shop), StringComparison.Ordinal);
        right.GoTo(new TextPosition(2, 4));
        diff.KeyTextInput("long ");
        diff.Close();
        Ui.Settle();
        Assert.Contains("    long int count;", File.ReadAllText(shop), StringComparison.Ordinal);
        Assert.DoesNotContain(window.EditorArea.Documents, d => d.Path == shop);
        window.Close();
    }

    private void A_click_on_a_change_bar_shows_what_the_lines_were_and_rolls_them_back()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(4, 4));
        window.KeyTextInput("public ");
        Ui.Landed(() => Marks(window).Count == 1, "a change bar on line 5");

        Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Changes, 4), settle: false);
        Ui.Landed(() => window.Popup is ChangePopup, "the change's popup");
        ChangePopup popup = (ChangePopup)window.Popup!;
        Assert.Equal("    void Add() { count++; }", popup.Before);
        // The bar's click is the change's, not a breakpoint's.
        Assert.Empty(window.Debugger.Breakpoints.In(window.EditorArea.Active!.Path));

        Avalonia.Controls.Button rollback = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(popup)
            .OfType<Avalonia.Controls.Button>()
            .First(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Rollback");
        Ui.Click(rollback, settle: false);
        Ui.Landed(() => Text(window) == Original, "the line as committed");
        Assert.False(window.IsPopupOpen);
        window.Close();
    }

    private void Typing_in_a_20000_line_file_rewritten_since_the_last_commit_moves_its_bars_and_a_bar_opens_its_change_at_once()
    {
        // A generated file of 20,000 lines, every twelfth line of it generated anew since the last commit: 1,667 changes.
        string generated = Path.Combine(folder, "Generated.txt");
        string[] lines = [.. Enumerable.Range(0, 20000).Select(i => $"    int value{i} = {i};")];
        File.WriteAllText(generated, string.Join("\n", lines) + "\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Generated");
        File.WriteAllText(generated, string.Join("\n", lines.Select((l, i) => i % 12 == 5 ? l.Replace(";", " + 1;", StringComparison.Ordinal) : l)) + "\n");
        MainWindow window = Open();
        window.OpenFile(generated);
        CodeEditor editor = window.EditorArea.CodeEditor;
        VcsController vcs = window.Vcs;
        Ui.Landed(() => Marks(window).Count == 1667, "a bar for each line generated anew");

        // Typed in, the file is compared again with the last commit off the window's thread, in a few copies' memory.
        editor.GoTo(new TextPosition(0, 4));
        window.KeyTextInput("const ");
        Ui.Landed(() => Marks(window).Count == 1668, "a bar for the first line typed in");
        editor.GoTo(new TextPosition(8, 4));
        window.KeyTextInput("const ");
        Ui.Landed(() => Marks(window).Count == 1669, "a bar for the second line typed in");
        Assert.Equal([new ChangeMark(0, 1, ChangeMarkKind.Modified), new ChangeMark(5, 1, ChangeMarkKind.Modified), new ChangeMark(8, 1, ChangeMarkKind.Modified)], Marks(window).Take(3));
        Assert.True(vcs.LargestComparison < 16_000_000, $"Comparing the file with the last commit took {vcs.LargestComparison / 1e6:F0} MB");

        // A click on a bar opens what the lines were from the comparison its bars came from: nothing is compared again.
        int comparisons = vcs.Comparisons;
        Ui.Settle();
        Avalonia.Point at = editor.TranslatePoint(editor.GutterColumnPoint(editor.Gutter!.Changes, 8), window)!.Value;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        // Pressed raw: Ui.Click's look at the frame before it would count among what the click allocated.
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        long clicked = GC.GetAllocatedBytesForCurrentThread() - allocated;
        ChangePopup popup = Assert.IsType<ChangePopup>(window.Popup);
        Assert.Equal("    int value8 = 8;", popup.Before);
        Assert.True(clicked < 4_000_000, $"Opening the bar's popup took {clicked / 1e6:F0} MB on the window's thread");
        Assert.True(vcs.Comparisons == comparisons, $"Opening the bar's popup compared the file {vcs.Comparisons - comparisons} times again");

        Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(popup).OfType<Avalonia.Controls.Button>()
            .First(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Show Diff"), settle: false);
        IDiffHost diff = OpenDiffs.Landed(window, "the diff from the change bar's popup");
        Assert.Equal(1669, OpenDiffs.ViewOf(diff).Hunks.Count);
        Assert.True(vcs.Comparisons == comparisons, $"Show Diff compared the file {vcs.Comparisons - comparisons} times again");
        diff.Close();

        editor.GoTo(new TextPosition(0, 0));
        window.Perform(AppActions.RollbackLines);
        Assert.StartsWith("    int value0 = 0;\n    int value1 = 1;\n", Text(window), StringComparison.Ordinal);
        Assert.True(vcs.Comparisons == comparisons, $"Rollback Lines compared the file {vcs.Comparisons - comparisons} times again");
        Ui.Landed(() => Marks(window).Count == 1668, "the rolled back line's bar gone");
        window.Close();
    }

    private void Blank_lines_added_at_the_end_of_a_file_show_an_added_bar_and_a_difference_and_roll_back()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;

        // Two blank lines after the last, which git diff lists as added.
        editor.GoTo(new TextPosition(6, 0));
        Ui.Press(window, "ENTER");
        Ui.Press(window, "ENTER");
        Assert.Equal(Original + "\n\n", Text(window));
        // The first Enter's bar can land before the second's: the wait is for the bar over both.
        Ui.Landed(() => Marks(window).SequenceEqual([new ChangeMark(6, 2, ChangeMarkKind.Added)]), "a bar over both blank lines");
        Task<IDiffHost?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        IDiffHost diff = Ui.Landed(opening, "the diff open")!;
        Assert.Equal([new DiffHunk(6, 0, 6, 2)], OpenDiffs.ViewOf(diff).Hunks);
        diff.Close();

        editor.GoTo(new TextPosition(7, 0));
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => Text(window) == Original, "the blank lines gone");
        Ui.Landed(() => Marks(window).Count == 0, "the bar gone");
        window.Close();
    }

    private void The_log_lays_out_a_merge_in_two_lanes_and_shows_what_a_commit_did_to_a_file()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        Git("switch", "-q", "-c", "side");
        File.WriteAllText(Path.Combine(folder, "Side.cs"), "class Side { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Side");
        Git("switch", "-q", "main");
        File.WriteAllText(shop, Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        Git("merge", "-q", "--no-ff", "-m", "Merge side", "side");

        MainWindow window = Open();
        GitLogView log = window.GitLog;
        window.Perform(AppActions.ShowGit);
        Ui.Landed(() => log.Rows.Count == 4, "the four commits");
        Assert.Equal(Git("log", "--all", "--topo-order", "--format=%H").Split('\n', StringSplitOptions.RemoveEmptyEntries), log.Rows.Select(r => r.Commit.Hash));
        GraphRow merge = log.Rows[0];
        Assert.Equal("Merge side", merge.Commit.Subject);
        Assert.Contains("main", merge.Commit.Refs);
        Assert.Equal(2, merge.Below.Count(l => l is not null));
        GraphRow first = log.Rows[^1];
        Assert.Equal(2, first.Above.Count(l => l == first.Commit.Hash));
        Assert.Empty(first.Below);

        CommitInfo twice = log.Rows.Select(r => r.Commit).First(c => c.Subject == "Twice");
        log.Select(twice);
        Task reload = log.LoadAsync();
        Ui.Landed(() => reload.IsCompleted, "the log read again");
        Assert.Equal(twice.Hash, log.Selected?.Hash);
        Ui.Landed(() => log.Files.Count == 1, "the commit's file");
        FileChange changed = Assert.Single(log.Files);
        Assert.Equal((PathNames.Canonical(shop), ChangeKind.Modified), (changed.Path, changed.Kind));
        Assert.Contains("Twice", log.Details, StringComparison.Ordinal);
        Task<IDiffHost?> opening = window.ShowCommitDiffAsync(twice, changed);
        IDiffHost shown = Ui.Landed(opening, "the commit's diff")!;
        DiffView diff = OpenDiffs.ViewOf(shown);
        Assert.Equal(Original, diff.Left.Document.Snapshot.ToString());
        Assert.Contains("count += 2", diff.Right.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Single(diff.Hunks);
        Assert.False(diff.IsEditable);
        shown.Close();

        log.FilterText = twice.Hash[..7];
        Ui.Settle();
        Assert.Equal(twice.Hash, Assert.Single(log.Rows).Commit.Hash);
        window.Close();
    }

    private void Update_project_brings_in_the_upstreams_commit_and_the_open_file_shows_it()
    {
        string clone = TestFolders.Create("git-clone-");
        Git("clone", "-q", "-c", "core.autocrlf=false", folder, clone);
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");

        MainWindow window = Ui.OpenFolder(clone);
        window.OpenFile(Path.Combine(clone, "Shop.cs"));
        Ui.Settle();
        Assert.Equal("main", window.Vcs.Branch);
        window.Perform(AppActions.UpdateProject);
        Ui.Landed(() => Text(window).Contains("long count", StringComparison.Ordinal), "the upstream's change in the open file");
        Ui.Landed(() => window.Status?.StartsWith("Updated", StringComparison.Ordinal) == true, "the update's end in the status bar");
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    /// <summary>Update Project whose remote stops answering is ended by Stop, git and all it started with it, and a commit
    /// asked for then runs: the remote holds no change to the tree for good.</summary>
    private void Stop_ends_an_update_whose_remote_stalls_and_a_commit_runs_after_it()
    {
        string clone = TestFolders.Create("git-clone-");
        Git("clone", "-q", "-c", "core.autocrlf=false", folder, clone);
        TestGit.Run(clone, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
        TestGit.Run(clone, "config", "user.name", "Passenger Tests");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");
        // The remote's side of the fetch says it is there, then stalls until go, a minute at most.
        string fetching = Path.Combine(clone, ".git", "fetching");
        string go = Path.Combine(clone, ".git", "go");
        TestGit.Run(clone, "config", "remote.origin.uploadpack",
            $"touch '{Unix(fetching)}'; i=0; while [ ! -f '{Unix(go)}' ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i + 1)); done; git-upload-pack");
        string before = TestGit.Run(clone, "rev-parse", "origin/main").Trim();

        MainWindow window = Ui.OpenFolder(clone, languageServer: false);
        Ui.Settle();
        try
        {
            Assert.True(window.Perform(AppActions.UpdateProject), window.Status);
            Ui.PumpUntil(() => File.Exists(fetching), "the update's fetch to reach the remote");
            Assert.True(window.Actions.Work.Of(AppActions.UpdateProject)?.Stoppable == true, "Update Project's work is not one Stop ends");
            Assert.True(window.Perform(AppActions.Stop), "Stop was not offered while Update Project waited on the remote");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.UpdateProject) is null && !window.Vcs.IsBusy, "the update to end once stopped");
            Assert.Equal("Update stopped", window.Status);
            Assert.Equal(before, TestGit.Run(clone, "rev-parse", "origin/main").Trim());

            File.WriteAllText(Path.Combine(clone, "Shop.cs"), Original.Replace("count++", "count += 1", StringComparison.Ordinal));
            Task read = window.Vcs.RefreshAsync();
            Ui.Landed(() => read.IsCompleted && window.Vcs.Changes.Count == 1, "Shop.cs listed as changed");
            CommitResult made = Ui.Landed(window.Vcs.CommitAsync([.. window.Vcs.Changes], "After the stop", amend: false, push: false), "the commit after the stop");
            Assert.True(made.Commit.Succeeded, made.Commit.Message);
            Assert.Equal("After the stop", TestGit.Run(clone, "log", "-1", "--format=%s").Trim());
        }
        finally
        {
            File.WriteAllText(go, string.Empty);
        }
        window.Close();
    }

    /// <summary>Update Project of a branch with commits of its own and the upstream's, where git's config says neither merge
    /// nor rebase, merges them, saying so, and an uncommitted edit to a file the upstream changed is set aside and put back.</summary>
    private void Update_project_merges_a_diverged_branch_where_git_is_not_told_how_keeping_an_uncommitted_edit()
    {
        string clone = Diverged();
        File.WriteAllText(Path.Combine(clone, "Notes.txt"), Notes.Replace("ten", "ten, edited here", StringComparison.Ordinal));

        MainWindow window = Ui.OpenFolder(clone, languageServer: false);
        Ui.Settle();
        window.Perform(AppActions.UpdateProject);
        Ui.Landed(() => window.Status?.StartsWith("Update", StringComparison.Ordinal) == true && window.Actions.Work.Of(AppActions.UpdateProject) is null, "the update's end in the status bar");
        Assert.Equal("Updated main: merged 1 commit from origin/main, as pull.rebase is not set; local changes set aside and put back", window.Status);
        Assert.Equal("0", TestGit.Run(clone, "rev-list", "--count", "HEAD..origin/main").Trim());
        Assert.Equal(3, TestGit.Run(clone, "rev-list", "--parents", "-1", "HEAD").Trim().Split(' ').Length);
        Assert.Equal(Notes.Replace("one", "one, upstream", StringComparison.Ordinal).Replace("ten", "ten, edited here", StringComparison.Ordinal),
            File.ReadAllText(Path.Combine(clone, "Notes.txt")));
        Assert.Equal(" M Notes.txt", TestGit.Run(clone, "status", "--porcelain").TrimEnd());
        Assert.True(File.Exists(Path.Combine(clone, "Local.cs")), "The branch's own commit was lost");
        Assert.Empty(TestGit.Run(clone, "stash", "list").Trim());
        window.Close();
    }

    /// <summary>Update Project where pull.rebase is true rebases the branch's own commit onto the upstream's with an
    /// uncommitted edit to a file the upstream did not change, which it sets aside and puts back.</summary>
    private void Update_project_with_pull_rebase_rebases_onto_the_upstream_keeping_an_unrelated_uncommitted_edit()
    {
        string clone = Diverged();
        TestGit.Run(clone, "config", "pull.rebase", "true");
        File.WriteAllText(Path.Combine(clone, "Shop.cs"), Original.Replace("count++", "count += 1", StringComparison.Ordinal));

        MainWindow window = Ui.OpenFolder(clone, languageServer: false);
        Ui.Settle();
        window.Perform(AppActions.UpdateProject);
        Ui.Landed(() => window.Status?.StartsWith("Update", StringComparison.Ordinal) == true && window.Actions.Work.Of(AppActions.UpdateProject) is null, "the update's end in the status bar");
        Assert.Equal("Updated main: rebased onto 1 commit from origin/main; local changes set aside and put back", window.Status);
        Assert.Equal(TestGit.Run(clone, "rev-parse", "origin/main").Trim(), TestGit.Run(clone, "rev-parse", "HEAD~1").Trim());
        Assert.Equal("Local", TestGit.Run(clone, "log", "-1", "--format=%s").Trim());
        Assert.Equal(Original.Replace("count++", "count += 1", StringComparison.Ordinal), File.ReadAllText(Path.Combine(clone, "Shop.cs")));
        Assert.Equal(Notes.Replace("one", "one, upstream", StringComparison.Ordinal), File.ReadAllText(Path.Combine(clone, "Notes.txt")));
        Assert.Equal(" M Shop.cs", TestGit.Run(clone, "status", "--porcelain").TrimEnd());
        window.Close();
    }

    private const string Notes = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\n";

    /// <summary>A clone of the repository with Notes.txt, an author of its own and a commit of its own (Local.cs), and a
    /// commit to Notes.txt's first line in the repository it cloned that it has not fetched.</summary>
    private string Diverged()
    {
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), Notes);
        Git("add", "Notes.txt");
        Git("commit", "-q", "-m", "Notes");
        string clone = TestFolders.Create("git-clone-");
        Git("clone", "-q", "-c", "core.autocrlf=false", folder, clone);
        TestGit.Run(clone, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
        TestGit.Run(clone, "config", "user.name", "Passenger Tests");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), Notes.Replace("one", "one, upstream", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Upstream");
        File.WriteAllText(Path.Combine(clone, "Local.cs"), "class Local { }\n");
        TestGit.Run(clone, "add", "Local.cs");
        TestGit.Run(clone, "commit", "-q", "-m", "Local");
        return clone;
    }

    /// <summary>Push to a remote that asks for a login asks the person in the window (git's askpass helper answering over
    /// the window's own socket): cancelled, the push fails saying so and the remote takes nothing; signed in, the push goes,
    /// and the password is in no log.</summary>
    private void Push_to_a_remote_that_asks_for_a_login_asks_the_person_and_goes_once_they_sign_in()
    {
        string served = TestFolders.Create("git-served-");
        using StandInGitRemote remote = new(served);
        TestGit.Run(served, "init", "-q", "--bare", "repo.git");
        Git("remote", "add", "origin", remote.Url);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.Settle();

        Ui.Press(PushAsked(window), "ENTER");
        Ui.ClickButton(LoginAsked(window, remote), "Cancel");
        Ui.Landed(() => window.Actions.Work.Of(AppActions.Push) is null, "the push's end once its login was cancelled");
        Assert.Equal($"main was not pushed to origin/main: the login to {remote.Place} was cancelled", window.Status);
        Assert.Equal(0, remote.SignedIn);

        Ui.ClickButton(PushAsked(window), "Push");
        GitQuestionDialog asked = LoginAsked(window, remote);
        asked.KeyTextInput(StandInGitRemote.User);
        Ui.Press(asked, "TAB");
        asked.KeyTextInput(StandInGitRemote.Password);
        Ui.Press(asked, "ENTER");
        Ui.Landed(() => window.Status == "Pushed main to origin/main, which it tracks from now on", "the push once signed in");
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), TestGit.Run(remote.Repository, "rev-parse", "main").Trim());
        Assert.True(remote.SignedIn > 0, "The remote heard no request with the login typed");
        Assert.False(Ui.IdeLog().Contains(StandInGitRemote.Password, StringComparison.Ordinal), "The password typed for git is in the IDE's log");
        window.Close();
    }

    /// <summary>The window's question for the login a remote asks for, once it shows, held to the dialogs' rule.</summary>
    private static GitQuestionDialog LoginAsked(MainWindow window, StandInGitRemote remote)
    {
        Ui.PumpUntil(() => window.GitQuestion is { IsVisible: true }, $"the window to ask for the login {remote.Url} asks for");
        GitQuestionDialog asked = DialogButtonsTests.Check(window.GitQuestion!);
        Assert.Equal($"Sign in to {remote.Place}", asked.Title);
        return asked;
    }

    /// <summary>A branch rewritten since it was pushed (amended, as a rebase rewrites it) is refused a plain Push, the Push
    /// dialog listing what it sends and saying so; Force Push asks first, leaving the remote as it was when declined, and
    /// replaces the remote's branch once agreed, with a lease: a commit someone else pushed there since it was last fetched
    /// is never replaced. Where the remote's branch has nothing the branch lacks, Force Push is greyed saying so.</summary>
    private void Force_push_replaces_a_rewritten_branch_on_the_remote_once_agreed_but_never_what_someone_pushed_since()
    {
        string served = TestFolders.Create("git-served-");
        TestGit.Run(served, "init", "-q", "--bare");
        Git("remote", "add", "origin", served);
        Git("push", "-q", "-u", "origin", "main");
        string pushed = Git("rev-parse", "HEAD").Trim();
        Git("commit", "-q", "--amend", "-m", "Rewritten");
        MainWindow window = Open();
        List<string> asked = [];
        bool agree = false;
        window.Confirm = (title, _, yes) =>
        {
            asked.Add($"{title}: {yes}");
            return Task.FromResult(agree);
        };

        PushDialog refused = PushAsked(window);
        Assert.Equal(["Rewritten"], refused.Sends.Select(c => c.Subject));
        Assert.Equal(
            [
                "Sends 1 commit to origin/main, as last fetched.",
                "origin/main has 1 commit main lacks, as after a rebase or an amend: git refuses Push until it is brought in (merge or rebase onto origin/main), "
                    + "and Force Push replaces it, only while origin/main stands as last fetched.",
                "main keeps tracking origin/main.",
            ], refused.WhatItDoes);
        Assert.True(refused.ForceButton.IsEnabled, "Force Push is greyed where origin/main has a commit main lacks");
        Ui.ClickButton(refused, "Push");
        Ui.Landed(() => window.Status?.StartsWith("main was not pushed to origin/main", StringComparison.Ordinal) == true, "the plain push of a rewritten branch refused");
        Assert.Equal(pushed, TestGit.Run(served, "rev-parse", "main").Trim());

        Ui.ClickButton(PushAsked(window), "Force Push");
        Ui.Settle();
        Assert.Equal(["Force Push: Force Push"], asked);
        Assert.Equal(pushed, TestGit.Run(served, "rev-parse", "main").Trim());

        agree = true;
        Ui.ClickButton(PushAsked(window), "Force Push");
        Ui.Landed(() => window.Status == "Force pushed main to origin/main", "the force push once agreed");
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), TestGit.Run(served, "rev-parse", "main").Trim());
        PushDialog even = PushAsked(window);
        Assert.False(even.ForceButton.IsEnabled, "Force Push is offered where origin/main has nothing main lacks");
        Assert.Equal("origin/main has no commit main lacks, as last fetched: Push sends it", ToolTip.GetTip(even.ForceButton));
        Ui.ClickButton(even, "Cancel");

        string elsewhere = TestFolders.Create("git-elsewhere-");
        TestGit.Run(elsewhere, "clone", "-q", "-b", "main", served, ".");
        TestGit.Run(elsewhere, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
        TestGit.Run(elsewhere, "config", "user.name", "Passenger Tests");
        TestGit.Run(elsewhere, "commit", "-q", "--allow-empty", "-m", "Theirs");
        TestGit.Run(elsewhere, "push", "-q", "origin", "main");
        string theirs = TestGit.Run(elsewhere, "rev-parse", "HEAD").Trim();
        Git("commit", "-q", "--amend", "-m", "Rewritten again");
        Ui.ClickButton(PushAsked(window), "Force Push");
        Ui.Landed(() => window.Status?.StartsWith("main was not force pushed to origin/main", StringComparison.Ordinal) == true, "the force push refused by its lease");
        Assert.Equal(theirs, TestGit.Run(served, "rev-parse", "main").Trim());
        window.Close();
    }

    /// <summary>The Push dialog Push… opened, once it shows, held to the dialogs' rule.</summary>
    private static PushDialog PushAsked(MainWindow window)
    {
        Assert.True(window.Perform(AppActions.Push), window.Status);
        Ui.PumpUntil(() => window.PushWindow is { IsVisible: true }, "the Push dialog to show");
        PushDialog asked = DialogButtonsTests.Check(window.PushWindow!);
        Assert.Equal($"Push {window.Vcs.Branch}", asked.Title);
        return asked;
    }

    /// <summary>Push of a branch that tracks none goes to the repository's only remote whatever its name, the branch tracking
    /// it there from then on; with several remotes the Push dialog asks which, and pushes to the one chosen.</summary>
    private void Push_of_a_new_branch_goes_to_the_only_remote_whatever_its_name_and_asks_which_of_several()
    {
        string upstream = TestFolders.Create("git-upstream-");
        string other = TestFolders.Create("git-other-");
        TestGit.Run(upstream, "init", "-q", "--bare");
        TestGit.Run(other, "init", "-q", "--bare");
        Git("remote", "add", "upstream", upstream);
        Git("switch", "-q", "-c", "feature");
        MainWindow window = Open();
        Assert.Equal("feature", window.Vcs.Branch);

        PushDialog first = PushAsked(window);
        Assert.Equal("upstream", first.RemoteBox.SelectedItem);
        Assert.Equal(Git("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries), first.Sends.Select(c => c.Subject));
        Assert.False(first.ForceButton.IsEnabled, "Force Push is offered where upstream has no branch feature to replace");
        Ui.ClickButton(first, "Push");
        Ui.Landed(() => window.Status == "Pushed feature to upstream/feature, which it tracks from now on", "the push to the only remote, upstream");
        Assert.Equal(Git("rev-parse", "feature").Trim(), TestGit.Run(upstream, "rev-parse", "feature").Trim());
        Assert.Equal("upstream/feature", Git("rev-parse", "--abbrev-ref", "feature@{upstream}").Trim());

        Git("remote", "add", "other", other);
        Git("switch", "-q", "-c", "second");
        Ui.WaitFor(() => window.Vcs.Branch == "second", "the window to follow the checkout of second");
        PushDialog choosing = PushAsked(window);
        Assert.Equal(["other", "upstream"], choosing.RemoteBox.Items);
        Assert.Equal("other", choosing.RemoteBox.SelectedItem);
        choosing.RemoteBox.SelectedItem = "upstream";
        Ui.Landed(() => choosing.WhatItDoes[0] == "Makes the branch second on upstream at second's commit, which upstream has already.", "what a push to upstream does");
        choosing.RemoteBox.SelectedItem = "other";
        Ui.Landed(() => choosing.Sends.Count > 0, "what a push to other sends");
        Ui.ClickButton(choosing, "Push");
        Ui.Landed(() => window.Status == "Pushed second to other/second, which it tracks from now on", "the push to the remote chosen, other");
        Assert.Equal(Git("rev-parse", "second").Trim(), TestGit.Run(other, "rev-parse", "second").Trim());
        Assert.Equal("other/second", Git("rev-parse", "--abbrev-ref", "second@{upstream}").Trim());
        window.Close();
    }

    /// <summary>Manage Remotes… adds a remote and fetches it, Push to… sends a branch that tracks origin to it, saying first
    /// which commits it sends and that the branch keeps tracking origin, Edit Remote… renames it and gives it another URL,
    /// and Remove Remote… asks first, naming the branch that goes: each as its dialog's preview said, in its words and git's.
    /// A URL holding a token shows masked in the list, the preview, git's words and the status bar, and an edit leaving its
    /// masked box alone keeps the URL whole; a remote never fetched is listed in the branches panel with its menu.</summary>
    private void Remotes_are_added_pushed_to_renamed_repointed_and_removed_as_their_dialogs_said_a_token_in_a_url_masked()
    {
        string origin = TestFolders.Create("git-origin-");
        string backup = TestFolders.Create("git-backup-");
        string moved = TestFolders.Create("git-moved-");
        foreach (string bare in (string[])[origin, backup, moved])
        {
            TestGit.Run(bare, "init", "-q", "--bare", "-b", "main");
        }
        Git("remote", "add", "origin", origin);
        Git("push", "-q", "-u", "origin", "main");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        string first = GitHashes.Short(Git("rev-parse", "HEAD~1").Trim());
        string twice = GitHashes.Short(Git("rev-parse", "HEAD").Trim());
        MainWindow window = Open();
        List<string> said = [];
        using IDisposable following = Ui.FollowStatus(window, said);

        Assert.True(window.Perform(AppActions.ManageRemotes), window.Status);
        Ui.WaitFor(() => window.RemotesWindow is { IsVisible: true }, "the Remotes dialog");
        RemotesDialog remotes = DialogButtonsTests.Check(window.RemotesWindow!);
        Ui.Landed(() => remotes.Rows.Count == 1, "the remotes listed");
        Assert.Equal((string[])["origin"], remotes.Rows.Select(r => r.Name));
        Ui.Click(remotes.AddButton);
        Ui.WaitFor(() => window.RemoteWindow is { IsVisible: true }, "the Add Remote dialog");
        RemoteDialog adding = DialogButtonsTests.Check(window.RemoteWindow!);
        Assert.Equal(string.Empty, adding.NameBox.Text);
        Ui.Settle();
        Assert.True(adding.Problem.Length == 0, $"Add Remote said why it will not save before anything was typed: {adding.Problem}");
        adding.KeyTextInput("backup");
        Ui.Click(adding.UrlBox);
        adding.KeyTextInput(backup);
        Ui.Settle();
        Assert.Equal(
            [
                $"Adds the remote backup, which this repository fetches from and pushes to at {backup}.",
                "Fetches its branches now: they come under Remote › backup in the Git log's branches, and nothing of yours changes.",
            ], adding.WhatItDoes);
        Assert.Equal($"git remote add backup {backup}\ngit fetch backup", adding.GitWords);
        Ui.Click(adding.SaveButton);
        Ui.Landed(() => window.RemoteWindow is null && !window.Vcs.IsBusy && remotes.Rows.Count == 2, "the remote added, fetched and listed");
        Assert.Contains($"Added the remote backup, at {backup} and fetched its branches", said);
        Assert.Equal(backup, Git("remote", "get-url", "backup").Trim());

        Assert.True(window.Perform(AppActions.PushTo), window.Status);
        Ui.WaitFor(() => window.PushWindow is { IsVisible: true }, "the Push dialog");
        PushDialog pushing = DialogButtonsTests.Check(window.PushWindow!);
        Assert.Equal("backup", pushing.RemoteBox.SelectedItem);
        Ui.Landed(() => pushing.GitWords.Length > 0, "what a push to backup sends");
        Assert.Equal(
            [
                "Makes the branch main on backup, sending 2 commits it lacks, as last fetched.",
                "main keeps tracking origin/main.",
            ], pushing.WhatItDoes);
        Assert.Equal([$"{twice} Twice", $"{first} First"], pushing.Sends.Select(c => $"{c.Short} {c.Subject}"));
        Assert.False(pushing.ForceButton.IsEnabled, "Force Push is offered where backup has no branch main to replace");
        Assert.Equal("git push backup main", pushing.GitWords);
        Ui.Click(pushing.PushButton);
        Ui.Landed(() => window.PushWindow is null && !window.Vcs.IsBusy, "the push to backup");
        Assert.Contains("Pushed main to backup/main", said);
        Assert.Equal(Git("rev-parse", "main").Trim(), TestGit.Run(backup, "rev-parse", "main").Trim());
        Assert.Equal("origin/main", Git("rev-parse", "--abbrev-ref", "main@{upstream}").Trim());

        Ui.Landed(() => remotes.Rows.Count == 2, "the remotes read again");
        remotes.Chosen = remotes.Rows.Single(r => r.Name == "backup");
        Ui.Click(remotes.EditButton);
        Ui.WaitFor(() => window.RemoteWindow is { IsVisible: true }, "the Edit Remote dialog");
        RemoteDialog editing = DialogButtonsTests.Check(window.RemoteWindow!);
        Assert.Equal("backup", editing.NameBox.Text);
        editing.NameBox.SelectAll();
        editing.KeyTextInput("mirror");
        Ui.Click(editing.UrlBox);
        editing.UrlBox.SelectAll();
        editing.KeyTextInput(moved);
        Ui.Settle();
        Assert.Equal(
            [
                "Renames backup to mirror: its 1 branch here becomes mirror/….",
                $"Fetches from and pushes to {moved} from now on, in place of {backup}; its branches here stay as last fetched until it is fetched again.",
                "The repository it names is not changed.",
            ], editing.WhatItDoes);
        Assert.Equal($"git remote rename backup mirror\ngit remote set-url mirror {moved}", editing.GitWords);
        Ui.Click(editing.SaveButton);
        Ui.Landed(() => window.RemoteWindow is null && !window.Vcs.IsBusy, "the remote renamed and given its new URL");
        Assert.Contains($"Renamed the remote backup to mirror, its branches here and those tracking them following; it fetches from and pushes to {moved} now", said);
        Assert.Equal(moved, Git("remote", "get-url", "mirror").Trim());
        Git("rev-parse", "--verify", "-q", "refs/remotes/mirror/main");

        const string Token = "s3cret-token";
        string url = $"https://alice:{Token}@example.invalid/shop.git";
        Ui.Click(remotes.AddButton);
        Ui.WaitFor(() => window.RemoteWindow is { IsVisible: true }, "the Add Remote dialog again");
        RemoteDialog secret = DialogButtonsTests.Check(window.RemoteWindow!);
        secret.KeyTextInput("hosted");
        Ui.Click(secret.UrlBox);
        secret.KeyTextInput(url);
        secret.ThenBox.SelectedIndex = 1;
        Ui.Settle();
        Assert.DoesNotContain(Token, string.Join('\n', [.. secret.WhatItDoes, secret.GitWords]));
        Assert.Equal("git remote add hosted https://alice:••••••@example.invalid/shop.git", secret.GitWords);
        Ui.Click(secret.SaveButton);
        Ui.Landed(() => window.RemoteWindow is null && !window.Vcs.IsBusy && remotes.Rows.Count == 3, "the remote with a token added and listed");
        Assert.Equal(url, Git("remote", "get-url", "hosted").Trim());
        Assert.DoesNotContain(said, s => s.Contains(Token, StringComparison.Ordinal));
        Assert.Equal("https://alice:••••••@example.invalid/shop.git", RemotesDialog.Said(remotes.Rows.Single(r => r.Name == "hosted")));
        remotes.Chosen = remotes.Rows.Single(r => r.Name == "hosted");
        Ui.Click(remotes.EditButton);
        Ui.WaitFor(() => window.RemoteWindow is { IsVisible: true }, "the Edit Remote dialog of the URL with a token");
        RemoteDialog masked = DialogButtonsTests.Check(window.RemoteWindow!);
        Assert.Equal("https://alice:••••••@example.invalid/shop.git", masked.UrlBox.Text);
        masked.KeyTextInput("server");
        Ui.Settle();
        Assert.Equal("git remote rename hosted server", masked.GitWords);
        Ui.Click(masked.SaveButton);
        Ui.Landed(() => window.RemoteWindow is null && !window.Vcs.IsBusy, "the remote with a token renamed");
        Assert.Equal(url, Git("remote", "get-url", "server").Trim());

        window.Perform(AppActions.ShowGit);
        BranchesPanel panel = window.GitBranches;
        Ui.Landed(() => panel.Rows.Any(r => r.Title == "server"), "the remote never fetched in the branches panel");
        Assert.Equal(["Fetch", "Edit Remote…", "Remove Remote…"], panel.RemoteMenu("server").OfType<MenuItem>().Select(i => i.Header as string));

        List<string> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add($"{title}: {text}");
            return Task.FromResult(true);
        };
        Ui.Landed(() => remotes.Rows.Any(r => r.Name == "mirror"), "the remotes read again after the rename");
        remotes.Chosen = remotes.Rows.Single(r => r.Name == "mirror");
        Ui.Click(remotes.RemoveButton);
        Ui.Landed(() => !window.Vcs.IsBusy && remotes.Rows.All(r => r.Name != "mirror"), "the remote removed and the list read again");
        Assert.Equal([$"Remove Remote: Remove the remote mirror? Its 1 branch here goes with it. The repository at {moved} keeps everything: "
            + "adding the remote again and fetching brings its branches back."], asked);
        Assert.Contains("Removed the remote mirror and its branches here: the repository it named keeps everything", said);
        Assert.Equal(["origin", "server"], Git("remote").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        remotes.Close();
        window.Close();
    }

    /// <summary>Update Project is one of Git's changes to the tree: a checkout asked for while its pull waits on the remote
    /// (as the branches' Checkout asks) is refused as Git still busy, leaving the branch where it was, and the update lands.</summary>
    private void A_checkout_asked_for_while_update_project_pulls_is_refused_as_git_busy_and_the_update_lands()
    {
        string clone = TestFolders.Create("git-clone-");
        Git("clone", "-q", "-c", "core.autocrlf=false", folder, clone);
        TestGit.Run(clone, "branch", "feature");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");
        // The remote's side of the pull waits for go, a minute at most, so the update is surely under way while the test acts.
        string pulling = Path.Combine(clone, ".git", "pulling");
        string go = Path.Combine(clone, ".git", "go");
        TestGit.Run(clone, "config", "remote.origin.uploadpack",
            $"touch '{Unix(pulling)}'; i=0; while [ ! -f '{Unix(go)}' ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i + 1)); done; git-upload-pack");

        MainWindow window = Ui.OpenFolder(clone);
        window.OpenFile(Path.Combine(clone, "Shop.cs"));
        Ui.Settle();
        List<string> said = [];
        using IDisposable following = Ui.FollowStatus(window, said);
        window.Perform(AppActions.UpdateProject);
        VcsOutcome checkout;
        try
        {
            Ui.PumpUntil(() => File.Exists(pulling), "the update's pull to reach the remote");
            checkout = Ui.AwaitPumped(window.Vcs.CheckoutAsync("feature"), "the checkout asked for during the update");
        }
        finally
        {
            File.WriteAllText(go, string.Empty);
        }
        Assert.True(checkout is { Succeeded: false, Message: "Git is still busy with the last change" },
            $"A checkout asked for while Update Project pulled was answered: {checkout.Message}");
        Assert.Contains("Git is still busy with the last change", said);
        Ui.Landed(() => Text(window).Contains("long count", StringComparison.Ordinal), "the upstream's change in the open file");
        Ui.Landed(() => window.Status?.StartsWith("Updated", StringComparison.Ordinal) == true, "the update's end in the status bar");
        Assert.Equal("main", TestGit.Run(clone, "branch", "--show-current").Trim());
        window.Close();
    }

    private void Annotations_name_each_lines_commit_mark_a_typed_line_and_a_click_chooses_the_commit()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        string twice = Git("rev-parse", "HEAD").Trim();
        string first = Git("rev-parse", "HEAD~1").Trim();
        MainWindow window = Open();
        Passenger.App.Workspace.OpenDocument shop = window.EditorArea.Active!;
        Task annotating = window.ToggleAnnotationsAsync(shop);
        Ui.Landed(() => annotating.IsCompleted, "the blame");
        IReadOnlyList<BlameLine> blame = window.Vcs.BlameOf(shop)!;
        Assert.Equal(first, blame[0].Hash);
        Assert.Equal(twice, blame[4].Hash);
        Assert.Equal("Twice", blame[4].Summary);
        // A block is named at its first line only, by the day its commit was made where the person is.
        Assert.StartsWith(Git("log", "-1", "--format=%ad", "--date=format-local:%Y-%m-%d", first).Trim(), shop.GutterAnnotations[0], StringComparison.Ordinal);
        Assert.Null(shop.GutterAnnotations[1]);
        Assert.NotNull(shop.GutterAnnotations[4]);
        Assert.True(window.EditorArea.CodeEditor.IsOnGutterColumn(window.EditorArea.CodeEditor.Gutter!.Blame, new Point(4, 4)));

        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// new\n")]);
        Ui.Landed(() => window.Vcs.BlameOf(shop) is { Count: > 0 } now && !now[0].IsCommitted, "the typed line blamed");
        Assert.Equal("Not committed yet", shop.GutterAnnotations[0]);

        CodeEditor editor = window.EditorArea.CodeEditor;
        // The signposts' authors read the blame Annotate shows: the field's newest commit, the first, chosen by a press on it.
        Ui.WaitFor(() => editor.RowsAbove.Count == 3 && editor.RowsAbove[1].Words[^1].Text == "Passenger Tests",
            () => $"the field's signpost naming its author: {string.Join(", ", editor.RowsAbove.SelectMany(r => r.Words).Select(w => w.Text))}");
        Ui.Click(editor, editor.RowAbovePoint(3, 0, editor.RowsAbove[1].Words.Count - 1)!.Value, settle: false);
        Ui.Landed(() => window.GitLog.Selected?.Hash == first, "the field's newest commit chosen in the log from its signpost");
        Ui.Click(editor, new Point(10, editor.PointOf(editor.Document.Snapshot.GetLineStart(5)).Y + (editor.LineHeight / 2)), settle: false);
        Ui.Landed(() => window.GitLog.Selected?.Hash == twice, "the commit chosen in the log");

        Task closing = window.ToggleAnnotationsAsync(shop);
        Ui.Landed(() => closing.IsCompleted, "the annotations closed");
        Assert.Empty(shop.GutterAnnotations);
        Assert.False(editor.IsOnGutterColumn(editor.Gutter!.Blame, new Point(4, 4)));
        window.Close();
    }

    /// <summary>Annotate in a repository whose hashes are SHA-256's 64 hex digits names each line by its commit, as in one
    /// of SHA-1's 40.</summary>
    private void Annotations_name_each_lines_commit_in_a_sha256_repository()
    {
        string tree = TestFolders.Create("git-sha256-");
        try
        {
            File.WriteAllText(Path.Combine(tree, "Shop.cs"), Original);
            TestGit.Run(tree, "init", "-q", "-b", "main", "--object-format=sha256");
            TestGit.Run(tree, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
            TestGit.Run(tree, "config", "user.name", "Passenger Tests");
            TestGit.Run(tree, "config", "core.autocrlf", "false");
            TestGit.Run(tree, "add", "-A");
            TestGit.Run(tree, "commit", "-q", "-m", "First");
            File.WriteAllText(Path.Combine(tree, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
            TestGit.Run(tree, "commit", "-q", "-am", "Twice");
            string twice = TestGit.Run(tree, "rev-parse", "HEAD").Trim();
            string first = TestGit.Run(tree, "rev-parse", "HEAD~1").Trim();
            Assert.Equal(64, twice.Length);

            MainWindow window = Ui.OpenFolder(tree);
            window.OpenFile(Path.Combine(tree, "Shop.cs"));
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument shop = window.EditorArea.Active!;
            Task annotating = window.ToggleAnnotationsAsync(shop);
            Ui.Landed(() => annotating.IsCompleted, "the blame");
            IReadOnlyList<BlameLine> blame = window.Vcs.BlameOf(shop) ?? [];
            Assert.True(blame.Count == 6, $"Annotate blamed {blame.Count} lines of the SHA-256 repository's 6: {window.Status}");
            Assert.Equal(first, blame[0].Hash);
            Assert.Equal(twice, blame[4].Hash);
            Assert.Equal("Twice", blame[4].Summary);
            Assert.NotNull(shop.GutterAnnotations[0]);
            Assert.NotNull(shop.GutterAnnotations[4]);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(tree);
        }
    }

    private static Avalonia.Media.Color Modified() =>
        Avalonia.Controls.ResourceNodeExtensions.TryFindResource(Avalonia.Application.Current!, TokenKeys.Vcs_Modified, Avalonia.Styling.ThemeVariant.Dark, out object? brush) && brush is Avalonia.Media.ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException("The theme has no modified bar's colour");

    private void Stashing_puts_every_change_away_unsaved_and_new_files_too_and_unstashing_brings_them_back()
    {
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        string notes = Path.Combine(folder, "Notes.txt");
        File.WriteAllText(notes, "a new file\n");

        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Half-done count";
        Task stashing = stash.SubmitAsync();
        Ui.Landed(() => stashing.IsCompleted, "the stash made");
        Ui.Landed(() => Text(window) == Original, $"the file back as the commit has it ({window.Status})");
        Assert.False(File.Exists(notes));
        Assert.False(window.EditorArea.Active!.IsModified);
        Assert.Contains("Half-done count", Git("stash", "list"), StringComparison.Ordinal);

        Task<UnstashPopup?> showing = window.ShowUnstashAsync();
        UnstashPopup unstash = Ui.Landed(showing, "the stashes")!;
        Assert.Contains("Half-done count", Assert.Single(unstash.Stashes).Message, StringComparison.Ordinal);
        Task popping = unstash.PopAsync();
        Ui.Landed(() => popping.IsCompleted && Text(window).Contains("long int count", StringComparison.Ordinal), "the change back in the open file");
        Assert.True(File.Exists(notes));
        Assert.Empty(Git("stash", "list"));
        Assert.False(window.IsPopupOpen);
        window.Close();
    }

    /// <summary>Stash Changes with nothing changed makes no stash and says so, in its popup and the status bar, where git
    /// says nothing failed.</summary>
    private void Stash_changes_with_nothing_to_stash_is_refused_saying_so()
    {
        MainWindow window = Open();
        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Nothing at all";
        Task stashing = stash.SubmitAsync();
        Ui.Landed(() => stashing.IsCompleted, "the stash's answer");
        Assert.Equal("No local changes to save", stash.Problem);
        Assert.Equal("No local changes to save", window.Status);
        Assert.Empty(Stashes());
        window.Close();
    }

    private void Backspace_in_unstash_changes_keeps_every_stash_and_delete_asks_before_dropping_the_chosen_one_once()
    {
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// older\n");
        Git("stash", "push", "-m", "Older");
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// newer\n");
        Git("stash", "push", "-m", "Newer");
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        List<string> asked = [];
        bool agree = false;
        window.Confirm = (title, text, yes) =>
        {
            asked.Add($"{title} {text}");
            return Task.FromResult(agree);
        };
        Task<UnstashPopup?> showing = window.ShowUnstashAsync();
        UnstashPopup unstash = Ui.Landed(showing, "the stashes")!;
        Assert.Equal(2, unstash.Stashes.Count);
        Ui.Settle();
        Assert.True(window.FocusManager?.GetFocusedElement() is Avalonia.Controls.ListBoxItem { DataContext: StashInfo { Index: 0 } },
            $"Unstash Changes opened with {window.FocusManager?.GetFocusedElement()?.GetType().Name} taking the keys, not the newest stash's row");

        // Backspace, the key a Mac labels delete, does nothing at all.
        Ui.Press(window, "BACK_SPACE");
        Ui.Landed(() => !window.Vcs.IsBusy, "the end of whatever Backspace began");
        Assert.True(Stashes().Length == 2, $"Backspace in Unstash Changes dropped a stash, leaving [{string.Join(", ", Stashes())}]");
        Assert.Empty(asked);
        Assert.True(window.IsPopupOpen, "Backspace closed Unstash Changes");

        Ui.Press(window, "DELETE");
        Ui.Landed(() => !window.Vcs.IsBusy, "the end of whatever Delete began");
        Assert.Equal(2, Stashes().Length);
        string question = Assert.Single(asked);
        Assert.True(question.Contains("stash@{0}", StringComparison.Ordinal) && question.Contains("Newer", StringComparison.Ordinal), $"Drop asked \"{question}\"");
        Assert.True(window.IsPopupOpen, "Answering no to the drop closed Unstash Changes");

        agree = true;
        Avalonia.Controls.Button[] buttons = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(unstash).OfType<Avalonia.Controls.Button>()];
        Assert.Equal("Drop Apply Pop", string.Join(' ', buttons.Select(b => b.Content)));
        window.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        Assert.All(buttons, b => Assert.False(b.IsEnabled, $"{b.Content} could be pressed while a drop ran"));
        window.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        // Raised, not clicked: a pointer never reaches a button that is off, and this checks its handler refuses too.
#pragma warning disable RS0030
        buttons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
#pragma warning restore RS0030
        Assert.True(window.IsPopupOpen, "A second drop pressed while the first ran closed Unstash Changes before it ended");
        Ui.Landed(() => !window.IsPopupOpen, "the drop's end");
        Assert.Equal(2, asked.Count);
        Assert.Equal("On main: Older", Assert.Single(Stashes()));
        Assert.StartsWith("Dropped", window.Status, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Enter on a stash in Unstash Changes, and a double click, apply and drop nothing: the popup closes for a tab
    /// of the Git window listing the files the stash holds, a change and a file git did not track, each opening as the stash
    /// has it beside the working tree's, a second stash's in a tab of its own; Pop alone puts a stash back.</summary>
    private void Enter_or_a_double_click_on_a_stash_shows_its_files_beside_the_working_tree_and_pops_nothing()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        string longer = Original.Replace("int count", "long count", StringComparison.Ordinal);
        File.WriteAllText(shop, Original + "// older\n");
        Git("stash", "push", "-m", "Older");
        File.WriteAllText(shop, longer);
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "a new file\n");
        Git("stash", "push", "--include-untracked", "-m", "Half-done count");
        MainWindow window = Open();

        UnstashPopup unstash = Ui.Landed(window.ShowUnstashAsync(), "the stashes")!;
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, "Enter on a stash left Unstash Changes open");
        WorkingTreeDiffView files = Assert.IsType<WorkingTreeDiffView>(window.GitWindow.Shown);
        Ui.Landed(() => files.Files.Count == 2, $"the stash's files, where the tab lists [{string.Join(", ", files.Files.Select(f => f.Name))}]");
        Assert.Equal(["Log", "Compare stash@{0} and Working Tree"], window.GitWindow.Titles);
        Assert.Equal(["Notes.txt Unversioned", "Shop.cs Modified"], files.Files.Select(f => $"{f.Name} {f.Kind}"));
        Assert.True(Stashes() is ["On main: Half-done count", "On main: Older"] && File.ReadAllText(shop) == Original && !File.Exists(Path.Combine(folder, "Notes.txt")),
            $"Enter on a stash applied or dropped one, leaving [{string.Join(", ", Stashes())}] and Shop.cs as [{File.ReadAllText(shop)}]");

        foreach ((string name, string stashed, string now) in (ReadOnlySpan<(string, string, string)>)[("Shop.cs", longer, Original), ("Notes.txt", "a new file\n", "")])
        {
            Ui.Click(Ui.Row(files.List, files.Files.Single(f => f.Name == name)));
            Ui.Press(window, "ENTER");
            IDiffHost diff = OpenDiffs.Landed(window, $"{name} as the stash has it beside the working tree's");
            DiffView view = OpenDiffs.ViewOf(diff);
            Assert.True(view.Left.Document.Snapshot.ToString() == stashed && view.Right.Document.Snapshot.ToString() == now,
                $"{name}'s diff with the stash showed [{view.Left.Document.Snapshot}] beside [{view.Right.Document.Snapshot}]; the status bar says \"{window.Status}\"");
            Assert.Equal($"{name}: stash@{{0}} and your version", OpenDiffs.TitleOf(diff));
            diff.Close();
            Ui.Settle();
        }

        // A double click on the older stash: its own tab, and still nothing applied or dropped.
        UnstashPopup again = Ui.Landed(window.ShowUnstashAsync(), "the stashes again")!;
        Ui.Settle();
        Ui.DoubleClick(Ui.Row(Find(again, again.Stashes), again.Stashes[1]));
        Assert.False(window.IsPopupOpen, "A double click on a stash left Unstash Changes open");
        WorkingTreeDiffView older = Assert.IsType<WorkingTreeDiffView>(window.GitWindow.Shown);
        Ui.Landed(() => older.Files.Count == 1, $"the older stash's files, where its tab lists [{string.Join(", ", older.Files.Select(f => f.Name))}]");
        Assert.Equal(["Log", "Compare stash@{0} and Working Tree", "Compare stash@{1} and Working Tree"], window.GitWindow.Titles);
        Assert.Equal(2, Stashes().Length);
        Assert.Equal(Original, File.ReadAllText(shop));
        window.Close();
    }

    /// <summary>Unstash Changes acts on the stash chosen, by its commit: another program stashing while the list is open
    /// moves it down the list, and Drop and Pop still take that one, never the newer stash in its old place; one another
    /// program dropped meanwhile is refused, saying so.</summary>
    private void Unstash_changes_takes_the_stash_chosen_though_another_program_stashed_or_dropped_since_the_list_was_read()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        File.AppendAllText(shop, "// older\n");
        Git("stash", "push", "-m", "Older");
        MainWindow window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(true);

        UnstashPopup dropping = Ui.Landed(window.ShowUnstashAsync(), "the stashes")!;
        Assert.Equal("On main: Older", Assert.Single(dropping.Stashes).Message);
        File.AppendAllText(shop, "// newer\n");
        Git("stash", "push", "-m", "Newer");
        Task dropped = dropping.DropAsync();
        Ui.Landed(() => dropped.IsCompleted, "the drop");
        Assert.True(Stashes() is ["On main: Newer"], $"Drop of Older, after another program stashed Newer, left [{string.Join(", ", Stashes())}]");
        Assert.Equal("Dropped On main: Older", window.Status);

        UnstashPopup popping = Ui.Landed(window.ShowUnstashAsync(), "the stashes again")!;
        Assert.Equal("On main: Newer", Assert.Single(popping.Stashes).Message);
        File.AppendAllText(shop, "// newest\n");
        Git("stash", "push", "-m", "Newest");
        Task popped = popping.PopAsync();
        Ui.Landed(() => popped.IsCompleted, "the pop");
        Assert.True(Stashes() is ["On main: Newest"], $"Pop of Newer, after another program stashed Newest, left [{string.Join(", ", Stashes())}]");
        Assert.Equal(Original + "// newer\n", File.ReadAllText(shop));

        UnstashPopup gone = Ui.Landed(window.ShowUnstashAsync(), "the stashes once more")!;
        Git("stash", "drop");
        Task refused = gone.DropAsync();
        Ui.Landed(() => refused.IsCompleted, "the drop's refusal");
        Assert.Empty(Stashes());
        Assert.Equal("The stash \"On main: Newest\" is no longer in the list: it was popped or dropped since the list was read", window.Status);
        window.Close();
    }

    /// <summary>Stash Changes leaves new files in place once its box for them is unticked and puts them away with it ticked;
    /// the Commit window's Stashes tab lists both, newest first, by message, branch and age, opens one to its files (its new
    /// file among them) and a file's diff beside what it was, says what Pop, Apply and Drop do with it in words and git's,
    /// applies one keeping it, pops the other dropping it, and drops the last once asked, whose Undo puts it back.</summary>
    private void The_commit_windows_stashes_list_each_stashs_files_and_diffs_then_apply_pop_and_drop_it_as_said_and_undo_brings_a_dropped_one_back()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        string notes = Path.Combine(folder, "Notes.txt");
        MainWindow window = Open();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        File.WriteAllText(notes, "a new file\n");
        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        Assert.Equal(MainWindow.IncludeNewFiles, stash.Option!.Content);
        Assert.True(stash.Option.IsChecked, "Stash Changes offered to leave new files out to begin with");
        Ui.Click(stash.Option);
        stash.Text = "Count only";
        Task stashing = stash.SubmitAsync();
        Ui.Landed(() => stashing.IsCompleted && Text(window) == Original, $"the change stashed and the file as the commit has it ({window.Status})");
        Assert.True(File.Exists(notes), "Stash Changes with its new files' box unticked put Notes.txt away");
        stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Notes too";
        stashing = stash.SubmitAsync();
        Ui.Landed(() => stashing.IsCompleted && !File.Exists(notes), "the new file stashed");
        Assert.Equal(["On main: Notes too", "On main: Count only"], Stashes());

        window.Confirm = (_, _, _) => Task.FromResult(true);
        window.Perform(AppActions.ShowCommit);
        CommitView view = window.CommitView;
        Ui.Click(view.StashesTab);
        Assert.True(view.ShowsStashes, "The Stashes tab's click left the changes shown");
        StashesView stashes = view.Stashes;
        Ui.Landed(() => stashes.Stashes.Count == 2, "the stashes listed");
        Assert.Equal(["Notes too", "Count only"], stashes.Rows.Select(r => r.Said.Message));
        Assert.Equal("main", stashes.Rows[0].Said.Branch);
        Ui.Await(stashes.ChooseAsync(1, "Shop.cs"), "Count only's files");
        Assert.Equal(
            [
                "Pop puts the changes to Shop.cs back into your files and drops the stash; Apply puts them back and keeps it.",
                "Where your files changed the same lines since, git stops for the conflicts and the stash stays.",
                "Drop deletes it without putting anything back; Undo brings it back while git keeps its commit.",
            ], stashes.WhatItDoes);
        Assert.Equal("git stash pop stash@{1}\ngit stash apply stash@{1}\ngit stash drop stash@{1}", stashes.GitWords);
        stashes.FocusRows();
        Ui.Press(window, "ENTER");
        IDiffHost diff = OpenDiffs.Landed(window, "the stashed file's diff");
        Assert.Equal(Original, OpenDiffs.ViewOf(diff).Left.Document.Snapshot.ToString());
        Assert.Equal(Original.Replace("int count", "long int count", StringComparison.Ordinal), OpenDiffs.ViewOf(diff).Right.Document.Snapshot.ToString());
        diff.Close();
        Ui.Await(stashes.ChooseAsync(0, "Notes.txt"), "Notes too's files");
        Assert.StartsWith("Pop puts the new file Notes.txt back", stashes.WhatItDoes[0], StringComparison.Ordinal);

        Ui.Await(stashes.ChooseAsync(1), "Count only chosen");
        Ui.Click(stashes.ApplyButton);
        Ui.Landed(() => !window.Vcs.IsBusy && Text(window).Contains("long int count", StringComparison.Ordinal), "the stash applied to the open file");
        Assert.Equal(["On main: Notes too", "On main: Count only"], Stashes());
        Ui.Await(stashes.ChooseAsync(0), "Notes too chosen");
        Ui.Click(stashes.PopButton);
        Ui.Landed(() => !window.Vcs.IsBusy && File.Exists(notes) && stashes.Stashes.Count == 1, "the stash popped and dropped");
        Assert.Equal(["On main: Count only"], Stashes());

        List<string> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add($"{title}: {text}");
            return Task.FromResult(true);
        };
        string kept = Git("rev-parse", "stash@{0}").Trim();
        Ui.Await(stashes.ChooseAsync(0), "Count only chosen again");
        Ui.Click(stashes.DropButton);
        Ui.Landed(() => !window.Vcs.IsBusy && stashes.Stashes.Count == 0, "the stash dropped");
        Assert.Equal([$"Drop Stash: Drop the stash \"Count only\" without putting its changes back? Undo brings it back while git keeps its commit, {GitHashes.Short(kept)}."], asked);
        Assert.Empty(Stashes());
        Assert.NotNull(stashes.Placeholder);
        Assert.True(stashes.IsKeyboardFocusWithin, "Once its last stash was dropped the Stashes tab let go of the keys, so Undo would take the open file's own step");
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => Stashes() is ["On main: Count only"] && stashes.Stashes.Count == 1, () => $"Undo to put the dropped stash back ({window.Status})");
        Assert.Equal(kept, Git("rev-parse", "stash@{0}").Trim());
        window.Close();
    }

    /// <summary>A folder holding another repository below its own: the Commit window has a tab of each, by its name, branch
    /// and how many files changed, its own listing its file alone (the other's top left to it), the status bar's branch
    /// names the folder's repository, a commit from each tab goes to that repository, its files and branch as they were
    /// in the other, and a tab's Push… pushes its repository while another is chosen.</summary>
    private void A_folder_holding_a_second_repository_lists_and_commits_each_from_the_commit_windows_tab_of_it()
    {
        string lib = Path.Combine(folder, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "Lib.cs"), "class Lib { }\n");
        TestGit.Init(lib);
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(lib, "Lib.cs"), "class Lib { int size; }\n");
        MainWindow window = Open();
        window.Perform(AppActions.ShowCommit);
        CommitView view = window.CommitView;
        string top = VcsController.NameOf(window.Vcs.Repository!);
        Ui.WaitFor(() => view.RepositoryTabs.Count == 2, "the Commit window's tab of each repository");
        Ui.Landed(() => view.RepositoryTabs.Select(t => t.Content as string).SequenceEqual([$"{top} · main (1)", "lib · main (1)"]),
            $"the tabs of {top} and lib with their branches and changes, where they say [{string.Join(", ", view.RepositoryTabs.Select(t => t.Content))}]");
        Assert.Equal([PathNames.Canonical(Path.Combine(folder, "Shop.cs"))], view.Chosen.Select(c => c.Path));
        Assert.Equal($"{top}: main", window.FindControl<TextBlock>("GitBranchText")?.Text);

        view.Message = "Twice";
        Assert.True(Ui.Await(view.CommitAsync(push: false), "the commit in the folder's repository"), window.Status);
        Assert.Equal("Twice", Git("log", "-1", "--format=%s").Trim());
        Ui.Click(view.RepositoryTabs[1]);
        Ui.Landed(() => view.Vcs.Repository is { } tree && PathNames.Same(tree.Root, PathNames.Canonical(lib)) && view.Chosen.Count == 1, "lib's changed file listed");
        Assert.Equal(PathNames.Canonical(Path.Combine(lib, "Lib.cs")), view.Chosen[0].Path);
        view.Message = "Size";
        Assert.True(Ui.Await(view.CommitAsync(push: false), "the commit in lib"), window.Status);
        Assert.Equal("Size", TestGit.Run(lib, "log", "-1", "--format=%s").Trim());
        Assert.Equal(string.Empty, TestGit.Run(lib, "status", "--porcelain").Trim());
        Assert.Equal("Twice", Git("log", "-1", "--format=%s").Trim());

        // Each repository's tab pushes that repository, whichever is chosen.
        string libOrigin = TestFolders.Create("git-lib-origin-");
        TestGit.Run(libOrigin, "init", "-q", "--bare", "-b", "main");
        TestGit.Run(lib, "remote", "add", "origin", libOrigin);
        Ui.Click(view.RepositoryTabs[0]);
        Ui.ChooseFromMenu(view.RepositoryTabs[1], "Push…");
        Ui.PumpUntil(() => window.PushWindow is { IsVisible: true }, "the Push dialog of lib");
        PushDialog pushing = DialogButtonsTests.Check(window.PushWindow!);
        Assert.Equal(["Size"], pushing.Sends.Take(1).Select(c => c.Subject));
        Ui.ClickButton(pushing, "Push");
        Ui.Landed(() => window.Status == "Pushed main to origin/main, which it tracks from now on", $"lib pushed from its tab's menu, where the status bar says \"{window.Status}\"");
        Assert.Equal(TestGit.Run(lib, "rev-parse", "main").Trim(), TestGit.Run(libOrigin, "rev-parse", "main").Trim());
        window.Close();
    }

    private void Stashing_a_change_and_a_new_file_leaves_the_git_log_as_it_was()
    {
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 1, "the log");
        string before = Listed(log);
        window.Perform(AppActions.FocusEditor);

        // A stash with a new file in it is three commits of git's own under refs/stash.
        window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "a new file\n");
        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Half-done count";
        Task stashing = stash.SubmitAsync();
        Ui.Landed(() => stashing.IsCompleted, "the stash made");
        Assert.Single(Stashes());

        Task reading = log.LoadAsync();
        Ui.Landed(() => reading.IsCompleted, "the log read again");
        Assert.True(Listed(log) == before, $"After one stash the log lists\n{Listed(log)}\nwhere it listed\n{before}");
        window.Close();

        static string Listed(GitLogView log) => string.Join('\n', log.Rows.Select(r => $"{r.Commit.Subject} ({string.Join(", ", r.Commit.Refs)})"));
    }

    /// <summary>In a new solution's folder of no repository, the Git window lists no branch, and Create Git Repository from
    /// the Commit window's empty state writes the .NET .gitignore and makes the repository: the Commit window lists the
    /// solution's files and the .gitignore, nothing of bin or obj, as git does, and the action is offered no more.</summary>
    private void Create_git_repository_from_the_commit_windows_empty_state_leaves_build_output_out_of_the_changes()
    {
        string solution = PathNames.Canonical(TestFolders.Create("git-create-"));
        TestFolders.Write(solution, "Shop.slnx", "<Solution>\n  <Project Path=\"Shop/Shop.csproj\" />\n</Solution>\n");
        TestFolders.Write(solution, "Shop/Shop.csproj", TestProjects.Console());
        TestFolders.Write(solution, "Shop/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        TestFolders.Write(solution, $"Shop/bin/Debug/{TestProjects.Framework}/Shop.dll", string.Empty);
        TestFolders.Write(solution, $"Shop/obj/Debug/{TestProjects.Framework}/Shop.dll", string.Empty);
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        window.Perform(AppActions.ShowGit);
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        Ui.Settle();
        Assert.True(window.Vcs.Repository is null && window.GitBranches.Rows.Count == 0,
            $"The Git window of a folder in no repository lists [{string.Join(", ", window.GitBranches.Rows.Select(r => r.Title))}]");
        Assert.Contains(window.GitLog.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == ActionTitles.CreateRepository.English);

        Ui.ClickButton(commit, ActionTitles.CreateRepository.English);
        string[] Listed() => [.. commit.Rows.OfType<CommitRow>().Select(r => Path.GetRelativePath(solution, r.Change.Path).Replace('\\', '/')).Order(StringComparer.Ordinal)];
        Ui.WaitFor(() => window.Vcs.Repository is not null && Listed().Contains(".gitignore"),
            () => $"the repository made with its .gitignore, where the Commit window lists [{string.Join(", ", Listed())}] and the status bar says '{window.Status}'");
        Assert.Equal([".gitignore", "Shop.slnx", "Shop/Program.cs", "Shop/Shop.csproj"], Listed());
        Assert.Contains("[Bb]in/", File.ReadAllText(Path.Combine(solution, ".gitignore")), StringComparison.Ordinal);
        string untracked = TestGit.Run(solution, "status", "--porcelain", "--untracked-files=all");
        Assert.True(!untracked.Contains("/bin/", StringComparison.Ordinal) && !untracked.Contains("/obj/", StringComparison.Ordinal),
            $"git offers build output for the first commit:\n{untracked}");
        Assert.Equal($"Created a Git repository in {Path.GetFileName(solution)}", window.Status);
        Ui.WaitFor(() => window.GitBranches.Rows.Count > 0, "the Git window to list the new repository's HEAD");
        Assert.Equal(Refusals.InRepository.Shown, window.Actions.WhyNot(AppActions.CreateRepository, window.Actions.Context())?.Shown);
        Assert.DoesNotContain(commit.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == ActionTitles.CreateRepository.English);
        window.Close();
    }

    private void Add_to_gitignore_keeps_the_files_byte_order_mark_its_windows_line_breaks_and_an_unsaved_edit_to_it()
    {
        // As Visual Studio's template writes one: a byte order mark and CRLF, its last line with no break.
        string ignore = Path.Combine(folder, ".gitignore");
        File.WriteAllBytes(ignore, [0xEF, 0xBB, 0xBF, .. "bin/\r\nobj/"u8]);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "scratch\n");
        MainWindow window = Open();
        window.OpenFile(ignore);
        Ui.Settle();
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("# out ");
        Ui.Settle();

        window.Perform(AppActions.ShowCommit);
        Avalonia.Controls.ListBox changes = Find(window.CommitView, window.CommitView.Rows);
        Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "notes.txt"), "the unversioned file listed");
        changes.SelectedItem = window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "notes.txt");
        Ui.ChooseFromMenu(changes, "Add to .gitignore");
        Assert.Equal([0xEF, 0xBB, 0xBF, .. "# out bin/\r\nobj/\r\n/notes.txt\r\n"u8], File.ReadAllBytes(ignore));
        Ui.Landed(() => window.Vcs.Changes.All(c => c.Name != "notes.txt"), "the ignored file gone from the changes");
        Passenger.App.Workspace.OpenDocument shown = window.EditorArea.Documents.Single(d => d.Name == ".gitignore");
        Assert.Equal("# out bin/\r\nobj/\r\n/notes.txt\r\n", shown.Text.Snapshot.ToString());
        Assert.False(shown.IsModified, "The open .gitignore shows unsaved changes it does not have");
        window.Close();
    }

    /// <summary>Add to .gitignore with no .gitignore makes one as its .editorconfig asks, a byte order mark and CRLF, and
    /// Undo then Redo in the Solution view puts it back in those same bytes, whether it was closed or open when undone.</summary>
    private void Add_to_gitignore_making_the_file_as_its_editorconfig_asks_and_Undo_then_Redo_put_it_back_in_the_same_bytes()
    {
        File.WriteAllText(Path.Combine(folder, ".editorconfig"), "root = true\n\n[*]\nend_of_line = crlf\ncharset = utf-8-bom\n");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "scratch\n");
        string ignore = Path.Combine(PathNames.Canonical(folder), ".gitignore");
        MainWindow window = Open();
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        window.Perform(AppActions.ShowCommit);
        Avalonia.Controls.ListBox changes = Find(window.CommitView, window.CommitView.Rows);
        Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "notes.txt"), "the unversioned file listed");
        changes.SelectedItem = window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "notes.txt");
        Ui.ChooseFromMenu(changes, "Add to .gitignore");
        byte[] made = [0xEF, 0xBB, 0xBF, .. "/notes.txt\r\n"u8];
        Assert.Equal(made, File.ReadAllBytes(ignore));

        foreach (bool open in new[] { false, true })
        {
            if (open)
            {
                window.OpenFile(ignore);
                Ui.Settle();
            }
            Global(window, EditorActions.Undo);
            Assert.True(!File.Exists(ignore), $"Undo left the .gitignore Add to .gitignore made ({(open ? "open" : "closed")}): {window.Status}; asked [{string.Join("; ", asked)}]");
            Global(window, EditorActions.Redo);
            Assert.True(File.Exists(ignore) && File.ReadAllBytes(ignore).AsSpan().SequenceEqual(made),
                $"Redo of Add to .gitignore, undone {(open ? "open" : "closed")}, wrote {(File.Exists(ignore) ? Convert.ToHexString(File.ReadAllBytes(ignore)) : "nothing")}, not {Convert.ToHexString(made)}: {window.Status}");
        }
        window.Close();
    }

    /// <summary>The Explorer's Git menu adds an unversioned file to .gitignore and goes to the line ignoring a file, saying
    /// it; the open .gitignore says on hover what a line ignores, fades a line ignoring nothing, marks one a later line takes
    /// back, and completes an untracked folder, each as git answers.</summary>
    private void The_explorer_adds_to_gitignore_and_goes_to_the_ignoring_line_and_a_gitignore_says_what_each_line_ignores_as_git_answers()
    {
        string root = PathNames.Canonical(folder);
        string ignore = Path.Combine(root, ".gitignore");
        File.WriteAllText(ignore, "*.log\nnothing-here\n*.tmp\n!keep.tmp\n");
        foreach (string name in (string[])["a.log", "keep.tmp", "notes.txt", "scratch/x.dat"])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, name))!);
            File.WriteAllText(Path.Combine(root, name), "scratch\n");
        }
        MainWindow window = Open();
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "notes.txt"), "the unversioned file listed");
        Ui.ChooseFromRowMenu(window, Row(window, Path.Combine(root, "notes.txt")), "Git", "Add to .gitignore");
        Ui.WaitFor(() => File.ReadAllText(ignore).EndsWith("\n/notes.txt\n", StringComparison.Ordinal), () => $"notes.txt added to .gitignore: {window.Status}");

        Ui.ChooseFromRowMenu(window, Row(window, Path.Combine(root, "a.log")), "Git", "Go to Ignoring Line");
        Ui.WaitFor(() => window.Status == "a.log is ignored by line 1 of .gitignore (*.log)", () => $"Go to Ignoring Line said: {window.Status}");
        Passenger.App.Workspace.OpenDocument shown = window.EditorArea.Active!;
        Assert.Equal(ignore, shown.Path);

        string[] marked = ["Unnecessary nothing-here = Ignores nothing in the working tree", "Warning *.tmp = Ignores nothing: line 4 takes back everything it ignores"];
        Ui.WaitFor(() => Marked().SequenceEqual(marked), () => $"The .gitignore's marks are {string.Join("; ", Marked())}");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        Ui.RestOn(editor, 1);
        Ui.WaitFor(() => window.Assist.HoverText?.Contains("Ignores 1 path here: a.log", StringComparison.Ordinal) == true, () => $"The card on *.log says {window.Assist.HoverText}");
        window.MouseMove(new Avalonia.Point(1, 1), Avalonia.Input.RawInputModifiers.None);

        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.Length);
        window.KeyTextInput("scr");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "scratch/", () => $"Completion lists {string.Join(", ", window.Assist.CompletionLabels)} for scr");
        Ui.Press(window, "ENTER");
        Assert.EndsWith("\nscratch/", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();

        IEnumerable<string> Marked() => shown.Markers.Select(m => $"{m.Kind} {shown.Text.Snapshot.GetText(m.Range.Start, m.Range.Length)} = {m.Message}");
    }

    /// <summary>The Explorer's row of a path, revealed.</summary>
    private static Passenger.App.Explorer.ExplorerNode Row(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path is { } at && PathNames.Same(at, path))
            ?? throw new InvalidOperationException($"The Explorer shows no row of {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    /// <summary>Undo or Redo of the window's last command of files, run in the Solution view with Shop.cs's row chosen.</summary>
    private void Global(MainWindow window, string action)
    {
        string shop = PathNames.Canonical(Path.Combine(folder, "Shop.cs"));
        Assert.True(window.Perform(AppActions.FocusExplorer), "The Solution view did not come forward");
        window.ExplorerView.Reveal(shop);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == shop));
        Ui.Run(window, action);
        Ui.Settle();
    }

    /// <summary>Add to .gitignore has Git ignore the chosen file and no other, whatever its name holds: notes.txt at the top
    /// and not docs/notes.txt, [draft].md and not d.md (its brackets a pattern), #todo.txt and !keep.txt (a comment and a
    /// negation written plainly), and where a name can hold them a wildcard and a space it ends with.</summary>
    private void Add_to_gitignore_has_git_ignore_the_chosen_file_and_no_other_whatever_its_name_holds()
    {
        // Windows takes no '*' in a name, and drops a space it ends with.
        string[] chosen = ["notes.txt", "[draft].md", "#todo.txt", "!keep.txt", .. OperatingSystem.IsWindows() ? (string[])[] : ["a*b.txt", "end "]];
        string[] others = ["docs/notes.txt", "d.md", "docs/#todo.txt", "keep.txt", .. OperatingSystem.IsWindows() ? (string[])[] : ["aXb.txt", "end"]];
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        foreach (string name in chosen.Concat(others))
        {
            File.WriteAllText(Path.Combine(folder, name), "scratch\n");
        }
        MainWindow window = Open();
        window.Perform(AppActions.ShowCommit);
        Avalonia.Controls.ListBox changes = Find(window.CommitView, window.CommitView.Rows);
        foreach (string name in chosen)
        {
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => Unix(window.Vcs.Repository!.Relative(r.Change.Path)) == name), $"{name} listed");
            changes.SelectedItem = window.CommitView.Rows.OfType<CommitRow>().First(r => Unix(window.Vcs.Repository!.Relative(r.Change.Path)) == name);
            Ui.ChooseFromMenu(changes, "Add to .gitignore");
            Assert.Equal($"Added {name} to .gitignore", window.Status);
            Ui.Landed(() => window.Vcs.Changes.All(c => Unix(window.Vcs.Repository!.Relative(c.Path)) != name), $"{name} ignored, gone from the changes");
        }

        // Each path after the line that ignores it ("source:line:pattern", or "::" for none) and a tab.
        Dictionary<string, string> ignoredBy = [];
        foreach (string said in Git(["check-ignore", "-v", "-n", "--", .. chosen, .. others]).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains('\t')))
        {
            ignoredBy[said[(said.IndexOf('\t') + 1)..]] = said[..said.IndexOf('\t')];
        }
        string[] wrong = [.. chosen.Where(n => ignoredBy.GetValueOrDefault(n) is null or "::").Select(n => $"{n} not ignored")
            .Concat(others.Where(n => ignoredBy.GetValueOrDefault(n) is not (null or "::")).Select(n => $"{n} ignored by {ignoredBy[n]}"))];
        Assert.True(wrong.Length == 0, $"After Add to .gitignore on {string.Join(", ", chosen)} alone, Git finds {string.Join("; ", wrong)}; .gitignore holds:\n{File.ReadAllText(Path.Combine(folder, ".gitignore"))}");
        window.Close();
    }

    /// <summary>On Windows Add to .gitignore into a .gitignore another program holds waits for it: while it is held the
    /// status bar says it waits for that file, never that it added the line, and once the program lets go the line is in the
    /// file and the status bar says it was added.</summary>
    private void Add_to_gitignore_into_a_gitignore_another_program_holds_says_it_waits_and_added_once_the_file_is_let_go()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace or write a file another program holds");
        string ignore = Path.Combine(folder, ".gitignore");
        File.WriteAllText(ignore, "bin/\nobj/\n");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "scratch\n");
        MainWindow window = Open();
        window.Perform(AppActions.ShowCommit);
        Avalonia.Controls.ListBox changes = Find(window.CommitView, window.CommitView.Rows);
        Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "notes.txt"), "the unversioned file listed");
        changes.SelectedItem = window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "notes.txt");
        List<string> said = [];
        using IDisposable following = Ui.FollowStatus(window, said);
        using (new FileStream(ignore, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Ui.ChooseFromMenu(changes, false, "Add to .gitignore");
            Ui.PumpUntil(() => said.Any(s => s.StartsWith("Waiting", StringComparison.Ordinal) || s.StartsWith("Added", StringComparison.Ordinal)),
                "Add to .gitignore to say it waits for the held .gitignore");
            Assert.Equal("bin/\nobj/\n", File.ReadAllText(ignore));
        }
        Ui.PumpUntil(() => said.Any(s => s.StartsWith("Added", StringComparison.Ordinal) || s.StartsWith(".gitignore was not changed", StringComparison.Ordinal)),
            "Add to .gitignore to say it added the line once .gitignore was let go");
        Assert.Equal(["Waiting for .gitignore to be saved…", "Added notes.txt to .gitignore"],
            said.Where(s => s.StartsWith("Waiting", StringComparison.Ordinal) || s.StartsWith("Added", StringComparison.Ordinal) || s.StartsWith(".gitignore", StringComparison.Ordinal)));
        Assert.Equal("bin/\nobj/\n/notes.txt\n", File.ReadAllText(ignore));
        window.Close();
    }

    private void The_commit_and_log_menus_ignore_a_file_revert_a_commit_and_branch_from_one()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "scratch\n");
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);

        window.Perform(AppActions.ShowCommit);
        Avalonia.Controls.ListBox changes = Find(window.CommitView, window.CommitView.Rows);
        Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "notes.txt"), "the unversioned file listed");
        changes.SelectedItem = window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "notes.txt");
        Ui.ChooseFromMenu(changes, "Add to .gitignore");
        Assert.Equal("/notes.txt\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
        Ui.Landed(() => window.Vcs.Changes.All(c => c.Name != "notes.txt"), "the ignored file gone from the changes");

        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == "Twice"), "the log");
        Avalonia.Controls.ListBox commits = Find(log, log.Rows);
        log.Select(log.Rows.First(r => r.Commit.Subject == "Twice").Commit);
        Ui.ChooseFromMenu(commits, "Revert Commit");
        Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == "Revert \"Twice\"", "the revert's commit");
        Ui.Landed(() => Text(window).Contains("count++", StringComparison.Ordinal), "the revert in the open file");
        // Git reads what the revert did (slower on Windows) before it takes another change; one asked for meanwhile is refused.
        Ui.Landed(() => !window.Vcs.IsBusy, "the revert's end");

        Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == "First"), "the first commit in the log");
        CommitInfo first = log.Rows.First(r => r.Commit.Subject == "First").Commit;
        log.Select(first);
        Ui.ChooseFromMenu(commits, "Copy Revision Number");
        Ui.WaitFor(() => Ui.Clipboard(window) == first.Hash, "the hash on the clipboard");
        Ui.ChooseFromMenu(commits, "New Branch…");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = "from-first";
        Task named = popup.SubmitAsync();
        Ui.Landed(() => named.IsCompleted, "the new branch");
        Assert.True(popup.Problem is null, $"The new branch was refused: {popup.Problem}");
        Assert.Equal("from-first", window.Vcs.Branch);
        Assert.Equal(first.Hash, Git("rev-parse", "HEAD").Trim());
        window.Close();
    }

    /// <summary>Interactively Rebase from Here plans the commits from the one chosen in the log: one dragged to the bottom,
    /// one squashed by its key, one reworded by typing its message and one dropped from its menu, the preview showing the
    /// history it makes before anything runs; Start Rebasing makes that history, the commits' messages and files as
    /// planned, the dropped commit's file gone under one change of Local History named for the rebase.</summary>
    private void An_interactive_rebase_from_the_log_moves_squashes_rewords_and_drops_commits_making_the_history_its_preview_showed()
    {
        foreach (string name in (string[])["One", "Two", "Three", "Four", "Five"])
        {
            File.WriteAllText(Path.Combine(folder, $"{name}.txt"), $"{name}\n");
            Git("add", $"{name}.txt");
            Git("commit", "-q", "-m", name);
        }
        MainWindow window = Open();
        InteractiveRebaseDialog dialog = PlanRebase(window, "One");
        Assert.Equal(["Five", "Four", "Three", "Two", "One"], dialog.Rows.Select(r => r.Step.Commit.Subject));
        Assert.False(dialog.StartButton.IsEnabled, "Start Rebasing was offered for a plan that changes nothing");
        ListBox commits = Find(dialog, dialog.Rows);

        Ui.Drag(dialog, RowPoint(dialog, commits, 1, 0.5), RowPoint(dialog, commits, 4, 0.8));
        Assert.Equal(["Five", "Three", "Two", "One", "Four"], dialog.Rows.Select(r => r.Step.Commit.Subject));
        Ui.Click(RowView(commits, 1), new Point(300, ListRow.Height / 2));
        Ui.Press(dialog, "S");
        dialog.Chosen = dialog.RowOf("Five");
        Ui.ChooseFromMenu(commits, "Drop");
        dialog.Chosen = dialog.RowOf("One");
        Ui.Click(dialog.MessageBox);
        dialog.MessageBox.SelectAll();
        dialog.KeyTextInput("One, reworded");
        Ui.Settle();
        Assert.Equal([RebaseCommand.Drop, RebaseCommand.Squash, RebaseCommand.Pick, RebaseCommand.Reword, RebaseCommand.Pick], dialog.Rows.Select(r => r.Step.Command));
        Assert.Equal(dialog.RowOf("Two").Step.Commit.Short, dialog.RowOf("Three").Into);

        string[] shown = [.. dialog.History.Where(r => r.Made is not null).Select(r => r.Subject)];
        Assert.Equal(["Two", "One, reworded", "Four"], shown);
        Assert.Equal(["new message, 1 folded in", "new message", "moved"], dialog.History.Where(r => r.Made is not null).Select(r => string.Join(", ", r.Tags)));
        Assert.EndsWith(" First", dialog.History[^1].Subject);
        Assert.Contains("Five", dialog.LeftOut, StringComparison.Ordinal);
        Assert.True(dialog.StartButton.IsEnabled, dialog.Problem);

        Ui.Click(dialog.StartButton);
        Ui.Landed(() => window.InteractiveRebaseWindow is null && !window.Vcs.IsBusy, "the rebase's end");
        Assert.Equal(MergeOperation.None, window.Vcs.Merge.Operation);
        Assert.Equal([.. shown, "First"], Git("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("Two\n\nThree", Git("log", "-1", "--format=%B").Trim());
        Assert.False(File.Exists(Path.Combine(folder, "Five.txt")), "The dropped commit's file was still there after the rebase");
        Assert.True(File.Exists(Path.Combine(folder, "Four.txt")), "The moved commit's file was gone after the rebase");
        LocalHistory history = window.LocalHistory!;
        Ui.WaitFor(() => history.Snapshot().Events.Any(e => e.Name == "Git rebase" && e.Path?.EndsWith("Five.txt", StringComparison.Ordinal) == true),
            "the dropped commit's file kept in Local History under the rebase");
        Assert.Single(history.Snapshot().Events.Where(e => e.Name == "Git rebase").Select(e => e.Set).Distinct());

        // A commit made on the branch while the next plan is open: the plan does not run, and the commit stays.
        dialog = PlanRebase(window, "Four");
        dialog.Chosen = dialog.RowOf("Two");
        Ui.ChooseFromMenu(Find(dialog, dialog.Rows), "Drop");
        File.WriteAllText(Path.Combine(folder, "Six.txt"), "Six\n");
        Git("add", "Six.txt");
        Git("commit", "-q", "-m", "Six");
        Ui.Click(dialog.StartButton);
        Ui.Landed(() => window.InteractiveRebaseWindow is null && !window.Vcs.IsBusy, "the refused rebase's end");
        Assert.Equal(RebaseTodo.MovedSince, window.Status);
        Assert.Equal(["Six", .. shown, "First"], Git("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        window.Close();
    }

    /// <summary>An interactive rebase follows git through its stops as a rebase does: a commit moved under the one it builds
    /// on and reworded, its preview foreseeing it stops for conflicts and the one it built on may, stops it for conflicts,
    /// the Conflicts dialog naming the person's commit, never the twin made for its new message; Skip Commit leaves that
    /// commit out and goes on to the edit planned after it, where it stops saying so with Skip Commit refused, and
    /// Continue Rebase finishes it.</summary>
    private void An_interactive_rebase_stops_for_conflicts_and_at_an_edit_and_goes_on_by_skip_commit_and_continue_rebase()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 3", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Thrice");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes\n");
        Git("add", "Notes.txt");
        Git("commit", "-q", "-m", "Notes");
        MainWindow window = Open();
        InteractiveRebaseDialog dialog = PlanRebase(window, "Twice");
        ListBox commits = Find(dialog, dialog.Rows);
        Ui.Click(RowView(commits, 1), new Point(300, ListRow.Height / 2));
        Ui.Press(dialog, "alt DOWN");
        Ui.Click(RowView(commits, 0), new Point(300, ListRow.Height / 2));
        Ui.Press(dialog, "E");
        dialog.Chosen = dialog.RowOf("Thrice");
        Ui.Click(dialog.MessageBox);
        dialog.MessageBox.SelectAll();
        dialog.KeyTextInput("Thrice, reworded");
        Assert.Equal(["Notes", "Twice", "Thrice"], dialog.Rows.Select(r => r.Step.Commit.Subject));
        Assert.Equal([true, false, false], dialog.History.Where(r => r.Made is not null).Select(r => r.Made!.Stops));
        string thrice = dialog.RowOf("Thrice").Step.Commit.Short;
        Ui.WaitFor(() => dialog.History.Any(r => r.Stops is not null),
            () => $"the preview's stops for conflicts, where its rows say {string.Join(" | ", dialog.History.Select(r => $"{r.Subject}: {r.Stops ?? "no stop"}"))}");
        Assert.Equal([null, "may stop for conflicts", "stops for conflicts"], dialog.History.Where(r => r.Made is not null).Select(r => r.Stops));
        Assert.Equal($"Stops at {thrice} Thrice for conflicts in Shop.cs", dialog.History[2].StopsWhy);

        Ui.Click(dialog.StartButton);
        Ui.WaitFor(() => window.ConflictsWindow is not null, "the Conflicts dialog for the moved commit");
        Assert.EndsWith($", stopped at {thrice} Thrice", window.ConflictsWindow!.Description, StringComparison.Ordinal);
        window.ConflictsWindow!.Close();
        Ui.Settle();
        Assert.Equal((MergeOperation.Rebase, (RebaseStop?)null), (window.Vcs.Merge.Operation, window.Vcs.Merge.Stop));
        Assert.True(window.Perform(AppActions.SkipRebase), window.Status);
        Ui.Landed(() => !window.Vcs.IsBusy && window.Vcs.Merge.Stop is { Step: RebaseStep.Edit }, "the rebase stopped at the planned edit");
        Assert.Contains("stopped to edit", window.Status, StringComparison.Ordinal);
        Assert.Contains("Continue Rebase goes on", window.Actions.WhyNot(AppActions.SkipRebase, window.Actions.Context())?.Shown, StringComparison.Ordinal);
        Assert.True(window.Perform(AppActions.ContinueRebase), window.Status);
        Ui.Landed(() => !window.Vcs.IsBusy && window.Vcs.Merge.Operation is MergeOperation.None, "the rebase's end");
        Assert.Equal(["Notes", "Twice", "First"], Git("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("count += 2", File.ReadAllText(Path.Combine(folder, "Shop.cs")), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>New Tag… from a commit of the log makes an annotated tag signed with the repository's SSH key and pushes it,
    /// the dialog saying so in words and in git's before anything runs; Move Tag… from the tag's menu moves it to HEAD and
    /// pushes it in place of the remote's; a tag whose remote has its own of the name asks before replacing it; and Delete
    /// on Remote… then Delete take it off the remote and out of the repository, each saying what it means for others.</summary>
    private void A_tag_made_from_the_log_annotated_and_signed_is_pushed_moved_and_deleted_here_and_on_the_remote_as_its_preview_said()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string origin = TestFolders.Create("tag-origin-");
        string keys = TestFolders.Create("tag-keys-");
        try
        {
            string key = Ui.AwaitPumped(Waiting.Apart(() => TestSshKeys.MakeAsync(keys, "tagger")), "the key tags are signed with", TimeSpan.FromMinutes(1));
            File.WriteAllText(Path.Combine(keys, "allowed_signers"), $"tests@passenger.invalid {TestSshKeys.Public(key)}\n");
            Git("config", GitConfigKeys.GpgFormat, "ssh");
            Git("config", GitConfigKeys.UserSigningKey, key + ".pub");
            Git("config", GitConfigKeys.GpgSshAllowedSignersFile, Path.Combine(keys, "allowed_signers"));
            TestGit.Run(origin, "init", "-q", "--bare", "-b", "main");
            Git("remote", "add", "origin", origin);
            File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
            Git("commit", "-q", "-am", "Twice");
            string first = Git("rev-parse", "HEAD~1").Trim();
            string twice = Git("rev-parse", "HEAD").Trim();
            MainWindow window = Open();
            window.Perform(AppActions.ShowGit);
            GitLogView log = window.GitLog;
            Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == "First"), "the log");
            log.Select(log.Rows.First(r => r.Commit.Subject == "First").Commit);
            Ui.ChooseFromMenu(Find(log, log.Rows), "New Tag…");
            Ui.WaitFor(() => window.TagWindow is { IsVisible: true }, "the New Tag dialog");
            TagDialog dialog = DialogButtonsTests.Check(window.TagWindow!);
            Assert.Equal(first, dialog.AtBox.Text);
            Ui.Settle();
            Assert.True(dialog.Problem.Length == 0, $"New Tag said why it will not make the tag before anything was typed: {dialog.Problem}");
            dialog.KeyTextInput("v1.0");
            Ui.Click(dialog.KindBox[true]);
            Ui.Click(dialog.MessageBox);
            dialog.KeyTextInput("Release one");
            Ui.Click(dialog.SignBox);
            dialog.PushBox.SelectedIndex = 1;
            Ui.Settle();
            string at = $"{GitHashes.Short(first)} (First)";
            Assert.Equal(
                [
                    $"Makes an annotated tag v1.0 at {at}.",
                    "Keeps the message, you as its tagger and the time with it; signs it with SSH key tagger.pub, so whoever fetches it can check it is yours.",
                    "Pushes it to origin, where whoever fetches from origin gets it.",
                ], dialog.WhatItDoes);
            Assert.Equal($"git tag -a -s -m \"…\" v1.0 {GitHashes.Short(first)}\ngit push origin v1.0", dialog.GitWords);
            Ui.Click(dialog.CreateButton);
            Ui.Landed(() => window.TagWindow is null && !window.Vcs.IsBusy, "the tag made and pushed");
            Assert.Equal($"Tagged {GitHashes.Short(first)} as v1.0, annotated and signed and pushed it to origin, where whoever fetches gets it", window.Status);
            Assert.Equal(first, TestGit.Run(origin, "rev-parse", "v1.0^{commit}").Trim());
            Assert.Equal("Release one", Git("tag", "-l", "--format=%(contents:subject)", "v1.0").Trim());
            Git("tag", "-v", "v1.0");

            BranchesPanel panel = window.GitBranches;
            ListBox branches = Find(panel, panel.Rows);
            Ui.Landed(() => panel.Select("v1.0"), "the tag in the branches panel");
            Ui.ChooseFromMenu(branches, "Move Tag…");
            Ui.WaitFor(() => window.TagWindow is { IsVisible: true }, "the Move Tag dialog");
            dialog = DialogButtonsTests.Check(window.TagWindow!);
            Ui.Landed(() => dialog.MessageBox.Text == "Release one", "the tag's own message to keep");
            Assert.True(dialog.KindBox.Choice, "Move Tag… of an annotated tag offered to make it lightweight");
            dialog.PushBox.SelectedIndex = 1;
            Ui.Settle();
            Assert.Equal(
                [
                    $"Moves v1.0 from {at} to {GitHashes.Short(twice)} (Twice).",
                    "Keeps the message, you as its tagger and the time with it.",
                    "Pushes it to origin in place of the v1.0 there, if it has one: whoever fetched that one keeps it until they fetch tags again with --force.",
                ], dialog.WhatItDoes);
            Assert.Equal("Move Tag", dialog.CreateButton.Content);
            Ui.Click(dialog.CreateButton);
            Ui.Landed(() => window.TagWindow is null && !window.Vcs.IsBusy, "the tag moved and pushed");
            Assert.Equal($"Moved v1.0 from {GitHashes.Short(first)} to {GitHashes.Short(twice)} here and on origin: whoever fetched it before keeps it where it was", window.Status);
            Assert.Equal(twice, TestGit.Run(origin, "rev-parse", "v1.0^{commit}").Trim());

            Git("tag", "v2", first);
            Git("push", "-q", "origin", "v2");
            Git("tag", "-f", "v2", twice);
            List<string> asked = [];
            window.Confirm = (title, _, _) =>
            {
                asked.Add(title);
                return Task.FromResult(true);
            };
            window.Perform(AppActions.GitLogRefresh);
            Ui.Landed(() => panel.Select("v2"), "the second tag in the branches panel");
            Ui.ChooseFromMenu(branches, "Push…");
            Ui.Landed(() => asked.Count == 1 && !window.Vcs.IsBusy, "the question whether to replace origin's own v2");
            Assert.Equal("Replace origin's Tag v2", asked[0]);
            Assert.Equal(twice, TestGit.Run(origin, "rev-parse", "v2^{commit}").Trim());

            Assert.True(panel.Select("v1.0"));
            Ui.ChooseFromMenu(branches, "Delete on Remote…");
            Ui.Landed(() => asked.Count == 2 && !window.Vcs.IsBusy, "the tag deleted on origin");
            Assert.Equal("Delete Tag on Remote", asked[1]);
            Assert.DoesNotContain("v1.0", TestGit.Run(origin, "tag").Split('\n'));
            Assert.Contains("v1.0", Git("tag").Split('\n'));
            Ui.ChooseFromMenu(branches, "Delete");
            Ui.Landed(() => !Git("tag").Split('\n').Contains("v1.0") && !window.Vcs.IsBusy, "the tag deleted here");
            Assert.StartsWith("Deleted tag v1.0 here: a remote that has it keeps it", window.Status, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(origin);
            TestFolders.Delete(keys);
        }
    }

    /// <summary>New Worktree from a branch's menu checks it out in a folder beside the repository, saying so first, and opens
    /// it in a window of its own, whose Commit window commits on that branch, which this window's log then shows and its
    /// branches say is checked out there, Checkout greyed saying so; Remove Worktree…, refused while a window shows it, asks
    /// first once it is closed, naming the change going to the Trash with its folder, and git forgets it, the branch kept.</summary>
    private void A_worktree_made_from_a_branchs_menu_opens_in_its_own_window_is_committed_from_and_removed_once_asked()
    {
        Git("branch", "feature");
        string beside = Worktrees.Beside(PathNames.Canonical(folder), "feature");
        try
        {
            MainWindow window = Open();
            window.Perform(AppActions.ShowGit);
            BranchesPanel panel = window.GitBranches;
            ListBox rows = Find(panel, panel.Rows);
            Ui.Landed(() => panel.Select("feature"), "feature in the branches panel");
            Ui.ChooseFromMenu(rows, "New Worktree from 'feature'…");
            Ui.WaitFor(() => window.WorktreeWindow is { IsVisible: true }, "the New Worktree dialog");
            WorktreeDialog dialog = DialogButtonsTests.Check(window.WorktreeWindow!);
            Ui.Settle();
            Assert.False(dialog.KindBox.Choice, "feature, checked out nowhere, was not offered as the branch to check out");
            Assert.Equal(beside, dialog.FolderBox.Text);
            Assert.Equal(
                [
                    $"Checks feature out in {Path.GetFileName(beside)}, a folder beside this repository's: a second working tree of this repository, with files and staged changes of its own, sharing every commit, branch, tag and stash with this one.",
                    "While feature is checked out there, no other worktree can check it out: git keeps a branch in one worktree at a time.",
                    "Opens it in a new window, trusted as this repository is.",
                ], dialog.WhatItDoes);
            Assert.Equal($"git worktree add ../{Path.GetFileName(beside)} feature", dialog.GitWords);
            Ui.Click(dialog.CreateButton);
            Ui.Landed(() => window.WorktreeWindow is null && PassengerApp.Windows.Any(w => w.Folder is { } shown && PathNames.Same(shown, beside)), "the worktree's own window");
            MainWindow other = PassengerApp.Windows.Single(w => w.Folder is { } shown && PathNames.Same(shown, beside));
            Ui.Landed(() => other.Vcs.Branch == "feature", "the worktree's window on feature");

            File.WriteAllText(Path.Combine(beside, "Shop.cs"), Original.Replace("count++", "count += 3", StringComparison.Ordinal));
            other.Perform(AppActions.Commit);
            Ui.Landed(() => other.CommitView.Chosen.Any(c => c.Name == "Shop.cs"), "the worktree's change offered to commit");
            Ui.Landed(() => other.FocusManager?.GetFocusedElement() is TextBox, "the commit message to have the keys");
            other.KeyTextInput("Feature");
            Ui.Press(other, OperatingSystem.IsMacOS() ? "meta Enter" : "control Enter");
            Ui.Landed(() => Git("log", "-1", "--format=%s", "feature").Trim() == "Feature", "the commit on feature, made from the worktree's window");
            Assert.Equal("main", window.Vcs.Branch);
            Ui.Landed(() => window.GitLog.Rows.Any(r => r.Commit.Subject == "Feature"), "the worktree's commit in this window's log");

            Ui.Landed(() => panel.Branches.Any(b => b.Name == "feature" && b.Worktree is not null), "feature read as checked out in the worktree");
            MenuItem checkout = panel.Menu(panel.Branches.Single(b => b.Name == "feature")).OfType<MenuItem>().Single(i => i.Header as string == "Checkout");
            Ui.Landed(() => !checkout.IsEnabled, "Checkout of feature greyed");
            Assert.Contains("is checked out in the worktree", ToolTip.GetTip(checkout) as string, StringComparison.Ordinal);
            WorktreeInfo made = panel.Worktrees.Single(w => PathNames.Same(PathNames.Canonical(w.Folder), beside));
            MenuItem refused = panel.WorktreeMenu(made).OfType<MenuItem>().Single(i => i.Header as string == "Remove Worktree…");
            Assert.False(refused.IsEnabled, "Remove Worktree… was offered while a window showed the worktree");

            File.WriteAllText(Path.Combine(beside, "Notes.txt"), "notes\n");
            other.Close();
            Ui.Settle();
            List<string> asked = [];
            window.Confirm = (_, text, _) =>
            {
                asked.Add(text);
                return Task.FromResult(true);
            };
            Ui.Click(Ui.Row(rows, panel.Rows.Single(r => r.Worktree is { } w && PathNames.Same(PathNames.Canonical(w.Folder), beside))));
            Ui.ChooseFromMenu(rows, "Remove Worktree…");
            Ui.Landed(() => !Directory.Exists(beside) && !window.Vcs.IsBusy, "the worktree's folder moved to the Trash");
            Assert.Contains("1 file changed there that no commit holds goes to the Trash with it", Assert.Single(asked), StringComparison.Ordinal);
            string trashed = Path.Combine(trash, Path.GetFileName(beside));
            Assert.Equal("notes\n", File.ReadAllText(Path.Combine(trashed, "Notes.txt")));
            Assert.StartsWith($"Moved to the Trash, as {PathNames.Shown(trashed, null)}, the folder of the worktree {Path.GetFileName(beside)}; feature and its commits stay",
                window.Status, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetFileName(beside), Git("worktree", "list"), StringComparison.Ordinal);
            Assert.Contains("feature", Git("branch", "--list", "feature"), StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(beside);
        }
    }

    /// <summary>The Git log open, the commit with <paramref name="subject"/> chosen and Interactively Rebase from Here taken
    /// from its menu: the dialog it opens.</summary>
    private static InteractiveRebaseDialog PlanRebase(MainWindow window, string subject)
    {
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == subject), "the log");
        log.Select(log.Rows.First(r => r.Commit.Subject == subject).Commit);
        Ui.ChooseFromMenu(Find(log, log.Rows), "Interactively Rebase from Here…");
        Ui.WaitFor(() => window.InteractiveRebaseWindow is { IsVisible: true }, "the Interactive Rebase dialog");
        return DialogButtonsTests.Check(window.InteractiveRebaseWindow!);
    }

    private static Control RowView(ListBox list, int index) => list.ContainerFromIndex(index) ?? throw new InvalidOperationException($"No row {index} is drawn");

    /// <summary>A point of a row, past its box, in the dialog's units: <paramref name="down"/> of the way down it.</summary>
    private static Point RowPoint(Window dialog, ListBox list, int index, double down)
    {
        Control row = RowView(list, index);
        return row.TranslatePoint(new Point(300, row.Bounds.Height * down), dialog) ?? throw new InvalidOperationException($"Row {index} is not in the dialog");
    }

    /// <summary>A commit's file in the Git log offers what a changed file in the Commit window does: its diff, its file
    /// now, its path.</summary>
    private void A_commits_file_in_the_log_jumps_to_its_source_and_copies_its_path_from_its_menu()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        File.WriteAllText(shop, Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        File.WriteAllText(Path.Combine(folder, "notes.md"), "notes\nmore notes\n");
        Git("add", "notes.md");
        Git("commit", "-q", "-m", "Notes");
        MainWindow window = Open();
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == "Twice") && log.Rows.Any(r => r.Commit.Subject == "Notes"), "the log");
        log.Select(log.Rows.First(r => r.Commit.Subject == "Twice").Commit);
        Ui.Landed(() => log.Files.Count == 1, "the commit's file");
        Avalonia.Controls.ListBox files = Find(log, log.Files);
        files.SelectedItem = log.Files[0];
        window.OpenFile(Path.Combine(folder, "notes.md"));
        Ui.Settle();
        Ui.ChooseFromMenu(files, "Copy Path");
        Ui.WaitFor(() => Ui.Clipboard(window) == PathNames.Canonical(shop), "the file's path on the clipboard");
        Ui.ChooseFromMenu(files, "Go to Source");
        Assert.Equal(PathNames.Canonical(shop), window.EditorArea.Active?.Path);

        // The file a commit added, in a diff's window: no line of the parent's, saying so, and every line of it added.
        window.ApplySettings(window.Settings with { OpenDiffsIn = Passenger.App.Settings.DiffPlace.Window });
        CommitInfo notes = log.Rows.First(r => r.Commit.Subject == "Notes").Commit;
        log.Select(notes);
        Ui.Landed(() => log.Files is [{ Kind: ChangeKind.Added }], "the file the Notes commit added");
        DiffWindow added = Assert.IsType<DiffWindow>(Ui.Landed(window.ShowCommitDiffAsync(notes, log.Files[0]), "the added file's diff"));
        OpenDiffs.ShowsNoFile(added.View, MergeSide.Left);
        added.Close();
        window.Close();
    }

    private void Open_files_are_saved_before_a_checkout_or_a_rollback_and_take_what_git_puts_there_with_no_question()
    {
        File.WriteAllText(Path.Combine(folder, "Notes.cs"), "class Notes { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Notes");
        Git("switch", "-q", "-c", "other");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 5", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Fives");
        Git("switch", "-q", "main");
        MainWindow window = Open();
        int asked = 0;
        window.AskConflict = _ => Task.FromResult<bool?>(asked++ < 0);
        window.OpenFile(Path.Combine(folder, "Notes.cs"));
        Ui.Settle();
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("sealed ");
        Assert.Equal("main", window.Vcs.Branch);

        Task<VcsOutcome> checkout = window.Vcs.CheckoutAsync("other");
        VcsOutcome checkedOut = Ui.Landed(checkout, "the checkout done");
        Assert.True(checkedOut.Succeeded, checkedOut.Message);
        Assert.Equal("sealed class Notes { }\n", File.ReadAllText(Path.Combine(folder, "Notes.cs")));
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Landed(() => Text(window).Contains("count += 5", StringComparison.Ordinal), "the other branch's text");
        Assert.False(window.EditorArea.Active!.IsModified);

        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("// saved\n");
        window.Perform(AppActions.Save);
        window.KeyTextInput("// unsaved\n");
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "Shop.cs"), "Shop.cs listed as changed");
        Task<VcsOutcome> rollback = window.Vcs.RollbackAsync([.. window.Vcs.Changes.Where(c => c.Name == "Shop.cs")]);
        Ui.Landed(() => rollback.IsCompleted, "the rollback done");
        string head = Git("show", "HEAD:Shop.cs");
        Assert.Equal(head, File.ReadAllText(Path.Combine(folder, "Shop.cs")));
        Ui.Landed(() => Text(window) == head, "the editor at HEAD");
        Assert.Equal(0, asked);
        window.Close();
    }

    /// <summary>A submodule whose own files alone changed (a file it does not track put in it) has nothing to commit here:
    /// the Commit window lists it with no box, saying why, and leaves it out; a commit of it is refused, and Rollback leaves
    /// it as it is, each saying why. Once its commit moves it is offered and committed, and its commit moved again is rolled
    /// back by checking the recorded one out in it, the file it does not track kept.</summary>
    private void A_submodule_whose_own_files_alone_changed_is_not_offered_for_commit_and_one_whose_commit_moved_is()
    {
        string library = TestFolders.Create("git-library-");
        File.WriteAllText(Path.Combine(library, "Lib.cs"), "class Lib { }\n");
        TestGit.Init(library);
        Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", library, "lib");
        Git("commit", "-q", "-m", "Lib");
        string lib = Path.Combine(folder, "lib");
        File.WriteAllText(Path.Combine(lib, "Notes.txt"), "notes\n");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        const string Why = "lib is a submodule: its own untracked files are committed in it, not here";

        MainWindow window = Open();
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "lib"), "lib listed as changed");
        CommitRow content = commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "lib");
        Assert.False(content.CanChoose, "lib, with only a file it does not track, is offered for commit");
        Assert.Equal(Why, content.Change.Uncommittable);
        Assert.Equal(["Shop.cs"], commit.Chosen.Select(c => c.Name));
        Assert.Contains(commit.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Why && t.IsEffectivelyVisible);

        CommitResult refused = Ui.Landed(window.Vcs.CommitAsync([content.Change], "Lib's notes", amend: false, push: false), "the commit's answer");
        Assert.Equal(Why, refused.Commit.Message);
        VcsOutcome left = Ui.Landed(window.Vcs.RollbackAsync([content.Change]), "the rollback's answer");
        Assert.Equal("lib was left as it is: a submodule's own changes are rolled back in it", left.Message);
        Assert.True(File.Exists(Path.Combine(lib, "Notes.txt")), "Rollback deleted the submodule's own file");

        Library("Lib's own");
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "lib" && r.CanChoose && r.IsChosen), "lib offered and chosen once its commit moved");
        commit.Message = "Lib moved";
        Assert.True(Ui.Landed(commit.CommitAsync(push: false), "the commit"), window.Status);
        string moved = TestGit.Run(lib, "rev-parse", "HEAD").Trim();
        Assert.Equal(moved, Git("rev-parse", "HEAD:lib").Trim());
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().SingleOrDefault() is { CanChoose: false, Change.Name: "lib" }, "lib alone listed again, its untracked file left");

        Library("Again");
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "lib" && r.CanChoose), "lib offered once its commit moved again");
        VcsOutcome back = Ui.Landed(window.Vcs.RollbackAsync([commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "lib").Change]), "the rollback's answer");
        Assert.True(back.Succeeded, back.Message);
        Assert.Equal(moved, TestGit.Run(lib, "rev-parse", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(lib, "Notes.txt")), "Rollback deleted the submodule's own file");
        window.Close();

        // A commit in the submodule, whose clone has no author of its own, then the window told of it.
        void Library(string message)
        {
            TestGit.Run(lib, "-c", "user.name=Passenger Tests", "-c", "user.email=tests@passenger.invalid", "commit", "-q", "--allow-empty", "-m", message);
            Task reading = window.Vcs.RefreshAsync();
            Ui.Landed(() => reading.IsCompleted, "Git read again");
        }
    }

    private void A_file_name_with_brackets_is_that_file_alone_to_roll_back_and_to_commit()
    {
        File.WriteAllText(Path.Combine(folder, "[ab].cs"), "class Ab { }\n");
        File.WriteAllText(Path.Combine(folder, "a.cs"), "class A { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Brackets");
        File.WriteAllText(Path.Combine(folder, "[ab].cs"), "class Ab { int x; }\n");
        File.WriteAllText(Path.Combine(folder, "a.cs"), "class A { int x; }\n");
        MainWindow window = Open();
        Ui.Landed(() => window.Vcs.Changes.Count == 2, "both files listed as changed");

        Task<VcsOutcome> rollback = window.Vcs.RollbackAsync([.. window.Vcs.Changes.Where(c => c.Name == "[ab].cs")]);
        Ui.Landed(() => rollback.IsCompleted, "the rollback done");
        Assert.Equal("class Ab { }\n", File.ReadAllText(Path.Combine(folder, "[ab].cs")));
        Assert.Equal("class A { int x; }\n", File.ReadAllText(Path.Combine(folder, "a.cs")));

        File.WriteAllText(Path.Combine(folder, "[ab].cs"), "class Ab { int y; }\n");
        Ui.WaitFor(() => window.Vcs.Changes.Count == 2, "both files changed again");
        Task<CommitResult> commit = window.Vcs.CommitAsync([.. window.Vcs.Changes.Where(c => c.Name == "[ab].cs")], "Only brackets", amend: false, push: false);
        CommitResult made = Ui.Landed(commit, "the commit made");
        Assert.True(made.Succeeded, made.Commit.Message);
        Assert.Equal("[ab].cs", Git("show", "--name-only", "--format=", "HEAD").Trim());
        window.Close();
    }

    /// <summary>Windows refuses a save while another program holds the file sharing neither writes nor its deletion, and
    /// a commit is made at once: the commit waits for the save to land, saying so, and commits the saved text, never the
    /// file as it was on disk.</summary>
    private void A_commit_made_while_a_held_files_save_lands_waits_for_it_and_commits_the_saved_text()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace or write a file another program holds");
        string shop = Path.Combine(folder, "Shop.cs");
        File.WriteAllText(shop, "public " + Original);
        MainWindow window = Open();
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "Shop.cs"), "Shop.cs listed as changed");
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("sealed ");
        Ui.Settle();
        Task<CommitResult> commit;
        using (new FileStream(shop, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Ui.Press(window, Ui.Shortcut(AppActions.Save), settle: false);
            Ui.PumpUntil(() => !window.EditorArea.Active!.IsModified, "the window to take the held file's save", TimeSpan.FromSeconds(10));
            commit = window.Vcs.CommitAsync([.. window.Vcs.Changes.Where(c => c.Name == "Shop.cs")], "Sealed", amend: false, push: false);
            Ui.PumpUntil(() => window.Actions.Work.Latest?.Stage.Shown == "Waiting for Shop.cs to be saved…", "the commit to wait for Shop.cs's save", TimeSpan.FromSeconds(10));
        }
        CommitResult made = Ui.Landed(commit, "the commit made once Shop.cs was saved");
        Assert.True(made.Succeeded, made.Commit.Message);
        string committed = Git("show", "HEAD:Shop.cs").ReplaceLineEndings("\n");
        Assert.True(committed == "sealed public " + Original, $"The commit took Shop.cs as it was before the save: '{committed}'");
        window.Close();
    }

    private void A_save_leaves_the_explorers_chosen_rows_the_git_log_and_go_to_file_as_they_were()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Models"));
        File.WriteAllText(Path.Combine(folder, "Models", "Order.cs"), "class Order { }\n");
        File.WriteAllText(Path.Combine(folder, "Models", "Line.cs"), "class Line { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Models");
        MainWindow window = Open();
        window.Perform(AppActions.ShowGit);
        Ui.Landed(() => window.GitLog.Rows.Count == 2, "the log");
        Ui.Landed(() => window.FileIndex.Files.Count == 3, "the files for Go to File");
        // The log may be read once before the window's first look at the repository knows its history, and once after.
        Task known = window.Vcs.RefreshAsync();
        Ui.Landed(() => known.IsCompleted, "the repository's history");
        Passenger.App.Explorer.ExplorerTree tree = window.ExplorerView.Tree;
        tree.Expand(tree.Rows.First(r => r.Name == "Models"));
        Passenger.App.Explorer.ExplorerNode[] chosen = [.. tree.Rows.Where(r => r.Name is "Order.cs" or "Line.cs")];
        window.ExplorerView.Choose(chosen);
        int loads = window.GitLog.Loads;

        window.Perform(AppActions.FocusEditor);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("sealed ");
        window.Perform(AppActions.Save);
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "Shop.cs"), "the save seen");
        Ui.Hold(TimeSpan.FromMilliseconds(400), "long enough for a refresh the save might set off to have run");
        Ui.Settle();

        Assert.Equal(chosen, window.ExplorerView.SelectedNodes);
        Assert.Equal(loads, window.GitLog.Loads);
        Assert.Equal(3, window.FileIndex.Files.Count);
        Assert.Empty(Directory.EnumerateFiles(folder, "*" + FileText.SaveSuffix, SearchOption.AllDirectories));
        window.Close();
    }

    /// <summary>A save among 5,000 tags costs the UI thread what one among 5 does, the least of three saves each: Git's
    /// refs are not read for a save, which leaves the history where it was.</summary>
    private void A_save_among_five_thousand_tags_allocates_on_the_ui_thread_what_one_among_five_does_and_reads_no_ref()
    {
        string many = PathNames.Canonical(TestFolders.Create("git-tags-"));
        try
        {
            TestFolders.Prepare(() =>
            {
                File.WriteAllText(Path.Combine(many, "Shop.cs"), Original);
                TestGit.Init(many);
            }, many);
            (long few, int fewReads) = Saves(PathNames.Canonical(folder), 5);
            (long lots, int lotsReads) = Saves(many, 5000);
            TestContext.Current.TestOutputHelper?.WriteLine($"A save allocated {few / 1024:N0} KB on the UI thread among 5 tags, {lots / 1024:N0} KB among 5,000");
            Assert.True(fewReads == 0 && lotsReads == 0, $"Three saves read Git's refs {fewReads} times among 5 tags and {lotsReads} among 5,000, where a save leaves the history as it was");
            Assert.True(lots - few < 32 * 1024,
                $"A save among 5,000 tags allocated {lots / 1024:N0} KB on the UI thread against {few / 1024:N0} KB among 5: a save costs the window the repository's refs");
        }
        finally
        {
            TestFolders.Delete(many);
        }

        // The least a save of Shop.cs allocates on the UI thread from the key to its last read landed, and how often Git's
        // refs were read meanwhile.
        static (long Least, int Reads) Saves(string root, int tags)
        {
            string head = TestGit.Run(root, "rev-parse", "HEAD").Trim();
            string stream = string.Concat(Enumerable.Range(0, tags).Select(i => $"reset refs/tags/tag{i:0000}\nfrom {head}\n\n"));
            ChildResult made = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("git", ["fast-import", "--quiet"], root), stream,
                TimeSpan.FromMinutes(1).Scaled())), "git fast-import", TimeSpan.FromMinutes(1) + TestFolders.Spare);
            Assert.True(made.Succeeded, $"git fast-import made no {tags} tags: {made.Error}");
            MainWindow window = Ui.OpenFolder(root, languageServer: false);
            window.OpenFile(Path.Combine(root, "Shop.cs"));
            window.Perform(AppActions.ShowGit);
            window.Perform(AppActions.FocusEditor);
            Ui.Settle();
            bool heard = false;
            window.FolderWatcher!.Changed += (_, changes) => heard |= changes.Paths.Any(p => Path.GetFileName(p) == "Shop.cs");
            int reads = 0;
            void Started(object? sender, ChildStart start)
            {
                if (start.Command.Contains("for-each-ref", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref reads);
                }
            }
            long least = long.MaxValue;
            ChildProcesses.Started += Started;
            try
            {
                for (int save = 0; save < 3; save++)
                {
                    Ui.Settle();
                    heard = false;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
                    window.KeyTextInput("// ");
                    window.Perform(AppActions.Save);
                    Ui.WaitFor(() => heard, "the folder watcher to hear of the save");
                    Ui.Settle();
                    least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                }
            }
            finally
            {
                ChildProcesses.Started -= Started;
            }
            Assert.Equal(tags, TestGit.Run(root, "tag", "--list").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            window.Close();
            return (least, reads);
        }
    }

    /// <summary>A commit another program makes reaches the log with the window left as it is: the working tree's top open,
    /// as Git's own folder is heard changing; a folder in it open, whose watcher hears nothing of Git's folder, as the
    /// next file is changed there.</summary>
    private void A_commit_made_elsewhere_reaches_the_log_of_the_working_trees_top_and_of_a_folder_in_it()
    {
        Directory.CreateDirectory(Path.Combine(folder, "App"));
        File.WriteAllText(Path.Combine(folder, "App", "App.cs"), "class App { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "App");
        foreach ((string open, string what) in new[] { (folder, "the working tree's top"), (Path.Combine(folder, "App"), "a folder in it") })
        {
            MainWindow window = Ui.OpenFolder(open, languageServer: false);
            window.Perform(AppActions.ShowGit);
            int listed = int.Parse(Git("rev-list", "--count", "HEAD").Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Ui.Landed(() => window.GitLog.Rows.Count == listed, $"the log of {what}");
            string subject = $"Made elsewhere with {what} open";
            Git("commit", "-q", "--allow-empty", "-m", subject);
            File.WriteAllText(Path.Combine(open, "Notes.txt"), $"{subject}\n");
            Ui.WaitFor(() => window.GitLog.Rows.Any(r => r.Commit.Subject == subject),
                () => $"the commit made elsewhere in the log of {what}, which lists {string.Join(", ", window.GitLog.Rows.Select(r => r.Commit.Subject))}");
            window.Close();
        }
    }

    private void A_file_chosen_in_the_commit_window_stays_chosen_through_a_refresh_and_rollback_names_and_rolls_back_it_alone()
    {
        foreach (string name in new[] { "A.cs", "B.cs", "C.cs" })
        {
            File.WriteAllText(Path.Combine(folder, name), $"class {name[0]} {{ }}\n");
        }
        Git("add", "-A");
        Git("commit", "-q", "-m", "Three");
        foreach (string name in new[] { "A.cs", "B.cs", "C.cs", "Shop.cs" })
        {
            File.AppendAllText(Path.Combine(folder, name), "// changed\n");
        }
        MainWindow window = Open();
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Count() == 4, "the four changed files listed");
        Assert.Equal(4, commit.Chosen.Count);
        Avalonia.Controls.ListBox changes = Find(commit, commit.Rows);
        CommitRow a = commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == "A.cs");
        changes.SelectedItem = a;

        Task again = window.Vcs.RefreshAsync();
        Ui.Landed(() => again.IsCompleted, "the status read again");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "new\n");
        Task newer = window.Vcs.RefreshAsync();
        Ui.Landed(() => newer.IsCompleted, "the status read with the new file");
        Assert.Contains(commit.Rows.OfType<CommitRow>(), r => r.Change.Name == "Notes.txt");
        Assert.Same(a, commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == "A.cs"));
        Assert.Equal(["A.cs"], changes.SelectedItems!.OfType<CommitRow>().Select(r => r.Change.Name));

        File.Delete(Path.Combine(folder, "A.cs"));
        Task deleted = window.Vcs.RefreshAsync();
        Ui.Landed(() => deleted.IsCompleted, "the status read with A.cs deleted");
        Assert.Equal([("A.cs", ChangeKind.Deleted)], changes.SelectedItems!.OfType<CommitRow>().Select(r => (r.Change.Name, r.Change.Kind)));

        RollbackChosen(window);
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the Rollback Changes dialog");
        ConfirmDialog dialog = window.OwnedWindows.OfType<ConfirmDialog>().Single();
        string[] shown = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? string.Empty)];
        Assert.Contains("A.cs", shown);
        Assert.DoesNotContain(shown, t => t is "B.cs" or "C.cs" or "Shop.cs" or "Notes.txt");
        Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>().First(b => (string?)b.Content == "Rollback"), settle: false);
        Ui.Landed(() => window.Vcs.Changes.All(c => c.Name != "A.cs"), "A.cs rolled back");
        Assert.Equal("class A { }\n", File.ReadAllText(Path.Combine(folder, "A.cs")));
        Assert.Equal(["B.cs", "C.cs", "Notes.txt", "Shop.cs"], window.Vcs.Changes.Select(c => c.Name).Order(StringComparer.Ordinal));
        window.Close();
    }

    private void Commit_and_stash_are_off_while_one_runs_so_a_second_press_makes_no_second()
    {
        MainWindow window = Open();
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// more\n");
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => commit.Chosen.Any(c => c.Name == "Shop.cs"), "Shop.cs listed");
        commit.Message = "More";
        Ui.Settle();
        Avalonia.Controls.Button[] buttons = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(commit).OfType<Avalonia.Controls.Button>()
            .Where(b => b.Content is "Commit" or "Commit and Push…")];
        Assert.Equal(2, buttons.Length);
        Assert.All(buttons, b => Assert.True(b.IsEnabled));

        // Pressed twice, as a double-click does; the pre-commit hook holds the commit until the second press is made.
        string go = Path.Combine(folder, ".git", "go");
        HoldCommits(go);
        Avalonia.Controls.Button press = buttons.First(b => b.Content is "Commit");
        try
        {
            Ui.Click(press, settle: false);
            Assert.All(buttons, b => Assert.False(b.IsEnabled, "A commit can be pressed again while one runs"));
            // Raised, not clicked: a pointer never reaches a button that is off, and this checks its handler refuses too.
#pragma warning disable RS0030
            press.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
#pragma warning restore RS0030
            Assert.True(window.Status?.StartsWith("Already under way: Committing 1 file", StringComparison.Ordinal) == true,
                $"A second press while the commit runs should be told it is under way, but the status bar says: {window.Status}");
        }
        finally
        {
            File.WriteAllText(go, string.Empty);
        }
        Ui.Landed(() => window.Vcs.Changes.Count == 0 && window.Status?.StartsWith("1 file committed", StringComparison.Ordinal) == true, $"the commit ({window.Status})");
        Assert.Equal("2", Git("rev-list", "--count", "HEAD").Trim());
        // Git reads what the commit did (slower on Windows) before it takes another change; one asked for meanwhile is refused.
        Ui.Landed(() => !window.Vcs.IsBusy, "the commit's end");

        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// stashed\n");
        commit.Message = "Not now";
        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Later";
        Task first = stash.SubmitAsync();
        Task second = stash.SubmitAsync();
        Assert.All(buttons, b => Assert.False(b.IsEnabled, "A commit can be pressed while a stash runs"));
        Ui.Landed(() => first.IsCompleted && second.IsCompleted, "the stash made");
        Assert.Null(stash.Problem);
        Assert.Single(Git("stash", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        window.Close();
    }

    /// <summary>A commit's message file another program opens as git reads it (a scanner, an indexer; here a handle sharing
    /// neither writes nor its deletion, which Windows then refuses) fails nothing: the commit is made and said, and the file
    /// goes once the program lets go of it.</summary>
    private void A_commit_whose_message_file_another_program_holds_is_made_and_the_file_goes_once_let_go()
    {
        string tree = PathNames.Canonical(folder);
        Lock gate = new();
        string? message = null;
        FileStream? holding = null;
        Exception? unheld = null;
        void Heard(object? sender, ChildStart start)
        {
            if (start.Start is not { } given || start.Folder.Length == 0 || PathNames.Canonical(start.Folder) != tree || !given.ArgumentList.Contains("commit")
                || given.ArgumentList.IndexOf("-F") is not (>= 0 and int at) || at + 1 >= given.ArgumentList.Count)
            {
                return;
            }
            lock (gate)
            {
                message = given.ArgumentList[at + 1];
                try
                {
                    holding = new FileStream(message, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch (IOException failed)
                {
                    unheld = failed;
                }
            }
        }
        File.AppendAllText(Path.Combine(tree, "Shop.cs"), "// held\n");
        MainWindow window = Ui.OpenFolder(tree, languageServer: false);
        CommitView commit = window.CommitView;
        window.Perform(AppActions.ShowCommit);
        Ui.WaitFor(() => commit.Chosen.Any(c => c.Name == "Shop.cs"), () => $"Shop.cs listed to commit, where the Commit window lists {string.Join(", ", commit.Chosen.Select(c => c.Name))}");
        commit.Message = "Held a moment";
        ChildProcesses.Started += Heard;
        try
        {
            Ui.ClickButton(commit, "Commit");
            Ui.WaitFor(() => window.Vcs.Changes.Count == 0 && !window.Vcs.IsBusy, () => $"the commit, where the status bar says '{window.Status}'");
        }
        finally
        {
            ChildProcesses.Started -= Heard;
        }
        lock (gate)
        {
            Assert.True(message is not null && unheld is null, $"The commit's message file was not held as git read it: {unheld?.Message ?? "git was given none"}");
        }
        Assert.Equal("1 file committed: Held a moment", window.Status);
        Assert.Equal("Held a moment", Git("log", "-1", "--format=%s").Trim());
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(message), "The message file a program held was deleted under it, where Windows refuses that");
        }
        lock (gate)
        {
            holding!.Dispose();
        }
        Ui.WaitFor(() => !File.Exists(message), () => $"the commit's message file {message} to be deleted once let go");
        window.Close();
    }

    private void A_commit_whose_hook_leaves_a_job_holding_gits_output_is_done_within_seconds()
    {
        // A post-commit hook that leaves a job running, as `daemon &` does, holding git's error output while it lives.
        string job = Path.Combine(folder, ".git", "job");
        string hook = Path.Combine(folder, ".git", "hooks", "post-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'watching the build'\n(cd / && exec sleep 300) &\necho $! > .git/job\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        MainWindow window = Open();
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// more\n");
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => commit.Chosen.Any(c => c.Name == "Shop.cs"), "Shop.cs listed");
        commit.Message = "More";
        Task<bool> committed = commit.CommitAsync(push: false);
        Assert.True(Ui.Await(committed, "the commit to be done once git had exited, its hook's job still holding git's errors", TimeSpan.FromSeconds(20)), window.Status);
        Assert.Equal("More", Git("log", "-1", "--format=%s").Trim());
        Ui.Landed(() => !window.Vcs.IsBusy && window.Vcs.Changes.Count == 0, $"the commit's end, with nothing left to commit ({window.Status})");
        // Ended with git's process group; Windows' git hooks run under MSYS, whose ids are its own.
        int left = Processes.ReadId(job);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(left > 0, "The hook never ran: it wrote no job's id");
            Assert.True(Processes.Gone(left), $"The job the hook left ({Processes.Describe(left)}) outlived the commit that ran it");
        }
        window.Close();
    }

    private void Coming_back_to_the_window_reads_no_last_commit_text_again_and_head_moved_elsewhere_moves_every_files_bars_in_one_read()
    {
        // Prices.cs has characters of more than a byte before its change, and New.cs, open between the others, no commit.
        const string Prices = "// Prix en €, café crème 😀\nclass Prices\n{\n    decimal euro = 1m;\n}\n";
        File.WriteAllText(Path.Combine(folder, "Prices.cs"), Prices);
        File.WriteAllText(Path.Combine(folder, "A.cs"), "class A { }\n");
        File.WriteAllText(Path.Combine(folder, "B.cs"), "class B { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "More");
        File.WriteAllText(Path.Combine(folder, "Prices.cs"), Prices.Replace("1m", "2m", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Dearer");
        File.WriteAllText(Path.Combine(folder, "New.cs"), "class New { }\n");
        MainWindow window = Open();
        foreach (string name in new[] { "Prices.cs", "A.cs", "New.cs", "B.cs" })
        {
            window.OpenFile(Path.Combine(folder, name));
        }
        Ui.Settle();
        Task opened = window.Vcs.RefreshAsync();
        Ui.Landed(() => opened.IsCompleted && window.Vcs.Branch == "main", "the working tree read");

        int reads = window.Vcs.HeadReads;
        Task back = window.Vcs.RefreshAsync();
        Ui.Landed(() => back.IsCompleted, "the status read again");
        Assert.True(window.Vcs.HeadReads == reads, $"Coming back to the window read last-commit texts {window.Vcs.HeadReads - reads} times though HEAD had not moved");

        // HEAD moved back a commit, as in a terminal.
        Git("reset", "-q", "--soft", "HEAD~1");
        Ui.WaitFor(() => Bars(window, "Shop.cs").Count > 0 && Bars(window, "Prices.cs").Count > 0, "the bars against the commit HEAD moved to");
        Assert.Equal([new ChangeMark(2, 1, ChangeMarkKind.Modified)], Bars(window, "Shop.cs"));
        Assert.Equal([new ChangeMark(3, 1, ChangeMarkKind.Modified)], Bars(window, "Prices.cs"));
        Assert.All(new[] { "A.cs", "B.cs", "New.cs" }, name => Assert.Empty(Bars(window, name)));
        Assert.True(window.Vcs.HeadReads == reads + 1, $"HEAD moving read the five open files' texts in {window.Vcs.HeadReads - reads} reads, not one");
        window.Close();
    }

    private static IReadOnlyList<ChangeMark> Bars(MainWindow window, string name) =>
        window.EditorArea.Documents.First(d => d.Name == name).ChangeMarks;

    /// <summary>A path as the hook's shell and git's configuration take it on every system.</summary>
    private static string Unix(string path) => path.Replace('\\', '/');

    private static Avalonia.Controls.ListBox Find(Avalonia.Controls.Control view, object rows) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.ListBox>().First(l => ReferenceEquals(l.ItemsSource, rows));

    /// <summary>The Git keys of the keymap in force: VCS Operations lists the Git menu's actions with their keys and runs the
    /// one chosen (Commit…, showing the Commit window); Commit and Push there commits the change and pushes it to the remote;
    /// New Branch's box makes a branch from the head and checks it out.</summary>
    private void Vcs_operations_commit_and_push_and_new_branch_run_from_their_keys()
    {
        string remote = TestFolders.Create("git-remote-");
        TestGit.Run(remote, "init", "-q", "--bare", "-b", "main");
        Git("remote", "add", "origin", remote);
        Git("push", "-q", "-u", "origin", "main");
        MainWindow window = Open();
        try
        {
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, Ui.Shortcut(AppActions.VcsOperations));
            Passenger.App.Vcs.ChoicePopup operations = Assert.IsType<Passenger.App.Vcs.ChoicePopup>(window.Popup);
            Assert.True(operations.Rows.FirstOrDefault()?.Label.English == "Commit…" && operations.Rows.Any(r => r.Label.English == "Commit and Push…" && r.Detail.English.Length > 0),
                $"VCS Operations listed [{string.Join("; ", operations.Rows.Select(r => $"{r.Label} {r.Detail}"))}], not the Git menu's actions with their keys");
            Ui.Press(window, "ENTER");
            Ui.Settle();
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Commit), "Commit… chosen in VCS Operations left the Commit window hidden");

            File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 3", StringComparison.Ordinal));
            Assert.True(window.Perform(AppActions.CommitRefresh), window.Status);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Shop.cs"), "Shop.cs changed in the Commit window");
            window.CommitView.Message = "Three at a time";
            window.CommitView.FocusMessage();
            Ui.Settle();
            Ui.Press(window, Ui.Shortcut(AppActions.CommitAndPush));
            Ui.Landed(() => TestGit.Run(remote, "log", "-1", "--format=%s", "main").Trim() == "Three at a time", "the commit pushed to the remote");

            Ui.Press(window, Ui.Shortcut(AppActions.NewBranch));
            NamePopup branch = Assert.IsType<NamePopup>(window.Popup);
            branch.Text = "feature/keys";
            Task submitted = branch.SubmitAsync();
            Ui.Landed(() => submitted.IsCompleted && window.Vcs.Branch == "feature/keys", "the new branch checked out");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(remote);
        }
    }

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Settle();
        return window;
    }

    /// <summary>Lets the window's background work land and finds Git listing nothing changed; failing, it says what is
    /// still listed, whether Git is still busy, what the status bar says (git's own words when a rollback failed) and what
    /// git finds on disk.</summary>
    private void AssertNothingChanged(MainWindow window)
    {
        Ui.Settle();
        string listed = string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"));
        Assert.True(window.Vcs.Changes.Count == 0, $"Once its work had landed Git still listed {listed} and was {(window.Vcs.IsBusy ? "still busy" : "idle")}, "
            + $"the status bar said \"{window.Status}\" and git status says \"{Git("status", "--porcelain").Trim()}\"");
    }

    /// <summary>Gives the repository a pre-commit hook that holds each commit until <paramref name="go"/> is there, a minute
    /// at most, so a commit is surely under way while a test acts.</summary>
    private void HoldCommits(string go)
    {
        string hook = Path.Combine(folder, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        // Git runs a hook in the working tree's top folder, so the path from there is the hook's on every system.
        string wait = Path.GetRelativePath(folder, go).Replace('\\', '/');
        File.WriteAllText(hook, $"#!/bin/sh\ni=0\nwhile [ ! -f '{wait}' ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i + 1)); done\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Presses the Commit window's Rollback, as the person would, the Commit window shown first.</summary>
    private static void RollbackChosen(MainWindow window)
    {
        window.ShowToolWindow(ToolWindowIds.Commit);
        Ui.Settle();
        Avalonia.Controls.Button rollback = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.CommitView)
            .OfType<Avalonia.Controls.Button>()
            .First(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Rollback");
        Ui.Click(rollback, settle: false);
    }

    /// <summary>Shows the open file's diff, typed in on its right side, and closes it: the diff gone, held by nothing here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<DiffView> DiffAndClose(MainWindow window)
    {
        Passenger.App.Workspace.OpenDocument file = window.EditorArea.Active!;
        Task<IDiffHost?> opening = window.ShowDiffAsync(file.Path);
        IDiffHost diff = Ui.Landed(opening, "the diff open")!;
        DiffView view = OpenDiffs.ViewOf(diff);
        Assert.Same(file.Text, view.Right.Document);
        view.Right.Focus();
        OpenDiffs.KeysOf(diff).KeyTextInput("// ");
        Ui.Landed(() => view.Hunks.Count == 1, "the difference typed");
        diff.Close();
        Ui.Settle();
        OpenDiffs.None(window, "Closed, the diff stayed");
        return new WeakReference<DiffView>(view);
    }

    private static IReadOnlyList<ChangeMark> Marks(MainWindow window) => window.EditorArea.Active?.ChangeMarks ?? [];

    private static string Text(MainWindow window) => window.EditorArea.Active!.Text.Snapshot.ToString();

    /// <summary>Whether colours give the word at an offset a field's colour.</summary>
    private static bool Field(IHighlighter? colours, TextSnapshot text, int at) =>
        colours?.GetSpans(text, at, 1).Any(s => s.Start == at && s.Kind == TokenKind.Field) == true;

    /// <summary>What each stash says, the newest first.</summary>
    private string[] Stashes() => Git("stash", "list", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
