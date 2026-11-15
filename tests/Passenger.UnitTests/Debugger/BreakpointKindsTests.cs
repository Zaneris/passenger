using Passenger.Dap;
using Passenger.Debugger;
using Passenger.Debugger.ExpressionEvaluator.Compiler;

namespace Passenger.UnitTests.Debugger;

/// <summary>What every kind of breakpoint shares, in every debugger: what the debugger refuses to read, how those asked for
/// are matched with those already set, and how a hit is tried (<see cref="BreakpointHits"/>).</summary>
public class BreakpointKindsTests
{
    private const string NoLambdas = "Lambda expressions are not supported in breakpoint conditions.";

    [Fact]
    public void A_condition_holding_a_lambda_anywhere_a_hit_count_the_debugger_cannot_read_and_a_message_it_would_misread_are_refused()
    {
        Assert.Equal(NoLambdas, ManagedDebugger.Unreadable("items.Any(item => item > 4)", null, null));
        Assert.Equal(NoLambdas, ManagedDebugger.Unreadable("count > 1 && items.Where((item, at) => at > count).Any()", "==2", null));
        Assert.Equal(NoLambdas, ManagedDebugger.Unreadable("Run(delegate { return 1; }) == 1", null, null));
        Assert.Contains("The hit count \"abc\" is not one the debugger can read", ManagedDebugger.Unreadable("count > 1", "abc", null), StringComparison.Ordinal);
        Assert.Equal(DisplayFormat.Misread("{{count}}"), ManagedDebugger.Unreadable("count > 1", ">=3", "{{count}}"));
        Assert.Null(ManagedDebugger.Unreadable("count > 1 && name == \"=>\"", ">=3", "\\{count\\} is {count}"));
        Assert.Null(ManagedDebugger.Unreadable(null, null, null));
    }

    /// <summary>A condition nested past what the parser reads, by interpolations (each hole a level of the lexer, which
    /// checks no stack) or by brackets, is refused saying so, on a stack a parse of it would run out of; what reads an
    /// expression of its own (its LINQ, its operand, its writable forms) takes it as unread; one nested short of that is read.</summary>
    [Fact]
    public void A_condition_nested_past_what_the_parser_reads_is_refused_saying_so_and_no_reader_of_it_runs_out_of_stack()
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
    }

    /// <summary>One asked for again just as it was is kept, once; one in its place counts on from its hits while its condition
    /// and hit count stay (its message, whether it stops or Run to Cursor's hold edited, a hit count written another way),
    /// and counts again from the edit where its condition or hit count changed; one at a new place counts from none.</summary>
    [Fact]
    public void A_breakpoint_asked_for_again_is_kept_whole_counts_on_through_an_edit_of_its_message_and_again_from_an_edit_of_its_count_or_condition()
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
    }

    /// <summary>A hit count counts only the hits where the condition held: i &gt; 10 with ==4 stops at i 14 alone, the hits
    /// before eleven never counted; with no condition every hit counts.</summary>
    [Fact]
    public async Task A_hit_count_counts_only_the_hits_where_the_condition_held()
    {
        int[] counted = await StopsAsync(Set(1, new BreakpointRequest(5, Condition: "i > 10", HitCondition: "==4"), hits: 0), i => i > 10);
        int[] every = await StopsAsync(Set(1, new BreakpointRequest(5, HitCondition: "==4"), hits: 0), _ => true);
        int[] second = await StopsAsync(Set(1, new BreakpointRequest(5, Condition: "i > 10", HitCondition: "%2"), hits: 0), i => i is > 10 and < 18);
        Assert.Equal([14], counted);
        Assert.Equal([3], every);
        Assert.Equal([12, 14, 16], second);
    }

    /// <summary>A condition that cannot be told is taken as held once its hit count is met: the hit stops, a tracepoint's too,
    /// its message logged first, and the breakpoint and the console say why; a hit before the count says nothing.</summary>
    [Fact]
    public async Task A_condition_that_cannot_be_told_stops_its_counted_hit_saying_why_a_tracepoints_too()
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
        Assert.Equal(why, tracepoint.Message);
        Assert.Equal([tracepoint], told);
    }

    /// <summary>Held for Run to Cursor, a breakpoint stops at its next hit whatever its condition and hit count say, the hit
    /// counted only where its condition held; one that neither stops nor logs says where it was reached and goes on.</summary>
    [Fact]
    public async Task Run_to_Cursors_hold_stops_the_next_hit_and_a_breakpoint_neither_stopping_nor_logging_says_where_it_was_reached()
    {
        BreakpointManager.BreakpointInfo held = Set(1, new BreakpointRequest(5, Condition: "i > 10", HitCondition: "==4", RunTo: true), hits: 2);
        List<string> said = [];
        Assert.True(await BreakpointHits.StopsAsync(held, Reading(3, i => new ConditionRead(i > 10), said, [])), "Run to Cursor's hold did not stop where the condition is false");
        Assert.Equal(2, held.HitCount);
        Assert.True(await BreakpointHits.StopsAsync(held, Reading(11, i => new ConditionRead(i > 10), said, [])), "Run to Cursor's hold did not stop where the hit count is not met");
        Assert.Equal(3, held.HitCount);

        BreakpointManager.BreakpointInfo reached = Set(2, new BreakpointRequest(5), hits: 0);
        reached.Suspend = false;
        Assert.False(await BreakpointHits.StopsAsync(reached, Reading(0, _ => new ConditionRead(true), said, [])), "A breakpoint that does not suspend stopped");
        Assert.Equal(["Breakpoint reached at Loop.cs:5"], said);
    }

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

    /// <summary>A hit at turn <paramref name="i"/>, its condition read as <paramref name="holds"/>, a message's {i} as i.</summary>
    private static HitReading Reading(int i, Func<int, ConditionRead> holds, List<string> said, List<IHitBreakpoint> told) => new()
    {
        Named = "the breakpoint",
        Holds = _ => Task.FromResult(holds(i)),
        Logged = message => BreakpointHits.LoggedAsync(message, _ => Task.FromResult(DisplayValue.OfWhole(i, sizeof(int)))),
        Reached = () => BreakpointHits.Reached("/src/Loop.cs", 5),
        Say = said.Add,
        Told = told.Add,
    };

    private static BreakpointManager.BreakpointInfo Set(int id, BreakpointRequest request, int hits) =>
        new() { Id = id, Line = request.Line, Condition = request.Condition, HitCondition = request.HitCondition, Request = request, HitCount = hits };
}
