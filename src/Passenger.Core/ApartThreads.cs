namespace Passenger.Core;

/// <summary>A thread of its own for work that lasts (a reader or writer to its stream's end, a log's writer), or a timer kept
/// for the process's life, started apart from whoever starts it: the starter's execution context, in which a call's
/// AsyncLocals ride (an agent's tools, and through them its window), would otherwise be held for as long as either lives.</summary>
public static class ApartThreads
{
    /// <summary>Starts <paramref name="work"/> on a background thread named <paramref name="name"/>, in no caller's context.</summary>
    public static Thread Start(string name, ThreadStart work)
    {
        Thread thread = new(work) { IsBackground = true, Name = name };
        thread.UnsafeStart();
        return thread;
    }

    /// <summary>A timer, stopped until changed, whose callback runs in no caller's context.</summary>
    public static Timer Timer(TimerCallback callback)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return new Timer(callback);
        }
    }
}
