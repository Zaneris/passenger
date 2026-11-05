using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Passenger.Debugger;
using Passenger.Debugger.Interop;

namespace Passenger.UnitTests.Debugger;

/// <summary>An exception's stack trace written from metadata as its StackTrace property writes it, from the frames this
/// process's own exceptions store (each method's token and its file and line, as the runtime read them): generic methods
/// and types, nested types, lambdas, local functions, iterators and state machines named as their methods, parameters by
/// reference and arrays, and hidden and inlined frames left out. And the IL offset compiled code is at, as the
/// runtime's traces read it from the code's map.</summary>
public class StackTraceTextTests
{
    public static TheoryData<string> Shapes => [.. Thrown.Keys];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_stack_trace_written_from_metadata_is_what_StackTrace_writes(string shape)
    {
        Exception thrown = await CatchAsync(Thrown[shape]);
        Dictionary<Module, ModuleMetadataReader> readers = [];
        try
        {
            List<TracedFrame> frames = [];
            foreach (StackFrame frame in new StackTrace(thrown, fNeedFileInfo: true).GetFrames())
            {
                MethodBase method = frame.GetMethod() ?? throw new InvalidOperationException($"A frame of the {shape} trace has no method");
                if (!readers.TryGetValue(method.Module, out ModuleMetadataReader? reader))
                {
                    readers[method.Module] = reader = ModuleMetadataReader.TryLoad(method.Module.Assembly.Location)
                        ?? throw new InvalidOperationException($"{method.Module.Name} could not be read");
                }
                frames.Add(new TracedFrame(reader.PeMetadataReader, method.MetadataToken,
                    frame.GetFileName() is { } file ? (file, frame.GetFileLineNumber()) : null, LastOfEarlierThrow: false));
            }
            string written = StackTraceText.Of(frames);
            Assert.True(written == thrown.StackTrace, $"The {shape} trace is written\n{written}\nwhere StackTrace writes\n{thrown.StackTrace}");
        }
        finally
        {
            foreach (ModuleMetadataReader reader in readers.Values)
            {
                reader.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(0x00u, 0)]
    [InlineData(0x10u, 0)]
    [InlineData(0x14u, 3)]
    [InlineData(0x1Fu, 3)]
    [InlineData(0x20u, 7)]
    [InlineData(0x30u, 0)]
    [InlineData(0x40u, 12)]
    [InlineData(0x60u, 12)]
    public void Compiled_code_is_at_the_lowest_IL_offset_of_its_run_none_in_the_prolog_and_the_last_in_the_epilog(uint native, int il)
    {
        COR_DEBUG_IL_TO_NATIVE_MAP[] map =
        [
            new() { ilOffset = unchecked((uint)-2), nativeStartOffset = 0x00, nativeEndOffset = 0x10 },
            new() { ilOffset = 0, nativeStartOffset = 0x10, nativeEndOffset = 0x14 },
            new() { ilOffset = 7, nativeStartOffset = 0x14, nativeEndOffset = 0x14 },
            new() { ilOffset = 3, nativeStartOffset = 0x14, nativeEndOffset = 0x20 },
            new() { ilOffset = 7, nativeStartOffset = 0x20, nativeEndOffset = 0x30 },
            new() { ilOffset = unchecked((uint)-1), nativeStartOffset = 0x30, nativeEndOffset = 0x38 },
            new() { ilOffset = 12, nativeStartOffset = 0x38, nativeEndOffset = 0x48 },
            new() { ilOffset = unchecked((uint)-3), nativeStartOffset = 0x48, nativeEndOffset = 0x60 },
        ];
        int at = CompiledOffsets.ILAt(map, native);
        Assert.True(at == il, $"Native offset 0x{native:x} was read at IL 0x{at:x}, where the runtime's trace reads 0x{il:x}: its line would be another statement's");
    }

    private static readonly Dictionary<string, Func<Task>> Thrown = new(StringComparer.Ordinal)
    {
        ["generic method"] = () => Ran(() => Throws(4, "four")),
        ["generic type"] = () => Ran(() => Holder<int>.Fail([1, 2])),
        ["nested type"] = () => Ran(Outer.Inner.Fail),
        ["lambda"] = () => Ran(() => throw new InvalidOperationException("from a lambda")),
        ["local function"] = () => Ran(Locally),
        ["iterator"] = () => Ran(() =>
        {
            foreach (int number in Numbers())
            {
                GC.KeepAlive(number);
            }
        }),
        ["await"] = AwaitsAsync,
        ["hidden and inlined"] = () => Ran(Hides),
        ["by reference"] = () => Ran(Referenced),
    };

    private static Task Ran(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static async Task<Exception> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            return e;
        }
        throw new InvalidOperationException("Nothing was thrown");
    }

    private static T Throws<T>(T value, string name) => throw new ArgumentException($"{value} named {name}", nameof(value));

    private static void Locally()
    {
        Local(2);

        static void Local(int depth) => throw new NotSupportedException($"from a local function at {depth}");
    }

    private static IEnumerable<int> Numbers()
    {
        yield return 1;
        throw new InvalidOperationException("from an iterator");
    }

    private static async Task AwaitsAsync()
    {
        await Task.Yield();
        throw new TimeoutException("from an await");
    }

    private static void Hides() => Inlined();

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.NoInlining)]
    private static void Inlined() => Hidden();

    [StackTraceHidden]
    private static void Hidden() => throw new InvalidOperationException("from a hidden frame");

    private static void Referenced()
    {
        int number = 3;
        ByReference(ref number, out _, [[1]], new int[1, 1]);
    }

    private static void ByReference(ref int number, out string text, int[][] jagged, int[,] grid) =>
        throw new FormatException($"{number} {jagged.Length} {grid.Length}");

    private static class Outer
    {
        public static class Inner
        {
            public static void Fail() => throw new KeyNotFoundException("from a nested type");
        }
    }

    private static class Holder<T>
    {
        public static void Fail(T[] items) => throw new IndexOutOfRangeException($"{items.Length} {typeof(T).Name}");
    }
}
