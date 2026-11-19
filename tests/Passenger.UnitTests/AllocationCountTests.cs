using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Runtime.Loader;

namespace Passenger.UnitTests;

/// <summary>What the tests holding code to what it allocates read: a thread's own count of the bytes it allocated, which a
/// collection must leave as it was for a thread that allocated nothing meanwhile, and which no class beside them may add to
/// by unloading a load context.</summary>
public class AllocationCountTests
{
    private static byte[]? taken;

    /// <summary>Each instruction by its first byte, and those beginning 0xFE by their second.</summary>
    private static readonly (OpCode[] OneByte, OpCode[] TwoByte) Instructions = ReadInstructions();

    /// <summary>A background collection's mark would end while this thread holds the fresh allocation context it took as the
    /// collection began, and count the context's unused rest against it.</summary>
    [Fact]
    public void A_collection_counts_nothing_against_a_thread_that_allocated_nothing()
    {
        Assert.False(GCSettings.LatencyMode == GCLatencyMode.Interactive, "Collections here run in the background: a test's count of what it allocated may take in up to 8 KB it never did");
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
        taken = new byte[64];
        long before = GC.GetAllocatedBytesForCurrentThread();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long counted = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(counted == 0, $"A collection counted {counted:N0} bytes against a thread that allocated nothing: a test holding code to allocating nothing fails on bytes it never made");
        Assert.NotNull(taken);
    }

    /// <summary>A class that unloads a load context runs in the Alone collection, after the rest: as each context's loader
    /// allocator goes, the runtime's cast cache is emptied, and a thread beside it whose cast misses builds it again as its
    /// own allocation. Read from the built tests' code, a lambda's or a state machine's with the class it is written in.</summary>
    [Fact]
    public void Every_class_unloading_a_load_context_runs_alone()
    {
        MethodInfo unload = typeof(AssemblyLoadContext).GetMethod(nameof(AssemblyLoadContext.Unload))!;
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        SortedSet<string> alone = new(StringComparer.Ordinal);
        SortedSet<string> beside = new(StringComparer.Ordinal);
        foreach (Type type in typeof(AllocationCountTests).Assembly.GetTypes())
        {
            Type outermost = type;
            while (outermost.DeclaringType is { } declaring)
            {
                outermost = declaring;
            }
            bool runsAlone = outermost.GetCustomAttributes(inherit: true).OfType<Xunit.v3.ICollectionAttribute>().Any(collection => collection.Name == Alone.Name);
            foreach (MethodBase method in type.GetMethods(Declared).Concat<MethodBase>(type.GetConstructors(Declared)).Where(method => Calls(method, unload)))
            {
                (runsAlone ? alone : beside).Add($"{outermost.Name}, in {type.Name}.{method.Name}");
            }
        }
        Assert.True(alone.Count + beside.Count > 0, "No class was found unloading a load context, so the built tests' calls were not read");
        Assert.True(beside.Count == 0, "A class unloading a load context runs beside the tests holding code to what their thread allocates: once collected, "
            + "each context empties the runtime's cast cache, and a test's thread casting next builds it again, up to 98 KB counted as the test's own. "
            + "Mark the class [Collection(Alone.Name)]:\n" + string.Join('\n', beside));
    }

    /// <summary>Whether a method's code calls <paramref name="target"/>.</summary>
    private static bool Calls(MethodBase method, MethodBase target)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } code)
        {
            return false;
        }
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (int at = 0; at < code.Length;)
        {
            OpCode instruction = code[at] == 0xFE ? Instructions.TwoByte[code[at + 1]] : Instructions.OneByte[code[at]];
            at += instruction.Size;
            if (instruction.OperandType == OperandType.InlineMethod
                && target.Equals(method.Module.ResolveMethod(BitConverter.ToInt32(code, at), typeArguments, methodArguments)))
            {
                return true;
            }
            at += instruction.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(code, at)),
                _ => 4,
            };
        }
        return false;
    }

    private static (OpCode[] OneByte, OpCode[] TwoByte) ReadInstructions()
    {
        (OpCode[] OneByte, OpCode[] TwoByte) read = (new OpCode[0x100], new OpCode[0x100]);
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode instruction = (OpCode)field.GetValue(null)!;
            (instruction.Size == 1 ? read.OneByte : read.TwoByte)[(ushort)instruction.Value & 0xFF] = instruction;
        }
        return read;
    }
}
