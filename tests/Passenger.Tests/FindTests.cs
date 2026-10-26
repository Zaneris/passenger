using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Find and replace through the window, with the platform's own shortcuts.</summary>
public sealed class FindTests : IDisposable
{
    private readonly string folder = TestFolders.Create("find-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Typing_a_query_selects_the_nearest_match_and_enter_and_shift_enter_walk_them()
    {
        (MainWindow window, CodeEditor editor) = Open("alpha beta\nbeta gamma\nbeta");
        Ui.Press(window, Ui.Shortcut(AppActions.Find));
        window.KeyTextInput("beta");
        Ui.Settle();
        Assert.Equal(new Selection(6, 10), editor.Selections.Primary);
        Assert.Equal("1/3", window.EditorArea.FindBar.CountText);

        Ui.Press(window, "ENTER");
        Assert.Equal(new Selection(11, 15), editor.Selections.Primary);
        Assert.Equal("2/3", window.EditorArea.FindBar.CountText);

        Ui.Press(window, "shift ENTER");
        Assert.Equal(new Selection(6, 10), editor.Selections.Primary);

        Ui.Press(window, "ESCAPE");
        Assert.False(window.EditorArea.FindBar.IsVisible);
        Assert.Empty(editor.Highlights);
        window.Close();
    }

    [AvaloniaFact]
    public void Replace_all_changes_every_match_in_one_undoable_step()
    {
        (MainWindow window, CodeEditor editor) = Open("total + total * total");
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        window.KeyTextInput("total");
        Ui.Settle();
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<TextBox>("ReplaceWith")!.Text = "sum";
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Assert.Equal("sum + sum * sum", editor.Document.Snapshot.ToString());
        editor.TryExecute(EditorActions.Undo);
        Assert.Equal("total + total * total", editor.Document.Snapshot.ToString());
        window.Close();
    }

    [AvaloniaFact]
    public void A_regex_sees_a_crlf_files_line_ends_and_stripping_trailing_space_keeps_its_breaks()
    {
        (MainWindow window, CodeEditor editor) = Open("int foo  \r\nfoo bar\t\r\nbar foo\r\nend");
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<Avalonia.Controls.Primitives.ToggleButton>("UseRegex")!.IsChecked = true;
        window.KeyTextInput("foo$");
        Ui.Settle();
        Assert.Equal("1/1", bar.CountText);
        Selection found = editor.Selections.Primary;
        Assert.Equal((2, "foo"), (editor.CaretPosition.Line, editor.Document.Snapshot.GetText(found.Start, found.Length)));

        // The everyday trailing-whitespace strip takes the spaces and tabs, never half of a line break.
        bar.FindControl<TextBox>("Query")!.Text = @"\s+$";
        bar.FindControl<TextBox>("ReplaceWith")!.Text = string.Empty;
        Ui.Settle();
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        window.Perform(AppActions.Save);
        Assert.Equal("int foo\r\nfoo bar\r\nbar foo\r\nend", File.ReadAllText(Path.Combine(folder, "Notes.txt")));
        window.Close();
    }

    [AvaloniaFact]
    public void A_regex_replacement_fills_its_groups_from_the_match_where_it_stands_lookahead_and_all()
    {
        (MainWindow window, CodeEditor editor) = Open("foo(1); bar (2); baz(3);");
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = true;
        window.KeyTextInput(@"(\w+)(?=\()");
        Ui.Settle();
        Assert.Equal("1/2", bar.CountText);
        bar.FindControl<TextBox>("ReplaceWith")!.Text = "$1_";

        Ui.Click(bar.FindControl<Button>("ReplaceButton")!);
        Assert.True(editor.Document.Snapshot.ToString() == "foo_(1); bar (2); baz(3);",
            $"Replace wrote '{editor.Document.Snapshot}': the group was not filled in from the match its lookahead found");
        editor.TryExecute(EditorActions.Undo);

        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Assert.True(editor.Document.Snapshot.ToString() == "foo_(1); bar (2); baz_(3);",
            $"Replace All wrote '{editor.Document.Snapshot}': the groups were not filled in from the matches their lookahead found");
        window.Close();
    }

    [AvaloniaFact]
    public void With_words_on_a_regex_alternation_finds_whole_words_on_both_sides()
    {
        (MainWindow window, CodeEditor editor) = Open("catalog cat hotdog dog");
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = true;
        bar.FindControl<ToggleButton>("WholeWords")!.IsChecked = true;
        window.KeyTextInput("cat|dog");
        Ui.Settle();
        Assert.True(bar.CountText == "1/2", $"The count says {bar.CountText}: cat|dog found a word's part (catalog, hotdog) as a whole word");
        bar.FindControl<TextBox>("ReplaceWith")!.Text = "pet";
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Assert.Equal("catalog pet hotdog pet", editor.Document.Snapshot.ToString());

        // A stray ')' is the person's regex not reading, never a close of the group Words puts it in.
        bar.FindControl<TextBox>("Query")!.Text = "pet)|(dog";
        Ui.Settle();
        Assert.Contains("'pet)|(dog'", bar.CountText, StringComparison.Ordinal);
        Assert.Empty(editor.Highlights);
        window.Close();
    }

    [AvaloniaFact]
    public void Replacing_every_lines_start_comments_each_line_and_leaves_the_end_after_the_last_break_alone()
    {
        (MainWindow window, CodeEditor editor) = Open("one\n\ntwo\n");
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = true;
        window.KeyTextInput("^");
        Ui.Settle();
        // Each line's start, the empty one's too, but not the end after the last break.
        Assert.Equal(("1/3", new Selection(0, 0)), (bar.CountText, editor.Selections.Primary));
        Ui.Press(window, "ENTER");
        Assert.Equal(("2/3", new Selection(4, 4)), (bar.CountText, editor.Selections.Primary));

        bar.FindControl<TextBox>("ReplaceWith")!.Text = "// ";
        Ui.Click(bar.FindControl<Button>("ReplaceButton")!);
        Assert.Equal("one\n// \ntwo\n", editor.Document.Snapshot.ToString());
        Assert.Equal(new Selection(8, 8), editor.Selections.Primary);
        editor.TryExecute(EditorActions.Undo);

        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Assert.Equal("// one\n// \n// two\n", editor.Document.Snapshot.ToString());
        window.Close();
    }

    [AvaloniaFact]
    public void A_regex_that_backtracks_without_end_leaves_the_window_free_stops_saying_so_and_replaces_nothing()
    {
        string text = "ab\n" + new string('a', 3000) + "\n";
        (MainWindow window, CodeEditor editor) = Open(text);
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = true;
        window.KeyTextInput("(a+)+b");
        // Back from the key with the regex still running: the UI thread gave it up after its moment.
        Assert.True(bar.CountText == "Searching…", $"Just after the key the count said '{bar.CountText}', not that the regex is searched off the UI thread");
        Assert.Equal(new Selection(0, 2), editor.Selections.Primary);

        Ui.WaitFor(() => bar.CountText != "Searching…", () => $"the search off the UI thread to end; the count says '{bar.CountText}'");
        Assert.True(bar.CountText == SearchResult.TookTooLong, $"The count says '{bar.CountText}' of a regex that never ends");
        Assert.Equal(new TextRange(0, 2), Assert.Single(editor.Highlights));

        bar.FindControl<TextBox>("ReplaceWith")!.Text = "x";
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Ui.WaitFor(() => bar.CountText != "Searching…", () => $"Replace All's search to end; the count says '{bar.CountText}'");
        Assert.True(bar.CountText.EndsWith("Nothing was replaced.", StringComparison.Ordinal), $"After Replace All the count says '{bar.CountText}'");
        Assert.True(editor.Document.Snapshot.ToString() == text, "Replace All replaced the matches a regex found before it stopped, not all of them");
        window.Close();
    }

    /// <summary>Keys typed at the top of a file of 20,000 lines with the find bar open allocate on the UI thread under
    /// twice what they do in one of 200, the match at the end moving with them and still counted; on every thread, the
    /// search's own included, a key allocates under half the file's size, plain or as a regex over "\r\n" lines, and the
    /// stripe's marks stand, the search finding the match where the key moved it.</summary>
    [AvaloniaFact]
    public void Typing_with_the_find_bar_open_costs_a_key_in_twenty_thousand_lines_about_what_it_does_in_two_hundred()
    {
        List<(string What, long Few, long Many, long All, long File, int Built)> measured = [];
        foreach ((bool regex, string end) in new[] { (false, "\n"), (true, "\r\n") })
        {
            string what = regex ? "a regex over \"\\r\\n\" lines" : "plain text";
            (long few, _, _, _) = KeyWithFindOpen(200, regex, end);
            (long many, long all, long file, int built) = KeyWithFindOpen(20_000, regex, end);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Finding {what}, a key allocated {few:N0} bytes above 200 lines, {many:N0} above 20,000 ({all:N0} on every thread, the file {file:N0}); the stripe's marks were worked out {built} times.");
            measured.Add((what, few, many, all, file, built));
        }
        foreach ((string what, long few, long many, long all, long file, int built) in measured)
        {
            Assert.True(many < few * 2,
                $"With the find bar open on {what} a key above 20,000 lines allocated {many / 1024:N0} KB on the UI thread, above 200 {few / 1024:N0} KB: the whole file was searched there");
            Assert.True(all < file / 2,
                $"With the find bar open on {what} a key above 20,000 lines allocated {all / 1024:N0} KB on every thread, its file {file / 1024:N0} KB: the search copied the file");
            Assert.True(built == 0,
                $"With the find bar open on {what} the stripe's marks were worked out {built} times for keys that moved no line: each search's landing set the match it carried again");
        }
    }

    /// <summary>Each key typed into the query over a file of 20,000 lines allocates on the UI thread what it does over one
    /// of 200, plain over "\n" lines and as a regex over "\r\n" ones: the search reads the text where it lies, or reads it
    /// as lines once for the keys typed over it.</summary>
    [AvaloniaFact]
    public void Typing_a_query_costs_a_key_over_twenty_thousand_lines_what_it_does_over_two_hundred()
    {
        List<(string What, long Few, long Many)> measured = [];
        foreach ((bool regex, string end) in new[] { (false, "\n"), (true, "\r\n") })
        {
            string what = regex ? "a regex over \"\\r\\n\" lines" : "plain text over \"\\n\" lines";
            long few = QueryKey(200, regex, end);
            long many = QueryKey(20_000, regex, end);
            TestContext.Current.TestOutputHelper?.WriteLine($"A key of a query for {what} allocated {few:N0} bytes over 200 lines, {many:N0} over 20,000.");
            measured.Add((what, few, many));
        }
        foreach ((string what, long few, long many) in measured)
        {
            Assert.True(many < few + (16 * 1024),
                $"A key of a query for {what} allocated {many / 1024:N0} KB on the UI thread over 20,000 lines, over 200 {few / 1024:N0} KB: the search copied the file");
        }
    }

    /// <summary>What each key of "needle" after its first, typed into the query over a file of some lines with the word
    /// once at its end, allocates on the UI thread, its search and what it shows included: the first key's search takes
    /// what it reads the file into from the shared pool, once.</summary>
    private long QueryKey(int lines, bool regex, string end)
    {
        string notes = Filler(lines, end);
        (MainWindow window, CodeEditor editor) = Open(notes);
        Ui.Press(window, Ui.Shortcut(AppActions.Find));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = regex;
        window.KeyTextInput("n");
        Ui.Settle();
        string[] keys = ["e", "e", "d", "l", "e"];
        long bytes = 0;
        foreach (string key in keys)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput(key);
            Ui.Settle();
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(bar.CountText == "1/1", $"After typing '{key}' the count says {bar.CountText}");
        }
        Assert.Equal(new TextRange(notes.IndexOf("needle", StringComparison.Ordinal), "needle".Length), Assert.Single(editor.Highlights));
        window.Close();
        return bytes / keys.Length;
    }

    /// <summary>Lines of code without a letter of "needle", and the word once on the last.</summary>
    private static string Filler(int lines, string end)
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < lines; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    var x{i} = {i} * {i};{end}");
        }
        return text.Append("// the needle").Append(end).ToString();
    }

    /// <summary>What a key typed at the top of a file of some lines, unwrapped, with the find bar open on a word at its end,
    /// allocates on the UI thread, its search landing included; the least one allocated on every thread; the file's own
    /// bytes; and how many times the keys had the stripe's marks worked out.</summary>
    private (long Ui, long All, long File, int Built) KeyWithFindOpen(int lines, bool regex, string end)
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < lines; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"line {i} of the notes{end}");
        }
        string notes = text.Append("the needle").Append(end).ToString();
        (MainWindow window, CodeEditor editor) = Open(notes);
        // Unwrapped, as a wrapped line's rows are worked out again with each key.
        editor.Focus();
        Ui.Run(window, AppActions.SoftWrap);
        Assert.False(window.EditorArea.Active!.SoftWrap);
        Ui.Press(window, Ui.Shortcut(AppActions.Find));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = regex;
        window.KeyTextInput("needle");
        Ui.Settle();
        Assert.Equal("1/1", bar.CountText);
        editor.Selections = SelectionSet.Single(0);
        editor.Focus();
        Ui.Settle();
        int built = editor.StripeMarksBuilt;
        const int Keys = 5;
        long bytes = 0;
        long least = long.MaxValue;
        for (int i = 0; i < Keys; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long all = GC.GetTotalAllocatedBytes(precise: true);
            window.KeyTextInput("x");
            Ui.Settle();
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - all);
        }
        int needle = notes.IndexOf("needle", StringComparison.Ordinal) + Keys;
        Assert.Equal(new TextRange(needle, "needle".Length), Assert.Single(editor.Highlights));
        Assert.True(bar.IsVisible && bar.CountText.EndsWith("/1", StringComparison.Ordinal), $"After the keys the bar's count says {bar.CountText}");
        built = editor.StripeMarksBuilt - built;
        window.Close();
        return (bytes / Keys, least, notes.Length * sizeof(char), built);
    }

    private (MainWindow Window, CodeEditor Editor) Open(string text)
    {
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), text);
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Notes.txt");
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(0);
        return (window, editor);
    }
}
