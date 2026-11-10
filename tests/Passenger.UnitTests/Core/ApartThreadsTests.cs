using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Work that lasts, started on a thread or timer of its own (<see cref="ApartThreads"/>, <see
/// cref="BackgroundWork.UnheldThread(Action)"/>), carries nothing of whoever started it: a value the starter's flow held
/// (an agent's tools in its call, and through them a window) is not there, so the thread keeps none of it alive.</summary>
public class ApartThreadsTests
{
    private static readonly AsyncLocal<object?> Carried = new();

    [Fact]
    public async Task A_thread_started_apart_sees_nothing_its_starter_carried()
    {
        object? seen = await SeenAsync(done =>
        {
            ApartThreads.Start("apart", () => done(Carried.Value));
            return Task.CompletedTask;
        });
        Assert.True(seen is null, $"A thread started apart saw {seen}, which its starter carried: it would keep that for as long as it lived");
    }

    [Fact]
    public async Task A_timer_made_apart_calls_back_with_nothing_its_maker_carried()
    {
        Timer? made = null;
        object? seen = await SeenAsync(done =>
        {
            made = ApartThreads.Timer(_ => done(Carried.Value));
            made.Change(0, Timeout.Infinite);
            return Task.CompletedTask;
        });
        await (made?.DisposeAsync() ?? ValueTask.CompletedTask);
        Assert.True(seen is null, $"A timer made apart called back seeing {seen}, which its maker carried: it would keep that for as long as it lived");
    }

    [Fact]
    public async Task Unheld_work_on_a_thread_of_its_own_sees_nothing_its_starter_carried()
    {
        object? seen = await SeenAsync(done => BackgroundWork.UnheldThread(() => done(Carried.Value)));
        Assert.True(seen is null, $"Unheld work on a thread of its own saw {seen}, which its starter carried: it would keep that for as long as it ran");
    }

    /// <summary>What the work <paramref name="start"/> starts saw of <see cref="Carried"/>, set in the starter's flow, once
    /// it told and the task it gave ended.</summary>
    private static async Task<object?> SeenAsync(Func<Action<object?>, Task> start)
    {
        TaskCompletionSource<object?> told = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task started;
        Carried.Value = new object();
        try
        {
            started = start(value => told.TrySetResult(value));
        }
        finally
        {
            Carried.Value = null;
        }
        await started;
        return await told.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
