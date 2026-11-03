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
}
