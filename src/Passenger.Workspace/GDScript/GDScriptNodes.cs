namespace Passenger.Workspace.GDScript;

/// <summary>A node of a GDScript's syntax tree, as Godot's parser makes it (modules/gdscript/gdscript_parser.h): where it
/// lies (offsets from its member's start, <see cref="GDScriptTree"/>, its end exclusive) and the annotations before it.</summary>
public abstract class GDScriptNode
{
    public int Start { get; internal set; } = -1;

    public int End { get; internal set; } = -1;

    private List<GDScriptAnnotation>? annotations;

    /// <summary>The annotations written before it, in order.</summary>
    public IReadOnlyList<GDScriptAnnotation> Annotations => annotations ?? (IReadOnlyList<GDScriptAnnotation>)[];

    internal void Annotate(IEnumerable<GDScriptAnnotation> added)
    {
        foreach (GDScriptAnnotation annotation in added)
        {
            (annotations ??= []).Add(annotation);
        }
    }

    /// <summary>The nodes it holds, in the order written.</summary>
    public abstract IEnumerable<GDScriptNode> Children { get; }

    internal static IEnumerable<GDScriptNode> Of(params GDScriptNode?[] nodes) => nodes.OfType<GDScriptNode>();
}

/// <summary>What GDScript's syntax trees say of any node: the name a member declares, and every node below one.</summary>
public static class GDScriptNodes
{
    /// <summary>The name a member declares: a function's, a variable's, a constant's or a parameter's, a signal's, an enum's,
    /// an inner class's; null for a node that declares none.</summary>
    public static GDScriptIdentifier? DeclaringName(GDScriptNode node) => node switch
    {
        GDScriptFunction function => function.Identifier,
        GDScriptAssignable assignable => assignable.Identifier,
        GDScriptSignal signal => signal.Identifier,
        GDScriptEnum enumerated => enumerated.Identifier,
        GDScriptClass inner => inner.Identifier,
        _ => null,
    };

    /// <summary>The name a member declares, as written; null for none.</summary>
    public static string? NameOf(GDScriptNode node) => DeclaringName(node)?.Name;

    /// <summary>A node and every node below it, every level down, walked on a stack of its own so no depth of nesting runs
    /// out of the thread's.</summary>
    public static IEnumerable<GDScriptNode> Below(GDScriptNode root)
    {
        Stack<GDScriptNode> left = new([root]);
        while (left.Count > 0)
        {
            GDScriptNode node = left.Pop();
            yield return node;
            foreach (GDScriptNode child in node.Children)
            {
                left.Push(child);
            }
        }
    }
}

public abstract class GDScriptExpression : GDScriptNode;

public sealed class GDScriptAnnotation : GDScriptNode
{
    public string Name { get; internal set; } = string.Empty;

    public List<GDScriptExpression> Arguments { get; } = [];

    public override IEnumerable<GDScriptNode> Children => Arguments;
}

public sealed class GDScriptIdentifier : GDScriptExpression
{
    public string Name { get; internal set; } = string.Empty;

    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptLiteral : GDScriptExpression
{
    public object? Value { get; internal set; }

    public GDScriptLiteralKind Kind { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptSelf : GDScriptExpression
{
    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptArray : GDScriptExpression
{
    public List<GDScriptExpression> Elements { get; } = [];

    public override IEnumerable<GDScriptNode> Children => Elements;
}

public sealed class GDScriptDictionary : GDScriptExpression
{
    public List<(GDScriptExpression Key, GDScriptExpression Value)> Elements { get; } = [];

    /// <summary>Whether its keys are written <c>key = value</c>, as a Lua table's, rather than <c>key: value</c>.</summary>
    public bool LuaTable { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Elements.SelectMany(e => Of(e.Key, e.Value));
}

public sealed class GDScriptAssignment : GDScriptExpression
{
    /// <summary>The operator written, <c>=</c> or a compound one such as <c>+=</c>.</summary>
    public GDScriptTokenKind Operator { get; internal set; }

    public GDScriptExpression? Assignee { get; internal set; }

    public GDScriptExpression? Value { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Assignee, Value);
}

public sealed class GDScriptAwait : GDScriptExpression
{
    public GDScriptExpression? Awaited { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Awaited);
}

public sealed class GDScriptBinary : GDScriptExpression
{
    /// <summary>The operator written (<c>and</c> and <c>&amp;&amp;</c> each as itself; <c>not in</c> is a unary not over <c>in</c>).</summary>
    public GDScriptTokenKind Operator { get; internal set; }

    public GDScriptExpression? Left { get; internal set; }

    public GDScriptExpression? Right { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Left, Right);
}

public sealed class GDScriptCall : GDScriptExpression
{
    /// <summary>What is called: an identifier, an attribute (<c>a.b</c>), or for <c>super.f()</c> the identifier; null for
    /// a bare <c>super()</c>.</summary>
    public GDScriptExpression? Callee { get; internal set; }

    public List<GDScriptExpression> Arguments { get; } = [];

    /// <summary>The function's name where the callee says it; for <c>super()</c>, its own function's.</summary>
    public string? FunctionName { get; internal set; }

    public bool IsSuper { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Callee).Concat(Arguments);
}

public sealed class GDScriptCast : GDScriptExpression
{
    public GDScriptExpression? Operand { get; internal set; }

    public GDScriptTypeNode? Type { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Operand, Type);
}

public sealed class GDScriptGetNode : GDScriptExpression
{
    public string Path { get; internal set; } = string.Empty;

    /// <summary>Whether it is written with <c>$</c> rather than <c>%</c> alone.</summary>
    public bool UsesDollar { get; internal set; } = true;

    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptLambda : GDScriptExpression
{
    public GDScriptFunction? Function { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Function);
}

public sealed class GDScriptPreload : GDScriptExpression
{
    public GDScriptExpression? Path { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Path);
}

/// <summary>An index (<c>a[i]</c>) or, with <see cref="IsAttribute"/>, an attribute (<c>a.b</c>).</summary>
public sealed class GDScriptSubscript : GDScriptExpression
{
    public GDScriptExpression? Base { get; internal set; }

    public GDScriptExpression? Index { get; internal set; }

    public GDScriptIdentifier? Attribute { get; internal set; }

    public bool IsAttribute { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Base, Index, Attribute);
}

public sealed class GDScriptTernary : GDScriptExpression
{
    public GDScriptExpression? Condition { get; internal set; }

    public GDScriptExpression? True { get; internal set; }

    public GDScriptExpression? False { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(True, Condition, False);
}

public sealed class GDScriptTypeTest : GDScriptExpression
{
    public GDScriptExpression? Operand { get; internal set; }

    public GDScriptTypeNode? Type { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Operand, Type);
}

public sealed class GDScriptUnary : GDScriptExpression
{
    /// <summary>The operator written: <c>-</c>, <c>+</c>, <c>~</c>, <c>not</c> or <c>!</c> (<c>not</c> for <c>not in</c> and <c>is not</c>).</summary>
    public GDScriptTokenKind Operator { get; internal set; }

    public GDScriptExpression? Operand { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Operand);
}

public sealed class GDScriptTypeNode : GDScriptNode
{
    /// <summary>Its names as written, <c>A.B.C</c> a name each.</summary>
    public List<GDScriptIdentifier> Chain { get; } = [];

    /// <summary>A typed collection's element types: <c>Array[int]</c>'s one, <c>Dictionary[String, int]</c>'s two.</summary>
    public List<GDScriptTypeNode> Elements { get; } = [];

    /// <summary>Whether it is <c>void</c>, which only a return type may be.</summary>
    public bool IsVoid { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Chain.Concat<GDScriptNode>(Elements);
}

public sealed class GDScriptSuite : GDScriptNode
{
    public List<GDScriptNode> Statements { get; } = [];

    internal GDScriptSuite? Parent { get; set; }

    internal Dictionary<string, (string Kind, GDScriptNode Node)>? Locals { get; set; }

    internal bool InLoop { get; set; }

    internal bool HasReturn { get; set; }

    internal bool HasContinue { get; set; }

    internal void AddLocal(string name, string kind, GDScriptNode node) => (Locals ??= new(StringComparer.Ordinal))[name] = (kind, node);

    /// <summary>What a local of a name is ("variable", "constant", "parameter", "for loop iterator", "pattern bind") in this
    /// block or one around it; null for none.</summary>
    internal string? LocalKind(string name) => Locals is not null && Locals.TryGetValue(name, out (string Kind, GDScriptNode Node) local) ? local.Kind : Parent?.LocalKind(name);

    public override IEnumerable<GDScriptNode> Children => Statements;
}

public abstract class GDScriptAssignable : GDScriptNode
{
    public GDScriptIdentifier? Identifier { get; internal set; }

    public GDScriptExpression? Initializer { get; internal set; }

    public GDScriptTypeNode? Type { get; internal set; }

    /// <summary>Whether its type is inferred, written <c>:=</c>.</summary>
    public bool Infers { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Identifier, Type, Initializer);
}

public sealed class GDScriptParameter : GDScriptAssignable;

public sealed class GDScriptConstant : GDScriptAssignable;

public sealed class GDScriptVariable : GDScriptAssignable
{
    public bool IsStatic { get; internal set; }

    /// <summary>How its property is written: none, inline <c>get:</c>/<c>set(v):</c> bodies, or <c>get = f, set = g</c>.</summary>
    public GDScriptPropertyStyle Property { get; internal set; }

    public GDScriptFunction? Getter { get; internal set; }

    public GDScriptFunction? Setter { get; internal set; }

    public GDScriptIdentifier? GetterName { get; internal set; }

    public GDScriptIdentifier? SetterName { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => base.Children.Concat(Of(Getter, Setter, GetterName, SetterName));
}

public enum GDScriptPropertyStyle
{
    None,
    Inline,
    SetGet,
}

public sealed class GDScriptFunction : GDScriptNode
{
    /// <summary>Its name; null for a lambda written without one.</summary>
    public GDScriptIdentifier? Identifier { get; internal set; }

    public List<GDScriptParameter> Parameters { get; } = [];

    /// <summary>Its <c>...rest</c> parameter, which takes every argument after the others.</summary>
    public GDScriptParameter? Rest { get; internal set; }

    public GDScriptTypeNode? ReturnType { get; internal set; }

    public GDScriptSuite? Body { get; internal set; }

    public bool IsStatic { get; internal set; }

    public bool IsAbstract { get; internal set; }

    /// <summary>Whether it is a lambda's.</summary>
    public bool IsLambda { get; internal set; }

    /// <summary>Where its header ends: after its return type, or its parameters' closing parenthesis.</summary>
    public int HeaderEnd { get; internal set; }

    /// <summary>Where its parameters' parentheses open and close; -1 where not written.</summary>
    public int ParametersStart { get; internal set; } = -1;

    public int ParametersEnd { get; internal set; } = -1;

    public override IEnumerable<GDScriptNode> Children => Of(Identifier).Concat(Parameters).Concat(Of(Rest, ReturnType, Body));
}

public sealed class GDScriptSignal : GDScriptNode
{
    public GDScriptIdentifier? Identifier { get; internal set; }

    public List<GDScriptParameter> Parameters { get; } = [];

    /// <summary>Whether its parameters are written in parentheses, <c>signal hit()</c>, rather than left out, <c>signal hit</c>.</summary>
    public bool HasParentheses { get; internal set; }

    public int ParametersEnd { get; internal set; } = -1;

    public override IEnumerable<GDScriptNode> Children => Of(Identifier).Concat(Parameters);
}

public sealed class GDScriptEnum : GDScriptNode
{
    /// <summary>Its name; null for an unnamed enum, whose values are the class's constants.</summary>
    public GDScriptIdentifier? Identifier { get; internal set; }

    public List<(GDScriptIdentifier Name, GDScriptExpression? Value)> Values { get; } = [];

    public override IEnumerable<GDScriptNode> Children => Of(Identifier).Concat(Values.SelectMany(v => Of(v.Name, v.Value)));
}

/// <summary>A class: the script's own (whose members <see cref="GDScriptTree.Members"/> holds) or an inner <c>class</c>.</summary>
public sealed class GDScriptClass : GDScriptNode
{
    /// <summary>Its name: an inner class's, or the script's <c>class_name</c>.</summary>
    public GDScriptIdentifier? Identifier { get; internal set; }

    /// <summary>What it extends by path, <c>extends "res://a.gd"</c>; null where written by name or not at all.</summary>
    public string? ExtendsPath { get; internal set; }

    /// <summary>What it extends by name, <c>extends A.B</c> a name each, after a path where both are written.</summary>
    public List<GDScriptIdentifier> Extends { get; } = [];

    public bool ExtendsUsed { get; internal set; }

    public bool IsAbstract { get; internal set; }

    public bool IsTool { get; internal set; }

    public string? IconPath { get; internal set; }

    /// <summary>An inner class's members; the script's own are its tree's.</summary>
    public List<GDScriptNode> Members { get; } = [];

    public override IEnumerable<GDScriptNode> Children => Of(Identifier).Concat(Extends).Concat(Members);
}

public sealed class GDScriptPass : GDScriptNode
{
    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptBreak : GDScriptNode
{
    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptContinue : GDScriptNode
{
    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptBreakpoint : GDScriptNode
{
    public override IEnumerable<GDScriptNode> Children => [];
}

public sealed class GDScriptReturn : GDScriptNode
{
    public GDScriptExpression? Value { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Value);
}

public sealed class GDScriptAssert : GDScriptNode
{
    public GDScriptExpression? Condition { get; internal set; }

    public GDScriptExpression? Message { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Condition, Message);
}

public sealed class GDScriptIf : GDScriptNode
{
    public GDScriptExpression? Condition { get; internal set; }

    public GDScriptSuite? True { get; internal set; }

    /// <summary>Its <c>else</c> block; an <c>elif</c> is a block holding the next <c>if</c>.</summary>
    public GDScriptSuite? False { get; internal set; }

    /// <summary>Whether it was written <c>elif</c>: no block of its own holds its condition, which a statement can't go before.</summary>
    public bool IsElif { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Condition, True, False);
}

public sealed class GDScriptFor : GDScriptNode
{
    public GDScriptIdentifier? Variable { get; internal set; }

    public GDScriptTypeNode? Type { get; internal set; }

    public GDScriptExpression? List { get; internal set; }

    public GDScriptSuite? Loop { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Variable, Type, List, Loop);
}

public sealed class GDScriptWhile : GDScriptNode
{
    public GDScriptExpression? Condition { get; internal set; }

    public GDScriptSuite? Loop { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Of(Condition, Loop);
}

public sealed class GDScriptMatch : GDScriptNode
{
    public GDScriptExpression? Test { get; internal set; }

    public List<GDScriptMatchBranch> Branches { get; } = [];

    public override IEnumerable<GDScriptNode> Children => Of(Test).Concat(Branches);
}

public sealed class GDScriptMatchBranch : GDScriptNode
{
    public List<GDScriptPattern> Patterns { get; } = [];

    /// <summary>Its <c>when</c> guard's block, holding the guard's expression.</summary>
    public GDScriptSuite? Guard { get; internal set; }

    public GDScriptSuite? Block { get; internal set; }

    public override IEnumerable<GDScriptNode> Children => Patterns.Concat(Of(Guard, Block));
}

public enum GDScriptPatternKind
{
    Literal,
    Expression,
    Bind,
    Array,
    Dictionary,
    Rest,
    Wildcard,
}

public sealed class GDScriptPattern : GDScriptNode
{
    public GDScriptPatternKind Kind { get; internal set; }

    /// <summary>A literal's or expression's pattern.</summary>
    public GDScriptExpression? Expression { get; internal set; }

    /// <summary>A <c>var name</c> pattern's name.</summary>
    public GDScriptIdentifier? Bind { get; internal set; }

    public List<GDScriptPattern> Array { get; } = [];

    public List<(GDScriptExpression? Key, GDScriptPattern? Value)> Dictionary { get; } = [];

    internal Dictionary<string, GDScriptIdentifier>? Binds { get; set; }

    public override IEnumerable<GDScriptNode> Children => Of(Expression, Bind).Concat(Array).Concat(Dictionary.SelectMany(p => Of(p.Key, p.Value)));
}
