using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A type's short name, which a step definition, a test, a binding error and XAML's types are shown by, is its own
/// name whichever spelling of its full name it comes from.</summary>
public sealed class TypeNamesTests
{
    [Fact]
    public void A_short_name_is_the_types_own_name_in_either_spelling() => Cases.Each<(string fullName, string shown)>(
    [
        ("Shop.Specs.BasketSteps", "BasketSteps"),
        ("Shop.Specs.BasketSteps+Discounts", "Discounts"),
        ("Shop.Specs.BasketSteps.Discounts", "Discounts"),
        ("System.Collections.Generic.List`1", "List`1"),
        ("BasketSteps", "BasketSteps"),
    ], row =>
    {
        (string fullName, string shown) = row;
        
                Assert.True(TypeNames.Short(fullName) == shown, $"{fullName} is shown as {TypeNames.Short(fullName)}, not {shown}: a step, test or binding error names another type");
    });
}
