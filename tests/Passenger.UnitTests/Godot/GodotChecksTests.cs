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
    public void A_path_no_node_answers_offers_the_nearest_of_the_type_asked_and_a_wrong_type_the_nodes_own()
    {
        GodotScenes scenes = Scenes();
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotTypeRef label = new("Label", true, "Label");
        GodotTypeRef node2D = new("Node2D", true, "Node2D");
        GodotCodeFound found = Found(asked, [Call("Hud/Scor", label), Call("Hud/Score", new GodotTypeRef("Button", true, "Button")), Call("Scor", label), Call("Hud/Scor", node2D)],
            methods: [new GodotSignature("OnPressed", [], 0, null, null, null), new GodotSignature("OnToggled", ["bool", "string"], 2, null, null, null)]);
        IReadOnlyList<GodotChecks.Finding> findings = GodotChecks.Findings(scenes, asked, found, _ => true);
        Assert.Equal(["Hud/Score"], findings[0].Offer!.Near);
        Assert.Equal(["Hud/Score"], findings[2].Offer!.Near);
        Assert.Empty(findings[3].Offer!.Near);
        GodotFixRequest typed = findings[1].Offer!.Make!;
        Assert.Equal((GodotFixKind.NodeType, "Label", "/game/Main.cs", At), (typed.Kind, typed.Type, typed.Path, typed.At));
        Assert.Equal("Label", findings[1].Offer!.Shown);
    }

    [Fact]
    public void A_connection_to_a_missing_method_offers_it_made_with_what_the_signal_gives_less_unbinds_and_with_binds_beside_the_scripts_handlers()
    {
        GodotScenes scenes = Scenes();
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotCodeFound found = Found(asked, [], methods: [new GodotSignature("OnPressed", [], 0, null, null, null), null]);
        GodotChecks.Finding finding = Assert.Single(GodotChecks.Findings(scenes, asked, found, _ => true));
        GodotFixRequest made = finding.Offer!.Make!;
        Assert.Equal((GodotFixKind.Handler, "res://Main.cs", "OnToggled", "/game/Main.cs"), (made.Kind, made.Script, made.Name, made.Path));
        Assert.Equal(new GodotMemberAsked("CanvasLayer", "res://Hud.cs", "toggled"), made.Signal);
        Assert.Equal(["String"], made.Binds);
        Assert.Equal(["OnPressed"], made.Beside);
        Assert.Equal("OnToggled(bool, string)", finding.Offer.Shown);
    }

    [Fact]
    public void A_property_a_scene_sets_that_the_script_lacks_is_an_error_offering_it_exported_of_its_values_type()
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(Scene.Replace("script = ExtResource(\"1\")\n",
            "script = ExtResource(\"1\")\nLives = 3\nTint = Color(1, 0, 0, 1)\nTarget = NodePath(\"Hud/Score\")\nposition = Vector2(1, 2)\nmetadata/Note = \"x\"\n", StringComparison.Ordinal)) });
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        Assert.Equal(["Lives", "Tint", "Target"], asked.Properties.Select(p => p.Name));
        GodotCodeFound found = Found(asked, [], methods: [new GodotSignature("OnPressed", [], 0, null, null, null), new GodotSignature("OnToggled", ["bool", "string"], 2, null, null, null)])
            with { Properties = [null, null, new GodotSignature("Target", ["Label"], 0, null, null, null)] };
        IReadOnlyList<GodotChecks.Finding> findings = GodotChecks.Findings(scenes, asked, found, _ => true);
        Assert.Equal(["main.tscn:8 sets Lives on Main, which Main has no property of", "main.tscn:9 sets Tint on Main, which Main has no property of"], findings.Select(f => f.Message));
        Assert.Equal([(GodotFixKind.Export, "Lives", "int"), (GodotFixKind.Export, "Tint", "Color")], findings.Select(f => (f.Offer!.Make!.Kind, f.Offer.Make.Name, f.Offer.Make.Type!)));
        GodotChecks.SceneFinding scene = Assert.Single(GodotChecks.SceneFindings(scenes, "res://main.tscn", GodotChecks.SceneAsks("/game/Main.cs", scenes, "res://main.tscn"),
            found with { Properties = [null, new GodotSignature("Tint", ["Color"], 0, null, null, null), new GodotSignature("Target", ["Label"], 0, null, null, null)] }, _ => true), f => f.Message.Contains("property", StringComparison.Ordinal));
        Assert.Equal("Main, a Node2D (Main.cs), has no property Lives, so Godot drops its value", scene.Message);
    }

    /// <summary>A node path a scene sets on a script's property that names a node of another type than the property's, or
    /// no node, is an error at the path; one the property takes is not.</summary>
    [Fact]
    public void A_scene_marks_a_node_path_it_sets_that_names_no_node_or_one_of_another_type()
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(Scene.Replace("script = ExtResource(\"1\")\n",
            "script = ExtResource(\"1\")\nDisplay = NodePath(\"Hud\")\nScore = NodePath(\"Hud/Score\")\nTarget = NodePath(\"Hud/Gone\")\n", StringComparison.Ordinal)) });
        GodotCodeRequest asked = GodotChecks.SceneAsks("/game/Main.cs", scenes, "res://main.tscn");
        Assert.Equal(["Display", "Score", "Target"], asked.Properties.Select(p => p.Name));
        GodotTypeRef label = new("Label", true, "Label");
        GodotCodeFound found = Found(asked, [], methods: [new GodotSignature("OnPressed", [], 0, null, null, null), new GodotSignature("OnToggled", ["bool", "string"], 2, null, null, null)])
            with { Properties = [.. new[] { label, label, new GodotTypeRef("Node", true, "Node") }.Select(n => new GodotSignature("x", ["Node"], 0, null, null, null) { Node = n })] };
        string text = scenes.Files["res://main.tscn"].Text;
        Assert.Equal([("Hud", "Main's Display takes a Label: 'Hud' is a CanvasLayer (Hud.cs)"), ("Hud/Gone", "Main's Target names no node: Hud has no child Gone")],
            GodotChecks.SceneFindings(scenes, "res://main.tscn", asked, found, _ => true).Select(f => (text[f.Start..f.End], f.Message)));
    }

    /// <summary>A SubResource no sub_resource of the file has is an error at its id, inside an array too; one the file has, and
    /// the words of one inside a string, are not.</summary>
    [Fact]
    public void A_scene_marks_a_sub_resource_id_it_has_no_sub_resource_of()
    {
        const string scene = """
            [gd_scene format=3]

            [sub_resource type="RectangleShape2D" id="Box_a"]

            [node name="Main" type="Area2D"]
            shape = SubResource("Box_a")
            shapes = [SubResource("Box_b"), "SubResource(\"Box_c\")"]
            old = SubResource( 3 )

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(scene) });
        Assert.Equal([("Box_b", "No sub_resource of this file has the id \"Box_b\""), ("3", "No sub_resource of this file has the id \"3\"")],
            GodotChecks.SceneFindings(scenes, "res://main.tscn", GodotChecks.SceneAsks("/game/Main.cs", scenes, "res://main.tscn"), GodotCodeFound.None, _ => true)
                .Select(f => (scene[f.Start..f.End], f.Message)));
    }

    [Theory]
    [InlineData("true", "bool")]
    [InlineData("-3", "int")]
    [InlineData("2.5", "float")]
    [InlineData("1e-05", "float")]
    [InlineData("\"a\"", "String")]
    [InlineData("&\"run\"", "StringName")]
    [InlineData("^\"Hud\"", "NodePath")]
    [InlineData("[1, 2]", "Array")]
    [InlineData("Array[int]([1])", "Array")]
    [InlineData("{}", "Dictionary")]
    [InlineData("Vector2(1, 2)", "Vector2")]
    [InlineData("PackedStringArray(\"a\")", "PackedStringArray")]
    [InlineData("null", null)]
    public void A_scenes_value_is_of_the_godot_type_its_text_writes(string written, string? type) => Assert.Equal(type, GodotChecks.TypeOfValue(written));

    [Fact]
    public void An_arrays_items_are_read_at_its_top() =>
        Assert.Equal(["1", "\"a, b\"", "Vector2(1, 2)", "[3]"], GodotChecks.Items("[1, \"a, b\", Vector2(1, 2), [3]]"));

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

    /// <summary>An exported node a scene sets to a node of another type, or a NodePath it sets to no node, is an error at the
    /// member's name naming the scene and line; one of the type, or a path leaving the tree, is not.</summary>
    [Fact]
    public void An_exported_node_set_to_another_type_or_no_node_is_an_error()
    {
        const string scene = """
            [gd_scene format=3]

            [ext_resource type="Script" path="res://Main.cs" id="1"]

            [node name="Main" type="Node2D"]
            script = ExtResource("1")
            Display = NodePath("Hud")
            Shown = NodePath("Hud/Score")
            Target = NodePath("Hud/Gone")
            Above = NodePath("../Away")

            [node name="Hud" type="CanvasLayer" parent="."]

            [node name="Score" type="Label" parent="Hud"]

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(scene) });
        GodotTypeRef label = new("Label", true, "Label");
        GodotTypeRef node = new("Node", true, "Node");
        GodotMember Export(string name, GodotTypeRef type, int line) => new(name, type.Shown, "/game/Main.cs", new Place(line, 17), new Place(line, 17 + name.Length)) { Node = type };
        GodotClassFound main = Main with { Exports = [Export("Display", label, 4), Export("Shown", label, 5), Export("Target", node, 6), Export("Above", node, 7)] };
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotCodeFound found = new([main], [], [],
            [new GodotChain("Label", ["Label", "Control", "CanvasItem", "Node", "Object"], "Label", "Label"), new GodotChain("CanvasLayer", ["CanvasLayer", "Node", "Object"], "CanvasLayer", "CanvasLayer"),
                new GodotChain("Node2D", ["Node2D", "CanvasItem", "Node", "Object"], "Node2D", "Node2D")], [], [], []);
        Assert.Equal(
        [
            (4, "main.tscn:7 sets Display to 'Hud', a CanvasLayer, not a Label"),
            (6, "main.tscn:9 sets Target to 'Hud/Gone', which names no node: Hud has no child Gone"),
        ], GodotChecks.Findings(scenes, asked, found, _ => true).Select(f => (f.At.Line, f.Message)));
    }

    /// <summary>A uid:// a string gives that no file has is an error once Godot keeps a cache of the project's uids (without
    /// one, a uid may name a file Passenger does not read); one naming a file gone is one too; in a scene, an external
    /// resource whose path is gone but whose uid names a file is a warning that Godot loads it by its uid.</summary>
    [Fact]
    public void A_uid_no_file_has_is_an_error_and_a_scenes_resource_found_by_its_uid_a_warning()
    {
        const string scene = """
            [gd_scene format=3]

            [ext_resource type="PackedScene" uid="uid://bplayer" path="res://old_player.tscn" id="1"]

            [node name="Main" type="Node2D"]

            [node name="Player" parent="." instance=ExtResource("1")]

            """;
        Dictionary<string, GodotFile> files = new()
        {
            ["res://main.tscn"] = GodotFile.Parse(scene),
            ["res://player.tscn"] = GodotFile.Parse("[gd_scene format=3 uid=\"uid://bplayer\"]\n\n[node name=\"Player\" type=\"Node2D\"]\n"),
        };
        GodotScenes cached = new("/game", files, new Dictionary<string, string> { ["uid://cgone"] = "res://gone.tscn" }, cached: true);
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", cached, ["res://Main.cs"]);
        GodotCodeFound found = Found(asked, [], [new GodotPathRead("uid://bplayer", At, End), new GodotPathRead("uid://bnone", At, End), new GodotPathRead("uid://cgone", At, End)]);
        bool Exists(string path) => !path.EndsWith("gone.tscn", StringComparison.Ordinal) && !path.EndsWith("old_player.tscn", StringComparison.Ordinal);
        Assert.Equal(["No file of the Godot project has uid://bnone", "uid://cgone is res://gone.tscn, which is not in the Godot project"],
            GodotChecks.Findings(cached, asked, found, Exists).Select(f => f.Message));
        GodotScenes uncached = new("/game", files);
        Assert.Empty(GodotChecks.Findings(uncached, asked, found with { Resources = [new GodotPathRead("uid://bnone", At, End)] }, Exists));

        GodotChecks.SceneFinding loaded = Assert.Single(GodotChecks.SceneFindings(cached, "res://main.tscn", GodotChecks.SceneAsks("/game/Main.cs", cached, "res://main.tscn"), GodotCodeFound.None, Exists));
        Assert.Equal(("res://old_player.tscn", false, "No file res://old_player.tscn is in the Godot project: Godot loads uid://bplayer, res://player.tscn, by its uid"),
            (scene[loaded.Start..loaded.End], loaded.Error, loaded.Message));
    }

    /// <summary>A string naming an input action project.godot lacks, and not one of Godot's own, is an error saying where Godot
    /// looks; a group no scene gives a node is one unless the code adds it; an animation its player's libraries lack is one
    /// where the code tells the player by its path, and never where it does not; an autoload a path names through /root that
    /// project.godot lacks, and a node its scene lacks, are errors too.</summary>
    [Fact]
    public void A_string_naming_what_the_project_does_not_define_is_an_error_saying_where_godot_looks()
    {
        const string scene = """
            [gd_scene format=3]

            [ext_resource type="Script" path="res://Main.cs" id="1"]

            [sub_resource type="AnimationLibrary" id="AnimationLibrary_1"]
            _data = {
            &"run": SubResource("Animation_1")
            }

            [node name="Main" type="Node2D"]
            script = ExtResource("1")

            [node name="Enemy" type="Area2D" parent="." groups=["enemies"]]

            [node name="Anim" type="AnimationPlayer" parent="."]
            libraries/ = SubResource("AnimationLibrary_1")

            """;
        const string project = "[autoload]\n\nHud=\"*res://hud.tscn\"\n\n[input]\n\njump={\n\"events\": []\n}\n";
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>
        {
            ["res://main.tscn"] = GodotFile.Parse(scene),
            ["res://hud.tscn"] = GodotFile.Parse("[gd_scene format=3]\n\n[node name=\"Hud\" type=\"CanvasLayer\"]\n\n[node name=\"Score\" type=\"Label\" parent=\".\"]\n"),
        }, texts: new Dictionary<string, string> { ["res://project.godot"] = project });
        GodotCodeRequest asked = GodotChecks.Asks("/game/Main.cs", scenes, ["res://Main.cs"]);
        GodotKindRead Read(GodotStringKind kind, string name, string? node = null, bool adds = false) => new(kind, name, At, End, true, adds, 0, node, null, null);
        GodotCodeFound found = Found(asked, [Call("/root/Hud/Score", null), Call("/root/Hud/Scor", null), Call("/root/Main", null), Call("/root/Gone", null)]) with
        {
            Kinds =
            [
                Read(GodotStringKind.Action, "jump"), Read(GodotStringKind.Action, "ui_accept"), Read(GodotStringKind.Action, "jmp"),
                Read(GodotStringKind.Group, "enemies"), Read(GodotStringKind.Group, "allies"), Read(GodotStringKind.Group, "spawned"), Read(GodotStringKind.Group, "made", adds: true),
                Read(GodotStringKind.Animation, "run", "Anim"), Read(GodotStringKind.Animation, "walk", "Anim"), Read(GodotStringKind.Animation, "fly"),
                Read(GodotStringKind.Translation, "HELLO"),
            ],
            Added = [new GodotKindAdded(GodotStringKind.Group, "spawned")],
        };
        Assert.Equal(
        [
            "No input action 'jmp' is in project.godot's [input], nor among Godot's own ui_ actions",
            "No group 'allies' is given to a node by the project's scenes, nor added by its code",
            "No animation 'walk' is in the animation libraries of Anim (main.tscn:15)",
            "hud.tscn:3 has no node 'Scor' under Hud, the autoload Hud: Hud has no child Scor",
            "No autoload 'Gone' is in project.godot's [autoload], nor is a scene's root node so named",
        ], GodotChecks.Findings(scenes, asked, found, _ => true).Select(f => f.Message));

        GodotChecks.Candidates actions = GodotChecks.Named(scenes, GodotStringKind.Action, [], null, null, null);
        Assert.Equal("jump", actions.Names[0].Name);
        Assert.Contains(actions.Names, n => n.Name == "ui_accept" && n.Places.Count == 0);
        Assert.False(GodotChecks.Named(scenes, GodotStringKind.Animation, ["res://Main.cs"], null, null, null).Whole, "a player the code does not tell is never checked");
    }
}
