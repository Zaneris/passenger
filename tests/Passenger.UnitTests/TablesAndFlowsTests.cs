using Xunit.Sdk;

namespace Passenger.UnitTests;

/// <summary>The two ways one test holds many checks (tests/Shared): a table runs every row and names each that failed, and
/// a flow names the step that failed after those that passed.</summary>
public sealed class TablesAndFlowsTests
{
    [Fact]
    public async Task A_table_runs_every_row_naming_each_that_failed_and_a_flow_names_its_failed_step_after_those_passed()
    {
        List<int> ran = [];
        Exception several = Assert.ThrowsAny<Exception>(() => Cases.Each([(1, "one"), (2, "two"), (3, "three")], row =>
        {
            ran.Add(row.Item1);
            Assert.True(row.Item1 == 1, $"{row.Item2} is no one");
        }));
        Assert.True(ran.SequenceEqual([1, 2, 3]), $"The table ran rows {string.Join(", ", ran)}, so a failed row hid those after it");
        string said = Said(several);
        Assert.True(said.Contains("(2, \"two\") failed\ntwo is no one", StringComparison.Ordinal)
            && said.Contains("(3, \"three\")", StringComparison.Ordinal) && !said.Contains("(1, \"one\")", StringComparison.Ordinal),
            $"A table's failure did not name each row that failed, and only those, with what it found: {said}");

        Exception one = Assert.ThrowsAny<Exception>(() => Cases.Each(["kept", "lost"], row => Assert.Equal("kept", row), row => $"the {row} row"));
        Assert.True(Said(one).Contains("the lost row", StringComparison.Ordinal), $"A table's one failed row went unnamed: {Said(one)}");
        Assert.ThrowsAny<Exception>(() => Cases.Each(Array.Empty<int>(), _ => { }));
        Exception waited = await Assert.ThrowsAnyAsync<Exception>(() => Cases.EachAsync([1, 2], async row =>
        {
            await Task.Yield();
            Assert.Equal(1, row);
        }));
        Assert.True(Said(waited).Contains("The row 2 failed", StringComparison.Ordinal), $"A waiting table's failed row went unnamed: {Said(waited)}");

        Flow.Step("the first step holds", () => { });
        Assert.Equal(4, Flow.Step("a step hands on what it made", () => 4));
        await Flow.StepAsync("a step that waits holds", async () => await Task.Yield());
        Exception step = Assert.ThrowsAny<Exception>(() => Flow.Step("the fourth step holds", () => Assert.Equal(1, 2)));
        Assert.True(Said(step).Contains("step 4 failed, so this no longer holds: the fourth step holds", StringComparison.Ordinal)
            && Said(step).Contains("(the step before it: a step that waits holds)", StringComparison.Ordinal),
            $"A flow's failure did not name its step and the steps before it: {Said(step)}");
    }

    /// <summary>An exception's words with those of each it carries, as a runner writes them.</summary>
    private static string Said(Exception failure) => failure switch
    {
        MultipleException several => string.Join("\n", [several.Message, .. several.InnerExceptions.Select(Said)]),
        { InnerException: { } inner } => $"{failure.Message}\n{Said(inner)}",
        _ => failure.Message,
    };
}
