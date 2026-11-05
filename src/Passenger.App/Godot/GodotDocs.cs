using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.App.Godot;

/// <summary>A Godot class or member as Godot's class reference names it: its class (<c>Node2D</c>, <c>@GlobalScope</c>), what
/// kind of member it is (<see cref="GodotDocs.Method"/>, …; null for the class itself), and the member's own name
/// (<c>position</c>, <c>_ready</c>).</summary>
public sealed record GodotDocItem(string Class, string? Kind, string? Member);

/// <summary>Godot's online class reference: a class's page and a member's place on it, for a version of Godot
/// (<c>docs.godotengine.org/en/4.7/classes/class_node2d.html#class-node2d-property-position</c>), as the reference's pages
/// name them (a label of the class, the kind and the name, made an id as its pages make one: lower case, each run of other
/// characters one hyphen, a virtual method's leading underscore read as its kind <c>private-method</c>); and the class and
/// member Godot's GDScript server names at the top of its card for a native one (<c>func Node.get_tree() -> SceneTree</c>).
/// Pure.</summary>
public static partial class GodotDocs
{
    public const string Method = "method";
    public const string Property = "property";
    public const string Signal = "signal";
    public const string Constant = "constant";
    public const string Enum = "enum";

    /// <summary>The reference's version for a Godot's (4.7.2's is 4.7), else the stable one's.</summary>
    public static string VersionOf(string? godot) =>
        godot?.Split('.') is [{ } major, { } minor, ..] && major.All(char.IsAsciiDigit) && minor.All(char.IsAsciiDigit) && major.Length > 0 && minor.Length > 0
            ? $"{major}.{minor}" : "stable";

    /// <summary>The page of a class, at its member's place where one is named.</summary>
    public static Uri Page(string version, GodotDocItem item)
    {
        string page = $"https://docs.godotengine.org/en/{version}/classes/class_{item.Class.ToLowerInvariant()}.html";
        if (item is not { Kind: { } kind, Member: { Length: > 0 } member })
        {
            return new Uri(page);
        }
        string label = kind == Enum ? $"enum_{item.Class}_{member}"
            : kind == Method && member.StartsWith('_') ? $"class_{item.Class}_private_method_{member}"
            : $"class_{item.Class}_{kind}_{member}";
        return new Uri($"{page}#{IdOf(label)}");
    }

    /// <summary>A place in the reference's shading language pages: a shader type's page (<see cref="ShaderPageOf"/>) or the
    /// language's own (<c>shading_language</c>, <c>shader_functions</c>), at a section's id where one is named.</summary>
    public static Uri ShaderPage(string version, string page, string? section) =>
        new($"https://docs.godotengine.org/en/{version}/tutorials/shaders/shader_reference/{page}.html{(section is null ? string.Empty : $"#{section}")}");

    /// <summary>The page of the reference describing a shader type's built-ins and modes.</summary>
    public static string ShaderPageOf(string type) => type switch
    {
        "spatial" => "spatial_shader",
        "canvas_item" => "canvas_item_shader",
        "particles" => "particle_shader",
        "sky" => "sky_shader",
        "fog" => "fog_shader",
        "texture_blit" => "texture_blit_shader",
        _ => "shading_language",
    };

    /// <summary>A label as the reference's pages make it an id: lower case, each run of characters but letters and digits one
    /// hyphen, none at either end.</summary>
    private static string IdOf(string label)
    {
        StringBuilder id = new(label.Length);
        foreach (char c in label.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                id.Append(c);
            }
            else if (id.Length > 0 && id[^1] != '-')
            {
                id.Append('-');
            }
        }
        return id.ToString().TrimEnd('-');
    }

    /// <summary>The native class or member a card of Godot's GDScript server is of, by the line it opens with
    /// (<c>func Node.get_tree() -> SceneTree</c>, <c>var Node2D.position: Vector2</c>, <c>&lt;Native&gt; class Node2D</c>); null
    /// for a card of the project's own code, which says where it is defined, or one that names neither.</summary>
    public static GodotDocItem? OfScriptCard(string card)
    {
        if (card.Contains("Defined in [", StringComparison.Ordinal))
        {
            return null;
        }
        string first = card.Split('\n').Select(l => l.Trim().Trim('`').Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        if (NativeClass().Match(first) is { Success: true } type)
        {
            return new GodotDocItem(type.Groups[1].Value, null, null);
        }
        if (Member().Match(first) is not { Success: true } member)
        {
            return null;
        }
        string kind = member.Groups[1].Value switch
        {
            "func" => Method,
            "var" => Property,
            "signal" => Signal,
            "enum" => Enum,
            _ => Constant,
        };
        return new GodotDocItem(member.Groups[2].Value, kind, member.Groups[3].Value);
    }

    [GeneratedRegex(@"^<Native> class (@?[A-Za-z_]\w*)")]
    private static partial Regex NativeClass();

    [GeneratedRegex(@"^(func|var|signal|const|enum) (@?[A-Za-z_]\w*)\.([A-Za-z_]\w*)")]
    private static partial Regex Member();
}
