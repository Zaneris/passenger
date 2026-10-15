using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Markup.Xaml;

namespace Passenger.Inspector;

/// <summary>The compiled Avalonia loader's override, retaining its original population before the app's constructor
/// continues. A caller-supplied service provider cannot cross Action&lt;object&gt;, so that trampoline is refused.</summary>
internal sealed class XamlPopulation
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private readonly FieldInfo field;
    private readonly Action<object> callback;

    private XamlPopulation(Type type, string file, FieldInfo field, MethodInfo populate, MethodInfo services, Action<AvaloniaObject> updated)
    {
        Type = type;
        File = file;
        this.field = field;
        callback = instance =>
        {
            // These are the same two calls the compiler's one-argument trampoline makes. Loading loose XAML here would
            // replace compiled bindings, namescope connections and event handlers with a different construction path.
            try
            {
                object? provider = services.Invoke(null, [null]);
                populate.Invoke(null, [provider, instance]);
            }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
            }
            updated((AvaloniaObject)instance);
        };
    }

    public Type Type { get; }

    public string File { get; }

    public bool IsInstalled => ReferenceEquals(field.GetValue(null), callback);

    public void Install() => field.SetValue(null, callback);

    public void Restore()
    {
        if (IsInstalled)
        {
            field.SetValue(null, null);
        }
    }

    public static (XamlPopulation? Population, string? Problem) Prepare(Type type, string file, Action<AvaloniaObject> updated)
    {
        FieldInfo? field = type.GetField("!XamlIlPopulateOverride", Static);
        MethodInfo[] trampolines = [.. type.GetMethods(Static).Where(m => m.Name == "!XamlIlPopulateTrampoline")];
        MethodInfo? populate = type.GetMethods(Static).FirstOrDefault(m => m.Name == "!XamlIlPopulate" && m.ReturnType == typeof(void)
            && m.GetParameters() is { Length: 2 } parameters && parameters[0].ParameterType == typeof(IServiceProvider) && parameters[1].ParameterType == type);
        MethodInfo? services = typeof(AvaloniaXamlLoader).Assembly.GetType("Avalonia.Markup.Xaml.XamlIl.Runtime.XamlIlRuntimeHelpers")?
            .GetMethod("CreateRootServiceProviderV3", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(IServiceProvider)]);
        if (field is null || field.FieldType != typeof(Action<object>) || field.IsInitOnly || populate is null || services is null || trampolines.Length == 0)
        {
            return (null, $"{type.Name}'s compiled XAML loader cannot preserve population of newly created views; restart to load the edit");
        }
        if (trampolines.Any(m => m.GetParameters().Length != 1))
        {
            return (null, $"{type.Name}'s XAML loader takes a custom service provider, which cannot be preserved by its population override; restart to load the edit");
        }
        if (trampolines.Any(m => !Matches(m, field, populate, services)))
        {
            return (null, $"{type.Name}'s compiled XAML trampoline is not a recognized population sequence; restart to load the edit");
        }
        if (field.GetValue(null) is not null)
        {
            return (null, $"{type.Name}'s XAML population is already overridden by the app or another tool; restart to load the edit");
        }
        return (new XamlPopulation(type, file, field, populate, services, updated), null);
    }

    /// <summary>Recognizes the actual loader emitted by the target's compiler, not its name or the Avalonia runtime's
    /// version. Unknown compiler/instrumentation variants must not lose a caller's services or initialization work.</summary>
    private static bool Matches(MethodInfo trampoline, FieldInfo field, MethodInfo populate, MethodInfo services)
    {
        if (trampoline.ReturnType != typeof(void) || trampoline.GetParameters()[0].ParameterType != populate.DeclaringType
            || trampoline.GetMethodBody()?.GetILAsByteArray() is not { } il)
        {
            return false;
        }
        int at = 0;
        Nops();
        if (!Token(0x7e, field)) // ldsfld override
        {
            return false;
        }
        int target;
        if (Take(0x39) && at + 4 <= il.Length) // brfalse
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at));
            at += 4;
            target = at + offset;
        }
        else if (Take(0x2c) && at < il.Length) // brfalse.s
        {
            target = at + 1 + unchecked((sbyte)il[at]);
            at++;
        }
        else
        {
            return false;
        }
        if (!Token(0x7e, field) || !Take(0x02) || !Token(0x28, typeof(Action<object>).GetMethod(nameof(Action<object>.Invoke))!) || !Take(0x2a))
        {
            return false;
        }
        int fallback = at;
        Nops();
        if (target < fallback || target > at || !Take(0x14) || !Token(0x28, services) || !Take(0x02) || !Token(0x28, populate) || !Take(0x2a))
        {
            return false;
        }
        Nops();
        return at == il.Length;

        bool Take(byte opcode)
        {
            if (at >= il.Length || il[at] != opcode)
            {
                return false;
            }
            at++;
            return true;
        }

        void Nops()
        {
            while (Take(0x00))
            {
            }
        }

        bool Token(byte opcode, MemberInfo expected)
        {
            if (!Take(opcode) || at + 4 > il.Length)
            {
                return false;
            }
            int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at));
            at += 4;
            MemberInfo? actual = opcode == 0x7e ? trampoline.Module.ResolveField(token) : trampoline.Module.ResolveMethod(token);
            return expected.Equals(actual);
        }
    }
}
