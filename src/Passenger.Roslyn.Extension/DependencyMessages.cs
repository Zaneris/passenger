using System.Text.Json.Serialization;

namespace Passenger.Roslyn.Extension;

// The dependency map's messages to Passenger's extension in Roslyn's server, compiled into both.

/// <summary>A level of the dependency map asked for: the children of a node (<see cref="DependencyIds"/>), the solution's
/// projects for none, each multi-targeted project read in the framework chosen for it, by its file, and the folder's layer
/// rules its edges are judged by (none for a folder with none).</summary>
public sealed record DependenciesAsked(
    [property: JsonPropertyName("of")] string? Of,
    [property: JsonPropertyName("frameworks")] IReadOnlyDictionary<string, string> Frameworks,
    [property: JsonPropertyName("rules")] LayerRules? Rules = null);

/// <summary>A level of the map: its nodes, and what each of them depends on, every edge from one of them (or, for a type's
/// members, from the type itself) to the path of ids of what it depends on, from its project down as far as the level reads
/// (a namespace's level to namespaces, a type's and a member's to members).</summary>
public sealed record DependenciesFound(
    [property: JsonPropertyName("nodes")] IReadOnlyList<DependencyNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<DependencyEdge> Edges);

/// <summary>A node of the map: its id, its name as C# shows it, its kind (project, namespace, or a type's or member's as a
/// symbol's kind is named), where it is written (its first declaration's file and the server's line and character), and
/// whether it has a level below it.</summary>
public sealed record DependencyNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("at")] Place? At,
    [property: JsonPropertyName("expands")] bool Expands);

/// <summary>What one node depends on: the ids from the project down of what it uses, how many times by each kind
/// (<see cref="DependencyKinds"/>), and whether any of those uses breaks the layer rules asked with.</summary>
public sealed record DependencyEdge(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] IReadOnlyList<string> To,
    [property: JsonPropertyName("kinds")] IReadOnlyDictionary<string, int> Kinds,
    [property: JsonPropertyName("broken")] bool Broken = false);

/// <summary>A place's node on the map asked for (<see cref="Caret"/>): the ids from the project down of the symbol at the
/// caret, else of the declaration around it, else of the document's first type; none in a document of no project's.</summary>
public sealed record DependencyPlace([property: JsonPropertyName("path")] IReadOnlyList<string> Path);

/// <summary>How one node depends on another, as an edge's kinds name it.</summary>
public static class DependencyKinds
{
    /// <summary>A project's reference to another.</summary>
    public const string References = "references";

    /// <summary>A class's or an interface's base.</summary>
    public const string Inherits = "inherits";

    /// <summary>An interface a class or struct implements.</summary>
    public const string Implements = "implements";

    /// <summary>A method or constructor called, an operator used.</summary>
    public const string Calls = "calls";

    /// <summary>A type a field, property or event is of.</summary>
    public const string FieldType = "fieldType";

    /// <summary>A type a method, constructor, indexer or delegate takes or returns.</summary>
    public const string ParameterType = "parameterType";

    /// <summary>An attribute a declaration carries.</summary>
    public const string Attribute = "attribute";

    /// <summary>Any other use: a field or property read or written, a type named in code, a method passed as a value.</summary>
    public const string Uses = "uses";

    /// <summary>Every kind, in the order the map offers them.</summary>
    public static readonly IReadOnlyList<string> All = [References, Inherits, Implements, Calls, FieldType, ParameterType, Attribute, Uses];
}

/// <summary>The ids of the map's nodes: a project by its file; a namespace, a type and a member each by its project's file
/// and its own name or documentation id, so a node is known again however the code was read.</summary>
public static class DependencyIds
{
    private const char Apart = '|';

    public static string Project(string path) => $"p{Apart}{path}";

    public static string Namespace(string project, string name) => $"n{Apart}{project}{Apart}{name}";

    public static string Type(string project, string id) => $"t{Apart}{project}{Apart}{id}";

    public static string Member(string project, string id) => $"m{Apart}{project}{Apart}{id}";

    /// <summary>An id's level (0 a project's, 1 a namespace's, 2 a type's, 3 a member's), its project's file and its own part
    /// (none for a project); null for no id of the map's.</summary>
    public static (int Level, string Project, string? Own)? Read(string id)
    {
        if (id.Length < 3 || id[1] != Apart || "pntm".IndexOf(id[0], StringComparison.Ordinal) is not (>= 0 and int level))
        {
            return null;
        }
        if (level == 0)
        {
            return (0, id[2..], null);
        }
        // A namespace's name and a documentation id hold no '|', where a file's path may: the own part follows the last.
        int apart = id.LastIndexOf(Apart);
        return apart < 2 ? null : (level, id[2..apart], id[(apart + 1)..]);
    }
}
