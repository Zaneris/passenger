using Passenger.Dap;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>An exception the catch the compiler writes around an async method's body takes is not handled there: it faults
/// the method's task and is thrown again in what awaits it, or, from an async void method, where the method was posted.
/// Followed up the methods awaiting each other while its throw's frames still stand, it is known to go unhandled where it
/// reaches the program's async Main, or an async void method, with no catch of the user's on the way, and stops as such an
/// exception does, at its throw with its locals.</summary>
public partial class ManagedDebugger
{
    /// <summary>How many methods awaiting each other are followed before the answer is left unknown.</summary>
    private const int AwaitersFollowed = 64;

    /// <summary>The exception last known to go unhandled through what awaits it, whose later stops as unhandled (thrown
    /// again in each method awaiting it, then by the program's entry point) say nothing new; null when there is none.</summary>
    private ICorDebugHandleValue? _endsUnhandled;

    /// <summary>How an exception caught at <paramref name="handlerOffset"/> in <paramref name="handler"/> goes on when that
    /// is its async method's own catch and no method awaiting the method catches it: unhandled where it reaches the
    /// program's async Main, handled outside the user's code where an async void method's catch posts it on. Null where
    /// anything on the way may catch it, or what takes a task is no method awaiting it (a wait, a fire and forget,
    /// Task.WhenAll), so it may yet be handled. Known as it is caught, its throw's frames still on the stack.</summary>
    private ExceptionScenarios? UnhandledThroughAwaits(ICorDebugThread thread, ICorDebugFrame? handler, uint handlerOffset)
    {
        try
        {
            if (handler is not ICorDebugILFrame frame || FindModule(frame.Function.Module) is not { } module
                || module.MetadataReader.AsyncCatch(frame.Function.Token, ModuleMetadataReader.VersionOf(frame)) is not { } caught || caught.Offset != (int)handlerOffset
                || ThrownTypes(thread) is not { } thrown || frame.TryGetArgument(0, out ICorDebugValue? stateMachine) < 0 || stateMachine is null)
            {
                return null;
            }
            int moveNext = frame.Function.Token;
            if (caught.Recorded && !IsAsyncMain(module, moveNext))
            {
                // Only an async void method's catch is recorded but the async Main's: it hands the exception on to be thrown where the method was posted.
                return ExceptionScenarios.HandledInExternalCode;
            }
            ICorDebugFrame? running = frame;
            List<ICorDebugFrame> stack = [.. FramesOnStack(thread)];
            for (int followed = 0; followed < AwaitersFollowed; followed++)
            {
                if (IsAsyncMain(module, moveNext))
                {
                    _logger?.Invoke($"An exception caught to fault a task on thread {thread.Id} goes on unhandled through {followed} awaiting method(s) to the program's async Main");
                    return ExceptionScenarios.Unhandled;
                }
                if ((AwaiterOf(stateMachine) ?? (running is null ? null : CallerAwaiting(stack, running, module, moveNext))) is not { } next
                    || next.Module.MetadataReader.AsyncCatch(next.MoveNext) is not { } nextCatch)
                {
                    return null;
                }
                int at = next.Running is ICorDebugILFrame caller ? caller.IP.pnOffset : ResumeOffset(next.Module, next.MoveNext, next.StateMachine) ?? -1;
                if (at < 0 || next.Module.MetadataReader.MayCatchAt(next.MoveNext, at, nextCatch.Offset, thrown))
                {
                    return null;
                }
                (module, moveNext, stateMachine, running) = next;
            }
            return null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"Whether an exception caught to fault a task on thread {thread.Id} goes unhandled is unknown: {error.Message}");
            return null;
        }
    }

    /// <summary>Whether a MoveNext is the program's async Main's, which its entry point waits on, catching nothing.</summary>
    private static bool IsAsyncMain(ModuleInfo module, int moveNext) => module.MetadataReader.ResolveManagedEntryPoint() is { MethodToken: var entry } && entry == moveNext;

    /// <summary>The method awaiting a state machine's task, by its own state machine, suspended at an await; null where
    /// the task has no such continuation.</summary>
    private (ModuleInfo Module, int MoveNext, ICorDebugValue StateMachine, ICorDebugFrame? Running)? AwaiterOf(ICorDebugValue stateMachine)
    {
        if (GetStateMachineTask(stateMachine)?.UnwrapDebugValueToObjectOrNull() is not { } task
            || GetInstanceFieldValue(task, "m_continuationObject") is not { } continuation
            || FindStateMachine(continuation, 0) is not { } awaiter
            || CreateSyntheticAsyncCallerFrame(awaiter) is not { } method)
        {
            return null;
        }
        return (method.Module, method.MoveNextToken, awaiter, null);
    }

    /// <summary>The async method on the thread's stack below one that has not yet yielded, which called it and awaits
    /// the task it returns in the same statement; null where its caller is no async method, or does not await it there.</summary>
    private (ModuleInfo Module, int MoveNext, ICorDebugValue StateMachine, ICorDebugFrame? Running)? CallerAwaiting(List<ICorDebugFrame> stack, ICorDebugFrame running,
        ModuleInfo module, int moveNext)
    {
        if (module.MetadataReader.GetStateMachineKickoffMethodToken(moveNext) is not int kickoff || RangeOf(running) is not { } range)
        {
            return null;
        }
        int at = stack.FindIndex(frame => RangeOf(frame) == range);
        int started = at < 0 ? -1 : stack.FindIndex(at + 1, frame => frame is ICorDebugILFrame il && il.Function.Token == kickoff && FindModule(il.Function.Module) == module);
        if (started < 0 || stack.Skip(started + 1).OfType<ICorDebugILFrame>().FirstOrDefault() is not { } caller
            || FindModule(caller.Function.Module) is not { } callerModule
            || callerModule.MetadataReader.GetStateMachineKickoffMethodToken(caller.Function.Token) is null
            || callerModule.MetadataReader.GetAsyncMethodSteppingInfo(caller.Function.Token, ModuleMetadataReader.VersionOf(caller)) is not { } awaits
            || caller.TryGetArgument(0, out ICorDebugValue? stateMachine) < 0 || stateMachine is null)
        {
            return null;
        }
        int call = caller.IP.pnOffset;
        int? version = ModuleMetadataReader.VersionOf(caller);
        uint? awaited = awaits.AwaitInfos.Select(a => a.YieldOffset).Where(yield => yield > call).Order().Cast<uint?>().FirstOrDefault();
        bool sameStatement = awaited is { } yieldOffset
            && callerModule.MetadataReader.GetSourceLocationForOffset(caller.Function.Token, call, version) is { } statement
            && callerModule.MetadataReader.GetSourceLocationForOffset(caller.Function.Token, (int)yieldOffset, version) is { } awaiting
            && (statement.startLine, statement.startColumn) == (awaiting.startLine, awaiting.startColumn);
        return sameStatement ? (callerModule, caller.Function.Token, stateMachine, caller) : null;
    }

    /// <summary>Where a suspended async method goes on once what it awaits is done, the IL offset its await resumes at.</summary>
    private static int? ResumeOffset(ModuleInfo module, int moveNext, ICorDebugValue stateMachine) =>
        stateMachine.UnwrapDebugValueToObjectOrNull() is { } machine
            && (ReadInt32Field(machine, "<>1__state") ?? ReadInt32Field(machine, "$State")) is int state and >= 0
            && module.MetadataReader.GetAsyncMethodSteppingInfo(moveNext) is { } awaits && state < awaits.AwaitInfos.Count
            ? (int)awaits.AwaitInfos[state].ResumeOffset
            : null;

    /// <summary>The full names of the thread's exception's type and each type it derives from, as a catch names one;
    /// null where the thread has none.</summary>
    private static HashSet<string>? ThrownTypes(ICorDebugThread thread)
    {
        if (thread.TryGetCurrentException(out ICorDebugValue? exception) is not Cor.S_OK)
        {
            return null;
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        for (ICorDebugType? type = exception.ExactType; type is not null; type = type.Base)
        {
            ICorDebugClass corClass = type.Class;
            names.Add(MetadataTypeNames.Of(corClass.Module.GetMetaDataInterface<IMetaDataImport>(), corClass.Token));
        }
        return names;
    }

    /// <summary>Whether the thread's exception is the one already known to go unhandled (<see cref="EndsUnhandled"/>),
    /// whose stop was told where it was thrown.</summary>
    private bool EndsUnhandledAlready(ICorDebugThread thread)
    {
        try
        {
            return _endsUnhandled is { } known && thread.TryGetCurrentException(out ICorDebugValue? exception) is Cor.S_OK
                && known.Dereference().Address == exception.UnwrapDebugValue().Address;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"Whether thread {thread.Id}'s exception is the one known to go unhandled is unknown: {error.Message}");
            return false;
        }
    }

    /// <summary>Notes the thread's exception as one known to go unhandled, held so its later stops as unhandled are known as
    /// its own however a collection moves it.</summary>
    private void EndsUnhandled(ICorDebugThread thread)
    {
        _endsUnhandled?.TryDispose();
        _endsUnhandled = thread.TryGetCurrentException(out ICorDebugValue? exception) is Cor.S_OK && exception.UnwrapDebugValue() is ICorDebugHeapValue2 thrown
            ? thrown.CreateHandle(CorDebugHandleType.HANDLE_STRONG)
            : null;
    }
}
