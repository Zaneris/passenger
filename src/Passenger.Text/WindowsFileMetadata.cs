using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Text;

/// <summary>What a Windows file keeps beside its bytes that a new file moved over it would not have: an attribute (Hidden,
/// System, compression, a cloud file's pin), another name (a hard link), a named stream (Zone.Identifier's among them),
/// an access control list of its own (an entry not inherited, or inheritance turned off), an integrity label, or another
/// owner. Read from one handle that opens none of its content, so no scanner reads the file for it.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFileMetadata
{
    private const uint ReadControl = 0x00020000;
    private const uint ReadAttributes = 0x0080;
    private const int FileStreamInfo = 7;
    private const uint OwnerInformation = 0x1;
    private const uint DaclInformation = 0x4;
    private const uint LabelInformation = 0x10;
    private const int InsufficientBuffer = 122;

    /// <summary>"::$DATA", the stream every file has: its content.</summary>
    private const string ContentStream = "::$DATA";

    /// <summary>Who owns a file this process makes.</summary>
    private static readonly Lazy<SecurityIdentifier?> NewOwner = new(() =>
    {
        using WindowsIdentity process = WindowsIdentity.GetCurrent();
        return process.Owner;
    });

    /// <summary>Whether a move of <paramref name="temporary"/> over <paramref name="target"/> keeps all the target has
    /// but its creation date, which is given to the temporary file then; false when any of it could not be read.</summary>
    public static bool MoveKeeps(string target, string temporary)
    {
        using SafeFileHandle handle = CreateFile(target, ReadControl | ReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out FileInformation information) || information.Links != 1)
        {
            return false;
        }
        const FileAttributes Anew = FileAttributes.Archive | FileAttributes.Normal;
        if ((((FileAttributes)information.Attributes ^ File.GetAttributes(temporary)) & ~Anew) != 0 || !ContentOnly(handle) || !Inherited(handle))
        {
            return false;
        }
        File.SetCreationTimeUtc(temporary, DateTime.FromFileTimeUtc(information.Created));
        return true;
    }

    /// <summary>Whether the file's one stream is its content.</summary>
    private static unsafe bool ContentOnly(SafeFileHandle handle)
    {
        // FILE_STREAM_INFO: the next entry's offset, the name's length in bytes, two sizes, then the name.
        byte* streams = stackalloc byte[128];
        if (!GetFileInformationByHandleEx(handle, FileStreamInfo, streams, 128))
        {
            return false;
        }
        uint next = *(uint*)streams;
        int length = (int)(*(uint*)(streams + 4) / sizeof(char));
        return next == 0 && new ReadOnlySpan<char>(streams + 24, length).SequenceEqual(ContentStream);
    }

    /// <summary>Whether the file's access control list is only what its folder gives it, it has no integrity label, and
    /// this process's new files' owner owns it.</summary>
    private static bool Inherited(SafeFileHandle handle)
    {
        const uint Asked = OwnerInformation | DaclInformation | LabelInformation;
        byte[] descriptor = new byte[256];
        uint needed;
        while (!GetKernelObjectSecurity(handle, Asked, descriptor, (uint)descriptor.Length, out needed))
        {
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer || needed <= descriptor.Length)
            {
                return false;
            }
            descriptor = new byte[needed];
        }
        RawSecurityDescriptor security = new(descriptor, 0);
        return !security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            && security.DiscretionaryAcl is { } access && access.Cast<GenericAce>().All(e => e.IsInherited)
            && security.SystemAcl is null or { Count: 0 }
            && security.Owner is { } owner && owner == NewOwner.Value;
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION, its times FILETIMEs of two halves.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes;
        public long Created;
        public long Accessed;
        public long Written;
        public uint VolumeSerialNumber;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint IndexHigh;
        public uint IndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, byte* information, uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKernelObjectSecurity(SafeFileHandle handle, uint asked, byte[] descriptor, uint length, out uint needed);
}
