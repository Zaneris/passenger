using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A feature laid out as Reqnroll's editors lay one out, by the <c>gherkin_*</c> editorconfig keys at their defaults
/// unless said: headers at the left, steps indented under their scenario, tables and doc strings under their step, an
/// examples table under its Examples, every table's columns aligned with numbers to the right.</summary>
public sealed class GherkinFormatterTests
{
    private static string Format(string text, GherkinFormatOptions? options = null) =>
        GherkinFormatter.Format(text, options ?? GherkinFormatOptions.Default, "en");

    [Fact]
    public void A_feature_is_laid_out_with_its_tables_aligned_keeping_every_value_and_line_break_as_written() => Cases.All(
        ("headers stay at the left steps are indented and tables aligned under them", () =>
        {
            string formatted = Format(
                "  @tag\n" +
                "    Feature: F\n" +
                "\n" +
                "   Scenario: S\n" +
                "Given the users\n" +
                "      |name|age|\n" +
                "  | Ann | 7 |\n" +
                "  | Bartholomew | 42 |\n" +
                "    And   more\n" +
                "Then done   \n");

            Assert.Equal(
                "@tag\n" +
                "Feature: F\n" +
                "\n" +
                "Scenario: S\n" +
                "    Given the users\n" +
                "        | name        | age |\n" +
                "        | Ann         |   7 |\n" +
                "        | Bartholomew |  42 |\n" +
                "    And more\n" +
                "    Then done\n",
                formatted);
        }),
        ("a doc string's lines keep their indentation within it", () =>
        {
            Assert.Equal(
                "Feature: F\nScenario: S\n    Given text\n        \"\"\"json\n        {\n          \"a\": 1\n        }\n        \"\"\"\n",
                Format("Feature: F\nScenario: S\nGiven text\n  \"\"\"json\n  {\n    \"a\": 1\n  }\n  \"\"\"\n"));
        }),
        ("the keys indent a feature's children and its Ands and set a cell's padding", () =>
        {
            GherkinFormatOptions options = GherkinFormatOptions.Default with { IndentFeatureChildren = true, IndentAndSteps = true, CellPadding = 0, RightAlignNumbers = false, Indent = "  " };

            Assert.Equal(
                "Feature: F\n  Scenario: S\n    Given one\n      |a |\n      |10|\n      And two\n",
                Format("Feature: F\nScenario: S\nGiven one\n|a|\n|10|\nAnd two\n", options));
        }),
        ("a comment is indented as what follows it and a description is left as written", () =>
        {
            Assert.Equal(
                "Feature: F\n  as written\n\n# about S\nScenario: S\n    # about the step\n    Given one\n",
                Format("Feature: F\n  as written\n\n  # about S\nScenario: S\n# about the step\nGiven one\n"));
        }),
        ("a doc string's trailing whitespace is its value and stays", () =>
        {
            Assert.Equal("Feature: F\nScenario: S\n    Given text\n        \"\"\"\n        kept  \n        \"\"\"\n",
                Format("Feature: F\nScenario: S\nGiven text\n\"\"\"\nkept  \n\"\"\"\n"));
        }),
        ("a file of crlf lines keeps its line breaks", () =>
        {
            Assert.Equal("Feature: F\r\nScenario: S\r\n    Given one\r\n        | a |\r\n",
                Format("Feature: F\r\nScenario: S\r\nGiven one\r\n|a|\r\n"));
        }),
        ("what a row holds after its last bar stays after its aligned cell's", () =>
        {
            Assert.Equal(
                "Feature: F\nScenario: S\n    Given table\n" +
                "        | name\n" +
                "        | foo | bar\n" +
                "        | foo | bar baz\n" +
                "        |\n" +
                "        | foo |\n" +
                "        | foo | bar \\| baz\n" +
                "        |   5 |   6 | 11\n" +
                "        | foo | bar |\n" +
                "\nScenario Outline: O\n    Given <a>\n\nExamples:\n    | a | sum\n    | 1 | 1 |\n",
                Format(
                    "Feature: F\nScenario: S\nGiven table\n" +
                    "    | name\n" +
                    "    | foo |    bar  \n" +
                    "    | foo  |  bar baz  \n" +
                    "    |   \n" +
                    "    | foo   |    \n" +
                    "    | foo   | bar \\| baz\n" +
                    "    | 5 | 6 | 11\n" +
                    "    | foo   | bar |\n" +
                    "\nScenario Outline: O\nGiven <a>\n\nExamples:\n|a|sum\n|1|1|\n"));
        }));

}
