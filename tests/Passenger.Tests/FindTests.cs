using Avalonia;
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
[AboutMacOS]
public sealed class FindTests : IDisposable
{
    private readonly string folder = TestFolders.Create("find-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The find bar, the gate's checks as one flow: a query chooses the nearest match and Enter and
    /// Shift+Enter walk them, Replace All is one step of Undo, a regex reads a CRLF file's breaks and fills its groups
    /// where each match stands, whole words take an alternation whole, every line's start is replaced, a regex that
    /// never ends leaves the window free, and a key costs the same over twenty thousand lines as two hundred.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_find_bar_finds_walks_and_replaces_as_written_never_holding_the_window()
    {
        Flow.StepApart<FindTests>("typing a query selects the nearest match and enter and Shift+Enter walk them", t => t.Typing_a_query_selects_the_nearest_match_and_enter_and_shift_enter_walk_them());
        Flow.StepApart<FindTests>("Replace All changes every match in one undoable step", t => t.Replace_all_changes_every_match_in_one_undoable_step());
        Flow.StepApart<FindTests>("a regex sees a CRLF file's line ends and stripping trailing space keeps its breaks", t => t.A_regex_sees_a_crlf_files_line_ends_and_stripping_trailing_space_keeps_its_breaks());
        Flow.StepApart<FindTests>("a regex replacement fills its groups from the match where it stands lookahead and all", t => t.A_regex_replacement_fills_its_groups_from_the_match_where_it_stands_lookahead_and_all());
        Flow.StepApart<FindTests>("with words on a regex alternation finds whole words on both sides", t => t.With_words_on_a_regex_alternation_finds_whole_words_on_both_sides());
        Flow.StepApart<FindTests>("replacing every line's start comments each line and leaves the end after the last break alone", t => t.Replacing_every_lines_start_comments_each_line_and_leaves_the_end_after_the_last_break_alone());
        Flow.StepApart<FindTests>("a regex that backtracks without end leaves the window free stops saying so and replaces nothing", t => t.A_regex_that_backtracks_without_end_leaves_the_window_free_stops_saying_so_and_replaces_nothing());
        Flow.StepApart<FindTests>("Replace All taking the whole text or what stands around each match gives what dotnet gives", t => t.Replace_all_taking_the_whole_text_or_what_stands_around_each_match_gives_what_dotnet_gives());
        Flow.StepApart<FindTests>("typing a query costs a key over twenty thousand lines what it does over two hundred", t => t.Typing_a_query_costs_a_key_over_twenty_thousand_lines_what_it_does_over_two_hundred());
    }

    /// <summary>Replace All says how many it replaced until the text or the search changes, a screen reader hearing it,
    /// Shift+Enter puts a line break in the replacement written as the file's own, a Replace All that would make the text
    /// too long is refused before any of it is made, and Find Word at Caret, In Selection and Incremental Search do as Rider's
    /// keys have them.</summary>
    [AvaloniaFact]
    public void Replace_all_says_what_it_did_and_never_makes_what_it_cannot_hold()
    {
        Flow.StepApart<FindTests>("Replace All says how many it replaced until the text or the search changes and a screen reader hears it", t => t.Replace_all_says_how_many_it_replaced_until_the_text_or_the_search_changes_and_a_screen_reader_hears_it());
        Flow.StepApart<FindTests>("Shift+Enter puts a line break in the replacement and a regex reads backslash n each written as the file's own", t => t.Shift_enter_puts_a_line_break_in_the_replacement_and_a_regex_reads_backslash_n_each_written_as_the_files_own());
        Flow.StepApart<FindTests>("Replace All that would make the text too long is refused before any of it is made", t => t.Replace_all_that_would_make_the_text_too_long_is_refused_before_any_of_it_is_made());
        Flow.StepApart<FindTests>("Find Word at Caret walks the word's copies In Selection finds and replaces inside the selection alone and Incremental Search opens empty", t => t.Find_word_at_caret_walks_the_words_copies_in_selection_finds_and_replaces_inside_the_selection_alone_and_incremental_search_opens_empty());
    }

    private void Typing_a_query_selects_the_nearest_match_and_enter_and_shift_enter_walk_them()
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

    /// <summary>Find Word at Caret's keys go to the next copy of the word at the caret and its previous's back, the editor
    /// keeping the keys; Find in Selection keeps the search, and Replace All, inside the line chosen; Incremental Search's
    /// keys open the bar with nothing in it, finding as the query is typed (#2318).</summary>
    private void Find_word_at_caret_walks_the_words_copies_in_selection_finds_and_replaces_inside_the_selection_alone_and_incremental_search_opens_empty()
    {
        const string Text = "one beta two\nbeta three beta\nfour beta";
        (MainWindow window, CodeEditor editor) = Open(Text);
        FindBar bar = window.EditorArea.FindBar;
        editor.Focus();
        editor.Selections = SelectionSet.Single(5);
        Ui.Press(window, Ui.Shortcut(AppActions.FindWordAtCaret));
        Assert.True(editor.Selections.Primary == new Selection(13, 17) && bar.CountText == "2/4" && editor.IsKeyboardFocusWithin,
            $"Find Word at Caret from inside the first beta chose {editor.Selections.Primary}, the bar counting '{bar.CountText}', the editor {(editor.IsKeyboardFocusWithin ? "keeping" : "losing")} the keys, where it goes to the next beta, 2/4, the editor keeping them");
        Ui.Press(window, Ui.Shortcut(AppActions.FindPreviousWordAtCaret));
        Assert.Equal(new Selection(4, 8), editor.Selections.Primary);

        // Replace's keys over the beta chosen, for its row; then the second line chosen for In Selection.
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        editor.Selections = SelectionSet.Single(new Selection(13, 28));
        window.Perform(AppActions.FindInSelection);
        Ui.Settle();
        Assert.True(bar.CountText == "1/2" && editor.Selections.Primary == new Selection(13, 17),
            $"In Selection over the second line left the bar counting '{bar.CountText}' and {editor.Selections.Primary} chosen, where it finds that line's two betas alone, the first chosen");
        bar.FindControl<TextBox>("ReplaceWith")!.Text = "B";
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Assert.Equal("one beta two\nB three B\nfour beta", editor.Document.Snapshot.ToString());
        editor.TryExecute(EditorActions.Undo);
        window.Perform(AppActions.FindInSelection);
        Ui.Settle();
        Assert.True(bar.CountText.EndsWith("/4", StringComparison.Ordinal), $"In Selection turned off left the bar counting '{bar.CountText}', where it finds every beta again");
        Ui.Press(window, "ESCAPE");

        editor.Focus();
        Ui.Press(window, Ui.Shortcut(AppActions.IncrementalSearch));
        TextBox query = bar.FindControl<TextBox>("Query")!;
        Assert.True(bar.IsVisible && query.Text == string.Empty, $"Incremental Search left the bar {(bar.IsVisible ? "open" : "closed")} with '{query.Text}' in it, where it opens empty");
        window.KeyTextInput("four");
        Ui.Settle();
        Assert.Equal(new Selection(29, 33), editor.Selections.Primary);
        window.Close();
    }

    private void Replace_all_changes_every_match_in_one_undoable_step()
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

    /// <summary>What Replace All came to stays in the find bar's count once the window settles, through the search its own
    /// edits bring, and a screen reader hears it; a key typed in the text, or a search changed, puts the count back.</summary>
    private void Replace_all_says_how_many_it_replaced_until_the_text_or_the_search_changes_and_a_screen_reader_hears_it()
    {
        (MainWindow window, CodeEditor editor) = Open("total + total * total");
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        window.KeyTextInput("total");
        Ui.Settle();
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<TextBox>("ReplaceWith")!.Text = "sum";
        List<(string Words, Avalonia.Automation.AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Ui.Settle();
        Assert.True(bar.CountText == "Replaced 3", $"Replace All left the find bar saying '{bar.CountText}' once the window settled, where it says how many it replaced");
        Assert.True(heard.Any(h => h.Words == "Replaced 3"), $"A screen reader heard {AccessibilityTests.Said(heard)} as Replace All ended, not \"Replaced 3\"");

        editor.Focus();
        window.KeyTextInput(" ");
        Ui.Settle();
        Assert.True(bar.CountText == "0 results", $"A key typed in the text left the find bar saying '{bar.CountText}', where it counts the query's matches again");

        editor.TryExecute(EditorActions.Undo);
        editor.TryExecute(EditorActions.Undo);
        Assert.Equal("total + total * total", editor.Document.Snapshot.ToString());
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Ui.Settle();
        Assert.Equal("Replaced 3", bar.CountText);
        bar.FindControl<TextBox>("Query")!.Text = "sum";
        Ui.Settle();
        Assert.True(bar.CountText.EndsWith("/3", StringComparison.Ordinal), $"A query typed after Replace All left the find bar saying '{bar.CountText}', where it counts the new query's 3 matches");
        window.Close();
    }

    /// <summary>Shift+Enter in the Replace box puts a line break into it, as its tooltip says, where Enter replaces; Replace
    /// All writes it in a "\r\n" file as "\r\n", and with Regex on a \n typed in the box makes one too.</summary>
    private void Shift_enter_puts_a_line_break_in_the_replacement_and_a_regex_reads_backslash_n_each_written_as_the_files_own()
    {
        (MainWindow window, CodeEditor editor) = Open("one, two\r\nthree\r\n");
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        window.KeyTextInput(", ");
        Ui.Settle();
        FindBar bar = window.EditorArea.FindBar;
        TextBox replacing = bar.FindControl<TextBox>("ReplaceWith")!;
        Assert.Contains(OperatingSystem.IsMacOS() ? "⇧⏎ puts a line break in" : "Shift+Enter puts a line break in", ToolTip.GetTip(replacing) as string, StringComparison.Ordinal);
        replacing.Focus();
        window.KeyTextInput(",");
        Ui.Press(window, "shift ENTER");
        Assert.True(editor.Document.Snapshot.ToString() == "one, two\r\nthree\r\n", "Shift+Enter in the Replace box replaced the match, where it puts a line break in");
        window.KeyTextInput("and ");
        Ui.Settle();
        Assert.True(replacing.Text == ",\nand ", $"Shift+Enter in the Replace box left '{replacing.Text}' there, not a line break");
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Assert.Equal("one,\r\nand two\r\nthree\r\n", editor.Document.Snapshot.ToString());
        editor.TryExecute(EditorActions.Undo);

        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = true;
        replacing.Text = @"\n\t";
        Ui.Settle();
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        window.Perform(AppActions.Save);
        Assert.Equal("one\r\n\ttwo\r\nthree\r\n", File.ReadAllText(Path.Combine(folder, "Notes.txt")));
        window.Close();
    }

    private void A_regex_sees_a_crlf_files_line_ends_and_stripping_trailing_space_keeps_its_breaks()
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

    private void A_regex_replacement_fills_its_groups_from_the_match_where_it_stands_lookahead_and_all()
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

    private void With_words_on_a_regex_alternation_finds_whole_words_on_both_sides()
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

    private void Replacing_every_lines_start_comments_each_line_and_leaves_the_end_after_the_last_break_alone()
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

    private void A_regex_that_backtracks_without_end_leaves_the_window_free_stops_saying_so_and_replaces_nothing()
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
        Assert.True(bar.CountText == Refusals.RegexStopped.Shown, $"The count says '{bar.CountText}' of a regex that never ends");
        Assert.Equal(new TextRange(0, 2), Assert.Single(editor.Highlights));

        bar.FindControl<TextBox>("ReplaceWith")!.Text = "x";
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        Ui.WaitFor(() => bar.CountText != "Searching…", () => $"Replace All's search to end; the count says '{bar.CountText}'");
        Assert.True(bar.CountText.EndsWith("Nothing was replaced.", StringComparison.Ordinal), $"After Replace All the count says '{bar.CountText}'");
        Assert.True(editor.Document.Snapshot.ToString() == text, "Replace All replaced the matches a regex found before it stopped, not all of them");
        window.Close();
    }

    /// <summary>Replace All of every word's edge (\b) with the whole text ($_) over a file of 12,000 characters would make
    /// it 144 million long, the text again at each of its 12,000 matches: it is refused before any of it is made, saying
    /// how long, the file left as it was and the click allocating a few times the file's size (most of it the list of its
    /// matches), never the replacement's 288 MB.</summary>
    private void Replace_all_that_would_make_the_text_too_long_is_refused_before_any_of_it_is_made()
    {
        string text = string.Concat(Enumerable.Repeat("a a a a a a a a a a\n", 600));
        (MainWindow window, CodeEditor editor) = Open(text);
        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        FindBar bar = window.EditorArea.FindBar;
        bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = true;
        window.KeyTextInput(@"\b");
        bar.FindControl<TextBox>("ReplaceWith")!.Text = "$_";
        Ui.Settle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Ui.WaitFor(() => bar.CountText != "Searching…", () => $"Replace All's search to end; the count says '{bar.CountText}'");
        Assert.True(bar.CountText == "Nothing was replaced: the text replaced would be 144,012,000 characters long, more than 100,000,000.",
            $"After Replace All of \\b with $_ over 12,000 characters the count says '{bar.CountText}'");
        Assert.True(editor.Document.Snapshot.ToString() == text, "Replace All refused for a text too long still changed it");
        long file = text.Length * sizeof(char);
        TestContext.Current.TestOutputHelper?.WriteLine($"Replace All allocated {allocated:N0} bytes on the UI thread, the file {file:N0}.");
        Assert.True(allocated < file * 32,
            $"Replace All allocated {allocated / 1024:N0} KB on the UI thread refusing a replacement of a {file / 1024:N0} KB file: it made the replacement it refused");
        window.Close();
    }

    /// <summary>Replace All with a regex whose replacement takes the whole text ($_), what stands before and after each
    /// match ($`, $') and its groups, with '$'s that stand for nothing, gives what .NET's Regex.Replace gives; in a file
    /// of "\r\n" lines, with each break it carries over the file's own.</summary>
    private void Replace_all_taking_the_whole_text_or_what_stands_around_each_match_gives_what_dotnet_gives()
    {
        const string Text = "ab cd\nef\n";
        (string Pattern, string Replacement)[] cases =
        [
            (@"\b", "[$_]"),
            (@"(\w)(?<rest>\w*)", "<$`|$'|$+|${rest}$1$$_$9${x}$&$>"),
            (@"d\s", "{$'$`}"),
        ];
        (MainWindow window, CodeEditor editor) = Open(Text);
        File.WriteAllText(Path.Combine(folder, "Crlf.txt"), Text.ReplaceLineEndings("\r\n"));
        foreach (string end in new[] { "\n", "\r\n" })
        {
            if (end == "\r\n")
            {
                window.OpenFile("Crlf.txt");
                Ui.Settle();
                editor = window.EditorArea.CodeEditor;
            }
            Ui.Press(window, Ui.Shortcut(AppActions.Replace));
            FindBar bar = window.EditorArea.FindBar;
            bar.FindControl<ToggleButton>("UseRegex")!.IsChecked = true;
            foreach ((string pattern, string replacement) in cases)
            {
                bar.FindControl<TextBox>("Query")!.Text = pattern;
                bar.FindControl<TextBox>("ReplaceWith")!.Text = replacement;
                Ui.Settle();
                Ui.Click(bar.FindControl<Button>("ReplaceAllButton")!);
                string expected = System.Text.RegularExpressions.Regex.Replace(Text, pattern, replacement).ReplaceLineEndings(end);
                string replaced = editor.Document.Snapshot.ToString();
                Assert.True(replaced == expected,
                    $"Replace All of {pattern} with {replacement} in {(end == "\n" ? "\"\\n\"" : "\"\\r\\n\"")} lines wrote '{replaced}', where .NET writes '{expected}'");
                editor.TryExecute(EditorActions.Undo);
                Assert.Equal(Text.ReplaceLineEndings(end), editor.Document.Snapshot.ToString());
            }
        }
        window.Close();
    }

    /// <summary>Each key typed into the query over a file of 20,000 lines allocates on the UI thread what it does over one
    /// of 200, plain over "\n" lines and as a regex over "\r\n" ones: the search reads the text where it lies, or reads it
    /// as lines once for the keys typed over it.</summary>
    private void Typing_a_query_costs_a_key_over_twenty_thousand_lines_what_it_does_over_two_hundred()
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

    private (MainWindow Window, CodeEditor Editor) Open(string text) => Open(folder, text);

    /// <summary>A window on <paramref name="folder"/> with Notes.txt open, written with <paramref name="text"/>, the caret at
    /// its start.</summary>
    internal static (MainWindow Window, CodeEditor Editor) Open(string folder, string text)
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

/// <summary>What a key typed with the find bar open costs, counted on every thread too: the class runs alone, as whatever a
/// class beside the window tests allocates meanwhile would count as the search's.</summary>
[Collection(Alone.Name)]
public sealed class FindBytesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("find-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Keys typed at the top of a file of 20,000 lines with the find bar open allocate on the UI thread under
    /// twice what they do in one of 200, the match at the end moving with them and still counted; on every thread, the
    /// search's own included, a key allocates under half the file's size, plain or as a regex over "\r\n" lines, with
    /// soft wrap on or off, and the stripe's marks stand, the search finding the match where the key moved it.</summary>
    [Gate]
    [AvaloniaFact]
    public void Typing_with_the_find_bar_open_costs_a_key_in_twenty_thousand_lines_about_what_it_does_in_two_hundred()
    {
        List<(string What, long Few, long Many, long All, long File, int Built)> measured = [];
        foreach ((bool regex, string end, bool wrapped) in new[] { (false, "\n", false), (true, "\r\n", false), (false, "\n", true) })
        {
            string what = (regex ? "a regex over \"\\r\\n\" lines" : "plain text") + (wrapped ? " in wrapped lines" : string.Empty);
            (long few, _, _, _) = KeyWithFindOpen(200, regex, end, wrapped);
            (long many, long all, long file, int built) = KeyWithFindOpen(20_000, regex, end, wrapped);
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

    /// <summary>What a key typed at the top of a file of some lines, wrapped or not, with the find bar open on a word at its
    /// end, allocates on the UI thread, its search landing included; the least one allocated on every thread; the file's own
    /// bytes; and how many times the keys had the stripe's marks worked out.</summary>
    private (long Ui, long All, long File, int Built) KeyWithFindOpen(int lines, bool regex, string end, bool wrapped)
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < lines; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"line {i} of the notes{end}");
        }
        string notes = text.Append("the needle").Append(end).ToString();
        (MainWindow window, CodeEditor editor) = FindTests.Open(folder, notes);
        editor.Focus();
        if (!wrapped)
        {
            Ui.Run(window, AppActions.SoftWrap);
        }
        Assert.Equal(wrapped, window.EditorArea.Active!.SoftWrap);
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
}
