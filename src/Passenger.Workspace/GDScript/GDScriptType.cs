namespace Passenger.Workspace.GDScript;

/// <summary>What kind a GDScript type is, as Godot's analyzer kinds them: any value, a built-in type (<c>int</c>,
/// <c>Vector2</c>, <c>Array</c>), an engine class, a script's class (its own, another's, or an inner class), or an enum.</summary>
public enum GDScriptTypeKind
{
    Variant,
    Builtin,
    Native,
    Class,
    Enum,
}

/// <summary>A GDScript type as Godot's analyzer gives one (its DataType): its kind and name, a typed collection's element
/// types, the script class it is (with the script's res:// path), whether it is hard (declared, or inferred with
/// <c>:=</c>) or only inferred, and whether it stands for the type itself (<c>Node</c> in <c>Node.new()</c>).</summary>
public sealed record GDScriptType(GDScriptTypeKind Kind, string Name)
{
    public static readonly GDScriptType Variant = new(GDScriptTypeKind.Variant, "Variant");

    /// <summary>A typed collection's element types: <c>Array[int]</c>'s one, <c>Dictionary[String, int]</c>'s two.</summary>
    public IReadOnlyList<GDScriptType> Elements { get; init; } = [];

    /// <summary>A script class's class node: the script's own (<see cref="GDScriptTree.Class"/>) or an inner class.</summary>
    public GDScriptClass? Class { get; init; }

    /// <summary>The offset an inner class's node's positions count from (its member's, <see cref="GDScriptMember.Offset"/>).</summary>
    public int Offset { get; init; }

    /// <summary>Whether it is a function's <c>void</c>.</summary>
    public bool IsVoid { get; init; }

    /// <summary>The res:// path of the script a class or enum is declared in.</summary>
    public string? Script { get; init; }

    /// <summary>An enum's values by name.</summary>
    public IReadOnlyDictionary<string, long>? Values { get; init; }

    /// <summary>For a node's engine class, the res:// path of the C# script its scene runs on it, whose members the C# file
    /// declares; null for none.</summary>
    public string? CSharpScript { get; init; }

    /// <summary>Whether a script enum's values are only partly known, one written as more than a number.</summary>
    public bool ValuesPartial { get; init; }

    /// <summary>For an enum, the class it belongs to: the engine class, or the script class.</summary>
    public string? Owner { get; init; }

    public bool Hard { get; init; } = true;

    public bool Meta { get; init; }

    /// <summary>Whether it is a value of no known type.</summary>
    public bool IsVariant => Kind == GDScriptTypeKind.Variant;

    public static GDScriptType Builtin(string name, bool hard = true) => new(GDScriptTypeKind.Builtin, name) { Hard = hard };

    public static GDScriptType Native(string name, bool hard = true) => new(GDScriptTypeKind.Native, name) { Hard = hard };

    /// <summary>The same type as a value of it, not the type itself.</summary>
    public GDScriptType Instance => Meta ? this with { Meta = false } : this;

    /// <summary>The same type, inferred only: what an untyped variable's initializer gives.</summary>
    public GDScriptType Soft => Hard ? this with { Hard = false } : this;

    /// <summary>How Godot writes it (DataType's to_string): <c>Variant</c>, <c>null</c>, <c>Array[int]</c>,
    /// <c>Dictionary[String, int]</c>, a class by its <c>class_name</c> or its script's path, an enum by its owner and name.</summary>
    public override string ToString() => Kind switch
    {
        GDScriptTypeKind.Variant => "Variant",
        GDScriptTypeKind.Builtin when Name == "Nil" => "null",
        GDScriptTypeKind.Builtin when Name == "Array" && Elements.Count > 0 => $"Array[{Elements[0]}]",
        GDScriptTypeKind.Builtin when Name == "Dictionary" && Elements.Count > 0 => $"Dictionary[{Elements[0]}, {(Elements.Count > 1 ? Elements[1] : Variant)}]",
        GDScriptTypeKind.Native when Meta => "GDScriptNativeClass",
        GDScriptTypeKind.Enum => Owner is { Length: > 0 } owner ? $"{owner[(owner.LastIndexOf('/') + 1)..]}.{Name}" : Name,
        _ => Name,
    };
}
