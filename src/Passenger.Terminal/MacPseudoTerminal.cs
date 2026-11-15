using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;
using Porta.Pty;

namespace Passenger.Terminal;

/// <summary>A program in a pseudo-terminal of its own on macOS, started held until its output has a reader
/// (<see cref="LetRun"/>), the IDE's copy of the terminal's other end closed before it runs: macOS's last close of that
/// end waits at most 0.6 s for what was written to be read, then throws it away, so a program that wrote and ended before
/// a close made where nothing reads yet would lose its output. posix_spawn makes the program a session's leader but opens
/// its terminal before that, so a shell takes the terminal as the session's controlling one before it becomes the
/// program. The program holds no descriptor but 0, 1 and 2 and starts with every signal's default action and none
/// blocked; its environment is the process's own with the start's set over it, an empty value taking one away, and its
/// end is heard on a thread of its own.</summary>
internal sealed unsafe partial class MacPseudoTerminal : IPseudoTerminal
{
    private readonly int controller;
    private readonly Lock gate = new();
    private readonly ManualResetEventSlim ended = new();

    /// <summary>Whether the program has been reaped, after which its id may be another's: under
    /// <see cref="gate"/>.</summary>
    private bool reaped;
    private int letRun;
    private int disposed;

    private MacPseudoTerminal(int controller, int pid)
    {
        this.controller = controller;
        Pid = pid;
        // Two streams of one descriptor, neither owning it: Dispose closes it once.
        ReaderStream = new FileStream(new SafeFileHandle(controller, ownsHandle: false), FileAccess.Read, bufferSize: 0);
        WriterStream = new FileStream(new SafeFileHandle(controller, ownsHandle: false), FileAccess.Write, bufferSize: 0);
        ApartThreads.Start("Terminal program's end", Watch);
    }

    public event EventHandler<int>? Exited;

    public int Pid { get; }

    public Stream ReaderStream { get; }

    public Stream WriterStream { get; }

    public bool HasExited => ended.IsSet;

    public int ExitCode { get; private set; }

    /// <summary>Starts the program the options name, held until <see cref="LetRun"/>, through a shell that takes the
    /// terminal as its session's controlling one and then becomes the program, found on the PATH it is given as execvp
    /// finds it (<see cref="TerminalSession.Unsetting"/>); throws a <see cref="Win32Exception"/> saying why it could not.</summary>
    public static MacPseudoTerminal Start(PtyOptions options)
    {
        Termios settings = Settings();
        WindowSize size = new((ushort)options.Rows, (ushort)options.Cols);
        byte* name = stackalloc byte[NameLength];
        int controller;
        int other;
        if (OpenTerminal(&controller, &other, name, &settings, &size) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        int pid;
        try
        {
            Libc.CloseOnExec(controller);
            Libc.CloseOnExec(other);
            Dictionary<string, string> given = new(options.Environment, StringComparer.Ordinal);
            List<KeyValuePair<string, string>> inherited = Inherited();
            (string shell, string[] arguments) = TerminalSession.Unsetting([], options.App, options.CommandLine, given,
                TerminalSession.LoaderVariables(given, inherited), Marshal.PtrToStringUTF8((nint)name));
            List<string> environment = [.. inherited.Where(variable => !given.ContainsKey(variable.Key)).Select(variable => $"{variable.Key}={variable.Value}"),
                .. given.Where(variable => variable.Value.Length > 0).Select(variable => $"{variable.Key}={variable.Value}")];
            pid = Spawn(shell, [shell, .. arguments], environment, options.Cwd, name);
        }
        catch
        {
            Libc.Close(controller);
            throw;
        }
        finally
        {
            // The program, held, holds the other end, so this close is never the last one.
            Libc.Close(other);
        }
        return new MacPseudoTerminal(controller, pid);
    }

    public void LetRun()
    {
        if (Interlocked.Exchange(ref letRun, 1) == 0)
        {
            Signal(SignalContinue);
        }
    }

    /// <summary>Hangs the program up, and kills it where that has not ended it a moment later.</summary>
    public void Kill()
    {
        Signal(SignalHangUp);
        if (!ended.Wait(KillGrace))
        {
            Signal(SignalKill);
        }
    }

    public void Resize(int columns, int rows)
    {
        WindowSize size = new((ushort)rows, (ushort)columns);
        int set = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ControlOnStack(controller, SetWindowSize, 0, 0, 0, 0, 0, 0, &size)
            : Control(controller, SetWindowSize, &size);
        if (set != 0)
        {
            throw new InvalidOperationException($"The terminal's size could not be set: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        ReaderStream.Dispose();
        WriterStream.Dispose();
        if (!ended.IsSet)
        {
            Kill();
        }
        Libc.Close(controller);
    }

    /// <summary>A signal to the program, none once it has been reaped.</summary>
    private void Signal(int signal)
    {
        lock (gate)
        {
            if (!reaped)
            {
                Libc.Signal(Pid, signal);
            }
        }
    }

    /// <summary>Waits for the program's end, then reaps it under <see cref="gate"/>, so no signal goes to an id reissued:
    /// the end heard through a kqueue, since waitid tells of a program held as if it had ended; reaped as it ends where
    /// none could watch it.</summary>
    private void Watch()
    {
        bool watched = WaitForEnd(Pid);
        int status = 0;
        int reaping;
        if (watched)
        {
            lock (gate)
            {
                reaping = Reap(&status);
                reaped = true;
            }
        }
        else
        {
            reaping = Reap(&status);
            lock (gate)
            {
                reaped = true;
            }
        }
        int signal = status & 0x7f;
        ExitCode = reaping == Pid && signal == 0 ? (status >> 8) & 0xff : 0;
        ended.Set();
        Exited?.Invoke(this, ExitCode);
    }

    private int Reap(int* status)
    {
        int reaping;
        while ((reaping = WaitForProcess(Pid, status, 0)) == -1 && Marshal.GetLastPInvokeError() == Interrupted)
        {
        }
        return reaping;
    }

    /// <summary>Waits through a kqueue for the process to end, reaping nothing: true once it has (or is gone already),
    /// false where it could not be watched.</summary>
    private static bool WaitForEnd(int pid)
    {
        int queue = KernelQueue();
        if (queue < 0)
        {
            return false;
        }
        try
        {
            KernelEvent watch = new() { Ident = (nuint)pid, Filter = ProcessFilter, Flags = EventAdd | EventOneShot, FilterFlags = NoteExit };
            if (KernelEvents(queue, &watch, 1, null, 0, null) == -1)
            {
                // One that has ended already cannot be watched.
                return Marshal.GetLastPInvokeError() == NoSuchProcess;
            }
            KernelEvent told;
            int got;
            while ((got = KernelEvents(queue, null, 0, &told, 1, null)) == -1 && Marshal.GetLastPInvokeError() == Interrupted)
            {
            }
            return got == 1;
        }
        finally
        {
            Libc.Close(queue);
        }
    }

    /// <summary>posix_spawn of the program, suspended, in a session of its own whose controlling terminal is the one
    /// named (opened as descriptor 0, which a session leader with none takes as its own), as its 0, 1 and 2.</summary>
    private static int Spawn(string program, IReadOnlyList<string> arguments, IReadOnlyList<string> environment, string folder, byte* terminal)
    {
        nint actions;
        nint attributes;
        FileActionsInit(&actions);
        AttributesInit(&attributes);
        List<nint> held = [];
        try
        {
            Check(FileActionsAddOpen(&actions, 0, terminal, OpenReadWrite, 0));
            Check(FileActionsAddDuplicate(&actions, 0, 1));
            Check(FileActionsAddDuplicate(&actions, 0, 2));
            if (folder.Length > 0)
            {
                Check(FileActionsAddFolder(&actions, folder));
            }
            uint every = uint.MaxValue;
            uint none = 0;
            Check(AttributesSetDefaults(&attributes, &every));
            Check(AttributesSetMask(&attributes, &none));
            Check(AttributesSetFlags(&attributes, SpawnFlags));
            nint* argv = Strings(arguments, held);
            nint* envp = Strings(environment, held);
            int pid;
            Check(SpawnProgram(&pid, program, &actions, &attributes, argv, envp));
            return pid;
        }
        finally
        {
            held.ForEach(Marshal.FreeCoTaskMem);
            FileActionsDestroy(&actions);
            AttributesDestroy(&attributes);
        }
    }

    private static void Check(int error)
    {
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    /// <summary>A NULL-ended array of NUL-ended UTF-8 strings, each allocation added to <paramref name="held"/> to
    /// free.</summary>
    private static nint* Strings(IReadOnlyList<string> strings, List<nint> held)
    {
        nint array = Marshal.AllocCoTaskMem((strings.Count + 1) * sizeof(nint));
        held.Add(array);
        nint* at = (nint*)array;
        for (int i = 0; i < strings.Count; i++)
        {
            at[i] = Marshal.StringToCoTaskMemUTF8(strings[i]);
            held.Add(at[i]);
        }
        at[strings.Count] = 0;
        return at;
    }

    /// <summary>The process's own environment, as the C library holds it, which a program it starts inherits.</summary>
    private static List<KeyValuePair<string, string>> Inherited()
    {
        List<KeyValuePair<string, string>> variables = [];
        for (byte** variable = *Environ(); *variable is not null; variable++)
        {
            string text = Marshal.PtrToStringUTF8((nint)(*variable))!;
            int at = text.IndexOf('=', StringComparison.Ordinal);
            variables.Add(at < 0 ? new(text, string.Empty) : new(text[..at], text[(at + 1)..]));
        }
        return variables;
    }

    /// <summary>The terminal's settings, a terminal's usual ones in UTF-8: canonical input with echo, signals from the
    /// keys, output with new lines as CR LF.</summary>
    private static Termios Settings()
    {
        Termios settings = new()
        {
            InputFlags = 0x2 | 0x100 | 0x200 | 0x800 | 0x2000 | 0x4000, // BRKINT ICRNL IXON IXANY IMAXBEL IUTF8
            OutputFlags = 0x1 | 0x2, // OPOST ONLCR
            ControlFlags = 0x300 | 0x800 | 0x4000, // CS8 CREAD HUPCL
            LocalFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x40 | 0x80 | 0x100 | 0x400, // ECHOKE ECHOE ECHOK ECHO ECHOCTL ISIG ICANON IEXTEN
            InputSpeed = 38400,
            OutputSpeed = 38400,
        };
        ControlCharacters.CopyTo(new Span<byte>(settings.Characters, TermiosCharacters));
        return settings;
    }

    /// <summary>The terminal's special characters, by macOS's places: VEOF ^D, VEOL and VEOL2 none, VERASE DEL, VWERASE
    /// ^W, VKILL ^U, VREPRINT ^R, a spare place, VINTR ^C, VQUIT ^\, VSUSP ^Z, VDSUSP ^Y, VSTART ^Q, VSTOP ^S, VLNEXT ^V,
    /// VDISCARD ^O, VMIN 1, VTIME 0, VSTATUS ^T.</summary>
    private static ReadOnlySpan<byte> ControlCharacters => [4, 0xff, 0xff, 0x7f, 23, 21, 18, 0, 3, 0x1c, 26, 25, 17, 19, 22, 15, 1, 0, 20];

    /// <summary>struct termios, macOS's.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Termios
    {
        public nuint InputFlags;
        public nuint OutputFlags;
        public nuint ControlFlags;
        public nuint LocalFlags;
        public fixed byte Characters[TermiosCharacters];
        public nuint InputSpeed;
        public nuint OutputSpeed;
    }

    /// <summary>struct winsize.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowSize(ushort rows, ushort columns)
    {
        public readonly ushort Rows = rows;
        public readonly ushort Columns = columns;
        public readonly ushort Width;
        public readonly ushort Height;
    }

    private const int TermiosCharacters = 20;
    private const int NameLength = 1024;
    private const int Interrupted = 4;
    private const int OpenReadWrite = 2;
    private const int SignalHangUp = 1;
    private const int SignalKill = 9;
    private const int SignalContinue = 19;
    private const int NoSuchProcess = 3;
    private const short ProcessFilter = -5;
    private const ushort EventAdd = 0x1;
    private const ushort EventOneShot = 0x10;
    private const uint NoteExit = 0x80000000;
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(100);

    /// <summary>TIOCSWINSZ.</summary>
    private const nuint SetWindowSize = 0x80087467;

    /// <summary>POSIX_SPAWN_SETSID, START_SUSPENDED, CLOEXEC_DEFAULT, SETSIGDEF and SETSIGMASK.</summary>
    private const short SpawnFlags = 0x0400 | 0x0080 | 0x4000 | 0x0004 | 0x0008;

    [LibraryImport("libc", EntryPoint = "openpty", SetLastError = true)]
    private static partial int OpenTerminal(int* controller, int* other, byte* name, Termios* settings, WindowSize* size);

    [LibraryImport("libc", EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SpawnProgram(int* pid, string path, nint* actions, nint* attributes, nint* arguments, nint* environment);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static partial int FileActionsInit(nint* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static partial int FileActionsDestroy(nint* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
    private static partial int FileActionsAddOpen(nint* actions, int descriptor, byte* path, int flags, ushort mode);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static partial int FileActionsAddDuplicate(nint* actions, int from, int to);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int FileActionsAddFolder(nint* actions, string folder);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init")]
    private static partial int AttributesInit(nint* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static partial int AttributesDestroy(nint* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static partial int AttributesSetFlags(nint* attributes, short flags);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
    private static partial int AttributesSetDefaults(nint* attributes, uint* signals);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigmask")]
    private static partial int AttributesSetMask(nint* attributes, uint* signals);

    [LibraryImport("libc", EntryPoint = "_NSGetEnviron")]
    private static partial byte*** Environ();

    /// <summary>struct kevent, macOS's.</summary>
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

    [LibraryImport("libc", EntryPoint = "kqueue", SetLastError = true)]
    private static partial int KernelQueue();

    [LibraryImport("libc", EntryPoint = "kevent", SetLastError = true)]
    private static partial int KernelEvents(int queue, KernelEvent* changes, int changeCount, KernelEvent* events, int eventCount, void* timeout);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static partial int WaitForProcess(int pid, int* status, int options);

    /// <summary>ioctl with its argument, as x64 passes a variadic one: in the register after the request.</summary>
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Control(int descriptor, nuint request, WindowSize* size);

    /// <summary>ioctl with its argument, as arm64 macOS passes a variadic one: on the stack, where a ninth argument
    /// goes once the eight registers are taken.</summary>
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ControlOnStack(int descriptor, nuint request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, WindowSize* size);
}
