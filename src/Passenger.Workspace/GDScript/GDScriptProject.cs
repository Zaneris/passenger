namespace Passenger.Workspace.GDScript;

/// <summary>A node a script's node path reaches in its scenes: its engine class, and the script it runs, where it runs one.</summary>
public sealed record GDScriptNodeAt(string Class, string? Script);

/// <summary>What a GDScript's types are resolved against: the engine's API (none where no Godot could be asked), each script
/// of the project by its res:// path, each <c>class_name</c> by the script that gives it, the autoloads by the script each
/// runs (the singletons, which code reads by name), a resource's class by its path (a scene's <c>PackedScene</c>, a
/// <c>.tres</c>'s own), and, where its files are listed whole, which res:// paths none has. Pure; its scripts are parsed
/// once each where asked.</summary>
public sealed class GDScriptProject(GodotEngineApi? engine, Func<string, GDScriptTree?> scripts, IReadOnlyDictionary<string, string> classNames,
    IReadOnlyDictionary<string, string> autoloads, Func<string, string?>? resourceClass = null, Func<string, string?>? uids = null,
    Func<string, string, GDScriptNodeAt?>? nodes = null, Func<string, bool>? lacks = null)
{
    /// <summary>Whether the project's files, listed whole, have none at a res:// path; never where they are not listed.</summary>
    public bool Lacks(string path) => lacks?.Invoke(path) == true;

    /// <summary>The node a path names from the node a script runs on (<c>$Title</c>), as every scene running the script has
    /// it; null where none says or they differ.</summary>
    public GDScriptNodeAt? NodeAt(string script, string path) => nodes?.Invoke(script, path);

    /// <summary>A project of one script alone, with an engine's API or none.</summary>
    public static GDScriptProject Alone(GodotEngineApi? engine) => new(engine, _ => null, new Dictionary<string, string>(), new Dictionary<string, string>());

    public GodotEngineApi? Engine { get; } = engine;

    public GDScriptTree? Script(string path) => scripts(path);

    /// <summary>The res:// path of the script whose <c>class_name</c> is a name; null for none.</summary>
    public string? ClassPath(string name) => classNames.GetValueOrDefault(name);

    /// <summary>The res:// path of the script an autoload of a name runs; null for none, or one that runs a scene.</summary>
    public string? Autoload(string name) => autoloads.GetValueOrDefault(name);

    public IEnumerable<string> ClassNames => classNames.Keys;

    public IEnumerable<string> Autoloads => autoloads.Keys;

    /// <summary>The res:// path a path written names: a uid:// one's file, any other as it is.</summary>
    public string Resolved(string path) => path.StartsWith("uid://", StringComparison.Ordinal) && uids?.Invoke(path) is { } found ? found : path;

    /// <summary>The class of the resource a path names: a script's, a scene's (<c>PackedScene</c>), a resource file's own.</summary>
    public string ResourceClass(string path) => resourceClass?.Invoke(path) ?? LoadedAs(path) ?? "Resource";

    /// <summary>The class Godot loads a file as by its extension, as its importers do by default; null for any other file.</summary>
    public static string? LoadedAs(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tscn" or ".scn" => "PackedScene",
        ".gd" => "GDScript",
        ".cs" => "CSharpScript",
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".svg" or ".bmp" or ".tga" or ".exr" or ".hdr" => "CompressedTexture2D",
        ".wav" => "AudioStreamWAV",
        ".ogg" => "AudioStreamOggVorbis",
        ".mp3" => "AudioStreamMP3",
        ".gdshader" or ".shader" => "Shader",
        ".ttf" or ".otf" or ".woff" or ".woff2" or ".fnt" => "FontFile",
        _ => null,
    };
}
