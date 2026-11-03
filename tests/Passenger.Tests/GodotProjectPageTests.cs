using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A Godot project's page over its project.godot, as a person opens it from the file's row in the Explorer or from
/// Find Action: its main scene, its two autoloads with their files, its input action with its key and button, its export
/// preset, its plugin and its layer each shown; a click on the action goes to its line in project.godot; the plugin's box
/// enables it in project.godot once asked, as one command Undo takes back; a preset added to export_presets.cfg on disk is
/// shown as it comes; and an autoload made a node from the page, once asked, is written so, Undo taking it back. Godot is
/// only asked its version.</summary>
public sealed class GodotProjectPageTests
{
    private const string Project = """
        ; Engine configuration file.
        config_version=5

        [application]

        config/name="Hopper"
        run/main_scene="res://main.tscn"
        config/features=PackedStringArray("4.7")

        [autoload]

        Score="*res://score.gd"
        Music="res://music.tscn"

        [input]

        jump={
        "deadzone": 0.2,
        "events": [Object(InputEventKey,"resource_local_to_scene":false,"resource_name":"","device":-1,"window_id":0,"alt_pressed":false,"shift_pressed":false,"ctrl_pressed":false,"meta_pressed":false,"pressed":false,"keycode":0,"physical_keycode":32,"key_label":0,"unicode":32,"location":0,"echo":false,"script":null)
        , Object(InputEventJoypadButton,"resource_local_to_scene":false,"resource_name":"","device":-1,"button_index":0,"pressure":0.0,"pressed":true,"script":null)
        ]
        }

        [layer_names]

        2d_physics/layer_1="World"

        """;

    private const string Presets = """
        [preset.0]

        name="Desktop"
        platform="Linux"
        runnable=true
        export_filter="all_resources"
        export_path="build/hopper.x86_64"

        [preset.0.options]

        binary_format/architecture="x86_64"

        """;

    [AvaloniaFact]
    public void The_page_shows_the_projects_settings_and_a_click_on_an_action_goes_to_its_line_in_project_godot()
    {
        string folder = TestFolders.Create("godot-page-");
        TestFolders.Write(folder, GodotProject.FileName, Project);
        TestFolders.Write(folder, GodotExports.FileName, Presets);
        TestFolders.Write(folder, "main.tscn", "[gd_scene format=3]\n\n[node name=\"Main\" type=\"Node2D\"]\n");
        TestFolders.Write(folder, "music.tscn", "[gd_scene format=3]\n\n[node name=\"Music\" type=\"AudioStreamPlayer\"]\n");
        TestFolders.Write(folder, "score.gd", "extends Node\n\nvar points := 0\n");
        TestFolders.Write(folder, Path.Combine("addons", "level_tools", "plugin.cfg"), "[plugin]\n\nname=\"Level Tools\"\ndescription=\"\"\nauthor=\"Ann\"\nversion=\"1.2\"\nscript=\"tools.gd\"\n");
        MainWindow window = new()
        {
            Width = 1200, Height = 900, Autosaves = false, StartsLanguageServer = false,
            GodotEnvironment = GodotUserFolders.Under(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home")),
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        string root = PathNames.Canonical(folder);
        string project = Path.Combine(root, GodotProject.FileName);

        ExplorerNode row = window.ExplorerView.Tree.Rows.Single(r => r.Path == project);
        Ui.ChooseFromRowMenu(window, row, "Godot Project");
        GodotPageViewer page = Shown(window);
        string[] said = Said(page);
        foreach (string expected in (string[])["Hopper", "res://main.tscn", "Score", "res://score.gd", "global", "Music", "res://music.tscn", "node", "jump",
            "Space (physical), Joypad Bottom Action", "Desktop", "Level Tools", "1.2 · Ann", "World", "2D physics layer 1"])
        {
            Assert.True(said.Contains(expected), $"The page does not show {expected}: it shows [{string.Join(" | ", said)}]");
        }
        Assert.True(said.Any(s => s.StartsWith("Linux · build/hopper.x86_64 · ", StringComparison.Ordinal)),
            $"The page does not show the preset's platform and path: [{string.Join(" | ", said)}]");

        Ui.ClickButton(page.Body, "jump");
        Ui.WaitFor(() => window.EditorArea.ActiveGroup.GodotPage is null, "the text under the page shown at the action's line");
        int jump = Array.FindIndex(File.ReadAllText(project).Split('\n'), l => l.StartsWith("jump=", StringComparison.Ordinal));
        Assert.Equal(jump, window.EditorArea.CodeEditor.CaretPosition.Line);

        Ui.Run(window, AppActions.FindAction);
        window.KeyTextInput("Godot Project");
        Ui.Press(window, "ENTER");
        page = Shown(window);
        CheckBox plugin = page.Body.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Content as string == "Level Tools");
        Assert.False(plugin.IsChecked, "The page shows Level Tools enabled, which project.godot does not enable");
        Ui.Click(plugin);
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the question before the plugin is enabled");
        Ui.ClickButton(DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single()), "Enable");
        Ui.WaitFor(() => File.ReadAllText(project).Contains("enabled=PackedStringArray(\"res://addons/level_tools/plugin.cfg\")", StringComparison.Ordinal),
            () => $"project.godot to enable the plugin ({window.Status})");
        Ui.WaitFor(() => Shown(window).Body.GetVisualDescendants().OfType<CheckBox>().Any(b => b.Content as string == "Level Tools" && b.IsChecked == true),
            "the plugin's box checked once project.godot enables it");

        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        string Open() => window.EditorArea.Find(project)!.Text.Snapshot.ToString();
        Ui.WaitFor(() => !Open().Contains("editor_plugins", StringComparison.Ordinal), () => $"Undo to take the plugin's enabling back ({window.Status}): {Open()}");
        Ui.WaitFor(() => Shown(window).Body.GetVisualDescendants().OfType<CheckBox>().Any(b => b.Content as string == "Level Tools" && b.IsChecked == false),
            "the plugin's box cleared once Undo took its enabling back");

        File.WriteAllText(Path.Combine(root, GodotExports.FileName), Presets + Presets.Replace("[preset.0", "[preset.1", StringComparison.Ordinal)
            .Replace("name=\"Desktop\"", "name=\"Web\"", StringComparison.Ordinal).Replace("platform=\"Linux\"", "platform=\"Web\"", StringComparison.Ordinal)
            .Replace("build/hopper.x86_64", "build/web/index.html", StringComparison.Ordinal));
        Ui.WaitFor(() => window.EditorArea.ActiveGroup.GodotPage is { File.Facts: { } facts } && facts.Presets.Any(p => p.Preset.Name == "Web"),
            "the page to show the preset added to export_presets.cfg on disk", TimeSpan.FromSeconds(30));

        page = Shown(window);
        Assert.Equal(2, page.Body.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string is GodotPageViewer.MakeGlobalWords or GodotPageViewer.MakeNodeWords));
        Ui.ClickButton(page.Body, GodotPageViewer.MakeNodeWords);
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the question before Score is made a node");
        Ui.ClickButton(DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single()), "Make Node");
        Ui.WaitFor(() => File.ReadAllText(project).Contains("Score=\"res://score.gd\"", StringComparison.Ordinal),
            () => $"project.godot to make Score a node ({window.Status}): {File.ReadAllText(project)}");
        Ui.WaitFor(() => Shown(window).File!.Facts!.Autoloads.Single(a => a.Name == "Score").Global == false, "the page to show Score a node");
        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        Ui.WaitFor(() => Open().Contains("Score=\"*res://score.gd\"", StringComparison.Ordinal), () => $"Undo to make Score a global again ({window.Status}): {Open()}");
        window.Close();
    }

    /// <summary>The page the active group shows, once its facts are read.</summary>
    private static GodotPageViewer Shown(MainWindow window)
    {
        Ui.WaitFor(() => window.EditorArea.ActiveGroup.GodotPage is { File: { IsReading: false, Facts: not null } }, "the Godot project's page, read");
        return window.EditorArea.ActiveGroup.GodotPage!;
    }

    /// <summary>The words the page shows: its texts, links and boxes.</summary>
    private static string[] Said(GodotPageViewer page) =>
        [.. page.Body.GetVisualDescendants().Select(v => v switch
        {
            TextBlock text => text.Text,
            ContentControl { Content: string words } => words,
            _ => null,
        }).OfType<string>()];
}
