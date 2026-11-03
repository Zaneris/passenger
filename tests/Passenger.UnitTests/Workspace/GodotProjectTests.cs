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
        Assert.Equal(new GodotProject("/games/space", "Space \"Shooter\"", "uid://b8k2m3n4p5q6", "SpaceShooter") { WindowSize = (1280, 648) }, project);
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

    [Fact]
    public void A_scene_uses_a_script_its_external_resources_name_by_its_res_path()
    {
        const string Scene = """
            [gd_scene load_steps=2 format=3 uid="uid://c1"]

            [ext_resource type="Script" uid="uid://b2" path="res://Scripts/Player.cs" id="1_ab"]

            [node name="Main" type="Node2D"]
            script = ExtResource("1_ab")
            description = "path=\"res://Scripts/Enemy.cs\""

            """;

        Assert.True(GodotProject.Uses(Scene, "res://Scripts/Player.cs") && !GodotProject.Uses(Scene, "res://Scripts/Enemy.cs") && !GodotProject.Uses(Scene, "res://Player.cs"),
            "A scene was taken to use a script none of its external resources names, or not the one it names, so Godot's editor would open the wrong scene");
        Assert.True(GodotProject.IsScene("res://main.tscn") && GodotProject.IsScene("Level.SCN") && !GodotProject.IsScene("Main.cs"),
            "A scene was not told from a script by its extension");
    }

    [Fact]
    public void A_games_window_size_is_its_override_else_its_viewports_else_Godots_default()
    {
        Assert.Equal((1152, 648), GodotProject.Parse("/game", "config_version=5\n\n[application]\n\nconfig/name=\"Game\"\n").WindowSize);
        Assert.Equal((640, 360), GodotProject.Parse("/game", "[display]\n\nwindow/size/viewport_width=640\nwindow/size/viewport_height=360\n").WindowSize);
        Assert.Equal((1280, 360), GodotProject.Parse("/game", "[display]\n\nwindow/size/viewport_width=640\nwindow/size/viewport_height=360\nwindow/size/window_width_override=1280\n").WindowSize);
    }
}
