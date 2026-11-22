using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's res:// paths as Passenger makes, resolves and turns them into files: a path a file writes resolved from
/// its folder and simplified as Godot 4.4 and later do, a '..' past res:// kept; a file's path made only under its project's
/// folder; and a res:// path's file refused where it climbs out of the folder, an export's output let go anywhere.</summary>
public class GodotResPathsTests
{
    private static readonly string Game = Path.GetFullPath("/games/space");

    /// <summary>A path resolves from its file's folder as Godot resolves it, never climbing into the project.</summary>
    [Fact]
    public void A_res_path_resolves_as_godot_resolves_it() => Cases.All(
        (nameof(A_written_path_resolves_from_its_files_folder_as_Godot_resolves_it),
            A_written_path_resolves_from_its_files_folder_as_Godot_resolves_it),
        (nameof(A_res_paths_file_is_under_the_project_its_sub_resource_dropped_and_one_climbing_out_refused),
            A_res_paths_file_is_under_the_project_its_sub_resource_dropped_and_one_climbing_out_refused));

    private void A_written_path_resolves_from_its_files_folder_as_Godot_resolves_it() => Cases.Each<(string Written, string From, string Resolved)>(
    [
        ("../base.gd", "res://sub/main.gd", "res://base.gd"),
        ("../base.gd", "res://main.gd", "res://../base.gd"),
        ("./lib//util.gd", "res://sub/main.gd", "res://sub/lib/util.gd"),
        ("common.gdshaderinc", "res://fx/water.tscn::Shader_x", "res://fx/common.gdshaderinc"),
        ("shaders\\a.gdshaderinc", "res://level.tscn::Shader_x", "res://shaders/a.gdshaderinc"),
        ("uid://b8k2m3n4p5q6", "res://main.gd", "uid://b8k2m3n4p5q6"),
    ], row => A_written_path_resolves_from_its_files_folder_as_Godot_resolves_it(row.Written, row.From, row.Resolved));

    private void A_written_path_resolves_from_its_files_folder_as_Godot_resolves_it(string written, string from, string resolved)
    {
        Assert.Equal(resolved, GodotResPaths.Resolved(written, from));
    }

    /// <summary>A stack frame, an error pin's or a profile's function names its script by a res:// path the game sent, so one
    /// climbing out of the project opens nothing.</summary>
    private void A_res_paths_file_is_under_the_project_its_sub_resource_dropped_and_one_climbing_out_refused()
    {
        Assert.Equal(Path.Combine(Game, "levels", "one.tscn"), GodotResPaths.FileOf(Game, "res://levels/one.tscn::Shader_x"));
        Assert.Null(GodotResPaths.FileOf(Game, "res://../outside.gd"));
        Assert.Null(GodotResPaths.FileOf(Game, "res://levels/../../outside.gd"));
        Assert.Null(GodotResPaths.FileOf(Game, "user://saves/one.tres"));
        Assert.Null(GodotResPaths.FileOf(Game, "res://"));
    }

}
