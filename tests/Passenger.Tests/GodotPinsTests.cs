using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A Godot game's runtime errors kept after it ends: a game that pushes one error every frame and another once
/// leaves both pinned at their lines once stopped, each with its count and its last words, a badge's press listing each
/// time with its stack; the Run console shows each once, folded with its count, its other lines as written; and an edit of
/// a pinned line takes its pin off. Godot runs headless, its user folders the run's; without a Godot 4 .NET build the test
/// skips saying why. Its game runs and is stopped, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotPinsTests : IClassFixture<GodotPinsTests.Game>
{
    private readonly Game game;

    public GodotPinsTests(Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot is null || game.Godot.WhyNot is not null, game.Skipped);
        game.Reset();
    }

    private const string Script = """
        using Godot;

        public partial class Main : Node
        {
            private int frames;

            public override void _Process(double delta)
            {
                frames++;
                GD.PushError("Every frame");
                if (frames == 5)
                {
                    GD.PushError("Only once");
                }
                if (frames % 10 == 0)
                {
                    GD.Print($"frame {frames}");
                }
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        """;

    private string ScriptPath => Path.Combine(PathNames.Canonical(game.Folder), "Main.cs");

    [AvaloniaFact]
    public void Errors_stay_pinned_at_their_lines_with_their_counts_after_Stop_the_console_folds_them_and_an_edit_takes_a_pin_off()
    {
        MainWindow window = game.Open(ScriptPath, languageServer: false);
        try
        {
            RunConfiguration generated = Generated(window, "Spinner");
            window.SetConfigurations([generated with { Name = "Spinner on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Spinner on Godot");
            Ui.Launched(window, window.RunAsync(), "the game's build and start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Run(window).Contains("frame 30\n", StringComparison.Ordinal), () => $"the game to run thirty frames ({Tail(window)})", TimeSpan.FromMinutes(1));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "the game to end");

            int every = LineOf("GD.PushError(\"Every frame\")");
            int once = LineOf("GD.PushError(\"Only once\")");
            OpenDocument script = window.EditorArea.Active!;
            Ui.WaitFor(() => script.ErrorPins.Count == 2, () => $"both errors pinned to Main.cs ({Pins(script)})");
            ErrorPin pinned = script.ErrorPins.Single(p => p.Line == every - 1);
            Assert.True(pinned is { Count: >= 30, Last: "Every frame", Warnings: false }, $"The every-frame pin says {Pins(script)}");
            Assert.True(script.ErrorPins.Single(p => p.Line == once - 1) is { Count: 1, Last: "Only once" }, $"The one-time pin says {Pins(script)}");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Assert.Equal(2, editor.Gutter!.Pins.Pins.Count);
            Assert.Contains(script.ShownAnnotations, a => a.Line == every - 1 && a.Text == "Every frame");

            // The console shows each error once, the every-frame one with its count, the game's own lines as it wrote them.
            string console = Run(window);
            Passenger.App.ToolWindows.OutputView run = window.BottomPanel.Run;
            GodotConsoleFold fold = run.Fold!;
            string entries = string.Join("\n----\n", fold.Entries.Select(e => $"×{e.Count}: {e.Text}"));
            Assert.True(Occurrences(console, "ERROR: Every frame") == 1 && Occurrences(console, "ERROR: Only once") == 1,
                $"The console shows each error other than once, its entries:\n{entries}");
            Assert.True(Occurrences(console, "frame 10\n") == 1 && Occurrences(console, "frame 30\n") == 1, $"The game's own lines were not kept as written: {Tail(window)}");
            Assert.True(fold.Entries.Count == 2, $"The console folds other than two entries:\n{entries}");
            int folded = console.Split('\n').ToList().FindIndex(l => l.StartsWith("ERROR: Every frame", StringComparison.Ordinal));
            Assert.Contains(run.FoldNotes, n => n.Line == folded && n.Text.StartsWith($"×{fold.Entries[0].Count:N0} · last at ", StringComparison.Ordinal));
            Ui.Click(run.FoldFilter);
            Assert.True(run.Filtered?.Snapshot.ToString() is { } only && only.StartsWith("ERROR: Every frame\n", StringComparison.Ordinal)
                && Occurrences(only, "ERROR: Only once") == 1 && !only.Contains("frame 10", StringComparison.Ordinal),
                $"The filter shows {run.Filtered?.Snapshot}");
            Ui.Click(run.FoldFilter);

            // The badge's press lists each time, newest first, with its stack.
            Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Pins, every - 1));
            Ui.WaitFor(() => window.PinPopup is not null, "the every-frame pin's occurrences");
            GodotPinPopup popup = window.PinPopup!;
            Assert.Equal(pinned.Count, popup.Listed.Count);
            Assert.Contains(popup.Frames, f => f.Contains($"Main.cs:{every}", StringComparison.Ordinal));
            Ui.Press(window, "ESCAPE");
            Ui.WaitFor(() => window.PinPopup is null, "the pin's popup to close");

            // An edit of the every-frame line takes its pin off; the other stays.
            editor.Focus();
            Ui.PlaceAfter(editor, "GD.PushError(\"Every frame\");");
            window.KeyTextInput(" ");
            Ui.WaitFor(() => script.ErrorPins.Count == 1 && script.ErrorPins[0].Line == once - 1, () => $"the edited line's pin to go and the other to stay ({Pins(script)})");
        }
        finally
        {
            if (window.IsRunning)
            {
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => !window.IsRunning, "the game to end");
            }
            window.Close();
        }
    }

    private int LineOf(string code) => Array.FindIndex(File.ReadAllText(ScriptPath).Split('\n'), l => l.Contains(code, StringComparison.Ordinal)) + 1;

    private static int Occurrences(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string Pins(OpenDocument document) => string.Join("; ", document.ErrorPins.Select(p => $"line {p.Line + 1} ×{p.Count} {p.Last}"));

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

    private static string Tail(MainWindow window) => string.Join(" | ", Run(window).Split('\n').TakeLast(6));

    /// <summary>The Godot 4 C# project whose errors are pinned: <see cref="Script"/> on <see cref="Scene"/>, restored from the
    /// Godot's own packages alone.</summary>
    public sealed class Game : SharedProject
    {
        public Game()
            : base("godot-pins-")
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
