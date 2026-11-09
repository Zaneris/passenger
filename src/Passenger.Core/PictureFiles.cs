namespace Passenger.Core;

/// <summary>The pictures Passenger draws, by their extensions: those Avalonia's decoding reads, an animated one by its
/// first frame.</summary>
public static class PictureFiles
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico"];

    /// <summary>Whether a file is one, by its extension in any letter case.</summary>
    public static bool Is(string path) => PathNames.HasExtension(path, Extensions);
}
