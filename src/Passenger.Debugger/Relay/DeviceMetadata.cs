using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Text;
using Passenger.Core;

namespace Passenger.Debugger.Relay;

/// <summary>A module's metadata as an app on a device sent it, checked before the host's runtime opens it in this
/// process: an ECMA-335 metadata root whose version string, stream headers and streams lie within the bytes sent, its
/// storage flags 0, each stream the host's reader looks up (without case) named exactly and once, one table stream and no
/// minimal delta's or portable PDB's, and its tables within their stream as System.Reflection.Metadata reads them.</summary>
internal static class DeviceMetadata
{
    private const uint Signature = 0x424A5342;
    private const int RootBeforeVersion = 16;
    private const int LongestStreamName = 32;

    /// <summary>The streams the host's reader takes, found by a name compared without case.</summary>
    private static readonly string[] s_read = ["#~", "#-", "#Strings", "#US", "#GUID", "#Blob"];

    /// <summary>A minimal delta's stream, under which the host's reader lays the tables out wider than it checked them, and a
    /// portable PDB's, which System.Reflection.Metadata reads the tables by and the host's reader does not.</summary>
    private static readonly string[] s_refused = ["#JTD", "#Pdb"];

    /// <summary>Why <paramref name="metadata"/> is not metadata the host may open, else null.</summary>
    public static unsafe string? Refused(ReadOnlySpan<byte> metadata)
    {
        if (metadata.Length < RootBeforeVersion + 4 || BinaryPrimitives.ReadUInt32LittleEndian(metadata) != Signature)
        {
            return "it does not begin with a metadata root";
        }
        (ushort major, ushort minor) = (BinaryPrimitives.ReadUInt16LittleEndian(metadata[4..]), BinaryPrimitives.ReadUInt16LittleEndian(metadata[6..]));
        if ((major, minor) != (1, 1))
        {
            return $"its root is of version {major}.{minor}, where every module's is 1.1";
        }
        uint versionLength = BinaryPrimitives.ReadUInt32LittleEndian(metadata[12..]);
        if (versionLength % 4 != 0 || versionLength > metadata.Length - RootBeforeVersion - 4)
        {
            return $"its version string of {versionLength} bytes runs past its root";
        }
        if (metadata.Slice(RootBeforeVersion, (int)versionLength).IndexOf((byte)0) < 0)
        {
            return "its version string has no end";
        }
        int at = RootBeforeVersion + (int)versionLength;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(metadata[at..]);
        if (flags != 0)
        {
            return $"its storage flags are 0x{flags:x}, which ECMA-335 reserves as 0";
        }
        int streams = BinaryPrimitives.ReadUInt16LittleEndian(metadata[(at + 2)..]);
        at += 4;
        bool[] seen = new bool[s_read.Length];
        for (int i = 0; i < streams; i++)
        {
            ReadOnlySpan<byte> header = metadata[at..];
            int end = header.Length > 8 ? header.Slice(8, Math.Min(LongestStreamName, header.Length - 8)).IndexOf((byte)0) : -1;
            int taken = 8 + ((end + 4) & ~3);
            if (end < 0 || taken > header.Length)
            {
                return $"its stream header {i + 1} of {Numbers.Said(streams)} runs past its end";
            }
            string said = Encoding.Latin1.GetString(header.Slice(8, end));
            ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(header);
            ulong size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (offset + size > (ulong)metadata.Length)
            {
                return $"its stream {said} of {size} bytes at {offset} runs past its end at {metadata.Length}";
            }
            if (Array.Find(s_refused, each => Ascii.EqualsIgnoreCase(said, each)) is { } refused)
            {
                return $"it has a {refused} stream, which a module's metadata has not";
            }
            int read = Array.FindIndex(s_read, each => Ascii.EqualsIgnoreCase(said, each));
            if (read >= 0 && (said != s_read[read] || seen[read]))
            {
                return said != s_read[read] ? $"its stream {said} is named {s_read[read]} in another case" : $"it has two {said} streams";
            }
            if (read >= 0)
            {
                seen[read] = true;
            }
            at += taken;
        }
        if (seen[0] == seen[1])
        {
            return seen[0] ? "it has both a #~ and a #- stream of tables" : "it has no stream of tables";
        }
        try
        {
            fixed (byte* bytes = metadata)
            {
                _ = new MetadataReader(bytes, metadata.Length, MetadataReaderOptions.None);
            }
        }
        catch (BadImageFormatException error)
        {
            return error.Message;
        }
        return null;
    }
}
