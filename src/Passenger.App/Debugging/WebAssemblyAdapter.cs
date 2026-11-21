using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>A Blazor app's WebAssembly half's debugger: a debug adapter inside the IDE speaking Passenger's protocol to its
/// session (over an <see cref="InProcessEnd"/>) and the Chrome DevTools Protocol to the app's page in a Chromium browser,
/// through .NET's browser debug proxy (<see cref="BrowserRun"/>), so the Debug window, Variables, the gutter and agents'
/// debug tools show a stop in the browser as they show any. Breakpoints go by file and line, their conditions, hit counts
/// and messages read here at each hit, as every debugger reads them (<see cref="BreakpointHits"/>). Beside a Blazor Web
/// App's server it attaches once the server listens; for a standalone app it launches the development server itself, its
/// output the session's. Requests and the page's events are taken one at a time.</summary>
public sealed class WebAssemblyAdapter : InProcessAdapter
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

    /// <summary>What ends the program a Stop of this session ends: the server's session beside it; null where this adapter
    /// launched the development server, which it ends itself.</summary>
    private readonly Func<Task>? terminate;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource ending = new();
    private BrowserRun? browser;

    /// <summary>Whether the page has been opened at the app's address.</summary>
    private volatile bool opened;

    /// <summary>Whether the page's .NET runtime has started since the page was last loaded, as the proxy says.</summary>
    private volatile bool runtimeReady;

    /// <summary>The breakpoints the page has, by file, each as asked and with its hits so far.</summary>
    private readonly Dictionary<string, List<Placed>> placed = new(PathNames.Comparer);
    private int lastId;

    private JsonElement? stop;
    private IReadOnlyList<WebAssemblyFrame> frames = [];
    private int generation;
    private Going going;

    private enum Going
    {
        None,
        Stepping,
        Pausing,
        Resuming,
    }

    /// <summary>A breakpoint as the page has it: what was asked, in which file, its hits, and the proxy's id of the page's
    /// breakpoint at its place, which stops at every hit.</summary>
    private sealed class Placed(int id, DapSourceBreakpoint request, string file, string browserId) : AskedLine(id, request)
    {
        public string File { get; } = file;

        public string BrowserId { get; } = browserId;

        /// <summary>The line the page bound it to (from one), once it has loaded its code; null before.</summary>
        public int? Bound { get; set; }
    }

    /// <summary>The adapter of a WebAssembly app's page over <paramref name="end"/>, its browser's profile kept under
    /// <paramref name="profiles"/>; <paramref name="terminate"/> ends the app's server, as a Stop of this session asks, null
    /// where the session launches the development server itself.</summary>
    public WebAssemblyAdapter(BrowserDebugging debugging, string profiles, string name, InProcessEnd end, Func<Task>? terminate) : base(end)
    {
        this.debugging = debugging;
        this.profiles = profiles;
        this.name = name;
        this.terminate = terminate;
        Register();
    }

    protected override string Debuggee => "page";

    protected override string NoInput => "The page reads no input from the console";

    protected override string LaunchedName => "The development server";

    /// <summary>Whether a file's breakpoints go to a browser session of these projects: one in their own folders.</summary>
    public static bool Takes(IReadOnlyList<string> projects, string path) => projects.Any(p => ProjectFolders.Holds(Path.GetDirectoryName(p)!, path));

    private static DapCapabilities Answered => Capabilities(".NET's browser debug proxy watches no data") with { SupportsExceptionOptions = true };

    private void Register()
    {
        Connection.Handle<InitializeArguments, DapCapabilities>(DapCommands.Initialize, _ => Task.FromResult(Answered), then: () => Tell(DapEvents.Initialized, null));
        Connection.Handle<AttachArguments>(DapCommands.Attach, _ => Held(() => OpenAsync(DapCommands.Attach)));
        Connection.Handle<LaunchArguments>(DapCommands.Launch, arguments => Held(() => LaunchAsync(arguments)));
        Connection.Handle<DapEmpty>(DapCommands.ConfigurationDone, _ => Held(ConfiguredAsync));
        Connection.Handle<SetBreakpointsArguments, SetBreakpointsResponse>(DapCommands.SetBreakpoints, arguments => Held(() => SetBreakpointsAsync(arguments)));
        Connection.Handle<SetExceptionBreakpointsArguments, SetExceptionBreakpointsResponse>(DapCommands.SetExceptionBreakpoints,
            arguments => Held(() => SetExceptionsAsync(arguments)));
        Connection.Handle<DapEmpty, ThreadsResponse>(DapCommands.Threads, _ => Task.FromResult(new ThreadsResponse([new DapThread(PageThread, "Browser") { IsMain = true }])));
        Connection.Handle<StackTraceArguments, StackTraceResponse>(DapCommands.StackTrace, arguments => Held(() => Task.FromResult(StackTrace(arguments))));
        Connection.Handle<ScopesArguments, ScopesResponse>(DapCommands.Scopes, arguments => Held(() => Task.FromResult(Scopes(arguments.FrameId))));
        Connection.Handle<VariablesArguments, VariablesResponse>(DapCommands.Variables, arguments => Held(() => VariablesAsync(arguments.VariablesReference)));
        Connection.Handle<EvaluateArguments, EvaluateResponse>(DapCommands.Evaluate, arguments => Held(() => EvaluateAsync(arguments)));
        Connection.Handle<SetExpressionArguments, SetExpressionResponse>(DapCommands.SetExpression,
            _ => Task.FromException<SetExpressionResponse>(new DapException(DapCommands.SetExpression, NoSet)));
        Connection.Handle<ThreadArguments, ExceptionInfoResponse>(DapCommands.ExceptionInfo, _ => Held(() => Task.FromResult(ExceptionInfo())));
        Connection.Handle<ThreadArguments>(DapCommands.Continue, _ => Held(() => ResumeAsync(CdpCommands.Resume, Going.Resuming)));
        Connection.Handle<ThreadArguments>(DapCommands.Next, _ => Held(() => ResumeAsync(CdpCommands.StepOver, Going.Stepping)));
        Connection.Handle<StepInArguments>(DapCommands.StepIn, _ => Held(() => ResumeAsync(CdpCommands.StepInto, Going.Stepping)));
        Connection.Handle<ThreadArguments>(DapCommands.StepOut, _ => Held(() => ResumeAsync(CdpCommands.StepOut, Going.Stepping)));
        Connection.Handle<ThreadArguments>(DapCommands.Pause, _ => Held(PauseAsync));
        Connection.Handle<InputArguments>(DapCommands.Input, arguments => InputAsync(arguments));
        Connection.Handle<DisconnectArguments>(DapCommands.Disconnect, arguments => DisconnectAsync(arguments), then: Ended, evenIfFailed: true);
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
        ProcessStartInfo start = ChildProcesses.Command(program, arguments.Args ?? [], arguments.Cwd, arguments.Env);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        Tell(DapEvents.Output, new OutputEvent($"{ChildProcesses.Shown(start)}\n", DapOutputCategories.Console));
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            throw new DapException(DapCommands.Launch, $"Could not start {Path.GetFileName(start.FileName)}: {error.Message}");
        }
        Launched = process;
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
                : $"Nothing listened at {debugging.Page} within {Durations.Said(Waits.Of(ListenLimit))}, so there was no page to debug in the browser");
        }
        BrowserRun run;
        try
        {
            run = await BrowserRun.StartAsync(debugging, profiles, Say, NetworkEnded, ending.Token);
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
        if (Launched is null)
        {
            Tell(DapEvents.Process, new ProcessEvent($"{debugging.Browser.Name} at {debugging.Page}", run.BrowserId, IsLocalProcess: true, StartMethod: DapCommands.Attach));
        }
        return true;
    }

    /// <summary>The browser was closed, or its page's conversation ended: the session ends, the development server with it.</summary>
    private async Task BrowserEndedAsync(BrowserRun run)
    {
        await run.Ended;
        if (End.HasEnded || ending.IsCancellationRequested)
        {
            return;
        }
        Say($"{debugging.Browser.Name} closed: debugging in the browser has ended");
        if (Launched is { } launched)
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
            opened = true;
            await run.Page.SendAsync(CdpCommands.PageNavigate, new { url = debugging.Page.ToString() });
        }
        return true;
    }

    /// <summary>The browser's network service ended, on any thread: a page still loading its .NET code lost what it was
    /// fetching and fails to start it in the moments the service takes to come back, which nothing in a page left as it is
    /// would try again, so it is loaded again.</summary>
    private void NetworkEnded()
    {
        if (opened && !runtimeReady && !ending.IsCancellationRequested)
        {
            IdeErrors.Observe(BackgroundWork.Run(() => Held(ReloadAsync)));
        }
    }

    /// <summary>The page loaded again, saying why, unless its .NET code has started meanwhile or the session has ended.</summary>
    private async Task<bool> ReloadAsync()
    {
        if (browser is not { } run || runtimeReady || End.HasEnded || ending.IsCancellationRequested)
        {
            return false;
        }
        Say($"{debugging.Browser.Name}'s network service ended as the page loaded its .NET code, which it could then not fetch: the page is loaded again");
        try
        {
            await run.Page.SendAsync(CdpCommands.PageReload);
        }
        catch (CdpException refused) when (!End.HasEnded && !ending.IsCancellationRequested)
        {
            Say($"The page could not be loaded again: {refused.Reason}");
        }
        catch (CdpException)
        {
            // The session ended meanwhile, and the browser with it.
        }
        return true;
    }

    /// <summary>A file's breakpoints, asked for again: one asked for just as it was is kept, one in its place counts on from
    /// its hits while its condition and hit count stay (<see cref="BreakpointHits.Reconcile"/>). The page holds one
    /// breakpoint at each place, kept while a breakpoint stands there.</summary>
    private async Task<SetBreakpointsResponse> SetBreakpointsAsync(SetBreakpointsArguments arguments)
    {
        IReadOnlyList<DapSourceBreakpoint> asked = arguments.Breakpoints ?? [];
        if (arguments.Source.Path is not { } path || browser is not { } run)
        {
            return new SetBreakpointsResponse([.. asked.Select(_ => new DapBreakpoint { Verified = false, Reason = BreakpointReasons.Failed, Message = "The browser is not debugged" })]);
        }
        List<DapSourceBreakpoint> taken = [.. asked.Where(b => Refused(b) is null)];
        (Placed? Kept, int Hits)[] matched = BreakpointHits.Reconcile(taken, placed.GetValueOrDefault(path) ?? [], p => p.Request,
            static (was, now) => was.Line == now.Line && was.Column == now.Column, out List<Placed> gone);
        List<Placed> now = [];
        List<DapBreakpoint> said = [];
        int next = 0;
        foreach (DapSourceBreakpoint breakpoint in asked)
        {
            if (Refused(breakpoint) is { } refused)
            {
                said.Add(refused);
                continue;
            }
            (Placed? kept, int hits) = matched[next++];
            if (kept is null && gone.FirstOrDefault(p => p.Request.Line == breakpoint.Line && p.Request.Column == breakpoint.Column) is { } there)
            {
                gone.Remove(there);
                kept = new Placed(++lastId, breakpoint, path, there.BrowserId) { Bound = there.Bound, HitCount = hits };
            }
            else if (kept is null)
            {
                JsonElement set = await run.Page.SendAsync(CdpCommands.SetBreakpointByUrl, new BreakpointByUrl(
                    breakpoint.Line - 1, WebAssemblyValues.PatternOf(path, ignoreCase: !OperatingSystem.IsLinux()), breakpoint.Column is int column ? column - 1 : 0));
                string browserId = JsonMembers.Text(set, "breakpointId") ?? throw new DapException(DapCommands.SetBreakpoints, "The browser gave the breakpoint no id");
                kept = new Placed(++lastId, breakpoint, path, browserId)
                {
                    Bound = JsonMembers.Items(set, "locations") is [var location, ..] ? (JsonMembers.Number(location, "lineNumber") ?? breakpoint.Line - 1) + 1 : null,
                    HitCount = hits,
                };
            }
            now.Add(kept);
            said.Add(Said(kept, arguments.Source));
        }
        foreach (Placed left in gone)
        {
            await run.Page.SendAsync(CdpCommands.RemoveBreakpoint, new { breakpointId = left.BrowserId });
        }
        placed[path] = now;
        return new SetBreakpointsResponse(said);
    }

    /// <summary>What the session is told of a breakpoint: bound where the page has its code, else waiting for it to load.</summary>
    private static DapBreakpoint Said(Placed breakpoint, DapSource source) => breakpoint.Bound is int line
        ? new DapBreakpoint { Id = breakpoint.Id, Verified = true, Line = line, Source = source }
        : new DapBreakpoint { Id = breakpoint.Id, Verified = false, Reason = BreakpointReasons.Pending, Line = breakpoint.Request.Line, Source = source,
            Message = "The page has not loaded this code yet: it binds as the browser loads it" };

    /// <summary>A breakpoint by its file, for the proxy: its line and column from zero, the file matched whole by a pattern. It
    /// carries no condition: the proxy drops a breakpoint's condition as the page loads, so one is read here at each hit.</summary>
    private sealed record BreakpointByUrl(int LineNumber, string UrlRegex, int ColumnNumber);

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
            ? new DapBreakpoint { Verified = false, Reason = BreakpointReasons.Failed, Message = "In the browser, .NET's debug proxy stops at every exception or every one left unhandled, never by type" }
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
                    VariablesReference = Reference(async () => [await GetterAsync(member, named, expression is null ? null : objectId, frame)]),
                    Unsettable = NoSet,
                });
            }
        }
        return rows;
    }

    /// <summary>A property read by its getter: called on <paramref name="owner"/>, the object the proxy read it from, which
    /// invokes the getter alone; evaluated in the frame it was opened from where the proxy has no object to call it on.</summary>
    private async Task<DapVariable> GetterAsync(string member, string expression, string? owner, WebAssemblyFrame frame)
    {
        JsonElement evaluated = owner is null
            ? await browser!.Page.SendAsync(CdpCommands.EvaluateOnCallFrame, new { callFrameId = frame.Id, expression })
            : await browser!.Page.SendAsync(CdpCommands.CallFunctionOn, new { objectId = owner, functionDeclaration = "function(e){return this[e]}", arguments = new[] { new { value = member } } });
        return JsonMembers.Object(evaluated, "exceptionDetails") is { } failed
            ? new DapVariable { Name = member, Value = WebAssemblyValues.Failure(failed), EvaluateName = expression, PresentationHint = new DapPresentationHint(Attributes: [DapAttributes.FailedEvaluation]) }
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

    /// <summary>Ends the browser and the proxy; a Stop of a Blazor Web App's page stops its server too, as a Stop of the
    /// server does, and a development server this adapter launched goes with its session whatever ends it.</summary>
    private async Task<bool> DisconnectAsync(DisconnectArguments arguments)
    {
        await ending.CancelAsync();
        if (browser is { } run)
        {
            await run.DisposeAsync();
        }
        if (Launched is { } launched)
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
        End.End();
        IdeErrors.Observe(Connection.DisposeAsync().AsTask());
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
            case CdpEvents.RuntimeReady:
                runtimeReady = true;
                break;
            case CdpEvents.FrameNavigated when JsonMembers.Object(parameters, "frame") is { } frame && JsonMembers.Text(frame, "parentId") is null:
                // The page's own document came again: its .NET code starts again with it.
                runtimeReady = false;
                break;
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
    /// where its hit stops as every debugger's does (a tracepoint logs and goes on); a stop in the browser's own code alone
    /// goes on unseen.</summary>
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
        Left();
        stop = paused;
        frames = stack;
        generation++;
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
        if (placed.Values.SelectMany(p => p).FirstOrDefault(p => hit.Contains(p.BrowserId)) is not { } at)
        {
            Stopped(StopReasons.Breakpoint, null, null);
            return true;
        }
        WebAssemblyFrame top = stack[0];
        if (await BreakpointHits.StopsAsync(at, new HitReading
        {
            Named = "the breakpoint",
            Holds = condition => HoldsAsync(condition, top),
            Logged = message => LoggedAsync(message, top),
            Reached = () => BreakpointHits.Reached(at.File, top.Line + 1),
            Say = Say,
            Told = told => Told(told, at.File, at.Bound ?? at.Request.Line),
        }))
        {
            Stopped(StopReasons.Breakpoint, null, [at.Id]);
            return true;
        }
        Left();
        going = Going.Resuming;
        await run.Page.SendAsync(CdpCommands.Resume);
        return false;
    }

    /// <summary>Whether a breakpoint's condition holds in the frame it stopped in, or why it could not be told: one that
    /// fails, or is no bool.</summary>
    private async Task<ConditionRead> HoldsAsync(string condition, WebAssemblyFrame frame)
    {
        JsonElement evaluated;
        try
        {
            evaluated = await browser!.Page.SendAsync(CdpCommands.EvaluateOnCallFrame, new { callFrameId = frame.Id, expression = condition });
        }
        catch (CdpException refused)
        {
            return new ConditionRead(true, refused.Reason);
        }
        if (JsonMembers.Object(evaluated, "exceptionDetails") is { } failed)
        {
            return new ConditionRead(true, WebAssemblyValues.Failure(failed));
        }
        JsonElement? value = JsonMembers.Object(evaluated, "result");
        return JsonMembers.Text(value, "type") == "boolean" && JsonMembers.Bool(value, "value") is bool holds ? new ConditionRead(holds) : new ConditionRead(true, "it is not a bool");
    }

    /// <summary>A tracepoint's message with each expression in it read in the frame it stopped in.</summary>
    private Task<string> LoggedAsync(string message, WebAssemblyFrame frame) => BreakpointHits.LoggedAsync(message, async expression =>
    {
        JsonElement evaluated = await browser!.Page.SendAsync(CdpCommands.EvaluateOnCallFrame, new { callFrameId = frame.Id, expression });
        return JsonMembers.Object(evaluated, "exceptionDetails") is { } failed ? DisplayValue.Said($"<{WebAssemblyValues.Failure(failed)}>")
            : WebAssemblyValues.Displayed(JsonMembers.Object(evaluated, "result") ?? default);
    });

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
        int line = (JsonMembers.Number(JsonMembers.Object(parameters, "location"), "lineNumber") ?? bound.Request.Line - 1) + 1;
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

    private void Stopped(string reason, string? text, IReadOnlyList<int>? hit) => Stopped(reason, text, hit, PageThread, allThreads: true);

    /// <summary>The stop is over: what was read of it goes.</summary>
    protected override void Left()
    {
        stop = null;
        frames = [];
        base.Left();
    }
}
