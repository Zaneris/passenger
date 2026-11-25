using Passenger.Workspace;

namespace Passenger.App.Templates;

/// <summary>An item template of Passenger's own: a file of the name given with this extension, its text made from that
/// name and the folder's namespace (<see cref="Text"/>, given them in that order), and whether it declares a C# type of
/// that name, which a C# name and a namespace are asked for (<see cref="DeclaresType"/>).</summary>
public sealed record OwnTemplate(string Extension, Func<string, string, string> Text, bool DeclaresType)
{
    /// <summary>The name given without the extension, where it was written with it ("notes.txt" for "notes").</summary>
    public string Bare(string name) => name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name[..^Extension.Length] : name;
}

/// <summary>Passenger's own templates, a source beside dotnet new's: its item templates, which New Item lists and searches
/// as it does dotnet new's and Add's New offers where they fit, one more being one more entry in <see cref="All"/>;
/// and its Godot 4 game and WinUI app, which New Project lists beside dotnet new's project templates
/// (<see cref="GodotGame"/>, <see cref="WinUIApp"/>).</summary>
public static class OwnTemplates
{
    /// <summary>The C# types Class/Interface makes, in the order its popup lists them.</summary>
    public static IReadOnlyList<string> TypeKinds { get; } = ["Class", "Interface", "Record", "Struct", "Enum"];

    /// <summary>Every one, as New Item lists it, with no options: each C# type, then a text file of no language.</summary>
    public static IReadOnlyList<ProjectTemplate> All { get; } = [.. TypeKinds.Select(Type), TextFile()];

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

    /// <summary>The option of Passenger's WinUI app that says how it is packaged (<see cref="WinUIApp"/>).</summary>
    public const string Packaging = "packaging";

    /// <summary>Its value for an app run as an MSIX package, registered from its build.</summary>
    public const string Packaged = "MSIX";

    /// <summary>A WinUI app New Project makes itself (<see cref="WinUIApps"/>), as Visual Studio's template lays one out, on
    /// the frameworks given, the default first chosen; packaged as an MSIX by default, as that template is, or unpackaged.</summary>
    public static ProjectTemplate WinUIApp(IReadOnlyList<string> frameworks, string? framework)
    {
        TemplateOption packaging = new(Packaging, Packaging, "Packaging", "How Windows runs the app", TemplateOptionKind.Choice, Packaged,
            [new(Packaged, "Packaged (MSIX)", "Registered from its build as a development package and started by Windows, as a Store app is"),
                new("None", "Unpackaged", "Run as a plain program, the Windows App SDK carried in its output")], Inverted: false, Required: false, EnabledWhen: null)
        {
            Picked = true,
        };
        TemplateVariant csharp = new("WinUI.App.CSharp", "C#", 0,
            $"A WinUI 3 app on the Windows App SDK {WinUIApps.WindowsAppSdk}: App.xaml, a window with a button, an app manifest and a launch profile, "
            + "and, packaged, its package's manifest and logos", null, frameworks, framework, [packaging]) { OutputType = "WinExe" };
        return new ProjectTemplate("WinUI App", ["winui"], "Passenger", ["Desktop", "WinUI"], "App", [csharp]) { OwnProject = true };
    }

    private static ProjectTemplate Type(string kind)
    {
        string lower = kind.ToLowerInvariant();
        TemplateVariant variant = new($"Passenger.{kind}.CSharp", "C#", 0, $"A C# {lower} in a file of its own, in the namespace of its folder", null, [], null, []);
        return new ProjectTemplate(kind, [lower], "Passenger", ["Common", "Code"], kind, [variant])
        {
            Own = new OwnTemplate(".cs", (name, space) => TypeText(kind, name, space), DeclaresType: true),
        };
    }

    private static ProjectTemplate TextFile()
    {
        TemplateVariant variant = new($"Passenger.{nameof(TextFile)}", null, 0, "An empty text file", null, [], null, []);
        return new ProjectTemplate("Text File", ["text", "txt"], "Passenger", ["Common", "Text"], "TextFile", [variant])
        {
            Own = new OwnTemplate(".txt", (_, _) => string.Empty, DeclaresType: false),
        };
    }
}
