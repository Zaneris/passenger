using System.Text.Json.Serialization;

namespace Passenger.Dap;

// The Debug Adapter Protocol, as much of it as Passenger's debugger speaks, in the protocol's own field
// names (serialized camelCase). Types the protocol names after things that are everywhere in .NET
// (Thread, StackFrame, Source, Module, Breakpoint) carry Dap in front. Lines and columns count from one,
// which the client asks for at initialize. A few fields are this debugger's own, and say so.

public sealed record InitializeArguments
{
    public string? ClientId { get; init; }
    public string? ClientName { get; init; }
    public string AdapterId { get; init; } = "coreclr";
    public string? Locale { get; init; }
    public bool? LinesStartAt1 { get; init; }
    public bool? ColumnsStartAt1 { get; init; }
    public string? PathFormat { get; init; }
    public bool? SupportsVariableType { get; init; }
    public bool? SupportsVariablePaging { get; init; }
    public bool? SupportsRunInTerminalRequest { get; init; }
}

public sealed record DapCapabilities
{
    public bool? SupportsConfigurationDoneRequest { get; init; }
    public bool? SupportsFunctionBreakpoints { get; init; }
    public bool? SupportsConditionalBreakpoints { get; init; }
    public bool? SupportsHitConditionalBreakpoints { get; init; }
    public bool? SupportsLogPoints { get; init; }
    public bool? SupportsEvaluateForHovers { get; init; }
    public bool? SupportsStepBack { get; init; }
    public bool? SupportsSetVariable { get; init; }
    public bool? SupportsSetExpression { get; init; }
    public bool? SupportsRestartFrame { get; init; }
    public bool? SupportsTerminateRequest { get; init; }
    public bool? SupportsExceptionInfoRequest { get; init; }
    public bool? SupportsExceptionFilterOptions { get; init; }
    public bool? SupportsExceptionOptions { get; init; }
    public bool? SupportsStepInTargetsRequest { get; init; }
    public bool? SupportsGotoTargetsRequest { get; init; }
    public bool? SupportsDataBreakpoints { get; init; }

    /// <summary>Passenger's own: why the debugger can set no data breakpoint where it runs, when it cannot.</summary>
    public string? DataBreakpointsUnavailable { get; init; }
    public IReadOnlyList<DapExceptionFilter>? ExceptionBreakpointFilters { get; init; }
}

public sealed record DapExceptionFilter(string Filter, string Label, bool? Default = null, bool? SupportsCondition = null);

/// <summary>The launch configuration a .NET adapter takes: a program (an apphost or a .dll, which runs
/// under <c>dotnet</c>), its arguments, folder and environment.</summary>
public sealed record LaunchArguments
{
    public string? Program { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public string? Cwd { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public bool? StopAtEntry { get; init; }
    public bool? JustMyCode { get; init; }

    /// <summary>internalConsole (the output comes as events), integratedTerminal or externalTerminal.</summary>
    public string? Console { get; init; }

    /// <summary>Passenger's own: the program's code may be changed as it runs (Hot Reload, applyChanges). The runtime is
    /// told DOTNET_MODIFIABLE_ASSEMBLIES=debug and each of the user's modules gets Edit and Continue as it loads.</summary>
    public bool? HotReload { get; init; }
}

public sealed record AttachArguments
{
    public int? ProcessId { get; init; }
    public bool? JustMyCode { get; init; }

    /// <summary>Passenger's own: the program was started held with DOTNET_MODIFIABLE_ASSEMBLIES=debug, so each of the
    /// user's modules, loading after the attach, gets Edit and Continue (Hot Reload, applyChanges).</summary>
    public bool? HotReload { get; init; }
}

public sealed record DapSource
{
    public string? Name { get; init; }
    public string? Path { get; init; }
    public int? SourceReference { get; init; }
}

/// <summary>A line breakpoint as asked for. <c>Suspend</c> is Passenger's own: whether a hit stops the program, so a
/// breakpoint can log and stop, or neither stop nor log a message of its own ("Breakpoint reached"); without it,
/// a log message alone means no stop, as the protocol has it.</summary>
public sealed record DapSourceBreakpoint(int Line, int? Column = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool? Suspend = null);

public sealed record SetBreakpointsArguments(DapSource Source, IReadOnlyList<DapSourceBreakpoint>? Breakpoints);

public sealed record SetBreakpointsResponse(IReadOnlyList<DapBreakpoint> Breakpoints);

public sealed record DapBreakpoint
{
    public int? Id { get; init; }
    public bool Verified { get; init; }
    public string? Message { get; init; }

    /// <summary>Why one is not verified: "failed" for one the debugger refused (and will not take as it is), "pending" for
    /// one that may yet bind.</summary>
    public string? Reason { get; init; }
    public DapSource? Source { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public int? Offset { get; init; }
}

/// <summary>A function breakpoint as asked for; <c>Suspend</c> is Passenger's own, as a line breakpoint's is.</summary>
public sealed record DapFunctionBreakpoint(string Name, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool? Suspend = null);

public sealed record SetFunctionBreakpointsArguments(IReadOnlyList<DapFunctionBreakpoint>? Breakpoints);

/// <summary>Whether a data breakpoint can watch a member of a container (an object's field or property, an array's element)
/// by the container's variables reference and the member's name as the variables request named it.</summary>
public sealed record DataBreakpointInfoArguments(string Name, int? VariablesReference = null, int? FrameId = null);

public sealed record DataBreakpointInfoResponse
{
    /// <summary>What a data breakpoint on it would watch, or null when none can: <see cref="Description"/> says why.</summary>
    public string? DataId { get; init; }

    /// <summary>What it is, or why no data breakpoint can watch it.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>read, write or readWrite: this debugger stops at a write alone.</summary>
    public IReadOnlyList<string>? AccessTypes { get; init; }

    /// <summary>Whether it could outlive the session: a member of one object never can.</summary>
    public bool? CanPersist { get; init; }
}

/// <summary>A data breakpoint as asked for: the data it watches (a dataBreakpointInfo's dataId), a condition and hit count, and,
/// Passenger's own as on a line breakpoint, a message to log and whether a hit stops the program.</summary>
public sealed record DapDataBreakpoint(string DataId, string? AccessType = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool? Suspend = null);

/// <summary>Every data breakpoint there is to be: those not named any more are let go.</summary>
public sealed record SetDataBreakpointsArguments(IReadOnlyList<DapDataBreakpoint>? Breakpoints);

/// <summary>What the debugger made of each data breakpoint, in the order asked.</summary>
public sealed record SetDataBreakpointsResponse(IReadOnlyList<DapBreakpoint> Breakpoints);

/// <summary>The exception breakpoints: filters, filters with a condition (a list of types, ! before it for every type
/// but those), and exception options, one for each CLR exception breakpoint. A client that sends exception options,
/// even none, has an unhandled exception stop only where one of them says so; without, every unhandled exception
/// stops.</summary>
public sealed record SetExceptionBreakpointsArguments(
    IReadOnlyList<string>? Filters, IReadOnlyList<DapExceptionFilterOptions>? FilterOptions = null, IReadOnlyList<DapExceptionOptions>? ExceptionOptions = null);

public sealed record DapExceptionFilterOptions(string FilterId, string? Condition = null);

/// <summary>Exceptions by their path ("CLR", then the types' full names; "CLR" alone is every exception) and when to
/// stop (never, always, unhandled or userUnhandled). The rest is Passenger's own: scenarios in place of the break mode,
/// and a condition, hit count and log message as a line breakpoint has.</summary>
public sealed record DapExceptionOptions(IReadOnlyList<DapExceptionPathSegment>? Path, string BreakMode)
{
    /// <summary>Where the exception was thrown and where it is handled, by <see cref="ExceptionScenarioNames"/>.</summary>
    public IReadOnlyList<string>? Scenarios { get; init; }

    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }

    /// <summary>Whether a hit stops the program, as a line breakpoint's <c>suspend</c>.</summary>
    public bool? Suspend { get; init; }
}

public sealed record DapExceptionPathSegment(IReadOnlyList<string> Names, bool? Negate = null);

/// <summary>What the debugger made of each filter, filter option and exception option, in that order.</summary>
public sealed record SetExceptionBreakpointsResponse(IReadOnlyList<DapBreakpoint>? Breakpoints = null);

/// <summary>When a CLR exception breakpoint stops: where the exception was thrown (the user's code or code outside it)
/// and where it is handled (the user's code, code outside it, or nowhere). A breakpoint stops when one of each
/// holds.</summary>
[Flags]
public enum ExceptionScenarios
{
    None = 0,
    ThrownInUserCode = 1,
    ThrownInExternalCode = 2,
    HandledInUserCode = 4,
    HandledInExternalCode = 8,
    Unhandled = 16,
    Thrown = ThrownInUserCode | ThrownInExternalCode,
    All = Thrown | HandledInUserCode | HandledInExternalCode | Unhandled,
}

/// <summary>The scenarios as the protocol and an agent name them.</summary>
public static class ExceptionScenarioNames
{
    private static readonly (ExceptionScenarios Scenario, string Name)[] Names =
    [
        (ExceptionScenarios.ThrownInUserCode, "thrownInUserCode"),
        (ExceptionScenarios.ThrownInExternalCode, "thrownInExternalCode"),
        (ExceptionScenarios.HandledInUserCode, "handledInUserCode"),
        (ExceptionScenarios.HandledInExternalCode, "handledInExternalCode"),
        (ExceptionScenarios.Unhandled, "unhandled"),
    ];

    public static IReadOnlyList<string> All { get; } = [.. Names.Select(n => n.Name)];

    public static IReadOnlyList<string> Of(ExceptionScenarios scenarios) => [.. Names.Where(n => scenarios.HasFlag(n.Scenario)).Select(n => n.Name)];

    /// <summary>The scenarios named, or the name that is none of them.</summary>
    public static ExceptionScenarios Parse(IEnumerable<string> names)
    {
        ExceptionScenarios scenarios = ExceptionScenarios.None;
        foreach (string name in names)
        {
            scenarios |= Names.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } known
                ? known.Scenario
                : throw new ArgumentException($"No exception scenario '{name}': they are {string.Join(", ", All)}");
        }
        return scenarios;
    }

    /// <summary>A break mode's scenarios: always is every one, userUnhandled those handled outside the user's code or
    /// not at all, unhandled only the last.</summary>
    public static ExceptionScenarios OfBreakMode(string breakMode) => breakMode switch
    {
        "always" => ExceptionScenarios.All,
        "userUnhandled" => ExceptionScenarios.Thrown | ExceptionScenarios.HandledInExternalCode | ExceptionScenarios.Unhandled,
        "unhandled" => ExceptionScenarios.Thrown | ExceptionScenarios.Unhandled,
        "never" => ExceptionScenarios.None,
        _ => throw new ArgumentException($"No exception break mode '{breakMode}': it is never, always, unhandled or userUnhandled"),
    };
}

public sealed record ThreadsResponse(IReadOnlyList<DapThread> Threads);

public sealed record DapThread(int Id, string Name)
{
    /// <summary>Passenger's own: true for the thread that runs Main (the first the runtime told of, or on a launch on Windows
    /// the one the program was started on), whatever the program named it; a desktop app's UI thread.</summary>
    public bool? IsMain { get; init; }
}

public sealed record StackTraceArguments(int ThreadId, int? StartFrame = null, int? Levels = null);

public sealed record StackTraceResponse(IReadOnlyList<DapStackFrame> StackFrames, int? TotalFrames = null);

public sealed record DapStackFrame
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DapSource? Source { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }

    /// <summary>"subtle" for a frame outside the user's code.</summary>
    public string? PresentationHint { get; init; }

    /// <summary>This debugger's own: false for a frame with no source yet, which resolveStackFrame
    /// decompiles; <see cref="DecompiledSourceInfo"/> says what it would decompile.</summary>
    public bool? IsResolved { get; init; }

    public DapDecompiledSource? DecompiledSourceInfo { get; init; }
}

public sealed record DapDecompiledSource(string TypeFullName, DapAssembly Assembly, string CallingUserCodeAssemblyPath);

public sealed record DapAssembly(string AssemblyPath, Guid Mvid);

/// <summary>This debugger's own request: a frame's source decompiled, for a frame without one.</summary>
public sealed record ResolveStackFrameArguments(int StackFrameId);

public sealed record ResolveStackFrameResponse(DapStackFrame StackFrame);

public sealed record ScopesArguments(int FrameId);

public sealed record ScopesResponse(IReadOnlyList<DapScope> Scopes);

public sealed record DapScope(string Name, int VariablesReference, bool Expensive);

public sealed record VariablesArguments(int VariablesReference);

public sealed record VariablesResponse(IReadOnlyList<DapVariable> Variables);

public sealed record DapVariable
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    [JsonConverter(typeof(SharedNames))]
    public string? Type { get; init; }
    public string? EvaluateName { get; init; }
    public int VariablesReference { get; init; }
    public DapPresentationHint? PresentationHint { get; init; }
}

/// <summary>How a variable shows: its kind ("virtual" for a row the adapter adds), its attributes, and whether it is lazy, its
/// value read only as it is opened (its reference then names one variable, the value in its place).</summary>
public sealed record DapPresentationHint(string? Kind = null, IReadOnlyList<string>? Attributes = null, string? Visibility = null, bool? Lazy = null);

public sealed record EvaluateArguments(string Expression, int? FrameId = null, string? Context = null);

public sealed record EvaluateResponse
{
    public string Result { get; init; } = string.Empty;
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
    public DapPresentationHint? PresentationHint { get; init; }
}

/// <summary>Where a paused thread could go on from in a source at a line (Set Next Statement's question).</summary>
public sealed record GotoTargetsArguments(DapSource Source, int Line, int? Column = null);

public sealed record GotoTarget(int Id, string Label, int Line, int? Column = null);

public sealed record GotoTargetsResponse
{
    public IReadOnlyList<GotoTarget> Targets { get; init; } = [];
}

/// <summary>Set Next Statement: the thread goes on from a target gotoTargets gave.</summary>
public sealed record GotoArguments(int ThreadId, int TargetId);

/// <summary>Assign a value to what an expression names in a frame (a local, a member, an element): Set Value.</summary>
public sealed record SetExpressionArguments(string Expression, string Value, int? FrameId = null);

public sealed record SetExpressionResponse
{
    public string Value { get; init; } = string.Empty;
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
}

public sealed record ThreadArguments(int ThreadId);

/// <summary>Step Into: the thread, and the call to step straight into (a target stepInTargets gave), Smart Step Into's.</summary>
public sealed record StepInArguments(int ThreadId, int? TargetId = null);

/// <summary>The calls a frame's statement can step into, in the order they run (Smart Step Into's question).</summary>
public sealed record StepInTargetsArguments(int FrameId);

public sealed record StepInTargetsResponse
{
    public IReadOnlyList<StepInTarget> Targets { get; init; } = [];
}

/// <summary>A call a step can go straight into: its label, and where it is written, when the debugger can say.</summary>
public sealed record StepInTarget
{
    public int Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}

public sealed record ContinueResponse(bool? AllThreadsContinued = null);

/// <summary>A disconnect; Passenger's own <paramref name="LetGoWithin"/> is how long, in milliseconds, the client waits
/// for a program let go (not terminated) to be run off its stop and detached, which the adapter keeps its let-go
/// within.</summary>
public sealed record DisconnectArguments(bool? Restart = null, bool? TerminateDebuggee = null, int? LetGoWithin = null);

public sealed record TerminateArguments(bool? Restart = null);

public sealed record ExceptionInfoResponse
{
    public string ExceptionId { get; init; } = string.Empty;
    public string? Description { get; init; }

    /// <summary>never, always, unhandled or userUnhandled.</summary>
    public string BreakMode { get; init; } = "unhandled";

    /// <summary>The exception's HRESULT.</summary>
    public int? Code { get; init; }

    public DapExceptionDetails? Details { get; init; }
}

public sealed record DapExceptionDetails
{
    public string? Message { get; init; }
    public string? TypeName { get; init; }
    public string? FullTypeName { get; init; }
    public string? EvaluateName { get; init; }
    public string? StackTrace { get; init; }
    public IReadOnlyList<DapExceptionDetails>? InnerException { get; init; }
    public string? FormattedDescription { get; init; }
    public int? HResult { get; init; }
    public string? Source { get; init; }
}

/// <summary>The adapter's request to the client, to start the program in a terminal.</summary>
public sealed record RunInTerminalArguments(string Kind, string Title, string? Cwd, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env);

public sealed record RunInTerminalResponse(int? ProcessId = null, int? ShellProcessId = null);

/// <summary>This debugger's own request: text for the launched program's standard input, and whether
/// its input then ends.</summary>
public sealed record InputArguments(string Text, bool? Close = null);

/// <summary>An empty body: requests with no arguments, responses with nothing to say.</summary>
public sealed record DapEmpty;

// Events

public sealed record StoppedEvent
{
    /// <summary>step, breakpoint, exception, pause, entry, goto, function breakpoint, data breakpoint.</summary>
    public string Reason { get; init; } = StopReasons.Unknown;
    public string? Description { get; init; }

    /// <summary>More on the stop: at a data breakpoint, the value it watches before and after the write.</summary>
    public string? Text { get; init; }
    public int? ThreadId { get; init; }
    public bool? AllThreadsStopped { get; init; }
    public IReadOnlyList<int>? HitBreakpointIds { get; init; }

    /// <summary>This debugger's own: where it stopped, so the client need not ask before showing it.</summary>
    public DapSource? Source { get; init; }

    public int? Line { get; init; }
    public int? Column { get; init; }
}

public sealed record ExitedEvent(int ExitCode);

public sealed record TerminatedEvent(bool? Restart = null);

/// <summary>A thread started or exited.</summary>
public sealed record ThreadEvent(string Reason, int ThreadId);

/// <summary>Output, of category stdout, stderr or console.</summary>
public sealed record OutputEvent(string Output, string? Category = null);

/// <summary>A breakpoint changed (bound, or moved), new or removed.</summary>
public sealed record BreakpointEvent
{
    public string Reason { get; init; } = "changed";
    public DapBreakpoint Breakpoint { get; init; } = new();
}

public sealed record ModuleEvent(string Reason, DapModule Module);

public sealed record DapModule(string Id, string Name, string? Path = null);

public sealed record ProcessEvent(string Name, int? SystemProcessId = null, bool? IsLocalProcess = null, string? StartMethod = null);

/// <summary>This debugger's own request for what Hot Reload needs of the program: <paramref name="Hold"/> stops a running
/// program for the update being made, until <see cref="DapCommands.HotReloadRelease"/> lets it go.</summary>
public sealed record HotReloadStateArguments(bool? Hold = null);

/// <summary>This debugger's own request: what Hot Reload needs of the program. The edits its runtime can take, as its own
/// CoreLib names them (System.Reflection.Metadata.MetadataUpdater's capabilities), or why they are unknown; the modules it
/// has loaded, each with whether the runtime lets it be edited; and, paused or held, the statements its threads are in.</summary>
public sealed record HotReloadStateResponse
{
    /// <summary>Whether the program, running, is now held for the update, until it is let go.</summary>
    public bool Held { get; init; }

    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Why the runtime's capabilities could not be read, when they could not.</summary>
    public string? CapabilitiesUnknown { get; init; }

    public IReadOnlyList<DapHotReloadModule> Modules { get; init; } = [];

    public IReadOnlyList<DapActiveStatement> ActiveStatements { get; init; } = [];
}

/// <summary>A module loaded: its MVID, name and file, and whether its code can be edited (the runtime's Edit and Continue on
/// for it), else why not.</summary>
public sealed record DapHotReloadModule(Guid Mvid, string Name, string? Path, bool Editable, string? WhyNot = null);

/// <summary>A statement a thread is in: its method (module, token, the version of its code the frame runs), the IL offset,
/// its sequence point's document and place (lines and columns from zero), and Roslyn's ActiveStatementFlags.</summary>
public sealed record DapActiveStatement(Guid Module, int Token, int Version, int ILOffset, string? Document,
    int StartLine, int StartColumn, int EndLine, int EndColumn, int Flags);

/// <summary>This debugger's own request: the updates the language's Edit and Continue made, applied to the program, paused
/// or running (a running one is stopped for it and goes on).</summary>
public sealed record ApplyChangesArguments(IReadOnlyList<DapModuleUpdate> Updates);

/// <summary>One module's update: the metadata, IL and PDB deltas, the methods and types it changes, the lines that moved in
/// each document (a line from zero, from which on the lines move to the new one's), where each active statement went, and
/// the first line it changed in each document it edits.</summary>
public sealed record DapModuleUpdate(Guid Module, string Name, byte[] Metadata, byte[] IL, byte[] Pdb,
    IReadOnlyList<int> UpdatedMethods, IReadOnlyList<int> UpdatedTypes, IReadOnlyList<DapLineUpdates> Lines,
    IReadOnlyList<DapStatementUpdate> ActiveStatements, IReadOnlyList<DapFirstChange> FirstChanges);

/// <summary>A document an update edits and the first of its lines the edit changed (from zero), as it read before.</summary>
public sealed record DapFirstChange(string Document, int Line);

public sealed record DapLineUpdates(string Document, IReadOnlyList<DapLineUpdate> Updates);

public sealed record DapLineUpdate(int OldLine, int NewLine);

/// <summary>Where an active statement of a method's version is in the new version's source (lines and columns from zero).</summary>
public sealed record DapStatementUpdate(int Token, int Version, int ILOffset, int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>What became of each module's update, in the order given: applied, or why not. Once one fails, the program
/// may hold part of it, so Hot Reload stops for the session.</summary>
public sealed record ApplyChangesResponse(IReadOnlyList<DapModuleApplied> Modules);

public sealed record DapModuleApplied(Guid Module, string Name, bool Applied, string? Message = null);
