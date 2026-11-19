using System.Collections.Concurrent;

namespace Passenger.Core;

/// <summary>Tasks run on threads of the scheduler's own (<see cref="ApartThreads"/>), never the thread pool's: the one way a
/// Parallel loop runs (<see cref="ForEach"/>, PSG0010), as a loop keeping every processor busy on the pool's threads would
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

    /// <summary>A Parallel.ForEach over <paramref name="items"/>, each worker taking the next in their order, on threads of
    /// its own named <paramref name="name"/>: as many as <paramref name="most"/> (every processor's where it is 0 or less)
    /// and the items allow, ended with the loop. Cancelled, it starts no item more and throws.</summary>
    public static void ForEach<T>(string name, IList<T> items, int most, CancellationToken cancel, Action<T> body)
    {
        int count = Math.Min(most > 0 ? most : Environment.ProcessorCount, items.Count);
        if (count == 0)
        {
            cancel.ThrowIfCancellationRequested();
            return;
        }
        using ApartScheduler scheduler = new(name, count);
        scheduler.ForEach(items, count, cancel, body);
    }

    /// <summary>A Parallel.ForEach over <paramref name="items"/>, each worker taking the next in their order, on this
    /// scheduler's threads, as many at once as <paramref name="most"/> allows (all of them where it is 0 or less).</summary>
    public void ForEach<T>(IList<T> items, int most, CancellationToken cancel, Action<T> body) =>
        Parallel.ForEach(Partitioner.Create(items, loadBalance: true),
            new ParallelOptions { MaxDegreeOfParallelism = most > 0 ? Math.Min(most, threads) : threads, CancellationToken = cancel, TaskScheduler = this }, body);

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
