using Avalonia.Logging;
using Avalonia.Threading;

namespace Passenger.App;

/// <summary>The IDE's internal errors: an exception that escapes on the UI thread or from a task nobody awaited is
/// written to the log in the settings folder (<c>logs/passenger.log</c>, through <see cref="IdeLog"/>) and shown in the
/// status bar, and the IDE keeps running. Repeats of one error are one entry with its count, and the list holds a
/// hundred. Avalonia's warnings and errors, and the programs the IDE runs that fail, go to the log. The process's: an
/// error caught on the one UI thread or a task is no one window's, so every window counts the same list, and the window
/// in front says it.</summary>
public static class IdeErrors
{
    /// <summary>The different errors listed at most: past it, they are only in the log.</summary>
    public const int Limit = 100;

    /// <summary>The rotated logs kept beside the current one, <c>passenger.1.log</c> the newest.</summary>
    public const int Rotations = IdeLog.Rotations;

    private static readonly object Gate = new();
    private static readonly List<IdeError> Reported = [];

    /// <summary>Each listed error's place in <see cref="Reported"/>, by the whole exception's text.</summary>
    private static readonly Dictionary<string, int> Places = new(StringComparer.Ordinal);
    private static int unlisted;
    private static bool installed;

    /// <summary>The exceptions reported already: one the dispatcher caught also faults its operation's task, which nobody
    /// observes, so it comes round again as an unobserved task's.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, object> Seen = [];
    private static Dispatcher? hooked;

    /// <summary>An error was reported, a repeat with its count so far; raised on the UI thread.</summary>
    public static event EventHandler<IdeError>? Raised;

    /// <summary>The folder Help, Show Log opens: the state store's <c>logs</c>.</summary>
    public static string LogFolder => IdeLog.Folder;

    public static string LogPath => IdeLog.PathOf(IdeLog.Ide);

    /// <summary>The size past which a log is rotated: no log file grows beyond it.</summary>
    public static long LogLimit
    {
        get => IdeLog.Limit;
        set => IdeLog.Limit = value;
    }

    /// <summary>The errors reported since the last Clear, each once with how many times it came, the oldest first.</summary>
    public static IReadOnlyList<IdeError> Errors
    {
        get
        {
            lock (Gate)
            {
                return [.. Reported];
            }
        }
    }

    /// <summary>The errors since the last Clear that came once the list was full: they are only in the log.</summary>
    public static int Unlisted
    {
        get
        {
            lock (Gate)
            {
                return unlisted;
            }
        }
    }

    /// <summary>Catches what would end the IDE: on the UI thread's dispatcher (each window makes sure of it, since a
    /// headless session can bring its own), and once per process for tasks nobody awaited and Avalonia's log.</summary>
    public static void Install()
    {
        if (hooked != Dispatcher.UIThread)
        {
            hooked = Dispatcher.UIThread;
            hooked.UnhandledException += (_, e) =>
            {
                Report(e.Exception);
                e.Handled = true;
            };
        }
        if (installed)
        {
            return;
        }
        installed = true;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(Single(e.Exception));
            e.SetObserved();
        };
        // Past saving: an exception off the UI thread still ends the process, but leaves its reason in the log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write($"Fatal: {e.ExceptionObject}");
            Flush();
        };
        Logger.Sink = new Sink();
        Core.PassengerLog.Install(Write, Report);
        Core.UnawaitedWork.Install(Report);
        // No program's secret reaches the log or a console's echo of its command line, whoever started it.
        Core.ChildProcesses.Shown = Containers.ContainerSecrets.CommandLine;
        // Every program at debug level; otherwise those worth a person's look.
        Core.ChildProcesses.Started += (_, start) =>
        {
            if (IdeLog.Programs)
            {
                Write(start.ToString());
            }
        };
        Core.ChildProcesses.Ended += (_, end) =>
        {
            if (end.Notable || IdeLog.Programs)
            {
                Write(end.ToString());
            }
        };
    }

    /// <summary>Logs an error and shows it, from any thread. A repeat counts against the error listed already and is
    /// one line in the log, its details being there from the first time.</summary>
    public static void Report(Exception error)
    {
        string details = error.ToString();
        IdeError reported;
        lock (Gate)
        {
            if (!Seen.TryAdd(error, Gate))
            {
                return;
            }
            DateTime now = DateTime.Now;
            if (Places.TryGetValue(details, out int place))
            {
                reported = Reported[place] with { Count = Reported[place].Count + 1, Last = now };
                Reported[place] = reported;
            }
            else
            {
                reported = new IdeError(now, error.GetType().Name, error.Message, details);
                if (Reported.Count < Limit)
                {
                    Places[details] = Reported.Count;
                    Reported.Add(reported);
                }
                else
                {
                    unlisted++;
                }
            }
        }
        Write(reported.Count == 1 ? $"Error: {details}" : $"Error, occurred {reported.Count} times: {reported.Kind}: {reported.Message}");
        Dispatcher.UIThread.Post(() => Raised?.Invoke(null, reported));
    }

    /// <summary>Work started that nothing awaits (a handler's, a menu item's, a callback's, a method's that returns before
    /// it ends): its fault is reported as it faults, as an exception escaping a handler is, never only once the task is
    /// collected. A cancelled task is no fault.</summary>
    public static void Observe(Task work) =>
        _ = work.ContinueWith(faulted => Report(Single(faulted.Exception!)), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>A task's one exception as itself; several as the AggregateException holding them.</summary>
    private static Exception Single(AggregateException error) => error.InnerExceptions.Count == 1 ? error.InnerException! : error;

    /// <summary>Forgets the errors shown (Clear All); the log keeps them.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Reported.Clear();
            Places.Clear();
            unlisted = 0;
        }
    }

    /// <summary>A line in the log, for what is worth finding later (a server restarted) but not an error. It is queued
    /// from any thread and written by the log's own, so the UI thread never waits on the disk.</summary>
    public static void Write(string line) => IdeLog.Write(IdeLog.Ide, line);

    /// <summary>Writes every line queued, now, on the calling thread: before the process ends, and before the log is read.</summary>
    public static void Flush() => IdeLog.Flush();

    /// <summary>Avalonia's warnings and errors, in the IDE's log rather than a trace nobody reads.</summary>
    private sealed class Sink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Write($"Avalonia {level} [{area}] {messageTemplate}");

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Write($"Avalonia {level} [{area}] {messageTemplate} {string.Join(", ", propertyValues)}");
    }
}

/// <summary>One error the IDE caught: when it first came, its kind and message, the whole exception for its details,
/// and how many times it came since the list was cleared, the last when.</summary>
public sealed record IdeError(DateTime At, string Kind, string Message, string Details)
{
    public int Count { get; init; } = 1;

    public DateTime Last { get; init; } = At;
}
