using System.Runtime.ExceptionServices;

namespace Passenger.Core;

/// <summary>The IDE's own log, passenger.log, and its list of errors, for code that knows nothing of the app: a line or an
/// error from any thread goes where the app says as it starts (<see cref="Install"/>). A line goes nowhere in a process
/// that says nothing; an error is thrown again there, so none is lost.</summary>
public static class PassengerLog
{
    private static volatile Action<string>? writer;
    private static volatile Action<Exception>? reporter;

    /// <summary>Gives <see cref="Write"/> the app's log and <see cref="Report"/> its errors, which only the app knows.</summary>
    public static void Install(Action<string> write, Action<Exception> report)
    {
        writer = write;
        reporter = report;
    }

    public static void Write(string line) => writer?.Invoke(line);

    /// <summary>An error caught where it must not stop what comes after it (one handler of a document's change among many),
    /// shown as the IDE's error.</summary>
    public static void Report(Exception error)
    {
        if (reporter is not { } report)
        {
            ExceptionDispatchInfo.Throw(error);
            return;
        }
        report(error);
    }
}
