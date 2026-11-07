using Passenger.App.Templates;

namespace Passenger.UnitTests.Templates;

/// <summary>A template's option enabled by its condition over the options' values, and one that does not read left enabled.</summary>
public class TemplateConditionsTests
{
    private static readonly Dictionary<string, string> Values = new() { ["UseSdk"] = "true", ["Runner"] = "MSTest" };

    [Theory]
    [InlineData("UseSdk && (Runner == MSTest)", true)]
    [InlineData("UseSdk && (Runner == NUnit)", false)]
    [InlineData("!(UseSdk) || Runner != MSTest", false)]
    [InlineData("!!UseSdk", true)]
    [InlineData("(Runner == ", true)]
    public void A_condition_reads_over_the_options_values(string condition, bool enabled) =>
        Assert.Equal(enabled, TemplateConditions.IsEnabled(condition, Values));

    [Theory]
    [InlineData("!", "UseSdk", "")]
    [InlineData("(", "UseSdk", ")")]
    public void A_condition_nested_past_what_is_read_does_not_read_and_ends_nothing(string open, string inner, string close)
    {
        string condition = string.Concat(Enumerable.Repeat(open, 100_000)) + inner + string.Concat(Enumerable.Repeat(close, 100_000));
        SmallStack.Run(() => Assert.True(TemplateConditions.IsEnabled(condition, new Dictionary<string, string> { ["UseSdk"] = "false" })));
    }
}
