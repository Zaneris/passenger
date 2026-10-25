namespace Passenger.Core;

/// <summary>Work started that nothing awaits, for code that knows nothing of the app: a fault goes, as the task faults,
/// to the reporter the app gives as it starts (<see cref="Install"/>), never only to the task's finalizer. In a process
/// that gives none, the fault is left unobserved.</summary>
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
}
