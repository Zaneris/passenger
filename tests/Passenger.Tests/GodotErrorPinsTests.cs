using Avalonia.Headless.XUnit;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Whose errors pin while two Godot games run: games heard as a relayed game is, no Godot needed.</summary>
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

    /// <summary>A game's error at line 4 of main.gd, as it tells Godot's debugger of it.</summary>
    private static void Raise(GodotGame game, string said) =>
        game.Take(GodotMessage.Frame(GodotWords.Error, 0L, 0L, 1L, 0L, "res://main.gd", "_process", 4L, said, said, false, 0L)[4..]);
}
