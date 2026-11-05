namespace Passenger.Workspace.GDScript;

/// <summary>What a GDScript's own text says a bare name is where it is written, read from its syntax alone (no project, no
/// engine API): the innermost declaration in scope, and whether that holds an input event. Pure.</summary>
public static class GDScriptScopes
{
    /// <summary>The engine's callbacks whose first parameter Godot gives an InputEvent.</summary>
    private static readonly HashSet<string> InputCallbacks = new(StringComparer.Ordinal) { "_input", "_unhandled_input", "_unhandled_key_input", "_shortcut_input", "_gui_input" };

    /// <summary>A declaration in scope, and the function it is a parameter of; null for any other.</summary>
    private readonly record struct Scoped(GDScriptNode Node, GDScriptFunction? Of);

    /// <summary>The innermost declaration of a name in scope at an offset of a script: a function's or lambda's parameter, a
    /// local of a block around it declared before it, a loop's variable, a match pattern's binding, an inner class's member,
    /// the script's own; null for none, which leaves a name of the classes it extends or the project's.</summary>
    public static GDScriptNode? DeclaredAt(GDScriptTree tree, string name, int offset) => ScopedAt(tree, name, offset)?.Node;

    /// <summary>Whether a name at an offset is written to hold an InputEvent: typed or cast as one of the engine's, made as one,
    /// or the event an input callback is given.</summary>
    public static bool IsInputEvent(GDScriptTree tree, string name, int offset) => ScopedAt(tree, name, offset) is { } scoped && scoped.Node switch
    {
        GDScriptAssignable { Type: { Chain: [.., { } type] } } => IsEvent(type.Name),
        GDScriptAssignable { Initializer: GDScriptCast { Type.Chain: [.., { } cast] } } => IsEvent(cast.Name),
        GDScriptAssignable { Initializer: GDScriptCall { Callee: GDScriptSubscript { Base: GDScriptIdentifier made, Attribute.Name: "new" } } } => IsEvent(made.Name),
        GDScriptParameter parameter => scoped.Of is { Identifier: { } function, Parameters: [{ } first, ..] } && first == parameter && InputCallbacks.Contains(function.Name),
        _ => false,
    };

    private static bool IsEvent(string type) => type.StartsWith("InputEvent", StringComparison.Ordinal);

    private static Scoped? ScopedAt(GDScriptTree tree, string name, int offset)
    {
        foreach (GDScriptMember member in tree.Members)
        {
            if (member.Start <= offset && offset <= member.End && Within(member.Node, name, offset - member.Offset) is { } inside)
            {
                return inside;
            }
        }
        return tree.Members.FirstOrDefault(m => Declares(m.Node, name)) is { Node: { } declared } ? new Scoped(declared, null) : null;
    }

    private static Scoped? Within(GDScriptNode node, string name, int at)
    {
        foreach (GDScriptNode child in node.Children)
        {
            if (child.Start >= 0 && child.Start <= at && at <= child.End && Within(child, name, at) is { } deeper)
            {
                return deeper;
            }
        }
        return node switch
        {
            GDScriptFunction function when function.Parameters.Concat(GDScriptNode.Of(function.Rest).Cast<GDScriptParameter>()).FirstOrDefault(p => p.Identifier?.Name == name) is { } parameter =>
                new Scoped(parameter, function),
            GDScriptSuite suite when suite.Statements.Where(s => s.Start <= at).OfType<GDScriptAssignable>().LastOrDefault(s => s.Identifier?.Name == name) is { } local => new Scoped(local, null),
            GDScriptFor loop when loop.Variable?.Name == name && loop.Loop is { } body && body.Start <= at => new Scoped(loop, null),
            GDScriptMatchBranch branch when branch.Patterns.Any(p => Binds(p, name)) => new Scoped(branch, null),
            GDScriptClass inner when inner.Members.FirstOrDefault(m => Declares(m, name)) is { } member => new Scoped(member, null),
            _ => null,
        };
    }

    private static bool Binds(GDScriptPattern pattern, string name) =>
        pattern.Bind?.Name == name || pattern.Array.Any(p => Binds(p, name)) || pattern.Dictionary.Any(p => p.Value is { } value && Binds(value, name));

    /// <summary>Whether a member of a class declares a name, an unnamed enum's values among its own.</summary>
    private static bool Declares(GDScriptNode member, string name) => member switch
    {
        GDScriptFunction function => function.Identifier?.Name == name,
        GDScriptAssignable assignable => assignable.Identifier?.Name == name,
        GDScriptSignal signal => signal.Identifier?.Name == name,
        GDScriptEnum { Identifier: { } named } => named.Name == name,
        GDScriptEnum values => values.Values.Any(v => v.Name.Name == name),
        GDScriptClass inner => inner.Identifier?.Name == name,
        _ => false,
    };
}
