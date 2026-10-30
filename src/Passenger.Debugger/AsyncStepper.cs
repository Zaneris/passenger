using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public enum StepType
{
    StepIn,
    StepOver,
    StepOut
}

internal sealed partial class AsyncStepper
{
    private readonly ManagedDebugger _debugger;
    private class AsyncBreakpoint
    {
        public ICorDebugFunctionBreakpoint? Breakpoint;
        public CORDB_ADDRESS ModuleAddress;
        public mdMethodDef MethodToken;
        public uint ILOffset;

        public void Deactivate()
        {
            // A breakpoint the runtime cannot deactivate is one whose module or process has gone, with nothing left to stop.
            Breakpoint?.TryActivate(false);
        }

        public void Dispose()
        {
            Deactivate();
        }
    }

    /// <summary>A step over (or into) an await: from its line on the thread that steps, to the line after it on whichever
    /// thread the method's run goes on.</summary>
    private class AsyncStep
    {
        public int ThreadId;
        public StepType InitialStepType;
        public uint ResumeOffset;

        /// <summary>Where the stepping thread leaves the method at the await, before the awaiter is told to go on with it.</summary>
        public AsyncBreakpoint? Yield;

        /// <summary>Where the method's runs go on after the await, set as the stepping thread leaves it.</summary>
        public AsyncBreakpoint? Resume;

        /// <summary>Which run of the method the step is in; none when it could not be told, and the method's first resume
        /// is then the step's.</summary>
        public RunId? Run;

        public void Dispose()
        {
            Yield?.Dispose();
            Resume?.Dispose();
            Run?.Handle.TryDispose();
        }
    }

    /// <summary>A run of an async method, by a strong handle compared by address at a stop: its state machine where that
    /// is a class (a Debug build's), read with nothing run in the program, else the id its builder gives a debugger.</summary>
    private sealed record RunId(ICorDebugHandleValue Handle, bool IsStateMachine);

    private readonly Dictionary<ICorDebugModule, ModuleInfo> _modules;
    // Every call comes under the engine's lock (a request or a runtime event at a time), so the stepper needs no lock of its own.
    private AsyncStep? _currentAsyncStep;
    private AsyncBreakpoint? _notifyDebuggerBreakpoint;

    public AsyncStepper(Dictionary<ICorDebugModule, ModuleInfo> modules, ManagedDebugger debugger)
    {
        _modules = modules;
        _debugger = debugger;
    }

    /// <summary>
    /// Call SetNotificationForWaitCompletion on the async builder
    /// </summary>
    private async Task<bool> SetNotificationForWaitCompletion(ICorDebugValue builder, ICorDebugThread thread)
    {
        try
        {
            ICorDebugObjectValue objectValue = builder.UnwrapDebugValueToObject();

            ICorDebugEval eval = thread.CreateEval();
            ICorDebugValue boolValue = eval.NewBooleanValue(true);

            ICorDebugFunction? function = _debugger.FindMethodOnType(objectValue.ExactType, "SetNotificationForWaitCompletion", [boolValue], false, false);
            ArgumentNullException.ThrowIfNull(function);

            ICorDebugType[] typeParameterArgs = objectValue.ExactType.TypeParameters;
            ICorDebugValue? result = await eval.CallParameterizedFunctionAsync(
                _debugger.ProcessRuntimeEventsUntilEvalEvent,
                _debugger.EvalStatus,
                function,
                typeParameterArgs.Length,
                typeParameterArgs,
                2,
                [builder, boolValue]
            );
            if (result is not null)
            {
                throw new InvalidOperationException("SetNotificationForWaitCompletion returned a value when void was expected");
            }

            return true;
        }
        catch (Exception error)
        {
            // A call into the program, whose failure (its throw, its abort, the runtime's refusal) leaves the step a plain one.
            _debugger.Log($"The async step out could not ask the builder to tell of its end: {error}");
            return false;
        }
    }

    /// <summary>
    /// Setup breakpoint in Task.NotifyDebuggerOfWaitCompletion method
    /// </summary>
    private bool SetupNotifyDebuggerOfWaitCompletionBreakpoint()
    {
        try
        {
            const string assemblyName = "System.Private.CoreLib.dll";
            const string className = "System.Threading.Tasks.Task";
            const string methodName = "NotifyDebuggerOfWaitCompletion";

            ICorDebugModule? targetModule = null;
            foreach (ModuleInfo module in _modules.Values)
            {
                if (module.Module.Name.EndsWith(assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    targetModule = module.Module;
                    break;
                }
            }

            if (targetModule is null)
            {
                return false;
            }

            IMetaDataImport metadataImport = targetModule.GetMetaDataInterface<IMetaDataImport>();
            mdTypeDef? classDef = metadataImport.FindTypeDefByNameOrNull(className, mdToken.Nil);
            if (classDef is null)
            {
                return false;
            }

            mdMethodDef methodDef = metadataImport.FindMethod(classDef.Value, methodName, 0, 0);
            if (methodDef.IsNil)
            {
                return false;
            }

            ICorDebugFunction function = targetModule.GetFunctionFromToken(methodDef);
            ICorDebugCode ilCode = function.ILCode;
            ICorDebugFunctionBreakpoint breakpoint = ilCode.CreateBreakpoint(0);
            breakpoint.Activate(true);

            _notifyDebuggerBreakpoint = new AsyncBreakpoint
            {
                Breakpoint = breakpoint,
                ModuleAddress = targetModule.BaseAddress,
                MethodToken = methodDef,
                ILOffset = 0
            };

            return true;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
    }

    /// <summary>Sets up an async step where the step is over an await or out of an async method: whether the async
    /// stepper took it, and whether a simple stepper steps too.</summary>
    public async Task<(bool HandledByAsyncStepper, bool? ShouldUseSimpleStepper)> TrySetupAsyncStep(ICorDebugThread thread, StepType stepType)
    {
        try
        {
            ICorDebugFrame? frame = thread.ActiveFrame;
            if (frame is null)
            {
                return (false, null);
            }

            _runtimeStep?.Dispose();
            _runtimeStep = null;
            if (frame is ICorDebugILFrame awaiting && TrySetupRuntimeAsyncStep(thread, awaiting, stepType))
            {
                // A run whose caller waits in a continuation runs to it with no stepper, which would pass its breakpoint by.
                return (true, _runtimeStep!.FrameAwaits);
            }

            ICorDebugFunction function = frame.Function;
            CORDB_ADDRESS moduleAddress = function.Module.BaseAddress;
            mdMethodDef methodToken = function.Token;
            ICorDebugCode ilCode = function.ILCode;
            int methodVersion = ilCode.VersionNumber;

            if (_debugger.FindModule(function.Module) is not { } moduleInfo || moduleInfo.MetadataReader.HasSymbols is false)
            {
                return (false, null);
            }

            ModuleMetadataReader.AsyncMethodSteppingInfo? asyncInfo = moduleInfo.MetadataReader.GetAsyncMethodSteppingInfo(methodToken, methodVersion);
            if (asyncInfo is null)
            {
                return (false, null);
            }

            ICorDebugILFrame? ilFrame = frame as ICorDebugILFrame;

            if (stepType != StepType.StepOut)
            {
                if (ilFrame is not null)
                {
                    (int pnOffset, CorDebugMappingResult pMappingResult) ipResult = ilFrame.IP;
                    int currentOffset = ipResult.pnOffset;
                    CorDebugMappingResult mappingResult = ipResult.pMappingResult;

                    if (mappingResult != CorDebugMappingResult.MAPPING_PROLOG &&
                        mappingResult != CorDebugMappingResult.MAPPING_EPILOG &&
                        currentOffset >= asyncInfo.LastUserCodeIlOffset)
                    {
                        stepType = StepType.StepOut;
                    }
                }
            }

            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;

            if (stepType == StepType.StepOut)
            {
                ICorDebugValue? builder = ilFrame is not null ? GetAsyncBuilder(ilFrame) : null;
                if (builder is not null)
                {
                    string builderType = ManagedDebugger.GetCorDebugTypeFriendlyName(builder.ExactType);
                    if (builderType == "System.Runtime.CompilerServices.AsyncVoidMethodBuilder")
                    {
                        return (false, null);
                    }

                    bool success = await SetNotificationForWaitCompletion(builder, thread);
                    if (success)
                    {
                        bool notifyBpSuccess = SetupNotifyDebuggerOfWaitCompletionBreakpoint();
                        if (notifyBpSuccess)
                        {
                            return (true, false);
                        }
                    }
                }

                return (false, null);
            }

            if (ilFrame is null)
            {
                return (false, null);
            }

            int stepFrom = ilFrame.IP.pnOffset;
            ModuleMetadataReader.AsyncAwaitInfo? awaitInfo = FindNextAwaitInfo(asyncInfo, (uint)stepFrom);
            if (awaitInfo is null)
            {
                return (false, null);
            }

            _currentAsyncStep = new AsyncStep
            {
                ThreadId = thread.Id,
                InitialStepType = stepType,
                ResumeOffset = awaitInfo.ResumeOffset,
                Yield = Arm(ilCode, moduleAddress, methodToken, awaitInfo.YieldOffset),
            };

            // The simple stepper steps on until the yield breakpoint is reached.
            return (true, true);
        }
        catch (Exception)
        {
            throw;
        }
    }

    /// <summary>Handles a breakpoint hit as part of an async step: whether the async stepper took it, and whether the
    /// program stops there (none when the step has ended there, its stop shown).</summary>
    public async Task<(bool HandledByAsyncStepper, bool? ShouldStop)> TryHandleBreakpoint(ICorDebugThread thread, ICorDebugFunctionBreakpoint breakpoint)
    {
        if (_runtimeStep is { } runtime && runtime.Resume.Breakpoint == breakpoint)
        {
            return (true, TakeRuntimeResume(runtime, thread) ? null : false);
        }

        if (_notifyDebuggerBreakpoint is not null &&
            MatchesBreakpoint(breakpoint, _notifyDebuggerBreakpoint, thread))
        {
            _notifyDebuggerBreakpoint?.Dispose();
            _notifyDebuggerBreakpoint = null;

            // The breakpoint's handler steps into the hook, on to the awaiting continuation's next user code.
            return (true, true);
        }

        if (_currentAsyncStep is not { } step)
        {
            return (false, null);
        }
        bool atYield = step.Yield is { } yield && MatchesBreakpoint(breakpoint, yield, thread);
        if (!atYield && (step.Resume is not { } resume || !MatchesBreakpoint(breakpoint, resume, thread)))
        {
            // Another breakpoint ends the async step only if it stops, which the debugger tells once its condition is tested.
            return (false, null);
        }

        ICorDebugILFrame? frame = thread.ActiveFrame as ICorDebugILFrame;
        if (frame is null || frame.IP.pnOffset != (atYield ? step.Yield! : step.Resume!).ILOffset)
        {
            _debugger.Log($"The async step's breakpoint at IL_{(atYield ? step.Yield! : step.Resume!).ILOffset:X4} was met on thread {thread.Id} "
                + $"at {(frame is null ? "no IL frame" : $"IL_{frame.IP.pnOffset:X4}")}: the async step ends");
            step.Dispose();
            _currentAsyncStep = null;
            return (false, null);
        }

        if (atYield)
        {
            // Another run of the method leaving it at the await is not the step's.
            return step.ThreadId == thread.Id ? (true, await HandleYieldBreakpoint(step, frame)) : (false, null);
        }
        await HandleResumeBreakpoint(step, thread, frame);
        return (true, false);
    }

    /// <summary>The stepping thread is leaving the method at the await (Roslyn's yield point comes before the awaiter is
    /// told to go on with it): its stepper goes, and the step waits where the run resumes, its run told first. Never stops,
    /// and never fails: a run not told makes the method's first resume the step's.</summary>
    private async Task<bool> HandleYieldBreakpoint(AsyncStep step, ICorDebugILFrame frame)
    {
        _debugger.EndStepper();
        step.Yield!.Dispose();
        step.Yield = null;
        ICorDebugFunction function = frame.Function;
        step.Run = StateMachineOf(frame) ?? await BuilderRunOf(frame);
        step.Resume = Arm(function.ILCode, function.Module.BaseAddress, function.Token, step.ResumeOffset);
        _debugger.Log($"Thread {step.ThreadId} leaves the method at the await: the step waits where its run resumes, IL_{step.ResumeOffset:X4}, "
            + (step.Run is not { } run ? "the run not told" : run.IsStateMachine ? "the run told by its state machine" : "the run told by its builder"));
        return false;
    }

    /// <summary>A run of the method goes on after the await: when it is the step's, the step goes on from here, on
    /// whichever thread. Another run's is let go on.</summary>
    private async Task HandleResumeBreakpoint(AsyncStep step, ICorDebugThread thread, ICorDebugILFrame frame)
    {
        if (!await IsTheStepsRun(step, thread, frame))
        {
            _debugger.Log($"A run of the method resumes on thread {thread.Id}, not the step's: it runs on");
            return;
        }
        _currentAsyncStep = null;
        step.Dispose();
        _debugger.Log($"The awaited run goes on on thread {thread.Id}{(thread.Id == step.ThreadId ? "" : $", not the stepping thread {step.ThreadId}")}: the step goes on there");
        _debugger.StepOnFromResume(thread, step.InitialStepType);
    }

    /// <summary>Whether a run resuming is the step's: its state machine the step's, compared with nothing run in the
    /// program; the id its builder gives a debugger the same; or the first resume, when the step's run could not be told.</summary>
    private async Task<bool> IsTheStepsRun(AsyncStep step, ICorDebugThread thread, ICorDebugILFrame frame)
    {
        if (step.Run is not { } run)
        {
            return true;
        }
        RunId? resumed = run.IsStateMachine ? StateMachineOf(frame) : await BuilderRunOf(frame);
        if (resumed is null)
        {
            return true;
        }
        try
        {
            return resumed.Handle.Dereference().Address == run.Handle.Dereference().Address;
        }
        catch (COMException error)
        {
            _debugger.Log($"Which run of the method resumed on thread {thread.Id} could not be told: {error.Message}");
            return true;
        }
        finally
        {
            resumed.Handle.TryDispose();
        }
    }

    /// <summary>The run of a frame's async method by its state machine, when that is a class (a Debug build's), as its
    /// <c>this</c>; none for a struct's, whose runs move from the stack to a box at their first await.</summary>
    private RunId? StateMachineOf(ICorDebugILFrame frame)
    {
        try
        {
            return frame.Arguments is [ICorDebugReferenceValue { Type: CorElementType.CLASS, IsNull: false } stateMachine, ..]
                && stateMachine.Dereference() is ICorDebugHeapValue2 heap
                ? new RunId(heap.CreateHandle(CorDebugHandleType.HANDLE_STRONG), IsStateMachine: true)
                : null;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            _debugger.Log($"The async method's state machine could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>The run of a frame's async method by the id its builder gives a debugger, which code run in the program
    /// reads (only that thread runs): none when it could not be read, as when that code waits on a thread held meanwhile.</summary>
    private async Task<RunId?> BuilderRunOf(ICorDebugILFrame frame)
    {
        try
        {
            return await GetAsyncIdReference(frame) is { } id ? new RunId(id, IsStateMachine: false) : null;
        }
        catch (Exception error) when (error is ManagedDebugger.EvalException or COMException or InvalidOperationException)
        {
            _debugger.Log($"Which run of the async method this is could not be read, so its first resume is taken as the step's: {error.Message}");
            return null;
        }
    }

    private static AsyncBreakpoint Arm(ICorDebugCode ilCode, CORDB_ADDRESS module, mdMethodDef method, uint offset)
    {
        ICorDebugFunctionBreakpoint breakpoint = ilCode.CreateBreakpoint((int)offset);
        breakpoint.Activate(true);
        return new AsyncBreakpoint { Breakpoint = breakpoint, ModuleAddress = module, MethodToken = method, ILOffset = offset };
    }

    private ModuleMetadataReader.AsyncAwaitInfo? FindNextAwaitInfo(ModuleMetadataReader.AsyncMethodSteppingInfo asyncInfo, uint currentOffset)
    {
        foreach (ModuleMetadataReader.AsyncAwaitInfo awaitInfo in asyncInfo.AwaitInfos)
        {
            if (currentOffset <= awaitInfo.YieldOffset)
            {
                return awaitInfo;
            }
            if (currentOffset < awaitInfo.ResumeOffset)
            {
                break;
            }
        }
        return null;
    }

    private async Task<ICorDebugHandleValue?> GetAsyncIdReference(ICorDebugILFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ICorDebugValue? builder = GetAsyncBuilder(frame);
        if (builder is null)
        {
            return null;
        }

        ICorDebugHandleValue? objectId = await GetObjectIdForDebugger(builder, frame);
        return objectId;
    }

    private ICorDebugValue? GetAsyncBuilder(ICorDebugILFrame frame)
    {
        try
        {
            ICorDebugFunction function = frame.Function;
            ICorDebugModule module = function.Module;
            mdMethodDef methodToken = function.Token;
            IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

            (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) methodProps = metadataImport.GetMethodProps(methodToken);
            bool isStatic = (methodProps.pdwAttr & CorMethodAttr.mdStatic) != 0;

            if (isStatic)
            {
                return null;
            }

            ICorDebugValue[] arguments = frame.Arguments;
            if (arguments.Length == 0)
            {
                return null;
            }

            ICorDebugValue thisValue = arguments[0];
            ICorDebugReferenceValue? thisRefValue = thisValue as ICorDebugReferenceValue;
            if (thisRefValue is null || thisRefValue.IsNull)
            {
                return null;
            }

            ICorDebugValue thisValueUnwrapped = thisRefValue.Dereference();
            ICorDebugObjectValue? thisObjectValue = thisValueUnwrapped as ICorDebugObjectValue;
            if (thisObjectValue is null)
            {
                return null;
            }

            ICorDebugClass thisClass = thisObjectValue.Class;
            mdFieldDef fieldDef = metadataImport.EnumFieldsWithName(thisClass.Token, "<>t__builder").SingleOrDefault();
            if (fieldDef.IsNil)
            {
                return null;
            }

            ICorDebugValue fieldValue = thisObjectValue.GetFieldValue(thisClass, fieldDef);
            ICorDebugValue fieldValueUnwrapped = fieldValue.UnwrapDebugValue();
            return fieldValueUnwrapped;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return null;
        }
    }

    private async Task<ICorDebugHandleValue?> GetObjectIdForDebugger(ICorDebugValue builder, ICorDebugILFrame frame)
    {

        ICorDebugObjectValue objectValue = builder.UnwrapDebugValueToObject();
        ICorDebugClass @class = objectValue.Class;
        ICorDebugModule module = @class.Module;
        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

        mdProperty? propertyDef = metadataImport.GetPropertyWithName(@class.Token, "ObjectIdForDebugger");
        if (propertyDef is null || propertyDef.Value.IsNil)
        {
            return null;
        }

        (string szProperty, mdTypeDef pClass, CorPropertyAttr pdwPropFlags, nint ppvSig, int pbSig, CorElementType pdwCPlusTypeFlag, nint ppDefaultValue, int pcchDefaultValue, mdMethodDef pmdSetter, mdMethodDef pmdGetter, mdMethodDef rmdOtherMethod, int pcOtherMethod) propertyProps = metadataImport.GetPropertyProps(propertyDef.Value);
        mdMethodDef getMethodDef = propertyProps.pmdGetter;
        if (getMethodDef.IsNil)
        {
            return null;
        }

        ICorDebugFunction getMethod = module.GetFunctionFromToken(getMethodDef);
        ICorDebugEval eval = frame.Chain.Thread.CreateEval();

        ICorDebugValue? result = await eval.CallParameterizedFunctionAsync(
            _debugger.ProcessRuntimeEventsUntilEvalEvent,
            _debugger.EvalStatus,
            getMethod,
            builder.ExactType.TypeParameters.Length,
            builder.ExactType.TypeParameters,
            1,
            [builder]
        );

        if (result is not ICorDebugHandleValue handleValue)
        {
            throw new InvalidOperationException("ObjectIdForDebugger is not a handle value");
        }

        return handleValue;
    }

    private bool MatchesBreakpoint(ICorDebugFunctionBreakpoint breakpoint, AsyncBreakpoint asyncBp, ICorDebugThread thread)
    {
        ICorDebugFrame? frame = thread.ActiveFrame;
        if (frame is null)
        {
            return false;
        }

        ICorDebugFunction function = frame.Function;
        CORDB_ADDRESS moduleAddress = function.Module.BaseAddress;
        mdMethodDef methodToken = function.Token;

        return moduleAddress == asyncBp.ModuleAddress && methodToken == asyncBp.MethodToken && breakpoint == asyncBp.Breakpoint;
    }

    public void ClearActiveAsyncStep()
    {
        _currentAsyncStep?.Dispose();
        _currentAsyncStep = null;
        _runtimeStep?.Dispose();
        _runtimeStep = null;
    }

    /// <summary>
    /// Disable all async stepping and cleanup
    /// </summary>
    public void Disable()
    {
        _currentAsyncStep?.Dispose();
        _currentAsyncStep = null;
        _runtimeStep?.Dispose();
        _runtimeStep = null;
        _notifyDebuggerBreakpoint?.Dispose();
        _notifyDebuggerBreakpoint = null;
    }

    public void Dispose()
    {
        Disable();
    }
}
