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

    [Theory]
    // Indentation: tabs, one level per block.
    [InlineData("func f():\n    for i in range(10):\n        print(\"hello\")\n", "func f():\n\tfor i in range(10):\n\t\tprint(\"hello\")\n")]
    // Indentation: "Use 2 indent levels to distinguish continuation lines from regular code blocks."
    [InlineData("func f():\n\teffect.interpolate_property(sprite, \"transform/scale\",\n\tsprite.get_scale(), Vector2(2.0, 2.0), 0.3,\n\tTween.TRANS_QUAD, Tween.EASE_OUT)\n",
        "func f():\n\teffect.interpolate_property(sprite, \"transform/scale\",\n\t\t\tsprite.get_scale(), Vector2(2.0, 2.0), 0.3,\n\t\t\tTween.TRANS_QUAD, Tween.EASE_OUT)\n")]
    // Indentation: "Exceptions to this rule are arrays, dictionaries, and enums. Use a single indentation level."
    [InlineData("var party = [\n\t\t\"Godot\",\n\t\t\"Godette\",\n\t\t\"Steve\",\n]\n", "var party = [\n\t\"Godot\",\n\t\"Godette\",\n\t\"Steve\",\n]\n")]
    // Trailing comma: "Use trailing commas on the last line in arrays, dictionaries, and enums."
    [InlineData("var array = [\n\t1,\n\t2,\n\t3\n]\n", "var array = [\n\t1,\n\t2,\n\t3,\n]\n")]
    [InlineData("enum Tile {\n\tBRICK,\n\tFLOOR,\n\tSPIKE,\n\tTELEPORT\n}\n", "enum Tile {\n\tBRICK,\n\tFLOOR,\n\tSPIKE,\n\tTELEPORT,\n}\n")]
    // Trailing comma: "Trailing commas are unnecessary in single-line lists."
    [InlineData("enum Tile {BRICK, FLOOR, SPIKE, TELEPORT}\n", "enum Tile {BRICK, FLOOR, SPIKE, TELEPORT}\n")]
    // Blank lines: "Surround functions and class definitions with two blank lines."
    [InlineData("func heal(amount):\n\thealth += amount\n\thealth = min(health, max_health)\n\thealth_changed.emit(health)\n\nfunc take_damage(amount, effect=null):\n\thealth -= amount\n",
        "func heal(amount):\n\thealth += amount\n\thealth = min(health, max_health)\n\thealth_changed.emit(health)\n\n\nfunc take_damage(amount, effect=null):\n\thealth -= amount\n")]
    // Blank lines: "Use one blank line inside functions to separate logical sections."
    [InlineData("func f():\n\tvar a = 1\n\n\n\n\tvar b = 2\n", "func f():\n\tvar a = 1\n\n\tvar b = 2\n")]
    // Whitespace: "Always use one space around operators and after commas. Also, avoid extra spaces in dictionary references and function calls."
    [InlineData("func f():\n\tposition.x=5\n\tposition.y = mpos.y+10\n\tdict [\"key\"] = 5\n\tmyarray = [4,5,6]\n\tprint (\"foo\")\n",
        "func f():\n\tposition.x = 5\n\tposition.y = mpos.y + 10\n\tdict[\"key\"] = 5\n\tmyarray = [4, 5, 6]\n\tprint(\"foo\")\n")]
    // Line length's multiline statement: its parentheses' lines two levels in, the closing one at the statement's.
    [InlineData("var quadrant = (\n\"northeast\" if angle_degrees <= 90\nelse \"southeast\" if angle_degrees <= 180\nelse \"northwest\"\n)\n",
        "var quadrant = (\n\t\t\"northeast\" if angle_degrees <= 90\n\t\telse \"southeast\" if angle_degrees <= 180\n\t\telse \"northwest\"\n)\n")]
    // Line length's multiline statement: a bracket closing on its own line at the line it opened on.
    [InlineData("func f():\n\tfoo({\n\t\t\"a\": 1,\n\t\t\t})\n", "func f():\n\tfoo({\n\t\t\"a\": 1,\n\t})\n")]
    // Static typing: "var health: int = 0", inferred "var direction := Vector3(1, 2, 3)", "func heal(amount: int) -> void:".
    [InlineData("var health : int=0\nvar direction:=Vector3(1,2,3)\n\n\nfunc heal(amount:int)->void:\n\tpass\n",
        "var health: int = 0\nvar direction := Vector3(1, 2, 3)\n\n\nfunc heal(amount: int) -> void:\n\tpass\n")]
    public void A_script_is_laid_out_as_the_style_guides_own_examples(string written, string laid)
    {
        Assert.Equal(laid, Formatted(written));
    }

    [Fact]
    public void Comments_node_paths_unary_signs_and_strings_are_kept_as_written()
    {
        string script = "extends Node # the base\n\n# A header.\n\n\n## Its docs.\nfunc f(x):  # trailing\n\t#region Moves\n\tvar n = $Path/To/Node\n\tvar u = %Unique\n"
            + "\tvar s = \"\"\"a  +  b\n\tkept\"\"\"\n\treturn -x  + -1 % 3\n\t#endregion\n";
        Assert.Equal("extends Node # the base\n\n# A header.\n\n\n## Its docs.\nfunc f(x):  # trailing\n\t#region Moves\n\tvar n = $Path/To/Node\n\tvar u = %Unique\n"
            + "\tvar s = \"\"\"a  +  b\n\tkept\"\"\"\n\treturn -x + -1 % 3\n\t#endregion\n", Formatted(script));
    }

    [Fact]
    public void A_script_indented_with_spaces_by_its_editorconfig_keeps_them()
    {
        Assert.Equal("func f():\n  if true:\n    pass\n", Formatted("func f():\n\tif true:\n\t\tpass\n", new GDScriptFormatOptions(UseTabs: false, IndentSize: 2)));
    }

    [Fact]
    public void A_script_that_does_not_parse_is_left_as_it_is_saying_why()
    {
        GDScriptFormatted refused = GDScriptFormatter.Format("func f(:\n\tpass\n", new GDScriptFormatOptions());
        Assert.StartsWith("the script does not parse", refused.Refused, StringComparison.Ordinal);
        Assert.Empty(refused.Edits);
        Assert.Equal("its tokens", GDScriptFormatter.Differs("var a = b+c\n", "var a = b+d\n"));
        Assert.Equal("its comments", GDScriptFormatter.Differs("var a = 1 # one\n", "var a = 1 # two\n"));
        Assert.Null(GDScriptFormatter.Differs("var a = [\n\t1\n]\n", "var a = [\n\t1,\n]\n"));
    }

    [Fact]
    public void A_subscript_over_several_lines_takes_no_trailing_comma_and_a_lambda_keeps_its_lines()
    {
        string script = "func f():\n\tvar x = a[\n\t\t0\n\t]\n\tcall(func():\n\t\tpass\n\t)\n";
        Assert.Equal(script, Formatted(script));
    }

    [Theory]
    // Indentation's own continuation: a call's arguments run on two levels in, as many to a line as fit.
    [InlineData(100, "func f():\n\teffect.interpolate_property(sprite, \"transform/scale\", sprite.get_scale(), Vector2(2.0, 2.0), 0.3, Tween.TRANS_QUAD, Tween.EASE_OUT)\n",
        "func f():\n\teffect.interpolate_property(sprite, \"transform/scale\", sprite.get_scale(), Vector2(2.0, 2.0),\n\t\t\t0.3, Tween.TRANS_QUAD, Tween.EASE_OUT)\n")]
    // A declaration's parameters alike, the function's body one level in.
    [InlineData(60, "func take_damage(amount: int, effect: Effect = null, source: Node = null) -> void:\n\tpass\n",
        "func take_damage(amount: int, effect: Effect = null,\n\t\tsource: Node = null) -> void:\n\tpass\n")]
    // Indentation's arrays, dictionaries and enums: an element to a line, one level in, with its trailing comma.
    [InlineData(30, "var party = [\"Godot\", \"Godette\", \"Steve\"]\n", "var party = [\n\t\"Godot\",\n\t\"Godette\",\n\t\"Steve\",\n]\n")]
    [InlineData(40, "var character_dict = {\"Name\": \"Bob\", \"Age\": 27, \"Job\": \"Mechanic\"}\n",
        "var character_dict = {\n\t\"Name\": \"Bob\",\n\t\"Age\": 27,\n\t\"Job\": \"Mechanic\",\n}\n")]
    [InlineData(30, "enum Tile {BRICK, FLOOR, SPIKE, TELEPORT}\n", "enum Tile {\n\tBRICK,\n\tFLOOR,\n\tSPIKE,\n\tTELEPORT,\n}\n")]
    // Multiline statements: a parenthesized expression's contents two levels in, broken before else, and and or.
    [InlineData(60, "var quadrant = (\"northeast\" if angle_degrees <= 90 else \"southeast\" if angle_degrees <= 180 else \"southwest\" if angle_degrees <= 270 else \"northwest\")\n",
        "var quadrant = (\n\t\t\"northeast\" if angle_degrees <= 90\n\t\telse \"southeast\" if angle_degrees <= 180\n\t\telse \"southwest\" if angle_degrees <= 270\n\t\telse \"northwest\"\n)\n")]
    [InlineData(60, "func f():\n\tif (position.x > 200 and position.x < 400 and position.y > 300 and position.y < 400):\n\t\tpass\n",
        "func f():\n\tif (\n\t\t\tposition.x > 200 and position.x < 400\n\t\t\tand position.y > 300 and position.y < 400\n\t):\n\t\tpass\n")]
    // A call of one dictionary hugs it, its elements one level in.
    [InlineData(30, "func f():\n\tsave({\"name\": \"Bob\", \"age\": 27})\n", "func f():\n\tsave({\n\t\t\"name\": \"Bob\",\n\t\t\"age\": 27,\n\t})\n")]
    // A line inside brackets opened above it runs on at its own level.
    [InlineData(50, "var all = [\n\tItem.new(\n\t\t\t\"quit_confirmation\", \"Startup/Quit\", \"pressed\", false\n\t),\n]\n",
        "var all = [\n\tItem.new(\n\t\t\t\"quit_confirmation\", \"Startup/Quit\",\n\t\t\t\"pressed\", false\n\t),\n]\n")]
    public void A_long_line_wraps_inside_its_brackets_in_the_guides_multiline_forms(int length, string written, string laid)
    {
        Assert.Equal(laid, Formatted(written, new GDScriptFormatOptions() with { LineLength = length }));
    }

    [Fact]
    public void A_line_opening_a_bracket_that_closes_lines_on_wraps_at_its_commas_and_one_gdlint_ignores_is_left()
    {
        GDScriptFormatOptions options = new GDScriptFormatOptions() with { LineLength = 60 };
        Assert.Equal("func f():\n\tadd_edit(\"argument_type\", ValueType.FIXED_OPTIONS,\n\t\t\t{\"left\": \"Type:\", \"options\": [\n\t\t\t\t1,\n\t\t\t]})\n",
            Formatted("func f():\n\tadd_edit(\"argument_type\", ValueType.FIXED_OPTIONS, {\"left\": \"Type:\", \"options\": [\n\t\t1,\n\t]})\n", options));
        string ignored = "func f():\n\t# gdlint: ignore=max-line-length\n\tprint(\"a\", \"b\", \"c\", \"d\", \"e\", \"f\", \"g\", \"h\", \"i\", \"j\")\n"
            + "\tprint(\"a\", \"b\", \"c\", \"d\", \"e\", \"f\", \"g\", \"h\", \"i\", \"j\") # gdlint: ignore = max-line-length\n";
        Assert.Equal(ignored, Formatted(ignored, options));
    }

    [Fact]
    public void Order_parts_follows_the_guides_code_order_and_refuses_a_value_reading_a_member_that_would_come_after_it()
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

    [Fact]
    public void Order_parts_is_refused_where_a_value_that_calls_would_run_on_the_other_side_of_another()
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

    [Fact]
    public void A_long_line_with_nothing_to_wrap_safely_is_left_as_written()
    {
        // A string, a comment, a subscript, a lambda's brackets and a line no shorter for wrapping are left alone.
        string script = "func f():\n\tprint(\"a string far too long to fit in the forty columns asked\")\n\t# a comment far too long to fit in forty columns\n"
            + "\tvar v = values[first_index + second_index + third_index]\n\tbutton.pressed.connect(func(): print(\"pressed\", self, button))\n";
        Assert.Equal(script, Formatted(script, new GDScriptFormatOptions() with { LineLength = 40 }));
        string unwrapped = "var party = [\"Godot\", \"Godette\", \"Steve\"]\n";
        Assert.Equal(unwrapped, Formatted(unwrapped, new GDScriptFormatOptions() with { LineLength = 0 }));
    }

    [Fact]
    public void Settings_ask_for_one_blank_line_around_functions_and_no_trailing_comma()
    {
        GDScriptFormatOptions options = new GDScriptFormatOptions() with { BlankLines = 1, TrailingCommas = false, LineLength = 30 };
        Assert.Equal("func a():\n\tpass\n\nfunc b():\n\tvar party = [\n\t\t\"Godot\",\n\t\t\"Godette\",\n\t\t\"Steve\"\n\t]\n",
            Formatted("func a():\n\tpass\n\n\nfunc b():\n\tvar party = [\"Godot\", \"Godette\", \"Steve\"]\n", options));
    }

    [Fact]
    public void An_editorconfig_says_over_the_settings_by_its_keys()
    {
        Dictionary<string, string> config = new()
        {
            ["max_line_length"] = "80",
            ["gdscript_blank_lines_around_functions"] = "1",
            ["gdscript_trailing_comma"] = "false",
        };
        GDScriptFormatSettings read = new GDScriptFormatSettings().Over(key => config.GetValueOrDefault(key));
        Assert.Equal(new GDScriptFormatSettings { LineLength = 80, BlankLinesAroundFunctions = 1, TrailingCommas = false }, read);
        Assert.False(new GDScriptFormatSettings().Over(key => key == "max_line_length" ? "off" : null).WrapLongLines);
        // A value that does not read as its option's is passed over.
        Assert.Equal(new GDScriptFormatSettings(), new GDScriptFormatSettings().Over(key => key == "max_line_length" ? "wide" : key == "gdscript_blank_lines_around_functions" ? "7" : "maybe"));
        Assert.Equal(new GDScriptFormatOptions(false, 2, 2, 0, 1, false),
            new GDScriptFormatSettings { WrapLongLines = false, BlankLinesAroundFunctions = 1, TrailingCommas = false }.Options(false, 2, 2));
    }

    /// <summary>A script nested past what Passenger reads is left as it is, its result having no tree to be read back against.</summary>
    [Fact]
    public void A_script_read_only_in_part_is_neither_laid_out_nor_ordered()
    {
        string text = "func b( ):\n\tpass\nvar deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + "\nvar a=1\n";
        foreach (GDScriptFormatted refused in (GDScriptFormatted[])[GDScriptFormatter.Format(text, new GDScriptFormatOptions()), GDScriptFormatter.OrderParts(text)])
        {
            Assert.Equal((text, "the script is nested too deeply for Passenger to read whole"), (refused.Text, refused.Refused));
        }
    }
}
