using System.Collections.Concurrent;

namespace Passenger.Core;

/// <summary>Tasks run on threads of the scheduler's own (<see cref="ApartThreads"/>), never the thread pool's: for a Parallel
/// loop given it in its options that keeps every processor busy for long (a decompile), which on the pool's threads would
/// hold every one of them, and every await of the process with them, until it ends. Its threads end once it is disposed
/// and they have run what it was given.</summary>
public sealed class ApartScheduler : TaskScheduler, IDisposable
{
    /// <summary>The scheduler whose thread this is, if any.</summary>
    [ThreadStatic]
    private static ApartScheduler? serving;

    private readonly BlockingCollection<Task> queued = [];
    private readonly int threads;

    /// <summary>A scheduler of <paramref name="threads"/> threads, each named <paramref name="name"/> and its number.</summary>
    public ApartScheduler(string name, int threads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        this.threads = threads;
        for (int each = 1; each <= threads; each++)
        {
            ApartThreads.Start($"{name} {each}", Serve);
        }
    }

    public override int MaximumConcurrencyLevel => threads;

    public void Dispose() => queued.CompleteAdding();

    protected override void QueueTask(Task task) => queued.Add(task);

    /// <summary>Only on a thread of its own, so a loop's caller waits rather than runs the loop on its thread, and a task
    /// of the scheduler's waiting for another of them never waits on threads all taken; one taken from the queue after
    /// it ran here is not run again.</summary>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => serving == this && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => queued.ToArray();

    private void Serve()
    {
        serving = this;
        foreach (Task task in queued.GetConsumingEnumerable())
        {
            TryExecuteTask(task);
        }
    }
}
