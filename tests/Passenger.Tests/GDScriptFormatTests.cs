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

    private const string Unordered = "extends Node\n\nfunc _process(delta):\n\tpass\n\n# Its speed.\nvar speed := 2.0\n\nfunc helper():\n\tpass\n\nsignal hit\n"
        + "@export var size := 3\n\nfunc _ready():\n\tpass\n\nconst LIMIT := 10\n";

    private const string Ordered = "extends Node\n\nsignal hit\n\nconst LIMIT := 10\n\n@export var size := 3\n\n# Its speed.\nvar speed := 2.0\n\n\n"
        + "func _ready():\n\tpass\n\n\nfunc _process(delta):\n\tpass\n\n\nfunc helper():\n\tpass\n";

    /// <summary>Order Script's Parts puts a script's parts in the style guide's Code order, each with its comment, as one
    /// command of Undo; a script whose variable's value reads a constant that would come after it is refused, saying so.</summary>
    [AvaloniaFact]
    public void Order_script_parts_lays_out_the_guides_code_order_and_refuses_a_value_reading_what_would_come_after_it()
    {
        string folder = TestFolders.Create("gdscript-order-");
        string script = TestFolders.Write(folder, "order.gd", Unordered);
        string reads = TestFolders.Write(folder, "reads.gd", "extends Node\n\nconst LIMIT := 4\nenum Size { SMALL = LIMIT }\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            Assert.Null(window.OpenFile(script));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(window.RunFromMenu(AppActions.OrderScriptParts), window.Status);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString() == Ordered, "the parts in the guide's order", TimeSpan.FromSeconds(30));
            Ui.Run(window, EditorActions.Undo, settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString() == Unordered, "one Undo taking the order back", TimeSpan.FromSeconds(30));

            Assert.Null(window.OpenFile(reads));
            Ui.Settle();
            Assert.True(window.RunFromMenu(AppActions.OrderScriptParts), window.Status);
            Ui.WaitFor(() => window.Status?.StartsWith("Order Script's Parts was not applied", StringComparison.Ordinal) == true, () => $"the order refused ({window.Status})");
            Assert.Contains("'Size''s value reads 'LIMIT', which would come after it", window.Status ?? string.Empty, StringComparison.Ordinal);
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
