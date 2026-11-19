using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Passenger.Core;

namespace Passenger.Debugger;

/// <summary>The runtime's own threads an evaluation leaves running while it holds the program's: its tiering worker, which
/// runs none of the program's code and, held, stands partway through compiling a method, inside a type's load or the
/// check of a call to inline, holding what an evaluation may need. Nothing the debugger reads of the runtime says which
/// thread it is, so it is told by the name the runtime gives it, which Windows and Linux keep; macOS keeps no name one
/// thread gives another, so there it is held with the rest.</summary>
internal static partial class RuntimeThreads
{
    /// <summary>The name the runtime gives its tiering worker.</summary>
    private const string TieringWorker = ".NET Tiered Compilation Worker";

    /// <summary>How much of a thread's name Linux keeps.</summary>
    private const int LinuxNameLength = 15;

    /// <summary>OpenThread's THREAD_QUERY_LIMITED_INFORMATION.</summary>
    private const uint ThreadQueryLimitedInformation = 0x0800;

    /// <summary>Whether the thread of this id in the process of this id, both the system's, is the runtime's tiering worker.</summary>
    public static bool IsTieringWorker(int processId, int threadId) =>
        OperatingSystem.IsWindows() ? NameOnWindows(processId, threadId) == TieringWorker
        : OperatingSystem.IsLinux() && NameOnLinux(processId, threadId) == TieringWorker[..LinuxNameLength];

    /// <summary>A thread's name as the process gave it, null where the thread is gone, another process's, or unnamed.</summary>
    [SupportedOSPlatform("windows")]
    private static string? NameOnWindows(int processId, int threadId)
    {
        nint thread = Kernel32.OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
        if (thread == 0)
        {
            return null;
        }
        try
        {
            if (GetProcessIdOfThread(thread) != (uint)processId || GetThreadDescription(thread, out nint description) < 0 || description == 0)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(description);
            }
            finally
            {
                LocalFree(description);
            }
        }
        finally
        {
            Kernel32.CloseHandle(thread);
        }
    }

    /// <summary>A thread's name as Linux keeps it, its first 15 bytes, null where the thread is gone.</summary>
    private static string? NameOnLinux(int processId, int threadId)
    {
        try
        {
            return File.ReadAllText($"/proc/{processId}/task/{threadId}/comm").TrimEnd('\n');
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessIdOfThread")]
    private static partial uint GetProcessIdOfThread(nint thread);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadDescription")]
    private static partial int GetThreadDescription(nint thread, out nint description);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint memory);
}
