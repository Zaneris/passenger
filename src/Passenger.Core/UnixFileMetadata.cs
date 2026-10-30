using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>What a regular file on macOS or Linux has beside its bytes and mode: its owner and group, and how many names
/// (hard links) it has.</summary>
internal readonly record struct UnixFile(uint Owner, uint Group, uint Links);

/// <summary>What a macOS or Linux file keeps beside its bytes and mode, given to the file written to take its place as an
/// edit in place would have kept it: its owner and group where the process may set them (another user's owner only for
/// root, a group only one the person is in), and its extended attributes and access control list as far as the volume
/// holds them (macOS's copyfile(3); on Linux each attribute, the ACL being system.posix_acl_access); and the type of
/// what a path names, which no .NET call tells. None of these calls is variadic, so arm64 macOS passes each argument
/// where its fixed signature says.</summary>
internal static unsafe partial class UnixFileMetadata
{
    private const int AtWorkingFolder = -100;
    private const int NoFollow = 0x100;
    private const uint WantedStatus = 0x1 | 0x2 | 0x4 | 0x8 | 0x10;
    private const uint WantedType = 0x1;
    private const int TypeBits = 0xF000;
    private const int RegularFile = 0x8000;

    private const uint CopyAcl = 1 << 0;
    private const uint CopyXattr = 1 << 2;

    /// <summary>What the regular file at a path has, null when there is none there (or a symlink, or anything else), and
    /// on a Linux whose C library has no statx (glibc before 2.28), where the write goes on as a plain replace.</summary>
    public static UnixFile? Of(string path)
    {
        // statx's layout is the same on every Linux architecture; macOS's stat with 64-bit inodes is lstat$INODE64 on x64.
        byte* status = stackalloc byte[256];
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (StatX(AtWorkingFolder, path, NoFollow, WantedStatus, status) != 0
                    || (*(uint*)status & WantedStatus) != WantedStatus || (*(ushort*)(status + 28) & TypeBits) != RegularFile)
                {
                    return null;
                }
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            return new UnixFile(*(uint*)(status + 20), *(uint*)(status + 24), *(uint*)(status + 16));
        }
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        int read = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStatusX64(path, status) : MacStatus(path, status);
        if (read != 0 || (*(ushort*)(status + 4) & TypeBits) != RegularFile)
        {
            return null;
        }
        return new UnixFile(*(uint*)(status + 16), *(uint*)(status + 20), *(ushort*)(status + 6));
    }

    /// <summary>The type bits (S_IFMT's) of what a path names, links followed, read without opening it; null where nothing
    /// is there, on Windows, and on a Linux whose C library has no statx.</summary>
    public static int? TypeOf(string path)
    {
        byte* status = stackalloc byte[256];
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return StatX(AtWorkingFolder, path, 0, WantedType, status) == 0 && (*(uint*)status & WantedType) != 0
                    ? *(ushort*)(status + 28) & TypeBits
                    : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        int read = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacFollowedStatusX64(path, status) : MacFollowedStatus(path, status);
        return read == 0 ? *(ushort*)(status + 4) & TypeBits : null;
    }

    /// <summary>Gives the file being written, open as <paramref name="written"/> at <paramref name="writtenPath"/>, what the
    /// file at <paramref name="path"/> keeps, before any byte is in it; whatever the system refuses is left as the new file
    /// has it, and the save goes on.</summary>
    public static void Give(string path, UnixFile kept, SafeFileHandle written, string writtenPath)
    {
        int descriptor = (int)written.DangerousGetHandle();
        // Refused (EPERM) for another user's owner unless root: the group alone, which the owner may set to one of its own.
        if (ChangeOwner(descriptor, kept.Owner, kept.Group) != 0)
        {
            _ = ChangeOwner(descriptor, uint.MaxValue, kept.Group);
        }
        if (OperatingSystem.IsMacOS())
        {
            // copyfile answers -1 for what the volume cannot hold; the write goes on with what it could copy.
            _ = CopyFile(path, writtenPath, IntPtr.Zero, CopyAcl | CopyXattr);
            return;
        }
        foreach (byte[] name in AttributeNames(path))
        {
            if (Attribute(path, name) is { } value)
            {
                // Refused (a security.* label the process may not set, a volume without them): the new file goes without it.
                fixed (byte* bytes = value)
                {
                    _ = SetAttribute(descriptor, name, bytes, (nuint)value.Length, 0);
                }
            }
        }
    }

    /// <summary>The names of a Linux file's extended attributes the process may read, each ending in its NUL.</summary>
    private static List<byte[]> AttributeNames(string path)
    {
        List<byte[]> names = [];
        nint size = ListAttributes(path, null, 0);
        if (size <= 0)
        {
            return names;
        }
        byte[] list = new byte[size];
        fixed (byte* into = list)
        {
            size = ListAttributes(path, into, (nuint)list.Length);
        }
        for (int start = 0; start < size;)
        {
            int end = Array.IndexOf(list, (byte)0, start, (int)size - start);
            if (end < 0)
            {
                break;
            }
            if (end > start)
            {
                names.Add(list[start..(end + 1)]);
            }
            start = end + 1;
        }
        return names;
    }

    /// <summary>A Linux file's extended attribute's value, null when it could not be read (gone, or grown, meanwhile).</summary>
    private static byte[]? Attribute(string path, byte[] name)
    {
        fixed (byte* named = name)
        {
            nint size = GetAttribute(path, named, null, 0);
            if (size < 0)
            {
                return null;
            }
            byte[] value = new byte[size];
            fixed (byte* into = value)
            {
                nint read = size == 0 ? 0 : GetAttribute(path, named, into, (nuint)value.Length);
                return read == size ? value : null;
            }
        }
    }

    private static int SetAttribute(int descriptor, byte[] name, byte* value, nuint size, int flags)
    {
        fixed (byte* named = name)
        {
            return SetAttribute(descriptor, named, value, size, flags);
        }
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int folder, string path, int flags, uint mask, byte* status);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacStatus(string path, byte* status);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacStatusX64(string path, byte* status);

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacFollowedStatus(string path, byte* status);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacFollowedStatusX64(string path, byte* status);

    [LibraryImport("libc", EntryPoint = "fchown")]
    private static partial int ChangeOwner(int descriptor, uint owner, uint group);

    [LibraryImport("libc", EntryPoint = "copyfile", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CopyFile(string from, string to, IntPtr state, uint flags);

    [LibraryImport("libc", EntryPoint = "listxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ListAttributes(string path, byte* list, nuint size);

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetAttribute(string path, byte* name, byte* value, nuint size);

    [LibraryImport("libc", EntryPoint = "fsetxattr")]
    private static partial int SetAttribute(int descriptor, byte* name, byte* value, nuint size, int flags);
}
