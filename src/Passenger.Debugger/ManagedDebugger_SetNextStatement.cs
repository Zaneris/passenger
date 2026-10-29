using Passenger.Dap;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>Set Next Statement: a paused thread goes on from another line of the method it is in, the runtime moving
/// its instruction pointer (ICorDebugILFrame.SetIP) once it says the move is safe. The line is read in the code the frame
/// runs, which after a Hot Reload update may be the method's code from before it, its lines where the text now has
/// them.</summary>
public partial class ManagedDebugger
{
    /// <summary>The runtime's warning that the place moved to is part way through a statement (CORDBG_S_BAD_END_SEQUENCE_POINT).</summary>
    private const int PartWayThrough = 0x0013130C;

    /// <summary>The runtime's warning that the place moved from is part way through a statement
    /// (CORDBG_S_BAD_START_SEQUENCE_POINT): a stop after Step Out, at an exception or a data breakpoint, or a pause.</summary>
    private const int FromPartWay = 0x0013130B;

    /// <summary>Where a paused thread could go on from at a line, or a place in it given a column: the line's first statement
    /// (1-based, as its sequence point starts) and its IL offset, when the line is in the method its top frame runs, else a
    /// statement of that method starting on the line beside another's; null otherwise. Throws, saying why, for a line of
    /// code an update changed that the frame, still in the method's code from before it, does not run.</summary>
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
        int? version = ModuleMetadataReader.VersionOf(frame);
        (int, int?) running = (function.Token, version);
        ModuleMetadataReader.ResolvedBreakpoint? target = module.MetadataReader.ResolveBreakpoint(filePath, line, column, running: running);
        if (target?.MethodToken != function.Token)
        {
            target = module.MetadataReader.ResolveBreakpoint(filePath, line, column, function.Token, running) is { } own
                && own.StartLine == line && own.MethodToken == function.Token ? own : null;
        }
        if (target is not null && (target.Rewritten || (target.StartLine != line && RunsReplacedCode(function, version))))
        {
            throw new InvalidOperationException($"Line {line} is not a statement of the code the paused method runs: Hot Reload changed the method, "
                + "and it runs its code from before the change until it goes on into the new code");
        }
        return target is null ? null : (target.StartLine, target.StartColumn, target.ILOffset);
    }

    /// <summary>Whether a frame at a version of a function runs code a Hot Reload update has replaced since.</summary>
    private static bool RunsReplacedCode(ICorDebugFunction function, int? version) =>
        version is int at && function.TryGetCurrentVersionNumber(out uint current) is Cor.S_OK && current != at;

    /// <summary>Moves a paused thread's next statement to an IL offset of its top frame's method, where a statement of the
    /// code the frame runs begins. Throws, saying why, when the offset is not one, or the runtime finds the move unsafe:
    /// into a statement part way, out of a finally block, into a catch or finally block.</summary>
    public void SetNextStatement(int threadId, int ilOffset)
    {
        if (!_threads.TryGetValue(threadId, out ThreadInfo? info) || info.Thread.ActiveFrame is not ICorDebugILFrame frame)
        {
            throw new InvalidOperationException("The thread is not stopped in managed code");
        }
        ICorDebugFunction function = frame.Function;
        if (FindModule(function.Module) is not { MetadataReader.HasSymbols: true } module
            || !module.MetadataReader.Points(function.Token, ModuleMetadataReader.VersionOf(frame)).Any(point => !point.IsHidden && point.Offset == ilOffset))
        {
            throw new InvalidOperationException("The program cannot go on from there: no statement of the code the paused method runs begins there");
        }
        int safe = frame.TryCanSetIP(checked((uint)ilOffset));
        _logger?.Invoke($"Set Next Statement on thread {threadId} to IL_{ilOffset:X4}: the runtime says 0x{safe:X8}");
        if (safe is not (Cor.S_OK or FromPartWay))
        {
            throw new InvalidOperationException($"The program cannot go on from there: {WhyNotThere(safe)}");
        }
        frame.SetIP(ilOffset);
        ForgetReturnedValues();
        // Every frame and value of the thread was invalidated by the move: the stop is new.
        _variableManager.ClearAndDisposeHandleValues();
        _frameReferenceManager.Clear();
        ReportStop(threadId, StopReasons.Goto);
    }

    /// <summary>Why the runtime will not move a thread to a place, in a person's words, by its answer to CanSetIP.</summary>
    private static string WhyNotThere(int answer) => unchecked((uint)answer) switch
    {
        PartWayThrough => "the code there is part way through a statement, which would go on without what its start computes",
        0x8013130E => "it is inside a finally block, which runs only as its try block is left",
        0x8013130F or 0x8013131E => "the program is in a finally block, which must end first",
        0x80131310 => "it is inside a catch block, which runs only for an exception it catches",
        0x8013131F => "the program is in a catch block, which this system lets it leave only by its end",
        0x8013131C => "it would enter or leave an exception filter",
        0x80131333 => "the thread is stopped at an exception being thrown",
        _ => $"the runtime will not move the thread there (0x{answer:X8})",
    };
}
