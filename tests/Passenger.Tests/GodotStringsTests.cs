using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The strings a Godot 4 game's C# gives Godot's calls naming what the project's own files define, as a person
/// works on them: an input action (project.godot's [input]), a group (a scene's node's), an animation (its player's
/// library, an animated sprite's SpriteFrames), an autoload (project.godot's [autoload], through a GetNode path's /root), a
/// shader parameter (the uniform of its material's shader), a translation's key (a CSV project.godot lists), an
/// AnimationTree's parameter (its tree's) and a global shader uniform (project.godot's [shader_globals]). For each,
/// completion offers what the project defines, a name it does not is an error saying where Godot looks, Go to Declaration
/// opens its definition, and Rename renames it there, in a GDScript's strings and in every string naming it, one step of
/// Undo; Find Usages lists them all. A layer's number hovers its name in [layer_names] and is an error outside Godot's
/// numbers. Its project restores from the packages the Godot found carries, so without a Godot 4 .NET build each test
/// skips saying why; no Godot runs.</summary>
public sealed class GodotStringsTests : IClassFixture<GodotStringsTests.Game>
{
    private readonly Game game;

    public GodotStringsTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is not { WhyNot: null, Version: not null, Packages: not null }, game.Skipped);
        game.Reset();
    }

    internal const string Script = """
        using Godot;

        public partial class Main : Node2D
        {
            private AnimationPlayer anim;

            public override void _Ready()
            {
                anim = GetNode<AnimationPlayer>("Anim");
                anim.Play("run");
                anim.Queue("rnu");
                bool enemy = IsInGroup("enemies");
                int enemies = GetTree().GetNodesInGroup("enemis").Count;
                Globals globals = GetNode<Globals>("/root/Globals");
                Node gone = GetNode("/root/Globls");
                ShaderMaterial material = (ShaderMaterial)GetNode<Sprite2D>("Sprite").Material;
                material.SetShaderParameter("tint", Colors.Red);
                material.SetShaderParameter("tnt", Colors.Red);
                string hello = Tr("HELLO");
                string bye = Tr("BYE");
                AnimationTree tree = GetNode<AnimationTree>("Tree");
                tree.Set("parameters/conditions/is_running", true);
                tree.Set("parameters/conditions/is_runing", true);
                AnimatedSprite2D frames = GetNode<AnimatedSprite2D>("Frames");
                frames.Play("walk");
                frames.Play("wlk");
                RenderingServer.GlobalShaderParameterSet("wind", Vector2.One);
                RenderingServer.GlobalShaderParameterSet("wnd", Vector2.One);
                GetNode<Area2D>("Enemy").SetCollisionLayerValue(3, true);
                GetNode<Area2D>("Enemy").SetCollisionMaskValue(33, true);
            }

            public override void _Process(double delta)
            {
                if (Input.IsActionPressed("jump") || Input.IsActionJustPressed("jmp") || Input.IsActionPressed("ui_accept"))
                {
                    GetTree().CallGroup("enemies", "queue_free");
                }
            }
        }

        """;

    internal const string Globals = """
        using Godot;

        public partial class Globals : Node
        {
        }

        """;

    internal const string Settings = """

        [autoload]

        Globals="*res://Globals.cs"

        [input]

        jump={
        "deadzone": 0.5,
        "events": []
        }

        [internationalization]

        locale/translations=PackedStringArray("res://text.en.translation", "res://text.fr.translation")

        [layer_names]

        2d_physics/layer_3="enemies"

        [shader_globals]

        wind={
        "type": "vec2",
        "value": Vector2(0, 0)
        }

        """;

    internal const string Scene = """
        [gd_scene format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]
        [ext_resource type="Shader" path="res://tint.gdshader" id="2_tint"]

        [sub_resource type="Animation" id="Animation_run"]
        length = 0.5

        [sub_resource type="Animation" id="Animation_idle"]

        [sub_resource type="AnimationLibrary" id="AnimationLibrary_main"]
        _data = {
        &"idle": SubResource("Animation_idle"),
        &"run": SubResource("Animation_run")
        }

        [sub_resource type="ShaderMaterial" id="ShaderMaterial_tint"]
        shader = ExtResource("2_tint")
        shader_parameter/tint = Color(1, 0, 0, 1)

        [sub_resource type="AnimationNodeAnimation" id="AnimationNodeAnimation_idle"]
        animation = &"idle"

        [sub_resource type="AnimationNodeStateMachineTransition" id="AnimationNodeStateMachineTransition_go"]
        advance_mode = 2
        advance_condition = &"is_running"

        [sub_resource type="AnimationNodeStateMachine" id="AnimationNodeStateMachine_moves"]
        states/Idle/node = SubResource("AnimationNodeAnimation_idle")
        transitions = ["Start", "Idle", SubResource("AnimationNodeStateMachineTransition_go")]

        [sub_resource type="SpriteFrames" id="SpriteFrames_walk"]
        animations = [{
        "frames": [],
        "loop": true,
        "name": &"idle",
        "speed": 5.0
        }, {
        "frames": [],
        "loop": true,
        "name": &"walk",
        "speed": 8.0
        }]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")

        [node name="Enemy" type="Area2D" parent="." groups=["enemies"]]

        [node name="Anim" type="AnimationPlayer" parent="."]
        libraries/ = SubResource("AnimationLibrary_main")
        autoplay = &"run"

        [node name="Sprite" type="Sprite2D" parent="."]
        material = SubResource("ShaderMaterial_tint")

        [node name="Tree" type="AnimationTree" parent="."]
        tree_root = SubResource("AnimationNodeStateMachine_moves")
        anim_player = NodePath("../Anim")

        [node name="Frames" type="AnimatedSprite2D" parent="."]
        sprite_frames = SubResource("SpriteFrames_walk")
        animation = &"walk"

        """;

    internal const string Shader = """
        shader_type canvas_item;

        uniform vec4 tint : source_color = vec4(1.0);
        uniform float amount = 0.5;

        void fragment() {
            COLOR = tint * amount;
        }

        """;

    internal const string Translations = "keys,en,fr\nHELLO,Hello,Bonjour\nTHANKS,Thanks,Merci\n";

    /// <summary>A GDScript of the game's naming the input action.</summary>
    internal const string Hud = "extends CanvasLayer\n\n\nfunc _process(_delta: float) -> void:\n\tvisible = not Input.is_action_pressed(\"jump\")\n";

    private string HudPath => game.PathOf("hud.gd");

    private string ScriptPath => game.PathOf("Main.cs");

    private string ProjectPath => game.PathOf("project.godot");

    private string ScenePath => game.PathOf("main.tscn");

    /// <summary>The errors Passenger's Godot checks mark in the shown file, once each wanted one is marked.</summary>
    private static List<string> MarkedOnce(MainWindow window, params string[] wanted)
    {
        Ui.WaitFor(() => wanted.All(GodotSceneTests.Marked(window).Contains),
            () => $"{string.Join("; ", wanted)} to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(1));
        return GodotSceneTests.Marked(window);
    }

    /// <summary>Completion at a place in the shown file, once it offers each label wanted; its labels.</summary>
    private static List<string> Completed(MainWindow window, string after, params string[] wanted)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => wanted.All(window.Assist.CompletionLabels.Contains), () => $"{string.Join(", ", wanted)} to be offered after {after}: {string.Join(", ", window.Assist.CompletionLabels)}");
        List<string> labels = [.. window.Assist.CompletionLabels];
        Ui.Press(window, "ESCAPE");
        return labels;
    }

    /// <summary>Go to Declaration from a place in Main.cs, once it opens a file whose caret's line holds what is wanted.</summary>
    private static void Declared(MainWindow window, string after, string file, string line)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.GoToDeclaration);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => window.EditorArea.Active?.Name == file && editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line).Contains(line, StringComparison.Ordinal),
            () => $"Go to Declaration from {after} to open {file} on '{line}' ({window.EditorArea.Active?.Name}: {editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}; {window.Status})");
    }

    /// <summary>A new name holding a quote is written for each string it goes in: escaped in a GDScript's single quotes, as it
    /// is in project.godot and C#, or the script no longer parses.</summary>
    [AvaloniaFact]
    public void Renaming_an_action_to_a_name_holding_a_quote_escapes_it_in_a_gdscripts_single_quoted_string()
    {
        File.WriteAllText(HudPath, Hud.Replace("\"jump\"", "'jump'", StringComparison.Ordinal));
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Renamed(window, "IsActionPressed(\"ju", "jump", "player's_jump");
            Ui.WaitFor(() => TextOf(window, HudPath).Contains("is_action_pressed('player\\'s_jump')", StringComparison.Ordinal), () => $"hud.gd to escape the quote: {TextOf(window, HudPath)}");
            Assert.Contains("player's_jump={", File.ReadAllText(game.PathOf("project.godot")), StringComparison.Ordinal);
            Assert.Contains("IsActionPressed(\"player's_jump\")", window.EditorArea.CodeEditor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The first rename writes hud.gd, closed, and the folder's watcher never tells of it here: the second finds it as
    /// the first left it, rather than refusing a file its index read before.</summary>
    [AvaloniaFact]
    public void A_rename_right_after_another_finds_each_file_as_that_one_left_it()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            window.FolderWatcher!.Dispose();
            Renamed(window, "IsActionPressed(\"ju", "jump", "leap");
            Renamed(window, "IsActionPressed(\"le", "leap", "hop");
            Assert.Contains("is_action_pressed(\"hop\")", File.ReadAllText(HudPath), StringComparison.Ordinal);
            Assert.Contains("\nhop={", File.ReadAllText(ProjectPath), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file another program wrote a moment before, the folder's watcher yet to tell of it (here, never to), is one
    /// the rename waits to read, keeping what was written, never a refusal to try again.</summary>
    [AvaloniaFact]
    public void A_rename_a_moment_after_another_program_wrote_a_file_waits_for_it_and_keeps_what_it_wrote()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "IsActionPressed(\"ju");
            Ui.Run(window, AppActions.FindUsages);
            Ui.WaitFor(() => window.Find.Rows.Any(r => r.Usage is { } found && Path.GetFileName(found.Path) == "hud.gd"), () => $"the project to be read ({window.Status})");
            window.FolderWatcher!.Dispose();
            File.WriteAllText(HudPath, "# Written by another program.\n" + Hud);
            Renamed(window, "IsActionPressed(\"ju", "jump", "leap");
            Assert.Equal("# Written by another program.\n" + Hud.Replace("\"jump\"", "\"leap\"", StringComparison.Ordinal), File.ReadAllText(HudPath));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Rename at a place in Main.cs, from the name to another, once the status says it was made.</summary>
    private void Renamed(MainWindow window, string after, string old, string name)
    {
        window.OpenFile(ScriptPath);
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == old, () => $"Rename's box on {old} ({window.Assist.Renaming}; {window.Status})");
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Status?.StartsWith($"Renamed {old} to {name}", StringComparison.Ordinal) == true, () => $"{old}'s rename to be made ({window.Status})");
    }

    /// <summary>Undo in Main.cs, once every file Godot's names were renamed in is as it was.</summary>
    private void Undone(MainWindow window, params (string Path, string Text)[] files)
    {
        window.OpenFile(ScriptPath);
        Ui.Settle();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => files.All(f => TextOf(window, f.Path) == f.Text), () => $"Undo to put {string.Join(", ", files.Select(f => Path.GetFileName(f.Path)))} back ({window.Status})");
        Assert.Equal(Script, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
    }

    /// <summary>A file's text as the window has it: an open one's in its editor, which a rename changes unsaved, else the disk's.</summary>
    private static string TextOf(MainWindow window, string path) =>
        window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, PathNames.Canonical(path)))?.Text.Snapshot.ToString() ?? File.ReadAllText(path);

    private string Code(MainWindow window) => window.EditorArea.Documents.First(d => PathNames.Same(d.Path, PathNames.Canonical(ScriptPath))).Text.Snapshot.ToString();

    /// <summary>An input action project.godot lacks is an error, its own and Godot's ui_ ones are not; completion offers them,
    /// Go to Declaration opens project.godot on the action, Find Usages lists project.godot's key, the C# string and the
    /// GDScript's, and Rename renames it in all three.</summary>
    [AvaloniaFact]
    public void An_input_action_completes_from_project_godot_is_checked_leads_to_it_is_listed_and_is_renamed_there()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            List<string> marked = MarkedOnce(window, "No input action 'jmp' is in project.godot's [input], nor among Godot's own ui_ actions");
            Assert.DoesNotContain(marked, m => m.Contains("'jump'", StringComparison.Ordinal) || m.Contains("ui_accept", StringComparison.Ordinal));
            // The caret there brings the line into sight for the pointer.
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "IsActionPressed(\"");
            GodotSceneTests.Hover(window, "jump\")", $"project.godot:{GodotSceneTests.LineOf(File.ReadAllText(ProjectPath), "jump={")}");
            Completed(window, "IsActionPressed(\"", "jump", "ui_accept");

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "IsActionPressed(\"ju");
            Ui.Run(window, AppActions.FindUsages);
            (string File, int Line)[] uses = [("project.godot", GodotSceneTests.LineOf(File.ReadAllText(ProjectPath), "jump={") - 1),
                ("Main.cs", GodotSceneTests.LineOf(Script, "IsActionPressed(\"jump\")") - 1), ("hud.gd", GodotSceneTests.LineOf(Hud, "is_action_pressed") - 1)];
            Ui.WaitFor(() => uses.All(u => window.Find.Rows.Any(r => r.Usage is { } found && Path.GetFileName(found.Path) == u.File && found.Line == u.Line)),
                () => $"Find Usages of jump to list {string.Join(", ", uses)} ({window.Status}): {string.Join("; ", window.Find.Rows.Select(r => r.Usage).OfType<Usage>().Select(u => $"{Path.GetFileName(u.Path)}:{u.Line}"))}");

            Declared(window, "IsActionPressed(\"ju", "project.godot", "jump={");
            string project = TextOf(window, ProjectPath);
            Renamed(window, "IsActionPressed(\"ju", "jump", "leap");
            Ui.WaitFor(() => TextOf(window, ProjectPath).Contains("\nleap={", StringComparison.Ordinal), () => $"project.godot to name the action leap: {TextOf(window, ProjectPath)}");
            Assert.Contains("Input.IsActionPressed(\"leap\")", Code(window), StringComparison.Ordinal);
            Ui.WaitFor(() => TextOf(window, HudPath).Contains("is_action_pressed(\"leap\")", StringComparison.Ordinal), () => $"hud.gd to name the action leap: {TextOf(window, HudPath)}");
            Undone(window, (ProjectPath, project), (HudPath, Hud));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A group no scene gives a node is an error; completion offers the scene's, Go to Declaration opens the scene on
    /// the node, and Rename renames the group there and in every string naming it.</summary>
    [AvaloniaFact]
    public void A_group_completes_from_the_scenes_is_checked_leads_to_its_node_and_is_renamed_there()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, "No group 'enemis' is given to a node by the project's scenes, nor added by its code");
            Completed(window, "IsInGroup(\"", "enemies");
            Declared(window, "IsInGroup(\"en", "main.tscn", "[node name=\"Enemy\"");
            Renamed(window, "IsInGroup(\"en", "enemies", "foes");
            Ui.WaitFor(() => TextOf(window, ScenePath).Contains("groups=[\"foes\"]", StringComparison.Ordinal), () => $"the scene to give the node foes: {TextOf(window, ScenePath)}");
            Assert.Contains("IsInGroup(\"foes\")", Code(window), StringComparison.Ordinal);
            Assert.Contains("CallGroup(\"foes\", ", Code(window), StringComparison.Ordinal);
            Undone(window, (ScenePath, Scene));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An animation the player the code gets lacks is an error naming the player; completion offers its library's,
    /// Go to Declaration opens the scene on the library's animation, and Rename renames it there, in the player's autoplay
    /// and in every string naming it.</summary>
    [AvaloniaFact]
    public void An_animation_completes_from_its_players_library_is_checked_leads_to_it_and_is_renamed_there()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, $"No animation 'rnu' is in the animation libraries of Anim (main.tscn:{GodotSceneTests.LineOf(Scene, "[node name=\"Anim\"")})");
            Completed(window, "Play(\"", "run", "idle");
            Declared(window, "Play(\"r", "main.tscn", "&\"run\": SubResource");
            Renamed(window, "Play(\"r", "run", "sprint");
            Ui.WaitFor(() => TextOf(window, ScenePath).Contains("&\"sprint\": SubResource(\"Animation_run\")", StringComparison.Ordinal),
                () => $"the library to name the animation sprint: {TextOf(window, ScenePath)}");
            Assert.Contains("autoplay = &\"sprint\"", TextOf(window, ScenePath), StringComparison.Ordinal);
            Assert.Contains("anim.Play(\"sprint\")", Code(window), StringComparison.Ordinal);
            Undone(window, (ScenePath, Scene));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A GetNode path through /root naming no autoload is an error; completion from its "/" offers the autoloads,
    /// Go to Declaration opens project.godot on the autoload, and Rename renames it there and in every path through it.</summary>
    [AvaloniaFact]
    public void An_autoload_completes_from_project_godot_is_checked_leads_to_it_and_is_renamed_there()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            List<string> marked = MarkedOnce(window, "No autoload 'Globls' is in project.godot's [autoload], nor is a scene's root node so named");
            Assert.DoesNotContain(marked, m => m.Contains("/root/Globals", StringComparison.Ordinal));
            Completed(window, "GetNode<Globals>(\"/", "/root/Globals");
            Declared(window, "GetNode<Globals>(\"/root/Glo", "project.godot", "Globals=\"*res://Globals.cs\"");
            string project = TextOf(window, ProjectPath);
            Renamed(window, "GetNode<Globals>(\"/root/Glo", "Globals", "State");
            Ui.WaitFor(() => TextOf(window, ProjectPath).Contains("State=\"*res://Globals.cs\"", StringComparison.Ordinal), () => $"project.godot to name the autoload State: {TextOf(window, ProjectPath)}");
            Assert.Contains("GetNode<Globals>(\"/root/State\")", Code(window), StringComparison.Ordinal);
            Undone(window, (ProjectPath, project));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A shader parameter the uniforms of the node's material's shader lack is an error naming the shader; completion
    /// offers its uniforms, Go to Declaration opens the shader on the uniform, and Rename renames it there, in the shader's
    /// code, in the material setting it and in every string naming it.</summary>
    [AvaloniaFact]
    public void A_shader_parameter_completes_from_the_materials_shader_is_checked_leads_to_its_uniform_and_is_renamed_there()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, "No shader parameter 'tnt' is a uniform of tint.gdshader, the shader of Sprite's material");
            Completed(window, "SetShaderParameter(\"", "tint", "amount");
            Declared(window, "SetShaderParameter(\"ti", "tint.gdshader", "uniform vec4 tint");
            string shader = game.PathOf("tint.gdshader");
            Renamed(window, "SetShaderParameter(\"ti", "tint", "colour");
            Ui.WaitFor(() => TextOf(window, shader).Contains("uniform vec4 colour", StringComparison.Ordinal), () => $"the shader to name its uniform colour: {TextOf(window, shader)}");
            Assert.Contains("COLOR = colour * amount;", TextOf(window, shader), StringComparison.Ordinal);
            Assert.Contains("shader_parameter/colour = Color(", TextOf(window, ScenePath), StringComparison.Ordinal);
            Assert.Contains("SetShaderParameter(\"colour\", ", Code(window), StringComparison.Ordinal);
            Undone(window, (shader, Shader), (ScenePath, Scene));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A layer's number hovers the name project.godot's [layer_names] gives it and leads there, and one outside the
    /// layers Godot numbers is an error naming them.</summary>
    [AvaloniaFact]
    public void A_layer_number_hovers_its_name_leads_to_it_and_is_marked_outside_godots_layers()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, "No layer '33' is among Godot's 2D physics layers, numbered 1 to 32");
            int line = GodotSceneTests.LineOf(File.ReadAllText(ProjectPath), "2d_physics/layer_3");
            GodotSceneTests.Hover(window, "(3, true)", $"2D physics layer 3: enemies, named in project.godot:{line}");
            Declared(window, "SetCollisionLayerValue(", "project.godot", "2d_physics/layer_3=\"enemies\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An AnimationTree's parameter its tree does not make is an error naming the tree; completion offers those it
    /// makes, its state machine's playback and its transition's condition among them.</summary>
    [AvaloniaFact]
    public void An_animation_trees_parameter_completes_from_its_tree_and_is_checked()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, $"No AnimationTree parameter 'parameters/conditions/is_runing' is made by the tree of Tree (main.tscn:{GodotSceneTests.LineOf(Scene, "[node name=\"Tree\"")})");
            Completed(window, "tree.Set(\"", "parameters/conditions/is_running", "parameters/playback");
            Declared(window, "tree.Set(\"parameters/conditions/is_r", "main.tscn", "advance_condition = &\"is_running\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An animation an animated sprite's SpriteFrames lack is an error naming the sprite; completion offers its
    /// frames' animations, and Go to Declaration opens the scene on the animation's name.</summary>
    [AvaloniaFact]
    public void An_animated_sprites_animation_completes_from_its_sprite_frames_and_is_checked()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, $"No animation 'wlk' is in the SpriteFrames of Frames (main.tscn:{GodotSceneTests.LineOf(Scene, "[node name=\"Frames\"")})");
            Completed(window, "frames.Play(\"", "walk", "idle");
            Declared(window, "frames.Play(\"wa", "main.tscn", "\"name\": &\"walk\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A global shader uniform project.godot's [shader_globals] lacks is an error; completion offers its own, and Go
    /// to Declaration opens project.godot on it.</summary>
    [AvaloniaFact]
    public void A_global_shader_uniform_completes_from_project_godot_and_is_checked()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, "No global shader uniform 'wnd' is in project.godot's [shader_globals]");
            Completed(window, "GlobalShaderParameterSet(\"", "wind");
            Declared(window, "GlobalShaderParameterSet(\"wi", "project.godot", "wind={");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A translation's key the CSV project.godot lists lacks is an error naming it; completion offers its keys, Go to
    /// Declaration opens the CSV on the key, and Rename renames it there and in every string naming it.</summary>
    [AvaloniaFact]
    public void A_translation_key_completes_from_the_listed_csv_is_checked_leads_to_it_and_is_renamed_there()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            MarkedOnce(window, "No translation 'BYE' is in the translations project.godot lists (text.csv)");
            Completed(window, "Tr(\"", "HELLO", "THANKS");
            Declared(window, "Tr(\"HE", "text.csv", "HELLO,Hello,Bonjour");
            string csv = game.PathOf("text.csv");
            Renamed(window, "Tr(\"HE", "HELLO", "GREETING");
            Ui.WaitFor(() => TextOf(window, csv).Contains("\nGREETING,Hello,Bonjour", StringComparison.Ordinal), () => $"the CSV to name the key GREETING: {TextOf(window, csv)}");
            Assert.Contains("Tr(\"GREETING\")", Code(window), StringComparison.Ordinal);
            Undone(window, (csv, Translations));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Godot 4 C# project whose project.godot defines an input action, an autoload and translations, whose main
    /// scene gives a node a group, its player an animation library and its sprite a shader's material, on the Godot found:
    /// its Godot.NET.Sdk the version that Godot carries, restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-strings-")
        {
            Godot = GodotExecutables.Find(null);
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.AppendAllText(PathOf("project.godot"), Settings);
            File.WriteAllText(PathOf("Globals.cs"), Globals);
            File.WriteAllText(PathOf("tint.gdshader"), Shader);
            File.WriteAllText(PathOf("text.csv"), Translations);
            File.WriteAllText(PathOf("hud.gd"), Hud);
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
