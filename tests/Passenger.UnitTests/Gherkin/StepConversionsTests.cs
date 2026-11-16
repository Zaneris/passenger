using System.Globalization;
using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A step's argument converted to its definition's parameter as Reqnroll 3.3.4's converter converts it at run time:
/// each verdict below is what its StepArgumentTypeConverter.CanConvert answered for that text, type and binding culture
/// (a transformation for Money matching <c>\$(\d+)</c>), and what is refused is said in words naming what is taken.</summary>
public sealed class StepConversionsTests
{
    private static readonly StepTransformation Money = new("\\$(\\d+)", null, "Shop.Money") { Parameters = ["System.String"], Method = "Transforms.ToMoney" };

    /// <summary>A parameter of a type as the test names it: a .NET type by its short name, an enum of the tests', a nullable
    /// one with a <c>?</c>.</summary>
    private static StepParameter ParameterOf(string type)
    {
        StepParameter parameter = type.TrimEnd('?') switch
        {
            "Color" => new StepParameter("color", "Shop.Color", ["Red", "Green", "DarkBlue"]) { Underlying = "System.Int32" },
            "Access" => new StepParameter("access", "Shop.Access", ["Read", "Write"]) { Underlying = "System.Int32" },
            "Small" => new StepParameter("small", "Shop.Small", ["A", "B"]) { Underlying = "System.Byte" },
            "Money" => new StepParameter("price", "Shop.Money") { Unconverted = true },
            { } name => new StepParameter("value", "System." + name),
        };
        return parameter with { Nullable = type.EndsWith('?') };
    }

    [Theory]
    [InlineData("en-US", "Int32", "50", true)]
    [InlineData("en-US", "Int32", "two", false)]
    [InlineData("en-US", "Int32", "1,000", false)]
    [InlineData("en-US", "Int32", "0x1F", true)]
    [InlineData("en-US", "Int32", "#FF", true)]
    [InlineData("en-US", "Int32", "99999999999", false)]
    [InlineData("en-US", "Int32", " 5 ", true)]
    [InlineData("en-US", "Int32", "", false)]
    [InlineData("en-US", "Int64", "99999999999", true)]
    [InlineData("en-US", "Byte", "300", false)]
    [InlineData("en-US", "Byte", "-7", false)]
    [InlineData("en-US", "UInt32", "-1", false)]
    [InlineData("en-US", "Double", "1,000", true)]
    [InlineData("en-US", "Double", "1,000.5", true)]
    [InlineData("en-US", "Double", "7-", false)]
    [InlineData("de-DE", "Double", "1,5", true)]
    [InlineData("de-DE", "Double", "1.000,5", true)]
    [InlineData("en-US", "Double?", "1,000", false)]
    [InlineData("en-US", "Double?", "", true)]
    [InlineData("en-US", "Decimal", "7-", true)]
    [InlineData("en-US", "Decimal", "1e3", true)]
    [InlineData("en-US", "Single", "two", false)]
    [InlineData("en-US", "Boolean", "True", true)]
    [InlineData("en-US", "Boolean", "yes", false)]
    [InlineData("en-US", "Boolean", "1", false)]
    [InlineData("en-US", "DateTime", "01/15/2024", true)]
    [InlineData("en-US", "DateTime", "15/01/2024", false)]
    [InlineData("de-DE", "DateTime", "15.01.2024", true)]
    [InlineData("fr-FR", "DateTime", "15/01/2024", true)]
    [InlineData("en-US", "DateTime", "", true)]
    [InlineData("en-US", "DateTime", "Jan 32 2024", false)]
    [InlineData("en-US", "DateTime", "not a date", false)]
    [InlineData("en-US", "DateTime?", "", true)]
    [InlineData("en-US", "DateTimeOffset", "2024-01-15", true)]
    [InlineData("en-US", "TimeSpan", "+7", false)]
    [InlineData("en-US", "TimeSpan", "1.02:03:04", true)]
    [InlineData("en-US", "TimeSpan", "two", false)]
    [InlineData("en-US", "Char", "ab", false)]
    [InlineData("en-US", "Char", "", true)]
    [InlineData("en-US", "Char", " 5 ", true)]
    [InlineData("en-US", "Guid", "not-a-guid", true)]
    [InlineData("en-US", "Guid?", "", true)]
    [InlineData("en-US", "Color", "Dark Blue", true)]
    [InlineData("en-US", "Color", "dark blue", true)]
    [InlineData("en-US", "Color", "Purple", false)]
    [InlineData("en-US", "Color", "5", true)]
    [InlineData("en-US", "Color", "Red,", false)]
    [InlineData("en-US", "Color", "Red, Green", true)]
    [InlineData("en-US", "Color", "", false)]
    [InlineData("en-US", "Color?", "Dark Blue", false)]
    [InlineData("en-US", "Color?", "", true)]
    [InlineData("en-US", "Color?", "1,000", true)]
    [InlineData("en-US", "Small", "256", false)]
    [InlineData("en-US", "Small", "a", true)]
    [InlineData("en-US", "Small", "-1", false)]
    [InlineData("en-US", "Access", "Read, Write", true)]
    [InlineData("en-US", "Money", "$12", true)]
    [InlineData("en-US", "Money", "12", false)]
    [InlineData("en-US", "String", "two", true)]
    [InlineData("en-US", "Uri", "two", true)]
    public void An_argument_is_taken_as_reqnrolls_converter_takes_it_at_run_time(string culture, string type, string text, bool takes)
    {
        string? refusal = StepConversions.Refusal(text, ParameterOf(type), [Money], CultureInfo.GetCultureInfo(culture));

        Assert.True(takes == refusal is null, takes ? $"\"{text}\" was refused as {type} in {culture}, which Reqnroll takes: {refusal}" : $"\"{text}\" was taken as {type} in {culture}, which Reqnroll refuses");
    }

    [Theory]
    [InlineData("en-US", "Int32", "a whole number (from -2147483648 to 2147483647)")]
    [InlineData("en-US", "Double?", "a number (as en-US writes one, like 1234.5), or nothing")]
    [InlineData("de-DE", "DateTime", "a date (as de-DE writes one, like 31.01.2024)")]
    [InlineData("en-US", "DateTime", "a date (as en-US writes one, like 1/31/2024)")]
    [InlineData("en-US", "Boolean", "true or false")]
    [InlineData("en-US", "Color", "one of Color's names (Red, Green or DarkBlue, in any letter case, spaces left out) or a number")]
    [InlineData("en-US", "Money", "text matching ^\\$(\\d+)$ (made by Transforms.ToMoney)")]
    public void A_refusal_says_what_the_parameter_takes(string culture, string type, string words)
    {
        Assert.Equal(words, StepConversions.Refusal("~", ParameterOf(type), [Money], CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void A_type_of_the_solutions_own_that_no_transformation_makes_takes_no_text_and_one_nothing_is_known_of_takes_any()
    {
        StepParameter basket = new("basket", "Shop.Basket") { Unconverted = true };
        StepParameter unknown = new("anything", "Shop.Anything");

        Assert.Equal("a Basket, which only a [StepArgumentTransformation] makes from text, and none makes one",
            StepConversions.Refusal("full", basket, [Money], CultureInfo.InvariantCulture));
        Assert.Null(StepConversions.Refusal("full", unknown, [Money], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_transformation_with_no_expression_takes_any_text_but_one_taking_a_table_takes_none()
    {
        StepParameter price = ParameterOf("Money");
        StepTransformation anyText = new(null, null, "Shop.Money") { Parameters = ["System.String"] };
        StepTransformation fromTable = new(null, null, "Shop.Money") { Parameters = ["Reqnroll.DataTable"] };

        Assert.Null(StepConversions.Refusal("twelve", price, [anyText], CultureInfo.InvariantCulture));
        Assert.NotNull(StepConversions.Refusal("twelve", price, [fromTable], CultureInfo.InvariantCulture));
    }
}
