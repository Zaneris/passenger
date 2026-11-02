using Passenger.App.Godot;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Godot;

/// <summary>A running game's values as the properties pane tunes and keeps them: each property's editor read from its type
/// and Godot's hint (a range's ends, step and flags, an enum's names, a colour's alpha, a resource's classes), a value written
/// as a scene and as C# write it, and Keep's write planned where the value comes from: the scene that sets it, outermost
/// first; else the C# [Export] member's default; else a line added to the node in the scene it is written in.</summary>
public class GodotTuningTests
{
    private static GodotProperty Property(string name, GodotType type, long hint = 0, string hintString = "", object? value = null) =>
        new(name, type, hint, hintString, 4102, value, "Main");

    [Fact]
    public void A_range_hint_gives_a_slider_its_ends_step_and_flags_and_whole_numbers_step_by_one_where_none_is_given()
    {
        GodotHint speed = GodotHints.Of(Property("speed", GodotType.Float, GodotHints.Range, "0,10,0.5,or_greater,suffix:m/s", 2.0));
        Assert.Equal(GodotEditing.Range, speed.Editing);
        Assert.Equal(new GodotRange(0, 10, 0.5, OrGreater: true, Suffix: "m/s"), speed.Range);
        Assert.Equal(1, GodotHints.RangeOf("0,100", integers: true)!.Step);
        Assert.Equal(0.001, GodotHints.RangeOf("0,1", integers: false)!.Step);
        Assert.True(GodotHints.RangeOf("-180,180,0.1,radians_as_degrees", integers: false)!.Degrees);
        Assert.Null(GodotHints.RangeOf("low,high", integers: false));
        // Hidden, its slider gives way to the axis that scrubs, at the range's step.
        GodotHint hidden = GodotHints.Of(Property("depth", GodotType.Float, GodotHints.Range, "0,1,0.25,hide_slider"));
        Assert.Equal(GodotEditing.Axes, hidden.Editing);
        Assert.Equal(0.25, hidden.Range!.Step);
    }

    [Fact]
    public void A_slider_snaps_to_its_step_keeps_within_its_ends_and_moves_exponentially_where_its_hint_says()
    {
        GodotRange range = new(0, 10, 0.5);
        Assert.Equal(2.5, range.At(0.26));
        Assert.Equal(10, range.Snapped(12));
        Assert.Equal(12, (range with { OrGreater = true }).Snapped(12));
        Assert.Equal(0.25, range.Fraction(2.5));
        GodotRange exponential = new(1, 100, 0, Exponential: true);
        Assert.Equal(10, exponential.At(0.5), 6);
        Assert.Equal(0.5, exponential.Fraction(10), 6);
    }

    [Fact]
    public void An_enums_names_are_numbered_from_zero_or_after_the_number_one_gives_and_a_strings_are_the_names()
    {
        GodotHint mode = GodotHints.Of(Property("mode", GodotType.Int, GodotHints.Enum, "Walk,Run:5,Fly", 5L));
        Assert.Equal(GodotEditing.Choice, mode.Editing);
        Assert.Equal([new GodotChoice("Walk", 0L), new GodotChoice("Run", 5L), new GodotChoice("Fly", 6L)], mode.Choices);
        Assert.Equal([new GodotChoice("Red", "Red"), new GodotChoice("Blue", "Blue")], GodotHints.Of(Property("team", GodotType.String, GodotHints.Enum, "Red,Blue")).Choices);
    }

    [Fact]
    public void A_colour_a_bool_a_vector_and_a_resource_each_take_their_own_editor_and_any_other_value_its_text()
    {
        Assert.True(GodotHints.Of(Property("modulate", GodotType.Color)) is { Editing: GodotEditing.Colour, Alpha: true });
        Assert.True(GodotHints.Of(Property("tint", GodotType.Color, GodotHints.ColorNoAlpha)) is { Editing: GodotEditing.Colour, Alpha: false });
        Assert.Equal(GodotEditing.Check, GodotHints.Of(Property("visible", GodotType.Bool)).Editing);
        GodotHint position = GodotHints.Of(Property("position", GodotType.Vector3I));
        Assert.True(position is { Editing: GodotEditing.Axes, Integers: true }, $"{position}");
        Assert.Equal(["x", "y", "z"], position.Axes);
        Assert.Equal([string.Empty], GodotHints.Of(Property("lives", GodotType.Int)).Axes);
        GodotHint texture = GodotHints.Of(Property("texture", GodotType.Object, GodotHints.ResourceType, "Texture2D"));
        Assert.True(texture is { Editing: GodotEditing.Resource, PathString: false } && texture.Classes.SequenceEqual(["Texture2D"]), $"{texture}");
        Assert.True(GodotHints.Of(Property("icon", GodotType.String, GodotHints.File, "*.png,*.svg")) is { Editing: GodotEditing.Resource, PathString: true });
        Assert.Equal(GodotEditing.Text, GodotHints.Of(Property("name", GodotType.String)).Editing);
        Assert.Equal(GodotEditing.Text, GodotHints.Of(Property("transform", GodotType.Transform2D)).Editing);
    }

    [Fact]
    public void A_value_is_written_as_a_scene_writes_it_a_single_in_its_shortest_form_and_a_whole_real_with_its_fraction()
    {
        Assert.Equal("Vector2(100, 0.3)", GodotText.Scene(new GodotMath(GodotType.Vector2, [100, (double)0.3f])));
        Assert.Equal("Color(1, 0, 0, 1)", GodotText.Scene(new GodotMath(GodotType.Color, [1, 0, 0, 1])));
        Assert.Equal("Vector2i(3, -4)", GodotText.Scene(new GodotMath(GodotType.Vector2I, [3, -4])));
        Assert.Equal("2.0", GodotText.Scene(2.0));
        Assert.Equal("0.75", GodotText.Scene(0.75));
        Assert.Equal("7", GodotText.Scene(7L));
        Assert.Equal("\"say \\\"hi\\\"\"", GodotText.Scene("say \"hi\""));
        Assert.Equal("&\"jump\"", GodotText.Scene(new GodotName("jump")));
        Assert.Null(GodotText.Scene(new GodotObjectId(5)));
    }

    [Fact]
    public void A_value_is_written_as_CSharp_writes_a_default_for_the_members_type_an_enums_by_its_name()
    {
        Assert.Equal("new Color(1f, 0.5f, 0f, 1f)", GodotText.CSharp(new GodotMath(GodotType.Color, [1, 0.5, 0, 1]), "Color"));
        Assert.Equal("2.5f", GodotText.CSharp(2.5, "float"));
        Assert.Equal("3.0", GodotText.CSharp(3.0, "double"));
        Assert.Equal("Mode.Run", GodotText.CSharp(5L, "Mode", [new GodotChoice("Walk", 0L), new GodotChoice("Run", 5L)]));
        Assert.Equal("5", GodotText.CSharp(5L, "int", [new GodotChoice("Run", 5L)]));
        Assert.Equal("new Vector2I(1, 2)", GodotText.CSharp(new GodotMath(GodotType.Vector2I, [1, 2]), "Vector2I"));
        Assert.Equal("\"a\\nb\"", GodotText.CSharp("a\nb", "string"));
        Assert.Null(GodotText.CSharp(new GodotMath(GodotType.Transform2D, [1, 0, 0, 1, 0, 0]), "Transform2D"));
    }

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1")
        speed = 2.0

        [node name="Sprite" type="Sprite2D" parent="."]
        position = Vector2(4, 5)

        [node name="Enemy" parent="." instance=ExtResource("1")]

        """;

    private const string Code = """
        using Godot;

        public partial class Main : Node2D
        {
            [Export]
            public Color Tint { get; set; } = Colors.White;

            [Export]
            public float Speed { get; set; }

            [Export(PropertyHint.Range, "0,10")]
            private int lives = 3;

            public float Hidden { get; set; } = 1f;
        }

        """;

    private static Func<string, (string File, string Text)?> Files(params (string Res, string Text)[] files) =>
        res => files.FirstOrDefault(f => f.Res == res) is { Res: not null } found ? ("/game/" + res["res://".Length..], found.Text) : null;

    private static string Applied(GodotKept kept)
    {
        System.Text.StringBuilder text = new(kept.Was);
        foreach (TextEdit edit in kept.Edits.OrderByDescending(e => e.Start))
        {
            text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return text.ToString();
    }

    private static readonly GodotNodePlace Main = new([("Main", "res://main.tscn")], "res://Main.cs");

    [Fact]
    public void A_value_the_scene_sets_is_kept_on_its_line_there_shown_as_it_reads_now_and_as_it_will()
    {
        (GodotKept? kept, string? why) = GodotKeep.Plan(Main, "speed", 4.5, Property("speed", GodotType.Float), Files(("res://main.tscn", Scene), ("res://Main.cs", Code)), _ => CSharpParsing.Default);
        Assert.True(kept is not null, why);
        Assert.Equal(("/game/main.tscn", 6, "speed = 2.0", "speed = 4.5", false), (kept.File, kept.Line, kept.Before, kept.After, kept.InCode));
        Assert.Equal(Scene.Replace("speed = 2.0", "speed = 4.5", StringComparison.Ordinal), Applied(kept));

        GodotNodePlace sprite = new([("Main", "res://main.tscn"), ("Sprite", string.Empty)], null);
        (GodotKept? moved, _) = GodotKeep.Plan(sprite, "position", new GodotMath(GodotType.Vector2, [10, 20]), Property("position", GodotType.Vector2), Files(("res://main.tscn", Scene)), _ => CSharpParsing.Default);
        Assert.Equal("position = Vector2(10, 20)", moved!.After);
        Assert.Equal(9, moved.Line);
    }

    [Fact]
    public void A_value_only_the_code_gives_is_kept_as_its_Export_members_default_an_initializer_replaced_or_given()
    {
        (GodotKept? tint, string? why) = GodotKeep.Plan(Main, "Tint", new GodotMath(GodotType.Color, [1, 0, 0, 1]), Property("Tint", GodotType.Color),
            Files(("res://main.tscn", Scene), ("res://Main.cs", Code)), _ => CSharpParsing.Default);
        Assert.True(tint is not null, why);
        Assert.True(tint.InCode, $"{tint.File} keeps Tint, not Main.cs");
        Assert.Equal(("    public Color Tint { get; set; } = Colors.White;", "    public Color Tint { get; set; } = new Color(1f, 0f, 0f, 1f);"), (tint.Before, tint.After));
        Assert.Equal(5, tint.Line);
        (GodotKept? speed, _) = GodotKeep.Plan(Main, "Speed", 3.0, Property("Speed", GodotType.Float), Files(("res://main.tscn", Scene), ("res://Main.cs", Code)), _ => CSharpParsing.Default);
        Assert.Equal("    public float Speed { get; set; } = 3f;", speed!.After);
        (GodotKept? lives, _) = GodotKeep.Plan(Main, "lives", 7L, Property("lives", GodotType.Int, GodotHints.Range, "0,10"), Files(("res://main.tscn", Scene), ("res://Main.cs", Code)), _ => CSharpParsing.Default);
        Assert.Equal("    private int lives = 7;", lives!.After);
    }

    [Fact]
    public void A_value_neither_sets_is_a_line_added_to_the_node_in_its_scene_an_instance_in_the_scene_that_holds_it()
    {
        (GodotKept? hidden, _) = GodotKeep.Plan(Main, "Hidden", 2.0, Property("Hidden", GodotType.Float), Files(("res://main.tscn", Scene), ("res://Main.cs", Code)), _ => CSharpParsing.Default);
        Assert.True(hidden is not null && hidden is { InCode: false, Before: null }, $"{hidden}");
        Assert.Equal(("Hidden = 2.0", 7), (hidden.After, hidden.Line));
        Assert.Contains("speed = 2.0\nHidden = 2.0\n\n[node name=\"Sprite\"", Applied(hidden), StringComparison.Ordinal);

        GodotNodePlace enemy = new([("Main", "res://main.tscn"), ("Enemy", "res://enemy.tscn")], null);
        string inner = "[gd_scene format=3]\n\n[node name=\"Enemy\" type=\"Node2D\"]\nscale = Vector2(2, 2)\n";
        (GodotKept? inScene, _) = GodotKeep.Plan(enemy, "rotation", 1.5, Property("rotation", GodotType.Float), Files(("res://main.tscn", Scene), ("res://enemy.tscn", inner)), _ => CSharpParsing.Default);
        Assert.Equal(("/game/main.tscn", "rotation = 1.5"), (inScene!.File, inScene.After));
        // The scene of its own that sets it is where it comes from.
        (GodotKept? inOwn, _) = GodotKeep.Plan(enemy, "scale", new GodotMath(GodotType.Vector2, [3, 3]), Property("scale", GodotType.Vector2), Files(("res://main.tscn", Scene), ("res://enemy.tscn", inner)), _ => CSharpParsing.Default);
        Assert.Equal(("/game/enemy.tscn", "scale = Vector2(3, 3)"), (inOwn!.File, inOwn.After));

        GodotNodePlace made = new([("Main", "res://main.tscn"), ("Bullet", string.Empty)], null);
        (GodotKept? none, string? why) = GodotKeep.Plan(made, "speed", 1.0, Property("speed", GodotType.Float), Files(("res://main.tscn", Scene)), _ => CSharpParsing.Default);
        Assert.Null(none);
        Assert.Equal("Bullet is not written in main.tscn: the game made it as it ran, so no scene keeps its values", why);
    }

    [Fact]
    public void A_resource_is_kept_by_the_scenes_external_resource_of_its_path_or_one_added_with_the_load_steps_counted()
    {
        GodotProperty texture = Property("texture", GodotType.Object, GodotHints.ResourceType, "Texture2D");
        GodotNodePlace sprite = new([("Main", "res://main.tscn"), ("Sprite", string.Empty)], null);
        (GodotKept? kept, string? why) = GodotKeep.Plan(sprite, "texture", "res://icon.png", texture, Files(("res://main.tscn", Scene)), _ => CSharpParsing.Default);
        Assert.True(kept is not null, why);
        string written = Applied(kept);
        Assert.Contains("[gd_scene load_steps=3 format=3]", written, StringComparison.Ordinal);
        string id = written.Split("path=\"res://icon.png\" id=\"")[1].Split('"')[0];
        Assert.StartsWith("2_", id, StringComparison.Ordinal);
        Assert.Contains($"[ext_resource type=\"Texture2D\" path=\"res://icon.png\" id=\"{id}\"]", written, StringComparison.Ordinal);
        Assert.Equal($"texture = ExtResource(\"{id}\")", kept.After);
        Assert.Contains($"position = Vector2(4, 5)\ntexture = ExtResource(\"{id}\")\n", written, StringComparison.Ordinal);
    }
}
