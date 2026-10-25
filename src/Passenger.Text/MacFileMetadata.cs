using System.Runtime.InteropServices;
using System.Text;

namespace Passenger.Text;

/// <summary>What a macOS file keeps beside its bytes, given to the file a save writes before it is moved over the old one,
/// as an edit in place would have kept it: its extended attributes (Finder's tags and colour label, a resource fork),
/// its access control list (copyfile(3)'s, the system's own) and its creation date. TextEdit's encoding attribute goes
/// once the save writes another encoding, since TextEdit would read the file in the one it names.</summary>
internal static partial class MacFileMetadata
{
    private const uint CopyAcl = 1 << 0;
    private const uint CopyXattr = 1 << 2;

    private const string TextEncoding = "com.apple.TextEncoding";

    /// <summary>Gives <paramref name="to"/> what <paramref name="from"/> keeps, as far as the system lets it: a volume
    /// without extended attributes or ACLs (some network shares) leaves the save as it was, with its bytes and mode.</summary>
    public static void Copy(string from, string to, Encoding? written)
    {
        // copyfile answers -1 for what the volume cannot hold; the save goes on with what it could copy.
        _ = CopyFile(from, to, IntPtr.Zero, CopyAcl | CopyXattr);
        try
        {
            File.SetCreationTimeUtc(to, File.GetCreationTimeUtc(from));
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The volume keeps no creation date it lets a program set.
        }
        if (Named(to, TextEncoding) is { } named && (written is null || !named.Split(';')[0].Trim().Equals(written.WebName, StringComparison.OrdinalIgnoreCase)))
        {
            _ = RemoveXattr(to, TextEncoding, 0);
        }
    }

    /// <summary>An extended attribute's value as text, or null when the file has none of that name.</summary>
    private static string? Named(string path, string name)
    {
        nint size = GetXattr(path, name, null, 0, 0, 0);
        if (size <= 0)
        {
            return null;
        }
        byte[] value = new byte[size];
        nint read = GetXattr(path, name, value, (nuint)value.Length, 0, 0);
        return read <= 0 ? null : Encoding.UTF8.GetString(value, 0, (int)read);
    }

    [LibraryImport("libc", EntryPoint = "copyfile", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CopyFile(string from, string to, IntPtr state, uint flags);

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetXattr(string path, string name, byte[]? value, nuint size, uint position, int options);

    [LibraryImport("libc", EntryPoint = "removexattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RemoveXattr(string path, string name, int options);
}
