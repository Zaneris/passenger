using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Vim mode through the window, keys pressed as a keyboard presses them (the key with its character, then the
/// character as text unless the key was taken): the setting turns it on and off live, the status bar says the mode and a
/// command half typed, normal mode's keys (Enter and Tab too) move and delete but never type, a Shift pressed on its own
/// keeps a count, u takes back one x and a whole insert session, Escape leaves insert mode keeping every caret, carets
/// stand on a character in normal mode however they were put there, and an input method composes only in insert mode and
/// on the command line; motions, operators with motions and text objects, registers (the system clipboard's too) and .
/// typed as keys, each change one step of the window's undo and every caret taking it; and a long random session of
/// normal mode's keys checked key by key against a small model of Vim's rules. What each key does is VimTests'.</summary>
public sealed class VimModeTests : IDisposable
{
    private readonly string folder = TestFolders.Create("vim-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Vim_mode_takes_normal_modes_keys_types_only_in_insert_mode_and_says_its_mode()
    {
        (MainWindow window, CodeEditor editor) = Open("one two\nthree\nfour\n");
        try
        {
            Assert.False(window.FindControl<TextBlock>("VimModeText")!.IsVisible, "The status bar shows a Vim mode with Vim mode off");
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Assert.Equal(("NORMAL", "1:1"), Status(window));

            Key(window, "W", "w");
            Key(window, "X", "x");
            Key(window, "X", "x");
            Assert.Equal("one o\nthree\nfour\n", Text(editor));
            Key(window, "U", "u");
            Ui.WaitFor(() => Text(editor) == "one wo\nthree\nfour\n", () => $"u left \"{Text(editor)}\", not the text before the last x alone");

            Key(window, "2", "2");
            // The Shift of Shift+G goes down on its own first, as a person presses it.
            window.KeyPress(Avalonia.Input.Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.LeftShift, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.Settle();
            Assert.Equal("NORMAL 2", Status(window).Mode);
            Key(window, "shift G", "G");
            Assert.Equal(("NORMAL", "2:1"), Status(window));

            Key(window, "shift Q", "Q");
            Ui.Press(window, "ENTER");
            Ui.Press(window, "TAB");
            Key(window, "K", "k");
            Assert.True(Text(editor) == "one wo\nthree\nfour\n", "normal mode's Q, Enter or Tab typed into the text");
            Assert.Equal(("NORMAL", "2:1"), Status(window));

            Key(window, "I", "i");
            Assert.Equal(("INSERT", "2:1"), Status(window));
            Key(window, "shift Z", "Z");
            Key(window, "Z", "z");
            Ui.Press(window, "ESCAPE");
            Assert.Equal("one wo\nZzthree\nfour\n", Text(editor));
            Assert.Equal(("NORMAL", "2:2"), Status(window));

            window.ApplySettings(window.Settings with { VimMode = false });
            Ui.Settle();
            Assert.False(window.FindControl<TextBlock>("VimModeText")!.IsVisible, "The status bar still shows a Vim mode once Vim mode is off");
            Assert.Equal("2:2", Status(window).Caret);
            Key(window, "Q", "q");
            Assert.True(Text(editor) == "one wo\nZqzthree\nfour\n", "with Vim mode turned off, typing typed nothing");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What insert mode types, Enter and Backspace included, is one step of undo however long it took, from the
    /// key that began it (o opening its line too) to Escape; the caret undo puts back stands on a character.</summary>
    [AvaloniaFact]
    public void An_insert_session_is_one_step_of_undo_and_undo_leaves_the_caret_on_a_character()
    {
        (MainWindow window, CodeEditor editor) = Open("alpha\nomega\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Key(window, "O", "o");
            foreach (char typed in "one two")
            {
                Key(window, typed == ' ' ? "SPACE" : char.ToUpperInvariant(typed).ToString(), typed.ToString());
            }
            Ui.Press(window, "ENTER");
            Key(window, "T", "t");
            Key(window, "X", "x");
            Ui.Press(window, "BACK_SPACE");
            Key(window, "O", "o");
            Ui.Press(window, "ESCAPE");
            Assert.Equal("alpha\none two\nto\nomega\n", Text(editor));
            Key(window, "U", "u");
            Ui.WaitFor(() => Text(editor) == "alpha\nomega\n", () => $"u took back only part of the insert session, leaving \"{Text(editor)}\"");
            Key(window, "shift A", "A");
            Key(window, "S", "s");
            Ui.Press(window, "ESCAPE");
            Key(window, "U", "u");
            Ui.WaitFor(() => Text(editor) == "alpha\nomega\n", () => $"u left \"{Text(editor)}\"");
            Assert.Equal(("NORMAL", "1:5"), Status(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Escape leaves insert mode with every caret still where it typed, a second one the keymap's (one caret); a
    /// caret put past a line's end in normal mode (by the IDE, an agent, the keymap) stands on its last character; and an
    /// input method composes in insert mode alone, so normal mode's keys reach it as commands.</summary>
    [AvaloniaFact]
    public void Escape_keeps_every_caret_carets_stand_on_a_character_and_only_insert_mode_composes()
    {
        (MainWindow window, CodeEditor editor) = Open("ab\ncd\n");
        try
        {
            Assert.True(InputMethod.GetIsInputMethodEnabled(editor), "The input method is off with Vim mode off");
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Assert.False(InputMethod.GetIsInputMethodEnabled(editor), "An input method composes in normal mode, whose keys are commands");
            editor.Selections = SelectionSet.Create([Selection.At(0), Selection.At(3)], 0);
            Key(window, "I", "i");
            Assert.True(InputMethod.GetIsInputMethodEnabled(editor), "No input method composes in insert mode");
            Key(window, "X", "x");
            Ui.Press(window, "ESCAPE");
            Assert.Equal("xab\nxcd\n", Text(editor));
            Assert.Equal([0, 4], editor.Selections.Items.Select(s => s.Caret));
            Ui.Press(window, "ESCAPE");
            Assert.Equal(1, editor.Selections.Count);

            editor.Selections = SelectionSet.Single(3);
            Ui.Settle();
            Assert.Equal(("NORMAL", "1:3"), Status(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An editor with a keymap of its own, outside a window's, takes Vim's keys before it as the window's editors
    /// do, undo its own.</summary>
    [AvaloniaFact]
    public void An_editor_with_a_keymap_of_its_own_takes_vims_keys_before_it()
    {
        EditorRig rig = new("one two\nthree\n");
        rig.Editor.UsesVim = true;
        Key(rig.Window, "W", "w");
        Key(rig.Window, "X", "x");
        Key(rig.Window, "I", "i");
        Key(rig.Window, "shift Z", "Z");
        rig.Press("ESCAPE");
        Assert.Equal(("one Zwo\nthree\n", 4), (rig.Text, rig.Caret));
        Key(rig.Window, "U", "u");
        Assert.Equal(("one wo\nthree\n", 4), (rig.Text, rig.Caret));
    }

    [AvaloniaFact]
    public void Motions_typed_move_the_caret_as_vims_do()
    {
        EditorRig rig = new("one (two [three] four)\n\nfive six-seven eight\n");
        rig.Editor.UsesVim = true;
        Keys(rig.Window, "f(");
        Assert.Equal(4, rig.Caret);
        Keys(rig.Window, "%");
        Assert.Equal(21, rig.Caret);
        Keys(rig.Window, "F[;");
        Assert.Equal(9, rig.Caret);
        Keys(rig.Window, "}");
        Assert.Equal(23, rig.Caret);
        Keys(rig.Window, "jWE");
        Assert.Equal(37, rig.Caret);
        Keys(rig.Window, "gge");
        Assert.Equal(2, rig.Caret);
    }

    /// <summary>An operator over a motion or an object, its count either side, and a change with what it typed: each one
    /// step of the window's undo, u taking each back in turn.</summary>
    [AvaloniaFact]
    public void Operators_with_motions_and_objects_change_the_text_each_one_step_of_undo()
    {
        (MainWindow window, CodeEditor editor) = Open("alpha beta gamma delta\n  (one two) three\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "d2w");
            Assert.Equal("gamma delta\n  (one two) three\n", Text(editor));
            Keys(window, "jfociwZED<Esc>");
            Assert.Equal("gamma delta\n  (ZED two) three\n", Text(editor));
            Keys(window, "di(");
            Assert.Equal("gamma delta\n  () three\n", Text(editor));
            Keys(window, ">>kgUiw");
            Assert.Equal("gamma DELTA\n      () three\n", Text(editor));
            string[] undone = ["gamma delta\n      () three\n", "gamma delta\n  () three\n", "gamma delta\n  (ZED two) three\n", "gamma delta\n  (one two) three\n", "alpha beta gamma delta\n  (one two) three\n"];
            foreach (string was in undone)
            {
                Keys(window, "u");
                Ui.WaitFor(() => Text(editor) == was, () => $"u left \"{Text(editor)}\", not \"{was}\": a change was not one step of undo");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A named register yanked into and appended to, then put; the black hole register keeping nothing; and + reaching
    /// the system clipboard both ways.</summary>
    [AvaloniaFact]
    public void Registers_keep_what_was_yanked_and_plus_is_the_system_clipboard()
    {
        (MainWindow window, CodeEditor editor) = Open("one\ntwo\nthree\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "\"ayyj\"Ayy\"_ddG\"ap");
            Assert.Equal("one\nthree\none\ntwo\n", Text(editor));
            Keys(window, "gg\"+yiw");
            Ui.WaitFor(() => Ui.Await(window.Clipboard!.TryGetTextAsync(), "the clipboard's text") == "one", () => "\"+yiw put nothing on the system clipboard");
            Ui.Await(window.Clipboard!.SetTextAsync("pasted\n"), "the clipboard written");
            Keys(window, "\"+P");
            Ui.WaitFor(() => Text(editor) == "pasted\none\nthree\none\ntwo\n", () => $"\"+P put \"{Text(editor)}\", not the clipboard's line above");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Each window keeps its own registers, as each is a session of its own: what one yanks, p in another never
    /// puts, while + still crosses through the system clipboard.</summary>
    [AvaloniaFact]
    public void Each_window_keeps_its_own_registers_and_plus_crosses_between_them()
    {
        (MainWindow first, CodeEditor one) = Open("one\n");
        (MainWindow second, CodeEditor two) = Open("two\n", TestFolders.Create("vim-other-"));
        try
        {
            first.ApplySettings(first.Settings with { VimMode = true });
            second.ApplySettings(second.Settings with { VimMode = true });
            Ui.Settle();
            one.Focus();
            Keys(first, "yy\"ayy\"+yy");
            Ui.WaitFor(() => Ui.Await(first.Clipboard!.TryGetTextAsync(), "the clipboard's text") == "one\n", () => "\"+yy put nothing on the system clipboard");
            two.Focus();
            Keys(second, "p\"ap");
            Assert.True(Text(two) == "two\n", $"A put in one window took another window's yank, leaving \"{Text(two)}\"");
            Keys(second, "\"+p");
            Ui.WaitFor(() => Text(two) == "two\none\n", () => $"\"+p in the other window put \"{Text(two)}\", not the clipboard's line below");
        }
        finally
        {
            second.Close();
            first.Close();
        }
    }

    /// <summary>. makes a change with what its insert typed again at every caret, as one step of undo.</summary>
    [AvaloniaFact]
    public void Dot_repeats_a_change_at_every_caret_as_one_step_of_undo()
    {
        EditorRig rig = new("one two three\nfour five six\n");
        rig.Editor.UsesVim = true;
        rig.Editor.Selections = SelectionSet.Create([Selection.At(0), Selection.At(14)], 1);
        Keys(rig.Window, "cwX<Esc>w");
        Assert.Equal("X two three\nX five six\n", rig.Text);
        Keys(rig.Window, ".");
        Assert.Equal("X X three\nX X six\n", rig.Text);
        Keys(rig.Window, "u");
        Assert.Equal("X two three\nX five six\n", rig.Text);
    }

    /// <summary>v, V and Ctrl+V select as the editor's own selections, a block one selection per line, and the status
    /// bar says which; an operator takes what is selected as one step of undo, which puts back carets in normal mode; a
    /// block's I types on every line it crosses and leaves one caret at its corner.</summary>
    [AvaloniaFact]
    public void Visual_modes_select_as_the_editors_own_selections_and_their_changes_are_one_step_of_undo()
    {
        (MainWindow window, CodeEditor editor) = Open("one two\nthree four\nfive six\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "wvj");
            Assert.Equal("VISUAL", Status(window).Mode);
            Assert.Equal([new Selection(4, 13)], editor.Selections.Items);
            Keys(window, "d");
            Assert.Equal(("one  four\nfive six\n", "NORMAL"), (Text(editor), Status(window).Mode));
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == "one two\nthree four\nfive six\n", () => $"u left \"{Text(editor)}\"");
            Assert.True(editor.Selections.Items is [{ IsEmpty: true, Caret: 4 }] && Status(window).Mode == "NORMAL",
                $"Undo of a visual change put back {string.Join(", ", editor.Selections.Items)} in {Status(window).Mode}, not a caret where the change began in normal mode");

            Keys(window, "V");
            Assert.Equal("VISUAL LINE", Status(window).Mode);
            Assert.Equal([new Selection(0, 8)], editor.Selections.Items);
            Ui.Press(window, "ESCAPE");
            Assert.Equal("NORMAL", Status(window).Mode);

            Ui.Press(window, "control V");
            Keys(window, "jjl");
            Assert.Equal("VISUAL BLOCK", Status(window).Mode);
            Assert.Equal([new Selection(4, 6), new Selection(12, 14), new Selection(23, 25)], editor.Selections.Items);
            Keys(window, "IX<Esc>");
            Assert.Equal("one Xtwo\nthreXe four\nfiveX six\n", Text(editor));
            Assert.True(editor.Selections.Items is [{ IsEmpty: true, Caret: 4 }], $"A block's insert left {string.Join(", ", editor.Selections.Items)}, not one caret at its corner");
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == "one two\nthree four\nfive six\n", () => $"u took back part of the block's insert, leaving \"{Text(editor)}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A drag of the mouse in normal mode enters visual mode over what it selected, whose operators then act on it,
    /// and a click leaves visual mode again.</summary>
    [AvaloniaFact]
    public void A_drag_enters_visual_mode_and_a_click_leaves_it()
    {
        (MainWindow window, CodeEditor editor) = Open("alpha beta\ngamma\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Point At(int offset) => editor.TranslatePoint(editor.PointOf(offset) + new Point(1, editor.LineHeight / 2), window)!.Value;
            Ui.Drag(window, At(0), At(5));
            Assert.Equal("VISUAL", Status(window).Mode);
            Assert.Equal([new Selection(0, 5)], editor.Selections.Items);
            Keys(window, "d");
            Assert.Equal((" beta\ngamma\n", "NORMAL"), (Text(editor), Status(window).Mode));
            Ui.Drag(window, At(1), At(4));
            Assert.Equal("VISUAL", Status(window).Mode);
            Ui.Click(editor, editor.PointOf(8) + new Point(1, editor.LineHeight / 2));
            Assert.True(Status(window).Mode == "NORMAL" && editor.Selections.Items is [{ IsEmpty: true }],
                $"A click left {Status(window).Mode} with {string.Join(", ", editor.Selections.Items)}, not normal mode with a caret");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>R types over the text until Escape, Backspace putting back what it typed over, the status bar saying
    /// REPLACE, and the whole session is one step of undo.</summary>
    [AvaloniaFact]
    public void Replace_mode_types_over_the_text_as_one_step_of_undo()
    {
        (MainWindow window, CodeEditor editor) = Open("one two\nthree\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "wR");
            Assert.Equal("REPLACE", Status(window).Mode);
            Keys(window, "XYZ!<BS>");
            Assert.Equal("one XYZ\nthree\n", Text(editor));
            Keys(window, "<BS><Esc>");
            Assert.Equal(("one XYo\nthree\n", "NORMAL"), (Text(editor), Status(window).Mode));
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == "one two\nthree\n", () => $"u took back part of the replace session, leaving \"{Text(editor)}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>q records the keys typed, insert mode's typing too, into a register, the status bar saying so, and @
    /// presses them again, @@ the last again, each change one step of undo.</summary>
    [AvaloniaFact]
    public void A_macro_recorded_with_q_is_pressed_again_by_at()
    {
        (MainWindow window, CodeEditor editor) = Open("one\ntwo\nthree\nfour\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "qa");
            Assert.Equal("NORMAL recording @a", Status(window).Mode);
            Keys(window, "A!<Esc>j");
            Keys(window, "q");
            Assert.Equal(("one!\ntwo\nthree\nfour\n", "NORMAL"), (Text(editor), Status(window).Mode));
            Keys(window, "@a");
            Assert.Equal("one!\ntwo!\nthree\nfour\n", Text(editor));
            Keys(window, "2@@");
            Assert.Equal("one!\ntwo!\nthree!\nfour!\n", Text(editor));
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == "one!\ntwo!\nthree!\nfour\n", () => $"u after a macro left \"{Text(editor)}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>/ opens a command line in the status bar's place, Enter searches with the editor's own find, its matches
    /// shown and the one reached the current one, n goes on, :noh hides them, and a command not done is said.</summary>
    [AvaloniaFact]
    public void A_search_is_typed_in_the_status_bar_and_shows_its_matches_as_find_does()
    {
        (MainWindow window, CodeEditor editor) = Open("one two\nfoo two\ntwo three\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "/two");
            Assert.Equal("/two", StatusWords(window));
            Keys(window, "<CR>");
            Assert.True(editor.Selections.Primary.Caret == 4 && editor.Highlights.Count == 3 && editor.CurrentHighlight == 0,
                $"/two left the caret at {editor.Selections.Primary.Caret} with {editor.Highlights.Count} matches shown, the current {editor.CurrentHighlight}");
            Assert.NotEqual("/two", StatusWords(window));
            Keys(window, "n");
            Assert.Equal((12, 1), (editor.Selections.Primary.Caret, editor.CurrentHighlight));
            Keys(window, ":noh<CR>");
            Assert.Empty(editor.Highlights);
            Keys(window, ":zz<CR>");
            Assert.Equal("Vim mode does not do :zz", StatusWords(window));
            Keys(window, "/zzz<CR>");
            Assert.Equal("E486: Pattern not found: zzz", StatusWords(window));
            Assert.Equal(12, editor.Selections.Primary.Caret);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>In the smallest window a command line longer than the status bar is cut at its start, so its end, where it
    /// is typed, stays in sight; once it closes, what the bar says is cut at its end again.</summary>
    [AvaloniaFact]
    public void A_command_line_longer_than_the_status_bar_keeps_its_end_in_sight()
    {
        (MainWindow window, _) = Open("one two\nfoo two\ntwo three\n");
        try
        {
            window.Width = MainWindow.SmallestWidth;
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, ":%s/two/one two three four five six seven eight nine ten eleven twelve/g");
            Assert.Equal((":%s/two/one two three four five six seven eight nine ten eleven twelve/g", true), (StatusWords(window), Cut(window)));
            Assert.True(Shown(window).EndsWith("twelve/g", StringComparison.Ordinal),
                $"the command line showed \"{Shown(window)}\", out of sight where it is typed");
            Keys(window, "<Esc>:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz<CR>");
            Assert.True(Cut(window) && Shown(window).StartsWith("Vim mode does not do", StringComparison.Ordinal),
                $"what the bar said after the command line read \"{Shown(window)}\", cut at its start");
        }
        finally
        {
            window.Close();
        }

        static TextBlock Status(MainWindow window) => window.FindControl<TextBlock>("StatusText")!;
        static bool Cut(MainWindow window) => Status(window).TextLayout.TextLines is [{ HasCollapsed: true }];
        static string Shown(MainWindow window) => string.Concat(Status(window).TextLayout.TextLines[0].TextRuns.Select(r => r.Text.ToString()));
    }

    /// <summary>:s over a range is one step of undo, : in visual mode names its lines, :w saves as Save does, :e opens a
    /// file and :q closes its tab as Close Tab does.</summary>
    [AvaloniaFact]
    public void Ex_commands_substitute_as_one_step_save_open_and_close()
    {
        string other = Path.Combine(folder, "Other.txt");
        File.WriteAllText(other, "other\n");
        (MainWindow window, CodeEditor editor) = Open("one two\nfoo two\ntwo three\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, ":%s/two/2/g<CR>");
            Assert.Equal("one 2\nfoo 2\n2 three\n", Text(editor));
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == "one two\nfoo two\ntwo three\n", () => $"u took back part of :s, leaving \"{Text(editor)}\"");
            Keys(window, "Vj:");
            Assert.Equal((":'<,'>", "NORMAL"), (StatusWords(window), Status(window).Mode));
            Keys(window, "s/o/0/g<CR>");
            Assert.Equal("0ne tw0\nf00 tw0\ntwo three\n", Text(editor));
            Keys(window, ":w<CR>");
            Ui.WaitFor(() => File.ReadAllText(Path.Combine(folder, "One.txt")) == Text(editor), "the file :w saved on disk");
            Keys(window, ":e Other.txt<CR>");
            Ui.WaitFor(() => window.EditorArea.Active?.Path == PathNames.Canonical(other), () => $":e left {window.EditorArea.Active?.Path} shown, not Other.txt");
            Keys(window, ":q<CR>");
            Ui.WaitFor(() => window.EditorArea.Documents.All(d => d.Path != PathNames.Canonical(other)), ":q to close Other.txt's tab");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>= lays out the lines it moves over as Reformat Code lays out a selection (XAML by Passenger's own layout),
    /// one step of undo, the caret on the first line's first non-blank, and . does it again.</summary>
    [AvaloniaFact]
    public void Equals_lays_out_its_lines_as_reformat_code_one_step_of_undo()
    {
        const string Before = "<UserControl xmlns=\"https://github.com/avaloniaui\">\n<StackPanel>\n<Button/>\n</StackPanel>\n</UserControl>\n";
        (MainWindow window, CodeEditor editor) = Open(Before, name: "View.axaml");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "jj==");
            Ui.WaitFor(() => Indent(Lines(editor)[2]) > 0, () => $"== left line 3 as \"{Lines(editor)[2]}\", not laid out inside its panel");
            Assert.Equal("<StackPanel>", Lines(editor)[1]);
            Assert.Equal(("NORMAL", $"3:{Indent(Lines(editor)[2]) + 1}"), Status(window));
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == Before, () => $"u took back part of ==, leaving \"{Text(editor)}\"");
            Keys(window, "k=j");
            Ui.WaitFor(() => Indent(Lines(editor)[1]) > 0 && Indent(Lines(editor)[2]) > Indent(Lines(editor)[1]),
                () => $"=j left \"{Text(editor)}\", its two lines not laid out");
            Assert.Equal(("NORMAL", $"2:{Indent(Lines(editor)[1]) + 1}"), Status(window));
            Keys(window, "j.");
            Ui.WaitFor(() => Indent(Lines(editor)[3]) > 0, () => $". after =j left \"{Text(editor)}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>:action runs any of the IDE's actions by its id or title as its keys would, one that cannot run here saying
    /// why in the status bar as its keys would; = says Reformat Code's own words where it cannot lay out the file; gq wraps
    /// at :set textwidth as one step of undo.</summary>
    [AvaloniaFact]
    public void Action_runs_the_ides_actions_equals_says_why_where_reformat_code_cannot_and_gq_wraps()
    {
        (MainWindow window, CodeEditor editor) = Open("one\ntwo\nthree\nalpha beta gamma delta epsilon\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, ":action Join Lines<CR>");
            Ui.WaitFor(() => Lines(editor)[0] == "one two", () => $":action Join Lines left \"{Text(editor)}\"");
            Keys(window, ":action " + EditorActions.JoinLines + "<CR>");
            Ui.WaitFor(() => Lines(editor)[0] == "one two three", () => $":action by Join Lines' id left \"{Text(editor)}\"");
            Keys(window, ":action Nothing Of The Sort<CR>");
            Assert.Equal(window.Actions.WhyNot("Nothing Of The Sort", window.Actions.Context()), StatusWords(window));
            Keys(window, "==");
            Assert.Equal(window.Actions.WhyNot(AppActions.Reformat, window.Actions.Context()), StatusWords(window));
            Assert.Equal("one two three\nalpha beta gamma delta epsilon\n", Text(editor));
            Keys(window, ":set tw=20<CR>jgqq");
            Assert.Equal("one two three\nalpha beta gamma\ndelta epsilon\n", Text(editor));
            Assert.Equal(("NORMAL", "3:1"), Status(window));
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == "one two three\nalpha beta gamma delta epsilon\n", () => $"u took back part of gq, leaving \"{Text(editor)}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The mappings file in the state folder is read as Vim mode comes on and again as it changes: its mappings
    /// take keys in their modes, the leader it sets among them, a line it cannot read said in the status bar and passed over.</summary>
    [AvaloniaFact]
    public void The_mappings_file_maps_keys_and_is_read_again_as_it_changes()
    {
        string state = Path.Combine(folder, "state");
        Directory.CreateDirectory(state);
        string vimrc = Path.Combine(state, "vimrc");
        File.WriteAllText(vimrc, "let mapleader = \",\"\nnnoremap <leader>d dd\ninoremap jk <Esc>\n");
        (MainWindow window, CodeEditor editor) = Open("one\ntwo\nthree\n", state: state);
        try
        {
            Assert.Equal(PathNames.Canonical(vimrc), PathNames.Canonical(window.VimMappingsPath));
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.WaitFor(() => window.VimMappings.All.Count == 2, "the mappings file read as Vim mode came on");
            Keys(window, ",d", together: true);
            Assert.Equal("two\nthree\n", Text(editor));
            Keys(window, "ix");
            Keys(window, "jk", together: true);
            Assert.Equal(("NORMAL", "1:1"), Status(window));
            Assert.Equal("xtwo\nthree\n", Text(editor));

            File.WriteAllText(vimrc, "nnoremap Q dd\nnmap\n");
            Ui.WaitFor(() => window.VimMappings.All.Count == 1, "the mappings file read again as it changed");
            Assert.Equal("Vim mode passed over 1 line of vimrc, line 2: a mapping names the keys it takes", StatusWords(window));
            Keys(window, "Q");
            Assert.Equal("three\n", Text(editor));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A key that may begin a mapping waits timeoutlen (the mappings file's set line) for the rest, then means
    /// itself: with jk mapped in insert mode, j typed alone types j once the wait passes, and jk typed together leaves insert
    /// mode; with notimeout set, j waits for the next key however long that takes.</summary>
    [AvaloniaFact]
    public void A_key_that_may_begin_a_mapping_means_itself_once_timeoutlen_passes()
    {
        string state = Path.Combine(folder, "state");
        Directory.CreateDirectory(state);
        string vimrc = Path.Combine(state, "vimrc");
        File.WriteAllText(vimrc, "set timeoutlen=300\ninoremap jk <Esc>\n");
        (MainWindow window, CodeEditor editor) = Open("ab\n", state: state);
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.WaitFor(() => window.VimMappings.All.Count == 1, "the mappings file read as Vim mode came on");
            Keys(window, "ij");
            Assert.Equal(("jab\n", "INSERT"), (Text(editor), Status(window).Mode));
            Keys(window, "k");
            Keys(window, "jk", together: true);
            Assert.Equal(("jkab\n", "NORMAL"), (Text(editor), Status(window).Mode));

            File.WriteAllText(vimrc, "set notimeout\ninoremap jk <Esc>\n");
            Ui.WaitFor(() => window.VimMappings.Settings.Contains("notimeout"), "the mappings file read again as it changed");
            Keys(window, "ij");
            Ui.Hold(TimeSpan.FromSeconds(1.5), "longer than any timeoutlen the j could wait");
            Assert.Equal(("jkab\n", "INSERT j"), (Text(editor), Status(window).Mode));
            Keys(window, "k");
            Assert.Equal(("jkab\n", "NORMAL"), (Text(editor), Status(window).Mode));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>:bufdo runs its commands, parted at '|', in each open file in turn as one command of the IDE's undo: :s over
    /// two files and :update saving each, a read-only file passed over and said, the last file left shown, and u there taking
    /// back both; :windo runs its command in each split's file, u taking back both again.</summary>
    [AvaloniaFact]
    public void Bufdo_and_windo_change_every_file_as_one_command_of_undo()
    {
        string decompiled = Path.Combine(folder, "MetadataAsSource");
        Directory.CreateDirectory(decompiled);
        string one = Path.Combine(folder, "One.txt");
        string two = Path.Combine(folder, "Two.txt");
        string three = Path.Combine(decompiled, "Three.txt");
        File.WriteAllText(two, "one more\n");
        File.WriteAllText(three, "one gone\n");
        (MainWindow window, CodeEditor editor) = Open("one two\nthree\n");
        try
        {
            window.OpenFile(three);
            window.OpenFile(two);
            window.OpenFile(one);
            List<string> asked = [];
            window.Confirm = (_, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, ":bufdo %s/one/1/ge | update<CR>");
            Assert.Equal(("1 two\nthree\n", "1 more\n", "one gone\n"), (Shown(window, one), Shown(window, two), Shown(window, three)));
            Ui.WaitFor(() => File.ReadAllText(one) == "1 two\nthree\n" && File.ReadAllText(two) == "1 more\n", "each file :update saved");
            Assert.Equal(("Two.txt", ":bufdo passed over 1 read-only file: Three.txt"), (window.EditorArea.Active!.Name, StatusWords(window)));
            Keys(window, "u");
            Ui.WaitFor(() => Shown(window, one) == "one two\nthree\n" && Shown(window, two) == "one more\n",
                () => $"u after :bufdo left \"{Shown(window, one)}\" and \"{Shown(window, two)}\", not both files as they were");
            Assert.Equal(["Undo :bufdo %s/one/1/ge | update? It changes One.txt and Two.txt."], asked);

            Assert.True(window.Perform(AppActions.SplitRight));
            window.OpenFile(one);
            Ui.Settle();
            Assert.Equal(["One.txt", "Two.txt"], window.EditorArea.Groups.Select(g => g.Active!.Name));
            Keys(window, ":windo 1s/^/> /<CR>");
            Assert.Equal(("> one two\nthree\n", "> one more\n"), (Shown(window, one), Shown(window, two)));
            Keys(window, "u");
            Ui.WaitFor(() => Shown(window, one) == "one two\nthree\n" && Shown(window, two) == "one more\n",
                () => $"u after :windo left \"{Shown(window, one)}\" and \"{Shown(window, two)}\", not both files as they were");
            Assert.Equal("Undo :windo 1s/^/> /? It changes One.txt and Two.txt.", asked[^1]);
        }
        finally
        {
            window.Close();
        }

        static string Shown(MainWindow window, string path) => window.EditorArea.Find(PathNames.Canonical(path))!.Text.Snapshot.ToString();
    }

    /// <summary>:cdo runs its commands at each match Find in Files lists in the Find window, as Vim runs them at each entry
    /// of its quickfix list, as one command of the IDE's undo: :s and :update at each match in each file, u taking back
    /// both files; :cfdo runs its command once in each file at its first match and :cdo at every match, :normal x taking
    /// the character there, the matches after one in a file moving with its edits (Vim 9.1's results, with 'hidden').</summary>
    [AvaloniaFact]
    public void Cdo_and_cfdo_run_at_the_matches_find_in_files_lists_as_one_command_of_undo()
    {
        string one = Path.Combine(folder, "One.txt");
        string two = Path.Combine(folder, "Two.txt");
        File.WriteAllText(two, "one more\n");
        (MainWindow window, CodeEditor editor) = Open("one two\nthree one\n");
        try
        {
            List<string> asked = [];
            window.Confirm = (_, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            window.ApplySettings(window.Settings with { VimMode = true });
            Passenger.App.Navigation.FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Ui.Search(popup, "one");
            Ui.Click(popup.FindWindow);
            Assert.Equal([("One.txt", 0, 0), ("One.txt", 1, 6), ("Two.txt", 0, 0)], window.Find.Places.Select(p => (Path.GetFileName(p.Path), p.Line, p.Column)));
            window.EditorArea.CodeEditor.Focus();
            Ui.Settle();
            Keys(window, ":cdo s/one/1/ | update<CR>");
            Assert.Equal(("1 two\nthree 1\n", "1 more\n", "Two.txt"), (Shown(window, one), Shown(window, two), window.EditorArea.Active!.Name));
            Ui.WaitFor(() => File.ReadAllText(one) == "1 two\nthree 1\n" && File.ReadAllText(two) == "1 more\n", "each file :update saved");
            Keys(window, "u");
            Ui.WaitFor(() => Shown(window, one) == "one two\nthree one\n" && Shown(window, two) == "one more\n",
                () => $"u after :cdo left \"{Shown(window, one)}\" and \"{Shown(window, two)}\", not both files as they were");
            Assert.Equal(["Undo :cdo s/one/1/ | update? It changes One.txt and Two.txt."], asked);

            Keys(window, ":cfdo normal x<CR>");
            Assert.Equal(("ne two\nthree one\n", "ne more\n"), (Shown(window, one), Shown(window, two)));
            Keys(window, "u");
            Ui.WaitFor(() => Shown(window, one) == "one two\nthree one\n" && Shown(window, two) == "one more\n", "u to take back :cfdo in both files");
            Keys(window, ":cdo normal x<CR>");
            Assert.Equal(("ne two\nthree ne\n", "ne more\n"), (Shown(window, one), Shown(window, two)));
        }
        finally
        {
            window.Close();
        }

        static string Shown(MainWindow window, string path) => window.EditorArea.Find(PathNames.Canonical(path))!.Text.Snapshot.ToString();
    }

    /// <summary>Commands joined by '|' on a plain command line run in turn, as Vim's do, and u takes back all they changed
    /// at once; = inside :bufdo lays out each file before the next command runs, so its layout is part of the one command
    /// u takes back in both files.</summary>
    [AvaloniaFact]
    public void Commands_joined_by_a_bar_are_one_change_and_equals_inside_bufdo_is_part_of_its_one_command()
    {
        const string Loose = "<UserControl xmlns=\"https://github.com/avaloniaui\">\n<StackPanel>\n<Button/>\n</StackPanel>\n</UserControl>\n";
        string second = Path.Combine(folder, "Second.axaml");
        File.WriteAllText(second, Loose);
        (MainWindow window, CodeEditor editor) = Open(Loose, name: "First.axaml");
        try
        {
            List<string> asked = [];
            window.Confirm = (_, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, ":2s/Stack/Wrap/ | 4s/Stack/Wrap/<CR>");
            Assert.Equal(Loose.Replace("StackPanel", "WrapPanel", StringComparison.Ordinal), Text(editor));
            Keys(window, "u");
            Assert.Equal(Loose, Text(editor));
            Assert.Empty(asked);

            window.OpenFile(second);
            Ui.Settle();
            Keys(window, ":bufdo normal gg=G<CR>");
            Ui.WaitFor(() => window.EditorArea.Documents.All(d => Indent(d.Text.Snapshot.ToString().Split('\n')[2]) > 0),
                () => $"=G inside :bufdo left [{string.Join(" | ", window.EditorArea.Documents.Select(d => d.Text.Snapshot.ToString().Split('\n')[2]))}], not each file laid out");
            Keys(window, "u");
            Ui.WaitFor(() => window.EditorArea.Documents.All(d => d.Text.Snapshot.ToString() == Loose),
                () => $"u after :bufdo normal gg=G left [{string.Join(" | ", window.EditorArea.Documents.Select(d => d.Text.Snapshot.ToString().Split('\n')[2]))}]");
            Assert.Equal(["Undo :bufdo normal gg=G? It changes First.axaml and Second.axaml."], asked);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file shown read-only refuses Vim mode's every change as it refuses every other, the status bar saying
    /// so in Refusals' words: x, dd, an insert, u and :s leave its text as it was and normal mode as it was, while a motion
    /// and a yank work, the yank put in a file that takes changes.</summary>
    [AvaloniaFact]
    public void A_read_only_file_refuses_vims_every_change_saying_why()
    {
        string decompiled = Path.Combine(folder, "MetadataAsSource");
        Directory.CreateDirectory(decompiled);
        string other = Path.Combine(folder, "Two.txt");
        File.WriteAllText(other, "x\n");
        (MainWindow window, CodeEditor editor) = Open("one two\nthree\n", into: decompiled);
        try
        {
            Assert.True(window.EditorArea.Active!.IsReadOnly, "A file under MetadataAsSource was not shown read-only");
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            foreach (string keys in new[] { "x", "dd", "i", "u", ":s/one/1/<CR>" })
            {
                Keys(window, ":set ic?<CR>");
                Assert.Equal("noignorecase", StatusWords(window));
                Keys(window, keys);
                Assert.True(Text(editor) == "one two\nthree\n" && Status(window).Mode == "NORMAL",
                    $"{keys} on a read-only file left \"{Text(editor)}\" in {Status(window).Mode}");
                Assert.Equal("One.txt is read-only", StatusWords(window));
            }
            Keys(window, "wyw");
            Assert.Equal(("NORMAL", "1:5"), Status(window));
            window.OpenFile(other);
            Ui.Settle();
            Keys(window, "p");
            Assert.Equal("xtwo\n", Text(window.EditorArea.CodeEditor));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>While the command line is open an input method composes on it: what it composes shows at the line's end
    /// in the status bar, its window opening there, never in the text, and a key it takes runs nothing; what it commits,
    /// a dead key's character that comes with no key of its own, and press and hold's accent over the letter held are
    /// typed on the line, the text left as it was until the command runs; closed, the line leaves the keys to Vim's
    /// commands again.</summary>
    [AvaloniaFact]
    public void The_command_line_takes_what_an_input_method_composes_and_commits_and_leaves_the_text_alone()
    {
        (MainWindow window, CodeEditor editor) = Open("alpha 日本 café\nsecond line\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs asked = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.RaiseEvent(asked);
            Avalonia.Input.TextInput.TextInputMethodClient client = asked.Client ?? throw new InvalidOperationException("The editor gave the input method no client");
            Keys(window, "/");
            Assert.True(InputMethod.GetIsInputMethodEnabled(editor), "No input method composes on the command line");
            double after = editor.PointOf(1).X;
            client.SetPreeditText("にほん");
            Ui.Settle();
            Assert.Equal("/にほん", StatusWords(window));
            Assert.True(editor.PointOf(1).X == after, "The composition was drawn in the text at the caret, moving what follows it aside");
            TextBlock words = window.FindControl<TextBlock>("StatusText")!;
            Point end = words.TranslatePoint(new Point(words.TextLayout.WidthIncludingTrailingWhitespace, 0), editor)!.Value;
            Point told = client.CursorRectangle.TopLeft;
            Assert.True(Math.Abs(told.X - end.X) < 0.5 && Math.Abs(told.Y - end.Y) < 0.5,
                $"The input method's window would open at {told}, not at the command line's end in the status bar, {end}");

            window.KeyPress(Avalonia.Input.Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Settle();
            Assert.True(StatusWords(window) == "/にほん" && editor.Selections.Primary.Caret == 0,
                $"The Enter the input method took ran the search, the status bar saying \"{StatusWords(window)}\" and the caret at {editor.Selections.Primary.Caret}");
            client.SetPreeditText(null);
            window.KeyTextInput("日本");
            Ui.Settle();
            Assert.Equal("/日本", StatusWords(window));
            Assert.Equal("alpha 日本 café\nsecond line\n", Text(editor));
            Assert.True(client.CursorRectangle.X > told.X - (words.TextLayout.WidthIncludingTrailingWhitespace / 2), "The input method's window stayed where the composition began, not at the line's end");
            Keys(window, "<CR>");
            Assert.Equal(6, editor.Selections.Primary.Caret);
            Assert.False(InputMethod.GetIsInputMethodEnabled(editor), "An input method still composes once the command line closed");

            Keys(window, ":s/caf");
            window.KeyTextInput("é");
            Ui.Settle();
            Assert.Equal(":s/café", StatusWords(window));
            Keys(window, "/cafe");
            Avalonia.Input.TextInput.TextSelection held = client.Selection;
            client.Selection = new Avalonia.Input.TextInput.TextSelection(held.End - 1, held.End);
            window.KeyTextInput("è");
            Ui.Settle();
            Assert.Equal(":s/café/cafè", StatusWords(window));
            Assert.Equal("alpha 日本 café\nsecond line\n", Text(editor));
            Keys(window, "<CR>");
            Assert.Equal("alpha 日本 cafè\nsecond line\n", Text(editor));
            Keys(window, "x");
            Assert.Equal("lpha 日本 cafè\nsecond line\n", Text(editor));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file that keeps no final line break keeps the last line S empties as a line, the caret standing on it
    /// and j reaching it again, as Vim's 'eol' has it; once saved with the line break, the break ends the file.</summary>
    [AvaloniaFact]
    public void An_emptied_last_line_of_a_file_without_a_final_line_break_is_a_line_to_stand_on()
    {
        (MainWindow window, CodeEditor editor) = Open("one\ntwo");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "jS<Esc>");
            Assert.Equal("one\n", Text(editor));
            Assert.Equal(("NORMAL", "2:1"), Status(window));
            Keys(window, "kj");
            Assert.Equal(("NORMAL", "2:1"), Status(window));
            Keys(window, "ix<Esc>");
            Assert.Equal("one\nx", Text(editor));
            Keys(window, "ddp");
            Assert.True(Text(editor) == "one\nx" && Status(window).Caret == "2:1", $"dd and p of the last line left \"{Text(editor)}\" at {Status(window).Caret}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Ctrl key Settings gives the keymap is the keymap's, Vim mode taking it no more (Ctrl+V entered visual
    /// block mode until then), and Settings' row of Vim mode's Ctrl keys greys while Vim mode is off.</summary>
    [AvaloniaFact]
    public void A_ctrl_key_given_to_the_keymap_in_settings_is_the_keymaps()
    {
        (MainWindow window, CodeEditor editor) = Open("ab\ncd\n");
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.EditorPage;
            Ui.Settle();
            Assert.Equal("Vim mode is off", dialog.WhyGreyed("Vim mode's Ctrl keys"));
            dialog.Close();
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Ui.Press(window, "control V");
            Assert.Equal("VISUAL BLOCK", Status(window).Mode);
            Ui.Press(window, "ESCAPE");
            window.ApplySettings(window.Settings with { VimKeysToKeymap = "<C-v>" });
            Ui.Settle();
            Ui.Press(window, "control V");
            Assert.Equal("NORMAL", Status(window).Mode);
            Assert.Equal("ab\ncd\n", Text(editor));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A mark stays on its text as lines come above it, '' goes back to where a jump left, and Ctrl+O and Ctrl+I
    /// step through the IDE's places, a jump leaving one.</summary>
    [AvaloniaFact]
    public void Marks_move_with_the_text_and_ctrl_o_steps_back_through_the_ides_places()
    {
        (MainWindow window, CodeEditor editor) = Open("l1\nl2\nl3\nl4\nl5\nl6\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "jmaggOx<Esc>G'a");
            Assert.Equal(("x\nl1\nl2\nl3\nl4\nl5\nl6\n", 5), (Text(editor), editor.Selections.Primary.Caret));
            Keys(window, "''");
            Assert.Equal(17, editor.Selections.Primary.Caret);
            Keys(window, "gg");
            Ui.Press(window, "control O");
            Ui.WaitFor(() => editor.Selections.Primary.Caret == 17, () => $"Ctrl+O left the caret at {editor.Selections.Primary.Caret}, not the last line gg left");
            Ui.Press(window, "control I");
            Ui.WaitFor(() => editor.Selections.Primary.Caret == 0, () => $"Ctrl+I left the caret at {editor.Selections.Primary.Caret}, not back at the first line");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A long session of normal mode's keys in an editor, seeded, checked after each key against a model of what
    /// Vim does with them: counts, h l j k 0 $, x X D dd, yy p P, J, ~ and u, visual mode's v d, v y, V d, V j d and V y,
    /// and searches /baz and ?qux with n and N, wrapping; the model knows a string, a caret, the column j and k aim for,
    /// the unnamed register, the last search and the text before each change.</summary>
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_random_normal_mode_session_leaves_what_a_model_of_vims_rules_says(int seed)
    {
        Random random = new(seed);
        VimModel model = new(string.Concat(Enumerable.Range(0, 12).Select(_ => VimModel.Line(random) + "\n")));
        EditorRig rig = new(model.Text);
        rig.Window.Height = 300;
        rig.Editor.UsesVim = true;
        Assert.True(rig.Editor.VimRegisters.Unnamed is null, "Vim mode began with something to put, where Vim begins with its registers empty");
        string[] commands = ["h", "l", "j", "k", "0", "$", "x", "X", "D", "dd", "yy", "p", "P", "J", "~", "u", "vd", "vy", "Vd", "Vjd", "Vy", "/baz<CR>", "?qux<CR>", "n", "N"];
        for (int step = 0; step < 400; step++)
        {
            string command = commands[random.Next(commands.Length)];
            int count = command is "h" or "l" or "j" or "k" or "x" or "X" or "dd" or "~" && random.Next(4) == 0 ? random.Next(2, 4) : 0;
            string keys = (count > 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty) + command;
            if (!model.Press(command, Math.Max(count, 1)))
            {
                continue;
            }
            Keys(rig.Window, keys);
            string where = $"seed {seed}, step {step}, after {keys}";
            Assert.True(rig.DifferenceFrom(model.Text) < 0, $"The text differs from the model's at {rig.DifferenceFrom(model.Text)} ({where}): \"{rig.Text}\" against \"{model.Text}\"");
            Assert.True(rig.Caret == model.Caret, $"The caret stands at {rig.Caret}, the model's at {model.Caret} ({where}) in \"{model.Text}\"");
        }
    }

    /// <summary>Keys in Vim's notation typed one by one as a keyboard types them: a character with the key that types it,
    /// Escape, Enter and Backspace by name.</summary>
    /// <summary>Keys pressed in turn, each let land unless <paramref name="together"/>, where they come as fast as a person
    /// types a mapping's keys, the last alone let land.</summary>
    private static void Keys(TopLevel window, string keys, bool together = false)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            string? named = keys[i] == '<' ? Array.Find(["<Esc>", "<CR>", "<BS>"], n => string.CompareOrdinal(keys, i, n, 0, n.Length) == 0) : null;
            bool settle = !together || i + (named?.Length ?? 1) >= keys.Length;
            if (named is not null)
            {
                Ui.Press(window, named switch { "<Esc>" => "ESCAPE", "<CR>" => "ENTER", _ => "BACK_SPACE" }, settle);
                i += named.Length - 1;
                continue;
            }
            char c = keys[i];
            string key = c switch
            {
                >= 'a' and <= 'z' => char.ToUpperInvariant(c).ToString(),
                >= 'A' and <= 'Z' => "shift " + c,
                >= '0' and <= '9' => c.ToString(),
                ' ' => "SPACE",
                '.' => "PERIOD",
                ',' => "COMMA",
                ';' => "SEMICOLON",
                '[' => "OPEN_BRACKET",
                ']' => "CLOSE_BRACKET",
                '{' => "shift OPEN_BRACKET",
                '}' => "shift CLOSE_BRACKET",
                '(' => "shift 9",
                ')' => "shift 0",
                '%' => "shift 5",
                '$' => "shift 4",
                '"' => "shift QUOTE",
                '>' => "shift PERIOD",
                '<' => "shift COMMA",
                '~' => "shift BACK_QUOTE",
                '_' => "shift MINUS",
                '-' => "MINUS",
                '=' => "EQUALS",
                '+' => "shift EQUALS",
                '!' => "shift 1",
                '/' => "SLASH",
                '?' => "shift SLASH",
                ':' => "shift SEMICOLON",
                '\'' => "QUOTE",
                '`' => "BACK_QUOTE",
                '&' => "shift 7",
                '@' => "shift 2",
                '|' => "shift BACK_SLASH",
                '^' => "shift 6",
                _ => throw new ArgumentException($"No key types '{c}' here", nameof(keys)),
            };
            Ui.Type(window, key, c.ToString(), settle);
        }
    }

    private (MainWindow Window, CodeEditor Editor) Open(string text, string? into = null, string name = "One.txt", string? state = null)
    {
        string path = Path.Combine(into ?? folder, name);
        File.WriteAllText(path, text);
        MainWindow window = state is null ? new() { Width = 1200, Height = 800 } : new() { Width = 1200, Height = 800, StateFolder = state };
        window.Show();
        window.OpenFolder(into ?? folder);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        return (window, editor);
    }

    /// <summary>A key that types pressed as a keyboard presses it, carrying its character.</summary>
    private static void Key(TopLevel window, string key, string typed) => Ui.Type(window, key, typed);

    private static (string Mode, string? Caret) Status(MainWindow window)
    {
        TextBlock mode = window.FindControl<TextBlock>("VimModeText")!;
        return (mode.IsVisible ? mode.Text ?? string.Empty : string.Empty, window.FindControl<TextBlock>("CaretText")!.Text);
    }

    private static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();

    private static string[] Lines(CodeEditor editor) => Text(editor).Split('\n');

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    /// <summary>What the status bar says on its left, where Vim mode's command line stands.</summary>
    private static string? StatusWords(MainWindow window) => window.FindControl<TextBlock>("StatusText")!.Text;

    /// <summary>What Vim does with a few of normal mode's keys, as plainly as it can be said: a string of lines each ending
    /// with a break, a caret, the column j and k aim for, the unnamed register (whole lines or not) and the text and caret
    /// before each change, for u. It passes over a dd that would leave no line.</summary>
    private sealed class VimModel(string text)
    {
        private readonly Stack<(string Text, int Caret)> undo = [];

        private int want;

        private (string Text, bool Lines)? register;

        private (string Word, bool Back)? search;

        public string Text { get; private set; } = text;

        public int Caret { get; private set; }

        public static string Line(Random random)
        {
            string[] words = ["foo", "Bar", "x", "baz", "qux", "Ok"];
            int length = random.Next(0, 5);
            string indent = random.Next(4) == 0 ? "  " : string.Empty;
            return length == 0 ? string.Empty : indent + string.Join(' ', Enumerable.Range(0, length).Select(_ => words[random.Next(words.Length)]));
        }

        public bool Press(string command, int n)
        {
            List<string> lines = [.. Text[..^1].Split('\n')];
            int line = Text[..Caret].Count(c => c == '\n');
            int column = Caret - Start(lines, line);
            int length = lines[line].Length;
            switch (command)
            {
                case "/baz<CR>" or "?qux<CR>":
                    search = (command[1..4], command[0] == '?');
                    return Press("n", 1);
                case "n" or "N":
                    if (search is not { } searched)
                    {
                        break;
                    }
                    bool backward = searched.Back != (command == "N");
                    int behind = Caret > 0 ? Text.LastIndexOf(searched.Word, Math.Min(Text.Length - 1, Caret + searched.Word.Length - 2), StringComparison.Ordinal) : -1;
                    int ahead = Text.IndexOf(searched.Word, Caret + 1, StringComparison.Ordinal);
                    int hit = backward
                        ? behind >= 0 ? behind : Text.LastIndexOf(searched.Word, StringComparison.Ordinal)
                        : ahead >= 0 ? ahead : Text.IndexOf(searched.Word, StringComparison.Ordinal);
                    if (hit >= 0)
                    {
                        int hitLine = Text[..hit].Count(c => c == '\n');
                        Move(lines, hitLine, hit - Start(lines, hitLine));
                    }
                    break;
                case "vd" or "vy" when length == 0:
                    // On an empty line visual mode takes the line break, which the model leaves aside.
                    return false;
                case "vd":
                    return Press("x", 1);
                case "vy":
                    register = (lines[line][column].ToString(), false);
                    Move(lines, line, column);
                    break;
                case "Vd" or "Vjd":
                    int caretWas = Caret;
                    bool done = Press("dd", command == "Vjd" && line < lines.Count - 1 ? 2 : 1);
                    if (done && undo.TryPop(out (string Text, int Caret) was))
                    {
                        // Undo puts the caret back at the start of the lines visual mode took.
                        undo.Push((was.Text, caretWas - column));
                    }
                    return done;
                case "Vy":
                    Press("yy", 1);
                    Move(lines, line, 0);
                    break;
                case "h":
                    Move(lines, line, Math.Max(0, column - n));
                    break;
                case "l":
                    Move(lines, line, Math.Min(Math.Max(length - 1, 0), column + n));
                    break;
                case "0":
                    Move(lines, line, 0);
                    break;
                case "$":
                    Move(lines, line, Math.Max(length - 1, 0));
                    want = int.MaxValue;
                    break;
                case "j" or "k":
                    int to = Math.Clamp(line + (command == "j" ? n : -n), 0, lines.Count - 1);
                    if (to == line)
                    {
                        break;
                    }
                    Caret = Start(lines, to) + Math.Min(want, Math.Max(lines[to].Length - 1, 0));
                    break;
                case "x" or "X" or "D" or "~":
                    int from = command == "X" ? Math.Max(0, column - n) : column;
                    int end = command switch { "X" => column, "D" => length, _ => Math.Min(length, column + n) };
                    if (end <= from)
                    {
                        break;
                    }
                    string taken = lines[line][from..end];
                    Changed();
                    if (command == "~")
                    {
                        lines[line] = lines[line][..from] + new string([.. taken.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]) + lines[line][end..];
                        Set(lines, line, Math.Min(end, Math.Max(lines[line].Length - 1, 0)));
                        break;
                    }
                    register = (taken, false);
                    lines[line] = lines[line][..from] + lines[line][end..];
                    Set(lines, line, Math.Min(from, Math.Max(lines[line].Length - 1, 0)));
                    break;
                case "dd" or "yy":
                    if (n > 1 && line == lines.Count - 1)
                    {
                        break;
                    }
                    int last = Math.Min(line + n - 1, lines.Count - 1);
                    if (command == "dd" && line == 0 && last == lines.Count - 1)
                    {
                        return false;
                    }
                    register = (string.Concat(lines.Skip(line).Take(last - line + 1).Select(l => l + "\n")), true);
                    if (command == "yy")
                    {
                        break;
                    }
                    Changed();
                    lines.RemoveRange(line, last - line + 1);
                    int now = Math.Min(line, lines.Count - 1);
                    Set(lines, now, FirstNonBlank(lines[now]));
                    break;
                case "p" or "P":
                    if (register is not { } put || put.Text.Length == 0)
                    {
                        break;
                    }
                    Changed();
                    if (put.Lines)
                    {
                        int at = command == "p" ? line + 1 : line;
                        lines.InsertRange(at, put.Text[..^1].Split('\n'));
                        Set(lines, at, FirstNonBlank(lines[at]));
                        break;
                    }
                    int into = command == "p" && length > 0 ? column + 1 : column;
                    lines[line] = lines[line][..into] + put.Text + lines[line][into..];
                    Set(lines, line, into + put.Text.Length - 1);
                    break;
                case "J":
                    if (line == lines.Count - 1)
                    {
                        break;
                    }
                    Changed();
                    string next = lines[line + 1].TrimStart(' ');
                    string space = next.Length == 0 || length == 0 || lines[line].EndsWith(' ') ? string.Empty : " ";
                    lines[line] += space + next;
                    lines.RemoveAt(line + 1);
                    Set(lines, line, Math.Min(length, Math.Max(lines[line].Length - 1, 0)));
                    break;
                case "u":
                    if (undo.TryPop(out (string Text, int Caret) before))
                    {
                        (Text, Caret) = before;
                        List<string> back = [.. Text[..^1].Split('\n')];
                        int at = Text[..Caret].Count(c => c == '\n');
                        Caret = Start(back, at) + Math.Min(Caret - Start(back, at), Math.Max(back[at].Length - 1, 0));
                        want = Caret - Start(back, at);
                    }
                    break;
            }
            return true;
        }

        private void Changed() => undo.Push((Text, Caret));

        private void Move(List<string> lines, int line, int column)
        {
            Caret = Start(lines, line) + column;
            want = column;
        }

        private void Set(List<string> lines, int line, int column)
        {
            Text = string.Concat(lines.Select(l => l + "\n"));
            Move(lines, line, column);
        }

        private static int Start(List<string> lines, int line) => lines.Take(line).Sum(l => l.Length + 1);

        private static int FirstNonBlank(string line)
        {
            int at = 0;
            while (at < line.Length - 1 && line[at] == ' ')
            {
                at++;
            }
            return at;
        }
    }
}
