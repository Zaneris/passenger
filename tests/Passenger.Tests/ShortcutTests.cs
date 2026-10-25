using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.Editor;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>Shortcuts: a chord's first key says so in the status bar and waits for its second, but is forgotten on a
/// click, when the focus moves and after two seconds, so a letter typed later is typed; and on Windows, which sends AltGr
/// as Ctrl+Alt, AltGr types its character rather than running the Ctrl+Alt shortcut of the same key.</summary>
public sealed class ShortcutTests : IDisposable
{
    private const string Source = "class One { }\n";
    private readonly string folder = TestFolders.Create("keys-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_click_after_a_chords_first_key_forgets_it_so_the_next_letter_is_typed_not_a_comment()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Press(window, Ui.Shortcut(EditorActions.Comment));
        Assert.StartsWith("Prefix key pressed: ", window.Status);
        Assert.Contains("Comment (C)", window.Status);

        Point at = editor.TranslatePoint(editor.PointOf("class ".Length) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Ui.Type(window, "C", "c");
        Assert.Equal("class cOne { }\n", Text(editor));
        Assert.DoesNotContain("Prefix key pressed", window.Status ?? "", StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_chords_first_key_is_forgotten_when_the_focus_moves_and_after_two_seconds_as_in_Rider()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        string first = Ui.Shortcut(EditorActions.Comment);

        // The focus taken to Go to Line, as the Mac's menu bar takes it without a key or a click in the window, and back.
        Ui.Press(window, first);
        window.Perform(AppActions.GoToLine);
        Ui.Settle();
        Assert.False(editor.IsFocused, "Go to Line never took the focus");
        Ui.Press(window, "ESCAPE");
        Assert.True(editor.IsFocused, "Escape never gave the editor the focus back");
        Ui.Type(window, "C", "c");
        Assert.Equal("c" + Source, Text(editor));

        // Left waiting, the first key goes after two seconds, and its message with it.
        Ui.Press(window, first);
        Assert.StartsWith("Prefix key pressed: ", window.Status);
        Ui.WaitFor(() => !(window.Status ?? "").StartsWith("Prefix key pressed", StringComparison.Ordinal),
            "the chord's first key to be forgotten", TimeSpan.FromSeconds(10));
        Ui.Type(window, "C", "c");
        Assert.Equal("cc" + Source, Text(editor));
        window.Close();
    }

    [AvaloniaFact]
    public void On_Windows_AltGr_types_its_letter_where_the_layout_has_one_and_Ctrl_Alt_runs_its_shortcut_where_it_has_none()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows sends AltGr as Ctrl+Alt; the Mac's key symbol leaves Control out, so Ctrl+Option is a shortcut.
            return;
        }
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;

        // Polish AltGr+S and L type ś and ł, Czech AltGr+F types [: Settings, Explorer and Structure in Visual Studio's keys.
        Ui.Type(window, "control alt S", "ś");
        Ui.Type(window, "control alt L", "ł");
        Ui.Type(window, "shift control alt L", "Ł");
        Ui.Type(window, "control alt F", "[");
        Assert.StartsWith("śłŁ[", Text(editor), StringComparison.Ordinal);
        Assert.True(editor.IsFocused, "AltGr took the focus from the editor rather than typing");
        Assert.False(window.DockHost.IsShown(ToolWindowIds.Structure), "AltGr+F showed Structure rather than typing [");

        // An English layout gives Ctrl+Alt+F no character, so the shortcut runs.
        Ui.Press(window, "control alt F");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Structure), "Ctrl+Alt+F without a character never showed Structure");
        window.Close();
    }

    [AvaloniaFact]
    public void On_Windows_AltGr_types_its_character_into_the_terminal_rather_than_Ctrl_and_Alt_for_the_shell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        MainWindow window = Ui.OpenFolder(folder);
        window.TerminalPanel.Shell = ("cmd.exe", []);
        Ui.Run(window, AppActions.ShowTerminal);
        Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
        Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");
        TerminalSession shell = window.TerminalPanel.Active!;
        Ui.WaitFor(() => shell.Screen.CursorColumn > 0, "the shell's prompt");

        // A German layout's AltGr+Q is @.
        window.KeyTextInput("a");
        Ui.Type(window, "control alt Q", "@");
        window.KeyTextInput("b");
        Ui.WaitFor(() => Screen(shell).Contains("a@b", StringComparison.Ordinal), $"a@b typed at the prompt:\n{Screen(shell)}");
        window.Close();
    }

    private MainWindow Open()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), Source);
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        return window;
    }

    private static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();

    private static string Screen(TerminalSession session) =>
        string.Join('\n', session.Screen.Scrollback.Select(l => l.Text()).Concat(session.Screen.Lines.Select(l => l.Text())));
}
