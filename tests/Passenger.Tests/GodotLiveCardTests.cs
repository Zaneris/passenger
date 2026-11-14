using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A running Godot game's value card in an editor at the window's smallest width, against a stand-in game heard as
/// Godot's editor relays one: it answers its scene tree with Passenger's helper in it, and the helper's reads of a script's
/// vector with one that moves at each beat of its monitors. No Godot is needed.</summary>
public sealed class GodotLiveCardTests
{
    /// <summary>A script whose one member's line is short enough for its value's hint to show in an editor this narrow.</summary>
    private const string Script = "extends Node\n\nvar d\n";

    /// <summary>The line (from zero) of the script's member.</summary>
    private const int Member = 2;

    /// <summary>In a window at its smallest with the default layout, the Explorer open and the Live Visual Tree come beside
    /// the game as it connects, leaving the editor narrower than the card's chart and its legend in a row, a vector's card
    /// shows whole in the editor: its chart narrowed inside its frame and the card, and its legend of the vector's parts
    /// wrapped onto rows inside the card, none of its words cut.</summary>
    [AvaloniaFact]
    public void A_vectors_card_in_an_editor_at_the_windows_smallest_width_shows_its_chart_and_legend_whole_the_legend_wrapping()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-live-card-"));
        File.WriteAllText(Path.Combine(folder, GodotProject.FileName), "config_version=5\n\n[application]\n\nconfig/name=\"Drifter\"\n");
        File.WriteAllText(Path.Combine(folder, "drifter.gd"), Script);
        MainWindow window = Ui.OpenFolder(folder, MainWindow.SmallestWidth, MainWindow.SmallestHeight, languageServer: false);
        using StandIn game = new(folder);
        try
        {
            window.OpenFile(Path.Combine(folder, "drifter.gd"));
            Ui.Settle();
            OpenDocument script = window.EditorArea.Active!;
            window.Session.TakeGame(game.Game);
            game.Game.RelayConnected();
            Ui.WaitFor(() => Hint(script) is not null, () => $"d's hint ({window.GodotValues.Unread ?? "read"}; {game.Said})");
            Rect layer = CodeAssistTests.Layer(window);
            Assert.True(layer.Width < 300, $"The default layout at the window's smallest width was to leave the editor narrower than a card's chart and its legend in a row; it is {layer.Width} wide");
            string first = Hint(script)!;
            Ui.Hold(TimeSpan.FromSeconds(1.1), "a second since the first reading, which a reading in the same second takes the place of");
            game.Beat();
            Ui.WaitFor(() => Hint(script) is { } now && now != first, () => $"d's hint to move from {first} at the game's beat ({game.Said})");

            Rest(window, script);
            Ui.WaitFor(() => window.Assist.HoverText?.Contains("lowest and highest", StringComparison.Ordinal) == true,
                () => $"d's card with its last minute charted (it says {window.Assist.HoverText})");
            Control card = window.Assist.HoverCard!;
            CodeAssistTests.WholeInEditor(window, card, "d's card");
            Rect inCard = In(window, card);

            Sparkline chart = Assert.Single(card.GetVisualDescendants().OfType<Sparkline>());
            Control frame = (Control)chart.GetVisualParent()!;
            Rect line = In(window, chart);
            Rect framed = In(window, frame);
            Assert.True(inCard.Contains(framed) && framed.Inflate(0.5).Contains(line),
                $"The chart at {line} runs past its frame at {framed} or the frame past the card at {inCard}, where it was to narrow to the card's room");

            TextBlock[] parts = [.. card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text?.Contains(" to ", StringComparison.Ordinal) == true)];
            Assert.True(parts.Length == 3, $"The card's legend names {parts.Length} parts ({string.Join(", ", parts.Select(p => p.Text))}), not d's three");
            Assert.True(parts.Select(p => Math.Round(In(window, p).Y)).Distinct().Count() > 1,
                $"The legend stands in one row in a card of {inCard.Width:0.#}, where its parts ({string.Join(", ", parts.Select(p => $"{p.Text} at {In(window, p)}"))}) were to wrap");
            foreach (TextBlock part in parts)
            {
                Assert.True(part.TextLayout.TextLines.All(l => !l.HasCollapsed) && part.Bounds.Width >= part.TextLayout.Width - 0.5,
                    $"The legend cuts '{part.Text}' to {part.Bounds.Width:0.#} of its {part.TextLayout.Width:0.#}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Where a control lies in its window.</summary>
    private static Rect In(MainWindow window, Control control) => new(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);

    /// <summary>The member's hint as the file shows it, or null.</summary>
    private static string? Hint(OpenDocument document) => document.LiveValues.Where(a => a.Line == Member).Select(a => a.Text).FirstOrDefault();

    /// <summary>The pointer rested on the start of the member's hint as it is drawn now, the part a narrow editor shows.</summary>
    private static void Rest(MainWindow window, OpenDocument script)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Rect box = default;
        Ui.WaitFor(() => Hint(script) is { } hint && editor.AnnotationBox(Member, hint) is { } drawn && (box = drawn) != default, "d's hint drawn");
        Assert.True(box.X + 8 < editor.Bounds.Width, $"d's hint at {box} starts past the editor's {editor.Bounds.Width:0.#}, where it was to show");
        window.MouseMove(editor.TranslatePoint(new Point(box.X + 8, box.Center.Y), window)!.Value, RawInputModifiers.None);
    }

    /// <summary>A game heard as Godot's editor relays one: it answers its scene tree with Passenger's helper beside its scene,
    /// and the helper's reads of a member with a vector that moves at each <see cref="Beat"/>, which its monitors give.</summary>
    private sealed class StandIn : IDisposable
    {
        private int beats;

        public StandIn(string folder)
        {
            GodotGame? relayed = null;
            relayed = GodotGame.Relay("Drifter", folder, "4.7.2", (body, _) =>
            {
                Answer(relayed!, GodotMessage.Read(body));
                return Task.CompletedTask;
            });
            Game = relayed;
        }

        public GodotGame Game { get; }

        /// <summary>What it has answered, for a wait's words.</summary>
        public string Said { get; private set; } = "nothing asked";

        /// <summary>The game's monitors, as it sends them once a second.</summary>
        public void Beat()
        {
            beats++;
            Game.Take(GodotMessage.Frame(GodotWords.Monitors, [.. Enumerable.Range(0, 11).Select(i => (object?)(double)(i + beats))])[4..]);
        }

        private void Answer(GodotGame game, GodotMessage message)
        {
            switch (message.Name)
            {
                case GodotWords.RequestSceneTree:
                    // The root, its scene and the helper, each as six values: children, name, type, id, scene file and view flags.
                    game.Take(GodotMessage.Frame(GodotWords.SceneTree, 2L, "root", "Window", 1L, "", 0L, 0L, "Drifter", "Node", 2L, "res://drifter.tscn", 0L,
                        0L, GodotAgent.NodeName, "user://passenger_agent.gd", 3L, "", 0L)[4..]);
                    Said = "its scene tree";
                    break;
                case GodotWords.LiveNodeCall when message.Data is [_, string asked, string call, long ask, List<object?> arguments] && asked == GodotAgent.Asked:
                    List<object?> answer = [ask, true];
                    // Each script asked of (its path, the node chosen, its members' names) read in the one node that runs it.
                    if (call == "values" && arguments is [List<object?> scripts])
                    {
                        answer.Add(scripts.OfType<List<object?>>().Select(script => (object?)new List<object?>
                        {
                            1L, 2L, "Drifter", script is [_, _, List<object?> names] ? names.Select(_ => (object?)new List<object?> { Drift() }).ToList() : new List<object?>(),
                        }).ToList());
                        Said = $"d at beat {beats}";
                    }
                    game.Take(GodotMessage.Frame(GodotAgent.Said, [.. answer])[4..]);
                    break;
            }
        }

        /// <summary>The vector at the beat, each part of it moving.</summary>
        private GodotMath Drift() => new(GodotType.Vector3, [1234.5 + beats, -2.25 - (beats * 0.5), 0.125 * (beats + 1)]);

        public void Dispose() => Game.Dispose();
    }
}
