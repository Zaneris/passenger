using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>The processes holding a file or folder open, as Windows' file system lists them: a process working in a folder
/// holds it too, which keeps it from moving or going. Asked once Windows has refused a move or a delete, to name what
/// refused it.</summary>
public static partial class FileHolders
{
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
