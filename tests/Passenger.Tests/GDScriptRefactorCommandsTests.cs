using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.Tests;

/// <summary>GDScript's refactorings from the window's Refactor commands on a small Godot project whose scene connects to a
/// function and whose other script calls it, by name and by a string: Extract Function, Extract Variable, Inline Variable,
/// Inline Function, Introduce Parameter and Safe Delete in the script, and Change Signature renaming the function through
/// the other script, the string and the scene; each one command that one Undo takes back whole, every script parsing clean
/// in Passenger's parser and, where a Godot is found, in Godot's own. Godot's server held at its start, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptRefactorCommandsTests
{
    private const string Player = "class_name Player\nextends Node\n\n\nsignal hurt(amount)\n\n\nfunc hit(target, amount = 1):\n\tprint(target, amount)\n\n\n"
        + "func twice(x):\n\treturn x * 2\n\n\nfunc unused_helper():\n\tpass\n\n\nfunc _ready():\n\thit(\"self\")\n\tprint(twice(3) + 1)\n\tvar total = 2 + 3\n\tprint(total * 2)\n";

    private const string Enemy = "extends Node\n\n\nfunc attack(player: Player):\n\tplayer.hit(\"enemy\", 5)\n\tplayer.call(\"hit\", \"far\")\n";

    private const string Scene = "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\"res://player.gd\" id=\"1\"]\n"
        + "[ext_resource type=\"Script\" path=\"res://enemy.gd\" id=\"2\"]\n\n[node name=\"Main\" type=\"Node\"]\n\n[node name=\"Player\" type=\"Node\" parent=\".\"]\n"
        + "script = ExtResource(\"1\")\n\n[node name=\"Enemy\" type=\"Node\" parent=\".\"]\nscript = ExtResource(\"2\")\n\n"
        + "[connection signal=\"hurt\" from=\"Player\" to=\"Player\" method=\"hit\"]\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptRefactorCommandsTests()
    {
        folder = PathNames.Canonical(TestFolders.Create("gdscript-refactor-"));
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Refactor\"\nrun/main_scene=\"res://main.tscn\"\n");
        TestFolders.Write(folder, "player.gd", Player);
        TestFolders.Write(folder, "enemy.gd", Enemy);
        TestFolders.Write(folder, "main.tscn", Scene);
    }

    [AvaloniaFact]
    public void Each_refactoring_changes_every_script_scene_and_string_naming_it_as_one_command_one_undo_takes_back()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? held = godot is { Exists: true } ? window.Session.Web?.HoldNextConfigure(release.Task) : null;
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "player.gd")));
            if (held is not null)
            {
                Ui.AwaitPumped(held, "Godot's GDScript server held at its start", TimeSpan.FromMinutes(1));
            }
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            string Text() => document.Text.Snapshot.ToString();

            // Extract Function on the last two statements, named in its box.
            Select(editor, "\tvar total = 2 + 3\n\tprint(total * 2)\n");
            Named(window, AppActions.ExtractMethod, "show_total");
            Ui.PumpUntil(() => Text().Contains("\tshow_total()\n", StringComparison.Ordinal), "the statements moved into show_total", TimeSpan.FromSeconds(30));
            Assert.EndsWith("\tshow_total()\n\n\nfunc show_total() -> void:\n\tvar total = 2 + 3\n\tprint(total * 2)\n", Text(), StringComparison.Ordinal);
            Clean(Text());
            Undone(window, document, Player);

            // Extract Variable and Inline Variable.
            Select(editor, "twice(3) + 1");
            Named(window, AppActions.IntroduceVariable, "sum");
            Ui.PumpUntil(() => Text().Contains("\tvar sum = twice(3) + 1\n\tprint(sum)\n", StringComparison.Ordinal), "twice(3) + 1 made a variable", TimeSpan.FromSeconds(30));
            Undone(window, document, Player);
            Ui.PlaceAfter(editor, "var tot");
            Ui.Run(window, AppActions.Inline, settle: false);
            Ui.PumpUntil(() => Text().Contains("\tprint((2 + 3) * 2)\n", StringComparison.Ordinal) && !Text().Contains("var total", StringComparison.Ordinal), "total inlined", TimeSpan.FromSeconds(30));
            Undone(window, document, Player);

            // Inline Function: twice's one call given its body, twice taken out.
            Ui.PlaceAfter(editor, "func twi");
            Ui.Run(window, AppActions.Inline, settle: false);
            Ui.PumpUntil(() => Text().Contains("\tprint((3 * 2) + 1)\n", StringComparison.Ordinal) && !Text().Contains("func twice", StringComparison.Ordinal), "twice inlined", TimeSpan.FromSeconds(30));
            Clean(Text());
            Undone(window, document, Player);

            // Introduce Parameter: twice's 2 its default.
            Select(editor, "x * 2", "2");
            Named(window, AppActions.IntroduceParameter, "factor");
            Ui.PumpUntil(() => Text().Contains("func twice(x, factor := 2):\n\treturn x * factor\n", StringComparison.Ordinal), "2 made a parameter", TimeSpan.FromSeconds(30));
            Undone(window, document, Player);

            // Safe Delete: a function nothing names goes; one the scene and the other script name stays, said why.
            Ui.PlaceAfter(editor, "func unused_hel");
            Ui.Run(window, AppActions.SafeDelete, settle: false);
            Ui.PumpUntil(() => !Text().Contains("unused_helper", StringComparison.Ordinal), "unused_helper deleted", TimeSpan.FromSeconds(30));
            Clean(Text());
            Undone(window, document, Player);
            Ui.PlaceAfter(editor, "func hi");
            Ui.Run(window, AppActions.SafeDelete, settle: false);
            Ui.PumpUntil(() => window.Status?.StartsWith("Safe Delete was not applied", StringComparison.Ordinal) == true, "Safe Delete of hit refused", TimeSpan.FromSeconds(30));
            Assert.Contains("a scene's connection names it", window.Status, StringComparison.Ordinal);
            Assert.Equal(Player, Text());

            // Change Signature: a parameter added is refused, as the scene's connection calls it with the signal's arguments.
            Ui.PlaceAfter(editor, "func hi");
            ChangeSignatureDialog adding = Dialog(window);
            Assert.Equal("func hit(target, amount = 1)", adding.Preview);
            Ui.Click(adding.Parameters[0].NameBox, settle: false);
            Ui.Click(adding.AddButton, settle: false);
            adding.KeyTextInput("int");
            Ui.Click(adding.Parameters[1].NameBox, settle: false);
            adding.KeyTextInput("loud");
            Ui.Click(adding.Parameters[1].DefaultBox, settle: false);
            adding.KeyTextInput("2");
            Ui.PumpUntil(() => adding.Preview == "func hit(target, loud: int, amount = 1)", "the new parameter in the preview", TimeSpan.FromSeconds(30));
            Ui.Click(adding.NextButton, settle: false);
            Ui.PumpUntil(() => adding.Problem.Length > 0 || !adding.IsVisible, "Change Signature to answer", TimeSpan.FromSeconds(30));
            Assert.Contains("a scene's connection names it", adding.Problem, StringComparison.Ordinal);
            adding.Close();

            // Change Signature renaming hit: its calls here and in enemy.gd, the string, and the scene's connection, one command.
            Ui.PlaceAfter(editor, "func hi");
            ChangeSignatureDialog renaming = Dialog(window);
            renaming.NameBox.Text = "strike";
            Ui.PumpUntil(() => renaming.Preview == "func strike(target, amount = 1)", "the new name in the preview", TimeSpan.FromSeconds(30));
            Ui.Click(renaming.NextButton, settle: false);
            Ui.PumpUntil(() => !renaming.IsVisible || renaming.Problem.Length > 0, "Change Signature to be made", TimeSpan.FromSeconds(30));
            Assert.False(renaming.IsVisible, $"Change Signature stayed open: {renaming.Problem}");
            Ui.PumpUntil(() => File.ReadAllText(Path.Combine(folder, "main.tscn")).Contains("method=\"strike\"", StringComparison.Ordinal), "the scene's connection renamed",
                TimeSpan.FromSeconds(30));
            Assert.Contains("func strike(target, amount = 1):", Text(), StringComparison.Ordinal);
            Assert.Contains("\tstrike(\"self\")\n", Text(), StringComparison.Ordinal);
            string enemy = File.ReadAllText(Path.Combine(folder, "enemy.gd"));
            Assert.Equal("extends Node\n\n\nfunc attack(player: Player):\n\tplayer.strike(\"enemy\", 5)\n\tplayer.call(\"strike\", \"far\")\n", enemy);
            Clean(Text());
            Clean(enemy);
            Ui.Run(window, AppActions.SaveAll, settle: false);
            Ui.PumpUntil(() => !document.IsModified, "player.gd saved", TimeSpan.FromSeconds(30));
            GodotParses();
            List<string> asked = [];
            window.Confirm = (_, text, _) =>
            {
                asked.Add(text);
                return Task.FromResult(true);
            };
            editor.Focus();
            Ui.Run(window, EditorActions.Undo, settle: false);
            bool undone = Ui.Pumped(() => Text() == Player && File.ReadAllText(Path.Combine(folder, "enemy.gd")) == Enemy && File.ReadAllText(Path.Combine(folder, "main.tscn")) == Scene,
                TimeSpan.FromSeconds(30));
            Assert.Contains("Change Signature", Assert.Single(asked), StringComparison.Ordinal);
            Assert.True(undone, $"One Undo did not take the rename back from both scripts and the scene ({window.Status}):\n{Text()}\n{File.ReadAllText(Path.Combine(folder, "enemy.gd"))}"
                + File.ReadAllText(Path.Combine(folder, "main.tscn")));
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    private static void Select(CodeEditor editor, string text, string? within = null)
    {
        string all = editor.Document.Snapshot.ToString();
        int start = all.IndexOf(text, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The script has no {text}");
        if (within is not null)
        {
            start += text.IndexOf(within, StringComparison.Ordinal);
            text = within;
        }
        editor.Selections = SelectionSet.Single(new Selection(start, start + text.Length));
    }

    /// <summary>A refactoring that makes something run, its name typed in the box it asks it in and Enter pressed.</summary>
    private static void Named(MainWindow window, string action, string name)
    {
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, action, settle: false);
        Ui.PumpUntil(() => window.Assist.Renaming is not null, "the name's box");
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER", settle: false);
    }

    private static ChangeSignatureDialog Dialog(MainWindow window)
    {
        Ui.Run(window, AppActions.ChangeSignature, settle: false);
        Ui.PumpUntil(() => window.OwnedWindows.OfType<ChangeSignatureDialog>().Any() || window.Status?.Contains("Change Signature", StringComparison.Ordinal) == true, "Change Signature's dialog to open",
            TimeSpan.FromSeconds(30));
        return window.OwnedWindows.OfType<ChangeSignatureDialog>().FirstOrDefault() ?? throw new Xunit.Sdk.XunitException($"Change Signature opened no dialog: {window.Status}");
    }

    private static void Undone(MainWindow window, OpenDocument document, string text)
    {
        Ui.Run(window, EditorActions.Undo, settle: false);
        Ui.PumpUntil(() => document.Text.Snapshot.ToString() == text, "one Undo taking the refactoring back", TimeSpan.FromSeconds(30));
    }

    private static void Clean(string script)
    {
        IReadOnlyList<GDScriptError> errors = GDScriptTree.Parse(script).Errors;
        Assert.True(errors.Count == 0, $"The script no longer parses in Passenger's parser: {(errors.Count > 0 ? errors[0].Message : string.Empty)}\n{script}");
    }

    /// <summary>The project's scripts as Godot itself parses them: imported headless (its class cache made), then each
    /// checked, in user folders of the run's own; nothing where no Godot is found.</summary>
    private void GodotParses()
    {
        if (godot is not { Exists: true } found)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"No Godot found, so only Passenger's parser read the scripts: {godot?.WhyNot}");
            return;
        }
        // A copy of the project, so what Godot's import writes stays out of the one the window holds.
        string copy = PathNames.Canonical(TestFolders.Create("gdscript-refactor-godot-"));
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }
        Dictionary<string, string> homes = GodotTests.Game.Home(copy + "-home");
        string log = copy + "-check.log";
        foreach (string[] arguments in (string[][])[[GodotExecutables.Headless, "--path", copy, "--import"],
            [GodotExecutables.Headless, "--path", copy, "--check-only", "--script", "res://enemy.gd", "--log-file", log]])
        {
            ProcessStartInfo start = ChildProcesses.Command(found.Executable, arguments, copy);
            foreach ((string name, string value) in homes)
            {
                start.Environment[name] = value;
            }
            Process process = ChildProcesses.Start(start);
            Ui.PumpUntil(() => !DotnetProcesses.Runs(process.Id), $"Godot to end {string.Join(' ', arguments)}", TimeSpan.FromMinutes(2));
        }
        string said = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
        Assert.True(said.Contains("Godot Engine", StringComparison.Ordinal), $"Godot's check wrote no log of its run: {said}");
        Assert.False(said.Contains("Parse Error", StringComparison.OrdinalIgnoreCase) || said.Contains("SCRIPT ERROR", StringComparison.Ordinal),
            $"Godot found the refactored scripts wrong:\n{said}");
    }
}
