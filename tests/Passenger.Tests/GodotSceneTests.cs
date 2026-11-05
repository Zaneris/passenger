using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.Run;
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
            [Signal]
            public delegate void ScoredEventHandler(int points);

            [Export]
            public float Speed { get; set; } = 1;

            [Export]
            public Label Display { get; set; }

            [Export]
            public NodePath Target { get; set; }

            public override void _Ready()
            {
                Position = new Vector2(Speed, 0);
                Label score = GetNode<Label>("Hud/Score");
                Button pressed = GetNode<Button>("Hud/Score");
                Label lost = GetNode<Label>("Hud/Scor");
                Label unique = GetNode<Label>("%Score");
                Button start = GetNode<Button>("Hud/Start");
                start.Pressed += OnStartPressed;
                Node2D sprite = GetNode<Node2D>("Player/Sprite");
                PackedScene player = GD.Load<PackedScene>("res://player.tscn");
                PackedScene gone = GD.Load<PackedScene>("res://gone.tscn");
                PackedScene byUid = GD.Load<PackedScene>("uid://bplayer1");
                Callable started = new(this, MethodName.OnStartPressed);
                Scored += OnScored;
                EmitSignal(SignalName.Scored, 1);
                EmitSignalScored(2);
                EmitSignal("Scored", 3);
                Connect(SignalName.Scored, new Callable(this, "OnScord"));
                Connect("Scored", new Callable(this, "OnStartPressed"));
                EmitSignal("Scord");
            }

            private void OnScored(int points)
            {
            }

            private void OnStartPressed()
            {
            }

            private void OnToggled(bool on)
            {
            }
        }

        """;

    /// <summary>The script of the scene's HUD, which follows Main's signal through its event and its name.</summary>
    internal const string HudScript = """
        using Godot;

        public partial class Hud : CanvasLayer
        {
            public override void _Ready()
            {
                Main main = GetParent<Main>();
                main.Scored += points => GetNode<Label>("Score").Text = points.ToString();
                bool connected = main.IsConnected(Main.SignalName.Scored, new Callable(this, MethodName.OnScored));
            }

            private void OnScored(int points)
            {
            }
        }

        """;

    internal const string Scene = """
        [gd_scene load_steps=4 format=3]

        [ext_resource type="Script" uid="uid://cmain1" path="res://Main.cs" id="1_main"]
        [ext_resource type="PackedScene" path="res://player.tscn" id="2_player"]
        [ext_resource type="Script" path="res://Hud.cs" id="3_hud"]

        [sub_resource type="Theme" id="Theme_1"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")
        Speed = 2.5
        Display = NodePath("Player")
        Target = NodePath("Hud/Gone")

        [node name="Hud" type="CanvasLayer" parent="."]
        script = ExtResource("3_hud")

        [node name="Score" type="Label" parent="Hud"]
        unique_name_in_owner = true

        [node name="Start" type="Button" parent="Hud"]
        theme = SubResource("Theme_1")

        [node name="Player" parent="." instance=ExtResource("2_player")]

        [connection signal="pressed" from="Hud/Start" to="." method="OnStartPressed"]
        [connection signal="pressed" from="Hud/Start" to="." method="OnQuitPressed"]
        [connection signal="toggled" from="Hud/Start" to="." method="OnStartPressed"]
        [connection signal="Scored" from="." to="Hud" method="OnScored"]

        """;

    internal const string Player = """
        [gd_scene format=3 uid="uid://bplayer1"]

        [node name="Player" type="CharacterBody2D"]

        [node name="Sprite" type="Sprite2D" parent="."]

        """;

    private string ScriptPath => game.PathOf("Main.cs");

    private string ScenePath => game.PathOf("main.tscn");

    private string HudPath => game.PathOf("Hud.cs");

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

    /// <summary>A node a GDScript of the same scene names follows a rename begun in C#: its $ path and its get_node string, one
    /// step of Undo with the rest, or that script no longer finds its node.</summary>
    [AvaloniaFact]
    public void Renaming_a_node_from_the_csharp_also_renames_it_in_the_gdscripts_of_its_scenes()
    {
        const string gdscript = "extends CanvasLayer\n\n@onready var score = $Score\n\n\nfunc _ready():\n\tget_node(\"Score\").hide()\n";
        string script = game.PathOf("hud.gd");
        File.WriteAllText(script, gdscript);
        File.WriteAllText(ScenePath, Scene.Replace("res://Hud.cs", "res://hud.gd", StringComparison.Ordinal));
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
            Ui.WaitFor(() => File.ReadAllText(script) == gdscript.Replace("Score", "Points", StringComparison.Ordinal),
                () => $"the GDScript's paths to follow the node ({window.Status}): {File.ReadAllText(script)}");
        }
        finally
        {
            window.Close();
            File.Delete(script);
        }
    }

    /// <summary>A node's name may hold a backslash: the code's strings take it escaped as C# escapes it, the scene's as Godot does,
    /// so the string still says the name and the scene still loads it.</summary>
    [AvaloniaFact]
    public void Renaming_a_node_to_a_name_holding_a_backslash_writes_it_for_each_file_it_goes_in()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "GetNode<Label>(\"Hud/Sc");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Score", () => $"Rename's box on the node Score ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("Sc\\ore");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("[node name=\"Sc\\\\ore\" type=\"Label\" parent=\"Hud\"]", StringComparison.Ordinal),
                () => $"the scene to name the node as Godot writes it ({window.Status}): {File.ReadAllText(ScenePath)}");
            Assert.Contains("GetNode<Label>(\"Hud/Sc\\\\ore\")", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
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

    /// <summary>A string Godot's calls take naming a method or a signal Main lacks is an error where it is written, as is a
    /// method connected by strings to a signal it takes other arguments than; Go to Declaration from a string naming a signal
    /// goes to its delegate, and from one naming a method to the method.</summary>
    [AvaloniaFact]
    public void A_string_named_connection_to_a_missing_method_is_marked_and_its_strings_lead_to_what_they_name()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            string method = "Main has no method 'OnScord'";
            string signal = "Main has no signal 'Scord'";
            string wrong = "'Scored', which gives 1 (int), is connected to 'OnStartPressed', which takes none";
            Ui.WaitFor(() => Marked(window).Contains(method) && Marked(window).Contains(signal) && Marked(window).Contains(wrong),
                () => $"the strings naming a missing method and signal, and the connection of other arguments, to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            TextSnapshot text = window.EditorArea.Active!.Text.Snapshot;
            Marker missing = window.EditorArea.Active.Markers.First(m => m.Message == method);
            Assert.Equal("OnScord", text.GetText(missing.Range.Start, missing.Range.Length));
            Marker connected = window.EditorArea.Active.Markers.First(m => m.Message == wrong);
            Assert.Equal("OnStartPressed", text.GetText(connected.Range.Start, connected.Range.Length));
            Assert.DoesNotContain(Marked(window), m => m.Contains("'Scored' takes", StringComparison.Ordinal));

            CodeEditor code = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(code, "Connect(\"Scor");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => code.Document.Snapshot.GetLineText(code.CaretPosition.Line).Contains("delegate void ScoredEventHandler", StringComparison.Ordinal),
                () => $"Go to Declaration from the signal's string to go to its delegate ({code.Document.Snapshot.GetLineText(code.CaretPosition.Line)}; {window.Status})");
            Ui.PlaceAfter(code, "new Callable(this, \"OnStartPr");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => code.Document.Snapshot.GetLineText(code.CaretPosition.Line).Contains("private void OnStartPressed()", StringComparison.Ordinal),
                () => $"Go to Declaration from the method's string to go to the method ({code.Document.Snapshot.GetLineText(code.CaretPosition.Line)}; {window.Status})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An open scene is marked where Godot would not load what it says: its connection to a method Main lacks, and
    /// the one of other arguments than the handler takes, where it writes them; and an external resource whose file is not
    /// there, once typed so and the typing pauses. The HUD's connection, which holds, is not marked.</summary>
    [AvaloniaFact]
    public void An_open_scene_marks_a_missing_resource_and_a_broken_connection()
    {
        MainWindow window = game.Open(ScenePath);
        try
        {
            string method = "Main, a Node2D (Main.cs), has no method OnQuitPressed";
            string wrong = "toggled gives 1 (bool), but OnStartPressed takes none";
            Ui.WaitFor(() => Marked(window).Contains(method) && Marked(window).Contains(wrong),
                () => $"the scene's connections to a missing method and of other arguments to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            TextSnapshot text = window.EditorArea.Active!.Text.Snapshot;
            Marker missing = window.EditorArea.Active.Markers.First(m => m.Message == method);
            Assert.Equal("OnQuitPressed", text.GetText(missing.Range.Start, missing.Range.Length));
            Assert.DoesNotContain(Marked(window), m => m.Contains("Scored", StringComparison.Ordinal) || m.Contains("OnScored", StringComparison.Ordinal));

            Typing.Replace(window, "res://player.tscn", "res://plaer.tscn");
            string gone = "No file res://plaer.tscn is in the Godot project";
            Ui.WaitFor(() => Marked(window).Contains(gone), () => $"the external resource of no file to be marked: {string.Join("; ", Marked(window))}");
            text = window.EditorArea.Active.Text.Snapshot;
            Marker resource = window.EditorArea.Active.Markers.First(m => m.Message == gone);
            Assert.Equal("res://plaer.tscn", text.GetText(resource.Range.Start, resource.Range.Length));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An exported node the scene sets to a node of another type, and an exported NodePath it sets to a path no node
    /// answers, are errors on the member's name naming the scene and its line.</summary>
    [AvaloniaFact]
    public void An_exported_node_set_to_a_node_of_another_type_or_to_no_node_is_marked()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            string typed = $"main.tscn:{LineOf(Scene, "Display = ")} sets Display to 'Player', a CharacterBody2D, not a Label";
            string missing = $"main.tscn:{LineOf(Scene, "Target = ")} sets Target to 'Hud/Gone', which names no node: Hud has no child Gone";
            Ui.WaitFor(() => Marked(window).Contains(typed) && Marked(window).Contains(missing),
                () => $"the exported node of another type and the NodePath of no node to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            TextSnapshot text = window.EditorArea.Active!.Text.Snapshot;
            Marker marker = window.EditorArea.Active.Markers.First(m => m.Message == typed);
            Assert.Equal("Display", text.GetText(marker.Range.Start, marker.Range.Length));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An open scene marks where it sets an exported node to a node of another type than the script's member, or a
    /// NodePath to no node, at the path, as the script's file marks it at the member; and a SubResource no sub_resource of the
    /// file has, once typed so, where the one it has is not marked.</summary>
    [AvaloniaFact]
    public void An_open_scene_marks_a_node_path_set_to_a_node_of_another_type_or_to_none_and_an_unknown_sub_resource()
    {
        MainWindow window = game.Open(ScenePath);
        try
        {
            string typed = "Main's Display takes a Label: 'Player' is a CharacterBody2D";
            string missing = "Main's Target names no node: Hud has no child Gone";
            Ui.WaitFor(() => Marked(window).Contains(typed) && Marked(window).Contains(missing),
                () => $"the exported node of another type and the NodePath of no node to be marked in the scene: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            TextSnapshot text = window.EditorArea.Active!.Text.Snapshot;
            Marker onType = window.EditorArea.Active.Markers.First(m => m.Message == typed);
            Marker onPath = window.EditorArea.Active.Markers.First(m => m.Message == missing);
            Assert.Equal(("Player", "Hud/Gone"), (text.GetText(onType.Range.Start, onType.Range.Length), text.GetText(onPath.Range.Start, onPath.Range.Length)));
            Assert.DoesNotContain(Marked(window), m => m.Contains("sub_resource", StringComparison.Ordinal));

            CodeEditor editor = window.EditorArea.CodeEditor;
            int at = text.ToString().IndexOf("Theme_1\")", StringComparison.Ordinal) + "Theme_".Length;
            editor.Focus();
            editor.Select(new TextRange(at, 1));
            window.KeyTextInput("2");
            string unknown = "No sub_resource of this file has the id \"Theme_2\"";
            Ui.WaitFor(() => Marked(window).Contains(unknown), () => $"the SubResource of no sub_resource to be marked: {string.Join("; ", Marked(window))}");
            text = window.EditorArea.Active.Text.Snapshot;
            Marker onId = window.EditorArea.Active.Markers.First(m => m.Message == unknown);
            Assert.Equal("Theme_2", text.GetText(onId.Range.Start, onId.Range.Length));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A handler taken out of its script on disk while the script is closed, by another program, marks the open scene's
    /// connections to it, once the language server has read the script so.</summary>
    [AvaloniaFact]
    public void An_open_scene_marks_a_handler_taken_out_of_its_closed_script_on_disk()
    {
        MainWindow window = game.Open(ScenePath);
        try
        {
            string wrong = "toggled gives 1 (bool), but OnStartPressed takes none";
            Ui.WaitFor(() => Marked(window).Contains(wrong), () => $"the scene's connection of other arguments to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            string handled = "    private void OnStartPressed()\n    {\n    }\n\n";
            Assert.Contains(handled, Script, StringComparison.Ordinal);
            File.WriteAllText(ScriptPath, Script.Replace(handled, string.Empty, StringComparison.Ordinal));
            string gone = "Main, a Node2D (Main.cs), has no method OnStartPressed";
            Ui.WaitFor(() => Marked(window).Contains(gone) && !Marked(window).Contains(wrong),
                () => $"the scene's connections to the handler taken out of Main.cs to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            Assert.Equal(2, window.EditorArea.Active!.Markers.Count(m => m.Message == gone));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A script moved in the Explorer takes the .uid beside it along, and the open scene its path; the scene is
    /// checked against the script where it went (a connection of other arguments than its handler takes marked, no handler
    /// said missing), and the script, opened there, against the scene (a GetNode path no node answers).</summary>
    [AvaloniaFact]
    public void A_script_moved_with_its_uid_in_the_explorer_is_followed_by_its_scene_and_checked_where_it_went()
    {
        MainWindow window = game.Open(ScenePath);
        try
        {
            string wrong = "toggled gives 1 (bool), but OnStartPressed takes none";
            Ui.WaitFor(() => Marked(window).Contains(wrong), () => $"the scene's connection of other arguments to be marked: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Choose(window, ScriptPath);
            Ui.Run(window, EditorActions.Cut);
            Choose(window, game.PathOf("actors"));
            Ui.Run(window, EditorActions.Paste);
            string moved = game.PathOf(Path.Combine("actors", "Main.cs"));
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => File.Exists(moved) && editor.Document.Snapshot.ToString().Contains("path=\"res://actors/Main.cs\"", StringComparison.Ordinal),
                () => $"the scene to follow its script's move ({window.Status}): {editor.Document.Snapshot}");
            Assert.True(File.Exists(moved + ".uid") && !File.Exists(ScriptPath + ".uid"), "The script's .uid did not go with it to actors");
            Assert.Equal("uid://cmain1", File.ReadAllText(moved + ".uid").Trim());
            Ui.WaitFor(() => Marked(window).Contains(wrong) && !Marked(window).Any(m => m.Contains("has no method OnStartPressed", StringComparison.Ordinal)),
                () => $"the scene to be checked against the script where it went: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));

            window.OpenFile(moved);
            string lost = $"main.tscn:{LineOf(Scene, "[node name=\"Main\"")} has no node 'Hud/Scor' under Main: Hud has no child Scor";
            Ui.WaitFor(() => Marked(window).Contains(lost), () => $"the moved script to be checked against its scene: {string.Join("; ", Marked(window))}", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Context Help on Node2D's Position, which Main sets, browses the property's place on Node2D's page of Godot's
    /// class reference for the version of the Godot found; on Main's override of _Ready, Node's virtual _ready's place on
    /// Node's; on a signal Button's base class gives, its place there; on the project's own handler it says there is none.</summary>
    [AvaloniaFact]
    public void Context_help_browses_an_engine_members_place_in_godots_class_reference()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            string docs = $"https://docs.godotengine.org/en/{GodotDocs.VersionOf(game.Godot!.Version)}/classes/";
            foreach ((string at, string page) in new[]
            {
                ("        Posi", "class_node2d.html#class-node2d-property-position"),
                ("void _Rea", "class_node.html#class-node-private-method-ready"),
                ("GetNode<Bu", "class_button.html"),
                ("start.Pre", "class_basebutton.html#class-basebutton-signal-pressed"),
                ("GetNo", "class_node.html#class-node-method-get-node"),
            })
            {
                Ui.PlaceAfter(editor, at);
                Ui.Press(window, Ui.Shortcut(AppActions.ContextHelp));
                Uri wanted = new(docs + page);
                Ui.WaitFor(() => window.Browsed.Contains(wanted), () => $"{wanted} to be browsed from {at} ({window.Status}): {string.Join(", ", window.Browsed)}");
            }
            int browsed = window.Browsed.Count;
            Ui.PlaceAfter(editor, "void OnScor");
            Ui.Press(window, Ui.Shortcut(AppActions.ContextHelp));
            Ui.WaitFor(() => window.Status == Refusals.NoGodotDocumentation, () => $"Context Help on the project's own handler to say it has no page ({window.Status})");
            Assert.Equal(browsed, window.Browsed.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A uid:// in the code leads to the scene whose header gives it, its card saying which, and one in a scene's
    /// external resource to the script the .uid file beside it gives it to.</summary>
    [AvaloniaFact]
    public void A_uid_leads_to_its_file_from_the_code_and_from_a_scene()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            Hover(window, "uid://bplayer1", "uid://bplayer1 is res://player.tscn");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "\"uid://bpla");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "player.tscn", () => $"Go to Declaration from the uid to open player.tscn ({window.Status})");

            window.OpenFile(ScenePath);
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "uid=\"uid://cma");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Main.cs", () => $"Go to Declaration from the script's uid in the scene to open Main.cs ({window.Status})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A scene moved in the Explorer takes every res:// path naming it along, the external resource of the scene
    /// instancing it and the code's string, as one command Undo in the Explorer takes back whole.</summary>
    [AvaloniaFact]
    public void A_scene_moved_in_the_explorer_is_followed_by_every_res_path_naming_it()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor code = window.EditorArea.CodeEditor;
            Choose(window, game.PathOf("player.tscn"));
            Ui.Run(window, EditorActions.Cut);
            Choose(window, game.PathOf("actors"));
            Ui.Run(window, EditorActions.Paste);
            Ui.WaitFor(() => File.Exists(game.PathOf(Path.Combine("actors", "player.tscn"))) && File.ReadAllText(ScenePath).Contains("path=\"res://actors/player.tscn\"", StringComparison.Ordinal),
                () => $"the scene instancing the player to follow its move ({window.Status}): {File.ReadAllText(ScenePath)}");
            Assert.StartsWith("Moved player.tscn to actors", window.Status, StringComparison.Ordinal);
            Assert.Equal("Moved player.tscn to actors", window.Status);
            Assert.Contains("GD.Load<PackedScene>(\"res://actors/player.tscn\")", code.Document.Snapshot.ToString(), StringComparison.Ordinal);

            window.Confirm = (_, _, _) => Task.FromResult(true);
            Choose(window, game.PathOf("actors"));
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.Exists(game.PathOf("player.tscn")) && File.ReadAllText(ScenePath) == Scene,
                () => $"Undo to move the player back and put the scene back ({window.Status}): {File.ReadAllText(ScenePath)}");
            Assert.Contains("GD.Load<PackedScene>(\"res://player.tscn\")", code.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The Explorer's row of a file or folder chosen, with the keys.</summary>
    private static void Choose(MainWindow window, string path)
    {
        string shown = PathNames.Canonical(path);
        window.ExplorerView.Reveal(shown);
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Path is { } row && PathNames.Same(row, shown)),
            () => $"the Explorer to show {Path.GetFileName(path)}: {string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Path))}");
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path is { } row && PathNames.Same(row, shown)));
    }

    /// <summary>A res:// string in a scene completes from the project's files as its "res://" is typed, the scene itself and
    /// Godot's own folders left out, and the file chosen goes in whole in place of the string.</summary>
    [AvaloniaFact]
    public void A_res_path_in_a_scene_completes_from_the_projects_files()
    {
        MainWindow window = game.Open(ScenePath);
        try
        {
            Typing.Replace(window, "res://player.tscn", "res://");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("res://player.tscn") && window.Assist.CompletionLabels.Contains("res://Hud.cs"),
                () => $"the project's files to be offered: {string.Join(", ", window.Assist.CompletionLabels)}");
            Assert.DoesNotContain("res://main.tscn", window.Assist.CompletionLabels);
            Assert.DoesNotContain(window.Assist.CompletionLabels, l => l.StartsWith("res://.godot/", StringComparison.Ordinal));
            window.KeyTextInput("pla");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString().Contains("path=\"res://player.tscn\" id=\"2_player\"]", StringComparison.Ordinal),
                () => $"the chosen file to take the string's place: {window.EditorArea.CodeEditor.Document.Snapshot}");
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
            Assert.Contains("new(this, MethodName.OnPlayPressed)", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Contains("new Callable(this, \"OnPlayPressed\")", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

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

    /// <summary>An export set on a sub-resource running the script is its key too: the rename takes it with the node's, or the
    /// resource would load its default.</summary>
    [AvaloniaFact]
    public void Renaming_an_export_renames_its_key_on_a_sub_resource_running_the_script()
    {
        File.WriteAllText(ScenePath, Scene.Replace("[sub_resource type=\"Theme\" id=\"Theme_1\"]",
            "[sub_resource type=\"Theme\" id=\"Theme_1\"]\n\n[sub_resource type=\"Resource\" id=\"Held_1\"]\nscript = ExtResource(\"1_main\")\nSpeed = 9.0", StringComparison.Ordinal));
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "public float Spe");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Speed", () => $"Rename's box on the export ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("Power");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("Power = 2.5", StringComparison.Ordinal),
                () => $"the scene's keys to follow the export ({window.Status}): {File.ReadAllText(ScenePath)}", TimeSpan.FromMinutes(1));
            Assert.Contains("Power = 9.0", File.ReadAllText(ScenePath), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A scene saved a moment before a rename, the folder's watcher yet to tell of it (here, never to), holds its keys
    /// where the save put them: the rename changes the key it names, never whatever now lies where the key was read.</summary>
    [AvaloniaFact]
    public void Renaming_an_export_changes_a_scene_saved_a_moment_before_as_the_save_left_it()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            window.FolderWatcher!.Dispose();
            File.WriteAllText(ScenePath, Scene.Replace("Speed = 2.5", "Other = 1\nSpeed = 2.5", StringComparison.Ordinal));
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "public float Spe");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Speed", () => $"Rename's box on the export ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("Power");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("Power = 2.5", StringComparison.Ordinal),
                () => $"the scene's key to follow its export ({window.Status}): {File.ReadAllText(ScenePath)}", TimeSpan.FromMinutes(1));
            Assert.Contains("Other = 1", File.ReadAllText(ScenePath), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Find Usages of a signal's delegate lists its uses by what Godot's source generator makes of it and the scene's
    /// connection; its rename renames those wherever the code uses them (its event, its SignalName, its EmitSignal call) and
    /// the strings Godot's calls take naming it, in its own script and another, and the scene's connection to it: the game
    /// then builds with no error, and one Undo puts every file back.</summary>
    [AvaloniaFact]
    public void Renaming_a_signal_renames_its_event_SignalName_and_strings_in_every_script_and_the_game_builds()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "void Scor");
            Ui.Run(window, AppActions.FindUsages);
            int emitted = LineOf(Script, "EmitSignalScored(2)") - 1;
            Ui.WaitFor(() => window.Find.Rows.Any(r => r.Usage is { } u && u.Path.EndsWith("Hud.cs", StringComparison.Ordinal))
                && window.Find.Rows.Any(r => r.Usage is { } u && u.Path.EndsWith("Main.cs", StringComparison.Ordinal) && u.Line == emitted)
                && window.Find.Rows.Any(r => r.Usage is { } u && u.Path.EndsWith("main.tscn", StringComparison.Ordinal)),
                () => $"Find Usages of the signal to list its uses by its event and names and the scene's connection ({window.Status}): {string.Join("; ", window.Find.Rows.Select(r => r.Usage).OfType<Usage>().Select(u => $"{u.Path}:{u.Line + 1}"))}");

            Ui.PlaceAfter(editor, "void Scor");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "ScoredEventHandler", () => $"Rename's box on the signal's delegate ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("GoalEventHandler");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("signal=\"Goal\"", StringComparison.Ordinal) && File.ReadAllText(HudPath).Contains("main.Goal +=", StringComparison.Ordinal),
                () => $"the scene's connection and the HUD's script to follow the signal ({window.Status}): {File.ReadAllText(HudPath)}", TimeSpan.FromMinutes(1));
            string code = editor.Document.Snapshot.ToString();
            Assert.Contains("public delegate void GoalEventHandler(int points);", code, StringComparison.Ordinal);
            Assert.Contains("Goal += OnScored;", code, StringComparison.Ordinal);
            Assert.Contains("EmitSignal(SignalName.Goal, 1);", code, StringComparison.Ordinal);
            Assert.Contains("EmitSignalGoal(2);", code, StringComparison.Ordinal);
            Assert.Contains("EmitSignal(\"Goal\", 3);", code, StringComparison.Ordinal);
            Assert.Contains("Connect(\"Goal\", new Callable(", code, StringComparison.Ordinal);
            Assert.Contains("main.IsConnected(Main.SignalName.Goal, ", File.ReadAllText(HudPath), StringComparison.Ordinal);
            Built(window);

            window.Confirm = (_, _, _) => Task.FromResult(true);
            editor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(ScenePath) == Scene && File.ReadAllText(HudPath) == HudScript,
                () => $"Undo to put the scene and the HUD's script back ({window.Status}): {File.ReadAllText(HudPath)}");
            Assert.Equal(Script, editor.Document.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Rename from a use of a signal's event, in a script other than the one declaring it, renames the signal: its
    /// delegate takes the new name with EventHandler after it, and every other name of it and the scene follow, so the game builds.</summary>
    [AvaloniaFact]
    public void Renaming_a_signals_event_from_its_use_renames_the_signal_everywhere()
    {
        MainWindow window = game.Open(HudPath);
        try
        {
            Ui.WaitFor(() => window.CSharpService?.For(window.EditorArea.Active!)?.Lsp is not null, "the HUD's script to be open in the language server");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "main.Scor");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Scored", () => $"Rename's box on the signal's event ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("Goal");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScriptPath).Contains("delegate void GoalEventHandler(int points);", StringComparison.Ordinal),
                () => $"the signal's delegate to take the new name ({window.Status}): {File.ReadAllText(ScriptPath)}", TimeSpan.FromMinutes(1));
            string main = File.ReadAllText(ScriptPath);
            Assert.Contains("Goal += OnScored;", main, StringComparison.Ordinal);
            Assert.Contains("EmitSignal(SignalName.Goal, 1);", main, StringComparison.Ordinal);
            Assert.Contains("EmitSignalGoal(2);", main, StringComparison.Ordinal);
            Assert.Contains("EmitSignal(\"Goal\", 3);", main, StringComparison.Ordinal);
            Assert.Contains("main.Goal += points", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Contains("Main.SignalName.Goal", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Contains("[connection signal=\"Goal\" from=\".\" to=\"Hud\" method=\"OnScored\"]", File.ReadAllText(ScenePath), StringComparison.Ordinal);
            Built(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Structure over the open scene lists its nodes nested as Godot builds its tree, each with its class, under each
    /// its script and the scene it instances and its signals' connections: choosing a node goes to its line in the scene,
    /// the keys moving onto its script stay in the scene, and choosing the script, the instanced scene or a connection goes
    /// to the script, to that scene and to the handler the connection calls.</summary>
    [AvaloniaFact]
    public void Structure_lists_a_scenes_nodes_scripts_instances_and_connections_and_goes_to_each()
    {
        MainWindow window = game.Open(ScenePath);
        try
        {
            Ui.Run(window, AppActions.ShowStructure);
            StructureView structure = window.StructureView;
            Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "player.tscn"), () => $"the scene's tree in Structure: {Listed(structure)} ({structure.Said})");
            Assert.Equal([("Main", ": Node2D", 0), ("Main.cs", "script", 1), ("Scored", "→ Hud.OnScored", 1), ("Hud", ": CanvasLayer", 1), ("Hud.cs", "script", 2),
                ("Score", ": Label", 2), ("Start", ": Button", 2), ("pressed", "→ Main.OnStartPressed", 3), ("pressed", "→ Main.OnQuitPressed", 3),
                ("toggled", "→ Main.OnStartPressed", 3), ("Player", ": CharacterBody2D", 1), ("player.tscn", "instance", 2)],
                structure.Rows.Select(r => (r.Name, r.Detail ?? string.Empty, r.Depth)));
            Assert.Equal("%Score", structure.Rows.Single(r => r.Name == "Score").Where);
            ListBox list = structure.GetVisualDescendants().OfType<ListBox>().First();
            CodeEditor editor = window.EditorArea.CodeEditor;

            Ui.Click(Ui.Row(list, structure.Rows.Single(r => r.Name == "Start")));
            Ui.WaitFor(() => editor.CaretPosition.Line == LineOf(Scene, "[node name=\"Start\"") - 1, () => $"the caret on Start's line, where it is on {editor.CaretPosition.Line + 1}");
            Ui.Click(Ui.Row(list, structure.Rows.Single(r => r.Name == "Main")));
            TextPosition onMain = editor.CaretPosition;
            Assert.Equal(LineOf(Scene, "[node name=\"Main\"") - 1, onMain.Line);
            Assert.Equal("Main", structure.Selected?.Name);
            Ui.Row(list, structure.Selected!).Focus();
            Assert.True(list.IsKeyboardFocusWithin, "Structure's chosen row took no keys");
            Ui.Press(window, "DOWN");
            Assert.Equal("Main.cs", structure.Selected?.Name);
            Assert.True(window.EditorArea.Active?.Name == "main.tscn" && editor.CaretPosition == onMain,
                $"The keys moving onto the script's row went to {window.EditorArea.Active?.Name}:{editor.CaretPosition.Line + 1}, where they were to stay on Main");

            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Main.cs", () => $"the script to open ({window.EditorArea.Active?.Name})");
            window.OpenFile(ScenePath);
            Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "player.tscn"), () => $"the scene's tree in Structure again: {Listed(structure)}");
            Ui.Click(Ui.Row(list, structure.Rows.Single(r => r.Name == "player.tscn")));
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "player.tscn", () => $"the instanced scene to open ({window.EditorArea.Active?.Name})");

            window.OpenFile(ScenePath);
            Ui.WaitFor(() => structure.Rows.Any(r => r.Detail == "→ Main.OnStartPressed"), () => $"the scene's tree in Structure again: {Listed(structure)}");
            Ui.Click(Ui.Row(list, structure.Rows.First(r => r.Detail == "→ Main.OnStartPressed")));
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Main.cs" && window.EditorArea.CodeEditor.Document.Snapshot.GetLineText(window.EditorArea.CodeEditor.CaretPosition.Line)
                .Contains("private void OnStartPressed()", StringComparison.Ordinal),
                () => $"the connection to go to its handler ({window.EditorArea.Active?.Name}: {window.EditorArea.CodeEditor.Document.Snapshot.GetLineText(window.EditorArea.CodeEditor.CaretPosition.Line)}; {window.Status})");
        }
        finally
        {
            window.Close();
        }

        static string Listed(StructureView structure) => string.Join(", ", structure.Rows.Select(r => $"{new string(' ', r.Depth)}{r.Name} {r.Detail}"));
    }

    /// <summary>The game built through the window, failing with the build's errors where it does not build.</summary>
    private static void Built(MainWindow window)
    {
        BuildOutcome built = Ui.Await(window.BuildAsync(), "the game's build", TimeSpan.FromMinutes(3));
        Assert.True(built.Succeeded, $"the game to build: {built.Message}; {string.Join("; ", built.Problems.Select(p => p.Message))}");
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
            File.WriteAllText(PathOf("Hud.cs"), HudScript);
            File.WriteAllText(PathOf("Main.cs.uid"), "uid://cmain1\n");
            Directory.CreateDirectory(PathOf("actors"));
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
