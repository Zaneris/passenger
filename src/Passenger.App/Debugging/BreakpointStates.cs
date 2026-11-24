using Passenger.Languages.CSharp;

namespace Passenger.App.Debugging;

/// <summary>A breakpoint's state in words, the same wherever it is said (the gutter's hover, Problems, an agent's
/// breakpoint_set and debug_state): off, muted, set while nothing is debugged (where a C# line runs no code, the line it
/// moves to), bound, moved to the nearest line with code, pending until a module holding its code loads, or not bound and
/// why, the debugger's words saying how to put it right; then the condition, hit count and message it carries, what it
/// captures as a snapshot point, and whether it is temporary or waits for another.</summary>
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

    /// <summary>A C# line no code runs on, as its file's parse reads it, and the next line that may (from zero), null where
    /// none does to the file's end.</summary>
    public readonly record struct Unrun(int? Next);

    /// <summary>A line breakpoint's line as its C# file's parse reads it: null where code may run there, the breakpoint is
    /// inside a lambda, or there is no parse (a file not C#).</summary>
    public static Unrun? UnrunAt(ParsedText? parsed, LineBreakpoint breakpoint) =>
        parsed is not null && breakpoint.Column is null && !RunningLines.Runs(parsed, breakpoint.Line) ? new Unrun(RunningLines.NextAfter(parsed, breakpoint.Line)) : null;

    /// <summary>What is said of a breakpoint set on a line no code runs on while nothing is debugged: where the debugger will
    /// move it; null for any other.</summary>
    public static string? Moves(Breakpoint breakpoint, bool debugging, bool muted, Unrun? unrun) =>
        unrun is { } none && StateOf(breakpoint, debugging, muted) == "set"
            ? none.Next is int next
                ? $"{Named(breakpoint)}: no code runs on this line, so it moves to line {next + 1}, the next that may, as the program is debugged"
                : $"{Named(breakpoint)}: no code runs on this line or after it, so it will not {((breakpoint as CodeBreakpoint)?.Snapshot is null ? "stop the program" : "capture its hits")}"
            : null;

    /// <summary>A breakpoint's kind as its words begin: a snapshot point, a breakpoint that stops, or a tracepoint.</summary>
    private static string Named(Breakpoint breakpoint) =>
        (breakpoint as CodeBreakpoint)?.Snapshot is not null ? "Snapshot point" : breakpoint.Suspend ? "Breakpoint" : "Tracepoint";

    /// <summary>What the hover says of a breakpoint, a line each: its kind and state (for one set on a line no code runs on,
    /// where it moves), the debugger's words of it (why it is not bound, or where it stops), then what it carries.</summary>
    public static IReadOnlyList<string> Words(Breakpoint breakpoint, bool debugging, bool muted, Unrun? unrun = null)
    {
        SnapshotPoint? snapshot = (breakpoint as CodeBreakpoint)?.Snapshot;
        string kind = (breakpoint is MethodBreakpoint ? "Method " : string.Empty) + (snapshot is not null ? "snapshot point" : breakpoint.Suspend ? "breakpoint" : "tracepoint");
        kind = char.ToUpperInvariant(kind[0]) + kind[1..];
        string state = StateOf(breakpoint, debugging, muted);
        string does = snapshot is null ? "the program stops" : "each hit is captured as the program goes on";
        List<string> words = [state switch
        {
            "off" => $"{kind}, off: it does not {(snapshot is null ? "stop the program" : "capture its hits")}",
            "muted" => $"{kind}, muted: Mute Breakpoints is on, so no breakpoint stops the program{(snapshot is null ? string.Empty : " or captures its hits")}",
            "set" when Moves(breakpoint, debugging, muted, unrun) is { } moves => moves,
            "set" => $"{kind}: it binds as the program is debugged",
            "bound" => $"{kind}, bound: {does} here",
            "pending" => $"{kind}, pending: not bound yet",
            "not bound" => $"{kind}, not bound: it will not {(snapshot is null ? "stop the program" : "capture its hits")}",
            _ => $"{kind}, {state}, the nearest line with code: {does} there",
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
        if (snapshot is not null)
        {
            words.Add($"Captures the stack and locals{(snapshot.Expressions.Count > 0 ? $" with {string.Join(", ", snapshot.Expressions)}" : string.Empty)} at each hit and goes on, {Core.Plural.Of(snapshot.Takes, "snapshot")} at most a session");
        }
        if (breakpoint.LogMessage is { } message)
        {
            words.Add($"Logs: {message}");
        }
        else if (!breakpoint.Suspend && snapshot is null)
        {
            words.Add("Logs \"Breakpoint reached\" and goes on");
        }
        if (breakpoint is CodeBreakpoint { WaitsFor: { } waited })
        {
            words.Add($"Waits for {waited.Location} to be hit first");
        }
        if (breakpoint is CodeBreakpoint { Temporary: true })
        {
            words.Add("Temporary: removed once hit");
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
