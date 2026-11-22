using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Names as a sentence offers a choice of them.</summary>
public sealed class PluralTests
{
    [Fact]
    public void Names_offered_as_a_choice_join_the_last_with_or() => Cases.Each<(string[] names, string said)>(
    [
        (new string[0], ""),
        (new[] { "Shop" }, "Shop"),
        (new[] { "Admin", "Shop" }, "Admin or Shop"),
        (new[] { "Admin", "Shop", "Worker" }, "Admin, Shop or Worker"),
    ], row =>
    {
        (string[] names, string said) = row;
        Assert.Equal(said, Plural.Either(names));
    });
}
