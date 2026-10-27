using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>Steps through the awaits of runtime-async methods, which have no state machine: an await that suspends
/// returns from the method's frame to its caller, and the runtime later calls the method again where it goes on, on
/// whichever thread. The step waits there, at the IL after the await, for its run to go on.</summary>
internal sealed partial class AsyncStepper
{
    /// <summary>A step through an await of a runtime-async method. <see cref="Frame"/> is a frame on the stepping thread: the
    /// awaiting one (<see cref="FrameAwaits"/>), until it leaves at the await, or the one a step leaves whose caller is
    /// suspended, waiting for its run in a continuation.</summary>
    private sealed class RuntimeAsyncStep(ICorDebugThread thread, StepType type, ManagedDebugger.StackRange frame, bool frameAwaits, AsyncBreakpoint resume,
        ICorDebugHandleValue? chain)
    {
        public ICorDebugThread Thread { get; } = thread;
        public StepType Type { get; } = type;
        public ManagedDebugger.StackRange Frame { get; } = frame;
        public bool FrameAwaits { get; } = frameAwaits;

        /// <summary>Where the awaiting method goes on after the await, whether it suspended there or not.</summary>
        public AsyncBreakpoint Resume { get; } = resume;

        /// <summary>Whether the stepping thread has left the awaiting frame at the await, which then suspended.</summary>
        public bool Left { get; set; }

        /// <summary>The task the awaiting run's chain of continuations completes, which dispatches it again; none for a
        /// run whose chain has not suspended yet, the first run to go on then taken for it.</summary>
        public ICorDebugHandleValue? Chain { get; } = chain;

        public void Dispose()
        {
            Resume.Dispose();
            Chain?.TryDispose();
        }
    }

    private RuntimeAsyncStep? _runtimeStep;

    /// <summary>Sets up a step through an await of a runtime-async method: over or into the frame's statement when it
    /// awaits, or out of the frame's method (a step out, a step past its end, or over an await it returns right after) to
    /// the await its caller waits at. False when the step goes through no such await.</summary>
    private bool TrySetupRuntimeAsyncStep(ICorDebugThread thread, ICorDebugILFrame frame, StepType type)
    {
        if (type is not StepType.StepOut && AwaitIn(frame) is { } awaited)
        {
            if (ResumeOf(frame, awaited) is not { } resume)
            {
                return false;
            }
            if (resume != ModuleMetadataReader.Returns)
            {
                return Wait(thread, type, frame, frame, awaited, resume, frameAwaits: true);
            }
        }
        else if (!IsRuntimeAsync(frame) || type is not StepType.StepOut && !LeavesMethod(frame))
        {
            return false;
        }
        // A run not yet suspended has its caller on the stack; a resumed one, in the continuation its dispatcher goes on with.
        if (ManagedDebugger.EnumerateFramesForThread(thread).Skip(1).FirstOrDefault() is ICorDebugILFrame caller && IsRuntimeAsync(caller)
            && AwaitOfCall(caller) is { } called && ResumeOf(caller, called) is int callerResumes and >= 0)
        {
            return Wait(thread, StepType.StepOut, caller, caller, called, callerResumes, frameAwaits: true);
        }
        return WaitingCallerOf(thread, frame) is { } waiting && AwaitIn(waiting) is { } waited && ResumeOf(waiting, waited) is int waitingResumes and >= 0
            && Wait(thread, StepType.StepOut, frame, waiting, waited, waitingResumes, frameAwaits: false);
    }

    /// <summary>Arms the step's breakpoint where the awaiting frame's method goes on after its await.</summary>
    private bool Wait(ICorDebugThread thread, StepType type, ICorDebugFrame frame, ICorDebugILFrame awaiting, ModuleMetadataReader.RuntimeAwait awaited, int resume, bool frameAwaits)
    {
        ICorDebugFunction function = awaiting.Function;
        if (ManagedDebugger.RangeOf(frame) is not { } range)
        {
            return false;
        }
        ICorDebugHandleValue? chain = ChainOf(thread, frame);
        _runtimeStep = new RuntimeAsyncStep(thread, type, range, frameAwaits, Arm(function.ILCode, function.Module.BaseAddress, function.Token, (uint)resume), chain);
        _debugger.Log($"{type} through the runtime-async await at IL_{awaited.Call:X4} of method 0x{(int)function.Token:X} on thread {thread.Id}: "
            + $"its run goes on at IL_{resume:X4}{(frameAwaits ? "" : ", its caller waiting in a continuation")}{(chain is null ? "" : ", its chain's task told")}");
        return true;
    }

    /// <summary>The await a runtime-async frame's statement has yet to go through.</summary>
    private ModuleMetadataReader.RuntimeAwait? AwaitIn(ICorDebugILFrame frame)
    {
        if (_debugger.FindModule(frame.Function.Module) is not { } module || _debugger.StepRangeOf(frame) is not var (_, end))
        {
            return null;
        }
        int ip = frame.IP.pnOffset;
        foreach (ModuleMetadataReader.RuntimeAwait awaited in module.MetadataReader.RuntimeAwaits(frame.Function.Token, ModuleMetadataReader.VersionOf(frame)))
        {
            if (awaited.After > ip && awaited.Call < end)
            {
                return awaited;
            }
        }
        return null;
    }

    /// <summary>The await of the call a runtime-async frame is in, which its callee's run returns to: none for a call it
    /// does not await at once, made through the callee's task-returning thunk, which starts a chain of its own.</summary>
    private ModuleMetadataReader.RuntimeAwait? AwaitOfCall(ICorDebugILFrame frame) =>
        _debugger.FindModule(frame.Function.Module) is { } module
            ? module.MetadataReader.AwaitOfCallAt(frame.Function.Token, ModuleMetadataReader.VersionOf(frame), frame.IP.pnOffset)
            : null;

    /// <summary>Whether a step from a frame's statement leaves its method: no line of it comes after the statement.</summary>
    private bool LeavesMethod(ICorDebugILFrame frame) =>
        _debugger.FindModule(frame.Function.Module) is { } module && _debugger.StepRangeOf(frame) is var (_, end)
        && !module.MetadataReader.VisibleSequencePoints(frame.Function.Token, ModuleMetadataReader.VersionOf(frame)).Any(point => point.Offset >= end);

    /// <summary>Whether a frame's method is compiled with runtime async.</summary>
    private bool IsRuntimeAsync(ICorDebugILFrame frame) =>
        _debugger.FindModule(frame.Function.Module) is { } module && module.MetadataReader.IsRuntimeAsync(frame.Function.Token);

    /// <summary>The frame of the method that waits for a resumed run to end: the first of the continuations the thread's
    /// dispatcher goes on with, as the runtime reads them. None for a run that ends its chain.</summary>
    private ICorDebugILFrame? WaitingCallerOf(ICorDebugThread thread, ICorDebugFrame frame)
    {
        try
        {
            return thread.Process is ICorDebugProcess12 asyncStacks && Dispatching(frame, "NextContinuation") is { } continuation
                && asyncStacks.TryGetAsyncStack(continuation.Value, out ICorDebugStackWalk? walk) is Cor.S_OK && walk is not null
                && walk.TryGetFrame(out ICorDebugFrame? waiting) is Cor.S_OK
                ? waiting as ICorDebugILFrame
                : null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _debugger.Log($"The continuation waiting for the run on thread {thread.Id} could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>The task whose chain of continuations a frame's run is in, by a strong handle: the one the dispatcher below
    /// it on the stack runs, when only runtime-async frames awaiting the call above them lie between. None below any
    /// other, which started the chain on this run (a chain is given its task when it first suspends).</summary>
    private ICorDebugHandleValue? ChainOf(ICorDebugThread thread, ICorDebugFrame frame)
    {
        try
        {
            ManagedDebugger.StackRange? range = ManagedDebugger.RangeOf(frame);
            foreach (ICorDebugFrame below in ManagedDebugger.EnumerateFramesForThread(thread).SkipWhile(each => ManagedDebugger.RangeOf(each) != range).Skip(1))
            {
                if (below is ICorDebugILFrame il && IsRuntimeAsync(il))
                {
                    if (AwaitOfCall(il) is null)
                    {
                        return null;
                    }
                    continue;
                }
                return IsDispatcher(below) && Dispatching(below, "CurrentTask")?.Dereference() is ICorDebugHeapValue2 task
                    ? task.CreateHandle(CorDebugHandleType.HANDLE_STRONG)
                    : null;
            }
            return null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _debugger.Log($"Which chain the run on thread {thread.Id} is in could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>Whether a frame is the runtime's dispatcher of a chain of continuations, which calls each method again
    /// where it goes on.</summary>
    private bool IsDispatcher(ICorDebugFrame frame)
    {
        ICorDebugFunction function = frame.Function;
        return function.Module.Name.EndsWith(CoreLib, StringComparison.OrdinalIgnoreCase)
            && function.Module.GetMetaDataInterface<IMetaDataImport>().GetMethodProps(function.Token).szMethod is "DispatchContinuations" or "InstrumentedDispatchContinuations";
    }

    private const string CoreLib = "System.Private.CoreLib.dll";

    /// <summary>A field of what the thread's innermost dispatcher of continuations is doing (AsyncDispatcherInfo.t_current,
    /// read with a frame of the thread): the task it runs (CurrentTask), or the continuation it goes on with next
    /// (NextContinuation). None with no dispatcher on the thread, or a runtime with none.</summary>
    private ICorDebugReferenceValue? Dispatching(ICorDebugFrame frame, string field)
    {
        if (_modules.Values.FirstOrDefault(module => module.Module.Name.EndsWith(CoreLib, StringComparison.OrdinalIgnoreCase)) is not { } coreLib)
        {
            return null;
        }
        IMetaDataImport metadata = coreLib.Module.GetMetaDataInterface<IMetaDataImport>();
        if (metadata.FindTypeDefByNameOrNull("System.Runtime.CompilerServices.AsyncDispatcherInfo", mdToken.Nil) is not { } infoType)
        {
            return null;
        }
        mdFieldDef current = metadata.EnumFieldsWithName(infoType, "t_current").SingleOrDefault();
        mdFieldDef wanted = metadata.EnumFieldsWithName(infoType, field).SingleOrDefault();
        ICorDebugClass info = coreLib.Module.GetClassFromToken(infoType);
        return !current.IsNil && !wanted.IsNil && info.TryGetStaticFieldValue(current, frame, out ICorDebugValue pointer) is Cor.S_OK
            && pointer is ICorDebugReferenceValue { IsNull: false } dispatcher && dispatcher.Dereference() is ICorDebugObjectValue dispatching
            && dispatching.GetFieldValue(info, wanted) is ICorDebugReferenceValue { IsNull: false } value
            ? value
            : null;
    }

    /// <summary>Where a frame's method goes on after an await (<see cref="ModuleMetadataReader.ResumeAfter"/>): at once when
    /// what it awaited was done, else once the runtime calls it again there.</summary>
    private int? ResumeOf(ICorDebugILFrame frame, ModuleMetadataReader.RuntimeAwait awaited) =>
        _debugger.FindModule(frame.Function.Module) is { } module && MappedOffsets(frame) is { } mapped
            ? module.MetadataReader.ResumeAfter(frame.Function.Token, ModuleMetadataReader.VersionOf(frame), awaited, mapped)
            : null;

    /// <summary>The IL offsets a frame's compiled code maps, where a breakpoint binds; the prolog's, the epilog's and code of
    /// no IL (-2, -3, -1) left out.</summary>
    private static HashSet<int>? MappedOffsets(ICorDebugILFrame frame)
    {
        ICorDebugCode? code = null;
        if ((frame is not ICorDebugNativeFrame native || native.TryGetCode(out code) is not Cor.S_OK) && frame.Function.TryGetNativeCode(out code) is not Cor.S_OK)
        {
            return null;
        }
        if (code is null || code.TryGetILToNativeMapping(0, out uint count, null) is not Cor.S_OK || count == 0)
        {
            return null;
        }
        COR_DEBUG_IL_TO_NATIVE_MAP[] map = new COR_DEBUG_IL_TO_NATIVE_MAP[count];
        return code.TryGetILToNativeMapping(count, out count, map) is Cor.S_OK
            ? [.. map.Take((int)count).Select(entry => (int)entry.ilOffset).Where(offset => offset >= 0)]
            : null;
    }

    /// <summary>Whether a step over from a frame stops wherever the frame returns to, the user's code or not: the awaiting
    /// frame of a step over an await, seen leaving at an await that suspends. Just My Code, the stepper would run on
    /// through the code that called it, and, should that thread call the method again where it goes on, pass by the
    /// step's breakpoint there.</summary>
    public bool SeesFrameLeave(ICorDebugThread thread, ICorDebugFrame frame) =>
        _runtimeStep is { FrameAwaits: true, Left: false, Type: StepType.StepOver } step && thread.Id == step.Thread.Id && ManagedDebugger.RangeOf(frame) == step.Frame;

    /// <summary>The step's stepper has stopped on the stepping thread: true when the step goes on, to wait where its run
    /// goes on, the awaiting frame having left at its await (or, for a step out, the awaiting caller reached).</summary>
    public bool WaitsAtAwait(ICorDebugThread thread)
    {
        if (_runtimeStep is not { FrameAwaits: true } step || thread.Id != step.Thread.Id || step.Left)
        {
            return false;
        }
        bool held = ManagedDebugger.EnumerateFramesForThread(thread).Any(frame => ManagedDebugger.RangeOf(frame) == step.Frame);
        if (step.Type is StepType.StepOut ? ManagedDebugger.RangeOf(thread.ActiveFrame) == step.Frame : !held)
        {
            step.Left = !held;
            _debugger.Log(held ? "The step out is back in the awaiting caller: the step waits where it goes on after the await"
                : $"Thread {thread.Id} leaves the method at the await: the step waits where its run goes on");
            return true;
        }
        // Stopped in the awaiting frame, or in a call it made: the await is not the step's to wait for.
        _runtimeStep = null;
        step.Dispose();
        return false;
    }

    /// <summary>An exception on the stepping thread is caught: when that unwinds the awaiting frame, the step's await
    /// will never go on, and the step is a plain one again.</summary>
    public void CaughtOn(ICorDebugThread thread, ICorDebugFrame handler)
    {
        if (_runtimeStep is not { Left: false } step || thread.Id != step.Thread.Id || ManagedDebugger.RangeOf(handler) is not { } caught)
        {
            return;
        }
        List<ManagedDebugger.StackRange?> frames = [.. ManagedDebugger.EnumerateFramesForThread(thread).Select(ManagedDebugger.RangeOf)];
        int awaiting = frames.IndexOf(step.Frame);
        if (awaiting >= 0 && frames.IndexOf(caught) > awaiting)
        {
            _debugger.Log("An exception unwinds the step's frame: the step through its await ends");
            _runtimeStep = null;
            step.Dispose();
        }
    }

    /// <summary>Whether a thread still holds a frame; false for one that has ended.</summary>
    private static bool Holds(ICorDebugThread thread, ManagedDebugger.StackRange frame)
    {
        try
        {
            return ManagedDebugger.EnumerateFramesForThread(thread).Any(each => ManagedDebugger.RangeOf(each) == frame);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
    }

    /// <summary>Whether the run going on on a thread is in the step's chain, its dispatcher running the step's task; true
    /// when the step's chain or the thread's could not be told.</summary>
    private bool InChain(RuntimeAsyncStep step, ICorDebugThread thread)
    {
        try
        {
            return step.Chain is not { } chain || Dispatching(thread.ActiveFrame, "CurrentTask") is not { } running || running.Value == chain.Value;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _debugger.Log($"Which chain the run on thread {thread.Id} is in could not be read: {error.Message}");
            return true;
        }
    }

    /// <summary>The awaiting method goes on after the await on a thread. In the awaiting frame itself, what it awaited was
    /// done at once: a step over or into goes on as it was, a step out on to the caller's next line. Elsewhere, once the
    /// step's frame has left and in the step's chain, it is the step's run going on, which the step goes on from on this
    /// thread; else it is another run's.</summary>
    private void TakeRuntimeResume(RuntimeAsyncStep step, ICorDebugThread thread)
    {
        bool sameFrame = step is { FrameAwaits: true, Left: false } && thread.Id == step.Thread.Id && ManagedDebugger.RangeOf(thread.ActiveFrame) == step.Frame;
        if (!sameFrame && ((!step.Left && Holds(step.Thread, step.Frame)) || !InChain(step, thread)))
        {
            _debugger.Log($"Another run of the method goes on after the await on thread {thread.Id}: it runs on");
            return;
        }
        _runtimeStep = null;
        step.Dispose();
        if (sameFrame && step.Type is not StepType.StepOut)
        {
            _debugger.Log("The await was done at once: the step goes on");
            return;
        }
        _debugger.Log($"The awaiting method goes on after the await on thread {thread.Id}: the step goes on there");
        _debugger.StepOnFromResume(thread, step.Type is StepType.StepOut ? StepType.StepOver : step.Type);
    }
}
