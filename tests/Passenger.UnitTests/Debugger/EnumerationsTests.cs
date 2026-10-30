using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Interop.Extensions;

namespace Passenger.UnitTests.Debugger;

/// <summary>The debugger's walks of the runtime's enumerators, each fed a stand-in that answers Next as the runtime's do: an
/// element, a failure with nothing fetched (a neutered enumerator, a process gone), or S_FALSE at the end.</summary>
public class EnumerationsTests
{
    private const int SFalse = 1;

    [Fact]
    public void An_enumeration_failing_with_nothing_fetched_throws_its_failure_rather_than_ending_empty()
    {
        TypeParameters frame = new(new TypeEnum(Cor.CORDBG_E_OBJECT_NEUTERED));
        COMException thrown = Assert.Throws<COMException>(() => frame.EnumerateTypeParameters().ToList());
        Assert.True(thrown.HResult == Cor.CORDBG_E_OBJECT_NEUTERED, $"A neutered enumerator was thrown as {thrown.HResult:X8}, so its caller would hear another reason");
    }

    [Fact]
    public void An_enumeration_ending_with_nothing_fetched_ends_its_walk()
    {
        Assert.Empty(new TypeParameters(new TypeEnum(SFalse)).EnumerateTypeParameters());
    }

    [Fact]
    public void A_frames_values_end_before_one_it_has_no_home_for_and_throw_any_other_failure()
    {
        Value first = new();
        ICorDebugValue[] read = new Frame(new ValueEnum(first, Cor.CORDBG_E_IL_VAR_NOT_AVAILABLE, new Value())).LocalVariables;
        Assert.True(read is [{ } only] && only == first, $"A frame's locals read {read.Length} values where the first alone stands before one it has no home for");

        COMException thrown = Assert.Throws<COMException>(() => new Frame(new ValueEnum(first, Cor.CORDBG_E_PROCESS_TERMINATED)).LocalVariables);
        Assert.True(thrown.HResult == Cor.CORDBG_E_PROCESS_TERMINATED, $"A frame's locals of a process gone were thrown as {thrown.HResult:X8}");
    }

    /// <summary>Next's answers in turn, each an element fetched or an HRESULT with nothing fetched; S_FALSE after the last.</summary>
    private abstract class Answers(object[] answers) : ICorDebugEnum
    {
        private int next;

        protected int Next<T>(T[] into, out uint fetched)
        {
            object answer = next < answers.Length ? answers[next++] : SFalse;
            if (answer is T element)
            {
                into[0] = element;
                fetched = 1;
                return Cor.S_OK;
            }
            fetched = 0;
            return (int)answer;
        }

        public int TrySkip(uint celt) => throw new NotSupportedException();

        public int TryReset() => throw new NotSupportedException();

        public int TryClone(out ICorDebugEnum ppEnum) => throw new NotSupportedException();

        public int TryGetCount(out uint pcelt) => throw new NotSupportedException();
    }

    private sealed class TypeEnum(params object[] answers) : Answers(answers), ICorDebugTypeEnum
    {
        public int TryNext(uint celt, ICorDebugType[] values, out uint pceltFetched) => Next(values, out pceltFetched);
    }

    private sealed class ValueEnum(params object[] answers) : Answers(answers), ICorDebugValueEnum
    {
        public int TryNext(uint celt, ICorDebugValue[] values, out uint pceltFetched) => Next(values, out pceltFetched);
    }

    private sealed class TypeParameters(ICorDebugTypeEnum parameters) : ICorDebugILFrame2
    {
        public int TryRemapFunction(uint newILOffset) => throw new NotSupportedException();

        public int TryEnumerateTypeParameters(out ICorDebugTypeEnum ppTyParEnum)
        {
            ppTyParEnum = parameters;
            return Cor.S_OK;
        }
    }

    private sealed class Value : ICorDebugValue
    {
        public int TryGetType(out CorElementType pType) => throw new NotSupportedException();

        public int TryGetSize(out uint pSize) => throw new NotSupportedException();

        public int TryGetAddress(out CORDB_ADDRESS pAddress) => throw new NotSupportedException();

        public int TryCreateBreakpoint(out ICorDebugValueBreakpoint ppBreakpoint) => throw new NotSupportedException();
    }

    private sealed class Frame(ICorDebugValueEnum locals) : ICorDebugILFrame
    {
        public int TryEnumerateLocalVariables(out ICorDebugValueEnum ppValueEnum)
        {
            ppValueEnum = locals;
            return Cor.S_OK;
        }

        public int TryGetChain(out ICorDebugChain ppChain) => throw new NotSupportedException();

        public int TryGetCode(out ICorDebugCode ppCode) => throw new NotSupportedException();

        public int TryGetFunction(out ICorDebugFunction ppFunction) => throw new NotSupportedException();

        public int TryGetFunctionToken(out mdMethodDef pToken) => throw new NotSupportedException();

        public int TryGetStackRange(out CORDB_ADDRESS pStart, out CORDB_ADDRESS pEnd) => throw new NotSupportedException();

        public int TryGetCaller(out ICorDebugFrame ppFrame) => throw new NotSupportedException();

        public int TryGetCallee(out ICorDebugFrame ppFrame) => throw new NotSupportedException();

        public int TryCreateStepper(out ICorDebugStepper ppStepper) => throw new NotSupportedException();

        public int TryGetIP(out uint pnOffset, out CorDebugMappingResult pMappingResult) => throw new NotSupportedException();

        public int TrySetIP(uint nOffset) => throw new NotSupportedException();

        public int TryGetLocalVariable(uint dwIndex, out ICorDebugValue ppValue) => throw new NotSupportedException();

        public int TryEnumerateArguments(out ICorDebugValueEnum ppValueEnum) => throw new NotSupportedException();

        public int TryGetArgument(uint dwIndex, out ICorDebugValue ppValue) => throw new NotSupportedException();

        public int TryGetStackDepth(out uint pDepth) => throw new NotSupportedException();

        public int TryGetStackValue(uint dwIndex, out ICorDebugValue ppValue) => throw new NotSupportedException();

        public int TryCanSetIP(uint nOffset) => throw new NotSupportedException();
    }
}
