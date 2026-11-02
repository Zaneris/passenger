using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a Godot project's files define for the strings its code gives Godot's calls, each read with every place
/// its files write it: project.godot's input actions, autoloads and listed translations (a CSV's keys, a PO file's
/// msgids), the groups its scenes give nodes, an animation player's animations from its libraries, and a shader's uniforms
/// with the shader a node's material gives. The project.godot, scene and library are as Godot 4.7's editor wrote them.</summary>
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
        Assert.Equal(["shade", "tint", "amount"], uniforms.Select(u => u.Name));
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
}
