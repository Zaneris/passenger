using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A [Flags] enum's value named by its members, as Variables shows it: each member and the value read as their
/// underlying type's bits, so a negative member takes part as its bits.</summary>
public class FlagsTests
{
    private static readonly (string Name, object? Literal)[] Access = [("None", 0), ("Read", 1), ("Write", 2), ("Run", 4), ("All", -1)];

    [Fact]
    public void A_flags_value_is_named_by_the_members_its_bits_hold_and_one_they_leave_bits_of_is_shown_as_it_is() => Cases.All(
        ("a combination of a flags enum with a negative member is named by its members", () => Assert.Equal("Read | Write | Run", ManagedDebugger.FlagsOf(Access, "7"))),
        ("a value its members leave bits of is shown as it is", () => Assert.Equal("-2", ManagedDebugger.FlagsOf(Access, "-2"))),
        ("a negative value is named by the members its bits hold", () =>
        {
            (string Name, object? Literal)[] wide = [("Low", 1L), ("Top", long.MinValue)];
            Assert.Equal("Low | Top", ManagedDebugger.FlagsOf(wide, (long.MinValue + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)));
        }));
}
