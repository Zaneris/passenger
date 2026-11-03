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
}
