using Passenger.App.Templates;

namespace Passenger.UnitTests.Templates;

/// <summary>A template's option enabled by its condition over the options' values, and one that does not read left enabled.</summary>
public class TemplateConditionsTests
{
    private static readonly Dictionary<string, string> Values = new() { ["UseSdk"] = "true", ["Runner"] = "MSTest" };

    [Fact]
    public void A_templates_condition_reads_over_the_options_and_one_nested_too_deep_ends_nothing() => Cases.All(
        ("a condition reads over the options values", () => Cases.Each<(string, bool)>(
        [
            ("UseSdk && (Runner == MSTest)", true),
            ("!(UseSdk) || Runner != MSTest", false),
            ("(Runner == ", true),
        ], row => A_condition_reads_over_the_options_values(row.Item1, row.Item2))),
        ("a condition nested past what is read does not read and ends nothing", () => Cases.Each<(string, string, string)>(
        [
            ("!", "UseSdk", ""),
            ("(", "UseSdk", ")"),
        ], row => A_condition_nested_past_what_is_read_does_not_read_and_ends_nothing(row.Item1, row.Item2, row.Item3))));

    private void A_condition_reads_over_the_options_values(string condition, bool enabled) =>
        Assert.Equal(enabled, TemplateConditions.IsEnabled(condition, Values));

    private void A_condition_nested_past_what_is_read_does_not_read_and_ends_nothing(string open, string inner, string close)
    {
        string condition = string.Concat(Enumerable.Repeat(open, 100_000)) + inner + string.Concat(Enumerable.Repeat(close, 100_000));
        SmallStack.Run(() => Assert.True(TemplateConditions.IsEnabled(condition, new Dictionary<string, string> { ["UseSdk"] = "false" })));
    }
}
