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
    public void A_cucumber_expressions_parameters_are_holes_named_by_their_types()
    {
        StepSample sample = Sample("I have {int} cucumber(s) in my belly/stomach", "M", new StepParameter("count", "System.Int32"));

        Assert.Equal("I have [int] cucumbers in my belly", sample.Text);
        Assert.Equal(["[int]"], Holes(sample));
    }

    [Fact]
    public void A_string_parameter_is_a_hole_between_quotes()
    {
        StepSample sample = Sample("I enter {string} in the {word} field", "M", new StepParameter("v", "System.String"), new StepParameter("f", "System.String"));

        Assert.Equal("I enter \"[string]\" in the [word] field", sample.Text);
        Assert.Equal(["[string]", "[word]"], Holes(sample));
    }

    [Fact]
    public void A_regular_expressions_groups_are_holes_named_by_their_parameters_types_and_its_escapes_their_characters()
    {
        StepSample sample = Sample(@"^the price is (\d+)\.(\d+) (?:euro|dollar)s$", "M", new StepParameter("whole", "System.Int32"), new StepParameter("cents", "System.Int64"));

        Assert.Equal("the price is [int].[long] euros", sample.Text);
        Assert.Equal(["[int]", "[long]"], Holes(sample));
    }

    [Fact]
    public void A_choice_of_words_shows_its_first_and_a_pattern_too_free_to_write_is_given_as_written()
    {
        Assert.Equal("I press [string]", Sample("I press (.*)", "M", new StepParameter("button", "System.String")).Text);
        Assert.Equal("I go home", Sample("^I go (home|away)$", "M").Text);
        Assert.Null(StepSamples.Of(new StepBinding(BindingKind.Given, "^a+ b*$", "M", "T", [])));
    }

    [Fact]
    public void A_method_named_for_its_step_is_written_as_its_words()
    {
        Assert.Equal("the first number is [int]", Sample(null, "Given_the_first_number_is_COUNT", new StepParameter("count", "System.Int32")).Text);
    }
}
