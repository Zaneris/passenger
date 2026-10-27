using System.Text;
using Passenger.Dap;
using Passenger.Lsp;

namespace Passenger.App;

/// <summary>The IDE's logs folder, which Help, Show Log opens: <c>passenger.log</c>, each C# language server's own
/// under <c>roslyn/</c>, <c>dap.log</c> for the debugger, <c>vstest.log</c> for the test runner, and with
/// <see cref="Traces"/> on <c>lsp.log</c> and agents' <c>mcp.log</c>. A line is queued from any thread and written by
/// the log's own thread; each file is rotated by size, a dozen kept. The process's: every window writes the same files,
/// a line naming the folder where that matters, each language server a log of its own.</summary>
public static partial class IdeLog
{
    /// <summary>The IDE's own log: errors, what the servers and programs it runs said went wrong, startup times.</summary>
    public const string Ide = "passenger";

    /// <summary>The debugger's: each session, the adapter's own words, and with <see cref="Traces"/> its messages.</summary>
    public const string Debugger = "dap";

    /// <summary>The test runners': what VSTest and Microsoft.Testing.Platform's test hosts warned of or failed with, and
    /// with <see cref="Traces"/> their messages.</summary>
    public const string Tests = "vstest";

    /// <summary>The C# language servers' messages, written only with <see cref="Traces"/> on.</summary>
    public const string LspTrace = "lsp";

    /// <summary>Agents' MCP calls, written only with <see cref="Traces"/> on: each call's start, what it waited on (being let
    /// in, the person's leave, a window to join or start) with how long, and how it ended. Names and times, never what a
    /// call was given.</summary>
    public const string Mcp = "mcp";

    /// <summary>The rotated logs kept beside the current one, <c>passenger.1.log</c> the newest.</summary>
    public const int Rotations = 12;

    /// <summary>Lines waiting for the log's thread; taken under <see cref="Writing"/> so they reach the file in order.</summary>
    private static readonly List<LogLine> Queued = [];
    private static readonly object Writing = new();
    private static Thread? writer;
    private static volatile bool traces;

    /// <summary>The logs that could not be moved aside to rotate (Windows, a reader not sharing deletion), each with the
    /// length it had then: written on, and tried again only once it has grown by a tenth of the limit, not at every write.
    /// Taken under <see cref="Writing"/>.</summary>
    private static readonly Dictionary<string, long> Unmoved = new(StringComparer.Ordinal);

    /// <summary>How long the log's thread rests after a write, so the lines of a burst share one opening of the file.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

    /// <summary>The state store's <c>logs</c>, read each time, since the state folder can change under a running
    /// process.</summary>
    public static string Folder => Path.Combine(State.StateStore.DefaultRoot, "logs");

    /// <summary>The size past which a log is rotated: no log file grows beyond it, but one another program holds where
    /// it is.</summary>
    public static long Limit { get; set; } = 10_000_000;

    /// <summary>Settings' Trace protocol messages: every message to and from the C# language servers (lsp.log), the debug
    /// adapter (dap.log), VSTest and the Testing Platform's test hosts (vstest.log) is written as it goes, as a client's
    /// verbose trace is, and each agent's MCP call (mcp.log).</summary>
    public static bool Traces
    {
        get => traces;
        set
        {
            traces = value;
            LanguageServer.Trace = value ? (server, message) => Write(LspTrace, $"[server {server}] {message}") : null;
            DapConnection.Trace = value ? (adapter, message) => Write(Debugger, $"[{adapter}] {message}") : null;
        }
    }

    /// <summary>Settings' Log every program started: each program the IDE runs, its exit code and time, in
    /// passenger.log. Off, only those that fail or take a second are, since git's quick reads come every few seconds
    /// and would bury the rest.</summary>
    public static bool Programs { get; set; }

    public static string PathOf(string log) => Path.Combine(Folder, $"{log}.log");

    /// <summary>The process's console, its standard output and error, written to passenger.log instead: an agent's MCP
    /// server has standard output for its protocol, and an agent may never read standard error (OpenCode's does not),
    /// whose pipe, once full, would stop whatever thread wrote to it. On macOS and Linux what native code writes to the
    /// standard error itself (fontconfig's warnings, the runtime's own) is read into passenger.log too.</summary>
    public static void TakeConsole()
    {
        TextWriter lines = TextWriter.Synchronized(new ConsoleLines());
        Console.SetOut(lines);
        Console.SetError(lines);
        if (!OperatingSystem.IsWindows())
        {
            TakeStandardError();
        }
    }

    /// <summary>The process's standard error made a pipe of its own, which a thread of the log's reads into passenger.log
    /// for as long as the process runs, so a write there never waits on whoever started it.</summary>
    private static unsafe void TakeStandardError()
    {
        const int StandardError = 2;
        int* ends = stackalloc int[2];
        if (Pipe(ends) != 0)
        {
            return;
        }
        // Kept apart from what the process starts: an agent's relay says why it ends there (GivenError).
        if (KeepEnd(StandardError) is var kept and >= 0 && Control(kept, SetFlags, CloseOnExec) == 0)
        {
            given = kept;
        }
        if (Duplicate(ends[1], StandardError) < 0)
        {
            CloseEnd(ends[0]);
            CloseEnd(ends[1]);
            return;
        }
        CloseEnd(ends[1]);
        FileStream read = new(new Microsoft.Win32.SafeHandles.SafeFileHandle(ends[0], ownsHandle: true), FileAccess.Read, 1);
        Thread reader = new(() =>
        {
            try
            {
                using StreamReader lines = new(read, Encoding.UTF8);
                while (lines.ReadLine() is { } line)
                {
                    Write(Ide, $"Standard error: {line}");
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The process is ending.
            }
        })
        { IsBackground = true, Name = "Passenger standard error" };
        reader.Start();
    }

    /// <summary>The standard error the process was given, before the console took it (<see cref="TakeConsole"/>), or -1.</summary>
    private static int given = -1;

    /// <summary>The standard error the process was given, which its starter reads: where an agent's relay says why it ends.</summary>
    public static Stream GivenError() =>
        given >= 0 ? new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(given, ownsHandle: false), FileAccess.Write, 1) : Console.OpenStandardError();

    private const int SetFlags = 2;

    private const int CloseOnExec = 1;

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int KeepEnd(int end);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Control(int end, int command, int flags);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static unsafe partial int Pipe(int* ends);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static partial int Duplicate(int from, int to);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseEnd(int end);

    /// <summary>A line in one of the logs, from any thread.</summary>
    public static void Write(string log, string line)
    {
        LogLine queued = new(PathOf(log), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        lock (Queued)
        {
            Queued.Add(queued);
            if (writer is null)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
                writer = new Thread(WriteQueued) { IsBackground = true, Name = "Passenger log" };
                writer.Start();
            }
            Monitor.Pulse(Queued);
        }
    }

    /// <summary>Writes every line queued, now, on the calling thread: before the process ends, and before a log is read.</summary>
    public static void Flush()
    {
        lock (Writing)
        {
            List<LogLine> lines;
            lock (Queued)
            {
                if (Queued.Count == 0)
                {
                    return;
                }
                lines = [.. Queued];
                Queued.Clear();
            }
            foreach (IGrouping<string, LogLine> file in lines.GroupBy(l => l.Path, StringComparer.Ordinal))
            {
                Append(file.Key, [.. file]);
            }
        }
    }

    private static void WriteQueued()
    {
        while (true)
        {
            lock (Queued)
            {
                while (Queued.Count == 0)
                {
                    Monitor.Wait(Queued);
                }
            }
            Flush();
            Thread.Sleep(Pause);
        }
    }

    /// <summary>Appends lines to one log, rotating it first whenever the next line would take it past the limit.</summary>
    private static void Append(string path, List<LogLine> lines)
    {
        FileStream? file = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            file = Open(path);
            long length = file.Length;
            foreach (LogLine line in lines)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line.Text);
                if (length > 0 && length + bytes.Length > Limit && !WaitsToRotate(path, length))
                {
                    file.Dispose();
                    // One that cannot be moved is written on rather than lost.
                    if (Rotate(path))
                    {
                        Unmoved.Remove(path);
                    }
                    else
                    {
                        Unmoved[path] = length;
                    }
                    file = Open(path);
                    length = file.Length;
                }
                file.Write(bytes);
                length += bytes.Length;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>Whether a log that could not be moved aside to rotate has grown by less than a tenth of the limit since.</summary>
    private static bool WaitsToRotate(string path, long length) => Unmoved.TryGetValue(path, out long held) && length < held + Limit / 10;

    private static FileStream Open(string path) => new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Rotates a log: the oldest goes, each moves up one, and the log becomes <c>passenger.1.log</c>. The log
    /// is moved aside first, so one that cannot be (Windows, a reader not sharing deletion) leaves the rotated ones as
    /// they were; false then.</summary>
    private static bool Rotate(string path)
    {
        string aside = Rotated(path, "rotating");
        try
        {
            File.Move(path, aside, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        try
        {
            File.Delete(Rotated(path, Rotations));
            for (int n = Rotations - 1; n >= 1; n--)
            {
                if (File.Exists(Rotated(path, n)))
                {
                    File.Move(Rotated(path, n), Rotated(path, n + 1));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A rotated log held where it is: the log still takes the first place.
        }
        try
        {
            File.Move(aside, Rotated(path, 1), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Held there too: the log stays aside until the next rotation's takes its place.
        }
        return true;
    }

    /// <summary>A rotated log's path: <c>passenger.3.log</c> beside <c>passenger.log</c>.</summary>
    private static string Rotated(string path, int number) => Rotated(path, $"{number}");

    private static string Rotated(string path, string part) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{part}{Path.GetExtension(path)}");

    private sealed record LogLine(string Path, string Text);

    /// <summary>What the console is given, a line at a time, as passenger.log's lines.</summary>
    private sealed class ConsoleLines : TextWriter
    {
        private readonly StringBuilder line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();
            }
            else if (value != '\r')
            {
                line.Append(value);
            }
        }

        public override void Flush()
        {
            if (line.Length > 0)
            {
                IdeLog.Write(Ide, $"Console: {line}");
                line.Clear();
            }
        }
    }
}
