using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A Godot project as its project.godot says it: its name, the scene its game starts in, and the assembly Godot
/// loads its C# from (<c>dotnet/project/assembly_name</c>), each null where the file sets none. Read from Godot's
/// ConfigFile text: <c>[section]</c> lines, then <c>key=value</c>, a value a quoted string with escapes or any other of
/// Godot's values (numbers, <c>PackedStringArray(…)</c>, a dictionary over several lines), comments after <c>;</c>.</summary>
public sealed partial record GodotProject(string Folder, string? Name, string? MainScene, string? AssemblyName)
{
    /// <summary>The file that makes a folder a Godot project.</summary>
    public const string FileName = "project.godot";

    /// <summary>The major and minor version a project.godot's text names in its features (<c>"4.4"</c>), of the Godot editor
    /// that opened it last; null where it names none.</summary>
    public static Version? GodotOf(string text) =>
        Values(text).TryGetValue("application/config/features", out string? features) && NamedVersion().Match(features) is { Success: true } named
            ? new Version(int.Parse(named.Groups[1].ValueSpan, CultureInfo.InvariantCulture), int.Parse(named.Groups[2].ValueSpan, CultureInfo.InvariantCulture))
            : null;

    [GeneratedRegex("\"(\\d{1,4})\\.(\\d{1,4})[.\"]")]
    private static partial Regex NamedVersion();

    /// <summary>The Godot project of a folder, or null when it has no project.godot or it cannot be read.</summary>
    public static GodotProject? Read(string folder)
    {
        string file = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(file) ? Parse(folder, File.ReadAllText(file)) : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Why Godot.NET.Sdk's build of a C# project does not reach the Godot folder its evaluation names, else null. The
    /// SDK reads GodotProjectDir in its props, before a project's body: set there, the source generators still name its scripts
    /// from the folder they were given (<paramref name="generators"/>, in base 64), and a relative one they read from the
    /// compiler's own folder, either way no scene's res:// path; and its build lands beside the project, where Godot finds no
    /// assembly (<paramref name="target"/>). Only a full path set before the props reaches both.</summary>
    public static string? WhyNotBuiltFor(string project, string folder, string godot, string written, string? generators, string? target)
    {
        string told = Decoded(generators) ?? written;
        string? scripts = !Path.IsPathRooted(told) ? $"Godot's source generators read {told} from the compiler's own folder, so no scene reaches its classes"
            : PathNames.Same(Path.TrimEndingDirectorySeparator(PathNames.Canonical(told)), godot) ? null : $"Godot's source generators name its scripts from {told}, so no scene reaches its classes";
        string? built = target is { Length: > 0 } && !PathNames.IsUnder(PathNames.Canonical(Path.Combine(folder, target.Replace('\\', Path.DirectorySeparatorChar))), Path.Combine(godot, ".godot"))
            ? $"its build goes to {Path.GetDirectoryName(target)}, where Godot looks for no assembly" : null;
        return (scripts, built) is (null, null) ? null
            : $"Godot.NET.Sdk does not build {project} for its Godot folder {Path.GetFileName(godot)}: {string.Join("; ", new[] { scripts, built }.OfType<string>())}. "
            + "Set GodotProjectDir to a full path in a PropertyGroup before <Import Project=\"Sdk.props\" Sdk=\"Godot.NET.Sdk\" Version=\"…\" />, where the SDK reads it";
    }

    /// <summary>Base 64's text; null for none, or what is not base 64.</summary>
    private static string? Decoded(string? base64)
    {
        if (base64 is not { Length: > 0 })
        {
            return null;
        }
        byte[] bytes = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, bytes, out int length) ? Encoding.UTF8.GetString(bytes, 0, length) : null;
    }

    /// <summary>The project a project.godot's text says, of its folder.</summary>
    public static GodotProject Parse(string folder, string text)
    {
        Dictionary<string, string> values = Strings(text);
        Dictionary<string, string> written = Values(text);
        int Number(string key) => written.TryGetValue(key, out string? value) && int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int number) && number > 0 ? number : 0;
        int width = Number("display/window/size/window_width_override") is > 0 and int over ? over : Number("display/window/size/viewport_width");
        int height = Number("display/window/size/window_height_override") is > 0 and int high ? high : Number("display/window/size/viewport_height");
        return new GodotProject(folder, values.GetValueOrDefault("application/config/name"), values.GetValueOrDefault("application/run/main_scene"),
            values.GetValueOrDefault("dotnet/project/assembly_name"))
        {
            WindowSize = (width > 0 ? width : 1152, height > 0 ? height : 648),
            Stretches = values.GetValueOrDefault("display/window/stretch/mode") is { } mode && mode != "disabled",
        };
    }

    /// <summary>The size its game's window opens at: the window's override where project.godot gives one, else its viewport's,
    /// Godot's 1152 by 648 where it says neither.</summary>
    public (int Width, int Height) WindowSize { get; init; } = (1152, 648);

    /// <summary>Whether its game scales what it draws to its window, as project.godot's stretch mode says: left off, it draws
    /// at the window's size in pixels whatever size it was made for.</summary>
    public bool Stretches { get; init; }

    /// <summary>The scenes Godot's editor opens from its command line, as their extensions say.</summary>
    public static bool IsScene(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".escn", StringComparison.OrdinalIgnoreCase);

    /// <summary>A project file's <c>res://</c> path.</summary>
    public static string ResourcePath(string folder, string path) => "res://" + Path.GetRelativePath(folder, path).Replace('\\', '/');

    /// <summary>The scene of a Godot project to open for a file of it: the file itself when it is a scene, else a text scene
    /// that uses it as a resource (a script attached to one of its nodes), the main scene first, then by path; null for none.
    /// Its own folders only, so neither Godot's cache (.godot) nor the build's output is read.</summary>
    public static string? SceneOf(string folder, string file, string? mainScene)
    {
        if (!PathNames.IsUnder(file, folder))
        {
            return null;
        }
        if (IsScene(file))
        {
            return File.Exists(file) ? file : null;
        }
        string used = ResourcePath(folder, file);
        List<string> scenes = [.. PathNames.FilesUnder(folder, ProjectFolders.Enters).SelectMany(f => f.Files)
            .Where(f => f.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".escn", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => mainScene is not null && ResourcePath(folder, f) == mainScene ? 0 : 1).ThenBy(f => f, StringComparer.Ordinal)];
        foreach (string scene in scenes)
        {
            try
            {
                if (Uses(File.ReadAllText(scene), used))
                {
                    return scene;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A scene that cannot be read names nothing to open.
            }
        }
        return null;
    }

    /// <summary>Whether a text scene names a resource by its <c>res://</c> path among its external resources.</summary>
    public static bool Uses(string scene, string resource)
    {
        string named = $"path=\"{resource}\"";
        foreach (string line in scene.Split('\n'))
        {
            if (line.StartsWith("[ext_resource", StringComparison.Ordinal) && line.Contains(named, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Every value of the text by its section and key ("debug/gdscript/warnings/enable"): a string's read, any
    /// other's as written (<c>2</c>, <c>{ "res://addons": 1 }</c>); a key set twice keeps its last.</summary>
    internal static Dictionary<string, string> Values(string text) => Read(text, written: true);

    /// <summary>Every value of the text that is a string, by its section and key ("application/config/name"); a key set
    /// twice keeps its last, as Godot reads it.</summary>
    internal static Dictionary<string, string> Strings(string text) => Read(text, written: false);

    private static Dictionary<string, string> Read(string text, bool written)
    {
        GodotFile file = GodotFile.Parse(text);
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        foreach (GodotSection section in file.Sections)
        {
            foreach (GodotEntry entry in section.Properties)
            {
                if (entry.Value is { Call: null, Text: { } said })
                {
                    found[$"{section.Tag}/{entry.Key}"] = said;
                }
                else if (written)
                {
                    found[$"{section.Tag}/{entry.Key}"] = file.Written(entry.Value);
                }
            }
        }
        return found;
    }

    /// <summary>Whether a setting's value says true or false as Godot reads a bool from it (<c>true</c> or <c>1</c>,
    /// <c>false</c> or <c>0</c>); null for any other.</summary>
    internal static bool? Truth(string value) => value.Trim() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };
}
