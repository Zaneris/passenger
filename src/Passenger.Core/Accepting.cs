using System.Net.Sockets;

namespace Passenger.Core;

/// <summary>The one way a server of the IDE's takes its connections: one after another, each handed on to be served apart,
/// until the server stops or its listener is closed. A connection its client reset or aborted before it was taken (Winsock's
/// accept says so of one gone from its queue) is passed over and the next taken; any other failure of the listener closes
/// it, said in the IDE's log, so a client coming after is refused at once, never left waiting on a listener that nobody
/// takes from.</summary>
public static class Accepting
{
    /// <summary>Takes each connection <paramref name="accept"/> gives and hands it to <paramref name="serve"/>, which must
    /// not wait on it, until <paramref name="stop"/>; <paramref name="close"/> closes the listener, and
    /// <paramref name="server"/> names it in the log.</summary>
    public static async Task EachAsync<T>(Func<CancellationToken, ValueTask<T>> accept, Action<T> serve, Action close, string server, CancellationToken stop)
    {
        while (true)
        {
            T client;
            try
            {
                client = await accept(stop).ConfigureAwait(false);
            }
            catch (Exception ended) when (stop.IsCancellationRequested && ended is (OperationCanceledException or SocketException or ObjectDisposedException))
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The listener was closed under the accept: nothing more connects.
                return;
            }
            catch (SocketException failed) when (failed.SocketErrorCode is SocketError.OperationAborted)
            {
                return;
            }
            catch (SocketException failed) when (OfOneConnection(failed.SocketErrorCode))
            {
                continue;
            }
            catch (SocketException failed)
            {
                PassengerLog.Write($"{server} stopped taking connections and was closed: {failed.SocketErrorCode} ({failed.Message})");
                close();
                return;
            }
            serve(client);
        }
    }

    /// <summary>Whether an accept failed for one connection alone, gone before it was taken, the listener as it was.</summary>
    public static bool OfOneConnection(SocketError error) => error is SocketError.ConnectionReset or SocketError.ConnectionAborted;
}
