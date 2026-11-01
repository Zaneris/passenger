namespace Passenger.App.Debugging;

/// <summary>A breakpoint's state in words, the same wherever it is said (the gutter's hover, Problems, an agent's
/// breakpoint_set and debug_state): off, muted, set while nothing is debugged, bound, moved to the nearest line with code,
/// pending until a module holding its code loads, or not bound and why, the debugger's words saying how to put it right;
/// then the condition, hit count and message it carries.</summary>
public static class BreakpointStates
{
    /// <summary>A breakpoint's state in a word or two: "bound", "moved to line 14", "pending", "not bound", "off", "muted"
    /// or "set" (nothing debugged).</summary>
    public static string StateOf(Breakpoint breakpoint, bool debugging, bool muted) => breakpoint switch
    {
        { Enabled: false } => "off",
        _ when muted => "muted",
        _ when !debugging => "set",
        LineBreakpoint { Status: BreakpointStatus.Bound, MovedBy: int by and not 0 } line => $"moved to line {line.Line + by + 1}",
        { Status: BreakpointStatus.Bound } => "bound",
        { Status: BreakpointStatus.Invalid } => "not bound",
        _ => "pending",
    };

    /// <summary>What the hover says of a breakpoint, a line each: its kind and state, the debugger's words of it (why it is not
    /// bound, or where it stops), then what it carries.</summary>
    public static IReadOnlyList<string> Words(Breakpoint breakpoint, bool debugging, bool muted)
    {
        string kind = breakpoint.Suspend ? "Breakpoint" : "Tracepoint";
        string state = StateOf(breakpoint, debugging, muted);
        List<string> words = [state switch
        {
            "off" => $"{kind}, off: it does not stop the program",
            "muted" => $"{kind}, muted: Mute Breakpoints is on, so no breakpoint stops the program",
            "set" => $"{kind}: it binds as the program is debugged",
            "bound" => $"{kind}, bound: the program stops here",
            "pending" => $"{kind}, pending: not bound yet",
            "not bound" => $"{kind}, not bound: it will not stop the program",
            _ => $"{kind}, {state}, the nearest line with code: the program stops there",
        }];
        if (debugging && !muted && breakpoint.Enabled && breakpoint.Message is { Length: > 0 } why)
        {
            words.Add(why.TrimEnd('.'));
        }
        if (breakpoint.Condition is { } condition)
        {
            words.Add($"Condition: {condition}");
        }
        if (breakpoint.HitCondition is { } hits)
        {
            words.Add($"Hit count: {hits}");
        }
        if (breakpoint.LogMessage is { } message)
        {
            words.Add($"Logs: {message}");
        }
        else if (!breakpoint.Suspend)
        {
            words.Add("Logs \"Breakpoint reached\" and goes on");
        }
        return words;
    }

    /// <summary>Why a breakpoint will not stop the program, as Problems lists it: the debugger's words, which say how to put it
    /// right; null for one that is bound or may yet bind.</summary>
    public static string? Problem(Breakpoint breakpoint, bool debugging, bool muted) =>
        debugging && !muted && breakpoint is { Enabled: true, Status: BreakpointStatus.Invalid }
            ? $"Breakpoint not bound: {(breakpoint.Message is { Length: > 0 } why ? why.TrimEnd('.') : "the debugger did not say why")}"
            : null;
}
