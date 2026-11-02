using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A Godot 4 game's scenes shown beside its C#: a gutter mark on a script's class lists the scenes it runs in and
/// goes to one, following the scenes as they change on disk; one on a handler names the connection calling it; a GetNode
/// path ends in a faint hint of the node it comes to; and a res:// path's card previews what it names, a texture drawn small,
/// a sound with Play, a scene's top nodes. Its project is restored from the packages the Godot found carries, so without a
/// Godot 4 .NET build each test skips saying why; no Godot runs.</summary>
public sealed class GodotBesideTests : IClassFixture<GodotBesideTests.Game>
{
    private readonly Game game;

    public GodotBesideTests(Game game)
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
                Label score = GetNode<Label>("Hud/Score");
                Texture2D icon = GD.Load<Texture2D>("res://icon.png");
                AudioStream hit = GD.Load<AudioStream>("res://hit.wav");
                PackedScene player = GD.Load<PackedScene>("res://player.tscn");
            }

            private void OnStartPressed()
            {
            }
        }

        """;

    internal const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1_main"]
        [ext_resource type="PackedScene" path="res://player.tscn" id="2_player"]

        [node name="Main" type="Node2D"]
        script = ExtResource("1_main")

        [node name="Hud" type="CanvasLayer" parent="."]

        [node name="Score" type="Label" parent="Hud"]

        [node name="Start" type="Button" parent="Hud"]

        [node name="Player" parent="." instance=ExtResource("2_player")]

        [connection signal="pressed" from="Hud/Start" to="." method="OnStartPressed"]

        """;

    internal const string Level = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="PackedScene" path="res://main.tscn" id="1_game"]

        [node name="Level" type="Node"]

        [node name="Game" parent="." instance=ExtResource("1_game")]

        """;

    internal const string Player = """
        [gd_scene format=3]

        [node name="Player" type="CharacterBody2D"]

        [node name="Sprite" type="Sprite2D" parent="."]

        [node name="Shape" type="CollisionShape2D" parent="."]

        """;

    /// <summary>A 2×2 PNG, the texture the code loads.</summary>
    private const string Icon = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGP438AARAwQCgAt7gX9iz9uhAAAAABJRU5ErkJggg==";

    private string ScriptPath => game.PathOf("Main.cs");

    /// <summary>The scene mark on the shown file's line that holds a piece of its text, or null.</summary>
    private static SceneMark? MarkOn(MainWindow window, string piece)
    {
        OpenDocument document = window.EditorArea.Active!;
        int line = Array.FindIndex(document.Text.Snapshot.ToString().Split('\n'), l => l.Contains(piece, StringComparison.Ordinal));
        return document.SceneMarks.FirstOrDefault(m => m.Line == line);
    }

    private static string Leads(SceneMark? mark) => mark is null ? "no mark" : string.Join("; ", mark.Leads.Select(l => l.Words));

    /// <summary>The class's mark lists main.tscn and level.tscn, which instances it, and its menu goes to the node in level.tscn;
    /// a third scene written on disk joins the list. The handler's mark names the connection calling it, and the GetNode path
    /// ends in a hint of the Label it comes to.</summary>
    [AvaloniaFact]
    public void A_class_mark_lists_its_scenes_a_handlers_names_its_connection_and_a_node_path_shows_its_node()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => MarkOn(window, "partial class Main")?.Leads.Count == 2,
                () => $"the class's mark to list main.tscn and level.tscn: {Leads(MarkOn(window, "partial class Main"))}", TimeSpan.FromMinutes(1));
            Assert.Equal(["level.tscn: Level/Game", "main.tscn: Main"], MarkOn(window, "partial class Main")!.Leads.Select(l => l.Words));
            Ui.WaitFor(() => MarkOn(window, "void OnStartPressed")?.Leads.Count == 1,
                () => $"the handler's mark to name its connection: {Leads(MarkOn(window, "void OnStartPressed"))}");
            Assert.Equal("main.tscn: Hud/Start's pressed → OnStartPressed", MarkOn(window, "void OnStartPressed")!.Leads[0].Words);

            OpenDocument code = window.EditorArea.Active!;
            int after = code.Text.Snapshot.ToString().IndexOf("\"Hud/Score\"", StringComparison.Ordinal) + "\"Hud/Score\"".Length;
            Ui.WaitFor(() => code.InlayHints.Any(h => h.Offset == after && h.Label == "→ Label"),
                () => $"the node path's hint of its Label: {string.Join(", ", code.InlayHints.Select(h => $"{h.Offset} {h.Label}"))}");

            CodeEditor editor = window.EditorArea.CodeEditor;
            int classLine = MarkOn(window, "partial class Main")!.Line;
            Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Marks, classLine));
            Ui.WaitFor(() => window.SceneMarkMenu?.IsOpen == true, "the class mark's menu to open");
            Ui.Choose(window.SceneMarkMenu, "level.tscn: Level/Game");
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "level.tscn", () => $"the mark's menu to open level.tscn ({window.EditorArea.Active?.Name})");
            Assert.Contains("[node name=\"Game\"", window.EditorArea.CodeEditor.Document.Snapshot.GetLineText(window.EditorArea.CodeEditor.CaretPosition.Line), StringComparison.Ordinal);

            window.OpenFile(ScriptPath);
            File.WriteAllText(game.PathOf("arena.tscn"), Level.Replace("name=\"Level\"", "name=\"Arena\"", StringComparison.Ordinal));
            Ui.WaitFor(() => MarkOn(window, "partial class Main")?.Leads.Count == 3,
                () => $"the class's mark to list the scene written on disk: {Leads(MarkOn(window, "partial class Main"))}", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A res:// path's card previews what it names: the texture drawn small, the sound with Play, which plays it, and
    /// the scene's root with its children.</summary>
    [AvaloniaFact]
    public void A_res_paths_card_previews_a_texture_a_sound_and_a_scene()
    {
        MainWindow window = game.Open(ScriptPath);
        try
        {
            Ui.WaitFor(() => MarkOn(window, "partial class Main") is not null, "the scenes to be read against the code", TimeSpan.FromMinutes(1));

            GodotSceneTests.Hover(window, "res://icon.png", "res://icon.png");
            Ui.WaitFor(() => window.Assist.HoverCard?.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null) == true, "the texture's thumbnail on its card");

            GodotSceneTests.Hover(window, "res://hit.wav", "res://hit.wav");
            Button play = window.Assist.HoverCard!.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Play");
            Ui.Click(play);
            Ui.WaitFor(() => window.Played.Any(p => PathNames.Same(p, PathNames.Canonical(game.PathOf("hit.wav")))), () => $"Play to play hit.wav ({string.Join(", ", window.Played)})");

            string card = GodotSceneTests.Hover(window, "res://player.tscn", "Sprite (Sprite2D)");
            Assert.Contains("Player (CharacterBody2D)", card, StringComparison.Ordinal);
            Assert.Contains("Shape (CollisionShape2D)", card, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Godot 4 C# project of a main scene its script runs in, a level instancing it, a player scene, a texture and a
    /// sound, on the Godot found: restored from that Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-beside-")
        {
            Godot = GodotExecutables.Find(null);
            if (Godot is not { WhyNot: null, Version: { } version, Packages: { } packages })
            {
                return;
            }
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.WriteAllText(PathOf("level.tscn"), Level);
            File.WriteAllText(PathOf("player.tscn"), Player);
            File.WriteAllBytes(PathOf("icon.png"), Convert.FromBase64String(Icon));
            File.WriteAllBytes(PathOf("hit.wav"), Wav());
        }

        public GodotFound? Godot { get; }

        public string Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
            : $"The Godot found carries no packages a C# project restores from: {Godot.WhyNot}";

        public override ValueTask InitializeAsync() => Godot is { WhyNot: null, Packages: not null } ? base.InitializeAsync() : ValueTask.CompletedTask;

        /// <summary>A tenth of a second of silence as a 16-bit mono WAV.</summary>
        private static byte[] Wav()
        {
            const int rate = 8000;
            byte[] samples = new byte[rate / 10 * 2];
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write("RIFF"u8);
            writer.Write(36 + samples.Length);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples.Length);
            writer.Write(samples);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
