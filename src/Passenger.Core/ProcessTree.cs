using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>On Windows, a process and everything it started, and what those started in turn, ended by a walk that reads
/// nothing of the caller's <see cref="System.Diagnostics.Process"/>: each process is held by a handle of the walk's own
/// from when it is found until the walk is done, so no id the walk follows is given to another process meanwhile. A
/// level is ended, and waited out, before the next is listed, so nothing it starts afterwards escapes; a child is a
/// process naming its parent's id, as it says itself once opened, that began no earlier than its parent. One Windows
/// lets Passenger read but not end (a higher integrity's) is walked through, what it started ended; one it will not open
/// at all (ended, or another user's) is passed over, and the walk never ends Passenger itself.</summary>
[SupportedOSPlatform("windows")]
internal static partial class ProcessTree
{
    /// <summary>TH32CS_SNAPPROCESS.</summary>
    private const uint SnapProcesses = 0x2;

    /// <summary>The exit code a process ended here is given, as .NET's own kill gives it.</summary>
    private const int Killed = -1;

    /// <summary>The longest a walk waits, all told, for the processes it ended to be gone before it lists what they
    /// started: Windows ends a process after the call that ends it returns, and one part-way through starting another
    /// may still start it.</summary>
    private static readonly TimeSpan EndingWait = TimeSpan.FromSeconds(2);

    /// <summary>Ends the process <paramref name="root"/> holds, whose id is <paramref name="id"/>, then everything it
    /// started, whether or not it had ended itself. The handle stays the caller's.</summary>
    public static void End(int id, SafeProcessHandle root)
    {
        Deadline waited = Deadline.In(Waits.Of(EndingWait));
        _ = TerminateProcess(root, Killed);
        if (Kernel32.BeganAt(root) is not { } began)
        {
            return;
        }
        List<SafeProcessHandle> held = [];
        try
        {
            Dictionary<int, long> level = new() { [id] = began };
            List<SafeProcessHandle> ending = [root];
            HashSet<int> found = [id, Environment.ProcessId];
            while (level.Count > 0)
            {
                WaitOut(ending, waited);
                Dictionary<int, long> next = [];
                ending = [];
                foreach ((int child, int parent) in Listed())
                {
                    if (found.Contains(child) || !level.TryGetValue(parent, out long parentBegan) || Opened(child, out bool endable) is not { } opened)
                    {
                        continue;
                    }
                    if (Kernel32.BeganUnder(opened, parent, parentBegan) is not { } childBegan)
                    {
                        // The id is now another process's, or the parent's id named an earlier process.
                        opened.Dispose();
                        continue;
                    }
                    held.Add(opened);
                    found.Add(child);
                    if (endable)
                    {
                        _ = TerminateProcess(opened, Killed);
                        ending.Add(opened);
                    }
                    next[child] = childBegan;
                }
                level = next;
            }
        }
        finally
        {
            foreach (SafeProcessHandle process in held)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>A process opened to be ended and waited for, else only to be read (one of a higher integrity); null when
    /// Windows will not open it (ended, or another user's).</summary>
    private static SafeProcessHandle? Opened(int id, out bool endable)
    {
        endable = true;
        SafeProcessHandle opened = Kernel32.OpenProcess(Kernel32.ProcessTerminate | Kernel32.ProcessQueryLimitedInformation | Kernel32.Synchronize, false, (uint)id);
        if (!opened.IsInvalid)
        {
            return opened;
        }
        opened.Dispose();
        endable = false;
        opened = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, (uint)id);
        if (!opened.IsInvalid)
        {
            return opened;
        }
        opened.Dispose();
        return null;
    }

    /// <summary>Waits until each process ended is gone, or the walk's wait has run out.</summary>
    private static void WaitOut(List<SafeProcessHandle> ending, Deadline waited)
    {
        foreach (SafeProcessHandle process in ending)
        {
            _ = Kernel32.WaitForSingleObject(process, (uint)Math.Ceiling(waited.Left.TotalMilliseconds));
        }
    }

    /// <summary>Each process running, by its id and the id of the process that started it, as one snapshot of the system's.</summary>
    private static unsafe List<(int Child, int Parent)> Listed()
    {
        List<(int Child, int Parent)> listed = [];
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(SnapProcesses, 0);
        if (snapshot.IsInvalid)
        {
            return listed;
        }
        ProcessEntry entry = new() { Size = (uint)sizeof(ProcessEntry) };
        for (bool more = Process32First(snapshot, &entry); more; more = Process32Next(snapshot, &entry))
        {
            listed.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
        }
        return listed;
    }

    /// <summary>PROCESSENTRY32W.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32First(SafeFileHandle snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32Next(SafeFileHandle snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, int exitCode);
}
