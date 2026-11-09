using Microsoft.Diagnostics.Tracing;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A session's stream read to its end, as every reader of one reads it: the program gone or the session stopped
/// ends it, and what that end throws (a pipe broken, a stream disposed or cut short, a block half written) is passed over,
/// what was heard by then being all the stream had. The read holds nothing (<see cref="BackgroundWork.Unheld"/>), as it
/// lasts as long as its session, past any wait for the IDE to be quiet.</summary>
internal static class EventPipeReads
{
    /// <summary>Reads a source made of a session's stream to its end: on a thread of its own for a session that lasts as
    /// long as someone looks (<paramref name="lasting"/>), else on the thread pool.</summary>
    public static Task ToEndAsync(EventPipeEventSource source, bool lasting = false) =>
        lasting ? BackgroundWork.UnheldThread(() => ToEnd(source)) : BackgroundWork.Unheld(() => ToEnd(source), CancellationToken.None);

    /// <summary>Reads a session's stream to its end on the thread pool, its source made there, as making it waits for the
    /// runtime's first words, hooked by <paramref name="follow"/> before it is read and let go once it has been.</summary>
    public static Task ToEndAsync(Stream stream, Action<EventPipeEventSource> follow) => BackgroundWork.Unheld(() =>
    {
        try
        {
            using EventPipeEventSource source = new(stream);
            follow(source);
            source.Process();
        }
        catch (Exception error) when (Ends(error))
        {
            // The stream ended with the program or its session.
        }
    }, CancellationToken.None);

    private static void ToEnd(EventPipeEventSource source)
    {
        try
        {
            source.Process();
        }
        catch (Exception error) when (Ends(error))
        {
            // The stream ended with the program or its session.
        }
    }

    /// <summary>Whether an exception is what a stream's end throws as it is read.</summary>
    private static bool Ends(Exception error) => error is IOException or ObjectDisposedException or EndOfStreamException or FormatException;
}
