using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 game's C# values as a game developer reads them at a stop in its script, in Variables and in the
/// debugger's hover card. Godot runs headless, its user folders the run's. Without a Godot 4 .NET build each test skips
/// saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotValuesTests : IClassFixture<GodotValuesTests.Game>
{
    private readonly Game game;

    public GodotValuesTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is null || game.Godot.WhyNot is not null, game.Skipped);
        game.Reset();
    }

    /// <summary>The script: each frame makes a value of each kind, the breakpoint's line using them all.</summary>
    private const string Script = """
        using Godot;

        public partial class Main : Node2D
        {
            private int frames;

            [Export]
            public float Speed { get; set; } = 3.5f;

            [Export]
            private Color tint = new(1f, 0.5f, 0.25f, 1f);

            public override void _Process(double delta)
            {
                frames++;
                Vector2 position = new(1.5f, -2f);
                Vector2I cell = new(3, 4);
                Vector3 up = new(0f, 1f, 0f);
                Vector3I voxel = new(1, 2, 3);
                Vector4 four = new(1f, 2f, 3f, 4f);
                Rect2 box = new(10f, 20f, 30f, 40f);
                Rect2I area = new(1, 2, 3, 4);
                Transform2D turn = new(0f, new Vector2(7f, 8f));
                Transform3D placed = new(Basis.Identity, new Vector3(1f, 2f, 3f));
                Basis basis = Basis.Identity;
                Quaternion spin = Quaternion.Identity;
                Aabb bounds = new(new Vector3(0f, 0f, 0f), new Vector3(1f, 2f, 3f));
                Plane floor = new(Vector3.Up, 2f);
                Color color = new(0.2f, 0.4f, 0.6f, 0.8f);
                Godot.Collections.Array list = [1, "two", new Vector2(3f, 4f)];
                Godot.Collections.Array<int> numbers = [5, 6, 7];
                Godot.Collections.Dictionary table = new() { ["hp"] = 10, ["name"] = "Bob" };
                Godot.Collections.Dictionary<string, int> scores = new() { ["ann"] = 3, ["bo"] = 4 };
                StringName action = "jump";
                NodePath path = "Player/Gun";
                Node player = GetNode("Player");
                Variant held = 42;
                GD.Print($"{position} {cell} {up} {voxel} {four} {box} {area} {turn} {placed} {basis} {spin} {bounds} {floor} {color} {list} {numbers} {table} {scores} {action} {path} {player} {held}");
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1")

        [node name="Player" type="Node2D" parent="."]
        position = Vector2(5, 6)

        [node name="Gun" type="Node2D" parent="Player"]

        [node name="Camera" type="Camera2D" parent="."]

        """;

    private string ScriptPath => Path.Combine(PathNames.Canonical(game.Folder), "Main.cs");

    /// <summary>Stopped in the script, each kind of Godot value reads in Variables as its fields allow without running the
    /// game's code, and as the hover card shows it.</summary>
    [AvaloniaFact]
    public void Godots_values_read_in_Variables_and_the_hover_card_as_a_game_developer_reads_them()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            RunConfiguration generated = Generated(window, "Kinds");
            window.SetConfigurations([generated with { Name = "Kinds on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Kinds on Godot");
            CodeEditor editor = window.EditorArea.CodeEditor;
            int line = LineOf(editor, "GD.Print(");
            editor.GoTo(new TextPosition(line, 9));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the game's build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Row(window, "held") is not null,
                () => $"the game to stop at the breakpoint in _Process ({window.Debugger.Session?.State}, {Ui.Said(window)}; {window.Debugger.Output()})", TimeSpan.FromMinutes(1));
            (string Name, string Value)[] expected =
            [
                ("position", "(1.5, -2)"), ("cell", "(3, 4)"), ("up", "(0, 1, 0)"), ("voxel", "(1, 2, 3)"), ("four", "(1, 2, 3, 4)"),
                ("box", "[P: (10, 20), S: (30, 40)]"), ("area", "[P: (1, 2), S: (3, 4)]"), ("turn", "[X: (1, 0), Y: (-0, 1), O: (7, 8)]"),
                ("placed", "[X: (1, 0, 0), Y: (0, 1, 0), Z: (0, 0, 1), O: (1, 2, 3)]"), ("basis", "[X: (1, 0, 0), Y: (0, 1, 0), Z: (0, 0, 1)]"),
                ("spin", "(0, 0, 0, 1)"), ("bounds", "[P: (0, 0, 0), S: (1, 2, 3)]"), ("floor", "[N: (0, 1, 0), D: 2]"), ("color", "(0.2, 0.4, 0.6, 0.8)"),
                ("list", "Count = 3"), ("table", "Count = 2"), ("action", "\"jump\""), ("path", "\"Player/Gun\""), ("player", "Player (/root/Main/Player)"),
                ("this", "Main (/root/Main)"), ("held", "42"),
            ];
            string[] wrong = [.. expected.Where(e => Row(window, e.Name)?.Value != e.Value).Select(e => $"{e.Name} = {Row(window, e.Name)?.Value} where {e.Value} was to be")];
            Assert.True(wrong.Length == 0, $"Godot's values read wrong in Variables: {string.Join("; ", wrong)}");
            Assert.Equal(Avalonia.Media.Color.FromArgb(204, 51, 102, 153), Row(window, "color")!.Swatch);

            Assert.Equal(["[0] = 1", "[1] = \"two\"", "[2] = (3, 4)", "Raw View = "], Opened(window, "list"));
            Assert.Equal(["[\"hp\"] = 10", "[\"name\"] = \"Bob\"", "Raw View = "], Opened(window, "table"));
            Assert.Equal(["[\"ann\"] = 3", "[\"bo\"] = 4", "Raw View = "], Opened(window, "scores"));
            // A typed dictionary's rows name their values' type, not the view's item type.
            Assert.Equal(["int", "int"], Row(window, "scores")!.Children!.Take(2).Select(r => r.Type));
            Assert.Equal(["Raw View = "], Opened(window, "action"));
            Assert.Equal(["Children = Count = 1", "Raw View = "], Opened(window, "player"));
            // The script's own members, its exported ones first, then the node's children, Godot's own members under Raw View.
            Assert.Equal(["tint = (1, 0.5, 0.25, 1)", "Speed = 3.5", "frames = 1", "Children = Count = 2", "Raw View = "], Opened(window, "this"));
            Assert.Equal(["[0] = Player (/root/Main/Player)", "[1] = Camera (/root/Main/Camera)", "Raw View = "], Opened(window, "this", "Children"));

            foreach (string word in (string[])["position", "color", "player"])
            {
                string value = expected.Single(e => e.Name == word).Value;
                editor.Selections = SelectionSet.Single(editor.Document.Snapshot.ToString().IndexOf($"{{{word}}}", StringComparison.Ordinal) + 2);
                Ui.Run(window, AppActions.QuickDocumentation);
                Ui.WaitFor(() => window.Assist.HoverValues is { Tree.Rows.Count: > 0 }, $"the hover card on {word}");
                VariableNode hovered = window.Assist.HoverValues!.Tree.Rows[0];
                Assert.True(hovered.Value == value && (word != "color" || hovered.Swatch is not null),
                    $"The hover card on {word} shows {hovered.Value}{(hovered.Swatch is null ? "" : " with a swatch")}, where {value} was to be");
                Ui.Press(window, "Escape");
                Ui.WaitFor(() => window.Assist.HoverValues is null, $"the hover card on {word} to go");
            }
        }
        finally
        {
            End(window);
        }
    }

    private static VariableNode? Row(MainWindow window, string name) =>
        window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name);

    /// <summary>The rows a local opens to, through the rows named after it, each as its name and value.</summary>
    private static string[] Opened(MainWindow window, params string[] path)
    {
        VariableNode row = Row(window, path[0]) ?? throw new InvalidOperationException($"The frame lists no {path[0]}");
        for (int at = 0; at < path.Length; at++)
        {
            VariableNode opening = row;
            Task opened = window.BottomPanel.Debug.Tree.Expand(opening);
            Ui.WaitFor(() => opened.IsCompleted, $"{opening.Name}'s rows", TimeSpan.FromMinutes(1));
            if (at + 1 < path.Length)
            {
                row = opening.Children?.FirstOrDefault(c => c.Name == path[at + 1])
                    ?? throw new InvalidOperationException($"{opening.Name} lists {string.Join(", ", (opening.Children ?? []).Select(c => c.Name))}, without {path[at + 1]}");
            }
        }
        return [.. (row.Children ?? []).Select(c => $"{c.Name} = {c.Value}")];
    }

    private static RunConfiguration Generated(MainWindow window, string name)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == name),
            () => $"the Godot project's configuration {name} ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        return window.Configurations.Single(c => c.Generated && c.Name == name);
    }

    private static int LineOf(CodeEditor editor, string code)
    {
        string[] lines = editor.Document.Snapshot.ToString().Split('\n');
        int index = Array.FindIndex(lines, l => l.Contains(code, StringComparison.Ordinal));
        Assert.True(index >= 0, $"The script has no line of {code}");
        return index + 1;
    }

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the game to end ({window.Status})");
        }
        window.Close();
    }

    /// <summary>A Godot 4 C# project named Kinds of one scene, its root's script <see cref="Script"/>, on the Godot found,
    /// restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-values-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            string project = File.ReadAllText(PathOf("project.godot")).Replace("\"Spinner\"", "\"Kinds\"", StringComparison.Ordinal);
            File.WriteAllText(PathOf("project.godot"), project);
            File.Move(PathOf("Spinner.csproj"), PathOf("Kinds.csproj"));
        }

        /// <summary>The Godot found, or null.</summary>
        public GodotFound? Godot { get; }

        /// <summary>Why each test skips, where it does.</summary>
        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot run a C# game: {Godot.WhyNot}";

        /// <summary>The variables that give Godot user folders of the run's.</summary>
        public IReadOnlyDictionary<string, string> Homes { get; }

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
