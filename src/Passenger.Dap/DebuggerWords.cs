namespace Passenger.Dap;

/// <summary>What Passenger's debugger answers and the IDE says alike, worded once for both ends, so the same want reads the
/// same in the debugger's answer and in the window's refusal.</summary>
public static class DebuggerWords
{
    /// <summary>No program is being debugged.</summary>
    public const string NothingDebugged = "Nothing is being debugged";

    /// <summary>A data breakpoint asked of a value that is no member of another: a local, a watch, a result.</summary>
    public const string NotAMember = "A data breakpoint watches a field or property of an object, or an element of an array, and this is neither";
}
