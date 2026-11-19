using System.Collections.Concurrent;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A Parallel loop run through an <see cref="ApartScheduler"/> runs on the scheduler's threads alone, so a loop
/// that keeps every one of them busy (a decompile, an evaluation) takes none of the thread pool's, which a debugger's or a
/// host's answers need; its threads end once it is disposed. The test waits for each with no limit of its own, so a busy
/// machine only slows it: one that never comes holds it, which the run's own limit on a test names.</summary>
public class ApartSchedulerTests
{
    [Fact]
    public async Task A_loop_given_an_apart_scheduler_runs_on_its_threads_alone_and_they_end_once_it_is_disposed()
    {
        const int threads = 4;
        ConcurrentDictionary<Thread, bool> ran = [];
        using ManualResetEventSlim letGo = new();
        using CountdownEvent arrived = new(threads);
        Task loop;
        using (ApartScheduler scheduler = new("apart", threads))
        {
            loop = BackgroundWork.UnheldThread(() => scheduler.ForEach(Enumerable.Range(0, 1000).ToList(), 0, TestContext.Current.CancellationToken, _ =>
            {
                if (ran.TryAdd(Thread.CurrentThread, Thread.CurrentThread.IsThreadPoolThread))
                {
                    arrived.Signal();
                }
                letGo.Wait();
            }));
            // Waits for good where the loop runs on fewer threads than the scheduler was given.
            arrived.Wait(TestContext.Current.CancellationToken);
            letGo.Set();
            await loop.WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.True(ran.Count == threads && !ran.Values.Contains(true),
            $"The loop ran on {ran.Count} threads, {ran.Values.Count(onPool => onPool)} of them the pool's, which a loop as long as a decompile would keep from everything else the process awaits");
        foreach (Thread thread in ran.Keys)
        {
            // Waits for good where a thread of the scheduler outlives its disposal, as it would the process's whole life.
            thread.Join();
        }
    }
}
