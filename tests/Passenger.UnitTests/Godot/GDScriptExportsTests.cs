using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>A GDScript's exports as Godot's inspector takes them: an enum of the script's by its names and values with its
/// member's value as the default, <c>@export_enum</c>'s and <c>@export_range</c>'s hints, a type written or inferred from
/// a literal or a built-in type's call, and no default where only running the code gives one.</summary>
public class GDScriptExportsTests
{
    [Fact]
    public void Each_export_is_typed_hinted_and_defaulted_as_Godots_inspector_takes_it()
    {
        IReadOnlyList<GodotScriptExport> exports = GDScriptExports.Of("""
            extends Node2D

            enum Pace { SLOW, STEADY = 5, FAST }

            @export var speed: Pace = Pace.STEADY
            @export_enum("Red", "Green") var team: int
            @export_range(0, 10, 0.5) var turn := 1.5
            @export var offset := Vector2(1, -2)
            @export var tint: Color
            @export var started = OS.get_ticks_msec()
            var hidden := 3

            """);
        Assert.Equal(
        [
            new GodotScriptExport("speed", 2, 2, "SLOW:0,STEADY:5,FAST:6", "5"),
            new GodotScriptExport("team", 2, 2, "Red,Green", null),
            new GodotScriptExport("turn", 3, 1, "0,10,0.5", "1.5"),
            new GodotScriptExport("offset", 5, 0, string.Empty, "Vector2(1, -2)"),
            new GodotScriptExport("tint", 20, 0, string.Empty, null),
            new GodotScriptExport("started", 0, 0, string.Empty, null),
        ], exports);
    }

    /// <summary>Each value as Godot 4.7.2 folded it for its inspector: operators by Variant's rules, a value after one by one
    /// more, an earlier value, a constant of constants, another enum's value and an unnamed one's.</summary>
    [Fact]
    public void An_enums_values_written_as_expressions_are_folded_as_Godot_folds_them()
    {
        const string Script = """
            extends Node

            enum Mood { LOW = 1 << 3, MID, NEG = -2, NEXT, SUM = LOW + 1, OR = 1 | 4, MUL = 3 * 3, DIV = 7 / 2, MOD = 7 % 4, SHR = 256 >> 2, XOR = 5 ^ 1, AND = 6 & 3, NOT = ~1, POW = 2 ** 5, PAREN = (1 + 2) * 3, HELD = LIMIT, OTHER = Other.B, UN = NORTH, LAST }
            enum Other { A = 3, B }
            enum { NORTH = 40, SOUTH }
            const LIMIT = 20 + 1
            const TWICE := LIMIT * 2
            enum Third { X = TWICE, Y, W = int(5.5), V = 1 if true else 2 }

            @export var mood: Mood = Mood.MID
            @export var third := Third.Y

            """;
        Assert.Equal(
        [
            new GodotScriptExport("mood", 2, 2, "LOW:8,MID:9,NEG:-2,NEXT:-1,SUM:9,OR:5,MUL:9,DIV:3,MOD:3,SHR:64,XOR:4,AND:2,NOT:-2,POW:32,PAREN:9,HELD:21,OTHER:4,UN:40,LAST:41", "9"),
            new GodotScriptExport("third", 2, 2, "X:42,Y:43,W:5,V:1", "43"),
        ], GDScriptExports.Of(Script));
    }

    /// <summary>An enum with a value only Godot can fold is an integer to type, never choices that would write a wrong
    /// value into the scene, and its member's default is left unknown.</summary>
    [Fact]
    public void An_enum_with_a_value_only_Godot_can_fold_is_a_plain_integer()
    {
        IReadOnlyList<GodotScriptExport> exports = GDScriptExports.Of("""
            extends Node

            enum Mode { A = Engine.get_frames_drawn(), B }
            enum Loop { C = D, D = C }

            @export var mode: Mode = Mode.B
            @export var loop: Loop

            """);
        Assert.Equal([new GodotScriptExport("mode", 2, 0, string.Empty, null), new GodotScriptExport("loop", 2, 0, string.Empty, null)], exports);
    }

    /// <summary>A script's exports lead, then each script's it extends as Godot's inspector lists them, by a path written or a
    /// class's name, an inherited enum by its values, each saying the file it comes from.</summary>
    [Fact]
    public void Inherited_exports_follow_a_scripts_own_through_its_bases()
    {
        Dictionary<string, GDScriptTree> trees = new(StringComparer.Ordinal)
        {
            ["res://base.gd"] = GDScriptTree.Parse("class_name Thing\nextends Node\n\nenum Mood { LOW = 1 << 3, MID }\n\n@export var speed := 3.5\n@export var mood: Mood = Mood.MID\nvar hidden := 1\n"),
            ["res://middle.gd"] = GDScriptTree.Parse("extends Thing\n\n@export var mine: Mood = Mood.LOW\n"),
            ["res://top.gd"] = GDScriptTree.Parse("extends \"res://middle.gd\"\n\n@export var last := 1\n"),
        };
        GDScriptProject project = new(null, path => trees.GetValueOrDefault(path), new Dictionary<string, string> { ["Thing"] = "res://base.gd" }, new Dictionary<string, string>());
        Assert.Equal(
        [
            new GodotScriptExport("last", 2, 0, string.Empty, "1"),
            new GodotScriptExport("mine", 2, 2, "LOW:8,MID:9", "8", "middle.gd"),
            new GodotScriptExport("speed", 3, 0, string.Empty, "3.5", "base.gd"),
            new GodotScriptExport("mood", 2, 2, "LOW:8,MID:9", "9", "base.gd"),
        ], GDScriptExports.Of(trees["res://top.gd"], "res://top.gd", project));
    }
}
