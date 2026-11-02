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
/// row; a resource's own properties, a check box among them, each reaching the game as it is set, as what the game prints
/// shows. The game is GDScript alone, run headless with the run's user folders; without a Godot 4 each test skips saying
/// why. Its game runs and is stopped, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotValueFormsTests : IClassFixture<GodotValueFormsTests.Game>
{
    private readonly Game game;

    public GodotValueFormsTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot?.WhyNotScripts is not null || game.Godot is null, game.Skipped);
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
    /// ints, a dictionary and a StyleBoxFlat its scene gives it, and prints them every fifth frame.</summary>
    public sealed class Game
    {
        public Game()
        {
            Godot = GodotExecutables.Find(null);
            Folder = PathNames.Canonical(TestFolders.Create("godot-forms-"));
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            File.WriteAllText(Path.Combine(Folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Forms\"\n"
                + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\nrun/max_fps=20\n");
            File.WriteAllText(Path.Combine(Folder, "main.gd"), """
                extends Node

                @export var waves: Array[int] = [1, 2, 3]
                @export var scores := {"red": 1, "blue": 2}
                @export var look: StyleBoxFlat
                var frames := 0


                func _process(_delta: float) -> void:
                	frames += 1
                	if frames % 5 == 0:
                		print("waves ", waves, " scores ", scores, " smooth ", look.anti_aliasing)

                """);
            File.WriteAllText(Path.Combine(Folder, "main.tscn"), """
                [gd_scene load_steps=3 format=3]

                [ext_resource type="Script" path="res://main.gd" id="1"]

                [sub_resource type="StyleBoxFlat" id="style"]
                bg_color = Color(1, 0, 0, 1)

                [node name="Main" type="Node"]
                script = ExtResource("1")
                look = SubResource("style")

                """);
        }

        public string Folder { get; }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 project: {Godot.WhyNotScripts}";

        public IReadOnlyDictionary<string, string> Homes { get; }
    }
}
