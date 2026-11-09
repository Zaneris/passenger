using Passenger.Debugger;
using Passenger.Debugger.ExpressionEvaluator.Compiler;

namespace Passenger.UnitTests.Debugger;

/// <summary>What every kind of breakpoint shares when it is set: what the debugger refuses to read, and how those asked for
/// are matched with those already set.</summary>
public class BreakpointKindsTests
{
    private const string NoLambdas = "Lambda expressions are not supported in breakpoint conditions.";

    [Fact]
    public void A_condition_holding_a_lambda_anywhere_and_a_hit_count_the_debugger_cannot_read_are_refused()
    {
        Assert.Equal(NoLambdas, ManagedDebugger.Unreadable("items.Any(item => item > 4)", null));
        Assert.Equal(NoLambdas, ManagedDebugger.Unreadable("count > 1 && items.Where((item, at) => at > count).Any()", "==2"));
        Assert.Equal(NoLambdas, ManagedDebugger.Unreadable("Run(delegate { return 1; }) == 1", null));
        Assert.Contains("The hit count \"abc\" is not one the debugger can read", ManagedDebugger.Unreadable("count > 1", "abc"), StringComparison.Ordinal);
        Assert.Null(ManagedDebugger.Unreadable("count > 1 && name == \"=>\"", ">=3"));
        Assert.Null(ManagedDebugger.Unreadable(null, null));
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
            Assert.Equal("The condition nests deeper than the debugger reads.", ManagedDebugger.Unreadable(holes, null));
            Assert.Equal("The condition nests deeper than the debugger reads.", ManagedDebugger.Unreadable(brackets, "==2"));
            Assert.False(CilExpressionCompiler.NamesLinq(holes), "An expression too deep to read was read as naming LINQ");
            Assert.Equal($"({brackets})", RowExpressions.Operand(brackets));
            Assert.Empty(WritableTargets.Of(brackets, _ => false));
        });
        Assert.Null(ManagedDebugger.Unreadable(new string('(', 900) + "true" + new string(')', 900), null));
    }

    [Fact]
    public void A_breakpoint_asked_for_again_just_as_it_was_is_kept_once_and_a_new_one_at_a_place_counts_on_from_the_hits_of_the_one_it_replaces()
    {
        BreakpointManager.BreakpointInfo same = Set(1, new BreakpointRequest(5, Condition: "x > 1"), hits: 3);
        BreakpointManager.BreakpointInfo changed = Set(2, new BreakpointRequest(9, HitCondition: "==3"), hits: 2);
        BreakpointManager.BreakpointInfo removed = Set(3, new BreakpointRequest(12), hits: 7);
        BreakpointRequest[] asked = [new(5, Condition: "x > 1"), new(5, Condition: "x > 1"), new(9, HitCondition: "==5"), new(20)];

        (BreakpointManager.BreakpointInfo? Kept, int Hits)[] matched = ManagedDebugger.Reconcile(asked, [same, changed, removed], b => b.Request,
            static (was, now) => was.Line == now.Line && was.Column == now.Column, out List<BreakpointManager.BreakpointInfo> gone);

        Assert.Same(same, matched[0].Kept);
        Assert.True(matched[1] == (null, 0), $"A second request just as the first took the one breakpoint again, or its hits: {matched[1]}");
        Assert.True(matched[2] == (null, 2), $"The breakpoint changed at line 9 did not count on from the 2 hits of the one it replaced: {matched[2]}");
        Assert.True(matched[3] == (null, 0), $"A breakpoint at a new place took hits: {matched[3]}");
        Assert.Equal([changed, removed], gone);
    }

    private static BreakpointManager.BreakpointInfo Set(int id, BreakpointRequest request, int hits) =>
        new() { Id = id, Line = request.Line, Condition = request.Condition, HitCondition = request.HitCondition, Request = request, HitCount = hits };
}
