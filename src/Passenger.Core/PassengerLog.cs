using System.Runtime.ExceptionServices;

namespace Passenger.Core;

/// <summary>The IDE's own log, passenger.log, and its list of errors, for code that knows nothing of the app: a line or an
/// error from any thread goes where the app says as it starts (<see cref="Install"/>), a line written before then (the
/// login shell's environment, read as the process starts) once it does, and what a program said that a line quotes
/// masked by the app's mask for secrets. A line goes nowhere in a process that says nothing; an error is thrown again
/// there, so none is lost.</summary>
public static class PassengerLog
{
    /// <summary>The most lines kept for the app's log before it is installed.</summary>
    private const int Early = 100;

    /// <summary>The app's log and its mask for secrets, once installed.</summary>
    private sealed record Installed(Action<string> Write, Func<string, string> Mask);

    private static volatile Installed? installed;
    private static volatile Action<Exception>? reporter;

    /// <summary>The lines written before the log was installed, each made given the mask, under their own lock.</summary>
    private static readonly Queue<Func<Func<string, string>, string>> early = new();

    /// <summary>Gives <see cref="Write"/> the app's log, <see cref="Report"/> its errors and <see cref="WriteMasked"/> its
    /// mask for secrets, which only the app knows, writing to it first the lines that came before it.</summary>
    public static void Install(Action<string> write, Action<Exception> report, Func<string, string> mask)
    {
        lock (early)
        {
            while (early.TryDequeue(out Func<Func<string, string>, string>? line))
            {
                write(line(mask));
            }
            installed = new Installed(write, mask);
            reporter = report;
        }
    }

    public static void Write(string line)
    {
        if (installed is { } log)
        {
            log.Write(line);
            return;
        }
        Later(line);
    }

    /// <summary>A line written before the log is installed, kept apart so a line written after it costs nothing more.</summary>
    private static void Later(string line) => Put(_ => line);

    /// <summary>A line quoting what a program said (its error output, which may echo a secret), made given the app's mask
    /// for secrets to mask those words with, however early it comes.</summary>
    public static void WriteMasked(Func<Func<string, string>, string> line)
    {
        if (installed is { } log)
        {
            log.Write(line(log.Mask));
            return;
        }
        Put(line);
    }

    private static void Put(Func<Func<string, string>, string> line)
    {
        lock (early)
        {
            if (installed is { } log)
            {
                log.Write(line(log.Mask));
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
