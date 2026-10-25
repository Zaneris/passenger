namespace Passenger.Dap;

/// <summary>The requests the IDE and Passenger's debug adapter make of each other, by the protocol's names, each written once
/// for both ends, so a name one end misspells is a build error rather than a request the other never answers. Passenger's
/// own extend the protocol: <see cref="ResolveStackFrame"/>, <see cref="HotReloadState"/>, <see cref="ApplyChanges"/> and
/// <see cref="Input"/>.</summary>
[IdentifierHome]
public static class DapCommands
{
    public const string Initialize = "initialize";
    public const string Launch = "launch";
    public const string Attach = "attach";
    public const string ConfigurationDone = "configurationDone";
    public const string SetBreakpoints = "setBreakpoints";
    public const string SetFunctionBreakpoints = "setFunctionBreakpoints";
    public const string SetExceptionBreakpoints = "setExceptionBreakpoints";
    public const string DataBreakpointInfo = "dataBreakpointInfo";
    public const string SetDataBreakpoints = "setDataBreakpoints";
    public const string Threads = "threads";
    public const string StackTrace = "stackTrace";
    public const string Scopes = "scopes";
    public const string Variables = "variables";
    public const string Evaluate = "evaluate";
    public const string SetExpression = "setExpression";
    public const string GotoTargets = "gotoTargets";
    public const string Goto = "goto";
    public const string Continue = "continue";
    public const string Next = "next";
    public const string StepIn = "stepIn";
    public const string StepInTargets = "stepInTargets";
    public const string StepOut = "stepOut";
    public const string Pause = "pause";
    public const string Disconnect = "disconnect";
    public const string Terminate = "terminate";
    public const string ExceptionInfo = "exceptionInfo";

    /// <summary>The adapter's request of the client: start the program in its terminal.</summary>
    public const string RunInTerminal = "runInTerminal";

    /// <summary>Passenger's own: a frame without source given its assembly's decompiled symbols.</summary>
    public const string ResolveStackFrame = "resolveStackFrame";

    /// <summary>Passenger's own: what the language's Edit and Continue needs of the program.</summary>
    public const string HotReloadState = "hotReloadState";

    /// <summary>Passenger's own: Hot Reload's updates applied to the program.</summary>
    public const string ApplyChanges = "applyChanges";

    /// <summary>Passenger's own: text for the program's standard input, or its input closed.</summary>
    public const string Input = "input";
}

/// <summary>The events Passenger's debug adapter tells the IDE, by the protocol's names, each written once for both ends.</summary>
[IdentifierHome]
public static class DapEvents
{
    public const string Initialized = "initialized";
    public const string Stopped = "stopped";
    public const string Breakpoint = "breakpoint";
    public const string Exited = "exited";
    public const string Terminated = "terminated";
    public const string Thread = "thread";
    public const string Module = "module";
    public const string Process = "process";
    public const string Output = "output";
}

/// <summary>Why the program stopped, the stopped event's reason, by the protocol's names: what the engine reports, what the
/// adapter passes on and what the IDE tells apart, each written once.</summary>
[IdentifierHome]
public static class StopReasons
{
    public const string Step = "step";
    public const string Breakpoint = "breakpoint";
    public const string Exception = "exception";
    public const string Pause = "pause";
    public const string Entry = "entry";
    public const string Goto = "goto";
    public const string FunctionBreakpoint = "function breakpoint";
    public const string DataBreakpoint = "data breakpoint";
    public const string InstructionBreakpoint = "instruction breakpoint";

    /// <summary>A reason the protocol has no name for.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The rows Passenger's debug adapter adds among a value's members, by their names, which the IDE tells apart: each
/// written once for both ends.</summary>
[IdentifierHome]
public static class DapRows
{
    /// <summary>An EF Core query's SQL: a lazy row, read only as it is opened.</summary>
    public const string QuerySql = "SQL";

    /// <summary>What <see cref="QuerySql"/> says before it is opened.</summary>
    public const string QuerySqlUnread = "Opening it runs ToQueryString() in the program";
}

/// <summary>The exception filters Passenger's debug adapter offers, by their ids: what initialize's answer lists, what
/// setExceptionBreakpoints names and what the adapter reads back, each written once.</summary>
[IdentifierHome]
public static class DapExceptionFilters
{
    /// <summary>Every exception, handled or not, as it is thrown.</summary>
    public const string All = "all";

    /// <summary>An exception user code leaves unhandled.</summary>
    public const string UserUnhandled = "user-unhandled";
}

/// <summary>An output event's categories, by the protocol's names: what the adapter tells and the IDE sorts into its
/// console's streams, each written once.</summary>
[IdentifierHome]
public static class DapOutputCategories
{
    /// <summary>What the program writes to its standard output.</summary>
    public const string Stdout = "stdout";

    /// <summary>What the program writes to its standard error.</summary>
    public const string Stderr = "stderr";

    /// <summary>The debugger's own words: a logpoint's message, a breakpoint reached, why it could not do as asked.</summary>
    public const string Console = "console";

    /// <summary>Data for the client to keep, never shown.</summary>
    public const string Telemetry = "telemetry";
}

/// <summary>The debug adapter's command line, as it reads it and as the IDE and the tests start it, each flag written once:
/// netcoredbg's, so a client written for that runs this.</summary>
[IdentifierHome]
public static class AdapterFlags
{
    /// <summary>The protocol spoken on standard input and output, the only one the adapter speaks.</summary>
    public const string Interpreter = "--interpreter=vscode";

    /// <summary>What every other interpreter flag begins with, refused.</summary>
    public const string AnyInterpreter = "--interpreter=";

    /// <summary>Followed by a file, which the engine's log is written to.</summary>
    public const string EngineLogging = "--engineLogging=";

    /// <summary>Followed by a folder, which keeps the symbols decompiled for assemblies that ship none.</summary>
    public const string SymbolCache = "--symbolCache=";

    /// <summary>The client is on another machine (the adapter in a container): the processes it tells of are not local.</summary>
    public const string Remote = "--remote";
}
