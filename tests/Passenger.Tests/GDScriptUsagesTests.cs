using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Find Usages and Rename of a GDScript's member or local by Passenger's own reading of the project's scripts,
/// Godot's server held at its start where a Godot is found (never started where none is): a function's calls in another
/// script, by name and by a string, and the scene's connection to it are listed and renamed together as one command one
/// Undo takes back, a local's only in its function; a rename a use Passenger can't see would leave wrong asks first saying
/// where. A Godot editor of its own each where one is found, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptUsagesTests
{
    private const string Player = "class_name Player\nextends Node\n\n\nsignal hurt(amount)\n\n\nfunc hit(target, amount = 1):\n\tprint(target, amount)\n\n\n"
        + "func _ready():\n\thit(\"self\")\n\tvar total = 2 + 3\n\tprint(total * 2)\n";

    private const string Enemy = "extends Node\n\n\nfunc attack(player: Player):\n\tplayer.hit(\"enemy\", 5)\n\tplayer.call(\"hit\", \"far\")\n";

    private const string Scene = "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\"res://player.gd\" id=\"1\"]\n"
        + "[ext_resource type=\"Script\" path=\"res://enemy.gd\" id=\"2\"]\n\n[node name=\"Main\" type=\"Node\"]\n\n[node name=\"Player\" type=\"Node\" parent=\".\"]\n"
        + "script = ExtResource(\"1\")\n\n[node name=\"Enemy\" type=\"Node\" parent=\".\"]\nscript = ExtResource(\"2\")\n\n"
        + "[connection signal=\"hurt\" from=\"Player\" to=\"Player\" method=\"hit\"]\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptUsagesTests()
    {
        folder = PathNames.Canonical(TestFolders.Create("gdscript-usages-"));
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Usages\"\nrun/main_scene=\"res://main.tscn\"\n");
        TestFolders.Write(folder, "player.gd", Player);
        TestFolders.Write(folder, "enemy.gd", Enemy);
        TestFolders.Write(folder, "main.tscn", Scene);
    }

    [AvaloniaFact]
    public void Find_usages_lists_a_functions_calls_in_another_script_its_string_and_the_scenes_connection_and_a_locals_in_its_function()
    {
        (MainWindow window, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            FindView find = window.Find;
            Ui.PlaceAfter(editor, "func hi");
            Ui.Run(window, AppActions.FindUsages, settle: false);
            string[] expected = ["func hit(target, amount = 1):", "hit(\"self\")", "player.hit(\"enemy\", 5)", "player.call(\"hit\", \"far\")",
                "[connection signal=\"hurt\" from=\"Player\" to=\"Player\" method=\"hit\"]"];
            bool listed = Ui.Pumped(() => expected.All(e => Usages(find).Contains(e)), TimeSpan.FromSeconds(60));
            Assert.True(listed, $"hit's usages in both scripts, its string and the scene's connection; the Find window lists {string.Join(" | ", Usages(find))} ({window.Status})");

            Ui.PlaceAfter(editor, "var tot");
            Ui.Run(window, AppActions.FindUsages, settle: false);
            listed = Ui.Pumped(() => find.Shown == "Usages of total" && Usages(find).SequenceEqual(["var total = 2 + 3", "print(total * 2)"]), TimeSpan.FromSeconds(30));
            Assert.True(listed, $"total's usages in its function alone; the Find window lists {string.Join(" | ", Usages(find))}");
            Assert.True(window.Session.Web?.For(window.EditorArea.Active!) is not { Serving: true }, "Godot's server answered: the test's hold of its start did not hold");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>Rename of the function takes its calls in the other script, by name and by its string, and the scene's
    /// connection with it, as one command one Undo takes back; a local's rename stays in its function.</summary>
    [AvaloniaFact]
    public void Rename_renames_a_function_through_the_other_script_its_string_and_the_scene_as_one_command()
    {
        (MainWindow window, TaskCompletionSource release) = Open();
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            string enemy = Path.Combine(folder, "enemy.gd");
            string scene = Path.Combine(folder, "main.tscn");
            Ui.PlaceAfter(editor, "func hi");
            Renamed(window, "strike");
            bool renamed = Ui.Pumped(() => document.Text.Snapshot.ToString().Contains("func strike(target", StringComparison.Ordinal)
                && File.ReadAllText(enemy).Contains("\tplayer.strike(\"enemy\", 5)\n\tplayer.call(\"strike\", \"far\")\n", StringComparison.Ordinal)
                && File.ReadAllText(scene).Contains("method=\"strike\"", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            Assert.True(renamed, $"hit renamed strike in both scripts, the string and the scene ({window.Status}):\n{document.Text.Snapshot}\n{File.ReadAllText(enemy)}\n{File.ReadAllText(scene)}");
            Assert.Contains("\tstrike(\"self\")\n", document.Text.Snapshot.ToString(), StringComparison.Ordinal);

            List<string> asked = [];
            window.Confirm = (_, text, _) =>
            {
                asked.Add(text);
                return Task.FromResult(true);
            };
            editor.Focus();
            Ui.Run(window, EditorActions.Undo, settle: false);
            bool undone = Ui.Pumped(() => document.Text.Snapshot.ToString() == Player && File.ReadAllText(enemy) == Enemy && File.ReadAllText(scene) == Scene, TimeSpan.FromSeconds(30));
            Assert.True(undone, $"One Undo did not take the rename back from both scripts and the scene ({window.Status})");
            Assert.Contains("Rename hit to strike", Assert.Single(asked), StringComparison.Ordinal);

            Ui.PlaceAfter(editor, "var tot");
            Renamed(window, "sum");
            bool local = Ui.Pumped(() => document.Text.Snapshot.ToString().EndsWith("\tvar sum = 2 + 3\n\tprint(sum * 2)\n", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.True(local, $"total renamed sum in its function ({window.Status}):\n{document.Text.Snapshot}");
            Assert.Equal(Enemy, File.ReadAllText(enemy));
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>A script calling the function on a value of no type written may call it unseen, so its rename asks first,
    /// saying where, and declined changes nothing.</summary>
    [AvaloniaFact]
    public void A_rename_with_a_use_passenger_cant_see_asks_first_naming_it_and_declined_changes_nothing()
    {
        TestFolders.Write(folder, "other.gd", "extends Node\n\n\nfunc poke(thing):\n\tthing.hit(1)\n");
        (MainWindow window, TaskCompletionSource release) = Open();
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            List<string> asked = [];
            window.Confirm = (_, text, _) =>
            {
                asked.Add(text);
                return Task.FromResult(false);
            };
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "func hi");
            Renamed(window, "strike");
            bool asks = Ui.Pumped(() => asked.Count > 0, TimeSpan.FromSeconds(60));
            Assert.True(asks, $"The rename asked nothing: {window.Status}");
            Assert.True(Assert.Single(asked).Contains("other.gd:5: its 'hit' is on a value whose script is not read", StringComparison.Ordinal),
                $"The question named no place Passenger can't see: {asked[0]}");
            Assert.Equal(Player, document.Text.Snapshot.ToString());
            Assert.Equal(Enemy, File.ReadAllText(Path.Combine(folder, "enemy.gd")));
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    private static IReadOnlyList<string> Usages(FindView find) => [.. find.Rows.Where(r => r.Usage is not null).Select(r => r.Label)];

    /// <summary>Rename at the caret, the new name typed in its box and Enter pressed.</summary>
    private static void Renamed(MainWindow window, string name)
    {
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.Rename, settle: false);
        Ui.PumpUntil(() => window.Assist.Renaming is not null, "the rename's box");
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER", settle: false);
    }

    private (MainWindow Window, TaskCompletionSource Release) Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home"), GodotHeadlessAtOpen = true };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? held = godot is { Exists: true } ? window.Session.Web?.HoldNextConfigure(release.Task) : null;
        Assert.Null(window.OpenFile(Path.Combine(folder, "player.gd")));
        if (held is not null)
        {
            Ui.AwaitPumped(held, "Godot's GDScript server held at its start", TimeSpan.FromMinutes(1));
        }
        return (window, release);
    }
}
