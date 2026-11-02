using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A debugged Godot game's audio muted from the Debug window, as a person mutes it: the toggle there while the game is
/// debugged, its choice kept with the folder for the next debugging. Godot answers nothing to its debug mute, and no script
/// can read it, so what shows Godot took the mute is the game raising no error over it and answering on (a mute it could
/// not read it refuses with an error). The game is GodotGameLoopTests' project of GDScript alone, run headless with the
/// run's user folders (whose audio driver plays nothing). Without a Godot 4 each test skips saying why. Debugged, so run
/// alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotAudioTests : IClassFixture<GodotGameLoopTests.Game>
{
    private readonly GodotGameLoopTests.Game game;

    public GodotAudioTests(GodotGameLoopTests.Game game)
    {
        this.game = game;
        Assert.SkipWhen(game.Godot?.WhyNotScripts is not null || game.Godot is null, game.Skipped);
    }

    /// <summary>Mute Game Audio shows in the Debug window for the game, unpressed; pressed, the game is muted, raising no error
    /// over it and answering on; the folder keeps it, so the next debugging starts muted with the toggle pressed, and pressed
    /// again the game plays and the folder keeps that.</summary>
    [AvaloniaFact]
    public void Mute_Game_Audio_mutes_the_debugged_game_and_the_folder_keeps_it_for_the_next_debugging()
    {
        // A person's window, which keeps the folder's own state, in a state folder of its own.
        MainWindow window = new()
        {
            Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = game.Homes, StateFolder = TestFolders.Create("godot-audio-state-"),
        };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(game.Folder);
        Ui.Settle();
        try
        {
            ToggleButton toggle = window.BottomPanel.Debug.GameAudio;
            GodotGame debugged = Debug(window);
            Ui.WaitFor(() => toggle.IsVisible, () => $"Mute Game Audio to show for the game debugged ({window.Debugger.Chosen?.Name}; {window.Status})");
            Assert.True(toggle.IsChecked == false && !debugged.AudioMuted, "The game started muted with nothing kept to mute it");

            Ui.Click(toggle);
            Ui.WaitFor(() => debugged.AudioMuted && toggle.IsChecked == true && window.Status?.Contains("audio is muted", StringComparison.Ordinal) == true,
                () => $"the game to be muted and the toggle pressed ({debugged.AudioMuted}, {toggle.IsChecked}; {window.Status})");
            Ui.AwaitPumped(debugged.SceneTreeAsync(), "the muted game's tree, which it answers once it has read the mute before it", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(window.BottomPanel.Problems.GodotProblems, p => p.Message.Contains("mute", StringComparison.OrdinalIgnoreCase) || p.Message.Contains("p_args", StringComparison.Ordinal));
            Stop(window);

            GodotGame again = Debug(window);
            Ui.WaitFor(() => toggle.IsVisible && toggle.IsChecked == true && again.AudioMuted,
                () => $"the next debugging to start muted, as the folder keeps it ({again.AudioMuted}, {toggle.IsVisible}, {toggle.IsChecked})");
            Ui.Click(toggle);
            Ui.WaitFor(() => !again.AudioMuted && toggle.IsChecked == false && window.Session.State?.Godot.Value.AudioMuted == false,
                () => $"the game to play again and the folder to keep that ({again.AudioMuted}, {toggle.IsChecked}, kept {window.Session.State?.Godot.Value.AudioMuted}; {window.Status})");
            Stop(window);
        }
        finally
        {
            if (window.Debugger.IsDebugging || window.IsRunning)
            {
                Assert.Null(window.Actions.Run(AppActions.StopAll));
                Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the game to end ({window.Status})", TimeSpan.FromMinutes(1));
            }
            window.Close();
        }
    }

    /// <summary>The game debugged, headless with the run's user folders, once it has connected to Passenger.</summary>
    private GodotGame Debug(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == "Arena"),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == "Arena");
        window.SetConfigurations([generated with { Name = "Arena on Godot", Generated = false, Arguments = "--headless", Environment = new(game.Homes) }], "Arena on Godot");
        Ui.Launched(window, window.DebugAsync(), "the game's start under the debugger", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => window.Session.Game is { State: LiveAppState.Connected, Debugged: not null },
            () => $"the game to connect under the debugger ({window.Session.Game?.State}; {window.Status})", TimeSpan.FromMinutes(1));
        return window.Session.Game!;
    }

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging && window.Session.Games.Count == 0, () => $"the game to end ({window.Status})", TimeSpan.FromMinutes(1));
    }
}
