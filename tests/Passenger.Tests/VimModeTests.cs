using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Vim mode through the window: the setting turns it on and off live, the status bar says the mode and a command
/// half typed, normal mode's keys (Enter and Tab too) move and delete but never type, a Shift pressed on its own keeps a
/// count, u takes back one x as the window's undo, and insert mode types until Escape. What each key does is VimTests'.</summary>
public sealed class VimModeTests : IDisposable
{
    private readonly string folder = TestFolders.Create("vim-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Vim_mode_takes_normal_modes_keys_types_only_in_insert_mode_and_says_its_mode()
    {
        string path = Path.Combine(folder, "One.txt");
        File.WriteAllText(path, "one two\nthree\nfour\n");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Assert.Equal("NORMAL  1:1", Caret(window));

            Ui.Press(window, "W");
            Ui.Press(window, "X");
            Ui.Press(window, "X");
            Assert.Equal("one o\nthree\nfour\n", Text(editor));
            Ui.Press(window, "U");
            Ui.WaitFor(() => Text(editor) == "one wo\nthree\nfour\n", () => $"u left \"{Text(editor)}\", not the text before the last x alone");

            Ui.Press(window, "2");
            // The Shift of Shift+G goes down on its own first, as a person presses it.
            window.KeyPress(Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.None, null);
            window.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.Settle();
            Assert.StartsWith("NORMAL 2  ", Caret(window));
            Ui.Press(window, "shift G");
            Assert.Equal("NORMAL  2:1", Caret(window));

            window.KeyTextInput("q");
            Ui.Press(window, "ENTER");
            Ui.Press(window, "TAB");
            Ui.Press(window, "K");
            Assert.True(Text(editor) == "one wo\nthree\nfour\n", "normal mode's q, Enter or Tab typed into the text");
            Assert.Equal("NORMAL  2:1", Caret(window));

            Ui.Press(window, "I");
            Assert.Equal("INSERT  2:1", Caret(window));
            window.KeyTextInput("Zz");
            Ui.Press(window, "ESCAPE");
            Assert.Equal("one wo\nZzthree\nfour\n", Text(editor));
            Assert.Equal("NORMAL  2:2", Caret(window));

            window.ApplySettings(window.Settings with { VimMode = false });
            Ui.Settle();
            Assert.Equal("2:2", Caret(window));
            window.KeyTextInput("q");
            Ui.Settle();
            Assert.True(Text(editor) == "one wo\nZqzthree\nfour\n", "with Vim mode turned off, typing typed nothing");
        }
        finally
        {
            window.Close();
        }
    }

    private static string? Caret(MainWindow window) => window.FindControl<TextBlock>("CaretText")!.Text;

    private static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();
}
