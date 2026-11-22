using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Find in Files: every text file of the folder but build output, an open file's unsaved text included,
/// narrowed by Match case, Words and Regex; choosing a match goes to it and selects it; Replace All changes an open
/// file as an edit that undoes, and writes a closed one.</summary>
public sealed class FindInFilesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("find-");

    public FindInFilesTests()
    {
        File.WriteAllText(Path.Combine(folder, "Alpha.cs"), "class Alpha\n{\n    int count;\n}\n");
        File.WriteAllText(Path.Combine(folder, "Beta.cs"), "// the count of things\nclass Beta\n{\n    void Count() { }\n}\n");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "Built.cs"), "int count;\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Find in Files, the gate's checks as one flow: its options narrow the matches, an open file's unsaved
    /// text searched; a match chosen goes to it; Replace All changes open and closed files, leaving a read-only file, a
    /// regex that never ends and a text grown too long, saying why, and keeping an open file's caret, folds and
    /// breakpoints; a regex's groups and each line's start replaced as written; and the Explorer dims what the search
    /// passes over.</summary>
    [AvaloniaFact]
    public void Find_in_files_finds_and_replaces_across_the_folder_as_asked()
    {
        Flow.StepApart<FindInFilesTests>("the options narrow the matches and an open file's unsaved text is searched", t => t.The_options_narrow_the_matches_and_an_open_files_unsaved_text_is_searched());
        Flow.StepApart<FindInFilesTests>("choosing a match goes to it and Replace All changes open and closed files", t => t.Choosing_a_match_goes_to_it_and_replace_all_changes_open_and_closed_files());
        Flow.StepApart<FindInFilesTests>("Replace All across a read only file replaces in the rest and says which file it left and why", t => t.Replace_all_across_a_read_only_file_replaces_in_the_rest_and_says_which_file_it_left_and_why());
        Flow.StepApart<FindInFilesTests>("a regex backtracking without end in a file is said to stop there to the popup and an agent and Replace All leaves that file", t => t.A_regex_backtracking_without_end_in_a_file_is_said_to_stop_there_to_the_popup_and_an_agent_and_replace_all_leaves_that_file());
        Flow.StepApart<FindInFilesTests>("Replace All that would make a file's text too long leaves it and says so", t => t.Replace_all_that_would_make_a_files_text_too_long_leaves_it_and_says_so());
        Flow.StepApart<FindInFilesTests>("Replace All over an open file keeps its caret folds and breakpoints", t => t.Replace_all_over_an_open_file_keeps_its_caret_folds_and_breakpoints());
        Flow.StepApart<FindInFilesTests>("Replace in Files fills a regex's groups from each match where it stands and words take an alternation whole", t => t.Replace_in_files_fills_a_regexs_groups_from_each_match_where_it_stands_and_words_take_an_alternation_whole());
        Flow.StepApart<FindInFilesTests>("replacing every line's start in files comments each line and leaves each file's end alone", t => t.Replacing_every_lines_start_in_files_comments_each_line_and_leaves_each_files_end_alone());
        Flow.StepApart<FindInFilesTests>("the explorer dims what Find in Files and Go to File pass over and a monorepo's packages of source are searched and shown as code", t => t.The_explorer_dims_what_find_in_files_and_go_to_file_pass_over_and_a_monorepos_packages_of_source_are_searched_and_shown_as_code());
    }

    /// <summary>Recent Files, bookmarks and breakpoints, the gate's checks as one flow: Recent Files offers the files
    /// behind the one in front and opens one where it was left, bookmarks toggle, move with their lines and Next goes
    /// round the files, a file left open as a folder opens is carried by nothing of the session left, and keys typed
    /// above two thousand breakpoints carry them without costing each.</summary>
    [AvaloniaFact]
    public void Recent_files_bookmarks_and_breakpoints_follow_the_files_and_hold_nothing_once_let_go()
    {
        Flow.StepApart<FindInFilesTests>("recent files offers the files behind the one in front and opens one where it was left", t => t.Recent_files_offers_the_files_behind_the_one_in_front_and_opens_one_where_it_was_left());
        Flow.StepApart<FindInFilesTests>("bookmarks toggle move with their lines and next goes round the files", t => t.Bookmarks_toggle_move_with_their_lines_and_next_goes_round_the_files());
        Flow.StepApart<FindInFilesTests>("a file left open as a folder opens is carried by nothing of the session left", t => t.A_file_left_open_as_a_folder_opens_is_carried_by_nothing_of_the_session_left());
        Flow.StepApart<FindInFilesTests>("keys typed above two thousand breakpoints carry them on the UI thread without costing each", t => t.Keys_typed_above_two_thousand_breakpoints_carry_them_on_the_UI_thread_without_costing_each());
    }

    /// <summary>Replace in Files writes each file as its bytes hold (8-bit, UTF-16, binary passed over) and replaces
    /// each match, past the list's thousand too or none once stopped, as one step of Undo and one change set, an
    /// agent's too; an Undo a closed file refused names it and leaves both files as the replace left them, a file a
    /// link leads outside the folder is left, and large closed files cost the window's thread what small ones
    /// do.</summary>
    [AvaloniaFact]
    public void Replace_in_files_writes_each_file_as_its_bytes_hold_and_is_one_undo_that_never_loses_a_change()
    {
        Flow.StepApart<FindInFilesTests>("a line with three matches is one row with each bold and Replace All still replaces each", t => t.A_line_with_three_matches_is_one_row_with_each_bold_and_replace_all_still_replaces_each());
        Flow.StepApart<FindInFilesTests>("Replace All with a character a closed 8 bit file cannot hold writes it as UTF-8 not a question mark", t => t.Replace_all_with_a_character_a_closed_8_bit_file_cannot_hold_writes_it_as_UTF_8_not_a_question_mark());
        Flow.StepApart<FindInFilesTests>("a UTF 16 file is found and replaced in as the editor reads it and a binary file is still passed over", t => t.A_UTF_16_file_is_found_and_replaced_in_as_the_editor_reads_it_and_a_binary_file_is_still_passed_over());
        Flow.StepApart<FindInFilesTests>("Replace All past the list's thousand matches replaces every match in the scope and says how many", t => t.Replace_all_past_the_lists_thousand_matches_replaces_every_match_in_the_scope_and_says_how_many());
        Flow.StepApart<FindInFilesTests>("Replace All past the list's thousand matches is stopped by Stop while it finds them replacing nothing", t => t.Replace_all_past_the_lists_thousand_matches_is_stopped_by_Stop_while_it_finds_them_replacing_nothing());
        Flow.StepApart<FindInFilesTests>("Replace All across open and closed files is one undo and one change set named for it", t => t.Replace_all_across_open_and_closed_files_is_one_undo_and_one_change_set_named_for_it());
        Flow.StepApart<FindInFilesTests>("an agent's Replace in Files is one undo across the files it changed", t => t.An_agents_replace_in_files_is_one_undo_across_the_files_it_changed());
        Flow.StepApart<FindInFilesTests>("undo of a replace is refused naming a closed file changed while its question was open", t => t.Undo_of_a_replace_is_refused_naming_a_closed_file_changed_while_its_question_was_open());
        Flow.StepApart<FindInFilesTests>("undo of a replace refused by its closed file leaves both files as the replace left them with undo offered", t => t.Undo_of_a_replace_refused_by_its_closed_file_leaves_both_files_as_the_replace_left_them_with_undo_offered());
        Flow.StepApart<FindInFilesTests>("Replace All and Replace in Files leave a file outside the folder that a link in it leads to", t => t.Replace_all_and_replace_in_files_leave_a_file_outside_the_folder_that_a_link_in_it_leads_to());
        Flow.StepApart<FindInFilesTests>("a replace across large closed files costs the window thread what one across small ones does", t => t.A_replace_across_large_closed_files_costs_the_window_thread_what_one_across_small_ones_does());
    }

    /// <summary>Find in Files searches and replaces only in the scope chosen: a directory scope inside node_modules or
    /// bin is searched though the folder's search passes over it, and the popup closed is let go with its preview and
    /// the file it showed.</summary>
    [AvaloniaFact]
    public void Find_in_files_searches_and_replaces_only_in_the_scope_chosen_and_lets_go_once_closed()
    {
        Flow.StepApart<FindInFilesTests>("a directory scope inside node_modules or bin is searched and replaced in though the folder's search passes over it", t => t.A_directory_scope_inside_node_modules_or_bin_is_searched_and_replaced_in_though_the_folders_search_passes_over_it());
        Flow.StepApart<FindInFilesTests>("Find in Files closed is let go with its preview and the file it showed", t => t.Find_in_files_closed_is_let_go_with_its_preview_and_the_file_it_showed());
    }

    [Gate]
    [AvaloniaFact]
    public void The_options_narrow_the_matches_and_an_open_files_unsaved_text_is_searched()
    {
        MainWindow window = Open();
        // Unsaved: Beta.cs open with another count typed in.
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// one more count\n")]);
        FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
        Ui.Search(popup, "count");
        Assert.Equal(4, popup.Matches.Count);
        Assert.DoesNotContain(popup.Matches, m => m.Path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.Contains(popup.Matches, m => m.LineText == "// one more count");
        Assert.Equal("4 matches in 2 files", popup.Status);

        popup.MatchCase = true;
        Ui.Search(popup, "Count");
        Assert.Equal(["    void Count() { }"], popup.Matches.Select(m => m.LineText));
        popup.MatchCase = false;
        popup.Regex = true;
        Ui.Search(popup, @"c\w+t\(");
        Assert.Single(popup.Matches);
        Ui.Search(popup, "count(");
        Assert.Empty(popup.Matches);
        string? why = FileSearch.Problem(popup.Query);
        Assert.True(why is { Length: > 0 }, "A regex that does not read is not said to be wrong");
        Assert.Equal(why, popup.Status);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Choosing_a_match_goes_to_it_and_replace_all_changes_open_and_closed_files()
    {
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Words = true;
        Ui.Search(popup, "count");
        Assert.Equal(3, popup.Matches.Count);

        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "total";
        popup.RequestReplace();
        Ui.WaitFor(() => File.ReadAllText(Path.Combine(folder, "Alpha.cs")).Contains("int total;", StringComparison.Ordinal), "Alpha.cs to be written");
        OpenDocument beta = window.EditorArea.Active!;
        Assert.Contains("void total()", beta.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("// the count of things", File.ReadAllText(Path.Combine(folder, "Beta.cs")), StringComparison.Ordinal);
        Assert.True(beta.IsModified);
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.TryExecute(Passenger.Editor.EditorActions.Undo);
        Assert.Contains("// the count of things", beta.Text.Snapshot.ToString(), StringComparison.Ordinal);

        popup = window.ShowFindInFiles(replace: false)!;
        Ui.Search(popup, "int total");
        Assert.Single(popup.Matches);
        Ui.Press(window, "ENTER");
        Assert.Equal("Alpha.cs", window.EditorArea.Active?.Name);
        Selection chosen = window.EditorArea.CodeEditor.Selections.Primary;
        Assert.Equal("int total", window.EditorArea.CodeEditor.Document.Snapshot.GetText(chosen.Start, chosen.Length));
        window.Close();
    }

    /// <summary>A line with three matches is one row, each of them bold, the count still counting matches; the keys move
    /// row by row, the preview marking every match on the row's line, Enter going to its first; Replace All replaces each.</summary>
    private void A_line_with_three_matches_is_one_row_with_each_bold_and_replace_all_still_replaces_each()
    {
        string gamma = Path.Combine(folder, "Gamma.cs");
        File.WriteAllText(gamma, "class Gamma\n{\n    int tally = tally + tally;\n    int other = tally;\n}\n");
        MainWindow window = Open();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        Ui.Search(popup, "tally");
        Ui.Settle();
        Assert.Equal("4 matches in 1 file", popup.Status);
        Assert.True(popup.Lines.Select(l => (l.Line, l.Matches.Count)).SequenceEqual([(2, 3), (3, 1)]),
            $"The two lines with matches were listed as [{string.Join(", ", popup.Lines.Select(l => $"line {l.Line + 1}: {l.Matches.Count}"))}], where a line is a row however many matches it holds");
        ListBox rows = popup.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<SearchLine>);
        Assert.Equal(2, rows.ItemCount);
        TextBlock first = Ui.Row(rows, popup.Lines[0]).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Inlines is { Count: > 0 });
        string[] bold = [.. first.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Where(r => r.FontWeight == FontWeight.Bold).Select(r => r.Text ?? string.Empty)];
        Assert.True(first.Inlines!.Text == "int tally = tally + tally;" && bold.SequenceEqual(["tally", "tally", "tally"]),
            $"The row of three matches reads '{first.Inlines!.Text}' with [{string.Join(", ", bold)}] bold, where each match is");
        CodeEditor preview = popup.GetVisualDescendants().OfType<CodeEditor>().Single();
        Assert.True(Marked(preview).SequenceEqual([(2, 8), (2, 16), (2, 24)]), $"The preview marks [{string.Join(", ", Marked(preview))}] for the row of three matches");

        Ui.Press(window, "DOWN");
        Assert.True(rows.SelectedIndex == 1 && Marked(preview).SequenceEqual([(3, 16)]),
            $"Down left row {rows.SelectedIndex} chosen and the preview marking [{string.Join(", ", Marked(preview))}], where the next line's one match is");

        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "total";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, "the replacement");
        Assert.Equal("class Gamma\n{\n    int total = total + total;\n    int other = total;\n}\n", File.ReadAllText(gamma));
        Assert.Equal("Replaced 4 matches in 1 file", window.Status);
        Ui.Press(window, "ESCAPE");

        popup = window.ShowFindInFiles(replace: false)!;
        Ui.Search(popup, "total");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.Equal("Gamma.cs", window.EditorArea.Active?.Name);
        Selection chosen = window.EditorArea.CodeEditor.Selections.Primary;
        TextPosition start = window.EditorArea.CodeEditor.Document.Snapshot.GetPosition(chosen.Start);
        Assert.True(start == new TextPosition(2, 8) && chosen.Length == "total".Length,
            $"Enter on the row of three matches chose {chosen.Length} characters from {start}, where its first match is");
        window.Close();

        static IEnumerable<(int Line, int Column)> Marked(CodeEditor preview) =>
            preview.Highlights.Select(h => preview.Document.Snapshot.GetPosition(h.Start)).Select(p => (p.Line, p.Column));
    }

    private void Replace_all_with_a_character_a_closed_8_bit_file_cannot_hold_writes_it_as_UTF_8_not_a_question_mark()
    {
        string path = Path.Combine(folder, "Gamma.cs");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("// Prix: 5 francs, déjà payés\nclass Gamma { }\n"));
        MainWindow window = Open();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        Ui.Search(popup, "francs");
        Assert.Single(popup.Matches);

        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "→";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, "the replacement");
        byte[] saved = File.ReadAllBytes(path);
        Assert.False(Array.IndexOf(saved, (byte)'?') >= 0, "The → Windows-1252 cannot hold was written as a question mark");
        Assert.Equal("// Prix: 5 →, déjà payés\nclass Gamma { }\n", new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(saved));
        Assert.Equal("Replaced 1 match in 1 file; Gamma.cs was saved as UTF-8: windows-1252 cannot hold '→'", window.Status);
        window.Close();
    }

    /// <summary>1,100 matches, ten to a file: the list stops at 1,000, so ten files' matches are past it, for the popup's
    /// Replace All and an agent's replace_in_files alike.</summary>
    private void Replace_all_past_the_lists_thousand_matches_replaces_every_match_in_the_scope_and_says_how_many()
    {
        TestFolders.WriteAll(folder, Enumerable.Range(0, 110).Select(file =>
            ($"Tally{file:000}.txt", string.Concat(Enumerable.Range(0, 10).Select(i => $"tally {i} of {file}\n")))));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.FileIndex.Count >= 112 && !window.FileIndex.IsWalking, "the folder's files indexed");
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        Ui.Search(popup, "tally");
        Assert.Equal(FileSearch.MostMatches, popup.Matches.Count);

        List<string> asked = [];
        window.Confirm = (_, question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        popup.Replacement = "sum";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, () => $"the replacement; the status says '{window.Status}'");
        string[] left = [.. Directory.GetFiles(folder, "Tally*.txt").Where(f => File.ReadAllText(f).Contains("tally", StringComparison.Ordinal)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.True(left.Length == 0, $"Replace All left the matches past the list's 1,000 in {string.Join(", ", left)}");
        string every = $"{Numbers.Said(1100)} matches in 110 files";
        Assert.Equal($"Replaced {every}", window.Status);
        Assert.StartsWith($"Replace {every} with 'sum'?", Assert.Single(asked), StringComparison.Ordinal);

        Task<string> replacing = new Passenger.App.Mcp.PassengerTools(window).ReplaceInFiles("sum", "tally", mask: "*.txt");
        Assert.StartsWith($"Replaced {every}: ", Ui.Await(replacing, "replace_in_files' answer"), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(folder, "Tally*.txt"), f => File.ReadAllText(f).Contains("sum", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>Replace All past the list's 1,000 matches finds every match under the window's work, which Stop ends: held,
    /// the status says it is finding them and Stop is offered; stopped, nothing is asked or replaced and the window is
    /// free, a Replace All asked again replacing every match.</summary>
    private void Replace_all_past_the_lists_thousand_matches_is_stopped_by_Stop_while_it_finds_them_replacing_nothing()
    {
        TestFolders.WriteAll(folder, Enumerable.Range(0, 110).Select(file =>
            ($"Tally{file:000}.txt", string.Concat(Enumerable.Range(0, 10).Select(i => $"tally {i} of {file}\n")))));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Ui.WaitFor(() => window.FileIndex.Count >= 112 && !window.FileIndex.IsWalking, "the folder's files indexed");
            FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
            Ui.Search(popup, "tally");
            Assert.Equal(FileSearch.MostMatches, popup.Matches.Count);
            List<string> asked = [];
            window.Confirm = (_, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };

            Task held = window.HoldNextFindEvery(release.Task);
            popup.Replacement = "sum";
            popup.RequestReplace();
            Ui.PumpUntil(() => held.IsCompleted, "Replace All's find of every match to be held");
            Assert.Equal("Finding every match to replace…", window.Status);
            Assert.True(window.Actions.WhyNot(AppActions.Stop, window.Actions.Context()) is null, "Stop is not offered while Replace All finds every match");
            Ui.Run(window, AppActions.Stop, settle: false);
            Ui.WaitFor(() => window.Status == "Stopped finding every match to replace: nothing was replaced",
                () => $"Replace All's find to end once stopped; the status says '{window.Status}'");
            Assert.True(asked.Count == 0, $"Replace All stopped while it found every match asked '{string.Join("; ", asked)}'");
            string[] changed = [.. Directory.GetFiles(folder, "Tally*.txt").Where(f => !File.ReadAllText(f).StartsWith("tally 0 of", StringComparison.Ordinal)).Select(Path.GetFileName)!];
            Assert.True(changed.Length == 0, $"Replace All stopped while it found every match replaced in {string.Join(", ", changed)}");
            Assert.True(window.Actions.WhyNot(AppActions.Stop, window.Actions.Context()) is not null, "Replace All's find, stopped, left work under way for Stop to end");

            popup.RequestReplace();
            Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, () => $"Replace All asked again; the status says '{window.Status}'");
            Assert.Equal($"Replaced {Numbers.Said(1100)} matches in 110 files", window.Status);
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    private void A_UTF_16_file_is_found_and_replaced_in_as_the_editor_reads_it_and_a_binary_file_is_still_passed_over()
    {
        string little = Path.Combine(folder, "Wide.cs");
        string big = Path.Combine(folder, "WideBig.cs");
        File.WriteAllText(little, "class Wide\n{\n    int count;\n}\n", System.Text.Encoding.Unicode);
        File.WriteAllText(big, "// a count kept big-endian\n", System.Text.Encoding.BigEndianUnicode);
        File.WriteAllBytes(Path.Combine(folder, "Blob.bin"), [.. System.Text.Encoding.ASCII.GetBytes("count"), 0, 1, 2]);
        MainWindow window = Open();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Words = true;
        Ui.Search(popup, "count");
        Assert.True(popup.Matches.Any(m => m.Name == "Wide.cs" && m.Line == 2 && m.Column == 8 && m.LineText == "    int count;"),
            $"Find in Files passed over a UTF-16 file the editor opens, or placed its match wrong: {string.Join("; ", popup.Matches.Select(m => $"{m.Name}:{m.Line + 1}:{m.Column + 1} {m.LineText}"))}");
        Assert.Contains(popup.Matches, m => m.Name == "WideBig.cs" && m.LineText == "// a count kept big-endian");
        Assert.DoesNotContain(popup.Matches, m => m.Name == "Blob.bin");

        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "total";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, "the replacement");
        Assert.Equal("Replaced 5 matches in 4 files", window.Status);
        byte[] saved = File.ReadAllBytes(little);
        Assert.True(saved.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]), "Replace in Files wrote a UTF-16 file without its byte-order mark");
        Assert.Equal("class Wide\n{\n    int total;\n}\n", System.Text.Encoding.Unicode.GetString(saved, 2, saved.Length - 2));
        Assert.Equal("// a total kept big-endian\n", File.ReadAllText(big, System.Text.Encoding.BigEndianUnicode));
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Replace_all_across_a_read_only_file_replaces_in_the_rest_and_says_which_file_it_left_and_why()
    {
        // As Perforce or TFVC leave a file not checked out; first by name, so the files after it show the replace went on.
        string locked = Path.Combine(folder, "Aardvark.cs");
        File.WriteAllText(locked, "class Aardvark { int count; }\n");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Words = true;
        Ui.Search(popup, "count");
        Assert.Equal(4, popup.Matches.Count);

        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "total";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.Contains("Replaced", StringComparison.Ordinal) == true || window.Status?.StartsWith("Nothing", StringComparison.Ordinal) == true, "the replacement");
        Assert.Equal("Replaced 3 matches in 2 files; not replaced: Aardvark.cs (read-only)", window.Status);
        Assert.Equal("class Aardvark { int count; }\n", File.ReadAllText(locked));
        Assert.True(new FileInfo(locked).IsReadOnly, "Replace in Files made the read-only file writable");
        Assert.Equal("class Alpha\n{\n    int total;\n}\n", File.ReadAllText(Path.Combine(folder, "Alpha.cs")));
        Assert.Contains("void total()", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);

        Task<string> replacing = new Passenger.App.Mcp.PassengerTools(window).ReplaceInFiles("count|total", "sum", wholeWords: true, regex: true);
        Assert.Contains(Ui.Await(replacing, "replace_in_files' answer"), new[] { "Alpha.cs, Beta.cs", "Beta.cs, Alpha.cs" }.Select(files => $"Replaced 3 matches in 2 files: {files}; not replaced: Aardvark.cs (read-only)"));
        Assert.Equal("class Alpha\n{\n    int sum;\n}\n", File.ReadAllText(Path.Combine(folder, "Alpha.cs")));
        Assert.Contains("void sum()", File.ReadAllText(Path.Combine(folder, "Beta.cs")), StringComparison.Ordinal);
        Assert.Equal("class Aardvark { int count; }\n", File.ReadAllText(locked));
        window.Close();
    }

    /// <summary>Replace All across an open file and closed ones is one command: one Undo, asked once, takes every file back,
    /// on disk and in the editor, and one Redo makes it again; Local History keeps what it wrote as one change set named
    /// for it, not a change of each file's own.</summary>
    private void Replace_all_across_open_and_closed_files_is_one_undo_and_one_change_set_named_for_it()
    {
        const string Named = "Replace 'count' with 'total'";
        string alpha = Path.Combine(folder, "Alpha.cs");
        string gamma = Path.Combine(folder, "Gamma.cs");
        File.WriteAllText(gamma, "class Gamma { int count; }\n");
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        OpenDocument beta = window.EditorArea.Active!;
        string before = beta.Text.Snapshot.ToString();
        int asked = 0;
        window.Confirm = (_, _, _) =>
        {
            asked++;
            return Task.FromResult(true);
        };
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Words = true;
        Ui.Search(popup, "count");
        popup.Replacement = "total";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, () => $"the replacement; the status says '{window.Status}'");
        Assert.Equal("Replaced 4 matches in 3 files", window.Status);
        Assert.Equal("class Alpha\n{\n    int total;\n}\n", File.ReadAllText(alpha));
        Assert.Equal("class Gamma { int total; }\n", File.ReadAllText(gamma));
        Assert.Equal(before.Replace("count", "total", StringComparison.Ordinal).Replace("Count", "total", StringComparison.Ordinal), beta.Text.Snapshot.ToString());
        Ui.Press(window, "ESCAPE");

        Assert.True(window.Perform(AppActions.ShowProjectHistory), "Show Project History did not run");
        Passenger.App.History.HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Any(r => r.Title == Named), "the replace's change set in Local History");
        Assert.True(history.Rows.Count(r => r.Title == Named) == 1 && !history.Rows.Any(r => r.Title is "Modify Alpha.cs" or "Modify Gamma.cs"),
            $"Local History lists the replace's files as [{string.Join(", ", history.Rows.Select(r => r.Title))}], not one change set named for it");

        window.EditorArea.CodeEditor.Focus();
        asked = 0;
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.ReadAllText(alpha).Contains("int count;", StringComparison.Ordinal) && beta.Text.Snapshot.ToString() == before,
            () => $"one Undo to take the replace back in Alpha.cs and Beta.cs; Beta.cs reads '{beta.Text.Snapshot}', and the status says '{window.Status}'");
        Assert.Equal(1, asked);
        Assert.Equal("class Alpha\n{\n    int count;\n}\n", File.ReadAllText(alpha));
        Assert.Equal("class Gamma { int count; }\n", File.ReadAllText(gamma));

        Ui.Run(window, EditorActions.Redo);
        Ui.WaitFor(() => File.ReadAllText(alpha).Contains("int total;", StringComparison.Ordinal) && beta.Text.Snapshot.ToString() != before,
            () => $"one Redo to make the replace again in Alpha.cs and Beta.cs; the status says '{window.Status}'");
        Assert.Equal(2, asked);
        Assert.Equal("class Gamma { int total; }\n", File.ReadAllText(gamma));
        Assert.Contains("void total()", beta.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>An agent's replace_in_files is the same one command, its open file saved: one Undo in the window takes every
    /// file back.</summary>
    private void An_agents_replace_in_files_is_one_undo_across_the_files_it_changed()
    {
        string alpha = Path.Combine(folder, "Alpha.cs");
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        OpenDocument beta = window.EditorArea.Active!;
        string before = beta.Text.Snapshot.ToString();
        window.Confirm = (_, _, _) => Task.FromResult(true);

        Task<string> replacing = new Passenger.App.Mcp.PassengerTools(window).ReplaceInFiles("count", "total", wholeWords: true);
        Assert.Contains(Ui.Await(replacing, "replace_in_files' answer"), new[] { "Alpha.cs, Beta.cs", "Beta.cs, Alpha.cs" }.Select(files => $"Replaced 3 matches in 2 files: {files}"));
        Assert.Contains("void total()", File.ReadAllText(Path.Combine(folder, "Beta.cs")), StringComparison.Ordinal);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.ReadAllText(alpha).Contains("int count;", StringComparison.Ordinal) && beta.Text.Snapshot.ToString() == before,
            () => $"one Undo to take the agent's replace back in Alpha.cs and Beta.cs; Beta.cs reads '{beta.Text.Snapshot}', and the status says '{window.Status}'");
        Assert.Equal("class Alpha\n{\n    int count;\n}\n", File.ReadAllText(alpha));
        window.Close();
    }

    /// <summary>Replace in Files reads no closed file on the window's thread, and makes its writes and the texts its Undo
    /// keeps off it: a replace across three closed files of 2 MB each costs that thread no more than one across three
    /// small ones, where a read of one of them there costs at least its bytes.</summary>
    private void A_replace_across_large_closed_files_costs_the_window_thread_what_one_across_small_ones_does()
    {
        const int Large = 2 * 1024 * 1024;
        string smalls = Path.Combine(folder, "small");
        string larges = Path.Combine(folder, "large");
        Directory.CreateDirectory(smalls);
        Directory.CreateDirectory(larges);
        const string Filler = "// a line of filler, with nothing in it to replace\n";
        string filler = string.Concat(Enumerable.Repeat(Filler, Large / Filler.Length + 1));
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(smalls, $"Small{i}.txt"), "int count;\n");
            File.WriteAllText(Path.Combine(larges, $"Large{i}.txt"), "int count;\n" + filler);
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            long few = Costs(smalls);
            long many = Costs(larges);
            Assert.True(many < few + Large,
                $"A replace across three closed files of 2 MB allocated {many / 1024} KB on the window's thread, across three small ones {few / 1024} KB: the files are read or their texts made there, stalling the window as the files grow");
            Assert.StartsWith("int count;\n", File.ReadAllText(Path.Combine(larges, "Large0.txt")), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }

        // The least a replace in the folder's files allocated on the window's thread, over rounds there and back.
        long Costs(string directory)
        {
            long least = long.MaxValue;
            for (int round = 0; round < 4; round++)
            {
                (string from, string to) = round % 2 == 0 ? ("count", "total") : ("total", "count");
                SearchQuery query = new(from, WholeWords: true);
                FilesFound found = Ui.Await(window.FindInFilesAsync(query, new SearchScope(SearchScopeKind.Directory, null, directory, true, null, null), CancellationToken.None),
                    $"the search for {from} in {Path.GetFileName(directory)}");
                Assert.Equal(3, found.Matches.Count);
                long before = GC.GetAllocatedBytesForCurrentThread();
                FilesReplaced done = Ui.Await(window.ReplaceInFilesAsync(query, to, found), $"the replace of {from} with {to} in {Path.GetFileName(directory)}");
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.Empty(done.Refused);
            }
            return least;
        }
    }

    /// <summary>Undo of a replace across closed files is refused, naming the file, when one changed while its question was
    /// open, every file left as it was; once that file is as the replace left it again, Undo takes the replace back.</summary>
    private void Undo_of_a_replace_is_refused_naming_a_closed_file_changed_while_its_question_was_open()
    {
        string alpha = Path.Combine(folder, "Alpha.cs");
        string beta = Path.Combine(folder, "Beta.cs");
        string alphaBefore = File.ReadAllText(alpha);
        string betaBefore = File.ReadAllText(beta);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            SearchQuery query = new("count", WholeWords: true);
            FilesFound found = Ui.Await(window.FindInFilesAsync(query, new SearchScope(SearchScopeKind.Directory, null, folder, true, null, null), CancellationToken.None), "the search");
            Assert.Empty(Ui.Await(window.ReplaceInFilesAsync(query, "total", found), "the replace").Refused);
            string name = FilesReplaced.Title(query, "total");
            string alphaReplaced = File.ReadAllText(alpha);
            string betaReplaced = File.ReadAllText(beta);
            Assert.NotEqual(betaBefore, betaReplaced);
            window.Confirm = (_, _, _) =>
            {
                File.AppendAllText(beta, "// written while Undo asked\n");
                return Task.FromResult(true);
            };
            string row = PathNames.Canonical(alpha);
            window.ExplorerView.Reveal(row);
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == row));
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.Status?.StartsWith("Cannot undo", StringComparison.Ordinal) == true, () => $"Undo to be refused; the status says '{window.Status}'");
            Assert.Equal($"Cannot undo {name}: Beta.cs has been changed since", window.Status);
            Assert.Equal(alphaReplaced, File.ReadAllText(alpha));
            Assert.Equal(betaReplaced + "// written while Undo asked\n", File.ReadAllText(beta));

            File.WriteAllText(beta, betaReplaced);
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.Status == $"Undone: {name}", () => $"Undo once Beta.cs is as the replace left it; the status says '{window.Status}'");
            Assert.Equal(alphaBefore, File.ReadAllText(alpha));
            Assert.Equal(betaBefore, File.ReadAllText(beta));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Undo of a replace across an open file and a closed one the disk then refuses (made read-only) takes back what
    /// it had done before the refusal: both files are as the replace left them, Undo still offered for the open file; once
    /// the closed file is writable again, Undo takes the whole replace back.</summary>
    private void Undo_of_a_replace_refused_by_its_closed_file_leaves_both_files_as_the_replace_left_them_with_undo_offered()
    {
        string alpha = Path.Combine(folder, "Alpha.cs");
        string beta = Path.Combine(folder, "Beta.cs");
        string alphaBefore = File.ReadAllText(alpha);
        string betaBefore = File.ReadAllText(beta);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(alpha);
            Ui.Settle();
            OpenDocument open = window.EditorArea.Active!;
            SearchQuery query = new("count", WholeWords: true);
            FilesFound found = Ui.Await(window.FindInFilesAsync(query, new SearchScope(SearchScopeKind.Directory, null, folder, true, null, null), CancellationToken.None), "the search");
            Assert.Empty(Ui.Await(window.ReplaceInFilesAsync(query, "total", found), "the replace").Refused);
            string name = FilesReplaced.Title(query, "total");
            string alphaReplaced = open.Text.Snapshot.ToString();
            string betaReplaced = File.ReadAllText(beta);
            Assert.True(alphaReplaced != alphaBefore && betaReplaced != betaBefore, "The replace changed only one of Alpha.cs and Beta.cs");
            File.SetAttributes(beta, FileAttributes.ReadOnly);
            window.Confirm = (_, _, _) => Task.FromResult(true);

            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.Status?.StartsWith("Cannot undo", StringComparison.Ordinal) == true, () => $"Undo to be refused; the status says '{window.Status}'");
            Assert.Equal($"Cannot undo {name}: Beta.cs could not be written: Beta.cs is read-only", window.Status);
            Assert.True(open.Text.Snapshot.ToString() == alphaReplaced,
                $"The Undo Beta.cs refused left Alpha.cs's replace taken back, Alpha.cs reading:\n{open.Text.Snapshot}");
            Assert.Equal(betaReplaced, File.ReadAllText(beta));
            Assert.True(window.EditorArea.Commands.ToUndo(open)?.Name == name, "The Undo Beta.cs refused left the replace where Undo no longer offers it");

            File.SetAttributes(beta, FileAttributes.Normal);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.Status == $"Undone: {name}", () => $"Undo once Beta.cs is writable; the status says '{window.Status}'");
            Assert.Equal(alphaBefore, open.Text.Snapshot.ToString());
            Assert.Equal(betaBefore, File.ReadAllText(beta));
        }
        finally
        {
            File.SetAttributes(beta, FileAttributes.Normal);
            window.Close();
        }
    }

    [Gate]
    [AvaloniaFact]
    public void A_regex_backtracking_without_end_in_a_file_is_said_to_stop_there_to_the_popup_and_an_agent_and_replace_all_leaves_that_file()
    {
        string runs = Path.Combine(folder, "Runs.txt");
        string text = "ab\n" + new string('a', 3000) + "\n";
        File.WriteAllText(runs, text);
        MainWindow window = Open();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Regex = true;
        Ui.Search(popup, "(a+)+b|count");
        Assert.True(popup.Status == $"4 matches in 3 files: {Refusals.RegexStoppedIn("Runs.txt")}", $"The popup says '{popup.Status}' of a regex that never ends in Runs.txt");
        Assert.Contains(popup.Matches, m => m is { Name: "Runs.txt", Line: 0, Column: 0, Length: 2 });

        Task<Passenger.App.Mcp.FindInFilesResult> asking = new Passenger.App.Mcp.PassengerTools(window).FindInFiles("(a+)+b|count", regex: true);
        Passenger.App.Mcp.FindInFilesResult answer = Ui.Await(asking, "find_in_files' answer");
        Assert.True(answer is { Count: 4, Stopped: ["Runs.txt"] }, $"find_in_files told an agent of {answer.Count} matches, stopped in {string.Join(", ", answer.Stopped ?? [])}");

        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "x";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, () => $"the replacement; the status says '{window.Status}'");
        Assert.Equal($"Replaced 3 matches in 2 files; not replaced: Runs.txt ({Refusals.RegexStopped})", window.Status);
        Assert.True(File.ReadAllText(runs) == text, "Replace All replaced the match found in Runs.txt before its search stopped");
        Assert.Equal("class Alpha\n{\n    int x;\n}\n", File.ReadAllText(Path.Combine(folder, "Alpha.cs")));
        window.Close();
    }

    /// <summary>A link in the folder to a file outside it (a cloned repository may carry one): Find in Files lists its match,
    /// but Replace All and an agent's replace_in_files leave the file outside as it was, saying why, and replace the rest.</summary>
    private void Replace_all_and_replace_in_files_leave_a_file_outside_the_folder_that_a_link_in_it_leads_to()
    {
        string outside = TestFolders.Create("outside-");
        try
        {
            string target = Path.Combine(PathNames.Canonical(outside), "profile.txt");
            const string kept = "count on me\n";
            File.WriteAllText(target, kept);
            TestFolders.Link(Path.Combine(folder, "notes.txt"), target);
            MainWindow window = Open();
            FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
            popup.Words = true;
            Ui.Search(popup, "count");
            Assert.True(popup.Matches.Any(m => PathNames.Same(m.Path, target)), $"Find in Files did not list the match in profile.txt, through notes.txt: {string.Join(", ", popup.Matches.Select(m => m.Path))}");

            window.Confirm = (_, _, _) => Task.FromResult(true);
            popup.Replacement = "total";
            popup.RequestReplace();
            Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, () => $"the replacement; the status says '{window.Status}'");
            Assert.Equal("Replaced 3 matches in 2 files; not replaced: profile.txt (outside the folder, where a link in it leads)", window.Status);
            Assert.True(File.ReadAllText(target) == kept, $"Replace All wrote profile.txt, outside the folder, through the link notes.txt: {File.ReadAllText(target)}");
            Assert.Equal("class Alpha\n{\n    int total;\n}\n", File.ReadAllText(Path.Combine(folder, "Alpha.cs")));

            Task<string> replacing = new Passenger.App.Mcp.PassengerTools(window).ReplaceInFiles("count", "sum", wholeWords: true);
            string answer = Ui.Await(replacing, "replace_in_files' answer");
            Assert.True(answer.Contains("not replaced: ", StringComparison.Ordinal) && answer.Contains("profile.txt", StringComparison.Ordinal),
                $"replace_in_files did not say it left profile.txt, outside the folder through the link notes.txt: {answer}");
            Assert.True(File.ReadAllText(target) == kept, $"replace_in_files wrote profile.txt, outside the folder, through the link notes.txt: {File.ReadAllText(target)}");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(outside);
        }
    }

    /// <summary>Replace in Files of every word's edge (\b) with the whole text ($_) in a file of 12,000 characters would
    /// make it 144 million long: the file is named as left as it was, with how long its text would be.</summary>
    [Gate]
    [AvaloniaFact]
    public void Replace_all_that_would_make_a_files_text_too_long_leaves_it_and_says_so()
    {
        string words = Path.Combine(folder, "Words.txt");
        string text = string.Concat(Enumerable.Repeat("a a a a a a a a a a\n", 600));
        File.WriteAllText(words, text);
        MainWindow window = Open();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Regex = true;
        popup.FileMask = "*.txt";
        Ui.Search(popup, @"\b");
        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "$_";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Nothing replaced", StringComparison.Ordinal) == true || window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true,
            () => $"the replacement; the status says '{window.Status}'");
        Assert.Equal("Nothing replaced; not replaced: Words.txt (the text replaced would be 144,012,000 characters long, more than 100,000,000)", window.Status);
        Assert.True(File.ReadAllText(words) == text, "Replace in Files wrote a file whose replaced text it said was too long");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Replace_all_over_an_open_file_keeps_its_caret_folds_and_breakpoints()
    {
        const string Source = "class Gamma\n{\n    int Count()\n    {\n        int count = 0;\n        return count;\n    }\n\n"
            + "    void Other()\n    {\n        Log(count);\n    }\n\n    void Last() { }\n}\n";
        string path = Path.Combine(folder, "Gamma.cs");
        File.WriteAllText(path, Source);
        MainWindow window = Open();
        window.OpenFile(path);
        Ui.Settle();
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument gamma = window.EditorArea.Active!;
        Ui.WaitFor(() => editor.FoldRegions.Count == 3, "the file's regions");
        // Other folded (Ctrl+M, S in its body), breakpoints in it and on Last, the caret on Last.
        editor.GoTo(new TextPosition(10, 8));
        editor.Focus();
        Ui.Press(window, "control M , control S");
        window.Debugger.Breakpoints.Toggle(gamma.Path, 10);
        window.Debugger.Breakpoints.Toggle(gamma.Path, 13);
        editor.GoTo(new TextPosition(13, 9));

        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.MatchCase = true;
        popup.Words = true;
        Ui.Search(popup, "count");
        window.Confirm = (_, _, _) => Task.FromResult(true);
        popup.Replacement = "amount";
        popup.RequestReplace();
        Ui.WaitFor(() => gamma.Text.Snapshot.ToString().Contains("Log(amount);", StringComparison.Ordinal), "the open file's matches replaced");
        Ui.Press(window, "ESCAPE");
        Ui.Settle();

        string text = gamma.Text.Snapshot.ToString();
        Assert.Equal(new TextPosition(13, 9), editor.CaretPosition);
        Assert.Equal([10, 13], window.Debugger.Breakpoints.In(gamma.Path).Select(b => b.Line));
        FoldRegion folded = Assert.Single(editor.FoldRegions, r => r.Folded);
        Assert.Equal(text.IndexOf("void Other()", StringComparison.Ordinal) + "void Other()".Length, folded.Start);

        editor.Focus();
        editor.TryExecute(Passenger.Editor.EditorActions.Undo);
        Assert.Equal(Source, gamma.Text.Snapshot.ToString());
        Assert.Equal(new TextPosition(13, 9), editor.CaretPosition);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Replace_in_files_fills_a_regexs_groups_from_each_match_where_it_stands_and_words_take_an_alternation_whole()
    {
        string calls = Path.Combine(folder, "Calls.cs");
        File.WriteAllText(calls, "// catalog cat hotdog dog\nfoo(1); bar (2);\n");
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        OpenDocument beta = window.EditorArea.Active!;
        window.Confirm = (_, _, _) => Task.FromResult(true);
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Regex = true;
        Ui.Search(popup, @"(\w+)(?=\()");
        Assert.Equal(2, popup.Matches.Count);

        popup.Replacement = "$1_";
        popup.RequestReplace();
        Ui.WaitFor(() => File.ReadAllText(calls) != "// catalog cat hotdog dog\nfoo(1); bar (2);\n", "Calls.cs to be written");
        Assert.Equal("// catalog cat hotdog dog\nfoo_(1); bar (2);\n", File.ReadAllText(calls));
        Assert.Contains("    void Count_() { }", beta.Text.Snapshot.ToString(), StringComparison.Ordinal);

        popup.Words = true;
        Ui.Search(popup, "cat|dog");
        Assert.True(popup.Matches.Count == 2, $"cat|dog with Words found {popup.Matches.Count}: a word's part (catalog, hotdog) was taken for a whole word");
        popup.Replacement = "pet";
        popup.RequestReplace();
        Ui.WaitFor(() => File.ReadAllText(calls).StartsWith("// catalog pet", StringComparison.Ordinal), "Calls.cs to be written again");
        Assert.Equal("// catalog pet hotdog pet\nfoo_(1); bar (2);\n", File.ReadAllText(calls));
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Replacing_every_lines_start_in_files_comments_each_line_and_leaves_each_files_end_alone()
    {
        string gamma = Path.Combine(folder, "Gamma.cs");
        File.WriteAllText(gamma, "class Gamma\r\n{\r\n\r\n}\r\n");
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        OpenDocument beta = window.EditorArea.Active!;
        window.Confirm = (_, _, _) => Task.FromResult(true);
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Regex = true;
        Ui.Search(popup, "^");
        // Every line's start, an empty line's too, but not the end after a file's last break.
        Assert.Equal((4, 5, 4), (popup.Matches.Count(m => m.Name == "Alpha.cs"), popup.Matches.Count(m => m.Name == "Beta.cs"), popup.Matches.Count(m => m.Name == "Gamma.cs")));

        popup.Replacement = "// ";
        popup.RequestReplace();
        Ui.WaitFor(() => File.ReadAllText(gamma).StartsWith("// ", StringComparison.Ordinal), "Gamma.cs to be written");
        Assert.Equal("// class Alpha\n// {\n//     int count;\n// }\n", File.ReadAllText(Path.Combine(folder, "Alpha.cs")));
        Assert.Equal("// class Gamma\r\n// {\r\n// \r\n// }\r\n", File.ReadAllText(gamma));
        Assert.Equal("// // the count of things\n// class Beta\n// {\n//     void Count() { }\n// }\n", beta.Text.Snapshot.ToString());
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Recent_files_offers_the_files_behind_the_one_in_front_and_opens_one_where_it_was_left()
    {
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Alpha.cs"));
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        Ui.Run(window, AppActions.RecentFiles);
        SymbolPopup popup = Assert.IsType<SymbolPopup>(window.Popup);
        Ui.WaitFor(() => popup.IsSettled, "the recent files");
        Assert.Equal(["Alpha.cs"], popup.Rows.Select(r => r.Name));
        Ui.Press(window, "ENTER");
        Assert.Equal("Alpha.cs", window.EditorArea.Active?.Name);
        Assert.Equal(new TextPosition(2, 4), window.EditorArea.CodeEditor.CaretPosition);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Bookmarks_toggle_move_with_their_lines_and_next_goes_round_the_files()
    {
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Alpha.cs"));
        Ui.Settle();
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 0));
        Ui.Run(window, AppActions.ToggleBookmark);
        OpenDocument alpha = window.EditorArea.Active!;
        Assert.Equal([2], alpha.Bookmarks);
        Assert.Equal([2], editor.Bookmarks);

        editor.ApplyEdits([new TextEdit(0, 0, "// top\n")]);
        Assert.Equal([3], alpha.Bookmarks);

        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        editor.GoTo(new TextPosition(3, 0));
        Ui.Run(window, AppActions.ToggleBookmark);
        Ui.Run(window, AppActions.NextBookmark);
        Assert.Equal(("Alpha.cs", 3), (window.EditorArea.Active!.Name, editor.CaretPosition.Line));
        Ui.Run(window, AppActions.PreviousBookmark);
        Assert.Equal(("Beta.cs", 3), (window.EditorArea.Active!.Name, editor.CaretPosition.Line));

        Assert.Equal(2, window.Bookmarks.All.Count);
        Ui.Run(window, AppActions.ToggleBookmark);
        Assert.Equal(["Alpha.cs"], window.Bookmarks.All.Select(b => b.Name));
        Assert.Empty(window.EditorArea.Active!.Bookmarks);

        Ui.Run(window, AppActions.ShowBookmarks);
        Avalonia.Controls.ListBox listed = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.DockHost)
            .OfType<Avalonia.Controls.ListBox>().First(l => l.Items.OfType<Passenger.App.Navigation.Bookmark>().Any());
        listed.SelectedIndex = 0;
        Ui.ChooseFromMenu(listed, "Remove Bookmark");
        Assert.Empty(window.Bookmarks.All);
        window.Close();
    }

    /// <summary>A file open with no folder stays open as a folder opens, and the session left lets go of all it carried in
    /// the file's text (its bookmark, breakpoint and places), so the text carries no more than it did before them.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_file_left_open_as_a_folder_opens_is_carried_by_nothing_of_the_session_left()
    {
        string other = TestFolders.Create("other-");
        try
        {
            MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false };
            window.Show();
            window.OpenFile(Path.Combine(folder, "Alpha.cs"));
            Ui.Settle();
            OpenDocument alpha = window.EditorArea.Active!;
            int bare = alpha.Text.Anchors;
            Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(2, 4));
            Ui.Run(window, AppActions.ToggleBookmark);
            window.Debugger.Breakpoints.Toggle(alpha.Path, 2);
            editor.ApplyEdits([new TextEdit(0, 0, "// top\n")]);
            Ui.Settle();
            Assert.Equal([3], alpha.Bookmarks);
            int carried = alpha.Text.Anchors;
            Assert.True(carried >= bare + 3, $"The file's text carries {carried} anchors with a bookmark, a breakpoint and an edit's place, {bare} without");

            window.OpenFolder(other);
            Ui.Settle();
            Assert.Same(alpha, window.EditorArea.Active);
            Assert.True(alpha.Text.Anchors == bare,
                $"With a folder open, the file left open carries {alpha.Text.Anchors} anchors, {bare} before the session left set any: its bookmark, breakpoint or places are still carried");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(other);
        }
    }

    /// <summary>A breakpoint is an anchor its file's text carries, so a key moves those below it without touching each: the
    /// least of twenty keys typed above 2,000 breakpoints allocates on the UI thread under 2 bytes for each more than above
    /// none, and a line broken above them takes each one down with its line.</summary>
    [Gate]
    [AvaloniaFact]
    public void Keys_typed_above_two_thousand_breakpoints_carry_them_on_the_UI_thread_without_costing_each()
    {
        long none = UiBytesPerKey(0);
        long many = UiBytesPerKey(2000);
        long each = (many - none) / 2000;
        TestContext.Current.TestOutputHelper?.WriteLine($"A key allocated at least {many:N0} bytes on the UI thread above 2,000 breakpoints, {none:N0} above none: {each} bytes a breakpoint.");
        Assert.True(each < 2,
            $"A key allocated at least {many / 1024:N0} KB on the UI thread above 2,000 breakpoints, {none / 1024:N0} KB above none: {each} bytes for each breakpoint, which the key carried one by one");
    }

    /// <summary>The least of twenty keys typed at the top of a file of 4,000 lines allocates on the UI thread, with as many
    /// breakpoints below on every other line, which Enter above then takes down a line each.</summary>
    private long UiBytesPerKey(int breakpoints)
    {
        System.Text.StringBuilder text = new("class Long\n{\n    int typed = 1;\n");
        for (int i = 0; i < 4000; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    int field{i};\n");
        }
        string source = text.Append("}\n").ToString();
        string path = Path.Combine(folder, $"Long{breakpoints}.cs");
        File.WriteAllText(path, source);
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700, languageServer: false);
        window.OpenFile(path);
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        for (int i = 0; i < breakpoints; i++)
        {
            window.Debugger.Breakpoints.Toggle(document.Path, 3 + (2 * i));
        }
        editor.Selections = SelectionSet.Single(source.IndexOf("= 1;", StringComparison.Ordinal) + 2);
        editor.Focus();
        Ui.Settle();
        long least = long.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput("1");
            Ui.Settle();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Ui.Press(window, "ENTER");
        Ui.Settle();
        IReadOnlyList<int> lines = [.. window.Debugger.Breakpoints.In(document.Path).Select(b => b.Line)];
        Assert.True(lines.SequenceEqual(Enumerable.Range(0, breakpoints).Select(i => 4 + (2 * i))),
            $"After Enter above them, the breakpoints were on lines {string.Join(", ", lines.Take(5))}, not each a line down from where it was set");
        Assert.True(editor.LineMarks.Select(m => m.Line).SequenceEqual(lines), "The gutter's breakpoints were not on the lines the breakpoints were carried to");
        window.Close();
        return least;
    }

    /// <summary>The Explorer dims what Find in Files, Go to File and the watcher pass over, by one list: each of its
    /// names, and a packages folder NuGet restored into, in packages.config's layout or the global folder's; Git's
    /// store is not listed at all. A JavaScript monorepo's packages of source are searched, offered and shown as
    /// code.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_explorer_dims_what_find_in_files_and_go_to_file_pass_over_and_a_monorepos_packages_of_source_are_searched_and_shown_as_code()
    {
        string root = PathNames.Canonical(folder);
        string[] passed = [.. IgnoredFolders.Names.Where(name => name != ".git").Append("packages")];
        foreach (string name in passed)
        {
            TestFolders.Write(folder, $"{name}/needle.ts", "// needle\n");
        }
        TestFolders.Write(folder, "packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg", "");
        TestFolders.Write(folder, "packages/Newtonsoft.Json.13.0.1/needle.ts", "// needle\n");
        TestFolders.Write(folder, "tools/packages/newtonsoft.json/13.0.1/.nupkg.metadata", "{ }\n");
        TestFolders.Write(folder, "tools/packages/newtonsoft.json/13.0.1/needle.ts", "// needle\n");
        TestFolders.Write(folder, "web/packages/ui/package.json", "{ \"name\": \"ui\" }\n");
        TestFolders.Write(folder, "web/packages/ui/src/needle.ts", "// needle\n");
        TestGit.Run(folder, "init", "-q", "-b", "main");
        string source = Path.Combine("web", "packages", "ui", "src", "needle.ts");
        MainWindow window = Open();
        try
        {
            ExplorerTree tree = window.ExplorerView.Tree;
            Assert.DoesNotContain(tree.Rows, row => row.Name == ".git");
            foreach (string name in passed)
            {
                ExplorerNode row = tree.Rows.Single(r => r.Path == Path.Combine(root, name));
                Assert.True(row.IsDimmed, $"The Explorer shows {name} as the person's code, though Find in Files and Go to File pass over it");
            }
            ExplorerNode restored = tree.Reveal(Path.Combine(root, "tools", "packages", "newtonsoft.json", "13.0.1", "needle.ts"))!;
            Ui.Settle();
            Assert.True(restored.IsDimmed && tree.Rows.Single(r => r.Path == Path.Combine(root, "tools", "packages")).IsDimmed,
                "The Explorer shows a packages folder of the global folder's layout as code, though the walks pass over it");
            ExplorerNode code = tree.Reveal(Path.Combine(root, source))!;
            Ui.Settle();
            string[] dimmed = [.. tree.Rows.Where(r => r.IsDimmed && r.Path is { } path && (PathNames.IsUnder(path, Path.Combine(root, "web")) || path == Path.Combine(root, "web"))).Select(r => r.Name)];
            Assert.True(!code.IsDimmed && dimmed.Length == 0, $"The Explorer dims [{string.Join(", ", dimmed)}] of a monorepo's packages, which are its source");

            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Ui.Search(popup, "needle");
            string[] found = [.. popup.Matches.Select(m => Path.GetRelativePath(root, m.Path)).Distinct()];
            Assert.True(found.SequenceEqual([source]), $"Find in Files found needle in [{string.Join(", ", found)}], where the monorepo's packages alone are the person's code");
            Ui.Press(window, "ESCAPE");

            Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
            window.KeyTextInput("needle");
            Ui.Settle();
            string[] offered = [.. Assert.IsType<GoToFilePopup>(window.Popup).Rows.Select(hit => hit.RelativePath)];
            Assert.True(offered.SequenceEqual([source]), $"Go to File offers [{string.Join(", ", offered)}] for needle, where the monorepo's packages alone are the person's code");
            Ui.Press(window, "ESCAPE");

            TestFolders.Write(folder, "web/packages/ui/src/later.ts", "// later\n");
            Ui.WaitFor(() => window.FileIndex.Files.Contains(Path.Combine("web", "packages", "ui", "src", "later.ts")), "the file made in the monorepo's packages in Go to File");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A directory chosen inside a folder the folder's search passes over (node_modules, bin) is searched all the
    /// same, from the Explorer's menu on its dimmed row or named in the popup, and Replace in Files changes its files
    /// alone.</summary>
    private void A_directory_scope_inside_node_modules_or_bin_is_searched_and_replaced_in_though_the_folders_search_passes_over_it()
    {
        TestFolders.Write(folder, "node_modules/pkg/index.js", "// needle in package\n");
        TestFolders.Write(folder, "bin/Debug/out.txt", "needle in bin\n");
        TestFolders.Write(folder, "src/a.js", "// needle in src\n");
        string root = PathNames.Canonical(folder);
        string package = Path.Combine(root, "node_modules", "pkg");
        string output = Path.Combine(root, "bin", "Debug");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Ui.Search(popup, "needle");
            Assert.Equal([Path.Combine("src", "a.js")], Found(popup, root));
            Ui.Press(window, "ESCAPE");

            window.ExplorerView.Reveal(package);
            Ui.Settle();
            ExplorerNode row = window.ExplorerView.Selected ?? throw new InvalidOperationException("The Explorer shows no node_modules/pkg");
            Assert.True(row.IsDimmed, "The Explorer shows node_modules/pkg as the person's code, though the folder's search passes over it");
            Ui.ChooseFromRowMenu(window, row, "Edit", "Find in Files…");
            popup = Assert.IsType<FindInFilesPopup>(window.Popup);
            Assert.Equal((SearchScopeKind.Directory, package), (popup.ScopeKind, popup.Directory));
            Ui.Search(popup, "needle");
            string[] found = Found(popup, root);
            Assert.True(found.SequenceEqual([Path.Combine("node_modules", "pkg", "index.js")]),
                $"Find in Files on node_modules/pkg found [{string.Join(", ", found)}], where its index.js holds needle");
            Ui.Press(window, "ESCAPE");

            window.Confirm = (_, _, _) => Task.FromResult(true);
            popup = window.ShowFindInFiles(replace: true)!;
            popup.ScopeKind = SearchScopeKind.Directory;
            popup.Directory = output;
            Ui.Search(popup, "needle");
            found = Found(popup, root);
            Assert.True(found.SequenceEqual([Path.Combine("bin", "Debug", "out.txt")]),
                $"Find in Files on bin/Debug found [{string.Join(", ", found)}], where its out.txt holds needle");
            popup.Replacement = "pin";
            popup.RequestReplace();
            Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, "the replacement in bin/Debug");
            Assert.Equal("Replaced 1 match in 1 file", window.Status);
            Assert.Equal("pin in bin\n", File.ReadAllText(Path.Combine(output, "out.txt")));
            Assert.Equal("// needle in package\n", File.ReadAllText(Path.Combine(package, "index.js")));
            Assert.Equal("// needle in src\n", File.ReadAllText(Path.Combine(root, "src", "a.js")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The files a popup's matches are in, from the folder, each once, in order.</summary>
    private static string[] Found(FindInFilesPopup popup, string root) =>
        [.. popup.Matches.Select(m => Path.GetRelativePath(root, m.Path)).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>Find in Files closed with a match in its preview: the popup, its preview editor and the file it showed go,
    /// so each search made leaves nothing behind for the window's life.</summary>
    private void Find_in_files_closed_is_let_go_with_its_preview_and_the_file_it_showed()
    {
        MainWindow window = Open();
        WeakReference<FindInFilesPopup> closed = SearchAndClose(window);
        Ui.Collected(closed, "the closed Find in Files popup to be let go: held from the dispatcher by its preview's timer, "
            + "each search would keep its popup, its matches and the file its preview showed, colours and all, for good");
        window.Close();
    }

    /// <summary>Searches with a match shown in the preview, then closes the popup by Escape: held only weakly here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<FindInFilesPopup> SearchAndClose(MainWindow window)
    {
        FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
        Ui.Search(popup, "count");
        Ui.Settle();
        CodeEditor preview = popup.GetVisualDescendants().OfType<CodeEditor>().Single();
        Assert.True(preview.Document.Snapshot.ToString().Contains("count", StringComparison.Ordinal), "The preview shows no file with a match");
        Ui.Press(window, "ESCAPE");
        Assert.False(window.IsPopupOpen, "Escape left Find in Files open");
        return new WeakReference<FindInFilesPopup>(popup);
    }

    /// <summary>Searches for a text and waits for the search in force to show its matches: the box tells of its new text
    /// after the frame, before the search begins; another change that lands meanwhile searches again, cancelling this one.</summary>
    private MainWindow Open() => Ui.OpenFolder(folder);
}

/// <summary>What Find in Files allocates over many files against few, counted on every thread: the class runs alone, as
/// whatever a class beside the window tests allocates meanwhile would count as the search's.</summary>
[Collection(Alone.Name)]
public sealed class FindInFilesBytesTests
{
    /// <summary>Find in Files reads as text only the files whose bytes may hold a match: over 2,000 files of 4 KB with a match
    /// in a few, each search allocates (on every thread, the least of three) under 4 KB for each file more than over 20,
    /// where reading one as text costs ten times that. It finds the same matches in UTF-8, UTF-8 with a BOM, UTF-16 and
    /// 8-bit files, ignoring case as its regex does (a Kelvin sign is a k), matching case, by whole words and by regex.</summary>
    [AvaloniaFact]
    public void A_search_over_two_thousand_files_reads_as_text_only_those_whose_bytes_may_match()
    {
        (string Text, bool MatchCase, bool Words, bool Regex, string Found)[] queries =
        [
            ("needle", false, false, false, "Bom.cs:1:6, Found.cs:1:8, Latin.cs:1:13, Wide.cs:1:6"),
            ("kelvin", false, false, false, "Kelvin.cs:1:4"),
            ("Needle", true, false, false, "Found.cs:1:8"),
            ("needle", false, true, false, "Bom.cs:1:6, Found.cs:1:8, Latin.cs:1:13, Wide.cs:1:6"),
            (@"ne+dle\b", false, false, true, "Bom.cs:1:6, Found.cs:1:8, Latin.cs:1:13, Wide.cs:1:6"),
        ];
        string small = TestFolders.Create("find-few-");
        string large = TestFolders.Create("find-many-");
        try
        {
            Files(small, 20);
            Files(large, 2000);
            (long Bytes, string Found)[] few = Searches(small, queries);
            (long Bytes, string Found)[] many = Searches(large, queries);
            for (int i = 0; i < queries.Length; i++)
            {
                string asked = $"{queries[i].Text}{(queries[i].MatchCase ? " matching case" : string.Empty)}{(queries[i].Words ? " as a word" : string.Empty)}{(queries[i].Regex ? " as a regex" : string.Empty)}";
                long each = (many[i].Bytes - few[i].Bytes) / 1980;
                TestContext.Current.TestOutputHelper?.WriteLine($"{asked}: {few[i].Bytes / 1024:N0} KB over 20 files, {many[i].Bytes / 1024:N0} KB over 2,000, {each:N0} bytes a file more");
                Assert.True(few[i].Found == queries[i].Found && many[i].Found == queries[i].Found,
                    $"Find in Files for {asked} found [{few[i].Found}] over 20 files and [{many[i].Found}] over 2,000, not [{queries[i].Found}]");
                Assert.True(each < 4096,
                    $"Find in Files for {asked} allocated {many[i].Bytes / 1024:N0} KB over 2,000 files, {few[i].Bytes / 1024:N0} KB over 20: {each:N0} bytes for each file more, as if every file were read as text");
            }
        }
        finally
        {
            TestFolders.Delete(small);
            TestFolders.Delete(large);
        }

        // Files of code that hold no match, a hundred to a folder, and beside them a match in each encoding.
        static void Files(string root, int count)
        {
            TestFolders.WriteAll(root, Enumerable.Range(0, count).Select(i => ($"src/Area{i / 100}/Filler{i}.cs",
                $"class Filler{i}\n{{\n" + string.Concat(Enumerable.Range(0, 80).Select(j => $"    int field{j} = {j} * {i}; // a sum of the parts\n")) + "}\n")));
            File.WriteAllText(Path.Combine(root, "Found.cs"), "// the Needle is here\nclass Found { }\n");
            File.WriteAllText(Path.Combine(root, "Bom.cs"), "// a needle after a BOM\n", new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(root, "Wide.cs"), "// a needle in UTF-16\n", System.Text.Encoding.Unicode);
            File.WriteAllBytes(Path.Combine(root, "Latin.cs"), System.Text.Encoding.Latin1.GetBytes("// déjà une needle\n"));
            File.WriteAllText(Path.Combine(root, "Kelvin.cs"), "// Kelvin's line\n");
            File.WriteAllBytes(Path.Combine(root, "Blob.bin"), [.. System.Text.Encoding.ASCII.GetBytes("needle kelvin"), 0, 1, 2]);
        }
    }

    /// <summary>What each search over a folder allocates on every thread from its query to its matches shown, the least of
    /// three, as a query's first search pays once for what the others reuse; and the matches as file:line:column, in
    /// order.</summary>
    private static (long Bytes, string Found)[] Searches(string root, (string Text, bool MatchCase, bool Words, bool Regex, string Found)[] queries)
    {
        MainWindow window = Ui.OpenFolder(root, languageServer: false);
        Ui.WaitFor(() => window.FileIndex.Count > 0 && !window.FileIndex.IsWalking, "the folder's files indexed");
        FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
        List<(long Bytes, string Found)> searches = [];
        foreach ((string text, bool matchCase, bool words, bool regex, _) in queries)
        {
            popup.MatchCase = matchCase;
            popup.Words = words;
            popup.Regex = regex;
            long least = long.MaxValue;
            for (int time = 0; time < 3; time++)
            {
                // The box tells of a new text after the frame, which puts the search off until a pause: told before searching now.
                popup.Text = string.Empty;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                long before = GC.GetTotalAllocatedBytes(precise: true);
                popup.Text = text;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Task searching = popup.SearchAsync();
                Ui.WaitFor(() => searching.IsCompleted, $"the search for {text}");
                least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
            }
            searches.Add((least, string.Join(", ", popup.Matches.Select(m => $"{m.Name}:{m.Line + 1}:{m.Column + 1}").Order(StringComparer.Ordinal))));
        }
        window.Close();
        return [.. searches];
    }
}
