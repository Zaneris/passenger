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

    [Fact]
    public void A_project_godot_with_no_project_beside_it_is_a_game_of_gdscript_alone_and_one_beside_a_csproj_is_not()
    {
        Write(Path.Combine("ticker", "project.godot"), Named);
        Write(Path.Combine("spinner", "project.godot"), Named);
        Write(Path.Combine("spinner", "Spinner.csproj"), "<Project Sdk=\"Godot.NET.Sdk/4.7.2\" />\n");

        WorkspaceModel read = WorkspaceModel.Read(root);

        Assert.Equal([Path.Combine(root, "ticker")], read.GodotGames);
        Assert.Equal([Path.Combine(root, "spinner", "Spinner.csproj")], read.Projects.Select(p => p.Path));
    }

    [Fact]
    public void A_project_godot_inside_another_godot_project_or_a_csharp_projects_folder_is_no_game_of_its_own()
    {
        Write(Path.Combine("game", "project.godot"), Named);
        Write(Path.Combine("game", "addons", "demo", "project.godot"), Named);
        Write(Path.Combine("tool", "Tool.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write(Path.Combine("tool", "sample", "project.godot"), Named);

        Assert.Equal([Path.Combine(root, "game")], WorkspaceModel.Read(root).GodotGames);
    }

    [Fact]
    public void A_game_a_project_builds_csharp_for_is_left_to_that_project_and_one_of_gdscript_alone_stands_as_its_project_godot()
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
        ProjectModel scripts = ProjectModel.OfScriptGame(game);
        Assert.True(scripts is { IsScriptGame: true, Name: "Ticker", Exists: true } && scripts.GodotFolder == game && scripts.Path == Path.Combine(game, GodotProject.FileName),
            $"A game of GDScript alone stands as {scripts.Path}, named {scripts.Name}, its Godot folder {scripts.GodotFolder}");
    }
}
