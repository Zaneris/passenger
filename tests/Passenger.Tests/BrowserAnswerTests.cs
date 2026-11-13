using Passenger.App.Run;

namespace Passenger.Tests;

/// <summary>A container's page opens once its service answers HTTP, against a stand-in on loopback: a port that takes each
/// request and lets it go unanswered, as an engine does with nothing listening behind its published port, is no answer,
/// and is asked again; any status at all is one; a service that never answers is given up at the limit, and a wait the
/// run ended says neither.</summary>
[Collection(Beside.Name)]
public sealed class BrowserAnswerTests
{
    [Fact]
    public async Task A_port_that_takes_requests_and_lets_them_go_is_no_answer_until_any_status_comes()
    {
        int asked = 0;
        await using StandInHttp service = new(exchange => Interlocked.Increment(ref asked) < 3 ? Task.CompletedTask : exchange.ReplyAsync(404, "text/plain", "none"));
        bool? answered = await BrowserLaunch.AnsweringAsync(new Uri($"{service.Root}/swagger"), TimeSpan.FromMinutes(1).Scaled(), TestContext.Current.CancellationToken);
        Assert.True(answered == true, $"A service whose third request was answered 404 was {(answered is null ? "never waited for" : "given up")}");
        Assert.True(Volatile.Read(ref asked) == 3, $"The service was asked {Volatile.Read(ref asked)} times, where two were let go unanswered and the third answered");
    }

    [Fact]
    public async Task A_service_that_never_answers_is_given_up_at_the_limit_and_a_wait_ended_first_says_neither()
    {
        await using StandInHttp service = new(_ => Task.CompletedTask);
        Uri address = new($"{service.Root}/");
        Assert.True(await BrowserLaunch.AnsweringAsync(address, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken) == false,
            "A service that let every request go unanswered was not given up at the limit");
        using CancellationTokenSource ended = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<bool?> waiting = BrowserLaunch.AnsweringAsync(address, TimeSpan.FromMinutes(5), ended.Token);
        await ended.CancelAsync();
        Assert.True(await waiting is null, "A wait the run ended said whether the service answered");
    }
}
