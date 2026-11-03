using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A new Godot 4 game's files as Godot's editor writes a new project's: project.godot starting its main scene,
/// whose root runs a script that prints, and for C# its project on the Godot found's Godot.NET.Sdk restoring Godot's
/// packages from that Godot's own folder.</summary>
public class GodotGamesFilesTests
{
    [Fact]
    public void A_csharp_game_is_a_project_godot_a_scene_its_script_and_a_project_on_the_godots_sdk()
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

    /// <summary>The C# game's nuget.config reads as XML with the same feeds and mapping, a comment saying how a feed of the
    /// person's is added and mapped, as a mapping gives each package only from the feeds it names.</summary>
    [Fact]
    public void A_csharp_games_nuget_config_says_in_a_comment_how_a_feed_of_ones_own_is_added_and_mapped()
    {
        string config = GodotGames.Files("Spinner", csharp: true, "4.7.2", "net10.0", "/Godot/nupkgs").Single(f => f.Path == "nuget.config").Text;
        System.Xml.Linq.XElement root = System.Xml.Linq.XDocument.Parse(config).Root!;
        System.Xml.Linq.XComment said = Assert.Single(root.Nodes().OfType<System.Xml.Linq.XComment>());
        Assert.Contains("under packageSources:       <add key=\"MyFeed\"", said.Value, StringComparison.Ordinal);
        Assert.Contains("under packageSourceMapping: <packageSource key=\"MyFeed\"><package pattern=\"MyCompany.*\" /></packageSource>", said.Value, StringComparison.Ordinal);
        Assert.Equal([("Godot", "/Godot/nupkgs"), ("nuget.org", "https://api.nuget.org/v3/index.json")],
            root.Element("packageSources")!.Elements("add").Select(a => ((string)a.Attribute("key")!, (string)a.Attribute("value")!)));
        Assert.Equal([("Godot", "Godot*"), ("nuget.org", "*")],
            root.Element("packageSourceMapping")!.Elements("packageSource").SelectMany(s => s.Elements("package").Select(p => ((string)s.Attribute("key")!, (string)p.Attribute("pattern")!))));
    }

    [Fact]
    public void A_gdscript_game_has_no_project_and_its_scene_runs_its_gdscript()
    {
        Dictionary<string, string> files = GodotGames.Files("Spinner", csharp: false, "4.7.2").ToDictionary(f => f.Path, f => f.Text);
        Assert.Equal(["project.godot", "main.tscn", "main.gd", ".gitignore"], files.Keys);
        Assert.DoesNotContain("[dotnet]", files["project.godot"], StringComparison.Ordinal);
        Assert.Contains("PackedStringArray(\"4.7\", \"Forward Plus\")", files["project.godot"], StringComparison.Ordinal);
        Assert.Equal("res://main.gd", Assert.Single(GodotFile.Parse(files["main.tscn"]).Externals).Path);
        Assert.Contains("print(\"Hello from Godot!\")", files["main.gd"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_games_frameworks_are_those_godots_host_runs_from_net_8_to_the_newest_released_runtime()
    {
        Assert.Equal(["net10.0", "net9.0", "net8.0"],
            GodotGames.Runnable(["net11.0", "net10.0", "net9.0", "net8.0", "net7.0"], ["11.0.0-rc.3.26522.148", "10.0.7", "8.0.21"]));
        Assert.Equal(["net10.0", "net8.0"], GodotGames.Runnable(["net11.0"], ["11.0.0-rc.3.26522.148", "10.0.7", "8.0.21", "7.0.20"]));
    }

    [Fact]
    public void A_games_frameworks_are_all_offered_where_no_runtime_is_known() =>
        Assert.Equal(["net11.0", "net10.0"], GodotGames.Runnable(["net11.0", "net10.0"], []));
}
