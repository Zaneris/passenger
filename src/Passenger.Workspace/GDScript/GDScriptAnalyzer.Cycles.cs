using System.Runtime.CompilerServices;

namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of a class's members that reach themselves as Godot resolves them (resolve_class_member,
/// in its members' order, each member's value, enum values and parameters' defaults read as it is resolved): the reference
/// that closes a cycle ("Could not resolve member "X": Cyclic reference."), with what then can't be typed or folded, and an
/// enum's value naming one of its own enum's not yet given. Each said where the class is read whole.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>A member as its resolution reads it: its class, its name, the expressions read as it is resolved, each with
    /// the declaration it gives a value to, the unnamed or named enum it is of, the function it is (whose override is read
    /// last), and the context its places count in.</summary>
    private sealed record Resolved(GDScriptClass Class, string Name, IReadOnlyList<(GDScriptExpression Value, GDScriptNode Owner)> Values, GDScriptEnum? Enum,
        GDScriptFunction? Function, Context At);

    /// <summary>The cycles of a script's classes' members (the script's own first, then each inner class's, as Godot resolves
    /// them), and its enums' values that name one of their enum's not yet given. Another script's members are resolved
    /// already where Godot's editor reads this one, so a cycle through them says nothing here.</summary>
    private void CycleErrors(GDScriptTree tree, string path, GDScriptType self, GDScriptAnalysis analysis)
    {
        List<(GDScriptClass Class, IEnumerable<(GDScriptNode Node, int Offset)> Members, Context At)> classes =
            [(tree.Class, tree.Members.Select(m => (m.Node, m.Offset)), new Context(analysis, self, 0, null, null))];
        foreach (GDScriptType inner in InnerClasses(tree.Members.Select(m => (m.Node, m.Offset)), path))
        {
            classes.Add((inner.Class!, inner.Class!.Members.Select(m => (m, inner.Offset)), new Context(analysis, inner, inner.Offset, null, null)));
        }
        Dictionary<string, GDScriptClass> named = new(StringComparer.Ordinal);
        foreach ((GDScriptClass node, _, _) in classes.Skip(1))
        {
            named.TryAdd(node.Identifier!.Name, node);
        }
        List<Resolved> order = [];
        foreach ((GDScriptClass node, IEnumerable<(GDScriptNode Node, int Offset)> members, Context at) in classes)
        {
            if (!Knows(at.Class))
            {
                continue;
            }
            foreach ((GDScriptNode member, int offset) in members)
            {
                Context own = at with { Offset = offset };
                switch (member)
                {
                    case GDScriptVariable { Identifier: { } name, Initializer: { } value } variable:
                        order.Add(new Resolved(node, name.Name, [(value, variable)], null, null, own));
                        break;
                    case GDScriptConstant { Identifier: { } name, Initializer: { } value } constant:
                        order.Add(new Resolved(node, name.Name, [(value, constant)], null, null, own));
                        break;
                    case GDScriptFunction { Identifier: { } name } function:
                        order.Add(new Resolved(node, name.Name, [.. function.Parameters.Where(p => p.Initializer is not null).Select(p => (p.Initializer!, (GDScriptNode)p))], null, function, own));
                        break;
                    case GDScriptEnum { Identifier: { } name } enumerated:
                        EarlyEnumValues(enumerated, own);
                        order.Add(new Resolved(node, name.Name, [.. enumerated.Values.Where(v => v.Value is not null).Select(v => (v.Value!, (GDScriptNode)enumerated))], enumerated, null, own));
                        break;
                    case GDScriptEnum unnamed:
                        EarlyEnumValues(unnamed, own);
                        order.AddRange(unnamed.Values.Select(v => new Resolved(node, v.Name.Name, v.Value is { } given ? [(given, unnamed)] : [], unnamed, null, own)));
                        break;
                    default:
                        break;
                }
            }
        }
        Dictionary<(GDScriptClass, string), Resolved> byName = [];
        foreach (Resolved member in order)
        {
            byName.TryAdd((member.Class, member.Name), member);
        }
        Dictionary<Resolved, bool> state = [];
        HashSet<Resolved> variant = [];
        foreach (Resolved member in order)
        {
            Resolve(member);
        }

        // A member past the stack the walk has left is resolved in its turn from the top of it, by the walk above.
        void Resolve(Resolved member)
        {
            if (state.ContainsKey(member) || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                return;
            }
            state[member] = false;
            // A parameter's default names the function's parameters, never members of their names.
            HashSet<string> parameters = member.Function is { } declared ? [.. declared.Parameters.Select(p => p.Identifier?.Name).OfType<string>()] : [];
            foreach ((GDScriptExpression value, GDScriptNode owner) in member.Values)
            {
                foreach ((GDScriptIdentifier name, bool attribute, string? within) in Reads(value, parameters, member.Enum))
                {
                    GDScriptClass target = within is null ? member.Class : named.GetValueOrDefault(within) ?? member.Class;
                    if (!(within is null || named.ContainsKey(within)) || !byName.TryGetValue((target, name.Name), out Resolved? reached))
                    {
                        continue;
                    }
                    if (Reached(reached, name))
                    {
                        if (Unresolved(value, owner, name, attribute, member.At))
                        {
                            variant.Add(member);
                        }
                    }
                    else if (variant.Contains(reached) && value == name && owner is GDScriptAssignable { Infers: true } inferred)
                    {
                        // What a cycle left a hard Variant is what := infers from.
                        Warn(member.At, inferred, "INFERENCE_ON_VARIANT", KindWord(inferred));
                    }
                }
            }
            // An override reads the function it overrides in the class it extends, last.
            if (member.Function is { } function && member.Class.Extends is [{ } based, ..] && named.TryGetValue(based.Name, out GDScriptClass? parent)
                && byName.TryGetValue((parent, member.Name), out Resolved? overridden) && overridden.Function is not null)
            {
                Reached(overridden, function);
            }
            state[member] = true;

            // Whether a read of a member closes a cycle, said at the node it was read from; else the member is resolved.
            bool Reached(Resolved reached, GDScriptNode from)
            {
                if (state.TryGetValue(reached, out bool done) && !done)
                {
                    Error(member.At, from, $"Could not resolve member \"{reached.Name}\": Cyclic reference.");
                    return true;
                }
                Resolve(reached);
                return false;
            }
        }
    }

    /// <summary>The names an expression reads as Godot reduces it, each with whether it is an attribute's base (an enum's
    /// name before its value) and, for a function called on a class (<c>B.f()</c>, <c>B.new().f()</c>), that class's name:
    /// none inside a lambda's body, nor one its parameters or an enum's own values take.</summary>
    private static IEnumerable<(GDScriptIdentifier Name, bool Attribute, string? Within)> Reads(GDScriptNode node, HashSet<string> hidden, GDScriptEnum? within)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            yield break;
        }
        switch (node)
        {
            case GDScriptIdentifier name when !hidden.Contains(name.Name) && within?.Values.Any(v => v.Name.Name == name.Name) != true:
                yield return (name, false, null);
                yield break;
            case GDScriptCall { Callee: GDScriptSubscript { IsAttribute: true, Attribute: { } called, Base: var on } } call
                when on is GDScriptIdentifier or GDScriptCall { Callee: GDScriptSubscript { IsAttribute: true, Attribute.Name: "new", Base: GDScriptIdentifier } }:
                GDScriptIdentifier owner = on as GDScriptIdentifier ?? (GDScriptIdentifier)((GDScriptSubscript)((GDScriptCall)on).Callee!).Base!;
                if (!hidden.Contains(owner.Name))
                {
                    yield return (owner, true, null);
                    yield return (called, false, owner.Name);
                }
                foreach (GDScriptExpression argument in call.Arguments)
                {
                    foreach ((GDScriptIdentifier, bool, string?) read in Reads(argument, hidden, within))
                    {
                        yield return read;
                    }
                }
                yield break;
            case GDScriptSubscript { IsAttribute: true, Base: GDScriptIdentifier name } when !hidden.Contains(name.Name):
                yield return (name, true, null);
                yield break;
            case GDScriptSubscript { IsAttribute: true }:
                yield break;
            case GDScriptLambda { Function: { } function }:
                HashSet<string> inner = [.. hidden, .. function.Parameters.Select(p => p.Identifier?.Name).OfType<string>()];
                foreach (GDScriptParameter parameter in function.Parameters.Where(p => p.Initializer is not null))
                {
                    foreach ((GDScriptIdentifier, bool, string?) read in Reads(parameter.Initializer!, inner, null))
                    {
                        yield return read;
                    }
                }
                yield break;
            default:
                foreach (GDScriptNode child in node.Children)
                {
                    foreach ((GDScriptIdentifier, bool, string?) read in Reads(child, hidden, within))
                    {
                        yield return read;
                    }
                }
                yield break;
        }
    }

    /// <summary>What Godot says of a value whose read closed a cycle, where the read is all the value is (a call's name
    /// with it): a type it can't resolve or infer, or an enum's value no constant; whether the declaration is then a
    /// hard Variant (inferred, or a constant's).</summary>
    private static bool Unresolved(GDScriptExpression value, GDScriptNode owner, GDScriptIdentifier read, bool attribute, Context context)
    {
        bool whole = value == read || value is GDScriptCall { Callee: GDScriptIdentifier called } && called == read
            || attribute && value is GDScriptSubscript { IsAttribute: true, Base: GDScriptIdentifier taken } && taken == read;
        if (!whole)
        {
            return false;
        }
        switch (owner)
        {
            case GDScriptEnum when attribute:
                Error(context, value, "Enum values must be constant.");
                return false;
            case GDScriptAssignable { Identifier: { } name, Infers: true } inferred:
                Error(context, value, $"Cannot infer the type of \"{name.Name}\" {KindWord(inferred)} because the value doesn't have a set type.");
                return true;
            case GDScriptAssignable { Identifier: { } name, Type: null } untyped:
                Error(context, value, $"Could not resolve type for {KindWord(untyped)} \"{name.Name}\".");
                return untyped is GDScriptConstant;
            default:
                return false;
        }
    }

    /// <summary>Godot's word for a declaration's kind in its messages.</summary>
    private static string KindWord(GDScriptAssignable declared) => declared is GDScriptParameter ? "parameter" : declared is GDScriptConstant ? "constant" : "variable";

    /// <summary>An enum's values that name one of its own not yet given (itself, or one after it), which Godot reads as no
    /// constant.</summary>
    private static void EarlyEnumValues(GDScriptEnum enumerated, Context context)
    {
        for (int i = 0; i < enumerated.Values.Count; i++)
        {
            if (enumerated.Values[i].Value is not GDScriptIdentifier named)
            {
                continue;
            }
            int at = enumerated.Values.FindIndex(v => v.Name.Name == named.Name);
            if (at >= i)
            {
                Error(context, named, "Cannot use another enum element before it was declared.");
                Error(context, named, "Enum values must be constant.");
            }
        }
    }
}
