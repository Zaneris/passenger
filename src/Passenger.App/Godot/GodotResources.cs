using Avalonia.Media.Imaging;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>A project's pictures as the resource picker and a card draw them: thumbnails, a picture's own drawn small.</summary>
public static partial class GodotResources
{
    /// <summary>A picture as a card previews it, decoded off the window's thread: a PNG no wider than <paramref name="most"/>
    /// pixels at its own size, any other drawn that wide; null for a file that is no picture Avalonia draws.</summary>
    public static Task<Bitmap?> PreviewAsync(string path, int most) => BackgroundWork.Run(() =>
    {
        if (!PathNames.HasExtension(path, [".png", ".jpg", ".jpeg", ".webp", ".bmp"]))
        {
            return null;
        }
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            bool small = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header[..8].SequenceEqual(PngSignature)
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]) <= most;
            stream.Position = 0;
            return small ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, most);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return (Bitmap?)null;
        }
    });

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A picture's thumbnail, a width of pixels wide, decoded off the window's thread; null for a file that is no
    /// picture Avalonia draws.</summary>
    public static Task<Bitmap?> ThumbnailAsync(string path, int width) => BackgroundWork.Run(() =>
    {
        if (!PathNames.HasExtension(path, [".png", ".jpg", ".jpeg", ".webp", ".bmp"]))
        {
            return null;
        }
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return (Bitmap?)null;
        }
    });
}
