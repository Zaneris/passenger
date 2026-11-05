using System.Runtime.CompilerServices;
using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>The integers Godot folds an enum's values to as it resolves them (reduce_expression, then Variant's operators on
/// integers), read off what the script writes: numbers, signs and operators, a ternary on a constant, a cast to int, an enum
/// value before it, a constant, an enum's or an engine enum's value. Null for all else, whose value only Godot can give.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>An expression's integer in a class's scope, <paramref name="given"/> the values of the enum it is written in.</summary>
    private long? IntegerValue(GDScriptExpression? expression, GDScriptType scope, IReadOnlyDictionary<string, long> given)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return null;
        }
        switch (expression)
        {
            case GDScriptLiteral { Value: long written }:
                return written;
            case GDScriptUnary { Operator: K.Minus or K.Plus or K.Tilde } unary when IntegerValue(unary.Operand, scope, given) is { } operand:
                return unary.Operator switch
                {
                    K.Minus => unchecked(-operand),
                    K.Tilde => ~operand,
                    _ => operand,
                };
            case GDScriptBinary binary when IntegerValue(binary.Left, scope, given) is { } left && IntegerValue(binary.Right, scope, given) is { } right:
                return Operated(binary.Operator, left, right);
            case GDScriptTernary ternary when Truth(ternary.Condition, scope, given) is { } condition:
                return IntegerValue(condition ? ternary.True : ternary.False, scope, given);
            case GDScriptCast { Type.Chain: [{ Name: "int" }] } cast:
                return IntegerValue(cast.Operand, scope, given);
            case GDScriptCall { Callee: GDScriptIdentifier { Name: "int" }, Arguments: [GDScriptLiteral { Value: double real }] }:
                return (long)real;
            case GDScriptCall { Callee: GDScriptIdentifier { Name: "int" }, Arguments: [{ } whole] }:
                return IntegerValue(whole, scope, given);
            case GDScriptIdentifier name:
                return given.TryGetValue(name.Name, out long earlier) ? earlier : NamedInteger(name.Name, scope);
            case GDScriptSubscript { IsAttribute: true, Base: GDScriptIdentifier taken, Attribute: { } member }:
                return TakenInteger(taken.Name, member.Name, scope);
            default:
                return null;
        }
    }

    /// <summary>What Variant's operators give of two integers; null for what Godot refuses and for an operator of no integer.</summary>
    private static long? Operated(K op, long left, long right) => op switch
    {
        K.Plus => unchecked(left + right),
        K.Minus => unchecked(left - right),
        K.Star => unchecked(left * right),
        K.Slash => right == 0 ? null : right == -1 ? unchecked(-left) : left / right,
        K.Percent => right == 0 ? null : right == -1 ? 0 : left % right,
        K.LessLess => left < 0 || right is < 0 or > 63 ? null : left << (int)right,
        K.GreaterGreater => left < 0 || right is < 0 or > 63 ? null : left >> (int)right,
        K.Ampersand => left & right,
        K.Pipe => left | right,
        K.Caret => left ^ right,
        K.StarStar => (long)Math.Pow(left, right),
        _ => null,
    };

    /// <summary>A ternary's condition where the script writes one Godot folds: a bool, or an integer's truth.</summary>
    private bool? Truth(GDScriptExpression? condition, GDScriptType scope, IReadOnlyDictionary<string, long> given) =>
        condition is GDScriptLiteral { Value: bool written } ? written : IntegerValue(condition, scope, given) is { } number ? number != 0 : null;

    /// <summary>The integer a name holds in a class's scope: a constant's, whose value is read in its own class, an unnamed
    /// enum's value, or an engine enum's value the class inherits or the engine holds globally.</summary>
    private long? NamedInteger(string name, GDScriptType scope)
    {
        foreach (GDScriptType at in ScopeClasses(scope))
        {
            if (OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) named))
            {
                return named.Node switch
                {
                    GDScriptConstant { Initializer: { } written } constant => ConstantInteger(constant, written, at),
                    GDScriptEnum { Identifier: null } unnamed when EnumType(unnamed, at, meta: false) is { ValuesPartial: false, Values: { } values } =>
                        values.TryGetValue(name, out long held) ? held : null,
                    _ => null,
                };
            }
        }
        if (Member(scope, name) is { Kind: "const", Type.Values: { } inherited } && inherited.TryGetValue(name, out long value))
        {
            return value;
        }
        return Engine?.GlobalEnums.Values.Select(e => e.Values.TryGetValue(name, out long global) ? global : (long?)null).FirstOrDefault(v => v is not null);
    }

    /// <summary>A constant's integer, null for one that reaches itself.</summary>
    private long? ConstantInteger(GDScriptConstant constant, GDScriptExpression value, GDScriptType owner)
    {
        if (!resolving.Add(constant))
        {
            return null;
        }
        long? integer = IntegerValue(value, owner, Empty);
        resolving.Remove(constant);
        return integer;
    }

    /// <summary>No enum's values, for what is read outside one.</summary>
    private static readonly IReadOnlyDictionary<string, long> Empty = new Dictionary<string, long>();

    /// <summary>The integer a member holds of what a name in a class's scope stands for: an enum's value, a class's constant.</summary>
    private long? TakenInteger(string taken, string member, GDScriptType scope)
    {
        GDScriptType? held = EnumNamed(taken, scope) ?? GlobalOrScopedType(taken, scope);
        return held switch
        {
            { Kind: GDScriptTypeKind.Enum, ValuesPartial: false, Values: { } values } => values.TryGetValue(member, out long value) ? value : null,
            { Kind: GDScriptTypeKind.Class or GDScriptTypeKind.Native } => NamedInteger(member, held),
            _ => null,
        };
    }

    /// <summary>A class's own enum of a name, as a type.</summary>
    private GDScriptType? OwnEnum(GDScriptType at, string name) =>
        OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) named) && named.Node is GDScriptEnum { Identifier: not null } enumerated ? EnumType(enumerated, at, meta: true) : null;
}
