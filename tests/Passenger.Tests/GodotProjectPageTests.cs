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
/// shown as it comes; an autoload made a node from the page, once asked, is written so, Undo taking it back; and, each once
/// asked, another scene made the main scene, an action added and given a mouse button, an event of an action taken out, an
/// action taken out, a layer named and another's name cleared, each written where Godot saves it, Undo taking the last back;
/// what Godot's editor saves there while the gate asks stays under the page's changes. Godot is only asked its version.</summary>
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
            "2 events", "Space (physical)", "Joypad Bottom Action", "Desktop", "Level Tools", "1.2 · Ann", "World", "2D physics layer 1"])
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

        Choose(window, GodotPageViewer.MainSceneName, item => item is GodotPageScene { Res: "res://music.tscn" });
        Ui.ClickButton(Shown(window).Body, GodotPageViewer.MakeMainWords);
        Agree(window, "Make Main Scene");
        Written(window, project, "run/main_scene=\"res://music.tscn\"\nconfig/features", "the main scene made music.tscn in its place");

        Ui.Click(Named<TextBox>(window, GodotPageViewer.NewActionName));
        window.KeyTextInput("dash");
        Ui.ClickButton(Shown(window).Body, GodotPageViewer.AddActionWords);
        Agree(window, "Add Action");
        Written(window, project, "}\ndash={\n\"deadzone\": 0.2,\n\"events\": []\n}\n\n[layer_names]", "the action dash added last to the input map");
        Choose(window, GodotPageViewer.EventActionName, item => item is GodotPageAction { Name: "dash" });
        Choose(window, GodotPageViewer.NewEventName, item => item as string == "Left Mouse Button");
        Ui.ClickButton(Shown(window).Body, GodotPageViewer.AddEventWords);
        Agree(window, "Add Event");
        Written(window, project, "dash={\n\"deadzone\": 0.2,\n\"events\": [Object(InputEventMouseButton,", "dash given the left mouse button");
        Ui.WaitFor(() => Shown(window).File!.Facts!.Actions.Single(a => a.Name == "dash").Events.SequenceEqual(["Left Mouse Button"]), "the page to show dash's button");

        Ui.Click(LinkBeside(Shown(window), "Joypad Bottom Action", GodotPageViewer.RemoveEventWords));
        Agree(window, "Remove Event");
        Ui.WaitFor(() => !File.ReadAllText(project).Contains("InputEventJoypadButton", StringComparison.Ordinal), () => $"jump's joypad button taken out ({window.Status}): {File.ReadAllText(project)}");
        Assert.Contains("\"physical_keycode\":32", File.ReadAllText(project), StringComparison.Ordinal);
        Ui.Click(LinkBeside(Shown(window), "dash", GodotPageViewer.RemoveActionWords));
        Agree(window, "Remove Action");
        Ui.WaitFor(() => !File.ReadAllText(project).Contains("dash=", StringComparison.Ordinal), () => $"dash taken out of the input map ({window.Status}): {File.ReadAllText(project)}");

        Choose(window, GodotPageViewer.LayerKindName, item => item as string == "3d_physics");
        Choose(window, GodotPageViewer.LayerNumberName, item => item as string == "2");
        Ui.Click(Named<TextBox>(window, GodotPageViewer.LayerNameName));
        window.KeyTextInput("Hit");
        Ui.ClickButton(Shown(window).Body, GodotPageViewer.NameLayerWords);
        Agree(window, "Name Layer");
        Written(window, project, "2d_physics/layer_1=\"World\"\n3d_physics/layer_2=\"Hit\"\n", "3D physics layer 2 named Hit after 2D physics layer 1");
        Ui.Click(LinkBeside(Shown(window), "World", GodotPageViewer.ClearLayerWords));
        Agree(window, "Clear Name");
        Written(window, project, "[layer_names]\n\n3d_physics/layer_2=\"Hit\"\n", "2D physics layer 1's name taken out");
        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        Ui.WaitFor(() => Open().Contains("2d_physics/layer_1=\"World\"", StringComparison.Ordinal), () => $"Undo to name 2D physics layer 1 World again ({window.Status}): {Open()}");
        window.Close();
    }

    /// <summary>The page's list of a name with the item chosen, as a person picks it.</summary>
    private static void Choose(MainWindow window, string name, Func<object?, bool> item)
    {
        ComboBox list = Named<ComboBox>(window, name);
        list.SelectedItem = list.ItemsSource!.Cast<object?>().First(item);
        Ui.Settle();
    }

    /// <summary>The page's control of a name.</summary>
    private static T Named<T>(MainWindow window, string name)
        where T : Control => Shown(window).Body.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>The link at the end of the page's row that shows the words given.</summary>
    private static Button LinkBeside(GodotPageViewer page, string words, string link) => page.Body.GetVisualDescendants().OfType<Grid>()
        .First(g => g.ColumnDefinitions.Count == 3 && g.Children.Any(c => c is TextBlock { Text: { } text } && text == words || c is Button { Content: string said } && said == words))
        .Children.OfType<Button>().Single(b => b.Content as string == link);

    /// <summary>The question before a change of project.godot answered with the words given.</summary>
    private static void Agree(MainWindow window, string yes)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), $"the question answered {yes}");
        Ui.ClickButton(DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single()), yes);
    }

    /// <summary>project.godot on disk once it holds the text given.</summary>
    private static void Written(MainWindow window, string project, string text, string what) =>
        Ui.WaitFor(() => File.ReadAllText(project).Contains(text, StringComparison.Ordinal), () => $"project.godot to have {what} ({window.Status}): {File.ReadAllText(project)}");

    /// <summary>A main scene and an autoload project.godot names by their uids, as Godot 4.4 and newer write them, are shown by
    /// their res:// paths.</summary>
    [AvaloniaFact]
    public void A_main_scene_and_an_autoload_named_by_their_uids_show_as_their_paths()
    {
        string folder = TestFolders.Create("godot-page-uids-");
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Hopper\"\nrun/main_scene=\"uid://cmainhop\"\n"
            + "config/features=PackedStringArray(\"4.7\")\n\n[autoload]\n\nScore=\"*uid://cscorehop\"\n");
        TestFolders.Write(folder, "main.tscn", "[gd_scene format=3 uid=\"uid://cmainhop\"]\n\n[node name=\"Main\" type=\"Node2D\"]\n");
        TestFolders.Write(folder, "score.gd", "extends Node\n\nvar points := 0\n");
        TestFolders.Write(folder, "score.gd.uid", "uid://cscorehop\n");
        MainWindow window = new()
        {
            Width = 1200, Height = 900, Autosaves = false, StartsLanguageServer = false,
            GodotEnvironment = GodotUserFolders.Under(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home")),
        };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.Settle();
            string project = Path.Combine(PathNames.Canonical(folder), GodotProject.FileName);
            Ui.ChooseFromRowMenu(window, window.ExplorerView.Tree.Rows.Single(r => r.Path == project), "Godot Project");
            string[] said = Said(Shown(window));
            Assert.True(said.Contains("res://main.tscn") && said.Contains("res://score.gd"), $"The page does not show the main scene and autoload by their paths: [{string.Join(" | ", said)}]");
            Assert.False(said.Any(s => s.Contains("uid://", StringComparison.Ordinal)), $"The page shows a uid where its file is known: [{string.Join(" | ", said)}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What Godot's editor saves in project.godot while the gate asks to write over what it holds there (a hold
    /// the test stands in, as the addon would say it) stays: the page's plugin box and its new action each write their
    /// change into project.godot as that save left it.</summary>
    [AvaloniaFact]
    public void What_godot_saves_while_the_gate_asks_stays_under_the_pages_changes()
    {
        string folder = TestFolders.Create("godot-page-held-");
        TestFolders.Write(folder, GodotProject.FileName, Project);
        TestFolders.Write(folder, Path.Combine("addons", "level_tools", "plugin.cfg"), "[plugin]\n\nname=\"Level Tools\"\ndescription=\"\"\nauthor=\"Ann\"\nversion=\"1.2\"\nscript=\"tools.gd\"\n");
        MainWindow window = new()
        {
            Width = 1200, Height = 900, Autosaves = false, StartsLanguageServer = false,
            GodotEnvironment = GodotUserFolders.Under(Path.Combine(folder, "..", Path.GetFileName(folder) + "-home")),
        };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.Settle();
            string project = Path.Combine(PathNames.Canonical(folder), GodotProject.FileName);
            window.EditorArea.HeldElsewhere = path => PathNames.Same(PathNames.Canonical(path), project) ? "Godot's editor holds project.godot" : null;
            List<string> asked = [];
            window.Confirm = (title, _, _) =>
            {
                asked.Add(title);
                if (title == "Write Over What Godot Holds?")
                {
                    // As the person reads the gate's question, Godot's editor saves the project's settings.
                    File.WriteAllText(project, File.ReadAllText(project).Replace("config/name=\"Hopper\"\n", $"config/name=\"Hopper\"\nconfig/saved_{asked.Count}=true\n",
                        StringComparison.Ordinal));
                }
                return Task.FromResult(true);
            };
            Ui.ChooseFromRowMenu(window, window.ExplorerView.Tree.Rows.Single(r => r.Path == project), "Godot Project");

            Ui.Click(Shown(window).Body.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Content as string == "Level Tools"));
            Written(window, project, "enabled=PackedStringArray(\"res://addons/level_tools/plugin.cfg\")", "the plugin enabled");
            Ui.Click(Named<TextBox>(window, GodotPageViewer.NewActionName));
            window.KeyTextInput("dash");
            Ui.ClickButton(Shown(window).Body, GodotPageViewer.AddActionWords);
            Written(window, project, "dash={", "the action dash added");

            string text = File.ReadAllText(project);
            Assert.True(asked.SequenceEqual(["Enable Plugin?", "Write Over What Godot Holds?", "Add Input Action?", "Write Over What Godot Holds?"]),
                $"Each change was to be asked, then the gate to ask before writing over what Godot holds; asked: {string.Join(" | ", asked)}");
            Assert.True(text.Contains("config/saved_2=true\n", StringComparison.Ordinal) && text.Contains("config/saved_4=true\n", StringComparison.Ordinal)
                && text.Contains("enabled=PackedStringArray(\"res://addons/level_tools/plugin.cfg\")", StringComparison.Ordinal),
                $"project.godot was to keep what Godot's editor saved while each write's gate asked, under the plugin enabled and the action added:\n{text}");
        }
        finally
        {
            window.Close();
        }
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
