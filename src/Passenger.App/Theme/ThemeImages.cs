using System.Buffers.Binary;

namespace Passenger.App;

/// <summary>A theme's background pictures as data: each file's kind and size read from its header alone (PNG, JPEG or WebP),
/// checked against <see cref="ThemeFiles.MostImageBytes"/>, <see cref="ThemeFiles.MostImageSide"/> and
/// <see cref="ThemeFiles.MostImagePixels"/> before anything decodes it, so a small file never becomes a huge bitmap; only
/// Avalonia's own decoders read one whole. Called on any thread.</summary>
public static class ThemeImages
{
    /// <summary>A picture's width and height from its header, or null where it is no PNG, JPEG or WebP this reads.</summary>
    public static (int Width, int Height)? SizeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) && bytes[12..16].SequenceEqual("IHDR"u8))
        {
            return Sized(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
        }
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return Jpeg(bytes);
        }
        if (bytes.Length >= 30 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP(bytes);
        }
        return null;
    }

    /// <summary>Why a picture's file cannot be a background, naming it; null where it can.</summary>
    public static string? WhyNot(string name, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ThemeFiles.MostImageBytes)
        {
            return $"{name} is {bytes.Length} bytes, more than the {ThemeFiles.MostImageBytes} a background picture may hold";
        }
        if (SizeOf(bytes) is not (int width, int height))
        {
            return $"{name} is no PNG, JPEG or WebP picture";
        }
        if (width > ThemeFiles.MostImageSide || height > ThemeFiles.MostImageSide || (long)width * height > ThemeFiles.MostImagePixels)
        {
            return $"{name} is {width}×{height} pixels, more than a background picture may be: at most {ThemeFiles.MostImageSide} along a side "
                + $"and {ThemeFiles.MostImagePixels / 1_000_000} million in all";
        }
        return null;
    }

    /// <summary>Every picture a theme names, each read through <paramref name="read"/> by its path beside the theme's file and
    /// checked; why not, naming the first that cannot be.</summary>
    public static async Task<(Dictionary<string, byte[]> Images, string? Why)> ReadAsync(ThemeFile theme, Func<string, Task<(byte[]? Bytes, string? Why)>> read)
    {
        Dictionary<string, byte[]> images = new(StringComparer.Ordinal);
        foreach (string image in theme.Images)
        {
            (byte[]? bytes, string? why) = await read(image);
            if (bytes is null)
            {
                return (images, $"{theme.Name}'s background {image} could not be read: {why}");
            }
            if (WhyNot($"{theme.Name}'s background {image}", bytes) is { } refused)
            {
                return (images, refused);
            }
            images[image] = bytes;
        }
        return (images, null);
    }

    /// <summary>A picture's path beside a theme file's path in its source, its folders joined by /.</summary>
    public static string Beside(string themePath, string image) =>
        themePath.LastIndexOf('/') is var slash and >= 0 ? themePath[..(slash + 1)] + image : image;

    private static (int, int)? Sized(long width, long height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? ((int)width, (int)height) : null;

    private static (int, int)? Jpeg(ReadOnlySpan<byte> bytes)
    {
        int at = 2;
        while (at + 4 <= bytes.Length)
        {
            if (bytes[at] != 0xFF)
            {
                return null;
            }
            byte marker = bytes[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]);
            if (length < 2)
            {
                return null;
            }
            // A frame's start (SOF0 to SOF15, but DHT, JPG and DAC) says its lines and samples per line.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return at + 9 <= bytes.Length
                    ? Sized(BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 5)..])) : null;
            }
            at += 2 + length;
        }
        return null;
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> chunk = bytes[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            return Sized(1 + Three(bytes[24..]), 1 + Three(bytes[27..]));
        }
        if (chunk.SequenceEqual("VP8L"u8) && bytes[20] == 0x2F)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            return Sized(1 + (bits & 0x3FFF), 1 + ((bits >> 14) & 0x3FFF));
        }
        if (chunk.SequenceEqual("VP8 "u8) && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
        {
            return Sized(BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF);
        }
        return null;

        static long Three(ReadOnlySpan<byte> at) => at[0] | (at[1] << 8) | (at[2] << 16);
    }
}
