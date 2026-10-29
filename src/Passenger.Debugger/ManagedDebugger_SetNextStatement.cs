using System.Runtime.InteropServices;
using Passenger.Dap;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>Set Next Statement: a paused thread goes on from another line of the method it is in, the runtime moving
/// its instruction pointer (ICorDebugILFrame.SetIP) once it says the move is safe.</summary>
public partial class ManagedDebugger
{
    /// <summary>Where a paused thread could go on from at a line, or a place in it given a column: the line's first statement
    /// (1-based, as its sequence point starts) and its IL offset, when the line is in the method its top frame runs, else a
    /// statement of that method starting on the line beside another's; null otherwise.</summary>
    public (int Line, int Column, int ILOffset)? GotoTarget(int threadId, string filePath, int line, int? column = null)
    {
        if (!_threads.TryGetValue(threadId, out ThreadInfo? info) || info.Thread.ActiveFrame is not ICorDebugILFrame frame)
        {
            return null;
        }
        ICorDebugFunction function = frame.Function;
        if (FindModule(function.Module) is not { } module)
        {
            return null;
        }
        ModuleMetadataReader.ResolvedBreakpoint? target = module.MetadataReader.ResolveBreakpoint(filePath, line, column);
        if (target?.MethodToken != function.Token)
        {
            target = module.MetadataReader.ResolveBreakpoint(filePath, line, column, function.Token) is { } own
                && own.StartLine == line && own.MethodToken == function.Token ? own : null;
        }
        return target is null ? null : (target.StartLine, target.StartColumn, target.ILOffset);
    }

    /// <summary>Moves a paused thread's next statement to an IL offset of its top frame's method. Throws with the
    /// runtime's reason when the move is not safe (out of a catch or finally, into another try).</summary>
    public void SetNextStatement(int threadId, int ilOffset)
    {
        if (!_threads.TryGetValue(threadId, out ThreadInfo? info) || info.Thread.ActiveFrame is not ICorDebugILFrame frame)
        {
            throw new InvalidOperationException("The thread is not stopped in managed code");
        }
        int safe = frame.TryCanSetIP(checked((uint)ilOffset));
        if (safe < 0)
        {
            throw new InvalidOperationException($"The program cannot go on from there ({Marshal.GetExceptionForHR(safe)?.Message ?? $"0x{safe:X8}"})");
        }
        frame.SetIP(ilOffset);
        ForgetReturnedValues();
        // Every frame and value of the thread was invalidated by the move: the stop is new.
        _variableManager.ClearAndDisposeHandleValues();
        _frameReferenceManager.Clear();
        ReportStop(threadId, StopReasons.Goto);
    }
}
