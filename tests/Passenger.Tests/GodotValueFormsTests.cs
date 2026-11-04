using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A running Godot game's arrays, dictionaries and resources set from the Live Visual Tree's properties pane in
/// forms fit for them: an array's items, a row each, one typed and one added; a dictionary's value typed in its entry's
/// row; an array in an array in a form of its own, the whole outer value sent back; a resource's own properties, a check
/// box among them, each reaching the game as it is set, as what the game prints shows, and kept where the resource is
/// written, its scene's sub_resource or its own file. The game is GDScript alone, run headless with the run's user
/// folders; without a Godot 4 each test skips saying why. Its game runs and is stopped, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotValueFormsTests : IClassFixture<GodotValueFormsTests.Game>
{
    private readonly Game game;

    public GodotValueFormsTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot?.WhyNotScripts is not null || game.Godot is null, game.Skipped);
        game.Reset();
    }

    /// <summary>The array's item typed and one added, the dictionary's value typed, and the resource's check box cleared,
    /// each printed by the game as it then holds it.</summary>
    [AvaloniaFact]
    public void An_arrays_item_a_dictionarys_value_and_a_resources_property_are_set_in_the_running_game()
    {
        MainWindow window = Open();
        try
        {
            Start(window);
            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"), () => $"the game's Main node ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
            tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Main");
            Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "waves") && tree.PropertyRows.Any(p => p.Name == "look"),
                () => $"Main's waves and look ({string.Join(", ", tree.PropertyRows.Select(p => p.Name))})");
            Ui.WaitFor(() => Printed(window, "waves [1, 2, 3]"), () => $"the game to print its first values ({Tail(window)})", TimeSpan.FromMinutes(1));

            GodotItemsEditor waves = Items(window, "waves", "[1, 2, 3]");
            Type(window, waves.Fields[1], "5");
            Ui.WaitFor(() => Printed(window, "waves [1, 5, 3]"), () => $"the game to print the item typed ({Tail(window)}; {window.Status}; fields {string.Join(", ", waves.Fields.Select(f => f.Text))})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => waves.Fields.Count == 3 && waves.Fields[1].Text == "5", () => $"the item's row to show what the game holds ({string.Join(", ", waves.Fields.Select(f => f.Text))})");
            Ui.Click(waves.Add);
            Ui.WaitFor(() => Printed(window, "waves [1, 5, 3, 0]"), () => $"the game to print the item added, an int's zero ({Tail(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "waves" && p.Value == "[1, 5, 3, 0]"),
                () => $"waves' row to show what the game holds ({tree.PropertyRows.FirstOrDefault(p => p.Name == "waves")?.Value})");
            Ui.Press(window, "ESCAPE");

            GodotItemsEditor scores = Items(window, "scores", "{ \"red\": 1, \"blue\": 2 }");
            Assert.True(scores.Entries && scores.Fields.Count == 4, $"The dictionary shows {scores.Fields.Count} fields, not two keys and their values");
            Type(window, scores.Fields[3], "7");
            Ui.WaitFor(() => Printed(window, "\"blue\": 7"), () => $"the game to print the value typed ({Tail(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.Press(window, "ESCAPE");

            Control look = Shown(window, "look");
            Button properties = look.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "Properties"));
            Ui.Click(properties);
            Ui.WaitFor(() => Popup<GodotResourceEditor>(window)?.RowOf("anti_aliasing") is { IsEffectivelyVisible: true },
                () => $"the resource's own properties ({window.Status})", TimeSpan.FromMinutes(1));
            GodotResourceEditor resource = Popup<GodotResourceEditor>(window)!;
            CheckBox smooth = resource.RowOf("anti_aliasing")!.GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible);
            Assert.True(smooth.IsChecked, "StyleBoxFlat's anti_aliasing is not shown on, as the game holds it");
            Ui.Click(smooth);
            Ui.WaitFor(() => Printed(window, "smooth false"), () => $"the game to print the resource's property cleared ({Tail(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Assert.False(Run(window).Contains("passenger_agent.gd", StringComparison.Ordinal), $"Passenger's helper raised errors in the game:\n{Run(window)}");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>An array in an array opens its own form under the outer one's rows: its item typed there reaches the game as
    /// the whole outer value with that array in its place, and both forms and the pane's row show what the game holds.</summary>
    [AvaloniaFact]
    public void A_nested_arrays_form_sends_the_whole_outer_value_back_with_its_item_typed()
    {
        MainWindow window = Open();
        try
        {
            Start(window);
            LiveTreeView tree = window.LiveTree;
            Choose(window);
            Ui.WaitFor(() => Printed(window, "grid [[1, 2], [3, 4]]"), () => $"the game to print its grid ({Tail(window)})", TimeSpan.FromMinutes(1));

            GodotItemsEditor grid = Items(window, "grid", "[[1, 2], [3, 4]]");
            Assert.Empty(grid.Fields);
            Ui.Click(Opener(grid, "[1, 2]"));
            Ui.WaitFor(() => Inner(grid) is { Fields.Count: 2 }, () => $"the first array's own form under grid's rows ({window.Status})");
            GodotItemsEditor inner = Inner(grid)!;
            Type(window, inner.Fields[1], "9");
            Ui.WaitFor(() => Printed(window, "grid [[1, 9], [3, 4]]"), () => $"the game to print the grid with the item typed in its first array ({Tail(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => inner.Fields[1].Text == "9" && grid.GetVisualDescendants().OfType<Button>().Any(b => b.Content is TextBlock { Text: "[1, 9]" }),
                () => $"both forms to show what the game holds ({string.Join(", ", inner.Fields.Select(f => f.Text))})");
            Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "grid" && p.Value == "[[1, 9], [3, 4]]"),
                () => $"grid's row to show what the game holds ({tree.PropertyRows.FirstOrDefault(p => p.Name == "grid")?.Value})");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A resource's own property set in the game is noted in its form with Keep, which shows and writes the line
    /// where the resource is written: in the scene's sub_resource for one the scene holds, in its own [resource] section for
    /// one saved as a file.</summary>
    [AvaloniaFact]
    public void A_resources_property_set_in_the_game_is_kept_in_its_scenes_sub_resource_and_in_its_own_file()
    {
        MainWindow window = Open();
        try
        {
            Start(window);
            Choose(window);
            Ui.WaitFor(() => Printed(window, "smooth true skin true"), () => $"the game to print its first values ({Tail(window)})", TimeSpan.FromMinutes(1));

            Cleared(window, "look", "smooth false");
            KeepIn(window, "+ anti_aliasing = false");
            string scene = Path.Combine(game.Folder, "main.tscn");
            Ui.WaitFor(() => File.ReadAllText(scene) == Game.Scene.Replace("bg_color = Color(1, 0, 0, 1)\n", "bg_color = Color(1, 0, 0, 1)\nanti_aliasing = false\n", StringComparison.Ordinal),
                () => $"main.tscn to keep the property in its sub_resource ({window.Status}):\n{File.ReadAllText(scene)}");
            Ui.Press(window, "ESCAPE");

            Cleared(window, "skin", "skin false");
            KeepIn(window, "+ anti_aliasing = false");
            string skin = Path.Combine(game.Folder, "skin.tres");
            Ui.WaitFor(() => File.ReadAllText(skin) == Game.Skin.Replace("bg_color = Color(0, 0, 1, 1)\n", "bg_color = Color(0, 0, 1, 1)\nanti_aliasing = false\n", StringComparison.Ordinal),
                () => $"skin.tres to keep the property in its [resource] ({window.Status}):\n{File.ReadAllText(skin)}");
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The game's Main node chosen in the Live Visual Tree, its properties shown.</summary>
    private static void Choose(MainWindow window)
    {
        LiveTreeView tree = window.LiveTree;
        Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"), () => $"the game's Main node ({string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Main");
        Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "grid") && tree.PropertyRows.Any(p => p.Name == "skin"),
            () => $"Main's grid and skin ({string.Join(", ", tree.PropertyRows.Select(p => p.Name))})");
    }

    /// <summary>A resource's form opened from its row's Properties, its anti_aliasing cleared, and the game printing it so.</summary>
    private static void Cleared(MainWindow window, string name, string printed)
    {
        Control row = Shown(window, name);
        Ui.Click(row.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "Properties")));
        Ui.WaitFor(() => Popup<GodotResourceEditor>(window)?.RowOf("anti_aliasing") is { IsEffectivelyVisible: true },
            () => $"{name}'s own properties ({window.Status})", TimeSpan.FromMinutes(1));
        Control smooth = Popup<GodotResourceEditor>(window)!.RowOf("anti_aliasing")!;
        Ui.Click(smooth.GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible));
        Ui.WaitFor(() => Printed(window, printed), () => $"the game to print {name}'s property cleared ({Tail(window)}; {window.Status})", TimeSpan.FromMinutes(1));
    }

    /// <summary>Keep on the resource form's changed anti_aliasing, its preview showing the line given, and Keep there.</summary>
    private static void KeepIn(MainWindow window, string line)
    {
        Control smooth = Popup<GodotResourceEditor>(window)!.RowOf("anti_aliasing")!;
        Ui.WaitFor(() => smooth.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "Keep")),
            () => $"anti_aliasing's row to offer Keep ({string.Join(", ", smooth.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text))})");
        Ui.Click(smooth.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "Keep")));
        Ui.WaitFor(() => window.KeepPreview is not null, () => $"Keep to show what it writes ({window.Status})");
        Assert.Contains(line, window.KeepPreview!.Lines);
        Ui.Click(window.KeepPreview.KeepButton);
        Ui.WaitFor(() => window.KeepPreview is null && window.Status?.StartsWith("Kept anti_aliasing in ", StringComparison.Ordinal) == true,
            () => $"Keep of anti_aliasing to be written ({window.Status})");
    }

    /// <summary>The button of a form's row that opens the item it shows.</summary>
    private static Button Opener(GodotItemsEditor form, string words)
    {
        Ui.WaitFor(() => form.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content is TextBlock { Text: { } said } && said == words),
            () => $"a row of the form to offer {words}");
        return form.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content is TextBlock { Text: { } said } && said == words);
    }

    /// <summary>The form open under another's rows, while one is.</summary>
    private static GodotItemsEditor? Inner(GodotItemsEditor outer) => outer.GetVisualDescendants().OfType<GodotItemsEditor>().FirstOrDefault(e => e != outer && e.IsEffectivelyVisible);

    /// <summary>A container's row, its words clicked to open its items or entries.</summary>
    private static GodotItemsEditor Items(MainWindow window, string name, string words)
    {
        Control row = Shown(window, name);
        Ui.WaitFor(() => row.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content is TextBlock { Text: { } said } && said == words),
            () => $"{name}'s row to offer its {words} ({string.Join(", ", row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text))})");
        Ui.Click(row.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content is TextBlock { Text: { } said } && said == words));
        Ui.WaitFor(() => Popup<GodotItemsEditor>(window) is not null, () => $"{name}'s items over its row ({window.Status})");
        return Popup<GodotItemsEditor>(window)!;
    }

    /// <summary>A field typed into from the start, and Enter.</summary>
    private static void Type(MainWindow window, TextBox field, string text)
    {
        Ui.WaitFor(() => field.IsEffectivelyVisible && field.Focus() && field.IsFocused, () => $"the field showing {field.Text} to take the keys (shown {field.IsEffectivelyVisible}, {field.Bounds})");
        field.SelectAll();
        window.KeyTextInput(text);
        Ui.Press(window, "ENTER");
    }

    private static T? Popup<T>(MainWindow window) where T : Control => window.GetVisualDescendants().OfType<T>().FirstOrDefault(p => p.IsEffectivelyVisible);

    /// <summary>A property's row, the filter narrowed to it so the list shows it.</summary>
    private static Control Shown(MainWindow window, string name)
    {
        LiveTreeView tree = window.LiveTree;
        if (tree.PropertyFilter.Text != name)
        {
            tree.PropertyFilter.Text = name;
        }
        Ui.WaitFor(() => tree.PropertyRowOf(name) is { IsEffectivelyVisible: true }, () => $"{name}'s row ({string.Join(", ", tree.PropertyRows.Select(p => p.Name))})");
        return tree.PropertyRowOf(name)!;
    }

    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = game.Homes };
        window.Show();
        window.OpenFolder(game.Folder);
        Ui.Settle();
        return window;
    }

    private void Start(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Forms"),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Forms");
        window.SetConfigurations([generated with { Name = "Forms on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Forms on Godot");
        Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => window.Session.Game is { State: LiveAppState.Connected }, () => $"the game to connect ({window.Session.Game?.State}; {window.Status})", TimeSpan.FromMinutes(1));
    }

    private static bool Printed(MainWindow window, string words) => Run(window).Contains(words, StringComparison.Ordinal);

    private static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Tail(MainWindow window) => string.Join(" | ", Run(window).Split('\n').TakeLast(3));

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.IsRunning && window.Session.Games.Count == 0, () => $"the game to end ({window.Status})");
    }

    private static void End(MainWindow window)
    {
        if (window.IsRunning)
        {
            Stop(window);
        }
        window.Close();
    }

    /// <summary>A Godot 4 project of GDScript alone named Forms at 20 frames a second: its Main node exports an array of
    /// ints, a dictionary, an array of arrays, a StyleBoxFlat its scene gives it and one saved as skin.tres, and prints them
    /// every fifth frame.</summary>
    public sealed class Game
    {
        public const string Script = """
            extends Node

            @export var waves: Array[int] = [1, 2, 3]
            @export var scores := {"red": 1, "blue": 2}
            @export var grid := [[1, 2], [3, 4]]
            @export var look: StyleBoxFlat
            @export var skin: StyleBoxFlat
            var frames := 0


            func _process(_delta: float) -> void:
            	frames += 1
            	if frames % 5 == 0:
            		print("waves ", waves, " scores ", scores, " smooth ", look.anti_aliasing, " skin ", skin.anti_aliasing, " grid ", grid)

            """;

        public const string Scene = """
            [gd_scene load_steps=4 format=3]

            [ext_resource type="Script" path="res://main.gd" id="1"]
            [ext_resource type="StyleBox" path="res://skin.tres" id="2"]

            [sub_resource type="StyleBoxFlat" id="style"]
            bg_color = Color(1, 0, 0, 1)

            [node name="Main" type="Node"]
            script = ExtResource("1")
            look = SubResource("style")
            skin = ExtResource("2")

            """;

        public const string Skin = """
            [gd_resource type="StyleBoxFlat" format=3]

            [resource]
            bg_color = Color(0, 0, 1, 1)

            """;

        public Game()
        {
            Godot = GodotExecutables.Find(null);
            Folder = PathNames.Canonical(TestFolders.Create("godot-forms-"));
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            File.WriteAllText(Path.Combine(Folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Forms\"\n"
                + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\nrun/max_fps=20\n");
            Reset();
        }

        public string Folder { get; }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 project: {Godot.WhyNotScripts}";

        public IReadOnlyDictionary<string, string> Homes { get; }

        /// <summary>The script, the scene and skin.tres as each test begins, whatever a test before kept in them.</summary>
        public void Reset()
        {
            File.WriteAllText(Path.Combine(Folder, "main.gd"), Script);
            File.WriteAllText(Path.Combine(Folder, "main.tscn"), Scene);
            File.WriteAllText(Path.Combine(Folder, "skin.tres"), Skin);
        }
    }
}
