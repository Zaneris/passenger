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
/// the script's function, and Find Usages there lists the C# call and the scene's connection beside what Godot's server finds. Its project restores
/// from the packages the Godot found carries and the script starts GDScript's server in a headless Godot editor, so
/// without a Godot 4 .NET build the test skips saying why; a Godot editor of its own, so run alone.</summary>
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

    private const string Script = """
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

    private const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]
        [ext_resource type="Script" path="res://enemy.gd" id="2_enemy"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")

        [node name="Enemy" type="CharacterBody2D" parent="."]
        script = ExtResource("2_enemy")

        [connection signal="ready" from="." to="Enemy" method="take_damage" binds= [1]]

        """;

    private const string Enemy = "extends CharacterBody2D\n\nsignal died\n\nvar health := 3.0\n\n\nfunc take_damage(amount: int) -> void:\n\thealth -= amount\n"
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
