using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Work shared by key (<see cref="SharedWork{TKey, T}"/>): asks at once take one task, an ask that stops waiting
/// leaves it to the rest, and an ended task is taken again only where it stands. Each task ends when the test says.</summary>
public class SharedWorkTests
{
    [Fact]
    public async Task Asks_at_once_take_one_task_and_one_that_stops_waiting_leaves_it_to_the_rest()
    {
        SharedWork<string, string> shared = new(comparer: StringComparer.Ordinal);
        TaskCompletionSource<string> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int begun = 0;
        Func<Task<string>> work = () =>
        {
            Interlocked.Increment(ref begun);
            return answer.Task;
        };

        Task<string> first = shared.Take("bootstrap.min.css", work);
        Task<string> second = shared.Take("bootstrap.min.css", work);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(new CancellationToken(canceled: true)));
        Task<string> third = shared.Take("bootstrap.min.css", work);
        answer.SetResult(".btn{}");

        Assert.Equal(".btn{}", await third);
        Assert.True(ReferenceEquals(first, second) && ReferenceEquals(first, third) && begun == 1,
            $"Three asks at once, the first giving up, began the work {begun} times, so a download would be made once for each");
    }

    [Fact]
    public async Task An_ended_task_is_taken_again_only_where_it_stands_and_one_that_does_not_is_begun_anew()
    {
        SharedWork<string, string> shared = new(task => task.IsCompletedSuccessfully, StringComparer.Ordinal);
        int begun = 0;
        Task<string> Fails() => Interlocked.Increment(ref begun) == 1 ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult("{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => shared.Take("schema.json", Fails));
        Task<string> again = shared.Take("schema.json", Fails);
        Assert.Equal("{}", await again);
        Task<string> kept = shared.Take("schema.json", Fails);

        Assert.True(ReferenceEquals(again, kept) && begun == 2,
            $"After a failed and a good download the work was begun {begun} times, so a failure is never tried again or a schema had is downloaded anew");
    }

    [Fact]
    public async Task Without_stands_a_task_that_ended_is_never_taken_again()
    {
        SharedWork<string, string> shared = new(comparer: StringComparer.Ordinal);
        int begun = 0;
        Task<string> Work() => Task.FromResult($"download {Interlocked.Increment(ref begun)}");

        Assert.Equal("download 1", await shared.Take("bootstrap.min.css", Work));

        Assert.Equal("download 2", await shared.Take("bootstrap.min.css", Work));
    }
}
