namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of what a class declares (resolve_class_inheritance's names, check_class_member_name_conflict,
/// resolve_function_signature's override check, and the parser's export annotations as the analyzer applies them), each said
/// where the class is read whole.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>The export annotations Godot's export_annotations applies, each of which a variable may carry once.</summary>
    private static readonly HashSet<string> ExportAnnotations = new(StringComparer.Ordinal)
    {
        "@export", "@export_enum", "@export_file", "@export_file_path", "@export_dir", "@export_global_file", "@export_global_dir",
        "@export_multiline", "@export_placeholder", "@export_range", "@export_exp_easing", "@export_color_no_alpha", "@export_node_path",
        "@export_flags", "@export_flags_2d_render", "@export_flags_2d_physics", "@export_flags_2d_navigation", "@export_flags_3d_render",
        "@export_flags_3d_physics", "@export_flags_3d_navigation", "@export_flags_avoidance",
    };

    /// <summary>A function's signature as an override is checked against it: its parameters' types, how many have defaults,
    /// whether it takes any number more, what it returns, whether it is static, and whether it is the engine's virtual.</summary>
    private sealed record Signature(IReadOnlyList<GDScriptType> Parameters, int Defaults, bool Varargs, GDScriptType Returns, bool IsStatic, bool Virtual = false);

    /// <summary>A class's name's error: one a built-in type, an engine class, for an inner class a global script class, or a
    /// singleton autoload has.</summary>
    private void ClassNameErrors(GDScriptClass node, bool inner, Context context)
    {
        if (node.Identifier is not { } named || context.Analysis.Tree is null || Engine is null)
        {
            return;
        }
        string name = named.Name;
        string? hides = Engine.Builtins.ContainsKey(name) && name != "Nil" || IsBuiltinName(name) ? "a built-in type"
            : Engine.Classes.ContainsKey(name) ? "a native class"
            : inner && project.ClassPath(name) is not null ? "a global script class"
            : project.Autoload(name) is not null ? "an autoload singleton" : null;
        if (hides is not null)
        {
            Error(context, named, $"Class \"{name}\" hides {hides}.");
        }
    }

    /// <summary>A member's errors where its name is taken (check_class_member_name_conflict): by a member of a script class
    /// it extends, by the engine class's signal, property or constant (or <c>script</c>), by an engine class, or by a built-in
    /// type. Functions, which override, are not checked.</summary>
    private void MemberNameErrors(GDScriptNode member, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        IEnumerable<(string Name, GDScriptNode At)> names = member switch
        {
            GDScriptVariable { Identifier: { } name } => [(name.Name, name)],
            GDScriptConstant { Identifier: { } name } => [(name.Name, name)],
            GDScriptSignal { Identifier: { } name } => [(name.Name, name)],
            // An inner class named as a built-in type or engine class is said to hide it, never to take its name.
            GDScriptClass { Identifier: { } name } when !(Engine?.Classes.ContainsKey(name.Name) == true || Engine?.Builtins.ContainsKey(name.Name) == true || IsBuiltinName(name.Name)) =>
                [(name.Name, name)],
            GDScriptEnum { Identifier: { } name } => [(name.Name, name)],
            GDScriptEnum unnamed => unnamed.Values.Select(v => (v.Name.Name, v.Value ?? (GDScriptNode)unnamed)),
            _ => [],
        };
        foreach ((string name, GDScriptNode at) in names)
        {
            if (NameTaken(name) is { } taken)
            {
                Error(context, at, taken);
            }
        }

        string? NameTaken(string name)
        {
            GDScriptType? parent = BaseOf(context.Class);
            for (int depth = 0; parent is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++, parent = BaseOf(parent))
            {
                if (OwnNames(parent).ContainsKey(name))
                {
                    string owner = parent.Class?.Identifier?.Name ?? parent.Script ?? parent.Name;
                    return $"The member \"{name}\" already exists in parent class {owner}.";
                }
            }
            if (parent is { Kind: GDScriptTypeKind.Native } native && (name == "script" || Engine?.Chain(native.Name).Any(c => c.Signals.ContainsKey(name)
                || c.Properties.ContainsKey(name) || c.Constants.ContainsKey(name) || c.Enums.Values.Any(e => e.Values.ContainsKey(name))) == true))
            {
                return $"Member \"{name}\" redefined (original in native class '{native.Name}')";
            }
            return Engine?.Classes.ContainsKey(name) == true ? $"The member \"{name}\" shadows a native class."
                : Engine?.Builtins.ContainsKey(name) == true && name != "Nil" || IsBuiltinName(name) ? $"The member \"{name}\" cannot have the same name as a builtin type."
                : null;
        }
    }

    /// <summary>An override's error where its signature doesn't match the function of its name in a class it extends:
    /// static where that is not or the other way, a return type written that is no narrower, fewer or more arguments taken,
    /// or a parameter's type narrower than the parent's.</summary>
    private void OverrideErrors(GDScriptFunction function, IReadOnlyList<GDScriptType> parameters, GDScriptType? returns, Context context)
    {
        if (function.Identifier is not { Name: not ("_init" or "_static_init") } named || function.IsLambda || context.Analysis.Tree is null || !Knows(context.Class)
            || ParentSignature(context.Class, named.Name, 0) is not { } parent)
        {
            return;
        }
        bool valid = function.IsStatic == parent.IsStatic;
        if (function.ReturnType is not null && returns is { } own)
        {
            valid &= own.IsVariant ? parent.Returns.IsVariant
                : own.IsVoid ? !parent.Returns.Hard || parent.Returns.IsVoid || parent.Returns is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" }
                : TypeCompatible(parent.Returns, own);
        }
        int least = function.Parameters.Count - function.Parameters.Count(p => p.Initializer is not null);
        int most = function.Rest is null ? function.Parameters.Count : int.MaxValue;
        valid &= least <= parent.Parameters.Count - parent.Defaults && (parent.Varargs ? int.MaxValue : parent.Parameters.Count) <= most;
        for (int i = 0; valid && i < parent.Parameters.Count && i < parameters.Count; i++)
        {
            valid = parent.Parameters[i] is { IsVariant: true, Hard: true } ? parameters[i].IsVariant : TypeCompatible(parameters[i], parent.Parameters[i]);
        }
        if (!valid)
        {
            Error(context, function, $"The function signature doesn't match the parent. Parent signature is \"{Written(named.Name, parent)}\".");
        }
    }

    /// <summary>The signature of the function of a name in the classes a class extends: a script's as written (one with no
    /// return type written returning what the function it overrides returns), an engine class's as the API gives it; null for none.</summary>
    private Signature? ParentSignature(GDScriptType type, string name, int depth)
    {
        GDScriptType? at = BaseOf(type);
        for (; at is not null && depth < 64; depth++, at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                return Engine?.Chain(at.Name).Select(c => c.Methods.GetValueOrDefault(name)).FirstOrDefault(m => m is not null) is { } method
                    ? new Signature([.. method.Parameters.Select(p => FromApi(p.Type) with { Hard = true })], method.Parameters.Count - method.Required, method.Varargs,
                        FromApi(method.Returns) with { Hard = true }, method.IsStatic, method.IsVirtual)
                    : null;
            }
            if (at.Kind != GDScriptTypeKind.Class || !OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) owned))
            {
                continue;
            }
            if (owned.Node is not GDScriptFunction function || at.Script is not { } script)
            {
                return null;
            }
            Context reading = new(new GDScriptAnalysis(null, script), at, owned.Offset, null, function);
            GDScriptType returns = function.ReturnType is { } written
                ? written.IsVoid ? GDScriptType.Builtin("Nil") with { IsVoid = true } : ResolveType(written, reading)
                : ParentSignature(at, name, depth + 1)?.Returns ?? GDScriptType.Variant.Soft;
            return new Signature([.. function.Parameters.Select(p => p.Type is { } typed ? ResolveType(typed, reading) : GDScriptType.Variant.Soft)],
                function.Parameters.Count(p => p.Initializer is not null), function.Rest is not null, returns, function.IsStatic);
        }
        return null;
    }

    /// <summary>An override of no return type written, read as returning what the function it overrides returns (Godot's
    /// resolve_function_signature): nothing for a void one, its hard type, the engine's property list as an untyped Array.</summary>
    private Context Overridden(GDScriptFunction function, Context inside)
    {
        if (function.ReturnType is not null || function.IsLambda || function.Identifier is not { Name: not ("_init" or "_static_init") } named
            || inside.Analysis.Tree is null || !Knows(inside.Class) || ParentSignature(inside.Class, named.Name, 0) is not { } parent)
        {
            return inside;
        }
        if (named.Name == "_get_property_list" && parent.Virtual)
        {
            return inside with { Returns = GDScriptType.Builtin("Array") };
        }
        return !parent.Returns.Hard ? inside
            : parent.Returns.IsVoid || parent.Returns is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" } ? inside with { Voids = true }
            : inside with { Returns = parent.Returns };
    }

    /// <summary>A constructor's, or a static one's, return type written as any but void.</summary>
    private void ConstructorErrors(GDScriptFunction function, Context context)
    {
        if (function.IsLambda || function.ReturnType is not { IsVoid: false } written || context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        string? said = function.Identifier?.Name switch
        {
            "_init" => "Constructor cannot have an explicit return type.",
            "_static_init" => "Static constructor cannot have an explicit return type.",
            _ => null,
        };
        if (said is not null)
        {
            Error(context, written, said);
        }
    }

    /// <summary>A rest parameter's type, an Array, its written type said where it is another or a typed one.</summary>
    private GDScriptType RestType(GDScriptParameter rest, Context context)
    {
        if (rest.Type is { } written && context.Analysis.Tree is not null && Knows(context.Class))
        {
            GDScriptType specified = ResolveType(written, context);
            if (specified is not { Kind: GDScriptTypeKind.Builtin, Name: "Array" })
            {
                Error(context, written, $"The rest parameter type must be \"Array\", but \"{specified}\" is specified.");
            }
            else if (specified.Elements is [{ IsVariant: false }, ..])
            {
                Error(context, written, "Typed arrays are currently not supported for the rest parameter.");
            }
        }
        return GDScriptType.Builtin("Array");
    }

    /// <summary>A signature as Godot's error writes it: <c>f(int, Node = &lt;default&gt;, ...) -&gt; void</c>.</summary>
    private static string Written(string name, Signature signature)
    {
        IEnumerable<string> parameters = signature.Parameters.Select((p, i) => (p.IsVariant || p is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" } ? "Variant" : p.ToString())
            + (i >= signature.Parameters.Count - signature.Defaults ? " = <default>" : string.Empty));
        if (signature.Varargs)
        {
            parameters = parameters.Append("...");
        }
        string returns = signature.Returns.IsVoid || signature.Returns is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" } ? "void" : signature.Returns.ToString();
        return $"{name}({string.Join(", ", parameters)}) -> {returns}";
    }

    /// <summary>A member variable's export annotations' errors: one on a static variable, one after another, and a simple
    /// <c>@export</c> of a variable of no type or value, of an object neither a resource nor a node, or of a node in a class
    /// that is none.</summary>
    private void ExportErrors(GDScriptVariable variable, GDScriptType type, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        bool exported = false;
        foreach (GDScriptAnnotation annotation in variable.Annotations.Where(a => ExportAnnotations.Contains(a.Name)))
        {
            if (variable.IsStatic)
            {
                Error(context, annotation, $"Annotation \"{annotation.Name}\" cannot be applied to a static variable.");
                continue;
            }
            if (exported)
            {
                Error(context, annotation, $"Annotation \"{annotation.Name}\" cannot be used with another \"@export\" annotation.");
                continue;
            }
            exported = true;
            if (annotation.Name == "@export")
            {
                SimpleExportErrors(variable, type, annotation, context);
            }
        }
    }

    /// <summary>A simple <c>@export</c>'s errors, by the variable's type or its value's (an array's by its element's).</summary>
    /// <summary>A class's properties' getter and setter functions named (<c>set = f, get = g</c>) that Godot refuses, as its
    /// resolve_class_body checks them once the class's functions are read: one the class does not declare, one whose
    /// parameters do not fit, one of a type the property's takes none of, and for a property of no type a getter and a
    /// setter of types that do not fit each other.</summary>
    private void PropertyErrors(IEnumerable<(GDScriptNode Node, int Offset)> members, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        List<(GDScriptNode Node, int Offset)> all = [.. members];
        Dictionary<string, GDScriptFunction> functions = new(StringComparer.Ordinal);
        foreach ((GDScriptNode node, _) in all)
        {
            if (node is GDScriptFunction { Identifier: { } name } function)
            {
                functions.TryAdd(name.Name, function);
            }
        }
        bool newer = Engine?.Version >= new Version(4, 8);
        foreach ((GDScriptNode node, int offset) in all)
        {
            if (node is not GDScriptVariable { GetterName: not null } and not GDScriptVariable { SetterName: not null } || node is not GDScriptVariable variable)
            {
                continue;
            }
            Context at = context with { Offset = offset };
            GDScriptType property = MemberType(variable, context.Class, offset);
            bool sure = SureDeclared(variable) || variable is { Type: null, Infers: false };
            GDScriptType? got = null;
            GDScriptType? set = null;
            if (variable.GetterName is { } getterName)
            {
                if (!functions.TryGetValue(getterName.Name, out GDScriptFunction? getter))
                {
                    Error(at, variable, $"Getter \"{getterName.Name}\" not found.");
                }
                else if (getter.Parameters.Count != 0)
                {
                    Error(at, variable, $"Function \"{getterName.Name}\" cannot be used as getter because of its signature.");
                }
                else if (GetterType(getter, newer, at) is { } returns)
                {
                    if (sure && !TypeCompatible(property, returns, implicitly: true))
                    {
                        Error(at, variable, $"Function with return type \"{returns}\" cannot be used as getter for a property of type \"{property}\".");
                    }
                    else
                    {
                        got = returns;
                    }
                }
            }
            if (variable.SetterName is { } setterName)
            {
                if (!functions.TryGetValue(setterName.Name, out GDScriptFunction? setter))
                {
                    Error(at, variable, $"Setter \"{setterName.Name}\" not found.");
                }
                else if (setter.Parameters.Count != 1)
                {
                    Error(at, variable, $"Function \"{setterName.Name}\" cannot be used as setter because of its signature.");
                }
                else if (SetterType(setter.Parameters[0], at) is { } takes)
                {
                    if (sure && !TypeCompatible(property, takes, implicitly: true))
                    {
                        Error(at, variable, $"Function with argument type \"{takes}\" cannot be used as setter for a property of type \"{property}\".");
                    }
                    else
                    {
                        set = takes;
                    }
                }
            }
            if (property.IsVariant && got is not null && set is not null && !TypeCompatible(got, set, implicitly: true))
            {
                Error(at, variable, $"Getter with type \"{got}\" cannot be used along with setter of type \"{set}\".");
            }
        }
    }

    /// <summary>The type a getter function returns as Godot's check reads it: written; else from Godot 4.8 Variant, and
    /// before it what its body's last return gives where this reading is sure of it, else Variant; null where unsure.</summary>
    private GDScriptType? GetterType(GDScriptFunction getter, bool newer, Context context)
    {
        if (getter.ReturnType is { } written)
        {
            return ResolveType(written, context with { Analysis = new GDScriptAnalysis(null, context.Analysis.Path) }) with { Meta = false };
        }
        if (newer || getter.Body is not { } body)
        {
            return GDScriptType.Variant;
        }
        // Godot's suite takes the type of the last statement that may return, whatever came before.
        GDScriptNode? last = body.Statements.LastOrDefault(s => s is GDScriptReturn or GDScriptIf or GDScriptFor or GDScriptWhile or GDScriptMatch);
        return last switch
        {
            null => GDScriptType.Variant,
            GDScriptReturn { Value: GDScriptLiteral or GDScriptIdentifier } returned when SureReturn(returned.Value, getter, context) => context.Analysis.TypeOf(returned.Value),
            _ => null,
        };
    }

    /// <summary>Whether a getter's returned value is one Passenger is sure of the type of outside its body: a literal, or a
    /// member's name no local or parameter of the function takes.</summary>
    private bool SureReturn(GDScriptExpression value, GDScriptFunction getter, Context context) => value switch
    {
        GDScriptLiteral => Sure(value, context),
        GDScriptIdentifier name => !getter.Parameters.Any(p => p.Identifier?.Name == name.Name)
            && !Within(getter.Body!).Any(d => d is GDScriptAssignable { Identifier.Name: var held } && held == name.Name || d is GDScriptFor { Variable.Name: var looped } && looped == name.Name)
            && Sure(value, context),
        _ => false,
    };

    /// <summary>A node's nodes below it, every level down.</summary>
    private static IEnumerable<GDScriptNode> Within(GDScriptNode node) => node.Children.SelectMany(child => Within(child).Prepend(child));

    /// <summary>The type a setter function's parameter takes: written or inferred from its default, else Variant; null for a
    /// default this reading is unsure of.</summary>
    private GDScriptType? SetterType(GDScriptParameter parameter, Context context) =>
        parameter.Type is not null || parameter.Infers && SureDeclared(parameter) ? context.Analysis.TypeOf(parameter)
        : parameter.Infers ? null : GDScriptType.Variant;

    /// <summary>A member variable's <c>@onready</c> annotations' errors, as Godot's onready_annotation gives them: in a
    /// class built on no Node, on a static variable, or written again.</summary>
    private void OnreadyErrors(GDScriptVariable variable, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class) || NativeBase(context.Class) is not { } native)
        {
            return;
        }
        bool onready = false;
        foreach (GDScriptAnnotation annotation in variable.Annotations.Where(a => a.Name == "@onready"))
        {
            if (Engine?.Extends(native, "Node") == false)
            {
                Error(context, annotation, "\"@onready\" can only be used in classes that inherit \"Node\".");
            }
            else if (variable.IsStatic)
            {
                Error(context, annotation, "\"@onready\" annotation cannot be applied to a static variable.");
            }
            else if (onready)
            {
                Error(context, annotation, "\"@onready\" annotation can only be used once per variable.");
            }
            else
            {
                onready = true;
            }
        }
    }

    private void SimpleExportErrors(GDScriptVariable variable, GDScriptType type, GDScriptAnnotation annotation, Context context)
    {
        if (variable.Type is null && variable.Initializer is null)
        {
            Error(context, annotation, "Cannot use simple \"@export\" annotation with variable without type or initializer, since type can't be inferred.");
            return;
        }
        GDScriptType exported = type.IsVariant && variable.Initializer is { } value && variable.Type is null && Sure(value, context) ? context.Analysis.TypeOf(value) : type;
        if (exported is { Kind: GDScriptTypeKind.Builtin, Name: "Array", Elements: [{ } element, ..] })
        {
            exported = element;
        }
        if (exported.Kind is not (GDScriptTypeKind.Native or GDScriptTypeKind.Class) || exported.Meta || !Whole(exported) || NativeBase(exported) is not { } native)
        {
            return;
        }
        if (Engine?.Extends(native, "Resource") == true)
        {
            return;
        }
        if (Engine?.Extends(native, "Node") != true)
        {
            Error(context, annotation, "Export type can only be built-in, a resource, a node, or an enum.");
        }
        else if (NativeBase(context.Class) is { } own && Engine?.Extends(own, "Node") != true && BaseOf(context.Class) is { } based)
        {
            Error(context, annotation, $"Node export is only supported in Node-derived classes, but the current class inherits \"{based}\".");
        }
    }
}
