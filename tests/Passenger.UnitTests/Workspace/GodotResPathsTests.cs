using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's res:// paths as Passenger makes, resolves and turns them into files: a path a file writes resolved from
/// its folder and simplified as Godot 4.4 and later do, a '..' past res:// kept; a file's path made only under its project's
/// folder; and a res:// path's file refused where it climbs out of the folder, an export's output let go anywhere.</summary>
public class GodotResPathsTests
{
    private static readonly string Game = Path.GetFullPath("/games/space");

    [Theory]
    [InlineData("../base.gd", "res://sub/main.gd", "res://base.gd")]
    [InlineData("../base.gd", "res://main.gd", "res://../base.gd")]
    [InlineData("../../base.gd", "res://sub/main.gd", "res://../base.gd")]
    [InlineData("./lib//util.gd", "res://sub/main.gd", "res://sub/lib/util.gd")]
    [InlineData("res://a/./b/../c.gd", "res://main.gd", "res://a/c.gd")]
    [InlineData("common.gdshaderinc", "res://fx/water.tscn::Shader_x", "res://fx/common.gdshaderinc")]
    [InlineData("shaders\\a.gdshaderinc", "res://level.tscn::Shader_x", "res://shaders/a.gdshaderinc")]
    [InlineData("uid://b8k2m3n4p5q6", "res://main.gd", "uid://b8k2m3n4p5q6")]
    [InlineData("user://saves/one.tres", "res://main.gd", "user://saves/one.tres")]
    public void A_written_path_resolves_from_its_files_folder_as_Godot_resolves_it(string written, string from, string resolved)
    {
        Assert.Equal(resolved, GodotResPaths.Resolved(written, from));
    }

    [Fact]
    public void A_files_res_path_is_made_only_under_its_projects_folder()
    {
        Assert.Equal("res://levels/one.tscn", GodotResPaths.Of(Game, Path.Combine(Game, "levels", "one.tscn")));
        Assert.Null(GodotResPaths.Of(Game, Path.GetFullPath("/games/other/main.gd")));
        Assert.Null(GodotResPaths.Of(Game, Game));
    }

    /// <summary>A stack frame, an error pin's or a profile's function names its script by a res:// path the game sent, so one
    /// climbing out of the project opens nothing.</summary>
    [Fact]
    public void A_res_paths_file_is_under_the_project_its_sub_resource_dropped_and_one_climbing_out_refused()
    {
        Assert.Equal(Path.Combine(Game, "levels", "one.tscn"), GodotResPaths.FileOf(Game, "res://levels/one.tscn::Shader_x"));
        Assert.Null(GodotResPaths.FileOf(Game, "res://../outside.gd"));
        Assert.Null(GodotResPaths.FileOf(Game, "res://levels/../../outside.gd"));
        Assert.Null(GodotResPaths.FileOf(Game, "user://saves/one.tres"));
        Assert.Null(GodotResPaths.FileOf(Game, "res://"));
    }

    /// <summary>A script built into a scene has lines of its own, none of them the scene's: an error, a stack frame or a
    /// profile's function in it goes to no line of main.tscn, where the scene as a resource is still main.tscn.</summary>
    [Fact]
    public void A_script_built_into_a_scene_names_no_file_and_one_of_its_own_names_its_file()
    {
        Assert.Null(GodotResPaths.ScriptFileOf(Game, "res://main.tscn::GDScript_abc12"));
        Assert.Equal(Path.Combine(Game, "main.tscn"), GodotResPaths.FileOf(Game, "res://main.tscn::GDScript_abc12"));
        Assert.Equal(Path.Combine(Game, "actors", "player.gd"), GodotResPaths.ScriptFileOf(Game, "res://actors/player.gd"));
        Assert.Null(GodotResPaths.ScriptFileOf(Game, "res://../outside.gd"));
    }

    [Fact]
    public void An_exports_output_is_taken_from_the_projects_folder_wherever_it_goes()
    {
        Assert.Equal(Path.GetFullPath("/games/builds/space.exe"), GodotResPaths.Globalized(Game, "res://../builds/space.exe"));
        Assert.Equal(Path.Combine(Game, "builds", "space.exe"), GodotResPaths.Globalized(Game, "builds/space.exe"));
        Assert.Equal(Path.GetFullPath("/elsewhere/space.exe"), GodotResPaths.Globalized(Game, Path.GetFullPath("/elsewhere/space.exe")));
    }
}
