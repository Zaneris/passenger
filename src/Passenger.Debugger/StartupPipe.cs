using System.Text;
using Passenger.Core;

namespace Passenger.Debugger;

/// <summary>On macOS dbgshim and a starting runtime each open a FIFO for writing without blocking, which fails until a
/// reader has it open, and try again only after 500 ms: dbgshim the one it answers a runtime's start on, the runtime the
/// one it tells its start on, which dbgshim reads only once its own open has taken. This opens the first for reading
/// the moment it exists, so dbgshim's first try finds a reader, spinning while the registration runs (a millisecond)
/// without yielding (macOS sets a yielding thread aside for a 10 ms quantum, by which time, under load, it has failed);
/// and the second as the registration ends, before the program is let go, so the runtime's word of its start waits
/// there for dbgshim however late it reads. It never reads either, and closes them once the runtime has answered (its
/// own ends open by then) or never will.</summary>
internal sealed class StartupPipe : IDisposable
{
    /// <summary>The FIFOs' paths, NUL-ended in UTF-8, as open takes them: the one dbgshim answers on, and the one the
    /// runtime tells its start on.</summary>
    private readonly byte[] path;
    private readonly byte[] startPath;
    private readonly Thread watching;
    private volatile bool stopped;
    private int descriptor = -1;
    private int startDescriptor = -1;

    private StartupPipe(string path, string startPath)
    {
        this.path = Encoding.UTF8.GetBytes(path + '\0');
        this.startPath = Encoding.UTF8.GetBytes(startPath + '\0');
        using ManualResetEventSlim running = new();
        watching = new Thread(() =>
        {
            running.Set();
            while (!stopped)
            {
                int opened = Opened(this.path);
                if (opened >= 0)
                {
                    descriptor = opened;
                    return;
                }
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
        // PAL_GetTransportPipeName's: the temporary folder, then clr-debug-pipe-PID-KEY-co (continue) or -st (startup).
        string named = Path.Combine(Path.GetTempPath(), $"clr-debug-pipe-{pid}-{started}-");
        return new StartupPipe(named + "co", named + "st");
    }

    /// <summary>Whether both FIFOs are held, for the log.</summary>
    public bool Held => descriptor >= 0 && startDescriptor >= 0;

    /// <summary>Stops watching once the registration has made the FIFOs, trying the first once more (a reader opened only
    /// now is still one dbgshim's next try finds, as the runtime's own would be), and holds the second.</summary>
    public void StopWatching()
    {
        if (stopped)
        {
            return;
        }
        stopped = true;
        watching.Join();
        startDescriptor = Opened(startPath);
        if (descriptor < 0 && Opened(path) is >= 0 and int opened)
        {
            descriptor = opened;
        }
    }

    public void Dispose()
    {
        StopWatching();
        foreach (int held in (int[])[Interlocked.Exchange(ref descriptor, -1), Interlocked.Exchange(ref startDescriptor, -1)])
        {
            if (held >= 0)
            {
                _ = Libc.Close(held);
            }
        }
    }

    /// <summary>A FIFO opened for reading without waiting for a writer, or -1 while it is not there.</summary>
    private static unsafe int Opened(byte[] fifo)
    {
        fixed (byte* named = fifo)
        {
            return Libc.Open(named, Libc.ReadOnly | Libc.NonBlocking | Libc.ClosedOnExec);
        }
    }
}
