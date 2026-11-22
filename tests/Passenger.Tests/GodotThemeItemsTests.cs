using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A Godot 4 C# game's theme item names read against the project's own theme and its scene, with no Godot
/// editor open: a colour the project's theme defines renames in the theme, the scene's override and the C# string as
/// one command of Undo, a default theme item refused. Its project restores from the packages the Godot found carries,
/// so without a Godot 4 .NET build each test skips saying why; no Godot runs.</summary>
public sealed class GodotThemeItemsTests : IClassFixture<GodotThemeItemsTests.Game>
{
    private readonly Game game;

    public GodotThemeItemsTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is not { WhyNot: null, Version: not null, Packages: not null }, game.Skipped);
        game.Reset();
    }

    internal const string Script = """
        using Godot;

        public partial class Main : Control
        {
            public override void _Ready()
            {
                Label title = GetNode<Label>("Title");
                Color accent = title.GetThemeColor("accent");
                Color glow = title.GetThemeColor("glow");
                Color plain = title.GetThemeColor("font_color");
                Color wrong = title.GetThemeColor("acent");
                Theme theme = new Theme();
                Color asked = theme.GetColor("font_color", "Label");
            }
        }

        """;

    /// <summary>The project's theme: a colour of its own for every Label, and a Label variation with its own glow.</summary>
    internal const string Theme = """
        [gd_resource type="Theme" format=3]

        [resource]
        Label/colors/accent = Color(1, 0, 0, 1)
        TitleLabel/base_type = &"Label"
        TitleLabel/colors/glow = Color(1, 1, 0, 1)

        """;

    /// <summary>The HUD under the project's theme, its Title a Label of the TitleLabel variation overriding accent.</summary>
    internal const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]
        [ext_resource type="Theme" path="res://ui/theme.tres" id="2_theme"]

        [node name="Main" type="Control"]
        theme = ExtResource("2_theme")
        script = ExtResource("1_main")

        [node name="Title" type="Label" parent="."]
        theme_type_variation = &"TitleLabel"
        theme_override_colors/accent = Color(0, 1, 0, 1)

        """;

    /// <summary>Main.cs open and read against its scene: the misspelt colour marked, the rest not.</summary>
    private MainWindow Opened()
    {
        MainWindow window = game.Open(game.PathOf("Main.cs"));
        Ui.WaitFor(() => GodotSceneTests.Marked(window).Any(m => m.Contains("'acent'", StringComparison.Ordinal)),
            () => $"acent to be marked: {string.Join("; ", GodotSceneTests.Marked(window))}", TimeSpan.FromMinutes(2));
        Assert.DoesNotContain(GodotSceneTests.Marked(window), m => m.Contains("'glow'", StringComparison.Ordinal) || m.Contains("'accent'", StringComparison.Ordinal));
        return window;
    }

    /// <summary>A project theme's colour renames across the theme, a scene's override and a C# string in one step of
    /// Undo.</summary>
    [AvaloniaFact]
    public void A_project_themes_colour_renames_across_theme_scene_and_csharp()
    {
        game.Reset();
        game.Reset();
        Flow.Step(nameof(A_project_themes_colour_renames_across_the_theme_the_scenes_override_and_the_csharp_string_in_one_undo),
            A_project_themes_colour_renames_across_the_theme_the_scenes_override_and_the_csharp_string_in_one_undo);
    }

    /// <summary>accent, which the project's theme defines for Label, renamed from the C# string is renamed in the theme, in
    /// the scene's override on Title and in the string, the misspelt string left; one Undo puts every file back; Rename of
    /// font_color, the default theme's, is refused.</summary>
    private void A_project_themes_colour_renames_across_the_theme_the_scenes_override_and_the_csharp_string_in_one_undo()
    {
        MainWindow window = Opened();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "GetThemeColor(\"font_col");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "font_color", () => $"Rename's box on font_color ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("text_color");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Status?.Contains("Godot's default theme", StringComparison.Ordinal) == true, () => $"Rename of font_color to be refused ({window.Status})");

            Ui.PlaceAfter(editor, "GetThemeColor(\"acc");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "accent", () => $"Rename's box on accent ({window.Assist.Renaming}; {window.Status})");
            window.KeyTextInput("highlight");
            Ui.Press(window, "ENTER");
            string code = game.PathOf("Main.cs");
            string theme = game.PathOf(Path.Combine("ui", "theme.tres"));
            string scene = game.PathOf("main.tscn");
            Ui.WaitFor(() => TextOf(window, theme).Contains("Label/colors/highlight = Color(1, 0, 0, 1)", StringComparison.Ordinal)
                && TextOf(window, scene).Contains("theme_override_colors/highlight = Color(0, 1, 0, 1)", StringComparison.Ordinal)
                && TextOf(window, code).Contains("GetThemeColor(\"highlight\")", StringComparison.Ordinal),
                () => $"accent renamed in the theme, the scene and the string ({window.Status}):\n{TextOf(window, theme)}\n{TextOf(window, scene)}\n{TextOf(window, code)}");
            Assert.Contains("GetThemeColor(\"acent\")", TextOf(window, code), StringComparison.Ordinal);

            window.Confirm = (_, _, _) => Task.FromResult(true);
            editor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => TextOf(window, code) == Script && TextOf(window, theme) == Theme && TextOf(window, scene) == Scene,
                () => $"Undo to put Main.cs, theme.tres and main.tscn back ({window.Status})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file's text as the window has it: an open one's in its editor, else the disk's.</summary>
    private static string TextOf(MainWindow window, string path) =>
        window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, PathNames.Canonical(path)))?.Text.Snapshot.ToString() ?? File.ReadAllText(path);

    /// <summary>A Godot 4 C# project whose main scene runs a Control's script under the project's theme, on the Godot found,
    /// restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-theme-items-")
        {
            Godot = GodotExecutables.Find(null);
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            Directory.CreateDirectory(PathOf("ui"));
            File.WriteAllText(PathOf(Path.Combine("ui", "theme.tres")), Theme);
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;
    }
}
