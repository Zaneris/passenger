using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public enum StepType
{
    StepIn,
    StepOver,
    StepOut
}

internal sealed class AsyncStepper
{
    private readonly ManagedDebugger _debugger;
    private enum AsyncStepStatus
    {
        YieldBreakpoint,
        ResumeBreakpoint
    }

    private class AsyncBreakpoint
    {
        public ICorDebugFunctionBreakpoint? Breakpoint;
        public CORDB_ADDRESS ModuleAddress;
        public mdMethodDef MethodToken;
        public uint ILOffset;

        public void Deactivate()
        {
            try
            {
                Breakpoint?.Activate(false);
            }
            catch
            {
                // Ignore deactivation errors
            }
        }

        public void Dispose()
        {
            Deactivate();
        }
    }

    private class AsyncStep
    {
        public int ThreadId;
        public StepType InitialStepType;
        public uint ResumeOffset;
        public AsyncStepStatus Status;
        public AsyncBreakpoint? Breakpoint;
        public ICorDebugHandleValue? AsyncIdHandle; // Strong handle to builder's ObjectIdForDebugger

        public void Dispose()
        {
            Breakpoint?.Dispose();
            try
            {
                AsyncIdHandle?.Dispose();
            }
            catch
            {
                // Ignore handle cleanup errors
            }
        }
    }

    private readonly Dictionary<CORDB_ADDRESS, ModuleInfo> _modules;
    // Every call comes under the engine's lock (a request or a runtime event at a time), so the stepper needs no lock of its own.
    private AsyncStep? _currentAsyncStep;
    private AsyncBreakpoint? _notifyDebuggerBreakpoint;

    /// <summary>The async step's ownership and phase, for correlating callbacks with a simple step's completion.</summary>
    public string State => _currentAsyncStep is { } step ? $"{step.Status} from thread {step.ThreadId}, resume IL_{step.ResumeOffset:X4}" : "none";

    public AsyncStepper(Dictionary<CORDB_ADDRESS, ModuleInfo> modules, ManagedDebugger debugger)
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

            // Find SetNotificationForWaitCompletion method
            ICorDebugFunction? function = _debugger.FindMethodOnType(objectValue.ExactType, "SetNotificationForWaitCompletion", [boolValue], false, false);
            ArgumentNullException.ThrowIfNull(function);

            // Call builder.SetNotificationForWaitCompletion(true)
            ICorDebugType[] typeParameterArgs = objectValue.ExactType.TypeParameters;
            // result should be null, as SetNotificationForWaitCompletion returns void
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
        catch (Exception)
        {
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

            // Find the module
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

            // TODO: This doesn't need to be looked up every time
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
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Try to set up async stepping. Returns true if async stepping was initiated.
    /// </summary>
    /// <param name="thread">Thread to step on</param>
    /// <param name="stepType">Type of step</param>
    /// <param name="shouldUseSimpleStepper">Output: whether to use simple stepper</param>
    /// <returns>True if async stepping was initiated, false otherwise</returns>
    public async Task<(bool HandledByAsyncStepper, bool? ShouldUseSimpleStepper)> TrySetupAsyncStep(ICorDebugThread thread, StepType stepType)
    {
        try
        {
            ICorDebugFrame? frame = thread.ActiveFrame;
            if (frame is null)
            {
                return (false, null);
            }

            ICorDebugFunction function = frame.Function;
            CORDB_ADDRESS moduleAddress = function.Module.BaseAddress;
            mdMethodDef methodToken = function.Token;
            ICorDebugCode ilCode = function.ILCode;
            int methodVersion = ilCode.VersionNumber;

            // Check if module has symbols
            if (_debugger.FindModule(function.Module) is not { } moduleInfo || moduleInfo.MetadataReader.HasSymbols is false)
            {
                return (false, null);
            }

            // Check if method has async stepping info
            ModuleMetadataReader.AsyncMethodSteppingInfo? asyncInfo = moduleInfo.MetadataReader.GetAsyncMethodSteppingInfo(methodToken, methodVersion);
            if (asyncInfo is null)
            {
                return (false, null);
            }

            ICorDebugILFrame? ilFrame = frame as ICorDebugILFrame;

            // Check if we're at the end of an async method and need step-out behavior
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
                        // At end of async method with await blocks - switch to step-out behavior
                        stepType = StepType.StepOut;
                    }
                }
            }

            // Clean up any existing async step
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;

            if (stepType == StepType.StepOut)
            {
                // For step-out in async method with await, check if we need NotifyDebuggerOfWaitCompletion
                ICorDebugValue? builder = ilFrame is not null ? GetAsyncBuilder(ilFrame) : null;
                if (builder is not null)
                {
                    // Check if builder is AsyncVoidMethodBuilder
                    string builderType = ManagedDebugger.GetCorDebugTypeFriendlyName(builder.ExactType);
                    if (builderType == "System.Runtime.CompilerServices.AsyncVoidMethodBuilder")
                    {
                        // async void method - use normal step-out
                        return (false, null);
                    }

                    // Not async void - use NotifyDebuggerOfWaitCompletion magic
                    bool success = await SetNotificationForWaitCompletion(builder, thread);
                    if (success)
                    {
                        // Setup breakpoint in Task.NotifyDebuggerOfWaitCompletion
                        bool notifyBpSuccess = SetupNotifyDebuggerOfWaitCompletionBreakpoint();
                        if (notifyBpSuccess)
                        {
                            // Async step-out handled - no need for stepper
                            return (true, false);
                        }
                    }
                }

                // Fall back to normal step-out
                return (false, null);
            }

            // Find next await block after current offset
            if (ilFrame is null)
            {
                return (false, null);
            }

            int stepFrom = ilFrame.IP.pnOffset;
            ModuleMetadataReader.AsyncAwaitInfo? awaitInfo = FindNextAwaitInfo(asyncInfo, (uint)stepFrom);
            if (awaitInfo is null)
            {
                // No more await blocks - use simple stepper
                return (false, null);
            }

            // Create yield breakpoint
            ICorDebugFunctionBreakpoint yieldBreakpoint = ilCode.CreateBreakpoint((int)awaitInfo.YieldOffset);
            yieldBreakpoint.Activate(true);

            _currentAsyncStep = new AsyncStep
            {
                ThreadId = thread.Id,
                InitialStepType = stepType,
                ResumeOffset = awaitInfo.ResumeOffset,
                Status = AsyncStepStatus.YieldBreakpoint,
                Breakpoint = new AsyncBreakpoint
                {
                    Breakpoint = yieldBreakpoint,
                    ModuleAddress = moduleAddress,
                    MethodToken = methodToken,
                    ILOffset = awaitInfo.YieldOffset
                }
            };

            // Don't set shouldUseSimpleStepper to false - the simple stepper should be created
            // to handle stepping until the yield breakpoint is reached
            return (true, true);
        }
        catch (Exception)
        {
            // If anything goes wrong, fall back to simple stepper, TODO remove this
            throw;
            //return (false, null);
        }
    }

    /// <summary>
    /// Try to handle a breakpoint hit as part of async stepping.
    /// </summary>
    /// <param name="thread">Thread that hit the breakpoint</param>
    /// <param name="breakpoint">Breakpoint that was hit</param>
    /// <param name="shouldStop">Output: whether execution should stop</param>
    /// <returns>True if breakpoint was handled by async stepper</returns>
    public async Task<(bool HandledByAsyncStepper, bool? ShouldStop)> TryHandleBreakpoint(ICorDebugThread thread, ICorDebugFunctionBreakpoint breakpoint)
    {
        // Check if it's our NotifyDebuggerOfWaitCompletion breakpoint
        if (_notifyDebuggerBreakpoint is not null &&
            MatchesBreakpoint(breakpoint, _notifyDebuggerBreakpoint, thread))
        {
            // The task-completion hook was hit during an async step-out operation.
            _notifyDebuggerBreakpoint?.Dispose();
            _notifyDebuggerBreakpoint = null;

            // Let the breakpoint handler step into the runtime hook to reach
            // the awaiting continuation's next user-code location.
            return (true, true);
        }

        // Check if we have an active async step
        if (_currentAsyncStep is null)
        {
            return (false, null);
        }

        // Check if breakpoint matches our async step
        if (!MatchesBreakpoint(breakpoint, _currentAsyncStep.Breakpoint!, thread))
        {
            // Another breakpoint ends the async step only if it stops, which the debugger tells once its condition is tested.
            return (false, null);
        }

        // Check if IP matches expected offset
        ICorDebugILFrame? frame = thread.ActiveFrame as ICorDebugILFrame;
        if (frame is null)
        {
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            return (false, null);
        }

        (int pnOffset, CorDebugMappingResult pMappingResult) ipResult = frame.IP;
        if (ipResult.pnOffset != _currentAsyncStep.Breakpoint!.ILOffset)
        {
            // Wrong offset - cancel async stepping
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            return (false, null);
        }

        if (_currentAsyncStep.Status == AsyncStepStatus.YieldBreakpoint)
        {
            if (_currentAsyncStep.ThreadId != thread.Id)
            {
                return (false, null);
            }
            // Yield breakpoint hit - switch to resume breakpoint
            return await HandleYieldBreakpoint(thread, frame);
        }
        else if (_currentAsyncStep.Status == AsyncStepStatus.ResumeBreakpoint)
        {
            // Resume breakpoint hit - check if we should stop
            return await HandleResumeBreakpoint(thread);
        }

        return (false, null);
    }

    private async Task<(bool HandledByAsyncStepper, bool? ShouldStop)> HandleYieldBreakpoint(ICorDebugThread thread, ICorDebugILFrame frame)
    {
        // Disable all simple steppers when we hit the yield breakpoint
        DisableAllSimpleSteppers(thread.Process);

        // Get async state machine ID for parallel execution tracking
        ICorDebugFunction function = frame.Function;
        ICorDebugCode ilCode = function.ILCode;
        ICorDebugHandleValue? asyncIdHandleValue = await GetAsyncIdReference(frame);
        ArgumentNullException.ThrowIfNull(asyncIdHandleValue);
        _currentAsyncStep!.AsyncIdHandle = asyncIdHandleValue;

        // Create resume breakpoint
        ICorDebugFunctionBreakpoint resumeBreakpoint = ilCode.CreateBreakpoint((int)_currentAsyncStep!.ResumeOffset);
        resumeBreakpoint.Activate(true);

        // Deactivate yield breakpoint
        _currentAsyncStep!.Breakpoint?.Deactivate();

        // Update state
        _currentAsyncStep!.Breakpoint = new AsyncBreakpoint
        {
            Breakpoint = resumeBreakpoint,
            ModuleAddress = function.Module.BaseAddress,
            MethodToken = function.Token,
            ILOffset = _currentAsyncStep!.ResumeOffset
        };
        _currentAsyncStep!.Status = AsyncStepStatus.ResumeBreakpoint;

        // Continue execution
        return (true, false);
    }

    private async Task<(bool HandledByAsyncStepper, bool? ShouldStop)> HandleResumeBreakpoint(ICorDebugThread thread)
    {
        // Check if this is the same thread
        if (_currentAsyncStep!.ThreadId == thread.Id)
        {
            // Same thread - set up stepper and clear async step
            _debugger.SetupStepper(thread, _currentAsyncStep.InitialStepType);
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
        }
        // Different thread - check async ID
        else if (_currentAsyncStep!.AsyncIdHandle is not null)
        {
            ICorDebugHandleValue? currentAsyncId = await GetAsyncIdReference((ICorDebugILFrame)thread.ActiveFrame);
            if (currentAsyncId is not null)
            {
                CORDB_ADDRESS currentAddress = currentAsyncId.Dereference().Address;
                ICorDebugValue dereferencedHandle = _currentAsyncStep!.AsyncIdHandle!.Dereference();
                CORDB_ADDRESS storedAddress = dereferencedHandle.Address;

                if (currentAddress == storedAddress || currentAddress == 0 || storedAddress == 0)
                {
                    // Same async instance - set up stepper and clear async step
                    ICorDebugStepper stepper = _debugger.SetupStepper(thread, _currentAsyncStep.InitialStepType);
                    _currentAsyncStep?.Dispose();
                    _currentAsyncStep = null;
                }
            }
        }

        return (true, false);
    }

    private ModuleMetadataReader.AsyncAwaitInfo? FindNextAwaitInfo(ModuleMetadataReader.AsyncMethodSteppingInfo asyncInfo, uint currentOffset)
    {
        foreach (ModuleMetadataReader.AsyncAwaitInfo awaitInfo in asyncInfo.AwaitInfos)
        {
            if (currentOffset <= awaitInfo.YieldOffset)
            {
                return awaitInfo;
            }
            // Stop search if we're inside an await block
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

            // Get 'this' parameter
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
        catch (Exception)
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

        // Call ObjectIdForDebugger getter
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

    /// <summary>
    /// Disable all simple steppers across all app domains
    /// </summary>
    private void DisableAllSimpleSteppers(ICorDebugProcess process)
    {
        IEnumerable<ICorDebugAppDomain> appDomains = process.EnumerateAppDomains();
        foreach (ICorDebugAppDomain appDomain in appDomains)
        {
            IEnumerable<ICorDebugStepper> steppers = appDomain.EnumerateSteppers();
            foreach (ICorDebugStepper stepper in steppers)
            {
                stepper.Deactivate();
            }
        }
    }

    public void ClearActiveAsyncStep()
    {
        _currentAsyncStep?.Dispose();
        _currentAsyncStep = null;
    }

    /// <summary>
    /// Disable all async stepping and cleanup
    /// </summary>
    public void Disable()
    {
        _currentAsyncStep?.Dispose();
        _currentAsyncStep = null;
        _notifyDebuggerBreakpoint?.Dispose();
        _notifyDebuggerBreakpoint = null;
    }

    public void Dispose()
    {
        Disable();
    }
}
