using System.Runtime.InteropServices;
using System.Text;

namespace Passenger.Core;

/// <summary>The C library's calls on macOS and Linux that more than one place makes, each declared once. Those that set
/// errno say it through <see cref="Marshal.GetLastPInvokeError"/>.</summary>
public static partial class Libc
{
    /// <summary>access's question whether the caller may write (W_OK).</summary>
    public const int MayWrite = 2;

    /// <summary>access's question whether the caller may search a folder or run a file (X_OK).</summary>
    public const int MaySearch = 1;

    /// <summary>proc_pidpath's longest path, PROC_PIDPATHINFO_MAXSIZE.</summary>
    private const int LongestPath = 4096;

    /// <summary>access: 0 when the caller may do with the path what the mode asks.</summary>
    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Access(string path, int mode);

    /// <summary>kill: a signal to a process, or with a negative id to its group; signal 0 asks only whether it is there.</summary>
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Signal(int process, int signal);

    /// <summary>dup: a new descriptor of the same open file, or -1.</summary>
    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    public static partial int Duplicate(int descriptor);

    /// <summary>dup2: <paramref name="to"/> made a descriptor of <paramref name="from"/>'s open file, or -1.</summary>
    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    public static partial int DuplicateTo(int from, int to);

    /// <summary>A process's executable on macOS, through proc_pidpath; null when it cannot be read (gone, or not the
    /// caller's to ask of).</summary>
    public static unsafe string? ExecutableOf(int process)
    {
        byte* path = stackalloc byte[LongestPath];
        int length = ProcPidPath(process, path, LongestPath);
        return length > 0 ? Encoding.UTF8.GetString(path, length) : null;
    }

    [LibraryImport("libc", EntryPoint = "proc_pidpath")]
    private static unsafe partial int ProcPidPath(int process, byte* buffer, uint size);
}
