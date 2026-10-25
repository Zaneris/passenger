using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>View, Active Editor, through the window: Soft-Wrap (a Markdown or text file opens wrapped, as Settings'
/// masks say), each row from a word, the line's later rows from its indent, its number beside its first row only, and
/// every key, click and mark finding the row it is on; Show Whitespaces' dots and arrows; and Increase, Decrease and
/// Reset Font Size and Cmd+wheel (Ctrl+wheel elsewhere) changing the font Settings keeps.</summary>
public sealed class ActiveEditorTests : ActiveEditorFolders
{
    [AvaloniaFact]
    public void A_Markdown_file_opens_wrapped_at_the_editors_width_each_row_from_a_word_and_its_number_by_its_first_row_only()
    {
        MainWindow window = Open("notes.md", Notes);
        CodeEditor editor = window.EditorArea.CodeEditor;
        TextSnapshot text = editor.Document.Snapshot;
        LineSpan line = text.GetLine(2);
        double width = CharWidth(editor, line.Start);
        double edge = TextEdge(editor);

        List<int> starts = RowStarts(editor, line);
        Assert.True(starts.Count >= 2, $"A line of {line.Length} characters in an editor {editor.Bounds.Width:F0} wide took {starts.Count + 1} rows.");
        int wrapRows = Enumerable.Range(0, text.LineCount).Sum(l => RowStarts(editor, text.GetLine(l)).Count);
        Assert.True(editor.RowsShown == text.LineCount + wrapRows, $"The text shows {editor.RowsShown} rows for its {text.LineCount} lines and their {wrapRows} wraps.");
        foreach (int start in starts)
        {
            Assert.True(text[start - 1] == ' ' && text[start] == 'w', $"A row starts at {start}, inside a word.");
            Assert.True(Math.Abs(At(editor, start).X - At(editor, line.Start).X) < 0.5, "A row after the first starts away from the left edge of a line with no indent.");
            double end = At(editor, start - 1).X;
            Assert.True(end <= edge - width, $"A row's text ends at {end:F0}, past the view's text edge at {edge:F0}.");
            Assert.True(end + (7 * width) > edge - (2 * width), $"A row ends at {end:F0} with room for another word before {edge:F0}.");
        }

        LineSpan indented = text.GetLine(4);
        List<int> indentedStarts = RowStarts(editor, indented);
        Assert.NotEmpty(indentedStarts);
        Assert.All(indentedStarts, start => Assert.Equal(At(editor, indented.Start + 4).X, At(editor, start).X, 1));

        LineSpan run = text.GetLine(6);
        List<int> runStarts = RowStarts(editor, run);
        Assert.True(runStarts.Count >= 2, "A run of 400 letters did not wrap.");
        Assert.Equal(run.Start + "        a ".Length, runStarts[0]);
        foreach (int start in runStarts.Append(run.End))
        {
            double end = At(editor, start - 1).X + width;
            Assert.True(end <= edge - width, $"A row of the unbroken run ends at {end:F0}, past the view's text edge at {edge:F0}.");
        }
        Assert.All(runStarts, start => Assert.Equal(At(editor, run.Start + 8).X, At(editor, start).X, 1));

        LineSpan accented = text.GetLine(7);
        List<int> accentedStarts = RowStarts(editor, accented);
        Assert.True(accentedStarts.Count >= 2, "A line of sixty accented words did not wrap.");
        foreach (int start in accentedStarts)
        {
            Assert.True(text[start - 1] == ' ' && text[start] == 'c', $"A row of the accented line starts at {start}, inside a word.");
            Assert.True(At(editor, start - 1).X <= edge - width, $"A row of the accented line ends at {At(editor, start - 1).X:F0}, past the view's edge at {edge:F0}.");
        }

        Color gutter = Token(window, TokenKeys.Editor_Gutter);
        Assert.True(GutterBeside(window, editor, line.Start).Any(c => c != gutter), "The wrapped line's first row has no number beside it.");
        Assert.Equal([gutter], GutterBeside(window, editor, starts[0]));

        editor.Focus();
        Ui.Run(window, AppActions.SoftWrap);
        Assert.False(window.EditorArea.Active!.SoftWrap);
        Assert.True(At(editor, line.End).Y == At(editor, line.Start).Y, "Soft-Wrap turned off still wraps the line.");
        Assert.Equal(text.LineCount, editor.RowsShown);
        Ui.Run(window, AppActions.SoftWrap);
        Assert.Equal(starts, RowStarts(editor, line));
        window.Close();
    }

    [AvaloniaFact]
    public void The_files_Settings_names_are_the_ones_that_open_wrapped()
    {
        string code = "class Long\n{\n    // " + Words + "\n}\n";
        File.WriteAllText(Path.Combine(folder, "Long.cs"), code);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), Words + "\n");
        MainWindow window = Open("notes.md", Notes);
        window.OpenFile(Path.Combine(folder, "notes.txt"));
        window.OpenFile(Path.Combine(folder, "Long.cs"));
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument csharp = window.EditorArea.Active!;
        Passenger.App.Workspace.OpenDocument notes = window.EditorArea.Documents.Single(d => d.Name == "notes.md");
        Assert.True(window.EditorArea.Documents.Single(d => d.Name == "notes.txt").SoftWrap, "A .txt file opened unwrapped.");
        Assert.True(notes.SoftWrap, "A .md file opened unwrapped.");
        Assert.False(csharp.SoftWrap, "A .cs file opened wrapped, which Rider's masks leave out.");
        CodeEditor editor = window.EditorArea.CodeEditor;
        LineSpan comment = editor.Document.Snapshot.GetLine(2);
        Assert.Equal(At(editor, comment.Start).Y, At(editor, comment.End).Y);

        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "soft-wrap";
        Ui.Settle();
        Assert.Contains("Soft-wrap these files", dialog.Shown);
        dialog.Change(s => s with { SoftWrapFiles = "*.cs" });
        dialog.Ok();
        Ui.Settle();
        Assert.True(csharp.SoftWrap, "Settings' masks naming *.cs left the open C# file unwrapped.");
        Assert.False(notes.SoftWrap, "Settings' masks no longer naming *.md left the open Markdown file wrapped.");
        Assert.True(At(editor, comment.End).Y > At(editor, comment.Start).Y, "The C# file's long line did not wrap.");
        window.Close();
    }

    [AvaloniaFact]
    public void Down_and_Up_go_by_rows_End_and_Home_by_IntelliJs_rules_and_typing_where_a_row_wraps_keeps_the_caret_after_it()
    {
        MainWindow window = Open("notes.md", Notes);
        CodeEditor editor = window.EditorArea.CodeEditor;
        LineSpan line = editor.Document.Snapshot.GetLine(2);
        List<int> starts = RowStarts(editor, line);
        double height = editor.LineHeight;
        double width = CharWidth(editor, line.Start);
        double top = At(editor, line.Start).Y;
        editor.GoTo(new TextPosition(2, 3));
        editor.Focus();
        Ui.Settle();

        Ui.Press(window, "DOWN");
        Assert.Equal(2, editor.CaretPosition.Line);
        Assert.Equal(top + height, Caret(editor).Y, 1);
        Assert.Equal(At(editor, line.Start + 3).X, Caret(editor).X, 1);
        Ui.Press(window, "DOWN");
        Assert.Equal(top + (2 * height), Caret(editor).Y, 1);
        Ui.Press(window, "UP");
        Ui.Press(window, "UP");
        Assert.Equal(line.Start + 3, editor.Selections.Primary.Caret);

        Ui.Press(window, "END");
        Assert.Equal(starts[0], editor.Selections.Primary.Caret);
        Assert.True(Caret(editor).Y == top, "End on a wrapped line's first row left the caret off that row.");
        Assert.Equal(At(editor, starts[0] - 1).X + width, Caret(editor).X, 1);
        Ui.Press(window, "END");
        Assert.Equal(starts[1], editor.Selections.Primary.Caret);
        Assert.Equal(top + height, Caret(editor).Y, 1);
        Ui.Press(window, "HOME");
        Assert.Equal(starts[0], editor.Selections.Primary.Caret);
        Assert.Equal(top + height, Caret(editor).Y, 1);
        Ui.Press(window, "HOME");
        Assert.Equal(line.Start, editor.Selections.Primary.Caret);

        editor.Selections = SelectionSet.Single(starts[0] - 1);
        Ui.Press(window, "RIGHT");
        Assert.Equal(starts[0], editor.Selections.Primary.Caret);
        Assert.Equal(top, Caret(editor).Y, 1);
        Ui.Press(window, "RIGHT");
        Assert.Equal(starts[0], editor.Selections.Primary.Caret);
        Assert.Equal(top + height, Caret(editor).Y, 1);
        Assert.Equal(At(editor, line.Start).X, Caret(editor).X, 1);
        Ui.Press(window, "RIGHT");
        Assert.Equal(starts[0] + 1, editor.Selections.Primary.Caret);
        Ui.Press(window, "LEFT");
        Ui.Press(window, "LEFT");
        Assert.Equal(starts[0], editor.Selections.Primary.Caret);
        Assert.Equal(top, Caret(editor).Y, 1);

        Ui.Type(window, "X", "X");
        Assert.Equal('X', editor.Document.Snapshot[starts[0]]);
        Assert.Equal(starts[0] + 1, editor.Selections.Primary.Caret);
        Assert.Equal(At(editor, starts[0]).Y, Caret(editor).Y, 1);
        Assert.Equal(At(editor, starts[0]).X + width, Caret(editor).X, 1);
        editor.Selections = SelectionSet.Single(line.Start);
        Ui.Press(window, "END");
        int wrap = editor.Selections.Primary.Caret;
        Ui.Type(window, "SPACE", " ");
        Assert.Equal(wrap + 1, editor.Selections.Primary.Caret);
        Assert.True(Caret(editor).Y == top, "The caret after a space typed at a row's end went to the next row.");
        Assert.Equal(At(editor, wrap).X + width, Caret(editor).X, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void A_click_on_a_wrapped_row_puts_the_caret_on_the_character_clicked_and_one_past_a_rows_end_at_that_end()
    {
        MainWindow window = Open("notes.md", Notes);
        CodeEditor editor = window.EditorArea.CodeEditor;
        LineSpan line = editor.Document.Snapshot.GetLine(2);
        List<int> starts = RowStarts(editor, line);
        double top = At(editor, line.Start).Y;

        int target = starts[1] + 3;
        Click(window, editor, editor.PointOf(target) + new Point(1, editor.LineHeight / 2));
        Assert.Equal(target, editor.Selections.Primary.Caret);

        Click(window, editor, new Point(TextEdge(editor) - 4, editor.PointOf(line.Start).Y + (editor.LineHeight / 2)));
        Assert.Equal(starts[0], editor.Selections.Primary.Caret);
        Assert.Equal(top, Caret(editor).Y, 1);

        Point word = editor.PointOf(starts[0] + 2) + new Point(1, editor.LineHeight / 2);
        Point inWindow = editor.TranslatePoint(word, window)!.Value;
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        Ui.Settle();
        Assert.Equal(new Selection(starts[0], starts[0] + 6), editor.Selections.Primary with { PreferredX = double.NaN });
        window.Close();
    }

    [AvaloniaFact]
    public void A_narrower_window_wraps_the_text_again_at_its_width_and_keeps_the_line_at_the_top()
    {
        StringBuilder text = new();
        for (int i = 0; i < 40; i++)
        {
            text.Append($"Paragraph {i}: ").Append(Words).Append('\n');
        }
        MainWindow window = Open("long.md", text.ToString());
        CodeEditor editor = window.EditorArea.CodeEditor;
        TextSnapshot snapshot = editor.Document.Snapshot;
        int rows = editor.RowsShown;
        LineSpan top = snapshot.GetLine(20);
        editor.ScrollTo(new Vector(0, At(editor, top.Start).Y));
        Ui.Settle();
        Assert.Equal(0, editor.PointOf(top.Start).Y, 1);

        window.Width -= 300;
        Ui.Settle();
        Assert.True(editor.RowsShown > rows, $"The narrower window shows {editor.RowsShown} rows, as many as the wider one's {rows}.");
        double edge = TextEdge(editor);
        double width = CharWidth(editor, top.Start);
        foreach (int start in RowStarts(editor, top))
        {
            Assert.True(At(editor, start - 1).X <= edge - width, $"After the resize a row's text ends at {At(editor, start - 1).X:F0}, past the view's edge at {edge:F0}.");
        }
        Assert.True(Math.Abs(editor.PointOf(top.Start).Y) < editor.LineHeight, $"The line at the top went to {editor.PointOf(top.Start).Y:F0} as the text wrapped again.");
        window.Close();
    }

    [AvaloniaFact]
    public void Folding_and_a_breakpoint_stay_on_their_lines_with_wrapped_lines_above_and_among_them()
    {
        string code = "class Shapes\n{\n    // " + Words + "\n    int Area(int w, int h)\n    {\n        int area = "
            + string.Join(" + ", Enumerable.Range(0, 30).Select(i => $"w * h * {i}")) + ";\n        return area;\n    }\n}\n";
        MainWindow window = Open("Shapes.cs", code);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => editor.FoldRegions.Count == 2, "the file's regions");
        editor.Focus();
        Ui.Run(window, AppActions.SoftWrap);
        TextSnapshot text = editor.Document.Snapshot;
        LineSpan comment = text.GetLine(2);
        LineSpan sum = text.GetLine(5);
        int commentRows = RowStarts(editor, comment).Count + 1;
        Assert.True(commentRows > 1, "The comment did not wrap.");
        Assert.Equal(At(editor, comment.Start).Y + (commentRows * editor.LineHeight), At(editor, text.GetLine(3).Start).Y, 1);

        editor.GoTo(new TextPosition(5, 8));
        editor.Focus();
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.WaitFor(() => editor.LineMarks.Any(m => m.Line == 5), "the breakpoint");
        List<int> sumStarts = RowStarts(editor, sum);
        Assert.NotEmpty(sumStarts);
        Color dot = Token(window, TokenKeys.Editor_Breakpoint);
        Assert.True(GutterBeside(window, editor, sum.Start).Contains(dot), "No breakpoint is drawn beside its line's first row.");
        Assert.False(GutterBeside(window, editor, sumStarts[0]).Contains(dot), "A breakpoint is drawn beside its line's second row too.");
        Point tinted = editor.TranslatePoint(new Point(TextEdge(editor) - 3, editor.PointOf(sumStarts[0]).Y + 2), window)!.Value;
        Assert.Equal(Token(window, TokenKeys.Editor_BreakpointLine), Ui.Pixel(window, tinted));

        // Rows shown: the six lines outside the fold and the comment's rows after its first.
        Point chevron = editor.TranslatePoint(editor.GutterColumnPoint(GutterColumn.Folding, 3), window)!.Value;
        window.MouseDown(chevron, MouseButton.Left);
        window.MouseUp(chevron, MouseButton.Left);
        Ui.Settle();
        FoldRegion folded = Assert.Single(editor.FoldRegions, r => r.Folded);
        Assert.Equal(3, text.GetLineFromOffset(folded.Start));
        Assert.Equal(At(editor, text.GetLine(3).Start).Y + editor.LineHeight, At(editor, text.GetLine(8).Start).Y, 1);
        Assert.Equal(6 + commentRows - 1, editor.RowsShown);
        Point brace = editor.TranslatePoint(editor.PointOf(text.GetLine(8).Start) + new Point(2, editor.LineHeight / 2), window)!.Value;
        window.MouseDown(brace, MouseButton.Left);
        window.MouseUp(brace, MouseButton.Left);
        Ui.Settle();
        Assert.Equal(8, editor.CaretPosition.Line);

        double secondRow = editor.PointOf(RowStarts(editor, comment)[0]).Y + (editor.LineHeight / 2);
        Point beside = editor.TranslatePoint(new Point(editor.GutterColumnPoint(GutterColumn.Folding, 2).X, secondRow), window)!.Value;
        window.MouseDown(beside, MouseButton.Left);
        window.MouseUp(beside, MouseButton.Left);
        Ui.Settle();
        Assert.Single(editor.FoldRegions, r => r.Folded);

        window.MouseDown(chevron, MouseButton.Left);
        window.MouseUp(chevron, MouseButton.Left);
        Ui.Settle();
        Assert.DoesNotContain(editor.FoldRegions, r => r.Folded);
        Assert.True(GutterBeside(window, editor, sum.Start).Contains(dot), "The breakpoint left its line's first row as the method unfolded.");
        window.Close();
    }

    [AvaloniaFact]
    public void Show_Whitespaces_draws_a_dot_in_each_space_and_an_arrow_across_each_tab_in_their_own_colour()
    {
        MainWindow window = Open("Tabs.cs", "class C\n{\n\tint  x = 1;\n}\n");
        CodeEditor editor = window.EditorArea.CodeEditor;
        TextSnapshot text = editor.Document.Snapshot;
        int tab = text.GetLineStart(2);
        int space = tab + "\tint".Length;
        Color background = Token(window, TokenKeys.Editor_Background);
        Color whitespace = Token(window, TokenKeys.Editor_Whitespace);
        Assert.Equal(background, Ui.Pixel(window, SpaceDot(window, editor, space)));
        Assert.Equal(background, Ui.Pixel(window, TabShaft(window, editor, tab)));

        editor.Focus();
        Ui.Run(window, AppActions.ShowWhitespaces);
        Assert.True(window.EditorArea.Active!.ShowWhitespaces);
        Assert.Equal(whitespace, Ui.Pixel(window, SpaceDot(window, editor, space)));
        Assert.Equal(whitespace, Ui.Pixel(window, SpaceDot(window, editor, space + 1)));
        Assert.Equal(whitespace, Ui.Pixel(window, TabShaft(window, editor, tab)));

        Ui.Run(window, AppActions.ShowWhitespaces);
        Assert.Equal(background, Ui.Pixel(window, SpaceDot(window, editor, space)));
        window.Close();
    }

    [AvaloniaFact]
    public void The_font_size_actions_and_the_zoom_wheel_change_the_editors_font_and_the_next_window_opens_on_it()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepSettings();
        window.OpenFolder(folder);
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(13, editor.FontSize);

        MenuEntry view = MainMenu.Build(window.Actions.All).Single(m => m.Title == "View");
        MenuEntry active = view.Items!.Single(m => m.Title == "Active Editor");
        Assert.Equal([AppActions.SoftWrap, null, AppActions.ShowWhitespaces, null, AppActions.IncreaseFontSize, AppActions.DecreaseFontSize, AppActions.ResetFontSize],
            active.Items!.Select(i => i.Action));

        Assert.True(window.RunFromMenu(AppActions.IncreaseFontSize));
        Assert.Equal(14, editor.FontSize);
        Assert.True(window.RunFromMenu(AppActions.DecreaseFontSize));
        Assert.True(window.RunFromMenu(AppActions.DecreaseFontSize));
        Assert.Equal(12, editor.FontSize);
        Assert.True(window.RunFromMenu(AppActions.ResetFontSize));
        Assert.Equal(13, editor.FontSize);

        // Until Settings turns it on, the wheel with Cmd (Ctrl elsewhere) leaves the font as it is.
        Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
        RawInputModifiers zoom = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.MouseWheel(over, new Vector(0, 1), zoom);
        Ui.Settle();
        Assert.Equal(13, editor.FontSize);
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "mouse wheel";
        Ui.Settle();
        Assert.Equal([OperatingSystem.IsMacOS() ? "Change font size with Command+Mouse Wheel" : "Change font size with Ctrl+Mouse Wheel"], dialog.Shown);
        dialog.Change(s => s with { WheelZoom = true });
        dialog.Ok();
        Ui.Settle();
        for (int notch = 0; notch < 3; notch++)
        {
            window.MouseWheel(over, new Vector(0, 1), zoom);
        }
        window.MouseWheel(over, new Vector(0, -1), zoom);
        Ui.Settle();
        Assert.Equal(15, editor.FontSize);
        Assert.Equal(15, window.Settings.EditorFontSize);
        window.Close();

        MainWindow again = new() { Width = 1200, Height = 800, StateFolder = settings };
        again.Show();
        again.KeepSettings();
        Ui.Settle();
        Assert.Equal(15, again.Settings.EditorFontSize);
        Assert.Equal(15, again.EditorArea.CodeEditor.FontSize);
        again.Close();
    }
}

/// <summary>A key in a long soft-wrapped file costs the line it changed, never a wrap of the whole file, and about what it
/// costs with soft wrap off. Held to times, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class ActiveEditorResponsivenessTests : ActiveEditorFolders
{
    /// <summary>Twenty thousand lines of prose, each wrapping onto rows: a key costs the line it changed, never a wrap of the
    /// whole file, and about what it costs with soft wrap off.</summary>
    [AvaloniaFact]
    public void A_key_in_twenty_thousand_soft_wrapped_lines_wraps_only_the_line_it_changed()
    {
        StringBuilder source = new();
        for (int i = 0; i < 20_000; i++)
        {
            source.Append("Line ").Append(i).Append(": ").Append(Words, 0, 140 + (i % 7 * 20)).Append('\n');
        }
        EditorRig rig = new(source.ToString());
        string text = rig.Text;
        int middle = rig.Document.Snapshot.GetLineStart(10_000) + 40;
        int line = rig.Document.Snapshot.GetLine(10_000).Length;

        List<double> plainTimes = [];
        List<double> wrappedTimes = [];
        List<double> turnOn = [];
        long wrapped = 0;
        for (int round = 0; round < 3; round++)
        {
            rig.Editor.SoftWrap = false;
            Ui.Settle();
            Keys(rig, middle, plainTimes);
            long on = Stopwatch.GetTimestamp();
            rig.Editor.SoftWrap = true;
            Ui.Settle();
            turnOn.Add(Stopwatch.GetElapsedTime(on).TotalMilliseconds);
            Assert.True(rig.Editor.RowsShown > 40_000, $"Twenty thousand lines of 150 to 270 characters wrapped into only {rig.Editor.RowsShown} rows.");
            long before = rig.Editor.CharactersWrapped;
            Keys(rig, middle, wrappedTimes);
            wrapped = rig.Editor.CharactersWrapped - before;
        }
        Assert.True(text == rig.Text, "The timed keys left the text changed.");

        // Six edits of a line of about two hundred characters: the whole file would be four million.
        Assert.True(wrapped <= 6 * (line + 2), $"Twelve keys, six of them edits of a line of {line} characters, wrapped {wrapped} characters.");
        double plain = Median(plainTimes);
        double soft = Median(wrappedTimes);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key in 20,000 lines ({rig.Editor.RowsShown} rows): median {soft:F3} ms soft-wrapped, {plain:F3} ms not, "
            + $"slowest {wrappedTimes.Max():F3} ms and {plainTimes.Max():F3} ms; twelve keys wrapped {wrapped} characters; "
            + $"turning soft wrap on took {Median(turnOn):F1} ms.");
        Assert.True(soft <= (3 * plain) + 1, $"A key took {soft:F2} ms soft-wrapped, against {plain:F2} ms with soft wrap off.");
    }

    /// <summary>With soft wrap on, a key in an open C# file (which has folds) of 2,000 members, a wrapped comment over
    /// each, wraps the line it changed and no more, as in a file of 20, and allocates on the UI thread what a key there
    /// does, give or take 16 KB.</summary>
    [AvaloniaFact]
    public void A_key_in_an_open_soft_wrapped_file_with_folds_wraps_only_its_line_however_long_the_file()
    {
        (long smallWrapped, long smallBytes, _) = SoftWrappedKeys("Few.cs", 20);
        (long largeWrapped, long largeBytes, long wrapAll) = SoftWrappedKeys("Many.cs", 2000);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key wrapped {smallWrapped} characters in 20 members and {largeWrapped} in 2,000, allocating {smallBytes / 1024:N0} KB and {largeBytes / 1024:N0} KB on the UI thread; wrapping the larger file's lines allocates {wrapAll / 1024:N0} KB.");
        Assert.True(largeWrapped <= smallWrapped, $"A key in 2,000 members wrapped {largeWrapped} characters, more than the {smallWrapped} it wraps in 20: it wrapped the file again");
        Assert.True(largeBytes <= smallBytes + (16 * 1024),
            $"A key in 2,000 members allocated {largeBytes / 1024:N0} KB on the UI thread, {(largeBytes - smallBytes) / 1024:N0} KB more than in 20: it copied what every line or region has");
    }

    /// <summary>A frame of lines with hints, a parameter's name before each argument, allocates on the UI thread about
    /// what a frame of the same lines naming their arguments in their text does.</summary>
    [AvaloniaFact]
    public void A_frame_of_hinted_lines_allocates_about_what_one_of_the_same_lines_naming_their_arguments_does()
    {
        MainWindow window = new() { Width = 1100, Height = 800, StartsLanguageServer = false };
        window.Show();
        (long named, _) = Frames(named: true);
        (long hinted, string hintedFile) = Frames(named: false);
        TestContext.Current.TestOutputHelper?.WriteLine($"A frame of 60 calls allocated {named / 1024} KB on the UI thread naming their arguments, {hinted / 1024} KB with them as hints");
        Assert.True(hinted <= named + (32 * 1024),
            $"A frame of 60 calls with two hints each allocated {hinted / 1024} KB on the UI thread, against {named / 1024} KB for the same calls naming their arguments: each line's hints were made again, or its text drawn once for each piece");
        window.Close();

        // The least of five frames' UI-thread allocations over 60 calls, naming their arguments or hinted.
        (long Bytes, string File) Frames(bool named)
        {
            StringBuilder text = new("static class Calls\n{\n    public static int Of(int width, int height) => width * height;\n\n    public static void Run()\n    {\n");
            for (int i = 0; i < 60; i++)
            {
                text.Append(named ? FormattableString.Invariant($"        Of(width: {i}, height: {i + 1});\n") : FormattableString.Invariant($"        Of({i}, {i + 1});\n"));
            }
            string source = text.Append("    }\n}\n").ToString();
            string path = Path.Combine(folder, named ? "Named.cs" : "Hinted.cs");
            File.WriteAllText(path, source);
            window.OpenFile(path);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            if (!named)
            {
                List<InlayHint> hints = [];
                for (int i = 0; i < 60; i++)
                {
                    int call = source.IndexOf($"Of({i}, ", StringComparison.Ordinal);
                    hints.Add(new InlayHint(call + 3, "width:", PadRight: true));
                    hints.Add(new InlayHint(source.IndexOf(", ", call, StringComparison.Ordinal) + 2, "height:", PadRight: true));
                }
                editor.SetInlayHints(hints);
                Ui.Settle();
                Assert.Equal(120, editor.InlayHints.Count);
            }
            long least = long.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                editor.InvalidateVisual();
                long before = GC.GetAllocatedBytesForCurrentThread();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return (least, path);
        }
    }
}

/// <summary>The folders an Active Editor test writes its files and keeps its settings in, its texts, and the helpers that
/// open, click and measure them.</summary>
public abstract class ActiveEditorFolders : IDisposable
{
    /// <summary>Sixty words of six letters, a space between each: a line that wraps in any editor this wide.</summary>
    protected static readonly string Words = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"word{i:00}"));

    protected static readonly string Notes = "# Notes\n\n" + Words + "\n\n    " + Words + "\nlast\n        a " + new string('x', 400) + "\n"
        + string.Join(' ', Enumerable.Range(0, 60).Select(i => $"café{i:00}")) + "\n";

    protected readonly string folder = TestFolders.Create("active-editor-");
    protected readonly string settings = TestFolders.Create("active-editor-settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>What a key typed at the top of an open C# file of members, each under a comment that wraps, costs with soft
    /// wrap on: the characters it wrapped and the bytes it allocated on the UI thread, its parse landing included, each key's
    /// average; and the bytes turning soft wrap on allocated, which wraps every line.</summary>
    protected (long Wrapped, long Bytes, long WrapAll) SoftWrappedKeys(string name, int members)
    {
        StringBuilder text = new("class Many\n{\n    int Typed()\n    {\n        int value = 1;\n        return value;\n    }\n\n");
        for (int i = 0; i < members; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    // {Words}\n    int Member{i}()\n    {{\n        return {i};\n    }}\n\n");
        }
        text.Append("}\n");
        string source = text.ToString();
        MainWindow window = Open(name, source);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.FoldsFound && document.FoldRegions.Count > members, "the file's regions");
        editor.Selections = SelectionSet.Single(source.IndexOf("= 1;", StringComparison.Ordinal) + 2);
        editor.Focus();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Ui.Run(window, AppActions.SoftWrap);
        long wrapAll = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(document.SoftWrap && editor.RowsShown > source.Count(c => c == '\n') + members, "Soft wrap did not wrap the comments.");
        const int Keys = 10;
        long wrapped = editor.CharactersWrapped;
        long bytes = 0;
        for (int i = 0; i < Keys; i++)
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput("1");
            Ui.Settle();
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        wrapped = editor.CharactersWrapped - wrapped;
        Assert.Contains("int value = 11111111111;", document.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
        return (wrapped / Keys, bytes / Keys, wrapAll);
    }

    /// <summary>Keys typing, deleting and moving through rows, which leave the text as it was: six edits of the caret's line
    /// and six moves.</summary>
    protected static readonly string[] Timed = ["x", "y", "LEFT", "RIGHT", "DOWN", "UP", "BACK_SPACE", "BACK_SPACE", "meta RIGHT", "meta LEFT", "z", "BACK_SPACE"];

    /// <summary>The timed keys at an offset, each timed.</summary>
    protected static void Keys(EditorRig rig, int offset, List<double> times)
    {
        rig.PlaceCaret(offset);
        Ui.Settle();
        foreach (string key in Timed)
        {
            long at = Stopwatch.GetTimestamp();
            if (key.Length == 1)
            {
                rig.Type(key);
            }
            else
            {
                rig.Press(key);
            }
            times.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
        }
    }

    protected static double Median(List<double> times)
    {
        List<double> sorted = [.. times.Order()];
        return sorted[sorted.Count / 2];
    }

    protected MainWindow Open(string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        MainWindow window = Ui.OpenFolder(folder, width: 1100);
        window.OpenFile(path);
        Ui.Settle();
        return window;
    }

    /// <summary>Where an offset's caret is drawn in the content, the scroll counted out: its row's top and its x.</summary>
    protected static Point At(CodeEditor editor, int offset) => editor.PointOf(offset) + editor.ScrollOffset;

    protected static Point Caret(CodeEditor editor) => At(editor, editor.Selections.Primary.Caret);

    protected static double CharWidth(CodeEditor editor, int offset) => At(editor, offset + 1).X - At(editor, offset).X;

    /// <summary>The offsets a line's rows after its first start at: where a character is drawn a row below the one before.
    /// Read with no caret on the line, whose place there would be the end of the row before.</summary>
    protected static List<int> RowStarts(CodeEditor editor, LineSpan line)
    {
        List<int> starts = [];
        for (int offset = line.Start + 1; offset < line.End; offset++)
        {
            if (At(editor, offset).Y > At(editor, offset - 1).Y)
            {
                starts.Add(offset);
            }
        }
        return starts;
    }

    /// <summary>The right edge of the view's text, where the vertical scrollbar begins.</summary>
    protected static double TextEdge(CodeEditor editor) =>
        editor.GetVisualChildren().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical).Bounds.Left;

    protected static void Click(MainWindow window, CodeEditor editor, Point inEditor)
    {
        Point at = editor.TranslatePoint(inEditor, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }

    /// <summary>The middle of a space's dot, as Show Whitespaces draws it, in the window.</summary>
    protected static Point SpaceDot(MainWindow window, CodeEditor editor, int offset)
    {
        double middle = Math.Floor(editor.PointOf(offset).Y + (editor.LineHeight / 2));
        double x = Math.Round(((editor.PointOf(offset).X + editor.PointOf(offset + 1).X) / 2) - 1);
        return editor.TranslatePoint(new Point(x + 0.5, middle - 0.5), window)!.Value;
    }

    /// <summary>A point on a tab's arrow's shaft, halfway across the tab.</summary>
    protected static Point TabShaft(MainWindow window, CodeEditor editor, int offset)
    {
        double middle = Math.Floor(editor.PointOf(offset).Y + (editor.LineHeight / 2));
        double x = (editor.PointOf(offset).X + editor.PointOf(offset + 1).X) / 2;
        return editor.TranslatePoint(new Point(Math.Floor(x) + 0.5, middle + 0.5), window)!.Value;
    }

    /// <summary>The colours drawn in the gutter beside the row an offset is on, from the gutter's left to the change bars:
    /// its ground alone where nothing is drawn there.</summary>
    protected static HashSet<Color> GutterBeside(MainWindow window, CodeEditor editor, int offset)
    {
        double top = editor.PointOf(offset).Y;
        double right = editor.GutterColumnPoint(GutterColumn.Changes, 0).X - 3;
        using Avalonia.Media.Imaging.WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The window has drawn no frame.");
        Point from = editor.TranslatePoint(new Point(1, top + 2), window)!.Value;
        PixelRect area = new(PixelPoint.FromPoint(from, window.RenderScaling), PixelSize.FromSize(new Size(right - 1, editor.LineHeight - 4), window.RenderScaling));
        byte[] pixels = new byte[area.Width * area.Height * 4];
        GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(area, pinned.AddrOfPinnedObject(), pixels.Length, area.Width * 4);
        }
        finally
        {
            pinned.Free();
        }
        bool rgba = frame.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        HashSet<Color> colours = [];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            colours.Add(rgba ? Color.FromRgb(pixels[i], pixels[i + 1], pixels[i + 2]) : Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]));
        }
        return colours;
    }

    protected static Color Token(MainWindow window, string key) =>
        Application.Current!.TryFindResource(key, window.ActualThemeVariant, out object? brush) && brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"The {window.ActualThemeVariant} theme has no {key}");
}
