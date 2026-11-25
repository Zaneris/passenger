using System.Globalization;
using System.Reflection;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>Designer code's values made as the compiled code would make them: literals, the class's components by name, an
/// enum's members ored together, static fields and properties, new objects and arrays, static methods' results, casts,
/// typeof, each converted to the type it is given to as C# converts a constant (an int to a float, a number to an enum).</summary>
internal sealed class Values(Types types, Func<string, object?> component)
{
    /// <summary>A value made, for a place of the type given where one is known (an argument's, a property's).</summary>
    public object? Of(DesignerValue value, Type? wanted = null) => value.Kind switch
    {
        DesignerValueKinds.Literal => Fit(Literal(value), wanted),
        DesignerValueKinds.Name => Fit(Named(value.Name!), wanted),
        DesignerValueKinds.New => Made(value.Type is { } written ? Type(written) : wanted ?? throw Unread("new() of no type here"), value.Arguments ?? []),
        DesignerValueKinds.Call => Fit(Called(value.Name!, value.Arguments ?? []), wanted),
        DesignerValueKinds.Array => Array(value.Type is { } item ? Type(item) : wanted?.GetElementType() ?? throw Unread("an array of no type here"), value.Arguments ?? []),
        DesignerValueKinds.Cast => Fit(Of(value.Arguments![0]), Type(value.Type!)),
        DesignerValueKinds.Or => Ored(Of(value.Arguments![0], wanted), Of(value.Arguments![1], wanted)),
        DesignerValueKinds.Negate => Negated(Of(value.Arguments![0], wanted)),
        DesignerValueKinds.TypeOf => Type(value.Type!),
        _ => throw Unread(value.Text ?? value.Kind),
    };

    /// <summary>A type by its written name, or why the code names none.</summary>
    public Type Type(string written) => types.Find(written) ?? throw new DesignerException($"No type {written} is found (built the project yet?)");

    private static DesignerException Unread(string what) => new($"The designer does not read {what}");

    private static object? Literal(DesignerValue value) => value.Type switch
    {
        "null" => null,
        "string" => value.Text,
        "char" => value.Text![0],
        "bool" => value.Text == "true",
        "int" => int.Parse(value.Text!, CultureInfo.InvariantCulture),
        "uint" => uint.Parse(value.Text!, CultureInfo.InvariantCulture),
        "long" => long.Parse(value.Text!, CultureInfo.InvariantCulture),
        "ulong" => ulong.Parse(value.Text!, CultureInfo.InvariantCulture),
        "float" => float.Parse(value.Text!, CultureInfo.InvariantCulture),
        "double" => double.Parse(value.Text!, CultureInfo.InvariantCulture),
        "decimal" => decimal.Parse(value.Text!, CultureInfo.InvariantCulture),
        _ => throw Unread($"the literal {value.Text}"),
    };

    /// <summary>A name: the root (<c>this</c>) or a component of the class, else a static member (an enum's member among them)
    /// of the longest type its start names, read on through properties past it.</summary>
    private object? Named(string name)
    {
        string[] parts = name.Split('.');
        if (component(parts[0]) is { } own)
        {
            return Read(own, parts.AsSpan(1));
        }
        for (int split = parts.Length - 1; split >= 1; split--)
        {
            if (types.Find(string.Join('.', parts[..split])) is { } type)
            {
                return Read(Static(type, parts[split]), parts.AsSpan(split + 1));
            }
        }
        throw new DesignerException($"{name} names no component of the form, nor anything a type has");
    }

    /// <summary>A type's static member: an enum's member, a static field or property.</summary>
    private static object? Static(Type type, string member)
    {
        if (type.IsEnum)
        {
            return Enum.Parse(type, member);
        }
        const BindingFlags Statics = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return type.GetProperty(member, Statics) is { } property ? property.GetValue(null)
            : type.GetField(member, Statics) is { } field ? field.GetValue(null)
            : throw new DesignerException($"{type.Name} has no {member}");
    }

    /// <summary>An object's properties read in turn.</summary>
    private static object? Read(object? owner, ReadOnlySpan<string> path)
    {
        object? at = owner;
        foreach (string member in path)
        {
            at = at?.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance)?.GetValue(at)
                ?? throw new DesignerException($"{member} is no property of {at?.GetType().Name ?? "null"}");
        }
        return at;
    }

    private object Made(Type type, IReadOnlyList<DesignerValue> arguments)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors().Where(c => c.GetParameters().Length == arguments.Count))
        {
            if (Fitted(constructor.GetParameters(), arguments) is { } given)
            {
                return constructor.Invoke(given);
            }
        }
        throw new DesignerException($"{type.Name} has no constructor taking {arguments.Count} such argument(s)");
    }

    private object? Called(string name, IReadOnlyList<DesignerValue> arguments)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            throw new DesignerException($"{name}() calls a method of the class, which the designer does not run");
        }
        string on = name[..dot];
        string method = name[(dot + 1)..];
        if (on.Split('.')[0] == "resources")
        {
            throw new DesignerException("The form's resources (its .resx) are not read yet");
        }
        Type type = Type(on);
        foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == method && m.GetParameters().Length == arguments.Count))
        {
            if (Fitted(candidate.GetParameters(), arguments) is { } given)
            {
                return candidate.Invoke(null, given);
            }
        }
        throw new DesignerException($"{type.Name}.{method} takes no {arguments.Count} such argument(s)");
    }

    /// <summary>A method of an object (an interface's it implements too) called with arguments made for its parameters.</summary>
    public object? Call(object on, string method, IReadOnlyList<DesignerValue> arguments)
    {
        const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
        IEnumerable<MethodInfo> candidates = on.GetType().GetMethods(Instance).Concat(on.GetType().GetInterfaces().SelectMany(i => i.GetMethods()))
            .Where(m => m.Name == method && m.GetParameters().Length == arguments.Count);
        foreach (MethodInfo candidate in candidates)
        {
            if (Fitted(candidate.GetParameters(), arguments) is { } given)
            {
                return candidate.Invoke(on, given);
            }
        }
        throw new DesignerException($"{on.GetType().Name}.{method} takes no {arguments.Count} such argument(s)");
    }

    /// <summary>Arguments made for a method's parameters, each converted to its parameter's type; null where one does not fit.</summary>
    private object?[]? Fitted(ParameterInfo[] parameters, IReadOnlyList<DesignerValue> arguments)
    {
        object?[] given = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            object? made = Of(arguments[i], parameters[i].ParameterType);
            if (made is null ? parameters[i].ParameterType.IsValueType : !parameters[i].ParameterType.IsInstanceOfType(made))
            {
                return null;
            }
            given[i] = made;
        }
        return given;
    }

    private System.Array Array(Type item, IReadOnlyList<DesignerValue> items)
    {
        System.Array made = System.Array.CreateInstance(item, items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            made.SetValue(Of(items[i], item), i);
        }
        return made;
    }

    /// <summary>A value as the type it is given to takes it, as C# converts a constant: a number to another number's type or to
    /// an enum; any other value as it is.</summary>
    public static object? Fit(object? value, Type? wanted)
    {
        if (value is null || wanted is null || wanted.IsInstanceOfType(value))
        {
            return value;
        }
        Type target = Nullable.GetUnderlyingType(wanted) ?? wanted;
        if (target.IsEnum && value is IConvertible)
        {
            return Enum.ToObject(target, value);
        }
        if (value is IConvertible && (target.IsPrimitive || target == typeof(decimal)))
        {
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        return value;
    }

    private static object Ored(object? left, object? right)
    {
        if (left is Enum flags && right is not null)
        {
            return Enum.ToObject(flags.GetType(), Convert.ToInt64(left, CultureInfo.InvariantCulture) | Convert.ToInt64(right, CultureInfo.InvariantCulture));
        }
        return Convert.ToInt64(left, CultureInfo.InvariantCulture) | Convert.ToInt64(right, CultureInfo.InvariantCulture);
    }

    private static object Negated(object? value) => value switch
    {
        int number => -number,
        long number => -number,
        float number => -number,
        double number => -number,
        decimal number => -number,
        short number => -number,
        sbyte number => -number,
        _ => throw new DesignerException($"{value} is no number to negate"),
    };
}

/// <summary>What the designer could not make of a statement, in its own words.</summary>
internal sealed class DesignerException(string message) : Exception(message);
