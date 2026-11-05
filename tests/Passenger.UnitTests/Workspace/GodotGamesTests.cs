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

    /// <summary>A game of GDScript alone takes any Godot 4, .NET or not, as its Run does, and no older Godot: its project
    /// would be written for one that cannot open it.</summary>
    [Fact]
    public void A_gdscript_game_is_refused_a_godot_older_than_4_saying_where_godot_4_comes_from()
    {
        GodotFound three = new("/opt/Godot_v3.5.3-stable_x11.64", "the PATH") { Exists = true, Version = "3.5.3" };
        GodotFound four = three with { Version = "4.7.2" };

        Assert.Equal("A Godot game needs Godot 4: /opt/Godot_v3.5.3-stable_x11.64 is Godot 3.5.3: Passenger runs Godot 4's projects. Godot 4 comes from godotengine.org/download",
            GodotGames.WhyNot(three, scripts: true, "none"));
        Assert.Null(GodotGames.WhyNot(four, scripts: true, "none"));
        Assert.StartsWith("A Godot game in C# needs Godot's .NET build: ", GodotGames.WhyNot(four, scripts: false, "none"), StringComparison.Ordinal);
        Assert.Equal("A Godot game needs Godot, and none. Godot 4 comes from godotengine.org/download", GodotGames.WhyNot(null, scripts: true, "none"));
    }

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
        Assert.Equal([csharp], WorkspaceModel.BuildingGodot([elsewhere, ProjectModel.Unevaluated(Write(Path.Combine("src", "Other.csproj"), "<Project />\n"))], game));
        ProjectModel scripts = ProjectModel.OfScriptGame(game);
        Assert.True(scripts is { IsScriptGame: true, Name: "Ticker", Exists: true } && scripts.GodotFolder == game && scripts.Path == Path.Combine(game, GodotProject.FileName),
            $"A game of GDScript alone stands as {scripts.Path}, named {scripts.Name}, its Godot folder {scripts.GodotFolder}");
    }

    /// <summary>A project evaluated with these: GodotProjectDir as it stands after its body, the generators' folder as the
    /// SDK's props wrote it from what stood before the body, and where its build lands.</summary>
    private ProjectModel Evaluated(string written, string? generators, string? target)
    {
        string csharp = Write("Game.csproj", "<Project />\n");
        Dictionary<string, Passenger.Host.EvaluatedProperty> properties = new() { [Passenger.Host.CarriedProperties.GodotProjectDir] = new(written, true) };
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
    [Theory]
    [InlineData("{root}/game/")]
    [InlineData("game")]
    public void A_project_setting_its_godot_folder_in_its_body_is_told_what_its_scripts_and_build_miss(string written)
    {
        Directory.CreateDirectory(Path.Combine(root, "game"));
        string problem = Assert.IsType<string>(Evaluated(written.Replace("{root}", root, StringComparison.Ordinal), $"{root}/", $"{root}/.godot/mono/temp/bin/Debug/Game.dll").GodotLayoutProblem);

        Assert.Contains($"Godot's source generators name its scripts from {root}/", problem, StringComparison.Ordinal);
        Assert.Contains($"its build goes to {root}/.godot/mono/temp/bin/Debug", problem, StringComparison.Ordinal);
        Assert.Contains("Set GodotProjectDir to a full path in a PropertyGroup before <Import Project=\"Sdk.props\"", problem, StringComparison.Ordinal);
    }

    /// <summary>The generators read a relative folder from the compiler's, a [ScriptPath] then climbing from there to the game,
    /// so setting the base 64 too doesn't mend a relative value.</summary>
    [Fact]
    public void A_relative_godot_folder_given_to_the_generators_is_said_to_be_read_from_the_compilers_folder()
    {
        Directory.CreateDirectory(Path.Combine(root, "game"));
        string problem = Assert.IsType<string>(Evaluated("game/", "game/", $"{root}/game/.godot/mono/temp/bin/Debug/Game.dll").GodotLayoutProblem);

        Assert.Contains("Godot's source generators read game/ from the compiler's own folder", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("its build goes to", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_whose_generators_are_right_is_told_only_where_its_build_goes()
    {
        Directory.CreateDirectory(Path.Combine(root, "game"));
        string problem = Assert.IsType<string>(Evaluated($"{root}/game/", $"{root}/game/", $"{root}/.godot/mono/temp/bin/Debug/Game.dll").GodotLayoutProblem);

        Assert.DoesNotContain("source generators", problem, StringComparison.Ordinal);
        Assert.Contains("its build goes to", problem, StringComparison.Ordinal);
    }

    /// <summary>A folder set before the SDK's props reaches both, and the project beside its project.godot never needed to.</summary>
    [Fact]
    public void A_project_whose_scripts_and_build_reach_its_godot_folder_is_told_nothing()
    {
        Directory.CreateDirectory(Path.Combine(root, "game"));

        Assert.Null(Evaluated($"{root}/game/", $"{root}/game/", $"{root}/game/.godot/mono/temp/bin/Debug/Game.dll").GodotLayoutProblem);
        Assert.Null(Evaluated($"{root}/", $"{root}/", $"{root}/.godot/mono/temp/bin/Debug/Game.dll").GodotLayoutProblem);
        Assert.Null((Evaluated($"{root}/game/", null, null) with { Evaluated = null }).GodotLayoutProblem);
    }

    [Fact]
    public void A_project_of_another_sdk_says_nothing_of_a_godot_folder()
    {
        Assert.Null(ProjectModel.Unevaluated(Write("App.csproj", "<Project />\n")).GodotLayoutProblem);
    }
}
