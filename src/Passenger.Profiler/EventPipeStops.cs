using Microsoft.Diagnostics.NETCore.Client;

namespace Passenger.Profiler;

/// <summary>The one stop of an EventPipe session: its runtime asked to end it, which flushes what it holds (its rundown
/// too, where asked for) into the session's stream and closes it. A stop the runtime cannot take is passed over, never
/// thrown, so what follows it (the stream read to its end, the session let go) always runs.</summary>
internal static class EventPipeStops
{
    /// <summary>Stops <paramref name="session"/>, given up after <paramref name="limit"/>: true once its runtime took the
    /// stop; false for one gone, unreachable or refusing (<see cref="DiagnosticsFailures.Of"/>), or past the limit.</summary>
    public static async Task<bool> StopAsync(EventPipeSession session, TimeSpan limit)
    {
        // A program gone mid-request would leave a port's endpoint waiting for its runtime for good.
        using CancellationTokenSource timer = new(limit);
        try
        {
            await session.StopAsync(timer.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException || DiagnosticsFailures.Of(error))
        {
            return false;
        }
    }
}
