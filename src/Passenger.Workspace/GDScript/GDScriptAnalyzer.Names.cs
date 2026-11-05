namespace Passenger.Workspace.GDScript;

/// <summary>The warnings Godot's parser and analyzer give a script's names and annotations as a whole: an identifier it
/// declares or names written with characters that may be taken for others (CONFUSABLE_IDENTIFIER, <see cref="IdentifierSpoofing"/>),
/// and <c>@static_unload</c> on a script of no static data (REDUNDANT_STATIC_UNLOAD).</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>CONFUSABLE_IDENTIFIER on each name Godot's parser reads with parse_identifier: the names a script declares
    /// (classes, members, parameters, enum values, loop variables, match binds), those its <c>extends</c>, types, attributes
    /// and super calls name, and each node name of a <c>$</c> path written unquoted; never a name read in an expression.</summary>
    private static void ConfusableWarnings(GDScriptTree tree, GDScriptAnalysis analysis, GDScriptType self)
    {
        Context own = new(analysis, self, 0, null, null);
        Named(tree.Class, own);
        foreach (GDScriptMember member in tree.Members)
        {
            Named(member.Node, own with { Offset = member.Offset });
        }

        static void Named(GDScriptNode node, Context context)
        {
            foreach (GDScriptIdentifier name in Declared(node))
            {
                if (IdentifierSpoofing.Spoofs(name.Name))
                {
                    Warn(context, name, "CONFUSABLE_IDENTIFIER", name.Name);
                }
            }
            if (node is GDScriptGetNode get && context.Analysis.Tree is { } written && context.Offset + get.Start + 1 is int after && after < written.Text.Length
                && written.Text[after] is not ('"' or '\''))
            {
                foreach (string part in get.Path.Split('/', '%').Where(p => p.Length > 0 && IdentifierSpoofing.Spoofs(p)))
                {
                    Warn(context, get, "CONFUSABLE_IDENTIFIER", part);
                }
            }
            foreach (GDScriptNode child in node is GDScriptClass ? [] : node.Children)
            {
                Named(child, context);
            }
        }
    }

    /// <summary>The names a node declares or names as Godot's parse_identifier reads them.</summary>
    private static IEnumerable<GDScriptIdentifier> Declared(GDScriptNode node) => node switch
    {
        GDScriptClass named => [.. Present(named.Identifier), .. named.Extends],
        GDScriptAssignable assignable => Present(assignable.Identifier),
        GDScriptFunction function => Present(function.Identifier),
        GDScriptSignal signal => Present(signal.Identifier),
        GDScriptEnum enumerated => [.. Present(enumerated.Identifier), .. enumerated.Values.Select(v => v.Name)],
        GDScriptFor loop => Present(loop.Variable),
        GDScriptPattern pattern => Present(pattern.Bind),
        GDScriptTypeNode type => type.Chain,
        GDScriptSubscript { IsAttribute: true } attribute => Present(attribute.Attribute),
        GDScriptCall { IsSuper: true, Callee: GDScriptIdentifier called } => [called],
        _ => [],
    };

    private static IEnumerable<GDScriptIdentifier> Present(GDScriptIdentifier? identifier) => identifier is null ? [] : [identifier];

    /// <summary>REDUNDANT_STATIC_UNLOAD where a script is annotated <c>@static_unload</c> and none of its classes has a static
    /// variable or a static constructor, on the script's class's line as Godot gives it (its class_name's or extends', else
    /// the first).</summary>
    private static void StaticUnloadWarning(GDScriptTree tree, GDScriptAnalysis analysis, GDScriptType self)
    {
        if (tree.Class.Annotations.Any(a => a.Name == "@static_unload") && !HoldsStatics(tree.Members.Select(m => m.Node)))
        {
            int start = tree.Class.Start;
            int end = tree.Text.IndexOf('\n', start) is int line and >= 0 ? line : tree.Text.Length;
            Warn(new Context(analysis, self, 0, null, null), start, end, "REDUNDANT_STATIC_UNLOAD");
        }

        static bool HoldsStatics(IEnumerable<GDScriptNode> members) => members.Any(m => m switch
        {
            GDScriptVariable { IsStatic: true } => true,
            GDScriptFunction { Identifier.Name: "_static_init" } => true,
            GDScriptClass inner => HoldsStatics(inner.Members),
            _ => false,
        });
    }
}
