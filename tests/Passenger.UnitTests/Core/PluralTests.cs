using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Names as a sentence offers a choice of them.</summary>
public sealed class PluralTests
{
    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "Shop" }, "Shop")]
    [InlineData(new[] { "Admin", "Shop" }, "Admin or Shop")]
    [InlineData(new[] { "Admin", "Shop", "Worker" }, "Admin, Shop or Worker")]
    public void Names_offered_as_a_choice_join_the_last_with_or(string[] names, string said) => Assert.Equal(said, Plural.Either(names));
}
