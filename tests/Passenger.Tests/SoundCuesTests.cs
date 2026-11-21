using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The sound cues as a person moving through code hears them, the window's record of what it played standing in
/// for the speakers: one as the caret comes to a line holding a change, a folded region, an error, a breakpoint or a warning,
/// nothing along a line nor on lines holding none; Settings turning a cue off, the cues off, or on only while a screen
/// reader runs. A build's and a test run's cues are UnitTestsTests'.</summary>
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
