using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The strings a Godot 4 game's C# gives Godot's calls naming what the project's own files define, as a person
/// works on them: an input action (project.godot's [input]), a group (a scene's node's), an animation (its player's
/// library), an autoload (project.godot's [autoload], through a GetNode path's /root), a shader parameter (the uniform of
/// its material's shader) and a translation's key (a CSV project.godot lists). For each, completion offers what the
/// project defines, a name it does not is an error saying where Godot looks, Go to Declaration opens its definition, and
/// Rename renames it there and in every string naming it, one step of Undo. Its project restores from the packages the
/// Godot found carries, so without a Godot 4 .NET build each test skips saying why; no Godot runs.</summary>
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

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")

        [node name="Enemy" type="Area2D" parent="." groups=["enemies"]]

        [node name="Anim" type="AnimationPlayer" parent="."]
        libraries/ = SubResource("AnimationLibrary_main")
        autoplay = &"run"

        [node name="Sprite" type="Sprite2D" parent="."]
        material = SubResource("ShaderMaterial_tint")

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
    /// Go to Declaration opens project.godot on the action, and Rename renames it there and in every string naming it.</summary>
    [AvaloniaFact]
    public void An_input_action_completes_from_project_godot_is_checked_leads_to_it_and_is_renamed_there()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            List<string> marked = MarkedOnce(window, "No input action 'jmp' is in project.godot's [input], nor among Godot's own ui_ actions");
            Assert.DoesNotContain(marked, m => m.Contains("'jump'", StringComparison.Ordinal) || m.Contains("ui_accept", StringComparison.Ordinal));
            GodotSceneTests.Hover(window, "jump\")", $"project.godot:{GodotSceneTests.LineOf(File.ReadAllText(ProjectPath), "jump={")}");
            Completed(window, "IsActionPressed(\"", "jump", "ui_accept");
            Declared(window, "IsActionPressed(\"ju", "project.godot", "jump={");
            string project = TextOf(window, ProjectPath);
            Renamed(window, "IsActionPressed(\"ju", "jump", "leap");
            Ui.WaitFor(() => TextOf(window, ProjectPath).Contains("\nleap={", StringComparison.Ordinal), () => $"project.godot to name the action leap: {TextOf(window, ProjectPath)}");
            Assert.Contains("Input.IsActionPressed(\"leap\")", Code(window), StringComparison.Ordinal);
            Undone(window, (ProjectPath, project));
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
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
