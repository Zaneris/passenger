using System.Runtime.InteropServices;
using System.Text;

namespace Passenger.Text;

/// <summary>What a macOS file keeps beside its bytes that only a save of its text keeps, past its owner, extended attributes
/// (Finder's tags and colour label, a resource fork) and access control list, which every write of a whole file keeps
/// (<see cref="Core.AtomicFile"/>): its creation date, given to the file a save writes before it is moved over the old one.
/// TextEdit's encoding attribute goes once the save writes another encoding, since TextEdit would read the file in the one
/// it names.</summary>
internal static partial class MacFileMetadata
{
    private const string TextEncoding = "com.apple.TextEncoding";

    /// <summary>Gives <paramref name="to"/> the creation date <paramref name="from"/> has, where the volume lets a program
    /// set one, and keeps its encoding attribute only while it names the encoding written.</summary>
    public static void Copy(string from, string to, Encoding? written)
    {
        try
        {
            File.SetCreationTimeUtc(to, File.GetCreationTimeUtc(from));
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The volume keeps no creation date it lets a program set.
        }
        Encoded(to, written);
    }

    /// <summary>A file's encoding attribute taken away when it names another encoding than the one written (none: bytes put
    /// back as a file had them).</summary>
    public static void Encoded(string path, Encoding? written)
    {
        if (Named(path, TextEncoding) is { } named && (written is null || !named.Split(';')[0].Trim().Equals(written.WebName, StringComparison.OrdinalIgnoreCase)))
        {
            _ = RemoveXattr(path, TextEncoding, 0);
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

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetXattr(string path, string name, byte[]? value, nuint size, uint position, int options);

    [LibraryImport("libc", EntryPoint = "removexattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RemoveXattr(string path, string name, int options);
}
