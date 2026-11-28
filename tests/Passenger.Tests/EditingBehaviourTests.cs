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
    /// <summary>Editing, the gate's checks as one flow: carets cloned down the lines type together and undo as one, a
    /// typed word undoes and redoes in one change, thousands of keys keep an Undo history within its bound, Select Next
    /// Occurrence takes the word and each copy, three lines joined around a blank one give one line, the comment chords
    /// add and take away comments, Copy with nothing selected takes the line, and an Option drag chooses a column that
    /// typing fills.</summary>
    [Gate]
    [AvaloniaFact]
    public void Carets_undo_selections_and_line_commands_edit_as_a_person_expects()
    {
        Flow.StepApart<EditingBehaviourTests>("caret's cloned down the lines type together and undo as one", t => t.Carets_cloned_down_the_lines_type_together_and_undo_as_one());
        Flow.StepApart<EditingBehaviourTests>("undoing a typed word takes it back in one change and redoing makes it again in one", t => t.Undoing_a_typed_word_takes_it_back_in_one_change_and_redoing_makes_it_again_in_one());
        Flow.StepApart<EditingBehaviourTests>("thousands of keys keep an undo history within its bound that still undoes their newest thousands of steps", t => t.Thousands_of_keys_keep_an_undo_history_within_its_bound_that_still_undoes_their_newest_thousands_of_steps());
        Flow.StepApart<EditingBehaviourTests>("select next occurrence picks the word then each copy and typing replaces them all", t => t.Select_next_occurrence_picks_the_word_then_each_copy_and_typing_replaces_them_all());
        Flow.StepApart<EditingBehaviourTests>("joining three selected lines whose middle one is only spaces gives one line and leaves the next alone", t => t.Joining_three_selected_lines_whose_middle_one_is_only_spaces_gives_one_line_and_leaves_the_next_alone());
        Flow.StepApart<EditingBehaviourTests>("the comment chords add and remove comments and the toggle undoes itself", t => t.The_comment_chords_add_and_remove_comments_and_the_toggle_undoes_itself());
        Flow.StepApart<EditingBehaviourTests>("copy with nothing selected takes the line and paste puts it back above", t => t.Copy_with_nothing_selected_takes_the_line_and_paste_puts_it_back_above());
        Flow.StepApart<EditingBehaviourTests>("option drag selects a column and typing fills every row", t => t.Option_drag_selects_a_column_and_typing_fills_every_row());
    }

    /// <summary>What is typed is drawn, the gate's checks as one flow: a word being typed keeps its colour before and
    /// after the reparse, a key draws the editor once, brackets take turns in three colours by depth, a selection from
    /// a line's start leaves the line above unpainted, and an input method's window opens on the caret's line and
    /// follows it, press and hold puts the accent chosen at every caret, and a composition moves the text after it
    /// aside.</summary>
    [Gate]
    [AvaloniaFact]
    public void What_is_typed_is_drawn_once_in_its_colours_and_an_input_method_writes_where_the_caret_is()
    {
        Flow.StepApart<EditingBehaviourTests>("a word being typed keeps its colour before the reparse lands and after", t => t.A_word_being_typed_keeps_its_colour_before_the_reparse_lands_and_after());
        Flow.StepApart<EditingBehaviourTests>("a key draws the editor once and a parse that changes a colour on screen draws it", t => t.A_key_draws_the_editor_once_and_a_parse_that_changes_a_colour_on_screen_draws_it());
        Flow.StepApart<EditingBehaviourTests>("brackets take turns in three colours by how deeply they nest and a comparison is not one", t => t.Brackets_take_turns_in_three_colours_by_how_deeply_they_nest_and_a_comparison_is_not_one());
        Flow.StepApart<EditingBehaviourTests>("a selection from the start of a line leaves the line above it unpainted", t => t.A_selection_from_the_start_of_a_line_leaves_the_line_above_it_unpainted());
        Flow.StepApart<EditingBehaviourTests>("the input method's window opens on the caret's line below a fold and far along a long line and follows the caret", t => t.The_input_methods_window_opens_on_the_carets_line_below_a_fold_and_far_along_a_long_line_and_follows_the_caret());
        Flow.StepApart<EditingBehaviourTests>("press and hold replaces the letter held with the accent chosen at every caret", t => t.Press_and_hold_replaces_the_letter_held_with_the_accent_chosen_at_every_caret());
        Flow.StepApart<EditingBehaviourTests>("a composition moves the text after it aside rather than covering it and goes in as typed", t => t.A_composition_moves_the_text_after_it_aside_rather_than_covering_it_and_goes_in_as_typed());
    }

    /// <summary>Occurrences taken from a caret leave longer names holding the word alone, a brace typed above the one
    /// Enter wrote types over it unless a block around lacks its own, Tab steps over a closer the typing help wrote, Cyclic
    /// Expand Word finishes a word begun as the text's words, Paste as Plain Text pastes what Paste reindents as it is, and
    /// a control character shows its mark two columns wide, a line or paragraph separator its mark one.</summary>
    [AvaloniaFact]
    public void Words_and_braces_typed_go_where_a_person_means()
    {
        Flow.StepApart<EditingBehaviourTests>("occurrences of a word taken from a caret leave longer names holding it alone", t => t.Occurrences_of_a_word_taken_from_a_caret_leave_longer_names_holding_it_alone());
        Flow.StepApart<EditingBehaviourTests>("a brace typed above the one enter wrote types over it unless a block around lacks its own", t => t.A_brace_typed_above_the_one_enter_wrote_types_over_it_unless_a_block_around_lacks_its_own());
        Flow.StepApart<EditingBehaviourTests>("tab steps over a closer the typing help wrote and indents before one the person wrote", t => t.Tab_steps_over_a_closer_the_typing_help_wrote_and_indents_before_one_the_person_wrote());
        Flow.StepApart<EditingBehaviourTests>("cyclic expand word finishes a word begun as the nearest above then steps on and back below and undoes at once", t => t.Cyclic_expand_word_finishes_a_word_begun_as_the_nearest_above_then_steps_on_and_back_below_and_undoes_at_once());
        Flow.StepApart<EditingBehaviourTests>("paste as plain text pastes the clipboard as it is where paste reindents it", t => t.Paste_as_plain_text_pastes_the_clipboard_as_it_is_where_paste_reindents_it());
        Flow.StepApart<EditingBehaviourTests>("statements and elements move by the syntax and unwrap takes a body out each one step of undo", t => t.Statements_and_elements_move_by_the_syntax_and_unwrap_takes_a_body_out_each_one_step_of_undo());
        Flow.StepApart<EditingBehaviourTests>("extend selection in xaml grows through the markup around the caret never a line and shrink takes it back", t => t.Extend_selection_in_xaml_grows_through_the_markup_around_the_caret_never_a_line_and_shrink_takes_it_back());
        Flow.StepApart<EditingBehaviourTests>("a xaml file's elements outline in structure and file structure by what names them and follow its typing", t => t.A_xaml_files_elements_outline_in_structure_and_file_structure_by_what_names_them_and_follow_its_typing());
        Flow.StepApart<EditingBehaviourTests>("split line transpose delete to line ends overwrite and column mode act on their keys", t => t.Split_line_transpose_delete_to_line_ends_overwrite_and_column_mode_act_on_their_keys());
        Flow.StepApart<EditingBehaviourTests>("a control character shows its mark two columns wide a separator typed its mark one and the line goes on past each", t => t.A_control_character_shows_its_mark_two_columns_wide_a_separator_typed_its_mark_one_and_the_line_goes_on_past_each());
    }

    /// <summary>A form feed is drawn as ^L, two columns, where it ended the line it stood on, the text after it unseen: the
    /// letter after it stands where the next line's fourth letter does, the line's end past it, and Right steps over it whole.
    /// A line separator and a paragraph separator typed between letters are drawn as one column each, where each ended the
    /// line's drawing: the letters typed after them stand where the third and fifth letters below do, the line's end past them.</summary>
    private void A_control_character_shows_its_mark_two_columns_wide_a_separator_typed_its_mark_one_and_the_line_goes_on_past_each()
    {
        EditorRig rig = new("a\fb\nabcd\n\nabcde");
        Ui.Settle();
        double after = rig.Editor.PointOf(2).X;
        double below = rig.Editor.PointOf(7).X;
        Assert.True(Math.Abs(after - below) < 0.5, $"The b after a form feed stands at {after}, not at {below} where the fourth letter below does, so its mark is not two columns wide");
        Assert.True(rig.Editor.PointOf(3).X > after + 1, "The line's end stands no further than the b after its form feed, so the line was drawn only to the form feed");
        rig.PlaceCaret(1);
        rig.Press("RIGHT");
        Assert.True(rig.Caret == 2, $"Right from before the form feed left the caret at {rig.Caret}, not past the character whole");

        rig.PlaceCaret(9);
        rig.Type("a\u2028b\u2029c");
        Assert.True(rig.Text == "a\fb\nabcd\na\u2028b\u2029c\nabcde", $"Typing letters and separators into the empty line left {System.Text.Json.JsonSerializer.Serialize(rig.Text)}");
        foreach ((int typed, int under, string what) in new[] { (11, 17, "b after the line separator"), (13, 19, "c after the paragraph separator") })
        {
            double at = rig.Editor.PointOf(typed).X;
            double letter = rig.Editor.PointOf(under).X;
            Assert.True(Math.Abs(at - letter) < 0.5, $"The {what} stands at {at}, not at {letter} where the letter below of its column does, so the line was drawn only to a separator or its mark is not one column");
        }
        Assert.True(rig.Editor.PointOf(14).X > rig.Editor.PointOf(13).X + 1, "The line's end stands no further than the c typed after its paragraph separator, so the line was drawn only to a separator");
    }

    /// <summary>Alt+/ after "to" finishes it as the words above, nearest first, then those below from the end; the word as
    /// typed comes round again, Undo takes the steps back at once, and Alt+Shift+/ goes the other way, the nearest below first.</summary>
    private void Cyclic_expand_word_finishes_a_word_begun_as_the_nearest_above_then_steps_on_and_back_below_and_undoes_at_once()
    {
        const string Text = "total = 1;\ntoken = 2;\nto\ntopmost = 3;\ntoday = 4;\n";
        EditorRig rig = new(Text);
        int after = Text.IndexOf("\nto\n", StringComparison.Ordinal) + 3;
        rig.PlaceCaret(after);
        List<string> stepped = [];
        for (int i = 0; i < 5; i++)
        {
            rig.Press("alt SLASH");
            stepped.Add(rig.Text.Split('\n')[2]);
        }
        Assert.Equal(["token", "total", "today", "topmost", "to"], stepped);
        rig.Press("alt SLASH");
        rig.Press("alt SLASH");
        Assert.Equal("total", rig.Text.Split('\n')[2]);
        rig.Press("meta Z");
        Assert.True(rig.Text == Text, $"Undo after Cyclic Expand Word's steps left \"{rig.Text.Split('\n')[2]}\", not the word as typed");
        rig.PlaceCaret(after);
        rig.Press("alt shift SLASH");
        Assert.Equal("topmost", rig.Text.Split('\n')[2]);
    }

    /// <summary>A copy holding a tab-indented line pasted at a line's start: Paste writes the tab as the editor's four spaces,
    /// Paste as Plain Text the tab as it was copied.</summary>
    private void Paste_as_plain_text_pastes_the_clipboard_as_it_is_where_paste_reindents_it()
    {
        EditorRig rig = new("one\n\tb\n");
        rig.Editor.Selections = SelectionSet.Single(new Selection(0, 6));
        rig.Press("meta C");
        rig.PlaceCaret(7);
        rig.Press("meta alt shift V");
        Ui.Settle();
        Assert.Equal("one\n\tb\none\n\tb", rig.Text);
        rig.Press("meta Z");
        rig.Press("meta V");
        Ui.Settle();
        Assert.Equal("one\n\tb\none\n    b", rig.Text);
    }

    private void Carets_cloned_down_the_lines_type_together_and_undo_as_one()
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
    private void Undoing_a_typed_word_takes_it_back_in_one_change_and_redoing_makes_it_again_in_one()
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
    private void Thousands_of_keys_keep_an_undo_history_within_its_bound_that_still_undoes_their_newest_thousands_of_steps()
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

    private void Select_next_occurrence_picks_the_word_then_each_copy_and_typing_replaces_them_all()
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

    private void Occurrences_of_a_word_taken_from_a_caret_leave_longer_names_holding_it_alone()
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

    private void Joining_three_selected_lines_whose_middle_one_is_only_spaces_gives_one_line_and_leaves_the_next_alone()
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

    private void The_comment_chords_add_and_remove_comments_and_the_toggle_undoes_itself()
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

    private void Copy_with_nothing_selected_takes_the_line_and_paste_puts_it_back_above()
    {
        EditorRig rig = new("first\nsecond");
        rig.PlaceCaret(8);
        rig.Press("meta C");
        rig.Press("meta V");
        Ui.Settle();
        Assert.Equal("first\nsecond\nsecond", rig.Text);
        Assert.Equal(new TextPosition(2, 2), rig.Editor.CaretPosition);
    }

    private void Option_drag_selects_a_column_and_typing_fills_every_row()
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

    private void A_word_being_typed_keeps_its_colour_before_the_reparse_lands_and_after()
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
    private void A_key_draws_the_editor_once_and_a_parse_that_changes_a_colour_on_screen_draws_it()
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

    private void Brackets_take_turns_in_three_colours_by_how_deeply_they_nest_and_a_comparison_is_not_one()
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

    private void A_selection_from_the_start_of_a_line_leaves_the_line_above_it_unpainted()
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

    private void The_input_methods_window_opens_on_the_carets_line_below_a_fold_and_far_along_a_long_line_and_follows_the_caret()
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

    private void Press_and_hold_replaces_the_letter_held_with_the_accent_chosen_at_every_caret()
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

    private void A_composition_moves_the_text_after_it_aside_rather_than_covering_it_and_goes_in_as_typed()
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
    /// <summary>Move Statement moves a statement past the one beside it, its comment with it, and into and out of an if's
    /// block; Move Element moves a C# argument, a JSON array's element and a XAML attribute; Unwrap takes out the if, or the
    /// loop chosen around it, a verbatim string's lines kept as they are; each one step of Undo; and overwrite holds (#2308).</summary>
    private void Statements_and_elements_move_by_the_syntax_and_unwrap_takes_a_body_out_each_one_step_of_undo()
    {
        const string Source = "class Shop\n{\n    void Sell(int a, int b)\n    {\n        // first\n        A();\n\n        B(a, b);\n        if (a > b)\n        {\n            C();\n        }\n    }\n\n" +
            "    void Count(int[] all)\n    {\n        foreach (int each in all)\n        {\n            if (each > 0)\n            {\n                D(@\"\n                    raw\");\n            }\n        }\n    }\n}\n";
        (Passenger.App.MainWindow window, Passenger.Editor.CodeEditor editor) = OpenCSharp(Source);
        try
        {
            editor.Selections = SelectionSet.Single(Source.IndexOf("A();", StringComparison.Ordinal) + 1);
            Ui.Run(window, Passenger.App.AppActions.MoveStatementDown);
            string moved = Source.Replace("        // first\n        A();\n\n        B(a, b);\n", "        B(a, b);\n\n        // first\n        A();\n", StringComparison.Ordinal);
            Assert.True(editor.Document.Snapshot.ToString() == moved, $"Move Statement Down did not move A(); and its comment past B(a, b);:\n{editor.Document.Snapshot}");
            Assert.Equal(moved.IndexOf("A();", StringComparison.Ordinal) + 1, editor.Selections.Primary.Caret);
            Ui.Run(window, Passenger.App.AppActions.MoveStatementUp);
            Assert.Equal(Source, editor.Document.Snapshot.ToString());
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Assert.True(editor.Document.Snapshot.ToString() == moved, "Undo after Move Statement Up took more or less than the move back");

            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            int a = Source.IndexOf("B(a", StringComparison.Ordinal) + 2;
            editor.Selections = SelectionSet.Single(a);
            Ui.Run(window, Passenger.App.AppActions.MoveElementRight);
            Assert.Equal(Source.Replace("B(a, b)", "B(b, a)", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Assert.Equal(a + 3, editor.Selections.Primary.Caret);
            Ui.Run(window, Passenger.App.AppActions.MoveElementLeft);
            Assert.Equal(Source, editor.Document.Snapshot.ToString());

            editor.Selections = SelectionSet.Single(Source.IndexOf("C();", StringComparison.Ordinal));
            Ui.Run(window, Passenger.App.AppActions.Unwrap);
            Assert.Equal(Source.Replace("        if (a > b)\n        {\n            C();\n        }\n", "        C();\n", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Assert.Equal(Source, editor.Document.Snapshot.ToString());

            const string Above = "        B(a, b);\n        if (a > b)\n        {\n            C();\n        }\n";
            editor.Selections = SelectionSet.Single(Source.IndexOf("B(a, b)", StringComparison.Ordinal));
            Ui.Run(window, Passenger.App.AppActions.MoveStatementDown);
            string into = Source.Replace(Above, "        if (a > b)\n        {\n            B(a, b);\n            C();\n        }\n", StringComparison.Ordinal);
            Assert.True(editor.Document.Snapshot.ToString() == into, $"Move Statement Down did not move B(a, b); into the if's block below it:\n{editor.Document.Snapshot}");
            Ui.Run(window, Passenger.App.AppActions.MoveStatementDown);
            Ui.Run(window, Passenger.App.AppActions.MoveStatementDown);
            string outOf = Source.Replace(Above, "        if (a > b)\n        {\n            C();\n        }\n        B(a, b);\n", StringComparison.Ordinal);
            Assert.True(editor.Document.Snapshot.ToString() == outOf, $"Move Statement Down did not move B(a, b); out of the if's block at its end:\n{editor.Document.Snapshot}");
            Assert.Equal("B(a, b);", editor.Document.Snapshot.GetLineText(editor.Document.Snapshot.GetLineFromOffset(editor.Selections.Primary.Caret)).Trim());
            Ui.Run(window, Passenger.App.AppActions.MoveStatementUp);
            Assert.Equal(Source.Replace(Above, "        if (a > b)\n        {\n            C();\n            B(a, b);\n        }\n", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Assert.Equal(Source, editor.Document.Snapshot.ToString());

            editor.Selections = SelectionSet.Single(Source.IndexOf("D(@", StringComparison.Ordinal));
            Ui.Run(window, Passenger.App.AppActions.MoveStatementDown);
            Assert.Equal(Source.Replace("            {\n                D(@\"\n                    raw\");\n            }\n", "            {\n            }\n            D(@\"\n                    raw\");\n",
                StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            editor.Selections = SelectionSet.Single(Source.IndexOf("D(@", StringComparison.Ordinal));
            Ui.Run(window, Passenger.App.AppActions.Unwrap);
            Assert.Equal(["Unwrap 'if'", "Unwrap 'foreach'"], window.Assist.Offered);
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            Assert.Equal(Source.Replace("        foreach (int each in all)\n        {\n            if (each > 0)\n            {\n                D(@\"\n                    raw\");\n            }\n        }\n",
                "        if (each > 0)\n        {\n            D(@\"\n                    raw\");\n        }\n", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Assert.Equal(Source, editor.Document.Snapshot.ToString());

            string folder = Path.GetDirectoryName(window.EditorArea.Active!.Path)!;
            const string Json = "{ \"sizes\": [\"small, round\", 2, [3, 4]] }\n";
            File.WriteAllText(Path.Combine(folder, "sizes.json"), Json);
            window.OpenFile(Path.Combine(folder, "sizes.json"));
            Ui.Settle();
            CodeEditor json = window.EditorArea.CodeEditor;
            json.Focus();
            json.Selections = SelectionSet.Single(Json.IndexOf("small", StringComparison.Ordinal));
            Ui.Run(window, Passenger.App.AppActions.MoveElementRight);
            Assert.Equal("{ \"sizes\": [2, \"small, round\", [3, 4]] }\n", json.Document.Snapshot.ToString());
            const string Xaml = "<StackPanel xmlns=\"https://github.com/avaloniaui\">\n    <Button Width=\"10\" Height=\"20\" />\n</StackPanel>\n";
            File.WriteAllText(Path.Combine(folder, "Panel.axaml"), Xaml);
            window.OpenFile(Path.Combine(folder, "Panel.axaml"));
            Ui.Settle();
            CodeEditor xaml = window.EditorArea.CodeEditor;
            xaml.Focus();
            xaml.Selections = SelectionSet.Single(Xaml.IndexOf("Width", StringComparison.Ordinal) + 2);
            Ui.Run(window, Passenger.App.AppActions.MoveElementRight);
            Assert.Equal(Xaml.Replace("Width=\"10\" Height=\"20\"", "Height=\"20\" Width=\"10\"", StringComparison.Ordinal), xaml.Document.Snapshot.ToString());

            Assert.True(window.Perform(Passenger.App.AppActions.SplitRight));
            Ui.Settle();
            Ui.Run(window, Passenger.Editor.EditorActions.ToggleOverwrite);
            Assert.True(window.EditorArea.Groups.Count == 2 && window.EditorArea.Groups.All(g => g.Editor.Overwrite),
                "Insert turned overwrite on in the editor with the keys alone, where it is the window's");
            Ui.Run(window, Passenger.Editor.EditorActions.ToggleOverwrite);
            Assert.True(window.EditorArea.Groups.All(g => !g.Editor.Overwrite), "Insert again left overwrite on in an editor of the window");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Extend Selection in XAML grows through the markup around the caret, never a line: a markup extension's
    /// argument, what its braces hold, the attribute's value, the attribute, the start tag's attributes, the element, its
    /// parent and the whole text; Shrink Selection takes each back in turn.</summary>
    private void Extend_selection_in_xaml_grows_through_the_markup_around_the_caret_never_a_line_and_shrink_takes_it_back()
    {
        const string Xaml = "<StackPanel xmlns=\"https://github.com/avaloniaui\">\n    <TextBlock Text=\"{Binding Name, Mode=OneWay}\" Margin=\"4\" />\n</StackPanel>\n";
        string folder = TestFolders.Create("selection-");
        File.WriteAllText(Path.Combine(folder, "Panel.axaml"), Xaml);
        Passenger.App.MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile("Panel.axaml");
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            editor.Selections = SelectionSet.Single(Xaml.IndexOf("Name,", StringComparison.Ordinal) + 1);
            string element = "<TextBlock Text=\"{Binding Name, Mode=OneWay}\" Margin=\"4\" />";
            string[] grown = ["Name", "Binding Name, Mode=OneWay", "{Binding Name, Mode=OneWay}", "Text=\"{Binding Name, Mode=OneWay}\"",
                "Text=\"{Binding Name, Mode=OneWay}\" Margin=\"4\"", element, Xaml.TrimEnd('\n'), Xaml];
            string Selected() => editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length);
            foreach (string next in grown)
            {
                Ui.Run(window, Passenger.App.AppActions.ExtendSelection);
                Assert.True(Selected() == next, $"Extend Selection grew to \"{Selected()}\" where the markup around it is \"{next}\"");
            }
            for (int i = grown.Length - 2; i >= 0; i--)
            {
                Ui.Run(window, Passenger.App.AppActions.ShrinkSelection);
                Assert.True(Selected() == grown[i], $"Shrink Selection took the selection back to \"{Selected()}\", not \"{grown[i]}\"");
            }
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A XAML file's elements outline in Structure and File Structure as its markup nests them, each by what names it
    /// with its type after (the root by its class, a resource by its key, an element by its name), another by its type and a
    /// property element as written; Structure follows an element typed, and File Structure narrows to a name and goes to it.</summary>
    private void A_xaml_files_elements_outline_in_structure_and_file_structure_by_what_names_them_and_follow_its_typing()
    {
        const string Xaml = "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"Shop.MainWindow\">\n"
            + "    <Window.Resources>\n        <SolidColorBrush x:Key=\"Accent\" Color=\"Teal\" />\n    </Window.Resources>\n"
            + "    <StackPanel>\n        <TextBlock Text=\"Shop\" />\n        <Button x:Name=\"Save\" Content=\"Save\" />\n    </StackPanel>\n</Window>\n";
        string folder = TestFolders.Create("outline-");
        File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), Xaml);
        Passenger.App.MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        try
        {
            window.OpenFile("MainWindow.axaml");
            Ui.Settle();
            Ui.Run(window, Passenger.App.AppActions.ShowStructure);
            Passenger.App.ToolWindows.StructureView structure = window.StructureView;
            Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "Save"), () => $"the window's elements in Structure: {Listed(structure.Rows)} ({structure.Said})");
            (string Name, int Depth, string? Detail)[] nested = [("Shop.MainWindow", 0, ": Window"), ("Window.Resources", 1, null), ("Accent", 2, ": SolidColorBrush"),
                ("StackPanel", 1, null), ("TextBlock", 2, null), ("Save", 2, ": Button")];
            Assert.Equal(nested, structure.Rows.Select(r => (r.Name, r.Depth, r.Detail)));

            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.Press(window, "ESCAPE");
            Ui.PlaceAfter(editor, "<TextBlock Text=\"Shop\" />");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("<CheckBox Name=\"Fresh\" />");
            Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "Fresh"), () => $"Structure to follow the element typed: {Listed(structure.Rows)}");
            Assert.Equal([("TextBlock", 2), ("Fresh", 2), ("Save", 2)], structure.Rows.Skip(4).Select(r => (r.Name, r.Depth)));

            Ui.Run(window, Passenger.App.AppActions.FileStructure);
            Passenger.App.Navigation.SymbolPopup popup = Assert.IsType<Passenger.App.Navigation.SymbolPopup>(window.Popup);
            Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Name == "Save"), () => $"the window's elements in File Structure: {Listed(popup.Rows)}");
            window.KeyTextInput("Save");
            Ui.WaitFor(() => popup.IsSettled && popup.Rows.Count > 0 && popup.Rows[^1].Name == "Save", () => $"File Structure filtered to Save, not [{Listed(popup.Rows)}]");
            for (int i = 0; i < popup.Rows.Count && popup.Selected?.Name != "Save"; i++)
            {
                // The elements it is under come first, for where the match is.
                Ui.Press(window, "DOWN");
            }
            Ui.Press(window, "ENTER");
            Assert.Contains("<Button x:Name=\"Save\"", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }

        static string Listed(IEnumerable<Passenger.App.Navigation.SymbolRow> rows) => string.Join(", ", rows.Select(r => $"{new string(' ', r.Depth)}{r.Name}"));
    }

    /// <summary>Split Line breaks the line leaving the caret before the break, Transpose swaps the characters around it, Delete
    /// to Line Start and End take the line's text either side of it, Insert types over the text, and Column Selection Mode
    /// makes Shift's moves select a column, each on its keys in VS Code's or IntelliJ IDEA's macOS keymap (#2308).</summary>
    private void Split_line_transpose_delete_to_line_ends_overwrite_and_column_mode_act_on_their_keys()
    {
        EditorRig rig = new("first second\nthird fourth\nfifth sixth");
        rig.Editor.Keymap = DefaultKeymaps.VSCodeMac();
        rig.PlaceCaret(5);
        rig.Press("control O");
        Assert.True(rig.Text == "first\nsecond\nthird fourth\nfifth sixth" && rig.Caret == 5, $"Split Line left \"{rig.Text}\" with the caret at {rig.Caret}");
        rig.Press("meta Z");
        rig.PlaceCaret(2);
        rig.Press("control T");
        Assert.True(rig.Text.StartsWith("fris", StringComparison.Ordinal) && rig.Caret == 3, $"Transpose left \"{rig.Text}\" with the caret at {rig.Caret}");
        rig.Press("meta Z");
        rig.PlaceCaret(6);
        rig.Press("control K");
        Assert.Equal("first \nthird fourth\nfifth sixth", rig.Text);
        rig.Press("meta BACK_SPACE");
        Assert.Equal("\nthird fourth\nfifth sixth", rig.Text);
        rig.Press("meta Z");
        rig.Press("meta Z");
        rig.PlaceCaret(0);
        rig.Press("INSERT");
        rig.Type("FIRST");
        Assert.Equal("FIRST second\nthird fourth\nfifth sixth", rig.Text);
        rig.Press("INSERT");
        rig.Type("!");
        Assert.Equal("FIRST! second\nthird fourth\nfifth sixth", rig.Text);

        rig.Editor.Keymap = DefaultKeymaps.IntelliJMac();
        rig.PlaceCaret(1);
        rig.Press("meta shift 8");
        rig.Press("shift DOWN");
        rig.Press("shift RIGHT");
        Assert.True(rig.Editor.Selections.Items.Select(s => rig.Text.Substring(s.Start, s.Length)).SequenceEqual(["I", "h"]),
            $"Shift+Down and Shift+Right in Column Selection Mode chose [{string.Join(", ", rig.Editor.Selections.Items.Select(s => rig.Text.Substring(s.Start, s.Length)))}], where they choose a column");
    }

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

    /// <summary>In C#, a '}' typed on a line of its own above the '}' that '{' and Enter wrote types over it, the line it was
    /// typed on taken away, so the block keeps one brace; where a block around it lacks its own '}', the parse pairs the
    /// written one with the block typed in, and the '}' typed is the one the code lacks, put in.</summary>
    private void A_brace_typed_above_the_one_enter_wrote_types_over_it_unless_a_block_around_lacks_its_own()
    {
        EditorRig rig = new("class C\n{\n    void M()\n    \n}\n");
        rig.Editor.Highlighter = new Passenger.Languages.CSharp.CSharpHighlighter(rig.Document);
        Ui.Settle();
        rig.PlaceCaret("class C\n{\n    void M()\n    ".Length);
        rig.Type("{");
        rig.Press("ENTER");
        rig.Type("x();");
        rig.Press("ENTER");
        rig.Type("}");
        const string Closed = "class C\n{\n    void M()\n    {\n        x();\n    }";
        Assert.Equal(Closed + "\n}\n", rig.Text);
        Assert.Equal(Closed.Length, rig.Caret);

        const string Lacking = "class C\n{\n    void M()\n    {\n        if (b)\n        {\n            x();\n            ";
        EditorRig open = new(Lacking + "\n    }\n}\n");
        open.Editor.Highlighter = new Passenger.Languages.CSharp.CSharpHighlighter(open.Document);
        Ui.Settle();
        open.PlaceCaret(Lacking.Length);
        open.Type("}");
        Assert.Equal(Lacking + "}\n    }\n}\n", open.Text);
    }

    /// <summary>Tab steps over the quote and the bracket the typing help wrote after the ones typed, the caret still inside
    /// each pair; before a bracket the person wrote, after the caret left the pair, or with the setting off, it indents.</summary>
    private void Tab_steps_over_a_closer_the_typing_help_wrote_and_indents_before_one_the_person_wrote()
    {
        EditorRig rig = new("f\n");
        rig.PlaceCaret(1);
        rig.Type("(");
        rig.Type("\"");
        rig.Type("a");
        rig.Press("TAB");
        rig.Press("TAB");
        Assert.True((rig.Text, rig.Caret) == ("f(\"a\")\n", 6), $"Tab twice left {rig.Text.ReplaceLineEndings("⏎")} with the caret at {rig.Caret}, where it steps over the quote and the bracket to 6");

        EditorRig written = new("g(b)\n");
        written.PlaceCaret(3);
        written.Press("TAB");
        EditorRig left = new("h\n");
        left.PlaceCaret(1);
        left.Type("(");
        left.Press("LEFT");
        left.Press("RIGHT");
        left.Press("TAB");
        EditorRig off = new("k\n");
        off.Editor.TabStepsOverClosers = false;
        off.PlaceCaret(1);
        off.Type("(");
        off.Press("TAB");
        Assert.True(written.Text != "g(b)\n" && left.Text != "h()\n" && off.Text != "k()\n",
            $"Tab left {written.Text.Trim()} before a bracket written by hand, {left.Text.Trim()} once the caret had left the pair and {off.Text.Trim()} with the setting off, "
            + "where each indents");
    }
}
