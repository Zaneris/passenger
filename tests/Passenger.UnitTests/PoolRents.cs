using System.Diagnostics.Tracing;

namespace Passenger.UnitTests;

/// <summary>The shared pool's rents of <paramref name="least"/> bytes or more made while <see cref="Counting"/> is set, on
/// whatever thread the work counted moves to, as the pool's own event source tells them.</summary>
internal sealed class PoolRents(int least = 0) : EventListener
{
    private static readonly AsyncLocal<bool> counting = new();
    private int count;

    /// <summary>Whether this flow of work, and the work it starts while set, is counted.</summary>
    public static bool Counting
    {
        get => counting.Value;
        set => counting.Value = value;
    }

    public int Count => Volatile.Read(ref count);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
        {
            EnableEvents(eventSource, EventLevel.Verbose);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (counting.Value && eventData.EventName == "BufferRented" && eventData.Payload is [_, int size, ..] && size >= least)
        {
            Interlocked.Increment(ref count);
        }
    }
}
