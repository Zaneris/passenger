using Passenger.App.Debugging;

namespace Passenger.UnitTests.Debugger;

/// <summary>The text that tells a breakpoint change worth sending to the debugger: a breakpoint's line, attribute, column,
/// condition, hit count, log message and whether it stops, and a file's breakpoints as a list of those.</summary>
public class SignaturesTests
{
    [Fact]
    public void Two_breakpoints_differing_only_in_where_a_bar_falls_between_condition_and_hit_count_have_different_signatures()
    {
        string one = Breakpoint(condition: "a ||", hitCount: "b");
        string other = Breakpoint(condition: "a |", hitCount: "|b");
        Assert.True(one != other, $"Both read {one}: the change between them would never reach the debugger");
        Assert.Equal(Breakpoint(condition: "a ||", hitCount: "b"), one);
    }

    [Fact]
    public void A_condition_holding_what_parts_a_files_breakpoints_never_passes_for_two_breakpoints()
    {
        string one = Signatures.Of([Breakpoint(condition: $"x;{Breakpoint(condition: "y")}")]);
        string two = Signatures.Of([Breakpoint(condition: "x"), Breakpoint(condition: "y")]);
        Assert.True(one != two, $"One breakpoint and two read alike, {one}: the change between them would never reach the debugger");
    }

    [Fact]
    public void A_field_left_out_is_told_apart_from_one_left_empty()
    {
        Assert.NotEqual(Breakpoint(condition: null), Breakpoint(condition: string.Empty));
    }

    /// <summary>A line breakpoint's fields as the session gives them.</summary>
    private static string Breakpoint(string? condition, string? hitCount = null) =>
        Signatures.Of("12", "Text", null, condition, hitCount, null, true);
}
