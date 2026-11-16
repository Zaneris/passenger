using System.Buffers.Binary;
using System.Globalization;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.Relay;

/// <summary>A module's image in another process's memory, and its metadata found there, read a piece at a time: the
/// image is either laid out as the file is or mapped by section, so both places of the CLI header are tried, and the one
/// whose metadata starts with its signature is taken. An image is read only as long as its own headers lay it out, each
/// read within what one answer of the relay carries.</summary>
internal static class ModuleImages
{
    private const uint MetadataSignature = 0x424A5342;
    private const int HeadersRead = 4096;
    private const int CertificateDirectory = 4;
    private const int CliHeaderDirectory = 14;

    /// <summary>The most one read of an image takes: a quarter of the longest answer the relay carries.</summary>
    internal const int Piece = RelayConnection.LongestFrame / 4;

    /// <summary>The image of an in-memory module the process holds at <paramref name="image"/>, which it says takes
    /// <paramref name="length"/> bytes (<see cref="Image"/>).</summary>
    public static byte[]? Read(ICorDebugProcess process, ulong image, uint length, out string? why, int piece = Piece) =>
        Image(image, length, (at, count) =>
        {
            byte[] bytes = new byte[count];
            return process.TryReadMemory(new CORDB_ADDRESS(at), (uint)count, bytes, out nuint read) >= 0 && read == (nuint)count ? bytes : null;
        }, out why, piece);

    /// <summary>The image at <paramref name="image"/>, said to take <paramref name="length"/> bytes, read a piece at a time,
    /// each no longer than what came before it nor than <paramref name="piece"/>, so what is held grows only with what came;
    /// null, saying why, where its headers are no PE image's, the length runs past what they lay out, or a piece cannot be
    /// read.</summary>
    public static byte[]? Image(ulong image, uint length, Func<ulong, int, byte[]?> read, out string? why, int piece = Piece)
    {
        byte[]? headers = read(image, (int)Math.Min(length, HeadersRead));
        if (headers is null || LayoutOf(headers) is not { } layout)
        {
            why = "its headers are not a PE image's";
            return null;
        }
        if (length > layout.Longest)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"it is said to take {length:N0} bytes, past the {layout.Longest:N0} its headers lay out");
            return null;
        }
        List<byte[]> pieces = [headers];
        long at = headers.Length;
        while (at < length)
        {
            int count = (int)Math.Min(Math.Min(piece, Math.Max(at, HeadersRead)), length - at);
            if (read(image + (ulong)at, count) is not { } more || more.Length != count)
            {
                why = string.Create(CultureInfo.InvariantCulture, $"its memory could not be read past {at:N0} of its {length:N0} bytes");
                return null;
            }
            pieces.Add(more);
            at += count;
        }
        why = null;
        if (pieces.Count == 1)
        {
            return headers;
        }
        byte[] whole = new byte[at];
        int filled = 0;
        foreach (byte[] each in pieces)
        {
            each.CopyTo(whole, filled);
            filled += each.Length;
        }
        return whole;
    }

    /// <summary>The metadata of the image at <paramref name="image"/>, or null where none is found.</summary>
    public static byte[]? Metadata(ulong image, uint length, Func<ulong, int, byte[]?> read)
    {
        if (read(image, (int)Math.Min(length, HeadersRead)) is not { } headers || LayoutOf(headers) is not { } layout)
        {
            return null;
        }
        foreach (bool mapped in (bool[])[true, false])
        {
            if (Place(layout.CliRva, mapped, layout.Sections) is not { } cliAt || read(image + cliAt, 16) is not { Length: 16 } cli)
            {
                continue;
            }
            uint metadataRva = BinaryPrimitives.ReadUInt32LittleEndian(cli.AsSpan(8));
            int metadataSize = BinaryPrimitives.ReadInt32LittleEndian(cli.AsSpan(12));
            if (metadataSize <= 0 || metadataSize > length || Place(metadataRva, mapped, layout.Sections) is not { } metadataAt)
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

    /// <summary>A PE image's headers as read: where its CLI header lies, its sections (each one's place mapped, its size
    /// mapped and its place in the file), and the longest it can be: its size mapped, or to its last section's or its
    /// certificates' end in the file.</summary>
    private sealed record Layout(uint CliRva, List<(uint Rva, uint Size, uint Raw)> Sections, long Longest);

    /// <summary>The layout the headers give, or null where they are no PE image's whole.</summary>
    private static Layout? LayoutOf(byte[] headers)
    {
        if (headers.Length < 0x40)
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
        int directories = optional + (wide ? 112 : 96);
        int directory = directories + CliHeaderDirectory * 8;
        if (directory + 8 > optional + optionalSize)
        {
            return null;
        }
        long longest = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(optional + 56));
        int certificates = directories + CertificateDirectory * 8;
        longest = Math.Max(longest, (long)BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(certificates)) + BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(certificates + 4)));
        List<(uint Rva, uint Size, uint Raw)> table = [];
        for (int i = 0, at = optional + optionalSize; i < sections && at + 40 <= headers.Length; i++, at += 40)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(at + 20));
            table.Add((BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(at + 12)), BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(at + 8)), raw));
            longest = Math.Max(longest, (long)raw + BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(at + 16)));
        }
        return new Layout(BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(directory)), table, longest);
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
