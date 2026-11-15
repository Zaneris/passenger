using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

public sealed class FeatureReadingTests
{
    private const string Calculator =
        "@calc\n" +
        "Feature: Calculator\n" +
        "  Adds numbers\n" +
        "\n" +
        "Background:\n" +
        "  Given a calculator\n" +
        "\n" +
        "@fast\n" +
        "Scenario: Add two numbers\n" +
        "  Given the first number is 50\n" +
        "  And the second number is 70\n" +
        "  When the two numbers are added\n" +
        "  Then the result should be 120\n" +
        "  But no error is shown\n" +
        "\n" +
        "Scenario Outline: Add numbers\n" +
        "  Given the first number is <a>\n" +
        "  * the second number is <b>\n" +
        "  Then the result should be <sum>\n" +
        "    \"\"\"json\n" +
        "    {\"sum\": <sum>}\n" +
        "    \"\"\"\n" +
        "\n" +
        "  @slow\n" +
        "  Examples: small\n" +
        "    | a | b | sum |\n" +
        "    | 1 | 2 | 3   |\n" +
        "    | 5 | 5 | 10  |\n";

    [Fact]
    public void Each_step_takes_the_kind_of_the_step_before_it_through_And_But_and_the_asterisk()
    {
        FeatureReading reading = FeatureReading.Read(Calculator, "en");

        Assert.Equal(
            [
                (5, StepKind.Given, "a calculator"),
                (9, StepKind.Given, "the first number is 50"),
                (10, StepKind.Given, "the second number is 70"),
                (11, StepKind.When, "the two numbers are added"),
                (12, StepKind.Then, "the result should be 120"),
                (13, StepKind.Then, "no error is shown"),
                (16, StepKind.Given, "the first number is <a>"),
                (17, StepKind.Given, "the second number is <b>"),
                (18, StepKind.Then, "the result should be <sum>"),
            ],
            reading.Steps.Select(s => (s.Line, s.Kind, s.Text)));
    }

    [Fact]
    public void A_steps_keyword_and_text_are_placed_on_its_line()
    {
        FeatureStep and = FeatureReading.Read(Calculator, "en").Steps[2];

        Assert.Equal((2, "And "), (and.KeywordColumn, and.Keyword));
        Assert.Equal(6, and.TextColumn);
        Assert.Equal(StepKeywordKind.Conjunction, and.KeywordKind);
    }

    [Fact]
    public void A_scenario_takes_the_features_tags_with_its_own_and_its_examples_keep_theirs()
    {
        FeatureReading reading = FeatureReading.Read(Calculator, "en");
        FeatureScenario add = reading.Scenarios.Single(s => s.Title == "Add two numbers");
        FeatureScenario outline = reading.Scenarios.Single(s => s.Kind == ScenarioKind.Outline);

        Assert.Equal(["@calc", "@fast"], add.AllTags);
        Assert.Equal((8, 13), (add.Line, add.EndLine));
        Assert.Equal("small", outline.Examples.Single().Title);
        Assert.Equal(["@slow"], outline.Examples.Single().Tags.Select(t => t.Name));
        Assert.Equal(["a", "b", "sum"], outline.Examples.Single().Header!.Cells.Select(c => c.Value));
        Assert.Equal([26, 27], outline.Examples.Single().Rows.Select(r => r.Line));
    }

    [Fact]
    public void Background_steps_belong_to_the_background()
    {
        FeatureReading reading = FeatureReading.Read(Calculator, "en");

        Assert.Equal(ScenarioKind.Background, reading.Steps[0].Scenario.Kind);
        Assert.Equal("Calculator", reading.Feature!.Title);
    }

    [Fact]
    public void A_doc_string_and_a_table_are_the_step_arguments_before_the_next_step()
    {
        FeatureReading reading = FeatureReading.Read(Calculator, "en");
        FeatureStep then = reading.Steps[^1];

        Assert.Equal("{\"sum\": <sum>}", then.DocString!.Content);
        Assert.Equal("json", then.DocString.ContentType);
        Assert.Equal((19, 21), (then.DocString.Line, then.DocString.EndLine));
        Assert.Null(reading.Steps[^2].DocString);
    }

    [Fact]
    public void An_outline_steps_placeholders_are_listed_with_their_columns()
    {
        FeatureStep step = FeatureReading.Read(Calculator, "en").Steps[^1];

        Assert.Equal([("sum", 28)], step.Placeholders.Select(p => (p.Name, p.Column)));
    }

    [Fact]
    public void A_file_in_german_reads_its_keywords_in_german()
    {
        FeatureReading reading = FeatureReading.Read(
            "# language: de\nFunktionalität: Rechner\n\nSzenario: Addieren\n  Angenommen eine Zahl 5\n  Und eine Zahl 6\n  Wenn ich addiere\n  Dann ist das Ergebnis 11\n", "en");

        Assert.Equal("de", reading.Dialect.Language);
        Assert.Equal([StepKind.Given, StepKind.Given, StepKind.When, StepKind.Then], reading.Steps.Select(s => s.Kind));
        Assert.Equal("Funktionalität", reading.Feature!.Keyword);
    }

    [Fact]
    public void A_project_language_with_a_region_falls_back_to_its_language()
    {
        Assert.Equal("de", FeatureReading.Read("Funktionalität: X\n", "de-AT").Dialect.Language);
        Assert.Equal("en", FeatureReading.Read("Feature: X\n", "xx-unknown").Dialect.Language);
    }

    [Fact]
    public void A_mistake_is_reported_and_what_reads_is_kept()
    {
        FeatureReading reading = FeatureReading.Read("Feature: X\n\nScenario: A\n  Given one\n  | a |\n  Banana\nScenario: B\n  Then two\n", "en");

        FeatureError error = Assert.Single(reading.Errors);
        Assert.Equal(5, error.Line);
        Assert.Contains("expected", error.Message, StringComparison.Ordinal);
        Assert.Equal(["one", "two"], reading.Steps.Select(s => s.Text));
    }

    [Fact]
    public void A_file_with_no_feature_has_no_steps_and_says_why()
    {
        FeatureReading reading = FeatureReading.Read("Given one\n", "en");

        Assert.Null(reading.Feature);
        Assert.NotEmpty(reading.Errors);
    }

    [Fact]
    public void Every_language_gherkin_knows_is_listed()
    {
        Assert.Contains("de", GherkinDialects.Languages);
        Assert.Contains("en", GherkinDialects.Languages);
        Assert.True(GherkinDialects.Languages.Count > 60);
    }

    [Fact]
    public void A_rules_scenarios_take_its_tags_and_name_it()
    {
        FeatureReading reading = FeatureReading.Read("Feature: X\n\n@r\nRule: one rule\n\n  Scenario: A\n    Given one\n", "en");

        FeatureScenario scenario = Assert.Single(reading.Scenarios);
        Assert.Equal("one rule", scenario.Rule!.Title);
        Assert.Equal(["@r"], scenario.AllTags);
    }
}
