using Passenger.App.Debugging;

namespace Passenger.UnitTests.Debugger;

/// <summary>The text that tells a breakpoint change worth sending to the debugger: a breakpoint's line, attribute, column,
/// condition, hit count, log message and whether it stops, and a file's breakpoints as a list of those.</summary>
public class SignaturesTests
{
    [Fact]
    public void A_breakpoint_change_always_changes_its_signature() => Cases.All(
        ("two breakpoints differing only in where a bar falls between condition and hit count have different signatures", () =>
        {
            string one = Breakpoint(condition: "a ||", hitCount: "b");
            string other = Breakpoint(condition: "a |", hitCount: "|b");
            Assert.True(one != other, $"Both read {one}: the change between them would never reach the debugger");
            Assert.Equal(Breakpoint(condition: "a ||", hitCount: "b"), one);
        }),
        ("a condition holding what parts a file's breakpoints never passes for two breakpoints", () =>
        {
            string one = Signatures.Of([Breakpoint(condition: $"x;{Breakpoint(condition: "y")}")]);
            string two = Signatures.Of([Breakpoint(condition: "x"), Breakpoint(condition: "y")]);
            Assert.True(one != two, $"One breakpoint and two read alike, {one}: the change between them would never reach the debugger");
        }),
        ("a field left out is told apart from one left empty", () =>
        {
            Assert.NotEqual(Breakpoint(condition: null), Breakpoint(condition: string.Empty));
        }));

    /// <summary>A line breakpoint's fields as the session gives them.</summary>
    private static string Breakpoint(string? condition, string? hitCount = null) =>
        Signatures.Of("12", "Text", null, condition, hitCount, null, true);
}
