using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private ICorDebug? _corDebug;
    private ICorDebugProcess? _process;

    /// <summary>Set on the runtime's callback thread the moment the debuggee is gone, before the event is
    /// queued: from then on mscordbi neuters the process on its own thread and shuts its transport down, and
    /// a call into the process races that and can crash the adapter (a Stop or Detach sent over a transport
    /// being torn down).</summary>
    private readonly ManualResetEventSlim _exited = new();
    private readonly CorDebugManagedCallback _callbacks;
    private readonly BreakpointManager _breakpointManager;
    private readonly VariableManager _variableManager;
    private readonly FrameReferenceManager _frameReferenceManager;
    private readonly Action<string>? _logger;
    private sealed class ThreadInfo(ICorDebugThread thread)
    {
        public ICorDebugThread Thread { get; } = thread;
        public string? Name { get; set; }
    }

    private readonly Dictionary<int, ThreadInfo> _threads = new();
    private int? _mainThreadId;
    /// <summary>The operating system's thread the launched program was started on, which runs its Main, where it can be told.</summary>
    private int? _initialThreadId;
    private readonly Dictionary<CORDB_ADDRESS, ModuleInfo> _modules = new();
    private readonly HashSet<COR_TYPEID> _initializedStaticTypes = [];
    private ICorDebugFunction? _suppressFinalizeFunction;
    /// <summary>
    /// Monotonically increasing version of the set of loaded debuggee modules. Incremented whenever a module
    /// is loaded or unloaded, so anything derived from <see cref="AllModules"/> (the expression compile cache and the
    /// metadata-blocks cache) can detect staleness and rebuild.
    /// </summary>
    internal int ModuleSet_Version { get; private set; }
    private bool _isAttached;
    private int? _pendingAttachProcessId;
    private bool _justMyCode;
    private bool _stopAtEntry;
    private EntryBreakpoint? _entryBreakpoint;
    private sealed record EntryBreakpoint(ICorDebugFunctionBreakpoint CorBreakpoint, CORDB_ADDRESS ModuleBaseAddress, int MethodToken, int IlOffset);

    // The active exception filters and optional type conditions supplied by the DAP client.
    private IReadOnlyList<ExceptionBreakpointRequest> _exceptionBreakpoints = [];
    // The client's CLR exception breakpoints, or null when it has given none (and every unhandled exception stops).
    private IReadOnlyList<ExceptionRule>? _exceptionRules;
    // Per-thread state retained between callbacks for the same exception propagation sequence.
    private readonly Dictionary<ThreadId, ExceptionPropagationState> _exceptionStates = [];
    // The mode that caused each thread's latest exception stop, used by the exceptionInfo response.
    private readonly Dictionary<ThreadId, ExceptionBreakMode> _exceptionBreakModes = [];
    private AsyncStepper? _asyncStepper;
    private CilExpressionEvaluator _expressionEvaluator = null!;

    /// <summary>A process attached to, held on Windows (its handle open, so its id is never another's meanwhile) for the
    /// exit code the system tells a process that is not its parent.</summary>
    private Process? _attached;

    /// <summary>The program the adapter launched, and how it ended once it has.</summary>
    private SuspendedLaunch? _launch;
    private Task<int?>? _launchExit;
    private Task? _launchOutput;

    /// <summary>When the launched program's output was last read (a <see cref="Stopwatch"/> timestamp).</summary>
    private long _lastOutputAt;
    private int _exitReported;
    private TaskCompletionSource _exitTold = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Tells the client the program has ended, once, after the last of its output. A second caller (the runtime's
    /// word of the end, after the wait's) returns only once it has been told, so the end it tells next comes after.</summary>
    private void ReportExit(int? exitCode)
    {
        if (Interlocked.Exchange(ref _exitReported, 1) == 1)
        {
            _exitTold.Task.Wait(TimeSpan.FromSeconds(15));
            return;
        }
        try
        {
            WaitForLastOutput();
            OnExited?.Invoke(exitCode);
        }
        finally
        {
            _exitTold.TrySetResult();
        }
    }

    /// <summary>Waits for the launched program's output readers to end, while its output still comes: a program it started
    /// that holds its output open gives up the wait once a second passes with nothing read, and 10 s at the most.</summary>
    private void WaitForLastOutput()
    {
        if (_launchOutput is not { } output)
        {
            return;
        }
        long began = Stopwatch.GetTimestamp();
        while (!output.Wait(TimeSpan.FromMilliseconds(100)))
        {
            long quietSince = Math.Max(began, Interlocked.Read(ref _lastOutputAt));
            if (Stopwatch.GetElapsedTime(quietSince) >= TimeSpan.FromSeconds(1) || Stopwatch.GetElapsedTime(began) >= TimeSpan.FromSeconds(10))
            {
                return;
            }
        }
    }

    /// <summary>How the program ended, once the runtime has said it has: a launched one's as its wait saw it, an attached
    /// one's as Windows tells it; null where it cannot be known (attached on macOS or Linux, where only a parent learns it).</summary>
    private int? EndedWith()
    {
        // The program's own end follows the runtime's word of it by a moment.
        if (_launchExit is { } ended)
        {
            return ended.Wait(TimeSpan.FromSeconds(2)) ? ended.Result : null;
        }
        try
        {
            return OperatingSystem.IsWindows() && _attached is { } attached && attached.WaitForExit(TimeSpan.FromSeconds(2)) ? attached.ExitCode : null;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Opens a process's handle and keeps it open with the process; false when the system will not open it.</summary>
    private static bool TryHold(Process process)
    {
        try
        {
            _ = process.SafeHandle;
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Every event below is raised on any thread (the runtime's callback thread, a request's, an output reader).
    public event Action<StopInfo>? OnStopped;
    public event Action<int?>? OnExited;
    public event Action? OnTerminated;
    public event Action<int>? OnThreadStarted;
    public event Action<int>? OnThreadExited;
    public event Action<string, string, string>? OnModuleLoaded;
    public event Action<string, string, string>? OnModuleUnloaded;
    // Output text, isError (true for stderr, false for stdout)
    public event Action<string, bool>? OnOutput;
    /// <summary>What the debugger itself has to tell the user, beside the program's output.</summary>
    public event Action<string>? OnConsole;
    public event Action<int, string>? OnProcessStarted;

    /// <summary>The debugger has hold of a process it attached to, told before configurationDone answers.</summary>
    public event Action<int>? OnAttached;
    public event Action<BreakpointManager.BreakpointInfo>? OnBreakpointChanged;
    /// <summary>Asks the client to start a program in its terminal, held at its start, and answers with its process; it
    /// fails, rather than waits for good, when the client never answers.</summary>
    public event Func<LaunchInfo, Task<int>> SendRunInTerminalRequest = null!;

    internal EvalStatus EvalStatus { get; }

    private readonly Channel<CorDebugManagedCallbackEventArgs> _runtimeEventChannel;
    public readonly EngineLock DapRequestAndRuntimeEventLock = new();

    /// <summary>Where symbols decompiled for assemblies without their own are kept; none without it, a frame of such an
    /// assembly then shown without source.</summary>
    private readonly Decompilation.SymbolCache? _symbols;

    /// <summary>Each assembly's decompile this session, by its version (MVID), running or done; locked itself, as a
    /// decompile is asked for under the engine's lock and ends off it.</summary>
    private readonly Dictionary<Guid, Task<bool>> _decompiling = [];

    /// <summary>Cancelled as the session ends, which gives up a decompile under way.</summary>
    private readonly CancellationTokenSource _ending = new();

    /// <summary>Cancelled without the engine's lock: a startup waiting on the runtime holds that lock itself.</summary>
    private readonly CancellationTokenSource _starting = new();

    public void CancelStartup() => _starting.Cancel();

    /// <summary>How often the program has been let go, so an event put off for a decompile is dropped when it was let go
    /// meanwhile (<see cref="AfterDecompiling"/>).</summary>
    private int _continued;

    public ManagedDebugger(Action<string>? logger = null, Decompilation.SymbolCache? symbols = null)
    {
        _logger = logger;
        _symbols = symbols;
        _breakpointManager = new BreakpointManager();
        _variableManager = new VariableManager();
        _frameReferenceManager = new FrameReferenceManager();
        _callbacks = new CorDebugManagedCallback();
        EvalStatus = new EvalStatus();
        _asyncStepper = new AsyncStepper(_modules, this);
        // The runtime's callback thread writes, and so does whichever thread hands back an event put off during an evaluation.
        _runtimeEventChannel = Channel.CreateUnbounded<CorDebugManagedCallbackEventArgs>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        _callbacks.OnAnyEvent += QueueEvent;
        _ = Task.Run(ProcessRuntimeEventQueue);
    }

    private void QueueEvent(object? sender, CorDebugManagedCallbackEventArgs e)
    {
        if (e is ExitProcessCorDebugManagedCallbackEventArgs)
        {
            _exited.Set();
        }
        _runtimeEventChannel.Writer.TryWrite(e);
    }

    /// <summary>Handles the runtime's events queued so far, for a request about to run. The caller holds the engine's lock,
    /// which is not re-entrant: taking it again here would wait for good.</summary>
    public async Task DrainRuntimeEventQueue()
    {
        ChannelReader<CorDebugManagedCallbackEventArgs> reader = _runtimeEventChannel.Reader;
        while (reader.TryRead(out CorDebugManagedCallbackEventArgs? callbackEvent))
        {
            await OnAnyEvent(this, callbackEvent).ConfigureAwait(false);
        }
    }

    private async Task ProcessRuntimeEventQueue()
    {
        try
        {
            ChannelReader<CorDebugManagedCallbackEventArgs> reader = _runtimeEventChannel.Reader;
            while (await reader.WaitToReadAsync())
            {
                // If a Dap request has obtained the lock, we will pause here. It will drain runtime events, and our TryRead may return false, which is fine
                using (await DapRequestAndRuntimeEventLock.LockAsync())
                {
                    if (reader.TryRead(out CorDebugManagedCallbackEventArgs? callbackEvent) is false)
                    {
                        continue;
                    }

                    await OnAnyEvent(this, callbackEvent).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e)
        {
            _logger?.Invoke($"Critical failure processing runtime event queue, no further events will be processed: {e}");
            throw;
        }
    }

    /// <summary>Handles the runtime's events while an evaluation runs, under the request's hold of the engine's lock, until
    /// the evaluation's end; that of one given up earlier is let go on the way. Ends when the evaluation is given up.</summary>
    internal async Task<CorDebugManagedCallbackEventArgs> ProcessRuntimeEventsUntilEvalEvent()
    {
        CancellationToken givenUp = EvalStatus.Reading;
        ChannelReader<CorDebugManagedCallbackEventArgs> reader = _runtimeEventChannel.Reader;
        while (await reader.WaitToReadAsync(givenUp))
        {
            if (reader.TryRead(out CorDebugManagedCallbackEventArgs? callbackEvent) is false)
            {
                throw new InvalidOperationException("Expected to read an event from the runtime event queue, but none was available");
            }

            bool abandoned = IsAbandonedEvaluationsEnd(callbackEvent);
            await OnAnyEvent(this, callbackEvent).ConfigureAwait(false);
            if (callbackEvent is EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs && !abandoned)
            {
                return callbackEvent;
            }
        }
        throw new InvalidOperationException("Expected to read an eval event from the runtime event queue, but Channel completed unexpectedly");
    }

    private static ICorDebugThread? EvaluationThread(CorDebugManagedCallbackEventArgs callbackEvent) => callbackEvent switch
    {
        EvalCompleteCorDebugManagedCallbackEventArgs complete => complete.Thread,
        EvalExceptionCorDebugManagedCallbackEventArgs exception => exception.Thread,
        _ => null,
    };

    private bool IsAbandonedEvaluationsEnd(CorDebugManagedCallbackEventArgs callbackEvent) =>
        EvaluationThread(callbackEvent) is { } thread && EvalStatus.Abandoned.ContainsKey(thread.Id);

    /// <summary>The end of an evaluation given up because it could not be stopped, come at last: its value is let go, its
    /// thread is free for evaluations again, and the program goes on, from the stop the evaluation began at.</summary>
    private void LetGoAbandonedEvaluation(CorDebugManagedCallbackEventArgs callbackEvent)
    {
        ICorDebugThread thread = EvaluationThread(callbackEvent)!;
        if (EvalStatus.Abandoned.Remove(thread.Id, out ICorDebugEval? eval) && eval.TryGetResult(out ICorDebugValue? value) is Cor.S_OK && value is ICorDebugHandleValue handle)
        {
            handle.TryDispose();
        }
        _logger?.Invoke($"The evaluation given up on thread {thread.Id} has returned; its end is let go");
        if (_stepAfterEvaluation is (int stepping, StepType type) && stepping == thread.Id)
        {
            // The evaluation's session is over (the runtime ends it before it tells of the end), so this stepper is the stop's.
            _stepAfterEvaluation = null;
            SetupStepper(thread, type, EnumerateFramesForThread(thread).First());
        }
        Continue(clearVariables: false);
    }

    /// <summary>An event put off while the symbols of the code its thread stopped in were decompiled off the engine's lock,
    /// handed back to the event loop once they were, the program held where the event found it meanwhile; dropped if the
    /// program was let go since, as nothing it said holds then.</summary>
    private sealed class AfterDecompiling(CorDebugManagedCallbackEventArgs held, int continued) : CorDebugManagedCallbackEventArgs
    {
        public CorDebugManagedCallbackEventArgs Held { get; } = held;
        public int Continued { get; } = continued;
    }

    /// <summary>Without Just My Code every stop shows its code, decompiled where the assembly has no symbols: an event that
    /// may stop in such code (a breakpoint, a step's end, a data breakpoint) first waits for that decompile, which runs off
    /// the engine's lock so a request meanwhile (a disconnect) is answered. True when the event was put off.</summary>
    private bool DecompileFirst(CorDebugManagedCallbackEventArgs e)
    {
        // During an evaluation an event is put off as it is (PutOffDuringEvaluation), and decompiled for once handed back.
        if (_justMyCode || _symbols is null || EvalStatus.IsRunning)
        {
            return false;
        }
        ICorDebugThread? thread = e switch
        {
            BreakpointCorDebugManagedCallbackEventArgs breakpoint => breakpoint.Thread,
            StepCompleteCorDebugManagedCallbackEventArgs step => step.Thread,
            DataBreakpointCorDebugManagedCallbackEventArgs data => data.Thread,
            _ => null,
        };
        SymbolsWanted? wanted;
        try
        {
            wanted = thread?.ActiveFrame is ICorDebugILFrame frame && FindModule(frame.Function.Module) is { } module ? WantedFor(module) : null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return false;
        }
        if (wanted is null)
        {
            return false;
        }
        int continued = _continued;
        _logger?.Invoke($"{e.GetType().Name} waits for {wanted.Name}'s symbols to be decompiled");
        DecompileAsync(wanted).ContinueWith(_ => _runtimeEventChannel.Writer.TryWrite(new AfterDecompiling(e, continued)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    private async Task OnAnyEvent(object? sender, CorDebugManagedCallbackEventArgs e)
    {
        try
        {
            if (e is AfterDecompiling after)
            {
                if (after.Continued != _continued)
                {
                    _logger?.Invoke($"{after.Held.GetType().Name}, put off for a decompile, is dropped: the program was let go meanwhile");
                    return;
                }
                e = after.Held;
            }
            else if (DecompileFirst(e))
            {
                return;
            }
            _logger?.Invoke($"Event: {e.GetType().Name}");
            switch (e)
            {
                case LogMessageCorDebugManagedCallbackEventArgs a: HandleLogMessage(sender, a); break;
                case CreateProcessCorDebugManagedCallbackEventArgs a: HandleProcessCreated(sender, a); break;
                case ExitProcessCorDebugManagedCallbackEventArgs a: HandleProcessExited(sender, a); break;
                case CreateThreadCorDebugManagedCallbackEventArgs a: HandleThreadCreated(sender, a); break;
                case ExitThreadCorDebugManagedCallbackEventArgs a: HandleThreadExited(sender, a); break;
                case LoadModuleCorDebugManagedCallbackEventArgs a: HandleModuleLoaded(sender, a); break;
                case LoadClassCorDebugManagedCallbackEventArgs a: HandleClassLoaded(a); break;
                case UnloadModuleCorDebugManagedCallbackEventArgs a: HandleModuleUnloaded(sender, a); break;
                case BreakpointCorDebugManagedCallbackEventArgs a: await HandleBreakpoint(sender, a).ConfigureAwait(false); break;
                case StepCompleteCorDebugManagedCallbackEventArgs a: await HandleStepComplete(sender, a).ConfigureAwait(false); break;
                case BreakCorDebugManagedCallbackEventArgs a: HandleBreak(sender, a); break;
                case ExceptionCorDebugManagedCallbackEventArgs a: HandleException(sender, a); break;
                case Exception2CorDebugManagedCallbackEventArgs a: await HandleException2(sender, a).ConfigureAwait(false); break;
                case DeferredExceptionStop a: await StopAtExceptionIfHit(a).ConfigureAwait(false); break;
                case DataBreakpointCorDebugManagedCallbackEventArgs a: await HandleDataBreakpoint(a).ConfigureAwait(false); break;
                case FunctionRemapOpportunityCorDebugManagedCallbackEventArgs a: HandleFunctionRemapOpportunity(a); break;
                case FunctionRemapCompleteCorDebugManagedCallbackEventArgs a: HandleFunctionRemapComplete(a); break;
                case EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs when IsAbandonedEvaluationsEnd(e): LetGoAbandonedEvaluation(e); break;
                case EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs: break; // don't continue on these, as they are being used for expression evaluation
                default:
                    if (_process is not null)
                    {
                        Continue(clearVariables: false);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error handling event {e.GetType().Name}: {ex}");
            if (_process is not null && _process.TryIsRunning(out bool isRunning) is Cor.S_OK && isRunning is false)
            {
                Continue(clearVariables: false);
            }
        }
    }

    private void HandleLogMessage(object? sender, LogMessageCorDebugManagedCallbackEventArgs logMessageEvent)
    {
        _logger?.Invoke($"Log: {logMessageEvent.Message}");
        Continue(clearVariables: false);
    }

    /// <summary>Takes hold of a process before configurationDone answers, under the engine's lock: a running one where it is,
    /// one held at its start once the debugger is registered for its runtime and has let it go (<paramref name="resume"/>),
    /// so a client that lets it go itself once answered finds the debugger there, and none of its events is handled before
    /// the process is the engine's. A failure answers the request with its reason.</summary>
    private async Task PerformAttach(int processId, Func<Process, Task, CancellationToken, Task> resume)
    {
        _logger?.Invoke($"Attaching to process: {processId}");
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"There is no process {processId} to attach to");
        }
        try
        {
            _exited.Reset();
            (_corDebug, _process) = await ClrDebugExtensions.Automatic(processId, answered => resume(process, answered, _starting.Token), TakeHold, _logger, _starting.Token);
        }
        catch (Exception error)
        {
            process.Dispose();
            _logger?.Invoke($"Attaching to process {processId} failed: {error}");
            throw new InvalidOperationException($"Could not attach to process {processId}: {error.Message}", error);
        }
        if (OperatingSystem.IsWindows() && TryHold(process))
        {
            _attached = process;
        }
        else
        {
            process.Dispose();
        }
        _isAttached = true;
        ConfigureExceptionCallbacks();

        _logger?.Invoke($"Attached to process: {processId}");
        OnAttached?.Invoke(processId);
        SendAllBreakpointEvents();

        ICorDebugProcess TakeHold(ICorDebug corDebug)
        {
            corDebug.Initialize();
            corDebug.SetManagedHandler(_callbacks);
            _logger?.Invoke($"Taking hold of process {processId}");
            try
            {
                return corDebug.DebugActiveProcess(processId, false);
            }
            catch
            {
                corDebug.TryTerminate();
                throw;
            }
        }
    }

    private void SendAllBreakpointEvents()
    {
        // Send a breakpoint changed event with verified false for every breakpoint, so the IDE can mark the BP as unverified, until it receives our later BP events when we bind them
        foreach (BreakpointManager.BreakpointInfo bp in _breakpointManager.GetAllBreakpoints())
        {
            OnBreakpointChanged?.Invoke(bp);
        }
    }

    /// <summary>Lets the program go on, unless another thread's event put off during an evaluation is handled first, or a
    /// pause waits to be taken there; during an evaluation it only lets that go on. <paramref name="clearVariables"/> when
    /// the program runs on from a stop or from an event that does not stop it: the stop's values and frames go with it,
    /// and a pause asked for meanwhile. <paramref name="allowSuperfluous"/> for the client's continue of a program running.</summary>
    private void Continue(bool clearVariables, bool allowSuperfluous = false)
    {
        ArgumentNullException.ThrowIfNull(_process);
        if (clearVariables)
        {
            _stopShown = false;
            _pauseRequested = false;
            EvalStatus.ImplicitOff = null;
            _unhandledByUser = null;
            _variableManager.ClearAndDisposeHandleValues();
            _frameReferenceManager.Clear();
            _stepInTargets.Clear();
            _writtenAt = null;
            _stoppedShort = null;
        }
        if (!EvalStatus.IsRunning && (TakeDeferredEvent() || TakePendingPause()))
        {
            return;
        }
        int result = _process.TryContinue(false);
        if (result is Cor.CORDBG_E_SUPERFLOUS_CONTINUE && allowSuperfluous)
        {
            return;
        }
        Marshal.ThrowExceptionForHR(result);
        _continued++;
    }

    /// <summary>Set once the client is told the program has stopped, until it lets the program go on (a continue, a step):
    /// an event that comes meanwhile, which the runtime had on its way as a pause stopped the program, waits until then.</summary>
    private bool _stopShown;

    private void ReportStop(int threadId, string reason, string? filePath = null, int line = 0, int column = 0, IReadOnlyList<int>? hitBreakpointIds = null, string? text = null)
    {
        // A step's calls' values are shown at its own stop; one that stops anywhere else stops showing them.
        EndReturnWatch();
        EndSmartStep();
        if (reason != "step")
        {
            ForgetReturnedValues();
        }
        _stopShown = true;
        // A pause holds threads where they stand, and an exception inside its managed dispatch, neither at a patch.
        _stoppedInDispatch = reason is "pause" or "exception" ? null : threadId;
        OnStopped?.Invoke(new StopInfo(threadId, reason, filePath, line, column, hitBreakpointIds, text));
    }

    /// <summary>Hands the first event put off during an evaluation to the event loop in place of a continue, the process
    /// still stopped and its thread still where the event found it; true when there was one. None is handed over while a
    /// stop is shown.</summary>
    private bool TakeDeferredEvent()
    {
        if (EvalStatus.IsRunning || _stopShown || !_deferredEvents.TryDequeue(out CorDebugManagedCallbackEventArgs? later))
        {
            return false;
        }
        _runtimeEventChannel.Writer.TryWrite(later);
        return true;
    }

    /// <summary>Other threads' events queued with the one an evaluation began from, which the runtime hands over before the
    /// evaluation can run: their threads stay suspended where they were until each is handled, in order.</summary>
    private readonly Queue<CorDebugManagedCallbackEventArgs> _deferredEvents = new();

    /// <summary>An exception that may stop the program: a filter's stop (<see cref="Stops"/>), or its exception breakpoints'
    /// once their hit counts and conditions are tried. Put off as one on another thread when found while an evaluation ran.</summary>
    private sealed class DeferredExceptionStop(ICorDebugThread thread, ExceptionBreakMode breakMode, StackRange? handler, string exceptionType, bool stops,
        IReadOnlyList<ExceptionRule> rules, ICorDebugFrame? stepPast) : CorDebugManagedCallbackEventArgs
    {
        public ICorDebugThread Thread { get; } = thread;
        public ExceptionBreakMode BreakMode { get; } = breakMode;
        public StackRange? Handler { get; } = handler;
        public string ExceptionType { get; } = exceptionType;
        public bool Stops { get; } = stops;
        public IReadOnlyList<ExceptionRule> Rules { get; } = rules;

        /// <summary>A handler outside the user's code that a step under way steps past if the exception does not stop.</summary>
        public ICorDebugFrame? StepPast { get; } = stepPast;
    }

    private void ConfigureExceptionCallbacks()
    {
        if (_process is not ICorDebugProcess8 process8)
        {
            return;
        }

        int result = process8.TryEnableExceptionCallbacksOutsideOfMyCode(!_justMyCode);
        if (result is not Cor.S_OK)
        {
            _logger?.Invoke($"Unable to configure exception callbacks outside user code: {result}");
        }
    }

    private ICorDebugStepper? _stepper;

    /// <summary>A pause that came while the process was held for an event of its own, taken at the next Continue.</summary>
    private bool _pauseRequested;

    /// <summary>A step asked of a thread whose evaluation was given up and runs on, begun once that evaluation returns.</summary>
    private (int ThreadId, StepType Type)? _stepAfterEvaluation;

    /// <summary>A thread stopped at an exception its user's code left unhandled, and the stack range of the frame of the
    /// handler outside that code which caught it, until the program runs on: a step from the stop steps out of it.</summary>
    private (int ThreadId, StackRange Handler)? _unhandledByUser;

    /// <summary>The thread the step in progress steps, and the stack range of the frame it steps from.</summary>
    private (int ThreadId, StackRange Frame)? _stepping;

    /// <summary>Where a frame lies on its thread's stack: its identity while it lives, as the stack's memory does not move.</summary>
    private readonly record struct StackRange(CORDB_ADDRESS Start, CORDB_ADDRESS End);

    private static StackRange? RangeOf(ICorDebugFrame frame) =>
        frame.TryGetStackRange(out CORDB_ADDRESS start, out CORDB_ADDRESS end) is Cor.S_OK ? new StackRange(start, end) : null;

    /// <summary>Steps out of the frame of a handler outside the user's code, Just My Code: the step ends where the user's
    /// code goes on, as Visual Studio's does from an exception that code left unhandled, rather than in the handler.</summary>
    private void StepOutOfHandler(ICorDebugThread thread, ICorDebugFrame handler) =>
        BeginStep(handler.CreateStepper(), thread, handler, justMyCode: true, stepper => stepper.StepOut());

    /// <summary>Makes a stepper (a frame's, or a thread's outside managed code) the step in progress: it stops nowhere
    /// unmapped, and, Just My Code, only in the user's code; <paramref name="step"/> says where it goes.</summary>
    private ICorDebugStepper BeginStep(ICorDebugStepper stepper, ICorDebugThread thread, ICorDebugFrame? from, bool justMyCode, Action<ICorDebugStepper> step)
    {
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
        if (justMyCode)
        {
            stepper.SetJMC(true);
        }
        step(stepper);
        _stepper = stepper;
        _stepping = from is not null && RangeOf(from) is { } range ? (thread.Id, range) : null;
        return stepper;
    }

    /// <summary>A step under way whose frame an exception will unwind, to a handler outside the user's code: the runtime's
    /// Just My Code stepper stops neither in that handler nor after it, so the step would run on to whatever stops next. It
    /// steps out of the handler instead, to where the user's code goes on. A handler in a call the step steps over, above
    /// the step's frame on the stack, leaves the step as it is.</summary>
    private void StepPastHandler(ICorDebugThread thread, ICorDebugFrame handler)
    {
        if (_stepper is null || _stepping is not (int steppingThread, StackRange stepped) || steppingThread != thread.Id || RangeOf(handler) is not { } caught)
        {
            return;
        }
        List<StackRange?> frames = [.. EnumerateFramesForThread(thread).Select(RangeOf)];
        int stepping = frames.IndexOf(stepped);
        if (stepping < 0 || frames.IndexOf(caught) <= stepping)
        {
            return;
        }
        _logger?.Invoke($"The step's frame is unwound to a handler outside the user's code: stepping out of the handler");
        _stepper.Deactivate();
        StepOutOfHandler(thread, handler);
    }

    /// <summary>A step from a frame, the thread's top one unless another is given, set up without letting the program go:
    /// through its statement, or the IL range given.</summary>
    internal ICorDebugStepper SetupStepper(ICorDebugThread thread, StepType stepType, ICorDebugFrame? from = null, (int Start, int End)? through = null)
    {
        ICorDebugFrame frame = from ?? thread.ActiveFrame;
        if (frame is not ICorDebugILFrame ilFrame)
        {
            throw new InvalidOperationException("Active frame is not an IL frame");
        }

        if (_stepper is not null)
        {
            throw new InvalidOperationException("A step operation is already in progress");
        }

        ICorDebugStepper stepper = frame.CreateStepper();
        stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
        return BeginStep(stepper, thread, frame, _justMyCode, stepper =>
        {
            if (stepType == StepType.StepOut)
            {
                stepper.StepOut();
                return;
            }
            bool stepIn = stepType is StepType.StepIn;
            // A module the debugger could not read has no lines: the step is the runtime's own, an instruction's.
            if ((through ?? StepRangeOf(ilFrame)) is not var (start, end))
            {
                stepper.Step(stepIn);
                return;
            }
            COR_DEBUG_STEP_RANGE range = new() { startOffset = checked((uint)start), endOffset = checked((uint)end) };
            stepper.StepRange(stepIn, [range], 1);
        });
    }

    /// <summary>The IL a step from a frame's statement steps through: its sequence point to the next one that shows a line,
    /// to the method's end from one of no length. None in a module the debugger could not read.</summary>
    private (int Start, int End)? StepRangeOf(ICorDebugILFrame frame)
    {
        ICorDebugFunction function = frame.Function;
        if (FindModule(function.Module)?.MetadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(function.Token, frame.IP.pnOffset, ModuleMetadataReader.VersionOf(frame)) is not var (start, end))
        {
            return null;
        }
        return (start, start == end ? function.ILCode.Size : end);
    }

    /// <summary>Binds a line breakpoint in the module whose symbols name its file best; false, with why on the breakpoint,
    /// when none can take it.</summary>
    private bool TryBindBreakpoint(BreakpointManager.BreakpointInfo bp)
    {
        try
        {
            if (_process is null)
            {
                return false;
            }

            // The module whose symbols name the file best: the very path over a document recorded
            // under another root, never just a file of the same name (SourcePaths).
            ModuleInfo? targetModule = null;
            ModuleMetadataReader.ResolvedBreakpoint? resolved = null;

            foreach (ModuleInfo moduleInfo in _modules.Values
                .Where(m => m.MetadataReader.HasSymbols)
                .Select(m => (Module: m, Score: m.MetadataReader.DocumentMatch(bp.FilePath)))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score)
                .Select(m => m.Module))
            {
                resolved = moduleInfo.MetadataReader.ResolveBreakpoint(bp.FilePath, bp.Line, bp.Column);
                if (resolved is not null)
                {
                    targetModule = moduleInfo;
                    break;
                }
            }

            if (targetModule is null || resolved is null)
            {
                // No module found with symbols for this file
                bp.Verified = false;
                bp.Message = "The breakpoint will not currently be hit. No symbols have been loaded for this document.";
                _logger?.Invoke($"Breakpoint at {bp.FilePath}:{bp.Line} - no symbols found");
                return false;
            }

            // Visual Studio's rule: Just My Code never stops in a method its code says is not the user's, and
            // no debugger stops in a [DebuggerHidden] one (upstream #54).
            if (targetModule.MetadataReader.IsHidden(resolved.MethodToken)
                || (_justMyCode && targetModule.IsUserCode && targetModule.MetadataReader.IsNonUserCode(resolved.MethodToken)))
            {
                bp.Verified = false;
                bp.Message = "The breakpoint will not be hit: the method is marked [DebuggerStepThrough], [DebuggerNonUserCode] or [DebuggerHidden], which Just My Code passes over.";
                return false;
            }

            // Get the function from the method token
            ICorDebugFunction function = targetModule.Module.GetFunctionFromToken(resolved.MethodToken);
            ICorDebugCode ilCode = function.ILCode;

            // Create a breakpoint at the resolved IL offset
            ICorDebugFunctionBreakpoint corBreakpoint = TryGetEntryBreakpoint(targetModule.BaseAddress, resolved.MethodToken, resolved.BindOffset)
                ?? ilCode.CreateBreakpoint(resolved.BindOffset);
            if (corBreakpoint != _entryBreakpoint?.CorBreakpoint)
            {
                corBreakpoint.Activate(true);
            }

            // Update breakpoint info
            bp.CorBreakpoint = corBreakpoint;
            bp.Verified = true;
            bp.Line = resolved.StartLine;
            bp.Column = resolved.StartColumn;
            bp.EndLine = resolved.EndLine;
            bp.EndColumn = resolved.EndColumn;
            bp.ResolvedBreakpointFromPdb = resolved;
            bp.ModuleBaseAddress = targetModule.BaseAddress;
            bp.Message = null;

            _logger?.Invoke($"Breakpoint bound at {bp.FilePath}:{bp.Line} -> resolved to line {resolved.StartLine}, IL offset {resolved.ILOffset}"
                + (resolved.BindOffset != resolved.ILOffset ? $" (bound at {resolved.BindOffset}, where the JIT maps code)" : string.Empty) + $" in method 0x{resolved.MethodToken:X}");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error binding breakpoint at {bp.FilePath}:{bp.Line}: {ex.Message}");
            bp.Verified = false;
            bp.Message = $"Error binding breakpoint: {ex.Message}";
            return false;
        }
    }

    /// <summary>Binds the line breakpoints no module could take yet, as one with symbols loads.</summary>
    private void TryBindPendingBreakpoints()
    {
        IEnumerable<BreakpointManager.BreakpointInfo> pendingBreakpoints = _breakpointManager.GetPendingBreakpoints().Where(bp => !bp.IsFunctionBreakpoint);

        foreach (BreakpointManager.BreakpointInfo? bp in pendingBreakpoints)
        {
            if (TryBindBreakpoint(bp))
            {
                // Notify that the breakpoint changed (became verified)
                OnBreakpointChanged?.Invoke(bp);
            }
        }
    }

    /// <summary>
    /// Binds all matching functions in a module and returns true only when the BreakpointInfo becomes verified.
    /// </summary>
    private bool TryBindFunctionBreakpoint(BreakpointManager.BreakpointInfo bp, ModuleInfo module)
    {
        if (module.MetadataReader.HasSymbols is false || bp.FunctionName is null)
        {
            return false;
        }

        bool wasVerified = bp.Verified;
        try
        {
            FunctionBreakpointPattern pattern = FunctionBreakpointPattern.Parse(bp.FunctionName);
            foreach (ResolvedFunctionBreakpoint resolved in FunctionBreakpointMetadataResolver.Resolve(module.MetadataReader, pattern))
            {
                if (bp.FunctionBindings.Any(binding => ReferenceEquals(binding.Module, module) && binding.MethodToken == resolved.MethodToken))
                {
                    continue;
                }
                ICorDebugFunction function = module.Module.GetFunctionFromToken(resolved.MethodToken);
                ICorDebugFunctionBreakpoint corBreakpoint = TryGetEntryBreakpoint(module.BaseAddress, resolved.MethodToken, resolved.Source.ILOffset)
                    ?? function.ILCode.CreateBreakpoint(resolved.Source.ILOffset);
                if (corBreakpoint != _entryBreakpoint?.CorBreakpoint)
                {
                    corBreakpoint.Activate(true);
                }

                bp.FunctionBindings.Add(new BreakpointManager.FunctionBreakpointBinding(corBreakpoint, module.BaseAddress, resolved.MethodToken, resolved.Source, module));
                bp.Verified = true;
                bp.Message = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error binding function breakpoint '{bp.FunctionName}' in {module.ModuleName}: {ex.Message}");
            if (!bp.Verified)
            {
                bp.Message = $"Error binding function breakpoint: {ex.Message}";
            }
        }
        return wasVerified is false && bp.Verified;
    }

    private void EnsureNoProcessBeingDebugged()
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("A process is already being debugged, you must terminate/detach first.");
        }
    }

    /// <summary>Refuses a request that reads the program's threads while it runs, saying so, where ICorDebug would answer
    /// CORDBG_E_PROCESS_NOT_SYNCHRONIZED: a stop the client was told of holds until the client lets the program go on.</summary>
    private void RequireStop()
    {
        if (_process is { } process && process.TryIsRunning(out bool running) is Cor.S_OK && running)
        {
            throw new InvalidOperationException("The program is running, so its threads' frames and values cannot be read until it stops");
        }
    }

    private ICorDebugFunctionBreakpoint? TryGetEntryBreakpoint(CORDB_ADDRESS moduleBaseAddress, int methodToken, int ilOffset)
    {
        return _entryBreakpoint is { } entry && entry.ModuleBaseAddress == moduleBaseAddress &&
            entry.MethodToken == methodToken && entry.IlOffset == ilOffset
            ? entry.CorBreakpoint
            : null;
    }

    internal ICorDebugILFrame GetIlFrameForThreadIdAndStackDepth(ThreadId threadId, FrameStackDepth stackDepth)
    {
        ICorDebugFrame frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
        if (frame is not ICorDebugILFrame ilFrame)
        {
            throw new InvalidOperationException("Frame is not an IL frame");
        }

        return ilFrame;
    }

    internal ICorDebugFrame GetFrameForThreadIdAndStackDepth(ThreadId threadId, FrameStackDepth stackDepth)
    {
        // We need to re-obtain the frame in case it has been neutered
        ICorDebugThread thread = _process!.GetThread(threadId.Value);
        ICorDebugFrame frame = EnumerateFramesForThread(thread).ElementAt(stackDepth.Value);
        return frame;
    }

    /// <summary>The thread's managed frames from its stop down. An evaluation given up, still running on the thread, is a
    /// chain of its own above the stop's (CHAIN_FUNC_EVAL, ending at its [Func Eval] frame): its frames are left out, so a
    /// frame's depth names the same frame as before the evaluation began.</summary>
    private static IEnumerable<ICorDebugFrame> EnumerateFramesForThread(ICorDebugThread thread)
    {
        List<ICorDebugChain> chains = [.. thread.EnumerateChains()];
        int stop = chains.FindLastIndex(chain => chain.Reason is CorDebugChainReason.CHAIN_FUNC_EVAL) + 1;
        foreach (ICorDebugChain chain in chains.Skip(stop))
        {
            if (chain.IsManaged is false)
            {
                continue;
            }

            foreach (ICorDebugFrame frame in chain.EnumerateFrames())
            {
                yield return frame;
            }
        }
    }

    internal IReadOnlyCollection<ModuleInfo> AllModules => _modules.Values;
    internal ModuleInfo GetModuleInfoForModule(ICorDebugModule module) => FindModule(module) ?? throw new InvalidOperationException(ModuleWithoutMetadata);

    private const string ModuleWithoutMetadata = "The code is in a module whose metadata the debugger could not read";

    /// <summary>Keys of modules emitted as the program runs, which have no image and so all have base address 0.</summary>
    private ulong _lastEmittedModuleKey = ulong.MaxValue;

    /// <summary>The module tracked for one the runtime names, null if the debugger could not read it: by its base address,
    /// or, for one emitted as the program runs, by itself, its metadata read again if the runtime has added to it.</summary>
    internal ModuleInfo? FindModule(ICorDebugModule module, bool reread = true)
    {
        CORDB_ADDRESS address = module.BaseAddress;
        if (address.Value != 0)
        {
            return _modules.GetValueOrDefault(address);
        }
        List<ModuleInfo> emitted = [.. _modules.Values.Where(info => info.IsDynamic)];
        ModuleInfo? found = emitted.Find(info => ReferenceEquals(info.Module, module));
        if (found is null && emitted.Count > 0 && module.TryGetMetaDataInterface(out IMetaDataImport? metadata) is Cor.S_OK && metadata is not null)
        {
            Guid mvid = metadata.ScopeProps.pmvid;
            found = emitted.Find(info => info.MetadataReader.Mvid == mvid);
        }
        if (reread && found is { Stale: true } && ReadEmittedMetadata(found.Module) is { } current)
        {
            found.Reload(current);
            ModuleSet_Version++;
        }
        return found;
    }

    /// <summary>A module emitted as the program runs (System.Reflection.Emit), which has no image on disk or in memory:
    /// a copy of its metadata as the runtime holds it; null when it cannot be read.</summary>
    private ModuleMetadataReader? ReadEmittedMetadata(ICorDebugModule module)
    {
        try
        {
            (nint pointer, int size) = module.GetMetaDataInterface<IMetaDataTables2>().MetaDataStorage;
            byte[] metadata = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(pointer, metadata, 0, size);
            return ModuleMetadataReader.TryLoadFromMetadata(metadata);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _logger?.Invoke($"  The emitted module's metadata could not be read: {error.Message}");
            return null;
        }
    }

    internal ICorDebugValue? GetCurrentException(ThreadId threadId)
    {
        ICorDebugThread? thread = _process?.GetThread(threadId.Value);
        if (thread is null)
        {
            return null;
        }

        thread.TryGetCurrentException(out ICorDebugValue? currentException);
        return currentException;
    }

    // Not intended to implement IDisposable - it is intended that this is called via Disconnect()
    // letGo: the program runs on after (a detach), so no thread may be left inside the runtime's handling of a stop.
    private void Dispose(bool letGo = false)
    {
        // A decompile under way is given up, its temporary file deleted, whether or not a process was ever held.
        _ending.Cancel();
        if (_process is null)
        {
            return; // A client may call Terminate, then Disconnect, both of which call Dispose. Dispose only needs to be run once.
        }
        // A process that has exited is the runtime's to tear down: nothing below may call into it.
        bool exited = _exited.IsSet;
        // The threads the runtime holds inside its handling of a breakpoint or a step's end, run off it before the detach.
        List<int> inDispatch = [.. _deferredEvents.Select(DispatchThread).OfType<int>()];
        if (_stopShown && _stoppedInDispatch is int stopped)
        {
            inDispatch.Insert(0, stopped);
        }

        // Dispose modules, which releases PDB files
        foreach (ModuleInfo moduleInfo in _modules.Values)
        {
            moduleInfo.Dispose();
        }
        _modules.Clear();

        // Deactivate all breakpoints
        if (_entryBreakpoint is { } entryBreakpoint && !exited)
        {
            entryBreakpoint.CorBreakpoint.TryActivate(false);
        }
        _entryBreakpoint = null;
        _stopAtEntry = false;
        foreach (BreakpointManager.BreakpointInfo? bp in _breakpointManager.GetAllBreakpoints().Where(b => !exited && ((b.CorBreakpoint is not null && b.IsFunctionBreakpoint is false) || b.IsFunctionBreakpoint)))
        {
            IEnumerable<ICorDebugFunctionBreakpoint> corBreakpoints = bp.IsFunctionBreakpoint ? bp.FunctionBindings.Select(binding => binding.CorBreakpoint) : [bp.CorBreakpoint!];
            foreach (ICorDebugFunctionBreakpoint? corBreakpoint in corBreakpoints)
            {
                int hResult = corBreakpoint.TryActivate(false);
                if (hResult is Cor.CORDBG_E_PROCESS_TERMINATED)
                {
                    break;
                }

                if (hResult is not Cor.S_OK)
                {
                    _logger?.Invoke($"Failed to deactivate breakpoint {bp.Id}: {hResult}");
                }
            }
        }
        _breakpointManager.Clear();
        // Before the program is let go: a debug register left set would end it at the next write it watched.
        ForgetDataBreakpoints(exited);

        _asyncStepper?.Dispose();
        _asyncStepper = null;
        if (exited)
        {
            _returnWatch = null;
            _returned.Clear();
            _returnedThread = null;
            _smartStep = null;
        }
        else
        {
            EndReturnWatch();
            ForgetReturnedValues();
            EndSmartStep();
        }
        _stepInTargets.Clear();
        if (!exited)
        {
            // A step under way would stop the program as it runs off its stop, and fails the detach.
            _stepper?.TryDeactivate();
        }
        _stepper = null!;
        _stepping = null;
        _stepAfterEvaluation = null;
        _unhandledByUser = null;
        _stopShown = false;
        EvalStatus.Abandoned.Clear();
        EvalStatus.ImplicitOff = null;
        _threads.Clear();
        _deferredEvents.Clear();
        _mainThreadId = null;
        _initialThreadId = null;
        _exceptionStates.Clear();
        _exceptionBreakModes.Clear();
        _initializedStaticTypes.Clear();
        _suppressFinalizeFunction = null;
        if (exited)
        {
            _variableManager.Clear();
        }
        else
        {
            _variableManager.ClearAndTryDisposeHandleValues();
        }
        _frameReferenceManager.Clear();

        // Unsubscribe from callbacks to avoid any further event dispatch
        _callbacks.OnAnyEvent -= QueueEvent;
        _runtimeEventChannel.Writer.Complete();
        // ProcessRuntimeEventQueue is blocked on DapRequestAndRuntimeEventLock (which we hold) and would
        // never complete if we waited on it here — that is the deadlock. Read and discard remaining events ourselves,
        // then let the processor exit once the lock is released.
        while (_runtimeEventChannel.Reader.TryRead(out CorDebugManagedCallbackEventArgs? unhandled))
        {
            if (DispatchThread(unhandled) is int thread)
            {
                inDispatch.Add(thread);
            }
        }

        // Detach from the process, once no thread is left for it to answer; the program may end as it runs off its stop.
        if (!exited && letGo)
        {
            RunOffDispatch(_process, inDispatch);
            exited = _exited.IsSet;
        }
        if (!exited)
        {
            if (_process?.TryDetach() is int detached and not Cor.S_OK)
            {
                // Refused while an evaluation that could not be stopped still runs: the program is let go rather than left held.
                _logger?.Invoke($"Detach: 0x{detached:X8}; the program is let go");
                _process.TryContinue(false);
            }
        }
        else if (_corDebug is not null)
        {
            ShutDown(_corDebug);
        }

        _isAttached = false;
        _process = null;
        _corDebug = null;

        _attached?.Dispose();
        _attached = null;
        _launch?.Dispose();
        _launch = null;
    }

    /// <summary>ICorDebug's own shutdown, once the process it debugged has exited: refused until the runtime's
    /// thread has finished neutering the process, then it stops that thread, so the adapter never exits (or
    /// releases the runtime's objects) under it.</summary>
    private void ShutDown(ICorDebug corDebug)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        int result;
        while ((result = corDebug.TryTerminate()) == Cor.CORDBG_E_ILLEGAL_SHUTDOWN_ORDER && DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
        }
        if (result != Cor.S_OK)
        {
            _logger?.Invoke($"ICorDebug.Terminate: 0x{result:X8}");
        }
    }

    private sealed class ExceptionPropagationState
    {
        // True once exception propagation has entered a JMC-marked frame. If the runtime later finds a
        // handler outside JMC code, the exception is user-unhandled.
        public required bool HasReachedUserCode { get; set; }

        // Prevents the first-chance and user-first-chance callbacks for the same exception from producing
        // duplicate "all exceptions" stops.
        public required bool AlwaysStopReported { get; set; }

        // Where it was thrown, for the exception breakpoints' "Exception thrown in": every frame is the user's without Just My Code.
        public required bool ThrownInUserCode { get; init; }
    }
}

/// <summary>Where a stop is, when the debugger can say: the client need not ask before showing it. A data breakpoint's says
/// what the write changed.</summary>
public sealed record StopInfo(int ThreadId, string Reason, string? FilePath, int Line, int Column, IReadOnlyList<int>? HitBreakpointIds, string? Text = null);

internal sealed class EvalStatus
{
    /// <summary>The thread an evaluation runs on, while one runs: the only thread of the program let go meanwhile.</summary>
    internal int? ThreadId { get; set; }

    internal bool IsRunning => ThreadId is not null;

    /// <summary>Cancelled when the running evaluation is given up, so its wait for the runtime's events ends with it and
    /// nothing reads them outside the engine's lock.</summary>
    internal CancellationToken Reading { get; set; }

    /// <summary>Evaluations given up that could not be stopped, by their thread: each runs on in the program until it
    /// returns, and its end, whenever it comes, is let go rather than taken for another's.</summary>
    internal Dictionary<int, ICorDebugEval> Abandoned { get; } = [];

    /// <summary>Why code the debugger would run of itself (a getter, a ToString, a proxy) is not run, until the program
    /// next runs on, as Visual Studio and Rider stop running it once an evaluation has timed out.</summary>
    internal string? ImplicitOff { get; set; }

    /// <summary>Set while an evaluation asked for by name runs (the Evaluate window, a watch), which runs code regardless.</summary>
    internal bool Explicit { get; set; }
}
