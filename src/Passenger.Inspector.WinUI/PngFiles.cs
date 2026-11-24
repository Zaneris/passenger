using System.Buffers.Binary;
using System.IO.Compression;

namespace Passenger.Inspector.WinUI;

/// <summary>A PNG made from pixels in memory with nothing but .NET's own zlib: what the inspector writes a WinUI app's
/// picture with, as WinUI's own encoders are reached only through the app's packages.</summary>
internal static class PngFiles
{
    private static readonly uint[] Crcs = MakeCrcs();

    /// <summary>A PNG of 8-bit BGRA pixels (Windows' and DirectX's order), <paramref name="stride"/> bytes a row, as RGBA
    /// rows its colour type 6 holds.</summary>
    public static byte[] OfBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        using MemoryStream rows = new();
        using (ZLibStream zipped = new(rows, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[(width * 4) + 1];
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> source = pixels.Slice(y * stride, width * 4);
                row[0] = 0;
                for (int x = 0; x < width; x++)
                {
                    row[1 + (x * 4)] = source[(x * 4) + 2];
                    row[2 + (x * 4)] = source[(x * 4) + 1];
                    row[3 + (x * 4)] = source[x * 4];
                    row[4 + (x * 4)] = source[(x * 4) + 3];
                }
                zipped.Write(row);
            }
        }
        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, rows.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(kind);
        png.Write(data);
        uint crc = Crc(Crc(uint.MaxValue, kind), data) ^ uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    /// <summary>PNG's CRC-32 carried on over more bytes.</summary>
    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            crc = Crcs[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] MakeCrcs()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
