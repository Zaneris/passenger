using System.Diagnostics;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Changes another program makes in the folder, seen at once: the Explorer, Go to File and the open files
/// follow, an open file with unsaved edits asks which version to keep, a project added on disk joins the Solution view
/// and the run configurations, and the solution the folder's files now pick is the one the Explorer and the language
/// server both open. Linux's way of watching, a watch for each kept folder from a pool with a limit, is chosen here on
/// any system, since only the kernel's own running out is Linux's alone.</summary>
[AboutMacOS]
public sealed class FolderWatchTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("watch-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Files and folders another program makes, changes, moves and deletes in the folder reach the Explorer,
    /// Go to File, Local History and the open tabs, each watched the platform's own way: build output and a
    /// repository's Git folder come and go, a file deleted and written again keeps its tab, lines changed on disk leave
    /// breakpoints, bookmarks and the caret with their own lines, and Go to File lists what a walk of the folder lists,
    /// through links and hidden folders too.</summary>
    [AvaloniaFact]
    public void Files_and_folders_changed_elsewhere_reach_the_explorer_go_to_file_local_history_and_the_open_files()
    {
        Flow.StepApart<FolderWatchTests>("files created changed and deleted elsewhere reach the explorer Go to File and the open tabs", t => t.Files_created_changed_and_deleted_elsewhere_reach_the_explorer_go_to_file_and_the_open_tabs());
        Flow.StepApart<FolderWatchTests>("build output folders made and deleted elsewhere come and go in the explorer", t => t.Build_output_folders_made_and_deleted_elsewhere_come_and_go_in_the_explorer(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("a repository git init makes outside the window is taken and one removed is let go", t => t.A_repository_git_init_makes_outside_the_window_is_taken_and_one_removed_is_let_go(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("a file deleted and written again at once keeps its tab with the new text and one deleted for good closes", t => t.A_file_deleted_and_written_again_at_once_keeps_its_tab_with_the_new_text_and_one_deleted_for_good_closes());
        Flow.StepApart<FolderWatchTests>("lines changed on disk leave breakpoints bookmarks and the caret with their own lines", t => t.Lines_changed_on_disk_leave_breakpoints_bookmarks_and_the_caret_with_their_own_lines());
        Flow.StepApart<FolderWatchTests>("a folder moved in or out hands over each of its files in the batch it comes or goes in", t => t.A_folder_moved_in_or_out_hands_over_each_of_its_files_in_the_batch_it_comes_or_goes_in(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("Go to File follows files and folders come and gone to list what a walk of the folder lists", t => t.Go_to_file_follows_files_and_folders_come_and_gone_to_list_what_a_walk_of_the_folder_lists());
        Flow.StepApart<FolderWatchTests>("Go to File, Local History and the folder's projects see its files alike through links and hidden folders", t => t.Go_to_file_local_history_and_the_folders_projects_see_its_files_alike_through_links_and_hidden_folders());
    }

    /// <summary>An open file's unsaved edits are never lost to what another program does on disk: a change asks which
    /// to keep and nothing is saved over the disk's version while it asks, a stray key answers neither way, a file
    /// deleted stays open and saving writes it back, open files follow their folder renamed and a folder trashed makes
    /// no autosave write it again, an agent's edit there says why it was not saved, and a BOM added on disk survives
    /// the next save.</summary>
    [AvaloniaFact]
    public void An_open_files_unsaved_edits_are_never_lost_to_what_another_program_does_to_it_on_disk()
    {
        Flow.StepApart<FolderWatchTests>("a file with unsaved edits changed on disk asks which to keep", t => t.A_file_with_unsaved_edits_changed_on_disk_asks_which_to_keep());
        Flow.StepApart<FolderWatchTests>("while the conflict question is open nothing is saved over the disk's version", t => t.While_the_conflict_question_is_open_nothing_is_saved_over_the_disks_version());
        Flow.StepApart<FolderWatchTests>("a key meant for the editor answers the conflict question neither way and the next save asks again", t => t.A_key_meant_for_the_editor_answers_the_conflict_question_neither_way_and_the_next_save_asks_again());
        Flow.StepApart<FolderWatchTests>("a file with unsaved edits deleted on disk stays open and saving writes it back", t => t.A_file_with_unsaved_edits_deleted_on_disk_stays_open_and_saving_writes_it_back());
        Flow.StepApart<FolderWatchTests>("open files follow their folder renamed elsewhere and their saves never make the old folder again", t => t.Open_files_follow_their_folder_renamed_elsewhere_and_their_saves_never_make_the_old_folder_again(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("a folder trashed elsewhere closes its open files and no autosave makes it again", t => t.A_folder_trashed_elsewhere_closes_its_open_files_and_no_autosave_makes_it_again(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("an agent's edit of a file whose folder has gone says why it was not saved", t => t.An_agents_edit_of_a_file_whose_folder_has_gone_says_why_it_was_not_saved());
        Flow.StepApart<FolderWatchTests>("a BOM added on disk or a conversion to UTF-8 survives the next save without a question", t => t.A_BOM_added_on_disk_or_a_conversion_to_UTF_8_survives_the_next_save_without_a_question());
    }

    /// <summary>Changes the platform's watcher lost (an error, an overflow, nothing heard, Git's watch failing) are
    /// found again, as the watcher looks again or the window comes in front; Linux's way, a watch for each folder from
    /// a pool with a limit, leaves build output, dependencies and Git's objects unwatched and says once how to raise
    /// the limit when it runs out; and the system's watchers are shared and given back as windows come and
    /// go.</summary>
    [AvaloniaFact]
    public void Changes_the_watcher_lost_are_found_and_linuxs_watches_stay_within_their_limit()
    {
        Flow.StepApart<FolderWatchTests>("changes the watcher lost are read again into the open files the explorer and Go to File", t => t.Changes_the_watcher_lost_are_read_again_into_the_open_files_the_explorer_and_go_to_file(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("changes an overflow lost are found by listing again the folders they changed", t => t.Changes_an_overflow_lost_are_found_by_listing_again_the_folders_they_changed(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("files made and deleted unheard are found once the watcher looks again", t => t.Files_made_and_deleted_unheard_are_found_once_the_watcher_looks_again(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("files made and deleted while the watcher heard nothing are found once the window comes in front", t => t.Files_made_and_deleted_while_the_watcher_heard_nothing_are_found_once_the_window_comes_in_front(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("a branch switched while Git's watcher heard nothing is read once its watch fails", t => t.A_branch_switched_while_gits_watcher_heard_nothing_is_read_once_its_watch_fails(OperatingSystem.IsLinux()));
        Flow.StepApart<FolderWatchTests>("watching each folder as on Linux leaves build output dependencies and Git's objects unwatched and follows new folders", t => t.Watching_each_folder_as_on_Linux_leaves_build_output_dependencies_and_gits_objects_unwatched_and_follows_new_folders());
        Flow.StepApart<FolderWatchTests>("running out of inotify watches is one IDE error saying how to raise the limit", t => t.Running_out_of_inotify_watches_is_one_ide_error_saying_how_to_raise_the_limit());
        Flow.StepApart<SystemWatchesTests>("windows opened and closed and folders changed many times leave the system watches as they were", t => t.Windows_opened_and_closed_and_folders_changed_many_times_leave_the_system_watches_as_they_were());
        Flow.StepApart<SystemWatchesTests>("hundreds of folders watched apart share a few of the system's watchers and each hears its own changes", t => t.Hundreds_of_folders_watched_apart_share_a_few_of_the_systems_watchers_and_each_hears_its_own_changes());
        Flow.StepApart<SystemWatchesTests>("a window over a solution of many projects and packages reads their folders through a few watchers", t => t.A_window_over_a_solution_of_many_projects_and_packages_reads_their_folders_through_a_few_watchers());
        Flow.StepApart<SystemWatchesTests>("a drop taken for lost is not taken for lost again as its burst is looked at once quiet", t => t.A_drop_taken_for_lost_is_not_taken_for_lost_again_as_its_burst_is_looked_at_once_quiet());
    }

    /// <summary>What macOS's watcher drops from a stream read late, or falls minutes behind on saying nothing, is
    /// found: as its word of the drop comes, and as the beat that goes on only while a window the person sees is in
    /// front has the watchers look for themselves.</summary>
    [AvaloniaFact]
    public void What_macOSs_watcher_drops_or_falls_behind_on_is_found()
    {
        Flow.StepApart<FolderWatchTests>("changes macOS drops from a stream read late are found as its word of the drop comes", t => t.Changes_macOS_drops_from_a_stream_read_late_are_found_as_its_word_of_the_drop_comes());
        Flow.StepApart<FolderWatchTests>("changes made while the system's watcher is behind are found as it lags", t => t.Changes_made_while_the_systems_watcher_is_behind_are_found_as_it_lags());
        Flow.StepApart<FolderWatchTests>("the system's watchers' beat goes on only while a window the person sees is in front", t => t.The_systems_watchers_beat_goes_on_only_while_a_window_the_person_sees_is_in_front());
    }

    /// <summary>Projects and solutions written, moved in or out or changed on disk reach the Solution view, the run
    /// configurations and the language server: a project added to the solution joins them, a changed .csproj has the
    /// solution read again, a project and a program written or moved in with their folders are found and go with them,
    /// and a folder's .sln and .slnx listing different projects give the Explorer and the language server the same
    /// one.</summary>
    [AvaloniaFact]
    public void Projects_and_solutions_written_moved_or_changed_on_disk_reach_the_solution_view_the_run_configurations_and_the_language_server()
    {
        Flow.StepApart<FolderWatchTests>("a project added to the solution on disk joins the solution view and the run configurations", t => t.A_project_added_to_the_solution_on_disk_joins_the_solution_view_and_the_run_configurations());
        Flow.StepApart<FolderWatchTests>("a .csproj changed elsewhere has the solution read again", t => t.A_proj_file_changed_elsewhere_has_the_solution_read_again());
        Flow.StepApart<FolderWatchTests>("a project and a program written each with its folder are found with their run configurations", t => t.A_project_and_a_program_written_each_with_its_folder_are_found_with_their_run_configurations(false));
        Flow.StepApart<FolderWatchTests>("a project and a program moved in with their folders whole are found and gone with their folders", t => t.A_project_and_a_program_moved_in_with_their_folders_whole_are_found_and_gone_with_their_folders(false));
        Flow.StepApart<FolderWatchTests>("a folder's .sln and .slnx listing different projects give the explorer and the language server the same one", t => t.A_folders_sln_and_slnx_listing_different_projects_give_the_explorer_and_the_language_server_the_same_one());
    }

    private void Files_created_changed_and_deleted_elsewhere_reach_the_explorer_go_to_file_and_the_open_tabs()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        Ui.Settle();

        File.WriteAllText(Path.Combine(folder, "Two.cs"), "class Two { }\n");
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == "Two.cs"), "the new file in the Explorer");
        Ui.WaitFor(() => window.FileIndex.Files.Contains("Two.cs"), "the new file in Go to File");

        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { int changed; }\n");
        Ui.WaitFor(() => Text(window) == "class One { int changed; }\n", "the open file to take the text on disk");
        Assert.False(window.EditorArea.Active!.IsModified);

        File.Delete(Path.Combine(folder, "One.cs"));
        Ui.WaitFor(() => window.EditorArea.Documents.Count == 0, "the deleted file's tab to close");
        Ui.WaitFor(() => !window.ExplorerView.Tree.Rows.Any(r => r.Name == "One.cs"), "the deleted file to leave the Explorer");
        Assert.False(File.Exists(Path.Combine(folder, "One.cs")), "Closing the deleted file's tab wrote it back");
        window.Close();
    }

    /// <summary>A build makes bin and obj after the folder opened, and a clean takes them away: the Explorer shows them come
    /// and go, dimmed as build output is, as it shows those there when the folder opened, though nothing in them is followed.</summary>
    private void Build_output_folders_made_and_deleted_elsewhere_come_and_go_in_the_explorer(bool eachFolder)
    {
        Write("Program.cs", "class Program { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree);
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Name is "bin" or "obj");

        Write("obj/project.assets.json", "{}\n");
        Write($"bin/Debug/{TestProjects.Framework}/App.dll", "");
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Count(r => r.Name is "bin" or "obj" && r.IsDimmed) == 2,
            $"the build's bin and obj, dimmed, in the Explorer (it shows {string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))})");

        Directory.Delete(Path.Combine(folder, "bin"), recursive: true);
        Ui.WaitFor(() => !window.ExplorerView.Tree.Rows.Any(r => r.Name == "bin"), "the deleted bin to leave the Explorer");
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Name == "obj");
        window.Close();
    }

    /// <summary>A repository git init makes in the folder from outside the window (a terminal) is taken as the folder watcher
    /// hears it, the branch widget naming its branch and the Commit window listing the folder's files; its .git removed,
    /// the window says again that the folder is in none. Watched each folder on its own as on Linux too.</summary>
    private void A_repository_git_init_makes_outside_the_window_is_taken_and_one_removed_is_let_go(bool eachFolder)
    {
        Write("Shop.cs", "class Shop { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree, languageServer: false);
        window.Perform(AppActions.ShowCommit);
        Passenger.App.ToolWindows.CommitView commit = window.CommitView;
        Avalonia.Controls.TextBlock branch = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(window, "BranchName")!;
        Ui.Settle();
        Assert.True(window.Vcs.Repository is null && Said(commit) == Refusals.NoRepository,
            $"The folder of no repository opened with the Commit window saying '{Said(commit)}'");
        string Shown() => $"the branch widget says {branch.Text}, the Commit window lists [{string.Join(", ", Listed(commit))}] and says '{Said(commit)}'";

        Git("init", "-q", "-b", "main");
        Ui.WaitFor(() => branch.Text == "main" && Listed(commit).Contains("Shop.cs"), () => $"the repository git init made to be taken, where {Shown()}");

        TestFolders.Delete(Path.Combine(folder, ".git"));
        Ui.WaitFor(() => window.Vcs.Repository is null && branch.Text == "Version control" && Listed(commit).Length == 0 && Said(commit) == Refusals.NoRepository,
            () => $"the repository whose .git was removed to be let go, where {Shown()}");
        window.Close();

        static string[] Listed(Passenger.App.ToolWindows.CommitView commit) =>
            [.. commit.Rows.OfType<Passenger.App.ToolWindows.CommitRow>().Select(r => r.Change.Name)];

        static string? Said(Passenger.App.ToolWindows.CommitView commit) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(commit)
            .OfType<Avalonia.Controls.TextBlock>().SingleOrDefault(t => t.Classes.Contains(StyleClasses.EmptyText) && t.IsEffectivelyVisible)?.Text;
    }

    /// <summary>Git's checkout and rollback, and editors that save so, delete a file and write it anew: the open file
    /// keeps its tab, even when the deletion is handed over just before the file is there again.</summary>
    private void A_file_deleted_and_written_again_at_once_keeps_its_tab_with_the_new_text_and_one_deleted_for_good_closes()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        Ui.Settle();

        // Written again just after the deletion is handed over, the file gone as it is looked at.
        bool rewritten = false;
        window.FolderWatcher!.Changed += (_, changes) =>
        {
            if (!rewritten && changes.Paths.Contains(PathNames.Canonical(path)) && !File.Exists(path))
            {
                rewritten = true;
                File.WriteAllText(path, "class One { int back; }\n");
            }
        };
        File.Delete(path);
        Ui.WaitFor(() => rewritten, "the deletion to be seen");
        Ui.Settle();
        Assert.True(window.EditorArea.Documents.Any(d => d.Name == "One.cs"), "The file deleted and written again at once had its tab closed");
        Ui.WaitFor(() => Text(window) == "class One { int back; }\n", "the file written again to show its new text", TimeSpan.FromSeconds(10));
        Assert.False(window.EditorArea.Active!.IsModified);

        File.Delete(path);
        Ui.WaitFor(() => window.EditorArea.Documents.Count == 0, "the tab of the file deleted for good to close");
        Assert.False(File.Exists(path), "Closing the deleted file's tab wrote it back");
        window.Close();
    }

    private void A_file_with_unsaved_edits_changed_on_disk_asks_which_to_keep()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        Ui.Settle();
        List<string> asked = [];
        bool loadDisk = false;
        window.AskConflict = document =>
        {
            asked.Add(document.Name);
            return Task.FromResult<bool?>(loadDisk);
        };
        Type(window, "public ");

        File.WriteAllText(path, "class One { int disk; }\n");
        Ui.WaitFor(() => asked.Count == 1, "the File Cache Conflict question");
        Assert.Equal("One.cs", asked[0]);
        Assert.Equal("public class One { }\n", Text(window));
        Assert.Equal("public class One { }\n", File.ReadAllText(path));
        Assert.False(window.EditorArea.Active!.IsModified);

        Type(window, "sealed ");
        loadDisk = true;
        File.WriteAllText(path, "class One { int again; }\n");
        Ui.WaitFor(() => asked.Count == 2, "the question again for the next change");
        Ui.WaitFor(() => Text(window) == "class One { int again; }\n", "the disk's text");
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    private void While_the_conflict_question_is_open_nothing_is_saved_over_the_disks_version()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        Ui.Settle();
        window.ApplySettings(window.Settings with { SaveWhenIdle = true, IdleSeconds = 1 });
        Type(window, "public ");

        File.WriteAllText(path, "class One { int disk; }\n");
        ChoiceDialog dialog = ConflictAsked(window);
        // The dialog has the focus and the idle wait goes by: neither writes over the disk's version, nor does Save.
        Ui.Hold(TimeSpan.FromSeconds(1.5), "the idle save's second to go by");
        window.Perform(AppActions.Save);
        Ui.Settle();
        Assert.Equal("class One { int disk; }\n", File.ReadAllText(path));
        Assert.Single(window.OwnedWindows.OfType<ChoiceDialog>());

        Ui.Click(Answer(dialog, "Load File System Changes"), settle: false);
        Ui.WaitFor(() => Text(window) == "class One { int disk; }\n", "the disk's text in the editor");
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    /// <summary>The File Cache Conflict comes on its own as another program writes the file, while the person may be
    /// typing, so a key meant for the editor answers it neither way: Escape and Enter close it with the edits kept unsaved
    /// and the disk's version as it was, and the next save asks again, where Keep Memory Changes writes the edits over it.</summary>
    private void A_key_meant_for_the_editor_answers_the_conflict_question_neither_way_and_the_next_save_asks_again()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open(languageServer: false);
        window.OpenFile("One.cs");
        Ui.Settle();
        Type(window, "public ");

        File.WriteAllText(path, "class One { int disk; }\n");
        foreach (string key in (string[])["ESCAPE", "ENTER"])
        {
            Ui.Press(ConflictAsked(window), key);
            Ui.WaitFor(() => !window.OwnedWindows.OfType<ChoiceDialog>().Any(), $"{key} to close the File Cache Conflict question");
            Assert.True(File.ReadAllText(path) == "class One { int disk; }\n" && Text(window) == "public class One { }\n" && window.EditorArea.Active!.IsModified,
                $"{key} on the File Cache Conflict question was to keep both versions as they were; the disk holds '{File.ReadAllText(path)}' and the editor '{Text(window)}'");
            window.Perform(AppActions.Save);
        }

        Ui.Click(Answer(ConflictAsked(window), "Keep Memory Changes"), settle: false);
        Ui.WaitFor(() => File.ReadAllText(path) == "public class One { }\n", "Keep Memory Changes to write the edits over the disk's version");
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    private static ChoiceDialog ConflictAsked(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(d => d.Title == "File Cache Conflict"), "the File Cache Conflict question");
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<ChoiceDialog>().Single());
    }

    private static Avalonia.Controls.Button Answer(ChoiceDialog dialog, string words) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>().First(b => (string?)b.Content == words);

    private void A_file_with_unsaved_edits_deleted_on_disk_stays_open_and_saving_writes_it_back()
    {
        string path = Path.Combine(folder, "Gone", "One.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open();
        window.OpenFile(path);
        Ui.Settle();
        Type(window, "public ");

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        Ui.WaitFor(() => !window.ExplorerView.Tree.Rows.Any(r => r.Name == "Gone"), "the deletion to be seen");
        Assert.Equal("public class One { }\n", Text(window));

        window.Perform(AppActions.Save);
        Assert.Equal("public class One { }\n", File.ReadAllText(path));
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
        Assert.False(window.IsVisible, "The window would not close");
    }

    /// <summary>A folder renamed elsewhere (mv, git mv, Finder) is seen as that folder alone, not the files in it: its
    /// open files move with it, and a save writes them in its new place alone.</summary>
    private void Open_files_follow_their_folder_renamed_elsewhere_and_their_saves_never_make_the_old_folder_again(bool eachFolder)
    {
        Assert.SkipWhen(eachFolder && OperatingSystem.IsWindows(), "Windows renames a folder only once its own watch lets go; there one watch covers the tree");
        string old = Path.Combine(folder, "src", "Old");
        string renamed = Path.Combine(folder, "src", "New");
        Write("src/Old/One.cs", "class One { }\n");
        Write("src/Old/Deep/Two.cs", "class Two { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree);
        window.OpenFile(Path.Combine(old, "Deep", "Two.cs"));
        Ui.Settle();
        Type(window, "sealed ");
        window.OpenFile(Path.Combine(old, "One.cs"));
        Ui.Settle();

        Directory.Move(old, renamed);
        Ui.WaitFor(() => window.EditorArea.Documents.All(d => PathNames.IsUnder(d.Path, renamed)), "the open files to follow their folder to its new name");
        Assert.Equal(["One.cs", "Two.cs"], window.EditorArea.Documents.Select(d => d.Name).Order(StringComparer.Ordinal));
        OpenDocument two = window.EditorArea.Documents.Single(d => d.Name == "Two.cs");
        Assert.True(two.IsModified && two.Text.Snapshot.ToString() == "sealed class Two { }\n", "The file with unsaved edits lost them as it followed its folder");

        window.EditorArea.CodeEditor.Focus();
        Type(window, "public ");
        window.Close();
        Assert.False(window.IsVisible, $"The window would not close: {window.Status}");
        Assert.False(Directory.Exists(old), "A save made the folder renamed elsewhere again under its old name");
        Assert.Equal("public class One { }\n", File.ReadAllText(Path.Combine(renamed, "One.cs")));
        Assert.Equal("sealed class Two { }\n", File.ReadAllText(Path.Combine(renamed, "Deep", "Two.cs")));
    }

    /// <summary>Finder's Move to Trash renames a folder out of the one open, seen as that folder gone alone: an open file in
    /// it with no edits closes as a file deleted does, and one with edits keeps them, never written back there unasked.</summary>
    private void A_folder_trashed_elsewhere_closes_its_open_files_and_no_autosave_makes_it_again(bool eachFolder)
    {
        Assert.SkipWhen(eachFolder && OperatingSystem.IsWindows(), "Windows moves a folder only once its own watch lets go; there one watch covers the tree");
        string old = Path.Combine(folder, "src", "Old");
        string trash = TestFolders.Create("trash-");
        Write("src/Old/One.cs", "class One { }\n");
        Write("src/Old/Two.cs", "class Two { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree);
        window.OpenFile(Path.Combine(old, "Two.cs"));
        Ui.Settle();
        Type(window, "sealed ");
        window.OpenFile(Path.Combine(old, "One.cs"));
        Ui.Settle();

        Directory.Move(old, Path.Combine(trash, "Old"));
        Ui.WaitFor(() => window.EditorArea.Documents.All(d => d.Name != "One.cs"), "the tab of the file trashed with its folder to close");
        OpenDocument two = Assert.Single(window.EditorArea.Documents);
        Assert.Equal("sealed class Two { }\n", two.Text.Snapshot.ToString());

        // Typed into and left for the terminal, which autosaves: the file stays unsaved, and the status bar says why.
        window.EditorArea.CodeEditor.Focus();
        Type(window, "public ");
        Ui.Run(window, AppActions.ShowTerminal);
        Ui.WaitFor(() => window.Status?.StartsWith("Two.cs could not be saved", StringComparison.Ordinal) == true, "the autosave to say why it left the file");
        Assert.Equal("Two.cs could not be saved: its folder Old was moved or deleted, and only Save makes it again", window.Status);
        Assert.False(Directory.Exists(old), "An autosave made the folder trashed elsewhere again");
        Assert.True(two.IsModified);

        window.Perform(AppActions.Save);
        Ui.WaitFor(() => !two.IsModified, "the person's Save");
        Assert.Equal("public sealed class Two { }\n", File.ReadAllText(Path.Combine(old, "Two.cs")));
        window.Close();
        Assert.False(window.IsVisible, $"The window would not close: {window.Status}");
    }

    /// <summary>An agent's edit of a file whose folder has gone (a volume ejected, a folder trashed elsewhere) keeps the edit
    /// and answers why it was not saved, in the save's own words.</summary>
    private void An_agents_edit_of_a_file_whose_folder_has_gone_says_why_it_was_not_saved()
    {
        string old = Path.Combine(folder, "src", "Old");
        string trash = TestFolders.Create("trash-");
        Write("src/Old/Two.cs", "class Two { }\n");
        MainWindow window = Open(languageServer: false);
        window.OpenFile(Path.Combine(old, "Two.cs"));
        Ui.Settle();
        // Typed into, so its tab stays once its folder goes.
        Type(window, "sealed ");
        Directory.Move(old, Path.Combine(trash, "Old"));

        Task<string> editing = new Passenger.App.Mcp.PassengerTools(window).Edit("src/Old/Two.cs", "class Two", "class Three");
        Ui.WaitFor(() => editing.IsCompleted, "the agent's edit");
        string answered = editing.IsCompletedSuccessfully ? editing.Result : editing.Exception!.InnerException!.Message;
        Assert.True(answered.Contains("not saved: its folder Old was moved or deleted, and only Save makes it again", StringComparison.Ordinal),
            $"An agent's edit of a file whose folder has gone answered, rather than why it was not saved: {answered}");
        OpenDocument two = Assert.Single(window.EditorArea.Documents);
        Assert.True(two.IsModified && two.Text.Snapshot.ToString() == "sealed class Three { }\n", $"The edit was not kept unsaved: {two.Text.Snapshot}");

        window.Perform(AppActions.Save);
        Ui.WaitFor(() => !two.IsModified, "the person's Save");
        window.Close();
    }

    private void Lines_changed_on_disk_leave_breakpoints_bookmarks_and_the_caret_with_their_own_lines()
    {
        string path = Path.Combine(folder, "Long.cs");
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, 60).Select(i => $"// line {i}\n")));
        MainWindow window = Open();
        window.OpenFile(path);
        Ui.Settle();
        string open = window.EditorArea.Active!.Path;
        window.Debugger.Breakpoints.Toggle(open, 19);
        window.Debugger.Breakpoints.Toggle(open, 39);
        window.Bookmarks.Toggle(open, 19);
        window.Bookmarks.Toggle(open, 39);
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new Passenger.Text.TextPosition(29, 4));

        // A checkout changes line 5 (adding one after it) and line 50, around everything the person placed.
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, 60).Select(i =>
            i == 5 ? "// line 5, changed\n// and one more\n" : i == 50 ? "// line 50, changed\n" : $"// line {i}\n")));
        Ui.WaitFor(() => Text(window).Contains("line 50, changed", StringComparison.Ordinal), "the disk's text");
        Assert.Equal([20, 40], window.Debugger.Breakpoints.In(open).Select(b => b.Line));
        Assert.Equal([20, 40], window.Bookmarks.LinesIn(open));
        Assert.Equal(new Passenger.Text.TextPosition(30, 4), editor.CaretPosition);
        window.Close();
    }

    private void A_BOM_added_on_disk_or_a_conversion_to_UTF_8_survives_the_next_save_without_a_question()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        File.WriteAllBytes(Path.Combine(folder, "Two.cs"), Encoding.Latin1.GetBytes("// Café\n"));
        MainWindow window = Open();
        window.OpenFile("One.cs");
        Ui.Settle();
        List<string> asked = [];
        window.AskConflict = document =>
        {
            asked.Add(document.Name);
            return Task.FromResult<bool?>(false);
        };
        Type(window, "public ");

        // dotnet format with charset = utf-8-bom: the same text, a BOM before it, under unsaved edits.
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class One { }\n")]);
        Ui.WaitFor(() => EncodingShown(window) == "UTF-8 BOM", "the status bar to show the BOM added on disk");
        window.Perform(AppActions.Save);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("public class One { }\n")], File.ReadAllBytes(path));

        window.OpenFile("Two.cs");
        Ui.Settle();
        Assert.Equal("windows-1252", EncodingShown(window));
        File.WriteAllBytes(Path.Combine(folder, "Two.cs"), Encoding.UTF8.GetBytes("// Café\n"));
        Ui.WaitFor(() => EncodingShown(window) == "UTF-8", "the status bar to show the file converted on disk");
        Type(window, "// señor ");
        window.Perform(AppActions.Save);
        Assert.Equal(Encoding.UTF8.GetBytes("// señor // Café\n"), File.ReadAllBytes(Path.Combine(folder, "Two.cs")));
        Assert.Empty(asked);
        window.Close();
    }

    private void Watching_each_folder_as_on_Linux_leaves_build_output_dependencies_and_gits_objects_unwatched_and_follows_new_folders()
    {
        Write("src/App/App.cs", "class App { }\n");
        Write("src/App/bin/Debug/App.dll", "");
        Write("src/App/obj/project.assets.json", "{}\n");
        Write("node_modules/left-pad/index.js", "");
        Write("artifacts/bin/App/debug/App.dll", "");
        Write("packages/Some.Package/Some.Package.nupkg", "");
        Write("TestResults/run.trx", "");
        Write(".vs/state", "");
        Git("init", "-q", "-b", "main");
        Git("-c", "user.email=tests@passenger.invalid", "-c", "user.name=Passenger Tests", "commit", "-q", "--allow-empty", "-m", "First");
        MainWindow window = Open(FolderWatching.Inotify);
        window.OpenFile("src/App/App.cs");
        Ui.Landed(() => window.Vcs.Branch == "main", "the branch");

        string[] watched = Watched(window);
        foreach (string kept in new[] { ".", ".git", ".git/refs", ".git/refs/heads", "src", "src/App", "src/App/obj", "artifacts" })
        {
            Assert.True(watched.Contains(kept), $"{kept} is not watched, so what other programs change in it goes unseen");
        }
        // Of build output only obj and artifacts themselves, where restores write the assets XAML is read from.
        Assert.All(watched, path => Assert.True(path is "." or ".git" or "src" or "src/App" or "src/App/obj" or "artifacts" || path.StartsWith(".git/refs", StringComparison.Ordinal),
            $"{path} is watched, spending one of the user's inotify watches on a folder nothing in the IDE looks at"));

        File.WriteAllText(Path.Combine(folder, "src", "App", "App.cs"), "class App { int changed; }\n");
        Ui.WaitFor(() => Text(window) == "class App { int changed; }\n", "the open file deep in the folder to take the text on disk");
        Git("switch", "-q", "-c", "side");
        Ui.WaitFor(() => window.Vcs.Branch == "side", "the branch checked out elsewhere to show");

        Directory.CreateDirectory(Path.Combine(folder, "src", "Feature", "Deep"));
        Directory.CreateDirectory(Path.Combine(folder, "src", "Feature", "obj", "Debug"));
        Ui.WaitFor(() => Watched(window).Contains("src/Feature/Deep") && Watched(window).Contains("src/Feature/obj"), "the folders made to be watched");
        Ui.Settle();
        Assert.DoesNotContain("src/Feature/obj/Debug", Watched(window));
        string deep = Path.Combine("src", "Feature", "Deep", "Deep.cs");
        Assert.DoesNotContain(deep, window.FileIndex.Files);
        Write("src/Feature/Deep/Deep.cs", "class Deep { }\n");
        Ui.WaitFor(() => window.FileIndex.Files.Contains(deep), "the file made in the new folder in Go to File, which only that folder's watch hears of");

        List<string> restored = [];
        window.FolderWatcher!.Changed += (_, changes) => restored.AddRange(changes.Restored);
        Write("artifacts/obj/Late/project.assets.json", "{}\n");
        Write("artifacts/obj/Late/Late.csproj.nuget.g.props", "<Project />\n");
        Ui.WaitFor(() => restored.Contains(Path.Combine(PathNames.Canonical(folder), "artifacts", "obj", "Late", "project.assets.json")), "the restore made in artifacts/obj/Late");
        Assert.Contains("artifacts/obj/Late", Watched(window));
        File.WriteAllText(Path.Combine(folder, "artifacts", "obj", "Late", "project.assets.json"), "{ }\n");
        restored.Clear();
        Ui.WaitFor(() => restored.Count > 0, "the restore written again in artifacts/obj/Late");

        // Windows moves or deletes a watched folder only once its watch lets go; there one watch covers the tree anyway.
        if (!OperatingSystem.IsWindows())
        {
            Directory.Move(Path.Combine(folder, "src", "Feature"), Path.Combine(folder, "src", "Moved"));
            Ui.WaitFor(() => Watched(window).Contains("src/Moved/Deep") && !Watched(window).Any(p => p.StartsWith("src/Feature", StringComparison.Ordinal)),
                () => $"the renamed folders to be watched under their new names alone, where {string.Join(", ", Watched(window))} are watched");
            Ui.Settle();
            string moved = Path.Combine("src", "Moved", "Deep", "Moved.cs");
            Write("src/Moved/Deep/Moved.cs", "class Moved { }\n");
            Ui.WaitFor(() => window.FileIndex.Files.Contains(moved), "the file made in the renamed folder in Go to File");

            Directory.Delete(Path.Combine(folder, "src", "Moved"), recursive: true);
            Ui.WaitFor(() => !Watched(window).Any(p => p.StartsWith("src/Moved", StringComparison.Ordinal)), "the deleted folders' watches to be let go of");
            Ui.WaitFor(() => !window.FileIndex.Files.Contains(moved), "the deleted file to leave Go to File");
        }
        window.Close();
    }

    /// <summary>Where each folder is watched on its own, a folder moved away ends its own watch and the folder above it says
    /// where it went, each heard on its own thread: heard ending first, the folder lets go of the folders watched under it
    /// too, so none stays watched under its old name, and one made again there is watched anew. The system's watchers hear
    /// nothing while the folder moves but the moved folder's own watch ending.</summary>
    [AvaloniaFact]
    public void A_folder_moved_away_whose_own_watch_is_heard_ending_first_leaves_nothing_watched_under_its_old_name()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows moves a watched folder only once its watch lets go");
        Write("src/Feature/Deep/Deep.cs", "class Deep { }\n");
        Write("src/Feature/obj/project.assets.json", "{}\n");
        MainWindow window = Open(FolderWatching.Inotify, languageServer: false);
        FolderWatcher watcher = window.FolderWatcher!;
        Assert.Contains("src/Feature/Deep", Watched(window));
        using (watcher.Deaf())
        {
            Directory.Move(Path.Combine(folder, "src", "Feature"), Path.Combine(folder, "src", "Moved"));
            watcher.Fail(Path.Combine(folder, "src", "Feature"), new DirectoryNotFoundException($"{Path.Combine(folder, "src", "Feature")} was moved"));
            Ui.Settle();
        }
        window.Activate();
        Ui.WaitFor(() => Watched(window).Contains("src/Moved/Deep") && !Watched(window).Any(p => p.StartsWith("src/Feature", StringComparison.Ordinal)),
            () => $"the moved folders to be watched under their new names alone, where {string.Join(", ", Watched(window))} are watched");

        Directory.CreateDirectory(Path.Combine(folder, "src", "Feature", "Deep"));
        Ui.WaitFor(() => Watched(window).Contains("src/Feature/Deep"), () => $"the folder made again under the old name to be watched, where {string.Join(", ", Watched(window))} are");
        Write("src/Feature/Deep/Again.cs", "class Again { }\n");
        string again = Path.Combine("src", "Feature", "Deep", "Again.cs");
        Ui.WaitFor(() => window.FileIndex.Files.Contains(again), "the file made in the folder made again under the old name in Go to File, which only that folder's watch hears of");
        window.Close();
    }

    private void Running_out_of_inotify_watches_is_one_ide_error_saying_how_to_raise_the_limit()
    {
        Write("One/One.cs", "class One { }\n");
        Write("Two/Two.cs", "class Two { }\n");
        MainWindow window = Open(FolderWatching.Inotify);
        FolderWatcher watcher = window.FolderWatcher!;

        // What .NET's watcher raises on Linux when inotify_add_watch fails with ENOSPC, from each folder's own watcher.
        watcher.Fail(Path.Combine(folder, "One"), OutOfWatches());
        watcher.Fail(Path.Combine(folder, "Two"), OutOfWatches());
        Ui.WaitFor(() => window.Status?.Contains("fs.inotify.max_user_watches", StringComparison.Ordinal) == true,
            "the status bar to say the inotify watches ran out");
        Ui.Settle();
        IdeError error = Assert.Single(IdeErrors.Errors);
        Assert.StartsWith("Changes made outside Passenger in some folders will go unseen", error.Message, StringComparison.Ordinal);
        Assert.Contains("sudo sysctl --system", error.Message, StringComparison.Ordinal);
        Assert.Contains("fs.inotify.max_user_watches", Ui.IdeLog(), StringComparison.Ordinal);
        Assert.Equal(["."], Watched(window));

        Write("Three.cs", "class Three { }\n");
        Ui.WaitFor(() => window.FileIndex.Files.Contains("Three.cs"), "a file made in a folder still watched in Go to File");
        // Reported on purpose: the check that fails a test over a caught error is not to see it.
        IdeErrors.Clear();
        window.Close();
    }

    /// <summary>A large checkout overflows the system's buffer of changes: those it lost are never heard of, so the window
    /// reads again what it shows, and a file the watcher's record held that is gone is named among the changes, for the
    /// language server to be told it went. Its watchers stop while the files change, so the changes are lost as an
    /// overflow loses them.</summary>
    private void Changes_the_watcher_lost_are_read_again_into_the_open_files_the_explorer_and_go_to_file(bool eachFolder)
    {
        Write("One.cs", "class One { }\n");
        Write("Old/Old.cs", "class Old { }\n");
        Write("Old/Gone.cs", "class Gone { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree);
        window.OpenFile("One.cs");
        window.OpenFile("Old/Old.cs");
        Ui.Settle();
        bool goneNamed = false;
        window.FolderWatcher!.Changed += (_, changes) => goneNamed |= changes.Paths.Any(p => Path.GetFileName(p) == "Gone.cs");
        IDisposable deaf = window.FolderWatcher.Deaf();

        Write("One.cs", "class One { int changed; }\n");
        Write("Old/Old.cs", "class Old { int changed; }\n");
        File.Delete(Path.Combine(folder, "Old", "Gone.cs"));
        Write("Fresh/Fresh.cs", "class Fresh { }\n");
        window.FolderWatcher.Fail(folder, new InternalBufferOverflowException("Too many changes at once; directory listing is required."));
        Ui.WaitFor(() => window.EditorArea.Documents.All(d => d.Text.Snapshot.ToString().Contains("int changed;", StringComparison.Ordinal)),
            $"every open file to take the text on disk, where they hold [{string.Join(" | ", window.EditorArea.Documents.Select(d => d.Text.Snapshot.ToString().Trim()))}]");
        Assert.All(window.EditorArea.Documents, d => Assert.False(d.IsModified));
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == "Fresh"),
            $"the folder made meanwhile in the Explorer (it shows {string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))})");
        Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Fresh", "Fresh.cs")), "the file made meanwhile in Go to File");
        Ui.WaitFor(() => goneNamed, "the file deleted meanwhile, which the watcher's record held, named among the changes handed over");

        // Started only now: a watcher started at once can still be told of the changes just made.
        deaf.Dispose();
        Write("Fresh/Later.cs", "class Later { }\n");
        Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Fresh", "Later.cs")), "a file made after in Go to File");
        window.Close();
    }

    /// <summary>macOS's stream of the folder, its reading held up as a loaded system holds it while thousands of files change,
    /// drops changes and says so: the watcher takes its word as an overflow and lists again what changed, so a commit, a file
    /// made in a new folder and one deleted among the dropped changes reach Git's status and Go to File.</summary>
    private void Changes_macOS_drops_from_a_stream_read_late_are_found_as_its_word_of_the_drop_comes()
    {
        Assert.SkipUnless(FolderWatching.ReadsMacEvents, "Only macOS's stream of Passenger's own says when the system dropped its changes");
        Write("One.cs", "class One { }\n");
        Write("Old/Gone.cs", "class Gone { }\n");
        TestGit.Init(folder);
        Write("One.cs", "sealed class One { }\n");
        string made = Path.Combine("Fresh", "Fresh.cs");
        string deleted = Path.Combine("Old", "Gone.cs");
        MainWindow window = Open(FolderWatching.WholeTree, languageServer: false);
        Ui.WaitFor(() => window.FileIndex.Files.Contains(deleted) && window.Vcs.Changes.Any(c => c.Name == "One.cs"), "the folder's files in Go to File and the changed file in Git's status");
        bool dropped = false;
        window.FolderWatcher!.Changed += (_, changes) => dropped |= changes.Overflowed;
        string churn = Path.Combine(folder, "Churn");
        using (window.FolderWatcher.HoldReading())
        {
            Git("commit", "-q", "-am", "More");
            File.Delete(Path.Combine(folder, deleted));
            Write(made, "class Fresh { }\n");
            Directory.CreateDirectory(churn);
            for (int i = 0; i < 20_000; i++)
            {
                File.WriteAllText(Path.Combine(churn, $"{i}.txt"), "churn");
            }
        }
        Ui.WaitFor(() => dropped, "macOS's word that it dropped the stream's changes, read late, to reach the watcher as an overflow");
        Ui.WaitFor(() => window.Vcs.Changes.Count(c => c.Name is "Gone.cs" or "Fresh.cs") == 2 && !window.Vcs.Changes.Any(c => c.Name == "One.cs")
                && window.FileIndex.Files.Contains(made) && !window.FileIndex.Files.Contains(deleted),
            () => $"the commit, the file made and the one deleted among the dropped changes to be found, where Git's status lists "
                + $"{string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"))} and Go to File {string.Join(", ", window.FileIndex.Files.Where(f => !f.StartsWith("Churn", StringComparison.Ordinal)))}");
        window.Close();
    }

    /// <summary>The system's buffer of changes overflows, as a large checkout makes it, a while after the folder opened:
    /// what it lost is found by listing again the folders whose entries changed, against what the watcher last knew of
    /// them, rather than by reading the whole folder again. Its watchers stop while the files change, so the changes are
    /// lost as an overflow loses them.</summary>
    private void Changes_an_overflow_lost_are_found_by_listing_again_the_folders_they_changed(bool eachFolder)
    {
        Write("One.cs", "class One { }\n");
        Write("Old/Old.cs", "class Old { }\n");
        Write("Old/Gone.cs", "class Gone { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree, languageServer: false);
        window.OpenFile("One.cs");
        window.OpenFile("Old/Old.cs");
        Ui.Hold(TimeSpan.FromSeconds(3.5), "the watcher's record of the folder to be older than what an overflow may have lost");
        bool lost = false;
        window.FolderWatcher!.Changed += (_, changes) => lost |= changes.Lost;
        IDisposable deaf = window.FolderWatcher.Deaf();

        Write("One.cs", "class One { int changed; }\n");
        Write("Old/Old.cs", "class Old { int changed; }\n");
        File.Delete(Path.Combine(folder, "Old", "Gone.cs"));
        Write("Fresh/Fresh.cs", "class Fresh { }\n");
        window.FolderWatcher.Fail(folder, new InternalBufferOverflowException("Too many changes at once; directory listing is required."));
        Ui.WaitFor(() => window.EditorArea.Documents.All(d => d.Text.Snapshot.ToString().Contains("int changed;", StringComparison.Ordinal)),
            $"every open file to take the text on disk, where they hold [{string.Join(" | ", window.EditorArea.Documents.Select(d => d.Text.Snapshot.ToString().Trim()))}]");
        Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Fresh", "Fresh.cs")) && !window.FileIndex.Files.Contains(Path.Combine("Old", "Gone.cs")),
            $"Go to File to gain the file made and lose the one deleted meanwhile (it lists {string.Join(", ", window.FileIndex.Files)})");
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == "Fresh"),
            $"the folder made meanwhile in the Explorer (it shows {string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))})");
        Assert.False(lost, "The folder's watcher read the whole folder again after the overflow rather than the folders whose entries changed");

        deaf.Dispose();
        Write("Fresh/Later.cs", "class Later { }\n");
        Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Fresh", "Later.cs")), "a file made after in the folder found in Go to File");
        window.Close();
    }

    /// <summary>A file made in a new folder and one deleted while the watchers heard nothing, as changes the system dropped
    /// without a word, are found once the watcher is asked to look again: the folders whose stamp moved are listed against
    /// its record, and what came or went there is handed over, the whole folder never read again. Its watchers stop while
    /// the files change, so the changes are lost as a dropped event loses them.</summary>
    private void Files_made_and_deleted_unheard_are_found_once_the_watcher_looks_again(bool eachFolder)
    {
        Write("One.cs", "class One { }\n");
        Write("Old/Gone.cs", "class Gone { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree, languageServer: false);
        Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Old", "Gone.cs")), "the folder's files in Go to File");
        bool lost = false;
        window.FolderWatcher!.Changed += (_, changes) => lost |= changes.Lost;
        IDisposable deaf = window.FolderWatcher.Deaf();
        try
        {
            File.Delete(Path.Combine(folder, "Old", "Gone.cs"));
            Write("Fresh/Fresh.cs", "class Fresh { }\n");

            window.FolderWatcher.LookAgain();
            Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Fresh", "Fresh.cs")) && !window.FileIndex.Files.Contains(Path.Combine("Old", "Gone.cs")),
                () => $"Go to File to gain the file made and lose the one deleted unheard once the watcher looked again (it lists {string.Join(", ", window.FileIndex.Files)})");
            Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == "Fresh"),
                () => $"the folder made unheard in the Explorer (it shows {string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))})");
            Assert.False(lost, "The folder's watcher read the whole folder again as it looked again, rather than the folders whose stamp moved");
        }
        finally
        {
            deaf.Dispose();
        }
        window.Close();
    }

    /// <summary>A file made in a new folder and one deleted while the folder's watcher heard nothing, as changes the system
    /// dropped without a word, are found once the window comes in front, as when a person comes back to it from a terminal:
    /// Go to File and the Explorer gain the one and lose the other, and Git's status lists both. The watchers stop while the
    /// files change, so the changes are lost as a dropped event loses them.</summary>
    private void Files_made_and_deleted_while_the_watcher_heard_nothing_are_found_once_the_window_comes_in_front(bool eachFolder)
    {
        Write("One.cs", "class One { }\n");
        Write("Old/Gone.cs", "class Gone { }\n");
        TestGit.Init(folder);
        string made = Path.Combine("Fresh", "Fresh.cs");
        string deleted = Path.Combine("Old", "Gone.cs");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree, languageServer: false);
        Ui.WaitFor(() => window.FileIndex.Files.Contains(deleted) && window.Vcs.Branch == "main", "the folder's files in Go to File and its branch read");
        IDisposable deaf = window.FolderWatcher!.Deaf();
        try
        {
            File.Delete(Path.Combine(folder, deleted));
            Write(made, "class Fresh { }\n");
            Ui.Settle();
            Assert.False(window.FileIndex.Files.Contains(made), "Go to File listed a file made while the folder's watcher was deaf before the window came in front");

            window.Activate();
            Ui.WaitFor(() => window.FileIndex.Files.Contains(made) && !window.FileIndex.Files.Contains(deleted) && window.ExplorerView.Tree.Rows.Any(r => r.Name == "Fresh")
                    && Listed(Passenger.Vcs.ChangeKind.Unversioned, made) && Listed(Passenger.Vcs.ChangeKind.Deleted, deleted),
                () => $"the file made and the one deleted unheard to be found once the window came in front, where Go to File lists {string.Join(", ", window.FileIndex.Files)}, "
                    + $"the Explorer shows {string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))} "
                    + $"and Git's status lists {string.Join(", ", window.Vcs.Changes.Select(c => $"{Path.GetRelativePath(folder, c.Path)} ({c.Kind})"))}");
        }
        finally
        {
            deaf.Dispose();
        }
        window.Close();

        bool Listed(Passenger.Vcs.ChangeKind kind, string relative) => window.Vcs.Changes.Any(c => c.Kind == kind && PathNames.Same(c.Path, Path.Combine(folder, relative)));
    }

    /// <summary>While macOS's watcher hands nothing over, as under a load of changes it falls minutes behind without a word,
    /// a commit made in a terminal, a file made in a new folder, one deleted, one made a folder of its name and an open one
    /// written in place are found without the window coming in front: the beat goes unheard, so the watchers look for
    /// themselves until it is heard again, and Git's status, Go to File and the open file follow. The system's watchers,
    /// the beat's too, hear nothing while the files change.</summary>
    private void Changes_made_while_the_systems_watcher_is_behind_are_found_as_it_lags()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Only macOS's watcher falls behind without a word; the others say when they lose changes");
        Write("One.cs", "class One { }\n");
        Write("Old/Gone.cs", "class Gone { }\n");
        Write("Old/Turned.cs", "class Turned { }\n");
        TestGit.Init(folder);
        Write("One.cs", "sealed class One { }\n");
        Write("Notes.txt", "notes\n");
        string made = Path.Combine("Fresh", "Fresh.cs");
        string deleted = Path.Combine("Old", "Gone.cs");
        string turned = Path.Combine("Old", "Turned.cs", "Inner.cs");
        MainWindow window = Open(FolderWatching.WholeTree, languageServer: false);
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Ui.WaitFor(() => window.FileIndex.Files.Contains(deleted) && window.Vcs.Changes.Any(c => c.Name == "One.cs"), "the folder's files in Go to File and the changed file in Git's status");
        WatchedFile beat = WatchLag.Canary ?? throw new InvalidOperationException("Nothing checks the system's watcher for lagging while a folder is watched");
        IDisposable deafFolder = window.FolderWatcher!.Deaf();
        IDisposable deafBeat = beat.Deaf();
        try
        {
            Git("commit", "-q", "-am", "More");
            File.Delete(Path.Combine(folder, deleted));
            Write(made, "class Fresh { }\n");
            File.Delete(Path.Combine(folder, "Old", "Turned.cs"));
            Write(turned, "class Inner { }\n");
            // In place, as an editor that writes over a file's own bytes saves it, which moves no folder's stamp.
            using (FileStream notes = new(Path.Combine(folder, "Notes.txt"), FileMode.Open, FileAccess.Write))
            {
                notes.Write("lagged\n"u8);
            }
            Ui.WaitFor(() => window.Vcs.Changes.Count(c => c.Name is "Gone.cs" or "Fresh.cs") == 2 && !window.Vcs.Changes.Any(c => c.Name == "One.cs")
                    && window.FileIndex.Files.Contains(made) && !window.FileIndex.Files.Contains(deleted) && window.FileIndex.Files.Contains(turned)
                    && window.EditorArea.Active!.Text.Snapshot.ToString() == "lagged\n",
                () => $"the commit, the file made, the one deleted, the one made a folder and the open one written in place while the system's watcher was behind to be found as it lagged, "
                    + $"where Git's status lists {string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"))}, Go to File {string.Join(", ", window.FileIndex.Files)} "
                    + $"and the open file holds [{window.EditorArea.Active!.Text.Snapshot.ToString().Trim()}]");
            Assert.True(WatchLag.IsBehind, "The system's watcher was not taken for behind while its beat went unheard");
        }
        finally
        {
            deafFolder.Dispose();
            deafBeat.Dispose();
        }
        window.Close();
    }

    /// <summary>The system's watcher's beat goes on only while it can matter: while a window the person sees is in front,
    /// a write of its file and its change heard at each beat; never while that window is in the background, whose look as
    /// it comes in front finds what went unheard meanwhile, nor for a window closed as it came in front.</summary>
    private void The_systems_watchers_beat_goes_on_only_while_a_window_the_person_sees_is_in_front()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Only macOS's watcher falls behind without a word, so only there is a beat written");
        Write("One.cs", "class One { }\n");
        string state = TestFolders.Create("watch-state-");
        // Shown and closed at once: its coming in front, posted as it showed, lands after it closed.
        int holds = WatchLag.Holds;
        MainWindow brief = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false, Unseen = false };
        brief.Show();
        brief.Close();
        Ui.Settle();
        Assert.True(WatchLag.Holds == holds, $"A window shown and closed at once still holds the beat ({WatchLag.Holds - holds} holds more), so it never ends once every window is in the background");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false, Unseen = false };
        try
        {
            window.Show();
            window.OpenFolder(folder);
            window.Activate();
            Ui.Settle();
            (long beats, long heard) = (WatchLag.Beats, WatchLag.HeardBeats);
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            Ui.WaitFor(() => WatchLag.Beats >= beats + 5, "five beats while the window the person sees is in front");
            TimeSpan on = System.Diagnostics.Stopwatch.GetElapsedTime(began);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"While on, the beat wrote its file {WatchLag.Beats - beats} times and heard {WatchLag.HeardBeats - heard} changes of it in {on.TotalSeconds:F1} s");

            window.Hide();
            Ui.WaitFor(() => !WatchLag.IsBeating, "the beat to end once the window went to the background");
            long stopped = WatchLag.Beats;
            Ui.Hold(TimeSpan.FromSeconds(5), "two beats' time with no window in front");
            Assert.True(WatchLag.Beats == stopped, $"The beat wrote its file {WatchLag.Beats - stopped} times while no window was in front");

            window.Show();
            window.Activate();
            Ui.WaitFor(() => WatchLag.IsBeating, "the beat to go on again once the window came back in front");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(state);
        }
    }

    /// <summary>A branch switched from outside while the watcher of Git's folder heard nothing is read once that watcher's
    /// watch fails, which says at once that anything there may have moved, rather than at the next change it hears: in a
    /// folder opened below the repository's top, whose Git folder is outside it and watched apart.</summary>
    private void A_branch_switched_while_gits_watcher_heard_nothing_is_read_once_its_watch_fails(bool eachFolder)
    {
        Write("src/Shop.cs", "class Shop { }\n");
        TestGit.Init(folder);
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree, languageServer: false, opened: Path.Combine(folder, "src"));
        Ui.WaitFor(() => window.Vcs.Branch == "main", () => $"the branch read (it is {window.Vcs.Branch})");
        Passenger.App.Vcs.GitWatcher git = Assert.Single(window.Vcs.GitWatchers);
        IDisposable deaf = git.Deaf();
        try
        {
            Git("switch", "-q", "-c", "switched");
            Ui.Settle();
            Assert.True(window.Vcs.Branch == "main", $"The branch switched while Git's watcher was deaf was read before its watch failed (it is {window.Vcs.Branch})");

            git.Fail(git.Top, new IOException("The watch failed."));
            Ui.WaitFor(() => window.Vcs.Branch == "switched", () => $"the branch switched unheard to be read once the watch of Git's folder failed (it is {window.Vcs.Branch})");
        }
        finally
        {
            deaf.Dispose();
        }
        window.Close();
    }

    /// <summary>A folder of fifty files moved into the folder from elsewhere, then out again, as a person's mv or file
    /// manager moves one: each of its files is handed over with it, come and then gone, in the batch the folder comes or
    /// goes in, the folder never read again whole, and Go to File follows.</summary>
    private void A_folder_moved_in_or_out_hands_over_each_of_its_files_in_the_batch_it_comes_or_goes_in(bool eachFolder)
    {
        Write("One.cs", "class One { }\n");
        MainWindow window = Open(eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree, languageServer: false);
        string elsewhere = TestFolders.Create("elsewhere-");
        TestFolders.WriteAll(elsewhere, Enumerable.Range(0, 50).Select(i => (Path.Combine("Moving", $"Moving{i}.cs"), $"class Moving{i} {{ }}\n")));
        string inside = Path.Combine(folder, "Moving");
        string[] files = [.. Enumerable.Range(0, 50).Select(i => Path.Combine(inside, $"Moving{i}.cs"))];
        List<FolderChanges> batches = [];
        window.FolderWatcher!.Changed += (_, changes) => batches.Add(changes);

        Directory.Move(Path.Combine(elsewhere, "Moving"), inside);
        Ui.PumpUntil(() => batches.Any(b => b.ComeOrGone.Contains(inside)), "the folder moved in handed over");
        FolderChanges came = batches.First(b => b.ComeOrGone.Contains(inside));
        Assert.True(files.All(came.ComeOrGone.Contains), $"The batch the folder moved in came in named {files.Count(came.ComeOrGone.Contains)} of its 50 files as come");
        Ui.PumpUntil(() => files.All(f => window.FileIndex.Files.Contains(Path.GetRelativePath(folder, f))), "the files moved in in Go to File");

        batches.Clear();
        Directory.Move(inside, Path.Combine(elsewhere, "Moved"));
        Ui.PumpUntil(() => batches.Any(b => b.ComeOrGone.Contains(inside)), "the folder moved out handed over");
        FolderChanges went = batches.First(b => b.ComeOrGone.Contains(inside));
        Assert.True(files.All(went.ComeOrGone.Contains), $"The batch the folder moved out went in named {files.Count(went.ComeOrGone.Contains)} of its 50 files as gone");
        Ui.PumpUntil(() => !window.FileIndex.Files.Any(f => f.StartsWith("Moving", StringComparison.Ordinal)), "the files moved out gone from Go to File");
        Assert.False(batches.Any(b => b.Lost), "The folder's watcher read the whole folder again for a folder moved out");
        window.Close();
    }

    /// <summary>Go to File follows what another program makes, moves, renames and deletes, files and folders, a link, a
    /// letter's case and a save's temporary file among them, to list after each what a window opened on the folder afresh
    /// lists, in the same order (which Find in Files searches in).</summary>
    private void Go_to_file_follows_files_and_folders_come_and_gone_to_list_what_a_walk_of_the_folder_lists()
    {
        Write("App/App.cs", "class App { }\n");
        Write("App/Sub/Deep.cs", "class Deep { }\n");
        Write("a-b/c.cs", "// a dash sorts before a slash\n");
        Write("a/c.cs", "// and a name before a longer one\n");
        MainWindow window = Open(languageServer: false);
        (string What, Action Change)[] changes =
        [
            ("a file made", () => Write("App/New.cs", "class New { }\n")),
            ("a folder made with folders and files in it", () =>
            {
                Write("Made/One.cs", "class One { }\n");
                Write("Made/Inner/Two.cs", "class Two { }\n");
                Write("Made/Inner/obj/built.cs", "// build output\n");
                Write("Made/.hidden/Three.cs", "class Three { }\n");
            }),
            ("a folder renamed", () => Directory.Move(Path.Combine(folder, "Made"), Path.Combine(folder, "Moved"))),
            ("a file renamed", () => File.Move(Path.Combine(folder, "App", "New.cs"), Path.Combine(folder, "App", "Renamed.cs"))),
            ("a file's case changed", () => File.Move(Path.Combine(folder, "App", "Renamed.cs"), Path.Combine(folder, "App", "renamed.cs"))),
            ("a folder's case changed", () => Directory.Move(Path.Combine(folder, "Moved", "Inner"), Path.Combine(folder, "Moved", "inner"))),
            ("a link to a folder made", () => TestFolders.LinkFolder(Path.Combine(folder, "Alias"), Path.Combine(folder, "App"))),
            ("a save's temporary file, a build's and the system's own", () =>
            {
                Write("App/Sub/Deep.cs" + FileText.SaveSuffix, "class Deep { int saving; }\n");
                Write(Momentary, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
                Write("Moved/.DS_Store", "the system's own\n");
            }),
            ("a folder deleted", () => Directory.Delete(Path.Combine(folder, "Moved"), recursive: true)),
            ("a file deleted and a folder made in its place", () =>
            {
                File.Delete(Path.Combine(folder, "a", "c.cs"));
                Write("a/c.cs/d.cs", "// a file's name is a folder's now\n");
            }),
            ("build output made", () => Write("App/bin/Debug/App.dll", string.Empty)),
        ];
        bool heard = false;
        window.FolderWatcher!.Changed += (_, changed) => heard |= changed.Structure;
        foreach ((string what, Action change) in changes)
        {
            heard = false;
            change();
            Ui.WaitFor(() => heard, $"the folder watcher to hear of {what}");
            Ui.Settle();
            string[] walked = Walked(folder);
            Ui.WaitFor(() => walked.SequenceEqual(window.FileIndex.Files),
                () => $"Go to File after {what} to list [{string.Join(", ", walked)}], not [{string.Join(", ", window.FileIndex.Files)}]");
        }
        Assert.False(window.FileIndex.Files.Contains(Path.Join("App", Path.GetFileName(Momentary))), "Go to File lists the project WPF's build writes for a moment, which no walk takes for one of the folder's files");
        window.Close();
    }

    /// <summary>The project WPF's markup compilation writes beside a project's own for the length of a build.</summary>
    private const string Momentary = "App/App_x1y2z3w4_wpftmp.csproj";

    /// <summary>The files a window opened on a folder afresh lists for Go to File, in their order.</summary>
    internal static string[] Walked(string root)
    {
        MainWindow fresh = new() { Width = 800, Height = 600, StartsLanguageServer = false };
        fresh.Show();
        fresh.OpenFolder(root);
        Ui.WaitFor(() => !fresh.FileIndex.IsWalking, "a fresh window's walk of the folder");
        string[] listed = [.. fresh.FileIndex.Files];
        fresh.Close();
        return listed;
    }

    private static IOException OutOfWatches() =>
        new("The configured user limit (8192) on the number of inotify watches has been reached, or the operating system failed to allocate a required resource.", 28);

    /// <summary>The folders watched, from the open folder, with forward slashes.</summary>
    private string[] Watched(MainWindow window) =>
        [.. window.FolderWatcher!.Watched.Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    private void Git(params string[] arguments) => TestGit.Run(folder, arguments);

    private static string? EncodingShown(MainWindow window) => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(window, "EncodingText")!.Text;

    private static void Type(MainWindow window, string text)
    {
        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(0);
        window.KeyTextInput(text);
        Ui.Settle();
    }

    private void A_project_added_to_the_solution_on_disk_joins_the_solution_view_and_the_run_configurations()
    {
        Write("App/App.csproj", Project);
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        MainWindow window = Open(languageServer: false);
        Assert.Equal(["App"], window.Configurations.Select(c => c.Name));

        Write("Tool/Tool.csproj", Project);
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Tool"), "the new project's run configuration");
        Assert.Equal(2, window.Solution!.Projects.Count());
        Assert.True(window.ExplorerView.Tree.IsSolutionView);
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == "Tool"), "the new project in the Solution view");
        window.Close();
    }

    /// <summary>A .proj is a project file as a .csproj is, so one another program changes, here one the solution's project
    /// imports, has the solution read again and its project evaluated anew.</summary>
    private void A_proj_file_changed_elsewhere_has_the_solution_read_again()
    {
        Write("Shared.proj", Shared("one"));
        Write("App/App.csproj", Project.With("  <Import Project=\"../Shared.proj\" />\n"));
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        MainWindow window = Open(languageServer: false);
        Ui.WaitFor(() => Secrets(window) == "one", () => $"App's evaluation to read the UserSecretsId Shared.proj sets; it reads {Secrets(window) ?? "none"}");

        Write("Shared.proj", Shared("two"));
        Ui.WaitFor(() => Secrets(window) == "two", () => $"Shared.proj, changed on disk, to have App evaluated again; its UserSecretsId is still {Secrets(window) ?? "none"}");
        window.Close();

        static string Shared(string id) => $"<Project>\n  <PropertyGroup>\n    <UserSecretsId>{id}</UserSecretsId>\n  </PropertyGroup>\n</Project>\n";
        static string? Secrets(MainWindow window) => window.Projects.FirstOrDefault()?.Property(Passenger.Host.CarriedProperties.UserSecretsId);
    }

    /// <summary>A project and a program each written with its folder at once (as <c>dotnet new -o</c> and an agent write
    /// them) are the folder's, each with its run configuration, watched the system's way and as on Linux, each folder on
    /// its own, whose watch on a new folder begins only once the files are written in it.</summary>
    private void A_project_and_a_program_written_each_with_its_folder_are_found_with_their_run_configurations(bool asOnLinux)
    {
        Write("App/App.csproj", Project);
        MainWindow window = Open(asOnLinux ? FolderWatching.Inotify : FolderWatching.Platform, languageServer: false);
        Assert.Equal(["App"], window.Configurations.Select(c => c.Name));

        Write("Tool/Tool.csproj", Project);
        Write("scripts/hello.cs", "#:property PublishAot=false\nConsole.WriteLine(\"hello\");\n");
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Tool") && window.Configurations.Any(c => c.Name == "hello"),
            () => $"Tool.csproj and hello.cs, written with their folders, to have run configurations; there are {string.Join(", ", window.Configurations.Select(c => c.Name))}");
        window.Close();
    }

    /// <summary>A project and a program brought into a folder with no solution with their folders whole, as a copy, a clone or
    /// an unzip lands them (which the system may tell as each folder alone, never the files in it), are the folder's, each
    /// with its run configuration; the project's folder deleted and the program's moved out (as to the Trash), both are
    /// gone. Watched the system's way and as on Linux, each folder on its own.</summary>
    private void A_project_and_a_program_moved_in_with_their_folders_whole_are_found_and_gone_with_their_folders(bool asOnLinux)
    {
        Write("App/App.csproj", Project);
        MainWindow window = Open(asOnLinux ? FolderWatching.Inotify : FolderWatching.Platform, languageServer: false);
        Assert.Equal(["App"], window.Configurations.Select(c => c.Name));
        string elsewhere = PathNames.Canonical(TestFolders.Create("elsewhere-"));
        try
        {
            TestFolders.Write(elsewhere, "Tool/Tool.csproj", Project);
            TestFolders.Write(elsewhere, "Tool/Program.cs", "Console.WriteLine(\"tool\");\n");
            TestFolders.Write(elsewhere, "scripts/hello.cs", "#:property PublishAot=false\nConsole.WriteLine(\"hello\");\n");
            Directory.Move(Path.Combine(elsewhere, "Tool"), Path.Combine(folder, "Tool"));
            Directory.Move(Path.Combine(elsewhere, "scripts"), Path.Combine(folder, "scripts"));
            Ui.WaitFor(() => Projects(window) is ["App", "Tool", "hello"] && Configurations(window) is ["App", "Tool", "hello"],
                () => $"Tool and hello.cs, moved in with their folders, the folder's with their run configurations; its projects are {string.Join(", ", Projects(window))} "
                    + $"and its run configurations {string.Join(", ", Configurations(window))}");

            Directory.Delete(Path.Combine(folder, "Tool"), recursive: true);
            Directory.Move(Path.Combine(folder, "scripts"), Path.Combine(elsewhere, "scripts"));
            Ui.WaitFor(() => Projects(window) is ["App"] && Configurations(window) is ["App"],
                () => $"Tool and hello.cs gone with their folders, and their run configurations; the folder's projects are {string.Join(", ", Projects(window))} "
                    + $"and its run configurations {string.Join(", ", Configurations(window))}");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(elsewhere);
        }

        static IReadOnlyList<string> Projects(MainWindow window) => [.. window.Projects.Select(p => p.Name).Order(StringComparer.Ordinal)];
        static IReadOnlyList<string> Configurations(MainWindow window) => [.. window.Configurations.Select(c => c.Name).Order(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> Names(IEnumerable<ProjectModel> projects) => [.. projects.Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> Names(IEnumerable<Passenger.App.Run.RunConfiguration> configurations) => [.. configurations.Select(c => c.Name).Order(StringComparer.Ordinal)];

    private void A_folders_sln_and_slnx_listing_different_projects_give_the_explorer_and_the_language_server_the_same_one()
    {
        // As dotnet sln migrate leaves a folder, with a project added to the .slnx since.
        Write("App/App.csproj", Project);
        Write("App/Program.cs", "System.Console.WriteLine(\"app\");\n");
        Write("Extra/Extra.csproj", TestProjects.Library());
        Write("Extra/Helper.cs", "public static class Helper\n{\n    public static string Name => \"extra\";\n}\n");
        Write("Extra/Use.cs", "public static class Use\n{\n    public static int Count()\n    {\n        int count = Helper.Name;\n        return count;\n    }\n}\n");
        Write("Foo.sln", ClassicSolution);
        Write("Foo.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Extra/Extra.csproj\" />\n</Solution>\n");
        Restore.Run(Path.Combine(folder, "Foo.slnx"));
        string slnx = Path.Combine(folder, "Foo.slnx");
        string sln = Path.Combine(folder, "Foo.sln");
        MainWindow window = Open();
        Assert.Equal(slnx, window.Solution?.Path);
        Assert.Equal(slnx, window.CSharpService?.Session?.Solution);
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Extra");
        // Helper is known only with Extra's project loaded: without it the error would be an unknown name, not a conversion.
        window.OpenFile("Extra/Use.cs");
        Ui.WaitFor(() => Codes(window).Contains("CS0029"), "the language server to load Extra, which the Explorer shows");

        File.Delete(slnx);
        Ui.WaitFor(() => window.Solution?.Path == sln, "the Explorer to show the .sln");
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Extra");
        Ui.WaitFor(() => window.CSharpService?.Session?.Solution == sln, "the language server to open the .sln the Explorer shows");
        window.Close();
    }

    /// <summary>Every look at the open folder's files walks it one way: Go to File lists each file once by its own name though
    /// links lead to it again, back up the tree or out of the folder, never build output or the system's own files; a folder
    /// with no solution takes its projects from those files but a hidden folder's (a tool's copy); and a folder another
    /// program makes, a link back up in it, is kept by Local History file for file as Go to File lists it.</summary>
    private void Go_to_file_local_history_and_the_folders_projects_see_its_files_alike_through_links_and_hidden_folders()
    {
        Write("App/App.csproj", Project);
        Write("App/Program.cs", "System.Console.WriteLine(\"app\");\n");
        Write("App/Thumbs.db", "the system's own\n");
        Write(".github/ci.yml", "on: push\n");
        Write(".copy/Copy.csproj", Project);
        Write("bin/out.txt", "built\n");
        Write(".DS_Store", "the system's own\n");
        string outside = TestFolders.Create("watch-outside-");
        try
        {
            File.WriteAllText(Path.Combine(outside, "Far.cs"), "class Far { }\n");
            // Alias comes before App by name, so the walk reaches App's files through it first.
            TestFolders.LinkFolder(Path.Combine(folder, "Alias"), Path.Combine(folder, "App"));
            TestFolders.LinkFolder(Path.Combine(folder, "App", "loop"), Path.Combine(folder, "App"));
            TestFolders.LinkFolder(Path.Combine(folder, "App", "up"), folder);
            TestFolders.LinkFolder(Path.Combine(folder, "far"), outside);
            MainWindow window = Open(languageServer: false);
            List<string> listed = [Path.Combine(".copy", "Copy.csproj"), Path.Combine(".github", "ci.yml"), Path.Combine("App", "App.csproj"), Path.Combine("App", "Program.cs")];
            Assert.Equal(listed, window.FileIndex.Files.Order(StringComparer.Ordinal));
            Assert.Equal([PathNames.Canonical(Path.Combine(folder, "App", "App.csproj"))], window.Projects.Select(p => p.Path));

            string made = Path.Combine(folder, "Made");
            string note = PathNames.Canonical(Path.Combine(made, "note.txt"));
            Write("Made/note.txt", "made elsewhere\n");
            Write("Made/.DS_Store", "the system's own\n");
            TestFolders.LinkFolder(Path.Combine(made, "again"), folder);
            Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Made", "note.txt")), "the file of the folder made elsewhere in Go to File");
            Ui.WaitFor(() => window.LocalHistory!.Snapshot().Events.Any(e => e.Path == note), "Local History to keep the file of the folder made elsewhere");
            Ui.Settle();
            Assert.Equal([.. listed, Path.Combine("Made", "note.txt")], window.FileIndex.Files.Order(StringComparer.Ordinal));
            Assert.Equal([note], window.LocalHistory!.Snapshot().Events.Select(e => e.Path).OfType<string>().Where(p => PathNames.IsUnder(p, made)).Distinct());
            window.Close();
        }
        finally
        {
            TestFolders.Delete(outside);
        }
    }

    private static IEnumerable<string?> Codes(MainWindow window) =>
        window.CSharpService?.For(window.EditorArea.Active!)?.Diagnostics.Select(d => d.Code?.ToString()) ?? [];

    /// <summary>A Visual Studio solution naming App alone.</summary>
    private const string ClassicSolution = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        VisualStudioVersion = 17.0.31903.59
        MinimumVisualStudioVersion = 10.0.40219.1
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{6F1A3C0E-1B2D-4C5E-9F70-1A2B3C4D5E6F}"
        EndProject
        Global
            GlobalSection(SolutionConfigurationPlatforms) = preSolution
                Debug|Any CPU = Debug|Any CPU
                Release|Any CPU = Release|Any CPU
            EndGlobalSection
            GlobalSection(ProjectConfigurationPlatforms) = postSolution
                {6F1A3C0E-1B2D-4C5E-9F70-1A2B3C4D5E6F}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                {6F1A3C0E-1B2D-4C5E-9F70-1A2B3C4D5E6F}.Debug|Any CPU.Build.0 = Debug|Any CPU
                {6F1A3C0E-1B2D-4C5E-9F70-1A2B3C4D5E6F}.Release|Any CPU.ActiveCfg = Release|Any CPU
                {6F1A3C0E-1B2D-4C5E-9F70-1A2B3C4D5E6F}.Release|Any CPU.Build.0 = Release|Any CPU
            EndGlobalSection
        EndGlobal

        """;

    private static readonly string Project = TestProjects.Console();

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);

    /// <summary>A window on the folder, or one <paramref name="opened"/> under it, watching it the platform's way unless told
    /// another; with the C# language server only where a test asks C# of it, so a project's folder starts and restores none
    /// for nothing.</summary>
    private MainWindow Open(FolderWatching? watching = null, bool languageServer = true, string? opened = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, Watching = watching ?? FolderWatching.Platform, StartsLanguageServer = languageServer };
        window.Show();
        window.OpenFolder(opened ?? folder);
        Ui.Settle();
        return window;
    }

    private static string Text(MainWindow window) => window.EditorArea.CodeEditor.Document.Snapshot.ToString();
}

/// <summary>What a file made or deleted in the folder costs on its way into or out of Go to File, counted on every thread: the
/// class runs alone, as whatever a class beside the window tests allocates meanwhile would count as the watcher's.</summary>
[Collection(Alone.Name)]
public sealed class FolderWatchBytesTests
{
    /// <summary>What a file made or deleted costs Go to File among twenty thousand against two thousand, and what a
    /// change under build output no watch matches costs, each held to the small case, counted on every thread as this
    /// class runs alone.</summary>
    [AvaloniaFact]
    public async Task What_a_file_change_and_a_childs_kill_cost_stays_the_same_as_the_folder_and_the_machine_grow()
    {
        Flow.StepApart<FolderWatchBytesTests>("a file made or deleted among twenty thousand costs Go to File what it does among two thousand", t => t.A_file_made_or_deleted_among_twenty_thousand_costs_go_to_file_what_it_does_among_two_thousand());
        Flow.StepApart<WatcherChurnTests>("a change under build output no registration matches costs no allocation", t => t.A_change_under_build_output_no_registration_matches_costs_no_allocation());
    }

    /// <summary>A file made in the folder joins Go to File, and one deleted leaves it, without a walk of the folder: over
    /// 20,000 files each batch allocates (on every thread, the least of three) under 8 bytes for each file more than over
    /// 2,000, where walking the folder again cost hundreds; and Go to File then lists what a walk lists, in its order.</summary>
    private void A_file_made_or_deleted_among_twenty_thousand_costs_go_to_file_what_it_does_among_two_thousand()
    {
        string few = PathNames.Canonical(TestFolders.Create("watch-few-"));
        string many = PathNames.Canonical(TestFolders.Create("watch-many-"));
        try
        {
            Areas(few, 10);
            Areas(many, 100);
            (long Made, long Deleted, TimeSpan Took) small = Batches(few, 2000);
            (long Made, long Deleted, TimeSpan Took) large = Batches(many, 20_000);
            TestContext.Current.TestOutputHelper?.WriteLine($"A file made: {small.Made / 1024:N0} KB over 2,000 files, {large.Made / 1024:N0} KB over 20,000; "
                + $"deleted: {small.Deleted / 1024:N0} KB and {large.Deleted / 1024:N0} KB; the quickest batch {small.Took.TotalMilliseconds:N0} ms and {large.Took.TotalMilliseconds:N0} ms");
            Assert.True(large.Made - small.Made < 8 * 18_000,
                $"A file made among 20,000 allocated {large.Made / 1024:N0} KB on its way into Go to File, against {small.Made / 1024:N0} KB among 2,000: 8 bytes or more for each file more, as if the folder were walked again");
            Assert.True(large.Deleted - small.Deleted < 8 * 18_000,
                $"A file deleted among 20,000 allocated {large.Deleted / 1024:N0} KB on its way out of Go to File, against {small.Deleted / 1024:N0} KB among 2,000: 8 bytes or more for each file more");
        }
        finally
        {
            TestFolders.Delete(few);
            TestFolders.Delete(many);
        }

        // Areas of 200 empty files each.
        static void Areas(string root, int areas) => TestFolders.WriteAll(root, Enumerable.Range(0, areas * 200)
            .Select(file => (Path.Combine($"Area{file / 200}", "Features", $"Feature{file / 200}x{file % 200}.cs"), string.Empty)));
    }

    /// <summary>What a file made and a file deleted in a folder allocate from the change to the IDE quiet again, the least of
    /// three of each, and the quickest of them; each checked in Go to File, which then lists what a window opened on the
    /// folder afresh lists.</summary>
    private static (long Made, long Deleted, TimeSpan Took) Batches(string root, int files)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(root);
        Ui.WaitFor(() => window.FileIndex.Count >= files && !window.FileIndex.IsWalking, $"the folder's {files} files indexed");
        bool heard = false;
        window.FolderWatcher!.Changed += (_, changes) => heard |= changes.Structure;
        long made = long.MaxValue;
        long deleted = long.MaxValue;
        TimeSpan took = TimeSpan.MaxValue;
        for (int time = 0; time < 3; time++)
        {
            string relative = Path.Combine("Area0", "Features", $"Made{time}.cs");
            string file = Path.Combine(root, relative);
            made = Math.Min(made, Batch(() => File.WriteAllText(file, "class Made { }\n")));
            Assert.True(window.FileIndex.Files.Contains(relative), $"{relative} made is not in Go to File");
            if (time < 2)
            {
                deleted = Math.Min(deleted, Batch(() => File.Delete(file)));
                Assert.False(window.FileIndex.Files.Contains(relative), $"{relative} deleted is still in Go to File");
            }
        }
        deleted = Math.Min(deleted, Batch(() => File.Delete(Path.Combine(root, "Area1", "Features", "Feature1x0.cs"))));
        Assert.Equal(files, window.FileIndex.Count);
        Assert.Equal(FolderWatchTests.Walked(root), window.FileIndex.Files);
        window.Close();
        return (made, deleted, took);

        long Batch(Action change)
        {
            Ui.Settle();
            heard = false;
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long began = Stopwatch.GetTimestamp();
            change();
            Ui.WaitFor(() => heard, "the folder watcher to hear of the change");
            Ui.Settle();
            took = TimeSpan.FromTicks(Math.Min(took.Ticks, Stopwatch.GetElapsedTime(began).Ticks));
            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }
    }
}
