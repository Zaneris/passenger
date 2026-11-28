using System.IO;
using System.Text.Json.Serialization.Metadata;

namespace Passenger.Core;

/// <summary>A previewer's requests (<see cref="PreviewWire"/>), read on a thread of their own until Passenger closes the
/// previewer's input, the newest waiting for the drawing to take it: one read while another is drawn replaces any still
/// waiting, so only the newest text is drawn.</summary>
public sealed class PreviewRequests<T> where T : class
{
    private readonly object gate = new();
    private T? waiting;
    private bool ended;

    /// <summary>Reads requests until the input ends, each read then told to <paramref name="arrived"/>.</summary>
    public void Read(TextReader input, Action? arrived = null)
    {
        JsonTypeInfo<T> type = PeerJson.Of<T>();
        try
        {
            while (JsonLines.Read(input, type, static (_, error) => Console.Error.WriteLine($"A request that does not read: {error.Message}")) is { } request)
            {
                lock (gate)
                {
                    waiting = request;
                    Monitor.Pulse(gate);
                }
                arrived?.Invoke();
            }
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"Passenger's requests ended: {error.Message}");
        }
        lock (gate)
        {
            ended = true;
            Monitor.Pulse(gate);
        }
    }

    /// <summary>Whether a request waits to be drawn.</summary>
    public bool Waiting
    {
        get
        {
            lock (gate)
            {
                return waiting is not null;
            }
        }
    }

    /// <summary>The newest request not yet drawn, or null for none.</summary>
    public T? Take()
    {
        lock (gate)
        {
            T? next = waiting;
            waiting = null;
            return next;
        }
    }

    /// <summary>The newest request not yet drawn, waited for; null once the requests have ended.</summary>
    public T? Next()
    {
        lock (gate)
        {
            while (waiting is null && !ended)
            {
                Monitor.Wait(gate);
            }
            T? next = waiting;
            waiting = null;
            return next;
        }
    }
}
