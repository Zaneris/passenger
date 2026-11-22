using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>What completion writes for a step definition: its expression's words, each parameter a hole named by its type
/// for the person to fill, an optional part left out and an alternation's first choice.</summary>
public sealed class StepSamplesTests
{
    private static StepSample Sample(string? expression, string method = "M", params StepParameter[] parameters) =>
        StepSamples.Of(new StepBinding(BindingKind.Given, expression, method, "T", parameters))!;

    private static string[] Holes(StepSample sample) => [.. sample.Holes.Select(h => sample.Text.Substring(h.Start, h.Length))];

    [Fact]
    public void A_definitions_expression_is_shown_as_a_step_with_holes_for_its_parameters() => Cases.All(
        ("a cucumber expression's parameters are holes named by their types", () =>
        {
            StepSample sample = Sample("I have {int} cucumber(s) in my belly/stomach", "M", new StepParameter("count", "System.Int32"));

            Assert.Equal("I have [int] cucumbers in my belly", sample.Text);
            Assert.Equal(["[int]"], Holes(sample));
        }),
        ("a string parameter is a hole between quotes", () =>
        {
            StepSample sample = Sample("I enter {string} in the {word} field", "M", new StepParameter("v", "System.String"), new StepParameter("f", "System.String"));

            Assert.Equal("I enter \"[string]\" in the [word] field", sample.Text);
            Assert.Equal(["[string]", "[word]"], Holes(sample));
        }),
        ("a regular expression's groups are holes named by their parameters types and its escapes their characters", () =>
        {
            StepSample sample = Sample(@"^the price is (\d+)\.(\d+) (?:euro|dollar)s$", "M", new StepParameter("whole", "System.Int32"), new StepParameter("cents", "System.Int64"));

            Assert.Equal("the price is [int].[long] euros", sample.Text);
            Assert.Equal(["[int]", "[long]"], Holes(sample));
        }),
        ("a choice of words shows its first and a pattern too free to write is given as written", () =>
        {
            Assert.Equal("I press [string]", Sample("I press (.*)", "M", new StepParameter("button", "System.String")).Text);
            Assert.Equal("I go home", Sample("^I go (home|away)$", "M").Text);
            Assert.Null(StepSamples.Of(new StepBinding(BindingKind.Given, "^a+ b*$", "M", "T", [])));
        }),
        ("a method named for its step is written as its words", () =>
        {
            Assert.Equal("the first number is [int]", Sample(null, "Given_the_first_number_is_COUNT", new StepParameter("count", "System.Int32")).Text);
        }));

}
