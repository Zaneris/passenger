namespace Passenger.App.Templates;

/// <summary>An item template of Passenger's own: a file of the name given with this extension, its text made from that
/// name and the folder's namespace (<see cref="Text"/>, given them in that order).</summary>
public sealed record OwnTemplate(string Extension, Func<string, string, string> Text);

/// <summary>Passenger's own templates, a source beside dotnet new's: its item templates, which New Item lists and searches
/// as it does dotnet new's, one more being one more entry in <see cref="All"/> (Add's Class/Interface makes the same types);
/// and its Godot 4 game, which New Project lists beside dotnet new's project templates (<see cref="GodotGame"/>).</summary>
public static class OwnTemplates
{
    /// <summary>The C# types Class/Interface makes, in the order its popup lists them.</summary>
    public static IReadOnlyList<string> TypeKinds { get; } = ["Class", "Interface", "Record", "Struct", "Enum"];

    /// <summary>Every one, as New Item lists it: an entry with one C# variant and no options.</summary>
    public static IReadOnlyList<ProjectTemplate> All { get; } = [.. TypeKinds.Select(Type)];

    /// <summary>A C# file declaring a type of a kind (<see cref="TypeKinds"/>), in a file-scoped namespace.</summary>
    public static string TypeText(string kind, string name, string space)
    {
        string declaration = kind switch
        {
            "Interface" => $"public interface {name}\n{{\n}}\n",
            "Record" => $"public record {name}();\n",
            "Struct" => $"public struct {name}\n{{\n}}\n",
            "Enum" => $"public enum {name}\n{{\n}}\n",
            _ => $"public class {name}\n{{\n}}\n",
        };
        return $"namespace {space};\n\n{declaration}";
    }

    /// <summary>The language of a Godot game whose scripts are GDScript, which no .NET project builds.</summary>
    public const string GDScript = "GDScript";

    /// <summary>A Godot 4 game New Project makes itself (<see cref="Passenger.Workspace.GodotGames"/>): in C#, its project
    /// targeting the frameworks given, the default first chosen, or in GDScript where the game is no project of a solution's
    /// (<paramref name="scripts"/>).</summary>
    public static ProjectTemplate GodotGame(IReadOnlyList<string> frameworks, string? framework, bool scripts)
    {
        TemplateVariant csharp = new("Godot.Game.CSharp", "C#", 0,
            "A Godot 4 game in C#: project.godot, a main scene, the script on its root, and its project on the Godot.NET.Sdk of the Godot found, "
            + "restored from that Godot's own packages", null, frameworks, framework, []);
        TemplateVariant gdscript = new("Godot.Game.GDScript", GDScript, 0,
            "A Godot 4 game in GDScript: project.godot, a main scene and the script on its root", null, [], null, []);
        return new ProjectTemplate("Godot 4 Game", ["godot"], "Passenger", ["Game", "Godot"], "Game", scripts ? [csharp, gdscript] : [csharp]) { GodotGame = true };
    }

    private static ProjectTemplate Type(string kind)
    {
        string lower = kind.ToLowerInvariant();
        TemplateVariant variant = new($"Passenger.{kind}.CSharp", "C#", 0, $"A C# {lower} in a file of its own, in the namespace of its folder", null, [], null, []);
        return new ProjectTemplate(kind, [lower], "Passenger", ["Common", "Code"], kind, [variant])
        {
            Own = new OwnTemplate(".cs", (name, space) => TypeText(kind, name, space)),
        };
    }
}
