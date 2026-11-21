using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Terminal;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The sound cues as a person moving through code hears them, the window's record of what it played standing in
/// for the speakers: one as the caret comes to a line holding a change, a folded region, an error, a breakpoint or a warning,
/// nothing along a line nor on lines holding none; Settings turning a cue off, the cues off, or on only while a screen
/// reader runs; a save refused; a command failing in each shell Passenger marks the commands of, which Read as Text reads
/// the last of by its marks. A build's and a test run's cues are UnitTestsTests', the debugger's DebugSessionsTests'.</summary>
public sealed class SoundCuesTests : IDisposable
{
    private const string Head = "class Shop\n{\n    int count;\n\n    void Add()\n    {\n        count++;\n    }\n\n    void Clear() { count = 0; }\n    void Reset() { count = 1; }\n}\n";

    private readonly string folder = TestFolders.Create("sound-cues-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Each_lines_cue_plays_once_as_the_caret_comes_to_it_and_Settings_turns_each_off()
    {
        string path = Path.Combine(folder, "Shop.cs");
        File.WriteAllText(path, Head);
        TestGit.Init(folder);
        File.WriteAllText(path, Head.Replace("int count;", "long count;", StringComparison.Ordinal));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(path);
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => editor.ChangeMarks.Count == 1, "the change bar on the changed line");
            Assert.Equal(SoundCuesPlayed.WithScreenReader, window.Settings.SoundCues);

            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Query = "sound cue";
            Ui.Settle();
            Assert.Equal(SettingsDialog.AccessibilityPage, dialog.Page);
            string[] rows = ["Play sound cues", "An error on the caret's line", "A warning on the caret's line", "A breakpoint on the caret's line",
                "A folded region at the caret's line", "A line changed since the last commit", "A build passing or failing", "A test run passing or failing",
                "The debugger stopping"];
            Assert.True(rows.All(dialog.Shown.Contains), $"Settings' Accessibility page shows {string.Join(", ", dialog.Shown)}, not each cue's row");
            dialog.Change(s => s with { SoundCues = SoundCuesPlayed.Always });
            dialog.Ok();
            Ui.Settle();

            editor.GoTo(new TextPosition(10, 4));
            editor.Focus();
            Ui.Run(window, AppActions.ToggleBreakpoint);
            editor.GoTo(new TextPosition(4, 4));
            Ui.Run(window, EditorActions.Fold);
            Ui.Settle();
            TextSnapshot text = editor.Document.Snapshot;
            editor.SetMarkers(
            [
                new Marker(new TextRange(text.GetLineStart(9) + 9, 5), MarkerKind.Error, "CS0103: The name 'Clear' does not exist in the current context"),
                new Marker(new TextRange(text.GetLineStart(0) + 6, 4), MarkerKind.Warning, "CS8618: Non-nullable field must contain a value"),
            ]);
            editor.GoTo(new TextPosition(1, 0));
            Ui.Settle();

            Heard(window, "DOWN", [SoundCue.ChangedLine], "the changed line");
            Heard(window, "RIGHT", [], "a key along the changed line");
            Heard(window, "DOWN", [], "a blank line");
            Heard(window, "DOWN", [SoundCue.FoldedLine], "the line of a folded method");
            Heard(window, "DOWN", [], "the blank line under the folded method");
            Heard(window, "DOWN", [SoundCue.ErrorLine], "a line holding an error");
            Heard(window, "DOWN", [SoundCue.BreakpointLine], "a line holding a breakpoint");
            Heard(window, "UP", [SoundCue.ErrorLine], "the error's line again");
            int at = window.CuesPlayed.Count;
            editor.GoTo(new TextPosition(0, 2));
            Ui.Settle();
            Assert.True(window.CuesPlayed.Skip(at).SequenceEqual([SoundCue.WarningLine]), $"The caret come to a line holding a warning played {Said(window, at)}");
            string[] handed = [.. window.SoundsHanded.Where(f => Path.GetFileName(f).StartsWith("ErrorLine-", StringComparison.Ordinal))];
            Assert.True(handed.Length == 2 && File.ReadAllBytes(handed[0]) is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..] && new FileInfo(handed[0]).Length > 1000,
                $"The error's cue reached the player as {string.Join(", ", handed)}, not twice as a WAV of its own");

            window.ApplySettings(window.Settings with { SoundOnErrorLine = false });
            editor.GoTo(new TextPosition(8, 0));
            Ui.Settle();
            Heard(window, "DOWN", [], "a line holding an error once Settings turned its cue off");

            window.ApplySettings(window.Settings with { SoundOnErrorLine = true, SoundCues = SoundCuesPlayed.Never });
            Heard(window, "DOWN", [], "a line holding a breakpoint once Settings turned the cues off");

            window.ApplySettings(window.Settings with { SoundCues = SoundCuesPlayed.WithScreenReader });
            using (ScreenReaders.StandIn(false))
            {
                Heard(window, "UP", [], "a line holding an error with no screen reader running");
            }
            using (ScreenReaders.StandIn(true))
            {
                Heard(window, "DOWN", [SoundCue.BreakpointLine], "a line holding a breakpoint with a screen reader running");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_save_refused_plays_its_cue_once_and_a_save_that_lands_none()
    {
        string path = Path.Combine(folder, "Locked.cs");
        File.WriteAllText(path, "class Locked { }\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(folder, "Open.cs"), "class Open { }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { SoundCues = SoundCuesPlayed.Always });
            window.Confirm = (_, _, _) => Task.FromResult(false);
            window.OpenFile(path);
            window.EditorArea.CodeEditor.Focus();
            window.KeyTextInput("public ");
            Ui.Settle();
            Assert.Empty(window.CuesPlayed);
            Ui.Press(window, Ui.Shortcut(AppActions.Save));
            Ui.Settle();
            Assert.True(window.CuesPlayed.SequenceEqual([SoundCue.SaveRefused]), $"Save of a read-only file played {Said(window, 0)}, not the save refused cue once");

            window.OpenFile(Path.Combine(folder, "Open.cs"));
            window.EditorArea.CodeEditor.Focus();
            window.KeyTextInput("public ");
            Ui.Press(window, Ui.Shortcut(AppActions.Save));
            Ui.Settle();
            Assert.True(window.CuesPlayed.Count == 1 && !window.EditorArea.Active!.IsModified, $"A save that landed played {Said(window, 1)}");
        }
        finally
        {
            window.Close();
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>Each shell here, set up to mark its commands: a command that fails plays its cue once (where the shell tells a
    /// command ran: not bash before 4.4, nor cmd), one that passes and an empty line none, and Read as Text reads the last
    /// command and its output from its prompt's mark.</summary>
    [AvaloniaTheory]
    [InlineData("zsh")]
    [InlineData("bash")]
    [InlineData("fish")]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    [InlineData("cmd")]
    public void A_command_failing_in_a_shell_plays_its_cue_once_and_Read_as_Text_reads_the_last_command_by_its_marks(string kind)
    {
        (string? program, string[] arguments) = kind switch
        {
            "zsh" => (Found("/bin/zsh", "zsh"), []),
            "bash" => (Found("/bin/bash", "bash"), ["--norc", "--noprofile"]),
            "fish" => (Programs.OnPath("fish"), ["--no-config"]),
            "pwsh" => (Programs.OnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"), ["-NoLogo", "-NoProfile"]),
            "powershell" => (OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : null, ["-NoLogo", "-NoProfile"]),
            _ => ((string?, string[]))(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : null, []),
        };
        Assert.SkipWhen(program is null || !File.Exists(program), $"{kind} is not installed here");
        List<string>? answered = kind == "pwsh" && !OperatingSystem.IsWindows() ? [] : null;
        (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, program!, arguments, answered);
        try
        {
            window.ApplySettings(window.Settings with { SoundCues = SoundCuesPlayed.Always });
            Ui.WaitFor(() => shell.Screen.Command is not null, () => $"{kind}'s first prompt marked; the terminal shows:\n{shell.Screen.ScreenText()}");
            LineEditorReading(window, shell, answered);
            bool tells = kind is not "cmd";
            if (kind == "bash")
            {
                Run(window, shell, "echo v${BASH_VERSINFO[0]}.${BASH_VERSINFO[1]}", answered);
                string version = ScreenLines(shell).Last(l => l.StartsWith('v'));
                tells = Version.Parse(version[1..]) >= new Version(4, 4);
            }
            Run(window, shell, "echo first", answered);
            Run(window, shell, OperatingSystem.IsWindows() ? "cmd /c exit 3" : "false", answered);
            Assert.True(Failed(window) == (tells ? 1 : 0), $"A command failing in {kind} played its cue {Failed(window)} times, not {(tells ? "once" : "never, as it tells no run")}");
            Run(window, shell, string.Empty, answered);
            Run(window, shell, "echo second", answered);
            Assert.True(Failed(window) == (tells ? 1 : 0), $"An empty line and a command passing in {kind} played the failure's cue again");

            PlainTextView view = ReadAsTextTests.Opened(window, window, $"{kind}'s terminal");
            string[] lines = view.Text.Split('\n');
            Assert.True(lines is [var command, "second"] && command.EndsWith("echo second", StringComparison.Ordinal),
                $"Read as Text in {kind} read the last command and its output as:\n{view.Text}");
            ReadAsTextTests.Escaped(window, window, () => window.TerminalPanel.View.IsFocused, "the terminal");
        }
        finally
        {
            window.Close();
        }

        static string? Found(string at, string name) => File.Exists(at) ? at : Programs.OnPath(name);
    }

    /// <summary>Types a command (or nothing) and presses Enter, then waits for the shell's next prompt, marked below the last,
    /// and where the screen's answers are heard, for the shell's line editor to read keys there.</summary>
    private static void Run(MainWindow window, TerminalSession shell, string command, List<string>? answered)
    {
        long before = shell.Screen.Command?.Prompt ?? -1;
        answered?.Clear();
        if (command.Length > 0)
        {
            window.KeyTextInput(command);
        }
        Ui.Press(window, "ENTER");
        TerminalTests.WaitForScreen(window, shell, s => s.Command is { Entered: false } next && next.Prompt > before && s.CursorColumn > 0,
            $"the shell's next prompt after '{command}'");
        LineEditorReading(window, shell, answered);
        Ui.Settle();
    }

    /// <summary>Waits for PowerShell's line editor to take the terminal at the prompt, asking where its line begins (a cursor
    /// position report at the prompt's end): its host draws the prompt first, and on macOS the editor first runs a program
    /// that has the terminal echo what is typed and read Enter as Ctrl+J, which leaves the line unrun.</summary>
    private static void LineEditorReading(MainWindow window, TerminalSession shell, List<string>? answered)
    {
        if (answered is not null)
        {
            TerminalTests.WaitForScreen(window, shell, s => answered.Contains($"\u001b[{s.CursorRow + 1};{s.CursorColumn + 1}R"),
                "PowerShell's line editor asking where its line begins, at the prompt's end");
        }
    }

    private static string[] ScreenLines(TerminalSession shell) =>
        [.. Enumerable.Range(0, (int)(shell.Screen.ScreenTop + shell.Screen.Rows - shell.Screen.FirstLine)).Select(i => shell.Screen.LineAt(shell.Screen.FirstLine + i).Text())];

    private static int Failed(MainWindow window) => window.CuesPlayed.Count(c => c == SoundCue.TerminalCommandFailed);

    /// <summary>Presses a key in the editor and holds the window to having played <paramref name="cues"/> for it, and no more.</summary>
    private static void Heard(MainWindow window, string key, SoundCue[] cues, string where)
    {
        int before = window.CuesPlayed.Count;
        Ui.Press(window, key);
        Ui.Settle();
        Assert.True(window.CuesPlayed.Skip(before).SequenceEqual(cues),
            $"{key} to {where} (caret {window.EditorArea.CodeEditor.CaretPosition}) played {Said(window, before)}, not {(cues.Length == 0 ? "nothing" : string.Join(", ", cues))}");
    }

    private static string Said(MainWindow window, int from) =>
        window.CuesPlayed.Count > from ? string.Join(", ", window.CuesPlayed.Skip(from)) : "nothing";
}
