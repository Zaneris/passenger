using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>The Godot projects of GDScript alone a folder's walk finds: a project.godot in a folder with no project file, none
/// inside another Godot project or a C# project's folder, and one a project builds C# for (its Godot folder) left to that
/// project; each stands among what Run runs as its project.godot, named as the project names its game.</summary>
public sealed class GodotGamesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Write(string path, string text)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private const string Named = "config_version=5\n\n[application]\n\nconfig/name=\"Ticker\"\n";

    /// <summary>A project.godot is a game of its own only where no C# project builds it and no Godot project holds
    /// it.</summary>
    [Fact]
    public void A_project_godot_is_a_game_of_its_own_only_where_no_project_builds_it() => Cases.AllApart<GodotGamesTests>(
        (nameof(A_project_godot_with_no_project_beside_it_is_a_game_of_gdscript_alone_and_one_beside_a_csproj_is_not),
            test => test.A_project_godot_with_no_project_beside_it_is_a_game_of_gdscript_alone_and_one_beside_a_csproj_is_not()),
        (nameof(A_project_godot_inside_another_godot_project_or_a_csharp_projects_folder_is_no_game_of_its_own),
            test => test.A_project_godot_inside_another_godot_project_or_a_csharp_projects_folder_is_no_game_of_its_own()),
        (nameof(A_game_a_project_builds_csharp_for_is_left_to_that_project_and_one_of_gdscript_alone_stands_as_its_project_godot),
            test => test.A_game_a_project_builds_csharp_for_is_left_to_that_project_and_one_of_gdscript_alone_stands_as_its_project_godot()));

    /// <summary>A project naming its Godot folder is told what its scripts and build miss, and nothing where they reach
    /// it.</summary>
    [Fact]
    public void A_project_is_told_what_its_godot_folder_leaves_its_build_missing() => Cases.AllApart<GodotGamesTests>(
        (nameof(A_project_setting_its_godot_folder_in_its_body_is_told_what_its_scripts_and_build_miss),
            _ => A_project_setting_its_godot_folder_in_its_body_is_told_what_its_scripts_and_build_miss()));

    private void A_project_godot_with_no_project_beside_it_is_a_game_of_gdscript_alone_and_one_beside_a_csproj_is_not()
    {
        Write(Path.Combine("ticker", "project.godot"), Named);
        Write(Path.Combine("spinner", "project.godot"), Named);
        Write(Path.Combine("spinner", "Spinner.csproj"), "<Project Sdk=\"Godot.NET.Sdk/4.7.2\" />\n");

        WorkspaceModel read = WorkspaceModel.Read(root);

        Assert.Equal([Path.Combine(root, "ticker")], read.GodotGames);
        Assert.Equal([Path.Combine(root, "spinner", "Spinner.csproj")], read.Projects.Select(p => p.Path));
    }

    private void A_project_godot_inside_another_godot_project_or_a_csharp_projects_folder_is_no_game_of_its_own()
    {
        Write(Path.Combine("game", "project.godot"), Named);
        Write(Path.Combine("game", "addons", "demo", "project.godot"), Named);
        Write(Path.Combine("tool", "Tool.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write(Path.Combine("tool", "sample", "project.godot"), Named);

        Assert.Equal([Path.Combine(root, "game")], WorkspaceModel.Read(root).GodotGames);
    }

    private void A_game_a_project_builds_csharp_for_is_left_to_that_project_and_one_of_gdscript_alone_stands_as_its_project_godot()
    {
        string game = Path.Combine(root, "game");
        Write(Path.Combine("game", "project.godot"), Named);
        WorkspaceModel read = WorkspaceModel.Read(root);
        string csharp = Write(Path.Combine("src", "Game.csproj"), "<Project />\n");
        ProjectModel elsewhere = ProjectModel.Unevaluated(csharp) with
        {
            Evaluated = new Passenger.Host.EvaluatedProject
            {
                Path = csharp,
                Properties = new Dictionary<string, Passenger.Host.EvaluatedProperty> { [Passenger.Host.CarriedProperties.GodotProjectDir] = new("../game", true) },
            },
        };

        Assert.Equal([game], read.ScriptGames([]));
        Assert.Empty(read.ScriptGames([elsewhere]));
        Assert.Equal([csharp], WorkspaceModel.BuildingGodot([elsewhere, ProjectModel.Unevaluated(Write(Path.Combine("src", "Other.csproj"), "<Project />\n"))], game));
        ProjectModel scripts = ProjectModel.OfScriptGame(game);
        Assert.True(scripts is { IsScriptGame: true, Name: "Ticker", Exists: true } && scripts.GodotFolder == game && scripts.Path == Path.Combine(game, GodotProject.FileName),
            $"A game of GDScript alone stands as {scripts.Path}, named {scripts.Name}, its Godot folder {scripts.GodotFolder}");
    }

    /// <summary>A project Godot.NET.Sdk builds, evaluated with these: GodotProjectDir as it stands after its body, the
    /// generators' folder as the SDK's props wrote it from what stood before the body, and where its build lands.</summary>
    private ProjectModel Evaluated(string written, string? generators, string? target)
    {
        string csharp = Write("Game.csproj", "<Project />\n");
        Dictionary<string, Passenger.Host.EvaluatedProperty> properties = new()
        {
            [Passenger.Host.CarriedProperties.GodotProjectDir] = new(written, true),
            [Passenger.Host.CarriedProperties.UsingGodotNETSdk] = new("true", false),
        };
        if (generators is not null)
        {
            properties[Passenger.Host.CarriedProperties.GodotProjectDirBase64] = new(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(generators)), false);
        }
        if (target is not null)
        {
            properties[Passenger.Host.CarriedProperties.TargetPath] = new(target, false);
        }
        return ProjectModel.Unevaluated(csharp) with { Evaluated = new Passenger.Host.EvaluatedProject { Path = csharp, Properties = properties } };
    }

    /// <summary>A project above its Godot folder setting GodotProjectDir in its body, full or relative, has
    /// [ScriptPath("res://game/Main.cs")] and its build beside the project, and Godot finds no class for res://Main.cs.</summary>
    private static void A_project_setting_its_godot_folder_in_its_body_is_told_what_its_scripts_and_build_miss() => Cases.EachApart<GodotGamesTests, string>(
    [
        "{root}/game/",
        "game",
    ], (test, row) => test.A_project_setting_its_godot_folder_in_its_body_is_told_what_its_scripts_and_build_miss(row));

    private void A_project_setting_its_godot_folder_in_its_body_is_told_what_its_scripts_and_build_miss(string written)
    {
        Directory.CreateDirectory(Path.Combine(root, "game"));
        string problem = Assert.IsType<string>(Evaluated(written.Replace("{root}", root, StringComparison.Ordinal), $"{root}/", $"{root}/.godot/mono/temp/bin/Debug/Game.dll").GodotLayoutProblem);

        Assert.Contains($"Godot's source generators name its scripts from {root}/", problem, StringComparison.Ordinal);
        Assert.Contains($"its build goes to {Path.GetDirectoryName($"{root}/.godot/mono/temp/bin/Debug/Game.dll")}", problem, StringComparison.Ordinal);
        Assert.Contains("Set GodotProjectDir to a full path in a PropertyGroup before <Import Project=\"Sdk.props\"", problem, StringComparison.Ordinal);
    }

}
