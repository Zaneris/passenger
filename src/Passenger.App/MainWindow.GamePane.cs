using Avalonia;
using Avalonia.Input;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>The Game window (<see cref="Godot.GamePane"/>): a game whose configuration draws it in Passenger started with the
/// window's handle and the pane's place (<see cref="Godot.GameWindows.ArgumentsAt"/>, its own placing left out), held over the
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
        // A layered game hears every key but those that take its keys back.
        pane.Keeps = e => AppActions.Keymap().ShortcutsFor(AppActions.KeysIn(AppActions.GameKeys))
            .Any(s => s.Second is null && s.First.Key == e.Key && s.First.Modifiers == e.KeyModifiers);
        return pane;
    }

    private void AddGamePane()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Game, "Game", IconKeys.Preview, () => GamePane, Docking.ToolAnchor.RightTop) { Action = AppActions.ShowGame });
        Actions.Add(new IdeAction(AppActions.GameKeys, "Give the Keys to the Game", "Run", _ =>
        {
            // The same keys take them back from a game drawn as a layer, whose keys are Passenger's to hear.
            if (GamePane is { Layered: true, IsFocused: true })
            {
                TakeKeysFromGame();
                return;
            }
            Dock.Activate(ToolWindowIds.Game);
            GamePane.GiveKeys();
        })
        {
            WhyNot = _ => gamePane is { GameWindow: not 0 } or { Layered: true } ? null : Refusals.NoGameInPane,
        });
    }

    /// <summary>The session's games whose configurations draw them in the Game window come to it as they start.</summary>
    private void BindGamePane(ProjectSession next) => next.InPane = InPane;

    /// <summary>A game's plan given what draws it over the Game window, the pane given the game to hold; the plan as it was,
    /// its own window said once and why, where it cannot.</summary>
    private LaunchPlan InPane(LaunchPlan plan, Godot.GodotGame game)
    {
        UiThread.Check();
        Godot.GameWindows? windows = Unseen || OperatingSystem.IsMacOS() ? null : GamePane.Windows(this);
        (Godot.GamePaneHolds holds, string? why) = InPaneChoice(plan.Name, Unseen, plan.GodotUnseen || GodotExecutables.RunsHeadless(plan.Arguments), plan.GodotVersion,
            OperatingSystem.IsMacOS(), windows is not null, Godot.GameLayer.Hosts, GamePane.Game is { State: not LiveAppState.Ended } holding ? holding.Name : null);
        if (holds == Godot.GamePaneHolds.Nothing)
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
        // On its place in the pane where it has one, as Godot's editor starts an embedded game; on macOS its display at that size.
        GamePane.OwnSize = Godot.GameFit.OwnSize([.. plan.Arguments.Skip(plan.Leading)], plan.GodotSize);
        PixelRect? place = GamePane.ScreenPlace();
        IReadOnlyList<string> embedding = holds == Godot.GamePaneHolds.Layer
            ? GodotExecutables.EmbeddedIn(place?.Width ?? GamePane.OwnSize.Width, place?.Height ?? GamePane.OwnSize.Height)
            : windows!.ArgumentsAt(place);
        List<string> arguments = [.. plan.Arguments.Take(plan.Leading), .. embedding, .. own];
        Dock.ShowBehind(ToolWindowIds.Game);
        if (holds == Godot.GamePaneHolds.Layer)
        {
            GamePane.HoldLayer(game);
        }
        else
        {
            GamePane.Hold(game);
            IdeErrors.Observe(HandKeysBackAsync(game));
        }
        if (!plan.GodotStretches)
        {
            IdeErrors.Observe(ScaleGameAsync(game));
        }
        return plan with { Arguments = arguments, Leading = plan.Leading + embedding.Count };
    }

    /// <summary>How the Game window holds a game, or why it holds none: the game's own window held over it (Windows, X11, from
    /// Godot 4.4, where the window has an embedding), or its layer drawn in it (macOS, from Godot 4.5, where QuartzCore has a
    /// CALayerHost); none for a game nobody sees or run headless, or while the pane holds another, each with the words why.</summary>
    public static (Godot.GamePaneHolds Holds, string? WhyNot) InPaneChoice(string game, bool unseen, bool headless, string? version, bool macOS,
        bool embedding, bool layerHost, string? holding) =>
        unseen || headless ? (Godot.GamePaneHolds.Nothing, Refusals.GameHeadless(game))
        : macOS && !GodotExecutables.DrawsEmbedded(version) ? (Godot.GamePaneHolds.Nothing, Refusals.GameEmbedsOnMacFrom(version))
        : macOS && !layerHost ? (Godot.GamePaneHolds.Nothing, Refusals.GameNoLayerHost)
        : !macOS && !GodotExecutables.Embeds(version) ? (Godot.GamePaneHolds.Nothing, Refusals.GameEmbedsFrom(version))
        : !macOS && !embedding ? (Godot.GamePaneHolds.Nothing, Refusals.GameNotEmbeddedHere)
        : holding is not null ? (Godot.GamePaneHolds.Nothing, Refusals.GameWindowHolds(holding))
        : (macOS ? Godot.GamePaneHolds.Layer : Godot.GamePaneHolds.Window, null);

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

    /// <summary>A game whose project stretches nothing, which would show only the part of it its place holds, scaled to its
    /// place by Passenger's helper once its first scene is open; where the helper cannot hear, it draws as its project says.</summary>
    private async Task ScaleGameAsync(Godot.GodotGame game)
    {
        try
        {
            await game.FirstSceneAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
            await game.AskAsync(Godot.GodotAgent.ScaleToWindow);
        }
        catch (LiveAppException unheard)
        {
            status.Text = $"{game.Name} shows in the Game window as its project draws it: {unheard.Message}";
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
