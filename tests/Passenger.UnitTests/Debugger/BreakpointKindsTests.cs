using Passenger.Dap;
using Passenger.Debugger;
using Passenger.Debugger.ExpressionEvaluator.Compiler;

namespace Passenger.UnitTests.Debugger;

/// <summary>What every kind of breakpoint shares, in every debugger: what the debugger refuses to read, how those asked for
/// are matched with those already set, and how a hit is tried (<see cref="BreakpointHits"/>).</summary>
public class BreakpointKindsTests
{
    private const string NoLambdas = "Lambda expressions are not supported in breakpoint conditions.";

    /// <summary>What every debugger refuses to read is refused saying why, a condition nested too deep too; a breakpoint asked
    /// for again is kept, counting on through an edit of its message and again from an edit of its condition or count; a hit
    /// count counts only where the condition held; a condition that cannot be told stops its counted hit saying why; Run to
    /// Cursor's hold stops the next hit; one waiting for another passes its hits by until that one takes one; a temporary
    /// one's first hit taken is its last; a snapshot point never stops, capturing each hit it takes within its limit.</summary>
    [Fact]
    public Task A_breakpoint_is_refused_matched_counted_and_tried_alike_in_every_debugger() => Cases.AllAsync(
        ("a condition holding a lambda, a hit count the debugger cannot read and a message it would misread are refused", () =>
        {
            Assert.Equal(NoLambdas, ManagedDebugger.Unreadable("count > 1 && items.Where((item, at) => at > count).Any()", "==2", null));
            Assert.Contains("The hit count \"abc\" is not one the debugger can read", ManagedDebugger.Unreadable("count > 1", "abc", null), StringComparison.Ordinal);
            Assert.Equal(DisplayFormat.Misread("{{count}}"), ManagedDebugger.Unreadable("count > 1", ">=3", "{{count}}"));
            Assert.Null(ManagedDebugger.Unreadable("count > 1 && name == \"=>\"", ">=3", "\\{count\\} is {count}"));
            return Task.CompletedTask;
        }),
        ("a condition nested past what the parser reads is refused saying so and no reader of it runs out of stack", () =>
        {
            string holes = string.Concat(Enumerable.Repeat("$\"{", 20_000)) + "0" + string.Concat(Enumerable.Repeat("}\"", 20_000));
            string brackets = new string('(', 20_000) + "true" + new string(')', 20_000);
            SmallStack.Run(() =>
            {
                Assert.Equal("The condition nests deeper than the debugger reads.", ManagedDebugger.Unreadable(holes, null, null));
                Assert.Equal("The condition nests deeper than the debugger reads.", ManagedDebugger.Unreadable(brackets, "==2", null));
                Assert.False(CilExpressionCompiler.NamesLinq(holes), "An expression too deep to read was read as naming LINQ");
                Assert.Equal($"({brackets})", RowExpressions.Operand(brackets));
                Assert.Empty(WritableTargets.Of(brackets, _ => false));
            });
            Assert.Null(ManagedDebugger.Unreadable(new string('(', 900) + "true" + new string(')', 900), null, null));
            return Task.CompletedTask;
        }),
        ("a breakpoint asked for again is kept whole, counts on through an edit of its message and again from an edit of its count or condition", () =>
        {
            BreakpointManager.BreakpointInfo same = Set(1, new BreakpointRequest(5, Condition: "x > 1"), hits: 3);
            BreakpointManager.BreakpointInfo logged = Set(2, new BreakpointRequest(9, Condition: "x > 1", HitCondition: "3", LogMessage: "x is {x}"), hits: 2);
            BreakpointManager.BreakpointInfo counted = Set(3, new BreakpointRequest(12, HitCondition: "==3"), hits: 4);
            BreakpointManager.BreakpointInfo conditioned = Set(4, new BreakpointRequest(14, Condition: "x > 1"), hits: 5);
            BreakpointManager.BreakpointInfo held = Set(5, new BreakpointRequest(16, HitCondition: "==8"), hits: 6);
            BreakpointManager.BreakpointInfo removed = Set(6, new BreakpointRequest(30), hits: 7);
            BreakpointRequest[] asked =
            [
                new(5, Condition: "x > 1"), new(5, Condition: "x > 1"), new(9, Condition: "x > 1", HitCondition: "== 3", LogMessage: "x was {x}", Suspend: false),
                new(12, HitCondition: "==5"), new(14, Condition: "x > 2"), new(16, HitCondition: "==8", RunTo: true), new(20),
            ];

            (BreakpointManager.BreakpointInfo? Kept, int Hits)[] matched = BreakpointHits.Reconcile(asked, [same, logged, counted, conditioned, held, removed], b => b.Request,
                static (was, now) => was.Line == now.Line && was.Column == now.Column, out List<BreakpointManager.BreakpointInfo> gone);

            Assert.Same(same, matched[0].Kept);
            Assert.True(matched[1] == (null, 0), $"A second request just as the first took the one breakpoint again, or its hits: {matched[1]}");
            Assert.True(matched[2] == (null, 2), $"The breakpoint whose message was edited did not count on from its 2 hits: {matched[2]}");
            Assert.True(matched[3] == (null, 0), $"The breakpoint whose hit count was edited counted on rather than again: {matched[3]}");
            Assert.True(matched[4] == (null, 0), $"The breakpoint whose condition was edited counted on rather than again: {matched[4]}");
            Assert.True(matched[5] == (null, 6), $"The breakpoint held for Run to Cursor did not count on from its 6 hits: {matched[5]}");
            Assert.True(matched[6] == (null, 0), $"A breakpoint at a new place took hits: {matched[6]}");
            Assert.Equal([logged, counted, conditioned, held, removed], gone);
            return Task.CompletedTask;
        }),
        ("a hit count counts only the hits where the condition held", async () =>
        {
            int[] counted = await StopsAsync(Set(1, new BreakpointRequest(5, Condition: "i > 10", HitCondition: "==4"), hits: 0), i => i > 10);
            int[] every = await StopsAsync(Set(1, new BreakpointRequest(5, HitCondition: "==4"), hits: 0), _ => true);
            Assert.Equal([14], counted);
            Assert.Equal([3], every);
        }),
        ("a condition that cannot be told stops its counted hit saying why, a tracepoint's too", async () =>
        {
            BreakpointManager.BreakpointInfo tracepoint = Set(1, new BreakpointRequest(5, Condition: "nothing", HitCondition: "==2", LogMessage: "at {i}", Suspend: false), hits: 0);
            tracepoint.Suspend = false;
            tracepoint.LogMessage = "at {i}";
            List<string> said = [];
            List<IHitBreakpoint> told = [];
            List<bool> stopped = [];
            for (int i = 0; i < 3; i++)
            {
                stopped.Add(await BreakpointHits.StopsAsync(tracepoint, Reading(i, _ => new ConditionRead(true, "no such name"), said, told)));
            }

            Assert.Equal([false, true, false], stopped);
            string why = "The condition 'nothing' could not be evaluated, so the breakpoint stopped: no such name";
            Assert.Equal([why, "at 1"], said);
            Assert.Equal([tracepoint], told);
        }),
        ("Run to Cursor's hold stops the next hit whatever its condition and count say, a forced run's too, which passes every other by uncounted", async () =>
        {
            BreakpointManager.BreakpointInfo held = Set(1, new BreakpointRequest(5, Condition: "i > 10", HitCondition: "==4", RunTo: true), hits: 2);
            Assert.True(await BreakpointHits.StopsAsync(held, Reading(3, i => new ConditionRead(i > 10), [], [])), "Run to Cursor's hold did not stop where the condition is false");
            Assert.Equal(2, held.HitCount);
            Assert.True(await BreakpointHits.StopsAsync(held, Reading(11, i => new ConditionRead(i > 10), [], [])), "Run to Cursor's hold did not stop where the hit count is not met");
            Assert.Equal(3, held.HitCount);
            Assert.True(await BreakpointHits.StopsAsync(held, Reading(11, _ => new ConditionRead(true), [], [], forced: true)), "A forced run passed Run to Cursor's hold by");
            BreakpointManager.BreakpointInfo plain = Set(2, new BreakpointRequest(9), hits: 0);
            Assert.False(await BreakpointHits.StopsAsync(plain, Reading(1, _ => new ConditionRead(true), [], [], forced: true)), "A forced step stopped at a breakpoint it passes by");
            Assert.Equal(0, plain.HitCount);
        }),
        ("a breakpoint waiting for another passes its hits by uncounted until that one takes a hit, a tracepoint's too, then stops as its own say", async () =>
        {
            SessionHits session = new();
            BreakpointManager.BreakpointInfo first = Set(1, new BreakpointRequest(5, Condition: "i > 2", Suspend: false, LogMessage: "first at {i}"), hits: 0);
            (first.Key, first.Suspend, first.LogMessage) = (101, false, "first at {i}");
            BreakpointManager.BreakpointInfo waiting = Set(2, new BreakpointRequest(9, HitCondition: "==2"), hits: 0);
            waiting.WaitFor = 101;
            List<string> said = [];
            List<int> stops = [];
            for (int i = 0; i < 6; i++)
            {
                Assert.False(await BreakpointHits.StopsAsync(first, Reading(i, at => new ConditionRead(at > 2), said, [], session)), $"The tracepoint stopped at {i}");
                if (await BreakpointHits.StopsAsync(waiting, Reading(i, _ => new ConditionRead(true), said, [], session)))
                {
                    stops.Add(i);
                }
            }
            Assert.Equal([4], stops);
            Assert.Equal(3, waiting.HitCount);
            Assert.Equal(["first at 3", "first at 4", "first at 5"], said);
        }),
        ("a temporary breakpoint's first hit taken is its last: the session is told to take it away and its later hits pass by", async () =>
        {
            SessionHits session = new();
            BreakpointManager.BreakpointInfo once = Set(1, new BreakpointRequest(5, HitCondition: ">=2"), hits: 0);
            (once.Key, once.Temporary) = (7, true);
            List<IHitBreakpoint> removed = [];
            List<int> stops = [];
            for (int i = 0; i < 5; i++)
            {
                if (await BreakpointHits.StopsAsync(once, Reading(i, _ => new ConditionRead(true), [], [], session, removed)))
                {
                    stops.Add(i);
                }
            }
            Assert.Equal([1], stops);
            Assert.Equal([once], removed);
            Assert.True(session.Went(7), "The temporary breakpoint was not gone once it stopped");
            Assert.True(session.WasHit(7), "The temporary breakpoint's hit armed nothing waiting for it");
        }),
        ("a snapshot point never stops: each hit where its condition holds is captured and logged until its limit, the first past it told once, one it cannot tell captured saying why", async () =>
        {
            SessionHits session = new();
            BreakpointManager.BreakpointInfo point = Set(1, new BreakpointRequest(5, Condition: "i % 2 == 0"), hits: 0);
            (point.Snapshot, point.CaptureLimit, point.LogMessage) = (true, 3, "at {i}");
            List<string> said = [];
            List<IHitBreakpoint> told = [];
            List<int> captured = [];
            for (int i = 0; i < 12; i++)
            {
                int at = i;
                HitReading hit = Reading(i, held => new ConditionRead(held % 2 == 0), said, told, session, capture: _ =>
                {
                    captured.Add(at);
                    return Task.CompletedTask;
                });
                Assert.False(await BreakpointHits.StopsAsync(point, hit), $"The snapshot point stopped the program at {i}");
            }
            Assert.Equal([0, 2, 4], captured);
            Assert.Equal([point], told);
            Assert.Equal(BreakpointHits.LimitReached(3), point.Message);
            Assert.Equal(["at 0", "at 2", "at 4"], said);

            BreakpointManager.BreakpointInfo unread = Set(2, new BreakpointRequest(9, Condition: "nothing"), hits: 0);
            unread.Snapshot = true;
            List<string> unreadSaid = [];
            bool took = false;
            Assert.False(await BreakpointHits.StopsAsync(unread, Reading(0, _ => new ConditionRead(true, "no such name"), unreadSaid, [], session, capture: _ =>
            {
                took = true;
                return Task.CompletedTask;
            })), "A snapshot point whose condition cannot be told stopped the program");
            Assert.True(took && unreadSaid.SequenceEqual(["The condition 'nothing' could not be evaluated, so a snapshot was taken: no such name"]),
                $"A snapshot point whose condition cannot be told took a snapshot: {took}, and said: {string.Join(" | ", unreadSaid)}");
        }));

    /// <summary>The values of i at which a breakpoint stops through a loop of 30 turns, its condition read as <paramref name="holds"/>.</summary>
    private static async Task<int[]> StopsAsync(BreakpointManager.BreakpointInfo breakpoint, Func<int, bool> holds)
    {
        List<int> stops = [];
        for (int i = 0; i < 30; i++)
        {
            if (await BreakpointHits.StopsAsync(breakpoint, Reading(i, at => new ConditionRead(holds(at)), [], [])))
            {
                stops.Add(i);
            }
        }
        return [.. stops];
    }

    /// <summary>A hit at turn <paramref name="i"/>, its condition read as <paramref name="holds"/>, a message's {i} as i, in a
    /// session of its own unless one is given, a snapshot point's capture taken by <paramref name="capture"/>, in a forced step
    /// where <paramref name="forced"/>.</summary>
    private static HitReading Reading(int i, Func<int, ConditionRead> holds, List<string> said, List<IHitBreakpoint> told, SessionHits? session = null,
        List<IHitBreakpoint>? removed = null, Func<IHitBreakpoint, Task>? capture = null, bool forced = false) => new()
    {
        Forced = forced,
        Capture = capture,
        Named = "the breakpoint",
        Holds = _ => Task.FromResult(holds(i)),
        Logged = message => BreakpointHits.LoggedAsync(message, _ => Task.FromResult(DisplayValue.OfWhole(i, sizeof(int)))),
        Reached = () => BreakpointHits.Reached("/src/Loop.cs", 5),
        Say = said.Add,
        Told = told.Add,
        Session = session ?? new SessionHits(),
        Removed = gone => removed?.Add(gone),
    };

    private static BreakpointManager.BreakpointInfo Set(int id, BreakpointRequest request, int hits) =>
        new() { Id = id, Line = request.Line, Condition = request.Condition, HitCondition = request.HitCondition, Request = request, HitCount = hits };
}
