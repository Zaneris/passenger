using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Dap;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models;
using Passenger.Debugger.Models.Response;
using Passenger.Debugger.PresentationHintModels;
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
public sealed class ExceptionRule(int id, ExceptionRuleRequest request)
{
    public int Id { get; } = id;
    public ExceptionRuleRequest Request { get; } = request;
    public int HitCount { get; set; }

    /// <summary>Why the debugger will not take it as it is (a hit count or condition it cannot read); it never stops.</summary>
    public string? Refused { get; init; }

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
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(folder, program);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return program;
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
        Task<int?> launchExit = Task.Run(launch.WaitForExit);
        _launchExit = launchExit;
        // Told as the program goes, not when the runtime's word of it is handled, which mscordbi may lose tearing down.
        Core.UnawaitedWork.Observe(launchExit.ContinueWith(ended => ReportExit(ended.Result), TaskScheduler.Default));
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
            string ended = launchExit.Wait(TimeSpan.FromMilliseconds(500)) && launchExit.Result is int code ? $" (the program had ended, with exit code {code})" : "";
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

        // A program's output as it comes, a prompt with no line end included.
        Task Forward(Stream stream, bool isError) => Task.Run(async () =>
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
                _logger?.Invoke($"Error forwarding debuggee output: {ex.Message}");
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

        if (bp.CorBreakpoint is not null)
        {
            try
            {
                bp.CorBreakpoint.Activate(false);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error deactivating breakpoint: {ex.Message}");
            }
        }
        return _breakpointManager.RemoveBreakpoint(id);
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
    /// <paramref name="within"/> when the client gives a time for the let-go.</summary>
    private void ReleaseHeldTarget(TimeSpan? within = null)
    {
        int target = Interlocked.Exchange(ref _heldTarget, 0);
        if (target == 0)
        {
            return;
        }
        string? why = DiagnosticClientHelper.Resume(target, within);
        _logger?.Invoke(why is null ? $"Let process {target} go: the debugger did not take hold of it" : $"Process {target}, not taken hold of, was not let go: {why}");
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
        SetupStepper(thread, type);
        if (type is StepType.StepOver && thread.ActiveFrame is ICorDebugILFrame from && StepRangeOf(from) is var (start, end))
        {
            WatchReturns(thread, from, start, end);
        }
        Continue(clearVariables: true);
    }

    /// <summary>Sets a file's breakpoints to those asked for. One asked for again just as it was (its line, column,
    /// condition, hit count, message and whether it stops) is kept whole: its id, its binding and its hit count, so a hit
    /// of it already on its way still stops, and a change elsewhere in the file starts no count again. Those no longer
    /// asked for are let go, and the new ones bound.</summary>
    public List<BreakpointManager.BreakpointInfo> SetBreakpoints(string filePath, BreakpointRequest[] breakpoints)
    {
        _logger?.Invoke($"SetBreakpoints: {filePath}, breakpoints: {string.Join(",", breakpoints.Select(b => $"L{b.Line} {(b.Column is not null ? $"C{b.Column}" : null)} {(b.Condition is not null ? $"[{b.Condition}]" : null)}{(b.HitCondition is not null ? $" hits {b.HitCondition}" : null)}"))}");

        List<BreakpointManager.BreakpointInfo> existingBreakpoints = _breakpointManager.GetBreakpointsForFile(filePath);
        BreakpointManager.BreakpointInfo?[] kept = new BreakpointManager.BreakpointInfo?[breakpoints.Length];
        for (int i = 0; i < breakpoints.Length; i++)
        {
            kept[i] = existingBreakpoints.FirstOrDefault(b => b.Request == breakpoints[i] && !kept.Contains(b));
        }
        foreach (BreakpointManager.BreakpointInfo gone in existingBreakpoints.Where(b => !kept.Contains(b)))
        {
            if (gone.CorBreakpoint is not null)
            {
                try
                {
                    gone.CorBreakpoint.Activate(false);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Error deactivating breakpoint: {ex.Message}");
                }
            }
            _breakpointManager.RemoveBreakpoint(gone.Id);
        }

        List<BreakpointManager.BreakpointInfo> result = new List<BreakpointManager.BreakpointInfo>();
        for (int i = 0; i < breakpoints.Length; i++)
        {
            if (kept[i] is { } same)
            {
                _logger?.Invoke($"Breakpoint {same.Id} at {filePath}:{breakpoints[i].Line} kept as it was, {same.HitCount} hits in");
                result.Add(same);
                continue;
            }
            BreakpointRequest request = breakpoints[i];
            BreakpointManager.BreakpointInfo bp = _breakpointManager.CreateBreakpoint(filePath, request.Line, request.Column, request.Condition, request.HitCondition, request.LogMessage, request.Suspend);
            bp.Request = request;
            if (request.Condition is not null && SyntaxFactory.ParseExpression(request.Condition).DescendantNodesAndSelf().Any(static node => node is AnonymousFunctionExpressionSyntax))
            {
                bp.Message = "Lambda expressions are not supported in breakpoint conditions.";
                bp.Refused = true;
                result.Add(bp);
                continue;
            }
            if (bp.HitCondition is { } hits && !Passenger.Dap.HitCondition.TryParse(hits, out _, out string? problem))
            {
                bp.Message = problem;
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

    /// <summary>Sets the function breakpoints to those asked for, which replace those set before. One asked for again just as
    /// it was is kept whole, as <see cref="SetBreakpoints"/> keeps a line's: its id, its bindings and its hit count, so a
    /// change beside it (a property breakpoint added) starts no count again and a hit of it on its way still stops.</summary>
    public List<BreakpointManager.BreakpointInfo> SetFunctionBreakpoints(FunctionBreakpointRequest[] breakpoints)
    {
        List<BreakpointManager.BreakpointInfo> existing = _breakpointManager.GetFunctionBreakpoints();
        BreakpointManager.BreakpointInfo?[] kept = new BreakpointManager.BreakpointInfo?[breakpoints.Length];
        for (int i = 0; i < breakpoints.Length; i++)
        {
            kept[i] = existing.FirstOrDefault(b => b.FunctionRequest == breakpoints[i] && !kept.Contains(b));
        }
        foreach (BreakpointManager.BreakpointInfo gone in existing.Where(b => !kept.Contains(b)))
        {
            foreach (BreakpointManager.FunctionBreakpointBinding binding in gone.FunctionBindings)
            {
                try { binding.CorBreakpoint.Activate(false); }
                catch (Exception ex) { _logger?.Invoke($"Error deactivating function breakpoint: {ex.Message}"); }
            }
            _breakpointManager.RemoveBreakpoint(gone.Id);
        }

        List<BreakpointManager.BreakpointInfo> result = new List<BreakpointManager.BreakpointInfo>();
        for (int i = 0; i < breakpoints.Length; i++)
        {
            if (kept[i] is { } same)
            {
                _logger?.Invoke($"Function breakpoint {same.Id} on {same.FunctionName} kept as it was, {same.HitCount} hits in");
                result.Add(same);
                continue;
            }
            FunctionBreakpointRequest request = breakpoints[i];
            BreakpointManager.BreakpointInfo bp = _breakpointManager.CreateFunctionBreakpoint(request.Name, request.Condition, request.HitCondition, request.LogMessage, request.Suspend);
            bp.FunctionRequest = request;
            if (bp.HitCondition is { } hits && !Passenger.Dap.HitCondition.TryParse(hits, out _, out string? problem))
            {
                bp.Message = problem;
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
        catch (Exception ex)
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
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A thread's frames from <paramref name="startFrame"/>, as many as asked: its own, then the callers of its
    /// async frames, which only their state machines know.</summary>
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
            long endFrame = levels is null ? long.MaxValue : (long)startFrame + levels.Value;
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
            _logger?.Invoke($"Error getting stack trace: {ex.Message}");
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
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or BadImageFormatException)
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
                await AddMembers(stateMachine, stateMachine.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.StackVariable)
            {
                if (variablesReference.DebuggerProxyInstance is not null)
                {
                    ICorDebugObjectValue objectValue = variablesReference.DebuggerProxyInstance.UnwrapDebugValueToObject();
                    await AddMembersAndStaticPseudoVariable(variablesReference.DebuggerProxyInstance, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, false);
                    int rawValueVariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.RawView, variablesReference.ObjectValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, null));
                    VariableInfo rawValuePseudoVariable = new VariableInfo
                    {
                        Name = "Raw View",
                        Value = "",
                        Type = "",
                        PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
                        VariablesReference = rawValueVariablesReference
                    };
                    result.Add(rawValuePseudoVariable);
                    return result;
                }
                ICorDebugValue unwrappedDebugValue = variablesReference.ObjectValue!.UnwrapDebugValue();

                if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
                {
                    await AddArrayElements(arrayValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, referenceValue: variablesReference.ObjectValue);
                }
                else if (unwrappedDebugValue is ICorDebugObjectValue objectValue)
                {
                    if (IsEnumerable(objectValue.ExactType))
                    {
                        if (EfQueryModule(objectValue.ExactType) is not null)
                        {
                            AddQuerySqlRow(variablesReference, result);
                        }
                        AddEnumerablePseudoVariables(variablesReference, result);
                    }
                    else
                    {
                        await AddMembersAndStaticPseudoVariable(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
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
                await AddMembersAndStaticPseudoVariable(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.EnumerableResults)
            {
                await AddEnumerableResults(variablesReference, result);
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.QuerySql)
            {
                result.Add(await ReadQuerySql(variablesReference));
            }
            else if (variablesReference.ReferenceKind is StoredReferenceKind.ArrayRange)
            {
                ICorDebugArrayValue arrayValue = (ICorDebugArrayValue)variablesReference.ObjectValue!.UnwrapDebugValue();
                await AddArrayElements(arrayValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, variablesReference.ArrayIndices, variablesReference.ArrayStartOffset, variablesReference.ArrayCount, variablesReference.ObjectValue);
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
    /// even once implicit evaluation is off; one the IDE asks of itself (a hover) does not.</summary>
    public async Task<VariableInfo> Evaluate(string expression, int? frameId, bool implicitly = false)
    {
        EvalStatus.Explicit = !implicitly;
        try
        {
            return await EvaluateInFrame(expression, frameId);
        }
        finally
        {
            EvalStatus.Explicit = false;
        }
    }

    private async Task<VariableInfo> EvaluateInFrame(string expression, int? frameId)
    {
        _logger?.Invoke($"Evaluate: {expression}");
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

        CompiledExpressionEvaluationContext evalContext = new CompiledExpressionEvaluationContext(thread, threadId, frameStackDepth);
        ArgumentNullException.ThrowIfNull(_expressionEvaluator);
        using EvaluationResult result = await _expressionEvaluator.Evaluate(expression, evalContext);

        if (result.Error is not null)
        {
            _logger?.Invoke($"Evaluation error: {result.Error}");
            return new VariableInfo
            {
                Name = null!,
                Value = result.Error,
                Type = null,
                PresentationHint = new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation },
                VariablesReference = 0
            };
        }
        (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(result.Value!, threadId, frameStackDepth, true);
        VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
        int variablesReference = GetVariablesReference(result.Value!, friendlyTypeName, threadId, frameStackDepth, debuggerProxyInstance);
        VariableInfo variableInfo = new VariableInfo
        {
            Name = null!,
            Value = value,
            Type = friendlyTypeName,
            PresentationHint = variablePresentationHint,
            VariablesReference = variablesReference
        };
        if (variablesReference != 0)
        {
            result.RelinquishResultHandleOwnership();
        }

        return variableInfo;
    }

    /// <summary>Ends the program and lets go of it, once the runtime has seen it go.</summary>
    public void Terminate()
    {
        _logger?.Invoke("Terminate");
        if (_process is not null && !_exited.IsSet)
        {
            try
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
                _process.Terminate(0);
                // The process goes on the runtime's thread; letting go of it before it has would race that.
                if (!_exited.Wait(TimeSpan.FromSeconds(5)))
                {
                    _logger?.Invoke("The process had not exited 5 s after Terminate");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error terminating process: {ex.Message}");
            }
        }
        Dispose();
    }

    /// <summary>Ends the session: the program ends with it when asked, else it is let go to run on, the let-go kept within
    /// the client's time for it when it gives one (<paramref name="within"/>).</summary>
    public void Disconnect(bool terminateDebuggee, TimeSpan? within = null)
    {
        _logger?.Invoke($"Disconnect (terminate: {terminateDebuggee})");

        if (terminateDebuggee)
        {
            Terminate();
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
            Dispose(letGo: true, within);
        }
    }

    /// <summary>The client went without disconnecting (crashed, or was ended): a program the session launched ends with it,
    /// and one it attached to is let go to run on, as a detach lets it go.</summary>
    public void Abandon() => Disconnect(terminateDebuggee: _launched);

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
        // No code runs to read it once an evaluation has timed out, until the program runs on.
        if (EvalStatus.ImplicitOff is { } off)
        {
            return UnevaluatedExceptionInfo(StopFrame(thread), threadId, currentException, off);
        }

        FrameStackDepth frameStackDepth = new FrameStackDepth(0);
        try
        {
            (string? friendlyTypeName, _, _, _) = await GetValueForCorDebugValueAsync(currentException, threadId, frameStackDepth, false);

            string source = await GetExceptionPropertyValue(currentException, "Source");
            string message = await GetExceptionPropertyValue(currentException, "Message");
            ExceptionInfo.ExceptionDetails details = await Details(currentException, friendlyTypeName, "$exception", depth: 0);
            string innermost = Innermost(details);

            ExceptionInfo exceptionInfo = new ExceptionInfo
            {
                ExceptionId = $"CLR/{friendlyTypeName}",
                // With an inner exception, a line naming the innermost.
                Description = $"Exception thrown: '{friendlyTypeName}' in {source}.dll: '{message}'"
                    + (details.InnerException.Count > 0 ? $"\n Inner exceptions found, see $exception in variables window for more details.\n Innermost exception: {innermost}" : ""),
                BreakMode = _exceptionBreakModes.GetValueOrDefault(threadId, ExceptionBreakMode.Unknown),
                Code = 0,
                Details = details,
            };
            return exceptionInfo;
        }
        catch (EvalRefusedException error) when (error.HResult is Cor.CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT or Cor.CORDBG_E_FUNC_EVAL_BAD_START_POINT)
        {
            // A thread whose exception came while another thread's evaluation ran is held where no code can run on it.
            return UnevaluatedExceptionInfo(StopFrame(thread), threadId, currentException,
                "It was thrown while the debugger ran code on another thread, so its thread is held where no code can run, and its properties cannot be read.");
        }

        // An exception's details, its inner exceptions' within, as deep as they go, within reason.
        async Task<ExceptionInfo.ExceptionDetails> Details(ICorDebugValue exception, string typeName, string evaluateName, int depth)
        {
            string detailMessage = await GetExceptionPropertyValue(exception, "Message");
            List<ExceptionInfo.ExceptionDetails> inner = [];
            ICorDebugValue? innerValue = depth < 10 ? await exception.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, StopFrame(thread), "InnerException") : null;
            try
            {
                if (innerValue is not null && innerValue.UnwrapDebugValue() is not ICorDebugReferenceValue { IsNull: true })
                {
                    (string? innerType, _, _, _) = await GetValueForCorDebugValueAsync(innerValue, threadId, frameStackDepth, false);
                    inner.Add(await Details(innerValue, innerType ?? "System.Exception", $"{evaluateName}.InnerException", depth + 1));
                }
            }
            finally
            {
                if (innerValue is ICorDebugHandleValue innerHandle)
                {
                    innerHandle.TryDispose();
                }
            }
            int lastDot = typeName.LastIndexOf('.');
            return new ExceptionInfo.ExceptionDetails
            {
                Message = detailMessage,
                TypeName = lastDot is not -1 ? typeName[(lastDot + 1)..] : typeName,
                FullTypeName = typeName,
                EvaluateName = evaluateName,
                StackTrace = await GetExceptionPropertyValue(exception, "StackTrace"),
                InnerException = inner,
                FormattedDescription = $"**{typeName}:** '{detailMessage}'",
                HResult = int.Parse(await GetExceptionPropertyValue(exception, "HResult")),
                Source = await GetExceptionPropertyValue(exception, "Source"),
            };
        }

        static string Innermost(ExceptionInfo.ExceptionDetails details) =>
            details.InnerException.Count > 0 ? Innermost(details.InnerException[0]) : details.FullTypeName;

        async Task<string> GetExceptionPropertyValue(ICorDebugValue exception, string propertyName)
        {
            ICorDebugValue propertyValue = await exception.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, StopFrame(thread), propertyName)
                ?? throw new InvalidOperationException($"Exception property '{propertyName}' returned no value");
            try
            {
                (_, string? value, _, _) = await GetValueForCorDebugValueAsync(propertyValue, threadId, frameStackDepth, false);
                return value;
            }
            finally
            {
                if (propertyValue is ICorDebugHandleValue handle)
                {
                    handle.TryDispose();
                }
            }
        }
    }

    /// <summary>The top frame of a thread's stop, found again for each use, as running code neuters a frame: an evaluation that
    /// could not be stopped, still running on the thread, has its frames above it.</summary>
    private static ICorDebugILFrame StopFrame(ICorDebugThread thread) =>
        EnumerateFramesForThread(thread).FirstOrDefault() as ICorDebugILFrame
            ?? throw new InvalidOperationException("The thread is stopped outside managed code, so its exception cannot be read");

    /// <summary>An exception as its own fields give it, read without running code on its thread: its type and the message
    /// it was made with, and why there is no more.</summary>
    private ExceptionInfo UnevaluatedExceptionInfo(ICorDebugILFrame frame, ThreadId threadId, ICorDebugValue exception, string why)
    {
        string typeName = GetCorDebugTypeFriendlyName(exception.ExactType);
        ICorDebugValue? field = exception.UnwrapDebugValueToObjectOrNull()?.GetClassFieldValue(frame, "_message");
        string message = field?.UnwrapDebugValue() is ICorDebugStringValue text ? text.String : "";
        int lastDot = typeName.LastIndexOf('.');
        return new ExceptionInfo
        {
            ExceptionId = $"CLR/{typeName}",
            Description = $"Exception thrown: '{typeName}': '{message}'\n {why}",
            BreakMode = _exceptionBreakModes.GetValueOrDefault(threadId, ExceptionBreakMode.Unknown),
            Code = 0,
            Details = new ExceptionInfo.ExceptionDetails
            {
                Message = message,
                TypeName = lastDot is not -1 ? typeName[(lastDot + 1)..] : typeName,
                FullTypeName = typeName,
                EvaluateName = "$exception",
                StackTrace = "",
                InnerException = [],
                FormattedDescription = $"**{typeName}:** '{message}'",
                HResult = 0,
                Source = "",
            },
        };
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
    /// so every unhandled exception stops). One asked for again just as it was keeps its hits, as a line breakpoint does.</summary>
    public IReadOnlyList<ExceptionRule> SetExceptionBreakpoints(IReadOnlyList<ExceptionBreakpointRequest> filters, IReadOnlyList<ExceptionRuleRequest>? rules = null)
    {
        _exceptionBreakpoints = filters.ToArray();
        if (rules is null)
        {
            _exceptionRules = null;
            return [];
        }
        List<ExceptionRule> before = [.. _exceptionRules ?? []];
        List<ExceptionRule> now = [];
        foreach (ExceptionRuleRequest request in rules)
        {
            int same = before.FindIndex(rule => rule.Request.Equals(request));
            if (same >= 0)
            {
                now.Add(before[same]);
                before.RemoveAt(same);
                continue;
            }
            now.Add(new ExceptionRule(_breakpointManager.NextId(), request) { Refused = Unreadable(request.Condition, request.HitCondition) });
        }
        _exceptionRules = now;
        return now;
    }

    /// <summary>Why the debugger will not take a breakpoint's condition or hit count as it is, if it will not.</summary>
    private static string? Unreadable(string? condition, string? hitCondition)
    {
        if (condition is not null && SyntaxFactory.ParseExpression(condition).DescendantNodesAndSelf().Any(static node => node is AnonymousFunctionExpressionSyntax))
        {
            return "Lambda expressions are not supported in breakpoint conditions.";
        }
        return hitCondition is { } hits && !Passenger.Dap.HitCondition.TryParse(hits, out _, out string? problem) ? problem : null;
    }
}
