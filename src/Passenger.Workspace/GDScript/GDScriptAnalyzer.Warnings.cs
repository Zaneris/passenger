using System.Runtime.CompilerServices;

namespace Passenger.Workspace.GDScript;

/// <summary>Godot's warnings and the errors of its analyzer that Passenger's reading gives, pushed where Godot's analyzer and
/// parser push them (gdscript_analyzer.cpp, gdscript_parser.cpp, their words gdscript_warning.cpp's): what a script declares
/// and never uses, types left to inference, shadowed names, unreachable code, standalone expressions, integer division, an
/// await that waits for nothing or one missing, a cast of what may hold anything, and a function whose every way does not
/// return.</summary>
public sealed partial class GDScriptAnalyzer
{
    private static readonly ConditionalWeakTable<GDScriptTree, HashSet<int>> Coroutines = [];

    private readonly Dictionary<GDScriptNode, Dictionary<string, (GDScriptNode Node, int Offset)>> ownNames = [];

    private static void Warn(Context context, GDScriptNode node, string code, params string[] symbols) =>
        Warn(context, node.Start, node.End, code, symbols);

    private static void Warn(Context context, int start, int end, string code, params string[] symbols)
    {
        if (context.Analysis.Tree is not null && start >= 0)
        {
            context.Analysis.Found.Add(new GDScriptFinding(context.Offset + start, context.Offset + Math.Max(start, end), code, Message(code, symbols)));
        }
    }

    private static void Error(Context context, GDScriptNode node, string message)
    {
        if (context.Analysis.Tree is not null && node.Start >= 0)
        {
            context.Analysis.Found.Add(new GDScriptFinding(context.Offset + node.Start, context.Offset + Math.Max(node.Start, node.End), null, message));
        }
    }

    private static string FunctionName(GDScriptFunction function) =>
        function.Identifier?.Name ?? (function.IsLambda ? "<anonymous lambda>" : "<unknown function>");

    /// <summary>A warning's words as Godot's get_message writes them from its symbols.</summary>
    private static string Message(string code, string[] s) => code switch
    {
        "UNASSIGNED_VARIABLE" => $"The variable \"{s[0]}\" is used before being assigned a value.",
        "UNASSIGNED_VARIABLE_OP_ASSIGN" => $"The variable \"{s[0]}\" is modified with the compound-assignment operator \"{s[1]}=\" but was not previously initialized.",
        "UNUSED_VARIABLE" => $"The local variable \"{s[0]}\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_{s[0]}\".",
        "UNUSED_LOCAL_CONSTANT" => $"The local constant \"{s[0]}\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_{s[0]}\".",
        "UNUSED_PRIVATE_CLASS_VARIABLE" => $"The class variable \"{s[0]}\" is declared but never used in the class.",
        "UNUSED_PARAMETER" => $"The parameter \"{s[1]}\" is never used in the function \"{s[0]}()\". If this is intended, prefix it with an underscore: \"_{s[1]}\".",
        "UNUSED_SIGNAL" => $"The signal \"{s[0]}\" is declared but never explicitly used in the class.",
        "SHADOWED_VARIABLE" => $"The local {s[0]} \"{s[1]}\" is shadowing an already-declared {s[2]} at line {s[3]} in the current class.",
        "SHADOWED_VARIABLE_BASE_CLASS" => s.Length > 4
            ? $"The local {s[0]} \"{s[1]}\" is shadowing an already-declared {s[2]} at line {s[3]} in the base class \"{s[4]}\"."
            : $"The local {s[0]} \"{s[1]}\" is shadowing an already-declared {s[2]} in the base class \"{s[3]}\".",
        "SHADOWED_GLOBAL_IDENTIFIER" => $"The {s[0]} \"{s[1]}\" has the same name as a {s[2]}.",
        "UNREACHABLE_CODE" => $"Unreachable code (statement after return) in function \"{s[0]}()\".",
        "UNREACHABLE_PATTERN" => "Unreachable pattern (pattern after wildcard or bind).",
        "STANDALONE_EXPRESSION" => "Standalone expression (the line may have no effect).",
        "STANDALONE_TERNARY" => "Standalone ternary operator (the return value is being discarded).",
        "INCOMPATIBLE_TERNARY" => "Values of the ternary operator are not mutually compatible.",
        "UNTYPED_DECLARATION" => s[0] == "Function" ? $"{s[0]} \"{s[1]}()\" has no static return type." : $"{s[0]} \"{s[1]}\" has no static type.",
        "INFERRED_DECLARATION" => $"{s[0]} \"{s[1]}\" has an implicitly inferred static type.",
        "UNSAFE_CAST" => $"Casting \"Variant\" to \"{s[0]}\" is unsafe.",
        "UNSAFE_PROPERTY_ACCESS" => $"The property \"{s[0]}\" is not present on the inferred type \"{s[1]}\" (but may be present on a subtype).",
        "UNSAFE_METHOD_ACCESS" => $"The method \"{s[0]}()\" is not present on the inferred type \"{s[1]}\" (but may be present on a subtype).",
        "UNSAFE_CALL_ARGUMENT" => $"The argument {s[0]} of the {s[1]} \"{s[2]}()\" requires the subtype \"{s[3]}\" but the supertype \"{s[4]}\" was provided.",
        "RETURN_VALUE_DISCARDED" => $"The function \"{s[0]}()\" returns a value that will be discarded if not used.",
        "REDUNDANT_AWAIT" => "\"await\" keyword is unnecessary because the expression isn't a coroutine nor a signal.",
        "MISSING_AWAIT" => "\"await\" keyword might be desired because the expression is a coroutine.",
        "ASSERT_ALWAYS_TRUE" => "Assert statement is redundant because the expression is always true.",
        "ASSERT_ALWAYS_FALSE" => "Assert statement will raise an error because the expression is always false.",
        "INTEGER_DIVISION" => "Integer division. Decimal part will be discarded.",
        "NARROWING_CONVERSION" => "Narrowing conversion (float is converted to int and loses precision).",
        "ENUM_VARIABLE_WITHOUT_DEFAULT" => $"The variable \"{s[0]}\" has an enum type and does not set an explicit default value. The default will be set to \"0\".",
        "EMPTY_FILE" => "Empty script file.",
        "INFERENCE_ON_VARIANT" => $"The {s[0]} type is being inferred from a Variant value, so it will be typed as Variant.",
        "GET_NODE_DEFAULT_WITHOUT_ONREADY" => $"The default value uses \"{s[0]}\" which won't return nodes in the scene tree before \"_ready()\" is called. Use the \"@onready\" annotation to solve this.",
        "ONREADY_WITH_EXPORT" => "\"@onready\" will set the default value after \"@export\" takes effect and will override it.",
        "CONFUSABLE_CAPTURE_REASSIGNMENT" => $"Reassigning lambda capture does not modify the outer local variable \"{s[0]}\".",
        "CONFUSABLE_TEMPORARY_MODIFICATION" => s.Length > 2
            ? $"The built-in property \"{s[1]}\" will not be modified as a result of calling the method \"{s[2]}()\". Consider assigning the desired value to the property instead, or check the \"{s[0]}\" class for a specialized API."
            : $"The built-in property \"{s[1]}\" will not be modified in the assignment chain. Consider using a temporary variable and assigning it back instead, or check the \"{s[0]}\" class for a specialized API.",
        "UNSAFE_VOID_RETURN" => $"The method \"{s[0]}()\" returns \"void\" but it's trying to return a call to \"{s[1]}()\" that can't be ensured to also be \"void\".",
        "INT_AS_ENUM_WITHOUT_CAST" => "Integer used when an enum value is expected. If this is intended, cast the integer to the enum type using the \"as\" keyword.",
        "STATIC_CALLED_ON_INSTANCE" => $"The function \"{s[0]}()\" is a static function but was called from an instance. Instead, it should be directly called from the type: \"{s[1]}.{s[0]}()\".",
        "INT_AS_ENUM_WITHOUT_MATCH" => $"Cannot {s[0]} {s[1]} as Enum \"{s[2]}\": no enum member has matching value.",
        "MISSING_TOOL" => "The base class script has the \"@tool\" annotation, but this script does not have it.",
        "CONFUSABLE_LOCAL_DECLARATION" => $"The {s[0]} \"{s[1]}\" is declared below in the parent block.",
        "CONFUSABLE_LOCAL_USAGE" => $"The identifier \"{s[0]}\" will be shadowed below in the block.",
        "ONREADY_WITH_CAST" => $"\"as\" will silently return \"null\" if the type of \"{s[0]}\" is wrong. Prefer assigning the node directly to an explicitly typed variable to get an error in such cases.",
        "NATIVE_METHOD_OVERRIDE" => $"The method \"{s[0]}()\" overrides a method from native class \"{s[1]}\". This won't be called by the engine and may not work as expected.",
        _ => code,
    };

    /// <summary>A declaration's typing warnings, as Godot's resolve_assignable gives them: no type written and none inferred,
    /// one only inferred, one inferred from a Variant, an enum with no zero and no value, a float given to an int; and a
    /// variable's or constant's name shadowing another (<paramref name="kind"/> as Godot names it).</summary>
    private void Declaring(GDScriptAssignable node, GDScriptType type, string kind, bool local, Context context)
    {
        if (node.Identifier is not { } name || context.Analysis.Tree is null)
        {
            return;
        }
        bool constant = node is GDScriptConstant;
        GDScriptType? value = node.Initializer is { } initializer ? context.Analysis.Types.GetValueOrDefault(initializer) : null;
        if (node.Infers && value is { Hard: true, IsVariant: true } && node.Initializer is { } inferred && DeclaredVariant(inferred, context.Analysis))
        {
            Warn(context, node, "INFERENCE_ON_VARIANT", kind);
        }
        string declared = constant ? "Constant" : node is GDScriptParameter ? "Parameter" : "Variable";
        if (node.Type is null)
        {
            if (node.Infers || constant)
            {
                if (!(constant && value is { Meta: true }))
                {
                    Warn(context, node, "INFERRED_DECLARATION", declared, name.Name);
                }
            }
            else
            {
                Warn(context, node, "UNTYPED_DECLARATION", declared, name.Name);
            }
        }
        else if (node is not GDScriptParameter && type is { Kind: GDScriptTypeKind.Enum, Values: { } values } && node.Initializer is null && !values.Values.Contains(0))
        {
            Warn(context, node, "ENUM_VARIABLE_WITHOUT_DEFAULT", name.Name);
        }
        if (node.Type is not null && type is { Kind: GDScriptTypeKind.Builtin, Name: "int" } && value is { Kind: GDScriptTypeKind.Builtin, Name: "float", Hard: true } && node.Initializer is { } narrowed)
        {
            Warn(context, narrowed, "NARROWING_CONVERSION");
        }
        if (node.Initializer is { } given && value is not null && Knows(context.Class))
        {
            InitializerErrors(node, name.Name, kind, type, given, value, context);
        }
        if (node is not GDScriptParameter)
        {
            Shadowing(name, kind, local, context);
        }
    }

    /// <summary>Godot's errors of a declaration's value (resolve_assignable): a collection written out of elements its typed
    /// one takes none of, a type <c>:=</c> cannot infer from null, and a value of a type the written one takes none of, a
    /// constant's said as its conversion first.</summary>
    private void InitializerErrors(GDScriptAssignable node, string name, string kind, GDScriptType type, GDScriptExpression given, GDScriptType value, Context context)
    {
        if (node.Type is not null)
        {
            TypedLiteralErrors(given, type, context);
        }
        if (node.Infers)
        {
            if (value is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" } && given is GDScriptLiteral && node is not GDScriptConstant)
            {
                Error(context, given, $"Cannot infer the type of \"{name}\" {kind} because the value is \"null\".");
            }
            return;
        }
        if (node.Type is null || type.IsVariant || value.IsVariant || !value.Hard || !Sure(given, context))
        {
            return;
        }
        ConstantConverted(given, value, type, "assign", context);
        if (!TypeCompatible(type, value, implicitly: true) && (node is GDScriptConstant || !TypeCompatible(value, type)))
        {
            Error(context, given, $"Cannot assign a value of type {value} to {kind} \"{name}\" with specified type {type}.");
        }
    }

    /// <summary>Whether a value is a name (or a member, or a call) the script itself declares as Variant in so many words:
    /// the one hard Variant Passenger's reading is sure of.</summary>
    private static bool DeclaredVariant(GDScriptExpression value, GDScriptAnalysis analysis)
    {
        GDScriptIdentifier? name = value switch
        {
            GDScriptIdentifier identifier => identifier,
            GDScriptSubscript { IsAttribute: true, Attribute: { } attribute } => attribute,
            GDScriptCall { Callee: GDScriptIdentifier callee } => callee,
            GDScriptCall { Callee: GDScriptSubscript { IsAttribute: true, Attribute: { } method } } => method,
            _ => null,
        };
        return name is not null && analysis.Declared.GetValueOrDefault(name) is { } declared && declared.Script == analysis.Path && analysis.WrittenVariant.Contains(declared.Start);
    }

    /// <summary>A class variable's warnings of its own: <c>@onready</c> with <c>@export</c> or (from Godot 4.8) with a node
    /// path cast by <c>as</c>, and a node got as its default value before the tree has it, without <c>@onready</c>.</summary>
    private void MemberVariableWarnings(GDScriptVariable variable, Context context)
    {
        bool onready = variable.Annotations.Any(a => a.Name == "@onready");
        if (onready && variable.Annotations.Any(a => a.Name.StartsWith("@export", StringComparison.Ordinal)))
        {
            Warn(context, variable, "ONREADY_WITH_EXPORT");
        }
        if (onready && variable.Initializer is GDScriptCast { Operand: GDScriptGetNode path } cast && Engine?.Version >= new Version(4, 8))
        {
            Warn(context, cast, "ONREADY_WITH_CAST", (path.UsesDollar ? "$" : "%") + path.Path);
        }
        if (variable.IsStatic || onready || variable.Initializer is not { } value)
        {
            return;
        }
        GDScriptExpression got = value is GDScriptCast { Operand: { } operand } ? operand : value;
        string? syntax = got switch
        {
            GDScriptGetNode node => node.UsesDollar ? "$" : "%",
            GDScriptCall { FunctionName: "get_node", Callee: GDScriptIdentifier or GDScriptSubscript { IsAttribute: true, Base: GDScriptSelf } } => "get_node()",
            _ => null,
        };
        if (syntax is not null)
        {
            Warn(context, variable, "GET_NODE_DEFAULT_WITHOUT_ONREADY", syntax);
        }
    }

    /// <summary>A function's warnings once its body is read: each parameter never used, and a declared return type some
    /// way through the body does not return.</summary>
    private void FunctionWarnings(GDScriptFunction function, List<Local> parameters, bool returns, Context context)
    {
        foreach (Local parameter in parameters)
        {
            if (parameter.Usages == 0 && !parameter.Name.Name.StartsWith('_'))
            {
                Warn(context, parameter.Name, "UNUSED_PARAMETER", FunctionName(function), parameter.Name.Name);
            }
        }
        NativeOverride(function, context);
        if (!returns && !function.IsAbstract && function.ReturnType is { IsVoid: false } declared && (function.IsLambda || function.Identifier?.Name is not ("_init" or "_static_init")))
        {
            Error(context, declared, "Not all code paths return a value.");
        }
    }

    /// <summary>Each string a class holds outside its members (a script's first lines, between its members, an inner
    /// class's body), which Godot's parse_standalone_string lets stand for a comment and warns of as STANDALONE_EXPRESSION;
    /// a string an annotation or <c>extends</c> takes is none.</summary>
    private static void ClassLevelStrings(GDScriptTree tree, Context context)
    {
        Strings(tree.Text, 0, tree.Text.Length, [.. tree.Members.Select(m => (m.Offset + m.Node.Start, m.Offset + m.Node.End))], context);
        foreach (GDScriptMember member in tree.Members)
        {
            InnerStrings(tree.Text, member.Node, member.Offset, context);
        }
    }

    private static void InnerStrings(string text, GDScriptNode node, int offset, Context context)
    {
        if (node is not GDScriptClass inner)
        {
            return;
        }
        Strings(text, offset + inner.Start, offset + inner.End, [.. inner.Members.Select(m => (offset + m.Start, offset + m.End))], context);
        foreach (GDScriptNode nested in inner.Members)
        {
            InnerStrings(text, nested, offset, context);
        }
    }

    /// <summary>The strings written in a stretch of a class's text outside its members' spans.</summary>
    private static void Strings(string text, int from, int to, List<(int Start, int End)> members, Context context)
    {
        int at = from;
        foreach ((int start, int end) in members.Where(m => m.Start < to && m.End > from).OrderBy(m => m.Start).Append((to, to)))
        {
            if (start > at)
            {
                GDScriptTokenizer tokens = new(text, at);
                int depth = 0;
                GDScriptTokenKind before = GDScriptTokenKind.Newline;
                for (GDScriptToken token = tokens.Scan(); token.Kind != GDScriptTokenKind.Eof && token.Start < start; token = tokens.Scan())
                {
                    depth += token.Kind is GDScriptTokenKind.ParenthesisOpen or GDScriptTokenKind.BracketOpen or GDScriptTokenKind.BraceOpen ? 1
                        : token.Kind is GDScriptTokenKind.ParenthesisClose or GDScriptTokenKind.BracketClose or GDScriptTokenKind.BraceClose ? -1 : 0;
                    if (token is { Kind: GDScriptTokenKind.Literal, Literal: GDScriptLiteralKind.String } && depth == 0 && before != GDScriptTokenKind.Extends)
                    {
                        Warn(context, token.Start, token.End, "STANDALONE_EXPRESSION");
                    }
                    before = token.Kind;
                }
            }
            at = Math.Max(at, end);
        }
    }

    /// <summary>An assignment in a lambda to a local it captures, as Godot's reduce_assignment warns of it: the local itself,
    /// or a part of a value the lambda holds a copy of (a hard built-in type other than Array and Dictionary, or an enum).</summary>
    private static void CaptureReassignment(GDScriptAssignment assignment, Context context)
    {
        if (context.Function is not { IsLambda: true })
        {
            return;
        }
        bool part = false;
        GDScriptExpression? at = assignment.Assignee;
        while (at is GDScriptSubscript subscript)
        {
            part = true;
            at = subscript.Base;
        }
        if (at is not GDScriptIdentifier name || context.Scope?.Captured(name.Name) is not { } captured)
        {
            return;
        }
        GDScriptType type = captured.Type;
        if (!part || type.Hard && (type.Kind == GDScriptTypeKind.Enum || type.Kind == GDScriptTypeKind.Builtin && type.Name is not ("Array" or "Dictionary" or "Variant")))
        {
            Warn(context, assignment, "CONFUSABLE_CAPTURE_REASSIGNMENT", name.Name);
        }
    }

    /// <summary>A packed array an engine class's property gives, changed in place (assigned into, or by a method that
    /// changes it): a copy the property never takes back, as Godot's warn_confusable_temporary_modification says, for an
    /// assignment's chain or a method read off it.</summary>
    private void TemporaryModification(GDScriptAssignment? assignment, GDScriptExpression? chain, Context context)
    {
        for (GDScriptExpression? current = chain; current is GDScriptSubscript subscript; current = subscript.Base)
        {
            if (subscript.Base is not { } container || context.Analysis.Types.GetValueOrDefault(container) is not { Kind: GDScriptTypeKind.Builtin, Meta: false } held
                || !held.Name.StartsWith("Packed", StringComparison.Ordinal) || !held.Name.EndsWith("Array", StringComparison.Ordinal))
            {
                continue;
            }
            (GDScriptIdentifier? property, GDScriptType? scope) = container switch
            {
                GDScriptIdentifier named when context.Scope?.Find(named.Name) is null => (named, context.Class.Instance),
                GDScriptSubscript { IsAttribute: true, Attribute: { } attribute, Base: { } owner } => (attribute, context.Analysis.Types.GetValueOrDefault(owner)),
                _ => (null, null),
            };
            if (property is null || scope is null || NativePropertyOwner(scope, property.Name) is not { } native)
            {
                continue;
            }
            if (assignment is not null)
            {
                Warn(context, assignment, "CONFUSABLE_TEMPORARY_MODIFICATION", native, property.Name);
            }
            else if (subscript is { IsAttribute: true, Attribute: { } method } && Engine?.Builtins.GetValueOrDefault(held.Name)?.Methods.GetValueOrDefault(method.Name) is { IsConst: false })
            {
                Warn(context, subscript, "CONFUSABLE_TEMPORARY_MODIFICATION", native, property.Name, method.Name);
            }
        }
    }

    /// <summary>The engine class a type is of (its own, or the one its script classes extend) where a name read off it is
    /// that class's property and no script class on the way declares it; null otherwise.</summary>
    private string? NativePropertyOwner(GDScriptType scope, string name)
    {
        GDScriptType? at = scope.Instance;
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++)
        {
            if (at.Class is null || OwnNames(at).ContainsKey(name))
            {
                return null;
            }
            at = BaseOf(at);
        }
        if (at is not { Kind: GDScriptTypeKind.Native } native || Engine is not { } engine || !engine.Classes.ContainsKey(native.Name))
        {
            return null;
        }
        return engine.Chain(native.Name).Any(c => c.Properties.ContainsKey(name)) ? native.Name : null;
    }

    /// <summary>An integer an expression is as Godot folds it for a cast: a number written, negated or not, or an enum's
    /// value named off an enum whose every value is known; null for any other.</summary>
    private static long? EnumConstant(GDScriptExpression expression, Context context) => expression switch
    {
        GDScriptLiteral { Value: long given } => given,
        GDScriptUnary { Operator: GDScriptTokenKind.Minus, Operand: GDScriptLiteral { Value: long negated } } => -negated,
        GDScriptSubscript { IsAttribute: true, Attribute: { } member, Base: { } owner }
            when context.Analysis.Types.GetValueOrDefault(owner) is { Kind: GDScriptTypeKind.Enum, Meta: true, ValuesPartial: false, Values: { } values }
            && values.TryGetValue(member.Name, out long named) => named,
        _ => null,
    };

    /// <summary>A class that is no tool while the script it extends is one, as Godot's resolve_class_inheritance warns of it:
    /// a script extended by path, by class_name or by an autoload's name, never one of its own script's classes.</summary>
    private void MissingTool(GDScriptClass node, GDScriptTree own, string script, int offset, Context context)
    {
        if (own.Class.IsTool || context.Analysis.Tree is null)
        {
            return;
        }
        string? based = node.ExtendsPath is { } written ? Absolute(written, script)
            : node.Extends.Count == 0 ? null
            : project.ClassPath(node.Extends[0].Name) ?? (node == own.Class || InnerClass(ScriptClass(own, script), node.Extends[0].Name) is null ? project.Autoload(node.Extends[0].Name) : null);
        if (based is null || based == script || Tree(based) is not { Class.IsTool: true })
        {
            return;
        }
        int start = offset + node.Start;
        int end = own.Text.IndexOf('\n', Math.Min(start, own.Text.Length));
        context.Analysis.Found.Add(new GDScriptFinding(start, end < 0 ? own.Text.Length : end, "MISSING_TOOL", Message("MISSING_TOOL", [])));
    }

    /// <summary>A local declared in a block while a block it is inside declares the name further down, as Godot's
    /// resolve_assignable warns of it.</summary>
    private static void ConfusableDeclaration(GDScriptIdentifier name, Scope scope, Context context)
    {
        if (scope.Parent?.InSuites(name.Name) is { } kind)
        {
            Warn(context, name, "CONFUSABLE_LOCAL_DECLARATION", kind, name.Name);
        }
    }

    /// <summary>A function named as a method an engine class it extends binds, not a virtual one, which the engine never
    /// calls in its place; none where a script class on the way declares the name or the way is not known throughout.</summary>
    private void NativeOverride(GDScriptFunction function, Context context)
    {
        if (function.IsLambda || function.Identifier is not { } name)
        {
            return;
        }
        GDScriptType? at = BaseOf(context.Class);
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++)
        {
            if (at.Class is null || OwnNames(at).ContainsKey(name.Name))
            {
                return;
            }
            at = BaseOf(at);
        }
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Native } && Engine?.Classes.GetValueOrDefault(at.Name) is { } engine && depth < 64; depth++)
        {
            if (engine.Methods.TryGetValue(name.Name, out GodotApiMethod? method))
            {
                if (!method.IsVirtual)
                {
                    Warn(context, name, "NATIVE_METHOD_OVERRIDE", name.Name, engine.Name);
                }
                return;
            }
            at = engine.Inherits is { } parent ? GDScriptType.Native(parent) : null;
        }
    }

    /// <summary>The variables and constants a block declared that it never used, as Godot's resolve_suite warns of them.</summary>
    private static void UnusedLocals(Scope scope, Context context)
    {
        foreach (Local local in scope.Declared)
        {
            if (local.Usages != 0 || local.Name.Name.StartsWith('_'))
            {
                continue;
            }
            switch (local.Kind)
            {
                case LocalKind.Variable:
                case LocalKind.Bind:
                    Warn(context, local.Node, "UNUSED_VARIABLE", local.Name.Name);
                    break;
                case LocalKind.Constant:
                    Warn(context, local.Node, "UNUSED_LOCAL_CONSTANT", local.Name.Name);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>A name that shadows another, as Godot's is_shadowing says: a GDScript or utility function, an engine class, a
    /// project's class, a built-in type; for a local, a member of its class or a script class it extends; then a member of
    /// the engine classes it extends.</summary>
    private void Shadowing(GDScriptIdentifier name, string kind, bool local, Context context)
    {
        if (context.Analysis.Tree is null)
        {
            return;
        }
        string named = name.Name;
        if (Engine?.Functions.ContainsKey(named) == true)
        {
            Warn(context, name, "SHADOWED_GLOBAL_IDENTIFIER", kind, named, "built-in function");
            return;
        }
        if (Engine?.Classes.ContainsKey(named) == true)
        {
            Warn(context, name, "SHADOWED_GLOBAL_IDENTIFIER", kind, named, "native class");
            return;
        }
        if (project.ClassPath(named) is { } global)
        {
            Warn(context, name, "SHADOWED_GLOBAL_IDENTIFIER", kind, named, $"global class defined in \"{global.Split('/')[^1]}\"");
            return;
        }
        if (Engine?.Builtins.ContainsKey(named) == true && named != "Nil")
        {
            Warn(context, name, "SHADOWED_GLOBAL_IDENTIFIER", kind, named, "built-in type");
            return;
        }
        GDScriptType? at = context.Class;
        if (local)
        {
            bool current = true;
            for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++, current = false)
            {
                if (OwnNames(at).TryGetValue(named, out (GDScriptNode Node, int Offset) owned) && owned is ({ } member, int offset))
                {
                    string line = LineOf(at.Script, offset + member.Start).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (current)
                    {
                        Warn(context, name, "SHADOWED_VARIABLE", kind, named, KindOf(member), line);
                    }
                    else
                    {
                        Warn(context, name, "SHADOWED_VARIABLE_BASE_CLASS", kind, named, KindOf(member), line, at.Class?.Identifier?.Name ?? at.Name);
                    }
                    return;
                }
                at = BaseOf(at);
            }
        }
        else
        {
            for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++)
            {
                at = BaseOf(at);
            }
        }
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Native } && Engine?.Classes.GetValueOrDefault(at.Name) is { } engine && depth < 64; depth++)
        {
            string? what = engine.Methods.ContainsKey(named) ? "method" : engine.Signals.ContainsKey(named) ? "signal" : engine.Properties.ContainsKey(named) ? "property"
                : engine.Constants.ContainsKey(named) ? "constant" : engine.Enums.ContainsKey(named) ? "enum" : null;
            if (what is not null)
            {
                Warn(context, name, "SHADOWED_VARIABLE_BASE_CLASS", kind, named, what, at.Name);
                return;
            }
            at = engine.Inherits is { } parent ? GDScriptType.Native(parent) : null;
        }
    }

    /// <summary>A class's own members by each name they declare, the first of a name kept, made once per class.</summary>
    private Dictionary<string, (GDScriptNode Node, int Offset)> OwnNames(GDScriptType type)
    {
        if (type.Class is not { } key)
        {
            return [];
        }
        if (!ownNames.TryGetValue(key, out Dictionary<string, (GDScriptNode, int)>? names))
        {
            names = new Dictionary<string, (GDScriptNode, int)>(StringComparer.Ordinal);
            foreach ((GDScriptNode node, int offset) in OwnMembers(type))
            {
                foreach (string name in Names(node))
                {
                    names.TryAdd(name, (node, offset));
                }
            }
            ownNames[key] = names;
        }
        return names;
    }

    private static string KindOf(GDScriptNode member) => member switch
    {
        GDScriptVariable => "variable",
        GDScriptConstant => "constant",
        GDScriptFunction => "function",
        GDScriptSignal => "signal",
        GDScriptClass => "class",
        GDScriptEnum => "enum",
        _ => "member",
    };

    /// <summary>The line (from 1) an offset of a script's text is on.</summary>
    private int LineOf(string? script, int offset)
    {
        if (script is null || Tree(script) is not { } tree)
        {
            return 0;
        }
        int line = 1;
        string text = tree.Text;
        for (int i = text.IndexOf('\n'); i >= 0 && i < offset; i = text.IndexOf('\n', i + 1))
        {
            line++;
        }
        return line;
    }

    /// <summary>An expression statement's warning as Godot's parser gives it: an assignment, await or call is fine, a
    /// preload's value is discarded, a lambda cannot be reached, a ternary's value is discarded, anything else stands alone.</summary>
    private static void Standalone(GDScriptExpression expression, Context context)
    {
        switch (expression)
        {
            case GDScriptAssignment or GDScriptAwait or GDScriptCall:
                break;
            case GDScriptPreload:
                Warn(context, expression, "RETURN_VALUE_DISCARDED", "preload");
                break;
            case GDScriptLambda:
                Error(context, expression, "Standalone lambdas cannot be accessed. Consider assigning it to a variable.");
                break;
            case GDScriptTernary:
                Warn(context, expression, "STANDALONE_TERNARY");
                break;
            default:
                Warn(context, expression, "STANDALONE_EXPRESSION");
                break;
        }
    }

    /// <summary>An expression's value where it is a constant Godot folds (a literal, a constant's name, a negated one); null
    /// for any other.</summary>
    private static object? Constant(GDScriptExpression? expression) => expression switch
    {
        GDScriptLiteral { Kind: not GDScriptLiteralKind.Null } literal => literal.Value,
        GDScriptLiteral => DBNull.Value,
        GDScriptUnary { Operator: GDScriptTokenKind.Minus, Operand: GDScriptLiteral { Value: long or double } negated } => negated.Value,
        _ => null,
    };

    private static bool Truthy(object value) => value switch
    {
        bool b => b,
        long i => i != 0,
        int i => i != 0,
        double d => d != 0,
        string s => s.Length > 0,
        _ => false,
    };

    /// <summary>A class's warnings once the script is read: its signals never used, and its private variables never used.</summary>
    private static void ClassWarnings(IReadOnlyList<GDScriptMember> members, GDScriptAnalysis analysis, IReadOnlyDictionary<int, int> uses)
    {
        foreach ((GDScriptNode node, int offset) in members)
        {
            Context at = new(analysis, GDScriptType.Variant, offset, null, null);
            switch (node)
            {
                case GDScriptVariable { Identifier: { } name } when name.Name.StartsWith('_') && uses.GetValueOrDefault(offset + name.Start) == 0:
                    Warn(at, name, "UNUSED_PRIVATE_CLASS_VARIABLE", name.Name);
                    break;
                case GDScriptSignal { Identifier: { } name } when uses.GetValueOrDefault(offset + name.Start) == 0 && !analysis.SignalsNamed.Contains(name.Name):
                    Warn(at, name, "UNUSED_SIGNAL", name.Name);
                    break;
                case GDScriptClass { Identifier: not null } inner:
                    ClassWarnings([.. inner.Members.Select(m => new GDScriptMember(m, offset))], analysis, uses);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>How many names of the script refer to each of its declarations (by the offset its name starts at), the
    /// declaring name itself not among them.</summary>
    private static Dictionary<int, int> Uses(GDScriptAnalysis analysis)
    {
        Dictionary<int, int> uses = [];
        foreach ((GDScriptIdentifier name, GDScriptDeclaration declared) in analysis.Declared)
        {
            if (declared.Script == analysis.Path && !analysis.Declaring.Contains(name))
            {
                uses[declared.Start] = uses.GetValueOrDefault(declared.Start) + 1;
            }
        }
        return uses;
    }

    /// <summary>Whether a declaration names a function of a project's script that waits (an <c>await</c> in its own body,
    /// not a lambda's): a coroutine, which Godot asks to be awaited.</summary>
    private bool IsCoroutine(GDScriptDeclaration? declared)
    {
        if (declared is not { Script: { } script } || Tree(script) is not { } tree)
        {
            return false;
        }
        return Coroutines.GetValue(tree, static t =>
        {
            HashSet<int> starts = [];
            foreach (GDScriptMember member in t.Members)
            {
                Collect(member.Node, member.Offset, starts);
            }
            return starts;
        }).Contains(declared.Start);

        static void Collect(GDScriptNode node, int offset, HashSet<int> starts)
        {
            if (node is GDScriptFunction { Identifier: { } name, Body: { } body, IsLambda: false } && Waits(body))
            {
                starts.Add(offset + name.Start);
            }
            if (node is GDScriptClass inner)
            {
                foreach (GDScriptNode member in inner.Members)
                {
                    Collect(member, offset, starts);
                }
            }
        }

        static bool Waits(GDScriptNode node) => node is GDScriptAwait || node is not GDScriptLambda && node.Children.Any(Waits);
    }
}
