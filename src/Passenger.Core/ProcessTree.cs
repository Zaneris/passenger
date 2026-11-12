using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>On Windows, a process and everything it started, and what those started in turn, ended by a walk that reads
/// nothing of the caller's <see cref="System.Diagnostics.Process"/>: each process is held by a handle of the walk's own
/// from when it is found until the walk is done, so no id the walk follows is given to another process meanwhile. A
/// level is ended before the next is listed, so nothing it starts afterwards escapes; a child is a process naming its
/// parent's id, as it says itself once opened, that began no earlier than its parent. One Windows will not open or end
/// (ended, ending, or another user's) is passed over, and the walk never ends Passenger itself.</summary>
[SupportedOSPlatform("windows")]
internal static partial class ProcessTree
{
    private const uint Terminate = 0x0001;
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>TH32CS_SNAPPROCESS.</summary>
    private const uint SnapProcesses = 0x2;

    private const int ProcessBasicInformation = 0;

    /// <summary>The exit code a process ended here is given, as .NET's own kill gives it.</summary>
    private const int Killed = -1;

    /// <summary>Ends the process <paramref name="root"/> holds, whose id is <paramref name="id"/>, then everything it
    /// started, whether or not it had ended itself. The handle stays the caller's.</summary>
    public static void End(int id, SafeProcessHandle root)
    {
        _ = TerminateProcess(root, Killed);
        if (!Began(root, out long began))
        {
            return;
        }
        List<SafeProcessHandle> held = [];
        try
        {
            Dictionary<int, long> level = new() { [id] = began };
            HashSet<int> found = [id, Environment.ProcessId];
            while (level.Count > 0)
            {
                Dictionary<int, long> next = [];
                foreach ((int child, int parent) in Listed())
                {
                    if (found.Contains(child) || !level.TryGetValue(parent, out long parentBegan))
                    {
                        continue;
                    }
                    SafeProcessHandle opened = OpenProcess(Terminate | QueryLimitedInformation, false, (uint)child);
                    if (opened.IsInvalid || ParentOf(opened) != parent || !Began(opened, out long childBegan) || childBegan < parentBegan)
                    {
                        // Gone or another user's; or the id is now another process's, or the parent's id named an earlier process.
                        opened.Dispose();
                        continue;
                    }
                    held.Add(opened);
                    found.Add(child);
                    _ = TerminateProcess(opened, Killed);
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

    /// <summary>The id a process names as its parent's, from PROCESS_BASIC_INFORMATION, six pointer-sized fields with that
    /// id last; null when Windows will not say.</summary>
    private static unsafe int? ParentOf(SafeProcessHandle process)
    {
        nint* information = stackalloc nint[6];
        uint returned = 0;
        return NtQueryInformationProcess(process, ProcessBasicInformation, (byte*)information, (uint)(6 * sizeof(nint)), &returned) == 0
            ? (int)information[5]
            : null;
    }

    private static bool Began(SafeProcessHandle process, out long began) => GetProcessTimes(process, out began, out _, out _, out _);

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

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, int exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int kind, byte* information, uint length, uint* returned);
}
