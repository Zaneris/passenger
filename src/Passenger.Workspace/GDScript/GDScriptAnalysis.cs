namespace Passenger.Workspace.GDScript;

/// <summary>A type inferred where nothing is written: after a <c>:=</c> variable's or an untyped parameter's name, the
/// offset it goes at and the type.</summary>
public readonly record struct GDScriptInferred(int Offset, GDScriptType Type);

/// <summary>One script's analysis (<see cref="GDScriptAnalyzer.Analyze"/>): each expression's and declaration's type,
/// each name's declaration, and the places it answers for (a name at an offset, the types inferred where none is
/// written). Immutable once made.</summary>
public sealed class GDScriptAnalysis
{
    internal GDScriptAnalysis(GDScriptTree? tree, string path)
    {
        Tree = tree;
        Path = path;
    }

    public GDScriptTree? Tree { get; }

    /// <summary>The script's res:// path.</summary>
    public string Path { get; }

    internal Dictionary<GDScriptNode, GDScriptType> Types { get; } = [];

    internal Dictionary<GDScriptIdentifier, GDScriptDeclaration> Declared { get; } = [];

    internal Dictionary<GDScriptNode, int> Offsets { get; } = [];

    /// <summary>A node's type: an expression's, or a declaration's (a variable's, constant's, parameter's); Variant where none was read.</summary>
    public GDScriptType TypeOf(GDScriptNode node) => Types.GetValueOrDefault(node) ?? GDScriptType.Variant.Soft;

    /// <summary>What a name refers to, where it was resolved.</summary>
    public GDScriptDeclaration? DeclarationOf(GDScriptIdentifier name) => Declared.GetValueOrDefault(name);

    /// <summary>The innermost node at an offset of the text, with the offset its positions count from; null outside every member.</summary>
    public (GDScriptNode Node, int Offset)? NodeAt(int offset)
    {
        if (Tree is null)
        {
            return null;
        }
        foreach (GDScriptMember member in Tree.Members)
        {
            if (offset >= member.Start && offset <= member.End)
            {
                GDScriptNode at = member.Node;
                int relative = offset - member.Offset;
                for (bool deeper = true; deeper;)
                {
                    deeper = false;
                    foreach (GDScriptNode child in at.Children)
                    {
                        if (child.Start >= 0 && relative >= child.Start && relative <= child.End && child is not GDScriptSuite { Statements.Count: 0 })
                        {
                            at = child;
                            deeper = true;
                            break;
                        }
                    }
                }
                return (at, member.Offset);
            }
        }
        return null;
    }

    /// <summary>The locals and parameters of the member an offset is in, declared before it, the nearest last.</summary>
    public IReadOnlyList<GDScriptMemberInfo> LocalsAt(int offset)
    {
        if (Tree?.Members.FirstOrDefault(m => offset >= m.Start && offset <= m.End) is not { Node: { } member } within)
        {
            return [];
        }
        List<(int Start, GDScriptMemberInfo Local)> locals = [];
        foreach ((GDScriptNode node, int at) in Offsets)
        {
            if (at == within.Offset && node != member && node is GDScriptAssignable { Identifier: { } name } && at + name.Start >= within.Start && at + name.Start < offset)
            {
                GDScriptType type = TypeOf(node);
                locals.Add((at + name.Start, new GDScriptMemberInfo(name.Name, node is GDScriptConstant ? "const" : "var", type, new GDScriptDeclaration(Path, at + name.Start, name.Name, type))));
            }
        }
        return [.. locals.OrderByDescending(l => l.Start).Select(l => l.Local)];
    }

    /// <summary>The types inferred where nothing is written: each <c>:=</c> variable's and constant's, and each untyped
    /// parameter's with a default value, after its name.</summary>
    public IReadOnlyList<GDScriptInferred> Inferred()
    {
        List<GDScriptInferred> inferred = [];
        foreach ((GDScriptNode node, int offset) in Offsets)
        {
            if (node is GDScriptAssignable { Identifier: { } name, Type: null, Initializer: not null } assignable
                && (assignable.Infers || node is GDScriptParameter) && TypeOf(node) is { IsVariant: false } type)
            {
                inferred.Add(new GDScriptInferred(offset + name.End, type));
            }
        }
        inferred.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return inferred;
    }

    /// <summary>How Godot's server writes a member or local in its symbols (its detail): <c>var speed: float</c>,
    /// <c>func hit(amount: int) -> void</c>, <c>const LIMIT: int</c>, a type shown only where it is hard, values left out.</summary>
    public string? Detail(GDScriptNode node)
    {
        static string Typed(GDScriptType type) => type.Hard && !type.IsVariant ? $": {type}" : type.Hard && type.IsVariant ? ": Variant" : string.Empty;
        switch (node)
        {
            case GDScriptVariable { Identifier: { } name } variable:
                return $"{(variable.Annotations.Any(a => a.Name.StartsWith("@export", StringComparison.Ordinal)) ? "@export " : string.Empty)}var {name.Name}{Typed(TypeOf(node))}";
            case GDScriptConstant { Identifier: { } name }:
                return $"const {name.Name}{Typed(TypeOf(node))}";
            case GDScriptFunction { Identifier: { } name } function:
                string parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Identifier?.Name}{Typed(TypeOf(p))}"));
                if (function.Rest is { Identifier: { } rest } restParameter)
                {
                    parameters += (parameters.Length > 0 ? ", " : string.Empty) + $"...{rest.Name}: {TypeOf(restParameter)}";
                }
                GDScriptType returned = ReturnOf(function);
                string returns = returned.IsVoid ? " -> void" : returned.Hard && !returned.IsVariant || function.ReturnType is not null ? $" -> {returned}" : string.Empty;
                return $"func {name.Name}({parameters}){returns}";
            default:
                return null;
        }
    }

    /// <summary>A function's declared return type as read in its class; Variant where none is written.</summary>
    internal GDScriptType ReturnOf(GDScriptFunction function) => Types.GetValueOrDefault(function) ?? GDScriptType.Variant;
}
