using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A Godot 4 C# game's strings completed from the person's Godot editor open on it, through its IDE messaging
/// protocol, and from Godot's default theme where no editor runs: a Label's theme color completes from the carried table, a
/// name the table lacks is marked, and the script's own node, a Control's class its scene runs on a Label, completes the
/// Label's; with a headless Godot editor of the test's open on the project (its user folders the test's own), an input
/// action only that editor holds, added by its own plugin and never saved, completes in IsActionPressed, and once the editor
/// is gone it is not offered. Its project restores from the packages the Godot found carries, so without a Godot 4 .NET
/// build each test skips saying why. Run alone: one starts a Godot of its own.</summary>
[Collection(Alone.Name)]
public sealed class GodotEditorCompletionTests : IClassFixture<GodotEditorCompletionTests.Game>
{
    private readonly Game game;

    public GodotEditorCompletionTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is not { WhyNot: null, Version: not null, Packages: not null }, game.Skipped);
        game.Reset();
    }

    internal const string Script = """
        using Godot;

        public partial class Main : Control
        {
            public override void _Ready()
            {
                Label score = GetNode<Label>("Score");
                Color color = score.GetThemeColor("font_color");
                Color wrong = score.GetThemeColor("font_colr");
                Color own = GetThemeColor("font_color");
            }

            public override void _Process(double delta)
            {
                if (Input.IsActionPressed("jump"))
                {
                    QueueRedraw();
                }
            }
        }

        """;

    /// <summary>The script's Control class on a Label, so only an editor holding the scene knows the node is a Label.</summary>
    internal const string Scene = """
        [gd_scene format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]

        [node name="Main" type="Label"]
        script = ExtResource("1_main")

        [node name="Score" type="Label" parent="."]

        """;

    /// <summary>An editor plugin that gives the editor's own settings an input action it never saves.</summary>
    internal const string Plugin = """
        @tool
        extends EditorPlugin


        func _enter_tree() -> void:
        	ProjectSettings.set_setting("input/editor_dash", {"deadzone": 0.5, "events": []})

        """;

    internal const string Settings = """

        [editor_plugins]

        enabled=PackedStringArray("res://addons/passenger_test/plugin.cfg")

        [input]

        jump={
        "deadzone": 0.5,
        "events": []
        }

        """;

    private string ScriptPath => game.PathOf("Main.cs");

    /// <summary>Completion after a place in the shown file, once it offers a label it always offers; its labels.</summary>
    private static List<string> Completed(MainWindow window, string after, string always)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains(always), () => $"{always} to be offered after {after}: {string.Join(", ", window.Assist.CompletionLabels)}");
        List<string> labels = [.. window.Assist.CompletionLabels];
        Ui.Press(window, "ESCAPE");
        return labels;
    }

    /// <summary>With no Godot editor open, a Label's theme color completes from Godot's default theme Passenger carries, and a
    /// name it lacks is an error naming the class; the script's own Control completes the Label's, the node its scene runs it on.</summary>
    [AvaloniaFact]
    public void With_no_editor_a_labels_theme_colors_complete_from_godots_default_theme_and_a_name_it_lacks_is_marked()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => GodotSceneTests.Marked(window).Contains("No theme color 'font_colr' is among Label's theme colors in Godot's default theme"),
                () => $"font_colr to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(GodotSceneTests.Marked(window), m => m.Contains("'font_color'", StringComparison.Ordinal));
            List<string> labels = Completed(window, "score.GetThemeColor(\"", "font_color");
            Assert.Contains("font_outline_color", labels);
            Assert.Contains("font_color", OwnColors(window));
            Completed(window, "IsActionPressed(\"", "jump");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>With a headless Godot editor of the test's open on the project and its scene, the input action only that
    /// editor holds completes, project.godot never naming it, and the script's own node's theme colors complete for the
    /// Label the scene runs it on; the editor ended, the action is not offered, the Label's colors still are, as its scene says.</summary>
    [AvaloniaFact]
    public void An_open_editors_unsaved_input_action_and_its_nodes_theme_colors_complete_until_it_ends()
    {
        GodotFound godot = game.Godot!;
        string meta = GodotIdeMessaging.MetaPath(game.Folder);
        if (File.Exists(meta))
        {
            File.Delete(meta);
        }
        MainWindow window = game.Open(ScriptPath);
        Process? editor = null;
        try
        {
            Completed(window, "IsActionPressed(\"", "jump");
            Ui.Settle();
            string log = game.Folder + "-editor.log";
            ProcessStartInfo start = ChildProcesses.Command(godot.Executable,
                [GodotExecutables.Headless, "--editor", "--path", game.Folder, "--log-file", log, "res://main.tscn"], game.Folder);
            foreach ((string name, string value) in game.Homes)
            {
                start.Environment[name] = value;
            }
            editor = ChildProcesses.Start(start);
            Ui.WaitFor(() => File.Exists(meta) || !DotnetProcesses.Runs(editor.Id), () => $"Godot's editor to write {meta}", TimeSpan.FromMinutes(2));
            Assert.True(File.Exists(meta), "Godot's editor ended before it wrote its IDE messaging meta file");

            string Said() => EditorSays(meta, log);
            List<string> actions = Offered(() => Completed(window, "IsActionPressed(\"", "jump"), "editor_dash", "editor_dash, which only Godot's editor holds", Said);
            Assert.DoesNotContain("editor_dash", File.ReadAllText(game.PathOf("project.godot")), StringComparison.Ordinal);
            Assert.Single(actions, a => a == "jump");
            Offered(() => OwnColors(window), "font_color", "the Label's font_color on the script's own node, as Godot's editor answers it", Said);

            ChildProcesses.Kill(editor);
            Ui.WaitFor(() => !DotnetProcesses.Runs(editor.Id), () => "Godot's editor to end", TimeSpan.FromMinutes(1));
            List<string> after = Completed(window, "IsActionPressed(\"", "jump");
            Assert.DoesNotContain("editor_dash", after);
            Assert.Contains("font_color", OwnColors(window));
        }
        finally
        {
            if (editor is not null)
            {
                ChildProcesses.Kill(editor);
            }
            window.Close();
        }
    }

    /// <summary>A list asked for again until it offers a label, the editor having started and loaded its plugins and scene
    /// meanwhile, within two minutes; the list that offered it.</summary>
    private static List<string> Offered(Func<List<string>> asked, string wanted, string what, Func<string> editor)
    {
        Deadline until = Deadline.In(TimeSpan.FromMinutes(2).Scaled());
        List<string> labels;
        while (!(labels = asked()).Contains(wanted))
        {
            Assert.True(!until.Passed, $"Completion never offered {what}: {string.Join(", ", labels)}\n{editor()}");
            Ui.Hold(TimeSpan.FromSeconds(1), "Godot's editor loads its plugins and scene before it answers them");
        }
        return labels;
    }

    /// <summary>What the editor's meta file names, what the editor answers a client of the test's own for the input
    /// actions, and the end of its log: why a list may lack what the editor holds.</summary>
    private static string EditorSays(string meta, string log)
    {
        string named = File.Exists(meta) ? File.ReadAllText(meta).Trim() : "no meta file";
        string answered = "nothing to ask";
        if (GodotIdeMessaging.MetaOf(named) is (int port, _))
        {
            answered = Ui.Await(Waiting.Apart(async () =>
            {
                await using GodotIdeClient? client = await GodotIdeClient.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None);
                if (client is null)
                {
                    return $"no Godot editor answered on port {port}";
                }
                using CancellationTokenSource within = new(TimeSpan.FromSeconds(10));
                try
                {
                    GodotIdeMessage? answer = await client.AskAsync(GodotIdeMessaging.CodeCompletion, GodotIdeMessaging.CompletionAsked(GodotIdeKind.InputActions, "res://Main.cs"), within.Token);
                    return answer is null ? "the connection ended unanswered" : $"{answer.Status}: {answer.Body}";
                }
                catch (OperationCanceledException)
                {
                    return "no answer within 10 s";
                }
            }), "a client of the test's own to ask Godot's editor", TimeSpan.FromSeconds(30));
        }
        string written = File.Exists(log) ? string.Join('\n', File.ReadAllLines(log).TakeLast(30)) : "no log";
        return $"The meta file: {named}\nThe editor answers: {answered}\nIts log ends:\n{written}";
    }

    /// <summary>What completion offers on the script's own node's GetThemeColor, once the list it opens is shown or none is.</summary>
    private static List<string> OwnColors(MainWindow window)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "own = GetThemeColor(\"");
        Ui.Run(window, AppActions.Complete);
        Ui.Settle();
        List<string> labels = [.. window.Assist.CompletionLabels];
        if (window.Assist.IsCompletionOpen)
        {
            Ui.Press(window, "ESCAPE");
        }
        return labels;
    }

    /// <summary>A Godot 4 C# project whose main scene runs a Control's script on a Label, whose project.godot defines an input
    /// action and enables an editor plugin of its own, on the Godot found: its Godot.NET.Sdk the version that Godot carries,
    /// restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-editor-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.AppendAllText(PathOf("project.godot"), Settings);
            Directory.CreateDirectory(PathOf(Path.Combine("addons", "passenger_test")));
            File.WriteAllText(PathOf(Path.Combine("addons", "passenger_test", "plugin.cfg")), "[plugin]\n\nname=\"Passenger test\"\ndescription=\"\"\nauthor=\"\"\nversion=\"1.0\"\nscript=\"plugin.gd\"\n");
            File.WriteAllText(PathOf(Path.Combine("addons", "passenger_test", "plugin.gd")), Plugin);
        }

        public GodotFound? Godot { get; }

        /// <summary>The variables that keep the test's Godot editor's user folders the test's own.</summary>
        public IReadOnlyDictionary<string, string> Homes { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
