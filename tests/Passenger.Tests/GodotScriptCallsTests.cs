using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A Godot 4 game's C# naming a GDScript's members by string, on a node its scene runs enemy.gd on: a call of a
/// function the script lacks and a connection to a signal it lacks are errors where the strings are written, those it
/// declares, its engine class's own method and its variable are not; Go to Declaration from the function's string goes to
/// the script's function, and Find Usages there lists the C# call and the scene's connection beside what Godot's server
/// finds; the string completes the script's functions and hovers the one it names, and a rename of the function in its
/// script renames the C# string and the scene's connection too, one step of Undo. Its project restores from the packages
/// the Godot found carries and the script starts GDScript's server in a headless Godot editor, so without a Godot 4 .NET
/// build each test skips saying why; a Godot editor of its own, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotScriptCallsTests : IClassFixture<GodotScriptCallsTests.Game>
{
    private readonly Game game;

    public GodotScriptCallsTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is not { WhyNot: null, Version: not null, Packages: not null }, game.Skipped);
        game.Reset();
    }

    internal const string Script = """
        using Godot;

        public partial class Main : Node2D
        {
            public override void _Ready()
            {
                Node enemy = GetNode("Enemy");
                enemy.Call("take_damage", 2);
                enemy.Call("take_damag", 2);
                enemy.Call("move_and_slide");
                GetNode("Enemy").Connect("died", new Callable(this, MethodName.OnDied));
                GetNode("Enemy").Connect("dyed", new Callable(this, MethodName.OnDied));
                float health = (float)GetNode("Enemy").Get("health");
            }

            private void OnDied()
            {
            }
        }

        """;

    internal const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]
        [ext_resource type="Script" path="res://enemy.gd" id="2_enemy"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")

        [node name="Enemy" type="CharacterBody2D" parent="."]
        script = ExtResource("2_enemy")

        [connection signal="ready" from="." to="Enemy" method="take_damage" binds= [1]]

        """;

    internal const string Enemy = "extends CharacterBody2D\n\nsignal died\n\nvar health := 3.0\n\n\nfunc take_damage(amount: int) -> void:\n\thealth -= amount\n"
        + "\tif health <= 0:\n\t\tdied.emit()\n";

    [AvaloniaFact]
    public void A_csharp_string_naming_a_gdscripts_function_is_checked_leads_to_it_and_is_among_its_usages()
    {
        MainWindow window = game.Open(game.PathOf("Main.cs"), godot: game.Homes);
        try
        {
            string function = "Enemy, a CharacterBody2D (enemy.gd), has no method 'take_damag'";
            string signal = "Enemy, a CharacterBody2D (enemy.gd), has no signal 'dyed'";
            Ui.WaitFor(() => GodotSceneTests.Marked(window) is { } marked && marked.Contains(function) && marked.Contains(signal),
                () => $"the strings naming what enemy.gd lacks to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(2));
            Marker missing = window.EditorArea.Active!.Markers.First(m => m.Message == function);
            Assert.Equal("take_damag", window.EditorArea.Active.Text.Snapshot.GetText(missing.Range.Start, missing.Range.Length));
            Assert.Equal([function, signal], GodotSceneTests.Marked(window).Where(m => m.Contains("enemy.gd", StringComparison.Ordinal)));

            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "enemy.Call(\"take_d");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "enemy.gd", () => $"Go to Declaration from the string to open enemy.gd ({window.Status})");
            Assert.StartsWith("func take_damage", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);

            OpenDocument script = window.EditorArea.Active!;
            Ui.WaitFor(() => window.Session.Web?.For(script)?.Lsp is not null && window.Session.Web.StateOf(script) is null,
                () => $"Godot's server for enemy.gd: {window.Session.Web?.StateOf(script)} {script.Notice}", TimeSpan.FromSeconds(90));
            Ui.PlaceAfter(editor, "func take_dam");
            Ui.Run(window, AppActions.FindUsages);
            int called = Script.Split('\n').ToList().FindIndex(l => l.Contains("enemy.Call(\"take_damage\"", StringComparison.Ordinal));
            Ui.WaitFor(() => window.Find.Rows.Any(r => r.Usage is { } u && u.Path.EndsWith("Main.cs", StringComparison.Ordinal) && u.Line == called),
                () => $"Find Usages of take_damage to list the C# call ({window.Status}): {string.Join("; ", window.Find.Rows.Select(r => r.Usage).OfType<Usage>().Select(u => $"{u.Path}:{u.Line + 1}"))}",
                TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(window.Find.Rows, r => r.Usage is { } u && u.Path.EndsWith("Main.cs", StringComparison.Ordinal) && u.Line == called + 1);
            int connected = Scene.Split('\n').ToList().FindIndex(l => l.Contains("method=\"take_damage\"", StringComparison.Ordinal));
            Assert.Contains(window.Find.Rows, r => r.Usage is { } u && u.Path.EndsWith("main.tscn", StringComparison.Ordinal) && u.Line == connected);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The C# string completes enemy.gd's functions and hovers take_damage's declaration; renamed in enemy.gd,
    /// take_damage is renamed in the script by Godot's server, in the C# string calling it and in the scene's connection,
    /// the misspelt string left, and Undo puts every file back.</summary>
    [AvaloniaFact]
    public void A_gdscripts_function_completes_and_hovers_in_a_csharp_string_and_its_rename_follows_the_strings_and_scenes_naming_it()
    {
        MainWindow window = game.Open(game.PathOf("Main.cs"), godot: game.Homes);
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => GodotSceneTests.Marked(window).Contains("Enemy, a CharacterBody2D (enemy.gd), has no method 'take_damag'"),
                () => $"Main.cs to be read against its scene: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(2));
            Ui.PlaceAfter(editor, "enemy.Call(\"");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("take_damage"), () => $"enemy.gd's take_damage offered in the string: {string.Join(", ", window.Assist.CompletionLabels)}");
            Assert.DoesNotContain("health", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE");
            GodotSceneTests.Hover(window, "take_damage\", 2)", "func take_damage(amount: int) -> void");

            string code = game.PathOf("Main.cs");
            string scene = game.PathOf("main.tscn");
            string enemy = game.PathOf("enemy.gd");
            Assert.Null(window.OpenFile(enemy));
            OpenDocument script = window.EditorArea.Active!;
            Ui.WaitFor(() => window.Session.Web?.For(script)?.Lsp is not null && window.Session.Web.StateOf(script) is null,
                () => $"Godot's server for enemy.gd: {window.Session.Web?.StateOf(script)} {script.Notice}", TimeSpan.FromSeconds(90));
            Ui.PlaceAfter(editor, "func take_dam");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "take_damage", () => $"Rename's box on take_damage ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("hurt");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => TextOf(window, code).Contains("enemy.Call(\"hurt\", 2)", StringComparison.Ordinal) && TextOf(window, scene).Contains("method=\"hurt\"", StringComparison.Ordinal)
                && TextOf(window, enemy).Contains("func hurt(amount: int)", StringComparison.Ordinal),
                () => $"take_damage renamed in the script, the C# string and the scene ({window.Status}):\n{TextOf(window, code)}\n{TextOf(window, scene)}\n{TextOf(window, enemy)}", TimeSpan.FromMinutes(1));
            Assert.Contains("enemy.Call(\"take_damag\", 2)", TextOf(window, code), StringComparison.Ordinal);

            window.Confirm = (_, _, _) => Task.FromResult(true);
            editor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => TextOf(window, code) == Script && TextOf(window, scene) == Scene && TextOf(window, enemy) == Enemy,
                () => $"Undo to put Main.cs, main.tscn and enemy.gd back ({window.Status})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A GDScript function with a rest parameter takes any count past the ones it requires (#1211): a scene's
    /// connection of a signal giving it three is no mark, one giving none is; a C# Call giving it one is no mark, one
    /// giving none is, where the string is written.</summary>
    [AvaloniaFact]
    public void A_function_with_a_rest_parameter_takes_any_count_from_a_scenes_connection_and_a_csharp_call()
    {
        File.WriteAllText(game.PathOf("enemy.gd"), Enemy + "\n\nsignal talked(a, b, c)\nsignal hushed()\n\n\nfunc shout(first, ...rest) -> void:\n\tprint(first, rest)\n");
        File.WriteAllText(game.PathOf("main.tscn"), Scene + "[connection signal=\"talked\" from=\"Enemy\" to=\"Enemy\" method=\"shout\"]\n"
            + "[connection signal=\"hushed\" from=\"Enemy\" to=\"Enemy\" method=\"shout\"]\n");
        string calls = Script.Replace("enemy.Call(\"move_and_slide\");", "enemy.Call(\"move_and_slide\");\n        enemy.Call(\"shout\", 1);\n        enemy.Call(\"shout\");", StringComparison.Ordinal);
        File.WriteAllText(game.PathOf("Main.cs"), calls);
        MainWindow window = game.Open(game.PathOf("Main.cs"), godot: game.Homes);
        try
        {
            string noneGiven = "'shout' takes at least 1 (first, ...rest), but this call gives it none";
            Ui.WaitFor(() => GodotSceneTests.Marked(window).Contains(noneGiven),
                () => $"the call giving shout none to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(2));
            OpenDocument code = window.EditorArea.Active!;
            Marker marked = Assert.Single(code.Markers, m => m.Message.Contains("'shout'", StringComparison.Ordinal));
            int line = calls.Split('\n').ToList().FindIndex(l => l.Contains("enemy.Call(\"shout\");", StringComparison.Ordinal));
            Assert.Equal((line, "shout"), (code.Text.Snapshot.GetLineFromOffset(marked.Range.Start), code.Text.Snapshot.GetText(marked.Range.Start, marked.Range.Length)));

            Assert.Null(window.OpenFile(game.PathOf("main.tscn")));
            string none = "hushed gives no arguments, but shout takes at least 1 (first, ...rest)";
            Ui.WaitFor(() => GodotSceneTests.Marked(window).Contains(none),
                () => $"the connection giving shout none to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(GodotSceneTests.Marked(window), m => m.Contains("talked", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file's text as the window has it: an open one's in its editor, which a rename changes unsaved, else the disk's.</summary>
    private static string TextOf(MainWindow window, string path) =>
        window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, PathNames.Canonical(path)))?.Text.Snapshot.ToString() ?? File.ReadAllText(path);

    /// <summary>A Godot 4 C# project whose main scene's Enemy runs a GDScript, on the Godot found, restored from that Godot's
    /// own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-script-calls-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Folder + "-home");
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.WriteAllText(PathOf("enemy.gd"), Enemy);
        }

        public GodotFound? Godot { get; }

        /// <summary>The user folders the Godot editor answering GDScript runs in.</summary>
        public IReadOnlyDictionary<string, string> Homes { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
