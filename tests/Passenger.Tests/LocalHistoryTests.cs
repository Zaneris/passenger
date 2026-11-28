using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.History;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Local History over real files: every save and every change made elsewhere is a version, listed newest first
/// in the Local History window with its time and name ("External change"), the person's labels among them; a version
/// shows beside the file as it is now, Revert puts it back as one undoable step and a diff's arrow one change of it; a
/// folder's history lists its files' versions and brings back one deleted; a file's history follows it through a rename
/// in the Solution view; a tree another program brings in at once is passed over unread; and the store keeps 5 days of
/// activity and no more content than its limit.</summary>
public sealed class LocalHistoryTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add()\n    {\n        count++;\n    }\n\n    int Total => count;\n}\n";

    // Each save changes another line of it, so a version against now differs in as many places as saves since.
    private static readonly string Saved1 = Original.Replace("    int count;", "    long int count;", StringComparison.Ordinal);
    private static readonly string Saved2 = Saved1.Replace("        count++;", "        count++; count++;", StringComparison.Ordinal);
    private static readonly string Saved3 = Saved2.Replace("    int Total", "    public int Total", StringComparison.Ordinal);

    private readonly string folder = PathNames.Canonical(TestFolders.Create("local-history-"));
    private readonly string trash = TestFolders.Create("trash-");
    private long clock;

    public LocalHistoryTests() => Trash.Folder = trash;

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    /// <summary>A file's Local History as one flow, each step the check one test made in an instance of its own: three
    /// saves and a change made elsewhere kept as four versions newest first, an old version's diff beside the open
    /// file, Revert putting a version back as one undoable step and the diff's arrow one change, a revert across a
    /// conversion and of a file open as its bytes writing the version's own text, a label showing the file as it was,
    /// and a revert making no row again.</summary>
    [AvaloniaFact]
    public void A_files_versions_are_kept_newest_first_and_any_is_put_back_as_one_undoable_step_in_its_own_bytes()
    {
        Flow.StepApart<LocalHistoryTests>("three saves and a change made elsewhere are four versions newest first the last named an external change", t => t.Three_saves_and_a_change_made_elsewhere_are_four_versions_newest_first_the_last_named_an_external_change());
        Flow.StepApart<LocalHistoryTests>("an old versions diff shows what changed since beside the open file", t => t.An_old_versions_diff_shows_what_changed_since_beside_the_open_file());
        Flow.StepApart<LocalHistoryTests>("revert puts a version back as one undoable step and a diffs arrow puts back one change", t => t.Revert_puts_a_version_back_as_one_undoable_step_and_a_diffs_arrow_puts_back_one_change());
        Flow.StepApart<LocalHistoryTests>("revert across a conversion brings back the versions text in its own encoding", t => t.Revert_across_a_conversion_brings_back_the_versions_text_in_its_own_encoding());
        Flow.StepApart<LocalHistoryTests>("revert of a file open as its bytes to a version from when it was text writes that version", t => t.Revert_of_a_file_open_as_its_bytes_to_a_version_from_when_it_was_text_writes_that_version());
        Flow.StepApart<LocalHistoryTests>("put label puts a label among the versions that shows the file as it was then", t => t.Put_label_puts_a_label_among_the_versions_that_shows_the_file_as_it_was_then());
        Flow.StepApart<LocalHistoryTests>("a revert of a text makes no row again and one bringing back a file only its own", t => t.A_revert_of_a_text_makes_no_row_again_and_one_bringing_back_a_file_only_its_own());
    }

    /// <summary>What Local History names each change as one flow, each step one test's check in an instance of its own: a
    /// git checkout's, an update's and a stash's changes under each one's name, the files opened or not, a conflict resolved
    /// each way under that resolution's, a change written after a checkout's moment an external change of its own, a save
    /// written as the history reads its file kept once, a save made while a replace writes its closed files filed apart from
    /// it, and a file a server's edit or rename writes over keeping what it held, Undo putting it back.</summary>
    [AvaloniaFact]
    public void Each_change_is_kept_under_the_name_of_what_made_it_and_what_it_wrote_over_kept_too()
    {
        Flow.StepApart<LocalHistoryTests>("what a git checkout an update and a stash write is kept under each ones name with what each file held", t => t.What_a_git_checkout_an_update_and_a_stash_write_is_kept_under_each_ones_name_with_what_each_file_held());
        Flow.StepApart<LocalHistoryTests>("a conflict resolved each way is kept under that resolutions name", t => t.A_conflict_resolved_each_way_is_kept_under_that_resolutions_name());
        Flow.StepApart<LocalHistoryTests>("a change written after a checkouts moment is an external change of its own though heard during the checkout", t => t.A_change_written_after_a_checkouts_moment_is_an_external_change_of_its_own_though_heard_during_the_checkout());
        Flow.StepApart<LocalHistoryTests>("a save written as the history reads its file for a change heard before is kept once as the save", t => t.A_save_written_as_the_history_reads_its_file_for_a_change_heard_before_is_kept_once_as_the_save());
        Flow.StepApart<LocalHistoryTests>("a save made while a replace writes its closed files is not filed under the replaces name", t => t.A_save_made_while_a_replace_writes_its_closed_files_is_not_filed_under_the_replaces_name());
        Flow.StepApart<LocalHistoryTests>("a file a servers edit makes over one there keeps what it held in the history and undo puts it back", t => t.A_file_a_servers_edit_makes_over_one_there_keeps_what_it_held_in_the_history_and_undo_puts_it_back());
        Flow.StepApart<LocalHistoryTests>("a servers rename over an open file with edits keeps them and undo writes that file back", t => t.A_servers_rename_over_an_open_file_with_edits_keeps_them_and_undo_writes_that_file_back());
    }

    /// <summary>A folder's Local History as one flow, each step one test's check in an instance of its own: a folder
    /// first read or brought in at once, the history's own writes in a state folder inside it, a file deleted in the
    /// Explorer brought back, two closed files reverted off the window's thread under one Undo, a file made later moved
    /// to the Trash, and a file's history followed through a rename.</summary>
    [AvaloniaFact]
    public void A_folders_history_lists_its_files_versions_and_puts_back_files_deleted_moved_or_brought_in()
    {
        Flow.StepApart<LocalHistoryTests>("a folder named as a file in it is first read brings no change where a folder that comes brings its files", t => t.A_folder_named_as_a_file_in_it_is_first_read_brings_no_change_where_a_folder_that_comes_brings_its_files());
        Flow.StepApart<LocalHistoryTests>("a tree brought in at once is never read while a known files change meanwhile and its own once opened are kept", t => t.A_tree_brought_in_at_once_is_never_read_while_a_known_files_change_meanwhile_and_its_own_once_opened_are_kept());
        Flow.StepApart<LocalHistoryTests>("what the history writes in a state folder inside the open folder is heard and starts no more writes", t => t.What_the_history_writes_in_a_state_folder_inside_the_open_folder_is_heard_and_starts_no_more_writes());
        Flow.StepApart<LocalHistoryTests>("a folders history lists its files versions and brings back a file deleted in the explorer", t => t.A_folders_history_lists_its_files_versions_and_brings_back_a_file_deleted_in_the_explorer());
        Flow.StepApart<LocalHistoryTests>("a folders revert of two closed files writes neither on the windows thread and one undo takes both back", t => t.A_folders_revert_of_two_closed_files_writes_neither_on_the_windows_thread_and_one_undo_takes_both_back());
        Flow.StepApart<LocalHistoryTests>("a folders revert to before a file was made moves it to the Trash and one undo brings it back", t => t.A_folders_revert_to_before_a_file_was_made_moves_it_to_the_Trash_and_one_undo_brings_it_back());
        Flow.StepApart<LocalHistoryTests>("a files history follows it through a rename in the solution view", t => t.A_files_history_follows_it_through_a_rename_in_the_solution_view());
    }

    /// <summary>Local History shared by Passengers on one state as one flow, each step the check one test made in an
    /// instance of its own: a change kept by its maker and heard by another listed once under its maker's name, a label
    /// put while another rewrites the log kept after the rewrite, and the store keeping days of activity and no more
    /// content than its limit.</summary>
    [AvaloniaFact]
    public void Passengers_sharing_a_state_keep_one_history_within_its_limits()
    {
        Flow.StepApart<LocalHistoryTests>("a change kept by its maker and as heard by another passenger on the same state lists once under its makers name", t => t.A_change_kept_by_its_maker_and_as_heard_by_another_passenger_on_the_same_state_lists_once_under_its_makers_name());
        Flow.StepApart<LocalHistoryTests>("a label put while another passenger on the same state writes the log anew is kept after its rewrite", t => t.A_label_put_while_another_passenger_on_the_same_state_writes_the_log_anew_is_kept_after_its_rewrite());
        Flow.StepApart<LocalHistoryTests>("the store keeps days of activity as Riders does and no more content than its limit", t => t.The_store_keeps_days_of_activity_as_Riders_does_and_no_more_content_than_its_limit());
    }

    private void Three_saves_and_a_change_made_elsewhere_are_four_versions_newest_first_the_last_named_an_external_change()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);

        string elsewhere = Saved3.Replace("class Shop", "sealed class Shop", StringComparison.Ordinal);
        File.WriteAllText(shop, elsewhere);
        Ui.WaitFor(() => Text(window) == elsewhere, "the open file to take the change made elsewhere");

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.LocalHistory), "Show History left the Local History window hidden");
        Assert.Equal(["Shop.cs"], window.LocalHistoryWindow.Titles);
        Ui.Landed(() => history.Rows.Count == 4, "the four versions: three saves and the change made elsewhere");
        Assert.Equal(["External change", "Change", "Change", "Change"], history.Rows.Select(r => r.Title.English));
        Assert.True(history.Rows.Zip(history.Rows.Skip(1)).All(p => p.First.Time >= p.Second.Time), "The versions are not listed newest first");

        Assert.Same(history.Rows[0], history.Selected);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved3, "the file as saved last, before the external change");
        Assert.Equal([new DiffHunk(0, 1, 0, 1)], history.Diff!.Hunks);
        window.Close();
    }

    private void An_old_versions_diff_shows_what_changed_since_beside_the_open_file()
    {
        Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);

        Ui.ChooseFromEditorMenu(window, "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count == 3, "the three saves");
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the oldest version, the file before its first save");
        Assert.Same(window.EditorArea.Active!.Text, history.Diff!.Right.Document);
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(6, 1, 6, 1), new DiffHunk(9, 1, 9, 1)], history.Diff.Hunks);

        history.Select(history.Rows[1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved1, "the version after the first save");
        Assert.Equal([new DiffHunk(6, 1, 6, 1), new DiffHunk(9, 1, 9, 1)], history.Diff!.Hunks);
        window.Close();
    }

    private void Revert_puts_a_version_back_as_one_undoable_step_and_a_diffs_arrow_puts_back_one_change()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);
        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 3, "the three saves");

        history.Select(history.Rows[1]);
        Ui.Settle();
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.Equal(Saved1, Text(window));
        Assert.Equal(Saved1, File.ReadAllText(shop));
        Assert.False(window.EditorArea.Active!.IsModified, "The revert was not written to the file");
        Ui.Landed(() => history.Rows.Count == 4 && history.Rows[0].Title.English.StartsWith("Revert to ", StringComparison.Ordinal), "the revert kept as a version of its own");

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Saved3, Text(window));

        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original && history.Diff.Hunks.Count == 3, "the first version's three changes");
        history.Diff!.Revert(history.Diff.Hunks[1]);
        Ui.Settle();
        Assert.Equal(Saved3.Replace("count++; count++;", "count++;", StringComparison.Ordinal), Text(window));
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(9, 1, 9, 1)], history.Diff.Hunks);
        window.Close();
    }

    private void Revert_across_a_conversion_brings_back_the_versions_text_in_its_own_encoding()
    {
        const string Cafe = "public class Café { }\n";
        string path = Write("Cafe.cs", "class Café { }\n");
        MainWindow window = Open("Cafe.cs");
        Save(window, 0, 0, "public ");
        // Converted to Latin-1 from the status bar's encoding widget: é, two bytes in UTF-8, is one now.
        FilePropertiesTests.Click(window, "EncodingButton");
        FilePropertiesTests.Choose(window, "ISO-8859-1");
        FilePropertiesTests.Answer(window, "Cafe.cs: Reload or Convert to ISO-8859-1", "Convert");
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes(Cafe), File.ReadAllBytes(path));

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 2, "the save and the conversion");
        history.Select(history.Rows[0]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Cafe, "the version before the conversion");
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.True(Text(window) == Cafe, $"The revert read the UTF-8 version in Latin-1: {Text(window)}");
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Cafe), File.ReadAllBytes(path));
        Assert.Equal("UTF-8", FilePropertiesTests.Shown(window, "EncodingText"));
        Assert.False(window.EditorArea.Active!.IsModified, $"The revert was not written: {window.Status}");

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Cafe, Text(window));
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes(Cafe), File.ReadAllBytes(path));
        Assert.Equal("ISO-8859-1", FilePropertiesTests.Shown(window, "EncodingText"));
        window.Close();
    }

    /// <summary>A binary file's tab holds its bytes drawn as signs, none of the file's text: a revert to a version from
    /// when the file was text writes that version's bytes, and the tab shows them.</summary>
    private void Revert_of_a_file_open_as_its_bytes_to_a_version_from_when_it_was_text_writes_that_version()
    {
        string path = Write("notes.dat", "First\n");
        MainWindow window = Open("notes.dat");
        File.WriteAllText(path, "Second\n");
        Ui.WaitFor(() => Text(window) == "Second\n", "the tab to take the change made elsewhere");
        window.EditorArea.Close(window.EditorArea.Active!);
        Ui.Settle();

        // Written elsewhere as bytes no text holds, it opens as them.
        File.WriteAllBytes(path, [0x7F, (byte)'E', 0, 1, (byte)'\n']);
        window.OpenFile(path);
        Ui.Settle();
        Assert.True(window.EditorArea.Active is { IsBinary: true }, "notes.dat, now binary, did not open as its bytes' text");

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 1, "the change made elsewhere while it was text");
        history.Select(history.Rows[0]);
        Ui.Settle();
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.True(File.ReadAllText(path) == "First\n", $"The revert did not write the version back: {window.Status}");
        Ui.WaitFor(() => Text(window) == "First\n", "the tab to show the bytes the revert wrote");
        Assert.False(window.EditorArea.Active!.IsModified, "The revert left the binary file's tab modified");
        window.Close();
    }

    private void Put_label_puts_a_label_among_the_versions_that_shows_the_file_as_it_was_then()
    {
        Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        Save(window, 2, 4, "long ");
        Ui.ChooseFromEditorMenu(window, "Local History", "Put Label…");
        Assert.IsType<NamePopup>(window.Popup);
        window.KeyTextInput("Before cleanup");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, "The label's name was refused");
        Assert.Equal("Label Before cleanup created", window.Status);
        Save(window, 6, 8, "count++; ");

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 3, "the two saves and the label between them");
        Assert.Equal(["Change", "Before cleanup", "Change"], history.Rows.Select(r => r.Title.English));
        Assert.True(history.Rows[1].IsLabel, "The label is listed as a change, not a label");
        history.Select(history.Rows[1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved1, "the file as it was when the label was put");
        window.Close();
    }

    /// <summary>What a stash, an update from the upstream and a checkout write over files, the one open and those never
    /// opened here, is kept under each one's name, never as an external change or a file made, with what the file held
    /// before it: git writes a file anew, which Windows gives the old file's creation time.</summary>
    private void What_a_git_checkout_an_update_and_a_stash_write_is_kept_under_each_ones_name_with_what_each_file_held()
    {
        string origin = TestFolders.Create("history-origin-");
        TestFolders.Write(origin, "Shop.cs", Original);
        TestFolders.Write(origin, "Notes.txt", "main\n");
        TestFolders.Write(origin, "Readme.md", "# Shop\n");
        TestFolders.Write(origin, "Todo.txt", "count\n");
        TestGit.Init(origin);
        TestGit.Run(origin, "switch", "-q", "-c", "side");
        File.WriteAllText(Path.Combine(origin, "Shop.cs"), Saved1);
        File.WriteAllText(Path.Combine(origin, "Notes.txt"), "side\n");
        TestGit.Run(origin, "commit", "-q", "-am", "Side");
        TestGit.Run(origin, "switch", "-q", "main");
        TestGit.Run(folder, "clone", "-q", "-c", "core.autocrlf=false", origin, ".");
        TestGit.Run(folder, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
        TestGit.Run(folder, "config", "user.name", "Passenger Tests");
        File.WriteAllText(Path.Combine(origin, "Readme.md"), "# Shop\n\nSells things.\n");
        TestGit.Run(origin, "commit", "-q", "-am", "Readme");
        File.WriteAllText(Path.Combine(folder, "Todo.txt"), "count\nsell\n");
        MainWindow window = Open("Shop.cs");
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the folder's repository");
        LocalHistory history = window.LocalHistory!;

        VcsOutcome stashed = Ui.Await(window.Vcs.StashAsync("Todo"), "the change to Todo.txt stashed");
        Assert.True(stashed.Succeeded, stashed.Message);
        KeptAs("Todo.txt", "Git stash", "count\nsell\n");
        VcsOutcome updated = Ui.Await(window.Vcs.UpdateAsync(), "main updated from the upstream");
        Assert.True(updated.Succeeded, updated.Message);
        KeptAs("Readme.md", "Update from VCS", "# Shop\n");
        Task<VcsOutcome> checkout = window.Vcs.CheckoutAsync("side");
        Ui.WaitFor(() => checkout.IsCompleted && Text(window) == Saved1, "side checked out and the open file following it");
        VcsOutcome checkedOut = Ui.Await(checkout, "side checked out");
        Assert.True(checkedOut.Succeeded, checkedOut.Message);
        KeptAs("Notes.txt", "Git checkout", "main\n");
        HistoryTab shown = ShowFromFileMenu(window, "Show History…");
        Ui.WaitFor(() => shown.Rows.Count == 1, "the checkout's change to the open file");
        Assert.Equal("Git checkout", shown.Rows[0].Title.English);
        Ui.Landed(() => shown.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as main had it");
        window.Close();

        // A file never opened here, its last change kept under the operation's name with what the file held before it.
        void KeptAs(string name, string title, string before)
        {
            string path = PathNames.Canonical(Path.Combine(folder, name));
            IReadOnlyList<HistoryRow> Rows()
            {
                (IReadOnlyList<HistoryEvent> events, IReadOnlyDictionary<string, string?> known) = history.Snapshot();
                return HistoryRows.OfFile(events, known, path);
            }
            Ui.WaitFor(() => Rows() is [{ } row, ..] && row.Title.English == title,
                () => $"{name}, never opened, kept by {title}: its history lists [{string.Join(", ", Rows().Select(r => r.Title.English))}]");
            Ui.WaitFor(() => !history.IsRecording, "the history to keep what it heard");
            HistoryRow kept = Rows()[0];
            Assert.True(kept.Title.English == title && kept.Version is { } version && Encoding.UTF8.GetString(history.Content(version)!) == before,
                $"{name}, never opened, is kept by {title} as [{string.Join(", ", Rows().Select(r => r.Title.English))}], its row with {(kept.Version is { } held ? $"\"{Encoding.UTF8.GetString(history.Content(held)!)}\"" : "no file")} before it, not \"{before}\"");
        }
    }

    /// <summary>A merge's conflicts resolved by Accept Yours, Accept Theirs and the merge window's Apply are each kept under
    /// that resolution's name, never as an external change, with the file in conflict before it.</summary>
    private void A_conflict_resolved_each_way_is_kept_under_that_resolutions_name()
    {
        string[] names = ["Yours.txt", "Theirs.txt", "Merged.txt"];
        foreach (string name in names)
        {
            Write(name, "one\n");
        }
        TestGit.Init(folder);
        TestGit.Run(folder, "switch", "-q", "-c", "side");
        foreach (string name in names)
        {
            Write(name, "side\n");
        }
        TestGit.Run(folder, "commit", "-q", "-am", "Side");
        TestGit.Run(folder, "switch", "-q", "main");
        foreach (string name in names)
        {
            Write(name, "main\n");
        }
        TestGit.Run(folder, "commit", "-q", "-am", "Main");
        ChildResult merging = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("git", ["merge", "-q", "side"], folder), limit: TimeSpan.FromMinutes(1))),
            "git merge side");
        Assert.False(merging.Succeeded, $"The merge was to stop for its conflicts: {merging.Error}{merging.Output}");
        string conflicted = File.ReadAllText(Path.Combine(folder, "Merged.txt"));
        MainWindow window = Open("Yours.txt");
        Ui.WaitFor(() => window.Vcs.Conflicts.Count == 3, "the merge's three conflicts");

        GitConflict Of(string name) => window.Vcs.Conflicts.Single(c => Path.GetFileName(c.Path) == name);
        Assert.True(Ui.Await(window.Vcs.AcceptAsync([Of("Yours.txt")], yours: true), "Accept Yours").Succeeded, window.Status);
        Assert.True(Ui.Await(window.Vcs.AcceptAsync([Of("Theirs.txt")], yours: false), "Accept Theirs").Succeeded, window.Status);
        Assert.True(Ui.Await(window.Vcs.ResolveAsync(Of("Merged.txt").Path, "main\nside\n"), "the merge window's Apply").Succeeded, window.Status);
        Assert.Equal(("main\n", "side\n", "main\nside\n"),
            (File.ReadAllText(Path.Combine(folder, "Yours.txt")), File.ReadAllText(Path.Combine(folder, "Theirs.txt")), File.ReadAllText(Path.Combine(folder, "Merged.txt"))));
        foreach ((string name, string title) in (ReadOnlySpan<(string, string)>)[("Yours.txt", "Accept Yours"), ("Theirs.txt", "Accept Theirs"), ("Merged.txt", "Resolve Conflict")])
        {
            window.OpenFile(Path.Combine(folder, name));
            Ui.Settle();
            HistoryTab history = ShowFromFileMenu(window, "Show History…");
            Ui.WaitFor(() => history.Rows.Count > 0 && history.Rows[0].Title.English != LocalHistory.ExternalChange || history.Rows.Count > 1,
                () => $"{name}'s resolution kept, its history listing [{string.Join(", ", history.Rows.Select(r => r.Title.English))}]");
            Assert.True(history.Rows[0].Title.English == title, $"{name} resolved by {title} is kept as [{string.Join(", ", history.Rows.Select(r => r.Title.English))}]");
            if (name == "Merged.txt")
            {
                history.Select(history.Rows[0]);
                Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == conflicted, "the file as it stood in conflict, before the merge window's Apply");
            }
        }
        window.Close();
    }

    /// <summary>What a file holds once Local History reads a change heard while a Git checkout was the folder's activity, but
    /// written after the checkout's moment was over (read late, behind other work), is an external change of its own at
    /// the time it was read, never kept under the checkout's name and earlier time: the checkout's row still shows, and
    /// reverts to, what the file held before the checkout, and the later write is a row of its own above it.</summary>
    private void A_change_written_after_a_checkouts_moment_is_an_external_change_of_its_own_though_heard_during_the_checkout()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        LocalHistory history = window.LocalHistory!;
        using (history.During("Git checkout"))
        {
            File.WriteAllText(shop, Saved1);
            history.Noticed([shop], []);
            Ui.WaitFor(() => history.Snapshot().Events.Any(e => e.Name == "Git checkout"), "the checkout's change kept under its name");
        }
        // Heard a moment after the checkout ended, as its own writes are, but written past its moment.
        File.WriteAllText(shop, Saved2);
        File.SetLastWriteTimeUtc(shop, DateTime.UtcNow + TimeSpan.FromMinutes(1));
        history.Noticed([shop], []);
        Ui.Settle();

        (IReadOnlyList<HistoryEvent> events, IReadOnlyDictionary<string, string?> known) = history.Snapshot();
        IReadOnlyList<HistoryRow> rows = HistoryRows.OfFile(events, known, shop);
        Assert.True(rows.Count == 2 && rows[0].Title.English == LocalHistory.ExternalChange && rows[1].Title.English == "Git checkout",
            $"A change written after the checkout's moment was kept as {string.Join(", ", rows.Select(r => r.Title.English))}, not an external change above the checkout");
        Assert.Equal(Saved1, Encoding.UTF8.GetString(history.Content(rows[0].Version!)!));
        Assert.Equal(Original, Encoding.UTF8.GetString(history.Content(rows[1].Version!)!));
        window.Close();
    }

    /// <summary>A save written while Local History reads its file for a change the watcher heard before it (the last save's,
    /// heard late) is kept once, as the save: the history reads the save's bytes before the save's own record comes, as an
    /// external change, and that record takes its place, never kept beside it with an external change back to what the
    /// save wrote over.</summary>
    private void A_save_written_as_the_history_reads_its_file_for_a_change_heard_before_is_kept_once_as_the_save()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        LocalHistory history = window.LocalHistory!;
        Save(window, 2, 4, "long ");
        using ManualResetEventSlim release = new();
        int reads = 0;
        void Held(string path)
        {
            if (PathNames.Same(path, shop) && Interlocked.Increment(ref reads) == 1)
            {
                release.Wait(TimeSpan.FromSeconds(30).Scaled());
            }
        }
        history.Reading += Held;
        try
        {
            // The watcher's word of the first save, read only once the second is written.
            history.Noticed([shop], []);
            Ui.PumpUntil(() => Volatile.Read(ref reads) > 0, "Local History reading Shop.cs for the change heard");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(6, 8));
            window.KeyTextInput("count++; ");
            Ui.Run(window, AppActions.Save, settle: false);
            Ui.PumpUntil(() => window.EditorArea.Active?.IsModified == false, "Shop.cs saved again");
            Assert.Equal(Saved2, File.ReadAllText(shop));
            release.Set();
            Ui.Settle();
        }
        finally
        {
            release.Set();
            history.Reading -= Held;
        }

        (IReadOnlyList<HistoryEvent> events, IReadOnlyDictionary<string, string?> known) = history.Snapshot();
        IReadOnlyList<HistoryRow> rows = HistoryRows.OfFile(events, known, shop);
        Assert.True(rows.Count == 2 && rows.All(r => r.Title.English == "Change"),
            $"Two saves, the second written as the first's change heard was read, were kept as {string.Join(", ", rows.Select(r => r.Title.English))}, not two changes");
        Assert.Equal(Saved1, Encoding.UTF8.GetString(history.Content(rows[0].Version!)!));
        Assert.Equal(Original, Encoding.UTF8.GetString(history.Content(rows[1].Version!)!));
        window.Close();
    }

    /// <summary>A folder the watcher names though nothing came in it, as Windows names one as a file in it is first read
    /// since it was written (a scanner's read, a build's), brings no change: its files, there as the folder opened and never
    /// looked at or saved here, are not kept as made by another program. A folder that comes is looked in, its file kept.</summary>
    private void A_folder_named_as_a_file_in_it_is_first_read_brings_no_change_where_a_folder_that_comes_brings_its_files()
    {
        Write("Shop.cs", Original);
        string project = Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("Lib/Notes.txt", "a note\n");
        MainWindow window = Open("Shop.cs");
        LocalHistory history = window.LocalHistory!;
        history.Noticed([Path.GetDirectoryName(project)!], []);
        Ui.WaitFor(() => !history.IsRecording, "the history to read the folder Windows named");

        string readme = Write("Docs/Readme.md", "# Docs\n");
        Ui.WaitFor(() => HistoryRows.OfFolder(history.Snapshot().Events, folder).Any(r => r.Files.Contains(readme, PathNames.Comparer)),
            "the folder that came looked in, its file kept");
        Ui.WaitFor(() => !history.IsRecording, "the history to keep what it heard");
        IReadOnlyList<HistoryRow> rows = HistoryRows.OfFolder(history.Snapshot().Events, folder);
        Assert.True(!rows.Any(r => r.Files.Any(f => PathNames.Within(f, Path.GetDirectoryName(project)!))),
            $"The folder Windows names as a file in it is first read is kept as changed by another program: {string.Join("; ", rows.Select(r => $"{r.Title.English} [{string.Join(", ", r.Files.Select(Path.GetFileName))}]"))}");
        window.Close();
    }

    /// <summary>A tree another program brings into the open folder at once (a library copied in, an SDK unpacked) is a bulk
    /// arrival: none of its files is read, nor listed in the folder's history, while a file the history knew that another
    /// program changed meanwhile is kept as an external change; a file of the tree is read once it is opened, so a change
    /// made to it elsewhere after is kept with what it held before.</summary>
    private void A_tree_brought_in_at_once_is_never_read_while_a_known_files_change_meanwhile_and_its_own_once_opened_are_kept()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        LocalHistory history = window.LocalHistory!;
        ConcurrentQueue<string> read = new();
        history.Reading += read.Enqueue;
        string outside = TestFolders.Create("library-");
        TestFolders.WriteAll(outside, Enumerable.Range(0, BulkArrivals.AtOnce + 1).Select(piece => ($"Library/Part{piece % 10}/Piece{piece}.cs", $"class Piece{piece}\n{{\n}}\n")));

        Directory.Move(Path.Combine(outside, "Library"), Path.Combine(folder, "Library"));
        File.WriteAllText(shop, Saved1);
        Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("Library", "Part0", "Piece0.cs")) && Text(window) == Saved1,
            "the library in Go to File and the open file following its change made elsewhere");
        Ui.Settle();
        string library = PathNames.Canonical(Path.Combine(folder, "Library"));
        string[] readThere = [.. read.Where(p => PathNames.IsUnder(p, library))];
        Assert.True(readThere.Length == 0, $"Local History read {readThere.Length} files of a tree brought in at once, which it keeps none of: {string.Join(", ", readThere.Take(5))}");
        HistoryTab all = window.ShowLocalHistory(folder)!;
        Ui.Landed(() => all.Rows.Count > 0, "the folder's history");
        Assert.True(all.Rows.All(r => r.Files.All(f => !PathNames.IsUnder(f, library))),
            $"The folder's history lists the tree brought in at once: {string.Join("; ", all.Rows.Select(r => $"{r.Title.English} [{string.Join(", ", r.Files)}]"))}");
        Assert.True(all.Rows.Any(r => r.Title.English == LocalHistory.ExternalChange && r.Files.SequenceEqual([shop])),
            $"A known file another program changed as the tree came is not kept as an external change: {string.Join("; ", all.Rows.Select(r => r.Title.English))}");

        string opened = Path.Combine(library, "Part0", "Piece0.cs");
        window.OpenFile(opened);
        Ui.Settle();
        const string Changed = "sealed class Piece0\n{\n}\n";
        File.WriteAllText(opened, Changed);
        Ui.WaitFor(() => Text(window) == Changed, "the tree's file opened following its change made elsewhere");
        HistoryTab its = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => its.Rows.Count == 1, "the change made elsewhere to the tree's file once opened");
        Assert.Equal(LocalHistory.ExternalChange, its.Rows[0].Title.English);
        Ui.Landed(() => its.Diff?.Left.Document.Snapshot.ToString() == "class Piece0\n{\n}\n", "the tree's file as it came");
        window.Close();
    }

    /// <summary>A state folder inside the open folder, named through a link to it (as macOS names its temporary folder
    /// through /var), has what the history writes there heard by the folder's watcher and kept by nothing: a change made
    /// elsewhere is kept once, and once its write is heard the history neither reads its own files nor writes again.</summary>
    private void What_the_history_writes_in_a_state_folder_inside_the_open_folder_is_heard_and_starts_no_more_writes()
    {
        string shop = Write("Shop.cs", Original);
        string linked = Path.Combine(TestFolders.Create("history-link-"), "open");
        TestFolders.LinkFolder(linked, folder);
        string state = PathNames.Canonical(Path.Combine(folder, "state"));
        MainWindow window = Open("Shop.cs", state: Path.Combine(linked, "state"), keeping: new HistoryKeeping());
        LocalHistory history = window.LocalHistory!;
        string log = Path.Combine(PathNames.Canonical(history.Root), "changes.log");
        ConcurrentQueue<string> read = new();
        history.Reading += read.Enqueue;
        long heardAt = -1;
        window.FolderWatcher!.Changed += (_, changes) =>
        {
            if (changes.Paths.Contains(log))
            {
                heardAt = new FileInfo(log).Length;
            }
        };
        try
        {
            File.WriteAllText(shop, Saved1);
            Ui.WaitFor(() => HistoryRows.OfFile(history.Snapshot().Events, history.Snapshot().Known, shop).Count > 0, "the change made elsewhere kept");
            Ui.WaitFor(() => File.Exists(log) && heardAt >= new FileInfo(log).Length, () => $"the history's log heard as long as it is ({heardAt} of {new FileInfo(log).Length} bytes)");
            Ui.Settle();
            long written = new FileInfo(log).Length;
            Ui.Hold(TimeSpan.FromSeconds(1), "longer than the watcher takes to hear a write and the history to keep it, for a write heard to bring another");

            Assert.True(new FileInfo(log).Length == written, $"Local History wrote its log again, {new FileInfo(log).Length - written} bytes, after hearing its own write in the state folder: it keeps its own writes, for good");
            string[] own = [.. read.Where(p => PathNames.Within(p, state))];
            Assert.True(own.Length == 0, $"Local History read {own.Length} files of its own state folder, which it keeps none of: {string.Join(", ", own.Take(5))}");
            HistoryRow[] rows = [.. HistoryRows.OfFolder(history.Snapshot().Events, folder)];
            Assert.True(rows.All(r => r.Files.All(f => !PathNames.Within(f, state))),
                $"The folder's history lists the state folder's files: {string.Join("; ", rows.Select(r => $"{r.Title.English} [{string.Join(", ", r.Files)}]"))}");
        }
        finally
        {
            history.Reading -= read.Enqueue;
            window.Close();
        }
    }

    private void A_folders_history_lists_its_files_versions_and_brings_back_a_file_deleted_in_the_explorer()
    {
        string a = Write("src/A.cs", "class A\n{\n}\n");
        string b = Write("src/B.cs", "class B\n{\n}\n");
        Write("Notes.txt", "notes\n");
        MainWindow window = Open("src/A.cs");
        Save(window, 0, 0, "sealed ");
        window.OpenFile(b);
        Save(window, 0, 0, "sealed ");
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Save(window, 0, 5, " kept");
        const string savedB = "sealed class B\n{\n}\n";
        Assert.Equal(savedB, File.ReadAllText(b));

        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ChooseFromRowMenu(window, Show(window, b), "Edit", "Delete…");
        Ui.WaitFor(() => !File.Exists(b), "B.cs deleted");

        Ui.ChooseFromRowMenu(window, Show(window, Path.Combine(folder, "src")), "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Assert.Equal("src", window.LocalHistoryWindow.Titles.Single());
        Ui.Landed(() => history.Rows.Count == 3, "A.cs's save, B.cs's save and B.cs's deletion");
        Assert.Equal(["Delete B.cs", "Modify B.cs", "Modify A.cs"], history.Rows.Select(r => r.Title.English));

        Ui.Landed(() => history.Files.Count == 1, "the file the deletion took");
        Assert.Equal((PathNames.Canonical(b), ChangeKind.Deleted), (history.Files[0].Path, history.Files[0].Kind));
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == savedB, "B.cs as it was deleted");
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.Equal(savedB, File.ReadAllText(b));

        history.Select(history.Rows.First(r => r.Title.English == "Modify A.cs"));
        Ui.Landed(() => history.Files.Count == 2, "A.cs and B.cs changed since");
        Assert.Equal([(PathNames.Canonical(a), ChangeKind.Modified), (PathNames.Canonical(b), ChangeKind.Modified)], history.Files.Select(f => (f.Path, f.Kind)));
        window.Close();
    }

    /// <summary>Revert Selected and Later Changes over a folder writes its closed files in turn with every other write of
    /// closed files, off the window's thread, and is one command of Undo: one Undo, asked once, takes back both files.</summary>
    private void A_folders_revert_of_two_closed_files_writes_neither_on_the_windows_thread_and_one_undo_takes_both_back()
    {
        const string A = "class A\n{\n}\n";
        const string B = "class B\n{\n}\n";
        string a = Write("src/A.cs", A);
        string b = Write("src/B.cs", B);
        MainWindow window = Open("src/A.cs");
        Save(window, 0, 0, "sealed ");
        window.OpenFile(b);
        Save(window, 0, 0, "sealed ");
        foreach (Passenger.App.Workspace.OpenDocument open in window.EditorArea.Documents.ToList())
        {
            window.EditorArea.Close(open);
        }
        Ui.Settle();

        // Asked as the history's tab asks it, with no diff borrowing a file the revert writes.
        string src = Path.Combine(folder, "src");
        Ui.WaitFor(() => !window.LocalHistory!.IsRecording, "the saves kept");
        HistoryRow first = HistoryRows.OfFolder(window.LocalHistory!.Snapshot().Events, src).Single(r => r.Title.English == "Modify A.cs");

        List<string> onWindowThread = [];
        void Writing(string path)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess() && PathNames.IsUnder(PathNames.Canonical(path), folder))
            {
                lock (onWindowThread)
                {
                    onWindowThread.Add(Path.GetFileName(path));
                }
            }
        }
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FileText.Writing += Writing;
        try
        {
            Task held = window.EditorArea.Commands.HoldNextWrite(release.Task);
            Task<string?> reverting = window.RevertLocalHistoryAsync(src, first.Set);
            Ui.PumpUntil(() => held.IsCompleted || reverting.IsCompleted, "the revert's writes held in their turn, or the revert ended");
            Assert.True(held.IsCompleted, $"The revert wrote its closed files without waiting its turn with the other writes of closed files: {window.Status}");
            Assert.True(File.ReadAllText(a) == "sealed " + A && File.ReadAllText(b) == "sealed " + B, $"The revert wrote a closed file before its turn: A.cs '{File.ReadAllText(a)}', B.cs '{File.ReadAllText(b)}', open [{string.Join(", ", window.EditorArea.Documents.Select(d => d.Name))}]");
            release.SetResult();
            Assert.Null(Ui.AwaitPumped(reverting, "the revert"));
        }
        finally
        {
            release.TrySetResult();
            FileText.Writing -= Writing;
        }
        Assert.True(onWindowThread.Count == 0, $"The revert wrote {string.Join(", ", onWindowThread)} on the window's thread");
        Assert.True(File.ReadAllText(a) == A && File.ReadAllText(b) == B, $"The revert did not put both files back: {window.Status}");

        List<string> asked = [];
        window.Confirm = (_, question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        Ui.ClickRow(window, Show(window, a));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.ReadAllText(a) == "sealed " + A && File.ReadAllText(b) == "sealed " + B,
            () => $"one Undo to take back the revert of both files: A.cs '{File.ReadAllText(a)}', B.cs '{File.ReadAllText(b)}', {window.Status}");
        Assert.True(asked.Count == 1 && asked[0].Contains("A.cs", StringComparison.Ordinal) && asked[0].Contains("B.cs", StringComparison.Ordinal),
            $"Undo of the revert asked: {string.Join(" | ", asked)}");
        window.Close();
    }

    /// <summary>A folder's revert to a set before one of its files was made moves that file to the Trash in the revert's
    /// command: one Undo, asked once, writes the other file back and brings the one made back out of the Trash.</summary>
    private void A_folders_revert_to_before_a_file_was_made_moves_it_to_the_Trash_and_one_undo_brings_it_back()
    {
        const string A = "class A\n{\n}\n";
        const string B = "class B\n{\n}\n";
        string a = Write("src/A.cs", A);
        string src = Path.Combine(folder, "src");
        string b = Path.Combine(src, "B.cs");
        MainWindow window = Open("src/A.cs");
        Save(window, 0, 0, "sealed ");
        Assert.Null(window.CreateFile(src, "B.cs", B));
        foreach (Passenger.App.Workspace.OpenDocument open in window.EditorArea.Documents.ToList())
        {
            window.EditorArea.Close(open);
        }
        Ui.Settle();
        Ui.WaitFor(() => !window.LocalHistory!.IsRecording, "the save and the new file kept");
        HistoryRow first = HistoryRows.OfFolder(window.LocalHistory!.Snapshot().Events, src).Single(r => r.Title.English == "Modify A.cs");

        Assert.Null(Ui.AwaitPumped(window.RevertLocalHistoryAsync(src, first.Set), "the revert"));
        Assert.True(File.ReadAllText(a) == A && !File.Exists(b), $"The revert did not put A.cs back and take B.cs away: {window.Status}");
        Assert.True(File.ReadAllText(Path.Combine(trash, "B.cs")) == B, "The revert did not move B.cs to the Trash");

        List<string> asked = [];
        window.Confirm = (_, question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        Ui.ClickRow(window, Show(window, a));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.Exists(b) && File.ReadAllText(a) == "sealed " + A,
            () => $"one Undo to take back the revert: A.cs '{File.ReadAllText(a)}', B.cs {(File.Exists(b) ? "back" : "gone")}, {window.Status}");
        Assert.Equal(B, File.ReadAllText(b));
        Assert.True(asked.Count == 1 && asked[0].Contains("brings back B.cs", StringComparison.Ordinal), $"Undo of the revert asked: {string.Join(" | ", asked)}");
        Assert.Empty(Directory.EnumerateFileSystemEntries(trash));
        window.Close();
    }

    /// <summary>A change kept twice in the log two Passengers on the same state append to, as an external change by the one
    /// that heard it and under its own name by the one that made it, in either order, lists once under its maker's name.</summary>
    private void A_change_kept_by_its_maker_and_as_heard_by_another_passenger_on_the_same_state_lists_once_under_its_makers_name()
    {
        string state = TestFolders.Create("history-state-");
        Write("Shop.cs", Saved1);
        Write("Notes.txt", "notes kept\n");
        string shop = Hash(Original);
        string shopNow = Hash(Saved1);
        string notes = Hash("notes\n");
        string notesNow = Hash("notes kept\n");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string root = Path.Combine(Passenger.App.State.ProjectState.FolderOf(state, folder), "localHistory");
        Directory.CreateDirectory(root);
        // Shop.cs's change heard by one before its maker kept it; Notes.txt's kept by its maker before another heard it.
        File.WriteAllText(Path.Combine(root, "changes.log"), string.Concat(
            $"{{\"s\":1,\"t\":{now - 6000},\"k\":\"seen\",\"p\":\"Shop.cs\",\"a\":\"{shop}\"}}\n",
            $"{{\"s\":2,\"t\":{now - 5000},\"k\":\"changed\",\"p\":\"Shop.cs\",\"b\":\"{shop}\",\"a\":\"{shopNow}\",\"n\":\"{LocalHistory.ExternalChange}\"}}\n",
            $"{{\"s\":3,\"t\":{now - 4990},\"k\":\"changed\",\"p\":\"Shop.cs\",\"b\":\"{shop}\",\"a\":\"{shopNow}\",\"n\":\"Change Signature\"}}\n",
            $"{{\"s\":4,\"t\":{now - 4000},\"k\":\"seen\",\"p\":\"Notes.txt\",\"a\":\"{notes}\"}}\n",
            $"{{\"s\":5,\"t\":{now - 3000},\"k\":\"changed\",\"p\":\"Notes.txt\",\"b\":\"{notes}\",\"a\":\"{notesNow}\"}}\n",
            $"{{\"s\":6,\"t\":{now - 2990},\"k\":\"changed\",\"p\":\"Notes.txt\",\"b\":\"{notes}\",\"a\":\"{notesNow}\",\"n\":\"{LocalHistory.ExternalChange}\"}}\n"));
        MainWindow window = Open("Shop.cs", state, new HistoryKeeping());

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count > 0, "Shop.cs's history");
        Assert.Equal(["Change Signature"], history.Rows.Select(r => r.Title.English));
        HistoryTab both = window.ShowLocalHistory(folder)!;
        Ui.Landed(() => both.Rows.Count > 0, "the folder's history");
        Assert.Equal(["Modify Notes.txt", "Change Signature"], both.Rows.Select(r => r.Title.English));
        window.Close();

        static string Hash(string text) => LocalHistory.Hash(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>On macOS and Linux a backslash is a letter of a file's name, so its history is kept under that name and a
    /// later Passenger on the same state lists it, where a slash in its place named another file.</summary>
    [AvaloniaFact]
    public void A_files_history_kept_under_a_name_holding_a_backslash_is_listed_by_a_later_passenger_on_the_same_state()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A backslash parts a path's names on Windows, so no file's name there holds one");
        string state = TestFolders.Create("history-state-");
        Write(@"Shop\Copy.cs", Original);
        MainWindow first = Open(@"Shop\Copy.cs", state, new HistoryKeeping());
        Save(first, 2, 4, "long ");
        first.Close();
        Ui.Settle();

        MainWindow later = Open(@"Shop\Copy.cs", state, new HistoryKeeping());
        HistoryTab history = ShowFromFileMenu(later, "Show History…");
        Ui.Settle();
        Assert.True(history.Rows.Any(r => r.Title.English == "Change"),
            $"A later Passenger on the same state listed none of the save kept for Shop\\Copy.cs, only [{string.Join(", ", history.Rows.Select(r => r.Title.English))}]: its history was kept under Shop/Copy.cs, another file's name");
        later.Close();
    }

    /// <summary>A label put while another Passenger on the same state holds the history, writing its log anew as a prune
    /// does and for longer than a change waits to say so, is in the log once that Passenger lets go: written after the
    /// rewrite, never beside it into the log the rewrite then replaces.</summary>
    private void A_label_put_while_another_passenger_on_the_same_state_writes_the_log_anew_is_kept_after_its_rewrite()
    {
        string state = TestFolders.Create("history-state-");
        Write("Shop.cs", Original);
        string root = Path.Combine(Passenger.App.State.ProjectState.FolderOf(state, folder), "localHistory");
        Directory.CreateDirectory(root);
        string log = Path.Combine(root, "changes.log");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(log, string.Concat(
            $"{{\"s\":1,\"t\":{now - 2000},\"k\":\"label\",\"n\":\"Dropped by the other\"}}\n",
            $"{{\"s\":2,\"t\":{now - 1000},\"k\":\"label\",\"n\":\"Kept by the other\"}}\n"));
        MainWindow window = Open("Shop.cs", state, new HistoryKeeping());
        Assert.Equal(PathNames.Canonical(root), PathNames.Canonical(window.LocalHistory!.Root));

        // The other Passenger's prune: the history held (its lock beside the log), the log read, and written anew without
        // its oldest line once longer than a change waits for the history has passed, or once the log grew meanwhile.
        using ManualResetEventSlim held = new();
        Task<bool> other = Waiting.Apart(() =>
        {
            using FileStream holding = new(Path.Combine(root, "changes.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string[] read = File.ReadAllLines(log);
            long length = new FileInfo(log).Length;
            held.Set();
            bool grew = Waiting.Until(() => new FileInfo(log).Length != length, TimeSpan.FromSeconds(15), scaled: false);
            AtomicFile.Write(log, Encoding.UTF8.GetBytes(string.Concat(read.Skip(1).Select(line => line + "\n"))));
            return grew;
        });
        Ui.PumpUntil(() => held.IsSet, "the other Passenger to hold the history");
        Ui.ChooseFromEditorMenu(window, "Local History", "Put Label…");
        window.KeyTextInput("Mine");
        Ui.Press(window, "ENTER", settle: false);
        bool written = Ui.AwaitPumped(other, "the other Passenger to write the log anew and let go");
        Ui.Settle();

        string[] labels = [.. File.ReadAllLines(log).Select(line => System.Text.Json.Nodes.JsonNode.Parse(line)?["n"]?.GetValue<string>()).OfType<string>()];
        Assert.False(written, "The label was written into the log while another Passenger held the history to write it anew");
        Assert.True(labels.Contains("Mine"), $"The label put while another Passenger on the same state wrote the log anew was lost to its rewrite: the log holds [{string.Join(", ", labels)}]");
        Assert.Equal(["Kept by the other", "Mine"], labels);
        window.Close();
    }

    /// <summary>A revert follows only what it changes: a file's text put back leaves every Explorer row as it was, with Go to
    /// File's folder never walked again and the project not checked again, and a file deleted brought back puts in its own
    /// row alone.</summary>
    private void A_revert_of_a_text_makes_no_row_again_and_one_bringing_back_a_file_only_its_own()
    {
        Write("Shop.csproj", TestProjects.Library());
        string a = Write("src/A.cs", "class A\n{\n}\n");
        string b = Write("src/B.cs", "class B\n{\n}\n");
        Write("Notes.txt", "notes\n");
        MainWindow window = Open("src/A.cs");
        Save(window, 0, 0, "sealed ");
        Save(window, 0, 0, "public ");
        window.OpenFile(b);
        Save(window, 0, 0, "sealed ");
        Show(window, a);
        Show(window, Path.Combine(folder, "Notes.txt"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ChooseFromRowMenu(window, Show(window, b), "Edit", "Delete…");
        Ui.WaitFor(() => !File.Exists(b), "B.cs deleted");

        Ui.ChooseFromRowMenu(window, Show(window, Path.Combine(folder, "src")), "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count > 0 && history.Rows[0].Title.English == "Delete B.cs", "B.cs's deletion");
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == "sealed class B\n{\n}\n", "B.cs as it was deleted");
        FilesFollowed.Check(window, "Revert of B.cs's deletion", [b], () =>
        {
            Task reverting = history.RevertAsync();
            Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        });
        Assert.Equal("sealed class B\n{\n}\n", File.ReadAllText(b));
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Path == PathNames.Canonical(b));

        window.OpenFile(a);
        Ui.Settle();
        history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 2, "A.cs's two saves");
        // The first save's version: A.cs as it was before it.
        history.Select(history.Rows[1]);
        Ui.Settle();
        FilesFollowed.Check(window, "Revert of A.cs", [a], () =>
        {
            Task reverting = history.RevertAsync();
            Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        }, textsAlone: true);
        Assert.Equal("class A\n{\n}\n", File.ReadAllText(a));
        window.Close();
    }

    private void A_files_history_follows_it_through_a_rename_in_the_solution_view()
    {
        Write("Shop/Shop.csproj", TestProjects.Library());
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop/Shop.csproj\" />\n</Solution>\n");
        string order = Write("Shop/Order.cs", Original);
        MainWindow window = Open("Shop/Order.cs");
        Save(window, 2, 4, "long ");

        Ui.ChooseFromRowMenu(window, Show(window, order), "Edit", "Rename…");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Purchase.cs");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, $"The new name was refused: {popup.Problem}");
        string purchase = Path.Combine(folder, "Shop", "Purchase.cs");
        Assert.True(File.Exists(purchase));
        Save(window, 6, 8, "count++; ");

        Ui.ChooseFromRowMenu(window, Show(window, purchase), "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count == 3, "the saves under both names and the rename between them");
        Assert.Equal(["Change", "Rename Order.cs to Purchase.cs", "Change"], history.Rows.Select(r => r.Title.English));
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as it was under its old name");
        window.Close();
    }

    /// <summary>A save made while a replace writes its closed files is the person's own change, never filed under the
    /// replace's name, which names only the replace's own writes.</summary>
    private void A_save_made_while_a_replace_writes_its_closed_files_is_not_filed_under_the_replaces_name()
    {
        string notes = Write("Notes.txt", "a tally of things\n");
        Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            SearchQuery query = new("tally", WholeWords: true);
            Passenger.App.Workspace.FilesFound found = Ui.Await(window.FindInFilesAsync(query,
                new Passenger.App.Workspace.SearchScope(Passenger.App.Workspace.SearchScopeKind.Directory, null, folder, true, null, null), CancellationToken.None), "the search");
            Task held = window.EditorArea.Commands.HoldNextWrite(release.Task);
            Task<Passenger.App.Workspace.FilesReplaced> replacing = window.ReplaceInFilesAsync(query, "count", found);
            Ui.PumpUntil(() => held.IsCompleted, "the replace's write of Notes.txt to be held");
            window.EditorArea.CodeEditor.Focus();
            window.KeyTextInput("// saved meanwhile ");
            Ui.Run(window, AppActions.Save, settle: false);
            Ui.PumpUntil(() => window.EditorArea.Active?.IsModified == false, "Shop.cs saved");
            release.SetResult();
            Ui.AwaitPumped(replacing, "the replace");
            Assert.Equal("a count of things\n", File.ReadAllText(notes));

            HistoryTab saved = ShowFromFileMenu(window, "Show History…");
            Ui.Landed(() => saved.Rows.Count == 1, "the save of Shop.cs");
            Assert.True(saved.Rows[0].Title.English == "Change", $"The save made while the replace wrote is filed as '{saved.Rows[0].Title.English}', not as the person's own change");
            Ui.ChooseFromRowMenu(window, Show(window, notes), "Local History", "Show History…");
            HistoryTab replaced = window.LocalHistoryWindow.Shown!;
            Ui.Landed(() => replaced.Rows.Count > 0, "the replace's write of Notes.txt");
            Assert.Equal(Passenger.App.Workspace.FilesReplaced.Title(query, "count"), replaced.Rows[0].Title.English);
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    /// <summary>A language server's edit that makes a file over one there (a create with overwrite, then its text), as a
    /// refactoring's answer is applied: the file is written as a save writes it, so the history keeps what it held, which
    /// it never saw open, and Undo puts that back.</summary>
    private void A_file_a_servers_edit_makes_over_one_there_keeps_what_it_held_in_the_history_and_undo_puts_it_back()
    {
        Write("Other.cs", "class Other\n{\n}\n");
        string notes = Write("Notes.cs", Original);
        MainWindow window = Open("Other.cs");
        const string Made = "class Notes\n{\n}\n";
        string uri = Passenger.Lsp.DocumentUris.FromPath(notes);
        System.Text.Json.JsonElement edit = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            documentChanges = new object[]
            {
                new { kind = "create", uri, options = new { overwrite = true } },
                new { textDocument = new { uri, version = (int?)null }, edits = new[] { new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = Made } } },
            },
        });
        MoveToFile(window, edit);
        Assert.Equal(Made, File.ReadAllText(notes));

        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ClickRow(window, Show(window, notes));
        Ui.Run(window, EditorActions.Undo);
        Assert.True(File.ReadAllText(notes) == Original, $"Undo left the file made anew as '{File.ReadAllText(notes)}', not as it was: {window.Status}");

        Ui.ChooseFromRowMenu(window, Show(window, notes), "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count > 1, "the file's versions");
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as it was before the edit made it anew");

        new FileInfo(notes).IsReadOnly = true;
        try
        {
            Passenger.App.CSharp.WorkspaceEditFailed refused = Assert.Throws<Passenger.App.CSharp.WorkspaceEditFailed>(
                () => MoveToFile(window, edit));
            Assert.Equal("Notes.cs is read-only", refused.Message);
            Assert.Equal(Original, File.ReadAllText(notes));
        }
        finally
        {
            new FileInfo(notes).IsReadOnly = false;
        }
        window.Close();
    }

    /// <summary>A language server's rename of one file over another that is open with edits not saved: the file written over
    /// is saved first, so its edits are kept, and Undo moves the renamed file back and writes the one written over back as
    /// it was, edits and all; Redo makes the rename again.</summary>
    private void A_servers_rename_over_an_open_file_with_edits_keeps_them_and_undo_writes_that_file_back()
    {
        const string Renamed = "class A\n{\n}\n";
        string a = Write("A.cs", Renamed);
        string b = Write("B.cs", "class B\n{\n}\n");
        MainWindow window = Open("B.cs");
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("sealed ");
        Ui.Settle();
        string edited = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        Assert.Equal("sealed class B\n{\n}\n", edited);
        System.Text.Json.JsonElement edit = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            documentChanges = new object[]
            {
                new { kind = "rename", oldUri = Passenger.Lsp.DocumentUris.FromPath(a), newUri = Passenger.Lsp.DocumentUris.FromPath(b), options = new { overwrite = true } },
            },
        });
        MoveToFile(window, edit);
        Assert.True(!File.Exists(a) && File.ReadAllText(b) == Renamed, $"The rename left A.cs {(File.Exists(a) ? "there" : "gone")} and B.cs reading '{File.ReadAllText(b)}'");

        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ClickRow(window, Show(window, b));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => window.Status?.StartsWith("Undone", StringComparison.Ordinal) == true || window.Status?.StartsWith("Cannot", StringComparison.Ordinal) == true,
            () => $"the Undo's end; the status says '{window.Status}'");
        Assert.True(File.Exists(a) && File.ReadAllText(a) == Renamed, $"Undo left A.cs {(File.Exists(a) ? $"reading '{File.ReadAllText(a)}'" : "gone")}: {window.Status}");
        Assert.True(File.Exists(b) && File.ReadAllText(b) == edited,
            $"Undo left B.cs, written over by the rename, {(File.Exists(b) ? $"reading '{File.ReadAllText(b)}'" : "gone")}, not with its edits: {window.Status}");

        Ui.ClickRow(window, Show(window, b));
        Ui.Run(window, EditorActions.Redo);
        Ui.WaitFor(() => window.Status?.StartsWith("Redone", StringComparison.Ordinal) == true || window.Status?.StartsWith("Cannot", StringComparison.Ordinal) == true,
            () => $"the Redo's end; the status says '{window.Status}'");
        Assert.True(!File.Exists(a) && File.ReadAllText(b) == Renamed, $"Redo left A.cs {(File.Exists(a) ? "there" : "gone")} and B.cs reading '{File.ReadAllText(b)}': {window.Status}");
        window.Close();
    }

    /// <summary>A server's edit applied as Move to File applies its own: one command of Undo, its files written.</summary>
    private static void MoveToFile(MainWindow window, System.Text.Json.JsonElement edit)
    {
        using Passenger.App.Editors.CommandHistory.Recording recording = window.EditorArea.Commands.Record("Move to File");
        Passenger.App.CSharp.WorkspaceEdits.ApplyAsCommand(window.EditorArea, edit, recording);
        Ui.Await(Passenger.App.CSharp.WorkspaceEdits.LandedAsync(recording, null), "the move's writes");
    }

    private void The_store_keeps_days_of_activity_as_Riders_does_and_no_more_content_than_its_limit()
    {
        // Twelve saves six hours apart are three days: a store keeping one day keeps the last four.
        string kept = TestFolders.Create("history-state-");
        Write("Shop.cs", Original);
        Set(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        HistoryKeeping day = new(Days: 1) { Clock = Now };
        MainWindow window = Open("Shop.cs", kept, day);
        for (int save = 1; save <= 12; save++)
        {
            Set(Now() + TimeSpan.FromHours(6));
            SaveEdit(window, new TextEdit(0, 0, $"// {save}\n"));
        }
        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count is > 0 and < 12, "the oldest saves pruned as the days turned");
        window.Close();

        MainWindow again = Open("Shop.cs", kept, day);
        history = ShowFromFileMenu(again, "Show History…");
        Ui.Landed(() => history.Rows.Count == 4, "the last day's four saves");
        history.Select(history.Rows[^1]);
        string eighth = string.Concat(Enumerable.Range(1, 8).Reverse().Select(n => $"// {n}\n")) + Original;
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == eighth, "the oldest kept version, the file after the eighth save");
        Assert.Equal(4, File.ReadAllLines(Path.Combine(again.LocalHistory!.Root, "changes.log")).Length);
        again.Close();

        string small = TestFolders.Create("history-state-");
        HistoryKeeping bytes = new(Days: 5, Bytes: 16_000) { Clock = Now };
        MainWindow third = Open("Shop.cs", small, bytes);
        Random random = new(9);
        for (int save = 1; save <= 8; save++)
        {
            // Each a day apart, so the store prunes as each day turns.
            Set(Now() + TimeSpan.FromHours(25));
            string noise = Convert.ToHexString(random.GetItems<byte>(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 3000));
            SaveEdit(third, new TextEdit(0, third.EditorArea.CodeEditor.Document.Snapshot.Length, $"// {save}\n{noise}\n"));
        }
        history = ShowFromFileMenu(third, "Show History…");
        Ui.Landed(() => history.Rows.Count is >= 1 and < 8, "the oldest versions pruned past the store's limit");
        Assert.StartsWith("// 7\n", Version(history, 0), StringComparison.Ordinal);
        third.Close();
    }

    private DateTimeOffset Now() => new(Interlocked.Read(ref clock), TimeSpan.Zero);

    private void Set(DateTimeOffset now) => Interlocked.Exchange(ref clock, now.UtcTicks);

    private static string Version(HistoryTab history, int row)
    {
        history.Select(history.Rows[row]);
        Ui.Settle();
        return history.Diff!.Left.Document.Snapshot.ToString();
    }

    /// <summary>Three saves of three edits, each to a line of its own.</summary>
    private static void SaveThree(MainWindow window)
    {
        Save(window, 2, 4, "long ");
        Save(window, 6, 8, "count++; ");
        Save(window, 9, 4, "public ");
        Assert.Equal(Saved3, Text(window));
    }

    /// <summary>Types at a place in the file shown and saves it with Save's keys.</summary>
    private static void Save(MainWindow window, int line, int column, string text)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.GoTo(new TextPosition(line, column));
        window.KeyTextInput(text);
        Ui.Settle();
        Ui.Run(window, AppActions.Save);
        Assert.False(window.EditorArea.Active!.IsModified, "The save did not write the file");
    }

    /// <summary>An edit made in the file shown, as a paste would make it, then saved with Save's keys.</summary>
    private static void SaveEdit(MainWindow window, TextEdit edit)
    {
        window.EditorArea.CodeEditor.Focus();
        window.EditorArea.CodeEditor.ApplyEdits([edit]);
        Ui.Settle();
        Ui.Run(window, AppActions.Save);
        Assert.False(window.EditorArea.Active!.IsModified, "The save did not write the file");
    }

    /// <summary>A window on the folder with a file open and the keys in it; with no C# language server, which Local History
    /// asks nothing of, so a folder holding a project starts and restores none.</summary>
    private MainWindow Open(string file, string? state = null, HistoryKeeping? keeping = null)
    {
        MainWindow window = state is null ? new() { Width = 1200, Height = 800, StartsLanguageServer = false }
            : new() { Width = 1200, Height = 800, StateFolder = state, HistoryKeeping = keeping!, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, file));
        window.EditorArea.CodeEditor.Focus();
        Ui.Settle();
        return window;
    }

    /// <summary>File, Local History, and one of its items, as the menu bar runs it.</summary>
    private static HistoryTab ShowFromFileMenu(MainWindow window, string item)
    {
        NativeMenu menu = Ui.Submenu(Ui.Submenu(window.MenuBar, "File"), "Local History");
        NativeMenuItem chosen = menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == item)
            ?? throw new InvalidOperationException($"File, Local History has no {item}: [{string.Join(", ", menu.Items.OfType<NativeMenuItem>().Select(i => i.Header))}]");
        Assert.True(chosen.IsEnabled, $"File, Local History, {item} is disabled");
        chosen.Command!.Execute(null);
        Ui.Settle();
        return window.LocalHistoryWindow.Shown ?? throw new InvalidOperationException($"{item} showed no history: {window.Status}");
    }

    /// <summary>Opens the Explorer's folders down to a file or folder, as a person does to get to it, and gives its row.</summary>
    private static ExplorerNode Show(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(PathNames.Canonical(path));
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == PathNames.Canonical(path))
            ?? throw new InvalidOperationException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    private static string Text(MainWindow window) => window.EditorArea.Active!.Text.Snapshot.ToString();

    private string Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
