using Passenger.Core;

namespace Passenger.Text;

/// <summary>The handlers of one of a document's change events, each told of a change apart: one that throws is reported
/// (<see cref="PassengerLog.Report"/>) and those after it still hear of it. Added and removed from any thread; a change
/// is told to those there as it began.</summary>
internal sealed class ChangeHandlers
{
    private readonly Lock gate = new();
    private EventHandler<DocumentChangedEventArgs>[] handlers = [];

    public void Add(EventHandler<DocumentChangedEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }
        lock (gate)
        {
            handlers = [.. handlers, handler];
        }
    }

    /// <summary>Takes away the last of the handler added, as an event's own removal does.</summary>
    public void Remove(EventHandler<DocumentChangedEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }
        lock (gate)
        {
            int at = Array.LastIndexOf(handlers, handler);
            if (at >= 0)
            {
                handlers = [.. handlers.AsSpan(0, at), .. handlers.AsSpan(at + 1)];
            }
        }
    }

    public void Raise(TextDocument document, DocumentChangedEventArgs change)
    {
        foreach (EventHandler<DocumentChangedEventArgs> handler in handlers)
        {
            try
            {
                handler(document, change);
            }
            catch (Exception error)
            {
                PassengerLog.Report(error);
            }
        }
    }
}
