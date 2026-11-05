using Avalonia.Controls.Primitives;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>What a window lends a game's audio: its Debug window's toolbar toggle, its actions, the status bar, and whether it
/// is an agent's own.</summary>
public sealed record GameAudioParts(ToggleButton Toggle, ActionRegistry Actions, Action<string> Say, Func<bool> AgentsOwn);

/// <summary>The audio of the Godot game a Debug tab debugs, muted and let play again from the Debug window's toolbar (the
/// toggle there while the chosen tab's program is a game, pressed while it is muted) or Mute Game Audio, through Godot's own
/// debug mute: the game plays on, silent. What the person chooses is kept with the folder, and each debugging of a game there
/// starts so; a game an agent starts starts muted, whatever the folder keeps, and an agent's choice is not kept. A UI-thread
/// object of the window.</summary>
public sealed class GameAudioCommands
{
    private readonly GameAudioParts parts;
    private ProjectSession? session;
    private GodotGame? followed;

    public GameAudioCommands(GameAudioParts parts)
    {
        this.parts = parts;
        parts.Toggle.IsVisible = false;
        parts.Toggle.Click += (_, _) =>
        {
            if (parts.Actions.Run(AppActions.MuteGameAudio) is { } why)
            {
                parts.Say(why);
            }
            Show();
        };
        parts.Actions.Add(new IdeAction(AppActions.MuteGameAudio, "Mute Game Audio", "Debug", context => IdeErrors.Observe(ToggleAsync(context.ByAgent)))
        {
            TitleIn = _ => Game() is { AudioMuted: true } ? "Unmute Game Audio" : "Mute Game Audio",
            IsChecked = _ => Game() is { AudioMuted: true },
            WhyNot = _ => Game() is null ? "No Godot game is being debugged: Debug one, its tab chosen in the Debug window" : null,
        });
    }

    /// <summary>The window follows a session from now: its Debug tabs and the games its launches start.</summary>
    public void Bind(ProjectSession next)
    {
        UiThread.Check();
        if (session is { } previous)
        {
            previous.Debug.SelectionChanged -= OnChanged;
            previous.Debug.TabsChanged -= OnChanged;
            previous.LiveAppChanged -= OnChanged;
            previous.GameStarted -= OnGameStarted;
        }
        session = next;
        next.Debug.SelectionChanged += OnChanged;
        next.Debug.TabsChanged += OnChanged;
        next.LiveAppChanged += OnChanged;
        next.GameStarted += OnGameStarted;
        Show();
    }

    /// <summary>A game launched starts muted where an agent launched it (in its own window, or by a call it made), or where it
    /// is debugged and the folder keeps its games muted.</summary>
    private void OnGameStarted(object? sender, GodotGame game)
    {
        // Its session is given once its debugger has started, after it may have connected.
        game.DebuggedChanged += OnChanged;
        if (parts.AgentsOwn() || Mcp.AgentCall.Current is not null || (game.ScriptsDebugged && session?.State?.Godot.Value.AudioMuted == true))
        {
            game.StartMuted();
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Show();

    /// <summary>The toggle shown while the chosen Debug tab's program is a game that runs, pressed while it is muted.</summary>
    private void Show()
    {
        GodotGame? game = Game();
        if (followed != game)
        {
            if (followed is not null)
            {
                followed.StateChanged -= OnChanged;
                followed.AudioChanged -= OnChanged;
            }
            followed = game;
            if (game is not null)
            {
                game.StateChanged += OnChanged;
                game.AudioChanged += OnChanged;
            }
        }
        parts.Toggle.IsVisible = game is not null;
        parts.Toggle.IsChecked = game is { AudioMuted: true };
    }

    /// <summary>The game the chosen Debug tab debugs (its C#, or its GDScript beside), while it has not ended.</summary>
    private GodotGame? Game()
    {
        if (session is not { Debug.Chosen: { } chosen } current)
        {
            return null;
        }
        return current.Games.FirstOrDefault(g => g.State != LiveAppState.Ended && g.Debugged is { } debugged && current.Debug.TabOf(debugged) is { } tab
            && (tab == chosen || chosen.Companion == tab || tab.Companion == chosen));
    }

    /// <summary>The chosen tab's game muted, or let play again; the person's choice kept with the folder for its later debugging.</summary>
    private async Task ToggleAsync(bool byAgent)
    {
        if (Game() is not { } game || session is not { } current)
        {
            return;
        }
        bool muted = !game.AudioMuted;
        using Working? work = parts.Actions.Work.Begin(AppActions.MuteGameAudio, muted ? $"Muting {game.Name}…" : $"Unmuting {game.Name}…", stoppable: false);
        try
        {
            await game.MuteAudioAsync(muted);
        }
        catch (LiveAppException failed)
        {
            work?.Tell(false, $"Mute Game Audio: {failed.Message}");
            parts.Say($"Mute Game Audio: {failed.Message}");
            return;
        }
        bool kept = !byAgent && Mcp.AgentCall.Current is null && current.State is not null;
        if (kept)
        {
            current.State!.Godot.Set(current.State.Godot.Value with { AudioMuted = muted });
        }
        string said = muted ? $"{game.Name}'s audio is muted{(kept ? ", as each debugging of a game in this folder starts from now" : string.Empty)}"
            : $"{game.Name}'s audio plays{(kept ? ", as each debugging of a game in this folder does from now" : string.Empty)}";
        work?.Tell(true, said);
        parts.Say(said);
        Show();
    }
}
