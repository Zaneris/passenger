using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a Godot project's files define for the strings its code gives Godot's calls, each read with every place
/// its files write it: project.godot's input actions, autoloads and listed translations (a CSV's keys, a PO file's
/// msgids), the groups its scenes give nodes, an animation player's animations from its libraries, a shader's uniforms with
/// the shader a node's material gives, and the renderer its shaders compile under. The project.godot, scene and library
/// are as Godot 4.7's editor wrote them.</summary>
public class GodotDefinitionsTests
{
    private const string Project = """
        ; Engine configuration file.
        config_version=5

        [application]

        config/name="Probe"

        [autoload]

        Globals="*res://Globals.cs"
        Hud="*res://hud.tscn"

        [input]

        jump={
        "deadzone": 0.2,
        "events": [Object(InputEventKey,"resource_local_to_scene":false,"device":16,"keycode":0,"unicode":0,"script":null)
        ]
        }
        "move left"={
        "deadzone": 0.5,
        "events": []
        }

        [internationalization]

        locale/translations=PackedStringArray("res://text.en.translation", "res://text.fr.translation", "res://de.po")

        """;

    private const string Main = """
        [gd_scene format=3]

        [ext_resource type="AnimationLibrary" path="res://moves.tres" id="1_0xm2m"]
        [ext_resource type="Shader" path="res://tint.gdshader" id="2_h2yge"]

        [sub_resource type="Animation" id="Animation_1bvp3"]

        [sub_resource type="Animation" id="Animation_lquwl"]
        length = 0.5

        [sub_resource type="AnimationLibrary" id="AnimationLibrary_7mycd"]
        _data = {
        &"idle": SubResource("Animation_1bvp3"),
        &"run": SubResource("Animation_lquwl")
        }

        [sub_resource type="ShaderMaterial" id="ShaderMaterial_272bh"]
        shader = ExtResource("2_h2yge")
        shader_parameter/tint = Color(1, 0, 0, 1)
        shader_parameter/amount = null

        [node name="Main" type="Node2D" unique_id=217495973]

        [node name="Enemy" type="Area2D" parent="." unique_id=1258246905 groups=["enemies", "hittable"]]

        [node name="Anim" type="AnimationPlayer" parent="." unique_id=584326472]
        libraries/ = SubResource("AnimationLibrary_7mycd")
        libraries/moves = ExtResource("1_0xm2m")
        autoplay = &"run"

        [node name="Sprite" type="Sprite2D" parent="." unique_id=169548992]
        material = SubResource("ShaderMaterial_272bh")

        """;

    private const string Moves = """
        [gd_resource type="AnimationLibrary" format=3]

        [sub_resource type="Animation" id="Animation_xptd0"]

        [resource]
        _data = {
        &"jump": SubResource("Animation_xptd0")
        }

        """;

    private const string Shader = """
        shader_type canvas_item;
        #include "res://common.gdshaderinc"
        // uniform vec4 commented;
        uniform vec4 tint : source_color = vec4(1.0);
        uniform float amount = 0.5;
        uniform float fade;
        global uniform vec2 wind;
        instance uniform float glow;
        void fragment() { COLOR = tint * amount + vec4(shade); COLOR.a = COLOR.tint; }

        """;

    private const string Csv = "keys,en,fr\nHELLO,Hello,Bonjour\n\"BYE, NOW\",\"Bye, \"\"now\"\"\",Salut\n,ignored,ignoré\n";

    private const string Po = """
        msgid ""
        msgstr ""
        "Language: de\n"

        msgid "HELLO"
        msgstr "Hallo"

        msgid ""
        "LONG_"
        "KEY"
        msgstr "Lang"

        """;

    private static GodotScenes Scenes() => new("/game", new Dictionary<string, GodotFile>
    {
        ["res://main.tscn"] = GodotFile.Parse(Main),
        ["res://moves.tres"] = GodotFile.Parse(Moves),
    }, texts: new Dictionary<string, string>
    {
        ["res://project.godot"] = Project,
        ["res://tint.gdshader"] = Shader,
        ["res://common.gdshaderinc"] = "uniform float shade;\n",
        ["res://text.csv"] = Csv,
        ["res://de.po"] = Po,
    });

    private static string At(string text, GodotPlace place) => text[place.Start..place.End];

    [Fact]
    public void Project_godots_input_actions_and_autoloads_are_its_keys_each_placed_on_its_line()
    {
        GodotDefinitions defined = Scenes().Defined;
        Assert.Equal(["jump", "move left"], defined.Actions.Select(a => a.Name));
        GodotPlace jump = Assert.Single(defined.Actions[0].Places);
        Assert.Equal(("jump", true, "project.godot:15"), (At(Project, jump), jump.Defines, jump.Shown));
        Assert.Equal("move left", At(Project, defined.Actions[1].Places[0]));
        Assert.Equal(["Globals", "Hud"], defined.Autoloads.Select(a => a.Name));
        Assert.Equal("res://Globals.cs", defined.AutoloadFile("Globals"));
        Assert.Null(defined.AutoloadFile("Gone"));
        Assert.Contains("ui_accept", GodotDefinitions.BuiltInActions);
    }

    /// <summary>The renderer a project names is the one its shaders compile under, Forward+ where it names none Godot knows.</summary>
    [Theory]
    [InlineData("", "forward_plus")]
    [InlineData("[rendering]\n\nrenderer/rendering_method=\"gl_compatibility\"\n", "gl_compatibility")]
    [InlineData("[rendering]\n\nrenderer/rendering_method=\"mobile\"\nrenderer/rendering_method.mobile=\"gl_compatibility\"\n", "mobile")]
    [InlineData("[rendering]\n\nrenderer/rendering_method=\"vulkan\"\n", "forward_plus")]
    public void A_projects_rendering_method_is_the_renderer_project_godot_names(string settings, string method)
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string>
        {
            ["res://project.godot"] = $"config_version=5\n\n[application]\n\nconfig/name=\"Probe\"\n\n{settings}",
        });
        Assert.Equal(method, scenes.Defined.RenderingMethod);
    }

    [Fact]
    public void A_scenes_groups_are_each_nodes_and_a_players_animations_come_from_its_libraries_its_own_and_a_resources()
    {
        GodotScenes scenes = Scenes();
        GodotDefinitions defined = scenes.Defined;
        Assert.Equal(["enemies", "hittable"], defined.Groups.Select(g => g.Name));
        GodotPlace enemies = Assert.Single(defined.Groups[0].Places);
        Assert.Equal(("enemies", "main.tscn:24, Enemy"), (At(Main, enemies), enemies.Shown));

        GodotTreeNode player = scenes.Tree("res://main.tscn")!.Child("Anim")!;
        Assert.True(GodotDefinitions.IsPlayer(player), "a node whose sections give it libraries holds animations");
        (IReadOnlyList<GodotDefined> animations, bool whole) = defined.AnimationsOf(player);
        Assert.True(whole, "every library the player holds is read");
        Assert.Equal(["idle", "run", "moves/jump"], animations.Select(a => a.Name));
        GodotDefined run = animations[1];
        Assert.Equal(["run", "run"], run.Places.Select(p => p.Res == "res://main.tscn" ? At(Main, p) : At(Moves, p)));
        Assert.Equal([true, false], run.Places.Select(p => p.Defines));
        GodotPlace jump = Assert.Single(animations[2].Places);
        Assert.Equal(("res://moves.tres", "jump"), (jump.Res, At(Moves, jump)));
        Assert.Equal(["idle", "run", "moves/jump"], defined.Animations.Select(a => a.Name));
    }

    [Fact]
    public void An_older_files_libraries_dictionary_and_animations_resource_name_are_read_too()
    {
        const string old = """
            [gd_scene load_steps=3 format=3]

            [sub_resource type="Animation" id="Animation_1"]
            resource_name = "walk"

            [sub_resource type="AnimationLibrary" id="AnimationLibrary_1"]
            _data = {
            "walk": SubResource("Animation_1")
            }

            [node name="Anim" type="AnimationPlayer"]
            libraries = {
            "": SubResource("AnimationLibrary_1")
            }

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://old.tscn"] = GodotFile.Parse(old) });
        GodotDefined walk = Assert.Single(scenes.Defined.AnimationsOf(scenes.Tree("res://old.tscn")!).Names);
        Assert.Equal("walk", walk.Name);
        Assert.Equal(["walk", "walk"], walk.Places.Select(p => At(old, p)));
        Assert.Equal([true, false], walk.Places.Select(p => p.Defines));
    }

    [Fact]
    public void A_materials_shader_gives_its_uniforms_with_their_uses_and_the_materials_settings_of_them()
    {
        GodotScenes scenes = Scenes();
        GodotDefinitions defined = scenes.Defined;
        GodotTreeNode sprite = scenes.Tree("res://main.tscn")!.Child("Sprite")!;
        Assert.Equal("res://tint.gdshader", defined.ShaderOf(sprite, "material"));
        Assert.Null(defined.ShaderOf(sprite, "material_override"));
        (IReadOnlyList<GodotDefined> uniforms, bool whole) = defined.Uniforms("res://tint.gdshader");
        Assert.True(whole, "the shader and its include are read");
        Assert.Equal(["shade", "tint", "amount", "fade"], uniforms.Select(u => u.Name));
        GodotDefined tint = uniforms[1];
        Assert.Equal(["tint", "tint", "tint"], tint.Places.Select(p => p.Res == "res://main.tscn" ? At(Main, p) : At(Shader, p)));
        Assert.Equal([true, false, false], tint.Places.Select(p => p.Defines));
        Assert.Equal("tint.gdshader:4", tint.Places[0].Shown);
        Assert.Equal("res://main.tscn", tint.Places[2].Res);
        Assert.Equal("res://common.gdshaderinc", uniforms[0].Places[0].Res);
        Assert.Contains("res://tint.gdshader", defined.Shaders);
    }

    [Fact]
    public void The_translations_project_godot_lists_give_a_csvs_keys_and_a_po_files_msgids()
    {
        GodotDefinitions defined = Scenes().Defined;
        Assert.Equal(["res://text.csv", "res://de.po"], defined.TranslationFiles);
        Assert.True(defined.TranslationsWhole, "every listed translation is read");
        Assert.Equal(["HELLO", "BYE, NOW", "LONG_KEY"], defined.Translations.Select(t => t.Name));
        GodotDefined hello = defined.Translations[0];
        Assert.Equal(["HELLO", "HELLO"], hello.Places.Select(p => p.Res == "res://de.po" ? At(Po, p) : At(Csv, p)));
        Assert.Equal(["text.csv:2", "de.po:5"], hello.Places.Select(p => p.Shown));
        Assert.Equal("BYE, NOW", At(Csv, defined.Translations[1].Places[0]));
        Assert.False(defined.Translations[2].Places[0].Plain, "a msgid over several lines is read but never written over");
        Assert.Equal("res://text.csv", GodotDefinitions.CsvOf("res://text.fr.translation"));
    }

    [Fact]
    public void A_translation_project_godot_lists_that_is_not_read_leaves_the_translations_partial()
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string> { ["res://project.godot"] = Project });
        Assert.False(scenes.Defined.TranslationsWhole, "a listed translation no file gives is not read");
        Assert.Empty(scenes.Defined.Translations);
    }

    [Fact]
    public void A_csv_parted_by_semicolons_gives_its_first_cells()
    {
        Assert.Equal(["A", "B"], GodotDefinitions.CsvKeys("keys;en\r\nA;a\r\nB;b\r\n").Select(k => k.Key));
    }

    private const string Named = """
        config_version=5

        [autoload]

        Globals="*res://globals.gd"

        [input]

        jump={
        "deadzone": 0.2,
        "events": []
        }

        [layer_names]

        2d_physics/layer_1="player"
        2d_physics/layer_3="enemies"
        3d_render/layer_2="sky"

        [shader_globals]

        wind={
        "type": "vec2",
        "value": Vector2(0, 0)
        }

        """;

    private const string Tree = """
        [gd_scene format=3]

        [sub_resource type="SpriteFrames" id="SpriteFrames_1"]
        animations = [{
        "frames": [],
        "loop": true,
        "name": &"default",
        "speed": 5.0
        }, {
        "frames": [],
        "loop": false,
        "name": &"run",
        "speed": 8.0
        }]

        [sub_resource type="AnimationNodeAnimation" id="AnimationNodeAnimation_idle"]
        animation = &"idle"

        [sub_resource type="AnimationNodeBlendSpace1D" id="AnimationNodeBlendSpace1D_run"]

        [sub_resource type="AnimationNodeBlend2" id="AnimationNodeBlend2_mix"]

        [sub_resource type="AnimationNodeBlendTree" id="AnimationNodeBlendTree_aim"]
        nodes/Mix/node = SubResource("AnimationNodeBlend2_mix")
        nodes/Mix/position = Vector2(100, 100)
        nodes/output/position = Vector2(300, 100)

        [sub_resource type="AnimationNodeStateMachineTransition" id="AnimationNodeStateMachineTransition_go"]
        advance_mode = 2
        advance_condition = &"is_running"

        [sub_resource type="AnimationNodeStateMachine" id="AnimationNodeStateMachine_main"]
        states/Idle/node = SubResource("AnimationNodeAnimation_idle")
        states/Idle/position = Vector2(300, 100)
        states/Run/node = SubResource("AnimationNodeBlendSpace1D_run")
        states/Aim/node = SubResource("AnimationNodeBlendTree_aim")
        transitions = ["Idle", "Run", SubResource("AnimationNodeStateMachineTransition_go")]

        [sub_resource type="AnimationNodeStateMachine" id="AnimationNodeStateMachine_odd"]
        states/Strange/node = SubResource("AnimationNodeExtension_1")

        [node name="Main" type="Node2D"]

        [node name="Sprite" type="AnimatedSprite2D" parent="."]
        sprite_frames = SubResource("SpriteFrames_1")
        animation = &"run"
        autoplay = "run"

        [node name="Tree" type="AnimationTree" parent="."]
        tree_root = SubResource("AnimationNodeStateMachine_main")

        [node name="Odd" type="AnimationTree" parent="."]
        tree_root = SubResource("AnimationNodeStateMachine_odd")

        [node name="Follow" type="Node" parent="."]
        target = NodePath("/root/Globals/Score")

        """;

    private const string Script = """
        extends Node2D

        # Input.is_action_pressed("jump") in a comment names nothing.
        func _process(_delta: float) -> void:
        	if Input.is_action_just_pressed("jump") or Input.get_axis("jump", "left") > 0:
        		Globals.score += 1
        		get_node("/root/Globals").reset()
        		self.Globals = null
        	RenderingServer.global_shader_parameter_set("wind", Vector2.ONE)

        """;

    private const string WindShader = """
        shader_type canvas_item;
        global uniform vec2 wind;
        void fragment() { COLOR.rg += wind; }

        """;

    private static GodotScenes NamedScenes() => new("/game", new Dictionary<string, GodotFile> { ["res://main.tscn"] = GodotFile.Parse(Tree) },
        texts: new Dictionary<string, string> { ["res://project.godot"] = Named, ["res://player.gd"] = Script, ["res://wind.gdshader"] = WindShader });

    [Fact]
    public void Project_godots_layer_names_name_each_layer_by_its_number_among_godots_32_or_20()
    {
        GodotDefinitions defined = NamedScenes().Defined;
        IReadOnlyList<GodotDefined> physics = defined.Layers("2d_physics");
        Assert.Equal(32, physics.Count);
        Assert.Equal(("1", "player"), (physics[0].Name, physics[0].Detail));
        Assert.Empty(physics[1].Places);
        GodotPlace enemies = Assert.Single(physics[2].Places);
        Assert.Equal(("2d_physics/layer_3", true, "project.godot:17"), (At(Named, enemies), enemies.Defines, enemies.Shown));
        Assert.Equal(20, defined.Layers("3d_render").Count);
        Assert.Equal("sky", defined.Layers("3d_render")[1].Detail);
        Assert.Equal(("2D physics", "avoidance"), (GodotDefinitions.LayersShown("2d_physics"), GodotDefinitions.LayersShown("avoidance")));
    }

    [Fact]
    public void A_global_shader_uniform_is_project_godots_key_with_each_shaders_global_uniform_of_it_and_its_uses()
    {
        GodotDefined wind = Assert.Single(NamedScenes().Defined.ShaderGlobals);
        Assert.Equal("wind", wind.Name);
        Assert.Equal([("res://project.godot", true), ("res://wind.gdshader", false), ("res://wind.gdshader", false), ("res://player.gd", false)],
            wind.Places.Select(p => (p.Res, p.Defines)));
        Assert.All(wind.Places, p => Assert.Equal("wind", (p.Res == "res://project.godot" ? Named : p.Res == "res://player.gd" ? Script : WindShader)[p.Start..p.End]));
    }

    [Fact]
    public void An_animation_trees_parameters_are_made_by_its_state_machines_states_and_conditions_and_its_blend_trees_nodes()
    {
        GodotScenes scenes = NamedScenes();
        GodotTreeNode main = scenes.Tree("res://main.tscn")!;
        (IReadOnlyList<GodotDefined> parameters, bool whole) = scenes.Defined.TreeParametersOf(main.Child("Tree")!);
        Assert.True(whole, "every node of the tree is of a kind whose parameters are known");
        List<string> names = [.. parameters.Select(p => p.Name)];
        Assert.Contains("parameters/playback", names);
        Assert.Contains("parameters/Run/blend_position", names);
        Assert.Contains("parameters/Aim/Mix/blend_amount", names);
        Assert.DoesNotContain("parameters/Aim/output/current_length", names);
        GodotDefined condition = parameters.Single(p => p.Name == "parameters/conditions/is_running");
        Assert.Equal(("is_running", "condition parameter of Tree"), (At(Tree, condition.Places.Single()), condition.Detail));
        Assert.Equal("Run", At(Tree, parameters.Single(p => p.Name == "parameters/Run/blend_position").Places.Single()));
        Assert.False(scenes.Defined.TreeParametersOf(main.Child("Odd")!).Whole, "a tree holding a node of an unknown kind may make more");
    }

    [Fact]
    public void An_animated_sprites_animations_are_its_sprite_frames_with_its_own_settings_of_them()
    {
        GodotScenes scenes = NamedScenes();
        GodotTreeNode sprite = scenes.Tree("res://main.tscn")!.Child("Sprite")!;
        Assert.True(GodotDefinitions.IsPlayer(sprite), "a node whose sections give it SpriteFrames holds animations");
        (IReadOnlyList<GodotDefined> animations, bool whole) = scenes.Defined.AnimationsOf(sprite);
        Assert.True(whole);
        Assert.Equal(["default", "run"], animations.Select(a => a.Name));
        Assert.Equal([true, false, false], animations[1].Places.Select(p => p.Defines));
        Assert.All(animations[1].Places, p => Assert.Equal("run", At(Tree, p)));
    }

    /// <summary>Only Input, InputMap and an input event take an action's name: a string given to a function of the name on
    /// the script's own class, or as a default where it is declared, names nothing; one joined to another names neither; and
    /// one on what the script does not say is an event may name it, which a rename leaves.</summary>
    [Fact]
    public void A_string_names_an_input_action_only_given_whole_to_what_takes_one()
    {
        const string script = """
            extends Node

            var kept: InputEventAction
            var custom = Custom.new()

            func is_action_pressed(label = "jump"):
            	return label == "jump"

            func _input(event):
            	if event.is_action_pressed("jump") or Input.is_action_pressed(&"jump") or InputMap.has_action('jump'):
            		kept.is_action("jump")

            func check(typed: InputEvent, cast, unknown):
            	var made = InputEventAction.new()
            	var key := cast as InputEventKey
            	typed.is_action("jump")
            	made.is_action("jump")
            	key.is_action("jump")
            	Input.is_action_pressed("jump" if unknown else "jump")
            	is_action_pressed("jump")
            	self.is_action_pressed("jump")
            	Input.is_action_pressed("jump" + "_fast")
            	custom.is_action_pressed("jump")
            	unknown.is_action("jump")
            	unknown.chain.is_action("jump")

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string> { ["res://project.godot"] = Named, ["res://player.gd"] = script });
        GodotDefined jump = scenes.Defined.Actions.Single();
        string[] lines = script.Split('\n');
        IReadOnlyList<string> Said(bool unsure) => [.. jump.Places.Where(p => p.Res == "res://player.gd" && p.Unsure == unsure).Select(p => lines[script[..p.Start].Count(c => c == '\n')].Trim())];
        string input = lines.Single(l => l.Contains("InputMap", StringComparison.Ordinal)).Trim();
        string either = lines.Single(l => l.Contains(" if unknown else ", StringComparison.Ordinal)).Trim();
        Assert.Equal([.. Enumerable.Repeat(input, 3), "kept.is_action(\"jump\")", "typed.is_action(\"jump\")", "made.is_action(\"jump\")", "key.is_action(\"jump\")", either, either], Said(false));
        Assert.Equal(["custom.is_action_pressed(\"jump\")", "unknown.is_action(\"jump\")", "unknown.chain.is_action(\"jump\")"], Said(true));
    }

    /// <summary>A name is written for each string it goes in: a quote like the string's own escaped, one a raw string can't say
    /// told as none, and project.godot's key as it is.</summary>
    [Fact]
    public void A_new_name_is_written_for_the_quotes_of_each_string_it_goes_in()
    {
        const string script = "extends Node\n\n\nfunc _process(_delta):\n\tInput.is_action_pressed('jump')\n\tInput.is_action_pressed(\"jump\")\n\tInput.is_action_pressed(r'jump')\n";
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string> { ["res://project.godot"] = Named, ["res://player.gd"] = script });
        Assert.Equal(["player's_jump", "player\\'s_jump", "player's_jump", null], scenes.Defined.Actions.Single().Places.Select(p => p.Written("player's_jump")));
    }

    [Fact]
    public void A_gdscripts_strings_and_words_and_a_scenes_node_paths_are_places_of_the_names_they_give_godot()
    {
        GodotDefinitions defined = NamedScenes().Defined;
        GodotDefined jump = defined.Actions.Single();
        Assert.Equal(["res://project.godot", "res://player.gd", "res://player.gd"], jump.Places.Select(p => p.Res));
        Assert.Equal(["project.godot:9", "player.gd:5", "player.gd:5"], jump.Places.Select(p => p.Shown));
        Assert.All(jump.Places.Skip(1), p => Assert.Equal("jump", Script[p.Start..p.End]));
        GodotDefined globals = defined.Autoloads.Single();
        Assert.Equal(["res://project.godot", "res://player.gd", "res://player.gd", "res://main.tscn"], globals.Places.Select(p => p.Res));
        Assert.Equal(["Globals", "Globals"], globals.Places.Where(p => p.Res == "res://player.gd").Select(p => Script[p.Start..p.End]));
        Assert.Equal("Globals", At(Tree, globals.Places[^1]));
    }
}
