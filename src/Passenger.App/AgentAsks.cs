using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Passenger.App;

/// <summary>Whether the work in this flow is an attached agent's beside the person, flowing into what it starts on the
/// window's thread and off it: an agent's tool call (<see cref="Beside"/>) shows a tool window or a file behind, never
/// bringing a window forward or taking the keys; an action an agent runs (ide_action, <see cref="Act"/>) opens no popup,
/// menu or modal dialog and moves no keys from where the person has them, as it runs or in the work it starts (the one
/// check of each, <see cref="Install"/>); a window that asks, opened by that work later, comes without the keys.</summary>
public static class AgentAsks
{
    private static readonly AsyncLocal<bool> beside = new();
    private static readonly AsyncLocal<Acting?> acting = new();

    /// <summary>Whether this flow is an attached agent's tool call, or work it started.</summary>
    public static bool Beside => beside.Value;

    /// <summary>Marks the rest of an async method's flow (a tool call's) as an attached agent's; its caller's is left as it was.</summary>
    public static void Call() => beside.Value = true;

    /// <summary>The process's checks of what an agent's action opens or focuses, wherever the IDE opens it: a focus move
    /// away from the person's keys is cancelled, a popup of Avalonia's (a menu, a flyout) closes as it opens, refused, and
    /// a modal dialog, which would hold the person's window until answered, is refused before it shows. Added once.</summary>
    public static void Install()
    {
        InputElement.LosingFocusEvent.AddClassHandler<TopLevel>((_, e) => KeepKeys(e), handledEventsToo: true);
        Avalonia.Controls.Primitives.Popup.IsOpenProperty.Changed.AddClassHandler<Avalonia.Controls.Primitives.Popup>(Opened);
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Modal(window), handledEventsToo: true);
    }

    /// <summary>Runs an action an attached agent asks for, its flow and what it starts marked so; null once it has run, else
    /// why it was refused as it ran (a popup, menu or dialog asked for at once). On the window's thread.</summary>
    public static string? Act(Action run)
    {
        Acting? was = acting.Value;
        bool wasBeside = beside.Value;
        Acting act = new();
        acting.Value = act;
        beside.Value = true;
        try
        {
            run();
            return act.Refused;
        }
        catch (AgentRefusedException refused)
        {
            return refused.Message;
        }
        finally
        {
            act.Running = false;
            acting.Value = was;
            beside.Value = wasBeside;
        }
    }

    /// <summary>Refuses a popup in an agent's action's flow, the action ending there as one canceled, the agent told why
    /// where it was asked for as the action ran.</summary>
    public static void Popup()
    {
        if (acting.Value is not null)
        {
            throw new AgentRefusedException(Refusals.AsksThePerson);
        }
    }

    /// <summary>Whether a popup is refused, being asked for in an agent's action's flow, the agent told why where the action
    /// still runs: its host then answers it as the person's Escape would, without showing it.</summary>
    public static bool Refused()
    {
        if (acting.Value is not { } act)
        {
            return false;
        }
        if (act.Running)
        {
            act.Refused = Refusals.AsksThePerson;
        }
        return true;
    }

    /// <summary>For a dialog or window that asks the person: refused as an agent's action runs, the agent told why; asked
    /// later by work an agent's action started, whether it comes without the keys.</summary>
    public static bool Dialog()
    {
        if (acting.Value is { Running: true })
        {
            throw new AgentRefusedException(Refusals.AsksThePerson);
        }
        return acting.Value is not null;
    }

    /// <summary>Leaves the keys where the person has them as an agent's action moves them: what would take them (a find
    /// bar's box, a tool window's) shows without them. Keys gone with what held them may go elsewhere.</summary>
    private static void KeepKeys(FocusChangingEventArgs e)
    {
        if (acting.Value is not null && e.OldFocusedElement is Visual { IsEffectivelyVisible: true } had && TopLevel.GetTopLevel(had) is not null)
        {
            e.TryCancel();
        }
    }

    /// <summary>Closes a popup of Avalonia's an agent's action opened (a context menu, a menu flyout) as it opens, before it
    /// is drawn: the agent told it was refused where the action was still running.</summary>
    private static void Opened(Avalonia.Controls.Primitives.Popup popup, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && Refused())
        {
            popup.IsOpen = false;
        }
    }

    /// <summary>Refuses a modal dialog an agent's action would show, as it runs or later, before it shows.</summary>
    private static void Modal(Window window)
    {
        if (acting.Value is not null && window.IsDialog)
        {
            throw new AgentRefusedException(Refusals.AsksThePerson);
        }
    }

    /// <summary>One agent's action: whether it is still running on the window's thread, and what it was refused meanwhile.</summary>
    private sealed class Acting
    {
        public bool Running { get; set; } = true;

        public string? Refused { get; set; }
    }
}

/// <summary>A popup or dialog an attached agent's action would have opened, refused: a cancellation, so work awaiting it
/// ends as the person's Cancel ends it, and nothing reports it as the IDE's error.</summary>
public sealed class AgentRefusedException(string why) : OperationCanceledException(why);
