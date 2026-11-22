using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A Godot 4 game's C# read against its scenes, as a person works on it: a GetNode path no node of the scene
/// answers, or whose node is not of the type asked for, is an error naming the scene and its line; completion in a
/// GetNode string offers the scene's nodes, and in a res:// string the project's files, which Go to Declaration opens;
/// an exported property's card says what the scene sets it to; Rename on a node's name in a GetNode path renames it in the
/// scene and in every path of the code, one step of Undo. Its project (Godot.NET.Sdk) is restored from the packages the
/// Godot found carries, so without a Godot 4 .NET build each test skips saying why; Godot runs only headless, to save a
/// scene as bytes.</summary>
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

    /// <summary>A Godot game's C# is checked and completed against its scenes and leads to their uids and files, an
    /// open scene marks what it lacks, and Structure lists a scene.</summary>
    [AvaloniaFact]
    public void A_scenes_code_is_checked_completed_and_followed_through_its_scenes()
    {
        Flow.Step(nameof(A_wrong_node_path_or_type_in_GetNode_is_an_error_naming_the_scene_and_its_line),
            A_wrong_node_path_or_type_in_GetNode_is_an_error_naming_the_scene_and_its_line);
        OpenWindows.CloseAll();
        game.Reset();
        Flow.Step(nameof(Node_paths_and_res_paths_complete_from_the_scenes_and_a_res_path_leads_to_its_file),
            Node_paths_and_res_paths_complete_from_the_scenes_and_a_res_path_leads_to_its_file);
        OpenWindows.CloseAll();
        game.Reset();
        Flow.Step(nameof(A_signals_connection_and_its_handler_lead_to_each_other_and_one_to_a_missing_method_is_marked),
            A_signals_connection_and_its_handler_lead_to_each_other_and_one_to_a_missing_method_is_marked);
        OpenWindows.CloseAll();
        game.Reset();
        game.Reset();
        Flow.Step(nameof(An_open_scene_marks_a_missing_resource_and_a_broken_connection),
            An_open_scene_marks_a_missing_resource_and_a_broken_connection);
        OpenWindows.CloseAll();
        game.Reset();
        game.Reset();
        game.Reset();
        game.Reset();
        game.Reset();
        Flow.Step(nameof(A_uid_leads_to_its_file_from_the_code_and_from_a_scene), A_uid_leads_to_its_file_from_the_code_and_from_a_scene);
        OpenWindows.CloseAll();
        game.Reset();
        game.Reset();
        game.Reset();
        Flow.Step(nameof(Structure_lists_a_scenes_nodes_scripts_instances_and_connections_and_goes_to_each),
            Structure_lists_a_scenes_nodes_scripts_instances_and_connections_and_goes_to_each);
    }

    /// <summary>A node's rename from the code, a script or scene moved, and a handler or signal renamed reach every
    /// scene, script and string naming them, one step of Undo, a scene saved a moment before taken as the save left
    /// it.</summary>
    [AvaloniaFact]
    public void Renames_and_moves_follow_through_every_godot_file()
    {
        Flow.Step(nameof(Renaming_a_node_from_its_path_in_the_code_renames_it_in_the_scene_and_the_code_one_step_of_undo),
            Renaming_a_node_from_its_path_in_the_code_renames_it_in_the_scene_and_the_code_one_step_of_undo);
        OpenWindows.CloseAll();
        game.Reset();
        game.Reset();
        game.Reset();
        game.Reset();
        game.Reset();
        game.Reset();
        Flow.Step(nameof(A_script_moved_with_its_uid_in_the_explorer_is_followed_by_its_scene_and_checked_where_it_went),
            A_script_moved_with_its_uid_in_the_explorer_is_followed_by_its_scene_and_checked_where_it_went);
        OpenWindows.CloseAll();
        game.Reset();
        Flow.Step(nameof(Renaming_a_scripts_class_moves_its_file_with_its_scenes_text_and_binary_and_its_uid_following),
            Renaming_a_scripts_class_moves_its_file_with_its_scenes_text_and_binary_and_its_uid_following);
        OpenWindows.CloseAll();
        game.Reset();
        Flow.Step(nameof(A_scene_moved_in_the_explorer_is_followed_by_every_res_path_naming_it),
            A_scene_moved_in_the_explorer_is_followed_by_every_res_path_naming_it);
        OpenWindows.CloseAll();
        game.Reset();
        Flow.Step(nameof(A_scene_moved_in_the_explorer_is_followed_by_the_gdscripts_naming_it),
            A_scene_moved_in_the_explorer_is_followed_by_the_gdscripts_naming_it);
        OpenWindows.CloseAll();
        game.Reset();
        game.Reset();
        Flow.Step(nameof(Renaming_a_handler_renames_the_method_its_scenes_connections_call),
            Renaming_a_handler_renames_the_method_its_scenes_connections_call);
        OpenWindows.CloseAll();
        game.Reset();
        game.Reset();
        game.Reset();
        Flow.Step(nameof(Renaming_a_node_changes_a_scene_saved_a_moment_before_as_the_save_left_it),
            Renaming_a_node_changes_a_scene_saved_a_moment_before_as_the_save_left_it);
        OpenWindows.CloseAll();
        game.Reset();
        Flow.Step(nameof(Renaming_a_signal_renames_its_event_SignalName_and_strings_in_every_script_and_the_game_builds),
            Renaming_a_signal_renames_its_event_SignalName_and_strings_in_every_script_and_the_game_builds);
        OpenWindows.CloseAll();
        game.Reset();
    }

    /// <summary>A path GetNode is given that the scene has no node of, and one whose node is a Label asked for as a Button, are
    /// errors where the path is written, naming the scene and its line; a res:// path no file answers is an error too, and the
    /// paths the scene answers (a child's, a unique name's, an instanced scene's node) and the file there are not marked.</summary>
    private void A_wrong_node_path_or_type_in_GetNode_is_an_error_naming_the_scene_and_its_line()
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
    private void Node_paths_and_res_paths_complete_from_the_scenes_and_a_res_path_leads_to_its_file()
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
            Assert.DoesNotContain(window.Assist.CompletionLabels, l => l.Contains(IgnoredFolders.GodotCache, StringComparison.Ordinal));
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
    private void Renaming_a_node_from_its_path_in_the_code_renames_it_in_the_scene_and_the_code_one_step_of_undo()
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
    private void A_signals_connection_and_its_handler_lead_to_each_other_and_one_to_a_missing_method_is_marked()
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

    /// <summary>An open scene is marked where Godot would not load what it says: its connection to a method Main lacks, and
    /// the one of other arguments than the handler takes, where it writes them; and an external resource whose file is not
    /// there, once typed so and the typing pauses. The HUD's connection, which holds, is not marked.</summary>
    private void An_open_scene_marks_a_missing_resource_and_a_broken_connection()
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

    /// <summary>A script moved in the Explorer takes the .uid beside it along, and the open scene its path; the scene is
    /// checked against the script where it went (a connection of other arguments than its handler takes marked, no handler
    /// said missing), and the script, opened there, against the scene (a GetNode path no node answers).</summary>
    private void A_script_moved_with_its_uid_in_the_explorer_is_followed_by_its_scene_and_checked_where_it_went()
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

    /// <summary>A Godot script that saves, compressed as Godot's editor saves a scene, a scene whose root keeps Main.cs, then quits.</summary>
    private const string BinaryMaker = "extends SceneTree\n\n\nfunc _init() -> void:\n\tvar level := Node2D.new()\n\tlevel.name = \"Level\"\n"
        + "\tlevel.set_meta(\"code\", load(\"res://Main.cs\"))\n\tvar scene := PackedScene.new()\n\tscene.pack(level)\n"
        + "\tprint(\"saved level \", ResourceSaver.save(scene, \"res://level.scn\", ResourceSaver.FLAG_COMPRESS))\n\tlevel.free()\n\tquit()\n";

    /// <summary>Rename of the script's class with its file moves the file, and what loads it follows within the rename's one
    /// command, as a move in the Explorer has it follow: the text scene's path, the .uid beside it, and a scene Godot saved as
    /// bytes, its path written anew with every other byte kept; one Undo puts every file back as it was.</summary>
    private void Renaming_a_scripts_class_moves_its_file_with_its_scenes_text_and_binary_and_its_uid_following()
    {
        string maker = TestFolders.Create("godot-binary-");
        try
        {
            TestFolders.Write(maker, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Maker\"\n");
            TestFolders.Write(maker, "Main.cs", Script);
            TestFolders.Write(maker, "Main.cs.uid", "uid://cmain1\n");
            string script = TestFolders.Write(maker + "-home", "make.gd", BinaryMaker);
            string made = GodotExplorerCopyTests.Run(game.Godot!, maker, maker + "-home", ["--script", script]) ?? string.Empty;
            Assert.True(made.Contains("saved level 0", StringComparison.Ordinal), $"Godot saved no binary scene naming Main.cs:\n{made}");
            File.Copy(Path.Combine(maker, "level.scn"), game.PathOf("level.scn"));
        }
        finally
        {
            TestFolders.Delete(maker);
            TestFolders.Delete(maker + "-home");
        }
        string level = game.PathOf("level.scn");
        byte[] saved = File.ReadAllBytes(level);
        GodotBinaryResource original = GodotBinaryResource.Read(saved, out string? unread) ?? throw new Xunit.Sdk.XunitException($"Passenger can't read Godot's binary scene: {unread}");
        Assert.True(saved.AsSpan().StartsWith("RSCC"u8) && original.Externals.Any(e => e.Path == "res://Main.cs"),
            $"Godot's binary scene is not compressed or names no res://Main.cs: {string.Join(", ", original.Externals)}");
        string hero = game.PathOf("Hero.cs");

        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "partial class Mai");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Main", () => $"Rename's box on the script's class ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("Hero");
            Ui.Press(window, "ENTER");
            IReadOnlyList<GodotBinaryExternal> named = [.. original.Externals.Select(e => e.Path == "res://Main.cs" ? e with { Path = "res://Hero.cs" } : e)];
            Ui.WaitFor(() => File.Exists(hero) && GodotBinaryResource.Read(File.ReadAllBytes(level), out _)?.Externals.SequenceEqual(named) == true,
                () => $"the binary scene to name the script where its rename moved it ({window.Status}): {string.Join(", ", GodotBinaryResource.Read(File.ReadAllBytes(level), out _)?.Externals ?? [])}",
                TimeSpan.FromMinutes(1));
            Assert.True(original.With(null, named).AsSpan().SequenceEqual(File.ReadAllBytes(level)), "The binary scene changed more than the script's path");
            Assert.Equal(Scene.Replace("path=\"res://Main.cs\"", "path=\"res://Hero.cs\"", StringComparison.Ordinal), File.ReadAllText(ScenePath));
            Assert.True(File.Exists(hero + ".uid") && !File.Exists(ScriptPath + ".uid") && !File.Exists(ScriptPath), "The script's .uid did not go with it to Hero.cs");
            Assert.Equal("uid://cmain1", File.ReadAllText(hero + ".uid").Trim());

            window.Confirm = (_, _, _) => Task.FromResult(true);
            editor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.Exists(ScriptPath) && File.ReadAllBytes(level).AsSpan().SequenceEqual(saved) && File.ReadAllText(ScenePath) == Scene,
                () => $"Undo to put the script, its scenes and its .uid back ({window.Status}): {File.ReadAllText(ScenePath)}");
            Assert.True(File.Exists(ScriptPath + ".uid") && !File.Exists(hero) && !File.Exists(hero + ".uid"), "Undo left the script or its .uid where the rename moved them");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A uid:// in the code leads to the scene whose header gives it, its card saying which, and one in a scene's
    /// external resource to the script the .uid file beside it gives it to.</summary>
    private void A_uid_leads_to_its_file_from_the_code_and_from_a_scene()
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
    private void A_scene_moved_in_the_explorer_is_followed_by_every_res_path_naming_it()
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

    /// <summary>A scene moved in the Explorer takes a GDScript's paths along, its preload and its load, as one command with the
    /// rest, or the script no longer loads it.</summary>
    private void A_scene_moved_in_the_explorer_is_followed_by_the_gdscripts_naming_it()
    {
        const string gdscript = "extends Node\n\nconst Player = preload(\"res://player.tscn\")\n\n\nfunc _ready():\n\tprint(load('res://player.tscn'))\n";
        string script = game.PathOf("spawner.gd");
        File.WriteAllText(script, gdscript);
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            Choose(window, game.PathOf("player.tscn"));
            Ui.Run(window, EditorActions.Cut);
            Choose(window, game.PathOf("actors"));
            Ui.Run(window, EditorActions.Paste);
            Ui.WaitFor(() => File.ReadAllText(script) == gdscript.Replace("res://player.tscn", "res://actors/player.tscn", StringComparison.Ordinal),
                () => $"the GDScript's paths to follow the scene ({window.Status}): {File.ReadAllText(script)}");
        }
        finally
        {
            window.Close();
            File.Delete(script);
        }
    }

    /// <summary>The Explorer's row of a file or folder chosen, with the keys.</summary>
    internal static void Choose(MainWindow window, string path)
    {
        string shown = PathNames.Canonical(path);
        window.ExplorerView.Reveal(shown);
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Path is { } row && PathNames.Same(row, shown)),
            () => $"the Explorer to show {Path.GetFileName(path)}: {string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Path))}");
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path is { } row && PathNames.Same(row, shown)));
    }

    /// <summary>Rename of the handler in the code renames the method the scene's connections call, one step of Undo with the code's.</summary>
    private void Renaming_a_handler_renames_the_method_its_scenes_connections_call()
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

    /// <summary>A scene saved a moment before a node's rename, the folder's watcher yet to tell of it (here, never to), is one
    /// the rename waits to read: its name changes where the save left it, what the save wrote kept, never a refusal.</summary>
    private void Renaming_a_node_changes_a_scene_saved_a_moment_before_as_the_save_left_it()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => Marked(window).Count > 0, "the scene's checks to be read");
            window.FolderWatcher!.Dispose();
            File.WriteAllText(ScenePath, Scene.Replace("Speed = 2.5", "Other = 1\nSpeed = 2.5", StringComparison.Ordinal));
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "GetNode<Label>(\"Hud/Sc");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Score", () => $"Rename's box on the node Score ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("Points");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("[node name=\"Points\"", StringComparison.Ordinal),
                () => $"the scene to name the node as the save left it ({window.Status}): {File.ReadAllText(ScenePath)}", TimeSpan.FromMinutes(1));
            Assert.Contains("Other = 1", File.ReadAllText(ScenePath), StringComparison.Ordinal);
            Assert.Contains("GetNode<Label>(\"Hud/Points\")", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
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
    private void Renaming_a_signal_renames_its_event_SignalName_and_strings_in_every_script_and_the_game_builds()
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

    /// <summary>Structure over the open scene lists its nodes nested as Godot builds its tree, each with its class, under each
    /// its script and the scene it instances and its signals' connections: choosing a node goes to its line in the scene,
    /// the keys moving onto its script stay in the scene, and choosing the script, the instanced scene or a connection goes
    /// to the script, to that scene and to the handler the connection calls.</summary>
    private void Structure_lists_a_scenes_nodes_scripts_instances_and_connections_and_goes_to_each()
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
