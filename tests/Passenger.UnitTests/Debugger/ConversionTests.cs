using System.Reflection.Metadata;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.UnitTests.Debugger;

/// <summary>The evaluator's conv instructions on a floating-point value, held to what the program's runtime computes: each
/// case's answer is what .NET 10 or .NET 11 gave running the same cast, and on the runtime the tests run on every
/// unchecked conversion answers what its own cast does.</summary>
public class ConversionTests
{
    /// <summary>Each row's answers are what .NET 10 and .NET 11 gave running the same cast, so a conversion for a program on
    /// another runtime than the tests' is still held.</summary>
    [Fact]
    public void A_float_out_of_range_converts_as_its_programs_runtime_and_pointer_size_do_and_a_checked_one_throws() => Cases.All(
        ("a float out of an int's range or wider converts to the nearest value its target holds on every runtime", () => Cases.Each(
        [
            (ILOpCode.Conv_i4, 1e19, (object)int.MaxValue),
            (ILOpCode.Conv_i4, -2147483649.0, int.MinValue),
            (ILOpCode.Conv_u4, -1.0, 0u),
            (ILOpCode.Conv_u8, double.PositiveInfinity, ulong.MaxValue),
            (ILOpCode.Conv_u8, double.NaN, 0UL),
        ], row =>
        {
            foreach (int runtime in (int[])[9, 10, 11])
            {
                Assert.Equal(row.Item3, Converted(row.Item1, row.Item2, 8, runtime));
            }
        })),
        ("a float out of a narrower type's range converts as its runtime's version does", () => Cases.Each(
        [
            (ILOpCode.Conv_i1, 1e19, -1, 127),
            (ILOpCode.Conv_u1, -300.7, 212, 0),
            (ILOpCode.Conv_i2, 32768.0, -32768, 32767),
            (ILOpCode.Conv_u2, double.NaN, 0, 0),
        ], row =>
        {
            (ILOpCode op, double value, int net10, int net11) = row;
            Assert.Equal(net10, Converted(op, value, 8, 10));
            Assert.Equal(net10, Converted(op, value, 8, 9));
            Assert.Equal(net11, Converted(op, value, 8, 11));
        })),
        ("a native integer takes the range of the program's pointer size", () =>
        {
            Assert.Equal(long.MaxValue, Converted(ILOpCode.Conv_i, 1e19, 8, 10));
            Assert.Equal(int.MaxValue, Converted(ILOpCode.Conv_i, 1e19, 4, 10));
            Assert.Equal(uint.MaxValue, Converted(ILOpCode.Conv_u, 1e10, 4, 10));
        }),
        ("a checked conversion out of range throws an overflow and one whose whole part fits converts", () =>
        {
            Cases.Each([(ILOpCode.Conv_ovf_i4, 1e10), (ILOpCode.Conv_ovf_u4, -1.0), (ILOpCode.Conv_ovf_i1, double.NaN)], row =>
            {
                foreach (int runtime in (int[])[10, 11])
                {
                    Assert.Throws<OverflowException>(() => Converted(row.Item1, row.Item2, 8, runtime));
                }
            });
            Assert.Equal(0u, Converted(ILOpCode.Conv_ovf_u4, -0.9, 8, 11));
        }));

    [Fact]
    public void Every_unchecked_conversion_answers_what_the_running_runtimes_own_cast_does()
    {
        Random random = new(633);
        double[] edges = [0.0, -0.0, 0.5, -0.5, 127.5, 128.0, -129.0, 255.9, 256.0, 32767.5, 32768.0, -32769.0, 65535.9, 65536.0,
            2147483647.5, 2147483648.0, -2147483649.0, 4294967295.5, 4294967296.0, 9223372036854775807.0, -9223372036854775808.0,
            18446744073709551615.0, 1e30, -1e30, double.MaxValue, double.MinValue, double.Epsilon, double.PositiveInfinity,
            double.NegativeInfinity, double.NaN];
        IEnumerable<double> spread = Enumerable.Range(0, 4000).Select(_ => Math.ScaleB(random.NextDouble() * 2 - 1, random.Next(0, 72)));
        int runtime = Environment.Version.Major;
        foreach (double value in edges.Concat(spread))
        {
            Check(ILOpCode.Conv_i1, value, (int)unchecked((sbyte)value));
            Check(ILOpCode.Conv_u1, value, (int)unchecked((byte)value));
            Check(ILOpCode.Conv_i2, value, (int)unchecked((short)value));
            Check(ILOpCode.Conv_u2, value, (int)unchecked((ushort)value));
            Check(ILOpCode.Conv_u2, value, (int)unchecked((char)value));
            Check(ILOpCode.Conv_i4, value, unchecked((int)value));
            Check(ILOpCode.Conv_u4, value, unchecked((uint)value));
            Check(ILOpCode.Conv_i8, value, unchecked((long)value));
            Check(ILOpCode.Conv_u8, value, unchecked((ulong)value));
            Check(ILOpCode.Conv_i, value, (long)unchecked((nint)value));
            Check(ILOpCode.Conv_u, value, (ulong)unchecked((nuint)value));
        }

        void Check(ILOpCode op, double value, object cast)
        {
            object? converted = Converted(op, value, IntPtr.Size, runtime);
            Assert.True(Equals(converted, cast), $"{op} of {value:R} gave {converted}, where .NET {runtime}'s own cast gives {cast}");
        }
    }

    private static object? Converted(ILOpCode op, double value, int pointerSize, int runtime) =>
        CilNumerics.Convert(op, CilValue.FromPrimitive(value), pointerSize, runtime).Value;
}
