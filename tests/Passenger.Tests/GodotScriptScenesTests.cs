using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A Godot 4 game of GDScript alone, a project.godot and its scenes and scripts with no C# project, its scenes read
/// as a game's with C# are: an open scene marks an ExtResource no resource of it has and a connection to a function its
/// node's script lacks, Structure lists its tree and follows a scene it instances written on disk, and a connection leads to
/// the function its node's script declares. No Godot is needed; the script opened starts GDScript's server where one is
/// found, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotScriptScenesTests
{
    private const string Script = "extends Node2D\n\nsignal scored(points: int)\n\n\nfunc _on_start_pressed() -> void:\n\tscored.emit(1)\n";

    private const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://main.gd" id="1_main"]
        [ext_resource type="PackedScene" path="res://actors/player.tscn" id="2_player"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")

        [node name="Start" type="Button" parent="."]
        theme = ExtResource("9_theme")

        [node name="Player" parent="." instance=ExtResource("2_player")]

        [connection signal="pressed" from="Start" to="." method="_on_start_pressed"]
        [connection signal="pressed" from="Start" to="." method="_on_quit_pressed"]
        [connection signal="scored" from="." to="Start" method="_on_scored"]

        """;

    private static string Player(string type) => $"[gd_scene format=3]\n\n[node name=\"Player\" type=\"{type}\"]\n";

    /// <summary>The scene opened marks the theme's ExtResource of no id the file has and the connection to _on_quit_pressed,
    /// which main.gd declares no function of, and the one to a handler the Start button, of no script, could not have, but
    /// not the one to _on_start_pressed; Structure lists the scene's nodes, script, instance and connections, the Player's
    /// class following player.tscn as it is written again on disk; and the connection's row goes to main.gd's function.</summary>
    [AvaloniaFact]
    public void A_scene_of_a_gdscript_game_is_marked_outlined_and_followed_and_its_connection_leads_to_its_function()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-script-scenes-"));
        File.WriteAllText(Path.Combine(folder, "project.godot"), "config_version=5\n\n[application]\n\nconfig/name=\"Blocks\"\nrun/main_scene=\"res://main.tscn\"\n");
        File.WriteAllText(Path.Combine(folder, "main.gd"), Script);
        File.WriteAllText(Path.Combine(folder, "main.tscn"), Scene);
        Directory.CreateDirectory(Path.Combine(folder, "actors"));
        File.WriteAllText(Path.Combine(folder, "actors", "player.tscn"), Player("CharacterBody2D"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "main.tscn")));
            string unknown = "No ext_resource of this file has the id \"9_theme\"";
            string quit = "Main, a Node2D (main.gd), has no method _on_quit_pressed";
            string scored = "Start, a Button, has no method _on_scored";
            Ui.WaitFor(() => GodotSceneTests.Marked(window) is { } marked && marked.Contains(unknown) && marked.Contains(quit) && marked.Contains(scored),
                () => $"the scene's unknown ExtResource and its connections to missing functions to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}");
            Assert.DoesNotContain(GodotSceneTests.Marked(window), m => m.Contains("_on_start_pressed", StringComparison.Ordinal));
            Marker missing = window.EditorArea.Active!.Markers.First(m => m.Message == quit);
            Assert.Equal("_on_quit_pressed", window.EditorArea.Active.Text.Snapshot.GetText(missing.Range.Start, missing.Range.Length));

            Ui.Run(window, AppActions.ShowStructure);
            StructureView structure = window.StructureView;
            Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "actors/player.tscn"), () => $"the scene's tree in Structure: {Listed(structure)} ({structure.Said})");
            Assert.Equal([("Main", ": Node2D", 0), ("main.gd", "script", 1), ("scored", "→ Start._on_scored", 1), ("Start", ": Button", 1),
                ("pressed", "→ Main._on_start_pressed", 2), ("pressed", "→ Main._on_quit_pressed", 2), ("Player", ": CharacterBody2D", 1), ("actors/player.tscn", "instance", 2)],
                structure.Rows.Select(r => (r.Name, r.Detail ?? string.Empty, r.Depth)));

            File.WriteAllText(Path.Combine(folder, "actors", "player.tscn"), Player("Area2D"));
            Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "Player" && r.Detail == ": Area2D"),
                () => $"Structure to follow player.tscn written on disk: {Listed(structure)}", TimeSpan.FromSeconds(30));

            ListBox list = structure.GetVisualDescendants().OfType<ListBox>().First();
            Ui.Click(Ui.Row(list, structure.Rows.First(r => r.Detail == "→ Main._on_start_pressed")));
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "main.gd" && window.EditorArea.CodeEditor.Document.Snapshot.GetLineText(window.EditorArea.CodeEditor.CaretPosition.Line)
                .StartsWith("func _on_start_pressed", StringComparison.Ordinal),
                () => $"the connection to go to its function ({window.EditorArea.Active?.Name}: {window.EditorArea.CodeEditor.Document.Snapshot.GetLineText(window.EditorArea.CodeEditor.CaretPosition.Line)}; {window.Status})");
        }
        finally
        {
            window.Close();
        }

        static string Listed(StructureView structure) => string.Join(", ", structure.Rows.Select(r => $"{new string(' ', r.Depth)}{r.Name} {r.Detail}"));
    }
}
