using System.Buffers.Binary;

namespace Passenger.Debugger.Relay;

/// <summary>A module's metadata found in its image in another process's memory, read a piece at a time: the image is
/// either laid out as the file is or mapped by section, so both places of the CLI header are tried, and the one whose
/// metadata starts with its signature is taken.</summary>
internal static class ModuleImages
{
    private const uint MetadataSignature = 0x424A5342;
    private const int HeadersRead = 4096;
    private const int CliHeaderDirectory = 14;

    /// <summary>The metadata of the image at <paramref name="image"/>, or null where none is found.</summary>
    public static byte[]? Metadata(ulong image, uint length, Func<ulong, int, byte[]?> read)
    {
        if (read(image, (int)Math.Min(length, HeadersRead)) is not { Length: >= 0x40 } headers)
        {
            return null;
        }
        int pe = BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(0x3C));
        if (headers[0] != 'M' || headers[1] != 'Z' || pe <= 0 || pe + 24 > headers.Length || BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(pe)) != 0x4550)
        {
            return null;
        }
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(pe + 6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(pe + 20));
        int optional = pe + 24;
        if (optional + optionalSize > headers.Length)
        {
            return null;
        }
        bool wide = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(optional)) == 0x20B;
        int directory = optional + (wide ? 112 : 96) + CliHeaderDirectory * 8;
        if (directory + 8 > optional + optionalSize)
        {
            return null;
        }
        uint cliRva = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(directory));
        List<(uint Rva, uint Size, uint Raw)> table = [];
        for (int i = 0, at = optional + optionalSize; i < sections && at + 40 <= headers.Length; i++, at += 40)
        {
            table.Add((BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(at + 12)), BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(at + 8)), BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(at + 20))));
        }
        foreach (bool mapped in (bool[])[true, false])
        {
            if (Place(cliRva, mapped, table) is not { } cliAt || read(image + cliAt, 16) is not { Length: 16 } cli)
            {
                continue;
            }
            uint metadataRva = BinaryPrimitives.ReadUInt32LittleEndian(cli.AsSpan(8));
            int metadataSize = BinaryPrimitives.ReadInt32LittleEndian(cli.AsSpan(12));
            if (metadataSize <= 0 || metadataSize > length || Place(metadataRva, mapped, table) is not { } metadataAt)
            {
                continue;
            }
            if (read(image + metadataAt, metadataSize) is { } metadata && metadata.Length == metadataSize && BinaryPrimitives.ReadUInt32LittleEndian(metadata) == MetadataSignature)
            {
                return metadata;
            }
        }
        return null;
    }

    private static ulong? Place(uint rva, bool mapped, List<(uint Rva, uint Size, uint Raw)> table)
    {
        if (mapped)
        {
            return rva;
        }
        foreach ((uint start, uint size, uint raw) in table)
        {
            if (rva >= start && rva < start + size)
            {
                return raw + (rva - start);
            }
        }
        return null;
    }
}
