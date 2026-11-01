using Passenger.App.Godot;
using Passenger.Roslyn.Extension;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>What a Godot project's C# and its scenes say of each other, the code as Passenger's extension answers for it:
/// what is asked of the code for the scenes a script runs in, and what is wrong: a GetNode path no scene's node answers, a
/// node not of the type asked for, a res:// path no file answers, a connection to a method the script lacks or one of other
/// parameters than its signal gives, a string naming a member a script lacks.</summary>
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

    /// <summary>A string naming a member a script's class lacks is an error, as is an EmitSignal giving a signal other
    /// arguments than it takes and a Callable's method connected by a string to a signal of other arguments; an engine
    /// class's object, which its script may give more, and strings that hold are not marked.</summary>
    [Fact]
    public void A_string_naming_a_member_a_script_lacks_or_of_other_arguments_is_an_error()
    {
        GodotScenes scenes = Scenes();
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotSignature scored = new("Scored", ["int"], 1, "/game/Main.cs", new Place(4, 25), new Place(4, 43));
        GodotSignature onScored = new("OnScored", ["int"], 1, "/game/Main.cs", new Place(9, 17), new Place(9, 25));
        GodotSignature onStart = new("OnStart", [], 0, "/game/Main.cs", new Place(11, 17), new Place(11, 24));
        GodotStringRead Read(GodotNameKind kind, string name, int line, bool script, GodotSignature? found, int? given = null, int? signal = null) =>
            new(kind, name, new Place(line, 10), new Place(line, 10 + name.Length), true, script ? "Main" : "Button", script, found, given, signal);
        GodotCodeFound found = Found(asked, [], methods: [null, null]) with
        {
            Strings =
            [
                Read(GodotNameKind.Method, "OnScord", 20, script: true, null),
                Read(GodotNameKind.Signal, "Scord", 21, script: true, null),
                Read(GodotNameKind.Property, "Spd", 22, script: true, null),
                Read(GodotNameKind.Signal, "presed", 23, script: false, null),
                Read(GodotNameKind.Signal, "Scored", 24, script: true, scored, given: 0),
                Read(GodotNameKind.Signal, "Scored", 25, script: true, scored, given: 1),
                Read(GodotNameKind.Signal, "Scored", 26, script: true, scored),
                Read(GodotNameKind.Method, "OnStart", 26, script: true, onStart, signal: 6),
                Read(GodotNameKind.Signal, "Scored", 27, script: true, scored),
                Read(GodotNameKind.Method, "OnScored", 27, script: true, onScored, signal: 8),
            ],
        };
        IReadOnlyList<GodotChecks.Finding> findings = [.. GodotChecks.Findings(scenes, asked, found, _ => true).Where(f => f.At.Line >= 20)];
        Assert.Equal(
        [
            (20, "Main has no method 'OnScord'"),
            (21, "Main has no signal 'Scord'"),
            (22, "Main has no property 'Spd'"),
            (24, "'Scored' takes 1 (int), but this EmitSignal gives it none"),
            (26, "'Scored', which gives 1 (int), is connected to 'OnStart', which takes none"),
        ], findings.Select(f => (f.At.Line, f.Message)));
        Assert.All(findings, f => Assert.True(f.Error));
    }

    /// <summary>A scene's own text is marked where Godot would not load it: an external resource of no file, an ExtResource
    /// of no id, a node under one the scene lacks, a connection's end no node answers, and a connection to a method the C#
    /// script lacks, from a signal the engine class lacks, or of other arguments; a node of a GDScript is never said to lack one.</summary>
    [Fact]
    public void A_scene_is_marked_where_godot_would_not_load_its_resources_nodes_and_connections()
    {
        const string scene = """
            [gd_scene format=3]

            [ext_resource type="Script" path="res://Main.cs" id="1"]
            [ext_resource type="Texture2D" path="res://gone.png" id="2"]
            [ext_resource type="Script" path="res://hud.gd" id="3"]

            [node name="Main" type="Node2D"]
            script = ExtResource("1")

            [node name="Hud" type="CanvasLayer" parent="."]
            script = ExtResource("3")

            [node name="Icon" type="Sprite2D" parent="."]
            texture = ExtResource("9")

            [node name="Lost" type="Label" parent="Gone"]

            [connection signal="pressed" from="Hud" to="." method="OnPressed"]
            [connection signal="visibility_changed" from="Icon" to="." method="OnShown"]
            [connection signal="presed" from="Icon" to="Hud" method="anything"]
            [connection signal="ready" from="Nowhere" to="." method="OnShown"]
            [connection signal="texture_changed" from="Icon" to="." method="OnToggled"]

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(scene) });
        GodotCodeRequest asked = GodotChecks.SceneAsks("/game/Main.cs", scenes, "res://main.tscn");
        Assert.Equal(["Node2D", "CanvasLayer", "Sprite2D"], asked.Engine);
        Assert.Equal(["res://Main.cs"], asked.Scripts);
        GodotSignature? Signal(GodotMemberAsked a) => a.Name switch
        {
            "visibility_changed" or "texture_changed" => new GodotSignature(a.Name, [], 0, null, null, null),
            _ => null,
        };
        GodotSignature? Method(GodotMemberAsked a) => a.Name switch
        {
            "OnShown" => new GodotSignature(a.Name, [], 0, null, null, null),
            "OnToggled" => new GodotSignature(a.Name, ["bool"], 1, null, null, null),
            _ => null,
        };
        GodotCodeFound found = new([], [], [], [], [], [.. asked.Signals.Select(Signal)], [.. asked.Methods.Select(Method)]);
        IReadOnlyList<GodotChecks.SceneFinding> findings = GodotChecks.SceneFindings(scenes, "res://main.tscn", asked, found, path => !path.EndsWith("gone.png", StringComparison.Ordinal));
        Assert.Equal(
        [
            ("res://gone.png", "No file res://gone.png is in the Godot project"),
            ("9", "No ext_resource of this file has the id \"9\""),
            ("Gone", "No node 'Gone' is in the scene, so Godot leaves Lost out"),
            ("OnPressed", "Main, a Node2D (Main.cs), has no method OnPressed"),
            ("presed", "Icon, a Sprite2D, has no signal presed"),
            ("Nowhere", "No node 'Nowhere' is in the scene"),
            ("OnToggled", "texture_changed gives no arguments, but OnToggled takes 1 (bool)"),
        ], findings.Select(f => (scene[f.Start..f.End], f.Message)));
    }
}
