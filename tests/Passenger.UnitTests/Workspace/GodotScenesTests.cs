using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's text scenes as Passenger reads them: each tag and its properties by place, a value's string through
/// escapes and calls, a scene's tree with the scenes it instances built in, where a node path leads from a node, and
/// the edits a node's rename makes in every scene that names it.</summary>
public class GodotScenesTests
{
    private const string Main = """
        [gd_scene load_steps=4 format=3 uid="uid://cecaux1sm7mo0"]

        [ext_resource type="Script" uid="uid://b1" path="res://Main.cs" id="1_main"]
        [ext_resource type="PackedScene" path="res://player.tscn" id="2_player"]

        [sub_resource type="RectangleShape2D" id="RectangleShape2D_1"]
        size = Vector2(10, 10)

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")
        Greeting = "say \"hi\"
        twice"
        Target = NodePath("Hud/Score")

        [node name="Hud" type="CanvasLayer" parent="."]

        [node name="Score" type="Label" parent="Hud"]
        unique_name_in_owner = true
        text = "0"

        [node name="Start" type="Button" parent="Hud"]
        layout_mode = 0
        offset_right = 8.0

        [node name="Player" parent="." instance=ExtResource("2_player")]
        position = Vector2(100, 100)

        [node name="Sprite" parent="Player" index="0"]
        modulate = Color(1, 0, 0, 1)

        [connection signal="pressed" from="Hud/Start" to="." method="OnStartPressed" flags=3 binds= [1, "two", [3, 4]]]
        [connection signal="hit" from="Player" to="." method="OnHit" unbinds=1]

        [editable path="Player"]

        """;

    private const string Player = """
        [gd_scene format=3]

        [ext_resource type="Script" path="res://Player.cs" id="1"]

        [node name="Player" type="CharacterBody2D"]
        script = ExtResource("1")

        [node name="Sprite" type="Sprite2D" parent="."]

        [node name="Weapon" type="Node2D" parent="Sprite"]
        unique_name_in_owner = true

        """;

    private static GodotScenes Scenes() => new("/game", new Dictionary<string, GodotFile>
    {
        ["res://main.tscn"] = GodotFile.Parse(Main),
        ["res://player.tscn"] = GodotFile.Parse(Player),
    });

    /// <summary>A scene's tags, tree, instances, paths and scripts are read as Godot builds them, however
    /// deep.</summary>
    [Fact]
    public void A_scene_is_read_as_godot_builds_its_tree() => Cases.All(
        (nameof(A_scenes_tree_builds_in_the_scenes_it_instances_their_overrides_and_owners),
            A_scenes_tree_builds_in_the_scenes_it_instances_their_overrides_and_owners),
        (nameof(A_node_path_leads_through_names_parents_and_unique_names_and_says_where_it_goes_missing),
            A_node_path_leads_through_names_parents_and_unique_names_and_says_where_it_goes_missing),
        (nameof(Resources_holding_each_other_are_walked_once_each), Resources_holding_each_other_are_walked_once_each),
        (nameof(A_long_chain_of_built_in_resources_is_followed_to_its_end), A_long_chain_of_built_in_resources_is_followed_to_its_end),
        (nameof(A_tree_deeper_than_a_stack_holds_is_built_and_walked_and_ends_nothing),
            A_tree_deeper_than_a_stack_holds_is_built_and_walked_and_ends_nothing));

    /// <summary>A node's rename writes every path through it, in scenes, scripts, tracks and built-in
    /// resources.</summary>
    [Fact]
    public void A_nodes_rename_follows_every_path_through_it() => Cases.All(
        (nameof(A_nodes_rename_writes_its_name_and_every_path_through_it_in_each_scene_that_holds_it),
            A_nodes_rename_writes_its_name_and_every_path_through_it_in_each_scene_that_holds_it),
        (nameof(A_nodes_rename_follows_a_unique_name_and_every_node_path_a_property_holds_at_any_depth),
            A_nodes_rename_follows_a_unique_name_and_every_node_path_a_property_holds_at_any_depth),
        (nameof(A_nodes_rename_reaches_the_paths_a_gdscript_leads_from_its_node), A_nodes_rename_reaches_the_paths_a_gdscript_leads_from_its_node),
        (nameof(A_name_godot_refuses_or_one_beside_it_already_is_no_rename), A_name_godot_refuses_or_one_beside_it_already_is_no_rename),
        (nameof(A_nodes_rename_follows_the_paths_of_its_players_animation_tracks_their_properties_kept),
            A_nodes_rename_follows_the_paths_of_its_players_animation_tracks_their_properties_kept),
        (nameof(A_nodes_rename_follows_the_paths_of_the_built_in_resources_a_node_holds_led_from_that_node_at_any_depth),
            A_nodes_rename_follows_the_paths_of_the_built_in_resources_a_node_holds_led_from_that_node_at_any_depth));

    /// <summary>Paths in shaders, scenes, scripts and resources follow the files and folders moved.</summary>
    [Fact]
    public void Paths_in_godot_files_follow_files_and_folders_moved() => Cases.All(
        (nameof(A_shaders_includes_follow_files_and_folders_moved), A_shaders_includes_follow_files_and_folders_moved),
        (nameof(A_gdscripts_paths_follow_files_and_folders_moved), A_gdscripts_paths_follow_files_and_folders_moved),
        (nameof(A_path_climbing_out_of_the_project_is_left_as_its_file_moves), A_path_climbing_out_of_the_project_is_left_as_its_file_moves),
        (nameof(A_moved_files_and_folders_res_paths_follow_them_in_every_godot_file),
            A_moved_files_and_folders_res_paths_follow_them_in_every_godot_file));

    private void A_scenes_tree_builds_in_the_scenes_it_instances_their_overrides_and_owners()
    {
        GodotTreeNode root = Scenes().Tree("res://main.tscn")!;
        Assert.Equal(["Main", "Hud", "Score", "Start", "Player", "Sprite", "Weapon"], root.Below().Select(n => n.Name));
        GodotTreeNode player = root.Child("Player")!;
        Assert.Equal(("CharacterBody2D", "res://Player.cs", "res://player.tscn"), (player.Type, player.Script, player.Instance));
        GodotTreeNode sprite = player.Child("Sprite")!;
        Assert.Equal("Sprite2D", sprite.Type);
        Assert.Equal(["res://player.tscn", "res://main.tscn"], sprite.Written.Select(w => w.Scene));
        Assert.Same(player, sprite.Owner);
        Assert.Same(root, player.Owner);
        Assert.Equal("Player/Sprite/Weapon", sprite.Child("Weapon")!.Path);
    }

    private void A_node_path_leads_through_names_parents_and_unique_names_and_says_where_it_goes_missing()
    {
        GodotScenes scenes = Scenes();
        GodotTreeNode root = scenes.Tree("res://main.tscn")!;
        Assert.Equal("Score", GodotScenes.Resolve(root, "Hud/Score").Found?.Name);
        Assert.Equal("Score", GodotScenes.Resolve(root, "%Score").Found?.Name);
        Assert.Equal("Score", GodotScenes.Resolve(root, "Hud/Score:text").Found?.Name);
        Assert.Equal("Start", GodotScenes.Resolve(root.Child("Hud")!.Child("Score")!, "../Start").Found?.Name);
        GodotResolved missing = GodotScenes.Resolve(root, "Hud/Scor");
        Assert.Null(missing.Found);
        Assert.Equal((4, 8), missing.Missing);
        Assert.Equal("Hud", missing.Reached?.Name);
        Assert.True(GodotScenes.Resolve(root, "..").Unknown, "A path above the scene's root is not the scene's to say");
        Assert.True(GodotScenes.Resolve(root, "/root/Main").Unknown, "An absolute path is not the scene's to say");

        // The player's script finds its unique Weapon in its own scene; instanced in main, main's unique Score through its owner.
        GodotTreeNode player = scenes.Tree("res://player.tscn")!;
        Assert.Equal("Weapon", GodotScenes.Resolve(player, "%Weapon").Found?.Name);
        Assert.Null(GodotScenes.Resolve(player, "%Score").Found);
        Assert.Equal("Weapon", GodotScenes.Resolve(root.Child("Player")!, "%Weapon").Found?.Name);
        Assert.Equal("Score", GodotScenes.Resolve(root.Child("Player")!, "%Score").Found?.Name);
        Assert.Equal(["Hud", "Hud/Score", "Hud/Start", "Player", "Player/Sprite", "Player/Sprite/Weapon", "%Score"], GodotScenes.PathsFrom(root).Select(p => p.Path));
    }

    private void A_nodes_rename_writes_its_name_and_every_path_through_it_in_each_scene_that_holds_it()
    {
        GodotScenes scenes = Scenes();
        GodotTreeNode hud = scenes.Tree("res://main.tscn")!.Child("Hud")!;
        IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> edits = scenes.Renames(hud, "Overlay");
        string renamed = Apply(Main, edits["res://main.tscn"]);
        Assert.Contains("[node name=\"Overlay\" type=\"CanvasLayer\" parent=\".\"]", renamed, StringComparison.Ordinal);
        Assert.Contains("[node name=\"Score\" type=\"Label\" parent=\"Overlay\"]", renamed, StringComparison.Ordinal);
        Assert.Contains("[node name=\"Start\" type=\"Button\" parent=\"Overlay\"]", renamed, StringComparison.Ordinal);
        Assert.Contains("from=\"Overlay/Start\"", renamed, StringComparison.Ordinal);
        Assert.Contains("Target = NodePath(\"Overlay/Score\")", renamed, StringComparison.Ordinal);
        Assert.DoesNotContain("Hud", renamed, StringComparison.Ordinal);

        // A node of an instanced scene is renamed where that scene declares it, and where an instancing scene names it.
        GodotTreeNode sprite = scenes.Tree("res://main.tscn")!.Child("Player")!.Child("Sprite")!;
        edits = scenes.Renames(sprite, "Body");
        Assert.Contains("[node name=\"Body\" type=\"Sprite2D\" parent=\".\"]", Apply(Player, edits["res://player.tscn"]), StringComparison.Ordinal);
        Assert.Contains("[node name=\"Weapon\" type=\"Node2D\" parent=\"Body\"]", Apply(Player, edits["res://player.tscn"]), StringComparison.Ordinal);
        Assert.Contains("[node name=\"Body\" parent=\"Player\" index=\"0\"]", Apply(Main, edits["res://main.tscn"]), StringComparison.Ordinal);
    }

    /// <summary>A NodePath is a path through a node wherever a node's property holds it, in an array or a dictionary at any
    /// depth, and led as Godot leads it from the node holding it, a <c>%Name</c> finding the unique node it names.</summary>
    private void A_nodes_rename_follows_a_unique_name_and_every_node_path_a_property_holds_at_any_depth()
    {
        const string scene = """
            [gd_scene format=3]

            [node name="Main" type="Node2D"]
            Unique = NodePath("%Score")
            Below = NodePath("%Score/Label:text")
            Listed = [NodePath("Hud/Score"), NodePath("Hud/Start")]
            Typed = Array[NodePath]([NodePath("Hud/Score:text")])
            Mapped = { "a": NodePath("Hud/Score"), "b": [NodePath("%Score")] }
            Words = "NodePath(\"Hud/Score\")"
            Far = NodePath("/root/Hud/Score")
            Short = ^"Hud/Score"

            [node name="Hud" type="CanvasLayer" parent="."]
            Near = NodePath("Score")
            Up = NodePath("../Hud/Score")

            [node name="Score" type="Label" parent="Hud"]
            unique_name_in_owner = true

            [node name="Label" type="Label" parent="Hud/Score"]

            [node name="Start" type="Button" parent="Hud"]

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(scene) });
        string renamed = Apply(scene, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!, "Points")["res://main.tscn"]);
        Assert.Equal(scene.Replace("%Score", "%Points", StringComparison.Ordinal).Replace("Hud/Score", "Hud/Points", StringComparison.Ordinal).Replace("NodePath(\"Score\")", "NodePath(\"Points\")", StringComparison.Ordinal)
            .Replace("name=\"Score\"", "name=\"Points\"", StringComparison.Ordinal).Replace("NodePath(\\\"Hud/Points\\\")", "NodePath(\\\"Hud/Score\\\")", StringComparison.Ordinal)
            .Replace("/root/Hud/Points", "/root/Hud/Score", StringComparison.Ordinal), renamed);
    }

    /// <summary>A GDScript's own paths follow a node's rename wherever they reach it from the node the script runs on, or a node
    /// running a script built on it; a path reaching another node of the name is left.</summary>
    private void A_nodes_rename_reaches_the_paths_a_gdscript_leads_from_its_node()
    {
        const string scene = """
            [gd_scene format=3]

            [ext_resource type="Script" path="res://main.gd" id="1"]

            [node name="Main" type="Node"]
            script = ExtResource("1")

            [node name="Score" type="Label" parent="."]
            unique_name_in_owner = true

            [node name="Other" type="Node" parent="."]

            [node name="Score" type="Label" parent="Other"]

            """;
        const string based = "extends Node\n\n\nfunc go():\n\t$Score.show()\n\t%Score.hide()\n\tget_node(\"./Score\")\n\tget_node(\"Other/Score\")\n\tget_node(\"Other\")\n";
        Dictionary<string, string> texts = new(StringComparer.Ordinal) { ["res://main.gd"] = "extends \"res://base.gd\"\n", ["res://base.gd"] = based };
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(scene) }, texts: texts);
        GodotTreeNode score = scenes.Tree("res://main.tscn")!.Child("Score")!;
        Assert.Equal(based.Replace("$Score", "$Points", StringComparison.Ordinal).Replace("%Score", "%Points", StringComparison.Ordinal).Replace("./Score", "./Points", StringComparison.Ordinal),
            Apply(based, scenes.ScriptRenames("res://base.gd", based, [score], "Points")!));
    }

    private void A_name_godot_refuses_or_one_beside_it_already_is_no_rename()
    {
        GodotScenes scenes = Scenes();
        GodotTreeNode score = scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!;
        Assert.Equal("Hud has a node named Start already, in main.tscn", scenes.RenameProblem(score, "Start"));
        Assert.Equal("A node's name never holds '/', so Score keeps its name", scenes.RenameProblem(score, "A/B"));
        Assert.Equal("'A\nB' is no node's name", scenes.RenameProblem(score, "A\nB"));
        Assert.Null(scenes.RenameProblem(score, "Points"));
    }

    private const string Animated = """
        [gd_scene format=3]

        [ext_resource type="Animation" path="res://blink.tres" id="1_blink"]

        [sub_resource type="Animation" id="Animation_walk"]
        resource_name = "walk"
        tracks/0/type = "value"
        tracks/0/imported = false
        tracks/0/path = NodePath("Hud/Score:text")
        tracks/1/type = "method"
        tracks/1/imported = false
        tracks/1/path = NodePath("Hud")
        tracks/2/type = "value"
        tracks/2/imported = true
        tracks/2/path = NodePath("Hud/Score:modulate")
        tracks/3/type = "bezier"
        tracks/3/imported = false
        tracks/3/path = NodePath("Other:position:x")
        tracks/4/type = "value"
        tracks/4/imported = false
        tracks/4/path = NodePath("%Score:visible")

        [sub_resource type="AnimationLibrary" id="AnimationLibrary_1"]
        _data = {
        &"walk": SubResource("Animation_walk"),
        &"blink": ExtResource("1_blink")
        }

        [node name="Main" type="Node2D"]

        [node name="Hud" type="CanvasLayer" parent="."]

        [node name="Score" type="Label" parent="Hud"]
        unique_name_in_owner = true

        [node name="Other" type="Node2D" parent="."]

        [node name="Moves" type="AnimationPlayer" parent="."]
        libraries/ = SubResource("AnimationLibrary_1")

        """;

    private const string Blink = """
        [gd_resource type="Animation" format=3]

        [resource]
        resource_name = "blink"
        tracks/0/type = "value"
        tracks/0/imported = false
        tracks/0/path = NodePath("Hud/Score:modulate")

        """;

    private static GodotScenes AnimatedScenes(string scene) => new("/game", new Dictionary<string, GodotFile>
    {
        ["res://main.tscn"] = GodotFile.Parse(scene),
        ["res://blink.tres"] = GodotFile.Parse(Blink),
    });

    /// <summary>Godot's editor rewrites a track's path as a node goes by another name, its property kept, and leaves an
    /// imported track to its import: a rename left without it stops the animation reaching the node.</summary>
    private void A_nodes_rename_follows_the_paths_of_its_players_animation_tracks_their_properties_kept()
    {
        GodotScenes scenes = AnimatedScenes(Animated);

        IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> edits = scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!, "Overlay");

        string renamed = Apply(Animated, edits["res://main.tscn"]);
        Assert.Contains("tracks/0/path = NodePath(\"Overlay/Score:text\")", renamed, StringComparison.Ordinal);
        Assert.Contains("tracks/1/path = NodePath(\"Overlay\")", renamed, StringComparison.Ordinal);
        Assert.Contains("tracks/2/path = NodePath(\"Hud/Score:modulate\")", renamed, StringComparison.Ordinal);
        Assert.Contains("tracks/3/path = NodePath(\"Other:position:x\")", renamed, StringComparison.Ordinal);
        Assert.Contains("tracks/0/path = NodePath(\"Overlay/Score:modulate\")", Apply(Blink, edits["res://blink.tres"]), StringComparison.Ordinal);

        string unique = Apply(Animated, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!, "Points")["res://main.tscn"]);
        Assert.Contains("tracks/0/path = NodePath(\"Hud/Points:text\")", unique, StringComparison.Ordinal);
        Assert.Contains("tracks/4/path = NodePath(\"%Points:visible\")", unique, StringComparison.Ordinal);
    }

    /// <summary>As Godot 4.7.2 saved it: a script's resource on a node's metadata holding another, and a ViewportTexture on a
    /// sprite and one inside a mesh's material.</summary>
    private const string BuiltIn = """
        [gd_scene format=3]

        [ext_resource type="Script" path="res://aim.gd" id="1_ldub0"]

        [sub_resource type="ViewportTexture" id="ViewportTexture_ag75j"]
        viewport_path = NodePath("Hud/View")

        [sub_resource type="StandardMaterial3D" id="StandardMaterial3D_fycry"]
        albedo_texture = SubResource("ViewportTexture_ag75j")

        [sub_resource type="ViewportTexture" id="ViewportTexture_s3u02"]
        viewport_path = NodePath("Hud/View")

        [sub_resource type="Resource" id="Resource_g8drb"]
        script = ExtResource("1_ldub0")
        target = NodePath("../Hud/Score")

        [sub_resource type="Resource" id="Resource_fji0u"]
        script = ExtResource("1_ldub0")
        target = NodePath("../Hud/Score:text")
        others = Array[NodePath]([NodePath("../Hud")])
        inner = SubResource("Resource_g8drb")

        [node name="Main" type="Node2D" unique_id=1964523380]

        [node name="Hud" type="Node2D" parent="." unique_id=566957418]

        [node name="Score" type="Label" parent="Hud" unique_id=1535000078]

        [node name="View" type="SubViewport" parent="Hud" unique_id=1916850243]

        [node name="Mesh" type="MeshInstance3D" parent="Hud" unique_id=568256544]
        material_override = SubResource("StandardMaterial3D_fycry")

        [node name="Screen" type="Sprite2D" parent="." unique_id=1890275947]
        texture = SubResource("ViewportTexture_s3u02")
        metadata/aim = SubResource("Resource_fji0u")

        """;

    private static GodotScenes BuiltInScenes(string scene) => new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(scene) });

    /// <summary>Godot's editor walks a node's built-in resources as it walks the node: left out, a resource's path goes on
    /// naming a node that is gone, the name kept only in the scene's own sections.</summary>
    private void A_nodes_rename_follows_the_paths_of_the_built_in_resources_a_node_holds_led_from_that_node_at_any_depth()
    {
        GodotScenes scenes = BuiltInScenes(BuiltIn);
        GodotTreeNode hud = scenes.Tree("res://main.tscn")!.Child("Hud")!;

        string score = Apply(BuiltIn, scenes.Renames(hud.Child("Score")!, "Points")["res://main.tscn"]);
        Assert.Contains("target = NodePath(\"../Hud/Points:text\")", score, StringComparison.Ordinal);
        Assert.Contains("target = NodePath(\"../Hud/Points\")", score, StringComparison.Ordinal);

        string overlay = Apply(BuiltIn, scenes.Renames(hud, "Overlay")["res://main.tscn"]);
        Assert.Contains("others = Array[NodePath]([NodePath(\"../Overlay\")])", overlay, StringComparison.Ordinal);
        Assert.Contains("target = NodePath(\"../Overlay/Score:text\")", overlay, StringComparison.Ordinal);
    }

    private void Resources_holding_each_other_are_walked_once_each()
    {
        const string Round = """
            [gd_scene format=3]

            [sub_resource type="Resource" id="Resource_a"]
            next = SubResource("Resource_b")
            target = NodePath("../Hud")

            [sub_resource type="Resource" id="Resource_b"]
            next = SubResource("Resource_a")

            [node name="Main" type="Node2D"]

            [node name="Hud" type="Node2D" parent="."]

            [node name="Holder" type="Node2D" parent="."]
            metadata/aim = SubResource("Resource_a")

            """;
        GodotScenes scenes = BuiltInScenes(Round);

        Assert.Contains("target = NodePath(\"../Overlay\")", Apply(Round, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!, "Overlay")["res://main.tscn"]), StringComparison.Ordinal);
    }

    /// <summary>A chain of resources as long as a file can hold is walked with no stack to run out of.</summary>
    private void A_long_chain_of_built_in_resources_is_followed_to_its_end()
    {
        const int Length = 20000;
        System.Text.StringBuilder chain = new("[gd_scene format=3]\n\n[sub_resource type=\"Resource\" id=\"R_0\"]\ntarget = NodePath(\"../Hud\")\n\n");
        for (int i = 1; i < Length; i++)
        {
            chain.Append($"[sub_resource type=\"Resource\" id=\"R_{i}\"]\nnext = SubResource(\"R_{i - 1}\")\n\n");
        }
        chain.Append($"[node name=\"Main\" type=\"Node2D\"]\n\n[node name=\"Hud\" type=\"Node2D\" parent=\".\"]\n\n[node name=\"Holder\" type=\"Node2D\" parent=\".\"]\nmetadata/aim = SubResource(\"R_{Length - 1}\")\n");
        string scene = chain.ToString();
        GodotScenes scenes = BuiltInScenes(scene);

        Assert.Contains("target = NodePath(\"../Overlay\")", Apply(scene, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!, "Overlay")["res://main.tscn"]), StringComparison.Ordinal);
    }

    private const string BinaryLibrary = """
        [gd_scene format=3]

        [ext_resource type="AnimationLibrary" path="res://moves.res" id="1_moves"]

        [node name="Main" type="Node2D"]

        [node name="Hud" type="CanvasLayer" parent="."]

        [node name="Score" type="Label" parent="Hud"]

        [node name="Moves" type="AnimationPlayer" parent="."]
        libraries = {
        &"": ExtResource("1_moves")
        }

        """;

    private static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        foreach (TextEdit edit in edits.OrderByDescending(e => e.Start))
        {
            text = text[..edit.Start] + edit.Text + text[edit.End..];
        }
        return text;
    }

    /// <summary>An include follows the file it names; one written from the shader's folder is left while both move together
    /// and written from the top once the move leaves it naming another; a uid:// names the file wherever it went.</summary>
    private void A_shaders_includes_follow_files_and_folders_moved() => Cases.Each<(string Code, string Shader, string From, string To, string Moved)>(
    [
        ("#include \"res://common/noise.gdshaderinc\"\n", "res://shader.gdshader", "res://common/noise.gdshaderinc", "res://lib/noise.gdshaderinc", "#include \"res://lib/noise.gdshaderinc\"\n"),
        ("#include \"noise.gdshaderinc\"\n", "res://common/shader.gdshader", "res://common/noise.gdshaderinc", "res://lib/noise.gdshaderinc", "#include \"res://lib/noise.gdshaderinc\"\n"),
        ("#include \"noise.gdshaderinc\"\n", "res://common/shader.gdshader", "res://common/", "res://lib/", "#include \"noise.gdshaderinc\"\n"),
        ("// #include \"res://common/noise.gdshaderinc\"\n", "res://shader.gdshader", "res://common/", "res://lib/", "// #include \"res://common/noise.gdshaderinc\"\n"),
    ], row => A_shaders_includes_follow_files_and_folders_moved(row.Code, row.Shader, row.From, row.To, row.Moved));

    private void A_shaders_includes_follow_files_and_folders_moved(string code, string shader, string from, string to, string moved)
    {
        Assert.Equal(moved, Apply(code, GodotScenes.MovedShaderEdits(code, shader, [(from, to)])));
    }

    /// <summary>A GDScript's strings follow a move as Godot reads them: a res:// path in any string, each in its own quotes, and
    /// a path written from the script's folder in a preload or extends where the move leaves it naming another; a comment, a
    /// string that says no moved path, and a load's relative string, which Godot reads from the top, are left.</summary>
    private void A_gdscripts_paths_follow_files_and_folders_moved()
    {
        const string script = """
            extends "res://base.gd"

            # load("res://player.tscn")
            const Player = preload("res://player.tscn")
            const Beside = preload("beside.tscn")
            var enemy = load('res://enemy.tscn::1')
            var other = load("res://other.tscn")
            var relative = load("player.tscn")
            var art = "res://art/hero.png"

            """;
        List<(string, string)> moves = [("res://player.tscn", "res://actors/player.tscn"), ("res://base.gd", "res://lib/base.gd"), ("res://enemy.tscn", "res://actors/enemy.tscn"), ("res://art/", "res://sprites/"),
            ("res://beside.tscn", "res://actors/beside.tscn")];
        string moved = Apply(script, GodotScenes.MovedScriptEdits(script, "res://main.gd", moves));
        Assert.Equal(script.Replace("\"res://base.gd\"", "\"res://lib/base.gd\"", StringComparison.Ordinal).Replace("preload(\"res://player.tscn\")", "preload(\"res://actors/player.tscn\")", StringComparison.Ordinal)
            .Replace("preload(\"beside.tscn\")", "preload(\"res://actors/beside.tscn\")", StringComparison.Ordinal).Replace("'res://enemy.tscn::1'", "'res://actors/enemy.tscn::1'", StringComparison.Ordinal)
            .Replace("res://art/hero.png", "res://sprites/hero.png", StringComparison.Ordinal), moved);

        // A script moved alone keeps naming what it named; one moved with what it names beside it keeps its words.
        const string beside = "extends \"base.gd\"\n\nconst Kept = preload(\"../art/hero.png\")\n";
        Assert.Equal("extends \"res://game/base.gd\"\n\nconst Kept = preload(\"res://art/hero.png\")\n",
            Apply(beside, GodotScenes.MovedScriptEdits(beside, "res://game/main.gd", [("res://game/main.gd", "res://game/deep/main.gd")])));
        Assert.Equal("extends \"base.gd\"\n\nconst Kept = preload(\"res://paint/hero.png\")\n",
            Apply(beside, GodotScenes.MovedScriptEdits(beside, "res://game/main.gd", [("res://game/", "res://stage/"), ("res://art/", "res://paint/")])));
    }

    /// <summary>A path written from a file's folder that climbs out of the project names a file the move never reaches, so it
    /// is left as written, never made a res://../ path.</summary>
    private void A_path_climbing_out_of_the_project_is_left_as_its_file_moves()
    {
        const string script = "extends \"../base.gd\"\n\nconst Beside = preload(\"beside.tscn\")\n";
        List<(string, string)> moves = [("res://main.gd", "res://deep/main.gd")];
        Assert.Equal("extends \"../base.gd\"\n\nconst Beside = preload(\"res://beside.tscn\")\n", Apply(script, GodotScenes.MovedScriptEdits(script, "res://main.gd", moves)));
        const string shader = "shader_type spatial;\n#include \"../outside.gdshaderinc\"\n";
        Assert.Empty(GodotScenes.MovedShaderEdits(shader, "res://water.gdshader", [("res://water.gdshader", "res://fx/water.gdshader")]));
    }

    /// <summary>As files and folders move, every res:// path a Godot text file gives naming one follows it: an external
    /// resource's, a sub-resource's with its "::", a property's, project.godot's main scene and an autoload's after its '*',
    /// and a path inside a moved folder; a call's argument, a path none moved and a uid are left as they are.</summary>
    private void A_moved_files_and_folders_res_paths_follow_them_in_every_godot_file()
    {
        const string scene = """
            [gd_scene format=3]

            [ext_resource type="PackedScene" uid="uid://bplayer" path="res://player.tscn" id="1"]
            [ext_resource type="Texture2D" path="res://art/icon.svg" id="2"]
            [ext_resource type="Script" path="res://Main.cs" id="3"]

            [node name="Main" type="Node2D"]
            level = "res://art/level.tres::1"
            label = NodePath("res://player.tscn")

            """;
        const string project = """
            [application]

            run/main_scene="res://player.tscn"

            [autoload]

            Global="*res://art/global.gd"

            """;
        IReadOnlyList<(string, string)> moves = [("res://player.tscn", "res://actors/player.tscn"), ("res://art/", "res://assets/art/")];
        Assert.Equal("res://assets/art/level.tres::1", GodotScenes.Moved(moves, "res://art/level.tres::1"));
        Assert.Null(GodotScenes.Moved(moves, "res://artist.tscn"));
        Assert.Equal(["res://actors/player.tscn", "res://assets/art/icon.svg", "res://assets/art/level.tres::1"], Applied(scene, moves));
        Assert.Equal(["res://actors/player.tscn", "res://assets/art/global.gd"], Applied(project, moves));

        static IReadOnlyList<string> Applied(string text, IReadOnlyList<(string, string)> moves)
        {
            IReadOnlyList<TextEdit> edits = GodotScenes.MovedEdits(GodotFile.Parse(text), moves);
            Assert.All(edits, e => Assert.StartsWith("res://", text.Substring(e.Start, e.Length), StringComparison.Ordinal));
            return [.. edits.Select(e => e.Text)];
        }
    }

    /// <summary>A scene of a chain of nodes, instancing the next scene at its last.</summary>
    private static GodotFile Chained(int scene, int deep)
    {
        System.Text.StringBuilder text = new($"[gd_scene format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://{scene + 1}.tscn\" id=\"1\"]\n\n[node name=\"r\" type=\"Node\"]\n");
        string parent = ".";
        for (int node = 0; node < deep; node++)
        {
            text.Append($"\n[node name=\"a\" type=\"Node\" parent=\"{parent}\"]\n");
            parent = parent == "." ? "a" : parent + "/a";
        }
        text.Append($"\n[node name=\"next\" parent=\"{parent}\" instance=ExtResource(\"1\")]\n");
        return GodotFile.Parse(text.ToString());
    }

    private void A_tree_deeper_than_a_stack_holds_is_built_and_walked_and_ends_nothing()
    {
        const int Scenes = 20;
        const int Deep = 300;
        GodotScenes scenes = new("/nowhere", Enumerable.Range(0, Scenes).ToDictionary(scene => $"res://{scene}.tscn", scene => Chained(scene, Deep)));
        SmallStack.Run(() =>
        {
            GodotTreeNode root = scenes.Tree("res://0.tscn")!;
            GodotTreeNode last = root.Below().Last();
            Assert.Equal(root, last.Root);
            int nodes = (Scenes * (Deep + 1)) + 1;
            Assert.Equal(nodes, root.Below().Count());
            Assert.Equal(nodes - 1, last.Path.Split('/').Length);
        });
    }
}
