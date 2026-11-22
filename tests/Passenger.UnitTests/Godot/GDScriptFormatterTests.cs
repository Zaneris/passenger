using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>A GDScript laid out by Passenger's formatter as Godot's GDScript style guide lays out its own examples, each rule
/// by the guide's section; laid out twice, the same; its comments and tokens kept; and a script it would make say something
/// else, or that does not parse, refused.</summary>
public class GDScriptFormatterTests
{
    private static string Formatted(string text, GDScriptFormatOptions? options = null)
    {
        GDScriptFormatted once = GDScriptFormatter.Format(text, options ?? new GDScriptFormatOptions());
        Assert.Null(once.Refused);
        GDScriptFormatted twice = GDScriptFormatter.Format(once.Text, options ?? new GDScriptFormatOptions());
        Assert.Equal(once.Text, twice.Text);
        Assert.Empty(twice.Edits);
        return once.Text;
    }

    /// <summary>A script is laid out and its long lines wrapped as Godot's style guide does, or left as it is saying
    /// why.</summary>
    [Fact]
    public void A_script_is_laid_out_as_godots_style_guide_lays_it_out() => Cases.All(
        (nameof(A_script_is_laid_out_as_the_style_guides_own_examples), A_script_is_laid_out_as_the_style_guides_own_examples),
        (nameof(Comments_node_paths_unary_signs_and_strings_are_kept_as_written), Comments_node_paths_unary_signs_and_strings_are_kept_as_written),
        (nameof(A_script_that_does_not_parse_is_left_as_it_is_saying_why), A_script_that_does_not_parse_is_left_as_it_is_saying_why),
        (nameof(A_long_line_wraps_inside_its_brackets_in_the_guides_multiline_forms),
            A_long_line_wraps_inside_its_brackets_in_the_guides_multiline_forms),
        (nameof(A_script_read_only_in_part_is_neither_laid_out_nor_ordered), A_script_read_only_in_part_is_neither_laid_out_nor_ordered));

    /// <summary>Order parts follows the style guide's code order, refused where a value would run otherwise.</summary>
    [Fact]
    public void Order_parts_follows_the_guides_order_and_refuses_what_would_run_otherwise() => Cases.All(
        (nameof(Order_parts_follows_the_guides_code_order_and_refuses_a_value_reading_a_member_that_would_come_after_it),
            Order_parts_follows_the_guides_code_order_and_refuses_a_value_reading_a_member_that_would_come_after_it),
        (nameof(Order_parts_is_refused_where_a_value_that_calls_would_run_on_the_other_side_of_another),
            Order_parts_is_refused_where_a_value_that_calls_would_run_on_the_other_side_of_another));

    private void A_script_is_laid_out_as_the_style_guides_own_examples() => Cases.Each<(string Written, string Laid)>(
    [
        // Indentation: tabs, one level per block.
        ("func f():\n    for i in range(10):\n        print(\"hello\")\n", "func f():\n\tfor i in range(10):\n\t\tprint(\"hello\")\n"),
        // Trailing comma: "Use trailing commas on the last line in arrays, dictionaries, and enums."
        ("var array = [\n\t1,\n\t2,\n\t3\n]\n", "var array = [\n\t1,\n\t2,\n\t3,\n]\n"),
        // Blank lines: "Surround functions and class definitions with two blank lines."
        ("func heal(amount):\n\thealth += amount\n\thealth = min(health, max_health)\n\thealth_changed.emit(health)\n\nfunc take_damage(amount, effect=null):\n\thealth -= amount\n", "func heal(amount):\n\thealth += amount\n\thealth = min(health, max_health)\n\thealth_changed.emit(health)\n\n\nfunc take_damage(amount, effect=null):\n\thealth -= amount\n"),
        // Whitespace: "Always use one space around operators and after commas. Also, avoid extra spaces in dictionary references and function calls."
        ("func f():\n\tposition.x=5\n\tposition.y = mpos.y+10\n\tdict [\"key\"] = 5\n\tmyarray = [4,5,6]\n\tprint (\"foo\")\n", "func f():\n\tposition.x = 5\n\tposition.y = mpos.y + 10\n\tdict[\"key\"] = 5\n\tmyarray = [4, 5, 6]\n\tprint(\"foo\")\n"),
        // Static typing: "var health: int = 0", inferred "var direction := Vector3(1, 2, 3)", "func heal(amount: int) -> void:".
        ("var health : int=0\nvar direction:=Vector3(1,2,3)\n\n\nfunc heal(amount:int)->void:\n\tpass\n", "var health: int = 0\nvar direction := Vector3(1, 2, 3)\n\n\nfunc heal(amount: int) -> void:\n\tpass\n"),
    ], row => A_script_is_laid_out_as_the_style_guides_own_examples(row.Written, row.Laid));

    private void A_script_is_laid_out_as_the_style_guides_own_examples(string written, string laid)
    {
        Assert.Equal(laid, Formatted(written));
    }

    private void Comments_node_paths_unary_signs_and_strings_are_kept_as_written()
    {
        string script = "extends Node # the base\n\n# A header.\n\n\n## Its docs.\nfunc f(x):  # trailing\n\t#region Moves\n\tvar n = $Path/To/Node\n\tvar u = %Unique\n"
            + "\tvar s = \"\"\"a  +  b\n\tkept\"\"\"\n\treturn -x  + -1 % 3\n\t#endregion\n";
        Assert.Equal("extends Node # the base\n\n# A header.\n\n\n## Its docs.\nfunc f(x):  # trailing\n\t#region Moves\n\tvar n = $Path/To/Node\n\tvar u = %Unique\n"
            + "\tvar s = \"\"\"a  +  b\n\tkept\"\"\"\n\treturn -x + -1 % 3\n\t#endregion\n", Formatted(script));
    }

    private void A_script_that_does_not_parse_is_left_as_it_is_saying_why()
    {
        GDScriptFormatted refused = GDScriptFormatter.Format("func f(:\n\tpass\n", new GDScriptFormatOptions());
        Assert.StartsWith("the script does not parse", refused.Refused, StringComparison.Ordinal);
        Assert.Empty(refused.Edits);
        Assert.Equal("its tokens", GDScriptFormatter.Differs("var a = b+c\n", "var a = b+d\n"));
        Assert.Equal("its comments", GDScriptFormatter.Differs("var a = 1 # one\n", "var a = 1 # two\n"));
        Assert.Null(GDScriptFormatter.Differs("var a = [\n\t1\n]\n", "var a = [\n\t1,\n]\n"));
    }

    private void A_long_line_wraps_inside_its_brackets_in_the_guides_multiline_forms() => Cases.Each<(int Length, string Written, string Laid)>(
    [
        // Indentation's own continuation: a call's arguments run on two levels in, as many to a line as fit.
        (100, "func f():\n\teffect.interpolate_property(sprite, \"transform/scale\", sprite.get_scale(), Vector2(2.0, 2.0), 0.3, Tween.TRANS_QUAD, Tween.EASE_OUT)\n", "func f():\n\teffect.interpolate_property(sprite, \"transform/scale\", sprite.get_scale(), Vector2(2.0, 2.0),\n\t\t\t0.3, Tween.TRANS_QUAD, Tween.EASE_OUT)\n"),
        // Indentation's arrays, dictionaries and enums: an element to a line, one level in, with its trailing comma.
        (30, "var party = [\"Godot\", \"Godette\", \"Steve\"]\n", "var party = [\n\t\"Godot\",\n\t\"Godette\",\n\t\"Steve\",\n]\n"),
        (30, "enum Tile {BRICK, FLOOR, SPIKE, TELEPORT}\n", "enum Tile {\n\tBRICK,\n\tFLOOR,\n\tSPIKE,\n\tTELEPORT,\n}\n"),
        (60, "func f():\n\tif (position.x > 200 and position.x < 400 and position.y > 300 and position.y < 400):\n\t\tpass\n", "func f():\n\tif (\n\t\t\tposition.x > 200 and position.x < 400\n\t\t\tand position.y > 300 and position.y < 400\n\t):\n\t\tpass\n"),
        // A line's length is its columns as drawn: a wide character takes two.
        (25, "var p = [\"日本語\", \"日本語\"]\n", "var p = [\n\t\"日本語\",\n\t\"日本語\",\n]\n"),
    ], row => A_long_line_wraps_inside_its_brackets_in_the_guides_multiline_forms(row.Length, row.Written, row.Laid));

    private void A_long_line_wraps_inside_its_brackets_in_the_guides_multiline_forms(int length, string written, string laid)
    {
        Assert.Equal(laid, Formatted(written, new GDScriptFormatOptions() with { LineLength = length }));
    }

    private void Order_parts_follows_the_guides_code_order_and_refuses_a_value_reading_a_member_that_would_come_after_it()
    {
        string script = "class_name Mover\nextends Node\n\n\nfunc _private():\n\tpass\n\n\nfunc go():\n\tpass\n\n## The speed.\nvar speed := 1\n"
            + "@onready var label := $Label\nstatic var count := 0\nconst LIMIT := 3\nenum State { IDLE }\nsignal moved\n\n\nfunc _ready():\n\tpass\n\n\nstatic func make():\n\tpass\n\n\n"
            + "class Inner:\n\tvar x := 1\n\n\nfunc _init():\n\tpass\n";
        GDScriptFormatted ordered = GDScriptFormatter.OrderParts(script);
        Assert.Null(ordered.Refused);
        Assert.Equal("class_name Mover\nextends Node\n\nsignal moved\n\nenum State { IDLE }\n\nconst LIMIT := 3\n\nstatic var count := 0\n\n## The speed.\nvar speed := 1\n\n"
            + "@onready var label := $Label\n\n\nstatic func make():\n\tpass\n\n\nfunc _init():\n\tpass\n\n\nfunc _ready():\n\tpass\n\n\nfunc go():\n\tpass\n\n\n"
            + "func _private():\n\tpass\n\n\nclass Inner:\n\tvar x := 1\n", ordered.Text);
        Assert.Empty(GDScriptFormatter.OrderParts(ordered.Text).Edits);
        Assert.Contains("'size''s value reads 'speed', which would come after it",
            GDScriptFormatter.OrderParts("extends Node\n\nvar speed := 2.0\n@export var size := speed * 2\n").Refused);
        Assert.Contains("export groups", GDScriptFormatter.OrderParts("extends Node\n\nvar a := 1\n@export_group(\"G\")\n@export var b := 2\n").Refused);
    }

    private void Order_parts_is_refused_where_a_value_that_calls_would_run_on_the_other_side_of_another()
    {
        const string Record = "\n\nfunc record(what):\n\treturn what\n";
        string? Refused(string variables) => GDScriptFormatter.OrderParts($"extends Node\n\n{variables}{Record}").Refused;
        Assert.Contains("'second''s value would run before '_first''s", Refused("var _first = record(\"first\")\nvar second = record(\"second\")\n"));
        Assert.Contains("'exported''s value would run before 'plain''s", Refused("var plain = record(\"a\")\n@export var exported = record(\"b\")\n"));
        // A call may read what a value it passes sets, however plain that value.
        Assert.Contains("'second''s value would run before '_first''s", Refused("var _first = 1\nvar second = record(\"second\")\n"));
        Assert.Contains("'second''s value would run before '_first''s", Refused("@onready var _first = record($A)\n@onready var second = $B\n"));
        Assert.Null(Refused("var _first = 1\nvar second = [2, Vector2(1, 2).x, abs(-3)]\n"));
        Assert.Null(Refused("var _first\nvar second = record(\"second\")\n"));
        // Those that keep their order, and those that run at another time, may be called.
        Assert.Null(Refused("var first = record(\"a\")\nvar second = record(\"b\")\nvar _third = record(\"c\")\n"));
        Assert.Null(Refused("var plain = record(\"a\")\nstatic var counted = record(\"b\")\n@onready var ready_one = record(\"c\")\n"));
        string engineMade = "extends Node\n\nvar _timer = Timer.new()\nvar second = 2\n";
        Assert.NotNull(GDScriptFormatter.OrderParts(engineMade).Refused);
        GodotEngineApi engine = GodotEngineApi.Read("""{ "classes": [{ "name": "Object" }, { "name": "Node", "inherits": "Object" }, { "name": "Timer", "inherits": "Node" }] }"""u8);
        Assert.Null(GDScriptFormatter.OrderParts(engineMade, engine).Refused);
    }

    /// <summary>A script nested past what Passenger reads is left as it is, its result having no tree to be read back against.</summary>
    private void A_script_read_only_in_part_is_neither_laid_out_nor_ordered()
    {
        string text = "func b( ):\n\tpass\nvar deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + "\nvar a=1\n";
        foreach (GDScriptFormatted refused in (GDScriptFormatted[])[GDScriptFormatter.Format(text, new GDScriptFormatOptions()), GDScriptFormatter.OrderParts(text)])
        {
            Assert.Equal((text, "the script is nested too deeply for Passenger to read whole"), (refused.Text, refused.Refused));
        }
    }
}
