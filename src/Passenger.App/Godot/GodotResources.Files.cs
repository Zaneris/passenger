using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>A project's files a resource property may take (<see cref="Takes"/>): by the classes its hint names, each file
/// by the class it loads as, a picture for a texture, a scene for a packed scene, a sound of its own format, a font, a
/// shader, a script of its own language, a resource file of the class; or by a file hint's patterns (<c>*.png</c>).</summary>
public static partial class GodotResources
{
    private static readonly string[] Resources = [".tres", ".res"];

    /// <summary>The classes a property may name of what a file loads as, past that class itself.</summary>
    private static readonly Dictionary<string, string[]> Bases = new(StringComparer.Ordinal)
    {
        ["CompressedTexture2D"] = ["Texture2D", "Texture"],
        ["AudioStreamWAV"] = ["AudioStream"],
        ["AudioStreamOggVorbis"] = ["AudioStream"],
        ["AudioStreamMP3"] = ["AudioStream"],
        ["FontFile"] = ["Font"],
        ["GDScript"] = ["Script"],
        ["CSharpScript"] = ["Script"],
    };

    /// <summary>The classes a file of the project loads as: by its extension, its <paramref name="typeOf"/> answer alone where
    /// that says another class than its extension's (a picture imported as an Image).</summary>
    private static IReadOnlyList<string> ClassesOf(string path, Func<string, string?> typeOf)
    {
        if (Passenger.Workspace.GDScript.GDScriptProject.LoadedAs(path) is not { } loaded)
        {
            return [];
        }
        return typeOf(path) is { Length: > 0 } written && written != loaded ? [written] : [loaded, .. Bases.GetValueOrDefault(loaded, [])];
    }

    /// <summary>Whether a file of the project may be a value of one of the classes or patterns: by the class it loads as, so
    /// never where the property would refuse it; a resource file of any class takes <paramref name="typeOf"/>'s answer (what
    /// its header says, or its <c>.import</c> beside an imported file).</summary>
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
            if (ClassesOf(path, typeOf).Contains(kind) || (PathNames.HasExtension(path, Resources) && (kind == "Resource" || typeOf(path) == kind)))
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
                if (Takes(file, classes, WrittenType))
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

    /// <summary>The class an <c>.import</c> file's text says its file loads as; null where it says none.</summary>
    public static string? ImportedType(string import) =>
        Passenger.Workspace.GodotFile.Parse(import).Sections.FirstOrDefault(s => s.Tag == "remap")?.Property("type")?.Value.Text;

    /// <summary>The class a file says it loads as: a resource file's header (<c>[gd_resource type="Gradient" …]</c>), an
    /// imported file's <c>.import</c> beside it (<c>type="AudioStreamWAV"</c>); null where it says none.</summary>
    private static string? WrittenType(string path)
    {
        try
        {
            if (!PathNames.HasExtension(path, Resources))
            {
                string import = $"{path}.import";
                return File.Exists(import) ? ImportedType(File.ReadAllText(import)) : null;
            }
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
            // A file that cannot be read says nothing of its class.
        }
        return null;
    }
}
