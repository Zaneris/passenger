using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;

namespace Passenger.Tests;

/// <summary>Several games' values left unkept, each held until they are kept or discarded: a second game's end never takes the
/// first's values away, and a game no one followed as it ended is offered too. The games are listened for and end unconnected,
/// so no Godot is needed.</summary>
public sealed class GodotUnkeptTests
{
    [AvaloniaFact]
    public void Each_ended_games_unkept_values_are_held_until_they_are_kept_or_discarded()
    {
        MainWindow window = Ui.OpenFolder(TestFolders.Create("godot-unkept-"), languageServer: false);
        GodotGame first = Taken(window, "Arena", "Speed");
        GodotGame second = Taken(window, "Arena #2", "Tint");
        try
        {
            first.Dispose();
            Ui.WaitFor(() => window.Unkept is { Game: "Arena" }, () => $"the game no one followed to be listed as it ended ({window.Status})");
            second.Dispose();
            Ui.WaitFor(() => window.Unkept!.Said.EndsWith("Arena #2's come next.", StringComparison.Ordinal),
                () => $"the second game's values to wait behind the first's, which stay listed ({window.Unkept?.Said}; {window.Status})");
            Assert.Equal("Arena", window.Unkept!.Game);
            Assert.Equal("Speed", Assert.Single(window.Unkept.Values).Name);

            first.Tuning.Discard();
            Ui.WaitFor(() => window.Unkept is { Game: "Arena #2" }, () => $"the second game's values to be listed once the first's are done with ({window.Unkept?.Said})");
            Assert.Equal("Tint", Assert.Single(window.Unkept!.Values).Name);
            Assert.Equal("Arena #2 ended with 1 value changed and not kept. Keep one where it comes from, or all of them:", window.Unkept.Said);

            second.Tuning.Discard();
            Ui.WaitFor(() => window.Unkept is null && window.LiveTree.Aside is null, "the list to go once nothing is left to keep");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            window.Close();
        }
    }

    /// <summary>A game taken as a launch's and so followed, one of its values set from the properties pane.</summary>
    private static GodotGame Taken(MainWindow window, string name, string value)
    {
        string folder = TestFolders.Create("godot-unkept-game-");
        (_, GodotGame? game) = GodotGame.Listen(new LaunchPlan { Name = name, Program = "godot", WorkingDirectory = folder, Godot = folder });
        Assert.NotNull(game);
        window.Session.TakeGame(game);
        game.Tuning.Set(1, new GodotNodePlace([("Main", "res://main.tscn")], null), new GodotProperty(value, GodotType.Float, 0, string.Empty, 4102, 1.0, "Main"), 2.0);
        return game;
    }
}
