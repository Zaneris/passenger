namespace Passenger.Workspace;

/// <summary>The scheme of a Godot project's paths (<c>res://</c>), apart from the rest of <see cref="GodotResPaths"/> so the
/// language server's extension, which reads such paths in C#'s strings, compiles it in too.</summary>
public static partial class GodotResPaths
{
    public const string Scheme = "res://";
}

/// <summary>The scheme of Godot's uid names (<c>uid://</c>), apart from the rest of <see cref="GodotUids"/> so the language
/// server's extension, which reads such names in C#'s strings, compiles it in too.</summary>
public static partial class GodotUids
{
    public const string Scheme = "uid://";
}
