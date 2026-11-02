using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>A Blazor app's WebAssembly half's debugger: a debug adapter inside the IDE speaking Passenger's protocol to its
/// session (over an <see cref="InProcessEnd"/>) and the Chrome DevTools Protocol to the app's page in a Chromium browser,
/// through .NET's browser debug proxy (<see cref="BrowserRun"/>), so the Debug window, Variables, the gutter and agents'
/// debug tools show a stop in the browser as they show any. Breakpoints go by file and line, their conditions to the proxy,
/// their hit counts and messages read here at each hit. Beside a Blazor Web App's server it attaches once the server
/// listens; for a standalone app it launches the development server itself, its output the session's. Requests and the
/// page's events are taken one at a time.</summary>
public sealed class WebAssemblyAdapter
{
    /// <summary>The one thread the page runs .NET on, as the Debug window lists it.</summary>
    private const int PageThread = 1;

    /// <summary>Why no value is set in the browser.</summary>
    public const string NoSet = ".NET's browser debug proxy sets no values";

    /// <summary>What a pause says that stopped the page in the browser's own code, between its .NET code's turns.</summary>
    public const string OutsideDotnet = "The page paused in the browser's own code, outside its .NET code: Continue lets it go on";

    /// <summary>How long the app has to listen at its page's address before the browser is given up.</summary>
    private static readonly TimeSpan ListenLimit = TimeSpan.FromMinutes(3);

    private readonly BrowserDebugging debugging;
    private readonly string profiles;
    private readonly string name;
    private readonly InProcessEnd end;
    private readonly DapConnection connection;

    /// <summary>What ends the program a Stop of this session ends: the server's session beside it; null where this adapter
    /// launched the development server, which it ends itself.</summary>
    private readonly Func<Task>? terminate;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource ending = new();
    private BrowserRun? browser;
    private Process? server;

    /// <summary>The breakpoints the page has, by file, each as asked and with its hits so far.</summary>
    private readonly Dictionary<string, List<Placed>> placed = new(PathNames.Comparer);
    private int lastId;

    private JsonElement? stop;
    private IReadOnlyList<WebAssemblyFrame> frames = [];
    private int generation;
    private readonly Dictionary<int, Func<Task<IReadOnlyList<DapVariable>>>> inside = [];
    private int lastReference;
    private Going going;

    private enum Going
    {
        None,
        Stepping,
        Pausing,
        Resuming,
    }

    /// <summary>A breakpoint as the page has it: the protocol's id and the proxy's, what was asked, and its hits.</summary>
    private sealed record Placed(int Id, string BrowserId, int Line, int? Column, string? Condition, HitCondition? Hits, string? Log, bool Suspend)
    {
        public int Count { get; set; }

        /// <summary>The line the page bound it to (from one), once it has loaded its code; null before.</summary>
        public int? Bound { get; set; }
    }

    /// <summary>The adapter of a WebAssembly app's page over <paramref name="end"/>, its browser's profile kept under
    /// <paramref name="profiles"/>; <paramref name="terminate"/> ends the app's server, as a Stop of this session asks, null
    /// where the session launches the development server itself.</summary>
    public WebAssemblyAdapter(BrowserDebugging debugging, string profiles, string name, InProcessEnd end, Func<Task>? terminate)
    {
        this.debugging = debugging;
        this.profiles = profiles;
        this.name = name;
        this.end = end;
        this.terminate = terminate;
        connection = new DapConnection(end.AdapterInput, end.AdapterOutput, line => IdeLog.Write(IdeLog.Debugger, $"[{end.Name}] {line}")) { Name = end.Name };
        Register();
    }

    /// <summary>Whether a file's breakpoints go to a browser session of these projects: one in their own folders.</summary>
    public static bool Takes(IReadOnlyList<string> projects, string path) => projects.Any(p => ProjectFolders.Holds(Path.GetDirectoryName(p)!, path));

    /// <summary>Starts answering its session.</summary>
    public void Start() => connection.Start();

    private static DapCapabilities Capabilities => new()
    {
        SupportsConfigurationDoneRequest = true,
        SupportsConditionalBreakpoints = true,
        SupportsHitConditionalBreakpoints = true,
        SupportsLogPoints = true,
        SupportsEvaluateForHovers = true,
        SupportsExceptionInfoRequest = true,
        SupportsExceptionOptions = true,
        SupportsDataBreakpoints = false,
        DataBreakpointsUnavailable = ".NET's browser debug proxy watches no data",
    };

    private void Register()
    {
        connection.Handle<InitializeArguments, DapCapabilities>(DapCommands.Initialize, _ => Task.FromResult(Capabilities), then: () => Tell(DapEvents.Initialized, null));
        connection.Handle<AttachArguments>(DapCommands.Attach, _ => Held(() => OpenAsync(DapCommands.Attach)));
        connection.Handle<LaunchArguments>(DapCommands.Launch, arguments => Held(() => LaunchAsync(arguments)));
        connection.Handle<DapEmpty>(DapCommands.ConfigurationDone, _ => Held(ConfiguredAsync));
        connection.Handle<SetBreakpointsArguments, SetBreakpointsResponse>(DapCommands.SetBreakpoints, arguments => Held(() => SetBreakpointsAsync(arguments)));
        connection.Handle<SetExceptionBreakpointsArguments, SetExceptionBreakpointsResponse>(DapCommands.SetExceptionBreakpoints,
            arguments => Held(() => SetExceptionsAsync(arguments)));
        connection.Handle<DapEmpty, ThreadsResponse>(DapCommands.Threads, _ => Task.FromResult(new ThreadsResponse([new DapThread(PageThread, "Browser") { IsMain = true }])));
        connection.Handle<StackTraceArguments, StackTraceResponse>(DapCommands.StackTrace, arguments => Held(() => Task.FromResult(StackTrace(arguments))));
        connection.Handle<ScopesArguments, ScopesResponse>(DapCommands.Scopes, arguments => Held(() => Task.FromResult(Scopes(arguments.FrameId))));
        connection.Handle<VariablesArguments, VariablesResponse>(DapCommands.Variables, arguments => Held(() => VariablesAsync(arguments.VariablesReference)));
        connection.Handle<EvaluateArguments, EvaluateResponse>(DapCommands.Evaluate, arguments => Held(() => EvaluateAsync(arguments)));
        connection.Handle<SetExpressionArguments, SetExpressionResponse>(DapCommands.SetExpression,
            _ => Task.FromException<SetExpressionResponse>(new DapException(DapCommands.SetExpression, NoSet)));
        connection.Handle<ThreadArguments, ExceptionInfoResponse>(DapCommands.ExceptionInfo, _ => Held(() => Task.FromResult(ExceptionInfo())));
        connection.Handle<ThreadArguments>(DapCommands.Continue, _ => Held(() => ResumeAsync(CdpCommands.Resume, Going.Resuming)));
        connection.Handle<ThreadArguments>(DapCommands.Next, _ => Held(() => ResumeAsync(CdpCommands.StepOver, Going.Stepping)));
        connection.Handle<StepInArguments>(DapCommands.StepIn, _ => Held(() => ResumeAsync(CdpCommands.StepInto, Going.Stepping)));
        connection.Handle<ThreadArguments>(DapCommands.StepOut, _ => Held(() => ResumeAsync(CdpCommands.StepOut, Going.Stepping)));
        connection.Handle<ThreadArguments>(DapCommands.Pause, _ => Held(PauseAsync));
        connection.Handle<InputArguments>(DapCommands.Input, arguments => InputAsync(arguments));
        connection.Handle<DisconnectArguments>(DapCommands.Disconnect, arguments => DisconnectAsync(arguments), then: Ended, evenIfFailed: true);
    }

    /// <summary>A request or an event taken in turn with the others.</summary>
    private async Task<T> Held<T>(Func<Task<T>> work)
    {
        await gate.WaitAsync();
        try
        {
            return await work();
        }
        catch (CdpException refused)
        {
            throw new DapException(refused.Method, refused.Reason);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Launches the standalone app's development server as the plan says, its output the session's as it comes and
    /// its end the session's, then opens its page in the browser.</summary>
    private async Task<bool> LaunchAsync(LaunchArguments arguments)
    {
        string program = arguments.Program ?? throw new DapException(DapCommands.Launch, "No program to launch");
        ProcessStartInfo start = ChildProcesses.Command(program, arguments.Args ?? [], arguments.Cwd);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        foreach ((string variable, string value) in arguments.Env ?? new Dictionary<string, string>())
        {
            start.Environment[variable] = value;
        }
        Tell(DapEvents.Output, new OutputEvent($"{ChildProcesses.Shown(start)}\n", DapOutputCategories.Console));
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new DapException(DapCommands.Launch, $"Could not start {Path.GetFileName(start.FileName)}: {error.Message}");
        }
        server = process;
        Tell(DapEvents.Process, new ProcessEvent(name, process.Id, IsLocalProcess: true, StartMethod: DapCommands.Launch));
        // For the server's life: no wait for the IDE to be quiet waits out a server that runs until it is stopped.
        Task heard = ChildProcesses.Hear(process, line => Tell(DapEvents.Output, new OutputEvent($"{line}\n", DapOutputCategories.Stdout)),
            line => Tell(DapEvents.Output, new OutputEvent($"{line}\n", DapOutputCategories.Stderr)));
        IdeErrors.Observe(BackgroundWork.Unheld(() => ServerEndedAsync(process, heard)));
        return await OpenAsync(DapCommands.Launch);
    }

    /// <summary>The development server launched has ended, what it wrote told: its exit code, then the session's end.</summary>
    private async Task ServerEndedAsync(Process process, Task heard)
    {
        await process.WaitForExitAsync();
        await Task.WhenAny(heard, Task.Delay(TimeSpan.FromSeconds(2)));
        Tell(DapEvents.Exited, new ExitedEvent(process.ExitCode));
        Tell(DapEvents.Terminated, new TerminatedEvent());
    }

    /// <summary>Once the app listens at its page's address, starts the browser and the proxy and takes the page's debugging,
    /// its stops coming here from then on; the page opens once the session's breakpoints are in.</summary>
    private async Task<bool> OpenAsync(string command)
    {
        Say($"Debugging {Plural.Listed([.. debugging.Projects.Select(p => Path.GetFileNameWithoutExtension(p))])} in the browser at {debugging.Page} once it listens. {BrowserDebugging.Limits}.");
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ending.Token);
        waiting.CancelAfter(Waits.Of(ListenLimit));
        if (await Run.BrowserLaunch.ListeningAsync(debugging.Page, waiting.Token) != true)
        {
            throw new DapException(command, ending.IsCancellationRequested ? DebugSession.StoppedStarting
                : $"Nothing listened at {debugging.Page} within {Waits.Of(ListenLimit).TotalMinutes:0} minutes, so there was no page to debug in the browser");
        }
        BrowserRun run;
        try
        {
            run = await BrowserRun.StartAsync(debugging, profiles, Say, ending.Token);
        }
        catch (InvalidOperationException failed)
        {
            throw new DapException(command, failed.Message);
        }
        browser = run;
        run.Page.Events = HeardAsync;
        IdeErrors.Observe(BackgroundWork.Unheld(() => BrowserEndedAsync(run)));
        // First: the browser runs the proxy's script for each new document, which readies .NET's debugging, only once Page is on.
        await run.Page.SendAsync(CdpCommands.PageEnable);
        await run.Page.SendAsync(CdpCommands.RuntimeEnable);
        await run.Page.SendAsync(CdpCommands.DebuggerEnable);
        if (server is null)
        {
            Tell(DapEvents.Process, new ProcessEvent($"{debugging.Browser.Name} at {debugging.Page}", run.BrowserId, IsLocalProcess: true, StartMethod: DapCommands.Attach));
        }
        return true;
    }

    /// <summary>The browser was closed, or its page's conversation ended: the session ends, the development server with it.</summary>
    private async Task BrowserEndedAsync(BrowserRun run)
    {
        await run.Ended;
        if (end.HasEnded || ending.IsCancellationRequested)
        {
            return;
        }
        Say($"{debugging.Browser.Name} closed: debugging in the browser has ended");
        if (server is { } launched)
        {
            ChildProcesses.Kill(launched);
            return;
        }
        Tell(DapEvents.Terminated, new TerminatedEvent());
    }

    /// <summary>The session's breakpoints are in: the page opens at the app's address, its .NET code loading with them set.</summary>
    private async Task<bool> ConfiguredAsync()
    {
        if (browser is { } run)
        {
            await run.Page.SendAsync(CdpCommands.PageNavigate, new { url = debugging.Page.ToString() });
        }
        return true;
    }

    private async Task<SetBreakpointsResponse> SetBreakpointsAsync(SetBreakpointsArguments arguments)
    {
        IReadOnlyList<DapSourceBreakpoint> asked = arguments.Breakpoints ?? [];
        if (arguments.Source.Path is not { } path || browser is not { } run)
        {
            return new SetBreakpointsResponse([.. asked.Select(_ => new DapBreakpoint { Verified = false, Reason = "failed", Message = "The browser is not debugged" })]);
        }
        List<Placed> had = [.. placed.GetValueOrDefault(path) ?? []];
        List<Placed> now = [];
        List<DapBreakpoint> said = [];
        foreach (DapSourceBreakpoint breakpoint in asked)
        {
            HitCondition? hits = null;
            if (breakpoint.HitCondition is { } hitText)
            {
                if (!HitCondition.TryParse(hitText, out HitCondition parsed, out string? problem))
                {
                    said.Add(new DapBreakpoint { Verified = false, Reason = "failed", Message = problem, Line = breakpoint.Line });
                    continue;
                }
                hits = parsed;
            }
            bool suspend = breakpoint.Suspend ?? breakpoint.LogMessage is null;
            if (had.FirstOrDefault(p => p.Line == breakpoint.Line && p.Column == breakpoint.Column && p.Condition == breakpoint.Condition) is { } kept)
            {
                had.Remove(kept);
                Placed again = kept with { Hits = hits, Log = breakpoint.LogMessage, Suspend = suspend };
                again.Count = kept.Hits == hits ? kept.Count : 0;
                now.Add(again);
                said.Add(Said(again, arguments.Source));
                continue;
            }
            JsonElement set = await run.Page.SendAsync(CdpCommands.SetBreakpointByUrl, new BreakpointByUrl(
                breakpoint.Line - 1, WebAssemblyValues.PatternOf(path, ignoreCase: !OperatingSystem.IsLinux()), breakpoint.Column is int column ? column - 1 : 0, breakpoint.Condition));
            string browserId = JsonMembers.Text(set, "breakpointId") ?? throw new DapException(DapCommands.SetBreakpoints, "The browser gave the breakpoint no id");
            Placed made = new(++lastId, browserId, breakpoint.Line, breakpoint.Column, breakpoint.Condition, hits, breakpoint.LogMessage, suspend)
            {
                Bound = JsonMembers.Items(set, "locations") is [var location, ..] ? (JsonMembers.Number(location, "lineNumber") ?? breakpoint.Line - 1) + 1 : null,
            };
            now.Add(made);
            said.Add(Said(made, arguments.Source));
        }
        foreach (Placed gone in had)
        {
            await run.Page.SendAsync(CdpCommands.RemoveBreakpoint, new { breakpointId = gone.BrowserId });
        }
        placed[path] = now;
        return new SetBreakpointsResponse(said);
    }

    /// <summary>What the session is told of a breakpoint: bound where the page has its code, else waiting for it to load.</summary>
    private static DapBreakpoint Said(Placed breakpoint, DapSource source) => breakpoint.Bound is int line
        ? new DapBreakpoint { Id = breakpoint.Id, Verified = true, Line = line, Source = source }
        : new DapBreakpoint { Id = breakpoint.Id, Verified = false, Reason = "pending", Line = breakpoint.Line, Source = source,
            Message = "The page has not loaded this code yet: it binds as the browser loads it" };

    /// <summary>A breakpoint by its file, for the proxy: its line and column from zero, the file matched whole by a pattern.</summary>
    private sealed record BreakpointByUrl(int LineNumber, string UrlRegex, int ColumnNumber, string? Condition);

    /// <summary>Exceptions stop in the browser as a whole (every one, or every one left unhandled), never by type.</summary>
    private async Task<SetExceptionBreakpointsResponse> SetExceptionsAsync(SetExceptionBreakpointsArguments arguments)
    {
        IReadOnlyList<DapExceptionOptions> options = arguments.ExceptionOptions ?? [];
        static bool Typed(DapExceptionOptions option) => option.Path is { Count: > 1 };
        string state = options.Any(o => !Typed(o) && o.BreakMode == DapBreakModes.Always) ? "all"
            : options.Any(o => !Typed(o) && o.BreakMode != DapBreakModes.Never) ? "uncaught" : "none";
        if (browser is { } run)
        {
            await run.Page.SendAsync(CdpCommands.SetPauseOnExceptions, new { state });
        }
        return new SetExceptionBreakpointsResponse([.. options.Select(o => Typed(o)
            ? new DapBreakpoint { Verified = false, Reason = "failed", Message = "In the browser, .NET's debug proxy stops at every exception or every one left unhandled, never by type" }
            : new DapBreakpoint { Verified = true })]);
    }

    private StackTraceResponse StackTrace(StackTraceArguments arguments) => stop is null || arguments.ThreadId != PageThread ? new StackTraceResponse([])
        : new StackTraceResponse([.. frames.Select((f, level) => new DapStackFrame
        {
            Id = FrameId(level),
            Name = f.Method,
            Source = f.Path is { } path ? new DapSource { Path = path, Name = Path.GetFileName(path) } : null,
            Line = f.Line + 1,
            Column = f.Column + 1,
        })]);

    /// <summary>A frame's id at this stop: its level, and the stop it is of, so a frame of a stop gone is told apart.</summary>
    private int FrameId(int level) => (generation % 0x10000 * 0x1000) + level + 1;

    /// <summary>The frame a stop's id names, or a refusal for one of a stop gone.</summary>
    private WebAssemblyFrame Frame(int frameId, string command)
    {
        int level = (frameId & 0xFFF) - 1;
        return stop is not null && frameId >> 12 == generation % 0x10000 && level >= 0 && level < frames.Count ? frames[level]
            : throw new DapException(command, "That frame is of a stop the page has gone on from");
    }

    private ScopesResponse Scopes(int frameId)
    {
        WebAssemblyFrame frame = Frame(frameId, DapCommands.Scopes);
        return frame.Locals is not { } locals ? new ScopesResponse([])
            : new ScopesResponse([new DapScope("Locals", Reference(() => PropertiesAsync(locals, null, frame)), false)]);
    }

    /// <summary>An object's members as the proxy reads them, fields and properties alike; a property it reads only by its
    /// getter is read as its row is opened. <paramref name="expression"/> names the object, null for a frame's locals.</summary>
    private async Task<IReadOnlyList<DapVariable>> PropertiesAsync(string objectId, string? expression, WebAssemblyFrame frame)
    {
        JsonElement read = await browser!.Page.SendAsync(CdpCommands.GetProperties, new { objectId, ownProperties = false });
        List<DapVariable> rows = [];
        foreach (JsonElement property in JsonMembers.Items(read, "result").Concat(JsonMembers.Items(read, "privateProperties")).Concat(JsonMembers.Items(read, "internalProperties")))
        {
            if (JsonMembers.Text(property, "name") is not { } member || rows.Any(r => r.Name == member))
            {
                continue;
            }
            string named = expression is null ? member : $"{expression}.{member}";
            if (JsonMembers.Object(property, "value") is { } value)
            {
                rows.Add(Row(member, named, value, frame));
            }
            else if (JsonMembers.Object(property, "get") is not null)
            {
                rows.Add(new DapVariable
                {
                    Name = member,
                    Value = "…",
                    EvaluateName = named,
                    PresentationHint = new DapPresentationHint(Lazy: true),
                    VariablesReference = Reference(async () => [await GetterAsync(member, named, frame)]),
                    Unsettable = NoSet,
                });
            }
        }
        return rows;
    }

    /// <summary>A property read by its getter, evaluated in the frame it was opened from.</summary>
    private async Task<DapVariable> GetterAsync(string member, string expression, WebAssemblyFrame frame)
    {
        JsonElement evaluated = await browser!.Page.SendAsync(CdpCommands.EvaluateOnCallFrame, new { callFrameId = frame.Id, expression });
        return JsonMembers.Object(evaluated, "exceptionDetails") is { } failed
            ? new DapVariable { Name = member, Value = WebAssemblyValues.Failure(failed), EvaluateName = expression, PresentationHint = new DapPresentationHint(Attributes: ["failedEvaluation"]) }
            : Row(member, expression, JsonMembers.Object(evaluated, "result") ?? default, frame);
    }

    private DapVariable Row(string member, string? expression, JsonElement value, WebAssemblyFrame frame) => new()
    {
        Name = member,
        Value = WebAssemblyValues.Shown(value),
        Type = WebAssemblyValues.TypeOf(value),
        EvaluateName = expression,
        VariablesReference = WebAssemblyValues.Inside(value) is { } id ? Reference(() => PropertiesAsync(id, expression, frame)) : 0,
        Unsettable = NoSet,
    };

    /// <summary>A reference to what a row holds, read as it is opened, until the page goes on.</summary>
    private int Reference(Func<Task<IReadOnlyList<DapVariable>>> read)
    {
        int reference = ++lastReference;
        inside[reference] = read;
        return reference;
    }

    private async Task<VariablesResponse> VariablesAsync(int reference) => inside.TryGetValue(reference, out Func<Task<IReadOnlyList<DapVariable>>>? read)
        ? new VariablesResponse(await read())
        : throw new DapException(DapCommands.Variables, "Those values are of a stop the page has gone on from");

    private async Task<EvaluateResponse> EvaluateAsync(EvaluateArguments arguments)
    {
        if (stop is null || browser is not { } run || frames.Count == 0)
        {
            throw new DapException(DapCommands.Evaluate, "The page is running: its .NET code is read only while it is paused there");
        }
        WebAssemblyFrame frame = arguments.FrameId is int frameId ? Frame(frameId, DapCommands.Evaluate) : frames[0];
        JsonElement evaluated = await run.Page.SendAsync(CdpCommands.EvaluateOnCallFrame, new { callFrameId = frame.Id, expression = arguments.Expression });
        if (JsonMembers.Object(evaluated, "exceptionDetails") is { } failed)
        {
            throw new DapException(DapCommands.Evaluate, WebAssemblyValues.Failure(failed));
        }
        DapVariable row = Row(arguments.Expression, arguments.Expression, JsonMembers.Object(evaluated, "result") ?? default, frame);
        return new EvaluateResponse { Result = row.Value, Type = row.Type, VariablesReference = row.VariablesReference };
    }

    private ExceptionInfoResponse ExceptionInfo() => stop is { } paused && JsonMembers.Text(paused, "reason") is "exception" or "promiseRejection"
        ? new ExceptionInfoResponse
        {
            ExceptionId = JsonMembers.Text(JsonMembers.Object(paused, "data"), "className") ?? "Exception",
            Description = JsonMembers.Text(JsonMembers.Object(paused, "data"), "description"),
            BreakMode = JsonMembers.Bool(JsonMembers.Object(paused, "data"), "uncaught") == true ? DapBreakModes.Unhandled : DapBreakModes.Always,
            Details = new DapExceptionDetails
            {
                Message = JsonMembers.Text(JsonMembers.Object(paused, "data"), "description"),
                TypeName = JsonMembers.Text(JsonMembers.Object(paused, "data"), "className"),
            },
        }
        : throw new DapException(DapCommands.ExceptionInfo, "The page did not stop at an exception");

    private async Task<bool> ResumeAsync(string command, Going how)
    {
        if (stop is null || browser is not { } run)
        {
            return true;
        }
        going = how;
        Left();
        await run.Page.SendAsync(command);
        return true;
    }

    private async Task<bool> PauseAsync()
    {
        if (stop is null && browser is { } run && going != Going.Pausing)
        {
            going = Going.Pausing;
            await run.Page.SendAsync(CdpCommands.Pause);
        }
        return true;
    }

    /// <summary>Text typed into the console of a development server launched, for its standard input; its end closes that input.</summary>
    private async Task<bool> InputAsync(InputArguments arguments)
    {
        if (server is not { } process)
        {
            throw new DapException(DapCommands.Input, "The page reads no input from the console");
        }
        try
        {
            if (arguments.Close == true)
            {
                process.StandardInput.Close();
            }
            else
            {
                await process.StandardInput.WriteAsync(arguments.Text);
                await process.StandardInput.FlushAsync();
            }
        }
        catch (Exception gone) when (gone is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new DapException(DapCommands.Input, $"The development server takes no more input: {gone.Message}");
        }
        return true;
    }

    /// <summary>Ends the browser and the proxy; a Stop of a Blazor Web App's page stops its server too, as a Stop of the
    /// server does, and a development server this adapter launched goes with its session whatever ends it.</summary>
    private async Task<bool> DisconnectAsync(DisconnectArguments arguments)
    {
        await ending.CancelAsync();
        if (browser is { } run)
        {
            await run.DisposeAsync();
        }
        if (server is { } launched)
        {
            ChildProcesses.Kill(launched);
        }
        else if (arguments.TerminateDebuggee == true && terminate is not null)
        {
            await terminate();
        }
        return true;
    }

    /// <summary>The disconnect has been answered: the adapter ends, and its session hears it gone.</summary>
    private void Ended()
    {
        end.End();
        IdeErrors.Observe(connection.DisposeAsync().AsTask());
    }

    /// <summary>An event of the page's, taken in turn with the session's requests.</summary>
    private Task HeardAsync(string method, JsonElement parameters)
    {
        switch (method)
        {
            case CdpEvents.Paused:
                return Held(() => OnPausedAsync(parameters));
            case CdpEvents.Resumed:
                return Held(() => Task.FromResult(OnResumed()));
            case CdpEvents.BreakpointResolved:
                return Held(() => Task.FromResult(Resolved(parameters)));
            case CdpEvents.ConsoleCalled:
                Console(parameters);
                break;
            case CdpEvents.ExceptionThrown:
                Tell(DapEvents.Output, new OutputEvent($"{WebAssemblyValues.Failure(JsonMembers.Object(parameters, "exceptionDetails") ?? default)}\n", DapOutputCategories.Stderr));
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>The page stopped: in .NET code, told to the session as a step's end, a pause, an exception or a breakpoint,
    /// where its hit count holds (a tracepoint logs and goes on); a stop in the browser's own code alone goes on unseen.</summary>
    private async Task<bool> OnPausedAsync(JsonElement paused)
    {
        IReadOnlyList<WebAssemblyFrame> stack = WebAssemblyValues.Frames(paused);
        Going was = going;
        going = Going.None;
        if (browser is not { } run)
        {
            return false;
        }
        if (stack.Count == 0 && was != Going.Pausing)
        {
            await run.Page.SendAsync(CdpCommands.Resume);
            return false;
        }
        stop = paused;
        frames = stack;
        generation++;
        inside.Clear();
        string? reason = JsonMembers.Text(paused, "reason");
        if (reason is "exception" or "promiseRejection")
        {
            Stopped(StopReasons.Exception, JsonMembers.Text(JsonMembers.Object(paused, "data"), "description"), null);
            return true;
        }
        if (was == Going.Stepping || was == Going.Pausing)
        {
            Stopped(was == Going.Stepping ? StopReasons.Step : StopReasons.Pause, stack.Count == 0 ? OutsideDotnet : null, null);
            return true;
        }
        HashSet<string> hit = [.. JsonMembers.Texts(paused, "hitBreakpoints")];
        Placed? at = placed.Values.SelectMany(p => p).FirstOrDefault(p => hit.Contains(p.BrowserId));
        if (at is null)
        {
            Stopped(StopReasons.Breakpoint, null, null);
            return true;
        }
        at.Count++;
        bool holds = at.Hits is not { } hits || hits.IsMet(at.Count);
        if (holds && at.Log is { } message)
        {
            Say(await LoggedAsync(message, stack[0]));
        }
        else if (holds && !at.Suspend)
        {
            Say("Breakpoint reached");
        }
        if (holds && at.Suspend)
        {
            Stopped(StopReasons.Breakpoint, null, [at.Id]);
            return true;
        }
        Left();
        going = Going.Resuming;
        await run.Page.SendAsync(CdpCommands.Resume);
        return false;
    }

    /// <summary>A tracepoint's message with each expression in it read in the frame it stopped in.</summary>
    private async Task<string> LoggedAsync(string message, WebAssemblyFrame frame)
    {
        StringBuilder said = new();
        foreach ((string text, bool expression) in Godot.GodotScriptBreakpoints.Pieces(message))
        {
            if (!expression)
            {
                said.Append(text);
                continue;
            }
            JsonElement evaluated = await browser!.Page.SendAsync(CdpCommands.EvaluateOnCallFrame, new { callFrameId = frame.Id, expression = text });
            said.Append(JsonMembers.Object(evaluated, "exceptionDetails") is { } failed ? $"<{WebAssemblyValues.Failure(failed)}>"
                : JsonMembers.Object(evaluated, "result") is { } value && JsonMembers.Text(value, "type") == "string" ? JsonMembers.Text(value, "value") ?? string.Empty
                : WebAssemblyValues.Shown(JsonMembers.Object(evaluated, "result") ?? default));
        }
        return said.ToString();
    }

    /// <summary>The page went on: of itself (no continue or step of the session's), the session hears it.</summary>
    private bool OnResumed()
    {
        bool own = going is Going.Resuming or Going.Stepping;
        if (going == Going.Resuming)
        {
            going = Going.None;
        }
        if (stop is not null && !own)
        {
            Left();
            Tell(DapEvents.Continued, new ContinuedEvent(PageThread));
        }
        return true;
    }

    /// <summary>A breakpoint the page bound as it loaded its code.</summary>
    private bool Resolved(JsonElement parameters)
    {
        string? browserId = JsonMembers.Text(parameters, "breakpointId");
        Placed? bound = null;
        string? path = null;
        foreach ((string file, List<Placed> list) in placed)
        {
            if (list.FirstOrDefault(p => p.BrowserId == browserId) is { } found)
            {
                (bound, path) = (found, file);
            }
        }
        if (bound is null)
        {
            return false;
        }
        int line = (JsonMembers.Number(JsonMembers.Object(parameters, "location"), "lineNumber") ?? bound.Line - 1) + 1;
        bound.Bound = line;
        Tell(DapEvents.Breakpoint, new BreakpointEvent
        {
            Breakpoint = new DapBreakpoint { Id = bound.Id, Verified = true, Line = line, Source = new DapSource { Path = path, Name = Path.GetFileName(path) } },
        });
        return true;
    }

    /// <summary>What the page wrote to its console, told as the program's output.</summary>
    private void Console(JsonElement parameters)
    {
        string words = string.Join(' ', JsonMembers.Items(parameters, "args").Select(a => JsonMembers.Text(a, "type") == "string"
            ? JsonMembers.Text(a, "value") ?? string.Empty : WebAssemblyValues.Shown(a)));
        string category = JsonMembers.Text(parameters, "type") is "error" or "warning" or "assert" ? DapOutputCategories.Stderr : DapOutputCategories.Stdout;
        Tell(DapEvents.Output, new OutputEvent($"{words}\n", category));
    }

    private void Stopped(string reason, string? text, IReadOnlyList<int>? hit) => Tell(DapEvents.Stopped, new StoppedEvent
    {
        Reason = reason,
        Text = text,
        Description = text,
        ThreadId = PageThread,
        AllThreadsStopped = true,
        HitBreakpointIds = hit,
    });

    /// <summary>The stop is over: what was read of it goes.</summary>
    private void Left()
    {
        stop = null;
        frames = [];
        inside.Clear();
    }

    private void Say(string line) => Tell(DapEvents.Output, new OutputEvent($"{line}\n", DapOutputCategories.Console));

    /// <summary>An event for the session, unless it has gone.</summary>
    private void Tell(string eventName, object? body)
    {
        if (end.HasEnded)
        {
            return;
        }
        try
        {
            connection.SendEvent(eventName, body);
        }
        catch (InvalidOperationException) when (end.HasEnded)
        {
            // The session went as this was told.
        }
    }
}
