using Passenger.Workspace.GDScript;

namespace Passenger.Workspace;

/// <summary>What a member of a GDScript's class is.</summary>
public enum GodotScriptKind
{
    Function,
    Signal,
    Variable,
    Constant,
}

/// <summary>A member a GDScript declares outside its inner classes: what it is, its name, and where its name starts in the
/// file's text.</summary>
public sealed record GodotScriptDeclared(GodotScriptKind Kind, string Name, int Start)
{
    /// <summary>A function's or signal's parameters as written (<c>amount: int</c>); null where its line does not close them,
    /// or for a signal written without them or that the script's own emits give other arguments than they say (Godot hands
    /// an emit's arguments on as they are), so they are not known.</summary>
    public IReadOnlyList<string>? Parameters { get; init; }

    /// <summary>How many of its parameters a call must give: those without a default.</summary>
    public int Required => Parameters?.Count(p => !p.Contains('=', StringComparison.Ordinal)) ?? 0;
}

/// <summary>A GDScript file's class as Godot's parser reads it (<see cref="GDScriptTree"/>): what it extends as written (a
/// quoted path, or a class by its name), its <c>class_name</c>, and its functions (<c>static</c> ones too), signals,
/// variables (not <c>static</c> ones) and constants (a named <c>enum</c>, and each value of an unnamed one, among them);
/// an inner class's are its own. Pure.</summary>
public sealed class GodotScriptDeclarations
{
    private GodotScriptDeclarations(string? extends, bool extendsPath, string? className, IReadOnlyList<GodotScriptDeclared> members)
    {
        Extends = extends;
        ExtendsPath = extendsPath;
        ClassName = className;
        Members = members;
    }

    /// <summary>What its <c>extends</c> names: a path (<see cref="ExtendsPath"/>, unquoted), else a class by its name; null where
    /// it extends nothing written, so RefCounted.</summary>
    public string? Extends { get; }

    public bool ExtendsPath { get; }

    /// <summary>The name <c>class_name</c> gives its class across the project; null for none.</summary>
    public string? ClassName { get; }

    /// <summary>Its members in the order written.</summary>
    public IReadOnlyList<GodotScriptDeclared> Members { get; }

    /// <summary>Whether a file is a GDScript.</summary>
    public static bool IsScript(string path) => path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase);

    /// <summary>The member of a kind it declares by a name; null for none.</summary>
    public GodotScriptDeclared? Member(GodotScriptKind kind, string name) => Members.FirstOrDefault(m => m.Kind == kind && m.Name == name);

    public static GodotScriptDeclarations Read(string text) => Read(GDScriptTree.Parse(text));

    /// <summary>The declarations of a parsed script: its members outside inner classes but static variables, a named enum
    /// a constant and an unnamed one's values each one.</summary>
    public static GodotScriptDeclarations Read(GDScriptTree tree)
    {
        string text = tree.Text;
        List<GodotScriptDeclared> members = [];
        foreach (GDScriptMember member in tree.Members)
        {
            switch (member.Node)
            {
                case GDScriptFunction { Identifier: { } name } function:
                    members.Add(new GodotScriptDeclared(GodotScriptKind.Function, name.Name, member.Offset + name.Start)
                    {
                        Parameters = Written(text, member.Offset, name, function.ParametersEnd, function.Rest is { } rest ? [.. function.Parameters, rest] : function.Parameters),
                    });
                    break;
                case GDScriptSignal { Identifier: { } name } signal:
                    members.Add(new GodotScriptDeclared(GodotScriptKind.Signal, name.Name, member.Offset + name.Start)
                    {
                        Parameters = signal.HasParentheses ? Written(text, member.Offset, name, signal.ParametersEnd, signal.Parameters) : null,
                    });
                    break;
                case GDScriptVariable { Identifier: { } name, IsStatic: false }:
                    members.Add(new GodotScriptDeclared(GodotScriptKind.Variable, name.Name, member.Offset + name.Start));
                    break;
                case GDScriptConstant { Identifier: { } name }:
                    members.Add(new GodotScriptDeclared(GodotScriptKind.Constant, name.Name, member.Offset + name.Start));
                    break;
                case GDScriptEnum { Identifier: { } name }:
                    members.Add(new GodotScriptDeclared(GodotScriptKind.Constant, name.Name, member.Offset + name.Start));
                    break;
                case GDScriptEnum unnamed:
                    members.AddRange(unnamed.Values.Select(v => new GodotScriptDeclared(GodotScriptKind.Constant, v.Name.Name, member.Offset + v.Name.Start)));
                    break;
                default:
                    break;
            }
        }
        Dictionary<string, HashSet<int>> emitted = Emitted(tree);
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is { Kind: GodotScriptKind.Signal, Parameters: { } declared } signal && emitted.TryGetValue(signal.Name, out HashSet<int>? given)
                && given.Any(count => count != declared.Count))
            {
                members[i] = signal with { Parameters = null };
            }
        }
        GDScriptClass head = tree.Class;
        string? extends = head.ExtendsPath ?? (head.Extends.Count > 0 ? string.Join('.', head.Extends.Select(e => e.Name)) : null);
        return new GodotScriptDeclarations(extends, head.ExtendsPath is not null, head.Identifier?.Name, members);
    }

    /// <summary>Parameters as written, each its own text (a rest parameter's with its <c>...</c>); null where its parentheses
    /// close on a later line than its name.</summary>
    private static IReadOnlyList<string>? Written(string text, int offset, GDScriptIdentifier name, int parametersEnd, IReadOnlyList<GDScriptParameter> parameters) =>
        parametersEnd < 0 || text.AsSpan(offset + name.End, parametersEnd - name.End).Contains('\n') ? null
            : [.. parameters.Select(p => text[Dotted(text, offset + p.Start)..(offset + p.End)])];

    /// <summary>Where a parameter's text begins: at the <c>...</c> before its name where it is the rest parameter.</summary>
    private static int Dotted(string text, int start)
    {
        int at = start;
        while (at > 0 && text[at - 1] is ' ' or '\t')
        {
            at--;
        }
        return at >= 3 && text.AsSpan(at - 3, 3).SequenceEqual("...") ? at - 3 : start;
    }

    /// <summary>How many arguments the script's own emits of each signal give, by its name: <c>name.emit(…)</c>,
    /// <c>self.name.emit(…)</c> and <c>emit_signal("name", …)</c>.</summary>
    private static Dictionary<string, HashSet<int>> Emitted(GDScriptTree tree)
    {
        Dictionary<string, HashSet<int>> emitted = new(StringComparer.Ordinal);
        Stack<GDScriptNode> pending = new(tree.Members.Select(m => m.Node));
        while (pending.TryPop(out GDScriptNode? node))
        {
            if (node is GDScriptCall call && Emit(call) is ({ } signal, int count))
            {
                (emitted.TryGetValue(signal, out HashSet<int>? counts) ? counts : emitted[signal] = []).Add(count);
            }
            foreach (GDScriptNode child in node.Children)
            {
                pending.Push(child);
            }
        }
        return emitted;

        static (string? Signal, int Count) Emit(GDScriptCall call) => call.Callee switch
        {
            GDScriptSubscript { IsAttribute: true, Attribute.Name: "emit", Base: GDScriptIdentifier named } => (named.Name, call.Arguments.Count),
            GDScriptSubscript { IsAttribute: true, Attribute.Name: "emit", Base: GDScriptSubscript { IsAttribute: true, Base: GDScriptSelf, Attribute: { } named } }
                => (named.Name, call.Arguments.Count),
            GDScriptIdentifier { Name: "emit_signal" } or GDScriptSubscript { IsAttribute: true, Attribute.Name: "emit_signal", Base: GDScriptSelf }
                when call.Arguments is [GDScriptLiteral { Kind: GDScriptLiteralKind.String or GDScriptLiteralKind.StringName, Value: string name }, ..]
                => (name, call.Arguments.Count - 1),
            _ => (null, 0),
        };
    }
}

/// <summary>A GDScript's class with the scripts it extends in its project, itself first (<see cref="GodotScenes.ChainOf"/>):
/// each script's res:// path and declarations, whether they are all its class has beside an engine class's (each script it
/// extends read, the last extending a class no script of the project names), and that engine class (RefCounted where
/// nothing is written). A node with no script has none, its engine class its own.</summary>
public sealed record GodotScriptChain(IReadOnlyList<(string Script, GodotScriptDeclarations Declared)> Scripts, bool Whole, string? Engine)
{
    /// <summary>The class of a node with no script: its engine class's alone, known where the scene says it.</summary>
    public static GodotScriptChain Of(string? engine) => new([], engine is not null, engine);

    /// <summary>The script and declaration of the member of a kind by a name its class has, the nearest first; null for none.</summary>
    public (string Script, GodotScriptDeclared Member)? Find(GodotScriptKind kind, string name)
    {
        foreach ((string script, GodotScriptDeclarations declared) in Scripts)
        {
            if (declared.Member(kind, name) is { } member)
            {
                return (script, member);
            }
        }
        return null;
    }

    /// <summary>Every name of a kind its scripts declare, each once, the nearest first.</summary>
    public IReadOnlyList<string> Names(GodotScriptKind kind) =>
        [.. Scripts.SelectMany(s => s.Declared.Members).Where(m => m.Kind == kind).Select(m => m.Name).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether its class is a script's that is, or extends, another.</summary>
    public bool Runs(string script) => Scripts.Any(s => s.Script == script);
}
