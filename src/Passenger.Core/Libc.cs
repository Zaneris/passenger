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

    /// <summary>A process's executable, through proc_pidpath on macOS and /proc's link on Linux; null when it cannot be
    /// read (gone, or not the caller's to ask of).</summary>
    public static unsafe string? ExecutableOf(int process)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return new FileInfo($"/proc/{process}/exe").LinkTarget;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        byte* path = stackalloc byte[LongestPath];
        int length = ProcPidPath(process, path, LongestPath);
        return length > 0 ? Encoding.UTF8.GetString(path, length) : null;
    }

    [LibraryImport("libc", EntryPoint = "proc_pidpath")]
    private static unsafe partial int ProcPidPath(int process, byte* buffer, uint size);

    /// <summary>A user's account entry's name and home folder (getpwuid_r's pw_name and pw_dir), each null where the system
    /// has none; nulls for a user it knows not.</summary>
    public static unsafe (string? Name, string? Home) Account(uint uid)
    {
        // struct passwd fits in 128 bytes on macOS and Linux alike; its home is its eighth field on macOS, its sixth on Linux.
        byte* entry = stackalloc byte[128];
        byte[] strings = new byte[4096];
        IntPtr result;
        fixed (byte* text = strings)
        {
            if (GetPasswordEntry(uid, entry, text, (nuint)strings.Length, &result) != 0 || result == IntPtr.Zero)
            {
                return (null, null);
            }
            IntPtr name = Marshal.ReadIntPtr((IntPtr)entry);
            IntPtr home = Marshal.ReadIntPtr((IntPtr)entry, OperatingSystem.IsMacOS() ? 48 : 32);
            return (name == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(name), home == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(home));
        }
    }

    /// <summary>The home folder of this process's own account, as the system's account entry has it whatever HOME says; null
    /// on Windows, or where the system names none.</summary>
    public static string? AccountHome => OperatingSystem.IsWindows() ? null : Account(EffectiveUserId()).Home;

    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    private static unsafe partial int GetPasswordEntry(uint uid, byte* entry, byte* strings, nuint size, IntPtr* result);

    /// <summary>geteuid: the user this process acts as.</summary>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    public static partial uint EffectiveUserId();
}
