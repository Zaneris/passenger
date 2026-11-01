using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a Godot project's project.godot says as Godot reads its ConfigFile: the project's name, the scene its game
/// starts in and the assembly its C# is loaded from, by section and key, past comments, escapes and values of other kinds
/// over several lines.</summary>
public class GodotProjectTests
{
    [Fact]
    public void The_name_main_scene_and_assembly_are_read_by_their_sections()
    {
        const string Text = """
            ; Engine configuration file.
            ; It's best edited using the editor UI and not directly,
            ; since the parameters that go here are not all obvious.

            config_version=5

            [application]

            config/name="Space \"Shooter\""
            config/description="Two lines
            of words; not a comment"
            run/main_scene="uid://b8k2m3n4p5q6"
            config/features=PackedStringArray("4.7", "C#", "Forward Plus")
            config/icon="res://icon.svg"

            [display]

            window/size/viewport_width=1280

            [dotnet]

            project/assembly_name="SpaceShooter"

            [input]

            fire={
            "deadzone": 0.5,
            "events": [Object(InputEventKey,"resource_local_to_scene":false,"keycode":0,"unicode":32)]
            }

            """;
        GodotProject project = GodotProject.Parse("/games/space", Text);
        Assert.Equal(new GodotProject("/games/space", "Space \"Shooter\"", "uid://b8k2m3n4p5q6", "SpaceShooter"), project);
    }

    [Fact]
    public void A_value_never_set_is_none_and_a_key_set_twice_keeps_its_last()
    {
        const string Text = """
            [application]
            config/name="First"
            config/name="Second"
            ; run/main_scene="res://commented.tscn"
            [rendering]
            run/main_scene="res://other_section.tscn"
            """;
        GodotProject project = GodotProject.Parse("/games/x", Text);
        Assert.True(project is { Name: "Second", MainScene: null, AssemblyName: null },
            $"project.godot read as {project}: a main scene commented out or set in another section would have the game refused for the wrong reason or run with none");
    }
}
