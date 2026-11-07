using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>The processes holding a file or folder open, as Windows' file system lists them (<see cref="Of"/>), or the
/// programs holding files open as its Restart Manager names them (<see cref="Named"/>): a process working in a folder holds
/// it too, which keeps it from moving or going. Asked once Windows has refused a move or a delete, to name what refused it.</summary>
public static partial class FileHolders
{
    /// <summary>What this process is called among the programs <see cref="Named"/> names.</summary>
    public const string Itself = "Passenger itself";

    /// <summary>The most files one question of <see cref="Named"/> names: the Restart Manager answers 100 in about 25 ms,
    /// 3,000 in about a second.</summary>
    private const int MostAsked = 200;

    /// <summary>CCH_RM_SESSION_KEY, and RmGetList's answer that more processes hold the files than it was given room for.</summary>
    private const int SessionKeyLength = 32;
    private const int MoreData = 234;

    private const uint ReadAttributes = 0x80;
    private const uint BackupSemantics = 0x02000000;

    /// <summary>NtQueryInformationFile's FileProcessIdsUsingFileInformation, and its answer that the room given was too small.</summary>
    private const int ProcessIdsUsingFile = 47;
    private const int TooSmall = unchecked((int)0xC0000004);

    /// <summary>The processes but this one (whose question holds it too) with the file or folder at a path open; none off
    /// Windows, or where Windows will not say.</summary>
    public static unsafe IReadOnlyList<int> Of(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        using SafeFileHandle handle = CreateFile(path, ReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return [];
        }
        for (int size = 1024; size <= 1 << 20; size *= 8)
        {
            byte[] answer = new byte[size];
            int status;
            fixed (byte* information = answer)
            {
                status = QueryInformationFile(handle, out IoStatus _, information, (uint)size, ProcessIdsUsingFile);
            }
            if (status == TooSmall)
            {
                continue;
            }
            if (status < 0)
            {
                return [];
            }
            // A count, then each process's id as wide as a pointer, from the first place aligned to one.
            int count = BitConverter.ToInt32(answer, 0);
            List<int> ids = [];
            for (int i = 0, at = IntPtr.Size; i < count && at + IntPtr.Size <= size; i++, at += IntPtr.Size)
            {
                int id = (int)(IntPtr.Size == 8 ? BitConverter.ToInt64(answer, at) : BitConverter.ToUInt32(answer, at));
                if (id != Environment.ProcessId)
                {
                    ids.Add(id);
                }
            }
            return ids;
        }
        return [];
    }

    /// <summary>The programs holding any of the first <see cref="MostAsked"/> of these files open, each named once as the
    /// Restart Manager names it, this process too (<see cref="Itself"/>), which <see cref="Of"/> cannot tell from its own
    /// question; none off Windows, where it names none or cannot be asked. It sees files' handles alone, never a folder's.</summary>
    public static unsafe IReadOnlyList<string> Named(IEnumerable<string> files)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        string[] asked = [.. files.Take(MostAsked)];
        if (asked.Length == 0)
        {
            return [];
        }
        try
        {
            char* key = stackalloc char[SessionKeyLength + 1];
            if (RmStartSession(out uint session, 0, key) != 0)
            {
                return [];
            }
            try
            {
                return Listed(session, asked);
            }
            finally
            {
                RmEndSession(session);
            }
        }
        catch (Exception missing) when (missing is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>The programs holding the files a Restart Manager session is given, by name.</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe List<string> Listed(uint session, string[] asked)
    {
        if (RmRegisterResources(session, (uint)asked.Length, asked, 0, null, 0, null) != 0)
        {
            return [];
        }
        uint count = 0;
        uint reasons = 0;
        if (RmGetList(session, out uint needed, ref count, null, ref reasons) != MoreData || needed == 0)
        {
            return [];
        }
        ProcessInfo[] infos = new ProcessInfo[needed];
        count = needed;
        List<string> named = [];
        fixed (ProcessInfo* at = infos)
        {
            if (RmGetList(session, out _, ref count, at, ref reasons) != 0)
            {
                return [];
            }
            for (int i = 0; i < Math.Min(count, needed); i++)
            {
                string name = at[i].ProcessId == Environment.ProcessId ? Itself : new string(at[i].AppName);
                if (name.Length > 0 && !named.Contains(name))
                {
                    named.Add(name);
                }
            }
        }
        return named;
    }

    /// <summary>RM_PROCESS_INFO: the process (its id and start time), its name and its service's, and what it is.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessInfo
    {
        public int ProcessId;
        public uint StartedLow;
        public uint StartedHigh;
        public fixed char AppName[256];
        public fixed char ServiceName[64];
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        public int Restartable;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmStartSession(out uint session, int flags, char* key);

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint session);

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int RmRegisterResources(uint session, uint files, string[] names, uint applications, void* processes, uint services, void* serviceNames);

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmGetList(uint session, out uint needed, ref uint count, ProcessInfo* processes, ref uint rebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus
    {
        public nint Status;
        public nint Information;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationFile")]
    private static unsafe partial int QueryInformationFile(SafeFileHandle file, out IoStatus status, byte* information, uint length, int kind);
}
