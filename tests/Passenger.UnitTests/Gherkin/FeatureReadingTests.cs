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
    public void A_feature_file_is_read_as_Reqnrolls_reader_reads_it_and_its_mistakes_placed_where_the_editor_shows_them() => Cases.All(
        ("each step takes the kind of the step before it through And But and the asterisk", () =>
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
        }),
        ("a step's keyword and text are placed on its line", () =>
        {
            FeatureStep and = FeatureReading.Read(Calculator, "en").Steps[2];

            Assert.Equal((2, "And "), (and.KeywordColumn, and.Keyword));
            Assert.Equal(6, and.TextColumn);
            Assert.Equal(StepKeywordKind.Conjunction, and.KeywordKind);
        }),
        ("a scenario takes the feature's tags with its own and its examples keep theirs", () =>
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
        }),
        ("background step's belong to the background", () =>
        {
            FeatureReading reading = FeatureReading.Read(Calculator, "en");

            Assert.Equal(ScenarioKind.Background, reading.Steps[0].Scenario.Kind);
            Assert.Equal("Calculator", reading.Feature!.Title);
        }),
        ("a doc string and a table are the step arguments before the next step", () =>
        {
            FeatureReading reading = FeatureReading.Read(Calculator, "en");
            FeatureStep then = reading.Steps[^1];

            Assert.Equal("{\"sum\": <sum>}", then.DocString!.Content);
            Assert.Equal("json", then.DocString.ContentType);
            Assert.Equal((19, 21), (then.DocString.Line, then.DocString.EndLine));
            Assert.Null(reading.Steps[^2].DocString);
        }),
        ("an outline step's placeholders are listed with their columns", () =>
        {
            FeatureStep step = FeatureReading.Read(Calculator, "en").Steps[^1];

            Assert.Equal([("sum", 28)], step.Placeholders.Select(p => (p.Name, p.Column)));
        }),
        ("a scenario is an outline where it has examples whatever its keyword as Reqnroll's parser builds it", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "Feature: F\n\nExample: With\n  Given <a>\n\n  Examples:\n    | a |\n    | 1 |\n\n" +
                "Scenario Outline: Without\n  Given <a>\n\nScenario: Plain\n  Given one\n", "en");

            Assert.Equal(
                [("With", ScenarioKind.Outline), ("Without", ScenarioKind.Scenario), ("Plain", ScenarioKind.Scenario)],
                reading.Scenarios.Select(s => (s.Title, s.Kind)));
            Assert.Empty(reading.Errors);
        }),
        ("a file in german reads its keywords in german", () =>
        {
            FeatureReading reading = FeatureReading.Read(
                "# language: de\nFunktionalität: Rechner\n\nSzenario: Addieren\n  Angenommen eine Zahl 5\n  Und eine Zahl 6\n  Wenn ich addiere\n  Dann ist das Ergebnis 11\n", "en");

            Assert.Equal("de", reading.Dialect.Language);
            Assert.Equal([StepKind.Given, StepKind.Given, StepKind.When, StepKind.Then], reading.Steps.Select(s => s.Kind));
            Assert.Equal("Funktionalität", reading.Feature!.Keyword);
        }),
        ("a mistake is reported and what reads is kept", () =>
        {
            FeatureReading reading = FeatureReading.Read("Feature: X\n\nScenario: A\n  Given one\n  | a |\n  Banana\nScenario: B\n  Then two\n", "en");

            FeatureError error = Assert.Single(reading.Errors);
            Assert.Equal(5, error.Line);
            Assert.Contains("expected", error.Message, StringComparison.Ordinal);
            Assert.Equal(["one", "two"], reading.Steps.Select(s => s.Text));
        }),
        ("a file its reader finds no feature in is one mistake where the feature was to begin", () =>
        {
            FeatureReading reading = FeatureReading.Read(Calculator, "de-AT");

            Assert.Null(reading.Feature);
            FeatureError none = Assert.Single(reading.Errors);
            Assert.Equal((1, 0, true, false), (none.Line, none.Column, none.NoFeature, none.ByGenerator));
            Assert.Equal("de", reading.Dialect.Language);
            Assert.Equal(["Funktionalität", "Funktion"], reading.Dialect.FeatureKeywords);
            Assert.StartsWith("expected: #TagLine, #FeatureLine, #Comment, #Empty, got 'Feature: Calculator'", none.Message, StringComparison.Ordinal);

            FeatureReading unknown = FeatureReading.Read("# language: xx\nFunktionalität: F\n\nSzenario: S\n  Angenommen eins\n", "en");
            Assert.Equal([(0, false, "Language not supported: xx"), (1, true, "expected: #EOF, #Language, #TagLine, #FeatureLine, #Comment, #Empty, got 'Funktionalität: F'")],
                unknown.Errors.Select(e => (e.Line, e.NoFeature, e.Message)));
            Assert.Equal(1, Assert.Single(FeatureReading.Read("@tagged\n", "en").Errors, e => e.NoFeature).Line);
            Assert.DoesNotContain(FeatureReading.Read("Feature: F\nScenario: S\n  Given one\n  Banana\n", "en").Errors, e => e.NoFeature);
        }),
        ("a rule's scenarios take its tags and name it", () =>
        {
            FeatureReading reading = FeatureReading.Read("Feature: X\n\n@r\nRule: one rule\n\n  Scenario: A\n    Given one\n", "en");

            FeatureScenario scenario = Assert.Single(reading.Scenarios);
            Assert.Equal("one rule", scenario.Rule!.Title);
            Assert.Equal(["@r"], scenario.AllTags);
        }),
        ("a file of crlf lines reads as one of lf lines", () =>
        {
            FeatureReading crlf = FeatureReading.Read(Calculator.Replace("\n", "\r\n", StringComparison.Ordinal), "en");
            FeatureReading lf = FeatureReading.Read(Calculator, "en");

            Assert.Equal(lf.Steps.Select(s => (s.Line, s.TextColumn, s.Text)), crlf.Steps.Select(s => (s.Line, s.TextColumn, s.Text)));
            Assert.Equal(lf.Steps[^1].DocString!.Content, crlf.Steps[^1].DocString!.Content);
            Assert.Equal(lf.Folds(), crlf.Folds());
            Assert.Empty(crlf.Errors);
        }),
        ("an unknown language or a tag holding a space is a mistake said where Gherkin says it and the rest reads", () =>
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
        }),
        ("a language line with a region Gherkin lacks reads in its language as Reqnroll's reader does", () =>
        {
            FeatureReading reading = FeatureReading.Read("# language: de-AT\nFunktionalität: Rechner\n\nSzenario: Addieren\n  Angenommen eine Zahl 5\n", "en");

            Assert.Empty(reading.Errors);
            Assert.Equal(("de-AT", "Funktionalität", StepKind.Given), (reading.Dialect.Language, reading.Feature?.Keyword, Assert.Single(reading.Steps).Kind));
            Assert.Equal("Language not supported: xx", Assert.Single(FeatureReading.Read("# language: xx-YY\nFeature: F\n", "en").Errors).Message);
        }),
        ("a mistake after a lone carriage return is placed on the editor's line", () =>
        {
            FeatureReading reading = FeatureReading.Read("Feature: F\r# a note\nScenario: A\n  Given one\n  | a |\n  Banana\n", "en");

            Assert.Equal(4, Assert.Single(reading.Errors).Line);
        }),
        ("a doc string's lines are kept between its own delimiters and one left open runs to the end", () =>
        {
            Assert.Equal([4, 5, 9], DocStringLines(
                "Feature: X\n  Scenario: Y\n    Given text\n      \"\"\"json\n      {\"a\": 1}\n        ```\n      \"\"\"\n    Then more\n      ```\n      one\n      ```\n"));
            Assert.Equal([2, 3, 4], DocStringLines("Given a\n  \"\"\"\n  open\n  still\n"));
            Assert.Empty(DocStringLines("Given a \"\"\" in a step\n  | \"\"\" |\n"));
        }));

    private static int[] DocStringLines(string text) => [.. FeatureReading.DocStringLines(Passenger.Text.TextSnapshot.From(text)).Order()];
}
