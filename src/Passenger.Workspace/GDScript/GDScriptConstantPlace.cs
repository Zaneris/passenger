namespace Passenger.Workspace.GDScript;

/// <summary>What a place takes of what must be known as Godot compiles: nothing asked of it, a constant's value, or an enum's
/// value, an integer among those.</summary>
public enum GDScriptConstantPlace
{
    None,
    Value,
    Integer,
}

/// <summary>The names a constant place takes, as Godot's analyzer folds them: a name that can begin what folds, so a class or an
/// enum, which a dot goes on from. Pure.</summary>
public static class GDScriptConstantPlaces
{
    /// <summary>The keywords no constant's value holds, but for an await that leaves the constant it waits for as it was.</summary>
    private static readonly HashSet<string> Running = new(StringComparer.Ordinal) { "self", "super", "await", "func" };

    /// <summary>Whether a place takes a local or a member: a constant, an enum or a class; for an integer, a constant holding none
    /// only where it is a class a dot goes on from.</summary>
    public static bool Takes(this GDScriptConstantPlace place, GDScriptMemberInfo name) => place switch
    {
        GDScriptConstantPlace.None => true,
        _ when name.Kind is not ("const" or "enum" or "class") => false,
        GDScriptConstantPlace.Integer => name.Kind != "const" || name.Type is { IsVariant: true } or { Meta: true } or { Kind: GDScriptTypeKind.Enum } or { Kind: GDScriptTypeKind.Builtin, Name: "int" },
        _ => true,
    };

    /// <summary>Whether a place takes a keyword: an integer no literal or operator a keyword writes.</summary>
    public static bool TakesKeyword(this GDScriptConstantPlace place, string keyword) => place switch
    {
        GDScriptConstantPlace.None => true,
        GDScriptConstantPlace.Value => !Running.Contains(keyword),
        _ => false,
    };

    /// <summary>Whether a place takes a utility function: one Godot folds, for an integer one that may give one.</summary>
    public static bool TakesFunction(this GDScriptConstantPlace place, GodotApiMethod function) => place switch
    {
        GDScriptConstantPlace.None => true,
        GDScriptConstantPlace.Value => function.Folds,
        _ => function.Folds && function.Returns is "int" or "Variant",
    };
}
