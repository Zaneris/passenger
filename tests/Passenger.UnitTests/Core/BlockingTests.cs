using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The libraries' synchronous wait on a task (Blocking.Until): it lets its thread go once another thread ends the
/// task, and throws nothing of how a task ended, a task with a result too, so the caller reads the outcome itself.</summary>
public class BlockingTests
{
    [Fact]
    public void A_wait_lets_its_thread_go_once_another_thread_ends_the_task()
    {
        TaskCompletionSource<int> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread answering = new(() => answer.SetResult(1));
        answering.Start();

        Blocking.Until(answer.Task);

        Assert.True(answer.Task.IsCompletedSuccessfully, $"The wait let its thread go with the task {answer.Task.Status}, so its caller reads an answer not yet given");
        answering.Join();
    }

    [Fact]
    public void A_wait_on_a_task_that_failed_or_was_cancelled_throws_nothing()
    {
        Task<int>[] ended = [Task.FromException<int>(new InvalidOperationException("The relay went away")), Task.FromCanceled<int>(new CancellationToken(canceled: true))];

        List<string> thrown = [];
        foreach (Task<int> task in ended)
        {
            try
            {
                Blocking.Until(task);
            }
            catch (Exception caught) when (caught is InvalidOperationException or OperationCanceledException or ArgumentOutOfRangeException)
            {
                thrown.Add($"{task.Status}: {caught.GetType().Name}");
            }
        }

        Assert.True(thrown.Count == 0, $"A wait threw [{string.Join(", ", thrown)}], where its caller reads how the task ended itself, so a fault "
            + "it means to say in its own words escapes it");
    }
}
