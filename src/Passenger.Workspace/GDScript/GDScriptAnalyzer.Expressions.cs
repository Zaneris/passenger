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
                Declaring(variable, type, "variable", local: false, context);
                MemberVariableWarnings(variable, context);
                if (variable.Type is { } memberType)
                {
                    ResolveType(memberType, context);
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
                        scope.Add(parameter.Name, type, new GDScriptDeclaration(context.Analysis.Path, context.Offset + parameter.Start, parameter.Name, type), new Local(only, parameter, LocalKind.Setter));
                        Record(analysis, only, type, context);
                    }
                    WalkSuite(setter, context with { Scope = scope, Function = set });
                }
                break;
            case GDScriptConstant constant:
                GDScriptType constantType = MemberType(constant, context.Class, context.Offset);
                Record(analysis, constant, constantType, context);
                if (constant.Initializer is { } constantValue)
                {
                    Reduce(constantValue, context);
                }
                Declaring(constant, constantType, "constant", local: false, context);
                break;
            case GDScriptFunction function:
                WalkFunction(function, context);
                break;
            case GDScriptClass inner when inner.Identifier is not null:
                GDScriptType innerType = new(GDScriptTypeKind.Class, inner.Identifier.Name) { Class = inner, Script = context.Analysis.Path, Offset = context.Offset };
                Paint(context, inner.Identifier, GDScriptColour.Class);
                PaintExtends(inner, context);
                if (analysis.Tree is { } own)
                {
                    MissingTool(inner, own, analysis.Path, context.Offset, context);
                }
                foreach (GDScriptNode innerMember in inner.Members)
                {
                    Walk(analysis, innerMember, context with { Class = innerType });
                }
                break;
            case GDScriptSignal signal:
                Paint(context, signal.Identifier, GDScriptColour.Event);
                foreach (GDScriptParameter parameter in signal.Parameters)
                {
                    Record(analysis, parameter, parameter.Type is { } written ? ResolveType(written, context) : GDScriptType.Variant.Soft, context);
                    if (parameter.Type is null && parameter.Identifier is { } untyped)
                    {
                        Warn(context, parameter, "UNTYPED_DECLARATION", "Parameter", untyped.Name);
                    }
                }
                break;
            case GDScriptEnum { Identifier: null } named:
                foreach ((GDScriptIdentifier enumValue, _) in named.Values)
                {
                    Paint(context, enumValue, GDScriptColour.Constant);
                    Shadowing(enumValue, "enum member", local: false, context);
                }
                break;
            case GDScriptEnum enumerated:
                Paint(context, enumerated.Identifier, GDScriptColour.Enum);
                foreach ((GDScriptIdentifier enumValue, _) in enumerated.Values)
                {
                    Paint(context, enumValue, GDScriptColour.EnumMember);
                }
                break;
            default:
                break;
        }
    }

    private void WalkFunction(GDScriptFunction function, Context context)
    {
        Scope scope = new(context.Scope) { Function = true };
        Context inside = context with { Scope = scope, Function = function };
        if (context.Analysis.Tree is not null)
        {
            context.Analysis.Types[function] = function.IsLambda ? GDScriptType.Variant.Soft : ReturnType(function, context.Class, context.Offset);
            if (function.Identifier is { } named && !function.IsLambda)
            {
                context.Analysis.Declared[named] = new GDScriptDeclaration(context.Analysis.Path, context.Offset + named.Start, named.Name, context.Analysis.Types[function]);
                context.Analysis.Declaring.Add(named);
                Paint(context, named, GDScriptColour.Method);
            }
        }
        List<Local> parameters = [];
        foreach (GDScriptParameter parameter in function.Rest is { } rest ? [.. function.Parameters, rest] : function.Parameters)
        {
            GDScriptType type = parameter == function.Rest ? GDScriptType.Builtin("Array") : AssignableType(parameter, inside, constant: false);
            Record(inside.Analysis, parameter, type, inside);
            if (parameter.Identifier is { } name)
            {
                Local local = new(parameter, name, LocalKind.Parameter);
                parameters.Add(local);
                scope.Add(name.Name, type, new GDScriptDeclaration(context.Analysis.Path, context.Offset + name.Start, name.Name, type), local);
                Declaring(parameter, type, "parameter", local: true, inside);
                Shadowing(name, "function parameter", local: true, inside);
            }
        }
        if (function.ReturnType is { } returned && !returned.IsVoid)
        {
            ResolveType(returned, inside);
        }
        if (function.ReturnType is null && !function.IsLambda && context.Analysis.Tree is not null)
        {
            Warn(context, function.Start, function.HeaderEnd, "UNTYPED_DECLARATION", "Function", FunctionName(function));
        }
        if (function.Body is { } body && !function.IsAbstract)
        {
            bool returns = WalkSuite(body, inside);
            FunctionWarnings(function, parameters, returns, inside);
        }
    }

    /// <summary>A suite's statements walked in a scope of their own; whether every way through it returns, as Godot's
    /// parser marks a suite (<c>has_return</c>).</summary>
    private bool WalkSuite(GDScriptSuite suite, Context context)
    {
        Dictionary<string, string> declared = new(StringComparer.Ordinal);
        foreach (GDScriptNode statement in suite.Statements)
        {
            if (statement is GDScriptAssignable { Identifier: { } named } assignable and (GDScriptVariable or GDScriptConstant))
            {
                declared.TryAdd(named.Name, assignable is GDScriptConstant ? "constant" : "variable");
            }
        }
        Scope scope = new(context.Scope) { Suite = declared };
        Context inside = context with { Scope = scope };
        bool returns = false;
        bool unreachable = false;
        foreach (GDScriptNode statement in suite.Statements)
        {
            if (returns && !unreachable && context.Function is { } function)
            {
                unreachable = true;
                Warn(context, statement, "UNREACHABLE_CODE", FunctionName(function));
            }
            returns |= WalkStatement(statement, inside, scope);
        }
        UnusedLocals(scope, context);
        return returns;
    }

    /// <summary>A statement walked; whether every way through it returns.</summary>
    private bool WalkStatement(GDScriptNode statement, Context context, Scope scope)
    {
        GDScriptAnalysis analysis = context.Analysis;
        switch (statement)
        {
            case GDScriptVariable { Identifier: { } name } variable:
                ConfusableDeclaration(name, scope, context);
                GDScriptType type = AssignableType(variable, context, constant: false);
                Record(analysis, variable, type, context);
                Local declared = new(variable, name, LocalKind.Variable) { Assignments = variable.Initializer is null ? 0 : 1 };
                scope.Add(name.Name, type, new GDScriptDeclaration(analysis.Path, context.Offset + name.Start, name.Name, type), declared);
                Declaring(variable, type, "variable", local: true, context);
                return false;
            case GDScriptConstant { Identifier: { } name } constant:
                ConfusableDeclaration(name, scope, context);
                GDScriptType constantType = AssignableType(constant, context, constant: true);
                Record(analysis, constant, constantType, context);
                scope.Add(name.Name, constantType, new GDScriptDeclaration(analysis.Path, context.Offset + name.Start, name.Name, constantType), new Local(constant, name, LocalKind.Constant));
                Declaring(constant, constantType, "constant", local: true, context);
                return false;
            case GDScriptIf branch:
                Reduce(branch.Condition, context);
                bool whenTrue = branch.True is { } yes && WalkSuite(yes, context);
                bool whenFalse = branch.False is { } no && WalkSuite(no, context);
                return whenTrue && whenFalse;
            case GDScriptWhile loop:
                Reduce(loop.Condition, context);
                WalkBlock(loop.Loop, context);
                return false;
            case GDScriptFor loop:
                GDScriptType list = Reduce(loop.List, context);
                Scope iteration = new(scope);
                if (loop.Variable is { } variableName)
                {
                    GDScriptType element = loop.Type is { } declaredType ? ResolveType(declaredType, context) : Element(list, loop.List);
                    analysis.Types[loop.Variable] = element;
                    Paint(context, variableName, GDScriptColour.Local);
                    iteration.Add(variableName.Name, element, new GDScriptDeclaration(analysis.Path, context.Offset + variableName.Start, variableName.Name, element), new Local(loop, variableName, LocalKind.Iterator));
                    if (loop.Type is null)
                    {
                        Warn(context, variableName, element.Hard && !element.IsVariant ? "INFERRED_DECLARATION" : "UNTYPED_DECLARATION", "\"for\" iterator variable", variableName.Name);
                    }
                }
                if (loop.Loop is { } body)
                {
                    WalkSuite(body, context with { Scope = iteration });
                }
                if (loop.Variable is { } iterated)
                {
                    Shadowing(iterated, "\"for\" iterator variable", local: true, context);
                }
                return false;
            case GDScriptMatch match:
                Reduce(match.Test, context);
                bool all = match.Branches.Count > 0;
                bool wildcard = false;
                foreach (GDScriptMatchBranch branch in match.Branches)
                {
                    if (wildcard && branch.Patterns.Count > 0)
                    {
                        Warn(context, branch.Patterns[0], "UNREACHABLE_PATTERN");
                    }
                    wildcard |= branch.Guard is null && branch.Patterns.Any(p => p.Kind is GDScriptPatternKind.Bind or GDScriptPatternKind.Wildcard);
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
                    bool returned = branch.Block is { } block && WalkSuite(block, context with { Scope = binds });
                    all &= returned;
                    if (branch.Patterns.Count == 1)
                    {
                        UnusedLocals(binds, context);
                    }
                }
                return all && wildcard;
            case GDScriptReturn returned:
                Reduce(returned.Value, context);
                return true;
            case GDScriptAssert assertion:
                Reduce(assertion.Condition, context);
                Reduce(assertion.Message, context);
                if (Constant(assertion.Condition) is { } constantCondition)
                {
                    if (Truthy(constantCondition))
                    {
                        Warn(context, assertion.Condition!, "ASSERT_ALWAYS_TRUE");
                    }
                    else if (assertion.Condition is not GDScriptLiteral { Kind: GDScriptLiteralKind.Bool })
                    {
                        Warn(context, assertion.Condition!, "ASSERT_ALWAYS_FALSE");
                    }
                }
                return false;
            case GDScriptExpression expression:
                Reduce(expression, context, root: true);
                Standalone(expression, context);
                return false;
            default:
                return false;
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
            Paint(context, name, GDScriptColour.Local);
            binds.Add(name.Name, GDScriptType.Variant.Soft, new GDScriptDeclaration(context.Analysis.Path, context.Offset + name.Start, name.Name, GDScriptType.Variant.Soft), new Local(name, name, LocalKind.Bind));
            Shadowing(name, "pattern bind", local: true, context);
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
            analysis.Declaring.Add(name);
            Paint(context, name, node switch
            {
                GDScriptParameter => GDScriptColour.Parameter,
                GDScriptConstant => GDScriptColour.Constant,
                _ when context.Function is not null || context.Scope is not null => GDScriptColour.Local,
                _ => GDScriptColour.Field,
            });
            if (node.Type is { Chain: [{ Name: "Variant" }] })
            {
                analysis.WrittenVariant.Add(context.Offset + name.Start);
            }
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
    private GDScriptType Reduce(GDScriptExpression? expression, Context context, bool root = false, bool awaited = false)
    {
        if (expression is null)
        {
            return GDScriptType.Variant;
        }
        GDScriptType type = ReduceOnce(expression, context, root, awaited);
        if (context.Analysis.Tree is not null)
        {
            context.Analysis.Types[expression] = type;
        }
        return type;
    }

    private GDScriptType ReduceOnce(GDScriptExpression expression, Context context, bool root, bool awaited)
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
                if (attribute.Attribute is { } named && Unsafe(owner, named.Name))
                {
                    Warn(context, attribute, "UNSAFE_PROPERTY_ACCESS", named.Name, owner.Name);
                }
                TemporaryModification(null, attribute, context);
                return attribute.Attribute is { } name ? Attribute(owner, name, context) : GDScriptType.Variant;
            case GDScriptSubscript subscript:
                GDScriptType indexed = Reduce(subscript.Base, context);
                Reduce(subscript.Index, context);
                return Indexed(indexed);
            case GDScriptCall call:
                return Call(call, context, root, awaited);
            case GDScriptAwait waiting:
                GDScriptType value = Reduce(waiting.Awaited, context, awaited: true);
                bool coroutine = waiting.Awaited is GDScriptCall waited && context.Analysis.CoroutineCalls.Contains(waited);
                if (!coroutine && !value.IsVariant && value is not { Kind: GDScriptTypeKind.Builtin, Name: "Signal" })
                {
                    Warn(context, waiting, "REDUNDANT_AWAIT");
                }
                return waiting.Awaited is GDScriptCall && !(value is { Kind: GDScriptTypeKind.Builtin, Name: "Signal" }) ? value : GDScriptType.Variant.Soft;
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
                if (!whenTrue.IsVariant && !whenFalse.IsVariant && result.IsVariant)
                {
                    Warn(context, ternary, "INCOMPATIBLE_TERNARY");
                }
                return result with { Hard = whenTrue.Hard && whenFalse.Hard };
            case GDScriptCast cast:
                GDScriptType operandType = Reduce(cast.Operand, context);
                GDScriptType castType = cast.Type is { } castTo ? ResolveType(castTo, context) : GDScriptType.Variant;
                if (!castType.IsVariant && (operandType.IsVariant || !operandType.Hard))
                {
                    Warn(context, cast, "UNSAFE_CAST", castType.ToString());
                }
                if (castType is { Kind: GDScriptTypeKind.Enum, Values: { } castValues, ValuesPartial: false } && cast.Operand is { } castOperand
                    && EnumConstant(castOperand, context) is long castValue && !castValues.Values.Contains(castValue))
                {
                    Warn(context, castOperand, "INT_AS_ENUM_WITHOUT_MATCH", "cast", castValue.ToString(System.Globalization.CultureInfo.InvariantCulture), castType.ToString());
                }
                return castType;
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
            case GDScriptGetNode get:
                return NodeTyped(get.Path, context);
            case GDScriptPreload preload:
                Reduce(preload.Path, context);
                return ConstantText(preload.Path, context) is { } path ? Preloaded(Absolute(path, context.Analysis.Path)) : GDScriptType.Native("Resource");
            case GDScriptLambda lambda:
                if (lambda.Function is { } function)
                {
                    WalkFunction(function, context);
                }
                return GDScriptType.Builtin("Callable");
            case GDScriptAssignment assignment:
                return Assign(assignment, context);
            default:
                return GDScriptType.Variant.Soft;
        }
    }

    /// <summary>The text a preload's path is as Godot folds it: a string written, or a constant's name, of the function or
    /// of the class and the script classes it extends, whose value is one; null for any other.</summary>
    private string? ConstantText(GDScriptExpression? expression, Context context)
    {
        if (expression is GDScriptLiteral { Value: string written })
        {
            return written;
        }
        if (expression is not GDScriptIdentifier name)
        {
            return null;
        }
        if (context.Scope?.Find(name.Name) is { } local)
        {
            return local.Local?.Node is GDScriptConstant { Initializer: GDScriptLiteral { Value: string held } } ? held : null;
        }
        GDScriptType? at = context.Class.Instance;
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++, at = BaseOf(at))
        {
            if (OwnNames(at).TryGetValue(name.Name, out (GDScriptNode Node, int Offset) owned))
            {
                return owned.Node is GDScriptConstant { Initializer: GDScriptLiteral { Value: string held } } ? held : null;
            }
        }
        return null;
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
            Paint(context, identifier, local.Local is { } named ? ColourOf(named) : GDScriptColour.Local);
            if (local.Local is { } counted && analysis.Tree is not null)
            {
                counted.Usages++;
                if (counted.Kind == LocalKind.Variable && counted.Assignments == 0 && !(local.Type.Hard && local.Type.Kind == GDScriptTypeKind.Builtin))
                {
                    Warn(context, identifier, "UNASSIGNED_VARIABLE", identifier.Name);
                }
            }
            return local.Type;
        }
        if (context.Scope?.InSuites(identifier.Name) is not null)
        {
            Warn(context, identifier, "CONFUSABLE_LOCAL_USAGE", identifier.Name);
        }
        if (Member(context.Class, identifier.Name) is { } member)
        {
            if (member.Declaration is { } declared)
            {
                analysis.Declared[identifier] = declared;
            }
            Paint(context, identifier, ColourOf(member));
            return member.Kind == "func" ? GDScriptType.Builtin("Callable") : member.Type;
        }
        if (Engine?.Singletons.GetValueOrDefault(identifier.Name) is { } engineSingleton)
        {
            Paint(context, identifier, GDScriptColour.Class);
            return GDScriptType.Native(engineSingleton);
        }
        if (GlobalOrScopedType(identifier.Name, context.Class) is { } type)
        {
            Paint(context, identifier, ColourOf(type));
            if (type is { Kind: GDScriptTypeKind.Class, Script: { } script, Class: { } node })
            {
                analysis.Declared[identifier] = new GDScriptDeclaration(script, type.Offset + (node.Identifier?.Start ?? 0), identifier.Name, type);
            }
            return type;
        }
        if (project.Autoload(identifier.Name) is { } autoload)
        {
            Paint(context, identifier, GDScriptColour.Class);
            GDScriptType loaded = Tree(autoload) is { } tree ? ScriptClass(tree, autoload) : GDScriptType.Native("Node");
            analysis.Declared[identifier] = new GDScriptDeclaration(autoload, 0, identifier.Name, loaded);
            return loaded;
        }
        if (Engine?.Functions.ContainsKey(identifier.Name) == true)
        {
            Paint(context, identifier, GDScriptColour.Method);
            return GDScriptType.Builtin("Callable");
        }
        if (identifier.Name is "PI" or "TAU" or "INF" or "NAN")
        {
            Paint(context, identifier, GDScriptColour.Constant);
            return GDScriptType.Builtin("float");
        }
        if (Engine?.GlobalEnumValues.GetValueOrDefault(identifier.Name) is { } owning && Engine.GlobalEnums.GetValueOrDefault(owning) is { } global)
        {
            Paint(context, identifier, GDScriptColour.EnumMember);
            return new GDScriptType(GDScriptTypeKind.Enum, owning) { Values = global.Values };
        }
        if (Engine?.GlobalConstants.Contains(identifier.Name) == true)
        {
            Paint(context, identifier, GDScriptColour.Constant);
            return GDScriptType.Builtin("int");
        }
        return GDScriptType.Variant.Soft;
    }

    /// <summary>A node path's node's type as the scenes running the script give it, its script's class where it runs one,
    /// else its engine class; Node, as Godot's own reduce_get_node types it, where they don't say.</summary>
    private GDScriptType NodeTyped(string path, Context context)
    {
        if (project.NodeAt(context.Analysis.Path, path) is not { } node)
        {
            return GDScriptType.Native("Node");
        }
        if (node.Script is { } script && project.Script(script) is { } tree)
        {
            return ScriptClass(tree, script);
        }
        return Engine?.Classes.ContainsKey(node.Class) == true ? GDScriptType.Native(node.Class) : GDScriptType.Native("Node");
    }

    /// <summary>Whether a member of a name read off a value is one Godot warns of as unsafe (its UNSAFE_PROPERTY_ACCESS and
    /// UNSAFE_METHOD_ACCESS): the value's type an engine class, known hard, the engine's whole API read, and no member so
    /// named on it or its bases. A script class's, a script's (whose class Godot reads through it) and a value of no type
    /// are left, whose members another reading may hold.</summary>
    private bool Unsafe(GDScriptType owner, string name) =>
        owner is { Kind: GDScriptTypeKind.Native, Hard: true, Meta: false } && Engine is { } engine && engine.Classes.ContainsKey(owner.Name)
        && !engine.Extends(owner.Name, "Script") && Member(owner, name) is null;

    /// <summary>A member's type read off a value or a type (Godot's reduce_identifier_from_base): an enum's value, a
    /// built-in type's constant or member, a class's or engine class's member; a method read as a value is a Callable.</summary>
    private GDScriptType Attribute(GDScriptType owner, GDScriptIdentifier name, Context context)
    {
        GDScriptType found = MemberOf(owner, name.Name, context, name);
        return found with { Hard = found.Hard && (owner.Hard || owner.Meta) };
    }

    private GDScriptType MemberOf(GDScriptType owner, string name, Context context, GDScriptIdentifier? at)
    {
        GDScriptAnalysis analysis = context.Analysis;
        switch (owner.Kind)
        {
            case GDScriptTypeKind.Enum:
                if (owner.Meta && owner.Values?.ContainsKey(name) == true)
                {
                    Paint(context, at, GDScriptColour.EnumMember);
                    return owner.Instance;
                }
                return GDScriptType.Variant.Soft;
            case GDScriptTypeKind.Builtin:
                if (Engine?.Builtins.GetValueOrDefault(owner.Name) is not { } builtin || owner.Name == "Dictionary" && !owner.Meta)
                {
                    return GDScriptType.Variant.Soft;
                }
                Paint(context, at, builtin.Methods.ContainsKey(name) ? GDScriptColour.Method : owner.Meta && builtin.Enums.ContainsKey(name) ? GDScriptColour.Enum
                    : owner.Meta && builtin.Enums.Values.Any(e => e.Values.ContainsKey(name)) ? GDScriptColour.EnumMember : owner.Meta && builtin.Constants.ContainsKey(name) ? GDScriptColour.Constant
                    : builtin.Properties.ContainsKey(name) ? GDScriptColour.Property : null);
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
                Paint(context, at, ColourOf(member));
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

    /// <summary>A call's type, and Godot's warnings of it: a coroutine called without <c>await</c> (a warning as a statement,
    /// else an error), a value a method returns left unused, and a signal of the class named by its string.</summary>
    private GDScriptType Call(GDScriptCall call, Context context, bool root, bool awaited)
    {
        GDScriptType returned = CallOnce(call, context);
        // The script's own get_node of a path its scenes give a node of a type.
        if (call is { Callee: GDScriptIdentifier { Name: "get_node" } or GDScriptSubscript { Base: GDScriptSelf, Attribute.Name: "get_node" }, Arguments: [GDScriptLiteral { Value: string path }] }
            && context.Scope?.Find("get_node") is null && project.NodeAt(context.Analysis.Path, path) is not null)
        {
            returned = NodeTyped(path, context);
        }
        if (context.Analysis.Tree is null)
        {
            return returned;
        }
        GDScriptIdentifier? called = call.Callee switch
        {
            GDScriptIdentifier name => name,
            GDScriptSubscript { IsAttribute: true, Attribute: { } method } => method,
            _ => null,
        };
        if (called is not null && context.Analysis.Declared.GetValueOrDefault(called) is { } declared && IsCoroutine(declared))
        {
            context.Analysis.CoroutineCalls.Add(call);
            if (!awaited)
            {
                if (root)
                {
                    Warn(context, call, "MISSING_AWAIT");
                }
                else
                {
                    Error(context, call, $"Function \"{call.FunctionName}()\" is a coroutine, so it must be called with \"await\".");
                }
            }
        }
        if (called is not null && context.Analysis.Declared.GetValueOrDefault(called) is { Script: { } script } function)
        {
            UnsafeArguments(call, script, function, context);
        }
        bool self = call.Callee is GDScriptIdentifier || call.Callee is GDScriptSubscript { IsAttribute: true, Base: GDScriptSelf };
        if (self && call.FunctionName is "emit_signal" or "connect" or "disconnect" && call.Arguments is [GDScriptLiteral { Value: string signal }, ..])
        {
            context.Analysis.SignalsNamed.Add(signal);
        }
        if (call.Callee is GDScriptIdentifier { Name: "Signal" } && call.Arguments is [GDScriptSelf, GDScriptLiteral { Value: string constructed }])
        {
            context.Analysis.SignalsNamed.Add(constructed);
        }
        if (root && call.Callee is GDScriptSubscript { IsAttribute: true } && !call.IsSuper && returned is { IsVoid: false, IsVariant: false }
            && returned is not { Kind: GDScriptTypeKind.Builtin, Name: "Nil" })
        {
            Warn(context, call, "RETURN_VALUE_DISCARDED", call.FunctionName ?? string.Empty);
        }
        return returned;
    }

    /// <summary>A script function's call warned of each argument Godot's validate_call_arg calls unsafe (UNSAFE_CALL_ARGUMENT),
    /// where its parameter's type is written: a local written with no type or as Variant passed, or an engine class's
    /// value where it asks for one of that class's subclasses.</summary>
    private void UnsafeArguments(GDScriptCall call, string script, GDScriptDeclaration declared, Context context)
    {
        if (project.Script(script) is not { } tree
            || tree.Members.FirstOrDefault(m => m.Node is GDScriptFunction { Identifier: { } n } && m.Offset + n.Start == declared.Start).Node is not GDScriptFunction function)
        {
            return;
        }
        for (int i = 0; i < call.Arguments.Count && i < function.Parameters.Count; i++)
        {
            if (function.Parameters[i].Type is not { Chain: [{ } written], Elements.Count: 0 } || Engine is null)
            {
                continue;
            }
            GDScriptType wanted = FromApi(written.Name);
            if (wanted.IsVariant || wanted.Kind is not (GDScriptTypeKind.Builtin or GDScriptTypeKind.Native))
            {
                continue;
            }
            GDScriptExpression argument = call.Arguments[i];
            GDScriptType given = context.Analysis.TypeOf(argument);
            string? supertype = argument is GDScriptIdentifier name && context.Scope?.Find(name.Name) is { Local.Node: GDScriptAssignable local } && Untyped(local) ? "Variant"
                : given is { Kind: GDScriptTypeKind.Native, Hard: true, Meta: false } && wanted.Kind == GDScriptTypeKind.Native && given.Name != wanted.Name
                    && Engine.Extends(wanted.Name, given.Name) ? given.Name
                : null;
            if (supertype is not null)
            {
                Warn(context, argument, "UNSAFE_CALL_ARGUMENT", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "function", call.FunctionName ?? string.Empty,
                    wanted.ToString(), supertype);
            }
        }
    }

    /// <summary>Whether a local or parameter is declared with no type written nor inferred, or as Variant.</summary>
    private static bool Untyped(GDScriptAssignable local) =>
        local is GDScriptVariable or GDScriptParameter && (local.Type is null && !local.Infers || local.Type is { Chain: [{ Name: "Variant" }] });

    /// <summary>What a call gives (Godot's reduce_call): a built-in type's constructor its type, a function's or method's
    /// return type, the script's own, its base classes', the engine's or a utility function's; <c>new()</c> an instance.</summary>
    private GDScriptType CallOnce(GDScriptCall call, Context context)
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
                Paint(context, superName, GDScriptColour.Method);
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
                    Paint(context, name, local.Local is { } named ? ColourOf(named) : GDScriptColour.Local);
                    if (local.Local is { } counted)
                    {
                        counted.Usages++;
                    }
                    return GDScriptType.Variant.Soft;
                }
                if (Engine?.Builtins.ContainsKey(name.Name) == true && name.Name != "Nil" || IsBuiltinName(name.Name))
                {
                    Paint(context, name, ColourOf(GDScriptType.Builtin(name.Name)));
                    return GDScriptType.Builtin(name.Name);
                }
                if (Member(context.Class, name.Name) is { Kind: "func" } own)
                {
                    if (own.Declaration is { } declared)
                    {
                        context.Analysis.Declared[name] = declared;
                    }
                    context.Analysis.Types[name] = GDScriptType.Builtin("Callable");
                    Paint(context, name, GDScriptColour.Method);
                    return own.Type;
                }
                if (Engine?.Functions.GetValueOrDefault(name.Name) is { } function)
                {
                    context.Analysis.Types[name] = GDScriptType.Builtin("Callable");
                    Paint(context, name, GDScriptColour.Method);
                    return FromApi(function.Returns);
                }
                Reduce(name, context);
                return GDScriptType.Variant.Soft;
            case GDScriptSubscript { IsAttribute: true, Attribute: { } method } attribute:
                GDScriptType owner = Reduce(attribute.Base, context);
                if (attribute.Base is not GDScriptSelf && Unsafe(owner, method.Name))
                {
                    Warn(context, call, "UNSAFE_METHOD_ACCESS", method.Name, owner.Name);
                }
                TemporaryModification(null, attribute, context);
                context.Analysis.Types[method] = GDScriptType.Builtin("Callable");
                GDScriptType called = MethodCall(owner, method, context);
                if (owner.Kind is not GDScriptTypeKind.Variant)
                {
                    Paint(context, method, GDScriptColour.Method);
                }
                return called;
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

    /// <summary>An assignment reduced as Godot's reduce_assignment does: its value first, then a local it assigns counted as
    /// assigned rather than used, then its target; a compound one to a variable never assigned, and a float given to an
    /// int, warned of.</summary>
    private GDScriptType Assign(GDScriptAssignment assignment, Context context)
    {
        GDScriptType value = Reduce(assignment.Value, context);
        Local? assigned = assignment.Assignee is GDScriptIdentifier name && context.Analysis.Tree is not null ? context.Scope?.Find(name.Name)?.Local : null;
        if (assigned is { Kind: LocalKind.Variable })
        {
            assigned.Assignments++;
            assigned.Usages--;
        }
        else if (assigned is { Kind: LocalKind.Parameter })
        {
            assigned.Usages--;
        }
        GDScriptType target = Reduce(assignment.Assignee, context);
        CaptureReassignment(assignment, context);
        TemporaryModification(assignment, assignment.Assignee, context);
        if (target is { Hard: true, Kind: GDScriptTypeKind.Builtin, Name: "int" } && value is { Kind: GDScriptTypeKind.Builtin, Name: "float" } && assignment.Value is { } narrowed)
        {
            Warn(context, narrowed, "NARROWING_CONVERSION");
        }
        if (assignment.Operator != K.Equal && assigned is { Kind: LocalKind.Variable, Assignments: 1 } && assignment.Assignee is GDScriptIdentifier modified)
        {
            Warn(context, assignment, "UNASSIGNED_VARIABLE_OP_ASSIGN", modified.Name, GDScriptToken.KindName(assignment.Operator).TrimEnd('='));
        }
        return GDScriptType.Variant.Soft;
    }

    private GDScriptType Binary(GDScriptBinary binary, Context context)
    {
        GDScriptType left = Reduce(binary.Left, context);
        GDScriptType right = Reduce(binary.Right, context);
        bool hard = left.Hard && right.Hard;
        if (binary.Operator == K.Slash && left is { Kind: GDScriptTypeKind.Builtin, Name: "int" or "Vector2i" or "Vector3i" or "Vector4i" }
            && right is { Kind: GDScriptTypeKind.Builtin } && (right.Name == "int" || right.Name == left.Name))
        {
            Warn(context, binary, "INTEGER_DIVISION");
        }
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
