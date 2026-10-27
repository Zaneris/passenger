using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>The platform seam that ends Passenger's children with it, a crash or a kill of Passenger's process included.
/// On Windows each child is in the runtime's job object (KillOnParentExit), which the system kills once Passenger's
/// handle to it is gone. macOS has neither a job nor a parent-death signal: there each child leads a session and process
/// group of its own, and a watchdog shell reading a pipe only Passenger writes kills every group still alive once that
/// pipe closes, which it does however Passenger's process ends. Linux is treated as macOS is.</summary>
internal static partial class Lifeline
{
    private const int SigKill = 9;

    /// <summary>ESRCH, on macOS and Linux alike: no process is in the group.</summary>
    private const int NoSuchProcess = 3;

    /// <summary>How often groups with no process left are struck off the watchdog's list, so it never kills a group whose
    /// number the system has since given to another.</summary>
    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(10);

    /// <summary>The watchdog: "+N" puts group N on its list and "-N" strikes it off; "!C" makes C the command it runs last,
    /// after every group still listed at the end of its input is killed. It writes nothing, and a terminal's hangup or
    /// interrupt is not its to obey.</summary>
    private const string Watch = """
        exec >/dev/null 2>&1
        trap '' HUP INT
        groups=' '
        last=''
        while IFS= read -r line; do
          case $line in
            +*) groups="$groups${line#+} " ;;
            -*) case $groups in *" ${line#-} "*) groups="${groups%% ${line#-} *} ${groups#* ${line#-} }" ;; esac ;;
            !*) last="${line#!}" ;;
          esac
        done
        for group in $groups; do kill -s KILL -- "-$group"; done
        if [ -n "$last" ]; then eval "$last"; fi
        """;

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: a job's processes end as its last handle closes.</summary>
    private const uint KillOnJobClose = 0x2000;

    /// <summary>JobObjectExtendedLimitInformation, the class of <see cref="JobLimits"/>.</summary>
    private const int ExtendedLimitInformation = 9;

    private static readonly object Gate = new();
    private static readonly HashSet<int> Groups = [];

    /// <summary>On Windows, the jobs of the children whose whole family ends with them (<see cref="HoldFamily"/>), by id.</summary>
    private static readonly Dictionary<int, SafeFileHandle> Families = [];
    private static Process? watchdog;
    private static string? atEnd;
    private static bool unavailable;
    private static Timer? pruning;

    /// <summary>Sets a child up, before it starts, to end with Passenger.</summary>
    public static void Prepare(ProcessStartInfo start)
    {
        if (OperatingSystem.IsWindows())
        {
            start.KillOnParentExit = true;
            return;
        }
        start.StartDetached = true;
        lock (Gate)
        {
            Watchdog();
        }
    }

    /// <summary>Puts a child that has started, and so its group, on the watchdog's list.</summary>
    public static void Adopt(Process process) => AdoptGroup(process.Id);

    /// <summary>Puts a process group that leads itself on the watchdog's list: a child's, or a program the terminal started
    /// in its pseudo-terminal.</summary>
    public static void AdoptGroup(int group)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        lock (Gate)
        {
            Groups.Add(group);
            Tell($"+{group}");
            pruning ??= new Timer(_ => Prune(), null, PruneEvery, PruneEvery);
        }
    }

    /// <summary>Has the watchdog run a one-line shell command once Passenger's process has ended and its children's groups
    /// are killed, in place of the one given before: for what no group holds, such as the containers an engine runs for
    /// Passenger, which only a command to that engine ends. Nothing on Windows, which has no watchdog.</summary>
    public static void AtEnd(string command)
    {
        if (OperatingSystem.IsWindows() || command.Contains('\n', StringComparison.Ordinal))
        {
            return;
        }
        lock (Gate)
        {
            atEnd = command;
            Tell($"!{command}");
        }
    }

    /// <summary>On Windows, puts a child that has just started in a job of its own, which every process it starts from then
    /// on joins, so <see cref="KillGroup"/> ends them all, those whose own parent has gone among them (a restore's MSBuild
    /// nodes once the restore has ended), which no walk of its tree finds. Elsewhere its process group does that already.</summary>
    public static void HoldFamily(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        JobLimits limits = new() { LimitFlags = KillOnJobClose };
        if (job.IsInvalid || !SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobLimits>())
            || !AssignProcessToJobObject(job, process.SafeHandle))
        {
            // Held by nothing more than its tree, as every other child is.
            job.Dispose();
            return;
        }
        lock (Gate)
        {
            Families[process.Id] = job;
        }
    }

    /// <summary>Kills what is left of a child's group: what it started, and what it left behind once it ended (Roslyn's
    /// BuildHost after its server, a build's nodes). Only a group of Passenger's own, still listed, is ever signalled; on
    /// Windows, the job of a child whose family it holds (<see cref="HoldFamily"/>).</summary>
    public static void KillGroup(Process process)
    {
        int group;
        try
        {
            group = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            SafeFileHandle? job;
            lock (Gate)
            {
                Families.Remove(group, out job);
            }
            // Its last handle closing ends every process still in it.
            job?.Dispose();
            return;
        }
        lock (Gate)
        {
            if (Groups.Contains(group))
            {
                Signal(-group, SigKill);
            }
        }
    }

    private static void Prune()
    {
        lock (Gate)
        {
            foreach (int group in Groups.ToList())
            {
                if (Signal(-group, 0) != 0 && Marshal.GetLastPInvokeError() == NoSuchProcess)
                {
                    Groups.Remove(group);
                    Tell($"-{group}");
                }
            }
        }
    }

    /// <summary>Writes a line to the watchdog. One that was ended by hand is replaced, and its successor is given every
    /// group listed, so the line need not be written again.</summary>
    private static void Tell(string line)
    {
        if (Watchdog() is not { } watching)
        {
            return;
        }
        try
        {
            watching.StandardInput.Write(line + "\n");
            watching.StandardInput.Flush();
        }
        catch (IOException)
        {
            Watchdog();
        }
    }

    /// <summary>The watchdog, started with the first child (again if it was ended by hand, with every group listed). Null
    /// where there is no shell to be it: children still run, only nothing ends them on a crash.</summary>
    private static Process? Watchdog()
    {
        if (unavailable || watchdog is { HasExited: false })
        {
            return watchdog;
        }
        watchdog?.Dispose();
        watchdog = null;
        try
        {
            // Its own session too, so a terminal's Ctrl+C or hangup, which ends Passenger, leaves it to do its work.
            ProcessStartInfo start = new("/bin/sh", ["-c", Watch])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                StartDetached = true,
            };
            Process started = Process.Start(start) ?? throw new InvalidOperationException("The watchdog did not start");
            foreach (int group in Groups)
            {
                started.StandardInput.Write($"+{group}\n");
            }
            if (atEnd is not null)
            {
                started.StandardInput.Write($"!{atEnd}\n");
            }
            started.StandardInput.Flush();
            watchdog = started;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            unavailable = true;
        }
        return watchdog;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Signal(int process, int signal);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(IntPtr security, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits information, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION, its basic limits, I/O counters and memory limits in the system's order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
