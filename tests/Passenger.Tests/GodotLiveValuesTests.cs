using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A running Godot game's values in the code, with no breakpoint: the members of the script shown carry their
/// values at their lines' ends, read again as the game runs (a frame count climbing, a vector's parts, a node path's node),
/// the hint hovered shows its last minute as a line, Settings turns them off and on, and Stop takes them away; a script two
/// nodes run says so until one is chosen in the Live Visual Tree, whose values it then shows; a GDScript's vars show alike. Godot runs headless, its user
/// folders the run's; without a Godot 4 .NET build each test skips saying why. Its game runs and is stopped, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotLiveValuesTests : IClassFixture<GodotLiveValuesTests.Game>
{
    private readonly Game game;

    public GodotLiveValuesTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is null || game.Godot.WhyNot is not null, game.Skipped);
        game.Reset();
    }

    private const string Script = """
        using Godot;

        public partial class Main : Node
        {
            private int frames;

            private Vector2 drift = new(1.5f, -2);

            [Export]
            public NodePath Target { get; set; } = "Tag";

            public override void _Process(double delta)
            {
                frames++;
                drift.X += 0.5f;
            }
        }

        """;

    private const string Enemy = """
        using Godot;

        public partial class Enemy : Node
        {
            [Export]
            public int Strength { get; set; }

            private int hits;

            public override void _Process(double delta)
            {
                hits++;
            }
        }

        """;

    private const string Spark = """
        extends Node

        var sparks := 0


        func _process(_delta: float) -> void:
            sparks += 1

        """;

    /// <summary>Main with a node its path names, two enemies of one script, the second stronger, and a spark of GDScript.</summary>
    private const string Scene = """
        [gd_scene load_steps=4 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]
        [ext_resource type="Script" path="res://Enemy.cs" id="2"]
        [ext_resource type="Script" path="res://spark.gd" id="3"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        [node name="Tag" type="Node2D" parent="."]

        [node name="Enemy1" type="Node" parent="."]
        script = ExtResource("2")

        [node name="Enemy2" type="Node" parent="."]
        script = ExtResource("2")
        Strength = 7

        [node name="Spark" type="Node" parent="."]
        script = ExtResource("3")

        """;

    /// <summary>The frame count's hint climbs as the game runs beside the vector's parts and the node its path reaches; hovered
    /// after two readings it shows the member's last minute as a rising line; turned off in Settings every hint goes and none
    /// comes while the game runs, and back on they return; at Stop they go.</summary>
    [AvaloniaFact]
    public void A_members_value_shows_at_its_line_end_climbs_as_the_game_runs_charts_its_last_minute_on_hover_and_goes_at_Stop()
    {
        MainWindow window = Started("Main.cs");
        try
        {
            OpenDocument script = window.EditorArea.Active!;
            int frames = LineOf("Main.cs", "private int frames;");
            Ui.WaitFor(() => Count(script, frames) is not null, () => $"the frame count's hint ({Unread(window, script)})", TimeSpan.FromMinutes(1));
            long first = Count(script, frames)!.Value;
            Ui.WaitFor(() => Count(script, frames) > first, () => $"the frame count's hint to climb from {first} as the game runs ({Hints(script)})", TimeSpan.FromSeconds(20));
            // A third reading, so the last minute holds readings of two seconds at least.
            long second = Count(script, frames)!.Value;
            Ui.WaitFor(() => Count(script, frames) > second, () => $"the frame count's hint to climb again from {second} ({Hints(script)})", TimeSpan.FromSeconds(20));
            Assert.Matches(@"^drift: \([0-9.]+, -2\)$", Hint(script, LineOf("Main.cs", "private Vector2 drift")) ?? Hints(script));
            Assert.Equal("Target: Tag → Tag (Node2D)", Hint(script, LineOf("Main.cs", "public NodePath Target")));

            CodeEditor editor = window.EditorArea.CodeEditor;
            Rest(window, editor, script, frames);
            Ui.WaitFor(() => window.Assist.HoverText?.Contains("Main.frames = ", StringComparison.Ordinal) == true,
                () => $"the hint's card ({window.Assist.HoverText})", TimeSpan.FromSeconds(20));
            Sparkline line = Assert.Single(window.Assist.HoverCard!.GetVisualDescendants().OfType<Sparkline>());
            Assert.True(line.Count >= 2 && line.Readings[^1][0] > line.Readings[0][0],
                $"The card's line holds {line.Count} readings ({string.Join(", ", line.Readings.Select(r => r[0]))}), not the count's climb over its last minute");
            Assert.Contains("over the last", window.Assist.HoverText, StringComparison.Ordinal);
            window.MouseMove(new Point(0, 0), RawInputModifiers.None);

            window.ApplySettings(window.Settings with { GodotLiveValues = false });
            Ui.WaitFor(() => script.LiveValues.Count == 0, () => $"the hints to go with the setting off ({Hints(script)})");
            Ui.Hold(TimeSpan.FromSeconds(2.5), "two of the game's beats with the hints turned off");
            Assert.True(script.LiveValues.Count == 0, $"With the setting off the hints came back: {Hints(script)}");
            window.ApplySettings(window.Settings with { GodotLiveValues = true });
            Ui.WaitFor(() => Count(script, frames) is not null, () => $"the hints back with the setting on ({Hints(script)})", TimeSpan.FromSeconds(20));

            Assert.False(Run(window).Contains(GodotAgentFile, StringComparison.Ordinal), $"Passenger's helper raised errors in the game as it read its values:\n{Run(window)}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "the game to end");
            Ui.WaitFor(() => script.LiveValues.Count == 0, () => $"the hints to go once the game ended ({Hints(script)})");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A script two nodes run shows their count after its class until one is chosen in the Live Visual Tree, then
    /// that node's values, its name after the class; the other chosen, its values.</summary>
    [AvaloniaFact]
    public void A_script_two_nodes_run_shows_their_count_until_one_is_chosen_in_the_Live_Visual_Tree_then_its_values()
    {
        MainWindow window = Started("Enemy.cs");
        try
        {
            OpenDocument script = window.EditorArea.Active!;
            int named = LineOf("Enemy.cs", "public partial class Enemy");
            int strength = LineOf("Enemy.cs", "public int Strength");
            Ui.WaitFor(() => Hint(script, named) == "2 nodes run Enemy: choose one in the Live Visual Tree", () => $"the count of nodes after the class ({Unread(window, script)})",
                TimeSpan.FromMinutes(1));
            Assert.True(Hint(script, strength) is null, $"With no node chosen of two, Strength shows {Hint(script, strength)}");

            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"), () => $"the game's nodes ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            if (tree.Rows.First(r => r.Element.Name == "Main") is { IsExpanded: false } closed)
            {
                tree.Toggle(closed);
            }
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Enemy2"), () => $"Main's nodes ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})");
            tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Enemy2");
            Ui.WaitFor(() => Hint(script, strength) == "Strength: 7" && Hint(script, named) == "Enemy2, chosen of 2 nodes",
                () => $"the second enemy's values once chosen ({Hints(script)})", TimeSpan.FromSeconds(20));
            Assert.Matches("^hits: [0-9]+$", Hint(script, LineOf("Enemy.cs", "private int hits;")) ?? Hints(script));
            tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Enemy1");
            Ui.WaitFor(() => Hint(script, strength) == "Strength: 0" && Hint(script, named) == "Enemy1, chosen of 2 nodes",
                () => $"the first enemy's values once chosen ({Hints(script)})", TimeSpan.FromSeconds(20));
            Assert.False(Run(window).Contains(GodotAgentFile, StringComparison.Ordinal), $"Passenger's helper raised errors in the game as it read its values:\n{Run(window)}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A GDScript's var shows its value at its line's end, read again as the game runs, as a C# member's does.</summary>
    [AvaloniaFact]
    public void A_gdscripts_var_shows_its_value_at_its_line_end_and_climbs_as_the_game_runs()
    {
        MainWindow window = Started("spark.gd");
        try
        {
            OpenDocument script = window.EditorArea.Active!;
            int sparks = LineOf("spark.gd", "var sparks");
            Ui.WaitFor(() => Hint(script, sparks)?.StartsWith("sparks: ", StringComparison.Ordinal) == true, () => $"the spark count's hint ({Unread(window, script)})", TimeSpan.FromMinutes(1));
            string first = Hint(script, sparks)!;
            Ui.WaitFor(() => Hint(script, sparks) is { } now && now != first, () => $"the spark count's hint to change from {first} as the game runs ({Hints(script)})", TimeSpan.FromSeconds(20));
            Assert.False(Run(window).Contains(GodotAgentFile, StringComparison.Ordinal), $"Passenger's helper raised errors in the game as it read its values:\n{Run(window)}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The file of Passenger's helper in the game, which an error it raises names.</summary>
    private const string GodotAgentFile = "passenger_agent.gd";

    private static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    /// <summary>A window on the game's project with a file of its open, the game run headless.</summary>
    private MainWindow Started(string file)
    {
        MainWindow window = game.Open(Path.Combine(PathNames.Canonical(game.Folder), file), languageServer: false, godot: game.Homes);
        RunConfiguration generated = Generated(window, "Spinner");
        window.SetConfigurations([generated with { Name = "Spinner on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Spinner on Godot");
        Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
        return window;
    }

    /// <summary>The pointer rested on a line's hint, as it is drawn now.</summary>
    private static void Rest(MainWindow window, CodeEditor editor, OpenDocument script, int line)
    {
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card gone as the pointer left the editor");
        Rect box = default;
        Ui.WaitFor(() => Hint(script, line) is { } hint && editor.AnnotationBox(line, hint) is { } drawn && (box = drawn) != default, () => $"the hint drawn at line {line + 1}");
        window.MouseMove(editor.TranslatePoint(box.Center, window)!.Value, RawInputModifiers.None);
    }

    /// <summary>A line's (from zero) hint as the file shows it, or null.</summary>
    private static string? Hint(OpenDocument document, int line) => document.LiveValues.Where(a => a.Line == line).Select(a => a.Text).FirstOrDefault();

    /// <summary>The frame count a hint says, or null for none.</summary>
    private static long? Count(OpenDocument document, int line) =>
        Hint(document, line) is { } hint && hint.StartsWith("frames: ", StringComparison.Ordinal) && long.TryParse(hint["frames: ".Length..], out long count) ? count : null;

    private static string Hints(OpenDocument document) => string.Join("; ", document.LiveValues.Select(a => $"line {a.Line + 1}: {a.Text}"));

    /// <summary>What the file shows, why the game's values were last not read, and the end of the game's console.</summary>
    private static string Unread(MainWindow window, OpenDocument document) =>
        $"{Hints(document)}; the last read: {window.GodotValues.Unread ?? "answered"}; the game said: {string.Join(" | ", Run(window).Split('\n').TakeLast(12))}";

    /// <summary>The line (from zero) of a file of the game's that holds some code.</summary>
    private int LineOf(string file, string code) =>
        Array.FindIndex(File.ReadAllText(Path.Combine(game.Folder, file)).Split('\n'), l => l.Contains(code, StringComparison.Ordinal));

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

    /// <summary>The Godot 4 C# project of Main and two enemies, restored from the Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-live-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            if (Godot is { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                GodotTests.Game.Make(Folder, version, packages, Script, Scene);
                File.WriteAllText(Path.Combine(Folder, "Enemy.cs"), Enemy);
                File.WriteAllText(Path.Combine(Folder, "spark.gd"), Spark);
            }
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot run a C# game: {Godot.WhyNot}";

        public IReadOnlyDictionary<string, string> Homes { get; }

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
