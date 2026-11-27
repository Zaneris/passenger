using System.Text.Json.Serialization;
using Passenger.Core;

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

    /// <summary>Passenger's own: whether the debugger marks objects (<see cref="DapCommands.MarkObject"/>).</summary>
    public bool? SupportsMarkObject { get; init; }

    /// <summary>Passenger's own: whether the debugger forces a step or a continue (<see cref="StepInArguments.Force"/>),
    /// stepping without Just My Code and passing every breakpoint by but Run to Cursor's.</summary>
    public bool? SupportsForcedSteps { get; init; }
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

    /// <summary>Where the program reads and writes (<see cref="ConsoleKinds"/>), the debug console where none is given.</summary>
    [IdentifierOf(typeof(ConsoleKinds))]
    public string? Console { get; init; }

    /// <summary>Passenger's own: the program's code may be changed as it runs (Hot Reload, applyChanges). The runtime is
    /// told DOTNET_MODIFIABLE_ASSEMBLIES=debug and each of the user's modules gets Edit and Continue as it loads.</summary>
    public bool? HotReload { get; init; }

    /// <summary>Passenger's own: how the values of types that carry no debugger attributes of their own are shown.</summary>
    public IReadOnlyList<DapTypePresentation>? Presentations { get; init; }

    /// <summary>Passenger's own: the assemblies, by name, that Passenger carries into the program (its inspectors, its Hot
    /// Reload and test hooks), which the debugger never takes for the user's code, however they were built.</summary>
    public IReadOnlyList<string>? Carried { get; init; }

    /// <summary>Passenger's own: the folders whose source the user writes (the open folder and the projects lent beside it): a
    /// module whose symbols name a file under one of them is the user's code however it was built, a Release build's
    /// compiled unoptimized as it loads where the runtime still lets it be, so steps stop in it as in a Debug build.</summary>
    public IReadOnlyList<string>? Own { get; init; }

    /// <summary>Passenger's own: no one is to see the program's windows (an agent's Godot game, drawn for its frames, or a
    /// desktop app a Passenger nobody sees runs): on Windows it runs on a desktop of its own, which nobody switches to.</summary>
    public bool? Unseen { get; init; }
}

public sealed record AttachArguments
{
    public int? ProcessId { get; init; }
    public bool? JustMyCode { get; init; }

    /// <summary>Passenger's own: the program was started held with DOTNET_MODIFIABLE_ASSEMBLIES=debug, so each of the
    /// user's modules, loading after the attach, gets Edit and Continue (Hot Reload, applyChanges).</summary>
    public bool? HotReload { get; init; }

    /// <summary>Passenger's own: how the values of types that carry no debugger attributes of their own are shown.</summary>
    public IReadOnlyList<DapTypePresentation>? Presentations { get; init; }

    /// <summary>Passenger's own: the assemblies, by name, that Passenger carries into the program (its inspectors, its Hot
    /// Reload and test hooks), which the debugger never takes for the user's code, however they were built.</summary>
    public IReadOnlyList<string>? Carried { get; init; }

    /// <summary>Passenger's own: the folders whose source the user writes (the open folder and the projects lent beside it): a
    /// module whose symbols name a file under one of them is the user's code however it was built, a Release build's
    /// compiled unoptimized as it loads where the runtime still lets it be, so steps stop in it as in a Debug build.</summary>
    public IReadOnlyList<string>? Own { get; init; }

    /// <summary>Passenger's own: an app on a device or simulator, reached through Passenger's relay inside it at this
    /// host:port, in place of <see cref="ProcessId"/>.</summary>
    public string? Relay { get; init; }

    /// <summary>Passenger's own: the host's folder of the assemblies (and their PDBs) the app on the device loads.</summary>
    public string? RelayModules { get; init; }

    /// <summary>Passenger's own: the token the relay at <see cref="Relay"/> takes a client by, which the app's environment
    /// gave it.</summary>
    public string? RelayToken { get; init; }
}

/// <summary>Passenger's own: how the debugger shows the values of a type (<see cref="Type"/>, its full name, declared in
/// <see cref="Assembly"/>) and of the types deriving from it that carry no [DebuggerDisplay] of their own, as a client
/// that knows a library tells it. Its value is <see cref="Value"/>, a [DebuggerDisplay] format on it. A value with
/// <see cref="Rows"/> opens to the members the types deriving from it declare, those carrying the attribute
/// <see cref="First"/> names first, then its rows, its type's own members and its bases' under Raw View; one without
/// opens to its members.</summary>
public sealed record DapTypePresentation
{
    public required string Type { get; init; }

    /// <summary>The name of the assembly that declares the type.</summary>
    public required string Assembly { get; init; }

    public string? Value { get; init; }

    /// <summary>The rows it opens to after its deriving types' members, each a name and an expression on the value.</summary>
    public IReadOnlyList<DapPresentedRow>? Rows { get; init; }

    /// <summary>The full name of an attribute whose members are listed first.</summary>
    public string? First { get; init; }
}

/// <summary>A row a <see cref="DapTypePresentation"/> opens its value to: its name and the C# on the value it shows.</summary>
public sealed record DapPresentedRow(string Name, string Expression);

public sealed record DapSource
{
    public string? Name { get; init; }
    public string? Path { get; init; }
    public int? SourceReference { get; init; }
}

/// <summary>A line breakpoint as asked for. <c>Suspend</c> is Passenger's own: whether a hit stops the program, so a
/// breakpoint can log and stop, or neither stop nor log a message of its own ("Breakpoint reached"); without it,
/// a log message alone means no stop, as the protocol has it. <c>RunTo</c> is Passenger's own too: Run to Cursor's line,
/// whose next hit stops whatever the condition, hit count and Suspend say, tried as the breakpoint's own hit all the same.
/// So are <c>Key</c>, <c>WaitFor</c>, <c>Temporary</c>, <c>Snapshot</c>, <c>Captures</c> and <c>CaptureLimit</c>
/// (<see cref="IHitBreakpoint"/>).</summary>
public sealed record DapSourceBreakpoint(int Line, int? Column = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool? Suspend = null,
    bool? RunTo = null, int? Key = null, int? WaitFor = null, bool? Temporary = null, bool? Snapshot = null, string? Captures = null, int? CaptureLimit = null) : IAskedBreakpoint;

public sealed record SetBreakpointsArguments(DapSource Source, IReadOnlyList<DapSourceBreakpoint>? Breakpoints);

public sealed record SetBreakpointsResponse(IReadOnlyList<DapBreakpoint> Breakpoints);

public sealed record DapBreakpoint
{
    public int? Id { get; init; }
    public bool Verified { get; init; }
    public string? Message { get; init; }

    /// <summary>Why one is not verified: <see cref="BreakpointReasons.Failed"/> or <see cref="BreakpointReasons.Pending"/>.</summary>
    [IdentifierOf(typeof(BreakpointReasons))]
    public string? Reason { get; init; }
    public DapSource? Source { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public int? Offset { get; init; }
}

/// <summary>A function breakpoint as asked for; <c>Suspend</c>, <c>Key</c>, <c>WaitFor</c>, <c>Temporary</c>,
/// <c>Snapshot</c>, <c>Captures</c> and <c>CaptureLimit</c> are Passenger's own, as a line breakpoint's are.</summary>
public sealed record DapFunctionBreakpoint(string Name, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool? Suspend = null,
    int? Key = null, int? WaitFor = null, bool? Temporary = null, bool? Snapshot = null, string? Captures = null, int? CaptureLimit = null);

public sealed record SetFunctionBreakpointsArguments(IReadOnlyList<DapFunctionBreakpoint>? Breakpoints);

/// <summary>Whether a data breakpoint can watch a member of a container (an object's field or property, an array's element)
/// by the container's variables reference and the member's name as the variables request named it.</summary>
public sealed record DataBreakpointInfoArguments(string Name, int? VariablesReference = null, int? FrameId = null);

/// <summary>Passenger's own request: the object an expression's value is in a frame, marked so that it is known again at every
/// stop and named by its label (<c>$1</c>) in any expression until it is unmarked or collected.</summary>
public sealed record MarkObjectArguments(string Expression, int? FrameId = null);

/// <summary>The label the object was marked with, the one it had where it was marked already.</summary>
public sealed record MarkObjectResponse(string Label);

/// <summary>Passenger's own request: a marked object let go of, its label named by no expression from then on.</summary>
public sealed record UnmarkObjectArguments(string Label);

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

public sealed record DapExceptionFilterOptions(string FilterId, string? Condition = null) : IAskedBreakpoint
{
    /// <summary>Passenger's own: a hit count, a message logged at each hit, and whether a hit stops, as a line breakpoint has.</summary>
    public string? HitCondition { get; init; }

    public string? LogMessage { get; init; }

    public bool? Suspend { get; init; }
}

/// <summary>Exceptions by their path ("CLR", then the types' full names; "CLR" alone is every exception) and when to
/// stop (a <see cref="DapBreakModes"/>). The rest is Passenger's own: scenarios in place of the break mode,
/// and a condition, hit count and log message as a line breakpoint has.</summary>
public sealed record DapExceptionOptions(IReadOnlyList<DapExceptionPathSegment>? Path, [IdentifierOf(typeof(DapBreakModes))] string BreakMode)
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
    public static ExceptionScenarios OfBreakMode([IdentifierOf(typeof(DapBreakModes))] string breakMode) => breakMode switch
    {
        DapBreakModes.Always => ExceptionScenarios.All,
        DapBreakModes.UserUnhandled => ExceptionScenarios.Thrown | ExceptionScenarios.HandledInExternalCode | ExceptionScenarios.Unhandled,
        DapBreakModes.Unhandled => ExceptionScenarios.Thrown | ExceptionScenarios.Unhandled,
        DapBreakModes.Never => ExceptionScenarios.None,
        _ => throw new ArgumentException($"No exception break mode '{breakMode}': it is {DapBreakModes.Never}, {DapBreakModes.Always}, {DapBreakModes.Unhandled} or {DapBreakModes.UserUnhandled}"),
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

    /// <summary><see cref="FrameHints.Subtle"/> for a frame outside the user's code.</summary>
    [IdentifierOf(typeof(FrameHints))]
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

public sealed record VariableTableArguments(int VariablesReference, int Start, int Count, int TableIndex = 0)
{
    public const int PageLimit = 100;
}

public sealed record VariableTableResponse(IReadOnlyList<string> Tables, IReadOnlyList<string> Columns, int RowCount,
    IReadOnlyList<IReadOnlyList<DapVariable>> Rows);

public sealed record DapVariable
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    [JsonConverter(typeof(SharedNames))]
    public string? Type { get; init; }
    public string? EvaluateName { get; init; }
    public int VariablesReference { get; init; }
    public DapPresentationHint? PresentationHint { get; init; }

    /// <summary>Passenger's own: why the debugger cannot set this value (a frame's local Godot's debugger sets none of), where
    /// it cannot though an expression reaches it.</summary>
    public string? Unsettable { get; init; }

    /// <summary>Passenger's own: the label of the marked object this value is (<c>$1</c>); null for any other.</summary>
    public string? Mark { get; init; }
}

/// <summary>How a variable shows: its kind (<see cref="PresentationHintKinds"/>), its attributes (<see cref="DapAttributes"/>),
/// and whether it is lazy, its value read only as it is opened (its reference then names one variable, the value in its
/// place).</summary>
public sealed record DapPresentationHint(
    [IdentifierOf(typeof(PresentationHintKinds))][property: IdentifierOf(typeof(PresentationHintKinds))] string? Kind = null,
    IReadOnlyList<string>? Attributes = null, string? Visibility = null, bool? Lazy = null)
{
    /// <summary>Whether it carries an attribute.</summary>
    public bool Has([IdentifierOf(typeof(DapAttributes))] string attribute) => Attributes?.Contains(attribute) == true;
}

/// <summary>An evaluation; Passenger's own <paramref name="LetThreadsRun"/> runs it with every thread of the program let go,
/// which the person asks for where one with the others held did not end (<see cref="DapAttributes.NeedsThreads"/>).</summary>
public sealed record EvaluateArguments(string Expression, int? FrameId = null,
    [IdentifierOf(typeof(EvaluateContexts))][property: IdentifierOf(typeof(EvaluateContexts))] string? Context = null, bool? LetThreadsRun = null);

public sealed record EvaluateResponse
{
    public string Result { get; init; } = string.Empty;
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
    public DapPresentationHint? PresentationHint { get; init; }

    /// <summary>Passenger's own: what other threads met and passed over while an evaluation ran with every thread let go
    /// (a breakpoint, an exception), said in words; null where they met none.</summary>
    public string? PassedOver { get; init; }

    /// <summary>Passenger's own: why implicit evaluation stays off once an evaluation with every thread let go has ended, in
    /// words; null where it is on, or the others were held.</summary>
    public string? ImplicitOff { get; init; }

    /// <summary>Passenger's own: the label of the marked object the value is; null for any other.</summary>
    public string? Mark { get; init; }
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

/// <summary>Step Into: the thread, and the call to step straight into (a target stepInTargets gave), Smart Step Into's;
/// <paramref name="Force"/> is Passenger's own, Force Step Into's (<see cref="DapCapabilities.SupportsForcedSteps"/>).</summary>
public sealed record StepInArguments(int ThreadId, int? TargetId = null, bool? Force = null);

/// <summary>Step Over and Continue: the thread, and Passenger's own <paramref name="Force"/>, Force Step Over's and Force
/// Run to Cursor's (<see cref="DapCapabilities.SupportsForcedSteps"/>).</summary>
public sealed record GoingArguments(int ThreadId, bool? Force = null);

/// <summary>The calls a frame's statement can step into, in the order they run (Smart Step Into's question), its source read
/// as its project compiles it: with these preprocessor symbols, at this C# version ("12.0", "preview"); with neither, with
/// no symbols at the newest C# the parser reads.</summary>
public sealed record StepInTargetsArguments(int FrameId, IReadOnlyList<string>? PreprocessorSymbols = null, string? LanguageVersion = null);

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

    /// <summary>When the exception stopped the program, by <see cref="DapBreakModes"/>.</summary>
    [IdentifierOf(typeof(DapBreakModes))]
    public string BreakMode { get; init; } = DapBreakModes.Unhandled;

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
public sealed record RunInTerminalArguments([IdentifierOf(typeof(RunInTerminalKinds))][property: IdentifierOf(typeof(RunInTerminalKinds))] string Kind, string Title, string? Cwd, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env);

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

    /// <summary>More on the stop: at a data breakpoint, the value it watches before and after the write; at a stop made of
    /// an event the debugger failed to handle, why.</summary>
    public string? Text { get; init; }
    public int? ThreadId { get; init; }
    public bool? AllThreadsStopped { get; init; }
    public IReadOnlyList<int>? HitBreakpointIds { get; init; }

    /// <summary>This debugger's own: where it stopped, so the client need not ask before showing it.</summary>
    public DapSource? Source { get; init; }

    /// <summary>This debugger's own: why no step goes on from this stop (a Godot script error, which only a continue leaves).</summary>
    public string? NoSteps { get; init; }

    public int? Line { get; init; }
    public int? Column { get; init; }
}

public sealed record ExitedEvent(int ExitCode);

/// <summary>The debugging ended; this debugger's own <paramref name="Failure"/> says why where nothing the client did and
/// no end of the program did it (the runtime's debugging of the program failed).</summary>
public sealed record TerminatedEvent(bool? Restart = null, string? Failure = null);

/// <summary>The program went on from a stop that no continue or step of the client's left.</summary>
public sealed record ContinuedEvent(int ThreadId, bool? AllThreadsContinued = null);

/// <summary>A thread started or exited.</summary>
public sealed record ThreadEvent([IdentifierOf(typeof(EventReasons))][property: IdentifierOf(typeof(EventReasons))] string Reason, int ThreadId);

/// <summary>Output, of category stdout, stderr or console.</summary>
public sealed record OutputEvent(string Output, string? Category = null);

/// <summary>Passenger's own event: a snapshot point's capture, the program's state where one of its threads took the point's
/// hit, read as a stop's frames and values are and copied whole, so it reads alike once the program has run on. The
/// program was held <see cref="Paused"/> for it and then went on; <see cref="Values"/> holds each frame's locals and each
/// row's members read, by the reference that names them (a row's own <see cref="DapVariable.VariablesReference"/>), and
/// <see cref="Asked"/> the point's own expressions, read in its top frame.</summary>
public sealed record SnapshotEvent
{
    /// <summary>The snapshot point, by the id the debugger gave it.</summary>
    public int BreakpointId { get; init; }

    public int ThreadId { get; init; }
    public string? ThreadName { get; init; }

    /// <summary>When it was taken, by the machine's clock.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>How long the program was held for it, from the hit to its going on, in milliseconds.</summary>
    public double PausedMilliseconds { get; init; }

    public IReadOnlyList<DapSnapshotFrame> Frames { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<DapVariable>> Values { get; init; } = new Dictionary<int, IReadOnlyList<DapVariable>>();
    public IReadOnlyList<DapVariable> Asked { get; init; } = [];

    /// <summary>What the capture left unread, where it read less than the frames and values asked of it; null for none.</summary>
    public string? Cut { get; init; }
}

/// <summary>A frame of a snapshot, its locals by their reference among the snapshot's values, 0 where they were not read.</summary>
public sealed record DapSnapshotFrame(DapStackFrame Frame, int Locals);

/// <summary>Passenger's own request: whether the debugger records the program's calls from now on (historical debugging).</summary>
public sealed record RecordArguments(bool On);

/// <summary>Passenger's own request: the recording's moments on a thread, every thread's without one.</summary>
public sealed record TimelineArguments(int? ThreadId = null);

/// <summary>What a recording kept: whether it records now, its moments oldest first, how many older ones it let go to keep
/// within <see cref="MostMoments"/> and <see cref="MostBytes"/> of values, how many it recorded in all, and how long
/// recording them held the program in all, in milliseconds.</summary>
public sealed record TimelineResponse(bool Recording, IReadOnlyList<DapMoment> Moments, int Dropped, int Recorded, double HeldMilliseconds, int MostMoments, long MostBytes);

/// <summary>A recorded moment: its number in the session's recording, what it is (<see cref="DapMomentKinds"/>), its
/// thread, when, its method's frame where it stood, how many recorded calls it ran inside, and what happened, in words.</summary>
public sealed record DapMoment(int Index, [property: IdentifierOf(typeof(DapMomentKinds))] string Kind, int ThreadId, DateTimeOffset At, DapStackFrame Place, int Depth, string Said);

/// <summary>Passenger's own request: a recorded moment, by its number, as a past stop.</summary>
public sealed record MomentArguments(int Index);

/// <summary>Passenger's own request: what a stopped frame's method will do from where it stands.</summary>
public sealed record AheadArguments(int FrameId);

/// <summary>The code a stopped frame's method won't run, from the values it stands with, running none of the program's code
/// (lines and columns from one); the values its locals will take on the lines ahead; and the line from which it could no
/// longer tell, none where it read on to the method's end. Empty for a frame not read (not the top one, or without symbols).</summary>
public sealed record AheadResponse(IReadOnlyList<DapAheadSpan> WontRun, IReadOnlyList<DapAheadValue> Values, int? UnsureFrom);

public sealed record DapAheadSpan(int Line, int Column, int EndLine, int EndColumn);

public sealed record DapAheadValue(int Line, string Name, string Value);

/// <summary>A breakpoint changed (bound, or moved), new or removed.</summary>
public sealed record BreakpointEvent
{
    [IdentifierOf(typeof(EventReasons))]
    public string Reason { get; init; } = EventReasons.Changed;
    public DapBreakpoint Breakpoint { get; init; } = new();
}

public sealed record ModuleEvent([IdentifierOf(typeof(EventReasons))][property: IdentifierOf(typeof(EventReasons))] string Reason, DapModule Module);

/// <summary>A module the program loaded, its id the debugger's own for the one load of it (one assembly in two load contexts
/// is two modules); with the protocol's version, Just My Code's verdict (<see cref="IsUserCode"/>), whether its code runs
/// optimized, where its symbols came from and its address.</summary>
public sealed record DapModule(string Id, string Name, string? Path = null)
{
    public string? Version { get; init; }
    public bool? IsUserCode { get; init; }
    public bool? IsOptimized { get; init; }

    /// <summary>Whether its symbols are loaded, in words (<see cref="DapSymbolStatus"/>).</summary>
    [IdentifierOf(typeof(DapSymbolStatus))]
    public string? SymbolStatus { get; init; }

    /// <summary>The file its symbols were read from, or where they lie (in the assembly), where it has any.</summary>
    public string? SymbolFilePath { get; init; }

    public string? AddressRange { get; init; }

    /// <summary>Passenger's own: made as the program runs (System.Reflection.Emit), with no file.</summary>
    public bool? IsDynamic { get; init; }
}

/// <summary>Whether a module's symbols are loaded, its symbolStatus as Passenger's debug adapter says it and the IDE reads it,
/// each written once.</summary>
[IdentifierHome]
public static class DapSymbolStatus
{
    /// <summary>Its own symbols, from a PDB beside it or inside it.</summary>
    public const string Loaded = "Loaded";

    /// <summary>Symbols the debugger made by decompiling it, which it had none of.</summary>
    public const string Decompiled = "Decompiled";

    /// <summary>None yet: a frame in it shows no source until it is decompiled.</summary>
    public const string None = "Not loaded";
}

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

/// <summary>A document an update edits, lines from zero: the first of its lines the edit changed, as it read before, and the
/// text below the change, which reads as it did, from line TailWas before and TailNow now.</summary>
public sealed record DapFirstChange(string Document, int Line, int TailWas, int TailNow);

public sealed record DapLineUpdates(string Document, IReadOnlyList<DapLineUpdate> Updates);

public sealed record DapLineUpdate(int OldLine, int NewLine);

/// <summary>Where an active statement of a method's version is in the new version's source (lines and columns from zero).</summary>
public sealed record DapStatementUpdate(int Token, int Version, int ILOffset, int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>What became of each module's update, in the order given: applied, or why not. Once one fails, the program
/// may hold part of it, so Hot Reload stops for the session.</summary>
public sealed record ApplyChangesResponse(IReadOnlyList<DapModuleApplied> Modules);

public sealed record DapModuleApplied(Guid Module, string Name, bool Applied, string? Message = null);
