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
    public void Headers_stay_at_the_left_steps_are_indented_and_tables_aligned_under_them()
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
    }

    [Fact]
    public void An_outlines_examples_stay_at_its_level_and_their_table_is_indented()
    {
        Assert.Equal(
            "Feature: F\n\nScenario Outline: O\n    Given <a>\n\n@slow\nExamples:\n    | a |\n    | x |\n",
            Format("Feature: F\n\nScenario Outline: O\nGiven <a>\n\n  @slow\n  Examples:\n|a|\n|x|\n"));
    }

    [Fact]
    public void A_doc_strings_lines_keep_their_indentation_within_it()
    {
        Assert.Equal(
            "Feature: F\nScenario: S\n    Given text\n        \"\"\"json\n        {\n          \"a\": 1\n        }\n        \"\"\"\n",
            Format("Feature: F\nScenario: S\nGiven text\n  \"\"\"json\n  {\n    \"a\": 1\n  }\n  \"\"\"\n"));
    }

    [Fact]
    public void The_keys_indent_a_features_children_and_its_ands_and_set_a_cells_padding()
    {
        GherkinFormatOptions options = GherkinFormatOptions.Default with { IndentFeatureChildren = true, IndentAndSteps = true, CellPadding = 0, RightAlignNumbers = false, Indent = "  " };

        Assert.Equal(
            "Feature: F\n  Scenario: S\n    Given one\n      |a |\n      |10|\n      And two\n",
            Format("Feature: F\nScenario: S\nGiven one\n|a|\n|10|\nAnd two\n", options));
    }

    [Fact]
    public void A_comment_is_indented_as_what_follows_it_and_a_description_is_left_as_written()
    {
        Assert.Equal(
            "Feature: F\n  as written\n\n# about S\nScenario: S\n    # about the step\n    Given one\n",
            Format("Feature: F\n  as written\n\n  # about S\nScenario: S\n# about the step\nGiven one\n"));
    }

    [Fact]
    public void The_editorconfig_keys_are_read_with_their_defaults()
    {
        Dictionary<string, string> said = new() { ["gherkin_indent_steps"] = "false", ["gherkin_table_cell_padding_size"] = "2", ["indent_style"] = "tab" };

        GherkinFormatOptions options = GherkinFormatOptions.Of(key => said.GetValueOrDefault(key), "    ");

        Assert.Equal((false, 2, true, "\t"), (options.IndentSteps, options.CellPadding, options.IndentDataTable, options.Indent));
    }

    [Fact]
    public void A_doc_strings_trailing_whitespace_is_its_value_and_stays()
    {
        Assert.Equal("Feature: F\nScenario: S\n    Given text\n        \"\"\"\n        kept  \n        \"\"\"\n",
            Format("Feature: F\nScenario: S\nGiven text\n\"\"\"\nkept  \n\"\"\"\n"));
    }
}
