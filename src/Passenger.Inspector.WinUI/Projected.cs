using System.Reflection;
using System.Runtime.InteropServices;

namespace Passenger.Inspector.WinUI;

/// <summary>The app's own WinUI reached by reflection, as the inspector references none of the Windows App SDK: its types by
/// name among the assemblies the app loaded (its projection, Microsoft.WinUI, and C#/WinRT's runtime), an object WinUI hands
/// over as an IInspectable made the projected object the app's own code sees, and members read, set and called by name.</summary>
internal static class Projected
{
    /// <summary>A type of the app's by its full name, among the assemblies it loaded; null for one not loaded.</summary>
    public static Type? Type(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(fullName, throwOnError: false) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The projected object an IInspectable is, through C#/WinRT's own marshalling in the app; null for none.</summary>
    public static object? Of(nint inspectable)
    {
        if (inspectable == 0 || Type("WinRT.MarshalInspectable`1") is not { } marshal)
        {
            return null;
        }
        return marshal.MakeGenericType(typeof(object)).GetMethod("FromAbi", [typeof(nint)])?.Invoke(null, [inspectable]);
    }

    /// <summary>A projected object's IInspectable, one reference added, through C#/WinRT; zero for none.</summary>
    public static nint InspectableOf(object projected)
    {
        if (Type("WinRT.MarshalInspectable`1") is not { } marshal)
        {
            return 0;
        }
        return marshal.MakeGenericType(typeof(object)).GetMethod("FromManaged", [typeof(object)])?.Invoke(null, [projected]) is nint made ? made : 0;
    }

    /// <summary>A property's value by name, its type's or an interface's; null where it has none.</summary>
    public static object? Get(object owner, string name) => Property(owner.GetType(), name)?.GetValue(owner);

    /// <summary>A number a struct holds (a Rect's, a Thickness's, a Size's), by its field's or property's name; zero where it
    /// holds none.</summary>
    public static double Number(object? value, string name)
    {
        object? read = value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)
            ?? value?.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
        return read switch
        {
            double number => number,
            float single => single,
            _ => 0,
        };
    }

    /// <summary>A static property's value of a type named by its full name; null where it has none.</summary>
    public static object? Static(string type, string name) => Type(type)?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    /// <summary>Sets a property by name; false where it has none to set.</summary>
    public static bool Set(object owner, string name, object? value)
    {
        if (Property(owner.GetType(), name) is not { CanWrite: true } property)
        {
            return false;
        }
        property.SetValue(owner, value);
        return true;
    }

    /// <summary>Calls a method by name with the arguments given, the first of that name and count; throws where it has none.</summary>
    public static object? Call(object owner, string name, params object?[] arguments)
    {
        MethodInfo method = owner.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(owner.GetType().FullName, name);
        return method.Invoke(owner, arguments);
    }

    /// <summary>Calls a static method of a type named by its full name; throws where it has none.</summary>
    public static object? CallStatic(string type, string name, params object?[] arguments)
    {
        MethodInfo method = Type(type)?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(type, name);
        return method.Invoke(null, arguments);
    }

    /// <summary>Whether an object is of a type named by its full name, or derives from it.</summary>
    public static bool Is(object? value, string type) => value is not null && Type(type) is { } wanted && wanted.IsInstanceOfType(value);

    /// <summary>A value of an enum of the app's by its member's name.</summary>
    public static object Enum(string type, string member) => System.Enum.Parse(Type(type) ?? throw new TypeLoadException(type), member);

    /// <summary>A delegate of the app's type (a handler WinUI calls) made of a method of the inspector's whose parameters it
    /// can take.</summary>
    public static Delegate Handler(Type type, Delegate method) => Delegate.CreateDelegate(type, method.Target, method.Method);

    /// <summary>A struct of the Windows SDK's projection (Windows.Foundation.Rect, Point) made from its fields' values.</summary>
    public static object Make(string type, params object[] values) => Activator.CreateInstance(Type(type) ?? throw new TypeLoadException(type), values)!;

    /// <summary>A property of a type or the interfaces it has, by name.</summary>
    private static PropertyInfo? Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
        ?? type.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p is not null);

    /// <summary>An IInspectable's reference let go.</summary>
    public static void Release(nint inspectable)
    {
        if (inspectable != 0)
        {
            Marshal.Release(inspectable);
        }
    }
}
