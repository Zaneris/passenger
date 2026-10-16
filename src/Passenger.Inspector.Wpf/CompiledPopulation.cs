using System.Buffers.Binary;
using System.Reflection;
using System.Windows;

namespace Passenger.Inspector.Wpf;

/// <summary>Recognizes the actual post-build initializer and helper, including branch destinations. A marker left by a
/// later compiler target or IL weaver is insufficient: extra work, another loader or a moved callback requires restart.</summary>
internal static class CompiledPopulation
{
    public static string? Source(Type type)
    {
        FieldInfo? guard = type.GetField("_contentLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? initialize = type.GetMethod("InitializeComponent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, []);
        MethodInfo? populated = type.Assembly.GetType("Passenger.Generated.WpfPopulation")?.GetMethod("Loaded", BindingFlags.Public | BindingFlags.Static,
            [typeof(object), typeof(Uri)]);
        if (guard?.FieldType != typeof(bool) || initialize?.ReturnType != typeof(void) || populated?.ReturnType != typeof(void)
            || !Helper(populated) || Reader.Of(initialize) is not { } reader)
        {
            return null;
        }
        reader.Nops();
        if (!reader.Take(0x02) || !reader.Token(0x7b, guard)) // this._contentLoaded
        {
            return null;
        }
        // Debug compilers keep the test in a bool local; optimized forms can branch directly.
        if (reader.Store(out int condition) && !reader.Local(condition))
        {
            return null;
        }
        if (!reader.Branch(0x39, 0x2c, out int load)) // brfalse -> population
        {
            return null;
        }
        reader.Nops();
        int done = 0;
        bool earlyReturn = reader.Take(0x2a);
        if (!earlyReturn && !reader.Branch(0x38, 0x2b, out done))
        {
            return null;
        }
        int loadStart = reader.At;
        reader.Nops();
        if (load < loadStart || load > reader.At || !reader.Take(0x02) || !reader.Take(0x17) || !reader.Token(0x7d, guard)
            || !reader.String(out string? uri) || !reader.Take(0x18) // UriKind.Relative
            || !reader.Token(0x73, typeof(Uri).GetConstructor([typeof(string), typeof(UriKind)])!)
            || !reader.Store(out int source) || !reader.Take(0x02) || !reader.Local(source)
            || !reader.Token(0x28, typeof(Application).GetMethod(nameof(Application.LoadComponent), [typeof(object), typeof(Uri)])!))
        {
            return null;
        }
        reader.Nops();
        if (!reader.Take(0x02) || !reader.Local(source) || !reader.Token(0x28, populated))
        {
            return null;
        }
        int returnStart = reader.At;
        reader.Nops();
        int returns = reader.At;
        if (!reader.Take(0x2a))
        {
            return null;
        }
        reader.Nops();
        return reader.End && (earlyReturn || done >= returnStart && done <= returns) ? uri : null;
    }

    private static bool Helper(MethodInfo method)
    {
        if (Reader.Of(method) is not { } reader)
        {
            return false;
        }
        reader.Nops();
        if (!reader.String(out string? slot) || slot != "Passenger.Wpf.Populated.1"
            || !reader.Token(0x28, typeof(AppContext).GetMethod(nameof(AppContext.GetData), [typeof(string)])!)
            || !reader.Token(0x75, typeof(Action<object, Uri>)))
        {
            return false;
        }
        // Optimized null-conditional invocation keeps the callback on the stack; Debug stores it in a local.
        bool stacked = reader.Take(0x25); // dup
        int callback = -1;
        int done = -1;
        int invoke;
        if (stacked)
        {
            if (!reader.Branch(0x3a, 0x2d, out invoke) || !reader.Take(0x26) || !reader.Take(0x2a)) // null -> pop; ret
            {
                return false;
            }
        }
        else
        {
            if (!reader.Store(out callback) || !reader.Local(callback))
            {
                return false;
            }
            // Another optimized compiler keeps the local but jumps directly to the final return when null.
            if (reader.Branch(0x39, 0x2c, out done))
            {
                invoke = reader.At;
            }
            else if (!reader.Branch(0x3a, 0x2d, out invoke) || !reader.Branch(0x38, 0x2b, out done))
            {
                return false;
            }
        }
        int invokeStart = reader.At;
        reader.Nops();
        if (invoke < invokeStart || invoke > reader.At || (!stacked && !reader.Local(callback)) || !reader.Take(0x02) || !reader.Take(0x03)
            || !reader.Token(0x6f, typeof(Action<object, Uri>).GetMethod(nameof(Action<object, Uri>.Invoke))!))
        {
            return false;
        }
        int returnStart = reader.At;
        reader.Nops();
        int returns = reader.At;
        if (!reader.Take(0x2a))
        {
            return false;
        }
        reader.Nops();
        return reader.End && (stacked || done >= returnStart && done <= returns);
    }

    private sealed class Reader(byte[] il, Module module)
    {
        public int At { get; private set; }
        public bool End => At == il.Length;
        public static Reader? Of(MethodInfo method) => method.GetMethodBody()?.GetILAsByteArray() is { } il ? new Reader(il, method.Module) : null;

        public bool Take(byte opcode)
        {
            if (At >= il.Length || il[At] != opcode)
            {
                return false;
            }
            At++;
            return true;
        }

        public void Nops()
        {
            while (Take(0x00))
            {
            }
        }

        public bool Store(out int local)
        {
            local = At < il.Length ? il[At] - 0x0a : -1;
            if (local is >= 0 and <= 3)
            {
                At++;
                return true;
            }
            if (Take(0x13) && At < il.Length)
            {
                local = il[At++];
                return true;
            }
            return false;
        }

        public bool Local(int local) => local < 4 ? Take((byte)(0x06 + local)) : Take(0x11) && At < il.Length && il[At++] == local;

        public bool Branch(byte wide, byte small, out int target)
        {
            target = -1;
            if (Take(wide) && At + 4 <= il.Length)
            {
                int offset = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(At));
                At += 4;
                target = At + offset;
                return true;
            }
            if (Take(small) && At < il.Length)
            {
                target = At + 1 + unchecked((sbyte)il[At]);
                At++;
                return true;
            }
            return false;
        }

        public bool String(out string? value)
        {
            value = null;
            if (!Take(0x72) || At + 4 > il.Length)
            {
                return false;
            }
            value = module.ResolveString(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(At)));
            At += 4;
            return true;
        }

        public bool Token(byte opcode, MemberInfo expected)
        {
            if (!Take(opcode) || At + 4 > il.Length)
            {
                return false;
            }
            int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(At));
            At += 4;
            MemberInfo? actual = opcode is 0x7b or 0x7d ? module.ResolveField(token) : opcode == 0x75 ? module.ResolveType(token) : module.ResolveMethod(token);
            return expected.Equals(actual);
        }
    }
}
