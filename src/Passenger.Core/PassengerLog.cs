using System.Runtime.ExceptionServices;

namespace Passenger.Core;

/// <summary>The IDE's own log, passenger.log, and its list of errors, for code that knows nothing of the app: a line or an
/// error from any thread goes where the app says as it starts (<see cref="Install"/>), a line written before then (the
/// login shell's environment, read as the process starts) once it does. A line goes nowhere in a process that says
/// nothing; an error is thrown again there, so none is lost.</summary>
public static class PassengerLog
{
    /// <summary>The most lines kept for the app's log before it is installed.</summary>
    private const int Early = 100;

    private static volatile Action<string>? writer;
    private static volatile Action<Exception>? reporter;

    /// <summary>The lines written before the log was installed, under their own lock.</summary>
    private static readonly Queue<string> early = new();

    /// <summary>Gives <see cref="Write"/> the app's log and <see cref="Report"/> its errors, which only the app knows,
    /// writing to it first the lines that came before it.</summary>
    public static void Install(Action<string> write, Action<Exception> report)
    {
        lock (early)
        {
            while (early.TryDequeue(out string? line))
            {
                write(line);
            }
            writer = write;
            reporter = report;
        }
    }

    public static void Write(string line)
    {
        if (writer is { } write)
        {
            write(line);
            return;
        }
        lock (early)
        {
            if (writer is { } installed)
            {
                installed(line);
            }
            else if (early.Count < Early)
            {
                early.Enqueue(line);
            }
        }
    }

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
