using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A wait for work however it ends (UnawaitedWork.Ended): it ends once the work has, and never fails, whether the
/// work ran to its end, failed or was cancelled.</summary>
public class UnawaitedWorkTests
{
    [Fact]
    public async Task A_wait_for_work_however_it_ends_ends_with_the_work_and_never_fails()
    {
        TaskCompletionSource ran = new();
        TaskCompletionSource failed = new();
        TaskCompletionSource cancelled = new();
        Task[] waits = [ran.Task.Ended(), failed.Task.Ended(), cancelled.Task.Ended()];
        Assert.All(waits, wait => Assert.False(wait.IsCompleted, "A wait for work still under way ended before the work did"));

        ran.SetResult();
        failed.SetException(new InvalidOperationException("The work failed"));
        cancelled.SetCanceled(TestContext.Current.CancellationToken);
        // A guard only: the waits end as the pool runs them.
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.All(waits, wait => Assert.True(wait.Status == TaskStatus.RanToCompletion,
            $"A wait for work however it ends was {wait.Status}: what awaits it would throw the work's fault in its place"));
        Assert.True(cancelled.Task.IsCanceled, "Waiting on the cancelled work changed how it ended");
        Assert.Equal("The work failed", failed.Task.Exception?.InnerException?.Message);
    }
}
