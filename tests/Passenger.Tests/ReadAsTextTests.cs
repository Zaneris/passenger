using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>Read as Text as a screen reader's user meets it: one key opens what the thing with the keys says as plain text
/// in the one view, an editor named by what it shows that a reader reads line by line, and Escape gives the keys back to
/// where they were: the status bar's last message, in every keymap; the terminal's last command and its output; a diff's
/// changes as lines removed and added. The caret's problems and card are AssistAccessibilityTests', a test's result
/// UnitTestsTests'.</summary>
public sealed class ReadAsTextTests : IDisposable
{
    private readonly string folder = TestFolders.Create("read-as-text-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Read as Text's keys: Ctrl+Cmd+R on the Mac, Ctrl+Alt+R elsewhere, in every keymap.</summary>
    internal static string Key => OperatingSystem.IsMacOS() ? "control meta R" : "control alt R";

    /// <summary>Presses Read as Text in a window and gives the view it opened, holding it to having the keys and being read
    /// by a screen reader as an editor named as it is titled.</summary>
    internal static PlainTextView Opened(MainWindow window, TopLevel keysIn, string what)
    {
        Ui.Press(keysIn, Key);
        Ui.Settle();
        PlainTextView view = window.PlainText ?? throw new Xunit.Sdk.XunitException($"Read as Text ({Key}) on {what} opened nothing; the status bar says \"{window.Status}\"");
        Assert.True(view.Editor.IsKeyboardFocusWithin, $"Read as Text on {what} left the keys outside its text, on {keysIn.FocusManager?.GetFocusedElement()?.GetType().Name}");
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(view.Editor);
        Assert.True(peer is AccessibleTextPeer && peer.GetName() == view.Title,
            $"Read as Text's editor is met by a screen reader as {peer.GetType().Name} \"{peer.GetName()}\", not as text named \"{view.Title}\"");
        Assert.True(view.Editor.IsReadOnly, $"Read as Text's text on {what} takes edits");
        return view;
    }

    /// <summary>Presses Escape in the view and holds the keys to having gone back to <paramref name="had"/>.</summary>
    internal static void Escaped(MainWindow window, TopLevel keysIn, Func<bool> had, string where)
    {
        Ui.Press(keysIn, "ESCAPE");
        Ui.Settle();
        Assert.True(window.PlainText is null, "Escape left Read as Text's view open");
        Assert.True(had(), $"Escape from Read as Text gave the keys to {keysIn.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, not back to {where}");
    }

    [AvaloniaFact]
    public void The_status_bars_last_message_opens_as_text_in_every_keymap_and_Escape_gives_its_bar_the_keys_back()
    {
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\ntwo\n");
        TestGit.Init(folder);
        File.WriteAllText(Path.Combine(folder, "Draft.txt"), "draft\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile("Notes.txt");
            Button changes = window.FindControl<Button>("GitChangesButton")!;
            Ui.WaitFor(() => changes.IsEffectivelyVisible, "the status bar's changed files, something there to take the keys");
            window.EditorArea.CodeEditor.Focus();
            Ui.Run(window, AppActions.NextError);
            Assert.Equal("No errors or warnings in this file", window.Status);
            Shortcut key = Shortcut.Parse(Key);
            foreach (KeymapPreset preset in (KeymapPreset[])[KeymapPreset.VisualStudio, KeymapPreset.VSCode, KeymapPreset.IntelliJ])
            {
                window.ApplySettings(window.Settings with { Keymap = preset });
                Keymap keymap = AppActions.Keymap();
                string[] sharing = [.. keymap.Actions.Where(a => a != AppActions.ReadAsText && keymap.ShortcutsFor(a).Any(s => s.First == key.First))];
                Assert.True(sharing.Length == 0, $"{preset}'s {Key} begins {string.Join(", ", sharing)} too, which Read as Text would take it from");
                changes.Focus(NavigationMethod.Tab);
                Ui.Settle();
                PlainTextView view = Opened(window, window, $"the status bar in {preset}'s keymap");
                Assert.Equal("Last message", view.Title);
                Assert.Equal("No errors or warnings in this file", view.Text);
                Escaped(window, window, () => changes.IsFocused, "the status bar's changed files");
            }
            window.EditorArea.CodeEditor.Focus();
            Assert.True(window.Actions.WhyNot(AppActions.ReadAsText, window.Actions.Context().ForAgent()) == Refusals.AsksThePerson,
                "An agent's ide_action may open Read as Text's view, which takes the person's keys");
            Assert.True(window.Actions.Find("Read as Text").Any(a => a.Id == AppActions.ReadAsText), "Find Action finds no Read as Text");
        }
        finally
        {
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_terminals_last_command_and_its_output_open_as_text_and_Escape_gives_the_terminal_its_keys_back()
    {
        (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
        try
        {
            window.KeyTextInput("echo first");
            Ui.Press(window, "ENTER");
            TerminalTests.WaitForScreen(window, shell, s => PromptBelow(s, "first"), "the shell's prompt under first");
            window.KeyTextInput("echo second");
            Ui.Press(window, "ENTER");
            TerminalTests.WaitForScreen(window, shell, s => PromptBelow(s, "second"), "the shell's prompt under second");
            TerminalView terminal = window.TerminalPanel.View;
            PlainTextView view = Opened(window, window, "the terminal");
            Assert.Equal("Terminal: last command and its output", view.Title);
            string[] lines = view.Text.Split('\n');
            Assert.True(lines is [var command, "second"] && command.EndsWith("echo second", StringComparison.Ordinal),
                $"Read as Text in the terminal read the last command and its output as:\n{view.Text}");
            Escaped(window, window, () => terminal.IsFocused, "the terminal");
        }
        finally
        {
            window.Close();
        }

        // A line of output with the shell's prompt below it, the cursor on that prompt; none while it is on the first line.
        static bool PromptBelow(TerminalScreen screen, string output) =>
            screen.CursorColumn > 0 && screen.ScreenTop + screen.CursorRow > screen.FirstLine
            && screen.LineAt(screen.ScreenTop + screen.CursorRow - 1) is { } above
            && (above.Text() == output || (screen.CursorRow > 1 && above.Text().Length == 0 && screen.LineAt(screen.ScreenTop + screen.CursorRow - 2).Text() == output));
    }

    [AvaloniaFact]
    public void A_diffs_changes_open_as_text_lines_removed_and_added_under_where_each_stands_and_Escape_gives_its_side_the_keys_back()
    {
        string path = Path.Combine(folder, "Shop.cs");
        File.WriteAllText(path, "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n");
        TestGit.Init(folder);
        File.WriteAllText(path, "class Shop\n{\n    long count;\n\n    void Add() { count++; }\n    void Clear() { count = 0; }\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(path);
            Ui.Settle();
            IDiffHost diff = Ui.Landed(window.ShowDiffAsync(window.EditorArea.Active!.Path), "the diff open")!;
            DiffView shown = OpenDiffs.ViewOf(diff);
            TopLevel keysIn = OpenDiffs.KeysOf(diff);
            Ui.WaitFor(() => shown.Hunks.Count == 2, () => $"the diff's two changes, where it shows {shown.Hunks.Count}");
            shown.Right.Focus();
            Ui.Settle();
            PlainTextView view = Opened(window, keysIn, "a diff");
            Assert.Equal($"Changes: {shown.Subject}", view.Title);
            string[] lines = view.Text.Split('\n');
            string[] changes = [.. lines.SkipWhile(l => !l.StartsWith("Change ", StringComparison.Ordinal))];
            Assert.True(changes.SequenceEqual(
                [
                    "Change 1 of 2: Line 3 of HEAD changed to Line 3 of Your version",
                    "-     int count;",
                    "+     long count;",
                    string.Empty,
                    "Change 2 of 2: Line 6 of Your version added",
                    "+     void Clear() { count = 0; }",
                ]), $"Read as Text in the diff read its changes as:\n{view.Text}");
            Assert.True(lines[0] == shown.Said, $"Read as Text's diff begins \"{lines[0]}\", not what its bar says, \"{shown.Said}\"");
            Escaped(window, keysIn, () => shown.Right.IsFocused, "the diff's side");
            diff.Close();
        }
        finally
        {
            window.Close();
        }
    }
}
