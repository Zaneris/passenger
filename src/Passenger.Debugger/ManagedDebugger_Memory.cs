using Passenger.Debugger.Interop;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    public int MemoryPointerSize => CilValue.PointerSize;

    public Task<ulong> ResolveMemoryAsync(string expression, int frameId)
    {
        if (!_stopShown || EvalStatus.IsRunning || _expressionEvaluator is not { } evaluator)
        {
            throw new InvalidOperationException("Pause the program to resolve an address");
        }
        return evaluator.MemoryAddressAsync(expression, ContextOf(frameId));
    }

    /// <summary>Reads a bounded range at the current stop, returning only the bytes the runtime could read.</summary>
    public byte[] ReadMemory(ulong address, int count)
    {
        if (!_stopShown || EvalStatus.IsRunning || _process is not { } process)
        {
            throw new InvalidOperationException("Pause the program to read its memory");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 4096);

        ulong remaining = ulong.MaxValue - address;
        int size = remaining < (ulong)(count - 1) ? (int)remaining + 1 : count;
        byte[] bytes = new byte[size];
        process.TryReadMemory(new CORDB_ADDRESS(address), (uint)size, bytes, out nuint read);
        return read == (nuint)size ? bytes : read < (nuint)size ? bytes[..(int)read] : [];
    }
}
