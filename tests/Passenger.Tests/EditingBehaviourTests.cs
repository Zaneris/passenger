using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The editing a person notices when it is wrong, each pressed through the real editor with
/// Rider's keys: typing help, Enter, several carets, the line commands, comments and the clipboard.</summary>
public sealed class EditingBehaviourTests
{
    [AvaloniaFact]
    public void Typing_an_opening_bracket_closes_it_and_typing_the_closer_steps_over_it()
    {
        EditorRig rig = new("");
        rig.Type("Call(");
        Assert.Equal("Call()", rig.Text);
        Assert.Equal(5, rig.Caret);
        rig.Type("x)");
        Assert.Equal("Call(x)", rig.Text);
        Assert.Equal(7, rig.Caret);
    }

    [AvaloniaFact]
    public void A_quote_typed_over_a_selection_wraps_it()
    {
        EditorRig rig = new("name");
        rig.Press("meta A");
        rig.Type("\"");
        Assert.Equal("\"name\"", rig.Text);
    }

    [AvaloniaFact]
    public void Enter_between_braces_opens_an_indented_line_with_the_closer_below()
    {
        EditorRig rig = new("    void Run() {}");
        rig.PlaceCaret("    void Run() {".Length);
        rig.Press("ENTER");
        Assert.Equal("    void Run() {\n        \n    }", rig.Text);
        Assert.Equal(new TextPosition(1, 8), rig.Editor.CaretPosition);
    }

    [AvaloniaFact]
    public void Enter_at_the_start_of_an_indented_line_moves_the_line_down_with_its_indentation()
    {
        EditorRig rig = new("{\n    first();\n    second();\n}");
        rig.PlaceCaret("{\n    first();\n".Length);
        rig.Press("ENTER");
        Assert.True(rig.Text == "{\n    first();\n\n    second();\n}", $"Enter at the line's start took its indentation away:\n{rig.Text}");
        Assert.Equal(new TextPosition(3, 4), rig.Editor.CaretPosition);

        rig.PlaceCaret("{\n    first();\n\n  ".Length);
        rig.Press("ENTER");
        Assert.True(rig.Text == "{\n    first();\n\n  \n    second();\n}", $"Enter inside the line's indentation took part of it away:\n{rig.Text}");
        Assert.Equal(new TextPosition(4, 4), rig.Editor.CaretPosition);
    }

    [AvaloniaFact]
    public void Backspace_in_indentation_removes_a_whole_level()
    {
        EditorRig rig = new("        x");
        rig.PlaceCaret(8);
        rig.Press("BACK_SPACE");
        Assert.Equal("    x", rig.Text);
    }

    [AvaloniaFact]
    public void Carets_cloned_down_the_lines_type_together_and_undo_as_one()
    {
        EditorRig rig = new("one\ntwo\nthree");
        rig.Press("alt shift DOWN");
        rig.Press("alt shift DOWN");
        Assert.Equal(3, rig.Editor.Selections.Count);
        rig.Type("//");
        Assert.Equal("//one\n//two\n//three", rig.Text);
        rig.Press("meta Z");
        Assert.Equal("one\ntwo\nthree", rig.Text);
    }

    [AvaloniaFact]
    public void Select_next_occurrence_picks_the_word_then_each_copy_and_typing_replaces_them_all()
    {
        EditorRig rig = new("total = total + total");
        rig.PlaceCaret(2);
        rig.Press("alt shift PERIOD");
        rig.Press("alt shift PERIOD");
        rig.Press("alt shift PERIOD");
        Assert.Equal(3, rig.Editor.Selections.Count);
        rig.Type("sum");
        Assert.Equal("sum = sum + sum", rig.Text);
    }

    [AvaloniaFact]
    public void Line_commands_duplicate_move_and_delete_the_caret_line()
    {
        EditorRig rig = new("alpha\nbeta\ngamma");
        rig.PlaceCaret(2);
        rig.Press("control D");
        Assert.Equal("alpha\nalpha\nbeta\ngamma", rig.Text);
        Assert.Equal(new TextPosition(1, 2), rig.Editor.CaretPosition);
        rig.Press("alt DOWN");
        Assert.Equal("alpha\nbeta\nalpha\ngamma", rig.Text);
        Assert.Equal(new TextPosition(2, 2), rig.Editor.CaretPosition);
        rig.Press("meta shift L");
        Assert.Equal("alpha\nbeta\ngamma", rig.Text);
        Assert.Equal(new TextPosition(2, 2), rig.Editor.CaretPosition);
    }

    [AvaloniaFact]
    public void Joining_three_selected_lines_whose_middle_one_is_only_spaces_gives_one_line_and_leaves_the_next_alone()
    {
        EditorRig rig = new("        Start();\n        \n        Stop();\n    }");
        rig.PlaceCaret(0);
        rig.Press("shift DOWN");
        rig.Press("shift DOWN");
        rig.Press("shift meta RIGHT");
        rig.Press("control shift J");
        Assert.Equal("        Start(); Stop();\n    }", rig.Text);
        Assert.Equal(new TextRange(0, "        Start(); Stop();".Length), rig.Editor.Selections.Primary.Range);
        rig.Press("meta Z");
        Assert.Equal("        Start();\n        \n        Stop();\n    }", rig.Text);
    }

    [AvaloniaFact]
    public void Enter_at_two_carets_in_one_run_of_spaces_splits_the_line_at_both()
    {
        EditorRig rig = new("    one    two");
        rig.PlaceCaret("    one".Length);
        Point second = Within(rig, rig.Editor.PointOf("    one  ".Length) + new Point(1, 4));
        rig.Window.MouseDown(second, MouseButton.Left, RawInputModifiers.Alt | RawInputModifiers.Shift);
        rig.Window.MouseUp(second, MouseButton.Left, RawInputModifiers.Alt | RawInputModifiers.Shift);
        Ui.Settle();
        Assert.Equal(2, rig.Editor.Selections.Count);
        rig.Press("ENTER");
        Assert.Equal("    one\n    \n    two", rig.Text);
        Assert.Equal([new TextPosition(1, 4), new TextPosition(2, 4)], rig.Editor.Selections.Items.Select(s => rig.Document.Snapshot.GetPosition(s.Caret)));
    }

    [AvaloniaFact]
    public void Enter_with_each_space_of_a_run_selected_splits_the_line_at_each()
    {
        EditorRig rig = new("first  second");
        rig.PlaceCaret("first".Length);
        rig.Press("shift RIGHT");
        rig.Press("alt shift SEMICOLON");
        Assert.Equal(2, rig.Editor.Selections.Count);
        rig.Press("ENTER");
        Assert.Equal("first\n\nsecond", rig.Text);
        Assert.Equal([new TextPosition(1, 0), new TextPosition(2, 0)], rig.Editor.Selections.Items.Select(s => rig.Document.Snapshot.GetPosition(s.Caret)));
    }

    [AvaloniaFact]
    public void The_comment_chords_add_and_remove_comments_and_the_toggle_undoes_itself()
    {
        EditorRig rig = new("    Run();\n    Stop();");
        rig.Press("meta A");
        rig.Press("meta K");
        rig.Press("meta C");
        Assert.Equal("    // Run();\n    // Stop();", rig.Text);
        rig.Press("meta K");
        rig.Press("meta U");
        Assert.Equal("    Run();\n    Stop();", rig.Text);
        rig.Press("meta SLASH");
        rig.Press("meta SLASH");
        Assert.Equal("    Run();\n    Stop();", rig.Text);
    }

    /// <summary>A block comment can hold what reads as another one's start (a path's "/*"), from which a caret after it
    /// finds the comment, by the same end as a caret before it does: the two carets take the comment's ends off once.</summary>
    [AvaloniaFact]
    public void Block_comment_at_carets_in_one_comment_holding_another_start_takes_its_ends_off_once()
    {
        const string text = "/* copies src/*.cs and docs/*.md */\nint x;\n";
        EditorRig rig = new(text);
        rig.PlaceCaret(text.IndexOf("copies", StringComparison.Ordinal));
        rig.AddCaret(text.IndexOf(".md", StringComparison.Ordinal));
        rig.Press("control meta SLASH");
        Assert.True(rig.Text == "copies src/*.cs and docs/*.md\nint x;\n",
            $"Block Comment at two carets in one comment left \"{rig.Text}\", not the comment's text without its ends");
        Assert.Equal(2, rig.Editor.Selections.Count);
    }

    [AvaloniaFact]
    public void Copy_with_nothing_selected_takes_the_line_and_paste_puts_it_back_above()
    {
        EditorRig rig = new("first\nsecond");
        rig.PlaceCaret(8);
        rig.Press("meta C");
        rig.Press("meta V");
        Ui.Settle();
        Assert.Equal("first\nsecond\nsecond", rig.Text);
        Assert.Equal(new TextPosition(2, 2), rig.Editor.CaretPosition);
    }

    /// <summary>Text on the clipboard whose lines end in a lone carriage return (as classic Mac text and some apps' put
    /// it there) goes in as lines in the file's breaks, never as one line the editor draws whole and the compiler reads
    /// as several.</summary>
    [AvaloniaFact]
    public void Text_pasted_with_lone_carriage_returns_goes_in_as_lines_with_the_files_breaks()
    {
        EditorRig rig = new("first\nlast");
        rig.PlaceCaret("first\n".Length);
        rig.Window.Clipboard!.SetTextAsync("one\rtwo\r").GetAwaiter().GetResult();
        rig.Press("meta V");
        Ui.Settle();
        Assert.True(rig.Text == "first\none\ntwo\nlast",
            $"The paste left \"{rig.Text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\", not its lines with the file's breaks");
        Assert.Equal(new TextPosition(3, 0), rig.Editor.CaretPosition);
    }

    [AvaloniaFact]
    public void Option_drag_selects_a_column_and_typing_fills_every_row()
    {
        EditorRig rig = new("abc\ndef\nghi");
        Point from = rig.Editor.PointOf(1) + new Point(1, 4);
        Point to = rig.Editor.PointOf(rig.Document.Snapshot.GetOffset(new TextPosition(2, 2))) + new Point(1, 4);
        rig.Window.MouseDown(Within(rig, from), MouseButton.Left, RawInputModifiers.Alt);
        rig.Window.MouseMove(Within(rig, to), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        rig.Window.MouseUp(Within(rig, to), MouseButton.Left, RawInputModifiers.Alt);
        Ui.Settle();
        Assert.Equal(3, rig.Editor.Selections.Count);
        rig.Type("X");
        Assert.Equal("aXc\ndXf\ngXi", rig.Text);
    }

    [AvaloniaFact]
    public void A_word_being_typed_keeps_its_colour_before_the_reparse_lands_and_after()
    {
        string source = "class Numbers\n{\n    int Twice(int value) => value * 2;\n}\n";
        (Passenger.App.MainWindow window, Passenger.Editor.CodeEditor editor) = OpenCSharp(source);
        int method = source.IndexOf("Twice", StringComparison.Ordinal);
        Assert.Equal(Passenger.Editor.TokenKind.Method, KindAt(editor, method));

        editor.Selections = SelectionSet.Single(method + "Twice".Length);
        window.KeyTextInput("r");
        Assert.Equal(Passenger.Editor.TokenKind.Method, KindAt(editor, method + 5));
        Ui.Settle();
        Assert.Equal(Passenger.Editor.TokenKind.Method, KindAt(editor, method + 5));
        window.Close();
    }

    /// <summary>A key draws the editor once: the parse it makes lands with the colours the key carried its words to,
    /// and a parse that changes nothing on screen draws nothing. A parse that does change a colour on screen draws it:
    /// a word typed becomes a keyword. The caret's blink draws the editor too, whenever a key's wait for its work
    /// outlasts 530 ms (a loaded machine's): its frames are not the key's.</summary>
    [AvaloniaFact]
    public void A_key_draws_the_editor_once_and_a_parse_that_changes_a_colour_on_screen_draws_it()
    {
        StringBuilder text = new("class Numbers\n{\n");
        for (int i = 0; i < 40; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    int Twice{i}(int value) => value * 2;\n");
        }
        string source = text.Append("    int Last()\n    {\n        int last = 1;\n    }\n}\n").ToString();
        (Passenger.App.MainWindow window, Passenger.Editor.CodeEditor editor) = OpenCSharp(source);
        int literal = source.IndexOf("value * 2;", StringComparison.Ordinal) + "value * 2".Length;
        editor.Selections = SelectionSet.Single(literal);
        Ui.Settle();
        for (int i = 0; i < 10; i++)
        {
            long drawn = Drawn(editor, () => window.KeyTextInput("2"));
            Assert.True(drawn == 1, $"Key {i + 1}, a digit in a number, drew the editor {drawn} times, the caret's blinks aside");
            Assert.Equal(Passenger.Editor.TokenKind.Number, KindAt(editor, literal + i));
        }

        int end = editor.Document.Snapshot.ToString().IndexOf("int last = 1;", StringComparison.Ordinal) + "int last = 1;".Length;
        editor.Selections = SelectionSet.Single(end);
        window.KeyTextInput(" retur");
        Ui.Settle();
        Assert.NotEqual(Passenger.Editor.TokenKind.ControlKeyword, KindAt(editor, end + 1));
        long keyword = Drawn(editor, () => window.KeyTextInput("n"));
        Assert.Equal(Passenger.Editor.TokenKind.ControlKeyword, KindAt(editor, end + 1));
        Assert.True(keyword == 2, $"The key that made a keyword drew the editor {keyword} times, the caret's blinks aside, not once for the key and once for the keyword's colour");
        window.Close();
    }

    [AvaloniaFact]
    public void Brackets_take_turns_in_three_colours_by_how_deeply_they_nest_and_a_comparison_is_not_one()
    {
        const string source = "class C\n{\n    bool M(List<int> xs)\n    {\n        return Call(xs[0], 1 < 2);\n    }\n}\n";
        (Passenger.App.MainWindow window, Passenger.Editor.CodeEditor editor) = OpenCSharp(source);
        Passenger.Editor.TokenKind? At(string before) => KindAt(editor, source.IndexOf(before, StringComparison.Ordinal) + before.Length - 1);

        Assert.Equal(Passenger.Editor.TokenKind.Brace1, At("C\n{"));
        Assert.Equal(Passenger.Editor.TokenKind.Brace2, At("M("));
        Assert.Equal(Passenger.Editor.TokenKind.Brace3, At("List<"));
        Assert.Equal(Passenger.Editor.TokenKind.Brace3, At("int>"));
        Assert.Equal(Passenger.Editor.TokenKind.Brace3, At("Call("));
        // The fourth level comes round to the first colour.
        Assert.Equal(Passenger.Editor.TokenKind.Brace1, At("xs["));
        Assert.Equal(Passenger.Editor.TokenKind.Brace1, At("0]"));
        Assert.NotEqual(Passenger.Editor.TokenKind.Brace1, At("1 <"));
        Assert.Equal(Passenger.Editor.TokenKind.Brace1, KindAt(editor, source.LastIndexOf('}')));
        window.Close();
    }

    [AvaloniaFact]
    public void A_selection_from_the_start_of_a_line_leaves_the_line_above_it_unpainted()
    {
        EditorRig rig = new("first\nsecond\nthird\nfourth\nfifth\n");
        rig.PlaceCaret(rig.Text.IndexOf("second", StringComparison.Ordinal));
        rig.Press("shift DOWN");
        Assert.Equal("second\n", rig.Text[rig.Editor.Selections.Primary.Start..rig.Editor.Selections.Primary.End]);
        Ui.Settle();

        // Past the lines' ends, where a selection running on through a line break fills to the edge.
        Avalonia.Media.Color above = Ui.Pixel(rig.Window, PastTheEnd(rig, 0));
        Avalonia.Media.Color selected = Ui.Pixel(rig.Window, PastTheEnd(rig, 1));
        Avalonia.Media.Color plain = Ui.Pixel(rig.Window, PastTheEnd(rig, 4));
        Assert.True(selected != plain, $"The selected line is {selected} past its end, as a line with no selection is");
        Assert.True(above == plain,
            $"The line above the selection is {above} past its end, the selection's {selected}, not the editor's {plain}: the selection reaches back over the line break before it");
    }

    [AvaloniaFact]
    public void The_input_methods_window_opens_on_the_carets_line_below_a_fold_and_far_along_a_long_line_and_follows_the_caret()
    {
        StringBuilder text = new();
        for (int i = 0; i < 40; i++)
        {
            text.Append("    int value").Append(i).Append(" = ").Append(i).Append(";\n");
        }
        text.Append(string.Concat(Enumerable.Range(0, 20_000).Select(i => $"w{i % 10} "))).Append('\n');
        EditorRig rig = new(text.ToString());
        TextSnapshot snapshot = rig.Document.Snapshot;
        TextInputMethodClient client = InputMethodOf(rig.Editor);
        // Lines 3 to 22 folded away behind line 2: the caret on line 30 is drawn on the eleventh row.
        rig.Editor.SetFoldRegions([new FoldRegion(snapshot.GetLine(2).End, snapshot.GetLine(22).End, "...", Folded: true)]);
        rig.PlaceCaret(snapshot.GetLineStart(30) + 6);
        Ui.Settle();
        Rect cursor = client.CursorRectangle;
        double row = (10 * rig.Editor.LineHeight) - rig.Editor.ScrollOffset.Y;
        Assert.True(Math.Abs(cursor.Y - row) < 0.5, $"The input method's window would open at y {cursor.Y}, a fold's height from the caret's row at {row}");
        Assert.Equal(rig.Editor.PointOf(rig.Caret), cursor.TopLeft);

        string seen = string.Empty;
        client.CursorRectangleChanged += (_, _) => seen += "cursor ";
        client.SelectionChanged += (_, _) => seen += "selection ";
        client.SurroundingTextChanged += (_, _) => seen += "text ";
        rig.Press("RIGHT");
        Assert.True(seen.Contains("cursor", StringComparison.Ordinal) && seen.Contains("selection", StringComparison.Ordinal),
            $"The caret moved and the input method heard only [{seen}], so its window stays where the caret was");
        Assert.Equal(rig.Editor.PointOf(rig.Caret), client.CursorRectangle.TopLeft);
        seen = string.Empty;
        rig.Press("DOWN");
        Assert.True(seen.Contains("text", StringComparison.Ordinal), $"The caret went to another line and the input method heard only [{seen}]");
        Assert.Equal(rig.Text.Split('\n')[31], client.SurroundingText);
        seen = string.Empty;
        rig.Type("z");
        Assert.True(seen.Contains("text", StringComparison.Ordinal) && seen.Contains("cursor", StringComparison.Ordinal),
            $"A letter typed and the input method heard only [{seen}]");
        seen = string.Empty;
        double folded = client.CursorRectangle.Y;
        rig.Editor.SetFoldRegions([]);
        Assert.True(seen.Contains("cursor", StringComparison.Ordinal), $"The lines above the caret unfolded and the input method heard only [{seen}]");
        Assert.Equal(folded + (20 * rig.Editor.LineHeight), client.CursorRectangle.Y, 3);

        int far = rig.Document.Snapshot.GetLineStart(40) + 50_000;
        rig.PlaceCaret(far);
        Ui.Settle();
        cursor = client.CursorRectangle;
        Assert.Equal(rig.Editor.PointOf(far), cursor.TopLeft);
        Assert.True(cursor.X > 0 && cursor.X < rig.Editor.Bounds.Width, $"The input method's window would open at x {cursor.X}, outside the editor's {rig.Editor.Bounds.Width}");
    }

    [AvaloniaFact]
    public void Press_and_hold_replaces_the_letter_held_with_the_accent_chosen_at_every_caret()
    {
        EditorRig rig = new("caf\ncaf\n");
        rig.PlaceCaret(3);
        rig.AddCaret(7);
        rig.Type("e");
        Assert.Equal("cafe\ncafe\n", rig.Text);
        TextInputMethodClient client = InputMethodOf(rig.Editor);
        // macOS's accent menu selects the letter it was held for through the client, then types the accent over it.
        TextSelection held = client.Selection;
        client.Selection = new TextSelection(held.End - 1, held.End);
        rig.Window.KeyTextInput("é");
        Ui.Settle();
        Assert.Equal("café\ncafé\n", rig.Text);
        Assert.All(rig.Editor.Selections.Items, s => Assert.True(s.IsEmpty && rig.Text[s.Caret - 1] == 'é', $"A caret was left at {s.Caret}, not after an accent"));
    }

    [AvaloniaFact]
    public void A_composition_moves_the_text_after_it_aside_rather_than_covering_it_and_goes_in_as_typed()
    {
        EditorRig rig = new("let = 1;\n");
        rig.PlaceCaret(3);
        TextInputMethodClient client = InputMethodOf(rig.Editor);
        double after = rig.Editor.PointOf(4).X;
        double caret = client.CursorRectangle.X;
        double letter = rig.Editor.PointOf(1).X - rig.Editor.PointOf(0).X;
        client.SetPreeditText("にほん");
        Ui.Settle();
        double moved = rig.Editor.PointOf(4).X - after;
        Assert.True(moved > 2 * letter, $"The text after the composition moved {moved:0.#} to the right, less than its three characters: it is drawn under it");
        Assert.Equal(caret + moved, client.CursorRectangle.X, 3);

        rig.Window.KeyTextInput("日本");
        Ui.Settle();
        Assert.Equal("let日本 = 1;\n", rig.Text);
        Assert.Equal(5, rig.Caret);
        Assert.Equal(rig.Editor.PointOf(5).X, client.CursorRectangle.X, 3);
    }

    /// <summary>The editor's input method client, as the platform asks the focused element for it.</summary>
    private static TextInputMethodClient InputMethodOf(CodeEditor editor)
    {
        TextInputMethodClientRequestedEventArgs request = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        editor.RaiseEvent(request);
        return request.Client ?? throw new InvalidOperationException("The editor gave the input method no client");
    }

    /// <summary>The colour the editor gives a character, as the window's highlighting has it now.</summary>
    private static Passenger.Editor.TokenKind? KindAt(Passenger.Editor.CodeEditor editor, int offset) =>
        editor.Highlighter!.GetSpans(editor.Document.Snapshot, offset, 1)
            .Where(s => s.Start <= offset && offset < s.End)
            .Cast<Passenger.Editor.SyntaxSpan?>()
            .FirstOrDefault()?.Kind;

    /// <summary>The frames the editor drew for a key and the work it set off, once that has landed, less those its
    /// caret's blink asked for meanwhile.</summary>
    private static long Drawn(Passenger.Editor.CodeEditor editor, Action key)
    {
        long frames = editor.Frames;
        long blinks = editor.Blinks;
        key();
        Ui.Settle();
        return editor.Frames - frames - (editor.Blinks - blinks);
    }

    /// <summary>A C# file opened in the window, with no project: its colours are the window's own, read from its
    /// syntax.</summary>
    private static (Passenger.App.MainWindow Window, Passenger.Editor.CodeEditor Editor) OpenCSharp(string source)
    {
        string folder = TestFolders.Create("colours-");
        File.WriteAllText(Path.Combine(folder, "Numbers.cs"), source);
        Passenger.App.MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Numbers.cs");
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        return (window, window.EditorArea.CodeEditor);
    }

    private static Point Within(EditorRig rig, Point point) =>
        rig.Editor.TranslatePoint(point, rig.Window) ?? throw new InvalidOperationException("The editor is not in the window.");

    /// <summary>A point halfway down a line, well right of its text.</summary>
    private static Point PastTheEnd(EditorRig rig, int line)
    {
        Point top = rig.Editor.PointOf(rig.Document.Snapshot.GetLineStart(line));
        double bottom = rig.Editor.GutterPoint(line).Y;
        return Within(rig, new Point(top.X + 300, (top.Y + bottom) / 2));
    }
}
