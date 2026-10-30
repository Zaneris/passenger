namespace Passenger.App;

/// <summary>Whether the work in this flow is an attached agent's beside the person, flowing into what it starts on the
/// window's thread and off it: an agent's tool call (<see cref="Beside"/>) shows a tool window behind, never bringing a
/// window forward or taking the keys; an action an agent runs (ide_action, <see cref="Act"/>) opens no popup, and no
/// dialog as it runs, either of which would take the keys from what the person types; a dialog work it started asks
/// later (a run's question) comes without them.</summary>
public static class AgentAsks
{
    private static readonly AsyncLocal<bool> beside = new();
    private static readonly AsyncLocal<bool> acting = new();

    /// <summary>How deep the window's thread is in an agent's action's own run (<see cref="Act"/>), as it is asked for.</summary>
    private static int running;

    /// <summary>Whether this flow is an attached agent's tool call, or work it started.</summary>
    public static bool Beside => beside.Value;

    /// <summary>Marks the rest of an async method's flow (a tool call's) as an attached agent's; its caller's is left as it was.</summary>
    public static void Call() => beside.Value = true;

    /// <summary>Runs an action an attached agent asks for, its flow and what it starts marked so; null once it has run, else
    /// why it was refused as it ran (a popup or dialog asked for at once). On the window's thread.</summary>
    public static string? Act(Action run)
    {
        bool was = acting.Value;
        bool wasBeside = beside.Value;
        acting.Value = beside.Value = true;
        running++;
        try
        {
            run();
            return null;
        }
        catch (AgentRefusedException refused)
        {
            return refused.Message;
        }
        finally
        {
            running--;
            acting.Value = was;
            beside.Value = wasBeside;
        }
    }

    /// <summary>Refuses a popup in an agent's action's flow, the action ending there as one canceled, the agent told why
    /// where it was asked for as the action ran.</summary>
    public static void Popup()
    {
        if (acting.Value)
        {
            throw new AgentRefusedException(Refusals.AsksThePerson);
        }
    }

    /// <summary>For a dialog or window that asks the person: refused as an agent's action runs, the agent told why; asked
    /// later by work an agent's action started, whether it comes without the keys.</summary>
    public static bool Dialog()
    {
        if (running > 0)
        {
            throw new AgentRefusedException(Refusals.AsksThePerson);
        }
        return acting.Value;
    }
}

/// <summary>A popup or dialog an attached agent's action would have opened, refused: a cancellation, so work awaiting it
/// ends as the person's Cancel ends it, and nothing reports it as the IDE's error.</summary>
public sealed class AgentRefusedException(string why) : OperationCanceledException(why);
