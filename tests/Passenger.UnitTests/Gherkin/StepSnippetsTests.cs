using System.Globalization;
using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A missing step's definition written as Reqnroll's editors write one: its keyword's attribute with a Cucumber
/// Expression (or a regular expression, as the project's skeleton style says), quoted text and numbers made parameters, an
/// outline's placeholders parameters named for them, a doc string and a table taken last, a pending body.</summary>
public sealed class StepSnippetsTests
{
    private static StepSnippet Snippet(string feature, SkeletonStyle style = SkeletonStyle.CucumberExpression, bool specFlow = false, int step = 0)
    {
        FeatureReading reading = FeatureReading.Read(feature, "en");
        return StepSnippets.Of(reading.Steps[step], reading, style, specFlow, CultureInfo.InvariantCulture, []);
    }

    [Fact]
    public void Numbers_and_quoted_text_become_typed_parameters_of_a_cucumber_expression()
    {
        StepSnippet snippet = Snippet("Feature: F\nScenario: S\n  Given I have entered 50 into the \"main\" calculator\n");

        Assert.Equal(
            "[Given(\"I have entered {int} into the {string} calculator\")]\n" +
            "public void GivenIHaveEnteredIntoTheCalculator(int p0, string main)\n" +
            "{\n" +
            "    throw new PendingStepException();\n" +
            "}\n",
            snippet.Code);
        Assert.Equal("GivenIHaveEnteredIntoTheCalculator", snippet.Method);
    }

    [Fact]
    public void A_regular_expression_style_writes_groups_in_a_verbatim_string()
    {
        StepSnippet snippet = Snippet("Feature: F\nScenario: S\n  When I pay 3.50 for \"a b\"\n", SkeletonStyle.Regex);

        Assert.Equal(
            "[When(@\"I pay (.*) for \"\"([^\"\"]*)\"\"\")]\n" +
            "public void WhenIPayFor(decimal p0, string p1)\n" +
            "{\n" +
            "    throw new PendingStepException();\n" +
            "}\n",
            snippet.Code);
    }

    [Fact]
    public void The_async_style_returns_a_task_and_names_the_method_async()
    {
        Assert.StartsWith("[Then(\"it works\")]\npublic async Task ThenItWorksAsync()\n",
            Snippet("Feature: F\nScenario: S\n  Then it works\n", SkeletonStyle.AsyncCucumberExpression).Code, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outlines_placeholders_become_parameters_named_for_them_typed_by_their_examples()
    {
        StepSnippet snippet = Snippet("Feature: F\nScenario Outline: O\n  Given <name> has <count> apples\n\nExamples:\n  | name  | count |\n  | Ann   | 1     |\n  | Bob   | 22    |\n");

        Assert.StartsWith("[Given(\"{word} has {int} apples\")]\npublic void GivenHasApples(string name, int count)\n", snippet.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_doc_string_and_a_table_are_taken_last_and_and_steps_take_the_kind_before_them()
    {
        StepSnippet snippet = Snippet("Feature: F\nScenario: S\n  Given one\n  And the users\n    | name |\n    | Ann  |\n", step: 1);
        StepSnippet docString = Snippet("Feature: F\nScenario: S\n  When I post\n    \"\"\"\n    {}\n    \"\"\"\n", SkeletonStyle.Regex, specFlow: true);

        Assert.StartsWith("[Given(\"the users\")]\npublic void GivenTheUsers(DataTable dataTable)\n", snippet.Code, StringComparison.Ordinal);
        Assert.StartsWith("[When(@\"I post\")]\npublic void WhenIPost(string multilineText)\n", docString.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cucumber_expressions_special_characters_are_escaped_and_a_taken_name_is_numbered()
    {
        FeatureReading reading = FeatureReading.Read("Feature: F\nScenario: S\n  Given a/b (c) {d}\n", "en");

        StepSnippet snippet = StepSnippets.Of(reading.Steps[0], reading, SkeletonStyle.CucumberExpression, false, CultureInfo.InvariantCulture, ["GivenABCD"]);

        Assert.StartsWith("[Given(\"a\\\\/b \\\\(c) \\\\{d}\")]\npublic void GivenABCD2()\n", snippet.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_named_as_a_csharp_keyword_is_a_verbatim_parameter()
    {
        StepSnippet snippet = Snippet("Feature: F\nScenario Outline: O\n  Given the <class> list\n\nExamples:\n  | class |\n  | a     |\n");

        Assert.Contains("(string @class)", snippet.Code, StringComparison.Ordinal);
    }
}
