using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A Godot project in a folder below its C# project (Game.csproj at the top, GodotProjectDir naming the folder): the
/// project's evaluation ties them together, so a res:// path the code names follows a move as it does with the project in
/// the Godot folder. Its project is restored from the packages the Godot found carries, so without a Godot 4 .NET build each
/// test skips saying why; no Godot runs.</summary>
public sealed class GodotProjectAboveTests : IClassFixture<GodotProjectAboveTests.Game>
{
    private const string Script = """
        using Godot;

        public partial class Main : Node2D
        {
            public override void _Ready()
            {
                PackedScene player = GD.Load<PackedScene>("res://player.tscn");
                Node gone = GetNode("Gone");
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")

        """;

    private const string Player = "[gd_scene format=3]\n\n[node name=\"Player\" type=\"Node2D\"]\n";

    private readonly Game game;

    public GodotProjectAboveTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is not { WhyNot: null, Version: not null, Packages: not null }, game.Skipped);
        game.Reset();
    }

    /// <summary>The code of a project above the Godot folder names the moved scene: its string follows the move.</summary>
    [AvaloniaFact]
    public void A_moved_scene_is_followed_by_the_code_of_a_project_above_the_godot_folder()
    {
        MainWindow window = game.Open(game.PathOf(Path.Combine("game", "Main.cs")));
        try
        {
            Ui.WaitFor(() => GodotSceneTests.Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor code = window.EditorArea.CodeEditor;
            GodotSceneTests.Choose(window, game.PathOf(Path.Combine("game", "player.tscn")));
            Ui.Run(window, EditorActions.Cut);
            GodotSceneTests.Choose(window, game.PathOf(Path.Combine("game", "actors")));
            Ui.Run(window, EditorActions.Paste);
            Ui.WaitFor(() => code.Document.Snapshot.ToString().Contains("GD.Load<PackedScene>(\"res://actors/player.tscn\")", StringComparison.Ordinal),
                () => $"the code's string to follow the scene ({window.Status}):\n{code.Document.Snapshot}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The project sets its Godot folder in its body, too late for Godot.NET.Sdk to build into it: the generators
    /// were told, but the build lands beside the project, where Godot finds no assembly. Problems says so, and what to set.</summary>
    [AvaloniaFact]
    public void A_project_above_its_godot_folder_is_told_where_its_build_misses_it()
    {
        MainWindow window = game.Open(game.PathOf(Path.Combine("game", "Main.cs")));
        try
        {
            Ui.WaitFor(() => window.BottomPanel.Problems.ToolsetProblems.Any(p => p.Message.Contains("does not build Spinner.csproj for its Godot folder game", StringComparison.Ordinal)),
                () => $"Problems to say what the project lacks ({window.Status}): {string.Join("; ", window.BottomPanel.Problems.ToolsetProblems.Select(p => p.Message))}");
            string said = window.BottomPanel.Problems.ToolsetProblems.First(p => p.Message.Contains("Godot.NET.Sdk", StringComparison.Ordinal)).Message;
            Assert.Contains("its build goes to", said, StringComparison.Ordinal);
            Assert.Contains("Set GodotProjectDir to a full path in a PropertyGroup before", said, StringComparison.Ordinal);
            Assert.DoesNotContain("source generators", said, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-above-")
        {
            Godot = GodotExecutables.Find(null);
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            string game = PathOf("game");
            Directory.CreateDirectory(Path.Combine(game, "actors"));
            GodotTests.Game.Make(game, version, packages, Script, Scene);
            File.WriteAllText(Path.Combine(game, "player.tscn"), Player);
            File.Move(Path.Combine(game, "nuget.config"), PathOf("nuget.config"));
            File.Delete(Path.Combine(game, "Spinner.csproj"));
            File.WriteAllText(PathOf("Spinner.csproj"), $"<Project Sdk=\"Godot.NET.Sdk/{version}\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                + "    <EnableDynamicLoading>true</EnableDynamicLoading>\n    <GodotProjectDir>$(MSBuildProjectDirectory)/game/</GodotProjectDir>\n"
                + "    <GodotProjectDirBase64>$([MSBuild]::ConvertToBase64('$(GodotProjectDir)'))</GodotProjectDirBase64>\n  </PropertyGroup>\n</Project>\n");
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
