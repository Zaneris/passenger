namespace Passenger.Workspace.GDScript;

/// <summary>What running a part of a script can do to what else runs: nothing another part could tell, read what another
/// part may change, or change or reach what another part may read or see.</summary>
internal enum GDScriptEffect
{
    None,
    Reads,
    Acts,
}

/// <summary>What a script's expressions do when they run and the order Godot's compiler runs them in, for a refactoring that
/// moves code: a call's arguments before what it is called on; an assignment's value after the object it assigns into but
/// before the last index; a plain variable of the script's class or a script it is built on only as what takes it runs, so
/// after everything else that takes part with it. What can't be known to be harmless acts. Pure.</summary>
internal sealed class GDScriptEffects
{
    /// <summary>The functions that only work out what they give from what they are given: Godot's math utilities, the
    /// general ones that say what a value is, GDScript's own.</summary>
    private static readonly HashSet<string> Calculations = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "sinh", "cosh", "tanh", "asin", "acos", "atan", "atan2", "asinh", "acosh", "atanh", "sqrt", "fmod", "fposmod", "posmod", "floor", "floorf",
        "floori", "ceil", "ceilf", "ceili", "round", "roundf", "roundi", "abs", "absf", "absi", "sign", "signf", "signi", "snapped", "snappedf", "snappedi", "pow", "log",
        "exp", "is_nan", "is_inf", "is_equal_approx", "is_zero_approx", "is_finite", "ease", "step_decimals", "lerp", "lerpf", "cubic_interpolate", "cubic_interpolate_angle",
        "cubic_interpolate_in_time", "cubic_interpolate_angle_in_time", "bezier_interpolate", "bezier_derivative", "angle_difference", "lerp_angle", "inverse_lerp", "remap",
        "smoothstep", "move_toward", "rotate_toward", "deg_to_rad", "rad_to_deg", "linear_to_db", "db_to_linear", "wrap", "wrapi", "wrapf", "max", "maxi", "maxf", "min",
        "mini", "minf", "clamp", "clampi", "clampf", "nearest_po2", "pingpong", "typeof", "type_convert", "str", "error_string", "type_string", "var_to_str", "str_to_var",
        "hash", "is_instance_id_valid", "is_instance_valid", "is_same", "len", "range", "char", "ord", "convert", "Color8", "type_exists", "is_instance_of", "load",
    };

    private readonly GDScriptAnalysis analysis;

    private readonly HashSet<int> locals = [];

    public GDScriptEffects(GDScriptAnalysis analysis)
    {
        this.analysis = analysis;
        foreach (GDScriptMember member in analysis.Tree?.Members ?? [])
        {
            foreach (GDScriptNode node in GDScriptRefactorings.Below(member.Node).OfType<GDScriptFunction>().SelectMany(GDScriptRefactorings.Below))
            {
                GDScriptIdentifier? name = node switch
                {
                    GDScriptAssignable assignable => assignable.Identifier,
                    GDScriptFor loop => loop.Variable,
                    GDScriptPattern pattern => pattern.Bind,
                    _ => null,
                };
                if (name is not null)
                {
                    locals.Add(member.Offset + name.Start);
                }
            }
        }
    }

    /// <summary>Whether two parts may run in either order.</summary>
    public static bool Commute(GDScriptEffect a, GDScriptEffect b) => a == GDScriptEffect.None || b == GDScriptEffect.None || a == GDScriptEffect.Reads && b == GDScriptEffect.Reads;

    /// <summary>What an expression does as it runs, all it holds counted.</summary>
    public GDScriptEffect Of(GDScriptExpression expression) => InOrder(expression).Select(Own).DefaultIfEmpty().Max();

    /// <summary>What a node of an order does itself, what it holds aside.</summary>
    public GDScriptEffect Own(GDScriptNode node) => node switch
    {
        GDScriptIdentifier name => OfName(name),
        GDScriptGetNode => GDScriptEffect.Reads,
        GDScriptAwait => GDScriptEffect.Acts,
        GDScriptAssignment { Assignee: GDScriptIdentifier target } when IsLocal(target) => GDScriptEffect.None,
        GDScriptAssignment => GDScriptEffect.Acts,
        GDScriptCall call => OfCall(call),
        GDScriptSubscript subscript => OfSubscript(subscript),
        _ => GDScriptEffect.None,
    };

    private bool IsLocal(GDScriptIdentifier name) => analysis.DeclarationOf(name) is { } declared && declared.Script == analysis.Path && locals.Contains(declared.Start);

    private GDScriptEffect OfName(GDScriptIdentifier name)
    {
        if (analysis.TypeOf(name).Meta || IsLocal(name) || name.Name is "PI" or "TAU" or "INF" or "NAN")
        {
            return GDScriptEffect.None;
        }
        if (analysis.DeclarationOf(name) is { } declared)
        {
            if (declared.Member is GDScriptConstant or GDScriptEnum or GDScriptFunction)
            {
                return GDScriptEffect.None;
            }
            if (declared.EngineClass is { } owner && analysis.Engine?.Chain(owner).Any(c => c.Constants.ContainsKey(name.Name) || c.Enums.Values.Any(e => e.Values.ContainsKey(name.Name))) == true)
            {
                return GDScriptEffect.None;
            }
        }
        return analysis.Engine is { } engine && (engine.GlobalConstants.Contains(name.Name) || engine.GlobalEnumValues.ContainsKey(name.Name)) ? GDScriptEffect.None : GDScriptEffect.Reads;
    }

    private GDScriptEffect OfSubscript(GDScriptSubscript subscript)
    {
        if (subscript.Base is not { } on)
        {
            return GDScriptEffect.Reads;
        }
        GDScriptType type = analysis.TypeOf(on);
        bool variable = subscript is { IsAttribute: true, Attribute: { } member } && analysis.DeclarationOf(member)?.Member is GDScriptVariable;
        return type.Meta && !variable || HoldsItsValue(type) ? GDScriptEffect.None : GDScriptEffect.Reads;
    }

    /// <summary>Whether a value of a type is its own, no other name reaching it: a built-in type that is no reference.</summary>
    public static bool HoldsItsValue(GDScriptType type) => type is { Kind: GDScriptTypeKind.Builtin, Hard: true, Meta: false } && type.Name != "Nil" && !GDScriptConversions.IsShared(type.Name);

    private GDScriptEffect OfCall(GDScriptCall call)
    {
        if (call.IsSuper)
        {
            return GDScriptEffect.Acts;
        }
        if (call.Callee is GDScriptIdentifier name)
        {
            return analysis.DeclarationOf(name) switch
            {
                { Script: not null } => GDScriptEffect.Acts,
                { EngineClass: { } owner } when analysis.Engine is { } engine => OfMethod(engine.Chain(owner).Select(c => c.Methods.GetValueOrDefault(name.Name)).OfType<GodotApiMethod>().FirstOrDefault(), false),
                _ => GDScriptConversions.IsBuiltin(name.Name) || Calculations.Contains(name.Name) ? GDScriptEffect.None : GDScriptEffect.Acts,
            };
        }
        if (call.Callee is not GDScriptSubscript { IsAttribute: true, Base: { } on, Attribute.Name: var method })
        {
            return GDScriptEffect.Acts;
        }
        GDScriptType type = analysis.TypeOf(on);
        if (type is { Meta: true, Kind: GDScriptTypeKind.Native } && method == "new" || type is { Meta: true, Kind: GDScriptTypeKind.Builtin } && MethodOf(type, method) is { IsStatic: true })
        {
            return GDScriptEffect.None;
        }
        return type.Name is "Callable" or "Signal" ? GDScriptEffect.Acts : OfMethod(MethodOf(type, method), HoldsItsValue(type));
    }

    private GodotApiMethod? MethodOf(GDScriptType type, string method) => type.Kind switch
    {
        GDScriptTypeKind.Builtin => analysis.Engine?.Builtins.GetValueOrDefault(type.Name)?.Methods.GetValueOrDefault(method),
        GDScriptTypeKind.Native => analysis.Engine?.Chain(type.Name).Select(c => c.Methods.GetValueOrDefault(method)).OfType<GodotApiMethod>().FirstOrDefault(),
        _ => null,
    };

    /// <summary>An engine's method by what the dump says of it: one that leaves what it is called on alone and runs nothing
    /// it is given reads, or on a value of its own does nothing else could tell.</summary>
    private static GDScriptEffect OfMethod(GodotApiMethod? method, bool onOwnValue) =>
        method is not { IsConst: true, Varargs: false } || method.Parameters.Any(p => p.Type == "Callable") ? GDScriptEffect.Acts
        : onOwnValue ? GDScriptEffect.None : GDScriptEffect.Reads;

    /// <summary>Whether an expression makes a new array, dictionary, callable or object each time it runs, its identity another's.</summary>
    public bool Makes(GDScriptExpression expression) => InOrder(expression).Any(n => n is GDScriptArray or GDScriptDictionary or GDScriptLambda
        || n is GDScriptCall { Callee: GDScriptIdentifier { Name: var type } } && GDScriptConversions.IsShared(type)
        || n is GDScriptCall { Callee: GDScriptSubscript { IsAttribute: true, Attribute.Name: "new" } });

    /// <summary>Whether Godot reads an expression only as what takes it runs: an object's variable of the script's class or one
    /// it is built on with no getter, by its name or through self.</summary>
    public bool Late(GDScriptExpression expression) => expression switch
    {
        GDScriptIdentifier name => IsPlain(name),
        GDScriptSubscript { IsAttribute: true, Base: GDScriptSelf, Attribute: { } name } => IsPlain(name),
        _ => false,
    };

    private bool IsPlain(GDScriptIdentifier name) => analysis.DeclarationOf(name)?.Member is GDScriptVariable { IsStatic: false, Getter: null, GetterName: null };

    /// <summary>Whether an expression may be read as <see cref="Late"/> says, a variable declared in a script not read being
    /// such a one; an engine's property is read where it stands.</summary>
    public bool MayBeLate(GDScriptExpression expression) => expression switch
    {
        GDScriptIdentifier name => Own(name) != GDScriptEffect.None && MayBePlain(name),
        GDScriptSubscript { IsAttribute: true, Base: GDScriptSelf, Attribute: { } name } => MayBePlain(name),
        _ => false,
    };

    private bool MayBePlain(GDScriptIdentifier name) => analysis.DeclarationOf(name) switch
    {
        null => true,
        { Script: null } => false,
        { Member: null } declared when declared.Script != analysis.Path => true,
        _ => IsPlain(name),
    };

    /// <summary>The first part of a statement that runs before an expression in it and can't change places with what
    /// <paramref name="moved"/> says is done there, read late where <paramref name="late"/>; null for none.</summary>
    public GDScriptNode? RunsFirst(GDScriptExpression expression, GDScriptNode statement, GDScriptEffect moved, bool late)
    {
        HashSet<GDScriptNode> own = [.. GDScriptRefactorings.Below(expression)];
        foreach (GDScriptNode node in InOrder(statement, late ? new HashSet<GDScriptExpression> { expression } : null))
        {
            if (own.Contains(node))
            {
                return null;
            }
            if (!Commute(Own(node), moved))
            {
                return node;
            }
        }
        return null;
    }

    /// <summary>A statement's expressions, its blocks aside, in the order Godot runs them, each after what it holds; a
    /// lambda as itself, what it runs waiting for its call. Those <paramref name="late"/> names are read late too. The
    /// parser bounds how deep they go.</summary>
    public IReadOnlyList<GDScriptNode> InOrder(GDScriptNode statement, IReadOnlySet<GDScriptExpression>? late = null)
    {
        List<GDScriptNode> order = [];
        Visit(statement switch
        {
            GDScriptExpression expression => expression,
            GDScriptAssignable assignable => assignable.Initializer,
            GDScriptReturn returned => returned.Value,
            GDScriptIf branch => branch.Condition,
            GDScriptWhile loop => loop.Condition,
            GDScriptFor loop => loop.List,
            GDScriptMatch match => match.Test,
            GDScriptAssert assert => assert.Condition,
            _ => null,
        }, order, late);
        return order;
    }

    private void Visit(GDScriptExpression? node, List<GDScriptNode> order, IReadOnlySet<GDScriptExpression>? late)
    {
        switch (node)
        {
            case null:
                return;
            case GDScriptCall call:
                Operands([.. call.Arguments, call.Callee switch
                {
                    GDScriptSubscript { IsAttribute: true } attribute => attribute.Base,
                    GDScriptIdentifier => null,
                    _ => call.Callee,
                }], order, late);
                break;
            case GDScriptAssignment { Assignee: GDScriptSubscript target } assignment:
                Operands([target.Base, assignment.Value, target.Index], order, late);
                break;
            case GDScriptAssignment assignment:
                Visit(assignment.Value, order, late);
                break;
            case GDScriptSubscript subscript:
                Operands([subscript.Base, subscript.Index], order, late);
                break;
            case GDScriptBinary binary:
                Operands([binary.Left, binary.Right], order, late);
                break;
            case GDScriptArray array:
                Operands(array.Elements, order, late);
                break;
            case GDScriptDictionary dictionary:
                Operands([.. dictionary.Elements.SelectMany(e => new[] { e.Key, e.Value })], order, late);
                break;
            case GDScriptTernary ternary:
                Visit(ternary.Condition, order, late);
                Visit(ternary.True, order, late);
                Visit(ternary.False, order, late);
                break;
            case GDScriptUnary unary:
                Visit(unary.Operand, order, late);
                break;
            case GDScriptCast cast:
                Visit(cast.Operand, order, late);
                break;
            case GDScriptTypeTest test:
                Visit(test.Operand, order, late);
                break;
            case GDScriptAwait waited:
                Visit(waited.Awaited, order, late);
                break;
        }
        order.Add(node);
    }

    /// <summary>What an operation takes, each run in turn, those read late after the rest.</summary>
    private void Operands(IEnumerable<GDScriptExpression?> operands, List<GDScriptNode> order, IReadOnlySet<GDScriptExpression>? late)
    {
        List<GDScriptExpression> held = [];
        foreach (GDScriptExpression operand in operands.OfType<GDScriptExpression>())
        {
            if (late?.Contains(operand) == true || Late(operand))
            {
                held.Add(operand);
            }
            else
            {
                Visit(operand, order, late);
            }
        }
        foreach (GDScriptExpression operand in held)
        {
            Visit(operand, order, late);
        }
    }
}
