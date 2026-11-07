using System.Net.Sockets;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>How a server of the IDE's takes its connections (<see cref="Accepting"/>): a connection gone before it was taken
/// is passed over and the next one served, a listener that fails is closed so no client waits on it, and a stop ends the
/// taking with the listener left to its owner. Each accept is a step the test gives, in turn.</summary>
public class AcceptingTests
{
    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted)]
    public async Task A_connection_gone_before_it_was_taken_is_passed_over_and_the_question_after_it_is_served(SocketError gone)
    {
        (List<string> served, int closed) = await TakeAsync(() => throw new SocketException((int)gone), () => "Password for 'https://example.invalid':");

        Assert.True(served is ["Password for 'https://example.invalid':"] && closed == 0,
            $"After a connection {gone} before it was taken the server served [{string.Join(", ", served)}] and closed its listener {closed} times, "
            + "so a helper asking next would wait on a listener nobody takes from");
    }

    [Fact]
    public async Task A_listener_that_fails_is_closed_so_a_client_coming_after_is_refused_rather_than_left_waiting()
    {
        (List<string> served, int closed) = await TakeAsync(() => throw new SocketException((int)SocketError.TooManyOpenSockets), () => "never taken");

        Assert.True(served.Count == 0 && closed == 1,
            $"A listener whose accept failed was closed {closed} times and served [{string.Join(", ", served)}], so a client coming after waits on it for good");
    }

    [Fact]
    public async Task A_stop_ends_the_taking_and_leaves_the_listener_to_its_owner()
    {
        (List<string> served, int closed) = await TakeAsync();

        Assert.True(served.Count == 0 && closed == 0, $"A server stopped served [{string.Join(", ", served)}] and closed its listener {closed} times");
    }

    /// <summary>The connections served and the times the listener was closed, each accept taking the next of
    /// <paramref name="steps"/> (a client, or a failure thrown), the server stopped once a client is served or the steps
    /// run out.</summary>
    private static async Task<(List<string> Served, int Closed)> TakeAsync(params Func<string>[] steps)
    {
        using CancellationTokenSource stop = new();
        Queue<Func<string>> left = new(steps);
        List<string> served = [];
        int closed = 0;
        await Accepting.EachAsync(
            _ =>
            {
                if (!left.TryDequeue(out Func<string>? step))
                {
                    stop.Cancel();
                    return ValueTask.FromCanceled<string>(stop.Token);
                }
                return ValueTask.FromResult(step());
            },
            client =>
            {
                served.Add(client);
                left.Clear();
            },
            () => closed++,
            "A test's server",
            stop.Token);
        return (served, closed);
    }
}
