using System.Collections.Concurrent;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A Parallel loop run through an <see cref="ApartScheduler"/> runs on the scheduler's threads alone, so a loop
/// that keeps every one of them busy (a decompile, an evaluation) takes none of the thread pool's, which a debugger's or a
/// host's answers need; its threads end once it is disposed.</summary>
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
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                $"{threads - arrived.CurrentCount} of the scheduler's {threads} threads came to the loop: it ran on fewer than it was given");
            letGo.Set();
            await loop.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        Assert.True(ran.Count == threads && !ran.Values.Contains(true),
            $"The loop ran on {ran.Count} threads, {ran.Values.Count(onPool => onPool)} of them the pool's, which a loop as long as a decompile would keep from everything else the process awaits");
        Assert.True(ran.Keys.All(thread => thread.Join(TimeSpan.FromSeconds(30))), "A thread of the scheduler was still running after it was disposed, and would be for as long as the process lives");
    }
}
