using System.Reflection.Emit;
using System.Reflection.Metadata;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.UnitTests.Debugger;

/// <summary>The evaluator's conditional branches on floats, held to what the runtime the tests run on does with the same
/// IL: every comparing branch, short forms too, on NaN, infinities, equal and ordered operands.</summary>
public class BranchTests
{
    private static readonly double[] Operands = [double.NaN, 1.0, 2.0, double.PositiveInfinity, double.NegativeInfinity, -0.0, 0.0];

    private static readonly ILOpCode[] Branches =
    [
        ILOpCode.Beq, ILOpCode.Bne_un, ILOpCode.Bgt, ILOpCode.Bgt_un, ILOpCode.Blt, ILOpCode.Blt_un,
        ILOpCode.Bge, ILOpCode.Bge_un, ILOpCode.Ble, ILOpCode.Ble_un,
        ILOpCode.Beq_s, ILOpCode.Bne_un_s, ILOpCode.Bgt_s, ILOpCode.Bgt_un_s, ILOpCode.Blt_s, ILOpCode.Blt_un_s,
        ILOpCode.Bge_s, ILOpCode.Bge_un_s, ILOpCode.Ble_s, ILOpCode.Ble_un_s,
    ];

    [Fact]
    public void A_branch_on_doubles_or_singles_is_taken_where_the_runtimes_is() => Cases.Each(Branches, op =>
    {
        Func<double, double, bool> doubles = Emitted<double>(op);
        Func<float, float, bool> singles = Emitted<float>(op);
        foreach (double left in Operands)
        {
            foreach (double right in Operands)
            {
                bool evaluated = CilNumerics.Branch(op, CilValue.FromPrimitive(left), CilValue.FromPrimitive(right));
                Assert.True(doubles(left, right) == evaluated,
                    $"{op} on the doubles {left} and {right} is {(evaluated ? "taken" : "not taken")} by the evaluator, where the program's runtime {(evaluated ? "does not take it" : "takes it")}");
                bool single = CilNumerics.Branch(op, CilValue.FromPrimitive((float)left), CilValue.FromPrimitive((float)right));
                Assert.True(singles((float)left, (float)right) == single,
                    $"{op} on the singles {left} and {right} is {(single ? "taken" : "not taken")} by the evaluator, where the program's runtime {(single ? "does not take it" : "takes it")}");
            }
        }
    });

    /// <summary>A method that runs the branch on its two arguments and answers whether it was taken.</summary>
    private static Func<T, T, bool> Emitted<T>(ILOpCode op)
    {
        DynamicMethod method = new($"Branch_{op}", typeof(bool), [typeof(T), typeof(T)]);
        ILGenerator il = method.GetILGenerator();
        Label taken = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodeOf(op), taken);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(taken);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<T, T, bool>>();
    }

    private static OpCode OpCodeOf(ILOpCode op) =>
        typeof(OpCodes).GetFields().Select(field => (OpCode)field.GetValue(null)!).Single(code => code.Value == (short)op);
}
