using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.Tests.Debugging.Helpers;

/// <summary>The debug adapter run as the IDE runs it, in its own process from the app's debugger/
/// folder, and the requests the tests make of it, each answered before the next is sent. Stops and
/// breakpoint changes queue in the order they came, for the tests to wait on one at a time.</summary>
public sealed class DebugSession : IDisposable
{
    /// <summary>The debug adapter's protocol tests run one at a time, as their upstream's did: each debuggee waits on
    /// timings (a suspended start, a breakpoint's binding) that a machine busy with others stretches. Beside the window
    /// tests, one at a time themselves, their adapter and small debuggee are what the run can bear; a protocol test
    /// that holds the engine to a time runs alone (<see cref="Alone"/>).</summary>
    public const string Collection = "Debugger";

    private static readonly TimeSpan EventLimit = TimeSpan.FromSeconds(60);

    private readonly Process adapter;
    private readonly Process? debuggee;
    private readonly bool terminateDebuggee;
    private readonly Channel<(string Name, JsonElement Body)> events = Channel.CreateUnbounded<(string, JsonElement)>();
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The adapter's latest events and standard error, which a wait that fails quotes.</summary>
    private readonly Queue<string> heard = new();
    private readonly string engineLog;
    private readonly long engineLogStart;
    private readonly bool ownLog;

    private DebugSession(ITestOutputHelper output, Process? debuggee, bool terminateDebuggee, string? symbolCache = null)
    {
        this.debuggee = debuggee;
        this.terminateDebuggee = terminateDebuggee;
        string path = Path.Combine(AppContext.BaseDirectory, "debugger", "Passenger.DebugAdapter.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The debug adapter is not beside the tests", path);
        }
        // The engine's own log: PASSENGER_ENGINE_LOG=FILE keeps every session's, else the session keeps one for a failed
        // wait to quote, which on a machine no one can debug on (CI's) is all there is to go by.
        string? kept = Environment.GetEnvironmentVariable("PASSENGER_ENGINE_LOG") is { Length: > 0 } named ? named : null;
        ownLog = kept is null;
        engineLog = kept ?? Path.Combine(TestRun.Root, $"engine-{Guid.NewGuid():N}.log");
        engineLogStart = File.Exists(engineLog) ? new FileInfo(engineLog).Length : 0;
        // The symbols it decompiles go where the run's IDE keeps them (its state folder's), unless a test names a folder.
        string symbols = symbolCache ?? Passenger.App.Debugging.DebugSession.SymbolCache;
        List<string> arguments = [path, "--interpreter=vscode", $"--engineLogging={engineLog}", $"--symbolCache={symbols}"];
        // Started as the IDE starts it, so a test run that is killed leaves no adapter or program behind.
        ProcessStartInfo start = ChildProcesses.DotnetCommand(arguments);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        adapter = ChildProcesses.Start(start);
        adapter.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Log(output, $"adapter: {e.Data}");
                Hear($"standard error: {e.Data}");
            }
        };
        adapter.BeginErrorReadLine();
        Connection = new DapConnection(adapter.StandardOutput.BaseStream, adapter.StandardInput.BaseStream, message => Log(output, message));
        Connection.EventReceived += (name, body) =>
        {
            if (name is not ("module" or "thread"))
            {
                Hear(body.ValueKind != JsonValueKind.Object ? name
                    : name == "output" && body.TryGetProperty("output", out JsonElement text) ? $"output: {text.GetString()?.TrimEnd()}"
                    : $"{name}: {body.GetRawText()}");
            }
            if (name == "initialized")
            {
                initialized.TrySetResult();
            }
            else if (name is "stopped" or "breakpoint" or "output" or "exited" or "terminated")
            {
                events.Writer.TryWrite((name, body.Clone()));
            }
        };
        Connection.Start();
    }

    public DapConnection Connection { get; }

    /// <summary>The engine's log read with sharing, while the adapter is still writing it.</summary>
    public string EngineLog
    {
        get
        {
            try
            {
                using FileStream file = new(engineLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file.Seek(engineLogStart, SeekOrigin.Begin);
                using StreamReader reader = new(file);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>The adapter with DebuggableConsoleApp started for it to attach to; suspended, the
    /// program waits at startup until the attach lets it go, in configurationDone.</summary>
    public static DebugSession ForAttach(ITestOutputHelper output, bool startSuspended, out Process debuggee, string? symbolCache = null)
    {
        debuggee = StartDebuggee(startSuspended);
        return new DebugSession(output, debuggee, terminateDebuggee: false, symbolCache);
    }

    /// <summary>The adapter, to attach to a program the test started; the program ends with the session.</summary>
    public static DebugSession ForAttach(ITestOutputHelper output, Process program) => new(output, program, terminateDebuggee: false);

    /// <summary>The adapter alone, to launch a program itself; the program ends with the session. Decompiled symbols go to
    /// <paramref name="symbolCache"/> when given.</summary>
    public static DebugSession ForLaunch(ITestOutputHelper output, string? symbolCache = null) => new(output, null, terminateDebuggee: true, symbolCache);

    /// <summary>Kills the adapter and the program it launched, as a crash or the IDE's own kill of an adapter that never
    /// answered a disconnect does.</summary>
    public void Kill()
    {
        ChildProcesses.Kill(adapter);
        if (!adapter.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException($"The adapter (process {adapter.Id}) was still running 10 s after it was killed");
        }
    }

    /// <summary>A debuggee launched with a breakpoint on one line of its Program.cs and the exception
    /// filters given, running.</summary>
    public static async Task<DebugSession> LaunchToBreakpoint(ITestOutputHelper output, string debuggee, int line, IReadOnlyList<string> exceptionFilters)
    {
        DebugSession host = ForLaunch(output);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program(debuggee), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        host.Request("setExceptionBreakpoints", new SetExceptionBreakpointsArguments(exceptionFilters));
        host.WithBreakpointsRequest(line, Debuggees.Source(debuggee, "Program.cs")).WithConfigurationDoneRequest();
        return host;
    }

    /// <summary>The top frame's variables at a stop, by name; a failure quotes the engine's log, which says what the program
    /// was doing meanwhile.</summary>
    public List<DapVariable> Locals(StoppedEvent stop)
    {
        try
        {
            DapStackFrame top = GetTopStackFrame(stop.ThreadId!.Value);
            WithScopesRequest(top.Id, out ScopesResponse scopes);
            WithVariablesRequest(scopes.Scopes.Single().VariablesReference, out List<DapVariable> variables);
            return variables;
        }
        catch (DapException failed)
        {
            throw new DapException(failed.Command, $"{failed.Reason}\n{Heard()}");
        }
    }

    /// <summary>A request's answer, waited for no longer than <see cref="RequestLimit"/> (or the limit given): one the
    /// adapter never answers fails the test naming the command, with what the adapter said last and where it waits.</summary>
    public T Request<T>(string command, object? arguments = null, TimeSpan? limit = null) =>
        Answer(command, Connection.RequestAsync<T>(command, arguments ?? new DapEmpty(), TestContext.Current.CancellationToken), limit);

    public void Request(string command, object? arguments = null, TimeSpan? limit = null) =>
        Answer(command, Answered(Connection.RequestAsync(command, arguments ?? new DapEmpty(), TestContext.Current.CancellationToken)), limit);

    /// <summary>A request sent without waiting for its answer, for the test to await when it wants it; one it never awaits
    /// (the adapter gone first) fails unseen by the window tests beside.</summary>
    public Task<T> Send<T>(string command, object? arguments = null)
    {
        Task<T> answer = Connection.RequestAsync<T>(command, arguments ?? new DapEmpty(), TestContext.Current.CancellationToken);
        Observe(answer);
        return answer;
    }

    /// <summary>A disconnect, answered or not within <paramref name="limit"/>: true when it was, the adapter then on its way out.</summary>
    public bool Disconnect(TimeSpan limit)
    {
        Task disconnect = Connection.RequestAsync("disconnect", new DisconnectArguments(TerminateDebuggee: terminateDebuggee));
        Observe(disconnect);
        return disconnect.Wait(limit, TestContext.Current.CancellationToken);
    }

    private static async Task<bool> Answered(Task request)
    {
        await request;
        return true;
    }

    /// <summary>The longest a request may go unanswered: launch and configurationDone start the program, and an evaluation
    /// may run a method of it.</summary>
    public static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(60);

    private T Answer<T>(string command, Task<T> answer, TimeSpan? limit)
    {
        TimeSpan most = limit ?? RequestLimit;
        Observe(answer);
        try
        {
            return answer.WaitAsync(most, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The adapter left {command} unanswered for {most.TotalSeconds:0} s. {Heard()}");
        }
    }

    public DebugSession WithInitializeRequest()
    {
        Request<DapCapabilities>("initialize", InitializeArguments);
        return this;
    }

    public static InitializeArguments InitializeArguments => new()
    {
        ClientId = "passenger",
        ClientName = "Passenger",
        AdapterId = "coreclr",
        Locale = "en-us",
        LinesStartAt1 = true,
        ColumnsStartAt1 = true,
        PathFormat = "path",
        SupportsVariableType = true,
        SupportsVariablePaging = true,
        SupportsRunInTerminalRequest = true,
    };

    public DebugSession WithAttachRequest(int processId, bool justMyCode = true)
    {
        Request("attach", new AttachArguments { ProcessId = processId, JustMyCode = justMyCode });
        return this;
    }

    public DebugSession WithLaunchRequest(string program, bool stopAtEntry, bool justMyCode, IReadOnlyList<string>? arguments = null, string console = "internalConsole")
    {
        Request("launch", new LaunchArguments
        {
            Program = program,
            Args = arguments,
            Cwd = Path.GetDirectoryName(program),
            Console = console,
            JustMyCode = justMyCode,
            StopAtEntry = stopAtEntry,
        });
        return this;
    }

    public async Task<DebugSession> WaitForInitializedEvent()
    {
        await initialized.Task.WaitAsync(EventLimit, TestContext.Current.CancellationToken);
        return this;
    }

    /// <summary>A breakpoint in DebuggableConsoleApp's MyClass.cs, at line 22 unless another is given.</summary>
    public DebugSession WithBreakpointsRequest(int line = 22) => WithBreakpointsRequest(line, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"));

    public DebugSession WithBreakpointsRequest(int line, string filePath) => WithBreakpointsRequest([line], filePath);

    public DebugSession WithBreakpointsRequest(int[] lines, string filePath) =>
        WithBreakpointsRequest(filePath, [.. lines.Select(line => new DapSourceBreakpoint(line))], out _);

    public DebugSession WithBreakpointsRequest(string filePath, List<DapSourceBreakpoint> breakpoints) => WithBreakpointsRequest(filePath, breakpoints, out _);

    public DebugSession WithBreakpointsRequest(string filePath, List<DapSourceBreakpoint> breakpoints, out SetBreakpointsResponse response)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Source file for breakpoint not found", filePath);
        }
        response = Request<SetBreakpointsResponse>("setBreakpoints", new SetBreakpointsArguments(new DapSource { Path = filePath }, breakpoints));
        Assert.Equal(breakpoints.Count, response.Breakpoints.Count);
        return this;
    }

    public DebugSession WithClearBreakpointsRequest(string filePath) => WithBreakpointsRequest(filePath, [], out _);

    /// <summary>configurationDone, which launches or attaches; a failure quotes the engine's log, which says what failed where
    /// (on CI, the only word of it).</summary>
    public DebugSession WithConfigurationDoneRequest()
    {
        try
        {
            Request("configurationDone");
        }
        catch (DapException failed)
        {
            throw new DapException(failed.Command, $"{failed.Reason}\n{Heard()}");
        }
        return this;
    }

    /// <summary>What the program wrote to its standard output and how it ended (no exit code when none was told), read
    /// from the events to the session's end.</summary>
    public async Task<(string Output, int? ExitCode)> WaitForEnd()
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(EventLimit);
        StringBuilder output = new();
        int? exitCode = null;
        while (true)
        {
            (string Name, JsonElement Body) next;
            try
            {
                next = await events.Reader.ReadAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The session had not ended after {EventLimit.TotalSeconds:0} s. {Heard()}");
            }
            if (next.Name == "terminated")
            {
                return (output.ToString(), exitCode);
            }
            if (next.Name == "exited")
            {
                exitCode = next.Body.Deserialize<ExitedEvent>(DapConnection.Options)!.ExitCode;
            }
            else if (next.Name == "output" && next.Body.Deserialize<OutputEvent>(DapConnection.Options) is { Category: "stdout" } written)
            {
                output.Append(written.Output);
            }
        }
    }

    /// <summary>Lets a program started suspended go on, as a client that held it does once configurationDone has answered.
    /// The attach has let it go already, having taken hold of it first, so a breakpoint set before sees its every pass.</summary>
    public DebugSession WithOptionalResumeRuntime(int processId, bool startSuspended)
    {
        if (!startSuspended)
        {
            return this;
        }
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                new DiagnosticsClient(processId).ResumeRuntime();
                return this;
            }
            catch (ServerNotAvailableException) when (DateTime.UtcNow < limit)
            {
                Thread.Sleep(50);
            }
            catch (ServerNotAvailableException never)
            {
                throw new TimeoutException($"The program started suspended (process {processId}) opened no diagnostic port in 10 s, so it could not be let go. {Heard()}", never);
            }
        }
    }

    public DapStackFrame GetTopStackFrame(int threadId) =>
        Assert.Single(Request<StackTraceResponse>("stackTrace", new StackTraceArguments(threadId, 0, 1)).StackFrames);

    public DebugSession WithStackTraceRequest(int threadId, out StackTraceResponse response, int? levels = 1)
    {
        response = Request<StackTraceResponse>("stackTrace", new StackTraceArguments(threadId, 0, levels));
        return this;
    }

    public DebugSession WithScopesRequest(int frameId, out ScopesResponse response)
    {
        response = Request<ScopesResponse>("scopes", new ScopesArguments(frameId));
        return this;
    }

    public DebugSession WithVariablesRequest(int variablesReference, out List<DapVariable> variables)
    {
        variables = [.. Request<VariablesResponse>("variables", new VariablesArguments(variablesReference)).Variables];
        return this;
    }

    public DebugSession WithEvaluateRequest(int frameId, string expression, out EvaluateResponse response)
    {
        response = Request<EvaluateResponse>("evaluate", new EvaluateArguments(expression, frameId, "repl"));
        return this;
    }

    public DebugSession WithContinueRequest()
    {
        Request<ContinueResponse>("continue", new ThreadArguments(0));
        return this;
    }

    public DebugSession WithStepInRequest(int threadId) => Step("stepIn", threadId);

    public DebugSession WithStepOutRequest(int threadId) => Step("stepOut", threadId);

    public DebugSession WithStepOverRequest(int threadId) => Step("next", threadId);

    /// <summary>The next stop, with where it is: a stop at a breakpoint or a step says so itself, any
    /// other (an exception, a pause) is wherever its thread's top frame is, if that has source.</summary>
    public async Task<StoppedEvent> WaitForStoppedEvent()
    {
        StoppedEvent stopped = await WaitForEvent<StoppedEvent>();
        if (stopped.Source is null && stopped.ThreadId is int threadId && GetTopStackFrame(threadId) is { Source: { } source } top)
        {
            stopped = stopped with { Source = new DapSource { Path = source.Path }, Line = top.Line, Column = top.Column };
        }
        return stopped;
    }

    /// <summary>The next stop or breakpoint change of type <typeparamref name="T"/> that meets the
    /// condition; the other events before it are passed over.</summary>
    public async Task<T> WaitForEvent<T>(Predicate<T>? condition = null)
    {
        string name = typeof(T) == typeof(StoppedEvent) ? "stopped"
            : typeof(T) == typeof(BreakpointEvent) ? "breakpoint"
            : typeof(T) == typeof(OutputEvent) ? "output"
            : throw new ArgumentException($"Only stops, breakpoint changes and output are queued, not {typeof(T).Name}");
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(EventLimit);
        while (true)
        {
            (string Name, JsonElement Body) next;
            try
            {
                next = await events.Reader.ReadAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No {name} event came in {EventLimit.TotalSeconds:0} s. {Heard()}");
            }
            if (next.Name == "terminated")
            {
                throw new InvalidOperationException($"The session ended while a {name} event was awaited. {Heard()}");
            }
            if (next.Name != name)
            {
                continue;
            }
            T body = next.Body.Deserialize<T>(DapConnection.Options)!;
            if (condition is null || condition(body))
            {
                return body;
            }
        }
    }

    /// <summary>A request given up on (its wait ran out, or the session ended first) fails when the adapter goes: seen here,
    /// that failure never reaches the IDE's catcher of tasks nobody awaited, which would fail the window test beside.</summary>
    private static void Observe(Task request) => request.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        try
        {
            Task disconnect = Connection.RequestAsync("disconnect", new DisconnectArguments(TerminateDebuggee: terminateDebuggee));
            Observe(disconnect);
            disconnect.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // An adapter that is gone already has nothing to disconnect.
        }
        if (!adapter.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            ChildProcesses.Kill(adapter);
        }
        adapter.Dispose();
        if (debuggee is not null)
        {
            if (!debuggee.HasExited)
            {
                ChildProcesses.Kill(debuggee);
            }
            debuggee.Dispose();
        }
        Connection.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        if (ownLog)
        {
            try
            {
                File.Delete(engineLog);
            }
            catch (IOException)
            {
                // Left in the temporary folder.
            }
        }
    }

    private void Hear(string said)
    {
        lock (heard)
        {
            heard.Enqueue(said.Length > 400 ? said[..400] + "…" : said);
            while (heard.Count > 30)
            {
                heard.Dequeue();
            }
        }
    }

    /// <summary>What the adapter said last and how the engine's log ends: the cause of a wait that failed.</summary>
    private string Heard()
    {
        string said;
        lock (heard)
        {
            said = string.Join("\n  ", heard);
        }
        IEnumerable<string> logged;
        try
        {
            using FileStream file = new(engineLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(file);
            logged = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(40);
        }
        catch (IOException error)
        {
            logged = [$"(not readable: {error.Message})"];
        }
        return $"What the adapter said last:\n  {said}\nThe engine's log ends:\n  {string.Join("\n  ", logged)}{Stacks()}";
    }

    /// <summary>On macOS, where the adapter's debugger and the debuggee's runtime were waiting: the engine's log says which
    /// step never ended, this says in which call.</summary>
    private string Stacks()
    {
        string stacks = adapter.HasExited ? string.Empty : NativeStacks.Describe("Where the adapter's debugger was", adapter.Id, "libmscordbi", "libdbgshim");
        if (debuggee is { HasExited: false })
        {
            stacks += NativeStacks.Describe("Where the debuggee's runtime was", debuggee.Id, "Debugger", "DbgTransport", "TwoWayPipe");
        }
        return stacks;
    }

    private DebugSession Step(string command, int threadId)
    {
        Request(command, new ThreadArguments(threadId));
        return this;
    }

    private static Process StartDebuggee(bool startSuspended)
    {
        string path = Debuggees.Apphost("DebuggableConsoleApp");
        ProcessStartInfo start = ChildProcesses.Command(path, []);
        if (startSuspended)
        {
            start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        }
        // A test runner under a debugger hands these to the tests' children, and they change what the
        // debuggee's stacks look like (ReadyToRun code, for one).
        foreach (string variable in (string[])["COMPLUS_FORCEENC", "COMPLUS_ReadyToRun", "COMPLUS_ZapDisable", "DOTNET_GCConserveMemory", "DOTNET_GCHeapCount", "DOTNET_GCNoAffinitize", "DOTNET_MODIFIABLE_ASSEMBLIES", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_TieredPGO", "DOTNET_gcServer", "_NO_DEBUG_HEAP"])
        {
            start.Environment.Remove(variable);
        }
        return ChildProcesses.Start(start);
    }

    private static void Log(ITestOutputHelper output, string message)
    {
        try
        {
            output.WriteLine(message);
        }
        catch (InvalidOperationException)
        {
            // The test has ended; what the adapter says after it goes nowhere.
        }
    }
}

public static class StoppedEventExtensions
{
    /// <summary>The frames without what says whether each has source yet, for comparing with frames
    /// written out by hand.</summary>
    public static List<DapStackFrame> WithoutResolution(this IEnumerable<DapStackFrame> frames) =>
        [.. frames.Select(frame => frame with { IsResolved = null, DecompiledSourceInfo = null })];

    /// <summary>Where a stop is, which this adapter puts in the event itself.</summary>
    public static (string filePath, int line, int column) ReadStopInfo(this StoppedEvent stopped) =>
        (stopped.Source?.Path ?? throw new InvalidOperationException("The stop has no source"), stopped.Line ?? 0, stopped.Column ?? 0);
}

/// <summary>The programs the tests debug: their sources under tests/Debuggees, their builds under
/// artifacts/bin, where their own Directory.Build.props puts them.</summary>
public static class Debuggees
{
    private static readonly Lazy<string> Repository = new(() =>
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Passenger.slnx")))
            {
                return folder.FullName;
            }
        }
        throw new DirectoryNotFoundException($"No Passenger.slnx above {AppContext.BaseDirectory}");
    });

    /// <summary>The repository the tests were built from, its Passenger.slnx at the top.</summary>
    public static string Root => Repository.Value;

    public static string Source(params string[] path) => Path.Combine([Repository.Value, "tests", "Debuggees", .. path]);

    public static string Program(string name) => Path.Combine(Repository.Value, "artifacts", "bin", name, "debug", $"{name}.dll");

    /// <summary>The line (from one) of a debuggee's Program.cs that holds some code, so a test names
    /// what it stops at rather than a number that moves when a case is added above it.</summary>
    public static int Line(string name, string code)
    {
        string[] lines = File.ReadAllLines(Source(name, "Program.cs"));
        int index = Array.FindIndex(lines, l => l.Contains(code, StringComparison.Ordinal));
        return index >= 0 ? index + 1 : throw new InvalidOperationException($"No line of {name}'s Program.cs holds '{code}'");
    }

    public static string Apphost(string name) => Path.ChangeExtension(Program(name), OperatingSystem.IsWindows() ? ".exe" : null);

    /// <summary>A debuggee as a project of a test's own folder, its project file and Program.cs copied there, for the window
    /// to build, run and debug; the project file's path. The caller restores it.</summary>
    public static string CopyTo(string name, string folder)
    {
        string project = Path.Combine(folder, $"{name}.csproj");
        File.Copy(Source(name, $"{name}.csproj"), project, overwrite: true);
        File.Copy(Source(name, "Program.cs"), Path.Combine(folder, "Program.cs"), overwrite: true);
        return project;
    }

    /// <summary>A program with no .NET in it, running a minute unless ended first: what an attach cannot take hold of.</summary>
    public static Process NotDotnet()
    {
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? ChildProcesses.Command("ping", ["-n", "60", "127.0.0.1"])
            : ChildProcesses.Command("/bin/sleep", ["60"]);
        start.RedirectStandardOutput = true;
        return ChildProcesses.Start(start);
    }
}
