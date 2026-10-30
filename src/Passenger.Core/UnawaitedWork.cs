namespace Passenger.Core;

/// <summary>Work started that nothing awaits, for code that knows nothing of the app: a fault goes, as the task faults,
/// to the reporter the app gives as it starts (<see cref="Install"/>), never only to the task's finalizer. In a process
/// that gives none, the fault is left unobserved. Also the work whose fault is no one's to report (<see cref="Dismiss"/>),
/// and a wait for work however it ends (<see cref="Ended"/>).</summary>
public static class UnawaitedWork
{
    private static volatile Action<Exception>? reporter;

    /// <summary>Gives <see cref="Observe"/> the app's way of reporting an error, which only the app knows.</summary>
    public static void Install(Action<Exception> report) => reporter = report;

    /// <summary>Reports the work's fault as it faults, its one exception as itself; a cancelled task is no fault.</summary>
    public static void Observe(Task work) =>
        _ = work.ContinueWith(static faulted =>
        {
            if (reporter is { } report)
            {
                AggregateException error = faulted.Exception!;
                report(error.InnerExceptions.Count == 1 ? error.InnerException! : error);
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>Lets go of work whose fault is expected and no one's to hear (a read its end cuts short, a request given
    /// up): the fault is marked seen as it faults, never reported, nor left for the finalizer.</summary>
    public static void Dismiss(Task work) =>
        _ = work.ContinueWith(static faulted => _ = faulted.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>A task that ends once the work has, however it ended, and never fails: what waits on it goes on from the
    /// thread pool, never inline as the work ends. The work's fault stays its own to observe.</summary>
    public static Task Ended(this Task work) =>
        work.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
}
