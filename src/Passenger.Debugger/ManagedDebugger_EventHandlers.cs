using System.Diagnostics;
using Passenger.Dap;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private void HandleProcessCreated(object? sender, CreateProcessCorDebugManagedCallbackEventArgs createProcessCorDebugManagedCallbackEventArgs)
    {
        _logger?.Invoke("Process created event");
        Continue(clearVariables: false);
    }

    private async Task HandleProcessExited(ExitProcessCorDebugManagedCallbackEventArgs exitProcessCorDebugManagedCallbackEventArgs)
    {
        _logger?.Invoke($"Process exited");
        await ReportExitAsync(await EndedWithAsync().ConfigureAwait(false)).ConfigureAwait(false);
        OnTerminated?.Invoke();
    }

    private void HandleThreadCreated(object? sender, CreateThreadCorDebugManagedCallbackEventArgs createThreadCorDebugManagedCallbackEventArgs)
    {
        ICorDebugThread corThread = createThreadCorDebugManagedCallbackEventArgs.Thread;
        NoteMainThread(corThread);
        _threads[corThread.Id] = new ThreadInfo(corThread);
        if (_dataSlots.Any(slot => slot is not null))
        {
            // A thread made since a data breakpoint was set watches what the others do.
            ApplyDataRegisters(corThread.Id);
        }
        OnThreadStarted?.Invoke(corThread.Id);
        Continue(clearVariables: false);
    }

    /// <summary>Notes the thread running Main. The runtime announces it first, but to a debugger that takes hold once
    /// the program is under way it lists the threads in the order they were made, and Windows makes the finalizer's
    /// before Main's. So where the thread the program was started on is known (<see cref="_initialThreadId"/>, a launch
    /// on Windows), that one is Main's.</summary>
    private void NoteMainThread(ICorDebugThread thread)
    {
        if (_mainThreadId is null || thread.Id == _initialThreadId)
        {
            _mainThreadId = thread.Id;
        }
    }

    private void HandleThreadExited(object? sender, ExitThreadCorDebugManagedCallbackEventArgs exitThreadCorDebugManagedCallbackEventArgs)
    {
        ICorDebugThread corThread = exitThreadCorDebugManagedCallbackEventArgs.Thread;
        _threads.Remove(corThread.Id);
        _exceptionBreakModes.Remove(new ThreadId(corThread.Id));
        OnThreadExited?.Invoke(corThread.Id);
        Continue(clearVariables: false);
    }

    private void HandleModuleLoaded(object? sender, LoadModuleCorDebugManagedCallbackEventArgs loadModuleCorDebugManagedCallbackEventArgs)
    {
        ICorDebugModule corModule = loadModuleCorDebugManagedCallbackEventArgs.Module;
        string modulePath = corModule.Name;
        string moduleName = Path.GetFileName(modulePath);
        CORDB_ADDRESS baseAddress = corModule.BaseAddress;

        _logger?.Invoke($"Module loaded: {modulePath} at 0x{(long)baseAddress:X}");

        ModuleMetadataReader? metadataReader = null;
        bool emitted = corModule.IsDynamic;
        try
        {
            if (emitted)
            {
                metadataReader = ReadEmittedMetadata(corModule);
            }
            else if (corModule.IsInMemory)
            {
                int size = corModule.Size;
                CORDB_ADDRESS baseAddress2 = corModule.BaseAddress;
                (byte[] bytes, _) = (_process ?? throw new InvalidOperationException("A module loaded before its process")).ReadMemory(baseAddress2, size);
                metadataReader = ModuleMetadataReader.TryLoadFromBytes(bytes);
            }
            else
            {
                metadataReader = ModuleMetadataReader.TryLoad(modulePath);
            }
            if (metadataReader is null)
            {
                throw new InvalidOperationException("The module's PE metadata could not be read.");
            }

            _logger?.Invoke(metadataReader.HasSymbols ? $"  Symbols loaded for {moduleName}" : $"  No symbols found for {moduleName}");
        }
        catch (Exception ex) when (Failures.OfRuntime(ex))
        {
            _logger?.Invoke($"  Error loading symbols for {moduleName}: {ex.Message}");
        }

        if (metadataReader is null)
        {
            _logger?.Invoke($"  Module metadata unavailable for {moduleName}");
            Continue(clearVariables: false);
            return;
        }
        if (TryConsumeTransientEvaluationModule(metadataReader.Mvid))
        {
            _logger?.Invoke($"  Ignoring transient evaluation module {metadataReader.Mvid}");
            metadataReader.Dispose();
            Continue(clearVariables: false);
            return;
        }

        // Unoptimized code (a Debug build's, Edit and Continue's) is the user's; emitted code, with no source, never.
        bool isUserCode = !emitted && corModule.JITCompilerFlags is CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION or CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC;
        if (_justMyCode && isUserCode && metadataReader.HasSymbols)
        {
            // The module is the user's, but not the methods its own code says are not: steppers pass over them.
            corModule.SetJMCStatus(true, 0, []);
            foreach (int token in metadataReader.NonUserCodeMethodTokens())
            {
                if (corModule.GetFunctionFromToken(token) is ICorDebugFunction2 function)
                {
                    function.TrySetJMCStatus(false);
                }
            }
        }

        ModuleInfo moduleInfo = new ModuleInfo(corModule, modulePath, metadataReader, isUserCode, emitted ? new CORDB_ADDRESS(_lastEmittedModuleKey--) : null);
        _modules[moduleInfo.BaseAddress] = moduleInfo;
        if (!emitted)
        {
            EnableEditAndContinue(corModule, moduleInfo);
        }
        ModuleSet_Version++;
        TryBindEntryBreakpoint(moduleInfo);

        if (moduleName is "System.Private.CoreLib.dll")
        {
            Passenger.Debugger.ExpressionEvaluator.Cil.CilValue.PointerSize = metadataReader.PointerSize;
            // Evaluations call methods on primitives through their value classes.
            MapRuntimePrimitiveTypesToCorDebugClass(corModule);
            // Made with CoreLib, which loads before any stop.
            RuntimeAssemblyPrimitiveTypeClasses runtimeAssemblyPrimitiveTypeClasses = new RuntimeAssemblyPrimitiveTypeClasses(CorElementToValueClassMap, CorVoidClass, CorDecimalClass);
            _expressionEvaluator = new CilExpressionEvaluator(runtimeAssemblyPrimitiveTypeClasses, this);
        }

        OnModuleLoaded?.Invoke(modulePath, Path.GetFileName(modulePath), modulePath);

        if (metadataReader.HasSymbols)
        {
            TryBindPendingBreakpoints();
            foreach (BreakpointManager.BreakpointInfo breakpoint in _breakpointManager.GetFunctionBreakpoints().Where(b => !b.Refused))
            {
                bool becameVerified = TryBindFunctionBreakpoint(breakpoint, moduleInfo);
                if (becameVerified)
                {
                    OnBreakpointChanged?.Invoke(breakpoint);
                }
            }
        }

        Continue(clearVariables: false);
    }

    /// <summary>A type loaded: one emitted into a module made as the program runs adds to that module's metadata, which is
    /// read again when next needed.</summary>
    private void HandleClassLoaded(LoadClassCorDebugManagedCallbackEventArgs classLoaded)
    {
        try
        {
            if (FindModule(classLoaded.C.Module, reread: false) is { IsDynamic: true } module)
            {
                module.Stale = true;
            }
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            _logger?.Invoke($"A loaded class's module could not be told: {error.Message}");
        }
        Continue(clearVariables: false);
    }

    private void HandleModuleUnloaded(object? sender, UnloadModuleCorDebugManagedCallbackEventArgs unloadModuleEvent)
    {
        ICorDebugModule corModule = unloadModuleEvent.Module;
        if (FindModule(corModule, reread: false) is not { } moduleInfo)
        {
            Continue(clearVariables: false);
            return;
        }
        CORDB_ADDRESS baseAddress = moduleInfo.BaseAddress;
        _modules.Remove(baseAddress);

        _logger?.Invoke($"Module unloaded: {moduleInfo.ModulePath} from 0x{(long)baseAddress:X}");
        moduleInfo.Dispose();
        ModuleSet_Version++;

        foreach (BreakpointManager.BreakpointInfo breakpoint in _breakpointManager.GetAllBreakpoints())
        {
            if (breakpoint.IsFunctionBreakpoint)
            {
                int removedBindingCount = breakpoint.FunctionBindings.RemoveAll(binding => binding.ModuleBaseAddress == baseAddress);
                if (removedBindingCount is 0 || breakpoint.FunctionBindings.Count > 0)
                {
                    continue;
                }

                breakpoint.Verified = false;
                breakpoint.Message = "The breakpoint will not currently be hit. No matching function has been loaded.";
                OnBreakpointChanged?.Invoke(breakpoint);
                continue;
            }

            if (breakpoint.ModuleBaseAddress != baseAddress)
            {
                continue;
            }

            breakpoint.CorBreakpoint = null;
            breakpoint.ResolvedBreakpointFromPdb = null;
            breakpoint.ModuleBaseAddress = null;
            breakpoint.Verified = false;
            breakpoint.Message = "The breakpoint will not currently be hit. No symbols have been loaded for this document.";
            TryBindBreakpoint(breakpoint);
            OnBreakpointChanged?.Invoke(breakpoint);
        }

        OnModuleUnloaded?.Invoke(moduleInfo.ModulePath, moduleInfo.ModuleName, moduleInfo.ModulePath);
        Continue(clearVariables: false);
    }

    private async Task HandleBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs breakpointCorDebugManagedCallbackEventArgs)
    {
        ICorDebugBreakpoint breakpoint = breakpointCorDebugManagedCallbackEventArgs.Breakpoint;
        ArgumentNullException.ThrowIfNull(breakpoint);

        if (PutOffDuringEvaluation(breakpointCorDebugManagedCallbackEventArgs, breakpointCorDebugManagedCallbackEventArgs.Thread))
        {
            return;
        }

        if (breakpoint is ICorDebugFunctionBreakpoint afterCall && WatchedCallOf(afterCall) is { } watched)
        {
            // Right after a call a step over runs: what it returned is read, and the step goes on.
            await TakeWatchedReturn(breakpointCorDebugManagedCallbackEventArgs.Thread, watched);
            Continue(clearVariables: true);
            return;
        }

        if (_smartStep is { } smart && breakpoint is ICorDebugFunctionBreakpoint entered && entered == smart.Breakpoint)
        {
            TakeSmartStepEntry(breakpointCorDebugManagedCallbackEventArgs.Thread, smart);
            return;
        }

        ICorDebugThread corThread = breakpointCorDebugManagedCallbackEventArgs.Thread;
        if (_stepper is { IsActive: false } && _stepping is (int stepping, _))
        {
            if (stepping == corThread.Id)
            {
                // The step is complete and its StepComplete queued behind this, at the same place: the step's stop stands for both.
                Continue(clearVariables: true);
                return;
            }
            PutOffBehindStep(breakpointCorDebugManagedCallbackEventArgs, corThread.Id, stepping);
            return;
        }
        // A step still going is ended only once this breakpoint stops (EndStepForStop), never by one that tests false or logs.

        if (breakpoint is not ICorDebugFunctionBreakpoint functionBreakpoint)
        {
            _logger?.Invoke("Unknown breakpoint type hit");
            Continue(clearVariables: false);
            return;
        }

        if (_entryBreakpoint is { } entryBreakpoint && entryBreakpoint.CorBreakpoint == functionBreakpoint)
        {
            // Remove the one-shot breakpoint unless a user breakpoint shares the same CLR breakpoint.
            if (_breakpointManager.FindByCorBreakpoint(functionBreakpoint) is null)
            {
                functionBreakpoint.TryActivate(false);
            }

            _entryBreakpoint = null;
            _stopAtEntry = false;
            EndStepForStop();
            SourceInfo? sourceInfo = GetSourceInfoAtFrame(corThread.ActiveFrame, _justMyCode is false);
            if (sourceInfo is null)
            {
                ReportStop(corThread.Id, StopReasons.Entry);
            }
            else
            {
                ReportStop(corThread.Id, StopReasons.Entry, sourceInfo.Value.FilePath, sourceInfo.Value.StartLine, sourceInfo.Value.StartColumn, null);
            }

            return;
        }

        if (_asyncStepper is not null)
        {
            // The async step's own breakpoints at an await, where it hands the step to the run's thread, never stop.
            (bool asyncHandled, bool? shouldStop) = await _asyncStepper.TryHandleBreakpoint(corThread, functionBreakpoint);
            if (asyncHandled)
            {
                if (shouldStop is false)
                {
                    Continue(clearVariables: true);
                    return;
                }

                if (_stepper is not null)
                {
                    _stepper.Deactivate();
                    _stepper = null;
                }

                SourceInfo? sourceInfo = GetSourceInfoAtFrame(corThread.ActiveFrame, _justMyCode is false);
                if (sourceInfo is null)
                {
                    // Into Task.NotifyDebuggerOfWaitCompletion, on toward the awaiting caller's next user code.
                    SetupStepper(corThread, StepType.StepIn);
                    Continue(clearVariables: true);
                    return;
                }
            }
        }

        BreakpointManager.BreakpointInfo? managedBreakpoint = _breakpointManager.FindByCorBreakpoint(functionBreakpoint);
        if (managedBreakpoint is null)
        {
            _logger?.Invoke("Hit a breakpoint that has since been replaced - continuing");
            Continue(clearVariables: true);
            return;
        }

        managedBreakpoint.HitCount++;

        if (managedBreakpoint.HitCondition is not null && EvaluateHitCondition(managedBreakpoint.HitCount, managedBreakpoint.HitCondition) is false)
        {
            _logger?.Invoke($"Hit count condition not met: count={managedBreakpoint.HitCount}, condition={managedBreakpoint.HitCondition}");
            Continue(clearVariables: true);
            return;
        }

        if (managedBreakpoint.Condition is { } condition)
        {
            (bool holds, string? error) = await EvaluateBreakpointCondition(corThread, condition);
            if (error is not null)
            {
                string message = $"{Passenger.Dap.BreakpointConditions.NotEvaluated(condition)}, so the breakpoint stopped: {error}";
                managedBreakpoint.Message = message;
                OnBreakpointChanged?.Invoke(managedBreakpoint);
                OnConsole?.Invoke($"{message}\n");
            }
            else if (!holds)
            {
                _logger?.Invoke($"Conditional breakpoint condition not met: {condition}");
                Continue(clearVariables: true);
                return;
            }
        }

        if (managedBreakpoint.LogMessage is { } logMessage)
        {
            (string text, _) = await RenderDebuggerDisplayAsync(logMessage, null, new ThreadId(corThread.Id), new FrameStackDepth(0), 0);
            OnConsole?.Invoke($"{text}\n");
        }
        else if (!managedBreakpoint.Suspend)
        {
            // What a breakpoint that neither stops nor says anything of its own writes.
            OnConsole?.Invoke($"Breakpoint reached at {Path.GetFileName(managedBreakpoint.FilePath)}:{managedBreakpoint.ResolvedBreakpointFromPdb?.StartLine ?? managedBreakpoint.Line}\n");
        }
        if (!managedBreakpoint.Suspend)
        {
            Continue(clearVariables: true);
            return;
        }

        EndStepForStop();
        if (managedBreakpoint.IsFunctionBreakpoint)
        {
            SourceInfo? sourceInfo = GetSourceInfoAtFrame(corThread.ActiveFrame, _justMyCode is false);
            // Told as a breakpoint's stop, not with the protocol's function breakpoint reason.
            if (sourceInfo is null)
            {
                ReportStop(corThread.Id, StopReasons.Breakpoint);
            }
            else
            {
                ReportStop(corThread.Id, StopReasons.Breakpoint, sourceInfo.Value.FilePath, sourceInfo.Value.StartLine, sourceInfo.Value.StartColumn, [managedBreakpoint.Id]);
            }

            return;
        }

        if (managedBreakpoint.ResolvedBreakpointFromPdb is not { } resolvedBreakpoint)
        {
            throw new UnreachableException("Breakpoint was not resolved from PDB - this should never happen, as source breakpoints are only bound to resolved source locations");
        }
        if (resolvedBreakpoint.Placed && corThread.ActiveFrame is { } shortOf && RangeOf(shortOf) is { } shortRange)
        {
            _stoppedShort = (corThread.Id, shortRange, resolvedBreakpoint);
        }

        ReportStop(corThread.Id, StopReasons.Breakpoint, managedBreakpoint.FilePath, resolvedBreakpoint.StartLine, resolvedBreakpoint.StartColumn, [managedBreakpoint.Id]);
    }

    private void TryBindEntryBreakpoint(ModuleInfo moduleInfo)
    {
        if (_stopAtEntry is false || _entryBreakpoint is not null)
        {
            return;
        }

        try
        {
            if (moduleInfo.MetadataReader.ResolveManagedEntryPoint() is not { } entryPoint)
            {
                return;
            }

            ICorDebugFunction function = moduleInfo.Module.GetFunctionFromToken(entryPoint.MethodToken);
            ICorDebugFunctionBreakpoint corBreakpoint = function.ILCode.CreateBreakpoint(entryPoint.ILOffset);
            corBreakpoint.Activate(true);
            _entryBreakpoint = new EntryBreakpoint(corBreakpoint, moduleInfo.BaseAddress, entryPoint.MethodToken, entryPoint.ILOffset);
            _logger?.Invoke($"Entry breakpoint bound in {moduleInfo.ModuleName} at method 0x{entryPoint.MethodToken:X}, IL offset {entryPoint.ILOffset}");
        }
        catch (Exception ex) when (Failures.OfRuntime(ex))
        {
            _logger?.Invoke($"Error binding entry breakpoint in {moduleInfo.ModuleName}: {ex.Message}");
        }
    }

    private async Task HandleStepComplete(object? sender, StepCompleteCorDebugManagedCallbackEventArgs stepCompleteEventArgs)
    {
        ICorDebugThread corThread = stepCompleteEventArgs.Thread;
        bool evaluatedHere = EvalStatus.ThreadId == corThread.Id || EvalStatus.Abandoned.ContainsKey(corThread.Id);
        if (PutOffDuringEvaluation(stepCompleteEventArgs, corThread))
        {
            if (evaluatedHere && stepCompleteEventArgs.Stepper == _stepper)
            {
                // Let go with the evaluated code's events, the step ends: no breakpoint may take it for one whose end is queued.
                _stepper = null;
                TakeBehindStep();
            }
            return;
        }
        TakeBehindStep();
        if (stepCompleteEventArgs.Stepper != _stepper)
        {
            // A step that had completed when a stop (another thread's, a pause) ended it: its end, queued behind, stops nothing.
            _logger?.Invoke($"The end of a step on thread {corThread.Id} that a stop ended meanwhile is let go");
            Continue(clearVariables: false);
            return;
        }
        ICorDebugILFrame ilFrame = (ICorDebugILFrame)corThread.ActiveFrame;
        // An async step's breakpoint at an await, which the step ran to, was not hit: the async step is over.
        _asyncStepper?.ClearActiveAsyncStep();
        ICorDebugStepper stepper = stepCompleteEventArgs.Stepper;
        stepper.Deactivate();
        _stepper = null;
        EndReturnWatch();
        // The statement is left without its Smart Step Into target entered (not called, or called on another thread).
        EndSmartStep();
        if (stepCompleteEventArgs.Reason is CorDebugStepReason.STEP_RETURN)
        {
            // Back in a caller; reading a value type's return runs the program, so the frame is read again.
            await TakeValueReturnedTo(corThread, ilFrame);
            ilFrame = (ICorDebugILFrame)corThread.ActiveFrame;
        }
        ModuleInfo? module = FindModule(ilFrame.Function.Module);
        if (module is not null && module.MetadataReader.IsCurrentSequencePointHidden(ilFrame.Function.Token, ilFrame.IP.pnOffset, ModuleMetadataReader.VersionOf(ilFrame)))
        {
            // Past a hidden sequence point, which would show the line before it as where the step ended.
            SetupStepper(corThread, StepType.StepOver);
            Continue(clearVariables: true);
            return;
        }
        SourceInfo? sourceInfo = module is null ? null : GetSourceInfoAtFrame(ilFrame, _justMyCode is false);
        if (module is null || sourceInfo is null)
        {
            // No line to show (no symbols, code the compiler made, an async state machine's start): the step goes on to one.
            SetupStepper(corThread, StepType.StepIn);
            Continue(clearVariables: true);
            return;
        }
        ModuleMetadataReader metadataReader = module.MetadataReader;

        (int currentIlOffset, int? nextUserCodeIlOffset) = metadataReader.GetFrameCurrentIlOffsetAndNextUserCodeIlOffset(ilFrame);
        if (stepCompleteEventArgs.Reason is CorDebugStepReason.STEP_CALL && currentIlOffset < nextUserCodeIlOffset)
        {
            SetupStepper(corThread, StepType.StepOver);
            Continue(clearVariables: true);
            return;
        }

        if (nextUserCodeIlOffset is null)
        {
            IMetaDataImport metadataImport = ilFrame.Function.Module.GetMetaDataInterface<IMetaDataImport>();
            mdMethodDef mdMethodDef = ilFrame.Function.Token;
            bool methodIsNotDebuggable =
                metadataImport.HasAnyAttribute(mdMethodDef, AttributeConstants.JmcMethodAttributeNames);
            if (methodIsNotDebuggable)
            {
                SetupStepper(corThread, StepType.StepIn);
                Continue(clearVariables: true);
                return;
            }
        }

        string sourceFilePath = sourceInfo.Value.FilePath;
        int line = sourceInfo.Value.StartLine;
        int column = sourceInfo.Value.StartColumn;
        ReportStop(corThread.Id, StopReasons.Step, sourceFilePath, line, column, null);
    }

    /// <summary>A breakpoint that stops ends the step in progress, on its own thread or another: the stepper, an async
    /// step's breakpoints at an await, and the step a remap would make again in the new code. One whose condition is
    /// false, or a tracepoint, leaves it going.</summary>
    private void EndStepForStop()
    {
        _asyncStepper?.Disable();
        _stepAfterEvaluation = null;
        _restepAfterRemap = null;
        EndSmartStep();
        if (_stepper is not null)
        {
            _stepper.Deactivate();
            _stepper = null;
        }
    }

    /// <summary>Another thread's breakpoint the runtime handed over just as a step completed, the step's StepComplete queued
    /// behind it: put off, its thread held at the breakpoint, until the step's end has been handled; then it waits with the
    /// events put off during an evaluation, and is handled as a hit once the program goes on from the step's stop. So both
    /// stop, the step that had completed first, rather than the breakpoint ending a step no longer in progress.</summary>
    private void PutOffBehindStep(CorDebugManagedCallbackEventArgs callbackEvent, int thread, int stepping)
    {
        _logger?.Invoke($"Put off: {callbackEvent.GetType().Name} on thread {thread}, behind the step completed on thread {stepping}");
        _behindStep.Enqueue(callbackEvent);
        Continue(clearVariables: false);
    }

    /// <summary>The events put off behind a step's end (<see cref="PutOffBehindStep"/>), which has come: they wait with those put
    /// off during an evaluation, handed back in order once the program goes on.</summary>
    private void TakeBehindStep()
    {
        while (_behindStep.TryDequeue(out CorDebugManagedCallbackEventArgs? behind))
        {
            _deferredEvents.Enqueue(behind);
        }
    }

    /// <summary>An event that came while an evaluation ran: the evaluated code's own is let go, since an evaluation never
    /// stops part way; another thread's was queued with the event the evaluation began from, and waits, its thread
    /// suspended where the event found it, until the evaluation's own stop is over. True when the event was either.</summary>
    private bool PutOffDuringEvaluation(CorDebugManagedCallbackEventArgs callbackEvent, ICorDebugThread thread)
    {
        if (EvalStatus.Abandoned.ContainsKey(thread.Id))
        {
            // The code of an evaluation given up, which runs on until it returns: it never stops part way either.
            _logger?.Invoke($"Let go: {callbackEvent.GetType().Name} on thread {thread.Id}, in an evaluation given up");
            Continue(clearVariables: false);
            return true;
        }
        if (EvalStatus.ThreadId is not { } evaluating)
        {
            if (!_stopShown)
            {
                return false;
            }
            // On its way as a pause stopped the program: answered, it waits for the continue under the pause's hold.
            _logger?.Invoke($"Put off: {callbackEvent.GetType().Name} on thread {thread.Id}, while a stop is shown");
            _deferredEvents.Enqueue(callbackEvent);
            _process!.Continue(false);
            return true;
        }
        if (thread.Id != evaluating)
        {
            _logger?.Invoke($"Put off: {callbackEvent.GetType().Name} on thread {thread.Id}, during an evaluation on thread {evaluating}");
            _deferredEvents.Enqueue(callbackEvent);
        }
        _process!.Continue(false);
        return true;
    }

    private void HandleBreak(object? sender, BreakCorDebugManagedCallbackEventArgs breakEventArgs)
    {
        ICorDebugThread corThread = breakEventArgs.Thread;
        if (PutOffDuringEvaluation(breakEventArgs, corThread))
        {
            return;
        }
        EndStepForStop();
        ReportStop(corThread.Id, StopReasons.Pause);
    }

    private void HandleException(object? sender, ExceptionCorDebugManagedCallbackEventArgs exceptionEventArgs)
    {
        // ICorDebugManagedCallback2 supplies the exception stage needed for filtering.
        Continue(clearVariables: false);
    }

    private async Task HandleException2(object? sender, Exception2CorDebugManagedCallbackEventArgs exceptionEventArgs)
    {
        ICorDebugThread corThread = exceptionEventArgs.Thread;
        if (EvalStatus.ThreadId == corThread.Id || EvalStatus.Abandoned.ContainsKey(corThread.Id))
        {
            // The evaluated code's own: caught inside it, or the evaluation's result.
            Continue(clearVariables: false);
            return;
        }

        ThreadId threadId = new ThreadId(corThread.Id);
        string exceptionType = GetCurrentExceptionType(corThread);
        bool shouldStop = false;
        ExceptionBreakMode breakMode = ExceptionBreakMode.Unknown;
        StackRange? handler = null;
        // The exception breakpoints whose type and scenario this is, their hit counts and conditions yet to be tried.
        List<ExceptionRule> rules = [];
        // A handler outside the user's code that a step under way is to step past, unless the exception stops.
        ICorDebugFrame? stepPast = null;

        switch (exceptionEventArgs.DwEventType)
        {
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE:
                {
                    bool inUserCode = IsUserCodeFrame(exceptionEventArgs.Frame);
                    ExceptionPropagationState state = new ExceptionPropagationState
                    {
                        HasReachedUserCode = inUserCode,
                        AlwaysStopReported = false,
                        ThrownInUserCode = !_justMyCode || inUserCode,
                    };
                    _exceptionStates[threadId] = state;
                    shouldStop = MatchesExceptionBreakpoint(ExceptionBreakpointFilter.All, exceptionType);
                    state.AlwaysStopReported = shouldStop;
                    breakMode = ExceptionBreakMode.Always;
                    break;
                }
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_USER_FIRST_CHANCE:
                {
                    if (_exceptionStates.TryGetValue(threadId, out ExceptionPropagationState? state) is false)
                    {
                        // Just My Code tells no first chance outside the user's code: this one was thrown there.
                        state = new ExceptionPropagationState
                        {
                            HasReachedUserCode = false,
                            AlwaysStopReported = false,
                            ThrownInUserCode = false,
                        };
                        _exceptionStates[threadId] = state;
                    }
                    state.HasReachedUserCode = true;
                    shouldStop = state.AlwaysStopReported is false && MatchesExceptionBreakpoint(ExceptionBreakpointFilter.All, exceptionType);
                    state.AlwaysStopReported |= shouldStop;
                    breakMode = ExceptionBreakMode.Always;
                    break;
                }
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND:
                {
                    bool handlerIsUserCode = IsUserCodeFrame(exceptionEventArgs.Frame);
                    bool seen = _exceptionStates.TryGetValue(threadId, out ExceptionPropagationState? state);
                    shouldStop = _justMyCode && handlerIsUserCode is false && seen && state!.HasReachedUserCode && MatchesExceptionBreakpoint(ExceptionBreakpointFilter.UserUnhandled, exceptionType);
                    // Without Just My Code every frame is the user's, so an exception is handled in user code or not at all.
                    bool handledInUserCode = !_justMyCode || handlerIsUserCode;
                    if (seen || !_justMyCode)
                    {
                        rules = MatchingRules(exceptionType, state?.ThrownInUserCode ?? true,
                            handledInUserCode ? Passenger.Dap.ExceptionScenarios.HandledInUserCode : Passenger.Dap.ExceptionScenarios.HandledInExternalCode);
                    }
                    _exceptionStates.Remove(threadId);
                    breakMode = shouldStop || !handledInUserCode ? ExceptionBreakMode.UserUnhandled : ExceptionBreakMode.Always;
                    if (_justMyCode && !handlerIsUserCode)
                    {
                        handler = RangeOf(exceptionEventArgs.Frame);
                        stepPast = shouldStop || EvalStatus.IsRunning ? null : exceptionEventArgs.Frame;
                    }
                    if (stepPast is not null && rules.Count == 0)
                    {
                        StepPastHandler(corThread, stepPast);
                        stepPast = null;
                    }
                    break;
                }
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED:
                {
                    bool seen = _exceptionStates.Remove(threadId, out ExceptionPropagationState? state);
                    // A client that gives no exception breakpoints has every unhandled exception stop; one that does, where they say.
                    shouldStop = _exceptionRules is null;
                    rules = MatchingRules(exceptionType, seen ? state!.ThrownInUserCode : !_justMyCode || IsUserCodeFrame(exceptionEventArgs.Frame),
                        Passenger.Dap.ExceptionScenarios.Unhandled);
                    breakMode = ExceptionBreakMode.Unhandled;
                    break;
                }
        }

        if (EvalStatus.IsRunning || _stopShown)
        {
            // Another thread's, queued or come with a pause: decided while its frame is good, its stop put off.
            if (shouldStop || rules.Count > 0)
            {
                _logger?.Invoke($"Put off: a stop at an exception on thread {corThread.Id}, during an evaluation on thread {EvalStatus.ThreadId} or a stop shown");
                _deferredEvents.Enqueue(new DeferredExceptionStop(corThread, breakMode, handler, exceptionType, shouldStop, rules, stepPast));
            }
            _process!.Continue(false);
            return;
        }
        await StopAtExceptionIfHit(new DeferredExceptionStop(corThread, breakMode, handler, exceptionType, shouldStop, rules, stepPast));
    }

    /// <summary>Stops at an exception a filter stops at, or one of its exception breakpoints whose hit count and condition
    /// allow; else lets the program go on, past a handler outside the user's code a step under way would stop in.</summary>
    private async Task StopAtExceptionIfHit(DeferredExceptionStop exception)
    {
        IReadOnlyList<int> hit = exception.Rules.Count == 0 ? [] : await TryExceptionRules(exception.Thread, exception.ExceptionType, exception.Rules);
        if (!exception.Stops && hit.Count == 0)
        {
            if (exception.StepPast is { } handlerFrame && !EvalStatus.IsRunning)
            {
                StepPastHandler(exception.Thread, handlerFrame);
            }
            Continue(clearVariables: true);
            return;
        }
        StopAtException(exception.Thread, exception.BreakMode, exception.Handler, hit);
    }

    private void StopAtException(ICorDebugThread corThread, ExceptionBreakMode breakMode, StackRange? handler = null, IReadOnlyList<int>? hit = null)
    {
        EndStepForStop();
        _exceptionBreakModes[new ThreadId(corThread.Id)] = breakMode;
        _unhandledByUser = breakMode is ExceptionBreakMode.UserUnhandled && handler is { } range ? (corThread.Id, range) : null;
        ReportStop(corThread.Id, StopReasons.Exception, hitBreakpointIds: hit is { Count: > 0 } ? hit : null);
    }

    /// <summary>The exception breakpoints for an exception of this type thrown where it was and handled as it is (or not).</summary>
    private List<ExceptionRule> MatchingRules(string exceptionType, bool thrownInUserCode, Passenger.Dap.ExceptionScenarios handled)
    {
        if (_exceptionRules is not { Count: > 0 } rules)
        {
            return [];
        }
        Passenger.Dap.ExceptionScenarios thrown = thrownInUserCode ? Passenger.Dap.ExceptionScenarios.ThrownInUserCode : Passenger.Dap.ExceptionScenarios.ThrownInExternalCode;
        return [.. rules.Where(rule => rule.Refused is null && rule.Request.Scenarios.HasFlag(thrown) && rule.Request.Scenarios.HasFlag(handled) && rule.IsFor(exceptionType))];
    }

    /// <summary>Tries the exception breakpoints an exception matched as a line breakpoint is tried: its hit count, then its
    /// condition (one that does not evaluate stops, saying why), then its message logged. The ids of those that stop.</summary>
    private async Task<IReadOnlyList<int>> TryExceptionRules(ICorDebugThread thread, string exceptionType, IReadOnlyList<ExceptionRule> rules)
    {
        List<int> stopping = [];
        foreach (ExceptionRule rule in rules)
        {
            ExceptionRuleRequest request = rule.Request;
            rule.HitCount++;
            if (request.HitCondition is { } hits && !EvaluateHitCondition(rule.HitCount, hits))
            {
                _logger?.Invoke($"Exception breakpoint {rule.Id}: hit count not met: count={rule.HitCount}, condition={hits}");
                continue;
            }
            if (request.Condition is { } condition)
            {
                (bool holds, string? error) = await EvaluateBreakpointCondition(thread, condition);
                if (error is not null)
                {
                    OnConsole?.Invoke($"{Passenger.Dap.BreakpointConditions.NotEvaluated(condition)}, so the breakpoint on {exceptionType} stopped: {error}\n");
                }
                else if (!holds)
                {
                    _logger?.Invoke($"Exception breakpoint {rule.Id}: condition not met: {condition}");
                    continue;
                }
            }
            if (request.LogMessage is { } logMessage)
            {
                (string text, _) = await RenderDebuggerDisplayAsync(logMessage, null, new ThreadId(thread.Id), new FrameStackDepth(0), 0);
                OnConsole?.Invoke($"{text}\n");
            }
            else if (!request.Suspend)
            {
                OnConsole?.Invoke($"Exception breakpoint reached: {exceptionType}\n");
            }
            if (request.Suspend)
            {
                stopping.Add(rule.Id);
            }
        }
        return stopping;
    }

    private static string GetCurrentExceptionType(ICorDebugThread thread)
    {
        return thread.TryGetCurrentException(out ICorDebugValue? exception) is Cor.S_OK
            ? GetCorDebugTypeFriendlyName(exception.ExactType)
            : "<unknown exception>";
    }

    private bool IsUserCodeFrame(ICorDebugFrame? frame)
    {
        if (_justMyCode is false)
        {
            return false;
        }

        try
        {
            return frame is not null && FindModule(frame.Function.Module) is { IsUserCode: true }
                && (frame.Function is not ICorDebugFunction2 function || function.TryGetJMCStatus(out bool isJustMyCode) is not Cor.S_OK || isJustMyCode);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
    }

    private bool MatchesExceptionBreakpoint(ExceptionBreakpointFilter filter, string exceptionType)
    {
        return _exceptionBreakpoints.Any(breakpoint => breakpoint.Filter == filter && MatchesCondition(breakpoint.Condition, exceptionType));

        static bool MatchesCondition(string? condition, string type)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return true;
            }

            ReadOnlySpan<char> typeAsSpan = type.AsSpan();
            ReadOnlySpan<char> trimmed = condition.AsSpan().Trim();
            bool exclude = trimmed.StartsWith('!');
            if (exclude)
            {
                trimmed = trimmed[1..];
            }

            bool contains = false;
            foreach (Range range in trimmed.SplitAny([',', ' ', '\t', '\r', '\n']))
            {
                ReadOnlySpan<char> entry = trimmed[range].Trim();
                if (entry.IsEmpty)
                {
                    continue;
                }

                if (entry.Equals(typeAsSpan, StringComparison.Ordinal))
                {
                    contains = true;
                    break;
                }
            }
            return exclude ? !contains : contains;
        }
    }
}
