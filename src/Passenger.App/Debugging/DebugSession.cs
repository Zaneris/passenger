using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Text;

namespace Passenger.App.Debugging;

public enum DebugState
{
    Starting,
    Running,
    Paused,
    Ended,
}

/// <summary>One program under the debugger: the adapter's process (debugger/ beside the app), the
/// conversation with it, and what the program is doing. A UI-thread object: every event and state change
/// lands on the UI thread. A file's breakpoints go to the adapter whenever what they ask of it changes, while it
/// runs (those changed while it starts once it does), at their lines in the build it runs (<see cref="BuiltLines"/>).</summary>
public sealed class DebugSession
{
    /// <summary>How many of the adapter's last lines on standard error go to the IDE's log when it dies.</summary>
    private const int ErrorLines = 40;

    /// <summary>Why a breakpoint on a line written since the build is not sent: the program has no code of it.</summary>
    private const string NotBuilt = "The breakpoint will not currently be hit: its line was written after the program being debugged was built.";

    private readonly Process adapter;
    private readonly DapConnection connection;

    /// <summary>Where the adapter runs when not beside the app, whose path map paths cross at the protocol's edge.</summary>
    private readonly AdapterPlace? place;

    private string ToProgram(string path) => place?.ToProgram(path) ?? path;

    private string ToIde(string path) => place?.ToIde(path) ?? path;
    private readonly BreakpointStore breakpoints;
    private readonly BuiltLines lines;

    /// <summary>The column (from one) a breakpoint asks for, in XAML (<see cref="XamlBreakpoints.Column"/>); null for its line.</summary>
    private readonly Func<LineBreakpoint, int?> columns;
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);

    /// <summary>The exception breakpoints as last sent.</summary>
    private string? exceptionsSent;

    /// <summary>The data breakpoints as last sent, and each by the id the debugger gave it.</summary>
    private string? dataSent;
    private readonly Dictionary<int, DataBreakpoint> dataById = [];

    /// <summary>The function breakpoints (the property breakpoints' and XAML Binding Errors') as last sent, and the
    /// breakpoint each is by the id the debugger gave it.</summary>
    private string functionsSent = string.Empty;
    private readonly Dictionary<int, PropertyBreakpoint> propertyById = [];
    private int? bindingErrorsId;

    /// <summary>The function breakpoints' last request, until its answer (their new ids) has been read.</summary>
    private Task functionsAnswered = Task.CompletedTask;

    /// <summary>The breakpoint requests a change made while the program runs or is paused sent, until each is answered.</summary>
    private readonly List<Task> breakpointsSending = [];

    private readonly Dictionary<int, LineBreakpoint> byId = [];
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> errors = new();
    private readonly TaskCompletionSource errorsRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Run to Cursor's place, at its built line, until the next stop.</summary>
    private (string Path, int Line)? runToCursor;

    /// <summary>How many stops the adapter has told of, so a resume's answer that comes after the next stop leaves it be.</summary>
    private int stops;

    /// <summary>The stop last taken, shown again when the continue or step that left it is refused.</summary>
    private StoppedEvent? stoppedAt;
    private volatile bool killed;

    /// <summary>The adapter said the program ended: with its exit code, or (attached, where none can be known) without.</summary>
    private bool programEnded;

    /// <summary>The adapter's one disconnect, once asked: nothing more is asked of it, since it may go before answering.</summary>
    private Task? disconnected;

    /// <summary>Done once the disconnect is asked, which ends a start's waits.</summary>
    private readonly TaskCompletionSource ending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Why a start failed that was stopped (Stop, Detach, the window's close) before it was done.</summary>
    public const string StoppedStarting = "the session was stopped as it started";

    /// <summary>The debug adapter the app carries, beside it in debugger/, which dotnet runs.</summary>
    public static string AdapterPath => Path.Combine(AppContext.BaseDirectory, "debugger", "Passenger.DebugAdapter.dll");

    /// <summary>How the adapter starts: beside the app, or where the program is (a container's, over docker exec).</summary>
    private static ProcessStartInfo AdapterStart(AdapterPlace? place, bool elsewhere)
    {
        ProcessStartInfo start;
        if (place is not null)
        {
            // The adapter where the program is: its flags the same, its symbols and the engine's log kept there.
            start = place.Command([AdapterFlags.Interpreter, AdapterFlags.SymbolCache + place.SymbolCache, AdapterFlags.Remote,
                .. AdapterEnvironment.EngineLog is not null ? (string[])[AdapterFlags.EngineLogging + ContainerEngineLog] : []]);
        }
        else
        {
            string path = AdapterPath;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The debug adapter is not beside the app", path);
            }
            List<string> arguments = [path, AdapterFlags.Interpreter, AdapterFlags.SymbolCache + SymbolCache];
            if (AdapterEnvironment.EngineLog is { } engineLog)
            {
                arguments.Add(AdapterFlags.EngineLogging + engineLog);
            }
            if (elsewhere)
            {
                arguments.Add(AdapterFlags.Remote);
            }
            start = ChildProcesses.DotnetCommand(arguments);
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return start;
    }

    private DebugSession(Process adapter, BreakpointStore breakpoints, BuiltLines lines, Action<string> log, Action<OutputEvent> output, Func<LineBreakpoint, int?>? columns,
        bool elsewhere, AdapterPlace? place, bool quiet = false)
    {
        Elsewhere = elsewhere || place is not null;
        this.place = place;
        this.breakpoints = breakpoints;
        this.lines = lines;
        this.columns = columns ?? (_ => null);
        // The log is called from the adapter's error stream and the connection's own threads: each line lands on the UI's.
        log = UiThread.Marshal(log);
        this.adapter = adapter;
        // What the adapter and the conversation say goes to the debugger's log as well as the console, so it outlives the session.
        string name = $"adapter {adapter.Id}";
        Action<string> console = log;
        log = line =>
        {
            IdeLog.Write(IdeLog.Debugger, $"[{name}] {line}");
            console(line);
        };
        adapter.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorsRead.TrySetResult();
                return;
            }
            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > ErrorLines)
                {
                    errors.Dequeue();
                }
            }
            log(e.Data);
        };
        adapter.BeginErrorReadLine();
        adapter.EnableRaisingEvents = true;
        // After the last of its error stream, so a crash's own words reach the console and the log before its end.
        adapter.Exited += (_, _) => IdeErrors.Observe(Task.WhenAny(errorsRead.Task, Task.Delay(TimeSpan.FromSeconds(2)))
            .ContinueWith(_ => UiThread.Post(() => OnAdapterExited(log)), TaskScheduler.Default));
        connection = new DapConnection(adapter.StandardOutput.BaseStream, adapter.StandardInput.BaseStream, log) { Name = name };
        connection.EventReceived += (name, body) =>
        {
            if (name == DapEvents.Output)
            {
                // Here, in order with the events around it, rather than a job for the window at each of a program's writes.
                if (body.ValueKind == JsonValueKind.Object && body.Deserialize<OutputEvent>(DapConnection.Options) is { } written)
                {
                    output(written);
                }
                return;
            }
            JsonElementCopy copy = new(body);
            UiThread.Post(() => OnEvent(name, copy));
        };
        Quiet = quiet;
        if (quiet)
        {
            return;
        }
        breakpoints.Join(this);
        breakpoints.Changed += OnBreakpointsChanged;
        breakpoints.ExceptionsChanged += OnExceptionsChanged;
        breakpoints.DataChanged += OnDataChanged;
        breakpoints.PropertiesChanged += OnFunctionsChanged;
        breakpoints.BindingErrorsChanged += OnFunctionsChanged;
    }

    /// <summary>Attached for Hot Reload alone (a container run's service under Run with Hot Reload): no breakpoint is sent,
    /// no exception stops it, and the breakpoints never hear of it.</summary>
    public bool Quiet { get; }

    public event EventHandler? StateChanged;

    public DebugState State { get; private set; } = DebugState.Starting;

    /// <summary>The thread that stopped, while paused.</summary>
    public int? StoppedThread { get; private set; }

    /// <summary>Why it stopped (breakpoint, step, exception, pause, entry, data breakpoint), while paused.</summary>
    public string? StopReason { get; private set; }

    /// <summary>The data breakpoint the program stopped at, while paused there, and what the write changed ("Previous
    /// Value: 1, New Value: 2").</summary>
    public (DataBreakpoint Breakpoint, string? Change)? DataStop { get; private set; }

    /// <summary>The property breakpoint the program stopped at, while paused there.</summary>
    public PropertyBreakpoint? PropertyStop { get; private set; }

    /// <summary>Paused at XAML Binding Errors: a binding of the app failed where it stopped.</summary>
    public bool BindingErrorStop { get; private set; }

    /// <summary>Why the debugger can set no data breakpoint where it runs, or null where it can.</summary>
    public string? DataBreakpointsUnavailable { get; private set; } = "The debugger has not started";

    public int? ExitCode { get; private set; }

    /// <summary>The program's process, once the adapter has said which it is.</summary>
    public Task<int> ProcessStarted => processStarted.Task;

    private readonly TaskCompletionSource<int> processStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Done once the session has heard its adapter's process end, a launched program the adapter left running
    /// ended by then.</summary>
    public Task AdapterEnded => adapterEnded.Task;

    private readonly TaskCompletionSource adapterEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The program the adapter launched, once it has said which it is: the adapter's child, which ends with the IDE
    /// (its job or process group), but which only the IDE can end once the adapter itself has died.</summary>
    private Process? launched;

    /// <summary>The program, when it runs in a tab of the Terminal window rather than the adapter's console: where it reads
    /// and writes, and how it ended, which the adapter cannot tell of a program it did not start on macOS and Linux.</summary>
    public Run.TerminalRun? Terminal { get; private set; }

    /// <summary>Why the session ended other than by the program ending or being stopped: the adapter crashed.</summary>
    public string? Failure { get; private set; }

    /// <summary>Why the program ended with the place it ran in, when it did ("web-1 stopped": its container stopped, taking
    /// the adapter there with it), rather than of itself or by the debugger.</summary>
    public string? Gone { get; private set; }

    public string Program { get; private set; } = string.Empty;

    /// <summary>The debugger attached to a program already running, rather than starting it: one Detach can let go of.</summary>
    public bool Attached { get; private init; }

    /// <summary>The program runs on another machine (a container's): its process id is that machine's, so the session never
    /// holds, signals or kills a process of this one by it, and its adapter says so of the processes it tells of.</summary>
    public bool Elsewhere { get; }

    /// <summary>Whether the program's process id (<see cref="ProcessStarted"/>) names a process of this machine.</summary>
    public bool IsLocal => !Elsewhere && processLocal;

    /// <summary>Where the adapter runs when not beside the app (the program's container), or null.</summary>
    public AdapterPlace? Place => place;

    private bool processLocal = true;

    /// <summary>The session ended with Detach: the program runs on without the debugger.</summary>
    public bool Detached { get; private set; }

    /// <summary>The adapter's process id.</summary>
    public int AdapterProcessId => adapter.Id;

    /// <summary>Where the adapter keeps symbols it decompiles for assemblies that ship none, and prunes them as it starts:
    /// under the state folder's cache/.</summary>
    public static string SymbolCache => Path.Combine(Passenger.App.State.StateStore.DefaultRoot, "cache", "symbols");

    /// <summary>Where an adapter in a container keeps the engine's log while PASSENGER_ENGINE_LOG asks for one.</summary>
    public const string ContainerEngineLog = "/tmp/passenger-engine.log";

    /// <summary>Starts the adapter and launches a plan's program under it (a .dll under dotnet, or an executable), with the
    /// store's breakpoints. What the program writes, and the debugger's own words ("console"), go to
    /// <paramref name="output"/> on the connection's thread, in order; with <paramref name="runInTerminal"/> it runs in the
    /// terminal it starts, its output there. <paramref name="starting"/> is handed the session as it begins, so Stop can end
    /// it while it starts; with <paramref name="elsewhere"/> the program is on another machine.</summary>
    public static Task<DebugSession> LaunchAsync(Run.LaunchPlan plan, BreakpointStore breakpoints, BuiltLines lines, Action<string> log, Action<OutputEvent> output,
        Func<RunInTerminalArguments, Task<Run.TerminalRun>>? runInTerminal = null, Func<LineBreakpoint, int?>? columns = null, bool hotReload = false,
        Action<DebugSession>? starting = null, bool elsewhere = false, AdapterPlace? place = null)
    {
        return StartAsync(plan.Built ?? plan.Program, DapCommands.Launch, new LaunchArguments
        {
            Program = place?.ToProgram(plan.Program) ?? plan.Program,
            Args = plan.Arguments,
            Cwd = place?.ToProgram(plan.WorkingDirectory) ?? plan.WorkingDirectory,
            Env = plan.Environment,
            Console = runInTerminal is null ? "internalConsole" : "integratedTerminal",
            JustMyCode = true,
            StopAtEntry = false,
            HotReload = hotReload,
        }, breakpoints, lines, log, output, runInTerminal, columns, starting, elsewhere, place);
    }

    /// <summary>Starts the adapter and attaches it to a running .NET process (one Attach to Process chose, or a test host
    /// VSTest started), with the store's breakpoints; <paramref name="starting"/> is handed the session as it begins. With
    /// <paramref name="hotReload"/> the program was started held for the debugger with DOTNET_MODIFIABLE_ASSEMBLIES=debug,
    /// so its code may change as a launch's may; with <paramref name="elsewhere"/> it is not on this machine, and with
    /// <paramref name="place"/> the adapter runs where it is (a container's, its process id that container's).</summary>
    public static Task<DebugSession> AttachAsync(int processId, BreakpointStore breakpoints, BuiltLines lines, Action<string> log, Action<OutputEvent> output,
        Func<LineBreakpoint, int?>? columns = null, Action<DebugSession>? starting = null, bool hotReload = false, bool elsewhere = false, AdapterPlace? place = null,
        bool quiet = false) =>
        StartAsync(place is null ? $"process {processId}" : $"process {processId} in {place.Where}", DapCommands.Attach,
            new AttachArguments { ProcessId = processId, JustMyCode = true, HotReload = hotReload ? true : null },
            breakpoints, lines, log, output, null, columns, starting, elsewhere, place, quiet);

    /// <summary>Opens the session: the adapter told what to start, the breakpoints sent (a quiet session's none, and no
    /// exception to stop at), then configurationDone, whose answer waits for the program's runtime however long it takes. A
    /// session stopped meanwhile ends the start at once, failing it with <see cref="StoppedStarting"/>.</summary>
    private static async Task<DebugSession> StartAsync(string program, string request, object arguments, BreakpointStore breakpoints, BuiltLines lines, Action<string> log, Action<OutputEvent> output,
        Func<RunInTerminalArguments, Task<Run.TerminalRun>>? runInTerminal, Func<LineBreakpoint, int?>? columns, Action<DebugSession>? starting, bool elsewhere, AdapterPlace? place,
        bool quiet = false)
    {
        UiThread.Check();
        // Forked off the window's thread; a Stop meanwhile finds the tab still opening, which its session closes.
        Process adapter = await ChildProcesses.StartAsync(AdapterStart(place, elsewhere));
        DebugSession session = new(adapter, breakpoints, lines, log, output, columns, elsewhere, place, quiet) { Program = program, Attached = request == DapCommands.Attach };
        IdeLog.Write(IdeLog.Debugger, $"[{session.connection.Name}] {request} {program}");
        if (runInTerminal is not null)
        {
            // Asked inside configurationDone, on the connection's thread: the terminal is the window's.
            session.connection.Handle<RunInTerminalArguments, RunInTerminalResponse>(DapCommands.RunInTerminal, asked => UiThread.InvokeAsync(async () =>
            {
                Run.TerminalRun run = await runInTerminal(asked);
                session.Terminal = run;
                return new RunInTerminalResponse(run.ProcessId);
            }));
        }
        session.connection.Start();
        starting?.Invoke(session);
        try
        {
            DapCapabilities capabilities = await session.connection.RequestAsync<DapCapabilities>(DapCommands.Initialize, new InitializeArguments
            {
                ClientId = "passenger",
                ClientName = "Passenger",
                AdapterId = "coreclr",
                LinesStartAt1 = true,
                ColumnsStartAt1 = true,
                PathFormat = "path",
                SupportsVariableType = true,
                SupportsRunInTerminalRequest = runInTerminal is not null,
            });
            session.DataBreakpointsUnavailable = capabilities.SupportsDataBreakpoints == true ? null
                : capabilities.DataBreakpointsUnavailable ?? "The debugger sets no data breakpoints";
            await session.connection.RequestAsync(request, arguments);
            await Task.WhenAny(session.initialized.Task, session.ending.Task).WaitAsync(TimeSpan.FromSeconds(30));
            session.GoingOn();
            if (quiet)
            {
                // Exception breakpoints given, none of them: an unhandled exception ends the program as it would run.
                await session.connection.RequestAsync<SetExceptionBreakpointsResponse>(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], [], []));
            }
            else
            {
                await session.SendChangedAsync();
            }
            // Never asked of an adapter already told to go: it would start the program as it went.
            session.GoingOn();
            await session.connection.RequestAsync(DapCommands.ConfigurationDone, new DapEmpty());
            // A stop told before this answer stands, as one before a resume's does.
            if (session.State == DebugState.Starting)
            {
                session.SetState(DebugState.Running);
            }
            // What changed while the debugger waited for the program's runtime, when nothing could be sent, goes now.
            if (!quiet)
            {
                session.Sending(session.SendChangedAsync());
            }
            return session;
        }
        catch
        {
            await session.StopAsync();
            throw;
        }
    }

    /// <summary>A start goes on only while nothing has stopped it.</summary>
    private void GoingOn()
    {
        if (disconnected is not null)
        {
            throw new InvalidOperationException(StoppedStarting);
        }
    }

    public Task ContinueAsync() => Resume(DapCommands.Continue);

    public Task StepOverAsync() => Resume(DapCommands.Next);

    public Task StepIntoAsync() => Resume(DapCommands.StepIn);

    /// <summary>Smart Step Into's step: straight into one of the calls <see cref="StepInTargetsAsync"/> gave.</summary>
    public Task StepIntoTargetAsync(int target) => Resume(DapCommands.StepIn, thread => new StepInArguments(thread, target));

    /// <summary>The calls a frame's statement can step into, in the order they run, each placed where it is written (lines
    /// and columns from one, at the built lines).</summary>
    public async Task<IReadOnlyList<StepInTarget>> StepInTargetsAsync(int frameId) =>
        (await connection.RequestAsync<StepInTargetsResponse>(DapCommands.StepInTargets, new StepInTargetsArguments(frameId))).Targets;

    public Task StepOutAsync() => Resume(DapCommands.StepOut);

    /// <summary>Runs on to a line: a breakpoint the user never sees, gone again at the next stop (the stop shown again when the
    /// continue is refused among them, so no later run stops at a breakpoint no one set).</summary>
    public Task RunToCursorAsync(string path, int line)
    {
        UiThread.Check();
        if (State != DebugState.Paused)
        {
            return Task.CompletedTask;
        }
        runToCursor = (path, lines.ToBuilt(path, line));
        return Resume(DapCommands.Continue, before: () => SendBreakpointsAsync(path));
    }

    /// <summary>Text for the program's standard input.</summary>
    public Task InputAsync(string text) => State == DebugState.Ended || disconnected is not null ? Task.CompletedTask : connection.RequestAsync(DapCommands.Input, new InputArguments(text));

    /// <summary>The end of the program's standard input, as Ctrl+D in its console.</summary>
    public Task CloseInputAsync() => State == DebugState.Ended || disconnected is not null ? Task.CompletedTask : connection.RequestAsync(DapCommands.Input, new InputArguments(string.Empty, Close: true));

    public async Task PauseAsync()
    {
        UiThread.Check();
        if (State == DebugState.Running && disconnected is null)
        {
            await connection.RequestAsync(DapCommands.Pause, new ThreadArguments(0));
        }
    }

    /// <summary>Ends the program and the adapter. A program that will not go is killed with the adapter; one in the terminal
    /// that is stopped, rather than at its own end, goes with all it started.</summary>
    public async Task StopAsync()
    {
        UiThread.Check();
        if (State == DebugState.Ended)
        {
            return;
        }
        bool stopping = !programEnded && Terminal is { IsRunning: true };
        if (stopping)
        {
            // Windows finds a tree only from its root while it runs, which the debugger's terminate would end first.
            if (OperatingSystem.IsWindows())
            {
                Terminal!.Stop();
            }
            else
            {
                Terminal!.Stopping();
            }
        }
        await EndProcessesAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        if (Terminal is { } terminal)
        {
            if (stopping && !OperatingSystem.IsWindows())
            {
                terminal.Stop();
            }
            await TerminalEnd(terminal);
        }
        End(ExitCode);
    }

    /// <summary>Detach: the debugger takes its breakpoints out of the program it attached to and lets go of it, and the
    /// program runs on, from where it was paused if it was; the session ends with the adapter.</summary>
    public async Task DetachAsync()
    {
        UiThread.Check();
        if (State == DebugState.Ended || disconnected is not null)
        {
            return;
        }
        Detached = true;
        await EndProcessesAsync(LetGoLimit, TimeSpan.FromSeconds(3), terminate: false);
        End(null);
    }

    /// <summary>How long the adapter's evaluations may run, which it has as the IDE has it (a container's adapter given it
    /// too): the IDE refuses to start with a malformed value, so this is the adapter's own.</summary>
    internal static TimeSpan EvaluationLimit => AdapterEnvironment.EvaluationTimeout;

    /// <summary>How long Detach waits for the adapter's let-go: the rest of an evaluation given up a moment before
    /// (<see cref="EvaluationLimit"/> at most), then a few seconds to run the program off its stop; within it, the adapter
    /// is never killed under a let-go, which would leave the program held.</summary>
    internal static TimeSpan LetGoLimit => EvaluationLimit + TimeSpan.FromSeconds(5);

    /// <summary>How long the adapter is given to let a program attached to go as the window closes, which waits a few seconds
    /// for its debugger (<see cref="MainWindow.EndLimit"/>) before the process goes: the let-go keeps within it, its answer and
    /// the adapter's exit after.</summary>
    private static readonly TimeSpan ClosingLetGo = TimeSpan.FromSeconds(2);

    /// <summary>Takes the exit code of a program in the terminal, which its tab has once its last output is in, when the
    /// adapter could not tell it.</summary>
    private async Task TerminalEnd(Run.TerminalRun terminal)
    {
        try
        {
            int code = await terminal.Ended.WaitAsync(TimeSpan.FromSeconds(3));
            ExitCode ??= code;
        }
        catch (TimeoutException)
        {
            // Still running after the debugger let it go: its tab tells its end when it comes.
        }
    }

    /// <summary>Ends the program and the adapter as the IDE closes, on a worker thread, so the process can wait for it
    /// once the window's loop has stopped; the session's state, which nothing will show, is left as it is. A program
    /// attached to is let go of instead, as Detach does: it ran before the IDE did, and runs on after it.</summary>
    public Task CloseAsync()
    {
        UiThread.Check();
        return State == DebugState.Ended ? Task.CompletedTask
            : Attached ? EndProcessesAsync(ClosingLetGo + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), terminate: false, letGoWithin: ClosingLetGo)
            : EndProcessesAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Asks the adapter to end the program (or, detaching, to let it go, within <paramref name="letGoWithin"/> when
    /// given) and itself, and kills the adapter when it has not gone within this ask's limits. It is asked once, by Stop,
    /// Detach, the program's end or the window's close, whichever comes first: a second disconnect waits on an adapter the
    /// first has let go. The answer is awaited, and the exit waited for, on the thread pool, never failing, so a window gone
    /// meanwhile (its dispatcher with it) leaves no failed request behind.</summary>
    private Task EndProcessesAsync(TimeSpan answer, TimeSpan exit, bool terminate = true, TimeSpan? letGoWithin = null)
    {
        // Not held: the window's end waits for these with no UI thread to come back to (MainWindow.Ended).
        Task asked = disconnected ??= Task.Run(() => DisconnectAsync(terminate, letGoWithin));
        ending.TrySetResult();
        return Task.Run(async () =>
        {
            await Task.WhenAny(asked, Task.Delay(answer));
            if (!adapter.WaitForExit(exit))
            {
                killed = true;
                ChildProcesses.Kill(adapter);
            }
        });
    }

    private async Task DisconnectAsync(bool terminate, TimeSpan? letGoWithin)
    {
        try
        {
            await connection.RequestAsync(DapCommands.Disconnect, new DisconnectArguments(TerminateDebuggee: terminate,
                LetGoWithin: letGoWithin is { } within ? (int)within.TotalMilliseconds : null));
        }
        catch (DapException)
        {
            // Never answered when the adapter was killed, or was gone already.
        }
    }

    /// <summary>The adapter's process ended: with exit code 0 as asked, or killed when it would not go; any other end is
    /// a crash, written to the IDE's log with its last words, and told in the console while the session lived.</summary>
    private void OnAdapterExited(Action<string> log)
    {
        int code = adapter.ExitCode;
        if (code != 0 && !killed && place?.Running is { } running)
        {
            IdeErrors.Observe(PlaceEndedAsync(running, code, log).ContinueWith(_ => adapterEnded.TrySetResult(), TaskScheduler.Default));
            return;
        }
        AdapterExited(code, log);
        adapterEnded.TrySetResult();
    }

    /// <summary>An adapter elsewhere that ended with a failure no one asked for: when its place has stopped (its container),
    /// that took docker exec and the adapter with the program, which has ended, said so; else the debugger's own end.</summary>
    private async Task PlaceEndedAsync(Func<Task<bool>> running, int code, Action<string> log)
    {
        using IDisposable held = BackgroundWork.Scope();
        bool stopped;
        try
        {
            stopped = !await running().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception unknown) when (unknown is TimeoutException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception
            or OperationCanceledException)
        {
            // Nothing could say: taken for the debugger's own end.
            stopped = false;
        }
        if (!stopped)
        {
            AdapterExited(code, log);
            return;
        }
        IdeLog.Write(IdeLog.Debugger, $"[adapter {adapter.Id}] ended with exit code {code} as {place!.Where} stopped, debugging {Program}");
        if (State != DebugState.Ended && !programEnded)
        {
            Gone = $"{place.Where} stopped";
            log($"{place.Where} stopped, and {Program} with it");
        }
        EndLaunched(log);
        IdeErrors.Observe(EndAfterTerminal());
    }

    /// <summary>The adapter's process ended, its end read as <see cref="OnAdapterExited"/> says.</summary>
    private void AdapterExited(int code, Action<string> log)
    {
        if (code != 0 && !killed)
        {
            string said;
            lock (errors)
            {
                said = string.Join('\n', errors);
            }
            IdeErrors.Write($"Passenger's debugger (process {adapter.Id}) stopped with exit code {code} debugging {Program}"
                + (said.Length > 0 ? $":\n{said}" : string.Empty));
            if (State != DebugState.Ended && !programEnded)
            {
                Failure = $"the debugger itself stopped (exit code {code})";
                log($"Passenger's debugger stopped unexpectedly with exit code {code}");
            }
            else if (State != DebugState.Ended)
            {
                // Its debugging library can crash tearing down after the program has gone, whose end was told already.
                log($"Passenger's debugger stopped with exit code {code} after the program ended");
            }
        }
        EndLaunched(log);
        IdeErrors.Observe(EndAfterTerminal());
    }

    /// <summary>Ends the session once a program in the terminal has ended too, so its exit code, which the terminal saw, is
    /// told with the end.</summary>
    private async Task EndAfterTerminal()
    {
        if (Terminal is { } terminal)
        {
            await TerminalEnd(terminal);
        }
        End(null);
    }

    /// <summary>Ends a launched program the adapter left running as it went (a crash, a kill), which nothing else would end
    /// before the IDE did: frozen at its stop or running on, holding its port and its files. Its tree on Windows; on macOS
    /// and Linux the adapter's process group too, which holds what the program started.</summary>
    private void EndLaunched(Action<string> log)
    {
        // Gone unasked (a crash): asked, the program has ended on its own or Stop ends it.
        if (disconnected is null && Terminal is { IsRunning: true } terminal)
        {
            log($"{terminal.Name} (process {terminal.ProcessId}) was left running in the terminal by the debugger, and is ended with it");
            terminal.Stop();
        }
        if (launched is not { } program)
        {
            return;
        }
        launched = null;
        using (program)
        {
            try
            {
                if (program.HasExited)
                {
                    return;
                }
            }
            catch (Exception gone) when (gone is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return;
            }
            log(EndedLeftRunning(Path.GetFileName(Program), program.Id));
            ChildProcesses.Kill(program);
            ChildProcesses.Kill(adapter);
        }
    }

    /// <summary>What the session's console says as the IDE ends a launched program its adapter left running.</summary>
    public static string EndedLeftRunning(string program, int processId) => $"{program} (process {processId}) was left running by the debugger, and is ended with it";

    /// <summary>A launched program's process, its handle opened at once on Windows so its id is never another's while held;
    /// null when it has gone already.</summary>
    private static Process? Hold(int processId)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            if (OperatingSystem.IsWindows())
            {
                _ = process.SafeHandle;
            }
            return process;
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DapThread>> ThreadsAsync() =>
        (await connection.RequestAsync<ThreadsResponse>(DapCommands.Threads, new DapEmpty())).Threads;

    /// <summary>A thread's frames, each source on this disk named as the IDE names its files: a PDB records a file as the
    /// build was given it (another letter case, an 8.3 name, a container's path the session's map names back), and its
    /// stops would otherwise miss the open file. Named off the caller's thread: a stop asks the disk twice for each frame
    /// with a source, a hundred and more of them in an app's stack.</summary>
    public async Task<IReadOnlyList<DapStackFrame>> StackTraceAsync(int threadId) =>
        [.. (await connection.RequestAsync<StackTraceResponse>(DapCommands.StackTrace, new StackTraceArguments(threadId)).ConfigureAwait(false)).StackFrames.Select(f =>
            f.Source is { Path: { } named } source && ToIde(named) is var path && Path.IsPathFullyQualified(path) && File.Exists(path)
                ? f with { Source = source with { Path = PathNames.Canonical(path) } }
                : f)];

    public async Task<IReadOnlyList<DapScope>> ScopesAsync(int frameId) =>
        (await connection.RequestAsync<ScopesResponse>(DapCommands.Scopes, new ScopesArguments(frameId))).Scopes;

    public async Task<IReadOnlyList<DapVariable>> VariablesAsync(int reference) =>
        (await connection.RequestAsync<VariablesResponse>(DapCommands.Variables, new VariablesArguments(reference))).Variables;

    /// <summary>The exception a thread stopped on: its type, message and inner exceptions.</summary>
    public Task<ExceptionInfoResponse> ExceptionInfoAsync(int threadId) =>
        connection.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(threadId));

    /// <summary>Assigns a value (a C# expression) to what an expression names, in a frame.</summary>
    public Task<SetExpressionResponse> SetExpressionAsync(string expression, string value, int frameId) =>
        connection.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments(expression, value, frameId));

    /// <summary>Where the paused thread could go on from at a line of a file (from one), if the line is in its method.</summary>
    public async Task<GotoTarget?> GotoTargetAsync(string path, int line) =>
        (await connection.RequestAsync<GotoTargetsResponse>(DapCommands.GotoTargets, new GotoTargetsArguments(new DapSource { Path = ToProgram(path) }, line))).Targets.FirstOrDefault();

    /// <summary>Set Next Statement: the paused thread goes on from a target; it stops there anew.</summary>
    public Task GotoAsync(int thread, GotoTarget target) => connection.RequestAsync<object>(DapCommands.Goto, new GotoArguments(thread, target.Id));

    /// <summary>Whether a data breakpoint can watch a member of a value (by the reference its members were listed by, and its
    /// name there), and what it would watch, or why none can.</summary>
    public Task<DataBreakpointInfoResponse> DataBreakpointInfoAsync(int variablesReference, string name) =>
        connection.RequestAsync<DataBreakpointInfoResponse>(DapCommands.DataBreakpointInfo, new DataBreakpointInfoArguments(name, variablesReference));

    /// <summary>What Hot Reload needs of the program: its runtime's capabilities, its modules and, paused, its active
    /// statements.</summary>
    public async Task<HotReloadStateResponse> HotReloadStateAsync()
    {
        HotReloadStateResponse state = await connection.RequestAsync<HotReloadStateResponse>(DapCommands.HotReloadState, new DapEmpty());
        return state with { ActiveStatements = [.. state.ActiveStatements.Select(s => s.Document is { } document ? s with { Document = ToIde(document) } : s)] };
    }

    /// <summary>Applies Edit and Continue's updates to the program, paused or running, each document's lines named as the
    /// program's PDB names it.</summary>
    public Task<ApplyChangesResponse> ApplyChangesAsync(IReadOnlyList<DapModuleUpdate> updates) =>
        connection.RequestAsync<ApplyChangesResponse>(DapCommands.ApplyChanges, new ApplyChangesArguments(
            [.. updates.Select(u => u with { Lines = [.. u.Lines.Select(l => l with { Document = ToProgram(l.Document) })] })]));

    /// <summary>Sends files' breakpoints again, at their lines now, after an update the program took: the debugger binds
    /// each again in the code it runs now, as the files' built lines are now their lines.</summary>
    public async Task SendBreakpointsAgainAsync(IEnumerable<string> paths)
    {
        UiThread.Check();
        foreach (string path in paths.Where(p => breakpoints.In(p).Count > 0 || sent.ContainsKey(p)).ToList())
        {
            if (State is DebugState.Running or DebugState.Paused && disconnected is null)
            {
                await SendBreakpointsAsync(path);
            }
        }
    }

    /// <summary>An expression's value in a frame; context is watch, hover or repl.</summary>
    public Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, string context) =>
        connection.RequestAsync<EvaluateResponse>(DapCommands.Evaluate, new EvaluateArguments(expression, frameId, context));

    /// <summary>Sets the paused program going (a continue or a step), saying it runs at once, as the adapter takes it ahead
    /// of what the stop still reads: a request it refuses throws its reason, and the stop is shown again as it was, the
    /// program still paused there. A stop that comes before the answer (a step that ends at once) stands.
    /// <paramref name="before"/> is sent first, the stop still there (Run to Cursor's place).</summary>
    private async Task Resume(string command, Func<int, object>? arguments = null, Func<Task>? before = null)
    {
        UiThread.Check();
        if (State != DebugState.Paused || disconnected is not null || StoppedThread is not int thread || stoppedAt is not { } at)
        {
            return;
        }
        int stop = stops;
        SetState(DebugState.Running);
        // Sent with the request, never awaited before it: the adapter takes the two in turn, ahead of the stop's reads.
        Task placed = before?.Invoke() ?? Task.CompletedTask;
        _ = placed.ContinueWith(static failed => failed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            await connection.RequestAsync(command, arguments?.Invoke(thread) ?? new ThreadArguments(thread));
        }
        catch (DapException) when (stops == stop && State == DebugState.Running && disconnected is null)
        {
            TakeStop(at);
            throw;
        }
        await placed;
    }

    /// <summary>Sends a file's breakpoints at their built lines; one on a line written since the build is refused here, as
    /// the debugger refuses a line with no code. The adapter keeps those sent as they were, hit counts and all.</summary>
    private async Task SendBreakpointsAsync(string path)
    {
        // Muted, none is sent and each is as no debugger has said, though Run to Cursor's place still is.
        IEnumerable<LineBreakpoint> sending = breakpoints.Muted ? [] : breakpoints.In(path).Where(b => b.Enabled);
        List<LineBreakpoint> built = [.. sending.Where(b => lines.IsBuilt(path, b.Line))];
        List<LineBreakpoint> unbuilt = [.. sending.Where(b => !lines.IsBuilt(path, b.Line))];
        List<DapSourceBreakpoint> requests = [.. built.Select(b => Request(path, b))];
        if (runToCursor is { } target && target.Path == path && requests.All(r => r.Line != target.Line + 1))
        {
            requests.Add(new DapSourceBreakpoint(target.Line + 1));
        }
        sent[path] = Signature(path);
        SetBreakpointsResponse response = await connection.RequestAsync<SetBreakpointsResponse>(
            DapCommands.SetBreakpoints, new SetBreakpointsArguments(new DapSource { Path = ToProgram(path), Name = Path.GetFileName(path) }, requests));
        foreach (int id in byId.Where(p => p.Value.Path == path).Select(p => p.Key).ToList())
        {
            byId.Remove(id);
        }
        for (int i = 0; i < Math.Min(built.Count, response.Breakpoints.Count); i++)
        {
            if (response.Breakpoints[i].Id is int id)
            {
                byId[id] = built[i];
            }
        }
        if (breakpoints.Muted)
        {
            breakpoints.Report(this, path, [.. breakpoints.In(path).Select(_ => (BreakpointStatus.Unknown, (string?)null, (int?)null))], breakpoints.In(path));
            return;
        }
        breakpoints.Report(this, path,
            [.. response.Breakpoints.Take(built.Count).Select(b => Status(path, b)), .. unbuilt.Select(_ => (BreakpointStatus.Invalid, (string?)NotBuilt, (int?)null))],
            [.. built.Take(response.Breakpoints.Count), .. unbuilt]);
    }

    /// <summary>A breakpoint as the debugger is asked for it: at its built line, and in XAML at its name's column there, both
    /// as the compiler counted them (a lone '\r' before the name begins another line in XAML).</summary>
    private DapSourceBreakpoint Request(string path, LineBreakpoint breakpoint)
    {
        int? column = columns(breakpoint);
        TextPosition at = lines.ToBuilt(path, breakpoint.Line, column is int named ? named - 1 : 0);
        return new DapSourceBreakpoint(at.Line + 1, column is null ? null : at.Column + 1, breakpoint.Condition, breakpoint.HitCondition, breakpoint.LogMessage, breakpoint.Suspend);
    }

    /// <summary>Sends every breakpoint whose request differs from what the debugger was last sent: each file's (one that lost
    /// its last breakpoint too), then the exception, function and data breakpoints. The start sends them all, from a list of
    /// the files taken first, and once configurationDone has answered what changed meanwhile, when no change could go.</summary>
    private async Task SendChangedAsync()
    {
        foreach (string path in breakpoints.Files.Concat(sent.Keys).Distinct(StringComparer.Ordinal).ToList())
        {
            if (Open() && (!sent.TryGetValue(path, out string? last) || last != Signature(path)))
            {
                await SendBreakpointsAsync(path);
            }
        }
        if (Open() && exceptionsSent != ExceptionSignature())
        {
            await SendExceptionBreakpointsAsync();
        }
        if (Open() && functionsSent != FunctionSignature())
        {
            await (functionsAnswered = SendFunctionBreakpointsAsync());
        }
        if (Open() && DataBreakpointsUnavailable is null && (dataSent ?? string.Empty) != DataSignature())
        {
            await SendDataBreakpointsAsync();
        }

        bool Open() => disconnected is null && State != DebugState.Ended;
    }

    private void OnBreakpointsChanged(object? sender, string path)
    {
        // A report from the debugger raises Changed too: only what changes the request goes back to it.
        if (State is DebugState.Running or DebugState.Paused && disconnected is null && (!sent.TryGetValue(path, out string? last) || last != Signature(path)))
        {
            Sending(SendBreakpointsAsync(path));
        }
    }

    /// <summary>Every breakpoint change made while the program runs or is paused, answered: the program has them once this
    /// is done (a request the debugger refused, or the session's end, ends it too), so what is done next can wait for
    /// that.</summary>
    public Task BreakpointsAnswered
    {
        get
        {
            UiThread.Check();
            breakpointsSending.RemoveAll(t => t.IsCompleted);
            return Task.WhenAll(breakpointsSending.Append(functionsAnswered).Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private void Sending(Task request)
    {
        breakpointsSending.RemoveAll(t => t.IsCompleted);
        breakpointsSending.Add(request);
    }

    /// <summary>What a file's breakpoints ask of the debugger, at their built lines, to tell a change worth sending.</summary>
    private string Signature(string path) => breakpoints.Muted ? "muted" :
        string.Join(';', breakpoints.In(path).Where(b => b.Enabled).Select(b =>
            $"{(lines.IsBuilt(path, b.Line) ? $"{lines.ToBuilt(path, b.Line)}" : $"new {b.Line}")}:{b.Attribute}|{b.Condition}|{b.HitCondition}|{b.LogMessage}|{b.Suspend}"));

    /// <summary>The exception breakpoints that are on, as the protocol's exception options; none while muted, so no exception
    /// stops the program, not even one left unhandled.</summary>
    private List<(ExceptionBreakpoint Breakpoint, DapExceptionOptions Option)> ExceptionOptions() => breakpoints.Muted ? [] :
        [.. breakpoints.Exceptions.Where(e => e.Enabled).Select(e => (e, new DapExceptionOptions(
            e.Type is { } type ? [new DapExceptionPathSegment(["CLR"]), new DapExceptionPathSegment([type])] : [new DapExceptionPathSegment(["CLR"])],
            e.Scenarios.HasFlag(ExceptionScenarios.HandledInUserCode) ? "always" : e.Scenarios.HasFlag(ExceptionScenarios.HandledInExternalCode) ? "userUnhandled" : "unhandled")
        {
            Scenarios = ExceptionScenarioNames.Of(e.Scenarios),
            Condition = e.Condition,
            HitCondition = e.HitCondition,
            LogMessage = e.LogMessage,
            Suspend = e.Suspend,
        }))];

    /// <summary>Sends the exception breakpoints, and records what the debugger made of each.</summary>
    private async Task SendExceptionBreakpointsAsync()
    {
        List<(ExceptionBreakpoint Breakpoint, DapExceptionOptions Option)> sending = ExceptionOptions();
        exceptionsSent = ExceptionSignature();
        SetExceptionBreakpointsResponse response = await connection.RequestAsync<SetExceptionBreakpointsResponse>(
            DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], [], [.. sending.Select(s => s.Option)]));
        IReadOnlyList<DapBreakpoint> said = response.Breakpoints ?? [];
        breakpoints.ReportExceptions(this, [.. said.Select(b => (b.Verified ? BreakpointStatus.Bound : BreakpointStatus.Invalid, b.Message))], [.. sending.Select(s => s.Breakpoint)]);
    }

    private void OnExceptionsChanged(object? sender, EventArgs e)
    {
        // A report from the debugger raises it too: only what changes the request goes back to it.
        if (State is DebugState.Running or DebugState.Paused && disconnected is null && exceptionsSent != ExceptionSignature())
        {
            Sending(SendExceptionBreakpointsAsync());
        }
    }

    /// <summary>The data breakpoints on this session's objects that are on; none while muted.</summary>
    private List<DataBreakpoint> DataSending() => breakpoints.Muted ? [] : [.. breakpoints.Data.Where(d => d.Enabled && d.Session == this)];

    /// <summary>Sends the data breakpoints, and records what the debugger made of each.</summary>
    private async Task SendDataBreakpointsAsync()
    {
        List<DataBreakpoint> sending = DataSending();
        dataSent = DataSignature();
        SetDataBreakpointsResponse response;
        try
        {
            response = await connection.RequestAsync<SetDataBreakpointsResponse>(DapCommands.SetDataBreakpoints,
                new SetDataBreakpointsArguments([.. sending.Select(d => new DapDataBreakpoint(d.DataId, "write", d.Condition, d.HitCondition, d.LogMessage, d.Suspend))]));
        }
        catch (DapException refused)
        {
            // A refusal of the whole request leaves none of them watching, each saying why.
            breakpoints.ReportData(this, [.. sending.Select(_ => (BreakpointStatus.Invalid, (string?)refused.Reason))], sending);
            return;
        }
        dataById.Clear();
        for (int i = 0; i < Math.Min(sending.Count, response.Breakpoints.Count); i++)
        {
            if (response.Breakpoints[i].Id is int id)
            {
                dataById[id] = sending[i];
            }
        }
        breakpoints.ReportData(this, [.. response.Breakpoints.Select(b => (b.Verified ? BreakpointStatus.Bound : BreakpointStatus.Invalid, b.Message))], sending);
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        // A report from the debugger raises it too: only what changes the request goes back to it.
        if (State is DebugState.Running or DebugState.Paused && disconnected is null && DataBreakpointsUnavailable is null && dataSent != DataSignature())
        {
            Sending(SendDataBreakpointsAsync());
        }
    }

    private string DataSignature() => string.Join(';', DataSending().Select(d => $"{d.DataId}|{d.Condition}|{d.HitCondition}|{d.LogMessage}|{d.Suspend}"));

    /// <summary>The property breakpoints on this session's app that are on; none while muted.</summary>
    private List<PropertyBreakpoint> PropertiesSending() => breakpoints.Muted ? [] : [.. breakpoints.Properties.Where(p => p.Enabled && p.Session == this)];

    /// <summary>XAML Binding Errors when it is on; never while muted.</summary>
    private BindingErrorBreakpoint? BindingErrorsSending() => !breakpoints.Muted && breakpoints.BindingErrors.Enabled ? breakpoints.BindingErrors : null;

    /// <summary>Sends the function breakpoints, which replace those sent before. A property breakpoint is one on the method
    /// the inspector in the app calls at a watched property's change, its condition naming the breakpoint first; its hit count
    /// is the inspector's, which counts only its own property's changes, and one the inspector cannot read is refused here, as
    /// a line's is. XAML Binding Errors is one on the method each failed binding calls, its condition and log message
    /// reading the failure as <c>$bindingError</c>, the method's parameter.</summary>
    private async Task SendFunctionBreakpointsAsync()
    {
        List<(PropertyBreakpoint, BreakpointStatus, string?)> said = [];
        List<PropertyBreakpoint> sending = [];
        foreach (PropertyBreakpoint breakpoint in PropertiesSending())
        {
            if (breakpoint.HitCondition is { } hits && !HitCondition.TryParse(hits, out _, out string? problem))
            {
                said.Add((breakpoint, BreakpointStatus.Invalid, problem));
            }
            else
            {
                sending.Add(breakpoint);
            }
        }
        BindingErrorBreakpoint? bindingErrors = BindingErrorsSending();
        functionsSent = FunctionSignature();
        List<DapFunctionBreakpoint> requests = [.. sending.Select(p => new DapFunctionBreakpoint(Inspector.Wire.Changed, EngineCondition(p), null, p.LogMessage, p.Suspend))];
        if (bindingErrors is not null)
        {
            requests.Add(new DapFunctionBreakpoint(Inspector.Wire.Failed, BindingErrorTerms(bindingErrors.Condition), bindingErrors.HitCondition,
                BindingErrorTerms(bindingErrors.LogMessage) ?? (bindingErrors.Suspend ? null : BindingErrorLog), bindingErrors.Suspend));
        }
        SetBreakpointsResponse response;
        try
        {
            response = await connection.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments(requests));
        }
        catch (DapException refused)
        {
            breakpoints.ReportProperties(this, [.. said, .. sending.Select(p => (p, BreakpointStatus.Invalid, (string?)refused.Reason))]);
            breakpoints.ReportBindingErrors(this, bindingErrors is null ? BreakpointStatus.Unknown : BreakpointStatus.Invalid, bindingErrors is null ? null : refused.Reason);
            return;
        }
        propertyById.Clear();
        bindingErrorsId = null;
        for (int i = 0; i < Math.Min(sending.Count, response.Breakpoints.Count); i++)
        {
            DapBreakpoint answer = response.Breakpoints[i];
            if (answer.Id is int id)
            {
                propertyById[id] = sending[i];
            }
            said.Add((sending[i], answer.Verified ? BreakpointStatus.Bound : answer.Reason == "failed" ? BreakpointStatus.Invalid : BreakpointStatus.Unknown, answer.Message));
        }
        breakpoints.ReportProperties(this, said);
        if (bindingErrors is not null && response.Breakpoints.Count > sending.Count)
        {
            DapBreakpoint answer = response.Breakpoints[sending.Count];
            bindingErrorsId = answer.Id;
            ReportBindingErrors(answer);
        }
        else
        {
            breakpoints.ReportBindingErrors(this, BreakpointStatus.Unknown, null);
        }
    }

    /// <summary>What a tracepoint of XAML Binding Errors with no message of its own logs: the failure.</summary>
    private const string BindingErrorLog = "Binding error: {bindingError.Description,nq}";

    /// <summary>A condition or log message of XAML Binding Errors as the debugger reads it: <c>$bindingError</c>, the name
    /// Variables shows the failure by, is the parameter of the method it stops in.</summary>
    private static string? BindingErrorTerms(string? text) => text?.Replace("$bindingError", "bindingError", StringComparison.Ordinal);

    /// <summary>A property breakpoint's condition as the debugger is given it, on the one method every such breakpoint
    /// stops in: its own id, then the person's condition, which a message about it names whole.</summary>
    public static string EngineCondition(PropertyBreakpoint breakpoint) =>
        $"breakpoint == {breakpoint.Id}" + (breakpoint.Condition is { } condition ? $" && ({condition})" : string.Empty);

    /// <summary>What the debugger said of XAML Binding Errors: until an Avalonia or WPF app's inspector has loaded, it has nowhere
    /// to stop, which is no fault of the breakpoint.</summary>
    private void ReportBindingErrors(DapBreakpoint answer) =>
        breakpoints.ReportBindingErrors(this, answer.Verified ? BreakpointStatus.Bound : answer.Reason == "failed" ? BreakpointStatus.Invalid : BreakpointStatus.Unknown,
            answer.Verified || answer.Reason == "failed" ? answer.Message : NoInspectedApp);

    /// <summary>Why XAML Binding Errors has not bound in a program the debugger runs.</summary>
    private const string NoInspectedApp = "No Avalonia or WPF app has started under the debugger with the Live Visual Tree's inspector, which tells of its failed bindings";

    private void OnFunctionsChanged(object? sender, EventArgs e)
    {
        // A report from the debugger raises it too: only what changes the request goes back to it.
        if (State is DebugState.Running or DebugState.Paused && disconnected is null && functionsSent != FunctionSignature())
        {
            functionsAnswered = SendFunctionBreakpointsAsync();
        }
    }

    /// <summary>A stop at a breakpoint the debugger gave an id in an answer still on its way back (the answer is read off
    /// the thread pool, the event straight onto the UI thread, so the stop can come first), taken once that answer is:
    /// a property breakpoint's or XAML Binding Errors' stop is then known for one, and shown in the code that did it.</summary>
    private async Task TakeStopAfter(Task answered, StoppedEvent stopped)
    {
        await Task.WhenAny(answered);
        if (disconnected is null && State == DebugState.Running)
        {
            TakeStop(stopped);
        }
    }

    private void TakeStop(StoppedEvent stopped)
    {
        stops++;
        stoppedAt = stopped;
        StoppedThread = stopped.ThreadId;
        StopReason = stopped.Reason;
        DataStop = stopped.Reason == StopReasons.DataBreakpoint && stopped.HitBreakpointIds?.Select(id => dataById.GetValueOrDefault(id)).FirstOrDefault(d => d is not null) is { } watched
            ? (watched, stopped.Text)
            : null;
        PropertyStop = stopped.Reason == StopReasons.Breakpoint ? stopped.HitBreakpointIds?.Select(id => propertyById.GetValueOrDefault(id)).FirstOrDefault(p => p is not null) : null;
        BindingErrorStop = stopped.Reason == StopReasons.Breakpoint && bindingErrorsId is int failed && stopped.HitBreakpointIds?.Contains(failed) == true;
        if (runToCursor is { } target)
        {
            runToCursor = null;
            IdeErrors.Observe(SendBreakpointsAsync(target.Path));
        }
        SetState(DebugState.Paused);
    }

    private string FunctionSignature() =>
        string.Join(';', PropertiesSending().Select(p => $"{p.Id}|{p.Condition}|{p.HitCondition}|{p.LogMessage}|{p.Suspend}"))
        + (BindingErrorsSending() is { } b ? $";binding|{b.Condition}|{b.HitCondition}|{b.LogMessage}|{b.Suspend}" : string.Empty);

    private string ExceptionSignature() => string.Join(';', ExceptionOptions().Select(s =>
        $"{s.Breakpoint.Type}|{string.Join(',', s.Option.Scenarios ?? [])}|{s.Option.Condition}|{s.Option.HitCondition}|{s.Option.LogMessage}|{s.Option.Suspend}"));

    private void OnEvent(string name, JsonElementCopy body)
    {
        switch (name)
        {
            case DapEvents.Initialized:
                initialized.TrySetResult();
                break;
            case DapEvents.Stopped when disconnected is null:
                StoppedEvent stopped = body.Read<StoppedEvent>();
                if (stopped.Reason == StopReasons.Breakpoint && !functionsAnswered.IsCompleted
                    && stopped.HitBreakpointIds?.Any(id => !byId.ContainsKey(id) && !propertyById.ContainsKey(id) && id != bindingErrorsId) == true)
                {
                    IdeErrors.Observe(TakeStopAfter(functionsAnswered, stopped));
                    break;
                }
                TakeStop(stopped);
                break;
            case DapEvents.Breakpoint:
                BreakpointEvent changed = body.Read<BreakpointEvent>();
                if (changed.Breakpoint.Id is int id && byId.TryGetValue(id, out LineBreakpoint? breakpoint))
                {
                    breakpoints.Report(this, breakpoint.Path, [Status(breakpoint.Path, changed.Breakpoint)], [breakpoint]);
                }
                else if (changed.Breakpoint.Id is int functionId && propertyById.TryGetValue(functionId, out PropertyBreakpoint? property))
                {
                    (BreakpointStatus status, string? message, _) = Status(changed.Breakpoint);
                    breakpoints.ReportProperty(this, property, status, message);
                }
                else if (changed.Breakpoint.Id is int bound && bound == bindingErrorsId)
                {
                    ReportBindingErrors(changed.Breakpoint);
                }
                break;
            case DapEvents.Process:
                ProcessEvent started = body.Read<ProcessEvent>();
                if (started.SystemProcessId is int processId)
                {
                    processLocal = started.IsLocalProcess != false;
                    // A program elsewhere has an id of its machine's: holding this machine's process by it could kill another.
                    if (started.StartMethod == DapCommands.Launch && IsLocal)
                    {
                        launched ??= Hold(processId);
                    }
                    processStarted.TrySetResult(processId);
                }
                break;
            case DapEvents.Exited:
                ExitCode = body.Read<ExitedEvent>().ExitCode;
                programEnded = true;
                break;
            case DapEvents.Terminated:
                programEnded = true;
                IdeErrors.Observe(StopAsync());
                break;
        }
    }

    private static (BreakpointStatus, string?, int?) Status(DapBreakpoint breakpoint) =>
        (breakpoint.Verified ? BreakpointStatus.Bound : breakpoint.Reason == "failed" ? BreakpointStatus.Invalid : BreakpointStatus.Unknown, breakpoint.Message, breakpoint.Line - 1);

    /// <summary>What the debugger said of a file's breakpoint, the line it bound to (the compiler's, in the build) where that
    /// line's code is now, as the editor counts lines.</summary>
    private (BreakpointStatus, string?, int?) Status(string path, DapBreakpoint breakpoint)
    {
        (BreakpointStatus status, string? message, int? line) = Status(breakpoint);
        return (status, message, line is int bound and >= 0 ? lines.ToCurrent(path, bound) : null);
    }

    private void SetState(DebugState state)
    {
        if (State == DebugState.Ended || State == state && state != DebugState.Paused)
        {
            return;
        }
        if (state != DebugState.Paused)
        {
            StoppedThread = null;
            StopReason = null;
            DataStop = null;
            PropertyStop = null;
            BindingErrorStop = false;
        }
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void End(int? exitCode)
    {
        if (State == DebugState.Ended)
        {
            return;
        }
        ExitCode = exitCode ?? ExitCode ?? (Terminal?.Ended is { IsCompletedSuccessfully: true } inTerminal ? inTerminal.Result : null);
        IdeLog.Write(IdeLog.Debugger, $"[{connection.Name}] Ended debugging {Program}{(ExitCode is { } code ? $": exit code {code}" : string.Empty)}{(Failure is { } failed ? $": {failed}" : string.Empty)}");
        breakpoints.Changed -= OnBreakpointsChanged;
        breakpoints.ExceptionsChanged -= OnExceptionsChanged;
        breakpoints.DataChanged -= OnDataChanged;
        breakpoints.PropertiesChanged -= OnFunctionsChanged;
        breakpoints.BindingErrorsChanged -= OnFunctionsChanged;
        // What its debugger said goes, and its data breakpoints, each on a member of one of its objects.
        if (!Quiet)
        {
            breakpoints.Leave(this);
        }
        SetState(DebugState.Ended);
        IdeErrors.Observe(connection.DisposeAsync().AsTask());
    }

    /// <summary>An event's body, copied off the connection's buffer for reading on the UI thread.</summary>
    private readonly struct JsonElementCopy(System.Text.Json.JsonElement element)
    {
        private readonly System.Text.Json.JsonElement element = element.ValueKind == System.Text.Json.JsonValueKind.Undefined ? element : element.Clone();

        public T Read<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(
            element.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : element.GetRawText(), DapConnection.Options)!;
    }
}
