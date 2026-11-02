namespace Passenger.Dap;

/// <summary>The Chrome DevTools Protocol's commands Passenger sends a browser's page through .NET's browser debug proxy, by
/// the protocol's names, each written once.</summary>
[IdentifierHome]
public static class CdpCommands
{
    public const string PageEnable = "Page.enable";
    public const string PageNavigate = "Page.navigate";
    public const string RuntimeEnable = "Runtime.enable";
    public const string GetProperties = "Runtime.getProperties";
    public const string DebuggerEnable = "Debugger.enable";
    public const string SetBreakpointByUrl = "Debugger.setBreakpointByUrl";
    public const string RemoveBreakpoint = "Debugger.removeBreakpoint";
    public const string SetPauseOnExceptions = "Debugger.setPauseOnExceptions";
    public const string Resume = "Debugger.resume";
    public const string StepOver = "Debugger.stepOver";
    public const string StepInto = "Debugger.stepInto";
    public const string StepOut = "Debugger.stepOut";
    public const string Pause = "Debugger.pause";
    public const string EvaluateOnCallFrame = "Debugger.evaluateOnCallFrame";
}

/// <summary>The Chrome DevTools Protocol's events Passenger hears from a browser's page, by the protocol's names.</summary>
[IdentifierHome]
public static class CdpEvents
{
    public const string Paused = "Debugger.paused";
    public const string Resumed = "Debugger.resumed";
    public const string BreakpointResolved = "Debugger.breakpointResolved";
    public const string ConsoleCalled = "Runtime.consoleAPICalled";
    public const string ExceptionThrown = "Runtime.exceptionThrown";
    public const string Detached = "Inspector.detached";
}
