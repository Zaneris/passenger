using System.Diagnostics;
using Passenger.App.Endpoints;

namespace Passenger.UnitTests.Endpoints;

/// <summary>Reads kept a moment by key (an app's OpenAPI documents by its address): one asked again within the moment is
/// the same read, and keeping another lets go of every read ended and past it, so what is held never outgrows a moment's
/// reads however many addresses a session asks.</summary>
public sealed class FreshReadsTests
{
    private static readonly TimeSpan Moment = TimeSpan.FromSeconds(10);

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void A_read_asked_again_within_the_moment_is_the_same_read_and_past_it_none()
    {
        FreshReads<string?> reads = new(Moment, StringComparer.OrdinalIgnoreCase);
        Task<string?> first = Task.FromResult<string?>("shop");
        reads.Add("http://localhost:5000", first, At(0));
        Assert.True(reads.TryGet("HTTP://LOCALHOST:5000", At(9), out Task<string?>? again) && again == first,
            "A read asked again within the moment, by the key in other letters, was read anew");
        Assert.False(reads.TryGet("http://localhost:5000", At(10), out _), "A read ended a moment ago was still given, where the app is asked afresh");
    }

    [Fact]
    public void Keeping_a_read_lets_go_of_every_read_ended_past_the_moment_and_keeps_those_under_way()
    {
        FreshReads<string?> reads = new(Moment, StringComparer.OrdinalIgnoreCase);
        TaskCompletionSource<string?> slow = new();
        for (int port = 0; port < 100; port++)
        {
            reads.Add($"http://localhost:{5000 + port}", Task.FromResult<string?>("shop"), At(port * 0.5));
        }
        reads.Add("http://localhost:7000", slow.Task, At(50));
        reads.Add("http://localhost:7001", Task.FromResult<string?>("shop"), At(70));
        Assert.True(reads.Count == 2, $"After 100 addresses read over 50 s, {reads.Count} reads were held where only the moment's last and one still under way stay: "
            + "every app address a session asks would keep its document for good");
        Assert.True(reads.TryGet("http://localhost:7000", At(70), out Task<string?>? waiting) && waiting == slow.Task,
            "A read still under way was let go, so the next ask would read the app again beside it");
    }
}
