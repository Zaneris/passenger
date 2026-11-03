using System.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>The engine's classes as Godot's ClassDB lists them: a class's properties its own first then each parent's, as
/// Godot's inspector lists them, headings among them, and a default the nearest class saying one gives.</summary>
public class GodotClassDefaultsTests
{
    private static readonly GodotClassDefaults Classes = GodotClassDefaults.Read(Encoding.UTF8.GetBytes("""
        {
          "Node": { "inherits": "Object", "properties": [["Node", 0, 0, "", 128], ["name", 21, 0, "", 4], ["process_mode", 2, 2, "Inherit,Pausable", 6]],
                    "defaults": { "process_mode": "0", "name": "&\"\"" } },
          "CanvasItem": { "inherits": "Node", "properties": [["Visibility", 0, 0, "", 64], ["modulate", 20, 0, "", 6]], "defaults": {} },
          "Node2D": { "inherits": "CanvasItem", "properties": [["position", 5, 0, "", 6], ["global_position", 5, 0, "", 0]],
                      "defaults": { "position": "Vector2(0, 0)", "modulate": "Color(1, 1, 1, 1)" } }
        }
        """));

    [Fact]
    public void A_class_lists_its_own_properties_before_its_parents_with_their_headings()
    {
        Assert.Equal(["position", "global_position", "Visibility", "modulate", "Node", "name", "process_mode"], Classes.PropertiesOf("Node2D").Select(p => p.Name));
        Assert.Equal(["Node2D", "CanvasItem", "Node"], Classes.Chain("Node2D"));
        GodotClassProperty heading = Classes.PropertiesOf("Node2D").Single(p => p.Name == "Visibility");
        Assert.True(heading.IsHeading && !heading.Shown, "A group's heading was read as a property the inspector shows");
        Assert.False(Classes.PropertiesOf("Node2D").Single(p => p.Name == "global_position").Shown, "A property without the editor's usage was read as one the inspector shows");
        Assert.Equal(("process_mode", 2L, "Inherit,Pausable", "Node"), Classes.PropertiesOf("Node2D").Single(p => p.Name == "process_mode") is var mode ? (mode.Name, mode.Hint, mode.HintString, mode.Owner) : default);
    }

    [Fact]
    public void A_default_comes_from_the_nearest_class_that_says_one()
    {
        Assert.Equal("Color(1, 1, 1, 1)", Classes.DefaultOf("Node2D", "modulate"));
        Assert.Equal("0", Classes.DefaultOf("Node2D", "process_mode"));
        Assert.Null(Classes.DefaultOf("CanvasItem", "modulate"));
        Assert.Empty(Classes.PropertiesOf("Unknown"));
    }
}
