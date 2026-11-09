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

    [Fact]
    public void A_scenes_tags_and_properties_are_read_by_place_with_their_strings_through_escapes_and_calls()
    {
        GodotFile file = GodotFile.Parse(Main);
        Assert.Equal("gd_scene", file.Header?.Tag);
        Assert.Equal(["res://Main.cs", "res://player.tscn"], file.Externals.Select(e => e.Path));
        Assert.Equal(["Main", "Hud", "Score", "Start", "Player", "Sprite"], file.Nodes.Select(n => n.Name));
        GodotNode main = file.Nodes[0];
        Assert.Equal("1_main", main.Script);
        Assert.Equal("say \"hi\"\ntwice", main.Section.Property("Greeting")!.Value.Text);
        Assert.False(main.Section.Property("Greeting")!.Value.Plain, "A string with an escape in it is read as written");
        GodotValue target = main.Section.Property("Target")!.Value;
        Assert.Equal(("NodePath", "Hud/Score", true), (target.Call, target.Text, target.Plain));
        Assert.Equal("Hud/Score", Main[target.TextStart..target.TextEnd]);
        Assert.Equal("Hud/Score", file.Nodes[2].PathInScene);
        Assert.True(file.Nodes[2].Unique, "Score is unique in its owner");
        Assert.Equal("2_player", file.Nodes[4].Instance);
        Assert.Equal("Vector2(100, 100)", file.Written(file.Nodes[4].Section.Property("position")!.Value));
        Assert.Equal(Main.Split('\n').ToList().FindIndex(l => l.StartsWith("[node name=\"Score\"", StringComparison.Ordinal)), file.Nodes[2].Section.Line);

        GodotConnection pressed = file.Connections[0];
        Assert.Equal(("pressed", "Hud/Start", ".", "OnStartPressed", 3, 0), (pressed.Signal, pressed.From, pressed.To, pressed.Method, pressed.Binds, pressed.Unbinds));
        Assert.Equal((0, 1), (file.Connections[1].Binds, file.Connections[1].Unbinds));
        Assert.Equal("Player", Assert.Single(file.Editables).Text("path"));
    }

    [Fact]
    public void An_old_files_numbered_resources_and_a_comment_are_read_as_godot_reads_them()
    {
        GodotFile file = GodotFile.Parse("""
            [gd_scene load_steps=2 format=2]
            ; Godot 3 wrote numbers
            [ext_resource path="res://Old.cs" type="Script" id=1]
            [node name="Old" type="Node"]
            script = ExtResource( 1 )
            """);
        Assert.Equal("1", file.Externals[0].Id);
        Assert.Equal("1", file.Nodes[0].Script);
        Assert.Equal("res://Old.cs", file.External(file.Nodes[0].Script)?.Path);
    }

    [Fact]
    public void A_scenes_tree_builds_in_the_scenes_it_instances_their_overrides_and_owners()
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

    [Fact]
    public void A_node_path_leads_through_names_parents_and_unique_names_and_says_where_it_goes_missing()
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

    /// <summary>A scene that inherits an imported model, whose nodes no text says, finds the nodes it adds itself, and a
    /// path into the model's own is not known, in it and in a scene instancing it; a path into a text scene still goes missing.</summary>
    [Fact]
    public void A_path_into_an_imported_models_nodes_is_not_the_scenes_to_say()
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>
        {
            ["res://chicken.tscn"] = GodotFile.Parse("""
                [gd_scene format=3]

                [ext_resource type="PackedScene" path="res://chicken.glb" id="1_model"]

                [node name="Chicken" instance=ExtResource("1_model")]

                [node name="Mesh" parent="Rig/Skeleton3D" index="0"]

                [node name="Halo" type="Node3D" parent="."]
                """),
            ["res://world.tscn"] = GodotFile.Parse("""
                [gd_scene format=3]

                [ext_resource type="PackedScene" path="res://chicken.tscn" id="1_chicken"]

                [node name="World" type="Node3D"]

                [node name="Chicken" parent="." instance=ExtResource("1_chicken")]
                """),
        });
        GodotTreeNode chicken = scenes.Tree("res://chicken.tscn")!;
        Assert.True(chicken.NodesUnread);
        Assert.Equal("res://chicken.glb", chicken.Instance);
        Assert.Equal("Halo", GodotScenes.Resolve(chicken, "Halo").Found?.Name);
        Assert.True(GodotScenes.Resolve(chicken, "Rig/Skeleton3D").Unknown, "The model's nodes are Godot's import's to say, not the scene's");
        GodotTreeNode world = scenes.Tree("res://world.tscn")!;
        Assert.Equal("res://chicken.tscn", world.Child("Chicken")?.Instance);
        Assert.True(GodotScenes.Resolve(world, "Chicken/Rig/Skeleton3D/Mesh").Unknown, "An instance of the scene keeps the model's nodes unknown");
        Assert.Equal("Halo", GodotScenes.Resolve(world, "Chicken/Halo").Found?.Name);
        GodotResolved missing = GodotScenes.Resolve(world, "Chicken/Halo/Gone");
        Assert.Equal((false, (13, 17)), (missing.Unknown, missing.Missing));
    }

    /// <summary>Godot takes an extends path from the script's folder, a '..' past res:// kept, so a script at the top
    /// extending "../base.gd" extends a file outside the project, never res://base.gd.</summary>
    [Fact]
    public void A_scripts_class_extends_by_a_relative_path_as_Godot_resolves_it()
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string>
        {
            ["res://base.gd"] = "extends Node\nfunc hit():\n\tpass\n",
            ["res://sub/main.gd"] = "extends \"../base.gd\"\n",
            ["res://top.gd"] = "extends \"../base.gd\"\n",
        });

        GodotScriptChain main = scenes.ChainOf("res://sub/main.gd");
        Assert.True(main is { Whole: true, Engine: "Node" } && main.Scripts.Select(s => s.Script).SequenceEqual(["res://sub/main.gd", "res://base.gd"]),
            $"A script's class did not take in the script its relative extends names: {string.Join(", ", main.Scripts.Select(s => s.Script))}");
        GodotScriptChain top = scenes.ChainOf("res://top.gd");
        Assert.True(top is { Whole: false } && top.Find(GodotScriptKind.Function, "hit") is null,
            "A script extending a file outside the project was given res://base.gd's members, which Godot never loads for it");
    }

    [Fact]
    public void A_script_runs_on_its_own_scenes_node_and_on_every_instance_of_that_scene()
    {
        GodotScenes scenes = Scenes();
        Assert.Equal([("res://main.tscn", "Player"), ("res://player.tscn", ".")], scenes.RunsOf("res://Player.cs").Select(r => (r.Scene, r.Node.Path)));
        Assert.Equal(["res://player.tscn"], scenes.AttachedOf("res://Player.cs").Select(r => r.Scene));
        Assert.Equal([("res://main.tscn", ".")], scenes.RunsOf("res://Main.cs").Select(r => (r.Scene, r.Node.Path)));
        Assert.Equal("res://levels/one.tscn", GodotResPaths.Of("/game", Path.Combine("/game", "levels", "one.tscn")));
        Assert.Null(GodotResPaths.FileOf("/game", "res://../outside.cs"));
    }

    [Fact]
    public void A_nodes_rename_writes_its_name_and_every_path_through_it_in_each_scene_that_holds_it()
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
    [Fact]
    public void A_nodes_rename_follows_a_unique_name_and_every_node_path_a_property_holds_at_any_depth()
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
    [Fact]
    public void A_nodes_rename_reaches_the_paths_a_gdscript_leads_from_its_node()
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

    /// <summary>A node's name may hold a backslash, which Godot's text writes doubled: a new name is written so wherever it goes,
    /// and a name written so is read through its escape, its name and every path through it renamed.</summary>
    [Fact]
    public void A_nodes_name_holding_a_backslash_is_written_as_godot_writes_it_in_a_scene()
    {
        const string plain = """
            [gd_scene format=3]

            [node name="Main" type="Node2D"]
            Target = NodePath("Hud/Score:text")

            [node name="Hud" type="CanvasLayer" parent="."]

            [node name="Score" type="Label" parent="Hud"]

            [node name="Label" type="Label" parent="Hud/Score"]

            [connection signal="hit" from="Hud/Score" to="." method="OnHit"]

            """;
        string escaped = plain.Replace("Score", "A\\\\B", StringComparison.Ordinal);
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(plain) });
        Assert.Equal(escaped, Apply(plain, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!, "A\\B")["res://main.tscn"]));
        GodotScenes written = new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(escaped) });
        GodotTreeNode node = written.Tree("res://main.tscn")!.Child("Hud")!.Child("A\\B")!;
        Assert.Equal(plain, Apply(escaped, written.Renames(node, "Score")["res://main.tscn"]));
    }

    [Fact]
    public void A_name_godot_refuses_or_one_beside_it_already_is_no_rename()
    {
        GodotScenes scenes = Scenes();
        GodotTreeNode score = scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!;
        Assert.Equal("Hud has a node named Start already, in main.tscn", scenes.RenameProblem(score, "Start"));
        Assert.Equal("A node's name never holds '/', so Score keeps its name", scenes.RenameProblem(score, "A/B"));
        Assert.Equal("'A\nB' is no node's name", scenes.RenameProblem(score, "A\nB"));
        Assert.Null(scenes.RenameProblem(score, "Points"));
    }

    /// <summary>A script's property is set on the resources it runs in as well as on nodes: a resource file's own section and
    /// a sub-resource, of a resource or a scene, each its key's characters told past a quote; another script's are left.</summary>
    [Fact]
    public void A_scripted_resources_keys_are_found_in_a_resource_files_own_section_and_every_sub_resource()
    {
        const string stats = "[gd_resource type=\"Resource\" script_class=\"Stats\" format=3]\n\n[ext_resource type=\"Script\" path=\"res://stats.gd\" id=\"1_s\"]\n\n"
            + "[sub_resource type=\"Resource\" id=\"inner\"]\nscript = ExtResource(\"1_s\")\n\"speed\" = 7\n\n[resource]\nscript = ExtResource(\"1_s\")\nspeed = 42\n";
        const string main = "[gd_scene format=3]\n\n[ext_resource type=\"Script\" path=\"res://stats.gd\" id=\"1_s\"]\n[ext_resource type=\"Script\" path=\"res://other.gd\" id=\"2_o\"]\n\n"
            + "[sub_resource type=\"Resource\" id=\"kept\"]\nscript = ExtResource(\"1_s\")\nspeed = 3\n\n[sub_resource type=\"Resource\" id=\"other\"]\nscript = ExtResource(\"2_o\")\nspeed = 9\n\n"
            + "[node name=\"Main\" type=\"Node\"]\n";
        Dictionary<string, GodotFile> files = new(StringComparer.Ordinal)
        {
            ["res://stats.tres"] = GodotFile.Parse(stats),
            ["res://main.tscn"] = GodotFile.Parse(main),
        };
        List<(string Res, GodotEntry Key)> keys = [.. new GodotScenes("/game", files).ResourceKeys(s => s == "res://stats.gd", "speed")];
        Assert.Equal(["res://main.tscn", "res://stats.tres", "res://stats.tres"], keys.Select(k => k.Res));
        Assert.Equal(["speed = 3", "speed\" = 7", "speed = 42"], keys.Select(k => (k.Res == "res://main.tscn" ? main : stats)[k.Key.NameStart..].Split('\n')[0]));
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
    [Fact]
    public void A_nodes_rename_follows_the_paths_of_its_players_animation_tracks_their_properties_kept()
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

    [Fact]
    public void A_players_track_paths_lead_from_its_root_node()
    {
        string scene = Animated.Replace("NodePath(\"Hud/Score:text\")", "NodePath(\"Score:text\")", StringComparison.Ordinal)
            .Replace("libraries/ = SubResource(\"AnimationLibrary_1\")", "root_node = NodePath(\"../Hud\")\nlibraries = {\n&\"\": SubResource(\"AnimationLibrary_1\")\n}", StringComparison.Ordinal);
        GodotScenes scenes = AnimatedScenes(scene);

        string renamed = Apply(scene, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!, "Points")["res://main.tscn"]);

        Assert.Contains("tracks/0/path = NodePath(\"Points:text\")", renamed, StringComparison.Ordinal);
    }

    /// <summary>An AnimationTree told to play a player's animations has that player's root: its own libraries are read by none.</summary>
    [Fact]
    public void A_tree_playing_another_players_animations_leads_no_track_of_its_own()
    {
        string scene = Animated.Replace("[node name=\"Moves\" type=\"AnimationPlayer\" parent=\".\"]\nlibraries/ = SubResource(\"AnimationLibrary_1\")\n",
            "[node name=\"Moves\" type=\"AnimationPlayer\" parent=\".\"]\n\n[node name=\"Tree\" type=\"AnimationTree\" parent=\".\"]\nanim_player = NodePath(\"../Moves\")\nlibraries/ = SubResource(\"AnimationLibrary_1\")\n",
            StringComparison.Ordinal);
        Assert.Contains("anim_player", scene, StringComparison.Ordinal);
        GodotScenes scenes = AnimatedScenes(scene);

        string renamed = Apply(scene, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!, "Overlay")["res://main.tscn"]);

        Assert.Contains("tracks/0/path = NodePath(\"Hud/Score:text\")", renamed, StringComparison.Ordinal);
    }

    /// <summary>A track's player may stand in a scene over the one declaring the node, through its editable children: the
    /// rename reaches the track from that scene's tree.</summary>
    [Fact]
    public void A_players_track_through_an_instanced_scene_follows_a_rename_of_a_node_in_that_scene()
    {
        const string Over = """
            [gd_scene format=3]

            [ext_resource type="PackedScene" path="res://player.tscn" id="1_player"]

            [sub_resource type="Animation" id="Animation_hit"]
            tracks/0/type = "value"
            tracks/0/imported = false
            tracks/0/path = NodePath("Player/Sprite:modulate")

            [sub_resource type="AnimationLibrary" id="AnimationLibrary_1"]
            _data = {
            &"hit": SubResource("Animation_hit")
            }

            [node name="Level" type="Node2D"]

            [node name="Player" parent="." instance=ExtResource("1_player")]

            [node name="Moves" type="AnimationPlayer" parent="."]
            libraries/ = SubResource("AnimationLibrary_1")

            [editable path="Player"]

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>
        {
            ["res://level.tscn"] = GodotFile.Parse(Over),
            ["res://player.tscn"] = GodotFile.Parse(Player),
        });

        IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> edits = scenes.Renames(scenes.Tree("res://player.tscn")!.Child("Sprite")!, "Body");

        Assert.Contains("tracks/0/path = NodePath(\"Player/Body:modulate\")", Apply(Over, edits["res://level.tscn"]), StringComparison.Ordinal);
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
    [Fact]
    public void A_nodes_rename_follows_the_paths_of_the_built_in_resources_a_node_holds_led_from_that_node_at_any_depth()
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

    /// <summary>A ViewportTexture's path is from the scene's root whoever holds it, as its Viewport keeps it: read from the
    /// mesh inside Hud, it would lead nowhere and stay.</summary>
    [Fact]
    public void A_viewport_textures_path_leads_from_the_scenes_root_whatever_holds_it_a_material_among_them()
    {
        GodotScenes scenes = BuiltInScenes(BuiltIn);
        GodotTreeNode hud = scenes.Tree("res://main.tscn")!.Child("Hud")!;

        string renamed = Apply(BuiltIn, scenes.Renames(hud.Child("View")!, "Screen")["res://main.tscn"]);

        Assert.DoesNotContain("viewport_path = NodePath(\"Hud/View\")", renamed, StringComparison.Ordinal);
        Assert.Equal(2, renamed.Split("viewport_path = NodePath(\"Hud/Screen\")").Length - 1);
    }

    /// <summary>A resource's path may name what is not in the scene, as Godot's editor allows: one with the renamed node's
    /// name that leads to no node is another's.</summary>
    [Fact]
    public void A_path_of_a_built_in_resource_that_reaches_no_node_stays_as_it_is()
    {
        string scene = BuiltIn.Replace("NodePath(\"../Hud/Score\")", "NodePath(\"../Hud/Score/Gone\")", StringComparison.Ordinal);
        GodotScenes scenes = BuiltInScenes(scene);

        string renamed = Apply(scene, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!, "Points")["res://main.tscn"]);

        Assert.Contains("target = NodePath(\"../Hud/Score/Gone\")", renamed, StringComparison.Ordinal);
    }

    /// <summary>A track is led from its player, never the node that holds its animation, and a resource no node holds is
    /// dropped by Godot as it saves.</summary>
    [Fact]
    public void An_animation_a_node_holds_and_a_resource_no_node_holds_are_no_built_in_paths_of_that_node()
    {
        const string Held = """
            [gd_scene format=3]

            [sub_resource type="Animation" id="Animation_1"]
            tracks/0/path = NodePath("../Hud:visible")

            [sub_resource type="Resource" id="Resource_1"]
            target = NodePath("../Hud")

            [node name="Main" type="Node2D"]

            [node name="Hud" type="Node2D" parent="."]

            [node name="Holder" type="Node2D" parent="."]
            metadata/moves = SubResource("Animation_1")

            """;
        GodotScenes scenes = BuiltInScenes(Held);

        string renamed = Apply(Held, scenes.Renames(scenes.Tree("res://main.tscn")!.Child("Hud")!, "Overlay")["res://main.tscn"]);

        Assert.Contains("tracks/0/path = NodePath(\"../Hud:visible\")", renamed, StringComparison.Ordinal);
        Assert.Contains("target = NodePath(\"../Hud\")", renamed, StringComparison.Ordinal);
    }

    [Fact]
    public void Resources_holding_each_other_are_walked_once_each()
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
    [Fact]
    public void A_long_chain_of_built_in_resources_is_followed_to_its_end()
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

    /// <summary>Binary tracks are no text to change: a rename that can't reach them has to say so, as the animation stops
    /// reaching the node with no other word why.</summary>
    [Fact]
    public void A_rename_says_the_binary_library_of_a_player_whose_tracks_may_name_the_node()
    {
        GodotScenes scenes = BuiltInScenes(BinaryLibrary);

        Assert.Equal(["res://moves.res"], scenes.RenameLeaves(scenes.Tree("res://main.tscn")!.Child("Hud")!.Child("Score")!));
    }

    [Fact]
    public void A_rename_says_a_binary_animation_of_a_text_library()
    {
        const string TextLibrary = """
            [gd_scene format=3]

            [ext_resource type="Animation" path="res://blink.anim" id="1_blink"]

            [sub_resource type="AnimationLibrary" id="AnimationLibrary_1"]
            _data = {
            &"blink": ExtResource("1_blink")
            }

            [node name="Main" type="Node2D"]

            [node name="Hud" type="CanvasLayer" parent="."]

            [node name="Moves" type="AnimationPlayer" parent="."]
            libraries/ = SubResource("AnimationLibrary_1")

            """;
        GodotScenes scenes = BuiltInScenes(TextLibrary);

        Assert.Equal(["res://blink.anim"], scenes.RenameLeaves(scenes.Tree("res://main.tscn")!.Child("Hud")!));
    }

    [Fact]
    public void A_rename_says_nothing_of_text_animations_of_a_root_no_track_names_or_of_a_scene_with_no_player()
    {
        GodotScenes animated = AnimatedScenes(Animated);
        Assert.Empty(animated.RenameLeaves(animated.Tree("res://main.tscn")!.Child("Hud")!));

        GodotScenes binary = BuiltInScenes(BinaryLibrary);
        Assert.Empty(binary.RenameLeaves(binary.Tree("res://main.tscn")!));

        GodotScenes none = BuiltInScenes(BuiltIn);
        Assert.Empty(none.RenameLeaves(none.Tree("res://main.tscn")!.Child("Hud")!));
    }

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
    [Theory]
    [InlineData("#include \"res://common/noise.gdshaderinc\"\n", "res://shader.gdshader", "res://common/noise.gdshaderinc", "res://lib/noise.gdshaderinc", "#include \"res://lib/noise.gdshaderinc\"\n")]
    [InlineData("#include \"res://common/noise.gdshaderinc\"\n", "res://shader.gdshader", "res://common/", "res://lib/", "#include \"res://lib/noise.gdshaderinc\"\n")]
    [InlineData("#include \"noise.gdshaderinc\"\n", "res://common/shader.gdshader", "res://common/noise.gdshaderinc", "res://lib/noise.gdshaderinc", "#include \"res://lib/noise.gdshaderinc\"\n")]
    [InlineData("#include \"noise.gdshaderinc\"\n", "res://common/shader.gdshader", "res://common/shader.gdshader", "res://fx/shader.gdshader", "#include \"res://common/noise.gdshaderinc\"\n")]
    [InlineData("#include \"../noise.gdshaderinc\"\n", "res://fx/shader.gdshader", "res://noise.gdshaderinc", "res://lib/noise.gdshaderinc", "#include \"res://lib/noise.gdshaderinc\"\n")]
    [InlineData("#include \"noise.gdshaderinc\"\n", "res://common/shader.gdshader", "res://common/", "res://lib/", "#include \"noise.gdshaderinc\"\n")]
    [InlineData("#include \"uid://abc\"\n", "res://shader.gdshader", "res://common/", "res://lib/", "#include \"uid://abc\"\n")]
    [InlineData("// #include \"res://common/noise.gdshaderinc\"\n", "res://shader.gdshader", "res://common/", "res://lib/", "// #include \"res://common/noise.gdshaderinc\"\n")]
    public void A_shaders_includes_follow_files_and_folders_moved(string code, string shader, string from, string to, string moved)
    {
        Assert.Equal(moved, Apply(code, GodotScenes.MovedShaderEdits(code, shader, [(from, to)])));
    }

    /// <summary>A Shader a scene writes itself holds its includes in a string, its quotes escaped.</summary>
    [Fact]
    public void A_scenes_own_shader_follows_a_moved_include_where_the_file_writes_it()
    {
        const string Scene = """
            [gd_scene format=3]

            [sub_resource type="Shader" id="Shader_1"]
            code = "shader_type canvas_item;
            #include \"res://common/noise.gdshaderinc\"
            #include \"beside.gdshaderinc\"
            "

            [node name="Main" type="Node2D"]

            """;

        Assert.Equal(Scene.Replace("res://common/noise", "res://lib/noise", StringComparison.Ordinal).Replace("\\\"beside", "\\\"res://lib/beside", StringComparison.Ordinal),
            Apply(Scene, GodotScenes.MovedEdits(GodotFile.Parse(Scene), [("res://common/noise.gdshaderinc", "res://lib/noise.gdshaderinc"), ("res://beside.gdshaderinc", "res://lib/beside.gdshaderinc")], "res://level.tscn")));
    }

    [Fact]
    public void A_shader_resource_follows_a_moved_include_and_a_string_of_another_resource_says_nothing_of_one()
    {
        const string Shader = "[gd_resource type=\"Shader\" format=3]\n\n[resource]\ncode = \"#include \\\"res://a.gdshaderinc\\\"\n\"\n";
        const string Other = "[gd_resource type=\"Resource\" format=3]\n\n[resource]\ncode = \"#include \\\"res://a.gdshaderinc\\\"\n\"\n";
        (string, string)[] moves = [("res://a.gdshaderinc", "res://b.gdshaderinc")];

        Assert.Contains("#include \\\"res://b.gdshaderinc\\\"", Apply(Shader, GodotScenes.MovedEdits(GodotFile.Parse(Shader), moves, "res://s.tres")), StringComparison.Ordinal);
        Assert.Empty(GodotScenes.MovedEdits(GodotFile.Parse(Other), moves, "res://o.tres"));
    }

    /// <summary>A GDScript's strings follow a move as Godot reads them: a res:// path in any string, each in its own quotes, and
    /// a path written from the script's folder in a preload or extends where the move leaves it naming another; a comment, a
    /// string that says no moved path, and a load's relative string, which Godot reads from the top, are left.</summary>
    [Fact]
    public void A_gdscripts_paths_follow_files_and_folders_moved()
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

    /// <summary>As files and folders move, every res:// path a Godot text file gives naming one follows it: an external
    /// resource's, a sub-resource's with its "::", a property's, project.godot's main scene and an autoload's after its '*',
    /// and a path inside a moved folder; a call's argument, a path none moved and a uid are left as they are.</summary>
    [Fact]
    public void A_moved_files_and_folders_res_paths_follow_them_in_every_godot_file()
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

    [Fact]
    public void A_tree_deeper_than_a_stack_holds_is_built_and_walked_and_ends_nothing()
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
