using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A scene's node running a GDScript, its properties as its outline shows them from Passenger's own reading: those
/// the script exports and those it inherits, as Godot's inspector lists them. Godot's server is never started; the engine's
/// classes are dumped from a Godot where one is found, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptScenePropertiesTests
{
    /// <summary>A node's script extending another lists its own export, then its base's under the base's file: a value the
    /// scene sets, and an enum written as an expression by the values Godot folds it to, its default among them.</summary>
    [AvaloniaFact]
    public void A_nodes_properties_list_the_exports_its_script_inherits_and_an_enums_folded_values()
    {
        string folder = TestFolders.Create("gdscript-properties-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Properties\"\n");
        TestFolders.Write(folder, "base.gd", "extends Node2D\n\nenum Mood { LOW = 1 << 3, MID }\n\n@export var speed := 3.5\n@export var mood: Mood = Mood.MID\n");
        TestFolders.Write(folder, "main.gd", "extends \"res://base.gd\"\n\n@export var own := 7\n");
        TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\nspeed = 5.0\n");
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            StartsLanguageServer = false,
            GodotEnvironment = GodotTests.Game.Home(folder + "-home"),
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            window.OpenFile(Path.Combine(folder, "main.tscn"));
            window.ShowToolWindow(ToolWindowIds.Structure);
            Ui.WaitFor(() => window.StructureView.Rows.Any(r => r.Name == "Main"), () => $"the scene's outline ({window.StructureView.Said})", TimeSpan.FromMinutes(1));
            window.EditorArea.CodeEditor.GoTo(new TextPosition(4, 2));
            Ui.WaitFor(() => window.SceneProperties?.Shown?.Name == "Main" && window.SceneProperties.Rows.Any(r => r.Property.Name == "mood"),
                () => $"Main's properties with its base's mood ({string.Join(", ", window.SceneProperties?.Rows.Take(6).Select(r => r.Property.Name) ?? [])}; {window.Status})", TimeSpan.FromMinutes(2));
            GodotSceneProperties shown = window.SceneProperties!;
            Assert.Equal(["own", "speed", "mood"], shown.Rows.Take(3).Select(r => r.Property.Name));
            Assert.Contains(shown.Rows, r => r is { Property.Name: "own", Property.Category: "main.gd", Set: false, Text: "7" });
            Assert.Contains(shown.Rows, r => r is { Property.Name: "speed", Property.Category: "base.gd", Set: true, Text: "5.0" });
            Assert.Contains(shown.Rows, r => r is { Property.Name: "mood", Property.Category: "base.gd", Property.HintString: "LOW:8,MID:9", Set: false, Text: "9" });
        }
        finally
        {
            window.Close();
        }
    }
}
