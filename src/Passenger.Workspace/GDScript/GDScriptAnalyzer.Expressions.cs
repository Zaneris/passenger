using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>The walk of a script's members and bodies, and each expression's type as Godot's reduce_* functions give it.</summary>
public sealed partial class GDScriptAnalyzer
{
    private void Walk(GDScriptAnalysis analysis, GDScriptNode member, Context context)
    {
        switch (member)
        {
            case GDScriptVariable variable:
                GDScriptType type = variable.IsStatic || variable.Identifier is null ? AssignableType(variable, context, false) : MemberType(variable, context.Class, context.Offset);
                Record(analysis, variable, type, context);
                if (variable.Initializer is { } value)
                {
                    Reduce(value, context);
                }
                if (variable.Getter is { Body: { } getter } get)
                {
                    WalkSuite(getter, context with { Scope = new Scope(null), Function = get });
                }
                if (variable.Setter is { Body: { } setter } set)
                {
                    Scope scope = new(null);
                    if (set.Parameters is [{ Identifier: { } parameter } only])
                    {
                        scope.Add(parameter.Name, type, new GDScriptDeclaration(context.Analysis.Path, context.Offset + parameter.Start, parameter.Name, type));
                        Record(analysis, only, type, context);
                    }
                    WalkSuite(setter, context with { Scope = scope, Function = set });
                }
                break;
            case GDScriptConstant constant:
                Record(analysis, constant, MemberType(constant, context.Class, context.Offset), context);
                if (constant.Initializer is { } constantValue)
                {
                    Reduce(constantValue, context);
                }
                break;
            case GDScriptFunction function:
                WalkFunction(function, context);
                break;
            case GDScriptClass inner when inner.Identifier is not null:
                GDScriptType innerType = new(GDScriptTypeKind.Class, inner.Identifier.Name) { Class = inner, Script = context.Analysis.Path, Offset = context.Offset };
                foreach (GDScriptNode innerMember in inner.Members)
                {
                    Walk(analysis, innerMember, context with { Class = innerType });
                }
                break;
            case GDScriptSignal signal:
                foreach (GDScriptParameter parameter in signal.Parameters)
                {
                    Record(analysis, parameter, parameter.Type is { } written ? ResolveType(written, context) : GDScriptType.Variant.Soft, context);
                }
                break;
            default:
                break;
        }
    }

    private void WalkFunction(GDScriptFunction function, Context context)
    {
        Scope scope = new(context.Scope);
        Context inside = context with { Scope = scope, Function = function };
        if (context.Analysis.Tree is not null)
        {
            context.Analysis.Types[function] = function.IsLambda ? GDScriptType.Variant.Soft : ReturnType(function, context.Class, context.Offset);
            if (function.Identifier is { } named && !function.IsLambda)
            {
                context.Analysis.Declared[named] = new GDScriptDeclaration(context.Analysis.Path, context.Offset + named.Start, named.Name, context.Analysis.Types[function]);
            }
        }
        foreach (GDScriptParameter parameter in function.Rest is { } rest ? [.. function.Parameters, rest] : function.Parameters)
        {
            GDScriptType type = parameter == function.Rest ? GDScriptType.Builtin("Array") : AssignableType(parameter, inside, constant: false);
            Record(inside.Analysis, parameter, type, inside);
            if (parameter.Identifier is { } name)
            {
                scope.Add(name.Name, type, new GDScriptDeclaration(context.Analysis.Path, context.Offset + name.Start, name.Name, type));
            }
        }
        if (function.Body is { } body)
        {
            WalkSuite(body, inside);
        }
    }

    private void WalkSuite(GDScriptSuite suite, Context context)
    {
        Scope scope = new(context.Scope);
        Context inside = context with { Scope = scope };
        foreach (GDScriptNode statement in suite.Statements)
        {
            WalkStatement(statement, inside, scope);
        }
    }

    private void WalkStatement(GDScriptNode statement, Context context, Scope scope)
    {
        GDScriptAnalysis analysis = context.Analysis;
        switch (statement)
        {
            case GDScriptVariable { Identifier: { } name } variable:
                GDScriptType type = AssignableType(variable, context, constant: false);
                Record(analysis, variable, type, context);
                scope.Add(name.Name, type, new GDScriptDeclaration(analysis.Path, context.Offset + name.Start, name.Name, type));
                break;
            case GDScriptConstant { Identifier: { } name } constant:
                GDScriptType constantType = AssignableType(constant, context, constant: true);
                Record(analysis, constant, constantType, context);
                scope.Add(name.Name, constantType, new GDScriptDeclaration(analysis.Path, context.Offset + name.Start, name.Name, constantType));
                break;
            case GDScriptIf branch:
                Reduce(branch.Condition, context);
                WalkBlock(branch.True, context);
                WalkBlock(branch.False, context);
                break;
            case GDScriptWhile loop:
                Reduce(loop.Condition, context);
                WalkBlock(loop.Loop, context);
                break;
            case GDScriptFor loop:
                GDScriptType list = Reduce(loop.List, context);
                Scope iteration = new(scope);
                if (loop.Variable is { } variableName)
                {
                    GDScriptType element = loop.Type is { } declared ? ResolveType(declared, context) : Element(list, loop.List);
                    analysis.Types[loop.Variable] = element;
                    iteration.Add(variableName.Name, element, new GDScriptDeclaration(analysis.Path, context.Offset + variableName.Start, variableName.Name, element));
                }
                if (loop.Loop is { } body)
                {
                    WalkSuite(body, context with { Scope = iteration });
                }
                break;
            case GDScriptMatch match:
                Reduce(match.Test, context);
                foreach (GDScriptMatchBranch branch in match.Branches)
                {
                    Scope binds = new(scope);
                    foreach (GDScriptPattern pattern in branch.Patterns)
                    {
                        Bind(pattern, binds, context);
                    }
                    if (branch.Guard is { } guard)
                    {
                        foreach (GDScriptNode condition in guard.Statements)
                        {
                            Reduce(condition as GDScriptExpression, context with { Scope = binds });
                        }
                    }
                    if (branch.Block is { } block)
                    {
                        WalkSuite(block, context with { Scope = binds });
                    }
                }
                break;
            case GDScriptReturn returned:
                Reduce(returned.Value, context);
                break;
            case GDScriptAssert assertion:
                Reduce(assertion.Condition, context);
                Reduce(assertion.Message, context);
                break;
            case GDScriptExpression expression:
                Reduce(expression, context);
                break;
            default:
                break;
        }
    }

    private void WalkBlock(GDScriptSuite? block, Context context)
    {
        if (block is not null)
        {
            WalkSuite(block, context);
        }
    }

    private void Bind(GDScriptPattern pattern, Scope binds, Context context)
    {
        if (pattern.Bind is { } name)
        {
            context.Analysis.Types[name] = GDScriptType.Variant.Soft;
            binds.Add(name.Name, GDScriptType.Variant.Soft, new GDScriptDeclaration(context.Analysis.Path, context.Offset + name.Start, name.Name, GDScriptType.Variant.Soft));
        }
        Reduce(pattern.Expression, context);
        foreach (GDScriptPattern inner in pattern.Array)
        {
            Bind(inner, binds, context);
        }
        foreach ((GDScriptExpression? key, GDScriptPattern? value) in pattern.Dictionary)
        {
            Reduce(key, context);
            if (value is not null)
            {
                Bind(value, binds, context);
            }
        }
    }

    private static void Record(GDScriptAnalysis analysis, GDScriptAssignable node, GDScriptType type, Context context)
    {
        analysis.Types[node] = type;
        if (node.Identifier is { } name && analysis.Tree is not null)
        {
            analysis.Types[name] = type;
            analysis.Declared[name] = new GDScriptDeclaration(analysis.Path, context.Offset + name.Start, name.Name, type);
            analysis.Offsets[node] = context.Offset;
        }
    }

    /// <summary>What a <c>for</c> over a value gives each turn: an int's or a range's int, a typed array's element, a
    /// string's String, a packed array's element, a dictionary's key (Variant).</summary>
    private GDScriptType Element(GDScriptType list, GDScriptExpression? written)
    {
        if (written is GDScriptCall { Callee: GDScriptIdentifier { Name: "range" } })
        {
            return GDScriptType.Builtin("int");
        }
        GDScriptType element = list switch
        {
            { Kind: GDScriptTypeKind.Builtin, Name: "int" or "float" or "String" } => GDScriptType.Builtin(list.Name),
            { Kind: GDScriptTypeKind.Builtin, Name: "Array", Elements: [{ } typed, ..] } => typed,
            { Kind: GDScriptTypeKind.Builtin, Name: "Dictionary", Elements: [{ } key, ..] } => key,
            { Kind: GDScriptTypeKind.Builtin } builtin when Engine?.Builtins.GetValueOrDefault(builtin.Name)?.Indexing is { } indexed && builtin.Name.StartsWith("Packed", StringComparison.Ordinal) => FromApi(indexed),
            _ => GDScriptType.Variant,
        };
        return element with { Hard = list.Hard && element.Hard };
    }

    /// <summary>An expression's type, recorded for it in the analysis where one is being made.</summary>
    private GDScriptType Reduce(GDScriptExpression? expression, Context context)
    {
        if (expression is null)
        {
            return GDScriptType.Variant;
        }
        GDScriptType type = ReduceOnce(expression, context);
        if (context.Analysis.Tree is not null)
        {
            context.Analysis.Types[expression] = type;
        }
        return type;
    }

    private GDScriptType ReduceOnce(GDScriptExpression expression, Context context)
    {
        switch (expression)
        {
            case GDScriptLiteral literal:
                return literal.Kind switch
                {
                    GDScriptLiteralKind.Int => GDScriptType.Builtin("int"),
                    GDScriptLiteralKind.Float => GDScriptType.Builtin("float"),
                    GDScriptLiteralKind.String => GDScriptType.Builtin("String"),
                    GDScriptLiteralKind.StringName => GDScriptType.Builtin("StringName"),
                    GDScriptLiteralKind.NodePath => GDScriptType.Builtin("NodePath"),
                    GDScriptLiteralKind.Bool => GDScriptType.Builtin("bool"),
                    _ => GDScriptType.Builtin("Nil"),
                };
            case GDScriptSelf:
                return context.Class.Instance;
            case GDScriptIdentifier identifier:
                return Identifier(identifier, context);
            case GDScriptSubscript { IsAttribute: true } attribute:
                GDScriptType owner = Reduce(attribute.Base, context);
                return attribute.Attribute is { } name ? Attribute(owner, name, context) : GDScriptType.Variant;
            case GDScriptSubscript subscript:
                GDScriptType indexed = Reduce(subscript.Base, context);
                Reduce(subscript.Index, context);
                return Indexed(indexed);
            case GDScriptCall call:
                return Call(call, context);
            case GDScriptAwait awaited:
                GDScriptType value = Reduce(awaited.Awaited, context);
                return awaited.Awaited is GDScriptCall && !(value is { Kind: GDScriptTypeKind.Builtin, Name: "Signal" }) ? value : GDScriptType.Variant.Soft;
            case GDScriptBinary binary:
                return Binary(binary, context);
            case GDScriptUnary unary:
                GDScriptType operand = Reduce(unary.Operand, context);
                if (unary.Operator is K.Not or K.Bang)
                {
                    return GDScriptType.Builtin("bool", operand.Hard || unary.Operand is GDScriptTypeTest);
                }
                return Operation(operand, unary.Operator switch { K.Minus => "unary-", K.Plus => "unary+", _ => "~" }, null);
            case GDScriptTernary ternary:
                Reduce(ternary.Condition, context);
                GDScriptType whenTrue = Reduce(ternary.True, context);
                GDScriptType whenFalse = Reduce(ternary.False, context);
                GDScriptType result = whenTrue.IsVariant || whenFalse.IsVariant ? GDScriptType.Variant
                    : Compatible(whenTrue, whenFalse) ? whenTrue : Compatible(whenFalse, whenTrue) ? whenFalse : GDScriptType.Variant;
                return result with { Hard = whenTrue.Hard && whenFalse.Hard };
            case GDScriptCast cast:
                Reduce(cast.Operand, context);
                return cast.Type is { } castTo ? ResolveType(castTo, context) : GDScriptType.Variant;
            case GDScriptTypeTest test:
                Reduce(test.Operand, context);
                return GDScriptType.Builtin("bool");
            case GDScriptArray array:
                foreach (GDScriptExpression element in array.Elements)
                {
                    Reduce(element, context);
                }
                return GDScriptType.Builtin("Array");
            case GDScriptDictionary dictionary:
                foreach ((GDScriptExpression key, GDScriptExpression item) in dictionary.Elements)
                {
                    if (!dictionary.LuaTable)
                    {
                        Reduce(key, context);
                    }
                    Reduce(item, context);
                }
                return GDScriptType.Builtin("Dictionary");
            case GDScriptGetNode:
                return GDScriptType.Native("Node");
            case GDScriptPreload preload:
                Reduce(preload.Path, context);
                return preload.Path is GDScriptLiteral { Value: string path } ? Preloaded(Absolute(path, context.Analysis.Path)) : GDScriptType.Native("Resource");
            case GDScriptLambda lambda:
                if (lambda.Function is { } function)
                {
                    WalkFunction(function, context);
                }
                return GDScriptType.Builtin("Callable");
            case GDScriptAssignment assignment:
                Reduce(assignment.Assignee, context);
                Reduce(assignment.Value, context);
                return GDScriptType.Variant.Soft;
            default:
                return GDScriptType.Variant.Soft;
        }
    }

    /// <summary>What a preloaded path gives: a script's class (as the type itself), else the resource's class.</summary>
    private GDScriptType Preloaded(string written)
    {
        string path = project.Resolved(written);
        return path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) && Tree(path) is { } tree ? ScriptClass(tree, path) with { Meta = true }
            : project.ClassPath(project.ResourceClass(path)) is { } scripted && Tree(scripted) is { } scriptedTree ? ScriptClass(scriptedTree, scripted)
            : GDScriptType.Native(project.ResourceClass(path));
    }

    private GDScriptType Identifier(GDScriptIdentifier identifier, Context context)
    {
        GDScriptAnalysis analysis = context.Analysis;
        if (context.Scope?.Find(identifier.Name) is { } local)
        {
            analysis.Declared[identifier] = local.Declaration;
            return local.Type;
        }
        if (Member(context.Class, identifier.Name) is { } member)
        {
            if (member.Declaration is { } declared)
            {
                analysis.Declared[identifier] = declared;
            }
            return member.Kind == "func" ? GDScriptType.Builtin("Callable") : member.Type;
        }
        if (Engine?.Singletons.GetValueOrDefault(identifier.Name) is { } engineSingleton)
        {
            return GDScriptType.Native(engineSingleton);
        }
        if (GlobalOrScopedType(identifier.Name, context.Class) is { } type)
        {
            if (type is { Kind: GDScriptTypeKind.Class, Script: { } script, Class: { } node })
            {
                analysis.Declared[identifier] = new GDScriptDeclaration(script, type.Offset + (node.Identifier?.Start ?? 0), identifier.Name, type);
            }
            return type;
        }
        if (project.Autoload(identifier.Name) is { } autoload)
        {
            GDScriptType loaded = Tree(autoload) is { } tree ? ScriptClass(tree, autoload) : GDScriptType.Native("Node");
            analysis.Declared[identifier] = new GDScriptDeclaration(autoload, 0, identifier.Name, loaded);
            return loaded;
        }
        if (Engine?.Functions.ContainsKey(identifier.Name) == true)
        {
            return GDScriptType.Builtin("Callable");
        }
        if (identifier.Name is "PI" or "TAU" or "INF" or "NAN")
        {
            return GDScriptType.Builtin("float");
        }
        if (Engine?.GlobalEnumValues.GetValueOrDefault(identifier.Name) is { } owning && Engine.GlobalEnums.GetValueOrDefault(owning) is { } global)
        {
            return new GDScriptType(GDScriptTypeKind.Enum, owning) { Values = global.Values };
        }
        if (Engine?.GlobalConstants.Contains(identifier.Name) == true)
        {
            return GDScriptType.Builtin("int");
        }
        return GDScriptType.Variant.Soft;
    }

    /// <summary>A member's type read off a value or a type (Godot's reduce_identifier_from_base): an enum's value, a
    /// built-in type's constant or member, a class's or engine class's member; a method read as a value is a Callable.</summary>
    private GDScriptType Attribute(GDScriptType owner, GDScriptIdentifier name, Context context)
    {
        GDScriptType found = MemberOf(owner, name.Name, context.Analysis, name);
        return found with { Hard = found.Hard && (owner.Hard || owner.Meta) };
    }

    private GDScriptType MemberOf(GDScriptType owner, string name, GDScriptAnalysis analysis, GDScriptIdentifier? at)
    {
        switch (owner.Kind)
        {
            case GDScriptTypeKind.Enum:
                return owner.Meta && owner.Values?.ContainsKey(name) == true ? owner.Instance : GDScriptType.Variant.Soft;
            case GDScriptTypeKind.Builtin:
                if (Engine?.Builtins.GetValueOrDefault(owner.Name) is not { } builtin || owner.Name == "Dictionary" && !owner.Meta)
                {
                    return GDScriptType.Variant.Soft;
                }
                if (owner.Meta)
                {
                    return builtin.Constants.TryGetValue(name, out string? constant) ? FromApi(constant)
                        : builtin.Enums.Values.FirstOrDefault(e => e.Values.ContainsKey(name)) is { } owning ? NativeEnum(owner.Name, owning, meta: false)
                        : builtin.Enums.GetValueOrDefault(name) is { } named ? NativeEnum(owner.Name, named, meta: true)
                        : builtin.Methods.ContainsKey(name) ? GDScriptType.Builtin("Callable") : GDScriptType.Variant.Soft;
                }
                return builtin.Properties.TryGetValue(name, out string? property) ? FromApi(property)
                    : builtin.Methods.ContainsKey(name) ? GDScriptType.Builtin("Callable") : GDScriptType.Variant.Soft;
            case GDScriptTypeKind.Native or GDScriptTypeKind.Class:
                if (Member(owner, name) is not { } member)
                {
                    return owner.Meta && name == "new" ? GDScriptType.Builtin("Callable") : GDScriptType.Variant.Soft;
                }
                if (at is not null && member.Declaration is { } declared && analysis.Tree is not null)
                {
                    analysis.Declared[at] = declared;
                }
                return member.Kind == "func" ? GDScriptType.Builtin("Callable") : member.Type;
            default:
                return GDScriptType.Variant.Soft;
        }
    }

    private GDScriptType Indexed(GDScriptType indexed)
    {
        GDScriptType element = indexed switch
        {
            { Kind: GDScriptTypeKind.Builtin, Name: "Array", Elements: [{ } typed, ..] } => typed,
            { Kind: GDScriptTypeKind.Builtin, Name: "Dictionary", Elements: [_, { } value, ..] } => value,
            { Kind: GDScriptTypeKind.Builtin, Name: "Array" or "Dictionary" } => GDScriptType.Variant,
            { Kind: GDScriptTypeKind.Builtin } builtin when Engine?.Builtins.GetValueOrDefault(builtin.Name)?.Indexing is { } returned => FromApi(returned),
            _ => GDScriptType.Variant,
        };
        return element with { Hard = element.Hard && indexed.Hard };
    }

    /// <summary>What a call gives (Godot's reduce_call): a built-in type's constructor its type, a function's or method's
    /// return type, the script's own, its base classes', the engine's or a utility function's; <c>new()</c> an instance.</summary>
    private GDScriptType Call(GDScriptCall call, Context context)
    {
        foreach (GDScriptExpression argument in call.Arguments)
        {
            Reduce(argument, context);
        }
        if (call.IsSuper)
        {
            GDScriptType? based = BaseOf(context.Class);
            GDScriptMemberInfo? method = call.FunctionName is { } called && based is not null ? Member(based, called) : null;
            if (call.Callee is GDScriptIdentifier superName && method?.Declaration is { } declared)
            {
                context.Analysis.Declared[superName] = declared;
            }
            return method is { Kind: "func" } ? method.Type : GDScriptType.Variant.Soft;
        }
        switch (call.Callee)
        {
            case GDScriptIdentifier name:
                if (context.Scope?.Find(name.Name) is { } local)
                {
                    context.Analysis.Declared[name] = local.Declaration;
                    context.Analysis.Types[name] = local.Type;
                    return GDScriptType.Variant.Soft;
                }
                if (Engine?.Builtins.ContainsKey(name.Name) == true && name.Name != "Nil" || IsBuiltinName(name.Name))
                {
                    return GDScriptType.Builtin(name.Name);
                }
                if (Member(context.Class, name.Name) is { Kind: "func" } own)
                {
                    if (own.Declaration is { } declared)
                    {
                        context.Analysis.Declared[name] = declared;
                    }
                    context.Analysis.Types[name] = GDScriptType.Builtin("Callable");
                    return own.Type;
                }
                if (Engine?.Functions.GetValueOrDefault(name.Name) is { } function)
                {
                    context.Analysis.Types[name] = GDScriptType.Builtin("Callable");
                    return FromApi(function.Returns);
                }
                Reduce(name, context);
                return GDScriptType.Variant.Soft;
            case GDScriptSubscript { IsAttribute: true, Attribute: { } method } attribute:
                GDScriptType owner = Reduce(attribute.Base, context);
                context.Analysis.Types[method] = GDScriptType.Builtin("Callable");
                return MethodCall(owner, method, context);
            default:
                Reduce(call.Callee, context);
                return GDScriptType.Variant.Soft;
        }
    }

    private GDScriptType MethodCall(GDScriptType owner, GDScriptIdentifier method, Context context)
    {
        if (owner.Meta && method.Name == "new" && owner.Kind is GDScriptTypeKind.Native or GDScriptTypeKind.Class)
        {
            if (owner.Kind == GDScriptTypeKind.Class && Member(owner.Instance, "_init") is { Declaration: { } constructor })
            {
                context.Analysis.Declared[method] = constructor;
            }
            return owner.Instance with { Hard = true };
        }
        GDScriptType returns;
        switch (owner.Kind)
        {
            case GDScriptTypeKind.Builtin:
                GodotApiMethod? builtin = Engine?.Builtins.GetValueOrDefault(owner.Name)?.Methods.GetValueOrDefault(method.Name);
                returns = builtin is null ? GDScriptType.Variant.Soft : FromApi(builtin.Returns);
                if (owner is { Name: "Array", Elements: [{ } element, ..] } && builtin?.Returns == "Variant" && method.Name is "front" or "back" or "pick_random"
                    or "pop_back" or "pop_front" or "pop_at" or "get" or "max" or "min")
                {
                    returns = element;
                }
                if (owner is { Name: "Array" } && method.Name is "duplicate" or "slice" or "filter")
                {
                    returns = owner.Instance;
                }
                break;
            case GDScriptTypeKind.Native or GDScriptTypeKind.Class:
                GDScriptMemberInfo? found = Member(owner, method.Name);
                if (found?.Declaration is { } declared && context.Analysis.Tree is not null)
                {
                    context.Analysis.Declared[method] = declared;
                }
                returns = found is { Kind: "func" } ? found.Type : GDScriptType.Variant.Soft;
                break;
            default:
                returns = GDScriptType.Variant.Soft;
                break;
        }
        return returns with { Hard = returns.Hard && (owner.Hard || owner.Meta) };
    }

    private GDScriptType Binary(GDScriptBinary binary, Context context)
    {
        GDScriptType left = Reduce(binary.Left, context);
        GDScriptType right = Reduce(binary.Right, context);
        bool hard = left.Hard && right.Hard;
        switch (binary.Operator)
        {
            case K.And or K.AmpersandAmpersand or K.Or or K.PipePipe or K.In:
                return GDScriptType.Builtin("bool", hard);
            case K.EqualEqual or K.BangEqual when left.Kind is GDScriptTypeKind.Native or GDScriptTypeKind.Class || right.Kind is GDScriptTypeKind.Native or GDScriptTypeKind.Class:
                return GDScriptType.Builtin("bool", hard);
            default:
                break;
        }
        string op = GDScriptToken.KindName(binary.Operator);
        return Operation(left, op, right) with { Hard = hard };
    }

    /// <summary>What an operator gives on built-in operands, as the engine's operator table says (Variant's
    /// get_operator_return_type); Variant for an operand of no known type.</summary>
    private GDScriptType Operation(GDScriptType left, string op, GDScriptType? right)
    {
        if (left.IsVariant || right is { IsVariant: true })
        {
            return GDScriptType.Variant with { Hard = false };
        }
        string leftName = left.Kind == GDScriptTypeKind.Enum ? "int" : left.Kind == GDScriptTypeKind.Builtin ? left.Name : "Object";
        string rightName = right is null ? string.Empty : right.Kind == GDScriptTypeKind.Enum ? "int" : right.Kind == GDScriptTypeKind.Builtin ? right.Name : "Object";
        if (Engine?.Builtins.GetValueOrDefault(leftName) is { } builtin
            && (builtin.Operators.TryGetValue((op, rightName), out string? returned) || builtin.Operators.TryGetValue((op, "Variant"), out returned)))
        {
            GDScriptType result = FromApi(returned);
            return leftName == "Array" && rightName == "Array" && op == "+" && left.Elements.Count > 0 && left.ToString() == right!.ToString() ? left.Instance : result;
        }
        return op is "==" or "!=" or "<" or "<=" or ">" or ">=" ? GDScriptType.Builtin("bool") : GDScriptType.Variant;
    }

    /// <summary>Whether a value of one type may stand where another is asked for, roughly as Godot's is_type_compatible.</summary>
    private bool Compatible(GDScriptType target, GDScriptType source)
    {
        if (target.IsVariant || target.ToString() == source.ToString())
        {
            return true;
        }
        if (target.Kind == GDScriptTypeKind.Builtin && source.Kind == GDScriptTypeKind.Builtin)
        {
            return target.Name == "float" && source.Name == "int" || target.Name is "String" or "StringName" && source.Name is "String" or "StringName";
        }
        int depth = 0;
        for (GDScriptType? at = source.Instance; at is not null && depth++ < 64; at = BaseOf(at))
        {
            if (at.ToString() == target.ToString())
            {
                return true;
            }
        }
        return false;
    }
}
