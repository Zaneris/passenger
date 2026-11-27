using Passenger.Core;
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
                || module.MetadataReader.AsyncCatch(frame.Function.Token, module.MetadataReader.VersionOf(frame)) is not { } caught || caught.Offset != (int)handlerOffset
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
                    _logger?.Invoke($"An exception caught to fault a task on thread {thread.Id} goes on unhandled through {Numbers.Said(followed)} awaiting method(s) to the program's async Main");
                    return ExceptionScenarios.Unhandled;
                }
                if ((AwaiterOf(stateMachine) ?? (running is null ? null : CallerAwaiting(stack, running, module, moveNext)) ?? AwaitingElsewhere(stateMachine, module, moveNext))
                    is not { } next || next.Module.MetadataReader.AsyncCatch(next.MoveNext) is not { } nextCatch)
                {
                    return null;
                }
                int at = next.Running is ICorDebugILFrame caller ? caller.IP.pnOffset : ResumeOffset(next.Module, next.MoveNext, next.StateMachine) ?? -1;
                if (at < 0 || next.Module.MetadataReader.MayCatchAt(next.MoveNext, at, nextCatch.Offset, thrown))
                {
                    return null;
                }
                (module, moveNext, stateMachine, running) = (next.Module, next.MoveNext, next.StateMachine, next.Running);
                stack = next.Stack ?? stack;
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
    private Awaiting? AwaiterOf(ICorDebugValue stateMachine)
    {
        if (GetStateMachineTask(stateMachine)?.UnwrapDebugValueToObjectOrNull() is not { } task
            || GetInstanceFieldValue(task, "m_continuationObject") is not { } continuation
            || FindStateMachine(continuation, 0) is not { } awaiter
            || CreateSyntheticAsyncCallerFrame(awaiter) is not { } method)
        {
            return null;
        }
        return new Awaiting(method.Module, method.MoveNextToken, awaiter, null, null);
    }

    /// <summary>An async method awaiting a task: its module, its MoveNext and state machine, its frame where it is still
    /// to await (on <see cref="Stack"/>, its thread's frames) and null where it is suspended at its await.</summary>
    private sealed record Awaiting(ModuleInfo Module, int MoveNext, ICorDebugValue StateMachine, ICorDebugFrame? Running, List<ICorDebugFrame>? Stack);

    /// <summary>The async method on the thread's stack below one that has not yet yielded, which called it and awaits
    /// the task it returns in the same statement; null where its caller is no async method, or does not await it there.</summary>
    private Awaiting? CallerAwaiting(List<ICorDebugFrame> stack, ICorDebugFrame running, ModuleInfo module, int moveNext)
    {
        if (module.MetadataReader.GetStateMachineKickoffMethodToken(moveNext) is not int kickoff || RangeOf(running) is not { } range)
        {
            return null;
        }
        int at = stack.FindIndex(frame => RangeOf(frame) == range);
        int started = at < 0 ? -1 : stack.FindIndex(at + 1, frame => frame is ICorDebugILFrame il && il.Function.Token == kickoff && FindModule(il.Function.Module) == module);
        return started < 0 || stack.Skip(started + 1).OfType<ICorDebugILFrame>().FirstOrDefault() is not { } caller ? null : AwaitingIn(caller, stack);
    }

    /// <summary>The async method a frame runs, where it stands in the statement of its next await, before the await
    /// resumes: before it yields, or handing what it awaits its continuation; null for a frame of another method, or
    /// standing elsewhere.</summary>
    private Awaiting? AwaitingIn(ICorDebugILFrame caller, List<ICorDebugFrame> stack)
    {
        if (FindModule(caller.Function.Module) is not { } callerModule
            || callerModule.MetadataReader.GetStateMachineKickoffMethodToken(caller.Function.Token) is null
            || callerModule.MetadataReader.GetAsyncMethodSteppingInfo(caller.Function.Token, callerModule.MetadataReader.VersionOf(caller)) is not { } awaits
            || caller.TryGetArgument(0, out ICorDebugValue? stateMachine) < 0 || stateMachine is null)
        {
            return null;
        }
        int call = caller.IP.pnOffset;
        int? version = callerModule.MetadataReader.VersionOf(caller);
        uint? awaited = awaits.AwaitInfos.Where(a => a.ResumeOffset > call).OrderBy(a => a.ResumeOffset).Select(a => a.YieldOffset).Cast<uint?>().FirstOrDefault();
        bool sameStatement = awaited is { } yieldOffset
            && callerModule.MetadataReader.GetSourceLocationForOffset(caller.Function.Token, call, version) is { } statement
            && callerModule.MetadataReader.GetSourceLocationForOffset(caller.Function.Token, (int)yieldOffset, version) is { } awaiting
            && (statement.startLine, statement.startColumn) == (awaiting.startLine, awaiting.startColumn);
        return sameStatement ? new Awaiting(callerModule, caller.Function.Token, stateMachine, caller, stack) : null;
    }

    /// <summary>The async method still to await a task nothing awaits yet, on any thread: one whose statement called the state
    /// machine's method, which ran on another thread from its first await while its caller had yet to hand the task its
    /// continuation. It stands before the statement's await, the task's own first run or an awaiter of it in a frame of
    /// its own or above it, the task called on, or the task as the call it is right after returned it. Null for a task
    /// with a continuation, which says what awaits it, and where no thread holds it so.</summary>
    private Awaiting? AwaitingElsewhere(ICorDebugValue stateMachine, ModuleInfo module, int moveNext)
    {
        if (_process is not { } process || module.MetadataReader.GetStateMachineKickoffMethodToken(moveNext) is not int kickoff
            || GetStateMachineTask(stateMachine)?.UnwrapDebugValueToObjectOrNull() is not { } task
            || GetInstanceFieldValue(task, "m_continuationObject") is not ICorDebugReferenceValue { IsNull: true })
        {
            return null;
        }
        CORDB_ADDRESS awaited = task.Address;
        foreach (ICorDebugThread thread in process.EnumerateThreads())
        {
            if (AwaitingOn(thread, awaited, module, kickoff) is { } found)
            {
                _logger?.Invoke($"An exception's async method's task, with no continuation yet, is still to be awaited by the method its thread {thread.Id} runs");
                return found;
            }
        }
        return null;
    }

    /// <summary>The async method on a thread still to await a task (<see cref="AwaitingElsewhere"/>): the first below the
    /// frames of the task's own method, where it or the frames above it hold the task. Null for a thread whose frames the
    /// runtime does not give.</summary>
    private Awaiting? AwaitingOn(ICorDebugThread thread, CORDB_ADDRESS task, ModuleInfo module, int kickoff)
    {
        try
        {
            List<ICorDebugFrame> stack = [.. FramesOnStack(thread)];
            List<ICorDebugILFrame> above = [];
            foreach (ICorDebugILFrame frame in stack.OfType<ICorDebugILFrame>())
            {
                if (FindModule(frame.Function.Module) is not { } frameModule || frameModule.MetadataReader.GetStateMachineKickoffMethodToken(frame.Function.Token) is null
                    || frame.TryGetArgument(0, out ICorDebugValue? machine) < 0 || machine is null || MadeFor(machine, task))
                {
                    above.Add(frame);
                    continue;
                }
                return CallsIn(frame, frameModule, module, kickoff) && AwaitingIn(frame, stack) is { } awaiting
                    && (above.Any(each => Holds(each, task, calledOn: true)) || Holds(frame, task, calledOn: false)
                        || above.Count == 0 && ReturnedHolds(frame, frameModule, task))
                    ? awaiting
                    : null;
            }
            return null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"Whether thread {thread.Id} is still to await an exception's task is unknown: {error.Message}");
            return null;
        }
    }

    /// <summary>Whether the statement a frame stands in calls a method (a module's, by its token).</summary>
    private bool CallsIn(ICorDebugILFrame frame, ModuleInfo frameModule, ModuleInfo module, int method)
    {
        int token = frame.Function.Token;
        int? version = frameModule.MetadataReader.VersionOf(frame);
        return frameModule.MetadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(token, frame.IP.pnOffset, version) is var (start, end)
            && frameModule.MetadataReader.CallSites(token, start, end > start ? end : int.MaxValue, version)
                .Any(call => call.Kind == ModuleMetadataReader.CallKind.Call && ResolveCalledMethod(frameModule, call.Token) is var (called, calledToken)
                    && called == module && calledToken == method);
    }

    /// <summary>Whether a frame's arguments or locals, as far as it has them, hold what is made for a task
    /// (<see cref="MadeFor"/>), or, <paramref name="calledOn"/>, its first argument is the task.</summary>
    private static bool Holds(ICorDebugILFrame frame, CORDB_ADDRESS task, bool calledOn)
    {
        try
        {
            return calledOn && frame.TryGetArgument(0, out ICorDebugValue? first) >= 0 && first?.UnwrapDebugValueToObjectOrNull()?.Address == task
                || frame.EnumerateArguments().Concat(frame.EnumerateLocalVariables()).Any(value => MadeFor(value, task));
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
    }

    /// <summary>Whether a frame standing right after a call of its statement has the task, or what is made for it, as the
    /// value that call returned.</summary>
    private static bool ReturnedHolds(ICorDebugILFrame frame, ModuleInfo module, CORDB_ADDRESS task)
    {
        int token = frame.Function.Token;
        int ip = frame.IP.pnOffset;
        int? version = module.MetadataReader.VersionOf(frame);
        return module.MetadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(token, ip, version) is var (start, _)
            && module.MetadataReader.CallSites(token, start, ip + 1, version).Any(call => call.Kind == ModuleMetadataReader.CallKind.Call && Read(frame, call.Offset) is { } value
                && (value.UnwrapDebugValueToObjectOrNull()?.Address == task || MadeFor(value, task)));
    }

    /// <summary>Whether a value is made for a task: the state machine whose task it is, or an awaiter of it.</summary>
    private static bool MadeFor(ICorDebugValue value, CORDB_ADDRESS task)
    {
        try
        {
            return value.UnwrapDebugValueToObjectOrNull() is { } held
                && (GetStateMachineTask(value)?.UnwrapDebugValueToObjectOrNull()?.Address == task || GetInstanceFieldValue(held, "m_task")?.UnwrapDebugValueToObjectOrNull()?.Address == task);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
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
