namespace Passenger.Workspace.GDScript;

/// <summary>What a name in a GDScript is, as its colour tells: a function, a script's variable or an engine class's
/// property, a constant or an enum's value, a signal, an enum, a class (the engine's or the project's), a built-in value
/// type, a local or a parameter.</summary>
public enum GDScriptColour
{
    Method,
    Field,
    Property,
    Constant,
    EnumMember,
    Event,
    Enum,
    Class,
    Struct,
    Local,
    Parameter,
}

/// <summary>A name's colour at its place in the text.</summary>
public readonly record struct GDScriptColoured(int Start, int Length, GDScriptColour Colour);

/// <summary>Each name's colour as the analysis resolves it, noted where it is resolved: what it declares, what it reads, the
/// types it writes.</summary>
public sealed partial class GDScriptAnalyzer
{
    private static void Paint(Context context, GDScriptIdentifier? name, GDScriptColour? colour)
    {
        if (name is { Start: >= 0 } && colour is { } painted && context.Analysis.Tree is not null)
        {
            context.Analysis.Coloured[name] = (painted, context.Offset);
        }
    }

    /// <summary>A member's colour by what it is, a variable a script's field or an engine class's property.</summary>
    private static GDScriptColour? ColourOf(GDScriptMemberInfo member) => member.Kind switch
    {
        "func" => GDScriptColour.Method,
        "var" => member.Declaration is { Script: null } ? GDScriptColour.Property : GDScriptColour.Field,
        "const" => member.Type.Kind == GDScriptTypeKind.Enum ? GDScriptColour.EnumMember : GDScriptColour.Constant,
        "signal" => GDScriptColour.Event,
        "enum" => GDScriptColour.Enum,
        "class" => GDScriptColour.Class,
        _ => null,
    };

    /// <summary>A type's colour where a name stands for it: a class's, an enum's, a built-in value type's; none for the
    /// built-in types a script writes as words (<c>int</c>, <c>String</c>), which the grammar colours.</summary>
    private static GDScriptColour? ColourOf(GDScriptType type) => type switch
    {
        { Kind: GDScriptTypeKind.Native or GDScriptTypeKind.Class } => GDScriptColour.Class,
        { Kind: GDScriptTypeKind.Enum } => GDScriptColour.Enum,
        { Kind: GDScriptTypeKind.Builtin, Name: "bool" or "int" or "float" or "String" or "Nil" } => null,
        { Kind: GDScriptTypeKind.Builtin, Name: "Array" or "Dictionary" or "StringName" or "NodePath" } => GDScriptColour.Class,
        { Kind: GDScriptTypeKind.Builtin } => GDScriptColour.Struct,
        _ => null,
    };

    /// <summary>A local's colour, a parameter's or any other's.</summary>
    private static GDScriptColour ColourOf(Local local) => local.Kind is LocalKind.Parameter or LocalKind.Setter ? GDScriptColour.Parameter : GDScriptColour.Local;

    /// <summary>A class's <c>class_name</c> and the names it extends, coloured as classes where they name one.</summary>
    private void PaintExtends(GDScriptClass @class, Context context)
    {
        Paint(context, @class.Identifier, GDScriptColour.Class);
        foreach (GDScriptIdentifier name in @class.Extends)
        {
            Paint(context, name, GlobalOrScopedType(name.Name, context.Class) is { } named ? ColourOf(named) : null);
        }
    }

    /// <summary>The names of a type written (<c>Array[Enemy]</c>, <c>Node.ProcessMode</c>) coloured as what they name.</summary>
    private void PaintType(GDScriptTypeNode node, GDScriptType resolved, Context context)
    {
        if (context.Analysis.Tree is null || node.Chain.Count == 0)
        {
            return;
        }
        GDScriptType? first = ConstantType(node.Chain[0].Name, context) ?? GlobalOrScopedType(node.Chain[0].Name, context.Class);
        Paint(context, node.Chain[0], first is null ? null : ColourOf(first));
        if (node.Chain.Count > 1)
        {
            Paint(context, node.Chain[^1], ColourOf(resolved));
        }
    }
}
