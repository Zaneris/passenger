using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public void The_options_narrow_the_matches_and_an_open_files_unsaved_text_is_searched()
    {
        MainWindow window = Open();
        // Unsaved: Beta.cs open with another count typed in.
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// one more count\n")]);
        FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
        Search(popup, "count");
        Assert.Equal(4, popup.Matches.Count);
        Assert.DoesNotContain(popup.Matches, m => m.Path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.Contains(popup.Matches, m => m.LineText == "// one more count");
        Assert.Equal("4 matches in 2 files", popup.Status);

        popup.MatchCase = true;
        Search(popup, "Count");
        Assert.Equal(["    void Count() { }"], popup.Matches.Select(m => m.LineText));
        popup.MatchCase = false;
        popup.Regex = true;
        Search(popup, @"c\w+t\(");
        Assert.Single(popup.Matches);
        Search(popup, "count(");
        Assert.Empty(popup.Matches);
        string? why = FileSearch.Problem(popup.Query);
        Assert.True(why is { Length: > 0 }, "A regex that does not read is not said to be wrong");
        Assert.Equal(why, popup.Status);
        window.Close();
    }

    [AvaloniaFact]
    public void Choosing_a_match_goes_to_it_and_replace_all_changes_open_and_closed_files()
    {
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Beta.cs"));
        Ui.Settle();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Words = true;
        Search(popup, "count");
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
        Search(popup, "int total");
        Assert.Single(popup.Matches);
        Ui.Press(window, "ENTER");
        Assert.Equal("Alpha.cs", window.EditorArea.Active?.Name);
        Selection chosen = window.EditorArea.CodeEditor.Selections.Primary;
        Assert.Equal("int total", window.EditorArea.CodeEditor.Document.Snapshot.GetText(chosen.Start, chosen.Length));
        window.Close();
    }

    [AvaloniaFact]
    public void Replace_all_with_a_character_a_closed_8_bit_file_cannot_hold_writes_it_as_UTF_8_not_a_question_mark()
    {
        string path = Path.Combine(folder, "Gamma.cs");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("// Prix: 5 francs, déjà payés\nclass Gamma { }\n"));
        MainWindow window = Open();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        Search(popup, "francs");
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

    [AvaloniaFact]
    public void A_UTF_16_file_is_found_and_replaced_in_as_the_editor_reads_it_and_a_binary_file_is_still_passed_over()
    {
        string little = Path.Combine(folder, "Wide.cs");
        string big = Path.Combine(folder, "WideBig.cs");
        File.WriteAllText(little, "class Wide\n{\n    int count;\n}\n", System.Text.Encoding.Unicode);
        File.WriteAllText(big, "// a count kept big-endian\n", System.Text.Encoding.BigEndianUnicode);
        File.WriteAllBytes(Path.Combine(folder, "Blob.bin"), [.. System.Text.Encoding.ASCII.GetBytes("count"), 0, 1, 2]);
        MainWindow window = Open();
        FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
        popup.Words = true;
        Search(popup, "count");
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
        Search(popup, "count");
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
        Ui.WaitFor(() => replacing.IsCompleted, "replace_in_files' answer");
        Assert.Contains(replacing.Result, new[] { "Alpha.cs, Beta.cs", "Beta.cs, Alpha.cs" }.Select(files => $"Replaced 3 matches in 2 files: {files}; not replaced: Aardvark.cs (read-only)"));
        Assert.Equal("class Alpha\n{\n    int sum;\n}\n", File.ReadAllText(Path.Combine(folder, "Alpha.cs")));
        Assert.Contains("void sum()", File.ReadAllText(Path.Combine(folder, "Beta.cs")), StringComparison.Ordinal);
        Assert.Equal("class Aardvark { int count; }\n", File.ReadAllText(locked));
        window.Close();
    }

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
        Search(popup, "count");
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
        Search(popup, @"(\w+)(?=\()");
        Assert.Equal(2, popup.Matches.Count);

        popup.Replacement = "$1_";
        popup.RequestReplace();
        Ui.WaitFor(() => File.ReadAllText(calls) != "// catalog cat hotdog dog\nfoo(1); bar (2);\n", "Calls.cs to be written");
        Assert.Equal("// catalog cat hotdog dog\nfoo_(1); bar (2);\n", File.ReadAllText(calls));
        Assert.Contains("    void Count_() { }", beta.Text.Snapshot.ToString(), StringComparison.Ordinal);

        popup.Words = true;
        Search(popup, "cat|dog");
        Assert.True(popup.Matches.Count == 2, $"cat|dog with Words found {popup.Matches.Count}: a word's part (catalog, hotdog) was taken for a whole word");
        popup.Replacement = "pet";
        popup.RequestReplace();
        Ui.WaitFor(() => File.ReadAllText(calls).StartsWith("// catalog pet", StringComparison.Ordinal), "Calls.cs to be written again");
        Assert.Equal("// catalog pet hotdog pet\nfoo_(1); bar (2);\n", File.ReadAllText(calls));
        window.Close();
    }

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
        Search(popup, "^");
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

    [AvaloniaFact]
    public void A_matched_lines_arrow_is_one_ligature_only_while_Settings_enables_ligatures()
    {
        File.WriteAllText(Path.Combine(folder, "Gamma.cs"), "class Gamma\n{\n    int Twice(int x) => x * 2;\n}\n");
        MainWindow window = Open();
        foreach (bool ligatures in (bool[])[false, true])
        {
            window.ApplySettings(window.Settings with { Ligatures = ligatures });
            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Search(popup, "Twice");
            Ui.Settle();
            TextBlock line = popup.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Inlines is { Count: > 0 } runs && runs.Text?.Contains("=>", StringComparison.Ordinal) == true);
            Assert.True(PlainArrow(line) != ligatures,
                ligatures ? "With ligatures enabled, a matched line's => is drawn as = and >, where the editor joins them" : "With ligatures off, a matched line's => is drawn as one arrow, where the editor draws = and >");
            Ui.Press(window, "Escape");
        }
        window.Close();
    }

    /// <summary>The Explorer dims what Find in Files, Go to File and the watcher pass over, by one list: each of its
    /// names, and a packages folder NuGet restored into, in packages.config's layout or the global folder's; Git's
    /// store is not listed at all. A JavaScript monorepo's packages of source are searched, offered and shown as
    /// code.</summary>
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
            Search(popup, "needle");
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

    /// <summary>Whether a line draws = and > as the font's own glyphs for them, not a ligature's.</summary>
    private static bool PlainArrow(TextBlock line)
    {
        ShapedTextRun[] runs = [.. line.TextLayout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>()];
        GlyphTypeface font = runs[0].GlyphRun.GlyphTypeface;
        HashSet<ushort> drawn = [.. runs.SelectMany(r => r.ShapedBuffer).Select(g => g.GlyphIndex)];
        return drawn.Contains(font.CharacterToGlyphMap['=']) && drawn.Contains(font.CharacterToGlyphMap['>']);
    }

    private static void Search(FindInFilesPopup popup, string text)
    {
        popup.Text = text;
        Task searching = popup.SearchAsync();
        Ui.WaitFor(() => searching.IsCompleted, $"the search for {text}");
    }

    private MainWindow Open() => Ui.OpenFolder(folder);
}
