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

    /// <summary>Keys typed at the top of a file of 20,000 lines with the find bar open allocate on the UI thread under
    /// twice what they do in one of 200, the match at the end moving with them and still counted.</summary>
    [AvaloniaFact]
    public void Typing_with_the_find_bar_open_costs_a_key_in_twenty_thousand_lines_about_what_it_does_in_two_hundred()
    {
        long few = KeyWithFindOpen(200);
        long many = KeyWithFindOpen(20_000);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key allocated {few:N0} bytes above 200 lines, {many:N0} above 20,000.");
        Assert.True(many < few * 2,
            $"With the find bar open a key above 20,000 lines allocated {many / 1024:N0} KB on the UI thread, above 200 {few / 1024:N0} KB: the whole file was searched there");
    }

    /// <summary>What a key typed at the top of a file of some lines, with the find bar open on a word at its end, allocates on
    /// the UI thread, its search landing included.</summary>
    private long KeyWithFindOpen(int lines)
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < lines; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"line {i} of the notes\n");
        }
        string notes = text.Append("the needle\n").ToString();
        (MainWindow window, CodeEditor editor) = Open(notes);
        Ui.Press(window, Ui.Shortcut(AppActions.Find));
        window.KeyTextInput("needle");
        Ui.Settle();
        FindBar bar = window.EditorArea.FindBar;
        Assert.Equal("1/1", bar.CountText);
        editor.Selections = SelectionSet.Single(0);
        editor.Focus();
        Ui.Settle();
        const int Keys = 5;
        long bytes = 0;
        for (int i = 0; i < Keys; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput("x");
            Ui.Settle();
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        int needle = notes.IndexOf("needle", StringComparison.Ordinal) + Keys;
        Assert.Equal(new TextRange(needle, "needle".Length), Assert.Single(editor.Highlights));
        Assert.True(bar.IsVisible && bar.CountText.EndsWith("/1", StringComparison.Ordinal), $"After the keys the bar's count says {bar.CountText}");
        window.Close();
        return bytes / Keys;
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
