using System.Buffers.Binary;
using System.IO.Compression;

namespace Passenger.Tests;

/// <summary>Pictures a test makes for itself, never one downloaded.</summary>
public static class TestPictures
{
    /// <summary>A PNG of one colour, written here.</summary>
    public static byte[] Png(int width, int height, byte red, byte green, byte blue)
    {
        using MemoryStream raw = new();
        using (ZLibStream deflate = new(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                deflate.WriteByte(0);
                for (int x = 0; x < width; x++)
                {
                    deflate.Write([red, green, blue]);
                }
            }
        }
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        (header[8], header[9]) = (8, 2);
        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, raw.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();

        static void Chunk(Stream png, ReadOnlySpan<byte> kind, byte[] body)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(number, (uint)body.Length);
            png.Write(number);
            byte[] checkedBytes = [.. kind, .. body];
            png.Write(checkedBytes);
            BinaryPrimitives.WriteUInt32BigEndian(number, Crc(checkedBytes));
            png.Write(number);
        }

        static uint Crc(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }
            return ~crc;
        }
    }
}
