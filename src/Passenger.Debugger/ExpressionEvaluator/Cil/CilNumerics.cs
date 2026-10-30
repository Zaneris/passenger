using System.Reflection.Metadata;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

/// <summary>The evaluation stack's arithmetic, comparisons and conversions, as ECMA-335 III.1.5 gives them for int32, int64,
/// native int and F operands. A checked operation that overflows, or an integer division by zero, throws what the runtime
/// would throw running the instruction in the program.</summary>
internal static class CilNumerics
{
    public static CilValue Binary(ILOpCode op, CilValue left, CilValue right)
    {
        if (left.Value is float or double || right.Value is float or double)
        {
            double a = left.AsFloat();
            double b = right.AsFloat();
            double result = op switch
            {
                ILOpCode.Add => a + b,
                ILOpCode.Sub => a - b,
                ILOpCode.Mul => a * b,
                ILOpCode.Div => a / b,
                ILOpCode.Rem => a % b,
                _ => throw new NotSupportedException($"Floating-point operation '{op}' is not supported")
            };
            return left.Value is double || right.Value is double ? CilValue.FromPrimitive(result) : CilValue.FromPrimitive((float)result);
        }
        return left.Value is long or ulong || right.Value is long or ulong
            ? CilValue.FromPrimitive(Int64(op, left.AsInt64(), right.AsInt64()))
            : CilValue.FromPrimitive(Int32(op, left.AsInt32(), right.AsInt32()));
    }

    private static object Int64(ILOpCode op, long a, long b) => op switch
    {
        ILOpCode.Add => a + b,
        ILOpCode.Sub => a - b,
        ILOpCode.Mul => a * b,
        ILOpCode.Div => a / b,
        ILOpCode.Rem => a % b,
        ILOpCode.And => a & b,
        ILOpCode.Or => a | b,
        ILOpCode.Xor => a ^ b,
        ILOpCode.Shl => a << ((int)b & 0x3f),
        ILOpCode.Shr => a >> ((int)b & 0x3f),
        ILOpCode.Shr_un => unchecked((long)((ulong)a >> ((int)b & 0x3f))),
        ILOpCode.Div_un => unchecked((ulong)a) / unchecked((ulong)b),
        ILOpCode.Rem_un => unchecked((ulong)a) % unchecked((ulong)b),
        ILOpCode.Add_ovf => checked(a + b),
        ILOpCode.Sub_ovf => checked(a - b),
        ILOpCode.Mul_ovf => checked(a * b),
        ILOpCode.Add_ovf_un => checked(unchecked((ulong)a) + unchecked((ulong)b)),
        ILOpCode.Sub_ovf_un => checked(unchecked((ulong)a) - unchecked((ulong)b)),
        ILOpCode.Mul_ovf_un => checked(unchecked((ulong)a) * unchecked((ulong)b)),
        _ => throw new NotSupportedException($"Integer operation '{op}' is not supported")
    };

    private static object Int32(ILOpCode op, int a, int b) => op switch
    {
        ILOpCode.Add => a + b,
        ILOpCode.Sub => a - b,
        ILOpCode.Mul => a * b,
        ILOpCode.Div => a / b,
        ILOpCode.Rem => a % b,
        ILOpCode.And => a & b,
        ILOpCode.Or => a | b,
        ILOpCode.Xor => a ^ b,
        ILOpCode.Shl => a << (b & 0x1f),
        ILOpCode.Shr => a >> (b & 0x1f),
        ILOpCode.Shr_un => unchecked((int)((uint)a >> (b & 0x1f))),
        ILOpCode.Div_un => unchecked((uint)a) / unchecked((uint)b),
        ILOpCode.Rem_un => unchecked((uint)a) % unchecked((uint)b),
        ILOpCode.Add_ovf => checked(a + b),
        ILOpCode.Sub_ovf => checked(a - b),
        ILOpCode.Mul_ovf => checked(a * b),
        ILOpCode.Add_ovf_un => checked(unchecked((uint)a) + unchecked((uint)b)),
        ILOpCode.Sub_ovf_un => checked(unchecked((uint)a) - unchecked((uint)b)),
        ILOpCode.Mul_ovf_un => checked(unchecked((uint)a) * unchecked((uint)b)),
        _ => throw new NotSupportedException($"Integer operation '{op}' is not supported")
    };

    public static CilValue Negate(CilValue value) => value.Value switch
    {
        float single => CilValue.FromPrimitive(-single),
        double number => CilValue.FromPrimitive(-number),
        long or ulong => CilValue.FromPrimitive(-value.AsInt64()),
        _ => CilValue.FromPrimitive(-value.AsInt32()),
    };

    /// <summary>An int32 stays one: only a 64-bit operand gives a 64-bit result.</summary>
    public static CilValue Not(CilValue value) => value.Value is long or ulong
        ? CilValue.FromPrimitive(~value.AsInt64())
        : CilValue.FromPrimitive(~value.AsInt32());

    /// <summary>ceq, cgt, cgt.un, clt or clt.un.</summary>
    public static bool Compare(ILOpCode op, CilValue left, CilValue right)
    {
        if (op is ILOpCode.Ceq)
        {
            if (left.IsNull || right.IsNull)
            {
                return left.IsNull == right.IsNull;
            }
            if (left.CorValue is ICorDebugReferenceValue leftReference && right.CorValue is ICorDebugReferenceValue rightReference)
            {
                return leftReference.Value == rightReference.Value;
            }
            if (left.Value is float or double || right.Value is float or double)
            {
                return left.AsFloat() == right.AsFloat();
            }
            if (left.TryGetInt64(out long leftInteger) && right.TryGetInt64(out long rightInteger))
            {
                return leftInteger == rightInteger;
            }
            return Equals(left.Value, right.Value);
        }
        if (left.CorValue is ICorDebugReferenceValue || right.CorValue is ICorDebugReferenceValue || left.IsNull || right.IsNull)
        {
            // cgt.un and clt.un compare a reference with null, as `x != null` compiles to.
            return op switch
            {
                ILOpCode.Cgt_un => !left.IsNull && right.IsNull,
                ILOpCode.Clt_un => left.IsNull && !right.IsNull,
                _ => throw new InvalidOperationException($"Reference values cannot be compared with '{op}'")
            };
        }
        if (left.Value is float or double || right.Value is float or double)
        {
            double a = left.AsFloat();
            double b = right.AsFloat();
            return op switch
            {
                ILOpCode.Cgt => a > b,
                ILOpCode.Clt => a < b,
                // The unordered comparisons hold when either side is NaN.
                ILOpCode.Cgt_un => double.IsNaN(a) || double.IsNaN(b) || a > b,
                _ => double.IsNaN(a) || double.IsNaN(b) || a < b,
            };
        }
        return op switch
        {
            ILOpCode.Cgt => left.AsInt64() > right.AsInt64(),
            ILOpCode.Clt => left.AsInt64() < right.AsInt64(),
            ILOpCode.Cgt_un => unchecked((ulong)left.AsInt64()) > unchecked((ulong)right.AsInt64()),
            _ => unchecked((ulong)left.AsInt64()) < unchecked((ulong)right.AsInt64()),
        };
    }

    /// <summary>Whether a conditional branch that compares two operands (beq to blt.un, short forms included) is taken. On
    /// floats bge and ble are the complements of the unordered compares, and bge.un and ble.un of the ordered ones
    /// (ECMA-335 III.3.6 to III.3.9), so a NaN fails x &gt;= y and passes bge.un.</summary>
    public static bool Branch(ILOpCode op, CilValue left, CilValue right)
    {
        bool floats = left.Value is float or double || right.Value is float or double;
        return op switch
        {
            ILOpCode.Beq or ILOpCode.Beq_s => Compare(ILOpCode.Ceq, left, right),
            ILOpCode.Bne_un or ILOpCode.Bne_un_s => !Compare(ILOpCode.Ceq, left, right),
            ILOpCode.Bgt or ILOpCode.Bgt_s => Compare(ILOpCode.Cgt, left, right),
            ILOpCode.Bgt_un or ILOpCode.Bgt_un_s => Compare(ILOpCode.Cgt_un, left, right),
            ILOpCode.Blt or ILOpCode.Blt_s => Compare(ILOpCode.Clt, left, right),
            ILOpCode.Blt_un or ILOpCode.Blt_un_s => Compare(ILOpCode.Clt_un, left, right),
            ILOpCode.Bge or ILOpCode.Bge_s => !Compare(floats ? ILOpCode.Clt_un : ILOpCode.Clt, left, right),
            ILOpCode.Bge_un or ILOpCode.Bge_un_s => !Compare(floats ? ILOpCode.Clt : ILOpCode.Clt_un, left, right),
            ILOpCode.Ble or ILOpCode.Ble_s => !Compare(floats ? ILOpCode.Cgt_un : ILOpCode.Cgt, left, right),
            ILOpCode.Ble_un or ILOpCode.Ble_un_s => !Compare(floats ? ILOpCode.Cgt : ILOpCode.Cgt_un, left, right),
            _ => throw new NotSupportedException($"Conditional branch '{op}' is not supported")
        };
    }

    /// <summary>A conv instruction, as the program's runtime runs it. A checked one (conv.ovf) reads an integer source as
    /// signed, and one ending .un as unsigned, each as wide as it is on the stack; a floating-point source is its value
    /// either way.</summary>
    public static CilValue Convert(ILOpCode op, CilValue value) => Convert(op, value, CilValue.PointerSize, CilValue.RuntimeVersion);

    /// <summary>A conv instruction run by a program whose native integers are <paramref name="pointerSize"/> bytes wide,
    /// on .NET <paramref name="runtimeVersion"/>.</summary>
    internal static CilValue Convert(ILOpCode op, CilValue value, int pointerSize, int runtimeVersion)
    {
        bool wide = pointerSize == 8;
        if (value.Value is float or double)
        {
            return ConvertFloat(op, value.AsFloat(), wide, runtimeVersion);
        }
        long signed = value.AsInt64();
        ulong unsigned = value.AsUInt64();
        return op switch
        {
            ILOpCode.Conv_i1 => CilValue.FromPrimitive((int)unchecked((sbyte)signed)),
            ILOpCode.Conv_u1 => CilValue.FromPrimitive((int)unchecked((byte)signed)),
            ILOpCode.Conv_i2 => CilValue.FromPrimitive((int)unchecked((short)signed)),
            ILOpCode.Conv_u2 => CilValue.FromPrimitive((int)unchecked((ushort)signed)),
            ILOpCode.Conv_i4 => CilValue.FromPrimitive(unchecked((int)signed)),
            ILOpCode.Conv_u4 => CilValue.FromPrimitive(unchecked((uint)signed)),
            ILOpCode.Conv_i8 => CilValue.FromPrimitive(signed),
            ILOpCode.Conv_u8 => CilValue.FromPrimitive(unsigned),
            ILOpCode.Conv_r4 => CilValue.FromPrimitive((float)signed),
            ILOpCode.Conv_r8 => CilValue.FromPrimitive((double)signed),
            ILOpCode.Conv_r_un => CilValue.FromPrimitive((double)unsigned),
            ILOpCode.Conv_i => wide ? CilValue.FromPrimitive(signed) : CilValue.FromPrimitive(unchecked((int)signed)),
            ILOpCode.Conv_u => wide ? CilValue.FromPrimitive(unsigned) : CilValue.FromPrimitive(unchecked((uint)signed)),
            ILOpCode.Conv_ovf_i1 => CilValue.FromPrimitive((int)checked((sbyte)signed)),
            ILOpCode.Conv_ovf_u1 => CilValue.FromPrimitive((int)checked((byte)signed)),
            ILOpCode.Conv_ovf_i2 => CilValue.FromPrimitive((int)checked((short)signed)),
            ILOpCode.Conv_ovf_u2 => CilValue.FromPrimitive((int)checked((ushort)signed)),
            ILOpCode.Conv_ovf_i4 => CilValue.FromPrimitive(checked((int)signed)),
            ILOpCode.Conv_ovf_u4 => CilValue.FromPrimitive(checked((uint)signed)),
            ILOpCode.Conv_ovf_i8 => CilValue.FromPrimitive(signed),
            ILOpCode.Conv_ovf_u8 => CilValue.FromPrimitive(checked((ulong)signed)),
            ILOpCode.Conv_ovf_i => wide ? CilValue.FromPrimitive(signed) : CilValue.FromPrimitive(checked((int)signed)),
            ILOpCode.Conv_ovf_u => wide ? CilValue.FromPrimitive(checked((ulong)signed)) : CilValue.FromPrimitive(checked((uint)signed)),
            ILOpCode.Conv_ovf_i1_un => CilValue.FromPrimitive((int)checked((sbyte)unsigned)),
            ILOpCode.Conv_ovf_u1_un => CilValue.FromPrimitive((int)checked((byte)unsigned)),
            ILOpCode.Conv_ovf_i2_un => CilValue.FromPrimitive((int)checked((short)unsigned)),
            ILOpCode.Conv_ovf_u2_un => CilValue.FromPrimitive((int)checked((ushort)unsigned)),
            ILOpCode.Conv_ovf_i4_un => CilValue.FromPrimitive(checked((int)unsigned)),
            ILOpCode.Conv_ovf_u4_un => CilValue.FromPrimitive(checked((uint)unsigned)),
            ILOpCode.Conv_ovf_i8_un => CilValue.FromPrimitive(checked((long)unsigned)),
            ILOpCode.Conv_ovf_u8_un => CilValue.FromPrimitive(unsigned),
            ILOpCode.Conv_ovf_i_un => wide ? CilValue.FromPrimitive(checked((long)unsigned)) : CilValue.FromPrimitive(checked((int)unsigned)),
            ILOpCode.Conv_ovf_u_un => wide ? CilValue.FromPrimitive(unsigned) : CilValue.FromPrimitive(checked((uint)unsigned)),
            _ => throw new NotSupportedException($"Conversion '{op}' is not supported")
        };
    }

    /// <summary>A floating-point value to an integer, as .NET 9 and later convert it: one out of the target's range is the
    /// nearest it holds, and NaN 0. A type narrower than an int takes its own range's nearest from .NET 11, and before
    /// it an int's nearest cut to its bits (a runtime before .NET 9, which leaves the result to the processor, is taken as
    /// .NET 9). A checked conversion throws as the runtime's does, whatever its version.</summary>
    private static CilValue ConvertFloat(ILOpCode op, double value, bool wide, int runtimeVersion)
    {
        bool ownRange = runtimeVersion >= 11;
        int whole = (int)Nearest(value, int.MinValue, int.MaxValue);
        return op switch
        {
            ILOpCode.Conv_i1 => CilValue.FromPrimitive(ownRange ? (int)Nearest(value, sbyte.MinValue, sbyte.MaxValue) : unchecked((sbyte)whole)),
            ILOpCode.Conv_u1 => CilValue.FromPrimitive(ownRange ? (int)Nearest(value, byte.MinValue, byte.MaxValue) : unchecked((byte)whole)),
            ILOpCode.Conv_i2 => CilValue.FromPrimitive(ownRange ? (int)Nearest(value, short.MinValue, short.MaxValue) : unchecked((short)whole)),
            ILOpCode.Conv_u2 => CilValue.FromPrimitive(ownRange ? (int)Nearest(value, ushort.MinValue, ushort.MaxValue) : unchecked((ushort)whole)),
            ILOpCode.Conv_i4 => CilValue.FromPrimitive(whole),
            ILOpCode.Conv_u4 => CilValue.FromPrimitive((uint)Nearest(value, uint.MinValue, uint.MaxValue)),
            ILOpCode.Conv_i8 => CilValue.FromPrimitive(Nearest(value, long.MinValue, long.MaxValue)),
            ILOpCode.Conv_u8 => CilValue.FromPrimitive(NearestUnsigned(value)),
            ILOpCode.Conv_r4 => CilValue.FromPrimitive((float)value),
            ILOpCode.Conv_r8 or ILOpCode.Conv_r_un => CilValue.FromPrimitive(value),
            ILOpCode.Conv_i => wide ? CilValue.FromPrimitive(Nearest(value, long.MinValue, long.MaxValue)) : CilValue.FromPrimitive(whole),
            ILOpCode.Conv_u => wide ? CilValue.FromPrimitive(NearestUnsigned(value)) : CilValue.FromPrimitive((uint)Nearest(value, uint.MinValue, uint.MaxValue)),
            ILOpCode.Conv_ovf_i1 or ILOpCode.Conv_ovf_i1_un => CilValue.FromPrimitive((int)checked((sbyte)value)),
            ILOpCode.Conv_ovf_u1 or ILOpCode.Conv_ovf_u1_un => CilValue.FromPrimitive((int)checked((byte)value)),
            ILOpCode.Conv_ovf_i2 or ILOpCode.Conv_ovf_i2_un => CilValue.FromPrimitive((int)checked((short)value)),
            ILOpCode.Conv_ovf_u2 or ILOpCode.Conv_ovf_u2_un => CilValue.FromPrimitive((int)checked((ushort)value)),
            ILOpCode.Conv_ovf_i4 or ILOpCode.Conv_ovf_i4_un => CilValue.FromPrimitive(checked((int)value)),
            ILOpCode.Conv_ovf_u4 or ILOpCode.Conv_ovf_u4_un => CilValue.FromPrimitive(checked((uint)value)),
            ILOpCode.Conv_ovf_i8 or ILOpCode.Conv_ovf_i8_un => CilValue.FromPrimitive(checked((long)value)),
            ILOpCode.Conv_ovf_u8 or ILOpCode.Conv_ovf_u8_un => CilValue.FromPrimitive(checked((ulong)value)),
            ILOpCode.Conv_ovf_i or ILOpCode.Conv_ovf_i_un => wide ? CilValue.FromPrimitive(checked((long)value)) : CilValue.FromPrimitive(checked((int)value)),
            ILOpCode.Conv_ovf_u or ILOpCode.Conv_ovf_u_un => wide ? CilValue.FromPrimitive(checked((ulong)value)) : CilValue.FromPrimitive(checked((uint)value)),
            _ => throw new NotSupportedException($"Conversion '{op}' is not supported")
        };
    }

    /// <summary>The integer from minimum to maximum nearest a value's whole part; 0 for NaN.</summary>
    private static long Nearest(double value, long minimum, long maximum) =>
        double.IsNaN(value) ? 0 : value <= minimum ? minimum : value >= maximum ? maximum : (long)value;

    /// <summary>The ulong nearest a value's whole part; 0 for NaN.</summary>
    private static ulong NearestUnsigned(double value) =>
        double.IsNaN(value) || value <= 0 ? 0 : value >= ulong.MaxValue ? ulong.MaxValue : (ulong)value;
}
