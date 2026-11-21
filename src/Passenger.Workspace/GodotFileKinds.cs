namespace Passenger.Workspace;

/// <summary>The kinds of a Godot project's files Passenger reads, by their extensions, which Godot's loaders take in any
/// letter case: scenes, resources, scripts and shaders.</summary>
[IdentifierHome]
public static class GodotFileKinds
{
    /// <summary>A scene Godot writes as text.</summary>
    public const string TextScene = ".tscn";

    /// <summary>A scene Godot writes as bytes.</summary>
    public const string BinaryScene = ".scn";

    /// <summary>A scene exported as text by another program for Godot to import.</summary>
    public const string ExportedScene = ".escn";

    /// <summary>A resource Godot writes as text.</summary>
    public const string TextResource = ".tres";

    /// <summary>A resource Godot writes as bytes.</summary>
    public const string BinaryResource = ".res";

    /// <summary>An Animation or AnimationLibrary Godot writes as bytes.</summary>
    public const string Animation = ".anim";

    public const string Script = ".gd";

    public const string Shader = ".gdshader";

    /// <summary>Shader code another shader includes.</summary>
    public const string ShaderInclude = ".gdshaderinc";

    /// <summary>A shader as Godot 3 named its files.</summary>
    public const string OlderShader = ".shader";

    /// <summary>The scenes Godot's editor opens from its command line.</summary>
    public static bool IsScene(string path) => Is(path, TextScene) || Is(path, BinaryScene) || Is(path, ExportedScene);

    /// <summary>A scene whose text tells what it uses.</summary>
    public static bool IsTextScene(string path) => Is(path, TextScene) || Is(path, ExportedScene);

    /// <summary>A file Godot writes as text: a scene or a resource.</summary>
    public static bool IsText(string path) => Is(path, TextScene) || Is(path, TextResource);

    /// <summary>A resource Godot writes as bytes, as it may an Animation or an AnimationLibrary.</summary>
    public static bool IsBinary(string path) => Is(path, BinaryResource) || Is(path, Animation);

    public static bool IsScript(string path) => Is(path, Script);

    /// <summary>A shader or a shader include.</summary>
    public static bool IsShader(string path) => Is(path, Shader) || Is(path, ShaderInclude);

    public static bool IsShaderInclude(string path) => Is(path, ShaderInclude);

    /// <summary>Whether a path ends in an extension of a kind, in any letter case.</summary>
    public static bool Is(string path, [IdentifierOf(typeof(GodotFileKinds))] string kind) => path.EndsWith(kind, StringComparison.OrdinalIgnoreCase);
}
