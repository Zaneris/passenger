using System.Runtime.InteropServices;

namespace Passenger.Debugger;

/// <summary>On macOS dbgshim opens the FIFO it answers a runtime's start on for writing without blocking, which fails
/// until a reader has it open and is tried again only after 500 ms. This opens that FIFO for reading the moment it
/// exists, so dbgshim's first try finds a reader; it never reads, and closes once the runtime has answered (the
/// runtime's own end open by then) or never will.</summary>
internal sealed partial class StartupPipe : IDisposable
{
    private const int ReadOnly = 0x0000;
    private const int NonBlocking = 0x0004;
    private const int CloseOnExec = 0x01000000;

    private readonly string path;
    private readonly Thread watching;
    private volatile bool stopped;
    private int descriptor = -1;

    private StartupPipe(string path)
    {
        this.path = path;
        using ManualResetEventSlim running = new();
        watching = new Thread(() =>
        {
            running.Set();
            while (!stopped)
            {
                int opened = Open(path, ReadOnly | NonBlocking | CloseOnExec);
                if (opened >= 0)
                {
                    descriptor = opened;
                    return;
                }
                Thread.Yield();
            }
        }) { IsBackground = true, Name = "dbgshim's startup pipe" };
        watching.Start();
        running.Wait();
    }

    /// <summary>Watching for the FIFO dbgshim is about to make for <paramref name="pid"/>'s start, from before the
    /// registration that makes it; null where dbgshim makes none (Windows, Linux) or the process has gone.</summary>
    public static StartupPipe? Watch(int pid)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        if (Core.DotnetProcesses.StartedAt(pid) is not { } startedAt)
        {
            return null;
        }
        // The PAL's disambiguation key: the process's start, in whole seconds since the epoch (sysctl's p_starttime).
        long started = new DateTimeOffset(startedAt).ToUnixTimeSeconds();
        // PAL_GetTransportPipeName's: the temporary folder, then clr-debug-pipe-PID-KEY-co for the continue FIFO.
        return new StartupPipe(Path.Combine(Path.GetTempPath(), $"clr-debug-pipe-{pid}-{started}-co"));
    }

    /// <summary>Whether the FIFO was opened before dbgshim needed it, for the log.</summary>
    public bool Held => descriptor >= 0;

    /// <summary>Stops watching once the registration has made the FIFO, trying it once more: a reader opened only now is
    /// still one dbgshim's next try finds, as the runtime's own would be.</summary>
    public void StopWatching()
    {
        if (stopped)
        {
            return;
        }
        stopped = true;
        watching.Join();
        if (descriptor < 0 && Open(path, ReadOnly | NonBlocking | CloseOnExec) is >= 0 and int opened)
        {
            descriptor = opened;
        }
    }

    public void Dispose()
    {
        StopWatching();
        int held = Interlocked.Exchange(ref descriptor, -1);
        if (held >= 0)
        {
            _ = Core.Libc.Close(held);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
}
