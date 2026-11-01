using Passenger.App.Godot;
using Passenger.Roslyn.Extension;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>What a Godot project's C# and its scenes say of each other, the code as Passenger's extension answers for it:
/// what is asked of the code for the scenes a script runs in, and what is wrong: a GetNode path no scene's node answers, a
/// node not of the type asked for, a res:// path no file answers, a connection to a method the script lacks or one of other
/// parameters than its signal gives.</summary>
public class GodotChecksTests
{
    private const string Scene = """
        [gd_scene format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]
        [ext_resource type="Script" path="res://Hud.cs" id="2"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1")

        [node name="Hud" type="CanvasLayer" parent="."]
        script = ExtResource("2")

        [node name="Score" type="Label" parent="Hud"]

        [connection signal="pressed" from="Hud" to="." method="OnPressed"]
        [connection signal="toggled" from="Hud" to="." method="OnToggled" binds= ["why"]]

        """;

    private static readonly Place At = new(9, 30);
    private static readonly Place End = new(9, 40);

    private static GodotScenes Scenes() => new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(Scene) });

    private static GodotClassFound Main => new("Main", "Main", new Place(2, 21), new Place(2, 25), "Node2D", ["res://Main.cs"], [], []);

    private static GodotCodeFound Found(GodotCodeRequest asked, IReadOnlyList<GodotNodeCall> calls, IReadOnlyList<GodotPathRead>? resources = null,
        IReadOnlyList<GodotSignature?>? methods = null) => new(
        [Main], calls, resources ?? [],
        [new GodotChain("Label", ["Label", "Control", "CanvasItem", "Node", "Object"], "Label", "Label"), new GodotChain("CanvasLayer", ["CanvasLayer", "Node", "Object"], "CanvasLayer", "CanvasLayer"),
            new GodotChain("Node2D", ["Node2D", "CanvasItem", "Node", "Object"], "Node2D", "Node2D")],
        [new GodotChain("res://Hud.cs", ["Hud", "Godot.CanvasLayer", "Godot.Node"], "CanvasLayer", "Hud")],
        [.. asked.Signals.Select(s => s.Name == "pressed" ? new GodotSignature("pressed", [], 0, null, null, null) : new GodotSignature("toggled", ["bool"], 1, null, null, null))],
        methods ?? [.. asked.Methods.Select(_ => (GodotSignature?)null)]);

    private static GodotNodeCall Call(string path, GodotTypeRef? type, bool orNull = false) => new(path, At, End, true, 0, type, orNull);

    [Fact]
    public void A_script_asks_the_engine_classes_scripts_signals_and_methods_of_the_scenes_it_runs_in()
    {
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", Scenes(), ["res://Main.cs"]);
        Assert.Equal(["Node2D", "CanvasLayer", "Label"], asked.Engine);
        Assert.Equal(["res://Main.cs", "res://Hud.cs"], asked.Scripts);
        Assert.Equal([new GodotMemberAsked("CanvasLayer", "res://Hud.cs", "pressed"), new GodotMemberAsked("CanvasLayer", "res://Hud.cs", "toggled")], asked.Signals);
        Assert.Equal([new GodotMemberAsked("Node2D", "res://Main.cs", "OnPressed"), new GodotMemberAsked("Node2D", "res://Main.cs", "OnToggled")], asked.Methods);
    }

    [Fact]
    public void A_path_no_node_answers_and_a_node_of_another_type_are_errors_naming_the_scene_and_line()
    {
        GodotScenes scenes = Scenes();
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotTypeRef label = new("Label", true, "Label");
        GodotTypeRef button = new("Button", true, "Button");
        GodotTypeRef hud = new("Hud", false, "Hud");
        GodotCodeFound found = Found(asked,
        [
            Call("Hud/Score", label), Call("Hud/Score", button), Call("Hud/Scor", label), Call("Hud/Scor", label, orNull: true), Call("Hud", hud), Call("Hud/Score", hud),
            Call("Hud/Score", button, orNull: true), Call("..", label),
        ], methods: [new GodotSignature("OnPressed", [], 0, null, null, null), new GodotSignature("OnToggled", ["bool", "string"], 2, null, null, null)]);
        IReadOnlyList<GodotChecks.Finding> findings = GodotChecks.Findings(scenes, asked, found, _ => true);
        Assert.Equal(
        [
            (true, "'Hud/Score' is a Label in main.tscn:12, not a Button"),
            (true, "main.tscn:6 has no node 'Hud/Scor' under Main: Hud has no child Scor"),
            (true, "'Hud/Score' is a Label in main.tscn:12, not a Hud"),
            (false, "GetNodeOrNull<Button> gives null: 'Hud/Score' is a Label in main.tscn:12, not a Button"),
        ], findings.Select(f => (f.Error, f.Message)));
    }

    [Fact]
    public void A_res_path_no_file_answers_is_an_error_and_a_folder_or_a_path_built_on_is_not()
    {
        GodotScenes scenes = Scenes();
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotCodeFound found = Found(asked, [], [new GodotPathRead("res://gone.tscn", At, End), new GodotPathRead("res://main.tscn", At, End),
            new GodotPathRead("res://levels/", At, End), new GodotPathRead("res://levels/level_", At, End)],
            methods: [new GodotSignature("OnPressed", [], 0, null, null, null), new GodotSignature("OnToggled", ["bool", "string"], 2, null, null, null)]);
        IReadOnlyList<GodotChecks.Finding> findings = GodotChecks.Findings(scenes, asked, found, path => path.EndsWith("main.tscn", StringComparison.Ordinal));
        Assert.Equal(["No file res://gone.tscn is in the Godot project", "No folder res://levels/ is in the Godot project"], findings.Select(f => f.Message));
    }

    [Fact]
    public void A_connection_to_a_method_the_script_lacks_or_of_other_parameters_than_its_signal_and_binds_give_is_an_error()
    {
        GodotScenes scenes = Scenes();
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotCodeFound found = Found(asked, [], methods: [null, new GodotSignature("OnToggled", ["bool"], 1, "/game/Main.cs", new Place(7, 17), new Place(7, 26))]);
        IReadOnlyList<GodotChecks.Finding> findings = GodotChecks.Findings(scenes, asked, found, _ => true);
        Assert.Equal(
        [
            (new Place(2, 21), "main.tscn:14 connects Hud's pressed to OnPressed, which Main has no method of"),
            (new Place(7, 17), "main.tscn:15 connects Hud's toggled, which gives 2 (bool, bound), to OnToggled, which takes 1 (bool)"),
        ], findings.Select(f => (f.At, f.Message)));
        found = Found(asked, [], methods: [new GodotSignature("OnPressed", [], 0, null, null, null), new GodotSignature("OnToggled", ["bool", "string"], 2, null, null, null)]);
        Assert.Empty(GodotChecks.Findings(scenes, asked, found, _ => true));
    }
}
