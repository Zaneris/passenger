using System.Runtime.CompilerServices;
using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>The walk of a script's members and bodies, and each expression's type as Godot's reduce_* functions give it.</summary>
public sealed partial class GDScriptAnalyzer
{
    private void Walk(GDScriptAnalysis analysis, GDScriptNode member, Context context)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return;
        }
        MemberNameErrors(member, context);
        AnnotationArgumentErrors(member, context);
        switch (member)
        {
            case GDScriptVariable variable:
                // A static variable's value is read once, in its static place.
                Context valued = variable.IsStatic ? context with { Static = string.Empty } : context;
                GDScriptType type = variable.IsStatic || variable.Identifier is null
                    ? AssignableType(variable, valued with { Analysis = new GDScriptAnalysis(null, analysis.Path) }, false) : MemberType(variable, context.Class, context.Offset);
                Record(analysis, variable, type, context);
                if (variable.Initializer is { } value
                    && Reduce(value, valued) is { Kind: GDScriptTypeKind.Builtin, Name: "int" } && variable.Type is not null && type is { Kind: GDScriptTypeKind.Enum, Meta: false })
                {
                    Warn(context, value, "INT_AS_ENUM_WITHOUT_CAST");
                }
                Declaring(variable, type, "variable", local: false, valued);
                ExportErrors(variable, type, context);
                OnreadyErrors(variable, context);
                PropertyFunctionsDeclared(variable, context);
                MemberVariableWarnings(variable, context);
                if (variable.Type is { } memberType)
                {
                    ResolveType(memberType, context);
                }
                if (variable.Getter is { Body: { } getter } get)
                {
                    WalkSuite(getter, context with { Scope = new Scope(null), Function = get, Returns = variable.Type is null ? null : type, Static = StaticAccessor(variable, "getter") });
                }
                if (variable.Setter is { Body: { } setter } set)
                {
                    Scope scope = new(null);
                    if (set.Parameters is [{ Identifier: { } parameter } only])
                    {
                        scope.Add(parameter.Name, type, new GDScriptDeclaration(context.Analysis.Path, context.Offset + parameter.Start, parameter.Name, type), new Local(only, parameter, LocalKind.Setter));
                        Record(analysis, only, type, context);
                        setterProperties[only] = variable;
                    }
                    WalkSuite(setter, context with { Scope = scope, Function = set, Static = StaticAccessor(variable, "setter") });
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
                DeclaresItself(inner.Identifier, innerType with { Meta = true }, context);
                Paint(context, inner.Identifier, GDScriptColour.Class);
                PaintExtends(inner, context);
                ExtendsDeclared(inner, context);
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
                DeclaresItself(signal.Identifier, GDScriptType.Builtin("Signal"), context);
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
                EnumValueErrors(named, context);
                foreach ((GDScriptIdentifier enumValue, _) in named.Values)
                {
                    DeclaresItself(enumValue, GDScriptType.Builtin("int"), context);
                    Paint(context, enumValue, GDScriptColour.Constant);
                    Shadowing(enumValue, "enum member", local: false, context);
                }
                break;
            case GDScriptEnum enumerated:
                EnumValueErrors(enumerated, context);
                GDScriptType enumType = EnumType(enumerated, context.Class, meta: true);
                DeclaresItself(enumerated.Identifier, enumType, context);
                Paint(context, enumerated.Identifier, GDScriptColour.Enum);
                foreach ((GDScriptIdentifier enumValue, _) in enumerated.Values)
                {
                    DeclaresItself(enumValue, enumType with { Meta = false }, context);
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
        Context inside = context with
        {
            Scope = scope, Function = function, Returns = null, Static = function.IsLambda ? context.Static : function.IsStatic ? FunctionName(function) : null,
            Voids = function.ReturnType is { IsVoid: true },
        };
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
        List<GDScriptType> parameterTypes = [];
        foreach (GDScriptParameter parameter in function.Rest is { } rest ? [.. function.Parameters, rest] : function.Parameters)
        {
            GDScriptType type = parameter == function.Rest ? RestType(parameter, inside) : AssignableType(parameter, inside, constant: false);
            if (parameter != function.Rest)
            {
                parameterTypes.Add(type);
            }
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
            inside = inside with { Returns = ResolveType(returned, inside) };
        }
        OverrideErrors(function, parameterTypes, function.ReturnType is { IsVoid: true } ? GDScriptType.Builtin("Nil") with { IsVoid = true } : inside.Returns, context);
        ConstructorErrors(function, inside);
        inside = Overridden(function, inside);
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
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return true;
        }
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
                GDScriptType? specified = loop.Type is { } declaredType ? ResolveType(declaredType, context) : null;
                ForErrors(loop, list, specified, context);
                Scope iteration = new(scope);
                if (loop.Variable is { } variableName)
                {
                    GDScriptType element = specified ?? Element(list, loop.List);
                    analysis.Types[loop.Variable] = element;
                    Paint(context, variableName, GDScriptColour.Local);
                    iteration.Add(variableName.Name, element, new GDScriptDeclaration(analysis.Path, context.Offset + variableName.Start, variableName.Name, element), new Local(loop, variableName, LocalKind.Iterator));
                    DeclaresItself(variableName, element, context);
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
                // A void function's call is returned as a statement is made, its void its own.
                bool voids = context.Voids;
                GDScriptType given = Reduce(returned.Value, context, root: voids && returned.Value is GDScriptCall);
                ReturnWarnings(returned, given, context);
                ReturnErrors(returned, given, voids, context);
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
            DeclaresItself(name, GDScriptType.Variant.Soft, context);
            Shadowing(name, "pattern bind", local: true, context);
        }
        Reduce(pattern.Expression, context);
        if (pattern.Expression is { } expression)
        {
            PatternErrors(expression, context);
        }
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

    /// <summary>A property's getter and setter named (<c>set = set_active</c>) as where the class declares those functions,
    /// so Go to Declaration, Find Usages and Rename take them as calls of them.</summary>
    private void PropertyFunctionsDeclared(GDScriptVariable variable, Context context)
    {
        if (context.Analysis.Tree is null)
        {
            return;
        }
        foreach (GDScriptIdentifier? named in new[] { variable.GetterName, variable.SetterName })
        {
            if (named is not null && OwnNames(context.Class).TryGetValue(named.Name, out (GDScriptNode Node, int Offset) own) && own.Node is GDScriptFunction { Identifier: { } function })
            {
                context.Analysis.Declared[named] = new GDScriptDeclaration(context.Analysis.Path, own.Offset + function.Start, named.Name, GDScriptType.Builtin("Callable"));
            }
        }
    }

    /// <summary>A signal's, inner class's, enum's, enum value's, loop variable's or pattern bind's name as where it is
    /// declared, as a variable's and a function's are, so Go to Declaration and Find Usages take it from its own declaration.</summary>
    private static void DeclaresItself(GDScriptIdentifier? name, GDScriptType type, Context context)
    {
        if (name is null || context.Analysis.Tree is null)
        {
            return;
        }
        context.Analysis.Declared[name] = new GDScriptDeclaration(context.Analysis.Path, context.Offset + name.Start, name.Name, type);
        context.Analysis.Declaring.Add(name);
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
        // An object goes through by its script's _iter_get, typed as that returns.
        GDScriptType element = IterationOf(list)
            ?? (list is { Kind: GDScriptTypeKind.Native or GDScriptTypeKind.Class, Meta: false } && Member(list, "_iter_get") is { Kind: "func", Declaration.Script: not null } iterated
                ? iterated.Type : null)
            ?? GDScriptType.Variant;
        return element with { Hard = list.Hard && element.Hard };
    }

    /// <summary>An expression's type, recorded for it in the analysis where one is being made. Variant where the stack this
    /// reading has left runs short: members' values read one another however long the chain, and a stack that ran out ends
    /// the process.</summary>
    private GDScriptType Reduce(GDScriptExpression? expression, Context context, bool root = false, bool awaited = false)
    {
        if (expression is null || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
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
                return AttributeOf(attribute, context, pseudo: false);
            case GDScriptSubscript subscript:
                GDScriptType indexed = ReduceBase(subscript.Base, context);
                SubscriptErrors(subscript, indexed, Reduce(subscript.Index, context), context);
                return ConstantElement(subscript, context) ?? Indexed(indexed);
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
                string unaryOperator = unary.Operator switch { K.Minus => "unary-", K.Plus => "unary+", _ => "~" };
                UnaryErrors(unary, unaryOperator, operand, context);
                return Operation(operand, unaryOperator, null);
            case GDScriptTernary ternary:
                Reduce(ternary.Condition, context);
                GDScriptType whenTrue = Reduce(ternary.True, context, root);
                GDScriptType whenFalse = Reduce(ternary.False, context, root);
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
                if (cast is { Operand: { } castFrom, Type: { } castNode })
                {
                    TypedLiteralErrors(castFrom, castType, context);
                    CastErrors(castFrom, castNode, operandType, castType, context);
                }
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
                GDScriptType tested = Reduce(test.Operand, context);
                if (test is { Operand: { } testOperand, Type: { } testNode })
                {
                    TypeTestErrors(testOperand, tested, ResolveType(testNode, context), context);
                }
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
                DuplicateKeyErrors(dictionary, context);
                return GDScriptType.Builtin("Dictionary");
            case GDScriptGetNode get:
                GetNodeErrors(get, context);
                return NodeTyped(get.Path, context);
            case GDScriptPreload preload:
                Reduce(preload.Path, context);
                if (ConstantText(preload.Path, context) is not { } path)
                {
                    return GDScriptType.Native("Resource");
                }
                string preloaded = GodotResPaths.Resolved(path, context.Analysis.Path);
                if (preload.Path is { } written && context.Analysis.Tree is not null && Knows(context.Class) && Lacked(preloaded))
                {
                    Error(context, written, $"Preload file \"{preloaded}\" does not exist.");
                }
                return Preloaded(preloaded);
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

    /// <summary>What a subscript is taken of, reduced as Godot's reduce_subscript reduces it: a name or an attribute that
    /// may be a type on its own (a built-in type, a global enum, an engine's enum), any other expression as it is.</summary>
    private GDScriptType ReduceBase(GDScriptExpression? taken, Context context)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return GDScriptType.Variant;
        }
        GDScriptType type = taken switch
        {
            GDScriptIdentifier name => Identifier(name, context, asBase: true),
            GDScriptSubscript { IsAttribute: true } attribute => AttributeOf(attribute, context, pseudo: true),
            _ => Reduce(taken, context),
        };
        if (taken is GDScriptIdentifier or GDScriptSubscript { IsAttribute: true } && context.Analysis.Tree is not null)
        {
            context.Analysis.Types[taken] = type;
        }
        return type;
    }

    /// <summary>An attribute's type (<c>a.b</c>), recorded for its name too, which is what a place in the text finds before a
    /// further dot; its unsafe access warned of, and Godot's errors of a member it is not where what it is taken of is known:
    /// an enum's value's property, a member an enum, a built-in type or a class does not have, a property of null, and an
    /// engine's enum standing on its own unless <paramref name="pseudo"/> (it is what another subscript is taken of).</summary>
    private GDScriptType AttributeOf(GDScriptSubscript attribute, Context context, bool pseudo)
    {
        GDScriptType owner = ReduceBase(attribute.Base, context);
        if (attribute.Attribute is { } named && Unsafe(owner, named.Name))
        {
            Warn(context, attribute, "UNSAFE_PROPERTY_ACCESS", named.Name, owner.Name);
        }
        TemporaryModification(null, attribute, context);
        if (attribute.Attribute is not { } name)
        {
            return GDScriptType.Variant;
        }
        if (attribute.Base is GDScriptIdentifier { Name: "Variant" } && context.Scope?.Find("Variant") is null && Member(context.Class, "Variant") is null
            && Engine?.GlobalEnums.GetValueOrDefault($"Variant.{name.Name}") is { } variantEnum)
        {
            if (!pseudo && context.Analysis.Tree is not null)
            {
                Error(context, name, $"Type \"{name.Name}\" in base \"Variant\" cannot be used on its own.");
            }
            return Recorded(name, new GDScriptType(GDScriptTypeKind.Enum, variantEnum.Name) { Values = variantEnum.Values, Meta = true }, context);
        }
        GDScriptType found = Attribute(owner, name, context);
        EnumValueDeclared(owner, name, found, context);
        if (context.Analysis.Tree is not null && Engine is not null && Missing(owner, attribute.Base, name, found, pseudo, context) is not null
            && found is { Kind: GDScriptTypeKind.Enum, Meta: true, Script: null, Owner: { } engine })
        {
            // An engine's enum on its own is no type Godot goes on with: a Variant standing for the enum's native class.
            return Recorded(name, GDScriptType.Variant with { Meta = true, Owner = $"{engine}.{found.Name}" }, context);
        }
        return Recorded(name, found, context);
    }

    /// <summary>A script enum's value read off the enum (<c>FillArea.AREA</c>) recorded as declared where the enum gives it.</summary>
    private void EnumValueDeclared(GDScriptType owner, GDScriptIdentifier name, GDScriptType found, Context context)
    {
        if (context.Analysis.Tree is null || owner is not { Kind: GDScriptTypeKind.Enum, Meta: true, Script: { } script, Class: { } holder })
        {
            return;
        }
        GDScriptType at = new(GDScriptTypeKind.Class, holder.Identifier?.Name ?? script) { Class = holder, Script = script, Offset = owner.Offset };
        if (OwnNames(at).TryGetValue(owner.Name, out (GDScriptNode Node, int Offset) declared) && declared.Node is GDScriptEnum enumerated
            && enumerated.Values.FirstOrDefault(v => v.Name.Name == name.Name).Name is { } value)
        {
            context.Analysis.Declared[name] = new GDScriptDeclaration(script, declared.Offset + value.Start, name.Name, found);
        }
    }

    /// <summary>Godot's errors of an attribute's member that is not there (reduce_identifier_from_base, reduce_subscript),
    /// a class's own instance member read from the class held constant among them: what it said, or null for none.</summary>
    private string? Missing(GDScriptType owner, GDScriptExpression? taken, GDScriptIdentifier name, GDScriptType found, bool pseudo, Context context)
    {
        string? said = owner switch
        {
            { Kind: GDScriptTypeKind.Enum, Meta: false } => "Cannot get property from enum value.",
            { Kind: GDScriptTypeKind.Enum, Meta: true, Values: { } values, ValuesPartial: false } when !values.ContainsKey(name.Name) => NotIn(owner),
            { Kind: GDScriptTypeKind.Builtin, Name: "Nil", Meta: false, Hard: true } => $"Cannot get property \"{name.Name}\" on a null object.",
            { Kind: GDScriptTypeKind.Builtin, Meta: true, Hard: true } when Engine!.Builtins.GetValueOrDefault(owner.Name) is { } builtin
                && !builtin.Constants.ContainsKey(name.Name) && !builtin.Enums.ContainsKey(name.Name) && !builtin.Enums.Values.Any(e => e.Values.ContainsKey(name.Name)) => NotIn(owner),
            { Kind: GDScriptTypeKind.Builtin, Meta: false, Hard: true, Name: not ("Dictionary" or "Nil") } when Engine!.Builtins.GetValueOrDefault(owner.Name) is { } builtin
                && !builtin.Properties.ContainsKey(name.Name) && !builtin.Methods.ContainsKey(name.Name) => NotIn(owner),
            { Kind: GDScriptTypeKind.Native or GDScriptTypeKind.Class, Meta: true } when name.Name != "new" && Knows(owner) && HeldConstant(taken, context)
                && (Member(owner.Instance, name.Name) is null || InstanceOnly(owner, name.Name)) => NotIn(owner),
            _ => null,
        };
        if (said is null && !pseudo && found is { Kind: GDScriptTypeKind.Enum, Meta: true, Script: null, Owner: { } engine })
        {
            said = $"Type \"{name.Name}\" in base \"{engine}\" cannot be used on its own.";
        }
        if (said is not null)
        {
            Error(context, name, said);
        }
        return said;

        string NotIn(GDScriptType of) => $"Cannot find member \"{name.Name}\" in base \"{of.Instance}\".";
    }

    /// <summary>Whether Godot holds what an attribute is taken of constant, as a class read through it must be for its
    /// members to be looked up: a constant, a class or engine class by its name, a preload, or a class held so of one.</summary>
    private bool HeldConstant(GDScriptExpression? taken, Context context) => RuntimeHelpers.TryEnsureSufficientExecutionStack() && taken switch
    {
        GDScriptPreload => true,
        GDScriptIdentifier name when context.Scope?.Find(name.Name) is { } local => local.Local?.Kind == LocalKind.Constant,
        GDScriptIdentifier name => Member(context.Class, name.Name) is { } member ? member.Kind is "const" or "class"
            : GlobalOrScopedType(name.Name, context.Class) is { Kind: GDScriptTypeKind.Class or GDScriptTypeKind.Native, Meta: true },
        GDScriptSubscript { IsAttribute: true, Base: { } outer, Attribute: { } inner } => HeldConstant(outer, context)
            && context.Analysis.Types.GetValueOrDefault(outer) is { Meta: true } holder && Member(holder.Instance, inner.Name) is { Kind: "const" or "class" },
        _ => false,
    };

    /// <summary>Whether a script class's member of a name, the first its classes declare, is one only an instance has: a
    /// variable or function that is not static, or a signal; Godot finds none of them on the class itself.</summary>
    private bool InstanceOnly(GDScriptType owner, string name)
    {
        GDScriptType? at = owner.Instance;
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++, at = BaseOf(at))
        {
            if (OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) owned))
            {
                return InstanceOnly(owned.Node);
            }
        }
        return false;
    }

    /// <summary>Whether a class's member is one only an instance has.</summary>
    private static bool InstanceOnly(GDScriptNode member) => member is GDScriptVariable { IsStatic: false } or GDScriptSignal or GDScriptFunction { IsStatic: false };

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
            : project.ResourceClass(path) is "Resource" ? GDScriptType.Native("Resource").Soft : GDScriptType.Native(project.ResourceClass(path));
    }

    /// <summary>A name's type as Godot's reduce_identifier gives it, and its errors where the class is read whole
    /// (<see cref="Knows"/>): a name declared nowhere, and, but as what a subscript is taken of (<paramref name="asBase"/>,
    /// Godot's can_be_builtin), a built-in type or a global enum on its own.</summary>
    private GDScriptType Identifier(GDScriptIdentifier identifier, Context context, bool asBase = false)
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
            StaticAccessErrors(identifier, context);
            return member.Kind == "func" ? GDScriptType.Builtin("Callable") : member.Type;
        }
        if (Engine?.Singletons.GetValueOrDefault(identifier.Name) is { } engineSingleton)
        {
            Paint(context, identifier, GDScriptColour.Class);
            return GDScriptType.Native(engineSingleton);
        }
        bool tells = analysis.Tree is not null && Knows(context.Class);
        if (!asBase && tells && (identifier.Name == "Variant" || GlobalOrScopedType(identifier.Name, context.Class) is { Kind: GDScriptTypeKind.Builtin, Meta: true }))
        {
            if (identifier.Name != "Variant")
            {
                Error(context, identifier, "Builtin type cannot be used as a name on its own.");
            }
            Error(context, identifier, $"Identifier \"{identifier.Name}\" not declared in the current scope.{GodotRenames.Identifier(identifier.Name)}");
            return GDScriptType.Variant.Soft;
        }
        // An autoload's name is its node, never its script, as a value.
        if (GlobalOrScopedType(identifier.Name, context.Class) is { } type && !(project.ClassPath(identifier.Name) is null && project.Autoload(identifier.Name) is { } autoloadName
            && type is { Kind: GDScriptTypeKind.Class, Script: { } autoloadScript } && autoloadScript == project.Resolved(autoloadName) && type.Class == Tree(autoloadScript)?.Class))
        {
            if (!asBase && tells && type is { Kind: GDScriptTypeKind.Enum, Meta: true, Script: null, Owner: null })
            {
                Error(context, identifier, $"Global enum \"{identifier.Name}\" cannot be used on its own.");
            }
            Paint(context, identifier, ColourOf(type));
            if (type is { Kind: GDScriptTypeKind.Class, Script: { } script, Class: { } node })
            {
                analysis.Declared[identifier] = new GDScriptDeclaration(script, type.Offset + (node.Identifier?.Start ?? 0), identifier.Name, type);
            }
            return type;
        }
        if (project.Autoload(identifier.Name) is { } written)
        {
            Paint(context, identifier, GDScriptColour.Class);
            string autoload = project.Resolved(written);
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
        if (tells && !InOuter(context.Class, identifier.Name))
        {
            Error(context, identifier, $"Identifier \"{identifier.Name}\" not declared in the current scope.{GodotRenames.Identifier(identifier.Name)}");
        }
        return GDScriptType.Variant.Soft;
    }

    /// <summary>Whether a class an inner class is written in, or one of its bases, has a member of a name, which the inner
    /// class's code reads through its scope (Godot's get_class_node_current_scope_classes).</summary>
    private bool InOuter(GDScriptType scope, string name)
    {
        if (scope.Script is not { } path || Tree(path) is not { } tree || scope.Class is not { } inner || inner == tree.Class)
        {
            return false;
        }
        GDScriptType top = ScriptClass(tree, path);
        foreach (GDScriptType outer in Enclosing(top, tree.Members.Select(m => (m.Node, m.Offset)), inner))
        {
            if (Member(outer, name) is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A node path's node's type as the scenes running the script give it, its script's class where it runs one,
    /// else its engine class; Node, as Godot's own reduce_get_node types it, where they don't say.</summary>
    private GDScriptType NodeTyped(string path, Context context)
    {
        if (project.NodeAt(context.Analysis.Path, path) is not { } node)
        {
            return GDScriptType.Native("Node");
        }
        if (node.Script is { } script && Tree(script) is { } tree)
        {
            return ScriptClass(tree, script);
        }
        GDScriptType engine = Engine?.Classes.ContainsKey(node.Class) == true ? GDScriptType.Native(node.Class) : GDScriptType.Native("Node");
        return node.Script is { } csharp && csharp.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? engine with { CSharpScript = csharp } : engine;
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

    /// <summary>The type of the element a constant array written out holds at a literal index, as Godot folds a constant's
    /// subscript; null for any other subscript.</summary>
    private GDScriptType? ConstantElement(GDScriptSubscript subscript, Context context)
    {
        if (subscript is not { Base: GDScriptIdentifier name, Index: GDScriptLiteral { Value: long index } })
        {
            return null;
        }
        GDScriptNode? declared = context.Scope?.Find(name.Name) is { } local ? local.Local?.Node
            : Member(context.Class, name.Name) is { Kind: "const", Declaration: { Script: { } script, Start: var start } } && script == context.Analysis.Path && Tree(script) is { } tree
                ? AssignableAt(tree.Members.Select(m => (m.Node, m.Offset)), start) : null;
        return declared is GDScriptConstant { Initializer: GDScriptArray array } && index >= 0 && index < array.Elements.Count
            && array.Elements[(int)index] is GDScriptLiteral element && context.Analysis.TypeOf(element) is { IsVariant: false } held ? held : null;
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
        if (Knows(context.Class) && call.FunctionName is { } calledName && calledName != "new" && CalledSignature(call, called, context) is var (parameters, defaults, varargs))
        {
            Arguments(call, parameters, defaults, varargs, context);
        }
        if (Knows(context.Class))
        {
            StaticCallErrors(call, context);
        }
        if (!root && !awaited && returned is { IsVoid: true, Hard: true } && call.FunctionName is { } voidName && Knows(context.Class))
        {
            Error(context, call, $"Cannot get return value of call to \"{voidName}()\" because it returns \"void\".");
        }
        if (call.Callee is GDScriptIdentifier typeName && context.Scope?.Find(typeName.Name) is null && typeName.Name != "Nil"
            && Engine?.Builtins.GetValueOrDefault(typeName.Name) is { } builtin)
        {
            ConstructorArguments(call, builtin, context);
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
        bool ofMethod = call.Callee is GDScriptSubscript { IsAttribute: true }
            || call.Callee is GDScriptIdentifier own && context.Scope?.Find(own.Name) is null && !(Engine?.Builtins.ContainsKey(own.Name) == true || IsBuiltinName(own.Name))
                && Member(context.Class, own.Name) is { Kind: "func" };
        if (root && ofMethod && !call.IsSuper && returned is { IsVoid: false, IsVariant: false } && returned is not { Kind: GDScriptTypeKind.Builtin, Name: "Nil" })
        {
            Warn(context, call, "RETURN_VALUE_DISCARDED", call.FunctionName ?? string.Empty);
        }
        if (call.Callee is GDScriptSubscript { IsAttribute: true, Base: { } instance and not GDScriptSelf, Attribute: { } named }
            && context.Analysis.Types.GetValueOrDefault(instance) is { Meta: false } caller && IsStaticOn(caller, named.Name))
        {
            Warn(context, call, "STATIC_CALLED_ON_INSTANCE", named.Name, caller.ToString());
        }
        return returned;
    }

    /// <summary>A script function's call warned of each argument Godot's validate_call_arg calls unsafe (UNSAFE_CALL_ARGUMENT),
    /// where its parameter's type is written: a local written with no type or as Variant passed, or an engine class's
    /// value where it asks for one of that class's subclasses.</summary>
    private void UnsafeArguments(GDScriptCall call, string script, GDScriptDeclaration declared, Context context)
    {
        if (Tree(script) is not { } tree
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
            else if (wanted is { Kind: GDScriptTypeKind.Builtin, Name: "int" } && given is { Kind: GDScriptTypeKind.Builtin, Name: "float", Hard: true })
            {
                Warn(context, argument, "NARROWING_CONVERSION");
            }
        }
    }

    /// <summary>A built-in type's constructor's call warned of each argument Godot's reduce_call calls unsafe
    /// (UNSAFE_CALL_ARGUMENT): of one argument, a local of no type or of Variant, which Godot says may be the type or one
    /// converting to it (<see cref="GDScriptConversions"/>); of more, each such local where only one constructor takes so
    /// many and its parameter asks for a type.</summary>
    private void ConstructorArguments(GDScriptCall call, GodotApiClass builtin, Context context)
    {
        bool Loose(GDScriptExpression argument) =>
            argument is GDScriptIdentifier name && context.Scope?.Find(name.Name) is { Local.Node: GDScriptAssignable local } && Untyped(local);
        string constructor = builtin.Name;
        if (call.Arguments is [{ } only])
        {
            if (Loose(only) && GDScriptConversions.Knows(constructor))
            {
                Warn(context, only, "UNSAFE_CALL_ARGUMENT", "1", "constructor", constructor, GDScriptConversions.Expected(constructor), "Variant");
            }
            return;
        }
        if (builtin.Constructors.Where(c => c.Count == call.Arguments.Count).ToList() is not [{ } parameters])
        {
            return;
        }
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Type != "Variant" && Loose(call.Arguments[i]))
            {
                Warn(context, call.Arguments[i], "UNSAFE_CALL_ARGUMENT", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "constructor", constructor,
                    FromApi(parameters[i].Type).ToString(), "Variant");
            }
        }
    }

    /// <summary>Whether a method a value of a type is called by is static: a script class's <c>static func</c>, or a built-in
    /// type's static method; an engine class's is left, as an engine singleton's every method reads as static.</summary>
    private bool IsStaticOn(GDScriptType type, string name)
    {
        if (type.Kind == GDScriptTypeKind.Builtin)
        {
            return Engine?.Builtins.GetValueOrDefault(type.Name)?.Methods.GetValueOrDefault(name) is { IsStatic: true };
        }
        GDScriptType? at = type.Instance;
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++, at = BaseOf(at))
        {
            if (OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) owned))
            {
                return owned.Node is GDScriptFunction { IsStatic: true };
            }
        }
        return false;
    }

    /// <summary>Whether a local or parameter is a Variant as Godot types it whatever it is given: a parameter of no type with
    /// no value or null for its default, a variable of no type declared with no value.</summary>
    private static bool OfNoType(GDScriptAssignable local) => local.Type is null && !local.Infers && local switch
    {
        GDScriptParameter parameter => parameter.Initializer is null or GDScriptLiteral { Kind: GDScriptLiteralKind.Null },
        GDScriptVariable variable => variable.Initializer is null,
        _ => false,
    };

    /// <summary>A return's warnings as Godot's resolve_return gives them: a float where an int is to come back
    /// (NARROWING_CONVERSION), and a void function returning a call of a script function of no return type, which may
    /// give a value (UNSAFE_VOID_RETURN).</summary>
    private void ReturnWarnings(GDScriptReturn returned, GDScriptType value, Context context)
    {
        if (returned.Value is null)
        {
            return;
        }
        if (context.Returns is { Kind: GDScriptTypeKind.Builtin, Name: "int", Hard: true } && value is { Kind: GDScriptTypeKind.Builtin, Name: "float", Hard: true })
        {
            Warn(context, returned, "NARROWING_CONVERSION");
        }
        if (context is { Voids: true, Function: { } voided } && returned.Value is GDScriptCall { Callee: GDScriptIdentifier or GDScriptSubscript { IsAttribute: true, Base: GDScriptSelf } } call
            && Member(context.Class, call.FunctionName ?? string.Empty) is { Kind: "func", Declaration: not null } && !value.Hard)
        {
            Warn(context, returned, "UNSAFE_VOID_RETURN", FunctionName(voided), call.FunctionName ?? string.Empty);
        }
    }

    /// <summary>Godot's errors of a return (resolve_return): a void function's value, and a value of a type the declared one
    /// takes no value of (a return of nothing null), a constant's said as its conversion first.</summary>
    private void ReturnErrors(GDScriptReturn returned, GDScriptType given, bool voids, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        if (returned.Value is not { } value)
        {
            // A return of nothing gives null, which a built-in type takes none of.
            if (!voids && context.Returns is { Hard: true, IsVariant: false } wanted && !TypeCompatible(wanted, GDScriptType.Builtin("Nil")))
            {
                Error(context, returned, $"Cannot return value of type \"null\" because the function return type is \"{wanted}\".");
            }
            return;
        }
        if (voids)
        {
            if (value is not GDScriptCall)
            {
                Error(context, returned, "A void function cannot return a value.");
            }
            return;
        }
        if (context.Returns is { Hard: true } typed)
        {
            TypedLiteralErrors(value, typed, context);
        }
        if (context.Returns is not { IsVariant: false, Hard: true } expected || given.IsVariant || !given.Hard || !Sure(value, context))
        {
            return;
        }
        ConstantConverted(value, given, expected, "return", context);
        if (!TypeCompatible(expected, given, implicitly: true) && !TypeCompatible(given, expected))
        {
            Error(context, returned, $"Cannot return value of type \"{given}\" because the function return type is \"{expected}\".");
        }
    }

    /// <summary>Godot's error of a constant expression of a type a built-in or enum target takes none of
    /// (update_const_expression_builtin_type), <paramref name="usage"/> what it is given to the target by, an int cast to
    /// an enum taken; whether it said one.</summary>
    private bool ConstantConverted(GDScriptExpression value, GDScriptType given, GDScriptType target, string usage, Context context, bool cast = false)
    {
        if (Folded(value, context) && target.Kind is GDScriptTypeKind.Builtin or GDScriptTypeKind.Enum && target.ToString() != given.ToString()
            && !(cast && target is { Kind: GDScriptTypeKind.Enum, Meta: false } && given is { Kind: GDScriptTypeKind.Builtin, Name: "int" } or { Kind: GDScriptTypeKind.Enum, Meta: false })
            && !TypeCompatible(target, given, implicitly: true))
        {
            Error(context, value, $"Cannot {usage} a value of type \"{given}\" as \"{target}\".");
            return true;
        }
        return false;
    }

    /// <summary>Whether an expression's type is one this reading is sure Godot gives it, which a mismatch's error stands on:
    /// a literal (a negative one too), an array's or dictionary's, a name of a type written (a local, a parameter, a member
    /// or a constant of a literal), a call of the script's own function whose return type is written, a built-in type's
    /// constructor. What the engine's API or an inference gives is left, Godot's own reading of it being finer.</summary>
    private bool Sure(GDScriptExpression expression, Context context) => Folded(expression, context) || expression switch
    {
        GDScriptLiteral or GDScriptArray or GDScriptDictionary => true,
        GDScriptUnary { Operator: K.Minus, Operand: GDScriptLiteral } => true,
        GDScriptIdentifier name when context.Scope?.Find(name.Name) is { } local => SureDeclared(local.Local?.Node)
            || local.Local is { Kind: LocalKind.Setter, Node: GDScriptParameter set } && setterProperties.TryGetValue(set, out GDScriptVariable? property) && SureDeclared(property)
            || local.Local?.Node is GDScriptFor { List: { } list } loop && (loop.Type is not null || list is GDScriptCall { Callee: GDScriptIdentifier { Name: "range" } } || Sure(list, context)),
        GDScriptIdentifier name => Member(context.Class, name.Name) is { Declaration: { Script: { } script, Start: var start } } && Tree(script) is { } tree
            && SureDeclared(AssignableAt(tree.Members.Select(m => (m.Node, m.Offset)), start)),
        GDScriptCall { Callee: GDScriptIdentifier builtin } when Engine?.Builtins.ContainsKey(builtin.Name) == true && context.Scope?.Find(builtin.Name) is null => true,
        GDScriptCall { Callee: GDScriptIdentifier own } => Declared(own, context),
        GDScriptCall { Callee: GDScriptSubscript { IsAttribute: true, Base: GDScriptSelf, Attribute: { } method } } => Declared(method, context),
        _ => false,
    };

    /// <summary>Whether a declaration's type is one Godot gives as Passenger does: written, or inferred from a literal, a
    /// built-in type's constant or its constructor, a class's <c>new()</c>, or an element taken by a literal index.</summary>
    private bool SureDeclared(GDScriptNode? declared) => declared switch
    {
        GDScriptAssignable { Type: not null } => true,
        GDScriptAssignable assignable when assignable.Infers || assignable is GDScriptConstant => assignable.Initializer switch
        {
            GDScriptLiteral { Kind: not GDScriptLiteralKind.Null } or GDScriptArray or GDScriptDictionary or GDScriptUnary { Operator: K.Minus, Operand: GDScriptLiteral } => true,
            GDScriptSubscript { IsAttribute: true, Base: GDScriptIdentifier type, Attribute: { } constant } =>
                Engine?.Builtins.GetValueOrDefault(type.Name)?.Constants.ContainsKey(constant.Name) == true,
            GDScriptCall { Callee: GDScriptIdentifier type } => type.Name != "Nil" && Engine?.Builtins.ContainsKey(type.Name) == true,
            GDScriptCall { Callee: GDScriptSubscript { IsAttribute: true, Attribute.Name: "new", Base: GDScriptIdentifier } } => true,
            GDScriptSubscript { IsAttribute: false, Index: GDScriptLiteral { Kind: GDScriptLiteralKind.Int } } => true,
            _ => false,
        },
        _ => false,
    };

    /// <summary>Whether a name a call is made by reads a script's function whose return type is written.</summary>
    private bool Declared(GDScriptIdentifier name, Context context) =>
        context.Analysis.Declared.GetValueOrDefault(name) is { Script: { } script, Start: var start } && Tree(script) is { } tree
        && FunctionAt(tree.Members.Select(m => (m.Node, m.Offset)), start) is { ReturnType: not null };

    /// <summary>The variable or constant a script declares by its name's offset, its own class's or an inner class's; null for none.</summary>
    private static GDScriptAssignable? AssignableAt(IEnumerable<(GDScriptNode Node, int Offset)> members, int start)
    {
        foreach ((GDScriptNode node, int offset) in members)
        {
            if (node is GDScriptAssignable { Identifier: { } name } assignable and (GDScriptVariable or GDScriptConstant) && offset + name.Start == start)
            {
                return assignable;
            }
            if (node is GDScriptClass inner && AssignableAt(inner.Members.Select(m => (m, offset)), start) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>Whether a value of <paramref name="source"/>'s type may be given where <paramref name="target"/>'s is asked
    /// for, as Godot's check_type_compatibility has it: anything as a Variant, a Variant as anything; a built-in type as itself
    /// (with <paramref name="implicitly"/>, one converting to it strictly), an enum's value as an int, typed collections by
    /// their element types; an enum as its own or an int; null as any object, an object as its class or one it extends.</summary>
    private bool TypeCompatible(GDScriptType target, GDScriptType source, bool implicitly = false)
    {
        if (target.IsVariant || source.IsVariant)
        {
            return true;
        }
        if (target.Kind == GDScriptTypeKind.Builtin)
        {
            // An object's Variant type is Object, which some built-in types (RID) take, an enum's value's an int and an enum's a Dictionary.
            string variantType = source.Kind switch
            {
                GDScriptTypeKind.Native or GDScriptTypeKind.Class => "Object",
                GDScriptTypeKind.Enum => source.Meta ? "Dictionary" : "int",
                _ => source.Name,
            };
            bool valid = source.Kind == GDScriptTypeKind.Builtin && source.Name == target.Name
                || implicitly && (variantType == target.Name || GDScriptConversions.From(target.Name).Contains(variantType))
                || target.Name == "int" && source is { Kind: GDScriptTypeKind.Enum, Meta: false };
            if (valid && target.Name is "Array" or "Dictionary" && source.Kind == GDScriptTypeKind.Builtin && source.Name == target.Name)
            {
                for (int i = 0; i < Math.Min(target.Elements.Count, source.Elements.Count); i++)
                {
                    valid &= target.Elements[i].ToString() == source.Elements[i].ToString();
                }
            }
            return valid;
        }
        if (target.Kind == GDScriptTypeKind.Enum)
        {
            return source is { Kind: GDScriptTypeKind.Builtin, Name: "int" } || source.Kind == GDScriptTypeKind.Enum && source.ToString() == target.ToString();
        }
        if (source is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" })
        {
            return true;
        }
        if (source.Kind is GDScriptTypeKind.Builtin or GDScriptTypeKind.Enum || target.Meta)
        {
            return false;
        }
        if (source.Meta)
        {
            // A class is a script resource, an engine class its native class.
            return target.Kind == GDScriptTypeKind.Native && Engine?.Extends(source.Kind == GDScriptTypeKind.Class ? "GDScript" : "GDScriptNativeClass", target.Name) == true;
        }
        int depth = 0;
        for (GDScriptType? at = source.Instance; at is not null && depth++ < 64; at = BaseOf(at))
        {
            if (at.Kind == target.Kind && (at.Kind == GDScriptTypeKind.Native ? at.Name == target.Name : at.Script == target.Script && at.Name == target.Name))
            {
                return true;
            }
            if (at.Kind == GDScriptTypeKind.Native && target.Kind == GDScriptTypeKind.Native)
            {
                return Engine?.Extends(at.Name, target.Name) == true;
            }
        }
        return false;
    }

    /// <summary>Godot's errors of a call's arguments against what is called (validate_call_arg): too few or too many, and
    /// each one of a type its parameter takes none of, a constant's said as its conversion first.</summary>
    private void Arguments(GDScriptCall call, IReadOnlyList<GDScriptType> parameters, int defaults, bool varargs, Context context)
    {
        string name = call.FunctionName ?? string.Empty;
        for (int i = 0; i < call.Arguments.Count && i < parameters.Count; i++)
        {
            if (parameters[i].Hard)
            {
                TypedLiteralErrors(call.Arguments[i], parameters[i], context);
            }
        }
        if (call.Arguments.Count < parameters.Count - defaults)
        {
            Error(context, call, $"Too few arguments for \"{name}()\" call. Expected at least {parameters.Count - defaults} but received {call.Arguments.Count}.");
        }
        if (!varargs && call.Arguments.Count > parameters.Count)
        {
            Error(context, call.Arguments[parameters.Count], $"Too many arguments for \"{name}()\" call. Expected at most {parameters.Count} but received {call.Arguments.Count}.");
        }
        for (int i = 0; i < call.Arguments.Count && i < parameters.Count; i++)
        {
            GDScriptType wanted = parameters[i];
            GDScriptType given = context.Analysis.TypeOf(call.Arguments[i]);
            if (!wanted.Hard || wanted.IsVariant || given.IsVariant || !given.Hard || !Sure(call.Arguments[i], context))
            {
                continue;
            }
            ConstantConverted(call.Arguments[i], given, wanted, "pass", context);
            if (!TypeCompatible(wanted, given, implicitly: true) && !TypeCompatible(given, wanted))
            {
                Error(context, call.Arguments[i], $"Invalid argument for \"{name}()\" function: argument {i + 1} should be \"{wanted}\" but is \"{given}\".");
            }
        }
    }

    /// <summary>What a call's function takes, where Godot's reduce_call checks it: a function or method of self (or through
    /// super), of what an attribute is called on, or a utility function; null for a built-in type's constructor, a call of
    /// a name not found, or what is called on a value of no known type.</summary>
    private (IReadOnlyList<GDScriptType> Parameters, int Defaults, bool Varargs)? CalledSignature(GDScriptCall call, GDScriptIdentifier? called, Context context)
    {
        if (called is null)
        {
            return null;
        }
        GDScriptDeclaration? declared = context.Analysis.Declared.GetValueOrDefault(called);
        switch (call.Callee)
        {
            case GDScriptIdentifier name when !call.IsSuper:
                if (context.Scope?.Find(name.Name) is not null || Engine?.Builtins.ContainsKey(name.Name) == true || IsBuiltinName(name.Name))
                {
                    return null;
                }
                // A utility function's name calls it before any method of the class (Godot's reduce_call).
                if (Engine?.Functions.ContainsKey(name.Name) == true)
                {
                    return SignatureOf(null, null, name.Name);
                }
                return declared is not null ? SignatureOf(declared, context.Class, name.Name)
                    : Member(context.Class, name.Name) is null ? SignatureOf(null, null, name.Name) : null;
            case GDScriptSubscript { IsAttribute: true, Base: { } taken } when context.Analysis.Types.GetValueOrDefault(taken) is { IsVariant: false } owner:
                return owner.Kind == GDScriptTypeKind.Builtin ? SignatureOf(null, owner, called.Name) : declared is not null ? SignatureOf(declared, owner, called.Name) : null;
            default:
                return call.IsSuper && declared is not null ? SignatureOf(declared, BaseOf(context.Class), called.Name) : null;
        }
    }

    /// <summary>What a method or function a call reaches takes, where its declaration is read: a script's function's
    /// parameters as their types are written, an engine method's or utility function's as the API gives them; null where
    /// what is called is not known.</summary>
    private (IReadOnlyList<GDScriptType> Parameters, int Defaults, bool Varargs)? SignatureOf(GDScriptDeclaration? declared, GDScriptType? owner, string name)
    {
        if (declared is { Script: { } script, Start: var start } && Tree(script) is { } tree
            && FunctionIn(ScriptClass(tree, script), tree.Members.Select(m => (m.Node, m.Offset)), start) is ({ } function, { } inClass, int offset))
        {
            // Its parameters' types are read in its own class's scope, an inner class's enums before its outer's.
            Context reading = new(new GDScriptAnalysis(null, script), inClass, offset, null, function);
            List<GDScriptType> types = [.. function.Parameters.Select(p => p.Type is { } written ? ResolveType(written, reading) : GDScriptType.Variant.Soft)];
            return (types, function.Parameters.Count(p => p.Initializer is not null), function.Rest is not null);
        }
        GodotApiMethod? method = owner switch
        {
            { Kind: GDScriptTypeKind.Builtin } builtin => Engine?.Builtins.GetValueOrDefault(builtin.Name)?.Methods.GetValueOrDefault(name),
            _ when declared?.EngineClass is { } engine => Engine?.Chain(engine).Select(c => c.Methods.GetValueOrDefault(name)).FirstOrDefault(m => m is not null),
            null => Engine?.Functions.GetValueOrDefault(name),
            _ => null,
        };
        // GDScript's own functions are listed without their parameters.
        if (method is null || method.Parameters.Count < method.Required)
        {
            return null;
        }
        // A utility function taking any number (print) asks for none of them.
        int defaults = owner is null && method.Varargs ? method.Parameters.Count : method.Parameters.Count - method.Required;
        return ([.. method.Parameters.Select(p => FromApi(p.Type) with { Hard = true })], defaults, method.Varargs);
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
            if (based is not null && call.FunctionName is { } absent && context.Analysis.Tree is not null && Knows(context.Class))
            {
                SuperMissing(call, based, absent, method, context);
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
                    // A local is no method (its Callable is called through call()); a built-in type's or utility's name is one.
                    if (!(Engine?.Builtins.ContainsKey(name.Name) == true || IsBuiltinName(name.Name) || Engine?.Functions.ContainsKey(name.Name) == true))
                    {
                        NotFoundOnSelf(call, name, context);
                    }
                    return GDScriptType.Variant.Soft;
                }
                if (Engine?.Builtins.ContainsKey(name.Name) == true && name.Name != "Nil" || IsBuiltinName(name.Name))
                {
                    Paint(context, name, ColourOf(GDScriptType.Builtin(name.Name)));
                    if (name.Name == "Color")
                    {
                        ColorCodeError(call, context);
                    }
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
                    EvaluatedCallErrors(call, name.Name, context);
                    return FromApi(function.Returns);
                }
                if (name.Name == "Object" && context.Analysis.Tree is not null && Engine is not null && Member(context.Class, "Object") is null)
                {
                    Error(context, call, "Invalid constructor \"Object()\", use \"Object.new()\" instead.");
                    return GDScriptType.Variant.Soft;
                }
                NotFoundOnSelf(call, name, context);
                return GDScriptType.Variant.Soft;
            case GDScriptSubscript { IsAttribute: true, Attribute: { } method } attribute:
                // A built-in type's name is its type here, as Godot's reduce_call takes it, never a name on its own.
                GDScriptType owner = attribute.Base is GDScriptIdentifier typeName && context.Scope?.Find(typeName.Name) is null && Member(context.Class, typeName.Name) is null
                    && (Engine?.Builtins.ContainsKey(typeName.Name) == true && typeName.Name != "Nil" || IsBuiltinName(typeName.Name))
                    ? Recorded(typeName, GDScriptType.Builtin(typeName.Name) with { Meta = true }, context)
                    : Reduce(attribute.Base, context);
                if (attribute.Base is not GDScriptSelf && Unsafe(owner, method.Name))
                {
                    Warn(context, call, "UNSAFE_METHOD_ACCESS", method.Name, owner.Name);
                }
                else if (attribute.Base is GDScriptIdentifier variantName && context.Scope?.Find(variantName.Name) is { Local.Node: GDScriptAssignable variant } && OfNoType(variant))
                {
                    Warn(context, call, "UNSAFE_METHOD_ACCESS", method.Name, "Variant");
                }
                TemporaryModification(null, attribute, context);
                context.Analysis.Types[method] = GDScriptType.Builtin("Callable");
                GDScriptType called = MethodCall(owner, method, context);
                if (owner.Kind is not GDScriptTypeKind.Variant)
                {
                    Paint(context, method, GDScriptColour.Method);
                }
                if (context.Analysis.Tree is not null && Engine is not null && !(method.Name == "new" && ConstructionErrors(call, attribute, owner, context)))
                {
                    MethodMissing(call, attribute, owner, method, context);
                }
                if (method.Name == "free" && attribute.Base is GDScriptSelf && Engine?.Version >= new Version(4, 8) && context.Analysis.Tree is not null && Knows(context.Class))
                {
                    Error(context, call, "Calling \"self.free()\" can lead to bugs and crashes. Consider using \"queue_free()\" instead.");
                }
                return called;
            default:
                Reduce(call.Callee, context);
                return GDScriptType.Variant.Soft;
        }
    }

    /// <summary>Godot's errors of a <c>super</c> call: a function the classes it extends lack, an engine's virtual one (a
    /// constructor among them) or a script's abstract one none of them defines.</summary>
    private void SuperMissing(GDScriptCall call, GDScriptType based, string name, GDScriptMemberInfo? method, Context context)
    {
        bool virtualOfEngine = method is { Kind: "func", Declaration.EngineClass: { } engine }
            && Engine!.Chain(engine).Select(c => c.Methods.GetValueOrDefault(name)).FirstOrDefault(m => m is not null) is { IsVirtual: true };
        bool abstractOfScript = method is { Kind: "func", Declaration: { Script: { } script, Start: var start } } && Tree(script) is { } tree
            && FunctionAt(tree.Members.Select(m => (m.Node, m.Offset)), start) is { IsAbstract: true };
        if (virtualOfEngine || method is null && name == "_init")
        {
            Error(context, call, $"Cannot call the parent class' virtual function \"{name}()\" because it hasn't been defined.");
        }
        else if (abstractOfScript)
        {
            Error(context, call, $"Cannot call the parent class' abstract function \"{name}()\" because it hasn't been defined.");
        }
        else if (method is not { Kind: "func" })
        {
            Error(context, call, $"Function \"{name}()\" not found in base {based}.{GodotRenames.Call(name)}");
        }
    }

    /// <summary>The function a script declares by its name's offset with the class it is in and the offset its positions count
    /// from; null for none.</summary>
    private static (GDScriptFunction Function, GDScriptType Class, int Offset)? FunctionIn(GDScriptType at, IEnumerable<(GDScriptNode Node, int Offset)> members, int start)
    {
        foreach ((GDScriptNode node, int offset) in members)
        {
            if (node is GDScriptFunction { Identifier: { } name } function && offset + name.Start == start)
            {
                return (function, at, offset);
            }
            if (node is GDScriptClass { Identifier: { } innerName } inner
                && FunctionIn(new GDScriptType(GDScriptTypeKind.Class, innerName.Name) { Class = inner, Script = at.Script, Offset = offset }, inner.Members.Select(m => (m, offset)), start) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>The function a script declares by its name's offset, its own class's or an inner class's; null for none.</summary>
    private static GDScriptFunction? FunctionAt(IEnumerable<(GDScriptNode Node, int Offset)> members, int start)
    {
        foreach ((GDScriptNode node, int offset) in members)
        {
            if (node is GDScriptFunction { Identifier: { } name } function && offset + name.Start == start)
            {
                return function;
            }
            if (node is GDScriptClass inner && FunctionAt(inner.Members.Select(m => (m, offset)), start) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>A type recorded for an expression, as <see cref="Reduce"/> records what it reduces.</summary>
    private static GDScriptType Recorded(GDScriptExpression expression, GDScriptType type, Context context)
    {
        if (context.Analysis.Tree is not null)
        {
            context.Analysis.Types[expression] = type;
        }
        return type;
    }

    /// <summary>Godot's error of a call by a name the class has no method of (reduce_call on self): the name another member
    /// holds (a Callable, a value of a type), else a function not found, where the class is read whole.</summary>
    private void NotFoundOnSelf(GDScriptCall call, GDScriptIdentifier name, Context context)
    {
        // new() alone is the class's own constructor.
        if (context.Analysis.Tree is null || name.Name == "new" || !Knows(context.Class))
        {
            return;
        }
        NotAFunction(call, context.Class, name.Name, context);
        if (Member(context.Class, name.Name) is { Kind: not "func" } other && !other.Type.IsVariant)
        {
            Error(context, name, other.Type is { Kind: GDScriptTypeKind.Builtin, Name: "Callable" }
                ? $"Name \"{name.Name}\" is a Callable. You can call it with \"{name.Name}.call()\" instead."
                : $"Name \"{name.Name}\" called as a function but is a \"{other.Type}\".");
            return;
        }
        if (!InOuter(context.Class, name.Name))
        {
            Error(context, name, $"Function \"{name.Name}()\" not found in base self.{GodotRenames.Call(name.Name)}");
        }
    }

    /// <summary>Godot's error of a call by the name of a script class's member that is no function (get_function_signature),
    /// a class it extends declaring it so, before any other said of the call.</summary>
    private void NotAFunction(GDScriptCall call, GDScriptType owner, string name, Context context)
    {
        int depth = 0;
        for (GDScriptType? at = owner.Instance; at is { Kind: GDScriptTypeKind.Class } && depth++ < 64; at = BaseOf(at))
        {
            if (OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) named))
            {
                if (named.Node is not GDScriptFunction)
                {
                    Error(context, call, $"Member \"{name}\" is not a function.");
                }
                return;
            }
        }
    }

    /// <summary>Godot's errors of a method a call names that what it is called on lacks (reduce_call), where that is known:
    /// on self, a built-in type's value or type, a class or engine class named, an enum.</summary>
    private void MethodMissing(GDScriptCall call, GDScriptSubscript callee, GDScriptType owner, GDScriptIdentifier method, Context context)
    {
        string name = method.Name;
        if (callee.Base is GDScriptSelf)
        {
            if (Member(context.Class, name) is not { Kind: "func" })
            {
                NotFoundOnSelf(call, method, context);
            }
            return;
        }
        switch (owner)
        {
            case { Kind: GDScriptTypeKind.Variant, Meta: true, Owner: { } native }:
                Error(context, call, $"Native class {native} used in script doesn't exist or isn't exposed.");
                Error(context, call, $"Static function \"{name}()\" not found in base \"Variant\".");
                return;
            case { Kind: GDScriptTypeKind.Enum, Meta: true } enumerated when enumerated.Script is null:
                Error(context, callee, $"The native enum \"{enumerated.Name}\" does not behave like Dictionary and does not have methods of its own.");
                Error(context, call, $"Static function \"{name}()\" not found in base \"{enumerated}\".");
                return;
            case { Kind: GDScriptTypeKind.Enum, Meta: true } enumerated:
                if (Engine!.Builtins.GetValueOrDefault("Dictionary")?.Methods.GetValueOrDefault(name) is not { } dictionary)
                {
                    Error(context, callee, $"Enums only have Dictionary built-in methods. Function \"{name}()\" does not exist for enum \"{enumerated.Name}\".");
                    Error(context, call, $"Static function \"{name}()\" not found in base \"{enumerated}\".");
                }
                else if (!dictionary.IsConst)
                {
                    Error(context, call, $"Cannot call non-const Dictionary function \"{name}()\" on enum \"{enumerated.Name}\".");
                }
                return;
            case { Kind: GDScriptTypeKind.Builtin, Hard: true } builtin when Engine!.Builtins.GetValueOrDefault(builtin.Name) is { } api && !api.Methods.ContainsKey(name):
                bool member = !builtin.Meta && api.Properties.ContainsKey(name) || builtin.Meta && (api.Constants.ContainsKey(name) || api.Enums.ContainsKey(name));
                if (member)
                {
                    Error(context, callee, $"Name \"{name}\" called as a function but is a \"{MemberOf(builtin, name, context, null)}\".");
                }
                else if (builtin.Meta)
                {
                    Error(context, method, $"Cannot find member \"{name}\" in base \"{builtin.Instance}\".");
                    Error(context, call, $"Static function \"{name}()\" not found in base \"{builtin}\".");
                }
                else if (builtin.Name != "Nil")
                {
                    // A Dictionary's names are its keys, never missing members; any other type's are its own.
                    if (builtin.Name != "Dictionary")
                    {
                        Error(context, method, $"Cannot find member \"{name}\" in base \"{builtin}\".");
                    }
                    Error(context, callee, $"Function \"{name}()\" not found in base {builtin}.{GodotRenames.Call(name)}");
                }
                return;
            case { Kind: GDScriptTypeKind.Class, Meta: false } instance when Knows(instance) && Member(instance, name) is { Kind: not "func" } held:
                NotAFunction(call, instance, name, context);
                if (!held.Type.IsVariant)
                {
                    Error(context, callee, held.Type is { Kind: GDScriptTypeKind.Builtin, Name: "Callable" }
                        ? $"Name \"{name}\" is a Callable. You can call it with \"{name}.call()\" instead."
                        : $"Name \"{name}\" called as a function but is a \"{held.Type}\".");
                }
                return;
            case { Kind: GDScriptTypeKind.Native or GDScriptTypeKind.Class, Meta: true } named when name != "new" && Knows(named):
                if (named.Kind == GDScriptTypeKind.Class)
                {
                    NotAFunction(call, named, name, context);
                }
                if (Member(named.Instance, name) is { Kind: not "func" } other && !other.Type.IsVariant)
                {
                    Error(context, callee, other.Type is { Kind: GDScriptTypeKind.Builtin, Name: "Callable" }
                        ? $"Name \"{name}\" is a Callable. You can call it with \"{name}.call()\" instead."
                        : $"Name \"{name}\" called as a function but is a \"{other.Type}\".");
                }
                else if (Member(named.Instance, name) is null)
                {
                    Error(context, call, $"Static function \"{name}()\" not found in base \"{named}\".");
                }
                return;
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
                // A typed array's element and the array itself, which Godot's API gives as Variant and Array: known, never hard.
                if (owner is { Name: "Array", Elements: [{ } element, ..] } && builtin?.Returns == "Variant" && method.Name is "front" or "back" or "pick_random"
                    or "pop_back" or "pop_front" or "pop_at" or "get" or "max" or "min")
                {
                    returns = element.Soft;
                }
                if (owner is { Name: "Array" } && method.Name is "duplicate" or "slice" or "filter")
                {
                    returns = owner.Instance.Soft;
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
        if (assignment.Operator == K.Equal && target is { Kind: GDScriptTypeKind.Enum, Meta: false } && value is { Kind: GDScriptTypeKind.Builtin, Name: "int" } && assignment.Value is { } integer)
        {
            Warn(context, integer, "INT_AS_ENUM_WITHOUT_CAST");
        }
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
        if (context.Analysis.Tree is not null && assignment.Assignee is { } assignee && assignment.Value is { } assignedValue && Knows(context.Class))
        {
            AssignmentErrors(assignment, assignee, target, assignedValue, value, context);
        }
        return GDScriptType.Variant.Soft;
    }

    /// <summary>Godot's errors of an assignment (reduce_assignment): to a constant, to an engine's property with no setter
    /// or a value held in one, or with <c>=</c> of a value of a type the variable's takes none of, a constant's said as its
    /// conversion first.</summary>
    private void AssignmentErrors(GDScriptAssignment assignment, GDScriptExpression assignee, GDScriptType target, GDScriptExpression assigned, GDScriptType value, Context context)
    {
        if (IsConstant(assignee, context) || assignee is GDScriptSubscript { Base: { } held } && IsConstant(held, context)
            && context.Analysis.TypeOf(held).Kind is not (GDScriptTypeKind.Class or GDScriptTypeKind.Native))
        {
            Error(context, assignee, "Cannot assign a new value to a constant.");
            return;
        }
        if (ReadOnlyProperty(assignee, context) || HeldInReadOnly(assignee, context))
        {
            Error(context, assignee, "Cannot assign a new value to a read-only property.");
            return;
        }
        if (target.Hard && Sure(assignee, context))
        {
            TypedLiteralErrors(assigned, target, context);
        }
        if (assignment.Operator != K.Equal)
        {
            CompoundErrors(assignment, assignee, target, assigned, value, context);
            return;
        }
        if (target.IsVariant || !target.Hard || value.IsVariant || !value.Hard || !Sure(assigned, context) || !Sure(assignee, context))
        {
            return;
        }
        ConstantConverted(assigned, value, target, "assign", context);
        if (!TypeCompatible(target, value, implicitly: true) && !TypeCompatible(value, target))
        {
            Error(context, assigned, $"Value of type \"{value}\" cannot be assigned to a variable of type \"{target}\".");
        }
    }

    /// <summary>Whether an expression is a name Godot holds constant: a local constant, a constant, enum, enum value, signal,
    /// function or inner class of the class, a class or engine class by its name, an enum's value read off the enum, or one
    /// of those read off a value whose type is sure.</summary>
    private bool IsConstant(GDScriptExpression expression, Context context) => expression switch
    {
        GDScriptIdentifier name => context.Scope?.Find(name.Name) is { } local ? local.Local?.Kind == LocalKind.Constant
            : Member(context.Class, name.Name) is { } member ? member.Kind is "const" or "enum" or "signal" or "func" or "class"
            : GlobalOrScopedType(name.Name, context.Class) is { Kind: GDScriptTypeKind.Class or GDScriptTypeKind.Native, Meta: true },
        GDScriptSubscript { IsAttribute: true, Base: { } taken } when context.Analysis.TypeOf(taken) is { Kind: GDScriptTypeKind.Enum, Meta: true } => true,
        GDScriptSubscript { IsAttribute: true, Base: { } taken, Attribute: { } read } => SureHolder(taken, context) && context.Analysis.TypeOf(taken) is { IsVariant: false } owner
            && !(owner.Meta && InstanceOnly(owner, read.Name)) && Member(owner, read.Name) is { Kind: "const" or "enum" or "signal" or "func" or "class" },
        _ => false,
    };

    /// <summary>Whether an expression is an engine class's property that has no setter, read off a value whose type is sure
    /// or by its name in a class built on that engine class.</summary>
    private bool ReadOnlyProperty(GDScriptExpression expression, Context context) => expression switch
    {
        GDScriptIdentifier name => context.Scope?.Find(name.Name) is null && NativeReadOnly(context.Class, name.Name),
        GDScriptSubscript { IsAttribute: true, Base: { } taken, Attribute: { } read } => SureHolder(taken, context)
            && context.Analysis.TypeOf(taken) is { Meta: false, Kind: GDScriptTypeKind.Native or GDScriptTypeKind.Class } owner && NativeReadOnly(owner, read.Name),
        _ => false,
    };

    /// <summary>Whether a member is read off what Passenger is sure of the type of as Godot is: <see cref="Sure"/>'s, self,
    /// or what an engine's method called on self returns.</summary>
    private bool SureHolder(GDScriptExpression taken, Context context) => taken is GDScriptSelf || Sure(taken, context)
        || taken is GDScriptCall { Callee: GDScriptIdentifier method } && context.Scope?.Find(method.Name) is null && Member(context.Class, method.Name) is { Kind: "func", Declaration.Script: null };

    /// <summary>Whether a subscript changes a value of a built-in type held by copy in a read-only property, through the
    /// read-only properties it is taken of (<c>state.center_of_mass.x</c>).</summary>
    private bool HeldInReadOnly(GDScriptExpression assignee, Context context)
    {
        for (GDScriptExpression? at = assignee; at is GDScriptSubscript { Base: { } taken }; at = taken)
        {
            if (!ReadOnlyProperty(taken, context))
            {
                return false;
            }
            if (context.Analysis.TypeOf(taken) is { Kind: GDScriptTypeKind.Builtin, Hard: true } held && !SharedBuiltins.Contains(held.Name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a type's member of a name is an engine class's property that has no setter, none of its script
    /// classes declaring it first.</summary>
    private bool NativeReadOnly(GDScriptType type, string name)
    {
        GDScriptType? at = type.Instance;
        for (int depth = 0; at is not null && depth < 64; depth++, at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                return Engine?.Chain(at.Name).FirstOrDefault(c => c.Properties.ContainsKey(name)) is { } owner && owner.ReadOnly.Contains(name);
            }
            if (at.Kind != GDScriptTypeKind.Class || OwnNames(at).ContainsKey(name))
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Godot's errors of a class's <c>new()</c> (reduce_call, get_function_signature): an engine singleton's, an
    /// abstract class's, and one built on an engine class that cannot be made, with what the call is then; whether no
    /// other error of the call is said.</summary>
    private bool ConstructionErrors(GDScriptCall call, GDScriptSubscript callee, GDScriptType owner, Context context)
    {
        if (callee.Base is GDScriptIdentifier named && context.Scope?.Find(named.Name) is null && Member(context.Class, named.Name) is null
            && Engine!.Singletons.ContainsKey(named.Name) && Engine.Classes.ContainsKey(named.Name))
        {
            Error(context, call, $"Cannot construct native class \"{named.Name}\" because it is an engine singleton.");
            return true;
        }
        if (!owner.Meta || owner.Kind is not (GDScriptTypeKind.Class or GDScriptTypeKind.Native) || !Knows(owner))
        {
            return false;
        }
        if (owner is { Kind: GDScriptTypeKind.Class, Class.IsAbstract: true })
        {
            Error(context, call, $"Cannot construct abstract class \"{owner.Instance}\".");
        }
        if (AbstractNative(owner) is not { } native)
        {
            return false;
        }
        Error(context, call, owner.Kind == GDScriptTypeKind.Native ? $"Native class \"{native}\" cannot be constructed as it is abstract."
            : $"Class \"{QualifiedFile(owner)}\" cannot be constructed as it is based on abstract native class \"{native}\".");
        Error(context, callee, "Name \"new\" is a Callable. You can call it with \"new.call()\" instead.");
        return true;
    }

    /// <summary>A class's name as Godot's fqcn ends: its script's file, then each inner class holding it and its own name.</summary>
    private string QualifiedFile(GDScriptType type)
    {
        string file = type.Script is { } path ? path[(path.LastIndexOf('/') + 1)..] : type.Name;
        if (type.Script is not { } script || Tree(script) is not { } tree || type.Class is not { Identifier: { } own } inner || inner == tree.Class)
        {
            return file;
        }
        List<GDScriptType> holders = Enclosing(ScriptClass(tree, script), tree.Members.Select(m => (m.Node, m.Offset)), inner);
        return string.Join("::", [file, .. holders.Skip(1).Select(h => h.Name), own.Name]);
    }

    /// <summary>Whether an expression is one Godot folds to a constant: a literal, a negative one, a constant's name, an
    /// enum's value, or a constant read off a built-in type, an enum or a class.</summary>
    private bool Folded(GDScriptExpression expression, Context context) => Constant(expression) is not null || expression switch
    {
        GDScriptIdentifier name => context.Scope?.Find(name.Name) is { } local ? local.Local?.Kind == LocalKind.Constant
            : Member(context.Class, name.Name) is { Kind: "const" or "enum" },
        GDScriptSubscript { IsAttribute: true, Base: { } taken, Attribute: { } member } => context.Analysis.TypeOf(taken) switch
        {
            { Kind: GDScriptTypeKind.Enum, Meta: true } => true,
            { Kind: GDScriptTypeKind.Builtin, Meta: true } builtin => Engine?.Builtins.GetValueOrDefault(builtin.Name)?.Constants.ContainsKey(member.Name) == true,
            { Kind: GDScriptTypeKind.Class, Meta: true } owner => Member(owner.Instance, member.Name) is { Kind: "const" or "enum" },
            _ => false,
        },
        _ => false,
    };

    private GDScriptType Binary(GDScriptBinary binary, Context context)
    {
        GDScriptType left = Reduce(binary.Left, context);
        GDScriptType right = Reduce(binary.Right, context);
        BinaryErrors(binary, left, right, context);
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
