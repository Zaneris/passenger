using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A wait for work however it ends (UnawaitedWork.Ended): it ends once the work has, and never fails, whether the
/// work ran to its end, failed or was cancelled; one held to a time (EndedWithin) ends once that has passed too.</summary>
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
        TaskCompletionSource unending = new();
        Task within = unending.Task.EndedWithin(TimeSpan.Zero, TestContext.Current.CancellationToken);
        await within.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(within.Status == TaskStatus.RanToCompletion, $"A wait held to a time that passed was {within.Status}, its timeout thrown at what awaits it");

        ran.SetResult();
        failed.SetException(new InvalidOperationException("The work failed"));
        cancelled.SetCanceled(TestContext.Current.CancellationToken);
        await Task.WhenAll(waits).WaitAsync(TestContext.Current.CancellationToken);

        Assert.All(waits, wait => Assert.True(wait.Status == TaskStatus.RanToCompletion,
            $"A wait for work however it ends was {wait.Status}: what awaits it would throw the work's fault in its place"));
        Assert.True(cancelled.Task.IsCanceled, "Waiting on the cancelled work changed how it ended");
        Assert.Equal("The work failed", failed.Task.Exception?.InnerException?.Message);
    }
}
