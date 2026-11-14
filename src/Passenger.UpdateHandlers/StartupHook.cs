using System.Text;
using Passenger.Core;
using Passenger.UpdateHandlers;

/// <summary>Run before the program's Main, as DOTNET_STARTUP_HOOKS names this assembly, only in a program Passenger debugs
/// with Hot Reload: it takes itself and its variable out of what the program's children inherit, connects to Passenger's
/// socket, starts a thread of its own to read it and answer, and returns once that thread has made every call of its own
/// and is about to wait in its first read. A stop between updates then finds the thread in the system's receive, partway
/// through nothing an evaluation there could need; the program's handlers still run for the first time at the first
/// update, as under the runtime's own agent. It never waits on Passenger: connecting waits for nothing of Passenger's, the
/// thread waits only to start, and a socket that cannot be reached leaves the program as it would run without the hook.</summary>
internal static class StartupHook
{
    /// <summary>The longest Main waits for the thread to reach its first read, in milliseconds of the clock, the time a
    /// debugger holds the program counted too: past it the program goes on, the thread reaching its read on its own.</summary>
    private const long StartLimitMilliseconds = 2000;

    /// <summary>The longest line read: each carries one type or ends a batch, and a longer one is passed over.</summary>
    private const int LongestLine = 256;

    /// <summary>Whether the thread has come to its first read, written by it with nothing of .NET's left to run before the
    /// read, so the program's Main goes on with the thread partway through nothing.</summary>
    private static bool reading;

    public static void Initialize()
    {
        string? socket = Environment.GetEnvironmentVariable(Wire.SocketVariable);
        OwnEnvironment.Set(Wire.SocketVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
        if (socket is not { Length: > 0 } || Connection.Open(socket) is not { } connection)
        {
            return;
        }
        Thread listening = new(() => Serve(connection)) { IsBackground = true, Name = Wire.ThreadName };
        listening.UnsafeStart();
        long until = Environment.TickCount64 + StartLimitMilliseconds;
        SpinWait waiting = default;
        while (!Volatile.Read(ref reading) && Environment.TickCount64 < until)
        {
            // Never Sleep(1), whose least on Windows is the clock's tick: the thread is a moment from its read.
            waiting.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Calls the handlers after each update Passenger says was applied, answering what they came to, until the
    /// socket closes; says it is <see cref="reading"/> once <see cref="Ready"/> has made every call it makes but those of
    /// the program's handlers, its next step its first read.</summary>
    private static void Serve(Connection connection)
    {
        using Connection open = connection;
        byte[] updated = Encoding.ASCII.GetBytes(Wire.Updated);
        byte[] buffer = new byte[LongestLine];
        ChangedTypes changes = new();
        Ready(open, buffer, updated);
        Volatile.Write(ref reading, true);
        int kept = 0;
        // Set while a line longer than the buffer is passed over, up to its end.
        bool passing = false;
        while (open.Receive(buffer, kept, wait: true) is > 0 and int read)
        {
            int length = kept + read;
            int start = 0;
            for (int end = kept; end < length; end++)
            {
                if (buffer[end] != '\n')
                {
                    continue;
                }
                if (!passing)
                {
                    if (Says(buffer, start, end - start, updated))
                    {
                        if (!Answer(open, Handlers.Run(changes.Take(AppDomain.CurrentDomain.GetAssemblies()))))
                        {
                            return;
                        }
                    }
                    else
                    {
                        changes.Add(Encoding.ASCII.GetString(buffer, start, end - start));
                    }
                }
                passing = false;
                start = end + 1;
            }
            passing |= length - start == buffer.Length;
            kept = passing ? 0 : length - start;
            Array.Copy(buffer, start, buffer, 0, kept);
        }
    }

    /// <summary>Makes once each of the hook's own calls of reading Passenger's lines and answering them: a receive that waits
    /// for nothing and takes nothing, a line matched, a failure's words encoded, a send of no bytes, an answer of nothing,
    /// and the handlers looked for in this assembly alone, which declares none, what that finds said to no one. Finding,
    /// reading and calling the program's own handlers is first done at the first update.</summary>
    private static void Ready(Connection open, byte[] buffer, byte[] updated)
    {
        open.Receive(buffer, 0, wait: false);
        Says(updated, 0, updated.Length, updated);
        Line(Wire.Failed + "a\nb");
        open.Send([]);
        ChangedTypes changes = new();
        changes.Add(Wire.TypeLine(typeof(StartupHook).Module.ModuleVersionId, typeof(StartupHook).MetadataToken));
        Handlers.Run([typeof(StartupHook).Assembly], changes.Take([typeof(StartupHook).Assembly]));
        Answer(open, [], done: false);
        Array.Copy(buffer, 0, buffer, 0, 0);
    }

    /// <summary>Whether the bytes of <paramref name="buffer"/> from <paramref name="start"/> are those of <paramref name="line"/>.</summary>
    private static bool Says(byte[] buffer, int start, int length, byte[] line) =>
        length == line.Length && buffer.AsSpan(start, length).SequenceEqual(line);

    /// <summary>Says each of the handlers' failures, then that they ran unless <paramref name="done"/> is false; false once
    /// Passenger has gone.</summary>
    private static bool Answer(Connection connection, IReadOnlyList<string> failures, bool done = true)
    {
        foreach (string failure in failures)
        {
            if (!connection.Send(Line(Wire.Failed + failure)))
            {
                return false;
            }
        }
        return !done || connection.Send(Line(Wire.Done));
    }

    /// <summary>A line of the wire: its words on one line, in UTF-8.</summary>
    private static byte[] Line(string words) => Encoding.UTF8.GetBytes(words.ReplaceLineEndings(" ") + "\n");
}
