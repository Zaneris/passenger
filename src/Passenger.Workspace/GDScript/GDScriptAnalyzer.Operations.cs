using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of operators, casts and type tests (reduce_binary_op, reduce_unary_op, reduce_assignment's
/// operators, reduce_cast, reduce_type_test), each said where the class is read whole and Passenger is sure of the types as
/// Godot has them.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>The operators of an object on their left as Variant's table registers them (the Godot Engine's
    /// <c>core/variant/variant_op.cpp</c>, MIT), which the API's dump leaves out: each with the right-hand types it takes, Nil
    /// written as Variant as the dump writes it; <c>and</c> and <c>or</c>, which take anything, left out.</summary>
    private static readonly Dictionary<string, string[]> ObjectOperators = new(StringComparer.Ordinal)
    {
        ["=="] = ["Variant", "Object"],
        ["!="] = ["Variant", "Object"],
        ["in"] = ["Dictionary", "Array"],
        ["not"] = [""],
    };

    /// <summary>The built-in types whose values Godot holds by reference (Variant::is_type_shared), which its constant folding
    /// passes over.</summary>
    private static readonly HashSet<string> SharedBuiltins = new(StringComparer.Ordinal)
    {
        "Array", "Dictionary", "PackedByteArray", "PackedInt32Array", "PackedInt64Array", "PackedFloat32Array", "PackedFloat64Array",
        "PackedStringArray", "PackedVector2Array", "PackedVector3Array", "PackedColorArray", "PackedVector4Array",
    };

    /// <summary>A binary operator's errors: of two constants Godot folds, as its evaluation of them fails (the operator not
    /// taking their types, an integer divided by zero, a negative shift), else of two hard types the operator takes no pair of.</summary>
    private void BinaryErrors(GDScriptBinary binary, GDScriptType left, GDScriptType right, Context context)
    {
        if (binary.Left is not { } leftValue || binary.Right is not { } rightValue || left.IsVariant || right.IsVariant || context.Analysis.Tree is null
            || !Knows(context.Class))
        {
            return;
        }
        string op = binary.Operator switch { K.And or K.AmpersandAmpersand => "and", K.Or or K.PipePipe => "or", _ => GDScriptToken.KindName(binary.Operator) };
        bool? leftFolds = Folding(leftValue, left, context);
        bool? rightFolds = Folding(rightValue, right, context);
        if (leftFolds == true && rightFolds == true)
        {
            FoldedErrors(binary, op, left, right, context);
            return;
        }
        if (leftFolds != false && rightFolds != false || op is "and" or "or" || !left.Hard || !right.Hard || !Sure(leftValue, context) || !Sure(rightValue, context))
        {
            return;
        }
        // Comparing with null always gives a bool, and a String's % formats whatever it is given.
        if (op is "==" or "!=" && (left is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" } || right is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" })
            || op == "%" && left is { Kind: GDScriptTypeKind.Builtin, Name: "String" })
        {
            return;
        }
        if (OperandName(left) is { } leftName && OperandName(right) is { } rightName && Takes(op, leftName, rightName) == false)
        {
            Error(context, binary, $"Invalid operands \"{left}\" and \"{right}\" for \"{op}\" operator.");
        }
    }

    /// <summary>The errors of Godot's evaluation of an operator on two constants, said by the Variant types of their values.</summary>
    private void FoldedErrors(GDScriptBinary binary, string op, GDScriptType left, GDScriptType right, Context context)
    {
        if (OperandName(left) is not { } leftName || OperandName(right) is not { } rightName || op == "%" && leftName == "String")
        {
            return;
        }
        bool? takes = Takes(op, leftName, rightName);
        if (takes == false)
        {
            Error(context, binary, $"Invalid operands to operator {op}, {leftName} and {rightName}.");
            return;
        }
        if (takes != true || leftName != "int" || rightName != "int")
        {
            return;
        }
        long? a = IntegerOf(binary.Left);
        long? b = IntegerOf(binary.Right);
        string? failed = op switch
        {
            "/" when b == 0 => "Division by zero error",
            "%" when b == 0 => "Modulo by zero error",
            "<<" or ">>" when a < 0 || b < 0 => "Invalid operands for bit shifting. Only positive operands are supported.",
            _ => null,
        };
        if (failed is not null)
        {
            Error(context, binary, $"{failed} in operator {op}.");
        }
    }

    /// <summary>A unary operator's error: a hard operand of a type the operator takes none of (<c>not</c> takes every type).</summary>
    private void UnaryErrors(GDScriptUnary unary, string op, GDScriptType operand, Context context)
    {
        if (unary.Operand is { } value && !operand.IsVariant && operand.Hard && context.Analysis.Tree is not null && Knows(context.Class) && Sure(value, context)
            && OperandName(operand) is { } name && Takes(op, name, string.Empty) == false)
        {
            Error(context, unary, $"Invalid operand of type \"{operand}\" for unary operator \"{op}\".");
        }
    }

    /// <summary>A compound assignment's error: hard types its operator takes no pair of.</summary>
    private void CompoundErrors(GDScriptAssignment assignment, GDScriptExpression assignee, GDScriptType target, GDScriptExpression assigned, GDScriptType value, Context context)
    {
        if (!target.IsVariant && target.Hard && !value.IsVariant && value.Hard && Sure(assignee, context) && Sure(assigned, context)
            && OperandName(target) is { } targetName && OperandName(value) is { } valueName
            && Takes(GDScriptToken.KindName(assignment.Operator).TrimEnd('='), targetName, valueName) == false)
        {
            Error(context, assignment, $"Invalid operands \"{target}\" and \"{value}\" for assignment operator.");
        }
    }

    /// <summary>A cast's errors: a constant of a type the built-in or enum it is cast to can't take, then a hard value of a
    /// type that converts to none of it (Variant::can_convert between built-in types, either way between classes).</summary>
    private void CastErrors(GDScriptExpression operand, GDScriptTypeNode castNode, GDScriptType given, GDScriptType cast, Context context)
    {
        if (cast.IsVariant || given.IsVariant || given.Meta || context.Analysis.Tree is null || !Knows(context.Class) || !Whole(given) || !Whole(cast))
        {
            return;
        }
        // A constant the cast's type takes is that type's, one it can't take is cast as it is.
        if (Folded(operand, context) && cast.Kind is GDScriptTypeKind.Builtin or GDScriptTypeKind.Enum && !ConstantConverted(operand, given, cast, "cast", context, cast: true))
        {
            given = cast;
        }
        if (!given.Hard || !Sure(operand, context) || given.Kind == GDScriptTypeKind.Enum && cast.Kind == GDScriptTypeKind.Enum)
        {
            return;
        }
        bool builtins = given.Kind == GDScriptTypeKind.Builtin && cast.Kind == GDScriptTypeKind.Builtin;
        bool valid = given is { Kind: GDScriptTypeKind.Builtin, Name: "int" } && cast.Kind == GDScriptTypeKind.Enum
            || given.Kind == GDScriptTypeKind.Enum && cast is { Kind: GDScriptTypeKind.Builtin, Name: "int" }
            || builtins && GDScriptConversions.Converts(given.Name, cast.Name)
            || given.Kind != GDScriptTypeKind.Builtin && cast.Kind != GDScriptTypeKind.Builtin && (TypeCompatible(cast, given) || TypeCompatible(given, cast));
        if (!valid)
        {
            Error(context, castNode, $"Invalid cast. Cannot convert from \"{given}\" to \"{cast}\".");
        }
    }

    /// <summary>A type test's error: a constant whose type is not the one tested, or a hard value whose type and the tested
    /// one take no value of each other.</summary>
    private void TypeTestErrors(GDScriptExpression operand, GDScriptType tested, GDScriptType test, Context context)
    {
        if (test.IsVariant || tested.IsVariant || tested.Meta || context.Analysis.Tree is null || !Knows(context.Class) || !Whole(tested) || !Whole(test))
        {
            return;
        }
        bool constant = Folded(operand, context);
        if (constant && (tested.Elements.Count > 0 || test.Elements.Count > 0) || !constant && (!tested.Hard || !Sure(operand, context)))
        {
            return;
        }
        if (!TypeCompatible(test, tested) && (constant || !TypeCompatible(tested, test)))
        {
            Error(context, operand, $"Expression is of type \"{tested}\" so it can't be of type \"{test}\".");
        }
    }

    /// <summary>Whether Godot folds an expression to a constant its operators evaluate: a constant (<see cref="Constancy"/>)
    /// whose value is not shared, false for a shared one.</summary>
    private bool? Folding(GDScriptExpression expression, GDScriptType type, Context context) => Constancy(expression, context) switch
    {
        true => !(type.Kind is GDScriptTypeKind.Native or GDScriptTypeKind.Class || type is { Kind: GDScriptTypeKind.Enum, Meta: true }
            || type.Kind == GDScriptTypeKind.Builtin && SharedBuiltins.Contains(type.Name)),
        var held => held,
    };

    /// <summary>Whether Godot holds an expression constant: true for a constant, a built-in type's constructor given
    /// constants among them; false for a variable, an array or dictionary written out, self or a call of the script's
    /// function; null where Passenger can't say.</summary>
    private bool? Constancy(GDScriptExpression expression, Context context) => Folded(expression, context) ? true : expression switch
    {
        GDScriptArray or GDScriptDictionary or GDScriptSelf => false,
        GDScriptIdentifier name when context.Scope?.Find(name.Name) is not null => false,
        GDScriptIdentifier name => Member(context.Class, name.Name) is { Kind: "var" } ? false : null,
        GDScriptCall { Callee: GDScriptIdentifier own } when Declared(own, context) => false,
        GDScriptCall { Callee: GDScriptIdentifier builtin } call when Engine?.Builtins.ContainsKey(builtin.Name) == true && context.Scope?.Find(builtin.Name) is null =>
            call.Arguments.All(a => Constancy(a, context) == true) ? true : null,
        _ => null,
    };

    /// <summary>A type's name in Variant's operator table: a built-in type's own, an enum's value an int and an enum a
    /// Dictionary, an object Object; null for a class or engine class named as a value.</summary>
    private static string? OperandName(GDScriptType type) => type.Kind switch
    {
        GDScriptTypeKind.Builtin => type.Name,
        GDScriptTypeKind.Enum => type.Meta ? "Dictionary" : "int",
        GDScriptTypeKind.Native or GDScriptTypeKind.Class when !type.Meta => "Object",
        _ => null,
    };

    /// <summary>Whether Variant's operator table holds an operator for two types (the right empty for a unary operator, Nil
    /// on the right written as the dump writes it); null where the API lists no operators of the left type.</summary>
    private bool? Takes(string op, string left, string right)
    {
        string named = right == "Nil" ? "Variant" : right;
        if (left == "Object")
        {
            return ObjectOperators.TryGetValue(op, out string[]? rights) && rights.Contains(named);
        }
        return Engine?.Builtins.GetValueOrDefault(left) is { Operators.Count: > 0 } builtin ? builtin.Operators.ContainsKey((op, named)) : null;
    }

    /// <summary>An integer literal's value, a negative one's too; null for anything else.</summary>
    private static long? IntegerOf(GDScriptExpression? expression) => expression switch
    {
        GDScriptLiteral { Value: long value } => value,
        GDScriptUnary { Operator: K.Minus, Operand: GDScriptLiteral { Value: long negated } } => -negated,
        _ => null,
    };

    /// <summary>Whether a type is one this reading holds whole: a class or engine class whose every base it has read.</summary>
    private bool Whole(GDScriptType type) => type.Kind is not (GDScriptTypeKind.Native or GDScriptTypeKind.Class) || Knows(type);
}
