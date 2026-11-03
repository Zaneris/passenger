namespace Passenger.Workspace.GDScript;

/// <summary>What a GDScript's types are resolved against: the engine's API (none where no Godot could be asked), each script
/// of the project by its res:// path, each <c>class_name</c> by the script that gives it, the autoloads by the script each
/// runs, and a resource's class by its path (a scene's <c>PackedScene</c>, a <c>.tres</c>'s own). Pure; its scripts are
/// parsed once each where asked.</summary>
public sealed class GDScriptProject(GodotEngineApi? engine, Func<string, GDScriptTree?> scripts, IReadOnlyDictionary<string, string> classNames,
    IReadOnlyDictionary<string, string> autoloads, Func<string, string?>? resourceClass = null, Func<string, string?>? uids = null)
{
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
    public string ResourceClass(string path) =>
        resourceClass?.Invoke(path) ?? Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tscn" or ".scn" => "PackedScene",
            ".gd" => "GDScript",
            ".cs" => "CSharpScript",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".svg" or ".bmp" or ".tga" or ".exr" or ".hdr" => "CompressedTexture2D",
            ".wav" => "AudioStreamWAV",
            ".ogg" => "AudioStreamOggVorbis",
            ".mp3" => "AudioStreamMP3",
            ".gdshader" => "Shader",
            ".ttf" or ".otf" or ".woff" or ".woff2" => "FontFile",
            _ => "Resource",
        };
}
