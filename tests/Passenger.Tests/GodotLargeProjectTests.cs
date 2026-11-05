using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A Godot project of thousands of scenes and scripts stays as quick to work in as a small one: a key in an open
/// scene, with the pause after it that has its project read again and its marks checked, allocates on the window's thread
/// what one in a project of a few files does. Measures allocations, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotLargeProjectTests
{
    [AvaloniaFact]
    public void A_key_in_a_scene_of_a_project_of_5000_scenes_and_2000_scripts_allocates_on_the_window_thread_what_one_in_a_small_project_does()
    {
        string small = Generated("godot-small-", scenes: 5, scripts: 2);
        string large = Generated("godot-large-", scenes: 5000, scripts: 2000);
        long few = KeyBytes(small);
        long many = KeyBytes(large);
        TestContext.Current.TestOutputHelper?.WriteLine($"Four keys in a scene, each with its pause, allocated {few:N0} bytes on the window's thread in a project of 7 files and {many:N0} in one of 7,000");
        Assert.True(many <= few + 65536,
            $"Four keys in a scene of a project of 5,000 scenes and 2,000 scripts allocated {many:N0} bytes on the window's thread, against {few:N0} in one of 7 files: the project is read again whole as a scene is typed");
    }

    /// <summary>A Godot project of scenes each running one of its scripts, beside a main scene of a few nodes.</summary>
    private static string Generated(string prefix, int scenes, int scripts)
    {
        string folder = TestFolders.Create(prefix);
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Large\"\nrun/main_scene=\"res://main.tscn\"\n");
        for (int i = 0; i < scripts; i++)
        {
            TestFolders.Write(folder, $"scripts/mover_{i}.gd", $"class_name Mover{i}\nextends Node2D\n\nvar speed := {i}.0\n\n\nfunc _process(delta: float) -> void:\n\tposition.x += speed * delta\n");
        }
        for (int i = 0; i < scenes; i++)
        {
            TestFolders.Write(folder, $"scenes/level_{i}.tscn", $"[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://scripts/mover_{i % scripts}.gd\" id=\"1\"]\n\n"
                + $"[node name=\"Level{i}\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\nposition = Vector2({i}, 0)\n");
        }
        TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://scripts/mover_0.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Player\" type=\"Sprite2D\" parent=\".\"]\nposition = Vector2(12, 0)\n");
        return folder;
    }

    /// <summary>What four keys in the main scene's Player position (two digits put in, then taken out) allocate on the
    /// window's thread with the pause after each that has the project read again, the least of three rounds.</summary>
    private static long KeyBytes(string folder)
    {
        MainWindow window = new() { Width = 700, Height = 300, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.Settle();
            Assert.Null(window.OpenFile(Path.Combine(folder, "main.tscn")));
            Ui.Settle();
            Ui.Hold(TimeSpan.FromSeconds(1), "the scene's project read and its marks checked a first time");
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            string text = editor.Document.Snapshot.ToString();
            Ui.PlaceAfter(editor, "position = Vector2(12");
            editor.Focus();
            Action[] keys = [() => window.KeyTextInput("3"), () => window.KeyTextInput("4"), () => Ui.Press(window, "BACK_SPACE", settle: false), () => Ui.Press(window, "BACK_SPACE", settle: false)];
            long[] least = [.. keys.Select(_ => long.MaxValue)];
            for (int round = 0; round < 3; round++)
            {
                for (int k = 0; k < keys.Length; k++)
                {
                    GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                    GC.WaitForPendingFinalizers();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    keys[k]();
                    Ui.Hold(TimeSpan.FromMilliseconds(800), "the typing's pause, after which the scene's project is read again");
                    Ui.Settle();
                    least[k] = Math.Min(least[k], GC.GetAllocatedBytesForCurrentThread() - before);
                }
            }
            Assert.Equal(text, editor.Document.Snapshot.ToString());
            return least.Sum();
        }
        finally
        {
            window.Close();
        }
    }
}
