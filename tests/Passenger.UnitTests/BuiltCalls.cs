using System.Reflection;
using System.Reflection.Emit;

namespace Passenger.UnitTests;

/// <summary>What built code calls, read from its IL, for the rules held to what a method's code does: each method it calls,
/// makes or takes as a delegate, with the field or method the instruction just before it names, which pushed the call's
/// last argument where that argument is read from one.</summary>
internal static class BuiltCalls
{
    /// <summary>Each instruction by its first byte, and those beginning 0xFE by their second.</summary>
    private static readonly (OpCode[] OneByte, OpCode[] TwoByte) Instructions = ReadInstructions();

    /// <summary>Every method and constructor a type declares itself, its compiler-made lambdas' and state machines' being
    /// their own nested types'.</summary>
    public static IEnumerable<MethodBase> Declared(Type type)
    {
        const BindingFlags Own = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type.GetMethods(Own).Concat<MethodBase>(type.GetConstructors(Own));
    }

    /// <summary>The type a type is written in, where the compiler nests a lambda's or a state machine's: itself at the top.</summary>
    public static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } declaring)
        {
            type = declaring;
        }
        return type;
    }

    /// <summary>The methods <paramref name="method"/>'s code calls, makes or takes as a delegate, each with the member the
    /// instruction before it names (null where that instruction names none).</summary>
    public static IEnumerable<(MethodBase Called, MemberInfo? Before)> Of(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } code)
        {
            yield break;
        }
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        MemberInfo? before = null;
        for (int at = 0; at < code.Length;)
        {
            OpCode instruction = code[at] == 0xFE ? Instructions.TwoByte[code[at + 1]] : Instructions.OneByte[code[at]];
            at += instruction.Size;
            MemberInfo? named = instruction.OperandType switch
            {
                OperandType.InlineMethod => method.Module.ResolveMethod(BitConverter.ToInt32(code, at), typeArguments, methodArguments),
                OperandType.InlineField => method.Module.ResolveField(BitConverter.ToInt32(code, at), typeArguments, methodArguments),
                _ => null,
            };
            if (named is MethodBase called)
            {
                yield return (called, before);
            }
            before = named;
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
