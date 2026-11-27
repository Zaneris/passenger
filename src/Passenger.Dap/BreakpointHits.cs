using System.Text;
using Passenger.Core;

namespace Passenger.Dap;

/// <summary>A breakpoint of any kind as asked for: the condition and hit count its hits are tried by.</summary>
public interface IAskedBreakpoint
{
    string? Condition { get; }
    string? HitCondition { get; }
}

/// <summary>A breakpoint of any kind (line, function, data, exception) as its hits are tried: what it asks of a hit, its
/// hits so far, and the debugger's word on it, which a condition a hit could not tell is written to.</summary>
public interface IHitBreakpoint
{
    int Id { get; }
    string? Condition { get; }
    string? HitCondition { get; }
    string? LogMessage { get; }
    bool Suspend { get; }

    /// <summary>Held for Run to Cursor: its next hit stops whatever its condition, hit count and Suspend say.</summary>
    bool RunTo => false;

    /// <summary>The client's own number for it, by which another breakpoint waits for it; null where it gave none.</summary>
    int? Key => null;

    /// <summary>The <see cref="Key"/> of the breakpoint it waits for: until that one takes a hit, each of its own is passed
    /// over, neither tried nor counted.</summary>
    int? WaitFor => null;

    /// <summary>Removed once hit: the first hit it takes is its last, which the session is told of.</summary>
    bool Temporary => false;

    /// <summary>A snapshot point: each hit it takes reads the program's state where it stands and lets the program go on,
    /// never stopping it, at most <see cref="CaptureLimit"/> times in a session.</summary>
    bool Snapshot => false;

    /// <summary>A snapshot point's own expressions, one to a line, read in its top frame at each capture.</summary>
    string? Captures => null;

    /// <summary>How many captures a snapshot point takes in a session, its later hits passed by; null for
    /// <see cref="BreakpointHits.CaptureLimit"/>.</summary>
    int? CaptureLimit => null;

    /// <summary>The hits where its condition held (or every hit, where it has none).</summary>
    int HitCount { get; set; }
    string? Message { get; set; }
}

/// <summary>What a breakpoint's condition came to at a hit: whether it held, or why it could not be told.</summary>
public readonly record struct ConditionRead(bool Holds, string? Unread = null);

/// <summary>What a debugger reads where a hit stands, for <see cref="BreakpointHits.StopsAsync"/>.</summary>
public sealed class HitReading
{
    /// <summary>The breakpoint as said where its condition could not be told: "the breakpoint", "the data breakpoint on …".</summary>
    public required string Named { get; init; }

    /// <summary>Whether a condition holds where the hit stands; null where it was tried before the hit came (the browser's).</summary>
    public required Func<string, Task<ConditionRead>>? Holds { get; init; }

    /// <summary>A log message with each expression in it read where the hit stands.</summary>
    public required Func<string, Task<string>> Logged { get; init; }

    /// <summary>What a hit that neither stops nor logs says.</summary>
    public required Func<string> Reached { get; init; }

    /// <summary>A line for the session's console.</summary>
    public required Action<string> Say { get; init; }

    /// <summary>The breakpoint's message has changed, for the session to show.</summary>
    public required Action<IHitBreakpoint> Told { get; init; }

    /// <summary>The debugger's own log, of a hit that goes on.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>What the session's breakpoints have come to between them.</summary>
    public required SessionHits Session { get; init; }

    /// <summary>A temporary breakpoint's hit was its last: the session is to take it away.</summary>
    public required Action<IHitBreakpoint> Removed { get; init; }

    /// <summary>A snapshot point's capture, taken where the hit stands; null where the debugger takes none.</summary>
    public Func<IHitBreakpoint, Task>? Capture { get; init; }

    /// <summary>Why a hit went on without stopping, in words ("its condition i == 3 was false"), for a recording; null where
    /// nothing records.</summary>
    public Action<string>? Passed { get; init; }

    /// <summary>Whether the hit came in a forced step or run (Force Step Over, Force Run to Cursor), which passes it by.</summary>
    public bool Forced { get; init; }
}

/// <summary>What one debugging session's breakpoints have come to between them, by their keys
/// (<see cref="IHitBreakpoint.Key"/>): those that took a hit, which a breakpoint waiting for one waits for no more, and the
/// temporary ones gone, whose later hits are passed over until the session takes them away. Any thread.</summary>
public sealed class SessionHits
{
    private readonly HashSet<int> hit = [];
    private readonly HashSet<int> gone = [];
    private readonly Dictionary<int, int> captured = [];
    private readonly Lock gate = new();

    /// <summary>Whether the breakpoint of a key has taken a hit.</summary>
    public bool WasHit(int key)
    {
        lock (gate)
        {
            return hit.Contains(key);
        }
    }

    internal void Hit(int key)
    {
        lock (gate)
        {
            hit.Add(key);
        }
    }

    /// <summary>Whether the temporary breakpoint of a key has gone.</summary>
    public bool Went(int key)
    {
        lock (gate)
        {
            return gone.Contains(key);
        }
    }

    /// <summary>The temporary breakpoint of a key goes: true the first time, false where it went already.</summary>
    internal bool Go(int key)
    {
        lock (gate)
        {
            return gone.Add(key);
        }
    }

    /// <summary>How many captures the snapshot point of an id has taken, with this one: past its limit, none was.</summary>
    internal int Capture(int id)
    {
        lock (gate)
        {
            return captured[id] = captured.GetValueOrDefault(id) + 1;
        }
    }
}

/// <summary>Every debugger's one way with a breakpoint's hits: a hit counts only where the condition held (or there is
/// none), as a person reads "stop the 3rd time i &gt; 10", and a breakpoint takes a hit where its hit count is met too,
/// arming those waiting for it, whose hits are passed over until then; a temporary breakpoint's first hit taken is its last,
/// and a snapshot point's hit taken is captured and goes on. A breakpoint asked for again keeps its count while
/// its place, condition and hit count stay (an edit of its message, of whether it stops, of what it waits for or whether it
/// is temporary keeps it), and counts again from the edit where any of those change.</summary>
public static class BreakpointHits
{
    /// <summary>How many captures a snapshot point takes in a session where it asks for no other number, so a line hit
    /// thousands of times a second holds the program a bounded while.</summary>
    public const int CaptureLimit = 100;

    /// <summary>The most captures a snapshot point may ask for.</summary>
    public const int MostCaptures = 10_000;

    /// <summary>Matches the breakpoints asked for with those set: one asked for again just as it was keeps the one set (each
    /// kept once), else counts on from the hits of one it replaces at its place (<paramref name="samePlace"/>) with the same
    /// condition and hit count. Those set that no request keeps are <paramref name="gone"/>.</summary>
    public static (T? Kept, int Hits)[] Reconcile<T, TRequest>(IReadOnlyList<TRequest> requests, IReadOnlyList<T> set, Func<T, TRequest?> requestOf,
        Func<TRequest, TRequest, bool> samePlace, out List<T> gone) where T : class, IHitBreakpoint where TRequest : class, IAskedBreakpoint
    {
        T?[] kept = new T?[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            kept[i] = set.FirstOrDefault(b => EqualityComparer<TRequest>.Default.Equals(requestOf(b), requests[i]) && !kept.Contains(b));
        }
        gone = [.. set.Where(b => !kept.Contains(b))];
        (T? Kept, int Hits)[] matched = new (T?, int)[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            TRequest request = requests[i];
            matched[i] = (kept[i], kept[i] is null && gone.FirstOrDefault(b => requestOf(b) is { } was && samePlace(was, request) && CountsOn(was, request)) is { } replaced
                ? replaced.HitCount : 0);
        }
        return matched;
    }

    /// <summary>Whether a breakpoint asked for in place of another counts on from its hits: the same condition and hit count,
    /// a hit count written another way (3, ==3, = 3) the same.</summary>
    private static bool CountsOn(IAskedBreakpoint was, IAskedBreakpoint now) =>
        Trimmed(was.Condition) == Trimmed(now.Condition) && (Trimmed(was.HitCondition), Trimmed(now.HitCondition)) switch
        {
            (null, null) => true,
            ({ } before, { } after) => before == after
                || (HitCondition.TryParse(before, out HitCondition first, out _) && HitCondition.TryParse(after, out HitCondition second, out _) && first == second),
            _ => false,
        };

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Why a breakpoint's hit count cannot be read, or null where it can (or there is none): such a breakpoint is
    /// refused when set.</summary>
    public static string? Unreadable(string? hitCondition) =>
        hitCondition is { } hits && !HitCondition.TryParse(hits, out _, out string? problem) ? problem : null;

    /// <summary>A hit of a breakpoint of any kind: passed over in a forced step, while what it waits for has taken no hit, or
    /// once it went as temporary; else its condition tried (one that cannot be told taken as held and said), counted where it
    /// held, then tried by its hit count. A hit taken arms those waiting for it, is a temporary one's last, is a snapshot
    /// point's capture within its limit, and logs its message, or says <see cref="HitReading.Reached"/> where it neither logs,
    /// stops nor captures. Whether it stops the program, as a snapshot point's never does but for Run to Cursor.</summary>
    public static async Task<bool> StopsAsync(IHitBreakpoint breakpoint, HitReading hit)
    {
        if (hit.Forced && !breakpoint.RunTo)
        {
            hit.Log?.Invoke($"Breakpoint {breakpoint.Id}: passed by a forced step");
            hit.Passed?.Invoke("a forced step passes breakpoints by");
            return false;
        }
        if (breakpoint.WaitFor is int awaited && !hit.Session.WasHit(awaited))
        {
            hit.Log?.Invoke($"Breakpoint {breakpoint.Id}: waits for another breakpoint to be hit first");
            hit.Passed?.Invoke("it waits for another breakpoint's hit");
            return breakpoint.RunTo;
        }
        if (breakpoint.Temporary && breakpoint.Key is int once && hit.Session.Went(once))
        {
            return breakpoint.RunTo;
        }
        string? unread = null;
        if (breakpoint.Condition is { } condition && hit.Holds is { } holds)
        {
            ConditionRead read = await holds(condition);
            if (read.Unread is { } why)
            {
                unread = $"{BreakpointConditions.NotEvaluated(condition)}, so {(breakpoint.Snapshot ? "a snapshot was taken" : $"{hit.Named} stopped")}: {why}";
            }
            else if (!read.Holds)
            {
                hit.Log?.Invoke($"Breakpoint {breakpoint.Id}: condition not met: {condition}");
                hit.Passed?.Invoke($"its condition {condition} was false");
                return breakpoint.RunTo;
            }
        }
        breakpoint.HitCount++;
        if (breakpoint.HitCondition is { } hits && !(HitCondition.TryParse(hits, out HitCondition parsed, out _) && parsed.IsMet(breakpoint.HitCount)))
        {
            hit.Log?.Invoke($"Breakpoint {breakpoint.Id}: hit count not met: count={breakpoint.HitCount}, condition={hits}");
            hit.Passed?.Invoke($"its hit count {hits} was not met at hit {Numbers.Said(breakpoint.HitCount)}");
            return breakpoint.RunTo;
        }
        if (breakpoint.Key is int key)
        {
            hit.Session.Hit(key);
        }
        if (breakpoint.Temporary)
        {
            // Two threads at it at once: the first takes it, the second passes it by.
            if (breakpoint.Key is int going && !hit.Session.Go(going))
            {
                return breakpoint.RunTo;
            }
            hit.Removed(breakpoint);
        }
        if (unread is not null)
        {
            breakpoint.Message = unread;
            hit.Told(breakpoint);
            hit.Say(unread);
        }
        if (breakpoint.Snapshot)
        {
            bool taken = await CaptureAsync(breakpoint, hit);
            if (taken && breakpoint.LogMessage is { } said)
            {
                hit.Say(await hit.Logged(said));
            }
            hit.Passed?.Invoke(taken ? "it took a snapshot" : "it took all the snapshots it takes");
            return breakpoint.RunTo;
        }
        if (breakpoint.LogMessage is { } message)
        {
            string logged = await hit.Logged(message);
            hit.Say(logged);
            hit.Passed?.Invoke($"it logged {logged}");
        }
        else if (!breakpoint.Suspend)
        {
            hit.Say(hit.Reached());
            hit.Passed?.Invoke("it was reached");
        }
        return breakpoint.Suspend || unread is not null || breakpoint.RunTo;
    }

    /// <summary>A snapshot point's hit captured, while it has taken fewer than its limit; the first hit past it tells the
    /// session so, on the point. Whether it took the hit.</summary>
    private static async Task<bool> CaptureAsync(IHitBreakpoint point, HitReading hit)
    {
        int limit = point.CaptureLimit ?? CaptureLimit;
        int count = hit.Session.Capture(point.Id);
        if (count > limit)
        {
            if (count == limit + 1)
            {
                point.Message = LimitReached(limit);
                hit.Told(point);
            }
            hit.Log?.Invoke($"Breakpoint {point.Id}: took its {limit} snapshots already");
            return false;
        }
        if (hit.Capture is { } capture)
        {
            await capture(point);
        }
        return true;
    }

    /// <summary>What a snapshot point says once it has taken its limit of captures.</summary>
    public static string LimitReached(int limit) => $"Took its {limit} {(limit == 1 ? "snapshot" : "snapshots")}, so its later hits pass by until debugging starts again";

    /// <summary>What a line breakpoint that neither stops nor logs says at a hit: its file's name and line (from one).</summary>
    public static string Reached(string file, int line) => $"Breakpoint reached at {Path.GetFileName(file)}:{line}";

    /// <summary>What an exception breakpoint that neither stops nor logs says at a hit: what was thrown.</summary>
    public static string ExceptionReached(string thrown) => $"Exception breakpoint reached: {thrown}";

    /// <summary>A tracepoint's message with each <c>{expression}</c> in it read by <paramref name="read"/>, which says a
    /// failure in its own words; the message is read, and each value shown by its specifiers, as every debugger reads and
    /// shows one (<see cref="DisplayFormat"/>), an empty expression showing nothing.</summary>
    public static async Task<string> LoggedAsync(string message, Func<string, Task<DisplayValue>> read)
    {
        StringBuilder said = new();
        foreach ((string text, bool expression) in DisplayFormat.Pieces(message))
        {
            if (!expression)
            {
                said.Append(text);
                continue;
            }
            (string asked, IReadOnlyList<string> specifiers) = DisplayFormat.Specifiers(text);
            if (asked.Length > 0)
            {
                said.Append(DisplayFormat.Shown(await read(asked), specifiers));
            }
        }
        return said.ToString();
    }
}
