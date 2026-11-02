using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The fixes Show Context Actions offers on a Godot 4 game's checks, as a person makes them: a connection to a method
/// the script lacks makes it with the signal's parameters, from the code and from the open scene; a GetNode path no node
/// answers changes to the scene's node near it, and one of another type takes the node's; a property the scene sets that the
/// script lacks is made with the type of its value; an input action, group or autoload the project's files lack is added
/// where Godot looks, once the person agrees to the line it writes. Each mark goes and the game builds. Its project is
/// restored from the packages the Godot found carries, so without a Godot 4 .NET build each test skips saying why.</summary>
public sealed class GodotFixesTests : IClassFixture<GodotFixesTests.Game>
{
    private readonly Game game;

    public GodotFixesTests(Game game)
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
                Label score = GetNode<Label>("Hud/Scor");
                Button typed = GetNode<Button>("Hud/Score");
                bool jumping = Input.IsActionPressed("jump");
                bool enemy = IsInGroup("enemies");
                Node globals = GetNode("/root/Globals");
            }

            private void OnStartPressed()
            {
            }
        }

        """;

    internal const string Globals = """
        using Godot;

        public partial class Globals : Node
        {
        }

        """;

    internal const string Scene = """
        [gd_scene format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")
        Speed = 2.5
        Lives = 3

        [node name="Hud" type="CanvasLayer" parent="."]

        [node name="Score" type="Label" parent="Hud"]

        [node name="Start" type="Button" parent="Hud"]

        [node name="Sound" type="CheckButton" parent="Hud"]

        [connection signal="pressed" from="Hud/Start" to="." method="OnStartPressed"]
        [connection signal="pressed" from="Hud/Start" to="." method="OnQuitPressed"]
        [connection signal="toggled" from="Hud/Sound" to="." method="OnSoundToggled"]

        """;

    private string ScriptPath => game.PathOf("Main.cs");

    private string ScenePath => game.PathOf("main.tscn");

    private string ProjectPath => game.PathOf("project.godot");

    /// <summary>A file's text as the window has it: an open one's in its editor, which a fix changes unsaved, else the disk's.</summary>
    private static string TextOf(MainWindow window, string path) =>
        window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, PathNames.Canonical(path)))?.Text.Snapshot.ToString() ?? File.ReadAllText(path);

    /// <summary>A fix offered at the caret placed after some text, chosen from Show Context Actions' list.</summary>
    private static void Fix(MainWindow window, string after, string fix)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains(fix), () => $"{fix} to be offered after {after}: {string.Join("; ", window.Assist.Offered)} ({window.Status})");
        int index = window.Assist.Offered.ToList().IndexOf(fix);
        for (int i = 0; i < index; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
    }

    /// <summary>The errors the shown file is marked with, once each wanted one is marked.</summary>
    private static List<string> MarkedOnce(MainWindow window, params string[] wanted)
    {
        Ui.WaitFor(() => wanted.All(GodotSceneTests.Marked(window).Contains),
            () => $"{string.Join("; ", wanted)} to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(1));
        return GodotSceneTests.Marked(window);
    }

    private static void Unmarked(MainWindow window, string mark) =>
        Ui.WaitFor(() => !GodotSceneTests.Marked(window).Contains(mark), () => $"'{mark}' to go once fixed: {string.Join("; ", GodotSceneTests.Marked(window))} ({window.Status})",
            TimeSpan.FromMinutes(1));

    /// <summary>The game built through the window, failing with the build's errors where it does not build.</summary>
    private static void Built(MainWindow window)
    {
        BuildOutcome built = Ui.Await(window.BuildAsync(), "the game's build", TimeSpan.FromMinutes(3));
        Assert.True(built.Succeeded, $"the game to build: {built.Message}; {string.Join("; ", built.Problems.Select(p => p.Message))}");
    }

    /// <summary>The scene's connection to a method Main lacks makes it from Main's mark, after the handler the scene connects,
    /// taking what the signal gives; the open scene's connection of a toggle to another makes it from the scene's mark with
    /// the toggle's bool; Undo takes the first back whole; each mark goes and the game builds.</summary>
    [AvaloniaFact]
    public void A_connection_to_a_method_the_script_lacks_makes_it_with_the_signals_parameters_from_the_code_and_the_scene()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            string quit = $"main.tscn:{GodotSceneTests.LineOf(Scene, "method=\"OnQuitPressed\"")} connects Hud/Start's pressed to OnQuitPressed, which Main has no method of";
            MarkedOnce(window, quit);
            Fix(window, "public partial class Ma", "Create method OnQuitPressed() in Main");
            Ui.WaitFor(() => TextOf(window, ScriptPath).Contains("private void OnStartPressed()\n    {\n    }\n\n    private void OnQuitPressed()\n    {\n    }\n", StringComparison.Ordinal),
                () => $"OnQuitPressed to be made after OnStartPressed ({window.Status}):\n{TextOf(window, ScriptPath)}");
            Unmarked(window, quit);

            window.Confirm = (_, _, _) => Task.FromResult(true);
            window.EditorArea.CodeEditor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => TextOf(window, ScriptPath) == Script, () => $"Undo to take the method back whole ({window.Status}):\n{TextOf(window, ScriptPath)}");
            Ui.Run(window, EditorActions.Redo);
            Ui.WaitFor(() => TextOf(window, ScriptPath).Contains("private void OnQuitPressed()", StringComparison.Ordinal), () => $"Redo to make it again ({window.Status})");

            window.OpenFile(ScenePath);
            Ui.Settle();
            string toggled = "Main, a Node2D (Main.cs), has no method OnSoundToggled";
            MarkedOnce(window, toggled);
            Fix(window, "method=\"OnSoundTog", "Create method OnSoundToggled(bool) in Main");
            Ui.WaitFor(() => TextOf(window, ScriptPath).Contains("private void OnSoundToggled(bool toggledOn)", StringComparison.Ordinal),
                () => $"OnSoundToggled to be made taking the toggle's bool ({window.Status}):\n{TextOf(window, ScriptPath)}");
            Unmarked(window, toggled);
            Built(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A GetNode path no node answers changes to the scene's label near it, and a Button asked of the scene's Label
    /// takes Label, with the local it declares; each mark goes and the game builds.</summary>
    [AvaloniaFact]
    public void A_node_path_no_node_answers_takes_the_nearest_and_a_wrong_type_the_nodes_own()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            string missing = $"main.tscn:{GodotSceneTests.LineOf(Scene, "[node name=\"Main\"")} has no node 'Hud/Scor' under Main: Hud has no child Scor";
            string typed = $"'Hud/Score' is a Label in main.tscn:{GodotSceneTests.LineOf(Scene, "[node name=\"Score\"")}, not a Button";
            MarkedOnce(window, missing, typed);
            Fix(window, "GetNode<Label>(\"Hud/Sc", "Change to 'Hud/Score'");
            Ui.WaitFor(() => TextOf(window, ScriptPath).Contains("Label score = GetNode<Label>(\"Hud/Score\");", StringComparison.Ordinal),
                () => $"the path to be the label's ({window.Status})");
            Unmarked(window, missing);

            Fix(window, "GetNode<Button>(\"Hud/Sc", "Change type to Label");
            Ui.WaitFor(() => TextOf(window, ScriptPath).Contains("Label typed = GetNode<Label>(\"Hud/Score\");", StringComparison.Ordinal),
                () => $"the call and its local to take Label ({window.Status}):\n{TextOf(window, ScriptPath)}");
            Unmarked(window, typed);
            Built(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A property the scene sets that Main lacks is made exported with the type of the scene's value, after Main's
    /// other exported property, from Main's mark; the scene's mark of it goes with it, and the game builds.</summary>
    [AvaloniaFact]
    public void A_property_the_scene_sets_that_the_script_lacks_is_made_exported_of_its_values_type()
    {
        MainWindow window = game.Open(ScenePath);
        try
        {
            string lacking = "Main, a Node2D (Main.cs), has no property Lives, so Godot drops its value";
            MarkedOnce(window, lacking);
            TextSnapshot text = window.EditorArea.Active!.Text.Snapshot;
            Marker marker = window.EditorArea.Active.Markers.First(m => m.Message == lacking);
            Assert.Equal("Lives", text.GetText(marker.Range.Start, marker.Range.Length));

            window.OpenFile(ScriptPath);
            Ui.Settle();
            string set = $"main.tscn:{GodotSceneTests.LineOf(Scene, "Lives = 3")} sets Lives on Main, which Main has no property of";
            MarkedOnce(window, set);
            Fix(window, "public partial class Ma", "Create exported property Lives (int) in Main");
            Ui.WaitFor(() => TextOf(window, ScriptPath).Contains("public float Speed { get; set; } = 1;\n\n    [Export]\n    public int Lives { get; set; }\n", StringComparison.Ordinal),
                () => $"Lives to be made after Speed ({window.Status}):\n{TextOf(window, ScriptPath)}");
            Unmarked(window, set);
            window.OpenFile(ScenePath);
            Ui.Settle();
            Unmarked(window, lacking);
            Built(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An input action, a group and an autoload the project's files lack are added where Godot looks once the person
    /// agrees to the line each writes: project.godot's [input] and [autoload], made where it has none, and Main's header in
    /// the scene; declined, nothing is written. Each mark goes.</summary>
    [AvaloniaFact]
    public void An_unknown_action_group_or_autoload_is_added_where_godot_looks_once_the_person_agrees_to_its_line()
    {
        string project = File.ReadAllText(ProjectPath);
        MainWindow window = game.Open(ScriptPath);
        try
        {
            string action = "No input action 'jump' is in project.godot's [input], nor among Godot's own ui_ actions";
            string group = "No group 'enemies' is given to a node by the project's scenes, nor added by its code";
            string autoload = "No autoload 'Globals' is in project.godot's [autoload], nor is a scene's root node so named";
            MarkedOnce(window, action, group, autoload);
            List<(string Title, string Text)> asked = [];
            window.Confirm = (title, text, _) =>
            {
                asked.Add((title, text));
                return Task.FromResult(false);
            };
            Fix(window, "IsActionPressed(\"ju", "Add input action 'jump' to project.godot…");
            Ui.WaitFor(() => asked.Count == 1, "the person to be asked before the action is added");
            Ui.Settle();
            Assert.Equal(project, File.ReadAllText(ProjectPath));
            Assert.Contains("jump={\n\"deadzone\": 0.2,\n\"events\": []\n}", asked[0].Text, StringComparison.Ordinal);

            window.Confirm = (title, text, _) =>
            {
                asked.Add((title, text));
                return Task.FromResult(true);
            };
            Fix(window, "IsActionPressed(\"ju", "Add input action 'jump' to project.godot…");
            Ui.WaitFor(() => TextOf(window, ProjectPath).Contains("[input]\n\njump={\n\"deadzone\": 0.2,\n\"events\": []\n}\n", StringComparison.Ordinal),
                () => $"the action to be added to project.godot ({window.Status}):\n{TextOf(window, ProjectPath)}");
            Unmarked(window, action);

            Fix(window, "IsInGroup(\"ene", "Add group 'enemies' to Main in main.tscn…");
            Ui.WaitFor(() => TextOf(window, ScenePath).Contains("[node name=\"Main\" type=\"Node2D\" groups=[\"enemies\"]]", StringComparison.Ordinal),
                () => $"the group to be given to Main ({window.Status}):\n{TextOf(window, ScenePath)}");
            Assert.Contains("[node name=\"Main\" type=\"Node2D\" groups=[\"enemies\"]]", asked[^1].Text, StringComparison.Ordinal);
            Unmarked(window, group);

            Fix(window, "GetNode(\"/root/Glo", "Add autoload 'Globals' of res://Globals.cs to project.godot…");
            Ui.WaitFor(() => TextOf(window, ProjectPath).Contains("[autoload]\n\nGlobals=\"*res://Globals.cs\"\n", StringComparison.Ordinal),
                () => $"the autoload to be added to project.godot ({window.Status}):\n{TextOf(window, ProjectPath)}");
            Unmarked(window, autoload);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Godot 4 C# project of a main scene on the Godot found, with a second script an autoload would load (its uid
    /// kept beside it, as Godot keeps one), restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-fixes-")
        {
            Godot = GodotExecutables.Find(null);
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.WriteAllText(PathOf("Globals.cs"), Globals);
            File.WriteAllText(PathOf("Globals.cs.uid"), "uid://cglobals1\n");
            File.WriteAllText(PathOf("Main.cs.uid"), "uid://cmain1\n");
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
