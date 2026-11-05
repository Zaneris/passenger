using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A running Godot game's values tuned from the Live Visual Tree's properties pane as a game developer tunes them,
/// and kept: the colour picker and a ranged value's slider each change what the game prints as the pointer moves, a value
/// changed is marked with what it was, Keep writes the scene's line or the C# [Export] default after showing it, one step
/// of Undo each, and the values left unkept are listed as the game stops. Godot runs headless, its user folders the run's;
/// without a Godot 4 .NET build each test skips saying why. Its game runs and is stopped, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotTuneTests : IClassFixture<GodotTuneTests.Game>
{
    private readonly Game game;

    public GodotTuneTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is null || game.Godot.WhyNot is not null, game.Skipped);
        game.Reset();
    }

    /// <summary>The script: every tenth frame it prints its tint and speed; the tint only the code gives, the speed a range
    /// the scene sets.</summary>
    private const string Script = """
        using System.Globalization;
        using Godot;

        public partial class Main : Node
        {
            private int frames;

            [Export]
            public Color Tint { get; set; } = new Color(1, 1, 1, 1);

            [Export(PropertyHint.Range, "0,10,0.5")]
            public float Speed { get; set; } = 1;

            public override void _Process(double delta)
            {
                frames++;
                if (frames % 10 == 0)
                {
                    GD.Print($"tint {Tint.ToHtml(false)} speed {Speed.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node"]
        script = ExtResource("1")
        Speed = 1.0

        """;

    private string ScriptPath => Path.Combine(PathNames.Canonical(game.Folder), "Main.cs");

    private string ScenePath => Path.Combine(PathNames.Canonical(game.Folder), "main.tscn");

    /// <summary>Dragging Speed's slider and the colour picker's square each reach the game as the pointer moves, before
    /// either is let go of, and what it prints follows; each row then says what its value was, with Keep.</summary>
    [AvaloniaFact]
    public void A_colour_picked_and_a_ranged_value_slid_reach_the_game_as_the_pointer_moves_each_marked_with_what_it_was()
    {
        MainWindow window = Started();
        try
        {
            LiveTreeView tree = window.LiveTree;
            Control speed = Shown(window, "Speed");
            ValueSlider slider = speed.GetVisualDescendants().OfType<ValueSlider>().Single(s => s.IsEffectivelyVisible);
            Ui.Drag(window, Along(window, slider, 0.1), Along(window, slider, 0.8));
            Ui.WaitFor(() => Run(window).Contains(" speed 8\n", StringComparison.Ordinal), () => $"the game to print the speed slid to ({Tail(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Noted(Shown(window, "Speed")) == "Changed from 1", () => $"Speed's row to say what it was ({Noted(Shown(window, "Speed"))})");

            Control tint = Shown(window, "Tint");
            Button swatch = tint.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "#FFFFFF"));
            Ui.Click(swatch);
            Ui.WaitFor(() => Picker(window) is not null, "the colour picker over Tint's swatch");
            ColourPicker picker = Picker(window)!;
            Control shades = picker.Shades;
            // White's hue is red's: past its square's top right corner, held there, is red, reached through its saturations.
            Point from = shades.TranslatePoint(new Point(2, 2), window)!.Value;
            Point to = shades.TranslatePoint(new Point(shades.Bounds.Width + 20, -20), window)!.Value;
            window.MouseDown(from, Avalonia.Input.MouseButton.Left);
            window.MouseMove(to, Avalonia.Input.RawInputModifiers.LeftMouseButton);
            Ui.WaitFor(() => Run(window).Contains("tint ff0000", StringComparison.Ordinal), () => $"the game to take the colour as the pointer holds it there, the picker at {picker.Colour.Hex}, {(Picker(window) is null ? "closed" : "open")} ({Tail(window)}; {window.Status})", TimeSpan.FromMinutes(1));
            window.MouseUp(to, Avalonia.Input.MouseButton.Left);
            Ui.Click(picker.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Choose")));
            Ui.WaitFor(() => Noted(Shown(window, "Tint")) == "Changed from #FFFFFF", () => $"Tint's row to say what it was ({Noted(Shown(window, "Tint"))})");
            Assert.Null(Picker(window));
            Assert.Equal("Keep All (2)…", tree.HeadingLink?.Content as string);
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Keep on Speed shows the scene's line as it will read and writes it there; Keep on Tint, which only the code
    /// gives, shows and writes its [Export] default in Main.cs; each is one step of Undo.</summary>
    [AvaloniaFact]
    public void Keep_shows_then_writes_the_scenes_line_and_the_CSharp_default_each_one_step_of_Undo()
    {
        MainWindow window = Started();
        try
        {
            Control speed = Shown(window, "Speed");
            ScrubField number = speed.GetVisualDescendants().OfType<ScrubField>().Single(s => s.IsEffectivelyVisible);
            Ui.Click(number.Box);
            number.Box.SelectAll();
            window.KeyTextInput("7.5");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Run(window).Contains(" speed 7.5\n", StringComparison.Ordinal), () => $"the game to print the speed typed ({Tail(window)})", TimeSpan.FromMinutes(1));
            Keep(window, "Speed", "+ Speed = 7.5", "− Speed = 1.0");
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("Speed = 7.5\n", StringComparison.Ordinal), () => $"main.tscn to keep the speed ({window.Status})");
            Assert.Equal(Scene.Replace("Speed = 1.0", "Speed = 7.5", StringComparison.Ordinal), File.ReadAllText(ScenePath));
            Ui.WaitFor(() => Noted(Shown(window, "Speed")) is null, "Speed's row to say nothing once its value is kept");
            Undone(window, ScenePath, Scene);

            Control tint = Shown(window, "Tint");
            Button swatch = tint.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "#FFFFFF"));
            Ui.Click(swatch);
            Ui.WaitFor(() => Picker(window) is not null, "the colour picker over Tint's swatch");
            window.KeyTextInput("#0000FF");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Run(window).Contains("tint 0000ff", StringComparison.Ordinal), () => $"the game to take the colour typed in the picker ({Tail(window)})", TimeSpan.FromMinutes(1));
            Keep(window, "Tint", "+ public Color Tint { get; set; } = new Color(0f, 0f, 1f, 1f);", "− public Color Tint { get; set; } = new Color(1, 1, 1, 1);");
            string kept = Script.Replace("= new Color(1, 1, 1, 1);", "= new Color(0f, 0f, 1f, 1f);", StringComparison.Ordinal);
            Ui.WaitFor(() => TextOf(window, ScriptPath) == kept, () => $"Main.cs to keep the tint as its default ({window.Status})");
            Undone(window, ScriptPath, Script);
            Stop(window);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A value set and not kept is listed as Stop ends the game, with what it was and is; Keep All there shows what
    /// it writes and writes it, and the list goes once nothing is left to keep.</summary>
    [AvaloniaFact]
    public void Values_left_unkept_are_listed_as_the_game_stops_and_Keep_All_there_writes_them()
    {
        MainWindow window = Started();
        try
        {
            Control speed = Shown(window, "Speed");
            ValueSlider slider = speed.GetVisualDescendants().OfType<ValueSlider>().Single(s => s.IsEffectivelyVisible);
            Ui.Drag(window, Along(window, slider, 0.1), Along(window, slider, 0.3));
            Ui.WaitFor(() => Run(window).Contains(" speed 3\n", StringComparison.Ordinal), () => $"the game to print the speed slid to ({Tail(window)})", TimeSpan.FromMinutes(1));
            Stop(window);
            Ui.WaitFor(() => window.Unkept is { Values.Count: 1 }, () => $"the values not kept to be listed as the game ends ({window.Status})");
            GodotUnkeptPanel unkept = window.Unkept!;
            Assert.Equal("Spinner on Godot ended with 1 value changed and not kept. Keep one where it comes from, or all of them:", unkept.Said);
            Assert.True(unkept.Values[0] is { Name: "Speed", NodeName: "Main" } listed && GodotVariant.Same(listed.Original, 1.0) && GodotVariant.Same(listed.Value, 3.0),
                $"The list holds {string.Join(", ", unkept.Values.Select(v => $"{v.NodeName}.{v.Name} {v.Original} → {v.Value}"))}");
            Ui.Click(unkept.KeepAllButton);
            Ui.WaitFor(() => window.KeepPreview is not null, "Keep All to show what it writes");
            Assert.Contains("+ Speed = 3.0", window.KeepPreview!.Lines);
            Ui.Click(window.KeepPreview.KeepButton);
            Ui.WaitFor(() => File.ReadAllText(ScenePath).Contains("Speed = 3.0\n", StringComparison.Ordinal), () => $"main.tscn to keep the speed ({window.Status})");
            Ui.WaitFor(() => window.Unkept is null && window.LiveTree.Aside is null, "the list to go once nothing is left to keep");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The window, the game run from its configuration and its Main node chosen in the Live Visual Tree, its
    /// properties shown.</summary>
    private MainWindow Started()
    {
        MainWindow window = game.Open(null, languageServer: false);
        RunConfiguration generated = Generated(window, "Spinner");
        window.SetConfigurations([generated with { Name = "Spinner on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Spinner on Godot");
        Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
        LiveTreeView tree = window.LiveTree;
        Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Main"),
            () => $"the Live Visual Tree to show the game's nodes ({window.LiveSource?.State}; {string.Join(", ", tree.Rows.Select(r => r.Element.Name))})", TimeSpan.FromMinutes(1));
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Main");
        Ui.WaitFor(() => tree.PropertyRows.Any(p => p.Name == "Speed") && tree.PropertyRows.Any(p => p.Name == "Tint"),
            () => $"Main's properties ({string.Join(", ", tree.PropertyRows.Select(p => p.Name))})");
        Ui.WaitFor(() => Run(window).Contains("tint ffffff speed 1\n", StringComparison.Ordinal), () => $"the game to print its first tint and speed ({Tail(window)})", TimeSpan.FromMinutes(1));
        return window;
    }

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

    /// <summary>What a row's note says, while it shows one.</summary>
    private static string? Noted(Control row) => row.GetVisualDescendants().OfType<TextBlock>()
        .FirstOrDefault(t => t.IsEffectivelyVisible && t.Text?.StartsWith("Changed from ", StringComparison.Ordinal) == true)?.Text;

    /// <summary>Keep on a row's note, its preview showing the lines given, and Keep there.</summary>
    private static void Keep(MainWindow window, string name, params string[] lines)
    {
        Control row = Shown(window, name);
        Ui.WaitFor(() => Noted(row) is not null, () => $"{name}'s row to note its change");
        Ui.Click(row.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "Keep")));
        Ui.WaitFor(() => window.KeepPreview is not null, () => $"Keep to show what it writes for {name} ({window.Status})");
        GodotKeepPreview preview = window.KeepPreview!;
        foreach (string line in lines)
        {
            Assert.Contains(line, preview.Lines);
        }
        Ui.Click(preview.KeepButton);
        Ui.WaitFor(() => window.KeepPreview is null && window.Status?.StartsWith($"Kept {name} in ", StringComparison.Ordinal) == true, () => $"Keep of {name} to be written ({window.Status})");
    }

    /// <summary>Undo from the Explorer, as a command of files not open is taken back, once the file kept is as it was.</summary>
    private static void Undone(MainWindow window, string path, string was)
    {
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.Run(window, AppActions.FocusExplorer);
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => TextOf(window, path) == was, () => $"Undo to put {Path.GetFileName(path)} back ({window.Status}; {TextOf(window, path)})");
    }

    private static string TextOf(MainWindow window, string path) =>
        window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, path))?.Text.Snapshot.ToString() ?? File.ReadAllText(path);

    private static ColourPicker? Picker(MainWindow window) => window.GetVisualDescendants().OfType<ColourPicker>().FirstOrDefault(p => p.IsEffectivelyVisible);

    /// <summary>A place along a slider, from its start (0) to its end (1), in the window's units.</summary>
    private static Point Along(MainWindow window, ValueSlider slider, double fraction)
    {
        double inset = slider.Bounds.Height / 2;
        return slider.TranslatePoint(new Point(inset + ((slider.Bounds.Width - (inset * 2)) * fraction), slider.Bounds.Height / 2), window)!.Value;
    }

    private static RunConfiguration Generated(MainWindow window, string name)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == name),
            () => $"the Godot project's configuration {name} ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        return window.Configurations.Single(c => c.Generated && c.Name == name);
    }

    private static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Tail(MainWindow window) => string.Join(" | ", Run(window).Split('\n').TakeLast(4));

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.IsRunning, "the game to end");
    }

    private static void End(MainWindow window)
    {
        if (window.IsRunning)
        {
            Stop(window);
        }
        window.Close();
    }

    /// <summary>The Godot 4 C# project the tests tune: <see cref="Script"/> on <see cref="Scene"/>, restored from the
    /// Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-tune-")
        {
            Godot = GodotExecutables.Find(null);
            Homes = GodotTests.Game.Home(Path.Combine(Folder, "..", Path.GetFileName(Folder) + "-home"));
            if (Godot is { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            }
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot run a C# game: {Godot.WhyNot}";

        public IReadOnlyDictionary<string, string> Homes { get; }

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
