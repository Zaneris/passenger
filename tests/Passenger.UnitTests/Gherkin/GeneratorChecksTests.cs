using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>What Reqnroll's code-behind generator refuses a feature for, read as the feature's mistakes in its words at the
/// places its build names: each feature here was built against Reqnroll 3.3.4, whose errors are the ones expected.</summary>
public sealed class GeneratorChecksTests
{
    private const string Refused =
        "Feature: Probe\n" +
        "\n" +
        "Scenario Outline: Header only\n" +
        "  Given a number <a>\n" +
        "\n" +
        "  Examples:\n" +
        "    | a |\n" +
        "\n" +
        "Scenario Outline: Twice named\n" +
        "  Given a number <a>\n" +
        "\n" +
        "  Examples: one\n" +
        "    | a |\n" +
        "    | 1 |\n" +
        "\n" +
        "  Examples: one\n" +
        "    | a |\n" +
        "    | 2 |\n" +
        "\n" +
        "Scenario Outline: Ignored twice\n" +
        "  Given a number <a>\n" +
        "\n" +
        "  Examples: two\n" +
        "    | a |\n" +
        "    | 1 |\n" +
        "\n" +
        "  @ignore\n" +
        "  Examples: two\n" +
        "    | a |\n" +
        "    | 2 |\n" +
        "\n" +
        "Scenario Outline: Header twice\n" +
        "  Given a number <a>\n" +
        "\n" +
        "  Examples:\n" +
        "    | a | a | b | b |\n" +
        "    | 1 | 1 | 2 | 2 |\n" +
        "\n" +
        "Scenario: Same\n" +
        "  Given a number 1\n" +
        "\n" +
        "Rule: R\n" +
        "\n" +
        "Scenario: Same\n" +
        "  Given a number 2\n" +
        "\n" +
        "Scenario: Same\n" +
        "  Given a number 3\n" +
        "\n" +
        "Scenario: With empty examples\n" +
        "  Given a number 1\n" +
        "\n" +
        "  Examples:\n";

    [Fact]
    public void Each_refusal_of_the_generator_is_a_mistake_in_its_words_where_its_build_places_it()
    {
        FeatureReading reading = FeatureReading.Read(Refused, "en");

        Assert.Equal(
            [
                (43, 0, "Feature file already contains a scenario with name 'Same'"),
                (15, 2, "Scenario Outline 'Twice named' already contains an example with name 'one'"),
                (27, 2, "Scenario Outline 'Ignored twice' already contains an example with name 'two'"),
                (2, 0, "Scenario Outline 'Header only' has no examples defined"),
                (49, 0, "Scenario Outline 'With empty examples' has no examples defined"),
                (31, 0, "Scenario Outline 'Header twice' already contains an example column with header 'a'"),
                (31, 0, "Scenario Outline 'Header twice' already contains an example column with header 'b'"),
            ],
            reading.Errors.Select(e => (e.Line, e.Column, e.Message)));
        Assert.All(reading.Errors, e => Assert.True(e.ByGenerator, $"\"{e.Message}\" should be said as the generator's, not Gherkin's"));
    }

    [Fact]
    public void A_feature_gherkin_finds_a_mistake_in_says_only_that_as_the_generator_reads_no_further()
    {
        FeatureReading reading = FeatureReading.Read(Refused + "    | 1 |\n  Given late\n", "en");

        FeatureError error = Assert.Single(reading.Errors);
        Assert.Equal((54, 2, false), (error.Line, error.Column, error.ByGenerator));
        Assert.StartsWith("expected: #EOF, #TableRow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_untitled_scenarios_are_refused_but_a_background_an_outline_with_no_examples_and_untitled_examples_are_not()
    {
        FeatureReading reading = FeatureReading.Read(
            "Feature: Probe\n" +
            "\n" +
            "Background: Same\n" +
            "  Given a number 0\n" +
            "\n" +
            "Scenario:\n" +
            "  Given a number 1\n" +
            "\n" +
            "Scenario:\n" +
            "  Given a number 2\n" +
            "\n" +
            "Scenario Outline: Plain\n" +
            "  Given a number 3\n" +
            "\n" +
            "Scenario Outline: Untitled examples\n" +
            "  Given a number <a>\n" +
            "\n" +
            "  Examples:\n" +
            "    | a |\n" +
            "    | 1 |\n" +
            "\n" +
            "  Examples:   \n" +
            "    | a |\n" +
            "    | 2 |\n" +
            "\n" +
            "Rule: R\n" +
            "\n" +
            "Background: Same\n" +
            "  Given a number 4\n" +
            "\n" +
            "Scenario: Same\n" +
            "  Given a number 5\n", "en");

        FeatureError error = Assert.Single(reading.Errors);
        Assert.Equal((8, 0, "Feature file already contains a scenario with name ''"), (error.Line, error.Column, error.Message));
    }
}
