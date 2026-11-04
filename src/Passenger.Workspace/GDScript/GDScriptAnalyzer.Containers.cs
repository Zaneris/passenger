namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of indexing, iterating and typed collections written out (reduce_subscript,
/// resolve_for, update_array_literal_element_type, update_dictionary_literal_element_type), each said where the class is
/// read whole and Passenger is sure of the types as Godot has them.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>The built-in types Godot's subscript takes no index of.</summary>
    private static readonly HashSet<string> Unindexed = new(StringComparer.Ordinal)
    {
        "RID", "bool", "Callable", "float", "int", "Nil", "NodePath", "Signal", "StringName",
    };

    /// <summary>The built-in types a for loop goes through without a typed element, besides those it gives each element of.</summary>
    private static readonly HashSet<string> Iterable = new(StringComparer.Ordinal)
    {
        "int", "float", "String", "Vector2", "Vector2i", "Vector3", "Vector3i", "Array", "Dictionary",
    };

    /// <summary>A subscript's errors: an index of a built-in type its base takes none of, one an object's base takes that is
    /// no String, and a base no index can be taken of; none where Godot gets a constant's index itself.</summary>
    private void SubscriptErrors(GDScriptSubscript subscript, GDScriptType based, GDScriptType index, Context context)
    {
        if (subscript.Base is not { } taken || subscript.Index is not { } at || context.Analysis.Tree is null || !Knows(context.Class)
            || Constancy(taken, context) != false && Constancy(at, context) != false || based.IsVariant || based.Meta || !Sure(taken, context) || !Whole(based))
        {
            return;
        }
        if (!index.IsVariant && Sure(at, context))
        {
            if (based.Kind == GDScriptTypeKind.Builtin && index.Kind == GDScriptTypeKind.Builtin && !IndexTaken(based, index.Name))
            {
                Error(context, at, $"Invalid index type \"{index}\" for a base of type \"{based}\".");
            }
            else if (based.Kind != GDScriptTypeKind.Builtin && OperandName(index) is not (null or "String" or "StringName" or "Object"))
            {
                Error(context, at, $"Only \"String\" or \"StringName\" can be used as index for type \"{based}\", but received \"{index}\".");
            }
        }
        if (based.Kind == GDScriptTypeKind.Builtin && Unindexed.Contains(based.Name))
        {
            Error(context, taken, $"Cannot use subscript operator on a base of type \"{based}\".");
        }
    }

    /// <summary>Whether a built-in type's subscript takes an index of a built-in type, as Godot's reduce_subscript has it.</summary>
    private static bool IndexTaken(GDScriptType based, string index) => based.Name switch
    {
        "Array" or "String" or "PackedByteArray" or "PackedInt32Array" or "PackedInt64Array" or "PackedFloat32Array" or "PackedFloat64Array"
            or "PackedStringArray" or "PackedVector2Array" or "PackedVector3Array" or "PackedColorArray" or "PackedVector4Array" => index is "int" or "float",
        "Rect2" or "Rect2i" or "Plane" or "Quaternion" or "AABB" => index is "String" or "StringName",
        "Basis" or "Vector2" or "Vector2i" or "Vector3" or "Vector3i" or "Vector4" or "Vector4i" or "Transform2D" or "Transform3D" or "Projection" =>
            index is "int" or "float" or "String" or "StringName",
        "Color" => index is "int" or "String" or "StringName",
        "Dictionary" when based.Elements is [{ } key, ..] => index switch
        {
            // Null stands for an object, and an int for a float.
            "Nil" => key.Kind is GDScriptTypeKind.Native or GDScriptTypeKind.Class,
            "String" or "StringName" => OperandName(key) is "String" or "StringName",
            "int" => OperandName(key) is "int" or "float",
            _ => OperandName(key) == index,
        },
        _ => true,
    };

    /// <summary>A for loop's errors: <c>range</c> given no arguments or more than three, a list of a type no loop goes through,
    /// a variable's written type no element of the list's is, and a list written out of elements that type takes none of.</summary>
    private void ForErrors(GDScriptFor loop, GDScriptType list, GDScriptType? specified, Context context)
    {
        if (loop.List is not { } written || context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        GDScriptType? element = null;
        if (written is GDScriptCall { Callee: GDScriptIdentifier { Name: "range" } } range)
        {
            if (range.Arguments.Count == 0)
            {
                Error(context, range, "Invalid call for \"range()\" function. Expected at least 1 argument, none given.");
            }
            else if (range.Arguments.Count > 3)
            {
                Error(context, range, $"Invalid call for \"range()\" function. Expected at most 3 arguments, {range.Arguments.Count} given.");
            }
            element = GDScriptType.Builtin("int");
            list = GDScriptType.Builtin("Array");
        }
        else if (!list.IsVariant && list.Hard && !list.Meta && Sure(written, context))
        {
            element = IterationOf(list);
            if (element is null && list.Kind == GDScriptTypeKind.Builtin && !Iterable.Contains(list.Name))
            {
                Error(context, written, $"Unable to iterate on value of type \"{list}\".");
            }
        }
        if (loop.Type is not { } declared || specified is not { IsVariant: false } named)
        {
            return;
        }
        if (element is { IsVariant: false } given && !TypeCompatible(named, given, implicitly: true) && !TypeCompatible(given, named))
        {
            Error(context, declared, $"Unable to iterate on value of type \"{list}\" with variable of type \"{named}\".");
        }
        switch (written)
        {
            case GDScriptArray array:
                ArrayLiteralErrors(array, named, context);
                break;
            case GDScriptDictionary dictionary:
                DictionaryLiteralErrors(dictionary, named, GDScriptType.Variant, context);
                break;
            default:
                break;
        }
    }

    /// <summary>The type of each element a for loop takes from a hard list of a type Godot types them by: a typed array's
    /// element or dictionary's key, a packed array's, a number's or String's own, an integer vector's int, a vector's float;
    /// null for any other.</summary>
    private GDScriptType? IterationOf(GDScriptType list) => list switch
    {
        { Kind: GDScriptTypeKind.Builtin, Name: "Array" or "Dictionary", Elements: [{ } typed, ..] } => typed,
        { Kind: GDScriptTypeKind.Builtin, Name: "int" or "float" or "String" } => GDScriptType.Builtin(list.Name),
        { Kind: GDScriptTypeKind.Builtin, Name: "Vector2i" or "Vector3i" } => GDScriptType.Builtin("int"),
        { Kind: GDScriptTypeKind.Builtin, Name: "Vector2" or "Vector3" } => GDScriptType.Builtin("float"),
        { Kind: GDScriptTypeKind.Builtin } packed when packed.Name.StartsWith("Packed", StringComparison.Ordinal)
            && Engine?.Builtins.GetValueOrDefault(packed.Name)?.Indexing is { } indexed => FromApi(indexed),
        { Kind: GDScriptTypeKind.Enum, Meta: false } => GDScriptType.Builtin("int"),
        _ => null,
    };

    /// <summary>The errors of an array or dictionary written out where a typed one is asked for, by the target's element types.</summary>
    private void TypedLiteralErrors(GDScriptExpression value, GDScriptType target, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class) || target.Kind != GDScriptTypeKind.Builtin)
        {
            return;
        }
        if (value is GDScriptArray array && target is { Name: "Array", Elements: [{ } element, ..] })
        {
            ArrayLiteralErrors(array, element, context);
        }
        else if (value is GDScriptDictionary dictionary && target is { Name: "Dictionary", Elements: [{ } key, ..] })
        {
            DictionaryLiteralErrors(dictionary, key, target.Elements.Count > 1 ? target.Elements[1] : GDScriptType.Variant, context);
        }
    }

    /// <summary>An array written out for an array of a type: its first element of a type that takes none of it.</summary>
    private void ArrayLiteralErrors(GDScriptArray array, GDScriptType element, Context context)
    {
        GDScriptType expected = element with { Elements = [] };
        foreach (GDScriptExpression item in array.Elements)
        {
            if (!Fits(item, expected, actual => $"an element of type \"{actual}\" in an array of type \"Array[{expected}]\"", context))
            {
                return;
            }
        }
    }

    /// <summary>A dictionary written out for a dictionary of types: its first key or value of a type that takes none of it.</summary>
    private void DictionaryLiteralErrors(GDScriptDictionary dictionary, GDScriptType key, GDScriptType value, Context context)
    {
        GDScriptType keys = key with { Elements = [] };
        GDScriptType values = value with { Elements = [] };
        string typed = $"a dictionary of type \"Dictionary[{keys}, {values}]\"";
        foreach ((GDScriptExpression itemKey, GDScriptExpression itemValue) in dictionary.Elements)
        {
            if (!dictionary.LuaTable && !Fits(itemKey, keys, actual => $"a key of type \"{actual}\" in {typed}", context)
                || !Fits(itemValue, values, actual => $"a value of type \"{actual}\" in {typed}", context))
            {
                return;
            }
        }
    }

    /// <summary>Whether an element written out fits the type its collection holds, a constant converted to it first; where
    /// not, the error said, in the words <paramref name="what"/> gives of its type.</summary>
    private bool Fits(GDScriptExpression item, GDScriptType expected, Func<GDScriptType, string> what, Context context)
    {
        GDScriptType actual = context.Analysis.TypeOf(item);
        if (Folded(item, context) && expected.Kind is GDScriptTypeKind.Builtin or GDScriptTypeKind.Enum && !actual.IsVariant
            && !ConstantConverted(item, actual, expected, "include", context))
        {
            actual = expected;
        }
        if (actual.IsVariant || !actual.Hard || !Sure(item, context) || !Whole(actual) || !Whole(expected)
            || TypeCompatible(expected, actual, implicitly: true) || TypeCompatible(actual, expected))
        {
            return true;
        }
        Error(context, item, $"Cannot have {what(actual)}.");
        return false;
    }
}
