using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
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

/// <summary>The debugger a session's adapter is: Passenger's own engine, for .NET's code, a Godot game's GDScript, over
/// the game's own connection (<see cref="Godot.GodotScriptAdapter"/>), or a Blazor app's WebAssembly half, in a Chromium
/// browser through .NET's browser debug proxy (<see cref="WebAssemblyAdapter"/>).</summary>
public enum DebugEngine
{
    Managed,
    GDScript,
    WebAssembly,
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
    private const string NotBuilt = "Its line was written after the program being debugged was built: restart debugging to build it";

    /// <summary>Why a GDScript breakpoint on a line written since its game loaded the script is not sent.</summary>
    private const string NotLoaded = "Its line was written after the game loaded its script: save it with Hot Reload on, and the game reloads it";

    /// <summary>The projects of the program debugged whose code runs in the browser on .NET's WebAssembly runtime (their project
    /// files), and the program's own project, from its plan (<see cref="Run.LaunchPlan.InBrowser"/>): a WebAssembly session
    /// takes their files' breakpoints; where no browser debugs them, a breakpoint in their files says it stops only where the
    /// program runs their code itself, or never where the program is their development server.</summary>
    public (IReadOnlyList<string> Projects, string Program)? InBrowser { get; set; }

    /// <summary>The project of <see cref="InBrowser"/> whose own folders hold a file, if one does.</summary>
    private string? BrowserProjectOf(string path) =>
        InBrowser?.Projects.FirstOrDefault(p => ProjectFolders.Holds(System.IO.Path.GetDirectoryName(p)!, path));

    private readonly AdapterEnd adapter;
    private readonly DapConnection connection;

    /// <summary>Where the adapter runs when not beside the app, whose path map paths cross at the protocol's edge.</summary>
    private readonly AdapterPlace? place;

    private string ToProgram(string path) => place?.ToProgram(path) ?? path;

    private string ToIde(string path) => place?.ToIde(path) ?? path;
    private readonly BreakpointStore breakpoints;
    private readonly BuiltLines lines;

    /// <summary>The column (from one) a line (from zero) of a file asks for, a XAML attribute's (by its name and the column
    /// from zero its name began at), a C# lambda's (by its column from zero) or the line's own: in XAML
    /// (<see cref="XamlBreakpoints.Column"/>), at the lambda's body and where C#'s code begins; null for the whole line.</summary>
    private readonly Func<string, int, string?, int?, int?> columns;
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);

    /// <summary>The exception breakpoints as last sent, and each by the id the debugger gave it.</summary>
    private string? exceptionsSent;
    private readonly Dictionary<int, ExceptionBreakpoint> exceptionById = [];

    /// <summary>The data breakpoints as last sent, and each by the id the debugger gave it.</summary>
    private string? dataSent;
    private readonly Dictionary<int, DataBreakpoint> dataById = [];

    /// <summary>The function breakpoints (the property breakpoints', the method breakpoints' and XAML Binding Errors') as last
    /// sent, and the breakpoint each is by the id the debugger gave it.</summary>
    private string functionsSent = string.Empty;
    private readonly Dictionary<int, PropertyBreakpoint> propertyById = [];

    /// <summary>The ids of the function breakpoints that stand for breakpoints in WPF XAML, each kept in <see cref="byId"/>.</summary>
    private readonly HashSet<int> xamlIds = [];
    private readonly Dictionary<int, MethodBreakpoint> methodById = [];
    private int? bindingErrorsId;

    /// <summary>The function breakpoints' last request, until its answer (their new ids) has been read.</summary>
    private Task functionsAnswered = Task.CompletedTask;

    /// <summary>The breakpoint requests a change made while the program runs or is paused sent, until each is answered.</summary>
    private readonly List<Task> breakpointsSending = [];

    private readonly Dictionary<int, LineBreakpoint> byId = [];
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> errors = new();

    /// <summary>Done once the adapter's error stream has been read to its end.</summary>
    private readonly Task errorsHeard;

    /// <summary>Run to Cursor's place, as the debugger is asked for it (<see cref="Asked"/>), until the next stop.</summary>
    private (string Path, DapSourceBreakpoint At)? runToCursor;

    /// <summary>How many stops the adapter has told of, so a resume's answer that comes after the next stop leaves it be.</summary>
    private int stops;

    /// <summary>How many stops the adapter has told of, the one paused at now the last.</summary>
    public int Stops => stops;

    /// <summary>Whether an attached agent set the program going last (its start, a resume, a step or a pause came from an
    /// agent's tool call, an action it ran or its input into Passenger's windows), so the stop it comes to is the agent's.</summary>
    public bool AgentDrives { get; private set; } = AgentsFlow;

    private static bool AgentsFlow => AgentAsks.Beside || AgentAsks.Inputting;

    /// <summary>The stop last taken, shown again when the continue or step that left it is refused.</summary>
    private StoppedEvent? stoppedAt;

    /// <summary>When the program was last set going (its start, a continue or step, or by itself), on the IDE's clock,
    /// until the stop it comes to.</summary>
    private long? setGoing;

    /// <summary>How long the program ran to the stop paused at now, from what last set it going to the stop being heard, and
    /// whether that was the session's start; null while it runs, and at a stop nothing set going before (Set Next Statement's).</summary>
    public (TimeSpan Time, bool SinceStart)? Ran { get; private set; }
    private volatile bool killed;

    /// <summary>The adapter said the program ended: with its exit code, or (attached, where none can be known) without.</summary>
    private bool programEnded;

    /// <summary>The program was heard gone other than from the adapter (<see cref="ProgramGone"/>).</summary>
    private bool goneHeard;

    /// <summary>The adapter's one disconnect, once asked: nothing more is asked of it, since it may go before answering.</summary>
    private Task? disconnected;

    /// <summary>Whether the conversation with the adapter is over (<see cref="End"/>): a session shown ended as its program
    /// ended may still wait for its adapter to go.</summary>
    private bool closed;

    /// <summary>Done once the disconnect is asked, which ends a start's waits.</summary>
    private readonly TaskCompletionSource ending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Why a start failed that was stopped (Stop, Detach, the window's close) before it was done.</summary>
    public const string StoppedStarting = "the session was stopped as it started";

    /// <summary>The debug adapter the app carries, beside it in debugger/, which the helper host runs.</summary>
    public static string AdapterPath => Path.Combine(AppContext.BaseDirectory, "debugger", "Passenger.DebugAdapter.dll");

    /// <summary>How the adapter starts: beside the app (in the debugger's 32-bit host for a 32-bit program, in
    /// <paramref name="x86Host"/>), or where the program is (a container's, over docker exec).</summary>
    private static ProcessStartInfo AdapterStart(AdapterPlace? place, bool elsewhere, string? x86Host)
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
            List<string> arguments = [AdapterFlags.Interpreter, AdapterFlags.SymbolCache + SymbolCache];
            if (AdapterEnvironment.EngineLog is { } engineLog)
            {
                arguments.Add(AdapterFlags.EngineLogging + engineLog);
            }
            if (elsewhere)
            {
                arguments.Add(AdapterFlags.Remote);
            }
            // On Passenger's runtime or the x86 host's own: the program it debugs may run on another, or carry its own.
            start = ChildProcesses.HelperCommand(path, arguments, host: x86Host);
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return start;
    }

    private DebugSession(AdapterEnd adapter, BreakpointStore breakpoints, BuiltLines lines, Action<string> log, Action<OutputEvent> output, Func<string, int, string?, int?, int?>? columns,
        bool elsewhere, AdapterPlace? place, bool quiet = false, DebugEngine engine = DebugEngine.Managed)
    {
        Engine = engine;
        Elsewhere = elsewhere || place is not null;
        this.place = place;
        this.breakpoints = breakpoints;
        this.lines = lines;
        this.columns = columns ?? ((_, _, _, column) => column + 1);
        // The log is called from the adapter's error stream and the connection's own threads: each line lands on the UI's.
        log = UiThread.Marshal(log);
        this.adapter = adapter;
        // What the adapter and the conversation say goes to the debugger's log as well as the console, so it outlives the session.
        string name = adapter.Name;
        Action<string> console = log;
        log = line =>
        {
            IdeLog.Write(IdeLog.Debugger, $"[{name}] {line}");
            console(line);
        };
        errorsHeard = adapter.HearErrors(line =>
        {
            lock (errors)
            {
                errors.Enqueue(line);
                if (errors.Count > ErrorLines)
                {
                    errors.Dequeue();
                }
            }
            log(line);
        });
        IdeErrors.Observe(errorsHeard);
        IdeErrors.Observe(HearEndAsync(log));
        connection = new DapConnection(adapter.FromAdapter, adapter.ToAdapter, log) { Name = name };
        connection.EventReceived += (name, body) =>
        {
            if (name == DapEvents.Output)
            {
                // Here, in order with the events around it, rather than a job for the window at each of a program's writes.
                if (body.ValueKind == JsonValueKind.Object && body.Deserialize<OutputEvent>(DapConnection.Options) is { } written)
                {
                    output(written);
                    Heard?.Invoke(written);
                }
                return;
            }
            if (name == DapEvents.Snapshot)
            {
                // Its frames' sources named here, off the window's thread, as a stop's are.
                if (body.ValueKind == JsonValueKind.Object && body.Deserialize<SnapshotEvent>(DapConnection.Options) is { } taken)
                {
                    SnapshotEvent named = taken with { Frames = [.. taken.Frames.Select(f => f with { Frame = Named(f.Frame) })] };
                    UiThread.Post(() => TakeSnapshot(named));
                }
                return;
            }
            JsonElementCopy copy = new(body);
            // Heard here, ahead of whatever the window's thread is doing, so a stop's time is the program's.
            long heard = Stopwatch.GetTimestamp();
            UiThread.Post(() => OnEvent(name, copy, heard));
        };
        Quiet = quiet;
        if (!quiet)
        {
            JoinBreakpoints();
        }
    }

    private void JoinBreakpoints()
    {
        breakpoints.Join(this);
        breakpoints.Changed += OnBreakpointsChanged;
        breakpoints.ExceptionsChanged += OnExceptionsChanged;
        breakpoints.DataChanged += OnDataChanged;
        breakpoints.PropertiesChanged += OnFunctionsChanged;
        breakpoints.MethodsChanged += OnFunctionsChanged;
        breakpoints.BindingErrorsChanged += OnFunctionsChanged;
    }

    /// <summary>Attached for Hot Reload alone (a container run's service or a Godot game under Run with Hot Reload): no
    /// breakpoint is sent, no exception stops it, and the breakpoints never hear of it, until it debugs (<see cref="DebugAsync"/>).</summary>
    public bool Quiet { get; private set; }

    /// <summary>A quiet session made one that debugs its program as it runs (Attach to a service it runs with Hot Reload):
    /// the breakpoints joined and sent, and the exceptions they stop at; done once the debugger has them.</summary>
    public Task DebugAsync()
    {
        UiThread.Check();
        if (!Quiet || State == DebugState.Ended || disconnected is not null)
        {
            return Task.CompletedTask;
        }
        Quiet = false;
        AgentDrives = AgentsFlow;
        JoinBreakpoints();
        Task sending = SendChangedAsync();
        Sending(sending);
        return sending;
    }

    public event EventHandler? StateChanged;

    /// <summary>A snapshot point's capture, with the point it is of where the session still knows it.</summary>
    public event Action<SnapshotEvent, CodeBreakpoint?>? SnapshotTaken;

    /// <summary>Each output event as it comes, on the connection's thread in order, beside the console it goes to: for what
    /// reads a program's words as it writes them (a test run's game).</summary>
    public event Action<OutputEvent>? Heard;

    /// <summary>A listener of <see cref="Heard"/> handing on each whole line the program wrote, a part line held until its end.</summary>
    public static Action<OutputEvent> Lines(Action<string> line)
    {
        System.Text.StringBuilder held = new();
        return written =>
        {
            if (written.Category is "telemetry" or "important")
            {
                return;
            }
            foreach (char c in written.Output)
            {
                if (c == '\n')
                {
                    line(held.ToString().TrimEnd('\r'));
                    held.Clear();
                }
                else
                {
                    held.Append(c);
                }
            }
        };
    }

    /// <summary>Which debugger its adapter is, and so which files' breakpoints it is sent (<see cref="Takes"/>).</summary>
    public DebugEngine Engine { get; }

    /// <summary>Whether a file's breakpoints go to this session's adapter: a GDScript file's to a GDScript session, a file of
    /// the projects a WebAssembly session's browser runs to it, every other file's (and a Blazor Web App's client's, whose
    /// components its server prerenders) to Passenger's engine.</summary>
    public bool Takes(string path) => Engine switch
    {
        DebugEngine.GDScript => Godot.GodotScriptAdapter.Takes(path),
        DebugEngine.WebAssembly => InBrowser is { } inBrowser && WebAssemblyAdapter.Takes(inBrowser.Projects, path),
        _ => !Godot.GodotScriptAdapter.Takes(path),
    };

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

    /// <summary>Whether the program reads its XAML as it runs (a WPF app's BAML, with Passenger's inspector in it): a
    /// breakpoint in a .xaml file is then a function breakpoint on the inspector's call for each element made from XAML
    /// (<see cref="Inspector.Wire.Loaded"/>), its condition the element's place, where no code of the app's stands; set as
    /// the session starts, before any breakpoint is sent.</summary>
    public bool XamlAtRunTime { get; set; }

    /// <summary>The breakpoint in a WPF XAML file the program stopped at, as an element made from it joined its window.</summary>
    public LineBreakpoint? XamlStop { get; private set; }

    /// <summary>A .NET MAUI program's XAML as its build's generated code, read as the session starts, with each XAML file's
    /// text as the editor has it: a breakpoint in a .xaml file binds in that code (<see cref="MauiXamlCode"/>) and a frame
    /// in it is shown at the XAML; null for any other program. Set before any breakpoint is sent.</summary>
    public (Task<MauiXamlCode> Code, Func<string, TextSnapshot?> Texts)? MauiXaml { get; set; }

    /// <summary>Why no step goes on from the stop, while paused at one its debugger only continues from (a Godot script
    /// error); null where steps go on.</summary>
    public string? NoSteps { get; private set; }

    /// <summary>Paused at XAML Binding Errors: a binding of the app failed where it stopped.</summary>
    public bool BindingErrorStop { get; private set; }

    /// <summary>Why the debugger can set no data breakpoint where it runs, or null where it can.</summary>
    public string? DataBreakpointsUnavailable { get; private set; } = "The debugger has not started";

    /// <summary>Whether the debugger can move where a paused thread goes on (Set Next Statement): Godot's GDScript one cannot.</summary>
    public bool MovesNextStatement { get; private set; }

    /// <summary>Whether its adapter reads the bytes of its paused .NET program, wherever the program runs.</summary>
    public bool ReadsMemory { get; private set; }

    private readonly List<DapModule> modules = [];

    /// <summary>The modules the program has loaded and not unloaded, in the order its debugger told of them, each path as
    /// the IDE names it.</summary>
    public IReadOnlyList<DapModule> Modules => modules;

    /// <summary>A module loaded, unloaded, or had its symbols loaded.</summary>
    public event EventHandler? ModulesChanged;

    /// <summary>Why the session's debugger forces no step, where it cannot; null where it does (Force Step Into and Over,
    /// Force Run to Cursor).</summary>
    public string? ForcesWhyNot { get; private set; } = "The debugger has not started";

    /// <summary>Why the session's debugger marks no object, where it cannot; null where it does (Mark Object).</summary>
    public string? MarksWhyNot { get; private set; } = "The debugger has not started";

    /// <summary>The objects marked, each by its label, in the order they were marked; gone with the session.</summary>
    public IReadOnlyList<string> Marks => marks;

    private readonly List<string> marks = [];

    /// <summary>Marks the object an expression's value is in a frame, answering its label ($1); one the debugger refuses
    /// throws its reason.</summary>
    public async Task<string> MarkObjectAsync(string expression, int frameId)
    {
        string label = (await connection.RequestAsync<MarkObjectResponse>(DapCommands.MarkObject, new MarkObjectArguments(expression, frameId))).Label;
        if (!marks.Contains(label))
        {
            marks.Add(label);
        }
        return label;
    }

    /// <summary>Lets a marked object go: its label names nothing from then on.</summary>
    public async Task UnmarkObjectAsync(string label)
    {
        marks.Remove(label);
        await connection.RequestAsync(DapCommands.UnmarkObject, new UnmarkObjectArguments(label));
    }

    /// <summary>Why the session's debugger tells of no modules, where it cannot; null where it does.</summary>
    public string? ModulesUnlisted => Engine switch
    {
        DebugEngine.GDScript => "Godot's GDScript debugger loads scripts, not .NET modules: a C# game's own session lists its modules",
        DebugEngine.WebAssembly => "The browser's .NET debugger tells of no modules",
        _ => null,
    };

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

    /// <summary>Set once Stop, or a start in the session's place, asks the debugger to end the program.</summary>
    private bool endAsked;

    /// <summary>The program, when it runs in a tab of the Terminal window rather than the adapter's console: where it reads
    /// and writes, and how it ended, which the adapter cannot tell of a program it did not start on macOS and Linux.</summary>
    public Run.TerminalRun? Terminal { get; private set; }

    /// <summary>Why the session ended other than by the program ending or being stopped: the adapter crashed, or said the
    /// runtime's debugging of the program failed.</summary>
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

    /// <summary>What Passenger carries into a program it debugs, by assembly name: its inspectors and its Hot Reload and
    /// test hooks, which the debugger never takes for the user's code, however they were built.</summary>
    private static IReadOnlyList<string> Carried { get; } = [.. ((string[])[LiveTree.LiveApp.InspectorPath, LiveTree.LiveApp.WpfInspectorPath,
        LiveTree.LiveApp.MauiInspectorPath, UpdateHandlerHook.HookPath, Testing.TestFrames.HookPath]).Select(path => Path.GetFileNameWithoutExtension(path))];

    /// <summary>Where the adapter keeps symbols it decompiles for assemblies that ship none, and prunes them as it starts:
    /// under the state folder's cache/.</summary>
    public static string SymbolCache => Path.Combine(Passenger.App.State.StateStore.DefaultRoot, "cache", "symbols");

    /// <summary>Where an adapter in a container keeps the engine's log while PASSENGER_ENGINE_LOG asks for one.</summary>
    public const string ContainerEngineLog = "/tmp/passenger-engine.log";

    /// <summary>Starts the adapter (a 32-bit program's in the 32-bit host in <paramref name="x86Host"/>) and launches a plan's
    /// program under it, with the store's breakpoints. What the program writes, and the debugger's own words ("console"), go
    /// to <paramref name="output"/> on the connection's thread, in order; with <paramref name="runInTerminal"/> it runs in the
    /// terminal it starts, its output there. <paramref name="starting"/> is handed the session as it begins, so Stop can end
    /// it while it starts; with <paramref name="elsewhere"/> the program is on another machine.</summary>
    public static Task<DebugSession> LaunchAsync(Run.LaunchPlan plan, BreakpointStore breakpoints, IReadOnlyList<string> own, BuiltLines lines, Action<string> log, Action<OutputEvent> output,
        Func<RunInTerminalArguments, Task<Run.TerminalRun>>? runInTerminal = null, Func<string, int, string?, int?, int?>? columns = null, bool hotReload = false,
        Action<DebugSession>? starting = null, bool elsewhere = false, AdapterPlace? place = null, string? x86Host = null)
    {
        return StartAsync(OwnAdapter(place, elsewhere, place is null && !elsewhere && plan.RunsAsX86, x86Host), plan.Built ?? plan.Program, DapCommands.Launch, new LaunchArguments
        {
            Program = place?.ToProgram(plan.Program) ?? plan.Program,
            Args = plan.Arguments,
            Cwd = place?.ToProgram(plan.WorkingDirectory) ?? plan.WorkingDirectory,
            Env = runInTerminal is null ? ToolWindows.ConsoleSink.TellColours(plan.Environment) : plan.Environment,
            Console = runInTerminal is null ? ConsoleKinds.InternalConsole : ConsoleKinds.IntegratedTerminal,
            JustMyCode = true,
            StopAtEntry = false,
            HotReload = hotReload,
            Presentations = Godot.GodotValues.Presentations,
            Carried = Carried,
            Own = own,
            Unseen = plan.OnOwnDesktop ? true : null,
        }, breakpoints, lines, log, output, runInTerminal, columns, starting, elsewhere, place);
    }

    /// <summary>Starts the adapter (a 32-bit program's in the 32-bit host in <paramref name="x86Host"/>) and attaches it to a
    /// running .NET process (one Attach to Process chose, or a test host VSTest started), with the store's breakpoints;
    /// <paramref name="starting"/> is handed the session as it begins. With <paramref name="hotReload"/> it was started held
    /// with DOTNET_MODIFIABLE_ASSEMBLIES=debug, so its code may change as a launch's may; with <paramref name="elsewhere"/> it
    /// is not on this machine, and with <paramref name="place"/> the adapter runs where it is (a container's).</summary>
    public static Task<DebugSession> AttachAsync(int processId, BreakpointStore breakpoints, IReadOnlyList<string> own, BuiltLines lines, Action<string> log, Action<OutputEvent> output,
        Func<string, int, string?, int?, int?>? columns = null, Action<DebugSession>? starting = null, bool hotReload = false, bool elsewhere = false, AdapterPlace? place = null,
        bool quiet = false, string? x86Host = null) =>
        StartAsync(OwnAdapter(place, elsewhere, place is null && !elsewhere && DotnetProcesses.Runs32Bit(processId), x86Host),
            place is null ? $"process {processId}" : $"process {processId} in {place.Where}", DapCommands.Attach,
            new AttachArguments
            {
                ProcessId = processId, JustMyCode = true, HotReload = hotReload ? true : null, Presentations = Godot.GodotValues.Presentations, Carried = Carried,
                Own = own,
            },
            breakpoints, lines, log, output, null, columns, starting, elsewhere, place, quiet);

    /// <summary>Starts the adapter and attaches it to an app on a device through Passenger's relay in it, the app's start
    /// waiting for it there: the relay's local address and token, and the build's copies of the app's assemblies, with the
    /// store's breakpoints (none where <paramref name="quiet"/>, Run's for Hot Reload alone); <paramref name="starting"/> is
    /// handed the session as it begins.</summary>
    public static Task<DebugSession> AttachThroughRelayAsync(string program, Run.Devices.RelaySession relay, BreakpointStore breakpoints, IReadOnlyList<string> own,
        BuiltLines lines, Action<string> log,
        Action<OutputEvent> output, Func<string, int, string?, int?, int?>? columns = null, Action<DebugSession>? starting = null, bool hotReload = false, bool quiet = false) =>
        StartAsync(OwnAdapter(null, elsewhere: true, x86: false, x86Host: null), program, DapCommands.Attach,
            new AttachArguments
            {
                Relay = relay.Address, RelayModules = relay.Modules, RelayToken = relay.Token, JustMyCode = true, HotReload = hotReload ? true : null,
                Presentations = Godot.GodotValues.Presentations, Carried = Carried, Own = own,
            },
            breakpoints, lines, log, output, null, columns, starting, elsewhere: true, place: null, quiet);

    /// <summary>Attaches an adapter inside the IDE to a program another session runs (a Godot game's GDScript, over the game's
    /// own connection, <paramref name="engine"/> saying which files' breakpoints it takes), with the store's breakpoints;
    /// <paramref name="starting"/> is handed the session as it begins.</summary>
    public static Task<DebugSession> AttachInProcessAsync(InProcessEnd adapter, DebugEngine engine, string program, BreakpointStore breakpoints, BuiltLines lines,
        Action<string> log, Action<OutputEvent> output, Func<string, int, string?, int?, int?>? columns = null, Action<DebugSession>? starting = null) =>
        StartAsync(() => Task.FromResult<AdapterEnd>(adapter), program, DapCommands.Attach, new AttachArguments(), breakpoints, lines, log, output, null, columns, starting,
            elsewhere: false, place: null, engine: engine);

    /// <summary>Has an adapter inside the IDE launch a plan's program itself (a Godot game of GDScript alone, its output told as
    /// the adapter reads it, or with <paramref name="runInTerminal"/> in the terminal it starts), <paramref name="engine"/>
    /// saying which files' breakpoints it takes, with the store's breakpoints; <paramref name="starting"/> is handed the
    /// session as it begins.</summary>
    public static Task<DebugSession> LaunchInProcessAsync(InProcessEnd adapter, DebugEngine engine, Run.LaunchPlan plan, BreakpointStore breakpoints, BuiltLines lines,
        Action<string> log, Action<OutputEvent> output, Func<RunInTerminalArguments, Task<Run.TerminalRun>>? runInTerminal = null,
        Func<string, int, string?, int?, int?>? columns = null, Action<DebugSession>? starting = null) =>
        StartAsync(() => Task.FromResult<AdapterEnd>(adapter), plan.Program, DapCommands.Launch, new LaunchArguments
        {
            Program = plan.Program,
            Args = plan.Arguments,
            Cwd = plan.WorkingDirectory,
            Env = plan.Environment,
            Console = runInTerminal is null ? ConsoleKinds.InternalConsole : ConsoleKinds.IntegratedTerminal,
            Unseen = plan.OnOwnDesktop ? true : null,
        }, breakpoints, lines, log, output, runInTerminal, columns, starting, elsewhere: false, place: null, engine: engine);

    /// <summary>Passenger's own adapter, started beside the app or where the program is (<see cref="AdapterStart"/>), forked
    /// off the window's thread; for a 32-bit program (<paramref name="x86"/>) in the 32-bit host its caller had ready.</summary>
    private static Func<Task<AdapterEnd>> OwnAdapter(AdapterPlace? place, bool elsewhere, bool x86, string? x86Host) =>
        async () => new ProcessEnd(await ChildProcesses.StartAsync(AdapterStart(place, elsewhere,
            x86 ? x86Host ?? throw new InvalidOperationException($"{X86DebuggerSource.Named} was not made ready for a 32-bit program") : null)));

    /// <summary>Opens the session: the adapter told what to start, the breakpoints sent (a quiet session's none, and no
    /// exception to stop at), then configurationDone, whose answer waits for the program's runtime however long it takes. A
    /// session stopped meanwhile ends the start at once, failing it with <see cref="StoppedStarting"/>.</summary>
    private static async Task<DebugSession> StartAsync(Func<Task<AdapterEnd>> start, string program, string request, object arguments, BreakpointStore breakpoints, BuiltLines lines,
        Action<string> log, Action<OutputEvent> output, Func<RunInTerminalArguments, Task<Run.TerminalRun>>? runInTerminal, Func<string, int, string?, int?, int?>? columns,
        Action<DebugSession>? starting, bool elsewhere, AdapterPlace? place, bool quiet = false, DebugEngine engine = DebugEngine.Managed)
    {
        UiThread.Check();
        // A Stop meanwhile finds the tab still opening, which its session closes.
        AdapterEnd adapter = await start();
        DebugSession session = new(adapter, breakpoints, lines, log, output, columns, elsewhere, place, quiet, engine) { Program = program, Attached = request == DapCommands.Attach };
        IdeLog.Write(IdeLog.Debugger, $"[{session.connection.Name}] {request} {program}");
        if (runInTerminal is not null)
        {
            // Asked inside configurationDone, on the connection's thread: the terminal is the window's.
            session.connection.Handle<RunInTerminalArguments, RunInTerminalResponse>(DapCommands.RunInTerminal, asked => UiThread.InvokeAsync(async () =>
            {
                Run.TerminalRun run = await runInTerminal(asked);
                session.Terminal = run;
                return new RunInTerminalResponse(run.Debuggee);
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
                AdapterId = engine switch { DebugEngine.GDScript => "gdscript", DebugEngine.WebAssembly => "webassembly", _ => "coreclr" },
                LinesStartAt1 = true,
                ColumnsStartAt1 = true,
                PathFormat = "path",
                SupportsVariableType = true,
                SupportsMemoryReferences = engine == DebugEngine.Managed,
                SupportsRunInTerminalRequest = runInTerminal is not null,
            });
            session.DataBreakpointsUnavailable = capabilities.SupportsDataBreakpoints == true ? null
                : capabilities.DataBreakpointsUnavailable ?? "The debugger sets no data breakpoints";
            session.MovesNextStatement = capabilities.SupportsGotoTargetsRequest == true;
            session.ReadsMemory = engine == DebugEngine.Managed && capabilities.SupportsReadMemoryRequest == true;
            session.ForcesWhyNot = capabilities.SupportsForcedSteps == true ? null : engine switch
            {
                DebugEngine.GDScript => "Godot's GDScript debugger has no Just My Code to step past nor a way to pass breakpoints by",
                DebugEngine.WebAssembly => "The browser's .NET debugger steps as it does and stops at each breakpoint",
                _ => "The debugger forces no step",
            };
            session.MarksWhyNot = capabilities.SupportsMarkObject == true ? null : engine switch
            {
                DebugEngine.GDScript => "Godot's GDScript debugger marks no object",
                DebugEngine.WebAssembly => "The browser's .NET debugger names an object for one stop alone, so none is marked",
                _ => "The debugger marks no object",
            };
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
            session.setGoing = Stopwatch.GetTimestamp();
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

    /// <summary>Step Over; forced, past every breakpoint in what it steps over (Force Step Over).</summary>
    public Task StepOverAsync(bool force = false) => Resume(DapCommands.Next, thread => new GoingArguments(thread, force ? true : null));

    /// <summary>Step Into; forced, into code Just My Code passes by and past every breakpoint (Force Step Into).</summary>
    public Task StepIntoAsync(bool force = false) => Resume(DapCommands.StepIn, thread => new StepInArguments(thread, Force: force ? true : null));

    /// <summary>Smart Step Into's step: straight into one of the calls <see cref="StepInTargetsAsync"/> gave.</summary>
    public Task StepIntoTargetAsync(int target) => Resume(DapCommands.StepIn, thread => new StepInArguments(thread, target));

    /// <summary>The calls a frame's statement can step into, in the order they run, each placed where it is written (lines
    /// and columns from one, at the built lines), its source read as <paramref name="parsing"/> says its project compiles it.</summary>
    public async Task<IReadOnlyList<StepInTarget>> StepInTargetsAsync(int frameId, CSharpParseOptions parsing) =>
        (await connection.RequestAsync<StepInTargetsResponse>(DapCommands.StepInTargets,
            new StepInTargetsArguments(frameId, [.. parsing.PreprocessorSymbolNames], parsing.LanguageVersion.ToDisplayString()))).Targets;

    public Task StepOutAsync() => Resume(DapCommands.StepOut);

    /// <summary>Runs on to a line: a breakpoint the user never sees, gone again at the next stop (the stop shown again when the
    /// continue is refused among them, so no later run stops at a breakpoint no one set); forced, past every other breakpoint
    /// on the way (Force Run to Cursor).</summary>
    public Task RunToCursorAsync(string path, int line, bool force = false)
    {
        UiThread.Check();
        if (State != DebugState.Paused)
        {
            return Task.CompletedTask;
        }
        // Its own breakpoint is Run to Cursor's as one held there is, which a forced run stops at alone.
        runToCursor = (path, Asked(path, line, null) with { RunTo = true });
        return Resume(DapCommands.Continue, thread => new GoingArguments(thread, force ? true : null), before: () => SendBreakpointsAsync(path));
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
            AgentDrives = AgentsFlow;
            await connection.RequestAsync(DapCommands.Pause, new ThreadArguments(0));
        }
    }

    /// <summary>Ends the program and the adapter. A program that will not go is killed with the adapter; one in the terminal
    /// that is stopped, rather than at its own end, goes with all it started. Once the adapter has said the program ended,
    /// the session is ended as the window shows it, the adapter's own end waited for after.</summary>
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
        // The program has gone: the window shows the session ended at once, the adapter's end waited for behind it.
        if (programEnded && Terminal?.Ended.IsCompleted != false)
        {
            Ended(ExitCode);
        }
        endAsked = true;
        await EndProcessesAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        EndStarted();
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

    /// <summary>The program has gone, heard other than from the adapter (a Godot game's connection, then its process, ended):
    /// the adapter says its debugging ended itself once the last of the program's output is read; one that has not within
    /// <see cref="AdapterSilence"/> has the session ended without it, the adapter asked to end, and the log says so, so an
    /// adapter that said the program exited and never that its debugging ended leaves no program shown running.</summary>
    public void ProgramGone()
    {
        UiThread.Check();
        if (State == DebugState.Ended || goneHeard)
        {
            return;
        }
        goneHeard = true;
        IdeErrors.Observe(GoneAsync());
    }

    /// <summary>How long the adapter is waited for to say its debugging ended once its program has gone: past the 10 s its
    /// engine gives the program's last output.</summary>
    public static TimeSpan AdapterSilence => Waits.Of(TimeSpan.FromSeconds(15));

    private async Task GoneAsync()
    {
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs e)
        {
            if (State == DebugState.Ended)
            {
                ended.TrySetResult();
            }
        }
        StateChanged += Changed;
        try
        {
            Changed(this, EventArgs.Empty);
            await Task.WhenAny(ended.Task, Task.Delay(AdapterSilence));
        }
        finally
        {
            StateChanged -= Changed;
        }
        if (State == DebugState.Ended)
        {
            return;
        }
        IdeLog.Write(IdeLog.Debugger, $"[{adapter.Name}] {Program} has gone, and its adapter did not say its debugging ended within {Durations.Said(AdapterSilence)}: "
            + "the session ends without it");
        FallbackUsed = true;
        programEnded = true;
        await StopAsync();
    }

    /// <summary>Whether the session ended without its adapter saying its debugging had (<see cref="ProgramGone"/>).</summary>
    public bool FallbackUsed { get; private set; }

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
    /// attached to is let go of instead, as Detach does: it ran before the IDE did, and runs on after it; unless
    /// <paramref name="letGo"/> is false, as when Hot Reload changed it and its runtime would hold it for good.</summary>
    public Task CloseAsync(bool letGo = true)
    {
        UiThread.Check();
        return closed ? Task.CompletedTask
            : Attached && letGo && State != DebugState.Ended ? EndProcessesAsync(ClosingLetGo + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), terminate: false, letGoWithin: ClosingLetGo)
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
        Task asked = disconnected ??= BackgroundWork.Unheld(() => DisconnectAsync(terminate, letGoWithin));
        ending.TrySetResult();
        return BackgroundWork.Unheld(async () =>
        {
            await Task.WhenAny(asked, Task.Delay(answer));
            if (!await adapter.EndsWithinAsync(exit))
            {
                killed = true;
                adapter.Kill();
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

    /// <summary>Hears the adapter's end after the last of its error stream (or 2 s of waiting for it), so a crash's own
    /// words reach the console and the log before its end.</summary>
    private async Task HearEndAsync(Action<string> log)
    {
        int code = await adapter.Exited.ConfigureAwait(false);
        await Task.WhenAny(errorsHeard, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        UiThread.Post(() => OnAdapterExited(code, log));
    }

    /// <summary>The adapter's process ended: with exit code 0 as asked, or killed when it would not go; any other end is
    /// a crash, written to the IDE's log with its last words, and told in the console while the session lived.</summary>
    private void OnAdapterExited(int code, Action<string> log)
    {
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
        IdeLog.Write(IdeLog.Debugger, $"[{adapter.Name}] ended with {ExitCodes.Said(code)} as {place!.Where} stopped, debugging {Program}");
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
            IdeErrors.Write($"Passenger's debugger ({adapter.Described}) stopped with {ExitCodes.Said(code)} debugging {Program}"
                + (said.Length > 0 ? $":\n{said}" : string.Empty));
            if (State != DebugState.Ended && !programEnded)
            {
                Failure = $"the debugger itself stopped ({ExitCodes.Said(code)})";
                log($"Passenger's debugger stopped unexpectedly with {ExitCodes.Said(code)}");
            }
            else if (State != DebugState.Ended)
            {
                // Its debugging library can crash tearing down after the program has gone, whose end was told already.
                log($"Passenger's debugger stopped with {ExitCodes.Said(code)} after the program ended");
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
            // An ended one waiting for its parent, the adapter, to take its exit (a Godot game after its teardown) reads to .NET as running.
            if (!DotnetProcesses.Runs(program.Id))
            {
                EndStarted(program);
                return;
            }
            log(EndedLeftRunning(Path.GetFileName(Program), program.Id));
            ChildProcesses.Kill(program);
            adapter.Kill();
        }
    }

    /// <summary>Once Stop or a start in the session's place asked the debugger to end the program, ends what the program
    /// started, which the debugger's end of the program alone leaves running (a dev server holding the port the next start
    /// needs), as a run's Stop ends all a program started: on Windows the tree under the program, whose held handle keeps
    /// the id its children name; elsewhere what is left of the adapter's process group, which holds them.</summary>
    private void EndStarted(Process? program = null)
    {
        if (!endAsked)
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            if ((program ?? launched) is { } ended)
            {
                ChildProcesses.Kill(ended);
            }
        }
        else if (adapter.Id > 0)
        {
            ChildProcesses.KillLeft(adapter.Id);
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
    /// with a source, a hundred and more of them in an app's stack. A source a PDB names on a share or a device has no
    /// path, so neither this nor anything shown from the frame asks for it.</summary>
    public async Task<IReadOnlyList<DapStackFrame>> StackTraceAsync(int threadId)
    {
        List<DapStackFrame> frames = [.. (await connection.RequestAsync<StackTraceResponse>(DapCommands.StackTrace, new StackTraceArguments(threadId)).ConfigureAwait(false)).StackFrames.Select(Named)];
        // A frame in the code MAUI's source generator wrote of a page is shown at the page's XAML.
        if (MauiXaml is { } generated && (await generated.Code) is var code
            && frames.Select((f, i) => (Frame: f, At: i)).Where(f => f.Frame.Source?.Path is { } document && code.OfDocument(document) is not null).ToList() is { Count: > 0 } placed)
        {
            // The editor's text and the built lines are the window's thread's.
            await UiThread.InvokeAsync(() =>
            {
                Dictionary<string, TextSnapshot?> texts = new(PathNames.Comparer);
                foreach ((DapStackFrame frame, int at) in placed)
                {
                    MauiXamlPage page = code.OfDocument(frame.Source!.Path!)!;
                    if (!texts.TryGetValue(page.Xaml, out TextSnapshot? text))
                    {
                        texts[page.Xaml] = text = generated.Texts(page.Xaml);
                    }
                    if (page.Place(text, frame.Source.Path!, frame.Line, frame.Column, p => BuiltPlace(page.Xaml, p)) is { } place)
                    {
                        frames[at] = frame with { Source = new DapSource { Path = page.Xaml, Name = Path.GetFileName(page.Xaml) }, Line = place.Line + 1, Column = place.Column + 1 };
                    }
                }
                return true;
            });
        }
        // A stop at a breakpoint in WPF XAML is shown at its XAML, in place of the inspector's call it stopped in.
        if (XamlStop is { } xaml && threadId == StoppedThread && frames is [var call, ..] && call.Name.Contains(XamlCall, StringComparison.Ordinal))
        {
            frames[0] = call with
            {
                Name = $"{Path.GetFileName(xaml.Path)} (XAML)",
                Source = new DapSource { Path = xaml.Path, Name = Path.GetFileName(xaml.Path) },
                Line = xaml.Line + 1,
                Column = columns(xaml.Path, xaml.Line, xaml.Attribute, xaml.Column ?? xaml.AttributeColumn) ?? 1,
                PresentationHint = null,
            };
        }
        return frames;
    }

    /// <summary>How the debugger names the inspector's call a WPF XAML breakpoint stops in, as its frame's name holds it.</summary>
    private static readonly string XamlCall = Inspector.Wire.Loaded[(Inspector.Wire.Loaded.LastIndexOf('.', Inspector.Wire.Loaded.LastIndexOf('.') - 1) + 1)..];

    /// <summary>A frame with its source named as the IDE names its files (<see cref="StackTraceAsync"/>); asks the disk.</summary>
    private DapStackFrame Named(DapStackFrame f) =>
        f.Source is not { Path: { } named } source ? f
            : ToIde(named) is var path && !FoundPaths.Askable(path) ? f with { Source = source with { Path = null } }
            : Path.IsPathFullyQualified(path) && File.Exists(path) ? f with { Source = source with { Path = PathNames.Canonical(path) } }
            : f;

    /// <summary>Whether the program's calls are recorded (historical debugging): asked before the program starts, sent with
    /// what changed, and at once while it runs or is paused.</summary>
    public bool Records { get; private set; }

    /// <summary>What the debugger was last told of recording, null before it is told.</summary>
    private bool? recordsSent;

    /// <summary>Records the program's calls from now on, or no longer; told at once once the program runs.</summary>
    public Task RecordAsync(bool on)
    {
        UiThread.Check();
        Records = on;
        return State is DebugState.Running or DebugState.Paused && disconnected is null ? SendRecordAsync() : Task.CompletedTask;
    }

    private async Task SendRecordAsync()
    {
        bool on = Records;
        if (recordsSent != on && (on || recordsSent is not null))
        {
            recordsSent = on;
            await connection.RequestAsync(DapCommands.Record, new RecordArguments(on));
        }
    }

    /// <summary>The recording's moments on a thread, oldest first, with what it keeps at most and let go.</summary>
    public Task<TimelineResponse> TimelineAsync(int? threadId) =>
        connection.RequestAsync<TimelineResponse>(DapCommands.Timeline, new TimelineArguments(threadId));

    /// <summary>What a stopped frame's method will do from where it stands: the code it won't run and the values ahead.</summary>
    public Task<AheadResponse> AheadAsync(int frameId) =>
        connection.RequestAsync<AheadResponse>(DapCommands.Ahead, new AheadArguments(frameId));

    /// <summary>A recorded moment as a past stop, its frames' sources named as a stop's are; one let go throws why.</summary>
    public async Task<SnapshotEvent> MomentAsync(int index)
    {
        SnapshotEvent moment = await connection.RequestAsync<SnapshotEvent>(DapCommands.Moment, new MomentArguments(index)).ConfigureAwait(false);
        return moment with { Frames = [.. moment.Frames.Select(f => f with { Frame = Named(f.Frame) })] };
    }

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

    internal Task<ReadMemoryResponse> ReadMemoryAsync(string address, int count) =>
        connection.RequestAsync<ReadMemoryResponse>(DapCommands.ReadMemory, new ReadMemoryArguments(address, count));

    internal Task<ResolveMemoryResponse> ResolveMemoryAsync(string expression, int frameId) =>
        connection.RequestAsync<ResolveMemoryResponse>(DapCommands.ResolveMemory, new ResolveMemoryArguments(expression, frameId));

    /// <summary>Where the paused thread could go on from at a line of a file (from zero, the editor's), if the line is in its
    /// method.</summary>
    public async Task<GotoTarget?> GotoTargetAsync(string path, int line)
    {
        DapSourceBreakpoint at = Asked(path, line, null);
        return (await connection.RequestAsync<GotoTargetsResponse>(DapCommands.GotoTargets,
            new GotoTargetsArguments(new DapSource { Path = ToProgram(path) }, at.Line, at.Column))).Targets.FirstOrDefault();
    }

    /// <summary>Set Next Statement: the paused thread goes on from a target; it stops there anew.</summary>
    public Task GotoAsync(int thread, GotoTarget target) => connection.RequestAsync<object>(DapCommands.Goto, new GotoArguments(thread, target.Id));

    /// <summary>Whether a data breakpoint can watch a member of a value (by the reference its members were listed by, and its
    /// name there), and what it would watch, or why none can.</summary>
    public Task<DataBreakpointInfoResponse> DataBreakpointInfoAsync(int variablesReference, string name) =>
        connection.RequestAsync<DataBreakpointInfoResponse>(DapCommands.DataBreakpointInfo, new DataBreakpointInfoArguments(name, variablesReference));

    /// <summary>What Hot Reload needs of the program: its runtime's capabilities, its modules and, paused, its active
    /// statements; with <paramref name="hold"/>, a running program is stopped for the update being made (its statements
    /// read too) until <see cref="ReleaseHotReloadAsync"/>.</summary>
    public async Task<HotReloadStateResponse> HotReloadStateAsync(bool hold = false)
    {
        HotReloadStateResponse state = await connection.RequestAsync<HotReloadStateResponse>(DapCommands.HotReloadState, new HotReloadStateArguments(hold ? true : null));
        return state with { ActiveStatements = [.. state.ActiveStatements.Select(s => s.Document is { } document ? s with { Document = ToIde(document) } : s)] };
    }

    /// <summary>Lets a program held for an update go on.</summary>
    public Task ReleaseHotReloadAsync() => connection.RequestAsync(DapCommands.HotReloadRelease, new DapEmpty());

    /// <summary>Applies Edit and Continue's updates to the program, paused or running, each document named as the program's
    /// PDB names it.</summary>
    public Task<ApplyChangesResponse> ApplyChangesAsync(IReadOnlyList<DapModuleUpdate> updates) =>
        connection.RequestAsync<ApplyChangesResponse>(DapCommands.ApplyChanges, new ApplyChangesArguments(
            [.. updates.Select(u => u with
            {
                Lines = [.. u.Lines.Select(l => l with { Document = ToProgram(l.Document) })],
                FirstChanges = [.. u.FirstChanges.Select(c => c with { Document = ToProgram(c.Document) })],
            })]));

    /// <summary>Sends files' breakpoints again, at their lines now, after an update the program took: the debugger binds
    /// each again in the code it runs now, as the files' built lines are now their lines.</summary>
    public async Task SendBreakpointsAgainAsync(IEnumerable<string> paths)
    {
        UiThread.Check();
        foreach (string path in paths.Where(p => Takes(p) && (breakpoints.In(p).Count > 0 || sent.ContainsKey(p))).ToList())
        {
            if (State is DebugState.Running or DebugState.Paused && disconnected is null)
            {
                await SendBreakpointsAsync(path);
            }
        }
    }

    /// <summary>An expression's value in a frame; with <paramref name="letThreadsRun"/>, run with every thread of the program
    /// let go, which only the person asks for.</summary>
    public Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, [IdentifierOf(typeof(EvaluateContexts))] string context, bool letThreadsRun = false) =>
        connection.RequestAsync<EvaluateResponse>(DapCommands.Evaluate, new EvaluateArguments(expression, frameId, context, letThreadsRun ? true : null));

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
        (TimeSpan, bool)? ran = Ran;
        AgentDrives = AgentsFlow;
        SetState(DebugState.Running);
        // Sent with the request, never awaited before it: the adapter takes the two in turn, ahead of the stop's reads.
        Task placed = before?.Invoke() ?? Task.CompletedTask;
        UnawaitedWork.Dismiss(placed);
        setGoing = Stopwatch.GetTimestamp();
        try
        {
            await connection.RequestAsync(command, arguments?.Invoke(thread) ?? new ThreadArguments(thread));
        }
        catch (DapException) when (stops == stop && State == DebugState.Running && disconnected is null)
        {
            TakeStop(at, null, ran);
            throw;
        }
        await placed;
    }

    /// <summary>Sends a file's breakpoints at their built lines; one on a line written since the build is refused here, as
    /// the debugger refuses a line with no code. The adapter keeps those sent as they were, hit counts and all. Run to
    /// Cursor's line stops: its own breakpoint is sent there, or the one there held for it, its count going on.</summary>
    private async Task SendBreakpointsAsync(string path)
    {
        if (MauiXaml is { } generated && IsRunTimeXaml(path))
        {
            await SendGeneratedAsync(path, await generated.Code, generated.Texts(path));
            return;
        }
        if (XamlAtRunTime && IsRunTimeXaml(path))
        {
            // Its breakpoints are function breakpoints, sent with the others, where the program reads this XAML as it runs.
            sent[path] = Signature(path);
            await SendFunctionBreakpointsAsync();
            if (breakpoints.Muted)
            {
                breakpoints.Report(this, path, [.. breakpoints.In(path).Select(_ => (BreakpointStatus.Unknown, (string?)null, (int?)null))], breakpoints.In(path));
            }
            return;
        }
        // Muted, none is sent and each is as no debugger has said, though Run to Cursor's place still is.
        IEnumerable<LineBreakpoint> sending = breakpoints.Muted ? [] : breakpoints.In(path).Where(b => b.Enabled);
        // A WebAssembly app's own code never runs where its development server is debugged: none of it is sent.
        string? browser = Engine == DebugEngine.Managed ? BrowserProjectOf(path) : null;
        string? neverHit = browser is not null && InBrowser is { } inBrowser && PathNames.Same(browser, inBrowser.Program) ? Run.LaunchPlan.NeverHitInBrowser(browser) : null;
        List<LineBreakpoint> built = [.. sending.Where(b => neverHit is null && lines.IsBuilt(path, b.Line))];
        List<LineBreakpoint> unbuilt = [.. sending.Where(b => neverHit is not null || !lines.IsBuilt(path, b.Line))];
        List<DapSourceBreakpoint> requests = [.. built.Select(b => Request(path, b))];
        if (runToCursor is { } target && target.Path == path)
        {
            bool held = false;
            for (int i = 0; i < requests.Count; i++)
            {
                // A lambda's breakpoint is its own, stopping only inside the lambda.
                if (requests[i].Line == target.At.Line && built[i].Column is null)
                {
                    // Held until the stop, which Run to Cursor makes whatever its condition, hit count or going on say.
                    requests[i] = requests[i] with { RunTo = true };
                    held = true;
                }
            }
            if (!held)
            {
                requests.Add(target.At);
            }
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
        string unbuiltWhy = neverHit ?? (Engine == DebugEngine.GDScript ? NotLoaded : NotBuilt);
        breakpoints.Report(this, path,
            [.. response.Breakpoints.Take(built.Count).Select(b => Status(path, b)), .. unbuilt.Select(_ => (BreakpointStatus.Invalid, (string?)unbuiltWhy, (int?)null))],
            [.. built.Take(response.Breakpoints.Count), .. unbuilt]);
    }

    /// <summary>A .NET MAUI XAML file's breakpoints sent for the code MAUI's source generator wrote of it, at each place its
    /// element or attribute binds to (the page's InitializeComponent, an update's patch, the XAML a #line maps code to), one
    /// request a document, one the last sending had and this one has none of sent empty; one on a line written since the
    /// code was made, where the code makes or sets nothing, or of a file of no code, said why. Answered on the XAML at its own
    /// lines, bound where any of its places bound.</summary>
    private async Task SendGeneratedAsync(string path, MauiXamlCode code, TextSnapshot? text)
    {
        IEnumerable<LineBreakpoint> sending = breakpoints.Muted ? [] : breakpoints.In(path).Where(b => b.Enabled);
        (MauiXamlPage? page, string? why) = code.Of(path);
        List<(LineBreakpoint Breakpoint, IReadOnlyList<(string Document, int Line)> At)> bound = [];
        List<(LineBreakpoint Breakpoint, string Why)> unbound = [];
        foreach (LineBreakpoint breakpoint in sending)
        {
            IReadOnlyList<(string Document, int Line)>? at = page is null || text is null ? []
                : lines.IsBuilt(path, breakpoint.Line) ? page.Bind(text, breakpoint.Line, breakpoint.Attribute, breakpoint.Column ?? breakpoint.AttributeColumn, p => BuiltPlace(path, p))
                : null;
            if (at is null)
            {
                unbound.Add((breakpoint, NotBuilt));
            }
            else if (at.Count > 0)
            {
                bound.Add((breakpoint, at));
            }
            else
            {
                unbound.Add((breakpoint, why ?? $"Line {breakpoint.Line + 1} of {Path.GetFileName(path)} makes no element and sets no property"));
            }
        }
        sent[path] = Signature(path);
        foreach (int id in byId.Where(p => p.Value.Path == path).Select(p => p.Key).ToList())
        {
            byId.Remove(id);
        }
        HashSet<string> documents = [.. bound.SelectMany(b => b.At).Select(a => a.Document)];
        IEnumerable<string> emptied = generatedSent.TryGetValue(path, out HashSet<string>? before) ? before.Except(documents, StringComparer.Ordinal) : [];
        Dictionary<LineBreakpoint, DapBreakpoint> answered = [];
        foreach (string document in documents.Concat(emptied).ToList())
        {
            List<(LineBreakpoint Breakpoint, int Line)> here = [.. bound.SelectMany(b => b.At.Where(a => a.Document == document).Select(a => (b.Breakpoint, a.Line)))];
            SetBreakpointsResponse response = await connection.RequestAsync<SetBreakpointsResponse>(DapCommands.SetBreakpoints,
                new SetBreakpointsArguments(new DapSource { Path = document, Name = Path.GetFileName(document) },
                    [.. here.Select(b => Request(path, b.Breakpoint) with { Line = b.Line, Column = null })]));
            for (int i = 0; i < Math.Min(here.Count, response.Breakpoints.Count); i++)
            {
                if (response.Breakpoints[i].Id is int id)
                {
                    byId[id] = here[i].Breakpoint;
                }
                // Of a breakpoint's places, one bound stands for it, else its first answer.
                if (!answered.TryGetValue(here[i].Breakpoint, out DapBreakpoint? other) || !other.Verified && response.Breakpoints[i].Verified)
                {
                    answered[here[i].Breakpoint] = response.Breakpoints[i];
                }
            }
        }
        generatedSent[path] = documents;
        if (breakpoints.Muted)
        {
            breakpoints.Report(this, path, [.. breakpoints.In(path).Select(_ => (BreakpointStatus.Unknown, (string?)null, (int?)null))], breakpoints.In(path));
            return;
        }
        List<LineBreakpoint> answeredFor = [.. bound.Select(b => b.Breakpoint).Where(answered.ContainsKey)];
        breakpoints.Report(this, path,
            [.. answeredFor.Select(b => Status(path, answered[b])), .. unbound.Select(u => (BreakpointStatus.Invalid, (string?)u.Why, (int?)null))],
            [.. answeredFor, .. unbound.Select(u => u.Breakpoint)]);
    }

    /// <summary>The documents each .NET MAUI XAML file's breakpoints were last sent for (<see cref="SendGeneratedAsync"/>).</summary>
    private readonly Dictionary<string, HashSet<string>> generatedSent = new(StringComparer.Ordinal);

    /// <summary>Where a place in a file as the editor has it (line and column from zero) was as the program's code was made of
    /// the file, as its compiler counts them; null for one on a line written since.</summary>
    private TextPosition? BuiltPlace(string path, TextPosition place) => lines.IsBuilt(path, place.Line) ? lines.ToBuilt(path, place.Line, place.Column) : null;

    /// <summary>A breakpoint as the debugger is asked for it (<see cref="Asked"/>), with its key, the key of the one it waits
    /// for, whether it is temporary, and what it captures as a snapshot point, which never stops.</summary>
    private DapSourceBreakpoint Request(string path, LineBreakpoint breakpoint) =>
        Asked(path, breakpoint.Line, breakpoint.Attribute, breakpoint.Column ?? breakpoint.AttributeColumn) with
        {
            Condition = breakpoint.Condition,
            HitCondition = breakpoint.HitCondition,
            LogMessage = breakpoint.LogMessage,
            Suspend = breakpoint.Snapshot is null && breakpoint.Suspend,
            Key = breakpoint.Key,
            WaitFor = breakpoint.WaitsFor?.Key,
            Temporary = breakpoint.Temporary ? true : null,
            Snapshot = breakpoint.Snapshot is null ? null : true,
            Captures = breakpoint.Snapshot?.Captures,
            CaptureLimit = breakpoint.Snapshot?.Limit,
        };

    /// <summary>A line (from zero) of a file, or a lambda's or an attribute's column (from zero) on it, as the debugger is
    /// asked for it: at its built line, and at its column there (<see cref="columns"/>), both as the compiler counted them
    /// (a lone '\r' before the column begins another line in XAML).</summary>
    private DapSourceBreakpoint Asked(string path, int line, string? attribute, int? place = null)
    {
        int? column = columns(path, line, attribute, place);
        TextPosition at = lines.ToBuilt(path, line, column is int named ? named - 1 : 0);
        return new DapSourceBreakpoint(at.Line + 1, column is null ? null : at.Column + 1);
    }

    /// <summary>Sends every breakpoint whose request differs from what the debugger was last sent: each file's (one that lost
    /// its last breakpoint too), then the exception, function and data breakpoints. The start sends them all, from a list of
    /// the files taken first, and once configurationDone has answered what changed meanwhile, when no change could go.</summary>
    private async Task SendChangedAsync()
    {
        foreach (string path in breakpoints.Files.Concat(sent.Keys).Distinct(StringComparer.Ordinal).Where(Takes).ToList())
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
        if (Open() && Engine == DebugEngine.Managed && functionsSent != FunctionSignature())
        {
            await (functionsAnswered = SendFunctionBreakpointsAsync());
        }
        if (Open() && DataBreakpointsUnavailable is null && (dataSent ?? string.Empty) != DataSignature())
        {
            await SendDataBreakpointsAsync();
        }
        if (Open() && Engine == DebugEngine.Managed)
        {
            await SendRecordAsync();
        }

        bool Open() => disconnected is null && State != DebugState.Ended;
    }

    private void OnBreakpointsChanged(object? sender, string path)
    {
        // A report from the debugger raises Changed too: only what changes the request goes back to it.
        if (Takes(path) && State is DebugState.Running or DebugState.Paused && disconnected is null && (!sent.TryGetValue(path, out string? last) || last != Signature(path)))
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
            return Task.WhenAll(breakpointsSending.Append(functionsAnswered).Select(UnawaitedWork.Ended));
        }
    }

    private void Sending(Task request)
    {
        breakpointsSending.RemoveAll(t => t.IsCompleted);
        breakpointsSending.Add(request);
    }

    /// <summary>What a file's breakpoints ask of the debugger, at their built lines, to tell a change worth sending.</summary>
    private string Signature(string path) => breakpoints.Muted ? "muted" :
        Signatures.Of([.. breakpoints.In(path).Where(b => b.Enabled).Select(b => Signatures.Of(
            lines.IsBuilt(path, b.Line) ? $"{lines.ToBuilt(path, b.Line)}" : $"new {b.Line}", b.Attribute, b.Column ?? b.AttributeColumn, b.Condition, b.HitCondition, b.LogMessage, b.Suspend,
            b.Key, b.WaitsFor?.Key, b.Temporary, b.Snapshot is null, b.Snapshot?.Captures, b.Snapshot?.Limit))]);

    /// <summary>The exception breakpoints that are on, as the protocol's exception options, but those whose scenarios never stop
    /// (<see cref="ExceptionBreakpoint.Inactive"/>), which no debugger is to stop at; none while muted, so no exception stops
    /// the program, not even one left unhandled. Any exception is every type but a test framework's outcome signals
    /// (<see cref="Testing.TestOutcomeSignals"/>).</summary>
    private List<(ExceptionBreakpoint Breakpoint, DapExceptionOptions Option)> ExceptionOptions() => breakpoints.Muted ? [] :
        [.. breakpoints.Exceptions.Where(e => e.Enabled && e.Inactive is null).Select(e => (e, new DapExceptionOptions(
            [new DapExceptionPathSegment(["CLR"]), e.Type is { } type ? new DapExceptionPathSegment([type]) : new DapExceptionPathSegment(Testing.TestOutcomeSignals.Exceptions, Negate: true)],
            e.Scenarios.HasFlag(ExceptionScenarios.HandledInUserCode) ? DapBreakModes.Always
                : e.Scenarios.HasFlag(ExceptionScenarios.HandledInExternalCode) ? DapBreakModes.UserUnhandled : DapBreakModes.Unhandled)
        {
            Scenarios = ExceptionScenarioNames.Of(e.Scenarios),
            Condition = e.Condition,
            HitCondition = e.HitCondition,
            LogMessage = e.LogMessage,
            Suspend = e.Suspend,
        }))];

    /// <summary>Sends the exception breakpoints, and records what the debugger made of each; a GDScript session's are its
    /// errors' (<see cref="SendScriptErrorsAsync"/>).</summary>
    private async Task SendExceptionBreakpointsAsync()
    {
        if (Engine == DebugEngine.GDScript)
        {
            await SendScriptErrorsAsync();
            return;
        }
        List<(ExceptionBreakpoint Breakpoint, DapExceptionOptions Option)> sending = ExceptionOptions();
        exceptionsSent = ExceptionSignature();
        SetExceptionBreakpointsResponse response = await connection.RequestAsync<SetExceptionBreakpointsResponse>(
            DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], [], [.. sending.Select(s => s.Option)]));
        IReadOnlyList<DapBreakpoint> said = response.Breakpoints ?? [];
        exceptionById.Clear();
        for (int i = 0; i < Math.Min(sending.Count, said.Count); i++)
        {
            if (said[i].Id is int id)
            {
                exceptionById[id] = sending[i].Breakpoint;
            }
        }
        breakpoints.ReportExceptions(this, [.. said.Select(b => (b.Verified ? BreakpointStatus.Bound : BreakpointStatus.Invalid, b.Message))], [.. sending.Select(s => s.Breakpoint)]);
    }

    /// <summary>GDScript Errors while it is on and nothing is muted: the one filter of a GDScript session's exception
    /// breakpoints, with its condition, hit count and message.</summary>
    private ScriptErrorBreakpoint? ScriptErrorsSending() => !breakpoints.Muted && breakpoints.ScriptErrors.Enabled ? breakpoints.ScriptErrors : null;

    /// <summary>Sends GDScript Errors, on or off, and records what the debugger made of it.</summary>
    private async Task SendScriptErrorsAsync()
    {
        ScriptErrorBreakpoint? errors = ScriptErrorsSending();
        exceptionsSent = ExceptionSignature();
        SetExceptionBreakpointsResponse response = await connection.RequestAsync<SetExceptionBreakpointsResponse>(DapCommands.SetExceptionBreakpoints,
            new SetExceptionBreakpointsArguments(errors is null ? [] : [Godot.GodotScriptAdapter.ErrorsFilter], errors is null ? null
                : [new DapExceptionFilterOptions(Godot.GodotScriptAdapter.ErrorsFilter, errors.Condition) { HitCondition = errors.HitCondition, LogMessage = errors.LogMessage, Suspend = errors.Suspend }]));
        DapBreakpoint? said = response.Breakpoints is [var first, ..] ? first : null;
        breakpoints.ReportScriptErrors(this, errors is null || said is null ? BreakpointStatus.Unknown : said.Verified ? BreakpointStatus.Bound : BreakpointStatus.Invalid, said?.Message);
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

    private string DataSignature() => Signatures.Of([.. DataSending().Select(d => Signatures.Of(d.DataId, d.Condition, d.HitCondition, d.LogMessage, d.Suspend))]);

    /// <summary>The property breakpoints on this session's app that are on; none while muted.</summary>
    private List<PropertyBreakpoint> PropertiesSending() => breakpoints.Muted ? [] : [.. breakpoints.Properties.Where(p => p.Enabled && p.Session == this)];

    /// <summary>XAML Binding Errors when it is on; never while muted.</summary>
    private BindingErrorBreakpoint? BindingErrorsSending() => !breakpoints.Muted && breakpoints.BindingErrors.Enabled ? breakpoints.BindingErrors : null;

    /// <summary>The method breakpoints that are on; none while muted.</summary>
    private List<MethodBreakpoint> MethodsSending() => breakpoints.Muted ? [] : [.. breakpoints.Methods.Where(m => m.Enabled)];

    /// <summary>Sends the function breakpoints, which replace those sent before: the property breakpoints, the method
    /// breakpoints, then XAML Binding Errors. A property breakpoint is one on the method the inspector in the app calls at a
    /// watched property's change, its condition naming the breakpoint first, its hit count the inspector's (one it cannot read
    /// refused here, as a line's is). XAML Binding Errors is one on the method each failed binding calls, its condition and log
    /// message reading the failure as <c>$bindingError</c>, the method's parameter.</summary>
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
        List<MethodBreakpoint> methods = MethodsSending();
        List<LineBreakpoint> xaml = XamlSending();
        functionsSent = FunctionSignature();
        if (XamlUnplaced is { } unplaced)
        {
            // None could stop: each says why, and none is sent.
            foreach (IGrouping<string, LineBreakpoint> file in xaml.GroupBy(b => b.Path))
            {
                breakpoints.Report(this, file.Key, [.. file.Select(_ => (BreakpointStatus.Invalid, (string?)unplaced, (int?)null))], [.. file]);
            }
            xaml = [];
        }
        List<DapFunctionBreakpoint> requests = [.. sending.Select(p => new DapFunctionBreakpoint(Inspector.Wire.Changed, EngineCondition(p), null, p.LogMessage, p.Suspend)),
            .. methods.Select(m => new DapFunctionBreakpoint(m.Name, m.Condition, m.HitCondition, m.LogMessage, m.Snapshot is null && m.Suspend, m.Key, m.WaitsFor?.Key, m.Temporary ? true : null,
                m.Snapshot is null ? null : true, m.Snapshot?.Captures, m.Snapshot?.Limit))];
        if (bindingErrors is not null)
        {
            requests.Add(new DapFunctionBreakpoint(Inspector.Wire.Failed, BindingErrorTerms(bindingErrors.Condition), bindingErrors.HitCondition,
                BindingErrorTerms(bindingErrors.LogMessage) ?? (bindingErrors.Suspend ? null : BindingErrorLog), bindingErrors.Suspend));
        }
        int xamlFrom = requests.Count;
        requests.AddRange(xaml.Select(b => new DapFunctionBreakpoint(Inspector.Wire.Loaded, XamlCondition(b), b.HitCondition, b.LogMessage, b.Snapshot is null && b.Suspend, b.Key,
            b.WaitsFor?.Key, b.Temporary ? true : null)));
        SetBreakpointsResponse response;
        try
        {
            response = await connection.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments(requests));
        }
        catch (DapException refused)
        {
            foreach (IGrouping<string, LineBreakpoint> file in xaml.GroupBy(b => b.Path))
            {
                breakpoints.Report(this, file.Key, [.. file.Select(_ => (BreakpointStatus.Invalid, (string?)refused.Reason, (int?)null))], [.. file]);
            }
            breakpoints.ReportProperties(this, [.. said, .. sending.Select(p => (p, BreakpointStatus.Invalid, (string?)refused.Reason))]);
            breakpoints.ReportMethods(this, [.. methods.Select(_ => (BreakpointStatus.Invalid, (string?)refused.Reason))], methods);
            breakpoints.ReportBindingErrors(this, bindingErrors is null ? BreakpointStatus.Unknown : BreakpointStatus.Invalid, bindingErrors is null ? null : refused.Reason);
            return;
        }
        propertyById.Clear();
        methodById.Clear();
        bindingErrorsId = null;
        foreach (int gone in xamlIds)
        {
            byId.Remove(gone);
        }
        xamlIds.Clear();
        List<(BreakpointStatus, string?, int?)> xamlSaid = [];
        for (int i = 0; i < xaml.Count; i++)
        {
            DapBreakpoint? answer = xamlFrom + i < response.Breakpoints.Count ? response.Breakpoints[xamlFrom + i] : null;
            if (answer?.Id is int id)
            {
                byId[id] = xaml[i];
                xamlIds.Add(id);
            }
            xamlSaid.Add(answer is null ? (BreakpointStatus.Unknown, null, null) : Status(answer) with { Item3 = null });
        }
        foreach (IGrouping<string, (LineBreakpoint Breakpoint, (BreakpointStatus, string?, int?) Said)> file in xaml.Zip(xamlSaid).GroupBy(p => p.First.Path, p => (p.First, p.Second)))
        {
            breakpoints.Report(this, file.Key, [.. file.Select(p => p.Said)], [.. file.Select(p => p.Breakpoint)]);
        }
        for (int i = 0; i < Math.Min(sending.Count, response.Breakpoints.Count); i++)
        {
            DapBreakpoint answer = response.Breakpoints[i];
            if (answer.Id is int id)
            {
                propertyById[id] = sending[i];
            }
            said.Add((sending[i], answer.Verified ? BreakpointStatus.Bound : answer.Reason == BreakpointReasons.Failed ? BreakpointStatus.Invalid : BreakpointStatus.Unknown, answer.Message));
        }
        breakpoints.ReportProperties(this, said);
        List<(BreakpointStatus, string?)> methodsSaid = [];
        for (int i = 0; i < methods.Count && sending.Count + i < response.Breakpoints.Count; i++)
        {
            DapBreakpoint answer = response.Breakpoints[sending.Count + i];
            if (answer.Id is int id)
            {
                methodById[id] = methods[i];
            }
            (BreakpointStatus status, string? message, _) = Status(answer);
            methodsSaid.Add((status, message));
        }
        breakpoints.ReportMethods(this, methodsSaid, methods);
        if (bindingErrors is not null && response.Breakpoints.Count > sending.Count + methods.Count)
        {
            DapBreakpoint answer = response.Breakpoints[sending.Count + methods.Count];
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
        breakpoints.ReportBindingErrors(this, answer.Verified ? BreakpointStatus.Bound : answer.Reason == BreakpointReasons.Failed ? BreakpointStatus.Invalid : BreakpointStatus.Unknown,
            answer.Verified || answer.Reason == BreakpointReasons.Failed ? answer.Message : NoInspectedApp);

    /// <summary>Why XAML Binding Errors has not bound in a program the debugger runs.</summary>
    private const string NoInspectedApp = "No Avalonia or WPF app has started under the debugger with the Live Visual Tree's inspector, which tells of its failed bindings";

    private void OnFunctionsChanged(object? sender, EventArgs e)
    {
        // A report from the debugger raises it too: only what changes the request goes back to it.
        if (Engine == DebugEngine.Managed && State is DebugState.Running or DebugState.Paused && disconnected is null && functionsSent != FunctionSignature())
        {
            functionsAnswered = SendFunctionBreakpointsAsync();
        }
    }

    /// <summary>A stop at a breakpoint the debugger gave an id in an answer still on its way back (the answer is read off
    /// the thread pool, the event straight onto the UI thread, so the stop can come first), taken once that answer is:
    /// a property breakpoint's or XAML Binding Errors' stop is then known for one, and shown in the code that did it.</summary>
    private async Task TakeStopAfter(Task answered, StoppedEvent stopped, long heard)
    {
        await Task.WhenAny(answered);
        if (disconnected is null && State == DebugState.Running)
        {
            TakeStop(stopped, heard);
        }
    }

    /// <summary>Pauses at a stop, heard at <paramref name="heard"/> on the IDE's clock, or shown again (null) with the time
    /// it was first shown with.</summary>
    private void TakeStop(StoppedEvent stopped, long? heard, (TimeSpan, bool)? ranBefore = null)
    {
        Ran = heard is long at && setGoing is long from ? (Stopwatch.GetElapsedTime(from, at), stops == 0) : ranBefore;
        setGoing = null;
        stops++;
        stoppedAt = stopped;
        StoppedThread = stopped.ThreadId;
        StopReason = stopped.Reason;
        NoSteps = stopped.NoSteps;
        DataStop = stopped.Reason == StopReasons.DataBreakpoint && stopped.HitBreakpointIds?.Select(id => dataById.GetValueOrDefault(id)).FirstOrDefault(d => d is not null) is { } watched
            ? (watched, stopped.Text)
            : null;
        PropertyStop = stopped.Reason == StopReasons.Breakpoint ? stopped.HitBreakpointIds?.Select(id => propertyById.GetValueOrDefault(id)).FirstOrDefault(p => p is not null) : null;
        BindingErrorStop = stopped.Reason == StopReasons.Breakpoint && bindingErrorsId is int failed && stopped.HitBreakpointIds?.Contains(failed) == true;
        XamlStop = stopped.Reason == StopReasons.Breakpoint && stopped.HitBreakpointIds?.FirstOrDefault(xamlIds.Contains) is int xaml and > 0 ? byId.GetValueOrDefault(xaml) : null;
        if (runToCursor is { } target)
        {
            runToCursor = null;
            IdeErrors.Observe(SendBreakpointsAsync(target.Path));
        }
        SetState(DebugState.Paused);
    }

    /// <summary>Whether a file is XAML a WPF app reads as it runs (its BAML), where no code of the app's stands.</summary>
    private static bool IsRunTimeXaml(string path) => Path.GetExtension(path).Equals(".xaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>The breakpoints in WPF XAML that are on, where the program reads its XAML as it runs; none otherwise or while muted.</summary>
    private List<LineBreakpoint> XamlSending() => !XamlAtRunTime || breakpoints.Muted ? []
        : [.. breakpoints.Files.Where(IsRunTimeXaml).SelectMany(breakpoints.In).Where(b => b.Enabled)];

    /// <summary>A WPF XAML breakpoint's condition as the debugger is given it, on the inspector's call for every element made
    /// from XAML: the element's start tag's file and line as WPF recorded them as the app was built (its built line), the
    /// person's own condition after them.</summary>
    private string XamlCondition(LineBreakpoint breakpoint)
    {
        (int line, int? column) = XamlElementPlace?.Invoke(breakpoint.Path, breakpoint.Line, breakpoint.Attribute, breakpoint.Column ?? breakpoint.AttributeColumn)
            ?? (breakpoint.Line, null);
        string file = breakpoint.Path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"line == {lines.ToBuilt(breakpoint.Path, line) + 1}" + (column is int at ? $" && column == {at}" : string.Empty) + $" && file == \"{file}\""
            + (breakpoint.Condition is { } condition ? $" && ({condition})" : string.Empty);
    }

    /// <summary>Where the element a breakpoint on a XAML line (or on an attribute there) stops as it is made starts
    /// (<see cref="XamlBreakpoints.ElementPlace"/>): its line (from zero), and its name's column (from one) where another
    /// element starts on that line; given as the session starts, from the file's text as the editor has it.</summary>
    public Func<string, int, string?, int?, (int Line, int? Column)?>? XamlElementPlace { get; set; }

    /// <summary>Why a WPF program's XAML breakpoints cannot stop, where its build records no element's place in its XAML
    /// (XamlDebuggingInformation off, as in a Release build); null where they can. Set as the session starts.</summary>
    public string? XamlUnplaced { get; set; }

    private string FunctionSignature()
    {
        List<object?> sending = [.. PropertiesSending().Select(p => Signatures.Of(p.Id, p.Condition, p.HitCondition, p.LogMessage, p.Suspend)),
            .. XamlSending().Select(b => Signatures.Of("xaml", XamlCondition(b), b.HitCondition, b.LogMessage, b.Suspend, b.Key, b.WaitsFor?.Key, b.Temporary, b.Snapshot is null)),
            .. MethodsSending().Select(m => Signatures.Of("method", m.Name, m.Condition, m.HitCondition, m.LogMessage, m.Suspend, m.Key, m.WaitsFor?.Key, m.Temporary, m.Snapshot is null, m.Snapshot?.Captures, m.Snapshot?.Limit))];
        if (BindingErrorsSending() is { } b)
        {
            sending.Add(Signatures.Of("binding", b.Condition, b.HitCondition, b.LogMessage, b.Suspend));
        }
        return Signatures.Of([.. sending]);
    }

    private string ExceptionSignature() => Engine == DebugEngine.GDScript
        ? ScriptErrorsSending() is { } errors ? Signatures.Of("errors", errors.Condition, errors.HitCondition, errors.LogMessage, errors.Suspend) : "none"
        : Signatures.Of([.. ExceptionOptions().Select(s => Signatures.Of(
            s.Breakpoint.Type, Signatures.Of([.. s.Option.Scenarios ?? []]), s.Option.Condition, s.Option.HitCondition, s.Option.LogMessage, s.Option.Suspend))]);

    /// <summary>A snapshot point's capture, told with the point it is of where the session still knows it.</summary>
    private void TakeSnapshot(SnapshotEvent taken) =>
        SnapshotTaken?.Invoke(taken, byId.GetValueOrDefault(taken.BreakpointId) ?? (CodeBreakpoint?)methodById.GetValueOrDefault(taken.BreakpointId));

    private void OnEvent(string name, JsonElementCopy body, long heard)
    {
        switch (name)
        {
            case DapEvents.Initialized:
                initialized.TrySetResult();
                break;
            case DapEvents.Stopped when disconnected is null:
                StoppedEvent stopped = body.Read<StoppedEvent>();
                if (stopped.Reason == StopReasons.Breakpoint && !functionsAnswered.IsCompleted
                    && stopped.HitBreakpointIds?.Any(id => !byId.ContainsKey(id) && !propertyById.ContainsKey(id) && !methodById.ContainsKey(id) && id != bindingErrorsId) == true)
                {
                    IdeErrors.Observe(TakeStopAfter(functionsAnswered, stopped, heard));
                    break;
                }
                TakeStop(stopped, heard);
                break;
            case DapEvents.Breakpoint:
                BreakpointEvent changed = body.Read<BreakpointEvent>();
                if (changed.Reason == EventReasons.Removed)
                {
                    // A temporary breakpoint that took its hit goes, from every session.
                    if (changed.Breakpoint.Id is int gone && (byId.GetValueOrDefault(gone) ?? (CodeBreakpoint?)methodById.GetValueOrDefault(gone)) is { } spent)
                    {
                        breakpoints.Remove(spent);
                    }
                }
                else if (changed.Breakpoint.Id is int id && byId.TryGetValue(id, out LineBreakpoint? breakpoint))
                {
                    breakpoints.Report(this, breakpoint.Path, [Status(breakpoint.Path, changed.Breakpoint)], [breakpoint]);
                }
                else if (changed.Breakpoint.Id is int functionId && propertyById.TryGetValue(functionId, out PropertyBreakpoint? property))
                {
                    (BreakpointStatus status, string? message, _) = Status(changed.Breakpoint);
                    breakpoints.ReportProperty(this, property, status, message);
                }
                else if (changed.Breakpoint.Id is int methodId && methodById.TryGetValue(methodId, out MethodBreakpoint? method))
                {
                    (BreakpointStatus status, string? message, _) = Status(changed.Breakpoint);
                    breakpoints.ReportMethod(this, method, status, message);
                }
                else if (changed.Breakpoint.Id is int bound && bound == bindingErrorsId)
                {
                    ReportBindingErrors(changed.Breakpoint);
                }
                else if (changed.Breakpoint.Id is int dataId && dataById.TryGetValue(dataId, out DataBreakpoint? data))
                {
                    (BreakpointStatus status, string? message, _) = Status(changed.Breakpoint);
                    breakpoints.ReportData(this, data, status, message);
                }
                else if (changed.Breakpoint.Id is int exceptionId && exceptionById.TryGetValue(exceptionId, out ExceptionBreakpoint? exception))
                {
                    (BreakpointStatus status, string? message, _) = Status(changed.Breakpoint);
                    breakpoints.ReportException(this, exception, status, message);
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
            case DapEvents.Module:
                ModuleEvent loaded = body.Read<ModuleEvent>();
                DapModule module = loaded.Module with
                {
                    Path = loaded.Module.Path is { } path ? ToIde(path) : null,
                    SymbolFilePath = loaded.Module.SymbolFilePath is { } symbols && Path.IsPathRooted(symbols) ? ToIde(symbols) : loaded.Module.SymbolFilePath,
                };
                int at = modules.FindIndex(m => m.Id == module.Id);
                if (loaded.Reason == EventReasons.Removed)
                {
                    if (at >= 0)
                    {
                        modules.RemoveAt(at);
                    }
                }
                else if (at >= 0)
                {
                    modules[at] = module;
                }
                else
                {
                    modules.Add(module);
                }
                ModulesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case DapEvents.Continued when State == DebugState.Paused:
                // Gone on from its stop by itself (a Godot game's script, whose debugger did not let it go).
                setGoing = heard;
                SetState(DebugState.Running);
                break;
            case DapEvents.Exited:
                ExitCode = body.Read<ExitedEvent>().ExitCode;
                programEnded = true;
                break;
            case DapEvents.Terminated:
                Failure ??= body.Read<TerminatedEvent>().Failure;
                programEnded = true;
                IdeErrors.Observe(StopAsync());
                break;
        }
    }

    private static (BreakpointStatus, string?, int?) Status(DapBreakpoint breakpoint) =>
        (breakpoint.Verified ? BreakpointStatus.Bound : breakpoint.Reason == BreakpointReasons.Failed ? BreakpointStatus.Invalid : BreakpointStatus.Unknown, breakpoint.Message, breakpoint.Line - 1);

    /// <summary>What the debugger said of a file's breakpoint, the line it bound to (the compiler's, in the build) where that
    /// line's code is now, as the editor counts lines.</summary>
    private (BreakpointStatus, string?, int?) Status(string path, DapBreakpoint breakpoint)
    {
        (BreakpointStatus status, string? message, int? line) = Status(breakpoint);
        // A MAUI XAML breakpoint binds in generated code, whose lines are not the XAML's.
        line = MauiXaml is not null && IsRunTimeXaml(path) ? null : line;
        // A Blazor Web App's client code stops only where its server runs it, which the debugger cannot tell from the browser's.
        if (status != BreakpointStatus.Invalid && Engine == DebugEngine.Managed && BrowserProjectOf(path) is { } client && InBrowser is { } inBrowser)
        {
            message = Run.LaunchPlan.HitOnlyOnServer(client, inBrowser.Program);
        }
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
            Ran = null;
            StoppedThread = null;
            StopReason = null;
            NoSteps = null;
            DataStop = null;
            PropertyStop = null;
            BindingErrorStop = false;
            XamlStop = null;
        }
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends the session, and the conversation with its adapter, once.</summary>
    private void End(int? exitCode)
    {
        Ended(exitCode);
        if (!closed)
        {
            closed = true;
            IdeErrors.Observe(connection.DisposeAsync().AsTask());
        }
    }

    /// <summary>The session has ended as the window shows it, once: its breakpoints' words let go of, its exit code kept.</summary>
    private void Ended(int? exitCode)
    {
        if (State == DebugState.Ended)
        {
            return;
        }
        ExitCode = exitCode ?? ExitCode ?? (Terminal?.Ended is { IsCompletedSuccessfully: true } inTerminal ? inTerminal.Result : null);
        IdeLog.Write(IdeLog.Debugger, $"[{connection.Name}] Ended debugging {Program}{(ExitCode is { } code ? $": {ExitCodes.Said(code)}" : string.Empty)}{(Failure is { } failed ? $": {failed}" : string.Empty)}");
        breakpoints.Changed -= OnBreakpointsChanged;
        breakpoints.ExceptionsChanged -= OnExceptionsChanged;
        breakpoints.DataChanged -= OnDataChanged;
        breakpoints.PropertiesChanged -= OnFunctionsChanged;
        breakpoints.MethodsChanged -= OnFunctionsChanged;
        breakpoints.BindingErrorsChanged -= OnFunctionsChanged;
        // What its debugger said goes, and its data breakpoints, each on a member of one of its objects.
        if (!Quiet)
        {
            breakpoints.Leave(this);
        }
        SetState(DebugState.Ended);
    }

    /// <summary>An event's body, copied off the connection's buffer for reading on the UI thread.</summary>
    private readonly struct JsonElementCopy(System.Text.Json.JsonElement element)
    {
        private readonly System.Text.Json.JsonElement element = element.ValueKind == System.Text.Json.JsonValueKind.Undefined ? element : element.Clone();

        public T Read<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(
            element.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : element.GetRawText(), DapConnection.Options)!;
    }
}
