using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Whose errors pin while two Godot games run, and where a pin's stack goes: games heard as a relayed game is, no
/// Godot needed.</summary>
public sealed class GodotErrorPinsTests
{
    private const string Script = "extends Node\n\nfunc _process(_delta):\n\tpush_error(\"a\")\n";

    [AvaloniaFact]
    public void An_older_game_still_running_pins_nothing_among_the_newest_games_errors()
    {
        string folder = PathNames.Canonical(TestFolders.Create("pins-two-games-"));
        File.WriteAllText(Path.Combine(folder, "main.gd"), Script);
        using GodotGame older = GodotGame.Relay("Older", folder, "4.7.2", (_, _) => Task.CompletedTask);
        using GodotGame newest = GodotGame.Relay("Newest", folder, "4.7.2", (_, _) => Task.CompletedTask);
        GodotErrorPins pins = new();
        pins.Follow(older, _ => TextSnapshot.From(Script), _ => null);
        Raise(older, "before the newest started");
        Ui.WaitFor(() => pins.All.Count == 1, () => $"the older game's error to pin while it was the last launched ({pins.All.Count} pins)");

        pins.Follow(newest, _ => TextSnapshot.From(Script), _ => null);
        Assert.Empty(pins.All);
        Raise(older, "after the newest started");
        Raise(newest, "from the newest");
        Ui.WaitFor(() => pins.All.Count > 0, () => "the newest game's error to pin");

        GodotPin pin = Assert.Single(pins.All);
        Assert.True(pin.Count == 1 && pin.Last.Said.Contains("from the newest", StringComparison.Ordinal),
            $"The pin counts {pin.Count}, the last saying {pin.Last.Said}: the older game's error was pinned among the newest game's, which the pins are said to be");
    }

    /// <summary>Each Godot project of the open folder is its own game: a pin of server/'s game goes to its stack's scripts
    /// in server/, though the game chosen once it ended is client/'s, whose folder holds a script of the same name.</summary>
    [AvaloniaFact]
    public void A_pins_stack_opens_its_own_games_scripts_once_another_projects_game_is_chosen()
    {
        string folder = PathNames.Canonical(TestFolders.Create("pins-two-projects-"));
        string client = Project(folder, "client");
        string server = Project(folder, "server");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(server, "main.gd"));
            Ui.Settle();
            OpenDocument script = window.EditorArea.Active!;
            using GodotGame clientGame = GodotGame.Relay("Client", client, "4.7.2", (_, _) => Task.CompletedTask);
            using GodotGame serverGame = GodotGame.Relay("Server", server, "4.7.2", (_, _) => Task.CompletedTask);
            window.Session.TakeGame(clientGame);
            window.Session.TakeGame(serverGame);
            serverGame.Take(GodotMessage.Frame(GodotWords.Error, 0L, 0L, 1L, 0L, "res://main.gd", "_process", 4L, "a", "a", false, 6L,
                "res://main.gd", "_process", 4L, "res://lib/util.gd", "fail", 4L)[4..]);
            Ui.WaitFor(() => script.ErrorPins.Count == 1, () => $"the server's error to pin at main.gd ({script.ErrorPins.Count} pins)");
            serverGame.RelayEnded();
            Ui.WaitFor(() => serverGame.State == LiveAppState.Ended && window.Session.Game == clientGame, "the server's game to end and the client's to be chosen");
            Assert.Null(serverGame.PathOf("res://../outside.gd"));

            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Pins, 3));
            Ui.WaitFor(() => window.PinPopup is not null, "the pin's occurrences");
            Button util = window.PinPopup!.GetLogicalDescendants().OfType<Button>().Single(b => b.Content is TextBlock { Text: { } said } && said.Contains("util.gd", StringComparison.Ordinal));
            Ui.Click(util);
            string expected = Path.Combine(server, "lib", "util.gd");
            Ui.WaitFor(() => window.EditorArea.Active?.Path is { } shown && PathNames.Same(shown, expected),
                () => $"the frame's script {expected} to open, where {window.EditorArea.Active?.Path} is shown");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A key costs the pins only what it changed: given the change a key made in a C# file holding no pin, as each
    /// key's is given, they allocate nothing on the window's thread and move none, while the pin of a GDScript file follows a
    /// line put above it and comes off once its own line is typed in.</summary>
    [AvaloniaFact]
    public void A_key_in_a_file_holding_no_pin_allocates_nothing_for_the_pins_and_a_pinned_line_still_follows_and_clears()
    {
        string folder = PathNames.Canonical(TestFolders.Create("pins-keys-"));
        string game = Project(folder, "game");
        string code = Path.Combine(folder, "Code.cs");
        File.WriteAllText(code, "public sealed class Code\n{\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(game, "main.gd"));
            Ui.Settle();
            OpenDocument script = window.EditorArea.Active!;
            using GodotGame running = GodotGame.Relay("Game", game, "4.7.2", (_, _) => Task.CompletedTask);
            window.Session.TakeGame(running);
            Raise(running, "a");
            Ui.WaitFor(() => script.ErrorPins.Count == 1, () => $"the game's error to pin at main.gd ({script.ErrorPins.Count} pins)");
            GodotErrorPins pins = window.Session.ErrorPins;
            GodotPin pin = Assert.Single(pins.All);

            window.OpenFile(code);
            Ui.Settle();
            OpenDocument typed = window.EditorArea.Active!;
            DocumentChangedEventArgs? change = null;
            typed.Text.Changed += (_, e) => change = e;
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            Ui.PlaceAfter(editor, "class Code");
            long least = long.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                window.KeyTextInput("s");
                DocumentChangedEventArgs key = change ?? throw new Xunit.Sdk.XunitException("A key typed into Code.cs changed nothing");
                long before = GC.GetAllocatedBytesForCurrentThread();
                pins.Edited(typed.Path, key);
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Ui.Settle();
            Assert.True(least == 0, $"A key in Code.cs, which holds no pin, cost the pins {least:N0} bytes on the window's thread at the least of 6");
            Assert.True(pins.All.Count == 1 && pin.Line == 3 && script.ErrorPins is [{ Line: 3 }],
                $"Keys in Code.cs moved the pins: {pins.All.Count} at line {pin.Line}, main.gd showing {script.ErrorPins.Count}");

            window.OpenFile(script.Path);
            Ui.Settle();
            editor = window.EditorArea.CodeEditor;
            editor.Focus();
            Ui.PlaceAfter(editor, "extends Node");
            Ui.Press(window, "ENTER");
            Assert.True(pins.All.Count == 1 && pin.Line == 4 && script.ErrorPins is [{ Line: 4 }],
                $"The pin did not follow its line down past a line put above it: {pins.All.Count} at line {pin.Line}, main.gd showing {script.ErrorPins.Count}");
            Ui.PlaceAfter(editor, "push_error(\"a");
            window.KeyTextInput("b");
            Ui.Settle();
            Assert.True(pins.All.Count == 0 && script.ErrorPins.Count == 0,
                $"The pin stayed on a line typed in: {pins.All.Count} pins, main.gd showing {script.ErrorPins.Count}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Godot project of GDScript alone in a folder of the open one, its main.gd raising an error at line 4 and a
    /// lib/util.gd beside it.</summary>
    private static string Project(string folder, string name)
    {
        string project = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.Combine(project, "lib"));
        File.WriteAllText(Path.Combine(project, GodotProject.FileName), $"config_version=5\n\n[application]\n\nconfig/name=\"{name}\"\n");
        File.WriteAllText(Path.Combine(project, "main.gd"), Script);
        File.WriteAllText(Path.Combine(project, "lib", "util.gd"), "extends Node\n\nfunc fail():\n\tpush_error(\"b\")\n");
        return project;
    }

    /// <summary>A game's error at line 4 of main.gd, as it tells Godot's debugger of it.</summary>
    private static void Raise(GodotGame game, string said) =>
        game.Take(GodotMessage.Frame(GodotWords.Error, 0L, 0L, 1L, 0L, "res://main.gd", "_process", 4L, said, said, false, 0L)[4..]);
}
