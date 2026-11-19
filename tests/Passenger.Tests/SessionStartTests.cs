using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Profiler;

namespace Passenger.Tests;

/// <summary>A session Passenger starts to hear a program from its first moment is on once its start returns, though the
/// runtime may answer the start before it turns the session's providers on: what the program does from then is heard. The
/// program here is the test's own process, whose provider of its own tells whether the session turned it on yet.</summary>
[Collection(Alone.Name)]
public sealed class SessionStartTests
{
    [Fact]
    public async Task A_session_started_to_hear_a_program_from_its_first_moment_has_its_providers_on_once_its_start_returns()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using SessionMark mark = new();
        EventPipeProvider[] marked = [new EventPipeProvider(SessionMark.Provider, EventLevel.Informational)];
        // Ten starts, as a runtime that answers first is still turning the session on at most answers, not at all.
        for (int round = 0; round < 10; round++)
        {
            using EventPipeSession session = await SessionStarts.OnAsync(new DiagnosticsClient(Environment.ProcessId), marked, circularBufferMB: 1, cancel);
            bool on = mark.IsEnabled();
            Task reading = Waiting.Apart(() => session.EventStream.CopyTo(Stream.Null), cancel);
            await session.StopAsync(cancel);
            Assert.True(await Waiting.Within(reading, TimeSpan.FromSeconds(30), cancel), "The session's stream never ended after its stop");
            Assert.True(on, $"In round {round + 1} of 10, the session's provider was not on as its start returned: what the program did then went unheard");
        }
    }

    /// <summary>A provider of the test's own, which writes nothing: its being on says its session turned it on.</summary>
    [EventSource(Name = Provider)]
    private sealed class SessionMark : EventSource
    {
        public const string Provider = "Passenger-Tests-SessionMark";
    }
}
