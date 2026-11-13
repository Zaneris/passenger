using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Vim mode through the window, keys pressed as a keyboard presses them (the key with its character, then the
/// character as text unless the key was taken): the setting turns it on and off live, the status bar says the mode and a
/// command half typed, normal mode's keys (Enter and Tab too) move and delete but never type, a Shift pressed on its own
/// keeps a count, u takes back one x and a whole insert session, Escape leaves insert mode keeping every caret, carets
/// stand on a character in normal mode however they were put there, and an input method composes only in insert mode.
/// What each key does is VimTests'.</summary>
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

            Key(window, "Q", "q");
            Ui.Press(window, "ENTER");
            Ui.Press(window, "TAB");
            Key(window, "K", "k");
            Assert.True(Text(editor) == "one wo\nthree\nfour\n", "normal mode's q, Enter or Tab typed into the text");
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

    private (MainWindow Window, CodeEditor Editor) Open(string text)
    {
        string path = Path.Combine(folder, "One.txt");
        File.WriteAllText(path, text);
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
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
}
