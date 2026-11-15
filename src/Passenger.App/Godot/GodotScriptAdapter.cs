using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>A Godot game's GDScript debugger: a debug adapter inside the IDE speaking Passenger's protocol to its session (over
/// an <see cref="InProcessEnd"/>) and Godot's remote debug protocol to the game, over the game's one connection
/// (<see cref="GodotGame"/>), so the Debug window, Variables, the gutter and agents' debug tools show a GDScript stop as they
/// show any. Godot sets breakpoints by file and line and stops at script errors; their conditions, hit counts and messages
/// are read here at each hit, as every debugger reads them (<see cref="BreakpointHits"/>), the game let go on where they
/// say. An evaluation runs where Godot's own does, in a frame of a script's instance, and Set Value sets a member or property
/// of an object the game has. Beside a game's C# it attaches to the game Debug started; for a game of GDScript alone it
/// launches the game itself, its output the session's. Requests run on the UI thread, where the game's messages are heard.</summary>
public sealed class GodotScriptAdapter : InProcessAdapter
{
    /// <summary>The exception filter GDScript Errors is sent as.</summary>
    public const string ErrorsFilter = "gdscript-errors";

    /// <summary>Why a frame of a static function, or a pause outside every script, evaluates nothing.</summary>
    public const string NoInstance = "Godot evaluates GDScript only in a frame of a script's instance, and this one has none (a static function's)";

    /// <summary>Why steps are refused at a script error.</summary>
    public const string NoStepFromError = "Godot steps on from no script error: Continue goes on past it";

    /// <summary>Why a frame's local cannot be set.</summary>
    public const string NoLocalSet = "Godot's debugger sets no local of a frame, only a member or property of an object: set self's members instead";

    /// <summary>Why a value inside another (an element, a component) cannot be set alone.</summary>
    public const string NoPartSet = "Godot sets an object's property whole: set the member or property that holds this value";

    private readonly GodotGame game;

    /// <summary>What ends the game a Stop of this session ends: the session's beside it, or, for a game this adapter launched,
    /// null, the game's process ended here.</summary>
    private readonly Func<Task>? terminate;

    /// <summary>Whether its session starts a program in a tab of the Terminal window when asked (runInTerminal).</summary>
    private bool runsInTerminal;

    /// <summary>What each row a person may set names, by its expression: the object it is of and the property, for this stop.</summary>
    private readonly Dictionary<string, (ulong Owner, string Property)> settable = new(StringComparer.Ordinal);

    /// <summary>One question of the game's at a time: its answers come by name, in the order asked.</summary>
    private readonly SemaphoreSlim asking = new(1, 1);

    /// <summary>The breakpoints wanted in each file, by its <c>res://</c> path.</summary>
    private readonly Dictionary<string, List<AskedLine>> wanted = new(StringComparer.Ordinal);

    /// <summary>The breakpoints the game has, by file and line.</summary>
    private readonly HashSet<(string Resource, int Line)> inGame;

    private AskedErrors? errors;
    private int lastId;
    private bool configured;
    private GodotBreak? stop;
    private int generation;
    private IReadOnlyList<GodotScriptFrame>? frames;
    private readonly Dictionary<int, IReadOnlyList<GodotScriptVariable>> variables = [];
    private Going going;
    private bool letGo;

    private TaskCompletionSource<IReadOnlyList<GodotScriptFrame>>? stackAsked;
    private TaskCompletionSource<IReadOnlyList<GodotScriptVariable>>? variablesAsked;
    private List<GodotScriptVariable>? variablesHeard;
    private int variablesExpected;
    private TaskCompletionSource<GodotScriptVariable>? evaluationAsked;
    private List<GodotError>? evaluationErrors;

    private enum Going
    {
        None,
        Stepping,
        Pausing,
    }

    /// <summary>GDScript Errors as asked for, with its hits so far: a breakpoint of every script error.</summary>
    private sealed class AskedErrors(DapExceptionFilterOptions request) : IHitBreakpoint
    {
        public int Id => 0;

        public DapExceptionFilterOptions Request { get; } = request;

        public string? Condition => Request.Condition;

        public string? HitCondition => Request.HitCondition;

        public string? LogMessage => Request.LogMessage;

        public bool Suspend => Request.Suspend ?? true;

        public int HitCount { get; set; }

        public string? Message { get; set; }
    }

    /// <summary>The adapter of a game over <paramref name="end"/>; <paramref name="terminate"/> ends the game, as a Stop of
    /// this session asks; null where the session launches the game itself, which this adapter then ends.</summary>
    public GodotScriptAdapter(GodotGame game, InProcessEnd end, Func<Task>? terminate) : base(end)
    {
        this.game = game;
        this.terminate = terminate;
        inGame = [.. game.StartBreakpoints];
        Register();
    }

    protected override string Debuggee => "game";

    protected override string NoInput => $"{game.Name} reads its input from the program that started it";

    protected override string LaunchedName => game.Name;

    /// <summary>Whether a file is a GDScript file, whose breakpoints this adapter takes.</summary>
    public static bool Takes(string path) => Languages.Language.ForPath(path) == Languages.Language.GDScript;

    private static Task<T> OnUi<T>(Func<Task<T>> work) => UiThread.InvokeAsync(work);

    /// <summary>What it answers the session's initialize with.</summary>
    private static DapCapabilities Answered => Capabilities("Godot's debugger watches no data") with
    {
        SupportsExceptionFilterOptions = true,
        SupportsSetExpression = true,
        ExceptionBreakpointFilters = [new DapExceptionFilter(ErrorsFilter, "GDScript errors", Default: true, SupportsCondition: true)],
    };

    private void Register()
    {
        Connection.Handle<InitializeArguments, DapCapabilities>(DapCommands.Initialize, arguments =>
        {
            runsInTerminal = arguments.SupportsRunInTerminalRequest == true;
            return Task.FromResult(Answered);
        }, then: () => Tell(DapEvents.Initialized, null));
        Connection.Handle<AttachArguments>(DapCommands.Attach, _ => OnUi(AttachAsync));
        Connection.Handle<LaunchArguments>(DapCommands.Launch, arguments => OnUi(() => LaunchAsync(arguments)));
        Connection.Handle<InputArguments>(DapCommands.Input, arguments => OnUi(() => InputAsync(arguments)));
        Connection.Handle<SetExpressionArguments, SetExpressionResponse>(DapCommands.SetExpression, arguments => OnUi(() => SetAsync(arguments)));
        Connection.Handle<DapEmpty>(DapCommands.ConfigurationDone, _ => OnUi(ConfiguredAsync));
        Connection.Handle<SetBreakpointsArguments, SetBreakpointsResponse>(DapCommands.SetBreakpoints, arguments => OnUi(() => SetBreakpointsAsync(arguments)));
        Connection.Handle<SetExceptionBreakpointsArguments, SetExceptionBreakpointsResponse>(DapCommands.SetExceptionBreakpoints,
            arguments => OnUi(() => SetErrorsAsync(arguments)));
        Connection.Handle<DapEmpty, ThreadsResponse>(DapCommands.Threads, _ => OnUi(() => Task.FromResult(Threads())));
        Connection.Handle<StackTraceArguments, StackTraceResponse>(DapCommands.StackTrace, arguments => OnUi(() => StackTraceAsync(arguments)));
        Connection.Handle<ScopesArguments, ScopesResponse>(DapCommands.Scopes, arguments => OnUi(() => ScopesAsync(arguments.FrameId)));
        Connection.Handle<VariablesArguments, VariablesResponse>(DapCommands.Variables, arguments => OnUi(() => VariablesAsync(arguments.VariablesReference)));
        Connection.Handle<EvaluateArguments, EvaluateResponse>(DapCommands.Evaluate, arguments => OnUi(() => EvaluateAsync(arguments)));
        Connection.Handle<ThreadArguments, ExceptionInfoResponse>(DapCommands.ExceptionInfo, _ => OnUi(() => Task.FromResult(ExceptionInfo())));
        Connection.Handle<ThreadArguments>(DapCommands.Continue, _ => OnUi(() => ResumeAsync(DapCommands.Continue, GodotWords.Continue, Going.None)));
        Connection.Handle<ThreadArguments>(DapCommands.Next, _ => OnUi(() => ResumeAsync(DapCommands.Next, GodotWords.Next, Going.Stepping)));
        Connection.Handle<StepInArguments>(DapCommands.StepIn, _ => OnUi(() => ResumeAsync(DapCommands.StepIn, GodotWords.Step, Going.Stepping)));
        Connection.Handle<ThreadArguments>(DapCommands.StepOut, _ => OnUi(() => ResumeAsync(DapCommands.StepOut, GodotWords.Out, Going.Stepping)));
        Connection.Handle<ThreadArguments>(DapCommands.Pause, _ => OnUi(PauseAsync));
        Connection.Handle<DisconnectArguments>(DapCommands.Disconnect, arguments => OnUi(() => DisconnectAsync(arguments)), then: Ended, evenIfFailed: true);
    }

    /// <summary>Takes the game's debugging over once it has connected: its stops come here from then on.</summary>
    private async Task<bool> AttachAsync()
    {
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs e)
        {
            if (game.State != LiveTree.LiveAppState.Waiting)
            {
                connected.TrySetResult();
            }
        }
        game.StateChanged += Changed;
        try
        {
            Changed(null, EventArgs.Empty);
            await connected.Task.WaitAsync(Waits.Of(TimeSpan.FromSeconds(60)));
        }
        catch (TimeoutException)
        {
            throw new DapException(DapCommands.Attach, $"{game.Name} did not connect to its debugger");
        }
        finally
        {
            game.StateChanged -= Changed;
        }
        if (game.State != LiveTree.LiveAppState.Connected)
        {
            throw new DapException(DapCommands.Attach, $"{game.Name} ended before its debugger could take its GDScript");
        }
        game.Scripts = this;
        if (game.ProcessId is int id)
        {
            Tell(DapEvents.Process, new ProcessEvent(game.Name, id, IsLocalProcess: true, StartMethod: DapCommands.Attach));
        }
        return true;
    }

    /// <summary>Launches a game of GDScript alone, as the plan its session was given says: its output and errors told to the
    /// session as they come, its end once both are read, and the game ended with the session however that ends. Answered
    /// once the game has connected, from which its stops come here.</summary>
    private async Task<bool> LaunchAsync(LaunchArguments arguments)
    {
        string program = arguments.Program ?? throw new DapException(DapCommands.Launch, "No program to launch");
        if (arguments.Console == "integratedTerminal" && runsInTerminal)
        {
            return await LaunchInTerminalAsync(program, arguments);
        }
        System.Diagnostics.ProcessStartInfo start = ChildProcesses.Command(program, arguments.Args ?? [], arguments.Cwd, arguments.Env);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        if (arguments.Unseen == true)
        {
            ChildProcesses.OnDesktopOfItsOwn(start, $"{game.Name} is an agent's game, drawn where no one sees it for its frames");
        }
        Tell(DapEvents.Output, new OutputEvent($"{ChildProcesses.Shown(start)}\n", DapOutputCategories.Console));
        System.Diagnostics.Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            throw new DapException(DapCommands.Launch, $"Could not start {Path.GetFileName(start.FileName)}: {error.Message}");
        }
        Launched = process;
        // For the game's life: no wait for the IDE to be quiet waits out a game that runs until it is stopped.
        Task told = Task.WhenAll(BackgroundWork.Unheld(() => TellAsync(ChildProcesses.Output(process), DapOutputCategories.Stdout)),
            BackgroundWork.Unheld(() => TellAsync(ChildProcesses.Errors(process), DapOutputCategories.Stderr)));
        IdeErrors.Observe(BackgroundWork.Unheld(() => EndedAsync(process, told)));
        IdeErrors.Observe(BackgroundWork.Unheld(async () =>
        {
            await End.Exited;
            ChildProcesses.Kill(process);
        }));
        return await AttachAsync();
    }

    /// <summary>Has the session start the game in a tab of the Terminal window, which reads its keys and shows what it writes
    /// (<see cref="Run.GodotConsole"/>), and ends it there with a Stop; its end is its connection's. Answered once the game
    /// has connected.</summary>
    private async Task<bool> LaunchInTerminalAsync(string program, LaunchArguments arguments)
    {
        try
        {
            await Connection.RequestAsync<RunInTerminalResponse>(DapCommands.RunInTerminal,
                new RunInTerminalArguments("integrated", game.Name, arguments.Cwd, [program, .. arguments.Args ?? []], arguments.Env));
        }
        catch (DapException refused)
        {
            throw new DapException(DapCommands.Launch, $"Could not start {game.Name} in the terminal: {refused.Reason}");
        }
        return await AttachAsync();
    }

    /// <summary>What the game writes on one stream, told to the session as it comes, to the stream's end.</summary>
    private async Task TellAsync(TextReader stream, string category)
    {
        using (stream)
        {
            char[] buffer = new char[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                }
                catch (IOException)
                {
                    // A pipe broken as the game was killed: the stream has ended all the same.
                    read = 0;
                }
                if (read == 0)
                {
                    return;
                }
                Tell(DapEvents.Output, new OutputEvent(new string(buffer, 0, read), category));
            }
        }
    }

    /// <summary>The game launched has ended, what it wrote told: its exit code, then the session's end.</summary>
    private async Task EndedAsync(System.Diagnostics.Process process, Task told)
    {
        await process.WaitForExitAsync();
        await Task.WhenAny(told, Task.Delay(TimeSpan.FromSeconds(2)));
        Tell(DapEvents.Exited, new ExitedEvent(process.ExitCode));
        Tell(DapEvents.Terminated, new TerminatedEvent());
    }

    /// <summary>The session has sent its breakpoints: a stop the game made before (a breakpoint in what runs first) is told now.</summary>
    private Task<bool> ConfiguredAsync()
    {
        configured = true;
        if ((game.TakeHeldBreak() ?? early) is { } held)
        {
            early = null;
            IdeErrors.Observe(OnBreakAsync(held));
        }
        return Task.FromResult(true);
    }

    /// <summary>A stop the game made after the attach but before the session's breakpoints came, told once they have.</summary>
    private GodotBreak? early;

    private async Task<SetBreakpointsResponse> SetBreakpointsAsync(SetBreakpointsArguments arguments)
    {
        IReadOnlyList<DapSourceBreakpoint> asked = arguments.Breakpoints ?? [];
        string? path = arguments.Source.Path;
        string? resource = path is null ? null : Passenger.Workspace.GodotResPaths.Of(game.Folder, path);
        if (resource is null)
        {
            string why = $"Not in the Godot project the game runs ({game.Folder}), whose scripts alone it stops in";
            return new SetBreakpointsResponse([.. asked.Select(_ => new DapBreakpoint { Verified = false, Reason = BreakpointReasons.Failed, Message = why })]);
        }
        string text = await BackgroundWork.Run(() => File.Exists(path) ? File.ReadAllText(path!) : string.Empty);
        List<DapSourceBreakpoint> lines = [.. asked.Where(b => Refused(b) is null && GodotScriptBreakpoints.IsCodeLine(text, b.Line))];
        (AskedLine? Kept, int Hits)[] matched = BreakpointHits.Reconcile(lines, wanted.GetValueOrDefault(resource) ?? [], w => w.Request,
            static (was, now) => was.Line == now.Line, out _);
        List<DapBreakpoint> said = [];
        List<AskedLine> taken = [];
        int next = 0;
        foreach (DapSourceBreakpoint breakpoint in asked)
        {
            if (Refused(breakpoint) is { } refused)
            {
                said.Add(refused);
                continue;
            }
            if (!GodotScriptBreakpoints.IsCodeLine(text, breakpoint.Line))
            {
                said.Add(new DapBreakpoint { Verified = false, Reason = BreakpointReasons.Failed, Line = breakpoint.Line,
                    Message = $"Line {breakpoint.Line} has no code of the script's on it, and GDScript stops only at a line with code" });
                continue;
            }
            (AskedLine? kept, int hits) = matched[next++];
            AskedLine now = kept ?? new AskedLine(++lastId, breakpoint) { HitCount = hits };
            taken.Add(now);
            said.Add(new DapBreakpoint { Id = now.Id, Verified = true, Line = breakpoint.Line, Source = arguments.Source });
        }
        wanted[resource] = taken;
        HashSet<int> stopping = [.. taken.Select(w => w.Request.Line)];
        foreach ((string file, int line) in inGame.Where(b => b.Resource == resource && !stopping.Contains(b.Line)).ToList())
        {
            inGame.Remove((file, line));
            await SendAsync(GodotWords.Breakpoint, file, (long)line, false);
        }
        foreach (int line in stopping.Where(l => !inGame.Contains((resource, l))))
        {
            inGame.Add((resource, line));
            await SendAsync(GodotWords.Breakpoint, resource, (long)line, true);
        }
        return new SetBreakpointsResponse(said);
    }

    private async Task<SetExceptionBreakpointsResponse> SetErrorsAsync(SetExceptionBreakpointsArguments arguments)
    {
        bool on = arguments.Filters?.Contains(ErrorsFilter) == true;
        DapExceptionFilterOptions? options = arguments.FilterOptions?.FirstOrDefault(f => f.FilterId == ErrorsFilter);
        string? problem = BreakpointHits.Unreadable(options?.HitCondition);
        // A hit count it cannot read is said, the errors stopping as if it had none.
        DapExceptionFilterOptions asked = problem is null ? options ?? new DapExceptionFilterOptions(ErrorsFilter) : options! with { HitCondition = null };
        (AskedErrors? Kept, int Hits)[] matched = BreakpointHits.Reconcile<AskedErrors, DapExceptionFilterOptions>([asked], errors is { } before ? [before] : [], e => e.Request,
            static (_, _) => true, out _);
        errors = on ? matched[0].Kept ?? new AskedErrors(asked) { HitCount = matched[0].Hits } : null;
        await SendAsync(GodotWords.IgnoreErrorBreaks, !on);
        return new SetExceptionBreakpointsResponse(on ? [new DapBreakpoint { Verified = problem is null, Reason = problem is null ? null : BreakpointReasons.Failed, Message = problem }] : []);
    }

    private ThreadsResponse Threads() =>
        new([new DapThread((int)GodotMessage.MainThread, "Main thread") { IsMain = true },
            .. stop is { Thread: not GodotMessage.MainThread } stopped ? [new DapThread((int)stopped.Thread, $"Thread {stopped.Thread}")] : (DapThread[])[]]);

    private async Task<StackTraceResponse> StackTraceAsync(StackTraceArguments arguments)
    {
        if (stop is not { HasStack: true } stopped || arguments.ThreadId != stopped.Thread)
        {
            return new StackTraceResponse([]);
        }
        IReadOnlyList<GodotScriptFrame> stack = await FramesAsync();
        return new StackTraceResponse([.. stack.Select((f, level) => new DapStackFrame
        {
            Id = FrameId(level),
            Name = f.Function,
            Source = new DapSource { Path = game.PathOf(f.File), Name = Path.GetFileName(f.File) },
            Line = f.Line,
            Column = 1,
        })]);
    }

    /// <summary>A frame's id at this stop: its level, and the stop it is of, so a frame of a stop gone is told apart.</summary>
    private int FrameId(int level) => (generation % 0x10000 * 0x1000) + level + 1;

    /// <summary>The level of a frame this stop gave, or a refusal for one of a stop gone.</summary>
    private int Level(int frameId, string command)
    {
        int level = (frameId & 0xFFF) - 1;
        return frameId >> 12 == generation % 0x10000 && level >= 0 && frames is { } stack && level < stack.Count ? level
            : throw new DapException(command, "That frame is of a stop the game has gone on from");
    }

    private async Task<ScopesResponse> ScopesAsync(int frameId)
    {
        int level = Level(frameId, DapCommands.Scopes);
        IReadOnlyList<GodotScriptVariable> all = await VariablesOfAsync(level);
        List<DapVariable> rows = [.. all.Where(v => v.Scope == GodotScope.Local).Select(v => Row(v.Name, v.Name, v.Value, v.Type, v.Hint) with { Unsettable = NoLocalSet })];
        if (all.FirstOrDefault(v => v.Scope == GodotScope.Member && v.Name == "self") is { } self)
        {
            List<GodotScriptVariable> members = [.. all.Where(v => v.Scope == GodotScope.Member && v.Name != "self")];
            rows.Add(new DapVariable
            {
                Name = "self",
                Value = self.Shown,
                Type = self.TypeName,
                EvaluateName = "self",
                VariablesReference = Reference(() => SelfAsync(members, self)),
                Unsettable = "self is the object the frame runs in: set its members and properties",
            });
        }
        List<GodotScriptVariable> globals = [.. all.Where(v => v.Scope == GodotScope.Global)];
        if (globals.Count > 0)
        {
            rows.Add(new DapVariable
            {
                Name = "Globals",
                Value = Plural.Of(globals.Count, "autoload"),
                VariablesReference = Reference(() => Task.FromResult<IReadOnlyList<DapVariable>>([.. globals.Select(v => Row(v.Name, v.Name, v.Value, v.Type, v.Hint)
                    with { Unsettable = "An autoload is the project's own: set its members and properties" })])),
                PresentationHint = new DapPresentationHint(Kind: "virtual"),
            });
        }
        return new ScopesResponse([new DapScope("Locals", Reference(() => Task.FromResult<IReadOnlyList<DapVariable>>(rows)), false)]);
    }

    /// <summary>What <c>self</c> holds: the script's members as the frame sees them, then the object's properties as the game
    /// has them, by the class each comes from.</summary>
    private async Task<IReadOnlyList<DapVariable>> SelfAsync(IReadOnlyList<GodotScriptVariable> members, GodotScriptVariable self)
    {
        ulong? owner = (self.Value as GodotObjectId)?.Id;
        List<DapVariable> rows = [.. members.Select(m => Settable(Row(m.Name, $"self.{m.Name}", m.Value, m.Type, m.Hint), owner, m.Name, null))];
        if (owner is { } id)
        {
            rows.AddRange(await PropertiesAsync(id, "self", script: false));
        }
        return rows;
    }

    /// <summary>A row Set Value sets as a property of an object (<paramref name="owner"/>, null where it is unknown), unless
    /// <paramref name="why"/> says why not.</summary>
    private DapVariable Settable(DapVariable row, ulong? owner, string property, string? why)
    {
        if (why is null && owner is { } id && row.EvaluateName is { } expression)
        {
            settable[expression] = (id, property);
            return row;
        }
        return row with { Unsettable = why ?? "Godot names no object this value is a property of" };
    }

    /// <summary>An object's properties, each class's under its name; with <paramref name="script"/>, its script's members among
    /// them too.</summary>
    private async Task<IReadOnlyList<DapVariable>> PropertiesAsync(ulong id, string? expression, bool script = true)
    {
        GodotObjectState state;
        try
        {
            state = await game.InspectAsync(id);
        }
        catch (LiveTree.LiveAppException unread)
        {
            return [new DapVariable { Name = "error", Value = unread.Message }];
        }
        List<DapVariable> rows = [];
        foreach (IGrouping<string, GodotProperty> category in state.Properties.Where(p => script || (p.Usage & ScriptVariable) == 0).GroupBy(p => p.Category))
        {
            List<GodotProperty> properties = [.. category];
            rows.Add(new DapVariable
            {
                Name = category.Key.Length > 0 ? category.Key : state.Class,
                Value = Plural.Of(properties.Count, "property", "properties"),
                PresentationHint = new DapPresentationHint(Kind: "virtual"),
                VariablesReference = Reference(() => Task.FromResult<IReadOnlyList<DapVariable>>(
                    [.. properties.Select(p => Settable(Row(p.Name, $"{expression ?? $"instance_from_id({id})"}.{p.Name}", p.Value, p.Type, p.Value is GodotObjectId ? p.HintString : string.Empty),
                        id, p.Name, p.Settable ? null : $"{p.Name} is a {p.Type}{((p.Usage & ReadOnlyUsage) != 0 ? ", read-only" : string.Empty)}, which Godot's debugger does not set"))])),
            });
        }
        return rows;
    }

    /// <summary>The usage flag of a property a script declares.</summary>
    private const long ScriptVariable = 1 << 12;

    /// <summary>The usage flag of a property Godot's inspector shows read-only.</summary>
    private const long ReadOnlyUsage = 1 << 28;

    /// <summary>A variable's row: its value shown, its type, and what it holds behind a reference.</summary>
    private DapVariable Row(string name, string? expression, object? value, GodotType type, string hint)
    {
        int reference = value switch
        {
            GodotObjectId id => Reference(() => PropertiesAsync(id.Id, expression)),
            _ when GodotScriptValues.Inside(value, expression) is { Count: > 0 } held => Reference(() => Task.FromResult<IReadOnlyList<DapVariable>>(
                [.. held.Select(h => Row(h.Name, h.Expression, h.Value, GodotVariant.TypeOf(h.Value), string.Empty) with { Unsettable = NoPartSet })])),
            _ => 0,
        };
        return new DapVariable { Name = name, Value = GodotScriptValues.Shown(value, hint), Type = GodotScriptValues.TypeName(type, hint), EvaluateName = expression, VariablesReference = reference };
    }

    /// <summary>Set Value: the member or property a row names takes the value typed, read as every typed Godot value is
    /// (<see cref="GodotVariant.ReadAsync"/>: as its type's value is shown, else as GDScript in the frame), set in the game
    /// through <c>scene:set_object_property</c>, then read back; a value Godot would not take for it is said, the old one kept.</summary>
    private async Task<SetExpressionResponse> SetAsync(SetExpressionArguments arguments)
    {
        if (stop is null)
        {
            throw new DapException(DapCommands.SetExpression, "The game is running: a value is set only while it is paused");
        }
        if (!settable.TryGetValue(arguments.Expression, out (ulong Owner, string Property) target))
        {
            throw new DapException(DapCommands.SetExpression, $"{arguments.Expression} is no member or property of an object the game has shown here");
        }
        int level = arguments.FrameId is int frameId ? Level(frameId, DapCommands.SetExpression) : 0;
        (object? before, GodotType type) = await ReadBackAsync(target, level);
        (object? value, string? wanted) = await GodotVariant.ReadAsync(type, arguments.Value, async text =>
        {
            try
            {
                return ((await ReadAsync(text, level)).Value, null);
            }
            catch (DapException unread)
            {
                return (null, unread.Reason);
            }
        });
        if (wanted is not null)
        {
            throw new DapException(DapCommands.SetExpression, $"{arguments.Value} reads as no value here: {target.Property} takes {wanted}");
        }
        if (value is GodotObjectId or GodotObject)
        {
            throw new DapException(DapCommands.SetExpression, "Godot's debugger sets no object as a value: set it to null here, or in the script");
        }
        try
        {
            await game.SetPropertyAsync(target.Owner, target.Property, value);
        }
        catch (LiveTree.LiveAppException lost)
        {
            throw new DapException(DapCommands.SetExpression, lost.Message);
        }
        variables.Clear();
        (object? after, GodotType now) = await ReadBackAsync(target, level);
        string shown = GodotScriptValues.Shown(after);
        if (shown == GodotScriptValues.Shown(before) && GodotScriptValues.Shown(value) != shown)
        {
            throw new DapException(DapCommands.SetExpression, $"Godot kept {target.Property} as it was, {shown}: it takes no {GodotScriptValues.TypeName(GodotVariant.TypeOf(value))} "
                + $"{GodotScriptValues.Shown(value)} there");
        }
        return new SetExpressionResponse { Value = shown, Type = GodotScriptValues.TypeName(now) };
    }

    /// <summary>A target's value as the game has it now, and its type: a member of the frame's object from the frame's values,
    /// any other property from its object's.</summary>
    private async Task<(object? Value, GodotType Type)> ReadBackAsync((ulong Owner, string Property) target, int level)
    {
        IReadOnlyList<GodotScriptVariable> all = await VariablesOfAsync(level);
        if (all.FirstOrDefault(v => v.Scope == GodotScope.Member && v.Name == "self")?.Value is GodotObjectId self && self.Id == target.Owner
            && all.FirstOrDefault(v => v.Scope == GodotScope.Member && v.Name == target.Property) is { } member)
        {
            return (member.Value, member.Type);
        }
        try
        {
            GodotObjectState state = await game.InspectAsync(target.Owner);
            return state.Properties.FirstOrDefault(p => p.Name == target.Property) is { } property ? (property.Value, property.Type)
                : throw new DapException(DapCommands.SetExpression, $"{target.Property} is no longer a property of the object");
        }
        catch (LiveTree.LiveAppException unread)
        {
            throw new DapException(DapCommands.SetExpression, unread.Message);
        }
    }

    private async Task<EvaluateResponse> EvaluateAsync(EvaluateArguments arguments)
    {
        if (stop is not { } stopped)
        {
            throw new DapException(DapCommands.Evaluate, "The game is running: GDScript is read only while it is paused");
        }
        if (!stopped.HasStack)
        {
            throw new DapException(DapCommands.Evaluate, "The game is paused outside every script, where Godot evaluates no GDScript");
        }
        if (frames is null)
        {
            await FramesAsync();
        }
        int level = arguments.FrameId is int frameId ? Level(frameId, DapCommands.Evaluate) : 0;
        GodotScriptVariable answer = await ReadAsync(arguments.Expression, level);
        return new EvaluateResponse
        {
            Result = answer.Shown,
            Type = answer.TypeName,
            VariablesReference = Row(answer.Name, arguments.Expression, answer.Value, answer.Type, answer.Hint).VariablesReference,
        };
    }

    /// <summary>An expression's value in a frame of the stop; its failure (a parse error, a call that fails) throws what Godot
    /// said. Refused in a frame with no instance, where Godot's evaluation would let the game go on.</summary>
    private async Task<GodotScriptVariable> ReadAsync(string expression, int level)
    {
        if (!(await VariablesOfAsync(level)).Any(v => v.Scope == GodotScope.Member && v.Name == "self"))
        {
            throw new DapException(DapCommands.Evaluate, NoInstance);
        }
        GodotBreak stopped = stop ?? throw new DapException(DapCommands.Evaluate, "The game went on");
        await asking.WaitAsync();
        List<GodotError> raised = [];
        try
        {
            evaluationAsked = Question<GodotScriptVariable>();
            stackAsked = Question<IReadOnlyList<GodotScriptFrame>>();
            evaluationErrors = raised;
            Task<GodotScriptVariable> answered = evaluationAsked.Task;
            Task<IReadOnlyList<GodotScriptFrame>> fenced = stackAsked.Task;
            await game.SendToAsync(GodotWords.Evaluate, stopped.Thread, expression, (long)level);
            // Its errors are written out before the game reads its next question, whose answer comes after them.
            await game.SendToAsync(GodotWords.GetStackDump, stopped.Thread);
            GodotScriptVariable answer = await Answer(answered, "with the expression's value");
            await Answer(fenced, "with its stack");
            return raised.FirstOrDefault(e => !e.Warning) is { } failed ? throw new DapException(DapCommands.Evaluate, GodotScriptValues.Failure(failed)) : answer;
        }
        finally
        {
            evaluationAsked = null;
            stackAsked = null;
            evaluationErrors = null;
            asking.Release();
        }
    }

    private ExceptionInfoResponse ExceptionInfo() => stop is { IsError: true } stopped
        ? new ExceptionInfoResponse
        {
            ExceptionId = "GDScript error",
            Description = stopped.Error,
            BreakMode = DapBreakModes.Always,
            Details = new DapExceptionDetails { Message = stopped.Error, TypeName = "GDScript error" },
        }
        : throw new DapException(DapCommands.ExceptionInfo, "The game did not stop at a script error");

    private async Task<bool> ResumeAsync(string command, string word, Going how)
    {
        if (stop is not { } stopped)
        {
            return true;
        }
        if (how == Going.Stepping && !stopped.CanStep)
        {
            throw new DapException(command, NoStepFromError);
        }
        going = how;
        Left();
        await game.SendToAsync(word, stopped.Thread);
        return true;
    }

    private async Task<bool> PauseAsync()
    {
        if (stop is null && going != Going.Pausing)
        {
            going = Going.Pausing;
            await SendAsync(GodotWords.Break);
        }
        return true;
    }

    private async Task<bool> DisconnectAsync(DisconnectArguments arguments)
    {
        if (Launched is { } process)
        {
            // A game this adapter launched is the session's to its end, Detach or not.
            ChildProcesses.Kill(process);
            return true;
        }
        if (arguments.TerminateDebuggee == true && game.State == LiveTree.LiveAppState.Connected && terminate is not null)
        {
            await terminate();
            return true;
        }
        await LetGoAsync();
        return true;
    }

    /// <summary>Lets the game go on without this debugger: no breakpoint or error stops it, and a stop it is at goes on.</summary>
    private async Task LetGoAsync()
    {
        letGo = true;
        if (game.Scripts == this)
        {
            game.Scripts = null;
        }
        if (game.State != LiveTree.LiveAppState.Connected)
        {
            return;
        }
        // No stop waits for a script debugger from now: the game skips its breakpoints and goes on past its errors.
        game.LetScriptsGo();
        try
        {
            foreach ((string file, int line) in inGame.ToList())
            {
                await SendAsync(GodotWords.Breakpoint, file, (long)line, false);
            }
            inGame.Clear();
            if (stop is { } stopped)
            {
                Left();
                await game.SendToAsync(GodotWords.Continue, stopped.Thread);
            }
        }
        catch (LiveTree.LiveAppException)
        {
            // The game went as it was let go.
        }
    }

    /// <summary>The disconnect has been answered: the adapter ends, and its session hears it gone.</summary>
    private void Ended() => UiThread.Post(() =>
    {
        if (game.Scripts == this)
        {
            game.Scripts = null;
        }
        End.End();
        IdeErrors.Observe(Connection.DisposeAsync().AsTask());
    });

    /// <summary>The game ended, its connection with it: its session hears the end, but of a game this adapter launched, which
    /// it tells with its exit code once the game's process has gone and what it wrote has been told.</summary>
    internal void GameEnded()
    {
        stackAsked?.TrySetException(new LiveTree.LiveAppException($"{game.Name} has ended"));
        variablesAsked?.TrySetException(new LiveTree.LiveAppException($"{game.Name} has ended"));
        evaluationAsked?.TrySetException(new LiveTree.LiveAppException($"{game.Name} has ended"));
        stop = null;
        if (Launched is null)
        {
            Tell(DapEvents.Terminated, new TerminatedEvent());
        }
    }

    /// <summary>Whether an error the game raised is this debugger's own doing, kept out of Problems: one an evaluation raised,
    /// or Godot's word that a frame asked of is past its stack.</summary>
    internal bool Claims(GodotError error)
    {
        if (evaluationErrors is { } raised)
        {
            raised.Add(error);
            return true;
        }
        return error.Function.StartsWith("debug_get_stack_level_", StringComparison.Ordinal);
    }

    /// <summary>A message of the game's debugger: a stop, its end, or an answer to a question asked.</summary>
    internal void Heard(GodotMessage message)
    {
        switch (message.Name)
        {
            case GodotWords.DebugEnter when GodotBreak.Read(message.Data) is { } stopped && !configured:
                early = stopped;
                break;
            case GodotWords.DebugEnter when GodotBreak.Read(message.Data) is { } stopped:
                IdeErrors.Observe(OnBreakAsync(stopped));
                break;
            case GodotWords.DebugExit when stop is { } gone && gone.Thread == message.Thread:
                // Gone on with no continue or step of the session's.
                Left();
                Tell(DapEvents.Continued, new ContinuedEvent((int)gone.Thread));
                break;
            case GodotWords.StackDump:
                stackAsked?.TrySetResult(GodotScriptFrame.Stack(message.Data) ?? []);
                break;
            case GodotWords.StackFrameVars when GodotScriptVariable.Count(message.Data) is int count && variablesAsked is { } asked:
                variablesHeard = [];
                variablesExpected = count;
                if (count == 0)
                {
                    asked.TrySetResult([]);
                }
                break;
            case GodotWords.StackFrameVar when variablesHeard is { } heard && variablesAsked is { } asked:
                if (GodotScriptVariable.Read(message.Data) is { } variable)
                {
                    heard.Add(variable);
                }
                if (--variablesExpected <= 0)
                {
                    asked.TrySetResult(heard);
                }
                break;
            case GodotWords.EvaluationReturn when GodotScriptVariable.Read(message.Data) is { } answer:
                evaluationAsked?.TrySetResult(answer);
                break;
        }
    }

    /// <summary>A stop of the game's: told to the session as a step's end, a pause, an error or a breakpoint, where its hit
    /// stops as every debugger's does (a tracepoint logs and goes on); else the game goes on unseen. One whose
    /// stack the game does not say is told as a pause, saying so, the game held there until the session lets it go.</summary>
    private async Task OnBreakAsync(GodotBreak stopped)
    {
        if (letGo)
        {
            await game.SendToAsync(GodotWords.Continue, stopped.Thread);
            return;
        }
        Left();
        stop = stopped;
        generation++;
        Going was = going;
        going = Going.None;
        if (!stopped.HasStack)
        {
            Stopped(StopReasons.Pause, null, null);
            return;
        }
        IReadOnlyList<GodotScriptFrame> stack;
        try
        {
            stack = await FramesAsync();
        }
        catch (LiveTree.LiveAppException)
        {
            return;
        }
        catch (DapException unanswered)
        {
            if (stop == stopped && game.State == LiveTree.LiveAppState.Connected)
            {
                Say(unanswered.Reason);
                Stopped(StopReasons.Pause, null, null);
            }
            return;
        }
        if (stopped.IsError)
        {
            if (errors is not { } watched || await HitStopsAsync(watched, stopped, "the breakpoint on GDScript errors", () => BreakpointHits.ExceptionReached("GDScript error"), _ => { }))
            {
                Stopped(StopReasons.Exception, stopped.Error, null);
            }
            return;
        }
        if (was != Going.None)
        {
            Stopped(was == Going.Stepping ? StopReasons.Step : StopReasons.Pause, null, null);
            return;
        }
        if (stack.FirstOrDefault() is not { } top || wanted.GetValueOrDefault(top.File)?.FirstOrDefault(w => w.Request.Line == top.Line) is not { } hit)
        {
            // GDScript's own breakpoint statement.
            Stopped(StopReasons.Breakpoint, null, null);
            return;
        }
        if (await HitStopsAsync(hit, stopped, "the breakpoint", () => BreakpointHits.Reached(top.File, top.Line), told => Told(told, game.PathOf(top.File) ?? top.File, top.Line)))
        {
            Stopped(StopReasons.Breakpoint, null, [hit.Id]);
        }
    }

    /// <summary>Whether a hit of a breakpoint stops the game, tried as every debugger tries one (<see cref="BreakpointHits.StopsAsync"/>),
    /// its condition read as the game's own <c>if</c> reads it and its message read in the stop's frame. Where it does not,
    /// the game goes on.</summary>
    private async Task<bool> HitStopsAsync(IHitBreakpoint breakpoint, GodotBreak stopped, string named, Func<string> reached, Action<IHitBreakpoint> told)
    {
        if (await BreakpointHits.StopsAsync(breakpoint, new HitReading
        {
            Named = named,
            Holds = HoldsAsync,
            Logged = message => BreakpointHits.LoggedAsync(message, ShownAsync),
            Reached = reached,
            Say = Say,
            Told = told,
        }))
        {
            return true;
        }
        Left();
        await game.SendToAsync(GodotWords.Continue, stopped.Thread);
        return false;
    }

    /// <summary>Whether a condition holds in the stop's frame, as the game's own <c>if</c> reads its value, or why it could
    /// not be told.</summary>
    private async Task<ConditionRead> HoldsAsync(string condition)
    {
        try
        {
            return new ConditionRead((await ReadAsync(GodotScriptBreakpoints.Truth(condition), 0)).Value is true);
        }
        catch (DapException unread)
        {
            return new ConditionRead(true, unread.Reason);
        }
    }

    /// <summary>An expression of a tracepoint's message as read in the stop's frame, or why it could not be read.</summary>
    private async Task<string> ShownAsync(string expression)
    {
        try
        {
            object? value = (await ReadAsync(expression, 0)).Value;
            return value is string words ? words : GodotScriptValues.Shown(value);
        }
        catch (DapException unread)
        {
            return $"<{unread.Reason}>";
        }
    }

    private void Stopped(string reason, string? text, IReadOnlyList<int>? hit) =>
        Stopped(reason, text, hit, (int)stop!.Thread, allThreads: false, stop.CanStep ? null : NoStepFromError);

    /// <summary>The stop is over: what was read of it goes.</summary>
    protected override void Left()
    {
        stop = null;
        frames = null;
        variables.Clear();
        settable.Clear();
        base.Left();
    }

    private async Task<IReadOnlyList<GodotScriptFrame>> FramesAsync()
    {
        if (frames is { } known)
        {
            return known;
        }
        GodotBreak stopped = stop ?? throw new LiveTree.LiveAppException($"{game.Name} went on");
        int at = generation;
        await asking.WaitAsync();
        try
        {
            stackAsked = Question<IReadOnlyList<GodotScriptFrame>>();
            Task<IReadOnlyList<GodotScriptFrame>> answered = stackAsked.Task;
            await game.SendToAsync(GodotWords.GetStackDump, stopped.Thread);
            IReadOnlyList<GodotScriptFrame> stack = await Answer(answered, "with its stack");
            if (at == generation && stop == stopped)
            {
                frames = stack;
            }
            return stack;
        }
        finally
        {
            stackAsked = null;
            asking.Release();
        }
    }

    private async Task<IReadOnlyList<GodotScriptVariable>> VariablesOfAsync(int level)
    {
        if (variables.TryGetValue(level, out IReadOnlyList<GodotScriptVariable>? known))
        {
            return known;
        }
        GodotBreak stopped = stop ?? throw new DapException(DapCommands.Variables, "The game went on");
        int at = generation;
        await asking.WaitAsync();
        try
        {
            variablesAsked = Question<IReadOnlyList<GodotScriptVariable>>();
            variablesHeard = null;
            Task<IReadOnlyList<GodotScriptVariable>> answered = variablesAsked.Task;
            await game.SendToAsync(GodotWords.GetStackFrameVars, stopped.Thread, (long)level);
            IReadOnlyList<GodotScriptVariable> read = await Answer(answered, "with the frame's values");
            if (at == generation)
            {
                variables[level] = read;
            }
            return read;
        }
        finally
        {
            variablesAsked = null;
            variablesHeard = null;
            asking.Release();
        }
    }

    /// <summary>A question for the game, its answer to come by name. Its fault as the game ends is heard only by what awaits
    /// it: one given up on (past its limit, or asked behind another that failed) is no one's to hear.</summary>
    private static TaskCompletionSource<T> Question<T>()
    {
        TaskCompletionSource<T> asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UnawaitedWork.Dismiss(asked.Task);
        return asked;
    }

    /// <summary>A question's answer, or a refusal saying the game gave none.</summary>
    private async Task<T> Answer<T>(Task<T> answer, string what)
    {
        try
        {
            return await answer.WaitAsync(Waits.Of(LiveTree.LiveApp.AnswerLimit));
        }
        catch (TimeoutException)
        {
            throw new DapException("godot", $"{game.Name} did not answer {what} in {LiveTree.LiveApp.AnswerLimit.TotalSeconds:0} s");
        }
        catch (LiveTree.LiveAppException gone)
        {
            throw new DapException("godot", gone.Message);
        }
    }

    private async Task SendAsync(string name, params object?[] data)
    {
        try
        {
            await game.SendToAsync(name, GodotMessage.MainThread, data);
        }
        catch (LiveTree.LiveAppException lost)
        {
            throw new DapException(name, lost.Message);
        }
    }
}
