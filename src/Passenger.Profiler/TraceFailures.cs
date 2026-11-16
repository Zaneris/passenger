using System.Runtime.Serialization;

namespace Passenger.Profiler;

/// <summary>What TraceEvent's reading of a .nettrace throws where the trace is cut short or is none it can read, told from
/// a fault of Passenger's own, which a catch of these leaves to go on to the boundary.</summary>
public static class TraceFailures
{
    /// <summary>A trace TraceEvent cannot read: its EventPipe reader's FormatException (a read past the end of a trace cut
    /// short, a header, block or event it does not know), and its FastSerialization's plain Exception (a read past the end
    /// of what it reads) and SerializationException (a string longer than what is left).</summary>
    public static bool Broken(Exception error) => error is FormatException or SerializationException || error.GetType() == typeof(Exception);

    /// <summary>A trace's read that failed: <see cref="Broken"/>, or its files' IOException or UnauthorizedAccessException
    /// (the trace's, or the index TraceEvent writes of it).</summary>
    public static bool Of(Exception error) => Broken(error) || error is IOException or UnauthorizedAccessException;
}
