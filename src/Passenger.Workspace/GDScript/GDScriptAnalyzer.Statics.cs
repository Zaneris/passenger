namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of what a static place reaches (reduce_call's, reduce_identifier's and reduce_get_node's
/// static checks): an instance's function called or named, its variable or signal read, <c>$</c> used, and a class's
/// function called on the class itself; each said where the class is read whole.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>Godot's name for a static variable's setter or getter, the static function its body is; null for a variable
    /// that is not static.</summary>
    private static string? StaticAccessor(GDScriptVariable variable, string kind) =>
        variable is { IsStatic: true, Identifier: { } name } ? $"@{name.Name}_{kind}" : null;

    /// <summary>A call's static errors: a function of the instance called by its name in a static place, or a function no
    /// instance is needed for called on a class, an engine class or a built-in type as it is.</summary>
    private void StaticCallErrors(GDScriptCall call, Context context)
    {
        if (call.IsSuper || call.FunctionName is not { } name || name == "new")
        {
            return;
        }
        switch (call.Callee)
        {
            case GDScriptIdentifier own when context.Static is { } within && context.Scope?.Find(own.Name) is null
                && !(Engine?.Builtins.ContainsKey(own.Name) == true || IsBuiltinName(own.Name) || Engine?.Functions.ContainsKey(own.Name) == true)
                && Member(context.Class, own.Name) is { Kind: "func" } && !StaticFunction(context.Class, own.Name):
                Error(context, call, within.Length == 0 ? $"Cannot call non-static function \"{name}()\" from a static variable initializer."
                    : $"Cannot call non-static function \"{name}()\" from the static function \"{within}()\".");
                break;
            case GDScriptSubscript { IsAttribute: true, Base: { } taken }
                when context.Analysis.Types.GetValueOrDefault(taken) is { Meta: true, Kind: GDScriptTypeKind.Builtin or GDScriptTypeKind.Native or GDScriptTypeKind.Class } owner
                    && Whole(owner) && (owner.Kind == GDScriptTypeKind.Builtin ? Engine?.Builtins.GetValueOrDefault(owner.Name)?.Methods.ContainsKey(name) == true
                        : Member(owner.Instance, name) is { Kind: "func" }) && !StaticFunction(owner, name):
                Error(context, call, $"Cannot call non-static function \"{name}()\" on the class \"{owner.Instance}\" directly. Make an instance instead.");
                break;
            default:
                break;
        }
    }

    /// <summary>A name's error where it reads the instance from a static place: a variable not static (an engine class's
    /// property among them), a function not static, or a signal.</summary>
    private void StaticAccessErrors(GDScriptIdentifier identifier, Context context)
    {
        if (context.Static is { } within && context.Analysis.Tree is not null && Knows(context.Class) && StaticSource(context.Class, identifier.Name) is { } source)
        {
            Error(context, identifier, within.Length == 0 ? $"Cannot access {source} \"{identifier.Name}\" from a static variable initializer."
                : $"Cannot access {source} \"{identifier.Name}\" from the static function \"{within}()\".");
        }
    }

    /// <summary><c>$</c>'s or <c>%</c>'s errors: in a class that is no node, then in a static place.</summary>
    private void GetNodeErrors(GDScriptGetNode get, Context context)
    {
        if (context.Analysis.Tree is null || !Knows(context.Class))
        {
            return;
        }
        char mark = get.UsesDollar ? '$' : '%';
        if (NativeBase(context.Class) is { } native && Engine?.Extends(native, "Node") == false)
        {
            Error(context, get, $"Cannot use shorthand \"get_node()\" notation (\"{mark}\") on a class that isn't a node.");
        }
        else if (context.Static is not null)
        {
            Error(context, get, $"Cannot use shorthand \"get_node()\" notation (\"{mark}\") in a static function.");
        }
    }

    /// <summary>Whether a function a class or built-in type has by a name is static: a script's as it is written, an engine
    /// class's or built-in type's as the API says.</summary>
    private bool StaticFunction(GDScriptType type, string name)
    {
        if (type.Kind == GDScriptTypeKind.Builtin)
        {
            return Engine?.Builtins.GetValueOrDefault(type.Name)?.Methods.GetValueOrDefault(name) is { IsStatic: true };
        }
        GDScriptType? at = type.Instance;
        for (int depth = 0; at is not null && depth < 64; depth++, at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                return Engine?.Chain(at.Name).Select(c => c.Methods.GetValueOrDefault(name)).FirstOrDefault(m => m is not null) is { IsStatic: true };
            }
            if (at.Kind == GDScriptTypeKind.Class && OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) owned))
            {
                return owned.Node is GDScriptFunction { IsStatic: true };
            }
        }
        return false;
    }

    /// <summary>What a member of a class is that a static place can't read, in Godot's words (<c>non-static variable</c>,
    /// <c>non-static function</c>, <c>signal</c>); null for a static one, a constant, an enum, a class or none.</summary>
    private string? StaticSource(GDScriptType type, string name)
    {
        GDScriptType? at = type.Instance;
        for (int depth = 0; at is not null && depth < 64; depth++, at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                foreach (GodotApiClass engine in Engine?.Chain(at.Name) ?? [])
                {
                    if (engine.Properties.ContainsKey(name))
                    {
                        return "non-static variable";
                    }
                    if (engine.Methods.GetValueOrDefault(name) is { } method)
                    {
                        return method.IsStatic ? null : "non-static function";
                    }
                    if (engine.Signals.ContainsKey(name))
                    {
                        return "signal";
                    }
                }
                return null;
            }
            if (at.Kind == GDScriptTypeKind.Class && OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) owned))
            {
                return owned.Node switch
                {
                    GDScriptVariable { IsStatic: false } => "non-static variable",
                    GDScriptFunction { IsStatic: false } => "non-static function",
                    GDScriptSignal => "signal",
                    _ => null,
                };
            }
        }
        return null;
    }

    /// <summary>The engine class a class is built on at the end of its bases; null where one between is not read.</summary>
    private string? NativeBase(GDScriptType type)
    {
        GDScriptType? at = type.Instance;
        for (int depth = 0; at is not null && depth < 64; depth++, at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                return at.Name;
            }
        }
        return null;
    }
}
