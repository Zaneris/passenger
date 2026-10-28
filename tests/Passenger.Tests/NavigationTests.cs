using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Getting to a file or a line from the keyboard, through the window.</summary>
public sealed class NavigationTests : IDisposable
{
    private readonly string folder = TestFolders.Create("nav-");

    public void Dispose() => TestFolders.Delete(folder);

    [Gate]
    [AvaloniaFact]
    public void Go_to_file_finds_a_file_by_its_camel_humps_and_enter_opens_it()
    {
        Write("src/Editor/CodeEditor.cs", "class CodeEditor { }");
        Write("src/Editor/Commands.cs", "class Commands { }");
        Write("notes/Changes.txt", "nothing");
        MainWindow window = Open();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        Assert.True(window.IsPopupOpen);
        window.KeyTextInput("CE");
        Ui.Settle();
        Ui.Press(window, "ENTER");

        Assert.False(window.IsPopupOpen);
        Assert.Equal("CodeEditor.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    /// <summary>Page Down and Page Up in Go to File's field choose a page of results below or above: the first Page Down
    /// chooses the last row in sight without scrolling, the next keeps the row it left in sight, and Page Up comes back the
    /// same way, stopping at the top.</summary>
    [Gate]
    [AvaloniaFact]
    public void Page_down_and_up_in_go_to_file_choose_a_page_of_results_keeping_the_choice_in_sight()
    {
        for (int i = 0; i < 60; i++)
        {
            Write($"src/Item{i:00}.cs", "class Item { }");
        }
        MainWindow window = Open();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        window.KeyTextInput("Item");
        Ui.Settle();
        Passenger.App.Navigation.GoToFilePopup popup = Assert.IsType<Passenger.App.Navigation.GoToFilePopup>(window.Popup);
        ListBox results = popup.GetVisualDescendants().OfType<ListBox>().First();
        Assert.Equal(0, results.SelectedIndex);

        Ui.Press(window, "PAGE_DOWN");
        int page = results.SelectedIndex;
        Assert.True(page > 1 && InSight(results, 0) && InSight(results, page),
            $"Page Down chose row {page}, where it chooses the last of the rows in sight and scrolls nothing");
        Ui.Press(window, "PAGE_DOWN");
        Assert.True(results.SelectedIndex == 2 * page && InSight(results, page) && InSight(results, 2 * page),
            $"A second Page Down chose row {results.SelectedIndex}, where it moves a page more ({2 * page}) keeping the row it left in sight");
        Ui.Press(window, "PAGE_UP");
        Ui.Press(window, "PAGE_UP");
        Ui.Press(window, "PAGE_UP");
        Assert.True(results.SelectedIndex == 0 && InSight(results, 0), $"Page Up three times chose row {results.SelectedIndex}, where it stops at the first");
        Ui.Press(window, "DOWN");
        Assert.Equal(1, results.SelectedIndex);
        Ui.Press(window, "ENTER");
        Assert.Equal("Item01.cs", window.EditorArea.Active?.Name);
        window.Close();

        static bool InSight(ListBox list, int index) =>
            list.ContainerFromIndex(index) is Control row && row.TranslatePoint(default, list) is { } top
            && top.Y >= -0.5 && top.Y + row.Bounds.Height <= list.Bounds.Height + 0.5;
    }

    /// <summary>Go to File over a large folder costs a key little more than over a small one: a file whose name cannot match
    /// is passed over with nothing done for it, and the first letter, which a great many match, keeps only the best of them as
    /// it goes. What the window allocates is the count, not a time a busy machine stretches: each key over 20,000 files
    /// allocates on the UI thread less than 8 bytes for each file more than over 2,000, the least of three tries of it.</summary>
    [Gate]
    [AvaloniaFact]
    public void Go_to_file_in_twenty_thousand_files_does_no_work_for_a_file_whose_name_cannot_match()
    {
        string small = TestFolders.Create("nav-small-");
        Files(small, 10);
        Files(folder, 100);
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        (long fewFirst, long few) = KeyInGoToFile(window, small, 2000);
        Ui.Press(window, "ESCAPE");
        (long manyFirst, long many) = KeyInGoToFile(window, folder, 20_000);
        TestContext.Current.TestOutputHelper?.WriteLine($"The key allocated {few / 1024} KB over 2,000 files, {many / 1024} KB over 20,000; the first letter {fewFirst / 1024} KB and {manyFirst / 1024} KB");
        Assert.True(many - few < 8 * 18_000,
            $"A key in Go to File over 20,000 files allocated {many / 1024} KB on the UI thread, against {few / 1024} KB over 2,000: 8 bytes or more for each file more");
        Assert.True(manyFirst - fewFirst < 8 * 18_000,
            $"The first letter in Go to File over 20,000 files allocated {manyFirst / 1024} KB on the UI thread, against {fewFirst / 1024} KB over 2,000: every match was sorted");
        Ui.Press(window, "ENTER");
        Assert.Matches(@"^Validator7x\d+\.cs$", window.EditorArea.Active?.Name ?? string.Empty);
        window.Close();
    }

    /// <summary>A query with a slash in Go to File matches along each file's whole path, '/' and '\' alike, copying no
    /// path or query for it: over 20,000 files a key of either allocates on the UI thread under 8 bytes for each file more
    /// than over 2,000, the least of three tries, and both slashes offer the same files in the same order.</summary>
    [Gate]
    [AvaloniaFact]
    public void Go_to_file_with_a_slash_in_twenty_thousand_files_copies_nothing_for_a_file_and_takes_either_slash()
    {
        string small = TestFolders.Create("nav-small-");
        Files(small, 10);
        Files(folder, 100);
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        (long fewSlash, long fewBackslash, string[] fewOffered) = SlashedKeys(window, small, 2000);
        (long manySlash, long manyBackslash, string[] manyOffered) = SlashedKeys(window, folder, 20_000);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key after / allocated {fewSlash / 1024} KB over 2,000 files, {manySlash / 1024} KB over 20,000; after \\ {fewBackslash / 1024} KB and {manyBackslash / 1024} KB");
        Assert.True(manySlash - fewSlash < 8 * 18_000,
            $"A key after a / in Go to File over 20,000 files allocated {manySlash / 1024} KB on the UI thread, against {fewSlash / 1024} KB over 2,000: 8 bytes or more for each file more");
        Assert.True(manyBackslash - fewBackslash < 8 * 18_000,
            $"A key after a \\ in Go to File over 20,000 files allocated {manyBackslash / 1024} KB on the UI thread, against {fewBackslash / 1024} KB over 2,000: the query or each path was copied for each file");
        Assert.True(fewOffered.Length > 0 && manyOffered.Length > 0, "Go to File offered nothing for Features/Validator2x1");
        window.Close();
    }

    /// <summary>What a key typed after a query with a / and after the same with a \ allocates in Go to File over a folder,
    /// the least of three tries of each, a full list of the folder's validators offered after it either way; and what a
    /// longer query offers, which must be the same with either slash.</summary>
    private static (long Slash, long Backslash, string[] Offered) SlashedKeys(MainWindow window, string root, int files)
    {
        window.OpenFolder(root);
        Ui.WaitFor(() => window.FileIndex.Count >= files, $"the folder's {files} files indexed");
        long[] keys = new long[2];
        string[][] offered = new string[2][];
        foreach ((int at, string slash) in (ReadOnlySpan<(int, string)>)[(0, "/"), (1, "\\")])
        {
            Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
            window.KeyTextInput($"Features{slash}Vali");
            keys[at] = Least(window, "d");
            window.KeyTextInput("ator2x1");
            Ui.Settle();
            offered[at] = [.. Assert.IsType<Passenger.App.Navigation.GoToFilePopup>(window.Popup).Rows.Select(hit => hit.RelativePath)];
            Ui.Press(window, "ESCAPE");
        }
        Assert.True(offered[0].SequenceEqual(offered[1]),
            $"Go to File offers [{string.Join(", ", offered[0].Take(5))}…] for Features/Validator2x1, and [{string.Join(", ", offered[1].Take(5))}…] for Features\\Validator2x1");
        return (keys[0], keys[1], offered[0]);
    }

    /// <summary>Areas of 200 empty files each, of eight kinds, in two folders by turns.</summary>
    private static void Files(string root, int areas)
    {
        string[] kinds = ["Controller", "Service", "Repository", "ViewModel", "Handler", "Validator", "Mapper", "Options"];
        for (int area = 0; area < areas; area++)
        {
            string dir = Path.Combine(root, $"Area{area}", area % 2 == 0 ? "Features" : "Infrastructure");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 200; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"{kinds[i % kinds.Length]}{area}x{i}.cs"), string.Empty);
            }
        }
    }

    /// <summary>What the first letter of a query in Go to File (which three kinds of file in eight match) and the key that
    /// narrows its list to a few of a folder's validators allocate on the UI thread, the folder opened and its files
    /// indexed; the popup is left open on it.</summary>
    private static (long First, long Narrowing) KeyInGoToFile(MainWindow window, string root, int files)
    {
        window.OpenFolder(root);
        Ui.WaitFor(() => window.FileIndex.Count >= files, $"the folder's {files} files indexed");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        Assert.True(window.IsPopupOpen);
        long first = Least(window, "V");
        window.KeyTextInput("alAr");
        long key = Least(window, "7");
        Ui.Settle();
        return (first, key);
    }

    /// <summary>The least a key typed in Go to File's field allocates on the UI thread of three times, each taken back after:
    /// what else lands on that thread meanwhile (a frame the key's own wait draws) goes into one look, while what the key
    /// costs for each file goes into every one.</summary>
    private static long Least(MainWindow window, string key)
    {
        long least = long.MaxValue;
        for (int time = 0; time < 3; time++)
        {
            Ui.Settle();
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput(key);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            Ui.Settle();
            if (time < 2)
            {
                Ui.Press(window, "BACK_SPACE");
            }
        }
        return least;
    }

    [Gate]
    [AvaloniaFact]
    public void Go_to_file_with_a_line_after_the_name_opens_the_file_at_that_line_and_column()
    {
        Write("src/Editor/CodeEditor.cs", string.Join('\n', Enumerable.Range(1, 30).Select(i => $"// line {i} of the editor")));
        Write("src/Editor/Commands.cs", "class Commands { }");
        MainWindow window = Open();

        // A stack trace's "CodeEditor.cs:20" or "CodeEditor.cs:20:4", pasted or typed, finds the file and its place.
        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        window.KeyTextInput("CodeEditor.cs:20");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, "Go to File found nothing for a file's name with a line after it");
        Assert.Equal("CodeEditor.cs", window.EditorArea.Active?.Name);
        Assert.Equal(new TextPosition(19, 0), window.EditorArea.CodeEditor.CaretPosition);

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        window.KeyTextInput("codeed:7:4");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.Equal(new TextPosition(6, 3), window.EditorArea.CodeEditor.CaretPosition);

        Ui.Press(window, Ui.Shortcut(AppActions.GoToEverything));
        window.KeyTextInput("CodeEditor.cs:12");
        Ui.WaitFor(() => window.Popup is Passenger.App.Navigation.SymbolPopup { IsSettled: true } popup && popup.Rows.Count > 0, "Go to Everything's file");
        Ui.Press(window, "ENTER");
        Assert.Equal(new TextPosition(11, 0), window.EditorArea.CodeEditor.CaretPosition);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Go_to_file_with_nothing_typed_offers_the_files_opened_before_this_one()
    {
        Write("First.cs", "class First { }");
        Write("Second.cs", "class Second { }");
        MainWindow window = Open();
        window.OpenFile("First.cs");
        window.OpenFile("Second.cs");
        Ui.Settle();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        Ui.Press(window, "ENTER");

        Assert.Equal("First.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Go_to_line_takes_a_line_and_column_counted_from_one()
    {
        Write("Lines.txt", string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line number {i}")));
        MainWindow window = Open();
        window.OpenFile("Lines.txt");
        Ui.Settle();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToLine));
        window.KeyTextInput("25:6");
        Ui.Settle();
        Ui.Press(window, "ENTER");

        Assert.False(window.IsPopupOpen);
        Assert.Equal(new TextPosition(24, 5), window.EditorArea.CodeEditor.CaretPosition);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Going_to_a_line_of_a_file_that_fits_the_view_keeps_its_first_line_shown_and_to_a_long_files_end_shows_its_last_at_the_foot()
    {
        Write("Long.txt", string.Join('\n', Enumerable.Range(1, 300).Select(i => $"line number {i}")));
        MainWindow window = Open();
        window.OpenFile("Long.txt");
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        GoToLine(window, "299");
        double foot = 300 * editor.LineHeight - editor.Bounds.Height;
        Assert.True(Math.Abs(editor.ScrollOffset.Y - foot) < 1,
            $"Going to line 299 of 300 scrolled to {editor.ScrollOffset.Y / editor.LineHeight:0.#} lines down, not to the last line at the view's foot ({foot / editor.LineHeight:0.#}), leaving the view empty below the file's end");

        // A file a line shorter than the view holds, gone to near its end.
        int fits = (int)(editor.Bounds.Height / editor.LineHeight) - 1;
        Write("Short.txt", string.Join('\n', Enumerable.Range(1, fits).Select(i => $"line number {i}")));
        window.OpenFile("Short.txt");
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        GoToLine(window, (fits - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(editor.ScrollOffset.Y == 0,
            $"Going to line {fits - 1} of a file of {fits}, which fits the view, scrolled its first {editor.ScrollOffset.Y / editor.LineHeight:0.#} lines out of sight");
        window.Close();
    }

    private static void GoToLine(MainWindow window, string line)
    {
        Ui.Press(window, Ui.Shortcut(AppActions.GoToLine));
        window.KeyTextInput(line);
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.Settle();
    }

    [Gate]
    [AvaloniaFact]
    public void The_explorers_shortcut_gives_it_the_keys_on_the_open_files_row()
    {
        Write("One.cs", "class One { }");
        Write("Two.cs", "class Two { }");
        MainWindow window = Open();
        window.OpenFile("Two.cs");
        Ui.Settle();
        Assert.True(window.EditorArea.CodeEditor.IsFocused);

        Ui.Run(window, AppActions.FocusExplorer);
        Assert.False(window.EditorArea.CodeEditor.IsFocused, "The Explorer's shortcut left the keys with the editor");
        Ui.Press(window, "UP");
        Assert.Equal("One.cs", window.ExplorerView.Selected?.Name);
        Ui.Press(window, "ENTER");
        Assert.Equal("One.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void The_explorer_chooses_the_file_shown_as_select_next_tab_or_a_click_on_a_tab_shows_another()
    {
        Write("One.cs", "class One { }");
        Write("Two.cs", "class Two { }");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        window.OpenFile("Two.cs");
        Ui.Settle();
        Assert.Equal("Two.cs", window.ExplorerView.Selected?.Name);

        Ui.Run(window, AppActions.NextTab);
        Assert.Equal("One.cs", window.EditorArea.Active?.Name);
        Assert.True(window.ExplorerView.Selected?.Name == "One.cs", $"Select Next Tab showed One.cs and left the Explorer on {window.ExplorerView.Selected?.Name}");

        Control two = window.EditorArea.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Two.cs" && t.Classes.Contains("tabName"));
        Ui.Click(two);
        Assert.Equal("Two.cs", window.EditorArea.Active?.Name);
        Assert.True(window.ExplorerView.Selected?.Name == "Two.cs", $"A click on Two.cs's tab left the Explorer on {window.ExplorerView.Selected?.Name}");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Focus_editor_gives_the_keys_back_to_the_file_shown()
    {
        Write("One.cs", "class One { }");
        MainWindow window = Open();
        window.OpenFile("One.cs");
        Ui.Settle();
        Ui.Run(window, AppActions.FocusExplorer);
        Assert.False(window.EditorArea.CodeEditor.IsFocused);

        // As ide_action runs it, or a shortcut the person gives it.
        Assert.True(window.Perform(AppActions.FocusEditor));
        Ui.Settle();
        Assert.True(window.EditorArea.CodeEditor.IsFocused, "Focus Editor left the keys in the Explorer");
        window.KeyTextInput("sealed ");
        Assert.Equal("sealed class One { }", window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        window.Close();
    }

    /// <summary>Back and Forward step through the files opened one after another from the Explorer, each at the caret it
    /// was left at, as they do through the places a navigation left.</summary>
    [AvaloniaFact]
    public void Back_and_forward_step_through_the_files_opened_from_the_explorer_each_at_its_caret()
    {
        WriteFiles("A.cs", "B.cs", "C.cs");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        FromExplorer(window, "A.cs");
        Down(window, 2);
        FromExplorer(window, "B.cs");
        Down(window, 5);
        FromExplorer(window, "C.cs");
        Down(window, 7);

        Step(window, AppActions.Back, "B.cs", 5, "Back from C.cs, opened from the Explorer after B.cs,");
        Step(window, AppActions.Back, "A.cs", 2, "A second Back");
        Step(window, AppActions.Forward, "B.cs", 5, "Forward after two Backs");
        Step(window, AppActions.Forward, "C.cs", 7, "A second Forward");
        window.Close();
    }

    /// <summary>A click on a tab leaves the file shown for Back as a navigation does; a file left again keeps one place
    /// behind, where it was left last.</summary>
    [AvaloniaFact]
    public void Back_and_forward_step_through_the_files_shown_by_a_click_on_their_tabs_each_once()
    {
        WriteFiles("A.cs", "B.cs", "C.cs");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("A.cs");
        window.OpenFile("B.cs");
        window.OpenFile("C.cs");
        Ui.Settle();
        ClickTab(window, "A.cs");
        Down(window, 3);
        ClickTab(window, "B.cs");
        Down(window, 6);

        Step(window, AppActions.Back, "A.cs", 3, "Back after a click on A.cs's tab, then on B.cs's,");
        Step(window, AppActions.Back, "C.cs", 0, "A second Back, to the file A.cs's tab was clicked from,");
        Step(window, AppActions.Back, "B.cs", 0, "A third Back, to B.cs as C.cs opened over it,");
        Step(window, AppActions.Back, "B.cs", 0, "A fourth Back, past A.cs left twice and kept once,");
        Step(window, AppActions.Forward, "C.cs", 0, "Forward after the Backs");
        Step(window, AppActions.Forward, "A.cs", 3, "A second Forward");
        Step(window, AppActions.Forward, "B.cs", 6, "A third Forward");
        window.Close();
    }

    /// <summary>A tab closed puts nothing behind, so Back never opens the file just closed, while a file closed after it
    /// was left for another opens again at its place.</summary>
    [AvaloniaFact]
    public void Back_passes_a_tab_just_closed_by_and_opens_again_a_file_closed_after_it_was_left()
    {
        WriteFiles("A.cs", "B.cs", "C.cs");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("A.cs");
        Ui.Settle();
        Down(window, 4);
        window.OpenFile("B.cs");
        window.OpenFile("C.cs");
        Ui.Settle();
        Ui.Run(window, AppActions.CloseTab);
        Assert.Equal("B.cs", window.EditorArea.Active?.Name);
        Step(window, AppActions.Back, "A.cs", 4, "Back after C.cs's tab closed and showed B.cs");

        ClickTab(window, "B.cs");
        ClickTab(window, "A.cs");
        Ui.Run(window, AppActions.CloseTab);
        Assert.True(window.EditorArea.Active?.Name == "B.cs" && window.EditorArea.Documents.All(d => d.Name != "A.cs"), "Close Tab on A.cs left it open");
        Step(window, AppActions.Back, "A.cs", 4, "Back after A.cs, left for B.cs, was shown again and closed,");
        window.Close();
    }

    /// <summary>A click in the split beside leaves the file of the split worked in for Back.</summary>
    [AvaloniaFact]
    public void Back_returns_to_the_file_left_by_a_click_in_the_split_beside()
    {
        WriteFiles("A.cs", "B.cs");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("A.cs");
        Ui.Settle();
        EditorGroup left = window.EditorArea.ActiveGroup;
        Assert.True(window.Perform(AppActions.SplitRight));
        window.OpenFile("B.cs");
        Ui.Settle();
        Down(window, 5);
        Ui.Click(left.Editor);
        Assert.True(window.EditorArea.ActiveGroup == left && window.EditorArea.Active?.Name == "A.cs", "A click in the left split did not make its A.cs the file worked in");
        int clicked = window.EditorArea.CodeEditor.CaretPosition.Line;

        Step(window, AppActions.Back, "B.cs", 5, "Back after a click in the left split's A.cs");
        Step(window, AppActions.Forward, "A.cs", clicked, "Forward from B.cs");
        window.Close();
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);

    /// <summary>Files of ten numbered lines each, none a comment, so none folds.</summary>
    private void WriteFiles(params string[] names)
    {
        foreach (string name in names)
        {
            Write(name, string.Join('\n', Enumerable.Range(1, 10).Select(i => $"int line{i} = {i};")));
        }
    }

    /// <summary>A file opened by a double-click on its row in the Explorer, the keys then in its editor.</summary>
    private static void FromExplorer(MainWindow window, string name)
    {
        Ui.DoubleClick(Ui.Row(window.ExplorerView, window.ExplorerView.Tree.Rows.First(r => r.Name == name)));
        Assert.True(window.EditorArea.Active?.Name == name, $"A double-click on {name} in the Explorer showed {window.EditorArea.Active?.Name}");
    }

    private static void ClickTab(MainWindow window, string name)
    {
        Ui.Click(window.EditorArea.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name && t.Classes.Contains("tabName")));
        Assert.True(window.EditorArea.Active?.Name == name, $"A click on {name}'s tab showed {window.EditorArea.Active?.Name}");
    }

    /// <summary>The caret moved down some lines by the Down key, in the file shown.</summary>
    private static void Down(MainWindow window, int lines)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.True(editor.IsKeyboardFocusWithin, $"The keys are not in {window.EditorArea.Active?.Name}'s editor");
        int from = editor.CaretPosition.Line;
        for (int i = 0; i < lines; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Assert.True(editor.CaretPosition.Line == from + lines, $"Down {lines} times in {window.EditorArea.Active?.Name} moved its caret from line {from + 1} to {editor.CaretPosition.Line + 1}");
    }

    /// <summary>Back or Forward by its keys, and the file and line it shows.</summary>
    private static void Step(MainWindow window, string action, string file, int line, string what)
    {
        Ui.Press(window, Ui.Shortcut(action));
        TextPosition caret = window.EditorArea.CodeEditor.CaretPosition;
        Assert.True(window.EditorArea.Active?.Name == file && caret.Line == line,
            $"{what} showed {window.EditorArea.Active?.Name} at line {caret.Line + 1}, not {file} at line {line + 1}");
    }

    private MainWindow Open() => Ui.OpenFolder(folder);
}
