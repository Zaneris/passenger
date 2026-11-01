using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 game's C# read against its scenes, as a person works on it: a GetNode path no node of the scene
/// answers, or whose node is not of the type asked for, is an error naming the scene and its line; completion in a
/// GetNode string offers the scene's nodes, and in a res:// string the project's files, which Go to Declaration opens;
/// an exported property's card says what the scene sets it to; Rename on a node's name in a GetNode path renames it in the
/// scene and in every path of the code, one step of Undo. Its project (Godot.NET.Sdk) is restored from the packages the
/// Godot found carries, so without a Godot 4 .NET build each test skips saying why; no Godot runs.</summary>
public sealed class GodotSceneTests : IClassFixture<GodotSceneTests.Game>
{
    private readonly Game game;

    public GodotSceneTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is not { WhyNot: null, Version: not null, Packages: not null }, game.Skipped);
        game.Reset();
    }

    internal const string Script = """
        using Godot;

        public partial class Main : Node2D
        {
            [Export]
            public float Speed { get; set; } = 1;

            public override void _Ready()
            {
                Label score = GetNode<Label>("Hud/Score");
                Button pressed = GetNode<Button>("Hud/Score");
                Label lost = GetNode<Label>("Hud/Scor");
                Label unique = GetNode<Label>("%Score");
                Button start = GetNode<Button>("Hud/Start");
                Node2D sprite = GetNode<Node2D>("Player/Sprite");
                PackedScene player = GD.Load<PackedScene>("res://player.tscn");
                PackedScene gone = GD.Load<PackedScene>("res://gone.tscn");
                Callable started = new(this, MethodName.OnStartPressed);
            }

            private void OnStartPressed()
            {
            }

            private void OnToggled(bool on)
            {
            }
        }

        """;

    internal const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]
        [ext_resource type="PackedScene" path="res://player.tscn" id="2_player"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")
        Speed = 2.5

        [node name="Hud" type="CanvasLayer" parent="."]

        [node name="Score" type="Label" parent="Hud"]
        unique_name_in_owner = true

        [node name="Start" type="Button" parent="Hud"]

        [node name="Player" parent="." instance=ExtResource("2_player")]

        [connection signal="pressed" from="Hud/Start" to="." method="OnStartPressed"]
        [connection signal="pressed" from="Hud/Start" to="." method="OnQuitPressed"]
        [connection signal="toggled" from="Hud/Start" to="." method="OnStartPressed"]

        """;

    internal const string Player = """
        [gd_scene format=3]

        [node name="Player" type="CharacterBody2D"]

        [node name="Sprite" type="Sprite2D" parent="."]

        """;

    private string ScriptPath => game.PathOf("Main.cs");

    private string ScenePath => game.PathOf("main.tscn");

    /// <summary>The line (from one) of a scene's text that a piece of it is on.</summary>
    internal static int LineOf(string text, string piece) => Array.FindIndex(text.Split('\n'), l => l.Contains(piece, StringComparison.Ordinal)) + 1;

    /// <summary>The errors Passenger's Godot check marks in the shown file.</summary>
    internal static List<string> Marked(MainWindow window) =>
        [.. window.EditorArea.Active!.Markers.Where(m => m.Kind == MarkerKind.Error && !m.Message.StartsWith("CS", StringComparison.Ordinal)).Select(m => m.Message)];

    /// <summary>A path GetNode is given that the scene has no node of, and one whose node is a Label asked for as a Button, are
    /// errors where the path is written, naming the scene and its line; a res:// path no file answers is an error too, and the
    /// paths the scene answers (a child's, a unique name's, an instanced scene's node) and the file there are not marked.</summary>
    [AvaloniaFact]
    public void A_wrong_node_path_or_type_in_GetNode_is_an_error_naming_the_scene_and_its_line()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            string missing = $"main.tscn:{LineOf(Scene, "[node name=\"Main\"")} has no node 'Hud/Scor' under Main: Hud has no child Scor";
            string typed = $"'Hud/Score' is a Label in main.tscn:{LineOf(Scene, "[node name=\"Score\"")}, not a Button";
            string gone = "No file res://gone.tscn is in the Godot project";
            Ui.WaitFor(() => Marked(window).Contains(missing) && Marked(window).Contains(typed) && Marked(window).Contains(gone),
                () => $"the wrong path, the wrong type and the missing file to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            TextSnapshot text = window.EditorArea.Active!.Text.Snapshot;
            Marker marker = window.EditorArea.Active.Markers.First(m => m.Message == missing);
            Assert.Equal("Hud/Scor", text.GetText(marker.Range.Start, marker.Range.Length));
            Assert.DoesNotContain(Marked(window), m => m.Contains("'%Score'", StringComparison.Ordinal) || m.Contains("Hud/Start", StringComparison.Ordinal) && !m.Contains("connects", StringComparison.Ordinal)
                || m.Contains("Player/Sprite", StringComparison.Ordinal) || m.Contains("player.tscn is", StringComparison.Ordinal));

            string card = Hover(window, "Speed { get", "is set in");
            Assert.Contains($"main.tscn:{LineOf(Scene, "Speed = 2.5")}, Main: 2.5", card, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Completion in the string GetNode&lt;Label&gt; is given offers the scene's labels by their paths from the script's
    /// node, its unique one by its %name, and no button; in a res:// string it offers the project's files, and Go to Declaration
    /// from one opens its file.</summary>
    [AvaloniaFact]
    public void Node_paths_and_res_paths_complete_from_the_scenes_and_a_res_path_leads_to_its_file()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "GetNode<Label>(\"Hud/Sc");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Hud/Score"), () => $"the scene's labels to be offered: {string.Join(", ", window.Assist.CompletionLabels)}");
            Assert.DoesNotContain("Hud/Start", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(editor, "GetNode<Label>(\"%Sc");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("%Score"), () => $"the unique label to be offered: {string.Join(", ", window.Assist.CompletionLabels)}");
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(editor, "\"res://pl");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("res://player.tscn"), () => $"the project's files to be offered: {string.Join(", ", window.Assist.CompletionLabels)}");
            Assert.DoesNotContain(window.Assist.CompletionLabels, l => l.Contains(".godot", StringComparison.Ordinal));
            Ui.Press(window, "ESCAPE");

            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "player.tscn", () => $"Go to Declaration to open player.tscn ({window.Status})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Rename on Score in a GetNode path renames the node in the scene, the nodes and connections under it following,
    /// and every path of the code naming it, its %name too, as one command: Undo puts the scene and the code back.</summary>
    [AvaloniaFact]
    public void Renaming_a_node_from_its_path_in_the_code_renames_it_in_the_scene_and_the_code_one_step_of_undo()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "GetNode<Label>(\"Hud/Sc");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Score", () => $"Rename's box on the node Score ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("Points");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Status?.StartsWith("Renamed Score to Points", StringComparison.Ordinal) == true, () => $"the node's rename to be made ({window.Status})");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("[node name=\"Points\" type=\"Label\" parent=\"Hud\"]", StringComparison.Ordinal),
                () => $"the scene to name the node Points: {File.ReadAllText(ScenePath)}");
            string code = editor.Document.Snapshot.ToString();
            Assert.Contains("GetNode<Label>(\"Hud/Points\")", code, StringComparison.Ordinal);
            Assert.Contains("GetNode<Button>(\"Hud/Points\")", code, StringComparison.Ordinal);
            Assert.Contains("GetNode<Label>(\"%Points\")", code, StringComparison.Ordinal);
            Assert.Contains("GetNode<Button>(\"Hud/Start\")", code, StringComparison.Ordinal);

            window.Confirm = (_, _, _) => Task.FromResult(true);
            editor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(ScenePath) == Scene, () => $"Undo to put the scene back ({window.Status}): {File.ReadAllText(ScenePath)}");
            Assert.Equal(Script, editor.Document.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The scene's connection to a method Main lacks is an error on Main's name, and one whose signal gives an argument
    /// the handler does not take is one on the handler's, each naming the scene and its line; Find Usages of the handler lists
    /// the connection calling it, and Go to Declaration from that connection's method in the scene goes back to the handler, as
    /// it does from the name Godot's source generator writes for it.</summary>
    [AvaloniaFact]
    public void A_signals_connection_and_its_handler_lead_to_each_other_and_one_to_a_missing_method_is_marked()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            string missing = $"main.tscn:{LineOf(Scene, "method=\"OnQuitPressed\"")} connects Hud/Start's pressed to OnQuitPressed, which Main has no method of";
            string wrong = $"main.tscn:{LineOf(Scene, "signal=\"toggled\"")} connects Hud/Start's toggled, which gives 1 (bool), to OnStartPressed, which takes none";
            Ui.WaitFor(() => Marked(window).Contains(missing) && Marked(window).Contains(wrong),
                () => $"the connections to a missing method and to one of other parameters to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            TextSnapshot text = window.EditorArea.Active!.Text.Snapshot;
            Marker onClass = window.EditorArea.Active.Markers.First(m => m.Message == missing);
            Assert.Equal("Main", text.GetText(onClass.Range.Start, onClass.Range.Length));
            Marker onMethod = window.EditorArea.Active.Markers.First(m => m.Message == wrong);
            Assert.Equal("OnStartPressed", text.GetText(onMethod.Range.Start, onMethod.Range.Length));

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "void OnStartPr");
            Ui.Run(window, AppActions.FindUsages);
            int connected = LineOf(Scene, "method=\"OnStartPressed\"") - 1;
            Ui.WaitFor(() => window.Find.Rows.Any(r => r.Usage is { } u && u.Path.EndsWith("main.tscn", StringComparison.Ordinal) && u.Line == connected),
                () => $"Find Usages of the handler to list the connection calling it ({window.Status}): {string.Join("; ", window.Find.Rows.Select(r => r.Usage).OfType<Usage>().Select(u => $"{u.Path}:{u.Line + 1}"))}");

            window.OpenFile(ScenePath);
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "method=\"OnStartPr");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Main.cs", () => $"Go to Declaration from the connection's method to open Main.cs ({window.Status})");
            CodeEditor code = window.EditorArea.CodeEditor;
            Assert.Contains("private void OnStartPressed()", code.Document.Snapshot.GetLineText(code.CaretPosition.Line), StringComparison.Ordinal);

            // The name Godot's source generator writes for the handler leads to the handler, not to the generator's code.
            Ui.PlaceAfter(code, "MethodName.OnStartPr");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Main.cs" && code.Document.Snapshot.GetLineText(code.CaretPosition.Line).Contains("private void OnStartPressed()", StringComparison.Ordinal),
                () => $"Go to Declaration from MethodName.OnStartPressed to go to the handler ({window.EditorArea.Active?.Name}: {code.Document.Snapshot.GetLineText(code.CaretPosition.Line)}; {window.Status})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Rename of the handler in the code renames the method the scene's connections call, one step of Undo with the code's.</summary>
    [AvaloniaFact]
    public void Renaming_a_handler_renames_the_method_its_scenes_connections_call()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "void OnStartPr");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "OnStartPressed", () => $"Rename's box on the handler ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("OnPlayPressed");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("method=\"OnPlayPressed\"", StringComparison.Ordinal),
                () => $"the scene's connections to call the renamed handler ({window.Status}): {File.ReadAllText(ScenePath)}", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain("OnStartPressed", File.ReadAllText(ScenePath), StringComparison.Ordinal);
            Assert.Contains("private void OnPlayPressed()", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

            window.Confirm = (_, _, _) => Task.FromResult(true);
            editor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(ScenePath) == Scene, () => $"Undo to put the scene's connections back ({window.Status}): {File.ReadAllText(ScenePath)}");
            Assert.Contains("private void OnStartPressed()", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The card the pointer resting on a piece of the shown file brings, once it says <paramref name="until"/>.</summary>
    internal static string Hover(MainWindow window, string text, string until)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Press(window, "ESCAPE");
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText?.Contains(until, StringComparison.Ordinal) == true, () => $"the card on {text}: {window.Assist.HoverText}");
        return window.Assist.HoverText!;
    }

    /// <summary>A Godot 4 C# project of a main scene, its HUD's nodes and an instanced player scene, on the Godot found: its
    /// Godot.NET.Sdk the version that Godot carries, restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-scenes-")
        {
            Godot = GodotExecutables.Find(null);
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.WriteAllText(PathOf("player.tscn"), Player);
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
