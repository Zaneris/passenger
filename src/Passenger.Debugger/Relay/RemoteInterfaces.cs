using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.Relay;

/// <summary>How one parameter of an interop method crosses to the app: read from its declaration, as the COM source
/// generator reads it to write the native call.</summary>
internal enum ParamKind
{
    /// <summary>A number, enum, bool or small struct, by value.</summary>
    Value,
    /// <summary>An interface pointer the callee reads.</summary>
    Interface,
    /// <summary>A UTF-16 string the callee reads.</summary>
    String,
    /// <summary>An interface pointer the callee writes (out T, or out object named by an IID).</summary>
    InterfaceOut,
    /// <summary>A value the callee writes.</summary>
    ValueOut,
    /// <summary>A value the callee reads and writes (ref T).</summary>
    ValueInOut,
    /// <summary>An array the callee reads, its length another parameter.</summary>
    ArrayIn,
    /// <summary>An array the callee writes, its length another parameter.</summary>
    ArrayOut,
    /// <summary>A raw pointer whose extent only the method says.</summary>
    Pointer,
}

internal sealed record ParamPlan(string Name, ParamKind Kind, Type ElementType, int ElementSize, int CountIndex);

/// <summary>A method at its slot, with the interface that declares it (a base's, for a derived interface's first slots).</summary>
internal sealed record MethodPlan(string Name, ParamPlan[] Params, Type Declaring);

/// <summary>An interop interface as the relay carries it: its IID and its methods by vtable slot.</summary>
internal sealed class InterfacePlan
{
    public required Type Type { get; init; }
    public required Guid Iid { get; init; }
    public required MethodPlan[] Methods { get; init; }

    public MethodPlan? At(int slot) => slot >= 3 && slot - 3 < Methods.Length ? Methods[slot - 3] : null;
}

/// <summary>Every interface of Passenger.Debugger.Interop, planned once.</summary>
internal static class RemoteInterfaces
{
    private static readonly Dictionary<Type, InterfacePlan> s_byType = [];
    private static readonly Dictionary<Guid, InterfacePlan> s_byIid = [];

    public static InterfacePlan IUnknown { get; } = new() { Type = typeof(object), Iid = new Guid("00000000-0000-0000-C000-000000000046"), Methods = [] };

    static RemoteInterfaces()
    {
        foreach (Type type in typeof(ICorDebug).Assembly.GetTypes())
        {
            if (type.IsInterface && type.GetCustomAttribute<GeneratedComInterfaceAttribute>() is not null)
            {
                InterfacePlan plan = Plan(type);
                s_byType[type] = plan;
                s_byIid[plan.Iid] = plan;
            }
        }
    }

    public static InterfacePlan Of(Type type) => s_byType[type];

    public static InterfacePlan? OfIid(Guid iid) => s_byIid.GetValueOrDefault(iid);

    public static int MostSlots => s_byType.Values.Max(each => each.Methods.Length) + 3;

    private static InterfacePlan Plan(Type type)
    {
        List<MethodPlan> methods = [];
        foreach (Type each in Chain(type))
        {
            // The generator restates a base's methods in a derived interface, not abstract: only the abstract ones are its own.
            foreach (MethodInfo method in each.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.IsAbstract).OrderBy(m => m.MetadataToken))
            {
                ParameterInfo[] parameters = method.GetParameters();
                methods.Add(new MethodPlan(method.Name, [.. parameters.Select(p => PlanOf(p, parameters))], each));
            }
        }
        return new InterfacePlan { Type = type, Iid = type.GUID, Methods = [.. methods] };
    }

    /// <summary>The interface and the ones it derives from, the base first, as their methods fill the vtable.</summary>
    private static IEnumerable<Type> Chain(Type type)
    {
        List<Type> chain = [];
        for (Type? at = type; at is not null; at = Direct(at))
        {
            chain.Insert(0, at);
        }
        return chain;

        static Type? Direct(Type at)
        {
            Type[] all = at.GetInterfaces().Where(b => b.GetCustomAttribute<GeneratedComInterfaceAttribute>() is not null).ToArray();
            // The direct base is the one every other base is a base of.
            return all.FirstOrDefault(b => all.All(other => other == b || b.GetInterfaces().Contains(other)));
        }
    }

    private static ParamPlan PlanOf(ParameterInfo parameter, ParameterInfo[] all)
    {
        Type type = parameter.ParameterType;
        string name = parameter.Name ?? "";
        if (type.IsByRef)
        {
            Type element = type.GetElementType()!;
            if (element.IsInterface || element == typeof(object))
            {
                return new ParamPlan(name, ParamKind.InterfaceOut, element, IntPtr.Size, -1);
            }
            return new ParamPlan(name, parameter.IsOut ? ParamKind.ValueOut : ParamKind.ValueInOut, element, SizeOf(element, parameter), -1);
        }
        if (type.IsArray)
        {
            Type element = type.GetElementType()!;
            int count = CountIndex(parameter, all);
            bool written = parameter.GetCustomAttribute<OutAttribute>() is not null;
            return new ParamPlan(name, written ? ParamKind.ArrayOut : ParamKind.ArrayIn, element, element.IsInterface ? IntPtr.Size : SizeOf(element, null), count);
        }
        if (type.IsInterface)
        {
            return new ParamPlan(name, ParamKind.Interface, type, IntPtr.Size, -1);
        }
        if (type == typeof(string))
        {
            return new ParamPlan(name, ParamKind.String, type, 2, -1);
        }
        if (type == typeof(nint) || type == typeof(nuint))
        {
            // A blob the callee reads, its byte count the parameter before it (cbSigBlob, pvSigBlob).
            if (parameter.Position > 0 && all[parameter.Position - 1] is { Name: { } count } && count.StartsWith("cb", StringComparison.Ordinal)
                && all[parameter.Position - 1].ParameterType == typeof(uint) && (name.StartsWith("pv", StringComparison.Ordinal) || name.StartsWith("pb", StringComparison.Ordinal)))
            {
                return new ParamPlan(name, ParamKind.ArrayIn, typeof(byte), 1, parameter.Position - 1);
            }
            return new ParamPlan(name, ParamKind.Pointer, type, IntPtr.Size, -1);
        }
        return new ParamPlan(name, ParamKind.Value, type, SizeOf(type, parameter), -1);
    }

    private static int CountIndex(ParameterInfo parameter, ParameterInfo[] all)
    {
        if (parameter.GetCustomAttribute<MarshalUsingAttribute>() is { CountElementName: { Length: > 0 } countName })
        {
            return Array.FindIndex(all, p => p.Name == countName);
        }
        if (parameter.GetCustomAttribute<MarshalAsAttribute>() is { } marshalAs)
        {
            return marshalAs.SizeParamIndex;
        }
        return -1;
    }

    private static int SizeOf(Type type, ParameterInfo? parameter)
    {
        if (type == typeof(bool))
        {
            return 4;
        }
        if (type == typeof(char))
        {
            return 2;
        }
        if (type.IsEnum)
        {
            return Marshal.SizeOf(Enum.GetUnderlyingType(type));
        }
        if (type == typeof(nint) || type == typeof(nuint))
        {
            return IntPtr.Size;
        }
        return Marshal.SizeOf(type);
    }
}
