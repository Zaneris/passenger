namespace Passenger.App.Templates;

/// <summary>An item template of Passenger's own: a file of the name given with this extension, its text made from that
/// name and the folder's namespace (<see cref="Text"/>, given them in that order).</summary>
public sealed record OwnTemplate(string Extension, Func<string, string, string> Text);

/// <summary>Passenger's own item templates, a source beside dotnet new's: New Item lists and searches them as it does
/// dotnet new's, and one more is one more entry in <see cref="All"/>. Add's Class/Interface makes the same types.</summary>
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
