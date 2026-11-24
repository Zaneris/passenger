using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A new Godot 4 game's files as Godot's editor writes a new project's: project.godot starting its main scene,
/// whose root runs a script that prints, and for C# its project on the Godot found's Godot.NET.Sdk restoring Godot's
/// packages from that Godot's own folder.</summary>
public class GodotGamesFilesTests
{
    /// <summary>A new game's files and frameworks are those its Godot builds and runs.</summary>
    [Fact]
    public void A_new_games_files_and_frameworks_are_those_its_godot_takes() => Cases.All(
        (nameof(A_csharp_game_is_a_project_godot_a_scene_its_script_and_a_project_on_the_godots_sdk),
            A_csharp_game_is_a_project_godot_a_scene_its_script_and_a_project_on_the_godots_sdk),
        (nameof(A_gdscript_game_has_no_project_and_its_scene_runs_its_gdscript), A_gdscript_game_has_no_project_and_its_scene_runs_its_gdscript),
        (nameof(A_game_targets_the_newest_framework_its_godot_builds_and_runs_else_the_newest_it_can_never_one_older_than_its_godots),
            A_game_targets_the_newest_framework_its_godot_builds_and_runs_else_the_newest_it_can_never_one_older_than_its_godots));

    private void A_csharp_game_is_a_project_godot_a_scene_its_script_and_a_project_on_the_godots_sdk()
    {
        Dictionary<string, string> files = GodotGames.Files("Spinner", csharp: true, "4.7.2", "net10.0", "/Godot/nupkgs").ToDictionary(f => f.Path, f => f.Text);
        Assert.Equal(["project.godot", "main.tscn", "Main.cs", ".gitignore", "Spinner.csproj", "nuget.config"], files.Keys);
        GodotProject project = GodotProject.Parse("/game", files["project.godot"]);
        Assert.Equal(("Spinner", "res://main.tscn", "Spinner"), (project.Name, project.MainScene, project.AssemblyName));
        Assert.Contains("config/features=PackedStringArray(\"4.7\", \"C#\", \"Forward Plus\")", files["project.godot"], StringComparison.Ordinal);
        GodotFile scene = GodotFile.Parse(files["main.tscn"]);
        Assert.Equal("res://Main.cs", Assert.Single(scene.Externals).Path);
        Assert.Equal("1_main", Assert.Single(scene.Nodes).Script);
        Assert.Contains("GD.Print(\"Hello from Godot!\");", files["Main.cs"], StringComparison.Ordinal);
        Assert.StartsWith("<Project Sdk=\"Godot.NET.Sdk/4.7.2\">", files["Spinner.csproj"], StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", files["Spinner.csproj"], StringComparison.Ordinal);
        Assert.Contains("<add key=\"Godot\" value=\"/Godot/nupkgs\" />", files["nuget.config"], StringComparison.Ordinal);
    }

    private void A_gdscript_game_has_no_project_and_its_scene_runs_its_gdscript()
    {
        Dictionary<string, string> files = GodotGames.Files("Spinner", csharp: false, "4.7.2").ToDictionary(f => f.Path, f => f.Text);
        Assert.Equal(["project.godot", "main.tscn", "main.gd", ".gitignore"], files.Keys);
        Assert.DoesNotContain("[dotnet]", files["project.godot"], StringComparison.Ordinal);
        Assert.Contains("PackedStringArray(\"4.7\", \"Forward Plus\")", files["project.godot"], StringComparison.Ordinal);
        Assert.Equal("res://main.gd", Assert.Single(GodotFile.Parse(files["main.tscn"]).Externals).Path);
        Assert.Contains("print(\"Hello from Godot!\")", files["main.gd"], StringComparison.Ordinal);
    }

    private void A_game_targets_the_newest_framework_its_godot_builds_and_runs_else_the_newest_it_can_never_one_older_than_its_godots()
    {
        string[] offered = ["net11.0", "net10.0", "net9.0", "net8.0"];
        Assert.Equal(["net10.0", "net9.0", "net8.0"], GodotGames.Runnable(offered, ["11.0.0-rc.3.26522.148", "10.0.15", "9.0.7"], 8));
        Assert.Equal(["net10.0"], GodotGames.Runnable(offered, ["11.0.0-rc.3.26522.148", "10.0.15", "9.0.7"], 10));
        Assert.Equal(["net11.0", "net10.0"], GodotGames.Runnable(offered, [], 10));
        string[] runnable = ["net11.0", "net10.0", "net9.0", "net8.0"];
        Assert.Equal("net10.0", GodotGames.Default(runnable, 10));
        Assert.Equal("net11.0", GodotGames.Default(runnable, 12));
        Assert.Equal("net11.0", GodotGames.Default(runnable, null));
        Assert.Equal("net11.0", GodotGames.Default(runnable, 7));
        Assert.Null(GodotGames.Default([], 10));
    }
}
