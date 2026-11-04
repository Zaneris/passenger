using Avalonia.Input;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>The Game window (<see cref="Godot.GamePane"/>): a game whose configuration draws it in Passenger started with the
/// window's handle (<see cref="Godot.GameWindows.Arguments"/>, its own placing of its window left out) and held over the
/// pane; where it cannot be (headless, a Godot before 4.4, a system with no embedding, the pane holding another), it runs in
/// a window of its own, said once. Give the Keys to the Game hands it the keys, and the same keys, heard by Passenger's
/// helper in the game, take them back.</summary>
public partial class MainWindow
{
    private Godot.GamePane? gamePane;

    /// <summary>The Game window's pane, made as it is first asked for.</summary>
    public Godot.GamePane GamePane => gamePane ??= MakeGamePane();

    private Godot.GamePane MakeGamePane()
    {
        Godot.GamePane pane = new(Refusals.NoGameInWindow(Actions.Shortcut(AppActions.GameKeys) ?? "Give the Keys to the Game"));
        pane.KeysAsked += (_, _) => TakeKeysFromGame();
        return pane;
    }

    private void AddGamePane()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Game, "Game", IconKeys.Preview, () => GamePane, Docking.ToolAnchor.RightTop) { Action = AppActions.ShowGame });
        Actions.Add(new IdeAction(AppActions.GameKeys, "Give the Keys to the Game", "Run", _ =>
        {
            Dock.Activate(ToolWindowIds.Game);
            GamePane.GiveKeys();
        })
        {
            WhyNot = _ => gamePane is { GameWindow: not 0 } ? null : Refusals.NoGameInPane,
        });
    }

    /// <summary>The session's games whose configurations draw them in the Game window come to it as they start.</summary>
    private void BindGamePane(ProjectSession next) => next.InPane = InPane;

    /// <summary>A game's plan given what draws it over the Game window, the pane given the game to hold; the plan as it was,
    /// its own window said once and why, where it cannot.</summary>
    private LaunchPlan InPane(LaunchPlan plan, Godot.GodotGame game)
    {
        UiThread.Check();
        Godot.GameWindows? windows = Unseen ? null : GamePane.Windows(this);
        string? why = Unseen || plan.GodotUnseen || GodotExecutables.RunsHeadless(plan.Arguments) ? Refusals.GameHeadless(plan.Name)
            : !GodotExecutables.Embeds(plan.GodotVersion) ? Refusals.GameEmbedsFrom(plan.GodotVersion)
            : windows is null ? Refusals.GameNotEmbeddedHere
            : GamePane.Game is { State: not LiveAppState.Ended } held ? Refusals.GameWindowHolds(held.Name)
            : null;
        if (why is not null || windows is null)
        {
            GamePane.Refused(why ?? Refusals.GameNotEmbeddedHere);
            status.Text = $"{plan.Name} draws in a window of its own: {why}";
            return plan;
        }
        // The game's own placing of its window, which the pane's place stands for, is left out of its arguments, up to its own.
        List<string> own = [];
        bool past = false;
        for (int i = plan.Leading; i < plan.Arguments.Count; i++)
        {
            string argument = plan.Arguments[i];
            past |= argument is "--" or "++";
            if (!past && Godot.GameWindows.Placing.TryGetValue(argument, out bool valued))
            {
                i += valued ? 1 : 0;
                continue;
            }
            own.Add(argument);
        }
        List<string> arguments = [.. plan.Arguments.Take(plan.Leading), .. windows.Arguments, .. own];
        Dock.ShowBehind(ToolWindowIds.Game);
        GamePane.Hold(game);
        IdeErrors.Observe(HandKeysBackAsync(game));
        return plan with { Arguments = arguments, Leading = plan.Leading + windows.Arguments.Count };
    }

    /// <summary>The keys of Give the Keys to the Game told to Passenger's helper in the game once its first scene is open, so
    /// the same keys, pressed while the game has them, hand them back; where the helper cannot hear them, only a click does.</summary>
    private async Task HandKeysBackAsync(Godot.GodotGame game)
    {
        if (AppActions.Keymap().ShortcutsFor(AppActions.KeysIn(AppActions.GameKeys)).FirstOrDefault(s => s.Second is null) is not { First: var keys })
        {
            return;
        }
        int modifiers = (keys.Modifiers.HasFlag(KeyModifiers.Shift) ? Godot.GodotAgent.Shift : 0) | (keys.Modifiers.HasFlag(KeyModifiers.Control) ? Godot.GodotAgent.Control : 0)
            | (keys.Modifiers.HasFlag(KeyModifiers.Alt) ? Godot.GodotAgent.Alt : 0) | (keys.Modifiers.HasFlag(KeyModifiers.Meta) ? Godot.GodotAgent.Meta : 0);
        try
        {
            await game.FirstSceneAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
            await game.AskAsync(Godot.GodotAgent.KeysBack, Godot.GodotAgent.KeyName(keys.Key.ToString()), modifiers);
        }
        catch (LiveAppException unheard)
        {
            status.Text = $"Only a click in Passenger takes the keys back from {game.Name}: {unheard.Message}";
        }
    }

    /// <summary>Passenger takes the keys back from the game drawn in the Game window: its window in front, the editor given them.</summary>
    private void TakeKeysFromGame()
    {
        UiThread.Check();
        Activate();
        Actions.Run(AppActions.FocusEditor);
    }
}
