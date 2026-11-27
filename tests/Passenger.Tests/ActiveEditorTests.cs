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
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>View, Active Editor, through the window: Soft-Wrap (a Markdown or text file opens wrapped, as Settings'
/// masks say), each row from a word, the line's later rows from its indent, its number beside its first row only, and
/// every key, click and mark finding the row it is on; and Show Whitespaces' dots and arrows.</summary>
[SameOnEverySystem]
public sealed class ActiveEditorTests : ActiveEditorFolders
{
    /// <summary>Soft wrap: a Markdown file opens wrapped at the editor's width numbered by its first row, Down and Up
    /// go by rows and End and Home by IntelliJ's rules, a click on a wrapped row puts the caret on the character
    /// clicked, a narrower window wraps again keeping the line at the top, and folds and a breakpoint stay on their
    /// lines among wrapped ones; and the rows a host draws above lines, which every key, click and fold passes by.</summary>
    [AvaloniaFact]
    public void Soft_wrap_wraps_at_the_editors_width_and_moves_clicks_and_folds_by_rows()
    {
        Flow.StepApart<ActiveEditorTests>("a Markdown file opens wrapped at the editor's width each row from a word and its number by its first row only", t => t.A_Markdown_file_opens_wrapped_at_the_editors_width_each_row_from_a_word_and_its_number_by_its_first_row_only());
        Flow.StepApart<ActiveEditorTests>("down and Up go by rows End and Home by IntelliJ's rules and typing where a row wraps keeps the caret after it", t => t.Down_and_Up_go_by_rows_End_and_Home_by_IntelliJs_rules_and_typing_where_a_row_wraps_keeps_the_caret_after_it());
        Flow.StepApart<ActiveEditorTests>("a click on a wrapped row puts the caret on the character clicked and one past a row's end at that end", t => t.A_click_on_a_wrapped_row_puts_the_caret_on_the_character_clicked_and_one_past_a_rows_end_at_that_end());
        Flow.StepApart<ActiveEditorTests>("a narrower window wraps the text again at its width and keeps the line at the top", t => t.A_narrower_window_wraps_the_text_again_at_its_width_and_keeps_the_line_at_the_top());
        Flow.StepApart<ActiveEditorTests>("folding and a breakpoint stay on their lines with wrapped lines above and among them", t => t.Folding_and_a_breakpoint_stay_on_their_lines_with_wrapped_lines_above_and_among_them());
        Flow.StepApart<ActiveEditorTests>("a breakpoint preview follows the hovered line number and a click sets the breakpoint", t => t.A_breakpoint_preview_follows_the_hovered_line_number_and_a_click_sets_the_breakpoint());
        Flow.StepApart<ActiveEditorTests>("rows above lines are passed by keys clicks column selections folds Vim and a screen reader and go with their lines", t => t.Rows_above_lines_are_passed_by_keys_clicks_column_selections_folds_Vim_and_a_screen_reader_and_go_with_their_lines());
    }

    private void A_Markdown_file_opens_wrapped_at_the_editors_width_each_row_from_a_word_and_its_number_by_its_first_row_only()
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

    private void Down_and_Up_go_by_rows_End_and_Home_by_IntelliJs_rules_and_typing_where_a_row_wraps_keeps_the_caret_after_it()
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

    private void A_click_on_a_wrapped_row_puts_the_caret_on_the_character_clicked_and_one_past_a_rows_end_at_that_end()
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

    private void A_narrower_window_wraps_the_text_again_at_its_width_and_keeps_the_line_at_the_top()
    {
        StringBuilder text = new();
        for (int i = 0; i < 40; i++)
        {
            text.Append($"Paragraph {i}: ").Append(Words).Append('\n');
        }
        // Plain text, wrapped as Markdown is, has no preview to stand under it and widen the editor as the window narrows.
        MainWindow window = Open("long.txt", text.ToString());
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

    private void Folding_and_a_breakpoint_stay_on_their_lines_with_wrapped_lines_above_and_among_them()
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
        Point chevron = editor.TranslatePoint(editor.FoldPoint(3), window)!.Value;
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
        Point beside = editor.TranslatePoint(new Point(editor.FoldPoint(2).X, secondRow), window)!.Value;
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

    private void A_breakpoint_preview_follows_the_hovered_line_number_and_a_click_sets_the_breakpoint()
    {
        MainWindow window = Open("Counter.cs", "int count = 0;\ncount++;\nConsole.WriteLine(count);\n");
        CodeEditor editor = window.EditorArea.CodeEditor;
        MarkColumn marks = editor.Gutter!.Marks;
        double inset = (MarkColumn.StripWidth / 2) + CharWidth(editor, 0);
        Point first = editor.GutterColumnPoint(marks, 0) - new Point(inset, 0);
        Point second = editor.GutterColumnPoint(marks, 1) - new Point(inset, 0);
        Point firstOnWindow = editor.TranslatePoint(first, window)!.Value;
        Point secondOnWindow = editor.TranslatePoint(second, window)!.Value;
        Color firstBefore = Ui.Pixel(window, firstOnWindow);
        Color secondBefore = Ui.Pixel(window, secondOnWindow);
        Color dot = Token(window, TokenKeys.Editor_Breakpoint);
        Color gutter = Token(window, TokenKeys.Editor_Gutter);

        Ui.Hover(editor, first);
        Color preview = Ui.Pixel(window, firstOnWindow);
        Assert.InRange(Math.Abs(preview.R - ((dot.R + gutter.R) / 2)), 0, 2);
        Assert.InRange(Math.Abs(preview.G - ((dot.G + gutter.G) / 2)), 0, 2);
        Assert.InRange(Math.Abs(preview.B - ((dot.B + gutter.B) / 2)), 0, 2);

        Ui.Hover(editor, second);
        Assert.Equal(firstBefore, Ui.Pixel(window, firstOnWindow));
        Assert.Equal(preview, Ui.Pixel(window, secondOnWindow));
        Assert.Empty(editor.LineMarks);

        Ui.Hover(editor, editor.GutterColumnPoint(marks, 1));
        Assert.Equal(secondBefore, Ui.Pixel(window, secondOnWindow));
        Ui.Hover(editor, second);
        window.MouseMove(new Point(5, 5));
        Ui.Settle();
        Assert.Equal(secondBefore, Ui.Pixel(window, secondOnWindow));

        Ui.Hover(editor, second);
        Ui.Click(editor, second);
        Assert.Equal(1, Assert.Single(editor.LineMarks).Line);
        Assert.Equal(dot, Ui.Pixel(window, secondOnWindow));
        window.Close();
    }

    private void Rows_above_lines_are_passed_by_keys_clicks_column_selections_folds_Vim_and_a_screen_reader_and_go_with_their_lines()
    {
        StringBuilder code = new("class Rows\n{\n");
        for (int i = 0; i < 10; i++)
        {
            code.Append($"    int M{i}()\n    {{\n        return {i};\n    }}\n\n");
        }
        code.Append("}\n");
        EditorRig rig = new(code.ToString());
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        static int Declaration(int i) => 2 + (i * 5);
        RowAbove Above(TextSnapshot of, int i) => new(of.GetLineStart(Declaration(i)) + 4, [new RowAboveWord($"{i} usages", i), new RowAboveWord("Ada Lovelace", -1 - i)]);
        editor.SetRowsAbove([.. Enumerable.Range(0, 10).Select(i => Above(text, i))]);
        Ui.Settle();
        double h = editor.LineHeight;
        Assert.True(editor.RowsShown == text.LineCount + 10, $"The text shows {editor.RowsShown} rows for its {text.LineCount} lines and 10 rows above them.");
        Assert.True(Math.Abs(editor.PointOf(text.GetLineStart(2)).Y - (3 * h)) < 0.5, "M0's declaration is not a row lower for the row above it.");
        Assert.True(Math.Abs(editor.PointOf(text.GetLineStart(7)).Y - (9 * h)) < 0.5, "M1's declaration is not two rows lower for the rows above it and M0.");
        Point word = editor.RowAbovePoint(2, 0, 0) ?? throw new InvalidOperationException("M0's row above is not drawn.");
        Assert.True(Math.Abs(word.Y - (2.5 * h)) < 0.5, $"M0's row above is drawn at {word.Y:F0}, not on the row between the brace and the declaration.");
        Assert.True(word.X > editor.PointOf(text.GetLineStart(2) + 3).X && word.X < editor.PointOf(text.GetLineStart(2) + 12).X,
            "M0's first word does not start where the declaration does.");

        rig.PlaceCaret(text.GetLineStart(1));
        rig.Press("DOWN");
        Assert.True(editor.CaretPosition.Line == 2, $"Down from the brace went to line {editor.CaretPosition.Line + 1}, not the declaration below the row above it.");
        rig.Press("UP");
        Assert.True(editor.CaretPosition.Line == 1, $"Up from the declaration went to line {editor.CaretPosition.Line + 1}, not back past the row above it.");
        rig.PlaceCaret(text.GetLineStart(7) + 4);
        rig.Press("UP");
        Assert.True(editor.CaretPosition == new TextPosition(6, 0), $"Up from M1's declaration went to {editor.CaretPosition}, not the empty line above its row above.");

        RowAbovePress? pressed = null;
        editor.RowAbovePressed += (_, e) => pressed = e;
        HoverEventArgs? rested = null;
        editor.Hovered += (_, e) => rested = e;
        int caret = editor.Selections.Primary.Caret;
        Point onWord = editor.TranslatePoint(editor.RowAbovePoint(7, 0, 1)!.Value, rig.Window)!.Value;
        rig.Window.MouseMove(onWord);
        Ui.WaitFor(() => rested is not null, "a rest over a word of M1's row above");
        Assert.True(rested!.Above is { Data: -2 }, "A rest over M1's second word above it told no word, or another.");
        Assert.True(editor.ShowsHand, "The pointer over a word above a line is no hand.");
        rig.Window.MouseDown(onWord, MouseButton.Left);
        rig.Window.MouseUp(onWord, MouseButton.Left);
        Ui.Settle();
        Assert.True(pressed is { Line: 7, Word.Data: -2 }, $"A press on M1's second word above it told {pressed?.Word.Data ?? "nothing"}.");
        Assert.True(editor.Selections.Primary.Caret == caret, "A press on a word above a line moved the caret.");
        pressed = null;
        Point past = editor.TranslatePoint(new Point(editor.Bounds.Width - 40, editor.RowAbovePoint(7, 0, 0)!.Value.Y), rig.Window)!.Value;
        rig.Window.MouseDown(past, MouseButton.Left);
        rig.Window.MouseUp(past, MouseButton.Left);
        Ui.Settle();
        Assert.True(pressed is null && editor.Selections.Primary.Caret == caret, "A press on a row above a line, past its words, did something.");
        Point beside = editor.TranslatePoint(new Point(4, editor.RowAbovePoint(7, 0, 0)!.Value.Y), rig.Window)!.Value;
        rig.Window.MouseDown(beside, MouseButton.Left);
        rig.Window.MouseUp(beside, MouseButton.Left);
        Ui.Settle();
        Assert.True(editor.Selections.Primary.IsEmpty && editor.Selections.Primary.Caret == caret, "A press on the gutter beside a row above a line selected a line.");

        rig.PlaceCaret(text.GetLineStart(6));
        rig.Press("control shift DOWN");
        Assert.True(editor.Selections.Count == 2 && editor.Selections.Items[1].Caret == text.GetLineStart(7),
            $"A caret cloned below the line above M1's row above went to {editor.Selections.Items[^1].Caret}, not to M1's declaration.");

        TextRange declared = new(text.GetLineStart(7) + 4, 3);
        Rect bounds = Assert.Single(editor.AccessibleText.BoundsOf(declared));
        Assert.True(Math.Abs(bounds.Y - editor.PointOf(declared.Start).Y) < 0.5, $"A screen reader is told M1's declaration stands at {bounds.Y:F0}, not on its row of text.");

        editor.UsesVim = true;
        rig.PlaceCaret(text.GetLineStart(11));
        rig.Press("ESCAPE");
        Ui.Type(rig.Window, "G", "g");
        Ui.Type(rig.Window, "K", "k");
        Assert.True(editor.CaretPosition.Line == 10, $"Vim's gk from the empty line after M1 went to line {editor.CaretPosition.Line + 1}.");
        rig.PlaceCaret(text.GetLineStart(11));
        Ui.Type(rig.Window, "G", "g");
        Ui.Type(rig.Window, "J", "j");
        Assert.True(editor.CaretPosition.Line == 12, $"Vim's gj from the empty line before M2's row above went to line {editor.CaretPosition.Line + 1}, not M2's declaration.");
        editor.UsesVim = false;

        // Folding M1's class-like span hides the rows above the lines it hides, and keeps its own first line's.
        editor.SetFoldRegions([new FoldRegion(text.GetLine(7).End, text.GetLineStart(17) + 4, "{...}", Folded: true)]);
        Ui.Settle();
        Assert.True(editor.RowsShown == text.LineCount - 10 + 8, $"With M2 and M3 folded under M1, the text shows {editor.RowsShown} rows, where their rows above are hidden.");
        Assert.NotNull(editor.RowAbovePoint(7, 0, 0));
        editor.SetFoldRegions([]);
        Ui.Settle();

        // A line broken at a declaration takes its row above down with it; a declaration's line deleted takes its row away.
        rig.PlaceCaret(text.GetLineStart(12) + 4);
        rig.Press("ENTER");
        TextSnapshot broken = rig.Document.Snapshot;
        Assert.True(broken.GetLineFromOffset(editor.RowsAbove[2].Offset) == 13, "A line broken before M2's declaration left its row above on the line above.");
        Assert.True(Math.Abs(editor.PointOf(broken.GetLineStart(13)).Y - editor.PointOf(broken.GetLineStart(12)).Y - (2 * h)) < 0.5,
            "M2's row above is not between the line the break left and M2's declaration.");
        rig.Press("meta Z");
        int rows = editor.RowsShown;
        editor.Selections = SelectionSet.Single(new Selection(rig.Document.Snapshot.GetLineStart(17), rig.Document.Snapshot.GetLineStart(18)));
        rig.Press("BACK_SPACE");
        Assert.True(editor.RowsAbove.Count == 9 && editor.RowsShown == rows - 2, $"M3's declaration deleted left {editor.RowsAbove.Count} rows above and {editor.RowsShown} rows.");
        rig.Press("meta Z");

        // Rows set above the view's top leave the row at the top where it was.
        TextSnapshot now = rig.Document.Snapshot;
        editor.SetRowsAbove([.. Enumerable.Range(0, 10).Select(i => Above(now, i))]);
        // M3's row above stands below the rows above M0, M1 and M2.
        editor.ScrollTo(new Vector(0, 20 * h));
        Ui.Settle();
        Assert.True(editor.RowAbovePoint(17, 0, 0) is { } top && Math.Abs(top.Y - (h / 2)) < 0.5, "M3's row above is not at the view's top.");
        editor.SetRowsAbove([.. Enumerable.Range(0, 10).Select(i => Above(now, i)), new RowAbove(now.GetLineStart(3) + 8, [new RowAboveWord("more")])]);
        Ui.Settle();
        Assert.True(editor.RowAbovePoint(17, 0, 0) is { } kept && Math.Abs(kept.Y - (h / 2)) < 0.5, "A row set above the view's top moved M3's row above, at the top, down.");
        rig.Window.Close();
    }
}

/// <summary>A key in a long soft-wrapped file costs the line it changed, never a wrap of the whole file, and about what it
/// costs with soft wrap off, in the characters it wraps and the bytes it allocates on the UI thread.</summary>
[SameOnEverySystem]
public sealed class ActiveEditorResponsivenessTests : ActiveEditorFolders
{
    /// <summary>A key in twenty thousand soft-wrapped lines wraps only the line it changed, a key in a screen of
    /// coloured code makes again only the row it changed, a frame of hinted lines allocates about what one of the
    /// same lines without hints does, and a key among rows above lines costs what it does among fewer.</summary>
    [AvaloniaFact]
    public void A_key_wraps_and_draws_again_only_what_it_changed()
    {
        Flow.StepApart<ActiveEditorResponsivenessTests>("a key in twenty thousand soft wrapped lines wraps only the line it changed", t => t.A_key_in_twenty_thousand_soft_wrapped_lines_wraps_only_the_line_it_changed());
        Flow.StepApart<ActiveEditorResponsivenessTests>("a key in a screen of coloured code makes again only the row it changed", t => t.A_key_in_a_screen_of_coloured_code_makes_again_only_the_row_it_changed());
        Flow.StepApart<ActiveEditorResponsivenessTests>("a frame of hinted lines allocates about what one of the same lines naming their arguments does", t => t.A_frame_of_hinted_lines_allocates_about_what_one_of_the_same_lines_naming_their_arguments_does());
        Flow.StepApart<ActiveEditorResponsivenessTests>("a key among twenty thousand rows above lines costs what one among two thousand does", t => t.A_key_among_twenty_thousand_rows_above_lines_costs_what_one_among_two_thousand_does());
    }

    /// <summary>Twenty thousand lines of prose, each wrapping onto rows: a key costs the line it changed, never a wrap of the
    /// whole file, and allocates on the UI thread about what it does with soft wrap off.</summary>
    private void A_key_in_twenty_thousand_soft_wrapped_lines_wraps_only_the_line_it_changed()
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

        long plain = long.MaxValue;
        long soft = long.MaxValue;
        long wrapped = 0;
        for (int round = 0; round < 3; round++)
        {
            rig.Editor.SoftWrap = false;
            Ui.Settle();
            plain = Math.Min(plain, Keys(rig, middle));
            rig.Editor.SoftWrap = true;
            Ui.Settle();
            Assert.True(rig.Editor.RowsShown > 40_000, $"Twenty thousand lines of 150 to 270 characters wrapped into only {rig.Editor.RowsShown} rows.");
            long before = rig.Editor.CharactersWrapped;
            soft = Math.Min(soft, Keys(rig, middle));
            wrapped = rig.Editor.CharactersWrapped - before;
        }
        Assert.True(text == rig.Text, "The keys left the text changed.");

        // Six edits of a line of about two hundred characters: the whole file would be four million.
        Assert.True(wrapped <= 6 * (line + 2), $"Twelve keys, six of them edits of a line of {line} characters, wrapped {wrapped} characters.");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Twelve keys in 20,000 lines ({rig.Editor.RowsShown} rows) allocated {soft:N0} bytes on the UI thread soft-wrapped, {plain:N0} not; they wrapped {wrapped} characters.");
        Assert.True(soft <= plain + (SoftWrapSlack * Timed.Length),
            $"Twelve keys in 20,000 soft-wrapped lines allocated {soft:N0} bytes on the UI thread, against {plain:N0} with soft wrap off: what every line or row has was made again");
    }

    /// <summary>What a key may allocate on the UI thread soft-wrapped beyond what it does unwrapped: its line's rows.</summary>
    private const long SoftWrapSlack = 16 * 1024;

    /// <summary>A row above every line of twenty thousand: what the rows above add to what the keys allocate on the UI thread
    /// is what they add among two thousand, the rows carried through each edit as runs it moves only where it reaches, and
    /// the keys draw again no row above a line they did not change.</summary>
    private void A_key_among_twenty_thousand_rows_above_lines_costs_what_one_among_two_thousand_does()
    {
        // The least of three rounds of each, as what a collection's end counts that was never allocated only adds.
        long[] least = [long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue];
        long drawn = 0;
        for (int round = 0; round < 3; round++)
        {
            least[0] = Math.Min(least[0], Measured(2_000, above: false).Bytes);
            least[1] = Math.Min(least[1], Measured(2_000, above: true).Bytes);
            least[2] = Math.Min(least[2], Measured(20_000, above: false).Bytes);
            (long bytes, long redrawn) = Measured(20_000, above: true);
            least[3] = Math.Min(least[3], bytes);
            drawn = redrawn;
        }
        long few = least[1] - least[0];
        long many = least[3] - least[2];
        TestContext.Current.TestOutputHelper?.WriteLine($"Rows above lines added {few:N0} bytes on the UI thread to twelve keys among 2,000 lines, {many:N0} among 20,000");
        Assert.True(many <= few + (512 * Timed.Length),
            $"Rows above lines added {many:N0} bytes on the UI thread to twelve keys among 20,000 lines, against {few:N0} among 2,000: every row above was carried or made again");
        Assert.True(drawn == 0, $"Twelve keys on a line with no row above drew {drawn} rows above other lines again");

        // Twelve keys in the middle of a file of lines, each but that one with a row above or none: the bytes they
        // allocated, and the rows above drawn.
        static (long Bytes, long Drawn) Measured(int lines, bool above)
        {
            StringBuilder source = new();
            for (int i = 0; i < lines; i++)
            {
                source.Append("    int value").Append(i).Append(" = ").Append(i).Append(";\n");
            }
            EditorRig rig = new(source.ToString());
            TextSnapshot text = rig.Document.Snapshot;
            if (above)
            {
                rig.Editor.SetRowsAbove([.. Enumerable.Range(0, lines).Where(i => i != lines / 2).Select(i => new RowAbove(text.GetLineStart(i) + 4, [new RowAboveWord($"{i} usages"), new RowAboveWord("Ada Lovelace")]))]);
            }
            rig.Editor.GoTo(new TextPosition(lines / 2, 8));
            Ui.Settle();
            // The lines, the empty one after the last line break, and a row above each line but one.
            Assert.True(rig.Editor.RowsShown == lines + 1 + (above ? lines - 1 : 0), $"{lines:N0} lines show {rig.Editor.RowsShown:N0} rows.");
            long before = rig.Editor.RowsAboveDrawn;
            long bytes = Keys(rig, text.GetLineStart(lines / 2) + 8);
            long drawn = rig.Editor.RowsAboveDrawn - before;
            rig.Window.Close();
            return (bytes, drawn);
        }
    }

    /// <summary>A frame of lines with hints, a parameter's name before each argument, allocates on the UI thread about
    /// what a frame of the same lines naming their arguments in their text does.</summary>
    private void A_frame_of_hinted_lines_allocates_about_what_one_of_the_same_lines_naming_their_arguments_does()
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

    /// <summary>A key in a screenful of coloured C# makes again only the row it changed: it asks for the colours of that
    /// row alone, and what it allocates on the UI thread, its parse landing included, beyond what drawing the screen once
    /// does, is under 256 bytes more for each row in an editor showing 67 rows than in one showing 19, the least of six
    /// keys. Every row's view, colours and text were made again for each key, and every row's colours asked for again as
    /// its parse landed: 2.6 KB for each row more.</summary>
    private void A_key_in_a_screen_of_coloured_code_makes_again_only_the_row_it_changed()
    {
        StringBuilder text = new("static class Coloured\n{\n");
        for (int i = 0; i < 200; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"    public static string Name{i}(int value) => value > {i} ? \"large\" : $\"small {{value}}\"; // note {i}\n");
        }
        string source = text.Append("}\n").ToString();
        string path = Path.Combine(folder, "Coloured.cs");
        File.WriteAllText(path, source);
        (long few, int fewRows) = Keys(200);
        (long many, int manyRows) = Keys(1500);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key allocated {few:N0} bytes on the UI thread beyond its frame showing {fewRows} rows, {many:N0} showing {manyRows}");
        Assert.True(manyRows >= fewRows + 40, $"The tall editor showed {manyRows} rows, the short one {fewRows}");
        Assert.True(many - few < 256 * (manyRows - fewRows),
            $"A key in coloured C# allocated {many:N0} bytes on the UI thread beyond its frame showing {manyRows} rows, against {few:N0} showing {fewRows}: {(many - few) / (manyRows - fewRows)} bytes for each row more, every row on screen made again");

        // A key's least UI-thread bytes, at a mid-screen line's end, less a frame's with nothing changed; and the rows shown.
        (long Bytes, int Rows) Keys(double height)
        {
            MainWindow window = new() { Width = 1100, Height = height, StartsLanguageServer = false };
            window.Show();
            window.OpenFile(path);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            (int first, int last) = editor.LinesShown;
            int line = (first + last) / 2;
            LineSpan typed = editor.Document.Snapshot.GetLine(line);
            Assert.EndsWith($"// note {line - 2}", editor.Document.Snapshot.GetText(typed.Start, typed.Length), StringComparison.Ordinal);
            editor.Selections = SelectionSet.Single(typed.End);
            editor.Focus();
            Ui.Settle();
            long key = long.MaxValue;
            long frame = long.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                long coloured = editor.CharactersColoured;
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.KeyTextInput("7");
                Ui.Settle();
                key = Math.Min(key, GC.GetAllocatedBytesForCurrentThread() - before);
                coloured = editor.CharactersColoured - coloured;
                Assert.True(coloured <= typed.Length + 16,
                    $"A key on a line of {typed.Length + i + 1} characters showing {last - first + 1} rows had {coloured:N0} characters coloured: rows it did not change were coloured again");
                editor.InvalidateVisual();
                before = GC.GetAllocatedBytesForCurrentThread();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                frame = Math.Min(frame, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            TestContext.Current.TestOutputHelper?.WriteLine($"A key allocated {key:N0} bytes on the UI thread, a frame {frame:N0}");
            int rows = editor.LinesShown.Last - editor.LinesShown.First + 1;
            Assert.Contains($"// note {line - 2}777777\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            window.Close();
            return (key - frame, rows);
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

    /// <summary>What <see cref="Timed"/>'s keys at an offset allocate on the UI thread, each with what it settled.</summary>
    protected static long Keys(EditorRig rig, int offset)
    {
        rig.PlaceCaret(offset);
        Ui.Settle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string key in Timed)
        {
            if (key.Length == 1)
            {
                rig.Type(key);
            }
            else
            {
                rig.Press(key);
            }
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
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
        double right = editor.GutterColumnPoint(editor.Gutter!.Changes, 0).X - 3;
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
