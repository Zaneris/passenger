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
    public void A_scenario_is_an_outline_where_it_has_examples_whatever_its_keyword_as_reqnrolls_parser_builds_it()
    {
        FeatureReading reading = FeatureReading.Read(
            "Feature: F\n\nExample: With\n  Given <a>\n\n  Examples:\n    | a |\n    | 1 |\n\n" +
            "Scenario Outline: Without\n  Given <a>\n\nScenario: Plain\n  Given one\n", "en");

        Assert.Equal(
            [("With", ScenarioKind.Outline), ("Without", ScenarioKind.Scenario), ("Plain", ScenarioKind.Scenario)],
            reading.Scenarios.Select(s => (s.Title, s.Kind)));
        Assert.Empty(reading.Errors);
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
    public void A_rules_scenarios_take_its_tags_and_name_it()
    {
        FeatureReading reading = FeatureReading.Read("Feature: X\n\n@r\nRule: one rule\n\n  Scenario: A\n    Given one\n", "en");

        FeatureScenario scenario = Assert.Single(reading.Scenarios);
        Assert.Equal("one rule", scenario.Rule!.Title);
        Assert.Equal(["@r"], scenario.AllTags);
    }

    [Fact]
    public void The_feature_its_rules_scenarios_examples_doc_strings_and_tables_of_several_rows_fold_from_their_first_line()
    {
        FeatureReading reading = FeatureReading.Read(Calculator, "en");

        Assert.Equal(
            [(1, 27), (4, 5), (8, 13), (15, 27), (19, 21), (24, 27)],
            reading.Folds().Select(f => (f.FirstLine, f.LastLine)));
    }

    [Fact]
    public void A_steps_text_starts_after_every_space_after_its_keyword()
    {
        FeatureStep step = FeatureReading.Read("Feature: F\nScenario: S\n    Given  two spaces <here>\n", "en").Steps[0];

        Assert.Equal((11, "two spaces <here>"), (step.TextColumn, step.Text));
        Assert.Equal([22], step.Placeholders.Select(p => p.Column));
    }

    [Fact]
    public void A_file_of_crlf_lines_reads_as_one_of_lf_lines()
    {
        FeatureReading crlf = FeatureReading.Read(Calculator.Replace("\n", "\r\n", StringComparison.Ordinal), "en");
        FeatureReading lf = FeatureReading.Read(Calculator, "en");

        Assert.Equal(lf.Steps.Select(s => (s.Line, s.TextColumn, s.Text)), crlf.Steps.Select(s => (s.Line, s.TextColumn, s.Text)));
        Assert.Equal(lf.Steps[^1].DocString!.Content, crlf.Steps[^1].DocString!.Content);
        Assert.Equal(lf.Folds(), crlf.Folds());
        Assert.Empty(crlf.Errors);
    }

    [Fact]
    public void An_unknown_language_or_a_tag_holding_a_space_is_a_mistake_said_where_gherkin_says_it_and_the_rest_reads()
    {
        FeatureReading unknown = FeatureReading.Read("# language: d\nFeature: F\n\nScenario: S\n  Given one\n", "en");
        FeatureError language = Assert.Single(unknown.Errors);
        Assert.Equal((0, 0, "Language not supported: d"), (language.Line, language.Column, language.Message));
        Assert.Equal(FeatureLineKind.Comment, unknown.Lines[0]);
        Assert.Equal(("en", "Feature", "one"), (unknown.Dialect.Language, unknown.Feature?.Keyword, Assert.Single(unknown.Steps).Text));

        FeatureReading spaced = FeatureReading.Read("Feature: F\n\n@fast @wip in progress\nScenario: S\n  Given one\n", "en");
        FeatureError tag = Assert.Single(spaced.Errors);
        Assert.Equal((2, 6, "A tag may not contain whitespace"), (tag.Line, tag.Column, tag.Message));
        Assert.Equal(["@fast"], spaced.Tags.Select(t => t.Name));
        Assert.Equal("one", Assert.Single(spaced.Steps).Text);

        Assert.Equal("# language: d\nFeature: F\n\nScenario: S\n    Given one\n",
            GherkinFormatter.Format("# language: d\nFeature: F\n\nScenario: S\n  Given one\n", GherkinFormatOptions.Default, "en"));
    }

    [Fact]
    public void A_language_line_with_a_region_gherkin_lacks_reads_in_its_language_as_reqnrolls_reader_does()
    {
        FeatureReading reading = FeatureReading.Read("# language: de-AT\nFunktionalität: Rechner\n\nSzenario: Addieren\n  Angenommen eine Zahl 5\n", "en");

        Assert.Empty(reading.Errors);
        Assert.Equal(("de-AT", "Funktionalität", StepKind.Given), (reading.Dialect.Language, reading.Feature?.Keyword, Assert.Single(reading.Steps).Kind));
        Assert.Equal("Language not supported: xx", Assert.Single(FeatureReading.Read("# language: xx-YY\nFeature: F\n", "en").Errors).Message);
    }

    [Fact]
    public void A_mistake_after_a_lone_carriage_return_is_placed_on_the_editors_line()
    {
        FeatureReading reading = FeatureReading.Read("Feature: F\r# a note\nScenario: A\n  Given one\n  | a |\n  Banana\n", "en");

        Assert.Equal(4, Assert.Single(reading.Errors).Line);
    }

    [Fact]
    public void A_doc_strings_lines_are_kept_between_its_own_delimiters_and_one_left_open_runs_to_the_end()
    {
        Assert.Equal([4, 5, 9], DocStringLines(
            "Feature: X\n  Scenario: Y\n    Given text\n      \"\"\"json\n      {\"a\": 1}\n        ```\n      \"\"\"\n    Then more\n      ```\n      one\n      ```\n"));
        Assert.Equal([2, 3, 4], DocStringLines("Given a\n  \"\"\"\n  open\n  still\n"));
        Assert.Empty(DocStringLines("Given a \"\"\" in a step\n  | \"\"\" |\n"));
    }

    private static int[] DocStringLines(string text) => [.. FeatureReading.DocStringLines(Passenger.Text.TextSnapshot.From(text)).Order()];
}
