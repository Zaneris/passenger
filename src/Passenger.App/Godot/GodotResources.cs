using Avalonia.Media.Imaging;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>A project's files a resource property may take (<see cref="Takes"/>): by the classes its hint names, a picture
/// for a texture, a scene for a packed scene, a sound, a font, a shader, a script, a resource file of the class; or by a
/// file hint's patterns (<c>*.png</c>). Their thumbnails, a picture's own drawn small.</summary>
public static class GodotResources
{
    private static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".svg"];
    private static readonly string[] Sounds = [".wav", ".ogg", ".mp3"];
    private static readonly string[] Fonts = [".ttf", ".otf", ".woff", ".woff2", ".fnt"];
    private static readonly string[] Resources = [".tres", ".res"];

    /// <summary>Whether a file of the project may be a value of one of the classes or patterns, by its name; a resource file
    /// of any class takes <paramref name="typeOf"/>'s answer (its header's type), asked only for those.</summary>
    public static bool Takes(string path, IReadOnlyList<string> classes, Func<string, string?> typeOf)
    {
        if (classes.Count == 0)
        {
            return PathNames.HasExtension(path, Resources);
        }
        foreach (string kind in classes)
        {
            if (kind.StartsWith("*.", StringComparison.Ordinal))
            {
                if (path.EndsWith(kind[1..], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                continue;
            }
            bool named = kind switch
            {
                "Texture" or "Texture2D" or "CompressedTexture2D" or "ImageTexture" => PathNames.HasExtension(path, Pictures),
                "PackedScene" => PathNames.HasExtension(path, [".tscn", ".scn"]),
                "AudioStream" or "AudioStreamWAV" or "AudioStreamOggVorbis" or "AudioStreamMP3" => PathNames.HasExtension(path, Sounds),
                "Font" or "FontFile" => PathNames.HasExtension(path, Fonts),
                "Shader" => PathNames.HasExtension(path, [".gdshader", ".shader"]),
                "Script" or "CSharpScript" or "GDScript" => PathNames.HasExtension(path, [".gd", ".cs"]),
                _ => false,
            };
            if (named || (PathNames.HasExtension(path, Resources) && (kind == "Resource" || typeOf(path) == kind)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The <c>res://</c> paths of the project's files a property of the classes may take, in the order of their
    /// paths, the build's own under <c>.godot</c> left out; at most <paramref name="most"/>.</summary>
    public static IReadOnlyList<string> Under(string folder, IReadOnlyList<string> classes, int most = 2000)
    {
        string root = PathNames.Canonical(folder);
        string output = Path.Combine(root, ".godot");
        List<string> found = [];
        foreach ((_, IReadOnlyList<string> files) in PathNames.FilesUnder(root, f => !PathNames.Same(f, output) && !Path.GetFileName(f).StartsWith('.')))
        {
            foreach (string file in files)
            {
                if (Takes(file, classes, HeaderType))
                {
                    found.Add("res://" + Path.GetRelativePath(root, file).Replace('\\', '/'));
                    if (found.Count >= most)
                    {
                        return [.. found.Order(StringComparer.Ordinal)];
                    }
                }
            }
        }
        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>A resource file's class, from its header (<c>[gd_resource type="Gradient" …]</c>); null where it has none.</summary>
    private static string? HeaderType(string path)
    {
        try
        {
            using StreamReader reader = new(path);
            for (int i = 0; i < 5 && reader.ReadLine() is { } line; i++)
            {
                if (line.StartsWith("[gd_resource", StringComparison.Ordinal))
                {
                    return Passenger.Workspace.GodotFile.Parse(line).Header?.Text("type");
                }
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is no resource to offer.
        }
        return null;
    }

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
