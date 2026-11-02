using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Dap;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models;
using Passenger.Debugger.Models.Response;
using ZLinq;

namespace Passenger.Debugger;

public record BreakpointRequest(int Line, string? Condition = null, string? HitCondition = null, int? Column = null, string? LogMessage = null, bool? Suspend = null);
public record FunctionBreakpointRequest(string Name, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool? Suspend = null);
public record ExceptionBreakpointRequest(ExceptionBreakpointFilter Filter, string? Condition = null);
public enum ExceptionBreakpointFilter { All, UserUnhandled }

/// <summary>A CLR exception breakpoint: the exception types it is for (none: every exception; with
/// <paramref name="Excluded"/>, every one but those), the scenarios it stops in, and a condition, hit count and log
/// message as a line breakpoint has. A type given without a namespace is any type of that name.</summary>
public sealed record ExceptionRuleRequest(
    IReadOnlyList<string> Types, bool Excluded, Passenger.Dap.ExceptionScenarios Scenarios, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool Suspend = true)
{
    public bool Equals(ExceptionRuleRequest? other) => other is not null && Types.SequenceEqual(other.Types) && Excluded == other.Excluded && Scenarios == other.Scenarios
        && Condition == other.Condition && HitCondition == other.HitCondition && LogMessage == other.LogMessage && Suspend == other.Suspend;

    public override int GetHashCode() => HashCode.Combine(string.Join(',', Types), Excluded, Scenarios, Condition, HitCondition, LogMessage, Suspend);
}

/// <summary>An exception breakpoint as the engine keeps it: its request, its id, its hits, and whether it was refused.</summary>
public sealed class ExceptionRule(int id, ExceptionRuleRequest request) : IHitBreakpoint
{
    public int Id { get; } = id;
    public ExceptionRuleRequest Request { get; } = request;
    public int HitCount { get; set; }

    /// <summary>Why the debugger will not take it as it is (a hit count or condition it cannot read); it never stops.</summary>
    public string? Refused { get; init; }

    /// <summary>The debugger's word on it since it was set: a condition a hit could not tell.</summary>
    public string? Message { get; set; }

    public string? Condition => Request.Condition;
    public string? HitCondition => Request.HitCondition;
    public string? LogMessage => Request.LogMessage;
    public bool Suspend => Request.Suspend;

    public bool IsFor(string exceptionType)
    {
        if (Request.Types.Count == 0)
        {
            return !Request.Excluded;
        }
        string simple = exceptionType[(exceptionType.LastIndexOf('.') + 1)..];
        bool named = Request.Types.Any(type => type == exceptionType || (!type.Contains('.') && type == simple));
        return named != Request.Excluded;
    }
}

public partial class ManagedDebugger
{
    private LaunchInfo? _pendingLaunchInfo;

    /// <summary>The program to launch, started by configurationDone once the client's breakpoints are set.</summary>
    public void Launch(LaunchInfo launchInfo, bool justMyCode)
    {
        EnsureNoProcessBeingDebugged();
        _logger?.Invoke($"Launching program: {launchInfo.Program} {string.Join(' ', launchInfo.Arguments)}");
        _justMyCode = justMyCode;
        _stopAtEntry = launchInfo.StopAtEntry;
        _hotReload = launchInfo.HotReload;
        _pendingLaunchInfo = launchInfo;
        _launched = true;
    }

    /// <summary>A program named without a folder, found on the PATH as a shell would find it.</summary>
    private static string Resolve(string program)
    {
        if (program.Contains(Path.DirectorySeparatorChar) || Environment.ProcessPath is not { } self)
        {
            return program;
        }
        // The adapter runs under the very dotnet that runs a .dll it launches.
        if (program == "dotnet" && Path.GetFileNameWithoutExtension(self) == "dotnet")
        {
            return self;
        }
        return Core.Programs.OnPath(program) ?? program;
    }

    /// <summary>Starts the program held before its first instruction, on every system alike (<see cref="SuspendedLaunch"/>),
    /// and takes hold of it as its runtime starts. A failure answers configurationDone saying which step failed.</summary>
    private async Task PerformLaunch()
    {
        if (_pendingLaunchInfo is null)
        {
            _logger?.Invoke("No pending launch to perform");
            return;
        }

        LaunchInfo launchInfo = _pendingLaunchInfo;
        _pendingLaunchInfo = null;

        _exitReported = 0;
        _exitTold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SuspendedLaunch launch;
        try
        {
            launch = SuspendedLaunch.Start(Resolve(launchInfo.Program), launchInfo.Arguments, launchInfo.Cwd ?? Environment.CurrentDirectory, launchInfo.Env);
        }
        catch (Exception error)
        {
            _logger?.Invoke($"Starting {launchInfo.Program} failed: {error}");
            throw new InvalidOperationException($"Could not start {launchInfo.Program}: {error.Message}", error);
        }
        _launch = launch;
        _initialThreadId = launch.InitialThreadId;
        _launchOutput = Task.WhenAll(Forward(launch.Output, isError: false), Forward(launch.Error, isError: true));
        // Not held: it waits for the program to end.
        Task<int?> launchExit = Core.BackgroundWork.Unheld(launch.WaitForExit);
        _launchExit = launchExit;
        // Told as the program goes, not when the runtime's word of it is handled, which mscordbi may lose tearing down.
        Core.UnawaitedWork.Observe(launchExit.ContinueWith(ended => ReportExitAsync(ended.Result), TaskScheduler.Default).Unwrap());
        int processId = launch.ProcessId;

        _logger?.Invoke($"Process created suspended with PID: {processId}");

        _exited.Reset();
        try
        {
            (_corDebug, _process) = await ClrDebugExtensions.Automatic(processId, _ =>
            {
                launch.Resume();
                return Task.CompletedTask;
            }, corDebug =>
            {
                corDebug.Initialize();
                corDebug.SetManagedHandler(_callbacks);
                return corDebug.DebugActiveProcess(processId, false);
            }, () => launchExit.IsCompleted, _ending.Token, _logger);
        }
        catch (Exception error)
        {
            _logger?.Invoke($"Taking hold of process {processId} failed: {error}");
            // A program that ended meanwhile is why, whatever the debugger was refused on the way.
            await Task.WhenAny(launchExit, Task.Delay(TimeSpan.FromMilliseconds(500)));
            string ended = launchExit.IsCompletedSuccessfully && launchExit.Result is int code ? $" (the program had ended, with exit code {code})" : "";
            // A program the debugger could not take hold of (not .NET, or failed first) is not left behind, held.
            launch.Kill();
            throw new InvalidOperationException($"Could not take hold of process {processId} as it started: {error.Message}{ended}", error);
        }
        _isAttached = true;
        ConfigureExceptionCallbacks();

        _logger?.Invoke($"Successfully attached to process: {processId}");
        OnProcessStarted?.Invoke(processId, launchInfo.Program);
        SendAllBreakpointEvents();
        return;

        // A program's output as it comes, a prompt with no line end included; not held, as it lasts as the program does.
        Task Forward(Stream stream, bool isError) => Core.BackgroundWork.Unheld(async () =>
        {
            try
            {
                using StreamReader reader = new(stream, Encoding.UTF8);
                char[] buffer = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    Interlocked.Exchange(ref _lastOutputAt, Stopwatch.GetTimestamp());
                    InvokeOnOutputWithTryCatch(new string(buffer, 0, read), isError);
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
        });

        // The DataReceived callbacks run on background threads - a throwing subscriber (e.g. a disposed protocol layer) must not crash the adapter
        void InvokeOnOutputWithTryCatch(string output, bool isError)
        {
            try
            {
                OnOutput?.Invoke(output, isError);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error forwarding debuggee output: {ex}");
            }
        }
    }

    public bool RemoveBreakpoint(int id)
    {
        _logger?.Invoke($"RemoveBreakpoint: {id}");
        BreakpointManager.BreakpointInfo? bp = _breakpointManager.GetBreakpoint(id);
        if (bp is null)
        {
            return false;
        }

        LetGo(bp);
        return _breakpointManager.RemoveBreakpoint(id);
    }

    /// <summary>A breakpoint's bindings let go, as it is removed.</summary>
    private void LetGo(BreakpointManager.BreakpointInfo bp)
    {
        foreach (BreakpointManager.BreakpointBinding binding in bp.Bindings)
        {
            if (binding.CorBreakpoint.TryActivate(false) is int deactivated and < 0)
            {
                _logger?.Invoke($"Error deactivating breakpoint {bp.Id}: 0x{deactivated:X8}");
            }
        }
    }

    /// <summary>The process to attach to, taken hold of by configurationDone once the client's breakpoints are set;
    /// with <paramref name="hotReload"/> (a program started held, DOTNET_MODIFIABLE_ASSEMBLIES=debug set) the user's
    /// modules that load after the attach get Edit and Continue.</summary>
    public void Attach(int processId, bool justMyCode, bool hotReload = false)
    {
        EnsureNoProcessBeingDebugged();
        _logger?.Invoke($"Storing attach target: {processId}");
        _justMyCode = justMyCode;
        _stopAtEntry = false;
        _hotReload = hotReload;
        _pendingAttachProcessId = processId;
        Volatile.Write(ref _heldTarget, processId);
        _launched = false;
    }

    /// <summary>Lets go the runtime of a process an attach was to take hold of and did not (the session ended first, or the
    /// attach failed), should it be held at its start: it runs on undebugged rather than wait for good for a debugger that
    /// is not coming. A runtime already running takes the word as done; one still starting is waited for, within
    /// <paramref name="within"/> when the client gives a time for the let-go. One that lives on not let go is told in the
    /// console, never left waiting unsaid.</summary>
    private async Task ReleaseHeldTargetAsync(TimeSpan? within = null)
    {
        int target = Interlocked.Exchange(ref _heldTarget, 0);
        if (target == 0)
        {
            return;
        }
        string? why = await DiagnosticClientHelper.ResumeAsync(target, within).ConfigureAwait(false);
        _logger?.Invoke(why is null ? $"Let process {target} go: the debugger did not take hold of it" : $"Process {target}, not taken hold of, was not let go: {why}");
        if (why is not null && Core.DotnetProcesses.Runs(target))
        {
            OnConsole?.Invoke($"Process {target} was not let go ({why}): a .NET program held at its start waits there until a debugger attaches to it or it is stopped.\n");
        }
    }

    /// <summary>The client's breakpoints are set: the launch or attach asked for is made.</summary>
    public async Task ConfigurationDone()
    {
        _logger?.Invoke("ConfigurationDone");

        if (_pendingLaunchInfo is { LaunchRequestConsoleType: LaunchRequestConsoleType.ExternalTerminal or LaunchRequestConsoleType.IntegratedTerminal } inTerminal)
        {
            _pendingLaunchInfo = null;
            int launchedProcessId = await SendRunInTerminalRequest.Invoke(inTerminal);
            try
            {
                // Held at its port until the debugger is registered for its runtime, which then waits for the debugger's hold.
                await PerformAttach(launchedProcessId, DiagnosticClientHelper.LetGoIfHeld);
            }
            catch
            {
                // Left held at its port, it would wait in the client's terminal for good.
                EndHeld(launchedProcessId);
                throw;
            }
        }
        else if (_pendingLaunchInfo is not null)
        {
            await PerformLaunch();
        }
        else if (_pendingAttachProcessId is int processId)
        {
            _pendingAttachProcessId = null;
            await PerformAttach(processId, DiagnosticClientHelper.LetGoIfHeld);
        }
    }

    /// <summary>Ends a program started in the client's terminal that the debugger could not take hold of.</summary>
    private void EndHeld(int processId)
    {
        try
        {
            using Process held = Process.GetProcessById(processId);
            held.Kill();
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger?.Invoke($"Process {processId}, not taken hold of, could not be ended: {gone.Message}");
        }
    }

    /// <summary>The client's continue: the program runs on from the stop shown, all its threads.</summary>
    public void Continue()
    {
        _logger?.Invoke("Continue");
        ForgetReturnedValues();
        Continue(clearVariables: true, allowSuperfluous: true);
    }

    /// <summary>The client's pause: a running program stops wherever it is; one held for the runtime's own event stops
    /// there once a thread runs managed code; one stopped already stays as it is.</summary>
    public void Pause()
    {
        _logger?.Invoke("Pause");
        ArgumentNullException.ThrowIfNull(_process);
        if (_stopShown)
        {
            return;
        }
        if (_process.IsRunning is false)
        {
            // Held for the runtime's own event (a module loading, a thread starting): the pause is taken in Continue.
            _pauseRequested = true;
            return;
        }

        _process.Stop(0);
        EndStepForStop();
        // ICorDebug's Stop is synchronous and raises no callback of its own, so the stop is reported here.
        if (PausedThread() is { } thread)
        {
            ReportStop(thread, StopReasons.Pause);
            return;
        }
        // No thread runs managed code yet (the runtime is still starting): the pause is taken at its next event.
        _pauseRequested = true;
        _process.Continue(false);
    }

    /// <summary>A pause asked for while the process was held for the runtime's own event, taken instead of letting it go
    /// on once a thread runs managed code; true when it was. It ends a step under way, as a pause of a running one does,
    /// so the next step is not refused as one already in progress.</summary>
    private bool TakePendingPause()
    {
        if (!_pauseRequested || _stopShown || PausedThread() is not { } thread)
        {
            return false;
        }
        _pauseRequested = false;
        EndStepForStop();
        ReportStop(thread, StopReasons.Pause);
        return true;
    }

    /// <summary>The thread a pause shows: the main thread, else the first running managed code; none before any does.</summary>
    private int? PausedThread()
    {
        if (_mainThreadId is { } main && _threads.TryGetValue(main, out ThreadInfo? mainThread) && HasManagedFrames(mainThread.Thread))
        {
            return main;
        }
        foreach ((int id, ThreadInfo info) in _threads)
        {
            if (HasManagedFrames(info.Thread))
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>A thread paused outside managed code (in a sleep, a wait, a read) has no line to step from: any step runs
    /// it out to managed code first, where the step completes as one from the caller would.</summary>
    private bool StepOutToManagedCode(ICorDebugThread thread)
    {
        if (thread.ActiveFrame is ICorDebugILFrame)
        {
            return false;
        }
        BeginStep(thread.CreateStepper(), thread, null, _justMyCode, stepper => stepper.StepOut());
        Continue(clearVariables: true);
        return true;
    }

    /// <summary>Steps a thread on, or refuses saying why: a step answered as taken that never set the program going would
    /// leave the IDE waiting for a stop that never comes. An async method's step over an await is the async stepper's.</summary>
    public async Task Step(int threadId, StepType type)
    {
        _logger?.Invoke($"{type} on thread {threadId}");
        _lastStep = type;
        ArgumentNullException.ThrowIfNull(_process);
        if (!_threads.TryGetValue(threadId, out ThreadInfo? threadInfo))
        {
            throw new InvalidOperationException($"The program has no thread {threadId} to step: it has ended");
        }
        // Held while the IDE shows a stop, even after an evaluation that could not be stopped (RunEvalAsync takes it back).
        if (_process.TryIsRunning(out bool running) is Cor.S_OK && running)
        {
            throw new InvalidOperationException("The program is running, so it cannot step");
        }
        ICorDebugThread thread = threadInfo.Thread;
        if (_stepper is not null || _stepAfterEvaluation is not null)
        {
            throw new InvalidOperationException("A step operation is already in progress");
        }
        ForgetReturnedValues();
        _stepBegan = null;
        if (EvalStatus.Abandoned.ContainsKey(threadId))
        {
            // A stepper made now would be the given-up evaluation's, ended with it: the step waits for it to return.
            _stepAfterEvaluation = (threadId, type);
            Continue(clearVariables: true);
            return;
        }
        if (_unhandledByUser is (int stopped, StackRange caught) && stopped == threadId
            && EnumerateFramesForThread(thread).FirstOrDefault(frame => RangeOf(frame) == caught) is { } handler)
        {
            // At an exception the user's code left unhandled the step goes where that code goes on, past its handler.
            StepOutOfHandler(thread, handler);
            Continue(clearVariables: true);
            return;
        }
        if (StepOutToManagedCode(thread) || StepPastWrite(thread, type))
        {
            return;
        }
        if (_asyncStepper is not null)
        {
            (bool handledByAsyncStepper, bool? useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, type);
            if (handledByAsyncStepper && useSimpleStepper is false)
            {
                Continue(clearVariables: true);
                return;
            }
        }
        _stepBegan = BeganIn(thread);
        SetupStepper(thread, type);
        if (type is StepType.StepOver && thread.ActiveFrame is ICorDebugILFrame from && StepRangeOf(from) is var (start, end))
        {
            WatchReturns(thread, from, start, end);
        }
        Continue(clearVariables: true);
    }

    /// <summary>Sets a file's breakpoints to those asked for. One asked for again just as it was (its line, column,
    /// condition, hit count, message and whether it stops) is kept whole: its id, its binding and its hit count, so a hit
    /// of it already on its way still stops, and a change elsewhere in the file starts no count again; one asked for at the
    /// same line and column with other settings counts on from the hits of the one it replaces. Those no longer asked for
    /// are let go, and the new ones bound.</summary>
    public List<BreakpointManager.BreakpointInfo> SetBreakpoints(string filePath, BreakpointRequest[] breakpoints)
    {
        _logger?.Invoke($"SetBreakpoints: {filePath}, breakpoints: {string.Join(",", breakpoints.Select(b => $"L{b.Line} {(b.Column is not null ? $"C{b.Column}" : null)} {(b.Condition is not null ? $"[{b.Condition}]" : null)}{(b.HitCondition is not null ? $" hits {b.HitCondition}" : null)}"))}");

        (BreakpointManager.BreakpointInfo? Kept, int Hits)[] matched = Reconcile(breakpoints, _breakpointManager.GetBreakpointsForFile(filePath), b => b.Request,
            static (was, now) => was.Line == now.Line && was.Column == now.Column, out List<BreakpointManager.BreakpointInfo> gone);
        foreach (BreakpointManager.BreakpointInfo each in gone)
        {
            LetGo(each);
            _breakpointManager.RemoveBreakpoint(each.Id);
        }

        List<BreakpointManager.BreakpointInfo> result = new List<BreakpointManager.BreakpointInfo>();
        for (int i = 0; i < breakpoints.Length; i++)
        {
            if (matched[i].Kept is { } same)
            {
                _logger?.Invoke($"Breakpoint {same.Id} at {filePath}:{breakpoints[i].Line} kept as it was, {same.HitCount} hits in");
                result.Add(same);
                continue;
            }
            BreakpointRequest request = breakpoints[i];
            BreakpointManager.BreakpointInfo bp = _breakpointManager.CreateBreakpoint(filePath, request.Line, request.Column, request.Condition, request.HitCondition, request.LogMessage, request.Suspend);
            bp.Request = request;
            bp.HitCount = matched[i].Hits;
            if (Unreadable(bp.Condition, bp.HitCondition) is { } unreadable)
            {
                bp.Message = unreadable;
                bp.Refused = true;
                result.Add(bp);
                continue;
            }

            if (_process is not null)
            {
                TryBindBreakpoint(bp);
            }
            else
            {
                bp.Message = "Breakpoint has not been processed by the debugger.";
            }

            result.Add(bp);
        }

        return result;
    }

    /// <summary>Sets the function breakpoints to those asked for, which replace those set before, kept and counted on as
    /// <see cref="SetBreakpoints"/> does a file's, one of the same name with other settings counting on from the hits of the
    /// one it replaces: a change beside one (a property breakpoint added) starts no count again.</summary>
    public List<BreakpointManager.BreakpointInfo> SetFunctionBreakpoints(FunctionBreakpointRequest[] breakpoints)
    {
        (BreakpointManager.BreakpointInfo? Kept, int Hits)[] matched = Reconcile(breakpoints, _breakpointManager.GetFunctionBreakpoints(), b => b.FunctionRequest,
            static (was, now) => was.Name == now.Name, out List<BreakpointManager.BreakpointInfo> gone);
        foreach (BreakpointManager.BreakpointInfo each in gone)
        {
            LetGo(each);
            _breakpointManager.RemoveBreakpoint(each.Id);
        }

        List<BreakpointManager.BreakpointInfo> result = new List<BreakpointManager.BreakpointInfo>();
        for (int i = 0; i < breakpoints.Length; i++)
        {
            if (matched[i].Kept is { } same)
            {
                _logger?.Invoke($"Function breakpoint {same.Id} on {same.FunctionName} kept as it was, {same.HitCount} hits in");
                result.Add(same);
                continue;
            }
            FunctionBreakpointRequest request = breakpoints[i];
            BreakpointManager.BreakpointInfo bp = _breakpointManager.CreateFunctionBreakpoint(request.Name, request.Condition, request.HitCondition, request.LogMessage, request.Suspend);
            bp.FunctionRequest = request;
            bp.HitCount = matched[i].Hits;
            if (Unreadable(bp.Condition, bp.HitCondition) is { } unreadable)
            {
                bp.Message = unreadable;
                bp.Refused = true;
                result.Add(bp);
                continue;
            }
            try
            {
                _ = FunctionBreakpointPattern.Parse(request.Name);
                foreach (ModuleInfo module in _modules.Values)
                {
                    TryBindFunctionBreakpoint(bp, module);
                }
                if (!bp.Verified)
                {
                    bp.Message = _process is null
                    ? "Breakpoint has not been processed by the debugger."
                    : $"No functions matching '{request.Name}' were found.";
                }
            }
            catch (ArgumentException ex)
            {
                bp.Message = ex.Message;
            }
            result.Add(bp);
        }
        return result;
    }

    /// <summary>The threads running managed code, each with its name (the program's, else the engine's) and whether it is the
    /// thread that runs Main (<see cref="NoteMainThread"/>), whatever the program named it.</summary>
    public List<(int id, string name, bool main)> GetThreads()
    {
        List<(int, string, bool)> result = new List<(int, string, bool)>();
        if (_process is null)
        {
            return result;
        }

        try
        {
            foreach ((int id, ThreadInfo? threadInfo) in _threads)
            {
                // A thread with no managed frame (one of the runtime's own) would be a row that opens to nothing.
                if (!HasManagedFrames(threadInfo.Thread))
                {
                    continue;
                }
                threadInfo.Name ??= threadInfo.Thread.GetThreadNameOrNull();
                result.Add((id, threadInfo.Name ?? (id == _mainThreadId ? "Main Thread" : "<No Name>"), id == _mainThreadId));
            }
        }
        catch (Exception ex) when (Failures.OfRuntime(ex))
        {
            _logger?.Invoke($"Error getting threads: {ex.Message}");
        }

        return result;
    }

    private static bool HasManagedFrames(ICorDebugThread thread)
    {
        try
        {
            return EnumerateFramesForThread(thread).Any();
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
    }

    /// <summary>A thread's frames from <paramref name="startFrame"/>, as many as asked (all of them for no
    /// <paramref name="levels"/> or 0, as the protocol means it): its own, then the callers of its async frames, which only
    /// their state machines know.</summary>
    public List<StackFrameInfo> GetStackTrace(int threadIdInt, int startFrame = 0, int? levels = null)
    {
        List<StackFrameInfo> result = new List<StackFrameInfo>();

        if (!_threads.TryGetValue(threadIdInt, out ThreadInfo? threadInfo))
        {
            return result;
        }
        ICorDebugThread thread = threadInfo.Thread;

        try
        {
            long endFrame = levels is > 0 ? (long)startFrame + levels.Value : long.MaxValue;
            int index = 0;

            // Physical frames are walked lazily, the IL frames kept so their async callers can follow them all.
            List<(int physicalDepth, ICorDebugILFrame frame)> ilFramesForSyntheticAsyncFrames = new List<(int physicalDepth, ICorDebugILFrame frame)>();
            ThreadId threadId = new ThreadId(threadIdInt);
            foreach ((int physicalDepth, ICorDebugFrame? frame) in EnumerateFramesForThread(thread).AsValueEnumerable().Index())
            {
                if (index >= endFrame)
                {
                    return result;
                }

                if (index >= startFrame)
                {
                    int frameId = _frameReferenceManager.GetOrCreateFrameId(threadId, new FrameStackDepth(physicalDepth));
                    result.Add(DescribeFrame(frameId, frame));
                }
                if (frame is ICorDebugILFrame ilFrame)
                {
                    ilFramesForSyntheticAsyncFrames.Add((physicalDepth, ilFrame));
                }

                index++;
            }

            foreach ((int physicalDepth, ICorDebugILFrame? ilFrame) in ilFramesForSyntheticAsyncFrames)
            {
                FrameStackDepth physicalFrameStackDepth = new FrameStackDepth(physicalDepth);
                foreach ((int syntheticFrameIndex, SyntheticAsyncCallerFrame? syntheticFrame) in GetSyntheticAsyncCallerFrames(ilFrame).AsValueEnumerable().Index())
                {
                    if (index >= endFrame)
                    {
                        return result;
                    }

                    if (index < startFrame)
                    {
                        index++;
                        continue;
                    }
                    index++;

                    int syntheticFrameId = _frameReferenceManager.GetOrCreateSyntheticAsyncFrameId(threadId, physicalFrameStackDepth, syntheticFrameIndex, syntheticFrame);
                    result.Add(CreateSyntheticAsyncStackFrameInfo(syntheticFrameId, threadId, physicalFrameStackDepth, syntheticFrame, decompiled: false));
                }
            }
        }
        catch (Exception ex)
        {
            // A frame that fails to be read leaves the frames read before it, its failure said whole.
            _logger?.Invoke($"Error getting stack trace: {ex}");
        }

        return result;
    }

    /// <summary>A frame with source: its module's own symbols, else those decompiled for it, which the cache has once
    /// <see cref="SymbolsWantedAt"/>'s decompile has made them. Fails for a frame of a stop the program has run on from.</summary>
    public StackFrameInfo ResolveStackFrame(int frameId)
    {
        (ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame)? syntheticFrame = _frameReferenceManager.GetSyntheticAsyncFrameById(frameId);
        if (syntheticFrame is not null)
        {
            return CreateSyntheticAsyncStackFrameInfo(frameId, syntheticFrame.Value.threadId, syntheticFrame.Value.physicalFrameStackDepth, syntheticFrame.Value.frame, decompiled: true);
        }

        return CreateStackFrameInfo(frameId, ResolvableFrame(frameId), true);
    }

    /// <summary>What a frame's module needs decompiled before <see cref="ResolveStackFrame"/> can give it source, or null when
    /// nothing: read under the engine's lock, for <see cref="DecompileAsync"/> to make off it.</summary>
    public SymbolsWanted? SymbolsWantedAt(int frameId)
    {
        if (_frameReferenceManager.GetSyntheticAsyncFrameById(frameId) is { } synthetic)
        {
            return WantedFor(synthetic.frame.Module);
        }
        ICorDebugILFrame frame = ResolvableFrame(frameId);
        return FindModule(frame.Function.Module) is { } module ? WantedFor(module) : null;
    }

    private ICorDebugILFrame ResolvableFrame(int frameId)
    {
        (ThreadId threadId, FrameStackDepth frameStackDepth) frameInfo = _frameReferenceManager.GetFrameInfoById(frameId) ?? throw new ArgumentException(_frameReferenceManager.Unknown(frameId));
        return GetFrameForThreadIdAndStackDepth(frameInfo.threadId, frameInfo.frameStackDepth) as ICorDebugILFrame
            ?? throw new InvalidOperationException($"Stack frame '{frameId}' cannot be resolved because it is not an IL frame.");
    }

    /// <summary>What a frame whose method the debugger cannot read is called.</summary>
    private const string UnknownMethod = "[Unknown method]";

    /// <summary>A frame as the stack shows it; one that cannot be read is named <see cref="UnknownMethod"/>, and the
    /// walk goes on.</summary>
    private StackFrameInfo DescribeFrame(int frameId, ICorDebugFrame frame)
    {
        try
        {
            return CreateStackFrameInfo(frameId, frame, false);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"Frame {frameId} could not be read: {error.Message}");
            return new StackFrameInfo
            {
                Id = frameId,
                Name = UnknownMethod,
                Line = 0,
                EndLine = null,
                Column = 0,
                EndColumn = null,
                Source = null,
                IsUserCode = false,
                IsResolved = false,
                DecompiledSourceInfo = null
            };
        }
    }

    private StackFrameInfo CreateStackFrameInfo(int frameId, ICorDebugFrame frame, bool decompiled)
    {
        StackFrameInfo stackFrameInfo = new StackFrameInfo
        {
            Id = frameId,
            Name = null!,
            Line = 0,
            EndLine = null,
            Column = 0,
            EndColumn = null,
            Source = null,
            IsUserCode = false,
            IsResolved = true,
            DecompiledSourceInfo = null
        };

        if (frame is ICorDebugILFrame ilFrame)
        {
            ICorDebugFunction function = ilFrame.Function;
            if (FindModule(function.Module) is not { } module)
            {
                stackFrameInfo.Name = UnknownMethod;
                stackFrameInfo.IsResolved = false;
                return stackFrameInfo;
            }
            int? kickoffMethodToken = module.MetadataReader.GetStateMachineKickoffMethodToken(function.Token);
            stackFrameInfo.Name = GetMethodFormattedName(module, kickoffMethodToken ?? function.Token, ilFrame.TypeParameters);
            SourceInfo? sourceInfo = GetSourceInfoAtFrame(ilFrame, decompiled);
            // A method its own code says is not the user's ([DebuggerNonUserCode] and kin) is shown as external code.
            stackFrameInfo.IsUserCode = module.IsUserCode && !(_justMyCode && module.MetadataReader.IsNonUserCode(function.Token));
            stackFrameInfo.IsResolved = module.MetadataReader.HasSymbols;
            if (sourceInfo is not null)
            {
                stackFrameInfo.Line = sourceInfo.Value.StartLine;
                stackFrameInfo.EndLine = sourceInfo.Value.EndLine;
                stackFrameInfo.Column = sourceInfo.Value.StartColumn;
                stackFrameInfo.EndColumn = sourceInfo.Value.EndColumn;
                stackFrameInfo.Source = sourceInfo.Value.FilePath;
                stackFrameInfo.DecompiledSourceInfo = sourceInfo.Value.DecompiledSourceInfo;
            }
        }
        else if (frame is ICorDebugInternalFrame internalFrame)
        {
            stackFrameInfo.Name = internalFrame.FrameType.ToDisplayName();
        }
        else if (frame is ICorDebugNativeFrame)
        {
            stackFrameInfo.Name = "[Native Frame]";
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "Unknown frame type");
        }

        return stackFrameInfo;
    }

    private StackFrameInfo CreateSyntheticAsyncStackFrameInfo(int frameId, ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame, bool decompiled)
    {
        SourceInfo? source = GetSyntheticAsyncFrameSourceInfo(frame, threadId, physicalFrameStackDepth, decompiled);
        return new StackFrameInfo
        {
            Id = frameId,
            Name = frame.Name,
            Line = source?.StartLine ?? 0,
            EndLine = source?.EndLine,
            Column = source?.StartColumn ?? 0,
            EndColumn = source?.EndColumn,
            Source = source?.FilePath,
            IsUserCode = frame.Module.IsUserCode,
            IsResolved = frame.Module.MetadataReader.HasSymbols,
            DecompiledSourceInfo = source?.DecompiledSourceInfo
        };
    }

    /// <summary>A frame's one scope, Locals, when it has locals, arguments, an exception or return values to show; none
    /// otherwise.</summary>
    public List<ScopeInfo> GetScopes(int frameId)
    {
        List<ScopeInfo> result = new List<ScopeInfo>();

        (ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame)? syntheticFrame = _frameReferenceManager.GetSyntheticAsyncFrameById(frameId);
        if (syntheticFrame is not null)
        {
            int syntheticLocalsReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.SyntheticAsyncScope,
                syntheticFrame.Value.frame.StateMachine, syntheticFrame.Value.threadId, syntheticFrame.Value.physicalFrameStackDepth, null));
            result.Add(new ScopeInfo { Name = "Locals", VariablesReference = syntheticLocalsReference, Expensive = false });
            return result;
        }
        (ThreadId threadId, FrameStackDepth frameStackDepth)? frameInfo = _frameReferenceManager.GetFrameInfoById(frameId);
        if (frameInfo is not var (threadId, frameStackDepth))
        {
            throw new ArgumentException(_frameReferenceManager.Unknown(frameId));
        }
        RequireStop();

        ICorDebugFrame frame = GetFrameForThreadIdAndStackDepth(threadId, frameStackDepth);
        if (frame is not ICorDebugILFrame ilFrame)
        {
            return result;
        }

        ICorDebugValue[] localVariables = ilFrame.LocalVariables;
        ICorDebugValue[] arguments = ilFrame.Arguments;
        ICorDebugThread? thread = _threads.GetValueOrDefault(threadId.Value)?.Thread;
        ArgumentNullException.ThrowIfNull(thread);
        bool hasCurrentException = thread.TryGetCurrentException(out _) is Cor.S_OK;
        if (localVariables.Length is 0 && arguments.Length is 0 && !hasCurrentException && !HasReturnedValues(threadId, frameStackDepth))
        {
            return result;
        }

        int localsRef = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.Scope, null, threadId, frameStackDepth, null));
        result.Add(new ScopeInfo
        {
            Name = "Locals",
            VariablesReference = localsRef,
            Expensive = false
        });
        return result;
    }

    /// <summary>What a reference names: a frame's locals, an object's members, an array's elements, a collection's
    /// results. One from an earlier stop is refused, the program having run on since.</summary>
    public async Task<List<VariableInfo>> GetVariables(int variablesReferenceInt)
    {
        List<VariableInfo> result = new List<VariableInfo>();

        VariablesReference? variablesReferenceNullable = _variableManager.GetReference(variablesReferenceInt);
        if (variablesReferenceNullable is not { } variablesReference)
        {
            throw new ArgumentException(_variableManager.IsFromEarlierStop(variablesReferenceInt)
                ? "This value is from an earlier stop, and the program has run on since"
                : "Invalid variables reference");
        }
        RequireStop();

        try
        {
            if (variablesReference.ReferenceKind is StoredReferenceKind.Scope)
            {
                ICorDebugILFrame ilFrame = GetIlFrameForThreadIdAndStackDepth(variablesReference.ThreadId, variablesReference.FrameStackDepth);
                ICorDebugFunction corDebugFunction = ilFrame.Function;
                ModuleInfo module = GetModuleInfoForModule(corDebugFunction.Module);
                await AddReturnedValues(result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
                await AddCurrentException(result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
                ICorDebugValue? classContainingHoistedLocalsValue = await AddArguments(module, corDebugFunction, result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
                await AddLocalVariables(module, corDebugFunction, result, variablesReference.ThreadId, variablesReference.FrameStackDepth, classContainingHoistedLocalsValue);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.SyntheticAsyncScope)
            {
                ICorDebugValue stateMachine = variablesReference.ObjectValue!;
                await AddHeldVariables(stateMachine, stateMachine.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.StackVariable)
            {
                if (variablesReference.DebuggerProxyInstance is not null)
                {
                    // The view's rows are reached through a view made over the value, but a list's elements by their index in it.
                    ICorDebugObjectValue objectValue = variablesReference.DebuggerProxyInstance.UnwrapDebugValueToObject();
                    string? expression = variablesReference.Expression;
                    ICorDebugObjectValue? viewedValue = variablesReference.ObjectValue!.UnwrapDebugValueToObjectOrNull();
                    string? own = viewedValue is null ? expression : AsOwnType(expression, variablesReference.Declared, viewedValue.ExactType);
                    string? viewed = RowExpressions.Viewed(own, GetCorDebugTypeFriendlyName(objectValue.ExactType));
                    string? listed = own is not null && viewedValue is not null && IsList(viewedValue.ExactType) ? own : null;
                    await AddMembersAndStaticPseudoVariable(variablesReference.DebuggerProxyInstance, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, false,
                        owner: viewed, listed: listed);
                    if (listed is null || variablesReference.Copied)
                    {
                        // What the view holds it made itself, but for a list's elements, each the list's own.
                        Copies(result);
                    }
                    result.Add(GroupRow(DapRows.RawView, new VariablesReference(StoredReferenceKind.RawView, variablesReference.ObjectValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, null,
                        Declared: variablesReference.Declared, Expression: expression, Copied: variablesReference.Copied)));
                    return result;
                }
                ICorDebugValue unwrappedDebugValue = variablesReference.ObjectValue!.UnwrapDebugValue();

                if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
                {
                    await AddArrayElements(arrayValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, referenceValue: variablesReference.ObjectValue, declared: variablesReference.Declared,
                        expression: AsOwnType(variablesReference.Expression, variablesReference.Declared, arrayValue.ExactType));
                }
                else if (unwrappedDebugValue is ICorDebugObjectValue objectValue)
                {
                    if (OpenedBy(objectValue.ExactType) is ({ } presentation, int own))
                    {
                        await AddPresentedRows(variablesReference, objectValue, presentation, own, result);
                    }
                    else if (IsEnumerable(objectValue.ExactType))
                    {
                        if (EfQueryModule(objectValue.ExactType) is not null)
                        {
                            AddQuerySqlRow(variablesReference, result);
                        }
                        AddEnumerablePseudoVariables(variablesReference, result);
                    }
                    else
                    {
                        await AddMembersAndStaticPseudoVariable(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result,
                            declared: variablesReference.Declared, owner: variablesReference.Expression);
                    }
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(unwrappedDebugValue));
                }
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.StaticClassVariable)
            {
                ICorDebugObjectValue objectValue = variablesReference.ObjectValue!.UnwrapDebugValueToObject();
                await AddStaticMembers(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.RawView)
            {
                ICorDebugObjectValue objectValue = variablesReference.ObjectValue!.UnwrapDebugValueToObject();
                await AddMembersAndStaticPseudoVariable(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result,
                    declared: variablesReference.Declared, owner: variablesReference.Expression);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.EnumerableResults)
            {
                await AddEnumerableResults(variablesReference, result);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.QuerySql)
            {
                result.Add(await ReadQuerySql(variablesReference));
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.Pointer)
            {
                await AddPointedAt(variablesReference, result);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.ArrayRange)
            {
                ICorDebugArrayValue arrayValue = (ICorDebugArrayValue)variablesReference.ObjectValue!.UnwrapDebugValue();
                await AddArrayElements(arrayValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, variablesReference.ArrayIndices, variablesReference.ArrayStartOffset, variablesReference.ArrayCount, variablesReference.ObjectValue,
                    variablesReference.Declared, variablesReference.Expression);
            }
            if (variablesReference.Copied)
            {
                Copies(result);
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error getting variables: {ex.Message}, {ex}");
            throw;
        }

        return result;
    }

    /// <summary>An expression's value in a frame. One asked for by name (the Evaluate window, a watch) runs the code it needs
    /// even once implicit evaluation is off; one the IDE asks of itself (a hover) does not. One the person asks to run with
    /// every thread let go (<paramref name="letThreadsRun"/>) does, first letting one left running on its thread return, and
    /// says what the other threads passed over meanwhile.</summary>
    public async Task<VariableInfo> Evaluate(string expression, int? frameId, bool implicitly = false, bool letThreadsRun = false)
    {
        EvalStatus.LetThreadsRun = letThreadsRun;
        EvalStatus.PassedOver.Clear();
        try
        {
            // Before anything is read, as letting the program run leaves no value read before it good.
            if (letThreadsRun && frameId is int id && _frameReferenceManager.GetFrameInfoById(id) is var (threadId, _) && EvalStatus.Abandoned.ContainsKey(threadId.Value)
                && _threads.GetValueOrDefault(threadId.Value)?.Thread is { } thread && !await LetAbandonedReturnAsync(thread))
            {
                return FailedRow(expression.Trim(), $"The evaluation left running on this thread has not returned {Extensions.EvaluationTimeout.TotalSeconds:0.#} s after every thread was let go, so no other can run on it until it does");
            }
            VariableInfo value = await Evaluate(expression, frameId, expression.Trim(), implicitly);
            if (EvalStatus.PassedOver.Count > 0)
            {
                value.PassedOver = $"While it ran with every thread let go, other threads passed over {string.Join(", ", EvalStatus.PassedOver.Distinct())}, which did not stop";
            }
            if (letThreadsRun)
            {
                value.ImplicitOff = EvalStatus.ImplicitOff;
            }
            return value;
        }
        finally
        {
            EvalStatus.LetThreadsRun = false;
            EvalStatus.PassedOver.Clear();
        }
    }

    /// <summary>Set Value: what an expression names takes a value, by their assignment's one evaluation, whose own value is
    /// the answer, so the target runs once. A real number with no suffix, as a float's or a decimal's is shown, takes the one
    /// its target's type needs. A variable typed by type parameters (T, List&lt;T&gt;) takes a value fitting the types its
    /// frame runs them as; one C# keeps readonly (an in parameter, a ref readonly local, a member of either) or reaches
    /// through a nullable's Value is written through the location it refers to (<see cref="WritableTargets"/>).</summary>
    public async Task<VariableInfo> Assign(string target, string value, int? frameId)
    {
        ArgumentNullException.ThrowIfNull(_expressionEvaluator);
        CompiledExpressionEvaluationContext context = ContextOf(frameId);
        (string Cast, string? RunAs)? typed = _expressionEvaluator.TypeParametersOf(target, context);
        if (RealLiteral().IsMatch(value))
        {
            value += (typed?.RunAs ?? _expressionEvaluator.TypeOf(target, context)) switch
            {
                "System.Single" or "System.Nullable`1<System.Single>" or "float" or "float?" => "f",
                "System.Decimal" or "System.Nullable`1<System.Decimal>" or "decimal" or "decimal?" => "m",
                _ => string.Empty,
            };
        }
        if (typed is { } parameterized)
        {
            // An element of an array of the type the frame runs takes the value as C# would, converting it or refusing it.
            value = parameterized.RunAs is { } runAs
                ? $"({parameterized.Cast})(object)new {CSharpTypeNames.ArrayOf(runAs, "[]")} {{ {value} }}[0]"
                : $"({parameterized.Cast})(object)({value})";
        }
        string assignment = $"{target} = {value}";
        if (_expressionEvaluator.FailsWith(assignment, context, NotWritable))
        {
            assignment = WritableTargets.Of(target, receiver => _expressionEvaluator.TypeOf(receiver, context)?.StartsWith("System.Nullable`1<", StringComparison.Ordinal) == true)
                .Select(writable => $"{writable} = {value}").FirstOrDefault(writable => _expressionEvaluator.Compiles(writable, context)) ?? assignment;
        }
        return await Evaluate(assignment, frameId, target.Trim(), implicitly: false);
    }

    /// <summary>The compiler's errors for an assignment to a variable it keeps readonly, to a member of one, and to a member
    /// of the copy a property gives (a nullable's Value).</summary>
    private static readonly string[] NotWritable = ["CS8331", "CS8332", "CS1612"];

    /// <summary>A real number as the debugger shows a float's, a double's or a decimal's: a point or an exponent, no suffix.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^-?\d+(\.\d+([eE][+-]?\d+)?|[eE][+-]?\d+)$")]
    private static partial System.Text.RegularExpressions.Regex RealLiteral();

    /// <summary>An expression's value, its members reached by the name given (the target of an assignment, for one).</summary>
    private async Task<VariableInfo> Evaluate(string expression, int? frameId, string named, bool implicitly)
    {
        EvalStatus.Explicit = !implicitly;
        try
        {
            return await EvaluateInFrame(expression, frameId, named);
        }
        finally
        {
            EvalStatus.Explicit = false;
        }
    }

    private async Task<VariableInfo> EvaluateInFrame(string expression, int? frameId, string named)
    {
        _logger?.Invoke($"Evaluate: {expression}");
        CompiledExpressionEvaluationContext evalContext = ContextOf(frameId);
        (ThreadId threadId, FrameStackDepth frameStackDepth) = (evalContext.ThreadId, evalContext.StackDepth);
        ArgumentNullException.ThrowIfNull(_expressionEvaluator);
        using EvaluationResult result = await _expressionEvaluator.Evaluate(expression, evalContext);

        if (result.Error is not null)
        {
            _logger?.Invoke($"Evaluation error: {result.Error}");
            return FailedRow(named, result.Error);
        }
        if (result.Text is { } text)
        {
            return new VariableInfo { Name = named, Value = SymbolDisplay.FormatLiteral(text, quote: true), Type = "string", VariablesReference = 0 };
        }
        VariableInfo row = await ValueRow(named, named, result.Value!, threadId, frameStackDepth, result.Declared);
        if (row.VariablesReference != 0)
        {
            result.RelinquishResultHandleOwnership();
        }
        return row;
    }

    /// <summary>Where an expression is evaluated: a frame of the stopped program, one an evaluation can run in.</summary>
    private CompiledExpressionEvaluationContext ContextOf(int? frameId)
    {
        if (frameId is null or 0)
        {
            throw new InvalidOperationException("Frame ID is required for evaluation");
        }

        if (_frameReferenceManager.GetSyntheticAsyncFrameById(frameId.Value) is not null)
        {
            throw new InvalidOperationException("Expression evaluation is not supported for synthetic async caller frames");
        }

        (ThreadId threadId, FrameStackDepth frameStackDepth)? frameInfo = _frameReferenceManager.GetFrameInfoById(frameId.Value);
        if (frameInfo is not var (threadId, frameStackDepth))
        {
            throw new InvalidOperationException(_frameReferenceManager.Unknown(frameId.Value));
        }
        RequireStop();

        ICorDebugThread? thread = _threads.GetValueOrDefault(threadId.Value)?.Thread;
        ArgumentNullException.ThrowIfNull(thread);
        return new CompiledExpressionEvaluationContext(thread, threadId, frameStackDepth);
    }

    /// <summary>Ends the program and lets go of it, once the runtime has seen it go.</summary>
    public async Task TerminateAsync()
    {
        _logger?.Invoke("Terminate");
        if (_process is not null && !_exited.IsSet)
        {
            // A running process cannot be terminated (CORDBG_E_PROCESS_NOT_SYNCHRONIZED): it is stopped first.
            if (_process.TryIsRunning(out bool isRunning) is Cor.S_OK && isRunning)
            {
                int stopResult = _process.TryStop(0);
                if (stopResult is not (Cor.S_OK or Cor.CORDBG_E_PROCESS_TERMINATED))
                {
                    _logger?.Invoke($"Error stopping process before terminating it: {stopResult}");
                }
            }
            int terminated = _process.TryTerminate(0);
            if (terminated < 0)
            {
                _logger?.Invoke($"Error terminating process: 0x{terminated:X8}");
            }
            // The process goes on the runtime's thread; letting go of it before it has would race that.
            else if (!_exited.Wait(TimeSpan.FromSeconds(5)))
            {
                _logger?.Invoke("The process had not exited 5 s after Terminate");
            }
        }
        await EndAsync().ConfigureAwait(false);
    }

    /// <summary>Ends the session: the program ends with it when asked, else it is let go to run on, the let-go kept within
    /// the client's time for it when it gives one (<paramref name="within"/>).</summary>
    public async Task DisconnectAsync(bool terminateDebuggee, TimeSpan? within = null)
    {
        _logger?.Invoke($"Disconnect (terminate: {terminateDebuggee})");

        if (terminateDebuggee)
        {
            await TerminateAsync().ConfigureAwait(false);
        }
        else
        {
            if (_process is not null && _isAttached && !_exited.IsSet && _process?.TryIsRunning(out bool isRunning) is Cor.S_OK && isRunning)
            {
                int hResult = _process.TryStop(0);
                if (hResult is not (Cor.S_OK or Cor.CORDBG_E_PROCESS_TERMINATED))
                {
                    _logger?.Invoke($"Error stopping process during disconnect: {hResult}");
                }
            }
            await EndAsync(letGo: true, within).ConfigureAwait(false);
        }
    }

    /// <summary>The client went without disconnecting (crashed, or was ended): a program the session launched ends with it,
    /// and one it attached to is let go to run on, as a detach lets it go.</summary>
    public Task AbandonAsync() => DisconnectAsync(terminateDebuggee: _launched);

    /// <summary>The exception a thread is stopped with. Its type, the message it was made with, its HResult, its inner
    /// exceptions and their stack traces are read from what they store, running no code, so a stop always names it; its
    /// getters then add what they say (an overridden Message, Source, a StackTrace the debugger could not read) in their
    /// time. Once one cannot be run, or implicit evaluation is off, none after it is, and the description says why.</summary>
    public async Task<ExceptionInfo> ExceptionInfo(ThreadId threadId)
    {
        _logger?.Invoke($"ExceptionInfo for thread {threadId.Value}");
        RequireStop();
        ICorDebugThread thread = _process!.GetThread(threadId.Value);
        if (thread.TryGetCurrentException(out ICorDebugValue? currentException) is not Cor.S_OK)
        {
            _logger?.Invoke("No current exception");
            throw new InvalidOperationException("No current exception on thread");
        }
        List<ThrownException> chain = StoredChain(currentException, thread);
        try
        {
            // No code runs to read it once an evaluation has timed out, until the program runs on.
            string? why = EvalStatus.ImplicitOff;
            // Every Message first, as what the person reads; the stack trace, the costliest to make, last.
            foreach (ThrownException each in chain)
            {
                why ??= await Add(each, "Message", said => each.Message = said);
            }
            foreach (ThrownException each in chain.Where(e => e.Source is null))
            {
                why ??= await Add(each, "Source", said => each.Source = said);
            }
            foreach (ThrownException each in chain.Where(e => !e.TraceRead))
            {
                why ??= await Add(each, "StackTrace", said => each.StackTrace = said);
            }
            ThrownException top = chain[0];
            return new ExceptionInfo
            {
                ExceptionId = $"CLR/{top.TypeName}",
                // With an inner exception, a line naming the innermost.
                Description = $"Exception thrown: '{top.TypeName}'{(top.Source is { Length: > 0 } source ? $" in {source}.dll" : "")}: '{top.Message}'"
                    + (chain.Count > 1 ? $"\n Inner exceptions found, see {DapRows.Exception} in variables window for more details.\n Innermost exception: {chain[^1].TypeName}" : "")
                    + (why is null ? "" : $"\n {why}"),
                BreakMode = _exceptionBreakModes.GetValueOrDefault(threadId, ExceptionBreakMode.Unknown),
                Code = 0,
                Details = Details(0, DapRows.Exception),
            };
        }
        finally
        {
            foreach (ThrownException each in chain)
            {
                each.Held.TryDispose();
            }
        }

        // A getter's words in place of what the fields gave, or why it could not be run.
        async Task<string?> Add(ThrownException exception, string property, Action<string> said)
        {
            ICorDebugValue? value;
            try
            {
                value = await exception.Held.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, StopFrame(thread), property);
            }
            catch (EvalRefusedException refused) when (refused.HResult is Cor.CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT or Cor.CORDBG_E_FUNC_EVAL_BAD_START_POINT)
            {
                // A thread whose exception came while another thread's evaluation ran is held where no code can run on it.
                return "It was thrown while the debugger ran code on another thread, so its thread is held where no code can run, and its properties cannot be read.";
            }
            catch (Exception error) when (error is EvalException || Failures.OfRuntime(error))
            {
                return $"Its {property} was not evaluated, nor what comes after it: {error.Message}";
            }
            try
            {
                // A getter that threw gives its exception, and one that gave null leaves the field's.
                if (value?.UnwrapDebugValue() is ICorDebugStringValue text)
                {
                    said(text.String);
                }
                return null;
            }
            finally
            {
                if (value is ICorDebugHandleValue handle)
                {
                    handle.TryDispose();
                }
            }
        }

        ExceptionInfo.ExceptionDetails Details(int at, string evaluateName)
        {
            ThrownException exception = chain[at];
            int lastDot = exception.TypeName.LastIndexOf('.');
            return new ExceptionInfo.ExceptionDetails
            {
                Message = exception.Message,
                TypeName = lastDot is not -1 ? exception.TypeName[(lastDot + 1)..] : exception.TypeName,
                FullTypeName = exception.TypeName,
                EvaluateName = evaluateName,
                StackTrace = exception.StackTrace ?? "",
                InnerException = at + 1 < chain.Count ? [Details(at + 1, $"{evaluateName}.InnerException")] : [],
                FormattedDescription = $"**{exception.TypeName}:** '{exception.Message}'",
                HResult = exception.HResult,
                Source = exception.Source ?? "",
            };
        }
    }

    /// <summary>The top frame of a thread's stop, found again for each use, as running code neuters a frame: an evaluation that
    /// could not be stopped, still running on the thread, has its frames above it.</summary>
    private static ICorDebugILFrame StopFrame(ICorDebugThread thread) =>
        EnumerateFramesForThread(thread).FirstOrDefault() as ICorDebugILFrame
            ?? throw new InvalidOperationException("The thread is stopped outside managed code, so its exception cannot be read");

    /// <summary>One exception of a stop, from what its fields hold to what its getters add, held by a strong handle, which
    /// outlives the evaluations that run before it is read again.</summary>
    private sealed class ThrownException(ICorDebugHandleValue held, string typeName)
    {
        public ICorDebugHandleValue Held { get; } = held;

        public string TypeName { get; } = typeName;

        public string Message { get; set; } = "";

        public int HResult { get; init; }

        /// <summary>Null until it is known: the field is set once the getter has run, or the program has set it.</summary>
        public string? Source { get; set; }

        /// <summary>What its StackTrace gives: read from what the exception stores where <see cref="TraceRead"/>, else from
        /// its getter once that has run (null for one never thrown).</summary>
        public string? StackTrace { get; set; }

        public bool TraceRead { get; init; }

        /// <summary>Its type as its ToString names it, reflection's way ("Ns.Outer+Failed`1[System.Int32]").</summary>
        public string ClassName { get; init; } = typeName;

        /// <summary>The message it was made with; null for none, where Exception's Message says it was thrown.</summary>
        public string? MadeMessage { get; init; }

        /// <summary>Whether its type writes its own Message, or its own ToString, below System.Exception's.</summary>
        public bool OwnMessage { get; init; }

        public bool OwnText { get; init; }
    }

    /// <summary>An exception and its inner exceptions, as deep as they go within reason, as their fields hold them: their
    /// types, the messages, HResults and sources they were made with, and their stack traces where the debugger can read
    /// them (<see cref="TryStoredStackTrace"/>), placed in the code of <paramref name="thread"/>'s frames. No code runs.</summary>
    private List<ThrownException> StoredChain(ICorDebugValue exception, ICorDebugThread thread)
    {
        List<ThrownException> chain = [];
        bool read = false;
        try
        {
            for (ICorDebugObjectValue? at = exception.UnwrapDebugValueToObjectOrNull(); at is not null && chain.Count <= 10;
                at = (ExceptionField(at, "_innerException") as ICorDebugReferenceValue)?.UnwrapDebugValueToObjectOrNull())
            {
                bool traceRead = TryStoredStackTrace(at, thread, out string? trace);
                string? made = Text(at, "_message");
                chain.Add(new ThrownException(((ICorDebugHeapValue2)at).CreateHandle(CorDebugHandleType.HANDLE_STRONG), GetCorDebugTypeFriendlyName(at.ExactType))
                {
                    Message = made ?? "",
                    MadeMessage = made,
                    HResult = ExceptionField(at, "_HResult") is ICorDebugGenericValue number ? BitConverter.ToInt32(number.GetValueAsBytes()) : 0,
                    Source = Text(at, "_source"),
                    StackTrace = trace,
                    TraceRead = traceRead,
                    ClassName = TypeToString(at.ExactType),
                    OwnMessage = Declares(at.ExactType, "get_Message"),
                    OwnText = Declares(at.ExactType, "ToString"),
                });
            }
            read = chain.Count > 0;
        }
        finally
        {
            if (!read)
            {
                foreach (ThrownException each in chain)
                {
                    each.Held.TryDispose();
                }
            }
        }
        return read ? chain : throw new InvalidOperationException("The thread's exception is not an object the debugger can read");

        static string? Text(ICorDebugObjectValue exception, string field) => ExceptionField(exception, field)?.UnwrapDebugValue() is ICorDebugStringValue text ? text.String : null;
    }

    /// <summary>A field System.Exception declares, of an exception of any type, never a field of the same name a derived
    /// type declares; null where the runtime's Exception has none of that name.</summary>
    private static ICorDebugValue? ExceptionField(ICorDebugObjectValue exception, string name)
    {
        for (ICorDebugType? type = exception.ExactType; type is not null; type = type.Base)
        {
            if (GetCorDebugTypeFriendlyName(type) == "System.Exception")
            {
                ICorDebugClass declaring = type.Class;
                mdFieldDef field = declaring.Module.GetMetaDataInterface<IMetaDataImport>().EnumFieldsWithName(declaring.Token, name).FirstOrDefault();
                return field.IsNil ? null : exception.GetFieldValue(declaring, field);
            }
        }
        return null;
    }

    /// <summary>Text for the launched program's standard input, or the end of its input when
    /// <paramref name="close"/>; nothing for a program the debugger attached to.</summary>
    public void WriteInput(string text, bool close)
    {
        if (_launch is { } launch && !_exited.IsSet)
        {
            try
            {
                launch.Input.Write(Encoding.UTF8.GetBytes(text));
                launch.Input.Flush();
                if (close)
                {
                    launch.Input.Close();
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                _logger?.Invoke($"The program's input is closed: {error.Message}");
            }
        }
    }

    /// <summary>The client's exception filters, and its CLR exception breakpoints when it gives them (null when it gives none,
    /// so every unhandled exception stops), kept and counted on as a file's line breakpoints are, one for the same exception
    /// types counting on from the hits of the one it replaces.</summary>
    public IReadOnlyList<ExceptionRule> SetExceptionBreakpoints(IReadOnlyList<ExceptionBreakpointRequest> filters, IReadOnlyList<ExceptionRuleRequest>? rules = null)
    {
        _exceptionBreakpoints = filters.ToArray();
        if (rules is null)
        {
            _exceptionRules = null;
            return [];
        }
        (ExceptionRule? Kept, int Hits)[] matched = Reconcile(rules, _exceptionRules ?? [], rule => rule.Request,
            static (was, now) => was.Excluded == now.Excluded && was.Types.SequenceEqual(now.Types), out _);
        List<ExceptionRule> now = [];
        for (int i = 0; i < rules.Count; i++)
        {
            ExceptionRuleRequest request = rules[i];
            now.Add(matched[i].Kept ?? new ExceptionRule(_breakpointManager.NextId(), request)
            {
                HitCount = matched[i].Hits,
                Refused = Unreadable(request.Condition, request.HitCondition),
            });
        }
        _exceptionRules = now;
        return now;
    }
}
