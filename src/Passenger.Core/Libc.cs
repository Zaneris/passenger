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

    /// <summary>close: a descriptor let go; 0, or -1.</summary>
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int descriptor);

    /// <summary>open's O_RDONLY: the file opened for reading.</summary>
    public const int ReadOnly = 0;

    /// <summary>open's O_NONBLOCK: macOS's 0x4, Linux's 0x800.</summary>
    public static readonly int NonBlocking = OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    /// <summary>open's O_CLOEXEC, the descriptor closed as the process becomes another program: macOS's 0x1000000, Linux's 0x80000.</summary>
    public static readonly int ClosedOnExec = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    /// <summary>open: a descriptor of the file named by <paramref name="path"/>'s NUL-ended UTF-8 bytes, or -1.</summary>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    public static unsafe partial int Open(byte* path, int flags);

    /// <summary>read: up to <paramref name="count"/> bytes of a descriptor into <paramref name="buffer"/>; how many, 0 at
    /// its end, or -1.</summary>
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint Read(int descriptor, byte* buffer, nuint count);

    /// <summary>FIOCLEX: macOS's _IO('f', 1), Linux's 0x5451.</summary>
    private static readonly nuint CloseOnExecRequest = OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u;

    /// <summary>Marks a descriptor to close as the process becomes another program, through ioctl, which takes no third
    /// argument here: fcntl's is variadic, which arm64 macOS passes on the stack, where a fixed signature never puts it.</summary>
    public static bool CloseOnExec(int descriptor) => Control(descriptor, CloseOnExecRequest) == 0;

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Control(int descriptor, nuint request);

    /// <summary>getxattr: a file's extended attribute named by <paramref name="name"/>'s NUL-ended bytes, its link followed,
    /// read into <paramref name="value"/>; its length (with no value, the length it needs), or -1. macOS's takes a place in
    /// a resource fork and options beside Linux's, given neither.</summary>
    public static unsafe nint ExtendedAttribute(string path, byte* name, byte* value, nuint size) =>
        OperatingSystem.IsMacOS() ? MacExtendedAttribute(path, name, value, size, 0, 0) : LinuxExtendedAttribute(path, name, value, size);

    [LibraryImport("libc", EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint LinuxExtendedAttribute(string path, byte* name, byte* value, nuint size);

    [LibraryImport("libc", EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint MacExtendedAttribute(string path, byte* name, byte* value, nuint size, uint position, int options);

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

    /// <summary>A group's entry's name and the members it lists (getgrgid_r's gr_name and gr_mem; a user whose own group it
    /// is goes unlisted); null for a group the system knows not. struct group is a name, a password, the id and the
    /// members, 32 bytes on macOS and Linux alike.</summary>
    public static unsafe (string? Name, IReadOnlyList<string> Members)? Group(uint gid)
    {
        // ERANGE, the same on both: its strings need more room.
        const int TooSmall = 34;
        byte* entry = stackalloc byte[32];
        for (int size = 4096; size <= 1 << 20; size *= 4)
        {
            byte[] strings = new byte[size];
            IntPtr result;
            fixed (byte* text = strings)
            {
                int failed = GetGroupEntry(gid, entry, text, (nuint)strings.Length, &result);
                if (failed == TooSmall)
                {
                    continue;
                }
                if (failed != 0 || result == IntPtr.Zero)
                {
                    return null;
                }
                IntPtr name = Marshal.ReadIntPtr((IntPtr)entry);
                List<string> members = [];
                for (IntPtr* member = (IntPtr*)Marshal.ReadIntPtr((IntPtr)entry, 24); member is not null && *member != IntPtr.Zero; member++)
                {
                    members.Add(Marshal.PtrToStringUTF8(*member) ?? string.Empty);
                }
                return (name == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(name), members);
            }
        }
        return null;
    }

    [LibraryImport("libc", EntryPoint = "getgrgid_r")]
    private static unsafe partial int GetGroupEntry(uint gid, byte* entry, byte* strings, nuint size, IntPtr* result);

    /// <summary>geteuid: the user this process acts as.</summary>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    public static partial uint EffectiveUserId();
}
