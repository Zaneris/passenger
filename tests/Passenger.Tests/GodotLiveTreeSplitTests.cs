using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Live Visual Tree's split for a small Godot game: the tree's half as tall as its rows, the properties taking
/// the rest, and a split the person dragged kept for Godot games when the window opens again. Godot runs headless, its user
/// folders the run's; without a Godot 4 .NET build each test skips saying why. Its game runs and is stopped, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotLiveTreeSplitTests : IClassFixture<GodotLiveTreeSplitTests.Game>
{
    private readonly Game game;

    public GodotLiveTreeSplitTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is null || game.Godot.WhyNot is not null, game.Skipped);
        game.Reset();
    }

    private const string Script = """
        using Godot;

        public partial class Main : Node
        {
            [Export]
            public float Speed { get; set; } = 1;

            public override void _Process(double delta)
            {
            }
        }

        """;

    /// <summary>Three nodes: Main, and a player and its label under it.</summary>
    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        [node name="Player" type="Node2D" parent="."]

        [node name="Tag" type="Label" parent="Player"]
        text = "P1"

        """;

    /// <summary>A three-node game's tree takes no more than its rows' height, its properties the rest; a split dragged lower
    /// stays where it was left, and is the split again once the window opens anew on the same state and runs the game.</summary>
    [AvaloniaFact]
    public void A_short_game_tree_takes_its_rows_height_the_properties_the_rest_and_a_dragged_split_comes_back()
    {
        string settings = TestFolders.Create("live-tree-split-state-");
        try
        {
            double kept;
            MainWindow window = Started(settings);
            try
            {
                LiveTreeView tree = window.LiveTree;
                double rows = tree.Rows.Count * ListRow.Height;
                double room = tree.TreeHalf.Bounds.Height + tree.DetailsHalf.Bounds.Height;
                Assert.True(tree.TreeHalf.Bounds.Height <= rows + 24,
                    $"The tree of {tree.Rows.Count} rows ({rows:0} px) takes {tree.TreeHalf.Bounds.Height:0} of {room:0} px, leaving the properties cramped under rows of nothing");
                Assert.True(tree.DetailsHalf.Bounds.Height >= room - rows - 24 && tree.PropertyList.Bounds.Height >= ListRow.Height * 3,
                    $"The properties take {tree.DetailsHalf.Bounds.Height:0} of {room:0} px, their list {tree.PropertyList.Bounds.Height:0}, where they take what the tree's rows leave");

                Point from = Centre(window, tree.Splitter);
                Ui.Drag(window, from, from + new Point(0, 140));
                Ui.Settle();
                kept = Share(tree);
                Assert.True(tree.TreeHalf.Bounds.Height >= rows + 100 && tree.DraggedSplit is double share && Math.Abs(share - kept) < 0.02,
                    $"The split dragged 140 px lower stands at {tree.TreeHalf.Bounds.Height:0} px ({tree.DraggedSplit}), not where it was left");
                Assert.True(window.Perform(AppActions.LiveTreeRefresh), "Refresh Live Visual Tree was refused with the game running");
                Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Tag"), () => $"the game's nodes read again ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})");
                Assert.True(Math.Abs(Share(tree) - kept) < 0.02, $"The split dragged to {kept:P0} of the room went to {Share(tree):P0} ({tree.TreeHalf.Bounds.Height:0} px) as the tree was read again");
            }
            finally
            {
                End(window);
            }

            MainWindow again = Started(settings);
            try
            {
                LiveTreeView tree = again.LiveTree;
                Assert.True(Math.Abs(Share(tree) - kept) < 0.02,
                    $"The split dragged to {kept:P0} of the room is {Share(tree):P0} once the window opens again and the game runs ({tree.DraggedSplit})");
            }
            finally
            {
                End(again);
            }
        }
        finally
        {
            TestFolders.Delete(settings);
        }
    }

    /// <summary>A window keeping its state in a folder of the test's, the game run, its Main node chosen in the Live Visual Tree
    /// with its properties shown.</summary>
    private MainWindow Started(string settings)
    {
        MainWindow window = game.Open(null, languageServer: false, state: settings, keepsLayout: true, godot: game.Homes);
        RunConfiguration generated = Generated(window, "Spinner");
        window.SetConfigurations([generated with { Name = "Spinner on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Spinner on Godot");
        Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
        LiveTreeView tree = window.LiveTree;
        Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main") && tree.TreeHalf.IsEffectivelyVisible,
            () => $"the Live Visual Tree to show the game's nodes ({window.LiveSource?.State}; {string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
        foreach (string name in (string[])["Main", "Player"])
        {
            if (tree.Rows.First(r => r.Element.Name == name) is { IsExpanded: false } closed)
            {
                tree.Toggle(closed);
            }
        }
        Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Tag"), () => $"the game's three nodes opened ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})");
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Main");
        Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "Speed"), () => $"Main's properties ({string.Join(", ", tree.PropertyRows.Select(p => p.Name))})");
        return window;
    }

    /// <summary>The tree's share of the split as it stands: the game's monitors over it may take another line as they change.</summary>
    private static double Share(LiveTreeView tree) => tree.TreeHalf.Bounds.Height / (tree.TreeHalf.Bounds.Height + tree.DetailsHalf.Bounds.Height);

    private static Point Centre(MainWindow window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static RunConfiguration Generated(MainWindow window, string name)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == name),
            () => $"the Godot project's configuration {name} ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        return window.Configurations.Single(c => c.Generated && c.Name == name);
    }

    private static void End(MainWindow window)
    {
        if (window.IsRunning)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "the game to end");
        }
        window.Close();
    }

    /// <summary>The Godot 4 C# project of three nodes, restored from the Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-split-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            if (Godot is { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            }
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot run a C# game: {Godot.WhyNot}";

        public IReadOnlyDictionary<string, string> Homes { get; }

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
