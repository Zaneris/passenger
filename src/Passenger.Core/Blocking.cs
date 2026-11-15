namespace Passenger.Core;

/// <summary>The libraries' one synchronous wait on a task (PSG0004 passes over no other), for a caller that must stay
/// synchronous: a getter answered at once, or an interface's call that answers before it returns. The thread is held until
/// the task ends, so only a task that no thread the caller holds must run to end is waited for here, or the wait never
/// ends. Each caller says why it cannot await.</summary>
public static class Blocking
{
    /// <summary>Holds this thread until <paramref name="task"/> has ended, however it ended: its outcome, a fault too, is
    /// left to the caller to read, and no wait handle of the system's is made for it.</summary>
    public static void Until(Task task) => task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
}
