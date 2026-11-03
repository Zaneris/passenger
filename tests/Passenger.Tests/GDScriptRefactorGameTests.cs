using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace.GDScript;

namespace Passenger.Tests;

/// <summary>GDScript's refactorings in a Godot 4 C# game, Roslyn's server loaded, whose C# calls a GDScript's function and
/// emits its signal by name and whose scene connects the signal: Change Signature of the function gives the C# Call and
/// CallDeferred their new arguments, Change Signature of the signal its emits in GDScript and C# theirs and its handlers,
/// connected in the script and in the scene, their parameters; Safe Delete refuses an exported variable the scene sets; and
/// Move takes a static function to a class_name script, its call made by that name; each one command one Undo takes back, the scripts parsing clean in Godot itself. Its project
/// restores from the packages the Godot found carries and its scripts start GDScript's server in a headless Godot editor,
/// so without a Godot 4 .NET build each test skips saying why; a Godot editor of its own, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptRefactorGameTests : IClassFixture<GDScriptRefactorGameTests.Game>
{
    private readonly Game game;

    public GDScriptRefactorGameTests(Game game)
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
                enemy.CallDeferred("take_damage", 3);
                enemy.EmitSignal("hurt", 5);
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
        speed = 4.0

        [connection signal="hurt" from="Enemy" to="Enemy" method="on_scene_hurt"]

        """;

    internal const string Enemy = "extends CharacterBody2D\n\nsignal hurt(amount)\n\nvar health := 3.0\n@export var speed := 2.0\n\n\nfunc take_damage(amount: int) -> void:\n\thealth -= amount\n\thurt.emit(amount)\n\n\n"
        + "static func doubled(x):\n\treturn x * 2\n\n\nfunc _ready():\n\thurt.connect(on_hurt)\n\ttake_damage(1)\n\tprint(doubled(2))\n\n\n"
        + "func on_hurt(amount):\n\tprint(amount)\n\n\nfunc on_scene_hurt(amount):\n\tprint(amount * 2)\n";

    internal const string Utils = "class_name Utils\nextends RefCounted\n";

    [AvaloniaFact]
    public void Change_signature_and_move_follow_the_csharp_calls_emits_and_the_scenes_handlers_one_undo_taking_each_back()
    {
        MainWindow window = game.Open(game.PathOf("enemy.gd"), godot: game.Homes);
        try
        {
            window.Confirm = (_, _, _) => Task.FromResult(true);
            OpenDocument enemy = window.EditorArea.Documents.Single(d => d.Name == "enemy.gd");
            CodeEditor editor = window.EditorArea.CodeEditor;
            string Code() => File.ReadAllText(game.PathOf("Main.cs"));

            // A new parameter of take_damage, which every call passes, the C# Call and CallDeferred too.
            Ui.PlaceAfter(editor, "func take_dam");
            ChangeSignatureDialog function = Dialog(window);
            Assert.Equal("func take_damage(amount: int) -> void", function.Preview);
            Ui.Click(function.Parameters[0].NameBox);
            Ui.Click(function.AddButton);
            function.KeyTextInput("int");
            Ui.Click(function.Parameters[1].NameBox);
            function.KeyTextInput("times");
            Ui.Click(function.Parameters[1].DefaultBox);
            function.KeyTextInput("1");
            Ui.Settle();
            Made(function);
            Ui.WaitFor(() => Code().Contains("enemy.Call(\"take_damage\", 2, 1);", StringComparison.Ordinal), () => $"the C# Call given its new argument ({window.Status}):\n{Code()}");
            Assert.Contains("enemy.CallDeferred(\"take_damage\", 3, 1);", Code(), StringComparison.Ordinal);
            Assert.Contains("func take_damage(amount: int, times: int) -> void:", Text(enemy), StringComparison.Ordinal);
            Assert.Contains("\ttake_damage(1, 1)\n", Text(enemy), StringComparison.Ordinal);
            Undone(window, editor, () => Text(enemy) == Enemy && Code() == Script, "Change Signature of take_damage");

            // A new parameter of the signal: its emits here and in C#, and its handlers here and in the scene.
            Ui.PlaceAfter(editor, "signal hu");
            ChangeSignatureDialog signal = Dialog(window);
            Assert.Equal("signal hurt(amount)", signal.Preview);
            Ui.Click(signal.Parameters[0].NameBox);
            Ui.Click(signal.AddButton);
            signal.KeyTextInput("bool");
            Ui.Click(signal.Parameters[1].NameBox);
            signal.KeyTextInput("critical");
            Ui.Click(signal.Parameters[1].DefaultBox);
            signal.KeyTextInput("false");
            Ui.Settle();
            Made(signal);
            Ui.WaitFor(() => Code().Contains("enemy.EmitSignal(\"hurt\", 5, false);", StringComparison.Ordinal), () => $"the C# emit given its new argument ({window.Status}):\n{Code()}");
            string changed = Text(enemy);
            Assert.Contains("signal hurt(amount, critical: bool)\n", changed, StringComparison.Ordinal);
            Assert.Contains("\thurt.emit(amount, false)\n", changed, StringComparison.Ordinal);
            Assert.Contains("func on_hurt(amount, critical: bool):", changed, StringComparison.Ordinal);
            Assert.Contains("func on_scene_hurt(amount, critical: bool):", changed, StringComparison.Ordinal);
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => !enemy.IsModified, "enemy.gd saved");
            GodotParses();
            Undone(window, editor, () => Text(enemy) == Enemy && Code() == Script, "Change Signature of hurt");

            // Safe Delete of the exported speed, which the scene sets on Enemy, is refused saying so.
            Ui.PlaceAfter(editor, "var spe");
            Ui.Run(window, AppActions.SafeDelete);
            Ui.WaitFor(() => window.Status?.StartsWith("Safe Delete was not applied", StringComparison.Ordinal) == true, () => $"Safe Delete of speed refused ({window.Status})");
            Assert.Contains("a scene sets it on Enemy", window.Status, StringComparison.Ordinal);
            Assert.Equal(Enemy, Text(enemy));

            // Move the static doubled to Utils: its call there made by Utils's name.
            Ui.PlaceAfter(editor, "static func doub");
            Ui.Run(window, AppActions.Move);
            Ui.WaitFor(() => window.Assist.Offered.Contains("res://utils.gd"), () => $"Move's list of scripts ({string.Join(", ", window.Assist.Offered)})");
            for (int i = window.Assist.Offered.ToList().IndexOf("res://utils.gd"); i > 0; i--)
            {
                Ui.Press(window, "DOWN");
            }
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Text(enemy).Contains("\tprint(Utils.doubled(2))\n", StringComparison.Ordinal), () => $"doubled moved ({window.Status}):\n{Text(enemy)}");
            Assert.DoesNotContain("func doubled", Text(enemy), StringComparison.Ordinal);
            Assert.Equal(Utils + "\n\nstatic func doubled(x):\n\treturn x * 2\n", File.ReadAllText(game.PathOf("utils.gd")));
            Undone(window, editor, () => Text(enemy) == Enemy && File.ReadAllText(game.PathOf("utils.gd")) == Utils, "Move of doubled");
        }
        finally
        {
            window.Close();
        }
    }

    private static string Text(OpenDocument document) => document.Text.Snapshot.ToString();

    private static ChangeSignatureDialog Dialog(MainWindow window)
    {
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.ChangeSignature);
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChangeSignatureDialog>().Any() || window.Status?.Contains("Change Signature", StringComparison.Ordinal) == true, "Change Signature's dialog to open");
        return window.OwnedWindows.OfType<ChangeSignatureDialog>().FirstOrDefault() ?? throw new Xunit.Sdk.XunitException($"Change Signature opened no dialog: {window.Status}");
    }

    private static void Made(ChangeSignatureDialog dialog)
    {
        Ui.Click(dialog.NextButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem.Length > 0, "Change Signature to be made", TimeSpan.FromMinutes(1));
        Assert.False(dialog.IsVisible, $"Change Signature stayed open: {dialog.Problem}");
    }

    private static void Undone(MainWindow window, CodeEditor editor, Func<bool> back, string what)
    {
        editor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(back, () => $"one Undo to take {what} back whole ({window.Status})", TimeSpan.FromMinutes(1));
    }

    /// <summary>The game's scripts as Godot itself parses them, on a copy of the project of GDScript alone, each checked
    /// headless in user folders of the copy's own.</summary>
    private void GodotParses()
    {
        string copy = PathNames.Canonical(TestFolders.Create("gdscript-refactor-game-"));
        foreach (string file in Directory.EnumerateFiles(game.Folder).Where(f => Path.GetExtension(f) is ".gd" or ".tscn" or ".godot"))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }
        // A copy of GDScript alone, so the import builds no C#.
        File.WriteAllText(Path.Combine(copy, "main.tscn"), Scene.Replace("[ext_resource type=\"Script\" path=\"res://Main.cs\" id=\"1_main\"]\n", string.Empty, StringComparison.Ordinal)
            .Replace("script = ExtResource(\"1_main\")\n", string.Empty, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(copy, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Copy\"\n");
        Dictionary<string, string> homes = GodotTests.Game.Home(copy + "-home");
        // The scripts name no class_name another declares, so Godot parses each with no import first.
        foreach (string script in (string[])["enemy.gd", "utils.gd"])
        {
            string log = $"{copy}-{script}.log";
            string[] arguments = [GodotExecutables.Headless, "--path", copy, "--check-only", "--script", $"res://{script}", "--log-file", log];
            ProcessStartInfo start = ChildProcesses.Command(game.Godot!.Executable, arguments, copy);
            foreach ((string name, string value) in homes)
            {
                start.Environment[name] = value;
            }
            Process process = ChildProcesses.Start(start);
            Ui.WaitFor(() => !DotnetProcesses.Runs(process.Id), () => $"Godot to end {string.Join(' ', arguments)}", TimeSpan.FromMinutes(2));
            string said = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
            Assert.True(said.Contains("Godot Engine", StringComparison.Ordinal), $"Godot's check of {script} wrote no log of its run: {said}");
            Assert.False(said.Contains("Parse Error", StringComparison.OrdinalIgnoreCase) || said.Contains("SCRIPT ERROR", StringComparison.Ordinal),
                $"Godot found {script} wrong after the refactoring:\n{said}");
        }
    }

    /// <summary>A Godot 4 C# project whose main scene's Enemy runs a GDScript, beside a class_name script, on the Godot
    /// found, restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("gdscript-refactor-game-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Folder + "-home");
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.WriteAllText(PathOf("enemy.gd"), Enemy);
            File.WriteAllText(PathOf("utils.gd"), Utils);
        }

        public GodotFound? Godot { get; }

        /// <summary>The user folders the Godot editor answering GDScript runs in.</summary>
        public IReadOnlyDictionary<string, string> Homes { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
