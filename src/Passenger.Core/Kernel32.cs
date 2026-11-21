using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>Windows' calls that more than one place makes, each declared once (kernel32's, and ntdll's beneath it), and
/// what a process says of itself through them. Those that set the last error say it through
/// <see cref="Marshal.GetLastPInvokeError"/>.</summary>
[SupportedOSPlatform("windows")]
public static partial class Kernel32
{
    /// <summary>OpenProcess's PROCESS_TERMINATE.</summary>
    public const uint ProcessTerminate = 0x0001;

    /// <summary>OpenProcess's PROCESS_QUERY_LIMITED_INFORMATION, which a process of another user allows unless it is protected.</summary>
    public const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>OpenProcess's SYNCHRONIZE, to wait for the process.</summary>
    public const uint Synchronize = 0x100000;

    /// <summary>GetStdHandle's and SetStdHandle's STD_INPUT_HANDLE, STD_OUTPUT_HANDLE and STD_ERROR_HANDLE.</summary>
    public const int StdInputHandle = -10;
    public const int StdOutputHandle = -11;
    public const int StdErrorHandle = -12;

    /// <summary>SetHandleInformation's HANDLE_FLAG_INHERIT: a handle a program started inherits.</summary>
    public const uint Inherited = 1;

    /// <summary>NtQueryInformationProcess's ProcessBasicInformation.</summary>
    private const int ProcessBasicInformation = 0;

    /// <summary>TH32CS_SNAPPROCESS.</summary>
    private const uint SnapProcesses = 0x2;

    /// <summary>Each process running, by its id and the id of the process that started it, as one snapshot of the system's.</summary>
    public static unsafe List<(int Child, int Parent)> Processes()
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

    /// <summary>The id a process names as its parent's, from PROCESS_BASIC_INFORMATION, six pointer-sized fields with that
    /// id last; null when Windows will not say.</summary>
    public static unsafe int? ParentOf(SafeProcessHandle process)
    {
        nint* information = stackalloc nint[6];
        uint returned = 0;
        return NtQueryInformationProcess(process, ProcessBasicInformation, (byte*)information, (uint)(6 * sizeof(nint)), &returned) == 0
            ? (int)information[5]
            : null;
    }

    /// <summary>Whether the volume at a root (C:\, \\server\share\) keeps files' owners and access lists
    /// (GetVolumeInformation's FILE_PERSISTENT_ACLS), as NTFS and ReFS do and FAT and exFAT do not; null when Windows will
    /// not say.</summary>
    public static unsafe bool? KeepsSecurity(string root)
    {
        const uint PersistentAcls = 0x8;
        uint flags = 0;
        return GetVolumeInformation(root, null, 0, null, null, &flags, null, 0) ? (flags & PersistentAcls) != 0 : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformation(string root, char* name, uint nameSize, uint* serial, uint* longestName, uint* flags,
        char* system, uint systemSize);

    /// <summary>When a process began, as a FILETIME; null when Windows will not say.</summary>
    public static long? BeganAt(SafeProcessHandle process) => GetProcessTimes(process, out long began, out _, out _, out _) ? began : null;

    /// <summary>When a process began, where it names <paramref name="parent"/>, which began at <paramref name="parentBegan"/>,
    /// as its parent and began no earlier: Windows keeps a gone parent's id, which a later process may be given. Null where
    /// it names another, began first, or Windows will not say.</summary>
    public static long? BeganUnder(SafeProcessHandle process, int parent, long parentBegan) =>
        ParentOf(process) == parent && BeganAt(process) is { } began && began >= parentBegan ? began : null;

    /// <summary>Whether a process runs as a 32-bit program on 64-bit Windows (IsWow64Process2 naming the machine it is
    /// built for: x86's, run natively on x64 and by emulation on Arm64); null when Windows will not say.</summary>
    public static bool? Runs32Bit(SafeProcessHandle process) => IsWow64Process2(process, out ushort built, out _) ? built != 0 : null;

    /// <summary>A process's executable, as the system started it (QueryFullProcessImageName): known from the moment the
    /// process is made, before its loader has listed any module; null when it is gone or Windows will not say.</summary>
    public static string? ExecutableOf(int process)
    {
        using SafeProcessHandle opened = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process);
        return opened.IsInvalid ? null : ExecutableOf(opened);
    }

    /// <inheritdoc cref="ExecutableOf(int)"/>
    public static unsafe string? ExecutableOf(SafeProcessHandle process)
    {
        char* buffer = stackalloc char[32768];
        uint length = 32768;
        return QueryFullProcessImageName(process, 0, buffer, &length) ? new string(buffer, 0, (int)length) : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, uint* size);

    [LibraryImport("kernel32.dll", EntryPoint = "IsWow64Process2", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    public static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    public static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int kind, byte* information, uint length, uint* returned);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenThread", SetLastError = true)]
    public static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle", SetLastError = true)]
    public static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll", EntryPoint = "SetStdHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetStdHandle(int which, nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, void* information, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32First(SafeFileHandle snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32Next(SafeFileHandle snapshot, ProcessEntry* entry);
}
