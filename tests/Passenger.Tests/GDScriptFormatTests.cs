using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Reformat Code on a GDScript wraps a line past the line length inside its brackets in the style guide's forms,
/// as Settings › Code Style › GDScript and the folder's .editorconfig say, and a save reformats a changed script first once
/// Settings ask. No project.godot, so no Godot server starts.</summary>
public sealed class GDScriptFormatTests
{
    private const string Long = "func f():\n"
        + "\teffect.interpolate_property(sprite, \"transform/scale\", sprite.get_scale(), Vector2(2.0, 2.0), 0.3, Tween.TRANS_QUAD, Tween.EASE_OUT)\n"
        + "\tvar party = [\"Godot\", \"Godette\", \"Steve\"]\n";

    private const string WrappedAt100 = "func f():\n"
        + "\teffect.interpolate_property(sprite, \"transform/scale\", sprite.get_scale(), Vector2(2.0, 2.0),\n\t\t\t0.3, Tween.TRANS_QUAD, Tween.EASE_OUT)\n"
        + "\tvar party = [\"Godot\", \"Godette\", \"Steve\"]\n";

    private const string WrappedAt40 = "func f():\n"
        + "\teffect.interpolate_property(sprite,\n\t\t\t\"transform/scale\",\n\t\t\tsprite.get_scale(),\n\t\t\tVector2(2.0, 2.0), 0.3,\n\t\t\tTween.TRANS_QUAD,\n\t\t\tTween.EASE_OUT)\n"
        + "\tvar party = [\n\t\t\"Godot\",\n\t\t\"Godette\",\n\t\t\"Steve\",\n\t]\n";

    [AvaloniaFact]
    public void Reformat_code_wraps_long_lines_as_settings_and_the_editorconfig_say_and_a_save_reformats_when_asked()
    {
        string folder = TestFolders.Create("gdscript-format-");
        string script = TestFolders.Write(folder, "wrap.gd", Long);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            Assert.Null(window.OpenFile(script));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;

            // The style guide's 100 columns: the call's arguments run on two levels in; the array fits.
            Reformat(window, document, WrappedAt100, "the call wrapped at the guide's 100 columns");
            Ui.Run(window, EditorActions.Undo);
            Assert.True(document.Text.Snapshot.ToString() == Long, "One Undo did not take the whole reformat back");

            // Settings' own page, its line length 40: the array an element to a line, with its trailing comma.
            SettingsDialog settings = window.ShowSettings();
            settings.Page = SettingsDialog.GDScriptCodeStylePage;
            Ui.Settle();
            Assert.Equal(["Wrap long lines inside brackets", "Line length (columns)", "Blank lines around functions and classes",
                "Trailing comma in multi-line arrays, dictionaries and enums", "Reformat on save"], settings.Shown);
            settings.Change(s => s with { GDScriptFormat = s.GDScriptFormat with { LineLength = 40 } });
            settings.Ok();
            Ui.Settle();
            editor.Focus();
            Reformat(window, document, WrappedAt40, "the script wrapped at the 40 columns Settings ask");
            Ui.Run(window, EditorActions.Undo);

            // The folder's .editorconfig says over Settings: max_line_length = off leaves long lines as written.
            File.WriteAllText(Path.Combine(folder, ".editorconfig"), "root = true\n\n[*.gd]\nmax_line_length = off\n");
            Ui.Run(window, AppActions.Reformat);
            Ui.Settle();
            Assert.True(document.Text.Snapshot.ToString() == Long, $"A script whose .editorconfig turns wrapping off was wrapped:\n{document.Text.Snapshot}");
            Assert.Equal("Already formatted", window.Status);
            File.Delete(Path.Combine(folder, ".editorconfig"));

            // Reformat on save: Save All lays the changed script out first, as one command of Undo.
            window.ApplySettings(window.Settings with { GDScriptFormatOnSave = true });
            editor.ApplyEdits([new TextEdit(document.Text.Snapshot.Length, 0, "\tprint (party)\n")]);
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => !document.IsModified, "the script saved");
            Assert.Equal(WrappedAt40 + "\tprint(party)\n", File.ReadAllText(script));
            Ui.Run(window, EditorActions.Undo);
            Assert.True(document.Text.Snapshot.ToString() == Long + "\tprint (party)\n", $"One Undo did not take the save's reformat back:\n{document.Text.Snapshot}");
        }
        finally
        {
            window.Close();
        }
    }

    private static void Reformat(MainWindow window, OpenDocument document, string laid, string what)
    {
        Ui.Run(window, AppActions.Reformat, settle: false);
        Ui.PumpUntil(() => document.Text.Snapshot.ToString() == laid, what, TimeSpan.FromSeconds(30));
    }
}
