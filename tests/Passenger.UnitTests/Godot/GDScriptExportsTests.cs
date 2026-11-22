using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>A GDScript's exports as Godot's inspector takes them: an enum of the script's by its names and values with its
/// member's value as the default, <c>@export_enum</c>'s and <c>@export_range</c>'s hints, a type written or inferred from
/// a literal or a built-in type's call, and no default where only running the code gives one.</summary>
public class GDScriptExportsTests
{
    /// <summary>A script's exports are typed, hinted and defaulted as Godot's inspector takes them.</summary>
    [Fact]
    public void Exports_are_typed_hinted_and_defaulted_as_godots_inspector_takes_them() => Cases.All(
        (nameof(Each_export_is_typed_hinted_and_defaulted_as_Godots_inspector_takes_it),
            Each_export_is_typed_hinted_and_defaulted_as_Godots_inspector_takes_it),
        (nameof(Inherited_exports_follow_a_scripts_own_through_its_bases), Inherited_exports_follow_a_scripts_own_through_its_bases));

    private void Each_export_is_typed_hinted_and_defaulted_as_Godots_inspector_takes_it()
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

    /// <summary>A script's exports lead, then each script's it extends as Godot's inspector lists them, by a path written or a
    /// class's name, an inherited enum by its values, each saying the file it comes from.</summary>
    private void Inherited_exports_follow_a_scripts_own_through_its_bases()
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
