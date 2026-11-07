using System.Runtime.CompilerServices;
using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of what must be constant and of types it can't infer (resolve_assignable,
/// resolve_annotation, an enum's values in resolve_class_member, reduce_dictionary, resolve_match_pattern), each said where
/// the class is read whole and Passenger is sure of what Godot holds constant or weak.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>Whether Godot holds an expression as no constant, as far as Passenger can be sure: a variable or parameter,
    /// self, a node path, a call of the script's own function, or what is made of one, an await of a constant being that
    /// constant; false where it may be one.</summary>
    private bool NonConstant(GDScriptExpression? expression, Context context) => RuntimeHelpers.TryEnsureSufficientExecutionStack() && expression switch
    {
        null or GDScriptLiteral => false,
        GDScriptSelf or GDScriptGetNode => true,
        GDScriptAwait waiting => NonConstant(waiting.Awaited, context),
        GDScriptIdentifier name => context.Scope?.Find(name.Name) is { } local ? local.Local?.Kind != LocalKind.Constant
            : Member(context.Class, name.Name) is { Kind: "var" or "signal" },
        GDScriptCall { Callee: GDScriptIdentifier own } when Declared(own, context) => true,
        GDScriptCall call => call.Arguments.Any(a => NonConstant(a, context)) || call.Callee is GDScriptSubscript { IsAttribute: true, Base: { } called } && NonConstant(called, context),
        GDScriptSubscript { IsAttribute: true, Base: { } taken } => NonConstant(taken, context),
        GDScriptSubscript subscript => NonConstant(subscript.Base, context) || NonConstant(subscript.Index, context),
        GDScriptBinary binary => NonConstant(binary.Left, context) || NonConstant(binary.Right, context),
        GDScriptUnary unary => NonConstant(unary.Operand, context),
        GDScriptTernary ternary => NonConstant(ternary.Condition, context) || NonConstant(ternary.True, context) || NonConstant(ternary.False, context),
        GDScriptCast cast => NonConstant(cast.Operand, context),
        GDScriptArray array => array.Elements.Any(e => NonConstant(e, context)),
        GDScriptDictionary dictionary => dictionary.Elements.Any(e => !dictionary.LuaTable && NonConstant(e.Key, context) || NonConstant(e.Value, context)),
        _ => false,
    };

    /// <summary>A constant's value that is no constant expression, in Godot's words.</summary>
    private void ConstantValueErrors(GDScriptAssignable node, string name, GDScriptExpression given, Context context)
    {
        if (node is GDScriptConstant && NonConstant(given, context))
        {
            Error(context, given, $"Assigned value for constant \"{name}\" isn't a constant expression.");
        }
    }

    /// <summary>Whether an expression's type is one Godot holds weak, which <c>:=</c> can't infer from: a variable or
    /// parameter of no type written nor inferred, a signal awaited, or a ternary with one of those.</summary>
    private bool SurelyWeak(GDScriptExpression expression, Context context) => RuntimeHelpers.TryEnsureSufficientExecutionStack() && expression switch
    {
        GDScriptIdentifier name when context.Scope?.Find(name.Name) is { } local => local.Local?.Node is GDScriptAssignable { Type: null, Infers: false } and not GDScriptConstant,
        GDScriptIdentifier name => Member(context.Class, name.Name) is { Kind: "var", Declaration: { Script: { } script, Start: var start } } && Tree(script) is { } tree
            && AssignableAt(tree.Members.Select(m => (m.Node, m.Offset)), start) is GDScriptVariable { Type: null, Infers: false },
        GDScriptAwait { Awaited: GDScriptIdentifier signal } => context.Scope?.Find(signal.Name) is null && Member(context.Class, signal.Name) is { Kind: "signal" },
        GDScriptTernary ternary => ternary.True is { } yes && SurelyWeak(yes, context) || ternary.False is { } no && SurelyWeak(no, context),
        _ => false,
    };

    /// <summary>An annotation's arguments that are no constant expression, each by its place among them.</summary>
    private void AnnotationArgumentErrors(GDScriptNode annotated, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        foreach (GDScriptAnnotation annotation in annotated.Annotations)
        {
            for (int i = 0; i < annotation.Arguments.Count; i++)
            {
                if (NonConstant(annotation.Arguments[i], context))
                {
                    Error(context, annotation.Arguments[i], $"Argument {i + 1} of annotation \"{annotation.Name}\" isn't a constant expression.");
                }
            }
        }
    }

    /// <summary>An enum's values written that are no constant, or a constant of no integer.</summary>
    private void EnumValueErrors(GDScriptEnum enumerated, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        foreach ((_, GDScriptExpression? value) in enumerated.Values)
        {
            if (NonConstant(value, context))
            {
                Error(context, value!, "Enum values must be constant.");
            }
            else if (value is GDScriptLiteral { Kind: not GDScriptLiteralKind.Int } or GDScriptUnary { Operator: K.Minus, Operand: GDScriptLiteral { Kind: GDScriptLiteralKind.Float } })
            {
                Error(context, value, "Enum values must be integers.");
            }
        }
    }

    /// <summary>A dictionary's key written a second time (a String and a StringName of one text alike), said with the line
    /// of the first's value as Godot says it.</summary>
    private void DuplicateKeyErrors(GDScriptDictionary dictionary, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        Dictionary<string, GDScriptExpression> seen = new(StringComparer.Ordinal);
        foreach ((GDScriptExpression key, GDScriptExpression value) in dictionary.Elements)
        {
            (string Kind, string Shown)? named = key switch
            {
                GDScriptIdentifier lua when dictionary.LuaTable => ("s", lua.Name),
                GDScriptLiteral { Value: string text } => ("s", text),
                GDScriptLiteral { Kind: GDScriptLiteralKind.Int, Value: long number } => ("i", number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                GDScriptLiteral { Kind: GDScriptLiteralKind.Bool, Value: bool truth } => ("b", truth ? "true" : "false"),
                _ => null,
            };
            if (named is not ({ } kind, { } shown))
            {
                continue;
            }
            if (seen.TryGetValue($"{kind}:{shown}", out GDScriptExpression? first))
            {
                Error(context, key, $"Key \"{shown}\" was already used in this dictionary (at line {LineOf(context.Analysis.Path, context.Offset + first.Start)}).");
            }
            else
            {
                seen[$"{kind}:{shown}"] = value;
            }
        }
    }

    /// <summary>A match pattern's expression that is neither a constant, a name, nor an attribute of one.</summary>
    private void PatternErrors(GDScriptExpression expression, Context context)
    {
        if (context.Analysis.Tree is not null && Knows(context.Class) && expression is GDScriptBinary or GDScriptUnary or GDScriptTernary or GDScriptCast or GDScriptCall
            && NonConstant(expression, context))
        {
            Error(context, expression, "Expression in match pattern must be a constant expression, an identifier, or an attribute access (\"A.B\").");
        }
    }
}
