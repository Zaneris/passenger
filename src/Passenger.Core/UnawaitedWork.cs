using System.Runtime.CompilerServices;

namespace Passenger.Core;

/// <summary>Work started that nothing awaits, for code that knows nothing of the app: a fault goes, as the task faults,
/// to the reporter the app gives as it starts (<see cref="Install"/>), never only to the task's finalizer. In a process
/// that gives none, the fault is left unobserved. Also the work whose fault is no one's to report (<see cref="Dismiss"/>),
/// and a wait for work however it ends (<see cref="Ended"/>), or until a time has passed (<see cref="EndedWithin"/>).</summary>
public static class UnawaitedWork
{
    private static volatile Action<Exception, string>? reporter;

    /// <summary>Gives <see cref="Observe"/> the app's way of reporting an error, which only the app knows: the error, and
    /// the place its work was observed (<see cref="ObservedAt"/>).</summary>
    public static void Install(Action<Exception, string> report) => reporter = report;

    /// <summary>Reports the work's fault as it faults, its one exception as itself, with the place it was observed; a
    /// cancelled task is no fault.</summary>
    public static void Observe(Task work, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        _ = work.ContinueWith(faulted =>
        {
            if (reporter is { } report)
            {
                AggregateException error = faulted.Exception!;
                report(error.InnerExceptions.Count == 1 ? error.InnerException! : error, ObservedAt(member, file, line));
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>The words for the place work was given to be observed, as a frame of a stack reads them.</summary>
    public static string ObservedAt(string member, string file, int line) => $"{member} in {file}:line {line}, where its task was observed";

    /// <summary>An error's words for a log: the exception whole; one never thrown (a timed wait's, a task's set by hand)
    /// carries no stack, so the place it was observed follows it as its one frame.</summary>
    public static string Details(Exception error, string? observedAt) =>
        error.StackTrace is null && observedAt is not null ? $"{error}\n   at {observedAt}" : error.ToString();

    /// <summary>Lets go of work whose fault is expected and no one's to hear (a read its end cuts short, a request given
    /// up): the fault is marked seen as it faults, never reported, nor left for the finalizer.</summary>
    public static void Dismiss(Task work) =>
        _ = work.ContinueWith(static faulted => _ = faulted.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>A task that ends once the work has, however it ended, and never fails: what waits on it goes on from the
    /// thread pool, never inline as the work ends. The work's fault stays its own to observe.</summary>
    public static Task Ended(this Task work) =>
        work.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>A task that ends once the work has or <paramref name="limit"/> has passed (or <paramref name="cancel"/> is
    /// cancelled), however it ended, and never fails, as <see cref="Ended"/>: the limit's passing is no one's fault to hear,
    /// where a timed wait's own task given to <see cref="Ended"/> would leave its timeout for the finalizer. The work's fault
    /// stays its own to observe.</summary>
    public static Task EndedWithin(this Task work, TimeSpan limit, CancellationToken cancel = default) =>
        work.WaitAsync(limit, cancel).ContinueWith(static waited => _ = waited.Exception, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
}
