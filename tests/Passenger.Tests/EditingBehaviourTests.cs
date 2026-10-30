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
    [Gate]
    [AvaloniaFact]
    public void Carets_cloned_down_the_lines_type_together_and_undo_as_one()
    {
        EditorRig rig = new("one\ntwo\nthree");
        rig.Press("control shift DOWN");
        rig.Press("control shift DOWN");
        Assert.Equal(3, rig.Editor.Selections.Count);
        rig.Type("//");
        Assert.Equal("//one\n//two\n//three", rig.Text);
        rig.Press("meta Z");
        Assert.Equal("one\ntwo\nthree", rig.Text);
    }

    /// <summary>A word typed undoes and redoes as one change, so whatever follows the text (the anchors, folds, colours,
    /// the language server) hears of it once rather than once a key; the typing help's closer and the step over it go
    /// with the word.</summary>
    [Gate]
    [AvaloniaFact]
    public void Undoing_a_typed_word_takes_it_back_in_one_change_and_redoing_makes_it_again_in_one()
    {
        EditorRig rig = new("first\n");
        rig.PlaceCaret(5);
        rig.Type(" Call(x)");
        Assert.Equal("first Call(x)\n", rig.Text);
        int changes = 0;
        rig.Document.Changed += (_, _) => changes++;

        rig.Press("meta Z");
        Assert.True(changes == 1, $"Undoing the word typed raised {changes} changes, one a key, so every handler of a change ran that often");
        Assert.Equal("first\n", rig.Text);
        Assert.Equal(5, rig.Caret);
        Assert.False(rig.Document.IsModified, "Undoing the only word typed left the text marked as changed from what was loaded");

        changes = 0;
        rig.Press("meta shift Z");
        Assert.True(changes == 1, $"Redoing the word typed raised {changes} changes, one a key");
        Assert.Equal("first Call(x)\n", rig.Text);
        Assert.Equal(13, rig.Caret);
    }

    /// <summary>Thousands of keys keep an undo history no larger than its bound, however many more are typed: no version
    /// of the text a key made stays held for its undo, and the newest steps, thousands of them, still undo one at a time
    /// back to the text the oldest kept began from and redo to the last.</summary>
    [Gate]
    [AvaloniaFact]
    public void Thousands_of_keys_keep_an_undo_history_within_its_bound_that_still_undoes_their_newest_thousands_of_steps()
    {
        StringBuilder lines = new();
        for (int i = 0; i < 1500; i++)
        {
            lines.Append("    int value").Append(i).Append(" = Compute(").Append(i).Append(");\n");
        }
        string initial = lines.ToString();
        EditorRig rig = new(initial);
        rig.Window.Width = 300;
        rig.Window.Height = 120;
        int caret = initial.Length / 2;
        rig.PlaceCaret(caret);
        Ui.Settle();
        // Each version of the text a key leaves behind, held weakly: one the history keeps for its undo stays alive.
        List<WeakReference<TextSnapshot>> versions = [];
        rig.Document.Changed += (_, change) => versions.Add(new WeakReference<TextSnapshot>(change.Before));

        // A word and a space a step: a step for each two keys, the most steps a run of typing makes.
        const int Keys = 8000;
        string typed = string.Concat(Enumerable.Repeat("x ", Keys / 2));
        long[] kept = new long[Keys / 1000];
        for (int chunk = 0; chunk < kept.Length; chunk++)
        {
            rig.Type(typed.Substring(chunk * 1000, 1000));
            kept[chunk] = rig.Document.HistoryBytes;
        }
        Assert.True(rig.Text == initial[..caret] + typed + initial[caret..], "The keys did not type what they were");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int alive = versions.Count(v => v.TryGetTarget(out _));
        Assert.True(alive <= 10, $"{alive} of the {versions.Count} versions of the text the keys left behind are still held, each with the pieces of text it was made of");
        string grew = string.Join(", ", kept.Select((bytes, i) => $"{(i + 1) * 1000} keys: {bytes / 1024} KB"));
        Assert.True(kept.All(bytes => bytes <= TextDocument.KeptBytes),
            $"The undo history kept more than its bound of {TextDocument.KeptBytes / 1024} KB as the keys went on ({grew})");
        Assert.True(kept[^1] > TextDocument.KeptBytes * 3 / 4 && kept[^2] > TextDocument.KeptBytes * 3 / 4,
            $"The keys never brought the undo history near its bound, so it was never held to it ({grew})");

        int steps = 0;
        while (rig.Document.CanUndo)
        {
            rig.Editor.TryExecute(EditorActions.Undo);
            steps++;
        }
        string start = rig.Text;
        Assert.True(steps >= 3000, $"Only the newest {steps} of the keys' {Keys / 2} steps undo, where the bound holds thousands");
        int dropped = start.Length - initial.Length;
        Assert.True(dropped > 0 && start == initial[..caret] + typed[..dropped] + initial[caret..],
            $"Undoing every step kept did not give back the text as the oldest step kept began from it ({dropped} characters typed before it)");
        Assert.Equal(caret + dropped, rig.Caret);
        while (rig.Document.CanRedo)
        {
            rig.Editor.TryExecute(EditorActions.Redo);
        }
        Assert.True(rig.Text == initial[..caret] + typed + initial[caret..], "Redoing every step did not give back the text the keys left");
    }

    [Gate]
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
    public void Occurrences_of_a_word_taken_from_a_caret_leave_longer_names_holding_it_alone()
    {
        EditorRig rig = new("_value value values value");
        rig.PlaceCaret(9);
        rig.Press("alt shift PERIOD");
        rig.Press("alt shift PERIOD");
        rig.Press("alt shift PERIOD");
        rig.Type("amount");
        Assert.True(rig.Text == "_value amount values amount", $"Select Next Occurrence from a caret in value, typing amount, left {rig.Text}");
        rig.PlaceCaret(9);
        rig.Press("alt shift SEMICOLON");
        rig.Type("sum");
        Assert.True(rig.Text == "_value sum values sum", $"Select All Occurrences from a caret in amount, typing sum, left {rig.Text}");
    }

    [Gate]
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

    [Gate]
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

    [Gate]
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

    [Gate]
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

    [Gate]
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
    [Gate]
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

    [Gate]
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

    [Gate]
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

    [Gate]
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

    [Gate]
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

    [Gate]
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
