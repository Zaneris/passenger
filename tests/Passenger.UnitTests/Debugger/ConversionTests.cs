using System.Reflection.Metadata;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.UnitTests.Debugger;

/// <summary>The evaluator's conv instructions on a floating-point value, held to what the program's runtime computes: each
/// case's answer is what .NET 10 or .NET 11 gave running the same cast, and on the runtime the tests run on every
/// unchecked conversion answers what its own cast does.</summary>
public class ConversionTests
{
    [Theory]
    [InlineData(ILOpCode.Conv_i4, 1e19, int.MaxValue)]
    [InlineData(ILOpCode.Conv_i4, -1e19, int.MinValue)]
    [InlineData(ILOpCode.Conv_u4, -1.0, 0u)]
    [InlineData(ILOpCode.Conv_u4, 300.7 * 1e8, uint.MaxValue)]
    [InlineData(ILOpCode.Conv_u4, 2147483648.0, 2147483648u)]
    [InlineData(ILOpCode.Conv_i4, 2147483648.0, int.MaxValue)]
    [InlineData(ILOpCode.Conv_i4, -2147483649.0, int.MinValue)]
    [InlineData(ILOpCode.Conv_i4, -0.9, 0)]
    [InlineData(ILOpCode.Conv_i8, 1e19, long.MaxValue)]
    [InlineData(ILOpCode.Conv_i8, -1e19, long.MinValue)]
    [InlineData(ILOpCode.Conv_i8, 9223372036854775808.0, long.MaxValue)]
    [InlineData(ILOpCode.Conv_u8, -1.0, 0UL)]
    [InlineData(ILOpCode.Conv_u8, -0.9, 0UL)]
    [InlineData(ILOpCode.Conv_u8, 1e19, 10000000000000000000UL)]
    [InlineData(ILOpCode.Conv_u8, 9223372036854775808.0, 9223372036854775808UL)]
    [InlineData(ILOpCode.Conv_u8, 18446744073709551616.0, ulong.MaxValue)]
    [InlineData(ILOpCode.Conv_u8, double.PositiveInfinity, ulong.MaxValue)]
    [InlineData(ILOpCode.Conv_i4, double.NaN, 0)]
    [InlineData(ILOpCode.Conv_u8, double.NaN, 0UL)]
    public void A_float_out_of_an_ints_range_or_wider_converts_to_the_nearest_value_its_target_holds(ILOpCode op, double value, object expected)
    {
        foreach (int runtime in (int[])[9, 10, 11])
        {
            Assert.Equal(expected, Converted(op, value, 8, runtime));
        }
    }

    [Theory]
    [InlineData(ILOpCode.Conv_i1, 1e19, -1, 127)]
    [InlineData(ILOpCode.Conv_i1, 70000.0, 112, 127)]
    [InlineData(ILOpCode.Conv_i1, 128.0, -128, 127)]
    [InlineData(ILOpCode.Conv_i1, -300.7, -44, -128)]
    [InlineData(ILOpCode.Conv_u1, -1.0, 255, 0)]
    [InlineData(ILOpCode.Conv_u1, 256.0, 0, 255)]
    [InlineData(ILOpCode.Conv_u1, -300.7, 212, 0)]
    [InlineData(ILOpCode.Conv_u1, 300.7 * 1e8, 255, 255)]
    [InlineData(ILOpCode.Conv_i2, 32768.0, -32768, 32767)]
    [InlineData(ILOpCode.Conv_i2, -32769.0, 32767, -32768)]
    [InlineData(ILOpCode.Conv_i2, -1e19, 0, -32768)]
    [InlineData(ILOpCode.Conv_u2, 70000.0, 4464, 65535)]
    [InlineData(ILOpCode.Conv_u2, -1.0, 65535, 0)]
    [InlineData(ILOpCode.Conv_u2, double.NaN, 0, 0)]
    public void A_float_out_of_a_narrower_types_range_converts_as_its_runtimes_version_does(ILOpCode op, double value, int net10, int net11)
    {
        Assert.Equal(net10, Converted(op, value, 8, 10));
        Assert.Equal(net10, Converted(op, value, 8, 9));
        Assert.Equal(net11, Converted(op, value, 8, 11));
    }

    [Fact]
    public void A_native_integer_takes_the_range_of_the_programs_pointer_size()
    {
        Assert.Equal(long.MaxValue, Converted(ILOpCode.Conv_i, 1e19, 8, 10));
        Assert.Equal(int.MaxValue, Converted(ILOpCode.Conv_i, 1e19, 4, 10));
        Assert.Equal(ulong.MaxValue, Converted(ILOpCode.Conv_u, 1e30, 8, 10));
        Assert.Equal(uint.MaxValue, Converted(ILOpCode.Conv_u, 1e10, 4, 10));
        Assert.Equal(0UL, Converted(ILOpCode.Conv_u, -1.0, 8, 10));
    }

    [Fact]
    public void A_single_converts_as_the_double_it_holds()
    {
        Assert.Equal(int.MaxValue, CilNumerics.Convert(ILOpCode.Conv_i4, CilValue.FromPrimitive(1e19f), 8, 10).Value);
        Assert.Equal(9999999980506447872UL, CilNumerics.Convert(ILOpCode.Conv_u8, CilValue.FromPrimitive(1e19f), 8, 10).Value);
    }

    [Theory]
    [InlineData(ILOpCode.Conv_ovf_i4, 1e10)]
    [InlineData(ILOpCode.Conv_ovf_u4, -1.0)]
    [InlineData(ILOpCode.Conv_ovf_u1, 256.0)]
    [InlineData(ILOpCode.Conv_ovf_i1, double.NaN)]
    [InlineData(ILOpCode.Conv_ovf_u8_un, -1.0)]
    public void A_checked_conversion_out_of_range_throws_an_overflow(ILOpCode op, double value)
    {
        foreach (int runtime in (int[])[10, 11])
        {
            Assert.Throws<OverflowException>(() => Converted(op, value, 8, runtime));
        }
    }

    [Theory]
    [InlineData(ILOpCode.Conv_ovf_u4, -0.9, 0u)]
    [InlineData(ILOpCode.Conv_ovf_i1, 127.9, 127)]
    [InlineData(ILOpCode.Conv_ovf_i8, -9.5, -9L)]
    public void A_checked_conversion_whose_whole_part_fits_converts(ILOpCode op, double value, object expected)
    {
        Assert.Equal(expected, Converted(op, value, 8, 11));
    }

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
