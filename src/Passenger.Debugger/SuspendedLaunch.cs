using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A program started suspended, held before its first instruction until <see cref="Resume"/> with no
/// environment variable to hold it, so a .NET program it starts in turn (a build, a language server) runs as it would
/// undebugged. On macOS and Linux dbgshim starts it (<c>CreateProcessForLaunch</c>), handing it the adapter's own
/// descriptors 0, 1 and 2 (<see cref="DetachProtocol"/>); on Windows, where dbgshim's start inherits no handles and so
/// no pipes of ours, .NET's own suspended start does.</summary>
public sealed partial class SuspendedLaunch : IDisposable
{
    private static readonly Lock Swap = new();
    private nint resumeHandle;

    /// <summary>The program on Windows, through which it is let go, waited for and killed.</summary>
    private readonly Process? process;
    private bool resumed;

    /// <summary>On macOS, a kqueue told of the program's exit with its status, which the system keeps for it whoever reaps
    /// it: the runtime's platform layer in the debugger's own libraries (dbgshim's, and the DAC's that mscordbi waits on
    /// the program through) reaps it with waitpid as it sees it end, so a waitpid of ours that comes after (a busy machine
    /// runs its thread late) finds no child, and the exit code is lost. -1 where there is none.</summary>
    private int exitQueue = -1;

    /// <summary>On Linux, a pidfd of the program, through which a kernel from 6.15 tells a reaped program's exit status to
    /// any who held one before it was reaped (<c>PIDFD_INFO_EXIT</c>); -1 where there is none.</summary>
    private int exitHandle = -1;

    private SuspendedLaunch(int processId, nint resumeHandle, Process? process, Stream input, Stream output, Stream error)
    {
        ProcessId = processId;
        this.resumeHandle = resumeHandle;
        this.process = process;
        Input = input;
        Output = output;
        Error = error;
    }

    public int ProcessId { get; }

    /// <summary>The thread the program was started on, which runs its Main, where the system tells it (Windows).</summary>
    public int? InitialThreadId { get; private init; }

    /// <summary>The program's standard input, written by the client's console.</summary>
    public Stream Input { get; }

    public Stream Output { get; }
    public Stream Error { get; }

    /// <summary>Why the program's exit code is not known, once it has ended without one, where it was this process's to
    /// learn; null otherwise.</summary>
    public string? CodeLost { get; private set; }

    /// <summary>Moves the adapter's protocol off its standard input and output onto descriptors of its own,
    /// closed on exec so no program ever inherits them, and points 0 at /dev/null and 1 at standard error,
    /// so a stray write to the console can never corrupt the protocol either. On Windows the protocol stays on
    /// the standard handles, made ones a program started does not inherit: held by the program, they would keep
    /// the client from seeing the adapter's end until the program's.</summary>
    public static (Stream Input, Stream Output) DetachProtocol()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (int standard in (int[])[StandardInput, StandardOutput, StandardError])
            {
                _ = SetHandleInformation(GetStdHandle(standard), HandleFlagInherit, 0);
            }
            return (Console.OpenStandardInput(), Console.OpenStandardOutput());
        }
        SafeFileHandle input = new(Check(Dup(0), "dup"), ownsHandle: true);
        SafeFileHandle output = new(Check(Dup(1), "dup"), ownsHandle: true);
        using SafeFileHandle nothing = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read);
        Check(Libc.DuplicateTo((int)nothing.DangerousGetHandle(), 0), "dup2");
        Check(Libc.DuplicateTo(2, 1), "dup2");
        return (new FileStream(input, FileAccess.Read, 1), new FileStream(output, FileAccess.Write, 1));
    }

    /// <summary>Starts a program suspended, in a folder, with an environment (the adapter's, with these
    /// variables over it) and its standard streams on pipes.</summary>
    public static SuspendedLaunch Start(string program, IReadOnlyList<string> arguments, string? folder, IReadOnlyDictionary<string, string> variables)
    {
        if (OperatingSystem.IsWindows())
        {
            return StartOnWindows(program, arguments, folder, variables);
        }
        Dictionary<string, string> environment = [];
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }
        foreach ((string key, string value) in variables)
        {
            environment[key] = value;
        }
        // dbgshim hands the block to the platform layer as narrow characters, whatever the command line is.
        StringBuilder block = new();
        foreach ((string key, string value) in environment)
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }
        block.Append('\0');
        byte[] encoded = Encoding.UTF8.GetBytes(block.ToString());

        (int childIn, int parentIn) = Pipe();
        (int parentOut, int childOut) = Pipe();
        (int parentErr, int childErr) = Pipe();
        nint environmentBlock = Marshal.AllocHGlobal(encoded.Length);
        Marshal.Copy(encoded, 0, environmentBlock, encoded.Length);
        try
        {
            int processId;
            nint resume;
            lock (Swap)
            {
                // The program takes descriptors 0, 1 and 2 as they are when dbgshim forks it.
                int savedIn = Check(Dup(0), "dup");
                int savedOut = Check(Dup(1), "dup");
                int savedErr = Check(Dup(2), "dup");
                try
                {
                    Check(Libc.DuplicateTo(childIn, 0), "dup2");
                    Check(Libc.DuplicateTo(childOut, 1), "dup2");
                    Check(Libc.DuplicateTo(childErr, 2), "dup2");
                    // dbgshim splits the command line back by Windows' rules on every system.
                    int hr = DbgShim.CreateProcessForLaunch(WindowsCommandLine.Of(arguments.Prepend(program)), true, environmentBlock, folder, out uint pid, out resume);
                    Marshal.ThrowExceptionForHR(hr);
                    processId = checked((int)pid);
                }
                finally
                {
                    Libc.DuplicateTo(savedIn, 0);
                    Libc.DuplicateTo(savedOut, 1);
                    Libc.DuplicateTo(savedErr, 2);
                    Close(savedIn);
                    Close(savedOut);
                    Close(savedErr);
                }
            }
            return new SuspendedLaunch(processId, resume, null, Stream(parentIn, FileAccess.Write), Stream(parentOut, FileAccess.Read), Stream(parentErr, FileAccess.Read))
            {
                // Watched while it is held before its first instruction, so its exit cannot come first.
                exitQueue = OperatingSystem.IsMacOS() ? WatchExit(processId) : -1,
                exitHandle = OperatingSystem.IsLinux() ? OpenProcess(processId) : -1,
            };
        }
        catch
        {
            Close(parentIn);
            Close(parentOut);
            Close(parentErr);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(environmentBlock);
            Close(childIn);
            Close(childOut);
            Close(childErr);
        }
    }

    /// <summary>On Windows the program shares the adapter's own console where it has one, set first to read and write UTF-8
    /// as the adapter does: a .NET program reads its redirected input and writes its output in its console's code page,
    /// which a console made for it alone takes from the system (437 in English).</summary>
    [SupportedOSPlatform("windows")]
    private static SuspendedLaunch StartOnWindows(string program, IReadOnlyList<string> arguments, string? folder, IReadOnlyDictionary<string, string> variables)
    {
        bool shared = SetConsoleOutputCP(Utf8CodePage) && SetConsoleCP(Utf8CodePage);
        ProcessStartInfo start = new(program, arguments)
        {
            WorkingDirectory = folder ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = !shared,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StartSuspended = true,
        };
        foreach ((string key, string value) in variables)
        {
            start.Environment[key] = value;
        }
        Process held = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start");
        return new SuspendedLaunch(held.Id, 0, held, held.StandardInput.BaseStream, held.StandardOutput.BaseStream, held.StandardError.BaseStream)
        {
            InitialThreadId = OnlyThread(held),
        };
    }

    /// <summary>The one thread a process started suspended has, which its Main will run on; null when the system will not
    /// say (the process gone already).</summary>
    [SupportedOSPlatform("windows")]
    private static int? OnlyThread(Process held)
    {
        try
        {
            return held.Threads.Cast<ProcessThread>().MinBy(thread => thread.StartTime)?.Id;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Lets the program run its first instruction.</summary>
    public void Resume()
    {
        if (process is not null)
        {
            if (!resumed && OperatingSystem.IsWindows())
            {
                process.SafeHandle.Resume();
                resumed = true;
            }
            return;
        }
        if (resumeHandle != 0)
        {
            Marshal.ThrowExceptionForHR(DbgShim.ResumeProcess(resumeHandle));
            DbgShim.CloseResumeHandle(resumeHandle);
            resumeHandle = 0;
        }
    }

    /// <summary>Waits for the program to end and says how: its exit code, or 128 and the signal that ended it, as a shell
    /// does. Null when the code is lost (<see cref="CodeLost"/>), or the program was let go of first.</summary>
    public int? WaitForExit()
    {
        if (process is not null)
        {
            try
            {
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or Win32Exception)
            {
                return null;
            }
        }
        if (OperatingSystem.IsMacOS() && exitQueue >= 0 && ExitStatus() is int told)
        {
            // Its status read, it is reaped unless the platform layer has reaped it already.
            while (WaitPid(ProcessId, out _, NoHang) == -1 && Marshal.GetLastPInvokeError() == Interrupted)
            {
            }
            return Decoded(told);
        }
        int status;
        int result;
        do
        {
            result = WaitPid(ProcessId, out status, 0);
        }
        while (result == -1 && Marshal.GetLastPInvokeError() == Interrupted);
        if (result == ProcessId)
        {
            CloseExitHandle();
            return Decoded(status);
        }
        if (OperatingSystem.IsLinux() && ReapedStatus() is int kept)
        {
            return Decoded(kept);
        }
        CodeLost = OperatingSystem.IsLinux()
            ? "the debugger's own libraries reaped it first, and Linux before 6.15 keeps a reaped program's status for no one else"
            : "the debugger's own libraries reaped it first";
        return null;
    }

    /// <summary>An exit code as a shell tells it from a wait's status: the code, or 128 and the signal that ended it.</summary>
    private static int Decoded(int status)
    {
        int signal = status & 0x7f;
        return signal == 0 ? (status >> 8) & 0xff : 128 + signal;
    }

    /// <summary>A kqueue watching a process for its exit and status, or -1 when the system would not watch it.</summary>
    [SupportedOSPlatform("macos")]
    private static int WatchExit(int processId)
    {
        int queue = KQueue();
        if (queue < 0)
        {
            return -1;
        }
        KernelEvent watch = new() { Ident = (nuint)processId, Filter = ProcessFilter, Flags = EventAdd | EventOneShot, FilterFlags = NoteExit | NoteExitStatus };
        if (KEvent(queue, ref watch, 1, out _, 0, 0) < 0)
        {
            Close(queue);
            return -1;
        }
        return queue;
    }

    /// <summary>Waits for the watched program's exit: the status the system kept for it, or null when the watch failed.</summary>
    [SupportedOSPlatform("macos")]
    private int? ExitStatus()
    {
        KernelEvent none = default;
        try
        {
            while (true)
            {
                int told = KEvent(exitQueue, ref none, 0, out KernelEvent exit, 1, 0);
                if (told == -1 && Marshal.GetLastPInvokeError() == Interrupted)
                {
                    continue;
                }
                return told == 1 && (exit.Flags & EventError) == 0 && (exit.FilterFlags & NoteExit) != 0 ? (int)exit.Data : null;
            }
        }
        finally
        {
            Close(exitQueue);
            exitQueue = -1;
        }
    }

    /// <summary>A pidfd of the process, or -1 when the system will not open one (Linux before 5.3).</summary>
    [SupportedOSPlatform("linux")]
    private static int OpenProcess(int processId) => (int)SystemCall(PidfdOpen, processId, 0);

    /// <summary>The exit status the kernel kept for the program another reaped, through its pidfd; null where it kept none.
    /// Its reaper takes it before releasing it, so a pidfd that still finds it there is asked again a moment later.</summary>
    [SupportedOSPlatform("linux")]
    private int? ReapedStatus()
    {
        if (exitHandle < 0)
        {
            return null;
        }
        try
        {
            long began = Stopwatch.GetTimestamp();
            while (true)
            {
                ProcessInfo info = new() { Mask = InfoExit };
                if (ProcessInfoOf(exitHandle, GetProcessInfo, ref info) < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Interrupted)
                    {
                        continue;
                    }
                    return null;
                }
                if ((info.Mask & InfoExit) != 0)
                {
                    return info.ExitCode;
                }
                if (Stopwatch.GetElapsedTime(began) >= TimeSpan.FromSeconds(1))
                {
                    return null;
                }
                Thread.Sleep(1);
            }
        }
        finally
        {
            CloseExitHandle();
        }
    }

    private void CloseExitHandle()
    {
        if (exitHandle >= 0)
        {
            Close(exitHandle);
            exitHandle = -1;
        }
    }

    public void Kill()
    {
        if (process is null)
        {
            _ = Libc.Signal(ProcessId, SigKill);
            return;
        }
        try
        {
            process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // It had ended.
        }
    }

    public void Dispose()
    {
        process?.Dispose();
        if (resumeHandle != 0)
        {
            DbgShim.CloseResumeHandle(resumeHandle);
            resumeHandle = 0;
        }
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
    }

    private static (int Read, int Write) Pipe()
    {
        Span<int> ends = stackalloc int[2];
        Check(SystemNativePipe(ref MemoryMarshal.GetReference(ends), CloseOnExec), "pipe");
        return (ends[0], ends[1]);
    }

    private static FileStream Stream(int descriptor, FileAccess access) => new(new SafeFileHandle(descriptor, ownsHandle: true), access, 1);

    private static int Check(int result, string call) =>
        result >= 0 ? result : throw new IOException($"{call} failed: errno {Marshal.GetLastPInvokeError()}");

    private static int Dup(int descriptor) => checked((int)SystemNativeDup(descriptor));

    // The runtime's own shim, whose descriptors are opened close-on-exec (fcntl is variadic, and a variadic
    // call is not one P/Invoke can make on Apple silicon).
    private const int CloseOnExec = 0x0010;
    private const int SigKill = 9;

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Dup", SetLastError = true)]
    private static partial nint SystemNativeDup(nint descriptor);

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Pipe", SetLastError = true)]
    private static partial int SystemNativePipe(ref int ends, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);

    private const int Interrupted = 4; // EINTR
    private const int NoHang = 1; // WNOHANG

    /// <summary>macOS's struct kevent.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KernelEvent
    {
        public nuint Ident;
        public short Filter;
        public ushort Flags;
        public uint FilterFlags;
        public nint Data;
        public nint UserData;
    }

    private const short ProcessFilter = -5; // EVFILT_PROC
    private const ushort EventAdd = 0x0001; // EV_ADD
    private const ushort EventOneShot = 0x0010; // EV_ONESHOT
    private const ushort EventError = 0x4000; // EV_ERROR
    private const uint NoteExit = 0x80000000; // NOTE_EXIT
    private const uint NoteExitStatus = 0x04000000; // NOTE_EXITSTATUS: the exit's status in Data, for a child

    [LibraryImport("libc", EntryPoint = "kqueue", SetLastError = true)]
    private static partial int KQueue();

    /// <summary>kevent with one change and one event at most, and no timeout (0: wait for good).</summary>
    [LibraryImport("libc", EntryPoint = "kevent", SetLastError = true)]
    private static partial int KEvent(int queue, ref KernelEvent change, int changes, out KernelEvent told, int events, nint timeout);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static partial int WaitPid(int processId, out int status, int options);

    private const long PidfdOpen = 434; // pidfd_open, numbered alike on every architecture
    private const ulong InfoExit = 1UL << 3; // PIDFD_INFO_EXIT
    private const ulong GetProcessInfo = 0xC040FF0B; // PIDFD_GET_INFO: _IOWR(0xFF, 11, the 64 bytes of struct pidfd_info)

    /// <summary>Linux's struct pidfd_info as first published, which every kernel that answers takes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ProcessInfo
    {
        [FieldOffset(0)] public ulong Mask;
        [FieldOffset(60)] public int ExitCode;
    }

    /// <summary>syscall with two arguments, which it takes in registers as a call of fixed arguments does (x64, arm64).</summary>
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long SystemCall(long number, long first, long second);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ProcessInfoOf(int descriptor, ulong request, ref ProcessInfo info);

    private const int StandardInput = -10;
    private const int StandardOutput = -11;
    private const int StandardError = -12;
    private const uint HandleFlagInherit = 1;

    [LibraryImport("kernel32", EntryPoint = "GetStdHandle", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    private const uint Utf8CodePage = 65001; // CP_UTF8

    /// <summary>Sets the code page the console this process is attached to writes in; false where it has no console.</summary>
    [LibraryImport("kernel32", EntryPoint = "SetConsoleOutputCP", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleOutputCP(uint codePage);

    /// <summary>Sets the code page the console this process is attached to reads in; false where it has no console.</summary>
    [LibraryImport("kernel32", EntryPoint = "SetConsoleCP", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCP(uint codePage);
}
